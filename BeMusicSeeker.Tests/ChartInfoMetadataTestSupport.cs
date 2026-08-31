using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text;
using System.Threading.Tasks;
using System.Windows;
using BeMusicSeeker.Models;
using BeMusicSeeker.Models.BmsLibraryInternal;
using MessageBoxButton = BeMusicSeeker.Models.UiDialogButton;
using MessageBoxImage = BeMusicSeeker.Models.UiDialogIcon;
using MessageBoxResult = BeMusicSeeker.Models.UiDialogDefaultResult;
using BeMusicSeeker.Models.LR2;
using BeMusicSeeker.Properties;
using BeMusicSeeker.ViewModels;
using ChartInfoExportTool;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using SQLite;

namespace BeMusicSeeker.Tests;

internal static class ChartInfoMetadataTestSupport
{
    internal const int FeatureMine = 2;

    internal const int FeatureRandom = 4;

    internal const int FeatureLongByLnMode = 8;

    internal const int FeatureChargeNote = 16;

    internal const int FeatureStop = 64;

    internal const int FeatureScroll = 128;

    internal static readonly HashSet<string> ProductionDiffKnownTimeoutSha256s = new(StringComparer.OrdinalIgnoreCase)
    {
        "273433f8e72e768603d18c986b50900538e94c2bd3e50d4487b66d5c0c3c8201",
        "a56958ab747ebb7fd4332afed2493f75a414c00be694e61182cbd3a363871a43",
        "ae3d8c2c5eb88da961df62a6e7fa6ca043b528f1a36de67643eb463b18864d6f",
        "bd496f28d4a61aba6e9315f61fda463209cd908f3b08f0c2e7e06150034e2e59"
    };



    internal static long CountChartInfoRows(string songDbPath, string sha256)
    {
        using var songDb = new LR2SongDBExtended(songDbPath);
        return songDb.ExecuteScalar<long>("SELECT COUNT(1) FROM chart_info WHERE sha256 = '" + sha256 + "';");
    }

    internal static void AssertJavaDoubleParseBits(string value, string expectedHexBits)
    {
        Assert.AreEqual(expectedHexBits, JavaDoubleParserJdk17.ParseDoubleBits(value).ToString("x16", CultureInfo.InvariantCulture), value);
    }

    internal static void AssertChartInfoEquivalent(LR2SongDBExtended.chart_info expected, LR2SongDBExtended.chart_info actual)
    {
        Assert.AreEqual(expected.sha256, actual.sha256);
        Assert.AreEqual(expected.md5, actual.md5);
        Assert.AreEqual(expected.charthash, actual.charthash);
        Assert.AreEqual(expected.level, actual.level);
        Assert.AreEqual(expected.difficulty, actual.difficulty);
        Assert.AreEqual(expected.difficulty_defined, actual.difficulty_defined);
        Assert.AreEqual(expected.mainbpm, actual.mainbpm);
        Assert.AreEqual(expected.maxbpm, actual.maxbpm);
        Assert.AreEqual(expected.minbpm, actual.minbpm);
        Assert.AreEqual(expected.length, actual.length);
        Assert.AreEqual(expected.mode, actual.mode);
        Assert.AreEqual(expected.judge, actual.judge);
        Assert.AreEqual(expected.bga, actual.bga);
        Assert.AreEqual(expected.exlevel, actual.exlevel);
        Assert.AreEqual(expected.feature, actual.feature);
        Assert.AreEqual(expected.notes, actual.notes);
        Assert.AreEqual(expected.n, actual.n);
        Assert.AreEqual(expected.ln, actual.ln);
        Assert.AreEqual(expected.s, actual.s);
        Assert.AreEqual(expected.ls, actual.ls);
        Assert.AreEqual(expected.total, actual.total);
        Assert.AreEqual(expected.total_defined, actual.total_defined);
        Assert.AreEqual(expected.density, actual.density);
        Assert.AreEqual(expected.peakdensity, actual.peakdensity);
        Assert.AreEqual(expected.enddensity, actual.enddensity);
        Assert.AreEqual(expected.distribution, actual.distribution);
        Assert.AreEqual(expected.speedchange, actual.speedchange);
        Assert.AreEqual(expected.speedchange_count, actual.speedchange_count);
        Assert.AreEqual(expected.lanenotes, actual.lanenotes);
        Assert.AreEqual(expected.parser_version, actual.parser_version);
    }

    internal static void AssertChartInfoDisplayProjectionEquivalent(LR2SongDBExtended.chart_info expected, LR2SongDBExtended.chart_info actual)
    {
        Assert.AreEqual(expected.sha256, actual.sha256);
        Assert.AreEqual(expected.md5, actual.md5);
        Assert.AreEqual(expected.level, actual.level);
        Assert.AreEqual(expected.difficulty, actual.difficulty);
        Assert.AreEqual(expected.difficulty_defined, actual.difficulty_defined);
        Assert.AreEqual(expected.mainbpm, actual.mainbpm);
        Assert.AreEqual(expected.maxbpm, actual.maxbpm);
        Assert.AreEqual(expected.minbpm, actual.minbpm);
        Assert.AreEqual(expected.length, actual.length);
        Assert.AreEqual(expected.mode, actual.mode);
        Assert.AreEqual(expected.judge, actual.judge);
        Assert.AreEqual(expected.bga, actual.bga);
        Assert.AreEqual(expected.exlevel, actual.exlevel);
        Assert.AreEqual(expected.feature, actual.feature);
        Assert.AreEqual(expected.notes, actual.notes);
        Assert.AreEqual(expected.n, actual.n);
        Assert.AreEqual(expected.ln, actual.ln);
        Assert.AreEqual(expected.s, actual.s);
        Assert.AreEqual(expected.ls, actual.ls);
        Assert.AreEqual(expected.total, actual.total);
        Assert.AreEqual(expected.total_defined, actual.total_defined);
        Assert.AreEqual(expected.density, actual.density);
        Assert.AreEqual(expected.peakdensity, actual.peakdensity);
        Assert.AreEqual(expected.enddensity, actual.enddensity);
        Assert.AreEqual(expected.speedchange_count, actual.speedchange_count);
        Assert.AreEqual(expected.parser_version, actual.parser_version);
        Assert.IsNull(actual.charthash);
        Assert.IsNull(actual.distribution);
        Assert.IsNull(actual.speedchange);
        Assert.IsNull(actual.lanenotes);
    }

    internal static void AssertNullableDouble(double? expected, double? actual, string message)
    {
        if (!expected.HasValue || !actual.HasValue)
        {
            Assert.AreEqual(expected.HasValue, actual.HasValue, message);
            return;
        }
        Assert.IsTrue(Math.Abs(expected.Value - actual.Value) <= 0.000001, message + " expected=" + expected.Value.ToString("R", CultureInfo.InvariantCulture) + " actual=" + actual.Value.ToString("R", CultureInfo.InvariantCulture));
    }

    internal static string ResolveInstalledSevenZipPathOrInconclusive()
    {
        string[] candidates =
        [
            @"C:\Program Files\7-Zip\7z.exe",
            @"C:\Program Files (x86)\7-Zip\7z.exe"
        ];
        foreach (string candidate in candidates)
        {
            if (File.Exists(candidate))
            {
                return candidate;
            }
        }

        Assert.Inconclusive("7-Zip CLI is not installed.");
        return null;
    }

    internal static string RunSevenZip(string sevenZipPath, string arguments)
    {
        var startInfo = new ProcessStartInfo
        {
            FileName = sevenZipPath,
            Arguments = arguments,
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true
        };
        using var process = Process.Start(startInfo);
        Assert.IsNotNull(process, "Failed to start 7z.exe.");
        string stdout = process.StandardOutput.ReadToEnd();
        string stderr = process.StandardError.ReadToEnd();
        Assert.IsTrue(process.WaitForExit(30000), "7z.exe timed out." + Environment.NewLine + stdout + Environment.NewLine + stderr);
        Assert.AreEqual(0, process.ExitCode, stdout + Environment.NewLine + stderr);
        return stdout + Environment.NewLine + stderr;
    }

    internal static string QuoteProcessArgument(string value)
    {
        return "\"" + (value ?? string.Empty).Replace("\"", "\\\"") + "\"";
    }

    internal static LR2SongDBExtended.chart_info CreateChartInfoRow(string sha256, string md5, int parserVersion)
    {
        return new LR2SongDBExtended.chart_info
        {
            sha256 = sha256,
            md5 = md5,
            charthash = new string('c', 64),
            level = 1,
            difficulty = 1,
            difficulty_defined = true,
            mainbpm = 120.0,
            maxbpm = 120.0,
            minbpm = 120.0,
            length = 0,
            mode = 7,
            judge = 100,
            bga = 0,
            exlevel = 0,
            feature = FeatureLongByLnMode,
            notes = 1,
            n = 1,
            ln = 0,
            s = 0,
            ls = 0,
            total = 260.0,
            total_defined = false,
            density = 1.0,
            peakdensity = 1.0,
            enddensity = 0.0,
            distribution = "#",
            speedchange = "120.0,0.0",
            speedchange_count = 0,
            lanenotes = "1,0,0",
            parser_version = parserVersion,
            updated_at = DateTime.UtcNow
        };
    }

    internal static LR2SongDBExtended.chart_digest_map CreateChartDigestRow(string md5, string sha256)
    {
        return new LR2SongDBExtended.chart_digest_map
        {
            md5 = md5,
            sha256 = sha256
        };
    }

    internal static void InsertSongForSummary(LR2SongDBExtended songDb, string path, string md5)
    {
        var file = new TestableBmsFile
        {
            path = path
        };
        file.SetHash(md5);
        songDb.InsertOrReplace(file, typeof(LR2SongDB.song));
    }

    internal static LR2SongDBExtended.chart_info_parse_failure CreateChartInfoParseFailureRow(string md5, string sha256, string path, int parserVersion, string failureKind, string exceptionType, string message, int? parseTimeoutMs)
    {
        return new LR2SongDBExtended.chart_info_parse_failure
        {
            md5 = md5,
            sha256 = sha256,
            path = path,
            parser_version = parserVersion,
            failure_kind = failureKind,
            exception_type = exceptionType,
            message = message,
            parse_timeout_ms = parseTimeoutMs,
            updated_at = DateTime.UtcNow
        };
    }

    internal static RealChartInfoExpectedRow CreateExpectedCompatibilityRow(string sha256, string charthash)
    {
        return new RealChartInfoExpectedRow
        {
            fixture_id = 1,
            fixture_path = "synthetic.bms",
            sha256 = sha256,
            md5 = new string('b', 32),
            charthash = charthash,
            level = 1,
            difficulty = 1,
            mainbpm = 120.0,
            maxbpm = 120.0,
            minbpm = 120.0,
            length = 0,
            mode = 7,
            judge = 100,
            feature = FeatureLongByLnMode,
            notes = 1,
            n = 1,
            ln = 0,
            s = 0,
            ls = 0,
            total = 260.0,
            density = 1.0,
            peakdensity = 1.0,
            enddensity = 0.0,
            distribution = "#",
            speedchange = "120.0,0.0",
            speedchange_count = 0,
            lanenotes = "1,0,0"
        };
    }

    internal static string CreateBmsonLongNoteWithContinuation(int continuationY)
    {
        return "{"
            + "\"version\":\"1.0.0\","
            + "\"info\":{\"level\":1,\"mode_hint\":\"beat-7k\",\"init_bpm\":120,\"judge_rank\":100,\"total\":100,\"resolution\":240,\"ln_type\":2},"
            + "\"sound_channels\":[{\"notes\":[{\"x\":1,\"y\":0,\"l\":480},{\"x\":0,\"y\":" + continuationY.ToString(System.Globalization.CultureInfo.InvariantCulture) + ",\"c\":true}]}]"
            + "}";
    }

    internal static void WithTemporarySongDb(Action<string, string> testAction)
    {
        string tempRootPath = Path.Combine(Path.GetTempPath(), "BeMusicSeeker_ChartInfoTests_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempRootPath);
        string songDbPath = Path.Combine(tempRootPath, "song.db");
        File.WriteAllBytes(songDbPath, []);
        try
        {
            testAction(tempRootPath, songDbPath);
        }
        finally
        {
            if (Directory.Exists(tempRootPath))
            {
                Directory.Delete(tempRootPath, recursive: true);
            }
        }
    }

    internal static async Task WithTemporarySongDb(Func<string, string, Task> testAction)
    {
        string tempRootPath = Path.Combine(Path.GetTempPath(), "BeMusicSeeker_ChartInfoTests_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempRootPath);
        string songDbPath = Path.Combine(tempRootPath, "song.db");
        File.WriteAllBytes(songDbPath, []);
        try
        {
            await testAction(tempRootPath, songDbPath);
        }
        finally
        {
            if (Directory.Exists(tempRootPath))
            {
                Directory.Delete(tempRootPath, recursive: true);
            }
        }
    }

    internal static string CreateChartInfoMetadataBundle(
        string tempRootPath,
        IEnumerable<LR2SongDBExtended.chart_info> chartInfos,
        IEnumerable<LR2SongDBExtended.chart_digest_map>? chartDigests = null)
    {
        string sourceDirectoryPath = Path.Combine(tempRootPath, Guid.NewGuid().ToString("N") + "-source");
        Directory.CreateDirectory(sourceDirectoryPath);
        string sourcePath = Path.Combine(sourceDirectoryPath, "song.db");
        File.WriteAllBytes(sourcePath, []);
        using (var sourceDb = new LR2SongDBExtended(sourcePath))
        {
            BmsLibraryDbGateway.EnsureBmsonSchema(sourceDb);
            BmsLibraryDbGateway.EnsureChartInfoSchema(sourceDb);
            foreach (LR2SongDBExtended.chart_info row in chartInfos ?? [])
            {
                sourceDb.InsertOrReplace(row, typeof(LR2SongDBExtended.chart_info));
            }
            foreach (LR2SongDBExtended.chart_digest_map row in chartDigests ?? [])
            {
                sourceDb.InsertOrReplace(row, typeof(LR2SongDBExtended.chart_digest_map));
            }
        }

        string bundlePath = Path.Combine(tempRootPath, Guid.NewGuid().ToString("N") + "-chart-info-metadata.db");
        ChartInfoExportRunner.Export(new ChartInfoExportOptions
        {
            SourceSongDbPath = sourcePath,
            OutputDbPath = bundlePath
        });
        return bundlePath;
    }

    internal static void InvokeInstallChartPackages(BMSLibrary library, IEnumerable<ChartPackage> packages, string installDirectory)
    {
        List<ChartPackage> packageList = [.. (packages ?? [])];
        foreach (PackageChartEntry entry in packageList
            .SelectMany(package => package?.ChartEntries ?? [])
            .Where(entry => entry?.Chart != null))
        {
            entry.SetInstallDestinationPathOnly(installDirectory);
        }

        library.ChartPackagesPending = new ObservableCollection<ChartPackage>(packageList);
        library.InstallPendingPackagesToEstimatedDestinations(packageList);
    }

    internal static Task AwaitChartInfoBackfillAsync(BMSLibrary library)
    {
        return AwaitChartInfoStateAsync(
            library,
            current => current.ChartInfoBackfillRequestedVersion > 0
                && current.ChartInfoBackfillCompletedVersion == current.ChartInfoBackfillRequestedVersion
                && !current.ChartInfoBackfillRunning,
            nameof(BMSLibrary.ChartInfoBackfillRequestedVersion),
            nameof(BMSLibrary.ChartInfoBackfillCompletedVersion),
            nameof(BMSLibrary.ChartInfoBackfillRunning));
    }

    internal static void InvokeDeferredChartInfoHydration(BMSLibrary library, string reason, bool queueFullBackfillAfterHydration)
    {
        MethodInfo method = typeof(BMSLibrary).GetMethod("QueueDeferredChartInfoHydration", BindingFlags.Instance | BindingFlags.NonPublic);
        Assert.IsNotNull(method, "QueueDeferredChartInfoHydration method was not found.");
        method.Invoke(library, [reason, queueFullBackfillAfterHydration]);
    }

    internal static Task AwaitChartInfoHydrationAsync(BMSLibrary library)
    {
        return AwaitChartInfoStateAsync(
            library,
            current => current.ChartInfoHydrationRequestedVersion > 0
                && current.ChartInfoHydrationCompletedVersion == current.ChartInfoHydrationRequestedVersion
                && !current.ChartInfoHydrationRunning,
            nameof(BMSLibrary.ChartInfoHydrationRequestedVersion),
            nameof(BMSLibrary.ChartInfoHydrationCompletedVersion),
            nameof(BMSLibrary.ChartInfoHydrationRunning));
    }

    private static async Task AwaitChartInfoStateAsync(
        BMSLibrary library,
        Func<BMSLibrary, bool> isComplete,
        params string[] observedPropertyNames)
    {
        var completion = new TaskCompletionSource<object?>(TaskCreationOptions.RunContinuationsAsynchronously);

        void CompleteIfReady()
        {
            try
            {
                if (isComplete(library))
                {
                    completion.TrySetResult(null);
                }
            }
            catch (Exception exception)
            {
                completion.TrySetException(exception);
            }
        }

        PropertyChangedEventHandler propertyChangedHandler = delegate (object sender, PropertyChangedEventArgs args)
        {
            if (args.PropertyName is null || Array.IndexOf(observedPropertyNames, args.PropertyName) >= 0)
            {
                CompleteIfReady();
            }
        };

        library.PropertyChanged += propertyChangedHandler;
        try
        {
            CompleteIfReady();
            await completion.Task;
        }
        finally
        {
            library.PropertyChanged -= propertyChangedHandler;
        }
    }

    internal static int ReadPositiveIntEnvironmentVariable(string name, int defaultValue)
    {
        string value = Environment.GetEnvironmentVariable(name);
        if (int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out int parsed) && parsed > 0)
        {
            return parsed;
        }
        return defaultValue;
    }

    internal static string FindRepoRoot()
    {
        var directory = new DirectoryInfo(AppDomain.CurrentDomain.BaseDirectory);
        while (directory != null)
        {
            if (File.Exists(Path.Combine(directory.FullName, "BeMusicSeeker.sln")))
            {
                return directory.FullName;
            }
            directory = directory.Parent;
        }
        throw new DirectoryNotFoundException("Could not find repository root.");
    }

    internal static ChartInfoBackfillResult BackfillChartInfos(
        ChartInfoBuildService service,
        BmsLibraryDbGateway gateway,
        IEnumerable<BMSFile>? currentFiles,
        IEnumerable<LR2SongDBExtended.bmson_song>? currentBmsonSongs,
        Action<int, int, string>? reportProgress = null,
        Action<string>? logInstallPerformance = null,
        Action<string>? logInstallPerformanceWarn = null,
        Action<ChartInfoStorageCommitPublication>? storageCommitPublished = null,
        IReadOnlyDictionary<string, LR2SongDBExtended.chart_info>? existingRowsSnapshot = null)
    {
        return service.BackfillChartInfos(
            gateway,
            CreateChartSnapshot(currentFiles, currentBmsonSongs),
            reportProgress,
            logInstallPerformance,
            logInstallPerformanceWarn,
            storageCommitPublished,
            existingRowsSnapshot,
            request => ApplyChartInfoStorageRequest(gateway, request));
    }

    internal static CatalogChartInfoStorageWriteReceipt ApplyChartInfoStorageRequest(
        BmsLibraryDbGateway gateway,
        CatalogChartInfoStorageWriteRequest request,
        Action<LR2SongDBExtended>? afterWrites = null)
    {
        if (request == null || !request.HasChanges)
        {
            return CatalogChartInfoStorageWriteReceipt.NotApplied;
        }
        Lr2ChartInfoSongProjectionWriteResult songProjectionResult =
            Lr2ChartInfoSongProjectionWriteResult.Empty;
        gateway.ExecuteSongDbTransaction(songDb =>
        {
            if (request.BmsRows.Count > 0)
            {
                BmsLibraryDbGateway.EnsureBmsonSchema(songDb);
                BmsLibraryDbGateway.EnsureSongLookupIndexes(songDb);
                Lr2SongDbWriter.UpsertGeneratedSongs(songDb, request.BmsRows);
            }
            if (request.ChartInfoSongProjections.Count > 0)
            {
                songProjectionResult = Lr2SongDbWriter.UpdateChartInfoSongProjections(
                    songDb,
                    request.ChartInfoSongProjections);
            }
            if (request.BmsonRows.Count > 0)
            {
                BmsLibraryDbGateway.EnsureBmsonSchema(songDb);
                foreach (LR2SongDBExtended.bmson_song row in request.BmsonRows)
                {
                    songDb.InsertOrReplace(row, typeof(LR2SongDBExtended.bmson_song));
                }
            }
            BmsLibraryDbGateway.UpsertChartInfoBackfillChunk(
                songDb,
                request.ChartInfo.DigestEntries,
                request.ChartInfo.ChartInfoRows,
                request.ChartInfo.ParseFailureRows,
                request.ChartInfo.ParseFailureDeleteMd5s);
            afterWrites?.Invoke(songDb);
        });
        return new CatalogChartInfoStorageWriteReceipt(
            applied: true,
            request.BmsRows.Count,
            request.BmsonRows.Count,
            new CatalogChartInfoWriteReceipt(
                applied: request.ChartInfo.HasChanges,
                request.ChartInfo.DigestEntries.Count,
                request.ChartInfo.ChartInfoRows.Count,
                request.ChartInfo.ParseFailureRows.Count,
                request.ChartInfo.ParseFailureDeleteMd5s.Count),
            songProjectionResult);
    }

    internal static List<ChartFile> CreateChartSnapshot(
        IEnumerable<BMSFile>? currentFiles,
        IEnumerable<LR2SongDBExtended.bmson_song>? currentBmsonSongs)
    {
        List<ChartFile> charts = [];
        charts.AddRange(ChartFileProjection.FromBmsFiles(currentFiles, includeWarningSnapshot: false));
        charts.AddRange(ChartFileProjection.FromBmsonSongs(currentBmsonSongs, includeWarningSnapshot: false));
        return charts;
    }

    internal sealed class ColumnNameRow
    {
        public string name { get; set; } = string.Empty;
    }

    internal sealed class ChartInfoMetadataBundleManifestRow
    {
        public string bundle_id { get; set; } = string.Empty;

        public int format_version { get; set; }

        public string generated_at { get; set; } = string.Empty;

        public int chart_info_schema_version { get; set; }

        public int chart_info_parser_version { get; set; }

        public int chart_info_count { get; set; }

        public int chart_digest_count { get; set; }
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
    }

    internal sealed class RecordingDialogService : IBmsLibraryDialogService
    {
        public MessageBoxResult Show(string messageBoxText, string caption, MessageBoxButton button, MessageBoxImage icon, MessageBoxResult defaultResult = MessageBoxResult.None)
        {
            return defaultResult == MessageBoxResult.None ? MessageBoxResult.OK : defaultResult;
        }
    }

    internal sealed class RealChartInfoExpectedRow
    {
        public int fixture_id { get; set; }

        public string fixture_path { get; set; } = string.Empty;

        public string sha256 { get; set; } = string.Empty;

        public string md5 { get; set; } = string.Empty;

        public string charthash { get; set; } = string.Empty;

        public int? level { get; set; }

        public int? difficulty { get; set; }

        public double? mainbpm { get; set; }

        public double? maxbpm { get; set; }

        public double? minbpm { get; set; }

        public int? length { get; set; }

        public int mode { get; set; }

        public int judge { get; set; }

        public int feature { get; set; }

        public int notes { get; set; }

        public int n { get; set; }

        public int ln { get; set; }

        public int s { get; set; }

        public int ls { get; set; }

        public double? total { get; set; }

        public double? density { get; set; }

        public double? peakdensity { get; set; }

        public double? enddensity { get; set; }

        public string distribution { get; set; } = string.Empty;

        public string speedchange { get; set; } = string.Empty;

        public int speedchange_count { get; set; }

        public string lanenotes { get; set; } = string.Empty;
    }

    internal sealed class EdgeCaseSampleChartRow
    {
        public int fixture_id { get; set; }

        public string source_path { get; set; } = string.Empty;

        public string fixture_path { get; set; } = string.Empty;

        public string reason { get; set; } = string.Empty;

        public string md5 { get; set; } = string.Empty;

        public string sha256 { get; set; } = string.Empty;

        public int has_beatoraja_song { get; set; }
    }

    internal sealed class BmsonCompatibilityDiffCounts
    {
        internal readonly List<string> samples = [];

        public int CoreDiffs { get; internal set; }

        public int ChartHashDiffs { get; internal set; }

        public int LengthDiffs { get; internal set; }

        public int DistributionDiffs { get; internal set; }

        public int BpmIntegerDiffs { get; internal set; }

        public int TotalDiffs { get; internal set; }

        public int DensityDiffs { get; internal set; }

        public int PeakDensityDiffs { get; internal set; }

        public int EndDensityDiffs { get; internal set; }

        public int MainBpmDiffs { get; internal set; }

        public void Add(RealChartInfoExpectedRow expected, LR2SongDBExtended.chart_info actual, string chartString)
        {
            CoreDiffs += CountCoreDiffs(expected, actual);
            if (!string.Equals(expected.charthash, actual.charthash, StringComparison.OrdinalIgnoreCase))
            {
                ChartHashDiffs++;
                AddSample("charthash", expected, expected.charthash, actual.charthash, chartString);
            }
            if (expected.length != actual.length)
            {
                LengthDiffs++;
                AddSample("length", expected, expected.length.GetValueOrDefault().ToString(CultureInfo.InvariantCulture), actual.length.GetValueOrDefault().ToString(CultureInfo.InvariantCulture), chartString);
            }
            if (!string.Equals(expected.distribution, actual.distribution, StringComparison.Ordinal))
            {
                DistributionDiffs++;
            }
            if ((int)(expected.maxbpm ?? 0.0) != (int)(actual.maxbpm ?? 0.0)
                || (int)(expected.minbpm ?? 0.0) != (int)(actual.minbpm ?? 0.0))
            {
                BpmIntegerDiffs++;
                AddSample(
                    "bpm",
                    expected,
                    (expected.minbpm ?? 0.0).ToString("R") + "/" + (expected.maxbpm ?? 0.0).ToString("R"),
                    (actual.minbpm ?? 0.0).ToString("R") + "/" + (actual.maxbpm ?? 0.0).ToString("R"),
                    chartString);
            }
            if (!NullableDoubleEquals(expected.total, actual.total))
            {
                TotalDiffs++;
            }
            if (!NullableDoubleEquals(expected.density, actual.density))
            {
                DensityDiffs++;
            }
            if (!NullableDoubleEquals(expected.peakdensity, actual.peakdensity))
            {
                PeakDensityDiffs++;
            }
            if (!NullableDoubleEquals(expected.enddensity, actual.enddensity))
            {
                EndDensityDiffs++;
            }
            if (!NullableDoubleEquals(expected.mainbpm, actual.mainbpm))
            {
                MainBpmDiffs++;
                AddSample(
                    "mainbpm",
                    expected,
                    (expected.mainbpm ?? 0.0).ToString("R", CultureInfo.InvariantCulture),
                    (actual.mainbpm ?? 0.0).ToString("R", CultureInfo.InvariantCulture),
                    chartString);
            }
        }

        public override string ToString()
        {
            return "chart_info bmson compatibility diffs: "
                + "core=" + CoreDiffs
                + " charthash=" + ChartHashDiffs
                + " length=" + LengthDiffs
                + " distribution=" + DistributionDiffs
                + " bpmInteger=" + BpmIntegerDiffs
                + " total=" + TotalDiffs
                + " density=" + DensityDiffs
                + " peakdensity=" + PeakDensityDiffs
                + " enddensity=" + EndDensityDiffs
                + " mainbpm=" + MainBpmDiffs
                + (samples.Count == 0 ? string.Empty : " samples=" + string.Join(" | ", samples));
        }

        internal void AddSample(string field, RealChartInfoExpectedRow expected, string expectedValue, string actualValue, string chartString)
        {
            if (samples.Count >= 25 || samples.Count(item => item.StartsWith(field + " ", StringComparison.Ordinal)) >= 5)
            {
                return;
            }
            string artifactPath = WriteChartStringArtifact(expected, chartString);
            samples.Add(
                field
                    + " fixture_id=" + expected.fixture_id
                    + " path=" + expected.fixture_path
                    + " sha256=" + expected.sha256
                    + " expected=" + expectedValue
                    + " actual=" + actualValue
                    + " chartString=" + artifactPath);
        }

        internal static string WriteChartStringArtifact(RealChartInfoExpectedRow expected, string chartString)
        {
            string directory = Path.Combine(Path.GetTempPath(), "BeMusicSeeker_ChartInfoBmsonDiffs");
            Directory.CreateDirectory(directory);
            string artifactPath = Path.Combine(directory, expected.sha256 + ".csharp.chart.txt");
            File.WriteAllText(artifactPath, chartString ?? string.Empty, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
            return artifactPath;
        }

        internal static int CountCoreDiffs(RealChartInfoExpectedRow expected, LR2SongDBExtended.chart_info actual)
        {
            int count = 0;
            count += string.Equals(expected.sha256, actual.sha256, StringComparison.OrdinalIgnoreCase) ? 0 : 1;
            count += string.Equals(expected.md5, actual.md5, StringComparison.OrdinalIgnoreCase) ? 0 : 1;
            count += LevelEqualsBeatoraja(expected.level, actual.level) ? 0 : 1;
            count += expected.difficulty == actual.difficulty ? 0 : 1;
            count += expected.mode == actual.mode ? 0 : 1;
            count += expected.judge == actual.judge ? 0 : 1;
            count += expected.feature == actual.feature ? 0 : 1;
            count += expected.notes == actual.notes ? 0 : 1;
            count += expected.n == actual.n ? 0 : 1;
            count += expected.ln == actual.ln ? 0 : 1;
            count += expected.s == actual.s ? 0 : 1;
            count += expected.ls == actual.ls ? 0 : 1;
            count += string.Equals(expected.lanenotes, actual.lanenotes, StringComparison.Ordinal) ? 0 : 1;
            return count;
        }

        internal static bool NullableDoubleEquals(double? expected, double? actual)
        {
            if (!expected.HasValue || !actual.HasValue)
            {
                return expected.HasValue == actual.HasValue;
            }
            return Math.Abs(expected.Value - actual.Value) <= 0.000001;
        }

        internal static bool LevelEqualsBeatoraja(int? expected, int? actual)
        {
            if (expected == actual)
            {
                return true;
            }
            return expected.GetValueOrDefault() == 0 && !actual.HasValue;
        }
    }

    internal sealed class CompatibilityDiffCounts
    {
        internal readonly List<string> samples = [];

        internal readonly List<FieldDiffRecord> fieldDiffRecords = [];

        internal readonly Dictionary<string, int> fieldDiffCounts = new(StringComparer.Ordinal);

        internal readonly List<long> parseElapsedMilliseconds = [];

        internal readonly List<ParseRecord> parseRecords = [];

        internal readonly List<string> skippedSamples = [];

        public int ParsedCount { get; internal set; }

        public int TimeoutCount { get; internal set; }

        public int ParseFailureCount { get; internal set; }

        public long TotalElapsedMs => parseElapsedMilliseconds.Sum();

        public double AvgParseMs => parseElapsedMilliseconds.Count == 0 ? 0.0 : parseElapsedMilliseconds.Average();

        public long MaxParseMs => parseElapsedMilliseconds.Count == 0 ? 0L : parseElapsedMilliseconds.Max();

        public long ParseP95Ms
        {
            get
            {
                if (parseElapsedMilliseconds.Count == 0)
                {
                    return 0L;
                }
                long[] sorted = [.. parseElapsedMilliseconds.OrderBy(value => value)];
                int index = Math.Max(0, (int)Math.Ceiling(sorted.Length * 0.95) - 1);
                return sorted[index];
            }
        }

        public int CoreDiffs { get; internal set; }

        public int ChartHashDiffs { get; internal set; }

        public int LengthDiffs { get; internal set; }

        public int DensityDiffs { get; internal set; }

        public int PeakDensityDiffs { get; internal set; }

        public int EndDensityDiffs { get; internal set; }

        public int DistributionDiffs { get; internal set; }

        public int SpeedChangeDiffs { get; internal set; }

        public int BpmIntegerDiffs { get; internal set; }

        public int TotalDiffs => fieldDiffRecords.Count;

        public static string GetReportDirectory()
        {
            string configured = Environment.GetEnvironmentVariable("BMS_TEST_PRODUCTION_DIFF_REPORT_DIR");
            if (!string.IsNullOrWhiteSpace(configured))
            {
                return configured;
            }
            return Path.Combine(Path.GetTempPath(), "BeMusicSeeker_ChartInfoProductionDiff");
        }

        public void AddParsed(RealChartInfoExpectedRow expected, LR2SongDBExtended.chart_info actual, string chartString, long elapsedMilliseconds)
        {
            ParsedCount++;
            RecordParse(expected, elapsedMilliseconds, "success");
            Add(expected, actual, chartString);
        }

        public void AddTimeout(RealChartInfoExpectedRow expected, long elapsedMilliseconds, Exception exception)
        {
            TimeoutCount++;
            RecordParse(expected, elapsedMilliseconds, "timeout");
            AddSkippedSample("timeout", expected, elapsedMilliseconds, exception);
        }

        public void AddParseFailure(RealChartInfoExpectedRow expected, long elapsedMilliseconds, Exception exception)
        {
            ParseFailureCount++;
            RecordParse(expected, elapsedMilliseconds, "failed");
            AddSkippedSample("failed", expected, elapsedMilliseconds, exception);
        }

        public void Add(RealChartInfoExpectedRow expected, LR2SongDBExtended.chart_info actual, string chartString)
        {
            if (IsRandomFeature(expected.feature) || IsRandomFeature(actual.feature))
            {
                return;
            }
            AddCoreDiffs(expected, actual);
            if (!string.Equals(expected.charthash, actual.charthash, StringComparison.OrdinalIgnoreCase))
            {
                ChartHashDiffs++;
                AddFieldDiff("charthash", expected, expected.charthash, actual.charthash);
                AddSample("charthash", expected, expected.charthash, actual.charthash, chartString);
            }
            if (expected.length != actual.length)
            {
                LengthDiffs++;
                AddFieldDiff("length", expected, FormatValue(expected.length), FormatValue(actual.length));
            }
            if (!NullableDoubleEquals(expected.density, actual.density))
            {
                DensityDiffs++;
                AddFieldDiff("density", expected, FormatValue(expected.density), FormatValue(actual.density));
            }
            if (!NullableDoubleEquals(expected.peakdensity, actual.peakdensity))
            {
                PeakDensityDiffs++;
                AddFieldDiff("peakdensity", expected, FormatValue(expected.peakdensity), FormatValue(actual.peakdensity));
            }
            if (!NullableDoubleEquals(expected.enddensity, actual.enddensity))
            {
                EndDensityDiffs++;
                AddFieldDiff("enddensity", expected, FormatValue(expected.enddensity), FormatValue(actual.enddensity));
            }
            if (!string.Equals(expected.distribution, actual.distribution, StringComparison.Ordinal))
            {
                DistributionDiffs++;
                AddFieldDiff("distribution", expected, expected.distribution, actual.distribution);
            }
            if (!string.Equals(expected.speedchange, actual.speedchange, StringComparison.Ordinal))
            {
                SpeedChangeDiffs++;
                AddFieldDiff("speedchange", expected, expected.speedchange, actual.speedchange);
            }
            if ((int)(expected.maxbpm ?? 0.0) != (int)(actual.maxbpm ?? 0.0)
                || (int)(expected.minbpm ?? 0.0) != (int)(actual.minbpm ?? 0.0))
            {
                BpmIntegerDiffs++;
                if ((int)(expected.minbpm ?? 0.0) != (int)(actual.minbpm ?? 0.0))
                {
                    AddFieldDiff("minbpm_integer", expected, FormatValue(expected.minbpm), FormatValue(actual.minbpm));
                }
                if ((int)(expected.maxbpm ?? 0.0) != (int)(actual.maxbpm ?? 0.0))
                {
                    AddFieldDiff("maxbpm_integer", expected, FormatValue(expected.maxbpm), FormatValue(actual.maxbpm));
                }
                AddSample(
                    "bpm",
                    expected,
                    (expected.minbpm ?? 0.0).ToString("R") + "/" + (expected.maxbpm ?? 0.0).ToString("R"),
                    (actual.minbpm ?? 0.0).ToString("R") + "/" + (actual.maxbpm ?? 0.0).ToString("R"),
                    chartString);
            }
        }

        public string WriteReport(string name)
        {
            string directory = Path.Combine(GetReportDirectory(), name);
            Directory.CreateDirectory(directory);

            WriteLines(
                Path.Combine(directory, "field_diffs.csv"),
                new[] { "field,count" }.Concat(fieldDiffCounts
                    .OrderByDescending(pair => pair.Value)
                    .ThenBy(pair => pair.Key, StringComparer.Ordinal)
                    .Select(pair => Csv(pair.Key) + "," + pair.Value.ToString(CultureInfo.InvariantCulture))));

            WriteLines(
                Path.Combine(directory, "diff_rows.csv"),
                new[] { "fixture_id,sha256,fixture_path,field,expected,actual" }.Concat(fieldDiffRecords.Select(record => record.ToCsv())));

            WriteLines(
                Path.Combine(directory, "skipped_rows.csv"),
                new[] { "sample" }.Concat(skippedSamples.Select(Csv)));

            WriteLines(
                Path.Combine(directory, "slow_parse_top.csv"),
                new[] { "rank,status,elapsed_ms,fixture_id,sha256,fixture_path" }.Concat(parseRecords
                    .OrderByDescending(record => record.ElapsedMilliseconds)
                    .Take(20)
                    .Select((record, index) =>
                        (index + 1).ToString(CultureInfo.InvariantCulture)
                            + "," + Csv(record.Status)
                            + "," + record.ElapsedMilliseconds.ToString(CultureInfo.InvariantCulture)
                            + "," + record.FixtureId.ToString(CultureInfo.InvariantCulture)
                            + "," + Csv(record.Sha256)
                            + "," + Csv(record.FixturePath))));

            WriteLines(
                Path.Combine(directory, "summary.txt"),
                [ToString()]);

            return directory;
        }

        public override string ToString()
        {
            return "chart_info real compatibility diffs: "
                + "parsed=" + ParsedCount
                + " timeout=" + TimeoutCount
                + " parseFailure=" + ParseFailureCount
                + " totalParseMs=" + TotalElapsedMs
                + " avgParseMs=" + AvgParseMs.ToString("0.###", CultureInfo.InvariantCulture)
                + " maxParseMs=" + MaxParseMs
                + " p95ParseMs=" + ParseP95Ms
                + " core=" + CoreDiffs
                + " charthash=" + ChartHashDiffs
                + " length=" + LengthDiffs
                + " density=" + DensityDiffs
                + " peakdensity=" + PeakDensityDiffs
                + " enddensity=" + EndDensityDiffs
                + " distribution=" + DistributionDiffs
                + " speedchange=" + SpeedChangeDiffs
                + " bpmInteger=" + BpmIntegerDiffs
                + " totalDiffs=" + TotalDiffs
                + (fieldDiffCounts.Count == 0 ? string.Empty : " fieldTop=" + string.Join(" | ", fieldDiffCounts
                    .OrderByDescending(pair => pair.Value)
                    .ThenBy(pair => pair.Key, StringComparer.Ordinal)
                    .Take(20)
                    .Select(pair => pair.Key + "=" + pair.Value.ToString(CultureInfo.InvariantCulture))))
                + (samples.Count == 0 ? string.Empty : " samples=" + string.Join(" | ", samples))
                + (skippedSamples.Count == 0 ? string.Empty : " skipped=" + string.Join(" | ", skippedSamples))
                + (parseRecords.Count == 0 ? string.Empty : " slowTop=" + string.Join(" | ", GetSlowTopRecords()));
        }

        internal void AddCoreDiffs(RealChartInfoExpectedRow expected, LR2SongDBExtended.chart_info actual)
        {
            CompareCoreField("level", expected, FormatValue(expected.level), FormatValue(actual.level), LevelEqualsBeatoraja(expected.level, actual.level));
            CompareCoreField("difficulty", expected, FormatValue(expected.difficulty), FormatValue(actual.difficulty), expected.difficulty == actual.difficulty);
            CompareCoreField("notes", expected, FormatValue(expected.notes), FormatValue(actual.notes), expected.notes == actual.notes);
            CompareCoreField("n", expected, FormatValue(expected.n), FormatValue(actual.n), expected.n == actual.n);
            CompareCoreField("ln", expected, FormatValue(expected.ln), FormatValue(actual.ln), expected.ln == actual.ln);
            CompareCoreField("s", expected, FormatValue(expected.s), FormatValue(actual.s), expected.s == actual.s);
            CompareCoreField("ls", expected, FormatValue(expected.ls), FormatValue(actual.ls), expected.ls == actual.ls);
            CompareCoreField("judge", expected, FormatValue(expected.judge), FormatValue(actual.judge), expected.judge == actual.judge);
            CompareCoreField("feature", expected, FormatValue(expected.feature), FormatValue(actual.feature), expected.feature == actual.feature);
            CompareCoreField("mode", expected, FormatValue(expected.mode), FormatValue(actual.mode), expected.mode == actual.mode);
            CompareCoreField("mainbpm", expected, FormatValue(expected.mainbpm), FormatValue(actual.mainbpm), NullableDoubleEquals(expected.mainbpm, actual.mainbpm));
            CompareCoreField("lanenotes", expected, expected.lanenotes, actual.lanenotes, string.Equals(expected.lanenotes, actual.lanenotes, StringComparison.Ordinal));
            CompareCoreField("total", expected, FormatValue(expected.total), FormatValue(actual.total), NullableDoubleEquals(expected.total, actual.total));
        }

        internal static bool IsRandomFeature(int? feature)
        {
            return ((feature ?? 0) & 4) != 0;
        }

        internal void CompareCoreField(string field, RealChartInfoExpectedRow expected, string expectedValue, string actualValue, bool equals)
        {
            if (equals)
            {
                return;
            }
            CoreDiffs++;
            AddFieldDiff(field, expected, expectedValue, actualValue);
        }

        internal void AddFieldDiff(string field, RealChartInfoExpectedRow expected, string expectedValue, string actualValue)
        {
            fieldDiffCounts.TryGetValue(field, out int count);
            fieldDiffCounts[field] = count + 1;
            fieldDiffRecords.Add(new FieldDiffRecord
            {
                FixtureId = expected.fixture_id,
                Sha256 = expected.sha256,
                FixturePath = expected.fixture_path,
                Field = field,
                Expected = expectedValue ?? string.Empty,
                Actual = actualValue ?? string.Empty
            });
        }

        internal void AddSample(string field, RealChartInfoExpectedRow expected, string expectedValue, string actualValue, string chartString)
        {
            if (samples.Count >= 5)
            {
                return;
            }
            string artifactPath = WriteChartStringArtifact(expected, chartString);
            samples.Add(
                field
                    + " fixture_id=" + expected.fixture_id
                    + " path=" + expected.fixture_path
                    + " sha256=" + expected.sha256
                    + " expected=" + expectedValue
                    + " actual=" + actualValue
                    + " chartString=" + artifactPath);
        }

        internal void RecordParse(RealChartInfoExpectedRow expected, long elapsedMilliseconds, string status)
        {
            parseElapsedMilliseconds.Add(elapsedMilliseconds);
            parseRecords.Add(new ParseRecord
            {
                FixtureId = expected.fixture_id,
                Sha256 = expected.sha256,
                FixturePath = expected.fixture_path,
                ElapsedMilliseconds = elapsedMilliseconds,
                Status = status
            });
        }

        internal void AddSkippedSample(string status, RealChartInfoExpectedRow expected, long elapsedMilliseconds, Exception exception)
        {
            if (skippedSamples.Count >= 10)
            {
                return;
            }
            skippedSamples.Add(
                status
                    + " fixture_id=" + expected.fixture_id
                    + " path=" + expected.fixture_path
                    + " sha256=" + expected.sha256
                    + " elapsedMs=" + elapsedMilliseconds
                    + " exception=" + exception.GetType().Name
                    + " message=" + (exception.Message ?? string.Empty));
        }

        internal IEnumerable<string> GetSlowTopRecords()
        {
            return parseRecords
                .OrderByDescending(record => record.ElapsedMilliseconds)
                .Take(10)
                .Select(record => "ranked"
                        + " status=" + record.Status
                        + " elapsedMs=" + record.ElapsedMilliseconds
                        + " fixture_id=" + record.FixtureId
                        + " path=" + record.FixturePath
                        + " sha256=" + record.Sha256);
        }

        internal static string WriteChartStringArtifact(RealChartInfoExpectedRow expected, string chartString)
        {
            string directory = Path.Combine(Path.GetTempPath(), "BeMusicSeeker_ChartInfoDiffs");
            Directory.CreateDirectory(directory);
            string artifactPath = Path.Combine(directory, expected.sha256 + ".csharp.chart.txt");
            File.WriteAllText(artifactPath, chartString ?? string.Empty, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
            return artifactPath;
        }

        internal static bool NullableDoubleEquals(double? expected, double? actual)
        {
            if (!expected.HasValue || !actual.HasValue)
            {
                return expected.HasValue == actual.HasValue;
            }
            return Math.Abs(expected.Value - actual.Value) <= 0.000001;
        }

        internal static bool LevelEqualsBeatoraja(int? expected, int? actual)
        {
            if (expected == actual)
            {
                return true;
            }
            return expected.GetValueOrDefault() == 0 && !actual.HasValue;
        }

        internal static string FormatValue(int value)
        {
            return value.ToString(CultureInfo.InvariantCulture);
        }

        internal static string FormatValue(int? value)
        {
            return value.HasValue ? value.Value.ToString(CultureInfo.InvariantCulture) : string.Empty;
        }

        internal static string FormatValue(double? value)
        {
            return value.HasValue ? value.Value.ToString("R", CultureInfo.InvariantCulture) : string.Empty;
        }

        internal static void WriteLines(string path, IEnumerable<string> lines)
        {
            File.WriteAllLines(path, lines, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
        }

        internal static string Csv(string value)
        {
            return "\"" + (value ?? string.Empty).Replace("\"", "\"\"") + "\"";
        }

        internal sealed class ParseRecord
        {
            public int FixtureId { get; set; }

            public string Sha256 { get; set; } = string.Empty;

            public string FixturePath { get; set; } = string.Empty;

            public long ElapsedMilliseconds { get; set; }

            public string Status { get; set; } = string.Empty;
        }

        internal sealed class FieldDiffRecord
        {
            public int FixtureId { get; set; }

            public string Sha256 { get; set; } = string.Empty;

            public string FixturePath { get; set; } = string.Empty;

            public string Field { get; set; } = string.Empty;

            public string Expected { get; set; } = string.Empty;

            public string Actual { get; set; } = string.Empty;

            public string ToCsv()
            {
                return FixtureId.ToString(CultureInfo.InvariantCulture)
                    + "," + Csv(Sha256)
                    + "," + Csv(FixturePath)
                    + "," + Csv(Field)
                    + "," + Csv(Expected)
                    + "," + Csv(Actual);
            }
        }
    }
}
