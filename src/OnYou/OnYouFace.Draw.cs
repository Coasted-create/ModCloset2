using System.Globalization;
using System.Numerics;
using System.Text;
using ModCloset2.Core;
using ModCloset2.Ui;
using ModCloset2.Ui.Canvas;
using ModCloset2.Ui.Widgets;

namespace ModCloset2.OnYou;

public sealed partial class OnYouFace
{
    // ---- look (docs/mockups/OnYou.dc.html; sizes in mockup pixels) ----

    /// <summary> The hint pill and the label's fill (mockup #221A29). </summary>
    private static readonly Colr PillFill = Colr.Hex(0x221A29);

    /// <summary> The quiet rings' middle (the mockup's backdrop, #120D16). </summary>
    private static readonly Colr RingFill = Colr.Hex(0x120D16);

    /// <summary> Behind the chooser's words, so they read on any part of the game (the mockup's backdrop, see-through). </summary>
    private static readonly Colr Scrim = Colr.Hex(0x120D16, 0.075f);

    private List<PlaceInfo> _places = new();

    /// <summary> The chooser's area as last drawn (null when closed), for <see cref="MouseWantedAt"/>. </summary>
    private Rect? _area;

    /// <summary> The hint pill as last drawn (null when not shown): it holds the way out (×), so it takes the mouse too. </summary>
    private Rect? _pillArea;

    private static readonly TextStyle NameStyle = new(FontFace.Display, 26, Weight.Bold, 0.02f);
    private static readonly TextStyle CapStyle  = new(FontFace.Body, 11);
    private static readonly TextStyle TabStyle  = new(FontFace.Body, 12);

    /// <summary> The fan: angle from the middle (degrees) and diameter of each of the five pictures, top to bottom. </summary>
    private static readonly (float Angle, float Size)[] FanSlots = { (-51, 48), (-25.4f, 56), (0, 84), (25.4f, 56), (51, 48) };

    /// <summary> Mockup: the fan's arc lies 115 beyond the glow's edge; the words start 24 after the middle picture. </summary>
    private const float FanReach = 115;

    /// <summary> The fan's arc is never tighter than this: below it the pictures would touch (mockup: 187). </summary>
    private const float MinReach = 170;
    private const float TextGap  = 24;
    private const float MinText  = 164;
    private const float MaxText  = 300;

    /// <summary> Draws one frame of the overlay and takes its input (the host calls it only when <see cref="Update"/> said so, and catches and logs anything a misbehaving session throws). </summary>
    public void Draw(ICanvas canvas, IClosetSession s, IBodyOnScreen body)
    {
        _c = canvas;
        _s = s;
        _u = canvas.U;
        _ui.Begin(canvas);
        Stats.Drawn = false;
        Stats.Rings.Clear();
        Stats.Lit       = null;
        Stats.Label     = string.Empty;
        Stats.BusyShown = false;
        Stats.Pill      = string.Empty;
        Stats.PillStrong = false;
        Stats.Fan.Clear();
        Stats.Places.Clear();
        Stats.PicturesWanted.Clear();
        Stats.PicturesFading.Clear();

        if (!Visible || !body.Available)
        {
            _places   = new List<PlaceInfo>();
            _area     = null;
            _pillArea = null;
            _ui.End();
            return;
        }

        Stats.Drawn = true;
        var places = _places = Places(body, s.Rail);
        var (bl, bt, br, bb) = body.Box;
        var box = new Rect(bl, bt, Math.Max(1, br - bl), Math.Max(1, bb - bt));

        PlaceInfo? open = null;
        Layout? layout  = null;
        if (Mode == OnYouMode.Open)
        {
            open = places.FirstOrDefault(p => p.Spot.Id == _place);
            if (open is not null)
                _openSpot = open.Spot;
            Follow(Cards);
            layout = Arrange(open, box, places);
        }

        _area = layout?.Area;
        var onChooser = layout is not null && layout.Area.Contains(_ui.Mouse) || _pillArea is { } pa && pa.Contains(_ui.Mouse);
        var lit       = onChooser ? null : Pick(places, _ui.Mouse);
        _lit      = lit?.Spot.Id;
        Stats.Lit = _lit;

        Rings(places, lit, open, box);
        if (open is not null)
            Glow(open, 1f);
        if (lit is not null && lit != open)
            Glow(lit, Mode == OnYouMode.Open ? 0.7f : 1f);
        if (layout is not null)
            Chooser(layout, open, places);
        if (lit is not null && lit != open)
            Label(lit, box);
        _pillArea = null;
        if (Mode == OnYouMode.Pointing)
            Pill(box);
        if (lit is not null)
            _c.SetCursor(CursorShape.Hand);

        Input(places, lit, layout);

        // while the chooser is open on the window's selected shelf, that shelf is being looked at: nothing jumps shelves
        // under the flipping (another shelf cannot be held this way; the fan follows its card by key anyway)
        if (Mode == OnYouMode.Open && s.Selected == _shelf)
            s.GridHovered(true);
        _ui.End();
    }

    // ======== rings and glow ========

    /// <summary> Which ring goes first when two would lie on top of each other (a small far-away character). </summary>
    private static readonly string[] RingOrder =
    {
        BodySpots.HeadTop, BodySpots.Face, BodySpots.Chest, BodySpots.Hips, BodySpots.HandLeft, BodySpots.HandRight, BodySpots.LegLeft,
        BodySpots.LegRight, BodySpots.FootLeft, BodySpots.FootRight, BodySpots.Tail, BodySpots.Neck, BodySpots.EarLeft, BodySpots.EarRight,
    };

    /// <summary>
    /// The quiet rings: one per place, and for the two hands (feet, legs, ears) only one, on the side nearer the camera,
    /// as in the mockup; the other side still lights up under the mouse. A ring that would touch another is left out.
    /// </summary>
    private void Rings(List<PlaceInfo> places, PlaceInfo? lit, PlaceInfo? open, Rect box)
    {
        // a small far-away character gets smaller rings (the mockup's 5 on a 720 pixel tall figure)
        var size  = P(5) * Math.Clamp(box.H / P(600), 0.7f, 1f);
        var drawn = new List<Vector2>();
        foreach (var p in places.OrderBy(p => Array.IndexOf(RingOrder, p.Spot.Id) is var i && i >= 0 ? i : 99))
        {
            if (BodyPlaces.Twin(p.Spot.Id) is { } twinId && places.FirstOrDefault(o => o.Spot.Id == twinId) is { } twin)
            {
                // the side nearer the camera (by its depth, else by being drawn larger); when both are alike, the one
                // further left on screen (the mockup's hand and leg; a tail usually swings out on the other side)
                var known = p.Spot.Depth > 0 && twin.Spot.Depth > 0;
                var mine  = known ? 1 / p.Spot.Depth : p.Spot.Radius;
                var other = known ? 1 / twin.Spot.Depth : twin.Spot.Radius;
                var keep  = Math.Abs(mine - other) > 0.05f * Math.Max(mine, other) ? mine > other : p.Middle.X < twin.Middle.X;
                if (!keep)
                    continue;
            }

            if (p == lit || p == open)
                continue;
            if (drawn.Any(d => Vector2.Distance(d, p.Middle) < Math.Max(size * 3.2f, P(16))))
                continue;
            drawn.Add(p.Middle);
            _c.FillCircle(p.Middle, size, RingFill);
            _c.StrokeCircle(p.Middle, size - P(0.75f), Theme.Muted, P(1.5f));
            Stats.Rings.Add(p.Spot.Id);
            Stats.Places["ring/" + p.Spot.Id] = new Rect(p.Middle.X - size, p.Middle.Y - size, 2 * size, 2 * size);
        }
    }

    /// <summary> The soft rose outline around a place: a faint light outside, a see-through fill, a dashed line. </summary>
    private void Glow(PlaceInfo p, float strength)
    {
        var e   = p.Glow;
        var pts = e.Points(72);
        var poly = Poly(pts);
        _c.Shape(poly, _c.Bounds, Theme.Accent.Fade(0.05f * strength), P(14));
        _c.Shape(poly, _c.Bounds, Theme.Accent.Fade(0.07f * strength), P(7));
        _c.Shape(poly, _c.Bounds, Colr.Transparent, 0, Theme.Accent.Fade(0.07f * strength));
        foreach (var (a, b) in Geometry.Dashes(pts, P(5), P(6)))
            _c.Line(a, b, Theme.Accent.Fade(strength), P(1.5f));
        Stats.Places["glow/" + p.Spot.Id] = e.Bounds;
    }

    /// <summary> A closed outline as a vector shape in screen pixels (the canvas's own box, so nothing is scaled). </summary>
    private PathShape Poly(IReadOnlyList<Vector2> pts)
    {
        var b  = _c.Bounds;
        var sb = new StringBuilder(pts.Count * 16);
        for (var i = 0; i < pts.Count; ++i)
            sb.Append(i == 0 ? 'M' : 'L').Append((pts[i].X - b.X).ToString("0.##", CultureInfo.InvariantCulture)).Append(' ')
                .Append((pts[i].Y - b.Y).ToString("0.##", CultureInfo.InvariantCulture));
        sb.Append('Z');
        return new PathShape(sb.ToString(), b.W, b.H);
    }

    /// <summary>
    /// Where the place under the mouse leads, in a small pill just outside its glow, on the body's outer side and at the
    /// mouse's height (so it never covers the place it names, and follows the mouse along a long place like a leg).
    /// </summary>
    /// <summary>
    /// A shelf's name in On you: the rail's, except the two ring shelves, which say their side ("Rings · right",
    /// "Rings · left"; a hand leads to its own), whatever the rail calls them.
    /// </summary>
    private static string ShelfName(RailRow row)
        => row.Id is Pieces.Rings or Pieces.RingsLeft && Pieces.BuiltIn.FirstOrDefault(d => d.Id == row.Id) is { } def ? Loc.T(def.Name) : row.Name;

    private void Label(PlaceInfo p, Rect box)
    {
        var text = string.Join(" · ", p.Shelves.Select(ShelfName));
        Stats.Label = text;
        var st = Theme.Caption;
        var b  = _c.Bounds;
        var w  = Math.Min(_c.Measure(text, st).X + P(24), b.W - P(16));
        var h  = P(26);
        var g  = p.Glow.Bounds;
        var y  = Math.Clamp(_ui.Mouse.Y - h / 2, g.Y, Math.Max(g.Y, g.Bottom - h));
        var right = p.Glow.Centre.X >= MiddleLine(_places, box) - box.W * 0.05f;
        var x = right ? g.Right + P(10) : g.X - P(10) - w;
        if (x + w > b.Right - P(8))
            x = g.X - P(10) - w;
        if (x < b.X + P(8))
            x = Math.Min(g.Right + P(10), b.Right - P(8) - w);
        var r = new Rect(x, Math.Clamp(y, b.Y + P(8), b.Bottom - P(8) - h), w, h);
        Painter.Shadow(_c, r, h / 2, _u, 8, 3, 0.3f);
        _c.FillRect(r, PillFill, h / 2);
        _c.StrokeRect(r, Theme.Edge, h / 2, Math.Max(1, _u));
        Painter.TextIn(_c, r.Inset(P(12), 0), text, st, Theme.Text, 0.5f);
        Stats.Places["label"] = r;
    }

    /// <summary> The quiet hint under the character while pointing: the key and what to do with it. </summary>
    private void Pill(Rect box)
    {
        var choice = KeyChoice(_s);
        string cap, text, after;
        if (_stayOn || choice == "none")
        {
            cap   = Loc.T("Esc");
            text  = Loc.T("Point at a part of your character");
            after = Loc.T("back to the closet");
        }
        else
        {
            cap   = Loc.T(KeyChoices.First(k => k.Value == choice).Label);
            text  = Loc.T("Hold it and point at a part of your character");
            after = string.Empty;
        }

        // just switched on from the window: the way out stands out for the first seconds, then calms down
        var strong = _stayOn && _stayOnSince >= 0 ? (float)Math.Clamp((_stayOnSince + HintStrong - _now) / 0.5, 0, 1) : 0f;
        Stats.PillStrong = strong > 0;
        Stats.Pill = string.Join(" ", new[] { _stayOn || choice == "none" ? text : cap, _stayOn || choice == "none" ? cap : text, after }.Where(t => t.Length > 0));
        var capW   = _c.Measure(cap, CapStyle).X + P(16);
        var textW  = _c.Measure(text, Theme.Caption).X;
        var afterW = after.Length > 0 ? _c.Measure(after, Theme.Caption).X : 0;
        var keyLed = !(_stayOn || choice == "none");
        // On you staying on gets a way out that needs no keyboard: a quiet × at the pill's end
        var closeW = keyLed ? 0 : P(8) + P(24);
        var w      = keyLed ? P(8) + capW + P(10) + textW + P(14) : P(14) + textW + P(10) + capW + P(6) + afterW + closeW + P(8);
        var h      = P(38);
        var maxW   = _c.Bounds.W - P(48);
        if (w > maxW)
        {
            textW = Math.Max(P(40), textW - (w - maxW));
            w     = maxW;
        }

        // centred under the character's feet; at the screen's bottom when the feet are below it
        var b = _c.Bounds;
        var x = Math.Clamp(box.Center.X - w / 2, b.X + P(24), b.Right - P(24) - w);
        var y = Math.Min(box.Bottom + P(24), b.Bottom - P(24) - h);
        var r = new Rect(x, y, w, h);
        Painter.Shadow(_c, r, h / 2, _u, 10, 4, 0.3f + 0.2f * strong);
        if (strong > 0)
            _c.FillRect(r.Expand(P(4)), Theme.Accent.Fade(0.12f * strong), h / 2 + P(4)); // a soft rose halo
        _c.FillRect(r, PillFill, h / 2);
        _c.StrokeRect(r, Theme.Edge.Mix(Theme.Accent, strong), h / 2, Math.Max(1, _u) + P(0.5f) * strong);
        var cx = r.X + (keyLed ? P(8) : P(14));
        if (keyLed)
        {
            Cap(new Rect(cx, r.Center.Y - P(11), capW, P(22)), cap, P(11));
            cx += capW + P(10);
            Painter.TextIn(_c, new Rect(cx, r.Y, textW, h), text, Theme.Caption, Theme.Text);
        }
        else
        {
            Painter.TextIn(_c, new Rect(cx, r.Y, textW, h), text, Theme.Caption, Theme.Text);
            cx += textW + P(10);
            var capBox = new Rect(cx, r.Center.Y - P(11), capW, P(22));
            if (strong > 0)
            {
                // the Esc cap filled in rose while it stands out
                _c.FillRect(capBox, Theme.Accent.Fade(strong), P(11));
                Painter.TextIn(_c, capBox, cap, CapStyle, Theme.Text.Mix(Theme.OnAccent, strong), 0.5f);
            }
            else
                Cap(capBox, cap, P(11));
            cx += capW + P(6);
            Painter.TextIn(_c, new Rect(cx, r.Y, Math.Max(0, r.Right - P(8) - closeW - cx), h), after, Theme.Caption, Theme.Muted.Mix(Theme.Text, strong));

            var close = new Rect(r.Right - P(8) - P(24), r.Center.Y - P(12), P(24), P(24));
            var resp  = _ui.Interact("pill-close", close);
            if (resp.Hovered)
                _c.FillRect(close, Theme.Picked, P(12));
            Painter.Icon(_c, Icons.Close, close.Center, P(12), resp.Hovered ? Theme.Text : Theme.Muted, 2);
            _ui.Tip("pill-close", close, Loc.T("Back to the closet"));
            Stats.Places["pill-close"] = close;
            if (resp.Clicked)
                BackToCloset();
        }

        Stats.Places["pill"] = r;
        _pillArea            = r;
    }

    /// <summary> A key's name in a small outlined box. </summary>
    private void Cap(Rect r, string text, float radius)
    {
        _c.StrokeRect(r, Theme.EdgeStrong, radius, Math.Max(1, _u));
        Painter.TextIn(_c, r, text, CapStyle, Theme.Text, 0.5f);
    }

    // ======== the chooser ========

    /// <summary> Where the chooser's pieces go this frame. </summary>
    private sealed class Layout
    {
        public bool Right { get; init; }

        /// <summary> The five pictures, top to bottom (the middle one is index 2). </summary>
        public Rect[] Fan { get; init; } = Array.Empty<Rect>();
        public Rect Text { get; init; }
        public Content Content { get; init; } = null!;

        /// <summary> Everything of the chooser: the mouse here is the chooser's, not the body's. </summary>
        public Rect Area { get; init; }
    }

    /// <summary> The words beside the fan, measured. </summary>
    private sealed class Content
    {
        public List<RailRow> Tabs { get; init; } = new();
        public string Lead { get; init; } = string.Empty;
        public List<string> Name { get; init; } = new();

        /// <summary> The whole name (the lines may cut it). </summary>
        public string Title { get; init; } = string.Empty;
        public List<(string Cap, string Text)> Hints { get; init; } = new();
        public string All { get; init; } = string.Empty;
        public float W { get; set; }
        public float H { get; set; }
    }

    private Content Measure(PlaceInfo? open)
    {
        var cards  = Cards;
        var shelf  = _s.Rail.FirstOrDefault(r => r.Id == _shelf);
        var name   = shelf is null ? _shelf : ShelfName(shelf);
        var tabs   = open is not null && open.Shelves.Count > 1 ? open.Shelves : new List<RailRow>();
        var lead   = _index >= 0 ? Loc.F("{0} · {1}", name, Loc.F("{0} of {1}", _index + 1, cards.Count)) : name;
        var title  = _index >= 0 && _index < cards.Count ? cards[_index].Name : Loc.T("Nothing");
        var trying = _s.Trying.Active;
        var hints  = new List<(string, string)> { (Loc.T("Wheel"), Loc.T("flip through")) };
        if (trying)
            hints.Add((Loc.T("Click"), _index < 0 && TakingOffHere() ? Loc.T("take it off") : Loc.T("keep it")));
        hints.Add((Loc.T("Esc"), trying ? Loc.T("put it back") : Loc.T("close")));
        var all = Loc.F("All {0}", cards.Count);

        // width: the widest line, between the mockup's 164 and a limit that keeps long mod names from pushing it wide
        var w = P(MinText);
        if (tabs.Count > 0)
            w = Math.Max(w, tabs.Sum(t => _c.Measure(ShelfName(t), TabStyle).X + P(20)) + P(4) * (tabs.Count - 1));
        w = Math.Max(w, _c.Measure(lead, Theme.Small).X + (_s.Trying.Busy ? P(30) : 0));
        w = Math.Max(w, Math.Min(P(MaxText), _c.Measure(title, NameStyle).X));
        foreach (var (cap, text) in hints)
            w = Math.Max(w, CapWidth(cap) + P(8) + _c.Measure(text, Theme.Small).X);
        w = Math.Max(w, _c.Measure(all, Theme.Caption).X + P(20));

        var lines = Painter.Wrap(_c, title, w, NameStyle, 2);
        var h     = (tabs.Count > 0 ? P(24) + P(12) : 0) + _c.LineHeight(Theme.Small) + P(4) + lines.Count * _c.LineHeight(NameStyle)
          + P(10) + hints.Count * P(22) + (hints.Count - 1) * P(7) + P(14) + P(32);
        return new Content { Tabs = tabs, Lead = lead, Name = lines, Title = title, Hints = hints, All = all, W = w, H = h };
    }

    private float CapWidth(string cap)
        => Math.Max(P(44), _c.Measure(cap, CapStyle).X + P(12));

    /// <summary>
    /// Places the chooser beside the open place (the side rule is inside): the fan on an arc around the place (as in
    /// the mockup, its middle 115 beyond the glow and clear of the body), the words after the middle picture, all of it
    /// moved inside the screen when it would run off an edge.
    /// </summary>
    private Layout? Arrange(PlaceInfo? open, Rect box, List<PlaceInfo> places)
    {
        if (open is null && _openSpot is null)
            return null;
        var content = Measure(open);
        var screen  = _c.Bounds;

        var spot = open?.Spot ?? _openSpot!;
        var glow = open?.Glow ?? Geometry.Around(spot.Line, Math.Max(spot.Radius, P(3)), P(6), spot.Id == BodySpots.Tail);

        // The place sways with the idle animation (a tail swishes all the time): the chooser stays where it was put, and
        // moves only when the place really moved (the camera turned, the character walked).
        if (!_anchored || Vector2.Distance(_anchor, glow.Centre) > P(60))
        {
            _anchor   = glow.Centre;
            _anchored = true;
            var need    = P(MinReach) + P(42) + P(TextGap) + content.W + P(24);
            var roomR   = screen.Right - box.Right;
            var roomL   = box.X - screen.X;
            // the brief: on the side with more room. A place that is clearly on one side of the body (a hand, the tail)
            // keeps the chooser on its own side while that side has room enough, so the fan stays beside it.
            var line    = MiddleLine(places, box);
            var central = Math.Abs(glow.Centre.X - line) < box.W * 0.15f;
            var ownSide = glow.Centre.X >= line;
            _right = !central && (ownSide ? roomR : roomL) >= need ? ownSide : roomR >= roomL;

            // The fan is an arc around the place, as in the mockup (there its centre is the tail's glow, 46 inside the
            // body's edge). A place deep inside the body (a hand on the far side, the chest) moves the arc's centre out
            // to that edge, so the fan never curls back over the body.
            var dir  = _right ? 1f : -1f;
            var edge = _right ? box.Right : box.X;
            _arc     = new Vector2(_right ? Math.Max(glow.Centre.X, edge - P(46)) : Math.Min(glow.Centre.X, edge + P(46)), glow.Centre.Y);
            _glowTo  = (glow.Centre.X + dir * glow.HalfWidth - _arc.X) * dir;
        }

        var right  = _right;
        var side   = right ? 1f : -1f;
        var centre = _arc;
        var reach  = Math.Max(P(MinReach), _glowTo + P(FanReach));

        // near enough to fit the words on the screen; never so near that the pictures touch each other
        var room = right ? screen.Right - P(16) - centre.X : centre.X - screen.X - P(16);
        var fits = room - P(42) - P(TextGap) - content.W;
        if (reach > fits)
            reach = Math.Max(P(MinReach), fits);

        var fan = new Rect[FanSlots.Length];
        for (var i = 0; i < FanSlots.Length; ++i)
        {
            var (angle, size) = FanSlots[i];
            var a = angle * MathF.PI / 180f;
            var c = centre + new Vector2(side * reach * MathF.Cos(a), reach * MathF.Sin(a));
            fan[i] = new Rect(c.X - P(size) / 2, c.Y - P(size) / 2, P(size), P(size));
        }

        var mid   = fan[2];
        var tabsH = content.Tabs.Count > 0 ? P(24) + P(12) : 0;
        var tx    = right ? mid.Right + P(TextGap) : mid.X - P(TextGap) - content.W;
        var text  = new Rect(tx, mid.Center.Y - P(52) - tabsH, content.W, content.H);

        // inside the screen, all of it moved together
        var area = text;
        foreach (var f in fan)
            area = Union(area, f);
        var m  = P(16);
        var dx = area.X < screen.X + m ? screen.X + m - area.X : area.Right > screen.Right - m ? screen.Right - m - area.Right : 0;
        var dy = area.Y < screen.Y + m ? screen.Y + m - area.Y : area.Bottom > screen.Bottom - m ? screen.Bottom - m - area.Bottom : 0;
        for (var i = 0; i < fan.Length; ++i)
            fan[i] = fan[i].Offset(dx, dy);
        text = text.Offset(dx, dy);
        area = area.Offset(dx, dy);
        return new Layout { Right = right, Fan = fan, Text = text, Content = content, Area = area.Expand(P(8)) };
    }

    /// <summary> Where the body's middle is across the screen: the head, chest and hips (the box leans towards the tail). </summary>
    private static float MiddleLine(List<PlaceInfo> places, Rect box)
    {
        var xs = places.Where(p => p.Spot.Id is BodySpots.HeadTop or BodySpots.Face or BodySpots.Neck or BodySpots.Chest or BodySpots.Hips)
            .Select(p => p.Middle.X).ToList();
        return xs.Count > 0 ? xs.Average() : box.Center.X;
    }

    private static Rect Union(Rect a, Rect b)
        => Rect.FromMinMax(Vector2.Min(a.Min, b.Min), Vector2.Max(a.Max, b.Max));

    private void Chooser(Layout l, PlaceInfo? open, List<PlaceInfo> places)
    {
        Stats.ChooserRight         = l.Right;
        Stats.Places["chooser"]    = l.Area;
        Stats.Places["text"]       = l.Text;
        var cards = Cards;
        var shelfIcon = _s.Rail.FirstOrDefault(r => r.Id == _shelf)?.Icon ?? _shelf;

        // the words get a soft dark cloud behind them (no edge, no card): it vanishes on a dark room and keeps them
        // readable on a bright sky
        for (var i = 0; i < 14; ++i)
            _c.FillRect(l.Text.Expand(P(2) + i * P(2.2f)), Scrim, P(12) + i * P(2.2f));

        // ---- the fan ----
        for (var i = 0; i < l.Fan.Length; ++i)
        {
            var at = _index + (i - 2);
            if (at < First || at >= cards.Count)
            {
                Stats.Fan.Add(string.Empty);
                continue;
            }

            var r      = l.Fan[i];
            var middle = i == 2;
            var card   = at >= 0 ? cards[at] : null;
            Stats.Fan.Add(card?.Name ?? Loc.T("Nothing"));
            var id   = middle ? "middle" : "fan/" + (i - 2);
            var resp = _ui.Interact(id, r);
            Stats.Places[id] = r;
            Painter.Shadow(_c, r, r.W / 2, _u, 10, 4, 0.35f);
            if (card is null)
            {
                // Nothing: a dashed circle with the "nothing" sign, as the window's Nothing tile
                _c.FillRect(r, Theme.Rail, r.W / 2);
                _c.StrokeRect(r, Theme.EdgeStrong, r.W / 2, Math.Max(1, _u), Corners.All, P(4), P(4));
                Painter.Icon(_c, Icons.Nothing, r.Center, r.W * 0.42f, Theme.Muted, middle ? 1.3f : 1.6f);
            }
            else
            {
                CardPicture(card, r, string.IsNullOrEmpty(card.Icon) ? shelfIcon : card.Icon, middle ? 1.2f : 1.45f, Colr.Hex(0xF4ECF2, middle ? 0.6f : 0.5f));
            }

            if (resp.Hovered)
                _c.FillRect(r, Theme.HoverWash, r.W / 2);

            if (middle)
            {
                var state = card?.State ?? (TakingOffHere() ? CardState.Trying : CardState.None);
                if (state == CardState.Trying)
                    _c.StrokeCircle(r.Center, r.W / 2 + P(1.5f), resp.Hovered ? Colr.Hex(0xF7C6D6) : Theme.Accent, P(3));
                else if (state == CardState.On)
                    _c.StrokeCircle(r.Center, r.W / 2 + P(1), Theme.Good, P(2));
                if (resp.Clicked)
                    Keep();
            }
            else
            {
                _ui.Tip(id, r, card?.Name ?? Loc.T("Nothing"));
                if (resp.Clicked)
                    Step(i - 2);
            }
        }

        // ---- the words ----
        var c = l.Content;
        var x = l.Text.X;
        var y = l.Text.Y;
        if (c.Tabs.Count > 0 && open is not null)
        {
            foreach (var tab in c.Tabs)
            {
                var w    = _c.Measure(ShelfName(tab), TabStyle).X + P(20);
                var r    = new Rect(x, y, w, P(24));
                var id   = "tab/" + tab.Id;
                var on   = tab.Id == _shelf;
                var resp = _ui.Interact(id, r, !on);
                Stats.Places[id] = r;
                if (on || resp.Hovered)
                    _c.FillRect(r, Theme.Picked, P(12));
                else
                    _c.StrokeRect(r, Theme.Edge, P(12), Math.Max(1, _u));
                Painter.TextIn(_c, r, ShelfName(tab), TabStyle, on ? Theme.Accent : resp.Hovered ? Theme.Text : Theme.Muted, 0.5f);
                if (resp.Clicked && !on)
                    SwitchShelf(open, tab.Id);
                x += w + P(4);
            }

            x =  l.Text.X;
            y += P(24) + P(12);
        }

        var lh    = _c.LineHeight(Theme.Small);
        var leadW = Painter.TextIn(_c, new Rect(x, y, c.W - (_s.Trying.Busy ? P(30) : 0), lh), c.Lead, Theme.Small, Theme.Muted);
        if (_s.Trying.Busy)
        {
            // the game is still dressing the character: a quiet mark; the next step does not wait for it
            Stats.BusyShown = true;
            var busy = new Rect(x + leadW + P(4), y, P(26), lh);
            Painter.Busy(_c, new Vector2(busy.X + P(4), busy.Center.Y), _u, _ui.Time);
            _ui.Interact("busy", busy, false);
            _ui.Tip("busy", busy, Loc.T("The game is still dressing your character."));
        }

        y += lh + P(4);
        var nh       = _c.LineHeight(NameStyle);
        var nameArea = new Rect(x, y, c.W, nh * c.Name.Count);
        foreach (var line in c.Name)
        {
            _c.Text(new Vector2(x, y), line, NameStyle, Theme.Text);
            y += nh;
        }

        // a name cut with "…" shows whole under the mouse
        if (string.Join(" ", c.Name) != c.Title)
        {
            _ui.Interact("name", nameArea, false);
            _ui.Tip("name", nameArea, c.Title);
        }

        y += P(10);
        foreach (var (cap, text) in c.Hints)
        {
            var cw = CapWidth(cap);
            Cap(new Rect(x, y, cw, P(22)), cap, P(6));
            Painter.TextIn(_c, new Rect(x + cw + P(8), y, c.W - cw - P(8), P(22)), text, Theme.Small, Theme.Muted);
            y += P(22) + P(7);
        }

        y += P(14) - P(7);

        // "All 86": the window on this shelf (colours are the window's; nothing here that does not work yet)
        var allW = Math.Min(_c.Measure(c.All, Theme.Caption).X + P(20), l.Text.W);
        var all  = new Rect(x, y, allW, P(32));
        Stats.Places["all"] = all;
        var allResp = _ui.Interact("all", all);
        if (allResp.Hovered)
            _c.FillRect(all, Theme.Picked, P(8));
        _c.StrokeRect(all, Theme.EdgeStrong, P(8), Math.Max(1, _u));
        Painter.TextIn(_c, all.Inset(P(6), 0), c.All, Theme.Caption, Theme.Text, 0.5f);
        if (allResp.Clicked)
            ShowAll();
    }

    /// <summary> "Nothing" is being tried for the open shelf (taking off what is on there). </summary>
    private bool TakingOffHere()
        => _s.Trying.Items.Any(t => t.TakingOff && t.Piece == _shelf);

    // ---- pictures of pieces ----

    /// <summary> How long a picture that arrives while its card is on screen takes to fade in. </summary>
    public const double PictureFade = 0.15;

    /// <summary> When each card was last drawn without its picture (a picture arriving soon after fades in). </summary>
    private readonly Dictionary<string, double> _pictureMissingAt = new(StringComparer.Ordinal);

    /// <summary> When a card's picture arrived (while the fade runs). </summary>
    private readonly Dictionary<string, double> _pictureArrivedAt = new(StringComparer.Ordinal);

    /// <summary>
    /// A card's round picture: the picture when there is one (a photo the closet took, else the mod's own), else the
    /// tinted circle with the shelf's icon, as before. A card drawn without its photo (no picture, or the mod's own)
    /// asks the closet for one (<see cref="IClosetSession.WantPicture"/>: what is on screen is taken first). A picture that arrives while its
    /// card is on screen fades in: the placeholder lies over it and fades out over <see cref="PictureFade"/>, so nothing
    /// moves.
    /// </summary>
    private void CardPicture(Card card, Rect r, string icon, float iconStroke, Colr iconColor)
    {
        var key    = card.Key.ToString();
        var radius = r.W / 2;
        var shown  = !string.IsNullOrEmpty(card.Picture) && _c.Picture(card.Picture, r, radius);
        var veil   = 1f;
        // a card without its photo asks for it (also one showing the mod's own picture: it should not wait behind the
        // whole shelf; the closet ignores asks for which no photo is due)
        if (!Pictures.IsPhoto(card.Picture))
        {
            _s.WantPicture(card.Key);
            Stats.PicturesWanted.Add(key);
        }

        if (shown)
        {
            if (_pictureMissingAt.Remove(key, out var missing) && _now - missing <= 4 * PictureFade)
                _pictureArrivedAt[key] = _now;
            veil = 0;
            if (_pictureArrivedAt.TryGetValue(key, out var arrived))
            {
                var t = (float)Math.Clamp((_now - arrived) / PictureFade, 0, 1);
                if (t >= 1)
                    _pictureArrivedAt.Remove(key);
                else
                {
                    veil                     = 1 - t;
                    Stats.PicturesFading[key] = t;
                }
            }
        }
        else
        {
            if (_pictureMissingAt.Count > 512)
                _pictureMissingAt.Clear();
            _pictureMissingAt[key] = _now;
        }

        if (veil <= 0)
            return;
        _c.FillRect(r, Theme.Tint(key).Fade(veil), radius);
        Painter.Icon(_c, icon, r.Center, r.W / 2, iconColor.Fade(veil), iconStroke);
    }
}
