using System.Collections.Generic;
using System.Linq;
using BeMusicSeeker.Models;
using BeMusicSeeker.Models.BmsLibraryInternal;

namespace BeMusicSeeker.Tests;

internal static class ChartPackageTestExtensions
{
    internal static ChartPackage CreatePackage(params ChartFile[] chartFiles)
    {
        return CreatePackage((IEnumerable<ChartFile>)chartFiles);
    }

    internal static ChartPackage CreatePackage(IEnumerable<ChartFile> chartFiles)
    {
        return ChartPackage.FromChartEntries((chartFiles ?? []).Select(CreateEntry));
    }

    internal static PackageChartEntry CreateEntry(ChartFile chartFile)
    {
        return PackageChartEntry.FromChart((chartFile));
    }

    internal static PackageChartEntry CreateEntryWithInstallDestination(
        ChartFile chartFile,
        string installDestination,
        string title = "",
        string artist = "",
        IReadOnlyList<string>? suggestions = null)
    {
        ChartFile chart = (chartFile);
        return PackageChartEntry.FromChart(ChartFileProjection.WithPackageState(
            chart,
            installDestination,
            title,
            artist,
            suggestions ?? [],
            chart.Warnings));
    }

    internal static ChartPackage CreatePackage(params PackageChartEntry[] entries)
    {
        return CreatePackage((IEnumerable<PackageChartEntry>)entries);
    }

    internal static ChartPackage CreatePackage(IEnumerable<PackageChartEntry> entries)
    {
        return ChartPackage.FromChartEntries(entries);
    }

    internal static List<ChartFile> GetBmsChartsForTest(this ChartPackage package)
    {
        return [.. (package?.ChartEntries ?? [])
            .Select(entry => entry?.GetBmsChartForTest())
            .OfType<ChartFile>()];
    }

    internal static ChartFile GetBmsChartForTest(this PackageChartEntry entry)
    {
        return entry?.Chart?.Kind == ChartFileKind.Bms ? entry.Chart : null!;
    }
}
