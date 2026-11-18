using ModCloset2.Looks;

namespace ModCloset2.Closet;

/// <summary>
/// The closet's part of the plugin's unload, in the one order that leaves nothing behind, every step guarded on its own
/// (a step that throws is reported and the next ones still run). The plugin puts back first (<see cref="PutBack"/>), lets
/// its faces go, then runs <see cref="Run"/>. The tests run the same two calls.
/// </summary>
public static class ClosetShutdown
{
    /// <summary> Whatever is tried goes back at once, a look's colours with it (the first thing on unload). </summary>
    public static void PutBack(ClosetSession? session, Action<string, Exception> failed)
        => Step("put back", session is null ? null : session.PutBack, failed);

    /// <param name="started"> False for a load that failed partway: nothing is remembered then, only let go. </param>
    public static void Run(ClosetSession? session, Action? disposeWardrobe, LooksEngine? looks, IDisposable? looksFile, IDisposable? colours,
        IDisposable? colourFile, bool started, Action<string, Exception> failed)
    {
        Step("session", session is null ? null : session.Dispose, failed);

        // the wardrobe puts back at once what is still tried (its saved look too)
        Step("wardrobe", disposeWardrobe, failed);

        // the looks remember an outfit changed since the last one (they read the colours for it); their file is written
        if (started && looks is not null)
            Step("looks", looks.ClosetClosed, failed);
        Step("looks", looks is null ? null : looks.Flush, failed);
        Step("looks file", looksFile is null ? null : looksFile.Dispose, failed);

        // after the wardrobe has put back: the colours end a try and pointing, and make one last pass (also while the
        // wardrobe is still busy), so nothing of a try stays on the gear; then their file is written
        Step("colours", colours is null ? null : colours.Dispose, failed);
        Step("colours file", colourFile is null ? null : colourFile.Dispose, failed);
    }

    private static void Step(string what, Action? action, Action<string, Exception> failed)
    {
        if (action is null)
            return;
        try
        {
            action();
        }
        catch (Exception ex)
        {
            failed(what, ex);
        }
    }
}
