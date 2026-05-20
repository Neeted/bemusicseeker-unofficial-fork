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
        return ChartPackage.FromChartEntries((chartFiles ?? []).Select(PackageChartEntry.FromChartAdapter));
    }

    internal static ChartPackage CreatePackage(params PackageChartEntry[] entries)
    {
        return CreatePackage((IEnumerable<PackageChartEntry>)entries);
    }

    internal static ChartPackage CreatePackage(IEnumerable<PackageChartEntry> entries)
    {
        return ChartPackage.FromChartEntries(entries);
    }

    internal static List<BMSFile> MaterializeChartAdaptersForTest(this ChartPackage package)
    {
        return [.. (package?.ChartEntries ?? [])
            .Select(entry => entry?.GetOrCreateCompatibilityAdapter())
            .Where(file => file != null)];
    }

    internal static BMSFile GetOrCreateCompatibilityAdapter(this PackageChartEntry entry)
    {
        return entry?.Chart?.BmsFile!;
    }

    internal static BMSFile GetCompatibilityAdapterForTest(this PackageChartEntry entry)
    {
        return entry?.Chart?.BmsFile!;
    }
}
