using System.Numerics;
using ModCloset2.Core;
using ModCloset2.Ui.Canvas;
using ModCloset2.Ui.Widgets;

namespace ModCloset2.Ui.Face;

public sealed partial class WindowFace
{
    /// <summary> Slim: heading, the "Wearing" strip, two columns of cards, the stacked bar. </summary>
    private void ShelfSlim(Rect area)
    {
        Stats.Places["right"] = area;
        if (_s.Rail.Count == 0 && !Searching)
        {
            Message(area, Loc.T("No mods yet."), Loc.T("Mods you add in Penumbra show up here."), Icons.Other, Theme.Muted);
            return;
        }

        if (!Searching && _view == View.Colours && _s.Colours is { } oc)
        {
            ColoursSlim(area, oc);
            return;
        }

        if (!Searching && _view == View.Looks && _s.Looks is { } looks)
        {
            LooksSlim(area, looks);
            return;
        }

        if (_s.Selected == Pieces.ToSort && !Searching)
        {
            SortSlim(area);
            return;
        }

        var header = new Rect(area.X + P(16), area.Y + P(10), area.W - P(32), P(32));
        Header(header, true);
        var y = header.Bottom + P(8);
        var x = area.X + P(12);
        var w = area.W - P(24);
        if (_s.Suggestion is { } sug)
            y += Banner(new Rect(x, y, w, 0), sug, true) + P(10);

        if (!Searching && IsWornPlace(_s.Selected))
        {
            var h = _s.Wearing is null ? P(44) : P(60);
            WearingSlim(new Rect(x, y, w, h));
            y += h;
        }

        var barH = SlimBarHeight(w);
        var bar  = new Rect(x, area.Bottom - P(12) - barH, w, barH);
        Grid(new Rect(x, y, w, bar.Y - y), true);
        TryBarSlim(bar);
        Toast(new Rect(x, area.Y, w, area.H), bar.Y);
    }

    private void WearingSlim(Rect r)
    {
        Stats.Places["wearing"] = r;
        _c.FillRect(r, Theme.Rail, P(12));
        var (wearing, shownIndex, shownCount) = ShownWearing();
        if (wearing is null)
        {
            Painter.TextIn(_c, r.Inset(P(14), 0), Loc.T("Nothing on here right now."), Theme.Caption, Theme.Muted);
            return;
        }

        var switchW = WornSwitch(r.X + P(10), r.Center.Y, shownIndex, shownCount, true);
        var pic     = new Rect(r.X + P(10) + (switchW > 0 ? switchW + P(10) : 0), r.Center.Y - P(20), P(40), P(40));
        PictureTile("wearing/" + wearing.Key, wearing.Key, pic, wearing.Picture, wearing.Icon, wearing.Name, P(9), Corners.All, P(22), 1.4f, Colr.Hex(0xF4ECF2, 0.45f));
        // slim has no "Nothing" card: taking off is this small button at the end of the strip (not on Skin)
        var right0 = r.Right - P(10);
        if (CanTakeOff(_s.Selected))
        {
            var off     = new Rect(r.Right - P(10) - P(30), r.Center.Y - P(15), P(30), P(30));
            var offResp = _ui.Interact("take-off", off);
            var taking  = TakingOffHere();
            if (taking || offResp.Hovered)
                _c.FillRect(off, Theme.Picked, P(8));
            if (taking)
                _c.StrokeRect(off, Theme.Accent, P(8), P(1.5f));
            Painter.Icon(_c, Icons.Nothing, off.Center, P(18), taking ? Theme.Accent : offResp.Hovered ? Theme.Text : Theme.Muted, 1.6f);
            _ui.Tip("take-off", off, Loc.T("Take it off"));
            Stats.Places["nothing"] = off;
            if (offResp.Clicked && !taking)
                _s.TryNothing(_s.Selected);
            right0 = off.X - P(8);
        }

        var x      = pic.Right + P(12);
        var right  = right0;
        var chips  = WearingChips(wearing);
        var live   = ShownColours(wearing, shownIndex);
        var hasBar = live is not null;
        var second = hasBar ? P(12) : chips.Count > 0 ? P(24) : 0;
        var lh     = _c.LineHeight(BodyBoldSlim);
        var top    = r.Center.Y - (lh + (second > 0 ? P(6) + second : 0)) / 2;
        var label  = WearingLabel(wearing, shownCount, true);
        var lw     = Math.Min(_c.Measure(label, Theme.Label).X, (right - x) / 2);
        Painter.TextIn(_c, new Rect(x, top + P(1), lw, lh), label, Theme.Label, Theme.Muted);
        Painter.TextIn(_c, new Rect(x + lw + P(8), top, right - x - lw - P(8), lh), wearing.Name, BodyBoldSlim, Theme.Text);
        var y = top + lh + P(6);
        if (hasBar)
            LiveBar(live!, new Rect(x, y, right - x, P(12)), P(6), Theme.Rail, wholeButton: true);
        else if (chips.Count > 0)
            ChipRow(new Rect(x, y, right - x, P(24)), wearing, chips);
    }

    private static readonly TextStyle BodyBoldSlim = Theme.BodyBold;
}
