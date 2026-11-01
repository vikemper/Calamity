using System.Globalization;
using System.IO;
using System.Linq;
using Content.Server.Chat.Managers;
using Content.Server.GameTicking;
using Content.Shared._Calamity.CCVar;
using Content.Shared.Chat;
using Content.Shared.GameTicking;
using Content.Shared.Maps;
using Robust.Server;
using Robust.Shared.Configuration;
using Robust.Shared.ContentPack;
using Robust.Shared.EntitySerialization.Systems;
using Robust.Shared.GameObjects;
using Robust.Shared.Map.Components;
using Robust.Shared.Map;
using Robust.Shared.Prototypes;
using Robust.Shared.Timing;
using Robust.Shared.Utility;

namespace Content.Server._Calamity.Persistence;

/// <summary>
/// Saves and restores the active world map using committed, versioned snapshots.
/// This is the first single-map persistence slice; sector state will be added separately.
/// </summary>
public sealed class CalamityWorldPersistenceSystem : EntitySystem
{
    private const string SnapshotDirectoryName = "Calamity/Persistence";
    private const string MainMapKey = "main";
    private const int RetainedSnapshots = 2;

    [Dependency] private IBaseServer _baseServer = default!;
    [Dependency] private IConfigurationManager _configuration = default!;
    [Dependency] private IChatManager _chatManager = default!;
    [Dependency] private SharedMapSystem _mapSystem = default!;
    [Dependency] private IResourceManager _resources = default!;
    [Dependency] private IGameTiming _timing = default!;
    [Dependency] private MapLoaderSystem _mapLoader = default!;
    [Dependency] private MetaDataSystem _metadata = default!;

    private MapId? _worldMap;
    private TimeSpan _nextSaveAt;
    private TimeSpan? _queuedSaveAt;
    private string? _queuedSaveReason;
    private bool _saveInProgress;
    private bool _shutdownSaveAttempted;

    /// <summary>
    /// True after this server boot restored a previously saved world map.
    /// Initial world generation can use this to avoid duplicating saved content.
    /// </summary>
    public bool RestoredSavedWorldMapThisBoot { get; private set; }

    public override void Initialize()
    {
        base.Initialize();
        _baseServer.ShutdownRequested += OnShutdownRequested;
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
    public void SetWorldMap(MapId mapId)
    {
        _worldMap = mapId;
        RegisterPersistentMap(MainMapKey, mapId);
        _nextSaveAt = _timing.RealTime + GetSaveInterval();
    }

    /// <summary>
    /// Registers a world map under an identifier that remains stable across server restarts.
    /// Sector systems can use coordinates in place of runtime MapIds.
    /// </summary>
    public void RegisterPersistentMap(string key, MapId mapId)
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
    }

    /// <summary>
    /// Queues a manual world snapshot and announces it before the synchronous save begins.
    /// </summary>
    public bool RequestManualSave()
    {
        if (!_configuration.GetCVar(CalamityCCVars.WorldPersistenceEnabled) || _worldMap == null || _saveInProgress || _queuedSaveReason != null)
            return false;

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

        if (!_configuration.GetCVar(CalamityCCVars.WorldPersistenceEnabled))
            return false;

        var directory = GetSnapshotDirectory(create: false);
        if (directory == null)
            return false;

        foreach (var marker in GetCommittedSnapshots(directory))
        {
            try
            {
                var snapshotName = ReadText(directory, marker).Trim();
                if (!IsSnapshotFileName(snapshotName))
                {
                    Log.Error($"Ignoring invalid persistence marker {marker}.");
                    continue;
                }

                var snapshotPath = new ResPath($"{SnapshotDirectoryName}/{snapshotName}");
                if (!_mapLoader.TryLoadMap(snapshotPath, out var map, out var loadedGrids) || map == null)
                {
                    Log.Error($"Could not load world snapshot {snapshotPath}; trying the previous committed snapshot.");
                    continue;
                }

                mapId = map.Value.Comp.MapId;
                grids = loadedGrids.Select(grid => grid.Owner).ToArray();
                _metadata.SetEntityName(map.Value.Owner, prototype.MapName);
                RaiseLocalEvent(new PostGameMapLoad(prototype, mapId, grids, null));
                RestoredSavedWorldMapThisBoot = true;
                SetWorldMap(mapId);
                Log.Info($"Restored world map from snapshot {snapshotPath}.");
                return true;
            }
            catch (Exception exception)
            {
                Log.Error($"Failed to restore snapshot marker {marker}: {exception}");
            }
        }

        return false;
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
            var snapshotName = $"world-{epoch:D8}-{MainMapKey}.yml";
            var markerName = $"world-{epoch:D8}.complete";
            var temporaryMarkerName = $"world-{epoch:D8}.complete.tmp";
            var snapshotPath = new ResPath($"{SnapshotDirectoryName}/{snapshotName}");

            if (!_mapLoader.TrySaveMap(mapEntity.Value, snapshotPath) || !directory.Exists(snapshotPath))
            {
                Log.Error($"Failed to write world snapshot {snapshotPath}.");
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
            PruneSnapshots(directory);
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
