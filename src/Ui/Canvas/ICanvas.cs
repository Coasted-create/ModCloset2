using System.Numerics;

namespace ModCloset2.Ui.Canvas;

/// <summary>
/// Everything the closet window draws goes through this small surface, so the same window can be drawn in the game
/// (<c>ImGuiCanvas</c>, onto the window's draw list) and into a picture (<c>SvgCanvas</c>, for looking at it without
/// the game). Geometry is in screen pixels; text sizes are in mockup pixels and scaled by <see cref="U"/>.
/// </summary>
public interface ICanvas
{
    /// <summary> One mockup pixel in screen pixels (DESIGN.md: U = body font em / 14, times the global scale). </summary>
    float U { get; }

    /// <summary> The area the window's content may use. </summary>
    Rect Bounds { get; }
    InputState Input { get; }

    // ---- shapes ----

    void FillRect(Rect r, Colr color, float radius = 0, Corners corners = Corners.All);

    /// <summary> An outline drawn inside the rectangle's edge; <paramref name="dash"/> above 0 draws it dashed. </summary>
    void StrokeRect(Rect r, Colr color, float radius, float thickness, Corners corners = Corners.All, float dash = 0, float gap = 0);

    void Line(Vector2 a, Vector2 b, Colr color, float thickness);
    void FillCircle(Vector2 center, float radius, Colr color);
    void StrokeCircle(Vector2 center, float radius, Colr color, float thickness);

    /// <summary> Colours at the four corners, blended across (for colour fields). </summary>
    void Gradient(Rect r, Colr topLeft, Colr topRight, Colr bottomRight, Colr bottomLeft);

    /// <summary> A vector shape (an icon, the stick figure) fitted into <paramref name="box"/>; widths are in the shape's own units. </summary>
    void Shape(PathShape shape, Rect box, Colr stroke, float strokeWidth, Colr fill = default);

    /// <summary>
    /// A picture file filling <paramref name="r"/> (cover: cropped, keeping the top; a photo the closet took keeps its
    /// middle, see <c>Pictures.IsPhoto</c>), at <paramref name="alpha"/> (a picture fading in). Returns false while it is
    /// not available (still loading, missing, broken): the caller then draws the placeholder.
    /// </summary>
    bool Picture(string path, Rect r, float radius, Corners corners = Corners.All, float alpha = 1);

    // ---- text ----

    /// <summary> Width of a single line, and the height of its line box. </summary>
    Vector2 Measure(string text, TextStyle style);

    /// <summary> Height of the line box text of this style takes (the glyphs are centred in it). </summary>
    float LineHeight(TextStyle style);

    /// <summary> One line of text; <paramref name="pos"/> is the top-left of its line box. </summary>
    void Text(Vector2 pos, string text, TextStyle style, Colr color);

    // ---- structure ----

    /// <summary> Restricts drawing to a rectangle (intersected with the current one) until <see cref="PopClip"/>. </summary>
    void PushClip(Rect r);
    void PopClip();

    /// <summary> Everything until <see cref="EndOverlay"/> is drawn above the rest of the window (popups, the drag ghost, tooltips). </summary>
    void BeginOverlay();
    void EndOverlay();

    /// <summary> Turns what is drawn until <see cref="PopRotation"/> around a point (the lifted card). </summary>
    void PushRotation(float degrees, Vector2 pivot);
    void PopRotation();

    // ---- input ----

    /// <summary>
    /// A one-line text field. In the game this is a real text input (typing, paste, IME); in pictures it is plain
    /// text. The field draws no frame of its own: the caller draws the box around <paramref name="r"/>.
    /// Returns true when the text changed. <paramref name="active"/> tells whether it has the keyboard now.
    /// <paramref name="selectAll"/>: when it takes the focus, all its text is selected, so typing replaces it.
    /// </summary>
    bool TextField(string id, Rect r, ref string text, string hint, TextStyle style, Colr color, Colr hintColor, bool takeFocus, bool interactive, out bool active, bool selectAll = false);

    void SetCursor(CursorShape shape);
}
