using BeMusicSeeker.Models;
using BeMusicSeeker.Models.BmsLibraryInternal;

namespace BeMusicSeeker.TestSupport.Components;

/// <summary>複数の実行区分で、統合の安全性確認の検査器に同じ準備・観測を使うための共通補助です。</summary>
internal static class DuplicateMergeSafetyTestSupport
{
    /// <summary>所持譜面の情報だけをメモリ上で代替する取得口です。指定フォルダの直下と配下の譜面を、呼出しの記録とともに返します。</summary>
    internal sealed class MemoryOwnedChartSource : IDuplicateMergeOwnedChartSource
    {
        private readonly List<ChartFile> charts = [];

        /// <summary>取得口が受けたフォルダを、呼出しの順に保持します。</summary>
        internal List<string> RequestedFolders { get; } = [];

        /// <summary>所持している譜面を追加します。</summary>
        internal MemoryOwnedChartSource Add(ChartFile chart)
        {
            charts.Add(chart);
            return this;
        }

        public IReadOnlyList<ChartFile> CaptureOwnedCharts(string folder)
        {
            lock (RequestedFolders) { RequestedFolders.Add(folder); }
            string prefix = folder.TrimEnd('\\') + "\\";
            return [.. charts.Where(chart => chart.Path.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))];
        }
    }

    /// <summary>
    /// 所持譜面の値を作ります。リソース参照は指定した原文から作り、<paramref name="unusedReferences"/> は未使用の定義として加えます。
    /// 親ディレクトリ参照は <c>../x.wav</c> のような原文で指定します。
    /// </summary>
    internal static ChartFile OwnedChart(string path, string[]? usedReferences = null, string[]? unusedReferences = null)
        => new(ChartFileKind.Bms, path, "00000000000000000000000000000000", null, "Title", "Title", "Artist", "Genre",
            Path.GetFileName(Path.GetDirectoryName(path)) ?? string.Empty, string.Empty, string.Empty, null, null, null)
        {
            Token = new OwnedChartToken(),
            Resources = [.. (usedReferences ?? []).Select(raw => ChartResourceReference.Parse(raw, ChartResourceKind.Audio)),
                .. (unusedReferences ?? []).Select(raw => ChartResourceReference.Parse(raw, ChartResourceKind.Audio).WithUsage(false))]
        };

    /// <summary>確認の入力を作って検査します。別のグループは、与えたフォルダの集合ごとに別のインスタンスとして索引へ登録します。</summary>
    internal static DuplicateMergeGroupInspection Inspect(
        IReadOnlyList<string> folders,
        MemoryOwnedChartSource? charts = null,
        bool sizeAndTimeOnly = false,
        IReadOnlyList<string>? registeredFolders = null,
        IEnumerable<IReadOnlyList<string>>? otherGroups = null)
    {
        object own = new object();
        var groups = new List<(object Owner, IReadOnlyList<string> Folders)> { (own, folders) };
        foreach (IReadOnlyList<string> other in otherGroups ?? []) { groups.Add((new object(), other)); }
        var request = new DuplicateMergeInspectionRequest(folders, registeredFolders ?? [], new DuplicateMergeFolderIndex(groups), own, sizeAndTimeOnly);
        return new DuplicateMergeSafetyInspector(charts ?? new MemoryOwnedChartSource()).Inspect(request);
    }

    /// <summary>同じ内容のバイト列を再現できるよう、seedから決まる内容を作ります。</summary>
    internal static byte[] PatternBytes(int length, int seed)
    {
        var random = new Random(seed);
        byte[] bytes = new byte[length];
        random.NextBytes(bytes);
        return bytes;
    }

    /// <summary>必要な親ディレクトリを作ってファイルを書き、指定があれば更新日時（UTC）を設定します。</summary>
    internal static string WriteFile(string path, byte[] content, DateTime? lastWriteTimeUtc = null)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllBytes(path, content);
        if (lastWriteTimeUtc.HasValue) { File.SetLastWriteTimeUtc(path, lastWriteTimeUtc.Value); }
        return path;
    }

    /// <summary>理由の種類を、結果の並びのまま取り出します。</summary>
    internal static DuplicateMergeVerificationReasonKind[] Kinds(DuplicateMergeVerificationResult result)
        => [.. result.Reasons.Select(reason => reason.Kind)];

    /// <summary>指定した種類の理由を返します。なければnullです。</summary>
    internal static DuplicateMergeVerificationReason? Reason(DuplicateMergeVerificationResult result, DuplicateMergeVerificationReasonKind kind)
        => result.Reasons.SingleOrDefault(reason => reason.Kind == kind);
}
