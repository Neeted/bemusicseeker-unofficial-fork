using System.Collections.Generic;

namespace BeMusicSeeker.Models.BmsLibraryInternal;

/// <summary>
/// 重複 chart の解析結果です。
/// duplicate warning 反映対象の chart と、duplicate tree に表示する chart group を分けて保持します。
/// </summary>
internal sealed class DuplicateAnalysisResult
{
    public HashSet<ChartFile> DuplicateCharts { get; } = [];

    public List<DuplicateGroup> DuplicateGroups { get; } = [];

    public int MaterializedChartCount { get; set; }

    public int ConnectedDirectoryCount { get; set; }
}
