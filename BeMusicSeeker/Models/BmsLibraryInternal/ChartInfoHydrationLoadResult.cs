using System.Collections.Generic;
using BeMusicSeeker.Models.LR2;

namespace BeMusicSeeker.Models.BmsLibraryInternal;

internal sealed class ChartInfoHydrationLoadResult
{
    public Dictionary<string, LR2SongDBExtended.chart_info> ChartInfoBySha256 { get; } =
        new Dictionary<string, LR2SongDBExtended.chart_info>(System.StringComparer.OrdinalIgnoreCase);

    public Dictionary<string, LR2SongDBExtended.chart_info_parse_failure> CurrentParseFailuresByMd5 { get; } =
        new Dictionary<string, LR2SongDBExtended.chart_info_parse_failure>(System.StringComparer.OrdinalIgnoreCase);

    public int ChartInfoRows { get; set; }

    public int ParseFailureRows { get; set; }

    public long DbReadMs { get; set; }

    public long MaterializeMs { get; set; }
}
