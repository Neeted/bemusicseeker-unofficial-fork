using System.Collections.Generic;
using BeMusicSeeker.Models.LR2;

namespace BeMusicSeeker.Models.BmsLibraryInternal;

internal sealed class ChartInfoInlineBuildResult
{
    public List<LibraryChartHashChange> HashChanges { get; } = [];

    public List<LR2SongDBExtended.chart_info> ChartInfoRows { get; } = [];

    public List<LR2SongDBExtended.chart_info> AppliedRows { get; } = [];

    public List<LR2SongDBExtended.chart_info_parse_failure> ParseFailureRows { get; } = [];

    public List<string> ParseFailureDeleteMd5s { get; } = [];

    public int TargetCount { get; set; }

    public int SuccessCount { get; set; }

    public int CurrentSkippedCount { get; set; }

    public int FailureSkippedCount { get; set; }

    public int ParseFailedCount { get; set; }

    public int FailurePersistedCount { get; set; }

    public int FailureClearedCount { get; set; }

    public int ReadFailedCount { get; set; }

    public long ParseMs { get; set; }
}
