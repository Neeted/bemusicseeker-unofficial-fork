namespace BeMusicSeeker.Models.BmsLibraryInternal;

/// <summary>
/// maintenance 再スキャンの進捗を UI とログへ渡すための軽量 snapshot です。
/// section 単位でしか cancel しないため、処理済み件数も section 完了時に進みます。
/// </summary>
internal sealed class MaintenanceWorkflowProgress
{
    /// <summary>
    /// 全対象数です。
    /// </summary>
    public int TotalCount { get; set; }

    /// <summary>
    /// 処理済み対象数です。
    /// </summary>
    public int ProcessedCount { get; set; }

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
