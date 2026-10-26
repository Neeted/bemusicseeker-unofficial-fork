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

    /// <summary>正規化済みの手動改名対象を停止前に確定します。確定登録本体と空の登録子を含む祖先、従来の導入先保護を実移動へ渡しません。</summary>
    internal static FolderMoveBatch PrepareChartFolderRename(LibraryMutationOwner host, string srcDir, string newName, bool renameRootFolder)
    {
        ArgumentNullException.ThrowIfNull(srcDir);
        ArgumentNullException.ThrowIfNull(newName);
        string normalizedName = host.NormalizeAutoRenameFolderName(newName);
        string parent = Path.GetDirectoryName(srcDir);
        string destination = string.IsNullOrWhiteSpace(normalizedName) || parent == null
            ? null : Path.Combine(parent, normalizedName);
        return new FolderMoveBatch(host.ContainsRegisteredBmsRoot(srcDir) || (!renameRootFolder && host.IsLibraryRootFolder(srcDir))
            ? [] : [new FolderAutoRenamePlan { SourceDirectory = srcDir, DestinationDirectory = destination }]);
    }

    /// <summary>準備時に確定した手動改名の対象を一つの変更セッションへ適用します。直接入口だけはこの受付内で対象を確定します。</summary>
    internal static LibraryMutationSessionReceipt RenameChartFolderWithReceipt(
        LibraryMutationOwner host,
        string srcDir,
        string newName,
        bool? unregister,
        bool renameRootFolder,
        bool reportAtTerminal = false, LibraryFileMutationCapability capability = null, FolderMoveBatch preparedBatch = null)
    {
        if (srcDir == null)
        {
            throw new ArgumentNullException(nameof(srcDir));
        }
        if (newName == null)
        {
            throw new ArgumentNullException(nameof(newName));
        }
        if (host.ContainsRegisteredBmsRoot(srcDir))
        {
            host.ShowCannotMoveRegisteredBmsRoot(srcDir);
            return null;
        }
        if (!renameRootFolder && host.IsLibraryRootFolder(srcDir))
        {
            host.ShowCannotRenameRootFolder(srcDir);
            return null;
        }
        FolderMoveBatch batch = preparedBatch ?? PrepareChartFolderRename(host, srcDir, newName, renameRootFolder);
        string dstDir = batch.Plans.FirstOrDefault()?.DestinationDirectory;
        if (string.IsNullOrWhiteSpace(dstDir) || Path.GetPathRoot(srcDir).Equals(srcDir, StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }
        if (!host.DirectoryExists(srcDir))
        {
            host.ShowRenameFolderNotExists(srcDir);
            return null;
        }
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
                    batch,
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
    /// <param name="preparedBatch">同じ受理操作の診断計画と実移動。直接入口では短いモデル保護で捕捉します。</param>
    internal static LibraryMutationSessionReceipt MoveLibraryRootFolderWithReceipt(
        LibraryMutationOwner host,
        IEnumerable<LibraryChartRef> charts,
        string dstDir,
        bool? unregister,
        bool reportAtTerminal = false, LibraryFileMutationCapability capability = null, FolderMoveBatch preparedBatch = null)
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
                FolderMoveBatch batch = preparedBatch;
                if (batch == null)
                {
                    host.RunWithFolderMoveSnapshotLocks(
                        () => batch = new FolderMoveBatch(host.BuildRootFolderMovePlans(chartSnapshots, dstDir)));
                }
                if (ContainsDriveRootSource(chartRefs))
                {
                    postLeaseNotifications.Add(host.ShowDriveRootCannotChangeRoot);
                }
                receipt = MoveLibraryChartFoldersWithSession(
                    host,
                    batch,
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
        FolderMoveBatch batch,
        bool? unregister,
        bool notifyStorageRowPathChanges,
        LibraryFileMutationCapability mutationCapability,
        ICollection<Action> postLeaseNotifications,
        bool reportAtTerminal)
    {
        ArgumentNullException.ThrowIfNull(mutationCapability);
        ArgumentNullException.ThrowIfNull(postLeaseNotifications);
        ArgumentNullException.ThrowIfNull(batch);
        IReadOnlyList<FolderAutoRenamePlan> movePlans = batch.Moves;
        foreach (FolderAutoRenamePlan plan in batch.Plans.Where(plan => !batch.ContainsPhysicalMove(plan)))
        {
            if (plan.FailureException != null)
            {
                postLeaseNotifications.Add(() => host.ShowFolderMoveFailed(plan.SourceDirectory, plan.DestinationDirectory, plan.FailureException));
                continue;
            }
            if (!string.IsNullOrWhiteSpace(plan.DestinationDirectory)
                && !string.Equals(plan.SourceDirectory, plan.DestinationDirectory, StringComparison.OrdinalIgnoreCase)
                && host.EntryExists(plan.DestinationDirectory))
            {
                postLeaseNotifications.Add(() => host.ShowMoveDestinationAlreadyExists(plan.SourceDirectory, plan.DestinationDirectory));
            }
        }
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
