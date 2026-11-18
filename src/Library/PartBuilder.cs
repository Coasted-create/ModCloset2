using ModCloset2.Core;
using ModCloset2.Game;

namespace ModCloset2.Library;

/// <summary>
/// Turns what is known about a mod into its parts: step 1 (Penumbra's changed items, shelf by socket alone), then the
/// detector's answer on top, then whether each part can show on the player. Pure.
/// </summary>
public static class PartBuilder
{
    /// <summary> Confidence of a step-1 part: above <see cref="DetectorThresholds.Unsure"/>, so step 1 alone fills the shelves. </summary>
    public const float Step1Confidence = 0.6f;

    /// <summary> The shelf of a socket at step 1 (before the detector looked at the files). </summary>
    public static string ShelfOf(Socket socket)
        => socket switch
        {
            Socket.Head         => Pieces.Head,
            Socket.Body         => Pieces.Top,
            Socket.Hands        => Pieces.Gloves,
            Socket.Legs         => Pieces.Bottoms,
            Socket.Feet         => Pieces.Shoes,
            Socket.Ears         => Pieces.Earrings,
            Socket.Neck         => Pieces.Necklace,
            Socket.Wrists       => Pieces.Bracelets,
            Socket.Rings        => Pieces.Rings,
            Socket.Hair         => Pieces.Hair,
            Socket.Face         => Pieces.Face,
            Socket.FaceTextures => Pieces.Face,
            Socket.Tail         => Pieces.Tail,
            Socket.EarShape     => Pieces.Ears,
            Socket.BodyBase     => Pieces.Skin,
            _                   => Pieces.Other,
        };

    /// <summary> The slot a socket borrows, in a word ("earrings", "tail"), English: shown through Loc.T. </summary>
    public static string SlotWord(Socket socket)
        => socket switch
        {
            Socket.Head         => "hat",
            Socket.Body         => "top",
            Socket.Hands        => "gloves",
            Socket.Legs         => "bottoms",
            Socket.Feet         => "shoes",
            Socket.Ears         => "earrings",
            Socket.Neck         => "necklace",
            Socket.Wrists       => "bracelets",
            Socket.Rings        => "rings",
            Socket.MainHand     => "weapon",
            Socket.OffHand      => "weapon",
            Socket.Hair         => "hair",
            Socket.Face         => "face",
            Socket.Tail         => "tail",
            Socket.EarShape     => "ears",
            Socket.BodyBase     => "body",
            Socket.FaceTextures => "face",
            _                   => "nothing worn",
        };

    private static Socket CustomizationSocket(string key)
        => key switch
        {
            "Hairstyle" => Socket.Hair,
            "Face"      => Socket.Face,
            "Ears"      => Socket.EarShape,
            _           => Socket.Tail,
        };

    /// <summary>
    /// The customize values to set when a part is tried or kept: the player's own number when the mod has files for it
    /// (an eye or makeup mod made for every face must not change the face; a hair mod covering the player's hairstyle
    /// must not swap it), else the mod's first number for the player. <paramref name="current"/> gives the player's value
    /// of a key now (null when unknown).
    /// </summary>
    public static Dictionary<string, int> PickCustomize(LibPart part, Func<string, int?> current)
    {
        var result = new Dictionary<string, int>();
        foreach (var (key, value) in part.Customize)
            result[key] = current(key) is { } now && part.CustomizeChoices.TryGetValue(key, out var all) && all.Contains(now) ? now : value;
        return result;
    }

    /// <summary> What step 1 says about a part. </summary>
    public static PartInfo Step1Info(string mod, Socket socket, string kind, IEnumerable<string> groups, int models)
    {
        var shelf = ShelfOf(socket);
        return new PartInfo
        {
            Mod        = mod,
            Socket     = socket,
            Piece      = shelf,
            Kind       = kind,
            Confidence = Step1Confidence,
            Guesses    = new List<PieceGuess> { new() { Piece = shelf, Score = Step1Confidence } },
            Evidence   = new List<Evidence> { new() { Kind = EvidenceKind.Slot, Value = SlotWord(socket), Piece = shelf, Weight = 1f } },
            Groups     = groups.ToList(),
            Models     = Math.Max(1, models),
        };
    }

    /// <summary> Words in group names that name a piece, for groups whose files say nothing (attribute toggles). </summary>
    private static readonly (Socket Socket, string[] Words)[] PieceWords =
    {
        (Socket.Body, new[] { "top", "tops", "shirt", "bra", "jersey", "dress", "jacket", "coat", "chest", "body" }),
        (Socket.Legs, new[] { "bottom", "bottoms", "pants", "skirt", "shorts", "panties", "legs" }),
        (Socket.Hands, new[] { "glove", "gloves", "nails", "hands", "hand" }),
        (Socket.Feet, new[] { "shoe", "shoes", "socks", "sock", "feet", "boots", "heels" }),
        (Socket.Head, new[] { "hat", "head", "helmet", "headwear" }),
        (Socket.Ears, new[] { "earring", "earrings" }),
        (Socket.Neck, new[] { "necklace", "choker", "neck" }),
        (Socket.Wrists, new[] { "bracelet", "bracelets", "wrist", "wrists" }),
        (Socket.Rings, new[] { "ring", "rings" }),
    };

    /// <summary>
    /// The option groups that change a socket's files (the only ones worth showing for that part). Body-size and race
    /// groups are kept here (the strip leaves them out). A group whose files and edits name no piece (attribute toggles
    /// such as "Top Toggles") goes to the piece its name names, else to every part of the mod: shown too often is better
    /// than never.
    /// </summary>
    public static List<string> GroupsFor(IReadOnlyList<GroupInfo> groups, Socket socket, IReadOnlyCollection<Socket> partSockets)
    {
        var result = new List<string>();
        foreach (var g in groups.Where(g => g.Name.Length > 0))
        {
            var real = g.Sockets.Where(s => s is not (Socket.Other or Socket.None)).ToList();
            if (real.Count > 0)
            {
                if (real.Any(s => GamePaths.SameFamily(s, socket)))
                    result.Add(g.Name);
                continue;
            }

            if (socket == Socket.Other)
            {
                result.Add(g.Name);
                continue;
            }

            var words = g.Name.ToLowerInvariant().Split(new[] { ' ', '-', '_', '/', ':', '(', ')', '[', ']', ',', '.' }, StringSplitOptions.RemoveEmptyEntries);
            var named = PieceWords.Where(p => p.Words.Any(words.Contains) && partSockets.Contains(p.Socket)).Select(p => p.Socket).ToList();
            if (named.Count == 0 || named.Contains(socket))
                result.Add(g.Name);
        }

        return result;
    }

    /// <summary> Builds the parts of one mod. A mod whose changed items are not read yet has no parts. </summary>
    public static List<LibPart> Build(LibMod mod, PlayerInfo player)
    {
        var parts = new List<LibPart>();
        if (mod.Items is not { } items)
            return parts;

        var facts  = mod.Facts;
        var groups = mod.Groups;
        var chain  = Races.Chain(player.RaceCode);

        // ---- step 1 ----
        var step1 = new Dictionary<Socket, (string Kind, List<ItemChoice> Items, Dictionary<string, int>? Custom, bool Fits)>();
        foreach (var pieces in items.Slots)
        {
            var socket = GearSlots.SocketOf(pieces.Slot);
            if (socket == Socket.None)
                continue;
            if (!step1.TryGetValue(socket, out var entry))
                step1[socket] = entry = (socket is Socket.MainHand or Socket.OffHand ? "weapon" : string.Empty, new List<ItemChoice>(), null, true);
            foreach (var item in pieces.Items)
                if (entry.Items.All(i => i.Id != item.Id))
                    entry.Items.Add(item);
        }

        var choices = new Dictionary<Socket, IReadOnlyList<int>>();
        foreach (var group in items.Customizations.GroupBy(c => CustomizationSocket(c.Key)))
        {
            var mine  = Races.MatchingCustomization(group, player);
            var key   = GearSlots.CustomizeKey(group.Key)!;
            if (Races.MatchingValues(group, player).Values.FirstOrDefault() is { Count: > 0 } all)
                choices[group.Key] = all;
            var value = mine.Count > 0 ? new Dictionary<string, int> { [key] = mine.Values.First() } : new Dictionary<string, int>();
            // Hair, face and tail only for the player's own race, gender and clan (their numbers mean nothing elsewhere).
            step1[group.Key] = (string.Empty, new List<ItemChoice>(), value, !player.Known || mine.Count > 0);
        }

        if (items.Bodies.Count > 0)
            step1[Socket.BodyBase] = (string.Empty, new List<ItemChoice>(), null,
                !player.Known || items.Bodies.Contains($"{player.Race}|{player.Gender}", StringComparer.OrdinalIgnoreCase));

        if (step1.Count == 0)
            step1[Socket.Other] = (items.OtherKind, new List<ItemChoice>(), null, true);

        // ---- the detector's answer on top ----
        var detected = mod.Detection is { Parts.Count: > 0 } d
            ? d.Parts.Where(p => p.Socket != Socket.None).GroupBy(p => p.Socket).ToDictionary(g => g.Key, g => g.First())
            : new Dictionary<Socket, PartInfo>();
        // Once the detector has answered, its sockets are the parts: what it leaves out are sockets with meta edits only
        // (support for the parts that have files), helper files of a mod that has worn parts, and a step-1 "nothing worn"
        // part that was only Penumbra not knowing what the files are (body textures, say). Step 1 still says how to put
        // each part on.
        var sockets = detected.Count > 0 ? detected.Keys.ToList() : step1.Keys.ToList();

        // A face's textures without its model: step 1 knows the face number from Penumbra, and the textures only show on
        // that face, so trying the part sets it.
        if (detected.ContainsKey(Socket.FaceTextures) && !detected.ContainsKey(Socket.Face) && step1.TryGetValue(Socket.Face, out var face))
        {
            step1[Socket.FaceTextures] = face;
            if (choices.TryGetValue(Socket.Face, out var faces))
                choices[Socket.FaceTextures] = faces;
        }

        foreach (var socket in sockets.OrderBy(s => s))
        {
            var inStep1  = step1.TryGetValue(socket, out var s1);
            var versions = Versions(s1.Items ?? new List<ItemChoice>());
            var mineGroups = GroupsFor(groups, socket, sockets);
            PartInfo info;
            var fromDetector = detected.TryGetValue(socket, out var det);
            if (fromDetector)
            {
                info = Clone(det!);
                info.Mod = mod.Directory;
                if (info.Groups.Count == 0)
                    info.Groups = mineGroups;
                if (Pieces.IsVirtual(info.Piece) || info.Piece == Pieces.Hidden || info.Piece.Length == 0)
                    info.Piece = ShelfOf(socket);
            }
            else
            {
                info = Step1Info(mod.Directory, socket, s1.Kind ?? string.Empty, mineGroups, versions.Count);
                if (facts?.Codes.TryGetValue(socket, out var c) == true)
                    info.RaceCodes = (c.Models.Count > 0 ? c.Models : c.Other).Order().ToList();
            }

            var fits = FitsMe(socket, inStep1 ? s1.Fits : null, info, facts, player, chain);
            parts.Add(new LibPart
            {
                Key          = new PartKey(mod.Directory, socket),
                Mod          = mod,
                Info         = info,
                FromDetector = fromDetector,
                Versions     = versions,
                Items        = s1.Items ?? new List<ItemChoice>(),
                SlotIds      = SlotIds(socket, facts, s1.Items),
                Customize    = s1.Custom ?? new Dictionary<string, int>(),
                CustomizeChoices = s1.Custom is { Count: > 0 } custom && choices.TryGetValue(socket, out var list)
                    ? new Dictionary<string, IReadOnlyList<int>> { [custom.Keys.First()] = list }
                    : new Dictionary<string, IReadOnlyList<int>>(),
                FitsMe       = fits,
            });
        }

        return parts;
    }

    /// <summary>
    /// Whether a part can show on the player (the old FitsRace / Look.CodesFor rules, per socket). Gear: its files are for
    /// the player's race code or one the game falls back to (accessories made for c0101 count: the game uses those for
    /// everyone). Hair, face, tail and ears: only the player's own race, gender and clan. Body textures: the player's race
    /// code or its fallbacks. Everything not worn fits. An unknown player (not logged in) fits everything.
    /// </summary>
    public static bool FitsMe(Socket socket, bool? step1Fits, PartInfo info, ModFacts? facts, PlayerInfo player, IReadOnlyList<int> chain)
    {
        if (!player.Known)
            return true;
        if (socket is Socket.Other or Socket.MainHand or Socket.OffHand or Socket.None)
            return true;

        if (GearSlots.IsCustomization(socket))
        {
            if (step1Fits is { } known)
                return known;
            var own = info.RaceCodes.Count > 0 ? info.RaceCodes.ToHashSet() : Codes(facts, socket, modelsFirst: true);
            return own.Count == 0 || own.Contains(player.RaceCode);
        }

        if (GearSlots.IsGear(socket))
        {
            var codes = Codes(facts, socket, modelsFirst: true);
            var accessory = socket is Socket.Ears or Socket.Neck or Socket.Wrists or Socket.Rings;
            return codes.Count == 0 || codes.Overlaps(chain) || accessory && codes.Contains(101);
        }

        // body and face textures
        if (step1Fits is false)
            return false;
        var body = info.RaceCodes.Count > 0 ? info.RaceCodes.ToHashSet() : Codes(facts, socket, modelsFirst: false);
        return body.Count == 0 || body.Overlaps(chain);
    }

    private static HashSet<int> Codes(ModFacts? facts, Socket socket, bool modelsFirst)
    {
        if (facts is null)
            return new HashSet<int>();
        var result = new HashSet<int>();
        foreach (var (s, c) in facts.Codes)
            if (GamePaths.SameFamily(s, socket))
            {
                if (modelsFirst && c.Models.Count > 0)
                    result.UnionWith(c.Models);
                else
                    result.UnionWith(c.Models.Concat(c.Other));
            }

        // models count first: textures are often shared between races
        if (modelsFirst && result.Count == 0)
            foreach (var (s, c) in facts.Codes)
                if (GamePaths.SameFamily(s, socket))
                    result.UnionWith(c.Other);
        return result;
    }

    /// <summary> One item per model set, best first (items of one set look the same, so they are one version). </summary>
    public static List<ItemChoice> Versions(IReadOnlyList<ItemChoice> items)
    {
        var seen   = new HashSet<long>();
        var result = new List<ItemChoice>();
        foreach (var item in items)
            if (seen.Add(item.Model == 0 ? -(long)item.Id : item.SetId))
                result.Add(item);
        return result;
    }

    /// <summary>
    /// The Glamourer slots a gear part goes into. A ring is put on the right hand, and on the left too when the mod has files
    /// for the left hand (rings share one item list; the old plugin put a ring mod's item on the hand it was listed for).
    /// </summary>
    public static IReadOnlyList<byte> SlotIds(Socket socket, ModFacts? facts, IReadOnlyList<ItemChoice>? items)
    {
        if (socket == Socket.Rings)
        {
            var right = facts?.GearSlots.Contains("RFinger") ?? true;
            var left  = facts?.GearSlots.Contains("LFinger") ?? false;
            var ids   = new List<byte>();
            if (right || !left)
                ids.Add(12);
            if (left)
                ids.Add(14);
            return ids;
        }

        if (!GearSlots.IsGear(socket))
            return Array.Empty<byte>();
        return GearSlots.NamesOf(socket).Select(n => GearSlots.IdOf(n)!.Value).ToArray();
    }

    /// <summary> A copy of the detector's answer that the closet may adjust without touching the cached one. </summary>
    private static PartInfo Clone(PartInfo p)
        => new()
        {
            Mod        = p.Mod,
            Socket     = p.Socket,
            Piece      = p.Piece,
            Kind       = p.Kind,
            Confidence = p.Confidence,
            Guesses    = p.Guesses.ToList(),
            Evidence   = p.Evidence.ToList(),
            Covers     = p.Covers,
            Signature  = p.Signature,
            TextureOnly = p.TextureOnly,
            RaceCodes  = p.RaceCodes.ToList(),
            Groups     = p.Groups.ToList(),
            Models     = p.Models,
        };
}
