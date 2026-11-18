using System.Numerics;
using Dalamud.Bindings.ImGui;
using Dalamud.Game.ClientState.Conditions;
using Dalamud.Plugin.Services;
using ModCloset2.Core;

namespace ModCloset2.OnYou.Game;

/// <summary>
/// The user's own character on screen, for the "On you" face: its places (head top, hands, tail…) as screen lines with a
/// thickness, every frame. Two halves, so the game's memory is read where the rules say: the bones are read on the
/// framework thread (<see cref="IFramework.Update"/>) into world positions, and <see cref="Project"/> puts them on the
/// screen in Draw, with the camera of the frame being drawn (Sculpt+ projects in Draw the same way). Bones are read only
/// while <see cref="Wanted"/> (the key is held or On you is up).
/// </summary>
public sealed class BodyOnScreen : IBodyOnScreen, IDisposable
{
    /// <summary> How long the last places are kept while the character has no skeleton (every try redraws it). </summary>
    private const double HoldSeconds = 1.5;

    private readonly IFramework _framework;
    private readonly IObjectTable _objects;
    private readonly IClientState _clientState;
    private readonly ICondition _condition;
    private readonly IGameGui _gui;
    private readonly IPluginLog _log;
    private readonly HashSet<string> _logged = new(StringComparer.Ordinal);
    private DateTime _settleUntil = DateTime.MinValue;

    /// <summary> Written on the framework thread, read in Draw: replaced as a whole, never changed in place. </summary>
    private volatile World? _world;
    private volatile bool _wanted;
    private volatile bool _gone;

    private sealed record World(List<WorldSpot> Spots, DateTime At);

    public BodyOnScreen(IFramework framework, IObjectTable objects, IClientState clientState, ICondition condition, IGameGui gui, IPluginLog log)
    {
        _framework   = framework;
        _objects     = objects;
        _clientState = clientState;
        _condition   = condition;
        _gui         = gui;
        _log         = log;
        _framework.Update              += OnUpdate;
        _clientState.TerritoryChanged  += OnTerritoryChanged;
    }

    /// <summary> Set in Draw: read the bones (the On-you key is held, or On you is up). </summary>
    public bool Wanted
    {
        get => _wanted;
        set => _wanted = value;
    }

    /// <summary>
    /// On you must not be up: the character has left the screen for good (logged out, zoning and the seconds after, a
    /// cutscene, group pose) or the player is in combat, in a duty or busy (talking, crafting, gathering…). Checked every
    /// frame, also while On you is not up (it ends "stays on"). Only out of the camera's view is not gone.
    /// </summary>
    public bool Gone
        => _gone;

    public bool Available { get; private set; }
    public IReadOnlyList<BodySpot> Spots { get; private set; } = Array.Empty<BodySpot>();
    public (float Left, float Top, float Right, float Bottom) Box { get; private set; }

    private void OnTerritoryChanged(uint _)
        => _settleUntil = DateTime.UtcNow.AddSeconds(4);

    // ---- framework thread ----

    private void OnUpdate(IFramework _)
    {
        try
        {
            var me = _objects.LocalPlayer;
            _gone = BodyGone.Reason(me is not null, Zoning(), Cutscene(), _clientState.IsGPosing, Any(Combat), Any(Duty), Any(Busy)) is not null;
            if (!_wanted || _gone || me is null)
            {
                _world = null;
                return;
            }

            var bones = Bones.Read(me.Address);
            if (bones.Count == 0)
            {
                // a redraw (every try causes one): no skeleton for a few frames. Keep the last places a moment, so the
                // rings and the chooser do not blink with every step through a shelf.
                if (_world is { } w && (DateTime.UtcNow - w.At).TotalSeconds < HoldSeconds)
                    return;
                _world = null;
                return;
            }

            _world = new World(BodyPlaces.FromBones(bones), DateTime.UtcNow);
        }
        catch (Exception e)
        {
            _world = null;
            _gone  = false;
            if (_logged.Add("read " + e.GetType().Name))
                _log.Error(e, "Mod Closet 2: could not read the character's bones for On you");
        }
    }

    /// <summary> A loading screen, or the seconds after it while the character is still loading. </summary>
    private bool Zoning()
    {
        if (DateTime.UtcNow < _settleUntil)
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

    /// <summary> Fighting: the hotbars' modifier keys must stay the hotbars'. </summary>
    private static readonly ConditionFlag[] Combat = { ConditionFlag.InCombat };

    /// <summary> Inside a duty (dungeon, trial, raid…): no dressing up there. </summary>
    private static readonly ConditionFlag[] Duty = { ConditionFlag.BoundByDuty, ConditionFlag.BoundByDuty56, ConditionFlag.BoundByDuty95 };

    /// <summary> Busy with the game, where pointing at yourself makes no sense: talking, a summoning bell, crafting, gathering, fishing, playing music, down. </summary>
    private static readonly ConditionFlag[] Busy =
    {
        ConditionFlag.Occupied, ConditionFlag.Occupied30, ConditionFlag.Occupied33, ConditionFlag.Occupied38, ConditionFlag.Occupied39,
        ConditionFlag.OccupiedInEvent, ConditionFlag.OccupiedInQuestEvent, ConditionFlag.OccupiedSummoningBell, ConditionFlag.Crafting,
        ConditionFlag.PreparingToCraft, ConditionFlag.ExecutingCraftingAction, ConditionFlag.Gathering, ConditionFlag.ExecutingGatheringAction,
        ConditionFlag.Fishing, ConditionFlag.Performing, ConditionFlag.Unconscious,
    };

    private bool Any(ConditionFlag[] flags)
    {
        try
        {
            foreach (var f in flags)
                if (_condition[f])
                    return true;
            return false;
        }
        catch
        {
            return false;
        }
    }

    private bool Cutscene()
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

    // ---- draw thread ----

    /// <summary> Puts the places on the screen for this frame (call in Draw, before the face). </summary>
    public void Project()
    {
        var world = _world;
        if (world is null)
        {
            Clear();
            return;
        }

        try
        {
            var spots = BodyPlaces.Project(world.Spots, ToScreen, PixelsPerMetre);
            var view  = ImGui.GetMainViewport();
            var min   = view.Pos;
            var max   = view.Pos + view.Size;

            // "the character is not on screen": no place has a point inside the game's view
            var onScreen = spots.Any(s => s.Line.Any(p => p.X >= min.X && p.Y >= min.Y && p.X <= max.X && p.Y <= max.Y));
            Spots     = spots;
            Box       = BodyPlaces.BoxOf(spots);
            Available = onScreen && spots.Count > 0;
        }
        catch (Exception e)
        {
            Clear();
            if (_logged.Add("project " + e.GetType().Name))
                _log.Error(e, "Mod Closet 2: could not put the character's places on the screen for On you");
        }
    }

    private void Clear()
    {
        Available = false;
        Spots     = Array.Empty<BodySpot>();
        Box       = default;
    }

    /// <summary> A world point on the screen; null when it is behind the camera (then it has no place on screen at all). </summary>
    private Vector2? ToScreen(Vector3 world)
        => _gui.WorldToScreen(world, out var screen, out _) ? screen : null;

    /// <summary>
    /// How many pixels a metre covers at a point (from Sculpt+: a perspective camera draws near things larger, so three
    /// small steps along the axes say it without knowing anything about the camera). Of the three steps, two lie across
    /// the view and one along it (nearly nothing on screen), whichever way the camera looks: the length of all three
    /// together is √2 times a metre across the view, hence the division.
    /// </summary>
    private float PixelsPerMetre(Vector3 at)
    {
        const float e = 0.03f;
        if (!_gui.WorldToScreen(at, out var p0, out _) || !_gui.WorldToScreen(at + new Vector3(e, 0, 0), out var px, out _)
         || !_gui.WorldToScreen(at + new Vector3(0, e, 0), out var py, out _) || !_gui.WorldToScreen(at + new Vector3(0, 0, e), out var pz, out _))
            return 0;
        return MathF.Sqrt((Vector2.DistanceSquared(px, p0) + Vector2.DistanceSquared(py, p0) + Vector2.DistanceSquared(pz, p0)) / 2) / e;
    }

    public void Dispose()
    {
        _framework.Update             -= OnUpdate;
        _clientState.TerritoryChanged -= OnTerritoryChanged;
        _world = null;
    }
}
