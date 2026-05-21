using System;
using System.Collections.Generic;
using System.Linq;

namespace BeMusicSeeker.Models.BmsLibraryInternal;

internal sealed class BmsLibraryDuplicateService
{
    /// <summary>
    /// ChartFile projection から重複判定用 snapshot を作成します。
    /// bmson は compatibility adapter を作らず、storage row を持つ <see cref="ChartFile"/> として保持します。
    /// </summary>
    /// <param name="charts">重複判定対象の chart。</param>
    /// <returns>重複判定用 snapshot。</returns>
    public List<DuplicateChartRow> BuildSnapshot(IEnumerable<ChartFile> charts)
    {
        return [.. (charts ?? [])
            .Select(DuplicateChartRow.CreateFromChart)
            .Where(row => row != null)];
    }

    /// <summary>
    /// BMS storage row に残っている重複 warning だけを消します。
    /// bmson duplicate warning は duplicate group の <see cref="ChartFile"/> projection にだけ持つため、ここでは永続状態を消しません。
    /// </summary>
    /// <param name="charts">重複判定対象の chart。</param>
    public void ClearDuplicateState(IEnumerable<ChartFile> charts)
    {
        foreach (BMSFile file in (charts ?? [])
            .Select(chart => chart?.GetBmsStorageOwner())
            .Where(file => file != null)
            .Distinct())
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
    public void ApplyDuplicateWarnings(IEnumerable<ChartFile> charts, string duplicateWarningMessage)
    {
        foreach (BMSFile file in (charts ?? [])
            .Select(chart => chart?.GetBmsStorageOwner())
            .Where(file => file != null)
            .Distinct())
        {
            file.ClearWarning(ChartWarningKind.DuplicateChart);
            file.SetWarning(ChartWarningKind.DuplicateChart, duplicateWarningMessage);
        }
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
        List<DuplicateChartRow> snapshotRows = [.. (snapshot ?? []).Where(row => row != null && row.Chart != null && !string.IsNullOrWhiteSpace(row.LookupHash))];
        List<IGrouping<string, DuplicateChartRow>> duplicateHashGroups = [.. snapshotRows
            .GroupBy(row => row.LookupHash, StringComparer.OrdinalIgnoreCase)
            .Where(group => group.Count() > 1)];
        HashSet<DuplicateChartRow> duplicateRows = [];
        foreach (IGrouping<string, DuplicateChartRow> duplicateHashGroup in duplicateHashGroups)
        {
            foreach (DuplicateChartRow item in duplicateHashGroup)
            {
                duplicateRows.Add(item);
                if (item.Chart != null)
                {
                    result.DuplicateCharts.Add(item.Chart);
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
                List<ChartFile> groupCharts = [.. groupRows
                    .Select(row => duplicateRows.Contains(row) ? ApplyDuplicateWarning(row.Chart, duplicateWarningMessage) : row.Chart)
                    .Where(chart => chart != null)];
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
