using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using Content.Server.Atmos.Rotting;
using Content.Server.Chat.Managers;
using Content.Server.Database;
using Content.Server.GameTicking;
using Content.Shared._Calamity.CCVar;
using Content.Shared.Chat;
using Content.Shared.GameTicking;
using Content.Shared.Mobs;
using Content.Shared.Mobs.Components;
using Content.Shared.Mobs.Systems;
using Content.Shared.Mind;
using Content.Shared.Mind.Components;
using Content.Shared.Maps;
using Robust.Server;
using Robust.Server.Upload;
using Robust.Shared.Configuration;
using Robust.Shared.ContentPack;
using Robust.Shared.EntitySerialization;
using Robust.Shared.EntitySerialization.Systems;
using Robust.Shared.GameObjects;
using Robust.Shared.Map.Components;
using Robust.Shared.Map;
using Robust.Shared.Network;
using Robust.Shared.Prototypes;
using Robust.Shared.Spawners;
using Robust.Shared.Timing;
using Robust.Shared.Utility;

namespace Content.Server._Calamity.Persistence;

/// <summary>
/// Saves server entity state using committed, versioned snapshots. On restore, only the registered
/// primary map is retained; secondary maps from older global snapshots are discarded.
/// </summary>
public sealed class CalamityWorldPersistenceSystem : EntitySystem
{
    public const string MainGameMapPrototypeId = "Frontier";

    private const string SnapshotDirectoryName = "Calamity/Persistence";
    private const string SnapshotAssetDirectorySuffix = "-assets";
    private const string SnapshotAssetManifestName = "complete";
    private const string SnapshotResourceDirectoryName = "resources";
    private const string SnapshotPrototypeDirectoryName = "prototypes";
    private const string SnapshotDatabaseStateName = "database-state.json";
    private const string PreviousSnapshotAssetManifestVersion = "runtime-content-v1";
    private const string SnapshotAssetManifestVersion = "runtime-content-v2";
    private const string RestoreSelectionFileName = "restore-next";
    private const string MainMapKey = "main";
    private const int RetainedSnapshots = 2;
    private static readonly TimeSpan PlayerCorpseReviveWindow = TimeSpan.FromMinutes(5);

    [Dependency] private IBaseServer _baseServer = default!;
    [Dependency] private IConfigurationManager _configuration = default!;
    [Dependency] private IChatManager _chatManager = default!;
    [Dependency] private SharedMindSystem _mindSystem = default!;
    [Dependency] private MobStateSystem _mobState = default!;
    [Dependency] private RottingSystem _rotting = default!;
    [Dependency] private SharedMapSystem _mapSystem = default!;
    [Dependency] private IResourceManager _resources = default!;
    [Dependency] private IGameTiming _timing = default!;
    [Dependency] private MapLoaderSystem _mapLoader = default!;
    [Dependency] private MetaDataSystem _metadata = default!;
    [Dependency] private IPrototypeManager _prototypeManager = default!;
    [Dependency] private NetworkResourceManager _networkResourceManager = default!;
    [Dependency] private GamePrototypeLoadManager _prototypeLoadManager = default!;
    [Dependency] private IServerDbManager _database = default!;
    [Dependency] private CalamitySectorSystem _sectorSystem = default!;

    private MapId? _worldMap;
    private TimeSpan _nextSaveAt;
    private TimeSpan? _queuedSaveAt;
    private string? _queuedSaveReason;
    private bool _saveInProgress;
    private bool _shutdownSaveAttempted;
    private bool _worldHasEnteredRound;
    private bool _runtimeContentRestored;
    private int? _runtimeContentSnapshotEpoch;

    /// <summary>
    /// True after this server boot restored a previously saved world map.
    /// Initial world generation can use this to avoid duplicating saved content.
    /// </summary>
    public bool RestoredSavedWorldMapThisBoot { get; private set; }

    /// <summary>
    /// True when the restored snapshot included the server's game rules and auxiliary entities.
    /// </summary>
    public bool RestoredFullWorldSnapshotThisBoot { get; private set; }

    public MapId? WorldMapId => _worldMap;

    public override void Initialize()
    {
        base.Initialize();
        _baseServer.ShutdownRequested += OnShutdownRequested;
        SubscribeLocalEvent<GameRunLevelChangedEvent>(OnGameRunLevelChanged);
        SubscribeLocalEvent<PlayerSpawnCompleteEvent>(OnPlayerSpawnComplete);
        SubscribeLocalEvent<MindContainerComponent, MindAddedMessage>(OnMindAddedToBody);
        SubscribeLocalEvent<MobStateChangedEvent>(OnMobStateChanged);
    }

    public override void Update(float frameTime)
    {
        base.Update(frameTime);

        if (_queuedSaveReason != null && _queuedSaveAt <= _timing.RealTime)
        {
            var reason = _queuedSaveReason;
            _queuedSaveReason = null;
            _queuedSaveAt = null;
            PerformSnapshot(reason);
            return;
        }

        if (_configuration.GetCVar(CalamityCCVars.WorldPersistenceEnabled) &&
            _worldHasEnteredRound &&
            _worldMap != null &&
            !_saveInProgress &&
            _timing.RealTime >= _nextSaveAt)
        {
            QueueSnapshot("autosave");
        }
    }

    private bool HasDatabaseRollbackState(IWritableDirProvider directory, int epoch)
    {
        var assetPath = GetSnapshotAssetDirectoryPath(epoch);
        if (!directory.Exists(assetPath))
            return false;

        var assetDirectory = directory.OpenSubdirectory(assetPath);
        var manifestPath = new ResPath(SnapshotAssetManifestName).ToRootedPath();
        var databaseStatePath = new ResPath(SnapshotDatabaseStateName).ToRootedPath();
        return assetDirectory.Exists(manifestPath) &&
               ReadText(assetDirectory, manifestPath).Trim() == SnapshotAssetManifestVersion &&
               assetDirectory.Exists(databaseStatePath);
    }

    /// <summary>
    /// Loads runtime-uploaded resources and prototypes from the newest usable committed snapshot.
    /// EntryPoint calls this before accepting player connections so clients receive the restored content
    /// through the normal upload handshake.
    /// </summary>
    public void RestoreLatestRuntimeContent()
    {
        if (_runtimeContentRestored)
            return;

        _runtimeContentRestored = true;
        if (!_configuration.GetCVar(CalamityCCVars.WorldPersistenceEnabled))
            return;

        var directory = GetSnapshotDirectory(create: false);
        if (directory == null)
            return;

        var requestedRestoreEpoch = GetPendingRestoreEpoch(directory);
        var committed = GetCommittedSnapshots(directory);
        var markers = requestedRestoreEpoch is { } requestedEpoch
            ? committed.Where(marker => GetEpoch(marker) == requestedEpoch).ToArray()
            : committed;

        if (requestedRestoreEpoch != null && markers.Length == 0)
            throw new InvalidDataException($"Requested world snapshot {requestedRestoreEpoch} is no longer available.");

        foreach (var marker in markers)
        {
            try
            {
                if (!TryGetSnapshotName(directory, marker, out var snapshotName))
                {
                    Log.Error($"Ignoring invalid persistence marker {marker} while restoring uploaded content.");
                    continue;
                }

                var epoch = GetEpoch(marker);
                var assetPath = GetSnapshotAssetDirectoryPath(epoch);
                if (!directory.Exists(assetPath))
                {
                    if (requestedRestoreEpoch != null)
                        throw new InvalidDataException($"Requested world snapshot {requestedRestoreEpoch} has no runtime or database rollback assets.");

                    // Snapshots written before runtime-content support have no asset directory.
                    _runtimeContentSnapshotEpoch = epoch;
                    return;
                }

                RestoreRuntimeContent(directory, epoch, requestedRestoreEpoch != null);
                _runtimeContentSnapshotEpoch = epoch;
                Log.Info($"Restored runtime-uploaded content from world snapshot {snapshotName}.");
                return;
            }
            catch (Exception exception)
            {
                if (requestedRestoreEpoch != null)
                    throw new InvalidDataException($"Could not restore runtime content for requested world snapshot {requestedRestoreEpoch}.", exception);

                Log.Error($"Failed to restore runtime-uploaded content from snapshot marker {marker}: {exception}; trying the previous committed snapshot.");
            }
        }

        if (requestedRestoreEpoch != null)
            throw new InvalidDataException($"Could not restore runtime content for requested world snapshot {requestedRestoreEpoch}.");
    }

    /// <summary>
    /// Called by GameTicker for the selected main map after normal loading, or when this system restores it.
    /// </summary>
    public void SetWorldMap(MapId mapId, GameMapPrototype? prototype = null)
    {
        _worldMap = mapId;
        RegisterPersistentMap(MainMapKey, mapId, prototype?.ID);
        _sectorSystem.RegisterPrimaryMap(mapId);
        _nextSaveAt = _timing.RealTime + GetSaveInterval();
    }

    /// <summary>
    /// Registers a world map under an identifier that remains stable across server restarts.
    /// Sector systems can use coordinates in place of runtime MapIds.
    /// </summary>
    public void RegisterPersistentMap(string key, MapId mapId, string? gameMapPrototype = null)
    {
        if (string.IsNullOrWhiteSpace(key) || key.Any(character =>
                !char.IsAsciiLetterOrDigit(character) && character is not '-' and not '_'))
        {
            throw new ArgumentException("Persistent map keys may contain only ASCII letters, numbers, '-' and '_'.", nameof(key));
        }

        if (!_mapSystem.TryGetMap(mapId, out var mapUid) || mapUid == null)
            throw new ArgumentException($"Map {mapId} does not exist.", nameof(mapId));

        var persistentMap = EnsureComp<CalamityPersistentMapComponent>(mapUid.Value);
        persistentMap.Key = key;
        if (gameMapPrototype != null)
            persistentMap.GameMapPrototype = gameMapPrototype;
    }

    /// <summary>
    /// Queues a manual world snapshot and announces it before the synchronous save begins.
    /// </summary>
    public bool RequestManualSave()
    {
        if (!_configuration.GetCVar(CalamityCCVars.WorldPersistenceEnabled) ||
            !_worldHasEnteredRound ||
            _worldMap == null ||
            _saveInProgress ||
            _queuedSaveReason != null)
        {
            return false;
        }

        QueueSnapshot("manual");
        return true;
    }

    public IReadOnlyList<int> GetAvailableSnapshots()
    {
        var directory = GetSnapshotDirectory(create: false);
        if (directory == null)
            return Array.Empty<int>();

        return GetCommittedSnapshots(directory)
            .Where(marker => TryGetSnapshotName(directory, marker, out _) &&
                             HasDatabaseRollbackState(directory, GetEpoch(marker)))
            .Select(GetEpoch)
            .ToArray();
    }

    /// <summary>
    /// Selects a committed snapshot to restore on the next server boot.
    /// </summary>
    public bool RequestRestore(int epoch)
    {
        if (!_configuration.GetCVar(CalamityCCVars.WorldPersistenceEnabled) ||
            _saveInProgress ||
            _queuedSaveReason != null ||
            epoch <= 0)
            return false;

        var directory = GetSnapshotDirectory(create: false);
        if (directory == null)
            return false;

        var marker = GetCommittedSnapshots(directory)
            .FirstOrDefault(candidate => GetEpoch(candidate) == epoch);
        if (marker == default || !TryGetSnapshotName(directory, marker, out var snapshotName))
            return false;

        var snapshotPath = new ResPath($"{SnapshotDirectoryName}/{snapshotName}");
        try
        {
            if (!HasDatabaseRollbackState(directory, epoch) || !_mapLoader.TryReadFile(snapshotPath, out _))
                return false;
        }
        catch (Exception exception)
        {
            Log.Error($"Could not validate world snapshot {snapshotPath} for restore: {exception}");
            return false;
        }

        WriteText(
            directory,
            new ResPath($"{RestoreSelectionFileName}.tmp"),
            epoch.ToString(CultureInfo.InvariantCulture));
        var selection = new ResPath(RestoreSelectionFileName).ToRootedPath();
        directory.Delete(selection);
        directory.Rename(new ResPath($"{RestoreSelectionFileName}.tmp").ToRootedPath(), selection);
        return true;
    }

    public bool CancelRestoreRequest()
    {
        var directory = GetSnapshotDirectory(create: false);
        if (directory == null)
            return false;

        var selection = new ResPath(RestoreSelectionFileName).ToRootedPath();
        if (!directory.Exists(selection))
            return false;

        directory.Delete(selection);
        return true;
    }

    /// <summary>
    /// Loads the newest valid committed snapshot and discards every map except its registered primary map.
    /// Returns false when there is no usable snapshot.
    /// </summary>
    public bool TryLoadSavedWorldMap(
        GameMapPrototype prototype,
        out MapId mapId,
        out IReadOnlyList<EntityUid> grids)
    {
        mapId = default;
        grids = Array.Empty<EntityUid>();
        RestoredSavedWorldMapThisBoot = false;
        RestoredFullWorldSnapshotThisBoot = false;

        if (!_configuration.GetCVar(CalamityCCVars.WorldPersistenceEnabled))
            return false;

        var directory = GetSnapshotDirectory(create: false);
        if (directory == null)
            return false;

        var committed = GetCommittedSnapshots(directory);
        var requestedRestoreEpoch = GetPendingRestoreEpoch(directory);
        var snapshots = requestedRestoreEpoch is { } requestedEpoch
            ? committed.Where(marker => GetEpoch(marker) == requestedEpoch).ToArray()
            : committed;

        if (requestedRestoreEpoch != null && snapshots.Length == 0)
            throw new InvalidDataException($"Requested world snapshot {requestedRestoreEpoch} is no longer available.");

        foreach (var marker in snapshots)
        {
            if (_runtimeContentSnapshotEpoch is { } assetEpoch && GetEpoch(marker) > assetEpoch)
                continue;

            var snapshotName = string.Empty;
            var snapshotPath = default(ResPath);
            var isFullWorldSnapshot = false;
            MapId restoredMapId;
            IReadOnlyList<EntityUid> restoredGrids;
            LoadResult? loadedState = null;

            try
            {
                if (!TryGetSnapshotName(directory, marker, out snapshotName))
                {
                    Log.Error($"Ignoring invalid persistence marker {marker}.");
                    continue;
                }

                snapshotPath = new ResPath($"{SnapshotDirectoryName}/{snapshotName}");
                isFullWorldSnapshot = snapshotName.EndsWith("-universe.yml", StringComparison.Ordinal);
                var loadOptions = MapLoadOptions.Default;
                loadOptions.ExpectedCategory = isFullWorldSnapshot ? FileCategory.Save : FileCategory.Map;
                if (!_mapLoader.TryLoadGeneric(snapshotPath, out loadedState, loadOptions) || loadedState == null)
                {
                    Log.Error($"Could not load world snapshot {snapshotPath}; trying the previous committed snapshot.");
                    continue;
                }

                Entity<MapComponent>? mainMap = null;
                foreach (var candidate in loadedState.Maps)
                {
                    if (isFullWorldSnapshot)
                    {
                        if (!TryComp(candidate.Owner, out CalamityPersistentMapComponent? persistentMap) ||
                            persistentMap.Key != MainMapKey)
                        {
                            continue;
                        }

                    }
                    else if (loadedState.Maps.Count != 1)
                    {
                        continue;
                    }

                    mainMap = candidate;
                    break;
                }

                if (mainMap == null)
                {
                    _mapLoader.Delete(loadedState);
                    loadedState = null;
                    Log.Error(isFullWorldSnapshot
                        ? $"World snapshot {snapshotPath} did not contain a map registered as '{MainMapKey}'; trying the previous committed snapshot."
                        : $"Legacy map snapshot {snapshotPath} did not contain exactly one map; trying the previous committed snapshot.");
                    continue;
                }

                if (!TryComp(mainMap.Value.Owner, out CalamityPersistentMapComponent? persistentMainMap) ||
                    persistentMainMap.Key != MainMapKey ||
                    persistentMainMap.GameMapPrototype != prototype.ID)
                {
                    _mapLoader.Delete(loadedState);
                    loadedState = null;
                    Log.Info($"Ignoring world snapshot {snapshotPath} because its registered main map is not Colossus ({prototype.ID}).");
                    continue;
                }

                restoredMapId = mainMap.Value.Comp.MapId;
                restoredGrids = isFullWorldSnapshot
                    ? loadedState.Grids
                        .Where(grid => Transform(grid.Owner).MapID == restoredMapId)
                        .Select(grid => grid.Owner)
                        .ToArray()
                    : loadedState.Grids.Select(grid => grid.Owner).ToArray();

                if (isFullWorldSnapshot)
                {
                    foreach (var candidate in loadedState.Maps)
                    {
                        if (candidate.Owner != mainMap.Value.Owner)
                            _mapSystem.DeleteMap(candidate.Comp.MapId);
                    }
                }
            }
            catch (Exception exception)
            {
                if (loadedState != null)
                    _mapLoader.Delete(loadedState);

                Log.Error($"Failed to load snapshot marker {marker}: {exception}; trying the previous committed snapshot.");
                continue;
            }

            if (requestedRestoreEpoch != null && GetEpoch(marker) != requestedRestoreEpoch)
                throw new InvalidDataException($"Requested world snapshot {requestedRestoreEpoch} could not be loaded.");

            mapId = restoredMapId;
            grids = restoredGrids;
            RestoredSavedWorldMapThisBoot = true;
            RestoredFullWorldSnapshotThisBoot = isFullWorldSnapshot;

            try
            {
                SetWorldMap(mapId, prototype);
                if (_mapSystem.TryGetMap(mapId, out var restoredMapEntity) && restoredMapEntity != null)
                    _metadata.SetEntityName(restoredMapEntity.Value, prototype.MapName);

                RestoreLoadedPlayerMindData();
                RaiseLocalEvent(new PostGameMapLoad(prototype, mapId, grids, null, restored: true));
                Log.Info($"Restored world map from snapshot {snapshotPath}.");
            }
            catch (Exception exception)
            {
                // The snapshot entities are already loaded. Do not fall back and load another copy of the world.
                Log.Error($"World snapshot {snapshotPath} loaded, but post-load initialization failed: {exception}");
            }

            if (requestedRestoreEpoch != null)
                directory.Delete(new ResPath(RestoreSelectionFileName).ToRootedPath());

            return true;
        }

        if (requestedRestoreEpoch != null)
            throw new InvalidDataException($"Requested world snapshot {requestedRestoreEpoch} does not contain a usable main map.");

        return false;
    }

    private void RestoreLoadedPlayerMindData()
    {
        var minds = EntityQueryEnumerator<MindComponent>();
        while (minds.MoveNext(out var uid, out var mind))
        {
            _mindSystem.SynchronizePlayerMindData(uid, mind);

            if (mind.UserId == null)
                continue;

            var body = mind.LastMob;
            if (body == null || TerminatingOrDeleted(body.Value) || !HasComp<MobStateComponent>(body.Value))
                body = mind.OwnedEntity;

            if (body == null || TerminatingOrDeleted(body.Value) || !TryComp(body.Value, out MobStateComponent? mobState))
                continue;

            MarkPlayerCharacterBody(body.Value);
            if (mobState.CurrentState == MobState.Dead)
                StartPlayerCorpseTimer(body.Value);
        }
    }

    private void OnPlayerSpawnComplete(PlayerSpawnCompleteEvent ev)
    {
        MarkPlayerCharacterBody(ev.Mob);
    }

    public void AbandonCharacter(EntityUid mindId, MindComponent mind)
    {
        var accountId = mind.UserId;
        var body = mind.LastMob;
        if (body == null || TerminatingOrDeleted(body.Value) || !HasComp<MobStateComponent>(body.Value))
            body = mind.OwnedEntity;

        // Clear the account link first so the old body is not possessed when it is marked dead.
        _mindSystem.WipeMind(mindId, mind);
        RemComp<CalamityPersistentCharacterComponent>(mindId);

        if (body == null || TerminatingOrDeleted(body.Value) || !TryComp(body.Value, out MobStateComponent? mobState))
            return;

        MarkPlayerCharacterBody(body.Value);
        _mobState.ChangeMobState(body.Value, MobState.Dead, mobState);
        StartPlayerCorpseTimer(body.Value);
        Log.Info($"Abandoned persistent character for account {accountId}; corpse {ToPrettyString(body.Value)} will despawn after its player corpse timer.");
    }

    private void OnMindAddedToBody(EntityUid uid, MindContainerComponent component, MindAddedMessage ev)
    {
        if (ev.Mind.Comp.UserId != null)
            MarkPlayerCharacterBody(uid);
    }

    private void MarkPlayerCharacterBody(EntityUid body)
    {
        EnsureComp<CalamityPlayerCharacterBodyComponent>(body);
        _rotting.SetRotAfter(body, PlayerCorpseReviveWindow);
    }

    private void OnMobStateChanged(MobStateChangedEvent ev)
    {
        if (!HasComp<CalamityPlayerCharacterBodyComponent>(ev.Target))
            return;

        if (ev.NewMobState == MobState.Dead)
        {
            StartPlayerCorpseTimer(ev.Target);
        }
        else if (ev.OldMobState == MobState.Dead)
        {
            CancelPlayerCorpseTimer(ev.Target);
        }
    }

    private void StartPlayerCorpseTimer(EntityUid body)
    {
        if (HasComp<CalamityPlayerCorpseDespawnComponent>(body))
            return;

        var corpseTimer = AddComp<CalamityPlayerCorpseDespawnComponent>(body);
        var lifetime = Math.Max(1f, _configuration.GetCVar(CalamityCCVars.AbandonedCharacterCorpseLifetime));
        if (TryComp(body, out TimedDespawnComponent? previousDespawn))
        {
            corpseTimer.HadPreviousTimedDespawn = true;
            corpseTimer.PreviousTimedDespawnLifetime = previousDespawn.Lifetime;
            previousDespawn.Lifetime = lifetime;
        }
        else
        {
            EnsureComp<TimedDespawnComponent>(body).Lifetime = lifetime;
        }
    }

    private void CancelPlayerCorpseTimer(EntityUid body)
    {
        if (!TryComp(body, out CalamityPlayerCorpseDespawnComponent? corpseTimer))
            return;

        if (corpseTimer.HadPreviousTimedDespawn)
        {
            if (TryComp(body, out TimedDespawnComponent? despawn))
                despawn.Lifetime = corpseTimer.PreviousTimedDespawnLifetime;
        }
        else
        {
            RemComp<TimedDespawnComponent>(body);
        }

        RemComp<CalamityPlayerCorpseDespawnComponent>(body);
    }

    private void OnShutdownRequested()
    {
        _baseServer.ShutdownRequested -= OnShutdownRequested;
        if (_shutdownSaveAttempted)
            return;

        _shutdownSaveAttempted = true;
        if (!_configuration.GetCVar(CalamityCCVars.WorldPersistenceEnabled) || _worldMap == null)
            return;

        if (_queuedSaveReason == null)
            Announce("calamity-world-save-starting");

        _queuedSaveReason = null;
        _queuedSaveAt = null;
        PerformSnapshot("shutdown");
    }

    private void QueueSnapshot(string reason)
    {
        if (_saveInProgress || _queuedSaveReason != null)
            return;

        _queuedSaveReason = reason;
        _queuedSaveAt = _timing.RealTime + TimeSpan.FromSeconds(1);
        Announce("calamity-world-save-starting");
        Log.Info($"Queued {reason} world snapshot.");
    }

    private void OnGameRunLevelChanged(GameRunLevelChangedEvent ev)
    {
        if (ev.New != GameRunLevel.InRound || _worldHasEnteredRound)
            return;

        _worldHasEnteredRound = true;
        ScheduleNextSave();
    }

    private void PerformSnapshot(string reason)
    {
        if (_worldMap == null || !_configuration.GetCVar(CalamityCCVars.WorldPersistenceEnabled))
        {
            Announce("calamity-world-save-failed");
            return;
        }

        if (_saveInProgress)
            return;

        _saveInProgress = true;

        if (!_mapSystem.TryGetMap(_worldMap, out var mapEntity) || mapEntity == null)
        {
            Log.Error($"Cannot save world snapshot: map {_worldMap} is not loaded.");
            _saveInProgress = false;
            Announce("calamity-world-save-failed");
            ScheduleNextSave();
            return;
        }

        try
        {
            var directory = GetSnapshotDirectory(create: true)!;
            var epoch = GetCommittedSnapshots(directory)
                .Select(GetEpoch)
                .DefaultIfEmpty(0)
                .Max() + 1;
            var snapshotName = $"world-{epoch:D8}-universe.yml";
            var markerName = $"world-{epoch:D8}.complete";
            var temporaryMarkerName = $"world-{epoch:D8}.complete.tmp";
            var snapshotPath = new ResPath($"/{SnapshotDirectoryName}/{snapshotName}");
            var snapshotRelativePath = new ResPath(snapshotName).ToRootedPath();

            if (!_mapLoader.TrySaveAllEntities(snapshotPath) || !directory.Exists(snapshotRelativePath))
            {
                Log.Error($"Failed to write server world snapshot {snapshotPath}.");
                Announce("calamity-world-save-failed");
                _saveInProgress = false;
                ScheduleNextSave();
                return;
            }

            if (!_mapLoader.TryReadFile(snapshotPath, out _))
            {
                Log.Error($"World snapshot {snapshotPath} could not be parsed; leaving the previous snapshot active.");
                Announce("calamity-world-save-failed");
                _saveInProgress = false;
                ScheduleNextSave();
                return;
            }

            SaveRuntimeContent(directory, epoch);

            WriteText(directory, new ResPath(temporaryMarkerName), snapshotName);
            directory.Rename(new ResPath(temporaryMarkerName).ToRootedPath(), new ResPath(markerName).ToRootedPath());

            try
            {
                PruneSnapshots(directory);
            }
            catch (Exception exception)
            {
                Log.Warning($"World snapshot {snapshotName} was committed, but old snapshots could not be pruned: {exception}");
            }

            Log.Info($"Completed {reason} world snapshot {snapshotName}.");
            Announce("calamity-world-save-complete");
        }
        catch (Exception exception)
        {
            Log.Error($"Failed to complete {reason} world snapshot: {exception}");
            Announce("calamity-world-save-failed");
            _saveInProgress = false;
            ScheduleNextSave();
            return;
        }

        _saveInProgress = false;
        ScheduleNextSave();
    }

    private void Announce(string messageId)
    {
        var message = Loc.GetString(messageId);
        _chatManager.ChatMessageToAll(ChatChannel.Server, message, message, EntityUid.Invalid, hideChat: false, recordReplay: true);
    }

    private void ScheduleNextSave()
    {
        _nextSaveAt = _timing.RealTime + GetSaveInterval();
    }

    private TimeSpan GetSaveInterval()
    {
        var seconds = _configuration.GetCVar(CalamityCCVars.WorldPersistenceInterval);
        return TimeSpan.FromSeconds(Math.Max(1f, seconds));
    }

    private IWritableDirProvider? GetSnapshotDirectory(bool create)
    {
        var path = new ResPath(SnapshotDirectoryName).ToRootedPath();
        if (create)
            _resources.UserData.CreateDir(path);

        if (!_resources.UserData.IsDir(path))
            return null;

        return _resources.UserData.OpenSubdirectory(path);
    }

    private static ResPath[] GetCommittedSnapshots(IWritableDirProvider directory)
    {
        return directory.Find("world-*.complete", recursive: false).files
            .OrderByDescending(GetEpoch)
            .ToArray();
    }

    private static int? GetPendingRestoreEpoch(IWritableDirProvider directory)
    {
        var selection = new ResPath(RestoreSelectionFileName).ToRootedPath();
        if (!directory.Exists(selection))
            return null;

        var value = ReadText(directory, selection).Trim();
        if (!int.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture, out var epoch) || epoch <= 0)
            throw new InvalidDataException($"The pending world restore selection '{value}' is invalid.");

        return epoch;
    }

    private bool TryGetSnapshotName(IWritableDirProvider directory, ResPath marker, out string snapshotName)
    {
        snapshotName = string.Empty;
        try
        {
            snapshotName = ReadText(directory, marker).Trim();
            var epoch = GetEpoch(marker);
            var epochPrefix = $"world-{epoch:D8}";
            return epoch > 0 &&
                   IsSnapshotFileName(snapshotName) &&
                   snapshotName.StartsWith(epochPrefix, StringComparison.Ordinal) &&
                   snapshotName.Length > epochPrefix.Length &&
                   snapshotName[epochPrefix.Length] is '-' or '.' &&
                   directory.Exists(new ResPath(snapshotName).ToRootedPath());
        }
        catch (Exception exception)
        {
            Log.Error($"Could not read committed world snapshot marker {marker}: {exception}");
            return false;
        }
    }

    private static int GetEpoch(ResPath marker)
    {
        var name = Path.GetFileNameWithoutExtension(marker.ToString());
        return name.StartsWith("world-", StringComparison.Ordinal) &&
               int.TryParse(name.AsSpan("world-".Length), NumberStyles.None, CultureInfo.InvariantCulture, out var epoch)
            ? epoch
            : 0;
    }

    private static bool IsSnapshotFileName(string fileName)
    {
        return fileName.StartsWith("world-", StringComparison.Ordinal) &&
               fileName.EndsWith(".yml", StringComparison.Ordinal) &&
               !fileName.Contains('/') &&
               !fileName.Contains('\\');
    }

    private static string ReadText(IWritableDirProvider directory, ResPath path)
    {
        using var stream = directory.Open(path.ToRootedPath(), FileMode.Open, FileAccess.Read, FileShare.Read);
        using var reader = new StreamReader(stream);
        return reader.ReadToEnd();
    }

    private static void WriteText(IWritableDirProvider directory, ResPath path, string content)
    {
        using var stream = directory.Open(path.ToRootedPath(), FileMode.Create, FileAccess.Write, FileShare.None);
        using var writer = new StreamWriter(stream);
        writer.Write(content);
        writer.Flush();
        stream.Flush();
    }

    private static byte[] ReadBytes(IWritableDirProvider directory, ResPath path)
    {
        using var stream = directory.Open(path.ToRootedPath(), FileMode.Open, FileAccess.Read, FileShare.Read);
        using var memory = new MemoryStream();
        stream.CopyTo(memory);
        return memory.ToArray();
    }

    private static void WriteBytes(IWritableDirProvider directory, ResPath path, byte[] data)
    {
        using var stream = directory.Open(path.ToRootedPath(), FileMode.Create, FileAccess.Write, FileShare.None);
        stream.Write(data);
        stream.Flush();
    }

    private static void PruneSnapshots(IWritableDirProvider directory)
    {
        var committed = GetCommittedSnapshots(directory);
        var pendingRestoreEpoch = GetPendingRestoreEpoch(directory);
        foreach (var marker in committed.Skip(RetainedSnapshots))
        {
            if (pendingRestoreEpoch == GetEpoch(marker))
                continue;

            var snapshotName = ReadText(directory, marker).Trim();
            if (IsSnapshotFileName(snapshotName))
                directory.Delete(new ResPath(snapshotName).ToRootedPath());

            var assetPath = GetSnapshotAssetDirectoryPath(GetEpoch(marker));
            if (directory.Exists(assetPath))
                directory.Delete(assetPath);

            directory.Delete(marker);
        }
    }

    private void SaveRuntimeContent(IWritableDirProvider directory, int epoch)
    {
        var assetPath = GetSnapshotAssetDirectoryPath(epoch);
        directory.Delete(assetPath);
        directory.CreateDir(assetPath);
        var assetDirectory = directory.OpenSubdirectory(assetPath);

        var resourcePath = new ResPath(SnapshotResourceDirectoryName).ToRootedPath();
        assetDirectory.CreateDir(resourcePath);
        var resourceDirectory = assetDirectory.OpenSubdirectory(resourcePath);
        var uploadedFiles = _networkResourceManager.GetFilesForPersistence();
        for (var index = 0; index < uploadedFiles.Count; index++)
        {
            var (relative, data) = uploadedFiles[index];
            ValidateRuntimeResourcePath(relative.CanonPath);

            using var stream = resourceDirectory.Open(
                new ResPath($"resource-{index:D8}.bin").ToRootedPath(),
                FileMode.Create,
                FileAccess.Write,
                FileShare.None);
            using var writer = new BinaryWriter(stream, Encoding.UTF8, leaveOpen: true);
            writer.Write(relative.CanonPath);
            writer.Write(data.Length);
            writer.Write(data);
            writer.Flush();
            stream.Flush();
        }

        var prototypePath = new ResPath(SnapshotPrototypeDirectoryName).ToRootedPath();
        assetDirectory.CreateDir(prototypePath);
        var prototypeDirectory = assetDirectory.OpenSubdirectory(prototypePath);
        var prototypes = _prototypeLoadManager.GetPrototypesForPersistence();
        for (var index = 0; index < prototypes.Count; index++)
        {
            WriteText(prototypeDirectory, new ResPath($"prototype-{index:D8}.yml"), prototypes[index]);
        }

        var databaseState = _database.CaptureCalamityRollbackStateAsync().GetAwaiter().GetResult();
        WriteBytes(assetDirectory, new ResPath(SnapshotDatabaseStateName), databaseState);
        WriteText(assetDirectory, new ResPath(SnapshotAssetManifestName), SnapshotAssetManifestVersion);
    }

    private void RestoreRuntimeContent(IWritableDirProvider directory, int epoch, bool requireDatabaseRollback)
    {
        var assetPath = GetSnapshotAssetDirectoryPath(epoch);
        var assetDirectory = directory.OpenSubdirectory(assetPath);
        var manifestPath = new ResPath(SnapshotAssetManifestName).ToRootedPath();
        if (!assetDirectory.Exists(manifestPath))
        {
            throw new InvalidDataException($"Snapshot assets for epoch {epoch} have no supported completion manifest.");
        }

        var manifest = ReadText(assetDirectory, manifestPath).Trim();
        if (manifest != SnapshotAssetManifestVersion && manifest != PreviousSnapshotAssetManifestVersion)
            throw new InvalidDataException($"Snapshot assets for epoch {epoch} have unsupported manifest '{manifest}'.");

        if (requireDatabaseRollback && manifest != SnapshotAssetManifestVersion)
            throw new InvalidDataException($"World snapshot {epoch} predates database rollback data and cannot be restored completely.");

        var resourceDirectory = assetDirectory.OpenSubdirectory(new ResPath(SnapshotResourceDirectoryName).ToRootedPath());
        var uploadedFiles = new List<(ResPath Relative, byte[] Data)>();
        foreach (var file in resourceDirectory.Find("resource-*.bin", recursive: false).files
                     .OrderBy(path => path.ToString(), StringComparer.Ordinal))
        {
            using var stream = resourceDirectory.Open(file, FileMode.Open, FileAccess.Read, FileShare.Read);
            using var reader = new BinaryReader(stream, Encoding.UTF8, leaveOpen: false);
            var path = ValidateRuntimeResourcePath(reader.ReadString());
            var dataLength = reader.ReadInt32();
            if (dataLength < 0 || dataLength > stream.Length - stream.Position)
                throw new InvalidDataException($"Uploaded resource record {file} has an invalid data length.");

            var data = reader.ReadBytes(dataLength);
            if (data.Length != dataLength || stream.Position != stream.Length)
                throw new InvalidDataException($"Uploaded resource record {file} is truncated or has trailing data.");

            uploadedFiles.Add((path, data));
        }

        var prototypeDirectory = assetDirectory.OpenSubdirectory(new ResPath(SnapshotPrototypeDirectoryName).ToRootedPath());
        var prototypes = prototypeDirectory.Find("prototype-*.yml", recursive: false).files
            .OrderBy(path => path.ToString(), StringComparer.Ordinal)
            .Select(path => ReadText(prototypeDirectory, path))
            .ToArray();

        // Client connections start after this method returns, so the regular upload handshake sends
        // resources first and prototypes second, before any restored map entities are replicated.
        _networkResourceManager.RestorePersistentFiles(uploadedFiles);
        _prototypeLoadManager.RestorePersistentPrototypes(prototypes);

        if (manifest == SnapshotAssetManifestVersion)
        {
            var databaseStatePath = new ResPath(SnapshotDatabaseStateName).ToRootedPath();
            if (!assetDirectory.Exists(databaseStatePath))
                throw new InvalidDataException($"Snapshot assets for epoch {epoch} have no database rollback state.");

            var databaseState = ReadBytes(assetDirectory, databaseStatePath);
            _database.RestoreCalamityRollbackStateAsync(databaseState).GetAwaiter().GetResult();
        }
        else
        {
            Log.Warning($"World snapshot {epoch} predates database rollback data; restoring it will not roll back database-backed gameplay state.");
        }
    }

    private static ResPath ValidateRuntimeResourcePath(string path)
    {
        if (!ResPath.IsValidPath(path))
            throw new InvalidDataException($"Runtime resource path '{path}' contains an invalid separator.");

        var relative = new ResPath(path);
        if (relative.IsRooted || relative.IsSelf || relative.Clean().CanonPath != path ||
            path.Split('/').Any(segment => segment is ".." or "." or ""))
        {
            throw new InvalidDataException($"Runtime resource path '{path}' is not a normalized relative path.");
        }

        return relative;
    }

    private static ResPath GetSnapshotAssetDirectoryPath(int epoch)
    {
        return new ResPath($"world-{epoch:D8}{SnapshotAssetDirectorySuffix}").ToRootedPath();
    }
}
