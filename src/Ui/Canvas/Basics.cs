using System.Globalization;
using System.Numerics;

namespace ModCloset2.Ui.Canvas;

/// <summary> An axis-aligned rectangle in screen pixels (top-left origin). </summary>
public readonly record struct Rect(float X, float Y, float W, float H)
{
    public float Right
        => X + W;

    public float Bottom
        => Y + H;

    public Vector2 Min
        => new(X, Y);

    public Vector2 Max
        => new(X + W, Y + H);

    public Vector2 Center
        => new(X + W / 2, Y + H / 2);

    public Vector2 Size
        => new(W, H);

    public bool IsEmpty
        => W <= 0 || H <= 0;

    public static Rect FromMinMax(Vector2 min, Vector2 max)
        => new(min.X, min.Y, max.X - min.X, max.Y - min.Y);

    public bool Contains(Vector2 p)
        => p.X >= X && p.Y >= Y && p.X < X + W && p.Y < Y + H;

    public bool Overlaps(Rect o)
        => o.X < Right && o.Right > X && o.Y < Bottom && o.Bottom > Y;

    /// <summary> Grows (or with negative values shrinks) the rectangle on every side. </summary>
    public Rect Expand(float by)
        => new(X - by, Y - by, W + 2 * by, H + 2 * by);

    public Rect Inset(float left, float top, float right, float bottom)
        => new(X + left, Y + top, Math.Max(0, W - left - right), Math.Max(0, H - top - bottom));

    public Rect Inset(float x, float y)
        => Inset(x, y, x, y);

    public Rect Offset(float dx, float dy)
        => this with { X = X + dx, Y = Y + dy };

    public Rect Intersect(Rect o)
    {
        var x = Math.Max(X, o.X);
        var y = Math.Max(Y, o.Y);
        var r = Math.Min(Right, o.Right);
        var b = Math.Min(Bottom, o.Bottom);
        return new Rect(x, y, Math.Max(0, r - x), Math.Max(0, b - y));
    }

    /// <summary> A box of the given size centred in this one. </summary>
    public Rect CenterBox(float w, float h)
        => new(X + (W - w) / 2, Y + (H - h) / 2, w, h);

    public override string ToString()
        => string.Create(CultureInfo.InvariantCulture, $"{X:0.#},{Y:0.#} {W:0.#}x{H:0.#}");
}

/// <summary> A colour with straight alpha. </summary>
public readonly record struct Colr(byte R, byte G, byte B, byte A = 255)
{
    public static readonly Colr Transparent = new(0, 0, 0, 0);

    /// <summary> From 0xRRGGBB and an opacity 0..1. </summary>
    public static Colr Hex(uint rgb, float alpha = 1f)
        => new((byte)(rgb >> 16), (byte)(rgb >> 8), (byte)rgb, (byte)Math.Clamp(MathF.Round(alpha * 255), 0, 255));

    public float Alpha
        => A / 255f;

    public uint Rgb
        => ((uint)R << 16) | ((uint)G << 8) | B;

    /// <summary> The same colour with its opacity multiplied. </summary>
    public Colr Fade(float factor)
        => this with { A = (byte)Math.Clamp(MathF.Round(A * factor), 0, 255) };

    /// <summary> Packed for Dear ImGui (IM_COL32: A B G R from the high byte down). </summary>
    public uint Abgr
        => ((uint)A << 24) | ((uint)B << 16) | ((uint)G << 8) | R;

    /// <summary> Mixes towards another colour (alpha mixed too). </summary>
    public Colr Mix(Colr o, float t)
        => new((byte)(R + (o.R - R) * t), (byte)(G + (o.G - G) * t), (byte)(B + (o.B - B) * t), (byte)(A + (o.A - A) * t));

    public string HexText
        => $"#{R:X2}{G:X2}{B:X2}";
}

/// <summary> Which corners of a rectangle are rounded. </summary>
[Flags]
public enum Corners : byte
{
    None        = 0,
    TopLeft     = 1,
    TopRight    = 2,
    BottomLeft  = 4,
    BottomRight = 8,
    Top         = TopLeft | TopRight,
    Bottom      = BottomLeft | BottomRight,
    Left        = TopLeft | BottomLeft,
    Right       = TopRight | BottomRight,
    All         = 15,
}

/// <summary> The two type faces: the body font, and the condensed display font for titles and shelf headings. </summary>
public enum FontFace : byte
{
    Body,
    Display,
}

public enum Weight : byte
{
    Regular,
    Medium,
    Bold,
}

/// <summary>
/// How a text is set. <see cref="Size"/> is in mockup pixels (CSS font size, the em); the canvas multiplies it by
/// its unit. <see cref="Tracking"/> is extra letter spacing in em.
/// </summary>
public readonly record struct TextStyle(FontFace Face, float Size, Weight Weight = Weight.Regular, float Tracking = 0f, bool Upper = false)
{
    public TextStyle With(Weight weight)
        => this with { Weight = weight };

    public TextStyle Sized(float size)
        => this with { Size = size };
}

/// <summary> The keys the closet listens to. </summary>
public enum Key : byte
{
    Enter,
    Escape,
    Left,
    Right,
    Up,
    Down,
    Z,
    Backspace,
}

public enum CursorShape : byte
{
    Arrow,
    Hand,

    /// <summary> Over something that moves a window (the options panel's head). </summary>
    Move,

    /// <summary> Over a corner that sizes a window. </summary>
    ResizeCorner,
}

/// <summary> What the mouse and keyboard did this frame, as the canvas saw it. </summary>
public sealed class InputState
{
    /// <summary> Mouse position in screen pixels. Far away when the mouse is not over the window (and no drag holds it). </summary>
    public Vector2 Mouse { get; set; } = new(-100000, -100000);

    /// <summary> Buttons held: 0 left, 1 right, 2 middle. </summary>
    public bool[] Down { get; } = new bool[3];

    /// <summary> Buttons that went down this frame. </summary>
    public bool[] Pressed { get; } = new bool[3];

    /// <summary> Buttons that went up this frame. </summary>
    public bool[] Released { get; } = new bool[3];
    public bool DoubleClicked { get; set; }

    /// <summary> Wheel notches this frame, positive is away from the user (scroll up). </summary>
    public float Wheel { get; set; }
    public HashSet<Key> Keys { get; } = new();
    public bool Ctrl { get; set; }

    /// <summary> A text field had the keyboard when the frame began (keys belong to it). </summary>
    public bool TextFocused { get; set; }

    /// <summary> The window has the keyboard focus (keys are meant for it). </summary>
    public bool Focused { get; set; } = true;

    /// <summary> Seconds since some start. </summary>
    public double Time { get; set; }

    public bool KeyPressed(Key key)
        => Keys.Contains(key);

    /// <summary> Clears the per-frame events (keeps what is held). </summary>
    public void NextFrame()
    {
        Array.Clear(Pressed);
        Array.Clear(Released);
        DoubleClicked = false;
        Wheel         = 0;
        Keys.Clear();
    }
}

/// <summary>
/// How a picture covers a box: the part of it (as texture coordinates) that fills the box, the overflow cropped. A
/// photo the closet took (<c>Pictures.IsPhoto</c>) keeps its middle, where the piece is; other pictures keep their top,
/// where a mod picture has the head. Both canvases go by it. Pure.
/// </summary>
public static class PictureCrop
{
    public static (System.Numerics.Vector2 Uv0, System.Numerics.Vector2 Uv1) Cover(float pictureW, float pictureH, float boxW, float boxH, bool middle)
    {
        var aspect = pictureW / Math.Max(1, pictureH);
        var box    = boxW / Math.Max(1, boxH);
        if (aspect > box)
        {
            var w = box / aspect;
            return (new System.Numerics.Vector2((1 - w) / 2, 0), new System.Numerics.Vector2((1 + w) / 2, 1));
        }

        var h = aspect / box;
        return middle
            ? (new System.Numerics.Vector2(0, (1 - h) / 2), new System.Numerics.Vector2(1, (1 + h) / 2))
            : (System.Numerics.Vector2.Zero, new System.Numerics.Vector2(1, h));
    }
}
