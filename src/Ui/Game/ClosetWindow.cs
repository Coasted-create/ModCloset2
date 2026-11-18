using System.Numerics;
using System.Runtime.InteropServices;
using Dalamud.Bindings.ImGui;
using Dalamud.Game.ClientState.Keys;
using Dalamud.Interface;
using Dalamud.Interface.Utility;
using Dalamud.Interface.Windowing;
using Dalamud.Plugin.Services;
using FFXIVClientStructs.FFXIV.Client.UI;
using ModCloset2.Core;
using ModCloset2.Ui.Canvas;
using ModCloset2.Ui.Face;

namespace ModCloset2.Ui.Game;

/// <summary>
/// The closet window in the game: a normal Dalamud window (title bar, close button, resizable) whose whole content is
/// the <see cref="WindowFace"/>, drawn through an <see cref="ImGuiCanvas"/>. One invisible button covers the content so
/// a drag inside it moves cards, never the window. Closing the window while trying something on puts it back.
/// </summary>
public sealed partial class ClosetWindow : Window, IDisposable
{
    private const float Rounding = 14;

    private readonly IClosetSession _session;
    private readonly IPluginLog _log;
    private readonly ImGuiCanvas _canvas;
    private readonly WindowFace _face = new();
    private readonly HashSet<string> _logged = new(StringComparer.Ordinal);
    private bool _placed;
    private bool _escLatch;
    private bool _gameTypingBroken;
    private readonly ChatTyping _chat = new();
    private readonly WindowKeySources _keySources = new();
    private bool _keysBroken;
    private readonly bool[] _charWasDown = new bool[CharacterVks.Length];
    private readonly InFrontState _front = new();
    private bool _frontBroken;
    private bool _charsInFront = true;
    private volatile bool _gameCharPressed;

    /// <summary> The keys that type a character (space, digits, letters, the number pad, punctuation), as On you has them. </summary>
    private static readonly int[] CharacterVks = new[] { 0x20 }.Concat(Enumerable.Range(0x30, 10)).Concat(Enumerable.Range(0x41, 26))
        .Concat(Enumerable.Range(0x60, 16)).Concat(Enumerable.Range(0xBA, 7)).Concat(Enumerable.Range(0xDB, 5)).Append(0xE2).ToArray();
    private bool _chatLogged;
    private bool _toOnYou;
    private readonly KeyGate _keys = new();
    private readonly ReleaseTimer _letGo = new(LetGoOfPicturesAfterMs);

    /// <summary> Pictures are let go this long after the window was closed. </summary>
    private const long LetGoOfPicturesAfterMs = 30_000;
    private int _styleVars;
    private int _styleColors;

    public ClosetWindow(IClosetSession session, IUiBuilder ui, ITextureProvider textures, IPluginLog log)
        : base(Loc.T("Mod Closet 2") + "###ModCloset2Closet", ImGuiWindowFlags.NoScrollbar | ImGuiWindowFlags.NoScrollWithMouse)
    {
        _session = session;
        _log     = log;
        _canvas  = new ImGuiCanvas(ui, textures);

        // sizes here are before Dalamud's global scale; the face's U is a little smaller than that scale (see ImGuiCanvas.EmRatio)
        Size            = WindowFace.WideDefault * 0.9f;
        SizeCondition   = ImGuiCond.FirstUseEver;
        SizeConstraints = new WindowSizeConstraints { MinimumSize = new Vector2(330, 420), MaximumSize = new Vector2(8000, 6000) };
    }

    public WindowFace Face
        => _face;

    /// <summary>
    /// Set by the plugin to start On you. While it is null the face switch's third segment stays "coming soon". A click
    /// on it closes this window without putting back what is tried (it goes on with On you) and calls this.
    /// </summary>
    public Action? OnYouRequested { get; set; }

    /// <summary>
    /// Opens the window (if it is closed, in the shape it had last) with its settings sheet showing, as a click on the
    /// settings button would: for Dalamud's settings button (UiBuilder.OpenConfigUi). The sheet is open on the next drawn
    /// frame; called while it is open already, nothing changes.
    /// </summary>
    public void OpenSettings()
    {
        IsOpen = true;
        _uncollapse = true;
        BringToFront();
        _face.RequestSettings();
    }

    private bool _uncollapse;

    public override void PreDraw()
    {
        // OpenSettings on a collapsed window: unfold it once (afterwards the user may fold it again)
        if (_uncollapse)
        {
            Collapsed          = false;
            CollapsedCondition = ImGuiCond.Always;
            _uncollapse        = false;
        }
        else if (Collapsed is not null)
            Collapsed = null;

        // Esc puts back what is tried on; only when nothing is tried does it close the window, as Dalamud windows do.
        // Dalamud closes on Esc held in any later frame, so after a put back the window stays until Esc is let go:
        // one press never does both.
        // the Esc that opened the window (On you's way back to the closet) stays out of it until let go, as well
        var escDown = ImGui.IsKeyDown(ImGuiKey.Escape) || _keySources.EscapeDown;
        if (_session.Trying.Active && escDown || _keySources.HeldFromBefore(Key.Escape) || escDown && !_keySources.Drawing(Environment.TickCount64 / 1000.0))
            _escLatch = true;
        else if (!escDown)
            _escLatch = false;
        RespectCloseHotkey = !_session.Trying.Active && !_escLatch;

        Var(ImGuiStyleVar.WindowPadding, Vector2.Zero);
        Var(ImGuiStyleVar.WindowRounding, Rounding * ImGuiHelpers.GlobalScale);
        Var(ImGuiStyleVar.WindowBorderSize, 1f);
        Var(ImGuiStyleVar.ScrollbarSize, 0f);
        Col(ImGuiCol.WindowBg, Theme.Window);
        Col(ImGuiCol.Border, Theme.Line);
        Col(ImGuiCol.TitleBg, Colr.Hex(0x120D16));
        Col(ImGuiCol.TitleBgActive, Colr.Hex(0x1B1220));
        Col(ImGuiCol.TitleBgCollapsed, Colr.Hex(0x120D16, 0.8f));
        Col(ImGuiCol.Text, Theme.Text);
        Col(ImGuiCol.ResizeGrip, Colr.Transparent);
        Col(ImGuiCol.ResizeGripHovered, Theme.Accent.Fade(0.5f));
        Col(ImGuiCol.ResizeGripActive, Theme.Accent);
    }

    public override void PostDraw()
    {
        if (_styleColors > 0)
            ImGui.PopStyleColor(_styleColors);
        if (_styleVars > 0)
            ImGui.PopStyleVar(_styleVars);
        _styleColors = 0;
        _styleVars   = 0;
    }

    private void Var(ImGuiStyleVar v, float value)
    {
        ImGui.PushStyleVar(v, value);
        _styleVars++;
    }

    private void Var(ImGuiStyleVar v, Vector2 value)
    {
        ImGui.PushStyleVar(v, value);
        _styleVars++;
    }

    private void Col(ImGuiCol c, Colr value)
    {
        ImGui.PushStyleColor(c, value.Abgr);
        _styleColors++;
    }

    public override void Draw()
    {
        try
        {
            DrawFace();
        }
        catch (Exception e)
        {
            // Draw never throws: say it once in the log, keep the window up
            if (_logged.Add(e.GetType().Name + e.Message))
                _log.Error(e, "Mod Closet 2: the window could not be drawn");
            ImGui.TextUnformatted(Loc.T("Something went wrong while drawing the closet. The log has the details."));
        }
    }

    private void DrawFace()
    {
        // the first time: the face and place the user had last
        if (!_placed)
        {
            _placed = true;
            var face = _session.Remembered(WindowFace.KeyFace);
            if (WindowFace.ParsePlace(_session.Remembered(face == "slim" ? WindowFace.KeySlim : WindowFace.KeyWide)) is { } place)
            {
                // a place remembered on a monitor or at a resolution that is gone comes back onto the screen
                var viewport  = ImGui.GetMainViewport();
                var (pos, sz) = WindowGeometry.KeepOnScreen(place.Pos, place.Size, viewport.Pos, viewport.Size);
                ImGui.SetWindowPos(pos);
                ImGui.SetWindowSize(sz);
            }
        }

        var min   = ImGui.GetCursorScreenPos();
        var avail = ImGui.GetContentRegionAvail();
        if (avail.X < 1 || avail.Y < 1)
            return;

        // one item over the whole content: dragging inside never moves the window; text fields drawn later still win
        ImGui.InvisibleButton("##closet", avail, ImGuiButtonFlags.MouseButtonLeft | ImGuiButtonFlags.MouseButtonRight | ImGuiButtonFlags.MouseButtonMiddle);
        ImGui.SetItemAllowOverlap();
        var held    = ImGui.IsItemActive();
        var content = new Rect(min.X, min.Y, avail.X, avail.Y);
        var io      = ImGui.GetIO();
        var mouse   = ImGui.GetMousePos();
        var window  = new Rect(ImGui.GetWindowPos().X, ImGui.GetWindowPos().Y, ImGui.GetWindowSize().X, ImGui.GetWindowSize().Y);

        // a press on the resize grip or an edge resizes the window; it must not also reach the cards under it, and
        // while ImGui is busy with something else of the window (resizing, moving), the closet keeps its hands off
        var grip    = WindowGeometry.GripSize(ImGui.GetFontSize(), ImGui.GetStyle().WindowRounding);
        var handle  = WindowGeometry.OnResizeHandle(mouse, window, grip);
        var other   = ImGui.IsAnyItemActive() && !held && !io.WantTextInput;
        var hovered = ImGui.IsWindowHovered(ImGuiHoveredFlags.AllowWhenBlockedByActiveItem) && content.Contains(mouse) && !handle && !other;
        ImGui.SetCursorScreenPos(min);

        // keys: only while the mouse is over the window, or while one of its text fields has the keyboard, and never
        // while the game's own chat line (or another game text field) is typed in
        var typing     = io.WantTextInput && ImGui.IsWindowFocused(ImGuiFocusedFlags.RootAndChildWindows);
        var mouseOver  = ImGui.IsWindowHovered(ImGuiHoveredFlags.AllowWhenBlockedByActiveItem) && window.Contains(mouse);
        // the game's field counts only once something was typed into it (direct chat keeps it active all the time)
        // (the keys from both sources: ImGui sees no keys while the window does not have the keyboard focus)
        var charPressed = _gameCharPressed;
        _gameCharPressed = false;
        var gameTyping = _chat.Update(GameFieldActive(), CharacterKeyPressed() || charPressed,
            ImGui.IsKeyDown(ImGuiKey.Enter) || ImGui.IsKeyDown(ImGuiKey.KeypadEnter) || ImGui.IsKeyDown(ImGuiKey.Escape) || _keySources.EnterOrEscapeDown);
        if (gameTyping && mouseOver && !typing && !_chatLogged && (WindowKeyPressed() || _keySources.Pending))
        {
            // once: tells from the user's log whether keys the window did not act on went to the game's chat
            _chatLogged = true;
            _log.Information("Mod Closet 2: the game's chat is being typed in; Enter, Esc and the arrows go to it, not to the closet");
        }

        _canvas.Begin(content, hovered || held, KeyGate.WindowReadsKeys(mouseOver, gameTyping, typing));

        // the keys: from the game's key state (ImGui gets no presses while the window does not have the keyboard focus,
        // which it has only after a click in it), and ImGui's report as the second source; each press once. Read from
        // the game only with the mouse over the window and nothing typed (own fields take their keys from ImGui)
        var gameAllowed = mouseOver && !gameTyping && !typing;
        var merged      = _keySources.Take(Environment.TickCount64 / 1000.0, _canvas.Input.Keys.ToList(), gameAllowed);
        _canvas.Input.Keys.Clear();
        _canvas.Input.Keys.UnionWith(merged);
        if (gameAllowed && _keySources.Alive(Environment.TickCount64 / 1000.0))
            _canvas.Input.Ctrl |= _keySources.Ctrl;
        foreach (var line in _keySources.Lines)
            _log.Information(line);
        _keySources.Lines.Clear();
        _face.OnYouAvailable = OnYouRequested is not null;
        _face.BottomRounding = Math.Max(0, ImGui.GetStyle().WindowRounding - 1);
        _face.Draw(_canvas, _session, new WindowPlace(ImGui.GetWindowPos(), ImGui.GetWindowSize(), !ImGui.IsMouseDown(ImGuiMouseButton.Left)));
        _canvas.End();

        if (_face.Request is { } request)
        {
            // the face switch: the other face's own size and place
            var screen = ImGui.GetMainViewport().Size;
            var size   = Vector2.Min(request.Size, screen);
            var pos    = Vector2.Clamp(request.Pos, Vector2.Zero, Vector2.Max(Vector2.Zero, screen - size));
            ImGui.SetWindowPos(pos);
            ImGui.SetWindowSize(size);
            _face.RequestDone();
        }

        _keys.Drawn(mouseOver, typing, _face.Wants);

        if (_face.WantsOnYou)
        {
            _face.OnYouDone();
            if (OnYouRequested is { } startOnYou)
            {
                _toOnYou = true;
                IsOpen   = false;
                startOnYou();
            }
        }
    }

    private static readonly ImGuiKey[] CharacterKeys = BuildCharacterKeys();

    private static ImGuiKey[] BuildCharacterKeys()
    {
        var list = new List<ImGuiKey>();
        for (var k = ImGuiKey.A; k <= ImGuiKey.Z; ++k)
            list.Add(k);
        for (var k = ImGuiKey.Key0; k <= ImGuiKey.Key9; ++k)
            list.Add(k);
        for (var k = ImGuiKey.Keypad0; k <= ImGuiKey.Keypad9; ++k)
            list.Add(k);
        list.AddRange(new[]
        {
            ImGuiKey.Space, ImGuiKey.Apostrophe, ImGuiKey.Comma, ImGuiKey.Minus, ImGuiKey.Period, ImGuiKey.Slash, ImGuiKey.Semicolon,
            ImGuiKey.Equal, ImGuiKey.LeftBracket, ImGuiKey.Backslash, ImGuiKey.RightBracket, ImGuiKey.GraveAccent,
            ImGuiKey.KeypadDecimal, ImGuiKey.KeypadDivide, ImGuiKey.KeypadMultiply, ImGuiKey.KeypadSubtract, ImGuiKey.KeypadAdd,
        });
        return list.ToArray();
    }

    /// <summary> A key that types a character went down this frame (with Ctrl or Alt held it is a shortcut, not typing). </summary>
    private static bool CharacterKeyPressed()
    {
        if (ImGui.IsKeyDown(ImGuiKey.LeftCtrl) || ImGui.IsKeyDown(ImGuiKey.RightCtrl) || ImGui.IsKeyDown(ImGuiKey.LeftAlt) || ImGui.IsKeyDown(ImGuiKey.RightAlt))
            return false;
        foreach (var key in CharacterKeys)
            if (ImGui.IsKeyPressed(key, false))
                return true;
        return false;
    }

    /// <summary> Esc, Enter or an arrow went down this frame. </summary>
    private static bool WindowKeyPressed()
        => ImGui.IsKeyPressed(ImGuiKey.Escape, false) || ImGui.IsKeyPressed(ImGuiKey.Enter, false) || ImGui.IsKeyPressed(ImGuiKey.KeypadEnter, false)
         || ImGui.IsKeyPressed(ImGuiKey.LeftArrow, false) || ImGui.IsKeyPressed(ImGuiKey.RightArrow, false)
         || ImGui.IsKeyPressed(ImGuiKey.UpArrow, false) || ImGui.IsKeyPressed(ImGuiKey.DownArrow, false);

    /// <summary>
    /// Is a text field of the game itself (the chat line, a search box) active? Read from the game's UI module; anything
    /// that fails means "no", once logged. Active is not yet typing: see <see cref="ChatTyping"/>.
    /// </summary>
    private unsafe bool GameFieldActive()
    {
        if (_gameTypingBroken)
            return false;
        try
        {
            var ui = UIModule.Instance();
            if (ui == null)
                return false;
            var atk = ui->GetRaptureAtkModule();
            return atk != null && atk->IsTextInputActive();
        }
        catch (Exception e)
        {
            _gameTypingBroken = true;
            _log.Warning(e, "Mod Closet 2: cannot tell whether the game's chat is typed in; assuming it is not");
            return false;
        }
    }

    /// <summary>
    /// Call every frame from IFramework.Update (before the game reads its keys). While the window was drawn in the last
    /// frame with the mouse over it, and no text field (the window's or the game's) has the keyboard, the keys the closet will act on (<see cref="WindowFace.Wants"/>: Enter and
    /// Esc while trying, Esc for a popup or a drag, the arrows while there are cards, Z with Ctrl while there is
    /// something to undo) are hidden from the game. Every other key always goes to the game, and with the mouse
    /// elsewhere the game gets everything. ImGui still sees the keys (it reads them from the window messages).
    /// </summary>
    public void FilterGameKeys(IKeyState keys)
    {
        // this runs every frame, also while the window is closed: the place to let go of pictures after closing
        if (_letGo.Tick(IsOpen, Environment.TickCount64) && _canvas.Pictures.Count > 0)
            _canvas.Pictures.Clear();

        // the keys the window acts on, as the game has them, before anything is hidden (the next draw takes the presses)
        SampleKeys(keys);

        // only what the window saw in a frame it was drawn (not collapsed, closed, hidden or in a cutscene), and
        // nothing while something is typed into the game's own text field (active alone does not count)
        // nothing is hidden from the game while it is not the window in front
        var wants = IsOpen && _front.InFront ? _keys.Hide(_chat.Typing && GameFieldActive()) : GameKeys.None;
        if (wants == GameKeys.None)
            return;
        if ((wants & GameKeys.Enter) != 0)
            keys[VirtualKey.RETURN] = false;
        if ((wants & GameKeys.Escape) != 0)
            keys[VirtualKey.ESCAPE] = false;
        if ((wants & GameKeys.Arrows) != 0)
        {
            keys[VirtualKey.LEFT]  = false;
            keys[VirtualKey.RIGHT] = false;
            keys[VirtualKey.UP]    = false;
            keys[VirtualKey.DOWN]  = false;
        }

        if ((wants & GameKeys.UndoZ) != 0 && keys[VirtualKey.CONTROL])
            keys[VirtualKey.Z] = false;
    }

    [LibraryImport("user32.dll")]
    private static partial short GetAsyncKeyState(int virtualKey);

    [LibraryImport("user32.dll")]
    private static partial nint GetForegroundWindow();

    [LibraryImport("user32.dll")]
    private static partial uint GetWindowThreadProcessId(nint window, out uint processId);

    /// <summary>
    /// Is the game the window in front (the foreground window belongs to this process; Dalamud's windows dragged out of
    /// the game count as the game)? null when it cannot be told now. A failure is logged once and then fails open.
    /// </summary>
    private bool? ForegroundIsGame()
    {
        if (_frontBroken)
            return null;
        try
        {
            var window = GetForegroundWindow();
            if (window == 0)
                return null;
            GetWindowThreadProcessId(window, out var pid);
            return pid == 0 ? null : pid == (uint)Environment.ProcessId;
        }
        catch (Exception e)
        {
            _frontBroken = true;
            _log.Warning(e, "Mod Closet 2: the window cannot tell whether the game is in front; assuming it is");
            return null;
        }
    }

    /// <summary>
    /// Samples the watched keys. While the game is not in front every key reads as up. In front a key is down when the
    /// physical keyboard has it down (<c>GetAsyncKeyState</c>: hiding a key from the game does not change it, and unlike
    /// the thread's key state it cannot stay "down" for a key let go while another window was in front) or the game's
    /// key buffer does (read before the hiding). As On you's.
    /// </summary>
    private void SampleKeys(IKeyState keys)
    {
        var inFront = _front.Update(ForegroundIsGame());
        if (_keysBroken)
            return;
        try
        {
            bool Down(VirtualKey vk)
                => inFront && ((GetAsyncKeyState((int)vk) & 0x8000) != 0 || keys[vk]);

            _keySources.Sample(Environment.TickCount64 / 1000.0, key => key switch
            {
                Key.Enter  => Down(VirtualKey.RETURN),
                Key.Escape => Down(VirtualKey.ESCAPE),
                Key.Left   => Down(VirtualKey.LEFT),
                Key.Right  => Down(VirtualKey.RIGHT),
                Key.Up     => Down(VirtualKey.UP),
                Key.Down   => Down(VirtualKey.DOWN),
                Key.Z      => Down(VirtualKey.Z),
                _          => false,
            }, Down(VirtualKey.CONTROL), inFront);

            // a character key going down (for the chat rule), not with Ctrl or Alt held; not one held when the game came
            // back to the front
            var shortcut = _keySources.Ctrl || Down(VirtualKey.MENU);
            var back     = inFront && !_charsInFront;
            _charsInFront = inFront;
            for (var i = 0; i < CharacterVks.Length; ++i)
            {
                var d = inFront && (GetAsyncKeyState(CharacterVks[i]) & 0x8000) != 0;
                if (d && !_charWasDown[i] && !shortcut && !back)
                    _gameCharPressed = true;
                _charWasDown[i] = d;
            }
        }
        catch (Exception e)
        {
            // cannot read the game's keyboard: the window goes on with ImGui's report alone
            _keysBroken = true;
            _log.Warning(e, "Mod Closet 2: the window cannot read the game's keyboard; using ImGui's keys only");
        }
    }

    /// <summary>
    /// Closing the window (or the game) puts everything back. Always, not only when the session shows a try: a Keep that
    /// failed can leave a try on the character that the session no longer shows (review M2); PutBack is a no-op when
    /// nothing is tried. The one exception: closing to go over to On you, which carries what is tried.
    /// </summary>
    /// <summary>
    /// Opening: no press from before counts in the window, and keys held now (the Esc that ended On you, the Enter that
    /// sent the command) stay out of it until they are let go.
    /// </summary>
    public override void OnOpen()
        => _keySources.Forget();

    public override void OnClose()
    {
        // a colour drag still open (the window closed under it) closes its undo step
        _face.WindowClosing();
        _keys.Clear();
        _chat.Clear();
        if (_toOnYou)
        {
            // going over to On you: what is tried stays on, On you carries it
            _toOnYou = false;
            return;
        }

        _session.PutBack();
    }

    public void Dispose()
        => _canvas.Dispose();
}
