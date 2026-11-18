using System.Numerics;
using ModCloset2.Core;
using ModCloset2.Ui.Canvas;
using ModCloset2.Ui.Widgets;

namespace ModCloset2.Ui.Face;

/// <summary>
/// "Save look": the header's button and its small sheet. A name (the closet's proposal, selected), the body shelves to
/// take along (one tick each, ticked by the closet's default), Save (Enter) and Cancel (Esc). When what is on already is
/// a look the sheet says so; when it started from one and changed, it offers "Update …" beside "Save as new".
/// </summary>
public sealed partial class WindowFace
{
    private string _saveName = string.Empty;
    private bool _saveFocus;
    private readonly Dictionary<string, bool> _saveTicks = new(StringComparer.Ordinal);

    private void SaveLookButton(Rect r)
    {
        Stats.Places["save-look"] = r;
        var now     = _s.Looks?.Now;
        var nothing = now is null || now.Saves == SaveWhat.Nothing;
        var tip     = nothing ? Loc.T("Nothing to save yet") : Loc.T("Save what you wear as a look");
        if (Painter.IconButton(_ui, "save-look", r, Icons.SaveLook, tip, !nothing, _ui.IsOpen(PopSaveLook)).Clicked && now is not null)
        {
            if (_ui.IsOpen(PopSaveLook))
            {
                _ui.ClosePopup();
                return;
            }

            _saveName  = now.ProposedName;
            _saveFocus = true;
            _saveTicks.Clear();
            foreach (var b in now.BodyShelves)
                _saveTicks[b.Shelf] = b.Default;
            _ui.OpenPopup(PopSaveLook, new Vector2(r.Right, r.Bottom + P(6)));
        }
    }

    /// <summary> The body shelves ticked to go along (in the order the closet gives them). </summary>
    private List<string> SaveTicked(WearingNow now)
        => now.BodyShelves.Where(b => _saveTicks.TryGetValue(b.Shelf, out var on) ? on : b.Default).Select(b => b.Shelf).ToList();

    private void SaveLookPanel()
    {
        if (_s.Looks is not { } looks)
        {
            _ui.ClosePopup();
            return;
        }

        var now = looks.Now;
        if (now.Saves == SaveWhat.Nothing)
        {
            // what was on came off while the sheet was open
            _ui.ClosePopup();
            return;
        }

        // exactly a look already: the sheet says so above the form (a save may still differ in what of the body it takes
        // along; the engine decides and says what it did); changed from one: "Update …" beside "Save as new"
        var already = now.Look is not null && !now.CanPin && !now.CanUpdate;
        var w       = Math.Min(P(420), _c.Bounds.W - P(16));
        var note    = already ? Painter.Wrap(_c, Loc.F("This is already “{0}”.", now.Name), w - P(40), Theme.Caption, 2) : new List<string>();
        var inner   = w - P(40);
        var rowH    = P(30);
        var ticks   = now.BodyShelves.Count;
        var lh      = _c.LineHeight(Theme.Caption);
        var h = P(20) + P(24) + P(12) + (note.Count > 0 ? note.Count * lh + P(10) : 0)
              + P(36) + P(14) + (ticks > 0 ? P(14) + P(8) + ticks * rowH + P(10) : 0)
              + P(36) + P(20);
        h = Math.Min(h, _c.Bounds.H - P(16));
        var a   = _ui.PopupAnchor;
        var box = new Rect(Math.Clamp(a.X - w, _c.Bounds.X + P(8), _c.Bounds.Right - P(8) - w), Math.Clamp(a.Y, _c.Bounds.Y + P(8), Math.Max(_c.Bounds.Y + P(8), _c.Bounds.Bottom - P(8) - h)), w, h);
        _ui.PopupArea(box);
        Stats.Places["save-sheet"] = box;
        Painter.Shadow(_c, box, P(12), _u, 18, 8, 0.55f);
        _c.FillRect(box, Theme.Card, P(12));
        _c.StrokeRect(box, Theme.EdgeStrong, P(12), Math.Max(1, _u));
        _ui.PushClip(box);

        var x = box.X + P(20);
        var y = box.Y + P(20);
        Painter.TextIn(_c, new Rect(x, y, inner, P(24)), Loc.T("Save as a look"), Theme.Strong, Theme.Text);
        y += P(24) + P(12);

        var esc = _ui.In.KeyPressed(Key.Escape);
        foreach (var line in note)
        {
            Painter.TextIn(_c, new Rect(x, y, inner, lh), line, Theme.Caption, Theme.Gold);
            y += lh;
        }

        if (note.Count > 0)
        {
            Stats.Places["save-already"] = new Rect(x, y - note.Count * lh, inner, note.Count * lh);
            y += P(10);
        }

        // the name: the closet's proposal, all of it selected, so typing replaces it
        var fbox = new Rect(x, y, inner, P(36));
        Stats.Places["save-name"] = fbox;
        _c.FillRect(fbox, Theme.Rail, P(9));
        var name    = _saveName;
        var changed = _c.TextField("save-name", fbox.Inset(P(12), 0), ref name, Loc.T("Name of the look"), Theme.Body, Theme.Text, Theme.Muted, _saveFocus, true, out var active, selectAll: true);
        _saveFocus = false;
        if (active)
            _c.StrokeRect(fbox, Theme.Accent.Fade(0.55f), P(9), Math.Max(1, _u));
        if (changed)
            _saveName = name;
        var typing = active || _ui.In.TextFocused;
        y += P(36) + P(14);

        // what of the body goes along: one tick per body shelf with something on
        if (ticks > 0)
        {
            Painter.TextIn(_c, new Rect(x, y, inner, P(14)), Loc.T("Take along"), Theme.Label, Theme.Muted);
            y += P(14) + P(8);
            var ticked = SaveTicked(now);
            foreach (var b in now.BodyShelves)
            {
                var row  = new Rect(x - P(6), y, inner + P(12), rowH - P(2));
                var id   = "save-take/" + b.Shelf;
                Stats.Places[id] = row;
                var on   = ticked.Contains(b.Shelf);
                // only body things on: at least one of them stays ticked
                var last = now.Saves == SaveWhat.BodyOnly && on && ticked.Count == 1;
                var resp = _ui.Interact(id, row, !last);
                if (resp.Hovered && !last)
                    _c.FillRect(row, Theme.HoverWash, P(7));
                var check = new Rect(row.X + P(6), row.Center.Y - P(9), P(18), P(18));
                _c.FillRect(check, on ? Theme.Accent : Theme.Rail, P(5));
                if (!on)
                    _c.StrokeRect(check, Theme.EdgeStrong, P(5), Math.Max(1, _u));
                else
                    Painter.Icon(_c, Icons.Check, check.Center, P(13), Theme.OnAccent, 2.4f);
                var tx = check.Right + P(10);
                var nw = _c.Measure(b.Name, Theme.Caption).X;
                Painter.TextIn(_c, new Rect(tx, row.Y, Math.Min(nw, row.Right - tx), row.H), b.Name, Theme.Caption, Theme.Text);
                if (b.Worn.Length > 0 && tx + nw + P(8) < row.Right - P(24))
                    Painter.TextIn(_c, new Rect(tx + nw + P(8), row.Y, row.Right - P(6) - (tx + nw + P(8)), row.H), Loc.F("· {0}", b.Worn), Theme.Caption, Theme.Muted);
                if (last)
                    _ui.Tip(id, row, Loc.T("Something has to be saved"));
                if (resp.Clicked && !last)
                    _saveTicks[b.Shelf] = !on;
                y += rowH;
            }
        }

        // the buttons: Save (Enter), or "Save as new" beside "Update …" when it started from a look; Cancel (Esc)
        var by      = box.Bottom - P(20) - P(36);
        var update  = now.Look is { } look && now.CanUpdate ? look : (Guid?)null;
        var save    = update is null ? Loc.T("Save") : Loc.T("Save as new");
        var saveW   = Painter.ButtonWidth(_c, save, Theme.BodyBold, P(18));
        var saveBox = new Rect(box.Right - P(20) - saveW, by, saveW, P(36));
        Stats.Places["save-save"] = saveBox;
        var ok      = _saveName.Trim().Length > 0 && (now.Saves != SaveWhat.BodyOnly || SaveTicked(now).Count > 0);
        var enter   = typing && _ui.In.KeyPressed(Key.Enter);
        var xLeft   = saveBox.X - P(8);
        if ((Painter.Button(_ui, "save-save", saveBox, save, ButtonKind.Primary, ok).Clicked || enter) && ok)
        {
            looks.SaveNow(_saveName.Trim(), SaveTicked(now));
            _ui.ClosePopup();
            _ui.PopClip();
            return;
        }

        if (update is { } u)
        {
            var upd    = Loc.F("Update {0}", now.Name);
            var full   = Painter.ButtonWidth(_c, upd, Theme.Body, P(14));
            var cancelNeed = Painter.ButtonWidth(_c, Loc.T("Cancel"), Theme.Body, P(14)) + P(8);
            var updW   = Math.Min(full, xLeft - box.X - P(20) - cancelNeed);
            var updBox = new Rect(xLeft - updW, by, updW, P(36));
            Stats.Places["save-update"] = updBox;
            var uresp  = Painter.Button(_ui, "save-update", updBox, upd, ButtonKind.Outline);
            if (updW < full)
                _ui.Tip("save-update", updBox, upd);
            if (uresp.Clicked)
            {
                looks.UpdateFromNow(u);
                _ui.ClosePopup();
                _ui.PopClip();
                return;
            }

            xLeft = updBox.X - P(8);
        }

        var cancel  = Loc.T("Cancel");
        var cancelW = Painter.ButtonWidth(_c, cancel, Theme.Body, P(14));
        var canBox  = new Rect(xLeft - cancelW, by, cancelW, P(36));
        Stats.Places["save-cancel"] = canBox;
        if (Painter.Button(_ui, "save-cancel", canBox, cancel, ButtonKind.Quiet).Clicked || esc && typing)
            _ui.ClosePopup();
        _ui.PopClip();
    }
}
