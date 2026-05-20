using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using BeMusicSeeker.Models;
using BeMusicSeeker.Models.BmsLibraryInternal;

namespace BeMusicSeeker.Tests;

internal static class ChartPackageTestExtensions
{
    private static readonly FieldInfo compatibilityAdapterField = typeof(PackageChartEntry).GetField("compatibilityAdapter", BindingFlags.Instance | BindingFlags.NonPublic);

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
        if (entry == null)
        {
            return null!;
        }
        if (entry.CompatibilityAdapter != null)
        {
            return entry.CompatibilityAdapter;
        }

        BMSFile bmsAdapter = entry.GetOrCreateBmsFormatAdapter();
        if (bmsAdapter != null)
        {
            compatibilityAdapterField?.SetValue(entry, bmsAdapter);
            return bmsAdapter;
        }

        ChartFile chart = entry.Chart;
        if (chart?.Kind != ChartFileKind.Bmson || chart.BmsonSong == null)
        {
            return null!;
        }

        PendingChartEntry bmsonAdapter = PendingChartEntry.CreateFromBmsonSong(chart.BmsonSong);
        if (bmsonAdapter == null)
        {
            return null!;
        }
        bmsonAdapter.ReplaceStructuredWarnings(chart.Warnings ?? []);
        bmsonAdapter.instl_dst = string.IsNullOrWhiteSpace(chart.InstallDestination) ? null : chart.InstallDestination;
        bmsonAdapter.InstallDestinationTitle = chart.InstallDestinationTitle ?? string.Empty;
        bmsonAdapter.InstallDestinationArtist = chart.InstallDestinationArtist ?? string.Empty;
        bmsonAdapter.InstallDestinationSuggestions = chart.InstallDestinationSuggestions ?? [];
        compatibilityAdapterField?.SetValue(entry, bmsonAdapter);
        return bmsonAdapter;
    }
}
