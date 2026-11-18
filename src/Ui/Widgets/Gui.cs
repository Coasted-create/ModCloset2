using System.Numerics;
using ModCloset2.Ui.Canvas;

namespace ModCloset2.Ui.Widgets;

/// <summary> What happened to one interactive thing this frame. </summary>
public readonly record struct Response(bool Hovered, bool Clicked, bool RightClicked, bool Pressed, bool Held, bool DoubleClicked);

/// <summary> A thing being dragged (a card on its way to another shelf). </summary>
public sealed class DragState
{
    public string SourceId { get; init; } = string.Empty;
    public object? Payload { get; init; }

    /// <summary> Where the pointer grabbed it, relative to the thing's top-left. </summary>
    public Vector2 GrabOffset { get; init; }
}

/// <summary>
/// The window's immediate-mode state: which thing is under the mouse or held, what is dragged, which popup is open,
/// scroll offsets, tooltips. Things are named by string ids; state lives here between frames, everything else is
/// drawn fresh each frame from the session. Rules kept here so every widget follows them: nothing under an open popup
/// reacts (a click outside only closes the popup), a click is a press and a release on the same thing without a
/// drag, a drag starts after 6 U of movement, the wheel scrolls only the area under the mouse.
/// </summary>
public sealed class Gui
{
    public const float DragThreshold = 6f;
    public const double TipDelay     = 0.45;

    private readonly Stack<Rect> _clips = new();
    private readonly Dictionary<string, float> _scroll = new(StringComparer.Ordinal);
    private readonly List<Rect> _popupRects     = new();
    private readonly List<Rect> _lastPopupRects = new();
    private readonly HashSet<Canvas.Key> _eatenKeys    = new();
    private readonly List<Rect> _overlayRects     = new();
    private readonly List<Rect> _lastOverlayRects = new();

    private ICanvas? _canvas;
    private int      _frame;
    private bool     _overlay;
    private bool     _clickEaten;
    private bool     _wheelUsed;

    private string? _pressed;
    private Vector2 _pressPos;
    private Rect    _pressRect;
    private string? _hovered;
    private string? _lastHovered;
    private double  _hoverSince;

    private string? _thumbDrag;
    private float   _thumbGrab;

    private (string Text, string[] Keys, Rect Near)? _tip;

    public ICanvas C
        => _canvas ?? throw new InvalidOperationException("Gui.Begin was not called");

    public InputState In
        => C.Input;

    public float U { get; private set; } = 1;
    public Vector2 Mouse
        => In.Mouse;

    public int Frame
        => _frame;

    public double Time
        => In.Time;

    /// <summary> The thing being dragged, once it has lifted (null before the threshold). </summary>
    public DragState? Drag { get; private set; }

    /// <summary> The open popup's id ("" when none), where it was opened, and what it is about. </summary>
    public string PopupId { get; private set; } = string.Empty;
    public Vector2 PopupAnchor { get; private set; }
    public object? PopupContext { get; private set; }
    public int PopupOpenedFrame { get; private set; }

    /// <summary> Index of the open submenu row in the open menu (-1 none). </summary>
    public int SubmenuIndex { get; set; } = -1;

    /// <summary> True while drawing the popup layer. </summary>
    public bool InOverlay
        => _overlay;

    /// <summary> Things under an open popup do not react to the mouse or keys. </summary>
    public bool Blocked
        => !_overlay && PopupId.Length > 0;

    /// <summary> Hover id of the last frame (for tests). </summary>
    public string? Hovered
        => _hovered;

    // ---- frame ----

    public void Begin(ICanvas canvas)
    {
        _canvas = canvas;
        U       = canvas.U;
        _frame++;
        _clips.Clear();
        _hovered    = null;
        _tip        = null;
        _wheelUsed  = false;
        _clickEaten = false;
        _eatenKeys.Clear();
        _lastPopupRects.Clear();
        _lastPopupRects.AddRange(_popupRects);
        _popupRects.Clear();
        _lastOverlayRects.Clear();
        _lastOverlayRects.AddRange(_overlayRects);
        _overlayRects.Clear();

        // a press that began in the popup and is still held (dragging in the colour picker) keeps it open
        if (PopupId.Length > 0 && !(_pressed is not null && In.Down[0] && !In.Pressed[0]))
        {
            if (In.KeyPressed(Canvas.Key.Escape) && !In.TextFocused)
            {
                ClosePopup();
                _eatenKeys.Add(Canvas.Key.Escape);
                // the popup was open when the frame began: the rest of this frame stays inert
                _clickEaten = true;
            }
            else if ((In.Pressed[0] || In.Pressed[1]) && PopupOpenedFrame != _frame && !_lastPopupRects.Any(r => r.Contains(In.Mouse)))
            {
                // a click outside only closes the popup; it does not reach what is underneath
                ClosePopup();
                _clickEaten = true;
            }
        }
    }

    public void End()
    {
        if (_tip is { } tip)
        {
            BeginOverlay();
            Painter.Tooltip(this, tip.Text, tip.Keys, tip.Near);
            EndOverlay();
        }

        if (_hovered != _lastHovered)
        {
            _lastHovered = _hovered;
            _hoverSince  = Time;
        }

        if (In.Released[0] || !In.Down[0])
        {
            _refused   = null;
            _pressed   = null;
            _thumbDrag = null;
            Drag       = null;
        }
    }

    // ---- layers and clipping ----

    public void BeginOverlay()
    {
        C.BeginOverlay();
        _overlay = true;
        _clips.Push(C.Bounds.Expand(4000));
    }

    public void EndOverlay()
    {
        if (_clips.Count > 0)
            _clips.Pop();
        _overlay = false;
        C.EndOverlay();
    }

    public void PushClip(Rect r)
    {
        var clip = _clips.Count > 0 ? _clips.Peek().Intersect(r) : r;
        _clips.Push(clip);
        C.PushClip(r);
    }

    public void PopClip()
    {
        if (_clips.Count > 0)
            _clips.Pop();
        C.PopClip();
    }

    /// <summary>
    /// Something clickable drawn in the overlay outside a popup (the toast). The overlay is drawn after the rest, so the
    /// rest learns of it a frame later: where it was last frame, the things underneath do not react.
    /// </summary>
    public void OverlayArea(Rect r)
        => _overlayRects.Add(r);

    private bool UnderOverlay(Vector2 p)
    {
        if (_overlay)
            return false;
        foreach (var r in _lastOverlayRects)
            if (r.Contains(p))
                return true;
        return false;
    }

    private bool InClip(Vector2 p)
        => (_clips.Count == 0 || _clips.Peek().Contains(p)) && !UnderOverlay(p);

    /// <summary> True when the mouse is over this rectangle and nothing above it (popup, clip edge) is in the way. </summary>
    public bool MouseOver(Rect r)
        => !Blocked && !_clickEaten && InClip(Mouse) && r.Contains(Mouse);

    // ---- pointer ----

    public Response Interact(string id, Rect r, bool clickable = true)
    {
        var hovered = !Blocked && !_clickEaten && Drag is null && InClip(Mouse) && r.Contains(Mouse) && (_pressed is null || _pressed == id)
         && _thumbDrag is null;
        if (hovered)
        {
            _hovered = id;
            if (clickable)
                C.SetCursor(CursorShape.Hand);
        }

        var pressed = hovered && In.Pressed[0];
        if (pressed)
        {
            _pressed   = id;
            _pressPos  = Mouse;
            _pressRect = r;
        }

        var clicked = hovered && _pressed == id && In.Released[0] && Drag is null && _refused != id;
        var right   = hovered && In.Pressed[1];
        var dbl     = hovered && In.DoubleClicked;
        var held    = _pressed == id && In.Down[0];
        return new Response(hovered, clicked, right, pressed, held, dbl);
    }

    /// <summary> Lets a held thing be dragged; returns true on the frame it lifts. </summary>
    public bool DragSource(string id, object payload)
    {
        if (Drag is not null || _pressed != id || !In.Down[0] || Blocked)
            return false;
        if (Vector2.Distance(Mouse, _pressPos) < DragThreshold * U)
            return false;

        Drag = new DragState { SourceId = id, Payload = payload, GrabOffset = _pressPos - _pressRect.Min };
        return true;
    }

    /// <summary>
    /// A drag that is not allowed (moves are locked): true once, on the frame a held thing would have lifted. Nothing
    /// lifts, and letting go then is no click either.
    /// </summary>
    public bool DragRefused(string id)
    {
        if (Drag is not null || _refused is not null || _pressed != id || !In.Down[0] || Blocked)
            return false;
        if (Vector2.Distance(Mouse, _pressPos) < DragThreshold * U)
            return false;
        _refused = id;
        return true;
    }

    /// <summary> A refused drag is still held. </summary>
    public bool Refusing
        => _refused is not null;

    private string? _refused;

    /// <summary> A place a dragged thing can be dropped: whether it is over it, and whether it was let go there. </summary>
    public (bool Over, bool Dropped) DropTarget(Rect r)
    {
        if (Drag is null || Blocked || !InClip(Mouse) || !r.Contains(Mouse))
            return (false, false);
        return (true, In.Released[0]);
    }

    /// <summary> Was this hovered long enough for its tooltip? Then shows it near the thing. </summary>
    public void Tip(string id, Rect near, string text, params string[] keys)
    {
        if (_hovered != id || _lastHovered != id || Time - _hoverSince < TipDelay || Drag is not null)
            return;
        _tip = (text, keys, near);
    }

    // ---- keys ----

    /// <summary> A key meant for the window: not while typing, not under a popup, and only once per frame. </summary>
    public bool Key(Canvas.Key key)
    {
        if (!In.Focused || In.TextFocused || Blocked || _eatenKeys.Contains(key) || !In.KeyPressed(key))
            return false;
        _eatenKeys.Add(key);
        return true;
    }

    // ---- popups ----

    public void OpenPopup(string id, Vector2 at, object? context = null)
    {
        PopupId          = id;
        PopupAnchor      = at;
        PopupContext     = context;
        PopupOpenedFrame = _frame;
        SubmenuIndex     = -1;
        _pressed         = null;
    }

    public void ClosePopup()
    {
        PopupId      = string.Empty;
        PopupContext = null;
        SubmenuIndex = -1;
    }

    public bool IsOpen(string id)
        => PopupId == id;

    /// <summary> Tells the popup logic where the popup is drawn (clicks there do not close it). </summary>
    public void PopupArea(Rect r)
    {
        _popupRects.Add(r);
        // the first frame has no rectangles from before: count this one at once
        if (PopupOpenedFrame == _frame)
            _lastPopupRects.Add(r);
    }

    // ---- scrolling ----

    public float ScrollOf(string id)
        => _scroll.GetValueOrDefault(id);

    public void SetScroll(string id, float y)
        => _scroll[id] = Math.Max(0, y);

    /// <summary>
    /// The scroll offset of an area, after this frame's wheel (only when the mouse is over the area and no inner area
    /// took the wheel) and clamped to the content.
    /// </summary>
    public float Scroll(string id, Rect viewport, float contentHeight, float step)
    {
        var max = Math.Max(0, contentHeight - viewport.H);
        var y   = _scroll.GetValueOrDefault(id);
        if (!_wheelUsed && In.Wheel != 0 && !Blocked && !_clickEaten && InClip(Mouse) && viewport.Contains(Mouse))
        {
            y          -= In.Wheel * step;
            _wheelUsed =  true;
        }

        y           = Math.Clamp(y, 0, max);
        _scroll[id] = y;
        return y;
    }

    /// <summary> The thin scrollbar thumb at the right edge of an area (drawn after its content); it can be dragged. </summary>
    public void ScrollBar(string id, Rect viewport, float contentHeight, float inset = 3)
    {
        var max = contentHeight - viewport.H;
        if (max <= 0.5f)
            return;
        var track  = viewport.Inset(0, 4 * U, inset * U, 4 * U);
        var thumbH = Math.Max(28 * U, track.H * viewport.H / contentHeight);
        var y      = _scroll.GetValueOrDefault(id);
        var thumb  = new Rect(track.Right - 4 * U, track.Y + (track.H - thumbH) * (y / max), 4 * U, thumbH);
        var hit    = new Rect(thumb.X - 5 * U, track.Y, thumb.W + 8 * U, track.H);
        var key    = id + "#thumb";
        var over   = !Blocked && !_clickEaten && Drag is null && hit.Contains(Mouse) && InClip(Mouse);
        if (over && In.Pressed[0])
        {
            _thumbDrag = key;
            _pressed   = key;
            _thumbGrab = thumb.Contains(Mouse) ? Mouse.Y - thumb.Y : thumbH / 2;
        }

        if (_thumbDrag == key && In.Down[0])
        {
            var t = Math.Clamp((Mouse.Y - _thumbGrab - track.Y) / Math.Max(1, track.H - thumbH), 0, 1);
            _scroll[id] = t * max;
            thumb       = thumb with { Y = track.Y + (track.H - thumbH) * t };
        }

        var lit = over || _thumbDrag == key;
        C.FillRect(thumb, lit ? Theme.Faint : Theme.EdgeStrong, 2 * U);
    }
}
