using System.Numerics;
using ModCloset2.Core;
using ModCloset2.Ui.Canvas;
using ModCloset2.Ui.Widgets;

namespace ModCloset2.Ui.Face;

/// <summary>
/// The options panel: a window of its own beside the closet with the shown thing's picture and whole name, its colour
/// bar, and every option group laid out openly (choices as a list, toggles as checkboxes, "Automatic" for the groups the
/// closet fits to the body, the versions, the slots it can be worn on). It opens on a click on a mod and on the
/// "Wearing" strip, follows the strip (and its "1 of 3" pager), and closes with its × or with the closet. The host
/// places it (<see cref="WindowGeometry.PanelPos"/>) and draws it with <see cref="DrawPanel"/> after the closet.
/// </summary>
public sealed partial class WindowFace
{
    private bool _panelOpen;

    /// <summary> Width of the panel in pixels at this scale. </summary>
    public static float PanelWidth(float u)
        => 340 * u;

    /// <summary> The gap between the closet and the panel in pixels at this scale. </summary>
    public static float PanelGap(float u)
        => 8 * u;

    /// <summary>
    /// The panel is to be drawn this frame: it was opened and the closet shows a shelf (not To sort, a search, the
    /// colours or the looks; it comes back with the shelf).
    /// </summary>
    public bool PanelShown
        => _panelOpen && _s is not null && _view == View.Shelf && !Searching && _s.Selected != Pieces.ToSort;

    /// <summary> The panel's own controls (for tests). </summary>
    public Gui PanelGui
        => _panelUi;

    public void ClosePanel()
    {
        _panelOpen = false;
        _panelUi.ClosePopup();
    }

    /// <summary> Opens the panel on this thing (the strip and the panel turn to it once the session lists it). </summary>
    private void OpenPanelOn(PartKey? key)
    {
        _panelOpen = true;
        if (key is { } k)
            _shownWant = k;
    }

    /// <summary> Draws the panel into the canvas's bounds (its own window), after <see cref="Draw"/> in the same frame. </summary>
    public void DrawPanel(ICanvas canvas, IClosetSession session)
    {
        _c  = canvas;
        _s  = session;
        _u  = canvas.U;
        _ui = _panelUi;
        try
        {
            _ui.Begin(canvas);
            var b = canvas.Bounds;
            Stats.Places["panel"] = b;
            canvas.FillRect(b, Theme.Window, P(14));
            canvas.StrokeRect(b, Theme.Line, P(14), Math.Max(1, _u));
            PanelContent(b);
            DrawPopups();
            if (_panelUi.PopupId.Length > 0 && _panelUi.PopupOpenedFrame == _panelUi.Frame)
                _mainUi.ClosePopup();
            FinishLostDrag();
            PointingDone();
            _ui.End();
        }
        finally
        {
            _ui = _mainUi;
        }
    }

    private void PanelContent(Rect b)
    {
        // the head: what this is, the × and (moved away) the button that puts it back; the rest of it moves the panel
        var head  = new Rect(b.X, b.Y, b.W, P(48));
        var close = new Rect(head.Right - P(12) - P(28), head.Center.Y - P(14), P(28), P(28));
        var dock  = _panelDetached ? new Rect(close.X - P(4) - P(28), close.Y, P(28), P(28)) : default;
        if (_panelDetached)
        {
            Stats.Places["panel-attach"] = dock;
            var ar = _ui.Interact("panel-attach", dock);
            if (ar.Hovered)
                _c.FillRect(dock, Theme.Picked, P(8));
            Painter.Icon(_c, Icons.Attach, dock.Center, P(15), ar.Hovered ? Theme.Text : Theme.Muted, 1.8f);
            _ui.Tip("panel-attach", dock, Loc.T("Put it back beside the closet"));
            if (ar.Clicked)
                AttachPanel();
        }

        var grab = new Rect(head.X, head.Y, (_panelDetached ? dock.X : close.X) - P(6) - head.X, head.H);
        PanelMove(grab);
        var grip = new Rect(b.Right - P(18), b.Bottom - P(18), P(18), P(18));
        PanelSize(grip);
        Painter.TextIn(_c, new Rect(head.X + P(18), head.Y, grab.W - P(18), head.H), Loc.T("Options & colours"), Theme.Label, Theme.Muted);
        Stats.Places["panel-close"] = close;
        var cr = _ui.Interact("panel-close", close);
        if (cr.Hovered)
            _c.FillRect(close, Theme.Picked, P(8));
        Painter.Icon(_c, Icons.Close, close.Center, P(14), cr.Hovered ? Theme.Text : Theme.Muted, 2);
        _ui.Tip("panel-close", close, Loc.T("Close"));
        _c.FillRect(new Rect(b.X + P(14), head.Bottom - 1, b.W - P(28), 1), Theme.Line);
        if (cr.Clicked)
        {
            ClosePanel();
            return;
        }

        var (wearing, index, count) = ShownWearing();
        var view = new Rect(b.X, head.Bottom, b.W, b.Bottom - head.Bottom - P(14));
        GripMarks(grip);
        if (wearing is null)
        {
            Painter.TextIn(_c, new Rect(view.X + P(18), view.Y + P(12), view.W - P(36), P(24)), Loc.T("Nothing on here right now."), Theme.Caption, Theme.Muted);
            return;
        }

        var content = PanelBody(view, 0, wearing, index, count, false);
        var scroll  = _ui.Scroll("panel", view, content, P(60));
        _ui.PushClip(view);
        PanelBody(view, scroll, wearing, index, count, true);
        _ui.PopClip();
        _ui.ScrollBar("panel", view, content, 5);
    }

    // ---- moving and sizing: once moved (or sized) the panel is detached, stays where it was put and is remembered ----

    /// <summary> Where a moved panel is ("x,y,w,h" in screen pixels; nothing remembered: beside the closet). </summary>
    public const string KeyPanel = "panel.place";

    private bool _panelDetached;
    private bool _panelLoaded;
    private Rect _panelPlace;   // the detached panel's place as last put (screen)
    private Rect _panelShownAt; // where the host was told to put it this frame (screen)
    private int _panelMoving;   // 0 none, 1 the head held, 2 the corner held
    private Vector2 _moveFrom;
    private Rect _moveStart;

    /// <summary> The smallest a detached panel can be made, in pixels at this scale. </summary>
    public static Vector2 PanelMinimum(float u)
        => new(260 * u, 240 * u);

    /// <summary> Is the panel moved away from the closet (it then stays where the user put it)? </summary>
    public bool PanelDetached
    {
        get
        {
            LoadPanelPlace();
            return _panelDetached;
        }
    }

    /// <summary>
    /// Where the host puts the panel this frame, in the coordinates of <paramref name="closet"/> and
    /// <paramref name="screen"/> (screen pixels in the game): beside the closet, or where the user moved it (no smaller
    /// than the minimum, no larger than the screen, and on the screen: a place remembered at another resolution comes
    /// back onto it).
    /// </summary>
    public Rect PanelRect(Rect closet, Rect screen)
    {
        LoadPanelPlace();
        Rect r;
        if (_panelDetached)
        {
            var min  = PanelMinimum(_u);
            var size = new Vector2(Math.Min(Math.Max(_panelPlace.W, min.X), screen.W), Math.Min(Math.Max(_panelPlace.H, min.Y), screen.H));
            var (pos, sz) = WindowGeometry.KeepOnScreen(new Vector2(_panelPlace.X, _panelPlace.Y), size, new Vector2(screen.X, screen.Y), new Vector2(screen.W, screen.H));
            r = new Rect(pos.X, pos.Y, sz.X, sz.Y);
        }
        else
        {
            var size = new Vector2(PanelWidth(_u), closet.H);
            var pos  = WindowGeometry.PanelPos(closet, size, screen, PanelGap(_u));
            r = new Rect(pos.X, pos.Y, size.X, size.Y);
        }

        _panelShownAt = r;
        return r;
    }

    private void LoadPanelPlace()
    {
        if (_panelLoaded || _s is null)
            return;
        _panelLoaded = true;
        if (ParsePlace(_s.Remembered(KeyPanel)) is { } place)
        {
            _panelDetached = true;
            _panelPlace    = new Rect(place.Pos.X, place.Pos.Y, place.Size.X, place.Size.Y);
        }
    }

    /// <summary> Back beside the closet, as before it was moved (also remembered). </summary>
    private void AttachPanel()
    {
        _panelDetached = false;
        _panelMoving   = 0;
        if (_s.Remembered(KeyPanel).Length > 0)
            _s.Remember(KeyPanel, string.Empty);
    }

    private void RememberPanel()
    {
        var r    = _panelShownAt;
        var text = string.Create(System.Globalization.CultureInfo.InvariantCulture, $"{r.X:0},{r.Y:0},{r.W:0},{r.H:0}");
        _panelPlace = r;
        if (_s.Remembered(KeyPanel) != text)
            _s.Remember(KeyPanel, text);
    }

    /// <summary> The head: dragged, it moves the panel (detaching it); a double click puts it back beside the closet. </summary>
    private void PanelMove(Rect grab)
    {
        Stats.Places["panel-head"] = grab;
        var resp = _ui.Interact("panel-head", grab, false);
        if (resp.Hovered)
            _c.SetCursor(CursorShape.Move);
        if (resp.DoubleClicked && _panelDetached)
        {
            AttachPanel();
            return;
        }

        if (resp.Pressed)
        {
            _panelMoving = 1;
            _moveFrom    = _ui.Mouse;
            _moveStart   = _panelShownAt;
        }

        if (_panelMoving == 1 && resp.Held)
        {
            var d = _ui.Mouse - _moveFrom;
            if (_panelDetached || d.Length() >= P(4))
            {
                _panelDetached = true;
                _panelPlace    = _moveStart with { X = _moveStart.X + d.X, Y = _moveStart.Y + d.Y };
            }
        }

        PanelLetGo();
    }

    /// <summary> The corner: dragged, it sizes the panel (detaching it too); its contents reflow, the body scrolls. </summary>
    private void PanelSize(Rect grip)
    {
        Stats.Places["panel-grip"] = grip;
        var resp = _ui.Interact("panel-grip", grip, false);
        if (resp.Hovered || _panelMoving == 2)
            _c.SetCursor(CursorShape.ResizeCorner);
        if (resp.Pressed)
        {
            _panelMoving = 2;
            _moveFrom    = _ui.Mouse;
            _moveStart   = _panelShownAt;
        }

        if (_panelMoving == 2 && resp.Held)
        {
            var d   = _ui.Mouse - _moveFrom;
            var min = PanelMinimum(_u);
            _panelDetached = true;
            _panelPlace    = _moveStart with { W = Math.Max(min.X, _moveStart.W + d.X), H = Math.Max(min.Y, _moveStart.H + d.Y) };
        }

        PanelLetGo();
    }

    /// <summary> A move or size ends when the button is let go: the place (as shown, on the screen) is remembered. </summary>
    private void PanelLetGo()
    {
        if (_panelMoving == 0 || _ui.In.Down[0])
            return;
        _panelMoving = 0;
        if (_panelDetached)
            RememberPanel();
    }

    /// <summary> Three short lines in the bottom-right corner: it can be sized there. </summary>
    private void GripMarks(Rect grip)
    {
        var col = _panelMoving == 2 || _ui.MouseOver(grip) ? Theme.Text.Fade(0.7f) : Theme.Faint.Fade(0.6f);
        for (var i = 1; i <= 3; ++i)
        {
            var o = P(4) * i;
            _c.Line(new Vector2(grip.Right - P(4) - o, grip.Bottom - P(4)), new Vector2(grip.Right - P(4), grip.Bottom - P(4) - o), col, Math.Max(1, P(1.2f)));
        }
    }

    /// <summary> Lays out (and with <paramref name="draw"/> draws) the panel's body; returns its height. </summary>
    private float PanelBody(Rect view, float scroll, WearingInfo wearing, int index, int count, bool draw)
    {
        var x   = view.X + P(18);
        var w   = view.W - P(36);
        var top = view.Y + P(14) - scroll;
        var y   = top;

        // the picture, and the whole name (wrapped, never cut)
        var pic = new Rect(x, y, P(64), P(64));
        var tx  = pic.Right + P(14);
        var tw  = x + w - tx;
        var lh  = _c.LineHeight(Theme.Label);
        var sh  = _c.LineHeight(Theme.Strong);
        var lines = Painter.Wrap(_c, wearing.Name, tw, Theme.Strong);
        if (draw)
        {
            Stats.Places["panel-pic/" + wearing.Key] = pic;
            PictureTile("panel-pic/" + wearing.Key, wearing.Key, pic, wearing.Picture, wearing.Icon, wearing.Name, P(10), Corners.All, P(32), 1.4f, Colr.Hex(0xF4ECF2, 0.45f));
            Painter.TextIn(_c, new Rect(tx, y + P(2), tw, lh), WearingLabel(wearing, count, false), Theme.Label, Theme.Muted);
            var ny = y + P(2) + lh + P(4);
            foreach (var line in lines)
            {
                _c.Text(new Vector2(tx, ny), line, Theme.Strong, Theme.Text);
                ny += sh;
            }

            Stats.Places["panel-name"] = new Rect(tx, y, tw, ny - y);
        }

        y = Math.Max(pic.Bottom, y + P(2) + lh + P(4) + lines.Count * sh) + P(20);

        // the colour bar, with the palette button for the whole piece, as the strip had it
        if (ShownColours(wearing, index) is { } live)
        {
            if (draw)
            {
                Painter.TextIn(_c, new Rect(x, y, w, lh), Loc.T("Colours"), Theme.Label, Theme.Muted);
                LiveBar(live, new Rect(x, y + lh + P(8), w, P(18)), P(8), Theme.Window, wholeButton: true);
            }

            y += lh + P(8) + P(24) + P(18);
        }

        // nothing to choose in this mod: one quiet line says so (the colours above still work)
        if (wearing.Options.Count == 0 && wearing.Versions <= 1 && _s.SlotsOf(wearing.Key).Count == 0)
        {
            if (draw)
                Painter.TextIn(_c, new Rect(x, y, w, P(20)), Loc.T("This mod has no options to choose."), Theme.Caption, Theme.Muted);
            y += P(20) + P(16);
        }

        // every group openly: the mod's own first, then those the closet fits to the body
        // (a list of its own: a click below may change the session's lists while this goes through them)
        foreach (var o in wearing.Options.Where(o => !o.Fits).Concat(wearing.Options.Where(o => o.Fits)).ToList())
            y = OptionBlock(x, y, w, wearing.Key, o, draw);

        if (wearing.Versions > 1)
        {
            y = BlockHead(x, y, w, "panel-versions", Loc.T("Version"), string.Empty, false, draw);
            for (var i = 0; i < wearing.Versions; ++i)
            {
                var v = i;
                if (ChoiceRow(new Rect(x, y, w, P(30)), "ver/" + i, Loc.F("Version {0}", i + 1), i == wearing.Version, false, string.Empty, draw) && i != wearing.Version)
                    _s.SetVersion(wearing.Key, v);
                y += P(30);
            }

            y += P(16);
        }

        var slots = _s.SlotsOf(wearing.Key);
        if (slots.Count > 0)
        {
            y = BlockHead(x, y, w, "panel-slots", Loc.T("Wear on"), string.Empty, false, draw);
            foreach (var slot in slots)
            {
                var hint = slot.Replaces.Length > 0 ? Loc.F("takes off {0}", slot.Replaces) : string.Empty;
                if (ChoiceRow(new Rect(x, y, w, P(30)), "wear-on/" + (int)slot.Slot, slot.Name, slot.On, false, hint, draw) && !slot.On)
                    _s.WearOn(wearing.Key, slot.Slot);
                y += P(30);
            }

            y += P(16);
        }

        return y - top + P(14);
    }

    /// <summary> One option group: its heading, then every choice (checkboxes for a group with several on at once). </summary>
    private float OptionBlock(float x, float y, float w, PartKey key, OptionChip given, bool draw)
    {
        // what the session says now, every frame: the ticks, the count and each click start from this
        var o     = CurrentOption(key, given);
        var count = o.Multi ? Loc.F("{0} on", o.Automatic ? 0 : o.On.Count) : string.Empty;
        y = BlockHead(x, y, w, "opt-group/" + o.Group, o.Group, count, o.Fits, draw);
        if (o.Fits)
        {
            var auto = o.Automatic && o.On.Count > 0 ? Loc.F("Automatic ({0})", o.On[0]) : Loc.T("Automatic");
            if (ChoiceRow(new Rect(x, y, w, P(30)), $"opt/{o.Group}/auto", auto, o.Automatic, false, string.Empty, draw) && !o.Automatic)
                _s.SetOption(key, o.Group, null);
            y += P(30);
        }

        for (var i = 0; i < o.Choices.Count; ++i)
        {
            var on = !o.Automatic && o.On.Contains(o.Choices[i]);
            if (ChoiceRow(new Rect(x, y, w, P(30)), $"opt/{o.Group}/{i}", o.Choices[i], on, o.Multi, string.Empty, draw) && (o.Multi || !on))
                SetChoice(key, o, i);
            y += P(30);
        }

        return y + P(16);
    }

    /// <summary> A block's heading: the group's name (a small figure first for a group fitted to the body), a count on the right. </summary>
    private float BlockHead(float x, float y, float w, string id, string text, string right, bool fits, bool draw)
    {
        var lh = _c.LineHeight(Theme.Label);
        if (draw)
        {
            var box = new Rect(x, y, w, lh);
            Stats.Places[id] = box;
            var tx = x;
            if (fits)
            {
                Painter.Icon(_c, Icons.Bodies, new Vector2(x + P(6), y + lh / 2), P(12), Theme.Muted, 1.6f);
                tx += P(16);
                _ui.Interact(id, box, false);
                _ui.Tip(id, box, Loc.T("Picked to fit your body"));
            }

            var rw = right.Length > 0 ? _c.Measure(right, Theme.Small).X : 0;
            Painter.TextIn(_c, new Rect(tx, y, x + w - tx - (rw > 0 ? rw + P(10) : 0), lh), text, Theme.Label, Theme.Muted);
            if (rw > 0)
                Painter.TextIn(_c, new Rect(x + w - rw, y, rw, lh), right, Theme.Small, Theme.Muted);
        }

        return y + lh + P(6);
    }

    /// <summary> One choice: a round mark (one of the group) or a box (one of several), the text, a quiet hint. True when clicked. </summary>
    private bool ChoiceRow(Rect row, string id, string text, bool on, bool box, string hint, bool draw)
    {
        if (!draw)
            return false;
        var hit = new Rect(row.X - P(6), row.Y + P(1), row.W + P(12), row.H - P(2));
        Stats.Places[id] = hit;
        var resp = _ui.Interact(id, hit);
        if (resp.Hovered)
            _c.FillRect(hit, Theme.Picked, P(8));
        var mark = new Vector2(row.X + P(8), row.Center.Y);
        if (box)
        {
            var sq = new Rect(mark.X - P(8), mark.Y - P(8), P(16), P(16));
            if (on)
            {
                _c.FillRect(sq, Theme.Accent, P(4));
                Painter.Icon(_c, Icons.Check, sq.Center, P(12), Theme.OnAccent, 2.4f);
            }
            else
                _c.StrokeRect(sq, Theme.EdgeStrong, P(4), P(1.5f));
        }
        else if (on)
        {
            _c.StrokeCircle(mark, P(7.5f), Theme.Accent, P(1.5f));
            _c.FillCircle(mark, P(4), Theme.Accent);
        }
        else
            _c.StrokeCircle(mark, P(7.5f), Theme.EdgeStrong, P(1.5f));

        var tx     = row.X + P(26);
        var hw     = hint.Length > 0 ? Math.Min(_c.Measure(hint, Theme.Small).X, (row.Right - tx) / 2) : 0;
        var tw     = row.Right - tx - (hw > 0 ? hw + P(10) : 0);
        var fitted = Painter.Fit(_c, text, tw, Theme.Caption);
        Painter.TextIn(_c, new Rect(tx, row.Y, tw, row.H), fitted, Theme.Caption, on ? Theme.Text : Theme.Text.Fade(0.85f));
        if (hw > 0)
            Painter.TextIn(_c, new Rect(row.Right - hw, row.Y, hw, row.H), hint, Theme.Small, Theme.Muted);
        if (fitted != text)
            _ui.Tip(id, hit, text);
        return resp.Clicked;
    }
}
