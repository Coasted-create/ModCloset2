using ModCloset2.Ui.Canvas;

namespace ModCloset2.Ui.Face;

/// <summary>
/// Decides, once per frame before the game reads its keys, which keys the closet window hides from the game. Pure, so
/// it can be tested without the game. The window tells it what it saw each time it is drawn; the plugin's per-frame
/// call asks it. A window that was not drawn in this or the last frame (collapsed, closed, hidden with the game's UI,
/// in a cutscene or group pose) hides nothing, and while any text field of the game is typed in, nothing is hidden and
/// the closet reads no keys.
/// </summary>
public sealed class KeyGate
{
    private int _framesSinceDraw = int.MaxValue / 2;
    private bool _mouseOver;
    private bool _typing;
    private GameKeys _wants;

    /// <summary> Called from the window's Draw: the mouse is over it, one of its text fields is typed in, what it would act on. </summary>
    public void Drawn(bool mouseOver, bool typing, GameKeys wants)
    {
        _framesSinceDraw = 0;
        _mouseOver       = mouseOver;
        _typing          = typing;
        _wants           = wants;
    }

    /// <summary> Called once per frame (from the plugin's framework update). Returns the keys to hide from the game now. </summary>
    public GameKeys Hide(bool gameTyping)
    {
        _framesSinceDraw++;
        if (_framesSinceDraw > 1)
        {
            // not drawn last frame: forget what it saw, so nothing sticks
            Clear();
            return GameKeys.None;
        }

        return _mouseOver && !_typing && !gameTyping ? _wants : GameKeys.None;
    }

    /// <summary>
    /// Does the window read the keyboard this frame: with the mouse over it while nothing is typed into the game's
    /// field, or while one of its own text fields has the keyboard.
    /// </summary>
    public static bool WindowReadsKeys(bool mouseOver, bool gameTyping, bool ownTyping)
        => mouseOver && !gameTyping || ownTyping;

    /// <summary> The window closed: nothing is hidden from now on. </summary>
    public void Clear()
    {
        _mouseOver = false;
        _typing    = false;
        _wants     = GameKeys.None;
    }
}

/// <summary> Lets go of the window's pictures a while after it was closed; ticked every frame, also while closed. </summary>
public sealed class ReleaseTimer
{
    private readonly long _afterMs;
    private long _closedAt = -1;
    private bool _released;

    public ReleaseTimer(long afterMs)
        => _afterMs = afterMs;

    /// <summary> True exactly once, when the window has been closed for longer than the delay. </summary>
    public bool Tick(bool open, long nowMs)
    {
        if (open)
        {
            _closedAt = -1;
            _released = false;
            return false;
        }

        if (_closedAt < 0)
            _closedAt = nowMs;
        if (_released || nowMs - _closedAt <= _afterMs)
            return false;
        _released = true;
        return true;
    }
}

/// <summary>
/// Tells whether a text field of the game is really being typed in. The game's field can be active without anyone
/// typing (the game's "direct chat" keeps the chat line active all the time, and a chat line that was just opened is
/// empty), so active alone does not count. It counts only when a character key (letters, digits, space, punctuation;
/// not with Ctrl or Alt) went down since the field became active, or since the last Enter or Esc. That Enter or Esc
/// still belongs to the chat until it is let go; after that the window gets its keys again. Pure, so it can be tested
/// without the game; fed once per drawn frame.
/// </summary>
public sealed class ChatTyping
{
    private bool _typed;
    private bool _closing;

    /// <summary> The result of the last <see cref="Update"/>. </summary>
    public bool Typing { get; private set; }

    /// <param name="active"> The game says one of its text fields is active. </param>
    /// <param name="characterDown"> A character key went down this frame (without Ctrl or Alt). </param>
    /// <param name="enterOrEscapeDown"> Enter or Esc is held. </param>
    public bool Update(bool active, bool characterDown, bool enterOrEscapeDown)
    {
        if (!active)
        {
            _typed   = false;
            _closing = false;
            return Typing = false;
        }

        if (_closing)
        {
            // the Enter or Esc that ends what was typed stays the chat's until it is let go
            if (!enterOrEscapeDown)
            {
                _closing = false;
                _typed   = false;
            }
        }
        else if (_typed && enterOrEscapeDown)
            _closing = true;

        if (characterDown && !_closing)
            _typed = true;
        return Typing = _typed || _closing;
    }

    /// <summary> The window closed or stopped being drawn: start again from "nothing typed". </summary>
    public void Clear()
    {
        _typed   = false;
        _closing = false;
        Typing   = false;
    }
}

/// <summary>
/// The keys the window acts on (Enter, Esc, the four arrows, Z for Ctrl+Z), from two sources. The main one is the game's
/// key state, sampled every frame in the framework update before anything is hidden from the game: in the game ImGui
/// gets no key presses while none of its windows has the keyboard focus, and the window has it only after a click in
/// it. The second is ImGui's own report. A press counts once, whichever source has it first: an ImGui press is dropped
/// when the game's state had that key down around then. The arrows repeat while held, in the canvas's rhythm. Like On
/// you's key sources (same timing), so the two behave alike. A press the game saw and ImGui did not is written to the log
/// once per key. Pure; the window feeds it.
/// </summary>
public sealed class WindowKeySources
{
    /// <summary> In the last sample the game was in front. </summary>
    public bool InFront
        => _wasInFront;

    public const double RepeatDelay = 0.275, RepeatRate = 0.05;

    /// <summary> How close in time the two sources' reports of one press are. </summary>
    public const double Same = 0.3;

    public static readonly Key[] Watched = { Key.Enter, Key.Escape, Key.Left, Key.Right, Key.Up, Key.Down, Key.Z };

    private readonly bool[] _down = new bool[Watched.Length];
    private readonly double[] _next = new double[Watched.Length];
    private readonly double[] _gameDownAt = Enumerable.Repeat(-10.0, Watched.Length).ToArray();
    private readonly double[] _imguiAt = Enumerable.Repeat(-10.0, Watched.Length).ToArray();
    private readonly double[] _checkAt = Enumerable.Repeat(-1.0, Watched.Length).ToArray();

    /// <summary> When ImGui's report of a key was acted on before the game's key state showed it (the same press, still to come there). </summary>
    private readonly double[] _imguiAlone = Enumerable.Repeat(-10.0, Watched.Length).ToArray();

    /// <summary> Held since before the window was drawn: neither a press nor a repeat until it is let go. </summary>
    private readonly bool[] _fromBefore = new bool[Watched.Length];
    private readonly Dictionary<Key, double> _pending = new();
    private readonly HashSet<Key> _said = new();
    private double _sampledAt = -10;

    // the "was drawn" mark: when Take last ran, and how many samples came since. Not drawn means both several samples
    // and some time without a draw, so neither a long frame (one sample, much time) nor doubled calls (many samples,
    // little time) count as a gap
    private bool _wasInFront = true;
    private double _drawnAt = -10;
    private int _samplesSinceDraw = int.MaxValue / 2;

    /// <summary> Samples and time without a draw after which the window counts as not drawn. </summary>
    public const int GapSamples = 3;
    public const double GapTime = 0.1;

    /// <summary> Ctrl as the game's key state has it (ImGui may not know it either). </summary>
    public bool Ctrl { get; private set; }

    /// <summary> Esc as the game's key state has it now. </summary>
    public bool EscapeDown
        => _down[Array.IndexOf(Watched, Key.Escape)];

    /// <summary> The game's key state saw a press that Draw has not taken yet. </summary>
    public bool Pending
        => _pending.Count > 0;

    /// <summary> Enter or Esc as the game's key state has them now (for the chat rule). </summary>
    public bool EnterOrEscapeDown
        => _down[0] || _down[1];

    /// <summary> Lines to write to the log (each key once per session); the host takes them out. </summary>
    public List<string> Lines { get; } = new();

    public static string MissedLine(Key key)
        => $"Mod Closet 2: the window: ImGui did not report {key} although the game's key state showed it down; acted on the game's key state";

    private static bool Repeats(Key key)
        => key is Key.Left or Key.Right or Key.Up or Key.Down;

    /// <summary> The game's key state is being sampled (the framework update ran in the last second). </summary>
    public bool Alive(double now)
        => now - _sampledAt < 1;

    /// <summary> The window is being drawn now (it was drawn lately, with no gap since). </summary>
    public bool Drawing(double now)
        => _drawnAt >= 0 && !(_samplesSinceDraw >= GapSamples && now - _drawnAt > GapTime);

    /// <summary> The key has been held since before the window was drawn (it stays out of the window until let go). </summary>
    public bool HeldFromBefore(Key key)
    {
        var i = Array.IndexOf(Watched, key);
        return i >= 0 && _fromBefore[i];
    }

    /// <summary>
    /// The window opens: forget every press so far, and keep the keys held now out of it until they are let go (the Esc
    /// that ended On you, the Enter that sent the command).
    /// </summary>
    public void Forget()
    {
        _pending.Clear();
        _drawnAt          = -10;
        _samplesSinceDraw = int.MaxValue / 2;
        for (var i = 0; i < Watched.Length; ++i)
            _fromBefore[i] = _down[i];
    }

    /// <summary>
    /// From the framework update, every frame, before anything is hidden: which keys the game has down. While the game
    /// is not in front every key reads as up; the keys down in the first sample back in front are held from before.
    /// </summary>
    public void Sample(double now, Func<Key, bool> down, bool ctrl, bool inFront = true)
    {
        _sampledAt = now;
        Ctrl       = inFront && ctrl;
        if (_samplesSinceDraw < int.MaxValue / 2)
            _samplesSinceDraw++;

        // a press counts only when the window was being drawn as the key went down, and the game was in front before
        var drawing = Drawing(now) && _wasInFront;
        _wasInFront = inFront;
        for (var i = 0; i < Watched.Length; ++i)
        {
            var key = Watched[i];
            var d   = inFront && down(key);
            if (d)
                _gameDownAt[i] = now;
            if (!d)
                _fromBefore[i] = false;
            if (d && !_down[i])
            {
                if (!drawing)
                    _fromBefore[i] = true;
                // ImGui had it first and it was acted on: the same press, not another one
                else if (now - _imguiAlone[i] > Same)
                    _pending[key] = now;
                _imguiAlone[i] = -10;
                _next[i]       = now + RepeatDelay;
                _checkAt[i]    = drawing ? now : -1;
            }
            else if (d && Repeats(key) && !_fromBefore[i] && now >= _next[i])
            {
                _pending[key] = now;
                _next[i]      = now + RepeatRate;
            }

            _down[i] = d;
        }
    }

    /// <summary>
    /// In Draw, every frame: ImGui's presses of this frame, and whether the game's presses may be read now (the mouse
    /// is over the window, and neither its own field nor the game's chat is typed in). Returns the presses to act on,
    /// each press once, and forgets the game's pending ones either way.
    /// </summary>
    public HashSet<Key> Take(double now, IEnumerable<Key> imgui, bool gameAllowed)
    {
        // coming back after not being drawn (collapsed, hidden with the game's UI, a cutscene, just opened): what went
        // down before is not the window's, and what is held now stays out until let go
        if (!Drawing(now))
        {
            _pending.Clear();
            for (var i = 0; i < Watched.Length; ++i)
                _fromBefore[i] |= _down[i];
        }

        _drawnAt          = now;
        _samplesSinceDraw = 0;

        // a press not taken for a while is old news, not acted on
        var fromGame = _pending.Where(p => now - p.Value <= Same).Select(p => p.Key).ToList();
        _pending.Clear();

        // ImGui's report of a key held since before the window was drawn is that same old press
        var fresh = imgui.Where(k => !HeldFromBefore(k)).ToList();
        if (!gameAllowed)
            return new HashSet<Key>(fresh);

        var result = new HashSet<Key>(fromGame);
        var alive  = Alive(now);
        foreach (var key in fresh)
        {
            var i = Array.IndexOf(Watched, key);
            if (i < 0)
            {
                result.Add(key);
                continue;
            }

            _imguiAt[i] = now;
            // the game's state had it down around now: the same press, already counted (or about to be)
            if (!alive || now - _gameDownAt[i] > Same)
            {
                result.Add(key);
                _imguiAlone[i] = now;
            }
        }

        // a press the game saw: did ImGui report it around then? Asked once, a moment later
        for (var i = 0; i < Watched.Length; ++i)
        {
            if (_checkAt[i] < 0 || now - _checkAt[i] < Same)
                continue;
            if (_imguiAt[i] < _checkAt[i] - Same && _said.Add(Watched[i]))
                Lines.Add(MissedLine(Watched[i]));
            _checkAt[i] = -1;
        }

        return result;
    }
}

/// <summary>
/// Whether the game is the window in front, from a check that can fail or have no answer for a moment (no foreground
/// window). No answer keeps the last one; before the first answer it is "in front". Fails open: the keys are never
/// dead because of it. Pure.
/// </summary>
public sealed class InFrontState
{
    public bool InFront { get; private set; } = true;

    /// <param name="answer"> true: the foreground window is this process's; false: another's; null: cannot tell. </param>
    public bool Update(bool? answer)
    {
        if (answer is { } a)
            InFront = a;
        return InFront;
    }
}
