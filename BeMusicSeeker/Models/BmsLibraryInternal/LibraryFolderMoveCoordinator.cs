using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using BeMusicSeeker.Models.Utils;

namespace BeMusicSeeker.Models.BmsLibraryInternal;

internal static class LibraryFolderMoveCoordinator
{
    internal static void RenameChartFolder(
        LibraryMutationOwner host,
        string srcDir,
        string newName,
        bool? unregister,
        bool renameRootFolder)
    {
        RenameChartFolderWithReceipt(host, srcDir, newName, unregister, renameRootFolder);
    }

    /// <summary>Runs one manual rename through the operation-scoped folder mutation session.</summary>
    internal static LibraryMutationSessionReceipt RenameChartFolderWithReceipt(
        LibraryMutationOwner host,
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

        LibraryMutationSessionReceipt receipt = null;
        List<Action> postLeaseNotifications = [];
        try
        {
            host.RunWithFolderMoveWriteLocks(mutationCapability =>
            {
                receipt = MoveLibraryChartFoldersWithSession(
                    host,
                    [new FolderAutoRenamePlan { SourceDirectory = srcDir, DestinationDirectory = dstDir }],
                    unregister,
                    notifyStorageRowPathChanges: false,
                    mutationCapability,
                    postLeaseNotifications,
                    reportAtTerminal);
            });
        }
        finally
        {
            InvokePostLeaseNotificationsBestEffort(postLeaseNotifications);
        }
        return receipt;
    }

    internal static void MoveLibraryRootFolder(
        LibraryMutationOwner host,
        IEnumerable<LibraryChartRef> charts,
        string dstDir,
        bool? unregister)
    {
        MoveLibraryRootFolderWithReceipt(host, charts, dstDir, unregister);
    }

    /// <summary>Moves all accepted root folders through one operation-scoped mutation session.</summary>
    internal static LibraryMutationSessionReceipt MoveLibraryRootFolderWithReceipt(
        LibraryMutationOwner host,
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

        LibraryMutationSessionReceipt receipt = LibraryMutationSessionReceipt.Empty;
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
                receipt = MoveLibraryChartFoldersWithSession(
                    host,
                    plans,
                    unregister,
                    notifyStorageRowPathChanges: true,
                    mutationCapability,
                    postLeaseNotifications,
                    reportAtTerminal);
            });
        }
        finally
        {
            InvokePostLeaseNotificationsBestEffort(postLeaseNotifications);
        }
        return receipt;
    }

    private static LibraryMutationSessionReceipt MoveLibraryChartFoldersWithSession(
        LibraryMutationOwner host,
        IEnumerable<FolderAutoRenamePlan> plans,
        bool? unregister,
        bool notifyStorageRowPathChanges,
        LibraryFileMutationCapability mutationCapability,
        ICollection<Action> postLeaseNotifications,
        bool reportAtTerminal)
    {
        ArgumentNullException.ThrowIfNull(mutationCapability);
        ArgumentNullException.ThrowIfNull(postLeaseNotifications);
        List<FolderAutoRenamePlan> movePlans = [.. (plans ?? []).Where(plan => plan != null)];
        LibraryMutationOwner.LibraryMutationSession session = host.BeginLibraryMutationSession(
            mutationCapability,
            "move_folder",
            postLeaseNotifications);

        for (int index = 0; index < movePlans.Count; index++)
        {
            FolderAutoRenamePlan plan = movePlans[index];
            string srcDir = plan.SourceDirectory;
            string dstDir = plan.DestinationDirectory;
            if (string.IsNullOrWhiteSpace(srcDir)
                || string.IsNullOrWhiteSpace(dstDir)
                || srcDir.Equals(dstDir, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }
            if (host.EntryExists(dstDir))
            {
                postLeaseNotifications.Add(() => host.ShowMoveDestinationAlreadyExists(srcDir, dstDir));
                continue;
            }

            try
            {
                LibraryFolderMoveFacts mutationFacts = null;
                host.RunWithFolderMoveSnapshotLocks(() =>
                {
                    mutationFacts = unregister.HasValue
                        ? host.BuildFolderMoveFacts(
                            srcDir,
                            dstDir,
                            unregister.Value,
                            notifyStorageRowPathChanges)
                        : new LibraryFolderMoveFacts(
                            LibraryCatalogMutationFacts.Empty,
                            LibraryPackageReferenceFacts.Empty,
                            LibraryStorageRowPathNotificationPolicy.Suppressed);
                });
                host.MoveFolderPhysical(srcDir, dstDir);
                session.AppendFolderMove(srcDir, dstDir, mutationFacts);
            }
            catch (Exception moveException)
            {
                session.RecordStoppedSuffix(
                    srcDir,
                    dstDir,
                    moveException,
                    movePlans.Skip(index + 1).Select(remaining => new LibraryMutationSessionTarget(
                        remaining.SourceDirectory,
                        remaining.DestinationDirectory)));
                if (!reportAtTerminal)
                {
                    postLeaseNotifications.Add(() => host.ShowFolderMoveFailed(srcDir, dstDir, moveException));
                }
                break;
            }
        }

        LibraryMutationSessionReceipt receipt = session.Commit();
        Exception commitFailure = receipt?.ApplyFailure ?? receipt?.FinalizationFailure;
        if (!reportAtTerminal && commitFailure != null && receipt?.PhysicalFailure == null)
        {
            LibraryMutationSessionTarget target = receipt.ConfirmedTargets.LastOrDefault();
            if (target != null)
            {
                postLeaseNotifications.Add(() => host.ShowFolderMoveFailed(
                    target.SourcePath,
                    target.DestinationPath,
                    commitFailure));
            }
        }
        return receipt;
    }

    private static bool ContainsDriveRootSource(IEnumerable<LibraryChartRef> charts)
    {
        return (charts ?? [])
            .Select(chart => DirectoryExt.GetDirectoryNameSimple(chart.Path))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Any(f => !string.IsNullOrWhiteSpace(f) && Path.GetPathRoot(f).Equals(f, StringComparison.OrdinalIgnoreCase));
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
