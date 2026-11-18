using System.Numerics;
using ModCloset2.Core;
using ModCloset2.Ui.Canvas;
using ModCloset2.Ui.Widgets;

namespace ModCloset2.Ui.Face;

/// <summary>
/// The filters in the heading row, after the kind chips: Slot, Body and Race, each one dropdown button ("Body: All ▾").
/// Its menu lists All, every chip with its count and, after a line, "Any body"/"Any race" (the cards tied to none); a
/// pick closes it and the button then shows just the pick in the chosen style, with a small × back to all. A filter
/// without chips is hidden (unless something is chosen in it). When the buttons do not all fit, they fold into one
/// "Filters ▾" button whose menu has the three sections, and the picks show as small × chips after it.
/// </summary>
public sealed partial class WindowFace
{
    private const string PopBodyChips = "body-chips";
    private const string PopRaceChips = "race-chips";
    private const string PopFilters   = "filters";

    /// <summary> One filter: its name in places ("slot"), icon, chips, the chosen id, its texts and its menu. </summary>
    private sealed record FilterDef(string Id, string Icon, string Pop, Func<IReadOnlyList<KindChip>> Chips, Func<string> Get, Action<string> Set,
        Func<string, string> Label, Func<string> AnyText, Func<string> AllTip);

    private FilterDef[]? _filters;

    private FilterDef[] Filters
        => _filters ??= new[]
        {
            new FilterDef("slot", Icons.Slots, PopSlotChips, () => _s.SlotChips, () => _s.SlotChip, v => _s.SlotChip = v,
                v => Loc.F("Slot: {0}", v), () => string.Empty, () => Loc.T("All slots")),
            new FilterDef("body", Icons.Bodies, PopBodyChips, () => _s.BodyChips, () => _s.BodyChip, v => _s.BodyChip = v,
                v => Loc.F("Body: {0}", v), () => Loc.T("Any body"), () => Loc.T("All bodies")),
            new FilterDef("race", Icons.Face, PopRaceChips, () => _s.RaceChips, () => _s.RaceChip, v => _s.RaceChip = v,
                v => Loc.F("Race: {0}", v), () => Loc.T("Any race"), () => Loc.T("All races")),
        };

    private bool FilterShown(FilterDef f)
        => f.Chips().Count > 0 || f.Get().Length > 0;

    private List<FilterDef> ShownFilters()
        => Filters.Where(FilterShown).ToList();

    /// <summary> A chip's name in a filter: "Any body" for the cards tied to none, else the session's name. </summary>
    private static string ChipName(FilterDef f, KindChip k)
        => k.Id == "any" && f.AnyText().Length > 0 ? f.AnyText() : k.Name;

    /// <summary> "Body: All" while nothing is chosen, else just the pick ("Rue"). </summary>
    private string FilterLabel(FilterDef f)
    {
        var chosen = f.Get();
        if (chosen.Length == 0)
            return f.Label(Loc.T("All"));
        return f.Chips().FirstOrDefault(k => k.Id == chosen) is { } k ? ChipName(f, k) : chosen == "any" && f.AnyText().Length > 0 ? f.AnyText() : chosen;
    }

    private float FilterWidth(FilterDef f)
        => P(12) + P(14) + P(6) + _c.Measure(FilterLabel(f), Theme.Caption).X + P(8) + (f.Get().Length > 0 ? P(22) : P(18)) + P(4);

    private float FiltersButtonWidth()
        => P(12) + P(14) + P(6) + _c.Measure(Loc.T("Filters"), Theme.Caption).X + P(8) + P(18) + P(4);

    /// <summary> The width the filters take side by side (each with its gap), or folded (the button and the picks). </summary>
    private float FiltersWidth(List<FilterDef> shown, bool folded)
        => shown.Count == 0 ? 0
            : folded ? FiltersButtonWidth() + P(10) + shown.Where(f => f.Get().Length > 0).Sum(f => FilterWidth(f) + P(8))
            : shown.Sum(f => FilterWidth(f) + P(10));

    /// <summary> Folding only helps with two or more filters; one alone keeps its own button. </summary>
    private static bool CanFold(List<FilterDef> shown)
        => shown.Count >= 2;

    /// <summary> Draws the filters in a row ending at <paramref name="right"/> (or starting at <paramref name="left"/>); returns the width used. </summary>
    private float DrawFilters(List<FilterDef> shown, bool folded, float y, float h, float? left = null, float? right = null)
    {
        var total = FiltersWidth(shown, folded) - P(10);
        var x     = left ?? right!.Value - total;
        if (folded)
        {
            var bw = FiltersButtonWidth();
            FiltersButton(new Rect(x, y, bw, h), shown);
            x += bw + P(10);
            foreach (var f in shown.Where(f => f.Get().Length > 0))
            {
                var w = FilterWidth(f);
                FilterButton(f, new Rect(x, y, w, h));
                x += w + P(8);
            }
        }
        else
        {
            foreach (var f in shown)
            {
                var w = FilterWidth(f);
                FilterButton(f, new Rect(x, y, w, h));
                x += w + P(10);
            }
        }

        return total;
    }

    /// <summary> The filters on a compact row of their own (no room beside the heading): side by side when they fit, else folded. </summary>
    private float FilterRow(float x, float y, float w, float h)
    {
        var shown  = ShownFilters();
        var folded = CanFold(shown) && FiltersWidth(shown, false) - P(10) > w;
        DrawFilters(shown, folded, y, h, left: x);
        return h;
    }

    /// <summary>
    /// One filter's button. Nothing chosen: its icon, "Body: All" and ▾, outlined. Chosen: just the pick, filled, with
    /// a × that goes back to all. A click opens its menu (or closes it).
    /// </summary>
    private void FilterButton(FilterDef f, Rect box)
    {
        var id     = f.Id + "-filter";
        var clear  = f.Id + "-clear";
        Stats.Places[id] = box;
        var chosen = f.Get().Length > 0;
        var label  = FilterLabel(f);
        var close  = chosen ? new Rect(box.Right - P(26), box.Y + (box.H - P(22)) / 2, P(22), P(22)) : default;
        if (chosen)
            Stats.Places[clear] = close;
        var cresp = chosen ? _ui.Interact(clear, close) : default;
        var resp  = _ui.Interact(id, box);
        var open  = _ui.IsOpen(f.Pop);
        _c.FillRect(box, chosen || open ? Theme.Picked : resp.Hovered ? Theme.HoverWash : Colr.Transparent, box.H / 2);
        if (!chosen && !open)
            _c.StrokeRect(box, Theme.Edge, box.H / 2, Math.Max(1, _u));
        Painter.Icon(_c, f.Icon, new Vector2(box.X + P(12) + P(7), box.Center.Y), P(14), chosen ? Theme.Accent : Theme.Muted, 1.8f);
        var tx    = box.X + P(12) + P(14) + P(6);
        var right = chosen ? close.X - P(2) : box.Right - P(26);
        Painter.TextIn(_c, new Rect(tx, box.Y, right - tx, box.H), label, Theme.Caption, Theme.Text);
        if (chosen)
        {
            if (cresp.Hovered)
                _c.FillRect(close, Theme.HoverWash, P(11));
            Painter.Icon(_c, Icons.Close, close.Center, P(11), cresp.Hovered ? Theme.Text : Theme.Muted, 2);
            _ui.Tip(clear, close, f.AllTip());
            if (cresp.Clicked)
            {
                f.Set(string.Empty);
                return;
            }
        }
        else
            Painter.Icon(_c, Icons.ChevronD, new Vector2(box.Right - P(16), box.Center.Y), P(12), Theme.Muted, 2);

        if (resp.Clicked && !cresp.Hovered)
        {
            if (open)
                _ui.ClosePopup();
            else
                _ui.OpenPopup(f.Pop, new Vector2(box.X, box.Bottom + P(6)));
        }
    }

    /// <summary> A filter's rows: All, every chip with its count, then (after a line) the cards tied to none. </summary>
    private List<MenuItem> FilterItems(FilterDef f, string prefix)
    {
        var chosen = f.Get();
        var chips  = f.Chips();
        var items  = new List<MenuItem> { new(prefix, Loc.T("All")) { Checked = chosen.Length == 0 } };
        items.AddRange(chips.Where(k => k.Id != "any").Select(k => new MenuItem(prefix + k.Id, $"{ChipName(f, k)} {Num(k.Count)}") { Checked = k.Id == chosen }));
        if (chips.FirstOrDefault(k => k.Id == "any") is { } any)
        {
            items.Add(MenuItem.Separator());
            items.Add(new MenuItem(prefix + any.Id, $"{ChipName(f, any)} {Num(any.Count)}") { Checked = any.Id == chosen });
        }

        return items;
    }

    /// <summary> One filter's menu (from its own button). </summary>
    private void FilterMenu(FilterDef f)
    {
        var prefix = f.Id + ":";
        var picked = Menu.Draw(_ui, f.Pop, _ui.PopupAnchor, FilterItems(f, prefix), 180);
        if (picked is not null && picked.Id.StartsWith(prefix, StringComparison.Ordinal))
            f.Set(picked.Id[prefix.Length..]);
    }

    /// <summary> "Filters ▾": the folded filters; its menu has a section per filter. </summary>
    private void FiltersButton(Rect box, List<FilterDef> shown)
    {
        Stats.Places["filters"] = box;
        var resp = _ui.Interact("filters", box);
        var open = _ui.IsOpen(PopFilters);
        var any  = shown.Any(f => f.Get().Length > 0);
        _c.FillRect(box, open ? Theme.Picked : resp.Hovered ? Theme.HoverWash : Colr.Transparent, box.H / 2);
        if (!open)
            _c.StrokeRect(box, any ? Theme.Accent.Fade(0.6f) : Theme.Edge, box.H / 2, Math.Max(1, _u));
        Painter.Icon(_c, Icons.Filter, new Vector2(box.X + P(12) + P(7), box.Center.Y), P(14), any ? Theme.Accent : Theme.Muted, 1.8f);
        var tx = box.X + P(12) + P(14) + P(6);
        Painter.TextIn(_c, new Rect(tx, box.Y, box.Right - P(26) - tx, box.H), Loc.T("Filters"), Theme.Caption, Theme.Text);
        Painter.Icon(_c, Icons.ChevronD, new Vector2(box.Right - P(16), box.Center.Y), P(12), Theme.Muted, 2);
        if (!resp.Clicked)
            return;
        if (open)
            _ui.ClosePopup();
        else
            _ui.OpenPopup(PopFilters, new Vector2(box.X, box.Bottom + P(6)));
    }

    private void FiltersMenu()
    {
        var items = new List<MenuItem>();
        foreach (var f in ShownFilters())
        {
            if (items.Count > 0)
                items.Add(MenuItem.Separator());
            items.Add(MenuItem.Header(f.Label(string.Empty).TrimEnd(' ', ':')));
            items.AddRange(FilterItems(f, f.Id + ":"));
        }

        // (a menu taller than the window scrolls, as every menu does)
        var picked = Menu.Draw(_ui, PopFilters, _ui.PopupAnchor, items, 200);
        if (picked is null)
            return;
        foreach (var f in Filters)
            if (picked.Id.StartsWith(f.Id + ":", StringComparison.Ordinal))
                f.Set(picked.Id[(f.Id.Length + 1)..]);
    }

    /// <summary> "Body: Rue · Race: Miqo'te" for the card's tooltip, or "" when neither is known. </summary>
    private static string BodyRaceLine(Card card)
    {
        var parts = new List<string>(2);
        if (card.Bodies.Count > 0)
            parts.Add(Loc.F("Body: {0}", string.Join(", ", card.Bodies)));
        if (card.Races.Count > 0)
            parts.Add(Loc.F("Race: {0}", string.Join(", ", card.Races)));
        return string.Join(" · ", parts);
    }
}
