using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using BeMusicSeeker.Models.Utils;

namespace BeMusicSeeker.Models.BmsLibraryInternal;

internal static class Lr2FolderDirectoryEnumerationService
{
    internal static IReadOnlyDictionary<string, RootFileEnumerationEntry> CreateEntriesFromSurface(
        IReadOnlyDictionary<string, RootFileEnumerationEntry> sourceEntries,
        IEnumerable<string> targetDirectories)
    {
        HashSet<string> targetSet = new((targetDirectories ?? [])
            .Select(Lr2FolderPath.NormalizeDirectoryPath)
            .Where(path => !string.IsNullOrWhiteSpace(path)), StringComparer.OrdinalIgnoreCase);
        if (sourceEntries == null || sourceEntries.Count == 0 || targetSet.Count == 0)
        {
            return new Dictionary<string, RootFileEnumerationEntry>(StringComparer.OrdinalIgnoreCase);
        }

        var entries = new Dictionary<string, RootFileEnumerationEntry>(StringComparer.OrdinalIgnoreCase);
        var missingTargets = new HashSet<string>(targetSet, StringComparer.OrdinalIgnoreCase);
        foreach (string target in targetSet)
        {
            if (!sourceEntries.TryGetValue(target, out RootFileEnumerationEntry sourceEntry))
            {
                continue;
            }

            string key = Lr2FolderPath.NormalizeDirectoryPath(sourceEntry?.Path);
            if (!string.Equals(key, target, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            entries[target] = new RootFileEnumerationEntry(target, sourceEntry.LastWriteTimeUtc, sourceEntry.FileSize);
            missingTargets.Remove(target);
        }
        if (missingTargets.Count == 0)
        {
            return entries;
        }

        foreach (RootFileEnumerationEntry sourceEntry in sourceEntries.Values)
        {
            string key = Lr2FolderPath.NormalizeDirectoryPath(sourceEntry?.Path);
            if (string.IsNullOrWhiteSpace(key) || !missingTargets.Contains(key))
            {
                continue;
            }

            entries[key] = new RootFileEnumerationEntry(key, sourceEntry.LastWriteTimeUtc, sourceEntry.FileSize);
            missingTargets.Remove(key);
            if (missingTargets.Count == 0)
            {
                break;
            }
        }
        return entries;
    }

    /// <summary>
    /// Projects grouped directory enumeration into the requested normalized target entries.
    /// </summary>
    /// <param name="rootFileEnumerator">Optional internal grouped enumerator; null preserves the production fallback.</param>
    internal static IReadOnlyDictionary<string, RootFileEnumerationEntry> CreateEntriesFromGroupedEnumeration(
        IEnumerable<string> rootDirectories,
        IEnumerable<string> targetDirectories,
        EverythingNative everythingNative,
        IRootFileEnumerator rootFileEnumerator = null)
    {
        HashSet<string> targetSet = new((targetDirectories ?? [])
            .Select(Lr2FolderPath.NormalizeDirectoryPath)
            .Where(path => !string.IsNullOrWhiteSpace(path)), StringComparer.OrdinalIgnoreCase);
        if (targetSet.Count == 0)
        {
            return new Dictionary<string, RootFileEnumerationEntry>(StringComparer.OrdinalIgnoreCase);
        }

        List<string> roots = [.. (rootDirectories ?? [])];
        RootFileEnumerationResult result = RootFileEnumerationService.EnumerateFilesWithFallback(
            roots,
            [new RootFileEnumerationGroup(RootFileEnumerationService.DirectoriesGroupName, [], includeDirectories: true)],
            everythingNative,
            retryEmptyEverythingResultWithFastEnumerator: true,
            rootFileEnumerator: rootFileEnumerator);
        return CreateEntriesFromGroupedResult(result, roots, targetSet);
    }

    /// <summary>
    /// Projects a grouped directory enumeration result and completes exact target directories that were omitted
    /// by the grouped surface using direct metadata reads within the supplied roots.
    /// </summary>
    /// <param name="result">The grouped enumeration result to project.</param>
    /// <param name="rootDirectories">The normalized-path roots that authorize direct target reads.</param>
    /// <param name="targetDirectories">The exact directory targets to project.</param>
    /// <param name="directoryEntryReader">The direct metadata reader; a null result or timestamp means the target is unavailable.</param>
    internal static IReadOnlyDictionary<string, RootFileEnumerationEntry> CreateEntriesFromGroupedResult(
        RootFileEnumerationResult result,
        IEnumerable<string> rootDirectories,
        IEnumerable<string> targetDirectories,
        Func<string, RootFileEnumerationEntry> directoryEntryReader = null)
    {
        if (!result.Success)
        {
            if (RootFileEnumerationService.IsBridgeContractFailure(result?.ErrorReason))
            {
                throw new InvalidOperationException("directory metadata grouped enumeration failed: " + result.ErrorReason);
            }

            return new Dictionary<string, RootFileEnumerationEntry>(StringComparer.OrdinalIgnoreCase);
        }

        IReadOnlyDictionary<string, RootFileEnumerationEntry> surfaceEntries = CreateEntriesFromSurface(
            result.EntriesByGroup.TryGetValue(RootFileEnumerationService.DirectoriesGroupName, out Dictionary<string, RootFileEnumerationEntry> entries)
            ? entries
            : new Dictionary<string, RootFileEnumerationEntry>(StringComparer.OrdinalIgnoreCase),
            targetDirectories);

        return CompleteMissingEntriesFromDirectoryMetadata(
            surfaceEntries,
            rootDirectories,
            targetDirectories,
            directoryEntryReader);
    }

    /// <summary>
    /// Completes exact target directories missing from a captured surface or metadata without broadening the allowed roots.
    /// </summary>
    /// <param name="sourceEntries">The captured directory entries, which take precedence over direct reads.</param>
    /// <param name="rootDirectories">The roots under which direct reads are permitted.</param>
    /// <param name="targetDirectories">The exact directory targets to complete.</param>
    /// <param name="directoryEntryReader">The direct metadata reader; a null result or timestamp means the target is unavailable.</param>
    internal static IReadOnlyDictionary<string, RootFileEnumerationEntry> CompleteMissingEntriesFromDirectoryMetadata(
        IReadOnlyDictionary<string, RootFileEnumerationEntry> sourceEntries,
        IEnumerable<string> rootDirectories,
        IEnumerable<string> targetDirectories,
        Func<string, RootFileEnumerationEntry> directoryEntryReader = null)
    {
        HashSet<string> targetSet = new((targetDirectories ?? [])
            .Select(Lr2FolderPath.NormalizeDirectoryPath)
            .Where(path => !string.IsNullOrWhiteSpace(path)), StringComparer.OrdinalIgnoreCase);
        IReadOnlyDictionary<string, RootFileEnumerationEntry> projectedEntries = CreateEntriesFromSurface(sourceEntries, targetSet);
        HashSet<string> missingTargets = [.. targetSet
            .Where(target => !projectedEntries.TryGetValue(target, out RootFileEnumerationEntry entry)
                || entry?.LastWriteTimeUtc == null)];
        if (missingTargets.Count == 0)
        {
            return projectedEntries;
        }

        HashSet<string> allowedRoots = new((rootDirectories ?? [])
            .Select(Lr2FolderPath.NormalizeDirectoryPath)
            .Where(path => !string.IsNullOrWhiteSpace(path)), StringComparer.OrdinalIgnoreCase);
        if (allowedRoots.Count == 0)
        {
            return projectedEntries;
        }

        directoryEntryReader ??= RootFileEnumerationEntry.FromDirectoryInfo;
        var completedEntries = new Dictionary<string, RootFileEnumerationEntry>(projectedEntries, StringComparer.OrdinalIgnoreCase);
        foreach (string target in missingTargets)
        {
            if (!allowedRoots.Any(root => Lr2FolderPath.IsSameOrDescendantNormalized(target, root)))
            {
                continue;
            }

            RootFileEnumerationEntry directEntry;
            try
            {
                directEntry = directoryEntryReader(target);
            }
            catch (Exception ex) when (ex is IOException
                || ex is UnauthorizedAccessException
                || ex is ArgumentException
                || ex is NotSupportedException
                || ex is PathTooLongException)
            {
                continue;
            }

            string directPath = Lr2FolderPath.NormalizeDirectoryPath(directEntry?.Path);
            if (!string.Equals(directPath, target, StringComparison.OrdinalIgnoreCase)
                || !directEntry.LastWriteTimeUtc.HasValue)
            {
                continue;
            }

            completedEntries[target] = new RootFileEnumerationEntry(
                target,
                directEntry.LastWriteTimeUtc,
                directEntry.FileSize);
        }

        return completedEntries;
    }

}
