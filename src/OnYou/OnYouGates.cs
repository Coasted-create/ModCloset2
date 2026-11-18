using ModCloset2.Ui.Canvas;

namespace ModCloset2.OnYou;

/// <summary>
/// Decides, once per frame before the game reads its keys, which keys On you hides from the game (the window's
/// KeyGate, for the overlay). Pure, so it can be tested without the game. The overlay says what it would act on each
/// time it is drawn; the plugin's per-frame call asks. An overlay that was not drawn in this or the last frame (plugins
/// are not drawn in cutscenes, in group pose or with the game's UI hidden) hides nothing and forgets what it saw, so no
/// key stays taken from the game; while a text field of the game is typed in, nothing is hidden.
/// </summary>
public sealed class OnYouKeyGate
{
    private int _framesSinceDraw = int.MaxValue / 2;
    private OnYouKeys _wants;
    private OnYouKeys _held;

    /// <summary> Called from the overlay's Draw: the keys On you would act on now (None when it does not own the keys). </summary>
    public void Drawn(OnYouKeys wants)
    {
        _framesSinceDraw = 0;
        _wants           = wants;
    }

    /// <summary>
    /// Called once per frame (from the plugin's framework update), with the keys the game's key state has down now.
    /// Returns the keys to hide from the game now. A key that was hidden stays hidden until it is let go, also when On you
    /// no longer wants it (Enter kept and closed the chooser while still held): the game would see the held key appear as
    /// a fresh press and act on it too (open the chat, the game's menu).
    /// </summary>
    public OnYouKeys Hide(bool gameTyping, OnYouKeys down = OnYouKeys.None, bool inFront = true)
    {
        _framesSinceDraw++;
        if (!inFront)
        {
            // the game is not in front: nothing is hidden, also no key held from before
            _held = OnYouKeys.None;
            return OnYouKeys.None;
        }

        var held = _held & down;
        if (_framesSinceDraw > 1)
        {
            // not drawn last frame: forget what it saw, so nothing sticks (only a key still held from before stays hidden)
            Clear();
            return _held = held;
        }

        var hide = gameTyping ? held : _wants | held;
        _held = hide;
        return hide;
    }

    /// <summary> Nothing is hidden from now on (until the next draw says otherwise). </summary>
    public void Clear()
        => _wants = OnYouKeys.None;

    /// <summary> What the last draw said (for the tests). </summary>
    public OnYouKeys Seen
        => _wants;
}

/// <summary>
/// Why On you must not be up now: the character has left the screen for good, or the player is busy with the game
/// itself. On you then ends (also when it stays on, an open chooser puts back) and the key does nothing.
/// </summary>
public static class BodyGone
{
    /// <summary>
    /// "logged out", "zoning" (a loading screen and the seconds after it), "cutscene", "group pose", "in combat",
    /// "in a duty", "busy" (talking to someone, crafting, gathering, fishing, performing, a summoning bell…), or null
    /// when On you may be up (only out of the camera's view is not a reason).
    /// </summary>
    public static string? Reason(bool loggedIn, bool zoning, bool cutscene, bool groupPose, bool combat = false, bool duty = false, bool occupied = false)
        => !loggedIn ? "logged out"
            : zoning ? "zoning"
            : cutscene ? "cutscene"
            : groupPose ? "group pose"
            : combat ? "in combat"
            : duty ? "in a duty"
            : occupied ? "busy"
            : null;
}

/// <summary>
/// The On-you key as a deliberate wish, not a hotbar modifier: it counts only after it has been held alone (no other
/// modifier, no other key, no mouse button) for <see cref="HoldSeconds"/>. A quick Alt+1 never shows anything; any
/// other key pressed during the hold spoils it until the key is let go. Pure; the face asks it every frame.
/// </summary>
public sealed class KeyHold
{
    /// <summary> About a quarter of a second: longer than a hotbar press, short enough not to feel slow. </summary>
    public const double HoldSeconds = 0.25;

    private double _since = -1;
    private bool _spoiled;

    /// <summary> Feeds one frame. <paramref name="down"/>: the chosen key alone; <paramref name="other"/>: another key or a mouse button is down. </summary>
    public bool Update(bool down, bool other, double time)
    {
        if (!down)
        {
            _since   = -1;
            _spoiled = false;
            return false;
        }

        if (_since < 0)
            _since = time;
        if (other && time - _since < HoldSeconds)
            _spoiled = true;
        return !_spoiled && time - _since >= HoldSeconds;
    }
}

/// <summary>
/// An ImGui window's frame: Begin, then the drawing when it is shown, and End always, also when the drawing throws
/// (ImGui needs every Begin matched by an End, or the next frame's window stack is broken).
/// </summary>
public static class FrameScope
{
    public static void Run(Func<bool> begin, Action draw, Action end)
    {
        var shown = begin();
        try
        {
            if (shown)
                draw();
        }
        finally
        {
            end();
        }
    }
}

/// <summary>
/// Whether the user is typing in one of the game's text fields, not merely whether one is active. With the game's
/// "direct chat" the chat line is active all the time; an active field that nothing was typed into since it became
/// active, or since the last Enter or Esc, does not take On you's Enter, Esc and arrows away. Pure; fed every frame.
/// </summary>
public sealed class TypingWatch
{
    private bool _wasActive;
    private bool _typed;

    /// <param name="fieldActive"> The game says one of its text fields is active. </param>
    /// <param name="characterKey"> A character key went down this frame (letters, digits, space, punctuation). </param>
    /// <param name="enterOrEsc"> Enter or Esc went down this frame. </param>
    /// <returns> True while the user is typing: this frame's Enter or Esc is then the text's (once). </returns>
    public bool Update(bool fieldActive, bool characterKey, bool enterOrEsc)
    {
        if (!fieldActive)
        {
            _wasActive = false;
            _typed     = false;
            return false;
        }

        if (!_wasActive)
        {
            // just became active (opened, or direct chat at start): nothing typed yet
            _wasActive = true;
            _typed     = false;
        }

        if (characterKey)
            _typed = true;
        var typing = _typed;

        // Enter sends, Esc leaves the line: the next keys are On you's again until something is typed
        if (enterOrEsc)
            _typed = false;
        return typing;
    }
}

/// <summary>
/// Whether the game is in front (its process owns the foreground window). An answer that cannot be had (no foreground
/// window for a moment) keeps the last one; before the first, the game counts as in front. Pure.
/// </summary>
public sealed class GameInFront
{
    public bool Now { get; private set; } = true;

    /// <param name="answer"> True or false, or null when it cannot be told. </param>
    public bool Update(bool? answer)
    {
        if (answer is { } a)
            Now = a;
        return Now;
    }

    /// <summary> The check failed: fail open (in front), so the keys never go dead because of it. </summary>
    public bool Failed()
        => Now = true;
}

/// <summary>
/// The keyboard as On you reads it, given whether the game is in front. Not in front: every key reads as up (nothing
/// stays hidden from the game, the hold-key is not held, no other key is down). Back in front: a key that is down at its
/// first reading is held from before (let go elsewhere, or still held from the other window) and reads as up until it is
/// let go: no press, no repeat, not the start of a hold. Every key must be read every sample. Pure.
/// <para> Not handled here: a press made in the very frame the game comes back in front is taken for one from before
/// (lost, never doubled). </para>
/// </summary>
public sealed class FrontKeys
{
    private readonly Dictionary<int, int> _seen = new();
    private readonly HashSet<int> _fromBefore = new();
    private int _epoch;
    private bool _front = true;

    /// <summary> Once per sample, before any key is read. </summary>
    public void Begin(bool inFront)
    {
        if (inFront && !_front)
            _epoch++;
        if (!inFront)
            _fromBefore.Clear();
        _front = inFront;
    }

    /// <summary> A key (any code) as On you sees it, from its raw reading now. </summary>
    public bool Down(int code, bool raw)
    {
        if (!_front)
            return false;
        if (!_seen.TryGetValue(code, out var e) || e != _epoch)
        {
            _seen[code] = _epoch;
            // (at the start, in front from the first sample, nothing is from before)
            if (raw && _epoch > 0)
                _fromBefore.Add(code);
        }

        if (_fromBefore.Contains(code))
        {
            if (raw)
                return false;
            _fromBefore.Remove(code);
        }

        return raw;
    }
}

/// <summary>
/// Which character keys went down, for <see cref="TypingWatch"/>: a key that goes down while Ctrl or Alt is held is a
/// shortcut (Ctrl+1 on the hotbar), not typing, as in the window. Pure; the host feeds it every frame.
/// </summary>
public sealed class CharacterPresses
{
    private bool[] _was = Array.Empty<bool>();

    /// <param name="down"> Each character key: down now. </param>
    /// <returns> True when one of them went down since the last call with neither Ctrl nor Alt held. </returns>
    public bool Sample(IReadOnlyList<bool> down, bool ctrl, bool alt)
    {
        if (_was.Length != down.Count)
            _was = new bool[down.Count];
        var typed = false;
        for (var i = 0; i < down.Count; ++i)
        {
            if (down[i] && !_was[i] && !ctrl && !alt)
                typed = true;
            _was[i] = down[i];
        }

        return typed;
    }
}

/// <summary>
/// The keys On you acts on (Esc, Enter, the four arrows), from two sources. The main one is the game's key state, sampled
/// every frame in the framework update before anything is hidden: ImGui gets no keys in the game while none of its
/// windows has the keyboard focus, and the overlay never has it. The second is ImGui's own report. A press counts once,
/// whichever source has it first: an ImGui press is dropped when the game's state had that key down around then. The
/// arrows repeat while held (the canvas's rhythm). A press counts only when On you was showing when the key went down
/// (<see cref="Showing"/>): the Esc or Enter that the window acted on just before its switch was clicked, or an arrow
/// pressed just before the hold-key (and its repeats), are not On you's; a press during one long frame (a stall) is. A press the game saw and ImGui did not is
/// written to the log once per key. Pure; the overlay feeds it.
/// </summary>
public sealed class KeySources
{
    public const double RepeatDelay = 0.275, RepeatRate = 0.05;

    /// <summary> How close in time the two sources' reports of one press are. </summary>
    public const double Same = 0.3;

    /// <summary>
    /// Having been away (nothing shown, whatever the reason) needs both: this many samples of the game's keyboard
    /// without a shown frame, and this much time. One long frame (a try can stall the game for half a second) is one
    /// sample and much time; a doubled framework call is many samples and little time: neither is a gap. As the window.
    /// </summary>
    public const int GapSamples = 3;
    public const double GapTime = 0.1;

    public static readonly Key[] Watched = { Key.Escape, Key.Enter, Key.Left, Key.Right, Key.Up, Key.Down };

    private readonly bool[] _down = new bool[Watched.Length];
    private readonly double[] _next = new double[Watched.Length];
    private readonly double[] _gameDownAt = Enumerable.Repeat(-10.0, Watched.Length).ToArray();

    /// <summary> When each key last went down in the game's key state (a repeat belongs to that press). </summary>
    private readonly double[] _edgeAt = Enumerable.Repeat(-10.0, Watched.Length).ToArray();
    private readonly double[] _imguiAt = Enumerable.Repeat(-10.0, Watched.Length).ToArray();

    /// <summary> When ImGui's report of a key was acted on before the game's key state showed it (the same press, still to come there). </summary>
    private readonly double[] _imguiAlone = Enumerable.Repeat(-10.0, Watched.Length).ToArray();
    private readonly double[] _checkAt = Enumerable.Repeat(-1.0, Watched.Length).ToArray();
    /// <summary> Presses the game's key state saw and Draw has not taken yet: when, and when the key went down. </summary>
    private readonly Dictionary<Key, (double At, double Edge)> _pending = new();

    /// <summary> Since when On you has been showing without a break (null: not showing). </summary>
    private double? _upSince;
    private double _lastShown = -10;
    private int _samplesSinceShown;
    private readonly HashSet<Key> _said = new();
    private double _sampledAt = -10;

    /// <summary> Lines to write to the log (each key once per session); the host takes them out. </summary>
    public List<string> Lines { get; } = new();

    public static string MissedLine(Key key)
        => $"Mod Closet 2: On you: ImGui did not report {key} although the game's key state showed it down; acted on the game's key state";

    private static bool Repeats(Key key)
        => key is Key.Left or Key.Right or Key.Up or Key.Down;

    /// <summary>
    /// From Draw, every time it asks the face (also when it is not shown): whether On you shows now. Times, not frame
    /// counts: it does not matter how often this, <see cref="Sample"/> and <see cref="Take"/> are called per frame.
    /// </summary>
    public void Showing(bool showing, double now)
    {
        if (!showing)
        {
            _upSince = null;
            return;
        }

        if (_upSince is null || _samplesSinceShown >= GapSamples && now - _lastShown > GapTime)
            _upSince = now;
        _lastShown         = now;
        _samplesSinceShown = 0;
    }

    /// <summary> The key went down after On you came up (strictly: the frame it comes up in is not yet its). </summary>
    private bool Since(double edge)
        => _upSince is { } up && edge > up;

    /// <summary> The game's key state is being sampled (the framework update ran in the last second). </summary>
    public bool Alive(double now)
        => now - _sampledAt < 1;

    /// <summary> From the framework update, every frame: which watched keys the game's key state has down. </summary>
    public void Sample(double now, Func<Key, bool> down)
    {
        _sampledAt = now;
        _samplesSinceShown++;
        for (var i = 0; i < Watched.Length; ++i)
        {
            var key = Watched[i];
            var d   = down(key);
            if (d)
                _gameDownAt[i] = now;
            if (d && !_down[i])
            {
                // ImGui had it first and it was acted on: the same press, not another one
                if (now - _imguiAlone[i] > Same)
                    _pending[key] = (now, now);
                _imguiAlone[i] = -10;
                _edgeAt[i]     = now;
                _next[i]    = now + RepeatDelay;
                _checkAt[i] = now;
            }
            else if (d && Repeats(key) && now >= _next[i])
            {
                _pending[key] = (now, _edgeAt[i]);
                _next[i] = now + RepeatRate;
            }

            _down[i] = d;
        }
    }

    /// <summary>
    /// In Draw, every frame: ImGui's presses of this frame. Returns the presses to act on (each press once), and
    /// forgets them.
    /// </summary>
    public HashSet<Key> Take(double now, IEnumerable<Key> imgui)
    {
        // a press not taken for a while (nothing was drawn then: a cutscene, or On you was off) is old news, not acted on;
        // and a key that went down before On you came up is not On you's (also its repeats)
        var result = new HashSet<Key>(_pending.Where(p => now - p.Value.At <= Same && Since(p.Value.Edge)).Select(p => p.Key));
        _pending.Clear();
        var alive = Alive(now);
        foreach (var key in imgui)
        {
            var i = Array.IndexOf(Watched, key);
            if (i < 0)
                continue;
            _imguiAt[i] = now;
            // the game's state had it down around now: the same press, already counted (or about to be)
            if (!alive || now - _gameDownAt[i] > Same)
            {
                // ImGui first: the game's report of the same press, if it comes, is not another press (also when this
                // one is not On you's)
                _imguiAlone[i] = now;
                if (Since(now))
                    result.Add(key);
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

/// <summary> When On you listens but cannot act on Esc, the reason goes to the log once per reason. Pure. </summary>
public sealed class KeyWitness
{
    private readonly HashSet<string> _said = new(StringComparer.Ordinal);

    /// <summary> Lines to write to the log (each reason once per session); the host takes them out. </summary>
    public List<string> Lines { get; } = new();

    public const string WhyTyping = "Mod Closet 2: On you did not act on Esc: the game's text field is being typed in";
    public const string WhyImGui  = "Mod Closet 2: On you did not act on Esc: an ImGui text field has the keyboard";
    public const string WhyWindow = "Mod Closet 2: On you did not act on Esc: another window is under the mouse";

    /// <summary> Esc went down (either source) while On you listens: if it cannot act on it, say why (once per reason). </summary>
    public void EscNotActedOn(bool listening, bool escDown, bool keysOwned, bool gameTyping, bool imguiText, bool otherWindow)
    {
        if (!listening || !escDown || keysOwned)
            return;
        if (gameTyping)
            Say("typing", WhyTyping);
        if (imguiText)
            Say("imguitext", WhyImGui);
        if (otherWindow)
            Say("window", WhyWindow);
    }

    private void Say(string reason, string line)
    {
        if (_said.Add(reason))
            Lines.Add(line);
    }
}
