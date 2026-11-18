using System.Numerics;
using ModCloset2.Core;
using ModCloset2.Ui.Canvas;
using ModCloset2.Ui.Widgets;

namespace ModCloset2.Ui.Face;

public sealed partial class WindowFace
{
    private const string LooksRowId  = "looks";
    private const string ColoursId   = "colours";
    private const string PopLook     = "look";
    private const string PopReplace  = "replace";

    private Guid? _renaming;
    private string _renameText = string.Empty;
    private bool _renameFocus;
    private bool _renameWasActive;

    /// <summary> The rail row for the looks: its count is the pinned looks, its second text the look that is on. </summary>
    private RailRow LooksRow(ILooks looks)
        => new()
        {
            Id       = LooksRowId,
            Name     = Loc.T("Looks"),
            Group    = PieceGroup.Top,
            Icon     = Icons.Looks,
            Count    = looks.Pinned.Count,
            // the look's name, or the names of equal looks ("Glam one or Glam two"); "" for an outfit of its own
            WornName = looks.Now.Name,
        };

    // ---- the view ----

    private void LooksWide(Rect r, ILooks looks)
    {
        var x = r.X + P(24);
        var w = r.W - P(48);
        HeaderText(new Rect(x, r.Y + P(16), w, P(40)), Loc.T("Looks"), looks.Pinned.Count, false);
        var y = r.Y + P(68);
        NowStrip(new Rect(x, y, w, P(84)), looks, false);
        y += P(84);
        var trying = _s.Trying.Active;
        var bottom = trying ? r.Bottom - P(18) - P(60) - P(14) : r.Bottom - P(18);
        LookLists(new Rect(x, y, w, bottom - y), looks, false);
        if (trying)
            TryBarWide(new Rect(x, r.Bottom - P(18) - P(60), w, P(60)));
        Toast(new Rect(x, r.Y, w, r.H), trying ? r.Bottom - P(18) - P(60) : r.Bottom - P(18));
    }

    private void LooksSlim(Rect area, ILooks looks)
    {
        var x = area.X + P(12);
        var w = area.W - P(24);
        HeaderText(new Rect(area.X + P(16), area.Y + P(10), area.W - P(32), P(32)), Loc.T("Looks"), looks.Pinned.Count, true);
        var y  = area.Y + P(50);
        var nh = NowSlimHeight(looks.Now, w);
        NowStrip(new Rect(x, y, w, nh), looks, true);
        y += nh;
        var trying = _s.Trying.Active;
        var barH   = trying ? SlimBarHeight(w) : 0;
        var bottom = trying ? area.Bottom - P(12) - barH - P(12) : area.Bottom - P(12);
        LookLists(new Rect(x, y, w, bottom - y), looks, true);
        if (trying)
            TryBarSlim(new Rect(x, area.Bottom - P(12) - barH, w, barH));
        Toast(new Rect(x, area.Y, w, area.H), trying ? area.Bottom - P(12) - barH : area.Bottom - P(12));
    }

    private float NowSlimHeight(WearingNow now, float w)
    {
        // the text beside the collage can reach below it (a long line under the name, two lines of it)
        var (_, textH) = NowText(now, w - P(14 + 48 + 16 + 14));
        var below      = Math.Max(0, textH - P(48 + 4));
        return P(12 + 48 + 10 + 34) + below + (now.CanPin || now.CanUpdate ? P(10 + 38) : 0) + P(12);
    }

    /// <summary>
    /// The line under the name ("You changed the shoes", "These looks are the same. Click the one you are wearing."),
    /// wrapped to two lines at most, and the height of the label, the name and that line together.
    /// </summary>
    private (List<string> Lines, float Height) NowText(WearingNow now, float width)
    {
        var lines = now.ChangeText.Length > 0 ? Painter.Wrap(_c, now.ChangeText, width, Theme.Small, 2) : new List<string>();
        var h     = _c.LineHeight(Theme.Label) + P(3) + _c.LineHeight(Theme.Strong) + (lines.Count > 0 ? P(3) + lines.Count * _c.LineHeight(Theme.Small) : 0);
        return (lines, h);
    }

    /// <summary> The look's name, the names of equal looks ("Glam one or Glam two"), or "An outfit of its own". </summary>
    private string NowName(WearingNow now)
        => now.Name.Length > 0 ? now.Name : Loc.T("An outfit of its own");

    /// <summary> "Wearing now": the collage, the name and what changed, the pieces, and Pin as new / Update. </summary>
    private void NowStrip(Rect r, ILooks looks, bool slim)
    {
        Stats.Places["now"] = r;
        var now = looks.Now;
        _c.FillRect(r, Theme.Rail, P(12));
        var cs      = slim ? P(48) : P(56);
        var collage = new Rect(r.X + P(14), slim ? r.Y + P(12) : r.Center.Y - cs / 2, cs, cs);
        Collage(collage, now.Tiles, null, P(10), now.Name, true, P(18));

        var tx  = collage.Right + P(16);
        var lh  = _c.LineHeight(Theme.Label);
        var sh  = _c.LineHeight(Theme.Strong);
        var ch  = _c.LineHeight(Theme.Small);

        // buttons first (at the right, or on their own line when slim): the rest takes what is left
        var pinText = Loc.T("Pin as new");
        var updText = Loc.F("Update {0}", now.Name);
        var pinW    = now.CanPin ? Painter.ButtonWidth(_c, pinText, Theme.Body, P(14)) : 0;
        var updW    = now.CanUpdate ? Math.Min(P(220), Painter.ButtonWidth(_c, updText, Theme.BodyBold, P(14))) : 0;
        Rect pinBox, updBox;
        float right;
        if (slim)
        {
            var by    = r.Bottom - P(12) - P(38);
            var count = (now.CanPin ? 1 : 0) + (now.CanUpdate ? 1 : 0);
            var each  = count == 0 ? 0 : (r.W - P(28) - (count - 1) * P(8)) / count;
            pinBox = new Rect(r.X + P(14), by, now.CanPin ? each : 0, P(38));
            updBox = new Rect(now.CanPin ? pinBox.Right + P(8) : r.X + P(14), by, now.CanUpdate ? each : 0, P(38));
            right  = r.Right - P(14);
        }
        else
        {
            updBox = new Rect(r.Right - P(14) - updW, r.Center.Y - P(19), updW, P(38));
            pinBox = new Rect((now.CanUpdate ? updBox.X - P(10) : r.Right - P(14)) - pinW, updBox.Y, pinW, P(38));
            right  = (now.CanPin ? pinBox.X : now.CanUpdate ? updBox.X : r.Right - P(14)) - P(16);
        }

        // the name gives way to the pieces down to a narrow column; what still does not fit shows as "+N"
        var tile   = slim ? P(34) : P(38);
        var tilesW = now.Tiles.Count == 0 ? P(120) : now.Tiles.Count * (tile + P(6)) - P(6);
        var nameW  = slim ? right - tx : Math.Clamp(right - tx - P(16) - tilesW, Math.Min(P(130), right - tx), P(210));
        var (changeLines, textH) = NowText(now, nameW);
        var ty = slim ? Math.Max(r.Y + P(8), collage.Center.Y - textH / 2) : Math.Max(r.Y + P(4), collage.Center.Y - textH / 2);
        Painter.TextIn(_c, new Rect(tx, ty, nameW, lh), Loc.T("Wearing now"), Theme.Label, Theme.Muted);
        var nameBox = new Rect(tx, ty + lh + P(3), nameW, sh);
        Stats.Places["now-name"] = nameBox;
        var shownName = NowName(now);
        var drawnW    = Painter.TextIn(_c, nameBox, shownName, Theme.Strong, Theme.Text);
        if (drawnW < _c.Measure(shownName, Theme.Strong).X - 0.5f)
        {
            // cut: the whole name (or the equal looks' names) on hovering
            _ui.Interact("now-name", nameBox, false);
            _ui.Tip("now-name", nameBox, shownName);
        }

        var cy = nameBox.Bottom + P(3);
        foreach (var line in changeLines)
        {
            Painter.TextIn(_c, new Rect(tx, cy, nameW, ch), line, Theme.Small, Theme.Gold);
            cy += ch;
        }

        // the pieces that are on, as small tiles; a changed one has a gold edge
        var x    = slim ? r.X + P(14) : tx + nameW + P(16);
        var y    = slim ? Math.Max(collage.Bottom, ty + textH) + P(10) : r.Center.Y - tile / 2;
        var end  = slim ? r.Right - P(14) : right;
        var fits = Math.Max(0, (int)((end - x + P(6)) / (tile + P(6))));
        var show = fits >= now.Tiles.Count ? now.Tiles.Count : Math.Max(0, fits - 1);
        for (var i = 0; i < show; ++i)
        {
            var t   = now.Tiles[i];
            var box = new Rect(x, y, tile, tile);
            var id  = "now-tile/" + i;
            Painter.Tile(_c, box, t.Picture, t.Icon, t.Name, P(9), Corners.All, P(20), 1.5f, Colr.Hex(0xF4ECF2, 0.5f));
            if (t.Changed)
                _c.StrokeRect(box, Theme.Gold, P(9), P(2));
            _ui.Interact(id, box, false);
            _ui.Tip(id, box, t.Changed ? Loc.F("{0} · changed", t.Name) : t.Name);
            x += tile + P(6);
        }

        if (show < now.Tiles.Count && fits > 0)
        {
            var box   = new Rect(x, y, tile, tile);
            var rest  = now.Tiles.Skip(show).ToList();
            _c.FillRect(box, Theme.Picked, P(9));
            if (rest.Any(t => t.Changed))
                _c.StrokeRect(box, Theme.Gold, P(9), P(2));
            Painter.TextIn(_c, box, Loc.F("+{0}", rest.Count), Theme.Caption, Theme.Muted, 0.5f);
            _ui.Interact("now-tile/more", box, false);
            _ui.Tip("now-tile/more", box, string.Join(", ", rest.Select(t => t.Changed ? Loc.F("{0} · changed", t.Name) : t.Name)));
        }

        if (now.Tiles.Count == 0)
            Painter.TextIn(_c, new Rect(x, y, end - x, tile), Loc.T("Nothing is on yet."), Theme.Caption, Theme.Muted);

        if (now.CanPin && Painter.Button(_ui, "now-pin", pinBox, pinText, ButtonKind.Outline, true, Theme.Body).Clicked)
            looks.PinNow();
        if (now.CanUpdate && now.Look is { } look)
        {
            var resp = Painter.Button(_ui, "now-update", updBox, updText, ButtonKind.Best, true, Theme.BodyBold);
            if (updW < Painter.ButtonWidth(_c, updText, Theme.BodyBold, P(14)))
                _ui.Tip("now-update", updBox, updText);
            if (resp.Clicked)
                looks.UpdateFromNow(look);
        }

        Stats.Places["now-pin"]    = pinBox;
        Stats.Places["now-update"] = updBox;
    }

    /// <summary> Pinned looks, then the outfits the closet kept lately; one scroll area. </summary>
    private void LookLists(Rect area, ILooks looks, bool slim)
    {
        var gap    = slim ? P(12) : P(14);
        var cols   = Math.Max(slim ? 2 : 1, (int)MathF.Round((area.W + gap) / (P(140) + gap)));
        var cw     = (area.W - (cols - 1) * gap) / cols;
        var pinH   = cw * 186f / 140f + P(9 + 18 + 30) + P(3);
        var lateCols = Math.Max(slim ? 3 : cols, cols);
        var lw     = (area.W - (lateCols - 1) * gap) / lateCols;
        var lateH  = lw * 112f / 140f + P(34);
        var pinned = looks.Pinned;
        var lately = looks.Lately;

        if (pinned.Count == 0 && lately.Count == 0)
        {
            Message(area, Loc.T("No looks yet."), Loc.T("The closet keeps what you wear on its own. Pin an outfit and it stays here."), Icons.Looks, Theme.Muted);
            return;
        }

        var pinRows  = (pinned.Count + cols - 1) / cols;
        var lateRows = (lately.Count + lateCols - 1) / lateCols;
        var emptyPin = pinned.Count == 0 ? P(40) : 0;
        var content  = P(40) + Math.Max(emptyPin, pinRows * (pinH + gap) - gap) + (lately.Count > 0 ? P(40) + lateRows * (lateH + gap) - gap : 0) + P(4);
        var view     = new Rect(area.X - P(4), area.Y, area.W + P(8), area.H);
        var scroll   = _ui.Scroll("looks", view, content, P(100));
        Stats.Places["looks-list"] = view;
        _ui.PushClip(view);
        var y = area.Y - scroll;

        Painter.TextIn(_c, new Rect(area.X, y + P(18), area.W, P(14)), Loc.T("Pinned"), Theme.Label, Theme.Muted);
        y += P(40);
        if (pinned.Count == 0)
            Painter.TextIn(_c, new Rect(area.X, y, area.W, P(24)), Loc.T("Pin an outfit you like and it stays here."), Theme.Caption, Theme.Muted);
        for (var i = 0; i < pinned.Count; ++i)
        {
            var box = new Rect(area.X + i % cols * (cw + gap), y + i / cols * (pinH + gap), cw, pinH);
            if (box.Bottom >= area.Y && box.Y <= area.Bottom)
                PinnedCard(looks, pinned[i], box, cw * 186f / 140f);
        }

        y += Math.Max(emptyPin, pinRows * (pinH + gap) - gap);
        if (lately.Count > 0)
        {
            var lw0 = _c.Measure(Loc.T("Lately"), Theme.Label).X;
            Painter.TextIn(_c, new Rect(area.X, y + P(18), lw0, P(14)), Loc.T("Lately"), Theme.Label, Theme.Muted);
            Painter.TextIn(_c, new Rect(area.X + lw0 + P(12), y + P(17), area.W - lw0 - P(12), P(16)), Loc.T("The closet keeps what you wore on its own. Pin the ones you like."), Theme.Small, Theme.Muted);
            y += P(40);
            for (var i = 0; i < lately.Count; ++i)
            {
                var box = new Rect(area.X + i % lateCols * (lw + gap), y + i / lateCols * (lateH + gap), lw, lateH);
                if (box.Bottom >= area.Y && box.Y <= area.Bottom)
                    LatelyCard(looks, lately[i], box, lw * 112f / 140f);
            }
        }

        _ui.PopClip();
        _ui.ScrollBar("looks", new Rect(area.X, area.Y, area.W + (slim ? P(10) : P(18)), area.H), content, 6);
    }

    /// <summary>
    /// A collage of a look's pieces: four in a 2 × 2, three as one tall and two small, two side by side, one alone;
    /// pictures where there are some, else the shelf's icon on a tint.
    /// </summary>
    private void Collage(Rect r, IReadOnlyList<LookTile> tiles, string? picture, float radius, string key, bool icons, float iconSize, Corners corners = Corners.All)
    {
        _c.FillRect(r, Theme.Window, radius, corners);
        if (!string.IsNullOrEmpty(picture) && _c.Picture(picture, r, radius, corners))
            return;
        var g    = P(2);
        var hw   = (r.W - g) / 2;
        var hh   = (r.H - g) / 2;
        var list = tiles.Take(4).ToList();
        var boxes = list.Count switch
        {
            0 => new[] { r },
            1 => new[] { r },
            2 => new[] { r with { W = hw }, new Rect(r.X + hw + g, r.Y, hw, r.H) },
            3 => new[] { r with { W = hw }, new Rect(r.X + hw + g, r.Y, hw, hh), new Rect(r.X + hw + g, r.Y + hh + g, hw, hh) },
            _ => new[] { new Rect(r.X, r.Y, hw, hh), new Rect(r.X + hw + g, r.Y, hw, hh), new Rect(r.X, r.Y + hh + g, hw, hh), new Rect(r.X + hw + g, r.Y + hh + g, hw, hh) },
        };

        for (var i = 0; i < boxes.Length; ++i)
        {
            var box = boxes[i];
            // only the collage's outer corners are rounded
            var c = Corners.None;
            if (box.X <= r.X + 0.5f && box.Y <= r.Y + 0.5f)
                c |= Corners.TopLeft;
            if (box.Right >= r.Right - 0.5f && box.Y <= r.Y + 0.5f)
                c |= Corners.TopRight;
            if (box.X <= r.X + 0.5f && box.Bottom >= r.Bottom - 0.5f)
                c |= Corners.BottomLeft;
            if (box.Right >= r.Right - 0.5f && box.Bottom >= r.Bottom - 0.5f)
                c |= Corners.BottomRight;
            c &= corners;
            if (i >= list.Count)
            {
                _c.FillRect(box, Theme.Tint(key), radius, c);
                Painter.Icon(_c, Icons.Outfits, box.Center, iconSize, Colr.Hex(0xF4ECF2, 0.3f), 1.3f);
                continue;
            }

            var t = list[i];
            if (!string.IsNullOrEmpty(t.Picture) && _c.Picture(t.Picture, box, radius, c))
                continue;
            _c.FillRect(box, Theme.Tint(key + t.Piece + t.Name), radius, c);
            if (icons)
                Painter.Icon(_c, t.Icon, box.Center, iconSize, Colr.Hex(0xF4ECF2, 0.3f), 1.3f);
        }
    }

    private void PinnedCard(ILooks looks, LookCard card, Rect r, float collageH)
    {
        var id = "look/" + card.Id;
        Stats.Places[id] = r;
        var collage = r with { H = collageH };

        // the parts with a meaning of their own are asked first, so a click there is theirs and not the card's
        var markText = card.State switch
        {
            CardState.On     => Loc.T("On"),
            CardState.Trying => Loc.T("Trying on"),
            _                => card.Missing > 0 ? Loc.F("{0} missing", card.Missing) : string.Empty,
        };
        var markFill = card.State switch { CardState.On => Theme.Good, CardState.Trying => Theme.Accent, _ => Theme.Gold };
        var markBox  = new Rect(collage.X + P(8), collage.Y + P(8), _c.Measure(markText, Theme.Mark).X + P(14), P(20));
        var missResp = card.Missing > 0 && card.State == CardState.None ? _ui.Interact("missing/" + card.Id, markBox) : default;
        var dots     = card.Colourways.Count >= 2 ? card.Colourways : Array.Empty<uint>();
        var metaY    = collage.Bottom + P(9 + 18);
        var dotResps = new List<(Rect Box, Response Resp)>();
        for (var i = 0; i < dots.Count; ++i)
        {
            var box = new Rect(r.X + P(10) + i * P(15), metaY + P(10), P(10), P(10));
            dotResps.Add((box, _ui.Interact($"way/{card.Id}/{i}", box.Expand(P(3)))));
            Stats.Places[$"way/{card.Id}/{i}"] = box;
        }

        var resp = _renaming == card.Id ? default : _ui.Interact(id, r);
        _c.FillRect(r, Theme.Card, P(12));
        Collage(collage, card.Tiles, card.Picture, P(12), card.Name, true, P(30), Corners.Top);
        if (markText.Length > 0)
        {
            Painter.Mark(_c, markBox.Min, markText, markFill, _u);
            if (card.Missing > 0 && card.State == CardState.None)
            {
                Stats.Places["missing/" + card.Id] = markBox;
                _ui.Tip("missing/" + card.Id, markBox, Loc.T("Some of its mods are gone: click for the closest ones"));
            }
        }

        if (_renaming == card.Id)
            RenameField(looks, card, new Rect(r.X + P(6), collage.Bottom + P(5), r.W - P(12), P(26)));
        else
        {
            var fitted = Painter.Fit(_c, card.Name, r.W - P(20), Theme.Caption.With(Weight.Medium));
            _c.Text(new Vector2(r.X + P(10), collage.Bottom + P(9) + (P(18) - _c.LineHeight(Theme.Caption)) / 2), fitted, Theme.Caption.With(Weight.Medium), Theme.Text);
            if (fitted != card.Name)
                _ui.Tip(id, r, card.Name);
        }

        var mx = r.X + P(10);
        for (var i = 0; i < dotResps.Count; ++i)
        {
            var (box, dr) = dotResps[i];
            Dot(box.Center, P(5), dots[i]);
            if (i == card.Colourway)
                _c.StrokeCircle(box.Center, P(7.5f), Theme.Accent, P(1.5f));
            else if (dr.Hovered)
                _c.StrokeCircle(box.Center, P(7.5f), Theme.Muted, P(1));
            _ui.Tip($"way/{card.Id}/{i}", box, Loc.F("Colourway {0}", i + 1));
            if (dr.Clicked)
                looks.TryColourway(card.Id, i);
            mx = box.Right + P(5);
        }

        Painter.TextIn(_c, new Rect(mx + (dots.Count > 0 ? P(2) : 0), metaY, r.Right - P(10) - mx, P(30)), card.Meta, Theme.Small, Theme.Muted);

        if (resp.Hovered)
            _c.FillRect(r, Theme.HoverWash, P(12));
        if (card.State == CardState.Trying)
            _c.StrokeRect(r.Expand(P(2)), Theme.Accent, P(14), P(2));
        if (missResp.Clicked)
            _ui.OpenPopup(PopReplace, new Vector2(markBox.X, markBox.Bottom + P(6)), card);
        else if (resp.Clicked && card.State != CardState.Trying)
            looks.Try(card.Id);
        if (resp.RightClicked)
            _ui.OpenPopup(PopLook, _ui.Mouse, card);
    }

    private void LatelyCard(ILooks looks, LookCard card, Rect r, float collageH)
    {
        var id = "lately/" + card.Id;
        Stats.Places[id] = r;
        var collage = r with { H = collageH };
        var pin     = new Rect(r.Right - P(4) - P(28), collage.Bottom + P(3), P(28), P(28));
        var pinResp = _ui.Interact("lately-pin/" + card.Id, pin);
        var resp    = _ui.Interact(id, r);
        Stats.Places["lately-pin/" + card.Id] = pin;
        _c.FillRect(r, Theme.Rail, P(12));
        Collage(collage, card.Tiles, card.Picture, P(12), card.Name, false, P(22), Corners.Top);
        if (card.State != CardState.None)
            Painter.Mark(_c, collage.Min + new Vector2(P(8), P(8)), card.State == CardState.On ? Loc.T("On") : Loc.T("Trying on"), card.State == CardState.On ? Theme.Good : Theme.Accent, _u);
        Painter.TextIn(_c, new Rect(r.X + P(10), collage.Bottom, pin.X - r.X - P(14), P(34)), card.When, Theme.Small, Theme.Muted);
        if (pinResp.Hovered)
            _c.FillRect(pin, Theme.Picked, P(7));
        Painter.Icon(_c, Icons.Pin, pin.Center, P(14), pinResp.Hovered ? Theme.Text : Theme.Muted, 1.9f);
        _ui.Tip("lately-pin/" + card.Id, pin, Loc.T("Pin this outfit as a look"));
        if (resp.Hovered)
            _c.FillRect(r, Theme.HoverWash, P(12));
        if (card.State == CardState.Trying)
            _c.StrokeRect(r.Expand(P(2)), Theme.Accent, P(14), P(2));
        if (pinResp.Clicked)
            looks.Pin(card.Id);
        else if (resp.Clicked && card.State != CardState.Trying)
            looks.Try(card.Id);
        if (resp.RightClicked)
            _ui.OpenPopup(PopLook, _ui.Mouse, card);
    }

    // ---- rename in place ----

    private void StartRename(LookCard card)
    {
        _renaming        = card.Id;
        _renameText      = card.Name;
        _renameFocus     = true;
        _renameWasActive = false;
    }

    private void RenameField(ILooks looks, LookCard card, Rect r)
    {
        Stats.Places["rename"] = r;
        _c.FillRect(r, Theme.Rail, P(7));
        _c.StrokeRect(r, Theme.Accent.Fade(0.7f), P(7), Math.Max(1, _u));
        var text  = _renameText;
        var enter = _ui.In.TextFocused && _ui.In.KeyPressed(Key.Enter);
        if (_c.TextField("rename", r.Inset(P(8), 0), ref text, Loc.T("Name of the look"), Theme.Caption, Theme.Text, Theme.Muted, _renameFocus, !_ui.Blocked, out var active))
            _renameText = text;
        _renameFocus = false;
        if (enter && _renameText.Trim().Length > 0)
        {
            looks.Rename(card.Id, _renameText.Trim());
            _renaming = null;
            return;
        }

        // Esc, a click elsewhere, or an emptied field: the old name stays
        if (_renameWasActive && !active)
            _renaming = null;
        _renameWasActive |= active;
    }

    // ---- menus ----

    private void LookMenu(ILooks looks, LookCard card)
    {
        var items = new List<MenuItem>();
        if (card.Pinned)
        {
            items.Add(new MenuItem("rename", Loc.T("Rename")) { Icon = Icons.Settings });
            items.Add(new MenuItem("body", Loc.T("Also change hair and body")) { Checked = card.WithBody });
            if (card.Colourways.Count >= 2)
                items.Add(new MenuItem("way", Loc.T("Remove this colourway")) { Icon = Icons.Palette });
            items.Add(MenuItem.Separator());
            items.Add(new MenuItem("remove", Loc.T("Remove")) { Icon = Icons.Close });
        }
        else
        {
            items.Add(new MenuItem("pin", Loc.T("Pin as a look")) { Icon = Icons.Pin });
            items.Add(MenuItem.Separator());
            items.Add(new MenuItem("remove", Loc.T("Forget this outfit")) { Icon = Icons.Close });
        }

        var picked = Menu.Draw(_ui, PopLook, _ui.PopupAnchor, items, 220);
        switch (picked?.Id)
        {
            case "rename":
                StartRename(card);
                break;
            case "body":
                looks.SetWithBody(card.Id, !card.WithBody);
                break;
            case "way":
                looks.RemoveColourway(card.Id, card.Colourway);
                break;
            case "remove":
                looks.Remove(card.Id);
                break;
            case "pin":
                looks.Pin(card.Id);
                break;
        }
    }

    /// <summary> A look with pieces whose mod is gone: per missing shelf the closest parts; a click puts one in. </summary>
    private void ReplacePanel(ILooks looks, LookCard card)
    {
        var missing = card.Tiles.Where(t => t.Missing).ToList();
        var lists   = missing.Select(t => (Tile: t, Cards: looks.ReplacementsFor(card.Id, t.Piece).Take(4).ToList())).ToList();
        var w       = Math.Min(P(380), _c.Bounds.W - P(16));
        var tileW   = (w - P(32) - P(3 * 8)) / 4;
        var tileH   = tileW * 1.15f + P(18);
        var rowH    = P(22) + P(6) + tileH;
        var h       = P(16) + P(22) + P(6) + P(18) + P(10) + Math.Max(1, lists.Count) * (rowH + P(10)) + P(6);
        var a       = _ui.PopupAnchor;
        var box     = new Rect(Math.Clamp(a.X, _c.Bounds.X + P(8), _c.Bounds.Right - P(8) - w), Math.Clamp(a.Y, _c.Bounds.Y + P(8), Math.Max(_c.Bounds.Y + P(8), _c.Bounds.Bottom - P(8) - h)), w, h);
        _ui.PopupArea(box);
        Stats.Places["replace"] = box;
        Painter.Shadow(_c, box, P(12), _u, 18, 8, 0.55f);
        _c.FillRect(box, Theme.Card, P(12));
        _c.StrokeRect(box, Theme.EdgeStrong, P(12), Math.Max(1, _u));
        var x = box.X + P(16);
        var y = box.Y + P(16);
        Painter.TextIn(_c, new Rect(x, y, box.W - P(32), P(22)), Loc.F("{0}: what is gone", card.Name), Theme.Strong, Theme.Text);
        y += P(22 + 6);
        Painter.TextIn(_c, new Rect(x, y, box.W - P(32), P(18)), Loc.T("Pick one of these to take its place."), Theme.Caption, Theme.Muted);
        y += P(18 + 10);
        if (lists.Count == 0)
        {
            Painter.TextIn(_c, new Rect(x, y, box.W - P(32), P(22)), Loc.T("Nothing like it on that shelf."), Theme.Caption, Theme.Muted);
            return;
        }

        foreach (var (tile, cards) in lists)
        {
            var label = tile.Name.Length > 0 ? Loc.F("{0} · was {1}", ShelfName(tile.Piece), tile.Name) : ShelfName(tile.Piece);
            Painter.TextIn(_c, new Rect(x, y, box.W - P(32), P(22)), label, Theme.Small, Theme.Muted);
            y += P(22 + 6);
            if (cards.Count == 0)
                Painter.TextIn(_c, new Rect(x, y, box.W - P(32), P(22)), Loc.T("Nothing like it on that shelf."), Theme.Caption, Theme.Muted);
            for (var i = 0; i < cards.Count; ++i)
            {
                var c    = cards[i];
                var cb   = new Rect(x + i * (tileW + P(8)), y, tileW, tileH);
                var id   = $"replace/{tile.Piece}/{i}";
                Stats.Places[id] = cb;
                var resp = _ui.Interact(id, cb);
                var pic  = cb with { H = tileH - P(18) };
                PictureTile($"replace/{tile.Piece}/{c.Key}", c.Key, pic, c.Picture, c.Icon, c.Name, P(8), Corners.All, P(24), 1.2f);
                if (resp.Hovered)
                    _c.StrokeRect(pic.Expand(P(2)), Theme.Accent, P(10), P(1.5f));
                Painter.TextIn(_c, new Rect(cb.X, pic.Bottom + P(2), cb.W, P(16)), c.Name, Theme.Small, Theme.Text);
                _ui.Tip(id, cb, c.Name);
                if (resp.Clicked)
                {
                    looks.Replace(card.Id, tile.Piece, c.Key);
                    _ui.ClosePopup();
                }
            }

            y += tileH + P(10);
        }
    }
}
