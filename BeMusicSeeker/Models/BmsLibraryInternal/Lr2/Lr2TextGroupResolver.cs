using System;
using System.IO;
using System.Linq;
using BeMusicSeeker.Models.Utils;

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
                : LongPathFileSystem.NormalizePathForStorage(directory);
        }
        catch (Exception ex) when (ex is ArgumentException || ex is NotSupportedException || ex is PathTooLongException)
        {
            return null;
        }
    }

    private static bool HasDirectTextFile(string directory)
    {
        if (string.IsNullOrWhiteSpace(directory) || !LongPathFileSystem.DirectoryExists(directory))
        {
            return false;
        }
        try
        {
            return LongPathFileSystem.EnumerateFiles(directory, "*.txt", SearchOption.TopDirectoryOnly).Any();
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
