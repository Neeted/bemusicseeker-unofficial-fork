using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using BeMusicSeeker.Models.Utils;

namespace BeMusicSeeker.Models.BmsLibraryInternal;

/// <summary>
/// Identifies one logically immutable resource-index generation and its matching directory cache.
/// </summary>
internal readonly record struct LibraryResourceIndexSnapshot(
    LibraryResourceIndex Index,
    DirectoryResourceLookupCache DirectoryLookupCache,
    long Generation);

/// <summary>
/// Reports a resource-index mutation together with the generation produced by it.
/// </summary>
internal readonly record struct LibraryResourceIndexMutationReceipt(
    LibraryResourceIndexSnapshot Snapshot,
    DirectoryResourceLookupCache.ReverseLookupMutationResult MutationResult);

/// <summary>
/// Reports a batch folder-reference rewrite against the current resource index.
/// </summary>
internal sealed class LibraryResourceIndexMovedFoldersResult
{
    /// <summary>Gets the mutation receipt for the current resource index.</summary>
    internal LibraryResourceIndexMutationReceipt Receipt { get; init; }

    /// <summary>Gets the number of normalized move commands.</summary>
    internal int MoveCount { get; init; }

    /// <summary>Gets the number of current directory keys examined.</summary>
    internal int LookupKeyCount { get; init; }

    /// <summary>Gets the number of directory keys matched by a move.</summary>
    internal int MatchedKeyCount { get; init; }

    /// <summary>Gets the elapsed time for normalization and mutation.</summary>
    internal long ElapsedMs { get; init; }
}

/// <summary>
/// Owns the current in-memory resource index and serializes replacement with all
/// directory-cache mutations. Long-lived consumers retain this owner, never a
/// cache instance that can become detached after a file-scan replacement.
/// </summary>
internal sealed class LibraryResourceIndexOwner
{
    private readonly object gate = new();

    private LibraryResourceIndexSnapshot currentSnapshot;

    /// <summary>
    /// Creates an owner for the initial resource index.
    /// </summary>
    internal LibraryResourceIndexOwner(LibraryResourceIndex initialIndex)
    {
        LibraryResourceIndex normalizedIndex = NormalizeIndex(initialIndex);
        currentSnapshot = new LibraryResourceIndexSnapshot(
            normalizedIndex,
            normalizedIndex.DirectoryLookupCache,
            Generation: 0L);
    }

    /// <summary>
    /// Captures the current index, its directory cache, and their runtime generation atomically.
    /// </summary>
    internal LibraryResourceIndexSnapshot CaptureSnapshot()
    {
        lock (gate)
        {
            return CaptureSnapshotUnsafe();
        }
    }

    /// <summary>
    /// Transfers ownership of a complete file-scan replacement and advances the runtime generation.
    /// </summary>
    /// <remarks>The caller must not mutate the replacement after it is published.</remarks>
    internal LibraryResourceIndexSnapshot Replace(LibraryResourceIndex replacement)
    {
        lock (gate)
        {
            LibraryResourceIndex normalizedReplacement = NormalizeIndex(replacement);
            currentSnapshot = new LibraryResourceIndexSnapshot(
                normalizedReplacement,
                normalizedReplacement.DirectoryLookupCache,
                currentSnapshot.Generation + 1L);
            return currentSnapshot;
        }
    }

    /// <summary>
    /// Rewrites one directory subtree in the current index after its filesystem move succeeds.
    /// </summary>
    internal LibraryResourceIndexMutationReceipt MoveFolderReferences(
        string sourceDirectory,
        string destinationDirectory)
    {
        return UpdateMovedFolderReferences(
        [
            new LibraryFolderPathChange
            {
                OldFolderPath = sourceDirectory,
                NewFolderPath = destinationDirectory
            }
        ]).Receipt;
    }

    /// <summary>
    /// 複数の移動subtreeを、一括変更と同じ未公開反映経路で書き換えます。
    /// </summary>
    internal LibraryResourceIndexMovedFoldersResult UpdateMovedFolderReferences(
        IEnumerable<LibraryFolderPathChange> movedFolders)
    {
        return ApplyFolderChanges(movedFolders, [], []);
    }

    /// <summary>
    /// 成功したsubtreeの移動・除去と、最終FSから選択済みのEntry置換を一つの未公開索引へ適用し、一世代だけ公開します。
    /// </summary>
    /// <param name="movedFolders">既存hashを移転する成功済みの旧新scope。</param>
    /// <param name="removedSourceDirectories">成功済み削除・統合元のsubtree。</param>
    /// <param name="directoryReplacements">置換するEntryと取得済みscan。nullのscanはそのEntryだけの除去を表します。</param>
    /// <remarks>FS取得は呼出し元で完了させます。列挙失敗では旧snapshotを維持し、同値の除去・再追加では公開しません。</remarks>
    internal LibraryResourceIndexMovedFoldersResult ApplyFolderChanges(
        IEnumerable<LibraryFolderPathChange> movedFolders,
        IEnumerable<string> removedSourceDirectories,
        IEnumerable<KeyValuePair<string, ChartScanResult>> directoryReplacements)
    {
        var stopwatch = Stopwatch.StartNew();
        List<LibraryFolderPathChange> moves = [.. (movedFolders ?? [])
            .Where(move => !string.IsNullOrWhiteSpace(move?.OldFolderPath)
                && !string.IsNullOrWhiteSpace(move.NewFolderPath)
                && !move.OldFolderPath.Equals(move.NewFolderPath, StringComparison.OrdinalIgnoreCase))
            .GroupBy(move => NormalizeDirectoryPath(move.OldFolderPath), StringComparer.OrdinalIgnoreCase)
            .Select(group => group.Last())];

        lock (gate)
        {
            // 移動時の既存全key照合だけを維持し、削除・祖先置換のためには全keyを複製しません。
            List<string> lookupKeys = moves.Count == 0 ? [] : [.. currentSnapshot.DirectoryLookupCache.Keys];
            var movesByOldPath = moves.ToDictionary(
                move => NormalizeDirectoryPath(move.OldFolderPath),
                move => move,
                StringComparer.OrdinalIgnoreCase);
            List<KeyValuePair<string, string>> replacements = [];
            foreach (string lookupKey in lookupKeys)
            {
                if (!TryFindMovedFolderReference(lookupKey, movesByOldPath, out LibraryFolderPathChange move))
                {
                    continue;
                }

                string oldFolderPath = NormalizeDirectoryPath(move.OldFolderPath);
                string newFolderPath = NormalizeDirectoryPath(move.NewFolderPath);
                replacements.Add(new KeyValuePair<string, string>(
                    lookupKey,
                    newFolderPath + lookupKey.Substring(oldFolderPath.Length)));
            }

            DirectoryResourceLookupCache previousCache = currentSnapshot.DirectoryLookupCache;
            LibraryResourceIndexMutationReceipt receipt = MutateCurrentUnsafe(cache =>
            {
                HashSet<string> affectedDirectories = new(StringComparer.OrdinalIgnoreCase);
                foreach (KeyValuePair<string, string> replacement in replacements)
                {
                    affectedDirectories.Add(replacement.Key);
                    affectedDirectories.Add(replacement.Value);
                }
                DirectoryResourceLookupCache.ReverseLookupMutationResult result = cache.ReplaceDirsWithResult(replacements);
                var removedDirectories = new List<string>();
                result = result.Combine(cache.RemoveUnderSourceDirectories(removedSourceDirectories, removedDirectories));
                affectedDirectories.UnionWith(removedDirectories);
                foreach (KeyValuePair<string, ChartScanResult> replacement in directoryReplacements ?? [])
                {
                    affectedDirectories.Add(replacement.Key);
                    result = result.Combine(replacement.Value == null
                        ? cache.RemoveDirWithResult(replacement.Key)
                        : cache.AddDir(replacement.Key, replacement.Value));
                }
                return previousCache.HasSameDirectoryEntries(cache, affectedDirectories)
                    ? DirectoryResourceLookupCache.ReverseLookupMutationResult.Empty
                    : result;
            });
            stopwatch.Stop();
            return new LibraryResourceIndexMovedFoldersResult
            {
                Receipt = receipt,
                MoveCount = moves.Count,
                LookupKeyCount = lookupKeys.Count,
                MatchedKeyCount = replacements.Count,
                ElapsedMs = stopwatch.ElapsedMilliseconds
            };
        }
    }

    /// <summary>
    /// Removes a directory subtree from the current resource index.
    /// </summary>
    internal LibraryResourceIndexMutationReceipt RemoveUnderSourceDirectory(string sourceDirectory)
    {
        return RemoveUnderSourceDirectories([sourceDirectory]);
    }

    /// <summary>
    /// Removes confirmed directory subtrees in one unpublished mutation and publishes at most
    /// one generation. Callers pass successful filesystem results, never the original delete plan.
    /// </summary>
    /// <remarks>
    /// Duplicate and overlapping subtrees cannot remove or count an entry twice. Enumeration or
    /// mutation failure leaves the prior snapshot published; it does not undo filesystem work.
    /// </remarks>
    internal LibraryResourceIndexMutationReceipt RemoveUnderSourceDirectories(
        IEnumerable<string> sourceDirectories)
    {
        lock (gate)
        {
            return MutateCurrentUnsafe(cache => cache.RemoveUnderSourceDirectories(sourceDirectories));
        }
    }

    /// <summary>
    /// Adds every chart directory in a completed scan to the current resource index.
    /// </summary>
    internal LibraryResourceIndexMutationReceipt AddScanDirectories(ChartScanResult scan)
    {
        lock (gate)
        {
            return MutateCurrentUnsafe(cache => cache.AddScanDirectories(scan));
        }
    }

    /// <summary>
    /// Atomically removes a source subtree and adds a replacement scan as one resource-index generation.
    /// </summary>
    /// <remarks>
    /// Both changes are applied to an unpublished structural copy. If scan enumeration throws, the
    /// previously published snapshot remains current and the exception is propagated to the caller.
    /// </remarks>
    internal LibraryResourceIndexMutationReceipt ReplaceSourceDirectoryWithScan(
        string sourceDirectory,
        ChartScanResult replacementScan)
    {
        return ReplaceSourceDirectoryWithDirectories(
            sourceDirectory,
            replacementScan?.ChartDirectories,
            replacementScan);
    }

    /// <summary>
    /// 元subtreeの除去と選択Entryの置換を、共通の一括変更経路へ渡します。
    /// </summary>
    /// <remarks>
    /// The explicit directory sequence is the command input and is enumerated only against the
    /// unpublished clone; enumeration failure therefore cannot partially publish source removal.
    /// </remarks>
    internal LibraryResourceIndexMutationReceipt ReplaceSourceDirectoryWithDirectories(
        string sourceDirectory,
        IEnumerable<string> replacementDirectories,
        ChartScanResult replacementScan)
    {
        return ApplyFolderChanges([], [sourceDirectory], (replacementDirectories ?? [])
            .Where(path => replacementScan != null && !string.IsNullOrWhiteSpace(path))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Select(path => new KeyValuePair<string, ChartScanResult>(path, replacementScan))).Receipt;
    }

    /// <summary>
    /// Adds or replaces one directory from a scan in the current resource index.
    /// </summary>
    internal LibraryResourceIndexMutationReceipt AddDirectory(
        string directoryPath,
        ChartScanResult scan)
    {
        lock (gate)
        {
            return MutateCurrentUnsafe(cache => cache.AddDir(directoryPath, scan));
        }
    }

    /// <summary>
    /// Adds or replaces selected scanned directories in one current-index generation.
    /// </summary>
    internal LibraryResourceIndexMutationReceipt AddDirectories(
        IEnumerable<string> directoryPaths,
        ChartScanResult scan)
    {
        lock (gate)
        {
            List<string> selectedDirectories = [.. (directoryPaths ?? [])
                .Where(path => !string.IsNullOrWhiteSpace(path))
                .Distinct(StringComparer.OrdinalIgnoreCase)];
            return MutateCurrentUnsafe(cache =>
            {
                DirectoryResourceLookupCache.ReverseLookupMutationResult result =
                    DirectoryResourceLookupCache.ReverseLookupMutationResult.Empty;
                foreach (string directoryPath in selectedDirectories)
                {
                    result = result.Combine(cache.AddDir(directoryPath, scan));
                }
                return result;
            });
        }
    }

    /// <summary>
    /// Adds or replaces one directory from its resource file names in the current resource index.
    /// </summary>
    internal LibraryResourceIndexMutationReceipt AddDirectory(
        string directoryPath,
        IEnumerable<string> fileNames)
    {
        lock (gate)
        {
            return MutateCurrentUnsafe(cache => cache.AddDir(directoryPath, fileNames));
        }
    }

    /// <summary>
    /// Adds or replaces one directory from its six resource-hash categories.
    /// </summary>
    internal LibraryResourceIndexMutationReceipt AddDirectory(
        string directoryPath,
        IEnumerable<uint> audioRelativePathHashes,
        IEnumerable<uint> imageRelativePathHashes,
        IEnumerable<uint> movieRelativePathHashes,
        IEnumerable<uint> selfOwnedAudioRelativePathHashes = null,
        IEnumerable<uint> selfOwnedImageRelativePathHashes = null,
        IEnumerable<uint> selfOwnedMovieRelativePathHashes = null)
    {
        lock (gate)
        {
            return MutateCurrentUnsafe(cache => cache.AddDir(
                directoryPath,
                audioRelativePathHashes,
                imageRelativePathHashes,
                movieRelativePathHashes,
                selfOwnedAudioRelativePathHashes,
                selfOwnedImageRelativePathHashes,
                selfOwnedMovieRelativePathHashes));
        }
    }

    private LibraryResourceIndexMutationReceipt MutateCurrentUnsafe(
        Func<DirectoryResourceLookupCache, DirectoryResourceLookupCache.ReverseLookupMutationResult> mutation)
    {
        ArgumentNullException.ThrowIfNull(mutation);
        LibraryResourceIndexSnapshot previousSnapshot = currentSnapshot;
        DirectoryResourceLookupCache nextCache =
            previousSnapshot.DirectoryLookupCache.CloneForMutation();
        DirectoryResourceLookupCache.ReverseLookupMutationResult mutationResult =
            mutation(nextCache);
        if (!mutationResult.Changed)
        {
            return new LibraryResourceIndexMutationReceipt(previousSnapshot, mutationResult);
        }

        LibraryResourceIndex nextIndex =
            previousSnapshot.Index.DeriveWithDirectoryLookupCache(nextCache);
        currentSnapshot = new LibraryResourceIndexSnapshot(
            nextIndex,
            nextCache,
            previousSnapshot.Generation + 1L);
        return new LibraryResourceIndexMutationReceipt(currentSnapshot, mutationResult);
    }

    private LibraryResourceIndexSnapshot CaptureSnapshotUnsafe()
    {
        return currentSnapshot;
    }

    private static LibraryResourceIndex NormalizeIndex(LibraryResourceIndex index)
    {
        return index ?? LibraryResourceIndex.CreateFromScanResult(new ChartScanResult());
    }

    private static bool TryFindMovedFolderReference(
        string path,
        IReadOnlyDictionary<string, LibraryFolderPathChange> movesByOldPath,
        out LibraryFolderPathChange move)
    {
        move = null;
        string currentPath = NormalizeDirectoryPath(path);
        while (!string.IsNullOrWhiteSpace(currentPath))
        {
            if (movesByOldPath.TryGetValue(currentPath, out move))
            {
                return true;
            }

            string parentPath = NormalizeDirectoryPath(GetParentDirectory(currentPath));
            if (string.IsNullOrWhiteSpace(parentPath)
                || string.Equals(parentPath, currentPath, StringComparison.OrdinalIgnoreCase))
            {
                break;
            }
            currentPath = parentPath;
        }
        return false;
    }

    private static string NormalizeDirectoryPath(string path)
    {
        return string.IsNullOrWhiteSpace(path)
            ? string.Empty
            : path.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
    }

    private static string GetParentDirectory(string path)
    {
        try
        {
            string normalizedPath = NormalizeDirectoryPath(path);
            return string.IsNullOrWhiteSpace(normalizedPath)
                ? null
                : Path.GetDirectoryName(normalizedPath);
        }
        catch (ArgumentException)
        {
            return null;
        }
    }
}
