using System;
using System.Collections.Generic;
using System.Linq;
using BeMusicSeeker.Models.LR2;
using BeMusicSeeker.Models.Utils;

namespace BeMusicSeeker.Models.BmsLibraryInternal;

internal sealed class BmsLibraryDuplicateService
{
    public List<DuplicateChartRow> BuildSnapshot(IEnumerable<BMSFile> bmsFiles, IEnumerable<LR2SongDBExtended.bmson_song> bmsonSongs)
    {
        List<DuplicateChartRow> rows =
        [
            .. (bmsFiles ?? [])
                .Select(DuplicateChartRow.CreateFromBmsFile)
                .Where(row => row != null),
            .. (bmsonSongs ?? [])
                .Select(DuplicateChartRow.CreateFromBmsonSong)
                .Where(row => row != null),
        ];
        return rows;
    }

    public void ClearDuplicateState(IEnumerable<BMSFile> files)
    {
        foreach (BMSFile file in files ?? [])
        {
            if (file == null)
            {
                continue;
            }
            file.ClearWarning(ChartWarningKind.DuplicateChart);
        }
    }

    public void ApplyDuplicateWarnings(IEnumerable<BMSFile> files, string duplicateWarningMessage)
    {
        foreach (BMSFile file in files ?? [])
        {
            if (file == null)
            {
                continue;
            }
            file.ClearWarning(ChartWarningKind.DuplicateChart);
            file.SetWarning(ChartWarningKind.DuplicateChart, duplicateWarningMessage);
        }
    }

    public DuplicateAnalysisResult Analyze(IEnumerable<DuplicateChartRow> snapshot)
    {
        var result = new DuplicateAnalysisResult();
        List<DuplicateChartRow> snapshotRows = [.. (snapshot ?? []).Where(row => row != null && row.Chart != null && !string.IsNullOrWhiteSpace(row.LookupHash))];
        List<IGrouping<string, DuplicateChartRow>> duplicateHashGroups = [.. snapshotRows
            .GroupBy(row => row.LookupHash, StringComparer.OrdinalIgnoreCase)
            .Where(group => group.Count() > 1)];
        foreach (IGrouping<string, DuplicateChartRow> duplicateHashGroup in duplicateHashGroups)
        {
            foreach (DuplicateChartRow item in duplicateHashGroup)
            {
                BMSFile displayRow = item.GetOrCreateDisplayRow();
                if (displayRow != null)
                {
                    result.DuplicateFiles.Add(displayRow);
                }
            }
        }

        var dirToId = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        List<string> idToDir = [];
        List<List<int>> groupIndices = [];
        foreach (IGrouping<string, DuplicateChartRow> duplicateHashGroup2 in duplicateHashGroups)
        {
            List<int> currentGroup = [];
            foreach (DuplicateChartRow duplicateRow in duplicateHashGroup2)
            {
                string dir = duplicateRow.DirectoryPath;
                if (string.IsNullOrWhiteSpace(dir))
                {
                    continue;
                }
                if (!dirToId.TryGetValue(dir, out int id))
                {
                    id = idToDir.Count;
                    dirToId[dir] = id;
                    idToDir.Add(dir);
                }
                currentGroup.Add(id);
            }
            if (currentGroup.Count > 0)
            {
                groupIndices.Add(currentGroup);
            }
        }

        int[] parent = [.. Enumerable.Range(0, idToDir.Count)];
        foreach (List<int> group in groupIndices)
        {
            if (group.Count <= 1)
            {
                continue;
            }
            int first = group[0];
            for (int i = 1; i < group.Count; i++)
            {
                int root1 = FindRoot(parent, first);
                int root2 = FindRoot(parent, group[i]);
                if (root1 != root2)
                {
                    parent[root1] = root2;
                }
            }
        }

        Dictionary<int, HashSet<string>> rootViewToDirs = [];
        for (int i = 0; i < idToDir.Count; i++)
        {
            int root = FindRoot(parent, i);
            if (!rootViewToDirs.TryGetValue(root, out HashSet<string> set))
            {
                set = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                rootViewToDirs[root] = set;
            }
            set.Add(idToDir[i]);
        }

        var rowsByDir = new Dictionary<string, List<DuplicateChartRow>>(StringComparer.OrdinalIgnoreCase);
        foreach (DuplicateChartRow row in snapshotRows)
        {
            string dir = row.DirectoryPath;
            if (string.IsNullOrWhiteSpace(dir))
            {
                continue;
            }
            if (!rowsByDir.TryGetValue(dir, out List<DuplicateChartRow> list))
            {
                list = [];
                rowsByDir[dir] = list;
            }
            list.Add(row);
        }

        foreach (HashSet<string> dirs in rootViewToDirs.Values)
        {
            List<DuplicateChartRow> groupRows = [];
            foreach (string directoryPath in dirs)
            {
                if (rowsByDir.TryGetValue(directoryPath, out List<DuplicateChartRow> list))
                {
                    groupRows.AddRange(list);
                }
            }
            if (groupRows.Count > 0)
            {
                List<ChartFile> groupCharts = [.. groupRows.Select(row => row.Chart).Where(chart => chart != null)];
                result.DuplicateGroups.Add(new DuplicateGroup(groupCharts, [.. dirs]));
            }
        }
        result.DuplicateGroups.Sort((x, y) => string.Compare(x.Header, y.Header, StringComparison.Ordinal));
        return result;
    }

    private static int FindRoot(int[] parent, int node)
    {
        int root = node;
        while (parent[root] != root)
        {
            parent[root] = parent[parent[root]];
            root = parent[root];
        }
        return root;
    }

}
