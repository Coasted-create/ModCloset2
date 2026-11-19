using System.Text.Json;
using System.Text.Json.Serialization;
using ModCloset2.Core;

namespace ModCloset2.Looks;

// The looks as they are kept (one JSON text through ILooksStore). Plain data, no logic beyond copying and reading.

/// <summary> What is needed to find a piece's mod again after it was renamed, moved or updated. </summary>
public sealed class ModPrint
{
    /// <summary> The mod's name when the piece was last seen. </summary>
    [JsonPropertyName("name")]
    public string Name { get; set; } = string.Empty;

    [JsonPropertyName("author")]
    public string Author { get; set; } = string.Empty;

    /// <summary> A few file names of the part (last path segment, lower case), at most <see cref="LookRules.PrintFiles"/>. </summary>
    [JsonPropertyName("files")]
    public List<string> Files { get; set; } = new();

    /// <summary> Where its model sits (the detection's signature, to 1/1000), for "the closest parts on that shelf". Empty: unknown. </summary>
    [JsonPropertyName("sig")]
    [JsonConverter(typeof(SignatureConverter))]
    public float[] Signature { get; set; } = Array.Empty<float>();

    public ModPrint Copy()
        => new() { Name = Name, Author = Author, Files = Files.ToList(), Signature = Signature.ToArray() };
}

/// <summary> One piece of a look: a part (mod + socket) with its options and version. A look holds at most one per socket. </summary>
public sealed class LookPiece
{
    /// <summary> The mod's Penumbra directory. </summary>
    [JsonPropertyName("mod")]
    public string Mod { get; set; } = string.Empty;

    [JsonPropertyName("socket")]
    public Socket Socket { get; set; }

    /// <summary>
    /// The shelf it was on when it was last seen (the library's shelf now wins while the part is there; this one counts
    /// when its mod is gone). Says whether it is outfit or body, and how it is shown.
    /// </summary>
    [JsonPropertyName("shelf")]
    public string Shelf { get; set; } = string.Empty;

    /// <summary> The options of the groups shown for the part (never body or race groups: those stay the user's). </summary>
    [JsonPropertyName("options")]
    public Dictionary<string, List<string>> Options { get; set; } = new();

    /// <summary> Which of its versions, -1: whichever the closet picks. </summary>
    [JsonPropertyName("version")]
    public int Version { get; set; } = -1;

    [JsonPropertyName("print")]
    public ModPrint? Print { get; set; }

    [JsonIgnore]
    public PartKey Key
        => new(Mod, Socket);

    public LookPiece Copy()
        => new()
        {
            Mod     = Mod,
            Socket  = Socket,
            Shelf   = Shelf,
            Options = Options.ToDictionary(kv => kv.Key, kv => kv.Value.ToList()),
            Version = Version,
            Print   = Print?.Copy(),
        };
}

/// <summary> One set of colours for a look's pieces. </summary>
public sealed class Colourway
{
    /// <summary> The colour engine's snapshot of the look's parts (opaque; see <see cref="ColourKey"/>). </summary>
    [JsonPropertyName("colours")]
    public string Colours { get; set; } = string.Empty;

    /// <summary> Its main colour as 0xRRGGBB (the dot under the card). </summary>
    [JsonPropertyName("main")]
    public uint Main { get; set; }

    public Colourway Copy()
        => new() { Colours = Colours, Main = Main };
}

/// <summary> A look (pinned) or an outfit the closet remembered on its own (lately). </summary>
public sealed class LookData
{
    [JsonPropertyName("id")]
    public Guid Id { get; set; } = Guid.NewGuid();

    /// <summary> The user's own name; "" while the name makes itself from the main piece. </summary>
    [JsonPropertyName("name")]
    public string Name { get; set; } = string.Empty;

    [JsonPropertyName("pinned")]
    public bool Pinned { get; set; }

    [JsonPropertyName("created")]
    public DateTime CreatedUtc { get; set; }

    [JsonPropertyName("worn")]
    public DateTime LastWornUtc { get; set; }

    /// <summary> How often it was worn, each wear weighing half after <see cref="LookRules.WearHalfLifeDays"/> days, as of <see cref="LastWornUtc"/>. </summary>
    [JsonPropertyName("wears")]
    public double Wears { get; set; }

    /// <summary>
    /// The body shelves the look wears (hair, face, makeup, ears, tails, bodies, skin, nails), chosen one by one when it
    /// was saved. Its body pieces on these shelves are put on with it; the other body shelves are left as they are (their
    /// pieces are still kept, for "Also change hair and body"). Empty: a look of clothes only.
    /// </summary>
    [JsonPropertyName("body")]
    public List<string> Body { get; set; } = new();

    /// <summary> It wears some of the body (a card's "with hair" and the switch "Also change hair and body"). </summary>
    [JsonIgnore]
    public bool WithBody
        => Body.Count > 0;

    /// <summary>
    /// The one switch looks had before the body shelves were chosen one by one (builds up to 0.6). Read once when a file
    /// is loaded: on means every body shelf it holds a piece on. Written too, so an older build still reads the look.
    /// </summary>
    [JsonPropertyName("withBody")]
    public bool LegacyWithBody { get; set; }

    /// <summary>
    /// The pieces, at most one per socket: everything that was on (clothes, jewellery, the user's own shelves, and the
    /// body as it was; the body pieces are worn only with <see cref="WithBody"/>).
    /// </summary>
    [JsonPropertyName("pieces")]
    public List<LookPiece> Pieces { get; set; } = new();

    /// <summary>
    /// The gear sockets that were empty when the look was made ("nothing here"): wearing the look takes off what is on
    /// there. Old looks know none (they leave every other socket alone).
    /// </summary>
    [JsonPropertyName("empty")]
    public List<Socket> Empty { get; set; } = new();

    [JsonPropertyName("colourways")]
    public List<Colourway> Colourways { get; set; } = new();

    /// <summary> The colourway worn last. </summary>
    [JsonPropertyName("colourway")]
    public int Colourway { get; set; }

    /// <summary>
    /// The character's colours the look sets (key → 0xRRGGBB: "skin", "hair", "highlights", "eyeLeft", "eyeRight", "lips",
    /// "facePaint", "tattoo"). A key that is not here is left as it is (every colour of an older look).
    /// </summary>
    [JsonPropertyName("mine")]
    public Dictionary<string, uint> MyColours { get; set; } = new();

    /// <summary> What the look shows or hides (key → on: "weapon" drawn, "hat" shown, "visor" down); absent: left alone. </summary>
    [JsonPropertyName("showing")]
    public Dictionary<string, bool> Showing { get; set; } = new();

    /// <summary> The gearset (its number, 1-based) that puts the look on by itself, or null. </summary>
    [JsonPropertyName("gearset")]
    public string? Gearset { get; set; }

    /// <summary> The job (its abbreviation, "DNC") that puts the look on by itself, or null. </summary>
    [JsonPropertyName("job")]
    public string? Job { get; set; }

    /// <summary> A Glamourer design named after the look is kept up to date. </summary>
    [JsonPropertyName("design")]
    public bool GlamourerDesign { get; set; }

    /// <summary> The Glamourer design made for it last (replaced when it is made again), or null. </summary>
    [JsonPropertyName("designId")]
    public Guid? DesignId { get; set; }

    /// <summary> When the user removed it (only in <see cref="LooksFile.Removed"/>; null for every look in use). </summary>
    [JsonPropertyName("removedAt")]
    public DateTime? RemovedUtc { get; set; }

    [JsonIgnore]
    public bool OwnName
        => Name.Length > 0;

    public LookData Copy()
        => new()
        {
            Id          = Id,
            Name        = Name,
            Pinned      = Pinned,
            CreatedUtc  = CreatedUtc,
            LastWornUtc = LastWornUtc,
            Wears       = Wears,
            Body        = Body.ToList(),
            LegacyWithBody = LegacyWithBody,
            Pieces      = Pieces.Select(p => p.Copy()).ToList(),
            Empty       = Empty.ToList(),
            Colourways  = Colourways.Select(c => c.Copy()).ToList(),
            Colourway   = Colourway,
            MyColours   = new Dictionary<string, uint>(MyColours),
            Showing     = new Dictionary<string, bool>(Showing),
            Gearset     = Gearset,
            Job         = Job,
            GlamourerDesign = GlamourerDesign,
            DesignId    = DesignId,
            RemovedUtc  = RemovedUtc,
        };
}

/// <summary>
/// A signature as a short text (base64 of whole thousandths, two bytes each) instead of 25 numbers: a thousand looks
/// write several times faster. Reads plain number arrays too.
/// </summary>
public sealed class SignatureConverter : JsonConverter<float[]>
{
    public override float[] Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        if (reader.TokenType == JsonTokenType.String)
        {
            try
            {
                var bytes = Convert.FromBase64String(reader.GetString() ?? string.Empty);
                var result = new float[bytes.Length / 2];
                for (var i = 0; i < result.Length; ++i)
                    result[i] = (short)(bytes[2 * i] | bytes[2 * i + 1] << 8) / 1000f;
                return result;
            }
            catch (FormatException)
            {
                return Array.Empty<float>();
            }
        }

        if (reader.TokenType == JsonTokenType.StartArray)
        {
            var list = new List<float>();
            while (reader.Read() && reader.TokenType != JsonTokenType.EndArray)
                list.Add(reader.TokenType == JsonTokenType.Number ? reader.GetSingle() : 0);
            return list.ToArray();
        }

        reader.Skip();
        return Array.Empty<float>();
    }

    public override void Write(Utf8JsonWriter writer, float[] value, JsonSerializerOptions options)
    {
        var bytes = new byte[value.Length * 2];
        for (var i = 0; i < value.Length; ++i)
        {
            var v = (short)Math.Clamp(MathF.Round(value[i] * 1000f), short.MinValue, short.MaxValue);
            bytes[2 * i]     = (byte)v;
            bytes[2 * i + 1] = (byte)(v >> 8);
        }

        writer.WriteStringValue(Convert.ToBase64String(bytes));
    }
}

/// <summary> Everything the looks keep. </summary>
public sealed class LooksFile
{
    /// <summary>
    /// 3: a look also keeps the character's colours, what it shows, and how it is worn by itself (gearset, job, Glamourer
    /// design); a file of version 2 reads with none of those, so its looks do what they did. 2: pieces by socket (1, one
    /// part per shelf, never shipped).
    /// </summary>
    public const int CurrentVersion = 3;

    [JsonPropertyName("v")]
    public int Version { get; set; } = CurrentVersion;

    /// <summary> The old Mod Closet's looks were carried over (once). </summary>
    [JsonPropertyName("importedOld")]
    public bool ImportedOld { get; set; }

    /// <summary> The pinned look the outfit started from, if any (so "you changed the shoes" survives a restart). </summary>
    [JsonPropertyName("base")]
    public Guid? Base { get; set; }

    [JsonPropertyName("looks")]
    public List<LookData> Looks { get; set; } = new();

    /// <summary>
    /// Pinned looks the user removed, kept (not shown) for <see cref="LookRules.RemovedKeep"/>: the undo history does not
    /// survive a restart, so this is what could bring one back later.
    /// </summary>
    [JsonPropertyName("removed")]
    public List<LookData> Removed { get; set; } = new();

    private static readonly JsonSerializerOptions Options = new()
    {
        WriteIndented          = false,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        NumberHandling         = JsonNumberHandling.AllowReadingFromString | JsonNumberHandling.AllowNamedFloatingPointLiterals,
    };

    public string ToJson()
        => JsonSerializer.Serialize(this, Options);

    /// <summary> Reads a looks file; anything unreadable gives an empty one (and says so in <paramref name="problem"/>). </summary>
    public static LooksFile FromJson(string? text, out string problem)
    {
        problem = string.Empty;
        if (string.IsNullOrWhiteSpace(text))
            return new LooksFile();
        try
        {
            var file = JsonSerializer.Deserialize<LooksFile>(text, Options) ?? new LooksFile();
            if (file.Version < 2)
                return new LooksFile(); // the one-part-per-shelf form never shipped: nothing to carry over
            // version 2 (before the look sheet) reads as it is: its looks keep no colours, show nothing and are bound to
            // nothing, so they do what they did; it is written as the current version
            file.Version = CurrentVersion;
            file.Looks   = Clean(file.Looks);
            file.Removed = Clean(file.Removed).Where(l => l.RemovedUtc is not null && file.Looks.All(k => k.Id != l.Id)).ToList();
            return file;
        }
        catch (Exception ex) when (ex is JsonException or NotSupportedException or InvalidOperationException)
        {
            problem = ex.Message;
            return new LooksFile();
        }
    }

    /// <summary> Looks as read: no duplicates, no empty lists, at most one piece per socket, a held socket never empty. </summary>
    private static List<LookData> Clean(List<LookData>? looks)
    {
        looks = (looks ?? new List<LookData>()).Where(l => l is not null && l.Id != Guid.Empty).GroupBy(l => l.Id).Select(g => g.First()).ToList();
        foreach (var look in looks)
        {
            look.Pieces     ??= new List<LookPiece>();
            look.Empty      ??= new List<Socket>();
            look.Colourways ??= new List<Colourway>();
            // at most one piece per socket, and a socket is either held or empty
            // one piece per place: a socket, in a layered socket one per shelf
            look.Pieces = look.Pieces.Where(p => p is not null && p.Mod.Length > 0)
                .GroupBy(p => (p.Socket, LookRules.Layered(p.Socket) ? p.Shelf ?? string.Empty : string.Empty)).Select(g => g.First())
                .GroupBy(p => p.Key).Select(g => g.First()).ToList();
            look.Empty  = look.Empty.Distinct().Where(e => look.Pieces.All(p => p.Socket != e)).ToList();
            foreach (var piece in look.Pieces)
            {
                piece.Shelf ??= string.Empty;
                piece.Options ??= new Dictionary<string, List<string>>();
                if (piece.Print is { } print)
                {
                    print.Files     ??= new List<string>();
                    print.Signature ??= Array.Empty<float>();
                }
            }

            look.Colourways.RemoveAll(c => c is null);
            // body shelves: once each, body shelves only; a look from before they were chosen one by one wears every body
            // shelf it holds a piece on when its one switch was on
            look.Body = (look.Body ?? new List<string>()).Where(Pieces.IsBodyPiece).Distinct().ToList();
            if (look.Body.Count == 0 && look.LegacyWithBody)
                look.Body = look.Pieces.Select(p => p.Shelf).Where(Pieces.IsBodyPiece).Distinct().ToList();
            look.Colourway = look.Colourways.Count == 0 ? 0 : Math.Clamp(look.Colourway, 0, look.Colourways.Count - 1);
        }

        return looks;
    }
}
