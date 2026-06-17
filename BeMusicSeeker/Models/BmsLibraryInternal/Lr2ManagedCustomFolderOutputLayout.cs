using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using BeMusicSeeker.Models.LR2;

namespace BeMusicSeeker.Models.BmsLibraryInternal;

internal sealed class Lr2ManagedCustomFolderOutputCounts(
    IReadOnlyDictionary<int, int> userFolderCounts,
    IReadOnlyDictionary<int, int> levelFolderCounts,
    ISet<int> nullLevelPlaylistIds)
{
    public IReadOnlyDictionary<int, int> UserFolderCounts { get; } =
        userFolderCounts ?? new Dictionary<int, int>();

    public IReadOnlyDictionary<int, int> LevelFolderCounts { get; } =
        levelFolderCounts ?? new Dictionary<int, int>();

    public ISet<int> NullLevelPlaylistIds { get; } =
        nullLevelPlaylistIds ?? new HashSet<int>();

    public static Lr2ManagedCustomFolderOutputCounts Empty { get; } = new(
        new Dictionary<int, int>(),
        new Dictionary<int, int>(),
        new HashSet<int>());
}

internal static class Lr2ManagedCustomFolderOutputLayout
{
    internal static Lr2ManagedCustomFolderOutputCounts CreateCountsFromLoadedTable(BMSTable table)
    {
        if (table?.playlist_id == null)
        {
            return Lr2ManagedCustomFolderOutputCounts.Empty;
        }

        int playlistId = table.playlist_id.Value;
        int userFolderCount = (table.folder_list ?? [])
            .Distinct(StringComparer.Ordinal)
            .Count();
        int levelFolderCount = (table.entries ?? [])
            .Where(entry => entry != null && !entry.is_removed && entry.level.HasValue)
            .Select(entry => (int)Math.Floor(entry.level.Value))
            .Distinct()
            .Count();
        bool hasNullLevel = (table.entries ?? [])
            .Any(entry => entry != null && !entry.is_removed && !entry.level.HasValue);

        return new Lr2ManagedCustomFolderOutputCounts(
            new Dictionary<int, int>
            {
                [playlistId] = userFolderCount
            },
            new Dictionary<int, int>
            {
                [playlistId] = levelFolderCount
            },
            hasNullLevel ? new HashSet<int> { playlistId } : new HashSet<int>());
    }

    internal static IReadOnlyList<string> CreateRelativeFilePaths(
        BMSTable table,
        Lr2ManagedCustomFolderOutputCounts outputCounts,
        bool enableUnsentSongs)
    {
        if (table?.playlist_id == null)
        {
            return [];
        }

        int playlistId = table.playlist_id.Value;
        LR2SongDBExtended.playlist.CustomFolderType ignored =
            LR2SongDBExtended.playlist.NormalizeCustomFolderOutputMask(table.ignore_folder_output);
        bool outputRandom = IsCustomFolderTypeEnabled(ignored, LR2SongDBExtended.playlist.CustomFolderType.RandomFolder);
        int folderScopeCount = 1 + (outputCounts?.UserFolderCounts.TryGetValue(playlistId, out int userFolderCount) == true
            ? userFolderCount
            : 0);
        var result = new List<string>();
        int rootFileCount = 0;
        if (IsCustomFolderTypeEnabled(ignored, LR2SongDBExtended.playlist.CustomFolderType.UserFolder))
        {
            rootFileCount += folderScopeCount;
            if (outputRandom)
            {
                rootFileCount += folderScopeCount;
            }
        }
        if (IsCustomFolderTypeEnabled(ignored, LR2SongDBExtended.playlist.CustomFolderType.LevelFolder))
        {
            int levelFolderCount = outputCounts?.LevelFolderCounts.TryGetValue(playlistId, out int countedLevelFolderCount) == true
                ? countedLevelFolderCount
                : 0;
            if (outputCounts?.NullLevelPlaylistIds.Contains(playlistId) == true)
            {
                levelFolderCount++;
            }
            rootFileCount += levelFolderCount;
            if (outputRandom)
            {
                rootFileCount += levelFolderCount;
            }
        }
        if (IsCustomFolderTypeEnabled(ignored, LR2SongDBExtended.playlist.CustomFolderType.AlphabetFolder))
        {
            rootFileCount += 7;
        }
        if (IsCustomFolderTypeEnabled(ignored, LR2SongDBExtended.playlist.CustomFolderType.CategoryAllFolder))
        {
            rootFileCount += 5;
        }
        if (IsCustomFolderTypeEnabled(ignored, LR2SongDBExtended.playlist.CustomFolderType.OtherFolder))
        {
            rootFileCount += enableUnsentSongs ? 4 : 3;
        }
        AddSequentialRelativePaths(result, string.Empty, rootFileCount);
        if (IsCustomFolderTypeEnabled(ignored, LR2SongDBExtended.playlist.CustomFolderType.ClearFolder))
        {
            foreach (string clearDirectory in new[] { "0 NO PLAY", "1 FAILED", "2 ASSIST", "3 EASY", "4 CLEAR", "5 HARD", "6 FC", "7 P.A" })
            {
                AddSequentialRelativePaths(
                    result,
                    Path.Combine("CLEAR FOLDER", clearDirectory),
                    folderScopeCount * (outputRandom ? 2 : 1));
            }
        }
        if (IsCustomFolderTypeEnabled(ignored, LR2SongDBExtended.playlist.CustomFolderType.DJLevelFolder))
        {
            foreach (string djLevelDirectory in new[] { "AAA", "AA", "A", "UNDER A" })
            {
                AddSequentialRelativePaths(
                    result,
                    Path.Combine("DJ LEVEL", djLevelDirectory),
                    folderScopeCount * (outputRandom ? 2 : 1));
            }
        }
        if (IsCustomFolderTypeEnabled(ignored, LR2SongDBExtended.playlist.CustomFolderType.BpmSortFolder))
        {
            AddSequentialRelativePaths(result, "BPM SORT", folderScopeCount);
        }
        if (IsCustomFolderTypeEnabled(ignored, LR2SongDBExtended.playlist.CustomFolderType.BpSortFolder))
        {
            AddSequentialRelativePaths(result, "BP SORT", folderScopeCount);
        }
        if (IsCustomFolderTypeEnabled(ignored, LR2SongDBExtended.playlist.CustomFolderType.PlayCountSortFolder))
        {
            AddSequentialRelativePaths(result, "PLAY COUNT SORT", folderScopeCount);
        }
        return result;
    }

    private static bool IsCustomFolderTypeEnabled(
        LR2SongDBExtended.playlist.CustomFolderType ignored,
        LR2SongDBExtended.playlist.CustomFolderType type)
    {
        return LR2SongDBExtended.playlist.IsCustomFolderTypeEnabled(ignored, type);
    }

    private static void AddSequentialRelativePaths(
        IList<string> result,
        string relativeDirectory,
        int count)
    {
        if (result == null || count <= 0)
        {
            return;
        }

        for (int index = 0; index < count; index++)
        {
            result.Add(string.IsNullOrWhiteSpace(relativeDirectory)
                ? $"{index:D4}.lr2folder"
                : Path.Combine(relativeDirectory, $"{index:D4}.lr2folder"));
        }
    }
}
