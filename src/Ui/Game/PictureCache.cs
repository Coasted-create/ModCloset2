using Dalamud.Interface.Textures.TextureWraps;
using Dalamud.Plugin.Services;
using ModCloset2.Ui.Canvas;

namespace ModCloset2.Ui.Game;

/// <summary>
/// Mod pictures and photos for the window, loaded lazily through Dalamud's texture provider (it decodes on its own
/// threads, never in Draw). Until a picture has arrived the caller draws the tinted tile. The bookkeeping is the pure
/// <see cref="PictureLoads{T}"/>: a few new loads per frame and a bounded number at once; finished loads collected every
/// frame, also those whose card was scrolled away (else they would keep their place among the loads under way, and
/// after enough scrolling no new picture would load at all); failed loads tried again a few times; the least recently
/// drawn pictures let go first.
/// </summary>
public sealed class PictureCache : IDisposable
{
    private readonly PictureLoads<IDalamudTextureWrap> _loads;

    public PictureCache(ITextureProvider textures)
        => _loads = new PictureLoads<IDalamudTextureWrap>(
            path => textures.GetFromFile(path).RentAsync(),
            wrap => (long)wrap.Width * wrap.Height * 4,
            wrap => wrap.Dispose(),
            () => Environment.TickCount64 / 1000.0);

    public int Count
        => _loads.Count;

    /// <summary> The picture if it is ready; null while it loads, after it failed (until it is tried again), or when this frame's loads are used up. </summary>
    public IDalamudTextureWrap? Get(string path)
        => _loads.Get(path);

    /// <summary> Call once per frame after drawing. </summary>
    public void EndFrame()
        => _loads.EndFrame();

    /// <summary> Forget everything (the window was closed a while: let the memory go). </summary>
    public void Clear()
        => _loads.Clear();

    public void Dispose()
        => Clear();
}
