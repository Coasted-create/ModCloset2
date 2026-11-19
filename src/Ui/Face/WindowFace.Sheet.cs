using System.Numerics;
using ModCloset2.Core;
using ModCloset2.Ui.Canvas;
using ModCloset2.Ui.Widgets;

namespace ModCloset2.Ui.Face;

/// <summary>
/// The look sheet: the panel beside the closet in its second mode, for one look (<see cref="ILookSheet"/>). A head with
/// the look's name (rename, ×), the change strip while the look is on and changed (Pin as new / Update), and four
/// sections, each folding to one summary line: Pieces (a row per slot, Saved or Leave as is; a row opens to that
/// piece's colour bar and options), Your colours (a swatch each, the picker on a click; Take mine now), Showing
/// (weapon, hat, visor) and Wear it by itself (a gearset, a job, a Glamourer design). Every switch is the session's.
/// </summary>
public sealed partial class WindowFace
{
    private const string PopGearsets = "sheet-gearsets";
    private const string PopJobs     = "sheet-jobs";

    /// <summary> Which sections are folded (remembered, comma separated: pieces, colours, showing, wear). </summary>
    public const string KeySheetFolded = "sheet.folded";

    private static readonly TextStyle CaptionBold = Theme.Caption.With(Weight.Bold);

    private Guid? _sheetLook;
    private string? _sheetOpenSlot;
    private string? _sheetRename;
    private bool _sheetRenameFocus;
    private bool _sheetRenameActive;

    /// <summary> Width of the panel showing a look sheet, in pixels at this scale. </summary>
    public static float SheetWidth(float u)
        => 420 * u;

    /// <summary> The look the panel shows the sheet of (null: the options of the shown piece). </summary>
    public Guid? SheetLook
        => _sheetLook;

    private ILookSheet? SheetApi
        => _s?.Looks?.Sheet;

    private LookSheet? CurrentSheet
        => _sheetLook is { } id && SheetApi is { } api ? api.Sheet(id) : null;

    /// <summary> Opens the panel on a look's sheet (a click on a look card, or on the "Wearing now" look). </summary>
    private void OpenSheet(Guid look)
    {
        if (SheetApi is null)
            return;
        if (_sheetLook != look)
        {
            _sheetOpenSlot = null;
            _sheetRename   = null;
            _panelUi.SetScroll("sheet", 0);
        }

        _sheetLook = look;
        _panelOpen = true;
    }

    private bool SheetFolded(string section)
        => _s.Remembered(KeySheetFolded).Split(',').Contains(section);

    private void ToggleFold(string section)
    {
        var set = _s.Remembered(KeySheetFolded).Split(',', StringSplitOptions.RemoveEmptyEntries).ToList();
        if (!set.Remove(section))
            set.Add(section);
        _s.Remember(KeySheetFolded, string.Join(",", set));
    }

    // ---- the sheet in the panel ----

    private void SheetContent(Rect b, ILookSheet api, LookSheet sheet)
    {
        // the head: the star (green while the look is on), "Look sheet · on now", the name; rename, put back, ×
        var head  = new Rect(b.X, b.Y, b.W, P(60));
        var close = new Rect(head.Right - P(12) - P(30), head.Y + P(14), P(30), P(30));
        var pen   = new Rect(close.X - P(4) - P(30), close.Y, P(30), P(30));
        var dock  = _panelDetached ? new Rect(pen.X - P(4) - P(30), close.Y, P(30), P(30)) : default;
        if (HeadButton("sheet-close", close, Icons.Close, Loc.T("Close the look sheet")))
        {
            ClosePanel();
            return;
        }

        if (HeadButton("sheet-rename", pen, Icons.Pencil, Loc.T("Rename this look")))
        {
            _sheetRename      = sheet.Name;
            _sheetRenameFocus = true;
            _sheetRenameActive = false;
        }

        if (_panelDetached && HeadButton("panel-attach", dock, Icons.Attach, Loc.T("Put it back beside the closet")))
            AttachPanel();

        var star = new Rect(head.X + P(16), head.Y + P(14), P(32), P(32));
        var grab = new Rect(star.Right, head.Y, (_panelDetached ? dock.X : pen.X) - P(6) - star.Right, head.H);
        PanelMove(grab);
        var grip = new Rect(b.Right - P(18), b.Bottom - P(18), P(18), P(18));
        PanelSize(grip);
        _c.FillRect(star, (sheet.OnNow ? Theme.Good : Theme.Muted).Fade(0.14f), P(8));
        Painter.Icon(_c, Icons.Looks, star.Center, P(16), sheet.OnNow ? Theme.Good : Theme.Muted, 1.8f);
        var tx = star.Right + P(12);
        var tw = grab.Right - tx;
        var lh = _c.LineHeight(Theme.Label);
        Painter.TextIn(_c, new Rect(tx, star.Y - P(1), tw, lh), sheet.OnNow ? Loc.T("Look sheet · on now") : Loc.T("Look sheet"), Theme.Label, Theme.Muted);
        var nameBox = new Rect(tx, star.Y + lh + P(1), tw, P(20));
        Stats.Places["sheet-name"] = nameBox;
        if (_sheetRename is not null && _s.Looks is { } looks)
            SheetRenameField(looks, sheet, nameBox.Expand(P(2)));
        else
            Painter.TextIn(_c, nameBox, sheet.Name, Theme.Strong.Sized(16), Theme.Text);

        var y = head.Bottom;
        if (sheet.ChangeText.Length > 0 && _s.Looks is { } l)
        {
            ChangeStrip(new Rect(b.X + P(14), y, b.W - P(28), P(40)), l, sheet);
            y += P(40) + P(8);
        }

        var view    = new Rect(b.X, y, b.W, b.Bottom - y - P(14));
        var content = SheetBody(view, 0, api, sheet, false);
        var scroll  = _ui.Scroll("sheet", view, content, P(60));
        _ui.PushClip(view);
        SheetBody(view, scroll, api, sheet, true);
        _ui.PopClip();
        _ui.ScrollBar("sheet", view, content, 5);
        GripMarks(grip);
    }

    private bool HeadButton(string id, Rect r, string icon, string tip)
    {
        Stats.Places[id] = r;
        var resp = _ui.Interact(id, r);
        if (resp.Hovered)
            _c.FillRect(r, Theme.Picked, P(8));
        Painter.Icon(_c, icon, r.Center, P(15), resp.Hovered ? Theme.Text : Theme.Muted, 1.8f);
        _ui.Tip(id, r, tip);
        return resp.Clicked;
    }

    private void SheetRenameField(ILooks looks, LookSheet sheet, Rect r)
    {
        Stats.Places["sheet-rename-field"] = r;
        _c.FillRect(r, Theme.Rail, P(6));
        _c.StrokeRect(r, Theme.Accent.Fade(0.7f), P(6), Math.Max(1, _u));
        var text  = _sheetRename!;
        var enter = _ui.In.TextFocused && _ui.In.KeyPressed(Key.Enter);
        if (_c.TextField("sheet-rename", r.Inset(P(6), 0), ref text, Loc.T("Name of the look"), Theme.Body, Theme.Text, Theme.Muted, _sheetRenameFocus, !_ui.Blocked, out var active, selectAll: true))
            _sheetRename = text;
        _sheetRenameFocus = false;
        if (enter && _sheetRename is { } typed && typed.Trim().Length > 0)
        {
            looks.Rename(sheet.Id, typed.Trim());
            _sheetRename = null;
            return;
        }

        if (_sheetRenameActive && !active)
            _sheetRename = null;
        _sheetRenameActive |= active;
    }

    /// <summary> "You changed the shoes · Pin as new · Update": while the look is on and something of it changed. </summary>
    private void ChangeStrip(Rect r, ILooks looks, LookSheet sheet)
    {
        Stats.Places["sheet-change"] = r;
        _c.FillRect(r, Theme.Gold.Fade(0.1f), P(10));
        _c.StrokeRect(r, Theme.Gold.Fade(0.4f), P(10), Math.Max(1, _u));
        Painter.Icon(_c, Icons.Info, new Vector2(r.X + P(12) + P(7), r.Center.Y), P(14), Theme.Gold, 1.8f);
        var upd  = Loc.T("Update");
        var pin  = Loc.T("Pin as new");
        var updW = _c.Measure(upd, Theme.SmallBold).X + P(18);
        var pinW = _c.Measure(pin, Theme.Small).X + P(18);
        var ub   = new Rect(r.Right - P(8) - updW, r.Center.Y - P(13), updW, P(26));
        var pb   = new Rect(ub.X - P(6) - pinW, ub.Y, pinW, P(26));
        var tx   = r.X + P(12) + P(14) + P(8);
        Painter.TextIn(_c, new Rect(tx, r.Y, pb.X - P(8) - tx, r.H), sheet.ChangeText, CaptionBold, Theme.Text);
        if (SmallButton("sheet-pin", pb, pin, false))
            looks.PinNow();
        if (SmallButton("sheet-update", ub, upd, true))
            looks.UpdateFromNow(sheet.Id);
    }

    private bool SmallButton(string id, Rect r, string text, bool accent)
    {
        Stats.Places[id] = r;
        var resp = _ui.Interact(id, r);
        _c.FillRect(r, accent ? Theme.Accent.Fade(resp.Hovered ? 0.2f : 0.1f) : resp.Hovered ? Theme.Picked : Colr.Transparent, P(7));
        _c.StrokeRect(r, accent ? Theme.Accent : Theme.EdgeStrong, P(7), Math.Max(1, _u));
        Painter.TextIn(_c, r, text, accent ? Theme.SmallBold : Theme.Small, accent ? Theme.Accent : Theme.Text, 0.5f);
        return resp.Clicked;
    }

    // ---- the body: four sections ----

    private float SheetBody(Rect view, float scroll, ILookSheet api, LookSheet sheet, bool draw)
    {
        var x   = view.X + P(14);
        var w   = view.W - P(28);
        var top = view.Y + P(4) - scroll;
        var y   = top;

        // Pieces
        var saved  = sheet.Pieces.Count(p => p.Keep != Keep.LeaveAlone);
        var left   = sheet.Pieces.Count - saved;
        var pchips = sheet.Pieces.Where(p => p.Keep == Keep.Saved).SelectMany(p => p.Colours.Take(1)).Take(6).ToList();
        y = SectionHead(x, y, w, "pieces", Loc.T("Pieces"), Loc.F("{0} saved · {1} left alone", saved, left), pchips, null, draw);
        if (!SheetFolded("pieces"))
        {
            foreach (var p in sheet.Pieces)
                y = PieceRow(x, y, w, api, sheet, p, draw);
            y += P(10);
        }

        // Your colours
        var csaved = sheet.Colours.Count(c => c.Keep == Keep.Saved);
        var cchips = sheet.Colours.Select(c => c.Rgb).Take(6).ToList();
        y = SectionHead(x, y, w, "colours", Loc.T("Your colours"), Loc.F("{0} saved · {1} left alone", csaved, sheet.Colours.Count - csaved), cchips,
            sheet.Colours.Any(c => c.Keep == Keep.Saved) ? () => api.TakeMineNow(sheet.Id) : null, draw);
        if (!SheetFolded("colours"))
        {
            var cw = (w - P(14)) / 2;
            for (var i = 0; i < sheet.Colours.Count; ++i)
            {
                var cell = new Rect(x + i % 2 * (cw + P(14)), y + i / 2 * P(34), cw, P(32));
                if (draw)
                    ColourCell(cell, api, sheet, sheet.Colours[i]);
            }

            y += (sheet.Colours.Count + 1) / 2 * P(34) + P(10);
        }

        // Showing
        var shows = string.Join(" · ", sheet.Showing.Select(s => Loc.F("{0}: {1}", s.Name, s.Keep == Keep.LeaveAlone ? Loc.T("left alone") : s.On ? s.OnWord : s.OffWord)));
        y = SectionHead(x, y, w, "showing", Loc.T("Showing"), shows, Array.Empty<uint>(), null, draw);
        if (!SheetFolded("showing"))
        {
            foreach (var s in sheet.Showing)
            {
                if (draw)
                    ShowingRow(new Rect(x, y, w, P(32)), api, sheet, s);
                y += P(34);
            }

            y += P(10);
        }

        // Wear it by itself
        y = SectionHead(x, y, w, "wear", Loc.T("Wear it by itself"), BindingSummary(api, sheet.Binding), Array.Empty<uint>(), null, draw);
        if (!SheetFolded("wear"))
            y = BindingCards(x, y, w, api, sheet, draw);

        return y - top + P(10);
    }

    /// <summary> A section's line: its name, the summary, a few colour dots (folded), and the chevron; a click folds it. </summary>
    private float SectionHead(float x, float y, float w, string id, string label, string summary, IReadOnlyList<uint> chips, Action? takeMine, bool draw)
    {
        var h = P(40);
        if (!draw)
            return y + h;
        var folded = SheetFolded(id);
        var row    = new Rect(x - P(4), y, w + P(8), h);
        Stats.Places["sheet-section/" + id] = row;

        // "Take mine now" sits on the colours' line while they are open (its press comes before the line's)
        var right = row.Right - P(26);
        if (takeMine is not null && !folded)
        {
            var text = Loc.T("Take mine now");
            var bw   = P(10) + P(12) + P(6) + _c.Measure(text, Theme.Small).X + P(10);
            var btn  = new Rect(right - bw, row.Center.Y - P(13), bw, P(26));
            Stats.Places["sheet-take-mine"] = btn;
            var br = _ui.Interact("sheet-take-mine", btn);
            _c.FillRect(btn, br.Hovered ? Theme.Picked : Colr.Transparent, P(7));
            _c.StrokeRect(btn, Theme.EdgeStrong, P(7), Math.Max(1, _u));
            Painter.Icon(_c, Icons.Refresh, new Vector2(btn.X + P(10) + P(6), btn.Center.Y), P(12), Theme.Text, 1.8f);
            Painter.TextIn(_c, new Rect(btn.X + P(10) + P(12) + P(6), btn.Y, bw - P(38), btn.H), text, Theme.Small, Theme.Text);
            _ui.Tip("sheet-take-mine", btn, Loc.T("Every saved colour takes the one you wear now"));
            if (br.Clicked)
                takeMine();
            right = btn.X - P(8);
        }

        var resp = _ui.Interact("sheet-section/" + id, row);
        if (resp.Hovered)
            _c.FillRect(row, Theme.HoverWash, P(8));
        if (folded)
            _c.FillRect(new Rect(row.X, row.Bottom - 1, row.W, 1), Theme.Line);
        var lw = Math.Min(P(134), w * 0.36f);
        Painter.TextIn(_c, new Rect(x, y, lw, h), label, Theme.Label, folded ? Theme.Muted : Theme.Accent);
        var dots = folded ? Math.Min(chips.Count, 6) : 0;
        var dw   = dots > 0 ? dots * P(13) : 0;
        Painter.TextIn(_c, new Rect(x + lw + P(8), y, right - (x + lw + P(8)) - dw - P(6), h), summary, Theme.Small, folded ? Theme.Text : Theme.Muted);
        for (var i = 0; i < dots; ++i)
            Dot(new Vector2(right - dw + i * P(13) + P(5), row.Center.Y), P(5), chips[i]);
        Painter.Icon(_c, folded ? Icons.ChevronR : Icons.ChevronD, new Vector2(row.Right - P(13), row.Center.Y), P(12), Theme.Muted, 2);
        _ui.Tip("sheet-section/" + id, row, folded ? Loc.T("Show") : Loc.T("Fold"));
        if (resp.Clicked)
            ToggleFold(id);
        return y + h;
    }

    /// <summary> "Saved | Leave as is" (or "Shown | Sheathed"): two halves, the chosen one lit. Returns the half clicked (-1 none). </summary>
    private int TwoWay(Rect r, string id, string a, string b, int chosen, float pad = 8)
    {
        Stats.Places[id] = r;
        var aw = _c.Measure(a, Theme.SmallBold).X + P(pad * 2);
        var ra = r with { W = aw };
        var rb = new Rect(r.X + aw, r.Y, r.W - aw, r.H);
        Stats.Places[id + "/0"] = ra;
        Stats.Places[id + "/1"] = rb;
        var pa = _ui.Interact(id + "/0", ra, chosen != 0);
        var pb = _ui.Interact(id + "/1", rb, chosen != 1);
        _c.StrokeRect(r, Theme.Edge, r.H / 2, Math.Max(1, _u));
        if (chosen == 0)
            _c.FillRect(ra.Inset(1, 1), Theme.Accent.Fade(0.14f), r.H / 2, Corners.Left);
        else if (pa.Hovered)
            _c.FillRect(ra.Inset(1, 1), Theme.HoverWash, r.H / 2, Corners.Left);
        if (chosen == 1)
            _c.FillRect(rb.Inset(1, 1), Theme.Accent.Fade(0.14f), r.H / 2, Corners.Right);
        else if (pb.Hovered)
            _c.FillRect(rb.Inset(1, 1), Theme.HoverWash, r.H / 2, Corners.Right);
        Painter.TextIn(_c, ra, a, chosen == 0 ? Theme.SmallBold : Theme.Small, chosen == 0 ? Theme.Accent : Theme.Muted, 0.5f);
        Painter.TextIn(_c, rb, b, chosen == 1 ? Theme.SmallBold : Theme.Small, chosen == 1 ? Theme.Accent : Theme.Muted, 0.5f);
        return pa.Clicked && chosen != 0 ? 0 : pb.Clicked && chosen != 1 ? 1 : -1;
    }

    private float TwoWayWidth(string a, string b, float pad = 8)
        => _c.Measure(a, Theme.SmallBold).X + _c.Measure(b, Theme.SmallBold).X + P(pad * 4);

    private static string SlotIcon(string slot)
        => slot switch
        {
            "head"     => Icons.Head,
            "body"     => Icons.Top,
            "hands"    => Icons.Gloves,
            "legs"     => Icons.Bottoms,
            "feet"     => Icons.Shoes,
            "ears"     => Icons.Earrings,
            "neck"     => Icons.Necklace,
            "wrists"   => Icons.Bracelets,
            "mainhand" => Icons.MainHand,
            "offhand"  => Icons.OffHand,
            _ when slot.StartsWith("ring", StringComparison.Ordinal) => Icons.Rings,
            _          => Icons.Other,
        };

    /// <summary>
    /// One slot of the look: the piece (picture, name, its options, colour dots) and Saved | Leave as is. A slot the
    /// look empties says "Nothing"; a slot it leaves alone is a quiet dashed row. A click on a saved row opens it.
    /// </summary>
    private float PieceRow(float x, float y, float w, ILookSheet api, LookSheet sheet, SheetPiece p, bool draw)
    {
        var open   = _sheetOpenSlot == p.Slot && p.Keep == Keep.Saved && p.Part is not null;
        var rowH   = P(34);
        var extraH = open ? PieceOpenHeight(w, p) : 0;
        if (!draw)
            return y + rowH + extraH + P(3);

        var box  = new Rect(x - P(6), y, w + P(12), rowH + extraH);
        var line = new Rect(box.X, y, box.W, rowH);
        var id   = "sheet-piece/" + p.Slot;
        Stats.Places[id] = line;
        if (open)
        {
            _c.FillRect(box, Theme.Card, P(10));
            _c.StrokeRect(box, Theme.EdgeStrong, P(10), Math.Max(1, _u));
        }
        else if (p.Keep == Keep.LeaveAlone)
            _c.StrokeRect(line.Inset(0, 1), Theme.Edge, P(8), Math.Max(1, _u), Corners.All, P(5), P(4));

        // the pill first (its press wins over the row's)
        var saved = Loc.T("Saved");
        var leave = Loc.T("Leave as is");
        var pw    = TwoWayWidth(saved, leave);
        var pill  = new Rect(line.Right - P(6) - pw, line.Center.Y - P(11), pw, P(22));
        var pick  = TwoWay(pill, id + "/keep", saved, leave, p.Keep == Keep.LeaveAlone ? 1 : 0);
        if (pick == 0)
            api.SetPiece(sheet.Id, p.Slot, Keep.Saved);
        else if (pick == 1)
            api.SetPiece(sheet.Id, p.Slot, Keep.LeaveAlone);

        var resp = _ui.Interact(id, line, p.Keep == Keep.Saved && p.Part is not null);
        if (resp.Hovered && !open)
            _c.FillRect(line, Theme.HoverWash, P(8));

        var thumb = new Rect(line.X + P(8), line.Center.Y - P(12), P(24), P(24));
        if (p.Keep == Keep.Nothing)
        {
            _c.StrokeRect(thumb, Theme.EdgeStrong, P(6), Math.Max(1, _u), Corners.All, P(3), P(3));
            Painter.Icon(_c, Icons.Nothing, thumb.Center, P(14), Theme.Muted, 1.6f);
        }
        else if (p.Keep == Keep.LeaveAlone)
            Painter.Icon(_c, SlotIcon(p.Slot), thumb.Center, P(15), Theme.Faint, 1.6f);
        else
            Painter.Tile(_c, thumb, p.Picture, SlotIcon(p.Slot), p.Name, P(6), Corners.All, P(14), 1.6f, Colr.Hex(0xF4ECF2, 0.6f));

        var name = p.Keep switch
        {
            Keep.Nothing    => Loc.T("Nothing"),
            Keep.LeaveAlone => p.SlotName,
            _               => p.Name,
        };
        var note = p.Keep switch
        {
            Keep.Nothing    => Loc.F("{0} · taken off", p.SlotName),
            Keep.LeaveAlone => Loc.T("Whatever you wear there stays"),
            _               => p.Missing ? Loc.T("missing") : p.Options.Length > 0 ? p.Options : p.SlotName,
        };
        var nx    = thumb.Right + P(10);
        var nw    = Math.Min(P(118), (pill.X - nx) * 0.5f);
        var dots  = p.Keep == Keep.Saved ? Math.Min(p.Colours.Count, 5) : 0;
        var dw    = dots > 0 ? dots * P(13) + P(4) : 0;
        Painter.TextIn(_c, new Rect(nx, line.Y, nw, line.H), name, open ? CaptionBold : Theme.Caption, p.Keep == Keep.Saved ? Theme.Text : Theme.Muted);
        var ox = nx + nw + P(8);
        Painter.TextIn(_c, new Rect(ox, line.Y, pill.X - P(8) - dw - ox, line.H), note, Theme.Small, p.Missing ? Theme.Gold : Theme.Muted);
        for (var i = 0; i < dots; ++i)
            Dot(new Vector2(pill.X - P(8) - dw + P(4) + i * P(13) + P(5), line.Center.Y), P(5), p.Colours[i]);
        if (p.Changed)
            _c.FillCircle(new Vector2(thumb.Right - P(1), thumb.Y + P(1)), P(3.5f), Theme.Gold);
        _ui.Tip(id, line, p.Changed ? Loc.F("{0} · changed", name) : p.Keep == Keep.Saved ? Loc.T("Click for its colours and options") : name);
        if (resp.Clicked)
            _sheetOpenSlot = open ? null : p.Slot;
        if (open)
            PieceOpen(new Rect(line.X + P(8), line.Bottom, line.W - P(16), extraH), p);
        return y + rowH + extraH + P(3);
    }

    /// <summary> The opened row: the piece's colour bar (as the options panel has it) and its options. </summary>
    private float PieceOpenHeight(float w, SheetPiece p)
    {
        var lh  = _c.LineHeight(Theme.Label);
        var h   = P(8) + lh + P(6) + P(20) + P(12);
        var opt = PieceOptions(p);
        if (opt.Count > 0)
            foreach (var o in opt)
                h += lh + P(6) + (o.Choices.Count + (o.Fits ? 1 : 0)) * P(30) + P(16);
        else
            h += lh + P(6) + P(18) + P(10);
        return h;
    }

    /// <summary> The options of the piece as it is on now (when it is: options can be set only on what is worn). </summary>
    private IReadOnlyList<OptionChip> PieceOptions(SheetPiece p)
        => p.Part is { } part && _s.WearingAll.FirstOrDefault(w => w.Key == part) is { } worn ? worn.Options.ToList() : Array.Empty<OptionChip>();

    private void PieceOpen(Rect r, SheetPiece p)
    {
        var lh = _c.LineHeight(Theme.Label);
        var y  = r.Y + P(8);
        Painter.TextIn(_c, new Rect(r.X, y, r.W, lh), Loc.T("Colours"), Theme.Label, Theme.Muted);
        y += lh + P(6);
        var bar = new Rect(r.X, y, r.W, P(20));
        if (p.Part is { } part && _s.Colours?.Pieces.FirstOrDefault(c => c.Key == part) is { } live)
            LiveBar(live, bar, P(10), Theme.Card, wholeButton: true);
        else
        {
            for (var i = 0; i < p.Colours.Count; ++i)
                Dot(new Vector2(bar.X + P(8) + i * P(18), bar.Center.Y), P(7), p.Colours[i]);
            if (p.Colours.Count == 0)
                Painter.TextIn(_c, bar, Loc.T("No colours to change"), Theme.Small, Theme.Muted);
        }

        y += P(20) + P(12);
        var opt = PieceOptions(p);
        if (opt.Count > 0 && p.Part is { } key)
        {
            foreach (var o in opt)
                y = OptionBlock(r.X + P(6), y, r.W - P(6), key, o, true);
        }
        else
        {
            Painter.TextIn(_c, new Rect(r.X, y, r.W, lh), Loc.T("Options"), Theme.Label, Theme.Muted);
            y += lh + P(6);
            Painter.TextIn(_c, new Rect(r.X, y, r.W, P(18)), p.Options.Length > 0 ? p.Options : Loc.T("Nothing to choose"), Theme.Small, Theme.Muted);
        }
    }

    /// <summary> One colour of yours: its swatch (the picker on a click), its name, Saved | Leave as is. </summary>
    private void ColourCell(Rect cell, ILookSheet api, LookSheet sheet, SheetColour c)
    {
        var id = "sheet-colour/" + c.Key;
        Stats.Places[id] = cell;
        var sw = new Rect(cell.X, cell.Center.Y - P(9), P(18), P(18));
        Stats.Places[id + "/swatch"] = sw;
        var sresp = _ui.Interact(id + "/swatch", sw);
        Dot(sw.Center, P(8), c.Rgb);
        if (sresp.Hovered || _ui.IsOpen(PopPicker) && _ui.PopupContext is PickTarget { Bar: { } bar } && bar == "sheet:" + c.Key)
            _c.StrokeCircle(sw.Center, P(10), Theme.Accent, P(1.5f));
        if (c.Changed && c.Keep == Keep.Saved)
            _c.FillCircle(new Vector2(sw.Right, sw.Y), P(3), Theme.Gold);
        _ui.Tip(id + "/swatch", sw, c.Changed && c.Keep == Keep.Saved ? Loc.F("{0} · not what you wear now", c.Name) : Loc.F("{0} · click to change it", c.Name));
        if (sresp.Clicked)
        {
            FinishColourDrag();
            (_pickH, _pickS, _pickV) = ColourMath.ToHsv(c.Rgb);
            _pickDrag = 0;
            _pickSent = c.Rgb;
            _ui.OpenPopup(PopPicker, new Vector2(sw.Center.X, sw.Bottom), new PickTarget("sheet:" + c.Key, SheetColourArea, ColourRole.Own, sw));
        }

        var saved = Loc.T("Saved");
        var leave = Loc.T("Leave as is");
        var pw    = TwoWayWidth(saved, leave, 5);
        var pill  = new Rect(cell.Right - pw, cell.Center.Y - P(11), pw, P(22));
        Painter.TextIn(_c, new Rect(sw.Right + P(6), cell.Y, pill.X - P(4) - sw.Right - P(6), cell.H), c.Name, Theme.Caption, c.Keep == Keep.Saved ? Theme.Text : Theme.Muted);
        var pick = TwoWay(pill, id + "/keep", saved, leave, c.Keep == Keep.Saved ? 0 : 1, 5);
        if (pick == 0)
            api.SetColour(sheet.Id, c.Key, Keep.Saved);
        else if (pick == 1)
            api.SetColour(sheet.Id, c.Key, Keep.LeaveAlone);
    }

    /// <summary> The picker's area number for one of the look's colours of yours (its key goes in the target's bar as "sheet:key"). </summary>
    private const int SheetColourArea = -2;

    /// <summary> One thing the game shows or hides: its two states, and Saved | Leave as is. </summary>
    private void ShowingRow(Rect r, ILookSheet api, LookSheet sheet, SheetShowing s)
    {
        var id = "sheet-showing/" + s.Key;
        Stats.Places[id] = r;
        var saved = Loc.T("Saved");
        var leave = Loc.T("Leave as is");
        var kw    = TwoWayWidth(saved, leave);
        var keep  = new Rect(r.Right - kw, r.Center.Y - P(11), kw, P(22));
        var vw    = TwoWayWidth(s.OnWord, s.OffWord);
        var value = new Rect(keep.X - P(10) - vw, keep.Y, vw, P(22));
        Painter.Icon(_c, s.Key switch { "weapon" => Icons.MainHand, "hat" => Icons.Head, _ => Icons.Eye }, new Vector2(r.X + P(8), r.Center.Y), P(14), Theme.Muted, 1.7f);
        Painter.TextIn(_c, new Rect(r.X + P(24), r.Y, value.X - P(8) - r.X - P(24), r.H), s.Name, Theme.Caption, s.Keep == Keep.Saved ? Theme.Text : Theme.Muted);
        var v = TwoWay(value, id + "/value", s.OnWord, s.OffWord, s.On ? 0 : 1);
        if (v >= 0)
            api.SetShowing(sheet.Id, s.Key, Keep.Saved, v == 0);
        var k = TwoWay(keep, id + "/keep", saved, leave, s.Keep == Keep.Saved ? 0 : 1);
        if (k == 0)
            api.SetShowing(sheet.Id, s.Key, Keep.Saved, s.On);
        else if (k == 1)
            api.SetShowing(sheet.Id, s.Key, Keep.LeaveAlone, s.On);
    }

    private static string BindingSummary(ILookSheet api, SheetBinding b)
    {
        var parts = new List<string>();
        if (b.Gearset is { } g)
            parts.Add(Loc.F("Gearset: {0}", api.Gearsets.FirstOrDefault(c => c.Id == g)?.Name ?? g));
        if (b.Job is { } j)
            parts.Add(Loc.F("Job: {0}", api.Jobs.FirstOrDefault(c => c.Id == j)?.Name ?? j));
        if (b.GlamourerDesign)
            parts.Add(Loc.T("Glamourer design"));
        return parts.Count > 0 ? string.Join(" · ", parts) : Loc.T("Only when you put it on");
    }

    /// <summary> The three ways a look is worn by itself: a gearset, a job, a Glamourer design; each a card with a switch. </summary>
    private float BindingCards(float x, float y, float w, ILookSheet api, LookSheet sheet, bool draw)
    {
        var b    = sheet.Binding;
        var lh   = _c.LineHeight(Theme.Small);
        var rule = Painter.Wrap(_c, Loc.T("A gearset wins over a job when both fit. One look per gearset; the last one you bind takes it over."), w - P(20), Theme.Small, 3);

        // the gearset
        var card = new Rect(x - P(4), y + P(2), w + P(8), P(10) + P(28) + P(6) + P(32) + P(10));
        if (draw)
        {
            _c.FillRect(card, Theme.Card, P(10));
            var on = b.Gearset is not null;
            if (Switch(new Rect(card.X + P(10), card.Y + P(14), P(36), P(20)), "sheet-gearset-switch", on, true))
            {
                if (on)
                    api.Bind(sheet.Id, null, b.Job);
                else
                    _ui.OpenPopup(PopGearsets, new Vector2(card.X + P(10), card.Y + P(10) + P(28) + P(6) + P(32) + P(4)), sheet.Id);
            }

            Painter.TextIn(_c, new Rect(card.X + P(56), card.Y + P(10), card.W - P(66), P(28)), Loc.T("When I switch to gearset"), CaptionBold, Theme.Text);
            var name = b.Gearset is { } g ? api.Gearsets.FirstOrDefault(c => c.Id == g)?.Name ?? g : Loc.T("Not bound to a gearset");
            DropButton(new Rect(card.X + P(10), card.Y + P(10) + P(28) + P(6), card.W - P(20), P(32)), "sheet-gearset", name, PopGearsets, sheet.Id);
        }

        y = card.Bottom + P(6);

        // the job, and the rule
        var jobCard = new Rect(x - P(4), y, w + P(8), P(10) + P(32) + P(6) + rule.Count * lh + P(10));
        if (draw)
        {
            _c.FillRect(jobCard, Theme.Card, P(10));
            var on = b.Job is not null;
            if (Switch(new Rect(jobCard.X + P(10), jobCard.Y + P(16), P(36), P(20)), "sheet-job-switch", on, true))
            {
                if (on)
                    api.Bind(sheet.Id, b.Gearset, null);
                else
                    _ui.OpenPopup(PopJobs, new Vector2(jobCard.Right - P(10) - P(170), jobCard.Y + P(10) + P(32) + P(4)), sheet.Id);
            }

            var dw = Math.Min(P(170), jobCard.W * 0.45f);
            Painter.TextIn(_c, new Rect(jobCard.X + P(56), jobCard.Y + P(10), jobCard.W - P(76) - dw, P(32)), Loc.T("When I change to job"), CaptionBold, Theme.Text);
            var name = b.Job is { } j ? api.Jobs.FirstOrDefault(c => c.Id == j)?.Name ?? j : Loc.T("Not bound to a job");
            DropButton(new Rect(jobCard.Right - P(10) - dw, jobCard.Y + P(10), dw, P(32)), "sheet-job", name, PopJobs, sheet.Id);
            var ry = jobCard.Y + P(10) + P(32) + P(6);
            foreach (var l in rule)
            {
                _c.Text(new Vector2(jobCard.X + P(10), ry), l, Theme.Small, Theme.Muted);
                ry += lh;
            }
        }

        y = jobCard.Bottom + P(6);

        // the Glamourer design
        // under the switch: why no design can be made (gold), the engine's own note while it is on and works (quiet),
        // or what the design is for
        var design = b.GlamourerDesign && b.DesignName.Length > 0 && b.DesignProblem.Length == 0;
        var said   = b.DesignProblem.Length > 0 ? b.DesignProblem
            : b.GlamourerDesign && b.DesignNote.Length > 0 ? b.DesignNote
            : Loc.T("Kept up to date every time you press Update, so Glamourer's own Automation can wear it too.");
        var note   = Painter.Wrap(_c, said, w - P(20), Theme.Small, 3);
        var dcard  = new Rect(x - P(4), y, w + P(8), P(10) + P(28) + (design ? P(26) : 0) + P(4) + note.Count * lh + P(10));
        if (draw)
        {
            _c.FillRect(dcard, Theme.Card, P(10));
            var usable = b.DesignProblem.Length == 0;
            if (Switch(new Rect(dcard.X + P(10), dcard.Y + P(14), P(36), P(20)), "sheet-design-switch", b.GlamourerDesign, usable))
                api.SetGlamourerDesign(sheet.Id, !b.GlamourerDesign);
            Painter.TextIn(_c, new Rect(dcard.X + P(56), dcard.Y + P(10), dcard.W - P(66), P(28)), Loc.T("Make a Glamourer design of this look"), CaptionBold, usable ? Theme.Text : Theme.Muted);
            var ny = dcard.Y + P(10) + P(28);
            if (design)
            {
                var lw = _c.Measure(Loc.T("Design").ToUpperInvariant(), Theme.Label).X;
                Painter.TextIn(_c, new Rect(dcard.X + P(10), ny, lw, P(22)), Loc.T("Design"), Theme.Label, Theme.Muted);
                Painter.TextIn(_c, new Rect(dcard.X + P(10) + lw + P(10), ny, dcard.W - lw - P(30), P(22)), b.DesignName, Theme.Caption, Theme.Text);
                Stats.Places["sheet-design-name"] = new Rect(dcard.X + P(10), ny, dcard.W - P(20), P(22));
                ny += P(26);
            }

            ny += P(4);
            Stats.Places["sheet-design-note"] = new Rect(dcard.X + P(10), ny, dcard.W - P(20), note.Count * lh);
            foreach (var l in note)
            {
                _c.Text(new Vector2(dcard.X + P(10), ny), l, Theme.Small, b.DesignProblem.Length > 0 ? Theme.Gold : Theme.Muted);
                ny += lh;
            }
        }

        return dcard.Bottom + P(10);
    }

    /// <summary> A small on/off switch (pink and to the right when on). Returns true when clicked. </summary>
    private bool Switch(Rect r, string id, bool on, bool enabled)
    {
        Stats.Places[id] = r;
        var resp = _ui.Interact(id, r.Expand(P(4)), enabled);
        _c.FillRect(r, on ? (enabled ? Theme.Accent : Theme.Accent.Fade(0.4f)) : resp.Hovered && enabled ? Theme.EdgeStrong : Theme.Edge, r.H / 2);
        var knob = new Vector2(on ? r.Right - r.H / 2 : r.X + r.H / 2, r.Center.Y);
        _c.FillCircle(knob, r.H / 2 - P(3), on ? Theme.Window : enabled ? Theme.Muted : Theme.Faint);
        return resp.Clicked && enabled;
    }

    private void DropButton(Rect r, string id, string text, string pop, Guid look)
    {
        Stats.Places[id] = r;
        var resp = _ui.Interact(id, r);
        var open = _ui.IsOpen(pop);
        _c.FillRect(r, open || resp.Hovered ? Theme.Picked : Theme.Chip, P(8));
        _c.StrokeRect(r, open ? Theme.Accent : Theme.EdgeStrong, P(8), Math.Max(1, _u));
        Painter.TextIn(_c, new Rect(r.X + P(10), r.Y, r.W - P(34), r.H), text, Theme.Caption, Theme.Text);
        Painter.Icon(_c, open ? Icons.ChevronD : Icons.ChevronD, new Vector2(r.Right - P(16), r.Center.Y), P(12), Theme.Muted, 2);
        if (!resp.Clicked)
            return;
        if (open)
            _ui.ClosePopup();
        else
            _ui.OpenPopup(pop, new Vector2(r.X, r.Bottom + P(4)), look);
    }

    /// <summary> The gearsets (or jobs) to bind the look to: a tick on its own, what another look already holds; "Not bound" first. </summary>
    private void BindMenu(Guid look, bool gearsets)
    {
        if (SheetApi is not { } api || api.Sheet(look) is not { } sheet)
        {
            _ui.ClosePopup();
            return;
        }

        var b       = sheet.Binding;
        var current = gearsets ? b.Gearset : b.Job;
        var list    = gearsets ? api.Gearsets : api.Jobs;
        var items   = new List<MenuItem> { new("bind:", gearsets ? Loc.T("Not bound to a gearset") : Loc.T("Not bound to a job")) { Checked = current is null } };
        items.AddRange(list.Select(c => new MenuItem("bind:" + c.Id, c.Name)
        {
            Checked = c.Id == current,
            Hint    = c.BoundTo is { Length: > 0 } other && c.Id != current ? Loc.F("bound to {0}", other) : null,
        }));
        var picked = Menu.Draw(_ui, gearsets ? PopGearsets : PopJobs, _ui.PopupAnchor, items, 240);
        if (picked is null)
            return;
        var id = picked.Id[5..];
        var v  = id.Length == 0 ? null : id;
        if (gearsets)
            api.Bind(look, v, b.Job);
        else
            api.Bind(look, b.Gearset, v);
    }
}
