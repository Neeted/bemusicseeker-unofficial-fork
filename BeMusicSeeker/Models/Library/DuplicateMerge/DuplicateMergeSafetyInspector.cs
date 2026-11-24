using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.IO;
using System.Linq;
using BeMusicSeeker.Models.BmsLibraryInternal;
using BeMusicSeeker.Models.Utils;

namespace BeMusicSeeker.Models;

/// <summary>統合の安全性確認が、所持している譜面の情報を得る窓口です。ライブラリの索引から、指定フォルダの範囲だけを返します。</summary>
internal interface IDuplicateMergeOwnedChartSource
{
    /// <summary>
    /// 指定フォルダの直下と配下の、所持している譜面を返します。呼出しはUIスレッド外で行います。
    /// 返す譜面は読取りロックの外で使えるよう、捕捉済みの値です。リソース参照が未取得の譜面（<see cref="ChartFile.Resources"/> が null）は、
    /// 呼出し側が譜面ファイルを読んで取得します。
    /// </summary>
    /// <param name="folder">対象のフォルダ。</param>
    IReadOnlyList<ChartFile> CaptureOwnedCharts(string folder);
}

/// <summary>一つのグループの確認の入力です。</summary>
/// <param name="Folders">グループのフォルダ。PATHの昇順です。</param>
/// <param name="RegisteredFolders">グループ内の登録範囲のフォルダです。一覧の構築時に捕捉した値です。</param>
/// <param name="GroupFolders">現在の重複結果の全グループのフォルダです。別のグループとの重なりの判定に使います。</param>
/// <param name="GroupKey">このグループの識別です。<paramref name="GroupFolders"/> の登録と同じインスタンスです。</param>
/// <param name="SizeAndTimeOnly">サイズと更新日時が完全に一致するファイルを、読まずに同一内容とみなすかです。</param>
internal sealed record DuplicateMergeInspectionRequest(
    IReadOnlyList<string> Folders,
    IReadOnlyList<string> RegisteredFolders,
    DuplicateMergeFolderIndex GroupFolders,
    object GroupKey,
    bool SizeAndTimeOnly);

/// <summary>統合の安全性確認の検査器です。ワークフローは、この窓口だけで走査を行います。</summary>
internal interface IDuplicateMergeSafetyInspector
{
    /// <summary>一つのグループを検査し、KEEPによらない事実とKEEPに依存する事実を持つ結果を返します。UIスレッド外で呼びます。</summary>
    DuplicateMergeGroupInspection Inspect(DuplicateMergeInspectionRequest request);
}

/// <summary>
/// 統合の安全性確認の検査器です。ファイルを読むだけで、何も変更しません。グループごとに、フォルダの走査、同名の資源の内容の比較、
/// 所持譜面の親ディレクトリ参照と配下の別の譜面フォルダの収集を行い、KEEPに依存する事実はフォルダ別に保持します。
/// </summary>
/// <remarks>
/// 入出力の失敗は解析不能の理由へ変換し、それ以外の予期しない例外は呼出元へ伝えます。
/// フォルダごとの列挙は一回で、ファイルの読取りは比較が必要な同名の資源と保護対象に限ります。モデルのロックを保持したまま、ファイルを読みません。
/// </remarks>
internal sealed class DuplicateMergeSafetyInspector : IDuplicateMergeSafetyInspector
{
    private readonly IDuplicateMergeOwnedChartSource ownedCharts;

    /// <param name="ownedCharts">所持譜面の情報の取得口。</param>
    internal DuplicateMergeSafetyInspector(IDuplicateMergeOwnedChartSource ownedCharts)
    {
        this.ownedCharts = ownedCharts ?? throw new ArgumentNullException(nameof(ownedCharts));
    }

    private readonly record struct Entry(int FolderIndex, string RelativePath, bool IsDirectory, DuplicateMergeScannedFile File);

    /// <inheritdoc />
    public DuplicateMergeGroupInspection Inspect(DuplicateMergeInspectionRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        IReadOnlyList<string> folders = request.Folders ?? [];
        var reasons = new Dictionary<DuplicateMergeVerificationReasonKind, List<DuplicateMergeFinding>>();
        List<DuplicateMergeFinding> Reason(DuplicateMergeVerificationReasonKind kind)
        {
            if (!reasons.TryGetValue(kind, out List<DuplicateMergeFinding> list))
            {
                list = [];
                reasons[kind] = list;
            }
            return list;
        }

        bool multiple = folders.Count >= 2;
        DuplicateMergeFolderScan[] scans = [.. folders.Select(folder => DuplicateMergeFolderScan.Scan(folder, descend: multiple))];
        for (int index = 0; index < scans.Length; index++)
        {
            foreach (string link in scans[index].Links) { Reason(DuplicateMergeVerificationReasonKind.Link).Add(new(folders[index], link)); }
            foreach (string failure in scans[index].Failures) { Reason(DuplicateMergeVerificationReasonKind.Unreadable).Add(new(folders[index], failure)); }
        }
        IReadOnlyList<string> registered = request.RegisteredFolders ?? [];
        if (registered.Count >= 2)
        {
            Reason(DuplicateMergeVerificationReasonKind.RegisteredRoot).AddRange(registered.Select(folder => new DuplicateMergeFinding(folder, string.Empty)));
        }

        IReadOnlyList<string> protectedCollisions = [];
        DuplicateMergeComparisonSummary comparisons = DuplicateMergeComparisonSummary.None;
        var keepDependent = new Dictionary<string, DuplicateMergeKeepDependentFacts>(StringComparer.OrdinalIgnoreCase);
        if (multiple)
        {
            InspectFolderRelations(request, folders, Reason);
            (protectedCollisions, comparisons) = InspectContents(request, folders, scans, Reason);
            for (int index = 0; index < folders.Count; index++)
            {
                keepDependent[folders[index]] = InspectOwnedCharts(folders[index], Reason);
            }
        }

        return new DuplicateMergeGroupInspection(
            reasons.ToDictionary(
                pair => pair.Key,
                pair => DuplicateMergeGroupInspection.Sort(pair.Value.Distinct())),
            protectedCollisions,
            comparisons,
            keepDependent);
    }

    /// <summary>グループ内の親子関係と、別のグループとの重なりを調べます。</summary>
    private static void InspectFolderRelations(
        DuplicateMergeInspectionRequest request, IReadOnlyList<string> folders,
        Func<DuplicateMergeVerificationReasonKind, List<DuplicateMergeFinding>> reason)
    {
        string[] normalized = [.. folders.Select(DuplicateMergeSafetyPolicy.NormalizeFolder)];
        for (int first = 0; first < folders.Count; first++)
        {
            for (int second = first + 1; second < folders.Count; second++)
            {
                if (LongPathFileSystem.IsSameOrDescendantNormalizedDirectoryPath(normalized[first], normalized[second])
                    || LongPathFileSystem.IsSameOrDescendantNormalizedDirectoryPath(normalized[second], normalized[first]))
                {
                    reason(DuplicateMergeVerificationReasonKind.ParentChild).Add(new(folders[first], string.Empty, folders[second]));
                }
            }
        }
        if (request.GroupFolders == null) { return; }
        foreach (string folder in folders)
        {
            foreach (string other in request.GroupFolders.FindRelatedFoldersOfOtherGroups(request.GroupKey, folder))
            {
                reason(DuplicateMergeVerificationReasonKind.OtherGroup).Add(new(folder, string.Empty, other));
            }
        }
    }

    /// <summary>
    /// 同じ相対パスの項目を集め、型の衝突・同名音源の拡張子違い・別内容ファイル衝突・同一内容ファイルの連結・保護対象の別内容の衝突を調べます。
    /// 各ファイルの内容は、比較が必要な場合に一度だけ読みます。保護対象は資源と同じ手順で比べますが、連結と比較の組の数には数えません。
    /// </summary>
    private static (IReadOnlyList<string> ProtectedCollisions, DuplicateMergeComparisonSummary Comparisons) InspectContents(
        DuplicateMergeInspectionRequest request, IReadOnlyList<string> folders, DuplicateMergeFolderScan[] scans,
        Func<DuplicateMergeVerificationReasonKind, List<DuplicateMergeFinding>> reason)
    {
        var byRelativePath = new Dictionary<string, List<Entry>>(StringComparer.OrdinalIgnoreCase);
        var audioNames = new Dictionary<string, List<Entry>>(StringComparer.OrdinalIgnoreCase);
        bool[] resourceHolders = new bool[folders.Count];
        for (int index = 0; index < scans.Length; index++)
        {
            foreach (string directory in scans[index].Directories) { Add(byRelativePath, directory, new Entry(index, directory, true, null)); }
            foreach (DuplicateMergeScannedFile file in scans[index].Files.Values)
            {
                var entry = new Entry(index, file.RelativePath, false, file);
                Add(byRelativePath, file.RelativePath, entry);
                if (file.Category != DuplicateMergeFileCategory.Resource) { continue; }
                resourceHolders[index] = true;
                if (DuplicateMergeSafetyPolicy.IsAudio(file.RelativePath))
                {
                    string withoutExtension = Path.Combine(Path.GetDirectoryName(file.RelativePath) ?? string.Empty,
                        Path.GetFileNameWithoutExtension(file.RelativePath));
                    Add(audioNames, withoutExtension, entry);
                }
            }
        }

        var comparer = new DuplicateMergeContentComparer(request.SizeAndTimeOnly);
        var groups = new UnionFind(folders.Count);
        int sizeAndTime = 0, full = 0, partial = 0;
        var protectedCollisions = new List<string>();
        foreach (List<Entry> entries in byRelativePath.Values)
        {
            if (entries.Count < 2) { continue; }
            // 型の衝突: 同じ相対パスが、あるフォルダではファイル、別のフォルダではディレクトリ。
            foreach (Entry file in entries.Where(entry => !entry.IsDirectory))
            {
                foreach (Entry directory in entries.Where(entry => entry.IsDirectory && entry.FolderIndex != file.FolderIndex))
                {
                    reason(DuplicateMergeVerificationReasonKind.TypeConflict).Add(new(folders[file.FolderIndex], file.RelativePath));
                    reason(DuplicateMergeVerificationReasonKind.TypeConflict).Add(new(folders[directory.FolderIndex], directory.RelativePath));
                }
            }
            Entry[] files = [.. entries.Where(entry => !entry.IsDirectory)];
            // 保護対象: 資源と同じく全ての組を比べ、別内容と判定された組が一つでもあれば付記する。読めない組は解析不能の理由だけを残し、付記の判定に使わない。
            if (files.Length >= 2 && files[0].File.Category == DuplicateMergeFileCategory.Protected
                && HasDifferentContentPair(files, comparer))
            {
                protectedCollisions.Add(files[0].RelativePath);
            }
            // 資源: 同じ相対パスの全ての組を、各ファイルを一度だけ読んで比べる。
            Entry[] resources = [.. files.Where(entry => entry.File.Category == DuplicateMergeFileCategory.Resource)];
            for (int first = 0; first < resources.Length; first++)
            {
                for (int second = first + 1; second < resources.Length; second++)
                {
                    if (resources[first].FolderIndex == resources[second].FolderIndex) { continue; }
                    switch (comparer.Compare(resources[first].File, resources[second].File, out DuplicateMergeComparisonMethod method))
                    {
                        case DuplicateMergeContentComparison.Same:
                            groups.Union(resources[first].FolderIndex, resources[second].FolderIndex);
                            switch (method)
                            {
                                case DuplicateMergeComparisonMethod.SizeAndTime: sizeAndTime++; break;
                                case DuplicateMergeComparisonMethod.Full: full++; break;
                                default: partial++; break;
                            }
                            break;
                        case DuplicateMergeContentComparison.Different:
                            reason(DuplicateMergeVerificationReasonKind.ContentConflict).Add(new(folders[resources[first].FolderIndex], resources[first].RelativePath));
                            reason(DuplicateMergeVerificationReasonKind.ContentConflict).Add(new(folders[resources[second].FolderIndex], resources[second].RelativePath));
                            break;
                        default:
                            // 判定できない組は、連結の判定では同一内容として扱い、確認できない理由（読取りの失敗）だけを残す。
                            // その組以外のフォルダの連結不足は、通常どおり「同一内容ファイルなし」になる。
                            groups.Union(resources[first].FolderIndex, resources[second].FolderIndex);
                            break;
                    }
                }
            }
        }

        // 読取りに失敗したファイル。
        for (int index = 0; index < scans.Length; index++)
        {
            foreach (DuplicateMergeScannedFile file in scans[index].Files.Values.Where(file => file.DigestFailure != null))
            {
                reason(DuplicateMergeVerificationReasonKind.Unreadable).Add(new(folders[index], file.RelativePath));
            }
        }

        // 同名音源を持つフォルダのどこかに、統合で新しい形式が加わるかを調べる。
        foreach (List<Entry> entries in audioNames.Values)
        {
            if (entries.TrueForAll(entry => string.Equals(entry.RelativePath, entries[0].RelativePath, StringComparison.OrdinalIgnoreCase))) { continue; }
            IEnumerable<string> variants = entries.Select(entry => entry.RelativePath).Distinct(StringComparer.OrdinalIgnoreCase);
            if (variants.Any(path => entries.Exists(holder => !scans[holder.FolderIndex].Files.ContainsKey(path))))
            {
                reason(DuplicateMergeVerificationReasonKind.AudioExtension).AddRange(
                    entries.Select(entry => new DuplicateMergeFinding(folders[entry.FolderIndex], entry.RelativePath)));
            }
        }

        // 同一内容ファイルなし: 資源を持つフォルダが、同一内容の資源を共有する関係で一つに連結しない。
        int[] holders = [.. Enumerable.Range(0, folders.Count).Where(index => resourceHolders[index])];
        if (holders.Length >= 2 && holders.Select(groups.Find).Distinct().Count() >= 2)
        {
            foreach (int holder in holders) { reason(DuplicateMergeVerificationReasonKind.NoSharedContent).Add(new(folders[holder], string.Empty)); }
        }
        return (
            [.. protectedCollisions.Distinct(StringComparer.OrdinalIgnoreCase).Order(StringComparer.OrdinalIgnoreCase)],
            new DuplicateMergeComparisonSummary(sizeAndTime, full, partial));
    }

    /// <summary>
    /// 同じ相対パスのファイルのうち、別のフォルダにある組が一つでも別内容と判定されるかを返します。
    /// 別内容の組が見つかった後も全ての組を比べます。途中で打ち切ると、未読のファイルの読取り失敗が解析不能の理由に載らないためです。
    /// 各ファイルは比較器が一度だけ読むので、続けて比べても追加の読取りは未読のファイルに限ります。
    /// </summary>
    private static bool HasDifferentContentPair(Entry[] files, DuplicateMergeContentComparer comparer)
    {
        bool different = false;
        for (int first = 0; first < files.Length; first++)
        {
            for (int second = first + 1; second < files.Length; second++)
            {
                if (files[first].FolderIndex != files[second].FolderIndex
                    && comparer.Compare(files[first].File, files[second].File, out _) == DuplicateMergeContentComparison.Different)
                {
                    different = true;
                }
            }
        }
        return different;
    }

    private static void Add(Dictionary<string, List<Entry>> map, string key, Entry entry)
    {
        if (!map.TryGetValue(key, out List<Entry> list))
        {
            list = [];
            map[key] = list;
        }
        list.Add(entry);
    }

    /// <summary>
    /// フォルダの直下・配下の所持譜面から、使用中の親ディレクトリ参照と、配下の子孫フォルダの所持譜面を集めます。
    /// リソース参照が未取得の譜面は譜面ファイルを読んで取得し、取得できない譜面は解析不能の理由にします。
    /// </summary>
    private DuplicateMergeKeepDependentFacts InspectOwnedCharts(
        string folder, Func<DuplicateMergeVerificationReasonKind, List<DuplicateMergeFinding>> reason)
    {
        string root = DuplicateMergeSafetyPolicy.NormalizeFolder(folder);
        var parentReferences = new List<DuplicateMergeFinding>();
        var nested = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (ChartFile chart in ownedCharts.CaptureOwnedCharts(folder) ?? [])
        {
            if (chart == null || string.IsNullOrWhiteSpace(chart.Path)) { continue; }
            string relativeChart = ToRelative(root, chart.Path);
            string relativeDirectory = Path.GetDirectoryName(relativeChart) ?? string.Empty;
            if (relativeDirectory.Length > 0) { nested.Add(relativeDirectory); }
            ImmutableList<ChartResourceReference> references;
            try
            {
                references = chart.Resources ?? ReadResourceReferences(chart);
            }
            catch (Exception failure) when (failure is not OperationCanceledException)
            {
                reason(DuplicateMergeVerificationReasonKind.Unreadable).Add(new(folder, relativeChart));
                continue;
            }
            foreach (ChartResourceReference reference in references)
            {
                if (reference.RestrictsAutomaticDestination) { parentReferences.Add(new(folder, relativeChart, reference.RawPath)); }
            }
        }
        return new DuplicateMergeKeepDependentFacts(
            DuplicateMergeGroupInspection.Sort(parentReferences.Distinct()),
            DuplicateMergeGroupInspection.Sort(nested.Select(directory => new DuplicateMergeFinding(folder, directory))));
    }

    /// <summary>リソース参照が未取得の譜面のファイルを読み、参照を抽出します。既存の解析と同じ入口を使います。</summary>
    private static ImmutableList<ChartResourceReference> ReadResourceReferences(ChartFile chart)
    {
        ChartFileSnapshot snapshot = ChartFileContentReader.ReadSnapshot(chart.Path);
        ChartFile parsed = chart.Kind == ChartFileKind.Bmson
            ? BmsonChartFileParser.ParseSnapshot(snapshot)
            : BmsChartFileParser.ParseSnapshot(snapshot, BmsChartFileParser.GetConcreteFixedEncoding(chart));
        return parsed.Resources ?? throw new InvalidOperationException("Resource references were not extracted.");
    }

    private static string ToRelative(string root, string path)
    {
        string normalized = DuplicateMergeSafetyPolicy.NormalizeFolder(path);
        string prefix = root.EndsWith('\\') ? root : root + "\\";
        return normalized.StartsWith(prefix, StringComparison.OrdinalIgnoreCase) ? normalized[prefix.Length..] : normalized;
    }

    private sealed class UnionFind(int count)
    {
        private readonly int[] parent = [.. Enumerable.Range(0, count)];

        internal int Find(int value)
        {
            while (parent[value] != value)
            {
                parent[value] = parent[parent[value]];
                value = parent[value];
            }
            return value;
        }

        internal void Union(int first, int second) => parent[Find(first)] = Find(second);
    }
}
