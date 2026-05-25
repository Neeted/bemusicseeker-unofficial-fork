using System.Collections.Generic;

namespace BeMusicSeeker.Models.BmsLibraryInternal;

/// <summary>
/// chart_info バックグラウンド構築の結果です。
/// 初回構築は時間がかかるため、件数と時間を install-performance.log に残します。
/// </summary>
internal sealed class ChartInfoBackfillResult
{
    public List<LibraryChartHashChange> HashChanges { get; } = [];

    /// <summary>
    /// バックフィルの実行モードです。
    /// </summary>
    public string Mode { get; set; }

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
    /// 解析 timeout により chart_info を生成できなかった譜面数です。
    /// </summary>
    public int TimeoutFailedCount { get; set; }

    /// <summary>
    /// current chart_info 行によりファイル読み取り前にスキップした譜面数です。
    /// </summary>
    public int CurrentRowSkippedCount { get; set; }

    /// <summary>
    /// 永続化済みの解析失敗記録により再解析をスキップした譜面数です。
    /// </summary>
    public int FailureSkippedCount { get; set; }

    /// <summary>
    /// 新たに保存または更新した解析失敗記録数です。
    /// </summary>
    public int FailurePersistedCount { get; set; }

    /// <summary>
    /// 解析成功により削除した解析失敗記録数です。
    /// </summary>
    public int FailureClearedCount { get; set; }

    /// <summary>
    /// 解析に失敗した譜面パスです。
    /// </summary>
    public List<string> FailedPaths { get; } = [];

    /// <summary>
    /// 解析に要した時間です。
    /// </summary>
    public long ComputeMs { get; set; }

    /// <summary>
    /// ファイル読み取りに要した時間です。
    /// </summary>
    public long ReadMs { get; set; }

    /// <summary>
    /// 実際にファイル読み取りに成功した譜面数です。
    /// </summary>
    public int FileReadCount { get; set; }

    /// <summary>
    /// 実際に読み取った譜面ファイル bytes の合計です。
    /// </summary>
    public long FileReadBytes { get; set; }

    /// <summary>
    /// インメモリ解析に要した時間です。
    /// </summary>
    public long ParseMs { get; set; }

    /// <summary>
    /// 1譜面あたりの平均解析時間です。
    /// </summary>
    public long ParseAvgMs { get; set; }

    /// <summary>
    /// 解析時間の最大値です。
    /// </summary>
    public long ParseMaxMs { get; set; }

    /// <summary>
    /// 解析時間の95パーセンタイルです。
    /// </summary>
    public long ParseP95Ms { get; set; }

    /// <summary>
    /// DB 保存に要した時間です。
    /// </summary>
    public long DbCommitMs { get; set; }

    /// <summary>
    /// DB 保存 chunk 数です。
    /// </summary>
    public int CommitChunks { get; set; }

    /// <summary>
    /// 最も長かった DB 保存 chunk の時間です。
    /// </summary>
    public long DbCommitMaxChunkMs { get; set; }

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
