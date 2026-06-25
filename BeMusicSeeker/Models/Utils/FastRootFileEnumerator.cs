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

        List<string> roots = NormalizeRoots(rootDirectories, result);
        List<RootFileEnumerationGroup> groupList = [.. (groups ?? []).Where(group => group != null && !string.IsNullOrWhiteSpace(group.Name))];
        foreach (RootFileEnumerationGroup group in groupList)
        {
            result.InitializeGroup(group.Name);
        }

        if (roots.Count == 0 || groupList.Count == 0)
        {
            result.Success = result.IsComplete;
            return result;
        }

        var stopwatch = System.Diagnostics.Stopwatch.StartNew();
        try
        {
            bool needsFileEntries = groupList.Any(group => !group.IncludeDirectories);
            bool needsDirectoryEntries = groupList.Any(group => group.IncludeDirectories);
            var allFiles = new Dictionary<string, RootFileEnumerationEntry>(StringComparer.Ordinal);
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
                    .SelectMany(root => EnumerateAllFilesForRoot(root, fileExtensions, sharedExcludedDirectories, result))
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
                    .SelectMany(root => EnumerateAllDirectoriesForRoot(root, sharedExcludedDirectories, result))
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

            result.Success = result.IsComplete && !result.ScanLimitExceeded;
            return result;
        }
        catch (Exception ex)
        {
            result.MarkFailed("fast_enumeration_failed:" + ex.GetType().Name + ":" + ex.Message);
            return result;
        }
        finally
        {
            stopwatch.Stop();
            result.EnumerationMs = stopwatch.ElapsedMilliseconds;
        }
    }

    private static List<string> NormalizeRoots(IEnumerable<string> rootDirectories, RootFileEnumerationResult result)
    {
        return RootFileEnumerationService.NormalizeExecutionRoots(rootDirectories, result);
    }

    private static IEnumerable<RootFileEnumerationEntry> EnumerateAllFilesForRoot(
        string root,
        string[] extensions,
        string[] excludedDirectories,
        RootFileEnumerationResult result)
    {
        foreach (string directory in EnumerateDirectoriesForTraversal(root, excludedDirectories, includeRoot: true, result))
        {
            List<string> files;
            try
            {
                files = [.. LongPathFileSystem.EnumerateFiles(directory, "*", SearchOption.TopDirectoryOnly)];
            }
            catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException || ex is ArgumentException || ex is NotSupportedException || ex is PathTooLongException)
            {
                result?.MarkIncomplete("file_enumeration_failed:" + directory + ":" + ex.GetType().Name + ":" + ex.Message);
                continue;
            }

            foreach (string path in files)
            {
                if (!string.IsNullOrWhiteSpace(path)
                    && (extensions == null || extensions.Length == 0 || extensions.Contains(Path.GetExtension(path), StringComparer.OrdinalIgnoreCase)))
                {
                    RootFileEnumerationEntry entry = CreateEntryFromFileInfo(path, result);
                    if (entry != null)
                    {
                        yield return entry;
                    }
                }
            }
        }
    }

    private static IEnumerable<RootFileEnumerationEntry> EnumerateAllDirectoriesForRoot(
        string root,
        string[] excludedDirectories,
        RootFileEnumerationResult result)
    {
        if (excludedDirectories?.Length > 0 && IsExcludedPath(root, excludedDirectories))
        {
            yield break;
        }

        RootFileEnumerationEntry rootEntry = CreateEntryFromDirectoryInfo(root, result);
        if (rootEntry != null)
        {
            yield return rootEntry;
        }

        foreach (string directory in EnumerateDirectoriesForTraversal(root, excludedDirectories, includeRoot: false, result))
        {
            RootFileEnumerationEntry entry = CreateEntryFromDirectoryInfo(directory, result);
            if (entry != null)
            {
                yield return entry;
            }
        }
    }

    private static IEnumerable<string> EnumerateDirectoriesForTraversal(
        string root,
        string[] excludedDirectories,
        bool includeRoot,
        RootFileEnumerationResult result)
    {
        string normalizedRoot = SafeNormalizeDirectory(root, result, "root_normalize_failed");
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
            List<string> children;
            try
            {
                children = [.. LongPathFileSystem.EnumerateDirectories(current, "*", SearchOption.TopDirectoryOnly)];
            }
            catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException || ex is ArgumentException || ex is NotSupportedException || ex is PathTooLongException)
            {
                result?.MarkIncomplete("directory_enumeration_failed:" + current + ":" + ex.GetType().Name + ":" + ex.Message);
                continue;
            }

            foreach (string child in children)
            {
                string normalizedChild = SafeNormalizeDirectory(child, result, "directory_normalize_failed");
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

    private static string SafeNormalizeDirectory(string path, RootFileEnumerationResult result = null, string failureKind = "directory_normalize_failed")
    {
        try
        {
            return string.IsNullOrWhiteSpace(path)
                ? null
                : LongPathFileSystem.TrimTrailingDirectorySeparators(LongPathFileSystem.NormalizePathForStorage(path));
        }
        catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException || ex is ArgumentException || ex is NotSupportedException || ex is PathTooLongException)
        {
            result?.MarkIncomplete(failureKind + ":" + path + ":" + ex.GetType().Name + ":" + ex.Message);
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
        return LongPathFileSystem.IsSameOrDescendantNormalizedDirectoryPath(normalizedCandidate, normalizedRoot);
    }

    private static RootFileEnumerationEntry CreateEntryFromFileInfo(string path, RootFileEnumerationResult result)
    {
        try
        {
            string normalizedPath = LongPathFileSystem.NormalizePathForStorage(path);
            LongPathFileSystem.FileMetadata metadata = LongPathFileSystem.GetFileMetadata(normalizedPath);
            return new RootFileEnumerationEntry(normalizedPath, metadata.LastWriteTimeUtc, metadata.Length);
        }
        catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException || ex is ArgumentException || ex is NotSupportedException || ex is PathTooLongException)
        {
            result?.MarkIncomplete("file_metadata_failed:" + path + ":" + ex.GetType().Name + ":" + ex.Message);
            try
            {
                return new RootFileEnumerationEntry(LongPathFileSystem.NormalizePathForStorage(path));
            }
            catch
            {
                return null;
            }
        }
    }

    private static RootFileEnumerationEntry CreateEntryFromDirectoryInfo(string path, RootFileEnumerationResult result)
    {
        try
        {
            string normalizedPath = LongPathFileSystem.NormalizePathForStorage(path);
            if (!LongPathFileSystem.DirectoryExists(normalizedPath))
            {
                result?.MarkIncomplete("directory_metadata_missing:" + normalizedPath);
                return null;
            }
            DateTime lastWriteTimeUtc = LongPathFileSystem.GetLastWriteTimeUtc(normalizedPath, isDirectory: true);
            return new RootFileEnumerationEntry(normalizedPath, lastWriteTimeUtc);
        }
        catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException || ex is ArgumentException || ex is NotSupportedException || ex is PathTooLongException)
        {
            result?.MarkIncomplete("directory_metadata_failed:" + path + ":" + ex.GetType().Name + ":" + ex.Message);
            try
            {
                return new RootFileEnumerationEntry(LongPathFileSystem.NormalizePathForStorage(path));
            }
            catch
            {
                return null;
            }
        }
    }

}
