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
    /// Commits package-inline storage rows and chart-info facts as one catalog command.
    /// The caller supplies a snapshot request; no facade-owned database writer is needed.
    /// </summary>
    internal CatalogInlineChartInfoWriteReceipt ApplyInlineChartInfoWrite(
        CatalogInlineChartInfoWriteRequest request)
    {
        if (request == null || !request.HasChanges)
        {
            return CatalogInlineChartInfoWriteReceipt.NotApplied;
        }
        if (dbGateway == null)
        {
            throw new InvalidOperationException("Catalog mutation owner is not configured with a song database.");
        }

        using (maintenanceWriteGate.GetWriterGuard())
        {
            dbGateway.ExecuteSongDbTransaction(songDb =>
            {
                if (request.BmsRows.Count > 0)
                {
                    BmsLibraryDbGateway.EnsureBmsonSchema(songDb);
                    BmsLibraryDbGateway.EnsureSongLookupIndexes(songDb);
                    foreach (BMSFile row in request.BmsRows)
                    {
                        Lr2SongDbWriter.UpsertGeneratedSong(songDb, row);
                    }
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
        return new CatalogInlineChartInfoWriteReceipt(
            applied: true,
            request.BmsRows.Count,
            request.BmsonRows.Count,
            CreateChartInfoWriteReceipt(request.ChartInfo));
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
                Lr2SongDbSyncStatusMutationKind.MarkCancelled => Lr2SongDbSyncStatusService.MarkCancelled(
                    songDb,
                    request.Signature,
                    request.RunId,
                    request.ProcessedCursor,
                    request.TotalCount,
                    request.Stage,
                    request.NowUtc),
                _ => throw new ArgumentOutOfRangeException(nameof(request.Kind), request.Kind, "Unknown LR2 song DB status mutation.")
            };
        }
    }

    internal Lr2StartupScanBlockerCleanupReceipt ApplyLr2StartupScanBlockerCleanup(
        Lr2StartupScanBlockerCleanupRequest request)
    {
        if (request == null)
        {
            throw new ArgumentNullException(nameof(request));
        }

        using (maintenanceWriteGate.GetWriterGuard())
        using (LR2SongDBExtended songDb = dbGateway?.OpenSongDb()
            ?? throw new InvalidOperationException("Catalog mutation owner is not configured with a song database."))
        {
            Lr2SongDbSyncInput input = request.Input;
            Lr2StartupScanBlockerCleanupResult result =
                Lr2SongDbSyncService.CleanupStartupScanBlockerFolderRows(
                    songDb,
                    input.RootDirectories,
                    input.Lr2FolderDiscoveryDirectories,
                    input.SongRows,
                    input.Lr2RootPath);
            Lr2SongDbSyncStatusSnapshot status = Lr2SongDbSyncStatusService.Evaluate(
                songDb,
                request.Enabled,
                request.Signature,
                request.NowUtc);
            return new Lr2StartupScanBlockerCleanupReceipt(result, status);
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
                relocationRequest.BmsPathReplacements.Count > 0,
                relocationRequest.BmsonPathReplacements.Count > 0,
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
                storageRowsVersion);
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
                 removalRequest?.RemoveRequests);
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
            && !string.Equals(bmsFile.path, oldPath, StringComparison.OrdinalIgnoreCase)
            && !string.Equals(bmsFile.path, newPath, StringComparison.OrdinalIgnoreCase))
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
            && !string.Equals(bmsonSong.path, oldPath, StringComparison.OrdinalIgnoreCase)
            && !string.Equals(bmsonSong.path, newPath, StringComparison.OrdinalIgnoreCase))
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

    internal CatalogStorageRowsReplacementRequest CreateStorageRowsReplacementRequest(
        IEnumerable<BMSFile> bmsRows,
        IEnumerable<LR2SongDBExtended.bmson_song> bmsonRows,
        bool replaceBmsRows,
        bool replaceBmsonRows)
    {
        using (storageRowsOwner.WriteGate.GetWriterGuard())
        {
            CatalogStorageRowsSnapshot currentRows = storageRowsOwner.CaptureSnapshot();
            return new CatalogStorageRowsReplacementRequest(
                bmsRows,
                bmsonRows,
                replaceBmsRows,
                replaceBmsonRows,
                replaceBmsRows && !ReferenceEquals(currentRows.BmsRows, bmsRows),
                replaceBmsonRows && !ReferenceEquals(currentRows.BmsonRows, bmsonRows));
        }
    }

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
                storageRowsOwner.ReplaceBmsRows([.. request.BmsRows]);
            }
            if (request.ReplaceBmsonRows && request.BmsonRowsChanged)
            {
                storageRowsOwner.ReplaceBmsonRows([.. request.BmsonRows]);
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
            CatalogStorageRowsSnapshot currentRows = storageRowsOwner.CaptureSnapshot();
            return CreateFileScanStorageReplacementRequestUnsafe(
                hasDbDiff,
                nextBmsRows,
                nextBmsonRows,
                deletedBmsPaths,
                deletedBmsonPaths,
                addedBmsFiles,
                addedBmsonSongs,
                currentRows);
        }
    }

    internal CatalogFileScanStorageReplacementReceipt ApplyFileScanStorageReplacement(
        CatalogFileScanStorageReplacementRequest request)
    {
        if (request == null)
        {
            return CatalogFileScanStorageReplacementReceipt.NotApplied;
        }

        using (storageRowsOwner.WriteGate.GetWriterGuard())
        {
            CatalogStorageRowsSnapshot currentRows = storageRowsOwner.CaptureSnapshot();
            if (currentRows.BmsRowsVersion != request.PreviousBmsRowsVersion
                || currentRows.BmsonRowsVersion != request.PreviousBmsonRowsVersion)
            {
                throw new InvalidOperationException(
                    "The catalog storage rows changed while a file-scan replacement was being prepared.");
            }

            CatalogStorageRowsSnapshot storageRows = request.HasDbDiff
                ? storageRowsOwner.ReplaceRowsAndCaptureSnapshot(
                    [.. request.NextBmsRows],
                    [.. request.NextBmsonRows])
                : request.CurrentRows;
            CatalogOwnedCollectionReplacementResult ownedReplacement = request.HasDbDiff
                ? ownedCollectionOwner.ReplaceForFileScan(storageRows)
                : CatalogOwnedCollectionReplacementResult.NotApplied;
            StorageRowsVersionSnapshot versions = new(
                request.PreviousBmsRowsVersion,
                request.PreviousBmsonRowsVersion,
                storageRows.BmsRowsVersion,
                storageRows.BmsonRowsVersion);
            return new CatalogFileScanStorageReplacementReceipt(
                applied: request.HasDbDiff,
                ownedCollectionApplied: ownedReplacement.Applied,
                versions,
                ownedCollectionOwner.CollectionVersion,
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
        IEnumerable<LR2SongDBExtended.bmson_song> addedBmsonSongs,
        CatalogStorageRowsSnapshot currentRows)
    {
        bool removedPayloadAvailable = ownedCollectionOwner.TryCreateFileScanRemovedStorageOwnerIdentityCharts(
            [.. deletedBmsPaths ?? []],
            [.. deletedBmsonPaths ?? []],
            [.. nextBmsRows ?? []],
            [.. nextBmsonRows ?? []],
            currentRows.BmsRowsVersion,
            currentRows.BmsonRowsVersion,
            out List<ChartFile> removedCharts);
        return new CatalogFileScanStorageReplacementRequest(
            hasDbDiff,
            nextBmsRows,
            nextBmsonRows,
            deletedBmsPaths,
            deletedBmsonPaths,
            addedBmsFiles,
            addedBmsonSongs,
            currentRows.BmsRowsVersion,
            currentRows.BmsonRowsVersion,
            removedPayloadAvailable,
            removedCharts,
            currentRows);
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

    internal CatalogInstalledTargetUpsertReceipt ApplyInstalledTargetUpsert(
        IEnumerable<BMSFile> bmsRows,
        IEnumerable<LR2SongDBExtended.bmson_song> bmsonRows,
        Action onValidationPassed = null)
    {
        CatalogWriteFailureFact failureFact = null;
        try
        {
            using (storageRowsOwner.WriteGate.GetWriterGuard())
            using (maintenanceWriteGate.GetWriterGuard())
            {
                return ApplyInstalledTargetUpsertUnsafe(
                    CreateInstalledTargetUpsertRequestUnsafe(bmsRows, bmsonRows),
                    onValidationPassed,
                    fact => failureFact = fact);
            }
        }
        catch
        {
            PublishCatalogWriteFailureFactBestEffort(failureFact);
            throw;
        }
    }

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
        IEnumerable<BMSFile> bmsRows,
        IEnumerable<LR2SongDBExtended.bmson_song> bmsonRows)
    {
        StorageRowsVersionSnapshot currentVersions = storageRowsOwner.CaptureVersionSnapshot();
        return new CatalogInstalledTargetUpsertRequest(
            bmsRows,
            bmsonRows,
            currentVersions.BmsRowsVersion,
            currentVersions.BmsonRowsVersion);
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

        ChartStorageTargetSet targets = ChartStorageTargetSet.FromRows(
            request.BmsRows,
            request.BmsonRows);
        if (targets.BmsFiles.Count == 0 && targets.BmsonSongs.Count == 0)
        {
            return CatalogInstalledTargetUpsertReceipt.NotApplied;
        }
        if (dbGateway == null)
        {
            throw new InvalidOperationException("Catalog mutation owner is not configured with a song database.");
        }

        ownedCollectionOwner.ValidateStorageRowUpsert(
            targets.BmsFiles,
            targets.BmsonSongs);
        onValidationPassed?.Invoke();

        try
        {
            dbGateway.ExecuteSongDbTransaction(songDb =>
            {
                if (targets.BmsFiles.Count > 0)
                {
                    BmsLibraryDbGateway.EnsureBmsonSchema(songDb);
                    BmsLibraryDbGateway.EnsureSongLookupIndexes(songDb);
                    foreach (BMSFile bmsFile in targets.BmsFiles)
                    {
                        Lr2SongDbWriter.UpsertGeneratedSong(songDb, bmsFile);
                    }
                }
                if (targets.BmsonSongs.Count > 0)
                {
                    BmsLibraryDbGateway.EnsureBmsonSchema(songDb);
                    foreach (LR2SongDBExtended.bmson_song bmsonSong in targets.BmsonSongs)
                    {
                        songDb.InsertOrReplace(bmsonSong, typeof(LR2SongDBExtended.bmson_song));
                    }
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

        IReadOnlyList<CatalogChartMutationFact> addedCharts = CatalogChartMutationFact.CreateFacts(
            ChartFileProjection.FromStorageRows(
                targets.BmsFiles,
                targets.BmsonSongs,
                includeWarningSnapshot: false,
                requirePath: true,
                includeResourceReferences: false,
                includeScoreSnapshot: false));
        StorageRowsVersionSnapshot versions = storageRowsOwner.ApplyInstalledTargets(targets);
        bool ownedCollectionApplied = ownedCollectionOwner.ApplyMutation(
            [],
            [],
            targets.BmsFiles,
            targets.BmsonSongs,
            versions);
        int ownedCollectionVersion = ownedCollectionApplied
            ? ownedCollectionOwner.IncrementVersion()
            : ownedCollectionOwner.CollectionVersion;
        return new CatalogInstalledTargetUpsertReceipt(
            applied: targets.BmsFiles.Count > 0 || targets.BmsonSongs.Count > 0,
            ownedCollectionApplied,
            versions,
            ownedCollectionVersion,
            addedCharts);
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
/// Immutable input snapshot for catalog storage-row removal.
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

    internal IReadOnlyList<string> BmsPathCleanupKeys { get; }

    internal IReadOnlyList<LR2SongDBExtended.bmson_song> RemovedBmsonRows { get; }

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
            .Select(request => OwnedChartCollectionState.CreateOwnedPathKey(request.Path))
            .Where(path => !string.IsNullOrWhiteSpace(path))
            .Distinct(StringComparer.OrdinalIgnoreCase));
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
    internal CatalogStorageRowsReplacementRequest(
        IEnumerable<BMSFile> bmsRows,
        IEnumerable<LR2SongDBExtended.bmson_song> bmsonRows,
        bool replaceBmsRows,
        bool replaceBmsonRows,
        bool bmsRowsChanged,
        bool bmsonRowsChanged)
    {
        BmsRows = Snapshot(bmsRows);
        BmsonRows = Snapshot(bmsonRows);
        ReplaceBmsRows = replaceBmsRows;
        ReplaceBmsonRows = replaceBmsonRows;
        BmsRowsChanged = bmsRowsChanged;
        BmsonRowsChanged = bmsonRowsChanged;
    }

    internal IReadOnlyList<BMSFile> BmsRows { get; }

    internal IReadOnlyList<LR2SongDBExtended.bmson_song> BmsonRows { get; }

    internal bool ReplaceBmsRows { get; }

    internal bool ReplaceBmsonRows { get; }

    internal bool BmsRowsChanged { get; }

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
        int previousBmsonRowsVersion)
    {
        BmsRows = Snapshot(bmsRows);
        BmsonRows = Snapshot(bmsonRows);
        PreviousBmsRowsVersion = previousBmsRowsVersion;
        PreviousBmsonRowsVersion = previousBmsonRowsVersion;
        AddedCharts = CatalogChartMutationFact.CreateFacts(
            ChartFileProjection.FromStorageRows(
                BmsRows,
                BmsonRows,
                includeWarningSnapshot: false,
                requirePath: false,
                includeResourceReferences: false,
                includeScoreSnapshot: false));
    }

    internal IReadOnlyList<BMSFile> BmsRows { get; }

    internal IReadOnlyList<LR2SongDBExtended.bmson_song> BmsonRows { get; }

    internal int PreviousBmsRowsVersion { get; }

    internal int PreviousBmsonRowsVersion { get; }

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
            []);

    internal CatalogInstalledTargetUpsertReceipt(
        bool applied,
        bool ownedCollectionApplied,
        StorageRowsVersionSnapshot storageRowsVersion,
        int ownedCollectionVersion,
        IEnumerable<CatalogChartMutationFact> addedCharts)
    {
        Applied = applied;
        Kind = applied
            ? CatalogMutationApplyKind.InstalledTargetUpsert
            : CatalogMutationApplyKind.NoOp;
        OwnedCollectionApplied = ownedCollectionApplied;
        StorageRowsVersion = storageRowsVersion;
        OwnedCollectionVersion = ownedCollectionVersion;
        AddedCharts = Array.AsReadOnly([.. addedCharts ?? []]);
    }

    internal bool Applied { get; }

    internal CatalogMutationApplyKind Kind { get; }

    internal bool OwnedCollectionApplied { get; }

    internal StorageRowsVersionSnapshot StorageRowsVersion { get; }

    internal int OwnedCollectionVersion { get; }

    internal IReadOnlyList<CatalogChartMutationFact> AddedCharts { get; }
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
        int previousBmsRowsVersion,
        int previousBmsonRowsVersion,
        bool removedPayloadAvailable,
        IEnumerable<ChartFile> removedCharts,
        CatalogStorageRowsSnapshot currentRows)
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
        PreviousBmsRowsVersion = previousBmsRowsVersion;
        PreviousBmsonRowsVersion = previousBmsonRowsVersion;
        RemovedPayloadAvailable = removedPayloadAvailable;
        RemovedCharts = Snapshot(removedCharts);
        CurrentRows = currentRows ?? throw new ArgumentNullException(nameof(currentRows));
    }

    internal bool HasDbDiff { get; }

    internal IReadOnlyList<BMSFile> NextBmsRows { get; }

    internal IReadOnlyList<LR2SongDBExtended.bmson_song> NextBmsonRows { get; }

    internal IReadOnlyList<string> DeletedBmsPaths { get; }

    internal IReadOnlyList<string> DeletedBmsonPaths { get; }

    internal IReadOnlyList<BMSFile> AddedBmsFiles { get; }

    internal IReadOnlyList<LR2SongDBExtended.bmson_song> AddedBmsonSongs { get; }

    internal IReadOnlyList<CatalogChartMutationFact> AddedCharts { get; }

    internal int PreviousBmsRowsVersion { get; }

    internal int PreviousBmsonRowsVersion { get; }

    internal bool RemovedPayloadAvailable { get; }

    internal IReadOnlyList<ChartFile> RemovedCharts { get; }

    internal CatalogStorageRowsSnapshot CurrentRows { get; }

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

    internal static CatalogChartMutationFact FromRemovalRequest(OwnedChartRemoveRequest request)
    {
        if (request == null)
        {
            return null;
        }

        BMSFile bmsOwner = request.BmsOwner;
        LR2SongDBExtended.bmson_song bmsonOwner = request.BmsonOwner;
        return new CatalogChartMutationFact(
            request.Kind,
            request.Path,
            bmsOwner?.hash ?? bmsonOwner?.md5,
            bmsOwner?.sha256 ?? bmsonOwner?.sha256,
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
