using System.Collections.Generic;

namespace BeMusicSeeker.Models.BmsLibraryInternal;

internal sealed class LibraryRemovalResult
{
    public List<BMSFile> RemovedFiles { get; } = new List<BMSFile>();

    public List<LibraryChartRef> RemovedCharts { get; } = new List<LibraryChartRef>();

    public List<LibraryDeleteFailure> Failures { get; } = new List<LibraryDeleteFailure>();
}
