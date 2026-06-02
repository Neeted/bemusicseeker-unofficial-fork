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
    public DuplicateAnalysisResult Analyze(OwnedDuplicateChartRowSnapshot snapshot, string duplicateWarningMessage)
    {
        var result = new DuplicateAnalysisResult();
        HashSet<DuplicateChartRow> duplicateRows = [];

        var dirToId = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        List<string> idToDir = [];
        List<int> parent = [];
        foreach (DuplicateHashBucket bucket in snapshot?.DuplicateHashBuckets ?? [])
        {
            int firstDirectoryId = -1;
            foreach (DuplicateChartRow row in bucket.Rows)
            {
                AddDuplicateHashRow(row, ref firstDirectoryId);
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
        result.ConnectedDirectoryCount = duplicateConnectedDirs.Count;

        var rowsByDir = new Dictionary<string, List<DuplicateChartRow>>(StringComparer.OrdinalIgnoreCase);
        foreach (DuplicateChartRow row in snapshot?.Rows ?? [])
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
            List<ChartFile> groupCharts = [];
            foreach (string directoryPath in dirs)
            {
                if (!rowsByDir.TryGetValue(directoryPath, out List<DuplicateChartRow> list))
                {
                    continue;
                }
                foreach (DuplicateChartRow row in list)
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
            }
            if (groupCharts.Count > 0)
            {
                result.DuplicateGroups.Add(new DuplicateGroup(groupCharts, [.. dirs]));
            }
        }
        result.DuplicateGroups.Sort((x, y) => string.Compare(x.Header, y.Header, StringComparison.Ordinal));
        return result;

        void AddDuplicateHashRow(DuplicateChartRow row, ref int firstDirectoryId)
        {
            duplicateRows.Add(row);
            string dir = row.DirectoryPath;
            if (string.IsNullOrWhiteSpace(dir))
            {
                return;
            }
            if (!dirToId.TryGetValue(dir, out int id))
            {
                id = idToDir.Count;
                dirToId[dir] = id;
                idToDir.Add(dir);
                parent.Add(id);
            }
            if (firstDirectoryId < 0)
            {
                firstDirectoryId = id;
                return;
            }
            int root1 = FindRoot(parent, firstDirectoryId);
            int root2 = FindRoot(parent, id);
            if (root1 != root2)
            {
                parent[root1] = root2;
            }
        }
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

    private static int FindRoot(List<int> parent, int node)
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
