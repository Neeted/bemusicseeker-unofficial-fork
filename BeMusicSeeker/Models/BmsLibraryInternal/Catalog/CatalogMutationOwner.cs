using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using BeMusicSeeker.Models.LR2;
using BeMusicSeeker.Models.Utils;
using BeMusicSeeker.Properties;

namespace BeMusicSeeker.Models.BmsLibraryInternal;

/// <summary>
/// DB確定と共通の所持現在値の適用を管理します。
/// 利用側のキャッシュ・表示への効果は <see cref="BMSLibrary"/> が解放後に接続します。
/// </summary>
internal sealed class CatalogMutationOwner
{
    private readonly CatalogOwnedCollectionOwner ownedCollectionOwner;

    private readonly BmsLibraryDbGateway dbGateway;

    private readonly Lr2ManagedPlaylistOutputScopeOwner managedPlaylistOutputScopeOwner;

    private readonly ReaderWriterLockSlimWrapper maintenanceWriteGate = new();

    internal CatalogMutationOwner(
        CatalogOwnedCollectionOwner ownedCollectionOwner,
        BmsLibraryDbGateway dbGateway)
    {
        this.ownedCollectionOwner = ownedCollectionOwner ?? throw new ArgumentNullException(nameof(ownedCollectionOwner));
        this.dbGateway = dbGateway;
        managedPlaylistOutputScopeOwner = dbGateway == null
            ? null
            : new Lr2ManagedPlaylistOutputScopeOwner(dbGateway);
    }

    internal event EventHandler<CatalogWriteFailureFact> CatalogWriteFailurePublished;

    /// <summary>現正本の管理出力先を固定し、物理競合の判定では読取失敗を元例外で伝播します。</summary>
    internal Lr2SongDbSyncAppManagedOutputScope CaptureLr2SongDbSyncAppManagedOutputScope(
        BmsLibraryOptionsSnapshot options, bool throwOnFailure = false)
    {
        if (options == null)
        {
            throw new InvalidOperationException("BMS library options snapshot provider returned null.");
        }

        return managedPlaylistOutputScopeOwner?.Capture(options, throwOnFailure)
            ?? new Lr2SongDbSyncAppManagedOutputScope([], [], [], isComplete: false);
    }

    /// <summary>
    /// 保守値と関連する共通基本値を一つのDBトランザクションで保存します。
    /// 評価側は不変の事実だけを渡し、DBを直接更新しません。
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
            if (request.Songs.Any(chart => chart.Kind == ChartFileKind.Bmson))
            {
                BmsLibraryDbGateway.EnsureBmsonSchema(songDb);
            }
            foreach (ResourceHealthMaintenanceSnapshot value in request.MaintenanceInfos)
            {
                songDb.InsertOrReplace(MaintenanceStorageMapping.ToStorage(value), typeof(LR2SongDBExtended.maintenance));
            }
            foreach (ChartFile chart in request.Songs)
            {
                if (chart.Kind == ChartFileKind.Bms)
                {
                    Lr2SongDbWriter.UpsertGeneratedSong(songDb, ChartSongStorageMapping.ToBmsRow(chart));
                }
                else
                {
                    songDb.InsertOrReplace(ChartSongStorageMapping.ToBmsonRow(chart), typeof(LR2SongDBExtended.bmson_song));
                }
            }
            foreach (string path in request.StaleMaintenancePaths)
            {
                deletedMaintenanceCount += songDb.Delete<LR2SongDBExtended.maintenance>(path);
            }
        });
        return new CatalogMaintenanceWriteReceipt(
            applied: true,
            request.MaintenanceInfos.Count,
            request.Songs.Count(chart => chart.Kind == ChartFileKind.Bms),
            request.Songs.Count(chart => chart.Kind == ChartFileKind.Bmson),
            deletedMaintenanceCount);
    }

    /// <summary>
    /// 不変の詳細情報chunkを既存のカタログ保存境界で確定します。
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
    internal IReadOnlyList<ChartFile> ApplyModeChangeSongRows(IEnumerable<ChartFile> bmsFiles)
    {
        List<ChartFile> files = [.. (bmsFiles ?? []).Where(file => file != null)];
        if (files.Count == 0)
        {
            return [];
        }
        if (dbGateway == null)
        {
            throw new InvalidOperationException("Catalog mutation owner is not configured with a song database.");
        }

        var changed = new List<ChartFile>();
        try
        {
            using (ownedCollectionOwner.WriteGate.GetWriterGuard())
            using (maintenanceWriteGate.GetWriterGuard())
            {
                dbGateway.UpdateSongModes(files);
                lock (ownedCollectionOwner.Gate)
                {
                    foreach (ChartFile chart in files)
                    {
                        ChartFile current = ownedCollectionOwner.Collection.ResolveCurrentChart(LibraryChartRef.FromChartFile(chart));
                        if (current?.Token == null || !ReferenceEquals(current.Token, chart.Token))
                        {
                            continue;
                        }
                        ChartFile next = current with { Mode = chart.Mode };
                        bool valueChanged = current.Mode != next.Mode;
                        if (valueChanged && ownedCollectionOwner.Collection.ApplyCurrentChartValue(next))
                        {
                            changed.Add(next);
                        }
                    }
                    if (changed.Count > 0)
                    {
                        ownedCollectionOwner.IncrementVersion();
                        ownedCollectionOwner.RebaseHashIndexSnapshot();
                    }
                }
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
        return changed;
    }

    /// <summary>
    /// Persists playlist level writeback rows under the catalog write gate.
    /// </summary>
    internal IReadOnlyList<ChartFile> ApplyPlaylistLevelRows(IEnumerable<ChartFile> bmsFiles)
    {
        List<ChartFile> files = [.. (bmsFiles ?? [])
            .Where(file => file != null && !string.IsNullOrWhiteSpace(file.Path) && file.Level.HasValue)];
        if (files.Count == 0)
        {
            return [];
        }
        if (dbGateway == null)
        {
            throw new InvalidOperationException("Catalog mutation owner is not configured with a song database.");
        }

        var changed = new List<ChartFile>();
        try
        {
            using (ownedCollectionOwner.WriteGate.GetWriterGuard())
            using (maintenanceWriteGate.GetWriterGuard())
            {
                dbGateway.UpdateSongLevels(files);
                lock (ownedCollectionOwner.Gate)
                {
                    foreach (ChartFile chart in files)
                    {
                        ChartFile current = ownedCollectionOwner.Collection.ResolveCurrentChart(LibraryChartRef.FromChartFile(chart));
                        if (current?.Token == null || !ReferenceEquals(current.Token, chart.Token))
                        {
                            continue;
                        }
                        ChartFile next = current with { Level = chart.Level, LevelText = chart.LevelText };
                        bool valueChanged = current.Level != next.Level || current.LevelText != next.LevelText;
                        if (valueChanged && ownedCollectionOwner.Collection.ApplyCurrentChartValue(next))
                        {
                            changed.Add(next);
                        }
                    }
                    if (changed.Count > 0)
                    {
                        ownedCollectionOwner.IncrementVersion();
                        ownedCollectionOwner.RebaseHashIndexSnapshot();
                    }
                }
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
        return changed;
    }

    /// <summary>
    /// 共通基本値と詳細情報を一つのカタログ保存コマンドで確定します。
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

        ChartFile[] bmsCharts = [.. request.Charts.Where(chart => chart.Kind == ChartFileKind.Bms)];
        ChartFile[] bmsonCharts = [.. request.Charts.Where(chart => chart.Kind == ChartFileKind.Bmson)];
        Lr2ChartInfoSongProjectionWriteResult chartInfoSongProjectionResult =
            Lr2ChartInfoSongProjectionWriteResult.Empty;
        using (maintenanceWriteGate.GetWriterGuard())
        {
            dbGateway.ExecuteSongDbTransaction(songDb =>
            {
                if (bmsCharts.Length > 0)
                {
                    BmsLibraryDbGateway.EnsureBmsonSchema(songDb);
                    BmsLibraryDbGateway.EnsureSongLookupIndexes(songDb);
                    Lr2SongDbWriter.UpsertGeneratedSongs(songDb, bmsCharts.Select(ChartSongStorageMapping.ToBmsRow).ToArray());
                }
                if (request.ChartInfoSongProjections.Count > 0)
                {
                    chartInfoSongProjectionResult = Lr2SongDbWriter.UpdateChartInfoSongProjections(
                        songDb,
                        request.ChartInfoSongProjections);
                }
                if (bmsonCharts.Length > 0)
                {
                    BmsLibraryDbGateway.EnsureBmsonSchema(songDb);
                    foreach (ChartFile chart in bmsonCharts)
                    {
                        songDb.InsertOrReplace(ChartSongStorageMapping.ToBmsonRow(chart), typeof(LR2SongDBExtended.bmson_song));
                    }
                }
                ApplyChartInfoWriteToTransaction(songDb, request.ChartInfo);
            });
        }
        return new CatalogChartInfoStorageWriteReceipt(
            applied: true,
            bmsCharts.Length,
            bmsonCharts.Length,
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
        return ownedCollectionOwner.WriteGate.GetWriterGuard();
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
    /// 所持集合から保守情報の順に既存の排他を取り、LR2入力の共通値と最新性の版を捕捉します。
    /// 戻り値は不変で、管理主体や排他を保持せず使用できます。
    /// </summary>
    internal Lr2SongDbSyncInputRowSnapshot CaptureLr2SynchronizationInputRowSnapshot()
    {
        using (ownedCollectionOwner.WriteGate.GetReaderGuard())
        using (maintenanceWriteGate.GetReaderGuard())
        {
            CatalogChartCollectionSnapshot storageSnapshot = ownedCollectionOwner.CaptureSnapshot();
            var chartPathSet = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var chartPaths = new List<string>();
            var songRows = new List<ChartFile>();
            foreach (ChartFile file in storageSnapshot.BmsRows ?? [])
            {
                if (file == null || string.IsNullOrWhiteSpace(file.Path))
                {
                    continue;
                }
                songRows.Add(file);
                if (chartPathSet.Add(file.Path))
                {
                    chartPaths.Add(file.Path);
                }
            }

            return new Lr2SongDbSyncInputRowSnapshot(chartPaths, songRows, ownedCollectionOwner.OwnedCollectionVersion,
                ownedCollectionOwner.Collection.CapturePathMembershipIndex());
        }
    }

    internal IReadOnlyList<ChartFile> CaptureLr2SynchronizationBmsFilesSnapshot() =>
        CaptureLr2SynchronizationInputRowSnapshot().SongRows;

    internal OwnedChartCollectionVersionSnapshot CaptureLr2SynchronizationOwnedChartCollectionVersionSnapshot()
    {
        // CaptureVersionSnapshot is serialized by the storage owner's version
        // gate.  Do not acquire either owner lock here: LR2 sync invokes this
        // freshness probe while holding the maintenance writer, and another
        // maintenance route acquires storage before maintenance.
        return ownedCollectionOwner.CaptureVersionSnapshot();
    }

    /// <summary>
    /// 保存トランザクション前に移転事実を検証して捕捉します。
    /// 指定tokenが退役済みなら失敗し、tokenなしの直接DB要求だけ入力値を使います。
    /// </summary>
    internal CatalogRelocationRequest CreateRelocationRequest(LibraryCatalogMutationFacts facts)
    {
        if (facts == null)
        {
            return new CatalogRelocationRequest([], [], []);
        }

        var folderChanges = new List<CatalogFolderPathReplacement>();
        foreach (LibraryFolderPathChange change in facts.FolderPathChanges)
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
        foreach (LibraryChartPathChange change in facts.ChartPathChanges)
        {
            ChartFile chart = change?.Chart;
            if (chart == null)
            {
                continue;
            }

            // 参照由来のパス変更要求にも、保存確定時点の共通基本値を引き継ぎます。
            // 所持識別を持たない直接のDB要求は、その捕捉値を使います。
            lock (ownedCollectionOwner.Gate)
            {
                if (chart.Token != null)
                {
                    chart = ownedCollectionOwner.Collection.ResolveCurrentChart(LibraryChartRef.FromChartFile(chart))
                        ?? throw new InvalidCastException(Resources.Error_OldPathMismatch);
                }
            }
            ValidateChartPathChange(chart, change.NewPath, change.OldPath);
            ResourceHealthMaintenanceSnapshot maintenance = chart.ResourceHealthMaintenanceSnapshot?.Origin == MaintenanceInfoOrigin.Placeholder
                ? null : chart.ResourceHealthMaintenanceSnapshot;
            if (maintenance != null)
            {
                maintenance = maintenance with { Path = change.NewPath, Hash = chart.Md5 };
                if (chart.Kind == ChartFileKind.Bms)
                {
                    maintenance = RefreshRelocatedMaintenanceInfo(maintenance, change.NewPath);
                }
            }
            IReadOnlyList<ChartWarning> warnings = chart.Kind == ChartFileKind.Bms
                ? [.. chart.Warnings.Where(warning => warning.Kind != ChartWarningKind.Lr2PathEncodingUnsupported
                        && warning.Kind != ChartWarningKind.Lr2PathTooLong),
                    .. Lr2CompatibilityWarningProjection.BuildWarnings(new ResourceHealthMaintenanceSnapshot
                    { Lr2WarningFlags = (int)Lr2CompatibilityEvaluator.EvaluateChartPath(change.NewPath).WarningFlags })]
                : chart.Warnings;
            ChartFile next = ChartFileProjection.WithMaintenance(chart with
            {
                Warnings = warnings,
                Path = change.NewPath,
                Folder = Path.GetFileName(Path.GetDirectoryName(change.NewPath)) ?? string.Empty,
                Txt = chart.Kind == ChartFileKind.Bms ? Lr2TextGroupResolver.ResolveFlag(change.NewPath, chart.Txt.GetValueOrDefault()) : chart.Txt
            }, maintenance);
            string oldPath = string.IsNullOrWhiteSpace(change.OldPath) ? chart.Path : change.OldPath;
            if (chart.Kind == ChartFileKind.Bms)
            {
                bmsChanges.Add(new BmsSongPathReplacement(next, chart, oldPath, maintenance));
            }
            else
            {
                bmsonChanges.Add(new BmsonSongPathReplacement(next, chart, oldPath));
            }
        }

        return new CatalogRelocationRequest(
            folderChanges,
            bmsChanges,
            bmsonChanges);
    }

    /// <summary>
    /// 捕捉済みcatalog factsから一つの汎用catalog変更を適用します。
    /// 移動と削除の行を一つのdurable transactionで処理した後、
    /// canonicalなstorageからmaintenanceまでのguard内でlive catalogと所持譜面を更新します。
    /// </summary>
    /// <param name="facts">immutable catalog relocation/removal facts。</param>
    /// <param name="onDurableCommit">DB commit完了時に一度だけ呼ぶ通知。</param>
    internal CatalogMutationReceipt ApplyCatalogMutation(
        LibraryCatalogMutationFacts facts,
        Action onDurableCommit = null)
    {
        CatalogWriteFailureFact failureFact = null;
        try
        {
            return ApplyCatalogMutationUnderGuards(
                facts,
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
        LibraryCatalogMutationFacts facts,
        Action onDurableCommit,
        Action<CatalogWriteFailureFact> captureFailureFact)
    {
        if (facts == null)
        {
            return CatalogMutationReceipt.NotApplied;
        }
        CatalogRelocationRequest relocationRequest;
        CatalogStorageRowsRemovalRequest removalRequest;
        using (ownedCollectionOwner.WriteGate.GetWriterGuard())
        using (maintenanceWriteGate.GetWriterGuard())
        {
            relocationRequest = CreateRelocationRequest(facts);
            removalRequest = CreateStorageRowsRemovalRequest(facts.ChartRemoveRequests);
            if (!relocationRequest.HasChanges
                && !removalRequest.HasChanges)
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
                    removalRequest);
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
            long liveApplyMs = 0;
            OwnedChartCollectionVersionSnapshot storageRowsVersion = ownedCollectionOwner.CaptureVersionSnapshot();
            IReadOnlyList<CatalogRelocationPathFact> pathFacts =
            [
                .. relocationRequest.BmsPathReplacements.Select(replacement => new CatalogRelocationPathFact(
                    ChartFileKind.Bms,
                    replacement.OldPath,
                    replacement.Song.Path,
                    replacement.Song.Md5,
                    replacement.Song.Sha256,
                    replacement.Song.Token)),
                .. relocationRequest.BmsonPathReplacements.Select(replacement => new CatalogRelocationPathFact(
                    ChartFileKind.Bmson,
                    replacement.OldPath,
                    replacement.Song.Path,
                    replacement.Song.Md5,
                    replacement.Song.Sha256,
                    replacement.Song.Token))
            ];
            IReadOnlyList<CatalogChartMutationFact> removedChartFacts =
                CatalogChartMutationFact.CreateRemovalFacts(removalRequest?.RemoveRequests);
            bool ownedCollectionChanged = removedChartFacts.Count > 0
                || pathFacts.Count > 0;
            bool ownedCollectionApplied = ownedCollectionOwner.ApplyMutation(removalRequest?.RemoveRequests, [.. relocationRequest.BmsPathReplacements.Select(value => new LibraryChartPathChange { Chart = value.Song, OldPath = value.OldPath, NewPath = value.Song.Path })
                    .Concat(relocationRequest.BmsonPathReplacements.Select(value => new LibraryChartPathChange { Chart = value.Song, OldPath = value.OldPath, NewPath = value.Song.Path }))], out bool bmsonCanonicalOrderNormalized);
            int ownedCollectionVersion = ownedCollectionChanged
                ? ownedCollectionOwner.IncrementVersion()
                : ownedCollectionOwner.OwnedCollectionVersion;
            return new CatalogMutationReceipt(
                applied: true,
                new OwnedChartCollectionVersionSnapshot(storageRowsVersion.OwnedCollectionVersion, ownedCollectionVersion),
                dbResult.FolderDbMs,
                dbResult.BmsPathDbMs,
                dbResult.BmsonPathDbMs,
                dbResult.BmsRemovalDbMs,
                dbResult.BmsonRemovalDbMs,
                liveApplyMs,
                ownedCollectionApplied,
                 ownedCollectionVersion,
                 [],
                 pathFacts,
                 removalRequest?.RemoveRequests,
                 bmsonCanonicalOrderNormalized,
                 dbResult.FolderDbTargetRows,
                 dbResult.FolderDbFullScanCount);
        }
    }

    private static IReadOnlyList<CatalogRelocationPathFact> CreateProtectedPathFacts(
        CatalogRelocationRequest relocationRequest,
        CatalogStorageRowsRemovalRequest removalRequest)
    {
        var removedBmsOwners = new HashSet<OwnedChartToken>((removalRequest?.RemovedBmsRows ?? []).Select(chart => chart.Token));
        var removedBmsonOwners = new HashSet<OwnedChartToken>((removalRequest?.RemovedBmsonRows ?? []).Select(chart => chart.Token));
        return [
            .. (relocationRequest?.BmsPathReplacements ?? [])
                .Where(replacement => replacement?.Song != null
                    && !removedBmsOwners.Contains(replacement.LiveOwner.Token))
                .Select(replacement => new CatalogRelocationPathFact(
                    ChartFileKind.Bms,
                    replacement.OldPath,
                    replacement.Song.Path)),
            .. (relocationRequest?.BmsonPathReplacements ?? [])
                .Where(replacement => replacement?.Song != null
                    && !removedBmsonOwners.Contains(replacement.LiveOwner.Token))
                .Select(replacement => new CatalogRelocationPathFact(
                    ChartFileKind.Bmson,
                    replacement.OldPath,
                    replacement.Song.Path))
        ];
    }

    /// <summary>移転によって変わるLR2互換性だけを再評価し、未取得の保守情報は作成しません。</summary>
    private static ResourceHealthMaintenanceSnapshot RefreshRelocatedMaintenanceInfo(ResourceHealthMaintenanceSnapshot value, string newPath)
    {
        ChartResourceSnapshot resources = null;
        if (value.Lr2ResourceHasParentTraversal == true)
        {
            try { resources = ChartResourceSnapshot.Create(BmsChartFileParser.ParseSnapshot(ChartFileContentReader.ReadSnapshot(newPath)).Resources); }
            catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException || ex is ArgumentException || ex is NotSupportedException) { }
        }
        return Lr2CompatibilityEvaluator.RefreshRelocatedMaintenanceFacts(value, newPath, resources);
    }

    private static void ValidateChartPathChange(ChartFile chart, string newPath, string oldPath)
    {
        ArgumentNullException.ThrowIfNull(chart);
        ArgumentNullException.ThrowIfNull(newPath);
        if (!File.Exists(newPath))
        {
            throw new FileNotFoundException(Resources.Error_RenameDestFileNotFound, newPath);
        }

        if (!string.IsNullOrWhiteSpace(oldPath) && !string.Equals(chart.Path, oldPath, StringComparison.Ordinal)
            && !string.Equals(chart.Path, newPath, StringComparison.Ordinal))
        {
            throw new InvalidCastException(Resources.Error_OldPathMismatch);
        }
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
        IEnumerable<ChartFile> bmsRows,
        IEnumerable<ChartFile> bmsonRows,
        bool replaceBmsRows,
        bool replaceBmsonRows)
    {
        using (ownedCollectionOwner.WriteGate.GetWriterGuard())
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

        using (ownedCollectionOwner.WriteGate.GetWriterGuard())
        {
            OwnedChartCollectionVersionSnapshot previousVersions = ownedCollectionOwner.CaptureVersionSnapshot();
            ownedCollectionOwner.ReplaceCharts(request.BmsRows, request.BmsonRows,
                request.ReplaceBmsRows && request.BmsRowsChanged, request.ReplaceBmsonRows && request.BmsonRowsChanged);
            bool applied = request.BmsRowsChanged || request.BmsonRowsChanged;
            if (applied)
            {
                ownedCollectionOwner.Invalidate();
            }
            int ownedCollectionVersion = applied
                ? ownedCollectionOwner.IncrementVersion()
                : ownedCollectionOwner.OwnedCollectionVersion;
            OwnedChartCollectionVersionSnapshot currentVersions = ownedCollectionOwner.CaptureVersionSnapshot();
            return new CatalogStorageRowsReplacementReceipt(
                applied,
                request.BmsRowsChanged,
                request.BmsonRowsChanged,
                ownedCollectionInvalidated: applied,
                new OwnedChartCollectionVersionSnapshot(previousVersions.OwnedCollectionVersion, currentVersions.OwnedCollectionVersion),
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
        IEnumerable<ChartFile> nextBmsRows,
        IEnumerable<ChartFile> nextBmsonRows,
        IEnumerable<string> deletedBmsPaths,
        IEnumerable<string> deletedBmsonPaths,
        IEnumerable<ChartFile> addedBmsFiles,
        IEnumerable<ChartFile> addedBmsonSongs)
    {
        using (ownedCollectionOwner.WriteGate.GetReaderGuard())
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
    /// 差分走査の共通現在値を置換し、排他解放後に使う確定済み集合版を捕捉します。
    /// </summary>
    /// <param name="request">不変の置換要求。置換しない場合は <see langword="null"/>。</param>
    internal CatalogFileScanStorageReplacementReceipt ApplyFileScanStorageReplacement(
        CatalogFileScanStorageReplacementRequest request)
    {
        if (request == null)
        {
            return CatalogFileScanStorageReplacementReceipt.NotApplied;
        }

        using (ownedCollectionOwner.WriteGate.GetWriterGuard())
        {
            OwnedChartCollectionVersionSnapshot previousVersions = ownedCollectionOwner.CaptureVersionSnapshot();
            OwnedChartStorageRowFilterSummary filterSummary;
            lock (ownedCollectionOwner.Gate)
            {
                if (request.HasDbDiff)
                {
                    ownedCollectionOwner.ReplaceChartsAndCaptureSnapshot(request.NextBmsRows, request.NextBmsonRows);
                }
                filterSummary = ownedCollectionOwner.Collection.FilterSummary;
            }
            // The receipt is captured at this commit boundary; post-lease
            // publication must observe this version without advancing again.
            int ownedCollectionVersion = request.HasDbDiff
                ? ownedCollectionOwner.IncrementVersion()
                : ownedCollectionOwner.OwnedCollectionVersion;
            OwnedChartCollectionVersionSnapshot versions = new(
                previousVersions.OwnedCollectionVersion,
                ownedCollectionVersion);
            return new CatalogFileScanStorageReplacementReceipt(
                applied: request.HasDbDiff,
                ownedCollectionApplied: request.HasDbDiff,
                versions,
                ownedCollectionVersion,
                filterSummary,
                request.AddedCharts,
                request.RemovedCharts,
                movedCharts: []);
        }
    }

    private CatalogFileScanStorageReplacementRequest CreateFileScanStorageReplacementRequestUnsafe(
        bool hasDbDiff,
        IEnumerable<ChartFile> nextBmsRows,
        IEnumerable<ChartFile> nextBmsonRows,
        IEnumerable<string> deletedBmsPaths,
        IEnumerable<string> deletedBmsonPaths,
        IEnumerable<ChartFile> addedBmsFiles,
        IEnumerable<ChartFile> addedBmsonSongs)
    {
        bool removedPayloadAvailable = ownedCollectionOwner.TryCaptureFileScanRemovedCharts(
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
        IEnumerable<ChartFile> bmsRows,
        IEnumerable<ChartFile> bmsonRows)
    {
        using (ownedCollectionOwner.WriteGate.GetWriterGuard())
        {
            return CreateInstalledTargetUpsertRequestUnsafe(bmsRows, bmsonRows);
        }
    }

    /// <summary>
    /// 導入対象をDB確定後に共通現在値へ適用し、失敗事実の公開を呼出し側へ遅延します。
    /// </summary>
    /// <param name="targets">確定先とdetachedな共通値を保持する導入対象。</param>
    /// <param name="failureFact">DB書込み失敗のimmutable fact。</param>
    /// <param name="onValidationPassed">DB validation通過時に一度だけ呼ぶcallback。</param>
    /// <param name="installPathsToDelete">同じtransactionで削除するpending install rowのpath。</param>
    /// <param name="installRowsToUpsert">同じtransactionでupsertするpending install row。</param>
    /// <returns>DB・storage・owned collectionの確定receipt。</returns>
    internal CatalogInstalledTargetUpsertReceipt ApplyInstalledTargetUpsertWithDeferredFailurePublication(
        ChartStorageTargetSet targets,
        out CatalogWriteFailureFact failureFact,
        Action onValidationPassed = null,
        IEnumerable<string> installPathsToDelete = null,
        IEnumerable<ChartPackage> installRowsToUpsert = null)
    {
        if (targets == null)
        {
            failureFact = null;
            return CatalogInstalledTargetUpsertReceipt.NotApplied;
        }

        CatalogWriteFailureFact capturedFailureFact = null;
        try
        {
            using (ownedCollectionOwner.WriteGate.GetWriterGuard())
            using (maintenanceWriteGate.GetWriterGuard())
            {
                CatalogInstalledTargetUpsertReceipt receipt = ApplyInstalledTargetUpsertUnsafe(
                    CreateInstalledTargetUpsertRequestUnsafe(targets, installPathsToDelete, installRowsToUpsert),
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
            using (ownedCollectionOwner.WriteGate.GetWriterGuard())
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
        IEnumerable<string> installPathsToDelete = null,
        IEnumerable<ChartPackage> installRowsToUpsert = null)
    {
        OwnedChartCollectionVersionSnapshot currentVersions = ownedCollectionOwner.CaptureVersionSnapshot();
        return new CatalogInstalledTargetUpsertRequest(targets, currentVersions.OwnedCollectionVersion, installPathsToDelete, installRowsToUpsert);
    }

    private CatalogInstalledTargetUpsertRequest CreateInstalledTargetUpsertRequestUnsafe(
        IEnumerable<ChartFile> bmsRows,
        IEnumerable<ChartFile> bmsonRows,
        IEnumerable<string> installPathsToDelete = null)
    {
        OwnedChartCollectionVersionSnapshot currentVersions = ownedCollectionOwner.CaptureVersionSnapshot();
        return new CatalogInstalledTargetUpsertRequest(ChartStorageTargetSet.FromCharts((bmsRows ?? []).Concat(bmsonRows ?? [])), currentVersions.OwnedCollectionVersion, installPathsToDelete);
    }

    private CatalogInstalledTargetUpsertReceipt ApplyInstalledTargetUpsertUnsafe(
        CatalogInstalledTargetUpsertRequest request,
        Action onValidationPassed = null,
        Action<CatalogWriteFailureFact> captureFailureFact = null)
    {
        OwnedChartCollectionVersionSnapshot currentVersions = ownedCollectionOwner.CaptureVersionSnapshot();
        if (currentVersions.OwnedCollectionVersion != request.PreviousOwnedCollectionVersion)
        {
            throw new InvalidOperationException("Catalog storage rows changed before installed target upsert.");
        }

        ChartStorageTargetSet targets = request.Targets;
        bool hasInstallRowDeletion = request.InstallPathsToDelete.Count > 0;
        bool hasInstallRowUpsert = request.InstallRowsToUpsert.Count > 0;
        if (targets == null
            || (targets.BmsCharts.Count == 0
                && targets.BmsonCharts.Count == 0
                && !hasInstallRowDeletion
                && !hasInstallRowUpsert))
        {
            return CatalogInstalledTargetUpsertReceipt.NotApplied;
        }
        if (dbGateway == null)
        {
            throw new InvalidOperationException("Catalog mutation owner is not configured with a song database.");
        }

        ownedCollectionOwner.ValidateChartUpsert(targets.Charts);
        onValidationPassed?.Invoke();

        try
        {
            dbGateway.ExecuteSongDbTransaction(songDb =>
            {
                if (targets.BmsCharts.Count > 0)
                {
                    BmsLibraryDbGateway.EnsureBmsonSchema(songDb);
                    BmsLibraryDbGateway.EnsureSongLookupIndexes(songDb);
                    foreach (ChartFile chart in targets.BmsCharts)
                    {
                        Lr2SongDbWriter.UpsertGeneratedSong(songDb, ChartSongStorageMapping.ToBmsRow(chart));
                    }
                }
                if (targets.BmsonCharts.Count > 0)
                {
                    BmsLibraryDbGateway.EnsureBmsonSchema(songDb);
                    foreach (ChartFile chart in targets.BmsonCharts)
                    {
                        songDb.InsertOrReplace(ChartSongStorageMapping.ToBmsonRow(chart), typeof(LR2SongDBExtended.bmson_song));
                    }
                }
                if (hasInstallRowDeletion)
                {
                    foreach (string installPath in request.InstallPathsToDelete)
                    {
                        songDb.Delete<LR2SongDBExtended.install>(installPath);
                    }
                }
                if (hasInstallRowUpsert)
                {
                    songDb.InsertAll(request.InstallRowsToUpsert, typeof(LR2SongDBExtended.install));
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
        bool hasOwnedStorageTargets = targets.Charts.Count > 0;
        OwnedChartCollectionVersionSnapshot versions = ownedCollectionOwner.CaptureVersionSnapshot();
        bool ownedCollectionApplied = false;
        bool bmsonCanonicalOrderNormalized = false;
        if (hasOwnedStorageTargets)
        {
            ownedCollectionApplied = ownedCollectionOwner.ApplyCommittedChartUpsert(targets.Charts, out bmsonCanonicalOrderNormalized);
        }
        int ownedCollectionVersion = ownedCollectionApplied
            ? ownedCollectionOwner.IncrementVersion()
            : ownedCollectionOwner.OwnedCollectionVersion;
        return new CatalogInstalledTargetUpsertReceipt(
            applied: targets.BmsCharts.Count > 0
                || targets.BmsonCharts.Count > 0
                || hasInstallRowDeletion
                || hasInstallRowUpsert,
            ownedCollectionApplied,
            new OwnedChartCollectionVersionSnapshot(versions.OwnedCollectionVersion, ownedCollectionVersion),
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

        using (ownedCollectionOwner.WriteGate.GetWriterGuard())
        {
            bool ownedCollectionApplied = ownedCollectionOwner.ApplyDigestChanges(request.DigestChanges);
            return new CatalogDigestMutationReceipt(
                applied: request.DigestChanges.Count > 0,
                ownedCollectionApplied,
                ownedCollectionOwner.CaptureVersionSnapshot(),
                ownedCollectionOwner.OwnedCollectionVersion,
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
        RemovedBmsRows = Snapshot(requests.Where(request => request.Kind == ChartFileKind.Bms && request.Mode == OwnedChartRemoveMode.Item).Select(request => request.CreateChartSnapshot()));
        BmsPathCleanupKeys = CreatePathCleanupKeys(requests, ChartFileKind.Bms);
        RemovedBmsonRows = Snapshot(requests.Where(request => request.Kind == ChartFileKind.Bmson && request.Mode == OwnedChartRemoveMode.Item).Select(request => request.CreateChartSnapshot()));
        BmsonPathCleanupKeys = CreatePathCleanupKeys(requests, ChartFileKind.Bmson);
    }

    internal IReadOnlyList<OwnedChartRemoveRequest> RemoveRequests { get; }

    internal IReadOnlyList<ChartFile> RemovedBmsRows { get; }

    /// <summary>明示的に削除対象とされたBMS行の未加工exact key。</summary>
    internal IReadOnlyList<string> BmsPathCleanupKeys { get; }

    internal IReadOnlyList<ChartFile> RemovedBmsonRows { get; }

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
/// 選択した形式の共通値で所持集合を置換する、変更不能な入力です。
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
        IEnumerable<ChartFile> bmsRows,
        IEnumerable<ChartFile> bmsonRows,
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
    internal IReadOnlyList<ChartFile> BmsRows { get; }

    /// <summary>immutable BMSON replacement input。</summary>
    internal IReadOnlyList<ChartFile> BmsonRows { get; }

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
/// 共通集合の置換後に確定した版と公開対象の事実です。
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
        OwnedChartCollectionVersionSnapshot storageRowsVersion,
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

    internal OwnedChartCollectionVersionSnapshot StorageRowsVersion { get; }

    internal int OwnedCollectionVersion { get; }
}

/// <summary>
/// 導入対象の共通現在値と保存変更を捕捉した不変要求です。
/// </summary>
internal sealed class CatalogInstalledTargetUpsertRequest
{
    /// <summary>
    /// 確定destinationを持つtargetをrequestへ固定します。
    /// </summary>
    /// <param name="targets">確定先とdetachedな共通値を保持する導入対象。</param>
    /// <param name="previousOwnedCollectionVersion">要求作成時の共通集合版。</param>
    /// <param name="installPathsToDelete">同じtransactionで削除するpending install rowのpath。</param>
    /// <param name="installRowsToUpsert">同じtransactionでupsertするpending install row。</param>
    internal CatalogInstalledTargetUpsertRequest(ChartStorageTargetSet targets, int previousOwnedCollectionVersion, IEnumerable<string> installPathsToDelete = null, IEnumerable<ChartPackage> installRowsToUpsert = null)
    {
        Targets = targets ?? ChartStorageTargetSet.FromCharts([]);
        BmsCharts = Targets.BmsCharts;
        BmsonCharts = Targets.BmsonCharts;
        PreviousOwnedCollectionVersion = previousOwnedCollectionVersion;

        InstallPathsToDelete = SnapshotInstallPaths(installPathsToDelete);
        InstallRowsToUpsert = SnapshotInstallRows(installRowsToUpsert);
        AddedCharts = CatalogChartMutationFact.CreateFacts(Targets.Charts);
    }

    /// <summary>確定した共通の保存変更事実です。保存行を保持しません。</summary>
    internal ChartStorageTargetSet Targets { get; }

    internal IReadOnlyList<ChartFile> BmsCharts { get; }

    internal IReadOnlyList<ChartFile> BmsonCharts { get; }

    internal int PreviousOwnedCollectionVersion { get; }


    /// <summary>installed target upsert と同じ transaction で削除する pending install row path。</summary>
    internal IReadOnlyList<string> InstallPathsToDelete { get; }

    /// <summary>installed target upsert と同じ transaction で upsert する pending install row。</summary>
    internal IReadOnlyList<ChartPackage> InstallRowsToUpsert { get; }

    internal IReadOnlyList<CatalogChartMutationFact> AddedCharts { get; }

    private static IReadOnlyList<T> Snapshot<T>(IEnumerable<T> values)
    {
        return Array.AsReadOnly([.. values ?? []]);
    }

    private static IReadOnlyList<string> SnapshotInstallPaths(IEnumerable<string> values)
    {
        return Array.AsReadOnly((values ?? [])
            .Where(path => !string.IsNullOrWhiteSpace(path))
            .Distinct(StringComparer.Ordinal)
            .ToArray());
    }

    private static IReadOnlyList<ChartPackage> SnapshotInstallRows(IEnumerable<ChartPackage> values)
    {
        return Array.AsReadOnly((values ?? [])
            .Where(package => package != null && !string.IsNullOrWhiteSpace(package.path))
            .GroupBy(package => package.path, StringComparer.Ordinal)
            .Select(group => group.Last())
            .ToArray());
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
        OwnedChartCollectionVersionSnapshot storageRowsVersion,
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

    internal OwnedChartCollectionVersionSnapshot StorageRowsVersion { get; }

    internal int OwnedCollectionVersion { get; }

    internal IReadOnlyList<CatalogChartMutationFact> AddedCharts { get; }

    internal bool InstallRowDeleted { get; }

    /// <summary>今回のupsertで初回BMSON canonical順序正規化が発生したか。</summary>
    internal bool BmsonCanonicalOrderNormalized { get; }
}

/// <summary>
/// 共通譜面の旧新ハッシュを捕捉する、変更不能な入力です。
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
/// ハッシュ変更と共通集合の適用後に確定した事実です。
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
        OwnedChartCollectionVersionSnapshot storageRowsVersion,
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

    internal OwnedChartCollectionVersionSnapshot StorageRowsVersion { get; }

    internal int OwnedCollectionVersion { get; }

    internal IReadOnlyList<LibraryChartDigestChange> DigestChanges { get; }
}

/// <summary>
/// 差分走査の所属・共通現在値・削除対象を固定した不変要求です。
/// </summary>
internal sealed class CatalogFileScanStorageReplacementRequest
{
    internal CatalogFileScanStorageReplacementRequest(
        bool hasDbDiff,
        IEnumerable<ChartFile> nextBmsRows,
        IEnumerable<ChartFile> nextBmsonRows,
        IEnumerable<string> deletedBmsPaths,
        IEnumerable<string> deletedBmsonPaths,
        IEnumerable<ChartFile> addedBmsFiles,
        IEnumerable<ChartFile> addedBmsonSongs,
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
        AddedCharts = CatalogChartMutationFact.CreateFacts(AddedBmsFiles.Concat(AddedBmsonSongs));
        RemovedPayloadAvailable = removedPayloadAvailable;
        RemovedCharts = Snapshot(removedCharts);
    }

    internal bool HasDbDiff { get; }

    internal IReadOnlyList<ChartFile> NextBmsRows { get; }

    internal IReadOnlyList<ChartFile> NextBmsonRows { get; }

    internal IReadOnlyList<string> DeletedBmsPaths { get; }

    internal IReadOnlyList<string> DeletedBmsonPaths { get; }

    internal IReadOnlyList<ChartFile> AddedBmsFiles { get; }

    internal IReadOnlyList<ChartFile> AddedBmsonSongs { get; }

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
        OwnedChartCollectionVersionSnapshot versions,
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

    internal OwnedChartCollectionVersionSnapshot StorageRowsVersion { get; }

    /// <summary>
    /// 差分走査の共通現在値適用時点で確定した集合版です。
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

/// <summary>永続確定した共通譜面の対象事実です。項目削除は所持token、パス整理は明示したパスを使います。</summary>
/// <param name="kind">確定した譜面形式。</param>
/// <param name="path">確定時に捕捉したDBパス。</param>
/// <param name="md5">捕捉したMD5。</param>
/// <param name="sha256">捕捉したSHA256。</param>
/// <param name="removalMode">項目削除とパス整理の区別。削除以外の事実はnull。</param>
/// <param name="token">厳密な項目削除で継承する所持識別。パス整理はnull。</param>
internal sealed class CatalogChartMutationFact(
    ChartFileKind kind,
    string path,
    string md5,
    string sha256,
    OwnedChartRemoveMode? removalMode = null,
    OwnedChartToken token = null)
{
    /// <summary>厳密な項目削除の所持識別です。パス整理の要求はnullです。</summary>
    internal OwnedChartToken Token { get; } = token;

    internal ChartFileKind Kind { get; } = kind;

    internal string Path { get; } = path;

    internal string Md5 { get; } = md5;

    internal string Sha256 { get; } = sha256;

    internal OwnedChartRemoveMode? RemovalMode { get; } = removalMode;

    internal static CatalogChartMutationFact FromChart(ChartFile chart)
    {
        return new CatalogChartMutationFact(chart.Kind, chart.Path, chart.Md5, chart.Sha256, token: chart.Token);
    }

    internal static IReadOnlyList<CatalogChartMutationFact> CreateFacts(IEnumerable<ChartFile> charts)
    {
        return Array.AsReadOnly((charts ?? [])
            .Where(chart => chart != null)
            .Select(FromChart)
            .ToArray());
    }

    /// <summary>
    /// 削除要求が破壊前に保持したtoken、kind、path、digestをcatalog mutation factへ変換します。
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
            request.Mode,
            request.Token);
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
