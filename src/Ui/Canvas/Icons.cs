namespace ModCloset2.Ui.Canvas;

/// <summary>
/// The window's icons as stroked paths in a 24 × 24 box. The shelf icons are the ones in the mockups; both backends
/// draw these same paths, so the pictures show the icons the game shows. Unknown keys get a neutral tag.
/// </summary>
public static class Icons
{
    // shelves (keys are the shelf ids of src/Core/Pieces.cs)
    public const string Outfits   = "outfits";
    public const string Head      = "head";
    public const string Top       = "top";
    public const string Gloves    = "gloves";
    public const string Bottoms   = "bottoms";
    public const string Legwear   = "legwear";
    public const string Shoes     = "shoes";
    public const string Earrings  = "earrings";
    public const string Necklace  = "necklace";
    public const string Bracelets = "bracelets";
    public const string Rings     = "rings";
    public const string RingsLeft = "ringsleft";
    public const string Hair      = "hair";
    public const string Face      = "face";
    public const string Ears      = "ears";
    public const string Tail      = "tail";
    public const string Skin      = "skin";
    public const string Bodies    = "bodies";
    public const string Makeup    = "makeup";
    public const string Nails     = "nails";
    public const string ToSort    = "tosort";
    public const string Other     = "other";
    public const string Hidden    = "hidden";

    /// <summary> Shelves the user made. </summary>
    public const string Mine = "mine";

    // second round
    public const string Looks   = "looks";
    public const string Palette = "ui.palette";
    public const string Pin     = "ui.pin";
    public const string Link    = "ui.link";
    public const string Dice    = "ui.dice";

    // the window
    public const string Search      = "ui.search";
    public const string Undo        = "ui.undo";
    public const string Settings    = "ui.settings";
    public const string ChevronR    = "ui.chevron-right";
    public const string ChevronL    = "ui.chevron-left";
    public const string ChevronD    = "ui.chevron-down";
    public const string Nothing     = "ui.nothing";
    public const string FaceWide    = "ui.face-wide";
    public const string FaceSlim    = "ui.face-slim";
    public const string FaceOnYou   = "ui.face-onyou";
    public const string Close       = "ui.close";
    public const string Check       = "ui.check";
    public const string Info        = "ui.info";
    public const string Plus        = "ui.plus";
    public const string Eye         = "ui.eye";
    public const string Move        = "ui.move";
    public const string Slots       = "ui.slots";
    public const string Copy        = "ui.copy";
    public const string SaveLook    = "ui.save-look";
    public const string Question    = "ui.question";
    public const string OpenOutside = "ui.open";

    private static readonly Dictionary<string, string> Paths = new(StringComparer.Ordinal)
    {
        [Outfits]   = "M12 8V6.5a2 2 0 1 0-2-2M12 8l9 6.5a1 1 0 0 1-.6 1.8H3.6a1 1 0 0 1-.6-1.8L12 8z",
        [Head]      = "M6 15c0-5 2.5-8 6-8s6 3 6 8M2.5 15.5c3 2 16 2 19 0",
        [Top]       = "M8.5 4L4 7l2 3.5 2-1V20h8V9.5l2 1L20 7l-4.5-3c-.6 1.6-2 2.4-3.5 2.4S9.1 5.6 8.5 4z",
        [Gloves]    = "M8 20v-7l-2.2-2.6a1.4 1.4 0 0 1 2.1-1.8L9.5 10V5.5a1.5 1.5 0 0 1 3 0V5a1.5 1.5 0 0 1 3 0v1.5a1.5 1.5 0 0 1 3 0V14c0 2.5-1 4.5-2 6H8z",
        [Bottoms]   = "M8 5h8l4 14H4L8 5zM7.2 8h9.6",
        [Legwear]   = "M9 4h6v8l4 4a2.5 2.5 0 0 1-3.5 3.5L9 14V4z",
        [Shoes]     = "M9 4v9l-5 3v3h16v-3c-3.5 0-5.5-2-5.5-5V4H9z",
        [Earrings]  = "M12 3v5M12 8l4 6a4.5 4.5 0 1 1-8 0l4-6z",
        [Necklace]  = "M5 4c0 6 3 10 7 10s7-4 7-10M12 14l2 3-2 3-2-3 2-3z",
        [Bracelets] = "M4 12a8 5 0 1 0 16 0a8 5 0 1 0-16 0M7.5 12a4.5 2.2 0 1 0 9 0a4.5 2.2 0 1 0-9 0",
        [Rings]     = "M6.5 15a5.5 5.5 0 1 0 11 0a5.5 5.5 0 1 0-11 0M9.5 7l2.5-3 2.5 3-2.5 2.5L9.5 7z",
        [RingsLeft] = "M6.5 15a5.5 5.5 0 1 0 11 0a5.5 5.5 0 1 0-11 0M9.5 7l2.5-3 2.5 3-2.5 2.5L9.5 7z",
        [Hair]      = "M5 20c-2-9 1.5-16 7-16s9 7 7 16M9 20c-1-5 0-9 3-10 3 1 4 5 3 10",
        [Face]      = "M4 12a8 8 0 1 0 16 0a8 8 0 1 0-16 0M9 10.5v1M15 10.5v1M9.5 15c1.5 1.2 3.5 1.2 5 0",
        [Ears]      = "M3.5 19L6.5 5l5 8.5M12.5 13.5l5-8.5 3 14",
        [Tail]      = "M6 20c0-8 9.5-6.5 9-13-.2-2.6 2.8-3.6 4-1.8",
        [Skin]      = "M12 3c-3.5 4.5-6 7.8-6 11a6 6 0 0 0 12 0c0-3.2-2.5-6.5-6-11z",
        // a figure: head, shoulders, waist, hips and legs (a body, not a garment and not a drop)
        // a lipstick: the slanted tip, the tube and its cap (not a face)
        [Makeup]    = "M10 3.5l4 2.5v4.5h-4V3.5zM9 10.5h6v3H9zM8.5 13.5h7V21h-7z",
        [Bodies]    = "M10.2 4.2a1.8 1.8 0 1 0 3.6 0a1.8 1.8 0 1 0-3.6 0M8.5 8h7c.8 0 1.3.7 1.1 1.4L15 13.5c-.3 1-.3 1.8 0 2.8l.6 4.7M8.5 8c-.8 0-1.3.7-1.1 1.4L9 13.5c.3 1 .3 1.8 0 2.8l-.6 4.7M12 16.5V21",
        [Nails]     = "M9 21v-9a3 3 0 0 1 6 0v9M9.8 11.5c0-4 .8-8 2.2-8s2.2 4 2.2 8",
        [ToSort]    = "M4 13l2.5-8h11L20 13v6H4v-6zM4 13h5a3 3 0 0 0 6 0h5",
        [Other]     = "M4 8l8-4 8 4v8l-8 4-8-4V8zM4 8l8 4 8-4M12 12v8",
        [Hidden]    = "M3 12c2.5-4 5.5-6 9-6s6.5 2 9 6c-2.5 4-5.5 6-9 6s-6.5-2-9-6zM9.5 12a2.5 2.5 0 1 0 5 0a2.5 2.5 0 1 0-5 0M4 20L20 4",
        [Mine]      = "M6.5 3.5h11v17L12 16.5l-5.5 4V3.5z",
        [Looks]     = "M12 3.5l2.6 5.3 5.9.9-4.2 4.1 1 5.8L12 16.9l-5.3 2.7 1-5.8-4.2-4.1 5.9-.9L12 3.5z",
        [Palette]   = "M12 3.5a8.5 8.5 0 1 0 0 17c1 0 1.6-.7 1.6-1.5 0-.5-.2-.8-.5-1.1-.3-.3-.5-.7-.5-1.1 0-.9.7-1.6 1.6-1.6H16a4.5 4.5 0 0 0 4.5-4.5c0-4-3.8-7.2-8.5-7.2zM7.5 12h.01M9.5 8h.01M14.5 8h.01",
        [Pin]       = "M12 17v4M8 4h8l-1 6 3 3H6l3-3-1-6z",
        [Link]      = "M10 14a4 4 0 0 0 5.7 0l3-3a4 4 0 0 0-5.7-5.7l-1 1M14 10a4 4 0 0 0-5.7 0l-3 3a4 4 0 0 0 5.7 5.7l1-1",
        [Dice]      = "M7 4h10a3 3 0 0 1 3 3v10a3 3 0 0 1-3 3H7a3 3 0 0 1-3-3V7a3 3 0 0 1 3-3zM9 9h.01M15 9h.01M9 15h.01M15 15h.01M12 12h.01",

        [Search]      = "M4.5 11a6.5 6.5 0 1 0 13 0a6.5 6.5 0 1 0-13 0M16 16l4.5 4.5",
        [Undo]        = "M9 14L4 9l5-5M4 9h10a6 6 0 0 1 0 12h-3",
        [Settings]    = "M4 7h8M18 7h2M4 17h2M12 17h8M12.5 7a2.5 2.5 0 1 0 5 0a2.5 2.5 0 1 0-5 0M6.5 17a2.5 2.5 0 1 0 5 0a2.5 2.5 0 1 0-5 0",
        [ChevronR]    = "M9 6l6 6-6 6",
        [ChevronL]    = "M15 6l-6 6 6 6",
        [Slots]       = "M5 4.5h14v6H5zM5 13.5h14v6H5z",
        [SaveLook]    = "M10.0 3.0L11.9 7.9L17.1 8.2L13.0 11.5L14.4 16.6L10.0 13.7L5.6 16.6L7.0 11.5L2.9 8.2L8.1 7.9zM18.5 14.5v6M15.5 17.5h6",
        [Copy]        = "M9 8.5h10v11.5H9zM15 8.5V4H5v11.5h4",
        [ChevronD]    = "M6 9l6 6 6-6",
        [Nothing]     = "M4 12a8 8 0 1 0 16 0a8 8 0 1 0-16 0M6.5 17.5l11-11",
        [FaceWide]    = "M3 5.5h18v13H3zM9 5.5v13",
        [FaceSlim]    = "M7.5 3h9v18h-9zM7.5 8h9",
        [FaceOnYou]   = "M6 12a6 6 0 1 0 12 0a6 6 0 1 0-12 0M12 2.5V6M12 18v3.5M2.5 12H6M18 12h3.5M11 12a1 1 0 1 0 2 0a1 1 0 1 0-2 0",
        [Close]       = "M6 6l12 12M18 6L6 18",
        [Check]       = "M5 12.5l4.5 4.5L19 7",
        [Info]        = "M3.5 12a8.5 8.5 0 1 0 17 0a8.5 8.5 0 1 0-17 0M12 11v5.5M12 7.5v.5",
        [Plus]        = "M12 5v14M5 12h14",
        [Eye]         = "M3 12c2.5-4 5.5-6 9-6s6.5 2 9 6c-2.5 4-5.5 6-9 6s-6.5-2-9-6zM9.5 12a2.5 2.5 0 1 0 5 0a2.5 2.5 0 1 0-5 0",
        [Move]        = "M5 12h12M13 7l5 5-5 5",
        [Question]    = "M3.5 12a8.5 8.5 0 1 0 17 0a8.5 8.5 0 1 0-17 0M9.5 9.5a2.5 2.5 0 1 1 3.5 2.3c-.6.3-1 .9-1 1.6v.6M12 17v.5",
        [OpenOutside] = "M13 4h7v7M20 4l-9 9M18 14v5a1 1 0 0 1-1 1H5a1 1 0 0 1-1-1V7a1 1 0 0 1 1-1h5",
    };

    private const string Fallback = "M4 4h9l7 7-9 9-7-7V4zM8.5 8.5v.5";

    private static readonly Dictionary<string, PathShape> Shapes = new(StringComparer.Ordinal);

    /// <summary> The shape of an icon (cached; unknown keys give the neutral tag). </summary>
    public static PathShape Get(string key)
    {
        lock (Shapes)
        {
            if (Shapes.TryGetValue(key, out var shape))
                return shape;
            shape       = new PathShape(Paths.TryGetValue(key, out var d) ? d : key.StartsWith("user:", StringComparison.Ordinal) ? Paths[Mine] : Fallback);
            Shapes[key] = shape;
            return shape;
        }
    }

    public static bool Has(string key)
        => Paths.ContainsKey(key);

    public static IEnumerable<string> Keys
        => Paths.Keys;
}
