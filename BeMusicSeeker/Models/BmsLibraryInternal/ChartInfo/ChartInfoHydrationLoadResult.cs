using System.Collections.Generic;

namespace BeMusicSeeker.Models.BmsLibraryInternal;

internal sealed class ChartInfoHydrationLoadResult
{
    public Dictionary<string, BeMusicSeeker.Models.ChartDetails> ChartInfoBySha256 { get; } =
        new Dictionary<string, BeMusicSeeker.Models.ChartDetails>(System.StringComparer.OrdinalIgnoreCase);

    public HashSet<string> CurrentChartInfoSha256s { get; } =
        new HashSet<string>(System.StringComparer.OrdinalIgnoreCase);

    public HashSet<string> CurrentParseFailureMd5s { get; } =
        new HashSet<string>(System.StringComparer.OrdinalIgnoreCase);

    public int ChartInfoRows { get; set; }

    public int ParseFailureRows { get; set; }

    public string MaterializeMode { get; set; } = string.Empty;

    public int RawRows { get; set; }

    public long RawReadMs { get; set; }

    public long RawObjectMs { get; set; }

    public long DbReadMs { get; set; }

    public long MaterializeMs { get; set; }

    public bool ReadOnly { get; set; }

    public long DbLockWaitMs { get; set; }
}
