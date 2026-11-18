using System.Numerics;
using ModCloset2.Core;
using ModCloset2.Ui.Canvas;
using ModCloset2.Ui.Widgets;

namespace ModCloset2.Ui.Face;

public sealed partial class WindowFace
{
    private static readonly (PieceGroup Group, string Label)[] Groups =
    {
        (PieceGroup.Top, string.Empty),
        (PieceGroup.Clothes, "Clothes"),
        (PieceGroup.Jewellery, "Jewellery"),
        (PieceGroup.Body, "Body"),
        (PieceGroup.Mine, "Mine"),
    };

    /// <summary> The rail: the outfit, shelf by shelf. The upper groups scroll when they do not fit; the bottom group stays put. </summary>
    private void Rail(Rect r)
    {
        _c.FillRect(r, Theme.Rail, BottomRounding, Corners.BottomLeft);
        _c.FillRect(new Rect(r.Right - 1, r.Y, 1, r.H), Theme.Line);
        Stats.Rail = r;
        var rows = _s.Rail;
        if (rows.Count == 0)
            return;

        var inner    = new Rect(r.X + P(10), r.Y + P(12), r.W - P(20) - 1, r.H - P(24));
        var bottom   = rows.Where(x => x.Group == PieceGroup.Bottom).ToList();
        var scan     = _s.Scan is { Ready: true, Text.Length: > 0 };
        var bottomH  = bottom.Count > 0 ? bottom.Count * P(32) - P(2) : 0;
        var scanLines = scan ? Painter.Wrap(_c, _s.Scan.Text, inner.W - P(16), Theme.Small, 2) : new List<string>();
        var scanH    = scan ? P(12) + scanLines.Count * _c.LineHeight(Theme.Small) + (_s.Scan.Total > 0 ? P(10) : 0) : 0;
        var upperBox = new Rect(inner.X, inner.Y, inner.W, inner.H - bottomH - scanH - (bottomH > 0 ? P(12) : 0));

        // the upper part as a list of labels and rows; on top the outfit colours and the looks, when there are any
        var items = new List<(string? Label, RailRow? Row, float H)>();
        var oc    = _s.Colours;
        if (oc is not null)
            items.Add((ColoursId, null, P(50)));
        if (_s.Looks is { } looks)
            items.Add((null, LooksRow(looks), P(32)));
        foreach (var (group, label) in Groups)
        {
            var inGroup = rows.Where(x => x.Group == group).ToList();
            if (inGroup.Count == 0)
                continue;
            if (label.Length > 0)
                items.Add((Loc.T(label), null, P(26)));
            foreach (var row in inGroup)
                items.Add((null, row, P(32)));
        }

        var contentH = items.Sum(i => i.H);
        var view     = new Rect(r.X, upperBox.Y, r.W - 1, upperBox.H);
        var scroll   = _ui.Scroll("rail", view, contentH, P(64));
        _ui.PushClip(view);
        var y = upperBox.Y - scroll;
        foreach (var (label, row, h) in items)
        {
            if (y + h >= view.Y && y <= view.Bottom)
            {
                if (label == ColoursId && oc is not null)
                    ColoursEntry(new Rect(inner.X, y, inner.W, P(44)), oc);
                else if (label is not null)
                    Painter.TextIn(_c, new Rect(inner.X + P(8), y + P(8), inner.W - P(16), P(14)), label, Theme.Label, Theme.Muted);
                else if (row is not null)
                    RailRowWide(row, new Rect(inner.X, y, inner.W, P(30)));
            }

            y += h;
        }

        _ui.PopClip();
        // when the list goes on beyond an edge, it fades into the rail there instead of being cut
        var fade = P(18);
        if (scroll > 0.5f)
            _c.Gradient(view with { H = fade }, Theme.Rail, Theme.Rail, Theme.Rail.Fade(0), Theme.Rail.Fade(0));
        if (scroll < contentH - view.H - 0.5f)
            _c.Gradient(new Rect(view.X, view.Bottom - fade, view.W, fade), Theme.Rail.Fade(0), Theme.Rail.Fade(0), Theme.Rail, Theme.Rail);
        _ui.ScrollBar("rail", view, contentH, 2);

        var by = inner.Bottom - scanH - bottomH;
        foreach (var row in bottom)
        {
            RailRowWide(row, new Rect(inner.X, by, inner.W, P(30)));
            by += P(32);
        }

        if (scan)
            ScanLine(new Rect(inner.X + P(8), inner.Bottom - scanH + P(12), inner.W - P(16), scanH - P(12)), scanLines);
    }

    /// <summary> "Getting to know your mods… 412 of 2,300" with a thin line under it, small and quiet. </summary>
    private void ScanLine(Rect r, List<string> lines)
    {
        var scan = _s.Scan;
        var lh   = _c.LineHeight(Theme.Small);
        var y    = r.Y;
        foreach (var line in lines)
        {
            _c.Text(new Vector2(r.X, y), line, Theme.Small, Theme.Muted);
            y += lh;
        }

        if (scan.Total <= 0)
            return;
        var track = new Rect(r.X, y + P(6), r.W, P(3));
        _c.FillRect(track, Theme.Edge, P(1.5f));
        _c.FillRect(track with { W = track.W * Math.Clamp(scan.Done / (float)scan.Total, 0, 1) }, Theme.Muted, P(1.5f));
    }

    private bool IsPicked(RailRow row)
        => _s.Search.Length == 0 && row.Id switch
        {
            LooksRowId => _view == View.Looks,
            ColoursId  => _view == View.Colours,
            _          => _view == View.Shelf && row.Id == _s.Selected,
        };

    /// <summary> May the card being dragged go onto this shelf? </summary>
    private bool CanDrop(string shelf)
    {
        if (_ui.Drag?.Payload is not Card card || card.Piece == shelf)
            return false;
        if (shelf == Pieces.Hidden)
            return true;
        foreach (var t in _s.MoveTargets)
            if (t.Id == shelf)
                return true;
        return false;
    }

    private void Drop(Card card, string shelf)
    {
        if (shelf == Pieces.Hidden)
            _s.Hide(card.Key);
        else
            _s.Move(card.Key, shelf);
    }

    private void PickShelf(RailRow row)
    {
        if (row.Id is LooksRowId or ColoursId)
        {
            OpenView(row.Id == LooksRowId ? View.Looks : View.Colours);
            return;
        }

        _view = View.Shelf;
        if (_s.Search.Length > 0)
            _s.Search = string.Empty;
        _slimSearch = false;
        if (_s.Selected != row.Id)
            _s.Select(row.Id);
    }

    private void RailRowWide(RailRow row, Rect r)
    {
        Stats.RailRowsDrawn++;
        Stats.Places["rail/" + row.Id] = r;
        var resp = _ui.Interact("rail/" + row.Id, r);
        var drop = false;
        if (CanDrop(row.Id))
        {
            var (over, dropped) = _ui.DropTarget(r);
            drop = over;
            if (dropped && _ui.Drag?.Payload is Card card)
                Drop(card, row.Id);
        }

        var picked = IsPicked(row);
        if (drop)
        {
            _c.FillRect(r, Theme.DropFill, P(8));
            _c.StrokeRect(r, Theme.Accent, P(8), P(1.5f), Corners.All, P(4), P(3));
        }
        else if (picked)
            _c.FillRect(r, Theme.Picked, P(8));
        else if (resp.Hovered)
            _c.FillRect(r, Theme.HoverWash, P(8));

        if (resp.Clicked)
            PickShelf(row);

        var bottom = row.Group == PieceGroup.Bottom;
        var thumb  = new Rect(r.X + P(8), r.Center.Y - P(12), P(24), P(24));
        if (bottom)
        {
            var fg = row.Id == Pieces.ToSort ? Theme.Gold : Theme.Muted;
            Painter.Icon(_c, row.Icon, thumb.Center, P(16), fg, 1.8f);
        }
        else if (row.WornName.Length > 0)
        {
            if (string.IsNullOrEmpty(row.WornPicture) || !_c.Picture(row.WornPicture, thumb, P(6)))
            {
                _c.FillRect(thumb, Theme.Tint(row.WornName), P(6));
                Painter.Icon(_c, row.Icon, thumb.Center, P(14), Theme.Text, 1.8f);
            }
        }
        else
        {
            _c.StrokeRect(thumb, Theme.EdgeStrong, P(6), Math.Max(1, _u), Corners.All, P(3), P(2));
            Painter.Icon(_c, row.Icon, thumb.Center, P(14), Theme.Muted, 1.8f);
        }

        var nameX  = thumb.Right + P(10);
        var accent = picked || drop;
        string tail;
        TextStyle tailStyle;
        Colr tailColor;
        if (drop)
        {
            tail      = Loc.T("Move here");
            tailStyle = Theme.SmallBold;
            tailColor = Theme.Accent;
        }
        else
        {
            tail      = Num(row.Count);
            tailStyle = Theme.Small;
            tailColor = Theme.Muted;
        }

        float tailW;
        if (bottom && row.Badge > 0 && !drop)
            tailW = Painter.Pill(_c, r.Inset(0, 0, P(8), 0), row.Badge.ToString(), Theme.Gold, _u);
        else
        {
            tailW = _c.Measure(tail, tailStyle).X;
            Painter.TextIn(_c, new Rect(r.Right - P(8) - tailW, r.Y, tailW, r.H), tail, tailStyle, tailColor);
        }

        var tailX = r.Right - P(8) - tailW - P(10);
        var nameW = Math.Min(_c.Measure(row.Name, Theme.BodyMedium).X, tailX - nameX);
        Painter.TextIn(_c, new Rect(nameX, r.Y, nameW, r.H), row.Name, Theme.BodyMedium, accent ? Theme.Accent : Theme.Text);
        var wornX = nameX + nameW + P(10);
        var whole = true;

        // "+2" when more is on than the one named: it stays, the name before it shortens (and goes only when not
        // even a few letters fit)
        var more  = !bottom && !drop && row.WornName.Length > 0 && row.WornMore > 0 ? Loc.F("+{0}", row.WornMore) : string.Empty;
        var moreW = more.Length > 0 ? _c.Measure(more, Theme.Small).X : 0;
        var room  = tailX - wornX - (more.Length > 0 ? moreW + P(5) : 0);
        var moreX = wornX;
        if (!bottom && !drop && row.WornName.Length > 0 && room > P(24))
        {
            var drawn = Painter.TextIn(_c, new Rect(wornX, r.Y, room, r.H), row.WornName, Theme.Small, Theme.Muted);
            whole = drawn >= _c.Measure(row.WornName, Theme.Small).X - 0.5f;
            moreX = wornX + drawn + P(5);
        }
        else if (row.WornName.Length > 0 && !bottom)
            whole = false;

        if (more.Length > 0 && moreX + moreW <= tailX + 0.5f)
        {
            var box = new Rect(moreX, r.Y, moreW, r.H);
            Stats.Places["rail-more/" + row.Id] = box;
            Painter.TextIn(_c, box, more, Theme.Small, Theme.Muted);
        }

        // a tooltip when the name of what is on is cut or could not be shown, or when more is on than it says
        if (more.Length > 0)
            _ui.Tip("rail/" + row.Id, r, Loc.F("{0}: {1} (and {2} more)", row.Name, row.WornName, row.WornMore));
        else if (!whole)
            _ui.Tip("rail/" + row.Id, r, Loc.F("{0}: {1}", row.Name, row.WornName));
    }

    // ---- slim: the shelves as a grid of icons ----

    /// <summary> Draws the icon grid under the top bar; returns where the next thing starts. </summary>
    private float IconGrid(Rect area)
    {
        var rows = _s.Rail;
        var list = new List<RailRow>();
        if (_s.Looks is { } looks)
            list.Add(LooksRow(looks));
        if (_s.Colours is not null)
            list.Add(new RailRow { Id = ColoursId, Name = Loc.T("Outfit colours"), Group = PieceGroup.Top, Icon = Icons.Palette });
        foreach (var (group, _) in Groups)
            list.AddRange(rows.Where(x => x.Group == group));
        list.AddRange(rows.Where(x => x.Group == PieceGroup.Bottom));
        if (list.Count == 0)
            return area.Y;

        // as few lines as the width allows, then the icons spread evenly over those lines (18 shelves: 3 × 6)
        var inner = new Rect(area.X + P(12), area.Y + P(12), area.W - P(24), 0);
        var gap   = P(6);
        var most  = Math.Max(4, (int)((inner.W + gap) / (P(40) + gap)));
        var lines0 = (list.Count + most - 1) / most;
        var cols  = (list.Count + lines0 - 1) / lines0;
        var w     = (inner.W - (cols - 1) * gap) / cols;
        var h     = P(40);
        for (var i = 0; i < list.Count; ++i)
        {
            var row = list[i];
            var box = new Rect(inner.X + i % cols * (w + gap), inner.Y + i / cols * (h + gap), w, h);
            IconTile(row, box);
        }

        var lines = (list.Count + cols - 1) / cols;
        return inner.Y + lines * h + (lines - 1) * gap + P(4);
    }

    private void IconTile(RailRow row, Rect box)
    {
        Stats.RailRowsDrawn++;
        Stats.Places["rail/" + row.Id] = box;
        var resp = _ui.Interact("rail/" + row.Id, box);
        var drop = false;
        if (CanDrop(row.Id))
        {
            var (over, dropped) = _ui.DropTarget(box);
            drop = over;
            if (dropped && _ui.Drag?.Payload is Card card)
                Drop(card, row.Id);
        }

        var picked = IsPicked(row);
        _c.FillRect(box, drop ? Theme.DropFill : picked ? Theme.Picked : resp.Hovered ? Colr.Hex(0x2A2131) : Theme.IconTile, P(9));
        if (drop)
            _c.StrokeRect(box, Theme.Accent, P(9), P(1.5f), Corners.All, P(4), P(3));
        var fg = picked || drop ? Theme.Accent : row.Id == Pieces.ToSort ? Theme.Gold : resp.Hovered ? Theme.Text : Theme.Muted;
        Painter.Icon(_c, row.Icon, row.Id == ColoursId ? box.Center - new Vector2(0, P(4)) : box.Center, P(18), fg, 1.7f);
        if (row.WornName.Length > 0 && row.Group != PieceGroup.Bottom)
        {
            if (row.WornMore > 0)
            {
                // more than one thing on: the dot becomes a small "+2" in the same green
                var more = Loc.F("+{0}", row.WornMore);
                var mw   = _c.Measure(more, Theme.Label).X;
                var mb   = new Rect(box.Right - P(3) - mw, box.Y + P(2), mw, _c.LineHeight(Theme.Label));
                Stats.Places["rail-more/" + row.Id] = mb;
                Painter.TextIn(_c, mb, more, Theme.Label, Theme.Good);
            }
            else
                _c.FillCircle(new Vector2(box.Right - P(8), box.Y + P(8)), P(3), Theme.Good);
        }
        if (resp.Clicked)
            PickShelf(row);

        if (row.Id == ColoursId && _s.Colours is { } colours)
        {
            // the outfit colours as four small dots under the palette
            var dx = box.Center.X - P(13.5f);
            foreach (var role in Roles)
            {
                Dot(new Vector2(dx, box.Bottom - P(7)), P(3), colours.Palette.Of(role));
                dx += P(9);
            }
        }

        var tip = row.WornName.Length > 0 && row.WornMore > 0 ? Loc.F("{0}: {1} (and {2} more)", row.Name, row.WornName, row.WornMore)
            : row.WornName.Length > 0 ? Loc.F("{0}: {1}", row.Name, row.WornName)
            : row.Badge > 0 ? Loc.F("{0}: {1} waiting", row.Name, row.Badge)
            : Loc.F("{0} ({1})", row.Name, row.Count);
        _ui.Tip("rail/" + row.Id, box, tip);
    }
}
