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
            result.InitializeGroup(group.Name);
        }

        if (roots.Count == 0 || groupList.Count == 0)
        {
            result.Success = true;
            return result;
        }

        var stopwatch = System.Diagnostics.Stopwatch.StartNew();
        try
        {
            var allFiles = new Dictionary<string, RootFileEnumerationEntry>(StringComparer.OrdinalIgnoreCase);
            foreach (RootFileEnumerationEntry entry in roots
                .AsParallel()
                .SelectMany(EnumerateAllFilesForRoot)
                .Where(entry => entry != null && !string.IsNullOrWhiteSpace(entry.Path)))
            {
                allFiles[entry.Path] = entry;
            }

            result.TotalFileCount = allFiles.Count;
            foreach (RootFileEnumerationEntry entry in allFiles.Values)
            {
                string absolutePath = entry.Path;
                string extension = Path.GetExtension(absolutePath ?? string.Empty);
                foreach (RootFileEnumerationGroup group in groupList)
                {
                    if (group.IncludeAllFiles || (!string.IsNullOrWhiteSpace(extension) && group.Extensions.Contains(extension, StringComparer.OrdinalIgnoreCase)))
                    {
                        result.AddEntry(group.Name, entry);
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

    private static IEnumerable<RootFileEnumerationEntry> EnumerateAllFilesForRoot(string root)
    {
        List<RootFileEnumerationEntry> fastEntries = [];
        try
        {
            fastEntries = [.. FastDirectoryEnumerator.GetFileDataAsParallel(root, null, SearchOption.AllDirectories)
                .Select(RootFileEnumerationEntry.FromFileData)
                .Where(entry => entry != null && !string.IsNullOrWhiteSpace(entry.Path))
                .GroupBy(entry => Path.GetFullPath(entry.Path), StringComparer.OrdinalIgnoreCase)
                .Select(group => new RootFileEnumerationEntry(Path.GetFullPath(group.First().Path), group.First().LastWriteTimeUtc, group.First().FileSize))];
        }
        catch
        {
            fastEntries = [];
        }

        if (fastEntries.Count > 0)
        {
            return fastEntries;
        }

        try
        {
            return [.. Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories)
                .Where(path => !string.IsNullOrWhiteSpace(path))
                .Select(Path.GetFullPath)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .Select(CreateEntryFromFileInfo)];
        }
        catch
        {
            return [];
        }
    }

    private static RootFileEnumerationEntry CreateEntryFromFileInfo(string path)
    {
        try
        {
            var fileInfo = new FileInfo(path);
            return new RootFileEnumerationEntry(fileInfo.FullName, fileInfo.LastWriteTimeUtc, fileInfo.Length);
        }
        catch
        {
            return new RootFileEnumerationEntry(path);
        }
    }
}
