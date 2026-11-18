using System.Text.RegularExpressions;

namespace ModCloset2.Photo;

// Pure code (no game, no Dalamud): linked into tests/PhotoTests and tools/photo.

/// <summary>
/// Every <see cref="Core.PhotoShot.Problem"/> the maker can answer. The closet counts them in its log and keeps them with
/// the photo index, so each is a fixed sentence that names no file, no mod and no path; one kind of trouble per sentence,
/// so a count of them says what went wrong across a library. File names, an exception's message and the reader's
/// numbers go to <see cref="Core.PhotoShot.Detail"/>, which is never logged or kept.
/// </summary>
public static partial class PhotoProblems
{
    public const string Stopped = "stopped";
    public const string NotTaken = "this kind of part is not photographed";
    public const string NothingToDraw = "this kind of part has nothing to draw";
    public const string NoModelOrTexture = "no model or picture for this part, in the mod or the game";
    public const string ModNotOpened = "the mod cannot be opened";
    public const string NoModel = "no model for this slot in the mod";
    public const string ModelNotInMod = "the model asked for is not in the mod";
    public const string ModelMissing = "the model file is missing";
    public const string ModelNotOpened = "the model file could not be opened";

    // the budget (PhotoBudget), one sentence for each ceiling
    public const string ModelTooBig = "the model file is larger than a picture may take";
    public const string TooManyTriangles = "the model has more triangles or vertices than a picture may take";
    public const string TooManyMaterials = "the model has more materials than a picture may take";
    public const string TooManyTexels = "the piece's textures are larger than a picture may take";
    public const string TooMuchDrawing = "the model would take far more drawing than any piece needs";

    /// <summary> The model reader's own reasons; the problem is "the model could not be read: " and one of them. </summary>
    public static class ModelReason
    {
        public const string Version = "unknown model version";
        public const string Lods = "bad level-of-detail count";
        public const string CutOff = "the data ends early";
        public const string Declarations = "vertex declarations of the wrong size";
        public const string MaterialCounts = "the material counts disagree";
        public const string Vertices = "vertices outside the file";
        public const string Triangles = "triangles outside the file";
        public const string Damaged = "damaged";

        public static readonly IReadOnlyList<string> All = [Version, Lods, CutOff, Declarations, MaterialCounts, Vertices, Triangles, Damaged];
    }

    public const string ModelUnreadablePrefix = "the model could not be read: ";

    public static string ModelUnreadable(string reason)
        => ModelUnreadablePrefix + (ModelReason.All.Contains(reason) ? reason : ModelReason.Damaged);

    /// <summary> Something nobody foresaw: the exception's type only (its message may name a file). </summary>
    public static string Exception(Exception ex)
        => $"could not draw ({TypeName().Replace(ex.GetType().Name, string.Empty)})";

    [GeneratedRegex(@"[^A-Za-z0-9]")]
    private static partial Regex TypeName();

    [GeneratedRegex(@"^could not draw \([A-Za-z0-9]*\)$")]
    private static partial Regex ExceptionSentence();

    /// <summary> Every fixed sentence (the exception's one aside, which carries a type name). </summary>
    public static readonly IReadOnlyList<string> All =
    [
        Stopped, NotTaken, NothingToDraw, NoModelOrTexture, ModNotOpened, NoModel, ModelNotInMod, ModelMissing, ModelNotOpened,
        ModelTooBig, TooManyTriangles, TooManyMaterials, TooManyTexels, TooMuchDrawing,
        .. ModelReason.All.Select(r => ModelUnreadablePrefix + r),
    ];

    /// <summary> True for "" and for a sentence of this list (what a test checks every answered problem against). </summary>
    public static bool IsKnown(string problem)
        => problem.Length == 0 || All.Contains(problem) || ExceptionSentence().IsMatch(problem);
}

/// <summary> A model the reader refuses: one of <see cref="PhotoProblems.ModelReason"/>, and what more it knows. </summary>
public sealed class ModelTrouble(string reason, string detail = "") : Exception(reason)
{
    public string Reason { get; } = reason;
    public string Detail { get; } = detail;

    /// <summary> True for the budget's ceiling (too many triangles or vertices), not a damaged file. </summary>
    public bool OverBudget { get; init; }
}
