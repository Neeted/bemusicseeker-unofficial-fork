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

internal sealed class BmsLibraryInitializationService
{
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
        stopwatchMaintenanceApply.Stop();
        result.MaintenanceApplyMs = stopwatchMaintenanceApply.ElapsedMilliseconds;

        logInstallPerformance?.Invoke(
            "song_tbl_load_maintenance_detail bmsCount=" + result.LoadedFiles.Count
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
        Action<string> logEverythingScan = null)
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
        if (scanResult?.Result == null)
        {
            return result;
        }
        result.ScanElapsedMs = stopwatchScan.ElapsedMilliseconds + (result.PrefetchedScanUsed ? prefetchedScanElapsedMs : 0L);
        result.NextFolderAllFileList = new BMSDirectoryFileNameHash();

        Stopwatch stopwatchDirhashBuild = Stopwatch.StartNew();
        if (scanResult.Result.FileNameHashesByDirectory != null && scanResult.Result.FileNameHashesByDirectory.Count > 0)
        {
            foreach (KeyValuePair<string, uint[]> item in scanResult.Result.FileNameHashesByDirectory)
            {
                result.NextFolderAllFileList.AddDirHashed(item.Key, item.Value);
            }
        }
        else
        {
            foreach (KeyValuePair<string, List<string>> item in scanResult.Result.FilesByDirectory)
            {
                result.NextFolderAllFileList.AddDir(item.Key, item.Value);
            }
        }
        stopwatchDirhashBuild.Stop();
        result.DirhashBuildMs = stopwatchDirhashBuild.ElapsedMilliseconds;

        HashSet<string> scannedPaths = new HashSet<string>(scanResult.Result.BmsFilePaths ?? new HashSet<string>(), StringComparer.OrdinalIgnoreCase);
        result.BmsPathCount = scannedPaths.Count;
        result.DirectoryCount = result.NextFolderAllFileList.Keys.Count;

        Stopwatch stopwatchDiff = Stopwatch.StartNew();
        List<BMSFile> currentFileList = (currentFiles ?? Enumerable.Empty<BMSFile>()).Where((BMSFile file) => file != null).ToList();
        HashSet<string> currentPaths = new HashSet<string>(currentFileList.Select((BMSFile file) => file.path), StringComparer.OrdinalIgnoreCase);
        result.DeletedPaths.AddRange(currentPaths.Except(scannedPaths, StringComparer.OrdinalIgnoreCase));
        List<string> addedPaths = scannedPaths.Except(currentPaths, StringComparer.OrdinalIgnoreCase).ToList();
        stopwatchDiff.Stop();
        result.DiffMs = stopwatchDiff.ElapsedMilliseconds;

        Stopwatch stopwatchNewFileParse = Stopwatch.StartNew();
        List<BMSFile> addedFiles = addedPaths.Count <= 0
            ? new List<BMSFile>()
            : (from x in addedPaths.AsParallel().Select(delegate (string path)
                {
                    BMSFile file = null;
                    try
                    {
                        file = BMSFile.CreateBMSFileFromFile(path);
                    }
                    catch (IOException ex)
                    {
                        dialogService?.Show(string.Format(Resources.Error_InitializationFailed, path, ex.Message), Resources.MessageBoxTitle_Error, MessageBoxButton.OK, MessageBoxImage.Hand, MessageBoxResult.OK);
                    }
                    return file;
                })
               where x != null
               select x).ToList();
        stopwatchNewFileParse.Stop();
        result.NewFileParseMs = stopwatchNewFileParse.ElapsedMilliseconds;
        result.AddedFiles.AddRange(addedFiles);
        result.HasDbDiff = result.DeletedPaths.Count > 0 || result.AddedFiles.Count > 0;

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

        if (result.HasDbDiff)
        {
            Stopwatch stopwatchDbCommit = Stopwatch.StartNew();
            using LR2SongDBExtended songDb = dbGateway.OpenSongDb();
            result.Pragmas.AddRange(songDb.TryApplyReadOptimizedPragmas(options?.EnableReadOptimizedPragmas ?? false));
            if (result.Pragmas.Count > 0)
            {
                logInstallPerformance?.Invoke("db_read_pragmas scope=song_tbl_file_check " + string.Join(" ", result.Pragmas));
            }
            songDb.BeginTransaction();
            foreach (string deletedPath in result.DeletedPaths)
            {
                songDb.Delete<LR2SongDB.song>(deletedPath);
            }
            foreach (BMSFile addedFile in result.AddedFiles)
            {
                songDb.InsertOrReplace(addedFile, typeof(LR2SongDB.song));
            }
            songDb.Commit();
            stopwatchDbCommit.Stop();
            result.DbCommitMs = stopwatchDbCommit.ElapsedMilliseconds;
        }

        logInstallPerformance?.Invoke(
            "song_tbl_file_check_breakdown scan_ms=" + result.ScanElapsedMs
            + " dirhash_build_ms=" + result.DirhashBuildMs
            + " diff_ms=" + result.DiffMs
            + " deleted_count=" + result.DeletedPaths.Count
            + " added_count=" + result.AddedFiles.Count
            + " newfile_parse_ms=" + result.NewFileParseMs
            + " apply_ms=" + result.ApplyMs
            + " db_commit_ms=" + result.DbCommitMs
            + " instl_dst_cleanup_ms=" + result.InstlDstCleanupMs);
        logEverythingScan?.Invoke("bms_scan totalMs=" + result.ScanElapsedMs + " bmsPaths=" + result.BmsPathCount + " dirs=" + result.DirectoryCount + " prefetched=" + result.PrefetchedScanUsed.ToString().ToLowerInvariant());
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
        Func<string, bool> isInstalledHash = null,
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
                bmsFile.SetHealthStatus(null, forceUpdate: false, memClear: false);
                result.PendingWarningInitTargets.Add(bmsFile);
                if (isInstalledHash != null && IsBmsHashAvailable(bmsFile.hash) && isInstalledHash(bmsFile.hash))
                {
                    bmsFile.warning = Resources.Warning_AlreadyInstalled;
                    result.InstalledWarningCount++;
                }
                else if (isSingleFilePackage)
                {
                    bmsFile.warning = Resources.Warning_SingleBmsFile;
                    result.SingleFileWarningCount++;
                }
                else if (applyStrictWarning != null && applyStrictWarning(bmsFile))
                {
                    result.StrictWarningCount++;
                }
            }
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
                if (!Path.IsPathRooted(song.path))
                {
                    result.DeletedSongPaths.Add(song.path);
                    song.path = Path.Combine(songDb.LR2RootPath, song.path);
                    string directoryName = Path.GetDirectoryName(song.path);
                    song.folder = ComputeLR2DirectoryHash(directoryName, crcEncoding);
                    song.parent = ComputeLR2DirectoryHash(Path.GetDirectoryName(directoryName), crcEncoding);
                    result.UpdatedSongs.Add(song);
                    result.RelativePathFixedCount++;
                    result.CrcRecalculatedCount++;
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
                if (IsLikelyCrcHex(song.folder) && IsLikelyCrcHex(song.parent))
                {
                    result.CrcSkippedCount++;
                    continue;
                }
                string directoryName2 = Path.GetDirectoryName(song.path);
                string expectedFolder = ComputeLR2DirectoryHash(directoryName2, crcEncoding);
                string expectedParent = ComputeLR2DirectoryHash(Path.GetDirectoryName(directoryName2), crcEncoding);
                if (expectedFolder != song.folder || expectedParent != song.parent)
                {
                    song.folder = expectedFolder;
                    song.parent = expectedParent;
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
                songDb.Delete<LR2SongDB.song>(deletedSongPath);
            }
            foreach (BMSFile updatedSong in result.UpdatedSongs)
            {
                songDb.InsertOrReplace(updatedSong, typeof(LR2SongDB.song));
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

    private static bool IsLikelyCrcHex(string value)
    {
        if (string.IsNullOrWhiteSpace(value) || value.Length < 1 || value.Length > 8)
        {
            return false;
        }
        for (int i = 0; i < value.Length; i++)
        {
            char c = value[i];
            if (!((c >= '0' && c <= '9') || (c >= 'a' && c <= 'f') || (c >= 'A' && c <= 'F')))
            {
                return false;
            }
        }
        return true;
    }

    private static string ComputeLR2DirectoryHash(string directoryPath, Encoding encoding)
    {
        return LR2CRC32.Compute(encoding.GetBytes((directoryPath ?? string.Empty) + "\\\0")).ToString("x");
    }

    private static bool IsLeapYearTimestamp(DateTime lastWriteTime)
    {
        return (new DateTime(2012, 2, 29) <= lastWriteTime && lastWriteTime < new DateTime(2012, 3, 2))
            || (new DateTime(2016, 2, 29) <= lastWriteTime && lastWriteTime < new DateTime(2016, 3, 2))
            || (new DateTime(2020, 2, 29) <= lastWriteTime && lastWriteTime < new DateTime(2020, 3, 2));
    }

    private static bool IsBmsHashAvailable(string hash)
    {
        return !string.IsNullOrWhiteSpace(hash) && LR2SongDB.md5HashRegex.IsMatch(hash);
    }
}
