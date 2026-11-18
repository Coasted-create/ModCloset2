using System.Numerics;
using ModCloset2.Core;
using ModCloset2.Ui.Canvas;
using ModCloset2.Ui.Widgets;

namespace ModCloset2.Ui.Face;

public sealed partial class WindowFace
{
    /// <summary> What the "Why is it here?" popup shows (asked once when it opens). </summary>
    private sealed record WhyContext(Card Card, IReadOnlyList<Evidence> Evidence);

    private void DrawPopups()
    {
        if (_ui.PopupId.Length == 0)
            return;
        _ui.BeginOverlay();
        try
        {
            switch (_ui.PopupId)
            {
                case PopCard when _ui.PopupContext is Card card:
                    CardMenu(card);
                    break;
                case PopOption when _ui.PopupContext is ValueTuple<WearingInfo, OptionChip> ctx:
                    OptionMenu(ctx.Item1, ctx.Item2);
                    break;
                case PopOwnMod when _ui.PopupContext is PartKey ownOf:
                    OwnModPanel(ownOf);
                    break;
                case PopSaveLook:
                    SaveLookPanel();
                    break;
                case PopSettings:
                    SettingsMenu();
                    break;
                case PopWhy when _ui.PopupContext is WhyContext why:
                    WhyPanel(why);
                    break;
                case PopNewShelf:
                    NewShelfPanel();
                    break;
                case PopOther when _ui.PopupContext is SortItem item:
                    OtherPicked(item, Menu.Draw(_ui, PopOther, _ui.PopupAnchor, OtherMenu(item), 200));
                    break;
                case PopSlotChips:
                    SlotChipsMenu();
                    break;
                case PopKinds:
                    KindsMenu();
                    break;
                case PopPicker when _ui.PopupContext is PickTarget pick && _s.Colours is { } oc:
                    Picker(oc, pick);
                    break;
                case PopRowState when _ui.PopupContext is string bar && _s.Colours is { } oc2:
                    RowStateMenu(oc2, bar);
                    break;
                case PopSaved when _ui.PopupContext is int saved && _s.Colours is { } oc3:
                    SavedMenu(oc3, saved);
                    break;
                case PopLook when _ui.PopupContext is LookCard look && _s.Looks is { } looks:
                    LookMenu(looks, look);
                    break;
                case PopReplace when _ui.PopupContext is LookCard gone && _s.Looks is { } looks2:
                    ReplacePanel(looks2, gone);
                    break;
                default:
                    _ui.ClosePopup();
                    break;
            }
        }
        finally
        {
            _ui.EndOverlay();
        }
    }

    // ---- the card's right-click menu ----

    private List<MenuItem> CardMenuItems(Card card)
    {
        var items = new List<MenuItem>();
        if (card.Key.Socket != Socket.None)
        {
            // a thing its mod offers through several slots: which one to wear it on
            var slots = _s.SlotsOf(card.Key);
            if (slots.Count > 0)
                items.Add(new MenuItem("wear", Loc.T("Wear on")) { Sub = SlotItems(slots), Icon = Icons.Slots });

            var targets = _s.MoveTargets.Select(t => new MenuItem("move:" + t.Id, Loc.T(t.Name))
            {
                Checked = t.Id == card.Piece,
                Enabled = t.Id != card.Piece,
            }).ToList();
            targets.Add(MenuItem.Separator());
            targets.Add(new MenuItem("newshelf", Loc.T("New shelf…")));
            items.Add(new MenuItem("move", Loc.T("Move to")) { Sub = targets, Icon = Icons.Move });
            if (card.OwnMod)
                items.Add(new MenuItem("ownmod", Loc.T("Make it its own mod…")) { Icon = Icons.Copy });
            if (card.OneHand != Hands.Both)
                items.Add(new MenuItem("ownhand", card.OneHand == Hands.Right ? Loc.T("Wear through the left ring slot (as a copy)…") : Loc.T("Wear through the right ring slot (as a copy)…")) { Icon = Icons.Copy });

            if (WornWithKey(card.Key) is { } w && w.Options.Count > 0)
            {
                var opts = w.Options.Select((o, i) => new MenuItem("opt:" + i, $"{o.Group}: {(o.Multi ? string.Join(", ", o.On) : o.On.FirstOrDefault() ?? string.Empty)}")).ToList();
                items.Add(new MenuItem("options", Loc.T("Options")) { Sub = opts, Icon = Icons.Settings });
            }

            items.Add(new MenuItem("why", Loc.T("Why is it here?")) { Icon = Icons.Question });
            if (card.Moved)
                items.Add(new MenuItem("unmove", Loc.T("Put back where the closet had it")) { Icon = Icons.Undo });
            items.Add(card.Piece == Pieces.Hidden || _s.Selected == Pieces.Hidden && !Searching
                ? new MenuItem("unhide", Loc.T("Show again")) { Icon = Icons.Eye }
                : new MenuItem("hide", Loc.T("Hide")) { Icon = Icons.Hidden });
            items.Add(MenuItem.Separator());
        }

        items.Add(new MenuItem("penumbra", Loc.T("Open in Penumbra")) { Icon = Icons.OpenOutside });
        return items;
    }

    private void CardMenu(Card card)
    {
        var anchor = _ui.PopupAnchor;
        var picked = Menu.Draw(_ui, PopCard, anchor, CardMenuItems(card), 200);
        if (picked is null)
            return;

        var id = picked.Id;
        if (WearOnPicked(card.Key, picked))
            return;
        if (id.StartsWith("move:", StringComparison.Ordinal))
            _s.Move(card.Key, id[5..]);
        else if (id.StartsWith("opt:", StringComparison.Ordinal) && WornWithKey(card.Key) is { } w && int.TryParse(id[4..], out var oi) && oi < w.Options.Count)
            _ui.OpenPopup(PopOption, anchor, (w, w.Options[oi]));
        else
            switch (id)
            {
                case "newshelf":
                    OpenNewShelf(card.Key, anchor);
                    break;
                case "ownmod":
                    OpenOwnMod(card.Key);
                    break;
                case "ownhand":
                    OpenOwnMod(card.Key, card.OneHand == Hands.Right ? Hands.Left : Hands.Right);
                    break;
                case "why":
                    _ui.OpenPopup(PopWhy, anchor, new WhyContext(card, _s.WhyHere(card.Key)));
                    break;
                case "unmove":
                    _s.PutBackWhereDetected(card.Key);
                    break;
                case "hide":
                    _s.Hide(card.Key);
                    break;
                case "unhide":
                    _s.Unhide(card.Key);
                    break;
                case "penumbra":
                    _s.OpenInPenumbra(card.Key);
                    break;
            }
    }

    private void OtherPicked(SortItem item, MenuItem? picked)
    {
        if (picked is null)
            return;
        if (picked.Id == "newshelf")
            OpenNewShelf(item.Key, _ui.PopupAnchor);
        else if (picked.Id.StartsWith("move:", StringComparison.Ordinal))
            _s.Move(item.Key, picked.Id[5..]);
    }

    // ---- options (the card menu's Options ▸ group; the panel lays them out openly) ----

    /// <summary>
    /// A group's choices as menu rows; a group the closet fits to the body starts with "Automatic" (ticked while nobody
    /// chose by hand; its choices are then not ticked).
    /// </summary>
    private static List<MenuItem> ChoiceItems(OptionChip option, string prefix)
    {
        var items = new List<MenuItem>();
        if (option.Fits)
            items.Add(new MenuItem(prefix + "auto", Loc.T("Automatic")) { Checked = option.Automatic });
        items.AddRange(option.Choices.Select((c, i) => new MenuItem(prefix + i, c) { Checked = !option.Automatic && option.On.Contains(c), KeepOpen = option.Multi }));
        return items;
    }

    /// <summary>
    /// The group as the session has it now (its newest <see cref="OptionChip.On"/>), else the one given. Ticks, counts
    /// and every click are built from this, never from a list kept since a menu or the panel opened: a written choice
    /// shows in the session's lists the frame after, so two quick clicks each start from the one before.
    /// </summary>
    private OptionChip CurrentOption(PartKey key, OptionChip option)
        => _s.WearingAll.FirstOrDefault(x => x.Key == key)?.Options.FirstOrDefault(o => o.Group == option.Group) ?? option;

    private void SetChoice(PartKey key, OptionChip option, int index)
    {
        option = CurrentOption(key, option);
        if (index < 0 || index >= option.Choices.Count)
            return;
        var choice = option.Choices[index];
        IReadOnlyList<string> on = option.Multi
            ? option.On.Contains(choice) ? option.On.Where(c => c != choice).ToList() : option.On.Append(choice).ToList()
            : new[] { choice };
        _s.SetOption(key, option.Group, on);
    }

    private void OptionMenu(WearingInfo w, OptionChip option)
    {
        option = CurrentOption(w.Key, option);
        var items = new List<MenuItem> { MenuItem.Header(option.Group) };
        items.AddRange(ChoiceItems(option, "choice:"));
        var picked = Menu.Draw(_ui, PopOption, _ui.PopupAnchor, items, 160);
        if (picked?.Id == "choice:auto")
            _s.SetOption(w.Key, option.Group, null);
        else if (picked is not null && int.TryParse(picked.Id.AsSpan(7), out var i))
            SetChoice(w.Key, option, i);
    }

    // ---- the slots a thing can be worn through ----

    /// <summary> One row per slot: its name, a tick on the one in use, a quiet ring on the one a click would take, what it would take off. </summary>
    public static List<MenuItem> SlotItems(IReadOnlyList<SlotChoice> slots)
        => slots.Select(c => new MenuItem("slot:" + (int)c.Slot, c.Name)
        {
            Checked = c.On,
            Ring    = c.Next && !c.On,
            Hint    = c.Replaces.Length > 0 ? Loc.F("takes off {0}", c.Replaces) : null,
        }).ToList();

    /// <summary> A slot row was clicked: wear the thing there. </summary>
    private bool WearOnPicked(PartKey key, MenuItem? picked)
    {
        if (picked is null || !picked.Id.StartsWith("slot:", StringComparison.Ordinal) || !int.TryParse(picked.Id.AsSpan(5), out var slot))
            return false;
        _s.WearOn(key, (Socket)slot);
        return true;
    }

    private void KindsMenu()
    {
        var items  = _s.Kinds.Select(k => new MenuItem("kind:" + k.Id, k.Id.Length == 0 ? Loc.T("All kinds") : KindLabel(k)) { Checked = k.Id == _s.Kind }).ToList();
        var picked = Menu.Draw(_ui, PopKinds, _ui.PopupAnchor, items, 180);
        if (picked is not null)
            _s.Kind = picked.Id[5..];
    }

    // ---- settings ----

    /// <summary> "1,240 of 3,600": pieces with a photo of all that could have one. </summary>
    public static string PhotosLine(PhotoStatus photos)
        => Loc.F("{0} of {1}", Num(photos.Taken), Num(photos.Total));

    private void SettingsMenu()
    {
        var items = new List<MenuItem>
        {
            MenuItem.Header(Loc.T("Language")),
            new("lang:", Loc.T("As the game")) { Checked = _s.Language.Length == 0 },
        };
        foreach (var (code, name) in Loc.Languages)
            items.Add(new MenuItem("lang:" + code, name) { Checked = _s.Language == code });
        items.Add(MenuItem.Separator());
        items.Add(MenuItem.Header(Loc.T("On you key")));
        var key = _s.Remembered(KeyOnYou);
        foreach (var (value, label) in OnYouKeys())
            items.Add(new MenuItem("key:" + value, label) { Checked = OnYouKeyIn(key) == value });
        items.Add(MenuItem.Note(Loc.T("Hold it and point at your character. Never active in combat or a duty.")));
        items.Add(MenuItem.Separator());

        // photos of the pieces: the switch, and how far the closet is as a quiet line beside it
        if (_s.Photos is { } photos)
        {
            items.Add(new MenuItem("photos", Loc.T("Photos of your pieces"))
            {
                Checked  = photos.On,
                Hint     = PhotosLine(photos),
                KeepOpen = true,
            });
            if (photos.Working && photos.On)
                items.Add(MenuItem.Note(Loc.T("Taking photos…")));
            items.Add(MenuItem.Separator());
        }

        items.Add(new MenuItem("rescan", Loc.T("Read my mods again")));
        items.Add(new MenuItem("census", Loc.T("Write the census file")));

        // opened under the settings button, right edges lined up
        var w      = Menu.Width(_c, _u, items, 220);
        var anchor = _ui.PopupAnchor;
        var picked = Menu.Draw(_ui, PopSettings, new Vector2(anchor.X - w, anchor.Y), items, 220);
        if (picked is null)
            return;
        if (picked.Id.StartsWith("lang:", StringComparison.Ordinal))
            _s.Language = picked.Id[5..];
        else if (picked.Id.StartsWith("key:", StringComparison.Ordinal))
            _s.Remember(KeyOnYou, picked.Id[4..]);
        else if (picked.Id == "photos" && _s.Photos is { } now)
            _s.SetPhotos(!now.On);
        else if (picked.Id == "rescan")
            _s.Rescan();
        else if (picked.Id == "census")
            _s.WriteCensus();
    }

    // ---- "Why is it here?" ----

    private static string WitnessName(EvidenceKind kind)
        => kind switch
        {
            EvidenceKind.Slot     => Loc.T("Slot"),
            EvidenceKind.Body     => Loc.T("Sits"),
            EvidenceKind.Words    => Loc.T("Name"),
            EvidenceKind.Material => Loc.T("Look"),
            EvidenceKind.Folder   => Loc.T("Folder"),
            EvidenceKind.Learned  => Loc.T("You moved one like it"),
            _                     => kind.ToString(),
        };

    private void WhyPanel(WhyContext why)
    {
        var u     = _u;
        var w     = Math.Min(P(380), _c.Bounds.W - P(16));
        var rowH  = P(30);
        var list  = why.Evidence;
        var title = Loc.F("Why it is on {0}", ShelfName(why.Card.Piece));
        var h     = P(16) + P(24) + P(4) + P(20) + P(10) + Math.Max(1, list.Count) * rowH + P(12);
        var a     = _ui.PopupAnchor;
        var box   = new Rect(Math.Clamp(a.X, _c.Bounds.X + P(8), _c.Bounds.Right - P(8) - w), Math.Clamp(a.Y, _c.Bounds.Y + P(8), Math.Max(_c.Bounds.Y + P(8), _c.Bounds.Bottom - P(8) - h)), w, h);
        _ui.PopupArea(box);
        Stats.Places["why"] = box;
        Painter.Shadow(_c, box, P(12), u, 18, 8, 0.55f);
        _c.FillRect(box, Theme.Card, P(12));
        _c.StrokeRect(box, Theme.EdgeStrong, P(12), Math.Max(1, u));

        var x  = box.X + P(16);
        var y  = box.Y + P(16);
        var cw = box.W - P(32);
        var close = new Rect(box.Right - P(12) - P(24), box.Y + P(12), P(24), P(24));
        var cr    = _ui.Interact("why-close", close);
        _ui.Tip("why-close", close, Loc.T("Close"));
        if (cr.Hovered)
            _c.FillRect(close, Theme.Picked, P(6));
        Painter.Icon(_c, Icons.Close, close.Center, P(14), cr.Hovered ? Theme.Text : Theme.Muted, 2);
        if (cr.Clicked)
            _ui.ClosePopup();

        Painter.TextIn(_c, new Rect(x, y, cw - P(28), P(24)), title, Theme.Strong, Theme.Text);
        y += P(24) + P(4);
        Painter.TextIn(_c, new Rect(x, y, cw, P(20)), why.Card.Name, Theme.Caption, Theme.Muted);
        y += P(20) + P(10);
        if (list.Count == 0)
        {
            Painter.TextIn(_c, new Rect(x, y, cw, rowH), Loc.T("The closet has not looked inside this mod yet."), Theme.Caption, Theme.Muted);
            return;
        }

        var labelW = list.Max(e => _c.Measure(WitnessName(e.Kind), Theme.Small).X) + P(16);
        foreach (var e in list)
        {
            var chip = new Rect(x, y + P(3), Math.Min(labelW, P(150)), P(24));
            _c.FillRect(chip, Theme.Rail, P(6));
            Painter.TextIn(_c, chip.Inset(P(8), 0), WitnessName(e.Kind), Theme.Small, Theme.Muted);
            var target = e.Piece.Length > 0 ? ShelfName(e.Piece) : string.Empty;
            var tw     = target.Length > 0 ? _c.Measure(target, Theme.Caption).X + P(20) : 0;
            var vx     = chip.Right + P(10);
            Painter.TextIn(_c, new Rect(vx, y, box.Right - P(16) - tw - vx - P(8), rowH), e.Value, Theme.Caption, Theme.Text);
            if (tw > 0)
            {
                var agrees = e.Piece == why.Card.Piece;
                var tx     = box.Right - P(16) - tw;
                Painter.Icon(_c, Icons.Move, new Vector2(tx + P(6), y + rowH / 2), P(12), agrees ? Theme.Accent : Theme.Muted, 2);
                Painter.TextIn(_c, new Rect(tx + P(16), y, tw - P(16), rowH), target, Theme.Caption, agrees ? Theme.Accent : Theme.Muted);
            }

            y += rowH;
        }
    }

    // ---- a new shelf ----

    private void OpenNewShelf(PartKey? part, Vector2 at)
    {
        _newShelfName  = string.Empty;
        _newShelfFor   = part;
        _focusNewShelf = true;
        _ui.OpenPopup(PopNewShelf, at);
    }

    private void NewShelfPanel()
    {
        var w   = Math.Min(P(320), _c.Bounds.W - P(16));
        var h   = P(16) + P(22) + P(10) + P(36) + P(14) + P(36) + P(16);
        var a   = _ui.PopupAnchor;
        var box = new Rect(Math.Clamp(a.X, _c.Bounds.X + P(8), _c.Bounds.Right - P(8) - w), Math.Clamp(a.Y, _c.Bounds.Y + P(8), Math.Max(_c.Bounds.Y + P(8), _c.Bounds.Bottom - P(8) - h)), w, h);
        _ui.PopupArea(box);
        Stats.Places["new-shelf"] = box;
        Painter.Shadow(_c, box, P(12), _u, 18, 8, 0.55f);
        _c.FillRect(box, Theme.Card, P(12));
        _c.StrokeRect(box, Theme.EdgeStrong, P(12), Math.Max(1, _u));

        var x = box.X + P(16);
        var y = box.Y + P(16);
        Painter.TextIn(_c, new Rect(x, y, box.W - P(32), P(22)), Loc.T("New shelf"), Theme.Strong, Theme.Text);
        y += P(22) + P(10);
        var field = new Rect(x, y, box.W - P(32), P(36));
        _c.FillRect(field, Theme.Rail, P(9));
        var name    = _newShelfName;
        var changed = _c.TextField("new-shelf", field.Inset(P(12), 0), ref name, Loc.T("Wings, horns, glasses…"), Theme.Body, Theme.Text, Theme.Muted, _focusNewShelf, true, out var active);
        _focusNewShelf = false;
        if (active)
            _c.StrokeRect(field, Theme.Accent.Fade(0.55f), P(9), Math.Max(1, _u));
        if (changed)
            _newShelfName = name;
        y += P(36) + P(14);

        var make    = Loc.T("Make the shelf");
        var cancel  = Loc.T("Cancel");
        var makeW   = Painter.ButtonWidth(_c, make, Theme.BodyBold, P(18));
        var cancelW = Painter.ButtonWidth(_c, cancel, Theme.Body, P(14));
        var makeBox = new Rect(box.Right - P(16) - makeW, y, makeW, P(36));
        var ok      = _newShelfName.Trim().Length > 0;
        // ImGui lets go of a field on Enter in the same frame, so "had the keyboard when the frame began" counts too
        var enter   = (active || _ui.In.TextFocused) && _ui.In.KeyPressed(Key.Enter);
        if ((Painter.Button(_ui, "new-shelf-make", makeBox, make, ButtonKind.Primary, ok).Clicked || enter) && ok)
        {
            var id = _s.AddPiece(_newShelfName.Trim());
            if (id.Length > 0 && _newShelfFor is { } part)
                _s.Move(part, id);
            _ui.ClosePopup();
        }

        if (Painter.Button(_ui, "new-shelf-cancel", new Rect(makeBox.X - P(8) - cancelW, y, cancelW, P(36)), cancel, ButtonKind.Quiet).Clicked)
            _ui.ClosePopup();
    }

    // ---- the lifted card ----

    private void DrawDragGhost()
    {
        if (_ui.Drag?.Payload is not Card card)
            return;
        _c.SetCursor(CursorShape.Hand);
        _ui.BeginOverlay();
        var m     = _ui.Mouse;
        var ghost = new Rect(m.X + P(14), m.Y + P(26), P(116), P(156));
        Stats.Places["ghost"] = ghost;
        _c.PushRotation(-4, ghost.Min);
        Painter.Shadow(_c, ghost, P(10), _u, 36, 18, 0.6f);
        _c.FillRect(ghost, Theme.Card, P(10));
        var pic = ghost with { H = P(122) };
        Painter.Tile(_c, pic, card.Picture, card.Icon, card.Name, P(10), Corners.Top, P(48), 1.2f, Colr.Hex(0xF4ECF2, 0.34f));
        Painter.TextIn(_c, new Rect(ghost.X + P(8), pic.Bottom, ghost.W - P(16), P(34)), card.Name, Theme.Small, Theme.Text);
        _c.StrokeRect(ghost.Expand(P(2)), Theme.Accent, P(12), P(2));
        _c.PopRotation();
        _ui.EndOverlay();
    }
}
