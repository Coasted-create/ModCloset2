using System.Text;
using ModCloset2.Core;

namespace ModCloset2.Detect;

/// <summary> A dictionary word found in a name: the shelf it points to (may be "" for kind-only words), its kind and how sure. </summary>
/// <remarks> <see cref="Alone"/>: the word stood alone in its segment ("Nose chain (Ears)", a folder "Ears"). </remarks>
public readonly record struct WordHit(string Word, string Piece, string Kind, float Strength, float Factor, bool Alone = false);

/// <summary>
/// The words witness: tokenises names (mod, option groups, options, the user's folder, files, materials) and looks the
/// tokens up in a dictionary of garment words (English, German and common modder spellings). Whole words only; body and
/// size words (Rue, YAB, small…) and ids say nothing. "no gloves", "without socks", "don't use with socks" do not count
/// for the word that follows; words after "with" count less.
/// </summary>
public static class Words
{
    private readonly record struct Entry(string Piece, string Kind, float Strength);

    private static readonly Dictionary<string, Entry> Dict = Build();

    /// <summary> Words that never say anything about the shelf (bodies, sizes, filler). </summary>
    private static readonly HashSet<string> Ignore = new(StringComparer.Ordinal)
    {
        // bodies and body variants (see OLD BodyFit, BodyBaseDetector)
        "rue", "tre", "yab", "yanilla", "bibo", "bibop", "tbse", "lavabod", "lava", "larue", "gen3", "gen2", "solace", "vanilla",
        "buff", "omoi", "teardrop", "cupcake", "sugar", "pillow", "melon", "watermelon", "crusher", "skull", "squish",
        "wc", "sc", "xs", "s", "m", "l", "xl", "xxl", "small", "medium", "large", "mini", "big", "huge", "tiny", "petite", "chonk",
        // races, clans and their nicknames: "Au Ra" is a race, not an aura (an effect), in mod names and in folders
        "aura", "auras", "au", "ra", "raen", "xaela", "hyur", "hyurs", "midlander", "midlanders", "highlander", "highlanders",
        "elezen", "wildwood", "duskwight", "lalafell", "lalafells", "lala", "lalas", "plainsfolk", "dunesfolk", "miqo", "miqos",
        "miqote", "miqotes", "seeker", "seekers", "keeper", "keepers", "roegadyn", "roe", "roes", "femroe", "hellsguard",
        "hrothgar", "hroth", "hrothgals", "hrothgal", "helion", "helions", "viera", "vieras", "rava", "veena", "femra", "malera",
        "fem", "male", "males", "female", "females", "unisex", "masc", "femme",
        // more body mods and their variants
        "ivcs", "ivcs2", "yas", "yiggle", "neolithe", "thicclander", "sbtl", "tbsex", "ruexb", "w25", "lava", "gen3", "eve", "x",
        // filler
        "the", "and", "or", "of", "for", "a", "an", "to", "in", "on", "by", "my", "your", "default", "option", "options",
        "size", "sizes", "texture", "textures", "model", "models", "toggle", "toggles", "required", "install", "base", "none",
        "disabled", "enabled", "off", "on", "v1", "v2", "v3", "left", "right", "variant", "version", "color", "colour", "dye",
        "pattern", "plain", "material", "materials", "mod", "mods", "files", "file", "chara", "equipment", "accessory", "human",
        "obj", "mdl", "mtrl", "tex", "dds", "png", "only", "set", "full", "new", "old", "alt", "extra", "fix", "update",
    };

    /// <summary> More words that say nothing in file and material names (texture roles). </summary>
    private static readonly HashSet<string> FileIgnore = new(StringComparer.Ordinal)
        { "id", "mask", "norm", "normal", "base", "diffuse", "spec", "specular", "multi", "index", "catchlight", "texture", "common", "etc" };

    /// <summary> Words that end the counting of a segment ("no gloves", "without socks", "dont use with socks"). </summary>
    private static readonly HashSet<string> Negations = new(StringComparer.Ordinal)
        { "no", "not", "without", "dont", "don", "nicht", "ohne", "kein", "keine", "non", "remove", "removed", "hide", "hidden", "except" };

    private static readonly HashSet<string> Withs = new(StringComparer.Ordinal) { "with", "w", "mit", "plus", "incl", "including" };

    /// <summary>
    /// Words that make a name about a choice for one layer of the part ("Stocking texture", "Skirt options"): the
    /// garment word in it says the layer is there, not that the part is that garment.
    /// </summary>
    private static readonly HashSet<string> LayerWords = new(StringComparer.Ordinal)
        { "texture", "textures", "tex", "option", "options", "colour", "colours", "color", "colors", "dye", "dyes", "pattern", "patterns",
          "toggle", "toggles", "variant", "variants", "style", "styles", "material", "materials" };

    private static Dictionary<string, Entry> Build()
    {
        var d = new Dictionary<string, Entry>(StringComparer.Ordinal);

        void Add(string piece, string kind, float strength, params string[] words)
        {
            foreach (var w in words)
                d[w] = new Entry(piece, kind, strength);
        }

        // Headwear
        Add(Pieces.Head, "", 1f, "hat", "hats", "cap", "caps", "beanie", "helmet", "helm", "hood", "crown", "tiara", "circlet",
            "headband", "headpiece", "headdress", "headwear", "beret", "bandana", "bonnet", "fedora", "turban", "visor", "witchhat",
            "hut", "muetze", "mütze", "kappe", "kapuze", "krone", "haarreif", "diadem", "hairclip", "hairclips", "hairpin", "hairpins",
            "barrette", "kanzashi", "scrunchie", "hairband", "headphones", "earmuffs");
        Add(Pieces.Head, "glasses", 1f, "glasses", "sunglasses", "goggles", "spectacles", "monocle", "brille", "shades");
        Add(Pieces.Head, "mask", 0.8f, "mask", "masks", "blindfold", "maske", "eyepatch");
        Add(Pieces.Head, "veil", 0.9f, "veil", "schleier");
        Add(Pieces.Head, "horns", 0.6f, "horns", "horn", "antlers", "hoerner", "hörner");
        Add(Pieces.Head, "halo", 0.6f, "halo", "heiligenschein");

        // Tops
        Add(Pieces.Top, "top", 0.6f, "top", "tops");
        Add(Pieces.Top, "top", 1f, "shirt", "tshirt", "shirts", "blouse", "sweater", "hoodie", "sweatshirt", "pullover",
            "jumper", "tank", "tanktop", "camisole", "cami", "crop", "croptop", "tee", "tunic", "jersey", "turtleneck", "halter",
            "tube", "chestpiece", "oberteil", "hemd", "bluse", "shirtdress");
        Add(Pieces.Top, "jacket", 1f, "jacket", "jackets", "blazer", "cardigan", "bolero", "shrug", "jacke", "parka", "windbreaker");
        Add(Pieces.Top, "coat", 1f, "coat", "coats", "trenchcoat", "cloak", "poncho", "mantel", "overcoat", "longcoat", "cape", "capelet");
        Add(Pieces.Top, "vest", 0.9f, "vest", "waistcoat", "weste", "gilet");
        Add(Pieces.Top, "corset", 1f, "corset", "bustier", "korsett", "corsage");
        Add(Pieces.Top, "bra", 1f, "bra", "bralette", "bh", "brassiere", "pasties", "bandeau");
        Add(Pieces.Top, "dress", 1f, "dress", "dresses", "gown", "kleid", "sundress", "minidress", "qipao", "cheongsam", "kimono",
            "yukata", "robe", "robes", "sundress", "chemise", "nightgown", "negligee", "babydoll", "frock");
        Add(Pieces.Top, "bodysuit", 1f, "bodysuit", "leotard", "jumpsuit", "catsuit", "romper", "swimsuit", "onesie", "playsuit",
            "unitard", "overall", "overalls", "teddy", "monokini", "plugsuit");
        Add(Pieces.Top, "harness", 0.7f, "harness", "harnesses");

        // Gloves
        Add(Pieces.Gloves, "", 1f, "gloves", "glove", "gauntlets", "gauntlet", "mittens", "mitts", "handschuhe", "handschuh", "fingerless");
        Add(Pieces.Gloves, "armwarmers", 0.9f, "armwarmers", "armsleeves", "stulpen");

        // Bottoms
        Add(Pieces.Bottoms, "pants", 1f, "pants", "trousers", "jeans", "slacks", "sweatpants", "joggers", "leggings", "hakama",
            "hosen", "cargo", "chaps", "culottes", "flares", "yogapants");
        Add(Pieces.Bottoms, "", 1f, "bottoms", "bottom");
        Add(Pieces.Bottoms, "shorts", 1f, "shorts", "hotpants", "shorties", "boyshorts", "bloomers", "jorts");
        Add(Pieces.Bottoms, "skirt", 1f, "skirt", "skirts", "miniskirt", "microskirt", "rock", "rocks", "sarong", "tutu", "pareo", "kilt");
        Add(Pieces.Bottoms, "underwear", 1f, "panties", "panty", "pantsu", "underwear", "thong", "thongs", "briefs", "knickers",
            "gstring", "string", "boxers", "unterhose", "slip", "hoeschen", "höschen", "loincloth", "fundoshi", "lingerie");

        // Legwear
        Add(Pieces.Legwear, "stockings", 1f, "stockings", "stocking", "thighhighs", "thighhigh", "overknees", "strümpfe", "struempfe",
            "strumpf", "nylons", "garter", "garters", "garterbelt", "suspenders", "halterlose");
        Add(Pieces.Legwear, "socks", 1f, "socks", "sock", "kneehighs", "kneesocks", "legwarmers", "socken", "anklets", "tabi", "kniestrümpfe");
        Add(Pieces.Legwear, "tights", 1f, "tights", "pantyhose", "fishnet", "fishnets", "strumpfhose", "thights", "hosiery", "netstockings");
        Add(Pieces.Legwear, "", 0.8f, "legwear");

        // Shoes
        Add(Pieces.Shoes, "", 1f, "shoes", "shoe", "footwear", "schuhe", "schuh", "clogs", "trainers", "kicks");
        Add(Pieces.Shoes, "heels", 1f, "heels", "heel", "highheels", "pumps", "stilettos", "stiletto", "platforms", "wedges", "absätze", "mules");
        Add(Pieces.Shoes, "boots", 1f, "boots", "boot", "booties", "stiefel", "stiefeletten", "thighboots");
        Add(Pieces.Shoes, "flats", 1f, "flats", "sneakers", "sneaker", "loafers", "slippers", "ballerinas", "moccasins", "oxfords", "mary", "janes");
        Add(Pieces.Shoes, "sandals", 1f, "sandals", "sandal", "geta", "flipflops", "sandalen", "zori", "slides");

        // Jewellery
        Add(Pieces.Earrings, "", 1f, "earrings", "earring", "earcuff", "earcuffs", "ohrringe", "ohrring", "ohrstecker");
        Add(Pieces.Earrings, "", 0.6f, "studs", "hoops");
        Add(Pieces.Necklace, "", 1f, "necklace", "necklaces", "pendant", "amulet", "locket", "halskette", "kette", "medallion", "beads");
        Add(Pieces.Necklace, "choker", 1f, "choker", "chokers", "halsband", "collar");
        Add(Pieces.Necklace, "scarf", 0.6f, "scarf", "scarves", "schal", "tuch", "neckerchief", "ascot", "bowtie", "necktie", "cravat");
        Add(Pieces.Bracelets, "", 1f, "bracelet", "bracelets", "bangle", "bangles", "wristband", "wristbands", "wristlet", "armband",
            "armlet", "armlets", "armreif", "armbänder", "cuffs", "shackles", "manacles", "watch", "wristwatch");
        Add(Pieces.Rings, "", 1f, "ring", "rings", "ringe", "fingerring", "signet");

        // Body
        Add(Pieces.Hair, "", 1f, "hair", "hairstyle", "hairstyles", "ponytail", "ponytails", "braid", "braids", "bangs", "bun", "buns",
            "pigtails", "twintails", "updo", "wig", "haare", "haar", "frisur", "zopf", "mohawk", "dreadlocks", "dreads", "locks", "curls",
            "hairdo", "fringe", "sidecut", "undercut");
        Add(Pieces.Face, "", 1f, "face", "sculpt", "teeth", "fangs", "gesicht", "beard", "stubble", "moustache", "mustache", "whiskers",
            "snout", "muzzle");
        // Paint on the face (brief: makeup and sculpts apart): makeup, eyes, brows, lashes, decals, freckles of the face.
        Add(Pieces.Makeup, "", 1f, "makeup", "lashes", "eyelashes", "brows", "eyebrows", "eyes", "eye", "iris", "irises", "freckles",
            "lips", "lipstick", "blush", "eyeliner", "eyeshadow", "mascara", "sclera", "catchlight", "augen", "wimpern", "augenbrauen",
            "sommersprossen", "schminke", "lash", "pupils", "contacts", "facepaint", "liner", "lipgloss", "decal", "decals");
        Add(Pieces.Ears, "", 1f, "ears", "kemonomimi", "mimi", "catears", "foxears", "bunnyears", "wolfears", "dogears", "ohren", "elfears");
        Add(Pieces.Ears, "", 0.6f, "ear");
        Add(Pieces.Tail, "", 1f, "tail", "tails", "schwanz", "schweif", "sippo");
        Add(Pieces.Skin, "", 1f, "skin", "tattoo", "tattoos", "tattoed", "tattooed", "scales", "scars", "scar", "stretchmarks", "tanlines",
            "haut", "tätowierung", "tattoo", "narben", "bodypaint", "veins", "abs", "muscles", "moles", "birthmark", "pubes",
            "pubic", "complexion", "skintone");
        Add(Pieces.Nails, "", 1f, "nails", "nail", "manicure", "claws", "claw", "fingernails", "naegel", "nägel", "krallen", "talons", "nailart", "polish");

        // Not worn
        Add(Pieces.Other, "animation", 1f, "animation", "animations", "emote", "emotes", "dance", "dances", "pose", "poses", "idle", "idles",
            "anim", "anims", "gpose", "groundsit", "doze");
        Add(Pieces.Other, "effect", 1f, "vfx", "effect", "effects", "particles", "sparkles");
        Add(Pieces.Other, "sound", 1f, "sound", "sounds", "sfx", "voice", "voices", "music", "bgm", "audio", "footsteps");
        Add(Pieces.Other, "interface", 1f, "ui", "interface", "hud", "icon", "icons", "font", "fonts", "nameplate", "loading", "loadingscreen", "cursor");
        Add(Pieces.Other, "mount or minion", 1f, "mount", "mounts", "minion", "minions", "chocobo", "pet", "companion");
        Add(Pieces.Other, "weapon", 1f, "weapon", "weapons", "sword", "staff", "gunblade", "dagger", "daggers", "katana", "scythe", "lance",
            "spear", "axe", "greatsword", "rapier", "grimoire", "shield", "fists", "chakrams", "gun", "pistol", "rifle", "wand", "glaive");
        Add(Pieces.Other, "world", 1f, "housing", "furniture", "glamourplate", "skybox", "weather", "lighting", "gshade", "reshade");
        Add(Pieces.Other, "helper", 1f, "skeleton", "physics", "bones", "sklb", "phyb", "scaling", "rsp");

        // Kinds without a built-in shelf: they suggest the user's own shelves ("New shelf: Wings").
        Add("", "wings", 1f, "wings", "wing", "flügel", "fluegel");
        Add("", "bag", 1f, "bag", "bags", "backpack", "backpacks", "purse", "handbag", "satchel", "rucksack", "tasche");
        Add("", "piercing", 1f, "piercing", "piercings", "pierced", "nosering", "septum", "labret", "navel", "bellybutton");

        return d;
    }

    /// <summary> Bigrams that are one garment word ("thigh highs", "ear cuff", "cat ears"). </summary>
    private static readonly Dictionary<(string, string), string> Pairs = new()
    {
        [("thigh", "highs")] = "thighhighs", [("thigh", "high")] = "thighhighs", [("thigh", "hi")] = "thighhighs",
        [("knee", "highs")] = "kneehighs", [("knee", "high")] = "kneehighs", [("knee", "socks")] = "kneesocks",
        [("leg", "warmers")] = "legwarmers", [("arm", "warmers")] = "armwarmers", [("ear", "cuff")] = "earcuff",
        [("ear", "cuffs")] = "earcuffs", [("ear", "piercing")] = "earrings", [("ear", "piercings")] = "earrings",
        [("cat", "ears")] = "catears", [("fox", "ears")] = "foxears", [("bunny", "ears")] = "bunnyears", [("wolf", "ears")] = "wolfears",
        [("elf", "ears")] = "elfears", [("make", "up")] = "makeup", [("high", "heels")] = "highheels", [("t", "shirt")] = "tshirt",
        [("crop", "top")] = "croptop", [("tank", "top")] = "tanktop", [("hair", "clip")] = "hairclip", [("hair", "pin")] = "hairpin",
        [("hair", "band")] = "hairband", [("flip", "flops")] = "flipflops", [("body", "suit")] = "bodysuit", [("jump", "suit")] = "jumpsuit",
        [("swim", "suit")] = "swimsuit", [("hot", "pants")] = "hotpants", [("g", "string")] = "gstring", [("over", "knees")] = "overknees",
        [("fish", "net")] = "fishnet", [("fish", "nets")] = "fishnets", [("nail", "art")] = "nailart", [("arm", "band")] = "armband",
        [("wrist", "band")] = "wristband", [("body", "paint")] = "bodypaint", [("tan", "lines")] = "tanlines",
        [("stretch", "marks")] = "stretchmarks", [("loading", "screen")] = "loadingscreen", [("nose", "ring")] = "nosering",
        [("belly", "button")] = "bellybutton", [("ground", "sit")] = "groundsit", [("mary", "janes")] = "flats",
        [("finger", "nails")] = "fingernails", [("eye", "lashes")] = "eyelashes", [("eye", "brows")] = "eyebrows",
        [("arm", "sleeves")] = "armsleeves", [("long", "coat")] = "longcoat", [("trench", "coat")] = "trenchcoat",
        [("garter", "belt")] = "garterbelt", [("pubic", "hair")] = "pubes",
    };

    /// <summary> Splits a name into lower-case tokens ("ThighHighs_v2 [Rue]" → thigh, highs, v2, rue). </summary>
    public static List<string> Tokens(string text)
    {
        var result = new List<string>();
        if (string.IsNullOrEmpty(text))
            return result;

        var sb = new StringBuilder();
        char prev = '\0';
        foreach (var ch in text.Normalize(NormalizationForm.FormC))
        {
            if (char.IsLetterOrDigit(ch) || ch == '\'')
            {
                if (ch == '\'')
                {
                    prev = ch;
                    continue; // don't → dont
                }

                // camelCase and letter/digit changes split words ("ThighHighs" → thigh highs; "gen3" stays).
                if (sb.Length > 0 && char.IsUpper(ch) && char.IsLower(prev))
                    Flush();
                sb.Append(char.ToLowerInvariant(ch));
                prev = ch;
            }
            else
            {
                Flush();
                prev = ch;
                if (IsSegmentBreak(ch))
                    result.Add("|");
            }
        }

        Flush();
        return result;

        void Flush()
        {
            if (sb.Length > 0)
                result.Add(sb.ToString());
            sb.Clear();
        }
    }

    private static bool IsSegmentBreak(char ch)
        => ch is '-' or '–' or '—' or '(' or ')' or '[' or ']' or '{' or '}' or '/' or '\\' or '|' or ',' or ';' or ':' or '.' or '+' or '&' or '!' or '?';

    /// <summary> True for tokens that are ids, hashes or numbers ("e6157", "c0201e6157", "3557988335", a 40-hex hash). </summary>
    private static bool IsId(string t)
    {
        if (t.Length == 0 || t.All(char.IsDigit))
            return true;
        if (t.Length >= 12 && t.All(Uri.IsHexDigit))
            return true;
        var digits = t.Count(char.IsDigit);
        return digits >= 2 && digits * 2 >= t.Length - 1;
    }

    /// <summary>
    /// The dictionary words in a name, with how much each counts there (1, or less after "with"). Words after a
    /// negation are skipped until the next segment break.
    /// </summary>
    public static List<WordHit> Scan(string text, IReadOnlySet<string>? ignore = null, bool fileName = false)
    {
        var hits   = new List<WordHit>();
        var tokens = Tokens(text);
        var negated = false;
        var factor  = 1f;
        var segmentStart = 0;
        var segmentWords = 0;
        var layer = false;
        for (var i = 0; i < tokens.Count; ++i)
        {
            var t = tokens[i];
            if (t == "|")
            {
                EndSegment();
                negated = false;
                factor  = 1f;
                continue;
            }

            if (!Ignore.Contains(t) && ignore?.Contains(t) != true && !IsId(t))
                ++segmentWords;
            if (LayerWords.Contains(t))
                layer = true;

            // "Stockings Off", "Socks None": a trailing off takes back what the segment said ("off shoulder" stays).
            if (t is "off" or "none" && (i + 1 == tokens.Count || tokens[i + 1] == "|"))
            {
                hits.RemoveRange(segmentStart, hits.Count - segmentStart);
                continue;
            }

            if (Negations.Contains(t))
            {
                negated = true;
                continue;
            }

            if (Withs.Contains(t))
            {
                // "top with socks", "with colored cat ears": what follows is an extra.
                factor = Math.Min(factor, Tuning.Votes.WordWithFactor);
                continue;
            }

            if (negated || IsId(t) || ignore?.Contains(t) == true)
                continue;

            // Two tokens that make one word ("thigh highs").
            if (i + 1 < tokens.Count && Pairs.TryGetValue((t, tokens[i + 1]), out var joined) && Dict.TryGetValue(joined, out var pe))
            {
                hits.Add(new WordHit(joined, pe.Piece, pe.Kind, pe.Strength, factor));
                ++i;
                continue;
            }

            if (t.Length < 2 || Ignore.Contains(t) || fileName && FileIgnore.Contains(t))
                continue;

            if (Lookup(t) is { } hit)
                hits.Add(hit with { Factor = factor });
        }

        EndSegment();

        // A name about one layer's choice ("Sock colours", "Stocking texture"): every garment word in it counts less.
        if (layer && !fileName)
            for (var k = 0; k < hits.Count; ++k)
                hits[k] = hits[k] with { Factor = hits[k].Factor * Tuning.Votes.WordLayerFactor };
        return hits;

        void EndSegment()
        {
            if (segmentWords == 1 && hits.Count == segmentStart + 1)
                hits[segmentStart] = hits[segmentStart] with { Alone = true };
            segmentStart = hits.Count;
            segmentWords = 0;
        }
    }

    /// <summary> One token: the word itself, its singular, or a compound ending in a known word ("ringfingernails" → nails). </summary>
    public static WordHit? Lookup(string t)
    {
        if (Dict.TryGetValue(t, out var e))
            return new WordHit(t, e.Piece, e.Kind, e.Strength, 1f);
        if (t.Length > 3 && t.EndsWith('s') && Dict.TryGetValue(t[..^1], out e))
            return new WordHit(t[..^1], e.Piece, e.Kind, e.Strength, 1f);

        // "lash1", "nails02": a word with a number after it.
        var letters = t.TrimEnd('0', '1', '2', '3', '4', '5', '6', '7', '8', '9');
        if (letters.Length >= 3 && letters.Length < t.Length)
            return Lookup(letters);

        // Compounds modders write as one word ("mpears", "ringfingernails", "thighboots"): a known word of 4+ letters at
        // the end. Only for longer tokens and with less strength.
        if (t.Length >= 7)
            for (var cut = 1; cut <= t.Length - 4; ++cut)
            {
                var tail = t[cut..];
                if (tail.Length >= 4 && Dict.TryGetValue(tail, out e) && !Ignore.Contains(tail))
                    return new WordHit(tail, e.Piece, e.Kind, e.Strength * 0.7f, 1f);
            }

        return null;
    }

    /// <summary> Lower-case tokens of a person's name (the author): they say nothing about the shelf. </summary>
    public static HashSet<string> NameTokens(string name)
        => Tokens(name).Where(t => t != "|").ToHashSet(StringComparer.Ordinal);

    /// <summary> For tests and the census tool: the dictionary size. </summary>
    public static int Count
        => Dict.Count;

    /// <summary> A word as the evidence shows it: in typographic quotes, as found. </summary>
    internal static string Quote(string word)
        => "“" + word + "”";
}
