namespace ModCloset2.Closet;

/// <summary>
/// One file written away from the framework thread by exactly one writer at a time: the newest text wins, written to a
/// temporary file and moved over the old one. <see cref="Write"/> hands over how to make the text (it is made on the
/// writer's thread, so a large text costs the frame nothing). <see cref="Flush"/> (unload) waits for the writer and
/// writes what is still waiting; it never writes beside a running writer.
/// </summary>
public sealed class BackgroundFile
{
    private readonly TimeSpan _delay;
    private readonly Action<string>? _log;
    private readonly object _gate = new();
    private readonly object _writing = new();
    private Func<string>? _pending;
    private Task? _writer;

    /// <param name="delay"> How long the writer waits before it takes the text (a burst of changes is one write). </param>
    public BackgroundFile(string path, TimeSpan delay, Action<string>? log = null)
    {
        Path   = path;
        _delay = delay;
        _log   = log;
    }

    public string Path { get; }

    /// <summary> How many times the file was written (the tests read it). </summary>
    public int Writes { get; private set; }

    public void Write(Func<string> text)
    {
        lock (_gate)
        {
            _pending = text;
            if (_writer is not null)
                return;
            _writer = Task.Run(Run);
        }
    }

    private void Run()
    {
        if (_delay > TimeSpan.Zero)
            Thread.Sleep(_delay);
        while (true)
        {
            Func<string> text;
            lock (_gate)
            {
                if (_pending is null)
                {
                    _writer = null;
                    return;
                }

                text     = _pending;
                _pending = null;
            }

            WriteNow(text);
        }
    }

    private void WriteNow(Func<string> make)
    {
        lock (_writing)
        {
            try
            {
                var text = make();
                var temp = Path + ".tmp";
                File.WriteAllText(temp, text);
                File.Move(temp, Path, true);
                Writes++;
            }
            catch (Exception ex)
            {
                _log?.Invoke($"{System.IO.Path.GetFileName(Path)} could not be written: {ex.Message}");
            }
        }
    }

    /// <summary> Writes what waits now, on this thread, after the writer (if any) has finished; never two writers at once. </summary>
    public void Flush()
    {
        Task? writer;
        lock (_gate)
            writer = _writer;
        try
        {
            // a disk that hangs must not hang the game's unload for ever; the write lock still keeps a late writer apart
            writer?.Wait(TimeSpan.FromSeconds(30));
        }
        catch
        {
            // the writer logs its own failures
        }

        Func<string>? text;
        lock (_gate)
        {
            text     = _pending;
            _pending = null;
        }

        if (text is not null)
            WriteNow(text);
    }
}
