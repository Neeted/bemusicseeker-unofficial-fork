using System;
using BeMusicSeeker.Models.BmsLibraryInternal;

namespace BeMusicSeeker.Models;

internal sealed class PlaylistSyncProgressSnapshot
{
    /// <summary>独立して実行できる進捗の生産元を識別します。</summary>
    public string Source { get; set; } = "playlist";

    /// <summary>対応する要求の発生元。開始・更新・終端で同じ値を渡します。</summary>
    internal OperationProgressRequest Request { get; set; }

    public bool IsActive { get; set; }

    public long OperationId { get; set; }

    public int TotalTableCount { get; set; }

    public int CompletedTableCount { get; set; }

    public string CurrentTableName { get; set; }

    public Uri CurrentUri { get; set; }

    public string LabelFormat { get; set; }

    public string SingleLabel { get; set; }
}
