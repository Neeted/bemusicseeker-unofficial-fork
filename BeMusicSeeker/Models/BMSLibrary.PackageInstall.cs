using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Runtime.ExceptionServices;
using System.Threading;
using System.Windows;
using BeMusicSeeker.Diagnostics;
using BeMusicSeeker.Models.BmsLibraryInternal;
using BeMusicSeeker.Models.LR2;
using BeMusicSeeker.Models.Utils;
using BeMusicSeeker.Properties;
using Livet;
using MessageBoxButton = BeMusicSeeker.Models.UiDialogButton;
using MessageBoxImage = BeMusicSeeker.Models.UiDialogIcon;
using MessageBoxResult = BeMusicSeeker.Models.UiDialogDefaultResult;
using Ribbit.Logging;
using Ribbit.Util;

namespace BeMusicSeeker.Models;

public partial class BMSLibrary
{
    /// <summary>
    /// Executes selected-folder auto rename with a feature-local progress
    /// writer. The writer is adapted once per batch so progress delivery does
    /// not accumulate a deferred callback for every plan.
    /// </summary>
    /// <param name="chartFiles">Charts whose containing folders may be renamed.</param>
    /// <param name="renameRootFolder">Whether the library root folder is eligible.</param>
    /// <param name="progressWriter">Best-effort immutable progress sink.</param>
    /// <param name="reportAtTerminal">Suppresses the session-backed unexpected-move dialog when a workflow terminal owns reporting.</param>
    /// <returns>The operation-scoped mutation session result.</returns>
    internal AutoRenameBatchResult AutoRenameChartFoldersWithProgress(
        IEnumerable<ChartFile> chartFiles,
        bool renameRootFolder,
        IFolderAutoRenameProgressWriter progressWriter,
        bool reportAtTerminal = false)
    {
        if (chartFiles == null)
        {
            throw new ArgumentNullException(nameof(chartFiles));
        }
        ArgumentNullException.ThrowIfNull(progressWriter);
        if (TryBlockCatalogFileMutation(nameof(AutoRenameChartFolders)))
        {
            return new AutoRenameBatchResult(false, 0, LibraryMutationSessionReceipt.Empty);
        }

        AutoRenameBatchResult result = null;
        ExceptionDispatchInfo primaryFailure = null;
        List<Action> postLeaseNotifications = [];
        try
        {
            libraryMutationOwner.RunWithFolderMoveWriteLocks(
                mutationCapability =>
                {
                    List<FolderAutoRenamePlan> plans = null;
                    libraryMutationOwner.RunWithFolderMoveSnapshotLocks(
                        () => plans = libraryMutationOwner.BuildAutoRenamePlans(
                            chartFiles.Where(chart => chart != null),
                            getBMSDirectories(),
                            renameRootFolder));
                    Lr2NormalFolderCurrentBmsCapture currentBmsCapture =
                        HasActionableAutoRenamePlan(plans)
                            ? TryCaptureAutoRenameLr2NormalFolderCurrentBmsFacts(plans)
                            : null;
                    result = libraryMutationOwner.ApplyAutoRenamePlansWithSessionReceipt(
                        plans,
                        mutationCapability,
                        (total, processed, currentPath) => progressWriter.TryWrite(
                            new FolderAutoRenameProgressUpdate(total, processed, currentPath)),
                        postLeaseNotifications);
                    SyncAutoRenameLr2NormalFoldersUnderExistingLease(
                        result,
                        currentBmsCapture,
                        mutationCapability);
                });
        }
        catch (Exception exception)
        {
            primaryFailure = ExceptionDispatchInfo.Capture(exception);
            if (result?.HasDurableCommit == true)
            {
                result = result.WithDurableFinalizationFailure(primaryFailure);
            }
        }
        FlushAutoRenamePostCommitEffects(result, primaryFailure, postLeaseNotifications, reportAtTerminal);
        return result ?? new AutoRenameBatchResult(false, 0, LibraryMutationSessionReceipt.Empty);
    }

    /// <summary>
    /// Executes all-folder auto rename with a feature-local progress writer.
    /// Progress is forwarded directly while the mutation capability remains
    /// owned by the workflow, avoiding a per-plan deferred callback list.
    /// </summary>
    /// <param name="parentDir">Optional source-folder scope.</param>
    /// <param name="progressWriter">Best-effort immutable progress sink.</param>
    /// <param name="reportAtTerminal">Suppresses the session-backed unexpected-move dialog when a workflow terminal owns reporting.</param>
    /// <returns>The operation-scoped mutation session result.</returns>
    internal AutoRenameBatchResult AutoRenameAllChartFoldersWithProgress(
        string parentDir,
        IFolderAutoRenameProgressWriter progressWriter,
        bool reportAtTerminal = false)
    {
        ArgumentNullException.ThrowIfNull(progressWriter);
        if (TryBlockCatalogFileMutation(nameof(AutoRenameAllChartFolders)))
        {
            return new AutoRenameBatchResult(false, 0, LibraryMutationSessionReceipt.Empty);
        }

        AutoRenameBatchResult result = null;
        ExceptionDispatchInfo primaryFailure = null;
        List<Action> postLeaseNotifications = [];
        try
        {
            libraryMutationOwner.RunWithFolderMoveWriteLocks(
                mutationCapability =>
                {
                    List<FolderAutoRenamePlan> plans = null;
                    libraryMutationOwner.RunWithFolderMoveSnapshotLocks(
                        () => plans = libraryMutationOwner.BuildAutoRenamePlansForSourceFolders(parentDir));
                    if (HasActionableAutoRenamePlan(plans))
                    {
                        Lr2NormalFolderCurrentBmsCapture currentBmsCapture =
                            TryCaptureAutoRenameLr2NormalFolderCurrentBmsFacts(plans);
                        result = libraryMutationOwner.ApplyAutoRenamePlansWithSessionReceipt(
                            plans,
                            mutationCapability,
                            (total, processed, currentPath) => progressWriter.TryWrite(
                                new FolderAutoRenameProgressUpdate(total, processed, currentPath)),
                            postLeaseNotifications);
                        SyncAutoRenameLr2NormalFoldersUnderExistingLease(
                            result,
                            currentBmsCapture,
                            mutationCapability);
                    }
                });
        }
        catch (Exception exception)
        {
            primaryFailure = ExceptionDispatchInfo.Capture(exception);
            if (result?.HasDurableCommit == true)
            {
                result = result.WithDurableFinalizationFailure(primaryFailure);
            }
        }
        FlushAutoRenamePostCommitEffects(result, primaryFailure, postLeaseNotifications, reportAtTerminal);
        return result ?? new AutoRenameBatchResult(false, 0, LibraryMutationSessionReceipt.Empty);
    }

    /// <summary>
    /// 指定されたパス群（ファイルまたはディレクトリ）から chart package を自動検出・インストールします。
    /// アーカイブの展開、song.db への登録、Pendingパッケージ生成を一括で行います。
    /// </summary>
    /// <param name="installPaths">インストール元のファイル/ディレクトリパスのコレクション。</param>
    /// <returns>インストール処理された chart package のリスト。</returns>
    public List<ChartPackage> InstallChartPackagesAuto(
        IEnumerable<string> installPaths,
        CancellationToken token = default)
    {
        return [.. InstallChartPackagesAutoWithProgress(
            installPaths,
            token,
            NullPackageInstallProgressWriter.Instance).RegisteredPackages];
    }

    /// <summary>
    /// Executes package installation with the feature-local bounded progress
    /// writer used by the workflow owner.  The writer is a producer boundary;
    /// durable mutation and terminal results never depend on its delivery.
    /// Terminal-owned callers suppress receipt-backed individual failure dialogs.
    /// </summary>
    internal PackageInstallCommandResult InstallChartPackagesAutoWithProgress(
        IEnumerable<string> installPaths,
        CancellationToken token,
        IPackageInstallProgressWriter progressWriter,
        bool reportAtTerminal = false)
    {
        ArgumentNullException.ThrowIfNull(progressWriter);
        BmsLibraryOptionsSnapshot options = CurrentOptionsSnapshot;
        List<ChartPackage> pendingPackagesToEstimate = [];
        List<ChartPackage> deferredPendingEstimatePackages = [];
        Dictionary<ChartPackage, int> deferredPendingEstimateHealthByPackage = [];
        List<ChartPackage> registeredPackages = [];
        List<string> regroupEligibleSourceDirectories = [];
        PendingEstimateSourceBatchSnapshot pendingBatchSourceSnapshot = null;
        List<Action> postLeaseEffects = [];
        List<Action> diagnosticEffects = [];
        FileDbMutationBatchReceipt autoInstallMutationReceipt = null;
        if (installPaths == null || installPaths.Any(path => !LongPathFileSystem.EntryExists(path)))
        {
            ShowOperationDialog(Resources.Warn_InstallAbortedFilesNotFound, Resources.MessageBoxTitle_Warning, MessageBoxButton.OK, MessageBoxImage.Hand, MessageBoxResult.OK);
            return CreatePackageInstallCommandResult(registeredPackages, null);
        }
        if (token.IsCancellationRequested)
        {
            return CreatePackageInstallCommandResult(registeredPackages, null);
        }
        LibraryFileMutationLease lr2SongDbSyncMutation = TryBeginLr2SongDbSyncBlockedMutation(
            nameof(InstallChartPackagesAuto),
            showMessage: true);
        if (lr2SongDbSyncMutation == null)
        {
            throw new InvalidOperationException(Resources.Warn_Lr2SongDbSyncRunning);
        }
        Exception primaryFailure = null;
        try
        {
            try
            {
                using LibraryFileMutationCapability mutationCapability =
                    lr2SongDbSyncMutation.CreateMutationCapability();
                // Ingress protection uses configured roots, even when the chart
                // index is empty or a root is excluded from resource scanning.
                IReadOnlyList<string> registeredBmsRoots =
                    lr2SearchRootSnapshotOwner.CaptureForUpdate(options).RequestedRoots;
                List<string> expandedInstallPaths = packageInstallService.ExpandInstallSourcesWithProgress(
                    installPaths,
                    fileMutationService,
                    targetOnlyFileMutationOptions,
                    info => NLogWrapper.FileLogger?.Info(info),
                    scopedOperationDialogService,
                    progressWriter,
                    token,
                    action => diagnosticEffects.Add(action));
                installPaths = expandedInstallPaths;
                if (token.IsCancellationRequested)
                {
                    CleanupManagedInstallSources(expandedInstallPaths, "auto_install_canceled_after_expand");
                    return CreatePackageInstallCommandResult(registeredPackages, null);
                }
                AutoInstallWorkflowResult workflow;
                List<ChartPackage> pendingPackageSnapshot;
                InstalledChartLookupIndexSnapshot installedChartLookup;
                using (rwlockBMSFilesInitializedAll.GetReaderGuard())
                {
                    using (rwlockPendingInstallCharts.GetReaderGuard())
                    {
                        using (rwlockBMSFiles.GetReaderGuard())
                        {
                            pendingPackageSnapshot = [.. ChartPackagesPending.Where(package => package != null)];
                            installedChartLookup = CreateInstalledChartLookupSnapshotUnsafe();
                        }
                    }
                }

                bool IsInstalledFromSnapshot(ChartFile chart)
                {
                    string lookupKey = ChartLookupKey.GetPrimaryHash(chart);
                    return !string.IsNullOrWhiteSpace(lookupKey)
                        && installedChartLookup.ContainsPrimaryHash(lookupKey);
                }

                workflow = packageInstallService.PrepareAutoInstallWorkflow(
                    installPaths,
                    pendingPackageSnapshot,
                    registeredBmsRoots,
                    IsInstalledFromSnapshot,
                    dupRateThreshInOnePkg,
                    installedChartLookup,
                    token);
                List<ChartPackage> discoveredPackages = [.. workflow.DiscoveredPackages];
                LogInstallPerformance("auto_install_prepare discovered=" + discoveredPackages.Count + " autoInstall=" + workflow.AutoInstallCandidates.Count + " pendingAdd=" + workflow.PendingPackagesToAdd.Count + " pendingRemove=" + workflow.PendingPackagesToRemove.Count + " discoveryMs=" + workflow.DiscoveryMs + " installedCheckMs=" + workflow.InstalledCheckMs + " warningClassifyMs=" + workflow.WarningClassificationMs + " classificationMs=" + workflow.ClassificationMs + " totalMs=" + workflow.TotalMs);
                if (discoveredPackages.Count == 0 || token.IsCancellationRequested)
                {
                    CleanupManagedInstallSources(expandedInstallPaths, discoveredPackages.Count == 0 ? "auto_install_no_packages" : "auto_install_canceled_after_prepare");
                    return CreatePackageInstallCommandResult(registeredPackages, null);
                }

                // Package state finalization raises entry PropertyChanged.  Keep
                // those notifications deferred for the whole command so the
                // durable finalizer cannot call a public subscriber while the
                // outer mutation lease is still held.
                IReadOnlyList<Func<Action>> packageEntryNotificationDeferrals =
                    DeferPackageEntryNotifications(discoveredPackages.SelectMany(package => package.ChartEntries));
                postLeaseEffects.Add(() =>
                {
                    if (autoInstallMutationReceipt?.HasDurableFinalizationFailure == true)
                    {
                        DiscardPackageEntryNotificationPublication(packageEntryNotificationDeferrals);
                        return;
                    }
                    QueuePackageEntryNotificationPublication(packageEntryNotificationDeferrals);
                });

                bool canAutoInstallToLibrary = SearchTargets != null
                    && SearchTargets.Count() > 0
                    && LongPathFileSystem.DirectoryExists(SearchTargets[0]);
                bool catalogPathConverged = catalogFileMutationAdmissionOwner.IsConverged;
                if (!catalogPathConverged
                    && !options.KeepInstallablePackagesPending
                    && canAutoInstallToLibrary
                    && workflow.AutoInstallCandidates.Count > 0)
                {
                    CatalogPathConvergenceBlockReason blockReason = catalogFileMutationAdmissionOwner.BlockReason;
                    diagnosticEffects.Add(() => ShowCatalogFileMutationRequiresFileDiffWarning(blockReason));
                }
                AutoInstallApplyResult applyResult = packageInstallService.ApplyAutoInstallWorkflowWithFileMutationReceipts(
                    workflow,
                    options.KeepInstallablePackagesPending,
                    canAutoInstallToLibrary && catalogPathConverged,
                    packagesToInstall =>
                    {
                        List<ChartPackage> packageList = [.. (packagesToInstall ?? []).Where(package => package != null)];
                        PackageInstallExecutionResult installExecutionResult = installChartPackages(
                            packageList,
                            installResult => libraryMutationOwner.ApplyInstalledChartStorageTargetsForFileMutation(
                                installResult,
                                "install_package",
                                mutationCapability,
                                postLeaseEffects.Add),
                            mutationReceiptObserver: null,
                            mutationBatchReceiptObserver: receipt => autoInstallMutationReceipt = receipt,
                            diagnosticEffectObserver: diagnosticEffects.Add,
                            postLeaseEffectObserver: postLeaseEffects.Add,
                            reportAtTerminal: reportAtTerminal,
                            existingHashes: installedChartLookup,
                            independentOwnershipLookup: installedChartLookup,
                            optionsSnapshot: options,
                            sourceCleanupPolicy: options.DeletePendingPackageSourceAfterInstall
                                ? PackageSourceCleanupPolicy.DeleteVerifiedResidualContents
                                : PackageSourceCleanupPolicy.PreserveUnconsumedContents);
                        List<ChartPackage> failedPackages = installExecutionResult.FailedPackages;
                        if (autoInstallMutationReceipt?.ManualRecoveryRequired == true
                            || autoInstallMutationReceipt?.HasDurableFinalizationFailure == true)
                        {
                            // The package executor stopped at a terminal mutation
                            // failure.  Keep the durable prefix successful and add
                            // only the unattempted suffix to the failure set so the
                            // classifier retains that suffix as pending.
                            int attemptedPackageCount = Math.Min(
                                autoInstallMutationReceipt.Receipts.Count,
                                packageList.Count);
                            var failedPackageSet = new HashSet<ChartPackage>(failedPackages);
                            for (int packageIndex = attemptedPackageCount;
                                packageIndex < packageList.Count;
                                packageIndex++)
                            {
                                failedPackageSet.Add(packageList[packageIndex]);
                            }
                            failedPackages = [.. packageList.Where(failedPackageSet.Contains)];
                        }
                        return new AutoInstallCandidateApplyResult(
                            failedPackages,
                            autoInstallMutationReceipt);
                    },
                    token);
                LogInstallPerformance("auto_install_apply pendingAdd=" + applyResult.PendingPackagesToAdd.Count + " pendingRemove=" + applyResult.PendingPackagesToRemove.Count + " autoInstalled=" + applyResult.AutoInstalledPackages.Count + " autoFailed=" + applyResult.AutoInstallFailures.Count + " installMs=" + applyResult.InstallMs + " applyMs=" + applyResult.ApplyMs + " totalMs=" + applyResult.TotalMs);
                registeredPackages = [.. applyResult.AutoInstalledPackages];
                if (applyResult.PendingPackagesToRemove.Count > 0
                    || applyResult.PendingPackagesToAdd.Count > 0
                    || applyResult.InstallRowsToUpsert.Count > 0)
                {
                    List<ChartPackage> currentPendingPackages;
                    using (rwlockPendingInstallCharts.GetReaderGuard())
                    {
                        currentPendingPackages = [.. ChartPackagesPending.Where(package => package != null)];
                    }
                    PendingPackageMutationDelta pendingMutationDelta = packageInstallService.BuildPendingPackageMutationDelta(
                        currentPendingPackages,
                        packagesToRemove: applyResult.PendingPackagesToRemove);
                    Action pendingMutationEffect = ApplyPendingPackageMutationDeltaUnderExistingReservation(
                        pendingMutationDelta,
                        applyResult.PendingPackagesToAdd,
                        applyResult.InstallRowsToUpsert);
                    if (pendingMutationEffect != null)
                    {
                        postLeaseEffects.Add(pendingMutationEffect);
                    }
                }
                if (applyResult.ManualRecoveryRequired
                    || applyResult.HasDurableFinalizationFailure)
                {
                    // The durable prefix has already been registered above;
                    // retain only the failed/manual and unattempted suffix as
                    // pending and stop before any later estimate work starts.
                    return CreatePackageInstallCommandResult(
                        registeredPackages,
                        applyResult.MutationReceipt);
                }

                BackgroundPendingEstimatePreparationResult estimatePreparation = PrepareBackgroundPendingEstimatePackagesUnsafe(applyResult.EstimateTargets, PendingInstallEstimateBatchSource.AutoInstall);
                pendingPackagesToEstimate = estimatePreparation.EstimablePackages;
                deferredPendingEstimatePackages = estimatePreparation.DeferredPackages;
                deferredPendingEstimateHealthByPackage = estimatePreparation.DeferredSourceHealthByPackage;
                pendingBatchSourceSnapshot = estimatePreparation.BatchSourceSnapshot;
                regroupEligibleSourceDirectories = [.. workflow.RegroupEligibleSourceDirectories];
                foreach (ChartPackage deferredPackage in deferredPendingEstimatePackages)
                {
                    deferredPendingEstimateHealthByPackage.TryGetValue(deferredPackage, out int sourceHealth);
                    LogPendingEstimateSkippedPackage("auto_install", deferredPackage, sourceHealth);
                }
                if (!token.IsCancellationRequested && pendingPackagesToEstimate.Count > 0)
                {
                    string displayName = PendingInstallEstimateBatchRequest.GetDisplayName(pendingPackagesToEstimate.FirstOrDefault()?.path);
                    PendingInstallEstimateBatchRequest estimateRequest = new(
                            PendingInstallEstimateBatchSource.AutoInstall,
                            pendingPackagesToEstimate,
                            displayName,
                            regroupEligibleSourceDirectories,
                            deferredPendingEstimatePackages.Count,
                            pendingBatchSourceSnapshot);
                    diagnosticEffects.Add(() => QueuePendingInstallEstimateBatch(estimateRequest));
                }
                return CreatePackageInstallCommandResult(
                    registeredPackages,
                    applyResult.MutationReceipt);
            }
            catch (Exception exception)
            {
                primaryFailure = exception;
            }
        }
        finally
        {
            // Capability invalidation and the owner release happen before any
            // command-owned post-lease catalog/package publication or ordinary feedback.
            lr2SongDbSyncMutation.Dispose();
            Exception terminalFailure = null;
            try
            {
                FlushPostLeaseEffects(postLeaseEffects, diagnosticEffects);
            }
            catch (Exception exception)
            {
                terminalFailure = exception;
            }
            if (primaryFailure != null && terminalFailure != null)
            {
                NLogWrapper.FileLogger?.Warn(
                    terminalFailure,
                    "auto_install_post_lease_terminalization_failed_after_primary_failure");
            }
            else if (primaryFailure == null && terminalFailure != null)
            {
                ExceptionDispatchInfo.Capture(terminalFailure).Throw();
            }
        }
        if (primaryFailure != null)
        {
            ExceptionDispatchInfo.Capture(primaryFailure).Throw();
        }
        return CreatePackageInstallCommandResult(registeredPackages, null);
    }

    private static PackageInstallCommandResult CreatePackageInstallCommandResult(
        IEnumerable<ChartPackage> registeredPackages,
        FileDbMutationBatchReceipt mutationReceipt)
    {
        return new PackageInstallCommandResult(registeredPackages, mutationReceipt);
    }

    private static void FlushPostLeaseEffects(
        IEnumerable<Action> authoritativeEffects,
        IEnumerable<Action> diagnosticEffects)
    {
        foreach (Action effect in authoritativeEffects ?? [])
        {
            try
            {
                effect?.Invoke();
            }
            catch (Exception exception)
            {
                NLogWrapper.FileLogger?.Warn(exception, "package_install_post_lease_notification_failed");
            }
        }
        foreach (Action diagnostic in diagnosticEffects ?? [])
        {
            try
            {
                diagnostic?.Invoke();
            }
            catch (Exception exception)
            {
                NLogWrapper.FileLogger?.Warn(exception, "package_install_diagnostic_failed");
            }
        }
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

    /// <summary>
    /// pending source cleanup is a zero-file promotion whose install row is
    /// deleted by the same durable receipt owner as a normal install.  Keeping
    /// the source in the plan until that receipt exists prevents source-first
    /// deletion when the catalog transaction fails.
    /// </summary>
    private FileDbMutationReceipt ExecutePendingPackageSourceCleanupWithReceipt(
        ChartPackage package,
        Func<PackageInstallExecutionResult, FileDbMutationCommitResult> applyDurableStorageRows,
        EstimatedInstallDeferredFeedback deferredFeedback = null,
        Action<Action> postLeaseNotificationObserver = null)
    {
        ArgumentNullException.ThrowIfNull(applyDurableStorageRows);
        string sourcePath = package?.path;
        bool sourceDirectoryExists = !string.IsNullOrWhiteSpace(sourcePath)
            && LongPathFileSystem.DirectoryExists(sourcePath);
        bool sourceFileExists = !sourceDirectoryExists
            && !string.IsNullOrWhiteSpace(sourcePath)
            && LongPathFileSystem.FileExists(sourcePath);
        var plan = new FileDbMutationPlan(
            Guid.NewGuid(),
            [],
            sourceFileExists ? [sourcePath] : [],
            sourceDirectoryExists
                ? [new FileDbMutationCleanupPathPlan(sourcePath, recursive: true)]
                : [],
            recursiveSourceCleanup: false);
        var installResult = new PackageInstallExecutionResult
        {
            InstallPathToDelete = sourcePath
        };
        FileDbMutationReceipt receipt = new FileDbMutationExecutor(
            plan,
            fileMutationService,
            targetOnlyFileMutationOptions,
            recursiveDirectoryTreeFileMutationOptions)
            .Execute(() => applyDurableStorageRows(installResult));
        if (receipt.TerminalState == FileDbMutationTerminalState.ManualRecoveryRequired
            || receipt.TerminalState == FileDbMutationTerminalState.DurableFinalizationFailed)
        {
            deferredFeedback?.LogInstallWarning(
                receipt.Failure,
                "estimated_install_cleanup_only_manual_recovery path="
                + sourcePath
                + " recoveryPaths="
                + string.Join("|", receipt.RecoveryPaths ?? []));
        }
        else if (!receipt.DurableCommit)
        {
            deferredFeedback?.LogInstallWarning(
                receipt.Failure,
                "estimated_install_cleanup_only_failed path=" + sourcePath);
        }
        return receipt;
    }

    private PackageInstallExecutionResult installChartPackages(IEnumerable<ChartPackage> chartPackagesInstall, Func<PackageInstallExecutionResult, FileDbMutationCommitResult> applyDurableStorageRows, PackageSourceCleanupPolicy sourceCleanupPolicy, string installationDirectory = null, List<ChartFile> deferredMaintenanceCharts = null, List<ChartPackage> deferredInstalledPackages = null, Dictionary<ChartPackage, HashSet<string>> excludedComponentPathsByPackage = null, IPrimaryHashLookup existingHashes = null, IInstalledChartLookupIndex independentOwnershipLookup = null, bool skipInstalledPackageWhenNoBms = false, EstimatedInstallBatchApplyContext estimatedInstallBatchApplyContext = null, EstimatedInstallDeferredFeedback estimatedInstallDeferredFeedback = null, Action<FileDbMutationReceipt> mutationReceiptObserver = null, Action<FileDbMutationBatchReceipt> mutationBatchReceiptObserver = null, Action<Action> diagnosticEffectObserver = null, Action<Action> postLeaseEffectObserver = null, bool reportAtTerminal = false, BmsLibraryOptionsSnapshot optionsSnapshot = null)
    {
        ArgumentNullException.ThrowIfNull(applyDurableStorageRows);
        List<ChartPackage> installPackageList = [.. (chartPackagesInstall ?? []).Where(package => package != null)];
        optionsSnapshot ??= CurrentOptionsSnapshot;
        List<ChartFile> addedChartsForChartInfo = [];
        IBmsLibraryDialogService installDialogService = estimatedInstallDeferredFeedback?.DialogService;
        Action<string> installPerformanceLogger = estimatedInstallDeferredFeedback == null
            ? LogInstallPerformance
            : estimatedInstallDeferredFeedback.LogInstallPerformance;
        if (diagnosticEffectObserver == null && estimatedInstallDeferredFeedback != null)
        {
            diagnosticEffectObserver = effect =>
                estimatedInstallDeferredFeedback.DeferDiagnosticEffect(effect);
        }

        void UpdateInstalledChartMaintenance(PackageInstallExecutionResult installResult)
        {
            IReadOnlyList<ChartFile> addedCharts = LibraryMutationOwner.CreateAddedStorageTargets(installResult).Charts;
            if (deferredMaintenanceCharts != null)
            {
                deferredMaintenanceCharts.AddRange(addedCharts);
                return;
            }
            if (addedCharts.Count > 0)
            {
                ApplyCatalogMaintenanceUnderExistingReservation(
                    addedCharts,
                    forceUpdate: true,
                    resourceHealthMutationReason: "install_package",
                    postLeaseNotificationObserver: postLeaseEffectObserver);
            }
        }

        void ApplyInstalledChartScores(PackageInstallExecutionResult installResult)
        {
            ChartStorageTargetSet addedTargets = LibraryMutationOwner.CreateAddedStorageTargets(installResult);
            SetBMSScore(addedTargets.BmsFiles);
        }

        void AddReverseLookupDirectoriesForInstall(IEnumerable<string> addedDirectories)
        {
            List<string> directoryList = [.. addedDirectories ?? []];
            if (directoryList.Count == 0)
            {
                return;
            }
            if (ChartDirectoryScanBuilder.TryBuildFromRoots(directoryList, out ChartScanResult addedDirectoryScan, out string scanFailureReason))
            {
                DirectoryResourceLookupCache.ReverseLookupMutationResult reverseLookupMutation =
                    libraryResourceIndexOwner.AddScanDirectories(addedDirectoryScan).MutationResult;
                diagnosticEffectObserver?.Invoke(
                    () => LogReverseLookupMutationAndQueueWarmupIfNeeded("install_package", reverseLookupMutation));
            }
            else
            {
                LogInstallPerformanceWarn("install_package resource_cache_update skipped reason=incomplete_scan detail=" + (scanFailureReason ?? "unknown") + " dirs=" + directoryList.Count);
            }
        }

        void ApplyInstalledChartState(PackageInstallExecutionResult installResult)
        {
            ChartStorageTargetSet addedTargets = LibraryMutationOwner.CreateAddedStorageTargets(installResult);
            IReadOnlyList<ChartFile> addedCharts = addedTargets.Charts;
            addedChartsForChartInfo.AddRange(addedCharts);
            AddReverseLookupDirectoriesForInstall(addedTargets.GetDistinctChartDirectories());
            estimatedInstallBatchApplyContext?.AddInstalledTargets(addedTargets, installationDirectory);
        }

        PackageInstallExecutionResult result = packageInstallService.InstallPackagesWithFileMutationReceipts(
            installPackageList,
            installationDirectory,
            (package, destinationDirectory, cleanupPolicy, hashSnapshot, ownershipLookup, excludedComponentPaths, applyDurableCommit) =>
            {
                FileDbMutationReceipt receipt = packageInstallService.MovePackageFilesWithReceipt(
                    package,
                    destinationDirectory,
                    optionsSnapshot,
                    CreateChartFolderPathFromCharts,
                    GetDisplayedExceptionMessage,
                    fileMutationService,
                    installDialogService ?? scopedOperationDialogService,
                    targetOnlyFileMutationOptions,
                    recursiveDirectoryTreeFileMutationOptions,
                    installPerformanceLogger,
                    applyDurableCommit,
                    showMessageBoxOnInstallFail: !reportAtTerminal,
                    sourceCleanupPolicy: cleanupPolicy,
                    existingHashes: hashSnapshot,
                    independentOwnershipLookup: independentOwnershipLookup,
                    excludedComponentPaths: excludedComponentPaths,
                    enqueueDiagnosticEffect: diagnosticEffectObserver);
                return receipt;
            },
            applyDurableStorageRows,
            UpdateInstalledChartMaintenance,
            ApplyInstalledChartScores,
            ApplyInstalledChartState,
            sourceCleanupPolicy: sourceCleanupPolicy,
            excludedComponentPathsByPackage: excludedComponentPathsByPackage,
            existingHashes: existingHashes,
            skipInstalledPackageWhenNoBms: skipInstalledPackageWhenNoBms,
            independentOwnershipLookup: independentOwnershipLookup);
        foreach (FileDbMutationReceipt mutationReceipt in result.MutationReceipt?.Receipts ?? [])
        {
            mutationReceiptObserver?.Invoke(mutationReceipt);
        }
        mutationBatchReceiptObserver?.Invoke(result.MutationReceipt);
        if (result.InstalledPackagesToRegister.Count > 0)
        {
            if (deferredInstalledPackages != null)
            {
                deferredInstalledPackages.AddRange(result.InstalledPackagesToRegister);
            }
            else
            {
                Action installedPackagesEffect = AddInstalledPackagesUnderExistingReservation(
                    result.InstalledPackagesToRegister);
                if (installedPackagesEffect != null)
                {
                    postLeaseEffectObserver?.Invoke(installedPackagesEffect);
                }
            }
        }
        installPerformanceLogger("install_chart_packages dst=" + (installationDirectory ?? "(auto)") + " packages=" + installPackageList.Count + " addedFiles=" + result.AddedEntries.Count + " failedPackages=" + result.FailedPackages.Count + " sourceCleanupPolicy=" + sourceCleanupPolicy + " moveMs=" + result.MoveMs + " songDbMs=" + result.SongDbMs + " maintenanceMs=" + result.MaintenanceMs + " scoreMs=" + result.ScoreMs + " applyMs=" + result.ApplyMs + " totalMs=" + result.TotalMs);
        if (deferredMaintenanceCharts == null)
        {
            BuildAndPersistInlineChartInfoForInstalledCharts(
                "install_package_inline",
                addedChartsForChartInfo);
        }
        return result;
    }

    private DirectoryResourceLookupCache.ReverseLookupMutationResult ApplyEstimatedInstallReverseLookupPreparationUnderGuard(
        PendingEstimatedInstallCatalogPreparation preparation)
    {
        if (preparation?.DirectoryScan == null
            || preparation.AffectedDirectories.Count == 0)
        {
            return DirectoryResourceLookupCache.ReverseLookupMutationResult.Empty;
        }
        return libraryResourceIndexOwner.AddDirectories(
            preparation.AffectedDirectories,
            preparation.DirectoryScan).MutationResult;
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
                                if (!HasUsableDirectoryLookupCache(
                                    libraryResourceIndexOwner.CaptureSnapshot().DirectoryLookupCache))
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
        PerformanceInteraction? firstVisibleInteraction = null;
        var executionPolicy = InstallEstimationExecutionPolicy.ForManualBatch();
        var stopwatch = Stopwatch.StartNew();
        LogInstallPerformance("pending_estimate_batch start source=" + source + " packages=" + request.PackageCount + " totalPackages=" + request.TotalPackageCount + " deferredPackages=" + request.DeferredPackageCount + " packageDegree=" + executionPolicy.WorkItemDegree + " display=" + (request.DisplayName ?? string.Empty));
        RunPendingEstimateExclusive(delegate
        {
            SetInstallEstimationProgress(InstallEstimationProgressSource.ManualReestimate, request.PackageCount, 0, request.DisplayName ?? string.Empty);
            PendingInstallEstimateBatchCapture evaluationCapture =
                CapturePendingInstallEstimateBatch(request);
            ProcessPendingInstallEstimateEvaluationPipeline(
                request,
                source,
                CancellationToken.None,
                evaluationCapture.Context,
                evaluationCapture.Requests,
                executionPolicy,
                ref completed,
                ref lowConfidenceCount,
                ref firstVisibleInteraction);
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
        _ = ForceInstallPendingPackagesWithReceipt(packages, approveNormalInstallOverride: null, approvedNormalInstallOverridePackages: null);
    }

    internal void ForceInstallPendingPackages(IEnumerable<ChartPackage> packages, bool? approveNormalInstallOverride)
    {
        ISet<ChartPackage> approvedNormalInstallOverridePackages = null;
        if (approveNormalInstallOverride == true)
        {
            approvedNormalInstallOverridePackages = new HashSet<ChartPackage>((packages ?? []).Where(package => package != null));
        }
        _ = ForceInstallPendingPackagesWithReceipt(
            packages,
            approveNormalInstallOverride,
            approvedNormalInstallOverridePackages);
    }

    /// <summary>Returns force-install facts, optionally assigning receipt-backed dialogs to the caller terminal.</summary>
    internal FileDbMutationBatchReceipt ForceInstallPendingPackagesWithReceipt(
        IEnumerable<ChartPackage> packages,
        bool? approveNormalInstallOverride,
        ISet<ChartPackage> approvedNormalInstallOverridePackages,
        bool reportAtTerminal = false)
    {
        if (packages == null)
        {
            throw new ArgumentNullException(nameof(packages));
        }
        if (TryBlockCatalogFileMutation(nameof(ForceInstallPendingPackages)))
        {
            return new FileDbMutationBatchReceipt([]);
        }
        BmsLibraryOptionsSnapshot options = CurrentOptionsSnapshot;
        bool hasInitializedBmsFiles;
        List<ChartPackage> pendingPackageSnapshot;
        InstalledChartLookupIndexSnapshot installedChartHashSnapshot;
        using (rwlockBMSFilesInitializedAll.GetReaderGuard())
        using (rwlockPendingInstallCharts.GetReaderGuard())
        using (rwlockBMSFiles.GetReaderGuard())
        {
            hasInitializedBmsFiles = BMSFiles != null;
            pendingPackageSnapshot = [.. ChartPackagesPending.Where(package => package != null)];
            installedChartHashSnapshot = CreateInstalledChartLookupSnapshotUnsafe();
        }
        if (!hasInitializedBmsFiles)
        {
            return new FileDbMutationBatchReceipt([]);
        }

        // Normal-install confirmations are a user decision, not mutation
        // work.  Resolve them from the immutable pre-admission package
        // snapshot so no dialog can run while the exclusive lease is held.
        var approvedNormalInstallPackages = new HashSet<ChartPackage>();
        foreach (ChartPackage requestedPackage in packageInstallService.DeduplicatePackagesByPathOrReference(packages))
        {
            ChartPackage pendingPackage = pendingPackageSnapshot.FirstOrDefault(package =>
                ReferenceEquals(package, requestedPackage)
                || (!string.IsNullOrWhiteSpace(package?.path)
                    && !string.IsNullOrWhiteSpace(requestedPackage?.path)
                    && string.Equals(package.path, requestedPackage.path, StringComparison.OrdinalIgnoreCase)));
            if (pendingPackage == null)
            {
                continue;
            }
            bool hasInstallDestination = pendingPackage.ChartEntries.Any(entry =>
                !string.IsNullOrWhiteSpace(entry?.Chart?.InstallDestination));
            bool approved = !hasInstallDestination
                || approvedNormalInstallOverridePackages?.Contains(pendingPackage) == true
                || approvedNormalInstallOverridePackages?.Any(package =>
                    package != null
                    && !string.IsNullOrWhiteSpace(package.path)
                    && string.Equals(package.path, pendingPackage.path, StringComparison.OrdinalIgnoreCase)) == true
                || approveNormalInstallOverride == true
                || (approveNormalInstallOverride != false
                    && ShowOperationDialog(
                        Resources.Confirm_NormalInstallOverride,
                        Resources.Confirm_NormalInstallTitle,
                        MessageBoxButton.YesNo,
                        MessageBoxImage.Question,
                        MessageBoxResult.Yes) == MessageBoxResult.Yes);
            if (approved)
            {
                approvedNormalInstallPackages.Add(pendingPackage);
            }
        }
        LibraryFileMutationLease mutationReservation = TryBeginCatalogFileMutationPreservingBusyFailure(
            nameof(ForceInstallPendingPackages),
            showMessage: true);
        if (mutationReservation == null)
        {
            return new FileDbMutationBatchReceipt([]);
        }
        List<Action> postLeaseEffects = [];
        List<Action> diagnosticEffects = [];
        ForceInstallBatchResult result = null;
        IReadOnlyList<Func<Action>> packageEntryNotificationDeferrals =
            DeferPackageEntryNotifications(approvedNormalInstallPackages.SelectMany(package => package.ChartEntries));
        postLeaseEffects.Add(() =>
        {
            if (result?.HasDurableFinalizationFailure == true)
            {
                DiscardPackageEntryNotificationPublication(packageEntryNotificationDeferrals);
                return;
            }
            QueuePackageEntryNotificationPublication(packageEntryNotificationDeferrals);
        });
        Exception primaryFailure = null;
        try
        {
            try
            {
                using LibraryFileMutationCapability mutationCapability =
                    mutationReservation.CreateMutationCapability();
                result = packageInstallService.ForceInstallPackagesWithFileMutationReceipts(
                    packages,
                    pendingPackageSnapshot,
                    pendingPackage =>
                    {
                        return approvedNormalInstallPackages.Contains(pendingPackage);
                    },
                    (packagesToInstall, deferredInstalledPackages) =>
                    {
                        bool manualRecoveryRequired = false;
                        FileDbMutationBatchReceipt mutationBatchReceipt = null;
                        PackageInstallExecutionResult installExecutionResult = installChartPackages(
                            packagesToInstall,
                            installResult => libraryMutationOwner.ApplyInstalledChartStorageTargetsForFileMutation(
                                installResult,
                                "install_package",
                                mutationCapability,
                                postLeaseEffects.Add),
                            sourceCleanupPolicy: options.DeletePendingPackageSourceAfterInstall
                                ? PackageSourceCleanupPolicy.DeleteVerifiedResidualContents
                                : PackageSourceCleanupPolicy.PreserveUnconsumedContents,
                            deferredInstalledPackages: deferredInstalledPackages,
                            mutationReceiptObserver: receipt =>
                            {
                                manualRecoveryRequired |=
                                    receipt?.TerminalState == FileDbMutationTerminalState.ManualRecoveryRequired
                                    || receipt?.TerminalState == FileDbMutationTerminalState.DurableFinalizationFailed;
                            },
                            mutationBatchReceiptObserver: batchReceipt => mutationBatchReceipt = batchReceipt,
                            diagnosticEffectObserver: diagnosticEffects.Add,
                            postLeaseEffectObserver: postLeaseEffects.Add,
                            reportAtTerminal: reportAtTerminal,
                            existingHashes: installedChartHashSnapshot,
                            independentOwnershipLookup: installedChartHashSnapshot,
                            optionsSnapshot: options);
                        List<ChartPackage> failedPackages = installExecutionResult.FailedPackages;
                        return new ForceInstallPackageApplyResult(
                            failedPackages,
                            manualRecoveryRequired,
                            mutationBatchReceipt);
                    },
                    info => NLogWrapper.FileLogger?.Info(info));
                if (result.Requested == 0)
                {
                    return result.MutationReceipt ?? new FileDbMutationBatchReceipt([]);
                }

                diagnosticEffects.Add(
                    () => NLogWrapper.FileLogger?.Info("force_install_batch start requested=" + result.Requested));
                List<ChartPackage> currentPendingPackages = [];
                if (result.PendingPackagesToRemove.Count > 0)
                {
                    using (rwlockPendingInstallCharts.GetReaderGuard())
                    {
                        currentPendingPackages = [.. ChartPackagesPending.Where(package => package != null)];
                    }
                }
                List<ChartPackage> installedPackages;
                using (rwlockBMSFilesInitializedAll.GetReaderGuard())
                using (rwlockBMSFiles.GetReaderGuard())
                {
                    installedPackages = [.. ChartPackagesInstalled.Where(package => package != null)];
                }
                var installedSet = new HashSet<ChartPackage>(installedPackages);
                int installedAdded = 0;
                foreach (ChartPackage deferredInstalledPackage in result.DeferredInstalledPackages)
                {
                    if (deferredInstalledPackage != null && installedSet.Add(deferredInstalledPackage))
                    {
                        installedPackages.Add(deferredInstalledPackage);
                        installedAdded++;
                    }
                }
                IDisposable collectionPublicationScope = packageLifecycleOwner.BeginCollectionMutationScope(queuePublication: true);
                try
                {
                    if (result.PendingPackagesToRemove.Count > 0)
                    {
                        PendingPackageMutationDelta pendingMutationDelta = packageInstallService.BuildPendingPackageMutationDelta(
                            currentPendingPackages,
                            packagesToRemove: result.PendingPackagesToRemove);
                        packageLifecycleOwner.ApplyPendingPackageMutationDelta(pendingMutationDelta);
                    }
                    if (installedAdded > 0)
                    {
                        packageLifecycleOwner.ReplaceInstalledPackages(installedPackages);
                    }
                }
                catch
                {
                    postLeaseEffects.Add(collectionPublicationScope.Dispose);
                    throw;
                }
                postLeaseEffects.Add(collectionPublicationScope.Dispose);
                diagnosticEffects.Add(
                    () => NLogWrapper.FileLogger?.Info("force_install_batch summary requested=" + result.Requested + " processed=" + result.Processed + " succeeded=" + result.Succeeded + " failed=" + result.Failed + " skipped=" + result.Skipped + " pendingRemoved=" + result.PendingPackagesToRemove.Count + " installedAdded=" + installedAdded));
                return result.MutationReceipt ?? new FileDbMutationBatchReceipt([]);
            }
            catch (Exception exception)
            {
                primaryFailure = exception;
            }
        }
        finally
        {
            mutationReservation.Dispose();
            Exception terminalFailure = null;
            try
            {
                FlushPostLeaseEffects(postLeaseEffects, diagnosticEffects);
            }
            catch (Exception exception)
            {
                terminalFailure = exception;
            }
            if (primaryFailure != null && terminalFailure != null)
            {
                NLogWrapper.FileLogger?.Warn(
                    terminalFailure,
                    "force_install_post_lease_terminalization_failed_after_primary_failure");
            }
            else if (primaryFailure == null && terminalFailure != null)
            {
                ExceptionDispatchInfo.Capture(terminalFailure).Throw();
            }
        }
        if (primaryFailure != null)
        {
            ExceptionDispatchInfo.Capture(primaryFailure).Throw();
        }
        return new FileDbMutationBatchReceipt([]);
    }

    internal void ForceInstallPendingPackages(
        IEnumerable<ChartPackage> packages,
        bool? approveNormalInstallOverride,
        ISet<ChartPackage> approvedNormalInstallOverridePackages)
    {
        _ = ForceInstallPendingPackagesWithReceipt(
            packages,
            approveNormalInstallOverride,
            approvedNormalInstallOverridePackages);
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

    private static IReadOnlyDictionary<string, IReadOnlyList<string>> CapturePackageComponentFiles(
        IEnumerable<ChartPackage> packages)
    {
        var snapshots = new Dictionary<string, IReadOnlyList<string>>(
            StringComparer.OrdinalIgnoreCase);
        foreach (ChartPackage package in (packages ?? []).Where(package => package != null).Distinct())
        {
            try
            {
                string path = package.path;
                if (string.IsNullOrWhiteSpace(path))
                {
                    continue;
                }
                if (LongPathFileSystem.FileExists(path))
                {
                    snapshots[path] = [path];
                }
                else if (LongPathFileSystem.DirectoryExists(path))
                {
                    snapshots[path] =
                    [
                        .. LongPathFileSystem.EnumerateFiles(
                            path,
                            "*",
                            SearchOption.AllDirectories)
                    ];
                }
                else
                {
                    snapshots[path] = [];
                }
            }
            catch
            {
                if (!string.IsNullOrWhiteSpace(package.path))
                {
                    snapshots[package.path] = [];
                }
            }
        }
        return snapshots;
    }

    private static int CountComponentMoveTargetsFromSnapshot(
        ChartPackage package,
        string destinationDirectory,
        ISet<string> excludedComponentPaths,
        IReadOnlyDictionary<string, IReadOnlyList<string>> componentFilesByPackage)
    {
        if (package == null
            || string.IsNullOrWhiteSpace(destinationDirectory)
            || componentFilesByPackage == null
            || string.IsNullOrWhiteSpace(package.path)
            || !componentFilesByPackage.TryGetValue(
                package.path,
                out IReadOnlyList<string> componentFiles))
        {
            return 0;
        }
        var excludedPathSet = new HashSet<string>(
            excludedComponentPaths ?? Enumerable.Empty<string>(),
            StringComparer.OrdinalIgnoreCase);
        foreach (PackageChartEntry entry in package.ChartEntries ?? [])
        {
            if (!string.IsNullOrWhiteSpace(entry?.Chart?.Path))
            {
                excludedPathSet.Add(entry.Chart.Path);
            }
        }
        return componentFiles.Count(path =>
            !string.IsNullOrWhiteSpace(path)
            && !excludedPathSet.Contains(path));
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
        List<ChartFile> destinationCharts = installDestinationStateOwner.OverlayRuntimeStates(CreateOwnedChartFilesForExactPathsUnsafe(
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
        // resource-only導入でも、destinationへ実際に移ったresourceを基準に
        // installed表示のhealth/warning projectionを確定する。
        BmsLibraryPackageInstallService.ApplyPendingResourceHealthProjectionToEntries(entries);
        ChartPackage displayPackage = ChartPackage.FromChartEntries(entries);
        displayPackage.path = destinationDirectory;
        displayPackage.delete_parent = false;
        return displayPackage;
    }

    private PendingEstimatedInstallExecutionReceipt ExecutePendingEstimatedInstall(
        IEnumerable<ChartPackage> packages,
        PendingEstimatedInstallExecutionContext executionContext,
        Func<PackageInstallExecutionResult, FileDbMutationCommitResult> applyDurableStorageRows,
        bool reportAtTerminal = false)
    {
        if (packages == null)
        {
            throw new ArgumentNullException(nameof(packages));
        }
        if (executionContext == null)
        {
            throw new ArgumentNullException(nameof(executionContext));
        }
        ArgumentNullException.ThrowIfNull(applyDurableStorageRows);

        var totalStopwatch = Stopwatch.StartNew();
        EstimatedInstallDeferredFeedback deferredFeedback = executionContext.DeferredFeedback;
        BmsLibraryOptionsSnapshot options;
        PendingInstallBatchPlan installPlan;
        // 推定実行ゲートは route 固有の plan 構築だけを保護する。
        // 論理ファイル予約を保持したまま filesystem executor と durable mutation が
        // このゲートを待たずに model へ再入できる必要がある。
        using (packageLifecycleOwner.EnterPendingEstimateExecutionScope())
        using (PendingEstimatedInstallMutationLease.Acquire(
            rwlockBMSFilesInitializedAll.GetReaderGuard,
            rwlockPendingInstallCharts.GetReaderGuard,
            rwlockBMSFiles.GetReaderGuard))
        {
            options = CurrentOptionsSnapshot;
            installPlan = packageInstallService.BuildEstimatedInstallBatchPlan(
                packages,
                [.. ChartPackagesPending.Where(package => package != null)],
                CreateInstalledChartLookupSnapshotUnsafe());
        }

        bool deletePendingPackageSourceAfterInstall = options.DeletePendingPackageSourceAfterInstall;
        if (installPlan.SelectedPendingPackages.Count == 0)
        {
            totalStopwatch.Stop();
            deferredFeedback.LogInstallPerformance(
                "install_pending_packages_to_estimated_destinations skipped reason=no_pending_target filterMs="
                + installPlan.FilterMs
                + " totalMs="
                + totalStopwatch.ElapsedMilliseconds);
            return CreateSkippedPendingEstimatedInstallReceipt(
                totalStopwatch,
                installPlan,
                deletePendingPackageSourceAfterInstall);
        }
        // component 列挙は意図的に model guard の外で行う。
        // filter 済み候補の snapshot だけを使い、hash を予約しないためである。
        IReadOnlyDictionary<string, IReadOnlyList<string>> componentFilesByPackage =
            CapturePackageComponentFiles(installPlan.SelectedPendingPackages);

        deferredFeedback.LogInstallPerformance(
            "install_pending_packages_to_estimated_destinations start selected="
            + installPlan.SelectedPendingCount
            + " deferredManualHold="
            + installPlan.DeferredManualHoldCount
            + " deleteSourceContents="
            + deletePendingPackageSourceAfterInstall
            + " filterMs="
            + installPlan.FilterMs
            + " planBuildMs="
            + installPlan.PlanBuildMs);

        var batchApplyContext = new EstimatedInstallBatchApplyContext();
        List<FileDbMutationReceipt> mutationReceipts = [];
        List<ChartFile> deferredMaintenanceCharts = [];
        List<ChartPackage> deferredInstalledPackages = [];
        PendingInstallBatchResult batchResult = packageInstallService.ExecuteEstimatedInstallBatchPlan(
            installPlan,
            deletePendingPackageSourceAfterInstall,
            item =>
            {
                Dictionary<ChartPackage, HashSet<string>> excludedComponentPathsByPackage = null;
                if (item?.ExcludedComponentPaths != null && item.InstallWorkPackage != null)
                {
                    excludedComponentPathsByPackage = new()
                    {
                        [item.InstallWorkPackage] = item.ExcludedComponentPaths
                    };
                }
                return installChartPackages(
                    [item.InstallWorkPackage],
                    applyDurableStorageRows,
                    sourceCleanupPolicy: deletePendingPackageSourceAfterInstall
                        ? PackageSourceCleanupPolicy.DeleteVerifiedResidualContents
                        : PackageSourceCleanupPolicy.PreserveUnconsumedContents,
                    installationDirectory: item.DestinationDirectory,
                    deferredMaintenanceCharts: deferredMaintenanceCharts,
                    deferredInstalledPackages: deferredInstalledPackages,
                    excludedComponentPathsByPackage: excludedComponentPathsByPackage,
                    existingHashes: installPlan.MoveGuardLookup,
                    independentOwnershipLookup: installPlan.IndependentOwnershipLookup,
                    skipInstalledPackageWhenNoBms: true,
                    estimatedInstallBatchApplyContext: batchApplyContext,
                    estimatedInstallDeferredFeedback: deferredFeedback,
                    mutationReceiptObserver: receipt => mutationReceipts.Add(receipt),
                    reportAtTerminal: reportAtTerminal,
                    optionsSnapshot: options);
            },
            CreateInstalledDisplayPackageForResourceOnlyMerge,
            cleanupPendingPackageSource: null,
            logInfo: deferredFeedback.LogInstallPerformance,
            cleanupPendingPackageSourceWithReceipt: package => ExecutePendingPackageSourceCleanupWithReceipt(
                package,
                applyDurableStorageRows,
                deferredFeedback),
            mutationReceiptObserver: receipt => mutationReceipts.Add(receipt),
            manualRecoveryObserved: () => mutationReceipts.Any(receipt =>
                receipt?.TerminalState == FileDbMutationTerminalState.ManualRecoveryRequired
                    || receipt?.TerminalState == FileDbMutationTerminalState.DurableFinalizationFailed),
            countComponentMoveTargets: (package, destinationDirectory, excludedComponentPaths) =>
                CountComponentMoveTargetsFromSnapshot(
                    package,
                    destinationDirectory,
                    excludedComponentPaths,
                    componentFilesByPackage));
        batchResult.DeferredMaintenanceCharts.AddRange(deferredMaintenanceCharts);
        batchResult.DeferredInstalledPackages.AddRange(deferredInstalledPackages);
        if (mutationReceipts.Count > 0)
        {
            batchResult.MutationReceipt = new FileDbMutationBatchReceipt(
                mutationReceipts,
                batchResult.MutationReceipt?.FinalizationFailure);
        }

        long libraryStateApplyMs;
        long pendingApplyMs;
        long installedApplyMs;
        PendingEstimatedInstallCollectionApplyResult pendingApplyResult;
        PendingEstimatedInstallCollectionApplyResult installedApplyResult;
        var libraryStateApplyStopwatch = Stopwatch.StartNew();
        libraryStateApplyStopwatch.Stop();
        libraryStateApplyMs = libraryStateApplyStopwatch.ElapsedMilliseconds;
        List<ChartPackage> currentPendingPackages;
        using (PendingEstimatedInstallMutationLease.Acquire(
            rwlockBMSFilesInitializedAll.GetReaderGuard,
            rwlockPendingInstallCharts.GetReaderGuard))
        {
            currentPendingPackages = [.. packageLifecycleOwner.PendingPackages.Where(package => package != null)];
        }
        PendingPackageMutationDelta pendingDelta = packageInstallService.BuildPendingPackageMutationDelta(
            currentPendingPackages,
            packagesToRemove: batchResult.PendingPackagesToRemove);
        // Every selected package's install row is deleted by its durable
        // catalog receipt.  The collection delta removes in-memory entries but
        // must not issue a second DB delete after that receipt.
        pendingDelta.InstallPathsToDelete.Clear();
        pendingDelta.HasChanges = pendingDelta.HasChanges
            || batchResult.PendingPackagesToRemove?.Count > 0;
        IDisposable collectionPublicationScope = packageLifecycleOwner.BeginCollectionMutationScope(queuePublication: true);
        try
        {
            (
                pendingApplyResult,
                installedApplyResult,
                pendingApplyMs,
                installedApplyMs) = packageLifecycleOwner.ApplyEstimatedInstallCollections(
                    pendingDelta,
                    batchResult.PendingPackagesToRemove?.Count ?? 0,
                    batchResult.DeferredInstalledPackages);
        }
        catch
        {
            collectionPublicationScope.Dispose();
            throw;
        }

        return new PendingEstimatedInstallExecutionReceipt
        {
            TotalStopwatch = totalStopwatch,
            InstallPlan = installPlan,
            BatchResult = batchResult,
            BatchApplyContext = batchApplyContext,
            PendingApplyResult = pendingApplyResult,
            InstalledApplyResult = installedApplyResult,
            CollectionPublicationScope = collectionPublicationScope,
            LibraryStateApplyMs = libraryStateApplyMs,
            PendingApplyMs = pendingApplyMs,
            InstalledApplyMs = installedApplyMs,
            DeletePendingPackageSourceAfterInstall = deletePendingPackageSourceAfterInstall
        };
    }

    private void CompletePendingEstimatedInstallUnderGuard(
        PendingEstimatedInstallExecutionReceipt receipt,
        EstimatedInstallDeferredFeedback deferredFeedback)
    {
        if (receipt == null)
        {
            throw new ArgumentNullException(nameof(receipt));
        }
        if (receipt.IsSkipped)
        {
            return;
        }
        var maintenanceStopwatch = Stopwatch.StartNew();
        Action maintenancePublication;
        receipt.MaintenanceReceipt = ApplyEstimatedInstallMaintenanceForDeferredDispatch(
            receipt.BatchResult.DeferredMaintenanceCharts,
            deferredFeedback,
            out maintenancePublication);
        receipt.MaintenancePublication = maintenancePublication;
        receipt.MaintenanceReceipt.ThrowIfFailed();
        List<ChartFile> targets =
            BuildEstimatedInstallMaintenanceTargets(receipt.BatchResult.DeferredMaintenanceCharts);
        List<ChartFile> inlineTargets = BuildEstimatedInstallMaintenanceTargets(
            targets.Concat(CreateAddedBmsonChartProjections(receipt.BatchApplyContext.AddedCharts)));
        Action inlineChartInfoPublication;
        receipt.InlineChartInfoReceipt = BuildEstimatedInstallInlineChartInfoForDeferredDispatch(
            "install_package_estimated_inline",
            inlineTargets,
            deferredFeedback,
            out inlineChartInfoPublication);
        receipt.InlineChartInfoPublication = inlineChartInfoPublication;
        receipt.InlineChartInfoReceipt.ThrowIfFailed();
        maintenanceStopwatch.Stop();

        if (receipt.DeletePendingPackageSourceAfterInstall && receipt.BatchResult.CleanupOnlySucceeded > 0)
        {
            deferredFeedback.ShowEstimatedCleanupOnlyCompletedWarning(
                receipt.BatchResult.CleanupOnlySucceeded);
        }

        receipt.TotalStopwatch.Stop();
        deferredFeedback.LogInstallPerformance(
            "install_pending_packages_to_estimated_destinations end libraryStateApplyMs="
            + receipt.LibraryStateApplyMs
            + " pendingApplyMs="
            + receipt.PendingApplyMs
            + " pendingBeforeApply="
            + receipt.PendingApplyResult.Before
            + " pendingRemovedTotal="
            + receipt.PendingApplyResult.Changed
            + " pendingAfterApply="
            + receipt.PendingApplyResult.After
            + " installedApplyMs="
            + receipt.InstalledApplyMs
            + " installedBeforeApply="
            + receipt.InstalledApplyResult.Before
            + " installedAddedTotal="
            + receipt.InstalledApplyResult.Changed
            + " installedAfterApply="
            + receipt.InstalledApplyResult.After
            + " maintenanceTargets="
            + receipt.MaintenanceReceipt.AffectedCount
            + " maintenanceMs="
            + maintenanceStopwatch.ElapsedMilliseconds
            + " cleanupOnlyCandidates="
            + receipt.InstallPlan.CleanupOnlyCandidates.Count
            + " cleanupOnlySucceeded="
            + receipt.BatchResult.CleanupOnlySucceeded
            + " cleanupOnlyFailed="
            + receipt.BatchResult.CleanupOnlyFailed
            + " cleanupOnlyMissingSource="
            + receipt.BatchResult.CleanupOnlyMissingSource
            + " deferredManualHold="
            + receipt.InstallPlan.DeferredManualHoldCount
            + " totalMs="
            + receipt.TotalStopwatch.ElapsedMilliseconds);
    }

    private static PendingEstimatedInstallExecutionReceipt CreateSkippedPendingEstimatedInstallReceipt(
        Stopwatch totalStopwatch,
        PendingInstallBatchPlan installPlan,
        bool deletePendingPackageSourceAfterInstall)
    {
        return new PendingEstimatedInstallExecutionReceipt
        {
            TotalStopwatch = totalStopwatch,
            InstallPlan = installPlan,
            DeletePendingPackageSourceAfterInstall = deletePendingPackageSourceAfterInstall
        };
    }

    private static void PublishPendingEstimatedInstallPostGuardEffects(
        PendingEstimatedInstallExecutionReceipt receipt,
        PendingEstimatedInstallExecutionContext executionContext)
    {
        void PublishBestEffort(Action publish, string diagnostic)
        {
            try
            {
                publish?.Invoke();
            }
            catch (Exception exception)
            {
                NLogWrapper.FileLogger?.Warn(exception, diagnostic);
            }
        }

        PublishBestEffort(
            () => receipt?.MaintenancePublication?.Invoke(),
            "estimated_install_maintenance_publication_failed");
        PublishBestEffort(
            () => receipt?.InlineChartInfoPublication?.Invoke(),
            "estimated_install_inline_publication_failed");
        // Package-entry property notifications are ordinary diagnostics.  A
        // subscriber must not be able to turn an already durable install into
        // a failed/manual-recovery result.
        PublishBestEffort(
            () => executionContext?.PublishDeferredPackageEntryNotifications(),
            "estimated_install_package_entry_notification_failed");
        PublishBestEffort(
            () => receipt?.CollectionPublicationScope?.Dispose(),
            "estimated_install_collection_publication_failed");
    }

    private void PublishPendingEstimatedInstallFeedback(
        EstimatedInstallDeferredFeedback deferredFeedback,
        bool suppressCleanupOnlyWarning = false)
    {
        while (true)
        {
            IReadOnlyList<EstimatedInstallFeedbackNotification> notifications =
                deferredFeedback?.DrainNotifications() ?? [];
            if (notifications.Count == 0)
            {
                break;
            }
            foreach (EstimatedInstallFeedbackNotification notification in notifications)
            {
                try
                {
                    switch (notification.Kind)
                    {
                        case EstimatedInstallFeedbackKind.Dialog:
                            ShowOperationDialog(
                                notification.Message,
                                notification.Caption,
                                notification.Button,
                                notification.Icon,
                                notification.DefaultResult);
                            break;
                        case EstimatedInstallFeedbackKind.CleanupOnlyWarning:
                            // A mixed abnormal batch is summarized once by its canonical
                            // terminal; normal-only and legacy cleanup advice is retained.
                            if (suppressCleanupOnlyWarning)
                            {
                                break;
                            }
                            ShowOperationDialog(
                                string.Format(
                                    Resources.Warn_estimated_install_cleanup_only_completed,
                                    notification.CleanupOnlySucceeded),
                                Resources.MessageBoxTitle_Warning,
                                MessageBoxButton.OK,
                                MessageBoxImage.Exclamation,
                                MessageBoxResult.OK);
                            break;
                        case EstimatedInstallFeedbackKind.PerformanceLog:
                            LogInstallPerformance(notification.Message);
                            break;
                        case EstimatedInstallFeedbackKind.WarningLog:
                            NLogWrapper.FileLogger?.Warn(notification.Exception, notification.Message);
                            break;
                        case EstimatedInstallFeedbackKind.DeferredAction:
                            notification.Action?.Invoke();
                            break;
                        default:
                            throw new InvalidOperationException(
                                "Unsupported estimated-install feedback kind: " + notification.Kind);
                    }
                }
                catch (Exception exception)
                {
                    NLogWrapper.FileLogger?.Warn(
                        exception,
                        "estimated_install_feedback_notification_failed kind=" + notification.Kind);
                }
            }
        }
    }

    /// <summary>
    /// 指定された pending package 群を推定されたインストール先ディレクトリへインストールします。
    /// SmartOverwrite ロジックによるコンポーネント移動計画を構築して実行します。
    /// </summary>
    public void InstallPendingPackagesToEstimatedDestinations(IEnumerable<ChartPackage> packages)
    {
        InstallPendingPackagesToEstimatedDestinationsWithReceipt(packages);
    }

    /// <summary>Returns estimated-install facts, optionally assigning receipt-backed dialogs to the caller terminal.</summary>
    internal PendingInstallBatchResult InstallPendingPackagesToEstimatedDestinationsWithReceipt(IEnumerable<ChartPackage> packages, bool reportAtTerminal = false)
    {
        if (packages == null)
        {
            throw new ArgumentNullException(nameof(packages));
        }
        if (TryBlockCatalogFileMutation(nameof(InstallPendingPackagesToEstimatedDestinations)))
        {
            return new PendingInstallBatchResult();
        }

        PendingInstallBatchResult result = null;
        List<Action> postLeaseNotifications = [];
        Exception primaryFailure = null;
        try
        {
            using (LibraryFileMutationLease mutationReservation =
                TryBeginCatalogFileMutationPreservingBusyFailure(
                    nameof(InstallPendingPackagesToEstimatedDestinations),
                    showMessage: true))
            {
                if (mutationReservation == null)
                {
                    return new PendingInstallBatchResult();
                }
                using LibraryFileMutationCapability mutationCapability =
                    mutationReservation.CreateMutationCapability();
                Func<PackageInstallExecutionResult, FileDbMutationCommitResult> applyDurableStorageRows =
                    installResult => libraryMutationOwner.ApplyInstalledChartStorageTargetsForFileMutation(
                        installResult,
                        "install_package",
                        mutationCapability,
                        postLeaseNotifications.Add);
                result = ExecutePendingPackagesToEstimatedDestinations(
                    packages,
                    applyDurableStorageRows,
                    postLeaseNotifications, reportAtTerminal);
            }
        }
        catch (Exception exception)
        {
            primaryFailure = exception;
        }

        InvokePostLeaseNotificationsBestEffort(postLeaseNotifications);
        if (primaryFailure != null)
        {
            ExceptionDispatchInfo.Capture(primaryFailure).Throw();
        }
        return result;
    }

    /// <summary>
    /// Executes pending estimated installation under the caller's active
    /// mutation lease.  The returned result carries terminal publication for
    /// the outer command; this method never acquires a second reservation.
    /// </summary>
    private PendingInstallBatchResult ExecutePendingPackagesToEstimatedDestinations(
        IEnumerable<ChartPackage> packages,
        Func<PackageInstallExecutionResult, FileDbMutationCommitResult> applyDurableStorageRows,
        ICollection<Action> postLeaseNotifications,
        bool reportAtTerminal = false)
    {
        if (packages == null)
        {
            throw new ArgumentNullException(nameof(packages));
        }
        ArgumentNullException.ThrowIfNull(applyDurableStorageRows);
        List<ChartPackage> requestedPackages =
            [.. packages.Where(package => package != null)];
        var executionContext = new PendingEstimatedInstallExecutionContext();
        executionContext.DeferPackageEntryNotifications(requestedPackages);
        PendingEstimatedInstallExecutionReceipt receipt = null;
        Exception operationFailure = null;

        try
        {
            receipt = ExecutePendingEstimatedInstall(
                requestedPackages,
                executionContext,
                applyDurableStorageRows, reportAtTerminal);
            CompletePendingEstimatedInstallUnderGuard(
                receipt,
                executionContext.DeferredFeedback);
        }
        catch (Exception exception)
        {
            operationFailure = exception;
        }
        PendingInstallBatchResult result = receipt?.BatchResult ?? new PendingInstallBatchResult();
        postLeaseNotifications?.Add(() =>
        {
            PublishPendingEstimatedInstallPostGuardEffects(receipt, executionContext);
            try
            {
                PublishPendingEstimatedInstallFeedback(executionContext?.DeferredFeedback,
                    suppressCleanupOnlyWarning: reportAtTerminal
                        && (result.CompletedWithCleanupFailure
                            || result.HasDurableFinalizationFailure
                            || result.ManualRecoveryRequired
                            || result.MutationReceipt?.Receipts.Any(item => !item.DurableCommit) == true));
            }
            catch (Exception exception)
            {
                NLogWrapper.FileLogger?.Warn(
                    exception,
                    "estimated_install_deferred_feedback_publication_failed");
            }
        });
        if (operationFailure != null)
        {
            ExceptionDispatchInfo.Capture(operationFailure).Throw();
        }
        return result;
    }

    /// <summary>
    /// 指定された installed package record 群をリストから削除します。
    /// </summary>
    public void RemoveInstalledPackageRecords(IEnumerable<ChartPackage> packages)
    {
        using (packageLifecycleOwner.BeginCollectionMutationScope())
        {
            using (rwlockBMSFilesInitializedAll.GetReaderGuard())
            {
                using (rwlockPendingInstallCharts.GetWriterGuard())
                {
                    using (rwlockSongDBInstall.GetWriterGuard())
                    {
                        packageLifecycleOwner.RemoveInstalledPackages(packages);
                    }
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
        packageLifecycleOwner.ApplyPendingPackageMutationDelta(delta);
    }

    private Action ApplyPendingPackageMutationDeltaUnderExistingReservation(
        PendingPackageMutationDelta delta,
        IEnumerable<ChartPackage> packagesToAdd,
        IEnumerable<ChartPackage> installRowsToUpsert)
    {
        IDisposable collectionPublicationScope = packageLifecycleOwner.BeginCollectionMutationScope(queuePublication: true);
        try
        {
            packageLifecycleOwner.ApplyPendingPackageMutationDelta(
                delta,
                packagesToAdd,
                installRowsToUpsert);
        }
        catch
        {
            collectionPublicationScope.Dispose();
            throw;
        }
        return collectionPublicationScope.Dispose;
    }

    private Action AddInstalledPackagesUnderExistingReservation(
        IEnumerable<ChartPackage> packages)
    {
        if (packages == null || !packages.Any())
        {
            return null;
        }
        IDisposable collectionPublicationScope = packageLifecycleOwner.BeginCollectionMutationScope(queuePublication: true);
        try
        {
            packageLifecycleOwner.AddInstalledPackages(packages);
        }
        catch
        {
            collectionPublicationScope.Dispose();
            throw;
        }
        return collectionPublicationScope.Dispose;
    }

    /// <summary>
    /// 指定された pending package 群を pending リストから削除します。
    /// </summary>
    public void RemovePendingPackages(IEnumerable<ChartPackage> packages)
    {
        Action pendingEffect = null;
        List<ChartPackage> managedPackagesToCleanup = [];
        using (LibraryFileMutationLease mutationReservation = TryBeginLr2SongDbSyncBlockedMutation(
            nameof(RemovePendingPackages),
            showMessage: true))
        {
            if (mutationReservation == null)
            {
                return;
            }
            {
                using (rwlockBMSFilesInitializedAll.GetReaderGuard())
                {
                    using (rwlockPendingInstallCharts.GetWriterGuard())
                    {
                        using (rwlockSongDBInstall.GetWriterGuard())
                        {
                            managedPackagesToCleanup = ResolveManagedPendingPackagesForCleanup(packages);
                            pendingEffect = RemovePendingPackagesFromPendingListAndInstallRows(
                                packages);
                        }
                    }
                }
            }
        }
        InvokePostLeaseNotificationsBestEffort([pendingEffect]);
        CleanupManagedPendingPackageSources(managedPackagesToCleanup, "pending_remove_selected");
    }

    /// <summary>
    /// 全ての installed package record をリストからクリアします。
    /// </summary>
    public void RemoveInstalledPackageRecordsAll()
    {
        using (packageLifecycleOwner.BeginCollectionMutationScope())
        {
            using (rwlockBMSFilesInitializedAll.GetReaderGuard())
            {
                using (rwlockPendingInstallCharts.GetWriterGuard())
                {
                    using (rwlockSongDBInstall.GetWriterGuard())
                    {
                        packageLifecycleOwner.ClearInstalledPackages();
                    }
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
        using (packageLifecycleOwner.BeginCollectionMutationScope())
        {
            using (rwlockBMSFilesInitializedAll.GetReaderGuard())
            {
                using (rwlockPendingInstallCharts.GetWriterGuard())
                {
                    using (rwlockSongDBInstall.GetWriterGuard())
                    {
                        managedPackagesToCleanup = ResolveManagedPendingPackagesForCleanup(ChartPackagesPending);
                        packageLifecycleOwner.ApplyPendingPackageMutationDelta(BuildPendingPackageMutationDelta(clearAll: true));
                    }
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
        packageLifecycleOwner.ReplacePendingPackagesWithRegroupedPackage(sourcePackages, regroupedPackage);
        List<PackageChartEntry> regroupedPackageEntries = regroupedPackage.ChartEntries;
        bool metadataResolved = regroupedPackageEntries.Any(entry => !string.IsNullOrWhiteSpace(entry?.Chart?.InstallDestinationTitle) || !string.IsNullOrWhiteSpace(entry?.Chart?.InstallDestinationArtist));
        LogInstallPerformance("pending_regroup success source=" + sourceDirectoryPath + " packages=" + sourcePackages.Count + " files=" + regroupedPackageEntries.Count + " dst=" + resolvedDestinationDirectory + " metadataResolved=" + metadataResolved);
    }

    private bool TryBuildRegroupedPendingPackage(string sourceDirectoryPath, List<ChartPackage> sourcePackages, IInstalledChartLookupIndex installedDirectoryIndex, out ChartPackage regroupedPackage, out string resolvedDestinationDirectory, out string skipReason)
    {
        regroupedPackage = null;
        resolvedDestinationDirectory = null;
        skipReason = "unknown";
        if (!PackageLifecycleOwner.TryNormalizePendingPackagePath(
            sourceDirectoryPath,
            out string canonicalSourceDirectoryPath))
        {
            skipReason = "invalid_source_path";
            return false;
        }
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
                if (regroupedEntries.Any(entry => entry.IsSameChartTarget(sourceEntry)))
                {
                    continue;
                }

                // Regrouping resolves a new package-level destination and
                // warning projection.  Keep that work detached from the live
                // entries until the owner has accepted the replacement and
                // the install rows have been durably updated.
                ChartFile storageProjection = ChartFileProjection.FromStorageOwner(
                    sourceChart,
                    includeWarningSnapshot: true,
                    includeResourceReferences: true);
                ChartFile packageProjection = storageProjection == null
                    ? sourceChart
                    : ChartFileProjection.WithPackageState(
                        storageProjection,
                        sourceChart.InstallDestination,
                        sourceChart.InstallDestinationTitle,
                        sourceChart.InstallDestinationArtist,
                        sourceChart.InstallDestinationSuggestions,
                        sourceChart.Warnings);
                PackageChartEntry detachedEntry = PackageChartEntry.FromChart(
                    ChartFileProjection.ToImmutableSnapshot(packageProjection));
                if (detachedEntry != null)
                {
                    regroupedEntries.Add(detachedEntry);
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
        regroupedPackage.path = canonicalSourceDirectoryPath;
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
        using (rwlockPendingInstallCharts.GetReaderGuard())
        {
            return ChartPackagesPending.Any(pendingPkg => pendingPkg != null && (ReferenceEquals(pendingPkg, package) || (!string.IsNullOrWhiteSpace(pendingPkg.path) && !string.IsNullOrWhiteSpace(package.path) && pendingPkg.path.Equals(package.path, StringComparison.OrdinalIgnoreCase))));
        }
    }

    public PendingInstalledOnlyResourceOverwriteResult OverwritePendingInstalledOnlyPackagesResources(IEnumerable<ChartPackage> packages, CancellationToken token = default, Action onEachProcessed = null)
    {
        if (packages == null)
        {
            throw new ArgumentNullException(nameof(packages));
        }
        if (TryBlockCatalogFileMutation(nameof(OverwritePendingInstalledOnlyPackagesResources)))
        {
            return new PendingInstalledOnlyResourceOverwriteResult();
        }
        BmsLibraryOptionsSnapshot options = CurrentOptionsSnapshot;
        int deferredProcessedCount = 0;
        List<Action> terminalEffects = [];
        List<string> deferredLogs = [];
        PendingInstalledOnlyResourceOverwriteResult publicResult = null;
        Exception primaryFailure = null;
        Action<string> logInfo = info =>
        {
            if (!string.IsNullOrWhiteSpace(info))
            {
                deferredLogs.Add(info);
            }
        };
        try
        {
            using (LibraryFileMutationLease mutationReservation = TryBeginCatalogFileMutationPreservingBusyFailure(
                nameof(OverwritePendingInstalledOnlyPackagesResources),
                showMessage: true))
            {
                if (mutationReservation == null)
                {
                    return new PendingInstalledOnlyResourceOverwriteResult();
                }
                using LibraryFileMutationCapability mutationCapability =
                    mutationReservation.CreateMutationCapability();
                InstalledChartLookupIndexSnapshot installedDirectoryIndexSnapshot;
                List<ChartPackage> pendingPackageSnapshot;
                using (rwlockBMSFilesInitializedAll.GetReaderGuard())
                using (rwlockPendingInstallCharts.GetReaderGuard())
                using (rwlockBMSFiles.GetReaderGuard())
                {
                    installedDirectoryIndexSnapshot = CreateInstalledChartLookupSnapshotUnsafe();
                    pendingPackageSnapshot = [.. ChartPackagesPending.Where(package => package != null)];
                }
                bool deletePendingPackageSourceAfterInstall = options.DeletePendingPackageSourceAfterInstall;
                string DescribeSkipDetail(InstalledOnlyPackageResolutionResult resolution, ChartPackage pendingPackage)
                {
                    if (pendingPackage == null)
                    {
                        return null;
                    }
                    switch (resolution.Reason)
                    {
                        case InstalledDirectoryResolveReason.ChartHasMultipleInstalledDirectories:
                            ChartFile multipleDirectoryChart = BmsLibraryInstallEstimationService.FindChartWithMultipleInstalledDirectories(pendingPackage, installedDirectoryIndexSnapshot);
                            return "advanced_pending_resource_overwrite skip_chart_multi_dst path=" + pendingPackage.path + " chartPath=" + (multipleDirectoryChart?.Path ?? "(null)") + " hash=" + (ChartLookupKey.GetPrimaryHash(multipleDirectoryChart) ?? "(null)") + " dirCount=" + (multipleDirectoryChart == null ? 0 : BmsLibraryInstallEstimationService.GetDistinctInstalledDirectoriesForChart(installedDirectoryIndexSnapshot, multipleDirectoryChart).Count);
                        case InstalledDirectoryResolveReason.PackageHasSplitInstalledDirectories:
                            return "advanced_pending_resource_overwrite skip_package_split_dst path=" + pendingPackage.path + " dirCount=" + BmsLibraryInstallEstimationService.CountDistinctInstalledDirectoriesForPackage(pendingPackage, installedDirectoryIndexSnapshot);
                        default:
                            ChartFile missingDirectoryChart = BmsLibraryInstallEstimationService.FindChartWithMissingInstalledDirectory(pendingPackage, installedDirectoryIndexSnapshot);
                            return "advanced_pending_resource_overwrite skip_missing_instl_dst path=" + pendingPackage.path + " chartPath=" + (missingDirectoryChart?.Path ?? "(null)") + " hash=" + (ChartLookupKey.GetPrimaryHash(missingDirectoryChart) ?? "(null)");
                    }
                }
                logInfo("advanced_pending_resource_overwrite scan pendingTotal=" + pendingPackageSnapshot.Count + " eligible=" + packageInstallService.DeduplicatePackagesByPathOrReference(packages).Count);
                logInfo("advanced_pending_resource_overwrite index_ready hashes=" + installedDirectoryIndexSnapshot.HashCount);
                bool manualRecoveryObserved = false;
                Func<PackageInstallExecutionResult, FileDbMutationCommitResult> applyDurableStorageRows =
                    installResult => libraryMutationOwner.ApplyInstalledChartStorageTargetsForFileMutation(
                        installResult,
                        "install_package",
                        mutationCapability,
                        terminalEffects.Add);
                PendingResourceOverwriteExecutionResult executionResult = packageInstallService.ExecuteInstalledOnlyResourceOverwrite(
                    packages,
                    pendingPackageSnapshot,
                    deletePendingPackageSourceAfterInstall,
                    pendingPackage => CreateInstallEstimationService().TryPrepareInstalledOnlyPackageDestination(pendingPackage, installedDirectoryIndexSnapshot),
                    DescribeSkipDetail,
                    HasResourceOverwriteTargetsForInstalledOnlyPackage,
                    (_, _) => false,
                    _ => (false, CleanupSourceKind.MissingSource),
                    IsPackageStillPending,
                    token,
                    () => deferredProcessedCount++,
                    logInfo,
                    installPackageToEstimatedDestinationWithReceipt: (pendingPackage, _) =>
                        ExecutePendingPackagesToEstimatedDestinations(
                            [pendingPackage],
                            applyDurableStorageRows,
                            terminalEffects),
                    cleanupPendingPackageSourceWithReceipt: package =>
                        ExecutePendingPackageSourceCleanupWithReceipt(
                            package,
                            applyDurableStorageRows),
                    mutationReceiptObserver: mutationReceipt =>
                    {
                        manualRecoveryObserved |= mutationReceipt?.TerminalState == FileDbMutationTerminalState.ManualRecoveryRequired
                            || mutationReceipt?.TerminalState == FileDbMutationTerminalState.DurableFinalizationFailed;
                        logInfo(
                            "advanced_pending_resource_overwrite mutation_receipt state="
                            + mutationReceipt?.TerminalState
                            + " durable="
                            + (mutationReceipt?.DurableCommit == true)
                            + " recoveryPaths="
                            + string.Join("|", mutationReceipt?.RecoveryPaths ?? []));
                    },
                    manualRecoveryObserved: () => manualRecoveryObserved);
                terminalEffects.AddRange(executionResult.PostLeaseEffects);
                if (executionResult.PendingPackagesToRemove.Count > 0)
                {
                    Action pendingEffect =
                        RemovePendingPackagesFromPendingListAfterDurableMutation(
                        executionResult.PendingPackagesToRemove);
                    if (pendingEffect != null)
                    {
                        terminalEffects.Add(pendingEffect);
                    }
                }
                publicResult = executionResult.ToPublicResult();
                logInfo("advanced_pending_resource_overwrite summary requested=" + publicResult.Requested + " processed=" + publicResult.Processed + " succeededInstall=" + publicResult.SucceededInstall + " succeededCleanupOnly=" + publicResult.SucceededCleanupOnly + " skippedNotPending=" + publicResult.SkippedNotPending + " skippedMissingInstlDst=" + publicResult.SkippedMissingInstlDst + " skippedMultiDst=" + publicResult.SkippedMultiDestination + " skippedNoComponentTarget=" + publicResult.SkippedNoComponentTarget + " failed=" + publicResult.Failed + " canceled=" + publicResult.Canceled);

            }
        }
        catch (Exception exception)
        {
            primaryFailure = exception;
        }

        foreach (Action effect in terminalEffects)
        {
            TryInvokePostLeaseNotification(effect, "advanced_pending_resource_overwrite_notification_failed");
        }
        foreach (string message in deferredLogs)
        {
            TryInvokePostLeaseNotification(
                () => NLogWrapper.FileLogger?.Info(message),
                "advanced_pending_resource_overwrite_log_failed");
        }
        if (onEachProcessed != null)
        {
            for (int index = 0; index < deferredProcessedCount; index++)
            {
                TryInvokePostLeaseNotification(onEachProcessed, "advanced_pending_resource_overwrite_progress_failed");
            }
        }
        if (primaryFailure != null)
        {
            ExceptionDispatchInfo.Capture(primaryFailure).Throw();
        }
        return publicResult ?? new PendingInstalledOnlyResourceOverwriteResult();
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
        Action<string> logInfo = info => NLogWrapper.FileLogger?.Info(info);
        List<ChartFile> chartSnapshot = targetCharts == null
            ? GetPendingBmsFormatChartFilesSnapshot()
            : [.. targetCharts.Where(chart => chart != null)];
        List<Action> postLeaseEffects = [];
        List<Action> diagnosticEffects = [];
        int deferredProcessedCount = 0;
        Exception primaryFailure = null;
        try
        {
            using (LibraryFileMutationLease mutationScope = TryBeginLr2SongDbSyncBlockedMutation(
                nameof(RenamePendingZeroNoteBmsFormatChartsToInvalidExtensions),
                showMessage: true))
            {
                if (mutationScope == null)
                {
                    return;
                }
                using LibraryFileMutationCapability mutationCapability =
                    mutationScope.CreateMutationCapability();
                PendingZeroNoteRenameResult result = libraryMutationOwner.RenamePendingZeroNoteBmsFormatChartsAfterAdmission(
                    chartSnapshot,
                    (file, requestedPath) => ProcessInvalidExtensionRename(file, requestedPath, removeFromLibraryOnSuccess: false),
                    token,
                    () => deferredProcessedCount++,
                    info => diagnosticEffects.Add(() => logInfo(info)));
                foreach (PendingZeroNoteRenameFailure failure in result.Failures)
                {
                    if (failure?.Outcome?.FailureException == null || failure.File == null)
                    {
                        continue;
                    }
                    if (failure.Outcome.FailedDuringDelete)
                    {
                        diagnosticEffects.Add(() => ShowOperationDialog(string.Format(Resources.Error_BmsFileDeleteFailed, failure.File.path, GetDisplayedExceptionMessage(failure.Outcome.FailureException)), Resources.MessageBoxTitle_Error, MessageBoxButton.OK, MessageBoxImage.Hand, MessageBoxResult.OK));
                    }
                    else
                    {
                        diagnosticEffects.Add(() => ShowOperationDialog(string.Format(Resources.Error_BmsFileMoveFailed, failure.File.path, failure.Outcome.FinalPath, GetDisplayedExceptionMessage(failure.Outcome.FailureException)), Resources.MessageBoxTitle_Error, MessageBoxButton.OK, MessageBoxImage.Hand, MessageBoxResult.OK));
                    }
                }
                libraryMutationOwner.RemovePendingChartsFromPendingPackagesAndInstallRows(
                    result.ChartPathsToRemove,
                    mutationCapability,
                    postLeaseNotifications: postLeaseEffects);
                diagnosticEffects.Add(() => logInfo("advanced_pending_zero_note_rename summary total=" + result.Total + " processed=" + result.Processed + " zeroNote=" + result.ZeroNote + " renamed=" + result.Renamed + " duplicateDeleted=" + result.DuplicateDeleted + " skipped=" + result.Skipped + " failed=" + result.Failed + " canceled=" + result.Canceled));
                for (int index = 0; index < deferredProcessedCount; index++)
                {
                    diagnosticEffects.Add(onEachProcessed);
                }
            }
        }
        catch (Exception exception)
        {
            primaryFailure = exception;
        }
        FlushPostLeaseEffects(postLeaseEffects, diagnosticEffects);
        if (primaryFailure != null)
        {
            ExceptionDispatchInfo.Capture(primaryFailure).Throw();
        }
    }

    /// <summary>
    /// Pending パッケージのソースファイル群を削除（またはゴミ箱へ移動）します。
    /// </summary>
    public void DeletePendingPackageSources(IEnumerable<ChartPackage> packages, bool sendToRecycleBin = true, CancellationToken token = default, Action onEachProcessed = null)
    {
        if (packages == null)
        {
            throw new ArgumentNullException(nameof(packages));
        }
        Action<string> logInfo = info => NLogWrapper.FileLogger?.Info(info);
        List<ChartPackage> requestedPackageSnapshot =
            [.. packageInstallService.DeduplicatePackagesByPathOrReference(packages)];
        List<PendingPackageSourceDeletionTarget> requestedTargets;
        using (rwlockPendingInstallCharts.GetReaderGuard())
        {
            // Capture only requested package identities and authorized source paths.
            requestedTargets = [.. requestedPackageSnapshot
                .Select(package => new PendingPackageSourceDeletionTarget(package))];
        }
        List<Action> postLeaseEffects = [];
        List<Action> diagnosticEffects = [];
        int deferredProcessedCount = 0;
        Exception primaryFailure = null;
        try
        {
            using (LibraryFileMutationLease mutationScope = TryBeginLr2SongDbSyncBlockedMutation(
                nameof(DeletePendingPackageSources),
                showMessage: true))
            {
                if (mutationScope == null)
                {
                    return;
                }
                diagnosticEffects.Add(() => logInfo("advanced_pending_cleanup start requested=" + requestedTargets.Count + " permanent=" + !sendToRecycleBin));

                PendingPackageSourceDeletionResult result = new()
                {
                    Requested = requestedTargets.Count
                };
                PendingPackageSourceDeletionResult execution = packageInstallService.DeletePendingPackageSources(
                    requestedTargets,
                    sendToRecycleBin,
                    fileMutationService,
                    targetOnlyFileMutationOptions,
                    recursiveDirectoryTreeFileMutationOptions,
                    token,
                    () => deferredProcessedCount++,
                    info => diagnosticEffects.Add(() => logInfo(info)));
                result.Processed += execution.Processed;
                result.Removed += execution.Removed;
                result.Failed += execution.Failed;
                result.Skipped += execution.Skipped;
                result.Canceled = execution.Canceled;
                result.PackagesToRemove.AddRange(execution.PackagesToRemove);
                result.Failures.AddRange(execution.Failures);
                foreach (PendingPackageSourceDeletionFailure failure in result.Failures)
                {
                    if (failure?.Package == null)
                    {
                        continue;
                    }
                    diagnosticEffects.Add(() => NLogWrapper.FileLogger?.Warn(failure.Exception, "advanced_pending_cleanup failed path=" + failure.Package.path + " kind=" + (failure.IsDirectory ? "directory" : "file") + " error=" + GetDisplayedExceptionMessage(failure.Exception)));
                    if (failure.IsDirectory)
                    {
                        diagnosticEffects.Add(() => ShowOperationDialog(string.Format(Resources.Error_FolderOrTrashDeleteFailed, failure.Package.path, GetDisplayedExceptionMessage(failure.Exception)), Resources.MessageBoxTitle_Error, MessageBoxButton.OK, MessageBoxImage.Hand, MessageBoxResult.OK));
                    }
                    else
                    {
                        diagnosticEffects.Add(() => ShowOperationDialog(string.Format(Resources.Error_BmsFileDeleteFailed, failure.Package.path, GetDisplayedExceptionMessage(failure.Exception)), Resources.MessageBoxTitle_Error, MessageBoxButton.OK, MessageBoxImage.Hand, MessageBoxResult.OK));
                    }
                }
                Action pendingEffect = RemovePendingPackagesFromPendingListAndInstallRows(
                    result.PackagesToRemove);
                if (pendingEffect != null)
                {
                    postLeaseEffects.Add(pendingEffect);
                }
                diagnosticEffects.Add(() => logInfo("advanced_pending_cleanup summary requested=" + result.Requested + " processed=" + result.Processed + " removed=" + result.Removed + " failed=" + result.Failed + " skipped=" + result.Skipped + " canceled=" + result.Canceled));
                for (int index = 0; index < deferredProcessedCount; index++)
                {
                    diagnosticEffects.Add(onEachProcessed);
                }
            }
        }
        catch (Exception exception)
        {
            primaryFailure = exception;
        }
        FlushPostLeaseEffects(postLeaseEffects, diagnosticEffects);
        if (primaryFailure != null)
        {
            ExceptionDispatchInfo.Capture(primaryFailure).Throw();
        }
    }

    private Action RemovePendingPackagesFromPendingListAndInstallRows(
        IEnumerable<ChartPackage> packages,
        IEnumerable<ChartPackage> currentPendingPackages = null)
    {
        PendingPackageMutationDelta delta = packageInstallService.BuildPendingPackageMutationDelta(
            currentPendingPackages ?? ChartPackagesPending,
            packagesToRemove: packages);
        IDisposable collectionPublicationScope = packageLifecycleOwner.BeginCollectionMutationScope();
        try
        {
            packageLifecycleOwner.ApplyPendingPackageMutationDelta(delta);
        }
        catch
        {
            collectionPublicationScope.Dispose();
            throw;
        }
        return collectionPublicationScope.Dispose;
    }

    private Action RemovePendingPackagesFromPendingListAfterDurableMutation(
        IEnumerable<ChartPackage> packages)
    {
        PendingPackageMutationDelta delta = BuildPendingPackageMutationDelta(packagesToRemove: packages);
        // The package's receipt owner already deleted each install row in the
        // same durable transaction as its target upsert.  This follow-up only
        // publishes the in-memory pending collection removal.
        delta.InstallPathsToDelete.Clear();
        return ApplyPendingPackageMutationDeltaUnderExistingReservation(
            delta,
            packagesToAdd: null,
            installRowsToUpsert: null);
    }
}
