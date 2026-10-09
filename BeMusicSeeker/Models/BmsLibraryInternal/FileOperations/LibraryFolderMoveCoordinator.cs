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
        bool reportAtTerminal = false, LibraryFileMutationCapability capability = null)
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
            }, capability);
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

    /// <summary>同ownerの生存L/P権限と停止前に固定した実移動計画を使い、一変更セッションで全対象を実行します。確定事実と失敗を返し、必要公開・cleanup終端まで外側が権限を保持します。</summary>
    /// <param name="preparedPlans">同じ受理操作の準備済みsrc/dst。省略時は従来入口の短いモデル保護で捕捉します。</param>
    internal static LibraryMutationSessionReceipt MoveLibraryRootFolderWithReceipt(
        LibraryMutationOwner host,
        IEnumerable<LibraryChartRef> charts,
        string dstDir,
        bool? unregister,
        bool reportAtTerminal = false, LibraryFileMutationCapability capability = null, IReadOnlyList<FolderAutoRenamePlan> preparedPlans = null)
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
                    .Select(chart => chart?.ToChartFileIdentity())
                    .Where(chart => chart != null)];
                IReadOnlyList<FolderAutoRenamePlan> plans = preparedPlans;
                if (plans == null)
                {
                    host.RunWithFolderMoveSnapshotLocks(
                        () => plans = host.BuildRootFolderMovePlans(chartSnapshots, dstDir));
                }
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
            }, capability);
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
        if (!host.TryEnterManagedOutputMutation(movePlans.SelectMany(plan => new[] { plan.SourceDirectory, plan.DestinationDirectory }),
            recursive: true, out LibraryFileMutationLease playlistLease, mutationCapability)) { return LibraryMutationSessionReceipt.Empty; }
        using LibraryFileMutationLease playlistOperation = playlistLease;
        using LibraryFileMutationCapability playlistCapability = playlistLease?.CreateMutationCapability();
        using LibraryFileMutationCapability combinedCapability = mutationCapability.WithPlaylistCapability(playlistCapability);
        using LibraryMutationOwner.LibraryMutationSession session = host.BeginLibraryMutationSession(
            combinedCapability,
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
