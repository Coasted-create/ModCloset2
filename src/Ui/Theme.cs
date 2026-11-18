using ModCloset2.Ui.Canvas;

namespace ModCloset2.Ui;

/// <summary> The closet's colours and type, from docs/DESIGN.md. Sizes elsewhere are mockup pixels times U. </summary>
public static class Theme
{
    // ---- colours ----
    public static readonly Colr Window     = Colr.Hex(0x17111C);
    public static readonly Colr Rail       = Colr.Hex(0x1C1522);
    public static readonly Colr Line       = Colr.Hex(0x2C2135);
    public static readonly Colr Card       = Colr.Hex(0x261D2D);
    public static readonly Colr Raised     = Colr.Hex(0x2A1F31);
    public static readonly Colr Picked     = Colr.Hex(0x33273D);
    public static readonly Colr Edge       = Colr.Hex(0x3D2F49);
    public static readonly Colr EdgeStrong = Colr.Hex(0x4A3A57);
    public static readonly Colr Text       = Colr.Hex(0xF4ECF2);
    public static readonly Colr Muted      = Colr.Hex(0xA394AB);
    public static readonly Colr Faint      = Colr.Hex(0x6F6277);
    public static readonly Colr Accent     = Colr.Hex(0xF0A6BE);
    public static readonly Colr Gold       = Colr.Hex(0xEBC98E);
    public static readonly Colr Good       = Colr.Hex(0x92DDB6);
    public static readonly Colr OnAccent   = Colr.Hex(0x16101B);

    /// <summary> The option chips' fill on the "Wearing" strip (mockup #2E2336). </summary>
    public static readonly Colr Chip = Colr.Hex(0x2E2336);

    /// <summary> Slim face: the shelf icon buttons (mockup #1F1826). </summary>
    public static readonly Colr IconTile = Colr.Hex(0x1F1826);

    /// <summary> Move: the shelf under the pointer, and the place a lifted card left (mockup #3A2634, #1B1420). </summary>
    public static readonly Colr DropFill = Colr.Hex(0x3A2634);
    public static readonly Colr GoneFill = Colr.Hex(0x1B1420);

    /// <summary> "Lightens slightly" under the mouse: white at a few percent. </summary>
    public static readonly Colr HoverWash = Colr.Hex(0xFFFFFF, 0.06f);

    /// <summary> Marks on a picture ("6 versions"): dark see-through. </summary>
    public static readonly Colr NoteFill = Colr.Hex(0x16101B, 0.82f);

    /// <summary> The placeholder icon on a picture tile. </summary>
    public static readonly Colr TileIcon = Colr.Hex(0xF4ECF2, 0.28f);

    /// <summary> Picture placeholders, cycled by a stable hash of the thing shown. </summary>
    public static readonly Colr[] Tints =
    {
        Colr.Hex(0x3A2B45), Colr.Hex(0x2E3348), Colr.Hex(0x43303B), Colr.Hex(0x2C3A3E), Colr.Hex(0x3F3A2E), Colr.Hex(0x34304A),
    };

    /// <summary> A tint that stays the same for the same text, whatever order things come in. </summary>
    public static Colr Tint(string key)
    {
        var h = 2166136261u;
        foreach (var ch in key)
            h = (h ^ ch) * 16777619u;
        return Tints[(int)(h % (uint)Tints.Length)];
    }

    // ---- type (sizes in mockup pixels) ----
    public static readonly TextStyle Body       = new(FontFace.Body, 14);
    public static readonly TextStyle BodyMedium = new(FontFace.Body, 14, Weight.Medium);
    public static readonly TextStyle BodyBold   = new(FontFace.Body, 14, Weight.Bold);
    public static readonly TextStyle Strong     = new(FontFace.Body, 15, Weight.Bold);
    public static readonly TextStyle Caption    = new(FontFace.Body, 13);
    public static readonly TextStyle Small      = new(FontFace.Body, 12);
    public static readonly TextStyle SmallBold  = new(FontFace.Body, 12, Weight.Bold);
    public static readonly TextStyle Label      = new(FontFace.Body, 11, Weight.Regular, 0.08f, true);
    public static readonly TextStyle Mark       = new(FontFace.Body, 11, Weight.Bold);
    public static readonly TextStyle MarkPlain  = new(FontFace.Body, 11);
    public static readonly TextStyle Title      = new(FontFace.Display, 26, Weight.Bold, 0.05f, true);
    public static readonly TextStyle Heading    = new(FontFace.Display, 34, Weight.Bold, 0.02f);
    public static readonly TextStyle SlimHeading = new(FontFace.Display, 28, Weight.Bold, 0.02f);
}
