using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;

namespace BeMusicSeeker.Models.Utils;

/// <summary>導入推定の入力元を訪問上限内で列挙し、通常の外部失敗では取得済みの部分結果を保持します。</summary>
internal sealed class BoundedSourceSurfaceEnumerator : IRootFileEnumerator
{
    private readonly int maxVisitedFileSystemEntryCount;

    /// <summary>訪問上限を指定します。1未満の値は従来どおり1項目として扱います。</summary>
    internal BoundedSourceSurfaceEnumerator(int maxVisitedFileSystemEntryCount)
    {
        this.maxVisitedFileSystemEntryCount = Math.Max(1, maxVisitedFileSystemEntryCount);
    }

    /// <summary>外部失敗・上限超過を不完全として返し、内部不具合や資源不足は呼出元へ伝播します。</summary>
    public RootFileEnumerationResult EnumerateFiles(IEnumerable<string> rootDirectories, IEnumerable<RootFileEnumerationGroup> groups, bool verboseLog = false)
    {
        var result = new RootFileEnumerationResult
        {
            BackendName = "bounded_fast_source_surface",
            MaxVisitedFileSystemEntryCount = maxVisitedFileSystemEntryCount
        };

        List<string> roots = RootFileEnumerationService.NormalizeExecutionRoots(rootDirectories, result);
        foreach (string reason in result.IncompleteReasons)
        {
            LogFailure(reason);
        }
        List<RootFileEnumerationGroup> groupList = [.. (groups ?? []).Where(group => group != null && !string.IsNullOrWhiteSpace(group.Name))];
        RootFileEnumerationService.LogSearchConditions(result.BackendName, roots, groupList);
        foreach (RootFileEnumerationGroup group in groupList)
        {
            result.InitializeGroup(group.Name);
        }

        if (roots.Count == 0 || groupList.Count == 0)
        {
            result.Success = result.IsComplete;
            return result;
        }

        var stopwatch = Stopwatch.StartNew();
        int totalFileCount = 0;
        string currentRoot = string.Empty;
        try
        {
            foreach (string root in roots)
            {
                currentRoot = root;
                if (!EnumerateRoot(root, groupList, result, ref totalFileCount))
                {
                    break;
                }
            }

            result.Success = result.IsComplete && !result.ScanLimitExceeded;
            return result;
        }
        catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException || ex is ArgumentException || ex is NotSupportedException)
        {
            RecordFailure(result, currentRoot, "bounded_enumeration_failed", ex);
            return result;
        }
        finally
        {
            stopwatch.Stop();
            result.TotalFileCount = totalFileCount;
            result.EnumerationMs = stopwatch.ElapsedMilliseconds;
            foreach (RootFileEnumerationGroup group in groupList)
            {
                result.QueryHitCountByGroup[group.Name] = (ulong)result.GetPaths(group.Name).Count;
            }
        }
    }

    private bool EnumerateRoot(string root, IReadOnlyCollection<RootFileEnumerationGroup> groups, RootFileEnumerationResult result, ref int totalFileCount)
    {
        string normalizedRoot = SafeNormalizeDirectory(root, result, "root_normalize_failed");
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

                string normalizedPath = SafeNormalizePath(path, result, "file_normalize_failed");
                if (string.IsNullOrWhiteSpace(normalizedPath))
                {
                    continue;
                }

                totalFileCount++;
                string extension = Path.GetExtension(normalizedPath);
                RootFileEnumerationEntry entry = CreateEntryFromFileInfo(normalizedPath, result);
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
                        && !IsExcludedByGroup(normalizedPath, group, result))
                    {
                        result.AddEntry(group.Name, entry);
                    }
                }
            }
        }
        catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException || ex is ArgumentException || ex is NotSupportedException)
        {
            RecordFailure(result, directory, "file_enumeration_failed", ex);
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

                string normalizedPath = SafeNormalizeDirectory(path, result, "directory_normalize_failed");
                if (string.IsNullOrWhiteSpace(normalizedPath)
                    || visitedDirectories.Contains(normalizedPath)
                    || IsReparsePoint(normalizedPath, result))
                {
                    continue;
                }

                RootFileEnumerationEntry entry = null;
                bool metadataRead = false;
                foreach (RootFileEnumerationGroup group in groups)
                {
                    if (!group.IncludeDirectories || IsExcludedByGroup(normalizedPath, group, result))
                    {
                        continue;
                    }

                    if (!metadataRead)
                    {
                        entry = CreateEntryFromDirectoryInfo(normalizedPath, result);
                        metadataRead = true;
                    }
                    result.AddEntry(group.Name, entry);
                }

                visitedDirectories.Add(normalizedPath);
                pendingDirectories.Push(normalizedPath);
            }
        }
        catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException || ex is ArgumentException || ex is NotSupportedException)
        {
            RecordFailure(result, directory, "directory_enumeration_failed", ex);
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
        result.MarkIncomplete("scan_limit_exceeded");
        return false;
    }

    private static bool IsReparsePoint(string path, RootFileEnumerationResult result)
    {
        try
        {
            return (LongPathFileSystem.GetAttributes(path) & FileAttributes.ReparsePoint) != 0;
        }
        catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException || ex is ArgumentException || ex is NotSupportedException)
        {
            RecordFailure(result, path, "directory_attributes_failed", ex);
            return true;
        }
    }

    private static bool IsExcludedByGroup(string path, RootFileEnumerationGroup group, RootFileEnumerationResult result)
    {
        return group?.ExcludedDirectories?.Length > 0
            && group.ExcludedDirectories.Any(excludedDirectory => IsSameOrDescendant(path, excludedDirectory, result));
    }

    private static bool IsSameOrDescendant(string candidate, string root, RootFileEnumerationResult result)
    {
        string normalizedCandidate = SafeNormalizeDirectory(candidate, result, "exclusion_candidate_normalize_failed");
        string normalizedRoot = SafeNormalizeDirectory(root, result, "exclusion_root_normalize_failed");
        return !string.IsNullOrWhiteSpace(normalizedCandidate)
            && !string.IsNullOrWhiteSpace(normalizedRoot)
            && LongPathFileSystem.IsSameOrDescendantNormalizedDirectoryPath(normalizedCandidate, normalizedRoot);
    }

    private static RootFileEnumerationEntry CreateEntryFromFileInfo(string path, RootFileEnumerationResult result)
    {
        try
        {
            string normalizedPath = LongPathFileSystem.NormalizePathForStorage(path);
            if (!LongPathFileSystem.FileExists(normalizedPath))
            {
                RecordFailure(result, normalizedPath, "file_metadata_unavailable");
                return null;
            }
            return new RootFileEnumerationEntry(normalizedPath, LongPathFileSystem.GetLastWriteTimeUtc(normalizedPath, isDirectory: false));
        }
        catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException || ex is ArgumentException || ex is NotSupportedException)
        {
            RecordFailure(result, path, "file_metadata_failed", ex);
            return null;
        }
    }

    private static RootFileEnumerationEntry CreateEntryFromDirectoryInfo(string path, RootFileEnumerationResult result)
    {
        try
        {
            if (!LongPathFileSystem.DirectoryExists(path))
            {
                RecordFailure(result, path, "directory_metadata_unavailable");
                return null;
            }
            return new RootFileEnumerationEntry(path, LongPathFileSystem.GetLastWriteTimeUtc(path, isDirectory: true));
        }
        catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException || ex is ArgumentException || ex is NotSupportedException)
        {
            RecordFailure(result, path, "directory_metadata_failed", ex);
            return null;
        }
    }

    private static string SafeNormalizePath(string path, RootFileEnumerationResult result, string phase)
    {
        try
        {
            return string.IsNullOrWhiteSpace(path) ? string.Empty : LongPathFileSystem.NormalizePathForStorage(path);
        }
        catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException || ex is ArgumentException || ex is NotSupportedException)
        {
            RecordFailure(result, path, phase, ex);
            return string.Empty;
        }
    }

    private static string SafeNormalizeDirectory(string path, RootFileEnumerationResult result, string phase)
    {
        try
        {
            return string.IsNullOrWhiteSpace(path)
                ? string.Empty
                : LongPathFileSystem.TrimTrailingDirectorySeparators(LongPathFileSystem.NormalizePathForStorage(path));
        }
        catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException || ex is ArgumentException || ex is NotSupportedException)
        {
            RecordFailure(result, path, phase, ex);
            return string.Empty;
        }
    }

    private static void RecordFailure(RootFileEnumerationResult result, string path, string phase, Exception exception = null)
    {
        string reason = phase + ":" + path;
        if (exception != null)
        {
            reason += ":" + exception.GetType().Name + ":" + exception.Message;
        }
        result.MarkFailed(reason);
        LogFailure(reason, exception);
    }

    private static void LogFailure(string reason, Exception exception = null)
    {
        try
        {
            Ribbit.Logging.NLogWrapper.FileLogger?.Warn(exception, "bounded_source_surface_incomplete reason=" + reason);
        }
        catch
        {
            // 診断出力の失敗で、走査の完全性や取得済みの部分結果を変えない。
        }
    }
}
