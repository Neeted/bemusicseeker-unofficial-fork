using System.Collections.Generic;
using BeMusicSeeker.Models.LR2;

namespace BeMusicSeeker.Models.BmsLibraryInternal;

internal sealed class ChartInfoHydrationLoadResult
{
    public Dictionary<string, LR2SongDBExtended.chart_info> ChartInfoBySha256 { get; } =
        new Dictionary<string, LR2SongDBExtended.chart_info>(System.StringComparer.OrdinalIgnoreCase);

    public HashSet<string> CurrentChartInfoSha256s { get; } =
        new HashSet<string>(System.StringComparer.OrdinalIgnoreCase);

    public HashSet<string> CurrentParseFailureMd5s { get; } =
        new HashSet<string>(System.StringComparer.OrdinalIgnoreCase);

    public int ChartInfoRows { get; set; }

    public int ParseFailureRows { get; set; }

    public long DbReadMs { get; set; }

    public long MaterializeMs { get; set; }

    public bool ReadOnly { get; set; }

    public long DbLockWaitMs { get; set; }
}
