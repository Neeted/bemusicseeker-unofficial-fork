using System.Collections.Generic;

namespace BeMusicSeeker.Models.BmsLibraryInternal;

/// <summary>
/// 重複 chart の解析結果です。
/// duplicate warning 反映対象の chart と、duplicate tree に表示する chart group を分けて保持します。
/// </summary>
internal sealed class DuplicateAnalysisResult
{
    /// <summary>
    /// 現在値へ重複警告を付与する値です。全体解析では重複ハッシュへ所属する全譜面、
    /// 差分解析では変化したハッシュのうち現在も重複である所属譜面だけです。
    /// </summary>
    public HashSet<ChartFile> DuplicateCharts { get; } = [];

    public List<DuplicateGroup> DuplicateGroups { get; } = [];

    /// <summary>差分解析だけが埋める、現在値の重複警告を解除する所持項目の識別です。付与する識別とは重ねません。</summary>
    public List<OwnedChartToken> WarningClearTokens { get; } = [];

    /// <summary>前回の結果と基底からの差分で作った結果なら true、所持集合全体の解析なら false です。</summary>
    public bool IsIncremental { get; init; }

    /// <summary>グループへ具体化した譜面の件数。差分解析では再判定した範囲だけです。</summary>
    public int MaterializedChartCount { get; set; }

    /// <summary>重複で接続したフォルダの数。差分解析では再判定した範囲だけです。</summary>
    public int ConnectedDirectoryCount { get; set; }

    /// <summary>差分解析で、基底から所属が変わったハッシュの数。</summary>
    public int ChangedHashCount { get; set; }

    /// <summary>差分解析で、バケットの参照が基底と異なり内容を比べたハッシュの数。</summary>
    public int ContentComparedBucketCount { get; set; }

    /// <summary>差分解析で、現在行を取得して調べたフォルダの数。</summary>
    public int RecheckedDirectoryCount { get; set; }

    /// <summary>差分解析で、現在行として取得した譜面の件数。</summary>
    public int FetchedRowCount { get; set; }

    /// <summary>差分解析で、作り直した（影響を受けた）前回のグループの数。</summary>
    public int RecheckedPreviousGroupCount { get; set; }

    /// <summary>差分解析で、新しく作ったグループの数。</summary>
    public int RecheckedGroupCount { get; set; }
}
