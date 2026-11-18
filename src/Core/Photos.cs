namespace ModCloset2.Core;

// Photos of pieces (docs/briefs/11-photos.md and 12-photos-in-the-closet.md). The closet draws a picture of every piece
// itself, from the mod's own files, and shows it on the piece's card. No character is touched for it.
//
// Two contracts live here: the maker (IPhotoMaker: pure drawing, in src/Photo), which the session is handed, and what
// a face sees (PhotoStatus, and IClosetSession.WantPicture / Photos / SetPhotos in Session.cs).

/// <summary> One part of one mod, to be drawn. </summary>
public sealed class PhotoAsk
{
    /// <summary> Full path of the mod's folder. </summary>
    public string ModFolder { get; init; } = string.Empty;
    public Socket Socket { get; init; }

    /// <summary>
    /// A model to draw, for a caller that already knows it (the tool, the tests): its game path and the option it comes
    /// from ("" when it is always on). The closet leaves both empty, the library keeps no model path: the maker then finds
    /// the part's model in the mod's definition itself, without reading or measuring anything else of the mod.
    /// </summary>
    public string ModelGamePath { get; init; } = string.Empty;
    public string Option { get; init; } = string.Empty;

    /// <summary>
    /// The player's model race code (801 for a Miqo'te woman), 0 when nobody is logged in. The maker draws the mod's model
    /// for this race when it has one, else the nearest that fits the player, else the race the mod has most of.
    /// </summary>
    public int Race { get; init; }

    /// <summary> Width in pixels. The picture is 4:5, the shape of a card's picture. </summary>
    public int Width { get; init; } = 384;
}

/// <summary> What came of it. </summary>
public sealed class PhotoShot
{
    /// <summary> The picture as a file, ready to be written as it is; null when there is no picture. </summary>
    public byte[]? File { get; init; }

    /// <summary> ".jpg" or ".png", for the file's name. </summary>
    public string Extension { get; init; } = ".png";

    /// <summary>
    /// The piece shows too little to say anything (an effect the maker cannot draw, a few pixels). No picture is better
    /// than an empty one: the card keeps its icon.
    /// </summary>
    public bool Nothing { get; init; }

    /// <summary>
    /// Why there is no picture ("" when there is one or the piece simply shows nothing): one of a small set of fixed
    /// sentences that names no file, no mod and no path ("the model has more triangles than a picture may take"). It is
    /// counted in the log and kept with the photo index, so it must be safe to show anywhere.
    /// </summary>
    public string Problem { get; init; } = string.Empty;

    /// <summary> More about the problem, for the tool and the tests: may name files. Never logged, never kept. </summary>
    public string Detail { get; init; } = string.Empty;
}

/// <summary>
/// Draws pictures of pieces. Pure: no game, no Penumbra, it only reads the mod's files. A call can take a few hundred
/// milliseconds and a few megabytes read: never on the frame. One call at a time; it keeps its working memory between
/// calls and lets it go in <see cref="Rest"/>. Never throws.
/// </summary>
public interface IPhotoMaker
{
    /// <summary> Goes up whenever pictures would come out different. Pictures taken under another number are taken again. </summary>
    int Version { get; }
    PhotoShot Take(PhotoAsk ask, CancellationToken stop);

    /// <summary> Nothing more to draw for now: lets the working memory go. </summary>
    void Rest();
}

/// <summary> How far the closet is with its photos, for the settings sheet. </summary>
public sealed class PhotoStatus
{
    /// <summary> The closet takes photos (the user's switch; on unless they turned it off). </summary>
    public bool On { get; init; }

    /// <summary> Pieces that have a photo, and pieces that could have one. </summary>
    public int Taken { get; init; }
    public int Total { get; init; }

    /// <summary> It is taking photos right now. </summary>
    public bool Working { get; init; }
}

public static class Pictures
{
    /// <summary>
    /// A picture the closet took itself carries this in its file name ("….photo.jpg"). The piece is in the middle of
    /// such a picture, so a face that has to crop one keeps the middle; other pictures (a mod's own, the old closet's)
    /// keep their top, where the head is.
    /// </summary>
    public const string PhotoMark = ".photo.";

    public static bool IsPhoto(string? path)
        => path is not null && path.Contains(PhotoMark, StringComparison.OrdinalIgnoreCase);
}
