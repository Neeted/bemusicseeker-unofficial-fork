using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using BeMusicSeeker.Models;
using BeMusicSeeker.Models.BmsLibraryInternal;
using MessageBoxButton = BeMusicSeeker.Models.UiDialogButton;
using MessageBoxImage = BeMusicSeeker.Models.UiDialogIcon;
using MessageBoxResult = BeMusicSeeker.Models.UiDialogDefaultResult;
using BeMusicSeeker.Models.LR2;
using BeMusicSeeker.Models.Utils;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace BeMusicSeeker.Tests;

internal static class BmsLibraryInitializationTestSupport
{

    internal static void WithTemporaryLr2SongDb(Action<string, string> testAction)
    {
        string tempRootPath = Path.Combine(Path.GetTempPath(), "BeMusicSeeker_InitTests_" + Guid.NewGuid().ToString("N"));
        string lr2FilesPath = Path.Combine(tempRootPath, "LR2files");
        string databaseDirectoryPath = Path.Combine(lr2FilesPath, "Database");
        string songDbPath = Path.Combine(databaseDirectoryPath, "song.db");
        Directory.CreateDirectory(databaseDirectoryPath);
        File.WriteAllBytes(songDbPath, []);
        try
        {
            testAction(tempRootPath, songDbPath);
        }
        finally
        {
            if (Directory.Exists(tempRootPath))
            {
                Directory.Delete(LongPathFileSystem.ToExtendedPath(tempRootPath), recursive: true);
            }
        }
    }

    internal static void WithTemporaryStandaloneSongDb(Action<string, string> testAction)
    {
        string tempRootPath = Path.Combine(Path.GetTempPath(), "BeMusicSeeker_StandaloneInitTests_" + Guid.NewGuid().ToString("N"));
        string databaseDirectoryPath = Path.Combine(tempRootPath, "data");
        string songDbPath = Path.Combine(databaseDirectoryPath, "song.db");
        Directory.CreateDirectory(databaseDirectoryPath);
        File.WriteAllBytes(songDbPath, []);
        try
        {
            testAction(tempRootPath, songDbPath);
        }
        finally
        {
            if (Directory.Exists(tempRootPath))
            {
                Directory.Delete(LongPathFileSystem.ToExtendedPath(tempRootPath), recursive: true);
            }
        }
    }

    /// <summary>
    /// ライブラリ機能テストのDBスキーマまたはfixtureデータを一つのトランザクションで準備します。
    /// </summary>
    /// <remarks>
    /// fixture準備のautocommit反復による共通DBロックを減らします。本番の保存経路やtransaction契約は変更しません。
    /// </remarks>
    internal static void ExecuteSongDbFixtureTransaction(
        string songDbPath,
        Action<LR2SongDBExtended> setup)
    {
        using var songDb = new LR2SongDBExtended(songDbPath);
        songDb.BeginTransaction();
        try
        {
            setup(songDb);
            songDb.Commit();
        }
        catch
        {
            songDb.Rollback();
            throw;
        }
    }

    internal static string CreateBmsonJson(string title, string subtitle, string chartName, string artist, string genre, int level, string modeHint)
    {
        return "{"
            + "\"version\":\"1.0.0\","
            + "\"info\":{"
            + "\"title\":\"" + title + "\","
            + "\"subtitle\":\"" + subtitle + "\","
            + "\"chart_name\":\"" + chartName + "\","
            + "\"artist\":\"" + artist + "\","
            + "\"genre\":\"" + genre + "\","
            + "\"level\":" + level + ","
            + "\"mode_hint\":\"" + modeHint + "\","
            + "\"banner_image\":\"banner.png\","
            + "\"back_image\":\"back.png\","
            + "\"eyecatch_image\":\"stage.png\","
            + "\"preview_music\":\"preview.ogg\","
            + "\"subartists\":[\"SubA\",\"SubB\"]"
            + "},"
            + "\"sound_channels\":[],"
            + "\"bpm_events\":[],"
            + "\"lines\":[{\"y\":0}]"
            + "}";
    }

    internal static string CreateBmsonJsonWithSound(string soundFileName)
    {
        return "{"
            + "\"version\":\"1.0.0\","
            + "\"info\":{\"title\":\"Resource\",\"artist\":\"Artist\",\"mode_hint\":\"beat-7k\"},"
            + "\"sound_channels\":[{\"name\":\"" + soundFileName + "\",\"notes\":[]}],"
            + "\"lines\":[{\"y\":0}]"
            + "}";
    }

    internal static string CreateBmsonFile(string directoryPath, string fileName, string title, string artist)
    {
        Directory.CreateDirectory(directoryPath);
        string filePath = Path.Combine(directoryPath, fileName);
        File.WriteAllText(filePath, CreateBmsonJson(title, string.Empty, string.Empty, artist, string.Empty, 1, "beat-7k"));
        return filePath;
    }

    internal static string CreateValidBmsText(string title)
    {
        return "#PLAYER 1\r\n"
            + "#TITLE " + title + "\r\n"
            + "#ARTIST Artist\r\n"
            + "#BPM 120\r\n"
            + "#WAV01 sound.wav\r\n"
            + "#00111:01\r\n";
    }

    internal static string CreateLongPathDirectory(string rootPath)
    {
        return Path.Combine(
            rootPath,
            "LongPath",
            new string('a', 70),
            new string('b', 70));
    }

    internal static int ToUnixSeconds(DateTime utcTime)
    {
        return (int)new DateTimeOffset(DateTime.SpecifyKind(utcTime, DateTimeKind.Utc)).ToUnixTimeSeconds();
    }

    internal static void ProjectCatalogState(
        SongTableFileCheckResult result,
        IEnumerable<BMSFile> currentFiles,
        IEnumerable<LR2SongDBExtended.bmson_song> currentBmsonSongs = null!,
        IEnumerable<ChartFile> currentInstallDestinationCharts = null!)
    {
        LibraryFileScanPipelineOwner.ApplyCatalogProjection(
            result,
            currentFiles,
            currentBmsonSongs,
            currentInstallDestinationCharts);
    }

    internal static string ToFolderPath(string path)
    {
        string fullPath = Path.GetFullPath(path);
        string? root = Path.GetPathRoot(fullPath);
        string trimmed = fullPath.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        string normalized = !string.IsNullOrEmpty(root)
            && string.Equals(trimmed, root.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar), StringComparison.OrdinalIgnoreCase)
                ? root.TrimEnd(Path.AltDirectorySeparatorChar)
                : trimmed;
        return normalized.EndsWith(Path.DirectorySeparatorChar.ToString(), StringComparison.Ordinal)
            || normalized.EndsWith(Path.AltDirectorySeparatorChar.ToString(), StringComparison.Ordinal)
                ? normalized
                : normalized + Path.DirectorySeparatorChar;
    }

    internal static SongTableFileCheckResult RunWithParserDegreeOverride(int? overrideValue, string songDbPath)
    {
        BmsLibraryInitializationService service = overrideValue.HasValue
            ? new BmsLibraryInitializationService(overrideValue.Value)
            : new BmsLibraryInitializationService();
        return service.ApplyFileScanDiff(
            new BmsLibraryDbGateway(songDbPath),
            new EverythingNative(ApplicationPathPolicy.Current),
            new BmsLibraryOptionsSnapshot(),
            [],
            new ChartScanExecutionResult
            {
                Success = true,
                Result = CreateScanResult([], new Dictionary<string, IEnumerable<string>>(StringComparer.OrdinalIgnoreCase))
            },
            0L,
            () => null,
            null,
            currentBmsonSongs: []);
    }

    internal static SongTableFileCheckResult RunWithCommitChunkSizeOverride(int? overrideValue, string songDbPath)
    {
        BmsLibraryInitializationService service = overrideValue.HasValue
            ? new BmsLibraryInitializationService(fileDiffParserDegreeOverride: 1, fileDiffCommitChunkSizeOverride: overrideValue.Value)
            : new BmsLibraryInitializationService(fileDiffParserDegreeOverride: 1);
        return service.ApplyFileScanDiff(
            new BmsLibraryDbGateway(songDbPath),
            new EverythingNative(ApplicationPathPolicy.Current),
            new BmsLibraryOptionsSnapshot(),
            [],
            new ChartScanExecutionResult
            {
                Success = true,
                Result = CreateScanResult([], new Dictionary<string, IEnumerable<string>>(StringComparer.OrdinalIgnoreCase))
            },
            0L,
            () => null,
            null,
            currentBmsonSongs: []);
    }

    internal static SongTableFileCheckResult RunWithInlineChartInfoBatchSizeOverride(int? overrideValue, string songDbPath)
    {
        BmsLibraryInitializationService service = overrideValue.HasValue
            ? new BmsLibraryInitializationService(fileDiffParserDegreeOverride: 1, inlineChartInfoBatchSizeOverride: overrideValue.Value)
            : new BmsLibraryInitializationService(fileDiffParserDegreeOverride: 1);
        return service.ApplyFileScanDiff(
            new BmsLibraryDbGateway(songDbPath),
            new EverythingNative(ApplicationPathPolicy.Current),
            new BmsLibraryOptionsSnapshot(),
            [],
            new ChartScanExecutionResult
            {
                Success = true,
                Result = CreateScanResult([], new Dictionary<string, IEnumerable<string>>(StringComparer.OrdinalIgnoreCase))
            },
            0L,
            () => null,
            null,
            currentBmsonSongs: []);
    }

    internal static LR2SongDBExtended.chart_info CreateMinimalChartInfoRow(string sha256, string md5)
    {
        return new LR2SongDBExtended.chart_info
        {
            sha256 = sha256,
            md5 = md5,
            charthash = sha256,
            level = 7,
            difficulty = 4,
            difficulty_defined = true,
            mainbpm = 120,
            maxbpm = 120,
            minbpm = 120,
            length = 1000,
            mode = 7,
            judge = 100,
            feature = 0,
            notes = 1,
            n = 1,
            ln = 0,
            s = 0,
            ls = 0,
            total = 100,
            total_defined = true,
            density = 1,
            peakdensity = 1,
            enddensity = 1,
            distribution = string.Empty,
            speedchange = string.Empty,
            speedchange_count = 0,
            lanenotes = string.Empty,
            parser_version = BmsLibraryDbGateway.CurrentChartInfoParserVersion,
            updated_at = DateTime.UtcNow
        };
    }

    internal static LR2SongDBExtended.chart_info_parse_failure CreateChartInfoParseFailureRow(string md5, string sha256, string path)
    {
        return new LR2SongDBExtended.chart_info_parse_failure
        {
            md5 = md5,
            sha256 = sha256,
            path = path,
            parser_version = BmsLibraryDbGateway.CurrentChartInfoParserVersion,
            failure_kind = "parse_failed",
            exception_type = "InvalidDataException",
            message = "bad bpm",
            parse_timeout_ms = null,
            updated_at = DateTime.UtcNow
        };
    }

    internal static ChartScanResult CreateScanResult(
        IEnumerable<string> chartPaths,
        IDictionary<string, IEnumerable<string>> resourcesByDirectory,
        IEnumerable<string>? directorySurfaceRoots = null)
    {
        var chartPathSet = new HashSet<string>(chartPaths ?? [], StringComparer.Ordinal);
        var chartDirectories = new HashSet<string>(
            chartPathSet.Select(Path.GetDirectoryName).Where(path => !string.IsNullOrWhiteSpace(path)).Select(path => path!),
            StringComparer.OrdinalIgnoreCase);
        foreach (string directoryPath in resourcesByDirectory?.Keys ?? Enumerable.Empty<string>())
        {
            if (!string.IsNullOrWhiteSpace(directoryPath))
            {
                chartDirectories.Add(directoryPath);
            }
        }

        var result = new ChartScanResult
        {
            ChartFilePaths = chartPathSet,
            ChartDirectories = chartDirectories
        };
        foreach (string directory in EnumerateExistingDirectorySurface(chartDirectories, directorySurfaceRoots))
        {
            RootFileEnumerationEntry entry = RootFileEnumerationEntry.FromDirectoryInfo(directory);
            if (entry != null)
            {
                result.DirectoryEntriesByPath[Lr2FolderPath.NormalizeDirectoryPath(directory)] = entry;
            }
        }
        var cache = new DirectoryResourceLookupCache();
        foreach (string chartDirectory in chartDirectories)
        {
            IEnumerable<string>? resourceFiles = null;
            resourcesByDirectory?.TryGetValue(chartDirectory, out resourceFiles);
            cache.AddDir(chartDirectory, resourceFiles ?? []);
            DirectoryResourceLookupCache.Entry entry = cache.GetEntryOrNull(chartDirectory) ?? new DirectoryResourceLookupCache.Entry();
            result.AudioRelativePathHashesByChartDirectory[chartDirectory] = entry.AudioRelativePathHashArray;
            result.ImageRelativePathHashesByChartDirectory[chartDirectory] = entry.ImageRelativePathHashArray;
            result.MovieRelativePathHashesByChartDirectory[chartDirectory] = entry.MovieRelativePathHashArray;
            result.SelfOwnedAudioRelativePathHashesByChartDirectory[chartDirectory] = entry.SelfOwnedAudioRelativePathHashArray;
            result.SelfOwnedImageRelativePathHashesByChartDirectory[chartDirectory] = entry.SelfOwnedImageRelativePathHashArray;
            result.SelfOwnedMovieRelativePathHashesByChartDirectory[chartDirectory] = entry.SelfOwnedMovieRelativePathHashArray;
            if ((resourceFiles ?? []).Any(IsDirectTextFile))
            {
                result.ChartDirectoriesWithTextFiles.Add(chartDirectory);
            }
        }
        return result;
    }

    internal static IEnumerable<string> EnumerateExistingDirectorySurface(
        IEnumerable<string> directories,
        IEnumerable<string>? roots)
    {
        var result = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (string root in roots ?? [])
        {
            string normalizedRoot = Lr2FolderPath.NormalizeDirectoryPath(root);
            if (string.IsNullOrWhiteSpace(normalizedRoot) || !Directory.Exists(normalizedRoot))
            {
                continue;
            }

            result.Add(normalizedRoot);
            foreach (string directory in Directory.EnumerateDirectories(normalizedRoot, "*", SearchOption.AllDirectories))
            {
                string normalized = Lr2FolderPath.NormalizeDirectoryPath(directory);
                if (!string.IsNullOrWhiteSpace(normalized))
                {
                    result.Add(normalized);
                }
            }
        }

        foreach (string directory in directories ?? [])
        {
            string current = Lr2FolderPath.NormalizeDirectoryPath(directory);
            while (!string.IsNullOrWhiteSpace(current) && Directory.Exists(current) && result.Add(current))
            {
                string parent = Lr2FolderPath.NormalizeDirectoryPath(Path.GetDirectoryName(current));
                if (string.IsNullOrWhiteSpace(parent) || string.Equals(parent, current, StringComparison.OrdinalIgnoreCase))
                {
                    break;
                }
                current = parent;
            }
        }
        return result;
    }

    internal static bool IsDirectTextFile(string path)
    {
        if (!string.Equals(Path.GetExtension(path ?? string.Empty), ".txt", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }
        string? directory = Path.GetDirectoryName(path);
        return string.IsNullOrWhiteSpace(directory);
    }

    internal sealed class TestableBmsFile : BMSFile
    {
        public void SetHash(string value)
        {
            hash = value;
        }

        public void SetSha256(string value)
        {
            ApplySha256(value);
        }

        public void SetFavorite(int? value)
        {
            favorite = value;
        }

        public void SetTextGroupFlagForTest(int value)
        {
            SetTextGroupFlag(value);
        }
    }

    internal sealed class TestFileMutationService : IFileMutationService
    {
        public void EnsureDirectory(string directoryPath, FileMutationOptions options = null!)
        {
            if (!string.IsNullOrWhiteSpace(directoryPath))
            {
                Directory.CreateDirectory(directoryPath);
            }
        }

        public void MoveFile(string sourcePath, string destinationPath, bool overwrite, FileMutationOptions options = null!)
        {
            throw new NotSupportedException();
        }

        public void MoveDirectory(string sourcePath, string destinationPath, bool overwrite, FileMutationOptions options = null!)
        {
            throw new NotSupportedException();
        }

        public void CopyFile(string sourcePath, string destinationPath, bool overwrite, FileMutationOptions options = null!)
        {
            throw new NotSupportedException();
        }

        public void CopyDirectory(string sourcePath, string destinationPath, bool overwrite, FileMutationOptions options = null!)
        {
            throw new NotSupportedException();
        }

        public void DeleteFileDirect(string filePath, FileMutationOptions options = null!)
        {
            if (File.Exists(filePath))
            {
                File.Delete(filePath);
            }
        }

        public void DeleteFileShell(string filePath, Microsoft.VisualBasic.FileIO.UIOption uiOption, Microsoft.VisualBasic.FileIO.RecycleOption recycleOption, FileMutationOptions options = null!)
        {
            DeleteFileDirect(filePath, options);
        }

        public void DeleteDirectoryDirect(string directoryPath, bool recursive, FileMutationOptions options = null!)
        {
            if (Directory.Exists(directoryPath))
            {
                Directory.Delete(directoryPath, recursive);
            }
        }

        public void DeleteDirectoryShell(string directoryPath, Microsoft.VisualBasic.FileIO.UIOption uiOption, Microsoft.VisualBasic.FileIO.RecycleOption recycleOption, FileMutationOptions options = null!)
        {
            DeleteDirectoryDirect(directoryPath, recursive: true, options);
        }

        public void SetTimestamps(string path, bool isDirectory, DateTime? creationTime, DateTime? lastWriteTime, FileMutationOptions options = null!)
        {
        }
    }

    internal sealed class RecordingFileMutationService : IFileMutationService
    {
        public List<TimestampCall> TimestampCalls { get; } = [];

        public Action<TimestampCall>? OnSetTimestamps { get; set; }

        public void EnsureDirectory(string directoryPath, FileMutationOptions options = null!)
        {
            if (!string.IsNullOrWhiteSpace(directoryPath))
            {
                Directory.CreateDirectory(directoryPath);
            }
        }

        public void MoveFile(string sourcePath, string destinationPath, bool overwrite, FileMutationOptions options = null!)
        {
            throw new NotSupportedException();
        }

        public void MoveDirectory(string sourcePath, string destinationPath, bool overwrite, FileMutationOptions options = null!)
        {
            throw new NotSupportedException();
        }

        public void CopyFile(string sourcePath, string destinationPath, bool overwrite, FileMutationOptions options = null!)
        {
            throw new NotSupportedException();
        }

        public void CopyDirectory(string sourcePath, string destinationPath, bool overwrite, FileMutationOptions options = null!)
        {
            throw new NotSupportedException();
        }

        public void DeleteFileDirect(string filePath, FileMutationOptions options = null!)
        {
            throw new NotSupportedException();
        }

        public void DeleteFileShell(string filePath, Microsoft.VisualBasic.FileIO.UIOption uiOption, Microsoft.VisualBasic.FileIO.RecycleOption recycleOption, FileMutationOptions options = null!)
        {
            throw new NotSupportedException();
        }

        public void DeleteDirectoryDirect(string directoryPath, bool recursive, FileMutationOptions options = null!)
        {
            throw new NotSupportedException();
        }

        public void DeleteDirectoryShell(string directoryPath, Microsoft.VisualBasic.FileIO.UIOption uiOption, Microsoft.VisualBasic.FileIO.RecycleOption recycleOption, FileMutationOptions options = null!)
        {
            throw new NotSupportedException();
        }

        public void SetTimestamps(string path, bool isDirectory, DateTime? creationTime, DateTime? lastWriteTime, FileMutationOptions options = null!)
        {
            var timestampCall = new TimestampCall
            {
                Path = path,
                IsDirectory = isDirectory,
                CreationTime = creationTime,
                LastWriteTime = lastWriteTime
            };
            TimestampCalls.Add(timestampCall);
            OnSetTimestamps?.Invoke(timestampCall);
        }
    }

    internal sealed class TimestampCall
    {
        public string Path { get; set; } = string.Empty;

        public bool IsDirectory { get; set; }

        public DateTime? CreationTime { get; set; }

        public DateTime? LastWriteTime { get; set; }
    }

    internal sealed class RecordingDialogService : IBmsLibraryDialogService
    {
        public List<DialogCall> Calls { get; } = [];

        public MessageBoxResult ResultToReturn { get; set; } = MessageBoxResult.OK;

        public Action<DialogCall>? OnShow { get; set; }

        public MessageBoxResult Show(string messageBoxText, string caption, MessageBoxButton button, MessageBoxImage icon, MessageBoxResult defaultResult = MessageBoxResult.None)
        {
            var dialogCall = new DialogCall
            {
                Message = messageBoxText,
                Caption = caption,
                Button = button,
                Icon = icon,
                DefaultResult = defaultResult
            };
            Calls.Add(dialogCall);
            OnShow?.Invoke(dialogCall);
            return ResultToReturn;
        }
    }

    internal sealed class DialogCall
    {
        public string Message { get; set; } = string.Empty;

        public string Caption { get; set; } = string.Empty;

        public MessageBoxButton Button { get; set; }

        public MessageBoxImage Icon { get; set; }

        public MessageBoxResult DefaultResult { get; set; }
    }
}

/// <summary>
/// Supplies one captured chart-scan result to a production-shaped library
/// route without consulting the local Everything service or filesystem at
/// scan time.
/// </summary>
internal sealed class CapturedChartFileScanner : IChartFileScanner
{
    private readonly ChartScanExecutionResult capturedResult;

    private CapturedChartFileScanner(ChartScanExecutionResult capturedResult)
    {
        this.capturedResult = capturedResult ?? throw new ArgumentNullException(nameof(capturedResult));
    }

    /// <summary>
    /// Captures the supplied fixture surface before the production ingress is
    /// invoked, including any directory metadata present under the roots.
    /// </summary>
    /// <param name="chartPaths">Stable chart paths to publish from the fixture.</param>
    /// <param name="resourcesByDirectory">Stable resource paths grouped by chart directory.</param>
    /// <param name="directorySurfaceRoots">Directories whose metadata is captured for the scan result.</param>
    internal static CapturedChartFileScanner FromFixture(
        IEnumerable<string> chartPaths,
        IDictionary<string, IEnumerable<string>> resourcesByDirectory,
        IEnumerable<string>? directorySurfaceRoots = null)
    {
        return new CapturedChartFileScanner(new ChartScanExecutionResult
        {
            ScanSource = ChartScanSource.Everything,
            Success = true,
            IsComplete = true,
            Result = BmsLibraryInitializationTestSupport.CreateScanResult(
                chartPaths,
                resourcesByDirectory,
                directorySurfaceRoots)
        });
    }

    /// <inheritdoc />
    public ChartScanExecutionResult Scan(
        IEnumerable<string> rootDirectories,
        IEnumerable<string> chartExtensions,
        bool verboseLog = false,
        bool includeTextSurface = true,
        bool includeDirectorySurface = false)
    {
        return capturedResult;
    }
}
