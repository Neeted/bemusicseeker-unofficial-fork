using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace BeMusicSeeker.Models.BmsLibraryInternal;

internal static class Lr2TextGroupResolver
{
    internal static int ResolveFlag(string chartPath, int fallback = 0)
    {
        string directory = GetChartDirectory(chartPath);
        if (string.IsNullOrWhiteSpace(directory))
        {
            return NormalizeFlag(fallback);
        }
        return HasDirectTextFile(directory) ? 1 : 0;
    }

    internal static IReadOnlyList<string> CreateTextFileDirectories(IEnumerable<string> chartPaths)
    {
        var directories = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (string chartPath in chartPaths ?? [])
        {
            string directory = GetChartDirectory(chartPath);
            if (string.IsNullOrWhiteSpace(directory))
            {
                continue;
            }
            if (HasDirectTextFile(directory))
            {
                directories.Add(directory);
            }
        }
        return [.. directories.OrderBy(path => path, StringComparer.OrdinalIgnoreCase)];
    }

    private static string GetChartDirectory(string chartPath)
    {
        if (string.IsNullOrWhiteSpace(chartPath))
        {
            return null;
        }
        try
        {
            string directory = Path.GetDirectoryName(chartPath);
            return string.IsNullOrWhiteSpace(directory)
                ? null
                : Path.GetFullPath(directory);
        }
        catch (Exception ex) when (ex is ArgumentException || ex is NotSupportedException || ex is PathTooLongException)
        {
            return null;
        }
    }

    private static bool HasDirectTextFile(string directory)
    {
        if (string.IsNullOrWhiteSpace(directory) || !Directory.Exists(directory))
        {
            return false;
        }
        try
        {
            return Directory.EnumerateFiles(directory, "*.txt", SearchOption.TopDirectoryOnly).Any();
        }
        catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException || ex is ArgumentException || ex is NotSupportedException || ex is PathTooLongException)
        {
            return false;
        }
    }

    private static int NormalizeFlag(int value)
    {
        return value == 0 ? 0 : 1;
    }
}
