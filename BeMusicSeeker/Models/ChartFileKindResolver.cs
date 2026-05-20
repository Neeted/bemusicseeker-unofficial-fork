using System;
using System.IO;
using System.Linq;

namespace BeMusicSeeker.Models;

internal static class ChartFileKindResolver
{
    internal static readonly string[] BmsonExtensions = [".bmson"];

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
        return BMSFile.bmsExtensions.Contains(extension, StringComparer.OrdinalIgnoreCase)
            || BmsonExtensions.Contains(extension, StringComparer.OrdinalIgnoreCase);
    }

    internal static bool IsBmsonChartFile(BMSFile file)
    {
        return file is PendingChartEntry pending && pending.IsBmsonChart;
    }

    internal static bool IsBmsChartFile(BMSFile file)
    {
        return file != null && !IsBmsonChartFile(file);
    }
}
