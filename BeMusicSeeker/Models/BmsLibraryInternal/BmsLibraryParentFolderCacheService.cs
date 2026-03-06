using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;

namespace BeMusicSeeker.Models.BmsLibraryInternal;

internal sealed class BmsLibraryParentFolderCacheService
{
    public List<string> BuildParentFolderCandidates(IEnumerable<string> bmsDirectories, List<BMSFile> bmsFilesSnapshot, BmsLibraryOptionsSnapshot options)
    {
        return (bmsDirectories ?? Enumerable.Empty<string>()).Where(delegate (string directoryPath)
        {
            if ((bmsFilesSnapshot ?? new List<BMSFile>()).Any(delegate (BMSFile file)
            {
                return file != null && !string.IsNullOrWhiteSpace(file.path) && file.path.StartsWith(directoryPath + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase);
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
            throw new NotImplementedException();
        }).ToList();
    }

    public BMSLibrary.ParentFolderListCacheSnapshot BuildSnapshot(int version, List<BMSFile> bmsFilesSnapshot, IEnumerable<string> bmsDirectories, BmsLibraryOptionsSnapshot options)
    {
        Stopwatch stopwatch = Stopwatch.StartNew();
        List<string> parentFolders = BuildParentFolderCandidates(bmsDirectories, bmsFilesSnapshot, options);
        stopwatch.Stop();
        return new BMSLibrary.ParentFolderListCacheSnapshot
        {
            Version = version,
            RebuildMs = stopwatch.ElapsedMilliseconds,
            ParentFolders = parentFolders
        };
    }
}
