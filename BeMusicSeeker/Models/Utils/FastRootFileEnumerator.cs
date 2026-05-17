using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace BeMusicSeeker.Models.Utils;

internal sealed class FastRootFileEnumerator : IRootFileEnumerator
{
    public RootFileEnumerationResult EnumerateFiles(IEnumerable<string> rootDirectories, IEnumerable<RootFileEnumerationGroup> groups, bool verboseLog = false)
    {
        var result = new RootFileEnumerationResult
        {
            BackendName = "fast"
        };

        List<string> roots = NormalizeRoots(rootDirectories);
        List<RootFileEnumerationGroup> groupList = [.. (groups ?? []).Where(group => group != null && !string.IsNullOrWhiteSpace(group.Name))];
        foreach (RootFileEnumerationGroup group in groupList)
        {
            result.PathsByGroup[group.Name] = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            result.QueryMsByGroup[group.Name] = 0L;
            result.QueryHitCountByGroup[group.Name] = 0UL;
        }

        if (roots.Count == 0 || groupList.Count == 0)
        {
            result.Success = true;
            return result;
        }

        var stopwatch = System.Diagnostics.Stopwatch.StartNew();
        try
        {
            var allFiles = new HashSet<string>(
                roots
                    .AsParallel()
                    .SelectMany(EnumerateAllFilesForRoot)
                    .Where(path => !string.IsNullOrWhiteSpace(path)),
                StringComparer.OrdinalIgnoreCase);

            result.TotalFileCount = allFiles.Count;
            foreach (string absolutePath in allFiles)
            {
                string extension = Path.GetExtension(absolutePath ?? string.Empty);
                foreach (RootFileEnumerationGroup group in groupList)
                {
                    if (group.IncludeAllFiles || (!string.IsNullOrWhiteSpace(extension) && group.Extensions.Contains(extension, StringComparer.OrdinalIgnoreCase)))
                    {
                        result.PathsByGroup[group.Name].Add(absolutePath);
                    }
                }
            }

            foreach (RootFileEnumerationGroup group in groupList)
            {
                result.QueryHitCountByGroup[group.Name] = (ulong)result.PathsByGroup[group.Name].Count;
            }

            result.Success = true;
            return result;
        }
        catch (Exception ex)
        {
            result.Success = false;
            result.ErrorReason = ex.Message;
            return result;
        }
        finally
        {
            stopwatch.Stop();
            result.EnumerationMs = stopwatch.ElapsedMilliseconds;
        }
    }

    private static List<string> NormalizeRoots(IEnumerable<string> rootDirectories)
    {
        return [.. (rootDirectories ?? [])
            .Where(path => !string.IsNullOrWhiteSpace(path) && Directory.Exists(path))
            .Select(Path.GetFullPath)
            .Distinct(StringComparer.OrdinalIgnoreCase)];
    }

    private static IEnumerable<string> EnumerateAllFilesForRoot(string root)
    {
        List<string> fastPaths = [];
        try
        {
            fastPaths = [.. FastDirectoryEnumerator.GetFilePathsAsParallel(root, null, SearchOption.AllDirectories)
                .Where(path => !string.IsNullOrWhiteSpace(path))
                .Select(Path.GetFullPath)
                .Distinct(StringComparer.OrdinalIgnoreCase)];
        }
        catch
        {
            fastPaths = [];
        }

        if (fastPaths.Count > 0)
        {
            return fastPaths;
        }

        try
        {
            return [.. Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories)
                .Where(path => !string.IsNullOrWhiteSpace(path))
                .Select(Path.GetFullPath)
                .Distinct(StringComparer.OrdinalIgnoreCase)];
        }
        catch
        {
            return [];
        }
    }
}
