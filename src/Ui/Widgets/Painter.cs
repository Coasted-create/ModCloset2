using System.Numerics;
using ModCloset2.Ui.Canvas;

namespace ModCloset2.Ui.Widgets;

public enum ButtonKind : byte
{
    /// <summary> Filled accent ("Keep"). </summary>
    Primary,

    /// <summary> Outlined ("Put back"). </summary>
    Outline,

    /// <summary> No frame until hovered ("Other ▾"). </summary>
    Quiet,

    /// <summary> The best guess on To sort: accent outline on a faint accent wash. </summary>
    Best,
}

/// <summary>
/// The closet's widgets, drawn on the canvas: buttons, chips, marks, key caps, tooltips, shadows, the stick figure.
/// All sizes are mockup pixels times U. Each interactive widget takes an id and returns what happened.
/// </summary>
public static class Painter
{
    public const string Ellipsis = "…";

    // ---- text ----

    /// <summary> The text cut with "…" so it fits the width (whole when it fits). </summary>
    public static string Fit(ICanvas c, string text, float width, TextStyle style)
    {
        if (string.IsNullOrEmpty(text) || c.Measure(text, style).X <= width)
            return text;
        if (c.Measure(Ellipsis, style).X > width)
            return string.Empty;

        // longest prefix that fits with the ellipsis (binary search: thousands of cards, few measurements)
        int lo = 0, hi = text.Length;
        while (lo < hi)
        {
            var mid = (lo + hi + 1) / 2;
            if (c.Measure(text[..mid].TrimEnd() + Ellipsis, style).X <= width)
                lo = mid;
            else
                hi = mid - 1;
        }

        return text[..lo].TrimEnd() + Ellipsis;
    }

    /// <summary> Splits text into lines no wider than the width (words kept whole where they fit). </summary>
    public static List<string> Wrap(ICanvas c, string text, float width, TextStyle style, int maxLines = 99)
    {
        var lines = new List<string>();
        var line  = string.Empty;
        foreach (var word in text.Split(' '))
        {
            var test = line.Length == 0 ? word : line + " " + word;
            if (c.Measure(test, style).X <= width || line.Length == 0)
            {
                line = test;
                continue;
            }

            lines.Add(line);
            line = word;
        }

        if (line.Length > 0)
            lines.Add(line);
        if (lines.Count > maxLines)
        {
            lines[maxLines - 1] = string.Join(' ', lines.Skip(maxLines - 1));
            lines.RemoveRange(maxLines, lines.Count - maxLines);
        }

        for (var i = 0; i < lines.Count; ++i)
            lines[i] = Fit(c, lines[i], width, style);
        return lines;
    }

    /// <summary>
    /// A sentence with one stretch set off in bold (the moved name inside "You moved … from Tails to Ears."), wrapped
    /// into lines of runs. Words are kept whole; a word belongs to the bold stretch when it lies inside it.
    /// </summary>
    public static List<List<(string Text, bool Bold)>> WrapRich(ICanvas c, string text, string bold, float width, TextStyle style, int maxLines = 99)
    {
        var start = bold.Length > 0 ? text.IndexOf(bold, StringComparison.Ordinal) : -1;
        var words = new List<(string Word, bool Bold)>();
        var at    = 0;
        foreach (var w in text.Split(' '))
        {
            var inBold = start >= 0 && at >= start && at + w.Length <= start + bold.Length;
            words.Add((w, inBold));
            at += w.Length + 1;
        }

        var lines = new List<List<(string Word, bool Bold)>>();
        var line  = new List<(string Word, bool Bold)>();
        var lineW = 0f;
        var space = c.Measure(" ", style).X;
        foreach (var (w, b) in words)
        {
            var ww = c.Measure(w, b ? style.With(Weight.Bold) : style).X;
            if (line.Count > 0 && lineW + space + ww > width && lines.Count < maxLines - 1)
            {
                lines.Add(line);
                line  = new List<(string, bool)>();
                lineW = 0;
            }

            lineW += (line.Count > 0 ? space : 0) + ww;
            line.Add((w, b));
        }

        if (line.Count > 0)
            lines.Add(line);

        // runs: neighbouring words of the same weight drawn as one text
        var result = new List<List<(string, bool)>>();
        foreach (var l in lines)
        {
            var runs = new List<(string Text, bool Bold)>();
            foreach (var (w, b) in l)
            {
                if (runs.Count > 0 && runs[^1].Bold == b)
                    runs[^1] = (runs[^1].Text + " " + w, b);
                else
                {
                    // the space between two runs goes with the plain one, so the bold run is exactly the name
                    if (runs.Count > 0 && !runs[^1].Bold)
                        runs[^1] = (runs[^1].Text + " ", false);
                    runs.Add((runs.Count > 0 && runs[^1].Bold ? " " + w : w, b));
                }
            }

            result.Add(runs);
        }

        return result;
    }

    public static float RichWidth(ICanvas c, IReadOnlyList<(string Text, bool Bold)> runs, TextStyle style)
        => runs.Sum(r => c.Measure(r.Text, r.Bold ? style.With(Weight.Bold) : style).X);

    /// <summary> Draws one line of runs; a line wider than <paramref name="width"/> is cut at its end with "…". </summary>
    public static void RichLine(ICanvas c, Vector2 pos, IReadOnlyList<(string Text, bool Bold)> runs, float width, TextStyle style, Colr color)
    {
        var x = pos.X;
        foreach (var (text, bold) in runs)
        {
            var st   = bold ? style.With(Weight.Bold) : style;
            var room = pos.X + width - x;
            if (room <= 0)
                break;
            var fit = Fit(c, text, room, st);
            c.Text(new Vector2(x, pos.Y), fit, st, color);
            x += c.Measure(fit, st).X;
            if (fit != text)
                break;
        }
    }

    public static float TextW(ICanvas c, string text, TextStyle style)
        => c.Measure(text, style).X;

    /// <summary> One line, vertically centred in the box; cut to its width. Returns the width drawn. </summary>
    public static float TextIn(ICanvas c, Rect box, string text, TextStyle style, Colr color, float align = 0)
    {
        var fitted = Fit(c, text, box.W + 0.5f, style);
        var w      = c.Measure(fitted, style).X;
        var lh     = c.LineHeight(style);
        c.Text(new Vector2(box.X + (box.W - w) * align, box.Y + (box.H - lh) / 2), fitted, style, color);
        return w;
    }

    // ---- shapes ----

    /// <summary> A soft drop shadow: a few widening, fading rounded rectangles (the same in both backends). </summary>
    public static void Shadow(ICanvas c, Rect r, float radius, float u, float blur = 18, float offsetY = 10, float strength = 0.5f)
    {
        const int layers = 6;
        for (var i = layers; i >= 1; --i)
        {
            var grow = blur * u * i / layers;
            var a    = strength / layers * 0.9f;
            c.FillRect(r.Expand(grow * 0.6f).Offset(0, offsetY * u * i / layers), Colr.Hex(0x000000, a), radius + grow * 0.6f);
        }
    }

    /// <summary> An icon centred in a box of the given size. </summary>
    public static void Icon(ICanvas c, string key, Vector2 center, float size, Colr color, float stroke)
        => c.Shape(Icons.Get(key), new Rect(center.X - size / 2, center.Y - size / 2, size, size), color, stroke);

    /// <summary> A picture tile: the picture when there is one and it has arrived, otherwise a tint with the shelf's icon. </summary>
    public static void Tile(ICanvas c, Rect r, string? picture, string icon, string tintKey, float radius, Corners corners, float iconSize, float iconStroke, Colr? iconColor = null)
    {
        if (!string.IsNullOrEmpty(picture) && c.Picture(picture, r, radius, corners))
            return;
        c.FillRect(r, Theme.Tint(tintKey), radius, corners);
        Icon(c, icon, r.Center, iconSize, iconColor ?? Theme.TileIcon, iconStroke);
    }

    // ---- buttons ----

    public static float ButtonWidth(ICanvas c, string label, TextStyle style, float padX)
        => c.Measure(label, style).X + 2 * padX;

    public static Response Button(Gui ui, string id, Rect r, string label, ButtonKind kind, bool enabled = true, TextStyle? style = null, float radius = 9)
    {
        var c    = ui.C;
        var u    = ui.U;
        var resp = enabled ? ui.Interact(id, r) : new Response();
        var st   = style ?? (kind is ButtonKind.Primary or ButtonKind.Best ? Theme.BodyBold : Theme.Body);
        var rad  = radius * u;
        Colr fg;
        switch (kind)
        {
            case ButtonKind.Primary:
                c.FillRect(r, enabled ? resp.Hovered ? Colr.Hex(0xF5B9CD) : Theme.Accent : Theme.Accent.Fade(0.4f), rad);
                fg = Theme.OnAccent;
                break;
            case ButtonKind.Best:
                c.FillRect(r, Theme.Accent.Fade(resp.Hovered ? 0.18f : 0.1f), rad);
                c.StrokeRect(r, Theme.Accent, rad, Math.Max(1, u));
                fg = Theme.Accent;
                break;
            case ButtonKind.Outline:
                if (resp.Hovered)
                    c.FillRect(r, Theme.Picked, rad);
                c.StrokeRect(r, Theme.EdgeStrong, rad, Math.Max(1, u));
                fg = Theme.Text;
                break;
            default:
                if (resp.Hovered)
                    c.FillRect(r, Theme.Picked, rad);
                fg = resp.Hovered ? Theme.Text : Theme.Muted;
                break;
        }

        if (!enabled)
            fg = fg.Fade(0.45f);
        TextIn(c, r.Inset(6 * u, 0), label, st, fg, 0.5f);
        return resp;
    }

    /// <summary> A square icon button (36 × 36 in the top bar). </summary>
    public static Response IconButton(Gui ui, string id, Rect r, string icon, string tip, bool enabled = true, bool picked = false, params string[] keys)
    {
        var c    = ui.C;
        var u    = ui.U;
        var resp = ui.Interact(id, r, enabled);
        if (!enabled)
            resp = resp with { Clicked = false, Pressed = false };
        c.FillRect(r, picked ? Theme.Picked : resp.Hovered && enabled ? Theme.Picked : Theme.Card, 9 * u);
        var fg = picked ? Theme.Accent : !enabled ? Theme.Muted.Fade(0.4f) : resp.Hovered ? Theme.Text : Theme.Muted;
        Icon(c, icon, r.Center, 16 * u, fg, 1.8f);
        ui.Tip(id, r, tip, keys);
        return resp;
    }

    // ---- chips and marks ----

    /// <summary> How wide <see cref="Mark"/> draws a mark. </summary>
    public static float MarkWidth(ICanvas c, string text, float u)
        => c.Measure(text, Theme.Mark).X + 14 * u;

    /// <summary> A mark on a picture: "On", "Trying on", "New" (filled) at a top-left point. Returns its width. </summary>
    public static float Mark(ICanvas c, Vector2 at, string text, Colr fill, float u)
    {
        var w = c.Measure(text, Theme.Mark).X + 14 * u;
        var r = new Rect(at.X, at.Y, w, 20 * u);
        c.FillRect(r, fill, 6 * u);
        TextIn(c, r, text, Theme.Mark, Theme.OnAccent, 0.5f);
        return w;
    }

    /// <summary> A dark see-through note at the bottom right of a picture ("6 versions"). </summary>
    public static Rect Note(ICanvas c, Vector2 bottomRight, string text, float u, float maxW)
    {
        var w = Math.Min(maxW, c.Measure(text, Theme.MarkPlain).X + 14 * u);
        var r = new Rect(bottomRight.X - w, bottomRight.Y - 20 * u, w, 20 * u);
        c.FillRect(r, Theme.NoteFill, 6 * u);
        TextIn(c, r.Inset(7 * u, 0), text, Theme.MarkPlain, Theme.Text, 0.5f);
        return r;
    }

    /// <summary> A count pill (the gold "14" of To sort). </summary>
    public static float Pill(ICanvas c, Rect row, string text, Colr fill, float u)
    {
        var w = c.Measure(text, Theme.SmallBold).X + 14 * u;
        var r = new Rect(row.Right - w, row.Y + (row.H - 20 * u) / 2, w, 20 * u);
        c.FillRect(r, fill, 10 * u);
        TextIn(c, r, text, Theme.SmallBold, Theme.OnAccent, 0.5f);
        return w;
    }

    /// <summary> Small boxed key names ("Ctrl" "Z") after a tooltip or label. Returns the width. </summary>
    public static float KeyCaps(ICanvas c, Vector2 at, float height, IReadOnlyList<string> keys, float u, bool draw = true)
    {
        var x = at.X;
        foreach (var k in keys)
        {
            var w = c.Measure(k, Theme.MarkPlain).X + 10 * u;
            if (draw)
            {
                var r = new Rect(x, at.Y + (height - 18 * u) / 2, w, 18 * u);
                c.FillRect(r, Theme.Rail, 5 * u);
                c.StrokeRect(r, Theme.EdgeStrong, 5 * u, Math.Max(1, u));
                TextIn(c, r, k, Theme.MarkPlain, Theme.Muted, 0.5f);
            }

            x += w + 4 * u;
        }

        return keys.Count == 0 ? 0 : x - at.X - 4 * u;
    }

    /// <summary> The tooltip box near a thing (below it, kept inside the window). </summary>
    public static void Tooltip(Gui ui, string text, IReadOnlyList<string> keys, Rect near)
    {
        var c     = ui.C;
        var u     = ui.U;
        var maxW  = Math.Min(320 * u, c.Bounds.W - 16 * u);
        var lines = Wrap(c, text, maxW - 20 * u, Theme.Caption);
        var lh    = c.LineHeight(Theme.Caption);
        var keysW = KeyCaps(c, Vector2.Zero, 0, keys, u, false);
        var textW = lines.Count == 0 ? 0 : lines.Max(l => c.Measure(l, Theme.Caption).X);
        var w     = Math.Min(maxW, textW + (keysW > 0 ? keysW + 10 * u : 0)) + 20 * u;
        var h     = lines.Count * lh + 12 * u;
        var x     = Math.Clamp(near.Center.X - w / 2, c.Bounds.X + 8 * u, c.Bounds.Right - w - 8 * u);
        var y     = near.Bottom + 6 * u;
        if (y + h > c.Bounds.Bottom - 4 * u)
            y = near.Y - h - 6 * u;
        var box = new Rect(x, y, w, h);
        Shadow(c, box, 8 * u, u, 10, 4, 0.35f);
        c.FillRect(box, Theme.Raised, 8 * u);
        c.StrokeRect(box, Theme.EdgeStrong, 8 * u, Math.Max(1, u));
        for (var i = 0; i < lines.Count; ++i)
            c.Text(new Vector2(box.X + 10 * u, box.Y + 6 * u + i * lh), lines[i], Theme.Caption, Theme.Text);
        if (keysW > 0)
            KeyCaps(c, new Vector2(box.Right - 10 * u - keysW, box.Y + 6 * u + (lines.Count - 1) * lh), lh, keys, u);
    }

    /// <summary> Three dots that pulse while the game redraws the character. </summary>
    public static void Busy(ICanvas c, Vector2 left, float u, double time)
    {
        for (var i = 0; i < 3; ++i)
        {
            var phase = (float)((time * 2.4 - i * 0.33) % 1.0);
            var a     = 0.35f + 0.65f * MathF.Max(0, MathF.Sin(phase * MathF.PI));
            c.FillCircle(new Vector2(left.X + 3 * u + i * 8 * u, left.Y), 2.5f * u, Theme.Accent.Fade(a));
        }
    }

    // ---- the stick figure for To sort ----

    private static readonly PathShape Figure = new(
        "M15.5 8L14 2.5L18 5.5M24.5 8L26 2.5L22 5.5M20 18V38M11 31L14 21H26L29 31M20 38L15 60M20 38L25 60M20 38C28 40 33 37 32 30"
      + "M14 12a6 6 0 1 0 12 0a6 6 0 1 0-12 0", 40, 64);

    /// <summary> A 40 × 64 figure with the places a part covers marked in accent. </summary>
    public static void StickFigure(ICanvas c, Rect box, Core.BodyRegions covers)
    {
        c.Shape(Figure, box, Theme.Faint, 2);
        var sx   = box.W / 40;
        var sy   = box.H / 64;
        var fill = Theme.Accent.Fade(0.3f);
        void Mark(float x, float y, float r)
        {
            var center = new Vector2(box.X + x * sx, box.Y + y * sy);
            c.FillCircle(center, r * sx, fill);
            c.StrokeCircle(center, r * sx, Theme.Accent, 1.5f * sx);
        }

        void Span(float y0, float y1, float w)
        {
            var r = new Rect(box.X + (20 - w / 2) * sx, box.Y + y0 * sy, w * sx, (y1 - y0) * sy);
            c.FillRect(r, fill, w / 2 * sx);
            c.StrokeRect(r, Theme.Accent, w / 2 * sx, 1.5f * sx);
        }

        var marks = StickMarks(covers, out var spans);
        foreach (var (x, y, r) in marks)
            Mark(x, y, r);
        foreach (var (y0, y1, w) in spans)
            Span(y0, y1, w);
    }

    /// <summary>
    /// Where the marks go on the 40 × 64 figure: the head as one circle, a run of places down the body as one capsule,
    /// arms and hands on both sides, the tail on its own.
    /// </summary>
    public static List<(float X, float Y, float R)> StickMarks(Core.BodyRegions covers, out List<(float Y0, float Y1, float W)> spans)
    {
        var marks = new List<(float, float, float)>();
        spans = new List<(float, float, float)>();
        var head = covers & (Core.BodyRegions.HeadTop | Core.BodyRegions.Face | Core.BodyRegions.Ears);
        if (head == Core.BodyRegions.HeadTop)
            marks.Add((20, 5, 6));
        else if (head == Core.BodyRegions.Face)
            marks.Add((20, 12, 7));
        else if (head == Core.BodyRegions.Ears)
            marks.Add((20, 5, 7));
        else if (head != 0)
            marks.Add((20, 9, 9));

        // down the middle of the body, top to bottom
        (Core.BodyRegions Region, float Y)[] trunk =
        {
            (Core.BodyRegions.Neck, 19), (Core.BodyRegions.Chest, 25), (Core.BodyRegions.Back, 27), (Core.BodyRegions.Waist, 33),
            (Core.BodyRegions.Hips, 39), (Core.BodyRegions.Skirt, 44), (Core.BodyRegions.Thighs, 46), (Core.BodyRegions.Knees, 50),
            (Core.BodyRegions.Calves, 55), (Core.BodyRegions.Feet, 59), (Core.BodyRegions.Toes, 61),
        };
        var ys = trunk.Where(t => (covers & t.Region) != 0).Select(t => t.Y).ToList();
        if (ys.Count == 1)
            marks.Add((20, ys[0], ys[0] is 27 ? 8 : ys[0] < 40 ? 6 : 5));
        else if (ys.Count > 1)
            spans.Add((ys.Min() - 5, ys.Max() + 4, ys.Max() - ys.Min() > 12 ? 18 : 16));

        if ((covers & (Core.BodyRegions.UpperArms | Core.BodyRegions.Forearms)) != 0)
        {
            marks.Add((12.5f, 27, 3.5f));
            marks.Add((27.5f, 27, 3.5f));
        }

        if ((covers & (Core.BodyRegions.Hands | Core.BodyRegions.Fingers)) != 0)
        {
            marks.Add((11, 31.5f, 3));
            marks.Add((29, 31.5f, 3));
        }

        if ((covers & Core.BodyRegions.Tail) != 0)
            marks.Add((31, 33, 6));
        return marks;
    }
}
