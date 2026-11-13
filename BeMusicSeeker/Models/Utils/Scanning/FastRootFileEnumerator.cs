using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace BeMusicSeeker.Models.Utils;

/// <summary>登録ルートをOSの列挙で直接歩く、管理側の全件列挙です。</summary>
/// <remarks>
/// 列挙やメタデータ取得の失敗は結果全体を不完全にします。例外は、登録ルートより下にあるリンク切れの再解析ポイントだけで、
/// 警告を残してその項目と配下を結果から除き、完全性は保ちます。壊れたリンク1件で全ルートの走査を無効にしないためです。
/// </remarks>
internal sealed class FastRootFileEnumerator : IRootFileEnumerator
{
    public RootFileEnumerationResult EnumerateFiles(IEnumerable<string> rootDirectories, IEnumerable<RootFileEnumerationGroup> groups, bool verboseLog = false)
    {
        var result = new RootFileEnumerationResult
        {
            BackendName = "fast"
        };

        var brokenLinkSkips = new BrokenLinkSkipState();
        List<string> roots = RootFileEnumerationService.NormalizeExecutionRoots(rootDirectories, result, brokenLinkSkips.RegisteredRoots);
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
                    .SelectMany(root => EnumerateAllFilesForRoot(root, fileExtensions, sharedExcludedDirectories, result, brokenLinkSkips))
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
                    .SelectMany(root => EnumerateAllDirectoriesForRoot(root, sharedExcludedDirectories, result, brokenLinkSkips))
                    .Where(entry => entry != null && !string.IsNullOrWhiteSpace(entry.Path)))
                {
                    allDirectories[entry.Path] = entry;
                }
            }

            // リンク自身の属性は取得できるため、配下の列挙で失敗が分かる前にディレクトリ項目として追加済みになり得る。
            foreach (string skippedBrokenLink in brokenLinkSkips.SkippedPaths)
            {
                allDirectories.Remove(skippedBrokenLink);
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

    /// <summary>一回の列挙の間だけ保持する、リンク切れを飛ばす判定の入力と記録です。</summary>
    private sealed class BrokenLinkSkipState
    {
        /// <summary>
        /// 存在確認を通った登録ルートです。祖先のルートへ縮約された入れ子のルートも含み、これらは飛ばしません。
        /// </summary>
        public HashSet<string> RegisteredRoots { get; } = new(StringComparer.OrdinalIgnoreCase);

        /// <summary>飛ばしたリンク切れのパスです。警告の重複防止と、ディレクトリ結果からの除外に使います。</summary>
        public HashSet<string> SkippedPaths { get; } = new(StringComparer.OrdinalIgnoreCase);
    }

    private static IEnumerable<RootFileEnumerationEntry> EnumerateAllFilesForRoot(
        string root,
        string[] extensions,
        string[] excludedDirectories,
        RootFileEnumerationResult result,
        BrokenLinkSkipState brokenLinkSkips)
    {
        foreach (string directory in EnumerateDirectoriesForTraversal(root, excludedDirectories, includeRoot: true, result, brokenLinkSkips))
        {
            List<string> files;
            try
            {
                files = [.. LongPathFileSystem.EnumerateFiles(directory, "*", SearchOption.TopDirectoryOnly)];
            }
            catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException || ex is ArgumentException || ex is NotSupportedException || ex is PathTooLongException)
            {
                if (!TrySkipBrokenLink(directory, "file_enumeration", ex, brokenLinkSkips))
                {
                    result?.MarkIncomplete("file_enumeration_failed:" + directory + ":" + ex.GetType().Name + ":" + ex.Message);
                }
                continue;
            }

            foreach (string path in files)
            {
                if (!string.IsNullOrWhiteSpace(path)
                    && (extensions == null || extensions.Length == 0 || extensions.Contains(Path.GetExtension(path), StringComparer.OrdinalIgnoreCase)))
                {
                    RootFileEnumerationEntry entry = CreateEntryFromFileInfo(path, result, brokenLinkSkips);
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
        RootFileEnumerationResult result,
        BrokenLinkSkipState brokenLinkSkips)
    {
        if (excludedDirectories?.Length > 0 && IsExcludedPath(root, excludedDirectories))
        {
            yield break;
        }

        RootFileEnumerationEntry rootEntry = CreateEntryFromDirectoryInfo(root, result, brokenLinkSkips);
        if (rootEntry != null)
        {
            yield return rootEntry;
        }

        foreach (string directory in EnumerateDirectoriesForTraversal(root, excludedDirectories, includeRoot: false, result, brokenLinkSkips))
        {
            RootFileEnumerationEntry entry = CreateEntryFromDirectoryInfo(directory, result, brokenLinkSkips);
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
        RootFileEnumerationResult result,
        BrokenLinkSkipState brokenLinkSkips)
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
                if (!TrySkipBrokenLink(current, "directory_enumeration", ex, brokenLinkSkips))
                {
                    result?.MarkIncomplete("directory_enumeration_failed:" + current + ":" + ex.GetType().Name + ":" + ex.Message);
                }
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

    /// <summary>
    /// 失敗した対象が登録ルートより下のリンク切れの再解析ポイントなら、警告を残して飛ばす対象として記録します。
    /// </summary>
    /// <remarks>
    /// 飛ばすのは、失敗が不存在系で、対象自身の属性が再解析ポイントである場合だけです。属性はリンク先が無くてもリンク自身から取得できます。
    /// 登録ルート自身、再解析ポイントでない対象、アクセス拒否などの不存在系以外の失敗、属性を取得できない対象は飛ばさず、
    /// 呼出元が従来どおり不完全として記録します。登録ルートには、別の登録ルートの配下に入れ子で登録されたルートも含みます。
    /// リンク先が一時的に利用できない場合も区別せず、配下の項目は通常の削除差分に従います。
    /// 正常な項目に追加のI/Oを掛けないよう、属性は失敗時だけ取得します。警告は一回の列挙につき対象ごとに一行です。
    /// </remarks>
    /// <param name="path">列挙またはメタデータ取得に失敗した対象のパスです。</param>
    /// <param name="stage">警告へ記録する失敗の段階です。</param>
    /// <param name="failure">対象で発生した失敗です。</param>
    /// <param name="brokenLinkSkips">この列挙の登録ルートと、飛ばしたパスの記録です。</param>
    /// <returns>飛ばす対象なら真です。偽の場合、呼出元は結果を不完全にします。</returns>
    private static bool TrySkipBrokenLink(string path, string stage, Exception failure, BrokenLinkSkipState brokenLinkSkips)
    {
        if (string.IsNullOrWhiteSpace(path)
            || brokenLinkSkips.RegisteredRoots.Contains(LongPathFileSystem.TrimTrailingDirectorySeparators(path)))
        {
            return false;
        }
        if (brokenLinkSkips.SkippedPaths.Contains(path))
        {
            return true;
        }
        if (!LongPathFileSystem.IsNotFoundFailure(failure))
        {
            return false;
        }

        try
        {
            if ((LongPathFileSystem.GetAttributes(path) & FileAttributes.ReparsePoint) == 0)
            {
                return false;
            }
        }
        catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException || ex is ArgumentException || ex is NotSupportedException)
        {
            return false;
        }

        brokenLinkSkips.SkippedPaths.Add(path);
        try
        {
            Ribbit.Logging.NLogWrapper.FileLogger?.Warn(
                "file_enumeration_broken_link_skipped stage={0} path={1} reason={2}:{3}",
                stage, path, failure.GetType().Name, failure.Message);
        }
        catch
        {
            // 診断出力で探索の成功・失敗を変えない。
        }
        return true;
    }

    private static RootFileEnumerationEntry CreateEntryFromFileInfo(string path, RootFileEnumerationResult result, BrokenLinkSkipState brokenLinkSkips)
    {
        try
        {
            string normalizedPath = LongPathFileSystem.NormalizePathForStorage(path);
            LongPathFileSystem.FileMetadata metadata = LongPathFileSystem.GetFileMetadata(normalizedPath);
            return new RootFileEnumerationEntry(normalizedPath, metadata.LastWriteTimeUtc, metadata.Length);
        }
        catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException || ex is ArgumentException || ex is NotSupportedException || ex is PathTooLongException)
        {
            if (TrySkipBrokenLink(path, "file_metadata", ex, brokenLinkSkips))
            {
                return null;
            }
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

    private static RootFileEnumerationEntry CreateEntryFromDirectoryInfo(string path, RootFileEnumerationResult result, BrokenLinkSkipState brokenLinkSkips)
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
            if (TrySkipBrokenLink(path, "directory_metadata", ex, brokenLinkSkips))
            {
                return null;
            }
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
