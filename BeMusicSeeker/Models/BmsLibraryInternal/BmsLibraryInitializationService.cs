using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using BeMusicSeeker.Models.LR2;
using BeMusicSeeker.Models.Utils;
using BeMusicSeeker.Properties;
using Ribbit.Util.Extensions;
using SQLite;

namespace BeMusicSeeker.Models.BmsLibraryInternal;

internal sealed class Lr2NormalFolderMtimeSnapshot(
    IReadOnlyList<string> rootDirectories,
    IReadOnlyDictionary<string, LR2SongDB.folder> existingRowsByPath,
    bool readOnly,
    long dbLockWaitMs,
    long elapsedMs)
{
    public IReadOnlyList<string> RootDirectories { get; } = rootDirectories ?? [];

    public IReadOnlyDictionary<string, LR2SongDB.folder> ExistingRowsByPath { get; } =
        existingRowsByPath ?? new Dictionary<string, LR2SongDB.folder>(StringComparer.OrdinalIgnoreCase);

    public int ExistingRowCount => ExistingRowsByPath.Count;

    public bool ReadOnly { get; } = readOnly;

    public long DbLockWaitMs { get; } = dbLockWaitMs;

    public long ElapsedMs { get; } = elapsedMs;
}

/// <summary>
/// Loads initialization phases against snapshot inputs owned by BMSLibrary.
/// The facade must acquire the required locks before invoking phase methods.
/// </summary>
internal sealed class BmsLibraryInitializationService
{
    private const string SongCatalogRawSelectSql =
        "SELECT hash, title, subtitle, artist, subartist, genre, tag, path, type, folder, stagefile, banner, backbmp, parent, level, difficulty, maxbpm, minbpm, mode, judge, longnote, bga, random, date, favorite, txt, karinotes, adddate, exlevel FROM song;";

    private const string MaintenanceRawSelectSql =
        "SELECT hash, path, encoding, is_encoding_fixed, wav_files_existing, wav_files_defined, bga_files_existing, bga_files_defined, movie_files_existing, movie_files_defined, is_stagefile_existing, is_stagefile_defined, is_banner_existing, is_banner_defined, is_backbmp_existing, is_backbmp_defined, is_files_warning_ignored, lr2_path_warning_flags, lr2_chart_path_cp932_bytes, lr2_folder_scan_cp932_bytes, lr2_resource_warning_flags, lr2_resource_max_raw_cp932_bytes, lr2_resource_max_resolved_cp932_bytes, lr2_resource_unsupported_count FROM maintenance;";

    private const int DefaultInlineChartInfoBatchSize = 2048;

    private const int DefaultFileDiffPostParseBatchSize = 1;

    private const int DefaultFileDiffCommitChunkSize = 10000;

    private const int DefaultFileDiffCommitWriterQueueCapacity = 2;

    private const int DefaultSlowFileDiffBatchLogThresholdMs = 2000;

    private readonly int? fileDiffParserDegreeOverride;

    private readonly ChartInfoBuildService chartInfoBuildService;

    private readonly int? inlineChartInfoBatchSizeOverride;

    private readonly int? fileDiffCommitChunkSizeOverride;

    public BmsLibraryInitializationService()
        : this(null, null, null, null)
    {
    }

    internal BmsLibraryInitializationService(int? fileDiffParserDegreeOverride, ChartInfoBuildService chartInfoBuildService = null, int? inlineChartInfoBatchSizeOverride = null, int? fileDiffCommitChunkSizeOverride = null)
    {
        this.fileDiffParserDegreeOverride = fileDiffParserDegreeOverride;
        this.chartInfoBuildService = chartInfoBuildService ?? new ChartInfoBuildService();
        this.inlineChartInfoBatchSizeOverride = inlineChartInfoBatchSizeOverride;
        this.fileDiffCommitChunkSizeOverride = fileDiffCommitChunkSizeOverride;
    }

    public SongTableLoadResult LoadSongTable(
        BmsLibraryDbGateway dbGateway,
        BmsLibraryOptionsSnapshot options,
        IBmsLibraryDialogService dialogService,
        IFileMutationService fileMutationService,
        FileMutationOptions targetOnlyFileMutationOptions,
        Func<Exception, string> getDisplayedExceptionMessage,
        Action<string> logInstallPerformance = null,
        Action<string> logDebugTrace = null)
    {
        var result = new SongTableLoadResult();
        if (dbGateway == null)
        {
            return result;
        }
        using LR2SongDBExtended songDb = dbGateway.OpenSongDb();
        result.Pragmas.AddRange(songDb.TryApplyReadOptimizedPragmas(options?.EnableReadOptimizedPragmas ?? false));
        if (result.Pragmas.Count > 0)
        {
            logInstallPerformance?.Invoke("db_read_pragmas scope=song_tbl_load " + string.Join(" ", result.Pragmas));
        }

        var stopwatchSongTableLoad = Stopwatch.StartNew();
        var stopwatchSongCount = Stopwatch.StartNew();
        try
        {
            result.SongTableCount = Math.Max(0L, songDb.ExecuteScalar<long>("SELECT COUNT(1) FROM song;"));
        }
        catch
        {
        }
        stopwatchSongCount.Stop();
        result.SongCountMs = stopwatchSongCount.ElapsedMilliseconds;

        var stopwatchSongMaterialize = Stopwatch.StartNew();
        List<BMSFile> loadedSongs;
        if (UseRawSongCatalogLoader())
        {
            loadedSongs = LoadSongCatalogRaw(songDb, result);
        }
        else
        {
            result.SongMaterializeMode = "sqlite_net";
            loadedSongs = (result.SongTableCount > 0L && result.SongTableCount <= int.MaxValue)
                ? new List<BMSFile>((int)result.SongTableCount)
                : [];
            using (BMSFile.SuppressPropertyChangedScope())
            {
                foreach (BMSFile item in songDb.Table<BMSFile>())
                {
                    loadedSongs.Add(item);
                }
            }
        }
        stopwatchSongMaterialize.Stop();
        result.SongMaterializeMs = stopwatchSongMaterialize.ElapsedMilliseconds;
        stopwatchSongTableLoad.Stop();
        result.SongTableLoadMs = stopwatchSongTableLoad.ElapsedMilliseconds;

        List<BMSFile> deletedFiles = [];
        logDebugTrace?.Invoke("relative path and invalid md5 check");
        if (!string.IsNullOrWhiteSpace(songDb.LR2RootPath))
        {
            NormalizeSongTable(
                songDb,
                loadedSongs,
                deletedFiles,
                result,
                dialogService,
                fileMutationService,
                targetOnlyFileMutationOptions,
                getDisplayedExceptionMessage,
                logInstallPerformance);
        }
        else
        {
            NormalizeStandaloneSongPathCompatibility(songDb, loadedSongs, result);
        }
        logDebugTrace?.Invoke("relative path check end");

        var stopwatchChartDigestMapLoad = Stopwatch.StartNew();
        Dictionary<string, string> chartDigestMap = dbGateway.LoadChartDigestMap(songDb);
        stopwatchChartDigestMapLoad.Stop();
        result.ChartDigestMapLoadMs = stopwatchChartDigestMapLoad.ElapsedMilliseconds;
        foreach (KeyValuePair<string, string> item in chartDigestMap)
        {
            result.ChartDigestMap[item.Key] = item.Value;
        }

        var stopwatchBmsonTableLoad = Stopwatch.StartNew();
        if (TableExists(songDb, SQLiteTable<LR2SongDBExtended.bmson_song>.GetTableName()))
        {
            foreach (LR2SongDBExtended.bmson_song item in songDb.Table<LR2SongDBExtended.bmson_song>())
            {
                if (item != null && !string.IsNullOrWhiteSpace(item.path))
                {
                    result.LoadedBmsonSongs.Add(item);
                }
            }
        }
        stopwatchBmsonTableLoad.Stop();
        result.BmsonTableLoadMs = stopwatchBmsonTableLoad.ElapsedMilliseconds;

        HashSet<BMSFile> deletedFileSet = deletedFiles.Count > 0 ? [.. deletedFiles] : null;
        var stopwatchChartDigestApply = Stopwatch.StartNew();
        using (BMSFile.SuppressPropertyChangedScope())
        {
            foreach (BMSFile item in loadedSongs)
            {
                if (deletedFileSet != null && deletedFileSet.Contains(item))
                {
                    continue;
                }
                if (!string.IsNullOrWhiteSpace(item.hash) && result.ChartDigestMap.TryGetValue(item.hash, out string sha256))
                {
                    item.ApplySha256(sha256);
                }
                result.LoadedFiles.Add(item);
            }
        }
        stopwatchChartDigestApply.Stop();
        result.ChartDigestApplyMs = stopwatchChartDigestApply.ElapsedMilliseconds;

        // chart_info is display/search metadata. Loading and applying every row can dominate startup
        // on large libraries, so the application hydrates it after the core install workflow is operable.
        logInstallPerformance?.Invoke(
            "song_tbl_load_projection projection=catalog mode=" + result.SongMaterializeMode
            + " readMs=" + result.SongTableLoadMs
            + " materializeMs=" + result.SongMaterializeMs
            + " rawReadMs=" + result.SongRawReadMs
            + " rawObjectMs=" + result.SongRawObjectMs
            + " rawRows=" + result.SongRawRows
            + " rows=" + result.SongTableCount
            + " chartDigestReadMs=" + result.ChartDigestMapLoadMs
            + " chartDigestApplyMs=" + result.ChartDigestApplyMs
            + " bmsonReadMs=" + result.BmsonTableLoadMs
            + " bmsonRows=" + result.LoadedBmsonSongs.Count
            + " chartDigestRows=" + result.ChartDigestMap.Count);
        logInstallPerformance?.Invoke(
            "song_tbl_load_io song_read_ms=" + result.SongTableLoadMs
            + " song_count_ms=" + result.SongCountMs
            + " song_materialize_ms=" + result.SongMaterializeMs
            + " song_count=" + result.SongTableCount
            + " folder_read_ms=" + result.FolderTableLoadMs
            + " db_write_required=" + result.DbWriteRequired.ToString().ToLowerInvariant()
            + " db_write_ms=" + result.DbWriteMs);
        return result;
    }

    private static bool UseRawSongCatalogLoader()
    {
        string mode = Environment.GetEnvironmentVariable("BMS_SONG_TABLE_LOAD_MODE");
        return !string.Equals(mode, "sqlite_net", StringComparison.OrdinalIgnoreCase);
    }

    private static List<BMSFile> LoadSongCatalogRaw(LR2SongDBExtended songDb, SongTableLoadResult result)
    {
        List<BMSFile> loadedSongs = (result.SongTableCount > 0L && result.SongTableCount <= int.MaxValue)
            ? new List<BMSFile>((int)result.SongTableCount)
            : [];
        result.SongMaterializeMode = "raw_string";
        SQLiteCommand command = songDb.CreateCommand(SongCatalogRawSelectSql);
        long objectTicks = 0L;
        long stopwatchFrequency = Stopwatch.Frequency;
        var totalStopwatch = Stopwatch.StartNew();
        int rawRows = ((LR2SongDBExtended.SQLiteCommandExtended)command).ForEachRawValueAsString(delegate (string[] values)
        {
            long objectStart = Stopwatch.GetTimestamp();
            loadedSongs.Add(BMSFile.FromSongTableRawValues(values));
            objectTicks += Stopwatch.GetTimestamp() - objectStart;
        });
        totalStopwatch.Stop();
        long totalMs = totalStopwatch.ElapsedMilliseconds;
        result.SongRawRows = rawRows;
        result.SongRawObjectMs = stopwatchFrequency > 0L ? objectTicks * 1000L / stopwatchFrequency : 0L;
        result.SongRawReadMs = Math.Max(0L, totalMs - result.SongRawObjectMs);
        return loadedSongs;
    }

    public MaintenanceTableHydrationResult LoadMaintenanceTable(
        BmsLibraryDbGateway dbGateway,
        BmsLibraryOptionsSnapshot options,
        Action<string> logInstallPerformance = null)
    {
        var result = new MaintenanceTableHydrationResult();
        if (dbGateway == null)
        {
            return result;
        }

        var totalStopwatch = Stopwatch.StartNew();
        using LR2SongDBExtended songDb = dbGateway.OpenSongDbReadOnly();
        result.ReadOnly = songDb.IsReadOnlyConnection;
        result.DbLockWaitMs = songDb.ProcessLockWaitMs;
        result.Pragmas.AddRange(songDb.TryApplyReadOptimizedPragmas(options?.EnableReadOptimizedPragmas ?? false));
        if (result.Pragmas.Count > 0)
        {
            logInstallPerformance?.Invoke("db_read_pragmas scope=maintenance_hydration " + string.Join(" ", result.Pragmas));
        }

        var stopwatchMaintenanceTableLoad = Stopwatch.StartNew();
        var stopwatchMaintenanceCount = Stopwatch.StartNew();
        try
        {
            result.MaintenanceTableCount = TableExists(songDb, SQLiteTable<LR2SongDBExtended.maintenance>.GetTableName())
                ? Math.Max(0L, songDb.ExecuteScalar<long>("SELECT COUNT(1) FROM maintenance;"))
                : 0L;
        }
        catch
        {
        }
        stopwatchMaintenanceCount.Stop();
        result.MaintenanceCountMs = stopwatchMaintenanceCount.ElapsedMilliseconds;

        List<BMSFileMaintenanceInfo> maintenanceInfos = (result.MaintenanceTableCount > 0L && result.MaintenanceTableCount <= int.MaxValue)
            ? new List<BMSFileMaintenanceInfo>((int)result.MaintenanceTableCount)
            : [];
        var stopwatchMaintenanceMaterialize = Stopwatch.StartNew();
        if (result.MaintenanceTableCount > 0L)
        {
            if (UseRawMaintenanceTableLoader())
            {
                LoadMaintenanceTableRaw(songDb, maintenanceInfos, result);
            }
            else
            {
                result.MaintenanceMaterializeMode = "sqlite_net";
                using (BMSFileMaintenanceInfo.SuppressPropertyChangedScope())
                {
                    foreach (BMSFileMaintenanceInfo item in songDb.Table<BMSFileMaintenanceInfo>())
                    {
                        maintenanceInfos.Add(item);
                    }
                }
            }
        }
        stopwatchMaintenanceMaterialize.Stop();
        result.MaintenanceMaterializeMs = stopwatchMaintenanceMaterialize.ElapsedMilliseconds;
        stopwatchMaintenanceTableLoad.Stop();
        result.MaintenanceTableLoadMs = stopwatchMaintenanceTableLoad.ElapsedMilliseconds;

        var stopwatchMaintenanceMapBuild = Stopwatch.StartNew();
        foreach (BMSFileMaintenanceInfo maintenanceInfo in maintenanceInfos)
        {
            if (!string.IsNullOrWhiteSpace(maintenanceInfo.path) && !result.MaintenanceMap.ContainsKey(maintenanceInfo.path))
            {
                result.MaintenanceMap[maintenanceInfo.path] = maintenanceInfo;
            }
        }
        stopwatchMaintenanceMapBuild.Stop();
        result.MaintenanceMapBuildMs = stopwatchMaintenanceMapBuild.ElapsedMilliseconds;
        totalStopwatch.Stop();
        result.TotalMs = totalStopwatch.ElapsedMilliseconds;
        return result;
    }

    private static bool UseRawMaintenanceTableLoader()
    {
        string mode = Environment.GetEnvironmentVariable("BMS_MAINTENANCE_TABLE_LOAD_MODE");
        return !string.Equals(mode, "sqlite_net", StringComparison.OrdinalIgnoreCase);
    }

    private static void LoadMaintenanceTableRaw(
        LR2SongDBExtended songDb,
        List<BMSFileMaintenanceInfo> maintenanceInfos,
        MaintenanceTableHydrationResult result)
    {
        result.MaintenanceMaterializeMode = "raw_string";
        SQLiteCommand command = songDb.CreateCommand(MaintenanceRawSelectSql);
        long objectTicks = 0L;
        long stopwatchFrequency = Stopwatch.Frequency;
        var totalStopwatch = Stopwatch.StartNew();
        using (BMSFileMaintenanceInfo.SuppressPropertyChangedScope())
        {
            int rawRows = ((LR2SongDBExtended.SQLiteCommandExtended)command).ForEachRawValueAsString(delegate (string[] values)
            {
                long objectStart = Stopwatch.GetTimestamp();
                maintenanceInfos.Add(CreateMaintenanceInfoFromRawValues(values));
                objectTicks += Stopwatch.GetTimestamp() - objectStart;
            });
            result.MaintenanceRawRows = rawRows;
        }
        totalStopwatch.Stop();
        long totalMs = totalStopwatch.ElapsedMilliseconds;
        result.MaintenanceRawObjectMs = stopwatchFrequency > 0L ? objectTicks * 1000L / stopwatchFrequency : 0L;
        result.MaintenanceRawReadMs = Math.Max(0L, totalMs - result.MaintenanceRawObjectMs);
    }

    private static BMSFileMaintenanceInfo CreateMaintenanceInfoFromRawValues(string[] values)
    {
        return new BMSFileMaintenanceInfo
        {
            hash = GetRawValue(values, 0),
            path = GetRawValue(values, 1),
            encoding = GetRawValue(values, 2),
            is_encoding_fixed = ParseBoolean(GetRawValue(values, 3)),
            wav_files_existing = ParseNullableInt(GetRawValue(values, 4)),
            wav_files_defined = ParseNullableInt(GetRawValue(values, 5)),
            bga_files_existing = ParseNullableInt(GetRawValue(values, 6)),
            bga_files_defined = ParseNullableInt(GetRawValue(values, 7)),
            movie_files_existing = ParseNullableInt(GetRawValue(values, 8)),
            movie_files_defined = ParseNullableInt(GetRawValue(values, 9)),
            is_stagefile_existing = ParseNullableBoolean(GetRawValue(values, 10)),
            is_stagefile_defined = ParseNullableBoolean(GetRawValue(values, 11)),
            is_banner_existing = ParseNullableBoolean(GetRawValue(values, 12)),
            is_banner_defined = ParseNullableBoolean(GetRawValue(values, 13)),
            is_backbmp_existing = ParseNullableBoolean(GetRawValue(values, 14)),
            is_backbmp_defined = ParseNullableBoolean(GetRawValue(values, 15)),
            is_files_warning_ignored = ParseBoolean(GetRawValue(values, 16)),
            lr2_path_warning_flags = ParseNullableInt(GetRawValue(values, 17)),
            lr2_chart_path_cp932_bytes = ParseNullableInt(GetRawValue(values, 18)),
            lr2_folder_scan_cp932_bytes = ParseNullableInt(GetRawValue(values, 19)),
            lr2_resource_warning_flags = ParseNullableInt(GetRawValue(values, 20)),
            lr2_resource_max_raw_cp932_bytes = ParseNullableInt(GetRawValue(values, 21)),
            lr2_resource_max_resolved_cp932_bytes = ParseNullableInt(GetRawValue(values, 22)),
            lr2_resource_unsupported_count = ParseNullableInt(GetRawValue(values, 23))
        };
    }

    private static string GetRawValue(string[] values, int index)
    {
        return index >= 0 && index < values.Length ? values[index] : null;
    }

    private static int? ParseNullableInt(string value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return null;
        }
        return int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out int parsed) ? parsed : null;
    }

    private static bool ParseBoolean(string value)
    {
        return !string.IsNullOrWhiteSpace(value)
            && (string.Equals(value, "1", StringComparison.Ordinal)
                || string.Equals(value, "true", StringComparison.OrdinalIgnoreCase));
    }

    private static bool? ParseNullableBoolean(string value)
    {
        return string.IsNullOrWhiteSpace(value) ? null : ParseBoolean(value);
    }

    public Lr2NormalFolderMtimeSnapshot LoadNormalFolderMtimeSnapshot(
        BmsLibraryDbGateway dbGateway,
        BmsLibraryOptionsSnapshot options,
        IEnumerable<string> rootDirectories,
        Action<string> logInstallPerformance = null)
    {
        if (dbGateway == null
            || options?.OperationModeLR2DB != true
            || options.EnableLR2SongDbFullGeneration != true)
        {
            return null;
        }

        var stopwatch = Stopwatch.StartNew();
        List<string> roots = NormalizeNormalFolderMtimeRoots(rootDirectories);
        if (roots.Count == 0)
        {
            stopwatch.Stop();
            return new Lr2NormalFolderMtimeSnapshot(
                roots,
                new Dictionary<string, LR2SongDB.folder>(StringComparer.OrdinalIgnoreCase),
                readOnly: false,
                dbLockWaitMs: 0L,
                stopwatch.ElapsedMilliseconds);
        }

        using LR2SongDBExtended songDb = dbGateway.OpenSongDb();
        IReadOnlyList<LR2SongDB.folder> rows = Lr2FolderExistingRowLookup.QueryNormalFolderMtimePathPrefixScopes(
            songDb,
            roots.Select(Lr2FolderPath.ToFolderPath));
        Dictionary<string, LR2SongDB.folder> rowsByPath = CreateExistingNormalFolderRowMap(rows);
        stopwatch.Stop();
        var snapshot = new Lr2NormalFolderMtimeSnapshot(
            roots,
            rowsByPath,
            songDb.IsReadOnlyConnection,
            songDb.ProcessLockWaitMs,
            stopwatch.ElapsedMilliseconds);
        logInstallPerformance?.Invoke("lr2_normal_folder_mtime_snapshot_prefetch"
            + " roots=" + snapshot.RootDirectories.Count
            + " existingRows=" + snapshot.ExistingRowCount
            + " readOnly=" + snapshot.ReadOnly.ToString().ToLowerInvariant()
            + " dbLockWaitMs=" + snapshot.DbLockWaitMs
            + " elapsedMs=" + snapshot.ElapsedMs);
        return snapshot;
    }

    public SongTableFileCheckResult ApplyFileScanDiff(
        BmsLibraryDbGateway dbGateway,
        BmsLibraryOptionsSnapshot options,
        IEnumerable<BMSFile> currentFiles,
        ChartScanExecutionResult prefetchedScanResult,
        long prefetchedScanElapsedMs,
        Func<ChartScanExecutionResult> executeScan,
        IBmsLibraryDialogService dialogService,
        Action<string> logInstallPerformance = null,
        Action<string> logEverythingScan = null,
        IEnumerable<LR2SongDBExtended.bmson_song> currentBmsonSongs = null,
        Func<ChartScanExecutionResult> executeBmsonScan = null,
        Action scanCompleted = null,
        Action fileDiffStarted = null,
        Action<int, int, string> reportParseProgress = null,
        Action<string> logInstallPerformanceWarn = null,
        Action<IReadOnlyList<LR2SongDBExtended.chart_info>> inlineChartInfoRowsCommitted = null,
        IEnumerable<ChartFile> currentInstallDestinationCharts = null,
        IEnumerable<string> lr2NormalFolderSyncRootDirectories = null,
        IEnumerable<string> lr2FolderDiscoveryRootDirectories = null,
        Lr2BuiltinCustomFolderSettings lr2BuiltinCustomFolderSettings = null,
        Lr2NormalFolderMtimeSnapshot normalFolderMtimeSnapshot = null,
        Func<Lr2NormalFolderMtimeSnapshot> normalFolderMtimeSnapshotProvider = null,
        Action<SongTableFileCheckResult> lr2ScanSurfacePrepared = null,
        bool protectExistingBmsRowsFromLr2FullGenerationMigration = false)
    {
        var result = new SongTableFileCheckResult();
        var stopwatchScan = Stopwatch.StartNew();
        ChartScanExecutionResult scanResult = prefetchedScanResult;
        if (scanResult != null && scanResult.Result != null)
        {
            result.PrefetchedScanUsed = true;
        }
        else
        {
            scanResult = executeScan?.Invoke();
        }
        if (scanResult?.Result == null)
        {
            stopwatchScan.Stop();
            scanCompleted?.Invoke();
            return result;
        }
        bool bmsFileScanSucceeded = scanResult.Success;
        if (bmsFileScanSucceeded)
        {
            result.Lr2ScanSurfaceAvailable = true;
            ApplyLr2FolderScanSurface(
                result,
                options,
                lr2FolderDiscoveryRootDirectories,
                lr2BuiltinCustomFolderSettings,
                logEverythingScan);
        }
        stopwatchScan.Stop();
        scanCompleted?.Invoke();
        fileDiffStarted?.Invoke();
        ChartScanResult mergedScanResult = scanResult.Result;
        if (executeBmsonScan != null)
        {
            var stopwatchBmsonScan = Stopwatch.StartNew();
            ChartScanExecutionResult bmsonScanResult = executeBmsonScan();
            stopwatchBmsonScan.Stop();
            if (bmsonScanResult?.Result != null)
            {
                mergedScanResult = MergeScanResults(scanResult.Result, bmsonScanResult.Result);
            }
            result.ScanElapsedMs = stopwatchScan.ElapsedMilliseconds + stopwatchBmsonScan.ElapsedMilliseconds + (result.PrefetchedScanUsed ? prefetchedScanElapsedMs : 0L);
        }
        else
        {
            result.ScanElapsedMs = stopwatchScan.ElapsedMilliseconds + (result.PrefetchedScanUsed ? prefetchedScanElapsedMs : 0L);
        }
        result.NativeBridgeMs = scanResult.NativeBridgeMs;
        result.NativeBridgeReason = scanResult.NativeBridgeReason ?? string.Empty;
        result.ManagedDecodeMs = scanResult.ManagedDecodeMs;
        result.ManagedMaterializeMs = scanResult.ManagedMaterializeMs;
        result.BridgeRawBufferBytes = scanResult.BridgeRawBufferBytes;
        bool canUseNativeResourceIndex = executeBmsonScan == null && scanResult.ResourceIndex != null && ReferenceEquals(mergedScanResult, scanResult.Result);
        result.NextResourceIndex = canUseNativeResourceIndex
            ? scanResult.ResourceIndex
            : LibraryResourceIndex.CreateFromScanResult(mergedScanResult);
        result.NextDirectoryResourceLookupCache = result.NextResourceIndex.DirectoryLookupCache;
        result.ResourceIndexBuildMs = result.NextResourceIndex.BuildMs;
        result.DirhashBuildMs = result.NextResourceIndex.BuildMs;
        result.ResourceLookupCacheMs = result.NextResourceIndex.ResourceLookupMs;
        ulong audioResourceKeyEntryCount = canUseNativeResourceIndex ? scanResult.AudioResourceKeyHashCount : CountHashEntries(mergedScanResult.AudioRelativePathHashesByChartDirectory);
        ulong imageResourceKeyEntryCount = canUseNativeResourceIndex ? scanResult.ImageResourceKeyHashCount : CountHashEntries(mergedScanResult.ImageRelativePathHashesByChartDirectory);
        ulong movieResourceKeyEntryCount = canUseNativeResourceIndex ? scanResult.MovieResourceKeyHashCount : CountHashEntries(mergedScanResult.MovieRelativePathHashesByChartDirectory);
        ulong chartRelativeKeyCount = audioResourceKeyEntryCount + imageResourceKeyEntryCount + movieResourceKeyEntryCount;
        logInstallPerformance?.Invoke("resource_index_build source=" + (result.NextResourceIndex.Source ?? "managed")
            + " directories=" + result.NextResourceIndex.DirectoryCount
            + " chartRelativeKeys=" + chartRelativeKeyCount
            + " reverseLookupKeys=" + (result.NextDirectoryResourceLookupCache?.CategoryReverseLookupEntryCount ?? 0)
            + " reverseLookupSource=" + (string.Equals(result.NextResourceIndex.Source, "native_canonical", StringComparison.OrdinalIgnoreCase) ? "native" : "managed")
            + " buildMs=" + result.ResourceIndexBuildMs
            + " lookupMs=" + result.ResourceLookupCacheMs
            + " nativePackMs=" + scanResult.PackMs
            + " managedMaterializeMs=" + result.ManagedMaterializeMs
            + " payloadBytes=" + result.BridgeRawBufferBytes);
        result.AudioResourceKeyHashEntryCount = audioResourceKeyEntryCount;
        result.ImageResourceKeyHashEntryCount = imageResourceKeyEntryCount;
        result.MovieResourceKeyHashEntryCount = movieResourceKeyEntryCount;

        if (bmsFileScanSucceeded)
        {
            result.Lr2ScanNormalFolderDirectoryPaths = [.. Lr2NormalFolderDbSyncService.CreateDirectoryMetadataTargetsFromNormalizedDirectories(
                lr2NormalFolderSyncRootDirectories,
                mergedScanResult.ChartDirectories ?? new HashSet<string>(StringComparer.OrdinalIgnoreCase))];
            result.Lr2ScanDirectoryEntries = new Dictionary<string, RootFileEnumerationEntry>(
                mergedScanResult.DirectoryEntriesByPath ?? new Dictionary<string, RootFileEnumerationEntry>(StringComparer.OrdinalIgnoreCase),
                StringComparer.OrdinalIgnoreCase);
            result.Lr2ScanNormalFolderDirectoryEntries = Lr2FolderDirectoryEnumerationService.CreateEntriesFromSurface(
                result.Lr2ScanDirectoryEntries,
                result.Lr2ScanNormalFolderDirectoryPaths);
            result.Lr2ScanFolderInfoFilePaths = [.. (mergedScanResult.FolderInfoFilePaths ?? [])
                .Where(path => !string.IsNullOrWhiteSpace(path))
                .OrderBy(path => path, StringComparer.OrdinalIgnoreCase)];
            result.Lr2ScanFolderInfoFileEntries = new Dictionary<string, RootFileEnumerationEntry>(
                mergedScanResult.FolderInfoFileEntriesByPath ?? new Dictionary<string, RootFileEnumerationEntry>(StringComparer.OrdinalIgnoreCase),
                StringComparer.OrdinalIgnoreCase);
            result.Lr2ScanTextFileDirectories = [.. (mergedScanResult.ChartDirectoriesWithTextFiles ?? [])
                .Where(path => !string.IsNullOrWhiteSpace(path))
                .OrderBy(path => path, StringComparer.OrdinalIgnoreCase)];
            lr2ScanSurfacePrepared?.Invoke(result);
        }
        result.DirectoryCount = result.NextDirectoryResourceLookupCache?.Count ?? 0;

        var stopwatchDiff = Stopwatch.StartNew();
        var stopwatchCurrentIndex = Stopwatch.StartNew();
        var currentFileList = new List<BMSFile>();
        var currentBmsByPath = new Dictionary<string, BMSFile>(StringComparer.OrdinalIgnoreCase);
        foreach (BMSFile file in currentFiles ?? [])
        {
            if (file == null)
            {
                continue;
            }
            currentFileList.Add(file);
            if (!string.IsNullOrWhiteSpace(file.path) && !currentBmsByPath.ContainsKey(file.path))
            {
                currentBmsByPath[file.path] = file;
            }
        }
        var currentBmsonList = new List<LR2SongDBExtended.bmson_song>();
        var currentBmsonByPath = new Dictionary<string, LR2SongDBExtended.bmson_song>(StringComparer.OrdinalIgnoreCase);
        foreach (LR2SongDBExtended.bmson_song song in currentBmsonSongs ?? [])
        {
            if (song == null || string.IsNullOrWhiteSpace(song.path))
            {
                continue;
            }
            currentBmsonList.Add(song);
            currentBmsonByPath[song.path] = song;
        }
        stopwatchCurrentIndex.Stop();
        result.DiffCurrentIndexMs = stopwatchCurrentIndex.ElapsedMilliseconds;

        var stopwatchScannedSplit = Stopwatch.StartNew();
        var scannedPaths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var scannedBmsonPaths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (string path in mergedScanResult.ChartFilePaths ?? [])
        {
            if (string.IsNullOrWhiteSpace(path))
            {
                continue;
            }
            if (IsBmsonChartPath(path))
            {
                scannedBmsonPaths.Add(path);
            }
            else
            {
                scannedPaths.Add(path);
            }
        }
        stopwatchScannedSplit.Stop();
        result.DiffScannedSplitMs = stopwatchScannedSplit.ElapsedMilliseconds;
        result.BmsPathCount = scannedPaths.Count;

        var stopwatchDeleted = Stopwatch.StartNew();
        foreach (string currentPath in currentBmsByPath.Keys)
        {
            if (!scannedPaths.Contains(currentPath))
            {
                result.DeletedPaths.Add(currentPath);
            }
        }
        foreach (string currentPath in currentBmsonByPath.Keys)
        {
            if (!scannedBmsonPaths.Contains(currentPath))
            {
                result.DeletedBmsonPaths.Add(currentPath);
            }
        }
        stopwatchDeleted.Stop();
        result.DiffDeletedMs = stopwatchDeleted.ElapsedMilliseconds;

        HashSet<string> textFileDirectories = mergedScanResult.ChartDirectoriesWithTextFiles ?? new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        bool textGroupSurfaceAvailable = options?.OperationModeLR2DB == true
            && options.EnableLR2SongDbFullGeneration == true
            && bmsFileScanSucceeded;
        IReadOnlyDictionary<string, RootFileEnumerationEntry> chartFileEntriesByPath =
            mergedScanResult.ChartFileEntriesByPath ?? new Dictionary<string, RootFileEnumerationEntry>(StringComparer.OrdinalIgnoreCase);
        Dictionary<string, Queue<BMSFile>> movedBmsSourcesByMd5 = BuildQueueByMd5(
            result.DeletedPaths
                .Select(path => currentBmsByPath.TryGetValue(path, out BMSFile file) ? file : null)
                .Where(file => file != null),
            file => file.hash);
        IReadOnlyDictionary<string, Lr2SongUserColumns> movedBmsUserColumnsByDeletedPath =
            dbGateway?.CreateSongUserColumnSnapshot(result.DeletedPaths)
            ?? new Dictionary<string, Lr2SongUserColumns>(StringComparer.OrdinalIgnoreCase);
        List<FileDiffParseTarget> bmsParseTargets = [];
        var stopwatchBmsTargets = Stopwatch.StartNew();
        foreach (string path in scannedPaths)
        {
            FileDiffParseTarget target = CreateBmsFileDiffTarget(
                path,
                currentBmsByPath,
                textFileDirectories,
                textGroupSurfaceAvailable,
                chartFileEntriesByPath,
                result,
                protectExistingBmsRowsFromLr2FullGenerationMigration);
            if (target != null)
            {
                bmsParseTargets.Add(target);
            }
        }
        stopwatchBmsTargets.Stop();
        result.DiffBmsTargetMs = stopwatchBmsTargets.ElapsedMilliseconds;

        List<string> addedOrUpdatedBmsonPaths = [];
        var stopwatchBmsonTargets = Stopwatch.StartNew();
        foreach (string path in scannedBmsonPaths)
        {
            if (!currentBmsonByPath.TryGetValue(path, out LR2SongDBExtended.bmson_song existing))
            {
                addedOrUpdatedBmsonPaths.Add(path);
                continue;
            }

            DateTime lastWriteTimeUtc = ResolveScannedChartLastWriteTimeUtc(
                path,
                chartFileEntriesByPath,
                result,
                isBmson: true);
            if (lastWriteTimeUtc != DateTime.MinValue && existing.updated_at != lastWriteTimeUtc)
            {
                addedOrUpdatedBmsonPaths.Add(path);
            }
        }
        stopwatchBmsonTargets.Stop();
        result.DiffBmsonTargetMs = stopwatchBmsonTargets.ElapsedMilliseconds;
        stopwatchDiff.Stop();
        result.DiffMs = stopwatchDiff.ElapsedMilliseconds;
        result.BmsAddedTargetCount = bmsParseTargets.Count;
        result.BmsDeletedTargetCount = result.DeletedPaths.Count;
        result.BmsonUpsertTargetCount = addedOrUpdatedBmsonPaths.Count;
        result.BmsonDeletedTargetCount = result.DeletedBmsonPaths.Count;
        result.FileDiffParserDegree = ResolveFileDiffParserDegree();
        result.ParseReadBytesEstimate = SaturatingAdd(
            EstimateCurrentFileDiffReadBytes(bmsParseTargets.Select(target => target.Path)),
            EstimateCurrentFileDiffReadBytes(addedOrUpdatedBmsonPaths));

        long bmsLightweightParseMs = 0L;
        int parseTargetCount = bmsParseTargets.Count + addedOrUpdatedBmsonPaths.Count;
        int parseProcessedCount = 0;
        if (parseTargetCount > 0)
        {
            reportParseProgress?.Invoke(parseTargetCount, 0, string.Empty);
        }
        result.InlineChartInfoBatchSize = ResolveInlineChartInfoBatchSize();
        result.DbCommitChunkSize = ResolveFileDiffCommitChunkSize();
        var inlineMaintenanceLookupContext = new ResourceHealthLookupContext(
            result.NextDirectoryResourceLookupCache);
        Dictionary<string, LR2SongDBExtended.chart_info_parse_failure> currentChartInfoParseFailures =
            parseTargetCount > 0 && dbGateway != null
                ? dbGateway.LoadCurrentChartInfoParseFailureMap(chartInfoBuildService.CurrentParseTimeout)
                : new Dictionary<string, LR2SongDBExtended.chart_info_parse_failure>(StringComparer.OrdinalIgnoreCase);
        using var commitContext = new FileDiffStreamingCommitContext(
            dbGateway,
            options,
            result,
            logInstallPerformance,
            logInstallPerformanceWarn,
            inlineChartInfoRowsCommitted);
        FileDiffParsePipelineResult pipelineResult = RunFileDiffParsePipeline(
            bmsParseTargets,
            addedOrUpdatedBmsonPaths,
            dbGateway,
            currentChartInfoParseFailures,
            result,
            parseTargetCount,
            ref parseProcessedCount,
            dialogService,
            logEverythingScan,
            reportParseProgress,
            logInstallPerformance,
            logInstallPerformanceWarn,
            inlineMaintenanceLookupContext,
            movedBmsSourcesByMd5,
            movedBmsUserColumnsByDeletedPath,
            commitContext);
        bmsLightweightParseMs = pipelineResult.BmsParseMs;
        result.NewFileParseMs = bmsLightweightParseMs;
        result.BmsParseMs = result.NewFileParseMs;
        result.BmsonParseMs = pipelineResult.BmsonParseMs;
        result.FileDiffReadMs = pipelineResult.ReadMs;
        result.FileDiffDigestMs = pipelineResult.DigestMs;
        result.FileDiffParseMs = pipelineResult.BmsParseMs + pipelineResult.BmsonParseMs;
        result.SnapshotQueueHighWatermark = pipelineResult.SnapshotQueueHighWatermark;
        result.InlineMaintenanceSharedResourceCacheEntries = inlineMaintenanceLookupContext.SharedResourceCacheEntryCount;
        result.InlineMaintenanceResourceSetCacheEntries = inlineMaintenanceLookupContext.ResourceHealthSetCacheEntryCount;
        result.AddedFiles.AddRange(pipelineResult.AddedFiles);
        foreach (string path in pipelineResult.NewlyInsertedBmsPaths)
        {
            result.NewlyInsertedBmsPaths.Add(path);
        }
        result.AddedBmsonSongs.AddRange(pipelineResult.ParsedBmsonSongs);
        result.BmsDateOnlyUpdateCount = pipelineResult.BmsDateOnlyUpdateCount;
        result.BmsTextOnlyUpdateCount = pipelineResult.BmsTextOnlyUpdateCount;
        result.BmsMovedHashRelinkCount = pipelineResult.BmsMovedHashRelinkCount;
        result.BmsMovedHashRelinkAmbiguousCount = pipelineResult.BmsMovedHashRelinkAmbiguousCount;
        foreach (string deletedPath in result.DeletedPaths)
        {
            commitContext.AddDeletedBmsPath(deletedPath);
        }
        foreach (string deletedBmsonPath in result.DeletedBmsonPaths)
        {
            commitContext.AddDeletedBmsonPath(deletedBmsonPath);
        }

        var stopwatchApply = Stopwatch.StartNew();
        var deletedPathSet = new HashSet<string>(result.DeletedPaths, StringComparer.OrdinalIgnoreCase);
        foreach (string updatedPath in pipelineResult.SuccessfullyReplacedBmsPaths)
        {
            deletedPathSet.Add(updatedPath);
        }
        result.NextFiles.AddRange(currentFileList.Where(file => !deletedPathSet.Contains(file.path)));
        result.NextFiles.AddRange(result.AddedFiles);
        var removedBmsonPaths = new HashSet<string>(result.DeletedBmsonPaths, StringComparer.OrdinalIgnoreCase);
        foreach (string updatedPath in pipelineResult.SuccessfullyParsedBmsonPaths)
        {
            removedBmsonPaths.Add(updatedPath);
        }
        List<LR2SongDBExtended.bmson_song> nextBmsonSongs = [.. currentBmsonList.Where(song => song != null && !removedBmsonPaths.Contains(song.path))];
        nextBmsonSongs.AddRange(result.AddedBmsonSongs);
        var directoryKeys = new HashSet<string>(result.NextDirectoryResourceLookupCache?.Keys ?? [], StringComparer.OrdinalIgnoreCase);
        var stopwatchInstlDstCleanup = Stopwatch.StartNew();
        int clearedInstallDestinationCountBefore = result.MutationDelta.UpdatedInstallDestinations.Count;
        var nextFileOwners = new HashSet<BMSFile>(result.NextFiles.Where(file => file != null));
        var nextFilePaths = new HashSet<string>(
            result.NextFiles.Select(file => file?.path).Where(path => !string.IsNullOrWhiteSpace(path)),
            StringComparer.OrdinalIgnoreCase);
        var nextBmsonOwners = new HashSet<LR2SongDBExtended.bmson_song>(nextBmsonSongs.Where(song => song != null));
        var nextBmsonPaths = new HashSet<string>(
            nextBmsonSongs.Select(song => song?.path).Where(path => !string.IsNullOrWhiteSpace(path)),
            StringComparer.OrdinalIgnoreCase);
        IEnumerable<ChartFile> installDestinationCleanupCharts = currentInstallDestinationCharts
            ?? [];
        foreach (ChartFile chart in installDestinationCleanupCharts
            .Where(IsCurrentChartOwner)
            .Where(chart => !string.IsNullOrWhiteSpace(chart.InstallDestination)))
        {
            if (!directoryKeys.Contains(chart.InstallDestination))
            {
                result.MutationDelta.UpdatedInstallDestinations.Add(new LibraryInstallDestinationChange
                {
                    Chart = chart,
                    NewInstallDestination = null,
                    ClearInstallDestinationState = true
                });
            }
        }
        if (result.MutationDelta.UpdatedInstallDestinations.Count > clearedInstallDestinationCountBefore)
        {
            result.MutationDelta.InvalidateInstalledDirectoryIndex = true;
            result.MutationDelta.ClearDuplicatedCache = true;
        }
        stopwatchInstlDstCleanup.Stop();
        result.InstlDstCleanupMs = stopwatchInstlDstCleanup.ElapsedMilliseconds;
        stopwatchApply.Stop();
        result.ApplyMs = stopwatchApply.ElapsedMilliseconds;

        result.NextBmsonSongs.Clear();
        result.NextBmsonSongs.AddRange(nextBmsonSongs);
        logEverythingScan?.Invoke("bmson_scan totalPaths=" + scannedBmsonPaths.Count + " deleted=" + result.DeletedBmsonPaths.Count + " upserted=" + result.AddedBmsonSongs.Count);
        result.DirectoryCount = result.NextDirectoryResourceLookupCache?.Count ?? 0;

        result.HasDbDiff = result.DeletedPaths.Count > 0
            || result.AddedFiles.Count > 0
            || result.BmsDateOnlyUpdateCount > 0
            || result.BmsTextOnlyUpdateCount > 0
            || result.DeletedBmsonPaths.Count > 0
            || result.AddedBmsonSongs.Count > 0;

        if (result.HasDbDiff)
        {
            commitContext.Flush();
            commitContext.RestoreSongUserColumns(pipelineResult.BmsMovedHashRelinkUserColumnRestores);
            if (inlineChartInfoRowsCommitted != null)
            {
                result.InlineChartInfoRows.Clear();
                result.InlineChartInfoAppliedRows.Clear();
                result.InlineChartInfoParseFailureRows.Clear();
                result.InlineChartInfoParseFailureDeleteMd5s.Clear();
            }
        }
        normalFolderMtimeSnapshot ??= normalFolderMtimeSnapshotProvider?.Invoke();
        Lr2NormalFolderMtimeDiffResult normalFolderMtimeDiff = CreateChangedNormalFolderDirectoryPaths(
            dbGateway,
            options,
            lr2NormalFolderSyncRootDirectories,
            result.Lr2ScanNormalFolderDirectoryPaths,
            result.DeletedPaths,
            result.Lr2ScanNormalFolderDirectoryEntries,
            normalFolderMtimeSnapshot);
        if (normalFolderMtimeDiff != null)
        {
            logInstallPerformance?.Invoke("lr2_normal_folder_mtime_diff"
                + " roots=" + normalFolderMtimeDiff.RootCount
                + " directories=" + normalFolderMtimeDiff.DirectoryCount
                + " existingRows=" + normalFolderMtimeDiff.ExistingRowCount
                + " prefetched=" + normalFolderMtimeDiff.PrefetchedSnapshotUsed.ToString().ToLowerInvariant()
                + " changed=" + normalFolderMtimeDiff.ChangedDirectoryPaths.Count
                + " pruneScopes=" + normalFolderMtimeDiff.PruneScopeDirectoryPaths.Count
                + " missingRows=" + normalFolderMtimeDiff.MissingRowCount
                + " missingMetadata=" + normalFolderMtimeDiff.MissingMetadataCount
                + " candidateBuildMs=" + normalFolderMtimeDiff.CandidateBuildMs
                + " existingMapMs=" + normalFolderMtimeDiff.ExistingMapMs
                + " compareMs=" + normalFolderMtimeDiff.CompareMs
                + " elapsedMs=" + normalFolderMtimeDiff.ElapsedMs);
        }
        SyncLr2NormalFoldersIfEnabled(
            dbGateway,
            options,
            lr2NormalFolderSyncRootDirectories,
            Lr2NormalFolderSyncScopeBuilder.CreateForFileDiff(
                lr2NormalFolderSyncRootDirectories,
                scannedPaths,
                normalFolderMtimeDiff?.ChangedDirectoryPaths,
                normalFolderMtimeDiff?.PruneScopeDirectoryPaths),
            result.Lr2ScanNormalFolderDirectoryEntries,
            mergedScanResult.FolderInfoFilePaths,
            mergedScanResult.FolderInfoFileEntriesByPath,
            result,
            logInstallPerformance,
            logInstallPerformanceWarn,
            bmsFileScanSucceeded);

        bool IsCurrentChartOwner(ChartFile chart)
        {
            BMSFile bmsOwner = chart?.GetBmsStorageOwner();
            if (bmsOwner != null)
            {
                return nextFileOwners.Contains(bmsOwner)
                    || (!string.IsNullOrWhiteSpace(chart.Path) && nextFilePaths.Contains(chart.Path));
            }

            LR2SongDBExtended.bmson_song bmsonOwner = chart?.GetBmsonStorageOwner();
            return (bmsonOwner != null && nextBmsonOwners.Contains(bmsonOwner))
                || (!string.IsNullOrWhiteSpace(chart?.Path) && nextBmsonPaths.Contains(chart.Path));
        }

        logInstallPerformance?.Invoke(
            "song_tbl_file_check_breakdown scan_ms=" + result.ScanElapsedMs
            + " native_bridge_ms=" + result.NativeBridgeMs
            + " native_bridge_reason=" + (string.IsNullOrWhiteSpace(result.NativeBridgeReason) ? string.Empty : result.NativeBridgeReason)
            + " managed_decode_ms=" + result.ManagedDecodeMs
            + " managed_materialize_ms=" + result.ManagedMaterializeMs
            + " bridge_raw_buffer_bytes=" + result.BridgeRawBufferBytes
            + " resource_index_build_ms=" + result.ResourceIndexBuildMs
            + " resource_index_lookup_ms=" + result.ResourceLookupCacheMs
            + " lazy_hash_cache_entries=" + (result.NextDirectoryResourceLookupCache?.LazyHashCacheEntryCount ?? 0)
            + " lazy_hash_build_ms=" + (result.NextDirectoryResourceLookupCache?.LazyHashBuildMs ?? 0L)
            + " lazy_hash_lookup_count=" + (result.NextDirectoryResourceLookupCache?.LazyHashLookupCount ?? 0L)
            + " diff_ms=" + result.DiffMs
            + " diff_current_index_ms=" + result.DiffCurrentIndexMs
            + " diff_scanned_split_ms=" + result.DiffScannedSplitMs
            + " diff_deleted_ms=" + result.DiffDeletedMs
            + " diff_bms_target_ms=" + result.DiffBmsTargetMs
            + " diff_bmson_target_ms=" + result.DiffBmsonTargetMs
            + " deleted_count=" + result.DeletedPaths.Count
            + " added_count=" + result.AddedFiles.Count
            + " bms_date_only_update_count=" + result.BmsDateOnlyUpdateCount
            + " bms_text_only_update_count=" + result.BmsTextOnlyUpdateCount
            + " bms_moved_hash_relink_count=" + result.BmsMovedHashRelinkCount
            + " bms_moved_hash_relink_ambiguous_count=" + result.BmsMovedHashRelinkAmbiguousCount
            + " bms_added_target_count=" + result.BmsAddedTargetCount
            + " bms_new_insert_path_count=" + result.NewlyInsertedBmsPaths.Count
            + " bms_legacy_existing_protected_count=" + result.BmsLegacyExistingProtectedCount
            + " bmson_deleted_count=" + result.DeletedBmsonPaths.Count
            + " bmson_upsert_count=" + result.AddedBmsonSongs.Count
            + " bmson_upsert_target_count=" + result.BmsonUpsertTargetCount
            + " bms_mtime_fallback_count=" + result.BmsMtimeFallbackCount
            + " bmson_mtime_fallback_count=" + result.BmsonMtimeFallbackCount
            + " file_diff_reader_degree=" + result.FileDiffReaderDegree
            + " file_diff_parser_degree=" + result.FileDiffParserDegree
            + " file_diff_post_parse_worker_degree=" + result.FileDiffPostParseWorkerDegree
            + " read_queue_capacity=" + result.ReadQueueCapacity
            + " parsed_queue_capacity=" + result.ParsedQueueCapacity
            + " post_parse_queue_capacity=" + result.PostParseQueueCapacity
            + " post_parse_result_queue_capacity=" + result.PostParseResultQueueCapacity
            + " post_parse_batch_size=" + result.PostParseBatchSize
            + " commit_queue_capacity=" + result.CommitQueueCapacity
            + " commit_writer_queue_capacity=" + result.CommitWriterQueueCapacity
            + " commit_writer_queue_high_watermark=" + result.CommitWriterQueueHighWatermark
            + " commit_streaming_enabled=" + result.CommitStreamingEnabled.ToString().ToLowerInvariant()
            + " commit_streaming_barrier=" + (result.CommitStreamingBarrierReason ?? string.Empty)
            + " reader_output_wait_ms=" + result.ReaderOutputWaitMs
            + " parser_output_wait_ms=" + result.ParserOutputWaitMs
            + " post_parse_queue_wait_ms=" + result.PostParseQueueWaitMs
            + " post_parse_output_wait_ms=" + result.PostParseOutputWaitMs
            + " commit_queue_wait_ms=" + result.CommitQueueWaitMs
            + " commit_writer_queue_wait_ms=" + result.CommitWriterQueueWaitMs
            + " db_commit_first_chunk_start_ms=" + result.DbCommitFirstChunkStartMs
            + " post_parse_batch_count=" + result.PostParseBatchCount
            + " post_parse_wall_ms=" + result.PostParseWallMs
            + " post_parse_max_batch_ms=" + result.PostParseMaxBatchMs
            + " newfile_parse_ms=" + result.NewFileParseMs
            + " bms_parse_ms=" + result.BmsParseMs
            + " bmson_parse_ms=" + result.BmsonParseMs
            + " file_diff_read_ms=" + result.FileDiffReadMs
            + " file_diff_digest_ms=" + result.FileDiffDigestMs
            + " file_diff_parse_ms=" + result.FileDiffParseMs
            + " snapshot_queue_high_watermark=" + result.SnapshotQueueHighWatermark
            + " inline_chart_info_target_count=" + result.InlineChartInfoTargetCount
            + " inline_chart_info_success_count=" + result.InlineChartInfoSuccessCount
            + " inline_chart_info_current_skipped_count=" + result.InlineChartInfoCurrentSkippedCount
            + " inline_chart_info_failure_skipped_count=" + result.InlineChartInfoFailureSkippedCount
            + " inline_chart_info_parse_failed_count=" + result.InlineChartInfoParseFailedCount
            + " inline_chart_info_failure_persisted_count=" + result.InlineChartInfoFailurePersistedCount
            + " inline_chart_info_failure_cleared_count=" + result.InlineChartInfoFailureClearedCount
            + " inline_chart_info_index_published_count=" + result.InlineChartInfoIndexPublishedCount
            + " inline_chart_info_parse_ms=" + result.InlineChartInfoParseMs
            + " inline_chart_info_wall_ms=" + result.InlineChartInfoWallMs
            + " inline_chart_info_batch_size=" + result.InlineChartInfoBatchSize
            + " inline_maintenance_degree=" + result.InlineMaintenanceDegree
            + " inline_maintenance_target_count=" + result.InlineMaintenanceTargetCount
            + " inline_maintenance_success_count=" + result.InlineMaintenanceSuccessCount
            + " inline_maintenance_failed_count=" + result.InlineMaintenanceFailedCount
            + " inline_maintenance_bms_count=" + result.InlineMaintenanceBmsCount
            + " inline_maintenance_bmson_count=" + result.InlineMaintenanceBmsonCount
            + " inline_maintenance_ms=" + result.InlineMaintenanceMs
            + " inline_maintenance_wall_ms=" + result.InlineMaintenanceWallMs
            + " inline_health_wall_ms=" + result.InlineHealthWallMs
            + " inline_encoding_wall_ms=" + result.InlineEncodingWallMs
            + " inline_encoding_reload_wall_ms=" + result.InlineEncodingReloadWallMs
            + " inline_encoding_reload_count=" + result.InlineEncodingReloadCount
            + " inline_encoding_detect_count=" + result.InlineEncodingDetectCount
            + " inline_encoding_fast_ascii_count=" + result.InlineEncodingFastAsciiCount
            + " inline_encoding_shift_jis_count=" + result.InlineEncodingShiftJisCount
            + " inline_encoding_shift_jis_question_count=" + result.InlineEncodingShiftJisQuestionCount
            + " inline_encoding_ks_c_5601_count=" + result.InlineEncodingKoreanCount
            + " inline_encoding_ks_c_5601_question_count=" + result.InlineEncodingKoreanQuestionCount
            + " inline_encoding_utf8_count=" + result.InlineEncodingUtf8Count
            + " inline_encoding_unknown_count=" + result.InlineEncodingUnknownCount
            + " inline_encoding_other_count=" + result.InlineEncodingOtherCount
            + " inline_encoding_max_item_ms=" + result.InlineEncodingMaxItemMs
            + " inline_bms_maintenance_wall_ms=" + result.InlineBmsMaintenanceWallMs
            + " inline_bmson_maintenance_wall_ms=" + result.InlineBmsonMaintenanceWallMs
            + " inline_maintenance_cache_hit=" + result.InlineMaintenanceCacheHitCount
            + " inline_maintenance_resource_index_hit=" + result.InlineMaintenanceResourceIndexHitCount
            + " inline_maintenance_resource_set_cache_hit=" + result.InlineMaintenanceResourceSetCacheHitCount
            + " inline_maintenance_file_exists_fallback=" + result.InlineMaintenanceFileExistsFallbackCount
            + " inline_maintenance_shared_resource_cache_entries=" + result.InlineMaintenanceSharedResourceCacheEntries
            + " inline_maintenance_resource_set_cache_entries=" + result.InlineMaintenanceResourceSetCacheEntries
            + " parse_read_bytes_estimate=" + result.ParseReadBytesEstimate
            + " apply_ms=" + result.ApplyMs
            + " db_commit_ms=" + result.DbCommitMs
            + " db_commit_apply_ms=" + result.DbCommitApplyMs
            + " db_commit_schema_ms=" + result.DbCommitSchemaMs
            + " db_commit_bms_delete_ms=" + result.DbCommitBmsDeleteMs
            + " db_commit_bms_date_update_ms=" + result.DbCommitBmsDateUpdateMs
            + " db_commit_bms_upsert_ms=" + result.DbCommitBmsUpsertMs
            + " db_commit_bms_changed=" + result.DbCommitBmsChangedCount
            + " db_commit_bmson_delete_ms=" + result.DbCommitBmsonDeleteMs
            + " db_commit_bmson_upsert_ms=" + result.DbCommitBmsonUpsertMs
            + " db_commit_maintenance_upsert_ms=" + result.DbCommitMaintenanceUpsertMs
            + " db_commit_chart_info_ms=" + result.DbCommitChartInfoMs
            + " db_commit_sqlite_commit_ms=" + result.DbCommitSqliteCommitMs
            + " db_commit_chunks=" + result.DbCommitChunks
            + " db_commit_chunk_size=" + result.DbCommitChunkSize
            + " db_commit_max_chunk_ms=" + result.DbCommitMaxChunkMs
            + " instl_dst_cleanup_ms=" + result.InstlDstCleanupMs
            + " lr2_normal_folder_sync_executed=" + result.Lr2NormalFolderSyncExecuted.ToString().ToLowerInvariant()
            + " lr2_normal_folder_sync_failed=" + result.Lr2NormalFolderSyncFailed.ToString().ToLowerInvariant()
            + " lr2_normal_folder_generated=" + result.Lr2NormalFolderGeneratedCount
            + " lr2_normal_folder_upserted=" + result.Lr2NormalFolderUpsertedCount
            + " lr2_normal_folder_deleted=" + result.Lr2NormalFolderDeletedCount
            + " lr2_normal_folder_skipped_unsupported=" + result.Lr2NormalFolderSkippedUnsupportedPathCount
            + " lr2_normal_folder_skipped_missing_metadata=" + result.Lr2NormalFolderSkippedMissingMetadataCount
            + " lr2_normal_folder_skipped_incompatible_chart=" + result.Lr2NormalFolderSkippedIncompatibleChartPathCount
            + " lr2_normal_folder_metadata_requested=" + result.Lr2NormalFolderMetadataRequestedDirectoryCount
            + " lr2_normal_folder_metadata_resolved=" + result.Lr2NormalFolderMetadataResolvedDirectoryCount
            + " lr2_normal_folderinfo_candidates=" + result.Lr2NormalFolderInfoCandidateCount
            + " lr2_normal_folderinfo_applied=" + result.Lr2NormalFolderInfoAppliedCount
            + " lr2_normal_folderinfo_read_failures=" + result.Lr2NormalFolderInfoReadFailureCount
            + " lr2_normal_folder_sync_ms=" + result.Lr2NormalFolderSyncMs);
        logInstallPerformance?.Invoke(
            "song_tbl_file_check_cache_counts chartDirs=" + result.DirectoryCount
            + " audioResourceKeyEntries=" + result.AudioResourceKeyHashEntryCount
            + " imageResourceKeyEntries=" + result.ImageResourceKeyHashEntryCount
            + " movieResourceKeyEntries=" + result.MovieResourceKeyHashEntryCount);
        logEverythingScan?.Invoke("bms_scan totalMs=" + result.ScanElapsedMs
            + " nativeBridgeMs=" + result.NativeBridgeMs
            + " managedDecodeMs=" + result.ManagedDecodeMs
            + " managedMaterializeMs=" + result.ManagedMaterializeMs
            + " resourceIndexBuildMs=" + result.ResourceIndexBuildMs
            + " resourceIndexLookupMs=" + result.ResourceLookupCacheMs
            + " bridgeReason=" + (string.IsNullOrWhiteSpace(result.NativeBridgeReason) ? string.Empty : result.NativeBridgeReason)
            + " bmsPaths=" + result.BmsPathCount
            + " dirs=" + result.DirectoryCount
            + " prefetched=" + result.PrefetchedScanUsed.ToString().ToLowerInvariant());
        return result;
    }

    private static void ApplyLr2FolderScanSurface(
        SongTableFileCheckResult result,
        BmsLibraryOptionsSnapshot options,
        IEnumerable<string> rootDirectories,
        Lr2BuiltinCustomFolderSettings builtinCustomFolderSettings,
        Action<string> logEverythingScan)
    {
        if (result == null
            || options?.OperationModeLR2DB != true
            || options.EnableLR2SongDbFullGeneration != true)
        {
            return;
        }

        List<string> lr2FolderDiscoveryDirectories = Lr2FolderFileDiscoveryService.CreateDiscoveryDirectories(
            rootDirectories,
            options.LR2CustomFolderOutputBaseDir,
            options.LR2CustomFolderOutputBaseDirRootType,
            Lr2FolderFileDiscoveryService.CreateBuiltinFolderSourceDirectories(options.LR2RootPath));
        Lr2FolderFileCandidateSnapshot candidates = Lr2FolderFileDiscoveryService.CreateFileCandidates(
            lr2FolderDiscoveryDirectories,
            options.LR2RootPath,
            builtinCustomFolderSettings ?? new Lr2BuiltinCustomFolderSettings(0, 24, false),
            logEverythingScan);

        result.Lr2ScanLr2FolderDiscoveryDirectories = lr2FolderDiscoveryDirectories;
        result.Lr2ScanLr2FolderFilePaths = candidates.Paths;
        result.Lr2ScanLr2FolderFileEntries = candidates.EntriesByPath;
        result.Lr2ScanLr2FolderFileDiscoveryComplete = candidates.DiscoveryComplete;
    }

    private static void SyncLr2NormalFoldersIfEnabled(
        BmsLibraryDbGateway dbGateway,
        BmsLibraryOptionsSnapshot options,
        IEnumerable<string> rootDirectories,
        Lr2NormalFolderSyncScope syncInput,
        IReadOnlyDictionary<string, RootFileEnumerationEntry> directoryEntrySurface,
        IEnumerable<string> folderInfoFilePaths,
        IReadOnlyDictionary<string, RootFileEnumerationEntry> folderInfoFileEntries,
        SongTableFileCheckResult result,
        Action<string> logInstallPerformance,
        Action<string> logInstallPerformanceWarn,
        bool scanCompletedSuccessfully)
    {
        if (dbGateway == null
            || result == null
            || options?.OperationModeLR2DB != true
            || options.EnableLR2SongDbFullGeneration != true)
        {
            return;
        }

        List<string> roots = [.. (rootDirectories ?? [])
            .Where(path => !string.IsNullOrWhiteSpace(path))
            .Distinct(StringComparer.OrdinalIgnoreCase)];
        if (roots.Count == 0)
        {
            return;
        }
        if (!scanCompletedSuccessfully)
        {
            logInstallPerformance?.Invoke("lr2_normal_folder_sync skipped reason=incomplete_scan");
            return;
        }
        if (syncInput == null
            || (!result.HasDbDiff && syncInput.DirectoryPaths.Count == 0))
        {
            logInstallPerformance?.Invoke("lr2_normal_folder_sync skipped reason=no_db_diff");
            return;
        }
        if (syncInput.ChartPaths.Count == 0
                && syncInput.DirectoryPaths.Count == 0
                && syncInput.PruneScopeDirectories.Count == 0
                && syncInput.PruneExactDirectories.Count == 0)
        {
            logInstallPerformance?.Invoke("lr2_normal_folder_sync skipped reason=no_normal_folder_mtime_diff");
            return;
        }

        result.Lr2NormalFolderSyncExecuted = true;
        try
        {
            using LR2SongDBExtended songDb = dbGateway.OpenSongDb();
            IReadOnlyCollection<string> directoryMetadataTargets = [.. Lr2NormalFolderDbSyncService.CreateDirectoryMetadataTargets(roots, syncInput.ChartPaths)
                .Concat(Lr2NormalFolderDbSyncService.CreateDirectoryMetadataTargetsFromDirectories(roots, syncInput.DirectoryPaths))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .OrderBy(path => path, StringComparer.OrdinalIgnoreCase)];
            IReadOnlyDictionary<string, RootFileEnumerationEntry> directoryEntries = Lr2FolderDirectoryEnumerationService.CreateEntriesFromSurface(
                directoryEntrySurface,
                directoryMetadataTargets);
            Lr2NormalFolderDbSyncResult syncResult = Lr2NormalFolderDbSyncService.Sync(songDb, new Lr2NormalFolderDbSyncRequest
            {
                RootDirectories = roots,
                ChartPaths = syncInput.ChartPaths,
                DirectoryPaths = directoryMetadataTargets,
                FolderInfoFilePaths = [.. (folderInfoFilePaths ?? [])],
                FolderInfoFileEntries = folderInfoFileEntries ?? new Dictionary<string, RootFileEnumerationEntry>(StringComparer.OrdinalIgnoreCase),
                DirectoryLastWriteTimeUtcResolver = CreateLastWriteTimeResolver(directoryEntries),
                PruneScopeDirectories = syncInput.PruneScopeDirectories,
                PruneExactDirectories = syncInput.PruneExactDirectories,
                AllowPrune = syncInput.PruneScopeDirectories.Count > 0 || syncInput.PruneExactDirectories.Count > 0,
                UseScopedExistingRows = true
            });
            ApplyLr2NormalFolderSyncResult(result, syncResult);
            logInstallPerformance?.Invoke("lr2_normal_folder_sync done generated=" + syncResult.GeneratedCount
                + " upserted=" + syncResult.UpsertedCount
                + " deleted=" + syncResult.DeletedCount
                + " paths=" + syncInput.ChartPaths.Count
                + " directoryPaths=" + syncInput.DirectoryPaths.Count
                + " pruneScopes=" + syncInput.PruneScopeDirectories.Count
                + " exactPrunes=" + syncInput.PruneExactDirectories.Count
                + " skippedUnsupported=" + syncResult.SkippedUnsupportedPathCount
                + " skippedMissingMetadata=" + syncResult.SkippedMissingMetadataCount
                + " skippedIncompatibleChart=" + syncResult.SkippedIncompatibleChartPathCount
                + " metadataRequested=" + syncResult.MetadataRequestedDirectoryCount
                + " metadataResolved=" + syncResult.MetadataResolvedDirectoryCount
                + " folderInfoCandidates=" + syncResult.FolderInfoCandidateCount
                + " folderInfoApplied=" + syncResult.FolderInfoAppliedCount
                + " folderInfoReadFailures=" + syncResult.FolderInfoReadFailureCount
                + " targetBuildMs=" + syncResult.TargetBuildMs
                + " metadataBuildMs=" + syncResult.MetadataBuildMs
                + " existingReadMs=" + syncResult.ExistingReadMs
                + " rowGenerateMs=" + syncResult.RowGenerateMs
                + " planMs=" + syncResult.PlanMs
                + " writeMs=" + syncResult.WriteMs
                + " elapsedMs=" + syncResult.ElapsedMs);
        }
        catch (Exception ex)
        {
            result.Lr2NormalFolderSyncFailed = true;
            result.Lr2NormalFolderSyncFailureReason = ex.Message ?? ex.GetType().Name;
            logInstallPerformanceWarn?.Invoke("lr2_normal_folder_sync failed reason=" + QuoteLogValue(result.Lr2NormalFolderSyncFailureReason));
        }
    }

    private static Lr2NormalFolderMtimeDiffResult CreateChangedNormalFolderDirectoryPaths(
        BmsLibraryDbGateway dbGateway,
        BmsLibraryOptionsSnapshot options,
        IEnumerable<string> rootDirectories,
        IEnumerable<string> currentNormalFolderDirectoryPaths,
        IEnumerable<string> deletedBmsPaths,
        IReadOnlyDictionary<string, RootFileEnumerationEntry> currentDirectoryEntries,
        Lr2NormalFolderMtimeSnapshot prefetchedSnapshot = null)
    {
        if (dbGateway == null
            || options?.OperationModeLR2DB != true
            || options.EnableLR2SongDbFullGeneration != true)
        {
            return null;
        }

        var stopwatch = Stopwatch.StartNew();
        List<string> roots = NormalizeNormalFolderMtimeRoots(rootDirectories);
        var stopwatchCandidates = Stopwatch.StartNew();
        List<Lr2NormalFolderMtimeCandidate> directoryCandidates = CreateNormalFolderMtimeCandidates(
            currentNormalFolderDirectoryPaths,
            roots,
            currentDirectoryEntries,
            pathsAlreadyNormalized: true);
        List<Lr2NormalFolderMtimeCandidate> deletedParentCandidates = CreateNormalFolderMtimeCandidates(
            (deletedBmsPaths ?? []).Select(path => Lr2FolderPath.SafeGetDirectoryName(path)),
            roots,
            currentDirectoryEntries,
            pathsAlreadyNormalized: false);
        stopwatchCandidates.Stop();
        if (roots.Count == 0 || (directoryCandidates.Count == 0 && deletedParentCandidates.Count == 0))
        {
            stopwatch.Stop();
            return new Lr2NormalFolderMtimeDiffResult(
                roots.Count,
                directoryCandidates.Count,
                existingRowCount: 0,
                missingRowCount: 0,
                missingMetadataCount: 0,
                prefetchedSnapshotUsed: false,
                candidateBuildMs: stopwatchCandidates.ElapsedMilliseconds,
                existingMapMs: 0L,
                compareMs: 0L,
                changedDirectoryPaths: [],
                pruneScopeDirectoryPaths: [],
                stopwatch.ElapsedMilliseconds);
        }

        IReadOnlyCollection<string> exactPaths = [.. directoryCandidates
            .Concat(deletedParentCandidates)
            .Select(candidate => candidate.FolderPath)
            .Where(path => !string.IsNullOrWhiteSpace(path))
            .Distinct(StringComparer.Ordinal)];
        bool prefetchedSnapshotUsed = CanUseNormalFolderMtimeSnapshot(prefetchedSnapshot, roots);
        var stopwatchExistingMap = Stopwatch.StartNew();
        Dictionary<string, LR2SongDB.folder> existingRowsByPath = prefetchedSnapshotUsed
            ? CreateExistingNormalFolderRowMap(prefetchedSnapshot, exactPaths)
            : CreateExistingNormalFolderRowMapFromDb(dbGateway, exactPaths);
        stopwatchExistingMap.Stop();

        var changed = new List<string>();
        var pruneScopes = new List<string>();
        int missingRowCount = 0;
        int missingMetadataCount = 0;
        var stopwatchCompare = Stopwatch.StartNew();
        foreach (Lr2NormalFolderMtimeCandidate candidate in directoryCandidates)
        {
            Lr2NormalFolderDirectoryChangeState changeState = ResolveNormalFolderDirectoryChangeState(
                existingRowsByPath,
                candidate);
            if (changeState == Lr2NormalFolderDirectoryChangeState.MissingRow)
            {
                missingRowCount++;
                changed.Add(candidate.DirectoryPath);
                continue;
            }
            if (changeState == Lr2NormalFolderDirectoryChangeState.MissingMetadata)
            {
                missingMetadataCount++;
                changed.Add(candidate.DirectoryPath);
                continue;
            }
            if (changeState == Lr2NormalFolderDirectoryChangeState.Changed)
            {
                changed.Add(candidate.DirectoryPath);
            }
        }
        foreach (Lr2NormalFolderMtimeCandidate candidate in deletedParentCandidates)
        {
            if (roots.Any(root => string.Equals(candidate.DirectoryPath, root, StringComparison.OrdinalIgnoreCase)))
            {
                continue;
            }
            Lr2NormalFolderDirectoryChangeState changeState = ResolveNormalFolderDirectoryChangeState(
                existingRowsByPath,
                candidate);
            if (changeState == Lr2NormalFolderDirectoryChangeState.MissingRow
                || changeState == Lr2NormalFolderDirectoryChangeState.MissingMetadata
                || changeState == Lr2NormalFolderDirectoryChangeState.Changed)
            {
                pruneScopes.Add(candidate.DirectoryPath);
            }
            if (changeState == Lr2NormalFolderDirectoryChangeState.MissingRow)
            {
                missingRowCount++;
            }
            else if (changeState == Lr2NormalFolderDirectoryChangeState.MissingMetadata)
            {
                missingMetadataCount++;
            }
        }
        stopwatchCompare.Stop();

        stopwatch.Stop();
        return new Lr2NormalFolderMtimeDiffResult(
            roots.Count,
            directoryCandidates.Count,
            existingRowsByPath.Count,
            missingRowCount,
            missingMetadataCount,
            prefetchedSnapshotUsed,
            stopwatchCandidates.ElapsedMilliseconds,
            stopwatchExistingMap.ElapsedMilliseconds,
            stopwatchCompare.ElapsedMilliseconds,
            [.. changed.Distinct(StringComparer.OrdinalIgnoreCase).OrderBy(path => path, StringComparer.OrdinalIgnoreCase)],
            [.. pruneScopes.Distinct(StringComparer.OrdinalIgnoreCase).OrderBy(path => path, StringComparer.OrdinalIgnoreCase)],
            stopwatch.ElapsedMilliseconds);
    }

    private static List<Lr2NormalFolderMtimeCandidate> CreateNormalFolderMtimeCandidates(
        IEnumerable<string> directoryPaths,
        IReadOnlyList<string> roots,
        IReadOnlyDictionary<string, RootFileEnumerationEntry> currentDirectoryEntries,
        bool pathsAlreadyNormalized)
    {
        var result = new List<Lr2NormalFolderMtimeCandidate>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (string directoryPath in directoryPaths ?? [])
        {
            string directory = pathsAlreadyNormalized
                ? directoryPath
                : Lr2FolderPath.NormalizeDirectoryPath(directoryPath);
            if (string.IsNullOrWhiteSpace(directory)
                || (roots != null && roots.Count > 0 && !IsSameOrDescendantOfAnyNormalizedRoot(directory, roots))
                || !seen.Add(directory))
            {
                continue;
            }

            string folderPath = Lr2FolderPath.ToFolderPathFromNormalizedDirectory(directory);
            if (string.IsNullOrWhiteSpace(folderPath))
            {
                continue;
            }

            int? currentDate = currentDirectoryEntries != null
                && currentDirectoryEntries.TryGetValue(directory, out RootFileEnumerationEntry entry)
                && entry.LastWriteTimeUtc.HasValue
                    ? Lr2SongRowEnricher.ToLr2UnixSeconds(entry.LastWriteTimeUtc.Value)
                    : null;
            result.Add(new Lr2NormalFolderMtimeCandidate(directory, folderPath, currentDate));
        }
        return result;
    }

    private static bool IsSameOrDescendantOfAnyNormalizedRoot(string directory, IReadOnlyList<string> roots)
    {
        foreach (string root in roots ?? [])
        {
            if (Lr2FolderPath.IsSameOrDescendantNormalized(directory, root))
            {
                return true;
            }
        }
        return false;
    }

    private static List<string> NormalizeNormalFolderMtimeRoots(IEnumerable<string> rootDirectories)
    {
        return [.. (rootDirectories ?? [])
            .Select(Lr2FolderPath.NormalizeDirectoryPath)
            .Where(path => !string.IsNullOrWhiteSpace(path))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(path => path, StringComparer.OrdinalIgnoreCase)];
    }

    private static Dictionary<string, LR2SongDB.folder> CreateExistingNormalFolderRowMapFromDb(
        BmsLibraryDbGateway dbGateway,
        IReadOnlyCollection<string> exactPaths)
    {
        using LR2SongDBExtended songDb = dbGateway.OpenSongDb();
        return CreateExistingNormalFolderRowMap(
            Lr2FolderExistingRowLookup.QueryExactPathsForNormalFolderMtime(songDb, exactPaths));
    }

    private static bool CanUseNormalFolderMtimeSnapshot(
        Lr2NormalFolderMtimeSnapshot snapshot,
        IReadOnlyCollection<string> roots)
    {
        if (snapshot?.ExistingRowsByPath == null || roots == null || roots.Count == 0)
        {
            return false;
        }

        return roots.All(root => snapshot.RootDirectories.Any(snapshotRoot =>
            string.Equals(root, snapshotRoot, StringComparison.OrdinalIgnoreCase)));
    }

    private static Dictionary<string, LR2SongDB.folder> CreateExistingNormalFolderRowMap(
        Lr2NormalFolderMtimeSnapshot snapshot,
        IEnumerable<string> exactPaths)
    {
        var result = new Dictionary<string, LR2SongDB.folder>(StringComparer.OrdinalIgnoreCase);
        if (snapshot?.ExistingRowsByPath == null)
        {
            return result;
        }

        foreach (string exactPath in exactPaths ?? [])
        {
            string key = !string.IsNullOrWhiteSpace(exactPath)
                && Lr2FolderPath.IsDirectorySeparator(exactPath[exactPath.Length - 1])
                    ? exactPath
                    : Lr2FolderPath.ToFolderPath(exactPath);
            if (!string.IsNullOrWhiteSpace(key)
                && snapshot.ExistingRowsByPath.TryGetValue(key, out LR2SongDB.folder row)
                && row != null
                && !result.ContainsKey(key))
            {
                result[key] = row;
            }
        }
        return result;
    }

    private static Lr2NormalFolderDirectoryChangeState ResolveNormalFolderDirectoryChangeState(
        IReadOnlyDictionary<string, LR2SongDB.folder> existingRowsByPath,
        Lr2NormalFolderMtimeCandidate candidate)
    {
        if (string.IsNullOrWhiteSpace(candidate.FolderPath))
        {
            return Lr2NormalFolderDirectoryChangeState.Unchanged;
        }
        if (existingRowsByPath == null
            || !existingRowsByPath.TryGetValue(candidate.FolderPath, out LR2SongDB.folder existing))
        {
            return Lr2NormalFolderDirectoryChangeState.MissingRow;
        }
        if (!candidate.CurrentDate.HasValue)
        {
            return Lr2NormalFolderDirectoryChangeState.MissingMetadata;
        }
        return existing.date == candidate.CurrentDate.Value
            ? Lr2NormalFolderDirectoryChangeState.Unchanged
            : Lr2NormalFolderDirectoryChangeState.Changed;
    }

    private static Dictionary<string, LR2SongDB.folder> CreateExistingNormalFolderRowMap(IEnumerable<LR2SongDB.folder> rows)
    {
        var result = new Dictionary<string, LR2SongDB.folder>(StringComparer.OrdinalIgnoreCase);
        foreach (LR2SongDB.folder row in rows ?? [])
        {
            if (row?.type != 1)
            {
                continue;
            }
            string path = Lr2FolderPath.ToFolderPath(row.path);
            if (!string.IsNullOrWhiteSpace(path) && !result.ContainsKey(path))
            {
                result[path] = row;
            }
        }
        return result;
    }

    private readonly struct Lr2NormalFolderMtimeCandidate(
        string directoryPath,
        string folderPath,
        int? currentDate)
    {
        public string DirectoryPath { get; } = directoryPath;

        public string FolderPath { get; } = folderPath;

        public int? CurrentDate { get; } = currentDate;
    }

    private sealed class Lr2NormalFolderMtimeDiffResult(
        int rootCount,
        int directoryCount,
        int existingRowCount,
        int missingRowCount,
        int missingMetadataCount,
        bool prefetchedSnapshotUsed,
        long candidateBuildMs,
        long existingMapMs,
        long compareMs,
        IReadOnlyList<string> changedDirectoryPaths,
        IReadOnlyList<string> pruneScopeDirectoryPaths,
        long elapsedMs)
    {
        public int RootCount { get; } = rootCount;

        public int DirectoryCount { get; } = directoryCount;

        public int ExistingRowCount { get; } = existingRowCount;

        public int MissingRowCount { get; } = missingRowCount;

        public int MissingMetadataCount { get; } = missingMetadataCount;

        public bool PrefetchedSnapshotUsed { get; } = prefetchedSnapshotUsed;

        public long CandidateBuildMs { get; } = candidateBuildMs;

        public long ExistingMapMs { get; } = existingMapMs;

        public long CompareMs { get; } = compareMs;

        public IReadOnlyList<string> ChangedDirectoryPaths { get; } = changedDirectoryPaths ?? [];

        public IReadOnlyList<string> PruneScopeDirectoryPaths { get; } = pruneScopeDirectoryPaths ?? [];

        public long ElapsedMs { get; } = elapsedMs;
    }

    private enum Lr2NormalFolderDirectoryChangeState
    {
        Unchanged,
        Changed,
        MissingRow,
        MissingMetadata
    }

    private static Func<string, DateTime?> CreateLastWriteTimeResolver(
        IReadOnlyDictionary<string, RootFileEnumerationEntry> entriesByPath)
    {
        if (entriesByPath == null || entriesByPath.Count == 0)
        {
            return null;
        }

        return path =>
        {
            string key = Lr2FolderPath.NormalizeDirectoryPath(path);
            return !string.IsNullOrWhiteSpace(key)
                && entriesByPath.TryGetValue(key, out RootFileEnumerationEntry entry)
                    ? entry.LastWriteTimeUtc
                    : null;
        };
    }

    private static void ApplyLr2NormalFolderSyncResult(SongTableFileCheckResult result, Lr2NormalFolderDbSyncResult syncResult)
    {
        result.Lr2NormalFolderGeneratedCount = syncResult.GeneratedCount;
        result.Lr2NormalFolderUpsertedCount = syncResult.UpsertedCount;
        result.Lr2NormalFolderDeletedCount = syncResult.DeletedCount;
        result.Lr2NormalFolderSkippedUnsupportedPathCount = syncResult.SkippedUnsupportedPathCount;
        result.Lr2NormalFolderSkippedMissingMetadataCount = syncResult.SkippedMissingMetadataCount;
        result.Lr2NormalFolderSkippedIncompatibleChartPathCount = syncResult.SkippedIncompatibleChartPathCount;
        result.Lr2NormalFolderMetadataRequestedDirectoryCount = syncResult.MetadataRequestedDirectoryCount;
        result.Lr2NormalFolderMetadataResolvedDirectoryCount = syncResult.MetadataResolvedDirectoryCount;
        result.Lr2NormalFolderInfoCandidateCount = syncResult.FolderInfoCandidateCount;
        result.Lr2NormalFolderInfoAppliedCount = syncResult.FolderInfoAppliedCount;
        result.Lr2NormalFolderInfoReadFailureCount = syncResult.FolderInfoReadFailureCount;
        result.Lr2NormalFolderSyncMs = syncResult.ElapsedMs;
    }

    private FileDiffParsePipelineResult RunFileDiffParsePipeline(
        IReadOnlyList<FileDiffParseTarget> bmsTargets,
        IReadOnlyList<string> bmsonPaths,
        BmsLibraryDbGateway dbGateway,
        IDictionary<string, LR2SongDBExtended.chart_info_parse_failure> currentChartInfoParseFailures,
        SongTableFileCheckResult result,
        int parseTargetCount,
        ref int parseProcessedCount,
        IBmsLibraryDialogService dialogService,
        Action<string> logEverythingScan,
        Action<int, int, string> reportParseProgress,
        Action<string> logInstallPerformance,
        Action<string> logInstallPerformanceWarn,
        ResourceHealthLookupContext inlineMaintenanceLookupContext,
        Dictionary<string, Queue<BMSFile>> movedBmsSourcesByMd5,
        IReadOnlyDictionary<string, Lr2SongUserColumns> movedBmsUserColumnsByDeletedPath,
        FileDiffStreamingCommitContext commitContext)
    {
        var pipelineResult = new FileDiffParsePipelineResult();
        if (parseTargetCount <= 0)
        {
            return pipelineResult;
        }

        List<FileDiffParseTarget> parseTargets = [.. EnumerateFileDiffTargets(bmsTargets, bmsonPaths)];
        int parserDegree = Math.Max(1, result.FileDiffParserDegree);
        int postParseWorkerDegree = ResolveFileDiffPostParseWorkerDegree(parserDegree);
        int readerDegree = ChartFileReadPipelinePolicy.ResolveReaderDegree(Environment.ProcessorCount, parseTargets.Count);
        int chartInfoBatchSize = Math.Max(1, result.InlineChartInfoBatchSize);
        int postParseBatchSize = DefaultFileDiffPostParseBatchSize;
        int readQueueCapacity = ChartFileReadPipelinePolicy.ResolveReadQueueCapacity(parserDegree, readerDegree);
        int parsedQueueCapacity = ResolveFileDiffParsedQueueCapacity(parserDegree, postParseBatchSize);
        int postParseQueueCapacity = ResolveFileDiffPostParseQueueCapacity(postParseWorkerDegree);
        int postParseResultQueueCapacity = Math.Max(postParseWorkerDegree * 2, postParseWorkerDegree + 1);
        bool streamCommitChunks = commitContext != null;
        int commitQueueCapacity = streamCommitChunks
            ? Math.Max(1, Math.Min(4, parserDegree))
            : 0;
        IReadOnlyDictionary<string, LR2SongDBExtended.chart_info> currentChartInfoRowsBySha256 =
            LoadFileDiffCurrentChartInfoRows(dbGateway, parseTargets.Count, chartInfoBatchSize, logInstallPerformance);
        if (currentChartInfoRowsBySha256 == null && parseTargets.Count > 1)
        {
            currentChartInfoRowsBySha256 = new Dictionary<string, LR2SongDBExtended.chart_info>(StringComparer.OrdinalIgnoreCase);
            logInstallPerformance?.Invoke("file_diff_chart_info_snapshot"
                + " status=suppressed"
                + " reason=multi_post_parse_without_snapshot"
                + " parseTargets=" + parseTargets.Count
                + " inlineChartInfoBatchSize=" + chartInfoBatchSize);
        }
        result.FileDiffReaderDegree = readerDegree;
        result.FileDiffPostParseWorkerDegree = postParseWorkerDegree;
        result.ReadQueueCapacity = readQueueCapacity;
        result.ParsedQueueCapacity = parsedQueueCapacity;
        result.PostParseQueueCapacity = postParseQueueCapacity;
        result.PostParseResultQueueCapacity = postParseResultQueueCapacity;
        result.PostParseBatchSize = postParseBatchSize;
        result.CommitQueueCapacity = commitQueueCapacity;
        result.CommitStreamingEnabled = streamCommitChunks;
        result.CommitStreamingBarrierReason = streamCommitChunks
            ? "none"
            : commitContext == null
                ? "no_commit_context"
                : "no_commit_context";
        result.InlineMaintenanceDegree = 1;
        var readQueue = new BlockingCollection<FileDiffReadCandidate>(readQueueCapacity);
        var parsedQueue = new BlockingCollection<FileDiffParsedCandidate>(parsedQueueCapacity);
        var postParseQueue = new BlockingCollection<FileDiffParsedBatch>(postParseQueueCapacity);
        var postParseResultQueue = new BlockingCollection<FileDiffPostParseResult>(postParseResultQueueCapacity);
        BlockingCollection<FileScanDiffCommitChunk> commitQueue = streamCommitChunks
            ? new BlockingCollection<FileScanDiffCommitChunk>(commitQueueCapacity)
            : [];
        long readTicks = 0L;
        long digestTicks = 0L;
        long bmsParseTicks = 0L;
        long bmsonParseTicks = 0L;
        long readerOutputWaitTicks = 0L;
        long parserOutputWaitTicks = 0L;
        long postParseQueueWaitTicks = 0L;
        long postParseOutputWaitTicks = 0L;
        long commitQueueWaitTicks = 0L;
        long postParseTicks = 0L;
        long postParseWallStartTimestamp = 0L;
        long postParseWallEndTimestamp = 0L;
        long postParseMaxBatchTicks = 0L;
        long inlineChartInfoWallTicks = 0L;
        long inlineMaintenanceWallTicks = 0L;
        long inlineBmsMaintenanceWallTicks = 0L;
        long inlineBmsonMaintenanceWallTicks = 0L;
        int postParseBatchCount = 0;
        int postParseProgressCount = parseProcessedCount;
        void ReportPostParsePreparedProgress(string path)
        {
            int processed = Interlocked.Increment(ref postParseProgressCount);
            reportParseProgress?.Invoke(parseTargetCount, Math.Min(parseTargetCount, processed), path ?? string.Empty);
        }
        int snapshotQueueHighWatermark = 0;
        Exception postParseException = null;
        var pipelineException = new PipelineExceptionSignal();
        var folderParentHashCache = new Lr2SongFolderParentNormalizer.Lr2FolderParentHashCache();
        var parsedCommitChunks = new List<FileScanDiffCommitChunk>();

        Task[] postParseWorkerTasks = [.. Enumerable.Range(0, postParseWorkerDegree)
            .Select(_ => Task.Run(delegate
            {
                try
                {
                    foreach (FileDiffParsedBatch batch in postParseQueue.GetConsumingEnumerable())
                    {
                        Interlocked.CompareExchange(ref postParseWallStartTimestamp, Stopwatch.GetTimestamp(), 0L);
                        FileDiffPostParseResult postParseResult = BuildFileDiffPostParseResult(
                            batch.Sequence,
                            batch.BmsCandidates,
                            batch.BmsonCandidates,
                            dbGateway,
                            currentChartInfoParseFailures,
                            parserDegree,
                            chartInfoBatchSize,
                            Math.Max(1, result.InlineMaintenanceDegree),
                            logInstallPerformance,
                            logInstallPerformanceWarn,
                            inlineMaintenanceLookupContext,
                            currentChartInfoRowsBySha256,
                            ReportPostParsePreparedProgress);
                        AddWithWait(postParseResultQueue, postParseResult, ref postParseOutputWaitTicks, pipelineException);
                    }
                }
                catch (Exception ex)
                {
                    Volatile.Write(ref postParseException, ex);
                    CompleteAddingSilently(postParseQueue);
                    CompleteAddingSilently(postParseResultQueue);
                    throw;
                }
            }))];
        Task postParseWorkerCompletionTask = Task.WhenAll(postParseWorkerTasks).ContinueWith(_ => CompleteAddingSilently(postParseResultQueue));

        var postParseCollectorTask = Task.Run(delegate
        {
            try
            {
                int nextPostParseSequence = 0;
                var pendingPostParseResults = new SortedDictionary<int, FileDiffPostParseResult>();
                foreach (FileDiffPostParseResult postParseResult in postParseResultQueue.GetConsumingEnumerable())
                {
                    if (postParseResult == null)
                    {
                        continue;
                    }
                    pendingPostParseResults[postParseResult.Sequence] = postParseResult;
                    while (pendingPostParseResults.TryGetValue(nextPostParseSequence, out FileDiffPostParseResult current))
                    {
                        pendingPostParseResults.Remove(nextPostParseSequence);
                        ApplyOrderedFileDiffPostParseResult(
                            current,
                            result,
                            pipelineResult,
                            dialogService,
                            logEverythingScan,
                            logInstallPerformance,
                            commitQueue,
                            pipelineException,
                            ref commitQueueWaitTicks,
                            ref postParseBatchCount,
                            ref postParseTicks,
                            ref postParseMaxBatchTicks,
                            ref inlineChartInfoWallTicks,
                            ref inlineMaintenanceWallTicks,
                            ref inlineBmsMaintenanceWallTicks,
                            ref inlineBmsonMaintenanceWallTicks);
                        Interlocked.Exchange(ref postParseWallEndTimestamp, Stopwatch.GetTimestamp());
                        nextPostParseSequence++;
                    }
                }
            }
            catch (Exception ex)
            {
                Volatile.Write(ref postParseException, ex);
                pipelineException.Set(ex);
                CompleteAddingSilently(postParseQueue);
                CompleteAddingSilently(postParseResultQueue);
                throw;
            }
            finally
            {
                CompleteAddingSilently(commitQueue);
            }
        });
        var commitCollectorTask = Task.Run(delegate
        {
            try
            {
                foreach (FileScanDiffCommitChunk chunk in commitQueue.GetConsumingEnumerable())
                {
                    if (streamCommitChunks)
                    {
                        commitContext.AddChunk(chunk);
                    }
                    else
                    {
                        parsedCommitChunks.Add(chunk);
                    }
                }
                if (streamCommitChunks)
                {
                    commitContext.Flush();
                }
            }
            catch (Exception ex)
            {
                pipelineException.Set(ex);
                try
                {
                    commitQueue.CompleteAdding();
                }
                catch (InvalidOperationException)
                {
                }
                throw;
            }
            finally
            {
                if (streamCommitChunks)
                {
                    commitContext.ReleaseSongDb();
                }
            }
        });

        int nextReadIndex = -1;
        Task[] readerTasks = [.. Enumerable.Range(0, readerDegree)
            .Select(_ => Task.Run(delegate
            {
                while (true)
                {
                    int index = Interlocked.Increment(ref nextReadIndex);
                    if (index >= parseTargets.Count)
                    {
                        break;
                    }

                    FileDiffReadCandidate candidate = ReadFileDiffTarget(parseTargets[index], ref readTicks);
                    AddWithWait(readQueue, candidate, ref readerOutputWaitTicks, pipelineException);
                    UpdateHighWatermark(ref snapshotQueueHighWatermark, readQueue.Count);
                }
            }))];
        Task readerCompletionTask = Task.WhenAll(readerTasks).ContinueWith(_ => readQueue.CompleteAdding());

        Task[] workerTasks = [.. Enumerable.Range(0, parserDegree)
            .Select(_ => Task.Run(delegate
            {
                foreach (FileDiffReadCandidate readCandidate in readQueue.GetConsumingEnumerable())
                {
                    FileDiffParsedCandidate parsedCandidate = ParseFileDiffCandidate(readCandidate, folderParentHashCache, ref digestTicks, ref bmsParseTicks, ref bmsonParseTicks);
                    AddWithWait(parsedQueue, parsedCandidate, ref parserOutputWaitTicks, pipelineException);
                }
            }))];
        Task parserCompletionTask = Task.WhenAll(workerTasks).ContinueWith(_ => parsedQueue.CompleteAdding());

        Exception pipelineFailure = null;
        try
        {
            var bmsBatch = new List<InlineBmsParseCandidate>(postParseBatchSize);
            var bmsonBatch = new List<InlineBmsonParseCandidate>(postParseBatchSize);
            int postParseBatchSequence = 0;
            foreach (FileDiffParsedCandidate parsedCandidate in parsedQueue.GetConsumingEnumerable())
            {
                if (parsedCandidate.BmsCandidate != null)
                {
                    bmsBatch.Add(parsedCandidate.BmsCandidate);
                }
                if (parsedCandidate.BmsonCandidate != null)
                {
                    bmsonBatch.Add(parsedCandidate.BmsonCandidate);
                }
                if (bmsBatch.Count + bmsonBatch.Count >= postParseBatchSize)
                {
                    EnqueuePostParseBatch(postParseQueue, new FileDiffParsedBatch(postParseBatchSequence++, bmsBatch, bmsonBatch), pipelineException, ref postParseQueueWaitTicks, ref postParseException);
                    bmsBatch = new List<InlineBmsParseCandidate>(postParseBatchSize);
                    bmsonBatch = new List<InlineBmsonParseCandidate>(postParseBatchSize);
                }
            }

            if (bmsBatch.Count > 0 || bmsonBatch.Count > 0)
            {
                EnqueuePostParseBatch(postParseQueue, new FileDiffParsedBatch(postParseBatchSequence++, bmsBatch, bmsonBatch), pipelineException, ref postParseQueueWaitTicks, ref postParseException);
            }
        }
        catch (Exception ex)
        {
            CapturePipelineException(ex, pipelineException, ref pipelineFailure);
        }
        finally
        {
            CompleteAddingSilently(postParseQueue);
            if (pipelineFailure != null)
            {
                CompleteAddingSilently(readQueue);
                CompleteAddingSilently(parsedQueue);
                CompleteAddingSilently(commitQueue);
            }
        }

        try
        {
            Task.WaitAll(readerTasks);
        }
        catch (Exception ex)
        {
            CapturePipelineException(ex, pipelineException, ref pipelineFailure);
        }
        try
        {
            readerCompletionTask.Wait();
        }
        catch (Exception ex)
        {
            CapturePipelineException(ex, pipelineException, ref pipelineFailure);
        }
        try
        {
            Task.WaitAll(workerTasks);
        }
        catch (Exception ex)
        {
            CapturePipelineException(ex, pipelineException, ref pipelineFailure);
        }
        try
        {
            parserCompletionTask.Wait();
        }
        catch (Exception ex)
        {
            CapturePipelineException(ex, pipelineException, ref pipelineFailure);
        }
        try
        {
            Task.WaitAll(postParseWorkerTasks);
        }
        catch (Exception ex)
        {
            CapturePipelineException(ex, pipelineException, ref pipelineFailure);
        }
        try
        {
            postParseWorkerCompletionTask.Wait();
        }
        catch (Exception ex)
        {
            CapturePipelineException(ex, pipelineException, ref pipelineFailure);
        }
        try
        {
            postParseCollectorTask.Wait();
        }
        catch (Exception ex)
        {
            CapturePipelineException(ex, pipelineException, ref pipelineFailure);
        }
        try
        {
            commitCollectorTask.Wait();
        }
        catch (Exception ex)
        {
            CapturePipelineException(ex, pipelineException, ref pipelineFailure);
        }
        if (pipelineFailure != null)
        {
            throw new AggregateException(pipelineFailure);
        }
        PrepareMovedBmsUserColumnRestores(
            pipelineResult,
            movedBmsSourcesByMd5,
            movedBmsUserColumnsByDeletedPath,
            logInstallPerformanceWarn);
        foreach (FileScanDiffCommitChunk chunk in parsedCommitChunks)
        {
            commitContext?.AddChunk(chunk);
        }
        if (!streamCommitChunks)
        {
            commitContext?.Flush();
        }
        parseProcessedCount = Math.Max(parseProcessedCount, Volatile.Read(ref postParseProgressCount));

        pipelineResult.ReadMs = TicksToMilliseconds(readTicks);
        pipelineResult.DigestMs = TicksToMilliseconds(digestTicks);
        pipelineResult.BmsParseMs = TicksToMilliseconds(bmsParseTicks);
        pipelineResult.BmsonParseMs = TicksToMilliseconds(bmsonParseTicks);
        pipelineResult.SnapshotQueueHighWatermark = snapshotQueueHighWatermark;
        result.ReaderOutputWaitMs = TicksToMilliseconds(readerOutputWaitTicks);
        result.ParserOutputWaitMs = TicksToMilliseconds(parserOutputWaitTicks);
        result.PostParseQueueWaitMs = TicksToMilliseconds(postParseQueueWaitTicks);
        result.PostParseOutputWaitMs = TicksToMilliseconds(postParseOutputWaitTicks);
        result.CommitQueueWaitMs = TicksToMilliseconds(commitQueueWaitTicks);
        result.PostParseBatchCount = postParseBatchCount;
        long postParseWallStart = Interlocked.Read(ref postParseWallStartTimestamp);
        long postParseWallEnd = Interlocked.Read(ref postParseWallEndTimestamp);
        result.PostParseWallMs = postParseWallStart > 0L && postParseWallEnd >= postParseWallStart
            ? TicksToMilliseconds(postParseWallEnd - postParseWallStart)
            : 0L;
        result.PostParseMaxBatchMs = TicksToMilliseconds(postParseMaxBatchTicks);
        result.InlineChartInfoWallMs = TicksToMilliseconds(inlineChartInfoWallTicks);
        result.InlineMaintenanceWallMs = TicksToMilliseconds(inlineMaintenanceWallTicks);
        result.InlineBmsMaintenanceWallMs = TicksToMilliseconds(inlineBmsMaintenanceWallTicks);
        result.InlineBmsonMaintenanceWallMs = TicksToMilliseconds(inlineBmsonMaintenanceWallTicks);
        return pipelineResult;
    }

    private static IReadOnlyDictionary<string, LR2SongDBExtended.chart_info> LoadFileDiffCurrentChartInfoRows(
        BmsLibraryDbGateway dbGateway,
        int parseTargetCount,
        int inlineChartInfoBatchSize,
        Action<string> logInstallPerformance)
    {
        if (dbGateway == null || parseTargetCount <= 1)
        {
            return null;
        }

        var stopwatch = Stopwatch.StartNew();
        Dictionary<string, LR2SongDBExtended.chart_info> rows = dbGateway.TryLoadCurrentChartInfoMapReadOnly();
        stopwatch.Stop();
        if (rows == null)
        {
            logInstallPerformance?.Invoke("file_diff_chart_info_snapshot"
                + " status=skipped"
                + " reason=read_only_schema_not_current"
                + " parseTargets=" + parseTargetCount
                + " inlineChartInfoBatchSize=" + inlineChartInfoBatchSize
                + " elapsedMs=" + stopwatch.ElapsedMilliseconds);
            return null;
        }
        logInstallPerformance?.Invoke("file_diff_chart_info_snapshot"
            + " status=loaded"
            + " rows=" + rows.Count
            + " parseTargets=" + parseTargetCount
            + " inlineChartInfoBatchSize=" + inlineChartInfoBatchSize
            + " elapsedMs=" + stopwatch.ElapsedMilliseconds);
        return rows;
    }

    private static IEnumerable<FileDiffParseTarget> EnumerateFileDiffTargets(IReadOnlyList<FileDiffParseTarget> bmsTargets, IReadOnlyList<string> bmsonPaths)
    {
        foreach (FileDiffParseTarget target in bmsTargets ?? [])
        {
            if (target != null)
            {
                yield return target;
            }
        }
        foreach (string path in bmsonPaths ?? [])
        {
            yield return new FileDiffParseTarget(FileDiffChartKind.Bmson, path);
        }
    }

    private static FileDiffParseTarget CreateBmsFileDiffTarget(
        string path,
        IReadOnlyDictionary<string, BMSFile> currentBmsByPath,
        ISet<string> textFileDirectories,
        bool textGroupSurfaceAvailable,
        IReadOnlyDictionary<string, RootFileEnumerationEntry> chartFileEntriesByPath,
        SongTableFileCheckResult result,
        bool protectExistingBmsRowsFromLr2FullGenerationMigration)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            return null;
        }
        if (currentBmsByPath == null || !currentBmsByPath.TryGetValue(path, out BMSFile existing))
        {
            int? scannedTextFlag = textGroupSurfaceAvailable
                ? ResolveTextGroupFlag(path, textFileDirectories)
                : null;
            return new FileDiffParseTarget(FileDiffChartKind.Bms, path, null, scannedTextFlag.GetValueOrDefault());
        }
        if (protectExistingBmsRowsFromLr2FullGenerationMigration)
        {
            result.BmsLegacyExistingProtectedCount++;
            return null;
        }
        int? existingTextFlag = textGroupSurfaceAvailable
            ? ResolveTextGroupFlag(path, textFileDirectories)
            : null;
        DateTime lastWriteTimeUtc = ResolveScannedChartLastWriteTimeUtc(
            path,
            chartFileEntriesByPath,
            result,
            isBmson: false);
        if (lastWriteTimeUtc == DateTime.MinValue)
        {
            return null;
        }
        int currentDate = Lr2SongRowEnricher.ToLr2UnixSeconds(lastWriteTimeUtc);
        int targetTextFlag = existingTextFlag ?? existing.txt.GetValueOrDefault();
        bool textChanged = existingTextFlag.HasValue
            && existing.txt.GetValueOrDefault() != existingTextFlag.Value;
        if (existing.date == currentDate && !textChanged)
        {
            return null;
        }
        return new FileDiffParseTarget(FileDiffChartKind.Bms, path, existing, targetTextFlag);
    }

    private static DateTime ResolveScannedChartLastWriteTimeUtc(
        string path,
        IReadOnlyDictionary<string, RootFileEnumerationEntry> chartFileEntriesByPath,
        SongTableFileCheckResult result,
        bool isBmson)
    {
        if (!string.IsNullOrWhiteSpace(path)
            && chartFileEntriesByPath != null
            && chartFileEntriesByPath.TryGetValue(path, out RootFileEnumerationEntry entry)
            && entry?.LastWriteTimeUtc != null)
        {
            return entry.LastWriteTimeUtc.Value;
        }

        if (isBmson)
        {
            if (result != null)
            {
                result.BmsonMtimeFallbackCount++;
            }
        }
        else if (result != null)
        {
            result.BmsMtimeFallbackCount++;
        }

        return SafeGetLastWriteTimeUtc(path);
    }

    private static int ResolveTextGroupFlag(string path, ISet<string> textFileDirectories)
    {
        if (string.IsNullOrWhiteSpace(path) || textFileDirectories == null || textFileDirectories.Count == 0)
        {
            return 0;
        }
        string directory = Path.GetDirectoryName(path);
        return !string.IsNullOrWhiteSpace(directory) && textFileDirectories.Contains(directory) ? 1 : 0;
    }

    private static bool IsBmsonChartPath(string path)
    {
        return !string.IsNullOrWhiteSpace(path)
            && path.EndsWith(".bmson", StringComparison.OrdinalIgnoreCase);
    }

    private static FileDiffReadCandidate ReadFileDiffTarget(FileDiffParseTarget target, ref long readTicks)
    {
        try
        {
            var stopwatch = Stopwatch.StartNew();
            ChartFileReadBuffer buffer = ChartFileContentReader.ReadBuffer(target.Path);
            stopwatch.Stop();
            Interlocked.Add(ref readTicks, stopwatch.ElapsedTicks);
            return FileDiffReadCandidate.CreateSuccess(target.Kind, target.Path, buffer, target.ExistingBmsFile, target.TextFlag);
        }
        catch (IOException ex)
        {
            return FileDiffReadCandidate.CreateFailure(target.Kind, target.Path, target.ExistingBmsFile, target.TextFlag, ex);
        }
        catch (Exception ex) when (target.Kind == FileDiffChartKind.Bmson)
        {
            return FileDiffReadCandidate.CreateFailure(target.Kind, target.Path, target.ExistingBmsFile, target.TextFlag, ex);
        }
    }

    private static FileDiffParsedCandidate ParseFileDiffCandidate(
        FileDiffReadCandidate candidate,
        Lr2SongFolderParentNormalizer.Lr2FolderParentHashCache folderParentHashCache,
        ref long digestTicks,
        ref long bmsParseTicks,
        ref long bmsonParseTicks)
    {
        ChartFileSnapshot snapshot = CreateFileDiffSnapshot(candidate, ref digestTicks);
        if (candidate.Kind == FileDiffChartKind.Bms)
        {
            if (candidate.Exception is IOException readException)
            {
                return FileDiffParsedCandidate.FromBms(InlineBmsParseCandidate.CreateFailure(candidate.Path, candidate.ExistingBmsFile, readException));
            }
            var stopwatch = Stopwatch.StartNew();
            BMSFile file = Lr2SongRowEnricher.CreateParsedSongRowFromSnapshot(
                snapshot,
                candidate.TextFlag,
                candidate.ExistingBmsFile,
                folderParentHashCache);
            stopwatch.Stop();
            Interlocked.Add(ref bmsParseTicks, stopwatch.ElapsedTicks);
            return FileDiffParsedCandidate.FromBms(InlineBmsParseCandidate.CreateSuccess(candidate.Path, snapshot, file, candidate.ExistingBmsFile));
        }

        if (candidate.Exception != null)
        {
            return FileDiffParsedCandidate.FromBmson(InlineBmsonParseCandidate.CreateFailure(candidate.Path, candidate.Exception));
        }
        try
        {
            var stopwatch = Stopwatch.StartNew();
            LR2SongDBExtended.bmson_song song = BmsonSongParser.ParseSnapshot(snapshot);
            stopwatch.Stop();
            Interlocked.Add(ref bmsonParseTicks, stopwatch.ElapsedTicks);
            return FileDiffParsedCandidate.FromBmson(InlineBmsonParseCandidate.CreateSuccess(candidate.Path, snapshot, song));
        }
        catch (Exception ex)
        {
            return FileDiffParsedCandidate.FromBmson(InlineBmsonParseCandidate.CreateFailure(candidate.Path, ex));
        }
    }

    private static ChartFileSnapshot CreateFileDiffSnapshot(FileDiffReadCandidate candidate, ref long digestTicks)
    {
        if (candidate?.Buffer == null)
        {
            return null;
        }

        var stopwatch = Stopwatch.StartNew();
        ChartFileSnapshot snapshot = ChartFileContentReader.CreateSnapshot(candidate.Buffer);
        stopwatch.Stop();
        Interlocked.Add(ref digestTicks, stopwatch.ElapsedTicks);
        return snapshot;
    }

    private FileDiffPostParseResult BuildFileDiffPostParseResult(
        int sequence,
        List<InlineBmsParseCandidate> bmsBatch,
        List<InlineBmsonParseCandidate> bmsonBatch,
        BmsLibraryDbGateway dbGateway,
        IDictionary<string, LR2SongDBExtended.chart_info_parse_failure> currentChartInfoParseFailures,
        int chartInfoParserDegree,
        int inlineChartInfoBatchSize,
        int inlineMaintenanceDegree,
        Action<string> logInstallPerformance,
        Action<string> logInstallPerformanceWarn,
        ResourceHealthLookupContext inlineMaintenanceLookupContext,
        IReadOnlyDictionary<string, LR2SongDBExtended.chart_info> currentChartInfoRowsBySha256,
        Action<string> reportPostParsePreparedProgress)
    {
        var postResult = new FileDiffPostParseResult(sequence);
        FileDiffPostParseBatchMetrics metrics = postResult.Metrics;
        metrics.BmsCount = bmsBatch?.Count ?? 0;
        metrics.BmsonCount = bmsonBatch?.Count ?? 0;
        var totalStopwatch = Stopwatch.StartNew();
        try
        {
            if ((bmsBatch == null || bmsBatch.Count == 0) && (bmsonBatch == null || bmsonBatch.Count == 0))
            {
                return postResult;
            }

            List<InlineBmsParseCandidate> fullBmsBatch = bmsBatch == null
                ? []
                : [.. bmsBatch.Where(candidate => !IsBmsDateOnlyCandidate(candidate))];
            var chartInfoStopwatch = Stopwatch.StartNew();
            ChartInfoInlineBuildResult bmsChartInfoResult = BuildInlineBmsChartInfo(
                fullBmsBatch,
                dbGateway,
                currentChartInfoParseFailures,
                chartInfoParserDegree,
                inlineChartInfoBatchSize,
                currentChartInfoRowsBySha256,
                logInstallPerformance,
                logInstallPerformanceWarn);
            ChartInfoInlineBuildResult bmsonChartInfoResult = BuildInlineBmsonChartInfo(
                bmsonBatch,
                dbGateway,
                currentChartInfoParseFailures,
                chartInfoParserDegree,
                inlineChartInfoBatchSize,
                currentChartInfoRowsBySha256,
                logInstallPerformance,
                logInstallPerformanceWarn);
            AddInlineChartInfoBuildResult(postResult.ChartInfoResult, bmsChartInfoResult);
            AddInlineChartInfoBuildResult(postResult.ChartInfoResult, bmsonChartInfoResult);
            chartInfoStopwatch.Stop();
            metrics.ChartInfoTicks = chartInfoStopwatch.ElapsedTicks;

            Dictionary<string, Queue<LR2SongDBExtended.chart_info>> chartInfoByMd5 = BuildQueueByMd5(postResult.ChartInfoResult.ChartInfoRows, row => row?.md5);
            Dictionary<string, Queue<LR2SongDBExtended.chart_info>> appliedChartInfoByMd5 = BuildQueueByMd5(postResult.ChartInfoResult.AppliedRows, row => row?.md5);
            Dictionary<string, Queue<LR2SongDBExtended.chart_info_parse_failure>> failureByMd5 = BuildQueueByMd5(postResult.ChartInfoResult.ParseFailureRows, row => row?.md5);
            Dictionary<string, LR2SongDBExtended.chart_info> appliedChartInfoByPath = BuildAppliedChartInfoByPath(fullBmsBatch, postResult.ChartInfoResult.AppliedRows);
            var failureDeletes = new HashSet<string>(postResult.ChartInfoResult.ParseFailureDeleteMd5s, StringComparer.OrdinalIgnoreCase);
            FileScanDiffCommitChunk commitChunk = postResult.CommitChunk;
            var maintenanceStopwatch = Stopwatch.StartNew();
            var bmsMaintenanceStopwatch = Stopwatch.StartNew();
            InlineMaintenanceItemResult[] bmsMaintenanceResults = BuildInlineBmsMaintenanceBatch(
                fullBmsBatch,
                inlineMaintenanceLookupContext,
                inlineMaintenanceDegree,
                logInstallPerformanceWarn);
            bmsMaintenanceStopwatch.Stop();
            var bmsonMaintenanceStopwatch = Stopwatch.StartNew();
            InlineMaintenanceItemResult[] bmsonMaintenanceResults = BuildInlineBmsonMaintenanceBatch(
                bmsonBatch,
                inlineMaintenanceLookupContext,
                inlineMaintenanceDegree,
                logInstallPerformanceWarn);
            bmsonMaintenanceStopwatch.Stop();
            maintenanceStopwatch.Stop();
            metrics.MaintenanceTicks = maintenanceStopwatch.ElapsedTicks;
            metrics.BmsMaintenanceTicks = bmsMaintenanceStopwatch.ElapsedTicks;
            metrics.BmsonMaintenanceTicks = bmsonMaintenanceStopwatch.ElapsedTicks;
            postResult.MaintenanceResults.AddRange(bmsMaintenanceResults.Where(item => item != null));
            postResult.MaintenanceResults.AddRange(bmsonMaintenanceResults.Where(item => item != null));
            metrics.HealthMs = SumInlineMaintenanceHealthMs(bmsMaintenanceResults) + SumInlineMaintenanceHealthMs(bmsonMaintenanceResults);
            metrics.EncodingMs = SumInlineMaintenanceEncodingMs(bmsMaintenanceResults);
            metrics.EncodingReloadMs = SumInlineMaintenanceEncodingReloadMs(bmsMaintenanceResults);
            metrics.EncodingMaxMs = MaxInlineMaintenanceEncodingMs(bmsMaintenanceResults);
            metrics.CacheHitCount = SumInlineMaintenanceCacheHitCount(bmsMaintenanceResults) + SumInlineMaintenanceCacheHitCount(bmsonMaintenanceResults);
            metrics.ResourceIndexHitCount = SumInlineMaintenanceResourceIndexHitCount(bmsMaintenanceResults) + SumInlineMaintenanceResourceIndexHitCount(bmsonMaintenanceResults);
            metrics.FileExistsFallbackCount = SumInlineMaintenanceFileExistsFallbackCount(bmsMaintenanceResults) + SumInlineMaintenanceFileExistsFallbackCount(bmsonMaintenanceResults);

            if (bmsBatch != null && bmsBatch.Count > 0)
            {
                foreach (InlineBmsParseCandidate candidate in bmsBatch)
                {
                    if (IsBmsDateOnlyCandidate(candidate))
                    {
                        int date = candidate.File.date.GetValueOrDefault();
                        int textFlag = candidate.File.txt.GetValueOrDefault();
                        bool dateChanged = candidate.ExistingFile.date != date;
                        bool textChanged = candidate.ExistingFile.txt.GetValueOrDefault() != textFlag;
                        candidate.ExistingFile.date = date;
                        candidate.ExistingFile.SetTextGroupFlag(textFlag);
                        if (dateChanged)
                        {
                            postResult.BmsDateOnlyUpdateCount++;
                        }
                        if (textChanged)
                        {
                            postResult.BmsTextOnlyUpdateCount++;
                        }
                        commitChunk.AddUpdatedBmsMetadata(candidate.File.path, date, textChanged ? textFlag : null);
                        reportPostParsePreparedProgress?.Invoke(candidate.Path);
                    }
                }
                for (int i = 0; i < fullBmsBatch.Count; i++)
                {
                    InlineBmsParseCandidate candidate = fullBmsBatch[i];
                    if (candidate.Exception != null)
                    {
                        postResult.BmsParseFailures.Add(candidate);
                    }
                    if (candidate.File != null)
                    {
                        postResult.TrackBmsRelinkDestinationCandidate(candidate);
                        if (bmsMaintenanceResults != null && i < bmsMaintenanceResults.Length && bmsMaintenanceResults[i]?.Succeeded == true)
                        {
                            commitChunk.AddMaintenanceInfoRow(candidate.File.maintenanceInfo);
                        }
                        Lr2SongRowEnricher.EnrichFromChartInfo(candidate.File, ResolveAppliedChartInfo(candidate, appliedChartInfoByPath));
                        postResult.SuccessfullyReplacedBmsPaths.Add(candidate.Path);
                        postResult.AddedFiles.Add(candidate.File);
                        if (candidate.ExistingFile == null && !string.IsNullOrWhiteSpace(candidate.File.path))
                        {
                            postResult.NewlyInsertedBmsPaths.Add(candidate.File.path);
                        }
                        commitChunk.AddAddedBmsFile(candidate.File);
                        AttachInlineChartInfoRows(commitChunk, candidate.File.hash, chartInfoByMd5, appliedChartInfoByMd5, failureByMd5, failureDeletes);
                        reportPostParsePreparedProgress?.Invoke(candidate.Path);
                    }
                    else
                    {
                        reportPostParsePreparedProgress?.Invoke(candidate.Path);
                    }
                }
                bmsBatch.Clear();
            }
            if (bmsonBatch != null && bmsonBatch.Count > 0)
            {
                for (int i = 0; i < bmsonBatch.Count; i++)
                {
                    InlineBmsonParseCandidate candidate = bmsonBatch[i];
                    if (candidate.Exception != null)
                    {
                        postResult.BmsonParseFailures.Add(candidate);
                    }
                    if (candidate.Song != null)
                    {
                        if (bmsonMaintenanceResults != null && i < bmsonMaintenanceResults.Length && bmsonMaintenanceResults[i]?.Succeeded == true)
                        {
                            commitChunk.AddMaintenanceInfoRow(candidate.Song.MaintenanceInfo);
                        }
                        postResult.SuccessfullyParsedBmsonPaths.Add(candidate.Path);
                        postResult.ParsedBmsonSongs.Add(candidate.Song);
                        commitChunk.AddUpsertBmsonSong(candidate.Song);
                        AttachInlineChartInfoRows(commitChunk, candidate.Song.md5, chartInfoByMd5, appliedChartInfoByMd5, failureByMd5, failureDeletes);
                        reportPostParsePreparedProgress?.Invoke(candidate.Path);
                    }
                    else
                    {
                        reportPostParsePreparedProgress?.Invoke(candidate.Path);
                    }
                }
                bmsonBatch.Clear();
            }
            AddRemainingInlineRows([], ref commitChunk, chartInfoByMd5, appliedChartInfoByMd5, failureByMd5, failureDeletes, int.MaxValue);
            return postResult;
        }
        finally
        {
            totalStopwatch.Stop();
            metrics.TotalTicks = totalStopwatch.ElapsedTicks;
        }
    }

    private static void ApplyOrderedFileDiffPostParseResult(
        FileDiffPostParseResult postResult,
        SongTableFileCheckResult result,
        FileDiffParsePipelineResult pipelineResult,
        IBmsLibraryDialogService dialogService,
        Action<string> logEverythingScan,
        Action<string> logInstallPerformance,
        BlockingCollection<FileScanDiffCommitChunk> commitQueue,
        PipelineExceptionSignal pipelineException,
        ref long commitQueueWaitTicks,
        ref int postParseBatchCount,
        ref long postParseTicks,
        ref long postParseMaxBatchTicks,
        ref long inlineChartInfoWallTicks,
        ref long inlineMaintenanceWallTicks,
        ref long inlineBmsMaintenanceWallTicks,
        ref long inlineBmsonMaintenanceWallTicks)
    {
        if (postResult == null)
        {
            return;
        }

        long commitWaitBefore = Interlocked.Read(ref commitQueueWaitTicks);
        ApplyFileDiffPostParseResult(
            postResult,
            result,
            pipelineResult,
            dialogService,
            logEverythingScan,
            commitQueue,
            pipelineException,
            ref commitQueueWaitTicks);
        long commitWaitAfter = Interlocked.Read(ref commitQueueWaitTicks);
        FileDiffPostParseBatchMetrics metrics = postResult.Metrics;
        metrics.CommitQueueWaitTicks = Math.Max(0L, commitWaitAfter - commitWaitBefore);
        postParseBatchCount++;
        postParseTicks += metrics.TotalTicks;
        UpdateMaxTicks(ref postParseMaxBatchTicks, metrics.TotalTicks);
        inlineChartInfoWallTicks += metrics.ChartInfoTicks;
        inlineMaintenanceWallTicks += metrics.MaintenanceTicks;
        inlineBmsMaintenanceWallTicks += metrics.BmsMaintenanceTicks;
        inlineBmsonMaintenanceWallTicks += metrics.BmsonMaintenanceTicks;

        long totalMs = TicksToMilliseconds(metrics.TotalTicks);
        if (totalMs >= DefaultSlowFileDiffBatchLogThresholdMs)
        {
            logInstallPerformance?.Invoke("song_tbl_file_check_batch_slow"
                + " batch=" + postParseBatchCount
                + " sequence=" + postResult.Sequence
                + " totalMs=" + totalMs
                + " chartInfoMs=" + TicksToMilliseconds(metrics.ChartInfoTicks)
                + " maintenanceMs=" + TicksToMilliseconds(metrics.MaintenanceTicks)
                + " bmsMaintenanceMs=" + TicksToMilliseconds(metrics.BmsMaintenanceTicks)
                + " bmsonMaintenanceMs=" + TicksToMilliseconds(metrics.BmsonMaintenanceTicks)
                + " healthMs=" + metrics.HealthMs
                + " encodingMs=" + metrics.EncodingMs
                + " encodingReloadMs=" + metrics.EncodingReloadMs
                + " encodingMaxMs=" + metrics.EncodingMaxMs
                + " cacheHit=" + metrics.CacheHitCount
                + " resourceIndexHit=" + metrics.ResourceIndexHitCount
                + " fileExistsFallback=" + metrics.FileExistsFallbackCount
                + " commitQueueMs=" + TicksToMilliseconds(metrics.CommitQueueWaitTicks)
                + " bms=" + metrics.BmsCount
                + " bmson=" + metrics.BmsonCount);
        }
    }

    private static void ApplyFileDiffPostParseResult(
        FileDiffPostParseResult postResult,
        SongTableFileCheckResult result,
        FileDiffParsePipelineResult pipelineResult,
        IBmsLibraryDialogService dialogService,
        Action<string> logEverythingScan,
        BlockingCollection<FileScanDiffCommitChunk> commitQueue,
        PipelineExceptionSignal pipelineException,
        ref long commitQueueWaitTicks)
    {
        if (postResult == null)
        {
            return;
        }

        ApplyInlineChartInfoResult(result, postResult.ChartInfoResult, storeRows: false);
        ApplyInlineMaintenanceResults(result, postResult.MaintenanceResults);
        foreach (InlineBmsParseCandidate candidate in postResult.BmsParseFailures)
        {
            if (candidate?.Exception != null)
            {
                dialogService?.Show(string.Format(Resources.Error_InitializationFailed, candidate.Path, candidate.Exception.Message), Resources.MessageBoxTitle_Error, MessageBoxButton.OK, MessageBoxImage.Hand, MessageBoxResult.OK);
            }
        }
        foreach (InlineBmsonParseCandidate candidate in postResult.BmsonParseFailures)
        {
            if (candidate?.Exception != null)
            {
                logEverythingScan?.Invoke("bmson_parse_failed path=" + candidate.Path + " message=" + candidate.Exception.Message);
            }
        }

        pipelineResult.BmsDateOnlyUpdateCount += postResult.BmsDateOnlyUpdateCount;
        pipelineResult.BmsTextOnlyUpdateCount += postResult.BmsTextOnlyUpdateCount;
        pipelineResult.AddedFiles.AddRange(postResult.AddedFiles);
        foreach (string path in postResult.NewlyInsertedBmsPaths)
        {
            pipelineResult.NewlyInsertedBmsPaths.Add(path);
        }
        foreach (string path in postResult.SuccessfullyReplacedBmsPaths)
        {
            pipelineResult.SuccessfullyReplacedBmsPaths.Add(path);
        }
        pipelineResult.BmsRelinkDestinationCandidates.AddRange(postResult.BmsRelinkDestinationCandidates);
        pipelineResult.ParsedBmsonSongs.AddRange(postResult.ParsedBmsonSongs);
        foreach (string path in postResult.SuccessfullyParsedBmsonPaths)
        {
            pipelineResult.SuccessfullyParsedBmsonPaths.Add(path);
        }
        AddCommitChunkWithWait(commitQueue, postResult.CommitChunk, pipelineException, ref commitQueueWaitTicks);
    }

    private static bool IsBmsDateOnlyCandidate(InlineBmsParseCandidate candidate)
    {
        return candidate?.File != null
            && candidate.ExistingFile != null
            && candidate.File.date.HasValue
            && string.Equals(candidate.File.hash, candidate.ExistingFile.hash, StringComparison.OrdinalIgnoreCase);
    }

    private static void PrepareMovedBmsUserColumnRestores(
        FileDiffParsePipelineResult pipelineResult,
        Dictionary<string, Queue<BMSFile>> movedBmsSourcesByMd5,
        IReadOnlyDictionary<string, Lr2SongUserColumns> movedBmsUserColumnsByDeletedPath,
        Action<string> logInstallPerformanceWarn)
    {
        if (pipelineResult == null || movedBmsSourcesByMd5 == null || movedBmsSourcesByMd5.Count == 0)
        {
            return;
        }

        var destinationsByMd5 = pipelineResult.BmsRelinkDestinationCandidates
            .Where(candidate => candidate?.File != null && !string.IsNullOrWhiteSpace(candidate.File.hash))
            .GroupBy(candidate => candidate.File.hash, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(group => group.Key, group => group.ToList(), StringComparer.OrdinalIgnoreCase);
        foreach (KeyValuePair<string, Queue<BMSFile>> sourceEntry in movedBmsSourcesByMd5)
        {
            string md5 = sourceEntry.Key;
            if (string.IsNullOrWhiteSpace(md5)
                || !destinationsByMd5.TryGetValue(md5, out List<BmsRelinkDestinationCandidate> destinations)
                || destinations == null
                || destinations.Count == 0)
            {
                continue;
            }

            int sourceCount = sourceEntry.Value?.Count ?? 0;
            if (sourceCount == 1 && destinations.Count == 1)
            {
                BMSFile source = sourceEntry.Value.Peek();
                BmsRelinkDestinationCandidate destination = destinations[0];
                if (source == null
                    || string.IsNullOrWhiteSpace(source.path)
                    || destination?.File == null
                    || string.IsNullOrWhiteSpace(destination.File.path)
                    || movedBmsUserColumnsByDeletedPath == null
                    || !movedBmsUserColumnsByDeletedPath.TryGetValue(source.path, out Lr2SongUserColumns userColumns)
                    || userColumns == null)
                {
                    continue;
                }
                BmsLibraryDbGateway.ApplySongUserColumns(destination.File, userColumns);
                pipelineResult.BmsMovedHashRelinkUserColumnRestores[destination.File.path] = userColumns;
                pipelineResult.BmsMovedHashRelinkCount++;
                continue;
            }

            pipelineResult.BmsMovedHashRelinkAmbiguousCount += destinations.Count;
            logInstallPerformanceWarn?.Invoke("lr2_song_relink_ambiguous md5=" + md5
                + " sourceCount=" + sourceCount
                + " destinationCount=" + destinations.Count);
        }
    }

    private static InlineMaintenanceItemResult[] BuildInlineBmsMaintenanceBatch(
        IReadOnlyList<InlineBmsParseCandidate> candidates,
        ResourceHealthLookupContext lookupContext,
        int degree,
        Action<string> logInstallPerformanceWarn)
    {
        if (candidates == null || candidates.Count == 0)
        {
            return [];
        }
        var results = new InlineMaintenanceItemResult[candidates.Count];
        Parallel.For(0, candidates.Count, new ParallelOptions { MaxDegreeOfParallelism = Math.Max(1, degree) }, delegate (int index)
        {
            InlineBmsParseCandidate candidate = candidates[index];
            BMSFile file = candidate?.File;
            if (file != null)
            {
                results[index] = BuildInlineBmsMaintenance(file, candidate.Snapshot, CreateInlineResourceLookupScope(lookupContext));
            }
        });
        LogInlineMaintenanceWarnings(results, logInstallPerformanceWarn);
        return results;
    }

    private static InlineMaintenanceItemResult[] BuildInlineBmsonMaintenanceBatch(
        IReadOnlyList<InlineBmsonParseCandidate> candidates,
        ResourceHealthLookupContext lookupContext,
        int degree,
        Action<string> logInstallPerformanceWarn)
    {
        if (candidates == null || candidates.Count == 0)
        {
            return [];
        }
        var results = new InlineMaintenanceItemResult[candidates.Count];
        Parallel.For(0, candidates.Count, new ParallelOptions { MaxDegreeOfParallelism = Math.Max(1, degree) }, delegate (int index)
        {
            LR2SongDBExtended.bmson_song song = candidates[index]?.Song;
            if (song != null)
            {
                results[index] = BuildInlineBmsonMaintenance(song, CreateInlineResourceLookupScope(lookupContext));
            }
        });
        LogInlineMaintenanceWarnings(results, logInstallPerformanceWarn);
        return results;
    }

    private static ResourceHealthLookupContext CreateInlineResourceLookupScope(ResourceHealthLookupContext lookupContext)
    {
        return lookupContext?.CreateCounterScope() ?? new ResourceHealthLookupContext(null);
    }

    private static InlineMaintenanceItemResult BuildInlineBmsMaintenance(
        BMSFile file,
        ChartFileSnapshot snapshot,
        ResourceHealthLookupContext lookupContext)
    {
        if (file == null)
        {
            return InlineMaintenanceItemResult.Empty;
        }
        var stopwatch = Stopwatch.StartNew();
        long healthMs = 0L;
        long encodingMs = 0L;
        long encodingReloadMs = 0L;
        long cacheHitCount = 0L;
        long resourceIndexHitCount = 0L;
        long resourceHealthSetCacheHitCount = 0L;
        long fileExistsFallbackCount = 0L;
        int encodingReloadCount = 0;
        bool completed = false;
        string warning = null;
        BMSFile.BmsEncodingDetectionResult detectionResult = null;
        try
        {
            BmsLibraryMaintenanceService.MaintenanceEvaluationResult maintenanceResult =
                BmsLibraryMaintenanceService.EvaluateBmsMaintenanceForInline(
                    file,
                    snapshot,
                    lookupContext,
                    componentReferencesAlreadyApplied: true);
            healthMs = TicksToMilliseconds(maintenanceResult.HealthElapsedTicks);
            encodingMs = TicksToMilliseconds(maintenanceResult.EncodingElapsedTicks);
            encodingReloadMs = TicksToMilliseconds(maintenanceResult.EncodingReloadElapsedTicks);
            encodingReloadCount = maintenanceResult.EncodingReloadCount;
            detectionResult = maintenanceResult.EncodingDetectionResult;
            cacheHitCount = maintenanceResult.CacheHitCount;
            resourceIndexHitCount = maintenanceResult.ResourceIndexHitCount;
            resourceHealthSetCacheHitCount = maintenanceResult.ResourceHealthSetCacheHitCount;
            fileExistsFallbackCount = maintenanceResult.FileExistsFallbackCount;
            completed = file.maintenanceInfo?.IsInformationChecked() == true;
        }
        catch (Exception ex) when (IsInlineMaintenanceRecoverable(ex))
        {
            warning = "inline_maintenance_failed kind=bms path=" + QuoteLogValue(file.path)
                + " exception=" + ex.GetType().Name
                + " message=" + QuoteLogValue(ex.Message);
        }
        finally
        {
            stopwatch.Stop();
            file.ClearResourceReferenceCollections();
        }
        return new InlineMaintenanceItemResult(
            kind: FileDiffChartKind.Bms,
            targetCount: 1,
            successCount: completed ? 1 : 0,
            failedCount: completed ? 0 : 1,
            elapsedMs: stopwatch.ElapsedMilliseconds,
            healthMs: healthMs,
            encodingMs: encodingMs,
            encodingReloadMs: encodingReloadMs,
            encodingReloadCount: encodingReloadCount,
            encodingDetectionResult: detectionResult,
            cacheHitCount: cacheHitCount,
            resourceIndexHitCount: resourceIndexHitCount,
            resourceHealthSetCacheHitCount: resourceHealthSetCacheHitCount,
            fileExistsFallbackCount: fileExistsFallbackCount,
            warningMessage: warning);
    }

    private static InlineMaintenanceItemResult BuildInlineBmsonMaintenance(
        LR2SongDBExtended.bmson_song song,
        ResourceHealthLookupContext lookupContext)
    {
        if (song == null)
        {
            return InlineMaintenanceItemResult.Empty;
        }
        var stopwatch = Stopwatch.StartNew();
        long healthMs = 0L;
        long cacheHitCount = 0L;
        long resourceIndexHitCount = 0L;
        long resourceHealthSetCacheHitCount = 0L;
        long fileExistsFallbackCount = 0L;
        bool completed = false;
        string warning = null;
        try
        {
            BmsLibraryMaintenanceService.MaintenanceEvaluationResult maintenanceResult =
                BmsLibraryMaintenanceService.EvaluateBmsonMaintenanceForInline(song, lookupContext);
            completed = maintenanceResult.MaintenanceInfo?.IsInformationChecked() == true;
            healthMs = TicksToMilliseconds(maintenanceResult.HealthElapsedTicks);
            cacheHitCount = maintenanceResult.CacheHitCount;
            resourceIndexHitCount = maintenanceResult.ResourceIndexHitCount;
            resourceHealthSetCacheHitCount = maintenanceResult.ResourceHealthSetCacheHitCount;
            fileExistsFallbackCount = maintenanceResult.FileExistsFallbackCount;
        }
        catch (Exception ex) when (IsInlineMaintenanceRecoverable(ex))
        {
            warning = "inline_maintenance_failed kind=bmson path=" + QuoteLogValue(song.path)
                + " exception=" + ex.GetType().Name
                + " message=" + QuoteLogValue(ex.Message);
        }
        finally
        {
            stopwatch.Stop();
        }
        return new InlineMaintenanceItemResult(
            kind: FileDiffChartKind.Bmson,
            targetCount: 1,
            successCount: completed ? 1 : 0,
            failedCount: completed ? 0 : 1,
            elapsedMs: stopwatch.ElapsedMilliseconds,
            healthMs: healthMs,
            encodingMs: 0,
            encodingReloadMs: 0,
            encodingReloadCount: 0,
            encodingDetectionResult: null,
            cacheHitCount: cacheHitCount,
            resourceIndexHitCount: resourceIndexHitCount,
            resourceHealthSetCacheHitCount: resourceHealthSetCacheHitCount,
            fileExistsFallbackCount: fileExistsFallbackCount,
            warningMessage: warning);
    }

    private static void ApplyInlineMaintenanceResults(SongTableFileCheckResult result, IEnumerable<InlineMaintenanceItemResult> itemResults)
    {
        if (result == null || itemResults == null)
        {
            return;
        }
        foreach (InlineMaintenanceItemResult itemResult in itemResults)
        {
            if (itemResult == null || itemResult.TargetCount <= 0)
            {
                continue;
            }
            result.InlineMaintenanceTargetCount += itemResult.TargetCount;
            if (itemResult.Kind == FileDiffChartKind.Bmson)
            {
                result.InlineMaintenanceBmsonCount += itemResult.TargetCount;
            }
            else
            {
                result.InlineMaintenanceBmsCount += itemResult.TargetCount;
            }
            result.InlineMaintenanceSuccessCount += itemResult.SuccessCount;
            result.InlineMaintenanceFailedCount += itemResult.FailedCount;
            result.InlineMaintenanceMs += itemResult.ElapsedMs;
            result.InlineHealthWallMs += itemResult.HealthMs;
            result.InlineEncodingWallMs += itemResult.EncodingMs;
            result.InlineEncodingReloadWallMs += itemResult.EncodingReloadMs;
            result.InlineEncodingReloadCount += itemResult.EncodingReloadCount;
            result.InlineEncodingDetectCount += itemResult.EncodingDetectCount;
            result.InlineEncodingFastAsciiCount += itemResult.EncodingFastAsciiCount;
            result.InlineEncodingShiftJisCount += itemResult.EncodingShiftJisCount;
            result.InlineEncodingShiftJisQuestionCount += itemResult.EncodingShiftJisQuestionCount;
            result.InlineEncodingKoreanCount += itemResult.EncodingKoreanCount;
            result.InlineEncodingKoreanQuestionCount += itemResult.EncodingKoreanQuestionCount;
            result.InlineEncodingUtf8Count += itemResult.EncodingUtf8Count;
            result.InlineEncodingUnknownCount += itemResult.EncodingUnknownCount;
            result.InlineEncodingOtherCount += itemResult.EncodingOtherCount;
            result.InlineEncodingMaxItemMs = Math.Max(result.InlineEncodingMaxItemMs, itemResult.EncodingMs);
            result.InlineMaintenanceCacheHitCount += itemResult.CacheHitCount;
            result.InlineMaintenanceResourceIndexHitCount += itemResult.ResourceIndexHitCount;
            result.InlineMaintenanceResourceSetCacheHitCount += itemResult.ResourceHealthSetCacheHitCount;
            result.InlineMaintenanceFileExistsFallbackCount += itemResult.FileExistsFallbackCount;
        }
    }

    private static long SumInlineMaintenanceEncodingMs(IEnumerable<InlineMaintenanceItemResult> itemResults)
    {
        return itemResults?.Where(item => item != null).Sum(item => item.EncodingMs) ?? 0L;
    }

    private static long SumInlineMaintenanceHealthMs(IEnumerable<InlineMaintenanceItemResult> itemResults)
    {
        return itemResults?.Where(item => item != null).Sum(item => item.HealthMs) ?? 0L;
    }

    private static long SumInlineMaintenanceCacheHitCount(IEnumerable<InlineMaintenanceItemResult> itemResults)
    {
        return itemResults?.Where(item => item != null).Sum(item => item.CacheHitCount) ?? 0L;
    }

    private static long SumInlineMaintenanceResourceIndexHitCount(IEnumerable<InlineMaintenanceItemResult> itemResults)
    {
        return itemResults?.Where(item => item != null).Sum(item => item.ResourceIndexHitCount) ?? 0L;
    }

    private static long SumInlineMaintenanceFileExistsFallbackCount(IEnumerable<InlineMaintenanceItemResult> itemResults)
    {
        return itemResults?.Where(item => item != null).Sum(item => item.FileExistsFallbackCount) ?? 0L;
    }

    private static long SumInlineMaintenanceEncodingReloadMs(IEnumerable<InlineMaintenanceItemResult> itemResults)
    {
        return itemResults?.Where(item => item != null).Sum(item => item.EncodingReloadMs) ?? 0L;
    }

    private static long MaxInlineMaintenanceEncodingMs(IEnumerable<InlineMaintenanceItemResult> itemResults)
    {
        return itemResults?.Where(item => item != null).Select(item => item.EncodingMs).DefaultIfEmpty(0L).Max() ?? 0L;
    }

    private static void LogInlineMaintenanceWarnings(IEnumerable<InlineMaintenanceItemResult> itemResults, Action<string> logInstallPerformanceWarn)
    {
        if (logInstallPerformanceWarn == null || itemResults == null)
        {
            return;
        }
        foreach (InlineMaintenanceItemResult itemResult in itemResults)
        {
            if (!string.IsNullOrWhiteSpace(itemResult?.WarningMessage))
            {
                logInstallPerformanceWarn(itemResult.WarningMessage);
            }
        }
    }

    private static bool IsInlineMaintenanceRecoverable(Exception ex)
    {
        return ex != null;
    }

    private static void UpdateHighWatermark(ref int highWatermark, int value)
    {
        int observed;
        do
        {
            observed = highWatermark;
            if (value <= observed)
            {
                return;
            }
        }
        while (Interlocked.CompareExchange(ref highWatermark, value, observed) != observed);
    }

    private static void UpdateMaxTicks(ref long maxTicks, long value)
    {
        long observed;
        do
        {
            observed = Interlocked.Read(ref maxTicks);
            if (value <= observed)
            {
                return;
            }
        }
        while (Interlocked.CompareExchange(ref maxTicks, value, observed) != observed);
    }

    private static void AddWithWait<T>(
        BlockingCollection<T> queue,
        T item,
        ref long waitTicks,
        PipelineExceptionSignal exceptionSignal = null)
    {
        if (queue == null)
        {
            return;
        }
        long start = Stopwatch.GetTimestamp();
        while (true)
        {
            ThrowIfSignaled(exceptionSignal);
            try
            {
                if (queue.TryAdd(item, 100))
                {
                    long elapsed = Stopwatch.GetTimestamp() - start;
                    if (elapsed > 0L)
                    {
                        Interlocked.Add(ref waitTicks, elapsed);
                    }
                    return;
                }
            }
            catch (InvalidOperationException)
            {
                ThrowIfSignaled(exceptionSignal);
                throw;
            }
        }
    }

    private static void AddCommitChunkWithWait(
        BlockingCollection<FileScanDiffCommitChunk> queue,
        FileScanDiffCommitChunk chunk,
        PipelineExceptionSignal writerException,
        ref long waitTicks)
    {
        if (queue == null || chunk == null || !chunk.HasItems)
        {
            return;
        }

        long start = Stopwatch.GetTimestamp();
        while (true)
        {
            ThrowIfSignaled(writerException);
            try
            {
                if (queue.TryAdd(chunk, 100))
                {
                    long elapsed = Stopwatch.GetTimestamp() - start;
                    if (elapsed > 0L)
                    {
                        Interlocked.Add(ref waitTicks, elapsed);
                    }
                    return;
                }
            }
            catch (InvalidOperationException)
            {
                ThrowIfSignaled(writerException);
                throw;
            }
        }
    }

    private static void ThrowIfSignaled(PipelineExceptionSignal exceptionSignal)
    {
        Exception exception = exceptionSignal?.Get();
        if (exception != null)
        {
            throw new AggregateException(exception);
        }
    }

    private static void CapturePipelineException(
        Exception exception,
        PipelineExceptionSignal exceptionSignal,
        ref Exception pipelineFailure)
    {
        if (exception == null)
        {
            return;
        }
        pipelineFailure ??= exception;
        exceptionSignal?.Set(exception);
    }

    private static void CompleteAddingSilently<T>(BlockingCollection<T> queue)
    {
        if (queue == null)
        {
            return;
        }
        try
        {
            queue.CompleteAdding();
        }
        catch (InvalidOperationException)
        {
        }
    }

    private static void EnqueuePostParseBatch(
        BlockingCollection<FileDiffParsedBatch> queue,
        FileDiffParsedBatch batch,
        PipelineExceptionSignal pipelineException,
        ref long waitTicks,
        ref Exception postParseException)
    {
        ThrowIfSignaled(pipelineException);
        Exception capturedException = Volatile.Read(ref postParseException);
        if (capturedException != null)
        {
            throw new AggregateException(capturedException);
        }
        try
        {
            AddWithWait(queue, batch, ref waitTicks, pipelineException);
        }
        catch (InvalidOperationException) when (Volatile.Read(ref postParseException) != null)
        {
            throw new AggregateException(Volatile.Read(ref postParseException));
        }
    }

    private sealed class PipelineExceptionSignal
    {
        private Exception exception;

        public Exception Get()
        {
            return Volatile.Read(ref exception);
        }

        public void Set(Exception value)
        {
            if (value != null)
            {
                Interlocked.CompareExchange(ref exception, value, null);
            }
        }
    }

    private sealed class FileDiffCommitWriterItem : IDisposable
    {
        private readonly ManualResetEventSlim completion;

        private FileDiffCommitWriterItem(FileScanDiffCommitChunk chunk, bool isBarrier)
        {
            Chunk = chunk;
            IsBarrier = isBarrier;
            completion = isBarrier ? new ManualResetEventSlim(false) : null;
        }

        public FileScanDiffCommitChunk Chunk { get; }

        public bool IsBarrier { get; }

        public Exception Exception { get; private set; }

        public static FileDiffCommitWriterItem CreateChunk(FileScanDiffCommitChunk chunk)
        {
            return new FileDiffCommitWriterItem(chunk, isBarrier: false);
        }

        public static FileDiffCommitWriterItem CreateBarrier()
        {
            return new FileDiffCommitWriterItem(null, isBarrier: true);
        }

        public bool Wait(int millisecondsTimeout)
        {
            return completion?.Wait(millisecondsTimeout) == true;
        }

        public void SignalComplete(Exception exception)
        {
            Exception = exception;
            completion?.Set();
        }

        public void Dispose()
        {
            completion?.Dispose();
        }
    }

    private sealed class FileDiffStreamingCommitContext : IDisposable
    {
        private readonly BmsLibraryDbGateway dbGateway;

        private readonly BmsLibraryOptionsSnapshot options;

        private readonly SongTableFileCheckResult result;

        private readonly Action<string> logInstallPerformance;

        private readonly Action<string> logInstallPerformanceWarn;

        private readonly Action<IReadOnlyList<LR2SongDBExtended.chart_info>> inlineChartInfoRowsCommitted;

        private readonly int chunkSize;

        private readonly BlockingCollection<FileDiffCommitWriterItem> writerQueue;

        private readonly Task writerTask;

        private readonly Stopwatch lifetimeStopwatch = Stopwatch.StartNew();

        private readonly PipelineExceptionSignal writerException = new();

        private FileScanDiffCommitChunk pendingChunk = new();

        private LR2SongDBExtended songDb;

        private bool pragmasApplied;

        private bool writerQueueCompleted;

        private bool writerWaitCompleted;

        private long writerQueueWaitTicks;

        private int writerQueueHighWatermark;

        public FileDiffStreamingCommitContext(
            BmsLibraryDbGateway dbGateway,
            BmsLibraryOptionsSnapshot options,
            SongTableFileCheckResult result,
            Action<string> logInstallPerformance,
            Action<string> logInstallPerformanceWarn,
            Action<IReadOnlyList<LR2SongDBExtended.chart_info>> inlineChartInfoRowsCommitted)
        {
            this.dbGateway = dbGateway;
            this.options = options;
            this.result = result;
            this.logInstallPerformance = logInstallPerformance;
            this.logInstallPerformanceWarn = logInstallPerformanceWarn;
            this.inlineChartInfoRowsCommitted = inlineChartInfoRowsCommitted;
            chunkSize = Math.Max(1, result?.DbCommitChunkSize ?? DefaultFileDiffCommitChunkSize);
            int writerQueueCapacity = Math.Max(1, DefaultFileDiffCommitWriterQueueCapacity);
            if (result != null)
            {
                result.CommitWriterQueueCapacity = writerQueueCapacity;
            }
            writerQueue = new BlockingCollection<FileDiffCommitWriterItem>(writerQueueCapacity);
            writerTask = Task.Run(WriterLoop);
        }

        public bool ShouldPruneCommittedInlineRows => inlineChartInfoRowsCommitted != null;

        public void AddDeletedBmsPath(string path)
        {
            pendingChunk.AddDeletedBmsPath(path);
            FlushIfNeeded();
        }

        public void AddDeletedBmsonPath(string path)
        {
            pendingChunk.AddDeletedBmsonPath(path);
            FlushIfNeeded();
        }

        public void AddChunk(FileScanDiffCommitChunk chunk)
        {
            if (chunk == null || !chunk.HasItems)
            {
                return;
            }

            Dictionary<string, Queue<BMSFileMaintenanceInfo>> maintenanceByPath =
                BmsLibraryInitializationService.BuildQueueByMd5(chunk.MaintenanceInfoRows, row => row?.path);
            Dictionary<string, Queue<LR2SongDBExtended.chart_info>> chartInfoByMd5 =
                BmsLibraryInitializationService.BuildQueueByMd5(chunk.ChartInfoRows, row => row?.md5);
            Dictionary<string, Queue<LR2SongDBExtended.chart_info>> appliedChartInfoByMd5 =
                BmsLibraryInitializationService.BuildQueueByMd5(chunk.AppliedChartInfoRows, row => row?.md5);
            Dictionary<string, Queue<LR2SongDBExtended.chart_info_parse_failure>> failureByMd5 =
                BmsLibraryInitializationService.BuildQueueByMd5(chunk.ParseFailureRows, row => row?.md5);
            var failureDeleteMd5s = new HashSet<string>(chunk.ParseFailureDeleteMd5s, StringComparer.OrdinalIgnoreCase);

            foreach (string path in chunk.DeletedBmsPaths)
            {
                pendingChunk.AddDeletedBmsPath(path);
                FlushIfNeeded();
            }
            foreach (BMSFile file in chunk.AddedBmsFiles)
            {
                pendingChunk.AddAddedBmsFile(file);
                AddMatchingMaintenanceInfoRows(maintenanceByPath, file?.path);
                BmsLibraryInitializationService.AttachInlineChartInfoRows(
                    pendingChunk,
                    file?.hash,
                    chartInfoByMd5,
                    appliedChartInfoByMd5,
                    failureByMd5,
                    failureDeleteMd5s);
                FlushIfNeeded();
            }
            foreach (BmsDateOnlyUpdate update in chunk.UpdatedBmsDates)
            {
                if (update != null)
                {
                    pendingChunk.AddUpdatedBmsMetadata(update.Path, update.Date, update.TextFlag);
                    FlushIfNeeded();
                }
            }
            foreach (string path in chunk.DeletedBmsonPaths)
            {
                pendingChunk.AddDeletedBmsonPath(path);
                FlushIfNeeded();
            }
            foreach (LR2SongDBExtended.bmson_song song in chunk.UpsertBmsonSongs)
            {
                pendingChunk.AddUpsertBmsonSong(song);
                AddMatchingMaintenanceInfoRows(maintenanceByPath, song?.path);
                BmsLibraryInitializationService.AttachInlineChartInfoRows(
                    pendingChunk,
                    song?.md5,
                    chartInfoByMd5,
                    appliedChartInfoByMd5,
                    failureByMd5,
                    failureDeleteMd5s);
                FlushIfNeeded();
            }

            AddRemainingMaintenanceInfoRows(maintenanceByPath);
            AddRemainingChartInfoRows(chartInfoByMd5, appliedChartInfoByMd5, failureByMd5, failureDeleteMd5s);
            FlushIfNeeded();
        }

        public void Flush()
        {
            if (pendingChunk.HasItems)
            {
                EnqueuePendingChunk();
            }
            WaitForWriterBarrier();
        }

        public void RestoreSongUserColumns(IEnumerable<KeyValuePair<string, Lr2SongUserColumns>> userColumnsByPath)
        {
            List<KeyValuePair<string, Lr2SongUserColumns>> rows = [.. (userColumnsByPath ?? [])
                .Where(pair => !string.IsNullOrWhiteSpace(pair.Key) && pair.Value != null)];
            if (rows.Count == 0)
            {
                return;
            }

            EnsureSongDb();
            int chunkNumber = result.DbCommitChunks + 1;
            logInstallPerformance?.Invoke("song_tbl_file_check user_column_restore_start"
                + " chunk=" + chunkNumber
                + " rows=" + rows.Count);
            string savepoint = songDb.SaveTransactionPoint();
            var restoreStopwatch = Stopwatch.StartNew();
            try
            {
                foreach (KeyValuePair<string, Lr2SongUserColumns> row in rows)
                {
                    BmsLibraryDbGateway.ApplySongUserColumns(songDb, row.Key, row.Value);
                }
                songDb.Commit();
            }
            catch (Exception ex)
            {
                restoreStopwatch.Stop();
                try
                {
                    songDb.RollbackTo(savepoint);
                }
                catch
                {
                }
                logInstallPerformanceWarn?.Invoke("song_tbl_file_check user_column_restore_failed"
                    + " chunk=" + chunkNumber
                    + " rows=" + rows.Count
                    + " elapsedMs=" + restoreStopwatch.ElapsedMilliseconds
                    + " exception=" + ex.GetType().Name
                    + " message=" + QuoteLogValue(ex.Message));
                ReleaseSongDb();
                throw;
            }
            restoreStopwatch.Stop();
            result.DbCommitChunks++;
            result.DbCommitMaxChunkMs = Math.Max(result.DbCommitMaxChunkMs, restoreStopwatch.ElapsedMilliseconds);
            result.DbCommitMs += restoreStopwatch.ElapsedMilliseconds;
            logInstallPerformance?.Invoke("song_tbl_file_check user_column_restore_done"
                + " chunk=" + chunkNumber
                + " rows=" + rows.Count
                + " elapsedMs=" + restoreStopwatch.ElapsedMilliseconds);
            ReleaseSongDb();
        }

        public void Dispose()
        {
            try
            {
                CompleteWriterQueue();
                WaitWriterTask();
            }
            catch (Exception ex)
            {
                logInstallPerformanceWarn?.Invoke("song_tbl_file_check commit_writer_dispose_failed"
                    + " exception=" + ex.GetType().Name
                    + " message=" + QuoteLogValue(ex.Message));
            }
            finally
            {
                writerQueue?.Dispose();
                ReleaseSongDb();
            }
        }

        public void ReleaseSongDb()
        {
            songDb?.Dispose();
            songDb = null;
            pragmasApplied = false;
        }

        private void FlushIfNeeded()
        {
            if (pendingChunk.MutationCount >= chunkSize)
            {
                EnqueuePendingChunk();
            }
        }

        private void EnqueuePendingChunk()
        {
            if (!pendingChunk.HasItems)
            {
                return;
            }
            FileScanDiffCommitChunk chunk = pendingChunk;
            pendingChunk = new FileScanDiffCommitChunk();
            AddWriterItemWithWait(FileDiffCommitWriterItem.CreateChunk(chunk));
        }

        private void WaitForWriterBarrier()
        {
            ThrowIfWriterFailed();
            using FileDiffCommitWriterItem barrier = FileDiffCommitWriterItem.CreateBarrier();
            AddWriterItemWithWait(barrier);
            while (!barrier.Wait(100))
            {
                ThrowIfWriterFailed();
                if (writerTask.IsCompleted)
                {
                    WaitWriterTask();
                    ThrowIfWriterFailed();
                    throw new InvalidOperationException("File diff commit writer completed before the flush barrier.");
                }
            }
            if (barrier.Exception != null)
            {
                throw new AggregateException(barrier.Exception);
            }
            ThrowIfWriterFailed();
            if (result != null)
            {
                result.CommitWriterQueueWaitMs = TicksToMilliseconds(Interlocked.Read(ref writerQueueWaitTicks));
            }
        }

        private void AddWriterItemWithWait(FileDiffCommitWriterItem item)
        {
            if (item == null)
            {
                return;
            }

            long start = Stopwatch.GetTimestamp();
            while (true)
            {
                ThrowIfWriterFailed();
                try
                {
                    if (writerQueue.TryAdd(item, 100))
                    {
                        UpdateHighWatermark(ref writerQueueHighWatermark, writerQueue.Count);
                        if (result != null)
                        {
                            result.CommitWriterQueueHighWatermark = Math.Max(
                                result.CommitWriterQueueHighWatermark,
                                Volatile.Read(ref writerQueueHighWatermark));
                        }
                        long elapsed = Stopwatch.GetTimestamp() - start;
                        if (elapsed > 0L)
                        {
                            Interlocked.Add(ref writerQueueWaitTicks, elapsed);
                        }
                        return;
                    }
                }
                catch (InvalidOperationException)
                {
                    ThrowIfWriterFailed();
                    throw;
                }
            }
        }

        private void WriterLoop()
        {
            try
            {
                foreach (FileDiffCommitWriterItem item in writerQueue.GetConsumingEnumerable())
                {
                    if (item == null)
                    {
                        continue;
                    }
                    if (item.IsBarrier)
                    {
                        item.SignalComplete(null);
                        continue;
                    }
                    CommitChunk(item.Chunk);
                }
            }
            catch (Exception ex)
            {
                writerException.Set(ex);
                throw;
            }
            finally
            {
                ReleaseSongDb();
            }
        }

        private void CompleteWriterQueue()
        {
            if (writerQueueCompleted)
            {
                return;
            }
            try
            {
                writerQueue.CompleteAdding();
            }
            catch (InvalidOperationException)
            {
            }
            writerQueueCompleted = true;
        }

        private void WaitWriterTask()
        {
            if (writerWaitCompleted)
            {
                ThrowIfWriterFailed();
                return;
            }
            try
            {
                writerTask.Wait();
                writerWaitCompleted = true;
            }
            catch (AggregateException ex)
            {
                writerException.Set(ex.InnerException ?? ex);
                throw;
            }
            ThrowIfWriterFailed();
        }

        private void ThrowIfWriterFailed()
        {
            Exception exception = writerException.Get();
            if (exception != null)
            {
                throw new AggregateException(exception);
            }
        }

        private void AddMatchingMaintenanceInfoRows(
            Dictionary<string, Queue<BMSFileMaintenanceInfo>> maintenanceByPath,
            string path)
        {
            if (string.IsNullOrWhiteSpace(path)
                || maintenanceByPath == null
                || !maintenanceByPath.TryGetValue(path, out Queue<BMSFileMaintenanceInfo> rows))
            {
                return;
            }
            while (rows.Count > 0)
            {
                pendingChunk.AddMaintenanceInfoRow(rows.Dequeue());
            }
            maintenanceByPath.Remove(path);
        }

        private void AddRemainingMaintenanceInfoRows(Dictionary<string, Queue<BMSFileMaintenanceInfo>> maintenanceByPath)
        {
            if (maintenanceByPath == null || maintenanceByPath.Count == 0)
            {
                return;
            }
            foreach (Queue<BMSFileMaintenanceInfo> rows in maintenanceByPath.Values)
            {
                while (rows.Count > 0)
                {
                    pendingChunk.AddMaintenanceInfoRow(rows.Dequeue(), countMutation: true);
                    FlushIfNeeded();
                }
            }
        }

        private void AddRemainingChartInfoRows(
            Dictionary<string, Queue<LR2SongDBExtended.chart_info>> chartInfoByMd5,
            Dictionary<string, Queue<LR2SongDBExtended.chart_info>> appliedChartInfoByMd5,
            Dictionary<string, Queue<LR2SongDBExtended.chart_info_parse_failure>> failureByMd5,
            HashSet<string> failureDeleteMd5s)
        {
            foreach (Queue<LR2SongDBExtended.chart_info> rows in chartInfoByMd5.Values)
            {
                while (rows.Count > 0)
                {
                    pendingChunk.AddChartInfoRow(rows.Dequeue());
                    FlushIfNeeded();
                }
            }
            foreach (Queue<LR2SongDBExtended.chart_info> rows in appliedChartInfoByMd5.Values)
            {
                while (rows.Count > 0)
                {
                    pendingChunk.AddAppliedChartInfoRow(rows.Dequeue());
                }
            }
            foreach (Queue<LR2SongDBExtended.chart_info_parse_failure> rows in failureByMd5.Values)
            {
                while (rows.Count > 0)
                {
                    pendingChunk.AddParseFailureRow(rows.Dequeue());
                    FlushIfNeeded();
                }
            }
            foreach (string md5 in failureDeleteMd5s)
            {
                pendingChunk.AddParseFailureDeleteMd5(md5);
                FlushIfNeeded();
            }
        }

        private void CommitChunk(FileScanDiffCommitChunk chunk)
        {
            if (dbGateway == null || result == null || chunk == null || !chunk.HasItems)
            {
                return;
            }
            EnsureSongDb();
            int chunkNumber = result.DbCommitChunks + 1;
            if (chunkNumber == 1 && result.DbCommitFirstChunkStartMs <= 0L)
            {
                result.DbCommitFirstChunkStartMs = lifetimeStopwatch.ElapsedMilliseconds;
            }
            logInstallPerformance?.Invoke("song_tbl_file_check db_commit_chunk_start chunk=" + chunkNumber
                + " deleted=" + chunk.DeletedBmsPaths.Count
                + " added=" + chunk.AddedBmsFiles.Count
                + " bmsDateOnly=" + chunk.UpdatedBmsDates.Count
                + " bmsonDeleted=" + chunk.DeletedBmsonPaths.Count
                + " bmsonUpsert=" + chunk.UpsertBmsonSongs.Count
                + " maintenance=" + chunk.MaintenanceInfoRows.Count
                + " chartInfo=" + chunk.ChartInfoRows.Count
                + " appliedChartInfo=" + chunk.AppliedChartInfoRows.Count
                + " failures=" + chunk.ParseFailureRows.Count
                + " failureDeletes=" + chunk.ParseFailureDeleteMd5s.Count
                + " mutations=" + chunk.MutationCount);
            string savepoint = songDb.SaveTransactionPoint();
            var chunkStopwatch = Stopwatch.StartNew();
            var metrics = new FileScanDiffCommitMetrics();
            long sqliteCommitMs = 0L;
            try
            {
                metrics = BmsLibraryDbGateway.CommitFileScanDiffChunk(songDb, chunk);
                var commitStopwatch = Stopwatch.StartNew();
                songDb.Commit();
                commitStopwatch.Stop();
                sqliteCommitMs = commitStopwatch.ElapsedMilliseconds;
            }
            catch (Exception ex)
            {
                chunkStopwatch.Stop();
                try
                {
                    songDb.RollbackTo(savepoint);
                }
                catch
                {
                }
                logInstallPerformanceWarn?.Invoke("song_tbl_file_check db_commit_chunk_failed chunk=" + chunkNumber
                    + " elapsedMs=" + chunkStopwatch.ElapsedMilliseconds
                    + " exception=" + ex.GetType().Name
                    + " message=" + QuoteLogValue(ex.Message));
                ReleaseSongDb();
                throw;
            }
            chunkStopwatch.Stop();
            result.DbCommitChunks++;
            result.DbCommitMaxChunkMs = Math.Max(result.DbCommitMaxChunkMs, chunkStopwatch.ElapsedMilliseconds);
            result.DbCommitMs += chunkStopwatch.ElapsedMilliseconds;
            result.DbCommitApplyMs += metrics.ApplyMs;
            result.DbCommitSchemaMs += metrics.SchemaMs;
            result.DbCommitBmsDeleteMs += metrics.BmsDeleteMs;
            result.DbCommitBmsDateUpdateMs += metrics.BmsDateUpdateMs;
            result.DbCommitBmsUpsertMs += metrics.BmsUpsertMs;
            result.DbCommitBmsChangedCount += metrics.BmsChangedCount;
            result.DbCommitBmsonDeleteMs += metrics.BmsonDeleteMs;
            result.DbCommitBmsonUpsertMs += metrics.BmsonUpsertMs;
            result.DbCommitMaintenanceUpsertMs += metrics.MaintenanceUpsertMs;
            result.DbCommitChartInfoMs += metrics.ChartInfoMs;
            result.DbCommitSqliteCommitMs += sqliteCommitMs;
            logInstallPerformance?.Invoke("song_tbl_file_check db_commit_chunk_done chunk=" + chunkNumber
                + " elapsedMs=" + chunkStopwatch.ElapsedMilliseconds
                + " applyMs=" + metrics.ApplyMs
                + " schemaMs=" + metrics.SchemaMs
                + " bmsDeleteMs=" + metrics.BmsDeleteMs
                + " bmsDateUpdateMs=" + metrics.BmsDateUpdateMs
                + " bmsUpsertMs=" + metrics.BmsUpsertMs
                + " bmsChanged=" + metrics.BmsChangedCount
                + " bmsonDeleteMs=" + metrics.BmsonDeleteMs
                + " bmsonUpsertMs=" + metrics.BmsonUpsertMs
                + " maintenanceUpsertMs=" + metrics.MaintenanceUpsertMs
                + " chartInfoMs=" + metrics.ChartInfoMs
                + " sqliteCommitMs=" + sqliteCommitMs
                + " deleted=" + chunk.DeletedBmsPaths.Count
                + " added=" + chunk.AddedBmsFiles.Count
                + " bmsDateOnly=" + chunk.UpdatedBmsDates.Count
                + " bmsonDeleted=" + chunk.DeletedBmsonPaths.Count
                + " bmsonUpsert=" + chunk.UpsertBmsonSongs.Count
                + " maintenance=" + chunk.MaintenanceInfoRows.Count
                + " chartInfo=" + chunk.ChartInfoRows.Count
                + " appliedChartInfo=" + chunk.AppliedChartInfoRows.Count
                + " failures=" + chunk.ParseFailureRows.Count
                + " failureDeletes=" + chunk.ParseFailureDeleteMd5s.Count);
            if (chunk.ChartInfoRows.Count > 0 && inlineChartInfoRowsCommitted != null)
            {
                result.InlineChartInfoIndexPublishedCount += chunk.ChartInfoRows.Count;
                inlineChartInfoRowsCommitted(chunk.ChartInfoRows);
            }
            ReleaseSongDb();
        }

        private void EnsureSongDb()
        {
            songDb ??= dbGateway.OpenSongDb();
            if (!pragmasApplied)
            {
                List<string> pragmas = songDb.TryApplyReadOptimizedPragmas(options?.EnableReadOptimizedPragmas ?? false);
                result.Pragmas.AddRange(pragmas);
                if (pragmas.Count > 0)
                {
                    logInstallPerformance?.Invoke("db_read_pragmas scope=song_tbl_file_check " + string.Join(" ", pragmas));
                }
                pragmasApplied = true;
            }
        }
    }

    private static Dictionary<string, Queue<T>> BuildQueueByMd5<T>(IEnumerable<T> rows, Func<T, string> md5Selector)
    {
        var result = new Dictionary<string, Queue<T>>(StringComparer.OrdinalIgnoreCase);
        foreach (T row in rows ?? [])
        {
            string md5 = md5Selector(row);
            if (string.IsNullOrWhiteSpace(md5))
            {
                continue;
            }
            if (!result.TryGetValue(md5, out Queue<T> queue))
            {
                queue = new Queue<T>();
                result[md5] = queue;
            }
            queue.Enqueue(row);
        }
        return result;
    }

    private static void AttachInlineChartInfoRows(
        FileScanDiffCommitChunk chunk,
        string md5,
        Dictionary<string, Queue<LR2SongDBExtended.chart_info>> chartInfoByMd5,
        Dictionary<string, Queue<LR2SongDBExtended.chart_info>> appliedChartInfoByMd5,
        Dictionary<string, Queue<LR2SongDBExtended.chart_info_parse_failure>> failureByMd5,
        HashSet<string> failureDeleteMd5s)
    {
        if (chunk == null || string.IsNullOrWhiteSpace(md5))
        {
            return;
        }
        if (chartInfoByMd5.TryGetValue(md5, out Queue<LR2SongDBExtended.chart_info> chartInfoRows))
        {
            while (chartInfoRows.Count > 0)
            {
                chunk.AddChartInfoRow(chartInfoRows.Dequeue(), countMutation: false);
            }
            chartInfoByMd5.Remove(md5);
        }
        if (appliedChartInfoByMd5.TryGetValue(md5, out Queue<LR2SongDBExtended.chart_info> appliedRows))
        {
            while (appliedRows.Count > 0)
            {
                chunk.AddAppliedChartInfoRow(appliedRows.Dequeue());
            }
            appliedChartInfoByMd5.Remove(md5);
        }
        if (failureByMd5.TryGetValue(md5, out Queue<LR2SongDBExtended.chart_info_parse_failure> failureRows))
        {
            while (failureRows.Count > 0)
            {
                chunk.AddParseFailureRow(failureRows.Dequeue(), countMutation: false);
            }
            failureByMd5.Remove(md5);
        }
        if (failureDeleteMd5s.Remove(md5))
        {
            chunk.AddParseFailureDeleteMd5(md5, countMutation: false);
        }
    }

    private static Dictionary<string, LR2SongDBExtended.chart_info> BuildAppliedChartInfoByPath(
        IEnumerable<InlineBmsParseCandidate> candidates,
        IEnumerable<LR2SongDBExtended.chart_info> appliedRows)
    {
        var rowsBySha256 = new Dictionary<string, LR2SongDBExtended.chart_info>(StringComparer.OrdinalIgnoreCase);
        foreach (LR2SongDBExtended.chart_info row in appliedRows ?? [])
        {
            if (row == null || string.IsNullOrWhiteSpace(row.sha256))
            {
                continue;
            }
            rowsBySha256[row.sha256] = row;
        }
        if (rowsBySha256.Count == 0)
        {
            return new Dictionary<string, LR2SongDBExtended.chart_info>(StringComparer.OrdinalIgnoreCase);
        }

        var result = new Dictionary<string, LR2SongDBExtended.chart_info>(StringComparer.OrdinalIgnoreCase);
        foreach (InlineBmsParseCandidate candidate in candidates ?? [])
        {
            if (candidate?.File == null || string.IsNullOrWhiteSpace(candidate.Path) || string.IsNullOrWhiteSpace(candidate.File.sha256))
            {
                continue;
            }
            if (rowsBySha256.TryGetValue(candidate.File.sha256, out LR2SongDBExtended.chart_info row))
            {
                result[candidate.Path] = row;
            }
        }
        return result;
    }

    private static LR2SongDBExtended.chart_info ResolveAppliedChartInfo(
        InlineBmsParseCandidate candidate,
        Dictionary<string, LR2SongDBExtended.chart_info> appliedChartInfoByPath)
    {
        if (candidate == null
            || string.IsNullOrWhiteSpace(candidate.Path)
            || appliedChartInfoByPath == null
            || !appliedChartInfoByPath.TryGetValue(candidate.Path, out LR2SongDBExtended.chart_info row))
        {
            return null;
        }
        return row;
    }

    private static void AddRemainingInlineRows(
        List<FileScanDiffCommitChunk> chunks,
        ref FileScanDiffCommitChunk current,
        Dictionary<string, Queue<LR2SongDBExtended.chart_info>> chartInfoByMd5,
        Dictionary<string, Queue<LR2SongDBExtended.chart_info>> appliedChartInfoByMd5,
        Dictionary<string, Queue<LR2SongDBExtended.chart_info_parse_failure>> failureByMd5,
        HashSet<string> failureDeleteMd5s,
        int chunkSize)
    {
        foreach (Queue<LR2SongDBExtended.chart_info> queue in chartInfoByMd5.Values)
        {
            while (queue.Count > 0)
            {
                current.AddChartInfoRow(queue.Dequeue());
                if (current.MutationCount >= chunkSize)
                {
                    chunks.Add(current);
                    current = new FileScanDiffCommitChunk();
                }
            }
        }
        foreach (Queue<LR2SongDBExtended.chart_info> queue in appliedChartInfoByMd5.Values)
        {
            while (queue.Count > 0)
            {
                current.AddAppliedChartInfoRow(queue.Dequeue());
                if (current.MutationCount >= chunkSize)
                {
                    chunks.Add(current);
                    current = new FileScanDiffCommitChunk();
                }
            }
        }
        foreach (Queue<LR2SongDBExtended.chart_info_parse_failure> queue in failureByMd5.Values)
        {
            while (queue.Count > 0)
            {
                current.AddParseFailureRow(queue.Dequeue());
                if (current.MutationCount >= chunkSize)
                {
                    chunks.Add(current);
                    current = new FileScanDiffCommitChunk();
                }
            }
        }
        foreach (string md5 in failureDeleteMd5s)
        {
            current.AddParseFailureDeleteMd5(md5);
            if (current.MutationCount >= chunkSize)
            {
                chunks.Add(current);
                current = new FileScanDiffCommitChunk();
            }
        }
    }

    private ChartInfoInlineBuildResult BuildInlineBmsChartInfo(
        IReadOnlyList<InlineBmsParseCandidate> candidates,
        BmsLibraryDbGateway dbGateway,
        IDictionary<string, LR2SongDBExtended.chart_info_parse_failure> currentFailures,
        int chartInfoParserDegree,
        int inlineChartInfoBatchSize,
        IReadOnlyDictionary<string, LR2SongDBExtended.chart_info> currentChartInfoRowsBySha256,
        Action<string> logInstallPerformance,
        Action<string> logInstallPerformanceWarn)
    {
        if (candidates == null || candidates.Count == 0)
        {
            return new ChartInfoInlineBuildResult();
        }
        List<InlineBmsParseCandidate> parsedCandidates = [.. candidates.Where(candidate => candidate?.File != null && candidate.Snapshot != null)];
        if (parsedCandidates.Count == 0)
        {
            return new ChartInfoInlineBuildResult();
        }
        var inlineBuildService = new ChartInfoInlineBuildService(chartInfoBuildService, chartInfoParserDegree, inlineChartInfoBatchSize, currentChartInfoRowsBySha256);
        return inlineBuildService.BuildForSnapshots(
            dbGateway,
            parsedCandidates.Select(candidate => InlineChartSnapshotTarget.FromBmsFile(candidate.File, candidate.Snapshot)),
            currentFailures,
            logInstallPerformance,
            logInstallPerformanceWarn);
    }

    private ChartInfoInlineBuildResult BuildInlineBmsonChartInfo(
        IReadOnlyList<InlineBmsonParseCandidate> candidates,
        BmsLibraryDbGateway dbGateway,
        IDictionary<string, LR2SongDBExtended.chart_info_parse_failure> currentFailures,
        int chartInfoParserDegree,
        int inlineChartInfoBatchSize,
        IReadOnlyDictionary<string, LR2SongDBExtended.chart_info> currentChartInfoRowsBySha256,
        Action<string> logInstallPerformance,
        Action<string> logInstallPerformanceWarn)
    {
        if (candidates == null || candidates.Count == 0)
        {
            return new ChartInfoInlineBuildResult();
        }
        List<InlineBmsonParseCandidate> parsedCandidates = [.. candidates.Where(candidate => candidate?.Song != null && candidate.Snapshot != null)];
        if (parsedCandidates.Count == 0)
        {
            return new ChartInfoInlineBuildResult();
        }
        var inlineBuildService = new ChartInfoInlineBuildService(chartInfoBuildService, chartInfoParserDegree, inlineChartInfoBatchSize, currentChartInfoRowsBySha256);
        return inlineBuildService.BuildForSnapshots(
            dbGateway,
            parsedCandidates.Select(candidate => InlineChartSnapshotTarget.FromBmsonSong(candidate.Song, candidate.Snapshot)),
            currentFailures,
            logInstallPerformance,
            logInstallPerformanceWarn);
    }

    private static void AddInlineChartInfoBuildResult(ChartInfoInlineBuildResult total, ChartInfoInlineBuildResult source)
    {
        if (total == null || source == null)
        {
            return;
        }
        total.ChartInfoRows.AddRange(source.ChartInfoRows);
        total.DigestChanges.AddRange(source.DigestChanges);
        total.AppliedRows.AddRange(source.AppliedRows);
        total.ParseFailureRows.AddRange(source.ParseFailureRows);
        foreach (string md5 in source.ParseFailureDeleteMd5s)
        {
            if (!total.ParseFailureDeleteMd5s.Contains(md5, StringComparer.OrdinalIgnoreCase))
            {
                total.ParseFailureDeleteMd5s.Add(md5);
            }
        }
        total.TargetCount += source.TargetCount;
        total.SuccessCount += source.SuccessCount;
        total.CurrentSkippedCount += source.CurrentSkippedCount;
        total.FailureSkippedCount += source.FailureSkippedCount;
        total.ParseFailedCount += source.ParseFailedCount;
        total.FailurePersistedCount += source.FailurePersistedCount;
        total.FailureClearedCount += source.FailureClearedCount;
        total.ReadFailedCount += source.ReadFailedCount;
        total.ParseMs += source.ParseMs;
    }

    private void ProcessInlineBmsChartInfo(
        IReadOnlyList<InlineBmsParseCandidate> candidates,
        BmsLibraryDbGateway dbGateway,
        IDictionary<string, LR2SongDBExtended.chart_info_parse_failure> currentFailures,
        SongTableFileCheckResult result,
        IReadOnlyDictionary<string, LR2SongDBExtended.chart_info> currentChartInfoRowsBySha256,
        Action<string> logInstallPerformance,
        Action<string> logInstallPerformanceWarn)
    {
        if (candidates == null || candidates.Count == 0)
        {
            return;
        }
        List<InlineBmsParseCandidate> parsedCandidates = [.. candidates.Where(candidate => candidate?.File != null && candidate.Snapshot != null)];
        if (parsedCandidates.Count == 0)
        {
            return;
        }
        var inlineBuildService = new ChartInfoInlineBuildService(chartInfoBuildService, result.FileDiffParserDegree, result.InlineChartInfoBatchSize, currentChartInfoRowsBySha256);
        ChartInfoInlineBuildResult inlineResult = inlineBuildService.BuildForSnapshots(
            dbGateway,
            parsedCandidates.Select(candidate => InlineChartSnapshotTarget.FromBmsFile(candidate.File, candidate.Snapshot)),
            currentFailures,
            logInstallPerformance,
            logInstallPerformanceWarn);
        ApplyInlineChartInfoResult(result, inlineResult);
    }

    private void ProcessInlineBmsonChartInfo(
        IReadOnlyList<InlineBmsonParseCandidate> candidates,
        BmsLibraryDbGateway dbGateway,
        IDictionary<string, LR2SongDBExtended.chart_info_parse_failure> currentFailures,
        SongTableFileCheckResult result,
        IReadOnlyDictionary<string, LR2SongDBExtended.chart_info> currentChartInfoRowsBySha256,
        Action<string> logInstallPerformance,
        Action<string> logInstallPerformanceWarn)
    {
        if (candidates == null || candidates.Count == 0)
        {
            return;
        }
        List<InlineBmsonParseCandidate> parsedCandidates = [.. candidates.Where(candidate => candidate?.Song != null && candidate.Snapshot != null)];
        if (parsedCandidates.Count == 0)
        {
            return;
        }
        var inlineBuildService = new ChartInfoInlineBuildService(chartInfoBuildService, result.FileDiffParserDegree, result.InlineChartInfoBatchSize, currentChartInfoRowsBySha256);
        ChartInfoInlineBuildResult inlineResult = inlineBuildService.BuildForSnapshots(
            dbGateway,
            parsedCandidates.Select(candidate => InlineChartSnapshotTarget.FromBmsonSong(candidate.Song, candidate.Snapshot)),
            currentFailures,
            logInstallPerformance,
            logInstallPerformanceWarn);
        ApplyInlineChartInfoResult(result, inlineResult);
    }

    private static void ApplyInlineChartInfoResult(SongTableFileCheckResult result, ChartInfoInlineBuildResult inlineResult, bool storeRows = true)
    {
        if (result == null || inlineResult == null)
        {
            return;
        }
        if (storeRows)
        {
            result.InlineChartInfoRows.AddRange(inlineResult.ChartInfoRows);
            result.InlineChartInfoAppliedRows.AddRange(inlineResult.AppliedRows);
            result.InlineChartInfoParseFailureRows.AddRange(inlineResult.ParseFailureRows);
            foreach (string md5 in inlineResult.ParseFailureDeleteMd5s)
            {
                if (!result.InlineChartInfoParseFailureDeleteMd5s.Contains(md5, StringComparer.OrdinalIgnoreCase))
                {
                    result.InlineChartInfoParseFailureDeleteMd5s.Add(md5);
                }
            }
        }
        result.InlineChartInfoTargetCount += inlineResult.TargetCount;
        result.InlineChartInfoSuccessCount += inlineResult.SuccessCount;
        result.InlineChartInfoCurrentSkippedCount += inlineResult.CurrentSkippedCount;
        result.InlineChartInfoFailureSkippedCount += inlineResult.FailureSkippedCount;
        result.InlineChartInfoParseFailedCount += inlineResult.ParseFailedCount;
        result.InlineChartInfoFailurePersistedCount += inlineResult.FailurePersistedCount;
        result.InlineChartInfoFailureClearedCount += inlineResult.FailureClearedCount;
        result.InlineChartInfoParseMs += inlineResult.ParseMs;
    }

    private static void RemoveRangeIfAny<T>(List<T> list, int index, int count)
    {
        if (list == null || count <= 0 || index < 0 || index >= list.Count)
        {
            return;
        }
        int safeCount = Math.Min(count, list.Count - index);
        if (safeCount > 0)
        {
            list.RemoveRange(index, safeCount);
        }
    }

    private static IEnumerable<List<string>> CreateBatches(IEnumerable<string> paths, int batchSize)
    {
        var batch = new List<string>(Math.Max(1, batchSize));
        foreach (string path in paths ?? [])
        {
            batch.Add(path);
            if (batch.Count >= batchSize)
            {
                yield return batch;
                batch = new List<string>(Math.Max(1, batchSize));
            }
        }
        if (batch.Count > 0)
        {
            yield return batch;
        }
    }

    private enum FileDiffChartKind
    {
        Bms,
        Bmson
    }

    private sealed class FileDiffParseTarget(BmsLibraryInitializationService.FileDiffChartKind kind, string path, BMSFile existingBmsFile = null, int textFlag = 0)
    {
        public FileDiffChartKind Kind { get; } = kind;

        public string Path { get; } = path ?? string.Empty;

        public BMSFile ExistingBmsFile { get; } = existingBmsFile;

        public int TextFlag { get; } = textFlag == 0 ? 0 : 1;
    }

    private sealed class FileDiffReadCandidate
    {
        private FileDiffReadCandidate(FileDiffChartKind kind, string path, ChartFileReadBuffer buffer, BMSFile existingBmsFile, int textFlag, Exception exception)
        {
            Kind = kind;
            Path = path ?? string.Empty;
            Buffer = buffer;
            ExistingBmsFile = existingBmsFile;
            TextFlag = textFlag == 0 ? 0 : 1;
            Exception = exception;
        }

        public FileDiffChartKind Kind { get; }

        public string Path { get; }

        public ChartFileReadBuffer Buffer { get; }

        public BMSFile ExistingBmsFile { get; }

        public int TextFlag { get; }

        public Exception Exception { get; }

        public static FileDiffReadCandidate CreateSuccess(FileDiffChartKind kind, string path, ChartFileReadBuffer buffer, BMSFile existingBmsFile, int textFlag)
        {
            return new FileDiffReadCandidate(kind, path, buffer, existingBmsFile, textFlag, null);
        }

        public static FileDiffReadCandidate CreateFailure(FileDiffChartKind kind, string path, BMSFile existingBmsFile, int textFlag, Exception exception)
        {
            return new FileDiffReadCandidate(kind, path, null, existingBmsFile, textFlag, exception);
        }
    }

    private sealed class FileDiffParsedCandidate
    {
        private FileDiffParsedCandidate(InlineBmsParseCandidate bmsCandidate, InlineBmsonParseCandidate bmsonCandidate)
        {
            BmsCandidate = bmsCandidate;
            BmsonCandidate = bmsonCandidate;
        }

        public InlineBmsParseCandidate BmsCandidate { get; }

        public InlineBmsonParseCandidate BmsonCandidate { get; }

        public string Path => BmsCandidate?.Path ?? BmsonCandidate?.Path ?? string.Empty;

        public static FileDiffParsedCandidate FromBms(InlineBmsParseCandidate candidate)
        {
            return new FileDiffParsedCandidate(candidate, null);
        }

        public static FileDiffParsedCandidate FromBmson(InlineBmsonParseCandidate candidate)
        {
            return new FileDiffParsedCandidate(null, candidate);
        }
    }

    private sealed class FileDiffParsedBatch(int sequence, List<BmsLibraryInitializationService.InlineBmsParseCandidate> bmsCandidates, List<BmsLibraryInitializationService.InlineBmsonParseCandidate> bmsonCandidates)
    {
        public int Sequence { get; } = sequence;

        public List<InlineBmsParseCandidate> BmsCandidates { get; } = bmsCandidates ?? [];

        public List<InlineBmsonParseCandidate> BmsonCandidates { get; } = bmsonCandidates ?? [];
    }

    private sealed class FileDiffPostParseResult(int sequence)
    {
        public int Sequence { get; } = sequence;

        public FileDiffPostParseBatchMetrics Metrics { get; } = new FileDiffPostParseBatchMetrics();

        public ChartInfoInlineBuildResult ChartInfoResult { get; } = new ChartInfoInlineBuildResult();

        public List<InlineMaintenanceItemResult> MaintenanceResults { get; } = [];

        public FileScanDiffCommitChunk CommitChunk { get; } = new FileScanDiffCommitChunk();

        public List<InlineBmsParseCandidate> BmsParseFailures { get; } = [];

        public List<InlineBmsonParseCandidate> BmsonParseFailures { get; } = [];

        public List<BMSFile> AddedFiles { get; } = [];

        public HashSet<string> NewlyInsertedBmsPaths { get; } = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        public HashSet<string> SuccessfullyReplacedBmsPaths { get; } = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        public int BmsDateOnlyUpdateCount { get; set; }

        public int BmsTextOnlyUpdateCount { get; set; }

        public List<BmsRelinkDestinationCandidate> BmsRelinkDestinationCandidates { get; } = [];

        public List<LR2SongDBExtended.bmson_song> ParsedBmsonSongs { get; } = [];

        public HashSet<string> SuccessfullyParsedBmsonPaths { get; } = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        public void TrackBmsRelinkDestinationCandidate(InlineBmsParseCandidate candidate)
        {
            if (candidate?.File != null && candidate.ExistingFile == null)
            {
                BmsRelinkDestinationCandidates.Add(new BmsRelinkDestinationCandidate(candidate.File));
            }
        }
    }

    private sealed class FileDiffPostParseBatchMetrics
    {
        public int BmsCount { get; set; }

        public int BmsonCount { get; set; }

        public long ChartInfoTicks { get; set; }

        public long MaintenanceTicks { get; set; }

        public long BmsMaintenanceTicks { get; set; }

        public long BmsonMaintenanceTicks { get; set; }

        public long HealthMs { get; set; }

        public long EncodingMs { get; set; }

        public long EncodingReloadMs { get; set; }

        public long EncodingMaxMs { get; set; }

        public long CacheHitCount { get; set; }

        public long ResourceIndexHitCount { get; set; }

        public long FileExistsFallbackCount { get; set; }

        public long CommitQueueWaitTicks { get; set; }

        public long TotalTicks { get; set; }
    }

    private sealed class FileDiffParsePipelineResult
    {
        public List<BMSFile> AddedFiles { get; } = [];

        public HashSet<string> NewlyInsertedBmsPaths { get; } = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        public HashSet<string> SuccessfullyReplacedBmsPaths { get; } = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        public int BmsDateOnlyUpdateCount { get; set; }

        public int BmsTextOnlyUpdateCount { get; set; }

        public int BmsMovedHashRelinkCount { get; set; }

        public int BmsMovedHashRelinkAmbiguousCount { get; set; }

        public List<BmsRelinkDestinationCandidate> BmsRelinkDestinationCandidates { get; } = [];

        public Dictionary<string, Lr2SongUserColumns> BmsMovedHashRelinkUserColumnRestores { get; } =
            new Dictionary<string, Lr2SongUserColumns>(StringComparer.OrdinalIgnoreCase);

        public List<LR2SongDBExtended.bmson_song> ParsedBmsonSongs { get; } = [];

        public HashSet<string> SuccessfullyParsedBmsonPaths { get; } = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        public long ReadMs { get; set; }

        public long DigestMs { get; set; }

        public long BmsParseMs { get; set; }

        public long BmsonParseMs { get; set; }

        public int SnapshotQueueHighWatermark { get; set; }

        public void TrackBmsRelinkDestinationCandidate(InlineBmsParseCandidate candidate)
        {
            if (candidate?.File != null && candidate.ExistingFile == null)
            {
                BmsRelinkDestinationCandidates.Add(new BmsRelinkDestinationCandidate(candidate.File));
            }
        }
    }

    private sealed class BmsRelinkDestinationCandidate(BMSFile file)
    {
        public BMSFile File { get; } = file;
    }

    private sealed class InlineMaintenanceItemResult
    {
        public static InlineMaintenanceItemResult Empty { get; } = new InlineMaintenanceItemResult(
            FileDiffChartKind.Bms,
            targetCount: 0,
            successCount: 0,
            failedCount: 0,
            elapsedMs: 0,
            healthMs: 0,
            encodingMs: 0,
            encodingReloadMs: 0,
            encodingReloadCount: 0,
            encodingDetectionResult: null,
            cacheHitCount: 0,
            resourceIndexHitCount: 0,
            resourceHealthSetCacheHitCount: 0,
            fileExistsFallbackCount: 0,
            warningMessage: null);

        public InlineMaintenanceItemResult(
            FileDiffChartKind kind,
            int targetCount,
            int successCount,
            int failedCount,
            long elapsedMs,
            long healthMs,
            long encodingMs,
            long encodingReloadMs,
            int encodingReloadCount,
            BMSFile.BmsEncodingDetectionResult encodingDetectionResult,
            long cacheHitCount,
            long resourceIndexHitCount,
            long resourceHealthSetCacheHitCount,
            long fileExistsFallbackCount,
            string warningMessage)
        {
            Kind = kind;
            TargetCount = Math.Max(0, targetCount);
            SuccessCount = Math.Max(0, successCount);
            FailedCount = Math.Max(0, failedCount);
            ElapsedMs = Math.Max(0L, elapsedMs);
            HealthMs = Math.Max(0L, healthMs);
            EncodingMs = Math.Max(0L, encodingMs);
            EncodingReloadMs = Math.Max(0L, encodingReloadMs);
            EncodingReloadCount = Math.Max(0, encodingReloadCount);
            EncodingDetectCount = encodingDetectionResult == null ? 0 : 1;
            EncodingFastAsciiCount = encodingDetectionResult?.FastAscii == true ? 1 : 0;
            SetEncodingOutcomeCounts(encodingDetectionResult?.Outcome ?? BMSFile.EncodingDetectionOutcome.Other, EncodingDetectCount);
            CacheHitCount = Math.Max(0L, cacheHitCount);
            ResourceIndexHitCount = Math.Max(0L, resourceIndexHitCount);
            ResourceHealthSetCacheHitCount = Math.Max(0L, resourceHealthSetCacheHitCount);
            FileExistsFallbackCount = Math.Max(0L, fileExistsFallbackCount);
            WarningMessage = warningMessage;
            Succeeded = SuccessCount > 0;
        }

        private void SetEncodingOutcomeCounts(BMSFile.EncodingDetectionOutcome outcome, int count)
        {
            if (count <= 0)
            {
                return;
            }
            switch (outcome)
            {
                case BMSFile.EncodingDetectionOutcome.ShiftJis:
                    EncodingShiftJisCount = count;
                    break;
                case BMSFile.EncodingDetectionOutcome.ShiftJisQuestion:
                    EncodingShiftJisQuestionCount = count;
                    break;
                case BMSFile.EncodingDetectionOutcome.Korean:
                    EncodingKoreanCount = count;
                    break;
                case BMSFile.EncodingDetectionOutcome.KoreanQuestion:
                    EncodingKoreanQuestionCount = count;
                    break;
                case BMSFile.EncodingDetectionOutcome.Utf8:
                    EncodingUtf8Count = count;
                    break;
                case BMSFile.EncodingDetectionOutcome.Unknown:
                    EncodingUnknownCount = count;
                    break;
                default:
                    EncodingOtherCount = count;
                    break;
            }
        }

        public FileDiffChartKind Kind { get; }

        public int TargetCount { get; }

        public int SuccessCount { get; }

        public int FailedCount { get; }

        public long ElapsedMs { get; }

        public long HealthMs { get; }

        public long EncodingMs { get; }

        public long EncodingReloadMs { get; }

        public int EncodingReloadCount { get; }

        public int EncodingDetectCount { get; private set; }

        public int EncodingFastAsciiCount { get; private set; }

        public int EncodingShiftJisCount { get; private set; }

        public int EncodingShiftJisQuestionCount { get; private set; }

        public int EncodingKoreanCount { get; private set; }

        public int EncodingKoreanQuestionCount { get; private set; }

        public int EncodingUtf8Count { get; private set; }

        public int EncodingUnknownCount { get; private set; }

        public int EncodingOtherCount { get; private set; }

        public long CacheHitCount { get; }

        public long ResourceIndexHitCount { get; }

        public long ResourceHealthSetCacheHitCount { get; }

        public long FileExistsFallbackCount { get; }

        public string WarningMessage { get; }

        public bool Succeeded { get; }
    }

    private sealed class InlineBmsParseCandidate
    {
        private InlineBmsParseCandidate(string path, ChartFileSnapshot snapshot, BMSFile file, BMSFile existingFile, IOException exception)
        {
            Path = path ?? string.Empty;
            Snapshot = snapshot;
            File = file;
            ExistingFile = existingFile;
            Exception = exception;
        }

        public string Path { get; }

        public ChartFileSnapshot Snapshot { get; }

        public BMSFile File { get; }

        public BMSFile ExistingFile { get; }

        public IOException Exception { get; }

        public static InlineBmsParseCandidate CreateSuccess(string path, ChartFileSnapshot snapshot, BMSFile file, BMSFile existingFile)
        {
            return new InlineBmsParseCandidate(path, snapshot, file, existingFile, null);
        }

        public static InlineBmsParseCandidate CreateFailure(string path, BMSFile existingFile, IOException exception)
        {
            return new InlineBmsParseCandidate(path, null, null, existingFile, exception);
        }
    }

    private sealed class InlineBmsonParseCandidate
    {
        private InlineBmsonParseCandidate(string path, ChartFileSnapshot snapshot, LR2SongDBExtended.bmson_song song, Exception exception)
        {
            Path = path ?? string.Empty;
            Snapshot = snapshot;
            Song = song;
            Exception = exception;
        }

        public string Path { get; }

        public ChartFileSnapshot Snapshot { get; }

        public LR2SongDBExtended.bmson_song Song { get; }

        public Exception Exception { get; }

        public static InlineBmsonParseCandidate CreateSuccess(string path, ChartFileSnapshot snapshot, LR2SongDBExtended.bmson_song song)
        {
            return new InlineBmsonParseCandidate(path, snapshot, song, null);
        }

        public static InlineBmsonParseCandidate CreateFailure(string path, Exception exception)
        {
            return new InlineBmsonParseCandidate(path, null, null, exception);
        }
    }

    private static long EstimateCurrentFileDiffReadBytes(IEnumerable<string> paths)
    {
        long total = 0L;
        foreach (string path in paths ?? [])
        {
            long length;
            try
            {
                length = new FileInfo(path).Length;
            }
            catch
            {
                continue;
            }
            if (length <= 0L)
            {
                continue;
            }
            if (long.MaxValue - total < length)
            {
                return long.MaxValue;
            }
            total += length;
        }
        return total;
    }

    internal static int ResolveDefaultFileDiffParserDegree()
    {
        return ResolveDefaultFileDiffParserDegree(Environment.ProcessorCount);
    }

    internal static int ResolveDefaultFileDiffParserDegree(int processorCount)
    {
        int normalizedProcessorCount = Math.Max(1, processorCount);
        if (normalizedProcessorCount <= 2)
        {
            return 1;
        }
        if (normalizedProcessorCount <= 4)
        {
            return 2;
        }
        return Math.Max(2, Math.Min(normalizedProcessorCount - 2, (normalizedProcessorCount + 1) / 2));
    }

    internal static int ResolveDefaultFileDiffPostParseWorkerDegree(int processorCount, int parserDegree)
    {
        int normalizedProcessorCount = Math.Max(1, processorCount);
        int normalizedParserDegree = Math.Max(1, parserDegree);
        if (normalizedProcessorCount <= 2)
        {
            return normalizedParserDegree;
        }
        int targetPostParseDegree = normalizedParserDegree + Math.Max(1, normalizedParserDegree / 2);
        return Math.Max(normalizedParserDegree, Math.Min(normalizedProcessorCount, targetPostParseDegree));
    }

    internal static int ResolveFileDiffParsedQueueCapacity(int parserDegree, int postParseBatchSize)
    {
        int normalizedBatchSize = Math.Max(1, postParseBatchSize);
        int normalizedParserDegree = Math.Max(1, parserDegree);
        return Math.Max(normalizedBatchSize, normalizedParserDegree * normalizedBatchSize * 2);
    }

    internal static int ResolveFileDiffPostParseQueueCapacity(int postParseWorkerDegree)
    {
        int normalizedWorkerDegree = Math.Max(1, postParseWorkerDegree);
        return Math.Max(1, normalizedWorkerDegree * 2);
    }

    private int ResolveFileDiffParserDegree()
    {
        if (fileDiffParserDegreeOverride.HasValue)
        {
            return Math.Max(1, fileDiffParserDegreeOverride.Value);
        }
        return ResolveDefaultFileDiffParserDegree();
    }

    private int ResolveFileDiffPostParseWorkerDegree(int parserDegree)
    {
        int normalizedParserDegree = Math.Max(1, parserDegree);
        if (fileDiffParserDegreeOverride.HasValue)
        {
            return normalizedParserDegree;
        }
        return ResolveDefaultFileDiffPostParseWorkerDegree(Environment.ProcessorCount, normalizedParserDegree);
    }

    private int ResolveInlineChartInfoBatchSize()
    {
        if (inlineChartInfoBatchSizeOverride.HasValue)
        {
            return Math.Max(1, inlineChartInfoBatchSizeOverride.Value);
        }
        return ResolveDefaultInlineChartInfoBatchSize();
    }

    internal static int ResolveDefaultInlineChartInfoBatchSize()
    {
        return DefaultInlineChartInfoBatchSize;
    }

    internal static int ResolveDefaultFileDiffCommitChunkSize()
    {
        return DefaultFileDiffCommitChunkSize;
    }

    private int ResolveFileDiffCommitChunkSize()
    {
        if (fileDiffCommitChunkSizeOverride.HasValue)
        {
            return Math.Max(1, fileDiffCommitChunkSizeOverride.Value);
        }
        return ResolveDefaultFileDiffCommitChunkSize();
    }

    private static long TicksToMilliseconds(long ticks)
    {
        return (long)(ticks * 1000.0 / Stopwatch.Frequency);
    }

    private static string QuoteLogValue(string value)
    {
        if (value == null)
        {
            return "\"\"";
        }
        return "\"" + value.Replace("\\", "\\\\").Replace("\"", "\\\"").Replace("\r", " ").Replace("\n", " ") + "\"";
    }

    private static long SaturatingAdd(long left, long right)
    {
        if (left >= long.MaxValue || right >= long.MaxValue || long.MaxValue - left < right)
        {
            return long.MaxValue;
        }
        return left + right;
    }

    private static ChartScanResult MergeScanResults(params ChartScanResult[] scanResults)
    {
        var merged = new ChartScanResult();
        foreach (ChartScanResult scanResult in scanResults.Where(scanResult => scanResult != null))
        {
            merged.ChartFilePaths.UnionWith(scanResult.ChartFilePaths ?? new HashSet<string>(StringComparer.OrdinalIgnoreCase));
            MergeFileEntryDictionary(merged.ChartFileEntriesByPath, scanResult.ChartFileEntriesByPath);
            merged.ChartDirectories.UnionWith(scanResult.ChartDirectories ?? new HashSet<string>(StringComparer.OrdinalIgnoreCase));
            merged.ChartDirectoriesWithTextFiles.UnionWith(scanResult.ChartDirectoriesWithTextFiles ?? new HashSet<string>(StringComparer.OrdinalIgnoreCase));
            merged.FolderInfoFilePaths.UnionWith(scanResult.FolderInfoFilePaths ?? new HashSet<string>(StringComparer.OrdinalIgnoreCase));
            MergeFileEntryDictionary(merged.DirectoryEntriesByPath, scanResult.DirectoryEntriesByPath);
            MergeFileEntryDictionary(merged.TextFileEntriesByPath, scanResult.TextFileEntriesByPath);
            MergeFileEntryDictionary(merged.FolderInfoFileEntriesByPath, scanResult.FolderInfoFileEntriesByPath);
            MergeHashDictionary(merged.AudioRelativePathHashesByChartDirectory, scanResult.AudioRelativePathHashesByChartDirectory);
            MergeHashDictionary(merged.ImageRelativePathHashesByChartDirectory, scanResult.ImageRelativePathHashesByChartDirectory);
            MergeHashDictionary(merged.MovieRelativePathHashesByChartDirectory, scanResult.MovieRelativePathHashesByChartDirectory);
            MergeHashDictionary(merged.SelfOwnedAudioRelativePathHashesByChartDirectory, scanResult.SelfOwnedAudioRelativePathHashesByChartDirectory);
            MergeHashDictionary(merged.SelfOwnedImageRelativePathHashesByChartDirectory, scanResult.SelfOwnedImageRelativePathHashesByChartDirectory);
            MergeHashDictionary(merged.SelfOwnedMovieRelativePathHashesByChartDirectory, scanResult.SelfOwnedMovieRelativePathHashesByChartDirectory);
        }
        return merged;
    }

    private static void MergeFileEntryDictionary(
        Dictionary<string, RootFileEnumerationEntry> destination,
        Dictionary<string, RootFileEnumerationEntry> source)
    {
        if (destination == null || source == null)
        {
            return;
        }

        foreach (KeyValuePair<string, RootFileEnumerationEntry> item in source)
        {
            if (string.IsNullOrWhiteSpace(item.Key) || item.Value == null)
            {
                continue;
            }

            destination[item.Key] = item.Value;
        }
    }

    private static void MergeHashDictionary(Dictionary<string, uint[]> destination, Dictionary<string, uint[]> source)
    {
        foreach (KeyValuePair<string, uint[]> item in source ?? new Dictionary<string, uint[]>(StringComparer.OrdinalIgnoreCase))
        {
            if (!destination.TryGetValue(item.Key, out uint[] existing) || existing == null || existing.Length == 0)
            {
                destination[item.Key] = item.Value ?? [];
                continue;
            }
            if (item.Value == null || item.Value.Length == 0)
            {
                continue;
            }
            destination[item.Key] = [.. existing.Concat(item.Value).Distinct()];
        }
    }

    private static ulong CountHashEntries(Dictionary<string, uint[]> hashesByDirectory)
    {
        ulong count = 0UL;
        foreach (uint[] hashes in hashesByDirectory?.Values ?? Enumerable.Empty<uint[]>())
        {
            count += (ulong)(hashes?.Length ?? 0);
        }
        return count;
    }

    public ChartDigestBackfillResult BackfillChartDigests(
        BmsLibraryDbGateway dbGateway,
        IEnumerable<BMSFile> currentFiles,
        Action<int, int, string> reportProgress = null,
        Action<string> logInstallPerformance = null)
    {
        var result = new ChartDigestBackfillResult();
        if (dbGateway == null)
        {
            return result;
        }
        var stopwatchTotal = Stopwatch.StartNew();
        List<BMSFile> targetFiles = [.. (currentFiles ?? []).Where(file => file != null && !string.IsNullOrWhiteSpace(file.hash) && string.IsNullOrWhiteSpace(file.sha256) && !string.IsNullOrWhiteSpace(file.path) && File.Exists(file.path))];
        result.TargetCount = targetFiles.Count;
        reportProgress?.Invoke(result.TargetCount, 0, string.Empty);
        if (targetFiles.Count == 0)
        {
            stopwatchTotal.Stop();
            result.TotalMs = stopwatchTotal.ElapsedMilliseconds;
            return result;
        }
        var stopwatchCompute = Stopwatch.StartNew();
        var completedFiles = new List<BMSFile>(targetFiles.Count);
        foreach (BMSFile file in targetFiles)
        {
            try
            {
                reportProgress?.Invoke(result.TargetCount, result.ProcessedCount, file.path);
                file.ApplySha256(BMSFile.GetSHA256Hash(file.path));
                completedFiles.Add(file);
                result.BackfilledCount++;
            }
            catch
            {
                result.FailedCount++;
                result.FailedPaths.Add(file.path);
            }
            finally
            {
                result.ProcessedCount++;
                reportProgress?.Invoke(result.TargetCount, result.ProcessedCount, file.path);
            }
        }
        stopwatchCompute.Stop();
        result.ComputeMs = stopwatchCompute.ElapsedMilliseconds;
        var stopwatchDbCommit = Stopwatch.StartNew();
        if (completedFiles.Count > 0)
        {
            dbGateway.UpsertChartDigests(completedFiles);
        }
        stopwatchDbCommit.Stop();
        result.DbCommitMs = stopwatchDbCommit.ElapsedMilliseconds;
        stopwatchTotal.Stop();
        result.TotalMs = stopwatchTotal.ElapsedMilliseconds;
        logInstallPerformance?.Invoke("chart_digest_backfill total=" + result.TargetCount + " success=" + result.BackfilledCount + " failed=" + result.FailedCount + " computeMs=" + result.ComputeMs + " dbCommitMs=" + result.DbCommitMs + " totalMs=" + result.TotalMs);
        return result;
    }

    public ScoreTableLoadResult LoadScoreTable(BmsLibraryDbGateway dbGateway, BmsLibraryOptionsSnapshot options = null)
    {
        if (dbGateway == null)
        {
            return new ScoreTableLoadResult();
        }

        var result = new ScoreTableLoadResult
        {
            EnableDownloadLr2IrScoreAndDetectUnsent = options?.EnableDownloadLr2IrScoreAndDetectUnsent ?? true
        };
        if (IsBeatorajaScoreDbEnabled(options))
        {
            result.ActiveScoreSource = ActiveScoreSource.Beatoraja;
            try
            {
                var loader = new BeatorajaScoreDbLoader();
                foreach (KeyValuePair<string, BMSScore> score in loader.LoadModeZeroScores(options.BeatorajaScoreDbPath))
                {
                    result.BeatorajaScoresBySha256[score.Key] = score.Value;
                }
            }
            catch
            {
            }
            return result;
        }

        if (!string.IsNullOrWhiteSpace(dbGateway.ScoreDbPath))
        {
            result.ActiveScoreSource = ActiveScoreSource.Lr2;
            try
            {
                ScoreTableLoadResult lr2Result = dbGateway.LoadScoresAndPlayerId();
                result.Scores.AddRange(lr2Result.Scores);
                result.LR2Id = lr2Result.LR2Id;
                result.ReadOnly = lr2Result.ReadOnly;
                result.DbLockWaitMs = lr2Result.DbLockWaitMs;
            }
            catch
            {
            }
        }

        return result;
    }

    private static bool IsBeatorajaScoreDbEnabled(BmsLibraryOptionsSnapshot options)
    {
        return options?.UseBeatorajaScoreDb == true
            && !string.IsNullOrWhiteSpace(options.BeatorajaScoreDbPath)
            && string.Equals(Path.GetFileName(options.BeatorajaScoreDbPath), "score.db", StringComparison.OrdinalIgnoreCase)
            && File.Exists(options.BeatorajaScoreDbPath);
    }

    public InstallTableLoadResult LoadInstallTable(
        BmsLibraryDbGateway dbGateway,
        Func<ChartFile, bool> isInstalledChart = null)
    {
        var result = new InstallTableLoadResult();
        if (dbGateway == null)
        {
            return result;
        }
        var totalStopwatch = Stopwatch.StartNew();
        var loadStopwatch = Stopwatch.StartNew();
        try
        {
            List<ChartPackage> packages = dbGateway.LoadInstallPackages();
            result.PendingPackages.AddRange(packages.Where(pkg => pkg != null && (File.Exists(pkg.path) || Directory.Exists(pkg.path)) && pkg.ChartEntries.Count > 0));
            result.StalePackages.AddRange(packages.Except(result.PendingPackages));
            result.StaleInstallPaths.AddRange(result.StalePackages.Where(pkg => !string.IsNullOrWhiteSpace(pkg.path)).Select(pkg => pkg.path));
        }
        catch
        {
            totalStopwatch.Stop();
            result.TotalMs = totalStopwatch.ElapsedMilliseconds;
            return result;
        }
        loadStopwatch.Stop();
        result.LoadMs = loadStopwatch.ElapsedMilliseconds;
        var warningStopwatch = Stopwatch.StartNew();
        foreach (ChartPackage pendingPackage in result.PendingPackages)
        {
            bool isSingleFilePackage = !Directory.Exists(pendingPackage.path);
            foreach (PackageChartEntry entry in pendingPackage.ChartEntries)
            {
                ChartFile chart = entry?.Chart;
                if (chart == null)
                {
                    continue;
                }
                bool isBmson = chart.Kind == ChartFileKind.Bmson;
                if (isInstalledChart != null && isInstalledChart(chart))
                {
                    entry.ClearWarningsByCategory(ChartWarningCategory.InstalledState);
                    entry.SetWarning(ChartWarningKind.AlreadyInstalled, Resources.Warning_AlreadyInstalled);
                    result.InstalledWarningCount++;
                }
                else if (isSingleFilePackage)
                {
                    entry.ClearWarningsByCategory(ChartWarningCategory.PackageLayout);
                    entry.SetWarning(isBmson ? ChartWarningKind.SingleBmsonFile : ChartWarningKind.SingleBmsFile, isBmson ? Resources.Warning_SingleBmsonFile : Resources.Warning_SingleBmsFile);
                    result.SingleFileWarningCount++;
                }
            }
            result.StrictWarningCount += BmsLibraryPackageInstallService.ApplyPendingResourceHealthProjectionToEntries(pendingPackage.ChartEntries);
            BmsLibraryPackageInstallService.ApplyNestedChartFileWarnings(pendingPackage);
        }
        warningStopwatch.Stop();
        result.WarningInitMs = warningStopwatch.ElapsedMilliseconds;
        totalStopwatch.Stop();
        result.TotalMs = totalStopwatch.ElapsedMilliseconds;
        return result;
    }

    public InitializationExecutionResult RunInitialize(
        List<Action> tasksContinuation,
        SemaphoreSlim semaphore,
        Action phase1,
        Action phase2,
        Action phase3)
    {
        var result = new InitializationExecutionResult();
        var stopwatchTotal = Stopwatch.StartNew();
        List<Task> continuationTasks = [];
        var stopwatchPhase1 = Stopwatch.StartNew();
        phase1?.Invoke();
        stopwatchPhase1.Stop();
        result.Phase1MinLoadMs = stopwatchPhase1.ElapsedMilliseconds;
        long waitForContinuationSignalMs = 0L;
        long waitForContinuationTasksMs = 0L;
        var stopwatchWaitBeforeContinuationStart = Stopwatch.StartNew();
        semaphore?.Wait();
        stopwatchWaitBeforeContinuationStart.Stop();
        long waitBeforeContinuationStartMs = stopwatchWaitBeforeContinuationStart.ElapsedMilliseconds;

        if (tasksContinuation != null)
        {
            for (int i = 0; i < tasksContinuation.Count; i++)
            {
                continuationTasks.Add(Task.Run(tasksContinuation[i]).Logging("Initialize"));
            }
        }

        Thread.Yield();

        var stopwatchPhase2 = Stopwatch.StartNew();
        phase2?.Invoke();
        stopwatchPhase2.Stop();
        result.Phase2ScanMaintMs = stopwatchPhase2.ElapsedMilliseconds;

        var stopwatchPhase3 = Stopwatch.StartNew();
        phase3?.Invoke();
        stopwatchPhase3.Stop();
        result.Phase3InstallMaintenanceMs = stopwatchPhase3.ElapsedMilliseconds;

        if (semaphore != null && tasksContinuation != null && tasksContinuation.Count > 0)
        {
            var stopwatchWaitForContinuationSignal = Stopwatch.StartNew();
            semaphore.Wait();
            stopwatchWaitForContinuationSignal.Stop();
            waitForContinuationSignalMs = stopwatchWaitForContinuationSignal.ElapsedMilliseconds;
        }

        var stopwatchWaitForContinuationTasks = Stopwatch.StartNew();
        Task.WaitAll([.. continuationTasks]);
        stopwatchWaitForContinuationTasks.Stop();
        waitForContinuationTasksMs = stopwatchWaitForContinuationTasks.ElapsedMilliseconds;

        result.WaitBeforeContinuationStartMs = waitBeforeContinuationStartMs;
        result.WaitForContinuationSignalMs = waitForContinuationSignalMs;
        result.WaitForContinuationTasksMs = waitForContinuationTasksMs;
        result.WaitContinuationMs = waitBeforeContinuationStartMs + waitForContinuationSignalMs + waitForContinuationTasksMs;
        stopwatchTotal.Stop();
        result.TotalMs = stopwatchTotal.ElapsedMilliseconds;
        return result;
    }

    private static void NormalizeStandaloneSongPathCompatibility(LR2SongDBExtended songDb, IEnumerable<BMSFile> loadedSongs, SongTableLoadResult result)
    {
        var stopwatchSongNormalizeLoop = Stopwatch.StartNew();
        foreach (BMSFile song in loadedSongs ?? [])
        {
            if (song == null || string.IsNullOrWhiteSpace(song.hash))
            {
                continue;
            }
            if (Lr2SongFolderParentNormalizer.ApplyIfMissingOrInvalid(song))
            {
                result.UpdatedSongs.Add(song);
                result.CrcRecalculatedCount++;
            }
        }
        stopwatchSongNormalizeLoop.Stop();
        result.SongNormalizeLoopMs = stopwatchSongNormalizeLoop.ElapsedMilliseconds;
        result.DbWriteRequired = result.UpdatedSongs.Count > 0;
        if (!result.DbWriteRequired)
        {
            return;
        }

        var stopwatchDbWrite = Stopwatch.StartNew();
        songDb.BeginTransaction();
        foreach (BMSFile updatedSong in result.UpdatedSongs)
        {
            Lr2SongDbWriter.UpsertGeneratedSong(songDb, updatedSong);
        }
        var stopwatchCommit = Stopwatch.StartNew();
        songDb.Commit();
        stopwatchCommit.Stop();
        result.CommitMs = stopwatchCommit.ElapsedMilliseconds;
        stopwatchDbWrite.Stop();
        result.DbWriteMs = stopwatchDbWrite.ElapsedMilliseconds;
    }

    private static void NormalizeSongTable(
        LR2SongDBExtended songDb,
        List<BMSFile> loadedSongs,
        List<BMSFile> deletedFiles,
        SongTableLoadResult result,
        IBmsLibraryDialogService dialogService,
        IFileMutationService fileMutationService,
        FileMutationOptions targetOnlyFileMutationOptions,
        Func<Exception, string> getDisplayedExceptionMessage,
        Action<string> logInstallPerformance)
    {
        int unixtime = (DateTime.Now + new TimeSpan(30, 0, 0, 0)).ToUnixtime();
        var stopwatchSongNormalizeLoop = Stopwatch.StartNew();
        foreach (BMSFile song in loadedSongs)
        {
            try
            {
                if (string.IsNullOrWhiteSpace(song.hash))
                {
                    result.DeletedSongPaths.Add(song.path);
                    deletedFiles.Add(song);
                    continue;
                }
                bool wasRelativePath = !Path.IsPathRooted(song.path);
                string originalPath = song.path;
                if (wasRelativePath)
                {
                    if (Lr2SongFolderParentNormalizer.ApplyExpected(song, songDb.LR2RootPath, fixRelativePath: true))
                    {
                        if (Path.IsPathRooted(song.path))
                        {
                            result.DeletedSongPaths.Add(originalPath);
                            result.RelativePathFixedCount++;
                        }
                        result.UpdatedSongs.Add(song);
                        result.CrcRecalculatedCount++;
                    }
                    continue;
                }
                if (song.adddate < 0 || song.adddate > unixtime)
                {
                    song.adddate = null;
                    song.date = null;
                    result.LeapYearDetected = true;
                    result.UpdatedSongs.Add(song);
                    continue;
                }
                if (Lr2SongFolderParentNormalizer.IsLikelyCrcHex(song.folder) && Lr2SongFolderParentNormalizer.IsLikelyCrcHex(song.parent))
                {
                    if (Lr2SongFolderParentNormalizer.ApplyIfMissingOrInvalid(song))
                    {
                        result.UpdatedSongs.Add(song);
                    }
                    result.CrcSkippedCount++;
                    continue;
                }
                if (Lr2SongFolderParentNormalizer.ApplyExpected(song, songDb.LR2RootPath, fixRelativePath: false))
                {
                    result.UpdatedSongs.Add(song);
                }
                result.CrcRecalculatedCount++;
            }
            catch
            {
                result.DeletedSongPaths.Add(song.path);
                deletedFiles.Add(song);
            }
        }
        stopwatchSongNormalizeLoop.Stop();
        result.SongNormalizeLoopMs = stopwatchSongNormalizeLoop.ElapsedMilliseconds;

        var stopwatchFolderTableLoad = Stopwatch.StartNew();
        List<LR2SongDB.folder> folders = [.. songDb.Table<LR2SongDB.folder>()];
        stopwatchFolderTableLoad.Stop();
        result.FolderTableLoadMs = stopwatchFolderTableLoad.ElapsedMilliseconds;

        var stopwatchFolderNormalizeLoop = Stopwatch.StartNew();
        result.UpdatedFolders.AddRange(folders.Where(delegate (LR2SongDB.folder folder)
        {
            try
            {
                if (!Path.IsPathRooted(folder.path))
                {
                    if (!folder.path.StartsWith("LR2files" + Path.DirectorySeparatorChar + "CustomFolder" + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)
                        && !folder.path.StartsWith("LR2files" + Path.DirectorySeparatorChar + "Rival" + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
                    {
                        result.DeletedFolderPaths.Add(folder.path);
                        folder.path = Path.Combine(songDb.LR2RootPath, folder.path);
                        string directoryName = Path.GetDirectoryName(folder.path);
                        if (folder.path.EndsWith("\\", StringComparison.Ordinal))
                        {
                            directoryName = Path.GetDirectoryName(directoryName);
                        }
                        if (folder.parent != Lr2SongFolderParentNormalizer.RootParentHash)
                        {
                            folder.parent = Lr2SongFolderParentNormalizer.ComputeDirectoryHash(directoryName);
                        }
                        return true;
                    }
                }
                else
                {
                    if (folder.adddate < 0 || folder.adddate > unixtime)
                    {
                        folder.adddate = null;
                        folder.date = null;
                        result.LeapYearDetected = true;
                        return true;
                    }
                    if ((folder.date < 0 || !folder.date.HasValue) && folder.type == 1 && !string.IsNullOrWhiteSpace(folder.path))
                    {
                        string directoryPath = folder.path.TrimEnd('\\');
                        if (Directory.Exists(directoryPath))
                        {
                            DateTime lastWriteTime = Directory.GetLastWriteTime(directoryPath);
                            if (IsLeapYearTimestamp(lastWriteTime)
                                && dialogService?.Show(string.Format(Resources.Warn_LR2LeapYearFolderDetected, directoryPath, lastWriteTime.ToShortDateString(), DateTime.Now.ToShortDateString()), Resources.MessageBoxTitle_Warning, MessageBoxButton.YesNo, MessageBoxImage.Exclamation, MessageBoxResult.No) == MessageBoxResult.Yes)
                            {
                                try
                                {
                                    fileMutationService?.SetTimestamps(directoryPath, isDirectory: true, creationTime: null, lastWriteTime: DateTime.Now, targetOnlyFileMutationOptions);
                                    folder.adddate = null;
                                    folder.date = null;
                                    result.LeapYearDetected = true;
                                    return true;
                                }
                                catch (Exception ex)
                                {
                                    dialogService?.Show(string.Format(Resources.Error_FailedToChangeDate, getDisplayedExceptionMessage?.Invoke(ex) ?? ex.Message), Resources.MessageBoxTitle_Error, MessageBoxButton.OK, MessageBoxImage.Hand, MessageBoxResult.OK);
                                }
                            }
                        }
                    }
                }
            }
            catch (EncoderFallbackException)
            {
                return false;
            }
            catch
            {
                result.DeletedFolderPaths.Add(folder.path);
            }
            return false;
        }));
        stopwatchFolderNormalizeLoop.Stop();
        result.FolderNormalizeLoopMs = stopwatchFolderNormalizeLoop.ElapsedMilliseconds;

        var stopwatchFixApply = Stopwatch.StartNew();
        result.DbWriteRequired = result.DeletedSongPaths.Count > 0 || result.UpdatedSongs.Count > 0 || result.DeletedFolderPaths.Count > 0 || result.UpdatedFolders.Count > 0;
        if (result.DbWriteRequired)
        {
            var stopwatchDbWrite = Stopwatch.StartNew();
            songDb.BeginTransaction();
            foreach (string deletedSongPath in result.DeletedSongPaths)
            {
                string deletedHash = songDb.ExecuteScalar<string>("SELECT hash FROM song WHERE path = " + BMSPlaylist.SqlQuoteForTest(deletedSongPath) + " LIMIT 1;");
                songDb.Delete<LR2SongDB.song>(deletedSongPath);
                BmsLibraryDbGateway.DeleteChartDigestIfOrphaned(songDb, deletedHash);
            }
            foreach (BMSFile updatedSong in result.UpdatedSongs)
            {
                Lr2SongDbWriter.UpsertGeneratedSong(songDb, updatedSong);
            }
            foreach (string deletedFolderPath in result.DeletedFolderPaths)
            {
                songDb.Delete<LR2SongDB.folder>(deletedFolderPath);
            }
            foreach (LR2SongDB.folder updatedFolder in result.UpdatedFolders)
            {
                songDb.InsertOrReplace(updatedFolder, typeof(LR2SongDB.folder));
            }
            var stopwatchCommit = Stopwatch.StartNew();
            songDb.Commit();
            stopwatchCommit.Stop();
            result.CommitMs = stopwatchCommit.ElapsedMilliseconds;
            stopwatchDbWrite.Stop();
            result.DbWriteMs = stopwatchDbWrite.ElapsedMilliseconds;
        }
        if (result.LeapYearDetected)
        {
            dialogService?.Show(Resources.Warn_LR2LeapYearBugDetected, Resources.MessageBoxTitle_Warning, MessageBoxButton.OK, MessageBoxImage.Exclamation, MessageBoxResult.OK);
        }
        stopwatchFixApply.Stop();
        result.FixApplyMs = stopwatchFixApply.ElapsedMilliseconds;
        logInstallPerformance?.Invoke(
            "song_tbl_load_detail totalSongs=" + loadedSongs.Count
            + " deletedSongs=" + deletedFiles.Count
            + " updatedSongs=" + result.UpdatedSongs.Count
            + " relativePathFixed=" + result.RelativePathFixedCount
            + " crcRecalculated=" + result.CrcRecalculatedCount
            + " crcSkipped=" + result.CrcSkippedCount
            + " updatedFolders=" + result.UpdatedFolders.Count
            + " deletedFolders=" + result.DeletedFolderPaths.Count);
    }

    private static bool IsLeapYearTimestamp(DateTime lastWriteTime)
    {
        if (!DateTime.IsLeapYear(lastWriteTime.Year))
        {
            return false;
        }
        return new DateTime(lastWriteTime.Year, 2, 29) <= lastWriteTime
            && lastWriteTime < new DateTime(lastWriteTime.Year, 3, 2);
    }

    private static bool IsBmsHashAvailable(string hash)
    {
        return !string.IsNullOrWhiteSpace(hash) && LR2SongDB.md5HashRegex.IsMatch(hash);
    }

    private static bool TableExists(LR2SongDBExtended songDb, string tableName)
    {
        if (songDb == null || string.IsNullOrWhiteSpace(tableName))
        {
            return false;
        }
        return songDb.ExecuteScalar<long>("SELECT COUNT(1) FROM sqlite_master WHERE type = 'table' AND name = " + BMSPlaylist.SqlQuoteForTest(tableName) + ";") > 0;
    }

    private static DateTime SafeGetLastWriteTimeUtc(string path)
    {
        try
        {
            return File.GetLastWriteTimeUtc(path);
        }
        catch
        {
            return DateTime.MinValue;
        }
    }

}
