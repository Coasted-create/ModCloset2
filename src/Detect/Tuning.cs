namespace ModCloset2.Detect;

/// <summary>
/// Every number the detection decides with, in one place, so the rules can be tuned from the census (tools/census
/// --redecide shows what a change moves). Body heights are metres in the c0201 / c0801 rest pose, measured on the sample
/// models; <see cref="BodyMap.Scale"/> scales them for other races.
/// </summary>
public static class Tuning
{
    public static readonly BodyTuning Body = new();
    public static readonly VoteTuning Votes = new();
    public static readonly GearTuning Gear = new();
    public static readonly SameThingTuning Same = new();

    /// <summary> Reading limits (per mod). </summary>
    public const long MaxModBytes = 64L << 20;
    public const long MaxModelBytes = 40L << 20;
    public const long MaxMaterialBytes = 1L << 20;
    public const int MaxMaterials = 200;
    public const int MaxPathsKept = 40;
    public const int ClustersPerBone = 8;
    public const float ClusterCell = 0.03f;
}

public sealed class BodyTuning
{
    // the head (j_kao surface is split by where it is relative to the head's centre)
    public float HeadCenterY { get; init; } = 1.49f;
    public float HeadCenterZ { get; init; } = 0.02f;
    public float ChinY { get; init; } = 1.40f;
    public float EarSideX { get; init; } = 0.06f;
    public float EarTopDy { get; init; } = 0.0f;
    public float FaceFrontDz { get; init; } = 0.06f;
    public float BrowDy { get; init; } = 0.035f;
    public float ChinDy { get; init; } = -0.09f;

    // heights of the body bands, top to bottom
    public float NeckBaseY { get; init; } = 1.38f;
    public float ChestLowY { get; init; } = 1.15f;
    public float WaistLowY { get; init; } = 1.02f;
    public float HipLowY { get; init; } = 0.85f;
    public float KneeTopY { get; init; } = 0.58f;
    public float KneeLowY { get; init; } = 0.46f;
    public float AnkleY { get; init; } = 0.12f;

    // sideways: where the arms and hands start in each band (A pose), front / back split, and "away from the body"
    public float ArmX { get; init; } = 0.20f;
    public float ForearmX { get; init; } = 0.25f;
    public float HandX { get; init; } = 0.30f;
    public float FrontBackZ { get; init; } = 0.0f;
    public float BehindZ { get; init; } = -0.09f;
    public float AwayZ { get; init; } = 0.35f;
    public float AwayXLegs { get; init; } = 0.35f;
    public float AwayXArms { get; init; } = 0.68f;

    /// <summary> Regions with less than this share of the item's surface are not listed in Covers. </summary>
    public float CoverMinShare { get; init; } = 0.06f;

    /// <summary> Item area (m², both hands, at scale 1) below which hand jewellery is a bracelet / ring / nails, above a glove. </summary>
    public float GloveMinArea { get; init; } = 0.02f;
}

/// <summary> Rules 1 and 2 of the tuning on a real library: gear slots' reach and home, and sizes (m², at scale 1). </summary>
public sealed class GearTuning
{
    /// <summary> A clothes slot's model with at least this share outside its slot's reach is decided by its coverage. </summary>
    public float OutsideMostly { get; init; } = 0.5f;

    /// <summary> An accessory slot's model with this share at home… </summary>
    public float HomeShare { get; init; } = 0.6f;

    /// <summary> …and no bigger than this is the slot's jewellery (earrings on the ear, necklaces, bracelets, rings). </summary>
    public float HomeMaxEars { get; init; } = 0.08f;
    public float HomeMaxNeck { get; init; } = 0.15f;
    public float HomeMaxWrists { get; init; } = 0.06f;
    public float HomeMaxRings { get; init; } = 0.03f;

    /// <summary> A neck-slot piece must have this much on the neck to be at home (a chest piercing is not a necklace). </summary>
    public float NeckHomeMinNeck { get; init; } = 0.15f;

    /// <summary> Smallest garment on the face or the top of the head (masks, glasses); smaller is a piercing. </summary>
    public float FaceGarmentMin { get; init; } = 0.0045f;

    /// <summary> Smallest garment on the torso or the hips; smaller is body jewellery (a chain, a navel piercing). </summary>
    public float TorsoGarmentMin { get; init; } = 0.03f;

    /// <summary> Smallest legwear (a garter); smaller is body jewellery (an anklet). </summary>
    public float LegGarmentMin { get; init; } = 0.01f;

    /// <summary> Arms (and hands) alone with almost no torso: sleeves, arm warmers. </summary>
    public float ArmsOnly { get; init; } = 0.75f;

    /// <summary> A coverage reading this clear is sure. </summary>
    public float ClearCoverage { get; init; } = 0.55f;

    /// <summary> Body slot: only a skirt (upper body below, lower body at least): top or bottoms, honestly split. </summary>
    public float SkirtOnlyUpperMax { get; init; } = 0.1f;
    public float SkirtOnlyLowerMin { get; init; } = 0.8f;

    /// <summary> Feet slot: reaching the hips is tights; up the thigh with toes weighted (and no heel) leans to legwear. </summary>
    public float FeetTightsHips { get; init; } = 0.1f;
    public float FeetThighs { get; init; } = 0.12f;
    public float FeetToes { get; init; } = 0.05f;

    /// <summary> The part's own words pick between shelves the shape cannot tell apart from this strength on. </summary>
    public float WordPick { get; init; } = 0.4f;

    /// <summary>
    /// "Ears and tail" in one model: any part at a body socket's home from this share of the surface on (ears on the
    /// head for the ear-shape socket and the head slot, tail bones for the tail socket) keeps it there.
    /// </summary>
    public float BodyHomeAnyShare { get; init; } = 0.01f;

    /// <summary> A model on the bare body's sets with at most this much surface of its own (m²) is bare (pubic hair, a seam)… </summary>
    public float NearBareArea { get; init; } = 0.01f;

    /// <summary> …and at most this share of its bare skin. </summary>
    public float NearBareShare { get; init; } = 0.05f;

    /// <summary> A head-slot model with this share at the hips (and no torso) carries a tail weighted to the hips. </summary>
    public float TailSetMinHips { get; init; } = 0.3f;

    /// <summary> A tail-socket model without tail bones counts as on the head from this share. </summary>
    public float TailOnHeadShare { get; init; } = 0.5f;

    /// <summary> A tail model counts as possibly ears and tail together from this share on the top of the head. </summary>
    public float TailWithEarsMinHead { get; init; } = 0.1f;

    /// <summary> Leg slot: legwear by shape needs this much on the feet and toes (trousers stop at the ankle). </summary>
    public float LegsFeetMin { get; init; } = 0.05f;

    /// <summary> A feet-slot part with this share on the thighs, knees and calves is stockings (its words are its own). </summary>
    public float StockingsMinLegs { get; init; } = 0.4f;

    /// <summary> Leg slot without the feet: legwear only on a word of the part's own at least this strong. </summary>
    public float LegwearWordMin { get; init; } = 0.5f;

    /// <summary> Back only: back and waist together at least this share, the back at least <see cref="BackOnlyMinBack"/>. </summary>
    public float BackOnlyShare { get; init; } = 0.8f;

    public float BackOnlyMinBack { get; init; } = 0.4f;

    /// <summary> Back only: at most this share on the chest and on the arms. </summary>
    public float BackOnlyMaxFront { get; init; } = 0.08f;

    /// <summary> Upper-body garment without arms and with this much on the neck: a scarf when the words say so. </summary>
    public float ScarfMinNeck { get; init; } = 0.1f;

    /// <summary> …and with its neck covered and no sleeves, the words name a scarf from this strength on. </summary>
    public float ScarfWordPick { get; init; } = 0.2f;

    /// <summary> Accessory-slot top with this much on the arms: outerwear (jacket or coat). </summary>
    public float OuterwearMinArms { get; init; } = 0.12f;

    /// <summary> …with this much on the thighs or skirt bones: a coat. </summary>
    public float CoatMinLegs { get; init; } = 0.08f;

    /// <summary> Leg slot: with this much on skirt bones it is a skirt (bottoms), whatever the words. </summary>
    public float LegsSkirtMax { get; init; } = 0.08f;

    public float ConfInReach { get; init; } = 0.88f;
    public float ConfCoverage { get; init; } = 0.78f;
    public float ConfWordPick { get; init; } = 0.8f;
    public float ConfConclusive { get; init; } = 0.85f;
}

public sealed class VoteTuning
{
    // How much each witness counts when they are added up.
    public float Slot { get; init; } = 1.0f;
    public float Body { get; init; } = 1.3f;
    public float Words { get; init; } = 1.1f;
    public float Folder { get; init; } = 1.4f;
    public float Material { get; init; } = 0.6f;

    // Word weights by where the word was found (combined per shelf as 1 − Π(1 − w)).
    public float WordGroup { get; init; } = 0.6f;
    public float WordOption { get; init; } = 0.45f;
    public float WordOffOption { get; init; } = 0.25f;
    public float WordFile { get; init; } = 0.25f;
    public float WordMaterial { get; init; } = 0.3f;
    public float WordModName { get; init; } = 0.55f;
    public float WordTag { get; init; } = 0.45f;
    public float WordFolder { get; init; } = 0.8f;

    /// <summary> Names of groups and options that only switch parts of the model on and off ("Bracelet Toggles"). </summary>
    public float WordToggle { get; init; } = 0.25f;
    public float WordOffToggle { get; init; } = 0.12f;

    /// <summary> A word must count this much (weight × strength) to name the kind. </summary>
    public float KindMinWord { get; init; } = 0.2f;

    /// <summary> All file and material names together count at most this much for one shelf. </summary>
    public float WordFilesMax { get; init; } = 0.35f;

    /// <summary> Words after "with", "w/", "mit", "+" count this much ("Top with socks" is a top). </summary>
    public float WordWithFactor { get; init; } = 0.35f;

    /// <summary> A garment word in a name about one layer's choice ("Stocking texture") counts this much. </summary>
    public float WordLayerFactor { get; init; } = 0.5f;

    /// <summary> A witness with less strength than this has "no hint". </summary>
    public float Silent { get; init; } = 0.2f;

    /// <summary> A witness this strong that points elsewhere is a disagreement (sends the part to To sort). </summary>
    public float StrongDisagree { get; init; } = 0.5f;

    // Confidence levels (the closet's threshold is DetectorThresholds.Unsure = 0.55).
    public float ConfThreeAgree { get; init; } = 0.9f;
    public float ConfTwoAgree { get; init; } = 0.74f;
    public float ConfTwoOverSlot { get; init; } = 0.7f;

    /// <summary> The body alone against the slot, when the place is impossible for the slot's garment. </summary>
    public float ConfBodyOverSlot { get; init; } = 0.6f;

    /// <summary> Strength a body reading needs to overrule the slot alone. </summary>
    public float DecisiveBody { get; init; } = 0.8f;
    public float ConfConflict { get; init; } = 0.45f;
    public float ConfGameItem { get; init; } = 0.62f;
    public float ConfNotWorn { get; init; } = 0.9f;
    public float ConfOwnPart { get; init; } = 0.72f;
    public float ConfAlone { get; init; } = 0.42f;
    public float ConfNothing { get; init; } = 0.3f;

    /// <summary> Body jewellery waits under To sort on purpose (kind "piercing"), for the "New shelf" offer. </summary>
    public float ConfOnPurpose { get; init; } = 0.45f;

    /// <summary> Added for a clear lead (times the best's lead over the second, 0..1). </summary>
    public float ConfMarginBonus { get; init; } = 0.12f;

    // Learning from moves: distance between signatures below which parts "look the same".
    public float LookAlikeDistance { get; init; } = 0.45f;
    public int LookAlikeMax { get; init; } = 50;
}

/// <summary>
/// One thing offered through several slots (<see cref="SameThing"/>). Measured on the user's library (brief 9): every
/// pair of one mod's parts on one shelf up to a signature distance of 0.33 with item areas within a factor of 1.35 is
/// the same garment in another slot (the clothes-slot copy often carries an extra layer: panties under tights, a bra
/// under a hoodie); the next pairs, from 0.36 on, are other variants of the garment with twice the surface (a hood, a
/// longer cut).
/// </summary>
public sealed class SameThingTuning
{
    /// <summary> Largest signature distance (<see cref="Decision.Distance"/>) of the same thing in two slots. </summary>
    public float Line { get; init; } = 0.34f;

    /// <summary> Largest difference of the item areas, in log10 (0.16 is a factor of about 1.45). </summary>
    public float AreaLine { get; init; } = 0.16f;

    /// <summary> Up to this distance two models are the same file: one shelf and one kind for every copy. </summary>
    public float IdenticalMax { get; init; } = 0.001f;
}
