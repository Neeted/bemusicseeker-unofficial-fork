using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using BeMusicSeeker.Models;
using BeMusicSeeker.Models.BmsLibraryInternal;
using BeMusicSeeker.Models.LR2;
using BeMusicSeeker.Models.Utils;

namespace BeMusicSeeker.Tests;

/// <summary>
/// Provides stateless helpers and per-test filesystem resources shared by the LR2 song database sync fixtures.
/// </summary>
internal static class Lr2SongDbSyncTestSupport
{
    /// <summary>
    /// Owns one GUID-named temporary directory and its initialized LR2 song database for a single test.
    /// </summary>
    internal sealed class TestDatabaseScope : IDisposable
    {
        /// <summary>
        /// Gets the temporary directory exclusively owned by this scope.
        /// </summary>
        internal string DirectoryPath { get; }

        /// <summary>
        /// Gets the song database path within the owned temporary directory.
        /// </summary>
        internal string SongDbPath { get; }

        private TestDatabaseScope(string directoryPath)
        {
            DirectoryPath = directoryPath;
            SongDbPath = Path.Combine(directoryPath, "song.db");
            using var _ = new LR2SongDBExtended(SongDbPath);
        }

        /// <summary>
        /// Creates an isolated database scope whose directory is unique to the calling test.
        /// </summary>
        /// <returns>The scope that owns the temporary directory and database.</returns>
        internal static TestDatabaseScope Create()
        {
            string directoryPath = Path.Combine(Path.GetTempPath(), nameof(Lr2SongDbSyncTestSupport), Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(directoryPath);
            return new TestDatabaseScope(directoryPath);
        }

        /// <summary>
        /// Deletes the complete temporary resource tree owned by this scope.
        /// </summary>
        public void Dispose()
        {
            if (Directory.Exists(DirectoryPath))
            {
                Directory.Delete(DirectoryPath, recursive: true);
            }
        }
    }

    /// <summary>
    /// Converts a directory path to the trailing-separator representation stored in LR2 folder rows.
    /// </summary>
    internal static string ToFolderPath(string directoryPath)
    {
        return Path.GetFullPath(directoryPath).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)
            + Path.DirectorySeparatorChar;
    }

    /// <summary>
    /// Creates a test song row whose digests match the supplied immutable chart snapshot.
    /// </summary>
    internal static TestableBmsFile CreateSyncTestFile(string path, ChartFileSnapshot snapshot)
    {
        var file = new TestableBmsFile
        {
            path = path
        };
        file.SetHash(snapshot.Md5);
        file.ApplySha256(snapshot.Sha256);
        return file;
    }

    /// <summary>
    /// Writes the minimal deterministic BMS text used by direct synchronization tests.
    /// </summary>
    internal static void WriteBasicBms(string chartPath, string title, string resourcePath = "sound.wav")
    {
        File.WriteAllText(
            chartPath,
            "#TITLE " + title + "\r\n#WAV01 " + resourcePath + "\r\n",
            new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
    }

    /// <summary>
    /// Creates an LR2-incompatible resource path for deterministic fallback coverage.
    /// </summary>
    internal static string CreateLr2TooLongResourcePath()
    {
        return new string('a', 270) + ".wav";
    }

    /// <summary>
    /// Captures file enumeration entries keyed with filesystem-compatible path comparison.
    /// </summary>
    internal static Dictionary<string, RootFileEnumerationEntry> CreateFileEntryMap(params string[] filePaths)
    {
        var result = new Dictionary<string, RootFileEnumerationEntry>(StringComparer.OrdinalIgnoreCase);
        foreach (string filePath in filePaths ?? [])
        {
            if (string.IsNullOrWhiteSpace(filePath))
            {
                continue;
            }

            result[filePath] = new RootFileEnumerationEntry(filePath, File.GetLastWriteTimeUtc(filePath));
        }

        return result;
    }

    /// <summary>
    /// Captures directory enumeration entries keyed with filesystem-compatible path comparison.
    /// </summary>
    internal static Dictionary<string, RootFileEnumerationEntry> CreateDirectoryEntryMap(params string[] directoryPaths)
    {
        var result = new Dictionary<string, RootFileEnumerationEntry>(StringComparer.OrdinalIgnoreCase);
        foreach (string directoryPath in directoryPaths ?? [])
        {
            if (string.IsNullOrWhiteSpace(directoryPath))
            {
                continue;
            }

            result[directoryPath] = RootFileEnumerationEntry.FromDirectoryInfo(directoryPath);
        }

        return result;
    }

    /// <summary>
    /// Creates a chart-info row using the current parser contract unless a version is supplied explicitly.
    /// </summary>
    internal static LR2SongDBExtended.chart_info CreateChartInfo(string sha256, string md5, int level, int? parserVersion = null)
    {
        return new LR2SongDBExtended.chart_info
        {
            sha256 = sha256,
            md5 = md5,
            level = level,
            parser_version = parserVersion ?? BmsLibraryDbGateway.CurrentChartInfoParserVersion
        };
    }

    /// <summary>
    /// Exposes controlled mutation points needed to arrange inherited LR2 song-row fields in tests.
    /// </summary>
    internal sealed class TestableBmsFile : BMSFile
    {
        /// <summary>
        /// Sets the inherited MD5 hash field for test arrangement.
        /// </summary>
        internal void SetHash(string value)
        {
            hash = value;
        }

        /// <summary>
        /// Sets the inherited favorite value for test arrangement.
        /// </summary>
        internal void SetFavorite(int? value)
        {
            favorite = value;
        }

        /// <summary>
        /// Sets the inherited title for test arrangement.
        /// </summary>
        internal void SetTitleForTest(string value)
        {
            title = value;
        }

        /// <summary>
        /// Sets the inherited artist for test arrangement.
        /// </summary>
        internal void SetArtistForTest(string value)
        {
            artist = value;
        }

        /// <summary>
        /// Sets the inherited hash and favorite fields and returns the same test row.
        /// </summary>
        internal TestableBmsFile WithHashAndFavorite(string hashValue, int? favoriteValue)
        {
            SetHash(hashValue);
            SetFavorite(favoriteValue);
            return this;
        }
    }
}
