using ModCloset2.Colours;
using ModCloset2.Core;
using ModCloset2.Library;

namespace ModCloset2.Closet;

// The foundation's side of the colour engine's seams (src/Colours/Seams.cs): what shows in each gear slot (from the
// session's tries and the wardrobe's kept outfit), the store (the session's Remember), and undo (one step in the
// session's history). No Dalamud types: the plugin hands in the game facts, the tests hand in their own.

/// <summary> The player and what they wear, for the colours. </summary>
public sealed class ColourWorld : IColourWorld
{
    private readonly ClosetSession _session;
    private readonly IWardrobe _wardrobe;
    private readonly Func<nint> _player;
    private readonly Func<string> _whyNot;
    private readonly Func<int, string?> _gameItem;
    private readonly Func<int> _gameItemsVersion;
    private readonly Func<bool> _busy;
    private readonly Func<string> _playerKey;

    private (int Wardrobe, int Session, int Items) _seen = (int.MinValue, 0, 0);
    private IReadOnlyList<SlotShowing> _showing = Array.Empty<SlotShowing>();
    private int _version;

    /// <param name="player"> The player's game object, 0 when none. </param>
    /// <param name="whyNot"> Why the character cannot be coloured now (one translated sentence), "" when it can. </param>
    /// <param name="gameItem"> The name of the game's own item in a gear slot (Glamourer's order), null for an empty slot. </param>
    /// <param name="gameItemsVersion"> Changes when <paramref name="gameItem"/> may say something else (default: never on its own). </param>
    /// <param name="busy"> The game is busy with the character besides the wardrobe's own redraws (zoning). </param>
    /// <param name="playerKey"> Who the player is, "name@world" ("" when nobody is logged in): the colours' rows belong to them. </param>
    public ColourWorld(ClosetSession session, IWardrobe wardrobe, Func<nint> player, Func<string> whyNot, Func<int, string?> gameItem,
        Func<int>? gameItemsVersion = null, Func<bool>? busy = null, Func<string>? playerKey = null)
    {
        _playerKey        = playerKey ?? (() => string.Empty);
        _busy             = busy ?? (() => false);
        _session          = session;
        _wardrobe         = wardrobe;
        _player           = player;
        _whyNot           = whyNot;
        _gameItem         = gameItem;
        _gameItemsVersion = gameItemsVersion ?? (() => 0);
    }

    /// <summary> Moves only when the slots really say something else (a try, a put back, a keep, an undo, another item). </summary>
    public int Version
    {
        get
        {
            Refresh();
            return _version;
        }
    }

    public IReadOnlyList<SlotShowing> Showing
    {
        get
        {
            Refresh();
            return _showing;
        }
    }

    public int PlayerIndex
        => 0;

    public nint PlayerAddress
        => _player();

    public string WhyNot
        => _whyNot();

    public string PlayerKey
        => _playerKey();

    public bool Busy
        => _wardrobe.Busy || _busy();

    private void Refresh()
    {
        var seen = (_wardrobe.Version, _session.ShowingStamp, _gameItemsVersion());
        if (seen == _seen)
            return;
        _seen = seen;
        var showing = _session.ShowingNow(_gameItem)
            .Select(g => new SlotShowing(g.Slot, g.Key, g.Piece, g.PieceName, g.Icon, g.Name, g.Picture, g.Trying))
            .ToList();
        if (showing.SequenceEqual(_showing))
            return;
        _showing = showing;
        _version++;
    }
}

/// <summary> The colours' memory, kept with the session's other remembered things (saved with the settings). </summary>
public sealed class ColourStore(IClosetSession session) : IColourStore
{
    public string Get(string key)
        => session.Remembered(key);

    public void Set(string key, string value)
        => session.Remember(key, value);
}

/// <summary>
/// A colour change as one step of the session's history, so Undo walks colours, tries, keeps and sorting in the order
/// they happened. Taking it back hands the "before" snapshot to the engine (<see cref="OutfitColours.Restore"/>).
/// </summary>
public sealed class ColourUndo(ClosetSession session, Func<OutfitColours?> engine) : IColourUndo
{
    public void Record(string description, string before, string after)
        => session.RecordColourStep(description, () => engine()?.Restore(before));
}

/// <summary> The colours as the looks see them (a look's colourways). </summary>
public sealed class LooksColours(OutfitColours colours) : Looks.ILooksColours
{
    public int Version
        => colours.Version;

    public string? Snapshot(IReadOnlyCollection<PartKey> parts)
        => colours.Snapshot(parts);

    public uint? ShowingMain(PartKey part)
        => colours.ShowingMain(part);
}

/// <summary> The colour engine's try layer, for the session (a look's colours come and go with its try). </summary>
public sealed class ColourTryLayer(OutfitColours colours) : ITryColours
{
    public void TryOn(string snapshot)
        => colours.TryOn(snapshot);

    public (string Before, string After)? EndTry(bool keep, bool byUser = true)
        => colours.EndTry(keep, byUser);

    public void Restore(string snapshot)
        => colours.Restore(snapshot);
}

/// <summary>
/// The colours' memory in a file of its own (<c>colours.json</c> beside the settings), written in the background a second
/// after the last change: a colour step, or every change of gear with "Dress everything" on, never costs a write of the
/// whole settings file on the framework thread. On the first start with it, what the settings held so far comes over
/// once (and leaves the settings).
/// </summary>
public sealed class ColourFileStore : IColourStore, IDisposable
{
    /// <summary> The colours' keys in the settings of the builds before this file. </summary>
    public const string OldPrefix = "colours.";

    private readonly BackgroundFile _file;
    private readonly Action<string>? _log;
    private readonly object _gate = new();
    private readonly Dictionary<string, string> _values = new(StringComparer.Ordinal);

    public ColourFileStore(string folder, ClosetSession session, Action<string>? log = null, TimeSpan? delay = null)
    {
        _file = new BackgroundFile(Path.Combine(folder, "colours.json"), delay ?? TimeSpan.FromSeconds(1), log);
        _log  = log;
        if (File.Exists(_file.Path))
        {
            // "there but cannot be read now" is not "no file": then nothing of this start is written over it
            Holding = !Load();
            if (Holding)
                _log?.Invoke("colours.json is there but cannot be read now: the colours start empty and write nothing over it until the next start");
            return;
        }

        foreach (var key in session.RememberedKeys(OldPrefix))
            if (session.Remembered(key) is { Length: > 0 } value)
                _values[key] = value;
        if (_values.Count == 0)
            return;
        _file.Write(Text);
        _file.Flush();
        if (File.Exists(_file.Path))
            foreach (var key in session.RememberedKeys(OldPrefix))
                session.Forget(key);
    }

    /// <summary> How many times the file was written (the tests read it). </summary>
    public int Writes
        => _file.Writes;

    /// <summary>
    /// The file is there but could not be read at this start (locked, a disk error): what the colours do now is kept in
    /// memory only and never written over it, because the colour engine read its memory as empty and would write that
    /// over what the file holds. The next start reads it again.
    /// </summary>
    public bool Holding { get; }

    /// <summary> How often a locked file is tried before the start gives up on it. </summary>
    private const int ReadAttempts = 5;

    /// <summary> Reads the file. False when it cannot be read now (then nothing may be written over it). </summary>
    private bool Load()
    {
        string? text = null;
        for (var attempt = 0; attempt < ReadAttempts && text is null; ++attempt)
        {
            try
            {
                text = ModFolder.ReadShared(_file.Path);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                if (attempt == ReadAttempts - 1)
                {
                    _log?.Invoke($"colours.json could not be read now: {ex.Message}");
                    return false;
                }

                Thread.Sleep(100);
            }
        }

        try
        {
            foreach (var (key, value) in System.Text.Json.JsonSerializer.Deserialize<Dictionary<string, string>>(text!) ?? new Dictionary<string, string>())
                _values[key] = value;
            return true;
        }
        catch (Exception ex)
        {
            // read, but damaged: kept beside it, then the colours start anew; if even that copy fails, nothing is written
            _log?.Invoke($"colours.json is damaged: {ex.Message}");
            try
            {
                File.WriteAllText(Path.Combine(Path.GetDirectoryName(_file.Path)!, "colours.damaged.json"), text);
                _values.Clear();
                return true;
            }
            catch
            {
                _values.Clear();
                return false;
            }
        }
    }

    public string Get(string key)
    {
        lock (_gate)
            return _values.GetValueOrDefault(key, string.Empty);
    }

    public void Set(string key, string value)
    {
        lock (_gate)
        {
            if (_values.TryGetValue(key, out var old) && old == value)
                return;
            _values[key] = value;
        }

        if (!Holding)
            _file.Write(Text);
    }

    /// <summary> The whole memory as JSON, made on the writer's thread from a copy. </summary>
    private string Text()
    {
        Dictionary<string, string> copy;
        lock (_gate)
            copy = new Dictionary<string, string>(_values);
        return System.Text.Json.JsonSerializer.Serialize(copy);
    }

    public void Flush()
        => _file.Flush();

    public void Dispose()
        => Flush();
}
