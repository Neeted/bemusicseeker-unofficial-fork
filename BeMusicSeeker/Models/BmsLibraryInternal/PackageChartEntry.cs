namespace BeMusicSeeker.Models.BmsLibraryInternal;

internal sealed class PackageChartEntry
{
    internal PackageChartEntry(BMSFile compatibilityAdapter)
    {
        CompatibilityAdapter = compatibilityAdapter;
        Chart = ChartFileProjection.FromBmsFile(compatibilityAdapter);
    }

    internal ChartFile Chart { get; }

    internal BMSFile CompatibilityAdapter { get; }

    internal ChartResourceSnapshot ResourceSnapshot => ChartResourceSnapshot.Create(Chart);

    internal static PackageChartEntry FromCompatibilityAdapter(BMSFile compatibilityAdapter)
    {
        return compatibilityAdapter == null ? null : new PackageChartEntry(compatibilityAdapter);
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
