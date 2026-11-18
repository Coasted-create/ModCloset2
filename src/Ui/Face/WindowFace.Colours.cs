using System.Numerics;
using ModCloset2.Core;
using ModCloset2.Ui.Canvas;
using ModCloset2.Ui.Widgets;

namespace ModCloset2.Ui.Face;

public sealed partial class WindowFace
{
    private const string PopPicker   = "picker";
    private const string PopRowState = "row-state";
    private const string PopSaved    = "saved";

    /// <summary> The right side shows a shelf (or search results), the colours board, or the looks. </summary>
    private enum View : byte
    {
        Shelf,
        Colours,
        Looks,
    }

    private View _view;

    /// <summary> What the picker is open on: an area of a bar, or (with <see cref="Bar"/> null) an outfit colour. </summary>
    private sealed record PickTarget(string? Bar, int Area, ColourRole Role, Rect Anchor);

    /// <summary> A picker's area for the whole piece: every area at once (<see cref="IOutfitColours.SetPiece"/>). </summary>
    public const int WholePiece = -1;

    // the picker's colour while it is open (kept as hue / saturation / value, so the hue survives black and white)
    private float _pickH, _pickS, _pickV;
    private int  _pickDrag; // 0 none, 1 the field, 2 the hue strip
    private uint _pickSent;

    /// <summary> What the drag in progress colours (sent with done: false so far), until it is finished. </summary>
    private PickTarget? _dragTarget;

    /// <summary>
    /// A drag in the picker that has not been finished (the picker closed by any path before the mouse was let go: its
    /// area went away, Esc, the window closed, another picker opened): its last value goes once more with done: true,
    /// when what it colours is still there, so the colour engine closes the undo step.
    /// </summary>
    private void FinishColourDrag()
    {
        if (_pickDrag == 0)
            return;
        var t = _dragTarget;
        _pickDrag   = 0;
        _dragTarget = null;
        if (t is null || _s?.Colours is not { } oc)
            return;
        var there = t.Bar is null
            || (t.Area == WholePiece ? oc.Pieces.Any(p => p.Id == t.Bar) : AreaOf(oc, t.Bar, t.Area) is not null);
        if (there)
            SendPick(oc, t, true);
    }

    /// <summary> The host's window is closing: a drag in the picker that is still open is finished now. </summary>
    public void WindowClosing()
        => FinishColourDrag();

    /// <summary> Once per frame, after drawing: a drag whose picker is no longer open is finished. </summary>
    private void FinishLostDrag()
    {
        if (_pickDrag != 0 && !(_ui.IsOpen(PopPicker) && ReferenceEquals(_ui.PopupContext, _dragTarget)))
            FinishColourDrag();
    }

    // pointing at a segment: Point every frame, PointNothing once when it stops
    private bool _pointedNow;
    private bool _pointedBefore;

    private static readonly Colr PickerFill = Colr.Hex(0x221A29);
    private static readonly Colr Hatch1     = Colr.Hex(0x4A3A57);
    private static readonly Colr Hatch2     = Colr.Hex(0x2A2031);
    private static readonly Colr DotEdge    = Colr.Hex(0x6F6277);

    private ColourRole[] Roles
        => new[] { ColourRole.Main, ColourRole.Second, ColourRole.Accent, ColourRole.Metal };

    private static string RoleName(ColourRole role)
        => role switch
        {
            ColourRole.Main   => Loc.T("Main"),
            ColourRole.Second => Loc.T("Second"),
            ColourRole.Accent => Loc.T("Accent"),
            ColourRole.Metal  => Loc.T("Metal"),
            _                 => Loc.T("Its own"),
        };

    /// <summary> Ends the frame's pointing: the light goes out at once when the mouse left the segments. </summary>
    private void PointingDone()
    {
        if (_pointedBefore && !_pointedNow)
            _s.Colours?.PointNothing();
        _pointedBefore = _pointedNow;
        _pointedNow    = false;
    }

    // ---- small drawings ----

    /// <summary> A colour dot; dark colours get a thin edge so they show on the dark window. </summary>
    private void Dot(Vector2 center, float radius, uint rgb)
    {
        _c.FillCircle(center, radius, Colr.Hex(rgb));
        if (ColourMath.Lightness(rgb) < 0.25f)
            _c.StrokeCircle(center, radius - 0.5f, DotEdge, 1);
    }

    /// <summary> Painted parts: diagonal stripes. <paramref name="behind"/> is what is around it (to round its ends). </summary>
    private void Hatch(Rect r, float radius, Corners corners, Colr behind)
    {
        _c.FillRect(r, Hatch2, radius, corners);
        _ui.PushClip(r);
        var step = P(11.3f);
        for (var x = r.X - r.H; x < r.Right + r.H; x += step)
            _c.Line(new Vector2(x, r.Bottom + 1), new Vector2(x + r.H + 2, r.Y - 1), Hatch1, P(4));
        _ui.PopClip();
        // the stripes ignore the rounding: round them off with the colour around them
        if (radius > 0 && corners != Corners.None)
            _c.StrokeRect(r.Expand(radius), behind, radius * 2, radius, corners);
    }

    // ---- the rail's "Outfit colours" entry ----

    private void ColoursEntry(Rect r, IOutfitColours oc)
    {
        var resp   = _ui.Interact("rail/colours", r);
        var picked = _view == View.Colours && !Searching;
        Stats.Places["rail/colours"] = r;
        _c.FillRect(r, picked ? Theme.Picked : resp.Hovered ? Colr.Hex(0x281F2F) : Colr.Hex(0x221A29), P(10));
        _c.StrokeRect(r, picked ? Colr.Hex(0x5A4468) : Theme.Edge, P(10), Math.Max(1, _u));
        var x = r.X + P(10);
        foreach (var role in Roles)
        {
            Dot(new Vector2(x + P(8), r.Center.Y), P(8), oc.Palette.Of(role));
            x += P(20);
        }

        x += P(6);
        Painter.TextIn(_c, new Rect(x, r.Y, r.Right - P(30) - x, r.H), Loc.T("Outfit colours"), Theme.BodyMedium, picked ? Theme.Accent : Theme.Text);
        Painter.Icon(_c, Icons.ChevronR, new Vector2(r.Right - P(17), r.Center.Y), P(14), Theme.Muted, 2);
        if (resp.Clicked)
            OpenView(View.Colours);
    }

    private void OpenView(View view)
    {
        if (_s.Search.Length > 0)
            _s.Search = string.Empty;
        _slimSearch = false;
        _view       = view;
        _ui.SetScroll("board", 0);
        _ui.SetScroll("looks", 0);
    }

    // ---- the board ----

    private void ColoursWide(Rect r, IOutfitColours oc)
    {
        var x = r.X + P(24);
        var w = r.W - P(48);
        HeaderText(new Rect(x, r.Y + P(16), w, P(40)), Loc.T("Colours"), null, false);
        var y = r.Y + P(68);

        // one band as in the mockup when it fits; narrower, the switch goes on a line of its own
        var savedW = SavedWidth(oc);
        var swW    = P(64 * 3 + 14 * 2 + 6 + 14 + 44);
        var split  = w - P(44) - swW - savedW - P(2 + 48 * 2) < P(220);
        var boardH = split ? P(148 + 64) : P(148);
        var board  = new Rect(x, y, w, boardH);
        Board(board, oc, split, false);
        y += boardH;

        var trying = _s.Trying.Active;
        var bottom = trying ? r.Bottom - P(18) - P(60) - P(14) : r.Bottom - P(18);
        PieceRows(new Rect(x, y, w, bottom - y), oc, false);
        if (trying)
            TryBarWide(new Rect(x, r.Bottom - P(18) - P(60), w, P(60)));
        Toast(new Rect(x, r.Y, w, r.H), trying ? r.Bottom - P(18) - P(60) : r.Bottom - P(18));
    }

    private void ColoursSlim(Rect area, IOutfitColours oc)
    {
        var x = area.X + P(12);
        var w = area.W - P(24);
        HeaderText(new Rect(area.X + P(16), area.Y + P(10), area.W - P(32), P(32)), Loc.T("Colours"), null, true);
        var y      = area.Y + P(50);
        var boardH = P(14 + 86 + 14 + 64 + 14 + 72 + 14);
        Board(new Rect(x, y, w, boardH), oc, true, true);
        y += boardH;
        var trying = _s.Trying.Active;
        var barH   = trying ? SlimBarHeight(w) : 0;
        var bottom = trying ? area.Bottom - P(12) - barH - P(12) : area.Bottom - P(12);
        PieceRows(new Rect(x, y, w, bottom - y), oc, true);
        if (trying)
            TryBarSlim(new Rect(x, area.Bottom - P(12) - barH, w, barH));
        Toast(new Rect(x, area.Y, w, area.H), trying ? area.Bottom - P(12) - barH : area.Bottom - P(12));
    }

    private float SavedWidth(IOutfitColours oc)
    {
        var chips = oc.Saved.Count * (P(9 + 12 * 3 + 3 * 2 + 9) + P(6)) + P(28);
        var surprise = _c.Measure(Loc.T("Surprise me"), Theme.Caption).X + P(12 + 14 + 8 + 12);
        return Math.Max(chips, surprise);
    }

    /// <summary> Swatches, the "dress everything" switch, saved colours and "Surprise me". </summary>
    private void Board(Rect r, IOutfitColours oc, bool split, bool slim)
    {
        Stats.Places["board"] = r;
        _c.FillRect(r, Theme.Rail, P(14));
        var savedW = SavedWidth(oc);
        if (slim)
        {
            // stacked: swatches, the switch, the saved colours
            var y = r.Y + P(14);
            Swatches(new Rect(r.X + P(14), y, r.W - P(28), P(86)), oc);
            y += P(86 + 14);
            DressSwitch(new Rect(r.X + P(14), y, r.W - P(28), P(64)), oc);
            y += P(64 + 14);
            Saved(new Rect(r.X + P(14), y, r.W - P(28), P(72)), oc);
            return;
        }

        var top   = split ? new Rect(r.X, r.Y, r.W, P(148)) : r;
        var x     = top.X + P(22);
        var swW   = P(64 * 3 + 14 * 2 + 6 + 14 + 44);
        Swatches(new Rect(x, top.Center.Y - P(43), swW, P(86)), oc);
        x += swW + P(24);
        _c.FillRect(new Rect(x, top.Center.Y - P(42), 1, P(84)), Theme.Line);
        x += P(24);
        var savedX = top.Right - P(22) - savedW;
        if (split)
        {
            // the switch gets the line under the swatches; the saved colours stay at the right
            Saved(new Rect(savedX, top.Center.Y - P(36), savedW, P(72)), oc);
            _c.FillRect(new Rect(r.X + P(22), top.Bottom - 1, r.W - P(44), 1), Theme.Line);
            DressSwitch(new Rect(r.X + P(22), top.Bottom + P(8), r.W - P(44), P(48)), oc);
            return;
        }

        DressSwitch(new Rect(x, top.Center.Y - P(32), savedX - P(48) - x, P(64)), oc);
        _c.FillRect(new Rect(savedX - P(24), top.Center.Y - P(42), 1, P(84)), Theme.Line);
        Saved(new Rect(savedX, top.Center.Y - P(36), savedW, P(72)), oc);
    }

    private void Swatches(Rect r, IOutfitColours oc)
    {
        var x  = r.X;
        var lh = _c.LineHeight(Theme.Small);
        foreach (var role in Roles)
        {
            var metal = role == ColourRole.Metal;
            var size  = metal ? P(44) : P(64);
            if (metal)
                x += P(6);
            var box  = new Rect(x, r.Bottom - lh - P(6) - size - (metal ? P(10) : 0), size, size);
            var id   = "swatch/" + role;
            Stats.Places[id] = box;
            var resp = _ui.Interact(id, box);
            var rgb  = oc.Palette.Of(role);
            var open = _ui.IsOpen(PopPicker) && _ui.PopupContext is PickTarget { Bar: null } t && t.Role == role;
            var rad  = metal ? size / 2 : P(14);
            _c.FillRect(box, Colr.Hex(rgb), rad);
            if (ColourMath.Lightness(rgb) < 0.25f)
                _c.StrokeRect(box, DotEdge, rad, Math.Max(1, _u));
            if (open)
                _c.StrokeRect(box.Expand(P(4)), Theme.Accent, rad + P(4), P(2));
            else if (resp.Hovered)
                _c.StrokeRect(box.Expand(P(3)), Theme.EdgeStrong, rad + P(3), P(1.5f));
            Painter.TextIn(_c, new Rect(box.X - P(10), r.Bottom - lh, box.W + P(20), lh), RoleName(role), Theme.Small, Theme.Muted, 0.5f);
            if (resp.Clicked)
                OpenPicker(new PickTarget(null, 0, role, box), rgb);
            x += size + P(14);
        }
    }

    private void DressSwitch(Rect r, IOutfitColours oc)
    {
        var on    = oc.DressEverything;
        var label = Loc.T("Dress everything I try on in these colours");
        var knob  = new Rect(r.X, r.Y + P(2), P(42), P(24));
        var lines = Painter.Wrap(_c, label, r.W - P(54), Theme.BodyMedium, 2);
        var lh    = _c.LineHeight(Theme.BodyMedium);
        var hit   = new Rect(r.X, r.Y, Math.Min(r.W, P(54) + lines.Max(l => _c.Measure(l, Theme.BodyMedium).X)), Math.Max(P(28), lines.Count * lh));
        Stats.Places["dress"] = knob;
        var resp = _ui.Interact("dress", hit);
        _c.FillRect(knob, on ? Theme.Accent : resp.Hovered ? Theme.Faint : Theme.EdgeStrong, P(12));
        var dot = on ? new Vector2(knob.Right - P(12), knob.Center.Y) : new Vector2(knob.X + P(12), knob.Center.Y);
        _c.FillCircle(dot, P(9), on ? Theme.Window : Theme.Muted);
        var y = r.Y + (lines.Count == 1 ? (P(28) - lh) / 2 : 0);
        foreach (var l in lines)
        {
            _c.Text(new Vector2(r.X + P(54), y), l, Theme.BodyMedium, Theme.Text);
            y += lh;
        }

        var note = Loc.T("Anything you colour by hand keeps its own colour.");
        foreach (var l in Painter.Wrap(_c, note, r.W - (r.H > P(56) ? 0 : P(54)), Theme.Caption, 2))
        {
            _c.Text(new Vector2(r.X + (r.H > P(56) ? 0 : P(54)), y + P(6)), l, Theme.Caption, Theme.Muted);
            y += _c.LineHeight(Theme.Caption);
        }

        if (resp.Clicked)
            oc.DressEverything = !on;
    }

    private void Saved(Rect r, IOutfitColours oc)
    {
        Painter.TextIn(_c, new Rect(r.X, r.Y, r.W, P(14)), Loc.T("Saved"), Theme.Label, Theme.Muted);
        var x = r.X;
        var y = r.Y + P(24);
        for (var i = 0; i < oc.Saved.Count; ++i)
        {
            var saved = oc.Saved[i];
            var w     = P(9 + 12 * 3 + 3 * 2 + 9);
            if (x + w > r.Right - P(34))
                break;
            var box  = new Rect(x, y, w, P(28));
            var id   = "saved/" + i;
            Stats.Places[id] = box;
            var resp = _ui.Interact(id, box);
            _c.FillRect(box, resp.Hovered ? Theme.Picked : Theme.Card, P(14));
            if (saved.InUse)
                _c.StrokeRect(box, Theme.Accent, P(14), Math.Max(1, _u));
            var dx = box.X + P(9 + 6);
            foreach (var rgb in new[] { saved.Colours.Main, saved.Colours.Second, saved.Colours.Accent })
            {
                Dot(new Vector2(dx, box.Center.Y), P(6), rgb);
                dx += P(15);
            }

            _ui.Tip(id, box, Loc.T("Use these colours"));
            if (resp.Clicked)
                oc.UseSaved(i);
            if (resp.RightClicked)
                _ui.OpenPopup(PopSaved, _ui.Mouse, i);
            x += w + P(6);
        }

        var plus     = new Rect(x, y, P(28), P(28));
        Stats.Places["saved/+"] = plus;
        var plusResp = _ui.Interact("saved/+", plus);
        if (plusResp.Hovered)
            _c.FillRect(plus, Theme.Picked, P(14));
        _c.StrokeRect(plus, Theme.EdgeStrong, P(14), Math.Max(1, _u), Corners.All, P(3), P(2));
        Painter.Icon(_c, Icons.Plus, plus.Center, P(12), Theme.Muted, 2.2f);
        _ui.Tip("saved/+", plus, Loc.T("Save these colours"));
        if (plusResp.Clicked)
            oc.SaveCurrent();

        var text = Loc.T("Surprise me");
        var sw   = Math.Min(r.W, _c.Measure(text, Theme.Caption).X + P(12 + 14 + 8 + 12));
        var sur  = new Rect(r.X, y + P(28 + 12), r.W > sw + P(40) ? r.W : sw, P(32));
        Stats.Places["surprise"] = sur;
        var sresp = _ui.Interact("surprise", sur);
        if (sresp.Hovered)
            _c.FillRect(sur, Theme.Picked, P(8));
        _c.StrokeRect(sur, Theme.EdgeStrong, P(8), Math.Max(1, _u));
        Painter.Icon(_c, Icons.Dice, new Vector2(sur.X + P(12 + 7), sur.Center.Y), P(14), Theme.Text, 1.8f);
        Painter.TextIn(_c, new Rect(sur.X + P(12 + 14 + 8), sur.Y, sur.W - P(12 + 14 + 8 + 12), sur.H), text, Theme.Caption, Theme.Text);
        if (sresp.Clicked)
            oc.Surprise();
    }

    // ---- what you are wearing: one row per bar ----

    private void PieceRows(Rect area, IOutfitColours oc, bool slim)
    {
        Painter.TextIn(_c, new Rect(area.X, area.Y + P(18), area.W, P(14)), Loc.T("What you are wearing"), Theme.Label, Theme.Muted);
        var list = new Rect(area.X, area.Y + P(40), area.W, area.H - P(40));
        if (oc.Problem.Length > 0)
        {
            Message(list, oc.Problem, string.Empty, Icons.Info, Theme.Gold);
            return;
        }

        var pieces = oc.Pieces;
        if (pieces.Count == 0)
        {
            Message(list, Loc.T("Nothing with colours is on right now."), Loc.T("Put on clothes or jewellery and their colours show here."), Icons.Palette, Theme.Muted);
            return;
        }

        var rowH    = slim ? P(88) : P(56);
        var legendH = slim ? P(16 + 40) : P(16 + 18);
        var content = pieces.Count * (rowH + P(8)) - P(8) + legendH;
        var view    = new Rect(list.X - P(6), list.Y, list.W + P(12), list.H);
        var scroll  = _ui.Scroll("board", view, content, P(80));
        _ui.PushClip(view);
        var y = list.Y - scroll;
        foreach (var piece in pieces)
        {
            if (y + rowH >= list.Y && y <= list.Bottom)
                PieceRow(new Rect(list.X, y, list.W, rowH), piece, slim);
            y += rowH + P(8);
        }

        Legend(new Rect(list.X + P(4), y - P(8) + P(16), list.W - P(8), legendH - P(16)), slim);
        _ui.PopClip();
        _ui.ScrollBar("board", new Rect(list.X, list.Y, list.W + P(16), list.H), content, 6);
    }

    private void PieceRow(Rect r, PieceColours piece, bool slim)
    {
        Stats.Places["row/" + piece.Id] = r;
        _c.FillRect(r, Theme.Rail, P(12));
        var tile = new Rect(r.X + P(12), slim ? r.Y + P(10) : r.Center.Y - P(18), P(36), P(36));
        PictureTile($"colour-row/{piece.Id}/{piece.Key}", piece.Key, tile, piece.Picture, piece.Icon, piece.Name, P(8), Corners.All, P(20), 1.5f, Colr.Hex(0xF4ECF2, 0.5f));

        var state = new Rect(r.Right - P(12) - P(32), tile.Center.Y - P(16), P(32), P(32));
        var sid   = "row-state/" + piece.Id;
        Stats.Places[sid] = state;
        var sresp = _ui.Interact(sid, state);
        if (sresp.Hovered || _ui.IsOpen(PopRowState) && Equals(_ui.PopupContext, piece.Id))
            _c.FillRect(state, Theme.Picked, P(8));
        Painter.Icon(_c, piece.HasOwn ? Icons.Pin : Icons.Link, state.Center, P(16), piece.HasOwn ? Theme.Accent : Theme.Muted, 1.8f);
        _ui.Tip(sid, state, piece.HasOwn ? Loc.T("Has colours of its own") : Loc.T("Follows the outfit colours"));
        if (sresp.Clicked)
            _ui.OpenPopup(PopRowState, new Vector2(state.Right - P(240), state.Bottom + P(6)), piece.Id);

        var textX = tile.Right + P(14);
        var nameW = slim ? state.X - P(10) - textX : P(190);
        var lh    = _c.LineHeight(Theme.Label);
        var bh    = _c.LineHeight(Theme.BodyBold);
        var ty    = tile.Center.Y - (lh + P(1) + bh) / 2;

        // the picture and the name: the whole piece in one colour, as a dye would do it
        var whole = new Rect(tile.X - P(4), tile.Y - P(4), textX + nameW - tile.X + P(8), tile.H + P(8));
        WholePieceTarget(piece, "piece/" + piece.Id, whole, P(10));
        Painter.TextIn(_c, new Rect(textX, ty, nameW, lh), piece.PieceName, Theme.Label, Theme.Muted);
        Painter.TextIn(_c, new Rect(textX, ty + lh + P(1), nameW, bh), piece.Name, Theme.BodyBold, Theme.Text);

        var bar = slim
            ? new Rect(r.X + P(12), r.Bottom - P(12) - P(22), r.W - P(24), P(22))
            : new Rect(textX + nameW + P(14), r.Center.Y - P(11), state.X - P(14) - (textX + nameW + P(14)), P(22));
        LiveBar(piece, bar, P(11), Theme.Rail);
    }

    private void Legend(Rect r, bool slim)
    {
        var a  = Loc.T("Coloured by hand, keeps its own colour");
        var b  = Loc.T("Painted on, has no colours to change");
        var lh = _c.LineHeight(Theme.Small);
        Painter.Icon(_c, Icons.Pin, new Vector2(r.X + P(7), r.Y + lh / 2), P(14), Theme.Accent, 2);
        var aw = Painter.TextIn(_c, new Rect(r.X + P(22), r.Y, r.W - P(22), lh), a, Theme.Small, Theme.Muted);
        var bx = slim ? r.X : r.X + P(22) + aw + P(22);
        var by = slim ? r.Y + lh + P(8) : r.Y;
        Hatch(new Rect(bx, by + (lh - P(12)) / 2, P(22), P(12)), P(6), Corners.All, Theme.Window);
        Painter.TextIn(_c, new Rect(bx + P(30), by, r.Right - bx - P(30), lh), b, Theme.Small, Theme.Muted);
    }

    // ---- the bar ----

    /// <summary>
    /// A piece's colour bar: segments sized by share (each at least a little wide, so it stays hittable), 2 px gaps,
    /// rounded ends, a pin on areas coloured by hand, painted areas hatched and not clickable, a quiet placeholder while
    /// the piece is read. Pointing at a segment lights the area up on the character; a click opens the picker.
    /// </summary>
    /// <summary>
    /// A place that opens the picker for the whole piece (the row's picture and name; the small button at the strip's
    /// bar): hover wash, its tooltip, the accent edge while that picker is open.
    /// </summary>
    private void WholePieceTarget(PieceColours piece, string id, Rect r, float radius)
    {
        Stats.Places[id] = r;
        var usable = !piece.Reading && piece.Areas.Any(a => !a.Painted);
        var resp   = _ui.Interact(id, r, usable);
        var open   = _ui.IsOpen(PopPicker) && _ui.PopupContext is PickTarget { Area: WholePiece } t && t.Bar == piece.Id;
        if (open)
            _c.StrokeRect(r, Theme.Accent, radius, P(1.5f));
        else if (resp.Hovered && usable)
            _c.FillRect(r, Theme.HoverWash, radius);
        if (usable)
            _ui.Tip(id, r, Loc.T("Colour the whole piece"));
        if (resp.Clicked && usable)
            OpenPicker(new PickTarget(piece.Id, WholePiece, CommonRole(piece) ?? ColourRole.Own, r), MainRgb(piece));
    }

    /// <summary> The colour of the piece's largest area that can be coloured (where the whole-piece picker starts). </summary>
    private static uint MainRgb(PieceColours piece)
        => piece.Areas.Where(a => !a.Painted).OrderByDescending(a => a.Share).Select(a => a.Rgb).FirstOrDefault();

    /// <summary> What every colourable area of the piece follows, when they all follow the same; else null. </summary>
    public static ColourRole? CommonRole(PieceColours piece)
    {
        var roles = piece.Areas.Where(a => !a.Painted).Select(a => a.Pinned ? ColourRole.Own : a.Follows).Distinct().ToList();
        return roles.Count == 1 ? roles[0] : null;
    }

    private void LiveBar(PieceColours piece, Rect r, float radius, Colr behind, bool wholeButton = false)
    {
        // on the strip: a small button at the bar's end colours the whole piece
        if (wholeButton && !piece.Reading && piece.Areas.Count > 0)
        {
            var size = Math.Max(P(22), r.H + P(6));
            var btn  = new Rect(r.Right - size, r.Center.Y - size / 2, size, size);
            r = r with { W = Math.Max(P(12), r.W - size - P(6)) };
            WholePieceTarget(piece, "whole/" + piece.Id, btn, P(7));
            Painter.Icon(_c, Icons.Palette, btn.Center, P(14), Theme.Muted, 1.7f);
        }

        Stats.Places["bar/" + piece.Id] = r;
        if (piece.Reading || piece.Areas.Count == 0)
        {
            _c.FillRect(r, Theme.Card, radius);
            _c.StrokeRect(r, Theme.Edge, radius, Math.Max(1, _u), Corners.All, P(4), P(3));
            var phase = (float)(_ui.Time * 0.8 % 1.0);
            _c.FillRect(new Rect(r.X + (r.W - P(40)) * phase, r.Y + P(3), P(40), r.H - P(6)), Theme.Edge.Fade(0.7f), (r.H - P(6)) / 2);
            Stats.Places["bar-reading/" + piece.Id] = r;
            return;
        }

        var widths = SegmentWidths(piece.Areas, r.W, P(2), Math.Min(P(12), r.W / piece.Areas.Count));
        var x      = r.X;
        for (var i = 0; i < piece.Areas.Count; ++i)
        {
            var area    = piece.Areas[i];
            var corners = (i == 0 ? Corners.Left : Corners.None) | (i == piece.Areas.Count - 1 ? Corners.Right : Corners.None);
            var seg     = new Rect(x, r.Y, widths[i], r.H);
            var id      = $"seg/{piece.Id}/{area.Index}";
            Stats.Places[id] = seg;
            x += widths[i] + P(2);

            if (area.Painted)
            {
                // painted texture: nothing to change, so no click; it still says what it is
                Hatch(seg, radius, corners, behind);
                _ui.Interact(id, seg, false);
                _ui.Tip(id, seg, area.Label);
                continue;
            }

            var resp = _ui.Interact(id, seg);
            _c.FillRect(seg, Colr.Hex(area.Rgb), radius, corners);
            if (ColourMath.Lightness(area.Rgb) < 0.2f)
                _c.StrokeRect(seg, Theme.EdgeStrong, radius, Math.Max(1, _u), corners);
            if (area.Pinned && seg.W >= P(16))
                Painter.Icon(_c, Icons.Pin, seg.Center, P(12), ColourMath.Lightness(area.Rgb) > 0.6f ? Theme.OnAccent : Theme.Text, 2.2f);
            var open = _ui.IsOpen(PopPicker) && _ui.PopupContext is PickTarget t && t.Bar == piece.Id && t.Area == area.Index;
            if (open)
                _c.StrokeRect(seg.Expand(P(4)), Theme.Accent, radius + P(4), P(2), corners);
            else if (resp.Hovered)
                _c.StrokeRect(seg.Expand(P(2)), Theme.Text.Fade(0.6f), radius + P(2), P(1.5f), corners);

            if (resp.Hovered)
            {
                // lights the area up on the character; the colours engine needs this every frame while pointed at
                _s.Colours?.Point(piece.Id, area.Index);
                _pointedNow = true;
                // what it is, and what the engine says about it (its share; a note when it is dormant or coloured elsewhere)
                var what = area.Pinned ? Loc.F("{0} · coloured by hand", area.Label) : area.Label;
                _ui.Tip(id, seg, area.ShareText.Length > 0 ? Loc.F("{0} · {1}", what, area.ShareText) : what);
            }

            if (resp.Clicked)
                OpenPicker(new PickTarget(piece.Id, area.Index, area.Follows, seg), area.Rgb);
        }
    }

    /// <summary> Widths by share, every segment at least <paramref name="min"/>; what the small ones gain the others give. </summary>
    public static float[] SegmentWidths(IReadOnlyList<ColourArea> areas, float width, float gap, float min)
    {
        var n      = areas.Count;
        var avail  = Math.Max(0, width - gap * (n - 1));
        var total  = Math.Max(0.0001f, areas.Sum(a => Math.Max(0, a.Share)));
        var w      = areas.Select(a => Math.Max(0, a.Share) / total * avail).ToArray();
        var raised = 0f;
        for (var i = 0; i < n; ++i)
            if (w[i] < min)
            {
                raised += min - w[i];
                w[i]   =  min;
            }

        if (raised > 0)
        {
            var room = w.Where(v => v > min).Sum(v => v - min);
            for (var i = 0; i < n && room > 0; ++i)
                if (w[i] > min)
                    w[i] -= (w[i] - min) / room * Math.Min(raised, room);
        }

        return w;
    }

    // ---- the picker ----

    private void OpenPicker(PickTarget target, uint rgb)
    {
        // another picker opening while a drag is open (it should not happen, but the undo step must close)
        FinishColourDrag();
        (_pickH, _pickS, _pickV) = ColourMath.ToHsv(rgb);
        _pickDrag = 0;
        _pickSent = rgb;
        _ui.OpenPopup(PopPicker, new Vector2(target.Anchor.Center.X, target.Anchor.Bottom), target);
    }

    private ColourArea? AreaOf(IOutfitColours oc, string bar, int index)
    {
        foreach (var p in oc.Pieces)
            if (p.Id == bar)
                return p.Areas.FirstOrDefault(a => a.Index == index);
        return null;
    }

    private void SendPick(IOutfitColours oc, PickTarget t, bool done)
    {
        var rgb = ColourMath.FromHsv(_pickH, _pickS, _pickV);
        if (!done && rgb == _pickSent)
            return;
        _pickSent = rgb;
        if (t.Bar is null)
            oc.SetPalette(t.Role, rgb, done);
        else if (t.Area == WholePiece)
            oc.SetPiece(t.Bar, rgb, done);
        else
            oc.SetArea(t.Bar, t.Area, rgb, done);
    }

    private void Picker(IOutfitColours oc, PickTarget t)
    {
        var wholePiece = t.Bar is not null && t.Area == WholePiece;
        var piece      = wholePiece ? oc.Pieces.FirstOrDefault(p => p.Id == t.Bar) : null;
        var area       = t.Bar is null || wholePiece ? null : AreaOf(oc, t.Bar, t.Area);
        if (t.Bar is not null && area is null && piece is null)
        {
            // the piece came off, or its areas changed, while the picker was open (a drag on it ends: its target is gone)
            FinishColourDrag();
            _ui.ClosePopup();
            return;
        }

        var u     = _u;
        var w     = P(264);
        var inner = w - P(28);
        var title = wholePiece ? Loc.T("The whole piece") : area?.Label ?? Loc.F("{0} colour", RoleName(t.Role));
        var note  = wholePiece ? string.Empty : area?.ShareText ?? Loc.T("everything that follows it");
        var foot  = wholePiece ? Loc.T("Every part of it takes this colour, as a dye would.")
            : area is null ? Loc.T("Everything that follows this colour changes with it.") : Loc.T("This area is lit up on your character while you point at it.");
        var follows = new List<ColourRole>();
        if (area is not null || piece is not null)
        {
            follows.AddRange(new[] { ColourRole.Main, ColourRole.Second, ColourRole.Accent });
            if (area is not null ? area.Metal || area.Follows == ColourRole.Metal : piece!.Areas.Any(a => a.Metal))
                follows.Add(ColourRole.Metal);
            follows.Add(ColourRole.Own);
        }

        // what the area is, on the right of the title; a long note (dormant, coloured elsewhere) gets its own lines
        var noteW     = _c.Measure(note, Theme.Small).X;
        var noteLong  = noteW > inner * 0.55f;
        var noteLines = noteLong ? Painter.Wrap(_c, note, inner, Theme.Small, 4) : new List<string>();
        var noteH     = noteLines.Count * _c.LineHeight(Theme.Small) + (noteLines.Count > 0 ? P(8) : 0);
        var footLines = Painter.Wrap(_c, foot, inner, Theme.Small, 3);
        var gridRows  = (follows.Count + 1) / 2;
        var h = P(14) + P(20) + P(10) + noteH + P(120) + P(10) + P(12) + P(10)
              + (follows.Count > 0 ? P(14) + P(10) + gridRows * P(32) + (gridRows - 1) * P(6) + P(10) : 0)
              + footLines.Count * _c.LineHeight(Theme.Small) + P(14);

        // under the segment or swatch, its notch pointing at it; above it when there is no room below
        var b     = _c.Bounds;
        var a     = _ui.PopupAnchor;
        var below = a.Y + P(10) + h <= b.Bottom - P(8);
        var y     = below ? a.Y + P(10) : t.Anchor.Y - P(10) - h;
        var x     = Math.Clamp(a.X - w / 2, b.X + P(8), b.Right - P(8) - w);
        var box   = new Rect(x, Math.Max(b.Y + P(8), y), w, h);
        _ui.PopupArea(box);
        Stats.Places["picker"] = box;
        Painter.Shadow(_c, box, P(14), u, 20, 9, 0.55f);
        _c.FillRect(box, PickerFill, P(14));
        _c.StrokeRect(box, Theme.EdgeStrong, P(14), Math.Max(1, u));
        var nx = Math.Clamp(a.X, box.X + P(20), box.Right - P(20));
        if (below)
        {
            var notch = new PathShape("M0 7L7 0L14 7Z", 14, 7);
            _c.Shape(notch, new Rect(nx - P(7), box.Y - P(7) + 1, P(14), P(7)), Colr.Transparent, 0, PickerFill);
            _c.Line(new Vector2(nx - P(7), box.Y + 0.5f), new Vector2(nx, box.Y - P(7) + 1), Theme.EdgeStrong, Math.Max(1, u));
            _c.Line(new Vector2(nx, box.Y - P(7) + 1), new Vector2(nx + P(7), box.Y + 0.5f), Theme.EdgeStrong, Math.Max(1, u));
        }

        var cx = box.X + P(14);
        var cy = box.Y + P(14);
        var nw = noteLong ? 0 : noteW;
        Painter.TextIn(_c, new Rect(cx, cy, inner - (nw > 0 ? Math.Min(nw, inner * 0.55f) + P(8) : 0), P(20)), title, Theme.BodyBold, Theme.Text);
        if (nw > 0)
            Painter.TextIn(_c, new Rect(box.Right - P(14) - nw, cy, nw, P(20)), note, Theme.Small, Theme.Muted, 1);
        cy += P(20 + 10);
        foreach (var l in noteLines)
        {
            Painter.TextIn(_c, new Rect(cx, cy, inner, _c.LineHeight(Theme.Small)), l, Theme.Small, Theme.Gold);
            cy += _c.LineHeight(Theme.Small);
        }

        if (noteLines.Count > 0)
            cy += P(8);

        // the field: saturation across, brightness down
        var field = new Rect(cx, cy, inner, P(120));
        Stats.Places["picker-field"] = field;
        var hue   = Colr.Hex(ColourMath.FromHsv(_pickH, 1, 1));
        _c.Gradient(field, Colr.Hex(0xFFFFFF), hue, hue, Colr.Hex(0xFFFFFF));
        _c.Gradient(field, Colr.Hex(0x000000, 0), Colr.Hex(0x000000, 0), Colr.Hex(0x000000), Colr.Hex(0x000000));
        _c.StrokeRect(field.Expand(P(10)), PickerFill, P(20), P(10));
        var fresp = _ui.Interact("picker-field", field);
        var cursor = new Vector2(field.X + _pickS * field.W, field.Y + (1 - _pickV) * field.H);
        _c.StrokeCircle(cursor, P(6) + 1, Colr.Hex(0x000000, 0.5f), 1);
        _c.StrokeCircle(cursor, P(5), Colr.Hex(0xFFFFFF), P(2));
        cy += P(120 + 10);

        // the hue strip
        var strip = new Rect(cx, cy, inner, P(12));
        Stats.Places["picker-hue"] = strip;
        for (var i = 0; i < 6; ++i)
        {
            var c0 = Colr.Hex(ColourMath.FromHsv(i / 6f, 1, 1));
            var c1 = Colr.Hex(ColourMath.FromHsv((i + 1) / 6f, 1, 1));
            var sx = strip.X + strip.W * i / 6;
            _c.Gradient(new Rect(sx, strip.Y, strip.W / 6 + 0.5f, strip.H), c0, c1, c1, c0);
        }

        _c.StrokeRect(strip.Expand(P(6)), PickerFill, P(12), P(6));
        var hresp = _ui.Interact("picker-hue", strip.Expand(P(4)));
        var thumb = new Rect(strip.X + _pickH * strip.W - P(4), strip.Y - P(3), P(8), P(18));
        _c.StrokeRect(thumb.Expand(1), Colr.Hex(0x000000, 0.5f), P(5), 1);
        _c.StrokeRect(thumb, Colr.Hex(0xFFFFFF), P(4), P(2));
        cy += P(12 + 10);

        // dragging: many changes while moving, one "done" on release; the popup stays open meanwhile
        if (fresp.Pressed)
            (_pickDrag, _dragTarget) = (1, t);
        else if (hresp.Pressed)
            (_pickDrag, _dragTarget) = (2, t);
        if (_pickDrag != 0)
        {
            var m = _ui.Mouse;
            if (_pickDrag == 1)
            {
                _pickS = Math.Clamp((m.X - field.X) / field.W, 0, 1);
                _pickV = 1 - Math.Clamp((m.Y - field.Y) / field.H, 0, 1);
            }
            else
                _pickH = Math.Clamp((m.X - strip.X) / strip.W, 0, 0.9999f);

            if (_ui.In.Released[0] || !_ui.In.Down[0])
            {
                SendPick(oc, t, true);
                _pickDrag   = 0;
                _dragTarget = null;
            }
            else
                SendPick(oc, t, false);
        }

        if (follows.Count > 0 && (area is not null || piece is not null))
        {
            var current = area is not null ? area.Follows : CommonRole(piece!);
            Painter.TextIn(_c, new Rect(cx, cy, inner, P(14)), Loc.T("Follows"), Theme.Label, Theme.Muted);
            cy += P(14 + 10);
            var bw = (inner - P(6)) / 2;
            for (var i = 0; i < follows.Count; ++i)
            {
                var role   = follows[i];
                var btn    = new Rect(cx + i % 2 * (bw + P(6)), cy + i / 2 * P(38), bw, P(32));
                var id     = "follow/" + role;
                Stats.Places[id] = btn;
                var resp   = _ui.Interact(id, btn);
                var chosen = current == role;
                _c.FillRect(btn, chosen ? Theme.Accent.Fade(0.1f) : resp.Hovered ? Theme.Picked : Colr.Transparent, P(8));
                _c.StrokeRect(btn, chosen ? Theme.Accent : Theme.EdgeStrong, P(8), Math.Max(1, u));
                if (role == ColourRole.Own)
                    Painter.Icon(_c, Icons.Pin, new Vector2(btn.X + P(16), btn.Center.Y), P(12), Theme.Muted, 2.2f);
                else
                    Dot(new Vector2(btn.X + P(16), btn.Center.Y), P(6), oc.Palette.Of(role));
                Painter.TextIn(_c, new Rect(btn.X + P(30), btn.Y, btn.W - P(36), btn.H), RoleName(role), Theme.Caption, Theme.Text);
                if (resp.Clicked && !chosen && t.Bar is not null)
                {
                    if (!wholePiece)
                        oc.Follow(t.Bar, t.Area, role);
                    else if (role == ColourRole.Own)
                        oc.ResetPiece(t.Bar);
                    else
                        oc.FollowPiece(t.Bar, role);
                }
            }

            cy += gridRows * P(38) - P(6) + P(10);
        }

        foreach (var l in footLines)
        {
            _c.Text(new Vector2(cx, cy), l, Theme.Small, Theme.Muted);
            cy += _c.LineHeight(Theme.Small);
        }
    }

    // ---- the row's state menu and the saved colours' menu ----

    /// <summary> The role an area takes when the whole piece follows the outfit colours again. </summary>
    public static ColourRole NaturalRole(PieceColours piece, ColourArea area)
    {
        if (area.Follows != ColourRole.Own && !area.Pinned)
            return area.Follows;
        if (area.Metal)
            return ColourRole.Metal;
        var order = piece.Areas.Where(a => !a.Painted && !a.Metal).Select(a => a.Index).ToList();
        return order.IndexOf(area.Index) switch
        {
            0 => ColourRole.Main,
            1 => ColourRole.Second,
            _ => ColourRole.Accent,
        };
    }

    private void RowStateMenu(IOutfitColours oc, string barId)
    {
        var piece = oc.Pieces.FirstOrDefault(p => p.Id == barId);
        if (piece is null)
        {
            _ui.ClosePopup();
            return;
        }

        var items = new List<MenuItem>
        {
            new("follow", Loc.T("Follow the outfit colours")) { Icon = Icons.Link },
            new("own", Loc.T("Back to its own colours")) { Icon = Icons.Undo },
        };
        var picked = Menu.Draw(_ui, PopRowState, _ui.PopupAnchor, items, 220);
        if (picked?.Id == "follow")
        {
            foreach (var area in piece.Areas.Where(a => !a.Painted))
                oc.Follow(piece.Id, area.Index, NaturalRole(piece, area));
        }
        else if (picked?.Id == "own")
            oc.ResetPiece(piece.Id);
    }

    private void SavedMenu(IOutfitColours oc, int index)
    {
        var picked = Menu.Draw(_ui, PopSaved, _ui.PopupAnchor, new List<MenuItem> { new("remove", Loc.T("Remove these colours")) { Icon = Icons.Close } }, 200);
        if (picked is not null && index < oc.Saved.Count)
            oc.RemoveSaved(index);
    }
}
