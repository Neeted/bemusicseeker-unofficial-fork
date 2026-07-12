using System;
using System.Collections.Generic;
using System.Linq;
using BeMusicSeeker.Models.Utils;

namespace BeMusicSeeker.Models;

/// <summary>
/// standalone mode の BMS root 設定を永続化形式から読み書きする adapter です。
/// </summary>
internal static class StandaloneBmsRootPathSettings
{
    internal static IReadOnlyList<string> Deserialize(
        string serializedPaths,
        string legacyBmsRootPath = null)
    {
        List<string> paths = [];
        if (!string.IsNullOrWhiteSpace(serializedPaths))
        {
            paths.AddRange(serializedPaths.Split(["\r\n", "\n", "\r"], StringSplitOptions.RemoveEmptyEntries));
        }
        if (paths.Count == 0
            && !string.IsNullOrWhiteSpace(legacyBmsRootPath)
            && LongPathFileSystem.DirectoryExists(legacyBmsRootPath))
        {
            paths.Add(legacyBmsRootPath);
        }
        return Normalize(paths);
    }

    internal static IReadOnlyList<string> Normalize(IEnumerable<string> paths)
    {
        if (paths == null)
        {
            return [];
        }

        List<string> normalized = [];
        foreach (string path in paths)
        {
            if (string.IsNullOrWhiteSpace(path))
            {
                continue;
            }
            string fullPath;
            try
            {
                fullPath = LongPathFileSystem.NormalizePathForStorage(path.Trim());
            }
            catch
            {
                continue;
            }
            fullPath = LongPathFileSystem.TrimTrailingDirectorySeparators(fullPath);
            if (!LongPathFileSystem.DirectoryExists(fullPath))
            {
                continue;
            }
            if (!normalized.Contains(fullPath, StringComparer.OrdinalIgnoreCase))
            {
                normalized.Add(fullPath);
            }
        }

        return normalized;
    }

    internal static string Serialize(IEnumerable<string> paths)
    {
        return string.Join(Environment.NewLine, Normalize(paths));
    }
}
