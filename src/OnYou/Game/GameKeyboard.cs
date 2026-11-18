using System.Runtime.InteropServices;
using Dalamud.Game.ClientState.Keys;
using Dalamud.Plugin.Services;
using CKey = ModCloset2.Ui.Canvas.Key;

namespace ModCloset2.OnYou.Game;

/// <summary>
/// The keyboard as the game sees it, sampled every frame in the framework update (before anything is hidden from the
/// game). ImGui gets no keys in the game while none of its windows has the keyboard focus, and the overlay never has it
/// (the user's log showed Esc down in the game's key state and not in ImGui), so this is On you's main source.
/// <para>
/// Two readings per key, OR-ed: the physical keyboard (<c>GetAsyncKeyState</c>: nothing On you or the window hides from
/// the game changes it, so a held key reads as held; unlike the game thread's own key state it cannot stay "down" for a
/// key let go while another window was in front), and the game's key buffer (<see cref="IKeyState"/>), read before the
/// hiding. The buffer alone cannot tell presses apart: once a key is hidden (set to up) it reads up while still held,
/// and the next real press then looks like no change.
/// </para>
/// <para>
/// Only while the game is in front (the foreground window belongs to this process; Dalamud's windows dragged out of the
/// game count as the game): otherwise every key reads as up. Back in front, a key already down is held from before and
/// reads as up until let go (<see cref="FrontKeys"/>).
/// </para>
/// Framework thread (the game's window thread); the draw side reads the results.
/// </summary>
public sealed partial class GameKeyboard
{
    [LibraryImport("user32.dll")]
    private static partial short GetAsyncKeyState(int virtualKey);

    [LibraryImport("user32.dll")]
    private static partial nint GetForegroundWindow();

    [LibraryImport("user32.dll")]
    private static partial uint GetWindowThreadProcessId(nint window, out uint processId);

    /// <summary> Every key that makes a held modifier part of a shortcut (all but the modifiers and the mouse). </summary>
    private static readonly int[] Others = Range(0x08, 0x09).Concat(new[] { 0x0D, 0x1B }).Concat(Range(0x20, 0x2F)).Concat(Range(0x30, 0x39))
        .Concat(Range(0x41, 0x5A)).Concat(new[] { 0x5D }).Concat(Range(0x60, 0x6F)).Concat(Range(0x70, 0x7B)).Concat(Range(0xBA, 0xC0))
        .Concat(Range(0xDB, 0xDF)).Concat(new[] { 0xE2 }).ToArray();

    /// <summary> The keys that type a character: space, digits, letters, the number pad, punctuation. </summary>
    private static readonly int[] Characters = new[] { 0x20 }.Concat(Range(0x30, 0x39)).Concat(Range(0x41, 0x5A)).Concat(Range(0x60, 0x6F))
        .Concat(Range(0xBA, 0xC0)).Concat(Range(0xDB, 0xDF)).Concat(new[] { 0xE2 }).ToArray();

    private readonly IPluginLog _log;
    private readonly CharacterPresses _characters = new();
    private readonly bool[] _charDown = new bool[Characters.Length];
    private readonly GameInFront _inFront = new();
    private readonly FrontKeys _front = new();
    private bool _broken, _frontFailed;
    private IKeyState? _keys;
    private volatile bool _charPressed;
    private long _sampledAt = -100000;

    public GameKeyboard(IPluginLog log)
        => _log = log;

    public bool Alt { get; private set; }
    public bool Ctrl { get; private set; }
    public bool Shift { get; private set; }

    /// <summary> Some other key is down (Alt+1: the modifier is part of a shortcut). </summary>
    public bool OtherDown { get; private set; }

    /// <summary> The game is in front (as of the last sample). </summary>
    public bool InFront
        => _inFront.Now;

    /// <summary> The sampling ran in the last second (else the draw side falls back to ImGui). </summary>
    public bool Alive
        => !_broken && Environment.TickCount64 - _sampledAt < 1000;

    /// <summary> A character key went down (without Ctrl or Alt: a shortcut is not typing) since the last call (from the draw side); then forgotten. </summary>
    public bool TakeCharacterPressed()
    {
        var p = _charPressed;
        _charPressed = false;
        return p;
    }

    /// <summary> Every frame, from the framework update, before anything is hidden from the game. </summary>
    public void Sample(IKeyState keys)
    {
        _keys = keys;
        _front.Begin(CheckInFront());
        if (_broken)
            return;
        try
        {
            Alt   = Read((int)VirtualKey.MENU);
            Ctrl  = Read((int)VirtualKey.CONTROL);
            Shift = Read((int)VirtualKey.SHIFT);
            // every key is read every sample (FrontKeys needs each key's first reading back in front)
            var other = false;
            foreach (var vk in Others)
                other |= Read(vk);

            OtherDown = other;
            for (var i = 0; i < Characters.Length; ++i)
                _charDown[i] = Read(Characters[i]);
            if (_characters.Sample(_charDown, Ctrl, Alt))
                _charPressed = true;

            _sampledAt = Environment.TickCount64;
        }
        catch (Exception e)
        {
            // cannot read the keyboard: the draw side goes on with ImGui's report alone
            _broken = true;
            _log.Warning(e, "Mod Closet 2: On you cannot read the game's keyboard; using ImGui's keys only");
        }
    }

    /// <summary> Whether the game is in front; cannot tell: the last answer; the check fails: in front (said once). </summary>
    private bool CheckInFront()
    {
        try
        {
            var window = GetForegroundWindow();
            if (window == 0)
                return _inFront.Update(null);
            if (GetWindowThreadProcessId(window, out var process) == 0)
                return _inFront.Update(null);
            return _inFront.Update(process == (uint)Environment.ProcessId);
        }
        catch (Exception e)
        {
            if (!_frontFailed)
            {
                _frontFailed = true;
                _log.Warning(e, "Mod Closet 2: On you cannot tell whether the game is in front; reading the keyboard as if it were");
            }

            return _inFront.Failed();
        }
    }

    /// <summary> A key On you acts on, as it reads now (both readings; up while the game is not in front, or held from before). </summary>
    public bool Down(IKeyState keys, CKey key)
    {
        _keys = keys;
        return Read(key switch
        {
            CKey.Escape => (int)VirtualKey.ESCAPE,
            CKey.Enter  => (int)VirtualKey.RETURN,
            CKey.Left   => (int)VirtualKey.LEFT,
            CKey.Right  => (int)VirtualKey.RIGHT,
            CKey.Up     => (int)VirtualKey.UP,
            CKey.Down   => (int)VirtualKey.DOWN,
            _           => 0,
        });
    }

    private bool Read(int vk)
    {
        if (vk == 0)
            return false;
        var raw = false;
        if (!_broken)
            try
            {
                raw = Os(vk);
            }
            catch (Exception e)
            {
                // cannot read the keyboard: the draw side goes on with ImGui's report (Alive is false from now)
                _broken = true;
                _log.Warning(e, "Mod Closet 2: On you cannot read the game's keyboard; using ImGui's keys only");
            }

        if (!raw)
            try
            {
                raw = _keys?[vk] ?? false;
            }
            catch
            {
                // a reading that fails is "up"
            }

        return _front.Down(vk, raw);
    }

    /// <summary> The physical keyboard: the high bit is "down now". </summary>
    private static bool Os(int vk)
        => (GetAsyncKeyState(vk) & 0x8000) != 0;

    private static IEnumerable<int> Range(int from, int to)
        => Enumerable.Range(from, to - from + 1);
}
