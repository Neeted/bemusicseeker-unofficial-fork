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
            bool needsFileEntries = groupList.Any(group => !group.IncludeDirectories);
            bool needsDirectoryEntries = groupList.Any(group => group.IncludeDirectories);
            var allFiles = new Dictionary<string, RootFileEnumerationEntry>(StringComparer.OrdinalIgnoreCase);
            if (needsFileEntries)
            {
                string[] sharedExcludedDirectories = ResolveSharedExcludedDirectories(groupList.Where(group => !group.IncludeDirectories));
                bool includeAllFiles = groupList.Any(group => group.IncludeAllFiles);
                string[] fileExtensions = includeAllFiles
                    ? null
                    : [.. groupList
                        .Where(group => !group.IncludeDirectories)
                        .SelectMany(group => group.Extensions ?? [])
                        .Where(extension => !string.IsNullOrWhiteSpace(extension))
                        .Distinct(StringComparer.OrdinalIgnoreCase)];
                foreach (RootFileEnumerationEntry entry in roots
                    .AsParallel()
                    .SelectMany(root => EnumerateAllFilesForRoot(root, fileExtensions, sharedExcludedDirectories))
                    .Where(entry => entry != null && !string.IsNullOrWhiteSpace(entry.Path)))
                {
                    allFiles[entry.Path] = entry;
                }
            }

            var allDirectories = new Dictionary<string, RootFileEnumerationEntry>(StringComparer.OrdinalIgnoreCase);
            if (needsDirectoryEntries)
            {
                string[] sharedExcludedDirectories = ResolveSharedExcludedDirectories(groupList.Where(group => group.IncludeDirectories));
                foreach (RootFileEnumerationEntry entry in roots
                    .AsParallel()
                    .SelectMany(root => EnumerateAllDirectoriesForRoot(root, sharedExcludedDirectories))
                    .Where(entry => entry != null && !string.IsNullOrWhiteSpace(entry.Path)))
                {
                    allDirectories[entry.Path] = entry;
                }
            }

            result.TotalFileCount = allFiles.Count + allDirectories.Count;
            foreach (RootFileEnumerationEntry entry in allFiles.Values)
            {
                string absolutePath = entry.Path;
                string extension = Path.GetExtension(absolutePath ?? string.Empty);
                foreach (RootFileEnumerationGroup group in groupList)
                {
                    if ((group.IncludeAllFiles || (!string.IsNullOrWhiteSpace(extension) && group.Extensions.Contains(extension, StringComparer.OrdinalIgnoreCase)))
                        && !IsExcludedByGroup(absolutePath, group))
                    {
                        result.AddEntry(group.Name, entry);
                    }
                }
            }
            foreach (RootFileEnumerationEntry entry in allDirectories.Values)
            {
                foreach (RootFileEnumerationGroup group in groupList)
                {
                    if (group.IncludeDirectories && !IsExcludedByGroup(entry.Path, group))
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
        return RootFileEnumerationService.NormalizeExecutionRoots(rootDirectories);
    }

    private static IEnumerable<RootFileEnumerationEntry> EnumerateAllFilesForRoot(string root, string[] extensions, string[] excludedDirectories)
    {
        if (excludedDirectories?.Length > 0)
        {
            return EnumerateFilesSkippingExcluded(root, extensions, excludedDirectories);
        }

        List<RootFileEnumerationEntry> fastEntries = [];
        try
        {
            fastEntries = [.. FastDirectoryEnumerator.GetFileDataAsParallel(root, extensions, SearchOption.AllDirectories)
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
                .Where(path => extensions == null || extensions.Length == 0 || extensions.Contains(Path.GetExtension(path), StringComparer.OrdinalIgnoreCase))
                .Select(Path.GetFullPath)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .Select(CreateEntryFromFileInfo)];
        }
        catch
        {
            return [];
        }
    }

    private static IEnumerable<RootFileEnumerationEntry> EnumerateAllDirectoriesForRoot(string root, string[] excludedDirectories)
    {
        var entries = new List<RootFileEnumerationEntry>();
        if (excludedDirectories?.Length > 0 && IsExcludedPath(root, excludedDirectories))
        {
            return entries;
        }

        RootFileEnumerationEntry rootEntry = RootFileEnumerationEntry.FromDirectoryInfo(root);
        if (rootEntry != null)
        {
            entries.Add(rootEntry);
        }

        try
        {
            entries.AddRange((excludedDirectories?.Length > 0
                    ? EnumerateDirectoriesSkippingExcluded(root, excludedDirectories)
                    : Directory.EnumerateDirectories(root, "*", SearchOption.AllDirectories))
                .Where(path => !string.IsNullOrWhiteSpace(path))
                .Select(Path.GetFullPath)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .Select(RootFileEnumerationEntry.FromDirectoryInfo)
                .Where(entry => entry != null));
        }
        catch
        {
        }
        return entries;
    }

    private static IEnumerable<RootFileEnumerationEntry> EnumerateFilesSkippingExcluded(
        string root,
        string[] extensions,
        string[] excludedDirectories)
    {
        foreach (string directory in EnumerateDirectoriesForTraversal(root, excludedDirectories, includeRoot: true))
        {
            IEnumerable<string> files;
            try
            {
                files = Directory.EnumerateFiles(directory, "*", SearchOption.TopDirectoryOnly);
            }
            catch
            {
                continue;
            }

            foreach (string path in files)
            {
                if (!string.IsNullOrWhiteSpace(path)
                    && (extensions == null || extensions.Length == 0 || extensions.Contains(Path.GetExtension(path), StringComparer.OrdinalIgnoreCase)))
                {
                    RootFileEnumerationEntry entry = CreateEntryFromFileInfo(Path.GetFullPath(path));
                    if (entry != null)
                    {
                        yield return entry;
                    }
                }
            }
        }
    }

    private static IEnumerable<string> EnumerateDirectoriesSkippingExcluded(string root, string[] excludedDirectories)
    {
        return EnumerateDirectoriesForTraversal(root, excludedDirectories, includeRoot: false);
    }

    private static IEnumerable<string> EnumerateDirectoriesForTraversal(
        string root,
        string[] excludedDirectories,
        bool includeRoot)
    {
        string normalizedRoot = SafeNormalizeDirectory(root);
        if (string.IsNullOrWhiteSpace(normalizedRoot))
        {
            yield break;
        }
        if (IsExcludedPath(normalizedRoot, excludedDirectories))
        {
            yield break;
        }
        if (includeRoot)
        {
            yield return normalizedRoot;
        }

        var stack = new Stack<string>();
        stack.Push(normalizedRoot);
        while (stack.Count > 0)
        {
            string current = stack.Pop();
            IEnumerable<string> children;
            try
            {
                children = Directory.EnumerateDirectories(current, "*", SearchOption.TopDirectoryOnly);
            }
            catch
            {
                continue;
            }

            foreach (string child in children)
            {
                string normalizedChild = SafeNormalizeDirectory(child);
                if (string.IsNullOrWhiteSpace(normalizedChild)
                    || IsExcludedPath(normalizedChild, excludedDirectories))
                {
                    continue;
                }

                yield return normalizedChild;
                stack.Push(normalizedChild);
            }
        }
    }

    private static string[] ResolveSharedExcludedDirectories(IEnumerable<RootFileEnumerationGroup> groups)
    {
        List<RootFileEnumerationGroup> groupList = [.. (groups ?? []).Where(group => group != null)];
        if (groupList.Count == 0)
        {
            return [];
        }

        string[] first = groupList[0].ExcludedDirectories ?? [];
        var firstSet = new HashSet<string>(first, StringComparer.OrdinalIgnoreCase);
        foreach (RootFileEnumerationGroup group in groupList.Skip(1))
        {
            if (!firstSet.SetEquals(group.ExcludedDirectories ?? []))
            {
                return [];
            }
        }
        return [.. firstSet.OrderBy(path => path, StringComparer.OrdinalIgnoreCase)];
    }

    private static bool IsExcludedByGroup(string path, RootFileEnumerationGroup group)
    {
        return group?.ExcludedDirectories?.Length > 0
            && IsExcludedPath(path, group.ExcludedDirectories);
    }

    private static bool IsExcludedPath(string path, IEnumerable<string> excludedDirectories)
    {
        string normalizedPath = SafeNormalizeDirectory(path);
        if (string.IsNullOrWhiteSpace(normalizedPath))
        {
            return false;
        }

        foreach (string excludedDirectory in excludedDirectories ?? [])
        {
            if (IsSameOrDescendant(normalizedPath, excludedDirectory))
            {
                return true;
            }
        }
        return false;
    }

    private static string SafeNormalizeDirectory(string path)
    {
        try
        {
            return string.IsNullOrWhiteSpace(path)
                ? null
                : Path.GetFullPath(path).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        }
        catch
        {
            return null;
        }
    }

    private static bool IsSameOrDescendant(string candidate, string root)
    {
        string normalizedCandidate = SafeNormalizeDirectory(candidate);
        string normalizedRoot = SafeNormalizeDirectory(root);
        if (string.IsNullOrWhiteSpace(normalizedCandidate) || string.IsNullOrWhiteSpace(normalizedRoot))
        {
            return false;
        }
        if (string.Equals(normalizedCandidate, normalizedRoot, StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        return normalizedCandidate.Length > normalizedRoot.Length
            && normalizedCandidate.StartsWith(normalizedRoot, StringComparison.OrdinalIgnoreCase)
            && (normalizedRoot.EndsWith(Path.DirectorySeparatorChar.ToString(), StringComparison.Ordinal)
                || normalizedRoot.EndsWith(Path.AltDirectorySeparatorChar.ToString(), StringComparison.Ordinal)
                || normalizedCandidate[normalizedRoot.Length] == Path.DirectorySeparatorChar
                || normalizedCandidate[normalizedRoot.Length] == Path.AltDirectorySeparatorChar);
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
