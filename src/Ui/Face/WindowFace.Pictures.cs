using ModCloset2.Core;
using ModCloset2.Ui.Canvas;
using ModCloset2.Ui.Widgets;

namespace ModCloset2.Ui.Face;

/// <summary>
/// Pictures on cards and tiles: asking the closet for a photo where one is missing, and letting a picture that arrives
/// while its card is on screen fade in. A picture that was there when the card came into view is simply drawn.
/// </summary>
public sealed partial class WindowFace
{
    /// <summary> How long a picture that arrives takes to fade in, in seconds. </summary>
    public const double FadeTime = 0.15;

    private sealed class PictureSlot
    {
        public string? Path;
        public string? Under;
        public bool Fading;
        public double StartedAt = double.NaN;
        public long Frame;
    }

    private readonly Dictionary<string, PictureSlot> _slots = new(StringComparer.Ordinal);
    private long _pictureFrame;

    /// <summary>
    /// A picture tile (card, strip, row): the picture when there is one and it has arrived, else the tint with the
    /// shelf's icon. <paramref name="slot"/> names the place by what it shows (the part's key in it), so another thing
    /// in the same spot is another place: no fade, its icon until its picture is in. <paramref name="want"/> is the
    /// part to ask a photo for when its picture is not a photo yet (none, or the mod's own; the closet ignores asks for
    /// which no photo is due).
    /// </summary>
    private void PictureTile(string slot, PartKey? want, Rect r, string? picture, string icon, string tintKey, float radius, Corners corners, float iconSize, float iconStroke, Colr? iconColor = null)
    {
        if (!Pictures.IsPhoto(picture) && want is { } part)
        {
            _s.WantPicture(part);
            Stats.PicturesWanted.Add(part);
        }

        // what this place showed before: a new picture while it stays on screen fades in, over what was there
        if (!_slots.TryGetValue(slot, out var s) || s.Frame < _pictureFrame - 1)
        {
            s = new PictureSlot { Path = picture };
            _slots[slot] = s;
        }
        else if (!string.Equals(s.Path, picture, StringComparison.Ordinal))
        {
            s.Under     = s.Fading ? s.Under : s.Path;
            s.Path      = picture;
            s.Fading    = !string.IsNullOrEmpty(picture);
            s.StartedAt = double.NaN;
        }

        s.Frame = _pictureFrame;
        if (!s.Fading || string.IsNullOrEmpty(picture))
        {
            Painter.Tile(_c, r, picture, icon, tintKey, radius, corners, iconSize, iconStroke, iconColor);
            return;
        }

        // fading: what was there underneath (the picture before, else the icon), the new picture over it
        var now   = _ui.In.Time;
        var alpha = double.IsNaN(s.StartedAt) ? 0.001f : (float)Math.Clamp((now - s.StartedAt) / FadeTime, 0, 1);
        Painter.Tile(_c, r, s.Under, icon, tintKey, radius, corners, iconSize, iconStroke, iconColor);
        var arrived = _c.Picture(picture, r, radius, corners, alpha);
        if (arrived && double.IsNaN(s.StartedAt))
            s.StartedAt = now;
        if (alpha >= 1)
            s.Fading = false;
    }

    /// <summary> Lets go of places not drawn for a while (called once per frame). </summary>
    private void ForgetPictureSlots()
    {
        if (_slots.Count < 512)
            return;
        foreach (var key in _slots.Where(kv => kv.Value.Frame < _pictureFrame - 1).Select(kv => kv.Key).ToList())
            _slots.Remove(key);
    }
}
