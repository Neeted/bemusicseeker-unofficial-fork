using System;
using System.Collections.Generic;
using System.Linq;
using BeMusicSeeker.Models.BmsLibraryInternal;

namespace BeMusicSeeker.ViewModels;

internal sealed class ChartLibraryMoveRequest
{
    private ChartLibraryMoveRequest(IReadOnlyList<LibraryChartRef> charts, string newParentDirectory, FolderMoveBatch preparedBatch = null)
    {
        Charts = charts ?? throw new ArgumentNullException(nameof(charts));
        NewParentDirectory = newParentDirectory;
        PreparedBatch = preparedBatch;
    }

    internal IReadOnlyList<LibraryChartRef> Charts { get; }

    internal string NewParentDirectory { get; }

    /// <summary>同じL内でモデルが固定した診断計画と実移動。未準備の入力要求ではnullです。</summary>
    internal FolderMoveBatch PreparedBatch { get; }

    /// <summary>入力を変えず、停止前に確定した物理計画を持つ要求を返します。</summary>
    internal ChartLibraryMoveRequest WithPreparedBatch(FolderMoveBatch batch)
        => new(Charts, NewParentDirectory, batch ?? throw new ArgumentNullException(nameof(batch)));

    internal bool HasTargets => Charts.Count > 0;

    internal static bool TryCreate(IEnumerable<ChartOperationTarget> targets, string newParentDirectory, out ChartLibraryMoveRequest request)
    {
        request = null;
        if (targets == null)
        {
            return false;
        }

        List<LibraryChartRef> charts = [.. targets
            .Where(target => target?.Chart != null
                && target.HasCapability(ChartOperationCapabilities.MoveInLibrary)
                && !string.IsNullOrWhiteSpace(target.Chart.Path))
            .Select(target => target.ToLibraryChartRef())
            .Where(chart => !string.IsNullOrWhiteSpace(chart?.Path))];
        if (charts.Count == 0)
        {
            return false;
        }

        request = new ChartLibraryMoveRequest(charts, newParentDirectory);
        return true;
    }
}
