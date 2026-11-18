using System.Numerics;
using ModCloset2.Ui.Canvas;

namespace ModCloset2.Ui.Widgets;

/// <summary> One row of a popup menu. </summary>
public sealed record MenuItem(string Id, string Label)
{
    public bool Enabled { get; init; } = true;

    /// <summary> A tick in front (choices); null when the row is not a choice. </summary>
    public bool? Checked { get; init; }

    /// <summary> Opens a submenu with these rows (one level deep). </summary>
    public IReadOnlyList<MenuItem>? Sub { get; init; }
    public string? Icon { get; init; }

    /// <summary> The menu stays open after a click (toggles in a multi-choice group). </summary>
    public bool KeepOpen { get; init; }
    public bool IsSeparator { get; init; }
    public bool IsHeader { get; init; }

    /// <summary> A quiet text at the right end of the row ("takes off Pearl drops"). </summary>
    public string? Hint { get; init; }

    /// <summary> A quiet ring where the tick goes: the choice a plain click elsewhere would make (shown when not ticked). </summary>
    public bool Ring { get; init; }

    /// <summary> A quiet line of explanation under the rows before it (wrapped to the menu's width, not clickable). </summary>
    public bool IsNote { get; init; }

    public static MenuItem Separator()
        => new(string.Empty, string.Empty) { IsSeparator = true, Enabled = false };

    public static MenuItem Header(string label)
        => new(string.Empty, label) { IsHeader = true, Enabled = false };

    public static MenuItem Note(string text)
        => new(string.Empty, text) { IsNote = true, Enabled = false };
}

/// <summary>
/// Popup menus in the closet's look (radius 10, card background, 1 px strong edge), with one level of submenu that
/// opens when its row is hovered or clicked. Drawn in the overlay; the caller keeps the popup open with
/// <see cref="Gui.OpenPopup"/> and calls this each frame while it is open.
/// </summary>
public static class Menu
{
    /// <summary> Draws the open menu; returns the row clicked this frame (null for none). </summary>
    public static MenuItem? Draw(Gui ui, string id, Vector2 anchor, IReadOnlyList<MenuItem> items, float minWidth = 180)
    {
        MenuItem? picked = null;
        var rect = Panel(ui, id, anchor, items, minWidth, 0, out var rows);
        for (var i = 0; i < rows.Count; ++i)
        {
            var (item, row) = rows[i];
            if (item.IsSeparator || item.IsHeader || item.IsNote)
                continue;
            var resp = ui.Interact($"{id}/{i}", row, item.Enabled);
            if (resp.Hovered)
                ui.SubmenuIndex = item.Sub is not null ? i : -1;
            if (resp.Clicked && item.Enabled)
            {
                if (item.Sub is not null)
                    ui.SubmenuIndex = i;
                else
                    picked = item;
            }
        }

        if (ui.SubmenuIndex >= 0 && ui.SubmenuIndex < rows.Count && rows[ui.SubmenuIndex].Item.Sub is { } sub)
        {
            var row     = rows[ui.SubmenuIndex].Row;
            Panel(ui, id + "/sub", new Vector2(rect.Right - 4 * ui.U, row.Y - 6 * ui.U), sub, minWidth, rect.X, out var subRows);
            for (var i = 0; i < subRows.Count; ++i)
            {
                var (item, r) = subRows[i];
                if (item.IsSeparator || item.IsHeader || item.IsNote)
                    continue;
                var resp = ui.Interact($"{id}/sub/{i}", r, item.Enabled);
                if (resp.Clicked && item.Enabled)
                    picked = item;
            }
        }

        if (picked is not null && !picked.KeepOpen)
            ui.ClosePopup();
        return picked;
    }

    public static float Width(ICanvas c, float u, IReadOnlyList<MenuItem> items, float minWidth)
    {
        var checks = items.Any(i => i.Checked is not null || i.Ring);
        var subs   = items.Any(i => i.Sub is not null);
        var icons  = items.Any(i => i.Icon is not null);
        var hint   = items.Where(i => !string.IsNullOrEmpty(i.Hint)).Select(i => c.Measure(i.Label, Theme.Body).X + 16 * u + c.Measure(i.Hint!, Theme.Small).X).DefaultIfEmpty(0).Max();
        // a note wraps to the width the other rows give; it does not widen the menu
        var label  = items.Count == 0 ? 0 : items.Where(i => !i.IsNote).Select(i => c.Measure(i.Label, i.IsHeader ? Theme.Label : Theme.Body).X).DefaultIfEmpty(0).Max();
        var w      = Math.Max(label, hint) + 20 * u + 12 * u + (checks ? 24 * u : 0) + (subs ? 22 * u : 0) + (icons ? 26 * u : 0);
        return Math.Clamp(w, minWidth * u, 360 * u);
    }

    private static float RowHeight(ICanvas c, MenuItem item, float u, float w)
        => item.IsSeparator ? 13 * u : item.IsHeader ? 26 * u : item.IsNote ? NoteLines(c, item, u, w).Count * c.LineHeight(Theme.Small) + 8 * u : 30 * u;

    private static List<string> NoteLines(ICanvas c, MenuItem item, float u, float w)
        => Painter.Wrap(c, item.Label, w - 12 * u - 20 * u, Theme.Small, 4);

    /// <summary> Draws the panel and its rows; returns the panel and where each row went (for the clicks). </summary>
    private static Rect Panel(Gui ui, string id, Vector2 anchor, IReadOnlyList<MenuItem> items, float minWidth, float flipLeftOf, out List<(MenuItem Item, Rect Row)> rows)
    {
        var c      = ui.C;
        var u      = ui.U;
        var b      = c.Bounds;
        var w      = Width(c, u, items, minWidth);
        var height = items.Sum(i => RowHeight(c, i, u, w)) + 12 * u;
        var h      = Math.Min(height, b.H - 16 * u);
        var x      = anchor.X;
        var y      = anchor.Y;
        if (x + w > b.Right - 8 * u)
            x = flipLeftOf > 0 ? flipLeftOf - w + 4 * u : b.Right - 8 * u - w;
        x = Math.Max(b.X + 8 * u, x);
        if (y + h > b.Bottom - 8 * u)
            y = flipLeftOf > 0 ? b.Bottom - 8 * u - h : Math.Max(b.Y + 8 * u, anchor.Y - h);
        y = Math.Clamp(y, b.Y + 8 * u, Math.Max(b.Y + 8 * u, b.Bottom - 8 * u - h));

        var rect = new Rect(x, y, w, h);
        ui.PopupArea(rect);
        Painter.Shadow(c, rect, 10 * u, u, 16, 8, 0.55f);
        c.FillRect(rect, Theme.Card, 10 * u);
        c.StrokeRect(rect, Theme.EdgeStrong, 10 * u, Math.Max(1, u));

        var view   = rect.Inset(0, 6 * u);
        var scroll = height > h ? ui.Scroll(id + "#scroll", view, height - 12 * u, 60 * u) : 0;
        var checks = items.Any(i => i.Checked is not null || i.Ring);
        var icons  = items.Any(i => i.Icon is not null);
        rows = new List<(MenuItem, Rect)>(items.Count);
        ui.PushClip(view);
        var ry = view.Y - scroll;
        for (var i = 0; i < items.Count; ++i)
        {
            var item = items[i];
            var rh   = RowHeight(c, item, u, w);
            var row  = new Rect(rect.X + 6 * u, ry, rect.W - 12 * u, rh);
            ry += rh;
            rows.Add((item, row));
            if (row.Bottom < view.Y || row.Y > view.Bottom)
                continue;
            if (item.IsSeparator)
            {
                c.FillRect(new Rect(row.X + 4 * u, row.Y + 6 * u, row.W - 8 * u, Math.Max(1, u)), Theme.Edge);
                continue;
            }

            if (item.IsNote)
            {
                var lh = c.LineHeight(Theme.Small);
                var ly = row.Y + 2 * u;
                foreach (var line in NoteLines(c, item, u, w))
                {
                    Painter.TextIn(c, new Rect(row.X + 10 * u, ly, row.W - 20 * u, lh), line, Theme.Small, Theme.Muted);
                    ly += lh;
                }

                continue;
            }

            if (item.IsHeader)
            {
                Painter.TextIn(c, new Rect(row.X + 10 * u, row.Y + 8 * u, row.W - 20 * u, rh - 10 * u), item.Label, Theme.Label, Theme.Muted);
                continue;
            }

            var hover = item.Enabled && ui.MouseOver(row) || ui.SubmenuIndex == i && item.Sub is not null && id.IndexOf("/sub", StringComparison.Ordinal) < 0;
            if (hover)
                c.FillRect(row, Theme.Picked, 7 * u);
            var fg = item.Enabled ? Theme.Text : Theme.Muted.Fade(0.6f);
            var tx = row.X + 10 * u;
            if (checks)
            {
                if (item.Checked == true)
                    Painter.Icon(c, Icons.Check, new Vector2(tx + 7 * u, row.Center.Y), 15 * u, Theme.Accent, 2.2f);
                else if (item.Ring)
                    c.StrokeCircle(new Vector2(tx + 7 * u, row.Center.Y), 4 * u, Theme.Muted, 1.5f * u);
                tx += 24 * u;
            }

            if (icons)
            {
                if (item.Icon is not null)
                    Painter.Icon(c, item.Icon, new Vector2(tx + 8 * u, row.Center.Y), 16 * u, item.Enabled ? Theme.Muted : Theme.Muted.Fade(0.5f), 1.8f);
                tx += 26 * u;
            }

            var right = row.Right - 10 * u - (item.Sub is not null ? 20 * u : 0);
            if (!string.IsNullOrEmpty(item.Hint))
            {
                // the label keeps what it needs; the hint takes the rest, cut at its end
                var lw    = Math.Min(c.Measure(item.Label, Theme.Body).X, right - tx);
                var hintX = tx + lw + 16 * u;
                if (right - hintX > 24 * u)
                    Painter.TextIn(c, new Rect(hintX, row.Y, right - hintX, rh), item.Hint, Theme.Small, Theme.Muted, 1);
                right = tx + lw;
            }

            Painter.TextIn(c, new Rect(tx, row.Y, right - tx, rh), item.Label, Theme.Body, fg);
            if (item.Sub is not null)
                Painter.Icon(c, Icons.ChevronR, new Vector2(row.Right - 16 * u, row.Center.Y), 14 * u, Theme.Muted, 2);
        }

        ui.PopClip();
        if (height > h)
            ui.ScrollBar(id + "#scroll", view, height - 12 * u, 2);
        return rect;
    }
}
