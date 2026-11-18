using System.Globalization;
using System.Numerics;
using ModCloset2.Core;
using ModCloset2.Ui.Canvas;
using ModCloset2.Ui.Widgets;

namespace ModCloset2.Ui.Face;

/// <summary> Where the window is and whether the user is busy moving or sizing it (the host fills this in). </summary>
/// <param name="Pos"> The window's top-left on screen. </param>
/// <param name="Size"> The window's full size. </param>
/// <param name="Settled"> No mouse button is held, so a size or place seen now is one the user chose. </param>
public readonly record struct WindowPlace(Vector2 Pos, Vector2 Size, bool Settled);

/// <summary> The face asks the host to move and size the window (after a click on the face switch). </summary>
public readonly record struct WindowRequest(Vector2 Pos, Vector2 Size);

/// <summary> What the last frame drew, for tests and for the performance check. </summary>
public sealed class FaceStats
{
    public int CardsDrawn { get; set; }
    public int FirstCard { get; set; } = -1;
    public int LastCard { get; set; } = -1;
    public int Columns { get; set; }
    public float CardWidth { get; set; }
    public float PictureHeight { get; set; }
    public int RailRowsDrawn { get; set; }
    public Rect Grid { get; set; }

    /// <summary> What the last frame said to the session about the mouse over the cards. </summary>
    public bool GridHovered { get; set; }
    public bool TakingOff { get; set; }
    public bool BusyShown { get; set; }
    public int UnsureMarks { get; set; }
    public bool ToastShown { get; set; }
    public int SortRowsDrawn { get; set; }

    /// <summary> Parts whose picture this frame asked for (<see cref="IClosetSession.WantPicture"/>). </summary>
    public HashSet<PartKey> PicturesWanted { get; } = new();

    /// <summary> How often the To sort list was measured (once per list, width and scale; not per frame). </summary>
    public int SortLayouts { get; set; }
    public Rect Rail { get; set; }
    public Dictionary<string, Rect> Places { get; } = new(StringComparer.Ordinal);
}

/// <summary>
/// The closet window, wide (face A) or slim (face B), drawn from an <see cref="IClosetSession"/> onto any canvas.
/// Wide when the content is at least <see cref="SlimBelow"/> U wide. It never talks to the game: it reads the
/// session's lists and calls its commands. The host (the game window or the picture tests) gives it a canvas and the
/// window's place each frame, and applies <see cref="Request"/> when the face switch asks for another size.
/// </summary>
public sealed partial class WindowFace
{
    /// <summary> Narrower than this (in U) the window turns slim by itself. </summary>
    public const float SlimBelow = 720;

    /// <summary> Default sizes in U for a face that has no remembered size yet (the mockups' sizes). </summary>
    public static readonly Vector2 WideDefault = new(1240, 860);
    public static readonly Vector2 SlimDefault = new(380, 860);

    // Remember keys (documented in docs/reports/window.md)
    public const string KeyFace = "window.face";
    public const string KeyWide = "window.wide";
    public const string KeySlim = "window.slim";

    /// <summary>
    /// The key that starts On you: "alt", "ctrl", "shift" or "none"; nothing remembered means "none" (off by default:
    /// Alt is a common hotbar modifier).
    /// </summary>
    public const string KeyOnYou = "onyou.key";

    /// <summary> The On you key a remembered value stands for: nothing remembered is "none". </summary>
    public static string OnYouKeyIn(string remembered)
        => remembered.Length == 0 ? "none" : remembered;

    /// <summary> The choices for the On you key, as remembered under <see cref="KeyOnYou"/>, with their labels. </summary>
    private static (string Value, string Label)[] OnYouKeys()
        => new[] { ("alt", Loc.T("Alt")), ("ctrl", Loc.T("Ctrl")), ("shift", Loc.T("Shift")), ("none", Loc.T("No key")) };

    private const string PopCard     = "card";
    private const string PopSlots    = "slots";
    private const string PopOwnMod   = "own-mod";
    private const string PopOption   = "option";
    private const string PopMore     = "options-more";
    private const string PopSettings = "settings";
    private const string PopWhy      = "why";
    private const string PopNewShelf = "new-shelf";
    private const string PopOther    = "sort-other";
    private const string PopKinds    = "kinds";

    private readonly Gui _ui = new();
    private IClosetSession _s = null!;
    private ICanvas _c = null!;
    private float _u = 1;

    private string _lastSelected = string.Empty;
    private string _lastSearch = string.Empty;
    private int _nav = -1;
    private bool _slimSearch;
    private bool _focusSearch;
    private string _newShelfName = string.Empty;
    private PartKey? _newShelfFor;
    private bool _focusNewShelf;
    private WindowPlace _lastPlace;
    private WindowPlace _placeBeforeHold;
    private bool _wasSettled = true;
    private bool _first = true;

    public Gui Gui
        => _ui;

    public bool IsSlim { get; private set; }

    /// <summary> Set when the face wants the window moved or resized; the host applies it and calls <see cref="RequestDone"/>. </summary>
    public WindowRequest? Request { get; private set; }

    public FaceStats Stats { get; } = new();

    /// <summary> Rounding of the window's bottom corners in pixels, so backgrounds drawn there follow them. </summary>
    public float BottomRounding { get; set; }

    /// <summary>
    /// The keys the closet would act on in the next frame, so the host can keep exactly these from the game while the
    /// mouse is over the window (and no others): Enter while trying; Esc while trying, a popup, a drag or the slim
    /// search is open; the arrows while there are cards to step through; Z (with Ctrl) while there is something to undo.
    /// </summary>
    public GameKeys Wants { get; private set; }

    private GameKeys KeysWanted()
    {
        var t     = _s.Trying;
        var wants = GameKeys.None;
        if (t.Active)
            wants |= GameKeys.Enter | GameKeys.Escape;
        if (_ui.PopupId.Length > 0 || _ui.Drag is not null || _slimSearch)
            wants |= GameKeys.Escape;
        if ((_view == View.Shelf && _s.Selected != Pieces.ToSort || Searching) && _s.Scan.Ready && _s.Cards.Count > 0)
            wants |= GameKeys.Arrows;
        if (CanUndoNow)
            wants |= GameKeys.UndoZ;
        return wants;
    }

    /// <summary> The host can start On you (it gives the window a way to): the third segment of the face switch is live. </summary>
    public bool OnYouAvailable { get; set; }

    /// <summary> The user clicked "On you"; the host closes the window without putting back and starts it, then calls <see cref="OnYouDone"/>. </summary>
    public bool WantsOnYou { get; private set; }

    public void OnYouDone()
        => WantsOnYou = false;

    public void RequestDone()
        => Request = null;

    private float P(float mockupPixels)
        => mockupPixels * _u;

    /// <summary> Draws one frame into the canvas's bounds. Never throws for a session that misbehaves: draws what it can. </summary>
    public void Draw(ICanvas canvas, IClosetSession session, WindowPlace place)
    {
        _c = canvas;
        _s = session;
        _u = canvas.U;
        _ui.Begin(canvas);
        Stats.CardsDrawn    = 0;
        Stats.RailRowsDrawn = 0;
        Stats.FirstCard     = -1;
        Stats.LastCard      = -1;
        Stats.Places.Clear();
        Stats.GridHovered = false;
        Stats.TakingOff   = false;
        Stats.BusyShown   = false;
        Stats.UnsureMarks = 0;
        Stats.ToastShown  = false;
        Stats.SortRowsDrawn = 0;
        Stats.PicturesWanted.Clear();
        _pictureFrame++;

        var b = canvas.Bounds;
        TrackPlace(place, b.W < P(SlimBelow));
        canvas.FillRect(b, Theme.Window, BottomRounding, Corners.Bottom);

        // a feature that went away takes its view with it
        if (_view == View.Colours && _s.Colours is null || _view == View.Looks && _s.Looks is null)
            _view = View.Shelf;
        if (_renaming is not null && _view != View.Looks)
            _renaming = null;

        if (_s.Selected != _lastSelected || _s.Search != _lastSearch)
        {
            _ui.SetScroll("grid", 0);
            _ui.SetScroll("sort", 0);
            _nav          = -1;
            _lastSelected = _s.Selected;
            _lastSearch   = _s.Search;
        }

        if (IsSlim)
            DrawSlim(b);
        else
            DrawWide(b);

        DrawPopups();
        DrawDragGhost();
        Keys();

        // every frame: is the mouse over the cards (or the To sort list)? While it is, nothing jumps shelves there
        _s.GridHovered(Stats.GridHovered);
        PointingDone();
        ForgetPictureSlots();
        Wants = KeysWanted();
        _ui.End();
        _first = false;
    }

    // ---- faces ----

    private void DrawWide(Rect b)
    {
        var top = new Rect(b.X, b.Y, b.W, P(56));
        TopBarWide(top);
        var body  = new Rect(b.X, top.Bottom, b.W, b.H - top.H);
        var rail  = new Rect(body.X, body.Y, P(284), body.H);
        var right = new Rect(rail.Right, body.Y, body.W - rail.W, body.H);
        Rail(rail);
        RightSide(right);
    }

    private void DrawSlim(Rect b)
    {
        var top = new Rect(b.X, b.Y, b.W, P(52));
        TopBarSlim(top);
        var y = top.Bottom;
        if (!_s.Scan.Ready || _s.Scan.Problem.Length > 0)
        {
            Waiting(new Rect(b.X, y, b.W, b.Bottom - y));
            return;
        }

        y = IconGrid(new Rect(b.X, y, b.W, b.Bottom - y));
        ShelfSlim(new Rect(b.X, y, b.W, b.Bottom - y));
    }

    // ---- remembering sizes and switching faces ----

    private void TrackPlace(WindowPlace place, bool slim)
    {
        if (_first)
        {
            IsSlim           = slim;
            _lastPlace       = place;
            _placeBeforeHold = place;
        }

        // the moment a button goes down the user may start sizing: keep the place from just before
        if (_wasSettled && !place.Settled)
            _placeBeforeHold = _lastPlace;

        if (slim != IsSlim)
        {
            // dragged across the threshold: the face being left keeps the size it had before the drag began
            var before = place.Settled ? _lastPlace : _placeBeforeHold;
            Remember(IsSlim ? KeySlim : KeyWide, before);
            IsSlim = slim;
            _s.Remember(KeyFace, slim ? "slim" : "wide");
            _ui.ClosePopup();
        }
        else if (place.Settled && !_first)
        {
            Remember(IsSlim ? KeySlim : KeyWide, place);
        }

        _wasSettled = place.Settled;
        _lastPlace  = place;
    }

    private void Remember(string key, WindowPlace place)
    {
        if (place.Size.X <= 0 || place.Size.Y <= 0)
            return;
        var text = string.Create(CultureInfo.InvariantCulture, $"{place.Pos.X:0},{place.Pos.Y:0},{place.Size.X:0},{place.Size.Y:0}");
        if (_s.Remembered(key) != text)
            _s.Remember(key, text);
    }

    /// <summary> Reads a remembered "x,y,w,h" (null when there is none or it does not read). </summary>
    public static (Vector2 Pos, Vector2 Size)? ParsePlace(string text)
    {
        var parts = text.Split(',');
        if (parts.Length != 4)
            return null;
        var v = new float[4];
        for (var i = 0; i < 4; ++i)
            if (!float.TryParse(parts[i], NumberStyles.Float, CultureInfo.InvariantCulture, out v[i]))
                return null;
        if (v[2] < 100 || v[3] < 100)
            return null;
        return (new Vector2(v[0], v[1]), new Vector2(v[2], v[3]));
    }

    /// <summary> The face switch: remember this face's place, then ask for the other face's place. </summary>
    private void SwitchTo(bool slim)
    {
        if (slim == IsSlim)
            return;
        Remember(IsSlim ? KeySlim : KeyWide, _lastPlace);
        var key  = slim ? KeySlim : KeyWide;
        var size = (slim ? SlimDefault : WideDefault) * _u;
        var pos  = _lastPlace.Pos;
        if (ParsePlace(_s.Remembered(key)) is { } known)
        {
            pos  = known.Pos;
            size = known.Size;
        }
        else
        {
            // no size yet: keep the window's top-right corner where it is
            size.Y = _lastPlace.Size.Y;
            pos.X  = _lastPlace.Pos.X + _lastPlace.Size.X - size.X;
        }

        // a slim size must stay slim and a wide size wide, or the face would flip straight back
        var chrome = _lastPlace.Size.X - _c.Bounds.W;
        if (slim)
            size.X = Math.Min(size.X, P(SlimBelow) + chrome - 8 * _u);
        else
            size.X = Math.Max(size.X, P(SlimBelow) + chrome + 8 * _u);

        Request = new WindowRequest(pos, size);
        _s.Remember(KeyFace, slim ? "slim" : "wide");
    }

    // ---- top bar ----

    private void TopBarWide(Rect bar)
    {
        _c.FillRect(new Rect(bar.X, bar.Bottom - 1, bar.W, 1), Theme.Line);
        var cy    = bar.Center.Y;
        var right = bar.Right - P(16);
        var sw    = FaceSwitch(new Rect(right - P(100), cy - P(18), P(100), P(36)), 30);
        right = sw.X - P(14);
        var settings = new Rect(right - P(36), cy - P(18), P(36), P(36));
        var undo     = new Rect(settings.X - P(10) - P(36), settings.Y, P(36), P(36));
        SettingsButton(settings);
        UndoButton(undo);

        var title  = Loc.T("Mod Closet");
        var titleW = _c.Measure(title, Theme.Title).X;
        var room   = undo.X - P(10) - (bar.X + P(20) + titleW + P(24));
        var searchW = Math.Clamp(room, P(150), P(340));
        var search  = new Rect(undo.X - P(10) - searchW, cy - P(18), searchW, P(36));
        SearchField(search);
        TitleText(new Rect(bar.X + P(20), bar.Y, search.X - P(16) - bar.X - P(20), bar.H), Theme.Title);
    }

    private void TopBarSlim(Rect bar)
    {
        _c.FillRect(new Rect(bar.X, bar.Bottom - 1, bar.W, 1), Theme.Line);
        var cy    = bar.Center.Y;
        var right = bar.Right - P(12);
        var sw    = FaceSwitch(new Rect(right - P(84), cy - P(17), P(84), P(34)), 25);
        right = sw.X - P(8);
        var settings = new Rect(right - P(34), cy - P(17), P(34), P(34));
        var undo     = new Rect(settings.X - P(6) - P(34), settings.Y, P(34), P(34));
        SettingsButton(settings);
        UndoButton(undo);
        var left = bar.X + P(16);
        if (_slimSearch || _s.Search.Length > 0)
        {
            // search takes the title's place until it is closed and empty
            var field = new Rect(bar.X + P(12), cy - P(17), undo.X - P(6) - bar.X - P(12), P(34));
            SearchField(field, closable: true);
            return;
        }

        var search = new Rect(undo.X - P(6) - P(34), settings.Y, P(34), P(34));
        Stats.Places["search-open"] = search;
        if (Painter.IconButton(_ui, "search-open", search, Icons.Search, Loc.T("Search all your mods")).Clicked)
        {
            _slimSearch  = true;
            _focusSearch = true;
        }

        TitleText(new Rect(left, bar.Y, search.X - P(8) - left, bar.H), Theme.Title.Sized(22));
    }

    /// <summary> The window's name in the display font; shrinks a little (then cuts) when the bar is crowded. </summary>
    private void TitleText(Rect box, TextStyle style)
    {
        var text = Loc.T("Mod Closet");
        while (style.Size > 16 && _c.Measure(text, style).X > box.W)
            style = style.Sized(style.Size - 1);
        Painter.TextIn(_c, box, text, style, Theme.Accent);
    }

    private void SearchField(Rect r, bool closable = false)
    {
        Stats.Places["search"] = r;
        var hover = _ui.MouseOver(r);
        _c.FillRect(r, hover ? Colr.Hex(0x2B2133) : Theme.Card, P(9));
        Painter.Icon(_c, Icons.Search, new Vector2(r.X + P(12) + P(8), r.Center.Y), P(16), Theme.Muted, 1.8f);
        var text     = _s.Search;
        var showX    = closable || text.Length > 0;
        var fieldBox = new Rect(r.X + P(12 + 16 + 8), r.Y, r.W - P(12 + 16 + 8 + 10) - (showX ? P(24) : 0), r.H);
        var changed = _c.TextField("search", fieldBox, ref text, Loc.T("Search all your mods"), Theme.Body, Theme.Text, Theme.Muted, _focusSearch, !_ui.Blocked, out var active);
        _focusSearch = false;
        if (active)
            _c.StrokeRect(r, Theme.Accent.Fade(0.55f), P(9), Math.Max(1, _u));
        if (changed)
            _s.Search = text;
        if (!showX)
            return;

        var x = new Rect(r.Right - P(30), r.Center.Y - P(11), P(22), P(22));
        var resp = _ui.Interact("search-clear", x);
        if (resp.Hovered)
            _c.FillRect(x, Theme.Picked, P(6));
        Painter.Icon(_c, Icons.Close, x.Center, P(14), resp.Hovered ? Theme.Text : Theme.Muted, 2);
        _ui.Tip("search-clear", x, Loc.T("Clear the search"));
        if (resp.Clicked)
        {
            _s.Search   = string.Empty;
            _slimSearch = false;
        }
    }

    private bool CanUndoNow
        => _s.CanUndo && !_s.Trying.Busy;

    private void UndoButton(Rect r)
    {
        Stats.Places["undo"] = r;
        // a Keep, Put back or Undo still waiting for the game: nothing to undo until it is done
        if (Painter.IconButton(_ui, "undo", r, Icons.Undo, Loc.T("Undo the last change"), CanUndoNow, false, "Ctrl", "Z").Clicked)
            _s.Undo();
    }

    private void SettingsButton(Rect r)
    {
        Stats.Places["settings"] = r;
        if (Painter.IconButton(_ui, "settings", r, Icons.Settings, Loc.T("Settings"), true, _ui.IsOpen(PopSettings)).Clicked)
        {
            if (_ui.IsOpen(PopSettings))
                _ui.ClosePopup();
            else
                _ui.OpenPopup(PopSettings, new Vector2(r.Right, r.Bottom + P(6)));
        }
    }

    /// <summary> Three small segments: the window, slim, and "On you" (not there yet). Returns its box. </summary>
    private Rect FaceSwitch(Rect r, float seg)
    {
        _c.FillRect(r, Theme.Card, P(9));
        var pad = (r.W - 3 * P(seg)) / 4;
        (string Id, string Icon, string Tip, bool Picked, bool Enabled)[] parts =
        {
            ("face-wide", Icons.FaceWide, Loc.T("The window"), !IsSlim, true),
            ("face-slim", Icons.FaceSlim, Loc.T("Slim"), IsSlim, true),
            ("face-onyou", Icons.FaceOnYou, OnYouAvailable ? Loc.T("On you") : Loc.T("On you: coming soon"), false, OnYouAvailable),
        };
        for (var i = 0; i < parts.Length; ++i)
        {
            var (id, icon, tip, picked, enabled) = parts[i];
            var box  = new Rect(r.X + pad + i * (P(seg) + pad), r.Center.Y - (r.H - 2 * pad) / 2, P(seg), r.H - 2 * pad);
            Stats.Places[id] = box;
            var resp = _ui.Interact(id, box, enabled && !picked);
            if (picked)
                _c.FillRect(box, Theme.Picked, P(7));
            else if (resp.Hovered && enabled)
                _c.FillRect(box, Colr.Hex(0x2E2338), P(7));
            var fg = picked ? Theme.Accent : !enabled ? Theme.Muted.Fade(0.45f) : resp.Hovered ? Theme.Text : Theme.Muted;
            Painter.Icon(_c, icon, box.Center, P(16), fg, 1.8f);
            _ui.Tip(id, box, tip);
            if (resp.Clicked && enabled && !picked)
            {
                if (id == "face-onyou")
                    WantsOnYou = true;
                else
                    SwitchTo(id == "face-slim");
            }
        }

        return r;
    }

    // ---- keys ----

    private void Keys()
    {
        var trying = _s.Trying;
        if (_ui.Key(Key.Escape))
        {
            if (_ui.Drag is not null)
                return;
            if (trying.Active)
                _s.PutBack();
            else if (_slimSearch && _s.Search.Length == 0)
                _slimSearch = false;
        }

        if (_ui.Key(Key.Enter) && trying.Active)
            _s.Keep();
        if (_ui.In.Ctrl && _ui.Key(Key.Z) && CanUndoNow)
            _s.Undo();

        var step = _ui.Key(Key.Left) ? -1 : _ui.Key(Key.Right) ? 1 : _ui.Key(Key.Up) ? -Math.Max(1, Stats.Columns) : _ui.Key(Key.Down) ? Math.Max(1, Stats.Columns) : 0;
        if (step != 0)
            Step(step);
    }

    /// <summary> Arrow keys: move to the next card and try it on (flip through like hangers). </summary>
    private void Step(int step)
    {
        if ((_s.Selected == Pieces.ToSort || _view != View.Shelf) && _s.Search.Length == 0)
            return;
        var cards = _s.Cards;
        if (cards.Count == 0)
            return;
        if (_nav < 0 || _nav >= cards.Count)
        {
            _nav = -1;
            for (var i = 0; i < cards.Count && _nav < 0; ++i)
                if (cards[i].State == CardState.Trying)
                    _nav = i;
            if (_nav < 0)
            {
                _nav = step > 0 ? 0 : cards.Count - 1;
                step = 0;
            }
        }

        _nav      = Math.Clamp(_nav + step, 0, cards.Count - 1);
        _navShown = true;
        var card = cards[_nav];
        if (card.State == CardState.None)
            _s.Try(card.Key);
        ScrollToCard(_nav);
    }
}
