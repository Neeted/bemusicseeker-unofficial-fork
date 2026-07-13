using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using BeMusicSeeker.Models;
using BeMusicSeeker.Models.LR2;
using BeMusicSeeker.Models.Utils;

namespace BeMusicSeeker.ViewModels;

public sealed partial class PlaylistWorkspaceViewModel
{
    private Func<BMSLibrary> getPlaylistLibrary;

    private Action<Action, string> runPlaylistOperationWithNotifications;

    internal event EventHandler<PlaylistWorkspaceMutationRejectedEventArgs> MutationRejected;

    internal event EventHandler<PlaylistWorkspaceEntriesChangedEventArgs> EntriesChanged;

    internal void ConfigureMutations(
        Func<BMSLibrary> playlistLibrary,
        Action<Action, string> operationWithNotifications)
    {
        getPlaylistLibrary = playlistLibrary ?? throw new ArgumentNullException(nameof(playlistLibrary));
        runPlaylistOperationWithNotifications = operationWithNotifications
            ?? throw new ArgumentNullException(nameof(operationWithNotifications));
    }

    internal Task RenameFolderAsync(BMSTable table, PlaylistFolderNode folder, string newName)
    {
        return Task.Run(() => RenameFolder(table, folder, newName));
    }

    internal Task RemoveFolderAsync(BMSTable table, PlaylistFolderNode folder)
    {
        return Task.Run(() => RemoveFolder(table, folder));
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

    internal Task DeleteEntriesAsync(IEnumerable<BMSTableEntry> entries, BMSTable table)
    {
        return Task.Run(() => DeleteEntries(entries, table));
    }

    private void RenameFolder(BMSTable table, PlaylistFolderNode folder, string newName)
    {
        if (folder?.IsEditable != true
            || !CanMutate(table, PlaylistWorkspaceMutationKind.RenameFolder))
        {
            return;
        }
        GetPlaylistStore().RenameFolderBMSTable(table, folder.FolderName, newName);
    }

    private void RemoveFolder(BMSTable table, PlaylistFolderNode folder)
    {
        if (folder?.IsEditable != true
            || !CanMutate(table, PlaylistWorkspaceMutationKind.RemoveFolder))
        {
            return;
        }
        GetPlaylistStore().RemoveFolderBMSTable(table, folder.FolderName);
    }

    private void CreateFolder(BMSTable table)
    {
        if (!CanMutate(table, PlaylistWorkspaceMutationKind.CreateFolder))
        {
            return;
        }
        GetPlaylistStore().CreateNewFolderBMSTable(table);
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
        playlistStore.EnsurePlaylistEntriesLoaded(table, "PlaylistWorkspaceViewModel.AddRowsToFolder");
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
                playlistStore.RemoveEntriesBMSTable(entriesFromTarget, table, commitFlag: false);
            }
            else
            {
                sourceRows = [.. sourceRows
                    .Where(row => !entriesAlreadyInFolder.Contains(GridRowResolver.GetPlaylistEntry(row)))];
                if (sourceRows.Count == 0)
                {
                    return;
                }
                playlistStore.RemoveEntriesBMSTable(
                    entriesFromTarget.Except(entriesAlreadyInFolder),
                    table,
                    commitFlag: false);
            }
        }

        List<ChartFile> resolvedCharts = [.. sourceRows
            .Select(ResolveDropChart)
            .Where(chart => chart != null)];
        if (table.entry_type == LR2SongDBExtended.playlist.EntryUnitType.Folder
            && string.IsNullOrWhiteSpace(folderName))
        {
            AddRowsToRootFolder(sourceRows, table, playlistStore, library);
        }
        else
        {
            ParallelQuery<BMSTableEntry> entries = from row in sourceRows.AsParallel()
                                                   let entry = GridRowResolver.GetPlaylistEntry(row)
                                                   let chart = ResolveDropChart(row)
                                                   where entry != null || chart != null
                                                   select entry != null
                                                       ? entry.Duplicate()
                                                       : BMSTableEntry.CreateForPlaylistDrop(
                                                           chart,
                                                           library.GetPlaylistOrgMd5sForChart(chart));
            playlistStore.AddPlaylistEntriesToFolderBMSTable(entries, table, folderName, commitFlag: false);
        }

        RunWithNotifications(
            () => playlistStore.ReOutputCustomFolderAndCommitToDB(table),
            "playlist drop custom folder output notification");
        playlistStore.AcquireReaderLockBMSTables();
        try
        {
            library.AddReferenceBMSTablesToCharts(table, resolvedCharts);
        }
        finally
        {
            playlistStore.FreeReaderLockBMSTables();
        }
        PublishEntriesChanged(table);
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
        playlistStore.RemoveEntriesBMSTable(entryList, table);
        playlistStore.AcquireReaderLockBMSTables();
        try
        {
            library.RemoveReferenceBMSTables(table, entryList);
        }
        finally
        {
            playlistStore.FreeReaderLockBMSTables();
        }
        PublishEntriesChanged(table);
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

    private void AddRowsToRootFolder(
        IReadOnlyCollection<object> sourceRows,
        BMSTable table,
        BMSPlaylist playlistStore,
        BMSLibrary library)
    {
        List<object> preservedEntryRows = [.. sourceRows.Where(ShouldPreserveEntryForRootFolderDrop)];
        List<ChartFile> charts = [.. sourceRows
            .Except(preservedEntryRows)
            .Select(ResolveDropChart)
            .Where(chart => chart != null)];
        playlistStore.AddPlaylistEntriesToFolderBMSTable(
            preservedEntryRows
                .Select(row => GridRowResolver.GetPlaylistEntry(row)?.Duplicate())
                .Where(entry => entry != null),
            table,
            string.Empty,
            commitFlag: false);
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
            if (string.IsNullOrWhiteSpace(targetFolder))
            {
                targetFolder = BMSLibrary.GetLongestCommonChartInfo(directoryCharts.Select(chart => chart.Title));
                targetFolder = playlistStore.CreateNewFolderBMSTable(table, targetFolder, commitFlag: false);
            }
            playlistStore.AddPlaylistEntriesToFolderBMSTable(
                directoryCharts.Select(chart => BMSTableEntry.CreateForPlaylistDrop(chart, orgMd5s)),
                table,
                targetFolder,
                commitFlag: false);
        }
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
        return getPlaylistStore?.Invoke()
            ?? throw new InvalidOperationException("Playlist persistence is not available.");
    }

    private BMSLibrary GetPlaylistLibrary()
    {
        return getPlaylistLibrary?.Invoke()
            ?? throw new InvalidOperationException("Playlist library is not available.");
    }

    private void RunWithNotifications(Action operation, string routeName)
    {
        Action<Action, string> runner = runPlaylistOperationWithNotifications
            ?? throw new InvalidOperationException("Playlist operation notification routing is not available.");
        runner(operation, routeName);
    }

    private void PublishEntriesChanged(BMSTable table)
    {
        EntriesChanged?.Invoke(this, new PlaylistWorkspaceEntriesChangedEventArgs(table));
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

internal sealed class PlaylistWorkspaceEntriesChangedEventArgs : EventArgs
{
    internal PlaylistWorkspaceEntriesChangedEventArgs(BMSTable table)
    {
        Table = table ?? throw new ArgumentNullException(nameof(table));
    }

    internal BMSTable Table { get; }
}
