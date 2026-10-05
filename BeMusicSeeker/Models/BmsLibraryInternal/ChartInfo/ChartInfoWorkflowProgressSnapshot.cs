namespace BeMusicSeeker.Models.BmsLibraryInternal;

/// <summary>一つの譜面情報要求が送出した件数と対象をまとめた変更不能な表示値です。</summary>
internal sealed class ChartInfoWorkflowProgressSnapshot
{
    /// <summary>workerが捕捉した要求版と、その処理の進捗から値を構築します。</summary>
    internal ChartInfoWorkflowProgressSnapshot(int requestVersion, int totalCount, int processedCount, string currentPath)
    {
        RequestVersion = requestVersion;
        TotalCount = totalCount;
        ProcessedCount = processedCount;
        CurrentPath = currentPath ?? string.Empty;
    }

    /// <summary>件数の送出元である既存要求の版です。受信時の最新要求で付け直しません。</summary>
    internal int RequestVersion { get; }

    /// <summary>確認・補完または読込みの対象数です。未確定では0です。</summary>
    internal int TotalCount { get; }

    /// <summary>処理済みまたはメモリ適用済みの件数です。保存成功は既存完了版で判断します。</summary>
    internal int ProcessedCount { get; }

    /// <summary>処理中の対象です。対象を持たない読込みでは空文字です。</summary>
    internal string CurrentPath { get; }
}
