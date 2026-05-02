using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Security;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using BeMusicSeeker.Models.LR2;
using BeMusicSeeker.Models.Utils;
using BeMusicSeeker.Properties;
using Ribbit.Util.Extensions;

namespace BeMusicSeeker.Models.BmsLibraryInternal;

/// <summary>
/// Loads initialization phases against snapshot inputs owned by BMSLibrary.
/// The facade must acquire the required locks before invoking phase methods.
/// </summary>
internal sealed class BmsLibraryInitializationService
{
    private const int DefaultInlineChartInfoBatchSize = 512;

    private readonly int? fileDiffParserDegreeOverride;

    private readonly ChartInfoBuildService chartInfoBuildService;

    private readonly int? inlineChartInfoBatchSizeOverride;

    public BmsLibraryInitializationService()
        : this(null, null, null)
    {
    }

    internal BmsLibraryInitializationService(int? fileDiffParserDegreeOverride, ChartInfoBuildService chartInfoBuildService = null, int? inlineChartInfoBatchSizeOverride = null)
    {
        this.fileDiffParserDegreeOverride = fileDiffParserDegreeOverride;
        this.chartInfoBuildService = chartInfoBuildService ?? new ChartInfoBuildService();
        this.inlineChartInfoBatchSizeOverride = inlineChartInfoBatchSizeOverride;
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
        SongTableLoadResult result = new SongTableLoadResult();
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

        Stopwatch stopwatchSongTableLoad = Stopwatch.StartNew();
        Stopwatch stopwatchSongCount = Stopwatch.StartNew();
        try
        {
            result.SongTableCount = Math.Max(0L, songDb.ExecuteScalar<long>("SELECT COUNT(1) FROM song;"));
        }
        catch
        {
        }
        stopwatchSongCount.Stop();
        result.SongCountMs = stopwatchSongCount.ElapsedMilliseconds;

        List<BMSFile> loadedSongs = (result.SongTableCount > 0L && result.SongTableCount <= int.MaxValue)
            ? new List<BMSFile>((int)result.SongTableCount)
            : new List<BMSFile>();
        Stopwatch stopwatchSongMaterialize = Stopwatch.StartNew();
        using (BMSFile.SuppressPropertyChangedScope())
        {
            foreach (BMSFile item in songDb.Table<BMSFile>())
            {
                loadedSongs.Add(item);
            }
        }
        stopwatchSongMaterialize.Stop();
        result.SongMaterializeMs = stopwatchSongMaterialize.ElapsedMilliseconds;
        stopwatchSongTableLoad.Stop();
        result.SongTableLoadMs = stopwatchSongTableLoad.ElapsedMilliseconds;

        List<BMSFile> deletedFiles = new List<BMSFile>();
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
        logDebugTrace?.Invoke("relative path check end");

        Stopwatch stopwatchChartDigestMapLoad = Stopwatch.StartNew();
        Dictionary<string, string> chartDigestMap = dbGateway.LoadChartDigestMap();
        stopwatchChartDigestMapLoad.Stop();
        result.ChartDigestMapLoadMs = stopwatchChartDigestMapLoad.ElapsedMilliseconds;
        foreach (KeyValuePair<string, string> item in chartDigestMap)
        {
            result.ChartDigestMap[item.Key] = item.Value;
        }

        Stopwatch stopwatchBmsonTableLoad = Stopwatch.StartNew();
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

        Stopwatch stopwatchMaintenanceTableLoad = Stopwatch.StartNew();
        Stopwatch stopwatchMaintenanceCount = Stopwatch.StartNew();
        try
        {
            result.MaintenanceTableCount = Math.Max(0L, songDb.ExecuteScalar<long>("SELECT COUNT(1) FROM maintenance;"));
        }
        catch
        {
        }
        stopwatchMaintenanceCount.Stop();
        result.MaintenanceCountMs = stopwatchMaintenanceCount.ElapsedMilliseconds;

        List<BMSFileMaintenanceInfo> maintenanceInfos = (result.MaintenanceTableCount > 0L && result.MaintenanceTableCount <= int.MaxValue)
            ? new List<BMSFileMaintenanceInfo>((int)result.MaintenanceTableCount)
            : new List<BMSFileMaintenanceInfo>();
        Stopwatch stopwatchMaintenanceMaterialize = Stopwatch.StartNew();
        using (BMSFileMaintenanceInfo.SuppressPropertyChangedScope())
        {
            foreach (BMSFileMaintenanceInfo item in songDb.Table<BMSFileMaintenanceInfo>())
            {
                maintenanceInfos.Add(item);
            }
        }
        stopwatchMaintenanceMaterialize.Stop();
        result.MaintenanceMaterializeMs = stopwatchMaintenanceMaterialize.ElapsedMilliseconds;
        stopwatchMaintenanceTableLoad.Stop();
        result.MaintenanceTableLoadMs = stopwatchMaintenanceTableLoad.ElapsedMilliseconds;

        Stopwatch stopwatchMaintenanceMapBuild = Stopwatch.StartNew();
        foreach (BMSFileMaintenanceInfo maintenanceInfo in maintenanceInfos)
        {
            if (!string.IsNullOrWhiteSpace(maintenanceInfo.path) && !result.MaintenanceMap.ContainsKey(maintenanceInfo.path))
            {
                result.MaintenanceMap[maintenanceInfo.path] = maintenanceInfo;
            }
        }
        stopwatchMaintenanceMapBuild.Stop();
        result.MaintenanceMapBuildMs = stopwatchMaintenanceMapBuild.ElapsedMilliseconds;

        HashSet<BMSFile> deletedFileSet = deletedFiles.Count > 0 ? new HashSet<BMSFile>(deletedFiles) : null;
        Stopwatch stopwatchChartDigestApply = Stopwatch.StartNew();
        foreach (BMSFile item in loadedSongs)
        {
            if (deletedFileSet == null || !deletedFileSet.Contains(item))
            {
                if (!string.IsNullOrWhiteSpace(item.hash) && result.ChartDigestMap.TryGetValue(item.hash, out string sha256))
                {
                    item.ApplySha256(sha256);
                }
            }
        }
        stopwatchChartDigestApply.Stop();
        result.ChartDigestApplyMs = stopwatchChartDigestApply.ElapsedMilliseconds;

        // chart_info is display/search metadata. Loading and applying every row can dominate startup
        // on large libraries, so the application hydrates it after the core install workflow is operable.
        result.ChartInfoMapLoadMs = 0L;
        result.ChartInfoApplyMs = 0L;

        Stopwatch stopwatchMaintenanceApply = Stopwatch.StartNew();
        foreach (BMSFile item in loadedSongs)
        {
            if (deletedFileSet != null && deletedFileSet.Contains(item))
            {
                continue;
            }
            if (result.MaintenanceMap.TryGetValue(item.path, out BMSFileMaintenanceInfo value))
            {
                if (item.HasMaintenanceInfoHash(value.hash) || string.Equals(value.hash, item.hash, StringComparison.OrdinalIgnoreCase))
                {
                    item.SetMaintenanceInfo(value, suppressPropertyChanged: true, registerEventHandlers: false);
                }
                else
                {
                    item.SetMaintenanceInfo(new BMSFileMaintenanceInfo(item), suppressPropertyChanged: true, registerEventHandlers: false);
                }
            }
            else
            {
                item.SetMaintenanceInfo(new BMSFileMaintenanceInfo(item), suppressPropertyChanged: true, registerEventHandlers: false);
            }
            result.LoadedFiles.Add(item);
        }
        foreach (LR2SongDBExtended.bmson_song item in result.LoadedBmsonSongs)
        {
            if (result.MaintenanceMap.TryGetValue(item.path, out BMSFileMaintenanceInfo value)
                && string.Equals(value.hash, item.md5, StringComparison.OrdinalIgnoreCase))
            {
                value.NormalizeForBmson(item.path, item.md5);
                item.MaintenanceInfo = value;
            }
            else
            {
                item.MaintenanceInfo = BMSFileMaintenanceInfo.CreateForBmson(item.path, item.md5);
            }
        }
        stopwatchMaintenanceApply.Stop();
        result.MaintenanceApplyMs = stopwatchMaintenanceApply.ElapsedMilliseconds;

        logInstallPerformance?.Invoke(
            "song_tbl_load_maintenance_detail bmsCount=" + result.LoadedFiles.Count
            + " bmsonCount=" + result.LoadedBmsonSongs.Count
            + " maintenanceCount=" + maintenanceInfos.Count
            + " maintenanceKeyCount=" + result.MaintenanceMap.Count);
        logInstallPerformance?.Invoke(
            "song_tbl_load_io song_read_ms=" + result.SongTableLoadMs
            + " song_count_ms=" + result.SongCountMs
            + " song_materialize_ms=" + result.SongMaterializeMs
            + " song_count=" + result.SongTableCount
            + " maintenance_read_ms=" + result.MaintenanceTableLoadMs
            + " maintenance_count_ms=" + result.MaintenanceCountMs
            + " maintenance_materialize_ms=" + result.MaintenanceMaterializeMs
            + " maintenance_count=" + result.MaintenanceTableCount
            + " chart_info_read_ms=" + result.ChartInfoMapLoadMs
            + " chart_info_apply_ms=" + result.ChartInfoApplyMs
            + " folder_read_ms=" + result.FolderTableLoadMs
            + " db_write_required=" + result.DbWriteRequired.ToString().ToLowerInvariant()
            + " db_write_ms=" + result.DbWriteMs);
        return result;
    }

    public SongTableFileCheckResult ApplyFileScanDiff(
        BmsLibraryDbGateway dbGateway,
        BmsLibraryOptionsSnapshot options,
        IEnumerable<BMSFile> currentFiles,
        BmsScanExecutionResult prefetchedScanResult,
        long prefetchedScanElapsedMs,
        Func<BmsScanExecutionResult> executeScan,
        IBmsLibraryDialogService dialogService,
        Action<string> logInstallPerformance = null,
        Action<string> logEverythingScan = null,
        IEnumerable<LR2SongDBExtended.bmson_song> currentBmsonSongs = null,
        Func<BmsScanExecutionResult> executeBmsonScan = null,
        Action scanCompleted = null,
        Action fileDiffStarted = null,
        Action<int, int, string> reportParseProgress = null,
        Action<string> logInstallPerformanceWarn = null)
    {
        SongTableFileCheckResult result = new SongTableFileCheckResult();
        Stopwatch stopwatchScan = Stopwatch.StartNew();
        BmsScanExecutionResult scanResult = prefetchedScanResult;
        if (scanResult != null && scanResult.Result != null)
        {
            result.PrefetchedScanUsed = true;
        }
        else
        {
            scanResult = executeScan?.Invoke();
        }
        stopwatchScan.Stop();
        scanCompleted?.Invoke();
        if (scanResult?.Result == null)
        {
            return result;
        }
        fileDiffStarted?.Invoke();
        BmsScanResult mergedScanResult = scanResult.Result;
        if (executeBmsonScan != null)
        {
            Stopwatch stopwatchBmsonScan = Stopwatch.StartNew();
            BmsScanExecutionResult bmsonScanResult = executeBmsonScan();
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
        Stopwatch stopwatchDirhashBuild = Stopwatch.StartNew();
        Stopwatch stopwatchFolderHashIndex = Stopwatch.StartNew();
        result.NextFolderAllFileList = BMSDirectoryFileNameHash.CreateFromHashedDirectories(
            mergedScanResult.ChartDirectories,
            (mergedScanResult.SelfOwnedAllResourceBaseNameHashesByChartDirectory?.Count ?? 0) > 0
                ? mergedScanResult.SelfOwnedAllResourceBaseNameHashesByChartDirectory
                : mergedScanResult.AllResourceBaseNameHashesByChartDirectory);
        stopwatchFolderHashIndex.Stop();
        result.FolderHashIndexMs = stopwatchFolderHashIndex.ElapsedMilliseconds;

        Stopwatch stopwatchResourceLookupCache = Stopwatch.StartNew();
        result.NextDirectoryResourceLookupCache = DirectoryResourceLookupCache.CreateFromScanResult(mergedScanResult);
        stopwatchResourceLookupCache.Stop();
        result.ResourceLookupCacheMs = stopwatchResourceLookupCache.ElapsedMilliseconds;
        Stopwatch stopwatchRelativePathHashIndex = Stopwatch.StartNew();
        result.NextDirectoryRelativePathHashIndex = DirectoryRelativePathHashIndex.CreateFromScanResult(mergedScanResult);
        stopwatchRelativePathHashIndex.Stop();
        result.RelativePathHashIndexMs = stopwatchRelativePathHashIndex.ElapsedMilliseconds;
        stopwatchDirhashBuild.Stop();
        result.DirhashBuildMs = stopwatchDirhashBuild.ElapsedMilliseconds;
        result.AllBaseHashEntryCount = CountHashEntries(mergedScanResult.AllResourceBaseNameHashesByChartDirectory);
        result.AudioBaseHashEntryCount = CountHashEntries(mergedScanResult.AudioBaseNameHashesByChartDirectory);
        result.ImageBaseHashEntryCount = CountHashEntries(mergedScanResult.ImageBaseNameHashesByChartDirectory);
        result.MovieBaseHashEntryCount = CountHashEntries(mergedScanResult.MovieBaseNameHashesByChartDirectory);
        result.AudioRelativeHashEntryCount = CountHashEntries(mergedScanResult.AudioRelativePathHashesByChartDirectory);
        result.ImageRelativeHashEntryCount = CountHashEntries(mergedScanResult.ImageRelativePathHashesByChartDirectory);
        result.MovieRelativeHashEntryCount = CountHashEntries(mergedScanResult.MovieRelativePathHashesByChartDirectory);

        HashSet<string> scannedPaths = new HashSet<string>(
            (mergedScanResult.ChartFilePaths ?? new HashSet<string>(StringComparer.OrdinalIgnoreCase))
                .Where((string path) => !string.Equals(Path.GetExtension(path), ".bmson", StringComparison.OrdinalIgnoreCase)),
            StringComparer.OrdinalIgnoreCase);
        result.BmsPathCount = scannedPaths.Count;
        result.DirectoryCount = result.NextFolderAllFileList.Keys.Count;

        Stopwatch stopwatchDiff = Stopwatch.StartNew();
        List<BMSFile> currentFileList = (currentFiles ?? Enumerable.Empty<BMSFile>()).Where((BMSFile file) => file != null).ToList();
        HashSet<string> currentPaths = new HashSet<string>(currentFileList.Select((BMSFile file) => file.path), StringComparer.OrdinalIgnoreCase);
        result.DeletedPaths.AddRange(currentPaths.Except(scannedPaths, StringComparer.OrdinalIgnoreCase));
        List<string> addedPaths = scannedPaths.Except(currentPaths, StringComparer.OrdinalIgnoreCase).ToList();
        List<LR2SongDBExtended.bmson_song> currentBmsonList = (currentBmsonSongs ?? Enumerable.Empty<LR2SongDBExtended.bmson_song>())
            .Where((LR2SongDBExtended.bmson_song song) => song != null && !string.IsNullOrWhiteSpace(song.path))
            .ToList();
        HashSet<string> scannedBmsonPaths = new HashSet<string>(
            (mergedScanResult.ChartFilePaths ?? new HashSet<string>(StringComparer.OrdinalIgnoreCase))
                .Where((string path) => string.Equals(Path.GetExtension(path), ".bmson", StringComparison.OrdinalIgnoreCase)),
            StringComparer.OrdinalIgnoreCase);
        Dictionary<string, LR2SongDBExtended.bmson_song> currentBmsonByPath = currentBmsonList.ToDictionary((LR2SongDBExtended.bmson_song song) => song.path, StringComparer.OrdinalIgnoreCase);
        result.DeletedBmsonPaths.AddRange(currentBmsonByPath.Keys.Except(scannedBmsonPaths, StringComparer.OrdinalIgnoreCase));
        List<string> addedOrUpdatedBmsonPaths = scannedBmsonPaths
            .Where(delegate (string path)
            {
                if (!currentBmsonByPath.TryGetValue(path, out LR2SongDBExtended.bmson_song existing))
                {
                    return true;
                }
                return existing.updated_at != SafeGetLastWriteTimeUtc(path);
            })
            .ToList();
        stopwatchDiff.Stop();
        result.DiffMs = stopwatchDiff.ElapsedMilliseconds;
        result.BmsAddedTargetCount = addedPaths.Count;
        result.BmsonUpsertTargetCount = addedOrUpdatedBmsonPaths.Count;
        result.FileDiffParserDegree = ResolveFileDiffParserDegree();
        result.ParseReadBytesEstimate = SaturatingAdd(
            EstimateCurrentFileDiffReadBytes(addedPaths),
            EstimateCurrentFileDiffReadBytes(addedOrUpdatedBmsonPaths));

        long bmsLightweightParseMs = 0L;
        int parseTargetCount = addedPaths.Count + addedOrUpdatedBmsonPaths.Count;
        int parseProcessedCount = 0;
        if (parseTargetCount > 0)
        {
            reportParseProgress?.Invoke(parseTargetCount, 0, string.Empty);
        }
        result.InlineChartInfoBatchSize = ResolveInlineChartInfoBatchSize();
        Dictionary<string, LR2SongDBExtended.chart_info_parse_failure> currentChartInfoParseFailures =
            parseTargetCount > 0 && dbGateway != null
                ? dbGateway.LoadCurrentChartInfoParseFailureMap(chartInfoBuildService.CurrentParseTimeout)
                : new Dictionary<string, LR2SongDBExtended.chart_info_parse_failure>(StringComparer.OrdinalIgnoreCase);
        List<BMSFile> addedFiles = new List<BMSFile>();
        foreach (List<string> batch in CreateBatches(addedPaths, result.InlineChartInfoBatchSize))
        {
            Stopwatch stopwatchBatchParse = Stopwatch.StartNew();
            List<InlineBmsParseCandidate> candidates = batch.AsParallel()
                .WithDegreeOfParallelism(result.FileDiffParserDegree)
                .Select(delegate (string path)
                {
                    try
                    {
                        ChartFileSnapshot snapshot = ChartFileContentReader.ReadSnapshot(path);
                        BMSFile file = BMSFile.CreateBMSFileFromSnapshot(snapshot);
                        Lr2SongFolderParentNormalizer.ApplyIfMissingOrInvalid(file);
                        return InlineBmsParseCandidate.CreateSuccess(path, snapshot, file);
                    }
                    catch (IOException ex)
                    {
                        return InlineBmsParseCandidate.CreateFailure(path, ex);
                    }
                })
                .ToList();
            stopwatchBatchParse.Stop();
            bmsLightweightParseMs += stopwatchBatchParse.ElapsedMilliseconds;
            ProcessInlineBmsChartInfo(candidates, dbGateway, currentChartInfoParseFailures, result, logInstallPerformance, logInstallPerformanceWarn);
            foreach (InlineBmsParseCandidate candidate in candidates)
            {
                if (candidate.Exception != null)
                {
                    dialogService?.Show(string.Format(Resources.Error_InitializationFailed, candidate.Path, candidate.Exception.Message), Resources.MessageBoxTitle_Error, MessageBoxButton.OK, MessageBoxImage.Hand, MessageBoxResult.OK);
                }
                if (candidate.File != null)
                {
                    addedFiles.Add(candidate.File);
                }
                int processed = Interlocked.Increment(ref parseProcessedCount);
                reportParseProgress?.Invoke(parseTargetCount, processed, candidate.Path);
            }
        }
        result.NewFileParseMs = bmsLightweightParseMs;
        result.BmsParseMs = result.NewFileParseMs;
        result.AddedFiles.AddRange(addedFiles);

        Stopwatch stopwatchApply = Stopwatch.StartNew();
        result.NextFiles.AddRange(currentFileList.Where((BMSFile file) => !result.DeletedPaths.Contains(file.path)));
        result.NextFiles.AddRange(result.AddedFiles);
        HashSet<string> directoryKeys = new HashSet<string>(result.NextFolderAllFileList.Keys, StringComparer.OrdinalIgnoreCase);
        Stopwatch stopwatchInstlDstCleanup = Stopwatch.StartNew();
        foreach (BMSFile file in result.NextFiles.Where((BMSFile file) => !string.IsNullOrWhiteSpace(file.instl_dst)))
        {
            if (!directoryKeys.Contains(file.instl_dst))
            {
                file.instl_dst = null;
                result.ClearedInstallDestinations.Add(file);
            }
        }
        stopwatchInstlDstCleanup.Stop();
        result.InstlDstCleanupMs = stopwatchInstlDstCleanup.ElapsedMilliseconds;
        stopwatchApply.Stop();
        result.ApplyMs = stopwatchApply.ElapsedMilliseconds;

        result.NextBmsonSongs.AddRange(currentBmsonList);
        {
            long bmsonLightweightParseMs = 0L;
            HashSet<string> successfullyParsedBmsonPaths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            List<LR2SongDBExtended.bmson_song> parsedBmsonSongs = new List<LR2SongDBExtended.bmson_song>();
            foreach (List<string> batch in CreateBatches(addedOrUpdatedBmsonPaths, result.InlineChartInfoBatchSize))
            {
                Stopwatch stopwatchBatchParse = Stopwatch.StartNew();
                List<InlineBmsonParseCandidate> candidates = batch.AsParallel()
                    .WithDegreeOfParallelism(result.FileDiffParserDegree)
                    .Select(delegate (string path)
                    {
                        try
                        {
                            ChartFileSnapshot snapshot = ChartFileContentReader.ReadSnapshot(path);
                            return InlineBmsonParseCandidate.CreateSuccess(path, snapshot, BmsonSongParser.ParseSnapshot(snapshot));
                        }
                        catch (Exception ex)
                        {
                            return InlineBmsonParseCandidate.CreateFailure(path, ex);
                        }
                    })
                    .ToList();
                stopwatchBatchParse.Stop();
                bmsonLightweightParseMs += stopwatchBatchParse.ElapsedMilliseconds;
                ProcessInlineBmsonChartInfo(candidates, dbGateway, currentChartInfoParseFailures, result, logInstallPerformance, logInstallPerformanceWarn);
                foreach (InlineBmsonParseCandidate candidate in candidates)
                {
                    if (candidate.Exception != null)
                    {
                        logEverythingScan?.Invoke("bmson_parse_failed path=" + candidate.Path + " message=" + candidate.Exception.Message);
                    }
                    if (candidate.Song != null)
                    {
                        successfullyParsedBmsonPaths.Add(candidate.Path);
                        parsedBmsonSongs.Add(candidate.Song);
                    }
                    int processed = Interlocked.Increment(ref parseProcessedCount);
                    reportParseProgress?.Invoke(parseTargetCount, processed, candidate.Path);
                }
            }
            result.BmsonParseMs = bmsonLightweightParseMs;
            result.AddedBmsonSongs.AddRange(parsedBmsonSongs);

            HashSet<string> removedBmsonPaths = new HashSet<string>(result.DeletedBmsonPaths, StringComparer.OrdinalIgnoreCase);
            foreach (string updatedPath in successfullyParsedBmsonPaths)
            {
                removedBmsonPaths.Add(updatedPath);
            }
            result.NextBmsonSongs.Clear();
            result.NextBmsonSongs.AddRange(currentBmsonList.Where((LR2SongDBExtended.bmson_song song) => !removedBmsonPaths.Contains(song.path)));
            result.NextBmsonSongs.AddRange(result.AddedBmsonSongs);
            logEverythingScan?.Invoke("bmson_scan totalPaths=" + scannedBmsonPaths.Count + " deleted=" + result.DeletedBmsonPaths.Count + " upserted=" + result.AddedBmsonSongs.Count);
        }

        result.DirectoryCount = result.NextFolderAllFileList.Keys.Count;

        result.HasDbDiff = result.DeletedPaths.Count > 0 || result.AddedFiles.Count > 0 || result.DeletedBmsonPaths.Count > 0 || result.AddedBmsonSongs.Count > 0;

        if (result.HasDbDiff)
        {
            Stopwatch stopwatchDbCommit = Stopwatch.StartNew();
            using LR2SongDBExtended songDb = dbGateway.OpenSongDb();
            result.Pragmas.AddRange(songDb.TryApplyReadOptimizedPragmas(options?.EnableReadOptimizedPragmas ?? false));
            if (result.Pragmas.Count > 0)
            {
                logInstallPerformance?.Invoke("db_read_pragmas scope=song_tbl_file_check " + string.Join(" ", result.Pragmas));
            }
            string savepoint = songDb.SaveTransactionPoint();
            try
            {
                BmsLibraryDbGateway.EnsureBmsonSchema(songDb);
                foreach (string deletedPath in result.DeletedPaths)
                {
                    string deletedHash = songDb.ExecuteScalar<string>("SELECT hash FROM song WHERE path = " + BMSPlaylist.SqlQuoteForTest(deletedPath) + " LIMIT 1;");
                    songDb.Delete<LR2SongDB.song>(deletedPath);
                    BmsLibraryDbGateway.DeleteChartDigestIfOrphaned(songDb, deletedHash);
                }
                foreach (BMSFile addedFile in result.AddedFiles)
                {
                    Lr2SongFolderParentNormalizer.ApplyIfMissingOrInvalid(addedFile);
                    string previousHash = songDb.ExecuteScalar<string>("SELECT hash FROM song WHERE path = " + BMSPlaylist.SqlQuoteForTest(addedFile.path) + " LIMIT 1;");
                    songDb.InsertOrReplace(addedFile, typeof(LR2SongDB.song));
                    BmsLibraryDbGateway.UpsertChartDigest(songDb, addedFile);
                    BmsLibraryDbGateway.DeleteChartDigestIfOrphaned(songDb, previousHash, addedFile.hash);
                }
                foreach (string deletedBmsonPath in result.DeletedBmsonPaths)
                {
                    songDb.Delete<LR2SongDBExtended.bmson_song>(deletedBmsonPath);
                }
                foreach (LR2SongDBExtended.bmson_song addedBmsonSong in result.AddedBmsonSongs)
                {
                    songDb.InsertOrReplace(addedBmsonSong, typeof(LR2SongDBExtended.bmson_song));
                }
                BmsLibraryDbGateway.UpsertChartInfoBackfillChunk(
                    songDb,
                    Enumerable.Empty<ChartDigestBackfillEntry>(),
                    result.InlineChartInfoRows,
                    result.InlineChartInfoParseFailureRows,
                    result.InlineChartInfoParseFailureDeleteMd5s);
                songDb.Commit();
            }
            catch
            {
                songDb.RollbackTo(savepoint);
                throw;
            }
            stopwatchDbCommit.Stop();
            result.DbCommitMs = stopwatchDbCommit.ElapsedMilliseconds;
        }

        logInstallPerformance?.Invoke(
            "song_tbl_file_check_breakdown scan_ms=" + result.ScanElapsedMs
            + " native_bridge_ms=" + result.NativeBridgeMs
            + " native_bridge_reason=" + (string.IsNullOrWhiteSpace(result.NativeBridgeReason) ? string.Empty : result.NativeBridgeReason)
            + " managed_decode_ms=" + result.ManagedDecodeMs
            + " managed_materialize_ms=" + result.ManagedMaterializeMs
            + " bridge_raw_buffer_bytes=" + result.BridgeRawBufferBytes
            + " dirhash_build_ms=" + result.DirhashBuildMs
            + " folder_hash_index_ms=" + result.FolderHashIndexMs
            + " resource_lookup_cache_ms=" + result.ResourceLookupCacheMs
            + " relative_path_hash_index_ms=" + result.RelativePathHashIndexMs
            + " lazy_hash_cache_entries=" + (result.NextDirectoryResourceLookupCache?.LazyHashCacheEntryCount ?? 0)
            + " lazy_hash_build_ms=" + (result.NextDirectoryResourceLookupCache?.LazyHashBuildMs ?? 0L)
            + " lazy_hash_lookup_count=" + (result.NextDirectoryResourceLookupCache?.LazyHashLookupCount ?? 0L)
            + " diff_ms=" + result.DiffMs
            + " deleted_count=" + result.DeletedPaths.Count
            + " added_count=" + result.AddedFiles.Count
            + " bms_added_target_count=" + result.BmsAddedTargetCount
            + " bmson_deleted_count=" + result.DeletedBmsonPaths.Count
            + " bmson_upsert_count=" + result.AddedBmsonSongs.Count
            + " bmson_upsert_target_count=" + result.BmsonUpsertTargetCount
            + " file_diff_parser_degree=" + result.FileDiffParserDegree
            + " newfile_parse_ms=" + result.NewFileParseMs
            + " bms_parse_ms=" + result.BmsParseMs
            + " bmson_parse_ms=" + result.BmsonParseMs
            + " inline_chart_info_target_count=" + result.InlineChartInfoTargetCount
            + " inline_chart_info_success_count=" + result.InlineChartInfoSuccessCount
            + " inline_chart_info_current_skipped_count=" + result.InlineChartInfoCurrentSkippedCount
            + " inline_chart_info_failure_skipped_count=" + result.InlineChartInfoFailureSkippedCount
            + " inline_chart_info_parse_failed_count=" + result.InlineChartInfoParseFailedCount
            + " inline_chart_info_failure_persisted_count=" + result.InlineChartInfoFailurePersistedCount
            + " inline_chart_info_failure_cleared_count=" + result.InlineChartInfoFailureClearedCount
            + " inline_chart_info_parse_ms=" + result.InlineChartInfoParseMs
            + " inline_chart_info_batch_size=" + result.InlineChartInfoBatchSize
            + " parse_read_bytes_estimate=" + result.ParseReadBytesEstimate
            + " apply_ms=" + result.ApplyMs
            + " db_commit_ms=" + result.DbCommitMs
            + " instl_dst_cleanup_ms=" + result.InstlDstCleanupMs);
        logInstallPerformance?.Invoke(
            "song_tbl_file_check_cache_counts chartDirs=" + result.DirectoryCount
            + " allBaseHashEntries=" + result.AllBaseHashEntryCount
            + " audioBaseHashEntries=" + result.AudioBaseHashEntryCount
            + " imageBaseHashEntries=" + result.ImageBaseHashEntryCount
            + " movieBaseHashEntries=" + result.MovieBaseHashEntryCount
            + " audioRelHashEntries=" + result.AudioRelativeHashEntryCount
            + " imageRelHashEntries=" + result.ImageRelativeHashEntryCount
            + " movieRelHashEntries=" + result.MovieRelativeHashEntryCount);
        logEverythingScan?.Invoke("bms_scan totalMs=" + result.ScanElapsedMs
            + " nativeBridgeMs=" + result.NativeBridgeMs
            + " managedDecodeMs=" + result.ManagedDecodeMs
            + " managedMaterializeMs=" + result.ManagedMaterializeMs
            + " indexBuildMs=" + result.DirhashBuildMs
            + " folderHashIndexMs=" + result.FolderHashIndexMs
            + " resourceLookupCacheMs=" + result.ResourceLookupCacheMs
            + " relativePathHashIndexMs=" + result.RelativePathHashIndexMs
            + " bridgeReason=" + (string.IsNullOrWhiteSpace(result.NativeBridgeReason) ? string.Empty : result.NativeBridgeReason)
            + " bmsPaths=" + result.BmsPathCount
            + " dirs=" + result.DirectoryCount
            + " prefetched=" + result.PrefetchedScanUsed.ToString().ToLowerInvariant());
        return result;
    }

    private void ProcessInlineBmsChartInfo(
        IReadOnlyList<InlineBmsParseCandidate> candidates,
        BmsLibraryDbGateway dbGateway,
        IDictionary<string, LR2SongDBExtended.chart_info_parse_failure> currentFailures,
        SongTableFileCheckResult result,
        Action<string> logInstallPerformance,
        Action<string> logInstallPerformanceWarn)
    {
        if (candidates == null || candidates.Count == 0)
        {
            return;
        }
        List<InlineBmsParseCandidate> parsedCandidates = candidates
            .Where((InlineBmsParseCandidate candidate) => candidate?.File != null && candidate.Snapshot != null)
            .ToList();
        if (parsedCandidates.Count == 0)
        {
            return;
        }
        ChartInfoInlineBuildService inlineBuildService = new ChartInfoInlineBuildService(chartInfoBuildService, result.FileDiffParserDegree, result.InlineChartInfoBatchSize);
        ChartInfoInlineBuildResult inlineResult = inlineBuildService.BuildForSnapshots(
            dbGateway,
            parsedCandidates.Select((InlineBmsParseCandidate candidate) => new InlineBmsChartSnapshot(candidate.File, candidate.Snapshot)),
            null,
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
        Action<string> logInstallPerformance,
        Action<string> logInstallPerformanceWarn)
    {
        if (candidates == null || candidates.Count == 0)
        {
            return;
        }
        List<InlineBmsonParseCandidate> parsedCandidates = candidates
            .Where((InlineBmsonParseCandidate candidate) => candidate?.Song != null && candidate.Snapshot != null)
            .ToList();
        if (parsedCandidates.Count == 0)
        {
            return;
        }
        ChartInfoInlineBuildService inlineBuildService = new ChartInfoInlineBuildService(chartInfoBuildService, result.FileDiffParserDegree, result.InlineChartInfoBatchSize);
        ChartInfoInlineBuildResult inlineResult = inlineBuildService.BuildForSnapshots(
            dbGateway,
            null,
            parsedCandidates.Select((InlineBmsonParseCandidate candidate) => new InlineBmsonChartSnapshot(candidate.Song, candidate.Snapshot)),
            currentFailures,
            logInstallPerformance,
            logInstallPerformanceWarn);
        ApplyInlineChartInfoResult(result, inlineResult);
    }

    private static void ApplyInlineChartInfoResult(SongTableFileCheckResult result, ChartInfoInlineBuildResult inlineResult)
    {
        if (result == null || inlineResult == null)
        {
            return;
        }
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
        result.InlineChartInfoTargetCount += inlineResult.TargetCount;
        result.InlineChartInfoSuccessCount += inlineResult.SuccessCount;
        result.InlineChartInfoCurrentSkippedCount += inlineResult.CurrentSkippedCount;
        result.InlineChartInfoFailureSkippedCount += inlineResult.FailureSkippedCount;
        result.InlineChartInfoParseFailedCount += inlineResult.ParseFailedCount;
        result.InlineChartInfoFailurePersistedCount += inlineResult.FailurePersistedCount;
        result.InlineChartInfoFailureClearedCount += inlineResult.FailureClearedCount;
        result.InlineChartInfoParseMs += inlineResult.ParseMs;
    }

    private static IEnumerable<List<string>> CreateBatches(IEnumerable<string> paths, int batchSize)
    {
        List<string> batch = new List<string>(Math.Max(1, batchSize));
        foreach (string path in paths ?? Enumerable.Empty<string>())
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

    private sealed class InlineBmsParseCandidate
    {
        private InlineBmsParseCandidate(string path, ChartFileSnapshot snapshot, BMSFile file, IOException exception)
        {
            Path = path ?? string.Empty;
            Snapshot = snapshot;
            File = file;
            Exception = exception;
        }

        public string Path { get; }

        public ChartFileSnapshot Snapshot { get; }

        public BMSFile File { get; }

        public IOException Exception { get; }

        public static InlineBmsParseCandidate CreateSuccess(string path, ChartFileSnapshot snapshot, BMSFile file)
        {
            return new InlineBmsParseCandidate(path, snapshot, file, null);
        }

        public static InlineBmsParseCandidate CreateFailure(string path, IOException exception)
        {
            return new InlineBmsParseCandidate(path, null, null, exception);
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
        foreach (string path in paths ?? Enumerable.Empty<string>())
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
        return Math.Min(4, Math.Max(1, Environment.ProcessorCount - 1));
    }

    private int ResolveFileDiffParserDegree()
    {
        if (fileDiffParserDegreeOverride.HasValue)
        {
            return Math.Max(1, fileDiffParserDegreeOverride.Value);
        }
        return ResolveDefaultFileDiffParserDegree();
    }

    private int ResolveInlineChartInfoBatchSize()
    {
        if (inlineChartInfoBatchSizeOverride.HasValue)
        {
            return Math.Max(1, inlineChartInfoBatchSizeOverride.Value);
        }
        return DefaultInlineChartInfoBatchSize;
    }

    private static long SaturatingAdd(long left, long right)
    {
        if (left >= long.MaxValue || right >= long.MaxValue || long.MaxValue - left < right)
        {
            return long.MaxValue;
        }
        return left + right;
    }

    private static BmsScanResult MergeScanResults(params BmsScanResult[] scanResults)
    {
        BmsScanResult merged = new BmsScanResult();
        foreach (BmsScanResult scanResult in scanResults.Where((BmsScanResult scanResult) => scanResult != null))
        {
            merged.ChartFilePaths.UnionWith(scanResult.ChartFilePaths ?? new HashSet<string>(StringComparer.OrdinalIgnoreCase));
            merged.ChartDirectories.UnionWith(scanResult.ChartDirectories ?? new HashSet<string>(StringComparer.OrdinalIgnoreCase));
            MergeHashDictionary(merged.AllResourceBaseNameHashesByChartDirectory, scanResult.AllResourceBaseNameHashesByChartDirectory);
            MergeHashDictionary(merged.AudioBaseNameHashesByChartDirectory, scanResult.AudioBaseNameHashesByChartDirectory);
            MergeHashDictionary(merged.ImageBaseNameHashesByChartDirectory, scanResult.ImageBaseNameHashesByChartDirectory);
            MergeHashDictionary(merged.MovieBaseNameHashesByChartDirectory, scanResult.MovieBaseNameHashesByChartDirectory);
            MergeHashDictionary(merged.AudioRelativePathHashesByChartDirectory, scanResult.AudioRelativePathHashesByChartDirectory);
            MergeHashDictionary(merged.ImageRelativePathHashesByChartDirectory, scanResult.ImageRelativePathHashesByChartDirectory);
            MergeHashDictionary(merged.MovieRelativePathHashesByChartDirectory, scanResult.MovieRelativePathHashesByChartDirectory);
            MergeHashDictionary(merged.SelfOwnedAllResourceBaseNameHashesByChartDirectory, scanResult.SelfOwnedAllResourceBaseNameHashesByChartDirectory);
            MergeHashDictionary(merged.SelfOwnedAudioBaseNameHashesByChartDirectory, scanResult.SelfOwnedAudioBaseNameHashesByChartDirectory);
            MergeHashDictionary(merged.SelfOwnedImageBaseNameHashesByChartDirectory, scanResult.SelfOwnedImageBaseNameHashesByChartDirectory);
            MergeHashDictionary(merged.SelfOwnedMovieBaseNameHashesByChartDirectory, scanResult.SelfOwnedMovieBaseNameHashesByChartDirectory);
            MergeHashDictionary(merged.SelfOwnedAudioRelativePathHashesByChartDirectory, scanResult.SelfOwnedAudioRelativePathHashesByChartDirectory);
            MergeHashDictionary(merged.SelfOwnedImageRelativePathHashesByChartDirectory, scanResult.SelfOwnedImageRelativePathHashesByChartDirectory);
            MergeHashDictionary(merged.SelfOwnedMovieRelativePathHashesByChartDirectory, scanResult.SelfOwnedMovieRelativePathHashesByChartDirectory);
        }
        return merged;
    }

    private static void MergeHashDictionary(Dictionary<string, uint[]> destination, Dictionary<string, uint[]> source)
    {
        foreach (KeyValuePair<string, uint[]> item in source ?? new Dictionary<string, uint[]>(StringComparer.OrdinalIgnoreCase))
        {
            if (!destination.TryGetValue(item.Key, out uint[] existing) || existing == null || existing.Length == 0)
            {
                destination[item.Key] = item.Value ?? Array.Empty<uint>();
                continue;
            }
            if (item.Value == null || item.Value.Length == 0)
            {
                continue;
            }
            destination[item.Key] = existing.Concat(item.Value).Distinct().ToArray();
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
        ChartDigestBackfillResult result = new ChartDigestBackfillResult();
        if (dbGateway == null)
        {
            return result;
        }
        Stopwatch stopwatchTotal = Stopwatch.StartNew();
        List<BMSFile> targetFiles = (currentFiles ?? Enumerable.Empty<BMSFile>())
            .Where((BMSFile file) => file != null && !string.IsNullOrWhiteSpace(file.hash) && string.IsNullOrWhiteSpace(file.sha256) && !string.IsNullOrWhiteSpace(file.path) && File.Exists(file.path))
            .ToList();
        result.TargetCount = targetFiles.Count;
        reportProgress?.Invoke(result.TargetCount, 0, string.Empty);
        if (targetFiles.Count == 0)
        {
            stopwatchTotal.Stop();
            result.TotalMs = stopwatchTotal.ElapsedMilliseconds;
            return result;
        }
        Stopwatch stopwatchCompute = Stopwatch.StartNew();
        List<BMSFile> completedFiles = new List<BMSFile>(targetFiles.Count);
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
        Stopwatch stopwatchDbCommit = Stopwatch.StartNew();
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

    public ScoreTableLoadResult LoadScoreTable(BmsLibraryDbGateway dbGateway)
    {
        if (dbGateway == null || string.IsNullOrWhiteSpace(dbGateway.ScoreDbPath))
        {
            return new ScoreTableLoadResult();
        }
        try
        {
            return dbGateway.LoadScoresAndPlayerId();
        }
        catch
        {
            return new ScoreTableLoadResult();
        }
    }

    public InstallTableLoadResult LoadInstallTable(
        BmsLibraryDbGateway dbGateway,
        Func<BMSFile, bool> isInstalledChart = null,
        Func<BMSFile, bool> applyStrictWarning = null)
    {
        InstallTableLoadResult result = new InstallTableLoadResult();
        if (dbGateway == null)
        {
            return result;
        }
        Stopwatch totalStopwatch = Stopwatch.StartNew();
        Stopwatch loadStopwatch = Stopwatch.StartNew();
        try
        {
            List<BMSPackage> packages = dbGateway.LoadInstallPackages();
            result.PendingPackages.AddRange(packages.Where((BMSPackage pkg) => pkg != null && (File.Exists(pkg.path) || Directory.Exists(pkg.path)) && pkg.BMSFiles.Count > 0));
            result.StalePackages.AddRange(packages.Except(result.PendingPackages));
            result.StaleInstallPaths.AddRange(result.StalePackages.Where((BMSPackage pkg) => !string.IsNullOrWhiteSpace(pkg.path)).Select((BMSPackage pkg) => pkg.path));
        }
        catch
        {
            totalStopwatch.Stop();
            result.TotalMs = totalStopwatch.ElapsedMilliseconds;
            return result;
        }
        loadStopwatch.Stop();
        result.LoadMs = loadStopwatch.ElapsedMilliseconds;
        Stopwatch warningStopwatch = Stopwatch.StartNew();
        foreach (BMSPackage pendingPackage in result.PendingPackages)
        {
            bool isSingleFilePackage = !Directory.Exists(pendingPackage.path);
            foreach (BMSFile bmsFile in (pendingPackage.BMSFiles ?? new List<BMSFile>()).Where((BMSFile file) => file != null))
            {
                bool isBmson = PendingChartEntry.IsBmsonChartFile(bmsFile);
                if (!isBmson)
                {
                    bmsFile.SetHealthStatus(null, forceUpdate: false, memClear: false);
                }
                result.PendingWarningInitTargets.Add(bmsFile);
                if (isInstalledChart != null && isInstalledChart(bmsFile))
                {
                    bmsFile.ClearWarningsByCategory(ChartWarningCategory.InstalledState);
                    bmsFile.SetWarning(ChartWarningKind.AlreadyInstalled, Resources.Warning_AlreadyInstalled);
                    result.InstalledWarningCount++;
                }
                else if (isSingleFilePackage)
                {
                    bmsFile.ClearWarningsByCategory(ChartWarningCategory.PackageLayout);
                    bmsFile.SetWarning(isBmson ? ChartWarningKind.SingleBmsonFile : ChartWarningKind.SingleBmsFile, isBmson ? Resources.Warning_SingleBmsonFile : Resources.Warning_SingleBmsFile);
                    result.SingleFileWarningCount++;
                }
                else if (applyStrictWarning != null && applyStrictWarning(bmsFile))
                {
                    result.StrictWarningCount++;
                }
            }
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
        InitializationExecutionResult result = new InitializationExecutionResult();
        Stopwatch stopwatchTotal = Stopwatch.StartNew();
        List<Task> continuationTasks = new List<Task>();
        Stopwatch stopwatchPhase1 = Stopwatch.StartNew();
        phase1?.Invoke();
        stopwatchPhase1.Stop();
        result.Phase1MinLoadMs = stopwatchPhase1.ElapsedMilliseconds;

        long waitBeforeContinuationStartMs = 0L;
        long waitForContinuationCompleteMs = 0L;
        Stopwatch stopwatchWaitBeforeContinuationStart = Stopwatch.StartNew();
        semaphore?.Wait();
        stopwatchWaitBeforeContinuationStart.Stop();
        waitBeforeContinuationStartMs = stopwatchWaitBeforeContinuationStart.ElapsedMilliseconds;

        if (tasksContinuation != null)
        {
            for (int i = 0; i < tasksContinuation.Count; i++)
            {
                continuationTasks.Add(Task.Run(tasksContinuation[i]).Logging("Initialize"));
            }
        }

        Thread.Yield();

        Stopwatch stopwatchPhase2 = Stopwatch.StartNew();
        phase2?.Invoke();
        stopwatchPhase2.Stop();
        result.Phase2ScanMaintMs = stopwatchPhase2.ElapsedMilliseconds;

        Stopwatch stopwatchPhase3 = Stopwatch.StartNew();
        phase3?.Invoke();
        stopwatchPhase3.Stop();
        result.Phase3InstallMaintenanceMs = stopwatchPhase3.ElapsedMilliseconds;

        if (semaphore != null && tasksContinuation != null && tasksContinuation.Count > 0)
        {
            Stopwatch stopwatchWaitForContinuationComplete = Stopwatch.StartNew();
            semaphore.Wait();
            stopwatchWaitForContinuationComplete.Stop();
            waitForContinuationCompleteMs = stopwatchWaitForContinuationComplete.ElapsedMilliseconds;
        }
        result.WaitContinuationMs = waitBeforeContinuationStartMs + waitForContinuationCompleteMs;

        Task.WaitAll(continuationTasks.ToArray());
        stopwatchTotal.Stop();
        result.TotalMs = stopwatchTotal.ElapsedMilliseconds;
        return result;
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
        Encoding crcEncoding = Encoding.GetEncoding("shift_jis", EncoderFallback.ExceptionFallback, DecoderFallback.ExceptionFallback);
        int unixtime = (DateTime.Now + new TimeSpan(30, 0, 0, 0)).ToUnixtime();
        Stopwatch stopwatchSongNormalizeLoop = Stopwatch.StartNew();
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

        Stopwatch stopwatchFolderTableLoad = Stopwatch.StartNew();
        List<LR2SongDB.folder> folders = songDb.Table<LR2SongDB.folder>().ToList();
        stopwatchFolderTableLoad.Stop();
        result.FolderTableLoadMs = stopwatchFolderTableLoad.ElapsedMilliseconds;

        Stopwatch stopwatchFolderNormalizeLoop = Stopwatch.StartNew();
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
                        if (folder.parent != "e2977170")
                        {
                            folder.parent = ComputeLR2DirectoryHash(directoryName, crcEncoding);
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

        Stopwatch stopwatchFixApply = Stopwatch.StartNew();
        result.DbWriteRequired = result.DeletedSongPaths.Count > 0 || result.UpdatedSongs.Count > 0 || result.DeletedFolderPaths.Count > 0 || result.UpdatedFolders.Count > 0;
        if (result.DbWriteRequired)
        {
            Stopwatch stopwatchDbWrite = Stopwatch.StartNew();
            songDb.BeginTransaction();
            foreach (string deletedSongPath in result.DeletedSongPaths)
            {
                string deletedHash = songDb.ExecuteScalar<string>("SELECT hash FROM song WHERE path = " + BMSPlaylist.SqlQuoteForTest(deletedSongPath) + " LIMIT 1;");
                songDb.Delete<LR2SongDB.song>(deletedSongPath);
                BmsLibraryDbGateway.DeleteChartDigestIfOrphaned(songDb, deletedHash);
            }
            foreach (BMSFile updatedSong in result.UpdatedSongs)
            {
                string previousHash = songDb.ExecuteScalar<string>("SELECT hash FROM song WHERE path = " + BMSPlaylist.SqlQuoteForTest(updatedSong.path) + " LIMIT 1;");
                songDb.InsertOrReplace(updatedSong, typeof(LR2SongDB.song));
                BmsLibraryDbGateway.UpsertChartDigest(songDb, updatedSong);
                BmsLibraryDbGateway.DeleteChartDigestIfOrphaned(songDb, previousHash, updatedSong.hash);
            }
            foreach (string deletedFolderPath in result.DeletedFolderPaths)
            {
                songDb.Delete<LR2SongDB.folder>(deletedFolderPath);
            }
            foreach (LR2SongDB.folder updatedFolder in result.UpdatedFolders)
            {
                songDb.InsertOrReplace(updatedFolder, typeof(LR2SongDB.folder));
            }
            Stopwatch stopwatchCommit = Stopwatch.StartNew();
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

    private static string ComputeLR2DirectoryHash(string directoryPath, Encoding encoding)
    {
        return LR2CRC32.Compute(encoding.GetBytes((directoryPath ?? string.Empty) + "\\\0")).ToString("x");
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
