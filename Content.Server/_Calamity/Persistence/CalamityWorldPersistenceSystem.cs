using System.Globalization;
using System.IO;
using System.Linq;
using Content.Server.Atmos.Rotting;
using Content.Server.Chat.Managers;
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
/// Saves and restores server entity state using committed, versioned snapshots.
/// Sector-specific identity and navigation will be added separately.
/// </summary>
public sealed class CalamityWorldPersistenceSystem : EntitySystem
{
    private const string SnapshotDirectoryName = "Calamity/Persistence";
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

    private MapId? _worldMap;
    private TimeSpan _nextSaveAt;
    private TimeSpan? _queuedSaveAt;
    private string? _queuedSaveReason;
    private bool _saveInProgress;
    private bool _shutdownSaveAttempted;
    private bool _worldHasEnteredRound;

    /// <summary>
    /// True after this server boot restored a previously saved world map.
    /// Initial world generation can use this to avoid duplicating saved content.
    /// </summary>
    public bool RestoredSavedWorldMapThisBoot { get; private set; }

    /// <summary>
    /// True when the restored snapshot included the server's game rules and auxiliary entities.
    /// </summary>
    public bool RestoredFullWorldSnapshotThisBoot { get; private set; }

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

    /// <summary>
    /// Called by GameTicker for the selected main map after normal loading, or when this system restores it.
    /// </summary>
    public void SetWorldMap(MapId mapId, GameMapPrototype? prototype = null)
    {
        _worldMap = mapId;
        RegisterPersistentMap(MainMapKey, mapId, prototype?.ID);
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

    /// <summary>
    /// Loads the newest valid committed world snapshot. Returns false when there is no usable snapshot.
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

        foreach (var marker in GetCommittedSnapshots(directory))
        {
            var snapshotName = string.Empty;
            var snapshotPath = default(ResPath);
            var isFullWorldSnapshot = false;
            var restoredPrototype = prototype;
            MapId restoredMapId;
            IReadOnlyList<EntityUid> restoredGrids;
            LoadResult? loadedState = null;

            try
            {
                snapshotName = ReadText(directory, marker).Trim();
                if (!IsSnapshotFileName(snapshotName))
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

                        if (!string.IsNullOrWhiteSpace(persistentMap.GameMapPrototype) &&
                            _prototypeManager.TryIndex<GameMapPrototype>(persistentMap.GameMapPrototype, out var savedPrototype))
                        {
                            restoredPrototype = savedPrototype;
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

                if (!isFullWorldSnapshot &&
                    TryComp(mainMap.Value.Owner, out CalamityPersistentMapComponent? legacyPersistentMap) &&
                    !string.IsNullOrWhiteSpace(legacyPersistentMap.GameMapPrototype) &&
                    _prototypeManager.TryIndex<GameMapPrototype>(legacyPersistentMap.GameMapPrototype, out var legacyPrototype))
                {
                    restoredPrototype = legacyPrototype;
                }

                restoredMapId = mainMap.Value.Comp.MapId;
                restoredGrids = isFullWorldSnapshot
                    ? loadedState.Grids
                        .Where(grid => Transform(grid.Owner).MapID == restoredMapId)
                        .Select(grid => grid.Owner)
                        .ToArray()
                    : loadedState.Grids.Select(grid => grid.Owner).ToArray();
            }
            catch (Exception exception)
            {
                if (loadedState != null)
                    _mapLoader.Delete(loadedState);

                Log.Error($"Failed to load snapshot marker {marker}: {exception}; trying the previous committed snapshot.");
                continue;
            }

            mapId = restoredMapId;
            grids = restoredGrids;
            RestoredSavedWorldMapThisBoot = true;
            RestoredFullWorldSnapshotThisBoot = isFullWorldSnapshot;

            try
            {
                SetWorldMap(mapId, restoredPrototype);
                if (_mapSystem.TryGetMap(mapId, out var restoredMapEntity) && restoredMapEntity != null)
                    _metadata.SetEntityName(restoredMapEntity.Value, restoredPrototype.MapName);

                RestoreLoadedPlayerMindData();
                RaiseLocalEvent(new PostGameMapLoad(restoredPrototype, mapId, grids, null, restored: true));
                Log.Info($"Restored world map from snapshot {snapshotPath}.");
            }
            catch (Exception exception)
            {
                // The snapshot entities are already loaded. Do not fall back and load another copy of the world.
                Log.Error($"World snapshot {snapshotPath} loaded, but post-load initialization failed: {exception}");
            }

            return true;
        }

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
            var snapshotPath = new ResPath($"{SnapshotDirectoryName}/{snapshotName}");

            if (!_mapLoader.TrySaveAllEntities(snapshotPath) || !directory.Exists(snapshotPath))
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

            WriteText(directory, new ResPath(temporaryMarkerName), snapshotName);
            directory.Rename(new ResPath(temporaryMarkerName), new ResPath(markerName));

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
        var path = new ResPath(SnapshotDirectoryName);
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
        using var stream = directory.Open(path, FileMode.Open, FileAccess.Read, FileShare.Read);
        using var reader = new StreamReader(stream);
        return reader.ReadToEnd();
    }

    private static void WriteText(IWritableDirProvider directory, ResPath path, string content)
    {
        using var stream = directory.Open(path, FileMode.Create, FileAccess.Write, FileShare.None);
        using var writer = new StreamWriter(stream);
        writer.Write(content);
        writer.Flush();
        stream.Flush();
    }

    private static void PruneSnapshots(IWritableDirProvider directory)
    {
        var committed = GetCommittedSnapshots(directory);
        foreach (var marker in committed.Skip(RetainedSnapshots))
        {
            var snapshotName = ReadText(directory, marker).Trim();
            if (IsSnapshotFileName(snapshotName))
                directory.Delete(new ResPath(snapshotName));

            directory.Delete(marker);
        }
    }
}
