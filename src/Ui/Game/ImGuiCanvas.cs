using System.Numerics;
using Dalamud.Bindings.ImGui;
using Dalamud.Interface;
using Dalamud.Interface.GameFonts;
using Dalamud.Interface.ManagedFontAtlas;
using Dalamud.Interface.Utility;
using Dalamud.Plugin.Services;
using ModCloset2.Ui.Canvas;

namespace ModCloset2.Ui.Game;

/// <summary>
/// The canvas in the game: draws onto the closet window's draw list and reads ImGui's input. Body text is Dalamud's
/// default font, built at the few sizes the window uses so small text stays crisp; titles and shelf headings are the
/// game's Trump Gothic. Popups, the drag ghost and tooltips go to a second draw-list channel that is merged on top.
/// </summary>
public sealed unsafe class ImGuiCanvas : ICanvas, IDisposable
{
    /// <summary>
    /// ImGui sizes a font by its line height (ascent to descent), CSS by its em. Noto Sans, Dalamud's default, is about
    /// 1.36 em tall, so the game's 17 px font has the em of a 12.5 px mockup font. U is taken from the em, which keeps
    /// the mockups' proportions of text to boxes. (Only the game can confirm the exact ratio: one constant to tune.)
    /// </summary>
    public const float EmRatio = 1.36f;

    /// <summary> Trump Gothic's size is close to its em (a condensed font with tall capitals). Also tunable in the game. </summary>
    public const float DisplayRatio = 1.0f;

    private static readonly Vector2 Far = new(-100000, -100000);

    private readonly PictureCache _pictures;
    private readonly FontSet      _fonts;
    private readonly Stack<(int Start, float Degrees, Vector2 Pivot)> _turns = new();
    private ImDrawListPtr _draw;
    private bool _overlay;
    private int  _clipDepth;
    private int  _overlayClipDepth;
    private CursorShape _cursor;
    private bool _mouseOwned;

    public ImGuiCanvas(IUiBuilder ui, ITextureProvider textures)
    {
        _pictures = new PictureCache(textures);
        _fonts    = new FontSet(ui);
    }

    public float U { get; private set; } = 1;
    public Rect Bounds { get; private set; }
    public InputState Input { get; } = new();
    public PictureCache Pictures
        => _pictures;

    /// <summary>
    /// Starts a frame inside the current window. <paramref name="mouseOwned"/>: the mouse is ours (over the window, or
    /// held since a press in it). <paramref name="keysOwned"/>: keys are read at all (the mouse is over the window, or
    /// one of its text fields has the keyboard); otherwise the closet ignores the keyboard entirely.
    /// </summary>
    public void Begin(Rect bounds, bool mouseOwned, bool keysOwned)
    {
        _draw       = ImGui.GetWindowDrawList();
        U           = ImGui.GetFontSize() / (14f * EmRatio);
        Bounds      = bounds;
        _overlay    = false;
        _clipDepth  = 0;
        _cursor     = CursorShape.Arrow;
        _mouseOwned = mouseOwned;
        _fonts.Update(U);
        _draw.ChannelsSplit(2);
        _draw.ChannelsSetCurrent(0);
        _draw.PushClipRect(bounds.Min, bounds.Max, true);

        var io = ImGui.GetIO();
        Input.NextFrame();
        Input.Mouse = mouseOwned ? io.MousePos : Far;
        for (var b = 0; b < 3; ++b)
        {
            var button = (ImGuiMouseButton)b;
            Input.Down[b]     = ImGui.IsMouseDown(button);
            Input.Pressed[b]  = mouseOwned && ImGui.IsMouseClicked(button);
            Input.Released[b] = ImGui.IsMouseReleased(button);
        }

        Input.DoubleClicked = mouseOwned && ImGui.IsMouseDoubleClicked(ImGuiMouseButton.Left);
        Input.Wheel         = mouseOwned ? io.MouseWheel : 0;
        Input.Ctrl          = io.KeyCtrl;
        Input.Focused       = keysOwned;
        Input.TextFocused   = io.WantTextInput;
        Input.Time          = ImGui.GetTime();
        if (Input.Focused)
        {
            Key(Canvas.Key.Enter, ImGuiKey.Enter, false);
            Key(Canvas.Key.Enter, ImGuiKey.KeypadEnter, false);
            Key(Canvas.Key.Escape, ImGuiKey.Escape, false);
            Key(Canvas.Key.Left, ImGuiKey.LeftArrow, true);
            Key(Canvas.Key.Right, ImGuiKey.RightArrow, true);
            Key(Canvas.Key.Up, ImGuiKey.UpArrow, true);
            Key(Canvas.Key.Down, ImGuiKey.DownArrow, true);
            Key(Canvas.Key.Z, ImGuiKey.Z, false);
            Key(Canvas.Key.Backspace, ImGuiKey.Backspace, true);
        }
    }

    private void Key(Canvas.Key key, ImGuiKey imguiKey, bool repeat)
    {
        if (ImGui.IsKeyPressed(imguiKey, repeat))
            Input.Keys.Add(key);
    }

    /// <summary> Ends the frame: closes what is open, merges the overlay on top, sets the mouse cursor, lets go of old pictures. </summary>
    public void End()
    {
        if (_overlay)
            EndOverlay();
        while (_turns.Count > 0)
            PopRotation();
        while (_clipDepth > 0)
            PopClip();
        _draw.PopClipRect();
        _draw.ChannelsMerge();
        if (_mouseOwned && _cursor == CursorShape.Hand)
            ImGui.SetMouseCursor(ImGuiMouseCursor.Hand);
        _pictures.EndFrame();
    }

    // ---- shapes ----

    private static ImDrawFlags Flags(Corners corners, float radius)
    {
        if (radius <= 0 || corners == Corners.None)
            return ImDrawFlags.RoundCornersNone;
        var f = ImDrawFlags.None;
        if ((corners & Corners.TopLeft) != 0)
            f |= ImDrawFlags.RoundCornersTopLeft;
        if ((corners & Corners.TopRight) != 0)
            f |= ImDrawFlags.RoundCornersTopRight;
        if ((corners & Corners.BottomLeft) != 0)
            f |= ImDrawFlags.RoundCornersBottomLeft;
        if ((corners & Corners.BottomRight) != 0)
            f |= ImDrawFlags.RoundCornersBottomRight;
        return f;
    }

    public void FillRect(Rect r, Colr color, float radius = 0, Corners corners = Corners.All)
    {
        if (r.IsEmpty || color.A == 0)
            return;
        _draw.AddRectFilled(r.Min, r.Max, color.Abgr, radius, Flags(corners, radius));
    }

    public void StrokeRect(Rect r, Colr color, float radius, float thickness, Corners corners = Corners.All, float dash = 0, float gap = 0)
    {
        if (r.IsEmpty || color.A == 0)
            return;

        // ImGui strokes on the edge; the canvas draws inside it (as the pictures do)
        var inner = r.Expand(-thickness / 2);
        var rad   = Math.Max(0, radius - thickness / 2);
        if (dash <= 0)
        {
            _draw.AddRect(inner.Min, inner.Max, color.Abgr, rad, Flags(corners, rad), thickness);
            return;
        }

        var pts = Outline.RoundRect(inner, rad, corners);
        foreach (var (a, b) in Outline.Dashes(pts, dash, gap > 0 ? gap : dash))
            _draw.AddLine(a, b, color.Abgr, thickness);
    }

    public void Line(Vector2 a, Vector2 b, Colr color, float thickness)
        => _draw.AddLine(a, b, color.Abgr, thickness);

    public void FillCircle(Vector2 center, float radius, Colr color)
        => _draw.AddCircleFilled(center, radius, color.Abgr);

    public void StrokeCircle(Vector2 center, float radius, Colr color, float thickness)
        => _draw.AddCircle(center, radius, color.Abgr, 0, thickness);

    public void Gradient(Rect r, Colr topLeft, Colr topRight, Colr bottomRight, Colr bottomLeft)
        => _draw.AddRectFilledMultiColor(r.Min, r.Max, topLeft.Abgr, topRight.Abgr, bottomRight.Abgr, bottomLeft.Abgr);

    public void Shape(PathShape shape, Rect box, Colr stroke, float strokeWidth, Colr fill = default)
    {
        var sx = box.W / shape.BoxW;
        var sy = box.H / shape.BoxH;
        foreach (var (points, closed) in shape.Lines)
        {
            if (fill.A > 0 && closed)
            {
                _draw.PathClear();
                foreach (var p in points)
                    _draw.PathLineTo(new Vector2(box.X + p.X * sx, box.Y + p.Y * sy));
                _draw.PathFillConvex(fill.Abgr);
            }

            if (stroke.A == 0 || strokeWidth <= 0)
                continue;
            _draw.PathClear();
            foreach (var p in points)
                _draw.PathLineTo(new Vector2(box.X + p.X * sx, box.Y + p.Y * sy));
            _draw.PathStroke(stroke.Abgr, closed ? ImDrawFlags.Closed : ImDrawFlags.None, strokeWidth * sx);
        }
    }

    public bool Picture(string path, Rect r, float radius, Corners corners = Corners.All, float alpha = 1)
    {
        if (string.IsNullOrEmpty(path) || r.IsEmpty)
            return false;
        var wrap = _pictures.Get(path);
        if (wrap is null)
            return false;

        // cover: fill the box, crop the overflow, keep the top (faces and heads are at the top of mod pictures); a photo
        // the closet took has the piece in its middle, so it keeps the middle
        var (uv0, uv1) = PictureCrop.Cover(wrap.Width, wrap.Height, r.W, r.H, Core.Pictures.IsPhoto(path));

        var tint = Colr.Hex(0xFFFFFF, Math.Clamp(alpha, 0, 1)).Abgr;
        _draw.AddImageRounded(wrap.Handle, r.Min, r.Max, uv0, uv1, tint, radius, Flags(corners, radius));
        return true;
    }

    // ---- text ----

    public Vector2 Measure(string text, TextStyle style)
    {
        if (string.IsNullOrEmpty(text))
            return new Vector2(0, LineHeight(style));
        if (style.Upper)
            text = text.ToUpperInvariant();
        var px = DrawSize(style);
        float w;
        using (_fonts.Push(style, px, out var scale))
            w = ImGui.CalcTextSize(text).X * scale;
        if (style.Tracking != 0)
            w += style.Tracking * style.Size * U * text.Length;
        if (style.Weight == Weight.Bold && style.Face == FontFace.Body)
            w += 1;
        return new Vector2(w, LineHeight(style));
    }

    public float LineHeight(TextStyle style)
        => style.Size * U * (style.Face == FontFace.Display ? 1.18f : 1.35f);

    /// <summary> The ImGui font size that gives the style's em. </summary>
    private float DrawSize(TextStyle style)
        => style.Size * U * (style.Face == FontFace.Display ? DisplayRatio : EmRatio);

    public void Text(Vector2 pos, string text, TextStyle style, Colr color)
    {
        if (string.IsNullOrEmpty(text) || color.A == 0)
            return;
        if (style.Upper)
            text = text.ToUpperInvariant();
        var px = DrawSize(style);
        using var font = _fonts.Push(style, px, out _);
        var top = new Vector2(pos.X, MathF.Round(pos.Y + (LineHeight(style) - px) / 2));
        if (style.Tracking == 0)
        {
            Glyphs(top, text, px, color, style.Weight == Weight.Bold && style.Face == FontFace.Body);
            return;
        }

        // ImGui has no letter spacing: set the letters one by one (only short labels and the title do this)
        var x     = top.X;
        var extra = style.Tracking * style.Size * U;
        var scale = px / ImGui.GetFontSize();
        foreach (var ch in text)
        {
            var s = ch.ToString();
            Glyphs(new Vector2(x, top.Y), s, px, color, false);
            x += ImGui.CalcTextSize(s).X * scale + extra;
        }
    }

    private void Glyphs(Vector2 at, string text, float px, Colr color, bool bold)
    {
        var font = ImGui.GetFont();
        _draw.AddText(font, px, at, color.Abgr, text);
        // the default font has one weight: bold is the same text again, a pixel to the right
        if (bold)
            _draw.AddText(font, px, at + new Vector2(1, 0), color.Abgr, text);
    }

    // ---- structure ----

    public void PushClip(Rect r)
    {
        _draw.PushClipRect(r.Min, r.Max, true);
        _clipDepth++;
    }

    public void PopClip()
    {
        if (_clipDepth == 0 || _overlay && _clipDepth <= _overlayClipDepth)
            return;
        _draw.PopClipRect();
        _clipDepth--;
    }

    public void BeginOverlay()
    {
        if (_overlay)
            return;
        _overlay = true;
        _draw.ChannelsSetCurrent(1);
        _draw.PushClipRect(Bounds.Min, Bounds.Max, false);
        _clipDepth++;
        _overlayClipDepth = _clipDepth;
    }

    public void EndOverlay()
    {
        if (!_overlay)
            return;
        while (_clipDepth > _overlayClipDepth)
        {
            _draw.PopClipRect();
            _clipDepth--;
        }

        _overlay = false;
        _draw.PopClipRect();
        _clipDepth--;
        _draw.ChannelsSetCurrent(0);
    }

    public void PushRotation(float degrees, Vector2 pivot)
        => _turns.Push((_draw.VtxBuffer.Size, degrees, pivot));

    /// <summary> ImGui cannot draw turned shapes: what was drawn since the push is turned afterwards, vertex by vertex. </summary>
    public void PopRotation()
    {
        if (_turns.Count == 0)
            return;
        var (start, degrees, pivot) = _turns.Pop();
        var a   = degrees * MathF.PI / 180f;
        var cos = MathF.Cos(a);
        var sin = MathF.Sin(a);
        var buf = _draw.VtxBuffer;
        for (var i = start; i < buf.Size; ++i)
        {
            ref var v = ref buf.Data[i];
            var d = v.Pos - pivot;
            v.Pos = pivot + new Vector2(d.X * cos - d.Y * sin, d.X * sin + d.Y * cos);
        }
    }

    // ---- input ----

    public bool TextField(string id, Rect r, ref string text, string hint, TextStyle style, Colr color, Colr hintColor, bool takeFocus, bool interactive, out bool active, bool selectAll = false)
    {
        var px = DrawSize(style);
        if (!interactive)
        {
            // under an open popup the field is only a picture of itself
            active = false;
            var lh = LineHeight(style);
            PushClip(r);
            Text(new Vector2(r.X, r.Y + (r.H - lh) / 2), text.Length > 0 ? text : hint, style, text.Length > 0 ? color : hintColor);
            PopClip();
            return false;
        }

        using var font = _fonts.Push(style, px, out var scale);
        var height = ImGui.GetFontSize() * scale;
        ImGui.PushStyleVar(ImGuiStyleVar.FramePadding, new Vector2(0, Math.Max(0, (r.H - height) / 2)));
        ImGui.PushStyleVar(ImGuiStyleVar.FrameBorderSize, 0f);
        ImGui.PushStyleColor(ImGuiCol.FrameBg, 0u);
        ImGui.PushStyleColor(ImGuiCol.FrameBgHovered, 0u);
        ImGui.PushStyleColor(ImGuiCol.FrameBgActive, 0u);
        ImGui.PushStyleColor(ImGuiCol.Text, color.Abgr);
        ImGui.PushStyleColor(ImGuiCol.TextDisabled, hintColor.Abgr);
        ImGui.PushStyleColor(ImGuiCol.TextSelectedBg, Theme.Accent.Fade(0.35f).Abgr);
        ImGui.SetCursorScreenPos(r.Min);
        ImGui.SetNextItemWidth(r.W);
        if (takeFocus)
            ImGui.SetKeyboardFocusHere();
        var before  = text;
        var changed = ImGui.InputTextWithHint("##" + id, hint, ref text, 256, selectAll ? ImGuiInputTextFlags.AutoSelectAll : ImGuiInputTextFlags.None);
        active = ImGui.IsItemActive();

        // this ImGui puts the old text back on Esc and lets go of the field; the closet wants Esc to empty it
        if (ImGui.IsItemDeactivated() && ImGui.IsKeyPressed(ImGuiKey.Escape, false) && before.Length + text.Length > 0)
        {
            text    = string.Empty;
            changed = true;
        }

        ImGui.PopStyleColor(6);
        ImGui.PopStyleVar(2);
        return changed;
    }

    public void SetCursor(CursorShape shape)
        => _cursor = shape;

    public void Dispose()
    {
        _pictures.Dispose();
        _fonts.Dispose();
    }

    /// <summary>
    /// Font handles for the sizes the window uses (body at 11–15, display at 22–34 mockup pixels), rebuilt when U
    /// changes. Until a handle is ready (the atlas builds in the background), the default font is scaled instead.
    /// </summary>
    private sealed class FontSet : IDisposable
    {
        private static readonly float[] BodySizes    = { 11, 12, 13, 14, 15 };
        private static readonly float[] DisplaySizes = { 22, 26, 28, 34 };

        private readonly IUiBuilder _ui;
        private readonly Dictionary<(FontFace, float), IFontHandle> _handles = new();
        private float _builtFor;

        public FontSet(IUiBuilder ui)
            => _ui = ui;

        public void Update(float u)
        {
            if (Math.Abs(u - _builtFor) < 0.01f)
                return;
            Dispose();
            _builtFor = u;
            var global = Math.Max(0.1f, ImGuiHelpers.GlobalScale);
            // the plugin's font atlas scales sizes by the global scale itself: ask for the size before that
            foreach (var size in BodySizes)
            {
                var px = size * u * EmRatio / global;
                _handles[(FontFace.Body, size)] = _ui.FontAtlas.NewDelegateFontHandle(e => e.OnPreBuild(tk => tk.AddDalamudDefaultFont(px)));
            }

            foreach (var size in DisplaySizes)
                _handles[(FontFace.Display, size)] = _ui.FontAtlas.NewGameFontHandle(new GameFontStyle(GameFontFamily.TrumpGothic, size * u * DisplayRatio / global));
        }

        /// <summary> Pushes the closest font for the style; <paramref name="scale"/> turns its measurements into the wanted size. </summary>
        public IDisposable Push(TextStyle style, float px, out float scale)
        {
            // the nearest built size (a plain loop: this runs for every text, every frame)
            var sizes = style.Face == FontFace.Display ? DisplaySizes : BodySizes;
            var best  = sizes[0];
            foreach (var s in sizes)
                if (Math.Abs(s - style.Size) < Math.Abs(best - style.Size))
                    best = s;
            if (_handles.TryGetValue((style.Face, best), out var handle) && handle.Available)
            {
                var pushed = handle.Push();
                scale = px / Math.Max(1, ImGui.GetFontSize());
                return pushed;
            }

            scale = px / Math.Max(1, ImGui.GetFontSize());
            return Nothing.Instance;
        }

        public void Dispose()
        {
            foreach (var h in _handles.Values)
                h.Dispose();
            _handles.Clear();
        }

        private sealed class Nothing : IDisposable
        {
            public static readonly Nothing Instance = new();

            public void Dispose()
            {
            }
        }
    }
}
