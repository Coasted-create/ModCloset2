using Dalamud.Game.Command;
using Dalamud.Interface.Windowing;
using Dalamud.Plugin;
using Dalamud.Plugin.Services;
using ModCloset2.Closet;
using ModCloset2.Colours;
using ModCloset2.Colours.Game;
using ModCloset2.Core;
using ModCloset2.Game;
using ModCloset2.Library;
using ModCloset2.Looks;
using ModCloset2.OnYou.Game;
using ModCloset2.Ui.Game;
using ModCloset2.Wear;

// Dalamud and its ImGui bindings only run on Windows.
[assembly: System.Runtime.Versioning.SupportedOSPlatform("windows")]

namespace ModCloset2;

/// <summary>
/// Mod Closet 2: the plugin shell. Makes the library, the wardrobe and the session, the window, and the command. Lives next
/// to the old Mod Closet: its own name, command, window ids, IPC tag and settings file, and it never writes the old one's
/// files.
/// </summary>
public sealed class Plugin : IDalamudPlugin
{
    private const string Command = "/closet2";

    private readonly IDalamudPluginInterface _pi;
    private readonly ICommandManager _commands;
    private readonly IFramework _framework;
    private readonly IChatGui _chat;
    private readonly IPluginLog _log;
    private readonly WindowSystem _windows = new("ModCloset2");

    /// <summary> The closet window (faces A and B). </summary>
    private readonly ClosetWindow _window;

    /// <summary> The plain window of the first build: the fallback if the real one misbehaves (/closet2 debug). </summary>
    private readonly DebugFace _debug;
    private readonly GameState _game;
    private readonly IKeyState _keys;
    private readonly ItemIndex _items;

    /// <summary> "On you" (face C): the character's body on screen and the overlay drawn over it. </summary>
    private readonly BodyOnScreen _body;
    private readonly OnYouOverlay _onYou;
    private bool _windowWasOpen;
    private bool _facesWereOpen;
    private readonly LooksFileStore _looksFile;
    private readonly ColourFileStore _colourFile;

    public Configuration Config { get; }
    public Ipc Ipc { get; }
    public ModLibrary Library { get; }
    public Journal Journal { get; }
    public Wardrobe Wardrobe { get; }
    public ClosetSession Session { get; }

    /// <summary> The colour engine (src/Colours), shown by the faces as <see cref="IClosetSession.Colours"/>. </summary>
    public OutfitColours Colours { get; }

    /// <summary> The looks engine (src/Looks), shown by the faces as <see cref="IClosetSession.Looks"/>. </summary>
    public LooksEngine Looks { get; }

    public Plugin(IDalamudPluginInterface pi, ICommandManager commands, IFramework framework, IObjectTable objects, IClientState clientState,
        IDataManager data, IPluginLog log, ICondition condition, IChatGui chat, ITextureProvider textures, IKeyState keyState,
        IGameGui gameGui)
    {
        _pi        = pi;
        _commands  = commands;
        _framework = framework;
        _chat      = chat;
        _log       = log;
        _keys      = keyState;

        Config = pi.GetPluginConfig() as Configuration ?? new Configuration();
        ApplyLanguage();
        pi.LanguageChanged += OnDalamudLanguageChanged;

        // A load that fails halfway must not leave threads, IPC subscriptions or game hooks behind: what was made is
        // undone in reverse order and the failure goes on to Dalamud.
        try
        {
            var configDir = pi.GetPluginConfigDirectory();
            Directory.CreateDirectory(configDir);
            var pluginConfigs = pi.ConfigFile.DirectoryName ?? Path.GetDirectoryName(configDir) ?? configDir;

            Ipc     = new Ipc(pi);
            _game   = new GameState(objects, clientState, condition);
            _items  = new ItemIndex(data, log);
            Library = new ModLibrary(Ipc, _items, log, configDir, pluginConfigs, _game.Player, MyBody, new Detect.Detector());

            // the journal and the wardrobe know each other: undo redraws through the wardrobe's redraw gate
            Wardrobe? wardrobe = null;
            Journal  = new Journal(Ipc, Config.Worn, SaveConfig, _game.PlayerKey, () => wardrobe?.RedrawFromJournal(), log, configDir);
            Wardrobe = wardrobe = new Wardrobe(Ipc, _game, Config, SaveConfig, log, Journal, Library.Part, Library.Mod, MyBody)
            {
                PriorityAdvisor = AdvisePriority,
            };

            Session = new ClosetSession(Library, Wardrobe, Config.Closet, SaveConfig, applyLanguage: _ => ApplyLanguage(),
                log: m => _log.Debug($"Mod Closet 2: {m}"));
            Ipc.ModMoved += OnModMoved;

            // "Make it its own mod": Penumbra's side, and the file work (reads one mod folder, writes one new folder)
            Session.PenumbraMods = new PenumbraMods(Ipc);
            Session.OwnModMaker  = new ModCloset2.OwnMod.OwnModMaker();

            // photos of pieces: drawn on the closet's own worker from the mod's files (no character is touched), kept in
            // photos/ beside the settings; they wait in combat, in a duty, in a cutscene or group pose and while a zone loads
            Session.PhotoFolder = Path.Combine(configDir, "photos");
            Session.PhotosWait  = () => _game.Zoning || _game.InCutscene || _game.GPosing || _game.InCombatOrDuty;
            Session.PhotoRace   = () => _game.Player().RaceCode;
            // the photos' lines (one summary per stretch of work, failures once a session) reach Dalamud's log file
            Session.PhotoLog = m => _log.Information($"Mod Closet 2: {m}");
            // the game's own unmodded files (read on the photo worker, never on the frame), counted for the summary line
            var gameFiles = new ModCloset2.Game.GameFiles(new LuminaGameFiles(data), Session.PhotoLog);
            Session.PhotoGameFiles = gameFiles;
            Session.PhotoMaker  = new ModCloset2.Photo.PhotoMaker(gameFiles);

            // the colours: what shows comes from the session's tries and the wardrobe's kept outfit, their memory is kept
            // with the session's remembered things, and every change is one step of the session's history
            var world = new ColourWorld(Session, Wardrobe, () => _game.PlayerAddress, _game.WhyNot, GameItemIn, busy: () => _game.Zoning,
                playerKey: _game.PlayerKey);
            // the colours' memory has a file of its own, written in the background (it moves over from the settings once)
            _colourFile = new ColourFileStore(configDir, Session, m => _log.Warning($"Mod Closet 2 colours: {m}"));
            Colours = ColoursSetup.Create(world, new GlamourerStates(Ipc), _colourFile, new ColourUndo(Session, () => Colours), data, log);
            Session.Colours   = Colours;
            Session.ColourTry = new ColourTryLayer(Colours);

            // the looks: the session is their outfit and try-on; their file sits next to the settings, the old plugin's
            // settings are only read (once, for the old looks)
            _looksFile = new LooksFileStore(configDir, Path.Combine(pluginConfigs, "ModCloset.json"), m => _log.Warning($"Mod Closet 2 looks: {m}"));
            Looks = new LooksEngine(Session, new LooksLibrary(Session, Library), new LooksColours(Colours), _looksFile,
                new LooksUndo(Session, () => Looks), new SystemLooksClock(), Library.Detector, m => _log.Information(m));
            Session.Looks = Looks;

            // both put back what is being tried when they close (their OnClose)
            _window = new ClosetWindow(Session, pi.UiBuilder, textures, log);
            _debug  = new DebugFace(Session, ex => _log.Error(ex, "Mod Closet 2: drawing failed"));
            _windows.AddWindow(_window);
            _windows.AddWindow(_debug);

            // On you: the window's third face switch starts it (the window closes without putting back); opening the
            // window ends it (see OnFrameworkUpdate)
            _body  = new BodyOnScreen(framework, objects, clientState, condition, gameGui, log);
            _onYou = new OnYouOverlay(Session, pi.UiBuilder, textures, _body, log)
            {
                WindowOpen = () => _window.IsOpen,
                OpenWindow = _ => _window.IsOpen = true, // the face has already selected the shelf
            };
            _window.OnYouRequested = _onYou.Start;
            _commands.AddHandler(Command, new CommandInfo(OnCommand)
            {
                HelpMessage = "Open Mod Closet 2. /closet2 rescan reads your mods again; /closet2 census writes the census file; /closet2 debug opens the plain window.",
            });

            _pi.UiBuilder.Draw       += _windows.Draw;
            _pi.UiBuilder.Draw       += _onYou.Draw; // after the windows: its GridHovered comes last in the frame
            _pi.UiBuilder.OpenMainUi += _window.Toggle;
            // Dalamud's plugin list: the cog opens the closet with the settings sheet showing
            _pi.UiBuilder.OpenConfigUi += _window.OpenSettings;
            _framework.Update        += OnFrameworkUpdate;
        }
        catch
        {
            UndoLoad();
            throw;
        }
    }

    // ---- the loop ----

    private void OnFrameworkUpdate(IFramework _)
    {
        Timed("ipc", () => Ipc.Refresh());
        Timed("library", () =>
        {
            Library.CheckProblem();
            Library.Update();
        });
        // opening the window ends On you (what is tried stays: the window shows it)
        if (_window.IsOpen && !_windowWasOpen)
            Timed("on you", _onYou.Stop);
        _windowWasOpen = _window.IsOpen;

        Timed("wardrobe", () =>
        {
            Wardrobe.Watching  = _window.IsOpen || _debug.IsOpen || _onYou.Face.Visible || _onYou.Face.StaysOn;
            Wardrobe.Following = true; // the colours and the looks follow what is worn, faces open or not
            Wardrobe.Update();
        });
        // the lists are only kept fresh while a face shows them; with none open only a failed Keep or a try left on the
        // character is looked at (review M1). On you pulls what it reads.
        if (Wardrobe.Watching)
            Timed("session", Session.Update);
        else
            Timed("session", Session.Idle);
        // every frame, faces open or not: a newly shown piece gets its remembered colours (and the outfit colours with
        // "Dress everything"); it returns at once when nothing changed and reads the drawn models once a second
        Timed("colours", Colours.Tick);
        // also with no face open: the quiet minute that remembers an outfit runs here (~2 µs when nothing changed)
        Timed("looks", Looks.Tick);

        // the last face closed (after the window's Put back): an outfit changed since the last one is remembered now
        var facesOpen = Wardrobe.Watching;
        if (_facesWereOpen && !facesOpen)
            Timed("looks", Looks.ClosetClosed);
        _facesWereOpen = facesOpen;
        Timed("keys", () =>
        {
            _window.FilterGameKeys(_keys);
            _onYou.FilterGameKeys(_keys);
        });
        SaveIfDue();
    }

    private readonly Dictionary<string, DateTime> _slowLogged = new();

    /// <summary>
    /// Runs one part of the update; never lets it break the game's frame. When it takes long, one log line says what ran and
    /// where the time went (FrameReport: the steps, the calls into other plugins and the slowest of them). At most one such
    /// line per part every 5 s, but a frame over 200 ms is always logged.
    /// </summary>
    private void Timed(string name, Action action, double limitMs = 30)
    {
        FrameReport.Begin();
        var start = System.Diagnostics.Stopwatch.GetTimestamp();
        try
        {
            action();
        }
        catch (Exception ex)
        {
            if (!_slowLogged.TryGetValue("x" + name, out var last) || (DateTime.UtcNow - last).TotalSeconds > 10)
            {
                _slowLogged["x" + name] = DateTime.UtcNow;
                _log.Error(ex, $"Mod Closet 2: {name} failed");
            }

            return;
        }

        var ms = System.Diagnostics.Stopwatch.GetElapsedTime(start).TotalMilliseconds;
        if (SlowFrames.ShouldLog(name, ms, limitMs, DateTime.UtcNow, _slowLogged))
            _log.Warning(FrameReport.Describe(name, ms));
    }

    /// <summary> The game's own item in a gear slot (Glamourer's order), by name; null for an empty or unknown slot. </summary>
    private string? GameItemIn(int slot)
        => slot >= 0 && slot < GearSlots.Worn.Length && Wardrobe.ItemIn(GearSlots.Worn[slot].Name) is var id and not 0 ? _items.NameOf(id) : null;

    /// <summary> Glamourer's state calls for the colours, through the plugin's Ipc. </summary>
    private sealed class GlamourerStates(Ipc ipc) : IGlamourerStates
    {
        public bool Available
            => ipc.GlamourerAvailable;

        public Newtonsoft.Json.Linq.JObject? GetState(int objectIndex, out int code)
            => ipc.GetStateJson(objectIndex, out code);

        public int ApplyState(Newtonsoft.Json.Linq.JObject state, int objectIndex, ulong flags)
            => ipc.ApplyState(state, objectIndex, flags);
    }

    // ---- the body and priorities ----

    private (string Body, DateTime At) _bodyCache = (string.Empty, DateTime.MinValue);

    /// <summary>
    /// The user's body ("Rue"): picked in the settings, else picked in the old Mod Closet, else the body the enabled body
    /// mods are made for (by their names; cached for 10 s). "" when unknown.
    /// </summary>
    public string MyBody()
    {
        if (Config.MyBody.Length > 0)
            return Config.MyBody;
        if (Library.OldBody.Length > 0)
            return Library.OldBody;
        if ((DateTime.UtcNow - _bodyCache.At).TotalSeconds < 10)
            return _bodyCache.Body;

        var votes = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        foreach (var part in Library.Parts.Where(p => p.Key.Socket == Socket.BodyBase))
        {
            if (Wardrobe is null || !Wardrobe.IsEnabled(part.Mod.Directory))
                continue;
            var names = BodyFit.NamesIn(part.Name);
            foreach (var name in names)
                votes[name] = votes.GetValueOrDefault(name) + (names.Count == 1 ? 2 : 1);
        }

        var body = votes.Count == 0 ? string.Empty : votes.OrderByDescending(v => v.Value).First().Key;
        _bodyCache = (body, DateTime.UtcNow);
        return body;
    }

    /// <summary>
    /// The priority a kept mod needs: just above the enabled mods that have parts in the same sockets (they are what it
    /// competes with there), or its own priority when nothing competes. Null while the library is still being read: then
    /// the wardrobe goes above everything, as the old plugin did without its analysis. The check after Keep raises a part
    /// that still loses.
    /// </summary>
    private int? AdvisePriority(string mod, IReadOnlyCollection<Socket> sockets, int current, IReadOnlyCollection<string> except)
    {
        if (!Library.Progress.Ready)
            return null;
        var top = int.MinValue;
        foreach (var part in Library.Parts)
        {
            if (!sockets.Contains(part.Key.Socket) || part.Mod.Directory.Equals(mod, StringComparison.OrdinalIgnoreCase)
             || except.Contains(part.Mod.Directory, StringComparer.OrdinalIgnoreCase))
                continue;
            // as Penumbra uses it now: a temporary setting of another plugin (a Glamourer design's body) counts
            if (Wardrobe.Effective(part.Mod.Directory) is { Enabled: true } s && s.Priority > top)
                top = s.Priority;
        }

        return top == int.MinValue ? current : Math.Max(current, top + 1);
    }

    // ---- commands ----

    private void OnCommand(string _, string arguments)
    {
        switch (arguments.Trim().ToLowerInvariant())
        {
            case "":
                _window.Toggle();
                return;
            case "rescan":
                Session.Rescan();
                Say();
                return;
            case "census":
                Session.WriteCensus();
                Say();
                return;
            case "debug":
                _debug.Toggle();
                return;
            default:
                _chat.Print(Loc.T("/closet2 opens the closet. /closet2 rescan reads your mods again. /closet2 census writes the census file. /closet2 debug opens the plain window."));
                return;
        }
    }

    private void Say()
    {
        if (Session.Status is { Text.Length: > 0 } status)
        {
            if (status.IsError)
                _chat.PrintError(Loc.F("[Mod Closet 2] {0}", status.Text));
            else
                _chat.Print(Loc.F("[Mod Closet 2] {0}", status.Text));
        }
    }

    // ---- language ----

    /// <summary> Uses the language from the settings, or Dalamud's when it is set to follow the game. </summary>
    private void ApplyLanguage()
        => Loc.SetLanguage(Config.Closet.Language.Length > 0 ? Config.Closet.Language : _pi.UiLanguage);

    private void OnDalamudLanguageChanged(string code)
    {
        if (Config.Closet.Language.Length == 0)
            ApplyLanguage();
    }

    // ---- renamed mods ----

    private void OnModMoved(string oldDirectory, string newDirectory)
    {
        Wardrobe.OnModMoved(oldDirectory, newDirectory);
        Looks.ModMoved(oldDirectory, newDirectory);
        Colours.ModMoved(oldDirectory, newDirectory);
        if (Config.Closet.RenameMod(oldDirectory, newDirectory))
            SaveConfig();
    }

    // ---- saving (debounced: a flip through twenty cards is one write) ----

    private DateTime? _saveDue;

    public void SaveConfig()
        => _saveDue ??= DateTime.UtcNow.AddSeconds(2);

    private void SaveIfDue()
    {
        if (_saveDue is { } due && DateTime.UtcNow >= due)
            SaveNow();
    }

    private void SaveNow()
    {
        _saveDue = null;
        try
        {
            _pi.SavePluginConfig(Config);
        }
        catch (Exception ex)
        {
            _log.Warning($"Mod Closet 2: could not save the settings: {ex.Message}");
        }
    }

    /// <summary> Undoes a load that failed partway: each piece that was made is let go, newest first; nothing here throws. </summary>
    private void UndoLoad()
        => Unload(started: false);

    public void Dispose()
        => Unload(started: true);

    /// <summary>
    /// Unload, one guarded step after the other: a step that throws is logged and the next ones still run, so the put back,
    /// the files, the threads and the unsubscribing always happen. The put back comes first (the character must never stay
    /// half dressed); the colours go after the wardrobe has put back and after the looks have read them.
    /// </summary>
    private void Unload(bool started)
    {
        void Failed(string what, Exception ex)
            => _log.Warning($"Mod Closet 2: unloading ({what}) failed: {ex.Message}");

        void Step(string what, Action? action)
        {
            if (action is null)
                return;
            try
            {
                action();
            }
            catch (Exception ex)
            {
                Failed(what, ex);
            }
        }

        Step("framework", () => _framework.Update -= OnFrameworkUpdate);

        // whatever is tried goes back at once, a look's colours with it
        if (started)
            ClosetShutdown.PutBack(Session, Failed);

        if (_onYou is not null)
        {
            Step("on you", () => _pi.UiBuilder.Draw -= _onYou.Draw);
            Step("on you", _onYou.Dispose);
        }
        if (_body is not null)
            Step("on you", _body.Dispose);
        if (_window is not null)
        {
            Step("window", () => _pi.UiBuilder.Draw       -= _windows.Draw);
            Step("window", () =>
            {
                _pi.UiBuilder.OpenMainUi   -= _window.Toggle;
                _pi.UiBuilder.OpenConfigUi -= _window.OpenSettings;
            });
        }
        Step("language", () => _pi.LanguageChanged -= OnDalamudLanguageChanged);
        Step("command", () => _commands.RemoveHandler(Command));
        if (Ipc is not null)
            Step("mod moved", () => Ipc.ModMoved -= OnModMoved);

        // session, wardrobe, looks, colours and their files, in the order that leaves nothing behind (ClosetShutdown)
        ClosetShutdown.Run(Session, Wardrobe is null ? null : Wardrobe.Dispose, Looks, _looksFile, Colours, _colourFile, started, Failed);

        Step("journal", Journal is null ? null : Journal.Flush);
        Step("library", Library is null ? null : Library.Dispose);
        Step("game", _game is null ? null : _game.Dispose);
        Step("ipc", Ipc is null ? null : Ipc.Dispose);
        Step("windows", _windows.RemoveAllWindows);
        Step("window", _window is null ? null : _window.Dispose);
        if (started)
            SaveNow();
    }
}
