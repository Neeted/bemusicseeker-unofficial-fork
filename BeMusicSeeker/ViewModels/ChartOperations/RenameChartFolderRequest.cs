using System;
using BeMusicSeeker.Models;

namespace BeMusicSeeker.ViewModels;

internal sealed class RenameChartFolderRequest
{
    private RenameChartFolderRequest(ChartFile chart)
    {
        Chart = chart ?? throw new ArgumentNullException(nameof(chart));
    }

    internal ChartFile Chart { get; }

    internal bool HasTarget => Chart != null;

    internal static bool TryCreate(ChartOperationTarget target, out RenameChartFolderRequest request)
    {
        request = null;
        if (target?.HasCapability(ChartOperationCapabilities.MoveInLibrary) != true
            || string.IsNullOrWhiteSpace(target.Chart?.Path))
        {
            return false;
        }

        request = new RenameChartFolderRequest(target.Chart);
        return true;
    }
}
