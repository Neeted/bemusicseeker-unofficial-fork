using System.Collections.Generic;
using System.Diagnostics;
using System.Threading;
using BeMusicSeeker.Models;

namespace BeMusicSeeker.ViewModels;

internal class PlaylistDetailPresentationRequest
{
    internal PlaylistBuildRequest BuildRequest { get; set; }
    internal MainViewUpdateMode Mode { get; set; }
    internal string KeywordFilter { get; set; }
    internal ChartModeFilter ModeFilter { get; set; }
    internal ChartListSortParameters SortParameters { get; set; }
    internal MainViewUpdateMode ColumnSettingMode { get; set; }
    internal MainViewUpdateMode CurrentTreeMode { get; set; }
    internal Stopwatch Stopwatch { get; set; }
    internal CancellationToken CancellationToken { get; set; }
}

internal sealed class PlaylistDetailRebuiltPresentationRequest : PlaylistDetailPresentationRequest
{
    internal List<PlaylistDetailSourceRow> SourceRows { get; set; }
    internal BMSTable CurrentTable { get; set; }
    internal string CurrentFolderName { get; set; }
    internal PlaylistDetailFilter CurrentFilterType { get; set; }
}
