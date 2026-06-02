using System;
using System.Collections.Generic;
using System.Linq;
using BeMusicSeeker.Models.LR2;

namespace BeMusicSeeker.Models.BmsLibraryInternal;

internal sealed class BmsLibraryDuplicateService
{
    /// <summary>
    /// BMS storage row に残っている重複 warning だけを消します。
    /// </summary>
    /// <param name="bmsFiles">重複判定対象の BMS storage row。</param>
    public void ClearDuplicateState(IEnumerable<BMSFile> bmsFiles)
    {
        foreach (BMSFile file in (bmsFiles ?? []).Where(file => file != null).Distinct())
        {
            file.ClearWarning(ChartWarningKind.DuplicateChart);
        }
    }

    /// <summary>
    /// BMS storage row へ重複 warning を反映します。
    /// bmson は storage row が warning collection を持たないため、<see cref="Analyze"/> が返す <see cref="DuplicateGroup.ChartFiles"/> の projection に反映します。
    /// </summary>
    /// <param name="charts">重複 warning を付与する chart。</param>
    /// <param name="duplicateWarningMessage">重複 warning の表示本文。</param>
    public HashSet<BMSFile> ApplyDuplicateWarnings(IEnumerable<ChartFile> charts, string duplicateWarningMessage)
    {
        var warningOwners = new HashSet<BMSFile>();
        foreach (BMSFile file in (charts ?? [])
            .Select(chart => chart?.GetBmsStorageOwner())
            .Where(file => file != null))
        {
            if (!warningOwners.Add(file))
            {
                continue;
            }
            file.ClearWarning(ChartWarningKind.DuplicateChart);
            file.SetWarning(ChartWarningKind.DuplicateChart, duplicateWarningMessage);
        }
        return warningOwners;
    }

    /// <summary>
    /// lookup hash が一致する chart を重複として検出し、接続されたディレクトリ単位の group へまとめます。
    /// </summary>
    /// <param name="snapshot">重複判定用 snapshot。</param>
    /// <param name="duplicateWarningMessage">duplicate group の chart projection に重ねる warning 本文。</param>
    /// <returns>重複解析結果。</returns>
    public DuplicateAnalysisResult Analyze(IEnumerable<DuplicateChartRow> snapshot, string duplicateWarningMessage)
    {
        var result = new DuplicateAnalysisResult();
        List<DuplicateChartRow> snapshotRows = [.. (snapshot ?? []).Where(row => row != null && row.HasChartSource && !string.IsNullOrWhiteSpace(row.LookupHash))];
        List<IGrouping<string, DuplicateChartRow>> duplicateHashGroups = [.. snapshotRows
            .GroupBy(row => row.LookupHash, StringComparer.OrdinalIgnoreCase)
            .Where(group => group.Count() > 1)];
        HashSet<DuplicateChartRow> duplicateRows = [];
        foreach (IGrouping<string, DuplicateChartRow> duplicateHashGroup in duplicateHashGroups)
        {
            foreach (DuplicateChartRow item in duplicateHashGroup)
            {
                duplicateRows.Add(item);
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

        var duplicateConnectedDirs = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (HashSet<string> dirs in rootViewToDirs.Values)
        {
            duplicateConnectedDirs.UnionWith(dirs);
        }

        var rowsByDir = new Dictionary<string, List<DuplicateChartRow>>(StringComparer.OrdinalIgnoreCase);
        foreach (DuplicateChartRow row in snapshotRows)
        {
            string dir = row.DirectoryPath;
            if (string.IsNullOrWhiteSpace(dir) || !duplicateConnectedDirs.Contains(dir))
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
                List<ChartFile> groupCharts = [];
                foreach (DuplicateChartRow row in groupRows)
                {
                    ChartFile chart = row.CreateChart();
                    if (chart == null)
                    {
                        continue;
                    }
                    result.MaterializedChartCount++;
                    if (duplicateRows.Contains(row))
                    {
                        chart = ApplyDuplicateWarning(chart, duplicateWarningMessage);
                        result.DuplicateCharts.Add(chart);
                    }
                    groupCharts.Add(chart);
                }
                result.DuplicateGroups.Add(new DuplicateGroup(groupCharts, [.. dirs]));
            }
        }
        result.DuplicateGroups.Sort((x, y) => string.Compare(x.Header, y.Header, StringComparison.Ordinal));
        return result;
    }

    private static ChartFile ApplyDuplicateWarning(ChartFile chart, string duplicateWarningMessage)
    {
        if (chart == null)
        {
            return null;
        }

        List<ChartWarning> warnings =
        [
            .. (chart.Warnings ?? []).Where(warning => warning != null && warning.Kind != ChartWarningKind.DuplicateChart),
            ChartWarning.Create(ChartWarningKind.DuplicateChart, duplicateWarningMessage),
        ];
        return ChartFileProjection.WithWarnings(chart, warnings);
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
