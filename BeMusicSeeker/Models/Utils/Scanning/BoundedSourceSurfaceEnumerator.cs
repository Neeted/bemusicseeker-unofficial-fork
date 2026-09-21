using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;

namespace BeMusicSeeker.Models.Utils;

internal sealed class BoundedSourceSurfaceEnumerator : IRootFileEnumerator
{
    private readonly int maxVisitedFileSystemEntryCount;

    internal BoundedSourceSurfaceEnumerator(int maxVisitedFileSystemEntryCount)
    {
        this.maxVisitedFileSystemEntryCount = Math.Max(1, maxVisitedFileSystemEntryCount);
    }

    public RootFileEnumerationResult EnumerateFiles(IEnumerable<string> rootDirectories, IEnumerable<RootFileEnumerationGroup> groups, bool verboseLog = false)
    {
        var result = new RootFileEnumerationResult
        {
            BackendName = "bounded_fast_source_surface",
            MaxVisitedFileSystemEntryCount = maxVisitedFileSystemEntryCount
        };

        List<string> roots = RootFileEnumerationService.NormalizeExecutionRoots(rootDirectories);
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

        var stopwatch = Stopwatch.StartNew();
        try
        {
            int totalFileCount = 0;
            foreach (string root in roots)
            {
                if (!EnumerateRoot(root, groupList, result, ref totalFileCount))
                {
                    result.TotalFileCount = totalFileCount;
                    result.Success = true;
                    return result;
                }
            }

            result.TotalFileCount = totalFileCount;
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
            foreach (RootFileEnumerationGroup group in groupList)
            {
                result.QueryHitCountByGroup[group.Name] = (ulong)result.GetPaths(group.Name).Count;
            }
        }
    }

    private bool EnumerateRoot(string root, IReadOnlyCollection<RootFileEnumerationGroup> groups, RootFileEnumerationResult result, ref int totalFileCount)
    {
        string normalizedRoot = SafeNormalizeDirectory(root);
        if (string.IsNullOrWhiteSpace(normalizedRoot))
        {
            return true;
        }

        var pendingDirectories = new Stack<string>();
        var visitedDirectories = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        pendingDirectories.Push(normalizedRoot);
        visitedDirectories.Add(normalizedRoot);

        while (pendingDirectories.Count > 0)
        {
            string currentDirectory = pendingDirectories.Pop();
            if (!EnumerateFilesInDirectory(currentDirectory, groups, result, ref totalFileCount))
            {
                return false;
            }
            if (!EnumerateChildDirectories(currentDirectory, groups, result, pendingDirectories, visitedDirectories))
            {
                return false;
            }
        }
        return true;
    }

    private bool EnumerateFilesInDirectory(string directory, IReadOnlyCollection<RootFileEnumerationGroup> groups, RootFileEnumerationResult result, ref int totalFileCount)
    {
        try
        {
            foreach (string path in LongPathFileSystem.EnumerateFiles(directory, "*", SearchOption.TopDirectoryOnly))
            {
                if (!TryVisitFileSystemEntry(result))
                {
                    return false;
                }

                string normalizedPath = SafeNormalizePath(path);
                if (string.IsNullOrWhiteSpace(normalizedPath))
                {
                    continue;
                }

                totalFileCount++;
                string extension = Path.GetExtension(normalizedPath);
                RootFileEnumerationEntry entry = CreateEntryFromFileInfo(normalizedPath);
                if (entry == null)
                {
                    continue;
                }

                foreach (RootFileEnumerationGroup group in groups)
                {
                    if (group.IncludeDirectories)
                    {
                        continue;
                    }
                    if ((group.IncludeAllFiles || (!string.IsNullOrWhiteSpace(extension) && group.Extensions.Contains(extension, StringComparer.OrdinalIgnoreCase)))
                        && !IsExcludedByGroup(normalizedPath, group))
                    {
                        result.AddEntry(group.Name, entry);
                    }
                }
            }
        }
        catch
        {
        }
        return true;
    }

    private bool EnumerateChildDirectories(string directory, IReadOnlyCollection<RootFileEnumerationGroup> groups, RootFileEnumerationResult result, Stack<string> pendingDirectories, HashSet<string> visitedDirectories)
    {
        try
        {
            foreach (string path in LongPathFileSystem.EnumerateDirectories(directory, "*", SearchOption.TopDirectoryOnly))
            {
                if (!TryVisitFileSystemEntry(result))
                {
                    return false;
                }

                string normalizedPath = SafeNormalizeDirectory(path);
                if (string.IsNullOrWhiteSpace(normalizedPath)
                    || visitedDirectories.Contains(normalizedPath)
                    || IsReparsePoint(normalizedPath))
                {
                    continue;
                }

                RootFileEnumerationEntry entry = null;
                foreach (RootFileEnumerationGroup group in groups)
                {
                    if (!group.IncludeDirectories || IsExcludedByGroup(normalizedPath, group))
                    {
                        continue;
                    }

                    entry ??= RootFileEnumerationEntry.FromDirectoryInfo(normalizedPath);
                    result.AddEntry(group.Name, entry);
                }

                visitedDirectories.Add(normalizedPath);
                pendingDirectories.Push(normalizedPath);
            }
        }
        catch
        {
        }
        return true;
    }

    private bool TryVisitFileSystemEntry(RootFileEnumerationResult result)
    {
        result.VisitedFileSystemEntryCount++;
        if (result.VisitedFileSystemEntryCount <= maxVisitedFileSystemEntryCount)
        {
            return true;
        }

        result.ScanLimitExceeded = true;
        return false;
    }

    private static bool IsReparsePoint(string path)
    {
        try
        {
            return (LongPathFileSystem.GetAttributes(path) & FileAttributes.ReparsePoint) != 0;
        }
        catch
        {
            return true;
        }
    }

    private static bool IsExcludedByGroup(string path, RootFileEnumerationGroup group)
    {
        return group?.ExcludedDirectories?.Length > 0
            && group.ExcludedDirectories.Any(excludedDirectory => IsSameOrDescendant(path, excludedDirectory));
    }

    private static bool IsSameOrDescendant(string candidate, string root)
    {
        string normalizedCandidate = SafeNormalizeDirectory(candidate);
        string normalizedRoot = SafeNormalizeDirectory(root);
        return !string.IsNullOrWhiteSpace(normalizedCandidate)
            && !string.IsNullOrWhiteSpace(normalizedRoot)
            && LongPathFileSystem.IsSameOrDescendantNormalizedDirectoryPath(normalizedCandidate, normalizedRoot);
    }

    private static RootFileEnumerationEntry CreateEntryFromFileInfo(string path)
    {
        try
        {
            string normalizedPath = LongPathFileSystem.NormalizePathForStorage(path);
            return LongPathFileSystem.FileExists(normalizedPath)
                ? new RootFileEnumerationEntry(normalizedPath, LongPathFileSystem.GetLastWriteTimeUtc(normalizedPath, isDirectory: false))
                : null;
        }
        catch
        {
            return null;
        }
    }

    private static string SafeNormalizePath(string path)
    {
        try
        {
            return string.IsNullOrWhiteSpace(path) ? string.Empty : LongPathFileSystem.NormalizePathForStorage(path);
        }
        catch
        {
            return string.Empty;
        }
    }

    private static string SafeNormalizeDirectory(string path)
    {
        try
        {
            return string.IsNullOrWhiteSpace(path)
                ? string.Empty
                : LongPathFileSystem.TrimTrailingDirectorySeparators(LongPathFileSystem.NormalizePathForStorage(path));
        }
        catch
        {
            return string.Empty;
        }
    }
}
