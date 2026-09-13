using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using BeMusicSeeker.Models.Utils;

namespace BeMusicSeeker.Models.BmsLibraryInternal;

internal static class LibraryFolderMoveCoordinator
{
    internal static void RenameChartFolder(
        LibraryFileOperationOwner host,
        string srcDir,
        string newName,
        bool? unregister,
        bool renameRootFolder)
    {
        RenameChartFolderWithReceipt(host, srcDir, newName, unregister, renameRootFolder);
    }

    /// <summary>Runs the same mutation while allowing a receipt-aware terminal to own failure reporting.</summary>
    internal static FileDbMutationReceipt RenameChartFolderWithReceipt(
        LibraryFileOperationOwner host,
        string srcDir,
        string newName,
        bool? unregister,
        bool renameRootFolder,
        bool reportAtTerminal = false)
    {
        if (srcDir == null)
        {
            throw new ArgumentNullException(nameof(srcDir));
        }
        if (newName == null)
        {
            throw new ArgumentNullException(nameof(newName));
        }
        if (!renameRootFolder && host.IsLibraryRootFolder(srcDir))
        {
            host.ShowCannotRenameRootFolder(srcDir);
            return null;
        }
        newName = host.NormalizeAutoRenameFolderName(newName);
        if (string.IsNullOrWhiteSpace(newName) || Path.GetPathRoot(srcDir).Equals(srcDir, StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }
        if (!host.DirectoryExists(srcDir))
        {
            host.ShowRenameFolderNotExists(srcDir);
            return null;
        }
        string dstDir = Path.Combine(Path.GetDirectoryName(srcDir), newName);
        if (host.EntryExists(dstDir))
        {
            host.ShowMoveDestinationAlreadyExists(srcDir, dstDir);
            return null;
        }
        FileDbMutationReceipt receipt = null;
        List<Action> postLeaseNotifications = [];
        try
        {
            host.RunWithFolderMoveWriteLocks(mutationCapability =>
            {
                receipt = MoveLibraryChartFolder(
                    host,
                    srcDir,
                    dstDir,
                    unregister,
                    notifyStorageRowPathChanges: false,
                    mutationCapability: mutationCapability,
                    postLeaseNotifications: postLeaseNotifications,
                    reportAtTerminal: reportAtTerminal);
            });
        }
        finally
        {
            InvokePostLeaseNotificationsBestEffort(postLeaseNotifications);
        }
        return receipt;
    }

    /// <summary>Executes a folder move under the existing capability; only receipt-backed notifications may be suppressed.</summary>
    internal static FileDbMutationReceipt MoveLibraryChartFolder(
        LibraryFileOperationOwner host,
        string srcDir,
        string dstDir,
        bool? unregister,
        bool notifyStorageRowPathChanges,
        LibraryFileMutationCapability mutationCapability,
        ICollection<Action> postLeaseNotifications,
        bool reportAtTerminal = false)
    {
        ArgumentNullException.ThrowIfNull(mutationCapability);
        ArgumentNullException.ThrowIfNull(postLeaseNotifications);
        return TryMoveLibraryChartFolder(
            host,
            srcDir,
            dstDir,
            unregister,
            notifyStorageRowPathChanges,
            mutationCapability,
            postLeaseNotifications,
            reportAtTerminal);
    }

    internal static void MoveLibraryRootFolder(
        LibraryFileOperationOwner host,
        IEnumerable<LibraryChartRef> charts,
        string dstDir,
        bool? unregister)
    {
        MoveLibraryRootFolderWithReceipt(host, charts, dstDir, unregister);
    }

    /// <summary>Preserves batch mutation and stopping rules while transferring receipt notification ownership when requested.</summary>
    internal static FileDbMutationBatchReceipt MoveLibraryRootFolderWithReceipt(
        LibraryFileOperationOwner host,
        IEnumerable<LibraryChartRef> charts,
        string dstDir,
        bool? unregister,
        bool reportAtTerminal = false)
    {
        if (charts == null)
        {
            throw new ArgumentNullException(nameof(charts));
        }
        if (dstDir == null)
        {
            throw new ArgumentNullException(nameof(dstDir));
        }
        List<FileDbMutationReceipt> receipts = [];
        List<Action> postLeaseNotifications = [];
        try
        {
            host.RunWithFolderMoveWriteLocks(mutationCapability =>
            {
                if (!host.DirectoryExists(dstDir))
                {
                    postLeaseNotifications.Add(() => host.ShowMoveDestinationRootNotFound(dstDir));
                    return;
                }
                List<LibraryChartRef> chartRefs = host.CreateNonNullChartRefList(charts);
                List<ChartFile> chartSnapshots = [.. chartRefs
                    .Select(chart => chart?.ToChartFile())
                    .Where(chart => chart != null)];
                List<FolderAutoRenamePlan> plans = null;
                host.RunWithFolderMoveSnapshotLocks(
                    () => plans = host.BuildRootFolderMovePlans(chartSnapshots, dstDir));
                if (ContainsDriveRootSource(chartRefs))
                {
                    postLeaseNotifications.Add(host.ShowDriveRootCannotChangeRoot);
                }
                foreach (FolderAutoRenamePlan plan in plans)
                {
                    FileDbMutationReceipt receipt = MoveLibraryChartFolder(
                        host,
                        plan.SourceDirectory,
                        plan.DestinationDirectory,
                        unregister,
                        notifyStorageRowPathChanges: true,
                        mutationCapability: mutationCapability,
                        postLeaseNotifications: postLeaseNotifications,
                        reportAtTerminal: reportAtTerminal);
                    if (receipt != null)
                    {
                        receipts.Add(receipt);
                    }
                    if (receipt?.TerminalState == FileDbMutationTerminalState.ManualRecoveryRequired
                        || receipt?.TerminalState == FileDbMutationTerminalState.DurableFinalizationFailed)
                    {
                        // A manual-recovery or durable-finalization stop leaves the
                        // current source/backup/staging outcome authoritative.  No
                        // later folder mutation may start in the same batch.
                        break;
                    }
                }
            });
        }
        finally
        {
            InvokePostLeaseNotificationsBestEffort(postLeaseNotifications);
        }
        return new FileDbMutationBatchReceipt(receipts);
    }

    private static bool ContainsDriveRootSource(IEnumerable<LibraryChartRef> charts)
    {
        return (charts ?? [])
            .Select(chart => DirectoryExt.GetDirectoryNameSimple(chart.Path))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Any(f => !string.IsNullOrWhiteSpace(f) && Path.GetPathRoot(f).Equals(f, StringComparison.OrdinalIgnoreCase));
    }

    private static FileDbMutationReceipt TryMoveLibraryChartFolder(
        LibraryFileOperationOwner host,
        string srcDir,
        string dstDir,
        bool? unregister,
        bool notifyStorageRowPathChanges,
        LibraryFileMutationCapability mutationCapability,
        ICollection<Action> postLeaseNotifications,
        bool reportAtTerminal)
    {
        ArgumentNullException.ThrowIfNull(mutationCapability);
        ArgumentNullException.ThrowIfNull(postLeaseNotifications);
        if (srcDir.Equals(dstDir, StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }
        if (host.EntryExists(dstDir))
        {
            postLeaseNotifications.Add(() => host.ShowMoveDestinationAlreadyExists(srcDir, dstDir));
            return null;
        }
        try
        {
            FileDbMutationPlan plan = null;
            LibraryFolderMoveFacts mutationFacts = null;
            host.RunWithFolderMoveSnapshotLocks(() =>
            {
                plan = host.BuildFolderMoveMutationPlan(srcDir, dstDir);
                mutationFacts = unregister.HasValue
                    ? host.BuildFolderMoveFacts(
                        srcDir,
                        dstDir,
                        unregister.Value,
                        notifyStorageRowPathChanges)
                    : null;
            });
            FileDbMutationExecutor executor = host.CreateFileDbMutationExecutor(plan);
            DirectoryResourceLookupCache.ReverseLookupMutationResult reverseLookupMutation =
                DirectoryResourceLookupCache.ReverseLookupMutationResult.Empty;
            List<Action> mutationPostLeaseNotifications = [];
            FileDbMutationReceipt receipt = executor.Execute(() =>
            {
                FileDbMutationCommitResult databaseResult = mutationFacts == null
                    ? FileDbMutationCommitResult.Durable()
                    : host.ApplyLibraryMutationFactsForFileMutation(
                        mutationFacts.CatalogFacts,
                        mutationFacts.PackageReferenceFacts,
                        "move_folder",
                        capability: mutationCapability,
                        postLeaseNotificationObserver: mutationPostLeaseNotifications.Add,
                        storageRowPathNotificationPolicy: mutationFacts.StorageRowPathNotificationPolicy);
                if (!databaseResult.DurableCommit)
                {
                    return databaseResult;
                }
                return FileDbMutationCommitResult.Durable(
                    () =>
                    {
                        if (databaseResult.Failure == null)
                        {
                            databaseResult.DurableFinalizer?.Invoke();
                            reverseLookupMutation = host.MoveFolderReferencesAfterCommit(srcDir, dstDir);
                            foreach (Action notification in mutationPostLeaseNotifications)
                            {
                                postLeaseNotifications.Add(notification);
                            }
                        }
                    },
                    databaseResult.Failure);
            });
            if (receipt?.DurableCommit == true
                && receipt.TerminalState != FileDbMutationTerminalState.DurableFinalizationFailed)
            {
                postLeaseNotifications.Add(() => host.LogReverseLookupMutationAndQueueWarmupIfNeeded(
                    "move_folder",
                    reverseLookupMutation));
            }
            if (!receipt.DurableCommit
                || receipt.TerminalState == FileDbMutationTerminalState.DurableFinalizationFailed)
            {
                Action showFailure = () => host.ShowFolderMoveFailed(
                    srcDir,
                    dstDir,
                    receipt.Failure ?? new IOException("Folder move failed."));
                if (!reportAtTerminal)
                    postLeaseNotifications.Add(showFailure);
                return receipt;
            }
            return receipt;
        }
        catch (Exception moveException)
        {
            postLeaseNotifications.Add(() => host.ShowFolderMoveFailed(srcDir, dstDir, moveException));
            return null;
        }
    }

    private static void InvokePostLeaseNotificationsBestEffort(IEnumerable<Action> notifications)
    {
        foreach (Action notification in notifications ?? [])
        {
            try
            {
                notification?.Invoke();
            }
            catch (Exception exception)
            {
                // A dialog or log subscriber is diagnostic-only.  It must not
                // replace a durable filesystem/DB result or trigger retry.
                try
                {
                    Ribbit.Logging.NLogWrapper.FileLogger?.Warn(
                        exception,
                        "file_mutation_post_lease_notification_failed");
                }
                catch
                {
                    // Diagnostics are intentionally best effort.
                }
            }
        }
    }
}
