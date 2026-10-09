using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace BeMusicSeeker.Models.BmsLibraryInternal;

internal sealed class Lr2SongDbSyncRuntimeSnapshot
{
    public bool Running { get; set; }

    public int RequestedVersion { get; set; }

    public string Stage { get; set; } = string.Empty;

    public int ProcessedCount { get; set; }

    public int TotalCount { get; set; }

    public int StageProcessedCount { get; set; }

    public int StageTotalCount { get; set; }
}

internal static class Lr2SongDbSyncRequestCoordinator
{
    /// <summary>準備から状態保存とcleanupの実終端まで共通受付を保持して同期します。</summary>
    /// <param name="capability">受理済み必須継続の共通権限。nullは新規受付。</param>
    /// <param name="acceptedBackground">外側起動登録の識別。全体同期の新規受付は背景でも非待機です。</param>
    /// <param name="capturePreparationInputs">受付取得後、非同期準備へ渡す前に小さい生成設定入力を捕捉する処理。</param>
    /// <returns>実終端後の同期状態。開始競合は副作用と警告を行わず現在表示を返し、admissionResultへfalseを渡します。</returns>
    internal static async Task<Lr2SongDbSyncStatusSnapshot> QueueAsync(
        BMSLibrary.Lr2SynchronizationOwner host, string reason, bool force,
        Func<LibraryFileMutationLease, BmsLibraryOptionsSnapshot, Lr2SongDbSyncPreparedDataSurface> prepareGeneratedData,
        bool allowIncompleteToQueue, bool allowCommittedPathReceipt,
        LibraryFileMutationCapability capability = null, bool acceptedBackground = false,
        BmsLibraryOptionsSnapshot optionsSnapshot = null, Action<LibraryFileMutationCapability> capturePreparationInputs = null, LibraryFileMutationCapability playlistCapability = null, Action<bool> admissionResult = null)
    {
        playlistCapability ??= capability?.PlaylistCapability;
        LibraryFileMutationLease lease = capability != null
            ? host.TryBeginMutation(reason, capability: capability)
            : host.TryBeginMutation(reason, showMessage: false);
        if (lease == null) { admissionResult?.Invoke(false); return host.GetLr2SongDbSyncStatusSnapshot(); }
        using (lease)
        {
            LibraryFileMutationLease playlistLease;
            if (playlistCapability != null) { playlistLease = host.PlaylistOperationAdmission.Borrow(playlistCapability); }
            else if (host.PlaylistOperationAdmission.TryEnter(out IDisposable acquired)) { playlistLease = (LibraryFileMutationLease)acquired; }
            else { admissionResult?.Invoke(false); return host.GetLr2SongDbSyncStatusSnapshot(); }
            admissionResult?.Invoke(true);
            using LibraryFileMutationLease playlistOperation = playlistLease;
            using LibraryFileMutationCapability playlistAuthority = playlistOperation.CreateMutationCapability();
            using LibraryFileMutationCapability primaryAuthority = lease.CreateMutationCapability();
            using LibraryFileMutationCapability authority = primaryAuthority.WithPlaylistCapability(playlistAuthority);
            using LibraryFileMutationLease preparationLease = host.OperationAdmission.Borrow(authority);
            bool receiptEligible = allowCommittedPathReceipt && !force;
            if (!receiptEligible) { host.DiscardLr2SongDbSyncCommittedPathReceipt("queue_origin_not_eligible"); }
            BmsLibraryOptionsSnapshot options = optionsSnapshot ?? host.CurrentOptionsSnapshot;
            string signature = Lr2SongDbSyncSignatureBuilder.Build(options);
            Lr2SongDbSyncStatusSnapshot status = host.EvaluateLr2SongDbSyncStatus(options.OperationModeLR2DB, signature, DateTime.UtcNow);
            host.PublishLr2SongDbSyncStatus(status);
            if (host.TrySkipForShutdown("lr2_song_db_sync", reason)) { host.DiscardLr2SongDbSyncCommittedPathReceipt("shutdown_requested"); return status; }
            if (!options.OperationModeLR2DB || (!force && !status.IsNeeded)
                || (!force && !allowIncompleteToQueue && (status.Status == Lr2SongDbSyncStatusKind.Incomplete || status.StoredStatus == Lr2SongDbSyncStatusKind.Incomplete)))
            {
                host.DiscardLr2SongDbSyncCommittedPathReceipt("queue_not_needed");
                host.ClearLr2SongDbSyncPreparedDataSurface("queue_not_needed");
                return status;
            }
            host.PublishLr2SongDbSyncStatus(CreateRuntimeLr2SongDbSyncStatus(Lr2SongDbSyncStatusKind.Running, signature,
                prepareGeneratedData != null ? "preparing" : "queued", 0, 0, null, 0, 0));
            int version = host.BeginRequest(authority);
            try
            {
                if (prepareGeneratedData != null)
                {
                    capturePreparationInputs?.Invoke(playlistAuthority);
                    Lr2SongDbSyncPreparedDataSurface surface = await Task.Run(() => prepareGeneratedData(preparationLease, options)).ConfigureAwait(false);
                    host.ApplyLr2SongDbSyncPreparedDataSurface("prepare_generated_data", surface, options);
                }
            }
            catch (Exception ex)
            {
                host.DiscardLr2SongDbSyncCommittedPathReceipt("prepare_failed");
                host.ClearLr2SongDbSyncPreparedDataSurface("prepare_failed");
                host.FailLr2SongDbSyncRequest(version, Lr2SongDbSyncStatusKind.Failed, "preparation_failed", ex.Message);
                throw;
            }
            // 共通受付の内側で起動枠を待つと、その枠で受付を待つ必須保守と循環する。
            await Task.Run(() => host.RunLr2SongDbSyncAsync(reason, signature, version, receiptEligible, authority, options)).ConfigureAwait(false);
            return host.GetLr2SongDbSyncStatusSnapshot();
        }
    }

    /// <summary>受理済み操作の設定で実同期し、状態保存・後片付け後に失敗と取消を伝播します。受付は呼出し元が保持します。</summary>
    /// <param name="options">署名と準備から継続して渡す変更不能設定。</param>
    internal static void Run(
        BMSLibrary.Lr2SynchronizationOwner host,
        string reason,
        string signature,
        int requestVersion,
        bool allowCommittedPathReceipt,
        BmsLibraryOptionsSnapshot options)
    {
        var stopwatch = Stopwatch.StartNew();
        string runId = Guid.NewGuid().ToString("N");
        CancellationToken cancellationToken = host.GetLr2SongDbSyncCancellationToken();
        int projectedCompatibilityWarningCount = 0;
        int committedChartInfoRowCount = 0;
        int committedChartInfoParseFailureChangeCount = 0;
        var committedCompatibilityFacts = new List<ResourceHealthMaintenanceSnapshot>();
        object committedCompatibilityFactsGate = new();
        var changedChartInfoMd5s = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        bool compatibilityProjectionCompleted = false;
        Dictionary<string, ChartFile> compatibilityProjectionIndex = null;
        Action applyCommittedCompatibilityProjection = () =>
        {
            if (compatibilityProjectionCompleted)
            {
                return;
            }

            List<ResourceHealthMaintenanceSnapshot> compatibilityFactsSnapshot;
            lock (committedCompatibilityFactsGate)
            {
                compatibilityFactsSnapshot = [.. committedCompatibilityFacts];
            }
            if (compatibilityFactsSnapshot.Count > 0)
            {
                projectedCompatibilityWarningCount = host.ApplyLr2SongDbSyncCompatibilityProjection(
                    compatibilityFactsSnapshot,
                    reason,
                    compatibilityProjectionIndex,
                    logSummary: false,
                    dispatchPresentation: false);
            }
            compatibilityProjectionCompleted = true;
        };

        try
        {
            if (host.IsShutdownRequested || cancellationToken.IsCancellationRequested)
            {
                host.DiscardLr2SongDbSyncCommittedPathReceipt("shutdown_requested");
                host.CompleteLr2SongDbSyncRequest(requestVersion, "shutdown_skipped");
                host.ReportStartupBackgroundTask("lr2_song_db_sync", "skipped", stopwatch.ElapsedMilliseconds, failed: false, detail: "shutdown_requested");
                host.LogInstallPerformance("lr2_song_db_sync skipped version=" + requestVersion + " reason=shutdown_requested");
                return;
            }
            host.ReportStartupBackgroundTask("lr2_song_db_sync", "start", 0L, failed: false, detail: "runId=" + runId);
            host.PublishLr2SongDbSyncPreflightStage("chart_info_hydration", reason, runId);
            var preflightStageStopwatch = Stopwatch.StartNew();
            host.EnsureLr2SongDbSyncChartInfoIndexHydrated(reason);
            host.LogLr2SongDbSyncPreflightStageDone("chart_info_hydration", reason, runId, preflightStageStopwatch.ElapsedMilliseconds);
            cancellationToken.ThrowIfCancellationRequested();

            host.PublishLr2SongDbSyncPreflightStage("input_surface", reason, runId);
            preflightStageStopwatch.Restart();
            Lr2SongDbSyncInput input = host.CreateLr2SongDbSyncInput(options);
            host.LogLr2SongDbSyncPreflightStageDone("input_surface", reason, runId, preflightStageStopwatch.ElapsedMilliseconds);
            cancellationToken.ThrowIfCancellationRequested();
            Lr2SongDbSyncCommittedPathReceipt committedPathReceipt = allowCommittedPathReceipt
                ? host.TakeLr2SongDbSyncCommittedPathReceipt(input, reason)
                : null;

            host.PublishLr2SongDbSyncPreflightStage("compatibility_projection_index", reason, runId);
            preflightStageStopwatch.Restart();
            compatibilityProjectionIndex = host.CreateLr2SongDbSyncCompatibilityProjectionIndex();
            host.LogLr2SongDbSyncPreflightStageDone("compatibility_projection_index", reason, runId, preflightStageStopwatch.ElapsedMilliseconds);
            cancellationToken.ThrowIfCancellationRequested();

            host.PublishLr2SongDbSyncPreflightStage("chart_info_resolver_snapshot", reason, runId);
            preflightStageStopwatch.Restart();
            Func<ChartFile, BeMusicSeeker.Models.ChartDetails> chartInfoResolver = host.CreateLr2SongDbSyncChartInfoResolverSnapshot();
            HashSet<string> currentChartInfoParseFailureMd5s = host.CreateLr2SongDbSyncCurrentChartInfoParseFailureMd5Snapshot(reason);
            host.LogLr2SongDbSyncPreflightStageDone("chart_info_resolver_snapshot", reason, runId, preflightStageStopwatch.ElapsedMilliseconds);
            cancellationToken.ThrowIfCancellationRequested();

            Lr2SongDbSyncResult result;
            result = host.ApplyLr2SongDbSync(new Lr2SongDbSyncRequest
            {
                Signature = signature,
                RunId = runId,
                RootDirectories = input.RootDirectories,
                ChartPaths = input.ChartPaths,
                NormalFolderDirectoryPaths = input.NormalFolderDirectoryPaths,
                FolderInfoFilePaths = input.FolderInfoFilePaths,
                FolderInfoFileEntries = input.FolderInfoFileEntries,
                DirectoryEntries = input.DirectoryEntries,
                Lr2FolderFilePaths = input.Lr2FolderFilePaths,
                Lr2FolderFileEntries = input.Lr2FolderFileEntries,
                Lr2FolderDiscoveryDirectories = input.Lr2FolderDiscoveryDirectories,
                Lr2FolderPruneDirectories = input.Lr2FolderPruneDirectories,
                Lr2RootPath = input.Lr2RootPath,
                Lr2NormalCustomFolderOutputBaseDir = input.Lr2NormalCustomFolderOutputBaseDir,
                Lr2AdditionalNormalCustomFolderOutputBaseDirs = input.Lr2AdditionalNormalCustomFolderOutputBaseDirs,
                Lr2RootCustomFolderOutputBaseDir = input.Lr2RootCustomFolderOutputBaseDir,
                Lr2BuiltinFolderSourceDirectories = input.Lr2BuiltinFolderSourceDirectories,
                Lr2FolderFileDiscoveryComplete = input.Lr2FolderFileDiscoveryComplete,
                SongRows = input.SongRows,
                TextFileDirectories = input.TextFileDirectories,
                ChartInfoResolver = chartInfoResolver,
                ChartInfoResolverIsThreadSafe = true,
                ChartInfoParseTimeout = host.CurrentChartInfoParseTimeout,
                CurrentChartInfoParseFailureMd5s = currentChartInfoParseFailureMd5s,
                ChartInfoRowsCommitted = rows =>
                {
                    int count = rows?.Count ?? 0;
                    if (count > 0)
                    {
                        host.UpsertLr2SongDbSyncChartInfoIndexRows(rows);
                        lock (committedCompatibilityFactsGate)
                        {
                            changedChartInfoMd5s.UnionWith(rows.Select(row => row.md5).Where(hash => !string.IsNullOrWhiteSpace(hash)));
                        }
                        Interlocked.Add(ref committedChartInfoRowCount, count);
                    }
                },
                ChartInfoParseFailuresCommitted = (persisted, cleared, hashes) =>
                {
                    int count = Math.Max(0, persisted) + Math.Max(0, cleared);
                    if (count > 0)
                    {
                        Interlocked.Add(ref committedChartInfoParseFailureChangeCount, count);
                        lock (committedCompatibilityFactsGate)
                        {
                            changedChartInfoMd5s.UnionWith(hashes ?? []);
                        }
                    }
                },
                StartedAtUtc = DateTime.UtcNow,
                CancellationToken = cancellationToken,
                IsShutdownRequested = () => host.IsShutdownRequested,
                IsSourceCurrent = () => host.IsLr2SongDbSyncInputCurrent(input),
                ProgressReporter = host.UpdateLr2SongDbSyncProgress,
                Lr2CompatibilityFactsCommitted = infos =>
                {
                    if (infos == null || infos.Count == 0)
                    {
                        return;
                    }
                    lock (committedCompatibilityFactsGate)
                    {
                        committedCompatibilityFacts.AddRange(infos.Where(info => info != null));
                    }
                },
                CommittedPathReceipt = committedPathReceipt,
                LogInstallPerformance = host.LogInstallPerformance
            });
            applyCommittedCompatibilityProjection();
            stopwatch.Stop();
            bool completed = string.Equals(result.FinalStage, Lr2SongDbSyncService.CompletedStage, StringComparison.Ordinal);
            host.LogInstallPerformance("lr2_song_db_sync " + (completed ? "completed" : "incomplete")
                + " reason=" + (reason ?? "unknown")
                + " runId=" + runId
                + " roots=" + input.RootDirectories.Count
                + " charts=" + input.ChartPaths.Count
                + " normalFolderDirs=" + input.NormalFolderDirectoryPaths.Count
                + " folderInfoCandidates=" + input.FolderInfoFilePaths.Count
                + " lr2FolderRoots=" + input.Lr2FolderDiscoveryDirectories.Count
                + " lr2FolderPruneRoots=" + input.Lr2FolderPruneDirectories.Count
                + " lr2FolderCandidates=" + input.Lr2FolderFilePaths.Count
                + " lr2FolderDiscoveryComplete=" + input.Lr2FolderFileDiscoveryComplete.ToString().ToLowerInvariant()
                + " textFileDirs=" + input.TextFileDirectories.Count
                + " songRows=" + input.SongRows.Count
                + " folderTableGenerated=" + (result.FolderTableReconciliationResult?.GeneratedCount ?? 0)
                + " folderTableUpserted=" + (result.FolderTableReconciliationResult?.UpsertedCount ?? 0)
                + " folderTableDeleted=" + (result.FolderTableReconciliationResult?.DeletedCount ?? 0)
                + " folderTableExistingRows=" + (result.FolderTableReconciliationResult?.ExistingRowCount ?? 0)
                + " lr2FolderProcessed=" + result.Lr2FolderFileProcessedCount
                + " songRowProcessed=" + result.SongRowProcessedCount
                + " songRowSkipped=" + result.SongRowSkippedCount
                + " songRowParseFailed=" + result.SongRowParseFailureCount
                + " songRowChartInfoApplied=" + result.SongRowChartInfoAppliedCount
                + " songRowLr2CompatibilityApplied=" + result.SongRowLr2CompatibilityAppliedCount
                + " processed=" + result.ProcessedCount
                + " total=" + result.TotalCount
                + " stage=" + result.FinalStage
                + " detail=" + (result.IncompleteReason ?? "completed")
                + " elapsedMs=" + stopwatch.ElapsedMilliseconds);
            host.LogInstallPerformance("lr2_song_db_sync_compatibility_projection applied"
                + " reason=" + (reason ?? "unknown")
                + " input=" + result.SongRowLr2CompatibilityAppliedCount
                + " applied=" + projectedCompatibilityWarningCount);
            host.LogInstallPerformance("lr2_song_db_sync_chart_info_projection applied"
                + " reason=" + (reason ?? "unknown")
                + " rows=" + committedChartInfoRowCount
                + " parseFailureChanges=" + committedChartInfoParseFailureChangeCount);
            if (committedChartInfoRowCount > 0 || committedChartInfoParseFailureChangeCount > 0)
            {
                host.DispatchWarningPresentationChanged("lr2_song_db_sync_inline_chart_info", [.. changedChartInfoMd5s]);
            }
            if (projectedCompatibilityWarningCount > 0)
            {
                host.DispatchWarningPresentationChanged("lr2_song_db_sync_compatibility_projection",
                    [.. committedCompatibilityFacts.Select(value => value.Hash).Where(hash => !string.IsNullOrWhiteSpace(hash)).Distinct(StringComparer.OrdinalIgnoreCase)]);
            }
            if (completed)
            {
                host.CompleteLr2SongDbSyncRequest(requestVersion, result.FinalStage);
            }
            else
            {
                host.FailLr2SongDbSyncRequest(requestVersion, Lr2SongDbSyncStatusKind.Incomplete, result.FinalStage, BuildLr2SongDbSyncIncompleteDetail(result));
            }
            host.ReportStartupBackgroundTask("lr2_song_db_sync", completed ? "done" : "incomplete", stopwatch.ElapsedMilliseconds, failed: false, detail: completed ? "completed" : BuildLr2SongDbSyncIncompleteDetail(result));
        }
        catch (OperationCanceledException cancellationException)
        {
            host.DiscardLr2SongDbSyncCommittedPathReceipt("sync_cancelled");
            try
            {
                applyCommittedCompatibilityProjection();
            }
            catch (Exception projectionException)
            {
                host.LogInstallPerformance("lr2_song_db_sync compatibility_projection_after_cancel_failed"
                    + " reason=" + (reason ?? "unknown")
                    + " exception=" + projectionException.GetType().Name
                    + " message=" + projectionException.Message);
            }
            stopwatch.Stop();
            Lr2SongDbSyncRuntimeSnapshot runtimeSnapshot = host.GetLr2SongDbSyncRuntimeSnapshot();
            bool shutdownInterruption = host.IsShutdownRequested;
            if (shutdownInterruption)
            {
                host.MarkLr2SongDbSyncInterruptedStatus(
                    signature,
                    runId,
                    runtimeSnapshot.ProcessedCount,
                    runtimeSnapshot.TotalCount,
                    runtimeSnapshot.Stage);
                host.LogInstallPerformance("lr2_song_db_sync interrupted reason=" + (reason ?? "unknown")
                    + " runId=" + runId
                    + " elapsedMs=" + stopwatch.ElapsedMilliseconds
                    + " stage=" + (runtimeSnapshot.Stage ?? string.Empty)
                    + " processed=" + runtimeSnapshot.ProcessedCount
                    + " total=" + runtimeSnapshot.TotalCount);
                host.FailLr2SongDbSyncRequest(
                    requestVersion,
                    Lr2SongDbSyncStatusKind.Incomplete,
                    runtimeSnapshot.Stage,
                    "shutdown_interrupted");
                host.ReportStartupBackgroundTask("lr2_song_db_sync", "incomplete", stopwatch.ElapsedMilliseconds, failed: false, detail: "shutdown_interrupted");
                throw;
            }
            host.MarkLr2SongDbSyncFailedStatus(signature, runId, cancellationException);
            host.LogInstallPerformance("lr2_song_db_sync cancellation_failed reason=" + (reason ?? "unknown")
                + " runId=" + runId
                + " elapsedMs=" + stopwatch.ElapsedMilliseconds
                + " stage=" + (runtimeSnapshot.Stage ?? string.Empty)
                + " processed=" + runtimeSnapshot.ProcessedCount
                + " total=" + runtimeSnapshot.TotalCount);
            host.FailLr2SongDbSyncRequest(requestVersion, Lr2SongDbSyncStatusKind.Failed, runtimeSnapshot.Stage, cancellationException.Message);
            host.ReportStartupBackgroundTask("lr2_song_db_sync", "failed", stopwatch.ElapsedMilliseconds, failed: true, detail: cancellationException.Message);
            throw;
        }
        catch (Exception ex)
        {
            host.DiscardLr2SongDbSyncCommittedPathReceipt("sync_failed");
            try
            {
                applyCommittedCompatibilityProjection();
            }
            catch (Exception projectionException)
            {
                host.LogInstallPerformance("lr2_song_db_sync compatibility_projection_after_failure_failed"
                    + " reason=" + (reason ?? "unknown")
                    + " exception=" + projectionException.GetType().Name
                    + " message=" + projectionException.Message);
            }
            stopwatch.Stop();
            try
            {
                host.MarkLr2SongDbSyncFailedStatus(signature, runId, ex);
            }
            catch
            {
                // Preserve the original failure in the startup task report.
            }
            host.LogInstallPerformance("lr2_song_db_sync failed reason=" + (reason ?? "unknown")
                + " runId=" + runId
                + " elapsedMs=" + stopwatch.ElapsedMilliseconds
                + " exception=" + ex.GetType().Name
                + " message=" + ex.Message);
            host.FailLr2SongDbSyncRequest(requestVersion, Lr2SongDbSyncStatusKind.Failed, "failed", ex.Message);
            host.ReportStartupBackgroundTask("lr2_song_db_sync", "failed", stopwatch.ElapsedMilliseconds, failed: true, detail: ex.Message);
            throw;
        }
    }



    /// <summary>
    /// 事前準備の実段階と対象件数を公開します。保存済みカーソルを持たないため、全体の保存位置は指定しません。
    /// </summary>
    internal static void PublishExternalStageProgress(
        BMSLibrary.Lr2SynchronizationOwner host,
        string stage,
        int processedCount,
        int totalCount,
        string detail)
    {
        BmsLibraryOptionsSnapshot options = host.CurrentOptionsSnapshot;
        bool enabled = options.OperationModeLR2DB;
        if (!enabled)
        {
            return;
        }

        string signature = Lr2SongDbSyncSignatureBuilder.Build(options);
        int safeTotal = Math.Max(0, totalCount);
        int safeProcessed = Math.Max(0, Math.Min(Math.Max(0, processedCount), safeTotal));
        host.PublishLr2SongDbSyncStatus(CreateRuntimeLr2SongDbSyncStatus(
            Lr2SongDbSyncStatusKind.Running,
            signature,
            stage: stage,
            processedCursor: null,
            totalCount: null,
            lastError: detail,
            stageProcessedCount: safeProcessed,
            stageTotalCount: safeTotal));
    }

    private static string BuildLr2SongDbSyncIncompleteDetail(Lr2SongDbSyncResult result)
    {
        if (result == null)
        {
            return string.Empty;
        }

        return result.IncompleteReason ?? result.FinalStage ?? string.Empty;
    }

    private static Lr2SongDbSyncStatusSnapshot CreateRuntimeLr2SongDbSyncStatus(
        Lr2SongDbSyncStatusKind status,
        string signature,
        string stage,
        int? processedCursor,
        int? totalCount,
        string lastError,
        int? stageProcessedCount = null,
        int? stageTotalCount = null)
    {
        return new Lr2SongDbSyncStatusSnapshot
        {
            Status = status,
            StoredStatus = status,
            Signature = signature ?? string.Empty,
            Stage = stage ?? string.Empty,
            ProcessedCursor = processedCursor,
            TotalCount = totalCount,
            StageProcessedCount = stageProcessedCount,
            StageTotalCount = stageTotalCount,
            LastError = lastError ?? string.Empty,
            UpdatedAt = DateTime.UtcNow
        };
    }
}
