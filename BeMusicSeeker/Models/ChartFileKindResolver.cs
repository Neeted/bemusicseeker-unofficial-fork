using System;
using System.IO;
using System.Linq;

namespace BeMusicSeeker.Models;

internal static class ChartFileKindResolver
{
    internal static readonly string[] BmsExtensions = [".bme", ".bms", ".bml", ".pms"];

    internal static readonly string[] BmsonExtensions = [".bmson"];

    internal static readonly string[] ChartExtensions = [.. BmsExtensions.Concat(BmsonExtensions).Distinct(StringComparer.OrdinalIgnoreCase)];

    internal static bool IsBmsonFilePath(string filePath)
    {
        string extension = Path.GetExtension(filePath);
        return !string.IsNullOrWhiteSpace(extension) && BmsonExtensions.Contains(extension, StringComparer.OrdinalIgnoreCase);
    }

    internal static bool IsSupportedChartFilePath(string filePath)
    {
        string extension = Path.GetExtension(filePath);
        if (string.IsNullOrWhiteSpace(extension))
        {
            return false;
        }
        return BmsExtensions.Contains(extension, StringComparer.OrdinalIgnoreCase)
            || BmsonExtensions.Contains(extension, StringComparer.OrdinalIgnoreCase);
    }

    internal static bool IsBmsChartFile(BMSFile file)
    {
        return file != null;
    }

    internal static bool IsBmsChartFile(ChartFile chart)
    {
        return IsBmsChartFile(chart?.GetBmsStorageOwner());
    }
}
