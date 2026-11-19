using System.Globalization;
using System.Numerics;
using ModCloset2.Core;
using ModCloset2.Ui.Canvas;
using ModCloset2.Ui.Widgets;

namespace ModCloset2.Ui.Face;

public sealed partial class WindowFace
{
    private static readonly CultureInfo German = new("de-DE");

    /// <summary> A count as people write it in the language in use (2,300 or 2.300). </summary>
    private static string Num(int n)
        => n.ToString("N0", Loc.Language == "de" ? German : CultureInfo.InvariantCulture);

    private bool Searching
        => _s.Search.Length > 0;

    /// <summary> Shelves that are a single place on the body: they get the "Wearing" strip and the "Nothing" tile. </summary>
    private static bool IsWornPlace(string piece)
        => piece is not (Pieces.Outfits or Pieces.ToSort or Pieces.Other or Pieces.Hidden);

    private string ShelfName(string id)
    {
        foreach (var row in _s.Rail)
            if (row.Id == id)
                return row.Name;
        foreach (var t in _s.MoveTargets)
            if (t.Id == id)
                return Loc.T(t.Name);
        return id;
    }

    private int ShelfCount(string id)
    {
        foreach (var row in _s.Rail)
            if (row.Id == id)
                return row.Count;
        return _s.Cards.Count;
    }

    // ---- the right side of the wide window ----

    private void RightSide(Rect r)
    {
        Stats.Places["right"] = r;
        if (!_s.Scan.Ready || _s.Scan.Problem.Length > 0)
        {
            Waiting(r);
            return;
        }

        if (_s.Rail.Count == 0 && !Searching)
        {
            Message(r, Loc.T("No mods yet."), Loc.T("Mods you add in Penumbra show up here."), Icons.Other, Theme.Muted);
            return;
        }

        if (!Searching && _view == View.Colours && _s.Colours is { } oc)
        {
            ColoursWide(r, oc);
            return;
        }

        if (!Searching && _view == View.Looks && _s.Looks is { } looks)
        {
            LooksWide(r, looks);
            return;
        }

        if (_s.Selected == Pieces.ToSort && !Searching)
        {
            SortWide(r);
            return;
        }

        var x      = r.X + P(24);
        var w      = r.W - P(48);
        var header = new Rect(x, r.Y + P(16), w, P(40));
        var slotRow = Header(header, false);
        var y = header.Bottom + P(12);
        if (slotRow)
        {
            // no room beside the heading: the filters on a compact row of their own
            y += FilterRow(x, y, w, P(30)) + P(10);
        }
        if (_s.Suggestion is { } suggestion)
        {
            var h = Banner(new Rect(x, y, w, 0), suggestion, false);
            y += h + P(14);
        }

        if (!Searching && IsWornPlace(_s.Selected))
        {
            WearingWide(new Rect(x, y, w, P(72)));
            y += P(72);
        }

        var bar  = new Rect(x, r.Bottom - P(18) - P(60), w, P(60));
        var grid = new Rect(x, y, w, bar.Y - P(14) - y);
        Grid(grid, false);
        TryBarWide(bar);
        Toast(new Rect(x, r.Y, w, r.H), bar.Y);
    }

    /// <summary> A heading with an optional count beside it (the colours board, the looks). </summary>
    private void HeaderText(Rect r, string title, int? count, bool slim)
    {
        var style  = slim ? Theme.SlimHeading : Theme.Heading;
        var cs     = slim ? Theme.Caption : Theme.Body;
        var text   = count is { } n ? Num(n) : string.Empty;
        var countW = _c.Measure(text, cs).X;
        var titleW = Painter.TextIn(_c, new Rect(r.X, r.Y, r.W - countW - P(12), r.H), title, style, Theme.Text);
        if (text.Length > 0)
            Painter.TextIn(_c, new Rect(r.X + titleW + (slim ? P(8) : P(12)), r.Y + (slim ? P(2) : P(4)), countW + 1, r.H), text, cs, Theme.Muted);
    }

    /// <summary>
    /// The shelf's name (or "Search"), its count, the kind chips and the filters (slot, body, race). Returns true when the
    /// filters did not fit beside the heading (the caller gives them a compact row of their own).
    /// </summary>
    private bool Header(Rect r, bool slim)
    {
        var title = Searching ? Loc.T("Search") : ShelfName(_s.Selected);
        var count = Searching ? _s.Cards.Count : ShelfCount(_s.Selected);
        var style = slim ? Theme.SlimHeading : Theme.Heading;
        var cs    = slim ? Theme.Caption : Theme.Body;
        var countText = Num(count);
        var countW    = _c.Measure(countText, cs).X;

        var kinds   = _s.Kinds;
        var chipsW  = 0f;
        var widths  = new List<float>(kinds.Count);
        foreach (var k in kinds)
        {
            var cw = _c.Measure(KindLabel(k), Theme.Caption).X + P(24);
            widths.Add(cw);
            chipsW += cw + P(8);
        }

        // the filters (slot, body, race) go at the right end, the kind chips left of them. Short of room the kinds fold
        // into their dropdown first, then the filters into one "Filters" button; when even that is too much the filters
        // take a row of their own
        var titleRoom = r.W - countW - P(12);
        var titleNeed = Math.Min(_c.Measure(title, style).X, Math.Max(P(90), titleRoom * 0.4f));
        var room      = r.W - titleNeed - countW - P(28);
        var shown     = ShownFilters();
        var fullW     = FiltersWidth(shown, false);
        var foldW     = CanFold(shown) ? FiltersWidth(shown, true) : fullW;
        var dropW     = kinds.Count == 0 ? 0 : KindDropdownWidth();
        var dropdown  = slim || kinds.Count > 0 && chipsW + fullW > room;
        var kindW     = kinds.Count == 0 ? 0 : dropdown ? dropW : chipsW - P(8);
        var folded    = CanFold(shown) && kindW + fullW > room;
        var filterW   = folded ? foldW : fullW;
        var ownRow    = shown.Count > 0 && kindW + filterW > room;
        float rightW  = 0;
        if (shown.Count > 0 && !ownRow)
        {
            DrawFilters(shown, folded, r.Center.Y - P(15), P(30), right: r.Right);
            rightW += filterW;
        }

        var kr = r with { W = r.W - rightW };
        if (kinds.Count > 0)
            rightW += dropdown ? KindDropdown(kr, slim) + P(12) : KindChips(kr, widths) + P(12);

        var titleW = Painter.TextIn(_c, new Rect(r.X, r.Y, r.W - rightW - countW - P(12), r.H), title, style, Theme.Text);
        var baseOff = slim ? P(2) : P(4);
        Painter.TextIn(_c, new Rect(r.X + titleW + (slim ? P(8) : P(12)), r.Y + baseOff, countW + 1, r.H), countText, cs, Theme.Muted);
        return ownRow;
    }

    private float KindDropdownWidth()
    {
        var current = _s.Kinds.FirstOrDefault(k => k.Id == _s.Kind) ?? _s.Kinds[0];
        var label   = current.Id.Length == 0 ? Loc.T("All kinds") : KindLabel(current);
        return _c.Measure(label, Theme.Caption).X + P(24 + 18);
    }

    private string KindLabel(KindChip k)
        => k.Id.Length == 0 ? k.Name : $"{k.Name} {Num(k.Count)}";

    /// <summary> The kind chips, right-aligned; returns their total width. </summary>
    private float KindChips(Rect r, List<float> widths)
    {
        var kinds = _s.Kinds;
        var total = widths.Sum() + P(8) * (widths.Count - 1);
        var x     = r.Right - total;
        for (var i = 0; i < kinds.Count; ++i)
        {
            var k      = kinds[i];
            var box    = new Rect(x, r.Center.Y - P(15), widths[i], P(30));
            var picked = k.Id == _s.Kind;
            var resp   = _ui.Interact("kind/" + k.Id, box, !picked);
            if (picked)
                _c.FillRect(box, Theme.Picked, P(15));
            else
            {
                if (resp.Hovered)
                    _c.FillRect(box, Theme.HoverWash, P(15));
                _c.StrokeRect(box, Theme.Edge, P(15), Math.Max(1, _u));
            }

            Painter.TextIn(_c, box, KindLabel(k), Theme.Caption, picked || resp.Hovered ? Theme.Text : Theme.Muted, 0.5f);
            if (resp.Clicked && !picked)
                _s.Kind = k.Id;
            x += widths[i] + P(8);
        }

        return total;
    }

    /// <summary> When the chips do not fit (and in the slim window): one chip that opens a menu of kinds. </summary>
    private float KindDropdown(Rect r, bool slim)
    {
        var current = _s.Kinds.FirstOrDefault(k => k.Id == _s.Kind) ?? _s.Kinds[0];
        var label   = current.Id.Length == 0 ? Loc.T("All kinds") : KindLabel(current);
        var w       = _c.Measure(label, Theme.Caption).X + P(24 + 18);
        var box     = new Rect(r.Right - w, r.Center.Y - P(15), w, P(30));
        var resp    = _ui.Interact("kinds", box);
        _c.FillRect(box, current.Id.Length > 0 ? Theme.Picked : resp.Hovered ? Theme.HoverWash : Colr.Transparent, P(15));
        if (current.Id.Length == 0)
            _c.StrokeRect(box, Theme.Edge, P(15), Math.Max(1, _u));
        Painter.TextIn(_c, new Rect(box.X + P(12), box.Y, w - P(30), box.H), label, Theme.Caption, Theme.Text);
        Painter.Icon(_c, Icons.ChevronD, new Vector2(box.Right - P(16), box.Center.Y), P(12), Theme.Muted, 2);
        if (resp.Clicked)
            _ui.OpenPopup(PopKinds, new Vector2(box.X, box.Bottom + P(6)));
        return w;
    }

    // ---- the "Wearing" strip ----

    // which of the things on the selected shelf the strip shows: kept by key while the shelf stays selected and the
    // thing is still on, else the first
    private string _shownShelf = string.Empty;
    private PartKey? _shownKey;
    private PartKey? _shownWant;

    /// <summary> The thing the strip shows, its place among everything on the shelf, and how many there are. </summary>
    private (WearingInfo? Shown, int Index, int Count) ShownWearing()
    {
        var all = _s.WearingAll;
        if (all.Count == 0)
        {
            _shownKey = null;
            return (null, 0, 0);
        }

        if (_shownShelf != _s.Selected)
        {
            _shownShelf = _s.Selected;
            _shownKey   = null;
            _shownWant  = null;
        }

        // a card just clicked: the strip (and the panel) turn to it once the session lists it
        if (_shownWant is { } want && all.Any(w => w.Key == want))
        {
            _shownKey  = want;
            _shownWant = null;
        }

        var index = 0;
        for (var i = 0; i < all.Count; ++i)
        {
            if (all[i].Key == _shownKey)
            {
                index = i;
                break;
            }
        }

        _shownKey = all[index].Key;
        return (all[index], index, all.Count);
    }

    /// <summary>
    /// The strip's small label: "Wearing", and when several things are on and the shown one's card has a label, that too
    /// ("Wearing · Jacket"; slim, short of room, only "Jacket").
    /// </summary>
    private string WearingLabel(WearingInfo shown, int count, bool slim)
    {
        // a thing being tried, not worn: "Trying"
        var lead = shown.Trying ? Loc.T("Trying") : Loc.T("Wearing");
        if (count < 2)
            return lead;
        var kind = string.Empty;
        foreach (var card in _s.Cards)
        {
            if (card.Key == shown.Key)
            {
                kind = card.KindName;
                break;
            }
        }

        return kind.Length == 0 ? lead : slim ? kind : Loc.F("{0} · {1}", lead, kind);
    }

    /// <summary> What is on with this key on the selected shelf (for menus opened from the strip or a card). </summary>
    private WearingInfo? WornWithKey(PartKey key)
        => _s.WearingAll.FirstOrDefault(w => w.Key == key);

    /// <summary> The colour bar of the thing shown: the bar with its key; for the first one the shelf's bar as before. </summary>
    private PieceColours? ShownColours(WearingInfo shown, int index)
    {
        if (_s.Colours is not { } oc)
            return null;
        foreach (var piece in oc.Pieces)
            if (piece.Key == shown.Key)
                return piece;
        return index == 0 ? oc.For(_s.Selected) : null;
    }

    /// <summary>
    /// "‹ 1 of 3 ›" at the start of the strip when several things are on the shelf; returns its width (0 when there is
    /// only one).
    /// </summary>
    private float WornSwitch(float x, float cy, int index, int count, bool slim)
    {
        if (count < 2)
            return 0;
        var all   = _s.WearingAll;
        var style = slim ? Theme.Small : Theme.Caption;
        var text  = Loc.F("{0} of {1}", index + 1, count);
        var tw    = Math.Max(_c.Measure(text, style).X, _c.Measure(Loc.F("{0} of {1}", count, count), style).X);
        var bw    = slim ? P(20) : P(24);
        var bh    = slim ? P(28) : P(32);
        var prev  = new Rect(x, cy - bh / 2, bw, bh);
        var label = new Rect(prev.Right + P(2), cy - bh / 2, tw, bh);
        var next  = new Rect(label.Right + P(2), cy - bh / 2, bw, bh);
        var whole = new Rect(prev.X, prev.Y, next.Right - prev.X, bh);
        Stats.Places["worn-switch"] = whole;
        Stats.Places["worn-prev"]   = prev;
        Stats.Places["worn-next"]   = next;
        _c.FillRect(whole, Theme.Chip, P(8));

        var before = (index - 1 + count) % count;
        var after  = (index + 1) % count;
        var pr     = _ui.Interact("worn-prev", prev);
        var nr     = _ui.Interact("worn-next", next);
        if (pr.Hovered)
            _c.FillRect(prev, Theme.Picked, P(8), Corners.Left);
        if (nr.Hovered)
            _c.FillRect(next, Theme.Picked, P(8), Corners.Right);
        Painter.Icon(_c, Icons.ChevronL, prev.Center, P(14), pr.Hovered ? Theme.Text : Theme.Muted, 2);
        Painter.Icon(_c, Icons.ChevronR, next.Center, P(14), nr.Hovered ? Theme.Text : Theme.Muted, 2);
        Painter.TextIn(_c, label, text, style, Theme.Text, 0.5f);
        _ui.Interact("worn-count", label, false);
        _ui.Tip("worn-count", label, Loc.F("{0} things are on in this shelf", count));
        _ui.Tip("worn-prev", prev, Loc.F("Show {0}", all[before].Name));
        _ui.Tip("worn-next", next, Loc.F("Show {0}", all[after].Name));
        if (pr.Clicked)
            _shownKey = all[before].Key;
        else if (nr.Clicked)
            _shownKey = all[after].Key;
        return whole.W;
    }

    private void WearingWide(Rect r)
    {
        Stats.Places["wearing"] = r;
        _c.FillRect(r, Theme.Rail, P(12));
        var (wearing, shownIndex, shownCount) = ShownWearing();
        if (wearing is null)
        {
            Painter.TextIn(_c, r.Inset(P(18), 0), Loc.T("Nothing on here right now."), Theme.Body, Theme.Muted);
            return;
        }

        var switchW = WornSwitch(r.X + P(14), r.Center.Y, shownIndex, shownCount, false);
        var pic     = new Rect(r.X + P(14) + (switchW > 0 ? switchW + P(14) : 0), r.Center.Y - P(24), P(48), P(48));
        PictureTile("wearing/" + wearing.Key, wearing.Key, pic, wearing.Picture, wearing.Icon, wearing.Name, P(10), Corners.All, P(26), 1.4f, Colr.Hex(0xF4ECF2, 0.45f));

        // the options and the colour bar live in the panel beside the closet; here one button opens it
        var x     = pic.Right + P(18);
        var right = r.Right - P(14);
        if (PanelHasMore(wearing, shownIndex))
        {
            var bw  = Math.Min(PanelButtonWidth(true), Math.Max(P(36), right - x - P(120)));
            var btn = new Rect(right - bw, r.Center.Y - P(17), bw, P(34));
            PanelButton(btn);
            right = btn.X - P(18);
        }

        var labelH  = _c.LineHeight(Theme.Label);
        var strongH = _c.LineHeight(Theme.Strong);
        var nameW   = Math.Max(P(40), right - x);
        var colY    = r.Center.Y - (labelH + P(2) + strongH) / 2;
        Painter.TextIn(_c, new Rect(x, colY, nameW, labelH), WearingLabel(wearing, shownCount, false), Theme.Label, Theme.Muted);
        var fitted = Painter.Fit(_c, wearing.Name, nameW, Theme.Strong);
        _c.Text(new Vector2(x, colY + labelH + P(2)), fitted, Theme.Strong, Theme.Text);
        WearingOpens(new Rect(pic.X, r.Y, x + nameW - pic.X, r.H), wearing, fitted);
    }

    /// <summary> The strip's picture and name: a click opens the options panel on the thing shown. </summary>
    private void WearingOpens(Rect r, WearingInfo wearing, string fitted)
    {
        Stats.Places["wearing-open"] = r;
        var resp = _ui.Interact("wearing-open", r);
        if (resp.Hovered)
            _c.FillRect(r, Theme.HoverWash, P(10));
        _ui.Tip("wearing-open", r, fitted != wearing.Name ? Loc.F("{0} · {1}", wearing.Name, Loc.T("Options & colours")) : Loc.T("Options & colours"));
        if (resp.Clicked)
            OpenPanelOn(wearing.Key);
    }

    /// <summary> Is there anything for the panel beyond the picture and name: options, versions, slots or a colour bar? </summary>
    private bool PanelHasMore(WearingInfo wearing, int index)
        => wearing.Options.Count > 0 || wearing.Versions > 1 || _s.SlotsOf(wearing.Key).Count > 0 || ShownColours(wearing, index) is not null;

    private float PanelButtonWidth(bool words)
        => words ? P(12) + P(14) + P(8) + _c.Measure(Loc.T("Options & colours"), Theme.Caption).X + P(14) : P(34);

    /// <summary>
    /// "Options & colours" on the strip: opens the panel beside the closet (and closes it again), so nothing is lost
    /// when the panel was closed. Only the sliders icon when the strip is short of room.
    /// </summary>
    private void PanelButton(Rect box)
    {
        Stats.Places["panel-open"] = box;
        var resp   = _ui.Interact("panel-open", box);
        var shown  = PanelShown;
        _c.FillRect(box, shown ? Theme.Picked : resp.Hovered ? Theme.Picked : Theme.Chip, P(8));
        if (shown)
            _c.StrokeRect(box, Theme.Accent.Fade(0.6f), P(8), Math.Max(1, _u));
        var fg    = shown ? Theme.Accent : resp.Hovered ? Theme.Text : Theme.Text.Fade(0.85f);
        var words = box.W >= PanelButtonWidth(true) - 0.5f;
        if (words)
        {
            Painter.Icon(_c, Icons.Settings, new Vector2(box.X + P(12) + P(7), box.Center.Y), P(14), fg, 1.8f);
            Painter.TextIn(_c, new Rect(box.X + P(12) + P(14) + P(8), box.Y, box.W - P(12) - P(14) - P(8), box.H), Loc.T("Options & colours"), Theme.Caption, fg);
        }
        else
            Painter.Icon(_c, Icons.Settings, box.Center, P(16), fg, 1.8f);

        _ui.Tip("panel-open", box, shown ? Loc.T("Close the options and colours") : Loc.T("Show the options and colours beside the closet"));
        if (!resp.Clicked)
            return;
        if (shown)
            ClosePanel();
        else
            OpenPanelOn(_shownKey);
    }

    /// <summary> The piece's colours as one bar of segments sized by their share (not clickable yet). </summary>
    private void ColourBar(Rect r, IReadOnlyList<ColourSegment> segments, float radius)
    {
        _c.FillRect(r, Theme.EdgeStrong, radius);
        var inner = r.Inset(1, 1);
        var total = segments.Sum(s => Math.Max(0.001f, s.Share));
        var gap   = P(2);
        var avail = inner.W - gap * (segments.Count - 1);
        var x     = inner.X;
        for (var i = 0; i < segments.Count; ++i)
        {
            var seg     = segments[i];
            var w       = avail * Math.Max(0.001f, seg.Share) / total;
            var corners = (i == 0 ? Corners.Left : Corners.None) | (i == segments.Count - 1 ? Corners.Right : Corners.None);
            var box     = new Rect(x, inner.Y, w, inner.H);
            _c.FillRect(box, Colr.Hex(seg.Rgb), radius - 1, corners);
            if (seg.Painted)
            {
                _ui.PushClip(box);
                for (var hx = box.X - box.H; hx < box.Right; hx += P(5))
                    _c.Line(new Vector2(hx, box.Bottom), new Vector2(hx + box.H, box.Y), Colr.Hex(0x000000, 0.3f), Math.Max(1, _u));
                _ui.PopClip();
            }

            var id = "seg/" + i;
            _ui.Interact(id, box, false);
            _ui.Tip(id, box, seg.Label);
            x += w + gap;
        }
    }

    // ---- the grid of cards ----

    /// <summary> Layout of the grid for a given width (also used by the tests and by the arrow keys). </summary>
    public readonly record struct GridLayout(int Columns, float CardW, float PicH, float NameH, float Gap, float PadTop, int Items, bool Nothing)
    {
        public float CardH
            => PicH + NameH;

        public float RowStep
            => CardH + Gap;

        public int Rows
            => (Items + Columns - 1) / Columns;

        public float ContentH
            => Rows == 0 ? 0 : PadTop + Rows * CardH + (Rows - 1) * Gap + PadTop;
    }

    /// <summary> "Right ring slot only" / "Left ring slot only" for a ring with files for one ring slot; "" otherwise. </summary>
    private static string HandOnly(Card card)
        => card.OneHand switch
        {
            Hands.Right => Loc.T("Right ring slot only"),
            Hands.Left  => Loc.T("Left ring slot only"),
            _           => string.Empty,
        };

    private object? _labelsOf;
    private bool _labelsAny;

    /// <summary> Some card of the shelf has a label (two cards of one mod): the cards get their second line. Kept per list. </summary>
    private bool AnyCardLabel()
    {
        var cards = _s.Cards;
        if (!ReferenceEquals(cards, _labelsOf))
        {
            _labelsOf  = cards;
            _labelsAny = false;
            foreach (var c in cards)
            {
                if (c.KindName.Length > 0 || c.OneHand != Hands.Both)
                {
                    _labelsAny = true;
                    break;
                }
            }
        }

        return _labelsAny;
    }

    public GridLayout LayoutGrid(float width, bool slim, int cardCount)
    {
        var gap     = slim ? P(12) : P(14);
        var aim     = slim ? P(172) : P(170);
        var cols    = Math.Max(slim ? 2 : 1, (int)MathF.Round((width + gap) / (aim + gap)));
        var cardW   = (width - (cols - 1) * gap) / cols;
        var pic     = slim ? cardW * 148f / 172f : cardW * 212f / 170f;
        var twoLine = Searching || _s.Selected == Pieces.Other || AnyCardLabel();
        var nameH   = (slim ? P(36) : P(40)) + (twoLine ? P(14) : 0);
        var nothing = !slim && !Searching && IsWornPlace(_s.Selected) && CanTakeOff(_s.Selected);
        return new GridLayout(cols, cardW, pic, nameH, gap, slim ? P(12) : P(14), cardCount + (nothing ? 1 : 0), nothing);
    }

    private void Grid(Rect area, bool slim)
    {
        Stats.Grid = area;
        Stats.GridHovered |= area.Contains(_ui.Mouse);
        var cards = _s.Cards;
        if (cards.Count == 0)
        {
            if (Searching)
                Message(area, Loc.F("Nothing found for “{0}”.", _s.Search), Loc.T("Try fewer or shorter words."), Icons.Search, Theme.Muted);
            else if (_s.Selected == Pieces.Hidden)
                Message(area, Loc.T("Nothing is hidden."), string.Empty, Icons.Hidden, Theme.Muted);
            else if (_s.Selected == Pieces.Other && !_s.ShowUnwearable)
            {
                Stats.Places["unwearable-hidden"] = area;
                Message(area, Loc.T("Animations, effects and other files are hidden. Settings can show them."), string.Empty, Icons.Other, Theme.Muted);
            }
            else
                Message(area, Loc.T("Nothing on this shelf yet."), string.Empty, _s.Selected, Theme.Muted);
            return;
        }

        var g = LayoutGrid(area.W, slim, cards.Count);
        Stats.Columns       = g.Columns;
        Stats.CardWidth     = g.CardW;
        Stats.PictureHeight = g.PicH;
        var view   = new Rect(area.X - P(4), area.Y, area.W + P(8), area.H);
        var scroll = _ui.Scroll("grid", view, g.ContentH, P(120));
        _ui.PushClip(view);
        var first = Math.Max(0, (int)((scroll - g.PadTop) / g.RowStep));
        var last  = Math.Min(g.Rows - 1, (int)((scroll + area.H - g.PadTop) / g.RowStep));
        for (var row = first; row <= last; ++row)
            for (var col = 0; col < g.Columns; ++col)
            {
                var i = row * g.Columns + col;
                if (i >= g.Items)
                    break;
                var box = new Rect(area.X + col * (g.CardW + g.Gap), area.Y + g.PadTop + row * g.RowStep - scroll, g.CardW, g.CardH);
                if (g.Nothing && i == 0)
                {
                    NothingTile(box);
                    continue;
                }

                var index = i - (g.Nothing ? 1 : 0);
                CardTile(cards[index], box, index, g, slim);
            }

        _ui.PopClip();
        _ui.ScrollBar("grid", new Rect(area.X, area.Y, area.W + (slim ? P(10) : P(18)), area.H), g.ContentH, 6);
    }

    private void ScrollToCard(int index)
    {
        var grid = Stats.Grid;
        if (grid.W <= 0)
            return;
        var g      = LayoutGrid(grid.W, IsSlim, _s.Cards.Count);
        var i      = index + (g.Nothing ? 1 : 0);
        var top    = g.PadTop + i / g.Columns * g.RowStep;
        var scroll = _ui.ScrollOf("grid");
        if (top < scroll)
            _ui.SetScroll("grid", top - g.PadTop);
        else if (top + g.CardH > scroll + grid.H)
            _ui.SetScroll("grid", top + g.CardH + g.PadTop - grid.H);
    }

    /// <summary>
    /// Shelves that offer "Nothing". Not Skin: what is on there is the body mod itself, and taking it off by accident
    /// breaks every outfit.
    /// </summary>
    private static bool CanTakeOff(string piece)
        => piece is not (Pieces.Skin or Pieces.Bodies or Pieces.MainHand);

    /// <summary> Taking off what is on in this shelf is being tried. </summary>
    private bool TakingOffHere()
    {
        var t = _s.Trying;
        if (!t.Active)
            return false;
        foreach (var item in t.Items)
            if (item.TakingOff && item.Piece == _s.Selected)
            {
                Stats.TakingOff = true;
                return true;
            }

        return false;
    }

    private void NothingTile(Rect r)
    {
        Stats.Places["nothing"] = r;
        var resp = _ui.Interact("nothing", r);
        if (resp.Hovered)
            _c.FillRect(r, Theme.HoverWash, P(12));
        if (TakingOffHere())
            _c.StrokeRect(r.Expand(P(2)), Theme.Accent, P(14), P(2));
        else
            _c.StrokeRect(r, Theme.EdgeStrong, P(12), Math.Max(1, _u), Corners.All, P(5), P(4));
        var lh = _c.LineHeight(Theme.Caption);
        var cy = r.Center.Y - (P(28) + P(10) + lh) / 2;
        Painter.Icon(_c, Icons.Nothing, new Vector2(r.Center.X, cy + P(14)), P(28), Theme.Muted, 1.5f);
        Painter.TextIn(_c, new Rect(r.X, cy + P(38), r.W, lh), Loc.T("Nothing"), Theme.Caption, Theme.Muted, 0.5f);
        if (resp.Clicked && _s.Wearing is not null && !TakingOffHere())
            _s.TryNothing(_s.Selected);
    }

    private void CardTile(Card card, Rect r, int index, GridLayout g, bool slim)
    {
        Stats.CardsDrawn++;
        if (Stats.FirstCard < 0)
            Stats.FirstCard = index;
        Stats.LastCard = index;
        Stats.Places["card/" + card.Key] = r;

        var id   = "card/" + card.Key;
        var resp = _ui.Interact(id, r);
        // a card lifts to be dropped on another shelf only while moves are unlocked; locked, a drag says where the lock is
        if (card.Key.Socket != Socket.None)
        {
            if (!_s.MovesLocked)
                _ui.DragSource(id, card);
            else if (_ui.DragRefused(id))
                RefusedDrag();
        }
        if (_ui.Drag?.SourceId == id)
        {
            // the card has lifted: its place stays as a quiet outline
            _c.FillRect(r, Theme.GoneFill, P(12));
            _c.StrokeRect(r, Theme.Edge, P(12), Math.Max(1, _u), Corners.All, P(5), P(4));
            return;
        }

        _c.FillRect(r, Theme.Card, P(12));
        var pic = r with { H = g.PicH };
        PictureTile("card/" + card.Key, card.Key, pic, card.Picture, card.Icon, card.Name, P(12), Corners.Top, slim ? P(52) : P(64), 1.1f);

        var (mark, fill) = card.State switch
        {
            CardState.On     => (Loc.T("On"), Theme.Good),
            CardState.Trying => (Loc.T("Trying on"), Theme.Accent),
            _                => card.IsNew ? (Loc.T("New"), Theme.Gold) : (string.Empty, default),
        };
        if (mark.Length > 0)
            Painter.Mark(_c, new Vector2(pic.X + P(8), pic.Y + P(8)), mark, fill, _u);
        // the notes at the picture's bottom right, stacked when there are two ("3 slots" under "2 versions"), so they
        // never crowd the corner sideways; they keep clear of what sits at the bottom left: the "?", then "Options"
        var notes = new List<(string Kind, string Text)>(2);
        if (card.Versions > 1)
            notes.Add(("versions", Loc.F("{0} versions", card.Versions)));
        if (card.Slots > 1)
            notes.Add(("slots", Loc.F("{0} slots", card.Slots)));
        if (notes.Count == 0 && card.PartsInMod > 1)
            notes.Add(("set", Loc.F("{0}-piece set", card.PartsInMod)));
        var noteBottom = pic.Bottom - P(8);
        var leftX      = pic.X + P(8) + (card.Unsure ? P(26) : 0);
        var optionsW   = 0f;
        if (card.OptionGroups.Count > 0)
        {
            // the word when it fits beside the bottom note, else the sliders icon alone (the tooltip says it either way)
            var noteW = notes.Count == 0 ? 0 : Math.Min(pic.W - P(16), _c.Measure(notes[^1].Text, Theme.MarkPlain).X + P(14)) + P(6);
            optionsW  = OptionsMarkWidth(true) <= pic.Right - P(8) - noteW - leftX ? OptionsMarkWidth(true) : OptionsMarkWidth(false);
        }

        var noteMax = pic.Right - P(8) - leftX - (optionsW > 0 ? optionsW + P(6) : 0);
        for (var i = notes.Count - 1; i >= 0; --i)
        {
            var box = Painter.Note(_c, new Vector2(pic.Right - P(8), noteBottom), notes[i].Text, _u, noteMax);
            Stats.Places[$"note-{notes[i].Kind}/{card.Key}"] = box;
            noteBottom = box.Y - P(4);
        }

        if (card.Unsure)
            UnsureMark(card, new Vector2(pic.X + P(8), pic.Bottom - P(8)));
        if (optionsW > 0)
            OptionsMark(card, new Rect(leftX, pic.Bottom - P(8) - P(20), optionsW, P(20)));
        var slotNamed = card.SlotName.Length > 0 && SlotMark(card, pic, mark.Length > 0 ? Painter.MarkWidth(_c, mark, _u) : 0);

        var nameBox = new Rect(r.X + P(10), pic.Bottom, r.W - P(20), g.NameH);
        string fitted;
        // what tells cards of one mod apart ("Jacket", "Wrist slot"; on Everything else the kind), on every shelf
        // (a ring with files for one ring slot only says so when nothing else is said)
        // ("Left ring slot only" only when the slot mark does not already say "Left ring slot")
        var label  = card.KindName.Length > 0 ? card.KindName : slotNamed ? string.Empty : HandOnly(card);
        var second = !Searching ? label : label.Length > 0 ? Loc.F("{0} · {1}", card.PieceName, label) : card.PieceName;
        if (g.NameH > (slim ? P(36) : P(40)))
        {
            var lh  = _c.LineHeight(Theme.Caption);
            var top = nameBox.Y + (nameBox.H - lh - P(16)) / 2;
            fitted = Painter.Fit(_c, card.Name, nameBox.W, Theme.Caption);
            _c.Text(new Vector2(nameBox.X, top), fitted, Theme.Caption, Theme.Text);
            Painter.TextIn(_c, new Rect(nameBox.X, top + lh, nameBox.W, P(16)), second, Theme.Small, Theme.Muted);
        }
        else
        {
            fitted = Painter.Fit(_c, card.Name, nameBox.W, Theme.Caption);
            Painter.TextIn(_c, nameBox, fitted, Theme.Caption, Theme.Text);
        }

        if (resp.Hovered)
            _c.FillRect(r, Theme.HoverWash, P(12));
        if (card.State == CardState.Trying)
            _c.StrokeRect(r.Expand(P(2)), Theme.Accent, P(14), P(2));
        else if (index == _nav && _navShown)
            _c.StrokeRect(r.Expand(P(2)), Theme.Faint, P(14), P(2));

        // the tooltip names the slot it is worn through (and the whole name when it is cut), and the options it has
        var bodyRace = BodyRaceLine(card);
        var tip = card.SlotName.Length > 0 ? Loc.F("{0} · {1}", card.Name, card.SlotName)
            : fitted != card.Name || card.OptionGroups.Count > 0 || bodyRace.Length > 0 ? card.Name : string.Empty;
        if (card.OptionGroups.Count > 0)
            tip += "\n" + OptionsText(card);
        // the bodies and races it is made for, when known ("Body: Rue · Race: Miqo'te")
        if (bodyRace.Length > 0)
            tip += "\n" + bodyRace;
        if (tip.Length > 0)
            _ui.Tip(id, r, tip);
        if (resp.Clicked)
        {
            _nav      = index;
            _navShown = false;
            // a click on a mod opens the options panel on it (also on one that is on already)
            if (card.State != CardState.Trying)
                OpenPanelOn(card.Key);
            switch (card.State)
            {
                case CardState.On:
                    break; // already on: nothing to do
                case CardState.Trying:
                    _s.StopTrying(card.Key);
                    break;
                default:
                    _s.Try(card.Key);
                    break;
            }
        }

        if (resp.RightClicked)
            _ui.OpenPopup(PopCard, _ui.Mouse, card);
    }

    private bool _navShown;

    /// <summary>
    /// The closet is not sure about this card's shelf: a small, quiet gold "?" at the bottom left of the picture. The
    /// card stays here (its best guess) and also waits under To sort.
    /// </summary>
    /// <summary>
    /// The slot a card is worn through, small at the picture's top right ("Wrist slot"): dim as a rule, in the accent
    /// colour when it is not the usual slot for the shelf (a jacket in the wrist slot). When it does not fit beside the
    /// "On"/"New" mark: the slot icon alone (the card's tooltip names the slot).
    /// </summary>
    private bool SlotMark(Card card, Rect pic, float leftMarkW)
    {
        var id    = "slot-mark/" + card.Key;
        var h     = P(20);
        var fg    = card.SlotUnusual ? Theme.OnAccent : Theme.Text.Fade(0.8f);
        var fill  = card.SlotUnusual ? Theme.Accent : Theme.NoteFill;
        var textW = _c.Measure(card.SlotName, Theme.MarkPlain).X + P(14);
        var room  = pic.W - P(16) - (leftMarkW > 0 ? leftMarkW + P(6) : 0);
        Rect box;
        if (textW <= room)
        {
            box = new Rect(pic.Right - P(8) - textW, pic.Y + P(8), textW, h);
            _c.FillRect(box, fill, P(6));
            Painter.TextIn(_c, box.Inset(P(7), 0), card.SlotName, Theme.MarkPlain, fg, 0.5f);
        }
        else
        {
            box = new Rect(pic.Right - P(8) - h, pic.Y + P(8), h, h);
            _c.FillRect(box, fill, P(6));
            Painter.Icon(_c, Icons.Slots, box.Center, P(12), fg, 1.8f);
        }

        Stats.Places[id] = box;
        return textW <= room;
    }

    /// <summary> "Options: Sleeves, Back": what the "Options" mark and the card's tooltip say. </summary>
    private static string OptionsText(Card card)
        => Loc.F("Options: {0}", string.Join(", ", card.OptionGroups));

    /// <summary> The "Options" mark's width: the sliders icon and the word, or the icon alone. </summary>
    private float OptionsMarkWidth(bool word)
        => word ? P(7) + P(12) + P(5) + _c.Measure(Loc.T("Options"), Theme.MarkPlain).X + P(7) : P(20);

    /// <summary>
    /// The mod has options to set (<see cref="Card.OptionGroups"/>): a quiet note at the picture's bottom left, opposite
    /// "3 versions" and styled like it, after the "?" when there is one. The sliders icon and "Options" (the word the
    /// Options strip uses), or only the icon when the word does not fit; its tooltip names the groups.
    /// </summary>
    private void OptionsMark(Card card, Rect box)
    {
        _c.FillRect(box, Theme.NoteFill, P(6));
        if (box.W > P(20))
        {
            Painter.Icon(_c, Icons.Settings, new Vector2(box.X + P(7) + P(6), box.Center.Y), P(12), Theme.Text, 1.8f);
            _c.Text(new Vector2(box.X + P(7) + P(12) + P(5), box.Center.Y - _c.LineHeight(Theme.MarkPlain) / 2), Loc.T("Options"), Theme.MarkPlain, Theme.Text);
        }
        else
            Painter.Icon(_c, Icons.Settings, box.Center, P(12), Theme.Text, 1.8f);

        var id = "options-mark/" + card.Key;
        Stats.Places[id] = box;
        _ui.Interact(id, box, false);
        _ui.Tip(id, box, OptionsText(card));
    }

    private void UnsureMark(Card card, Vector2 bottomLeft)
    {
        Stats.UnsureMarks++;
        var box = new Rect(bottomLeft.X, bottomLeft.Y - P(20), P(20), P(20));
        _c.FillRect(box, Theme.NoteFill, P(6));
        Painter.TextIn(_c, box, "?", Theme.Mark, Theme.Gold, 0.5f);
        var id = "unsure/" + card.Key;
        Stats.Places[id] = box;
        _ui.Interact(id, box, false);
        _ui.Tip(id, box, Loc.T("The closet is not sure about this one. To sort has it too."));
    }

    // ---- messages ----

    /// <summary> A calm centred message (empty shelf, nothing found, no mods). </summary>
    private void Message(Rect area, string line, string second, string icon, Colr iconColor)
    {
        var lh    = _c.LineHeight(Theme.Body);
        var ch    = _c.LineHeight(Theme.Caption);
        var more  = second.Length > 0 ? Painter.Wrap(_c, second, area.W - P(32), Theme.Caption, 3) : new List<string>();
        var sh    = more.Count > 0 ? more.Count * ch + P(4) : 0;
        var total = P(32) + P(14) + lh + sh;
        var y     = area.Y + Math.Max(P(20), (area.H - total) / 2 - P(10));
        Painter.Icon(_c, icon, new Vector2(area.Center.X, y + P(16)), P(32), iconColor.Fade(0.8f), 1.4f);
        y += P(32) + P(14);
        foreach (var l in Painter.Wrap(_c, line, area.W - P(32), Theme.Body, 2))
        {
            Painter.TextIn(_c, new Rect(area.X, y, area.W, lh), l, Theme.Body, Theme.Text, 0.5f);
            y += lh;
        }

        y += P(4);
        foreach (var l in more)
        {
            Painter.TextIn(_c, new Rect(area.X + P(16), y, area.W - P(32), ch), l, Theme.Caption, Theme.Muted, 0.5f);
            y += ch;
        }
    }

    /// <summary> While the library is read (or cannot be): one calm line, centred. </summary>
    private void Waiting(Rect r)
    {
        Stats.Places["waiting"] = r;
        var scan = _s.Scan;
        if (scan.Problem.Length > 0)
        {
            Message(r, scan.Problem, string.Empty, Icons.Info, Theme.Gold);
            return;
        }

        var text = scan.Text.Length > 0 ? scan.Text : Loc.T("Opening your closet…");
        var lh   = _c.LineHeight(Theme.Body);
        var y    = r.Center.Y - P(30);
        var w    = Math.Min(P(260), r.W - P(48));
        foreach (var l in Painter.Wrap(_c, text, r.W - P(48), Theme.Body, 2))
        {
            Painter.TextIn(_c, new Rect(r.X, y, r.W, lh), l, Theme.Body, Theme.Muted, 0.5f);
            y += lh;
        }

        if (scan.Total <= 0)
            return;
        var track = new Rect(r.Center.X - w / 2, y + P(12), w, P(4));
        _c.FillRect(track, Theme.Edge, P(2));
        _c.FillRect(track with { W = track.W * Math.Clamp(scan.Done / (float)scan.Total, 0, 1) }, Theme.Accent.Fade(0.8f), P(2));
    }
}
