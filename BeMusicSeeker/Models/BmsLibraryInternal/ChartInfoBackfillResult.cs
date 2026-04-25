using System.Collections.Generic;

namespace BeMusicSeeker.Models.BmsLibraryInternal;

/// <summary>
/// chart_info バックグラウンド構築の結果です。
/// 初回構築は時間がかかるため、件数と時間を install-performance.log に残します。
/// </summary>
internal sealed class ChartInfoBackfillResult
{
    /// <summary>
    /// 解析対象として選ばれた譜面数です。
    /// </summary>
    public int TargetCount { get; set; }

    /// <summary>
    /// 処理済み譜面数です。
    /// </summary>
    public int ProcessedCount { get; set; }

    /// <summary>
    /// chart_info を生成できた譜面数です。
    /// </summary>
    public int BackfilledCount { get; set; }

    /// <summary>
    /// 解析または保存に失敗した譜面数です。
    /// </summary>
    public int FailedCount { get; set; }

    /// <summary>
    /// SHA-256 digest の補完対象になった BMS 譜面数です。
    /// </summary>
    public int DigestTargetCount { get; set; }

    /// <summary>
    /// SHA-256 digest を補完できた BMS 譜面数です。
    /// </summary>
    public int DigestBackfilledCount { get; set; }

    /// <summary>
    /// SHA-256 digest 補完に失敗した BMS 譜面数です。
    /// </summary>
    public int DigestFailedCount { get; set; }

    /// <summary>
    /// ファイル読み取りに失敗した譜面数です。
    /// </summary>
    public int ReadFailedCount { get; set; }

    /// <summary>
    /// 読み取り後のメタデータ解析に失敗した譜面数です。
    /// </summary>
    public int ParseFailedCount { get; set; }

    /// <summary>
    /// 解析に失敗した譜面パスです。
    /// </summary>
    public List<string> FailedPaths { get; } = new List<string>();

    /// <summary>
    /// 解析に要した時間です。
    /// </summary>
    public long ComputeMs { get; set; }

    /// <summary>
    /// ファイル読み取りに要した時間です。
    /// </summary>
    public long ReadMs { get; set; }

    /// <summary>
    /// インメモリ解析に要した時間です。
    /// </summary>
    public long ParseMs { get; set; }

    /// <summary>
    /// DB 保存に要した時間です。
    /// </summary>
    public long DbCommitMs { get; set; }

    /// <summary>
    /// 全体の処理時間です。
    /// </summary>
    public long TotalMs { get; set; }

    /// <summary>
    /// 解析 worker 数です。
    /// </summary>
    public int WorkerCount { get; set; }

    /// <summary>
    /// 読み取り済み byte[] queue の上限です。
    /// </summary>
    public int QueueCapacity { get; set; }
}
