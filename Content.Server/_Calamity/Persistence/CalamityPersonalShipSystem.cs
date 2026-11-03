using System.IO;
using System.Linq;
using System.Numerics;
using Content.Server.Popups;
using Content.Server.Shuttles;
using Content.Server.Shuttles.Components;
using Content.Server.Shuttles.Systems;
using Content.Shared._Calamity.CCVar;
using Content.Shared._Calamity.Ships;
using Content.Shared._Mono.FireControl;
using Content.Shared.Containers.ItemSlots;
using Content.Shared.GameTicking;
using Content.Shared.Mind;
using Content.Shared.Mind.Components;
using Content.Shared.Mobs;
using Content.Shared.Mobs.Components;
using Content.Shared.Shuttles.Components;
using Content.Shared.Shuttles.Events;
using Content.Shared.UserInterface;
using Content.Shared.Verbs;
using Robust.Server.GameObjects;
using Robust.Shared.Configuration;
using Robust.Shared.Containers;
using Robust.Shared.EntitySerialization.Systems;
using Robust.Shared.Map;
using Robust.Shared.Map.Components;

namespace Content.Server._Calamity.Persistence;

public sealed partial class CalamityPersonalShipSystem : EntitySystem
{
    private const float TemporarySpawnOffset = 100_000f;

    [Dependency] private IConfigurationManager _configuration = default!;
    [Dependency] private DockingSystem _docking = default!;
    [Dependency] private ItemSlotsSystem _itemSlots = default!;
    [Dependency] private MapLoaderSystem _mapLoader = default!;
    [Dependency] private PopupSystem _popup = default!;
    [Dependency] private ShuttleSystem _shuttles = default!;
    [Dependency] private SharedTransformSystem _transform = default!;
    [Dependency] private UserInterfaceSystem _ui = default!;

    public override void Initialize()
    {
        base.Initialize();

        SubscribeLocalEvent<CalamityPersonalShipTerminalComponent, GetVerbsEvent<AlternativeVerb>>(OnGetTerminalVerbs);
        SubscribeLocalEvent<CalamityShipNavigationConsoleComponent, ActivatableUIOpenAttemptEvent>(OnNavigationConsoleOpenAttempt);
        SubscribeLocalEvent<CalamityShipNavigationConsoleComponent, ComponentShutdown>(OnNavigationConsoleShutdown);
        SubscribeLocalEvent<CalamityPersonalShipTerminalComponent, BoundUIOpenedEvent>(OnPersonalShipConsoleOpened);
        SubscribeLocalEvent<CalamityPersonalShipTerminalComponent, EntInsertedIntoContainerMessage>(OnPersonalShipConsoleItemChanged);
        SubscribeLocalEvent<CalamityPersonalShipTerminalComponent, EntRemovedFromContainerMessage>(OnPersonalShipConsoleItemChanged);
        SubscribeLocalEvent<CalamityShipNavigationConsoleComponent, EntInsertedIntoContainerMessage>(OnNavigationConsoleIdInserted);
        Subs.BuiEvents<CalamityPersonalShipTerminalComponent>(CalamityPersonalShipConsoleUiKey.Key, subs =>
            subs.Event<CalamityPersonalShipConsoleActionMessage>(OnPersonalShipConsoleAction));
    }

    private void OnGetTerminalVerbs(
        EntityUid uid,
        CalamityPersonalShipTerminalComponent component,
        ref GetVerbsEvent<AlternativeVerb> args)
    {
        var actor = args.User;
        if (!_configuration.GetCVar(CalamityCCVars.WorldPersistenceEnabled) ||
            !args.CanAccess ||
            !args.CanInteract ||
            !TryGetInsertedPersonalId(uid, actor, out var mind, out var idUid, out var id))
        {
            return;
        }

        if (id.StoredShipYaml != null)
        {
            args.Verbs.Add(new AlternativeVerb
            {
                Text = Loc.GetString("calamity-personal-ship-retrieve-verb"),
                Priority = 10,
                Act = () => TrySpawnStoredShip(actor, uid, mind, idUid, id),
            });
            return;
        }

        if (TryFindOwnedShip(idUid, out var ownedShip))
        {
            if (!IsDockedAtTerminal(uid, ownedShip))
                return;

            args.Verbs.Add(new AlternativeVerb
            {
                Text = Loc.GetString("calamity-personal-ship-store-verb"),
                Priority = 10,
                Act = () => TryStoreShip(actor, uid, mind, idUid, id, ownedShip),
            });

            args.Verbs.Add(new AlternativeVerb
            {
                Text = Loc.GetString("calamity-personal-ship-unlink-verb"),
                Priority = 9,
                Act = () => TryUnlinkShip(actor, uid, mind, idUid, id, ownedShip),
            });
            return;
        }

        foreach (var (grid, console) in GetLinkableShips(uid))
        {
            var shipName = MetaData(grid).EntityName;
            args.Verbs.Add(new AlternativeVerb
            {
                Text = Loc.GetString("calamity-personal-ship-link-verb", ("ship", shipName)),
                Priority = 1,
                Act = () => TryLinkShip(actor, uid, mind, idUid, id, grid, console),
            });
        }
    }

    private IEnumerable<(EntityUid Grid, EntityUid Console)> GetLinkableShips(EntityUid terminal)
    {
        var query = EntityQueryEnumerator<MapGridComponent, ShuttleComponent, TransformComponent>();
        while (query.MoveNext(out var grid, out _, out _, out _))
        {
            if (HasComp<CalamityPersonalShipComponent>(grid) ||
                !IsDockedAtTerminal(terminal, grid) ||
                !TryGetNavigationConsole(grid, out var console, allowDisabled: true))
            {
                continue;
            }

            yield return (grid, console);
        }
    }

    private IEnumerable<(EntityUid Grid, EntityUid Console)> GetDockedShips(EntityUid terminal)
    {
        var query = EntityQueryEnumerator<MapGridComponent, ShuttleComponent, TransformComponent>();
        while (query.MoveNext(out var grid, out _, out _, out _))
        {
            if (!IsDockedAtTerminal(terminal, grid) ||
                !TryGetNavigationConsole(grid, out var console, allowDisabled: true))
            {
                continue;
            }

            yield return (grid, console);
        }
    }

    private bool TryGetNavigationConsole(EntityUid grid, out EntityUid console, bool allowDisabled)
    {
        EntityUid? fallback = null;
        EntityUid? primary = null;
        EntityUid? ordinaryConsole = null;
        var query = EntityQueryEnumerator<ShuttleConsoleComponent, TransformComponent>();
        while (query.MoveNext(out var uid, out _, out var xform))
        {
            if (xform.GridUid != grid)
                continue;

            if (!TryComp(uid, out CalamityShipNavigationConsoleComponent? shipConsole))
            {
                ordinaryConsole ??= uid;
                continue;
            }

            if (!shipConsole.Disabled && !shipConsole.Primary)
                continue;

            if (shipConsole.Primary)
            {
                primary = uid;
                if (!shipConsole.Disabled)
                    break;
            }
            else if (allowDisabled)
            {
                fallback ??= uid;
            }
        }

        console = primary ?? fallback ?? ordinaryConsole ?? EntityUid.Invalid;
        return console.IsValid();
    }

    private void OnPersonalShipConsoleOpened(
        EntityUid uid,
        CalamityPersonalShipTerminalComponent component,
        BoundUIOpenedEvent args)
    {
        UpdatePersonalShipConsoleState(uid, args.Actor);
    }

    private void OnPersonalShipConsoleAction(
        EntityUid uid,
        CalamityPersonalShipTerminalComponent component,
        CalamityPersonalShipConsoleActionMessage args)
    {
        var actor = args.Actor;
        if (!IsActorNearTerminal(actor, uid) ||
            !TryGetInsertedPersonalId(uid, actor, out var mind, out var idUid, out var id))
        {
            UpdatePersonalShipConsoleState(uid, actor);
            return;
        }

        switch (args.Action)
        {
            case CalamityPersonalShipConsoleAction.Link when args.Ship is { } netShip:
            {
                var target = GetEntity(netShip);
                foreach (var (grid, console) in GetLinkableShips(uid))
                {
                    if (grid != target)
                        continue;

                    TryLinkShip(actor, uid, mind, idUid, id, grid, console);
                    break;
                }

                break;
            }
            case CalamityPersonalShipConsoleAction.Store:
                if (TryFindOwnedShip(idUid, out var ship))
                    TryStoreShip(actor, uid, mind, idUid, id, ship);
                break;
            case CalamityPersonalShipConsoleAction.Retrieve:
                TrySpawnStoredShip(actor, uid, mind, idUid, id);
                break;
            case CalamityPersonalShipConsoleAction.Unlink:
                if (TryFindOwnedShip(idUid, out var linkedShip))
                    TryUnlinkShip(actor, uid, mind, idUid, id, linkedShip);
                break;
        }

        UpdatePersonalShipConsoleState(uid, actor);
    }

    private void OnPersonalShipConsoleItemChanged(
        EntityUid uid,
        CalamityPersonalShipTerminalComponent component,
        EntInsertedIntoContainerMessage args)
    {
        if (args.Container.ID == CalamityPersonalIdComponent.PersonalIdSlotId)
            UpdatePersonalShipConsoleState(uid);
    }

    private void OnPersonalShipConsoleItemChanged(
        EntityUid uid,
        CalamityPersonalShipTerminalComponent component,
        EntRemovedFromContainerMessage args)
    {
        if (args.Container.ID == CalamityPersonalIdComponent.PersonalIdSlotId)
            UpdatePersonalShipConsoleState(uid);
    }

    private void UpdatePersonalShipConsoleState(EntityUid terminal, EntityUid? actor = null)
    {
        if (!TryGetInsertedPersonalId(terminal, actor, out var mind, out var idUid, out var id))
        {
            _ui.SetUiState(terminal, CalamityPersonalShipConsoleUiKey.Key,
                new CalamityPersonalShipConsoleState(
                    Loc.GetString("calamity-personal-ship-console-no-id"),
                    Loc.GetString("calamity-personal-ship-console-no-character"),
                    null, null, false, null, false, false, []));
            return;
        }

        var characterName = TryComp(mind, out MindComponent? mindComponent)
            ? mindComponent.CharacterName ?? Loc.GetString("calamity-personal-ship-console-no-character")
            : Loc.GetString("calamity-personal-ship-console-no-character");
        var idName = MetaData(idUid).EntityName;
        string? linkedShipName = null;
        var canStore = false;
        string? storeUnavailableReason = null;
        var canUnlink = false;

        if (TryFindOwnedShip(idUid, out var ownedShip))
        {
            linkedShipName = MetaData(ownedShip).EntityName;
            var docked = IsDockedAtTerminal(terminal, ownedShip);
            var inFtl = HasComp<FTLComponent>(ownedShip);
            var occupied = HasLivingOccupant(ownedShip);
            var piloted = HasActivePilot(ownedShip);
            canStore = docked && !inFtl && !occupied && !piloted;
            if (!docked)
                storeUnavailableReason = Loc.GetString("calamity-personal-ship-console-store-not-near");
            else if (inFtl)
                storeUnavailableReason = Loc.GetString("calamity-personal-ship-console-store-in-ftl");
            else if (occupied)
                storeUnavailableReason = Loc.GetString("calamity-personal-ship-console-store-occupied");
            else if (piloted)
                storeUnavailableReason = Loc.GetString("calamity-personal-ship-console-store-piloted");
            canUnlink = docked && !HasActivePilot(ownedShip);
        }

        var dockedShips = GetDockedShips(terminal)
                .Select(pair => new CalamityPersonalShipCandidate(
                    GetNetEntity(pair.Grid),
                    MetaData(pair.Grid).EntityName,
                    id.StoredShipYaml == null &&
                    linkedShipName == null &&
                    !HasComp<CalamityPersonalShipComponent>(pair.Grid)))
                .ToArray();

        _ui.SetUiState(terminal, CalamityPersonalShipConsoleUiKey.Key,
            new CalamityPersonalShipConsoleState(
                idName,
                characterName,
                linkedShipName,
                id.StoredShipName,
                canStore,
                storeUnavailableReason,
                !string.IsNullOrWhiteSpace(id.StoredShipYaml),
                canUnlink,
                dockedShips));
    }

    private bool IsActorNearTerminal(EntityUid actor, EntityUid terminal)
    {
        if (!Exists(actor) || !Exists(terminal))
            return false;

        var actorXform = Transform(actor);
        var terminalXform = Transform(terminal);
        return actorXform.MapUid == terminalXform.MapUid &&
               Vector2.DistanceSquared(
                   _transform.GetWorldPosition(actorXform),
                   _transform.GetWorldPosition(terminalXform)) <= 4f;
    }

    private bool TryGetInsertedPersonalId(
        EntityUid terminal,
        EntityUid? body,
        out EntityUid mind,
        out EntityUid idUid,
        out CalamityPersonalIdComponent id)
    {
        mind = EntityUid.Invalid;
        idUid = EntityUid.Invalid;
        id = default!;

        if (!_configuration.GetCVar(CalamityCCVars.WorldPersistenceEnabled) ||
            !TryComp(terminal, out ItemSlotsComponent? slots) ||
            !_itemSlots.TryGetSlot(terminal, CalamityPersonalIdComponent.PersonalIdSlotId, out var idSlot, slots) ||
            idSlot.Item is not { } card ||
            !TryComp(card, out CalamityPersonalIdComponent? candidate) ||
            !TryComp(candidate.OwnerMind, out MindComponent? mindComponent) ||
            !HasComp<CalamityPersistentCharacterComponent>(candidate.OwnerMind))
        {
            return false;
        }

        if (body is { } actorBody &&
            (!TryComp(actorBody, out MindContainerComponent? mindContainer) ||
             mindContainer.Mind != candidate.OwnerMind ||
             mindComponent.OwnedEntity != actorBody))
        {
            return false;
        }

        mind = candidate.OwnerMind;
        idUid = card;
        id = candidate;
        return true;
    }

    private bool TryFindOwnedShip(EntityUid id, out EntityUid grid)
    {
        var query = EntityQueryEnumerator<CalamityPersonalShipComponent>();
        while (query.MoveNext(out var uid, out var ship))
        {
            if (ship.OwnerId != id)
                continue;

            grid = uid;
            return true;
        }

        grid = EntityUid.Invalid;
        return false;
    }

    private bool IsDockedAtTerminal(EntityUid terminal, EntityUid grid)
    {
        var terminalXform = Transform(terminal);
        return terminalXform.GridUid is { } terminalGrid &&
               terminalGrid != grid &&
               _docking.AreGridsDocked(grid, terminalGrid);
    }

    private void TryLinkShip(
        EntityUid actor,
        EntityUid terminal,
        EntityUid mind,
        EntityUid idUid,
        CalamityPersonalIdComponent id,
        EntityUid grid,
        EntityUid console)
    {
        if (!TryGetInsertedPersonalId(terminal, actor, out var currentMind, out var currentIdUid, out var currentId) ||
            currentMind != mind ||
            currentIdUid != idUid ||
            currentId != id ||
            !Exists(terminal) ||
            id.StoredShipYaml != null ||
            !Exists(grid) ||
            HasComp<CalamityPersonalShipComponent>(grid) ||
            !Exists(console) ||
            !TryComp<MapGridComponent>(grid, out _) ||
            !TryComp<ShuttleComponent>(grid, out _) ||
            !IsDockedAtTerminal(terminal, grid) ||
            Transform(console).GridUid != grid ||
            !HasComp<ShuttleConsoleComponent>(console))
        {
            return;
        }

        if (TryFindOwnedShip(idUid, out _))
            return;

        var shipConsole = EnsureComp<CalamityShipNavigationConsoleComponent>(console);
        shipConsole.RootGrid = grid;
        shipConsole.Disabled = false;
        shipConsole.Primary = true;
        EnsureComp<CalamitySectorNavigationComponent>(console);
        EnsureComp<FireControlConsoleComponent>(console);

        var ship = AddComp<CalamityPersonalShipComponent>(grid);
        ship.OwnerId = idUid;
        ship.NavigationConsole = console;
        _popup.PopupEntity(Loc.GetString("calamity-personal-ship-linked"), terminal, actor);
    }

    private void TryStoreShip(
        EntityUid actor,
        EntityUid terminal,
        EntityUid mind,
        EntityUid idUid,
        CalamityPersonalIdComponent id,
        EntityUid grid)
    {
        if (!TryGetInsertedPersonalId(terminal, actor, out var currentMind, out var currentIdUid, out var currentId) ||
            currentMind != mind ||
            currentIdUid != idUid ||
            currentId != id ||
            !Exists(terminal) ||
            id.StoredShipYaml != null ||
            !Exists(grid) ||
            !TryComp(grid, out CalamityPersonalShipComponent? ship) ||
            ship.OwnerId != idUid ||
            !IsDockedAtTerminal(terminal, grid))
        {
            return;
        }

        if (!TryComp(grid, out ShuttleComponent? _) ||
            HasComp<FTLComponent>(grid) ||
            HasLivingOccupant(grid) ||
            HasActivePilot(grid))
        {
            _popup.PopupEntity(Loc.GetString("calamity-personal-ship-cannot-store"), terminal, actor);
            return;
        }

        _docking.UndockDocks(grid);

        var ownerId = ship.OwnerId;
        var console = ship.NavigationConsole;
        if (!TryComp(console, out CalamityShipNavigationConsoleComponent? shipConsole) ||
            !HasComp<ShuttleConsoleComponent>(console))
        {
            _popup.PopupEntity(Loc.GetString("calamity-personal-ship-store-failed"), terminal, actor);
            return;
        }

        shipConsole.RootGrid = null;
        RemComp<CalamityPersonalShipComponent>(grid);

        using var writer = new StringWriter();
        if (!_mapLoader.TrySaveGrid(grid, writer))
        {
            shipConsole.RootGrid = grid;
            var restoredShip = AddComp<CalamityPersonalShipComponent>(grid);
            restoredShip.OwnerId = ownerId;
            restoredShip.NavigationConsole = console;
            _popup.PopupEntity(Loc.GetString("calamity-personal-ship-store-failed"), terminal, actor);
            return;
        }

        var serializedGrid = writer.ToString();
        if (string.IsNullOrWhiteSpace(serializedGrid))
        {
            shipConsole.RootGrid = grid;
            var restoredShip = AddComp<CalamityPersonalShipComponent>(grid);
            restoredShip.OwnerId = ownerId;
            restoredShip.NavigationConsole = console;
            _popup.PopupEntity(Loc.GetString("calamity-personal-ship-store-failed"), terminal, actor);
            return;
        }

        id.StoredShipName = MetaData(grid).EntityName;
        id.StoredShipYaml = serializedGrid;
        Del(grid);
        _popup.PopupEntity(Loc.GetString("calamity-personal-ship-stored"), terminal, actor);
    }

    private bool HasLivingOccupant(EntityUid grid)
    {
        var query = EntityQueryEnumerator<MobStateComponent, TransformComponent>();
        while (query.MoveNext(out _, out var mob, out var xform))
        {
            if (xform.GridUid == grid && mob.CurrentState != MobState.Dead)
                return true;
        }

        return false;
    }

    private bool HasActivePilot(EntityUid grid)
    {
        var query = EntityQueryEnumerator<PilotComponent>();
        while (query.MoveNext(out _, out var pilot))
        {
            if (pilot.Console is { } console &&
                Exists(console) &&
                Transform(console).GridUid == grid)
            {
                return true;
            }
        }

        return false;
    }

    private void TrySpawnStoredShip(
        EntityUid actor,
        EntityUid terminal,
        EntityUid mind,
        EntityUid idUid,
        CalamityPersonalIdComponent id)
    {
        if (!TryGetInsertedPersonalId(terminal, actor, out var currentMind, out var currentIdUid, out var currentId) ||
            currentMind != mind ||
            currentIdUid != idUid ||
            currentId != id ||
            !Exists(terminal) ||
            string.IsNullOrWhiteSpace(id.StoredShipYaml) ||
            TryFindOwnedShip(idUid, out _))
        {
            return;
        }

        if (!TryGetNearestDock(terminal, id.StoredShipYaml, out var loadedMap, out var grid, out var config))
        {
            _popup.PopupEntity(Loc.GetString("calamity-personal-ship-no-dock"), terminal, actor);
            return;
        }

        if (!TryGetNavigationConsole(grid, out var console, allowDisabled: true))
        {
            Del(grid);
            Del(loadedMap);
            _popup.PopupEntity(Loc.GetString("calamity-personal-ship-restore-failed"), terminal, actor);
            return;
        }

        _shuttles.FTLDock((grid, Transform(grid)), config);
        Del(loadedMap);

        var shipConsole = EnsureComp<CalamityShipNavigationConsoleComponent>(console);
        shipConsole.RootGrid = grid;
        shipConsole.Disabled = false;
        shipConsole.Primary = true;

        var ship = AddComp<CalamityPersonalShipComponent>(grid);
        ship.OwnerId = idUid;
        ship.NavigationConsole = console;
        id.StoredShipYaml = null;
        id.StoredShipName = null;
        _popup.PopupEntity(Loc.GetString("calamity-personal-ship-retrieved"), terminal, actor);
    }

    private bool TryGetNearestDock(
        EntityUid terminal,
        string yaml,
        out EntityUid loadedMap,
        out EntityUid grid,
        out DockingConfig config)
    {
        loadedMap = EntityUid.Invalid;
        grid = EntityUid.Invalid;
        config = default!;

        using var reader = new StringReader(yaml);
        if (!_mapLoader.TryLoadGrid(
                reader,
                "stored personal ship",
                out var map,
                out var loadedGrid,
                offset: new Vector2(TemporarySpawnOffset, TemporarySpawnOffset)))
        {
            return false;
        }

        loadedMap = map.Value.Owner;
        grid = loadedGrid.Value.Owner;
        if (!TryComp(grid, out ShuttleComponent? _))
        {
            Del(grid);
            Del(loadedMap);
            loadedMap = EntityUid.Invalid;
            grid = EntityUid.Invalid;
            return false;
        }

        var terminalXform = Transform(terminal);
        if (terminalXform.MapUid is not { } terminalMap)
        {
            Del(grid);
            Del(loadedMap);
            loadedMap = EntityUid.Invalid;
            grid = EntityUid.Invalid;
            return false;
        }

        var terminalPosition = _transform.GetWorldPosition(terminalXform);
        var bestDistance = float.PositiveInfinity;
        DockingConfig? bestConfig = null;
        var shipDocks = _docking.GetDocks(grid);

        var query = EntityQueryEnumerator<MapGridComponent, TransformComponent>();
        while (query.MoveNext(out var candidate, out _, out var candidateXform))
        {
            if (candidate == grid || candidateXform.MapUid != terminalMap)
                continue;

            foreach (var (targetDockUid, targetDock) in _docking.GetDocks(candidate))
            {
                foreach (var (shipDockUid, shipDock) in shipDocks)
                {
                    var candidateConfig = _docking.GetDockingConfig(
                        grid,
                        candidate,
                        shipDockUid,
                        shipDock,
                        targetDockUid,
                        targetDock);
                    if (candidateConfig == null)
                        continue;

                    var destination = _transform.ToMapCoordinates(candidateConfig.Coordinates);
                    var distance = Vector2.DistanceSquared(terminalPosition, destination.Position);
                    if (distance >= bestDistance)
                        continue;

                    bestDistance = distance;
                    bestConfig = candidateConfig;
                }
            }
        }

        if (bestConfig != null)
        {
            config = bestConfig;
            return true;
        }

        Del(grid);
        Del(loadedMap);
        loadedMap = EntityUid.Invalid;
        grid = EntityUid.Invalid;
        return false;
    }

    private void TryUnlinkShip(
        EntityUid actor,
        EntityUid terminal,
        EntityUid mind,
        EntityUid idUid,
        CalamityPersonalIdComponent id,
        EntityUid grid)
    {
        if (!TryGetInsertedPersonalId(terminal, actor, out var currentMind, out var currentIdUid, out var currentId) ||
            currentMind != mind ||
            currentIdUid != idUid ||
            currentId != id ||
            !Exists(terminal) ||
            !Exists(grid) ||
            !TryComp(grid, out CalamityPersonalShipComponent? ship) ||
            ship.OwnerId != idUid ||
            !IsDockedAtTerminal(terminal, grid))
        {
            return;
        }

        if (HasActivePilot(grid))
        {
            _popup.PopupEntity(Loc.GetString("calamity-personal-ship-unlink-blocked"), terminal, actor);
            return;
        }

        if (!TryComp(ship.NavigationConsole, out CalamityShipNavigationConsoleComponent? shipConsole))
            return;

        shipConsole.Disabled = true;
        RemComp<CalamityPersonalShipComponent>(grid);
        _popup.PopupEntity(Loc.GetString("calamity-personal-ship-unlinked"), terminal, actor);
    }

    private void OnNavigationConsoleIdInserted(
        EntityUid uid,
        CalamityShipNavigationConsoleComponent component,
        EntInsertedIntoContainerMessage args)
    {
        if (args.Container.ID != CalamityPersonalIdComponent.PersonalIdSlotId ||
            !TryGetInsertedPersonalId(uid, null, out _, out var idUid, out var id) ||
            !TryComp(id.OwnerMind, out MindComponent? mindComponent) ||
            mindComponent.OwnedEntity is not { } actor ||
            id.StoredShipYaml != null ||
            Transform(uid).GridUid is not { } grid ||
            !TryComp(grid, out MapGridComponent? _) ||
            !TryComp(grid, out ShuttleComponent? _) ||
            !HasComp<ShuttleConsoleComponent>(uid))
        {
            return;
        }

        if (TryComp(grid, out CalamityPersonalShipComponent? existingShip))
        {
            if (existingShip.OwnerId == idUid && existingShip.NavigationConsole == uid)
                return;

            return;
        }

        if (TryFindOwnedShip(idUid, out _))
            return;

        component.RootGrid = grid;
        component.Disabled = false;
        component.Primary = true;
        EnsureComp<CalamitySectorNavigationComponent>(uid);
        EnsureComp<FireControlConsoleComponent>(uid);

        var ship = AddComp<CalamityPersonalShipComponent>(grid);
        ship.OwnerId = idUid;
        ship.NavigationConsole = uid;
        _popup.PopupEntity(Loc.GetString("calamity-personal-ship-linked"), uid, actor);
    }

    private void OnNavigationConsoleOpenAttempt(
        EntityUid uid,
        CalamityShipNavigationConsoleComponent component,
        ActivatableUIOpenAttemptEvent args)
    {
        if (component.Disabled)
            args.Cancel();
    }

    private void OnNavigationConsoleShutdown(
        EntityUid uid,
        CalamityShipNavigationConsoleComponent component,
        ComponentShutdown args)
    {
        if (component.Disabled)
        {
            return;
        }

        var grid = component.RootGrid;
        if (grid == null || !Exists(grid.Value))
        {
            if (!TryComp(uid, out TransformComponent? xform) ||
                xform.GridUid is not { } fallbackGrid)
            {
                return;
            }

            grid = fallbackGrid;
        }

        if (!TryComp(grid.Value, out CalamityPersonalShipComponent? ship) ||
            ship.NavigationConsole != uid)
        {
            return;
        }

        RemComp<CalamityPersonalShipComponent>(grid.Value);
    }
}
