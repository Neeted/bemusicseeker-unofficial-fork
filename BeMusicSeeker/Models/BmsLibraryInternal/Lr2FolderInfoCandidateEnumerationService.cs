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

internal sealed class Lr2TextMetadataCandidateSnapshot(
    Lr2FolderInfoCandidateSnapshot folderInfoCandidates,
    IReadOnlyList<string> textFileDirectories)
{
    public Lr2FolderInfoCandidateSnapshot FolderInfoCandidates { get; } =
        folderInfoCandidates ?? new Lr2FolderInfoCandidateSnapshot([], new Dictionary<string, RootFileEnumerationEntry>(StringComparer.OrdinalIgnoreCase), discoveryComplete: true);

    public IReadOnlyList<string> TextFileDirectories { get; } = textFileDirectories ?? [];
}

internal static class Lr2FolderInfoCandidateEnumerationService
{
    internal static Lr2FolderInfoCandidateSnapshot CreateSnapshot(
        IEnumerable<string> rootDirectories,
        IEnumerable<string> targetDirectories,
        EverythingNative everythingNative)
    {
        return CreateTextMetadataSnapshot(rootDirectories, targetDirectories, everythingNative).FolderInfoCandidates;
    }

    internal static Lr2TextMetadataCandidateSnapshot CreateTextMetadataSnapshot(
        IEnumerable<string> rootDirectories,
        IEnumerable<string> targetDirectories,
        EverythingNative everythingNative)
    {
        HashSet<string> targetSet = new((targetDirectories ?? [])
            .Select(Lr2FolderPath.NormalizeDirectoryPath)
            .Where(path => !string.IsNullOrWhiteSpace(path)), StringComparer.OrdinalIgnoreCase);
        if (targetSet.Count == 0)
        {
            return CreateEmptyTextMetadataSnapshot();
        }

        List<string> roots = [.. (rootDirectories ?? [])
            .Where(path => !string.IsNullOrWhiteSpace(path) && LongPathFileSystem.DirectoryExists(path))
            .Select(LongPathFileSystem.NormalizePathForStorage)
            .Distinct(StringComparer.OrdinalIgnoreCase)];
        if (roots.Count == 0)
        {
            return CreateEmptyTextMetadataSnapshot();
        }

        RootFileEnumerationGroup[] groups = [new RootFileEnumerationGroup(ChartDirectoryScanBuilder.TextGroupName, ChartDirectoryScanBuilder.TextExtensions)];
        RootFileEnumerationResult result = RootFileEnumerationService.EnumerateFilesWithFallback(roots, groups, everythingNative);
        if (!result.Success)
        {
            throw new InvalidOperationException("folderinfo grouped enumeration failed: " + (result.ErrorReason ?? "unknown"));
        }

        var entriesByPath = new Dictionary<string, RootFileEnumerationEntry>(StringComparer.OrdinalIgnoreCase);
        var textFileDirectories = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (RootFileEnumerationEntry entry in result.GetEntries(ChartDirectoryScanBuilder.TextGroupName))
        {
            string path = NormalizeTextFilePath(entry?.Path);
            if (string.IsNullOrWhiteSpace(path))
            {
                continue;
            }

            string directoryPath = Lr2FolderPath.NormalizeDirectoryPath(Path.GetDirectoryName(path));
            if (string.IsNullOrWhiteSpace(directoryPath) || !targetSet.Contains(directoryPath))
            {
                continue;
            }

            textFileDirectories.Add(directoryPath);
            if (IsFolderInfoPath(path))
            {
                entriesByPath[path] = new RootFileEnumerationEntry(path, entry.LastWriteTimeUtc, entry.FileSize);
            }
        }

        return new Lr2TextMetadataCandidateSnapshot(
            CreateFolderInfoSnapshot(entriesByPath, discoveryComplete: true),
            [.. textFileDirectories.OrderBy(path => path, StringComparer.OrdinalIgnoreCase)]);
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
        string normalized = NormalizeTextFilePath(path);
        return IsFolderInfoPath(normalized) ? normalized : null;
    }

    private static string NormalizeTextFilePath(string path)
    {
        if (string.IsNullOrWhiteSpace(path)
            || !string.Equals(Path.GetExtension(path), ".txt", StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }

        try
        {
            return LongPathFileSystem.NormalizePathForStorage(path);
        }
        catch (Exception ex) when (ex is ArgumentException || ex is NotSupportedException || ex is PathTooLongException)
        {
            return null;
        }
    }

    private static bool IsFolderInfoPath(string path)
    {
        return !string.IsNullOrWhiteSpace(path)
            && string.Equals(Path.GetFileName(path), "folderinfo.txt", StringComparison.OrdinalIgnoreCase);
    }

    private static Lr2FolderInfoCandidateSnapshot CreateFolderInfoSnapshot(
        IReadOnlyDictionary<string, RootFileEnumerationEntry> entriesByPath,
        bool discoveryComplete)
    {
        return new Lr2FolderInfoCandidateSnapshot(
            [.. (entriesByPath?.Keys ?? []).OrderBy(path => path, StringComparer.OrdinalIgnoreCase)],
            entriesByPath ?? new Dictionary<string, RootFileEnumerationEntry>(StringComparer.OrdinalIgnoreCase),
            discoveryComplete);
    }

    private static Lr2TextMetadataCandidateSnapshot CreateEmptyTextMetadataSnapshot()
    {
        return new Lr2TextMetadataCandidateSnapshot(
            new Lr2FolderInfoCandidateSnapshot([], new Dictionary<string, RootFileEnumerationEntry>(StringComparer.OrdinalIgnoreCase), discoveryComplete: true),
            []);
    }

}
