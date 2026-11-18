using ModCloset2.Core;

namespace ModCloset2.Detect;

/// <summary> What the body witness worked out for a gear part (the ten equipment slots). </summary>
public sealed class GearReading
{
    /// <summary> The shelf the body names (the slot's own shelf inside its reach). </summary>
    public string Piece = string.Empty;

    /// <summary> Shelves the words may still pick between, because the shape cannot tell them apart. </summary>
    public HashSet<string> Candidates = new();

    /// <summary> True when the shape alone settles it. </summary>
    public bool Sure;

    /// <summary> Small and away from its slot's home on an accessory slot: body jewellery (a piercing, a chain). </summary>
    public bool Jewellery;

    /// <summary> Only on the back (wings, a bag, a plush): not a top. It waits on the slot's own shelf. </summary>
    public bool BackOnly;

    /// <summary> Shelves the words may only pick with at least this strength (legwear in the leg slot without feet). </summary>
    public Dictionary<string, float> MinPick = new();

    /// <summary> Shelves the words may pick from a lower strength than usual (a scarf where the neck is covered). </summary>
    public Dictionary<string, float> PickAt = new();

    /// <summary> The model sits inside its clothes slot's reach (or at its accessory slot's home). </summary>
    public bool InReach;

    /// <summary> How much of the surface the reading rests on (0..1). </summary>
    public float Clarity;
}

public static partial class Decision
{
    /// <summary> The places a clothes slot's garments normally cover (rule 1 of the tuning). </summary>
    private static BodyRegions Reach(Socket s)
        => s switch
        {
            Socket.Head  => BodyRegions.HeadTop | BodyRegions.Face | BodyRegions.Ears | BodyRegions.Neck,
            Socket.Body  => BodyRegions.Neck | BodyRegions.Chest | BodyRegions.Back | BodyRegions.Waist | BodyRegions.Hips | BodyRegions.Skirt
                          | BodyRegions.UpperArms | BodyRegions.Forearms | BodyRegions.Hands | BodyRegions.Fingers | BodyRegions.Thighs,
            Socket.Hands => BodyRegions.Fingers | BodyRegions.Hands | BodyRegions.Forearms | BodyRegions.UpperArms,
            Socket.Legs  => BodyRegions.Waist | BodyRegions.Hips | BodyRegions.Skirt | BodyRegions.Thighs | BodyRegions.Knees | BodyRegions.Calves,
            Socket.Feet  => BodyRegions.Feet | BodyRegions.Toes | BodyRegions.Calves | BodyRegions.Knees | BodyRegions.Thighs,
            _            => BodyRegions.None,
        };

    /// <summary> Where an accessory slot's jewellery lives (rule 2): the head for earrings, neck and upper chest, forearms and hands, fingers and hands. </summary>
    private static BodyRegions Home(Socket s)
        => s switch
        {
            Socket.Ears   => BodyRegions.Ears | BodyRegions.HeadTop | BodyRegions.Face,
            Socket.Neck   => BodyRegions.Neck | BodyRegions.Chest,
            Socket.Wrists => BodyRegions.Forearms | BodyRegions.Hands,
            Socket.Rings  => BodyRegions.Fingers | BodyRegions.Hands,
            _             => BodyRegions.None,
        };

    /// <summary> The head as ears and hats sit on it. </summary>
    private const BodyRegions HeadRegions = BodyRegions.HeadTop | BodyRegions.Ears | BodyRegions.Face;

    private static bool IsAccessory(Socket s)
        => s is Socket.Ears or Socket.Neck or Socket.Wrists or Socket.Rings;

    private static float Share(BodyReading r, BodyRegions set)
    {
        var sum = 0f;
        foreach (var (region, share) in r.Shares)
            if ((set & region) != 0)
                sum += share;
        return sum;
    }

    /// <summary> The body witness for a gear slot: inside the reach it backs the slot, outside the coverage decides. </summary>
    private static GearReading ReadGear(Socket socket, BodyReading r, float placed)
    {
        var t = Tuning.Gear;
        var slot = SlotPrior(socket);
        var o = r.Opinion;
        float N(BodyRegions x) => r[x] / Math.Max(placed, 1e-6f);

        if (IsAccessory(socket))
        {
            // At home and small: the slot's jewellery, sure. The ear slot's home is the whole head, but only the ear
            // region keeps larger things (a big earring); on the face and the top of the head only the tiny ones count.
            var home = Share(r, Home(socket)) / Math.Max(placed, 1e-6f);
            var ears = N(BodyRegions.Ears);
            var max = socket switch
            {
                Socket.Ears   => ears >= N(BodyRegions.HeadTop) && ears >= N(BodyRegions.Face) ? t.HomeMaxEars : t.FaceGarmentMin,
                Socket.Neck   => t.HomeMaxNeck,
                Socket.Wrists => t.HomeMaxWrists,
                _             => t.HomeMaxRings,
            };
            var neckOk = socket != Socket.Neck || N(BodyRegions.Neck) >= t.NeckHomeMinNeck;
            if (placed >= 0.35f && home >= t.HomeShare && r.ScaledArea <= max && neckOk)
            {
                Speak(o, HomePhrase(socket), home, slot);
                var g = new GearReading { Piece = slot, Sure = true, InReach = true, Clarity = home, Candidates = { slot } };
                if (socket == Socket.Ears)
                    g.Candidates.Add(Pieces.Ears); // "Elf ears" in the ear slot: the words may say ears
                return g;
            }

            return FromCoverage(socket, r, placed, slot);
        }

        // An "ears and tail" set in the head slot: with any part on the head it is ears (worn on the head, the tail
        // comes with them); with nothing on the head it is a tail (the coverage says so below).
        var tailSet = Share(r, BodyRegions.Tail) >= t.BodyHomeAnyShare
                   || N(BodyRegions.Hips) + N(BodyRegions.Tail) >= t.TailSetMinHips
                      && N(BodyRegions.Chest) + N(BodyRegions.Back) + N(BodyRegions.Waist) < t.BackOnlyMaxFront;
        if (socket == Socket.Head && placed >= 0.35f && tailSet && Share(r, HeadRegions) >= t.BodyHomeAnyShare)
        {
            Speak(o, "top of head", Math.Max(Share(r, HeadRegions) / Math.Max(placed, 1e-6f), 0.5f), Pieces.Ears);
            return new GearReading { Piece = Pieces.Ears, Sure = true, InReach = true, Clarity = 1, Candidates = { Pieces.Ears } };
        }

        // Clothes: mostly outside the reach is a disagreement, decided by the coverage alone.
        var inside  = Share(r, Reach(socket));
        var outside = 1 - inside;
        if (placed < 0.35f || outside >= t.OutsideMostly)
            return FromCoverage(socket, r, placed, slot);

        var reading = new GearReading { Piece = slot, Sure = true, InReach = true, Clarity = inside, Candidates = { slot } };
        var phrase = CoveragePhrase(r, placed);
        switch (socket)
        {
            case Socket.Head:
                reading.Candidates.Add(Pieces.Ears); // animal ears worn as a hat: only the words can tell
                break;
            case Socket.Body:
            {
                var upper = N(BodyRegions.Chest) + N(BodyRegions.Back) + N(BodyRegions.Waist) + N(BodyRegions.Neck)
                          + N(BodyRegions.UpperArms) + N(BodyRegions.Forearms);
                var lower = N(BodyRegions.Hips) + N(BodyRegions.Skirt) + N(BodyRegions.Thighs);
                if (upper < t.SkirtOnlyUpperMax && lower >= t.SkirtOnlyLowerMin)
                {
                    // Only a skirt in the body slot: a top's lower half or a skirt worn there; both are honest.
                    reading.Sure = false;
                    reading.Candidates.Add(Pieces.Bottoms);
                    phrase = "hips";
                }

                break;
            }
            case Socket.Hands:
            {
                var fingers = N(BodyRegions.Fingers);
                var hands   = N(BodyRegions.Hands);
                var fore    = N(BodyRegions.Forearms);

                // Nails and bracelets only where the shape allows them: a glove with a "nails" option stays gloves.
                if (fingers >= 0.6f && hands < 0.25f)
                {
                    reading.Candidates.Add(Pieces.Nails);
                    if (r.TipShare >= 0.6f)
                    {
                        reading.Piece = Pieces.Nails;
                        phrase = "fingertips";
                    }
                    else if (r.BaseShare >= 0.6f && r.Fingers <= 4)
                    {
                        reading.Piece = Pieces.Rings;
                        reading.Candidates.Add(Pieces.Rings);
                        phrase = "fingers";
                    }
                    else
                    {
                        // Nails and rings together: split for real.
                        reading.Piece = Pieces.Nails;
                        reading.Sure  = false;
                        reading.Candidates.Add(Pieces.Rings);
                        phrase = "fingers";
                    }
                }
                else if (r.ScaledArea < Tuning.Body.GloveMinArea && fore + hands >= 0.6f && fingers < 0.2f)
                {
                    reading.Candidates.Add(Pieces.Bracelets);
                    reading.Piece = Pieces.Bracelets;
                    phrase = "wrists";
                }

                break;
            }
            case Socket.Legs:
                // Tights and stockings worn in the leg slot. Covering the feet (trousers do not), the shape allows it and
                // the words pick; otherwise only a strong word of the part's own does. Skirt bones: a skirt is bottoms.
                if (N(BodyRegions.Skirt) < t.LegsSkirtMax)
                {
                    reading.Candidates.Add(Pieces.Legwear);
                    if (N(BodyRegions.Feet) + N(BodyRegions.Toes) < t.LegsFeetMin)
                        reading.MinPick[Pieces.Legwear] = t.LegwearWordMin;
                }

                break;
            case Socket.Feet:
            {
                reading.Candidates.Add(Pieces.Legwear);
                var hips   = N(BodyRegions.Hips) + N(BodyRegions.Skirt);
                var thighs = N(BodyRegions.Thighs);
                var toes   = N(BodyRegions.Toes);
                if (hips >= t.FeetTightsHips)
                {
                    // Shoes never reach the hips: tights.
                    reading.Piece = Pieces.Legwear;
                    phrase = "hips and legs";
                }
                else if (!r.Heels && thighs >= t.FeetThighs && toes >= t.FeetToes)
                {
                    // Up the thigh with the toes weighted: mostly socks or stockings, some open boots. Split.
                    reading.Piece = Pieces.Legwear;
                    reading.Sure  = false;
                    phrase = "legs";
                }

                break;
            }
        }

        Speak(o, phrase, inside, reading.Piece);
        if (!reading.Sure)
            o.Votes[slot] = o.Votes.GetValueOrDefault(slot) + 0.5f;
        return reading;
    }

    private static void Speak(Opinion o, string phrase, float strength, string piece)
    {
        o.Phrase   = phrase;
        o.Strength = Math.Clamp(strength, 0, 1);
        o.Votes.Clear();
        o.Votes[piece] = 1;
    }

    private static string HomePhrase(Socket s)
        => s switch
        {
            Socket.Ears   => "head",
            Socket.Neck   => "neck",
            Socket.Wrists => "wrists",
            _             => "fingers",
        };

    /// <summary> A short phrase for where most of the surface is. </summary>
    private static string CoveragePhrase(BodyReading r, float placed)
    {
        var groups = Groups(r, placed);
        return groups.MaxBy(g => g.Share).Phrase;
    }

    private static (float Share, string Phrase)[] Groups(BodyReading r, float placed)
    {
        float N(BodyRegions x) => r[x] / Math.Max(placed, 1e-6f);
        return new[]
        {
            (N(BodyRegions.HeadTop), "top of head"), (N(BodyRegions.Face), "face"), (N(BodyRegions.Ears), "ears"), (N(BodyRegions.Neck), "neck"),
            (N(BodyRegions.Chest) + N(BodyRegions.Back) + N(BodyRegions.Waist), "upper body"), (N(BodyRegions.UpperArms) + N(BodyRegions.Forearms), "arms"),
            (N(BodyRegions.Hands), "hands"), (N(BodyRegions.Fingers), "fingers"), (N(BodyRegions.Hips) + N(BodyRegions.Skirt), "hips"),
            (N(BodyRegions.Thighs) + N(BodyRegions.Knees) + N(BodyRegions.Calves), "legs"), (N(BodyRegions.Feet) + N(BodyRegions.Toes), "feet"),
            (N(BodyRegions.Tail), "tail"),
        };
    }

    /// <summary>
    /// The shelf from the coverage alone, as if there were no slot (rule 2, and rule 1 outside the reach). Size matters
    /// where jewellery and garments share a place: what is smaller than a garment there is body jewellery.
    /// </summary>
    private static GearReading FromCoverage(Socket socket, BodyReading r, float placed, string slot)
    {
        var t = Tuning.Gear;
        var o = r.Opinion;
        var area = r.ScaledArea;
        var g = new GearReading();

        void Set(string piece, string phrase, float clarity, params string[] compatible)
        {
            g.Piece   = piece;
            g.Clarity = Math.Clamp(clarity, 0, 1);
            g.Sure    = g.Clarity >= t.ClearCoverage;
            g.Candidates.Add(piece);
            foreach (var c in compatible)
                g.Candidates.Add(c);
            Speak(o, phrase, g.Clarity, piece);
        }

        void Jewellery(string phrase, float clarity)
        {
            // Small and away from home: a piercing or a chain. It stays on the slot's own shelf and waits under To sort
            // (kind "piercing"), so the closet can offer one shelf for all of them.
            g.Piece     = slot;
            g.Jewellery = true;
            g.Clarity   = clarity;
            g.Candidates.Add(slot);
            Speak(o, phrase, Math.Max(clarity, 0.3f), slot);
        }

        if (placed < 0.35f)
        {
            Set(Pieces.Other, "away from the body", (1 - placed) * 0.9f);
            return g;
        }

        float N(BodyRegions x) => r[x] / Math.Max(placed, 1e-6f);
        var headTop = N(BodyRegions.HeadTop);
        var face    = N(BodyRegions.Face);
        var ears    = N(BodyRegions.Ears);
        var head    = headTop + face + ears;
        var neck    = N(BodyRegions.Neck);
        var chest   = N(BodyRegions.Chest);
        var back    = N(BodyRegions.Back);
        var torso   = chest + back + N(BodyRegions.Waist);
        var arms    = N(BodyRegions.UpperArms) + N(BodyRegions.Forearms);
        var fore    = N(BodyRegions.Forearms);
        var hands   = N(BodyRegions.Hands);
        var fingers = N(BodyRegions.Fingers);
        var hips    = N(BodyRegions.Hips) + N(BodyRegions.Skirt);
        var thighs  = N(BodyRegions.Thighs);
        var lowLegs = N(BodyRegions.Knees) + N(BodyRegions.Calves);
        var feet    = N(BodyRegions.Feet) + N(BodyRegions.Toes);
        var tail    = N(BodyRegions.Tail);
        var upper   = torso + arms + neck;

        if (tail >= 0.5f)
        {
            // Ears and tail in one model: the words may say ears, but only when the head carries some of it.
            if (headTop >= t.TailWithEarsMinHead)
                Set(Pieces.Tail, "tail", tail, Pieces.Ears);
            else
                Set(Pieces.Tail, "tail", tail);
            return g;
        }

        if (head >= 0.55f)
        {
            if (ears >= headTop && ears >= face)
                Set(Pieces.Earrings, "ears", head, Pieces.Ears, Pieces.Head);
            else if (area < t.FaceGarmentMin)
                Jewellery(face >= headTop ? "face" : "top of head", head);
            else if (face >= headTop)
                Set(Pieces.Head, "face", head); // masks, glasses, blindfolds: headwear, not the face itself
            else
                Set(Pieces.Head, "top of head", head, Pieces.Ears, Pieces.Hair);
            return g;
        }

        if (neck >= 0.25f && neck + chest >= 0.6f && back < 0.2f && arms < 0.1f)
        {
            Set(Pieces.Necklace, "neck", neck + chest);
            return g;
        }

        if (fingers >= 0.6f)
        {
            Set(r.TipShare >= 0.6f ? Pieces.Nails : Pieces.Rings, r.TipShare >= 0.6f ? "fingertips" : "fingers", fingers, Pieces.Rings, Pieces.Nails);
            return g;
        }

        if (hands + fingers + fore >= 0.6f && torso < 0.15f)
        {
            if (area >= Tuning.Body.GloveMinArea && hands >= 0.15f)
                Set(Pieces.Gloves, "hands", hands + fingers + fore, Pieces.Bracelets);
            else
                Set(Pieces.Bracelets, "wrists", hands + fingers + fore, Pieces.Gloves);
            return g;
        }

        if (arms + hands >= t.ArmsOnly && torso < 0.1f)
        {
            // Sleeves and arm warmers on their own: gloves; small bands on the arm: bracelets.
            if (area < t.TorsoGarmentMin)
                Set(Pieces.Bracelets, "arms", arms + hands, Pieces.Gloves);
            else
                Set(Pieces.Gloves, "arms", arms + hands, Pieces.Bracelets, Pieces.Top);
            return g;
        }

        // Only on the back (or back and waist), no chest, no arms: wings, a bag, a plush. Not a top: it waits on the
        // slot's own shelf, with its kind when the words name one.
        if (back + N(BodyRegions.Waist) >= t.BackOnlyShare && back >= t.BackOnlyMinBack && chest < t.BackOnlyMaxFront && arms < t.BackOnlyMaxFront)
        {
            g.Piece    = slot;
            g.BackOnly = true;
            g.Clarity  = back;
            g.Candidates.Add(slot);
            Speak(o, "back", Math.Max(back, 0.3f), slot);
            return g;
        }

        // Garments of the body: what covers the arms or the torso is a top; small things there are body jewellery.
        if (upper >= 0.3f && (arms >= 0.15f || upper >= 0.45f || upper >= 0.25f && hips + thighs >= 0.25f))
        {
            if (area < t.TorsoGarmentMin)
                Jewellery(back > chest ? "back" : "upper body", upper);
            else
                Set(Pieces.Top, hips + thighs >= 0.25f ? "upper body and legs" : "upper body", upper + (hips + thighs >= 0.25f ? hips + thighs : 0));

            // The neck covered and no sleeves: a scarf when the words say so (a long one hangs down to the hips).
            if (area >= t.TorsoGarmentMin && neck >= t.ScarfMinNeck && arms < 0.1f)
            {
                g.Candidates.Add(Pieces.Necklace);
                g.PickAt[Pieces.Necklace] = t.ScarfWordPick;
            }

            return g;
        }

        if (hips >= 0.25f || hips + thighs >= 0.4f && thighs < hips + 0.3f)
        {
            if (area < t.TorsoGarmentMin)
            {
                Jewellery("hips", hips + thighs);
                return g;
            }

            // Stocking tops: thighs and calves without the hips are legwear.
            if (hips < 0.15f && thighs + lowLegs >= 0.5f)
                Set(Pieces.Legwear, "legs", thighs + lowLegs + feet, Pieces.Bottoms);
            else
                Set(Pieces.Bottoms, "hips", hips + thighs + lowLegs, Pieces.Legwear, Pieces.Tail);
            return g;
        }

        if (thighs + lowLegs + feet >= 0.5f)
        {
            if (area < t.LegGarmentMin)
                Jewellery("legs", thighs + lowLegs + feet);
            else if (feet >= 0.45f && thighs < 0.08f)
                Set(Pieces.Shoes, "feet", feet + lowLegs, Pieces.Legwear);
            else
                Set(Pieces.Legwear, thighs >= 0.12f ? "legs" : "lower legs", thighs + lowLegs + feet, Pieces.Shoes, Pieces.Bottoms);
            return g;
        }

        if (upper >= 0.3f)
        {
            if (area < t.TorsoGarmentMin)
                Jewellery("upper body", upper);
            else
                Set(Pieces.Top, "upper body", upper);
            return g;
        }

        // Spread thin over several places: the largest place, and not sure.
        var top = Groups(r, placed).MaxBy(x => x.Share);
        var piece = top.Phrase switch
        {
            "top of head" or "face" => Pieces.Head,
            "ears"                  => Pieces.Earrings,
            "neck"                  => Pieces.Necklace,
            "upper body" or "arms"  => Pieces.Top,
            "hands"                 => Pieces.Gloves,
            "fingers"               => Pieces.Rings,
            "hips"                  => Pieces.Bottoms,
            "legs"                  => Pieces.Legwear,
            "feet"                  => Pieces.Shoes,
            _                       => Pieces.Tail,
        };
        Set(piece, "spread over the body", top.Share * 0.5f, slot);
        return g;
    }

    /// <summary>
    /// Rules 1 and 2 of the tuning: a gear part's shelf. The body names the shelf (the slot's own inside its reach or at
    /// its home; the coverage outside), the part's own words pick only between shelves the shape cannot tell apart
    /// (bottoms or legwear, shoes or legwear, headwear or ears…), and a word or folder that only repeats the slot adds
    /// nothing.
    /// </summary>
    private static PartInfo DecideGear(ModFacts facts, PartFacts p, BodyReading body, List<(WordHit Hit, float Weight, string Source)> wordHits,
        List<(WordHit Hit, float Weight, string Source)> folderHits)
    {
        var v    = Tuning.Votes;
        var t    = Tuning.Gear;
        var slot = SlotPrior(p.Socket);
        var slotOpinion = SlotOpinion(p);
        slotOpinion.Weight = v.Slot;
        var words  = WordsOpinion(wordHits, EvidenceKind.Words);
        words.Weight = v.Words;
        var folder = WordsOpinion(folderHits, EvidenceKind.Folder);
        folder.Weight = v.Folder;
        body.Opinion.Weight = v.Body;

        string piece;
        float confidence;
        var candidates = new HashSet<string>();
        var jewellery = false;
        var backOnly = false;
        if (body.Gear is { } g)
        {
            piece      = g.Piece;
            confidence = g.Jewellery || g.BackOnly ? v.ConfOnPurpose : g.Sure ? (g.InReach ? t.ConfInReach : t.ConfCoverage) : v.ConfConflict;
            candidates = g.Candidates;
            jewellery  = g.Jewellery;
            backOnly   = g.BackOnly;

            // A folder that names two shelves ("Tails & Ears") supports whichever of them the shape names, never argues.
            var folderPick = folderHits;
            if (folderHits.Select(h => h.Hit.Piece).Where(x => x.Length > 0).Distinct().Count() >= 2)
                folderPick = folderHits.Where(h => h.Hit.Piece == piece).ToList();

            // The words pick between shelves the shape cannot tell apart: the user's folder first, then the mod's own
            // name and tags (what the whole thing is), then the part's group and option names (which may only name a
            // layer of it: "Stocking dye" on heels).
            if (!jewellery && !backOnly)
                foreach (var source in new[] { folderPick, wordHits.Where(h => h.Source is "mod" or "tag").ToList(), wordHits })
                {
                    var pick = Restricted(source, candidates, source == folderPick ? EvidenceKind.Folder : EvidenceKind.Words);
                    if (pick.Speaks && pick.Top != piece && g.MinPick.TryGetValue(pick.Top, out var min))
                    {
                        // Only a strong word that names the part itself (not a layer's colour or a "with" extra).
                        var strong = Restricted(source.Where(h => h.Hit.Factor >= 0.99f).ToList(), candidates, EvidenceKind.Words);
                        if (strong.Top != pick.Top || strong.Strength < min)
                            continue;
                    }

                    if (pick.Speaks && pick.Strength >= g.PickAt.GetValueOrDefault(pick.Top, t.WordPick) && pick.Top != piece)
                    {
                        piece      = pick.Top;
                        confidence = Math.Max(confidence, t.ConfWordPick);
                        break;
                    }

                    if (pick.Speaks && pick.Top == piece && (!g.Sure || pick.Strength >= t.WordPick))
                    {
                        // The words take the shape's side: a split shape becomes sure, and weaker names below stay out.
                        if (!g.Sure)
                            confidence = Math.Max(confidence, t.ConfWordPick);
                        break;
                    }
                }

            // A split shape (nails and rings on the fingers) that any name of the part settles, file names included.
            if (!g.Sure && !jewellery && !backOnly && confidence < t.ConfWordPick
                && WordsOpinion(wordHits.Where(h => candidates.Contains(h.Hit.Piece) && h.Source is not ("offoption" or "offgroup" or "offtoggle")).ToList(), EvidenceKind.Words) is { Speaks: true } any
                && any.Top == piece)
                confidence = t.ConfWordPick;
        }
        else if (p.SwappedFrom.Count > 0)
        {
            // A game model swapped in: it sits where its own slot sits.
            var from = p.SwappedFrom[0];
            piece = from == p.Socket ? slot : p.Socket == Socket.Tail && from == Socket.Head ? Pieces.Ears : SlotPrior(from);
            confidence = from == p.Socket ? t.ConfInReach : v.ConfConflict;
            candidates.Add(piece);
            candidates.Add(slot);
        }
        else if (!p.HasModel)
        {
            // Textures for a game item: the item sits where its slot says (rule 4).
            piece      = slot;
            confidence = t.ConfConclusive;
        }
        else
        {
            // A model that could not be read, or one without any surface: the body cannot speak, as for textures
            // only, and the slot decides alone (rule 4).
            piece      = slot;
            confidence = t.ConfConclusive;
        }

        candidates.Add(piece);
        candidates.Add(slot);
        var order = candidates.OrderByDescending(c => c == piece).ThenByDescending(c => c == slot).ThenBy(c => c, StringComparer.Ordinal).ToList();
        var guesses = order.Select((c, i) => new PieceGuess { Piece = c, Score = MathF.Round(i == 0 ? confidence : (1 - confidence) / Math.Max(1, order.Count - 1), 3) }).ToList();

        var info = new PartInfo
        {
            Mod         = facts.Mod,
            Socket      = p.Socket,
            Piece       = piece,
            Confidence  = MathF.Round(confidence, 3),
            Guesses     = guesses,
            Covers      = body.Covers,
            Signature   = Signature(p, body),
            TextureOnly = !p.HasModel && p.SwappedFrom.Count == 0,
            RaceCodes   = p.RaceCodes.ToList(), // the race codes of its models (c0201…): the closet's Race filter
            Groups      = p.Groups.ToList(),
            Models      = Math.Max(1, p.ModelSets.Count),
            Hands       = HandsOf(p),
        };

        info.Kind = jewellery ? "piercing"
                  : backOnly ? BackKind(wordHits)
                  : Outerwear(p.Socket, piece, body, KindOf(p, piece, body, wordHits, info.Confidence), wordHits);
        info.Evidence.Add(Evidence(slotOpinion));
        info.Evidence.Add(Evidence(body.Opinion));
        info.Evidence.Add(Evidence(words));
        if (folder.Speaks)
            info.Evidence.Add(Evidence(folder));
        return info;
    }

    /// <summary>
    /// A ring-slot part: the fingers it has files for, from what any of the mod's options changes (so a ring whose left
    /// finger comes with an option that is off counts as both: it can be worn on the left). Older facts without the
    /// finger list fall back to the kept paths.
    /// </summary>
    public static Hands HandsOf(PartFacts p)
    {
        if (p.Socket != Socket.Rings)
            return Hands.Both;
        var models = p.Paths.Where(x => x.EndsWith(".mdl", StringComparison.Ordinal)).ToList();
        var fingers = p.Fingers.Length > 0 ? p.Fingers
            : string.Concat((models.Any(x => GamePaths.FingerOfPath(x) is not null) ? models : p.Paths)
                .Select(GamePaths.FingerOfPath).Where(c => c is not null).Select(c => c!.Value).Distinct().Order());
        return fingers switch
        {
            "r" => Hands.Right,
            "l" => Hands.Left,
            _   => Hands.Both,
        };
    }

    /// <summary> The kind of something only on the back: what the part's whole names say (wings, bag), else none. </summary>
    private static string BackKind(List<(WordHit Hit, float Weight, string Source)> words)
        => words.Where(w => w.Hit.Piece.Length == 0 && w.Hit.Kind is "wings" or "bag" && w.Source is "mod" or "tag" or "group" or "option")
                .GroupBy(w => w.Hit.Kind).OrderByDescending(x => x.Sum(w => w.Weight)).Select(x => x.Key).FirstOrDefault() ?? string.Empty;

    /// <summary>
    /// A garment worn through an accessory slot is worn over the body-slot piece: outerwear. Without a word of its own
    /// it is a jacket (torso and sleeves) or a coat (reaching the thighs, or with a skirt part).
    /// </summary>
    private static string Outerwear(Socket socket, string piece, BodyReading body, string kind, List<(WordHit Hit, float Weight, string Source)> words)
    {
        if (!IsAccessory(socket) || piece != Pieces.Top || body.Shares.Count == 0)
            return kind;
        if (kind.Length > 0 && words.Any(w => w.Hit.Kind == kind && w.Hit.Piece == Pieces.Top))
            return kind; // the words still win ("corset", "vest")
        var placed = Math.Max(body.Shares.Values.Sum(), 1e-6f);
        float N(BodyRegions x) => body[x] / placed;
        var torso = N(BodyRegions.Chest) + N(BodyRegions.Back) + N(BodyRegions.Waist);
        var arms  = N(BodyRegions.UpperArms) + N(BodyRegions.Forearms);
        var legs = N(BodyRegions.Thighs) + N(BodyRegions.Skirt);
        if (legs >= Tuning.Gear.CoatMinLegs && (torso >= 0.15f || arms >= Tuning.Gear.OuterwearMinArms))
            return "coat";
        return torso >= 0.15f && arms >= Tuning.Gear.OuterwearMinArms ? "jacket" : kind;
    }

    /// <summary>
    /// The words' opinion among some shelves only, from names that describe the part as a whole: the mod's name and
    /// tags (routed to the part they fit), group and option names that are on, the user's folder. Toggles, options
    /// that are off and file names describe sub-parts and alternatives (a "Fishnet" option under trousers).
    /// </summary>
    private static Opinion Restricted(List<(WordHit Hit, float Weight, string Source)> hits, HashSet<string> shelves, EvidenceKind kind)
        => WordsOpinion(hits.Where(h => shelves.Contains(h.Hit.Piece) && h.Source is "mod" or "tag" or "group" or "option" or "folder").ToList(), kind);
}
