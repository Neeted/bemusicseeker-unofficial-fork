using System;
using System.Collections.Generic;
using System.IO;
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
            archiveFile.Extract(extractedTempDirectoryPath, false, null);
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
}
