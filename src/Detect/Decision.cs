using ModCloset2.Core;

namespace ModCloset2.Detect;

/// <summary>
/// Step two of the detection: a pure function from <see cref="ModFacts"/> to <see cref="ModDetection"/>. Each witness
/// (the slot, where the model sits on the body, the words, the user's folder, the shaders) votes for shelves with a
/// strength; the votes are added with the weights in <see cref="Tuning.Votes"/>, and the confidence says how many
/// witnesses agree. Two agreeing: sorted. Disagreeing or silent: below <see cref="DetectorThresholds.Unsure"/> (To sort).
/// </summary>
public static partial class Decision
{
    /// <summary> What one witness said about a part. </summary>
    public sealed class Opinion
    {
        public EvidenceKind Kind;
        public Dictionary<string, float> Votes = new();

        /// <summary> 0..1; below <see cref="VoteTuning.Silent"/> the witness has no hint. </summary>
        public float Strength;
        public string Phrase = string.Empty;

        /// <summary> What the phrase quotes ("bracelet" in "says “bracelet”"), "" for fixed phrases. </summary>
        public string Word = string.Empty;
        public float Weight;

        public string Top
            => Votes.Count == 0 ? string.Empty : Votes.MaxBy(kv => kv.Value).Key;

        public bool Speaks
            => Strength >= Tuning.Votes.Silent && Top.Length > 0;
    }

    /// <summary> What the body witness worked out from a model. </summary>
    public sealed class BodyReading
    {
        public Dictionary<BodyRegions, float> Shares = new();
        public float Away;
        public BodyRegions Covers;
        public float TipShare;
        public float BaseShare;
        public int Fingers;
        public float ScaledArea;

        /// <summary> The model has a heel: a heels offset set on it, or a sole below the floor line. </summary>
        public bool Heels;

        /// <summary> Gear parts: what the body witness worked out (see Decision.Gear.cs). </summary>
        public GearReading? Gear;
        public Opinion Opinion = new() { Kind = EvidenceKind.Body };

        public float this[BodyRegions r]
            => Shares.GetValueOrDefault(r);
    }

    public static ModDetection Decide(ModFacts facts, int detectorVersion)
    {
        var result = new ModDetection { DetectorVersion = detectorVersion, Problems = facts.Problems.ToList() };

        // The "Other" part only stands on its own when it is more than helper files for the worn parts. Skin textures
        // read as helper files by an older reader (made-up paths) are the skin.
        var parts = facts.Parts.Select(AsSkin).Where(p => p.Socket != Socket.Other || !IsSupportOnly(p, facts)).ToList();

        // Rule 3: gear models that show only bare skin are the body (a body mod's shapes for the bare character). In a
        // body mod (one with such models, or with body models of its own) every gear model on the bare body's sets (the
        // empty slot, the smallclothes) is the body too, whatever it wears by default (its own underwear, nails,
        // piercings): a body mod never shows its bare models as clothes.
        var bare = parts.Where(IsBareBody).ToList();
        if (bare.Count > 0 || parts.Any(p => p.Socket == Socket.BodyBase && p.HasModel))
            bare.AddRange(parts.Where(p => !bare.Contains(p) && GamePaths.IsGear(p.Socket) && OnBodySets(p) && p.Model is { SkinArea: > 0 }));
        parts = parts.Where(p => !bare.Contains(p)).ToList();

        // Per-part witnesses that need no other part.
        var readings = parts.ToDictionary(p => p, p => ReadBody(p));
        var words    = parts.ToDictionary(p => p, p => PartWords(p, facts));

        // A word that another part of the mod explains does not vote here ("arm warmers" on the top, when the mod has
        // a hands part; "bottom" on the top, when it has a legs part).
        // Legwear words on the leg-slot part, when a feet-slot part of the same mod is the stockings (it reaches up the
        // legs and names them too), are that part's.
        var legwearElsewhere = parts.Where(q => q.Socket == Socket.Feet && readings[q].Gear?.Candidates.Contains(Pieces.Legwear) == true
                                             && readings[q][BodyRegions.Knees] + readings[q][BodyRegions.Calves] + readings[q][BodyRegions.Thighs]
                                                >= Tuning.Gear.StockingsMinLegs * Math.Max(readings[q].Shares.Values.Sum(), 1e-6f)
                                             && words[q].Any(w => w.Hit.Piece == Pieces.Legwear)).ToList();
        foreach (var p in parts)
            words[p] = words[p].Where(h => !ExplainedElsewhere(h.Hit, p, parts) && !NamesTheSlot(h.Hit, p.Socket)
                                        && !(p.Socket == Socket.Legs && h.Hit.Piece == Pieces.Legwear && legwearElsewhere.Any(q => q != p))).ToList();

        // Mod-level names (mod name, tags, the user's folder) speak for the part they suit best.
        var modWords    = new Dictionary<PartFacts, List<(WordHit Hit, float Weight, string Source)>>();
        var folderWords = new Dictionary<PartFacts, List<(WordHit Hit, float Weight, string Source)>>();
        foreach (var p in parts)
        {
            modWords[p]    = new();
            folderWords[p] = new();
        }

        var author = Words.NameTokens(facts.Author);
        var modHits = Words.Scan(facts.Name, author).Select(h => (h, Tuning.Votes.WordModName, "mod"))
            .Concat(facts.Tags.SelectMany(t => Words.Scan(t, author)).Select(h => (h, Tuning.Votes.WordTag, "tag")));
        foreach (var (hit, weight, source) in modHits)
        {
            // A word that only names a kind ("wings") goes to every part; the kind is kept only where it fits the shelf.
            if (hit.Piece.Length == 0)
                foreach (var p in parts)
                    modWords[p].Add((hit, weight, source));
            else if (BestFor(hit, parts, readings, words) is { } owner && !NamesTheSlot(hit, owner.Socket))
            {
                // The same model offered in several slots is one thing: the word speaks for each copy.
                foreach (var q in parts.Where(q => q == owner || SameModel(q, owner)))
                    modWords[q].Add((hit, weight, source));
            }
        }
        // The user's folder: a folder that only repeats the slot (a coat worn on the wrist filed under "Bracer") says
        // where it is worn, not what it is: no witness.
        // A folder that names two shelves ("Tails & Ears") goes to every part: it supports whichever of them the others name.
        var folderHits = Words.Scan(facts.SortFolder.Replace('/', '|').Replace('\\', '|'));
        var twoShelves = folderHits.Select(h => h.Piece).Where(x => x.Length > 0).Distinct().Count() >= 2;
        foreach (var hit in folderHits)
            foreach (var owner in twoShelves ? parts.Where(q => hit.Piece.Length > 0) : BestFor(hit, parts, readings, words) is { } o ? new[] { o } : Array.Empty<PartFacts>())
                if (twoShelves || hit.Piece != SlotPrior(owner.Socket) && !NamesTheSlot(hit with { Alone = true }, owner.Socket))
                    folderWords[owner].Add((hit, Tuning.Votes.WordFolder, "folder"));

        foreach (var p in parts)
        {
            var hits = words[p].Concat(modWords[p]).ToList();
            result.Parts.Add(GamePaths.IsGear(p.Socket)
                ? DecideGear(facts, p, readings[p], hits, folderWords[p])
                : DecidePart(facts, p, readings[p], hits, folderWords[p]));
        }

        if (bare.Count > 0)
            MergeBody(result, facts, bare);
        JoinSculptTextures(result, facts);
        OneThingOneAnswer(result);
        foreach (var p in result.Parts) p.Bodies = BodiesFor(facts, result, p);
        return result;
    }

    /// <summary>
    /// The same model offered through several gear slots is one thing (brief 9): identical copies get one shelf, kind and
    /// confidence (that of the copy on its own slot's shelf, else the surest); near-identical copies on one shelf get one
    /// kind (a top offered through an accessory slot is outerwear for every copy). Body sockets keep their own homes.
    /// </summary>
    private static void OneThingOneAnswer(ModDetection result)
    {
        var gear = result.Parts.Where(p => GamePaths.IsGear(p.Socket) && p.Signature.Length == SignatureLength).ToList();
        if (gear.Count < 2)
            return;

        static PartInfo Leader(IEnumerable<PartInfo> copies)
            => copies.OrderByDescending(p => p.Piece == SlotPrior(p.Socket) && p.Confidence >= DetectorThresholds.Unsure)
                     .ThenByDescending(p => p.Confidence).ThenBy(p => p.Socket).First();

        foreach (var group in Groups(gear, SameThing.Identical))
        {
            var lead = Leader(group);
            foreach (var p in group.Where(p => p != lead))
            {
                if (p.Piece != lead.Piece)
                {
                    p.Piece   = lead.Piece;
                    p.Guesses = p.Guesses.Where(g => g.Piece != lead.Piece)
                        .Prepend(new PieceGuess { Piece = lead.Piece, Score = MathF.Round(lead.Confidence, 3) }).Take(4).ToList();
                }

                p.Kind       = lead.Kind;
                p.Confidence = lead.Confidence;
            }
        }

        foreach (var group in Groups(gear, (a, b) => a.Piece == b.Piece && SameThing.Is(a, b)))
        {
            var lead = group[0].Piece == Pieces.Top && group.Any(p => IsAccessory(p.Socket))
                ? group.Where(p => IsAccessory(p.Socket)).OrderBy(p => p.Socket).First()
                : Leader(group);
            foreach (var p in group)
                p.Kind = lead.Kind;
        }
    }

    /// <summary>
    /// A sculpt's own face textures are the sculpt: in a mod with a face model, its face-texture part (the eyes, lashes,
    /// decals it installs with the face) joins the Face part, so a sculpt is one card. Unless the mod offers makeup
    /// choices of its own beside the sculpt: an option group that touches only face textures (not the face model) and
    /// offers at least two looks (options other than off / none / omit). Then the face textures stay a Makeup part.
    /// Face-path makeup options of a sculpt (textures for that face only) are part of the Face part already: they are
    /// options of the sculpt, not makeup that can be worn without it.
    /// </summary>
    private static void JoinSculptTextures(ModDetection result, ModFacts facts)
    {
        var faceFacts = facts.Parts.FirstOrDefault(p => p.Socket == Socket.Face && p.HasModel);
        var paintFacts = facts.Parts.FirstOrDefault(p => p.Socket == Socket.FaceTextures);
        var face  = result.Parts.FirstOrDefault(p => p.Socket == Socket.Face);
        var paint = result.Parts.FirstOrDefault(p => p.Socket == Socket.FaceTextures);
        if (faceFacts is null || paintFacts is null || face is null || paint is null)
            return;

        if (paintFacts.Groups.Where(g => !faceFacts.Groups.Contains(g)).Any(g => MakeupChoice(facts.Groups.FirstOrDefault(x => x.Name == g))))
            return;

        result.Parts.Remove(paint);
        face.Groups      = face.Groups.Concat(paint.Groups).Distinct().ToList();
        face.RaceCodes   = face.RaceCodes.Concat(paint.RaceCodes).Distinct().Order().ToList();
        face.TextureOnly = false;
    }

    /// <summary> An option group that offers at least two looks: its options other than off, none, omit, default… </summary>
    private static bool MakeupChoice(GroupFacts? g)
        => g is not null && g.Options.Count(o => !Words.Tokens(o).Any(t => t is "off" or "none" or "omit" or "disable" or "disabled" or "dont" or "no" or "remove" or "vanilla" or "default")) >= 2;

    /// <summary>
    /// The body mods a part is made for ("Rue", "Bibo+"…): what its option groups and options (also those that are off:
    /// a mod with sizes for several bodies is made for all of them), the author's folder names, the mod's name and its
    /// tags say. Only for what is worn on the body: the clothes slots, the body itself, and garments worn through an
    /// accessory slot. Material names do not count (most bodies share the same skin materials, "bibo" among them).
    /// </summary>
    public static List<string> BodiesFor(ModFacts facts, ModDetection result, PartInfo part)
    {
        var onBody = part.Socket is Socket.Body or Socket.Hands or Socket.Legs or Socket.Feet or Socket.BodyBase
                  || GamePaths.IsGear(part.Socket) && part.Piece is Pieces.Top or Pieces.Bottoms or Pieces.Legwear or Pieces.Shoes or Pieces.Gloves;
        if (!onBody)
            return new List<string>();

        // The body part speaks also for the bare gear models merged into it (rule 3).
        var sources = facts.Parts.Where(f => f.Socket == part.Socket
            || part.Socket == Socket.BodyBase && GamePaths.IsGear(f.Socket) && result.Parts.All(r => r.Socket != f.Socket));
        return BodyMods.InAll(sources.SelectMany(f => f.Texts).Where(t => t.Source != "material").Select(t => t.Text).Append(facts.Name).Concat(facts.Tags));
    }

    /// <summary> The connected groups (two or more) of parts that a relation links. </summary>
    private static List<List<PartInfo>> Groups(List<PartInfo> parts, Func<PartInfo, PartInfo, bool> linked)
    {
        var root = Enumerable.Range(0, parts.Count).ToArray();
        int Find(int i) => root[i] == i ? i : root[i] = Find(root[i]);
        for (var i = 0; i < parts.Count; ++i)
            for (var j = i + 1; j < parts.Count; ++j)
                if (linked(parts[i], parts[j]))
                    root[Find(i)] = Find(j);
        return Enumerable.Range(0, parts.Count).GroupBy(Find).Where(g => g.Count() > 1).Select(g => g.Select(i => parts[i]).ToList()).ToList();
    }

    /// <summary>
    /// Rule 6: a word standing alone that only names the slot ("Nose chain (Ears)"; in the user's folder, any "ears"
    /// for an ear-slot item) says where the thing is worn, not that it is a pair of animal ears.
    /// </summary>
    private static bool NamesTheSlot(WordHit hit, Socket socket)
        => hit.Alone && socket == Socket.Ears && hit.Word is "ear" or "ears";

    private static bool SameModel(PartFacts a, PartFacts b)
        => a.Model is { Area: > 0 } ma && b.Model is { Area: > 0 } mb && GamePaths.IsGear(a.Socket) && GamePaths.IsGear(b.Socket)
         && MathF.Abs(ma.Area - mb.Area) <= 0.01f * MathF.Max(ma.Area, mb.Area);

    /// <summary> Files one can see (textures, materials, models) and none of the helpers a body needs (skeletons, physics, shaders, animations). </summary>
    private static bool SeenFiles(PartFacts p)
        => p.Paths.Any(x => x.EndsWith(".tex", StringComparison.Ordinal) || x.EndsWith(".mtrl", StringComparison.Ordinal) || x.EndsWith(".mdl", StringComparison.Ordinal))
         && !p.Paths.Any(x => Path.GetExtension(x) is ".sklb" or ".skp" or ".phyb" or ".kdb" or ".pbd" or ".shpk" or ".pap" or ".tmb" or ".eid" or ".atch" or ".avfx");

    /// <summary>
    /// Facts of an older reader put a body's skin textures at made-up paths ("…/chara/bibo_mid_base.tex") into a helper
    /// Other part. When every path of it is such a texture, it is the skin: a texture-only body part, with the races
    /// its folders name.
    /// </summary>
    private static PartFacts AsSkin(PartFacts p)
    {
        if (p.Socket != Socket.Other || p.Paths.Count == 0 || p.OtherKinds.Keys.Any(k => k != "helper") || !p.Paths.All(GamePaths.IsSkinTexture))
            return p;
        return new PartFacts
        {
            Socket = Socket.BodyBase, PathCount = p.PathCount, Paths = p.Paths, Sets = p.Sets, Metas = p.Metas, Groups = p.Groups,
            Texts = p.Texts, Shaders = p.Shaders, FromCustomPaths = true,
            RaceCodes = p.Paths.SelectMany(GamePaths.SkinRaces).Distinct().Order().ToList(),
        };
    }

    /// <summary>
    /// A gear part whose model has no surface of its own, only bare skin; on the bare body's sets also one with a trace of
    /// surface (pubic hair, a seam, a piercing stud), but not hands with nails (those are nails).
    /// </summary>
    public static bool IsBareBody(PartFacts p)
        => GamePaths.IsGear(p.Socket) && p.Model is { SkinArea: > 0 } m
         && (m.Area <= 0 || OnBodySets(p) && p.Socket != Socket.Hands && m.Area <= Tuning.Gear.NearBareArea && m.Area <= Tuning.Gear.NearBareShare * m.SkinArea);

    /// <summary>
    /// The gear sets of the bare body: e0000 (what shows when a slot is empty) and the smallclothes (e0279, e9903). A body
    /// mod ships its shapes for the bare character there.
    /// </summary>
    public static bool OnBodySets(PartFacts p)
        => p.Paths.Any(x => x.StartsWith("chara/equipment/e0000/model/", StringComparison.Ordinal))
         || p.ModelSets.Count > 0 && p.ModelSets.All(s => s is 279 or 9903);

    private static bool ExplainedElsewhere(WordHit hit, PartFacts p, List<PartFacts> parts)
        => hit.Piece.Length > 0 && hit.Piece != SlotPrior(p.Socket)
         && parts.Any(q => q != p && GamePaths.IsGear(q.Socket) && SlotPrior(q.Socket) == hit.Piece);

    /// <summary>
    /// Rule 3: the bare-skin gear models join the mod's body part (socket BodyBase, shelf Bodies, kind "body"), with its
    /// skin textures when it has them: one part, not five.
    /// </summary>
    private static void MergeBody(ModDetection result, ModFacts facts, List<PartFacts> bare)
    {
        var body = result.Parts.FirstOrDefault(x => x.Socket == Socket.BodyBase);
        var races = bare.SelectMany(b => b.RaceCodes).ToList();
        var groups = bare.SelectMany(b => b.Groups).ToList();
        if (body is null)
        {
            body = new PartInfo
            {
                Mod        = facts.Mod,
                Socket     = Socket.BodyBase,
                Piece      = Pieces.Bodies,
                Confidence = Tuning.Gear.ConfConclusive,
                Guesses    = { new PieceGuess { Piece = Pieces.Bodies, Score = 1 } },
                Evidence   =
                {
                    new Evidence { Kind = EvidenceKind.Slot, Value = SlotPhrase(Socket.BodyBase), Piece = Pieces.Skin, Weight = 1 },
                    new Evidence { Kind = EvidenceKind.Body, Value = "only bare skin", Piece = Pieces.Bodies, Weight = 1 },
                    new Evidence { Kind = EvidenceKind.Words, Value = "no hint" },
                },
                Models = 0,
            };
            result.Parts.Add(body);
        }
        else
        {
            body.Piece      = Pieces.Bodies;
            body.Confidence = Math.Max(body.Confidence, Tuning.Gear.ConfConclusive);
            body.Guesses    = body.Guesses.Where(g => g.Piece != Pieces.Bodies)
                .Prepend(new PieceGuess { Piece = Pieces.Bodies, Score = 1 }).Take(4).ToList();
            var e = body.Evidence.FirstOrDefault(x => x.Kind == EvidenceKind.Body);
            if (e is not null && e.Value == "no hint")
            {
                e.Value  = "only bare skin";
                e.Piece  = Pieces.Bodies;
                e.Weight = 1;
            }
        }

        body.Kind      = "body";
        body.Models    = (body.TextureOnly ? 0 : body.Models) + bare.Count;
        body.TextureOnly = false;
        body.RaceCodes = body.RaceCodes.Concat(races).Distinct().Order().ToList();
        body.Groups    = body.Groups.Concat(groups).Distinct().ToList();
        result.Parts.Sort((a, b) => a.Socket.CompareTo(b.Socket));
    }

    private static bool IsSupportOnly(PartFacts p, ModFacts facts)
        => p.Socket == Socket.Other
         && p.OtherKinds.Keys.All(k => k is "helper")
         && facts.Parts.Any(x => x.Socket != Socket.Other && x.Socket != Socket.None);

    /// <summary> The part a mod-level word belongs to: the one whose slot, body and own words suit its shelf best. </summary>
    private static PartFacts? BestFor(WordHit hit, List<PartFacts> parts, Dictionary<PartFacts, BodyReading> readings,
        Dictionary<PartFacts, List<(WordHit Hit, float Weight, string Source)>> words)
    {
        if (parts.Count == 1)
            return parts[0];
        if (hit.Piece.Length == 0)
            return null;

        PartFacts? best = null;
        var bestScore = 0f;
        foreach (var p in parts)
        {
            var score = (SlotPrior(p.Socket) == hit.Piece ? 1f : 0f)
                      + readings[p].Opinion.Votes.GetValueOrDefault(hit.Piece) * readings[p].Opinion.Strength
                      + (words[p].Any(w => w.Hit.Piece == hit.Piece) ? 0.5f : 0f)
                      // A model whose shape leaves room for that shelf (rule 6: the word belongs to the part it fits).
                      + (readings[p].Gear?.Candidates.Contains(hit.Piece) == true ? 1f : 0f);
            if (score > bestScore)
            {
                bestScore = score;
                best      = p;
            }
        }

        return best;
    }

    // ------------------------------------------------------------------ one part

    private static PartInfo DecidePart(ModFacts facts, PartFacts p, BodyReading body, List<(WordHit Hit, float Weight, string Source)> wordHits,
        List<(WordHit Hit, float Weight, string Source)> folderHits)
    {
        var v = Tuning.Votes;
        var slot     = SlotOpinion(p);
        var words    = WordsOpinion(wordHits, EvidenceKind.Words);
        var folder   = WordsOpinion(folderHits, EvidenceKind.Folder);
        var material = MaterialOpinion(p);
        var bodyOpinion = body.Opinion;
        if (IsBodySocket(p.Socket))
        {
            // Hair, tail and ear-shape sockets are the body itself: they never go to a clothes or jewellery shelf.
            // Between the body's own shelves the body still decides (ears on top of the head, a tail at the hips).
            bodyOpinion = BodyShelvesOnly(bodyOpinion, p.Socket);
            words       = BodyShelvesOnly(words, p.Socket);
            folder      = BodyShelvesOnly(folder, p.Socket);
            material    = BodyShelvesOnly(material, p.Socket);
        }

        slot.Weight     = v.Slot;
        bodyOpinion.Weight = v.Body;
        words.Weight    = v.Words;
        folder.Weight   = v.Folder;
        material.Weight = v.Material;

        // A folder that names two shelves ("Tails & Ears") supports whichever of them the others name, never argues.
        if (folder.Votes.Count(kv => kv.Value > 0) >= 2)
        {
            var others = new Dictionary<string, float>();
            foreach (var w in new[] { slot, bodyOpinion, words, material }.Where(w => w.Speaks))
                foreach (var (piece, vote) in w.Votes)
                    if (piece.Length > 0)
                        others[piece] = others.GetValueOrDefault(piece) + w.Weight * w.Strength * vote;
            var named = others.Count > 0 ? others.MaxBy(kv => kv.Value).Key : SlotPrior(p.Socket);
            folder = folder.Votes.ContainsKey(named)
                ? new Opinion { Kind = folder.Kind, Votes = { [named] = 1 }, Strength = folder.Strength, Phrase = folder.Phrase, Word = folder.Word, Weight = folder.Weight }
                : new Opinion { Kind = folder.Kind };
        }

        var witnesses = new[] { slot, bodyOpinion, words, folder, material };

        // Add up.
        var scores = new Dictionary<string, float>();
        foreach (var w in witnesses.Where(w => w.Speaks))
            foreach (var (piece, vote) in w.Votes)
                if (piece.Length > 0)
                    scores[piece] = scores.GetValueOrDefault(piece) + w.Weight * w.Strength * vote;
        if (scores.Count == 0)
            scores[SlotPrior(p.Socket)] = 0.01f;

        var ordered = scores.OrderByDescending(kv => kv.Value).ThenBy(kv => kv.Key == SlotPrior(p.Socket) ? 0 : 1).ToList();
        var best    = ordered[0].Key;
        var total   = ordered.Sum(kv => kv.Value);
        var margin  = total > 0 ? (ordered[0].Value - (ordered.Count > 1 ? ordered[1].Value : 0)) / total : 0;

        var confidence = Confidence(p, best, slot, bodyOpinion, words, folder, material, margin);

        // "Ears and tail" in one model: a body socket's own home wins whenever the model has any part there, however
        // small (an ear-shape model with a big tail and small ears is ears; a tail-socket model with ears on it is a
        // tail). Only when nothing of it is at home does the body move it (an ear-shape model at the hips is a tail).
        if (p.Socket is Socket.EarShape or Socket.Tail && body.Shares.Count > 0)
        {
            var home = p.Socket == Socket.EarShape ? HeadRegions : BodyRegions.Tail;
            if (Share(body, home) >= Tuning.Gear.BodyHomeAnyShare)
            {
                best       = SlotPrior(p.Socket);
                confidence = Math.Max(confidence, Tuning.Gear.ConfInReach);
            }
            else if (p.Socket == Socket.EarShape && bodyOpinion.Speaks && bodyOpinion.Top == Pieces.Tail)
            {
                best       = Pieces.Tail;
                confidence = Math.Max(confidence, Tuning.Gear.ConfCoverage);
            }
            else if (p.Socket == Socket.Tail && Share(body, HeadRegions) >= Tuning.Gear.TailOnHeadShare * Math.Max(body.Shares.Values.Sum(), 1e-6f))
            {
                // No tail bones at all and sitting on the head: ears.
                best       = Pieces.Ears;
                confidence = Math.Max(confidence, Tuning.Gear.ConfCoverage);
            }
        }

        // Rule 4: a slot that can only mean one thing is sure alone (skin textures, face textures, a face model), and
        // rule 5: weapons and files outside every slot are never clothing. Stray words do not unsettle them.
        var conclusive = p.Socket is Socket.FaceTextures or Socket.Face or Socket.BodyBase;
        var notWorn    = p.Socket is Socket.Other or Socket.MainHand or Socket.OffHand;
        if (conclusive)
        {
            // The body with models of its own is a body (Bodies); skin textures alone, tattoos, freckles are Skin. Words
            // never make a body: a base's name in a mod's name only says what it was made for.
            best       = p.Socket == Socket.BodyBase && p.HasModel ? Pieces.Bodies : SlotPrior(p.Socket);
            confidence = Math.Max(confidence, Tuning.Gear.ConfConclusive);
        }
        else if (p.Socket == Socket.Other && p.OtherKinds.Count > 0 && p.OtherKinds.Keys.All(k => k is "helper" or "custom") && SeenFiles(p)
                 && folder.Speaks && folder.Top is { Length: > 0 } named && named != Pieces.Other)
        {
            // Files the reader can only call helpers (textures, materials, models at paths it does not know), in a folder
            // of the user's that names a shelf: the folder wins. Skeletons, physics and shaders stay helpers.
            best       = named;
            confidence = v.ConfTwoOverSlot;
        }
        else if (notWorn && !(p.Socket == Socket.Other && p.OtherKinds.ContainsKey("custom") && !Pieces.IsOutfitPiece(best)))
        {
            best       = Pieces.Other;
            confidence = p.OtherKinds.ContainsKey("custom") ? Math.Min(confidence, v.ConfAlone) : v.ConfNotWorn;
        }

        if (ordered[0].Key != best)
        {
            if (!scores.ContainsKey(best))
                scores[best] = 0;
            var keep = best;
            ordered = scores.OrderByDescending(kv => kv.Key == keep).ThenByDescending(kv => kv.Value).ToList();
            total   = Math.Max(ordered.Sum(kv => kv.Value), 1e-6f);
        }

        var info = new PartInfo
        {
            Mod        = facts.Mod,
            Socket     = p.Socket,
            Piece      = best,
            Confidence = MathF.Round(confidence, 3),
            Guesses    = ordered.Take(4).Select(kv => new PieceGuess { Piece = kv.Key, Score = MathF.Round(total > 0 ? kv.Value / total : 1, 3) }).ToList(),
            Covers     = body.Covers,
            Signature  = Signature(p, body),
            TextureOnly = !p.HasModel && p.SwappedFrom.Count == 0 && p.Socket is not Socket.Other,
            RaceCodes  = p.Socket is Socket.Other or Socket.MainHand or Socket.OffHand ? new List<int>() : p.RaceCodes.ToList(),
            Groups     = p.Groups.ToList(),
            Models     = Math.Max(1, p.ModelSets.Count),
        };

        info.Kind = best == Pieces.Bodies ? "body" : KindOf(p, best, body, wordHits, info.Confidence);

        // Evidence: the three witnesses always (silent ones say "no hint"), folder and material when they mattered.
        info.Evidence.Add(Evidence(slot));
        info.Evidence.Add(Evidence(bodyOpinion));
        info.Evidence.Add(Evidence(words));
        if (folder.Speaks)
            info.Evidence.Add(Evidence(folder));
        if (material.Speaks)
            info.Evidence.Add(Evidence(material));
        return info;
    }

    private static bool IsBodySocket(Socket s)
        => s is Socket.Hair or Socket.Tail or Socket.EarShape;

    /// <summary>
    /// A witness's opinion among the body's own shelves (hair, face, ears, tail, skin). For tail and ear-shape models a
    /// vote for what sits at the hips is the tail and one for the top of the head is the ears; other votes drop out.
    /// </summary>
    private static Opinion BodyShelvesOnly(Opinion o, Socket socket)
    {
        var r = new Opinion { Kind = o.Kind, Strength = o.Strength, Phrase = o.Phrase, Word = o.Word, Weight = o.Weight };
        foreach (var (piece, vote) in o.Votes)
        {
            var mapped = piece switch
            {
                Pieces.Hair or Pieces.Face or Pieces.Ears or Pieces.Tail or Pieces.Skin => piece,
                Pieces.Bottoms when socket is Socket.Tail or Socket.EarShape            => Pieces.Tail,
                Pieces.Head when socket is Socket.Tail or Socket.EarShape               => Pieces.Ears,
                Pieces.Head when socket == Socket.Hair                                  => Pieces.Hair,
                _                                                                       => string.Empty,
            };
            if (mapped.Length > 0)
                r.Votes[mapped] = r.Votes.GetValueOrDefault(mapped) + vote;
        }

        if (r.Votes.Count == 0)
            r.Strength = 0;
        return r;
    }

    private static Evidence Evidence(Opinion o)
        => o.Speaks
            ? new Evidence { Kind = o.Kind, Value = o.Phrase, Word = o.Word, Piece = o.Top, Weight = MathF.Round(o.Strength * o.Weight, 3) }
            : o.Kind == EvidenceKind.Body && o.Phrase.Length > 0
                ? new Evidence { Kind = o.Kind, Value = o.Phrase, Word = o.Word }
                : new Evidence { Kind = o.Kind, Value = "no hint" };

    /// <summary>
    /// How sure: two witnesses agreeing on the best shelf sort it; a strong witness pointing elsewhere (other than the
    /// slot, which mods borrow all the time) sends it to To sort; one witness alone is not enough except where the
    /// socket says it all (animations, a hairstyle's textures, a retexture of a game item).
    /// </summary>
    private static float Confidence(PartFacts p, string best, Opinion slot, Opinion body, Opinion words, Opinion folder, Opinion material, float margin)
    {
        var v = Tuning.Votes;
        var witnesses = new[] { slot, body, words, folder, material };
        var agree = witnesses.Where(w => w.Speaks && w.Top == best).ToList();
        var against = witnesses.Where(w => w.Speaks && w.Top != best && w.Strength >= v.StrongDisagree).ToList();
        var bonus = v.ConfMarginBonus * Math.Clamp(margin, 0, 1);

        // Not worn: the files say what they are.
        if (p.Socket == Socket.Other)
        {
            if (p.OtherKinds.ContainsKey("custom"))
                return Math.Min(v.ConfAlone, v.ConfConflict);
            return best == Pieces.Other ? v.ConfNotWorn : v.ConfConflict;
        }

        if (p.Socket is Socket.MainHand or Socket.OffHand)
            return best == Pieces.Other ? v.ConfNotWorn : v.ConfConflict;

        var nonSlotAgainst = against.Count(w => w.Kind != EvidenceKind.Slot);

        // Three agreeing outvote one word or folder against them ("Top Knot" is a hairstyle); two do not.
        if (agree.Count >= 3 && nonSlotAgainst == 1)
            return v.ConfTwoAgree;
        if (agree.Count >= 2 && nonSlotAgainst > 0)
            return v.ConfConflict;
        if (agree.Count >= 3)
            return Math.Min(0.97f, v.ConfThreeAgree + bonus * 0.5f);
        if (agree.Count == 2)
            return Math.Min(0.95f, (against.Count > 0 ? v.ConfTwoOverSlot : v.ConfTwoAgree) + bonus);

        // A clear reading of where the model sits that its slot's garment can never be (a tail on the head, shoes up to
        // the hips, gloves only on the fingertips) outweighs the borrowed slot on its own.
        if (agree.Count == 1 && agree[0] == body && against.All(w => w.Kind == EvidenceKind.Slot) && IsDecisive(body, SlotPrior(p.Socket)))
            return v.ConfBodyOverSlot + bonus * 0.5f;

        if (agree.Count == 1 && against.Count == 0)
        {
            var only = agree[0];
            if (only.Kind == EvidenceKind.Slot)
            {
                // The game's own body parts are what their slot says unless a model sits elsewhere (body would speak).
                if (p.FromCustomPaths)
                    return v.ConfAlone;
                if (p.Socket is Socket.Hair or Socket.Face or Socket.FaceTextures or Socket.BodyBase or Socket.EarShape)
                    return v.ConfOwnPart + bonus;
                if (p.Socket == Socket.Tail && !p.HasModel)
                    return v.ConfOwnPart + bonus;

                // Textures for a game item: game items sit where their slot says.
                if (!p.HasModel && GamePaths.IsGear(p.Socket))
                    return v.ConfGameItem;
            }

            if (only.Kind == EvidenceKind.Folder)
                return v.ConfTwoOverSlot;
            return v.ConfAlone;
        }

        return agree.Count == 0 ? v.ConfNothing : v.ConfConflict;
    }

    /// <summary> A body reading that names one shelf clearly and gives the slot's own shelf nothing. </summary>
    private static bool IsDecisive(Opinion body, string slotPiece)
        => body.Speaks && body.Strength >= Tuning.Votes.DecisiveBody && body.Votes.GetValueOrDefault(body.Top) >= 0.9f
         && body.Votes.GetValueOrDefault(slotPiece) <= 0;

    // ------------------------------------------------------------------ witness 1: the slot

    /// <summary> The shelf a socket means when nothing else is known. </summary>
    public static string SlotPrior(Socket s)
        => s switch
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
            Socket.FaceTextures => Pieces.Makeup,
            Socket.Tail         => Pieces.Tail,
            Socket.EarShape     => Pieces.Ears,
            Socket.BodyBase     => Pieces.Skin,
            _                   => Pieces.Other,
        };

    /// <summary> The slot in plain words, as "Why is it here?" shows it. </summary>
    public static string SlotPhrase(Socket s)
        => s switch
        {
            Socket.Head         => "headwear",
            Socket.Body         => "top",
            Socket.Hands        => "gloves",
            Socket.Legs         => "bottoms",
            Socket.Feet         => "shoes",
            Socket.Ears         => "earrings",
            Socket.Neck         => "necklace",
            Socket.Wrists       => "bracelets",
            Socket.Rings        => "rings",
            Socket.Hair         => "hairstyle",
            Socket.Face         => "face",
            Socket.FaceTextures => "face textures",
            Socket.Tail         => "tail",
            Socket.EarShape     => "ears",
            Socket.BodyBase     => "body",
            Socket.MainHand     => "weapon",
            Socket.OffHand      => "weapon",
            _                   => "not worn",
        };

    private static Opinion SlotOpinion(PartFacts p)
    {
        var o = new Opinion { Kind = EvidenceKind.Slot, Strength = 1, Phrase = SlotPhrase(p.Socket) };
        o.Votes[SlotPrior(p.Socket)] = 1;
        if (p.FromCustomPaths && p.Socket == Socket.BodyBase)
        {
            // Not a game slot: made-up paths that name a body or skin.
            o.Phrase   = "files for a body's skin";
            o.Strength = 0.5f;
        }
        if (p.Socket == Socket.Other)
        {
            var kind = OtherKind(p);
            if (kind is "custom")
            {
                o.Phrase   = "files at made-up paths";
                o.Strength = 0.5f;
            }
            else if (kind.Length > 0)
            {
                o.Phrase = kind;
            }
        }

        return o;
    }

    // ------------------------------------------------------------------ witness 2: the body

    /// <summary> Where the part's model sits, and which shelves that suggests for its socket. </summary>
    public static BodyReading ReadBody(PartFacts p)
    {
        var r = new BodyReading();
        var m = p.Model;

        // Weapons live in their own space (the grip at the origin) and "other" files are not worn: nothing to measure.
        if (p.Socket is Socket.MainHand or Socket.OffHand or Socket.Other)
            return r;

        if (m is null && p.SwappedFrom.Count > 0)
        {
            // A game model swapped in: it sits where its own slot sits.
            var from = p.SwappedFrom[0];
            r.Opinion.Phrase = "a game " + SlotPhrase(from) + " model";
            r.Opinion.Word   = SlotPhrase(from);
            if (from == p.Socket)
            {
                r.Opinion.Votes[SlotPrior(p.Socket)] = 1;
                r.Opinion.Strength = 0.6f;
            }
            else
            {
                var piece = SlotPrior(from);
                if (p.Socket == Socket.Tail && from == Socket.Head)
                    piece = Pieces.Ears;
                r.Opinion.Votes[piece] = 1;
                r.Opinion.Strength = 0.6f;
            }

            return r;
        }

        if (m is null || m.Area <= 0 || m.Bones.Count == 0)
        {
            if (p.HasModel && m is null)
                r.Opinion.Phrase = "model could not be read";
            else if (m is not null && m.Area <= 0)
                r.Opinion.Phrase = "only bare skin";
            return r;
        }

        var (shares, away) = Geometry.Regions(m);
        r.Shares = shares;
        r.Away   = away;
        var t = Tuning.Body;
        foreach (var (region, share) in shares)
            if (share >= t.CoverMinShare)
                r.Covers |= region;

        var fingerArea = m.Bones.Where(b => BodyMap.OfBone(b.Name) == BodyRegions.Fingers).Sum(b => b.Area);
        if (fingerArea > 0)
        {
            r.TipShare  = m.Bones.Where(b => BodyMap.IsFingerTip(b.Name)).Sum(b => b.Area) / fingerArea;
            r.BaseShare = m.Bones.Where(b => BodyMap.IsFingerBase(b.Name)).Sum(b => b.Area) / fingerArea;
            r.Fingers   = m.Bones.Where(b => BodyMap.OfBone(b.Name) == BodyRegions.Fingers && b.Area >= fingerArea * 0.05f)
                .Select(b => BodyMap.FingerOf(b.Name)).Distinct().Count();
        }

        var s = BodyMap.Scale(m.Race);
        r.ScaledArea = m.Area / (s * s);
        r.Heels = HasHeels(m);

        var placed = 1 - away;
        if (GamePaths.IsGear(p.Socket))
        {
            r.Gear = ReadGear(p.Socket, r, placed);
            return r;
        }

        if (p.Socket is Socket.Face)
        {
            // A face model is the face, sculpts with the neck and the shoulders included.
            Read(Socket.Face, r, placed);
            r.Opinion.Votes.Clear();
            r.Opinion.Votes[Pieces.Face] = 1;
            r.Opinion.Phrase = "the face";
            return r;
        }

        if (p.Socket == Socket.BodyBase)
        {
            // A body model is the body: where it sits only names the place, the shelf is Skin.
            Read(Socket.Body, r, placed);
            r.Opinion.Votes.Clear();
            r.Opinion.Votes[Pieces.Skin] = 1;
            r.Opinion.Phrase = "the body";
            return r;
        }

        Read(p.Socket, r, placed);
        if (placed >= 0.35f)
            r.Opinion.Strength = Math.Clamp(r.Opinion.Strength * Math.Min(1, placed / 0.8f), 0, 1);
        return r;
    }

    /// <summary> The rules from regions to shelves (see docs/reports/detection.md, "Body rules"). </summary>
    private static void Read(Socket socket, BodyReading r, float placed)
    {
        var o = r.Opinion;
        if (placed < 0.35f)
        {
            // Mostly beside the body (a pet that follows you, a floating effect): not the garment its slot names.
            o.Phrase = "away from the body";
            if (socket is not (Socket.Other or Socket.MainHand or Socket.OffHand))
            {
                o.Votes[Pieces.Other] = 1;
                o.Strength = (1 - placed) * 0.7f;
            }

            return;
        }

        float N(BodyRegions x) => r[x] / Math.Max(placed, 1e-6f);
        var headTop = N(BodyRegions.HeadTop);
        var face    = N(BodyRegions.Face);
        var ears    = N(BodyRegions.Ears);
        var head    = headTop + face + ears;
        var neck    = N(BodyRegions.Neck);
        var chest   = N(BodyRegions.Chest);
        var back    = N(BodyRegions.Back);
        var waist   = N(BodyRegions.Waist);
        var torso   = chest + back + waist;
        var upper   = N(BodyRegions.UpperArms);
        var fore    = N(BodyRegions.Forearms);
        var hands   = N(BodyRegions.Hands);
        var fingers = N(BodyRegions.Fingers);
        var hips    = N(BodyRegions.Hips) + N(BodyRegions.Skirt);
        var thighs  = N(BodyRegions.Thighs);
        var knees   = N(BodyRegions.Knees);
        var calves  = N(BodyRegions.Calves);
        var feet    = N(BodyRegions.Feet) + N(BodyRegions.Toes);
        var tail    = N(BodyRegions.Tail);
        var t = Tuning.Body;

        void Say(string phrase, float strength, params (string Piece, float Vote)[] votes)
        {
            o.Phrase   = phrase;
            o.Strength = Math.Clamp(strength, 0, 1);
            foreach (var (piece, vote) in votes)
                o.Votes[piece] = o.Votes.GetValueOrDefault(piece) + vote;
        }

        if (tail >= 0.5f)
        {
            Say("tail", tail, (Pieces.Tail, 1));
            return;
        }

        if (head >= 0.55f)
        {
            var place = headTop >= face && headTop >= ears ? BodyRegions.HeadTop : face >= ears ? BodyRegions.Face : BodyRegions.Ears;
            var share = place == BodyRegions.HeadTop ? headTop : place == BodyRegions.Face ? face : ears;
            var strength = head * (0.5f + 0.5f * share / head);
            switch (place)
            {
                case BodyRegions.HeadTop:
                    switch (socket)
                    {
                        case Socket.Head:                         Say("top of head", strength, (Pieces.Head, 1)); break;
                        case Socket.Tail or Socket.EarShape:      Say("top of head", strength, (Pieces.Ears, 1)); break;
                        case Socket.Hair:                         Say("top of head", strength, (Pieces.Hair, 1)); break;
                        case Socket.Face or Socket.FaceTextures:  Say("head", strength, (Pieces.Face, 1)); break;
                        case Socket.Ears:                         Say("top of head", strength * 0.8f, (Pieces.Ears, 0.5f), (Pieces.Head, 0.5f)); break;
                        default:                                  Say("top of head", strength, (Pieces.Head, 0.7f), (Pieces.Ears, 0.3f)); break;
                    }

                    return;
                case BodyRegions.Face:
                    switch (socket)
                    {
                        case Socket.Head:                         Say("face", strength, (Pieces.Head, 1)); break;
                        case Socket.Face or Socket.FaceTextures:  Say("face", strength, (Pieces.Face, 1)); break;
                        case Socket.Hair:                         Say("face", strength, (Pieces.Hair, 1)); break;
                        default:                                  Say("face", strength * 0.8f, (Pieces.Face, 0.6f), (Pieces.Head, 0.4f)); break;
                    }

                    return;
                default:
                    switch (socket)
                    {
                        case Socket.Ears:                         Say("ears", strength, (Pieces.Earrings, 1)); break;
                        case Socket.Tail or Socket.EarShape:      Say("ears", strength, (Pieces.Ears, 1)); break;
                        case Socket.Hair:                         Say("ears", strength, (Pieces.Hair, 1)); break;
                        case Socket.Face or Socket.FaceTextures:  Say("ears", strength, (Pieces.Face, 1)); break;
                        case Socket.Head:                         Say("ears", strength * 0.8f, (Pieces.Head, 0.5f), (Pieces.Earrings, 0.5f)); break;
                        default:                                  Say("ears", strength, (Pieces.Earrings, 0.8f), (Pieces.Ears, 0.2f)); break;
                    }

                    return;
            }
        }

        // Necklaces: the neck and the front of the chest (a pendant), not the back, not the arms.
        if (neck >= 0.25f && neck + chest >= 0.6f && back < 0.2f && upper + fore < 0.1f)
        {
            Say("neck", neck + chest, (Pieces.Necklace, 1));
            return;
        }

        var arm = hands + fingers + fore;
        if (arm >= 0.6f && torso < 0.15f)
        {
            if (fingers >= 0.6f && hands < 0.25f)
            {
                if (r.TipShare >= 0.6f)
                    Say("fingertips", fingers, (Pieces.Nails, 1));
                else if (r.BaseShare >= 0.6f && r.Fingers <= 4)
                    Say("fingers", fingers, (Pieces.Rings, 1));
                else
                    Say("fingers", fingers * 0.6f, (Pieces.Nails, 0.55f), (Pieces.Rings, 0.45f));
                return;
            }

            if (r.ScaledArea >= t.GloveMinArea && hands >= 0.15f)
            {
                Say("hands", arm, (Pieces.Gloves, 1));
                return;
            }

            if (r.ScaledArea < t.GloveMinArea && fore + hands >= 0.6f && fingers < 0.2f)
            {
                Say("wrists", fore + hands, (Pieces.Bracelets, 1));
                return;
            }

            if (fore >= 0.6f)
            {
                // Long cuffs or arm warmers: in the bracelet slot they are cuffs, elsewhere they read as gloves.
                if (socket == Socket.Wrists)
                    Say("forearms", fore * 0.7f, (Pieces.Bracelets, 0.7f), (Pieces.Gloves, 0.3f));
                else
                    Say("forearms", fore * 0.7f, (Pieces.Gloves, 0.6f), (Pieces.Bracelets, 0.4f));
                return;
            }

            Say("hands", arm * 0.6f, (Pieces.Gloves, 0.6f), (Pieces.Bracelets, 0.2f), (Pieces.Nails, 0.2f));
            return;
        }

        if (upper + fore >= 0.6f && torso < 0.2f)
        {
            Say("arms", (upper + fore) * 0.6f, (Pieces.Gloves, 0.5f), (Pieces.Bracelets, 0.3f), (Pieces.Top, 0.2f));
            return;
        }

        var legs = thighs + knees + calves;
        if (torso < 0.2f && legs + feet + hips >= 0.6f)
        {
            if (feet >= 0.45f && thighs < 0.08f)
            {
                Say("feet", feet + calves, (Pieces.Shoes, 1));
                return;
            }

            if (hips >= 0.25f)
            {
                if (calves + feet >= 0.2f && thighs >= 0.15f)
                {
                    if (socket == Socket.Feet)
                        Say("hips and legs", hips + legs + feet, (Pieces.Legwear, 1));
                    else
                        Say("hips and legs", (hips + legs + feet) * 0.8f, (Pieces.Bottoms, 0.6f), (Pieces.Legwear, 0.4f));
                    return;
                }

                if (socket == Socket.Feet)
                    Say("hips", hips + legs, (Pieces.Bottoms, 0.6f), (Pieces.Legwear, 0.4f));
                else
                    Say("hips", hips + legs, (Pieces.Bottoms, 1));
                return;
            }

            if (legs + feet >= 0.6f)
            {
                if (thighs >= 0.12f)
                {
                    if (socket == Socket.Legs)
                        Say("legs", legs + feet, (Pieces.Legwear, 0.6f), (Pieces.Bottoms, 0.4f));
                    else if (r.Heels)
                        Say("legs", (legs + feet) * 0.7f, (Pieces.Shoes, 0.6f), (Pieces.Legwear, 0.4f)); // thigh-high boots
                    else
                        Say("legs", legs + feet, (Pieces.Legwear, 1));
                }
                else if (feet >= 0.2f)
                {
                    // Socks or boots: the shape cannot tell, the words must.
                    Say("lower legs and feet", (legs + feet) * 0.6f, (Pieces.Shoes, socket == Socket.Feet ? 0.55f : 0.45f), (Pieces.Legwear, socket == Socket.Feet ? 0.45f : 0.55f));
                }
                else
                {
                    Say("lower legs", legs * 0.7f, (Pieces.Legwear, 0.7f), (Pieces.Shoes, 0.3f));
                }

                return;
            }
        }

        if (torso >= 0.35f || torso + upper + neck >= 0.6f)
        {
            var lower = hips + thighs;
            if (back >= 0.5f && chest < 0.15f && upper + fore < 0.1f)
            {
                // Wings, capes, things on the back: not a clear piece of clothing.
                Say("back", 0.3f, (Pieces.Top, 0.5f));
                return;
            }

            if (lower >= 0.25f)
            {
                if (socket == Socket.Legs)
                    Say("upper body and legs", torso + lower, (Pieces.Top, 0.6f), (Pieces.Bottoms, 0.4f));
                else
                    Say("upper body and legs", torso + lower, (Pieces.Top, 1));
                return;
            }

            Say("upper body", torso + upper + neck, (Pieces.Top, 1));
            return;
        }

        // Spread over several places: vote by where most of it is, without much strength.
        var map = new (float Share, string Piece, string Phrase)[]
        {
            (head, socket == Socket.Head ? Pieces.Head : Pieces.Face, "head"),
            (neck, Pieces.Necklace, "neck"),
            (torso + upper, Pieces.Top, "upper body"),
            (hands + fingers + fore, Pieces.Gloves, "hands"),
            (hips + thighs, Pieces.Bottoms, "hips"),
            (knees + calves, Pieces.Legwear, "legs"),
            (feet, Pieces.Shoes, "feet"),
            (tail, Pieces.Tail, "tail"),
        };
        var top = map.MaxBy(x => x.Share);
        Say("spread over the body", top.Share * 0.5f, (top.Piece, 1));
    }

    // ------------------------------------------------------------------ witness 3: the words

    /// <summary> The part's own names (groups, options, files, materials) as weighted word hits. </summary>
    private static List<(WordHit Hit, float Weight, string Source)> PartWords(PartFacts p, ModFacts facts)
    {
        var v = Tuning.Votes;
        var author = Words.NameTokens(facts.Author);
        var list = new List<(WordHit, float, string)>();
        foreach (var t in p.Texts)
        {
            var (weight, file) = t.Source switch
            {
                "group"     => (v.WordGroup, false),
                "offgroup"  => (v.WordOffOption, false),
                "option"    => (v.WordOption, false),
                "offoption" => (v.WordOffOption, false),
                "toggle"    => (v.WordToggle, false),
                "offtoggle" => (v.WordOffToggle, false),
                "file"      => (v.WordFile, true),
                "material"  => (v.WordMaterial, true),
                _           => (v.WordFile, true),
            };
            foreach (var hit in Words.Scan(t.Text, author, file))
                list.Add((hit, weight, t.Source));
        }

        return list;
    }

    private static Opinion WordsOpinion(List<(WordHit Hit, float Weight, string Source)> hits, EvidenceKind kind)
    {
        var o = new Opinion { Kind = kind };
        var miss = new Dictionary<string, float>();
        var fileMiss = new Dictionary<string, float>();
        var bestWord = new Dictionary<string, (string Word, float W)>();
        foreach (var (hit, weight, source) in hits)
        {
            if (hit.Piece.Length == 0)
                continue;
            var w = Math.Clamp(weight * hit.Strength * hit.Factor, 0, 0.95f);
            var into = IsFileSource(source) ? fileMiss : miss;
            into[hit.Piece] = into.GetValueOrDefault(hit.Piece, 1f) * (1 - w);
            if (!bestWord.TryGetValue(hit.Piece, out var b) || w > b.W)
                bestWord[hit.Piece] = (hit.Word, w);
        }

        if (miss.Count == 0 && fileMiss.Count == 0)
            return o;

        // File and material names repeat themselves (one folder per size): together they count as one weak word.
        foreach (var piece in miss.Keys.Concat(fileMiss.Keys).Distinct())
        {
            var fromFiles = Math.Min(Tuning.Votes.WordFilesMax, 1 - fileMiss.GetValueOrDefault(piece, 1f));
            o.Votes[piece] = 1 - miss.GetValueOrDefault(piece, 1f) * (1 - fromFiles);
        }

        // Strength: how sure the best word is, less what the second-best shelf has against it.
        var ordered = o.Votes.OrderByDescending(kv => kv.Value).ToList();
        var top     = ordered[0].Value;
        var second  = ordered.Count > 1 ? ordered[1].Value : 0;
        o.Strength = Math.Clamp(top * (top - second * 0.8f) / Math.Max(top, 1e-6f), 0, 1);

        var sum = ordered.Sum(kv => kv.Value);
        foreach (var key in o.Votes.Keys.ToList())
            o.Votes[key] /= sum;

        var word = bestWord[ordered[0].Key].Word;
        o.Phrase = kind == EvidenceKind.Folder ? $"your folder {Words.Quote(word)}" : $"says {Words.Quote(word)}";
        o.Word   = word;
        return o;
    }

    private static bool IsFileSource(string source)
        => source is "file" or "material";

    // ------------------------------------------------------------------ witness 4: shaders

    private static Opinion MaterialOpinion(PartFacts p)
    {
        // The game's body parts use their own shaders (hair and tails: hair; faces: skin, iris and hair for brows and
        // lashes; bodies: skin). A matching shader backs the slot up; gear shaders say nothing here.
        var o = new Opinion { Kind = EvidenceKind.Material };
        if (GamePaths.IsGear(p.Socket) || p.Shaders.Count == 0)
            return o;

        bool Has(string prefix) => p.Shaders.Any(s => s.StartsWith(prefix, StringComparison.OrdinalIgnoreCase));
        (string Piece, string Phrase)? fit = p.Socket switch
        {
            Socket.Hair when Has("hair")                                                    => (Pieces.Hair, "hair material"),
            Socket.Tail when Has("hair")                                                    => (Pieces.Tail, "fur material"),
            Socket.EarShape when Has("hair") || Has("skin")                                 => (Pieces.Ears, "ear material"),
            Socket.FaceTextures when Has("iris") && !Has("skin")                            => (Pieces.Makeup, "eye material"),
            Socket.FaceTextures when Has("skin") || Has("iris")                             => (Pieces.Makeup, "face material"),
            Socket.Face when Has("iris") && !Has("skin")                                    => (Pieces.Face, "eye material"),
            Socket.Face when Has("skin") || Has("iris")                                     => (Pieces.Face, "face material"),
            Socket.BodyBase or Socket.Other when Has("skin")                                => (Pieces.Skin, "skin material"),
            _                                                                               => null,
        };
        if (fit is not { } f)
            return o;

        o.Votes[f.Piece] = 1;
        o.Phrase   = f.Phrase;
        o.Strength = 0.6f;
        return o;
    }

    // ------------------------------------------------------------------ kind

    /// <summary> The kinds each shelf can have (rule 6: the kind fits the shelf; a jacket is never a "piercing"). </summary>
    private static readonly Dictionary<string, HashSet<string>> KindsOf = new()
    {
        [Pieces.Top]       = new() { "dress", "bodysuit", "bra", "top", "jacket", "coat", "vest", "corset", "harness", "wings" },
        [Pieces.Bottoms]   = new() { "skirt", "pants", "shorts", "underwear" },
        [Pieces.Legwear]   = new() { "stockings", "socks", "tights" },
        [Pieces.Shoes]     = new() { "heels", "boots", "flats", "sandals" },
        [Pieces.Head]      = new() { "glasses", "mask", "veil", "horns", "halo" },
        [Pieces.Necklace]  = new() { "choker", "scarf", "piercing" },
        [Pieces.Earrings]  = new() { "piercing" },
        [Pieces.Bracelets] = new() { "piercing" },
        [Pieces.Rings]     = new() { "piercing" },
        [Pieces.Gloves]    = new() { "armwarmers" },
        [Pieces.Bodies]    = new() { "body" },
    };

    /// <summary> Kinds an unsure part may carry on any shelf: they name a shelf the user may want of their own. </summary>
    private static readonly HashSet<string> OwnShelfKinds = new() { "wings", "horns", "glasses", "halo", "mask", "veil", "bag" };

    public static bool KindFits(string piece, string kind, bool unsure)
        => kind.Length == 0 || KindsOf.TryGetValue(piece, out var kinds) && kinds.Contains(kind) || unsure && OwnShelfKinds.Contains(kind);

    private static string KindOf(PartFacts p, string piece, BodyReading body, List<(WordHit Hit, float Weight, string Source)> words, float confidence)
    {
        var kind = KindFromWordsOrShape(p, piece, body, words, confidence);
        return piece == Pieces.Other || KindFits(piece, kind, confidence < DetectorThresholds.Unsure) ? kind : ShapeKind(p, piece, body);
    }

    private static string KindFromWordsOrShape(PartFacts p, string piece, BodyReading body, List<(WordHit Hit, float Weight, string Source)> words, float confidence)
    {
        if (piece == Pieces.Other)
            return p.Socket is Socket.MainHand or Socket.OffHand ? "weapon" : OtherKind(p) is var k && k != "custom" ? k : "helper";

        // A word that names a kind of this shelf wins ("kimono" on Tops → dress).
        // An unsure part also takes a kind that could become a shelf of its own ("horns" on a tail-slot part).
        var unsure = confidence < DetectorThresholds.Unsure;
        // The generic "top" leaves top / dress / bodysuit to the body, and a word must count for something.
        var fromWords = words.Where(w => w.Hit.Kind.Length > 0 && w.Hit.Kind != "top" && w.Weight * w.Hit.Factor * w.Hit.Strength >= Tuning.Votes.KindMinWord
            && (w.Hit.Piece == piece
                || !IsFileSource(w.Source) && (w.Hit.Piece.Length == 0 || unsure && ShelfProposals.Names.ContainsKey(w.Hit.Kind))))
            .GroupBy(w => w.Hit.Kind)
            .Select(g => (Kind: g.Key, Score: g.Sum(x => x.Weight * x.Hit.Factor * x.Hit.Strength),
                          // A word naming only a kind speaks for the whole part from the mod's name, tags or a group name;
                          // in an option or toggle it names an extra ("Feathered wings" on a top).
                          Own: g.Any(x => x.Hit.Piece == piece
                                       || x.Hit.Piece.Length == 0 && x.Source is "mod" or "tag" or "group" && KindFits(piece, g.Key, false))))
            .Where(x => (x.Own || unsure) && KindFits(piece, x.Kind, unsure))
            .OrderByDescending(x => x.Own)
            .ThenByDescending(x => x.Score)
            .FirstOrDefault();
        if (fromWords.Kind is { Length: > 0 })
            return fromWords.Kind;

        return ShapeKind(p, piece, body);
    }

    /// <summary> The kind from the shape alone. </summary>
    private static string ShapeKind(PartFacts p, string piece, BodyReading body)
    {
        if (body.Shares.Count == 0)
            return string.Empty;

        float N(BodyRegions r) => body[r];
        switch (piece)
        {
            case Pieces.Top:
            {
                var lower = N(BodyRegions.Hips) + N(BodyRegions.Skirt) + N(BodyRegions.Thighs);
                if (N(BodyRegions.Skirt) >= 0.08f || N(BodyRegions.Thighs) >= 0.12f && lower >= 0.25f)
                    return "dress";
                if (N(BodyRegions.Hips) >= 0.15f)
                    return "bodysuit";
                if (N(BodyRegions.Chest) >= 0.55f && N(BodyRegions.Back) < 0.3f && N(BodyRegions.Waist) < 0.1f
                 && N(BodyRegions.UpperArms) + N(BodyRegions.Forearms) < 0.1f)
                    return "bra";
                return "top";
            }
            case Pieces.Bottoms:
                if (N(BodyRegions.Skirt) >= 0.15f)
                    return "skirt";
                if (N(BodyRegions.Calves) >= 0.1f || N(BodyRegions.Knees) >= 0.08f)
                    return "pants";
                if (N(BodyRegions.Thighs) >= 0.15f)
                    return "shorts";
                return body.ScaledArea < 0.06f ? "underwear" : "shorts";
            case Pieces.Legwear:
                if (N(BodyRegions.Hips) >= 0.15f)
                    return "tights";
                return N(BodyRegions.Thighs) >= 0.12f ? "stockings" : "socks";
            case Pieces.Shoes:
                if (body.Heels)
                    return "heels";
                return N(BodyRegions.Calves) >= 0.25f || N(BodyRegions.Knees) >= 0.05f ? "boots" : string.Empty;
            default:
                return string.Empty;
        }
    }

    /// <summary>
    /// Heels: modders set "heels_offset=0.2" on the model for the heels plugin ("heels_offset_n_a…" means none), and
    /// heeled shoes reach below the floor line of the rest pose.
    /// </summary>
    private static bool HasHeels(ModelFacts m)
    {
        foreach (var a in m.Attributes)
        {
            var at = a.IndexOf("heels_offset=", StringComparison.OrdinalIgnoreCase);
            if (at >= 0 && float.TryParse(a[(at + 13)..], System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var v) && v > 0)
                return true;
        }

        return m.Min[1] < -0.03f * BodyMap.Scale(m.Race);
    }

    /// <summary> The kind of an Other part: what most of it is, animations first (they bring their effects along). </summary>
    public static string OtherKind(PartFacts p)
    {
        foreach (var k in new[] { "animation", "mount or minion", "effect", "sound", "interface", "world", "weapon", "custom", "helper" })
            if (p.OtherKinds.ContainsKey(k))
                return k;
        return p.OtherKinds.Keys.FirstOrDefault() ?? string.Empty;
    }

    // ------------------------------------------------------------------ signature

    /// <summary> Length of <see cref="PartInfo.Signature"/>: 19 region shares, then 6 shape numbers. </summary>
    public const int SignatureLength = 25;

    /// <summary>
    /// Fixed layout: [0..18] share of the item surface per <see cref="BodyRegions"/> bit (HeadTop … Skirt), [19] share away
    /// from the body, [20] log10 of the item area in m² (at scale 1, +4, so 0..4), [21] centre height / (1.6 × scale),
    /// [22] centre forward (z) / scale, [23] height range / (1.6 × scale), [24] finger tip share. Empty without a model.
    /// </summary>
    private static float[] Signature(PartFacts p, BodyReading body)
    {
        if (p.Model is not { Area: > 0 } m || body.Shares.Count == 0 && body.Away <= 0)
            return Array.Empty<float>();

        var s   = BodyMap.Scale(m.Race);
        var sig = new float[SignatureLength];
        for (var i = 0; i < BodyMap.Regions.Length && i < 19; ++i)
            sig[i] = MathF.Round(body.Shares.GetValueOrDefault(BodyMap.Regions[i]), 3);
        sig[19] = MathF.Round(body.Away, 3);
        sig[20] = MathF.Round(Math.Clamp(MathF.Log10(Math.Max(body.ScaledArea, 1e-4f)) + 4, 0, 6), 3);
        sig[21] = MathF.Round(m.Centroid[1] / (1.6f * s), 3);
        sig[22] = MathF.Round(m.Centroid[2] / s, 3);
        sig[23] = MathF.Round((m.Max[1] - m.Min[1]) / (1.6f * s), 3);
        sig[24] = MathF.Round(body.TipShare, 3);
        return sig;
    }

    private static readonly float[] SignatureWeights = BuildWeights();

    private static float[] BuildWeights()
    {
        var w = new float[SignatureLength];
        for (var i = 0; i < 20; ++i)
            w[i] = 1f;
        w[20] = 0.15f;
        w[21] = 1f;
        w[22] = 1f;
        w[23] = 0.5f;
        w[24] = 0.3f;
        return w;
    }

    /// <summary> Weighted L1 distance between two signatures (region shares count most); infinity when either is empty. </summary>
    public static float Distance(float[] a, float[] b)
    {
        if (a.Length != SignatureLength || b.Length != SignatureLength)
            return float.PositiveInfinity;
        var d = 0f;
        for (var i = 0; i < SignatureLength; ++i)
            d += SignatureWeights[i] * MathF.Abs(a[i] - b[i]);
        return d;
    }
}
