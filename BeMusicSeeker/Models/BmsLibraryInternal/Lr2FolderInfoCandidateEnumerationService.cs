using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using BeMusicSeeker.Models.Utils;

namespace BeMusicSeeker.Models.BmsLibraryInternal;

internal sealed class Lr2FolderInfoCandidateSnapshot(
    IReadOnlyList<string> paths,
    IReadOnlyDictionary<string, RootFileEnumerationEntry> entriesByPath,
    bool discoveryComplete)
{
    public IReadOnlyList<string> Paths { get; } = paths ?? [];

    public IReadOnlyDictionary<string, RootFileEnumerationEntry> EntriesByPath { get; } =
        entriesByPath ?? new Dictionary<string, RootFileEnumerationEntry>(StringComparer.OrdinalIgnoreCase);

    public bool DiscoveryComplete { get; } = discoveryComplete;
}

internal static class Lr2FolderInfoCandidateEnumerationService
{
    internal static Lr2FolderInfoCandidateSnapshot CreateSnapshot(
        IEnumerable<string> rootDirectories,
        IEnumerable<string> targetDirectories,
        bool forceManagedEnumeration = false)
    {
        HashSet<string> targetSet = new((targetDirectories ?? [])
            .Select(Lr2FolderPath.NormalizeDirectoryPath)
            .Where(path => !string.IsNullOrWhiteSpace(path)), StringComparer.OrdinalIgnoreCase);
        if (targetSet.Count == 0)
        {
            return new Lr2FolderInfoCandidateSnapshot([], new Dictionary<string, RootFileEnumerationEntry>(StringComparer.OrdinalIgnoreCase), discoveryComplete: true);
        }

        List<string> roots = [.. (rootDirectories ?? [])
            .Where(path => !string.IsNullOrWhiteSpace(path) && Directory.Exists(path))
            .Select(Path.GetFullPath)
            .Distinct(StringComparer.OrdinalIgnoreCase)];
        if (roots.Count == 0)
        {
            return new Lr2FolderInfoCandidateSnapshot([], new Dictionary<string, RootFileEnumerationEntry>(StringComparer.OrdinalIgnoreCase), discoveryComplete: true);
        }

        RootFileEnumerationGroup[] groups = [new RootFileEnumerationGroup(ChartDirectoryScanBuilder.TextGroupName, ChartDirectoryScanBuilder.TextExtensions)];
        RootFileEnumerationResult result = forceManagedEnumeration
            ? new FastRootFileEnumerator().EnumerateFiles(roots, groups)
            : RootFileEnumerationService.EnumerateFilesWithFallback(roots, groups);
        if (!result.Success)
        {
            return new Lr2FolderInfoCandidateSnapshot([], new Dictionary<string, RootFileEnumerationEntry>(StringComparer.OrdinalIgnoreCase), discoveryComplete: false);
        }

        var entriesByPath = new Dictionary<string, RootFileEnumerationEntry>(StringComparer.OrdinalIgnoreCase);
        foreach (RootFileEnumerationEntry entry in result.GetEntries(ChartDirectoryScanBuilder.TextGroupName))
        {
            string path = NormalizeFolderInfoPath(entry?.Path);
            if (string.IsNullOrWhiteSpace(path))
            {
                continue;
            }

            string directoryPath = Lr2FolderPath.NormalizeDirectoryPath(Path.GetDirectoryName(path));
            if (!string.IsNullOrWhiteSpace(directoryPath) && targetSet.Contains(directoryPath))
            {
                entriesByPath[path] = new RootFileEnumerationEntry(path, entry.LastWriteTimeUtc, entry.FileSize);
            }
        }

        return new Lr2FolderInfoCandidateSnapshot(
            [.. entriesByPath.Keys.OrderBy(path => path, StringComparer.OrdinalIgnoreCase)],
            entriesByPath,
            discoveryComplete: true);
    }

    internal static Lr2FolderInfoCandidateSnapshot CreateSnapshotFromTargetDirectories(
        IEnumerable<string> rootDirectories,
        IEnumerable<string> targetDirectories)
    {
        List<string> roots = [.. (rootDirectories ?? [])
            .Select(Lr2FolderPath.NormalizeDirectoryPath)
            .Where(path => !string.IsNullOrWhiteSpace(path))
            .Distinct(StringComparer.OrdinalIgnoreCase)];
        HashSet<string> targetSet = new((targetDirectories ?? [])
            .Select(Lr2FolderPath.NormalizeDirectoryPath)
            .Where(path => !string.IsNullOrWhiteSpace(path)
                && roots.Any(root => Lr2FolderPath.IsSameOrDescendant(path, root))), StringComparer.OrdinalIgnoreCase);
        if (roots.Count == 0 || targetSet.Count == 0)
        {
            return new Lr2FolderInfoCandidateSnapshot([], new Dictionary<string, RootFileEnumerationEntry>(StringComparer.OrdinalIgnoreCase), discoveryComplete: true);
        }

        var entriesByPath = new Dictionary<string, RootFileEnumerationEntry>(StringComparer.OrdinalIgnoreCase);
        foreach (string directoryPath in targetSet.OrderBy(path => path, StringComparer.OrdinalIgnoreCase))
        {
            RootFileEnumerationEntry entry = CreateFolderInfoEntry(directoryPath);
            if (entry != null)
            {
                entriesByPath[entry.Path] = entry;
            }
        }

        return new Lr2FolderInfoCandidateSnapshot(
            [.. entriesByPath.Keys.OrderBy(path => path, StringComparer.OrdinalIgnoreCase)],
            entriesByPath,
            discoveryComplete: true);
    }

    internal static Lr2FolderInfoCandidateSnapshot CreateSnapshotFromEntries(
        IEnumerable<RootFileEnumerationEntry> entries,
        IEnumerable<string> targetDirectories,
        bool discoveryComplete = true)
    {
        List<RootFileEnumerationEntry> entryList = [.. entries ?? []];
        return CreateSnapshotFromSurface(
            entryList.Select(entry => entry?.Path),
            entryList,
            targetDirectories,
            discoveryComplete);
    }

    internal static Lr2FolderInfoCandidateSnapshot CreateSnapshotFromSurface(
        IEnumerable<string> folderInfoFilePaths,
        IEnumerable<RootFileEnumerationEntry> entries,
        IEnumerable<string> targetDirectories,
        bool discoveryComplete = true)
    {
        HashSet<string> targetSet = new((targetDirectories ?? [])
            .Select(Lr2FolderPath.NormalizeDirectoryPath)
            .Where(path => !string.IsNullOrWhiteSpace(path)), StringComparer.OrdinalIgnoreCase);
        if (targetSet.Count == 0)
        {
            return new Lr2FolderInfoCandidateSnapshot([], new Dictionary<string, RootFileEnumerationEntry>(StringComparer.OrdinalIgnoreCase), discoveryComplete);
        }

        var entriesByPath = new Dictionary<string, RootFileEnumerationEntry>(StringComparer.OrdinalIgnoreCase);
        foreach (RootFileEnumerationEntry entry in entries ?? [])
        {
            string path = NormalizeFolderInfoPath(entry?.Path);
            if (string.IsNullOrWhiteSpace(path))
            {
                continue;
            }

            string directoryPath = Lr2FolderPath.NormalizeDirectoryPath(Path.GetDirectoryName(path));
            if (!string.IsNullOrWhiteSpace(directoryPath) && targetSet.Contains(directoryPath))
            {
                entriesByPath[path] = new RootFileEnumerationEntry(path, entry.LastWriteTimeUtc, entry.FileSize);
            }
        }
        foreach (string candidatePath in folderInfoFilePaths ?? [])
        {
            string path = NormalizeFolderInfoPath(candidatePath);
            if (string.IsNullOrWhiteSpace(path) || entriesByPath.ContainsKey(path))
            {
                continue;
            }

            string directoryPath = Lr2FolderPath.NormalizeDirectoryPath(Path.GetDirectoryName(path));
            if (!string.IsNullOrWhiteSpace(directoryPath) && targetSet.Contains(directoryPath))
            {
                entriesByPath[path] = new RootFileEnumerationEntry(path);
            }
        }

        return new Lr2FolderInfoCandidateSnapshot(
            [.. entriesByPath.Keys.OrderBy(path => path, StringComparer.OrdinalIgnoreCase)],
            entriesByPath,
            discoveryComplete);
    }

    private static string NormalizeFolderInfoPath(string path)
    {
        if (string.IsNullOrWhiteSpace(path)
            || !string.Equals(Path.GetFileName(path), "folderinfo.txt", StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }

        try
        {
            return Path.GetFullPath(path);
        }
        catch (Exception ex) when (ex is ArgumentException || ex is NotSupportedException || ex is PathTooLongException)
        {
            return null;
        }
    }

    private static RootFileEnumerationEntry CreateFolderInfoEntry(string directoryPath)
    {
        try
        {
            string path = Path.Combine(directoryPath, "folderinfo.txt");
            var fileInfo = new FileInfo(path);
            return fileInfo.Exists
                ? new RootFileEnumerationEntry(fileInfo.FullName, fileInfo.LastWriteTimeUtc, fileInfo.Length)
                : null;
        }
        catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException || ex is ArgumentException || ex is NotSupportedException || ex is PathTooLongException)
        {
            return null;
        }
    }
}
