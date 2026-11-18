using System.Numerics;
using ModCloset2.Core;
using ModCloset2.Ui.Canvas;
using ModCloset2.Ui.Widgets;

namespace ModCloset2.OnYou;

/// <summary> The modifier keys and the mouse as the host saw them this frame, before the face decides whether to show. </summary>
/// <param name="MouseDown"> A mouse button is held (pointing does not start in the middle of the game's own drag). </param>
/// <param name="Typing"> Some text field has the keyboard (the key is then a letter, not a wish to point). </param>
/// <param name="OtherKey"> Some other key (not Alt, Ctrl or Shift) is down: the modifier is part of a shortcut (Alt+1). </param>
public readonly record struct HeldKeys(bool Alt, bool Ctrl, bool Shift, bool MouseDown = false, bool Typing = false, bool OtherKey = false);

/// <summary> The keys the face acts on right now (the host hides exactly these from the game). </summary>
[Flags]
public enum OnYouKeys : byte
{
    None = 0,

    /// <summary> Enter: keep (the chooser is open). </summary>
    Enter = 1,

    /// <summary> Esc: put back and close the chooser; with On you staying on and nothing open, stop. </summary>
    Escape = 2,

    /// <summary> The four arrows: flip through the shelf (the chooser is open). </summary>
    Arrows = 4,
}

public enum OnYouMode : byte
{
    /// <summary> Nothing shows; the game has the mouse. </summary>
    Off,

    /// <summary> The places on the character show as rings; the one under the mouse glows. </summary>
    Pointing,

    /// <summary> The chooser is open beside one place (the rings stay, a click on another place moves it there). </summary>
    Open,
}

/// <summary> What the last frame drew, for the tests. </summary>
public sealed class OnYouStats
{
    public bool Drawn { get; set; }

    /// <summary> Places drawn as quiet rings, in drawing order. </summary>
    public List<string> Rings { get; } = new();

    /// <summary> The place under the mouse (null: none). </summary>
    public string? Lit { get; set; }

    /// <summary> Where the place under the mouse leads ("Hair · Headwear · Ears"). </summary>
    public string Label { get; set; } = string.Empty;
    public bool ChooserRight { get; set; }
    public bool BusyShown { get; set; }

    /// <summary> The hint at the bottom while pointing ("" when not shown). </summary>
    public string Pill { get; set; } = string.Empty;

    /// <summary> The hint stands out (the first seconds after On you was switched on from the window). </summary>
    public bool PillStrong { get; set; }

    /// <summary> Names in the fan, top to bottom ("" where a place in the fan is empty). </summary>
    public List<string> Fan { get; } = new();

    /// <summary> Cards drawn without their photo this frame (no picture, or the mod's own), asked of the closet with WantPicture (their keys). </summary>
    public List<string> PicturesWanted { get; } = new();

    /// <summary> Cards whose picture is fading in this frame (key → how far, 0 to 1). </summary>
    public Dictionary<string, float> PicturesFading { get; } = new(StringComparer.Ordinal);

    /// <summary> Where things were drawn: "ring/…", "glow/…", "fan/-2…2", "middle", "tab/…", "all", "text", "chooser", "label", "pill". </summary>
    public Dictionary<string, Rect> Places { get; } = new(StringComparer.Ordinal);
}

/// <summary>
/// Face C, "On you": no window, the user points at their own character. While the On-you key is held (or after the
/// window's "On you" switch, until Esc), the places on the character that lead somewhere show as quiet rings; the one
/// under the mouse glows and says where it leads. A click opens the chooser beside it: a fan of five round pictures
/// (the middle one is on the character now), the shelf, the name and key hints, and "All n" for the window on that shelf. The wheel or the arrow keys flip
/// through the shelf's cards in the closet's order, each step trying that card on; a click on the middle picture or
/// Enter keeps, Esc or a right-click puts back.
/// Pure: it draws from an <see cref="IBodyOnScreen"/> and an <see cref="IClosetSession"/> through any canvas, like the
/// window face. The host calls <see cref="Update"/> every frame and, when that says so, shows a full-screen overlay
/// that takes the mouse and calls <see cref="Draw"/> in it.
/// </summary>
public sealed partial class OnYouFace
{
    // Remember keys (documented in docs/reports/onyou.md)
    public const string KeyKey   = "onyou.key";
    /// <summary>
    /// Earlier builds saved "stays on" here. It is never restored any more (a forgotten "stays on" made the mouse seem
    /// broken after the next login); a stale value is cleared once.
    /// </summary>
    public const string KeyOn    = "onyou.on";
    public const string KeyShelf = "onyou.shelf.";

    /// <summary>
    /// The choices for the On-you key (value stored under <see cref="KeyKey"/>, English label for Loc.T). Nothing
    /// remembered means "none": On you is started from the window's switch unless the user picks a key.
    /// </summary>
    public static readonly (string Value, string Label)[] KeyChoices = { ("alt", "Alt"), ("ctrl", "Ctrl"), ("shift", "Shift"), ("none", "None") };

    /// <summary>
    /// Seconds the face waits while the body is gone before it gives up. Every try redraws the character, and the game
    /// has no skeleton for a few frames then; the chooser must not close on every step.
    /// </summary>
    public const double HideGrace = 3.0;

    /// <summary> Seconds the "Esc to stop" hint stands out after On you was switched on from the window. </summary>
    public const double HintStrong = 4.0;

    private readonly Gui _ui = new();
    private IClosetSession _s = null!;
    private ICanvas _c = null!;
    private float _u = 1;

    private bool _loaded;
    private bool _stayOn;
    private bool _held;

    /// <summary> Some text field (ImGui's or the game's chat) is typed in: the face reads no keys and claims none. </summary>
    private bool _typing;
    private double _now;

    /// <summary> The host saw the character leave for good while the overlay was not drawn (see <see cref="BodyGone"/>). </summary>
    private volatile bool _goneSeen;

    /// <summary> When "stays on" began (-1: not yet seen by Update), for the strong hint. </summary>
    private double _stayOnSince = -1;

    /// <summary> After "All 86" the window takes over: pointing starts again only after the key was let go once. </summary>
    private bool _waitForRelease;
    private double _hiddenSince = -1;

    // ---- the chooser ----
    private string _place = string.Empty;
    private string _shelf = string.Empty;

    /// <summary> Where the middle of the fan is in the shelf's cards; -1 is "Nothing". </summary>
    private int _index = -1;

    /// <summary> The card in the middle (followed by key, so the closet's reordering after a Keep does not lose it). </summary>
    private PartKey? _key;

    /// <summary> Where the chooser was put (it stays there while the place sways with the idle animation). </summary>
    private Vector2 _anchor;
    private bool _anchored;
    private bool _right;

    /// <summary> The centre of the fan's arc, and how far the glow reaches towards the fan from it. </summary>
    private Vector2 _arc;
    private float _glowTo;
    private BodySpot? _openSpot;
    private float _wheel;

    // ---- the mouse ----
    private string? _lit;
    private string? _pressed;
    private bool _pressedOnChooser;

    public OnYouMode Mode { get; private set; }

    /// <summary> The overlay shows this frame (pointing or open, and the character is there). </summary>
    public bool Visible { get; private set; }

    /// <summary> The window's "On you" switch was used: pointing stays until Esc (remembered as <see cref="KeyOn"/>). </summary>
    public bool StaysOn
        => _stayOn;

    /// <summary> The face listens to keys while this is true (the chooser is open, or On you stays on). </summary>
    public bool WantsKeyboard
        => Visible && !_typing && (Mode == OnYouMode.Open || _stayOn);

    /// <summary> On you listens to keys now, whatever stops them reaching it (for the host's "why was Esc not acted on" log). </summary>
    public bool Listening
        => Visible && (Mode == OnYouMode.Open || _stayOn);

    /// <summary> Exactly the keys the face acts on now; every other key stays the game's (walking, the camera, chat). </summary>
    public OnYouKeys Wants
        => !Visible || _typing ? OnYouKeys.None
            : Mode == OnYouMode.Open ? OnYouKeys.Enter | OnYouKeys.Escape | OnYouKeys.Arrows
            : _stayOn ? OnYouKeys.Escape : OnYouKeys.None;

    /// <summary> "All 86": the window should open on this shelf. On you ends; what is tried stays tried (the window shows it). </summary>
    public Action<string>? OpenWindow { get; set; }

    /// <summary> The window is open (the host says so each frame). When On you ends while trying and the window is closed, it puts back. </summary>
    public bool WindowOpen { get; set; }

    public OnYouStats Stats { get; } = new();

    public Gui Gui
        => _ui;

    /// <summary> The place the chooser is open on ("" when closed) and its shelf. </summary>
    public string OpenPlace
        => Mode == OnYouMode.Open ? _place : string.Empty;

    public string OpenShelf
        => Mode == OnYouMode.Open ? _shelf : string.Empty;

    private float P(float mockupPixels)
        => mockupPixels * _u;

    // ======== the key and the mode ========

    /// <summary> The chosen On-you key: "none" (the default: no key), "alt", "ctrl" or "shift". </summary>
    public static string KeyChoice(IClosetSession s)
        => s.Remembered(KeyKey) is "alt" or "ctrl" or "shift" ? s.Remembered(KeyKey) : "none";

    /// <summary> The chosen key alone is held (with another modifier it is some other shortcut, not a wish to point). </summary>
    public static bool KeyHeld(string choice, HeldKeys k)
        => choice switch
        {
            "alt"   => k.Alt && !k.Ctrl && !k.Shift,
            "ctrl"  => k.Ctrl && !k.Alt && !k.Shift,
            "shift" => k.Shift && !k.Alt && !k.Ctrl,
            _       => false,
        };

    /// <summary> The host should read the bones this frame (so they are there the moment pointing starts). </summary>
    public bool WantsBody(IClosetSession s, HeldKeys keys)
    {
        Load(s);
        return Mode != OnYouMode.Off || _stayOn || KeyHeld(KeyChoice(s), keys) && !keys.OtherKey;
    }

    private readonly KeyHold _hold = new();

    private void Load(IClosetSession s)
    {
        _s = s;
        if (_loaded)
            return;
        _loaded = true;

        // "stays on" always starts off; only the key choice is remembered
        if (s.Remembered(KeyOn).Length > 0)
            s.Remember(KeyOn, string.Empty);
    }

    /// <summary>
    /// The window's "On you" switch: pointing starts and stays until Esc, or until the character leaves the screen for
    /// good (zoning, logging out, a cutscene, group pose). Not remembered across restarts.
    /// </summary>
    public void Start(IClosetSession s)
    {
        Load(s);
        _stayOn      = true;
        _stayOnSince = -1;
    }

    /// <summary>
    /// Ends On you (Esc while pointing, another face picked). What is tried is put back unless the window is open; a
    /// host that is opening the window passes <paramref name="putBack"/> false (the window then shows what is tried).
    /// </summary>
    public void Stop(IClosetSession s, bool putBack = true)
    {
        Load(s);
        ForgetStayOn();
        End(putBack);
    }

    private void ForgetStayOn()
        => _stayOn = false;

    /// <summary>
    /// Esc while pointing (or the pill's ×), when On you was started from the window's switch: On you ends and the
    /// closet window opens again on the shelf it had. Something still tried stays tried; the window's bar shows it (as
    /// "All n"). Without a host to open the window, it only stops (and puts back, as on closing).
    /// </summary>
    private void BackToCloset()
    {
        ForgetStayOn();
        if (OpenWindow is not { } open)
        {
            End(true);
            return;
        }

        End(false);
        open(_s.Selected);
    }

    /// <summary>
    /// The character left the screen for good (zoning, logged out, a cutscene, group pose). For the host's per-frame path
    /// that runs whether or not the overlay is drawn: Dalamud draws no plugin in a cutscene or group pose, so
    /// <see cref="Update"/> would not hear of it until afterwards. "Stays on" ends at once; the rest (closing the
    /// chooser, putting back when the window is closed) happens at the next <see cref="Update"/>, on the draw side like
    /// every session command.
    /// </summary>
    public void BodyGone()
    {
        _stayOn   = false;
        _goneSeen = true;
    }

    /// <summary>
    /// Once per frame, before drawing: follows the key, the switch and the body. Returns whether the overlay shows this
    /// frame (then the host calls <see cref="Draw"/>; which parts of the screen take the mouse says
    /// <see cref="MouseWantedAt"/>). <paramref name="gone"/>: On you must not be up (<see cref="BodyGone.Reason"/>: the
    /// character left the screen for good, or the player is in combat, in a duty or busy), not just out of the camera's
    /// view; On you then ends, also when it stays on, and the key does nothing.
    /// </summary>
    public bool Update(IClosetSession s, IBodyOnScreen body, HeldKeys keys, double time, bool gone = false)
    {
        Load(s);
        _now    = time;
        _typing = keys.Typing;
        if (_stayOn && _stayOnSince < 0)
            _stayOnSince = time;
        if (_goneSeen)
        {
            _goneSeen = false;
            gone      = true;
        }

        if (gone && (_stayOn || Mode != OnYouMode.Off))
        {
            ForgetStayOn();
            End(true);
        }

        // the key counts only after a short hold alone (a hotbar's Alt+1 never shows anything), and never while busy
        _held = _hold.Update(KeyHeld(KeyChoice(s), keys), keys.OtherKey || keys.MouseDown, time) && !gone;
        if (!_held)
            _waitForRelease = false;
        var want      = _held || _stayOn;
        var available = body.Available && body.Spots.Count > 0;
        switch (Mode)
        {
            case OnYouMode.Off:
                // never in the middle of the game's own drag (the release would never reach the game), never while typing
                if (want && available && !gone && !keys.MouseDown && !keys.Typing && !_waitForRelease)
                    Mode = OnYouMode.Pointing;
                break;
            case OnYouMode.Pointing:
                // the key let go with nothing open: everything is gone at once
                if (!want)
                    End(true);
                break;
            case OnYouMode.Open:
                // the chooser stays when the key is let go: the wheel and a click on the middle work without it
                break;
        }

        if (Mode != OnYouMode.Off && !available)
        {
            if (_hiddenSince < 0)
                _hiddenSince = time;
            else if (time - _hiddenSince > HideGrace)
                End(true);
        }
        else
            _hiddenSince = -1;

        Visible = Mode != OnYouMode.Off && available;
        return Visible;
    }

    private void End(bool putBack)
    {
        Mode     = OnYouMode.Off;
        Visible  = false;
        _place   = string.Empty;
        _lit     = null;
        _pressed = null;
        _anchored = false;

        // the same rule as closing the window: nothing tried may stay on the character without a way to keep or put it back
        if (putBack && !WindowOpen && _s.Trying.Active)
            _s.PutBack();
    }

    /// <summary> The chooser closes (after Keep or Put back); pointing goes on while the key is held or On you stays on. </summary>
    private void CloseChooser()
    {
        _place    = string.Empty;
        _anchored = false;
        Mode      = _held || _stayOn ? OnYouMode.Pointing : OnYouMode.Off;
        if (Mode == OnYouMode.Off)
            Visible = false;
    }

    // ======== opening, flipping, deciding ========

    /// <summary> Shelves without a Nothing: no single place on the body there (as in the window). </summary>
    private static bool HasNothing(string shelf)
        => shelf is not (Pieces.Outfits or Pieces.Other or Pieces.Hidden);

    private int First
        => HasNothing(_shelf) ? -1 : 0;

    private void Open(PlaceInfo place)
    {
        if (place.Shelves.Count == 0)
            return;
        var remembered = _s.Remembered(KeyShelf + BodyPlaces.Base(place.Spot.Id));
        var shelf      = place.Shelves.FirstOrDefault(r => r.Id == remembered) ?? place.Shelves[0];
        OpenOn(place, shelf.Id);
    }

    private void OpenOn(PlaceInfo place, string shelf)
    {
        // the shelf's cards come from CardsOf: the window's selected shelf, search and kind chip stay as they are
        var moved = Mode == OnYouMode.Open && _place != place.Spot.Id;
        Mode     = OnYouMode.Open;
        _place   = place.Spot.Id;
        _shelf   = shelf;
        _wheel   = 0;
        _openSpot = place.Spot;
        if (moved)
            _anchored = false; // a new place: the chooser is put beside it afresh
        Current();
    }

    /// <summary> The middle of the fan when the chooser opens: what is tried here, else what is on, else Nothing. </summary>
    private void Current()
    {
        var cards = Cards;
        _index = -2;
        for (var i = 0; i < cards.Count && _index < -1; ++i)
            if (cards[i].State == CardState.Trying)
                _index = i;
        if (_index < -1 && _s.Trying.Items.Any(t => t.TakingOff && t.Piece == _shelf))
            _index = -1;
        for (var i = 0; i < cards.Count && _index < -1; ++i)
            if (cards[i].State == CardState.On)
                _index = i;
        if (_index < -1)
            _index = First;
        if (_index >= cards.Count)
            _index = cards.Count - 1;
        _key = _index >= 0 && _index < cards.Count ? cards[_index].Key : null;
    }

    /// <summary> Keeps the middle in step with the session: the same card, wherever the closet's order put it now. </summary>
    private void Follow(IReadOnlyList<Card> cards)
    {
        if (_key is { } key)
        {
            for (var i = 0; i < cards.Count; ++i)
                if (cards[i].Key == key)
                {
                    _index = i;
                    return;
                }

            // the card left the shelf (moved, hidden): stay at the same place in the list
            _index = Math.Min(_index, cards.Count - 1);
            if (_index < First)
                _index = First;
            _key = _index >= 0 && _index < cards.Count ? cards[_index].Key : null;
        }
        else if (_index >= 0 || !HasNothing(_shelf))
        {
            _index = cards.Count > 0 ? Math.Clamp(_index, 0, cards.Count - 1) : -1;
            _key   = _index >= 0 ? cards[_index].Key : null;
        }
    }

    /// <summary>
    /// One or more steps through the shelf, trying on only where it lands. The game's redraw takes a moment; the session
    /// makes only the newest try count, so a fast wheel never queues up behind it.
    /// </summary>
    private void Step(int delta)
    {
        var cards = Cards;
        var last  = cards.Count - 1;
        if (last < First)
            return; // an empty shelf without a Nothing: nowhere to go
        var target = Math.Clamp(_index + delta, First, last);
        if (target == _index)
            return;
        _index = target;
        if (target < 0)
        {
            _key = null;
            TryNothingHere(cards);
            return;
        }

        var card = cards[target];
        _key = card.Key;
        // the card that is on: Try puts the kept one back in its place (the session takes out what was tried there)
        if (card.State != CardState.Trying)
            _s.Try(card.Key);
    }

    /// <summary> The open shelf's cards in the closet's order (not the window's selection, search or kind chip). </summary>
    private IReadOnlyList<Card> Cards
        => _s.CardsOf(_shelf);

    /// <summary> "Nothing": take off what is on here; when nothing is on, only stop trying what was tried here. </summary>
    private void TryNothingHere(IReadOnlyList<Card> cards)
    {
        // something of this shelf is on: a card that is on, or one tried in place of a kept one (the rail names it)
        var on = cards.Any(c => c.State == CardState.On) || _s.Rail.Any(r => r.Id == _shelf && r.WornName.Length > 0);
        if (on)
        {
            if (!_s.Trying.Items.Any(t => t.TakingOff && t.Piece == _shelf))
                _s.TryNothing(_shelf);
            return;
        }

        foreach (var card in cards.Where(c => c.State == CardState.Trying).ToList())
            _s.StopTrying(card.Key);
    }

    private void Keep()
    {
        if (_s.Trying.Active)
            _s.Keep();
        CloseChooser();
    }

    private void PutBack()
    {
        if (_s.Trying.Active)
            _s.PutBack();
        CloseChooser();
    }

    private void SwitchShelf(PlaceInfo place, string shelf)
    {
        if (shelf == _shelf)
            return;
        _s.Remember(KeyShelf + BodyPlaces.Base(place.Spot.Id), shelf);
        OpenOn(place, shelf);
    }

    private void ShowAll()
    {
        var shelf = _shelf;
        // the window opens on this shelf: here selecting it is right (flipping never changes the window's shelf)
        _s.Select(shelf);
        ForgetStayOn();
        End(false);
        _waitForRelease = _held;
        OpenWindow?.Invoke(shelf);
    }

    // ======== input ========

    private void Input(List<PlaceInfo> places, PlaceInfo? lit, Layout? layout)
    {
        var inp       = _ui.In;
        var onChooser = layout is not null && layout.Area.Contains(inp.Mouse);

        // left button: a click on a place opens the chooser there (or moves it); a click on nothing closes a chooser
        // that has nothing tried (with something tried, Keep or Put back decide)
        if (inp.Pressed[0])
        {
            _pressedOnChooser = onChooser;
            _pressed          = onChooser ? null : lit?.Spot.Id ?? string.Empty;
        }

        if (inp.Released[0])
        {
            var here = onChooser ? null : lit?.Spot.Id ?? string.Empty;
            if (_pressed is not null && _pressed == here && !_pressedOnChooser)
            {
                if (lit is not null)
                {
                    if (Mode != OnYouMode.Open || lit.Spot.Id != _place)
                        Open(lit);
                }
                else if (Mode == OnYouMode.Open && !_s.Trying.Active)
                    CloseChooser();
            }

            _pressed = null;
        }

        // right button anywhere: put back and close
        if (inp.Pressed[1] && Mode == OnYouMode.Open)
        {
            PutBack();
            return;
        }

        // the wheel: flips the open shelf; over a place with nothing open, opens it there and takes the first step
        // The wheel is On you's only while the chooser is open, and over a place while the key is held (it opens the
        // chooser there and takes the first step); the overlay takes it nowhere else, so everywhere else it zooms the game.
        if (inp.Wheel != 0)
        {
            if (Mode != OnYouMode.Open && lit is not null && !onChooser && _held)
                Open(lit);
            if (Mode == OnYouMode.Open)
            {
                _wheel += inp.Wheel;
                var steps = (int)_wheel;
                _wheel -= steps;
                if (steps != 0)
                    Step(-steps); // the wheel towards you is the next card, like scrolling down a list
            }
        }

        // typing in the game's chat (or any text field): Enter, Esc and the arrows are the text's, not On you's
        if (_typing)
            return;

        if (_ui.Key(Key.Escape))
        {
            // Esc steps back one level: the chooser closes (putting back); pointing that came from the window goes back to it
            if (Mode == OnYouMode.Open)
                PutBack();
            else if (_stayOn)
                BackToCloset();
            return;
        }

        if (Mode != OnYouMode.Open)
            return;
        if (_ui.Key(Key.Enter))
        {
            Keep();
            return;
        }

        var step = _ui.Key(Key.Down) || _ui.Key(Key.Right) ? 1 : _ui.Key(Key.Up) || _ui.Key(Key.Left) ? -1 : 0;
        if (step != 0)
            Step(step);
    }

    // ======== places ========

    /// <summary>
    /// Whether the overlay takes the mouse at this point (the host makes the overlay see-through for the mouse
    /// elsewhere): only over a place of the character, the chooser or the hint pill (it holds the ×), whether the key
    /// is held or On you stays on.
    /// The game keeps its clicks, targeting, hotbars and zoom everywhere else. Uses what the last frame drew.
    /// </summary>
    public bool MouseWantedAt(Vector2 p)
    {
        if (!Visible)
            return false;
        if (_area is { } a && a.Contains(p) || _pillArea is { } pill && pill.Contains(p))
            return true;
        foreach (var place in _places)
        {
            var r = MathF.Max(place.Spot.Radius, P(6));
            if (Geometry.DistanceToLine(p, place.Spot.Line) <= r + P(8) + r * 0.2f)
                return true;
        }

        return false;
    }

    /// <summary> A place that leads somewhere, with what the face worked out about it this frame. </summary>
    private sealed class PlaceInfo
    {
        public required BodySpot Spot { get; init; }
        public required List<RailRow> Shelves { get; init; }
        public Vector2 Middle { get; init; }
        public Ellipse Glow { get; init; }
    }

    private List<PlaceInfo> Places(IBodyOnScreen body, IReadOnlyList<RailRow> rail)
    {
        var result = new List<PlaceInfo>();
        foreach (var spot in body.Spots)
        {
            if (spot.Line.Count == 0)
                continue;
            // shelves the closet does not show (no parts) are left out; a place that leads nowhere is not a place
            var shelves = new List<RailRow>();
            foreach (var id in BodySpots.Shelves(spot.Id))
                if (rail.FirstOrDefault(r => r.Id == id) is { } row)
                    shelves.Add(row);
            if (shelves.Count == 0)
                continue;
            var r = MathF.Max(spot.Radius, P(3));
            result.Add(new PlaceInfo
            {
                Spot    = spot,
                Shelves = shelves,
                Middle  = Geometry.Middle(spot.Line),
                Glow    = Geometry.Around(spot.Line, r, MathF.Max(P(6), r * 0.3f), spot.Id == BodySpots.Tail),
            });
        }

        return result;
    }

    /// <summary>
    /// The place under the mouse. Where the mouse is inside the outlines of several places, the one nearest the camera
    /// wins (a hand in front of the chest; the chest over a hand behind the body, which then cannot steal the click).
    /// Without depths, or near no outline, the nearest place within reach wins, measured against each place's own
    /// thickness. The one lit last frame is held a little longer, so the light does not flicker between neighbours.
    /// </summary>
    private PlaceInfo? Pick(List<PlaceInfo> places, Vector2 mouse)
    {
        PlaceInfo? best      = null;
        var        bestScore = float.MaxValue;
        PlaceInfo? front     = null;
        foreach (var p in places)
        {
            var r = MathF.Max(p.Spot.Radius, P(6));
            var d = Geometry.DistanceToLine(mouse, p.Spot.Line);
            if (d > r + P(8) + r * 0.2f)
                continue;
            var score = d / r;
            if (p.Spot.Id == _lit)
                score *= 0.85f;
            if (score < bestScore || score == bestScore && best is not null && p.Spot.Radius < best.Spot.Radius)
            {
                best      = p;
                bestScore = score;
            }

            // inside the outline: a candidate for "in front"
            if (d <= r && p.Spot.Depth > 0 && (front is null || Nearer(p, front)))
                front = p;
        }

        return front is not null && best is not null && best != front && best.Spot.Depth > 0 && Nearer(front, best) ? front : best;
    }

    /// <summary> Clearly nearer the camera (by more than 2 %, so two places at one depth are told apart by the mouse instead). </summary>
    private bool Nearer(PlaceInfo a, PlaceInfo b)
    {
        // the lit one keeps its place a little longer here too
        var da = a.Spot.Depth * (a.Spot.Id == _lit ? 0.99f : 1f);
        return da < b.Spot.Depth * 0.98f;
    }
}
