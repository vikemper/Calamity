using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Numerics;
using Content.Server.Stack;
using Content.Server.Shuttles.Components;
using Content.Server.Shuttles.Systems;
using Content.Shared._Calamity.CCVar;
using Content.Shared._Calamity.Ships;
using Content.Shared._Calamity.Sectors;
using Content.Shared._Mono.Ships;
using Content.Shared.CombatMode.Pacification;
using Content.Shared.Damage;
using Content.Shared.FixedPoint;
using Content.Shared.Interaction.Events;
using Content.Shared.Mind.Components;
using Content.Shared.Shuttles.Components;
using Content.Shared.Stacks;
using Content.Shared.Throwing;
using Content.Shared.Verbs;
using Content.Shared.Weapons.Ranged.Events;
using Robust.Shared.Player;
using Robust.Server.GameObjects;
using Robust.Shared.Configuration;
using Robust.Shared.Map;
using Robust.Shared.Map.Components;
using Robust.Shared.Physics.Components;

namespace Content.Server._Calamity.Persistence;

public sealed class CalamitySectorSystem : EntitySystem
{
    [Dependency] private IConfigurationManager _configuration = default!;
    [Dependency] private MetaDataSystem _metadata = default!;
    [Dependency] private SharedMapSystem _mapSystem = default!;
    [Dependency] private PhysicsSystem _physics = default!;
    [Dependency] private StackSystem _stack = default!;
    [Dependency] private ShuttleSystem _shuttle = default!;
    [Dependency] private TransformSystem _transform = default!;

    private readonly Dictionary<CalamitySectorCoordinates, EntityUid> _sectorMaps = new();
    private readonly Dictionary<EntityUid, PendingSectorTransition> _pendingTransitions = new();
    private static readonly VerbCategory HyperdriveVerbCategory =
        new("calamity-hyperdrive-verb-category", null);

    public override void Initialize()
    {
        base.Initialize();
        SubscribeLocalEvent<CalamitySectorNavigationComponent, GetVerbsEvent<AlternativeVerb>>(OnGetNavigationVerbs);
        SubscribeLocalEvent<DamageableComponent, BeforeDamageChangedEvent>(OnBeforeDamageChanged);
        SubscribeLocalEvent<TransformComponent, AttackAttemptEvent>(OnSafeAttackAttempt);
        SubscribeLocalEvent<TransformComponent, ShotAttemptedEvent>(OnSafeShotAttempt);
        SubscribeLocalEvent<TransformComponent, BeforeThrowEvent>(OnSafeThrowAttempt);
        Subs.BuiEvents<CalamityShipNavigationConsoleComponent>(ShuttleConsoleUiKey.Key, subs =>
            subs.Event<CalamitySectorCrossingMessage>(OnSectorCrossing));
    }

    public override void Update(float frameTime)
    {
        base.Update(frameTime);
        UpdateSafeSectorPacification();
        foreach (var grid in _pendingTransitions.Keys.Where(grid => !Exists(grid) || !HasComp<ShuttleComponent>(grid)).ToArray())
            _pendingTransitions.Remove(grid);

        var sectorSize = _configuration.GetCVar(CalamityCCVars.SectorSize);
        if (!float.IsFinite(sectorSize) || sectorSize <= 0f)
            return;

        var query = EntityQueryEnumerator<ShuttleComponent, MapGridComponent, TransformComponent>();
        while (query.MoveNext(out var uid, out var shuttle, out _, out var xform))
        {
            if (HasComp<FTLComponent>(uid) ||
                !TryGetSectorMap(xform.MapUid, out var sourceSector))
            {
                _pendingTransitions.Remove(uid);
                continue;
            }

            var worldPosition = _transform.GetWorldPosition(xform);
            if (_pendingTransitions.TryGetValue(uid, out var pending))
            {
                if (pending.SourceMap != sourceSector.Map ||
                    Vector2.DistanceSquared(worldPosition, pending.EdgePosition) > 16f)
                {
                    _pendingTransitions.Remove(uid);
                }
                else
                {
                    continue;
                }
            }

            var (destination, destinationPosition) =
                CalamitySectorCoordinates.GetEdgeTransition(sourceSector.Coordinates, worldPosition, sectorSize);
            if (destination == sourceSector.Coordinates)
                continue;

            if (!shuttle.Enabled || !_shuttle.CanFTL(uid, out _))
            {
                _transform.SetCoordinates(
                    uid,
                    xform,
                    new EntityCoordinates(sourceSector.Map, ClampToSector(worldPosition, sectorSize)),
                    rotation: _transform.GetWorldRotation(xform));

                if (TryComp(uid, out PhysicsComponent? physics))
                    _physics.SetLinearVelocity(uid, Vector2.Zero, body: physics);

                continue;
            }

            var edgePosition = ClampToSector(worldPosition, sectorSize);
            _transform.SetCoordinates(
                uid,
                xform,
                new EntityCoordinates(sourceSector.Map, edgePosition),
                rotation: _transform.GetWorldRotation(xform));
            _pendingTransitions[uid] = new PendingSectorTransition(
                sourceSector.Map,
                destination,
                destinationPosition,
                edgePosition);
        }

    }

    public void RegisterPrimaryMap(MapId mapId)
    {
        if (!_mapSystem.TryGetMap(mapId, out var mapUid) || mapUid == null)
            throw new ArgumentException($"Map {mapId} does not exist.", nameof(mapId));

        var sector = EnsureComp<CalamitySectorComponent>(mapUid.Value);
        sector.X = CalamitySectorCoordinates.Hub.X;
        sector.Y = CalamitySectorCoordinates.Hub.Y;
        EnsureComp<CalamitySafeSectorComponent>(mapUid.Value);
        RebuildSectorMapIndex();
    }

    public bool IsSafeSectorMap(EntityUid? mapUid)
    {
        return mapUid is { } uid && HasComp<CalamitySafeSectorComponent>(uid);
    }

    private (EntityUid Map, CalamitySectorCoordinates Coordinates) GetOrCreateSectorMap(
        CalamitySectorCoordinates coordinates)
    {
        if (_sectorMaps.TryGetValue(coordinates, out var existing) && Exists(existing))
            return (existing, Comp<CalamitySectorComponent>(existing).Coordinates);

        var map = _mapSystem.CreateMap(out _);
        var sector = EnsureComp<CalamitySectorComponent>(map);
        sector.X = coordinates.X;
        sector.Y = coordinates.Y;
        if (coordinates == CalamitySectorCoordinates.Hub)
            EnsureComp<CalamitySafeSectorComponent>(map);
        _metadata.SetEntityName(map, $"Sector {coordinates.X},{coordinates.Y}");
        _sectorMaps[coordinates] = map;

        return (map, coordinates);
    }

    private bool TryGetSectorMap(
        EntityUid? mapUid,
        out (EntityUid Map, CalamitySectorCoordinates Coordinates) sector)
    {
        sector = default;
        if (mapUid == null || !TryComp(mapUid.Value, out CalamitySectorComponent? component))
            return false;

        sector = (mapUid.Value, component.Coordinates);
        _sectorMaps[component.Coordinates] = mapUid.Value;
        return true;
    }

    private void RebuildSectorMapIndex()
    {
        _sectorMaps.Clear();
        var query = EntityQueryEnumerator<MapComponent, CalamitySectorComponent>();
        while (query.MoveNext(out var uid, out _, out var sector))
        {
            var coordinates = sector.Coordinates.Wrap();
            if (coordinates != sector.Coordinates)
            {
                sector.X = coordinates.X;
                sector.Y = coordinates.Y;
            }

            if (!_sectorMaps.TryAdd(coordinates, uid))
                throw new InvalidDataException($"More than one map is registered as sector ({coordinates.X},{coordinates.Y}).");

            if (coordinates == CalamitySectorCoordinates.Hub)
                EnsureComp<CalamitySafeSectorComponent>(uid);
        }
    }

    private void OnGetNavigationVerbs(
        EntityUid uid,
        CalamitySectorNavigationComponent component,
        ref GetVerbsEvent<AlternativeVerb> args)
    {
        if (!args.CanAccess || !args.CanInteract ||
            (TryComp(uid, out CalamityShipNavigationConsoleComponent? shipConsole) && shipConsole.Disabled) ||
            Transform(uid).GridUid is not { } grid ||
            !TryGetSectorMap(Transform(uid).MapUid, out var sourceSector))
        {
            return;
        }

        if (_pendingTransitions.TryGetValue(grid, out var pending))
        {
            args.Verbs.Add(new AlternativeVerb
            {
                Text = Loc.GetString(
                    "calamity-sector-cross-verb",
                    ("x", pending.Destination.X),
                    ("y", pending.Destination.Y)),
                Priority = 10,
                Act = () => ConfirmSectorTransition(grid, pending),
            });
        }

        if (!HasComp<FTLDriveComponent>(grid) ||
            HasComp<FTLComponent>(grid) ||
            !TryComp(grid, out ShuttleComponent? shuttle) ||
            !shuttle.Enabled)
        {
            return;
        }

        for (var x = 0; x < CalamitySectorCoordinates.GridSize; x++)
        {
            for (var y = 0; y < CalamitySectorCoordinates.GridSize; y++)
            {
                var destination = new CalamitySectorCoordinates(x, y);
                var distance = CalamitySectorCoordinates.GetDistance(sourceSector.Coordinates, destination);
                if (distance <= 1)
                    continue;

                var fuelCost = distance;
                args.Verbs.Add(new AlternativeVerb
                {
                    Text = Loc.GetString(
                        "calamity-sector-hyperdrive-verb",
                        ("x", x),
                        ("y", y),
                        ("fuel", fuelCost)),
                    Category = HyperdriveVerbCategory,
                    Priority = 1,
                    Act = () => ConfirmHyperdriveJump(grid, sourceSector, destination, fuelCost),
                });
            }
        }
    }

    private void OnSectorCrossing(
        EntityUid uid,
        CalamityShipNavigationConsoleComponent component,
        CalamitySectorCrossingMessage args)
    {
            if (component.Disabled ||
                Transform(uid).GridUid is not { } grid ||
                !TryComp(uid, out ShuttleConsoleComponent? console) ||
                !console.SubscribedPilots.Contains(args.Actor) ||
                !TryComp(grid, out ShuttleComponent? shuttle) ||
                !shuttle.Enabled ||
                HasComp<FTLComponent>(grid) ||
                !_shuttle.CanFTL(grid, out _) ||
                !TryGetSectorMap(Transform(grid).MapUid, out var source))
            {
                return;
            }

        var (deltaX, deltaY) = args.Direction switch
        {
            CalamitySectorDirection.North => (0, 1),
            CalamitySectorDirection.East => (1, 0),
            CalamitySectorDirection.South => (0, -1),
            CalamitySectorDirection.West => (-1, 0),
            _ => (0, 0),
        };
        if (deltaX == 0 && deltaY == 0)
            return;

        var destination = CalamitySectorCoordinates.Wrap(
            source.Coordinates.X + deltaX,
            source.Coordinates.Y + deltaY);
        var sectorSize = _configuration.GetCVar(CalamityCCVars.SectorSize);
        if (!float.IsFinite(sectorSize) || sectorSize <= 0f)
            return;

        var edge = sectorSize / 2f - 16f;
        var current = _transform.GetWorldPosition(Transform(grid));
        var position = args.Direction switch
        {
            CalamitySectorDirection.North => new Vector2(Math.Clamp(current.X, -edge, edge), -edge),
            CalamitySectorDirection.East => new Vector2(-edge, Math.Clamp(current.Y, -edge, edge)),
            CalamitySectorDirection.South => new Vector2(Math.Clamp(current.X, -edge, edge), edge),
            CalamitySectorDirection.West => new Vector2(edge, Math.Clamp(current.Y, -edge, edge)),
            _ => Vector2.Zero,
        };

        var destinationMap = GetOrCreateSectorMap(destination).Map;
        _pendingTransitions.Remove(grid);
        _shuttle.FTLToCoordinates(
            grid,
            shuttle,
            new EntityCoordinates(destinationMap, position),
            _transform.GetWorldRotation(Transform(grid)),
            startupTime: 0.1f,
            hyperspaceTime: 0.5f);
    }

    private void ConfirmSectorTransition(EntityUid grid, PendingSectorTransition pending)
    {
        if (!_pendingTransitions.TryGetValue(grid, out var current) ||
            current != pending ||
            !Exists(grid) ||
            !TryComp(grid, out ShuttleComponent? shuttle) ||
            !shuttle.Enabled ||
            Transform(grid).MapUid != pending.SourceMap ||
            !_shuttle.CanFTL(grid, out _))
        {
            _pendingTransitions.Remove(grid);
            return;
        }

        var destinationMap = GetOrCreateSectorMap(pending.Destination).Map;
        _pendingTransitions.Remove(grid);
        _shuttle.FTLToCoordinates(
            grid,
            shuttle,
            new EntityCoordinates(destinationMap, pending.DestinationPosition),
            _transform.GetWorldRotation(Transform(grid)),
            startupTime: 0.1f,
            hyperspaceTime: 0.5f);
    }

    private void ConfirmHyperdriveJump(
        EntityUid grid,
        (EntityUid Map, CalamitySectorCoordinates Coordinates) sourceSector,
        CalamitySectorCoordinates destination,
        int fuelCost)
    {
        if (!Exists(grid) ||
            HasComp<FTLComponent>(grid) ||
            !HasComp<FTLDriveComponent>(grid) ||
            !TryComp(grid, out ShuttleComponent? shuttle) ||
            !shuttle.Enabled ||
            !TryGetSectorMap(Transform(grid).MapUid, out var currentSector) ||
            currentSector != sourceSector ||
            CalamitySectorCoordinates.GetDistance(sourceSector.Coordinates, destination) <= 1 ||
            !_shuttle.CanFTL(grid, out _))
        {
            return;
        }

        var destinationMap = GetOrCreateSectorMap(destination).Map;
        if (!TryConsumeBluespaceFuel(grid, fuelCost))
            return;

        _shuttle.FTLToCoordinates(
            grid,
            shuttle,
            new EntityCoordinates(destinationMap, Vector2.Zero),
            _transform.GetWorldRotation(Transform(grid)),
            startupTime: 0.1f,
            hyperspaceTime: 0.5f);
    }

    private bool TryConsumeBluespaceFuel(EntityUid grid, int amount)
    {
        var stacks = new List<(EntityUid Uid, StackComponent Component)>();
        var available = 0;
        var query = EntityQueryEnumerator<StackComponent, TransformComponent>();
        while (query.MoveNext(out var uid, out var stack, out var xform))
        {
            if (stack.StackTypeId != "Bluespace" || xform.GridUid != grid)
                continue;

            stacks.Add((uid, stack));
            available += stack.Count;
        }

        if (available < amount)
            return false;

        var remaining = amount;
        foreach (var (uid, stack) in stacks)
        {
            var used = Math.Min(remaining, stack.Count);
            if (used <= 0)
                continue;

            if (!_stack.Use(uid, used, stack))
                return false;

            remaining -= used;
            if (remaining == 0)
                break;
        }

        return remaining == 0;
    }

    private void OnBeforeDamageChanged(
        EntityUid uid,
        DamageableComponent component,
        ref BeforeDamageChangedEvent args)
    {
        if (args.Origin == null ||
            !args.Damage.DamageDict.Values.Any(value => value > FixedPoint2.Zero) ||
            !TryComp(uid, out TransformComponent? xform) ||
            xform.MapUid is not { } mapUid ||
            !IsSafeSectorMap(mapUid))
        {
            return;
        }

        args.Cancelled = true;
    }

    private void OnSafeAttackAttempt(
        EntityUid uid,
        TransformComponent component,
        ref AttackAttemptEvent args)
    {
        if (IsSafeSectorMap(component.MapUid))
            args.Cancel();
    }

    private void OnSafeShotAttempt(
        EntityUid uid,
        TransformComponent component,
        ref ShotAttemptedEvent args)
    {
        if (IsSafeSectorMap(component.MapUid))
            args.Cancel();
    }

    private void OnSafeThrowAttempt(
        EntityUid uid,
        TransformComponent component,
        ref BeforeThrowEvent args)
    {
        if (IsSafeSectorMap(component.MapUid))
            args.Cancelled = true;
    }

    private void UpdateSafeSectorPacification()
    {
        var query = EntityQueryEnumerator<ActorComponent, TransformComponent>();
        while (query.MoveNext(out var uid, out _, out var xform))
        {
            var inSafeSector = xform.MapUid is { } mapUid && IsSafeSectorMap(mapUid);
            var hasMarker = TryComp(uid, out CalamitySafeSectorPacificationComponent? marker);

            if (inSafeSector && !hasMarker)
            {
                var hadPacified = HasComp<PacifiedComponent>(uid);
                marker = AddComp<CalamitySafeSectorPacificationComponent>(uid);
                marker.AddedPacifiedComponent = !hadPacified;
                if (!hadPacified)
                    EnsureComp<PacifiedComponent>(uid);
            }
            else if (!inSafeSector && hasMarker)
            {
                if (marker!.AddedPacifiedComponent)
                    RemComp<PacifiedComponent>(uid);

                RemComp<CalamitySafeSectorPacificationComponent>(uid);
            }
        }

        var staleQuery = EntityQueryEnumerator<CalamitySafeSectorPacificationComponent>();
        while (staleQuery.MoveNext(out var uid, out var marker))
        {
            if (HasComp<ActorComponent>(uid))
                continue;

            if (marker.AddedPacifiedComponent)
                RemComp<PacifiedComponent>(uid);

            RemComp<CalamitySafeSectorPacificationComponent>(uid);
        }
    }

    private static Vector2 ClampToSector(Vector2 position, float sectorSize)
    {
        var edge = sectorSize / 2f - 1f;
        return new Vector2(Math.Clamp(position.X, -edge, edge), Math.Clamp(position.Y, -edge, edge));
    }

    private readonly record struct PendingSectorTransition(
        EntityUid SourceMap,
        CalamitySectorCoordinates Destination,
        Vector2 DestinationPosition,
        Vector2 EdgePosition);
}
