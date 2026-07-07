using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading;
using System.Windows;
using BeMusicSeeker.Models.BmsLibraryInternal;
using BeMusicSeeker.Models.Utils;
using BeMusicSeeker.Properties;
using Livet;
using Ribbit.Logging;
using Ribbit.Util;

namespace BeMusicSeeker.Models;

public partial class BMSLibrary
{
    /// <summary>
    /// 指定されたパス群（ファイルまたはディレクトリ）から chart package を自動検出・インストールします。
    /// アーカイブの展開、song.db への登録、Pendingパッケージ生成を一括で行います。
    /// </summary>
    /// <param name="installPaths">インストール元のファイル/ディレクトリパスのコレクション。</param>
    /// <returns>インストール処理された chart package のリスト。</returns>
    public List<ChartPackage> InstallChartPackagesAuto(IEnumerable<string> installPaths, CancellationToken token = default, Action onEachSourceProcessed = null, Action<string, int, int> onEachArchiveExtractStarted = null)
    {
        BmsLibraryOptionsSnapshot options = CurrentOptionsSnapshot;
        List<ChartPackage> pendingPackagesToEstimate = [];
        List<ChartPackage> deferredPendingEstimatePackages = [];
        Dictionary<ChartPackage, int> deferredPendingEstimateHealthByPackage = [];
        List<ChartPackage> registeredPackages = [];
        List<string> regroupEligibleSourceDirectories = [];
        PendingEstimateSourceBatchSnapshot pendingBatchSourceSnapshot = null;
        if (TryBlockLr2SongDbSyncMutation(nameof(InstallChartPackagesAuto)))
        {
            return registeredPackages;
        }
        if (installPaths == null || installPaths.Any(path => !LongPathFileSystem.EntryExists(path)))
        {
            ShowOperationDialog(Resources.Warn_InstallAbortedFilesNotFound, Resources.MessageBoxTitle_Warning, MessageBoxButton.OK, MessageBoxImage.Hand, MessageBoxResult.OK);
            return registeredPackages;
        }
        if (token.IsCancellationRequested)
        {
            return registeredPackages;
        }
        List<string> expandedInstallPaths = packageInstallService.ExpandInstallSources(
            installPaths,
            fileMutationService,
            targetOnlyFileMutationOptions,
            info => NLogWrapper.FileLogger?.Info(info),
            scopedOperationDialogService,
            onEachSourceProcessed,
            onEachArchiveExtractStarted,
            token);
        installPaths = expandedInstallPaths;
        if (token.IsCancellationRequested)
        {
            CleanupManagedInstallSources(expandedInstallPaths, "auto_install_canceled_after_expand");
            return registeredPackages;
        }
        using IDisposable lr2SongDbSyncMutation = TryBeginLr2SongDbSyncBlockedMutation(nameof(InstallChartPackagesAuto));
        if (lr2SongDbSyncMutation == null)
        {
            CleanupManagedInstallSources(expandedInstallPaths, "auto_install_blocked_after_expand");
            return registeredPackages;
        }
        using (rwlockBMSFilesInitializedAll.GetReaderGuard())
        {
            using (rwlockPendingInstallCharts.GetWriterGuard())
            {
                using (rwlockBMSFiles.GetWriterGuard())
                {
                    using (rwlockSongDBInstall.GetWriterGuard())
                    {
                        AutoInstallWorkflowResult workflow = packageInstallService.PrepareAutoInstallWorkflow(
                            installPaths,
                            ChartPackagesPending,
                            CreateKnownChartDirectorySnapshotUnsafe(),
                            ContainsInstalledChartUnsafe,
                            dupRateThreshInOnePkg,
                            CreateInstalledChartKeySnapshotExcludingChartsUnsafe([], "auto_install_prepare", 0L),
                            token);
                        List<ChartPackage> discoveredPackages = [.. workflow.DiscoveredPackages];
                        LogInstallPerformance("auto_install_prepare discovered=" + discoveredPackages.Count + " autoInstall=" + workflow.AutoInstallCandidates.Count + " pendingAdd=" + workflow.PendingPackagesToAdd.Count + " pendingRemove=" + workflow.PendingPackagesToRemove.Count + " discoveryMs=" + workflow.DiscoveryMs + " installedCheckMs=" + workflow.InstalledCheckMs + " warningClassifyMs=" + workflow.WarningClassificationMs + " classificationMs=" + workflow.ClassificationMs + " totalMs=" + workflow.TotalMs);
                        if (discoveredPackages.Count == 0 || token.IsCancellationRequested)
                        {
                            CleanupManagedInstallSources(expandedInstallPaths, discoveredPackages.Count == 0 ? "auto_install_no_packages" : "auto_install_canceled_after_prepare");
                            return registeredPackages;
                        }
                        AutoInstallApplyResult applyResult = packageInstallService.ApplyAutoInstallWorkflow(
                            workflow,
                            options.KeepInstallablePackagesPending,
                            SearchTargets != null && SearchTargets.Count() > 0 && LongPathFileSystem.DirectoryExists(SearchTargets[0]),
                            (packagesToInstall) => installChartPackages(packagesToInstall),
                            token);
                        LogInstallPerformance("auto_install_apply pendingAdd=" + applyResult.PendingPackagesToAdd.Count + " pendingRemove=" + applyResult.PendingPackagesToRemove.Count + " autoInstalled=" + applyResult.AutoInstalledPackages.Count + " autoFailed=" + applyResult.AutoInstallFailures.Count + " installMs=" + applyResult.InstallMs + " applyMs=" + applyResult.ApplyMs + " totalMs=" + applyResult.TotalMs);
                        if (applyResult.PendingPackagesToRemove.Count > 0)
                        {
                            stateApplier.ApplyPendingPackageMutationDelta(BuildPendingPackageMutationDelta(packagesToRemove: applyResult.PendingPackagesToRemove));
                        }
                        if (applyResult.InstallRowsToUpsert.Count > 0)
                        {
                            dbGateway.UpsertInstallRows(applyResult.InstallRowsToUpsert);
                            ChartPackagesPending.AddRange(applyResult.PendingPackagesToAdd);
                        }
                        BackgroundPendingEstimatePreparationResult estimatePreparation = PrepareBackgroundPendingEstimatePackagesUnsafe(applyResult.EstimateTargets, PendingInstallEstimateBatchSource.AutoInstall);
                        pendingPackagesToEstimate = estimatePreparation.EstimablePackages;
                        deferredPendingEstimatePackages = estimatePreparation.DeferredPackages;
                        deferredPendingEstimateHealthByPackage = estimatePreparation.DeferredSourceHealthByPackage;
                        pendingBatchSourceSnapshot = estimatePreparation.BatchSourceSnapshot;
                        regroupEligibleSourceDirectories = [.. workflow.RegroupEligibleSourceDirectories];
                        registeredPackages = discoveredPackages;
                    }
                }
                foreach (ChartPackage deferredPackage in deferredPendingEstimatePackages)
                {
                    deferredPendingEstimateHealthByPackage.TryGetValue(deferredPackage, out int sourceHealth);
                    LogPendingEstimateSkippedPackage("auto_install", deferredPackage, sourceHealth);
                }
                if (!token.IsCancellationRequested && pendingPackagesToEstimate.Count > 0)
                {
                    string displayName = PendingInstallEstimateBatchRequest.GetDisplayName(pendingPackagesToEstimate.FirstOrDefault()?.path);
                    QueuePendingInstallEstimateBatch(new PendingInstallEstimateBatchRequest(
                        PendingInstallEstimateBatchSource.AutoInstall,
                        pendingPackagesToEstimate,
                        displayName,
                        regroupEligibleSourceDirectories,
                        deferredPendingEstimatePackages.Count,
                        pendingBatchSourceSnapshot));
                }
            }
        }
        return registeredPackages;
    }

    private void CleanupManagedInstallSources(IEnumerable<string> paths, string reason)
    {
        foreach (string path in (paths ?? []).Where(path => !string.IsNullOrWhiteSpace(path)).Distinct(StringComparer.OrdinalIgnoreCase))
        {
            TempDirectoryPublisher.TryDeleteManagedPath(
                path,
                info => NLogWrapper.FileLogger?.Info(info + " reason=" + (reason ?? string.Empty)),
                (cleanupPath, ex) => NLogWrapper.FileLogger?.Warn(ex, "temp_cleanup_failed reason=" + (reason ?? string.Empty) + " path=" + cleanupPath));
        }
    }

    private static bool IsSamePath(string path1, string path2)
    {
        return new BmsLibraryPackageInstallService().IsSamePath(path1, path2);
    }

    private static ComponentMovePlanBuildResult BuildComponentMovePlan(IEnumerable<string> installComponentFiles, string destinationDirectory, ISet<string> excludedComponentPaths)
    {
        return new BmsLibraryPackageInstallService().BuildComponentMovePlan(installComponentFiles, destinationDirectory, excludedComponentPaths);
    }

    private void CleanupEmptyComponentDirectories(IEnumerable<string> installComponentDirectories)
    {
        foreach (string installComponentDirectory in installComponentDirectories.Where(path => LongPathFileSystem.DirectoryExists(path)).OrderByDescending(path => path.Length))
        {
            TryDeleteEmptyDirectoryTree(installComponentDirectory);
        }
    }

    private void TryDeleteEmptyDirectoryTree(string rootDirectoryPath)
    {
        try
        {
            if (!LongPathFileSystem.DirectoryExists(rootDirectoryPath))
            {
                return;
            }

            foreach (string childDirectoryPath in LongPathFileSystem.EnumerateDirectories(rootDirectoryPath).ToList())
            {
                TryDeleteEmptyDirectoryTree(childDirectoryPath);
            }

            if (!LongPathFileSystem.EnumerateFileSystemEntries(rootDirectoryPath).Any())
            {
                fileMutationService.DeleteDirectoryDirect(rootDirectoryPath, recursive: false, targetOnlyFileMutationOptions);
            }
        }
        catch
        {
        }
    }

    private static string GetDisplayedExceptionMessage(Exception exception)
    {
        if (exception is AggregateException aggregateException)
        {
            return string.Join(Environment.NewLine, aggregateException.Flatten().InnerExceptions.Select(GetDisplayedExceptionMessage));
        }

        if (exception is FileMutationException fileMutationException)
        {
            return fileMutationException.InnerException?.Message ?? fileMutationException.Message;
        }

        return exception?.Message ?? string.Empty;
    }

    private static bool HasRemainingDirectoryEntries(string directoryPath)
    {
        try
        {
            return LongPathFileSystem.DirectoryExists(directoryPath) && LongPathFileSystem.EnumerateFileSystemEntries(directoryPath).Any();
        }
        catch
        {
            return false;
        }
    }

    private bool TryCleanupPendingPackageSourceForEstimatedInstall(ChartPackage package, out CleanupSourceKind sourceKind)
    {
        sourceKind = CleanupSourceKind.MissingSource;
        if (package == null || string.IsNullOrWhiteSpace(package.path))
        {
            return true;
        }

        string packagePath = package.path;
        bool sourceDirectoryExists = LongPathFileSystem.DirectoryExists(packagePath);
        bool sourceFileExists = LongPathFileSystem.FileExists(packagePath);

        if (!sourceDirectoryExists && !sourceFileExists)
        {
            return true;
        }

        sourceKind = sourceDirectoryExists ? CleanupSourceKind.Directory : CleanupSourceKind.File;
        try
        {
            if (sourceDirectoryExists)
            {
                fileMutationService.DeleteDirectoryDirect(packagePath, recursive: true, recursiveDirectoryTreeFileMutationOptions);
            }
            else
            {
                fileMutationService.DeleteFileDirect(packagePath, targetOnlyFileMutationOptions);
            }
            return true;
        }
        catch (Exception ex)
        {
            string displayedMessage = GetDisplayedExceptionMessage(ex);
            NLogWrapper.FileLogger?.Warn(ex, "estimated_install_cleanup_only_failed path=" + packagePath + " kind=" + sourceKind.ToString().ToLowerInvariant() + " error=" + displayedMessage);
            if (sourceKind == CleanupSourceKind.Directory)
            {
                ShowOperationDialog(string.Format(Resources.Error_FolderDeleteFailed, packagePath, displayedMessage), Resources.MessageBoxTitle_Error, MessageBoxButton.OK, MessageBoxImage.Hand, MessageBoxResult.OK);
            }
            else
            {
                ShowOperationDialog(string.Format(Resources.Error_BmsFileDeleteFailed, packagePath, displayedMessage), Resources.MessageBoxTitle_Error, MessageBoxButton.OK, MessageBoxImage.Hand, MessageBoxResult.OK);
            }
            return false;
        }
    }

    /// <summary>
    /// chart package のファイル群を指定ディレクトリに移動し、移動元の空フォルダを削除する。
    /// マージ処理（MergeChartDirectory）やインストール処理（installChartPackages）から呼ばれる共通メソッド。
    /// </summary>
    /// <param name="pkg">移動対象の譜面パッケージ</param>
    /// <param name="installationDirectory">移動先ディレクトリ（nullの場合は自動命名）</param>
    /// <param name="showMessageBoxOnInstallFail">移動失敗時にエラーダイアログを表示するか</param>
    /// <param name="deleteAllContents">移動元フォルダを再帰削除対象として扱うか（通常インストール時は安全判定を通過した場合のみ削除）</param>
    /// <param name="existingHashes">既存譜面ハッシュの lookup（重複スキップ用）</param>
    /// <param name="excludedComponentPaths">移動対象外のコンポーネントパス</param>
    /// <returns>移動成功時true</returns>
    private bool MoveChartPackageFiles(ChartPackage pkg, string installationDirectory, bool showMessageBoxOnInstallFail = true, bool deleteAllContents = false, IPrimaryHashLookup existingHashes = null, ISet<string> excludedComponentPaths = null)
    {
        return packageInstallService.MovePackageFiles(
            pkg,
            installationDirectory,
            BmsLibraryOptionsSnapshot.CreateCurrent(),
            CreateChartFolderPathFromCharts,
            GetDisplayedExceptionMessage,
            fileMutationService,
            scopedOperationDialogService,
            targetOnlyFileMutationOptions,
            recursiveDirectoryTreeFileMutationOptions,
            LogInstallPerformance,
            showMessageBoxOnInstallFail,
            deleteAllContents,
            existingHashes,
            excludedComponentPaths);
    }

    private sealed class EstimatedInstallBatchApplyContext
    {
        public List<ChartFile> AddedCharts { get; } = [];

        public HashSet<string> AffectedDirectories { get; } = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        public void AddInstalledTargets(ChartStorageTargetSet addedTargets, string destinationDirectory)
        {
            AddedCharts.AddRange((addedTargets?.Charts ?? []).Where(chart => chart != null));
            AddAffectedDirectory(destinationDirectory);
            foreach (ChartFile addedChart in addedTargets?.Charts ?? [])
            {
                AddAffectedDirectory(DirectoryExt.GetDirectoryNameSimple(addedChart.Path));
            }
        }

        private void AddAffectedDirectory(string directoryPath)
        {
            if (!string.IsNullOrWhiteSpace(directoryPath))
            {
                AffectedDirectories.Add(directoryPath);
            }
        }

    }

    private List<ChartPackage> installChartPackages(IEnumerable<ChartPackage> chartPackagesInstall, string installationDirectory = null, List<ChartFile> deferredMaintenanceCharts = null, List<ChartPackage> deferredInstalledPackages = null, Dictionary<ChartPackage, HashSet<string>> excludedComponentPathsByPackage = null, IPrimaryHashLookup existingHashes = null, bool skipInstalledPackageWhenNoBms = false, bool deleteSourceContentsAfterSuccessfulInstall = false, EstimatedInstallBatchApplyContext estimatedInstallBatchApplyContext = null)
    {
        ThrowIfLr2SongDbSyncMutationBlocked(nameof(installChartPackages));
        List<ChartPackage> installPackageList = [.. (chartPackagesInstall ?? []).Where(package => package != null)];
        List<ChartFile> addedChartsForChartInfo = [];

        static ChartStorageTargetSet CreateAddedStorageTargets(PackageInstallExecutionResult installResult)
        {
            return ChartStorageTargetSet.FromCharts(installResult?.AddedCharts);
        }

        void UpsertInstalledChartRows(PackageInstallExecutionResult installResult)
        {
            ChartStorageTargetSet addedTargets = CreateAddedStorageTargets(installResult);
            if (addedTargets.BmsFiles.Count > 0)
            {
                ExecuteLr2SongDbWrite(
                    () => dbGateway.UpsertSongs(addedTargets.BmsFiles),
                    stage: "lr2_song_db_install_upsert_failed",
                    logReason: "install_package");
            }
            if (addedTargets.BmsonSongs.Count > 0)
            {
                dbGateway.UpsertBmsonSongs(addedTargets.BmsonSongs);
            }
        }

        void UpdateInstalledChartMaintenance(PackageInstallExecutionResult installResult)
        {
            List<ChartFile> addedCharts = CreateAddedStorageTargets(installResult).Charts;
            if (deferredMaintenanceCharts != null)
            {
                deferredMaintenanceCharts.AddRange(addedCharts);
                return;
            }
            if (addedCharts.Count > 0)
            {
                setMaintenanceInfo(
                    addedCharts,
                    forceUpdate: true,
                    resourceHealthMutationReason: "install_package");
            }
        }

        void ApplyInstalledChartState(PackageInstallExecutionResult installResult)
        {
            ChartStorageTargetSet addedTargets = CreateAddedStorageTargets(installResult);
            List<ChartFile> addedCharts = addedTargets.Charts;
            addedChartsForChartInfo.AddRange(addedCharts);
            if (estimatedInstallBatchApplyContext != null)
            {
                estimatedInstallBatchApplyContext.AddInstalledTargets(addedTargets, installationDirectory);
                return;
            }
            ApplyInstalledChartStorageTargets(addedTargets, "install_package");
            List<string> addedDirectories = addedTargets.GetDistinctChartDirectories();
            if (addedDirectories.Count > 0)
            {
                if (ChartDirectoryScanBuilder.TryBuildFromRoots(addedDirectories, out ChartScanResult addedDirectoryScan, out string scanFailureReason))
                {
                    DirectoryResourceLookupCache.ReverseLookupMutationResult reverseLookupMutation = DirectoryResourceLookupCache.ReverseLookupMutationResult.Empty;
                    foreach (string dir in addedDirectoryScan.ChartDirectories)
                    {
                        reverseLookupMutation = reverseLookupMutation.Combine(directoryResourceLookupCache.AddDir(dir, addedDirectoryScan));
                    }
                    LogReverseLookupMutationAndQueueWarmupIfNeeded("install_package", reverseLookupMutation);
                }
                else
                {
                    LogInstallPerformanceWarn("install_package resource_cache_update skipped reason=incomplete_scan detail=" + (scanFailureReason ?? "unknown") + " dirs=" + addedDirectories.Count);
                }
            }
        }

        PackageInstallExecutionResult result = packageInstallService.InstallPackages(
            installPackageList,
            installationDirectory,
            (package, destinationDirectory, deleteAllContents, hashSnapshot, excludedComponentPaths) => MoveChartPackageFiles(package, destinationDirectory, true, deleteAllContents, hashSnapshot, excludedComponentPaths),
            UpsertInstalledChartRows,
            UpdateInstalledChartMaintenance,
            result => SetBMSScore(CreateAddedStorageTargets(result).BmsFiles),
            ApplyInstalledChartState,
            excludedComponentPathsByPackage,
            existingHashes,
            skipInstalledPackageWhenNoBms,
            deleteSourceContentsAfterSuccessfulInstall);
        if (estimatedInstallBatchApplyContext != null && result.FailedPackages.Count < installPackageList.Count)
        {
            estimatedInstallBatchApplyContext.AddInstalledTargets(ChartStorageTargetSet.FromCharts([]), installationDirectory);
        }
        if (result.InstalledPackagesToRegister.Count > 0)
        {
            if (deferredInstalledPackages != null)
            {
                deferredInstalledPackages.AddRange(result.InstalledPackagesToRegister);
            }
            else
            {
                ChartPackagesInstalled.AddRange(result.InstalledPackagesToRegister);
            }
        }
        LogInstallPerformance("install_chart_packages dst=" + (installationDirectory ?? "(auto)") + " packages=" + installPackageList.Count + " addedFiles=" + result.AddedEntries.Count + " failedPackages=" + result.FailedPackages.Count + " deleteSourceContents=" + deleteSourceContentsAfterSuccessfulInstall + " moveMs=" + result.MoveMs + " songDbMs=" + result.SongDbMs + " maintenanceMs=" + result.MaintenanceMs + " scoreMs=" + result.ScoreMs + " applyMs=" + result.ApplyMs + " totalMs=" + result.TotalMs);
        if (deferredMaintenanceCharts == null)
        {
            BuildAndPersistInlineChartInfoForInstalledCharts("install_package_inline", addedChartsForChartInfo);
        }
        return result.FailedPackages;
    }

    private DirectoryResourceLookupCache.ReverseLookupMutationResult ApplyEstimatedInstallBatchLibraryState(EstimatedInstallBatchApplyContext context)
    {
        if (context == null)
        {
            return DirectoryResourceLookupCache.ReverseLookupMutationResult.Empty;
        }
        ApplyInstalledChartStorageTargets(
            ChartStorageTargetSet.FromCharts(context.AddedCharts),
            "install_package_batch");
        List<string> affectedDirectories = [.. context.AffectedDirectories.Where(dir => !string.IsNullOrWhiteSpace(dir)).Distinct(StringComparer.OrdinalIgnoreCase)];
        if (affectedDirectories.Count == 0)
        {
            return DirectoryResourceLookupCache.ReverseLookupMutationResult.Empty;
        }
        if (!ChartDirectoryScanBuilder.TryBuildFromRoots(affectedDirectories, out ChartScanResult addedDirectoryScan, out string scanFailureReason))
        {
            LogInstallPerformanceWarn("install_package_batch resource_cache_update skipped reason=incomplete_scan detail=" + (scanFailureReason ?? "unknown") + " dirs=" + affectedDirectories.Count);
            return DirectoryResourceLookupCache.ReverseLookupMutationResult.Empty;
        }
        DirectoryResourceLookupCache.ReverseLookupMutationResult reverseLookupMutation = DirectoryResourceLookupCache.ReverseLookupMutationResult.Empty;
        foreach (string dir in affectedDirectories)
        {
            reverseLookupMutation = reverseLookupMutation.Combine(directoryResourceLookupCache.AddDir(dir, addedDirectoryScan));
        }
        LogReverseLookupMutationAndQueueWarmupIfNeeded("install_package", reverseLookupMutation);
        return reverseLookupMutation;
    }

    private static List<ChartFile> BuildEstimatedInstallMaintenanceTargets(IEnumerable<ChartFile> charts)
    {
        var targetsByKey = new Dictionary<string, ChartFile>(StringComparer.OrdinalIgnoreCase);
        foreach (ChartFile target in NormalizeResourceMaintenanceTargetCharts(charts))
        {
            string key = target.Kind + "|" + target.Path;
            if (!string.IsNullOrWhiteSpace(target.Path))
            {
                targetsByKey[key] = target;
            }
        }
        return [.. targetsByKey.Values];
    }

    private static List<ChartFile> CreateAddedBmsonChartProjections(IEnumerable<ChartFile> addedCharts)
    {
        var chartsByPath = new Dictionary<string, ChartFile>(StringComparer.OrdinalIgnoreCase);
        foreach (ChartFile chart in addedCharts ?? [])
        {
            if (chart?.Kind != ChartFileKind.Bmson)
            {
                continue;
            }
            ChartFile projected = ChartFileProjection.FromStorageOwner(
                chart,
                includeWarningSnapshot: false,
                includeResourceReferences: true,
                includeScoreSnapshot: false);
            if (projected?.Kind != ChartFileKind.Bmson || string.IsNullOrWhiteSpace(projected.Path))
            {
                continue;
            }
            chartsByPath[projected.Path] = projected;
        }
        return [.. chartsByPath.Values];
    }

    /// <summary>
    /// chart file の導入先（インストール先ディレクトリ）を推定します。
    /// </summary>
    /// <param name="package">推定対象 package。loose chart の場合は null。</param>
    /// <param name="targetEntries">インストール対象の chart entry リスト（通常は同一パッケージ内の譜面群）</param>
    /// <param name="asParallel">既存フォルダの走査（各フォルダとのマッチング評価）を並列実行するかどうか</param>
    /// <param name="estimateMode">通常推定、merge 候補探索、再インストール先修正などの推定モード</param>
    /// <remarks>
    /// 【設計意図・背景】
    /// 差分 chart package（追加の譜面データや難易度変更ファイル等）は、音源（WAV/OGGやBGA等）の実体を含まないことが多いため、
    /// そのまま独立してインストールしてもゲームプレイ時に音が鳴らないなどの不具合が生じます。
    /// ユーザーが手動で適切なベースとなる楽曲フォルダを探して統合する手間を省くべく、本ロジックでは
    /// 対象 chart file が必要とする依存ファイルのハッシュ群をキーとして、既存の全楽曲フォルダを事前フィルタリングし、
    /// 関連性が疑われるフォルダに対してのみ「仮想的に chart を配置したシミュレーション」を行います。
    /// 全てのフォルダを計算すると重すぎるため、事前のハッシュマッチで候補を絞り込むことで劇的な高速化を図りつつ、
    /// 根本的には旧来と同じく、最もファイルの依存関係が解決される（健康度/Health が高まる）フォルダを自動算出して提案します。
    /// </remarks>
    private void SearchEstimatedInstallationDirectoryForChartsCore(ChartPackage package, IEnumerable<PackageChartEntry> targetEntries, bool asParallel, ChartInstallationEstimateMode estimateMode)
    {
        using (rwlockBMSFilesInitializedAll.GetReaderGuard())
        {
            using (rwlockBMSFiles.GetReaderGuard())
            {
                List<PackageChartEntry> targetEntryList = null;
                try
                {
                    targetEntryList = [.. (targetEntries ?? []).Where(entry => entry?.Chart != null)];
                    if (targetEntryList.Count == 0
                        || targetEntryList.Any(entry => !string.IsNullOrWhiteSpace(entry.Chart.InstallDestination)))
                    {
                        return;
                    }
                    if (HasUnsupportedResourcePath(targetEntryList))
                    {
                        ApplyUnsupportedResourcePathToPackageUnsafe(package, targetEntryList);
                        return;
                    }
                    foreach (PackageChartEntry entry in targetEntryList)
                    {
                        entry.SetSearchingStatus(isSearching: true);
                    }
                    InstallEstimationEvaluationData estimationData = EvaluateInstallEstimation(
                        package,
                        targetEntryList,
                        BmsLibraryInstallEstimationService.ResolveCandidateEvaluationDegree(asParallel),
                        estimateMode);
                    if (estimationData.SourceSurfaceScanLimitExceeded)
                    {
                        LogSourceSurfaceScanLimitExceeded(estimationData, package?.path);
                        ApplySourceSurfaceScanLimitExceededToPackageUnsafe(package, targetEntryList, estimationData.SourceSurfaceMaxVisitedFileSystemEntryCount);
                        return;
                    }
                    LogInstallEstimationEvaluation(estimationData);
                    ApplyInstallEstimationResultToEntries(targetEntryList, estimationData.Result);
                }
                finally
                {
                    foreach (PackageChartEntry entry in targetEntryList ?? [])
                    {
                        entry?.SetSearchingStatus(isSearching: false);
                    }
                }
            }
        }
    }

    private static void ApplyPackageMixedInstallWarningsToEntries(IEnumerable<PackageChartEntry> installedEntries)
    {
        foreach (PackageChartEntry entry in (installedEntries ?? []).Where(entry => entry?.Chart != null))
        {
            entry.ClearWarningsByCategory(ChartWarningCategory.InstalledState);
            entry.SetWarning(ChartWarningKind.AlreadyInstalled, Resources.Warning_AlreadyInstalled);
        }
    }

    private static void ApplyInstalledDestinationResolveFailedToPackageUnsafe(ChartPackage package, IEnumerable<PackageChartEntry> missingEntries)
    {
        if (package != null)
        {
            package.DeferredEstimateReason = PendingEstimateDeferredReason.InstalledDestinationResolveFailed;
        }
        foreach (PackageChartEntry entry in (missingEntries ?? []).Where(entry => entry?.Chart != null))
        {
            entry.ApplyInstalledDestinationResolveFailed();
        }
    }

    private static bool HasUnsupportedResourcePath(IEnumerable<PackageChartEntry> entries)
    {
        return (entries ?? []).Any(entry => entry?.Chart != null && entry.ResourceSnapshot.HasUnsupportedParentTraversalReference);
    }

    private static void ApplyUnsupportedResourcePathToPackageUnsafe(ChartPackage package, IEnumerable<PackageChartEntry> missingEntries)
    {
        if (package != null)
        {
            package.DeferredEstimateReason = PendingEstimateDeferredReason.UnsupportedResourcePath;
        }
        foreach (PackageChartEntry entry in (missingEntries ?? []).Where(entry => entry?.Chart != null))
        {
            if (entry.ResourceSnapshot.HasUnsupportedParentTraversalReference)
            {
                entry.ApplyUnsupportedResourcePathWarning();
            }
            else
            {
                entry.ClearInstallDestination();
            }
        }
    }

    private static void ApplySourceSurfaceScanLimitExceededToPackageUnsafe(ChartPackage package, IEnumerable<PackageChartEntry> missingEntries, int maxVisitedFileSystemEntryCount)
    {
        if (package != null)
        {
            package.DeferredEstimateReason = PendingEstimateDeferredReason.SourceSurfaceScanLimitExceeded;
        }
        int normalizedMax = maxVisitedFileSystemEntryCount > 0
            ? maxVisitedFileSystemEntryCount
            : PackageInstallEstimationSnapshotBuilder.DefaultSourceSurfaceMaxVisitedFileSystemEntries;
        foreach (PackageChartEntry entry in (missingEntries ?? []).Where(entry => entry?.Chart != null))
        {
            entry.ApplySourceSurfaceScanLimitExceededWarning(normalizedMax);
        }
    }

    private bool TryResolveInstalledDestinationFromPackage(ChartPackage package, IReadOnlyCollection<PackageChartEntry> missingEntries, out string resolvedDir)
    {
        resolvedDir = null;
        InstalledDirectoryLookupResult resolution = ResolveInstalledDestinationFromPackage(package, missingEntries);
        LogMixedPackageResolution(resolution, package?.path, missingEntries?.Count ?? 0);
        if (!resolution.Success)
        {
            return false;
        }
        resolvedDir = resolution.InstallDirectory;
        return true;
    }

    private InstalledDirectoryLookupResult ResolveInstalledDestinationFromPackage(ChartPackage package, IReadOnlyCollection<PackageChartEntry> missingEntries)
    {
        if (package == null || missingEntries == null || missingEntries.Count == 0)
        {
            return new InstalledDirectoryLookupResult
            {
                Reason = InstalledDirectoryResolveReason.InvalidInput
            };
        }
        IInstalledChartLookupIndex installedDirectoryIndex = CreateInstalledChartLookupSnapshotUnsafe();
        if (installedDirectoryIndex.HashCount == 0)
        {
            return new InstalledDirectoryLookupResult
            {
                Reason = InstalledDirectoryResolveReason.InstalledIndexEmpty
            };
        }
        BmsLibraryInstallEstimationService installEstimationService = CreateInstallEstimationService();
        return installEstimationService.TryResolveInstalledDestinationFromPackage(package, missingEntries, installedDirectoryIndex);
    }

    /// <summary>
    /// 指定されたBMSパッケージに対して、最適な導入先ディレクトリへの推論処理をキューイングします。
    /// （UIからのドラッグ＆ドロップ登録時などに呼び出されます）
    /// </summary>
    /// <param name="package">推定を行う chart package オブジェクト</param>
    private void SearchEstimatedInstallationDirectoryCore(ChartPackage package)
    {
        if (package == null)
        {
            throw new ArgumentNullException("package");
        }
        using (rwlockBMSFilesInitializedAll.GetReaderGuard())
        {
            using (rwlockPendingInstallCharts.GetWriterGuard())
            {
                using (rwlockBMSFiles.GetReaderGuard())
                {
                    using (rwlockSongDBInstall.GetWriterGuard())
                    {
                        if (!ChartPackagesPending.Contains(package))
                        {
                            return;
                        }
                        package.DeferredEstimateReason = PendingEstimateDeferredReason.None;
                        PendingPackageChartEntryPartition partition = BuildPendingPackageChartEntryPartitionUnsafe(package);
                        if (partition.PackageEntries.Count == 0)
                        {
                            return;
                        }
                        List<PackageChartEntry> alreadyInstalledEntries = partition.AlreadyInstalledEntries;
                        List<PackageChartEntry> missingEntries = partition.MissingEntries;
                        ApplyPackageMixedInstallWarningsToEntries(alreadyInstalledEntries);
                        if (missingEntries.Count == 0)
                        {
                            return;
                        }
                        if (HasUnsupportedResourcePath(missingEntries))
                        {
                            ApplyUnsupportedResourcePathToPackageUnsafe(package, missingEntries);
                            return;
                        }
                        // 部分既所持パッケージでは、既存譜面の実配置先を優先利用して未所持譜面の導入先を補完する。
                        if (alreadyInstalledEntries.Count > 0)
                        {
                            InstalledDirectoryLookupResult resolution = ResolveInstalledDestinationFromPackage(package, missingEntries);
                            LogMixedPackageResolution(resolution, package.path, missingEntries.Count);
                            if (resolution.Success)
                            {
                                ApplyResolvedInstallDestinationToEntries(missingEntries, resolution.InstallDirectory);
                                return;
                            }
                            if (resolution.Reason == InstalledDirectoryResolveReason.MultipleCandidateDirectories)
                            {
                                if (!HasUsableDirectoryLookupCache(directoryResourceLookupCache))
                                {
                                    LogInstallPerformance("mixed_package_resolve failed reason=resource_index_unavailable missing=" + missingEntries.Count + " candidateDirs=" + resolution.CandidateDirectoryCount);
                                    ApplyInstalledDestinationResolveFailedToPackageUnsafe(package, missingEntries);
                                    return;
                                }
                                InstallEstimationEvaluationData estimationData = EvaluateInstallEstimation(
                                    package,
                                    missingEntries,
                                    BmsLibraryInstallEstimationService.ResolveCandidateEvaluationDegree(asParallel: true),
                                    ChartInstallationEstimateMode.Normal,
                                    candidateDirectoryOverride: resolution.CandidateDirectories,
                                    candidateDirectoryUniquePrimaryHashCountResolver: resolution.GetCandidateDirectoryUniquePrimaryHashCount,
                                    markInstalledDestinationAmbiguous: true);
                                if (estimationData.SourceSurfaceScanLimitExceeded)
                                {
                                    LogSourceSurfaceScanLimitExceeded(estimationData, package?.path);
                                    ApplySourceSurfaceScanLimitExceededToPackageUnsafe(package, missingEntries, estimationData.SourceSurfaceMaxVisitedFileSystemEntryCount);
                                    return;
                                }
                                LogInstallEstimationEvaluation(estimationData);
                                if (estimationData.Result?.HasViableDestination == true)
                                {
                                    ApplyInstallEstimationResultToEntries(missingEntries, estimationData.Result);
                                }
                                else
                                {
                                    ApplyInstalledDestinationResolveFailedToPackageUnsafe(package, missingEntries);
                                }
                                return;
                            }
                            LogInstallPerformance("mixed_package_resolve failed reason=installed_destination_unresolved missing=" + missingEntries.Count);
                            ApplyInstalledDestinationResolveFailedToPackageUnsafe(package, missingEntries);
                            return;
                        }
                        if (missingEntries.Count > 0)
                        {
                            SearchEstimatedInstallationDirectoryForChartsCore(package, missingEntries, asParallel: true, ChartInstallationEstimateMode.Normal);
                        }
                    }
                }
            }
        }
    }

    public void SearchEstimatedInstallationDirectory(ChartPackage package)
    {
        if (package == null)
        {
            throw new ArgumentNullException("package");
        }
        var stopwatch = Stopwatch.StartNew();
        string displayName = PendingInstallEstimateBatchRequest.GetDisplayName(package.path);
        LogInstallPerformance("manual_estimate_progress start kind=package total=1 current=" + displayName);
        SetInstallEstimationProgress(InstallEstimationProgressSource.ManualReestimate, 1, 0, displayName);
        try
        {
            RunPendingEstimateExclusive(delegate
            {
                SearchEstimatedInstallationDirectoryCore(package);
            });
            SetInstallEstimationProgress(InstallEstimationProgressSource.ManualReestimate, 1, 1, displayName);
        }
        finally
        {
            stopwatch.Stop();
            ClearInstallEstimationProgress();
            LogInstallPerformance("manual_estimate_progress done kind=package total=1 elapsedMs=" + stopwatch.ElapsedMilliseconds);
        }
    }

    public void SearchEstimatedInstallationDirectory(IEnumerable<ChartPackage> packages)
    {
        if (packages == null)
        {
            throw new ArgumentNullException("packages");
        }
        List<ChartPackage> packageList = [.. packages.Where(package => package != null)];
        if (packageList.Count == 0)
        {
            return;
        }
        var stopwatch = Stopwatch.StartNew();
        LogInstallPerformance("manual_estimate_progress start kind=packages total=" + packageList.Count);
        try
        {
            if (packageList.Count == 1)
            {
                string displayName = PendingInstallEstimateBatchRequest.GetDisplayName(packageList[0].path);
                SetInstallEstimationProgress(InstallEstimationProgressSource.ManualReestimate, 1, 0, displayName);
                RunPendingEstimateExclusive(delegate
                {
                    SearchEstimatedInstallationDirectoryCore(packageList[0]);
                });
                SetInstallEstimationProgress(InstallEstimationProgressSource.ManualReestimate, 1, 1, displayName);
            }
            else
            {
                ProcessManualPackageEstimateBatch(packageList);
            }
        }
        finally
        {
            stopwatch.Stop();
            ClearInstallEstimationProgress();
            LogInstallPerformance("manual_estimate_progress done kind=packages total=" + packageList.Count + " elapsedMs=" + stopwatch.ElapsedMilliseconds);
        }
    }

    private void ProcessManualPackageEstimateBatch(IReadOnlyList<ChartPackage> packageList)
    {
        if (packageList == null || packageList.Count == 0)
        {
            return;
        }
        var request = new PendingInstallEstimateBatchRequest(
            PendingInstallEstimateBatchSource.ManualReestimate,
            packageList,
            PendingInstallEstimateBatchRequest.GetDisplayName(packageList.FirstOrDefault()?.path));
        string source = ToPendingEstimateBatchSourceLogValue(request.Source);
        int lowConfidenceCount = 0;
        int completed = 0;
        var executionPolicy = InstallEstimationExecutionPolicy.ForManualBatch();
        var stopwatch = Stopwatch.StartNew();
        LogInstallPerformance("pending_estimate_batch start source=" + source + " packages=" + request.PackageCount + " totalPackages=" + request.TotalPackageCount + " deferredPackages=" + request.DeferredPackageCount + " packageDegree=" + executionPolicy.WorkItemDegree + " display=" + (request.DisplayName ?? string.Empty));
        RunPendingEstimateExclusive(delegate
        {
            SetInstallEstimationProgress(InstallEstimationProgressSource.ManualReestimate, request.PackageCount, 0, request.DisplayName ?? string.Empty);
            PendingInstallEstimateEvaluationContext evaluationContext = CreatePendingInstallEstimateEvaluationContext();
            List<PendingInstallEstimateEvaluationRequest> evaluationRequests = PreparePendingInstallEstimateEvaluationRequests(request);
            ProcessPendingInstallEstimateEvaluationPipeline(request, source, CancellationToken.None, evaluationContext, evaluationRequests, executionPolicy, ref completed, ref lowConfidenceCount);
        });
        stopwatch.Stop();
        LogInstallPerformance("pending_estimate_batch done source=" + source + " packages=" + request.PackageCount + " totalPackages=" + request.TotalPackageCount + " deferredPackages=" + request.DeferredPackageCount + " packageDegree=" + executionPolicy.WorkItemDegree + " estimated=" + completed + " completed=" + completed + " elapsedMs=" + stopwatch.ElapsedMilliseconds + " lowConfidence=" + lowConfidenceCount);
    }

    private void SearchEstimatedInstallationDirectoryCore(PackageChartEntry chartEntry, bool asParallel = true, bool fixMode = false)
    {
        if (chartEntry?.Chart == null)
        {
            throw new ArgumentNullException("chartEntry");
        }
        using (rwlockBMSFilesInitializedAll.GetReaderGuard())
        {
            using (rwlockPendingInstallCharts.GetWriterGuard())
            {
                ClearDeferredEstimateReasonForEntriesUnsafe([chartEntry]);
            }
        }
        bool resolvedInstalledDirectory = false;
        if (!fixMode && chartEntry.Chart.Kind == ChartFileKind.Bmson)
        {
            using (rwlockBMSFilesInitializedAll.GetReaderGuard())
            {
                using (rwlockPendingInstallCharts.GetReaderGuard())
                {
                    if (ContainsInstalledChartUnsafe(chartEntry.Chart))
                    {
                        List<string> installedDirectories = GetDistinctInstalledDirectoriesForChartUnsafe(chartEntry.Chart);
                        if (installedDirectories.Count == 1)
                        {
                            ApplyResolvedInstallDestinationToEntries([chartEntry], installedDirectories[0]);
                            resolvedInstalledDirectory = true;
                        }
                    }
                }
            }
            if (resolvedInstalledDirectory)
            {
                return;
            }
        }
        SearchEstimatedInstallationDirectoryForChartsCore(null, [chartEntry], asParallel, fixMode ? ChartInstallationEstimateMode.ReinstallCorrection : ChartInstallationEstimateMode.Normal);
    }

    internal void SearchEstimatedInstallationDirectory(PackageChartEntry chartEntry, bool asParallel = true, bool fixMode = false)
    {
        if (chartEntry?.Chart == null)
        {
            throw new ArgumentNullException(nameof(chartEntry));
        }
        var stopwatch = Stopwatch.StartNew();
        string displayName = PendingInstallEstimateBatchRequest.GetDisplayName(chartEntry.Chart.Path);
        LogInstallPerformance("manual_estimate_progress start kind=chart total=1 current=" + displayName);
        SetInstallEstimationProgress(InstallEstimationProgressSource.ManualReestimate, 1, 0, displayName);
        try
        {
            RunPendingEstimateExclusive(delegate
            {
                SearchEstimatedInstallationDirectoryCore(chartEntry, asParallel, fixMode);
            });
            SetInstallEstimationProgress(InstallEstimationProgressSource.ManualReestimate, 1, 1, displayName);
        }
        finally
        {
            stopwatch.Stop();
            ClearInstallEstimationProgress();
            LogInstallPerformance("manual_estimate_progress done kind=chart total=1 elapsedMs=" + stopwatch.ElapsedMilliseconds);
        }
    }

    internal void SearchEstimatedInstallationDirectory(IEnumerable<PackageChartEntry> chartEntries, bool asParallel = true, bool fixMode = false)
    {
        if (chartEntries == null)
        {
            throw new ArgumentNullException(nameof(chartEntries));
        }
        List<PackageChartEntry> targetEntries = [.. chartEntries.Where(entry => entry?.Chart != null)];
        if (targetEntries.Count == 0)
        {
            return;
        }
        var stopwatch = Stopwatch.StartNew();
        LogInstallPerformance("manual_estimate_progress start kind=charts total=" + targetEntries.Count);
        try
        {
            List<ChartPackage> packageTargets;
            using (rwlockPendingInstallCharts.GetReaderGuard())
            {
                packageTargets = [.. ChartPackagesPending.Where(package => package != null && PackageContainsAnyChartTarget(package, targetEntries))];
            }
            List<PackageChartEntry> looseEntries = [.. targetEntries
                .Where(entry => !PackageTargetsContainChartEntry(packageTargets, entry))];
            if (!fixMode && looseEntries.Count == 0 && packageTargets.Count > 1)
            {
                ProcessManualPackageEstimateBatch(packageTargets);
            }
            else
            {
                List<object> workItems = [.. packageTargets.Cast<object>()
, .. looseEntries.Cast<object>()];
                int totalWorkCount = workItems.Count;
                RunPendingEstimateExclusive(delegate
                {
                    for (int i = 0; i < totalWorkCount; i++)
                    {
                        object workItem = workItems[i];
                        string displayName = workItem is ChartPackage package
                            ? PendingInstallEstimateBatchRequest.GetDisplayName(package.path)
                            : PendingInstallEstimateBatchRequest.GetDisplayName(((PackageChartEntry)workItem).Chart.Path);
                        SetInstallEstimationProgress(InstallEstimationProgressSource.ManualReestimate, totalWorkCount, i, displayName);
                        if (workItem is ChartPackage targetPackage)
                        {
                            SearchEstimatedInstallationDirectoryCore(targetPackage);
                        }
                        else
                        {
                            SearchEstimatedInstallationDirectoryCore((PackageChartEntry)workItem, asParallel, fixMode);
                        }
                        SetInstallEstimationProgress(InstallEstimationProgressSource.ManualReestimate, totalWorkCount, i + 1, displayName);
                    }
                });
            }
        }
        finally
        {
            stopwatch.Stop();
            ClearInstallEstimationProgress();
            LogInstallPerformance("manual_estimate_progress done kind=charts total=" + targetEntries.Count + " elapsedMs=" + stopwatch.ElapsedMilliseconds);
        }
    }

    internal void SearchEstimatedInstallationDirectoryForLooseCharts(IEnumerable<PackageChartEntry> chartEntries, bool asParallel = true)
    {
        if (chartEntries == null)
        {
            throw new ArgumentNullException("chartEntries");
        }
        List<PackageChartEntry> targetEntries = [.. chartEntries.Where(entry => entry?.Chart != null)];
        if (targetEntries.Count == 0)
        {
            return;
        }
        var stopwatch = Stopwatch.StartNew();
        LogInstallPerformance("manual_estimate_progress start kind=loose_files total=" + targetEntries.Count);
        try
        {
            RunPendingEstimateExclusive(delegate
            {
                int totalWorkCount = targetEntries.Count;
                for (int i = 0; i < totalWorkCount; i++)
                {
                    PackageChartEntry targetEntry = targetEntries[i];
                    string displayName = PendingInstallEstimateBatchRequest.GetDisplayName(targetEntry.Chart.Path);
                    SetInstallEstimationProgress(InstallEstimationProgressSource.ManualReestimate, totalWorkCount, i, displayName);
                    SearchEstimatedInstallationDirectoryCore(targetEntry, asParallel);
                    SetInstallEstimationProgress(InstallEstimationProgressSource.ManualReestimate, totalWorkCount, i + 1, displayName);
                }
            });
        }
        finally
        {
            stopwatch.Stop();
            ClearInstallEstimationProgress();
            LogInstallPerformance("manual_estimate_progress done kind=loose_files total=" + targetEntries.Count + " elapsedMs=" + stopwatch.ElapsedMilliseconds);
        }
    }

    public void SearchMergeDestinationForPendingPackage(ChartPackage package)
    {
        if (package == null)
        {
            throw new ArgumentNullException("package");
        }
        if (!ChartPackagesPending.Contains(package))
        {
            return;
        }
        List<PackageChartEntry> entries = [.. package.ChartEntries.Where(entry => entry?.Chart != null)];
        if (entries.Count == 0)
        {
            LogInstallPerformance("estimated_merge_skip reason=no_target package=" + package.path);
            return;
        }
        foreach (PackageChartEntry entry in entries)
        {
            entry.ClearInstallDestination();
        }
        LogInstallPerformance("estimated_merge_start package=" + package.path + " targets=" + entries.Count);
        package.DeferredEstimateReason = PendingEstimateDeferredReason.None;
        if (!TryResolveInstalledDestinationFromPackage(package, entries, out string resolvedDir))
        {
            SearchEstimatedInstallationDirectoryForChartsCore(package, entries, asParallel: true, ChartInstallationEstimateMode.MergeCandidateOnly);
            LogMergeDestinationResult(package, entries);
            return;
        }
        ApplyResolvedInstallDestinationToEntries(entries, resolvedDir);
        LogMergeDestinationResult(package, entries);
    }

    private void LogMergeDestinationResult(ChartPackage package, IReadOnlyCollection<PackageChartEntry> entries)
    {
        List<PackageChartEntry> targetEntries = [.. (entries ?? []).Where(entry => entry?.Chart != null)];
        int targetCount = targetEntries.Count;
        int num = targetEntries.Count(entry => !string.IsNullOrWhiteSpace(entry.Chart?.InstallDestination));
        if (num == 0)
        {
            LogInstallPerformance("estimated_merge_skip reason=unresolved package=" + package.path + " targets=" + targetCount);
            return;
        }
        string resolvedDestination = targetEntries.Select(entry => entry.Chart?.InstallDestination).Where(d => !string.IsNullOrWhiteSpace(d)).Distinct(StringComparer.OrdinalIgnoreCase).FirstOrDefault();
        bool metadataResolved = targetEntries.Any(entry => !string.IsNullOrWhiteSpace(entry.Chart?.InstallDestinationTitle) || !string.IsNullOrWhiteSpace(entry.Chart?.InstallDestinationArtist));
        LogInstallPerformance("estimated_merge_done package=" + package.path + " resolved=" + num + " targets=" + targetCount + " dst=" + resolvedDestination + " metadataResolved=" + metadataResolved);
    }

    internal void SearchMergeDestinationForPendingCharts(IEnumerable<PackageChartEntry> chartEntries)
    {
        if (chartEntries == null)
        {
            throw new ArgumentNullException(nameof(chartEntries));
        }
        List<PackageChartEntry> entries = [.. chartEntries.Where(entry => entry?.Chart != null)];
        if (entries.Count == 0)
        {
            LogInstallPerformance("estimated_merge_skip reason=no_target");
            return;
        }
        using (rwlockBMSFilesInitializedAll.GetReaderGuard())
        {
            using (rwlockPendingInstallCharts.GetWriterGuard())
            {
                using (rwlockBMSFiles.GetReaderGuard())
                {
                    ClearDeferredEstimateReasonForEntriesUnsafe(entries);
                }
            }
        }
        foreach (PackageChartEntry entry in entries)
        {
            entry.ClearInstallDestination();
            SearchEstimatedInstallationDirectoryForChartsCore(null, [entry], asParallel: true, ChartInstallationEstimateMode.MergeCandidateOnly);
        }
        int num = entries.Count(entry => !string.IsNullOrWhiteSpace(entry.Chart?.InstallDestination));
        if (num == 0)
        {
            LogInstallPerformance("estimated_merge_skip reason=unresolved targets=" + entries.Count);
            return;
        }
        LogInstallPerformance("estimated_merge_done resolved=" + num + " targets=" + entries.Count);
    }

    /// <summary>
    /// 指定された pending package 群を、インストール先ディレクトリへ強制インストールします。
    /// </summary>
    public void ForceInstallPendingPackages(IEnumerable<ChartPackage> packages)
    {
        ForceInstallPendingPackages(packages, approveNormalInstallOverride: null);
    }

    internal void ForceInstallPendingPackages(IEnumerable<ChartPackage> packages, bool? approveNormalInstallOverride)
    {
        ISet<ChartPackage> approvedNormalInstallOverridePackages = null;
        if (approveNormalInstallOverride == true)
        {
            approvedNormalInstallOverridePackages = new HashSet<ChartPackage>((packages ?? []).Where(package => package != null));
        }
        ForceInstallPendingPackages(packages, approveNormalInstallOverride, approvedNormalInstallOverridePackages);
    }

    internal void ForceInstallPendingPackages(IEnumerable<ChartPackage> packages, bool? approveNormalInstallOverride, ISet<ChartPackage> approvedNormalInstallOverridePackages)
    {
        if (packages == null)
        {
            throw new ArgumentNullException("packages");
        }
        if (TryBlockLr2SongDbSyncMutation(nameof(ForceInstallPendingPackages)))
        {
            return;
        }
        using (rwlockBMSFilesInitializedAll.GetReaderGuard())
        {
            using (rwlockPendingInstallCharts.GetWriterGuard())
            {
                using (rwlockBMSFiles.GetWriterGuard())
                {
                    using (rwlockSongDBInstall.GetWriterGuard())
                    {
                        if (BMSFiles == null)
                        {
                            return;
                        }
                        ForceInstallBatchResult result = packageInstallService.ForceInstallPackages(
                            packages,
                            ChartPackagesPending,
                            delegate (ChartPackage pendingPackage)
                            {
                                if (approvedNormalInstallOverridePackages?.Contains(pendingPackage) == true
                                    || approvedNormalInstallOverridePackages?.Any(package =>
                                        package != null
                                        && pendingPackage != null
                                        && !string.IsNullOrWhiteSpace(package.path)
                                        && !string.IsNullOrWhiteSpace(pendingPackage.path)
                                        && string.Equals(package.path, pendingPackage.path, StringComparison.OrdinalIgnoreCase)) == true)
                                {
                                    return true;
                                }
                                return approveNormalInstallOverride == false
                                    ? false
                                    : ShowOperationDialog(Resources.Confirm_NormalInstallOverride, Resources.Confirm_NormalInstallTitle, MessageBoxButton.YesNo, MessageBoxImage.Question, MessageBoxResult.Yes) == MessageBoxResult.Yes;
                            },
                            delegate (IEnumerable<ChartPackage> packagesToInstall, List<ChartPackage> deferredInstalledPackages)
                            {
                                return installChartPackages(packagesToInstall, null, null, deferredInstalledPackages);
                            },
                            info => NLogWrapper.FileLogger?.Info(info));
                        if (result.Requested == 0)
                        {
                            return;
                        }
                        NLogWrapper.FileLogger?.Info("force_install_batch start requested=" + result.Requested);
                        if (result.PendingPackagesToRemove.Count > 0)
                        {
                            stateApplier.ApplyPendingPackageMutationDelta(BuildPendingPackageMutationDelta(packagesToRemove: result.PendingPackagesToRemove));
                        }
                        int num5 = 0;
                        if (result.DeferredInstalledPackages.Count > 0)
                        {
                            var hashSet3 = new HashSet<ChartPackage>(ChartPackagesInstalled.Where(pkg => pkg != null));
                            List<ChartPackage> list5 = [.. ChartPackagesInstalled.Where(pkg => pkg != null)];
                            foreach (ChartPackage deferredInstalledPackage in result.DeferredInstalledPackages)
                            {
                                if (deferredInstalledPackage != null && hashSet3.Add(deferredInstalledPackage))
                                {
                                    list5.Add(deferredInstalledPackage);
                                    num5++;
                                }
                            }
                            if (num5 > 0)
                            {
                                ChartPackagesInstalled = new DispatcherCollection<ChartPackage>(new ObservableCollection<ChartPackage>(list5), DispatcherHelper.UIDispatcher);
                            }
                        }
                        NLogWrapper.FileLogger?.Info("force_install_batch summary requested=" + result.Requested + " processed=" + result.Processed + " succeeded=" + result.Succeeded + " failed=" + result.Failed + " skipped=" + result.Skipped + " pendingRemoved=" + result.PendingPackagesToRemove.Count + " installedAdded=" + num5);
                    }
                }
            }
        }
    }

    private int CountComponentMoveTargetsForPackage(ChartPackage package, string destinationDirectory, ISet<string> excludedComponentPaths)
    {
        if (package == null || string.IsNullOrWhiteSpace(destinationDirectory))
        {
            return 0;
        }
        try
        {
            string path = package.path;
            List<string> list = null;
            if (LongPathFileSystem.FileExists(path))
            {
                list = [path];
            }
            else
            {
                if (!LongPathFileSystem.DirectoryExists(path))
                {
                    return 0;
                }
                list = [.. LongPathFileSystem.EnumerateFileSystemEntries(path)];
            }
            List<ChartFile> charts = [.. (package.ChartEntries ?? [])
                .Select(entry => entry?.Chart)
                .Where(chart => chart != null)];
            var hashSet = new HashSet<string>(list, StringComparer.OrdinalIgnoreCase);
            var hashSet2 = new HashSet<string>(charts.Where(chart => hashSet.Contains(chart.Path)).Select(chart => chart.Path), StringComparer.OrdinalIgnoreCase);
            List<string> installComponentFiles = [.. list.Where(p => !hashSet2.Contains(p))];
            var excludedPathSet = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            if (excludedComponentPaths != null)
            {
                foreach (string excludedPath in excludedComponentPaths)
                {
                    if (!string.IsNullOrWhiteSpace(excludedPath))
                    {
                        excludedPathSet.Add(excludedPath);
                    }
                }
            }
            foreach (ChartFile chart in charts)
            {
                if (!string.IsNullOrWhiteSpace(chart.Path))
                {
                    excludedPathSet.Add(chart.Path);
                }
            }
            installComponentFiles = [.. installComponentFiles.Where(p => !excludedPathSet.Contains(p))];
            return BuildComponentMovePlan(installComponentFiles, destinationDirectory, excludedPathSet).PlanItems.Count;
        }
        catch
        {
            return 0;
        }
    }

    private ChartPackage CreateInstalledDisplayPackageForResourceOnlyMerge(ChartPackage originalPackage, string destinationDirectory)
    {
        if (originalPackage == null || string.IsNullOrWhiteSpace(destinationDirectory))
        {
            return null;
        }
        var hashSet = new HashSet<string>((originalPackage.ChartEntries ?? [])
            .Select(entry => ChartLookupKey.GetPrimaryHash(entry?.Chart))
            .Where(key => !string.IsNullOrWhiteSpace(key)), StringComparer.OrdinalIgnoreCase);
        if (hashSet.Count == 0)
        {
            return null;
        }
        List<string> destinationPaths = GetInstalledDirectChildPathsByPrimaryHashesUnsafe(hashSet, destinationDirectory);
        if (destinationPaths.Count == 0)
        {
            return null;
        }
        List<ChartFile> destinationCharts = OverlayInstallDestinationRuntimeStates(CreateOwnedChartFilesForExactPathsUnsafe(
            destinationPaths,
            includeWarningSnapshot: false,
            includeResourceReferences: false));
        List<PackageChartEntry> entries = [.. destinationCharts.Where(delegate (ChartFile chart)
        {
            if (chart == null || string.IsNullOrWhiteSpace(chart.Path))
            {
                return false;
            }
            string key = ChartLookupKey.GetPrimaryHash(chart);
            if (string.IsNullOrWhiteSpace(key) || !hashSet.Contains(key))
            {
                return false;
            }
            return true;
        }).Select(PackageChartEntry.FromChart).Where(entry => entry?.Chart != null)];
        if (entries.Count == 0)
        {
            return null;
        }
        ChartPackage displayPackage = ChartPackage.FromChartEntries(entries);
        displayPackage.path = destinationDirectory;
        displayPackage.delete_parent = false;
        return displayPackage;
    }

    /// <summary>
    /// 指定された pending package 群を推定されたインストール先ディレクトリへインストールします。
    /// SmartOverwrite ロジックによるコンポーネント移動計画を構築して実行します。
    /// </summary>
    public void InstallPendingPackagesToEstimatedDestinations(IEnumerable<ChartPackage> packages)
    {
        if (packages == null)
        {
            throw new ArgumentNullException("packages");
        }
        if (TryBlockLr2SongDbSyncMutation(nameof(InstallPendingPackagesToEstimatedDestinations)))
        {
            return;
        }
        BmsLibraryOptionsSnapshot options = CurrentOptionsSnapshot;
        var totalStopwatch = Stopwatch.StartNew();
        using (rwlockBMSFilesInitializedAll.GetReaderGuard())
        {
            using (rwlockPendingInstallCharts.GetWriterGuard())
            {
                using (rwlockBMSFiles.GetWriterGuard())
                {
                    using (rwlockSongDBInstall.GetWriterGuard())
                    {
                        bool deletePendingPackageSourceAfterInstall = options.DeletePendingPackageSourceAfterInstall;
                        PendingInstallBatchPlan installPlan = packageInstallService.BuildEstimatedInstallBatchPlan(
                            packages,
                            ChartPackagesPending,
                            CreateInstalledChartKeySnapshotExcludingChartsUnsafe([], "install_pending_estimated_filter", 0L),
                            deletePendingPackageSourceAfterInstall,
                            CountComponentMoveTargetsForPackage);
                        if (installPlan.SelectedPendingPackages.Count == 0)
                        {
                            totalStopwatch.Stop();
                            LogInstallPerformance("install_pending_packages_to_estimated_destinations skipped reason=no_pending_target filterMs=" + installPlan.FilterMs + " totalMs=" + totalStopwatch.ElapsedMilliseconds);
                            return;
                        }
                        if (installPlan.Groups.Count == 0 && installPlan.DeferredManualHoldCount > 0)
                        {
                            totalStopwatch.Stop();
                            LogInstallPerformance("install_pending_packages_to_estimated_destinations skipped reason=deferred_manual_merge_hold deferredManualHold=" + installPlan.DeferredManualHoldCount + " selected=" + installPlan.SelectedPendingCount + " filterMs=" + installPlan.FilterMs + " totalMs=" + totalStopwatch.ElapsedMilliseconds);
                            return;
                        }
                        LogInstallPerformance("install_pending_packages_to_estimated_destinations start selected=" + installPlan.SelectedPendingCount + " groups=" + installPlan.Groups.Count + " groupedPackages=" + installPlan.GroupedPackageCount + " installTargets=" + installPlan.InstallTargetFileCount + " deferredManualHold=" + installPlan.DeferredManualHoldCount + " deleteSourceContents=" + deletePendingPackageSourceAfterInstall + " filterMs=" + installPlan.FilterMs + " groupBuildMs=" + installPlan.GroupBuildMs + " planBuildMs=" + installPlan.PlanBuildMs);
                        var batchApplyContext = new EstimatedInstallBatchApplyContext();
                        PendingInstallBatchResult batchResult = packageInstallService.ExecuteEstimatedInstallBatchPlan(
                            installPlan,
                            deletePendingPackageSourceAfterInstall,
                            (installPackages, destinationDirectory, deferredMaintenanceCharts, deferredInstalledPackages, excludedComponentPathsByPackage, existingHashes, skipInstalledPackageWhenNoBms, deleteSourceContentsAfterSuccessfulInstall) => installChartPackages(installPackages, destinationDirectory, deferredMaintenanceCharts, deferredInstalledPackages, excludedComponentPathsByPackage, existingHashes, skipInstalledPackageWhenNoBms, deleteSourceContentsAfterSuccessfulInstall, batchApplyContext),
                            CreateInstalledDisplayPackageForResourceOnlyMerge,
                            (cleanupOnlyPackage) =>
                            {
                                bool cleanupSucceeded = TryCleanupPendingPackageSourceForEstimatedInstall(cleanupOnlyPackage, out CleanupSourceKind sourceKind);
                                return (cleanupSucceeded, sourceKind);
                            },
                            LogInstallPerformance);
                        dbGateway.DeleteInstallRows(batchResult.InstallRowsToDelete);
                        bool canUseResourceHealthIndexDelta = IsResourceHealthIndexCurrent();
                        var libraryStateApplyStopwatch = Stopwatch.StartNew();
                        using (canUseResourceHealthIndexDelta ? SuppressResourceHealthIndexInvalidation() : null)
                        {
                            ApplyEstimatedInstallBatchLibraryState(batchApplyContext);
                        }
                        libraryStateApplyStopwatch.Stop();
                        var pendingApplyStopwatch = Stopwatch.StartNew();
                        int pendingCountBeforeApply = ChartPackagesPending.Count;
                        int pendingRemovedTotal = batchResult.PendingPackagesToRemove.Count;
                        if (pendingRemovedTotal > 0)
                        {
                            List<ChartPackage> remainingPending = [.. ChartPackagesPending.Where(pkg => pkg != null && !batchResult.PendingPackagesToRemove.Contains(pkg))];
                            ChartPackagesPending = new DispatcherCollection<ChartPackage>(new ObservableCollection<ChartPackage>(remainingPending), DispatcherHelper.UIDispatcher);
                        }
                        int pendingCountAfterApply = ChartPackagesPending.Count;
                        pendingApplyStopwatch.Stop();
                        var installedApplyStopwatch = Stopwatch.StartNew();
                        int installedCountBeforeApply = ChartPackagesInstalled.Count;
                        int installedAddedTotal = batchResult.DeferredInstalledPackages.Count;
                        if (installedAddedTotal > 0)
                        {
                            var installedSet = new HashSet<ChartPackage>(ChartPackagesInstalled.Where(pkg => pkg != null));
                            List<ChartPackage> mergedInstalled = [.. ChartPackagesInstalled.Where(pkg => pkg != null)];
                            foreach (ChartPackage installedPackage in batchResult.DeferredInstalledPackages)
                            {
                                if (installedPackage != null && installedSet.Add(installedPackage))
                                {
                                    mergedInstalled.Add(installedPackage);
                                }
                            }
                            ChartPackagesInstalled = new DispatcherCollection<ChartPackage>(new ObservableCollection<ChartPackage>(mergedInstalled), DispatcherHelper.UIDispatcher);
                        }
                        int installedCountAfterApply = ChartPackagesInstalled.Count;
                        installedApplyStopwatch.Stop();
                        var maintenanceStopwatch = Stopwatch.StartNew();
                        List<ChartFile> estimatedInstallMaintenanceTargets = BuildEstimatedInstallMaintenanceTargets(batchResult.DeferredMaintenanceCharts);
                        if (estimatedInstallMaintenanceTargets.Count > 0)
                        {
                            setMaintenanceInfo(
                                estimatedInstallMaintenanceTargets,
                                forceUpdate: true,
                                resourceHealthIndexUpdateMode: ResourceHealthIndexUpdateMode.DeltaOnUpdates,
                                resourceHealthMutationReason: "install_package_estimated");
                        }
                        List<ChartFile> estimatedInstallInlineTargets = BuildEstimatedInstallMaintenanceTargets(
                            estimatedInstallMaintenanceTargets.Concat(
                                CreateAddedBmsonChartProjections(batchApplyContext.AddedCharts)));
                        BuildAndPersistInlineChartInfoForInstalledCharts(
                            "install_package_estimated_inline",
                            estimatedInstallInlineTargets);
                        maintenanceStopwatch.Stop();
                        if (deletePendingPackageSourceAfterInstall && batchResult.CleanupOnlySucceeded > 0)
                        {
                            ShowOperationDialog(string.Format(Resources.Warn_estimated_install_cleanup_only_completed, batchResult.CleanupOnlySucceeded), Resources.MessageBoxTitle_Warning, MessageBoxButton.OK, MessageBoxImage.Exclamation, MessageBoxResult.OK);
                        }
                        totalStopwatch.Stop();
                        LogInstallPerformance("install_pending_packages_to_estimated_destinations end libraryStateApplyMs=" + libraryStateApplyStopwatch.ElapsedMilliseconds + " pendingApplyMs=" + pendingApplyStopwatch.ElapsedMilliseconds + " pendingBeforeApply=" + pendingCountBeforeApply + " pendingRemovedTotal=" + pendingRemovedTotal + " pendingAfterApply=" + pendingCountAfterApply + " installedApplyMs=" + installedApplyStopwatch.ElapsedMilliseconds + " installedBeforeApply=" + installedCountBeforeApply + " installedAddedTotal=" + installedAddedTotal + " installedAfterApply=" + installedCountAfterApply + " maintenanceTargets=" + estimatedInstallMaintenanceTargets.Count + " maintenanceMs=" + maintenanceStopwatch.ElapsedMilliseconds + " cleanupOnlyCandidates=" + installPlan.CleanupOnlyCandidates.Count + " cleanupOnlySucceeded=" + batchResult.CleanupOnlySucceeded + " cleanupOnlyFailed=" + batchResult.CleanupOnlyFailed + " cleanupOnlyMissingSource=" + batchResult.CleanupOnlyMissingSource + " deferredManualHold=" + installPlan.DeferredManualHoldCount + " totalMs=" + totalStopwatch.ElapsedMilliseconds);
                    }
                }
            }
        }
    }

    /// <summary>
    /// 指定された installed package record 群をリストから削除します。
    /// </summary>
    public void RemoveInstalledPackageRecords(IEnumerable<ChartPackage> packages)
    {
        using (rwlockBMSFilesInitializedAll.GetReaderGuard())
        {
            using (rwlockPendingInstallCharts.GetWriterGuard())
            {
                using (rwlockSongDBInstall.GetWriterGuard())
                {
                    ChartPackagesInstalled.Remove(packages);
                }
            }
        }
    }

    private PendingPackageMutationDelta BuildPendingPackageMutationDelta(IEnumerable<ChartPackage> packagesToRemove = null, IEnumerable<string> chartPathsToRemove = null, bool clearAll = false)
    {
        return packageInstallService.BuildPendingPackageMutationDelta(ChartPackagesPending, packagesToRemove, chartPathsToRemove, clearAll);
    }

    private void ApplyPendingPackageMutationDelta(PendingPackageMutationDelta delta)
    {
        stateApplier.ApplyPendingPackageMutationDelta(delta);
    }

    /// <summary>
    /// 指定された pending package 群を pending リストから削除します。
    /// </summary>
    public void RemovePendingPackages(IEnumerable<ChartPackage> packages)
    {
        List<ChartPackage> managedPackagesToCleanup = [];
        using (rwlockBMSFilesInitializedAll.GetReaderGuard())
        {
            using (rwlockPendingInstallCharts.GetWriterGuard())
            {
                using (rwlockSongDBInstall.GetWriterGuard())
                {
                    managedPackagesToCleanup = ResolveManagedPendingPackagesForCleanup(packages);
                    RemovePendingPackagesFromPendingListAndInstallRows(packages);
                }
            }
        }
        CleanupManagedPendingPackageSources(managedPackagesToCleanup, "pending_remove_selected");
    }

    /// <summary>
    /// 全ての installed package record をリストからクリアします。
    /// </summary>
    public void RemoveInstalledPackageRecordsAll()
    {
        using (rwlockBMSFilesInitializedAll.GetReaderGuard())
        {
            using (rwlockPendingInstallCharts.GetWriterGuard())
            {
                using (rwlockSongDBInstall.GetWriterGuard())
                {
                    ChartPackagesInstalled.Clear();
                }
            }
        }
    }

    /// <summary>
    /// 全ての Pending パッケージをリストからクリアします。
    /// </summary>
    public void RemovePendingPackagesAll()
    {
        List<ChartPackage> managedPackagesToCleanup = [];
        using (rwlockBMSFilesInitializedAll.GetReaderGuard())
        {
            using (rwlockPendingInstallCharts.GetWriterGuard())
            {
                using (rwlockSongDBInstall.GetWriterGuard())
                {
                    managedPackagesToCleanup = ResolveManagedPendingPackagesForCleanup(ChartPackagesPending);
                    stateApplier.ApplyPendingPackageMutationDelta(BuildPendingPackageMutationDelta(clearAll: true));
                }
            }
        }
        CleanupManagedPendingPackageSources(managedPackagesToCleanup, "pending_remove_all");
    }

    public List<ChartPackage> GetPendingPackagesContainingOnlyInstalledCharts()
    {
        using (rwlockBMSFilesInitializedAll.GetReaderGuard())
        {
            using (rwlockPendingInstallCharts.GetReaderGuard())
            {
                List<ChartPackage> list = packageInstallService.GetPendingPackagesContainingOnlyInstalledCharts(ChartPackagesPending, chart => ContainsInstalledChartUnsafe(chart));
                NLogWrapper.FileLogger?.Info("advanced_pending_cleanup scan pendingTotal=" + ChartPackagesPending.Count + " eligible=" + list.Count);
                return list;
            }
        }
    }

    private List<ChartPackage> ResolveManagedPendingPackagesForCleanup(IEnumerable<ChartPackage> packages)
    {
        List<ChartPackage> requestedPackages = packageInstallService.DeduplicatePackagesByPathOrReference(packages);
        List<ChartPackage> pendingPackages = [.. ChartPackagesPending.Where(pkg => pkg != null)];
        var cleanupPackages = new List<ChartPackage>();
        foreach (ChartPackage requestedPackage in requestedPackages)
        {
            ChartPackage pendingPackage = pendingPackages.FirstOrDefault(pkg =>
                ReferenceEquals(pkg, requestedPackage)
                || (!string.IsNullOrWhiteSpace(pkg.path)
                    && !string.IsNullOrWhiteSpace(requestedPackage?.path)
                    && string.Equals(pkg.path, requestedPackage.path, StringComparison.OrdinalIgnoreCase)));
            if (!string.IsNullOrWhiteSpace(pendingPackage?.path) && TempDirectoryPublisher.IsManagedPath(pendingPackage.path))
            {
                cleanupPackages.Add(pendingPackage);
            }
        }
        return cleanupPackages;
    }

    private void CleanupManagedPendingPackageSources(IEnumerable<ChartPackage> packages, string reason)
    {
        IEnumerable<string> paths = (packages ?? [])
            .Where(pkg => !string.IsNullOrWhiteSpace(pkg?.path))
            .Select(pkg => pkg.path);
        CleanupManagedInstallSources(paths, reason);
    }

    private enum PrepareSkipReason
    {
        None,
        MissingInstallDestination,
        ChartHasMultipleInstalledDirectories,
        PackageHasSplitInstalledDirectories
    }

    private void TryRegroupPendingPackagesForSourceDirectoriesUnsafe(IEnumerable<string> sourceDirectoryPaths)
    {
        List<string> sourceDirectories = [.. (sourceDirectoryPaths ?? [])
            .Where(path => !string.IsNullOrWhiteSpace(path))
            .Distinct(StringComparer.OrdinalIgnoreCase)];
        if (sourceDirectories.Count == 0)
        {
            return;
        }
        IInstalledChartLookupIndex installedDirectoryIndex = CreateInstalledChartLookupSnapshotUnsafe();
        foreach (string sourceDirectoryPath in sourceDirectories)
        {
            TryRegroupPendingPackagesForSourceDirectoryUnsafe(sourceDirectoryPath, installedDirectoryIndex);
        }
    }

    private void TryRegroupPendingPackagesForSourceDirectoryUnsafe(string sourceDirectoryPath, IInstalledChartLookupIndex installedDirectoryIndex)
    {
        if (string.IsNullOrWhiteSpace(sourceDirectoryPath))
        {
            return;
        }
        List<ChartPackage> sourcePackages = [.. ChartPackagesPending.Where(pendingPackage => pendingPackage != null && string.Equals(GetPendingPackageSourceDirectoryPath(pendingPackage), sourceDirectoryPath, StringComparison.OrdinalIgnoreCase))];
        if (sourcePackages.Count < 2)
        {
            return;
        }
        if (sourcePackages.Any(pendingPackage => string.Equals(NormalizePendingPackagePath(pendingPackage.path), sourceDirectoryPath, StringComparison.OrdinalIgnoreCase)))
        {
            LogInstallPerformance("pending_regroup skip reason=already_directory_package source=" + sourceDirectoryPath + " packages=" + sourcePackages.Count);
            return;
        }
        if (sourcePackages.Any(pendingPackage => pendingPackage != null && pendingPackage.DeferredEstimateReason != PendingEstimateDeferredReason.None))
        {
            LogInstallPerformance("pending_regroup skip reason=deferred_estimate source=" + sourceDirectoryPath + " packages=" + sourcePackages.Count);
            return;
        }
        if (!TryBuildRegroupedPendingPackage(sourceDirectoryPath, sourcePackages, installedDirectoryIndex, out ChartPackage regroupedPackage, out string resolvedDestinationDirectory, out string skipReason))
        {
            LogInstallPerformance("pending_regroup skip reason=" + skipReason + " source=" + sourceDirectoryPath + " packages=" + sourcePackages.Count);
            return;
        }
        ReinitializePendingWarningsForPackageUnsafe(regroupedPackage, CreateInstalledChartKeySnapshotExcludingChartsUnsafe([]));
        ReplacePendingPackagesWithRegroupedPackageUnsafe(sourcePackages, regroupedPackage);
        dbGateway.DeleteInstallRows(sourcePackages.Select(pendingPackage => pendingPackage.path));
        dbGateway.UpsertInstallRows([regroupedPackage]);
        List<PackageChartEntry> regroupedPackageEntries = regroupedPackage.ChartEntries;
        bool metadataResolved = regroupedPackageEntries.Any(entry => !string.IsNullOrWhiteSpace(entry?.Chart?.InstallDestinationTitle) || !string.IsNullOrWhiteSpace(entry?.Chart?.InstallDestinationArtist));
        LogInstallPerformance("pending_regroup success source=" + sourceDirectoryPath + " packages=" + sourcePackages.Count + " files=" + regroupedPackageEntries.Count + " dst=" + resolvedDestinationDirectory + " metadataResolved=" + metadataResolved);
    }

    private bool TryBuildRegroupedPendingPackage(string sourceDirectoryPath, List<ChartPackage> sourcePackages, IInstalledChartLookupIndex installedDirectoryIndex, out ChartPackage regroupedPackage, out string resolvedDestinationDirectory, out string skipReason)
    {
        regroupedPackage = null;
        resolvedDestinationDirectory = null;
        skipReason = "unknown";
        List<PackageChartEntry> regroupedEntries = [];
        foreach (ChartPackage sourcePackage in sourcePackages)
        {
            foreach (PackageChartEntry sourceEntry in sourcePackage.ChartEntries)
            {
                ChartFile sourceChart = sourceEntry?.Chart;
                if (sourceChart == null)
                {
                    continue;
                }
                if (!regroupedEntries.Any(entry => entry.IsSameChartTarget(sourceEntry)))
                {
                    regroupedEntries.Add(sourceEntry);
                }
            }
        }
        if (regroupedEntries.Count == 0)
        {
            skipReason = "no_files";
            return false;
        }
        var expectedDirectories = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (PackageChartEntry regroupedEntry in regroupedEntries)
        {
            if (!TryResolvePendingFileExpectedInstallDirectory(regroupedEntry.Chart, installedDirectoryIndex, out string expectedDirectory, out string unresolvedReason))
            {
                skipReason = unresolvedReason;
                return false;
            }
            expectedDirectories.Add(expectedDirectory);
            if (expectedDirectories.Count > 1)
            {
                skipReason = "split_expected_destination";
                return false;
            }
        }
        resolvedDestinationDirectory = expectedDirectories.Single();
        if (regroupedEntries.Count == 0)
        {
            skipReason = "no_files";
            return false;
        }
        ApplyRegroupedInstallDestinationToEntries(regroupedEntries, resolvedDestinationDirectory, installedDirectoryIndex);
        regroupedPackage = ChartPackage.FromChartEntries(regroupedEntries);
        regroupedPackage.path = sourceDirectoryPath;
        regroupedPackage.delete_parent = false;
        return true;
    }

    private void ApplyRegroupedInstallDestinationToEntries(IEnumerable<PackageChartEntry> entries, string destinationDirectory, IInstalledChartLookupIndex installedDirectoryIndex)
    {
        List<PackageChartEntry> missingEntries = [];
        foreach (PackageChartEntry entry in (entries ?? []).Where(entry => entry?.Chart != null))
        {
            if (BmsLibraryInstallEstimationService.GetDistinctInstalledDirectoriesForChart(installedDirectoryIndex, entry.Chart).Count > 0)
            {
                entry.ClearInstallDestination();
            }
            else
            {
                missingEntries.Add(entry);
            }
        }
        ApplyResolvedInstallDestinationPathAndMetadataToEntries(missingEntries, destinationDirectory);
    }

    private bool TryResolvePendingFileExpectedInstallDirectory(ChartFile chart, IInstalledChartLookupIndex installedDirectoryIndex, out string expectedDirectory, out string reason)
    {
        expectedDirectory = null;
        reason = "missing_expected_destination";
        if (chart == null)
        {
            reason = "null_chart";
            return false;
        }
        List<string> installedDirectories = BmsLibraryInstallEstimationService.GetDistinctInstalledDirectoriesForChart(installedDirectoryIndex, chart);
        if (installedDirectories.Count > 1)
        {
            reason = "multiple_installed_directories";
            return false;
        }
        string installedDirectory = installedDirectories.FirstOrDefault();
        string estimatedDirectory = string.IsNullOrWhiteSpace(chart.InstallDestination) ? null : chart.InstallDestination;
        if (!string.IsNullOrWhiteSpace(installedDirectory) && !string.IsNullOrWhiteSpace(estimatedDirectory) && !installedDirectory.Equals(estimatedDirectory, StringComparison.OrdinalIgnoreCase))
        {
            reason = "installed_directory_conflict";
            return false;
        }
        expectedDirectory = installedDirectory ?? estimatedDirectory;
        if (string.IsNullOrWhiteSpace(expectedDirectory))
        {
            reason = "missing_expected_destination";
            return false;
        }
        return true;
    }

    private void ReinitializePendingWarningsForPackageUnsafe(ChartPackage package, IPrimaryHashLookup installedHashes)
    {
        if (package == null)
        {
            return;
        }
        bool isSingleFilePackage = !LongPathFileSystem.DirectoryExists(package.path);
        IPrimaryHashLookup installedHashLookup = installedHashes ?? EmptyPrimaryHashLookup.Instance;
        foreach (PackageChartEntry entry in package.ChartEntries)
        {
            ChartFile chart = entry?.Chart;
            if (chart == null)
            {
                continue;
            }

            bool isBmson = chart.Kind == ChartFileKind.Bmson;
            entry.ClearStructuredWarnings();

            string key = ChartLookupKey.GetPrimaryHash(chart);
            if (!string.IsNullOrWhiteSpace(key) && installedHashLookup.ContainsPrimaryHash(key))
            {
                entry.ClearWarningsByCategory(ChartWarningCategory.InstalledState);
                entry.SetWarning(ChartWarningKind.AlreadyInstalled, Resources.Warning_AlreadyInstalled);
            }
            else if (isSingleFilePackage)
            {
                entry.ClearWarningsByCategory(ChartWarningCategory.PackageLayout);
                entry.SetWarning(isBmson ? ChartWarningKind.SingleBmsonFile : ChartWarningKind.SingleBmsFile, isBmson ? Resources.Warning_SingleBmsonFile : Resources.Warning_SingleBmsFile);
            }
        }
        BmsLibraryPackageInstallService.ApplyPendingResourceHealthProjectionToEntries(package.ChartEntries);
        BmsLibraryPackageInstallService.ApplyNestedChartFileWarnings(package);
    }

    private void ReplacePendingPackagesWithRegroupedPackageUnsafe(List<ChartPackage> sourcePackages, ChartPackage regroupedPackage)
    {
        if (sourcePackages == null || sourcePackages.Count == 0 || regroupedPackage == null)
        {
            return;
        }
        List<ChartPackage> currentPendingPackages = [.. ChartPackagesPending.Where(pendingPackage => pendingPackage != null)];
        int insertIndex = currentPendingPackages.FindIndex(pendingPackage => sourcePackages.Contains(pendingPackage));
        if (insertIndex < 0)
        {
            insertIndex = currentPendingPackages.Count;
        }
        List<ChartPackage> replacedPendingPackages = [.. currentPendingPackages.Where(pendingPackage => !sourcePackages.Contains(pendingPackage))];
        replacedPendingPackages.Insert(insertIndex, regroupedPackage);
        ChartPackagesPending = new DispatcherCollection<ChartPackage>(new ObservableCollection<ChartPackage>(replacedPendingPackages), DispatcherHelper.UIDispatcher);
    }

    private static string GetPendingPackageSourceDirectoryPath(ChartPackage package)
    {
        return NormalizePendingPackagePath(package?.path) switch
        {
            string normalizedPath when string.IsNullOrWhiteSpace(normalizedPath) => null,
            string normalizedPath when ChartFileKindResolver.IsSupportedChartFilePath(normalizedPath) => NormalizePendingPackagePath(Path.GetDirectoryName(normalizedPath)),
            string normalizedPath => normalizedPath
        };
    }

    private static string NormalizePendingPackagePath(string path)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            return null;
        }
        return path.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
    }

    private bool HasResourceOverwriteTargetsForInstalledOnlyPackage(ChartPackage package, string destinationDir)
    {
        if (package == null || string.IsNullOrWhiteSpace(destinationDir))
        {
            return false;
        }
        List<ChartFile> charts = [.. (package.ChartEntries ?? [])
            .Select(entry => entry?.Chart)
            .Where(chart => chart != null)];
        if (charts.Count == 0)
        {
            return false;
        }
        var excludedPaths = new HashSet<string>(charts.Where(chart => !string.IsNullOrWhiteSpace(chart.Path)).Select(chart => chart.Path), StringComparer.OrdinalIgnoreCase);
        return CountComponentMoveTargetsForPackage(package, destinationDir, excludedPaths) > 0;
    }

    private bool IsPackageStillPending(ChartPackage package)
    {
        if (package == null)
        {
            return false;
        }
        return ChartPackagesPending.Any(pendingPkg => pendingPkg != null && (ReferenceEquals(pendingPkg, package) || (!string.IsNullOrWhiteSpace(pendingPkg.path) && !string.IsNullOrWhiteSpace(package.path) && pendingPkg.path.Equals(package.path, StringComparison.OrdinalIgnoreCase))));
    }

    public PendingInstalledOnlyResourceOverwriteResult OverwritePendingInstalledOnlyPackagesResources(IEnumerable<ChartPackage> packages, CancellationToken token = default, Action onEachProcessed = null)
    {
        if (packages == null)
        {
            throw new ArgumentNullException("packages");
        }
        BmsLibraryOptionsSnapshot options = CurrentOptionsSnapshot;
        int deferredProcessedCount = 0;
        try
        {
            using (rwlockBMSFilesInitializedAll.GetReaderGuard())
            {
                using (rwlockPendingInstallCharts.GetWriterGuard())
                {
                    using (rwlockBMSFiles.GetWriterGuard())
                    {
                        using (rwlockSongDBInstall.GetWriterGuard())
                        {
                            bool deletePendingPackageSourceAfterInstall = options.DeletePendingPackageSourceAfterInstall;
                            InstalledChartLookupIndexSnapshot installedDirectoryIndexSnapshot = CreateInstalledChartLookupSnapshotUnsafe();
                            NLogWrapper.FileLogger?.Info("advanced_pending_resource_overwrite scan pendingTotal=" + ChartPackagesPending.Count + " eligible=" + packageInstallService.DeduplicatePackagesByPathOrReference(packages).Count);
                            NLogWrapper.FileLogger?.Info("advanced_pending_resource_overwrite index_ready hashes=" + installedDirectoryIndexSnapshot.HashCount);
                            PendingResourceOverwriteExecutionResult executionResult = packageInstallService.ExecuteInstalledOnlyResourceOverwrite(
                                packages,
                                ChartPackagesPending,
                                deletePendingPackageSourceAfterInstall,
                                (pendingPackage) => CreateInstallEstimationService().TryPrepareInstalledOnlyPackageDestination(pendingPackage, installedDirectoryIndexSnapshot),
                                delegate (InstalledOnlyPackageResolutionResult resolution, ChartPackage pendingPackage)
                                {
                                    if (pendingPackage == null)
                                    {
                                        return null;
                                    }
                                    switch (resolution.Reason)
                                    {
                                        case InstalledDirectoryResolveReason.ChartHasMultipleInstalledDirectories:
                                            ChartFile multipleDirectoryChart = BmsLibraryInstallEstimationService.FindChartWithMultipleInstalledDirectories(pendingPackage, installedDirectoryIndexSnapshot);
                                            return "advanced_pending_resource_overwrite skip_chart_multi_dst path=" + pendingPackage.path + " chartPath=" + (multipleDirectoryChart?.Path ?? "(null)") + " hash=" + (ChartLookupKey.GetPrimaryHash(multipleDirectoryChart) ?? "(null)") + " dirCount=" + ((multipleDirectoryChart == null) ? 0 : BmsLibraryInstallEstimationService.GetDistinctInstalledDirectoriesForChart(installedDirectoryIndexSnapshot, multipleDirectoryChart).Count);
                                        case InstalledDirectoryResolveReason.PackageHasSplitInstalledDirectories:
                                            return "advanced_pending_resource_overwrite skip_package_split_dst path=" + pendingPackage.path + " dirCount=" + BmsLibraryInstallEstimationService.CountDistinctInstalledDirectoriesForPackage(pendingPackage, installedDirectoryIndexSnapshot);
                                        default:
                                            ChartFile missingDirectoryChart = BmsLibraryInstallEstimationService.FindChartWithMissingInstalledDirectory(pendingPackage, installedDirectoryIndexSnapshot);
                                            return "advanced_pending_resource_overwrite skip_missing_instl_dst path=" + pendingPackage.path + " chartPath=" + (missingDirectoryChart?.Path ?? "(null)") + " hash=" + (ChartLookupKey.GetPrimaryHash(missingDirectoryChart) ?? "(null)");
                                    }
                                },
                                (pendingPackage, destinationDir) => HasResourceOverwriteTargetsForInstalledOnlyPackage(pendingPackage, destinationDir),
                                delegate (ChartPackage pendingPackage, string destinationDir)
                                {
                                    try
                                    {
                                        InstallPendingPackagesToEstimatedDestinations([pendingPackage]);
                                        return true;
                                    }
                                    catch (Exception ex)
                                    {
                                        string displayedExceptionMessage = GetDisplayedExceptionMessage(ex);
                                        NLogWrapper.FileLogger?.Warn(ex, "advanced_pending_resource_overwrite install_failed_exception path=" + pendingPackage.path + " dst=" + destinationDir + " error=" + displayedExceptionMessage);
                                        ShowOperationDialog(string.Format(Resources.Error_InstallFailed, pendingPackage.path, destinationDir, displayedExceptionMessage), Resources.MessageBoxTitle_Error, MessageBoxButton.OK, MessageBoxImage.Hand, MessageBoxResult.OK);
                                        return false;
                                    }
                                },
                                (cleanupPackage) =>
                                {
                                    bool cleanupSucceeded = TryCleanupPendingPackageSourceForEstimatedInstall(cleanupPackage, out CleanupSourceKind sourceKind);
                                    return (cleanupSucceeded, sourceKind);
                                },
                                (pendingPackage) => IsPackageStillPending(pendingPackage),
                                token,
                                () => deferredProcessedCount++,
                                info =>
                                {
                                    if (!string.IsNullOrWhiteSpace(info))
                                    {
                                        NLogWrapper.FileLogger?.Info(info);
                                    }
                                });
                            if (executionResult.PendingPackagesToRemove.Count > 0)
                            {
                                RemovePendingPackagesFromPendingListAndInstallRows(executionResult.PendingPackagesToRemove);
                            }
                            PendingInstalledOnlyResourceOverwriteResult publicResult = executionResult.ToPublicResult();
                            NLogWrapper.FileLogger?.Info("advanced_pending_resource_overwrite summary requested=" + publicResult.Requested + " processed=" + publicResult.Processed + " succeededInstall=" + publicResult.SucceededInstall + " succeededCleanupOnly=" + publicResult.SucceededCleanupOnly + " skippedNotPending=" + publicResult.SkippedNotPending + " skippedMissingInstlDst=" + publicResult.SkippedMissingInstlDst + " skippedMultiDst=" + publicResult.SkippedMultiDestination + " skippedNoComponentTarget=" + publicResult.SkippedNoComponentTarget + " failed=" + publicResult.Failed + " canceled=" + publicResult.Canceled);
                            return publicResult;
                        }
                    }
                }
            }
        }
        finally
        {
            InvokeDeferredProcessedCallbacks(onEachProcessed, deferredProcessedCount);
        }
    }

    internal List<ChartFile> GetPendingBmsFormatChartFilesSnapshot()
    {
        using (rwlockBMSFilesInitializedMin.GetReaderGuard())
        {
            using (rwlockPendingInstallCharts.GetReaderGuard())
            {
                return packageInstallService.GetPendingBmsFormatChartFilesSnapshot(ChartPackagesPending);
            }
        }
    }

    internal void RenamePendingZeroNoteBmsFormatChartsToInvalidExtensions(IEnumerable<ChartFile> targetCharts, CancellationToken token = default, Action onEachProcessed = null)
    {
        int deferredProcessedCount = 0;
        try
        {
            using (rwlockBMSFilesInitializedMin.GetReaderGuard())
            {
                using (rwlockPendingInstallCharts.GetWriterGuard())
                {
                    using (rwlockSongDBInstall.GetWriterGuard())
                    {
                        IEnumerable<ChartFile> enumerable = targetCharts ?? packageInstallService.GetPendingBmsFormatChartFilesSnapshot(ChartPackagesPending);
                        PendingZeroNoteRenameResult result = packageInstallService.RenamePendingZeroNoteBmsFormatChartsToInvalidExtensions(
                            enumerable,
                            (file, requestedPath) => ProcessInvalidExtensionRename(file, requestedPath, removeFromLibraryOnSuccess: false),
                            token,
                            () => deferredProcessedCount++,
                            info => NLogWrapper.FileLogger?.Info(info));
                        foreach (PendingZeroNoteRenameFailure failure in result.Failures)
                        {
                            if (failure?.Outcome?.FailureException == null || failure.File == null)
                            {
                                continue;
                            }
                            if (failure.Outcome.FailedDuringDelete)
                            {
                                ShowOperationDialog(string.Format(Resources.Error_BmsFileDeleteFailed, failure.File.path, GetDisplayedExceptionMessage(failure.Outcome.FailureException)), Resources.MessageBoxTitle_Error, MessageBoxButton.OK, MessageBoxImage.Hand, MessageBoxResult.OK);
                            }
                            else
                            {
                                ShowOperationDialog(string.Format(Resources.Error_BmsFileMoveFailed, failure.File.path, failure.Outcome.FinalPath, GetDisplayedExceptionMessage(failure.Outcome.FailureException)), Resources.MessageBoxTitle_Error, MessageBoxButton.OK, MessageBoxImage.Hand, MessageBoxResult.OK);
                            }
                        }
                        RemovePendingChartsFromPendingPackagesAndInstallRows(result.ChartPathsToRemove);
                        NLogWrapper.FileLogger?.Info("advanced_pending_zero_note_rename summary total=" + result.Total + " processed=" + result.Processed + " zeroNote=" + result.ZeroNote + " renamed=" + result.Renamed + " duplicateDeleted=" + result.DuplicateDeleted + " skipped=" + result.Skipped + " failed=" + result.Failed + " canceled=" + result.Canceled);
                    }
                }
            }
        }
        finally
        {
            InvokeDeferredProcessedCallbacks(onEachProcessed, deferredProcessedCount);
        }
    }

    /// <summary>
    /// Pending パッケージのソースファイル群を削除（またはゴミ箱へ移動）します。
    /// </summary>
    public void DeletePendingPackageSources(IEnumerable<ChartPackage> packages, bool sendToRecycleBin = true, CancellationToken token = default, Action onEachProcessed = null)
    {
        if (packages == null)
        {
            throw new ArgumentNullException("packages");
        }
        int deferredProcessedCount = 0;
        try
        {
            using (rwlockBMSFilesInitializedAll.GetReaderGuard())
            {
                using (rwlockPendingInstallCharts.GetWriterGuard())
                {
                    using (rwlockSongDBInstall.GetWriterGuard())
                    {
                        List<ChartPackage> list = packageInstallService.DeduplicatePackagesByPathOrReference(packages);
                        bool flag2 = !sendToRecycleBin;
                        NLogWrapper.FileLogger?.Info("advanced_pending_cleanup start requested=" + list.Count + " permanent=" + flag2);
                        PendingPackageSourceDeletionResult result = packageInstallService.DeletePendingPackageSources(
                            list,
                            ChartPackagesPending,
                            sendToRecycleBin,
                            fileMutationService,
                            targetOnlyFileMutationOptions,
                            recursiveDirectoryTreeFileMutationOptions,
                            token,
                            () => deferredProcessedCount++,
                            info => NLogWrapper.FileLogger?.Info(info));
                        foreach (PendingPackageSourceDeletionFailure failure in result.Failures)
                        {
                            if (failure?.Package == null)
                            {
                                continue;
                            }
                            NLogWrapper.FileLogger?.Warn(failure.Exception, "advanced_pending_cleanup failed path=" + failure.Package.path + " kind=" + (failure.IsDirectory ? "directory" : "file") + " error=" + GetDisplayedExceptionMessage(failure.Exception));
                            if (failure.IsDirectory)
                            {
                                ShowOperationDialog(string.Format(Resources.Error_FolderOrTrashDeleteFailed, failure.Package.path, GetDisplayedExceptionMessage(failure.Exception)), Resources.MessageBoxTitle_Error, MessageBoxButton.OK, MessageBoxImage.Hand, MessageBoxResult.OK);
                            }
                            else
                            {
                                ShowOperationDialog(string.Format(Resources.Error_BmsFileDeleteFailed, failure.Package.path, GetDisplayedExceptionMessage(failure.Exception)), Resources.MessageBoxTitle_Error, MessageBoxButton.OK, MessageBoxImage.Hand, MessageBoxResult.OK);
                            }
                        }
                        RemovePendingPackagesFromPendingListAndInstallRows(result.PackagesToRemove);
                        NLogWrapper.FileLogger?.Info("advanced_pending_cleanup summary requested=" + result.Requested + " processed=" + result.Processed + " removed=" + result.Removed + " failed=" + result.Failed + " skipped=" + result.Skipped + " canceled=" + result.Canceled);
                    }
                }
            }
        }
        finally
        {
            InvokeDeferredProcessedCallbacks(onEachProcessed, deferredProcessedCount);
        }
    }

    private static void InvokeDeferredProcessedCallbacks(Action onEachProcessed, int count)
    {
        if (onEachProcessed == null || count <= 0)
        {
            return;
        }
        for (int i = 0; i < count; i++)
        {
            onEachProcessed();
        }
    }

    private void RemovePendingPackagesFromPendingListAndInstallRows(IEnumerable<ChartPackage> packages)
    {
        stateApplier.ApplyPendingPackageMutationDelta(BuildPendingPackageMutationDelta(packagesToRemove: packages));
    }
}
