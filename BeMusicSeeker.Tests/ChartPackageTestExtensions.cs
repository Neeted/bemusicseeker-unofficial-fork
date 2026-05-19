using System.Collections.Generic;
using System.Linq;
using BeMusicSeeker.Models;
using BeMusicSeeker.Models.BmsLibraryInternal;

namespace BeMusicSeeker.Tests;

internal static class ChartPackageTestExtensions
{
    internal static List<BMSFile> MaterializeChartAdaptersForTest(this ChartPackage package)
    {
        return [.. (package?.ChartEntries ?? [])
            .Select(entry => entry?.GetOrCreateCompatibilityAdapter())
            .Where(file => file != null)];
    }
}
