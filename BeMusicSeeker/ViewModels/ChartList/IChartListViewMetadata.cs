namespace BeMusicSeeker.ViewModels;

internal interface IChartListViewMetadata
{
    int RowCount { get; }

    int DistinctFolderCount { get; }

    int RealizedRowCount { get; }

    void DisposeRealizedRows();
}
