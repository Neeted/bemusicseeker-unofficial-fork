using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using Microsoft.VisualStudio.TestTools.UnitTesting;
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
        BMSFile existingAdapter = entry.GetCompatibilityAdapterForTest();
        if (existingAdapter != null)
        {
            return existingAdapter;
        }

        BMSFile? bmsAdapter = entry.Chart?.BmsFile;
        if (bmsAdapter != null)
        {
            compatibilityAdapterField?.SetValue(entry, bmsAdapter);
            return bmsAdapter;
        }

        return null!;
    }

    internal static BMSFile GetCompatibilityAdapterForTest(this PackageChartEntry entry)
    {
        Assert.IsNotNull(compatibilityAdapterField, "PackageChartEntry compatibility adapter field was not found.");
        return entry == null ? null! : (BMSFile)compatibilityAdapterField.GetValue(entry)!;
    }
}
