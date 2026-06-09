namespace BeMusicSeeker.Models.BmsLibraryInternal;

/// <summary>
/// maintenance 再スキャンの進捗を UI とログへ渡すための軽量 snapshot です。
/// </summary>
internal sealed class MaintenanceWorkflowProgress
{
    /// <summary>
    /// 全対象数です。
    /// </summary>
    public int TotalCount { get; set; }

    /// <summary>
    /// UI に表示する処理済み対象数です。大量 pipeline では evaluator 完了件数を入れます。
    /// </summary>
    public int ProcessedCount { get; set; }

    /// <summary>
    /// digest / resource health / encoding などの evaluator が完了した件数です。
    /// </summary>
    public int EvaluatedCount { get; set; }

    /// <summary>
    /// DB 反映など writer 側まで完了した件数です。
    /// </summary>
    public int CompletedCount { get; set; }

    /// <summary>
    /// 現在処理中または直近で処理した譜面 path です。
    /// </summary>
    public string CurrentPath { get; set; } = string.Empty;

    /// <summary>
    /// 処理が完了したかどうかです。
    /// </summary>
    public bool IsCompleted { get; set; }

    /// <summary>
    /// ユーザー操作により中断されたかどうかです。
    /// </summary>
    public bool IsCanceled { get; set; }
}
