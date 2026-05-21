using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;

namespace BeMusicSeeker.Models.BmsLibraryInternal;

internal sealed class BmsLibraryParentFolderCacheService
{
    public List<string> BuildParentFolderCandidates(IEnumerable<string> bmsDirectories, IEnumerable<ChartFile> installedChartsSnapshot, BmsLibraryOptionsSnapshot options)
    {
        return [.. (bmsDirectories ?? []).Where(delegate (string directoryPath)
        {
            if ((installedChartsSnapshot ?? []).Any(delegate (ChartFile chart)
            {
                return chart != null && !string.IsNullOrWhiteSpace(chart.Path) && chart.Path.StartsWith(directoryPath + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase);
            }))
            {
                return true;
            }
            if (options.OperationModeLR2DB)
            {
                if ((directoryPath + Path.DirectorySeparatorChar).StartsWith(options.LR2CustomFolderOutputBaseDir + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
                {
                    return false;
                }
                if ((directoryPath + Path.DirectorySeparatorChar).StartsWith(options.LR2CustomFolderOutputBaseDirRootType + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
                {
                    return false;
                }
                try
                {
                    return !Directory.EnumerateFiles(directoryPath, "*.lr2folder", SearchOption.AllDirectories).Any();
                }
                catch
                {
                    return false;
                }
            }
            return true;
        })];
    }

    public BMSLibrary.ParentFolderListCacheSnapshot BuildSnapshot(int version, IEnumerable<ChartFile> installedChartsSnapshot, IEnumerable<string> bmsDirectories, BmsLibraryOptionsSnapshot options)
    {
        var stopwatch = Stopwatch.StartNew();
        List<string> parentFolders = BuildParentFolderCandidates(bmsDirectories, installedChartsSnapshot, options);
        stopwatch.Stop();
        return new BMSLibrary.ParentFolderListCacheSnapshot
        {
            Version = version,
            RebuildMs = stopwatch.ElapsedMilliseconds,
            ParentFolders = parentFolders
        };
    }
}
