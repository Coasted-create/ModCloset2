using System.Text;
using FFXIVClientStructs.FFXIV.Client.Graphics.Scene;
using CsCharacterBase = FFXIVClientStructs.FFXIV.Client.Graphics.Scene.CharacterBase;
using CsGameObject = FFXIVClientStructs.FFXIV.Client.Game.Object.GameObject;
using CsResourceHandle = FFXIVClientStructs.FFXIV.Client.System.Resource.Handle.ResourceHandle;
using CsSceneObject = FFXIVClientStructs.FFXIV.Client.Graphics.Scene.Object;

namespace ModCloset2.Colours.Game;

/// <summary>
/// Reads the models the game draws for the player right now, read only: per gear slot the model file, its switched-on
/// parts and shape keys, and the material and texture files it really uses (with Penumbra's replacements: the handle
/// names are the files on disk). Ported unchanged in substance from the old plugin's Web/Model3d/LiveModels.cs, which
/// works in the game. Framework thread only (game memory).
/// </summary>
public sealed unsafe class GameModels : ILiveModels
{
    private const int GearSlots = 10;
    private const int MaxSlots = 32;
    private const int MaxMaterials = 64;
    private const int MaxTextures = 32;

    public IReadOnlyList<LiveModel>? Read(nint player)
    {
        try
        {
            return ReadUnsafe(player);
        }
        catch
        {
            // a character half torn down while zoning: nothing to read this time
            return null;
        }
    }

    private static List<LiveModel>? ReadUnsafe(nint gameObject)
    {
        if (gameObject == nint.Zero)
            return null;

        var draw = ((CsGameObject*)gameObject)->DrawObject;
        if (draw == null || ((CsSceneObject*)draw)->GetObjectType() != ObjectType.CharacterBase)
            return null;

        var cb    = (CsCharacterBase*)draw;
        var slots = cb->SlotCount;
        if (slots <= 0 || slots > MaxSlots || cb->Models == null)
            return null;

        var list = new List<LiveModel>();
        // the first ten model slots of a human are the gear slots, in Glamourer's order (head … left ring)
        for (var i = 0; i < Math.Min(slots, GearSlots); ++i)
        {
            var model = cb->Models[i];
            if (model == null || model->ModelResourceHandle == null)
                continue;
            var path = Name((CsResourceHandle*)model->ModelResourceHandle);
            if (path.Length == 0)
                continue;

            var count     = Math.Clamp(model->MaterialCount, 0, MaxMaterials);
            var materials = new LiveMaterial?[count];
            if (model->Materials != null)
                for (var j = 0; j < count; ++j)
                {
                    var material = model->Materials[j];
                    if (material == null || material->MaterialResourceHandle == null)
                        continue;
                    var handle = material->MaterialResourceHandle;
                    var mtrl   = Name((CsResourceHandle*)handle);
                    if (mtrl.Length == 0)
                        continue;
                    var textures = new string[Math.Min((int)handle->TextureCount, MaxTextures)];
                    for (var k = 0; k < textures.Length; ++k)
                    {
                        var tex = handle->Textures == null ? null : handle->Textures[k].TextureResourceHandle;
                        textures[k] = tex == null ? string.Empty : Name((CsResourceHandle*)tex);
                    }

                    materials[j] = new LiveMaterial(mtrl, textures);
                }

            list.Add(new LiveModel(i, path, model->EnabledAttributeIndexMask, model->EnabledShapeKeyIndexMask, materials));
        }

        return list;
    }

    /// <summary> The file of a loaded resource. Penumbra puts "|collection|" in front of files it replaced; that goes. </summary>
    private static string Name(CsResourceHandle* handle)
    {
        var bytes = handle->FileName.AsSpan();
        var end   = bytes.IndexOf((byte)0);
        if (end >= 0)
            bytes = bytes[..end];
        if (bytes.Length == 0 || bytes.Length > 1024)
            return string.Empty;
        return Clean(Encoding.UTF8.GetString(bytes));
    }

    /// <summary> "|abc_123|C:\mods\x.mdl" → "C:\mods\x.mdl"; game paths stay as they are (lower case, forward slashes). </summary>
    public static string Clean(string name)
    {
        var bar = name.LastIndexOf('|');
        if (bar >= 0)
            name = name[(bar + 1)..];
        return Path.IsPathRooted(name) ? name : name.Replace('\\', '/').ToLowerInvariant();
    }
}
