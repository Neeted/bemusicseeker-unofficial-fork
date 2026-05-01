using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Security;
using System.Threading;
using BeMusicSeeker.Properties;
using System.Windows;
using BeMusicSeeker.Models.LR2;
using Ribbit.Util.Extensions;

namespace BeMusicSeeker.Models.BmsLibraryInternal;

/// <summary>
/// Computes and persists maintenance state for snapshots owned by BMSLibrary.
/// The facade must acquire the required locks before invoking this service.
/// </summary>
internal sealed class BmsLibraryMaintenanceService
{
    private static IEnumerable<BMSFile> EnumerateBmsChartFiles(IEnumerable<BMSFile> bmsFiles)
    {
        return (bmsFiles ?? Enumerable.Empty<BMSFile>())
            .Where((BMSFile file) => file != null && PendingChartEntry.IsBmsChartFile(file));
    }

    private static IEnumerable<BMSFile> EnumerateResourceHealthChartFiles(IEnumerable<BMSFile> bmsFiles)
    {
        return (bmsFiles ?? Enumerable.Empty<BMSFile>())
            .Where((BMSFile file) => file != null && (PendingChartEntry.IsBmsChartFile(file) || PendingChartEntry.IsBmsonChartFile(file)));
    }

    public bool ApplyNeedToBeFixedWarnings(BMSFile bmsFile, BMSFileMaintenanceInfo maintenanceInfo = null, bool strictCheck = false)
    {
        if (bmsFile == null || (!PendingChartEntry.IsBmsChartFile(bmsFile) && !PendingChartEntry.IsBmsonChartFile(bmsFile)))
        {
            return false;
        }
        _ = strictCheck;
        bool hasAnyWarning = false;
        maintenanceInfo ??= bmsFile.maintenanceInfo;
        bmsFile.ClearWarningsByCategory(ChartWarningCategory.ResourceHealth);
        hasAnyWarning |= AppendHealthWarning(bmsFile, maintenanceInfo.GetWAVHealth(), maintenanceInfo.wav_files_defined, maintenanceInfo.wav_files_existing, ChartWarningKind.ResourceWavMissing, Resources.Warning_WavFilesNotFound);
        hasAnyWarning |= AppendHealthWarning(bmsFile, maintenanceInfo.GetBGAHealth(), maintenanceInfo.bga_files_defined, maintenanceInfo.bga_files_existing, ChartWarningKind.ResourceBgaMissing, Resources.Warning_BgaFilesNotFound);
        hasAnyWarning |= AppendHealthWarning(bmsFile, maintenanceInfo.GetMovieHealth(), maintenanceInfo.movie_files_defined, maintenanceInfo.movie_files_existing, ChartWarningKind.ResourceMovieMissing, Resources.Warning_MovieFilesNotFound);
        hasAnyWarning |= AppendFlagWarning(bmsFile, maintenanceInfo.GetStagefileHealth(), ChartWarningKind.ResourceStagefileMissing, Resources.Warning_StagefileNotFound);
        hasAnyWarning |= AppendFlagWarning(bmsFile, maintenanceInfo.GetBackbmpHealth(), ChartWarningKind.ResourceBackbmpMissing, Resources.Warning_BackbmpNotFound);
        hasAnyWarning |= AppendFlagWarning(bmsFile, maintenanceInfo.GetBannerHealth(), ChartWarningKind.ResourceBannerMissing, Resources.Warning_BannerNotFound);
        return hasAnyWarning;
    }

    public List<BMSFile> GetGarbledFiles(IEnumerable<BMSFile> bmsFiles, bool isInFixedList)
    {
        return EnumerateBmsChartFiles(bmsFiles)
            .Where((BMSFile file) => !string.IsNullOrWhiteSpace(file?.maintenanceInfo?.encoding)
                && isInFixedList == file.maintenanceInfo.is_encoding_fixed
                && !file.maintenanceInfo.encoding.StartsWith("shift_jis"))
            .ToList();
    }

    public List<BMSFile> GetZeroNoteFiles(IEnumerable<BMSFile> bmsFiles)
    {
        return EnumerateBmsChartFiles(bmsFiles).Where((BMSFile file) => file.notes == 0).ToList();
    }

    public int CleanupMaintenanceTable(IEnumerable<BMSFile> bmsFiles, BmsLibraryDbGateway dbGateway)
    {
        List<string> currentPaths = EnumerateResourceHealthChartFiles(bmsFiles)
            .Where((BMSFile file) => !string.IsNullOrWhiteSpace(file.path))
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
        List<BMSFileMaintenanceInfo> changes = (from f in EnumerateResourceHealthChartFiles(bmsFiles)
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
        List<BMSFile> files = EnumerateBmsChartFiles(bmsFiles).ToList();
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
            string originalEncoding = info.encoding;
            if (!string.IsNullOrWhiteSpace(encoding))
            {
                if (reloadedFiles.Contains(file) || !string.Equals(info.encoding, encoding, StringComparison.Ordinal))
                {
                    info.encoding = encoding;
                    info.is_encoding_fixed = true;
                    file.NotifyMaintenanceInfoChanged(
                        encodingChanged: !string.Equals(originalEncoding, info.encoding, StringComparison.Ordinal),
                        healthChanged: false);
                    return info;
                }
                return null;
            }
            if ((info.encoding.EndsWith("?") && info.encoding != "shift_jis?") || info.encoding == "unknown")
            {
                info.encoding = "shift_jis";
                info.is_encoding_fixed = true;
                file.NotifyMaintenanceInfoChanged(
                    encodingChanged: !string.Equals(originalEncoding, info.encoding, StringComparison.Ordinal),
                    healthChanged: false);
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
        List<BMSFile> files = EnumerateBmsChartFiles(allFiles).ToList();
        List<BMSFile> zeroNoteFiles = files.Where((BMSFile f) => f.notes == 0 && !string.IsNullOrWhiteSpace(f.path)).ToList();
        List<BMSFile> staleMismatchFiles = files.Where((BMSFile f) => f.notes != 0 && f.Warnings.Contains(ChartWarningKind.ZeroNoteMismatch)).ToList();
        ZeroNoteRecheckResult result = new ZeroNoteRecheckResult
        {
            Total = zeroNoteFiles.Count
        };
        foreach (BMSFile staleMismatchFile in staleMismatchFiles)
        {
            if (ClearZeroNoteMismatchWarning(staleMismatchFile))
            {
                result.ClearedCount++;
                result.ChangedCount++;
            }
        }
        foreach (BMSFile zeroNoteFile in zeroNoteFiles)
        {
            try
            {
                bool isZeroNoteByFile = BMSFile.IsZeroNoteBMSFile(zeroNoteFile.path);
                if (!isZeroNoteByFile)
                {
                    if (SetZeroNoteMismatchWarning(zeroNoteFile))
                    {
                        result.ChangedCount++;
                    }
                    result.MismatchCount++;
                }
                else
                {
                    if (ClearZeroNoteMismatchWarning(zeroNoteFile))
                    {
                        result.ClearedCount++;
                        result.ChangedCount++;
                    }
                }
            }
            catch (Exception ex) when (ex is DirectoryNotFoundException || ex is FileNotFoundException || ex is IOException || ex is PathTooLongException || ex is System.Security.SecurityException || ex is UnauthorizedAccessException)
            {
                if (ClearZeroNoteMismatchWarning(zeroNoteFile))
                {
                    result.ClearedCount++;
                    result.ChangedCount++;
                }
                result.SkippedCount++;
                logWarn?.Invoke(ex, "zero_note_recheck skipped: path=" + zeroNoteFile.path);
            }
        }
        return result;
    }

    private static bool SetZeroNoteMismatchWarning(BMSFile file)
    {
        bool changed = file != null && !file.Warnings.Contains(ChartWarningKind.ZeroNoteMismatch);
        file?.SetWarning(ChartWarningKind.ZeroNoteMismatch, Resources.Warning_ZeroNoteMismatch);
        return changed;
    }

    private static bool ClearZeroNoteMismatchWarning(BMSFile file)
    {
        bool changed = file != null && file.Warnings.Contains(ChartWarningKind.ZeroNoteMismatch);
        file?.ClearWarning(ChartWarningKind.ZeroNoteMismatch);
        return changed;
    }

    public List<BMSFile> DetectModeChanges(IEnumerable<BMSFile> bmsFiles, bool forceUpdate)
    {
        List<BMSFile> targets = EnumerateBmsChartFiles(bmsFiles)
            .Where((BMSFile file) => (forceUpdate || !file.mode.HasValue) && File.Exists(file.path))
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
            ? EnumerateResourceHealthChartFiles(bmsFiles).ToList()
            : EnumerateResourceHealthChartFiles(bmsFiles).Where((BMSFile file) => !file.maintenanceInfo.IsInformationChecked() || (PendingChartEntry.IsBmsChartFile(file) && string.IsNullOrWhiteSpace(file.maintenanceInfo.encoding))).ToList());
        result.CheckedFileCount = targets.Count;
        result.BmsResourceTargetCount = targets.Count(PendingChartEntry.IsBmsChartFile);
        result.BmsonResourceTargetCount = targets.Count(PendingChartEntry.IsBmsonChartFile);
        foreach (IEnumerable<BMSFile> section in targets.Section(1000))
        {
            object reloadedLock = new object();
            List<BMSFile> reloadedFiles = new List<BMSFile>();
            object bmsonReparseLock = new object();
            int bmsonReparsedInSection = 0;
            int bmsonReparseFailedInSection = 0;
            List<BMSFile> filesInSection = section.Where((BMSFile file) => file != null).ToList();
            filesInSection.AsParallel().ForAll(delegate (BMSFile file)
            {
                bool isBmson = PendingChartEntry.IsBmsonChartFile(file);
                string originalHash = file.hash;
                if (isBmson)
                {
                    file.maintenanceInfo.NormalizeForBmson(file.path, file.hash);
                    if (forceUpdate || !file.maintenanceInfo.IsInformationChecked())
                    {
                        BmsonResourceRefreshResult refreshResult = TryRefreshBmsonResourceReferences(file);
                        if (refreshResult == BmsonResourceRefreshResult.Success)
                        {
                            lock (bmsonReparseLock)
                            {
                                bmsonReparsedInSection++;
                            }
                        }
                        else if (refreshResult == BmsonResourceRefreshResult.Failed)
                        {
                            lock (bmsonReparseLock)
                            {
                                bmsonReparseFailedInSection++;
                            }
                            return;
                        }
                    }
                }
                MaintenanceSnapshot beforeSnapshot = MaintenanceSnapshot.FromFile(file);
                int retryCount = 0;
                while (true)
                {
                    try
                    {
                        file.SetHealthStatus(folderAllFileList, forceUpdate, memClear: !isBmson);
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
                if (isBmson)
                {
                    file.maintenanceInfo.NormalizeForBmson(file.path, file.hash);
                }
                else if (forceUpdate || string.IsNullOrWhiteSpace(file.maintenanceInfo.encoding))
                {
                    file.SetEncosingInfo();
                }
                if (!isBmson && !string.IsNullOrWhiteSpace(file.maintenanceInfo.encoding) && !file.maintenanceInfo.encoding.StartsWith("shift_jis") && !file.maintenanceInfo.encoding.EndsWith("?") && file.maintenanceInfo.encoding != "unknown")
                {
                    BMSFile.ReloadBMSFileWithEncoding(file, file.maintenanceInfo.encoding);
                    file.maintenanceInfo.is_encoding_fixed = true;
                    lock (reloadedLock)
                    {
                        reloadedFiles.Add(file);
                    }
                }
                else if (!isBmson && originalHash != file.hash)
                {
                    lock (reloadedLock)
                    {
                        reloadedFiles.Add(file);
                    }
                }
                MaintenanceSnapshot afterSnapshot = MaintenanceSnapshot.FromFile(file);
                bool encodingChanged = !string.Equals(beforeSnapshot.Encoding, afterSnapshot.Encoding, StringComparison.Ordinal);
                bool healthChanged = beforeSnapshot.WAVHealth != afterSnapshot.WAVHealth
                    || beforeSnapshot.BGAHealth != afterSnapshot.BGAHealth
                    || beforeSnapshot.MovieHealth != afterSnapshot.MovieHealth
                    || beforeSnapshot.StagefileHealth != afterSnapshot.StagefileHealth
                    || beforeSnapshot.BannerHealth != afterSnapshot.BannerHealth
                    || beforeSnapshot.BackbmpHealth != afterSnapshot.BackbmpHealth;
                if (encodingChanged || healthChanged)
                {
                    file.NotifyMaintenanceInfoChanged(encodingChanged, healthChanged);
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
            result.BmsonReparsedCount += bmsonReparsedInSection;
            result.BmsonReparseFailedCount += bmsonReparseFailedInSection;
        }
        stopwatch.Stop();
        result.TotalMs = stopwatch.ElapsedMilliseconds;
        return result;
    }

    private static BmsonResourceRefreshResult TryRefreshBmsonResourceReferences(BMSFile file)
    {
        if (file is not PendingChartEntry pending || !pending.IsBmsonChart || string.IsNullOrWhiteSpace(pending.path) || !File.Exists(pending.path))
        {
            return BmsonResourceRefreshResult.NotApplicable;
        }
        try
        {
            LR2SongDBExtended.bmson_song parsed = BmsonSongParser.Parse(pending.path);
            pending.UpdateBmsonResourceReferences(parsed);
            return BmsonResourceRefreshResult.Success;
        }
        catch
        {
            return BmsonResourceRefreshResult.Failed;
        }
    }

    private enum BmsonResourceRefreshResult
    {
        NotApplicable,
        Success,
        Failed
    }

    private readonly struct MaintenanceSnapshot
    {
        public string Encoding { get; }

        public int? WAVHealth { get; }

        public int? BGAHealth { get; }

        public int? MovieHealth { get; }

        public bool? StagefileHealth { get; }

        public bool? BannerHealth { get; }

        public bool? BackbmpHealth { get; }

        private MaintenanceSnapshot(BMSFileMaintenanceInfo info)
        {
            Encoding = info?.encoding;
            WAVHealth = info?.WAVHealth;
            BGAHealth = info?.BGAHealth;
            MovieHealth = info?.MovieHealth;
            StagefileHealth = info?.StagefileHealth;
            BannerHealth = info?.BannerHealth;
            BackbmpHealth = info?.BackbmpHealth;
        }

        public static MaintenanceSnapshot FromFile(BMSFile file)
        {
            return new MaintenanceSnapshot(file?.maintenanceInfo);
        }
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
        List<BMSFile> targetFiles = EnumerateBmsChartFiles(bmsFiles).Where((BMSFile file) => !string.IsNullOrWhiteSpace(file.path))
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

    private static bool AppendHealthWarning(BMSFile bmsFile, int? health, int? defined, int? existing, ChartWarningKind kind, string warningFormat)
    {
        if (!health.HasValue || health.Value >= 100)
        {
            return false;
        }
        bmsFile.SetWarning(kind, string.Format(warningFormat, health, defined - existing, defined));
        return true;
    }

    private static bool AppendFlagWarning(BMSFile bmsFile, bool? isHealthy, ChartWarningKind kind, string warningText)
    {
        if (isHealthy != false)
        {
            return false;
        }
        bmsFile.SetWarning(kind, warningText);
        return true;
    }
}
