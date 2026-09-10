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
    /// Rewrites multiple directory subtrees in one current-index mutation.
    /// </summary>
    internal LibraryResourceIndexMovedFoldersResult UpdateMovedFolderReferences(
        IEnumerable<LibraryFolderPathChange> movedFolders)
    {
        Stopwatch stopwatch = Stopwatch.StartNew();
        List<LibraryFolderPathChange> moves = [.. (movedFolders ?? [])
            .Where(move => !string.IsNullOrWhiteSpace(move?.OldFolderPath)
                && !string.IsNullOrWhiteSpace(move.NewFolderPath)
                && !move.OldFolderPath.Equals(move.NewFolderPath, StringComparison.OrdinalIgnoreCase))
            .GroupBy(move => NormalizeDirectoryPath(move.OldFolderPath), StringComparer.OrdinalIgnoreCase)
            .Select(group => group.Last())];

        lock (gate)
        {
            List<string> lookupKeys = [.. currentSnapshot.DirectoryLookupCache.Keys ?? []];
            Dictionary<string, LibraryFolderPathChange> movesByOldPath = moves.ToDictionary(
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

            LibraryResourceIndexMutationReceipt receipt = MutateCurrentUnsafe(
                cache => cache.ReplaceDirsWithResult(replacements));
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
            return MutateCurrentUnsafe(cache =>
            {
                DirectoryResourceLookupCache.ReverseLookupMutationResult result =
                    DirectoryResourceLookupCache.ReverseLookupMutationResult.Empty;
                foreach (string directory in sourceDirectories ?? [])
                {
                    result = result.Combine(cache.RemoveUnderSourceDirectory(directory));
                }
                return result;
            });
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
    /// Atomically removes a source subtree and adds the selected replacement directories.
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
        lock (gate)
        {
            List<string> replacements = [.. (replacementDirectories ?? [])
                .Where(path => !string.IsNullOrWhiteSpace(path))
                .Distinct(StringComparer.OrdinalIgnoreCase)];
            DirectoryResourceLookupCache previousCache =
                currentSnapshot.DirectoryLookupCache;
            HashSet<string> affectedDirectories = [.. replacements];
            foreach (string currentDirectory in previousCache.Keys.Where(path =>
                (path + Path.DirectorySeparatorChar).StartsWith(
                    sourceDirectory + Path.DirectorySeparatorChar,
                    StringComparison.OrdinalIgnoreCase)))
            {
                affectedDirectories.Add(currentDirectory);
            }
            return MutateCurrentUnsafe(cache =>
            {
                DirectoryResourceLookupCache.ReverseLookupMutationResult result =
                    cache.RemoveUnderSourceDirectory(sourceDirectory);
                foreach (string directoryPath in replacements)
                {
                    result = result.Combine(cache.AddDir(directoryPath, replacementScan));
                }
                return previousCache.HasSameDirectoryEntries(cache, affectedDirectories)
                    ? DirectoryResourceLookupCache.ReverseLookupMutationResult.Empty
                    : result;
            });
        }
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

        nextCache.FreezeReverseLookupChanges();
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
