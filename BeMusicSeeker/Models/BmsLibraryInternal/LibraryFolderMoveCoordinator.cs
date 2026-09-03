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

    internal static FileDbMutationReceipt RenameChartFolderWithReceipt(
        LibraryFileOperationOwner host,
        string srcDir,
        string newName,
        bool? unregister,
        bool renameRootFolder)
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
                    postLeaseNotifications: postLeaseNotifications);
            });
        }
        finally
        {
            InvokePostLeaseNotificationsBestEffort(postLeaseNotifications);
        }
        return receipt;
    }

    internal static FileDbMutationReceipt MoveLibraryChartFolder(
        LibraryFileOperationOwner host,
        string srcDir,
        string dstDir,
        bool? unregister,
        bool notifyStorageRowPathChanges,
        LibraryFileMutationCapability mutationCapability,
        ICollection<Action> postLeaseNotifications)
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
            postLeaseNotifications);
    }

    internal static void MoveLibraryRootFolder(
        LibraryFileOperationOwner host,
        IEnumerable<LibraryChartRef> charts,
        string dstDir,
        bool? unregister)
    {
        MoveLibraryRootFolderWithReceipt(host, charts, dstDir, unregister);
    }

    internal static FileDbMutationBatchReceipt MoveLibraryRootFolderWithReceipt(
        LibraryFileOperationOwner host,
        IEnumerable<LibraryChartRef> charts,
        string dstDir,
        bool? unregister)
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
                        postLeaseNotifications: postLeaseNotifications);
                    if (receipt != null)
                    {
                        receipts.Add(receipt);
                    }
                    if (receipt?.TerminalState == FileDbMutationTerminalState.ManualRecoveryRequired)
                    {
                        // The first compensation failure leaves an uncertain
                        // source/backup/staging tree.  No later folder mutation may
                        // start until that tree is recovered manually.
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
        ICollection<Action> postLeaseNotifications)
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
            LibraryMutationDelta delta = null;
            host.RunWithFolderMoveSnapshotLocks(() =>
            {
                plan = host.BuildFolderMoveMutationPlan(srcDir, dstDir);
                delta = unregister.HasValue
                    ? host.BuildFolderMoveDelta(
                        srcDir,
                        dstDir,
                        unregister.Value,
                        notifyStorageRowPathChanges)
                    : null;
            });
            FileDbMutationExecutor executor = host.CreateFileDbMutationExecutor(plan);
            DirectoryResourceLookupCache.ReverseLookupMutationResult reverseLookupMutation =
                DirectoryResourceLookupCache.ReverseLookupMutationResult.Empty;
            FileDbMutationReceipt receipt = executor.Execute(() =>
            {
                FileDbMutationCommitResult databaseResult = delta == null
                    ? FileDbMutationCommitResult.Durable()
                    : host.ApplyLibraryMutationDeltaForFileMutation(
                        delta,
                        "move_folder",
                        capability: mutationCapability,
                        postLeaseNotificationObserver: postLeaseNotifications.Add);
                if (!databaseResult.DurableCommit)
                {
                    return databaseResult;
                }
                return FileDbMutationCommitResult.Durable(
                    () =>
                    {
                        databaseResult.DurableFinalizer?.Invoke();
                        reverseLookupMutation = host.MoveFolderReferencesAfterCommit(srcDir, dstDir);
                        if (unregister == false)
                        {
                            host.InvalidateDuplicateChartGroupsCache();
                        }
                    },
                    databaseResult.Failure);
            });
            if (receipt?.DurableCommit == true)
            {
                postLeaseNotifications.Add(() => host.LogReverseLookupMutationAndQueueWarmupIfNeeded(
                    "move_folder",
                    reverseLookupMutation));
            }
            if (!receipt.DurableCommit)
            {
                Action showFailure = () => host.ShowFolderMoveFailed(
                    srcDir,
                    dstDir,
                    receipt.Failure ?? new IOException("Folder move failed."));
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
