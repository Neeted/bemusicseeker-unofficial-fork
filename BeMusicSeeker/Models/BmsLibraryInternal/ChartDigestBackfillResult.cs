using System.Collections.Generic;

namespace BeMusicSeeker.Models.BmsLibraryInternal;

internal sealed class ChartDigestBackfillResult
{
    public int RequestedVersion { get; set; }

    public int TargetCount { get; set; }

    public int ProcessedCount { get; set; }

    public int FailedCount { get; set; }

    public int LoadedFromMapCount { get; set; }

    public int BackfilledCount { get; set; }

    public long LoadMapMs { get; set; }

    public long ComputeMs { get; set; }

    public long DbCommitMs { get; set; }

    public long TotalMs { get; set; }

    public List<string> FailedPaths { get; } = [];
}
