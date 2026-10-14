using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using BeMusicSeeker.Properties;
using SevenZipExtractor;

namespace BeMusicSeeker.Models.BmsLibraryInternal;

/// <summary>導入とメタデータ取込みに共通の、安全性検査と一括展開を行います。</summary>
internal static class SevenZipArchiveExtractor
{
    private const string NativeArchitectureDirectoryName = "x64";

    /// <summary>アプリ同梱のx64展開ライブラリを解決し、未配置なら処理を失敗させます。</summary>
    public static string ResolveBundledSevenZipLibraryPath()
    {
        string bundledLibraryPath = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "libs", NativeArchitectureDirectoryName, "7z.dll");
        if (File.Exists(bundledLibraryPath))
        {
            return bundledLibraryPath;
        }

        throw new FileNotFoundException(Resources.Warn_ArchiveBundledSevenZipMissingDetail, bundledLibraryPath);
    }

    /// <summary>借用元を消費せず同梱ライブラリで展開し、日時復元用のエントリ情報を返します。</summary>
    public static List<ArchiveEntryMetadata> ExtractArchiveEntries(string archivePath, string extractedTempDirectoryPath)
    {
        try
        {
            return ExtractArchiveEntries(archivePath, ResolveBundledSevenZipLibraryPath(), extractedTempDirectoryPath);
        }
        catch (OutOfMemoryException exception)
        {
            Environment.FailFast("アーカイブ展開の準備中にメモリが不足しました。", exception);
            throw;
        }
    }

    /// <summary>指定ライブラリで借用元を一括展開します。取得した入力Streamと書庫を所有し、通常の失敗でも解放します。</summary>
    /// <remarks>返らないコンストラクター内部のnative資源回収は依存の保証範囲に従います。捕捉したOOMは入力失敗として呼出元へ渡しません。</remarks>
    public static List<ArchiveEntryMetadata> ExtractArchiveEntries(string archivePath, string nativeLibraryPath, string extractedTempDirectoryPath)
    {
        try
        {
            List<ArchiveEntryMetadata> archiveEntries = [];
            // native生成より先に入力を所有し、書庫生成・解放の失敗でも外側の通常解放へ進みます。
            using (FileStream source = File.OpenRead(archivePath))
            {
                SevenZipFormat? format = Path.GetExtension(archivePath).TrimStart('.').Trim().ToLowerInvariant() switch
                {
                    "zip" => SevenZipFormat.Zip,
                    "7z" => SevenZipFormat.SevenZip,
                    "lzh" => SevenZipFormat.Lzh,
                    // RAR/RAR5と未知拡張は従来どおり依存の署名判定へ渡します。
                    _ => null
                };
                using (var archiveFile = new ArchiveFile(source, format, nativeLibraryPath))
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
            }

            return archiveEntries;
        }
        catch (OutOfMemoryException exception)
        {
            Environment.FailFast("アーカイブ展開中にメモリが不足しました。", exception);
            throw;
        }
        catch (SevenZipException exception) when (exception.InnerException is OutOfMemoryException)
        {
            // 固定依存のnative初期化だけが直接内部例外として運ぶOOMを扱います。
            Environment.FailFast("アーカイブ展開ライブラリの初期化中にメモリが不足しました。", exception.InnerException);
            throw;
        }
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
