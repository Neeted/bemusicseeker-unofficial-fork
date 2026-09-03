using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.ExceptionServices;
using System.Threading.Tasks;
using BeMusicSeeker.Models;
using BeMusicSeeker.Models.BmsLibraryInternal;
using BeMusicSeeker.Models.LR2;
using BeMusicSeeker.Models.Utils;

namespace BeMusicSeeker.ViewModels;

public sealed partial class PlaylistWorkspaceViewModel
{
    private readonly Func<BMSLibrary> getPlaylistLibrary;

    internal event EventHandler<PlaylistWorkspaceMutationRejectedEventArgs> MutationRejected;

    internal event EventHandler<PlaylistOperationNotificationPresentationRequestedEventArgs> PlaylistOperationNotificationPresentationRequested;

    internal event EventHandler PlaylistReferenceSortInvalidationRequested;

    internal Task<BMSTable> CreatePlaylistAsync()
    {
        return Task.Run(CreatePlaylist);
    }

    internal BMSTable CreatePlaylist()
    {
        return GetPlaylistStore().CreateBMSTable();
    }

    internal Task RenameFolderAsync(BMSTable table, PlaylistFolderNode folder, string newName)
    {
        return Task.Run(() => RenameFolder(table, folder, newName));
    }

    internal Task CreateFolderAsync(BMSTable table)
    {
        return Task.Run(() => CreateFolder(table));
    }

    internal Task AddRowsToFolderAsync(
        IEnumerable<object> rows,
        BMSTable table,
        PlaylistFolderNode targetFolder = null)
    {
        return Task.Run(() => AddRowsToFolder(rows, table, targetFolder));
    }

    internal Task DeleteSelectedEntriesAsync(IEnumerable<object> selectedRows)
    {
        if (selectedRows == null)
        {
            throw new ArgumentNullException(nameof(selectedRows));
        }

        List<BMSTableEntry> entries = [.. selectedRows
            .Where(row => row != null)
            .Select(GridRowResolver.GetPlaylistEntry)
            .Where(entry => entry != null)];
        if (entries.Count == 0)
        {
            return Task.CompletedTask;
        }

        Task[] deleteTasks = [.. entries
            .GroupBy(entry => entry.parent)
            .Select(group => Task.Run(() => DeleteEntries(group, group.Key)))];
        return Task.WhenAll(deleteTasks);
    }

    private void RenameFolder(BMSTable table, PlaylistFolderNode folder, string newName)
    {
        if (folder?.IsEditable != true
            || !CanMutate(table, PlaylistWorkspaceMutationKind.RenameFolder))
        {
            return;
        }
        string oldName = folder.FolderName;
        BMSPlaylist playlistStore = GetPlaylistStore();
        using PlaylistOperationNotificationOwner.OperationNotificationSession notificationSession = playlistStore.OperationNotificationOwner.BeginSession();
        playlistStore.AcquireReaderLockBMSTables();
        try
        {
            if (!CanMutate(table, PlaylistWorkspaceMutationKind.RenameFolder)
                || !playlistStore.ContainsBMSTable(table)
                || !playlistStore.RenameFolderBMSTable(table, oldName, newName))
            {
                return;
            }
            RemapCurrentPlaylistDetailFolderSelection(
                table,
                new Dictionary<string, string>(StringComparer.Ordinal)
                {
                    [oldName] = newName ?? string.Empty
                });
            PublishEntriesChanged(table);
        }
        finally
        {
            playlistStore.FreeReaderLockBMSTables();
            PublishPlaylistOperationNotificationReceipt(notificationSession, "playlist rename folder notification");
        }
    }

    private void CreateFolder(BMSTable table)
    {
        if (!CanMutate(table, PlaylistWorkspaceMutationKind.CreateFolder))
        {
            return;
        }
        BMSPlaylist playlistStore = GetPlaylistStore();
        using PlaylistOperationNotificationOwner.OperationNotificationSession notificationSession = playlistStore.OperationNotificationOwner.BeginSession();
        playlistStore.AcquireReaderLockBMSTables();
        try
        {
            if (!CanMutate(table, PlaylistWorkspaceMutationKind.CreateFolder)
                || !playlistStore.ContainsBMSTable(table))
            {
                return;
            }
            string createdFolder = playlistStore.CreateNewFolderBMSTable(table);
            if (createdFolder != null)
            {
                PublishEntriesChanged(table);
            }
        }
        finally
        {
            playlistStore.FreeReaderLockBMSTables();
            PublishPlaylistOperationNotificationReceipt(notificationSession, "playlist create folder notification");
        }
    }

    internal bool CanAcceptDrop(
        IEnumerable<object> rows,
        BMSTable table,
        PlaylistFolderNode targetFolder = null)
    {
        return table?.is_external_sync == false
            && targetFolder?.IsSpecial != true
            && AreDropCandidateRows(rows);
    }

    internal PlaylistFolderContextMenuAvailability CapturePlaylistFolderContextMenuAvailability(
        BMSTable table,
        PlaylistFolderNode folder)
    {
        bool canEdit = table != null
            && !table.is_external_sync
            && folder?.IsEditable == true;
        return new PlaylistFolderContextMenuAvailability(canEdit, canEdit);
    }

    private void AddRowsToFolder(
        IEnumerable<object> rows,
        BMSTable table,
        PlaylistFolderNode targetFolder = null)
    {
        if (rows == null)
        {
            throw new ArgumentNullException(nameof(rows));
        }
        if (!CanMutate(table, PlaylistWorkspaceMutationKind.AddEntries))
        {
            return;
        }

        if (targetFolder?.IsSpecial == true)
        {
            return;
        }
        string folderName = targetFolder?.FolderName ?? string.Empty;
        List<object> sourceRows = [.. rows.Where(row => row != null)];
        if (!AreDropCandidateRows(sourceRows))
        {
            return;
        }

        BMSPlaylist playlistStore = GetPlaylistStore();
        BMSLibrary library = GetPlaylistLibrary();
        using PlaylistOperationNotificationOwner.OperationNotificationSession notificationSession = playlistStore.OperationNotificationOwner.BeginSession();
        bool readerLockHeld = false;
        List<BMSTableEntry> entriesToRemove = [];
        List<BMSTableEntry> entriesToAdd = [];
        List<PlaylistDropFolderMutation> folderMutations = [];
        List<ChartFile> resolvedCharts = [];
        try
        {
            playlistStore.EnsurePlaylistEntriesLoaded(table, "PlaylistWorkspaceViewModel.AddRowsToFolder");
            playlistStore.AcquireReaderLockBMSTables();
            readerLockHeld = true;
            if (!CanMutate(table, PlaylistWorkspaceMutationKind.AddEntries)
                || !playlistStore.ContainsBMSTable(table))
            {
                return;
            }
            if (sourceRows.All(GridRowResolver.IsPlaylistRow))
            {
                List<BMSTableEntry> entriesFromTarget = [.. sourceRows
                    .Select(GridRowResolver.GetPlaylistEntry)
                    .Where(entry => entry != null && entry.parent == table)];
                List<BMSTableEntry> entriesAlreadyInFolder = [.. entriesFromTarget
                    .Where(entry => string.Equals(entry.folder ?? string.Empty, folderName, StringComparison.Ordinal))];
                if (table.entry_type == LR2SongDBExtended.playlist.EntryUnitType.Folder
                    && string.IsNullOrWhiteSpace(folderName))
                {
                    entriesToRemove.AddRange(entriesFromTarget);
                }
                else
                {
                    sourceRows = [.. sourceRows
                        .Where(row => !entriesAlreadyInFolder.Contains(GridRowResolver.GetPlaylistEntry(row)))];
                    if (sourceRows.Count == 0)
                    {
                        return;
                    }
                    entriesToRemove.AddRange(entriesFromTarget.Except(entriesAlreadyInFolder));
                }
            }

            resolvedCharts = [.. sourceRows
                .Select(ResolveDropChart)
                .Where(chart => chart != null)];
            if (table.entry_type == LR2SongDBExtended.playlist.EntryUnitType.Folder
                && string.IsNullOrWhiteSpace(folderName))
            {
                entriesToAdd.AddRange(sourceRows
                    .Where(ShouldPreserveEntryForRootFolderDrop)
                    .Select(row => GridRowResolver.GetPlaylistEntry(row)?.Duplicate())
                    .Where(entry => entry != null));
                folderMutations.AddRange(BuildRootFolderDropMutations(sourceRows, table, library));
            }
            else
            {
                entriesToAdd.AddRange(sourceRows
                    .Select(row =>
                    {
                        BMSTableEntry entry = GridRowResolver.GetPlaylistEntry(row);
                        ChartFile chart = ResolveDropChart(row);
                        return entry != null
                            ? entry.Duplicate()
                            : chart != null
                                ? BMSTableEntry.CreateForPlaylistDrop(
                                    chart,
                                    library.GetPlaylistOrgMd5sForChart(chart))
                                : null;
                    })
                    .Where(entry => entry != null));
            }
        }
        finally
        {
            if (readerLockHeld)
            {
                playlistStore.FreeReaderLockBMSTables();
            }
        }

        PlaylistDropMutationResult mutationResult = null;
        ExceptionDispatchInfo primaryFailure = null;
        try
        {
            mutationResult = playlistStore.ApplyPlaylistDropMutation(
                    table,
                    "PlaylistWorkspaceViewModel.AddRowsToFolder",
                    folderName,
                    entriesToRemove,
                    entriesToAdd,
                    folderMutations);
            if (mutationResult.Applied && mutationResult.Durable)
            {
                primaryFailure = mutationResult.PrimaryException;
                try
                {
                    library.AddReferenceBMSTablesToCharts(table, resolvedCharts);
                }
                catch (Exception exception)
                {
                    TryLogPlaylistDropSecondaryFailure(
                        exception,
                        "playlist_drop_post_lease_reference_update_failed");
                }
                try
                {
                    PublishEntriesChanged(table);
                }
                catch (Exception exception)
                {
                    TryLogPlaylistDropSecondaryFailure(
                        exception,
                        "playlist_drop_post_lease_ui_invalidation_failed");
                }
            }
        }
        finally
        {
            try
            {
                PublishPlaylistOperationNotificationReceipt(
                    notificationSession,
                    "playlist drop custom folder output notification");
            }
            catch (Exception exception)
            {
                TryLogPlaylistDropSecondaryFailure(
                    exception,
                    "playlist_drop_post_lease_notification_failed");
            }
        }
        primaryFailure?.Throw();
    }

    private void TryLogPlaylistDropSecondaryFailure(Exception exception, string diagnostic)
    {
        try
        {
            playlistSyncFailureLog(exception, diagnostic);
        }
        catch
        {
            // Secondary diagnostics must not replace the durable operation result.
        }
    }

    private void DeleteEntries(IEnumerable<BMSTableEntry> entries, BMSTable table)
    {
        if (entries == null)
        {
            throw new ArgumentNullException(nameof(entries));
        }
        if (!CanMutate(table, PlaylistWorkspaceMutationKind.RemoveEntries))
        {
            return;
        }

        List<BMSTableEntry> entryList = [.. entries.Where(entry => entry != null)];
        if (entryList.Count == 0)
        {
            return;
        }
        BMSPlaylist playlistStore = GetPlaylistStore();
        BMSLibrary library = GetPlaylistLibrary();
        using PlaylistOperationNotificationOwner.OperationNotificationSession notificationSession = playlistStore.OperationNotificationOwner.BeginSession();
        playlistStore.AcquireReaderLockBMSTables();
        try
        {
            if (!CanMutate(table, PlaylistWorkspaceMutationKind.RemoveEntries)
                || !playlistStore.ContainsBMSTable(table)
                || !playlistStore.RemoveEntriesBMSTable(entryList, table))
            {
                return;
            }
            library.RemoveReferenceBMSTables(table, entryList);
            PublishEntriesChanged(table);
        }
        finally
        {
            playlistStore.FreeReaderLockBMSTables();
            PublishPlaylistOperationNotificationReceipt(notificationSession, "playlist delete entries notification");
        }
    }

    internal static bool AreDropCandidateRows(IEnumerable<object> rows)
    {
        List<object> candidates = rows?.Where(row => row != null).ToList() ?? [];
        return candidates.Count > 0 && candidates.All(IsDropCandidateRow);
    }

    internal static bool IsDropCandidateRow(object row)
    {
        return GridRowResolver.GetPlaylistEntry(row) != null
            || ResolveDropChart(row) != null;
    }

    internal static bool ShouldPreserveEntryForRootFolderDrop(object row)
    {
        return GridRowResolver.GetPlaylistEntry(row) != null
            && ResolveDropChart(row) == null;
    }

    internal static ChartFile ResolveDropChart(object row)
    {
        if (row is PlayHistoryRow playHistoryRow)
        {
            return playHistoryRow.ResolvedChart;
        }
        return GridRowResolver.TryGetChartOperationTarget(row, out ChartOperationTarget target)
            && !target.IsPlaylistMissing
            ? target.Chart
            : null;
    }

    private IReadOnlyCollection<PlaylistDropFolderMutation> BuildRootFolderDropMutations(
        IReadOnlyCollection<object> sourceRows,
        BMSTable table,
        BMSLibrary library)
    {
        List<PlaylistDropFolderMutation> mutations = [];
        List<ChartFile> charts = [.. sourceRows
            .Where(row => !ShouldPreserveEntryForRootFolderDrop(row))
            .Select(ResolveDropChart)
            .Where(chart => chart != null)];
        foreach (IGrouping<string, ChartFile> directoryCharts in charts
            .GroupBy(chart => DirectoryExt.GetDirectoryNameSimple(chart.Path), StringComparer.OrdinalIgnoreCase)
            .ToList())
        {
            List<string> orgMd5s = library.GetPlaylistFolderOrgMd5sForCharts(directoryCharts);
            string targetFolder = null;
            if (orgMd5s.Count > 0)
            {
                targetFolder = table.folder_list
                    .Where(folder => !string.IsNullOrWhiteSpace(folder))
                    .FirstOrDefault(folder => table.entries
                        .Where(entry => entry.folder == folder)
                        .Select(entry => entry.md5)
                        .Intersect(orgMd5s, StringComparer.OrdinalIgnoreCase)
                        .Any());
            }
            string newFolderName = null;
            if (string.IsNullOrWhiteSpace(targetFolder))
            {
                newFolderName = BMSLibrary.GetLongestCommonChartInfo(directoryCharts.Select(chart => chart.Title));
            }
            mutations.Add(
                new PlaylistDropFolderMutation(
                    targetFolder,
                    newFolderName,
                    directoryCharts.Select(chart => BMSTableEntry.CreateForPlaylistDrop(chart, orgMd5s))));
        }
        return mutations;
    }

    private bool CanMutate(BMSTable table, PlaylistWorkspaceMutationKind kind)
    {
        if (table == null)
        {
            throw new ArgumentNullException(nameof(table));
        }
        if (!table.is_external_sync)
        {
            return true;
        }
        MutationRejected?.Invoke(this, new PlaylistWorkspaceMutationRejectedEventArgs(kind));
        return false;
    }

    private BMSPlaylist GetPlaylistStore()
    {
        return getPlaylistStore()
            ?? throw new InvalidOperationException("Playlist persistence is not available.");
    }

    private BMSLibrary GetPlaylistLibrary()
    {
        return getPlaylistLibrary()
            ?? throw new InvalidOperationException("Playlist library is not available.");
    }

    private void RunWithNotifications(Action operation, string routeName)
    {
        if (operation == null)
        {
            return;
        }
        BMSPlaylist store = GetPlaylistStore();
        using PlaylistOperationNotificationOwner.OperationNotificationSession session = store.OperationNotificationOwner.BeginSession();
        try
        {
            operation();
        }
        finally
        {
            PublishPlaylistOperationNotificationReceipt(session, routeName);
        }
    }

    internal async Task RunWithPlaylistOperationNotificationsAsync(Func<Task> operation, string routeName)
    {
        if (operation == null)
        {
            return;
        }
        BMSPlaylist store = GetPlaylistStore();
        using PlaylistOperationNotificationOwner.OperationNotificationSession session = store.OperationNotificationOwner.BeginSession();
        try
        {
            await operation().ConfigureAwait(false);
        }
        finally
        {
            PublishPlaylistOperationNotificationReceipt(session, routeName);
        }
    }

    private void PublishPlaylistOperationNotificationReceipt(
        PlaylistOperationNotificationOwner.OperationNotificationSession session,
        string routeName)
    {
        RaiseRequiredEvent(
            PlaylistOperationNotificationPresentationRequested,
            new PlaylistOperationNotificationPresentationRequestedEventArgs(session.TakeReceipt(), routeName),
            nameof(PlaylistOperationNotificationPresentationRequested));
    }

    private void PublishEntriesChanged(BMSTable table, bool refreshSummaryIfVisible = true)
    {
        bool detailContentChanged = MarkCurrentPlaylistDetailEntriesChanged(table, "playlist_updated");
        try
        {
            if (detailContentChanged
                && IsPlaylistDetailViewActive
                && !IsPlaylistSummaryMode)
            {
                RequestPlaylistDetailReloadRefresh();
            }
            RequestPlaylistReferenceSortInvalidation();
        }
        finally
        {
            if (refreshSummaryIfVisible)
            {
                RequestPlaylistSummaryDataRefresh(
                    "playlist_entries_updated");
            }
        }
    }

    internal void RequestPlaylistReferenceSortInvalidation()
    {
        PlaylistReferenceSortInvalidationRequested?.Invoke(this, EventArgs.Empty);
    }
}

internal enum PlaylistWorkspaceMutationKind
{
    RenameFolder,
    RemoveFolder,
    CreateFolder,
    AddEntries,
    RemoveEntries
}

internal sealed class PlaylistWorkspaceMutationRejectedEventArgs : EventArgs
{
    internal PlaylistWorkspaceMutationRejectedEventArgs(PlaylistWorkspaceMutationKind kind)
    {
        Kind = kind;
    }

    internal PlaylistWorkspaceMutationKind Kind { get; }
}

internal sealed class PlaylistFolderContextMenuAvailability
{
    internal PlaylistFolderContextMenuAvailability(bool canDelete, bool canRename)
    {
        CanDelete = canDelete;
        CanRename = canRename;
    }

    internal bool CanDelete { get; }

    internal bool CanRename { get; }
}

internal sealed class PlaylistOperationNotificationPresentationRequestedEventArgs : EventArgs
{
    internal PlaylistOperationNotificationPresentationRequestedEventArgs(
        PlaylistOperationNotificationOwner.OperationNotificationReceipt receipt,
        string routeName)
    {
        Receipt = receipt ?? throw new ArgumentNullException(nameof(receipt));
        RouteName = routeName;
    }

    internal PlaylistOperationNotificationOwner.OperationNotificationReceipt Receipt { get; }

    internal string RouteName { get; }
}
