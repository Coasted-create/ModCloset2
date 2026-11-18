using System.Numerics;
using ModCloset2.Core;
using ModCloset2.Ui.Canvas;
using ModCloset2.Ui.Widgets;

namespace ModCloset2.Ui.Face;

public sealed partial class WindowFace
{
    // ---- the suggestion after a move ----

    /// <summary> "You moved … / 3 more look the same. [Leave them] [Move them too]". Returns its height. </summary>
    private float Banner(Rect r, MoveSuggestion sug, bool slim)
    {
        var moveText  = Loc.T("Move them too");
        var leaveText = Loc.T("Leave them");
        if (!slim)
        {
            // wide, but the words do not fit next to the buttons: stack it like the slim banner
            var one  = Painter.WrapRich(_c, sug.Text, sug.Name, float.MaxValue, Theme.Body, 1)[0];
            var need = Math.Max(Painter.RichWidth(_c, one, Theme.Body), _c.Measure(sug.Detail, Theme.Caption).X) + P(16 + 14)
             + Painter.ButtonWidth(_c, moveText, Theme.BodyBold, P(20)) + Painter.ButtonWidth(_c, leaveText, Theme.Body, P(16)) + P(8 + 12);
            slim = need > r.W;
        }

        if (slim)
        {
            var lines  = Painter.WrapRich(_c, sug.Text, sug.Name, r.W - P(28), Theme.Body, 3);
            var detail = sug.Detail.Length > 0 ? Painter.Wrap(_c, sug.Detail, r.W - P(28), Theme.Caption, 2) : new List<string>();
            var bh     = _c.LineHeight(Theme.Body);
            var ch     = _c.LineHeight(Theme.Caption);
            var h      = P(12) + lines.Count * bh + (detail.Count > 0 ? P(2) + detail.Count * ch : 0) + P(12) + P(40) + P(12);
            var box    = r with { H = h };
            Stats.Places["banner"] = box;
            _c.FillRect(box, Theme.Raised, P(12));
            _c.StrokeRect(box, Theme.EdgeStrong, P(12), Math.Max(1, _u));
            var y = box.Y + P(12);
            foreach (var l in lines)
            {
                Painter.RichLine(_c, new Vector2(box.X + P(14), y), l, box.W - P(28), Theme.Body, Theme.Text);
                y += bh;
            }

            y += detail.Count > 0 ? P(2) : 0;
            foreach (var l in detail)
            {
                _c.Text(new Vector2(box.X + P(14), y), l, Theme.Caption, Theme.Muted);
                y += ch;
            }

            var half = (box.W - P(24) - P(8)) / 2;
            BannerButtons(new Rect(box.X + P(12), y + P(12), half, P(40)), new Rect(box.X + P(12) + half + P(8), y + P(12), half, P(40)));
            return h;
        }

        var bar = r with { H = P(76) };
        Stats.Places["banner"] = bar;
        _c.FillRect(bar, Theme.Raised, P(12));
        _c.StrokeRect(bar, Theme.EdgeStrong, P(12), Math.Max(1, _u));
        var moveW  = Painter.ButtonWidth(_c, moveText, Theme.BodyBold, P(20));
        var leaveW = Painter.ButtonWidth(_c, leaveText, Theme.Body, P(16));
        var move   = new Rect(bar.Right - P(12) - moveW, bar.Center.Y - P(20), moveW, P(40));
        var leave  = new Rect(move.X - P(8) - leaveW, move.Y, leaveW, P(40));
        BannerButtons(leave, move);

        // the text, then up to three of the look-alikes as small tiles when there is room for them
        var textX  = bar.X + P(16);
        var thumbs = Math.Min(3, Math.Max(sug.Pictures.Count, sug.Count));
        var runs   = Painter.WrapRich(_c, sug.Text, sug.Name, float.MaxValue, Theme.Body, 1)[0];
        var textW  = Math.Max(Painter.RichWidth(_c, runs, Theme.Body), _c.Measure(sug.Detail, Theme.Caption).X);
        var thumbW = thumbs * P(44) + Math.Max(0, thumbs - 1) * P(6);
        var room   = leave.X - P(14) - textX;
        var show   = thumbs > 0 && room >= textW + P(22) + thumbW;
        var colW   = show ? textW : room;
        var lh     = _c.LineHeight(Theme.Body);
        var dh     = _c.LineHeight(Theme.Caption);
        var top    = bar.Center.Y - (lh + P(3) + dh) / 2;
        Painter.RichLine(_c, new Vector2(textX, top), runs, colW, Theme.Body, Theme.Text);
        Painter.TextIn(_c, new Rect(textX, top + lh + P(3), colW, dh), sug.Detail, Theme.Caption, Theme.Muted);
        if (!show)
            return bar.H;
        for (var i = 0; i < thumbs; ++i)
        {
            var pic = i < sug.Pictures.Count ? sug.Pictures[i] : null;
            var box = new Rect(textX + colW + P(22) + i * P(50), bar.Center.Y - P(22), P(44), P(44));
            // no picture: the tile shows the shelf they would move to
            Painter.Tile(_c, box, pic, sug.ToIcon.Length > 0 ? sug.ToIcon : Icons.Outfits, sug.Text + i, P(9), Corners.All, P(24), 1.4f, Colr.Hex(0xF4ECF2, 0.4f));
        }

        return bar.H;
    }

    private void BannerButtons(Rect leave, Rect move)
    {
        if (Painter.Button(_ui, "suggest-leave", leave, Loc.T("Leave them"), ButtonKind.Outline).Clicked)
            _s.DismissSuggestion();
        if (Painter.Button(_ui, "suggest-move", move, Loc.T("Move them too"), ButtonKind.Primary).Clicked)
            _s.AcceptSuggestion();
    }

    // ---- To sort ----

    private void SortWide(Rect r)
    {
        var x      = r.X + P(24);
        var w      = r.W - P(48);
        var header = new Rect(x, r.Y + P(16), w, P(40));
        Header(header, false);
        var y = header.Bottom + P(12);
        if (_s.Suggestion is { } sug)
            y += Banner(new Rect(x, y, w, 0), sug, false);

        var trying = _s.Trying.Active;
        var bottom = trying ? r.Bottom - P(18) - P(60) - P(14) : r.Bottom - P(18);
        var items  = _s.ToSort;
        if (items.Count == 0)
            Message(new Rect(x, y, w, bottom - y), Loc.T("Nothing to sort."), Loc.T("The closet is sure about every mod."), Icons.ToSort, Theme.Gold);
        else
        {
            Painter.TextIn(_c, new Rect(x, y + P(18), w, P(14)), Loc.T("Not sure about these"), Theme.Label, Theme.Muted);
            y += P(18 + 14 + 8);
            SortList(new Rect(x, y, w, bottom - y), items, false);
        }

        if (trying)
            TryBarWide(new Rect(x, r.Bottom - P(18) - P(60), w, P(60)));
        Toast(new Rect(x, r.Y, w, r.H), trying ? r.Bottom - P(18) - P(60) : r.Bottom - P(18));
    }

    private void SortSlim(Rect area)
    {
        var x      = area.X + P(12);
        var w      = area.W - P(24);
        var header = new Rect(area.X + P(16), area.Y + P(10), area.W - P(32), P(32));
        Header(header, true);
        var y = header.Bottom + P(8);
        if (_s.Suggestion is { } sug)
            y += Banner(new Rect(x, y, w, 0), sug, true) + P(4);

        var trying = _s.Trying.Active;
        var barH   = trying ? SlimBarHeight(w) : 0;
        var bottom = trying ? area.Bottom - P(12) - barH - P(12) : area.Bottom - P(12);
        var items  = _s.ToSort;
        if (items.Count == 0)
            Message(new Rect(x, y, w, bottom - y), Loc.T("Nothing to sort."), Loc.T("The closet is sure about every mod."), Icons.ToSort, Theme.Gold);
        else
        {
            Painter.TextIn(_c, new Rect(area.X + P(16), y + P(10), w, P(14)), Loc.T("Not sure about these"), Theme.Label, Theme.Muted);
            y += P(10 + 14 + 8);
            SortList(new Rect(x, y, w, bottom - y), items, true);
        }

        if (trying)
            TryBarSlim(new Rect(x, area.Bottom - P(12) - barH, w, barH));
        Toast(new Rect(x, area.Y, w, area.H), trying ? area.Bottom - P(12) - barH : area.Bottom - P(12));
    }

    // The To sort list can hold thousands of parts: its measurements (the guesses column, each row's height) are made
    // once per list, width, scale and language, and only the rows in sight are drawn (ARCHITECTURE rule 9).
    private IReadOnlyList<SortItem>? _sortFor;
    private int _sortCount;
    private float _sortWidth;
    private float _sortU;
    private int _sortLang;
    private bool _sortSlim;
    private float _sortColumn;
    private bool _sortStacked;
    private float[] _sortTops = Array.Empty<float>();
    private float _sortContent;

    // per row (by SortItem, which the session keeps across its new lists): the guesses' width and the stacked height,
    // so a new list with mostly the same rows measures only the new ones
    private Dictionary<SortItem, (float Guess, float Stacked)> _sortRows = new(ReferenceEqualityComparer.Instance);
    private (float Width, float U, int Lang) _sortRowsFor;

    private void SortLayout(Rect area, IReadOnlyList<SortItem> items, bool slim)
    {
        if (ReferenceEquals(items, _sortFor) && items.Count == _sortCount && area.W == _sortWidth && _u == _sortU && Loc.Version == _sortLang
         && slim == _sortSlim)
            return;
        _sortFor   = items;
        _sortCount = items.Count;
        _sortWidth = area.W;
        _sortU     = _u;
        _sortLang  = Loc.Version;
        _sortSlim  = slim;
        Stats.SortLayouts++;

        if (_sortRowsFor != (area.W, _u, Loc.Version))
        {
            _sortRows.Clear();
            _sortRowsFor = (area.W, _u, Loc.Version);
        }

        var rows = new Dictionary<SortItem, (float Guess, float Stacked)>(items.Count, ReferenceEqualityComparer.Instance);
        var need = new float[items.Count];
        for (var i = 0; i < items.Count; ++i)
        {
            var item = items[i];
            if (!_sortRows.TryGetValue(item, out var m))
            {
                var (best, alt, other) = GuessWidths(item);
                m = (best + alt + other + P(16), StackedSortHeight(item, area.W));
            }

            rows[item] = m;
            need[i]    = m.Stacked;
        }

        _sortRows = rows; // rows gone from the list are forgotten

        // the wide row needs room for the picture, the words, the figure and the guesses side by side
        _sortColumn = P(352);
        foreach (var m in rows.Values)
            _sortColumn = Math.Max(_sortColumn, m.Guess);
        _sortStacked = slim || area.W - (P(12 + 72 + 16 + 16 + 40 + 16 + 14) + _sortColumn) < P(240);
        _sortTops    = new float[items.Count + 1];
        var y = 0f;
        for (var i = 0; i < items.Count; ++i)
        {
            _sortTops[i] = y;
            y += (_sortStacked ? need[i] : P(96)) + P(10);
        }

        _sortTops[items.Count] = y;
        _sortContent           = Math.Max(0, y - P(10));
    }

    private void SortList(Rect area, IReadOnlyList<SortItem> items, bool slim)
    {
        Stats.Grid = area;
        Stats.GridHovered |= area.Contains(_ui.Mouse);
        SortLayout(area, items, slim);
        var view   = new Rect(area.X - P(4), area.Y, area.W + P(8), area.H);
        var scroll = _ui.Scroll("sort", view, _sortContent + P(4), P(100));
        _ui.PushClip(view);

        // the first row in sight, found by halving (rows can differ in height when stacked)
        int lo = 0, hi = items.Count;
        while (lo < hi)
        {
            var mid = (lo + hi) / 2;
            if (_sortTops[mid + 1] - P(10) < scroll)
                lo = mid + 1;
            else
                hi = mid;
        }

        for (var i = lo; i < items.Count && _sortTops[i] - scroll <= area.H; ++i)
        {
            var row = new Rect(area.X, area.Y + _sortTops[i] - scroll, area.W, _sortTops[i + 1] - _sortTops[i] - P(10));
            Stats.SortRowsDrawn++;
            if (_sortStacked)
                SortRowStacked(items[i], row);
            else
                SortRowWide(items[i], row, _sortColumn);
        }

        _ui.PopClip();
        _ui.ScrollBar("sort", new Rect(area.X, area.Y, area.W + (slim ? P(10) : P(18)), area.H), _sortContent + P(4), 6);
    }

    private string GuessLabel(PieceDef g)
        => g.Id.Length == 0 ? Loc.F("New shelf: {0}", g.Name) : Loc.T(g.Name);

    private void Guess(SortItem item, PieceDef g)
    {
        if (g.Id.Length == 0)
        {
            var id = _s.AddPiece(g.Name);
            if (id.Length > 0)
                _s.Move(item.Key, id);
        }
        else
            _s.Move(item.Key, g.Id);
    }

    /// <summary> Slot, Sits, Name as three small chips; they wrap onto a second line when narrow. Returns the height used. </summary>
    private float Witnesses(SortItem item, Rect r, bool draw)
    {
        (string Label, string Value)[] chips =
        {
            (Loc.T("Slot"), item.SlotText), (Loc.T("Sits"), item.SitsText), (Loc.T("Name"), item.WordsText),
        };
        var x    = r.X;
        var y    = r.Y;
        var h    = P(24);
        foreach (var (label, value) in chips)
        {
            if (value.Length == 0)
                continue;
            var lw = _c.Measure(label, Theme.Small).X;
            var vw = _c.Measure(value, Theme.Small).X;
            var w  = Math.Min(P(16) + lw + P(6) + vw, r.W);
            if (x > r.X && x + w > r.Right)
            {
                x =  r.X;
                y += h + P(6);
            }

            if (draw)
            {
                var box = new Rect(x, y, w, h);
                _c.FillRect(box, Theme.Card, P(6));
                Painter.TextIn(_c, new Rect(box.X + P(8), box.Y, lw, h), label, Theme.Small, Theme.Muted);
                Painter.TextIn(_c, new Rect(box.X + P(8) + lw + P(6), box.Y, w - P(16) - lw - P(6), h), value, Theme.Small, Theme.Text);
            }

            x += w + P(6);
        }

        return y + h - r.Y;
    }

    private void SortPicture(SortItem item, Rect pic)
    {
        var id   = "sortpic/" + item.Key;
        var resp = _ui.Interact(id, pic);
        PictureTile("sort/" + item.Key, item.Key, pic, item.Picture, item.Icon, item.Name, P(10), Corners.All, pic.W * 34 / 72, 1.3f, Colr.Hex(0xF4ECF2, 0.34f));
        if (resp.Hovered)
            _c.FillRect(pic, Theme.HoverWash, P(10));
        _ui.Tip(id, pic, Loc.T("Click to try it on"));
        if (resp.Clicked)
            _s.Try(item.Key);
        if (resp.RightClicked)
            _ui.OpenPopup(PopOther, _ui.Mouse, item);
    }

    private (float Best, float Alt, float Other) GuessWidths(SortItem item)
    {
        var best  = item.Guesses.Count > 0 ? Painter.ButtonWidth(_c, GuessLabel(item.Guesses[0]), Theme.BodyBold, P(14)) : 0;
        var alt   = item.Guesses.Count > 1 ? Painter.ButtonWidth(_c, GuessLabel(item.Guesses[1]), Theme.Body, P(14)) : 0;
        var other = _c.Measure(Loc.T("Other"), Theme.Body).X + P(4 + 12 + 16);
        return (best, alt, other);
    }

    private void GuessButtons(SortItem item, Rect best, Rect alt, Rect other)
    {
        Stats.Places["guess0/" + item.Key] = best;
        Stats.Places["guess1/" + item.Key] = alt;
        if (item.Guesses.Count > 0 && Painter.Button(_ui, "guess0/" + item.Key, best, GuessLabel(item.Guesses[0]), ButtonKind.Best).Clicked)
            Guess(item, item.Guesses[0]);
        if (item.Guesses.Count > 1 && Painter.Button(_ui, "guess1/" + item.Key, alt, GuessLabel(item.Guesses[1]), ButtonKind.Outline).Clicked)
            Guess(item, item.Guesses[1]);

        var id   = "other/" + item.Key;
        var resp = _ui.Interact(id, other);
        var open = _ui.IsOpen(PopOther) && ReferenceEquals(_ui.PopupContext, item);
        if (resp.Hovered || open)
            _c.FillRect(other, Theme.Picked, P(9));
        var lw = _c.Measure(Loc.T("Other"), Theme.Body).X;
        Painter.TextIn(_c, new Rect(other.X + P(8), other.Y, lw, other.H), Loc.T("Other"), Theme.Body, resp.Hovered || open ? Theme.Text : Theme.Muted);
        Painter.Icon(_c, Icons.ChevronD, new Vector2(other.X + P(8) + lw + P(4) + P(6), other.Center.Y), P(12), Theme.Muted, 2);
        if (resp.Clicked)
            _ui.OpenPopup(PopOther, new Vector2(other.Right - Menu.Width(_c, _u, OtherMenu(item), 200), other.Bottom + P(6)), item);
    }

    private void SortRowWide(SortItem item, Rect r, float column)
    {
        Stats.Places["sort/" + item.Key] = r;
        _c.FillRect(r, Theme.Rail, P(12));
        var pic = new Rect(r.X + P(12), r.Center.Y - P(36), P(72), P(72));
        SortPicture(item, pic);

        // the guesses sit right-aligned in a column of the same width on every row, so the figures line up
        var (bw, aw, ow) = GuessWidths(item);
        var other = new Rect(r.Right - P(14) - ow, r.Center.Y - P(19), ow, P(38));
        var alt   = new Rect(other.X - P(8) - aw, other.Y, aw, P(38));
        var best  = new Rect((aw > 0 ? alt.X : other.X) - P(8) - bw, other.Y, bw, P(38));
        GuessButtons(item, best, alt, other);

        var fig = new Rect(r.Right - P(14) - column - P(16) - P(40), r.Center.Y - P(32), P(40), P(64));
        Painter.StickFigure(_c, fig, item.Covers);

        var tx  = pic.Right + P(16);
        var tw  = fig.X - P(16) - tx;
        var nh  = _c.LineHeight(Theme.Strong);
        var ch  = Witnesses(item, new Rect(tx, 0, tw, 0), false);
        var top = r.Center.Y - (nh + P(9) + ch) / 2;
        Painter.TextIn(_c, new Rect(tx, top, tw, nh), item.Name, Theme.Strong, Theme.Text);
        Witnesses(item, new Rect(tx, top + nh + P(9), tw, 0), true);
    }

    private float StackedSortHeight(SortItem item, float w)
    {
        var tw    = w - P(12) - P(56) - P(12) - P(30) - P(12) - P(12);
        var ch    = Witnesses(item, new Rect(0, 0, tw, 0), false);
        var block = Math.Max(P(56), _c.LineHeight(Theme.Strong) + P(6) + ch);
        return P(12) + block + P(12) + P(38) + P(12);
    }

    /// <summary> The narrow row: picture, words and figure on top, the guesses in a line under them. </summary>
    private void SortRowStacked(SortItem item, Rect r)
    {
        Stats.Places["sort/" + item.Key] = r;
        _c.FillRect(r, Theme.Rail, P(12));
        var pic = new Rect(r.X + P(12), r.Y + P(12), P(56), P(56));
        SortPicture(item, pic);
        var fig = new Rect(r.Right - P(12) - P(30), r.Y + P(12), P(30), P(48));
        Painter.StickFigure(_c, fig, item.Covers);
        var tx = pic.Right + P(12);
        var tw = fig.X - P(12) - tx;
        var nh = _c.LineHeight(Theme.Strong);
        Painter.TextIn(_c, new Rect(tx, r.Y + P(12), tw, nh), item.Name, Theme.Strong, Theme.Text);
        Witnesses(item, new Rect(tx, r.Y + P(12) + nh + P(6), tw, 0), true);

        // in a wide window the buttons keep their size under the words; slim, the two guesses share the line by how
        // long their words are, and "Other" keeps its own width at the end
        var (bw, aw, ow) = GuessWidths(item);
        var y = r.Bottom - P(12) - P(38);
        Rect best, alt, other;
        if (!IsSlim && bw + aw + ow + P(16) <= r.Right - P(12) - tx)
        {
            best  = new Rect(tx, y, bw, P(38));
            alt   = new Rect(best.Right + P(8), y, aw, P(38));
            other = new Rect((aw > 0 ? alt.Right : best.Right) + P(8), y, ow, P(38));
        }
        else
        {
            other = new Rect(r.Right - P(12) - ow, y, ow, P(38));
            var room  = other.X - P(8) - (r.X + P(12)) - (aw > 0 ? P(8) : 0);
            var total = Math.Max(1, bw + aw);
            var bestW = Math.Min(room, Math.Max(room * bw / total, Math.Min(bw, room / 2)));
            best  = new Rect(r.X + P(12), y, aw > 0 ? bestW : room, P(38));
            alt   = new Rect(best.Right + P(8), y, Math.Max(0, room - best.W), P(38));
        }

        GuessButtons(item, best, alt, other);
    }

    private List<MenuItem> OtherMenu(SortItem item)
    {
        var list = _s.MoveTargets.Select(t => new MenuItem("move:" + t.Id, Loc.T(t.Name)) { Icon = t.Icon }).ToList();
        list.Add(MenuItem.Separator());
        list.Add(new MenuItem("newshelf", Loc.T("New shelf…")) { Icon = Icons.Plus });
        return list;
    }
}
