using System.Collections.Generic;
using System.Linq;
using BeMusicSeeker.Models;
using BeMusicSeeker.Models.BmsLibraryInternal;

namespace BeMusicSeeker.Tests;

internal static class ChartPackageTestExtensions
{
    internal static ChartPackage CreatePackage(params BMSFile[] chartFiles)
    {
        return CreatePackage((IEnumerable<BMSFile>)chartFiles);
    }

    internal static ChartPackage CreatePackage(IEnumerable<BMSFile> chartFiles)
    {
        return ChartPackage.FromChartEntries((chartFiles ?? []).Select(CreateEntry));
    }

    internal static PackageChartEntry CreateEntry(BMSFile chartFile)
    {
        return PackageChartEntry.FromChart(ChartFileProjection.FromBmsFile(chartFile));
    }

    internal static PackageChartEntry CreateEntryWithInstallDestination(
        BMSFile chartFile,
        string installDestination,
        string title = "",
        string artist = "",
        IReadOnlyList<string>? suggestions = null)
    {
        ChartFile chart = ChartFileProjection.FromBmsFile(chartFile, includeWarningSnapshot: true);
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

    internal static List<BMSFile> GetBmsOwnersForTest(this ChartPackage package)
    {
        return [.. (package?.ChartEntries ?? [])
            .Select(entry => entry?.GetBmsOwnerForTest())
            .Where(file => file != null)];
    }

    internal static BMSFile GetBmsOwnerForTest(this PackageChartEntry entry)
    {
        return entry?.Chart?.GetBmsStorageOwner()!;
    }
}
