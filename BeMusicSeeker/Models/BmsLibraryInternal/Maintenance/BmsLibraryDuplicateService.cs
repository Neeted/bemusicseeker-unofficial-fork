using System;
using System.Collections.Generic;
using System.Linq;

namespace BeMusicSeeker.Models.BmsLibraryInternal;

internal sealed class BmsLibraryDuplicateService
{
    /// <summary>重複警告を付けた不変の共通値を返します。</summary>
    public IReadOnlyList<ChartFile> ApplyDuplicateWarnings(IEnumerable<ChartFile> charts)
        => [.. (charts ?? []).Where(chart => chart?.Kind == ChartFileKind.Bms).Select(chart => chart with
            { Warnings = [.. chart.Warnings.Where(warning => warning.Kind != ChartWarningKind.DuplicateChart),
                ChartWarning.Create(ChartWarningKind.DuplicateChart)] })];

    /// <summary>
    /// lookup hash が一致する chart を重複として検出し、接続されたディレクトリ単位の group へまとめます。
    /// 重複所属は捕捉したbucketのlookup hashで照合し、別列挙の行インスタンスの同一性を要求しません。
    /// </summary>
    /// <param name="snapshot">重複判定用 snapshot。</param>
    /// <returns>接続フォルダの兄弟譜面を含むグループと、重複hashへ所属する譜面だけに警告を付けた解析結果。</returns>
    public DuplicateAnalysisResult Analyze(OwnedDuplicateChartRowSnapshot snapshot)
    {
        var result = new DuplicateAnalysisResult();
        var duplicateHashes = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        var union = new DirectoryUnion();
        foreach (DuplicateHashBucket bucket in snapshot?.DuplicateHashBuckets ?? [])
        {
            // 増分snapshotのRowsは列挙ごとに行を作るため、同じ捕捉値のhashで重複所属を保持する。
            duplicateHashes.Add(bucket.LookupHash);
            union.AddBucket(bucket);
        }

        List<List<string>> components = union.GetComponents();
        var duplicateConnectedDirs = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (List<string> dirs in components)
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

        foreach (List<string> dirs in components)
        {
            AddGroup(result.DuplicateGroups, result, dirs,
                dir => rowsByDir.TryGetValue(dir, out List<DuplicateChartRow> list) ? list : [],
                hash => duplicateHashes.Contains(hash));
        }
        result.DuplicateGroups.Sort((x, y) => string.Compare(x.Header, y.Header, StringComparison.Ordinal));
        return result;
    }

    /// <summary>
    /// 前回の検索結果を、基底から現在までに変化したハッシュだけを起点に更新します。結果は同じ現在の所持集合を
    /// <see cref="Analyze"/> にかけた結果と、グループごとのフォルダ・譜面の識別とパス・順序・重複警告の有無・並びが一致します。
    /// </summary>
    /// <remarks>
    /// 変化したハッシュに基底または現在で所属する譜面のフォルダ（影響フォルダ）と、それを含む前回のグループの全フォルダを再判定の範囲とし、
    /// 範囲のフォルダの譜面だけを現在値から具体化します。範囲に入らない前回のグループは同じインスタンスを維持し、
    /// 題名など識別・パス・ハッシュ以外の値だけが変わった場合でも捕捉時の見出し・譜面値を保ちます。
    /// 重複でない行からは重複警告を外します。現在値へ付与・解除する警告の対象は、変化したハッシュの所属譜面だけです。
    /// </remarks>
    /// <param name="baseline">前回の検索が使った捕捉。</param>
    /// <param name="previousGroups">前回の検索結果。<paramref name="baseline"/> から作られたものに限ります。</param>
    /// <param name="current">現在の捕捉。</param>
    /// <param name="readDirectoryRows">フォルダ直下の現在行を、全体解析の行列と同じ正規順で返す窓口。範囲のフォルダごとに呼びます。</param>
    public DuplicateAnalysisResult AnalyzeIncremental(
        OwnedDuplicateChartRowSnapshot baseline,
        IReadOnlyList<DuplicateGroup> previousGroups,
        OwnedDuplicateChartRowSnapshot current,
        Func<string, IReadOnlyList<DuplicateChartRow>> readDirectoryRows)
    {
        ArgumentNullException.ThrowIfNull(baseline);
        ArgumentNullException.ThrowIfNull(current);
        ArgumentNullException.ThrowIfNull(readDirectoryRows);
        IReadOnlyList<DuplicateGroup> previous = previousGroups ?? [];
        var result = new DuplicateAnalysisResult { IsIncremental = true };

        IReadOnlyList<DuplicateHashChange> changes = current.FindChangedHashes(baseline, out int contentCompared);
        result.ChangedHashCount = changes.Count;
        result.ContentComparedBucketCount = contentCompared;
        if (changes.Count == 0)
        {
            result.DuplicateGroups.AddRange(previous);
            return result;
        }

        var changedHashes = new HashSet<string>(changes.Select(change => change.LookupHash), StringComparer.OrdinalIgnoreCase);
        var queue = new Queue<string>();
        foreach (DuplicateHashChange change in changes)
        {
            foreach (DuplicateChartRow row in change.BaseRows.Concat(change.CurrentRows))
            {
                if (!string.IsNullOrWhiteSpace(row.DirectoryPath))
                {
                    queue.Enqueue(row.DirectoryPath);
                }
            }
        }

        var previousByDirectory = new Dictionary<string, DuplicateGroup>(StringComparer.OrdinalIgnoreCase);
        foreach (DuplicateGroup group in previous)
        {
            foreach (string folder in group.Folders)
            {
                previousByDirectory[folder] = group;
            }
        }

        // 範囲は、変化したハッシュのフォルダ、それを含む前回グループの全フォルダ、現在の重複バケットで接続するフォルダの閉包。
        var rowsByDir = new Dictionary<string, IReadOnlyList<DuplicateChartRow>>(StringComparer.OrdinalIgnoreCase);
        var affectedGroups = new HashSet<DuplicateGroup>();
        var seenHashes = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        List<DuplicateHashBucket> buckets = [];
        while (queue.Count > 0)
        {
            string dir = queue.Dequeue();
            if (rowsByDir.ContainsKey(dir))
            {
                continue;
            }
            IReadOnlyList<DuplicateChartRow> rows = readDirectoryRows(dir) ?? [];
            rowsByDir[dir] = rows;
            result.FetchedRowCount += rows.Count;
            if (previousByDirectory.TryGetValue(dir, out DuplicateGroup affected) && affectedGroups.Add(affected))
            {
                foreach (string folder in affected.Folders)
                {
                    queue.Enqueue(folder);
                }
            }
            foreach (DuplicateChartRow row in rows)
            {
                if (string.IsNullOrWhiteSpace(row.LookupHash) || !seenHashes.Add(row.LookupHash)
                    || !current.TryGetDuplicateBucket(row.LookupHash, out DuplicateHashBucket bucket))
                {
                    continue;
                }
                buckets.Add(bucket);
                foreach (DuplicateChartRow member in bucket.Rows)
                {
                    if (!string.IsNullOrWhiteSpace(member.DirectoryPath))
                    {
                        queue.Enqueue(member.DirectoryPath);
                    }
                }
            }
        }
        result.RecheckedDirectoryCount = rowsByDir.Count;
        result.RecheckedPreviousGroupCount = affectedGroups.Count;

        // 全体解析と同じく、バケットを先頭行の正規順に並べて連結し、フォルダの最初の表記と順序を決める。
        var union = new DirectoryUnion();
        foreach (DuplicateHashBucket bucket in buckets.OrderBy(bucket => bucket.Order))
        {
            union.AddBucket(bucket);
        }
        List<DuplicateGroup> recheckedGroups = [];
        foreach (List<string> dirs in union.GetComponents())
        {
            result.ConnectedDirectoryCount += dirs.Count;
            AddGroup(recheckedGroups, result, dirs,
                dir => rowsByDir.TryGetValue(dir, out IReadOnlyList<DuplicateChartRow> rows) ? rows : [],
                current.IsDuplicateHash);
        }
        recheckedGroups = [.. recheckedGroups.OrderBy(group => group.Header, StringComparer.Ordinal)];
        result.RecheckedGroupCount = recheckedGroups.Count;

        List<DuplicateGroup> retained = [.. previous.Where(group => !affectedGroups.Contains(group))];
        MergeByHeader(result.DuplicateGroups, retained, recheckedGroups);

        CollectWarningTargets(result, changes, changedHashes, rowsByDir, current);
        return result;
    }

    /// <summary>
    /// 現在値の警告を付与・解除する対象を、変化したハッシュの所属譜面から決めます。
    /// 現在も重複なら付与、重複でなくなった所属と基底だけの所属は解除です。付与と解除が重なる識別は付与だけにします。
    /// </summary>
    private static void CollectWarningTargets(
        DuplicateAnalysisResult result,
        IReadOnlyList<DuplicateHashChange> changes,
        HashSet<string> changedHashes,
        Dictionary<string, IReadOnlyList<DuplicateChartRow>> rowsByDir,
        OwnedDuplicateChartRowSnapshot current)
    {
        var warned = new HashSet<OwnedChartToken>();
        var cleared = new HashSet<OwnedChartToken>();
        foreach (IReadOnlyList<DuplicateChartRow> rows in rowsByDir.Values)
        {
            foreach (DuplicateChartRow row in rows)
            {
                if (string.IsNullOrWhiteSpace(row.LookupHash) || !changedHashes.Contains(row.LookupHash)
                    || row.ChartKind != ChartFileKind.Bms || row.Chart?.Token == null)
                {
                    continue;
                }
                if (current.IsDuplicateHash(row.LookupHash))
                {
                    result.DuplicateCharts.Add(ApplyDuplicateWarning(row.Chart));
                    warned.Add(row.Chart.Token);
                }
                else
                {
                    cleared.Add(row.Chart.Token);
                }
            }
        }
        foreach (DuplicateHashChange change in changes)
        {
            foreach (DuplicateChartRow row in change.BaseRows)
            {
                if (row.ChartKind == ChartFileKind.Bms && row.Chart?.Token != null)
                {
                    cleared.Add(row.Chart.Token);
                }
            }
        }
        cleared.ExceptWith(warned);
        result.WarningClearTokens.AddRange(cleared);
    }

    /// <summary>見出しの序数順に並んだ二つの列を、見出し順を保って一つへ併合します。</summary>
    private static void MergeByHeader(List<DuplicateGroup> target, List<DuplicateGroup> first, List<DuplicateGroup> second)
    {
        int i = 0;
        int j = 0;
        while (i < first.Count && j < second.Count)
        {
            target.Add(string.Compare(second[j].Header, first[i].Header, StringComparison.Ordinal) < 0 ? second[j++] : first[i++]);
        }
        target.AddRange(first.Skip(i));
        target.AddRange(second.Skip(j));
    }

    /// <summary>一つの連結成分のフォルダの譜面を現在値から具体化し、グループとして追加します。</summary>
    private static void AddGroup(
        List<DuplicateGroup> groups,
        DuplicateAnalysisResult result,
        List<string> directories,
        Func<string, IReadOnlyList<DuplicateChartRow>> rowsOf,
        Func<string, bool> isDuplicateHash)
    {
        List<ChartFile> groupCharts = [];
        foreach (string directoryPath in directories)
        {
            foreach (DuplicateChartRow row in rowsOf(directoryPath))
            {
                ChartFile chart = row.CreateChart();
                if (chart == null)
                {
                    continue;
                }
                result.MaterializedChartCount++;
                if (isDuplicateHash(row.LookupHash))
                {
                    chart = ApplyDuplicateWarning(chart);
                    if (!result.IsIncremental)
                    {
                        result.DuplicateCharts.Add(chart);
                    }
                }
                else
                {
                    chart = RemoveDuplicateWarning(chart);
                }
                groupCharts.Add(chart);
            }
        }
        if (groupCharts.Count > 0)
        {
            groups.Add(new DuplicateGroup(groupCharts, [.. directories]));
        }
    }

    private static ChartFile ApplyDuplicateWarning(ChartFile chart)
    {
        if (chart == null)
        {
            return null;
        }

        List<ChartWarning> warnings =
        [
            .. (chart.Warnings ?? []).Where(warning => warning != null && warning.Kind != ChartWarningKind.DuplicateChart),
            ChartWarning.Create(ChartWarningKind.DuplicateChart),
        ];
        return ChartFileProjection.WithWarnings(chart, warnings);
    }

    /// <summary>重複ハッシュへ所属しない譜面から重複警告を外します。外す警告がなければ同じ値を返します。</summary>
    private static ChartFile RemoveDuplicateWarning(ChartFile chart)
    {
        if (chart?.Warnings == null || !chart.Warnings.Any(warning => warning?.Kind == ChartWarningKind.DuplicateChart))
        {
            return chart;
        }
        return ChartFileProjection.WithWarnings(chart,
            [.. chart.Warnings.Where(warning => warning != null && warning.Kind != ChartWarningKind.DuplicateChart)]);
    }

    /// <summary>
    /// 重複ハッシュのバケットで連結したフォルダの成分を求めます。バケットは先頭行の正規順に渡し、
    /// 各バケットの行の順にたどって最初に現れたフォルダの表記と順序を採ります。
    /// </summary>
    private sealed class DirectoryUnion
    {
        private readonly Dictionary<string, int> dirToId = new(StringComparer.OrdinalIgnoreCase);
        private readonly List<string> idToDir = [];
        private readonly List<int> parent = [];

        public void AddBucket(DuplicateHashBucket bucket)
        {
            int firstDirectoryId = -1;
            foreach (DuplicateChartRow row in bucket.Rows)
            {
                string dir = row.DirectoryPath;
                if (string.IsNullOrWhiteSpace(dir))
                {
                    continue;
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
                    continue;
                }
                int root1 = FindRoot(firstDirectoryId);
                int root2 = FindRoot(id);
                if (root1 != root2)
                {
                    parent[root1] = root2;
                }
            }
        }

        /// <summary>連結成分ごとのフォルダを、最初に現れた順で返します。</summary>
        public List<List<string>> GetComponents()
        {
            Dictionary<int, List<string>> byRoot = [];
            for (int i = 0; i < idToDir.Count; i++)
            {
                int root = FindRoot(i);
                if (!byRoot.TryGetValue(root, out List<string> list))
                {
                    list = [];
                    byRoot[root] = list;
                }
                list.Add(idToDir[i]);
            }
            return [.. byRoot.Values];
        }

        private int FindRoot(int node)
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
}
