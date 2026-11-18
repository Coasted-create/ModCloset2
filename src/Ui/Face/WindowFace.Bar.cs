using System.Numerics;
using ModCloset2.Core;
using ModCloset2.Ui.Canvas;
using ModCloset2.Ui.Widgets;

namespace ModCloset2.Ui.Face;

public sealed partial class WindowFace
{
    private int _toastDismissed = -1;

    private string Hint
        => Loc.T("Click a mod to try it on. Nothing is kept until you say so.");

    /// <summary> The try-on bar: what is tried, "Also in this mod", Put back and Keep. When nothing is tried, the quiet hint. </summary>
    private void TryBarWide(Rect r)
    {
        Stats.Places["bar"] = r;
        var t = _s.Trying;
        if (!t.Active)
        {
            _c.StrokeRect(r, Theme.Edge, P(12), Math.Max(1, _u), Corners.All, P(5), P(4));
            var dotsW = t.Busy ? P(34) : 0;
            Painter.TextIn(_c, r.Inset(P(16), 0, P(16) + dotsW, 0), Hint, Theme.Caption, Theme.Muted);
            if (t.Busy)
                BusyMark(new Rect(r.Right - P(16) - dotsW, r.Y, dotsW, r.H));
            return;
        }

        _c.FillRect(r, Theme.Raised, P(12));
        _c.StrokeRect(r, Theme.EdgeStrong, P(12), Math.Max(1, _u));

        var keepText = Loc.T("Keep");
        var backText = Loc.T("Put back");
        var keepW    = Painter.ButtonWidth(_c, keepText, Theme.BodyBold, P(24));
        var backW    = Painter.ButtonWidth(_c, backText, Theme.Body, P(16));
        var keep     = new Rect(r.Right - P(10) - keepW, r.Center.Y - P(20), keepW, P(40));
        var back     = new Rect(keep.X - P(10) - backW, keep.Y, backW, P(40));
        Buttons(back, keep);

        // left part: lead, name, note, then "Also in this mod" while there is room
        var x     = r.X + P(16);
        var right = back.X - P(16);
        var leadW = _c.Measure(t.Lead, Theme.Small).X;
        Painter.TextIn(_c, new Rect(x, r.Y, leadW, r.H), t.Lead, Theme.Small, Theme.Muted);
        x += leadW + P(10);

        var busyW  = t.Busy ? P(30) : 0;
        var nameW  = _c.Measure(t.Name, Theme.Strong).X;
        var noteW  = t.Note.Length > 0 ? _c.Measure(t.Note, Theme.Caption).X + P(10) : 0;
        var extras = t.Extras.Count > 0 ? ExtrasWidth(t) + P(10) : 0;
        var room   = right - x - busyW;
        if (nameW + noteW + extras > room)
        {
            // as many "+ part" chips as fit; none at all only when not even the first one does
            var first = t.Extras.Count > 0
                ? _c.Measure(Loc.T("Also in this mod"), Theme.Caption).X + P(10) + Math.Max(P(60), _c.Measure("+ " + t.Extras[0].Name, Theme.Caption).X + P(20)) + P(30)
                : 0;
            extras = nameW + noteW + first <= room ? first : 0;
        }
        if (nameW + noteW > room)
            noteW = Math.Max(0, room - nameW) > P(80) ? room - nameW : 0;
        nameW = Math.Min(nameW, room - noteW);

        var nameResp = _ui.Interact("bar-name", new Rect(x, r.Y, nameW, r.H), false);
        var fitted   = Painter.Fit(_c, t.Name, nameW + 0.5f, Theme.Strong);
        Painter.TextIn(_c, new Rect(x, r.Y, nameW, r.H), fitted, Theme.Strong, Theme.Text);
        if (fitted != t.Name && nameResp.Hovered)
            _ui.Tip("bar-name", new Rect(x, r.Y, nameW, r.H), t.Name);
        x += nameW;
        if (t.Busy)
        {
            Painter.Busy(_c, new Vector2(x + P(10), r.Center.Y), _u, _ui.Time);
            _ui.Interact("bar-busy", new Rect(x, r.Y, busyW, r.H), false);
            _ui.Tip("bar-busy", new Rect(x, r.Y, busyW, r.H), Loc.T("The game is still dressing your character."));
            x += busyW;
        }

        if (noteW > 0)
        {
            Painter.TextIn(_c, new Rect(x + P(10), r.Y, noteW - P(10), r.H), t.Note, Theme.Caption, Theme.Muted);
            x += noteW;
        }

        if (extras > 0)
            Extras(new Rect(x + P(20), r.Center.Y - P(14), right - x - P(20), P(28)), t);
    }

    /// <summary> A Keep, Put back or Undo still waiting for the game: the hint line stays, with the quiet dots at its end. </summary>
    private void BusyMark(Rect r)
    {
        Stats.BusyShown = true;
        Painter.Busy(_c, new Vector2(r.X + P(6), r.Center.Y), _u, _ui.Time);
        _ui.Interact("bar-busy", r, false);
        _ui.Tip("bar-busy", r, Loc.T("The game is still dressing your character."));
    }

    private float ExtrasWidth(TryState t)
    {
        var w = _c.Measure(Loc.T("Also in this mod"), Theme.Caption).X + P(10);
        foreach (var e in t.Extras)
            w += _c.Measure("+ " + e.Name, Theme.Caption).X + P(20) + P(8);
        return w;
    }

    /// <summary> "Also in this mod  + Obi belt  + Sandals": each chip adds that part to what is tried. </summary>
    private void Extras(Rect r, TryState t)
    {
        var label = Loc.T("Also in this mod");
        var lw    = _c.Measure(label, Theme.Caption).X;
        Painter.TextIn(_c, new Rect(r.X, r.Y, lw, r.H), label, Theme.Caption, Theme.Muted);
        var x = r.X + lw + P(10);
        foreach (var e in t.Extras)
        {
            var text = "+ " + e.Name;
            var w    = Math.Min(_c.Measure(text, Theme.Caption).X + P(20), r.Right - x);
            if (w < P(60))
                break;
            var box  = new Rect(x, r.Y, w, r.H);
            var id   = "extra/" + e.Key;
            var resp = _ui.Interact(id, box);
            if (resp.Hovered)
                _c.FillRect(box, Theme.Picked, P(8));
            _c.StrokeRect(box, Theme.EdgeStrong, P(8), Math.Max(1, _u));
            Painter.TextIn(_c, box.Inset(P(10), 0), text, Theme.Caption, Theme.Text);
            _ui.Tip(id, box, Loc.F("Try {0} on as well", e.Name));
            if (resp.Clicked)
                _s.Try(e.Key);
            x += w + P(8);
        }
    }

    private void Buttons(Rect back, Rect keep)
    {
        var backResp = Painter.Button(_ui, "put-back", back, Loc.T("Put back"), ButtonKind.Outline);
        _ui.Tip("put-back", back, Loc.T("Put back: your character is as before"), "Esc");
        if (backResp.Clicked)
            _s.PutBack();
        var keepResp = Painter.Button(_ui, "keep", keep, Loc.T("Keep"), ButtonKind.Primary);
        _ui.Tip("keep", keep, Loc.T("Keep what you are trying on"), "Enter");
        if (keepResp.Clicked)
            _s.Keep();
    }

    /// <summary> Height the slim bar needs at this width. </summary>
    private float SlimBarHeight(float w)
    {
        var t = _s.Trying;
        if (!t.Active)
            return Painter.Wrap(_c, Hint, w - P(32) - (t.Busy ? P(34) : 0), Theme.Caption, 3).Count * _c.LineHeight(Theme.Caption) + P(24);
        return P(12) + P(22) + P(10) + (t.Extras.Count > 0 ? P(28) + P(10) : 0) + P(40) + P(12);
    }

    /// <summary> The slim bar: the text on one line, the two buttons side by side under it. </summary>
    private void TryBarSlim(Rect r)
    {
        Stats.Places["bar"] = r;
        var t = _s.Trying;
        if (!t.Active)
        {
            _c.StrokeRect(r, Theme.Edge, P(12), Math.Max(1, _u), Corners.All, P(5), P(4));
            var lh    = _c.LineHeight(Theme.Caption);
            var y     = r.Y + P(12);
            var dotsW = t.Busy ? P(34) : 0;
            foreach (var line in Painter.Wrap(_c, Hint, r.W - P(32) - dotsW, Theme.Caption, 3))
            {
                _c.Text(new Vector2(r.X + P(16), y), line, Theme.Caption, Theme.Muted);
                y += lh;
            }

            if (t.Busy)
                BusyMark(new Rect(r.Right - P(16) - dotsW, r.Y + P(12), dotsW, lh));
            return;
        }

        _c.FillRect(r, Theme.Raised, P(12));
        _c.StrokeRect(r, Theme.EdgeStrong, P(12), Math.Max(1, _u));
        var line1 = new Rect(r.X + P(12), r.Y + P(12), r.W - P(24), P(22));
        var leadW = _c.Measure(t.Lead, Theme.Small).X;
        Painter.TextIn(_c, line1 with { W = leadW }, t.Lead, Theme.Small, Theme.Muted);
        var x     = line1.X + leadW + P(8);
        var busyW = t.Busy ? P(30) : 0;
        var nameW = Math.Min(_c.Measure(t.Name, Theme.Strong).X, line1.Right - x - busyW);
        Painter.TextIn(_c, new Rect(x, line1.Y, nameW, line1.H), t.Name, Theme.Strong, Theme.Text);
        x += nameW;
        if (t.Busy)
        {
            Painter.Busy(_c, new Vector2(x + P(10), line1.Center.Y), _u, _ui.Time);
            x += busyW;
        }

        if (t.Note.Length > 0 && line1.Right - x > P(70))
            Painter.TextIn(_c, new Rect(x + P(8), line1.Y, line1.Right - x - P(8), line1.H), t.Note, Theme.Caption, Theme.Muted);

        var y2 = line1.Bottom + P(10);
        if (t.Extras.Count > 0)
        {
            Extras(new Rect(line1.X, y2, line1.W, P(28)), t);
            y2 += P(28) + P(10);
        }

        var half = (line1.W - P(8)) / 2;
        Buttons(new Rect(line1.X, y2, half, P(40)), new Rect(line1.X + half + P(8), y2, half, P(40)));
    }

    /// <summary> What just happened (session.Status) for a few seconds above the bar. Errors are gold with a plain icon. </summary>
    private void Toast(Rect area, float above)
    {
        if (_s.Status is not { Text.Length: > 0 } status || status.Id == _toastDismissed)
            return;
        var life = status.IsError ? 8.0 : 4.0;
        if (status.Age >= life)
            return;
        Stats.ToastShown = true;
        var alpha = (float)Math.Clamp((life - status.Age) / 0.12, 0, 1);
        var icon  = status.IsError ? P(22) : 0;
        var maxW  = Math.Min(area.W, P(560));
        var text  = Painter.Fit(_c, status.Text, maxW - P(28) - icon, Theme.Caption);
        var w     = _c.Measure(text, Theme.Caption).X + P(28) + icon;
        var box   = new Rect(area.Center.X - w / 2, above - P(10) - P(36), w, P(36));
        _ui.BeginOverlay();
        Painter.Shadow(_c, box, P(10), _u, 12, 6, 0.4f * alpha);
        _c.FillRect(box, Theme.Raised.Fade(alpha), P(10));
        _c.StrokeRect(box, (status.IsError ? Theme.Gold.Fade(0.5f) : Theme.EdgeStrong).Fade(alpha), P(10), Math.Max(1, _u));
        if (status.IsError)
            Painter.Icon(_c, Icons.Info, new Vector2(box.X + P(14) + P(8), box.Center.Y), P(16), Theme.Gold.Fade(alpha), 1.8f);
        Painter.TextIn(_c, new Rect(box.X + P(14) + icon, box.Y, w - P(28) - icon, box.H), text, Theme.Caption, (status.IsError ? Theme.Gold : Theme.Text).Fade(alpha));
        Stats.Places["toast"] = box;
        _ui.OverlayArea(box);
        // a click puts this status away; the next one (a new Id, even with the same words) shows again
        if (_ui.Interact("toast", box).Clicked)
            _toastDismissed = status.Id;
        _ui.EndOverlay();
    }
}
