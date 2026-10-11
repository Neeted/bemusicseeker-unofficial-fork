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
/// LR2同期の固定入力とケース専有のFS資源を準備します。
/// </summary>
internal static class Lr2SongDbSyncTestSupport
{
    /// <summary>
    /// ケース専有の一時ディレクトリとLR2接続の準備を所有します。
    /// </summary>
    internal sealed class TestDatabaseScope : IDisposable
    {
        /// <summary>
        /// このscopeだけが所有する一時ディレクトリを返します。
        /// </summary>
        internal string DirectoryPath { get; }

        /// <summary>
        /// 専有ディレクトリ内のsong.dbパスを返します。
        /// </summary>
        internal string SongDbPath { get; }

        private TestDatabaseScope(string directoryPath)
        {
            DirectoryPath = directoryPath;
            SongDbPath = Path.Combine(directoryPath, "song.db");
            using var _ = new LR2SongDBExtended(SongDbPath);
        }

        /// <summary>
        /// ケースごとに一意なディレクトリでDB準備を所有します。
        /// </summary>
        /// <returns>一時ディレクトリとDBを所有するscope。</returns>
        internal static TestDatabaseScope Create()
        {
            string directoryPath = Path.Combine(Path.GetTempPath(), nameof(Lr2SongDbSyncTestSupport), Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(directoryPath);
            return new TestDatabaseScope(directoryPath);
        }

        /// <summary>
        /// 所有する一時資源を削除します。
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
    /// LR2フォルダ行の末尾区切り付き保存パスへ変換します。
    /// </summary>
    internal static string ToFolderPath(string directoryPath)
    {
        return Path.GetFullPath(directoryPath).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)
            + Path.DirectorySeparatorChar;
    }

    /// <summary>
    /// 捕捉済みの譜面バイト列に合うダイジェストで共通譜面入力を作ります。
    /// </summary>
    internal static ChartFile CreateSyncTestFile(string path, ChartFileSnapshot snapshot)
    {
        ChartFile file = ChartTestValues.Empty() with
        {
            Path = path
        };
        file = file with { Md5 = snapshot.Md5 };
        file = file with { Sha256 = snapshot.Sha256 };
        return file;
    }

    /// <summary>
    /// 直接同期の入力となる最小BMSテキストを書きます。
    /// </summary>
    internal static void WriteBasicBms(string chartPath, string title, string resourcePath = "sound.wav")
    {
        File.WriteAllText(
            chartPath,
            "#TITLE " + title + "\r\n#WAV01 " + resourcePath + "\r\n",
            new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
    }

    /// <summary>
    /// LR2の資源長を超える固定入力を作ります。
    /// </summary>
    internal static string CreateLr2TooLongResourcePath()
    {
        return new string('a', 270) + ".wav";
    }

    /// <summary>
    /// 固定entry集合から成功した完全列挙の入力を作ります。
    /// </summary>
    internal static RootFileEnumerationResult CreateRootFileEnumerationResult(
        string groupName,
        IEnumerable<RootFileEnumerationEntry> entries)
    {
        var result = new RootFileEnumerationResult
        {
            Success = true,
            IsComplete = true,
            BackendName = "deterministic-test"
        };
        result.InitializeGroup(groupName);
        foreach (RootFileEnumerationEntry entry in entries ?? [])
        {
            result.AddEntry(groupName, entry);
        }
        return result;
    }

    /// <summary>
    /// FS互換のパス比較でファイル列挙entryを捕捉します。
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
    /// FS互換のパス比較でディレクトリ列挙entryを捕捉します。
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
    /// 明示版または現行解析版の譜面情報入力を作ります。
    /// </summary>
    internal static BeMusicSeeker.Models.ChartDetails CreateChartInfo(string sha256, string md5, int level, int? parserVersion = null)
    {
        return new BeMusicSeeker.Models.ChartDetails
        {
            sha256 = sha256,
            md5 = md5,
            level = level,
            parser_version = parserVersion ?? BmsLibraryDbGateway.CurrentChartInfoParserVersion
        };
    }

    /// <summary>
    /// 直接同期の譜面情報書込みを実catalog保存入口へ接続します。
    /// </summary>
    internal static Func<CatalogChartInfoWriteRequest, CatalogChartInfoWriteReceipt> CreateDirectChartInfoWriter(
        LR2SongDBExtended songDb)
    {
        return request =>
        {
            if (request == null || !request.HasChanges)
            {
                return CatalogChartInfoWriteReceipt.NotApplied;
            }

            BmsLibraryDbGateway.UpsertChartInfoBackfillChunk(
                songDb,
                request.DigestEntries,
                request.ChartInfoRows,
                request.ParseFailureRows,
                request.ParseFailureDeleteMd5s);
            return new CatalogChartInfoWriteReceipt(
                applied: true,
                request.DigestEntries.Count,
                request.ChartInfoRows.Count,
                request.ParseFailureRows.Count,
                request.ParseFailureDeleteMd5s.Count);
        };
    }

}
