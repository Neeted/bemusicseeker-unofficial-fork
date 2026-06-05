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
        IEnumerable<string> targetDirectories)
    {
        HashSet<string> targetSet = [.. (targetDirectories ?? [])
            .Select(Lr2FolderPath.NormalizeDirectoryPath)
            .Where(path => !string.IsNullOrWhiteSpace(path))];
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

        RootFileEnumerationResult result = RootFileEnumerationService.EnumerateFilesWithFallback(
            roots,
            [new RootFileEnumerationGroup(ChartDirectoryScanBuilder.TextGroupName, ChartDirectoryScanBuilder.TextExtensions)]);
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
}
