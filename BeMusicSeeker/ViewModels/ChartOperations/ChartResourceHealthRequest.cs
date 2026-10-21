using System;
using System.Collections.Generic;
using System.Linq;
using BeMusicSeeker.Models;
using BeMusicSeeker.Models.BmsLibraryInternal;

namespace BeMusicSeeker.ViewModels;

internal sealed class ChartResourceHealthRequest
{
    private ChartResourceHealthRequest(IReadOnlyList<ChartFile> charts, IReadOnlyList<PackageChartEntry> pendingEntries)
    {
        Charts = charts ?? throw new ArgumentNullException(nameof(charts));
        PendingEntries = pendingEntries;
    }

    internal IReadOnlyList<ChartFile> Charts { get; }

    /// <summary>選択された保留項目の参照です。再調査結果を同じ現在値と表示へ届けます。</summary>
    internal IReadOnlyList<PackageChartEntry> PendingEntries { get; }

    internal bool HasTargets => Charts.Count > 0;

    internal static bool TryCreate(IEnumerable<ChartOperationTarget> targets, out ChartResourceHealthRequest request)
    {
        request = null;
        if (targets == null)
        {
            return false;
        }

        ChartOperationTarget[] eligible = [.. targets
            .Where(target => target?.Chart != null
                && target.HasCapability(ChartOperationCapabilities.RunResourceHealthCheck))];
        List<ChartFile> charts = [.. eligible.Select(target => target.Chart)];
        if (charts.Count == 0)
        {
            return false;
        }

        request = new ChartResourceHealthRequest(charts, [.. eligible.Where(target => target.IsPending && target.PackageEntry != null)
            .Select(target => target.PackageEntry).Distinct()]);
        return true;
    }
}
