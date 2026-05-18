using System;

namespace BeMusicSeeker.Models.BmsLibraryInternal;

internal sealed class PackageChartEntry
{
    internal PackageChartEntry(ChartFile chart, BMSFile compatibilityAdapter = null)
    {
        Chart = chart ?? throw new ArgumentNullException(nameof(chart));
        CompatibilityAdapter = compatibilityAdapter;
    }

    internal PackageChartEntry(BMSFile compatibilityAdapter)
        : this(ChartFileProjection.FromBmsFile(compatibilityAdapter), compatibilityAdapter)
    {
    }

    internal ChartFile Chart { get; }

    internal BMSFile CompatibilityAdapter { get; }

    internal ChartResourceSnapshot ResourceSnapshot => ChartResourceSnapshot.Create(Chart);

    internal static PackageChartEntry FromCompatibilityAdapter(BMSFile compatibilityAdapter)
    {
        return compatibilityAdapter == null ? null : new PackageChartEntry(compatibilityAdapter);
    }

    internal static PackageChartEntry FromChart(ChartFile chart)
    {
        return chart == null ? null : new PackageChartEntry(chart);
    }

    internal static PackageChartEntry FromPath(string filePath)
    {
        try
        {
            return FromCompatibilityAdapter(PendingChartEntry.CreateFromFilePath(filePath));
        }
        catch
        {
            return null;
        }
    }

}
