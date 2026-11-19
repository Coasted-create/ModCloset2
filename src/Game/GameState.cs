using Dalamud.Game.ClientState.Conditions;
using System.Diagnostics;
using Dalamud.Plugin.Services;
using ModCloset2.Wear;

namespace ModCloset2.Game;

/// <summary> Who the player is and whether the character can be dressed right now. Framework thread only. </summary>
public sealed class GameState : IDisposable
{
    private readonly IObjectTable _objects;
    private readonly IClientState _clientState;
    private readonly ICondition _condition;

    public GameState(IObjectTable objects, IClientState clientState, ICondition condition)
    {
        _objects     = objects;
        _clientState = clientState;
        _condition   = condition;
        Pace         = new IpcPace(Clock);
        _clientState.TerritoryChanged += OnTerritoryChanged;
    }

    /// <summary> Seconds, rising (the stopwatch): the clock of <see cref="Pace"/> and the wardrobe's periodic reads. </summary>
    public double Clock()
        => Stopwatch.GetTimestamp() / (double)Stopwatch.Frequency;

    /// <summary> When the closet may call Penumbra and Glamourer: quiet while loading, one call per frame. Fed by <see cref="Frame"/>. </summary>
    public IpcPace Pace { get; }

    private void OnTerritoryChanged(uint _)
        => Pace.TerritoryChanged();

    /// <summary> Once per frame, first thing in the framework update: the pace learns whether the game loads. </summary>
    public void Frame(double lastFrameMs, double lastOwnMs)
        => Pace.Frame(Loading, lastFrameMs, lastOwnMs);

    /// <summary>
    /// The character is being torn down or built: a loading screen, a cutscene, or no player object. Penumbra and
    /// Glamourer answer slowly then (a GetState took 829 ms while teleporting), so the closet calls neither.
    /// </summary>
    public bool Loading
    {
        get
        {
            if (_objects.LocalPlayer is null)
                return true;
            try
            {
                return _condition[ConditionFlag.BetweenAreas] || _condition[ConditionFlag.BetweenAreas51]
                    || _condition[ConditionFlag.OccupiedInCutSceneEvent] || _condition[ConditionFlag.WatchingCutscene];
            }
            catch
            {
                return false;
            }
        }
    }

    /// <summary> A loading screen is up (the keys are not looked at then). </summary>
    public bool LoadingScreen
    {
        get
        {
            try
            {
                return _condition[ConditionFlag.BetweenAreas] || _condition[ConditionFlag.BetweenAreas51];
            }
            catch
            {
                return false;
            }
        }
    }

    public bool LoggedIn
        => _objects.LocalPlayer is not null;

    public bool GPosing
        => _clientState.IsGPosing;

    public bool InCutscene
    {
        get
        {
            try
            {
                return _condition[ConditionFlag.OccupiedInCutSceneEvent] || _condition[ConditionFlag.WatchingCutscene] || _condition[ConditionFlag.WatchingCutscene78];
            }
            catch
            {
                return false;
            }
        }
    }

    /// <summary> In combat or in a duty (the closet's photos wait then). </summary>
    public bool InCombatOrDuty
    {
        get
        {
            try
            {
                return _condition[ConditionFlag.InCombat] || _condition[ConditionFlag.BoundByDuty] || _condition[ConditionFlag.BoundByDuty56]
                 || _condition[ConditionFlag.BoundByDuty95];
            }
            catch
            {
                return true;
            }
        }
    }

    /// <summary>
    /// True while the game loads the character (see <see cref="Loading"/>) and for <see cref="IpcPace.SettleSeconds"/>
    /// after (and after a territory change). As of the last <see cref="Frame"/>.
    /// </summary>
    public bool Zoning
        => Pace.Quiet;

    /// <summary> "name@world" of the player, "" when nobody is logged in. </summary>
    /// <summary> The player's game object (for reading what the game draws), 0 when there is none. </summary>
    public nint PlayerAddress
        => _objects.LocalPlayer?.Address ?? nint.Zero;

    public string PlayerKey()
        => _objects.LocalPlayer is { } p ? $"{p.Name.TextValue}@{p.HomeWorld.RowId}" : string.Empty;

    /// <summary> The player's race, gender and clan from the game's customize bytes. </summary>
    public PlayerInfo Player()
    {
        if (_objects.LocalPlayer is not { } p)
            return PlayerInfo.Nobody;
        try
        {
            return Races.PlayerFrom(p.Customize, PlayerKey());
        }
        catch
        {
            return PlayerInfo.Nobody with { Key = PlayerKey() };
        }
    }

    /// <summary> Why the character cannot be dressed now (one translated sentence), "" when it can. IPC is checked by the caller. </summary>
    public string WhyNot()
    {
        if (_objects.LocalPlayer is null)
            return Loc.T("Log in with a character first.");
        if (_clientState.IsGPosing)
            return Loc.T("Leave group pose first.");
        if (InCutscene)
            return Loc.T("Wait until the cutscene is over.");
        return string.Empty;
    }

    public void Dispose()
        => _clientState.TerritoryChanged -= OnTerritoryChanged;
}
