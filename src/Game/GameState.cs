using Dalamud.Game.ClientState.Conditions;
using Dalamud.Plugin.Services;

namespace ModCloset2.Game;

/// <summary> Who the player is and whether the character can be dressed right now. Framework thread only. </summary>
public sealed class GameState : IDisposable
{
    private readonly IObjectTable _objects;
    private readonly IClientState _clientState;
    private readonly ICondition _condition;
    private DateTime _settleUntil = DateTime.MinValue;

    public GameState(IObjectTable objects, IClientState clientState, ICondition condition)
    {
        _objects     = objects;
        _clientState = clientState;
        _condition   = condition;
        _clientState.TerritoryChanged += OnTerritoryChanged;
    }

    private void OnTerritoryChanged(uint _)
        => _settleUntil = DateTime.UtcNow.AddSeconds(4);

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
    /// True while changing area (loading screen) and for a few seconds after, while the character loads again. Asking
    /// Penumbra and Glamourer about a half-loaded character is slow and froze the game when teleporting (old plugin).
    /// </summary>
    public bool Zoning
    {
        get
        {
            if (_objects.LocalPlayer is null || DateTime.UtcNow < _settleUntil)
                return true;
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
