using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using BeMusicSeeker.Models.LR2;
using BeMusicSeeker.Properties;
using BeMusicSeeker.Models.Utils;

namespace BeMusicSeeker.Models.BmsLibraryInternal;

/// <summary>
/// Owns canonical catalog storage-row and owned-collection mutations.
/// Consumer cache and presentation effects remain composed by <see cref="BMSLibrary"/>.
/// </summary>
internal sealed class CatalogMutationOwner
{
    private readonly CatalogStorageRowsOwner storageRowsOwner;

    private readonly CatalogOwnedCollectionOwner ownedCollectionOwner;

    private readonly BmsLibraryDbGateway dbGateway;

    private readonly Lr2ManagedPlaylistOutputScopeOwner managedPlaylistOutputScopeOwner;

    private readonly ReaderWriterLockSlimWrapper maintenanceWriteGate = new();

    internal CatalogMutationOwner(
        CatalogStorageRowsOwner storageRowsOwner,
        CatalogOwnedCollectionOwner ownedCollectionOwner,
        BmsLibraryDbGateway dbGateway)
    {
        this.storageRowsOwner = storageRowsOwner ?? throw new ArgumentNullException(nameof(storageRowsOwner));
        this.ownedCollectionOwner = ownedCollectionOwner ?? throw new ArgumentNullException(nameof(ownedCollectionOwner));
        this.dbGateway = dbGateway;
        managedPlaylistOutputScopeOwner = dbGateway == null
            ? null
            : new Lr2ManagedPlaylistOutputScopeOwner(dbGateway);
    }

    internal event EventHandler<CatalogWriteFailureFact> CatalogWriteFailurePublished;

    internal Lr2SongDbSyncAppManagedOutputScope CaptureLr2SongDbSyncAppManagedOutputScope(
        BmsLibraryOptionsSnapshot options)
    {
        if (options == null)
        {
            throw new InvalidOperationException("BMS library options snapshot provider returned null.");
        }

        return managedPlaylistOutputScopeOwner?.Capture(options)
            ?? new Lr2SongDbSyncAppManagedOutputScope([], [], [], isComplete: false);
    }

    /// <summary>
    /// Applies maintenance rows and related song rows in one catalog transaction.
    /// Maintenance evaluators submit immutable facts; they never write the database directly.
    /// </summary>
    internal CatalogMaintenanceWriteReceipt ApplyMaintenanceWrite(CatalogMaintenanceWriteRequest request)
    {
        if (request == null || !request.HasChanges)
        {
            return CatalogMaintenanceWriteReceipt.NotApplied;
        }
        if (dbGateway == null)
        {
            throw new InvalidOperationException("Catalog mutation owner is not configured with a song database.");
        }

        using (maintenanceWriteGate.GetWriterGuard())
        {
            return ApplyMaintenanceWriteUnderGuard(request);
        }
    }

    internal CatalogMaintenanceWriteReceipt ApplyMaintenanceWriteUnderGuard(CatalogMaintenanceWriteRequest request)
    {
        if (request == null || !request.HasChanges)
        {
            return CatalogMaintenanceWriteReceipt.NotApplied;
        }
        if (dbGateway == null)
        {
            throw new InvalidOperationException("Catalog mutation owner is not configured with a song database.");
        }

        int deletedMaintenanceCount = 0;
        dbGateway.ExecuteSongDbTransaction(songDb =>
        {
            if (request.MaintenanceInfos.Count > 0 || request.StaleMaintenancePaths.Count > 0)
            {
                BmsLibraryDbGateway.EnsureMaintenanceSchema(songDb);
            }
            if (request.BmsonSongs.Count > 0)
            {
                BmsLibraryDbGateway.EnsureBmsonSchema(songDb);
            }
            foreach (BMSFileMaintenanceInfo maintenanceInfo in request.MaintenanceInfos)
            {
                songDb.InsertOrReplace(maintenanceInfo, typeof(LR2SongDBExtended.maintenance));
            }
            foreach (BMSFile song in request.Songs)
            {
                Lr2SongDbWriter.UpsertGeneratedSong(songDb, song);
            }
            foreach (LR2SongDBExtended.bmson_song bmsonSong in request.BmsonSongs)
            {
                songDb.InsertOrReplace(bmsonSong, typeof(LR2SongDBExtended.bmson_song));
            }
            foreach (string path in request.StaleMaintenancePaths)
            {
                deletedMaintenanceCount += songDb.Delete<LR2SongDBExtended.maintenance>(path);
            }
        });
        return new CatalogMaintenanceWriteReceipt(
            applied: true,
            request.MaintenanceInfos.Count,
            request.Songs.Count,
            request.BmsonSongs.Count,
            deletedMaintenanceCount);
    }

    /// <summary>
    /// Persists one immutable chart-info chunk in the catalog transaction boundary.
    /// </summary>
    internal CatalogChartInfoWriteReceipt ApplyChartInfoWrite(CatalogChartInfoWriteRequest request)
    {
        if (request == null || !request.HasChanges)
        {
            return CatalogChartInfoWriteReceipt.NotApplied;
        }
        if (dbGateway == null)
        {
            throw new InvalidOperationException("Catalog mutation owner is not configured with a song database.");
        }

        using (maintenanceWriteGate.GetWriterGuard())
        {
            dbGateway.ExecuteSongDbTransaction(songDb =>
            {
                ApplyChartInfoWriteToTransaction(songDb, request);
            });
        }
        return CreateChartInfoWriteReceipt(request);
    }

    /// <summary>
    /// Persists mode-detection song rows under the catalog write gate.
    /// </summary>
    internal void ApplyModeChangeSongRows(IEnumerable<BMSFile> bmsFiles)
    {
        List<BMSFile> files = [.. (bmsFiles ?? []).Where(file => file != null)];
        if (files.Count == 0)
        {
            return;
        }
        if (dbGateway == null)
        {
            throw new InvalidOperationException("Catalog mutation owner is not configured with a song database.");
        }

        try
        {
            using (maintenanceWriteGate.GetWriterGuard())
            {
                dbGateway.UpsertSongs(files);
            }
        }
        catch (Exception exception)
        {
            PublishCatalogWriteFailureFactBestEffort(new CatalogWriteFailureFact(
                runId: "song_db_write",
                stage: "lr2_song_db_mode_upsert_failed",
                logReason: "setModeAndCommitToDB",
                exception));
            throw;
        }
    }

    /// <summary>
    /// Persists playlist level writeback rows under the catalog write gate.
    /// </summary>
    internal void ApplyPlaylistLevelRows(IEnumerable<BMSFile> bmsFiles)
    {
        List<BMSFile> files = [.. (bmsFiles ?? [])
            .Where(file => file != null && !string.IsNullOrWhiteSpace(file.path) && file.level.HasValue)];
        if (files.Count == 0)
        {
            return;
        }
        if (dbGateway == null)
        {
            throw new InvalidOperationException("Catalog mutation owner is not configured with a song database.");
        }

        try
        {
            using (maintenanceWriteGate.GetWriterGuard())
            {
                dbGateway.UpdateSongLevels(files);
            }
        }
        catch (Exception exception)
        {
            PublishCatalogWriteFailureFactBestEffort(new CatalogWriteFailureFact(
                runId: "song_db_write",
                stage: "lr2_song_db_playlist_level_update_failed",
                logReason: "ReplaceBmsFileLevelByTableEntryLevel",
                exception));
            throw;
        }
    }

    /// <summary>
    /// Commits chart storage rows and chart-info facts as one catalog command.
    /// The caller supplies a snapshot request; no facade-owned database writer is needed.
    /// </summary>
    internal CatalogChartInfoStorageWriteReceipt ApplyChartInfoStorageWrite(
        CatalogChartInfoStorageWriteRequest request)
    {
        if (request == null || !request.HasChanges)
        {
            return CatalogChartInfoStorageWriteReceipt.NotApplied;
        }
        if (dbGateway == null)
        {
            throw new InvalidOperationException("Catalog mutation owner is not configured with a song database.");
        }

        Lr2ChartInfoSongProjectionWriteResult chartInfoSongProjectionResult =
            Lr2ChartInfoSongProjectionWriteResult.Empty;
        using (maintenanceWriteGate.GetWriterGuard())
        {
            dbGateway.ExecuteSongDbTransaction(songDb =>
            {
                if (request.BmsRows.Count > 0)
                {
                    BmsLibraryDbGateway.EnsureBmsonSchema(songDb);
                    BmsLibraryDbGateway.EnsureSongLookupIndexes(songDb);
                    Lr2SongDbWriter.UpsertGeneratedSongs(songDb, request.BmsRows);
                }
                if (request.ChartInfoSongProjections.Count > 0)
                {
                    chartInfoSongProjectionResult = Lr2SongDbWriter.UpdateChartInfoSongProjections(
                        songDb,
                        request.ChartInfoSongProjections);
                }
                if (request.BmsonRows.Count > 0)
                {
                    BmsLibraryDbGateway.EnsureBmsonSchema(songDb);
                    foreach (LR2SongDBExtended.bmson_song row in request.BmsonRows)
                    {
                        songDb.InsertOrReplace(row, typeof(LR2SongDBExtended.bmson_song));
                    }
                }
                ApplyChartInfoWriteToTransaction(songDb, request.ChartInfo);
            });
        }
        return new CatalogChartInfoStorageWriteReceipt(
            applied: true,
            request.BmsRows.Count,
            request.BmsonRows.Count,
            CreateChartInfoWriteReceipt(request.ChartInfo),
            chartInfoSongProjectionResult);
    }

    private static void ApplyChartInfoWriteToTransaction(
        LR2SongDBExtended songDb,
        CatalogChartInfoWriteRequest request)
    {
        BmsLibraryDbGateway.UpsertChartInfoBackfillChunk(
            songDb,
            request.DigestEntries,
            request.ChartInfoRows,
            request.ParseFailureRows,
            request.ParseFailureDeleteMd5s);
    }

    private static CatalogChartInfoWriteReceipt ApplyChartInfoWriteInTransactionUnderGuard(
        LR2SongDBExtended songDb,
        CatalogChartInfoWriteRequest request)
    {
        if (request == null || !request.HasChanges)
        {
            return CatalogChartInfoWriteReceipt.NotApplied;
        }
        if (songDb == null)
        {
            throw new ArgumentNullException(nameof(songDb));
        }

        ApplyChartInfoWriteToTransaction(songDb, request);
        return CreateChartInfoWriteReceipt(request);
    }

    private static CatalogChartInfoWriteReceipt CreateChartInfoWriteReceipt(
        CatalogChartInfoWriteRequest request)
    {
        if (request == null || !request.HasChanges)
        {
            return CatalogChartInfoWriteReceipt.NotApplied;
        }
        return new CatalogChartInfoWriteReceipt(
            applied: true,
            request.DigestEntries.Count,
            request.ChartInfoRows.Count,
            request.ParseFailureRows.Count,
            request.ParseFailureDeleteMd5s.Count);
    }

    internal IDisposable EnterMaintenanceWriteGuard()
    {
        return maintenanceWriteGate.GetWriterGuard();
    }

    internal IDisposable EnterStorageRowsWriteGuard()
    {
        return storageRowsOwner.WriteGate.GetWriterGuard();
    }

    internal Lr2FolderFileDbSyncResult ApplyLr2FolderFileSync(Lr2FolderFileDbSyncRequest request)
    {
        if (request == null)
        {
            throw new ArgumentNullException(nameof(request));
        }

        using (maintenanceWriteGate.GetWriterGuard())
        using (LR2SongDBExtended songDb = dbGateway?.OpenSongDb()
            ?? throw new InvalidOperationException("Catalog mutation owner is not configured with a song database."))
        {
            string savepoint = songDb.SaveTransactionPoint();
            try
            {
                Lr2FolderFileDbSyncResult result = Lr2FolderFileDbSyncService.Sync(
                    songDb,
                    request,
                    commitTransaction: false);
                songDb.Commit();
                return result;
            }
            catch
            {
                RollbackToSavepointBestEffort(songDb, savepoint);
                throw;
            }
        }
    }

    internal Lr2NormalFolderDbSyncResult ApplyLr2NormalFolderSync(Lr2NormalFolderDbSyncRequest request)
    {
        if (request == null)
        {
            throw new ArgumentNullException(nameof(request));
        }

        using (maintenanceWriteGate.GetWriterGuard())
        using (LR2SongDBExtended songDb = dbGateway?.OpenSongDb()
            ?? throw new InvalidOperationException("Catalog mutation owner is not configured with a song database."))
        {
            string savepoint = songDb.SaveTransactionPoint();
            try
            {
                Lr2NormalFolderDbSyncResult result = Lr2NormalFolderDbSyncService.Sync(
                    songDb,
                    request,
                    commitTransaction: false);
                songDb.Commit();
                return result;
            }
            catch
            {
                RollbackToSavepointBestEffort(songDb, savepoint);
                throw;
            }
        }
    }

    internal Lr2SongDbSyncResult ApplyLr2SongDbSync(Lr2SongDbSyncRequest request)
    {
        if (request == null)
        {
            throw new ArgumentNullException(nameof(request));
        }

        using (maintenanceWriteGate.GetWriterGuard())
        using (LR2SongDBExtended songDb = dbGateway?.OpenSongDb()
            ?? throw new InvalidOperationException("Catalog mutation owner is not configured with a song database."))
        {
            return Lr2SongDbSyncService.Run(
                songDb,
                request,
                writeRequest => ApplyChartInfoWriteInTransactionUnderGuard(songDb, writeRequest));
        }
    }

    internal Lr2FolderExistingRowsSnapshot CaptureLr2FolderExistingRows(
        Lr2SongDbSyncRequest request)
    {
        using (maintenanceWriteGate.GetWriterGuard())
        using (LR2SongDBExtended songDb = dbGateway?.OpenSongDb()
            ?? throw new InvalidOperationException("Catalog mutation owner is not configured with a song database."))
        {
            IReadOnlyDictionary<string, LR2SongDB.folder> rowsByPath =
                Lr2SongDbSyncService.CreateExistingLr2FolderRowMap(songDb, request);
            return new Lr2FolderExistingRowsSnapshot(rowsByPath);
        }
    }

    internal Lr2SongDbSyncStatusSnapshot EvaluateLr2SongDbSyncStatus(
        bool enabled,
        string signature,
        DateTime nowUtc)
    {
        using (maintenanceWriteGate.GetWriterGuard())
        using (LR2SongDBExtended songDb = dbGateway?.OpenSongDb()
            ?? throw new InvalidOperationException("Catalog mutation owner is not configured with a song database."))
        {
            return Lr2SongDbSyncStatusService.Evaluate(songDb, enabled, signature, nowUtc);
        }
    }

    internal Lr2SongDbSyncStatusSnapshot ApplyLr2SongDbSyncStatusMutation(
        Lr2SongDbSyncStatusMutationRequest request)
    {
        if (request == null)
        {
            throw new ArgumentNullException(nameof(request));
        }

        using (maintenanceWriteGate.GetWriterGuard())
        using (LR2SongDBExtended songDb = dbGateway?.OpenSongDb()
            ?? throw new InvalidOperationException("Catalog mutation owner is not configured with a song database."))
        {
            return request.Kind switch
            {
                Lr2SongDbSyncStatusMutationKind.MarkIncomplete => Lr2SongDbSyncStatusService.MarkIncomplete(
                    songDb,
                    request.Signature,
                    request.RunId,
                    request.ProcessedCursor,
                    request.TotalCount,
                    request.Stage,
                    request.Detail,
                    request.NowUtc),
                Lr2SongDbSyncStatusMutationKind.MarkFailed => Lr2SongDbSyncStatusService.MarkFailed(
                    songDb,
                    request.Signature,
                    request.RunId,
                    request.ProcessedCursor,
                    request.TotalCount,
                    request.Stage,
                    request.Detail,
                    request.NowUtc),
                _ => throw new ArgumentOutOfRangeException(nameof(request.Kind), request.Kind, "Unknown LR2 song DB status mutation.")
            };
        }
    }

    private static void RollbackToSavepointBestEffort(LR2SongDBExtended songDb, string savepoint)
    {
        try
        {
            songDb.RollbackTo(savepoint);
        }
        catch (Exception rollbackException)
        {
            try
            {
                Debug.WriteLine(
                    "catalog_lr2_sync_rollback_failed"
                    + " exception=" + rollbackException.GetType().Name
                    + " message=" + rollbackException.Message);
            }
            catch
            {
            }
        }
    }

    /// <summary>
    /// Captures LR2 input rows and all freshness versions under the canonical
    /// storage-to-maintenance lock order. The returned object is immutable and
    /// can be used without retaining either catalog owner.
    /// </summary>
    internal Lr2SongDbSyncInputRowSnapshot CaptureLr2SynchronizationInputRowSnapshot()
    {
        using (storageRowsOwner.WriteGate.GetReaderGuard())
        using (maintenanceWriteGate.GetReaderGuard())
        {
            CatalogStorageRowsSnapshot storageSnapshot = storageRowsOwner.CaptureSnapshot();
            var chartPathSet = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var chartPaths = new List<string>();
            var songRows = new List<BMSFile>();
            foreach (BMSFile file in storageSnapshot.BmsRows ?? [])
            {
                if (file == null || string.IsNullOrWhiteSpace(file.path))
                {
                    continue;
                }
                songRows.Add(file);
                if (chartPathSet.Add(file.path))
                {
                    chartPaths.Add(file.path);
                }
            }

            return new Lr2SongDbSyncInputRowSnapshot(
                chartPaths,
                songRows,
                ownedCollectionOwner.CollectionVersion,
                storageSnapshot.BmsRowsVersion,
                storageSnapshot.BmsonRowsVersion);
        }
    }

    internal IReadOnlyList<BMSFile> CaptureLr2SynchronizationBmsFilesSnapshot() =>
        CaptureLr2SynchronizationInputRowSnapshot().SongRows;

    internal StorageRowsVersionSnapshot CaptureLr2SynchronizationStorageRowsVersionSnapshot()
    {
        // CaptureVersionSnapshot is serialized by the storage owner's version
        // gate.  Do not acquire either owner lock here: LR2 sync invokes this
        // freshness probe while holding the maintenance writer, and another
        // maintenance route acquires storage before maintenance.
        return storageRowsOwner.CaptureVersionSnapshot();
    }

    /// <summary>
    /// Captures and validates the relocation facts before the durable catalog transaction.
    /// </summary>
    internal CatalogRelocationRequest CreateRelocationRequest(LibraryMutationDelta delta)
    {
        if (delta == null)
        {
            return new CatalogRelocationRequest([], [], []);
        }

        var folderChanges = new List<CatalogFolderPathReplacement>();
        foreach (LibraryFolderPathChange change in delta.FolderPathChanges ?? [])
        {
            if (change == null
                || string.IsNullOrWhiteSpace(change.OldFolderPath)
                || string.IsNullOrWhiteSpace(change.NewFolderPath))
            {
                continue;
            }
            folderChanges.Add(new CatalogFolderPathReplacement(change.OldFolderPath, change.NewFolderPath));
        }

        var bmsChanges = new List<BmsSongPathReplacement>();
        var bmsonChanges = new List<BmsonSongPathReplacement>();
        var folderParentHashCache = new Lr2SongFolderParentNormalizer.Lr2FolderParentHashCache();
        foreach (LibraryChartPathChange change in delta.ChartPathChanges ?? [])
        {
            BMSFile bmsFile = change?.GetBmsStorageOwner();
            if (bmsFile != null)
            {
                bmsChanges.Add(CreateBmsSongPathReplacement(
                    bmsFile,
                    change.NewPath,
                    change.OldPath,
                    folderParentHashCache));
                continue;
            }

            LR2SongDBExtended.bmson_song bmsonSong = change?.GetBmsonStorageOwner();
            if (bmsonSong != null)
            {
                bmsonChanges.Add(CreateBmsonSongPathReplacement(
                    bmsonSong,
                    change.NewPath,
                    change.OldPath));
            }
        }

        return new CatalogRelocationRequest(
            folderChanges,
            bmsChanges,
            bmsonChanges);
    }

    /// <summary>
    /// Applies one generic catalog mutation command. Relocation and removal rows share
    /// one durable transaction, then the live catalog and owned collection are updated
    /// under the canonical storage-to-maintenance guards.
    /// </summary>
    internal CatalogMutationReceipt ApplyCatalogMutation(
        LibraryMutationDelta delta,
        IEnumerable<OwnedChartRemoveRequest> removeRequests,
        IEnumerable<BMSFile> addedBmsFiles = null,
        IEnumerable<LR2SongDBExtended.bmson_song> addedBmsonSongs = null,
        Action onDurableCommit = null)
    {
        CatalogWriteFailureFact failureFact = null;
        try
        {
            return ApplyCatalogMutationUnderGuards(
                delta,
                removeRequests,
                addedBmsFiles,
                addedBmsonSongs,
                onDurableCommit,
                fact => failureFact = fact);
        }
        catch
        {
            PublishCatalogWriteFailureFactBestEffort(failureFact);
            throw;
        }
    }

    private CatalogMutationReceipt ApplyCatalogMutationUnderGuards(
        LibraryMutationDelta delta,
        IEnumerable<OwnedChartRemoveRequest> removeRequests,
        IEnumerable<BMSFile> addedBmsFiles,
        IEnumerable<LR2SongDBExtended.bmson_song> addedBmsonSongs,
        Action onDurableCommit,
        Action<CatalogWriteFailureFact> captureFailureFact)
    {
        if (delta == null)
        {
            return CatalogMutationReceipt.NotApplied;
        }
        CatalogRelocationRequest relocationRequest;
        CatalogStorageRowsRemovalRequest removalRequest;
        IReadOnlyList<BMSFile> addedBmsRows = [.. (addedBmsFiles ?? [])];
        IReadOnlyList<LR2SongDBExtended.bmson_song> addedBmsonRows = [.. (addedBmsonSongs ?? [])];
        using (storageRowsOwner.WriteGate.GetWriterGuard())
        using (maintenanceWriteGate.GetWriterGuard())
        {
            relocationRequest = CreateRelocationRequest(delta);
            removalRequest = CreateStorageRowsRemovalRequest(removeRequests);
            if (!relocationRequest.HasChanges
                && !removalRequest.HasChanges
                && addedBmsRows.Count == 0
                && addedBmsonRows.Count == 0)
            {
                return CatalogMutationReceipt.NotApplied;
            }
            if (dbGateway == null)
            {
                throw new InvalidOperationException("Catalog mutation owner is not configured with a song database.");
            }

            CatalogRelocationDbReceipt dbResult;
            try
            {
                dbResult = dbGateway.ReplaceAndRemoveLibraryMutationRows(
                    relocationRequest,
                    removalRequest,
                    addedBmsRows,
                    addedBmsonRows);
            }
            catch (Exception ex)
            {
                captureFailureFact?.Invoke(new CatalogWriteFailureFact(
                    runId: "song_db_write",
                    stage: relocationRequest.HasChanges
                        ? "lr2_song_db_library_mutation_path_replace_failed"
                        : "lr2_song_db_library_mutation_removal_failed",
                    logReason: relocationRequest.HasChanges
                        ? "lr2_song_db_library_mutation_path_replace_failed"
                        : "lr2_song_db_library_mutation_removal_failed",
                    ex));
                throw;
            }

            onDurableCommit?.Invoke();

            IReadOnlyList<CatalogRelocationPathFact> protectedPathFacts = CreateProtectedPathFacts(
                relocationRequest,
                removalRequest);
            long liveApplyMs = ApplyRelocationLive(relocationRequest);
            StorageRowsVersionSnapshot storageRowsVersion = storageRowsOwner.ApplyCatalogMutation(
                relocationRequest,
                removalRequest,
                protectedPathFacts,
                addedBmsRows,
                addedBmsonRows);
            IReadOnlyList<CatalogChartMutationFact> addedChartFacts = CatalogChartMutationFact.CreateFacts(
                ChartFileProjection.FromStorageRows(
                    addedBmsRows,
                    addedBmsonRows,
                    includeWarningSnapshot: false,
                    requirePath: false,
                    includeResourceReferences: false,
                    includeScoreSnapshot: false));
            IReadOnlyList<CatalogRelocationPathFact> pathFacts =
            [
                .. relocationRequest.BmsPathReplacements.Select(replacement => new CatalogRelocationPathFact(
                    ChartFileKind.Bms,
                    replacement.OldPath,
                    replacement.Song.path,
                    replacement.Song.hash,
                    replacement.Song.sha256)),
                .. relocationRequest.BmsonPathReplacements.Select(replacement => new CatalogRelocationPathFact(
                    ChartFileKind.Bmson,
                    replacement.OldPath,
                    replacement.Song.path,
                    replacement.Song.md5,
                    replacement.Song.sha256))
            ];
            IReadOnlyList<CatalogChartMutationFact> removedChartFacts =
                CatalogChartMutationFact.CreateRemovalFacts(removalRequest?.RemoveRequests);
            bool ownedCollectionChanged = addedChartFacts.Count > 0
                || removedChartFacts.Count > 0
                || pathFacts.Count > 0;
            bool ownedCollectionApplied = ownedCollectionOwner.ApplyMutation(
                removalRequest?.RemoveRequests,
                delta.ChartPathChanges,
                addedBmsRows,
                addedBmsonRows,
                storageRowsVersion,
                out bool bmsonCanonicalOrderNormalized);
            int ownedCollectionVersion = ownedCollectionChanged
                ? ownedCollectionOwner.IncrementVersion()
                : ownedCollectionOwner.CollectionVersion;
            return new CatalogMutationReceipt(
                applied: true,
                storageRowsVersion,
                dbResult.FolderDbMs,
                dbResult.BmsPathDbMs,
                dbResult.BmsonPathDbMs,
                dbResult.BmsRemovalDbMs,
                dbResult.BmsonRemovalDbMs,
                liveApplyMs,
                ownedCollectionApplied,
                 ownedCollectionVersion,
                 addedChartFacts,
                 pathFacts,
                 removalRequest?.RemoveRequests,
                 bmsonCanonicalOrderNormalized);
        }
    }

    private static IReadOnlyList<CatalogRelocationPathFact> CreateProtectedPathFacts(
        CatalogRelocationRequest relocationRequest,
        CatalogStorageRowsRemovalRequest removalRequest)
    {
        var removedBmsOwners = new HashSet<BMSFile>(removalRequest?.RemovedBmsRows ?? []);
        var removedBmsonOwners = new HashSet<LR2SongDBExtended.bmson_song>(
            removalRequest?.RemovedBmsonRows ?? []);
        return [
            .. (relocationRequest?.BmsPathReplacements ?? [])
                .Where(replacement => replacement?.Song != null
                    && !removedBmsOwners.Contains(replacement.LiveOwner))
                .Select(replacement => new CatalogRelocationPathFact(
                    ChartFileKind.Bms,
                    replacement.OldPath,
                    replacement.Song.path)),
            .. (relocationRequest?.BmsonPathReplacements ?? [])
                .Where(replacement => replacement?.Song != null
                    && !removedBmsonOwners.Contains(replacement.LiveOwner))
                .Select(replacement => new CatalogRelocationPathFact(
                    ChartFileKind.Bmson,
                    replacement.OldPath,
                    replacement.Song.path))
        ];
    }

    private static long ApplyRelocationLive(CatalogRelocationRequest request)
    {
        if (request == null || !request.HasChanges)
        {
            return 0;
        }

        Stopwatch stopwatch = Stopwatch.StartNew();
        foreach (BmsSongPathReplacement replacement in request.BmsPathReplacements)
        {
            ApplyBmsFilePathInMemory(replacement);
        }
        foreach (BmsonSongPathReplacement replacement in request.BmsonPathReplacements)
        {
            ApplyBmsonSongPathInMemory(replacement);
        }
        stopwatch.Stop();
        return stopwatch.ElapsedMilliseconds;
    }

    private static BmsSongPathReplacement CreateBmsSongPathReplacement(
        BMSFile bmsFile,
        string newPath,
        string oldPath,
        Lr2SongFolderParentNormalizer.Lr2FolderParentHashCache folderParentHashCache)
    {
        ValidateBmsFilePathChange(bmsFile, newPath, oldPath);
        BMSFile copy = bmsFile.CreateSongRowPersistenceCopy();
        copy.path = newPath;
        copy.SetTextGroupFlag(Lr2TextGroupResolver.ResolveFlag(newPath, bmsFile.txt.GetValueOrDefault()));
        copy.folder = null;
        copy.parent = null;
        Lr2SongRowEnricher.EnrichGeneratedSong(copy, folderParentHashCache);
        BMSFileMaintenanceInfo maintenanceInfo = bmsFile.HasValidMaintenanceInfoSnapshot
            ? bmsFile.TryGetMaintenanceInfoWithoutCreating()?.CreatePersistenceCopy(newPath, bmsFile.hash)
            : null;
        RefreshRelocatedBmsMaintenanceInfo(maintenanceInfo, newPath);
        return new BmsSongPathReplacement(
            copy,
            bmsFile,
            string.IsNullOrWhiteSpace(oldPath) ? bmsFile.path : oldPath,
            maintenanceInfo);
    }

    private static BmsonSongPathReplacement CreateBmsonSongPathReplacement(
        LR2SongDBExtended.bmson_song bmsonSong,
        string newPath,
        string oldPath)
    {
        ValidateBmsonSongPathChange(bmsonSong, newPath, oldPath);
        LR2SongDBExtended.bmson_song copy = CreateBmsonSongPersistenceCopy(bmsonSong);
        copy.path = newPath;
        copy.folder = Path.GetDirectoryName(newPath) ?? string.Empty;
        copy.MaintenanceInfo = bmsonSong.MaintenanceInfo?.CreatePersistenceCopy();
        copy.MaintenanceInfo?.NormalizeForBmson(copy.path, copy.md5);
        return new BmsonSongPathReplacement(
            copy,
            bmsonSong,
            string.IsNullOrWhiteSpace(oldPath) ? bmsonSong.path : oldPath);
    }

    private static void ApplyBmsFilePathInMemory(BmsSongPathReplacement replacement)
    {
        BMSFile bmsFile = replacement.LiveOwner;
        bmsFile.path = replacement.Song.path;
        bmsFile.SetTextGroupFlag(replacement.Song.txt.GetValueOrDefault());
        bmsFile.folder = replacement.Song.folder;
        bmsFile.parent = replacement.Song.parent;
        Lr2SongRowEnricher.EnrichGeneratedSong(bmsFile);
        if (replacement.MaintenanceInfo != null)
        {
            bmsFile.SetMaintenanceInfo(
                replacement.MaintenanceInfo.CreatePersistenceCopy(bmsFile.path, bmsFile.hash),
                suppressPropertyChanged: true,
                origin: MaintenanceInfoOrigin.Calculated);
        }
    }

    private static void ApplyBmsonSongPathInMemory(BmsonSongPathReplacement replacement)
    {
        LR2SongDBExtended.bmson_song bmsonSong = replacement.LiveOwner;
        bmsonSong.path = replacement.Song.path;
        bmsonSong.folder = replacement.Song.folder;
        bmsonSong.MaintenanceInfo = replacement.Song.MaintenanceInfo?.CreatePersistenceCopy();
        bmsonSong.MaintenanceInfo?.NormalizeForBmson(bmsonSong.path, bmsonSong.md5);
    }

    private static void RefreshRelocatedBmsMaintenanceInfo(BMSFileMaintenanceInfo maintenanceInfo, string newPath)
    {
        Lr2CompatibilityEvaluator.RefreshRelocatedMaintenanceFacts(
            maintenanceInfo,
            newPath,
            () => ChartFileContentReader.ReadSnapshot(newPath));
    }

    private static void ValidateBmsFilePathChange(BMSFile bmsFile, string newPath, string oldPath)
    {
        if (bmsFile == null)
        {
            throw new ArgumentNullException(nameof(bmsFile));
        }
        if (newPath == null)
        {
            throw new ArgumentNullException(nameof(newPath));
        }
        if (!File.Exists(newPath))
        {
            throw new FileNotFoundException(Resources.Error_RenameDestFileNotFound, newPath);
        }
        if (!string.IsNullOrWhiteSpace(oldPath)
            && !string.Equals(bmsFile.path, oldPath, StringComparison.Ordinal)
            && !string.Equals(bmsFile.path, newPath, StringComparison.Ordinal))
        {
            throw new InvalidCastException(Resources.Error_OldPathMismatch);
        }
    }

    private static void ValidateBmsonSongPathChange(
        LR2SongDBExtended.bmson_song bmsonSong,
        string newPath,
        string oldPath)
    {
        if (bmsonSong == null)
        {
            throw new ArgumentNullException(nameof(bmsonSong));
        }
        if (newPath == null)
        {
            throw new ArgumentNullException(nameof(newPath));
        }
        if (!File.Exists(newPath))
        {
            throw new FileNotFoundException(Resources.Error_RenameDestFileNotFound, newPath);
        }
        if (!string.IsNullOrWhiteSpace(oldPath)
            && !string.Equals(bmsonSong.path, oldPath, StringComparison.Ordinal)
            && !string.Equals(bmsonSong.path, newPath, StringComparison.Ordinal))
        {
            throw new InvalidCastException(Resources.Error_OldPathMismatch);
        }
    }

    private static LR2SongDBExtended.bmson_song CreateBmsonSongPersistenceCopy(
        LR2SongDBExtended.bmson_song source)
    {
        return new LR2SongDBExtended.bmson_song
        {
            path = source.path,
            folder = source.folder,
            title = source.title,
            subtitle = source.subtitle,
            artist = source.artist,
            genre = source.genre,
            level = source.level,
            mode_hint = source.mode_hint,
            md5 = source.md5,
            sha256 = source.sha256,
            banner = source.banner,
            backbmp = source.backbmp,
            stagefile = source.stagefile,
            preview_music = source.preview_music,
            updated_at = source.updated_at
        };
    }

    /// <summary>
    /// 選択された kind の raw storage replacement request を作成します。
    /// 明示 replacement は入力 view の参照同一性に依存せず publication 対象になります。
    /// </summary>
    /// <param name="bmsRows">BMS replacement input。</param>
    /// <param name="bmsonRows">BMSON replacement input。</param>
    /// <param name="replaceBmsRows">BMS を置換するかどうか。</param>
    /// <param name="replaceBmsonRows">BMSON を置換するかどうか。</param>
    internal CatalogStorageRowsReplacementRequest CreateStorageRowsReplacementRequest(
        IEnumerable<BMSFile> bmsRows,
        IEnumerable<LR2SongDBExtended.bmson_song> bmsonRows,
        bool replaceBmsRows,
        bool replaceBmsonRows)
    {
        using (storageRowsOwner.WriteGate.GetWriterGuard())
        {
            return new CatalogStorageRowsReplacementRequest(
                bmsRows,
                bmsonRows,
                replaceBmsRows,
                replaceBmsonRows,
                replaceBmsRows,
                replaceBmsonRows);
        }
    }

    /// <summary>storage replacement を適用し、対象 kind の version/publication facts を返します。</summary>
    /// <param name="request">適用する immutable replacement request。</param>
    internal CatalogStorageRowsReplacementReceipt ApplyStorageRowsReplacement(
        CatalogStorageRowsReplacementRequest request)
    {
        if (request == null)
        {
            return CatalogStorageRowsReplacementReceipt.NotApplied;
        }

        using (storageRowsOwner.WriteGate.GetWriterGuard())
        {
            StorageRowsVersionSnapshot previousVersions = storageRowsOwner.CaptureVersionSnapshot();
            if (request.ReplaceBmsRows && request.BmsRowsChanged)
            {
                storageRowsOwner.ReplaceBmsRows(request.BmsRows);
            }
            if (request.ReplaceBmsonRows && request.BmsonRowsChanged)
            {
                storageRowsOwner.ReplaceBmsonRows(request.BmsonRows);
            }
            bool applied = request.BmsRowsChanged || request.BmsonRowsChanged;
            if (applied)
            {
                ownedCollectionOwner.Invalidate();
            }
            int ownedCollectionVersion = applied
                ? ownedCollectionOwner.IncrementVersion()
                : ownedCollectionOwner.CollectionVersion;
            StorageRowsVersionSnapshot currentVersions = storageRowsOwner.CaptureVersionSnapshot();
            return new CatalogStorageRowsReplacementReceipt(
                applied,
                request.BmsRowsChanged,
                request.BmsonRowsChanged,
                ownedCollectionInvalidated: applied,
                new StorageRowsVersionSnapshot(
                    previousVersions.BmsRowsVersion,
                    previousVersions.BmsonRowsVersion,
                    currentVersions.BmsRowsVersion,
                    currentVersions.BmsonRowsVersion),
                ownedCollectionVersion);
        }
    }

    internal CatalogStorageRowsRemovalRequest CreateStorageRowsRemovalRequest(
        IEnumerable<OwnedChartRemoveRequest> removeRequests)
    {
        return new CatalogStorageRowsRemovalRequest(removeRequests);
    }

    internal CatalogFileScanStorageReplacementRequest CreateFileScanStorageReplacementRequest(
        bool hasDbDiff,
        IEnumerable<BMSFile> nextBmsRows,
        IEnumerable<LR2SongDBExtended.bmson_song> nextBmsonRows,
        IEnumerable<string> deletedBmsPaths,
        IEnumerable<string> deletedBmsonPaths,
        IEnumerable<BMSFile> addedBmsFiles,
        IEnumerable<LR2SongDBExtended.bmson_song> addedBmsonSongs)
    {
        using (storageRowsOwner.WriteGate.GetReaderGuard())
        {
            return CreateFileScanStorageReplacementRequestUnsafe(
                hasDbDiff,
                nextBmsRows,
                nextBmsonRows,
                deletedBmsPaths,
                deletedBmsonPaths,
                addedBmsFiles,
                addedBmsonSongs);
        }
    }

    /// <summary>
    /// Applies a file-scan catalog replacement and captures its committed owned
    /// collection version for post-lease consumers.
    /// </summary>
    /// <param name="request">The immutable replacement request, or <see langword="null"/> when no replacement is applied.</param>
    internal CatalogFileScanStorageReplacementReceipt ApplyFileScanStorageReplacement(
        CatalogFileScanStorageReplacementRequest request)
    {
        if (request == null)
        {
            return CatalogFileScanStorageReplacementReceipt.NotApplied;
        }

        using (storageRowsOwner.WriteGate.GetWriterGuard())
        {
            StorageRowsVersionSnapshot previousVersions = storageRowsOwner.CaptureVersionSnapshot();
            CatalogStorageRowsSnapshot storageRows = request.HasDbDiff
                ? storageRowsOwner.ReplaceRowsAndCaptureSnapshot(
                    request.NextBmsRows,
                    request.NextBmsonRows)
                : null;
            CatalogOwnedCollectionReplacementResult ownedReplacement = request.HasDbDiff
                ? ownedCollectionOwner.ReplaceForFileScan(storageRows)
                : CatalogOwnedCollectionReplacementResult.NotApplied;
            // The receipt is captured at this commit boundary; post-lease
            // publication must observe this version without advancing again.
            int ownedCollectionVersion = request.HasDbDiff
                ? ownedCollectionOwner.IncrementVersion()
                : ownedCollectionOwner.CollectionVersion;
            StorageRowsVersionSnapshot versions = new(
                previousVersions.BmsRowsVersion,
                previousVersions.BmsonRowsVersion,
                storageRows?.BmsRowsVersion ?? previousVersions.BmsRowsVersion,
                storageRows?.BmsonRowsVersion ?? previousVersions.BmsonRowsVersion);
            return new CatalogFileScanStorageReplacementReceipt(
                applied: request.HasDbDiff,
                ownedCollectionApplied: ownedReplacement.Applied,
                versions,
                ownedCollectionVersion,
                ownedReplacement.FilterSummary,
                request.AddedCharts,
                request.RemovedCharts,
                movedCharts: []);
        }
    }

    private CatalogFileScanStorageReplacementRequest CreateFileScanStorageReplacementRequestUnsafe(
        bool hasDbDiff,
        IEnumerable<BMSFile> nextBmsRows,
        IEnumerable<LR2SongDBExtended.bmson_song> nextBmsonRows,
        IEnumerable<string> deletedBmsPaths,
        IEnumerable<string> deletedBmsonPaths,
        IEnumerable<BMSFile> addedBmsFiles,
        IEnumerable<LR2SongDBExtended.bmson_song> addedBmsonSongs)
    {
        bool removedPayloadAvailable = ownedCollectionOwner.TryCreateFileScanRemovedStorageOwnerIdentityCharts(
            [.. deletedBmsPaths ?? []],
            [.. deletedBmsonPaths ?? []],
            [.. nextBmsRows ?? []],
            [.. nextBmsonRows ?? []],
            out List<ChartFile> removedCharts);
        return new CatalogFileScanStorageReplacementRequest(
            hasDbDiff,
            nextBmsRows,
            nextBmsonRows,
            deletedBmsPaths,
            deletedBmsonPaths,
            addedBmsFiles,
            addedBmsonSongs,
            removedPayloadAvailable,
            removedCharts);
    }

    internal CatalogInstalledTargetUpsertRequest CreateInstalledTargetUpsertRequest(
        IEnumerable<BMSFile> bmsRows,
        IEnumerable<LR2SongDBExtended.bmson_song> bmsonRows)
    {
        using (storageRowsOwner.WriteGate.GetWriterGuard())
        {
            return CreateInstalledTargetUpsertRequestUnsafe(bmsRows, bmsonRows);
        }
    }

    /// <summary>
    /// installed targetをDB durable後にlive ownerとcatalogへ適用し、failure factの公開を呼出し側へ遅延します。
    /// </summary>
    /// <param name="targets">確定destinationとlive ownerを保持する導入target。</param>
    /// <param name="failureFact">DB書込み失敗のimmutable fact。</param>
    /// <param name="onValidationPassed">DB validation通過時に一度だけ呼ぶcallback。</param>
    /// <param name="installPathToDelete">同じtransactionで削除するpending install rowのpath。</param>
    /// <returns>DB・storage・owned collectionの確定receipt。</returns>
    internal CatalogInstalledTargetUpsertReceipt ApplyInstalledTargetUpsertWithDeferredFailurePublication(
        ChartStorageTargetSet targets,
        out CatalogWriteFailureFact failureFact,
        Action onValidationPassed = null,
        string installPathToDelete = null)
    {
        if (targets == null)
        {
            failureFact = null;
            return CatalogInstalledTargetUpsertReceipt.NotApplied;
        }

        CatalogWriteFailureFact capturedFailureFact = null;
        try
        {
            using (storageRowsOwner.WriteGate.GetWriterGuard())
            using (maintenanceWriteGate.GetWriterGuard())
            {
                CatalogInstalledTargetUpsertReceipt receipt = ApplyInstalledTargetUpsertUnsafe(
                    CreateInstalledTargetUpsertRequestUnsafe(targets, installPathToDelete),
                    onValidationPassed,
                    fact => capturedFailureFact = fact);
                failureFact = null;
                return receipt;
            }
        }
        catch
        {
            failureFact = capturedFailureFact;
            throw;
        }
    }

    /// <summary>immutable requestを適用します。</summary>
    /// <param name="request">事前にversionとrowを固定したrequest。</param>
    /// <param name="onValidationPassed">DB validation通過時に一度だけ呼ぶcallback。</param>
    /// <returns>DB・storage・owned collectionの確定receipt。</returns>
    internal CatalogInstalledTargetUpsertReceipt ApplyInstalledTargetUpsert(
        CatalogInstalledTargetUpsertRequest request,
        Action onValidationPassed = null)
    {
        if (request == null)
        {
            return CatalogInstalledTargetUpsertReceipt.NotApplied;
        }

        CatalogWriteFailureFact failureFact = null;
        try
        {
            using (storageRowsOwner.WriteGate.GetWriterGuard())
            using (maintenanceWriteGate.GetWriterGuard())
            {
                return ApplyInstalledTargetUpsertUnsafe(request, onValidationPassed, fact => failureFact = fact);
            }
        }
        catch
        {
            PublishCatalogWriteFailureFactBestEffort(failureFact);
            throw;
        }
    }

    internal void PublishCatalogWriteFailureFactBestEffort(CatalogWriteFailureFact failureFact)
    {
        if (failureFact == null)
        {
            return;
        }

        InvokeFailureFactPublisher(this, CatalogWriteFailurePublished, failureFact);
    }

    private static void InvokeFailureFactPublisher(
        object sender,
        EventHandler<CatalogWriteFailureFact> publisher,
        CatalogWriteFailureFact failureFact)
    {
        if (publisher == null)
        {
            return;
        }

        try
        {
            publisher(sender, failureFact);
        }
        catch (Exception exception)
        {
            try
            {
                Debug.WriteLine(
                    "catalog_write_failure_fact_publish_failed"
                    + " exception=" + exception.GetType().Name
                    + " message=" + exception.Message);
            }
            catch
            {
                // Failure publication must never replace the original catalog exception.
            }
        }
    }

    private CatalogInstalledTargetUpsertRequest CreateInstalledTargetUpsertRequestUnsafe(
        ChartStorageTargetSet targets,
        string installPathToDelete = null)
    {
        StorageRowsVersionSnapshot currentVersions = storageRowsOwner.CaptureVersionSnapshot();
        return new CatalogInstalledTargetUpsertRequest(
            targets,
            currentVersions.BmsRowsVersion,
            currentVersions.BmsonRowsVersion,
            installPathToDelete);
    }

    private CatalogInstalledTargetUpsertRequest CreateInstalledTargetUpsertRequestUnsafe(
        IEnumerable<BMSFile> bmsRows,
        IEnumerable<LR2SongDBExtended.bmson_song> bmsonRows,
        string installPathToDelete = null)
    {
        StorageRowsVersionSnapshot currentVersions = storageRowsOwner.CaptureVersionSnapshot();
        return new CatalogInstalledTargetUpsertRequest(
            bmsRows,
            bmsonRows,
            currentVersions.BmsRowsVersion,
            currentVersions.BmsonRowsVersion,
            installPathToDelete);
    }

    private CatalogInstalledTargetUpsertReceipt ApplyInstalledTargetUpsertUnsafe(
        CatalogInstalledTargetUpsertRequest request,
        Action onValidationPassed = null,
        Action<CatalogWriteFailureFact> captureFailureFact = null)
    {
        StorageRowsVersionSnapshot currentVersions = storageRowsOwner.CaptureVersionSnapshot();
        if (currentVersions.BmsRowsVersion != request.PreviousBmsRowsVersion
            || currentVersions.BmsonRowsVersion != request.PreviousBmsonRowsVersion)
        {
            throw new InvalidOperationException("Catalog storage rows changed before installed target upsert.");
        }

        ChartStorageTargetSet targets = request.Targets;
        bool hasInstallRowDeletion = !string.IsNullOrWhiteSpace(request.InstallPathToDelete);
        if (targets == null
            || (targets.DatabaseBmsFiles.Count == 0
                && targets.DatabaseBmsonSongs.Count == 0
                && !hasInstallRowDeletion))
        {
            return CatalogInstalledTargetUpsertReceipt.NotApplied;
        }
        if (dbGateway == null)
        {
            throw new InvalidOperationException("Catalog mutation owner is not configured with a song database.");
        }

        ownedCollectionOwner.ValidateStorageRowUpsert(
            targets.DatabaseBmsFiles,
            targets.DatabaseBmsonSongs);
        onValidationPassed?.Invoke();

        try
        {
            dbGateway.ExecuteSongDbTransaction(songDb =>
            {
                if (targets.DatabaseBmsFiles.Count > 0)
                {
                    BmsLibraryDbGateway.EnsureBmsonSchema(songDb);
                    BmsLibraryDbGateway.EnsureSongLookupIndexes(songDb);
                    foreach (BMSFile bmsFile in targets.DatabaseBmsFiles)
                    {
                        Lr2SongDbWriter.UpsertGeneratedSong(songDb, bmsFile);
                    }
                }
                if (targets.DatabaseBmsonSongs.Count > 0)
                {
                    BmsLibraryDbGateway.EnsureBmsonSchema(songDb);
                    foreach (LR2SongDBExtended.bmson_song bmsonSong in targets.DatabaseBmsonSongs)
                    {
                        songDb.InsertOrReplace(bmsonSong, typeof(LR2SongDBExtended.bmson_song));
                    }
                }
                if (hasInstallRowDeletion)
                {
                    songDb.Delete<LR2SongDBExtended.install>(request.InstallPathToDelete);
                }
            });
        }
        catch (Exception ex)
        {
            captureFailureFact?.Invoke(new CatalogWriteFailureFact(
                runId: "song_db_write",
                stage: "lr2_song_db_install_target_upsert_failed",
                logReason: "lr2_song_db_install_target_upsert_failed",
                ex));
            throw;
        }

        // DB is durable at this boundary.  Only now can the canonical owner
        // move to the preflight destination and be published to storage views.
        targets.ApplyInstalledOwnerPaths();
        bool hasOwnedStorageTargets = targets.BmsFiles.Count > 0 || targets.BmsonSongs.Count > 0;
        StorageRowsVersionSnapshot versions = hasOwnedStorageTargets
            ? storageRowsOwner.ApplyInstalledTargets(targets)
            : storageRowsOwner.CaptureVersionSnapshot();
        bool ownedCollectionApplied = false;
        bool bmsonCanonicalOrderNormalized = false;
        if (hasOwnedStorageTargets)
        {
            ownedCollectionApplied = ownedCollectionOwner.ApplyMutation(
                [],
                [],
                targets.BmsFiles,
                targets.BmsonSongs,
                versions,
                out bmsonCanonicalOrderNormalized);
        }
        int ownedCollectionVersion = ownedCollectionApplied
            ? ownedCollectionOwner.IncrementVersion()
            : ownedCollectionOwner.CollectionVersion;
        return new CatalogInstalledTargetUpsertReceipt(
            applied: targets.DatabaseBmsFiles.Count > 0 || targets.DatabaseBmsonSongs.Count > 0 || hasInstallRowDeletion,
            ownedCollectionApplied,
            versions,
            ownedCollectionVersion,
            request.AddedCharts,
            installRowDeleted: hasInstallRowDeletion,
            bmsonCanonicalOrderNormalized: bmsonCanonicalOrderNormalized);
    }

    internal CatalogDigestMutationRequest CreateDigestMutationRequest(
        IEnumerable<LibraryChartDigestChange> digestChanges)
    {
        return new CatalogDigestMutationRequest(digestChanges);
    }

    internal CatalogDigestMutationReceipt ApplyDigestMutation(
        CatalogDigestMutationRequest request)
    {
        if (request == null)
        {
            return CatalogDigestMutationReceipt.NotApplied;
        }

        using (storageRowsOwner.WriteGate.GetWriterGuard())
        {
            bool ownedCollectionApplied = ownedCollectionOwner.ApplyDigestChanges(request.DigestChanges);
            return new CatalogDigestMutationReceipt(
                applied: request.DigestChanges.Count > 0,
                ownedCollectionApplied,
                storageRowsOwner.CaptureVersionSnapshot(),
                ownedCollectionOwner.CollectionVersion,
                request.DigestChanges);
        }
    }
}

internal enum CatalogMutationApplyKind
{
    NoOp,
    GenericMutation,
    StorageRowsReplacement,
    FileScanStorageReplacement,
    InstalledTargetUpsert,
    DigestMutation
}

/// <summary>
/// owner参照と未加工の旧exact path集合を保持する、catalog行削除の入力snapshot。
/// </summary>
internal sealed class CatalogStorageRowsRemovalRequest
{
    internal CatalogStorageRowsRemovalRequest(IEnumerable<OwnedChartRemoveRequest> removeRequests)
    {
        List<OwnedChartRemoveRequest> requests = [.. (removeRequests ?? []).Where(request => request != null)];
        RemoveRequests = Snapshot(requests);
        RemovedBmsRows = Snapshot(requests
            .Select(request => request.BmsOwner)
            .Where(file => file != null)
            .Distinct());
        BmsPathCleanupKeys = CreatePathCleanupKeys(requests, ChartFileKind.Bms);
        RemovedBmsonRows = Snapshot(requests
            .Select(request => request.BmsonOwner)
            .Where(song => song != null)
            .Distinct());
        BmsonPathCleanupKeys = CreatePathCleanupKeys(requests, ChartFileKind.Bmson);
    }

    internal IReadOnlyList<OwnedChartRemoveRequest> RemoveRequests { get; }

    internal IReadOnlyList<BMSFile> RemovedBmsRows { get; }

    /// <summary>明示的に削除対象とされたBMS行の未加工exact key。</summary>
    internal IReadOnlyList<string> BmsPathCleanupKeys { get; }

    internal IReadOnlyList<LR2SongDBExtended.bmson_song> RemovedBmsonRows { get; }

    /// <summary>明示的に削除対象とされたBMSON行の未加工exact key。</summary>
    internal IReadOnlyList<string> BmsonPathCleanupKeys { get; }

    internal bool HasChanges => RemovedBmsRows.Count > 0
        || BmsPathCleanupKeys.Count > 0
        || RemovedBmsonRows.Count > 0
        || BmsonPathCleanupKeys.Count > 0;

    private static IReadOnlyList<string> CreatePathCleanupKeys(
        IEnumerable<OwnedChartRemoveRequest> requests,
        ChartFileKind kind)
    {
        return Snapshot(requests
            .Where(request => request.Mode == OwnedChartRemoveMode.PathCleanup && request.Kind == kind)
            .Select(request => request.Path)
            .Where(path => !string.IsNullOrWhiteSpace(path))
            .Distinct(StringComparer.Ordinal));
    }

    private static IReadOnlyList<T> Snapshot<T>(IEnumerable<T> values)
    {
        return Array.AsReadOnly([.. values ?? []]);
    }
}

/// <summary>
/// Immutable input snapshot for a selected catalog storage-row replacement.
/// </summary>
internal sealed class CatalogStorageRowsReplacementRequest
{
    /// <summary>selected kind の replacement input と publication facts を固定します。</summary>
    /// <param name="bmsRows">BMS replacement input。</param>
    /// <param name="bmsonRows">BMSON replacement input。</param>
    /// <param name="replaceBmsRows">BMS を置換するかどうか。</param>
    /// <param name="replaceBmsonRows">BMSON を置換するかどうか。</param>
    /// <param name="bmsRowsChanged">BMS replacement を publication するかどうか。</param>
    /// <param name="bmsonRowsChanged">BMSON replacement を publication するかどうか。</param>
    internal CatalogStorageRowsReplacementRequest(
        IEnumerable<BMSFile> bmsRows,
        IEnumerable<LR2SongDBExtended.bmson_song> bmsonRows,
        bool replaceBmsRows,
        bool replaceBmsonRows,
        bool bmsRowsChanged,
        bool bmsonRowsChanged)
    {
        ReplaceBmsRows = replaceBmsRows;
        ReplaceBmsonRows = replaceBmsonRows;
        BmsRows = Snapshot(replaceBmsRows ? bmsRows : null);
        BmsonRows = Snapshot(replaceBmsonRows ? bmsonRows : null);
        BmsRowsChanged = replaceBmsRows && bmsRowsChanged;
        BmsonRowsChanged = replaceBmsonRows && bmsonRowsChanged;
    }

    /// <summary>immutable BMS replacement input。</summary>
    internal IReadOnlyList<BMSFile> BmsRows { get; }

    /// <summary>immutable BMSON replacement input。</summary>
    internal IReadOnlyList<LR2SongDBExtended.bmson_song> BmsonRows { get; }

    /// <summary>BMS replacement が選択されたかどうか。</summary>
    internal bool ReplaceBmsRows { get; }

    /// <summary>BMSON replacement が選択されたかどうか。</summary>
    internal bool ReplaceBmsonRows { get; }

    /// <summary>BMS replacement が publication 対象かどうか。</summary>
    internal bool BmsRowsChanged { get; }

    /// <summary>BMSON replacement が publication 対象かどうか。</summary>
    internal bool BmsonRowsChanged { get; }

    private static IReadOnlyList<T> Snapshot<T>(IEnumerable<T> values)
    {
        return Array.AsReadOnly([.. values ?? []]);
    }
}

/// <summary>
/// Canonical facts emitted after a catalog storage-row replacement.
/// </summary>
internal sealed class CatalogStorageRowsReplacementReceipt
{
    internal static CatalogStorageRowsReplacementReceipt NotApplied { get; } =
        new(
            applied: false,
            bmsRowsChanged: false,
            bmsonRowsChanged: false,
            ownedCollectionInvalidated: false,
            default,
            ownedCollectionVersion: 0);

    internal CatalogStorageRowsReplacementReceipt(
        bool applied,
        bool bmsRowsChanged,
        bool bmsonRowsChanged,
        bool ownedCollectionInvalidated,
        StorageRowsVersionSnapshot storageRowsVersion,
        int ownedCollectionVersion)
    {
        Applied = applied;
        Kind = applied
            ? CatalogMutationApplyKind.StorageRowsReplacement
            : CatalogMutationApplyKind.NoOp;
        BmsRowsChanged = bmsRowsChanged;
        BmsonRowsChanged = bmsonRowsChanged;
        OwnedCollectionInvalidated = ownedCollectionInvalidated;
        StorageRowsVersion = storageRowsVersion;
        OwnedCollectionVersion = ownedCollectionVersion;
    }

    internal bool Applied { get; }

    internal CatalogMutationApplyKind Kind { get; }

    internal bool BmsRowsChanged { get; }

    internal bool BmsonRowsChanged { get; }

    internal bool OwnedCollectionInvalidated { get; }

    internal StorageRowsVersionSnapshot StorageRowsVersion { get; }

    internal int OwnedCollectionVersion { get; }
}

/// <summary>
/// Immutable input snapshot for an installed-target catalog upsert.
/// </summary>
internal sealed class CatalogInstalledTargetUpsertRequest
{
    internal CatalogInstalledTargetUpsertRequest(
        IEnumerable<BMSFile> bmsRows,
        IEnumerable<LR2SongDBExtended.bmson_song> bmsonRows,
        int previousBmsRowsVersion,
        int previousBmsonRowsVersion,
        string installPathToDelete = null)
    {
        Targets = ChartStorageTargetSet.FromRows(bmsRows, bmsonRows);
        BmsRows = Snapshot(Targets.DatabaseBmsFiles);
        BmsonRows = Snapshot(Targets.DatabaseBmsonSongs);
        PreviousBmsRowsVersion = previousBmsRowsVersion;
        PreviousBmsonRowsVersion = previousBmsonRowsVersion;
        InstallPathToDelete = installPathToDelete;
        AddedCharts = CatalogChartMutationFact.CreateFacts(Targets.Charts);
    }

    /// <summary>
    /// 確定destinationを持つtargetをrequestへ固定します。
    /// </summary>
    /// <param name="targets">live ownerとDB用detached rowを保持する導入target。</param>
    /// <param name="previousBmsRowsVersion">request作成時のBMS storage version。</param>
    /// <param name="previousBmsonRowsVersion">request作成時のBMSON storage version。</param>
    /// <param name="installPathToDelete">同じtransactionで削除するpending install rowのpath。</param>
    internal CatalogInstalledTargetUpsertRequest(
        ChartStorageTargetSet targets,
        int previousBmsRowsVersion,
        int previousBmsonRowsVersion,
        string installPathToDelete = null)
    {
        Targets = targets ?? ChartStorageTargetSet.FromRows([], []);
        BmsRows = Targets.DatabaseBmsFiles;
        BmsonRows = Targets.DatabaseBmsonSongs;
        PreviousBmsRowsVersion = previousBmsRowsVersion;
        PreviousBmsonRowsVersion = previousBmsonRowsVersion;
        InstallPathToDelete = installPathToDelete;
        AddedCharts = CatalogChartMutationFact.CreateFacts(Targets.Charts);
    }

    /// <summary>live owner、detached DB row、確定destinationを束ねた内部target。</summary>
    internal ChartStorageTargetSet Targets { get; }

    internal IReadOnlyList<BMSFile> BmsRows { get; }

    internal IReadOnlyList<LR2SongDBExtended.bmson_song> BmsonRows { get; }

    internal int PreviousBmsRowsVersion { get; }

    internal int PreviousBmsonRowsVersion { get; }

    internal string InstallPathToDelete { get; }

    internal IReadOnlyList<CatalogChartMutationFact> AddedCharts { get; }

    private static IReadOnlyList<T> Snapshot<T>(IEnumerable<T> values)
    {
        return Array.AsReadOnly([.. values ?? []]);
    }
}

/// <summary>
/// Canonical facts emitted after an installed-target catalog upsert.
/// </summary>
internal sealed class CatalogInstalledTargetUpsertReceipt
{
    internal static CatalogInstalledTargetUpsertReceipt NotApplied { get; } =
        new(
            applied: false,
            ownedCollectionApplied: false,
            default,
            ownedCollectionVersion: 0,
            [],
            bmsonCanonicalOrderNormalized: false);

    internal CatalogInstalledTargetUpsertReceipt(
        bool applied,
        bool ownedCollectionApplied,
        StorageRowsVersionSnapshot storageRowsVersion,
        int ownedCollectionVersion,
        IEnumerable<CatalogChartMutationFact> addedCharts,
        bool installRowDeleted = false,
        bool bmsonCanonicalOrderNormalized = false)
    {
        Applied = applied;
        Kind = applied
            ? CatalogMutationApplyKind.InstalledTargetUpsert
            : CatalogMutationApplyKind.NoOp;
        OwnedCollectionApplied = ownedCollectionApplied;
        StorageRowsVersion = storageRowsVersion;
        OwnedCollectionVersion = ownedCollectionVersion;
        AddedCharts = Array.AsReadOnly([.. addedCharts ?? []]);
        InstallRowDeleted = installRowDeleted;
        BmsonCanonicalOrderNormalized = bmsonCanonicalOrderNormalized;
    }

    internal bool Applied { get; }

    internal CatalogMutationApplyKind Kind { get; }

    internal bool OwnedCollectionApplied { get; }

    internal StorageRowsVersionSnapshot StorageRowsVersion { get; }

    internal int OwnedCollectionVersion { get; }

    internal IReadOnlyList<CatalogChartMutationFact> AddedCharts { get; }

    internal bool InstallRowDeleted { get; }

    /// <summary>今回のupsertで初回BMSON canonical順序正規化が発生したか。</summary>
    internal bool BmsonCanonicalOrderNormalized { get; }
}

/// <summary>
/// Immutable input snapshot for a catalog digest mutation.
/// </summary>
internal sealed class CatalogDigestMutationRequest
{
    internal CatalogDigestMutationRequest(IEnumerable<LibraryChartDigestChange> digestChanges)
    {
        DigestChanges = Array.AsReadOnly((digestChanges ?? [])
            .Where(change => change?.HasDigestChange == true)
            .ToArray());
    }

    internal IReadOnlyList<LibraryChartDigestChange> DigestChanges { get; }
}

/// <summary>
/// Canonical facts emitted after a catalog digest mutation.
/// </summary>
internal sealed class CatalogDigestMutationReceipt
{
    internal static CatalogDigestMutationReceipt NotApplied { get; } =
        new(
            applied: false,
            ownedCollectionApplied: false,
            default,
            ownedCollectionVersion: 0,
            []);

    internal CatalogDigestMutationReceipt(
        bool applied,
        bool ownedCollectionApplied,
        StorageRowsVersionSnapshot storageRowsVersion,
        int ownedCollectionVersion,
        IEnumerable<LibraryChartDigestChange> digestChanges)
    {
        Applied = applied;
        Kind = applied
            ? CatalogMutationApplyKind.DigestMutation
            : CatalogMutationApplyKind.NoOp;
        OwnedCollectionApplied = ownedCollectionApplied;
        StorageRowsVersion = storageRowsVersion;
        OwnedCollectionVersion = ownedCollectionVersion;
        DigestChanges = Array.AsReadOnly([.. digestChanges ?? []]);
    }

    internal bool Applied { get; }

    internal CatalogMutationApplyKind Kind { get; }

    internal bool OwnedCollectionApplied { get; }

    internal StorageRowsVersionSnapshot StorageRowsVersion { get; }

    internal int OwnedCollectionVersion { get; }

    internal IReadOnlyList<LibraryChartDigestChange> DigestChanges { get; }
}

/// <summary>
/// Immutable input snapshot for a catalog file-scan replacement.
/// </summary>
internal sealed class CatalogFileScanStorageReplacementRequest
{
    internal CatalogFileScanStorageReplacementRequest(
        bool hasDbDiff,
        IEnumerable<BMSFile> nextBmsRows,
        IEnumerable<LR2SongDBExtended.bmson_song> nextBmsonRows,
        IEnumerable<string> deletedBmsPaths,
        IEnumerable<string> deletedBmsonPaths,
        IEnumerable<BMSFile> addedBmsFiles,
        IEnumerable<LR2SongDBExtended.bmson_song> addedBmsonSongs,
        bool removedPayloadAvailable,
        IEnumerable<ChartFile> removedCharts)
    {
        HasDbDiff = hasDbDiff;
        NextBmsRows = Snapshot(nextBmsRows);
        NextBmsonRows = Snapshot(nextBmsonRows);
        DeletedBmsPaths = Snapshot(deletedBmsPaths);
        DeletedBmsonPaths = Snapshot(deletedBmsonPaths);
        AddedBmsFiles = Snapshot(addedBmsFiles);
        AddedBmsonSongs = Snapshot(addedBmsonSongs);
        AddedCharts = CatalogChartMutationFact.CreateFacts(
            ChartFileProjection.FromStorageRows(
                AddedBmsFiles,
                AddedBmsonSongs,
                includeWarningSnapshot: false,
                requirePath: false,
                includeResourceReferences: false,
                includeScoreSnapshot: false));
        RemovedPayloadAvailable = removedPayloadAvailable;
        RemovedCharts = Snapshot(removedCharts);
    }

    internal bool HasDbDiff { get; }

    internal IReadOnlyList<BMSFile> NextBmsRows { get; }

    internal IReadOnlyList<LR2SongDBExtended.bmson_song> NextBmsonRows { get; }

    internal IReadOnlyList<string> DeletedBmsPaths { get; }

    internal IReadOnlyList<string> DeletedBmsonPaths { get; }

    internal IReadOnlyList<BMSFile> AddedBmsFiles { get; }

    internal IReadOnlyList<LR2SongDBExtended.bmson_song> AddedBmsonSongs { get; }

    internal IReadOnlyList<CatalogChartMutationFact> AddedCharts { get; }

    internal bool RemovedPayloadAvailable { get; }

    internal IReadOnlyList<ChartFile> RemovedCharts { get; }

    private static IReadOnlyList<T> Snapshot<T>(IEnumerable<T> values)
    {
        return Array.AsReadOnly([.. values ?? []]);
    }
}

/// <summary>
/// Canonical facts emitted after a successful catalog file-scan replacement.
/// </summary>
internal sealed class CatalogFileScanStorageReplacementReceipt
{
    internal static CatalogFileScanStorageReplacementReceipt NotApplied { get; } =
        new(
            applied: false,
            ownedCollectionApplied: false,
            default,
            ownedCollectionVersion: 0,
            new OwnedChartStorageRowFilterSummary(0, 0, 0, 0, 0, 0),
            [],
            [],
            []);

    internal CatalogFileScanStorageReplacementReceipt(
        bool applied,
        bool ownedCollectionApplied,
        StorageRowsVersionSnapshot versions,
        int ownedCollectionVersion,
        OwnedChartStorageRowFilterSummary filterSummary,
        IEnumerable<CatalogChartMutationFact> addedCharts,
        IEnumerable<ChartFile> removedCharts,
        IEnumerable<ChartFile> movedCharts)
    {
        Applied = applied;
        Kind = applied
            ? CatalogMutationApplyKind.FileScanStorageReplacement
            : CatalogMutationApplyKind.NoOp;
        OwnedCollectionApplied = ownedCollectionApplied;
        StorageRowsVersion = versions;
        OwnedCollectionVersion = ownedCollectionVersion;
        FilterSummary = filterSummary;
        AddedCharts = SnapshotFacts(addedCharts);
        RemovedCharts = CreateFacts(removedCharts);
        MovedCharts = CreateFacts(movedCharts);
    }

    internal bool Applied { get; }

    internal CatalogMutationApplyKind Kind { get; }

    internal bool OwnedCollectionApplied { get; }

    internal StorageRowsVersionSnapshot StorageRowsVersion { get; }

    /// <summary>
    /// Gets the owned collection version committed at the file-scan apply boundary.
    /// </summary>
    internal int OwnedCollectionVersion { get; }

    internal OwnedChartStorageRowFilterSummary FilterSummary { get; }

    internal IReadOnlyList<CatalogChartMutationFact> AddedCharts { get; }

    internal IReadOnlyList<CatalogChartMutationFact> RemovedCharts { get; }

    internal IReadOnlyList<CatalogChartMutationFact> MovedCharts { get; }

    private static IReadOnlyList<CatalogChartMutationFact> CreateFacts(IEnumerable<ChartFile> charts)
    {
        return Array.AsReadOnly((charts ?? [])
            .Where(chart => chart != null)
            .Select(CatalogChartMutationFact.FromChart)
            .ToArray());
    }

    private static IReadOnlyList<CatalogChartMutationFact> SnapshotFacts(
        IEnumerable<CatalogChartMutationFact> facts)
    {
        return Array.AsReadOnly([.. facts ?? []]);
    }
}

internal sealed class CatalogChartMutationFact(
    ChartFileKind kind,
    string path,
    string md5,
    string sha256,
    OwnedChartRemoveMode? removalMode = null)
{
    internal ChartFileKind Kind { get; } = kind;

    internal string Path { get; } = path;

    internal string Md5 { get; } = md5;

    internal string Sha256 { get; } = sha256;

    internal OwnedChartRemoveMode? RemovalMode { get; } = removalMode;

    internal static CatalogChartMutationFact FromChart(ChartFile chart)
    {
        return new CatalogChartMutationFact(chart.Kind, chart.Path, chart.Md5, chart.Sha256);
    }

    internal static IReadOnlyList<CatalogChartMutationFact> CreateFacts(IEnumerable<ChartFile> charts)
    {
        return Array.AsReadOnly((charts ?? [])
            .Where(chart => chart != null)
            .Select(FromChart)
            .ToArray());
    }

    /// <summary>
    /// 削除要求が破壊前に保持したkind、path、digestをcatalog mutation factへ変換します。
    /// </summary>
    /// <param name="request">不変identity factsを保持する削除要求。</param>
    /// <returns>削除要求のmutation fact。要求がnullの場合はnull。</returns>
    internal static CatalogChartMutationFact FromRemovalRequest(OwnedChartRemoveRequest request)
    {
        if (request == null)
        {
            return null;
        }

        return new CatalogChartMutationFact(
            request.Kind,
            request.Path,
            request.CapturedMd5,
            request.CapturedSha256,
            request.Mode);
    }

    internal static IReadOnlyList<CatalogChartMutationFact> CreateRemovalFacts(
        IEnumerable<OwnedChartRemoveRequest> requests)
    {
        return Array.AsReadOnly([..
            (requests ?? [])
                .Select(FromRemovalRequest)
                .Where(fact => fact != null)]);
    }
}
