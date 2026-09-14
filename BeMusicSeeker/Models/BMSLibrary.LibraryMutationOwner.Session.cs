using System;
using System.Collections.Generic;
using System.Linq;
using BeMusicSeeker.Models.BmsLibraryInternal;

namespace BeMusicSeeker.Models;

internal sealed partial class LibraryMutationOwner
{
    /// <summary>
    /// Starts an operation-scoped mutation session under an already-owned file mutation capability.
    /// Appending facts has no canonical side effects; <see cref="LibraryMutationSession.Commit"/>
    /// performs the shared catalog/state/index apply once for the operation.
    /// </summary>
    /// <param name="mutationCapability">The live capability issued by the owning outer mutation lease.</param>
    /// <param name="reason">Stable diagnostic reason for the operation-scoped apply.</param>
    /// <param name="postLeaseNotifications">Command-owned publication list released after the lease.</param>
    /// <param name="suppressNormalRefreshNotification">Whether the shared apply should omit its normal refresh publication.</param>
    /// <param name="suppressLr2NormalFolderSync">Whether LR2 folder synchronization is finalized by the caller instead.</param>
    /// <returns>An open session that accepts only confirmed mutation facts.</returns>
    internal LibraryMutationSession BeginLibraryMutationSession(
        LibraryFileMutationCapability mutationCapability,
        string reason,
        ICollection<Action> postLeaseNotifications,
        bool suppressNormalRefreshNotification,
        bool suppressLr2NormalFolderSync)
    {
        ArgumentNullException.ThrowIfNull(mutationCapability);
        ArgumentNullException.ThrowIfNull(postLeaseNotifications);
        return new LibraryMutationSession(
            this,
            mutationCapability,
            reason,
            postLeaseNotifications,
            suppressNormalRefreshNotification,
            suppressLr2NormalFolderSync);
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
        private readonly bool suppressNormalRefreshNotification;
        private readonly bool suppressLr2NormalFolderSync;
        private readonly List<LibraryCatalogMutationFacts> catalogFacts = [];
        private readonly List<LibraryPackageReferenceFacts> packageReferenceFacts = [];
        private readonly List<LibraryMutationSessionTarget> confirmedTargets = [];
        private readonly List<LibraryMutationSessionItemFailure> itemFailures = [];
        private readonly List<LibraryFolderPathChange> movedFolders = [];
        private readonly HashSet<string> resourceDirectoryRemovals = new(StringComparer.OrdinalIgnoreCase);
        private LibraryStorageRowPathNotificationPolicy storageRowPathNotificationPolicy =
            LibraryStorageRowPathNotificationPolicy.Suppressed;
        private Exception physicalFailure;
        private LibraryMutationSessionTarget failedTarget;
        private IReadOnlyList<LibraryMutationSessionTarget> unprocessedTargets = [];
        private bool committed;

        /// <summary>Creates an open operation-scoped mutation session owned by one outer lease.</summary>
        /// <param name="owner">Library mutation owner that performs the canonical apply.</param>
        /// <param name="mutationCapability">Live capability from the owning outer lease.</param>
        /// <param name="reason">Stable operation diagnostic reason.</param>
        /// <param name="postLeaseNotifications">Command-owned publication collection.</param>
        /// <param name="suppressNormalRefreshNotification">Whether normal refresh is published by a higher operation boundary.</param>
        /// <param name="suppressLr2NormalFolderSync">Whether LR2 normal-folder synchronization is finalized by the caller.</param>
        internal LibraryMutationSession(
            LibraryMutationOwner owner,
            LibraryFileMutationCapability mutationCapability,
            string reason,
            ICollection<Action> postLeaseNotifications,
            bool suppressNormalRefreshNotification,
            bool suppressLr2NormalFolderSync)
        {
            this.owner = owner ?? throw new ArgumentNullException(nameof(owner));
            this.mutationCapability = mutationCapability ?? throw new ArgumentNullException(nameof(mutationCapability));
            this.reason = reason ?? string.Empty;
            this.postLeaseNotifications = postLeaseNotifications ?? throw new ArgumentNullException(nameof(postLeaseNotifications));
            this.suppressNormalRefreshNotification = suppressNormalRefreshNotification;
            this.suppressLr2NormalFolderSync = suppressLr2NormalFolderSync;
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
            if (physicalFailure != null)
            {
                return;
            }
            physicalFailure = failure;
            failedTarget = new LibraryMutationSessionTarget(sourceDirectory, destinationDirectory);
            unprocessedTargets = Array.AsReadOnly((remainingTargets ?? [])
                .Where(target => target != null)
                .ToArray());
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
            if (confirmedTargets.Count == 0
                && !catalogFacts.Any(item => item?.HasChanges == true)
                && !packageReferenceFacts.Any(item => item?.HasChanges == true)
                && movedFolders.Count == 0
                && resourceDirectoryRemovals.Count == 0)
            {
                return CreateReceipt(durableCommit: false);
            }

            LibraryCatalogMutationFacts combinedCatalogFacts = CombineCatalogFacts(catalogFacts);
            LibraryPackageReferenceFacts combinedPackageFacts = CombinePackageReferenceFacts(packageReferenceFacts);
            var sessionNotifications = new List<Action>();
            FileDbMutationCommitResult applyResult = owner.ApplyLibraryMutationFactsForFileMutation(
                combinedCatalogFacts,
                combinedPackageFacts,
                reason,
                mutationCapability,
                sessionNotifications.Add,
                suppressNormalRefreshNotification,
                suppressLr2NormalFolderSync,
                storageRowPathNotificationPolicy);
            if (!applyResult.DurableCommit)
            {
                return CreateReceipt(durableCommit: false, applyFailure: applyResult.Failure);
            }
            if (applyResult.Failure != null)
            {
                return CreateReceipt(durableCommit: true, applyFailure: applyResult.Failure);
            }

            try
            {
                applyResult.DurableFinalizer?.Invoke();
                DirectoryResourceLookupCache.ReverseLookupMutationResult reverseLookupMutation =
                    DirectoryResourceLookupCache.ReverseLookupMutationResult.Empty;
                if (movedFolders.Count > 0)
                {
                    reverseLookupMutation = owner.UpdateMovedFolderReferences(movedFolders).MutationResult;
                }
                if (resourceDirectoryRemovals.Count > 0)
                {
                    reverseLookupMutation = reverseLookupMutation.Combine(
                        owner.resourceIndexOwner
                            .RemoveUnderSourceDirectories(resourceDirectoryRemovals)
                            .MutationResult);
                }
                foreach (Action notification in sessionNotifications)
                {
                    postLeaseNotifications.Add(notification);
                }
                postLeaseNotifications.Add(() => owner.LogReverseLookupMutationAndQueueWarmupIfNeeded(
                    reason,
                    reverseLookupMutation));
                return CreateReceipt(durableCommit: true);
            }
            catch (Exception exception)
            {
                return CreateReceipt(durableCommit: true, finalizationFailure: exception);
            }
        }

        private LibraryMutationSessionReceipt CreateReceipt(
            bool durableCommit,
            Exception applyFailure = null,
            Exception finalizationFailure = null)
        {
            return new LibraryMutationSessionReceipt(
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
                resourceDirectoryRemovalCount: resourceDirectoryRemovals.Count,
                itemFailures: itemFailures);
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
