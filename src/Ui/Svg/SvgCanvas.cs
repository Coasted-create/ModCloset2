using System.Globalization;
using System.Numerics;
using System.Text;
using ModCloset2.Ui.Canvas;

namespace ModCloset2.Ui.Svg;

/// <summary>
/// Draws the window into an SVG picture, so it can be looked at without the game. Body text is set in Inter and
/// titles in DejaVu Sans Condensed Bold (the fonts this container has); text is measured with a width table made from
/// those fonts (<see cref="SvgFontMetrics"/>), with kerning turned off in the picture, so what is measured is what is
/// drawn. Input is whatever the caller puts into <see cref="Input"/> before a frame.
/// </summary>
public sealed class SvgCanvas : ICanvas
{
    private static readonly CultureInfo Inv = CultureInfo.InvariantCulture;

    private readonly StringBuilder _base    = new();
    private readonly StringBuilder _overlay = new();
    private readonly StringBuilder _defs    = new();
    private readonly Stack<Rect>   _baseClips    = new();
    private readonly Stack<Rect>   _overlayClips = new();
    private readonly Stack<int>    _groups       = new(); // open <g> per push, for the current layer
    private StringBuilder _out;
    private Stack<Rect>   _clips;
    private int           _ids;
    private bool          _inOverlay;

    public SvgCanvas(float width, float height, float u)
    {
        Bounds = new Rect(0, 0, width, height);
        U      = u;
        _out   = _base;
        _clips = _baseClips;
    }

    public float U { get; }
    public Rect Bounds { get; }
    public InputState Input { get; set; } = new();

    /// <summary> Pictures that should act as "still loading" (the placeholder is drawn). </summary>
    public HashSet<string> Loading { get; } = new(StringComparer.OrdinalIgnoreCase);

    /// <summary> Characters "typed" this frame into the focused text field (set by the scene script). </summary>
    public string Typed { get; set; } = string.Empty;

    /// <summary> The text field that has the keyboard, kept across frames like the game would. </summary>
    public string? FocusedField { get; set; }

    public CursorShape Cursor { get; private set; }

    // ---- what was drawn, for the tests ----
    public int DrawCalls { get; private set; }
    public List<(string Text, Rect Box, Colr Color)> Texts { get; } = new();
    public List<(string Path, Rect Box)> Pictures { get; } = new();

    /// <summary> Path data of every vector shape drawn (icons, the stick figure). </summary>
    public List<string> ShapesDrawn { get; } = new();

    /// <summary> Starts a new frame: clears the picture and the per-frame records, keeps focus. </summary>
    public void BeginFrame()
    {
        _base.Clear();
        _overlay.Clear();
        _defs.Clear();
        _baseClips.Clear();
        _overlayClips.Clear();
        _groups.Clear();
        _out       = _base;
        _clips     = _baseClips;
        _inOverlay = false;
        _ids       = 0;
        DrawCalls  = 0;
        Texts.Clear();
        Pictures.Clear();
        PictureAlphas.Clear();
        ShapesDrawn.Clear();
        MeasureCalls = 0;
        Cursor = CursorShape.Arrow;
        Input.TextFocused = FocusedField is not null;
    }

    public string Document(Colr background)
    {
        var sb = new StringBuilder();
        sb.Append(Inv, $"<svg xmlns=\"http://www.w3.org/2000/svg\" xmlns:xlink=\"http://www.w3.org/1999/xlink\" width=\"{F(Bounds.W)}\" height=\"{F(Bounds.H)}\" viewBox=\"0 0 {F(Bounds.W)} {F(Bounds.H)}\">\n");
        sb.Append("<style>text{font-kerning:none;font-variant-ligatures:none;font-feature-settings:\"calt\" 0,\"kern\" 0;white-space:pre}</style>\n");
        sb.Append("<defs>").Append(_defs).Append("</defs>\n");
        sb.Append(Inv, $"<rect width=\"{F(Bounds.W)}\" height=\"{F(Bounds.H)}\" fill=\"{background.HexText}\"/>\n");
        sb.Append(_base);
        sb.Append(_overlay);
        sb.Append("</svg>\n");
        return sb.ToString();
    }

    // ---- shapes ----

    public void FillRect(Rect r, Colr color, float radius = 0, Corners corners = Corners.All)
    {
        if (r.IsEmpty || color.A == 0)
            return;
        DrawCalls++;
        _out.Append("<path d=\"").Append(RoundRectPath(r, radius, corners)).Append("\" ").Append(Fill(color)).Append("/>\n");
    }

    public void StrokeRect(Rect r, Colr color, float radius, float thickness, Corners corners = Corners.All, float dash = 0, float gap = 0)
    {
        if (r.IsEmpty || color.A == 0)
            return;
        DrawCalls++;
        var inner = r.Expand(-thickness / 2);
        _out.Append("<path d=\"").Append(RoundRectPath(inner, Math.Max(0, radius - thickness / 2), corners)).Append("\" fill=\"none\" ").Append(Stroke(color, thickness));
        if (dash > 0)
            _out.Append(Inv, $" stroke-dasharray=\"{F(dash)} {F(gap > 0 ? gap : dash)}\"");
        _out.Append("/>\n");
    }

    public void Line(Vector2 a, Vector2 b, Colr color, float thickness)
    {
        DrawCalls++;
        _out.Append(Inv, $"<line x1=\"{F(a.X)}\" y1=\"{F(a.Y)}\" x2=\"{F(b.X)}\" y2=\"{F(b.Y)}\" ").Append(Stroke(color, thickness)).Append(" stroke-linecap=\"round\"/>\n");
    }

    public void FillCircle(Vector2 center, float radius, Colr color)
    {
        DrawCalls++;
        _out.Append(Inv, $"<circle cx=\"{F(center.X)}\" cy=\"{F(center.Y)}\" r=\"{F(radius)}\" ").Append(Fill(color)).Append("/>\n");
    }

    public void StrokeCircle(Vector2 center, float radius, Colr color, float thickness)
    {
        DrawCalls++;
        _out.Append(Inv, $"<circle cx=\"{F(center.X)}\" cy=\"{F(center.Y)}\" r=\"{F(radius)}\" fill=\"none\" ").Append(Stroke(color, thickness)).Append("/>\n");
    }

    /// <summary> SVG has no four-corner gradient: the field is drawn as a fine grid of blended cells. </summary>
    public void Gradient(Rect r, Colr tl, Colr tr, Colr br, Colr bl)
    {
        if (tl == tr && bl == br || tl == bl && tr == br)
        {
            // one direction only: a plain linear gradient
            DrawCalls++;
            var id       = $"g{_ids++}";
            var vertical = tl == tr;
            var (a, z)   = vertical ? (tl, bl) : (tl, tr);
            _defs.Append(Inv, $"<linearGradient id=\"{id}\" x1=\"0\" y1=\"0\" x2=\"{(vertical ? 0 : 1)}\" y2=\"{(vertical ? 1 : 0)}\">");
            _defs.Append(Inv, $"<stop offset=\"0\" stop-color=\"{a.HexText}\" stop-opacity=\"{F(a.Alpha)}\"/><stop offset=\"1\" stop-color=\"{z.HexText}\" stop-opacity=\"{F(z.Alpha)}\"/></linearGradient>");
            _out.Append(Inv, $"<rect x=\"{F(r.X)}\" y=\"{F(r.Y)}\" width=\"{F(r.W)}\" height=\"{F(r.H)}\" fill=\"url(#{id})\"/>\n");
            return;
        }

        const int n = 16;
        DrawCalls++;
        var cw = r.W / n;
        var ch = r.H / n;
        for (var y = 0; y < n; ++y)
            for (var x = 0; x < n; ++x)
            {
                var u   = (x + 0.5f) / n;
                var v   = (y + 0.5f) / n;
                var top = tl.Mix(tr, u);
                var bot = bl.Mix(br, u);
                var c   = top.Mix(bot, v);
                _out.Append(Inv, $"<rect x=\"{F(r.X + x * cw)}\" y=\"{F(r.Y + y * ch)}\" width=\"{F(cw + 0.5f)}\" height=\"{F(ch + 0.5f)}\" ").Append(Fill(c)).Append("/>\n");
            }
    }

    public void Shape(PathShape shape, Rect box, Colr stroke, float strokeWidth, Colr fill = default)
    {
        DrawCalls++;
        ShapesDrawn.Add(shape.Data);
        var sx = box.W / shape.BoxW;
        var sy = box.H / shape.BoxH;
        _out.Append(Inv, $"<path d=\"{shape.Data}\" transform=\"translate({F(box.X)} {F(box.Y)}) scale({F(sx)} {F(sy)})\" ");
        _out.Append(fill.A > 0 ? Fill(fill) : "fill=\"none\"").Append(' ');
        if (stroke.A > 0 && strokeWidth > 0)
            _out.Append(Stroke(stroke, strokeWidth)).Append(" stroke-linecap=\"round\" stroke-linejoin=\"round\"");
        _out.Append("/>\n");
    }

    public bool Picture(string path, Rect r, float radius, Corners corners = Corners.All, float alpha = 1)
    {
        if (string.IsNullOrEmpty(path) || Loading.Contains(path))
            return false;

        // as the game's cache: a file that is missing or broken is asked for once, then the placeholder is drawn
        if (!_pictureOk.TryGetValue(path, out var ok))
        {
            PictureLoads++;
            ok = File.Exists(path) && new FileInfo(path).Length > 8;
            _pictureOk[path] = ok;
        }

        if (!ok)
            return false;
        DrawCalls++;
        Pictures.Add((path, r));
        var id = $"p{_ids++}";
        _defs.Append(Inv, $"<clipPath id=\"{id}\"><path d=\"{RoundRectPath(r, radius, corners)}\"/></clipPath>");
        var href = new Uri(Path.GetFullPath(path)).AbsoluteUri;
        var align   = Core.Pictures.IsPhoto(path) ? "xMidYMid" : "xMidYMin";
        var opacity = alpha < 1 ? $" opacity=\"{F(Math.Clamp(alpha, 0, 1))}\"" : string.Empty;
        _out.Append(Inv, $"<image href=\"{Esc(href)}\" x=\"{F(r.X)}\" y=\"{F(r.Y)}\" width=\"{F(r.W)}\" height=\"{F(r.H)}\" preserveAspectRatio=\"{align} slice\"{opacity} clip-path=\"url(#{id})\"/>\n");
        PictureAlphas.Add((path, r, alpha));
        return true;
    }

    private readonly Dictionary<string, bool> _pictureOk = new(StringComparer.OrdinalIgnoreCase);

    /// <summary> How often a picture file was looked at (each path once). </summary>
    public int PictureLoads { get; private set; }

    /// <summary> Pictures drawn this frame with their opacity (the fade check reads them). </summary>
    public List<(string Path, Rect Box, float Alpha)> PictureAlphas { get; } = new();

    // ---- text ----

    public Vector2 Measure(string text, TextStyle style)
    {
        MeasureCalls++;
        return new Vector2(Width(text, style), LineHeight(style));
    }

    /// <summary> Text measurements asked for since the frame began (the To sort check counts them). </summary>
    public int MeasureCalls { get; private set; }

    public float LineHeight(TextStyle style)
        => style.Size * U * (style.Face == FontFace.Display ? 1.18f : 1.35f);

    public void Text(Vector2 pos, string text, TextStyle style, Colr color)
    {
        if (string.IsNullOrEmpty(text) || color.A == 0)
            return;
        DrawCalls++;
        if (style.Upper)
            text = text.ToUpperInvariant();
        var px       = style.Size * U;
        var lh       = LineHeight(style);
        var (asc, d) = Metrics(style);
        var baseline = pos.Y + (lh - (asc + d) * px) / 2 + asc * px;
        var family   = style.Face == FontFace.Display ? "DejaVu Sans Condensed" : "Inter";
        var weight   = style.Face == FontFace.Display ? 700 : style.Weight switch { Weight.Medium => 500, Weight.Bold => 600, _ => 400 };
        _out.Append(Inv, $"<text x=\"{F(pos.X)}\" y=\"{F(baseline)}\" font-family=\"{family}\" font-size=\"{F(px)}\" font-weight=\"{weight}\" ");
        if (style.Tracking != 0)
            _out.Append(Inv, $"letter-spacing=\"{F(style.Tracking * px)}\" ");
        var width = Width(text, style);
        // data-w: the width the layout used, so tools/shots/shots.js can check the browser draws the same
        _out.Append(Inv, $"data-w=\"{F(width)}\" ").Append(Fill(color)).Append('>').Append(Esc(text)).Append("</text>\n");
        Texts.Add((text, new Rect(pos.X, pos.Y, width, lh), color));
    }

    private float Width(string text, TextStyle style)
    {
        if (string.IsNullOrEmpty(text))
            return 0;
        if (style.Upper)
            text = text.ToUpperInvariant();
        var (table, extra) = style.Face == FontFace.Display ? (SvgFontMetrics.Display, SvgFontMetrics.DisplayExtra)
            : style.Weight switch
            {
                Weight.Medium => (SvgFontMetrics.Medium, SvgFontMetrics.MediumExtra),
                Weight.Bold   => (SvgFontMetrics.SemiBold, SvgFontMetrics.SemiBoldExtra),
                _             => (SvgFontMetrics.Regular, SvgFontMetrics.RegularExtra),
            };
        var units = 0f;
        foreach (var ch in text)
        {
            if (ch >= 0x20 && ch <= 0xFF && table[ch - 0x20] > 0)
                units += table[ch - 0x20];
            else if (extra.TryGetValue(ch, out var w))
                units += w;
            else
                units += 600; // not in the font: the browser falls back to another font of about this width
        }

        var px = style.Size * U;
        return units / 1000f * px + style.Tracking * px * text.Length;
    }

    private static (float Ascent, float Descent) Metrics(TextStyle style)
        => style.Face == FontFace.Display ? (SvgFontMetrics.DisplayAscent, SvgFontMetrics.DisplayDescent)
            : (SvgFontMetrics.RegularAscent, SvgFontMetrics.RegularDescent);

    // ---- structure ----

    public void PushClip(Rect r)
    {
        var current = _clips.Count > 0 ? _clips.Peek() : Bounds.Expand(4000);
        var clip    = current.Intersect(r);
        _clips.Push(clip);
        var id = $"c{_ids++}";
        _defs.Append(Inv, $"<clipPath id=\"{id}\"><rect x=\"{F(clip.X)}\" y=\"{F(clip.Y)}\" width=\"{F(clip.W)}\" height=\"{F(clip.H)}\"/></clipPath>");
        _out.Append(Inv, $"<g clip-path=\"url(#{id})\">\n");
        _groups.Push(1);
    }

    public void PopClip()
    {
        if (_clips.Count == 0)
            return;
        _clips.Pop();
        _groups.Pop();
        _out.Append("</g>\n");
    }

    public void BeginOverlay()
    {
        if (_inOverlay)
            return;
        _inOverlay = true;
        _out       = _overlay;
        _clips     = _overlayClips;
    }

    public void EndOverlay()
    {
        if (!_inOverlay)
            return;
        _inOverlay = false;
        _out       = _base;
        _clips     = _baseClips;
    }

    public void PushRotation(float degrees, Vector2 pivot)
    {
        _out.Append(Inv, $"<g transform=\"rotate({F(degrees)} {F(pivot.X)} {F(pivot.Y)})\">\n");
        _groups.Push(2);
    }

    public void PopRotation()
    {
        if (_groups.Count == 0)
            return;
        _groups.Pop();
        _out.Append("</g>\n");
    }

    // ---- input ----

    public bool TextField(string id, Rect r, ref string text, string hint, TextStyle style, Colr color, Colr hintColor, bool takeFocus, bool interactive, out bool active, bool selectAll = false)
    {
        var before = text;
        if (interactive)
        {
            if (takeFocus)
            {
                FocusedField = id;
                _selected    = selectAll ? id : null;
            }
            else if (Input.Pressed[0])
            {
                FocusedField = r.Contains(Input.Mouse) ? id : FocusedField == id ? null : FocusedField;
                _selected    = null;
            }

            if (FocusedField == id)
            {
                if (Input.KeyPressed(Key.Escape))
                {
                    // as in the game: Esc empties the field and lets go of it
                    text         = string.Empty;
                    FocusedField = null;
                }
                else if (_selected == id && (Typed.Length > 0 || Input.KeyPressed(Key.Backspace)))
                {
                    // all of it selected: typing replaces it
                    text      = Typed;
                    _selected = null;
                }
                else
                {
                    if (Input.KeyPressed(Key.Backspace) && text.Length > 0)
                        text = text[..^1];
                    text += Typed;
                }
            }
        }

        active = interactive && FocusedField == id;
        if (active && _selected == id && text.Length > 0)
            FillRect(new Rect(r.X - 1, r.Y + (r.H - LineHeight(style)) / 2, Math.Min(r.W, Width(text, style)) + 2, LineHeight(style)), Theme.Accent.Fade(0.35f), 2);
        var lh = LineHeight(style);
        PushClip(r);
        var y = r.Y + (r.H - lh) / 2;

        // as the game's field: a text longer than the field shows its end while typed in (the caret is there), else its start
        var shown = text;
        if (Width(shown, style) > r.W)
        {
            if (active && _selected != id)
                while (shown.Length > 1 && Width(shown, style) > r.W - 2)
                    shown = shown[1..];
            else
                while (shown.Length > 1 && Width(shown, style) > r.W)
                    shown = shown[..^1];
        }

        if (text.Length > 0)
            Text(new Vector2(r.X, y), shown, style, color);
        else if (!active || hint.Length > 0)
            Text(new Vector2(r.X, y), hint, style, hintColor);
        if (active)
        {
            var x = r.X + (text.Length > 0 ? Math.Min(r.W - 1, Width(shown, style) + 1) : 0);
            Line(new Vector2(x, y + lh * 0.18f), new Vector2(x, y + lh * 0.82f), color, 1);
        }

        PopClip();
        return text != before;
    }

    public void SetCursor(CursorShape shape)
        => Cursor = shape;

    private string? _selected;

    // ---- helpers ----

    private static string F(float v)
        => v.ToString("0.##", Inv);

    private static string Fill(Colr c)
        => c.A == 255 ? $"fill=\"{c.HexText}\"" : $"fill=\"{c.HexText}\" fill-opacity=\"{F(c.Alpha)}\"";

    private static string Stroke(Colr c, float w)
        => c.A == 255 ? $"stroke=\"{c.HexText}\" stroke-width=\"{F(w)}\"" : $"stroke=\"{c.HexText}\" stroke-opacity=\"{F(c.Alpha)}\" stroke-width=\"{F(w)}\"";

    private static string Esc(string s)
        => s.Replace("&", "&amp;").Replace("<", "&lt;").Replace(">", "&gt;").Replace("\"", "&quot;");

    private static string RoundRectPath(Rect r, float radius, Corners corners)
    {
        var rad = Math.Clamp(radius, 0, Math.Min(r.W, r.H) / 2);
        float Rc(Corners c)
            => (corners & c) != 0 ? rad : 0;

        var tl = Rc(Corners.TopLeft);
        var tr = Rc(Corners.TopRight);
        var br = Rc(Corners.BottomRight);
        var bl = Rc(Corners.BottomLeft);
        var sb = new StringBuilder();
        sb.Append(Inv, $"M{F(r.X + tl)} {F(r.Y)}H{F(r.Right - tr)}");
        if (tr > 0)
            sb.Append(Inv, $"A{F(tr)} {F(tr)} 0 0 1 {F(r.Right)} {F(r.Y + tr)}");
        sb.Append(Inv, $"V{F(r.Bottom - br)}");
        if (br > 0)
            sb.Append(Inv, $"A{F(br)} {F(br)} 0 0 1 {F(r.Right - br)} {F(r.Bottom)}");
        sb.Append(Inv, $"H{F(r.X + bl)}");
        if (bl > 0)
            sb.Append(Inv, $"A{F(bl)} {F(bl)} 0 0 1 {F(r.X)} {F(r.Bottom - bl)}");
        sb.Append(Inv, $"V{F(r.Y + tl)}");
        if (tl > 0)
            sb.Append(Inv, $"A{F(tl)} {F(tl)} 0 0 1 {F(r.X + tl)} {F(r.Y)}");
        sb.Append('Z');
        return sb.ToString();
    }
}
