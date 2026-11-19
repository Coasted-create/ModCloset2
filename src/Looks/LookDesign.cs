using System.Text.Json.Nodes;
using ModCloset2.Core;

namespace ModCloset2.Looks;

/// <summary>
/// A look as a Glamourer design (its JSON, as Glamourer reads designs: FileVersion 2, Equipment, Customize, Parameters,
/// Mods). Only what the look keeps is applied: its gear items, the character's colours and the showings it saves; its
/// mods go in as the design's mod associations, with their options. Pure.
/// </summary>
public static class LookDesign
{
    /// <summary> One gear piece of the look: its slot, the game item it is worn through (0: unknown), its mod and options. </summary>
    public sealed record Piece(Socket Socket, ulong Item, string Mod, string ModName, IReadOnlyDictionary<string, List<string>> Options);

    /// <summary> Glamourer's parameter of a character colour (the same table as the wardrobe's, <c>GlamourerState.ColourParameter</c>). </summary>
    public static string? ParameterOf(string key)
        => key switch
        {
            "skin"       => "SkinDiffuse",
            "hair"       => "HairDiffuse",
            "highlights" => "HairHighlight",
            "eyeLeft"    => "LeftEye",
            "eyeRight"   => "RightEye",
            "lips"       => "LipDiffuse",
            "facePaint"  => "DecalColor",
            "tattoo"     => "FeatureColor",
            _            => null,
        };

    /// <summary> Glamourer's equipment slot of a socket. </summary>
    public static string? SlotOf(Socket socket)
        => socket switch
        {
            Socket.Head     => "Head",
            Socket.Body     => "Body",
            Socket.Hands    => "Hands",
            Socket.Legs     => "Legs",
            Socket.Feet     => "Feet",
            Socket.Ears     => "Ears",
            Socket.Neck     => "Neck",
            Socket.Wrists   => "Wrists",
            Socket.Rings    => "RFinger",
            Socket.MainHand => "MainHand",
            Socket.OffHand  => "OffHand",
            _               => null,
        };

    /// <summary> A colour 0xRRGGBB as Glamourer's parameter (red, green, blue from 0 to 1, as its colour picker shows them). </summary>
    public static (float R, float G, float B) ToParameter(uint rgb)
        => (((rgb >> 16) & 0xFF) / 255f, ((rgb >> 8) & 0xFF) / 255f, (rgb & 0xFF) / 255f);

    /// <summary> Glamourer's parameter as a colour 0xRRGGBB (values beyond 0..1 are clamped). </summary>
    public static uint FromParameter(float r, float g, float b)
    {
        static uint C(float v)
            => (uint)Math.Clamp((int)MathF.Round(v * 255f), 0, 255);
        return C(r) << 16 | C(g) << 8 | C(b);
    }

    public static string Json(string name, IReadOnlyList<Piece> pieces, IReadOnlyDictionary<string, uint> colours, IReadOnlyDictionary<string, bool> showing)
    {
        var now = DateTimeOffset.UtcNow.ToString("O");
        var equipment = new JsonObject();
        foreach (var piece in pieces)
            if (SlotOf(piece.Socket) is { } slot && piece.Item != 0)
                equipment[slot] = new JsonObject { ["ItemId"] = piece.Item, ["Stain"] = 0, ["Stain2"] = 0, ["Crest"] = false, ["Apply"] = true, ["ApplyStain"] = false, ["ApplyCrest"] = false };
        if (showing.TryGetValue("hat", out var hat))
            equipment["Hat"] = new JsonObject { ["Show"] = hat, ["Apply"] = true };
        if (showing.TryGetValue("visor", out var visor))
            equipment["Visor"] = new JsonObject { ["IsToggled"] = visor, ["Apply"] = true };
        if (showing.TryGetValue("weapon", out var weapon))
            equipment["Weapon"] = new JsonObject { ["Show"] = weapon, ["Apply"] = true };

        var parameters = new JsonObject();
        foreach (var (key, rgb) in colours)
            if (ParameterOf(key) is { } parameter)
            {
                var (r, g, b) = ToParameter(rgb);
                var entry = new JsonObject { ["Red"] = r, ["Green"] = g, ["Blue"] = b, ["Apply"] = true };
                if (parameter is "LipDiffuse" or "DecalColor")
                    entry["Alpha"] = 1f;
                parameters[parameter] = entry;
            }

        var mods = new JsonArray();
        foreach (var piece in pieces.GroupBy(p => p.Mod, StringComparer.OrdinalIgnoreCase).Select(g => g.First()))
        {
            var settings = new JsonObject();
            foreach (var (group, chosen) in piece.Options)
                settings[group] = new JsonArray(chosen.Select(c => (JsonNode?)JsonValue.Create(c)).ToArray());
            mods.Add(new JsonObject { ["Name"] = piece.ModName, ["Directory"] = piece.Mod, ["Enabled"] = true, ["Priority"] = 0, ["Settings"] = settings });
        }

        var design = new JsonObject
        {
            ["FileVersion"]  = 2,
            ["Identifier"]   = Guid.NewGuid().ToString(),
            ["CreationDate"] = now,
            ["LastEdit"]     = now,
            ["Name"]         = name,
            ["Description"]  = "Mod Closet",
            ["Equipment"]    = equipment,
            ["Customize"]    = new JsonObject { ["ModelId"] = 0 },
            ["Parameters"]   = parameters,
            ["Mods"]         = mods,
        };
        return design.ToJsonString();
    }
}
