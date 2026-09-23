using System;
using System.Collections.Generic;
using System.Linq;
using BeMusicSeeker.Models.BmsLibraryInternal;
using BeMusicSeeker.Models.Utils;

namespace BeMusicSeeker.Models;

internal sealed partial class LibraryMutationOwner
{
    /// <summary>
    /// 取得済みの file mutation capability の下で操作単位の session を開始します。
    /// append は canonical state を変更せず、<see cref="LibraryMutationSession.Commit"/> が
    /// catalog/state/index、LR2 同期、通常通知の準備を操作単位で一度だけ行います。
    /// </summary>
    /// <param name="mutationCapability">The live capability issued by the owning outer mutation lease.</param>
    /// <param name="reason">Stable diagnostic reason for the operation-scoped apply.</param>
    /// <param name="postLeaseNotifications">Command-owned publication list released after the lease.</param>
    /// <returns>An open session that accepts only confirmed mutation facts.</returns>
    internal LibraryMutationSession BeginLibraryMutationSession(
        LibraryFileMutationCapability mutationCapability,
        string reason,
        ICollection<Action> postLeaseNotifications)
    {
        ArgumentNullException.ThrowIfNull(mutationCapability);
        ArgumentNullException.ThrowIfNull(postLeaseNotifications);
        return new LibraryMutationSession(
            this,
            mutationCapability,
            reason,
            postLeaseNotifications);
    }

    /// <summary>
    /// Performs only the physical folder move. Canonical state is intentionally left unchanged
    /// until the owning mutation session commits all confirmed move facts together.
    /// </summary>
    /// <param name="sourceDirectory">Exact source directory accepted by the operation preflight.</param>
    /// <param name="destinationDirectory">Exact destination directory accepted by the operation preflight.</param>
    internal void MoveFolderPhysical(string sourceDirectory, string destinationDirectory)
    {
        libraryFileOperationsService.MoveFolder(
            sourceDirectory,
            destinationDirectory,
            fileMutationService,
            recursiveDirectoryTreeFileMutationOptions);
    }

    /// <summary>
    /// Collects immutable mutation facts for one accepted user operation and applies them once.
    /// </summary>
    internal sealed class LibraryMutationSession
    {
        private readonly LibraryMutationOwner owner;
        private readonly LibraryFileMutationCapability mutationCapability;
        private readonly string reason;
        private readonly ICollection<Action> postLeaseNotifications;
        private readonly List<LibraryCatalogMutationFacts> catalogFacts = [];
        private readonly List<LibraryPackageReferenceFacts> packageReferenceFacts = [];
        private readonly List<LibraryMutationSessionTarget> confirmedTargets = [];
        private readonly List<LibraryMutationSessionItemFailure> itemFailures = [];
        private readonly List<string> recoveryCandidatePaths = [];
        private readonly HashSet<string> recoveryCandidatePathSet = new(StringComparer.OrdinalIgnoreCase);
        private readonly List<LibraryFolderPathChange> movedFolders = [];
        private readonly List<LibraryFolderPathChange> mergedFolders = [];
        private readonly HashSet<string> resourceDirectoryRemovals = new(StringComparer.OrdinalIgnoreCase);
        private readonly List<ChartFile> installedPackageCharts = [];
        private readonly HashSet<string> installPathsToDelete = new(StringComparer.Ordinal);
        private readonly List<ChartPackage> installRowsToUpsert = [];
        private readonly List<PackageInstallSessionPhysicalMutation> preparedPackageMutations = [];
        private readonly HashSet<string> installedResourceDirectories = new(StringComparer.OrdinalIgnoreCase);
        private readonly List<Action> requiredDurableFinalizers = [];
        private readonly List<FileDbMutationDestinationTypeConflict> destinationTypeConflicts = [];
        private readonly HashSet<string> destinationTypeConflictKeys = new(StringComparer.OrdinalIgnoreCase);
        private LibraryStorageRowPathNotificationPolicy storageRowPathNotificationPolicy =
            LibraryStorageRowPathNotificationPolicy.Suppressed;
        private Exception physicalFailure;
        private Exception cleanupFailure;
        private bool manualRecoveryRequired;
        private LibraryMutationSessionTarget failedTarget;
        private IReadOnlyList<LibraryMutationSessionTarget> unprocessedTargets = [];
        private bool committed;
        private LibraryMutationSessionApplyCounts applyCounts;

        /// <summary>Creates an open operation-scoped mutation session owned by one outer lease.</summary>
        /// <param name="owner">Library mutation owner that performs the canonical apply.</param>
        /// <param name="mutationCapability">Live capability from the owning outer lease.</param>
        /// <param name="reason">Stable operation diagnostic reason.</param>
        /// <param name="postLeaseNotifications">Command-owned publication collection.</param>
        internal LibraryMutationSession(
            LibraryMutationOwner owner,
            LibraryFileMutationCapability mutationCapability,
            string reason,
            ICollection<Action> postLeaseNotifications)
        {
            this.owner = owner ?? throw new ArgumentNullException(nameof(owner));
            this.mutationCapability = mutationCapability ?? throw new ArgumentNullException(nameof(mutationCapability));
            this.reason = reason ?? string.Empty;
            this.postLeaseNotifications = postLeaseNotifications ?? throw new ArgumentNullException(nameof(postLeaseNotifications));
        }

        /// <summary>
        /// Appends a folder move only after its physical move is confirmed successful.
        /// </summary>
        /// <param name="sourceDirectory">The exact source path used by the successful physical move.</param>
        /// <param name="destinationDirectory">The exact destination path produced by the successful physical move.</param>
        /// <param name="facts">Immutable catalog/package facts captured before the physical move.</param>
        internal void AppendFolderMove(
            string sourceDirectory,
            string destinationDirectory,
            LibraryFolderMoveFacts facts)
        {
            EnsureOpen();
            ArgumentNullException.ThrowIfNull(facts);
            AppendCatalogChange(
                facts.CatalogFacts,
                facts.PackageReferenceFacts,
                [new LibraryMutationSessionTarget(sourceDirectory, destinationDirectory)],
                facts.StorageRowPathNotificationPolicy);
            movedFolders.Add(new LibraryFolderPathChange
            {
                OldFolderPath = sourceDirectory,
                NewFolderPath = destinationDirectory
            });
        }

        /// <summary>
        /// 確認済みのフォルダ統合を一つの change として受け入れます。
        /// source cleanup と統合先の逆引き反映は canonical durable apply 後に行います。
        /// </summary>
        /// <param name="sourceDirectory">統合元の確定 directory。</param>
        /// <param name="destinationDirectory">統合先の確定 directory。</param>
        /// <param name="catalogMutationFacts">実 destination から作った catalog facts。</param>
        /// <param name="packageMutationFacts">同じ統合に伴う package reference facts。</param>
        /// <param name="physicalMutation">promotion 済みで source cleanup を保留した physical mutation。</param>
        internal void AppendFolderMerge(
            string sourceDirectory,
            string destinationDirectory,
            LibraryCatalogMutationFacts catalogMutationFacts,
            LibraryPackageReferenceFacts packageMutationFacts,
            PackageInstallSessionPhysicalMutation physicalMutation)
        {
            EnsureOpen();
            ArgumentNullException.ThrowIfNull(physicalMutation);
            AppendCatalogChange(
                catalogMutationFacts,
                packageMutationFacts,
                [new LibraryMutationSessionTarget(sourceDirectory, destinationDirectory)],
                LibraryStorageRowPathNotificationPolicy.Suppressed);
            preparedPackageMutations.Add(physicalMutation);
            mergedFolders.Add(new LibraryFolderPathChange
            {
                OldFolderPath = sourceDirectory,
                NewFolderPath = destinationDirectory
            });
        }

        /// <summary>
        /// Appends already-confirmed catalog/package facts without applying canonical state.
        /// This is the shared path for non-folder filesystem batches such as chart deletion
        /// and invalid-extension rename.
        /// </summary>
        /// <param name="catalogMutationFacts">Confirmed catalog changes for the batch.</param>
        /// <param name="packageMutationFacts">Confirmed package-reference changes for the batch.</param>
        /// <param name="targets">Filesystem changes confirmed before this append.</param>
        /// <param name="notificationPolicy">Storage-row path publication policy for these facts.</param>
        internal void AppendCatalogChange(
            LibraryCatalogMutationFacts catalogMutationFacts,
            LibraryPackageReferenceFacts packageMutationFacts,
            IEnumerable<LibraryMutationSessionTarget> targets,
            LibraryStorageRowPathNotificationPolicy notificationPolicy = LibraryStorageRowPathNotificationPolicy.Notify)
        {
            EnsureOpen();
            catalogFacts.Add(catalogMutationFacts ?? LibraryCatalogMutationFacts.Empty);
            packageReferenceFacts.Add(packageMutationFacts ?? LibraryPackageReferenceFacts.Empty);
            confirmedTargets.AddRange((targets ?? []).Where(target => target != null));
            if (notificationPolicy == LibraryStorageRowPathNotificationPolicy.Notify)
            {
                storageRowPathNotificationPolicy = LibraryStorageRowPathNotificationPolicy.Notify;
            }
        }

        /// <summary>
        /// Retains attempted item failures for one operation-scoped terminal report. These failures
        /// do not change the success facts already appended and do not force otherwise-safe suffix
        /// targets to stop.
        /// </summary>
        /// <param name="failures">Failed source/destination candidates observed by the item executor.</param>
        internal void AppendItemFailures(IEnumerable<LibraryMutationSessionItemFailure> failures)
        {
            EnsureOpen();
            itemFailures.AddRange((failures ?? []).Where(failure => failure != null));
        }

        /// <summary>
        /// Registers successfully deleted directory subtrees for resource-index removal after
        /// the session's catalog and required internal apply succeed. Planned or failed directories
        /// must not be added.
        /// </summary>
        /// <param name="sourceDirectories">Confirmed deleted directory roots.</param>
        internal void AppendResourceDirectoryRemovals(IEnumerable<string> sourceDirectories)
        {
            EnsureOpen();
            foreach (string path in sourceDirectories ?? [])
            {
                if (!string.IsNullOrWhiteSpace(path))
                {
                    resourceDirectoryRemovals.Add(path);
                }
            }
        }

        /// <summary>
        /// Retains executor-local recovery candidates that must survive aggregation into the
        /// operation-scoped terminal receipt. These paths are diagnostics only and do not add
        /// confirmed mutation facts.
        /// </summary>
        /// <param name="paths">Physical paths that may require manual confirmation or recovery.</param>
        internal void AppendRecoveryCandidatePaths(IEnumerable<string> paths)
        {
            EnsureOpen();
            AppendRecoveryCandidatePathsCore(paths);
        }

        /// <summary>
        /// Appends one physically prepared package change to the operation-scoped install session.
        /// Exact chart identity normalization is intentionally delegated to <see cref="ChartStorageTargetSet"/> at commit.
        /// cleanup-only の zero-file change は install-row fact で session change を表し、physical confirmed target を捏造しません。
        /// </summary>
        internal void AppendInstalledPackageChange(
            PackageInstallExecutionResult installResult,
            PackageInstallSessionPhysicalMutation physicalMutation)
        {
            EnsureOpen();
            ArgumentNullException.ThrowIfNull(installResult);
            ArgumentNullException.ThrowIfNull(physicalMutation);

            IReadOnlyList<ChartFile> charts = installResult.AddedCharts.Count > 0
                ? installResult.AddedCharts
                : [.. installResult.AddedEntries
                    .Select(entry => entry?.Chart)
                    .Where(chart => chart != null)];
            installedPackageCharts.AddRange(charts);
            if (!string.IsNullOrWhiteSpace(installResult.InstallPathToDelete))
            {
                installPathsToDelete.Add(installResult.InstallPathToDelete);
            }
            preparedPackageMutations.Add(physicalMutation);
            confirmedTargets.AddRange(physicalMutation.ConfirmedTargets);
            if (!string.IsNullOrWhiteSpace(physicalMutation.DestinationDirectory))
            {
                installedResourceDirectories.Add(physicalMutation.DestinationDirectory);
            }
        }

        /// <summary>同じ install session の canonical transaction で削除する pending install row path を追加します。</summary>
        internal void AppendInstallPathsToDelete(IEnumerable<string> paths)
        {
            EnsureOpen();
            foreach (string path in paths ?? [])
            {
                if (!string.IsNullOrWhiteSpace(path))
                {
                    installPathsToDelete.Add(path);
                }
            }
        }

        /// <summary>同じ install session の canonical transaction で upsert する pending install row を追加します。</summary>
        internal void AppendInstallRowsToUpsert(IEnumerable<ChartPackage> packages)
        {
            EnsureOpen();
            installRowsToUpsert.AddRange((packages ?? [])
                .Where(package => package != null && !string.IsNullOrWhiteSpace(package.path)));
        }

        /// <summary>
        /// canonical durable apply 後に一度だけ実行する required finalizer を追加します。
        /// merge の対象捕捉は source cleanup 前、install の保守・lifecycle 反映は resource scan 後に行います。
        /// </summary>
        internal void AppendRequiredDurableFinalizer(Action finalizer)
        {
            EnsureOpen();
            if (finalizer != null)
            {
                requiredDurableFinalizers.Add(finalizer);
            }
        }

        /// <summary>一 package の事前拒否または physical prepare failure を session terminal facts に集約します。</summary>
        /// <param name="receipt">拒否または physical prepare failure の receipt。</param>
        /// <param name="isPreflightRefusal">変更開始前に確定した、後続を停止しない拒否かどうか。</param>
        internal void AppendPackagePhysicalFailure(FileDbMutationReceipt receipt, bool isPreflightRefusal = false)
        {
            EnsureOpen();
            AppendPackagePhysicalFailureCore(receipt, isPreflightRefusal);
        }

        /// <summary>
        /// Retains a best-effort physical cleanup failure without changing the confirmed
        /// mutation facts or stopping otherwise-safe suffix processing.
        /// </summary>
        /// <param name="failure">The cleanup failure observed after a physical move became durable.</param>
        internal void RecordCleanupFailure(Exception failure)
        {
            EnsureOpen();
            if (failure == null)
            {
                return;
            }
            cleanupFailure = cleanupFailure == null
                ? failure
                : new AggregateException(cleanupFailure, failure);
        }

        /// <summary>
        /// Records the first unexpected item failure and the suffix that must remain unprocessed.
        /// </summary>
        /// <param name="sourceDirectory">The source path whose processing failed.</param>
        /// <param name="destinationDirectory">The destination path whose processing failed.</param>
        /// <param name="failure">The unexpected failure that stops unsafe suffix processing.</param>
        /// <param name="remainingTargets">Targets intentionally left unprocessed after the failure.</param>
        internal void RecordStoppedSuffix(
            string sourceDirectory,
            string destinationDirectory,
            Exception failure,
            IEnumerable<LibraryMutationSessionTarget> remainingTargets)
        {
            EnsureOpen();
            LibraryMutationSessionTarget[] remaining = [.. (remainingTargets ?? [])
                .Where(target => target != null)];
            if (physicalFailure != null)
            {
                if (unprocessedTargets.Count == 0 && remaining.Length > 0)
                {
                    unprocessedTargets = Array.AsReadOnly(remaining);
                }
                return;
            }
            physicalFailure = failure;
            failedTarget = new LibraryMutationSessionTarget(sourceDirectory, destinationDirectory);
            unprocessedTargets = Array.AsReadOnly(remaining);
        }

        /// <summary>
        /// Applies all confirmed facts exactly once. A failure after the catalog durable point is
        /// retained on the receipt and never causes filesystem rollback or replay.
        /// </summary>
        /// <returns>Immutable terminal facts for the operation-scoped session.</returns>
        internal LibraryMutationSessionReceipt Commit()
        {
            EnsureOpen();
            committed = true;

            bool hasInstallChanges = installedResourceDirectories.Count > 0
                || installPathsToDelete.Count > 0
                || installRowsToUpsert.Count > 0
                || installedPackageCharts.Count > 0;
            bool hasGeneralChanges = catalogFacts.Any(item => item?.HasChanges == true)
                || packageReferenceFacts.Any(item => item?.HasChanges == true)
                || movedFolders.Count > 0
                || mergedFolders.Count > 0
                || resourceDirectoryRemovals.Count > 0;
            if (!hasInstallChanges && !hasGeneralChanges)
            {
                return CreateReceipt(durableCommit: false);
            }
            if (hasInstallChanges && hasGeneralChanges)
            {
                Exception invalidMix = new InvalidOperationException(
                    "Install storage changes and general library mutation facts must not share one session commit.");
                foreach (PackageInstallSessionPhysicalMutation physicalMutation in preparedPackageMutations)
                {
                    AppendRecoveryCandidatePathsCore(physicalMutation.RecoveryCandidatePaths);
                }
                return CreateReceipt(durableCommit: false, applyFailure: invalidMix);
            }

            var sessionNotifications = new List<Action>();
            FileDbMutationCommitResult applyResult;
            if (hasInstallChanges)
            {
                var installedTargets = ChartStorageTargetSet.FromInstalledCharts(installedPackageCharts);
                applyResult = owner.CommitInstalledSessionChanges(
                    installedTargets,
                    installPathsToDelete,
                    installRowsToUpsert,
                    reason,
                    mutationCapability,
                    sessionNotifications.Add,
                    ref applyCounts);
            }
            else
            {
                LibraryCatalogMutationFacts combinedCatalogFacts = CombineCatalogFacts(catalogFacts);
                LibraryPackageReferenceFacts combinedPackageFacts = CombinePackageReferenceFacts(packageReferenceFacts);
                applyResult = owner.CommitCatalogSessionChanges(
                    combinedCatalogFacts,
                    combinedPackageFacts,
                    reason,
                    mutationCapability,
                    sessionNotifications.Add,
                    ref applyCounts,
                    storageRowPathNotificationPolicy);
            }

            if (!applyResult.DurableCommit)
            {
                foreach (Action notification in sessionNotifications)
                {
                    postLeaseNotifications.Add(notification);
                }
                foreach (PackageInstallSessionPhysicalMutation physicalMutation in preparedPackageMutations)
                {
                    AppendRecoveryCandidatePathsCore(physicalMutation.RecoveryCandidatePaths);
                }
                return CreateReceipt(durableCommit: false, applyFailure: applyResult.Failure);
            }

            Exception finalizationFailure = applyResult.Failure;
            if (finalizationFailure == null)
            {
                try
                {
                    applyResult.DurableFinalizer?.Invoke();
                }
                catch (Exception exception)
                {
                    finalizationFailure = exception;
                }
            }

            if (!hasInstallChanges && finalizationFailure == null)
            {
                // merge の maintenance input は canonical owner が移転した直後に固定し、
                // source cleanup の副作用や lease 解放後の変更から切り離します。
                finalizationFailure = RunRequiredDurableFinalizers();
            }

            DirectoryResourceLookupCache.ReverseLookupMutationResult reverseLookupMutation =
                DirectoryResourceLookupCache.ReverseLookupMutationResult.Empty;
            foreach (PackageInstallSessionPhysicalMutation physicalMutation in preparedPackageMutations)
            {
                bool applyLiveState = finalizationFailure == null;
                FileDbMutationReceipt receipt = physicalMutation.CompleteAfterDurableCommit(
                    applyLiveState,
                    finalizationFailure);
                AppendRecoveryCandidatePathsCore(receipt?.RecoveryPaths);
                if (receipt?.CleanupFailure != null)
                {
                    cleanupFailure = CombineFailure(cleanupFailure, receipt.CleanupFailure);
                }
                if (finalizationFailure == null && receipt?.FinalizationFailure != null)
                {
                    finalizationFailure = CombineFailure(finalizationFailure, receipt.FinalizationFailure);
                }
                if (receipt?.TerminalState == FileDbMutationTerminalState.ManualRecoveryRequired)
                {
                    manualRecoveryRequired = true;
                }
            }

            if (hasInstallChanges)
            {
                if (finalizationFailure == null && installedResourceDirectories.Count > 0)
                {
                    if (ChartDirectoryScanBuilder.TryBuildFromRoots(
                        installedResourceDirectories,
                        out ChartScanResult scan,
                        out string scanFailureReason))
                    {
                        applyCounts = applyCounts with { ReverseLookupApplyCount = applyCounts.ReverseLookupApplyCount + 1 };
                        reverseLookupMutation = owner.AddReverseLookupDirectories(scan);
                    }
                    else
                    {
                        owner.LogInstallPerformanceWarning(
                            reason + " resource_cache_update skipped reason=incomplete_scan detail="
                            + (scanFailureReason ?? "unknown")
                            + " dirs=" + installedResourceDirectories.Count);
                    }
                }
            }
            else if (finalizationFailure == null)
            {
                try
                {
                    if (movedFolders.Count > 0)
                    {
                        applyCounts = applyCounts with { ReverseLookupApplyCount = applyCounts.ReverseLookupApplyCount + 1 };
                        reverseLookupMutation = owner.UpdateMovedFolderReferences(movedFolders).MutationResult;
                    }
                    foreach (LibraryFolderPathChange mergedFolder in mergedFolders)
                    {
                        if (ChartDirectoryScanBuilder.TryBuildFromRoots(
                            [mergedFolder.NewFolderPath],
                            out ChartScanResult scan,
                            out string scanFailureReason))
                        {
                            applyCounts = applyCounts with { ReverseLookupApplyCount = applyCounts.ReverseLookupApplyCount + 1 };
                            reverseLookupMutation = reverseLookupMutation.Combine(
                                owner.resourceIndexOwner.ReplaceSourceDirectoryWithScan(
                                    mergedFolder.OldFolderPath, scan).MutationResult);
                        }
                        else
                        {
                            owner.LogInstallPerformanceWarning(
                                reason + " resource_cache_update skipped reason=incomplete_scan detail="
                                + (scanFailureReason ?? "unknown"));
                        }
                    }
                    if (resourceDirectoryRemovals.Count > 0)
                    {
                        applyCounts = applyCounts with { ReverseLookupApplyCount = applyCounts.ReverseLookupApplyCount + 1 };
                        reverseLookupMutation = reverseLookupMutation.Combine(
                            owner.resourceIndexOwner
                                .RemoveUnderSourceDirectories(resourceDirectoryRemovals)
                                .MutationResult);
                    }
                }
                catch (Exception exception)
                {
                    finalizationFailure = exception;
                }
            }

            if (hasInstallChanges && finalizationFailure == null)
            {
                finalizationFailure = RunRequiredDurableFinalizers();
            }

            if (finalizationFailure == null)
            {
                foreach (Action notification in sessionNotifications)
                {
                    postLeaseNotifications.Add(notification);
                }
                postLeaseNotifications.Add(() => owner.LogReverseLookupMutationAndQueueWarmupIfNeeded(
                    reason,
                    reverseLookupMutation));
            }
            return CreateReceipt(
                durableCommit: true,
                applyFailure: applyResult.Failure,
                finalizationFailure: ReferenceEquals(finalizationFailure, applyResult.Failure)
                    ? null
                    : finalizationFailure);
        }

        private Exception RunRequiredDurableFinalizers()
        {
            foreach (Action finalizer in requiredDurableFinalizers)
            {
                try
                {
                    finalizer();
                }
                catch (Exception exception)
                {
                    return exception;
                }
            }
            return null;
        }

        private void AppendPackagePhysicalFailureCore(FileDbMutationReceipt receipt, bool isPreflightRefusal)
        {
            if (receipt == null)
            {
                return;
            }
            AppendRecoveryCandidatePathsCore(receipt.RecoveryPaths);
            manualRecoveryRequired |= receipt.TerminalState == FileDbMutationTerminalState.ManualRecoveryRequired;
            foreach (FileDbMutationDestinationTypeConflict conflict in receipt.DestinationTypeConflicts ?? [])
            {
                string key = string.Join("\u001f",
                    conflict.SourcePath,
                    conflict.DestinationPath,
                    conflict.ExpectedIsDirectory,
                    conflict.ExistingIsDirectory);
                if (destinationTypeConflictKeys.Add(key))
                {
                    destinationTypeConflicts.Add(conflict);
                }
            }
            // 継続可能な item-level refusal だけを ItemFailures に保持します。
            // suffix を停止する failure は RecordStoppedSuffix が PhysicalFailure/FailedTarget として
            // 同じ terminal に保持するため、ここでも追加すると terminal count と failure 選択が二重になります。
            if (receipt.Failure != null && (isPreflightRefusal || receipt.DestinationTypeConflicts.Count > 0))
            {
                string sourcePath = receipt.SourcePaths.FirstOrDefault() ?? string.Empty;
                string destinationPath = receipt.DestinationPaths.FirstOrDefault() ?? string.Empty;
                itemFailures.Add(new LibraryMutationSessionItemFailure(
                    new LibraryMutationSessionTarget(sourcePath, destinationPath),
                    receipt.Failure,
                    receipt.DestinationTypeConflicts));
            }
        }

        private void AppendRecoveryCandidatePathsCore(IEnumerable<string> paths)
        {
            foreach (string path in paths ?? [])
            {
                if (!string.IsNullOrWhiteSpace(path) && recoveryCandidatePathSet.Add(path))
                {
                    recoveryCandidatePaths.Add(path);
                }
            }
        }

        private static Exception CombineFailure(Exception current, Exception next)
        {
            if (next == null)
            {
                return current;
            }
            return current == null ? next : new AggregateException(current, next);
        }

        private LibraryMutationSessionReceipt CreateReceipt(
            bool durableCommit,
            Exception applyFailure = null,
            Exception finalizationFailure = null)
        {
            var receipt = new LibraryMutationSessionReceipt(
                confirmedTargets,
                durableCommit,
                catalogChartRemovalCount: catalogFacts.Sum(item => item?.ChartRemoveRequests?.Count ?? 0),
                catalogChartPathChangeCount: catalogFacts.Sum(item => item?.ChartPathChanges?.Count ?? 0),
                catalogFolderPathChangeCount: catalogFacts.Sum(item => item?.FolderPathChanges?.Count ?? 0),
                packageInstallDestinationChangeCount: packageReferenceFacts.Sum(
                    item => item?.InstallDestinationChanges?.Count ?? 0),
                packageInstalledPathChangeCount: packageReferenceFacts.Sum(
                    item => item?.InstalledPackagePathChanges?.Count ?? 0),
                folderReferenceMoveCount: movedFolders.Count,
                physicalFailure: physicalFailure,
                failedTarget: failedTarget,
                unprocessedTargets: unprocessedTargets,
                applyFailure: applyFailure,
                finalizationFailure: finalizationFailure,
                cleanupFailure: cleanupFailure,
                resourceDirectoryRemovalCount: resourceDirectoryRemovals.Count,
                recoveryCandidatePaths: recoveryCandidatePaths,
                itemFailures: itemFailures,
                manualRecoveryRequired: manualRecoveryRequired,
                destinationTypeConflicts: destinationTypeConflicts,
                applyCounts: applyCounts);
            // Commit の反映範囲を一度だけ診断します。merge 等の lease 解放後 maintenance や
            // subscriber の完了時間とは別で、診断自体は既存の lease 解放後通知へ渡します。
            postLeaseNotifications.Add(() => owner.LogInstallPerformance(
                "library_mutation_session_apply_done reason=" + reason
                + " changeCount=" + receipt.ConfirmedChangeCount
                + " durableCommit=" + receipt.DurableCommit.ToString().ToLowerInvariant()
                + " requiredFailure=" + receipt.HasRequiredFailure.ToString().ToLowerInvariant()
                + " catalogApplyCount=" + receipt.ApplyCounts.CatalogApplyCount
                + " installedTargetApplyCount=" + receipt.ApplyCounts.InstalledTargetApplyCount
                + " packageReferenceApplyCount=" + receipt.ApplyCounts.PackageReferenceApplyCount
                + " reverseLookupApplyCount=" + receipt.ApplyCounts.ReverseLookupApplyCount
                + " lr2SyncCount=" + receipt.ApplyCounts.Lr2SyncCount
                + " requiredPublicationCount=" + receipt.ApplyCounts.RequiredPublicationCount
                + " folderDbTargetRows=" + receipt.ApplyCounts.FolderDbTargetRows
                + " folderDbFullScanCount=" + receipt.ApplyCounts.FolderDbFullScanCount));
            return receipt;
        }

        private void EnsureOpen()
        {
            if (committed)
            {
                throw new InvalidOperationException("Library mutation session has already been committed.");
            }
        }

        private static LibraryCatalogMutationFacts CombineCatalogFacts(
            IEnumerable<LibraryCatalogMutationFacts> facts)
        {
            List<LibraryCatalogMutationFacts> items = [.. (facts ?? []).Where(item => item != null)];
            return new LibraryCatalogMutationFacts(
                items.SelectMany(item => item.ChartRemoveRequests),
                items.SelectMany(item => item.ChartPathChanges),
                items.SelectMany(item => item.FolderPathChanges));
        }

        private static LibraryPackageReferenceFacts CombinePackageReferenceFacts(
            IEnumerable<LibraryPackageReferenceFacts> facts)
        {
            List<LibraryPackageReferenceFacts> items = [.. (facts ?? []).Where(item => item != null)];
            return new LibraryPackageReferenceFacts(
                items.SelectMany(item => item.InstallDestinationChanges),
                items.SelectMany(item => item.InstalledPackagePathChanges));
        }
    }
}
