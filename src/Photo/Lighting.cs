using System.Numerics;

namespace ModCloset2.Photo;

// Pure code (no game, no Dalamud): linked into tests/PhotoTests and tools/photo.

/// <summary>
/// One fixed studio light set, placed relative to the camera so every picture is lit the same way whatever side it is
/// taken from: a warm key light from the upper left, a cooler, weaker fill from the right, a rim light from behind that
/// draws the outline against the dark card, and a soft ambient that is brighter from above. Metals reflect a simple
/// studio surround (a bright ceiling, a dim floor) instead of the black of empty space, so a silver ring reads as metal.
/// </summary>
public sealed class Lighting
{
    private readonly (Vector3 Direction, Vector3 Colour)[] _lights;
    private readonly Vector3 _keyDirection;

    public static readonly Vector3 Sky = new(0.42f, 0.42f, 0.46f);
    public static readonly Vector3 Ground = new(0.16f, 0.14f, 0.14f);

    public Lighting(PhotoCamera camera)
    {
        Vector3 toCamera = -camera.Forward, right = camera.Right, up = camera.Up;
        Vector3 Dir(float r, float u, float c) => Vector3.Normalize(right * r + up * u + toCamera * c);

        _keyDirection = Dir(-0.55f, 0.6f, 0.6f);
        _lights =
        [
            (_keyDirection, new Vector3(1.0f, 0.96f, 0.9f) * 1.8f),
            (Dir(0.8f, 0.05f, 0.55f), new Vector3(0.85f, 0.9f, 1.0f) * 0.75f),
            (Dir(0.35f, 0.55f, -0.75f), new Vector3(1.0f, 0.95f, 1.0f) * 1.4f),
        ];
    }

    /// <summary> The light leaving a pixel towards the camera (linear). </summary>
    public Vector3 Light(in SurfaceLook s, Vector3 toEye)
    {
        var n         = s.Normal;
        var metal     = Math.Clamp(s.Metalness, 0, 1);
        var diffuse   = s.Base * (1 - metal * 0.85f);
        var specColour = Vector3.Lerp(s.Specular, s.Base, metal);
        var rough     = Math.Clamp(s.Roughness, 0.08f, 1);
        var shininess = Math.Clamp(2 / (rough * rough * rough * rough) - 2, 2, 600);
        var norm      = (shininess + 8) / (8 * MathF.PI);
        var nv        = Math.Max(0, Vector3.Dot(n, toEye));
        var fresnel   = MathF.Pow(1 - nv, 5);

        // soft ambient from a brighter ceiling and a darker floor
        var hemi  = 0.5f + 0.5f * n.Y;
        var light = diffuse * Vector3.Lerp(Ground, Sky, hemi) * s.Occlusion;

        foreach (var (l, colour) in _lights)
        {
            var nl = Vector3.Dot(n, l);
            if (nl <= 0)
                continue;
            light += diffuse * colour * nl * s.Occlusion;
            if (s.Matte)
                continue;
            var half = Vector3.Normalize(l + toEye);
            var nh   = Math.Max(0, Vector3.Dot(n, half));
            var spec = norm * MathF.Pow(nh, shininess) * nl;
            light += colour * spec * (specColour + (Vector3.One - specColour) * fresnel * 0.25f) * 0.5f;
        }

        // metals (and a little of any glossy surface at grazing angles) mirror the studio around them
        if (!s.Matte)
        {
            var r   = Vector3.Reflect(-toEye, n);
            var env = Surround(r, rough);
            light += env * specColour * metal * 1.3f * s.Occlusion;
            light += env * fresnel * (1 - rough) * 0.15f * (1 - metal);
        }

        return light + s.Emissive;
    }

    /// <summary> The studio around the piece as a mirror sees it: a bright ceiling, a softbox on the key side, a dim floor. </summary>
    private Vector3 Surround(Vector3 r, float roughness)
    {
        var up       = Math.Clamp(r.Y * 1.5f + 0.5f, 0, 1);
        var gradient = Vector3.Lerp(new Vector3(0.08f), new Vector3(0.95f, 0.93f, 0.95f), up * up * (3 - 2 * up));
        var box      = MathF.Pow(Math.Max(0, Vector3.Dot(r, _keyDirection)), 2 + 30 * (1 - roughness)) * 2.2f;
        return Vector3.Lerp(gradient + new Vector3(box), new Vector3(0.45f), roughness * 0.6f);
    }
}
