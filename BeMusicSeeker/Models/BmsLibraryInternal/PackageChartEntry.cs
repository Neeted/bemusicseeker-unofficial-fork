using System;

namespace BeMusicSeeker.Models.BmsLibraryInternal;

internal sealed class PackageChartEntry
{
    private BMSFile compatibilityAdapter;

    internal PackageChartEntry(ChartFile chart, BMSFile compatibilityAdapter = null)
    {
        Chart = chart ?? throw new ArgumentNullException(nameof(chart));
        this.compatibilityAdapter = compatibilityAdapter;
    }

    internal PackageChartEntry(BMSFile compatibilityAdapter)
        : this(ChartFileProjection.FromBmsFile(compatibilityAdapter), compatibilityAdapter)
    {
    }

    internal ChartFile Chart { get; }

    internal BMSFile CompatibilityAdapter => compatibilityAdapter;

    internal ChartResourceSnapshot ResourceSnapshot => ChartResourceSnapshot.Create(Chart);

    internal static PackageChartEntry FromCompatibilityAdapter(BMSFile compatibilityAdapter)
    {
        return compatibilityAdapter == null ? null : new PackageChartEntry(compatibilityAdapter);
    }

    internal static PackageChartEntry FromChart(ChartFile chart)
    {
        return chart == null ? null : new PackageChartEntry(chart);
    }

    internal BMSFile GetOrCreateCompatibilityAdapter()
    {
        if (compatibilityAdapter != null)
        {
            return compatibilityAdapter;
        }
        if (Chart.Kind == ChartFileKind.Bmson && Chart.BmsonSong != null)
        {
            compatibilityAdapter = PendingChartEntry.CreateFromBmsonSong(Chart.BmsonSong);
        }
        return compatibilityAdapter;
    }

    internal static PackageChartEntry FromPath(string filePath)
    {
        try
        {
            if (PendingChartEntry.IsBmsonFilePath(filePath))
            {
                return FromChart(ChartFileProjection.FromBmsonSong(BmsonSongParser.Parse(filePath)));
            }
            return FromCompatibilityAdapter(PendingChartEntry.CreateFromFilePath(filePath));
        }
        catch
        {
            return null;
        }
    }

}
