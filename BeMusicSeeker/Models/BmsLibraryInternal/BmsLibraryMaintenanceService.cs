using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Security;
using System.Threading;
using BeMusicSeeker.Properties;
using System.Windows;
using Ribbit.Util.Extensions;

namespace BeMusicSeeker.Models.BmsLibraryInternal;

/// <summary>
/// Computes and persists maintenance state for snapshots owned by BMSLibrary.
/// The facade must acquire the required locks before invoking this service.
/// </summary>
internal sealed class BmsLibraryMaintenanceService
{
    public bool ApplyNeedToBeFixedWarnings(BMSFile bmsFile, BMSFileMaintenanceInfo maintenanceInfo = null, bool strictCheck = false)
    {
        if (bmsFile == null)
        {
            return false;
        }
        _ = strictCheck;
        bool hasAnyWarning = false;
        maintenanceInfo ??= bmsFile.maintenanceInfo;
        if (!string.IsNullOrWhiteSpace(bmsFile.warning))
        {
            bmsFile.warning = null;
        }
        hasAnyWarning |= AppendHealthWarning(bmsFile, maintenanceInfo.GetWAVHealth(), maintenanceInfo.wav_files_defined, maintenanceInfo.wav_files_existing, Resources.Warning_WavFilesNotFound);
        hasAnyWarning |= AppendHealthWarning(bmsFile, maintenanceInfo.GetBGAHealth(), maintenanceInfo.bga_files_defined, maintenanceInfo.bga_files_existing, Resources.Warning_BgaFilesNotFound);
        hasAnyWarning |= AppendHealthWarning(bmsFile, maintenanceInfo.GetMovieHealth(), maintenanceInfo.movie_files_defined, maintenanceInfo.movie_files_existing, Resources.Warning_MovieFilesNotFound);
        hasAnyWarning |= AppendFlagWarning(bmsFile, maintenanceInfo.GetStagefileHealth(), Resources.Warning_StagefileNotFound);
        hasAnyWarning |= AppendFlagWarning(bmsFile, maintenanceInfo.GetBackbmpHealth(), Resources.Warning_BackbmpNotFound);
        hasAnyWarning |= AppendFlagWarning(bmsFile, maintenanceInfo.GetBannerHealth(), Resources.Warning_BannerNotFound);
        return hasAnyWarning;
    }

    public List<BMSFile> GetGarbledFiles(IEnumerable<BMSFile> bmsFiles, bool isInFixedList)
    {
        return (bmsFiles ?? Enumerable.Empty<BMSFile>())
            .Where((BMSFile file) => !string.IsNullOrWhiteSpace(file?.maintenanceInfo?.encoding)
                && isInFixedList == file.maintenanceInfo.is_encoding_fixed
                && !file.maintenanceInfo.encoding.StartsWith("shift_jis"))
            .ToList();
    }

    public List<BMSFile> GetZeroNoteFiles(IEnumerable<BMSFile> bmsFiles)
    {
        return (bmsFiles ?? Enumerable.Empty<BMSFile>()).Where((BMSFile file) => file != null && file.notes == 0).ToList();
    }

    public int CleanupMaintenanceTable(IEnumerable<BMSFile> bmsFiles, BmsLibraryDbGateway dbGateway)
    {
        List<string> currentPaths = (bmsFiles ?? Enumerable.Empty<BMSFile>())
            .Where((BMSFile file) => file != null && !string.IsNullOrWhiteSpace(file.path))
            .Select((BMSFile file) => file.path)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
        List<string> stalePaths = new List<string>();
        dbGateway.ExecuteSongDbTransaction(delegate (Models.LR2.LR2SongDBExtended songDb)
        {
            stalePaths = (from m in songDb.Table<BMSFileMaintenanceInfo>().ToList()
                          select m.path).Except(currentPaths, StringComparer.OrdinalIgnoreCase).ToList();
            foreach (string stalePath in stalePaths)
            {
                songDb.Delete<Models.LR2.LR2SongDBExtended.maintenance>(stalePath);
            }
        });
        return stalePaths.Count;
    }

    public List<BMSFileMaintenanceInfo> SetFilesWarningIgnored(IEnumerable<BMSFile> bmsFiles, bool unset)
    {
        List<BMSFileMaintenanceInfo> changes = (from f in bmsFiles ?? Enumerable.Empty<BMSFile>()
                                                let m = f?.maintenanceInfo
                                                where m != null && m.is_files_warning_ignored == unset
                                                select m).ToList();
        foreach (BMSFileMaintenanceInfo item in changes)
        {
            item.is_files_warning_ignored = !unset;
        }
        return changes;
    }

    public MaintenanceEncodingUpdateResult ApplyEncoding(IEnumerable<BMSFile> bmsFiles, string encoding)
    {
        MaintenanceEncodingUpdateResult result = new MaintenanceEncodingUpdateResult();
        List<BMSFile> files = (bmsFiles ?? Enumerable.Empty<BMSFile>()).Where((BMSFile file) => file != null).ToList();
        HashSet<BMSFile> reloadedFiles = new HashSet<BMSFile>();
        if (!string.IsNullOrWhiteSpace(encoding))
        {
            foreach (BMSFile file in files.Where((BMSFile f) => File.Exists(f.path)))
            {
                if (!ShouldReloadMetadata(file, encoding))
                {
                    continue;
                }
                BMSFile.ReloadBMSFileWithEncoding(file, encoding);
                result.SongsToUpsert.Add(file);
                reloadedFiles.Add(file);
            }
        }
        List<BMSFileMaintenanceInfo> maintenanceChanges = files.Select(delegate (BMSFile file)
        {
            BMSFileMaintenanceInfo info = file.maintenanceInfo;
            if (info == null)
            {
                return null;
            }
            if (!string.IsNullOrWhiteSpace(encoding))
            {
                if (reloadedFiles.Contains(file) || !string.Equals(info.encoding, encoding, StringComparison.Ordinal))
                {
                    info.encoding = encoding;
                    info.is_encoding_fixed = true;
                    return info;
                }
                return null;
            }
            if ((info.encoding.EndsWith("?") && info.encoding != "shift_jis?") || info.encoding == "unknown")
            {
                info.encoding = "shift_jis";
                info.is_encoding_fixed = true;
                return info;
            }
            return null;
        }).Where((BMSFileMaintenanceInfo info) => info != null).ToList();
        result.MaintenanceInfosToUpsert.AddRange(maintenanceChanges);
        return result;
    }

    private static bool ShouldReloadMetadata(BMSFile currentFile, string encoding)
    {
        if (currentFile == null || string.IsNullOrWhiteSpace(currentFile.path) || !File.Exists(currentFile.path))
        {
            return false;
        }
        BMSFile reloadedFile = BMSFile.CreateBMSFileFromFile(currentFile.path, encoding);
        return !string.Equals(currentFile.Title ?? string.Empty, reloadedFile.Title ?? string.Empty, StringComparison.Ordinal)
            || !string.Equals(currentFile.Artist ?? string.Empty, reloadedFile.Artist ?? string.Empty, StringComparison.Ordinal)
            || !string.Equals(currentFile.genre ?? string.Empty, reloadedFile.genre ?? string.Empty, StringComparison.Ordinal);
    }

    public ZeroNoteRecheckResult RecheckZeroNoteWarnings(IEnumerable<BMSFile> allFiles, Action<Exception, string> logWarn = null)
    {
        List<BMSFile> files = (allFiles ?? Enumerable.Empty<BMSFile>()).Where((BMSFile f) => f != null).ToList();
        List<BMSFile> zeroNoteFiles = files.Where((BMSFile f) => f.notes == 0 && !string.IsNullOrWhiteSpace(f.path)).ToList();
        List<BMSFile> staleMismatchFiles = files.Where((BMSFile f) => f.notes != 0 && f.HasZeroNoteMismatchWarning).ToList();
        ZeroNoteRecheckResult result = new ZeroNoteRecheckResult
        {
            Total = zeroNoteFiles.Count
        };
        foreach (BMSFile staleMismatchFile in staleMismatchFiles)
        {
            if (staleMismatchFile.HasZeroNoteMismatchWarning)
            {
                staleMismatchFile.HasZeroNoteMismatchWarning = false;
                result.ClearedCount++;
            }
        }
        foreach (BMSFile zeroNoteFile in zeroNoteFiles)
        {
            try
            {
                bool isZeroNoteByFile = BMSFile.IsZeroNoteBMSFile(zeroNoteFile.path);
                if (!isZeroNoteByFile)
                {
                    zeroNoteFile.HasZeroNoteMismatchWarning = true;
                    result.MismatchCount++;
                }
                else
                {
                    if (zeroNoteFile.HasZeroNoteMismatchWarning)
                    {
                        result.ClearedCount++;
                    }
                    zeroNoteFile.HasZeroNoteMismatchWarning = false;
                }
            }
            catch (Exception ex) when (ex is DirectoryNotFoundException || ex is FileNotFoundException || ex is IOException || ex is PathTooLongException || ex is System.Security.SecurityException || ex is UnauthorizedAccessException)
            {
                if (zeroNoteFile.HasZeroNoteMismatchWarning)
                {
                    result.ClearedCount++;
                }
                zeroNoteFile.HasZeroNoteMismatchWarning = false;
                result.SkippedCount++;
                logWarn?.Invoke(ex, "zero_note_recheck skipped: path=" + zeroNoteFile.path);
            }
        }
        return result;
    }

    public List<BMSFile> DetectModeChanges(IEnumerable<BMSFile> bmsFiles, bool forceUpdate)
    {
        List<BMSFile> targets = (bmsFiles ?? Enumerable.Empty<BMSFile>())
            .Where((BMSFile file) => file != null && (forceUpdate || !file.mode.HasValue) && File.Exists(file.path))
            .ToList();
        foreach (BMSFile target in targets)
        {
            target.SetMode();
        }
        return targets;
    }

    public MaintenanceWorkflowResult UpdateMaintenanceInfo(
        IEnumerable<BMSFile> bmsFiles,
        bool forceUpdate,
        BMSDirectoryFileNameHash folderAllFileList,
        BmsLibraryDbGateway dbGateway,
        IBmsLibraryDialogService dialogService)
    {
        MaintenanceWorkflowResult result = new MaintenanceWorkflowResult();
        if (bmsFiles == null || dbGateway == null)
        {
            return result;
        }
        Stopwatch stopwatch = Stopwatch.StartNew();
        List<BMSFile> targets = (forceUpdate
            ? (bmsFiles ?? Enumerable.Empty<BMSFile>()).Where((BMSFile file) => file != null).ToList()
            : (bmsFiles ?? Enumerable.Empty<BMSFile>()).Where((BMSFile file) => file != null && (!file.maintenanceInfo.IsInformationChecked() || string.IsNullOrWhiteSpace(file.maintenanceInfo.encoding))).ToList());
        result.CheckedFileCount = targets.Count;
        foreach (IEnumerable<BMSFile> section in targets.Section(1000))
        {
            object reloadedLock = new object();
            List<BMSFile> reloadedFiles = new List<BMSFile>();
            List<BMSFile> filesInSection = section.Where((BMSFile file) => file != null).ToList();
            filesInSection.AsParallel().ForAll(delegate (BMSFile file)
            {
                string originalHash = file.hash;
                int retryCount = 0;
                while (true)
                {
                    try
                    {
                        file.SetHealthStatus(folderAllFileList, forceUpdate);
                        break;
                    }
                    catch (Exception ex)
                    {
                        if (ex is DirectoryNotFoundException || ex is FileNotFoundException || ex is IOException || ex is PathTooLongException || ex is SecurityException || ex is UnauthorizedAccessException)
                        {
                            if (retryCount < 3)
                            {
                                retryCount++;
                                Thread.Sleep(200);
                                continue;
                            }
                            dialogService?.Show(string.Format(Resources.Error_BmsLoadFailedSkip, file.path, ex.Message), Resources.MessageBoxTitle_Error, MessageBoxButton.OK, MessageBoxImage.Hand, MessageBoxResult.OK);
                            return;
                        }
                        throw;
                    }
                }
                if (forceUpdate || string.IsNullOrWhiteSpace(file.maintenanceInfo.encoding))
                {
                    file.SetEncosingInfo();
                }
                if (!string.IsNullOrWhiteSpace(file.maintenanceInfo.encoding) && !file.maintenanceInfo.encoding.StartsWith("shift_jis") && !file.maintenanceInfo.encoding.EndsWith("?") && file.maintenanceInfo.encoding != "unknown")
                {
                    BMSFile.ReloadBMSFileWithEncoding(file, file.maintenanceInfo.encoding);
                    file.maintenanceInfo.is_encoding_fixed = true;
                    lock (reloadedLock)
                    {
                        reloadedFiles.Add(file);
                        return;
                    }
                }
                if (originalHash != file.hash)
                {
                    lock (reloadedLock)
                    {
                        reloadedFiles.Add(file);
                    }
                }
            });
            List<BMSFileMaintenanceInfo> maintenanceInfos = filesInSection.Where((BMSFile file) => file.maintenanceInfo.IsInformationChecked()).Select((BMSFile file) => file.maintenanceInfo).ToList();
            if (maintenanceInfos.Count > 0 || reloadedFiles.Count > 0)
            {
                dbGateway.ExecuteSongDbTransaction(delegate (Models.LR2.LR2SongDBExtended songDb)
                {
                    foreach (BMSFileMaintenanceInfo maintenanceInfo in maintenanceInfos)
                    {
                        songDb.InsertOrReplace(maintenanceInfo, typeof(Models.LR2.LR2SongDBExtended.maintenance));
                    }
                    foreach (BMSFile reloadedFile in reloadedFiles)
                    {
                        songDb.InsertOrReplace(reloadedFile, typeof(Models.LR2.LR2SongDB.song));
                    }
                });
                result.HasUpdates = true;
                result.MaintenanceInfoUpsertCount += maintenanceInfos.Count;
                result.ReloadedSongCount += reloadedFiles.Count;
                result.SongUpsertCount += reloadedFiles.Count;
            }
        }
        stopwatch.Stop();
        result.TotalMs = stopwatch.ElapsedMilliseconds;
        return result;
    }

    public MaintenanceWorkflowResult UpdateZeroNoteAndCommit(
        IEnumerable<BMSFile> bmsFiles,
        BmsLibraryDbGateway dbGateway,
        IBmsLibraryDialogService dialogService)
    {
        MaintenanceWorkflowResult result = new MaintenanceWorkflowResult();
        if (bmsFiles == null || dbGateway == null)
        {
            return result;
        }
        Stopwatch stopwatch = Stopwatch.StartNew();
        List<BMSFile> targetFiles = bmsFiles.Where((BMSFile file) => file != null && !string.IsNullOrWhiteSpace(file.path))
            .GroupBy((BMSFile file) => file.path, StringComparer.OrdinalIgnoreCase)
            .Select((IGrouping<string, BMSFile> group) => group.First())
            .ToList();
        List<BMSFile> changedFiles = targetFiles.Where((BMSFile file) => !file.notes.HasValue && File.Exists(file.path)).AsParallel().Where(delegate (BMSFile file)
        {
            try
            {
                return file.SetNotesIfZeroNote();
            }
            catch (Exception ex)
            {
                if (ex is DirectoryNotFoundException || ex is FileNotFoundException || ex is IOException || ex is PathTooLongException || ex is SecurityException || ex is UnauthorizedAccessException)
                {
                    dialogService?.Show(string.Format(Resources.Error_BmsLoadFailedSkip, file.path, ex.Message), Resources.MessageBoxTitle_Error, MessageBoxButton.OK, MessageBoxImage.Hand, MessageBoxResult.OK);
                    return false;
                }
                throw;
            }
        }).ToList();
        if (changedFiles.Count > 0)
        {
            dbGateway.UpsertSongs(changedFiles);
            result.HasUpdates = true;
            result.SongUpsertCount = changedFiles.Count;
            result.ZeroNoteChangedCount = changedFiles.Count;
        }
        stopwatch.Stop();
        result.TotalMs = stopwatch.ElapsedMilliseconds;
        return result;
    }

    private static bool AppendHealthWarning(BMSFile bmsFile, int? health, int? defined, int? existing, string warningFormat)
    {
        if (!health.HasValue || health.Value >= 100)
        {
            return false;
        }
        AppendWarningLine(bmsFile, string.Format(warningFormat, health, defined - existing, defined));
        return true;
    }

    private static bool AppendFlagWarning(BMSFile bmsFile, bool? isHealthy, string warningText)
    {
        if (isHealthy != false)
        {
            return false;
        }
        AppendWarningLine(bmsFile, warningText);
        return true;
    }

    private static void AppendWarningLine(BMSFile bmsFile, string warningText)
    {
        if (!string.IsNullOrWhiteSpace(bmsFile.warning))
        {
            bmsFile.warning += Environment.NewLine;
        }
        bmsFile.warning += warningText;
    }
}
