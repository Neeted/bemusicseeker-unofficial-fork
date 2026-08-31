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
        FileDbMutationReceipt receipt = null;
        host.RunWithFolderMoveWriteLocks(() =>
        {
            string dstDir = Path.Combine(Path.GetDirectoryName(srcDir), newName);
            receipt = MoveLibraryChartFolder(
                host,
                srcDir,
                dstDir,
                unregister,
                notifyStorageRowPathChanges: false);
            if (receipt?.DurableCommit == true && unregister == false)
            {
                host.InvalidateDuplicateChartGroupsCache();
            }
        });
        return receipt;
    }

    internal static FileDbMutationReceipt MoveLibraryChartFolder(
        LibraryFileOperationOwner host,
        string srcDir,
        string dstDir,
        bool? unregister,
        bool notifyStorageRowPathChanges)
    {
        return TryMoveLibraryChartFolder(
            host,
            srcDir,
            dstDir,
            unregister,
            notifyStorageRowPathChanges);
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
        host.RunWithFolderMoveWriteLocks(() =>
        {
            if (!host.DirectoryExists(dstDir))
            {
                host.ShowMoveDestinationRootNotFound(dstDir);
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
                host.ShowDriveRootCannotChangeRoot();
            }
            foreach (FolderAutoRenamePlan plan in plans)
            {
                FileDbMutationReceipt receipt = MoveLibraryChartFolder(
                    host,
                    plan.SourceDirectory,
                    plan.DestinationDirectory,
                    unregister,
                    notifyStorageRowPathChanges: true);
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
        bool notifyStorageRowPathChanges)
    {
        if (srcDir.Equals(dstDir, StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }
        if (host.EntryExists(dstDir))
        {
            host.ShowMoveDestinationAlreadyExists(srcDir, dstDir);
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
                    : host.ApplyLibraryMutationDeltaForFileMutation(delta, "move_folder");
                if (!databaseResult.DurableCommit)
                {
                    return databaseResult;
                }
                return FileDbMutationCommitResult.Durable(() =>
                {
                    try
                    {
                        reverseLookupMutation = host.MoveFolderReferencesAfterCommit(srcDir, dstDir);
                        host.LogReverseLookupMutationAndQueueWarmupIfNeeded(
                            "move_folder",
                            reverseLookupMutation);
                    }
                    finally
                    {
                        // The catalog owner has already crossed the durable
                        // point.  Its deferred notification must still run if
                        // the resource-index publication faults.
                        databaseResult.PostCommit?.Invoke();
                    }
                }, databaseResult.Failure);
            });
            if (!receipt.DurableCommit)
            {
                host.ShowFolderMoveFailed(srcDir, dstDir, receipt.Failure ?? new IOException("Folder move failed."));
                return receipt;
            }
            return receipt;
        }
        catch (Exception moveException)
        {
            host.ShowFolderMoveFailed(srcDir, dstDir, moveException);
            return null;
        }
    }
}
