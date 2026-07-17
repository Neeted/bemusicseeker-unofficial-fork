using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using BeMusicSeeker.Models.LR2;
using BeMusicSeeker.Models.Utils;

namespace BeMusicSeeker.Models.BmsLibraryInternal;

/// <summary>
/// Owns the in-memory BMS/bmson storage rows, their versions, and the shared row write gate.
/// Derived catalog indexes and consumer-specific state remain composed by <see cref="BMSLibrary"/>.
/// </summary>
internal sealed class CatalogStorageRowsOwner
{
    private readonly ReaderWriterLockSlimWrapper writeGate = new();

    private readonly object versionGate = new();

    private List<BMSFile> bmsRows = [];

    private List<LR2SongDBExtended.bmson_song> bmsonRows = [];

    private int bmsRowsVersion;

    private int bmsonRowsVersion;

    internal ReaderWriterLockSlimWrapper WriteGate => writeGate;

    internal object VersionGate => versionGate;

    internal IReadOnlyList<BMSFile> BmsRows => bmsRows;

    internal IReadOnlyList<LR2SongDBExtended.bmson_song> BmsonRows => bmsonRows;

    internal IReadOnlyList<BMSFile> GetBmsRowsReadOnly() => bmsRows.AsReadOnly();

    internal IReadOnlyList<LR2SongDBExtended.bmson_song> GetBmsonRowsReadOnly() => bmsonRows.AsReadOnly();

    internal int BmsRowsVersion => Volatile.Read(ref bmsRowsVersion);

    internal int BmsonRowsVersion => Volatile.Read(ref bmsonRowsVersion);

    internal StorageRowsVersionSnapshot ReplaceBmsRows(List<BMSFile> rows)
    {
        using (writeGate.GetWriterGuard())
        {
            lock (versionGate)
            {
                int previousBmsRowsVersion = bmsRowsVersion;
                bmsRows = rows ?? [];
                IncrementBmsRowsVersion();
                return CreateVersionSnapshot(previousBmsRowsVersion, bmsonRowsVersion);
            }
        }
    }

    internal StorageRowsVersionSnapshot ReplaceBmsonRows(List<LR2SongDBExtended.bmson_song> rows)
    {
        using (writeGate.GetWriterGuard())
        {
            lock (versionGate)
            {
                int previousBmsonRowsVersion = bmsonRowsVersion;
                bmsonRows = rows ?? [];
                IncrementBmsonRowsVersion();
                return CreateVersionSnapshot(bmsRowsVersion, previousBmsonRowsVersion);
            }
        }
    }

    internal CatalogStorageRowsSnapshot ReplaceRowsAndCaptureSnapshot(
        List<BMSFile> nextBmsRows,
        List<LR2SongDBExtended.bmson_song> nextBmsonRows)
    {
        using (writeGate.GetWriterGuard())
        {
            lock (versionGate)
            {
                bmsRows = nextBmsRows ?? [];
                bmsonRows = nextBmsonRows ?? [];
                IncrementBmsRowsVersion();
                IncrementBmsonRowsVersion();
                return new CatalogStorageRowsSnapshot(
                    bmsRows,
                    bmsonRows,
                    bmsRowsVersion,
                    bmsonRowsVersion);
            }
        }
    }

    internal StorageRowsVersionSnapshot ApplyInstalledTargets(ChartStorageTargetSet addedTargets)
    {
        if (addedTargets == null)
        {
            return CaptureVersionSnapshot();
        }

        using (writeGate.GetWriterGuard())
        {
            lock (versionGate)
            {
                int previousBmsRowsVersion = bmsRowsVersion;
                int previousBmsonRowsVersion = bmsonRowsVersion;
                if (addedTargets.BmsFiles.Count > 0)
                {
                    var addedBmsPathSet = new HashSet<string>(
                        addedTargets.BmsFiles
                            .Select(file => CreateOwnedPathKey(file?.path))
                            .Where(path => !string.IsNullOrWhiteSpace(path)),
                        StringComparer.OrdinalIgnoreCase);
                    bmsRows = [.. (bmsRows ?? [])
                        .Where(file => file != null && !addedBmsPathSet.Contains(CreateOwnedPathKey(file.path))),
                        .. addedTargets.BmsFiles];
                    IncrementBmsRowsVersion();
                }
                if (addedTargets.BmsonSongs.Count > 0)
                {
                    var nextBmsonByPath = (bmsonRows ?? [])
                        .Where(song => song != null && !string.IsNullOrWhiteSpace(song.path))
                        .GroupBy(song => CreateOwnedPathKey(song.path), StringComparer.OrdinalIgnoreCase)
                        .ToDictionary(group => group.Key, group => group.First(), StringComparer.OrdinalIgnoreCase);
                    foreach (LR2SongDBExtended.bmson_song addedBmsonSong in addedTargets.BmsonSongs)
                    {
                        nextBmsonByPath[CreateOwnedPathKey(addedBmsonSong.path)] = addedBmsonSong;
                    }
                    bmsonRows = [.. nextBmsonByPath.Values.OrderBy(song => song.path, StringComparer.OrdinalIgnoreCase)];
                    IncrementBmsonRowsVersion();
                }
                return new StorageRowsVersionSnapshot(
                    previousBmsRowsVersion,
                    previousBmsonRowsVersion,
                    bmsRowsVersion,
                    bmsonRowsVersion);
            }
        }
    }

    /// <summary>
    /// Applies one catalog command's relocation and removal effects to the live storage rows.
    /// Each storage kind advances its version at most once for the command, even when a
    /// relocation and removal are combined.
    /// </summary>
    internal StorageRowsVersionSnapshot ApplyCatalogMutation(
        bool bmsRowsRelocated,
        bool bmsonRowsRelocated,
        CatalogStorageRowsRemovalRequest removalRequest,
        IEnumerable<CatalogRelocationPathFact> protectedPathFacts,
        IEnumerable<BMSFile> addedBmsFiles = null,
        IEnumerable<LR2SongDBExtended.bmson_song> addedBmsonSongs = null)
    {
        List<BMSFile> addedBmsRows = [.. (addedBmsFiles ?? []).Where(file => file != null)];
        List<LR2SongDBExtended.bmson_song> addedBmsonRows = [.. (addedBmsonSongs ?? []).Where(song => song != null)];
        using (writeGate.GetWriterGuard())
        {
            lock (versionGate)
            {
                int previousBmsRowsVersion = bmsRowsVersion;
                int previousBmsonRowsVersion = bmsonRowsVersion;
                var removedBmsRows = new HashSet<BMSFile>(
                    removalRequest?.RemovedBmsRows ?? []);
                var bmsPathCleanupKeys = new HashSet<string>(
                    removalRequest?.BmsPathCleanupKeys ?? [],
                    StringComparer.OrdinalIgnoreCase);
                bmsPathCleanupKeys.ExceptWith(CreateProtectedPathKeys(
                    protectedPathFacts,
                    ChartFileKind.Bms));
                var removedBmsonRows = new HashSet<LR2SongDBExtended.bmson_song>(
                    removalRequest?.RemovedBmsonRows ?? []);
                var bmsonPathCleanupKeys = new HashSet<string>(
                    removalRequest?.BmsonPathCleanupKeys ?? [],
                    StringComparer.OrdinalIgnoreCase);
                bmsonPathCleanupKeys.ExceptWith(CreateProtectedPathKeys(
                    protectedPathFacts,
                    ChartFileKind.Bmson));
                bool bmsRowsRemoved = removalRequest != null
                    && (removedBmsRows.Count > 0 || bmsPathCleanupKeys.Count > 0);
                bool bmsonRowsRemoved = removalRequest != null
                    && (removedBmsonRows.Count > 0 || bmsonPathCleanupKeys.Count > 0);
                if (bmsRowsRemoved)
                {
                    bmsRows = [.. (bmsRows ?? [])
                        .Where(file => !IsMatchedBmsRow(
                            file,
                            removedBmsRows,
                            bmsPathCleanupKeys))];
                }
                if (bmsonRowsRemoved)
                {
                    bmsonRows = [.. (bmsonRows ?? [])
                        .Where(song => !IsMatchedBmsonRow(
                            song,
                            removedBmsonRows,
                            bmsonPathCleanupKeys))];
                }

                if (addedBmsRows.Count > 0)
                {
                    var addedBmsPathSet = new HashSet<string>(
                        addedBmsRows
                            .Select(file => CreateOwnedPathKey(file.path))
                            .Where(path => !string.IsNullOrWhiteSpace(path)),
                        StringComparer.OrdinalIgnoreCase);
                    bmsRows = [.. (bmsRows ?? [])
                        .Where(file => file != null && !addedBmsPathSet.Contains(CreateOwnedPathKey(file.path))),
                        .. addedBmsRows];
                }
                if (addedBmsonRows.Count > 0)
                {
                    var nextBmsonByPath = (bmsonRows ?? [])
                        .Where(song => song != null && !string.IsNullOrWhiteSpace(song.path))
                        .GroupBy(song => CreateOwnedPathKey(song.path), StringComparer.OrdinalIgnoreCase)
                        .ToDictionary(group => group.Key, group => group.First(), StringComparer.OrdinalIgnoreCase);
                    foreach (LR2SongDBExtended.bmson_song addedBmsonSong in addedBmsonRows)
                    {
                        nextBmsonByPath[CreateOwnedPathKey(addedBmsonSong.path)] = addedBmsonSong;
                    }
                    bmsonRows = [.. nextBmsonByPath.Values.OrderBy(song => song.path, StringComparer.OrdinalIgnoreCase)];
                }
                if (bmsRowsRelocated || bmsRowsRemoved || addedBmsRows.Count > 0)
                {
                    IncrementBmsRowsVersion();
                }
                if (bmsonRowsRelocated || bmsonRowsRemoved || addedBmsonRows.Count > 0)
                {
                    IncrementBmsonRowsVersion();
                }
                return new StorageRowsVersionSnapshot(
                    previousBmsRowsVersion,
                    previousBmsonRowsVersion,
                    bmsRowsVersion,
                    bmsonRowsVersion);
            }
        }
    }

    private static HashSet<string> CreateProtectedPathKeys(
        IEnumerable<CatalogRelocationPathFact> protectedPathFacts,
        ChartFileKind kind)
    {
        return new HashSet<string>(
            (protectedPathFacts ?? [])
                .Where(fact => fact?.Kind == kind)
                .Select(fact => CreateOwnedPathKey(fact.NewPath))
                .Where(path => !string.IsNullOrWhiteSpace(path)),
            StringComparer.OrdinalIgnoreCase);
    }

    internal StorageRowsVersionSnapshot CaptureVersionSnapshot()
    {
        lock (versionGate)
        {
            return new StorageRowsVersionSnapshot(bmsRowsVersion, bmsonRowsVersion);
        }
    }

    internal CatalogStorageRowsSnapshot CaptureSnapshot()
    {
        lock (versionGate)
        {
            return new CatalogStorageRowsSnapshot(
                bmsRows,
                bmsonRows,
                bmsRowsVersion,
                bmsonRowsVersion);
        }
    }

    private void IncrementBmsRowsVersion()
    {
        Interlocked.Increment(ref bmsRowsVersion);
    }

    private void IncrementBmsonRowsVersion()
    {
        Interlocked.Increment(ref bmsonRowsVersion);
    }

    private StorageRowsVersionSnapshot CreateVersionSnapshot(
        int previousBmsRowsVersion,
        int previousBmsonRowsVersion)
    {
        return new StorageRowsVersionSnapshot(
            previousBmsRowsVersion,
            previousBmsonRowsVersion,
            bmsRowsVersion,
            bmsonRowsVersion);
    }

    private static bool IsMatchedBmsRow(
        BMSFile file,
        ISet<BMSFile> removedRows,
        ISet<string> pathCleanupKeys)
    {
        if (file == null)
        {
            return false;
        }
        if (removedRows?.Contains(file) == true)
        {
            return true;
        }
        string pathKey = CreateOwnedPathKey(file.path);
        return !string.IsNullOrWhiteSpace(pathKey)
            && pathCleanupKeys?.Contains(pathKey) == true;
    }

    private static bool IsMatchedBmsonRow(
        LR2SongDBExtended.bmson_song song,
        ISet<LR2SongDBExtended.bmson_song> removedRows,
        ISet<string> pathCleanupKeys)
    {
        if (song == null)
        {
            return false;
        }
        if (removedRows?.Contains(song) == true)
        {
            return true;
        }
        string pathKey = CreateOwnedPathKey(song.path);
        return !string.IsNullOrWhiteSpace(pathKey)
            && pathCleanupKeys?.Contains(pathKey) == true;
    }

    private static string CreateOwnedPathKey(string path)
    {
        return OwnedChartCollectionState.CreateOwnedPathKey(path);
    }
}

internal sealed class CatalogStorageRowsSnapshot
{
    internal CatalogStorageRowsSnapshot(
        List<BMSFile> bmsRows,
        List<LR2SongDBExtended.bmson_song> bmsonRows,
        int bmsRowsVersion,
        int bmsonRowsVersion)
    {
        BmsRows = bmsRows ?? [];
        BmsonRows = bmsonRows ?? [];
        BmsRowsVersion = bmsRowsVersion;
        BmsonRowsVersion = bmsonRowsVersion;
    }

    internal IReadOnlyList<BMSFile> BmsRows { get; }

    internal IReadOnlyList<LR2SongDBExtended.bmson_song> BmsonRows { get; }

    internal int BmsRowsVersion { get; }

    internal int BmsonRowsVersion { get; }
}
