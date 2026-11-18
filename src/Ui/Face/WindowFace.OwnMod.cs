using System.Globalization;
using ModCloset2.Core;
using ModCloset2.Ui.Canvas;
using ModCloset2.Ui.Widgets;

namespace ModCloset2.Ui.Face;

/// <summary> "Make it its own mod…": the small sheet that asks, drawn from <see cref="IClosetSession.OwnModOf"/> every frame. </summary>
public sealed partial class WindowFace
{
    private string _ownName = string.Empty;
    private bool _ownFilled;
    private bool _ownFocus;
    private bool _ownSeenMaking;
    private int _ownSentFrames = -1;

    /// <summary> Both: "Make it its own mod". Left or Right: a copy of a one-slot ring, worn through that ring slot. </summary>
    private Hands _ownHand;

    private void OpenOwnMod(PartKey part, Hands to = Hands.Both)
    {
        _ownHand       = to;
        _ownName       = string.Empty;
        _ownFilled     = false;
        _ownFocus      = false;
        _ownSeenMaking = false;
        _ownSentFrames = -1;
        _ui.OpenPopup(PopOwnMod, _ui.PopupAnchor, part);
    }

    /// <summary> The sheet's title: "Make it its own mod", or the ring slot a ring's copy is for. </summary>
    private string OwnModTitle()
        => _ownHand switch
        {
            Hands.Left  => Loc.T("A copy for the left ring slot"),
            Hands.Right => Loc.T("A copy for the right ring slot"),
            _           => Loc.T("Make it its own mod"),
        };

    /// <summary> A size as people read it: "38 MB", "412 KB", "1.2 GB". </summary>
    public static string SizeText(long bytes)
    {
        var culture = Loc.Language == "de" ? German : CultureInfo.InvariantCulture;
        string N(double v, bool fraction)
            => v.ToString(fraction && v < 10 ? "0.#" : "0", culture);

        return bytes switch
        {
            >= 1L << 30 => Loc.F("{0} GB", N(bytes / (double)(1L << 30), true)),
            >= 1L << 20 => Loc.F("{0} MB", N(bytes / (double)(1L << 20), true)),
            >= 1L << 10 => Loc.F("{0} KB", N(bytes / (double)(1L << 10), false)),
            _           => Loc.F("{0} bytes", bytes),
        };
    }

    private void OwnModPanel(PartKey part)
    {
        var plan = _ownHand == Hands.Both ? _s.OwnModOf(part) : _s.OwnModOf(part, _ownHand);
        if (plan is null)
        {
            // the card went away (or the session cannot do it): nothing to ask about
            _ui.ClosePopup();
            return;
        }

        // after "Make the mod": the outcome comes as the status line, the sheet goes by itself
        if (plan.State == OwnModState.Making)
            _ownSeenMaking = true;
        if (_ownSentFrames >= 0)
            _ownSentFrames++;
        if (plan.State != OwnModState.Making && (_ownSeenMaking || _ownSentFrames > 2))
        {
            _ui.ClosePopup();
            return;
        }

        if (plan.State == OwnModState.Making && !_ownFilled)
        {
            // opened again while it is being made: the name shown is the proposed one
            _ownName   = plan.Name;
            _ownFilled = true;
        }

        if (plan.State == OwnModState.Ready && !_ownFilled)
        {
            // filled once with the proposed name, all of it selected: typing replaces it, Enter takes it as it is
            _ownName   = plan.Name;
            _ownFilled = true;
            _ownFocus  = true;
        }

        var w     = Math.Min(P(440), _c.Bounds.W - P(16));
        var inner = w - P(40);
        var lines = new List<(string Text, TextStyle Style, Colr Color)>();
        switch (plan.State)
        {
            case OwnModState.Working:
                lines.Add((Loc.T("Looking at the mod's files…"), Theme.Caption, Theme.Muted));
                break;
            case OwnModState.No:
                lines.Add((plan.Why, Theme.Body, Theme.Text));
                break;
            case OwnModState.Ready:
                lines.Add((plan.Files == 1
                    ? Loc.F("Copies one file ({0}) out of “{1}”. That mod stays as it is.", SizeText(plan.Bytes), plan.From)
                    : Loc.F("Copies {0} files ({1}) out of “{2}”. That mod stays as it is.", plan.Files, SizeText(plan.Bytes), plan.From), Theme.Caption, Theme.Text));
                if (plan.Options.Count > 0)
                    lines.Add((Loc.F("Its options come along: {0}.", string.Join(", ", plan.Options)), Theme.Caption, Theme.Text));
                if (plan.Shared > 0)
                    lines.Add((plan.Shared == 1
                        ? Loc.T("One of the files is used by other pieces of the mod too. It is copied, not taken.")
                        : Loc.F("{0} of the files are used by other pieces of the mod too. They are copied, not taken.", plan.Shared), Theme.Caption, Theme.Text));
                foreach (var note in plan.Notes)
                    lines.Add((note, Theme.Small, Theme.Muted));
                break;
        }

        // every line wrapped to the sheet (a long mod name or note shortens at the fourth line)
        var wrapped = lines.Select(l => (Lines: Painter.Wrap(_c, l.Text, inner, l.Style, 4), l.Style, l.Color)).ToList();
        var field   = plan.State is OwnModState.Ready or OwnModState.Making;
        var textH   = wrapped.Sum(l => l.Lines.Count * _c.LineHeight(l.Style)) + Math.Max(0, wrapped.Count - 1) * P(6);
        var h       = P(20) + P(24) + P(12) + (field ? P(36) + P(14) : 0) + textH + (plan.State == OwnModState.Making ? P(6) + P(14) : 0) + P(18) + P(36) + P(20);
        h = Math.Min(h, _c.Bounds.H - P(16));
        var box = new Rect(_c.Bounds.Center.X - w / 2, Math.Max(_c.Bounds.Y + P(8), _c.Bounds.Y + (_c.Bounds.H - h) * 0.4f), w, h);
        _ui.PopupArea(box);
        Stats.Places["own-mod"] = box;
        Painter.Shadow(_c, box, P(12), _u, 18, 8, 0.55f);
        _c.FillRect(box, Theme.Card, P(12));
        _c.StrokeRect(box, Theme.EdgeStrong, P(12), Math.Max(1, _u));
        _ui.PushClip(box);

        var x = box.X + P(20);
        var y = box.Y + P(20);
        Painter.TextIn(_c, new Rect(x, y, inner, P(24)), OwnModTitle(), Theme.Strong, Theme.Text);
        y += P(24) + P(12);

        var typing = false;
        if (field)
        {
            var fbox = new Rect(x, y, inner, P(36));
            Stats.Places["own-mod-name"] = fbox;
            _c.FillRect(fbox, Theme.Rail, P(9));
            var name    = _ownName;
            var editing = plan.State == OwnModState.Ready && _ownSentFrames < 0;
            var changed = _c.TextField("own-mod-name", fbox.Inset(P(12), 0), ref name, Loc.T("Name of the new mod"), Theme.Body, Theme.Text, Theme.Muted, _ownFocus && editing, editing, out var active, selectAll: true);
            if (_ownFocus && editing)
                _ownFocus = false;
            if (active)
                _c.StrokeRect(fbox, Theme.Accent.Fade(0.55f), P(9), Math.Max(1, _u));
            if (changed)
                _ownName = name;
            // ImGui lets go of a field on Enter or Esc in the same frame: "had the keyboard when the frame began" counts too
            typing = active || _ui.In.TextFocused;
            y += P(36) + P(14);
        }

        foreach (var (wl, style, color) in wrapped)
        {
            var lh = _c.LineHeight(style);
            foreach (var line in wl)
            {
                Painter.TextIn(_c, new Rect(x, y, inner, lh), line, style, color);
                y += lh;
            }

            y += P(6);
        }

        if (plan.State == OwnModState.Making)
        {
            // a thin bar: files copied of all
            var bar = new Rect(x, y, inner, P(6));
            Stats.Places["own-mod-progress"] = bar;
            _c.FillRect(bar, Theme.Rail, P(3));
            var share = plan.Files > 0 ? Math.Clamp(plan.Done / (float)plan.Files, 0, 1) : 0;
            if (share > 0)
                _c.FillRect(bar with { W = Math.Max(P(6), bar.W * share) }, Theme.Accent, P(3));
            y += P(6) + P(14);
        }

        y = box.Bottom - P(20) - P(36);
        var esc = _ui.In.KeyPressed(Key.Escape);
        if (plan.State == OwnModState.No)
        {
            var close  = Loc.T("Close");
            var closeW = Painter.ButtonWidth(_c, close, Theme.BodyBold, P(18));
            var cbox   = new Rect(box.Right - P(20) - closeW, y, closeW, P(36));
            Stats.Places["own-mod-close"] = cbox;
            if (Painter.Button(_ui, "own-mod-close", cbox, close, ButtonKind.Outline).Clicked || esc && typing)
                _ui.ClosePopup();
            _ui.PopClip();
            return;
        }

        var make    = Loc.T("Make the mod");
        var cancel  = Loc.T("Cancel");
        var makeW   = Painter.ButtonWidth(_c, make, Theme.BodyBold, P(18));
        var cancelW = Painter.ButtonWidth(_c, cancel, Theme.Body, P(14));
        var makeBox = new Rect(box.Right - P(20) - makeW, y, makeW, P(36));
        var canBox  = new Rect(makeBox.X - P(8) - cancelW, y, cancelW, P(36));
        Stats.Places["own-mod-make"]   = makeBox;
        Stats.Places["own-mod-cancel"] = canBox;
        var ok    = plan.State == OwnModState.Ready && _ownSentFrames < 0 && _ownName.Trim().Length > 0;
        var enter = typing && _ui.In.KeyPressed(Key.Enter);
        if ((Painter.Button(_ui, "own-mod-make", makeBox, make, ButtonKind.Primary, ok).Clicked || enter) && ok)
        {
            if (_ownHand == Hands.Both)
                _s.MakeOwnMod(part, _ownName.Trim());
            else
                _s.MakeOwnMod(part, _ownName.Trim(), _ownHand);
            _ownSentFrames = 0;
        }

        var waiting = plan.State == OwnModState.Making || _ownSentFrames >= 0;
        // Esc is the sheet's Cancel (also while the name field has the keyboard, where the window's Esc does not reach)
        if ((Painter.Button(_ui, "own-mod-cancel", canBox, cancel, ButtonKind.Quiet, !waiting).Clicked || esc && typing) && !waiting)
            _ui.ClosePopup();
        _ui.PopClip();
    }
}
