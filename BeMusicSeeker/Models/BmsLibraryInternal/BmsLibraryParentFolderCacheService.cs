using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using BeMusicSeeker.Models.Utils;

namespace BeMusicSeeker.Models.BmsLibraryInternal;

internal sealed class BmsLibraryParentFolderCacheService
{
    public List<string> BuildParentFolderCandidates(IEnumerable<string> bmsDirectories, IEnumerable<string> installedChartPaths, BmsLibraryOptionsSnapshot options)
    {
        List<string> chartPaths = [.. (installedChartPaths ?? []).Where(path => !string.IsNullOrWhiteSpace(path))];
        return [.. (bmsDirectories ?? []).Where(delegate (string directoryPath)
        {
            if (options?.OperationModeLR2DB == true)
            {
                IReadOnlyList<string> customFolderOutputBases = CustomFolderOutputBaseRegistry.NormalizeBaseDirectories(
                    new[] { options.LR2CustomFolderOutputBaseDir }
                        .Concat(options.LR2CustomFolderAdditionalOutputBaseDirs ?? [])
                        .Append(options.LR2CustomFolderOutputBaseDirRootType));
                string normalizedDirectoryPath = CustomFolderOutputBaseRegistry.NormalizeDirectoryPath(directoryPath);
                if (!string.IsNullOrWhiteSpace(normalizedDirectoryPath)
                    && customFolderOutputBases.Any(outputBase => IsSameOrChildPath(normalizedDirectoryPath, outputBase)))
                {
                    return false;
                }
            }
            if (chartPaths.Any(delegate (string chartPath)
            {
                return chartPath.StartsWith(directoryPath + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase);
            }))
            {
                return true;
            }
            if (options?.OperationModeLR2DB == true)
            {
                try
                {
                    return !LongPathFileSystem.EnumerateFiles(directoryPath, "*.lr2folder", SearchOption.AllDirectories).Any();
                }
                catch
                {
                    return false;
                }
            }
            return true;
        })];
    }

    private static bool IsSameOrChildPath(string candidate, string parent)
    {
        if (string.IsNullOrWhiteSpace(candidate) || string.IsNullOrWhiteSpace(parent))
        {
            return false;
        }
        if (string.Equals(candidate, parent, StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        return candidate.StartsWith(parent + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase);
    }

    public BMSLibrary.ParentFolderListCacheSnapshot BuildSnapshot(int version, IEnumerable<string> installedChartPaths, IEnumerable<string> bmsDirectories, BmsLibraryOptionsSnapshot options)
    {
        var stopwatch = Stopwatch.StartNew();
        List<string> parentFolders = BuildParentFolderCandidates(bmsDirectories, installedChartPaths, options);
        stopwatch.Stop();
        return new BMSLibrary.ParentFolderListCacheSnapshot
        {
            Version = version,
            RebuildMs = stopwatch.ElapsedMilliseconds,
            ParentFolders = parentFolders
        };
    }
}
