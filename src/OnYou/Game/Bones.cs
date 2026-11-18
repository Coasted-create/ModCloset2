using System.Numerics;
using FFXIVClientStructs.FFXIV.Client.Graphics.Scene;
using FFXIVClientStructs.Havok.Animation.Rig;
using CsCharacterBase = FFXIVClientStructs.FFXIV.Client.Graphics.Scene.CharacterBase;
using CsGameObject = FFXIVClientStructs.FFXIV.Client.Game.Object.GameObject;
using CsSceneObject = FFXIVClientStructs.FFXIV.Client.Graphics.Scene.Object;

namespace ModCloset2.OnYou.Game;

/// <summary>
/// Where the bones of a character are in the world this frame, read from the game's skeleton. Ported from Sculpt+
/// (<c>SculptBones.Read</c>, the user's own plugin, Apache-2.0), where it is proven in the game; only the world positions
/// are kept here. Framework thread (it reads game memory).
/// </summary>
public static unsafe class Bones
{
    /// <summary> Every named bone of the character with its place in the world. Empty when there is no skeleton (yet). </summary>
    public static Dictionary<string, Vector3> Read(nint gameObject)
    {
        var result = new Dictionary<string, Vector3>(StringComparer.Ordinal);
        if (gameObject == nint.Zero)
            return result;

        // the draw object is only a CharacterBase for a drawn character (not while it is being redrawn, not for a mount
        // or minion object); anything else has no human skeleton to read
        var draw = ((CsGameObject*)gameObject)->DrawObject;
        if (draw == null || ((CsSceneObject*)draw)->GetObjectType() != ObjectType.CharacterBase)
            return result;
        var skeleton = ((CsCharacterBase*)draw)->Skeleton;

        // a skeleton being built or torn down can have a garbage count: never walk more than the game ever has
        if (skeleton == null || skeleton->PartialSkeletons == null || skeleton->PartialSkeletonCount is <= 0 or > 32)
            return result;

        var position = (Vector3)skeleton->Transform.Position;
        var rotation = (Quaternion)skeleton->Transform.Rotation;
        var scale    = (Vector3)skeleton->Transform.Scale;

        // The body, the face, the hair, the tail… are partial skeletons of their own; each one's model pose is already in
        // the character's model space, so all of them go through the same root transform. A bone that is in two of them
        // (j_kao starts the face's and ends the body's) is taken from the first, the body's.
        for (var i = 0; i < skeleton->PartialSkeletonCount; ++i)
        {
            var poses = skeleton->PartialSkeletons[i].HavokPoses;
            if (poses.Length == 0 || poses[0] == 0)
                continue;
            var pose  = (hkaPose*)poses[0];
            var havok = pose->Skeleton;
            if (havok == null || havok->Bones.Data == null || pose->ModelPose.Data == null)
                continue;
            var count = Math.Min(havok->Bones.Length, pose->ModelPose.Length);
            if (count <= 0 || count > 2048)
                continue;
            for (var b = 0; b < count; ++b)
            {
                var name = havok->Bones.Data[b].Name.String;
                if (string.IsNullOrEmpty(name) || result.ContainsKey(name))
                    continue;
                var t     = pose->ModelPose.Data[b];
                var model = new Vector3(t.Translation.X, t.Translation.Y, t.Translation.Z);
                // a pose in the middle of being written can hold NaN: such a bone is left out this frame
                if (float.IsNaN(model.X) || float.IsNaN(model.Y) || float.IsNaN(model.Z))
                    continue;
                result[name] = position + Vector3.Transform(model * scale, rotation);
            }
        }

        return result;
    }
}
