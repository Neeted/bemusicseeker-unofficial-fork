using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Security;
using System.Threading;
using System.Threading.Tasks;
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
    private readonly int maintenanceHealthDegree;

    public BmsLibraryMaintenanceService()
        : this(null)
    {
    }

    internal BmsLibraryMaintenanceService(int? maintenanceHealthDegreeOverride)
    {
        maintenanceHealthDegree = maintenanceHealthDegreeOverride.HasValue
            ? Math.Max(1, maintenanceHealthDegreeOverride.Value)
            : ResolveDefaultMaintenanceHealthDegree();
    }

    internal static int ResolveDefaultMaintenanceHealthDegree()
    {
        return Math.Max(1, Environment.ProcessorCount - 1);
    }

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
        IReadOnlyList<ChartWarning> warnings = BuildResourceHealthWarnings(bmsFile, maintenanceInfo, strictCheck);
        bmsFile?.ReplaceWarningsByCategory(ChartWarningCategory.ResourceHealth, warnings);
        return warnings.Count > 0;
    }

    public IReadOnlyList<ChartWarning> BuildResourceHealthWarnings(BMSFile bmsFile, BMSFileMaintenanceInfo maintenanceInfo = null, bool strictCheck = false)
    {
        if (bmsFile == null || (!PendingChartEntry.IsBmsChartFile(bmsFile) && !PendingChartEntry.IsBmsonChartFile(bmsFile)))
        {
            return Array.Empty<ChartWarning>();
        }
        _ = strictCheck;
        maintenanceInfo ??= bmsFile.HasValidMaintenanceInfoSnapshot ? bmsFile.TryGetMaintenanceInfoWithoutCreating() : null;
        if (maintenanceInfo == null)
        {
            return Array.Empty<ChartWarning>();
        }
        List<ChartWarning> warnings = new List<ChartWarning>();
        AppendHealthWarning(warnings, maintenanceInfo.GetWAVHealth(), maintenanceInfo.wav_files_defined, maintenanceInfo.wav_files_existing, ChartWarningKind.ResourceWavMissing, Resources.Warning_WavFilesNotFound);
        AppendHealthWarning(warnings, maintenanceInfo.GetBGAHealth(), maintenanceInfo.bga_files_defined, maintenanceInfo.bga_files_existing, ChartWarningKind.ResourceBgaMissing, Resources.Warning_BgaFilesNotFound);
        AppendHealthWarning(warnings, maintenanceInfo.GetMovieHealth(), maintenanceInfo.movie_files_defined, maintenanceInfo.movie_files_existing, ChartWarningKind.ResourceMovieMissing, Resources.Warning_MovieFilesNotFound);
        AppendFlagWarning(warnings, maintenanceInfo.GetStagefileHealth(), ChartWarningKind.ResourceStagefileMissing, Resources.Warning_StagefileNotFound);
        AppendFlagWarning(warnings, maintenanceInfo.GetBackbmpHealth(), ChartWarningKind.ResourceBackbmpMissing, Resources.Warning_BackbmpNotFound);
        AppendFlagWarning(warnings, maintenanceInfo.GetBannerHealth(), ChartWarningKind.ResourceBannerMissing, Resources.Warning_BannerNotFound);
        return warnings;
    }

    public List<BMSFile> GetGarbledFiles(IEnumerable<BMSFile> bmsFiles, bool isInFixedList)
    {
        return EnumerateBmsChartFiles(bmsFiles)
            .Where((BMSFile file) =>
            {
                BMSFileMaintenanceInfo info = file?.HasValidMaintenanceInfoSnapshot == true ? file.TryGetMaintenanceInfoWithoutCreating() : null;
                return !string.IsNullOrWhiteSpace(info?.encoding)
                    && isInFixedList == info.is_encoding_fixed
                    && !info.encoding.StartsWith("shift_jis");
            })
            .ToList();
    }

    public List<BMSFile> GetZeroNoteFiles(IEnumerable<BMSFile> bmsFiles)
    {
        return EnumerateBmsChartFiles(bmsFiles).Where((BMSFile file) => file.ChartInfo?.notes == 0).ToList();
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
        List<BMSFile> zeroNoteFiles = files.Where((BMSFile f) => f.ChartInfo?.notes == 0 && !string.IsNullOrWhiteSpace(f.path)).ToList();
        List<BMSFile> staleMismatchFiles = files.Where((BMSFile f) => f.ChartInfo?.notes != 0 && f.Warnings.Contains(ChartWarningKind.ZeroNoteMismatch)).ToList();
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
        IBmsLibraryDialogService dialogService,
        ResourceHealthLookupContext resourceLookupContext = null,
        Action<string> progressLogger = null,
        Action<MaintenanceWorkflowProgress> progressReporter = null,
        CancellationToken cancellationToken = default)
    {
        MaintenanceWorkflowResult result = new MaintenanceWorkflowResult();
        if (bmsFiles == null || dbGateway == null)
        {
            return result;
        }
        Stopwatch stopwatch = Stopwatch.StartNew();
        List<BMSFile> sourceFiles = EnumerateResourceHealthChartFiles(bmsFiles).ToList();
        List<BMSFile> targets = new List<BMSFile>();
        foreach (BMSFile file in sourceFiles)
        {
            bool isBms = PendingChartEntry.IsBmsChartFile(file);
            bool isBmson = PendingChartEntry.IsBmsonChartFile(file);
            bool missingInfo = file?.maintenanceInfo?.IsInformationChecked() != true;
            bool missingEncoding = isBms && string.IsNullOrWhiteSpace(file?.maintenanceInfo?.encoding);
            bool isTarget = forceUpdate || missingInfo || missingEncoding;
            if (!isTarget)
            {
                continue;
            }
            targets.Add(file);
            if (forceUpdate)
            {
                result.ForceTargetCount++;
            }
            if (missingInfo)
            {
                result.MissingInfoTargetCount++;
            }
            if (missingEncoding)
            {
                result.MissingEncodingTargetCount++;
            }
            if (isBmson && !HasFreshCurrentBmsonResourceReferences(file as PendingChartEntry))
            {
                result.BmsonMissingFreshResourceReferenceCount++;
            }
        }
        result.CheckedFileCount = targets.Count;
        result.BmsResourceTargetCount = targets.Count(PendingChartEntry.IsBmsChartFile);
        result.BmsonResourceTargetCount = targets.Count(PendingChartEntry.IsBmsonChartFile);
        result.HealthTargetCount = targets.Count;
        result.HealthDegree = maintenanceHealthDegree;
        resourceLookupContext ??= new ResourceHealthLookupContext(folderAllFileList, null, null);
        progressLogger?.Invoke("maintenance_target_summary total=" + targets.Count
            + " sourceCount=" + sourceFiles.Count
            + " force=" + result.ForceTargetCount
            + " missingInfo=" + result.MissingInfoTargetCount
            + " missingEncoding=" + result.MissingEncodingTargetCount
            + " bmsonMissingFreshRefs=" + result.BmsonMissingFreshResourceReferenceCount
            + " healthDegree=" + maintenanceHealthDegree);
        if (targets.Count <= 0)
        {
            stopwatch.Stop();
            result.HealthCacheHitCount = resourceLookupContext.CacheHitCount;
            result.HealthFileExistsFallbackCount = resourceLookupContext.FileExistsFallbackCount;
            result.HealthAudioFileExistsFallbackCount = resourceLookupContext.AudioFileExistsFallbackCount;
            result.HealthImageFileExistsFallbackCount = resourceLookupContext.ImageFileExistsFallbackCount;
            result.HealthMovieFileExistsFallbackCount = resourceLookupContext.MovieFileExistsFallbackCount;
            result.HealthOptionalImageFileExistsFallbackCount = resourceLookupContext.OptionalImageFileExistsFallbackCount;
            result.TotalMs = stopwatch.ElapsedMilliseconds;
            progressLogger?.Invoke("maintenance_update no_targets sourceCount=" + sourceFiles.Count
                + " healthDegree=" + maintenanceHealthDegree
                + " elapsedMs=" + result.TotalMs);
            progressReporter?.Invoke(new MaintenanceWorkflowProgress
            {
                TotalCount = sourceFiles.Count,
                ProcessedCount = sourceFiles.Count,
                IsCompleted = true
            });
            return result;
        }
        long healthTicks = 0L;
        long encodingTicks = 0L;
        long bmsonRefreshTicks = 0L;
        int sectionIndex = 0;
        int processedCount = 0;
        progressReporter?.Invoke(new MaintenanceWorkflowProgress
        {
            TotalCount = targets.Count,
            ProcessedCount = 0,
            CurrentPath = targets.FirstOrDefault()?.path ?? string.Empty
        });
        foreach (IEnumerable<BMSFile> section in targets.Section(1000))
        {
            if (cancellationToken.IsCancellationRequested)
            {
                result.Canceled = true;
                break;
            }
            sectionIndex++;
            Stopwatch sectionStopwatch = Stopwatch.StartNew();
            object reloadedLock = new object();
            List<BMSFile> reloadedFiles = new List<BMSFile>();
            object bmsonReparseLock = new object();
            int bmsonReparsedInSection = 0;
            int bmsonReparseFailedInSection = 0;
            int bmsonResourceReferencesReusedInSection = 0;
            List<BMSFile> filesInSection = section.Where((BMSFile file) => file != null).ToList();
            progressReporter?.Invoke(new MaintenanceWorkflowProgress
            {
                TotalCount = targets.Count,
                ProcessedCount = processedCount,
                CurrentPath = filesInSection.FirstOrDefault()?.path ?? string.Empty
            });
            progressLogger?.Invoke("maintenance_update section_start section=" + sectionIndex
                + " targetCount=" + filesInSection.Count
                + " total=" + targets.Count
                + " healthDegree=" + maintenanceHealthDegree);
            try
            {
                Parallel.ForEach(filesInSection, new ParallelOptions { MaxDegreeOfParallelism = maintenanceHealthDegree }, delegate (BMSFile file)
                {
                    bool isBmson = PendingChartEntry.IsBmsonChartFile(file);
                    string originalHash = file.hash;
                    if (isBmson)
                    {
                        file.maintenanceInfo.NormalizeForBmson(file.path, file.hash);
                        if (forceUpdate || !file.maintenanceInfo.IsInformationChecked())
                        {
                            long bmsonRefreshStart = Stopwatch.GetTimestamp();
                            BmsonResourceRefreshResult refreshResult = TryRefreshBmsonResourceReferences(file, forceUpdate);
                            AddElapsedTicks(ref bmsonRefreshTicks, bmsonRefreshStart);
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
                            else if (refreshResult == BmsonResourceRefreshResult.Reused)
                            {
                                lock (bmsonReparseLock)
                                {
                                    bmsonResourceReferencesReusedInSection++;
                                }
                            }
                        }
                    }
                    MaintenanceSnapshot beforeSnapshot = MaintenanceSnapshot.FromFile(file);
                    int retryCount = 0;
                    while (true)
                    {
                        try
                        {
                            long healthStart = Stopwatch.GetTimestamp();
                            if (!forceUpdate)
                            {
                                file.SetHealthStatusUsingLookupContext(resourceLookupContext, forceUpdate, memClear: !isBmson);
                            }
                            else
                            {
                                file.SetHealthStatus(folderAllFileList, forceUpdate, memClear: !isBmson);
                            }
                            AddElapsedTicks(ref healthTicks, healthStart);
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
                        long encodingStart = Stopwatch.GetTimestamp();
                        file.SetEncosingInfo();
                        AddElapsedTicks(ref encodingTicks, encodingStart);
                    }
                    if (!isBmson && !string.IsNullOrWhiteSpace(file.maintenanceInfo.encoding) && !file.maintenanceInfo.encoding.StartsWith("shift_jis") && !file.maintenanceInfo.encoding.EndsWith("?") && file.maintenanceInfo.encoding != "unknown")
                    {
                        long encodingStart = Stopwatch.GetTimestamp();
                        BMSFile.ReloadBMSFileWithEncoding(file, file.maintenanceInfo.encoding);
                        file.maintenanceInfo.is_encoding_fixed = true;
                        AddElapsedTicks(ref encodingTicks, encodingStart);
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
                result.BmsonResourceReferenceReusedCount += bmsonResourceReferencesReusedInSection;
                processedCount += filesInSection.Count;
                sectionStopwatch.Stop();
                progressLogger?.Invoke("maintenance_update section_done section=" + sectionIndex
                    + " targetCount=" + filesInSection.Count
                    + " maintenanceUpserted=" + maintenanceInfos.Count
                    + " songReloaded=" + reloadedFiles.Count
                    + " bmsonReparsed=" + bmsonReparsedInSection
                    + " bmsonReparseFailed=" + bmsonReparseFailedInSection
                    + " bmsonResourceRefsReused=" + bmsonResourceReferencesReusedInSection
                    + " elapsedMs=" + sectionStopwatch.ElapsedMilliseconds);
                progressReporter?.Invoke(new MaintenanceWorkflowProgress
                {
                    TotalCount = targets.Count,
                    ProcessedCount = processedCount,
                    CurrentPath = filesInSection.LastOrDefault()?.path ?? string.Empty,
                    IsCanceled = result.Canceled
                });
            }
            catch (Exception ex)
            {
                sectionStopwatch.Stop();
                progressLogger?.Invoke("maintenance_update section_failed section=" + sectionIndex
                    + " targetCount=" + filesInSection.Count
                    + " elapsedMs=" + sectionStopwatch.ElapsedMilliseconds
                    + " message=" + SanitizeLogValue(ex.Message));
                throw;
            }
        }
        stopwatch.Stop();
        result.HealthMs = TicksToMilliseconds(Interlocked.Read(ref healthTicks));
        result.EncodingMs = TicksToMilliseconds(Interlocked.Read(ref encodingTicks));
        result.BmsonRefreshMs = TicksToMilliseconds(Interlocked.Read(ref bmsonRefreshTicks));
        result.HealthCacheHitCount = resourceLookupContext.CacheHitCount;
        result.HealthFileExistsFallbackCount = resourceLookupContext.FileExistsFallbackCount;
        result.HealthAudioFileExistsFallbackCount = resourceLookupContext.AudioFileExistsFallbackCount;
        result.HealthImageFileExistsFallbackCount = resourceLookupContext.ImageFileExistsFallbackCount;
        result.HealthMovieFileExistsFallbackCount = resourceLookupContext.MovieFileExistsFallbackCount;
        result.HealthOptionalImageFileExistsFallbackCount = resourceLookupContext.OptionalImageFileExistsFallbackCount;
        result.TotalMs = stopwatch.ElapsedMilliseconds;
        progressReporter?.Invoke(new MaintenanceWorkflowProgress
        {
            TotalCount = targets.Count,
            ProcessedCount = processedCount,
            CurrentPath = string.Empty,
            IsCompleted = !result.Canceled,
            IsCanceled = result.Canceled
        });
        return result;
    }

    private static void AddElapsedTicks(ref long targetTicks, long startTimestamp)
    {
        long elapsedTicks = Stopwatch.GetTimestamp() - startTimestamp;
        Interlocked.Add(ref targetTicks, elapsedTicks);
    }

    private static string SanitizeLogValue(string value)
    {
        return (value ?? string.Empty).Replace('\r', ' ').Replace('\n', ' ');
    }

    private static long TicksToMilliseconds(long stopwatchTicks)
    {
        return stopwatchTicks <= 0L ? 0L : (long)(stopwatchTicks * 1000.0 / Stopwatch.Frequency);
    }

    private static BmsonResourceRefreshResult TryRefreshBmsonResourceReferences(BMSFile file, bool forceUpdate)
    {
        if (file is not PendingChartEntry pending || !pending.IsBmsonChart || string.IsNullOrWhiteSpace(pending.path) || !File.Exists(pending.path))
        {
            return BmsonResourceRefreshResult.NotApplicable;
        }
        if (!forceUpdate && HasFreshCurrentBmsonResourceReferences(pending))
        {
            return BmsonResourceRefreshResult.Reused;
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

    private static bool HasFreshCurrentBmsonResourceReferences(PendingChartEntry pending)
    {
        LR2SongDBExtended.bmson_song song = pending?.BmsonSong;
        if (song == null || !song.HasFreshResourceReferences || string.IsNullOrWhiteSpace(song.path))
        {
            return false;
        }
        try
        {
            return File.Exists(song.path) && File.GetLastWriteTimeUtc(song.path) == song.updated_at;
        }
        catch
        {
            return false;
        }
    }

    private enum BmsonResourceRefreshResult
    {
        NotApplicable,
        Reused,
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

    private static void AppendHealthWarning(List<ChartWarning> warnings, int? health, int? defined, int? existing, ChartWarningKind kind, string warningFormat)
    {
        if (!health.HasValue || health.Value >= 100)
        {
            return;
        }
        warnings.Add(ChartWarning.Create(kind, string.Format(warningFormat, health, defined - existing, defined)));
    }

    private static void AppendFlagWarning(List<ChartWarning> warnings, bool? isHealthy, ChartWarningKind kind, string warningText)
    {
        if (isHealthy != false)
        {
            return;
        }
        warnings.Add(ChartWarning.Create(kind, warningText));
    }
}
