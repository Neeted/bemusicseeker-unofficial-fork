using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Runtime.ExceptionServices;
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
    /// <param name="admissionResult">実L/P取得と終了・実行要否検査後の受付結果。保存済みCompletedによる正常省略もtrueです。成功通知の例外でも両受付を解放し、準備せずに元失敗を伝播します。</param>
    /// <returns>実終端後の同期状態。開始競合は副作用と警告を行わず現在表示を返します。受付拒否・終了・モード外などの不成立はadmissionResultへfalseを渡します。正常省略は保存済みCompletedを保持します。</returns>
    internal static async Task<Lr2SongDbSyncStatusSnapshot> QueueAsync(
        BMSLibrary.Lr2SynchronizationOwner host, string reason, bool force,
        Func<LibraryFileMutationLease, BmsLibraryOptionsSnapshot, Lr2SongDbSyncPreparedDataSurface> prepareGeneratedData,
        LibraryFileInitializationResult initializationResult,
        LibraryFileMutationCapability capability = null, bool acceptedBackground = false,
        BmsLibraryOptionsSnapshot optionsSnapshot = null, Action<LibraryFileMutationCapability> capturePreparationInputs = null, LibraryFileMutationCapability playlistCapability = null, Action<bool> admissionResult = null, Lr2SongDbSyncPreparedDataSurface preparedSurface = null, OperationProgressRequest originatingRequest = null)
    {
        playlistCapability ??= capability?.PlaylistCapability;
        LibraryFileMutationLease lease = capability != null
            ? host.TryBeginMutation(reason, capability: capability)
            : host.TryBeginMutation(reason);
        if (lease == null) { admissionResult?.Invoke(false); return host.GetLr2SongDbSyncStatusSnapshot(); }
        using (lease)
        {
            LibraryFileMutationLease playlistLease;
            if (playlistCapability != null) { playlistLease = host.PlaylistOperationAdmission.Borrow(playlistCapability); }
            else if (host.PlaylistOperationAdmission.TryEnter(out IDisposable acquired)) { playlistLease = (LibraryFileMutationLease)acquired; }
            else { admissionResult?.Invoke(false); return host.GetLr2SongDbSyncStatusSnapshot(); }
            using LibraryFileMutationLease playlistOperation = playlistLease;
            using LibraryFileMutationCapability playlistAuthority = playlistOperation.CreateMutationCapability();
            using LibraryFileMutationCapability primaryAuthority = lease.CreateMutationCapability();
            using LibraryFileMutationCapability authority = primaryAuthority.WithPlaylistCapability(playlistAuthority);
            using LibraryFileMutationLease preparationLease = host.OperationAdmission.Borrow(authority);
            BmsLibraryOptionsSnapshot options = optionsSnapshot ?? host.CurrentOptionsSnapshot;
            Lr2SongDbSyncStatusSnapshot status = host.EvaluateLr2SongDbSyncStatus(options.OperationModeLR2DB, DateTime.UtcNow);
            host.PublishLr2SongDbSyncStatus(status);
            if (host.TrySkipForShutdown("lr2_song_db_sync", reason)) { admissionResult?.Invoke(false); return status; }
            if (!force && options.OperationModeLR2DB && status.Status == Lr2SongDbSyncStatusKind.Completed)
            {
                admissionResult?.Invoke(true);
                return status;
            }
            if (!options.OperationModeLR2DB || (!force && !status.IsNeeded))
            {
                admissionResult?.Invoke(false);
                return status;
            }
            admissionResult?.Invoke(true);
            int version = host.BeginRequest(authority, originatingRequest,
                CreateRuntimeLr2SongDbSyncStatus(Lr2SongDbSyncStatusKind.Running,
                    prepareGeneratedData != null ? "preparing" : "queued", 0, 0, null, 0, 0));
            try
            {
                if (prepareGeneratedData != null)
                {
                    capturePreparationInputs?.Invoke(playlistAuthority);
                    preparedSurface = await Task.Run(() => prepareGeneratedData(preparationLease, options)).ConfigureAwait(false);
                }
            }
            catch (Exception ex)
            {
                host.RecordLr2SongDbSyncFailure(string.Empty, version, ex, "preparation_failed",
                    interruptedByShutdown: ex is OperationCanceledException && host.IsShutdownRequested);
                throw;
            }
            // 共通受付の内側で起動枠を待つと、その枠で受付を待つ必須保守と循環する。
            await Task.Run(() => host.RunLr2SongDbSyncAsync(reason, version, initializationResult, preparedSurface, authority, options)).ConfigureAwait(false);
            return host.GetLr2SongDbSyncStatusSnapshot();
        }
    }

    /// <summary>受理済み操作の設定で実同期し、状態保存・後片付け後に失敗と取消を伝播します。必須投影を一度試み、所有token確認後に完了を保存します。同時終了でも投影故障はFailedに残します。受付は呼出し元が保持します。</summary>
    /// <param name="options">準備から継続して渡す変更不能設定。</param>
    internal static void Run(
        BMSLibrary.Lr2SynchronizationOwner host,
        string reason,
        int requestVersion,
        LibraryFileInitializationResult initializationResult,
        Lr2SongDbSyncPreparedDataSurface preparedSurface,
        BmsLibraryOptionsSnapshot options)
    {
        var stopwatch = Stopwatch.StartNew();
        string runId = Guid.NewGuid().ToString("N");
        CancellationToken cancellationToken = host.GetLr2SongDbSyncCancellationToken();
        int projectedCurrentValueCount = 0;
        int committedChartInfoRowCount = 0;
        int committedChartInfoParseFailureChangeCount = 0;
        var committedCurrentFacts = new List<Lr2SongDbSyncCurrentFact>();
        object committedCurrentFactsGate = new();
        var changedChartInfoMd5s = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        bool currentProjectionAttempted = false;
        Exception currentProjectionFailure = null;
        Action applyCommittedCurrentProjection = () =>
        {
            if (currentProjectionAttempted)
            {
                return;
            }

            currentProjectionAttempted = true;
            List<Lr2SongDbSyncCurrentFact> currentFactsSnapshot;
            lock (committedCurrentFactsGate)
            {
                currentFactsSnapshot = [.. committedCurrentFacts];
            }
            try
            {
                if (currentFactsSnapshot.Count > 0)
                {
                    projectedCurrentValueCount = host.ApplyLr2SongDbSyncCurrentProjection(
                        currentFactsSnapshot,
                        reason,
                        logSummary: false,
                        dispatchPresentation: false);
                }
            }
            catch (Exception projectionException)
            {
                currentProjectionFailure = projectionException is OperationCanceledException
                    ? new InvalidOperationException("A required LR2 current-value projection failed.", projectionException)
                    : projectionException;
                ExceptionDispatchInfo.Capture(currentProjectionFailure).Throw();
            }
        };

        try
        {
            if (host.IsShutdownRequested || cancellationToken.IsCancellationRequested)
            {
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
            Lr2SongDbSyncInput input = host.CreateLr2SongDbSyncInput(options, initializationResult, preparedSurface);
            host.LogLr2SongDbSyncPreflightStageDone("input_surface", reason, runId, preflightStageStopwatch.ElapsedMilliseconds);
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
                RunId = runId,
                RootDirectories = input.RootDirectories,
                ChartPaths = input.ChartPaths,
                NormalFolderDirectoryPaths = input.NormalFolderDirectoryPaths,
                FolderInfoFilePaths = input.FolderInfoFilePaths,
                FolderInfoFileEntries = input.FolderInfoFileEntries,
                DirectoryEntries = input.DirectoryEntries,
                Lr2FolderFilePaths = input.Lr2FolderFilePaths,
                Lr2FolderFileEntries = input.Lr2FolderFileEntries,
                ManagedFolderDefinitions = input.ManagedFolderDefinitions,
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
                        lock (committedCurrentFactsGate)
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
                        lock (committedCurrentFactsGate)
                        {
                            changedChartInfoMd5s.UnionWith(hashes ?? []);
                        }
                    }
                },
                StartedAtUtc = DateTime.UtcNow,
                CancellationToken = cancellationToken,
                IsShutdownRequested = () => host.IsShutdownRequested,
                ProgressReporter = host.UpdateLr2SongDbSyncProgress,
                SongCurrentFactsCommitted = infos =>
                {
                    if (infos == null || infos.Count == 0)
                    {
                        return;
                    }
                    lock (committedCurrentFactsGate)
                    {
                        committedCurrentFacts.AddRange(infos.Where(info => info != null));
                    }
                },
                CommittedBmsPaths = initializationResult?.CommittedBmsPaths,
                LogInstallPerformance = host.LogInstallPerformance
            });
            applyCommittedCurrentProjection();
            bool completed = string.Equals(result.FinalStage, Lr2SongDbSyncService.CompletedStage, StringComparison.Ordinal);
            if (completed)
            {
                // 生成側の保守writerを解放してから必須投影を終え、所有取消を確認して実完了を保存します。
                host.UpdateLr2SongDbSyncProgress(new Lr2SongDbSyncProgress
                {
                    ProcessedCursor = result.ProcessedCount,
                    TotalCount = result.TotalCount,
                    Stage = "sync_state_saving"
                });
                cancellationToken.ThrowIfCancellationRequested();
                host.MarkLr2SongDbSyncCompletedStatus(runId, result.TotalCount);
                host.UpdateLr2SongDbSyncProgress(new Lr2SongDbSyncProgress
                {
                    ProcessedCursor = result.TotalCount,
                    TotalCount = result.TotalCount,
                    Stage = Lr2SongDbSyncService.CompletedStage,
                    StageProcessedCount = result.TotalCount,
                    StageTotalCount = result.TotalCount
                });
            }
            stopwatch.Stop();
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
                + " songRowInputRows=" + input.SongRows.Count
                + " folderTableGenerated=" + (result.FolderTableReconciliationResult?.GeneratedCount ?? 0)
                + " folderTableUpserted=" + (result.FolderTableReconciliationResult?.UpsertedCount ?? 0)
                + " folderTableDeleted=" + (result.FolderTableReconciliationResult?.DeletedCount ?? 0)
                + " folderTableExistingRows=" + (result.FolderTableReconciliationResult?.ExistingRowCount ?? 0)
                + " lr2FolderProcessed=" + result.Lr2FolderFileProcessedCount
                + " songRowProcessed=" + result.SongRowProcessedCount
                + " songRowBasicInputSkipped=" + result.SongRowSkippedCount
                + " songRowParseFailed=" + result.SongRowParseFailureCount
                + " songRowChartInfoApplied=" + result.SongRowChartInfoAppliedCount
                + " songRowLr2CompatibilityApplied=" + result.SongRowLr2CompatibilityAppliedCount
                + " processed=" + result.ProcessedCount
                + " total=" + result.TotalCount
                + " stage=" + result.FinalStage
                + " detail=" + (result.IncompleteReason ?? "completed")
                + " elapsedMs=" + stopwatch.ElapsedMilliseconds);
            host.LogInstallPerformance("lr2_song_db_sync_current_projection applied"
                + " reason=" + (reason ?? "unknown")
                + " input=" + result.SongRowLr2CompatibilityAppliedCount
                + " applied=" + projectedCurrentValueCount);
            host.LogInstallPerformance("lr2_song_db_sync_chart_info_projection applied"
                + " reason=" + (reason ?? "unknown")
                + " rows=" + committedChartInfoRowCount
                + " parseFailureChanges=" + committedChartInfoParseFailureChangeCount);
            if (committedChartInfoRowCount > 0 || committedChartInfoParseFailureChangeCount > 0)
            {
                host.DispatchWarningPresentationChanged("lr2_song_db_sync_inline_chart_info", [.. changedChartInfoMd5s]);
            }
            if (projectedCurrentValueCount > 0 && committedCurrentFacts.Any(fact => fact.CompatibilityInfo != null))
            {
                host.DispatchWarningPresentationChanged("lr2_song_db_sync_current_projection",
                    [.. committedCurrentFacts.Where(value => value.CompatibilityInfo != null).Select(value => value.CompatibilityInfo.Hash).Where(hash => !string.IsNullOrWhiteSpace(hash)).Distinct(StringComparer.OrdinalIgnoreCase)]);
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
            try
            {
                applyCommittedCurrentProjection();
            }
            catch (Exception projectionException)
            {
                host.LogInstallPerformance("lr2_song_db_sync current_projection_after_cancel_failed"
                    + " reason=" + (reason ?? "unknown") + " " + projectionException
                    + " original=" + cancellationException);
            }
            stopwatch.Stop();
            Lr2SongDbSyncRuntimeSnapshot runtimeSnapshot = host.GetLr2SongDbSyncRuntimeSnapshot();
            if (currentProjectionFailure != null)
            {
                host.RecordLr2SongDbSyncFailure(runId, requestVersion,
                    currentProjectionFailure, runtimeSnapshot.Stage);
                host.ReportStartupBackgroundTask("lr2_song_db_sync", "failed", stopwatch.ElapsedMilliseconds,
                    failed: true, detail: currentProjectionFailure.Message);
                ExceptionDispatchInfo.Capture(currentProjectionFailure).Throw();
            }
            bool shutdownInterruption = host.IsShutdownRequested;
            if (shutdownInterruption)
            {
                host.RecordLr2SongDbSyncFailure(runId, requestVersion, cancellationException,
                    runtimeSnapshot.Stage, interruptedByShutdown: true);
                host.LogInstallPerformance("lr2_song_db_sync interrupted reason=" + (reason ?? "unknown")
                    + " runId=" + runId
                    + " elapsedMs=" + stopwatch.ElapsedMilliseconds
                    + " stage=" + (runtimeSnapshot.Stage ?? string.Empty)
                    + " processed=" + runtimeSnapshot.ProcessedCount
                    + " total=" + runtimeSnapshot.TotalCount);
                host.ReportStartupBackgroundTask("lr2_song_db_sync", "incomplete", stopwatch.ElapsedMilliseconds, failed: false, detail: "shutdown_interrupted");
                throw;
            }
            host.RecordLr2SongDbSyncFailure(runId, requestVersion, cancellationException, runtimeSnapshot.Stage);
            host.LogInstallPerformance("lr2_song_db_sync cancellation_failed reason=" + (reason ?? "unknown")
                + " runId=" + runId
                + " elapsedMs=" + stopwatch.ElapsedMilliseconds
                + " stage=" + (runtimeSnapshot.Stage ?? string.Empty)
                + " processed=" + runtimeSnapshot.ProcessedCount
                + " total=" + runtimeSnapshot.TotalCount);
            host.ReportStartupBackgroundTask("lr2_song_db_sync", "failed", stopwatch.ElapsedMilliseconds, failed: true, detail: cancellationException.Message);
            throw;
        }
        catch (Exception ex)
        {
            try
            {
                applyCommittedCurrentProjection();
            }
            catch (Exception projectionException)
            {
                host.LogInstallPerformance("lr2_song_db_sync current_projection_after_failure_failed"
                    + " reason=" + (reason ?? "unknown") + " " + projectionException
                    + " original=" + ex);
            }
            stopwatch.Stop();
            host.RecordLr2SongDbSyncFailure(runId, requestVersion, ex, "failed");
            host.LogInstallPerformance("lr2_song_db_sync failed reason=" + (reason ?? "unknown")
                + " runId=" + runId
                + " elapsedMs=" + stopwatch.ElapsedMilliseconds
                + " exception=" + ex.GetType().Name
                + " message=" + ex.Message);
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

        int safeTotal = Math.Max(0, totalCount);
        int safeProcessed = Math.Max(0, Math.Min(Math.Max(0, processedCount), safeTotal));
        host.PublishLr2SongDbSyncStatus(CreateRuntimeLr2SongDbSyncStatus(
            Lr2SongDbSyncStatusKind.Running,
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
