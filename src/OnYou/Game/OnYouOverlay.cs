using System.Numerics;
using Dalamud.Bindings.ImGui;
using Dalamud.Game.ClientState.Keys;
using Dalamud.Interface;
using Dalamud.Plugin.Services;
using ModCloset2.Core;
using ModCloset2.Ui.Canvas;
using ModCloset2.Ui.Game;
using CKey = ModCloset2.Ui.Canvas.Key;

namespace ModCloset2.OnYou.Game;

/// <summary>
/// The "On you" face in the game: one full-screen, see-through ImGui window that exists only while pointing or open.
/// Being an ImGui window under the mouse is what keeps the game from the mouse: Dalamud gives the game no clicks and no
/// wheel while ImGui wants the mouse (no camera zoom, no targeting), the way Sculpt+ keeps its clicks on the character
/// with a small window under the mouse. It takes the mouse only over a place of the character or over the chooser
/// (whether the key is held or On you stays on), and is see-through for the mouse elsewhere. It sits behind every other ImGui window (the closet window stays usable on top)
/// and draws through the same game canvas as the window. The host calls <see cref="Draw"/> from UiBuilder.Draw and
/// <see cref="FilterGameKeys"/> from the framework update.
/// </summary>
public sealed class OnYouOverlay : IDisposable
{
    private const ImGuiWindowFlags Flags = ImGuiWindowFlags.NoDecoration | ImGuiWindowFlags.NoBackground | ImGuiWindowFlags.NoSavedSettings
      | ImGuiWindowFlags.NoMove | ImGuiWindowFlags.NoResize | ImGuiWindowFlags.NoNav | ImGuiWindowFlags.NoFocusOnAppearing
      | ImGuiWindowFlags.NoScrollbar | ImGuiWindowFlags.NoScrollWithMouse | ImGuiWindowFlags.NoDocking
        // ImGui puts a window made with this flag behind all others, and keeps it there when it is clicked
      | ImGuiWindowFlags.NoBringToFrontOnFocus;

    private readonly IClosetSession _session;
    private readonly IUiBuilder _ui;
    private readonly ITextureProvider _textures;
    private readonly BodyOnScreen _body;
    private readonly IPluginLog _log;
    private readonly HashSet<string> _logged = new(StringComparer.Ordinal);
    private ImGuiCanvas? _canvas;
    private bool _pressHere;

    /// <summary> What to hide from the game: only what a frame that was drawn asked for (read on the framework thread). </summary>
    private readonly OnYouKeyGate _gate = new();
    private readonly GameTyping _gameTyping;
    private readonly TypingWatch _typingWatch = new();
    private long _filteredAt = -1;

    /// <summary> The user is typing in a game text field (not merely: one is active), as the last Draw saw it. </summary>
    private volatile bool _typing;

    /// <summary> The keyboard as the game sees it (sampled in FilterGameKeys, before anything is hidden): On you's main source of keys. </summary>
    private readonly GameKeyboard _keyboard;

    /// <summary> Presses of Esc, Enter and the arrows from the game's key state and from ImGui, each press once. </summary>
    private readonly KeySources _sources = new();

    /// <summary> The "why was Esc not acted on" log lines. </summary>
    private readonly KeyWitness _witness = new();

    private static double Clock
        => Environment.TickCount64 / 1000.0;

    public OnYouOverlay(IClosetSession session, IUiBuilder ui, ITextureProvider textures, BodyOnScreen body, IPluginLog log)
    {
        _session  = session;
        _ui       = ui;
        _textures = textures;
        _body     = body;
        _log      = log;
        _gameTyping = new GameTyping(log);
        _keyboard   = new GameKeyboard(log);
    }

    public OnYouFace Face { get; } = new();

    /// <summary> Whether the closet window is open (asked every frame; when On you ends while trying and it is closed, On you puts back). </summary>
    public Func<bool>? WindowOpen { get; set; }

    /// <summary> "All 86": open the window on this shelf (the face has already selected it and ended On you). </summary>
    public Action<string>? OpenWindow
    {
        get => Face.OpenWindow;
        set => Face.OpenWindow = value;
    }

    /// <summary> The window's "On you" switch. </summary>
    public void Start()
        => Face.Start(_session);

    /// <summary> Another face was picked (the window opens): On you ends, what is tried stays for the window. </summary>
    public void Stop()
        => Face.Stop(_session, false);

    public void Draw()
    {
        try
        {
            DrawOverlay();
        }
        catch (Exception e)
        {
            // Draw never throws: say it once in the log; the overlay is gone for this frame, the game keeps the mouse
            if (_logged.Add(e.GetType().Name + e.Message))
                _log.Error(e, "Mod Closet 2: On you could not be drawn");
        }
    }

    private void DrawOverlay()
    {
        var io        = ImGui.GetIO();
        var mouseDown = ImGui.IsMouseDown(ImGuiMouseButton.Left) || ImGui.IsMouseDown(ImGuiMouseButton.Right) || ImGui.IsMouseDown(ImGuiMouseButton.Middle);

        // Keys come from the game's own key state first (GameKeyboard, sampled in FilterGameKeys before anything is hidden):
        // ImGui gets no keys while none of its windows has the keyboard focus, and the overlay never takes it. ImGui's
        // report is the second source; KeySources counts a press once whichever source has it first. The modifiers and
        // "another key is down" come from the same place (ImGui's may be stale for the same reason), ImGui's only when
        // the game's cannot be read. A held button that did not start on the overlay is the game's (a camera drag).
        // Typing in an ImGui field or in the game's own chat line: the keys are the text's. "Typing" means typing: the
        // game's field counts only when something was typed into it since it became active or since the last Enter or
        // Esc (with the game's direct chat the line is active all the time)
        var now     = ImGui.GetTime();
        var game    = _keyboard.Alive;
        var pressed = _sources.Take(Clock, ImGuiPresses());
        foreach (var line in _sources.Lines)
            _log.Information(line);
        _sources.Lines.Clear();

        var alt   = game ? _keyboard.Alt : io.KeyAlt;
        var ctrl  = game ? _keyboard.Ctrl : io.KeyCtrl;
        var shift = game ? _keyboard.Shift : io.KeyShift;
        var other = (alt || ctrl || shift) && (game ? _keyboard.OtherDown || OtherKeyDown(io) : OtherKeyDown(io));

        // a character key with Ctrl or Alt held is a shortcut (Ctrl+1 on the hotbar), not typing
        var esc        = pressed.Contains(CKey.Escape);
        var imguiChar  = CharacterKeyPressed() && !ctrl && !alt;
        var charTyped  = game ? _keyboard.TakeCharacterPressed() | imguiChar : imguiChar;
        var gameField  = _gameTyping.Now();
        var gameTyping = _typingWatch.Update(gameField, charTyped, esc || pressed.Contains(CKey.Enter));
        _typing = gameTyping;
        var typing = io.WantTextInput || gameTyping;
        var keys   = new HeldKeys(alt, ctrl, shift, mouseDown && !_pressHere, typing, other);
        _body.Wanted = Face.WantsBody(_session, keys);
        _body.Project();
        Face.WindowOpen = WindowOpen?.Invoke() ?? false;
        var shown = Face.Update(_session, _body, keys, now, _body.Gone);
        // a press counts only when On you was showing when the key went down (not the Esc or Enter of the window, nor an
        // arrow pressed just before the hold-key)
        _sources.Showing(shown, Clock);
        if (!shown)
        {
            // not drawn: the key gate forgets by itself after a frame
            _pressHere = false;
            return;
        }

        // The mouse is On you's only where Face.MouseWantedAt says (over a place of the character or over the chooser). Elsewhere the overlay is see-through for the mouse (NoInputs):
        // ImGui does not want the mouse there, so the game keeps its clicks, targeting, hotbars and the wheel.
        var wanted = _pressHere || Face.MouseWantedAt(io.MousePos);
        var flags  = wanted ? Flags : Flags | ImGuiWindowFlags.NoInputs;

        var view      = ImGui.GetMainViewport();
        var keysOwned = false;
        ImGui.SetNextWindowPos(view.Pos);
        ImGui.SetNextWindowSize(view.Size);
        ImGui.PushStyleVar(ImGuiStyleVar.WindowPadding, Vector2.Zero);
        ImGui.PushStyleVar(ImGuiStyleVar.WindowBorderSize, 0f);
        ImGui.PushStyleVar(ImGuiStyleVar.WindowRounding, 0f);
        try
        {
            // End always follows Begin, also when the drawing throws (Draw catches and logs it)
            FrameScope.Run(() => ImGui.Begin("###ModCloset2OnYou", flags), () =>
            {
                var hovered = wanted && ImGui.IsWindowHovered();
                if (hovered && (ImGui.IsMouseClicked(ImGuiMouseButton.Left) || ImGui.IsMouseClicked(ImGuiMouseButton.Right)))
                    _pressHere = true;
                else if (!mouseDown)
                    _pressHere = false;

                _canvas ??= new ImGuiCanvas(_ui, _textures);
                // The overlay rarely has ImGui's focus (it never comes to the front): its keys do not depend on it. They are
                // On you's while it listens (chooser open, or staying on), unless the mouse is over another window (the
                // closet window over the overlay has its own Enter and Esc then) or a text field is typed in.
                var otherWindow = !hovered && ImGui.IsWindowHovered(ImGuiHoveredFlags.AnyWindow | ImGuiHoveredFlags.AllowWhenBlockedByActiveItem);
                keysOwned = Face.WantsKeyboard && !otherWindow && !typing;
                _canvas.Begin(new Rect(view.Pos.X, view.Pos.Y, view.Size.X, view.Size.Y), hovered || _pressHere, keysOwned);

                // The keys On you acts on are this frame's presses from both sources (the canvas read ImGui's alone); and
                // when On you listens but cannot act on Esc, the reason goes to the log once
                foreach (var k in KeySources.Watched)
                    _canvas.Input.Keys.Remove(k);
                if (keysOwned)
                    _canvas.Input.Keys.UnionWith(pressed);
                _witness.EscNotActedOn(Face.Listening, esc, keysOwned, gameTyping, io.WantTextInput, otherWindow);
                foreach (var line in _witness.Lines)
                    _log.Information(line);
                _witness.Lines.Clear();
                try
                {
                    Face.Draw(_canvas, _session, _body);
                }
                finally
                {
                    _canvas.End();
                }
            }, ImGui.End);
        }
        finally
        {
            ImGui.PopStyleVar(3);
        }

        // Enter, Esc and the arrows On you acts on are kept from the game (Esc would open the game's menu, the arrows
        // turn the camera); every other key stays the game's. FilterGameKeys does that from the framework update, through
        // the key gate (only what a frame that was drawn asked for). Only if it has not run for a second (it should always
        // run) does ImGui's whole-keyboard capture stand in, as in the window.
        var wants = keysOwned ? Face.Wants : OnYouKeys.None;
        _gate.Drawn(wants);
        var filtered = _filteredAt >= 0 && Environment.TickCount64 - _filteredAt < 1000;
        if (!filtered && wants != OnYouKeys.None)
            ImGui.SetNextFrameWantCaptureKeyboard(true);
    }

    /// <summary> ImGui's presses of Esc, Enter and the arrows this frame (the arrows with ImGui's repeat). </summary>
    private static IEnumerable<CKey> ImGuiPresses()
    {
        if (ImGui.IsKeyPressed(ImGuiKey.Escape, false))
            yield return CKey.Escape;
        if (ImGui.IsKeyPressed(ImGuiKey.Enter, false) || ImGui.IsKeyPressed(ImGuiKey.KeypadEnter, false))
            yield return CKey.Enter;
        if (ImGui.IsKeyPressed(ImGuiKey.LeftArrow, true))
            yield return CKey.Left;
        if (ImGui.IsKeyPressed(ImGuiKey.RightArrow, true))
            yield return CKey.Right;
        if (ImGui.IsKeyPressed(ImGuiKey.UpArrow, true))
            yield return CKey.Up;
        if (ImGui.IsKeyPressed(ImGuiKey.DownArrow, true))
            yield return CKey.Down;
    }

    /// <summary> The keys that type a character: letters, digits, space, punctuation (keypad digits too). </summary>
    private static readonly ImGuiKey[] CharacterKeys = Enum.GetValues<ImGuiKey>().Distinct().Where(k =>
    {
        var n = k.ToString();
        return n.Length == 1 || n.StartsWith("Key", StringComparison.Ordinal) && n.Length == 4 || n.StartsWith("Keypad", StringComparison.Ordinal) && n.Length == 7
         || n is "Space" or "Apostrophe" or "Comma" or "Minus" or "Period" or "Slash" or "Semicolon" or "Equal" or "LeftBracket" or "Backslash"
             or "RightBracket" or "GraveAccent" or "KeypadDecimal" or "KeypadDivide" or "KeypadMultiply" or "KeypadSubtract" or "KeypadAdd";
    }).ToArray();

    /// <summary> A character key went down this frame (from ImGui's key events). </summary>
    private static bool CharacterKeyPressed()
    {
        foreach (var k in CharacterKeys)
            if (ImGui.IsKeyPressed(k, false))
                return true;
        return false;
    }

    /// <summary> The keys that make a modifier part of a shortcut (every named key but the modifiers, mouse and pad). </summary>
    private static readonly ImGuiKey[] OtherKeys = Enum.GetValues<ImGuiKey>().Distinct().Where(k =>
    {
        var n = k.ToString();
        return (int)k >= (int)ImGuiKey.Tab && (int)k < (int)ImGuiKey.GamepadStart
         && !n.Contains("Ctrl") && !n.Contains("Shift") && !n.Contains("Alt") && !n.Contains("Super") && !n.Contains("Mod")
         && !n.Contains("Reserved") && !n.Contains("Mouse") && !n.Contains("Gamepad") && !n.Contains("BEGIN") && !n.Contains("END") && !n.Contains("COUNT");
    }).ToArray();

    /// <summary> Another key is down as ImGui sees it: a held modifier is then part of a shortcut (Alt+1). </summary>
    private static bool OtherKeyDown(ImGuiIOPtr io)
    {
        foreach (var k in OtherKeys)
            if (ImGui.IsKeyDown(k))
                return true;
        return false;
    }

    /// <summary>
    /// Call every frame from IFramework.Update (before the game reads its keys), like the window's FilterGameKeys:
    /// the keys On you will act on (<see cref="OnYouFace.Wants"/>) are hidden from the game; every other key always
    /// goes to the game. Nothing is hidden when the overlay was not drawn in this or the last frame (plugins are not
    /// drawn in cutscenes, group pose or with the UI hidden: Esc must stay the game's there), nor while the game's chat
    /// is typed in. ImGui still sees the keys (it reads them from the window messages).
    /// </summary>
    public void FilterGameKeys(IKeyState keys)
    {
        _filteredAt = Environment.TickCount64;

        // The keyboard as the game sees it, read before anything is hidden: Esc, Enter and the arrows as presses (the
        // arrows repeating while held), acted on at the next draw; the modifiers and "another key" for the On-you key
        _keyboard.Sample(keys);
        _sources.Sample(Clock, k => _keyboard.Down(keys, k));

        // This runs every frame, drawn or not: the place to end "stays on" in a cutscene or group pose, where Dalamud
        // draws no plugin and the face's own Update would not see it
        if (_body.Gone)
            Face.BodyGone();

        var down = OnYouKeys.None;
        if (_keyboard.Down(keys, CKey.Enter))
            down |= OnYouKeys.Enter;
        if (_keyboard.Down(keys, CKey.Escape))
            down |= OnYouKeys.Escape;
        if (_keyboard.Down(keys, CKey.Left) || _keyboard.Down(keys, CKey.Right) || _keyboard.Down(keys, CKey.Up) || _keyboard.Down(keys, CKey.Down))
            down |= OnYouKeys.Arrows;
        var wants = _gate.Hide(_typing, down, _keyboard.InFront);
        if ((wants & OnYouKeys.Enter) != 0)
            keys[VirtualKey.RETURN] = false;
        if ((wants & OnYouKeys.Escape) != 0)
            keys[VirtualKey.ESCAPE] = false;
        if ((wants & OnYouKeys.Arrows) != 0)
        {
            keys[VirtualKey.LEFT]  = false;
            keys[VirtualKey.RIGHT] = false;
            keys[VirtualKey.UP]    = false;
            keys[VirtualKey.DOWN]  = false;
        }
    }

    public void Dispose()
        => _canvas?.Dispose();
}
