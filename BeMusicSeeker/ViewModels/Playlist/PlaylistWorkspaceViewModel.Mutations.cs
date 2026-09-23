using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.ExceptionServices;
using System.Threading.Tasks;
using BeMusicSeeker.Models;
using BeMusicSeeker.Models.BmsLibraryInternal;
using BeMusicSeeker.Models.LR2;
using BeMusicSeeker.Models.Utils;
using BeMusicSeeker.Properties;

namespace BeMusicSeeker.ViewModels;

public sealed partial class PlaylistWorkspaceViewModel
{
    private readonly Func<BMSLibrary> getPlaylistLibrary;

    internal event EventHandler<PlaylistWorkspaceMutationRejectedEventArgs> MutationRejected;

    internal event EventHandler<PlaylistOperationNotificationPresentationRequestedEventArgs> PlaylistOperationNotificationPresentationRequested;

    internal event EventHandler PlaylistReferenceSortInvalidationRequested;

    private bool TryEnterPlaylistMutationAdmission(
        BMSPlaylist playlistStore,
        out IDisposable lease)
    {
        if (playlistStore != null)
        {
            if (playlistStore.IsPlaylistUpdating)
            {
                lease = null;
                return false;
            }
            return playlistStore.TryEnterPlaylistMutation(out lease);
        }
        lease = null;
        return false;
    }

    private async Task<IDisposable> WaitForPlaylistMutationAdmissionAsync(BMSPlaylist playlistStore)
    {
        if (playlistStore != null)
        {
            return await playlistStore.WaitForPlaylistMutationAsync().ConfigureAwait(false);
        }
        throw new InvalidOperationException("Playlist persistence is not available.");
    }

    private IDisposable TryBeginPlaylistMutationForOwner(
        BMSPlaylist playlistStore,
        PlaylistWorkspaceMutationKind kind)
    {
        if (TryEnterPlaylistMutationAdmission(playlistStore, out IDisposable lease))
        {
            return lease;
        }
        RaiseMutationRejected(kind, isBusy: true, isStale: false);
        return null;
    }

    private bool TryBeginPlaylistMutation(
        BMSPlaylist playlistStore,
        PlaylistWorkspaceMutationKind kind,
        out IDisposable lease)
    {
        if (TryEnterPlaylistMutationAdmission(playlistStore, out lease))
        {
            return true;
        }
        RaiseMutationRejected(kind, isBusy: true, isStale: false);
        return false;
    }

    private void RaiseMutationRejected(
        PlaylistWorkspaceMutationKind kind,
        bool isBusy,
        bool isStale)
    {
        MutationRejected?.Invoke(
            this,
            new PlaylistWorkspaceMutationRejectedEventArgs(kind, isBusy, isStale));
    }

    private bool TryResolvePlaylistTableForMutation(
        BMSPlaylist playlistStore,
        BMSTable requestedTable,
        PlaylistWorkspaceMutationKind kind,
        out BMSTable activeTable)
    {
        activeTable = playlistStore?.ResolveActivePlaylistTableForMutation(requestedTable);
        if (activeTable != null)
        {
            return true;
        }
        RaiseMutationRejected(kind, isBusy: false, isStale: true);
        return false;
    }

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

        return Task.Run(() => DeleteEntries(entries));
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
        if (!TryBeginPlaylistMutation(
                playlistStore,
                PlaylistWorkspaceMutationKind.RenameFolder,
                out IDisposable admission))
        {
            return;
        }
        using (admission)
        {
            if (!TryResolvePlaylistTableForMutation(
                playlistStore,
                table,
                PlaylistWorkspaceMutationKind.RenameFolder,
                out BMSTable activeTable))
            {
                return;
            }
            if (!CanMutate(activeTable, PlaylistWorkspaceMutationKind.RenameFolder))
            {
                return;
            }
            if (!playlistStore.ContainsPlaylistFolderForMutation(activeTable, oldName))
            {
                RaiseMutationRejected(PlaylistWorkspaceMutationKind.RenameFolder, isBusy: false, isStale: true);
                return;
            }
            RenameFolderAdmitted(playlistStore, activeTable, oldName, newName);
        }
    }

    private void RenameFolderAdmitted(
        BMSPlaylist playlistStore,
        BMSTable activeTable,
        string oldName,
        string newName)
    {
        using PlaylistOperationNotificationOwner.OperationNotificationSession notificationSession = playlistStore.OperationNotificationOwner.BeginSession();
        try
        {
            if (!CanMutate(activeTable, PlaylistWorkspaceMutationKind.RenameFolder)
                || !playlistStore.ContainsBMSTable(activeTable)
                || !playlistStore.ContainsPlaylistFolderForMutation(activeTable, oldName)
                || !playlistStore.RenameFolderBMSTable(activeTable, oldName, newName))
            {
                return;
            }
            RemapCurrentPlaylistDetailFolderSelection(
                activeTable,
                new Dictionary<string, string>(StringComparer.Ordinal)
                {
                    [oldName] = newName ?? string.Empty
                });
            PublishEntriesChanged(activeTable);
        }
        catch (PlaylistMutationPostCommitException)
        {
            if (playlistStore.ContainsBMSTable(activeTable))
            {
                try
                {
                    RemapCurrentPlaylistDetailFolderSelection(
                        activeTable,
                        new Dictionary<string, string>(StringComparer.Ordinal)
                        {
                            [oldName] = newName ?? string.Empty
                        });
                    PublishEntriesChanged(activeTable);
                }
                catch (Exception secondaryException)
                {
                    TryLogPlaylistDropSecondaryFailure(
                        secondaryException,
                        "playlist_rename_post_commit_ui_invalidation_failed");
                }
            }
            throw;
        }
        finally
        {
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
        if (!TryBeginPlaylistMutation(
                playlistStore,
                PlaylistWorkspaceMutationKind.CreateFolder,
                out IDisposable admission))
        {
            return;
        }
        using (admission)
        {
            if (!TryResolvePlaylistTableForMutation(
                    playlistStore,
                    table,
                    PlaylistWorkspaceMutationKind.CreateFolder,
                    out BMSTable activeTable)
                || !CanMutate(activeTable, PlaylistWorkspaceMutationKind.CreateFolder))
            {
                return;
            }
            using PlaylistOperationNotificationOwner.OperationNotificationSession notificationSession = playlistStore.OperationNotificationOwner.BeginSession();
            try
            {
                if (!CanMutate(activeTable, PlaylistWorkspaceMutationKind.CreateFolder)
                    || !playlistStore.ContainsBMSTable(activeTable))
                {
                    return;
                }
                string createdFolder = playlistStore.CreateNewFolderBMSTable(activeTable);
                if (createdFolder != null)
                {
                    PublishEntriesChanged(activeTable);
                }
            }
            catch (PlaylistMutationPostCommitException)
            {
                if (playlistStore.ContainsBMSTable(activeTable))
                {
                    try
                    {
                        PublishEntriesChanged(activeTable);
                    }
                    catch (Exception secondaryException)
                    {
                        TryLogPlaylistDropSecondaryFailure(
                            secondaryException,
                            "playlist_create_post_commit_ui_invalidation_failed");
                    }
                }
                throw;
            }
            finally
            {
                PublishPlaylistOperationNotificationReceipt(notificationSession, "playlist create folder notification");
            }
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
        if (!TryBeginPlaylistMutation(
                playlistStore,
                PlaylistWorkspaceMutationKind.AddEntries,
                out IDisposable admission))
        {
            return;
        }
        using IDisposable admissionScope = admission;
        if (!TryResolvePlaylistTableForMutation(
                playlistStore,
                table,
                PlaylistWorkspaceMutationKind.AddEntries,
                out BMSTable activeTable)
            || !CanMutate(activeTable, PlaylistWorkspaceMutationKind.AddEntries))
        {
            return;
        }
        bool isRootFolderDrop = string.IsNullOrWhiteSpace(folderName);
        if (!isRootFolderDrop
            && !playlistStore.ContainsPlaylistFolderForMutation(activeTable, folderName))
        {
            RaiseMutationRejected(PlaylistWorkspaceMutationKind.AddEntries, isBusy: false, isStale: true);
            return;
        }
        using PlaylistOperationNotificationOwner.OperationNotificationSession notificationSession = playlistStore.OperationNotificationOwner.BeginSession();
        BMSLibrary library = GetPlaylistLibrary();
        List<BMSTableEntry> entriesToRemove = [];
        List<BMSTableEntry> entriesToAdd = [];
        List<PlaylistDropFolderMutation> folderMutations = [];
        List<ChartFile> resolvedCharts = [];
        PlaylistDropMutationResult mutationResult = null;
        ExceptionDispatchInfo primaryFailure = null;
        try
        {
            bool readerLockHeld = false;
            try
            {
                playlistStore.EnsurePlaylistEntriesLoaded(activeTable, "PlaylistWorkspaceViewModel.AddRowsToFolder");
                playlistStore.AcquireReaderLockBMSTables();
                readerLockHeld = true;
                if (!CanMutate(activeTable, PlaylistWorkspaceMutationKind.AddEntries)
                    || !playlistStore.ContainsBMSTable(activeTable))
                {
                    return;
                }
                if (sourceRows.All(GridRowResolver.IsPlaylistRow))
                {
                    List<BMSTableEntry> entriesFromTarget = [.. sourceRows
                        .Select(GridRowResolver.GetPlaylistEntry)
                        .Where(entry => entry != null && entry.parent == activeTable)];
                    List<BMSTableEntry> entriesAlreadyInFolder = [.. entriesFromTarget
                        .Where(entry => string.Equals(entry.folder ?? string.Empty, folderName, StringComparison.Ordinal))];
                    if (activeTable.entry_type == LR2SongDBExtended.playlist.EntryUnitType.Folder
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
                if (activeTable.entry_type == LR2SongDBExtended.playlist.EntryUnitType.Folder
                    && string.IsNullOrWhiteSpace(folderName))
                {
                    entriesToAdd.AddRange(sourceRows
                        .Where(ShouldPreserveEntryForRootFolderDrop)
                        .Select(row => GridRowResolver.GetPlaylistEntry(row)?.Duplicate())
                        .Where(entry => entry != null));
                    folderMutations.AddRange(BuildRootFolderDropMutations(sourceRows, activeTable, library));
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

            mutationResult = playlistStore.ApplyPlaylistDropMutation(
                activeTable,
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
                    library.AddReferenceBMSTablesToCharts(activeTable, resolvedCharts);
                }
                catch (Exception exception)
                {
                    TryLogPlaylistDropSecondaryFailure(
                        exception,
                        "playlist_drop_post_lease_reference_update_failed");
                }
                try
                {
                    PublishEntriesChanged(activeTable);
                }
                catch (Exception exception)
                {
                    TryLogPlaylistDropSecondaryFailure(
                        exception,
                        "playlist_drop_post_lease_ui_invalidation_failed");
                }
            }
        }
        catch (Exception exception)
        {
            primaryFailure ??= ExceptionDispatchInfo.Capture(exception);
        }
        finally
        {
            PublishPlaylistDropOperationNotificationReceipt(notificationSession, primaryFailure);
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

    private void DeleteEntries(IEnumerable<BMSTableEntry> entries)
    {
        if (entries == null)
        {
            throw new ArgumentNullException(nameof(entries));
        }
        List<BMSTableEntry> requestedEntries = [.. entries.Where(entry => entry != null)];
        if (requestedEntries.Count == 0)
        {
            return;
        }
        BMSPlaylist playlistStore = getPlaylistStore();
        List<IGrouping<BMSTable, BMSTableEntry>> requestedGroups = [.. requestedEntries
            .GroupBy(entry => entry.parent)
            .Where(group => group.Key != null)];
        if (playlistStore == null)
        {
            foreach (IGrouping<BMSTable, BMSTableEntry> group in requestedGroups)
            {
                if (CanMutate(group.Key, PlaylistWorkspaceMutationKind.RemoveEntries))
                {
                    // 永続化未構成の local 編集では、従来どおり capability failure を表面化します。
                    // external row はこの必須 store lookup より前に拒否します。
                    GetPlaylistStore();
                }
            }
            return;
        }

        if (!TryBeginPlaylistMutation(
                playlistStore,
                PlaylistWorkspaceMutationKind.RemoveEntries,
                out IDisposable admission))
        {
            return;
        }
        using IDisposable admissionScope = admission;
        Dictionary<BMSTable, List<BMSTableEntry>> entriesByTable = [];
        foreach (BMSTableEntry requestedEntry in requestedEntries)
        {
            if (!playlistStore.TryResolveActivePlaylistEntry(
                    requestedEntry,
                    out BMSTable activeTable,
                    out BMSTableEntry activeEntry))
            {
                RaiseMutationRejected(PlaylistWorkspaceMutationKind.RemoveEntries, isBusy: false, isStale: true);
                continue;
            }
            if (!CanMutate(activeTable, PlaylistWorkspaceMutationKind.RemoveEntries))
            {
                continue;
            }
            if (!entriesByTable.TryGetValue(activeTable, out List<BMSTableEntry> activeEntries))
            {
                activeEntries = [];
                entriesByTable.Add(activeTable, activeEntries);
            }
            if (!activeEntries.Contains(activeEntry))
            {
                activeEntries.Add(activeEntry);
            }
        }
        if (entriesByTable.Count == 0)
        {
            return;
        }
        BMSLibrary library = GetPlaylistLibrary();
        ExceptionDispatchInfo primaryFailure = null;
        foreach (KeyValuePair<BMSTable, List<BMSTableEntry>> group in entriesByTable)
        {
            using PlaylistOperationNotificationOwner.OperationNotificationSession notificationSession =
                playlistStore.OperationNotificationOwner.BeginSession();
            BMSTable activeTable = null;
            List<BMSTableEntry> activeEntries = null;
            try
            {
                activeTable = playlistStore.ResolveActivePlaylistTableForMutation(group.Key);
                if (activeTable == null)
                {
                    RaiseMutationRejected(PlaylistWorkspaceMutationKind.RemoveEntries, isBusy: false, isStale: true);
                    continue;
                }
                activeEntries = [.. group.Value
                    .Select(entry => playlistStore.TryResolveActivePlaylistEntry(
                        entry,
                        out BMSTable resolvedTable,
                        out BMSTableEntry resolvedEntry)
                        && ReferenceEquals(resolvedTable, activeTable)
                            ? resolvedEntry
                            : null)
                    .Where(entry => entry != null)
                    .Distinct()];
                if (activeEntries.Count == 0)
                {
                    RaiseMutationRejected(PlaylistWorkspaceMutationKind.RemoveEntries, isBusy: false, isStale: true);
                    continue;
                }
                if (!CanMutate(activeTable, PlaylistWorkspaceMutationKind.RemoveEntries)
                    || !playlistStore.ContainsBMSTable(activeTable)
                    || !playlistStore.RemoveEntriesBMSTable(activeEntries, activeTable))
                {
                    continue;
                }
                library.RemoveReferenceBMSTables(activeTable, activeEntries);
                PublishEntriesChanged(activeTable);
            }
            catch (PlaylistMutationPostCommitException exception)
            {
                if (activeTable != null && activeEntries != null)
                {
                    try
                    {
                        library.RemoveReferenceBMSTables(activeTable, activeEntries);
                    }
                    catch (Exception secondaryException)
                    {
                        TryLogPlaylistDropSecondaryFailure(
                            secondaryException,
                            "playlist_delete_post_commit_reference_update_failed");
                    }
                    try
                    {
                        PublishEntriesChanged(activeTable);
                    }
                    catch (Exception secondaryException)
                    {
                        TryLogPlaylistDropSecondaryFailure(
                            secondaryException,
                            "playlist_delete_post_commit_ui_invalidation_failed");
                    }
                }
                primaryFailure ??= ExceptionDispatchInfo.Capture(exception);
            }
            catch (Exception exception)
            {
                primaryFailure ??= ExceptionDispatchInfo.Capture(exception);
            }
            finally
            {
                PublishPlaylistOperationNotificationReceipt(notificationSession, "playlist delete entries notification");
            }
        }
        primaryFailure?.Throw();
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
        List<string> workingFolderOrder = [.. (table.Folder_order ?? [])];
        Dictionary<string, List<BMSTableEntry>> workingEntriesByFolder = new(StringComparer.Ordinal);
        foreach (BMSTableEntry entry in table.entries ?? [])
        {
            if (entry == null || entry.is_removed)
            {
                continue;
            }
            string entryFolder = entry.folder ?? string.Empty;
            if (!workingEntriesByFolder.TryGetValue(entryFolder, out List<BMSTableEntry> folderEntries))
            {
                folderEntries = [];
                workingEntriesByFolder.Add(entryFolder, folderEntries);
            }
            folderEntries.Add(entry);
        }
        List<ChartFile> charts = [.. sourceRows
            .Where(row => !ShouldPreserveEntryForRootFolderDrop(row))
            .Select(ResolveDropChart)
            .Where(chart => chart != null)];
        foreach (IGrouping<string, ChartFile> directoryCharts in charts
            .GroupBy(chart => DirectoryExt.GetDirectoryNameSimple(chart.Path), StringComparer.OrdinalIgnoreCase)
            .ToList())
        {
            List<string> orgMd5s = library.GetPlaylistFolderOrgMd5sForCharts(directoryCharts);
            List<string> orderedWorkingFolders = BMSTable.GetSortedFolderList(
                workingEntriesByFolder.Keys,
                workingFolderOrder);
            string targetFolder = null;
            if (orgMd5s.Count > 0)
            {
                targetFolder = orderedWorkingFolders
                    .Where(folder => !string.IsNullOrWhiteSpace(folder))
                    .FirstOrDefault(folder => workingEntriesByFolder.TryGetValue(folder, out List<BMSTableEntry> folderEntries)
                        && folderEntries
                        .Select(entry => entry.md5)
                        .Intersect(orgMd5s, StringComparer.OrdinalIgnoreCase)
                        .Any());
            }
            string newFolderName = null;
            if (string.IsNullOrWhiteSpace(targetFolder))
            {
                newFolderName = BMSTable.ResolveNewFolderName(
                    BMSLibrary.GetLongestCommonChartInfo(directoryCharts.Select(chart => chart.Title)),
                    orderedWorkingFolders);
                targetFolder = newFolderName;
            }
            List<BMSTableEntry> plannedEntries = [.. directoryCharts.Select(chart =>
                BMSTableEntry.CreateForPlaylistDrop(chart, orgMd5s))];
            if (!workingEntriesByFolder.TryGetValue(targetFolder, out List<BMSTableEntry> plannedFolderEntries))
            {
                plannedFolderEntries = [];
                workingEntriesByFolder.Add(targetFolder, plannedFolderEntries);
            }
            plannedFolderEntries.AddRange(plannedEntries);
            mutations.Add(
                new PlaylistDropFolderMutation(
                    string.IsNullOrWhiteSpace(newFolderName) ? targetFolder : null,
                    newFolderName,
                    plannedEntries));
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
        RaiseMutationRejected(kind, isBusy: false, isStale: false);
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

    /// <summary>バックグラウンド BMT 処理の確定した失敗を、元の操作 session と独立して提示します。</summary>
    internal void ReportBmtOutputFailures(IReadOnlyList<BmtTableExportService.FileOperationFailure> failures)
    {
        var receipt = PlaylistOperationNotificationOwner.OperationNotificationReceipt.Create(
            failures.Select(failure => new PlaylistOperationNotificationOwner.OperationNotification(
                string.Format(Resources.Beatoraja_bmt_output_failure_format, failure.Path, failure.Cause),
                Resources.MessageBoxTitle_Warning,
                PlaylistOperationNotificationOwner.OperationNotificationSeverity.Warning)));
        RaiseRequiredEvent(
            PlaylistOperationNotificationPresentationRequested,
            new PlaylistOperationNotificationPresentationRequestedEventArgs(receipt, "beatoraja BMT output failure"),
            nameof(PlaylistOperationNotificationPresentationRequested));
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

    private void PublishPlaylistDropOperationNotificationReceipt(
        PlaylistOperationNotificationOwner.OperationNotificationSession session,
        ExceptionDispatchInfo primaryFailure)
    {
        try
        {
            Exception primaryException = primaryFailure?.SourceException;
            if (primaryException is OperationCanceledException)
            {
                session.TakeReceipt();
                return;
            }

            PlaylistOperationNotificationOwner.OperationNotificationReceipt receipt = session.TakeReceipt();
            if (primaryException != null
                && !receipt.Notifications.Any(notification =>
                    notification.Severity is PlaylistOperationNotificationOwner.OperationNotificationSeverity.Warning
                        or PlaylistOperationNotificationOwner.OperationNotificationSeverity.Error))
            {
                session.Add(
                    new PlaylistOperationNotificationOwner.OperationNotification(
                        Resources.Msg_error_unexpected
                            + Environment.NewLine
                            + primaryException.Message,
                        Resources.Error,
                        PlaylistOperationNotificationOwner.OperationNotificationSeverity.Error));
                receipt = session.TakeReceipt();
            }

            RaiseRequiredEvent(
                PlaylistOperationNotificationPresentationRequested,
                new PlaylistOperationNotificationPresentationRequestedEventArgs(
                    receipt,
                    "playlist drop custom folder output notification"),
                nameof(PlaylistOperationNotificationPresentationRequested));
        }
        catch (Exception exception)
        {
            TryLogPlaylistDropSecondaryFailure(
                exception,
                "playlist_drop_post_lease_notification_failed");
        }
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

/// <summary>編集が未実行となった場合に、画面へ通知する操作の種類です。</summary>
internal enum PlaylistWorkspaceMutationKind
{
    RenameFolder,
    RemoveFolder,
    CreateFolder,
    AddEntries,
    RemoveEntries,
    /// <summary>手動のプレイリスト再取得です。</summary>
    Reload
}

/// <summary>編集を実行しなかった理由を、画面の通知へ渡します。</summary>
internal sealed class PlaylistWorkspaceMutationRejectedEventArgs : EventArgs
{
    /// <summary>操作種別と、競合または対象消失による未実行の理由を保持します。</summary>
    internal PlaylistWorkspaceMutationRejectedEventArgs(
        PlaylistWorkspaceMutationKind kind,
        bool isBusy = false,
        bool isStale = false)
    {
        Kind = kind;
        IsBusy = isBusy;
        IsStale = isStale;
    }

    /// <summary>実行しなかった操作の種類です。</summary>
    internal PlaylistWorkspaceMutationKind Kind { get; }

    /// <summary>先行操作との競合により、待機せず未実行となったかを示します。</summary>
    internal bool IsBusy { get; }

    /// <summary>現在の対象を一意に解決できず、未実行となったかを示します。</summary>
    internal bool IsStale { get; }
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
