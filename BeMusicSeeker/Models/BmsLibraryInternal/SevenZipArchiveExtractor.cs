using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using BeMusicSeeker.Properties;
using SevenZipExtractor;

namespace BeMusicSeeker.Models.BmsLibraryInternal;

internal static class SevenZipArchiveExtractor
{
    private const string NativeArchitectureDirectoryName = "x64";

    public static string ResolveBundledSevenZipLibraryPath()
    {
        string bundledLibraryPath = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "libs", NativeArchitectureDirectoryName, "7z.dll");
        if (File.Exists(bundledLibraryPath))
        {
            return bundledLibraryPath;
        }

        throw new FileNotFoundException(Resources.Warn_ArchiveBundledSevenZipMissingDetail, bundledLibraryPath);
    }

    public static List<ArchiveEntryMetadata> ExtractArchiveEntries(string archivePath, string extractedTempDirectoryPath)
    {
        return ExtractArchiveEntries(archivePath, ResolveBundledSevenZipLibraryPath(), extractedTempDirectoryPath);
    }

    public static List<ArchiveEntryMetadata> ExtractArchiveEntries(string archivePath, string nativeLibraryPath, string extractedTempDirectoryPath)
    {
        List<ArchiveEntryMetadata> archiveEntries = [];
        using (var archiveFile = new ArchiveFile(archivePath, nativeLibraryPath))
        {
            ValidateArchiveEntries(archiveFile.Entries, extractedTempDirectoryPath);
            archiveFile.Extract(extractedTempDirectoryPath, false, null);
            RejectReparsePoints(extractedTempDirectoryPath);
            if (archiveFile.Entries == null)
            {
                return archiveEntries;
            }

            foreach (Entry entry in archiveFile.Entries)
            {
                archiveEntries.Add(new ArchiveEntryMetadata
                {
                    FileName = entry.FileName,
                    IsFolder = entry.IsFolder,
                    CreationTime = entry.CreationTime,
                    LastAccessTime = entry.LastAccessTime,
                    LastWriteTime = entry.LastWriteTime
                });
            }
        }

        return archiveEntries;
    }

    private static void RejectReparsePoints(string extractedTempDirectoryPath)
    {
        if (!Directory.Exists(extractedTempDirectoryPath))
        {
            return;
        }

        var directoriesToVisit = new Stack<string>();
        directoriesToVisit.Push(extractedTempDirectoryPath);
        while (directoriesToVisit.Count > 0)
        {
            string directoryPath = directoriesToVisit.Pop();
            foreach (FileSystemInfo entry in new DirectoryInfo(directoryPath).EnumerateFileSystemInfos())
            {
                if ((entry.Attributes & FileAttributes.ReparsePoint) != 0)
                {
                    throw new InvalidDataException("Archive extraction produced a reparse point: " + entry.FullName);
                }

                if (entry is DirectoryInfo)
                {
                    directoriesToVisit.Push(entry.FullName);
                }
            }
        }
    }

    private static void ValidateArchiveEntries(IEnumerable<Entry> entries, string extractedTempDirectoryPath)
    {
        if (string.IsNullOrWhiteSpace(extractedTempDirectoryPath))
        {
            throw new ArgumentException("An archive extraction directory is required.", nameof(extractedTempDirectoryPath));
        }

        string extractionRoot = Path.GetFullPath(extractedTempDirectoryPath)
            .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)
            + Path.DirectorySeparatorChar;
        foreach (Entry entry in entries ?? [])
        {
            string entryName = entry?.FileName;
            if (string.IsNullOrWhiteSpace(entryName))
            {
                throw new InvalidDataException("Archive contains an entry without a name.");
            }

            string normalizedEntryName = entryName.Replace('\\', '/');
            string[] segments = normalizedEntryName.Split('/', StringSplitOptions.RemoveEmptyEntries);
            if (Path.IsPathRooted(entryName)
                || normalizedEntryName.StartsWith("/", StringComparison.Ordinal)
                || normalizedEntryName.Contains(':', StringComparison.Ordinal)
                || segments.Any(IsWindowsPathTraversalOrAmbiguousSegment))
            {
                throw new InvalidDataException("Archive entry escapes the extraction directory: " + entryName);
            }

            string fullEntryPath = Path.GetFullPath(Path.Combine(extractionRoot, normalizedEntryName.Replace('/', Path.DirectorySeparatorChar)));
            if (!fullEntryPath.StartsWith(extractionRoot, StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidDataException("Archive entry escapes the extraction directory: " + entryName);
            }
        }
    }

    private static bool IsWindowsPathTraversalOrAmbiguousSegment(string segment)
    {
        string windowsStableSegment = segment.TrimEnd(' ', '.');
        return !string.Equals(segment, windowsStableSegment, StringComparison.Ordinal)
            || string.Equals(windowsStableSegment, ".", StringComparison.Ordinal)
            || string.Equals(windowsStableSegment, "..", StringComparison.Ordinal);
    }
}
