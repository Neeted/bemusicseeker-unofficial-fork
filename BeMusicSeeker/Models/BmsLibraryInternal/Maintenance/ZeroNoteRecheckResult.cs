using System.Collections.Generic;

namespace BeMusicSeeker.Models.BmsLibraryInternal;

internal sealed class ZeroNoteRecheckResult
{
    /// <summary>確定後に所持集合へ適用する共通現在値です。</summary>
    internal List<ChartFile> ChangedCharts { get; } = [];

    public int Total { get; set; }

    public int MismatchCount { get; set; }

    public int ClearedCount { get; set; }

    public int SkippedCount { get; set; }

    public int ChangedCount { get; set; }
}
