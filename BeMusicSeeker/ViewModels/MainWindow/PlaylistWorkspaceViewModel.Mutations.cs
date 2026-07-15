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
    private readonly Func<BMSLibrary> getPlaylistLibrary;

    private readonly Action<BMSPlaylist.OperationNotificationScope, string> presentPlaylistOperationNotifications;

    internal event EventHandler<PlaylistWorkspaceMutationRejectedEventArgs> MutationRejected;

    internal event EventHandler PlaylistReferenceSortInvalidationRequested;

    internal event EventHandler<PlaylistTableRemovalInvalidOutputDirectoryEventArgs> PlaylistTableRemovalInvalidOutputDirectoryRequested;

    internal Task<BMSTable> CreatePlaylistAsync()
    {
        return Task.Run(CreatePlaylist);
    }

    internal BMSTable CreatePlaylist()
    {
        return GetPlaylistStore().CreateBMSTable();
    }

    internal Task ReplaceBmsFileLevelByTableEntryLevelAsync(BMSTable bmsTable)
    {
        return Task.Run(() => GetPlaylistLibrary().ReplaceBmsFileLevelByTableEntryLevel(bmsTable));
    }

    internal Task RenameFolderAsync(BMSTable table, PlaylistFolderNode folder, string newName)
    {
        return Task.Run(() => RenameFolder(table, folder, newName));
    }

    internal Task RemoveFolderAsync(BMSTable table, PlaylistFolderNode folder)
    {
        return Task.Run(() => RemoveFolder(table, folder));
    }

    internal Task RemoveTableAsync(BMSTable table)
    {
        return Task.Run(() => RemoveTable(table));
    }

    internal Task RemoveTablesAsync(IEnumerable<BMSTable> tables)
    {
        if (tables == null)
        {
            return Task.CompletedTask;
        }
        List<BMSTable> requestedTables = [.. tables.Where(table => table != null)];
        return requestedTables.Count == 0
            ? Task.CompletedTask
            : Task.Run(() => RemoveTables(requestedTables));
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
        string oldName = folder.FolderName;
        BMSPlaylist playlistStore = GetPlaylistStore();
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
        }
    }

    private void RemoveFolder(BMSTable table, PlaylistFolderNode folder)
    {
        if (folder?.IsEditable != true
            || !CanMutate(table, PlaylistWorkspaceMutationKind.RemoveFolder))
        {
            return;
        }
        BMSPlaylist playlistStore = GetPlaylistStore();
        string folderName = folder.FolderName;
        playlistStore.AcquireReaderLockBMSTables();
        try
        {
            if (!CanMutate(table, PlaylistWorkspaceMutationKind.RemoveFolder)
                || !playlistStore.ContainsBMSTable(table)
                || !playlistStore.RemoveFolderBMSTable(table, folderName))
            {
                return;
            }
            RemapCurrentPlaylistDetailFolderSelection(
                table,
                new Dictionary<string, string>(StringComparer.Ordinal)
                {
                    [folderName] = string.Empty
                });
            PublishEntriesChanged(table);
        }
        finally
        {
            playlistStore.FreeReaderLockBMSTables();
        }
    }

    private void RemoveTable(BMSTable bmsTable)
    {
        RemoveTables([bmsTable]);
    }

    private void RemoveTables(IReadOnlyList<BMSTable> bmsTables)
    {
        if (bmsTables == null)
        {
            return;
        }

        CustomFolderOutputSettingsSnapshot settings = null;
        bool settingsLoaded = false;
        CustomFolderOutputSettingsSnapshot GetSettingsSnapshot()
        {
            if (!settingsLoaded)
            {
                settings = GetCustomFolderOutputSettings();
                settingsLoaded = true;
            }
            return settings;
        }

        foreach (BMSTable bmsTable in bmsTables)
        {
            RemoveTableCore(bmsTable, GetSettingsSnapshot);
        }
        RequestPlaylistSummaryRefresh(
            "playlist_table_removed",
            invalidateTableCountCache: true,
            rebuildAsync: false);
    }

    private void RemoveTableCore(
        BMSTable bmsTable,
        Func<CustomFolderOutputSettingsSnapshot> settingsProvider)
    {
        if (bmsTable == null)
        {
            throw new ArgumentNullException(nameof(bmsTable));
        }

        RunWithNotifications(
            () =>
            {
                CustomFolderOutputSettingsSnapshot settings = settingsProvider();
                BMSPlaylist playlistStore = GetPlaylistStore();
                BMSLibrary library = GetPlaylistLibrary();
                if (settings.OperationModeLR2DB && !string.IsNullOrWhiteSpace(bmsTable.Output_dir))
                {
                    playlistStore.RemoveCustomFolder(bmsTable, settings);
                }

                BMSTable removedTable = playlistStore.RemoveBMSTable(bmsTable);
                if (settings.OperationModeLR2DB
                    && bmsTable.is_root_folder
                    && !string.IsNullOrWhiteSpace(bmsTable.Output_dir))
                {
                    LR2Config lr2config = GetLr2Config()
                        ?? throw new InvalidOperationException("LR2 config provider is not configured.");
                    string customFolderOutputDirectory = ResolveTableRemovalCustomFolderOutputDirectory(
                        bmsTable,
                        "playlist remove custom folder output directory notification",
                        settings);
                    lr2config.RemoveBMSSearchDirectories([customFolderOutputDirectory]);
                    lr2config.Save();
                }

                if (removedTable != null)
                {
                    library.RemoveReferenceBMSTables(removedTable);
                }

                RequestPlaylistReferenceSortInvalidation();
            },
            "playlist remove custom folder notification");
    }

    private string ResolveTableRemovalCustomFolderOutputDirectory(
        BMSTable bmsTable,
        string routeName,
        CustomFolderOutputSettingsSnapshot settings)
    {
        try
        {
            return BMSPlaylist.GetCustomFolderOutputDirectory(
                bmsTable,
                settings.LR2CustomFolderOutputBaseDir,
                settings.LR2CustomFolderOutputBaseDirRootType,
                settings.LR2CustomFolderAdditionalOutputBaseDirs);
        }
        catch (ArgumentNullException)
        {
            PlaylistTableRemovalInvalidOutputDirectoryRequested?.Invoke(
                this,
                new PlaylistTableRemovalInvalidOutputDirectoryEventArgs(routeName));
            throw;
        }
    }

    private void CreateFolder(BMSTable table)
    {
        if (!CanMutate(table, PlaylistWorkspaceMutationKind.CreateFolder))
        {
            return;
        }
        BMSPlaylist playlistStore = GetPlaylistStore();
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
        playlistStore.AcquireReaderLockBMSTables();
        try
        {
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
                    if (!playlistStore.RemoveEntriesBMSTable(entriesFromTarget, table, commitFlag: false))
                    {
                        return;
                    }
                }
                else
                {
                    sourceRows = [.. sourceRows
                        .Where(row => !entriesAlreadyInFolder.Contains(GridRowResolver.GetPlaylistEntry(row)))];
                    if (sourceRows.Count == 0)
                    {
                        return;
                    }
                    if (!playlistStore.RemoveEntriesBMSTable(
                            entriesFromTarget.Except(entriesAlreadyInFolder),
                            table,
                            commitFlag: false))
                    {
                        return;
                    }
                }
            }

            List<ChartFile> resolvedCharts = [.. sourceRows
                .Select(ResolveDropChart)
                .Where(chart => chart != null)];
            if (table.entry_type == LR2SongDBExtended.playlist.EntryUnitType.Folder
                && string.IsNullOrWhiteSpace(folderName))
            {
                if (!AddRowsToRootFolder(sourceRows, table, playlistStore, library))
                {
                    return;
                }
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
                if (!playlistStore.AddPlaylistEntriesToFolderBMSTable(entries, table, folderName, commitFlag: false))
                {
                    return;
                }
            }

            RunWithNotifications(
                () => playlistStore.ReOutputCustomFolderAndCommitToDB(table),
                "playlist drop custom folder output notification");
            library.AddReferenceBMSTablesToCharts(table, resolvedCharts);
            PublishEntriesChanged(table);
        }
        finally
        {
            playlistStore.FreeReaderLockBMSTables();
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

    private bool AddRowsToRootFolder(
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
        if (!playlistStore.AddPlaylistEntriesToFolderBMSTable(
            preservedEntryRows
                .Select(row => GridRowResolver.GetPlaylistEntry(row)?.Duplicate())
                .Where(entry => entry != null),
            table,
            string.Empty,
            commitFlag: false))
        {
            return false;
        }
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
            if (!playlistStore.AddPlaylistEntriesToFolderBMSTable(
                directoryCharts.Select(chart => BMSTableEntry.CreateForPlaylistDrop(chart, orgMd5s)),
                table,
                targetFolder,
                commitFlag: false))
            {
                return false;
            }
        }
        return true;
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
        using BMSPlaylist.OperationNotificationScope scope = BMSPlaylist.BeginOperationNotificationScope();
        try
        {
            operation();
        }
        finally
        {
            presentPlaylistOperationNotifications(scope, routeName);
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
                    "playlist_entries_updated",
                    invalidateTableCountCache: true);
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

internal sealed class PlaylistTableRemovalInvalidOutputDirectoryEventArgs : EventArgs
{
    internal PlaylistTableRemovalInvalidOutputDirectoryEventArgs(string routeName)
    {
        RouteName = routeName ?? string.Empty;
    }

    internal string RouteName { get; }
}
