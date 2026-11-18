using System.Numerics;
using ModCloset2.Ui.Canvas;

namespace ModCloset2.Ui.Face;

/// <summary> The game keys the closet wants hidden from the game while the mouse is over its window. </summary>
[Flags]
public enum GameKeys : byte
{
    None = 0,

    /// <summary> Enter: Keep, while something is tried on. </summary>
    Enter = 1,

    /// <summary> Esc: Put back while trying; closes a popup, a drag or the slim search. </summary>
    Escape = 2,

    /// <summary> The four arrows: step through the cards. </summary>
    Arrows = 4,

    /// <summary> Z, only together with Ctrl: Undo. </summary>
    UndoZ = 8,
}

/// <summary> Pure geometry for the game window: keeping a remembered place on screen, and the resize handles. </summary>
public static class WindowGeometry
{
    /// <summary>
    /// Brings a remembered window place back onto the screen: no larger than the screen, and moved inside it (a window
    /// remembered on a monitor that is gone, or at a resolution that is gone, must come back).
    /// </summary>
    public static (Vector2 Pos, Vector2 Size) KeepOnScreen(Vector2 pos, Vector2 size, Vector2 screenPos, Vector2 screenSize)
    {
        var s = Vector2.Min(size, screenSize);
        var p = Vector2.Clamp(pos, screenPos, screenPos + Vector2.Max(Vector2.Zero, screenSize - s));
        return (p, s);
    }

    /// <summary> Size of ImGui's resize grip in the bottom-right corner (as ImGui 1.88 computes it). </summary>
    public static float GripSize(float fontSize, float windowRounding)
        => MathF.Floor(MathF.Max(fontSize * 1.35f, windowRounding + 1 + fontSize * 0.2f));

    /// <summary>
    /// Is the mouse on the window's resize grip or a resize edge? A press there resizes the window and must not also
    /// reach the cards under it.
    /// </summary>
    public static bool OnResizeHandle(Vector2 mouse, Rect window, float grip, float edge = 4)
    {
        var onGrip = mouse.X >= window.Right - grip && mouse.Y >= window.Bottom - grip;
        var onEdge = mouse.X >= window.Right - edge || mouse.Y >= window.Bottom - edge || mouse.X <= window.X + edge;
        return onGrip || onEdge;
    }
}
