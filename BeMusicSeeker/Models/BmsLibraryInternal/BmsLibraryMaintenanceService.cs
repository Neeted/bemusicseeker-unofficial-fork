using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Security;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using BeMusicSeeker.Models.LR2;
using BeMusicSeeker.Properties;
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
        return (bmsFiles ?? [])
            .Where(file => file != null && ChartFileKindResolver.IsBmsChartFile(file));
    }

    public IReadOnlyList<ChartWarning> BuildResourceHealthWarnings(ChartFile chart)
    {
        if (chart == null || (chart.Kind != ChartFileKind.Bms && chart.Kind != ChartFileKind.Bmson))
        {
            return [];
        }
        return BuildResourceHealthWarnings(GetResourceHealthMaintenanceInfo(chart));
    }

    internal static BMSFileMaintenanceInfo GetResourceHealthMaintenanceInfo(ChartFile chart)
    {
        BMSFile bmsFile = chart?.GetBmsStorageOwner();
        if (bmsFile != null)
        {
            return bmsFile.HasValidMaintenanceInfoSnapshot
                ? bmsFile.TryGetMaintenanceInfoWithoutCreating()
                : null;
        }
        LR2SongDBExtended.bmson_song song = chart?.GetBmsonStorageOwner();
        if (song != null)
        {
            BMSFileMaintenanceInfo maintenanceInfo = song.MaintenanceInfo;
            if (IsCurrentBmsonMaintenanceInfo(song, maintenanceInfo) && HasResourceHealthSnapshot(maintenanceInfo))
            {
                maintenanceInfo.NormalizeForBmson(song.path, song.md5);
                return maintenanceInfo;
            }
            BMSFileMaintenanceInfo computedInfo = BuildResourceHealthMaintenanceInfo(chart);
            if (computedInfo != null && IsCurrentBmsonMaintenanceInfo(song, maintenanceInfo))
            {
                computedInfo.is_files_warning_ignored = maintenanceInfo.is_files_warning_ignored;
            }
            return computedInfo;
        }
        return BuildResourceHealthMaintenanceInfo(chart);
    }

    private static bool IsCurrentBmsonMaintenanceInfo(LR2SongDBExtended.bmson_song song, BMSFileMaintenanceInfo maintenanceInfo)
    {
        return song != null
            && maintenanceInfo != null
            && string.Equals(maintenanceInfo.hash, song.md5, StringComparison.OrdinalIgnoreCase);
    }

    private static bool HasResourceHealthSnapshot(BMSFileMaintenanceInfo maintenanceInfo)
    {
        return maintenanceInfo != null
            && (maintenanceInfo.wav_files_defined.HasValue
                || maintenanceInfo.bga_files_defined.HasValue
                || maintenanceInfo.movie_files_defined.HasValue
                || maintenanceInfo.is_stagefile_defined.HasValue
                || maintenanceInfo.is_backbmp_defined.HasValue
                || maintenanceInfo.is_banner_defined.HasValue);
    }

    internal static BMSFileMaintenanceInfo BuildResourceHealthMaintenanceInfo(ChartFile chart)
    {
        return BuildResourceHealthMaintenanceInfo(chart, null, forceUpdate: false);
    }

    internal static BMSFileMaintenanceInfo BuildResourceHealthMaintenanceInfo(
        ChartFile chart,
        ResourceHealthLookupContext lookupContext,
        bool forceUpdate = false)
    {
        if (chart == null || string.IsNullOrWhiteSpace(chart.Path))
        {
            return null;
        }
        string chartDirectory = Path.GetDirectoryName(chart.Path);
        if (string.IsNullOrWhiteSpace(chartDirectory))
        {
            return null;
        }

        ChartResourceSnapshot resources = ChartResourceSnapshot.Create(chart);
        var maintenanceInfo = new BMSFileMaintenanceInfo
        {
            path = chart.Path,
            hash = chart.Md5,
            encoding = chart.Kind == ChartFileKind.Bmson ? "utf-8" : null,
            is_encoding_fixed = false
        };

        maintenanceInfo.wav_files_defined = resources.AudioReferenceCount;
        if (maintenanceInfo.wav_files_defined > 0)
        {
            maintenanceInfo.wav_files_existing = CountExistingResourceReferences(
                chartDirectory,
                resources.AudioReferences,
                ChartResourceExtensions.AudioExtensions,
                ChartResourceKind.Audio,
                lookupContext);
        }
        maintenanceInfo.bga_files_defined = resources.VisualReferenceCount;
        if (maintenanceInfo.bga_files_defined > 0)
        {
            maintenanceInfo.bga_files_existing = CountExistingResourceReferences(
                chartDirectory,
                resources.VisualReferences,
                ChartResourceExtensions.ImageExtensions,
                ChartResourceKind.Image,
                lookupContext);
        }
        maintenanceInfo.movie_files_defined = resources.MovieReferenceCount;
        if (maintenanceInfo.movie_files_defined > 0)
        {
            maintenanceInfo.movie_files_existing = CountExistingResourceReferences(
                chartDirectory,
                resources.MovieReferences,
                ChartResourceExtensions.MovieExtensions,
                ChartResourceKind.Movie,
                lookupContext);
        }

        string stagefile = chart.Stagefile;
        string backbmp = chart.Backbmp;
        string banner = chart.Banner;
        maintenanceInfo.is_stagefile_defined = !string.IsNullOrWhiteSpace(stagefile);
        if (maintenanceInfo.is_stagefile_defined == true)
        {
            maintenanceInfo.is_stagefile_existing = ExistsOptionalImageResource(chartDirectory, stagefile, lookupContext);
        }
        maintenanceInfo.is_backbmp_defined = !string.IsNullOrWhiteSpace(backbmp);
        if (maintenanceInfo.is_backbmp_defined == true)
        {
            maintenanceInfo.is_backbmp_existing = ExistsOptionalImageResource(chartDirectory, backbmp, lookupContext);
        }
        maintenanceInfo.is_banner_defined = !string.IsNullOrWhiteSpace(banner);
        if (maintenanceInfo.is_banner_defined == true)
        {
            maintenanceInfo.is_banner_existing = ExistsOptionalImageResource(chartDirectory, banner, lookupContext);
        }
        if (forceUpdate)
        {
            maintenanceInfo.is_files_warning_ignored = false;
        }

        return maintenanceInfo;
    }

    internal static IReadOnlyList<ChartWarning> BuildResourceHealthWarnings(BMSFileMaintenanceInfo maintenanceInfo)
    {
        if (maintenanceInfo == null)
        {
            return [];
        }
        List<ChartWarning> warnings = [];
        AppendHealthWarning(warnings, maintenanceInfo.GetWAVHealth(), maintenanceInfo.wav_files_defined, maintenanceInfo.wav_files_existing, ChartWarningKind.ResourceWavMissing, Resources.Warning_WavFilesNotFound);
        AppendHealthWarning(warnings, maintenanceInfo.GetBGAHealth(), maintenanceInfo.bga_files_defined, maintenanceInfo.bga_files_existing, ChartWarningKind.ResourceBgaMissing, Resources.Warning_BgaFilesNotFound);
        AppendHealthWarning(warnings, maintenanceInfo.GetMovieHealth(), maintenanceInfo.movie_files_defined, maintenanceInfo.movie_files_existing, ChartWarningKind.ResourceMovieMissing, Resources.Warning_MovieFilesNotFound);
        AppendFlagWarning(warnings, maintenanceInfo.GetStagefileHealth(), ChartWarningKind.ResourceStagefileMissing, Resources.Warning_StagefileNotFound);
        AppendFlagWarning(warnings, maintenanceInfo.GetBackbmpHealth(), ChartWarningKind.ResourceBackbmpMissing, Resources.Warning_BackbmpNotFound);
        AppendFlagWarning(warnings, maintenanceInfo.GetBannerHealth(), ChartWarningKind.ResourceBannerMissing, Resources.Warning_BannerNotFound);
        return warnings;
    }

    private static int CountExistingResourceReferences(
        string chartDirectory,
        IEnumerable<ChartResourceSnapshot.ResourceReference> references,
        IEnumerable<string> extensions,
        ChartResourceKind resourceKind,
        ResourceHealthLookupContext lookupContext)
    {
        string lookupDirectory = NormalizeLookupDirectory(chartDirectory);
        DirectoryResourceLookupCache.Entry resourceEntry = lookupContext?.GetResourceEntryOrNull(lookupDirectory);
        int existingCount = 0;
        foreach (ChartResourceSnapshot.ResourceReference reference in references ?? [])
        {
            if (TryResolveResourceReferenceFromCache(resourceEntry, reference, resourceKind, out bool existsInCache))
            {
                lookupContext?.RecordCacheHit();
                if (existsInCache)
                {
                    existingCount++;
                }
                continue;
            }

            lookupContext?.RecordFileExistsFallback(GetFallbackKind(resourceKind));
            if (ExistsWithCompatibleExtensions(chartDirectory, reference.NormalizedPath, extensions))
            {
                existingCount++;
            }
        }
        return existingCount;
    }

    private static bool ExistsOptionalImageResource(string chartDirectory, string file, ResourceHealthLookupContext lookupContext)
    {
        string normalizedPath = ChartResourcePathNormalizer.NormalizeReferencePathForLookup(file);
        if (string.IsNullOrWhiteSpace(normalizedPath))
        {
            return false;
        }
        string lookupDirectory = NormalizeLookupDirectory(chartDirectory);
        DirectoryResourceLookupCache.Entry resourceEntry = lookupContext?.GetResourceEntryOrNull(lookupDirectory);
        var reference = new ChartResourceSnapshot.ResourceReference(
            normalizedPath,
            ChartResourceKeyHash.GetLookupHash(normalizedPath),
            ChartResourcePathNormalizer.HasDirectorySegments(normalizedPath));
        if (TryResolveResourceReferenceFromCache(resourceEntry, reference, ChartResourceKind.Image, out bool existsInCache))
        {
            lookupContext?.RecordCacheHit();
            return existsInCache;
        }

        lookupContext?.RecordFileExistsFallback(ResourceHealthFallbackKind.OptionalImage);
        return ExistsWithCompatibleExtensions(chartDirectory, normalizedPath, ChartResourceExtensions.ImageExtensions);
    }

    private static string NormalizeLookupDirectory(string chartDirectory)
    {
        return (chartDirectory ?? string.Empty).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
    }

    private static bool TryResolveResourceReferenceFromCache(
        DirectoryResourceLookupCache.Entry resourceEntry,
        ChartResourceSnapshot.ResourceReference reference,
        ChartResourceKind resourceKind,
        out bool existsInCache)
    {
        existsInCache = false;
        if (resourceEntry == null || reference.RelativePathHash == 0u)
        {
            return false;
        }
        ISet<uint> hashes = resourceKind switch
        {
            ChartResourceKind.Audio => resourceEntry.AudioRelativePathHashes,
            ChartResourceKind.Image => resourceEntry.ImageRelativePathHashes,
            ChartResourceKind.Movie => resourceEntry.MovieRelativePathHashes,
            _ => null
        };
        if (hashes == null)
        {
            return false;
        }
        existsInCache = hashes.Contains(reference.RelativePathHash);
        return true;
    }

    private static ResourceHealthFallbackKind GetFallbackKind(ChartResourceKind resourceKind)
    {
        return resourceKind switch
        {
            ChartResourceKind.Audio => ResourceHealthFallbackKind.Audio,
            ChartResourceKind.Image => ResourceHealthFallbackKind.Image,
            ChartResourceKind.Movie => ResourceHealthFallbackKind.Movie,
            _ => ResourceHealthFallbackKind.Unknown
        };
    }

    private static bool ExistsWithCompatibleExtensions(string chartDirectory, string file, IEnumerable<string> extensions)
    {
        if (string.IsNullOrWhiteSpace(chartDirectory) || string.IsNullOrWhiteSpace(file))
        {
            return false;
        }
        try
        {
            string fullPath = Path.GetFullPath(Path.Combine(chartDirectory, ChartResourcePathNormalizer.NormalizeReferencePathForLookup(file) ?? file));
            string directory = Path.GetDirectoryName(fullPath) + Path.DirectorySeparatorChar;
            string basename = Path.GetFileNameWithoutExtension(fullPath);
            return File.Exists(fullPath) || (extensions ?? []).Any(ext => !string.IsNullOrWhiteSpace(ext) && File.Exists(directory + basename + ext));
        }
        catch
        {
            return false;
        }
    }

    public List<BMSFile> GetGarbledFiles(IEnumerable<BMSFile> bmsFiles, bool isInFixedList)
    {
        return [.. EnumerateBmsChartFiles(bmsFiles)
            .Where(file =>
            {
                BMSFileMaintenanceInfo info = file?.HasValidMaintenanceInfoSnapshot == true ? file.TryGetMaintenanceInfoWithoutCreating() : null;
                return !string.IsNullOrWhiteSpace(info?.encoding)
                    && isInFixedList == info.is_encoding_fixed
                    && !info.encoding.StartsWith("shift_jis");
            })];
    }

    /// <summary>
    /// chart_info 上のノート数が 0 の BMS-format chart を抽出します。
    /// </summary>
    /// <param name="charts">BMS / bmson を含む chart snapshot。</param>
    /// <returns>chart_info 上のノート数が 0 の BMS-format chart 一覧。</returns>
    public List<ChartFile> GetZeroNoteCharts(
        IEnumerable<ChartFile> charts,
        Func<ChartFile, LR2SongDBExtended.chart_info> chartInfoResolver = null)
    {
        return [.. (charts ?? []).Where(chart => chart?.Kind == ChartFileKind.Bms && ResolveChartInfo(chart, chartInfoResolver)?.notes == 0)];
    }

    public List<BMSFileMaintenanceInfo> SetChartResourceWarningsIgnored(IEnumerable<ChartFile> charts, bool unset)
    {
        List<BMSFileMaintenanceInfo> changes = [];
        foreach (ChartFile chart in charts ?? [])
        {
            BMSFileMaintenanceInfo maintenanceInfo = GetResourceHealthMaintenanceInfo(chart);
            if (maintenanceInfo == null || maintenanceInfo.is_files_warning_ignored != unset || changes.Contains(maintenanceInfo))
            {
                continue;
            }
            AttachResourceHealthMaintenanceInfo(chart, maintenanceInfo);
            changes.Add(maintenanceInfo);
        }
        foreach (BMSFileMaintenanceInfo item in changes)
        {
            item.is_files_warning_ignored = !unset;
        }
        return changes;
    }

    private static void AttachResourceHealthMaintenanceInfo(ChartFile chart, BMSFileMaintenanceInfo maintenanceInfo)
    {
        LR2SongDBExtended.bmson_song song = chart?.GetBmsonStorageOwner();
        if (song != null && maintenanceInfo != null && !ReferenceEquals(song.MaintenanceInfo, maintenanceInfo))
        {
            song.MaintenanceInfo = maintenanceInfo;
        }
    }

    public MaintenanceEncodingUpdateResult ApplyEncoding(IEnumerable<BMSFile> bmsFiles, string encoding)
    {
        var result = new MaintenanceEncodingUpdateResult();
        List<BMSFile> files = [.. EnumerateBmsChartFiles(bmsFiles)];
        HashSet<BMSFile> reloadedFiles = [];
        if (!string.IsNullOrWhiteSpace(encoding))
        {
            foreach (BMSFile file in files.Where(f => File.Exists(f.path)))
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
        List<BMSFileMaintenanceInfo> maintenanceChanges = [.. files.Select(delegate (BMSFile file)
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
        }).Where(info => info != null)];
        result.MaintenanceInfosToUpsert.AddRange(maintenanceChanges);
        return result;
    }

    private static bool ShouldReloadMetadata(BMSFile currentFile, string encoding)
    {
        if (currentFile == null || string.IsNullOrWhiteSpace(currentFile.path) || !File.Exists(currentFile.path))
        {
            return false;
        }
        var reloadedFile = BMSFile.CreateBMSFileFromFile(currentFile.path, encoding);
        return !string.Equals(currentFile.title ?? string.Empty, reloadedFile.title ?? string.Empty, StringComparison.Ordinal)
            || !string.Equals(currentFile.subtitle ?? string.Empty, reloadedFile.subtitle ?? string.Empty, StringComparison.Ordinal)
            || !string.Equals(currentFile.artist ?? string.Empty, reloadedFile.artist ?? string.Empty, StringComparison.Ordinal)
            || !string.Equals(currentFile.subartist ?? string.Empty, reloadedFile.subartist ?? string.Empty, StringComparison.Ordinal)
            || !string.Equals(currentFile.genre ?? string.Empty, reloadedFile.genre ?? string.Empty, StringComparison.Ordinal);
    }

    public ZeroNoteRecheckResult RecheckZeroNoteWarnings(
        IEnumerable<ChartFile> charts,
        Action<Exception, string> logWarn = null,
        Func<ChartFile, LR2SongDBExtended.chart_info> chartInfoResolver = null)
    {
        List<ChartFile> bmsCharts = [.. (charts ?? []).Where(chart => chart?.Kind == ChartFileKind.Bms && chart.GetBmsStorageOwner() != null)];
        List<ChartFile> zeroNoteCharts = [.. bmsCharts.Where(chart => ResolveChartInfo(chart, chartInfoResolver)?.notes == 0 && !string.IsNullOrWhiteSpace(chart.Path))];
        List<BMSFile> staleMismatchFiles = [.. bmsCharts
            .Where(chart => ResolveChartInfo(chart, chartInfoResolver)?.notes != 0)
            .Select(chart => chart.GetBmsStorageOwner())
            .Where(file => file != null && file.Warnings.Contains(ChartWarningKind.ZeroNoteMismatch))];
        var result = new ZeroNoteRecheckResult
        {
            Total = zeroNoteCharts.Count
        };
        foreach (BMSFile staleMismatchFile in staleMismatchFiles)
        {
            if (ClearZeroNoteMismatchWarning(staleMismatchFile))
            {
                result.ClearedCount++;
                result.ChangedCount++;
            }
        }
        foreach (ChartFile zeroNoteChart in zeroNoteCharts)
        {
            BMSFile zeroNoteFile = zeroNoteChart.GetBmsStorageOwner();
            try
            {
                bool isZeroNoteByFile = BMSFile.IsZeroNoteBMSFile(zeroNoteChart.Path);
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
                logWarn?.Invoke(ex, "zero_note_recheck skipped: path=" + zeroNoteChart.Path);
            }
        }
        return result;
    }

    private static LR2SongDBExtended.chart_info ResolveChartInfo(
        ChartFile chart,
        Func<ChartFile, LR2SongDBExtended.chart_info> chartInfoResolver)
    {
        if (chart == null)
        {
            return null;
        }
        return chartInfoResolver == null ? chart.ChartInfo : chartInfoResolver.Invoke(chart);
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
        List<BMSFile> targets = [.. EnumerateBmsChartFiles(bmsFiles).Where(file => (forceUpdate || !file.mode.HasValue) && File.Exists(file.path))];
        foreach (BMSFile target in targets)
        {
            target.SetMode();
        }
        return targets;
    }

    public MaintenanceWorkflowResult UpdateMaintenanceInfo(
        IEnumerable<ChartFile> charts,
        bool forceUpdate,
        BmsLibraryDbGateway dbGateway,
        IBmsLibraryDialogService dialogService,
        ResourceHealthLookupContext resourceLookupContext = null,
        Action<string> progressLogger = null,
        Action<MaintenanceWorkflowProgress> progressReporter = null,
        CancellationToken cancellationToken = default)
    {
        List<BMSFile> bmsTargets = [];
        var bmsonTargetsByPath = new Dictionary<string, LR2SongDBExtended.bmson_song>(StringComparer.OrdinalIgnoreCase);
        foreach (ChartFile chart in charts ?? [])
        {
            if (chart == null)
            {
                continue;
            }

            BMSFile bmsFile = chart.GetBmsStorageOwner();
            if (ChartFileKindResolver.IsBmsChartFile(bmsFile))
            {
                bmsTargets.Add(bmsFile);
                continue;
            }

            LR2SongDBExtended.bmson_song bmsonSong = chart.GetBmsonStorageOwner();
            if (bmsonSong != null && !string.IsNullOrWhiteSpace(bmsonSong.path))
            {
                bmsonTargetsByPath[bmsonSong.path] = bmsonSong;
            }
        }
        List<LR2SongDBExtended.bmson_song> bmsonTargets = [.. bmsonTargetsByPath.Values];
        if (bmsonTargets.Count == 0)
        {
            return UpdateBmsMaintenanceInfo(bmsTargets, forceUpdate, dbGateway, dialogService, resourceLookupContext, progressLogger, progressReporter, cancellationToken);
        }
        if (bmsTargets.Count == 0)
        {
            return UpdateBmsonMaintenanceInfo(bmsonTargets, forceUpdate, dbGateway, dialogService, resourceLookupContext, progressLogger, progressReporter, cancellationToken);
        }

        resourceLookupContext ??= new ResourceHealthLookupContext(null);
        MaintenanceWorkflowResult bmsResult = UpdateBmsMaintenanceInfo(bmsTargets, forceUpdate, dbGateway, dialogService, resourceLookupContext, progressLogger, progressReporter, cancellationToken);
        if (bmsResult.Canceled || cancellationToken.IsCancellationRequested)
        {
            return bmsResult;
        }
        MaintenanceWorkflowResult bmsonResult = UpdateBmsonMaintenanceInfo(bmsonTargets, forceUpdate, dbGateway, dialogService, resourceLookupContext, progressLogger, progressReporter, cancellationToken);
        return CombineResults(bmsResult, bmsonResult);
    }

    private MaintenanceWorkflowResult UpdateBmsMaintenanceInfo(
        IEnumerable<BMSFile> bmsFiles,
        bool forceUpdate,
        BmsLibraryDbGateway dbGateway,
        IBmsLibraryDialogService dialogService,
        ResourceHealthLookupContext resourceLookupContext = null,
        Action<string> progressLogger = null,
        Action<MaintenanceWorkflowProgress> progressReporter = null,
        CancellationToken cancellationToken = default)
    {
        var result = new MaintenanceWorkflowResult();
        if (bmsFiles == null || dbGateway == null)
        {
            return result;
        }
        var stopwatch = Stopwatch.StartNew();
        List<BMSFile> sourceFiles = [.. EnumerateBmsChartFiles(bmsFiles)];
        List<BMSFile> targets = [];
        foreach (BMSFile file in sourceFiles)
        {
            bool missingInfo = file?.maintenanceInfo?.IsInformationChecked() != true;
            bool missingEncoding = string.IsNullOrWhiteSpace(file?.maintenanceInfo?.encoding);
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
        }
        result.CheckedFileCount = targets.Count;
        result.BmsResourceTargetCount = targets.Count(ChartFileKindResolver.IsBmsChartFile);
        result.BmsonResourceTargetCount = 0;
        result.HealthTargetCount = targets.Count;
        result.HealthDegree = maintenanceHealthDegree;
        resourceLookupContext ??= new ResourceHealthLookupContext(null);
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
            var sectionStopwatch = Stopwatch.StartNew();
            object reloadedLock = new();
            List<BMSFile> reloadedFiles = [];
            object bmsonReparseLock = new();
            int bmsonReparsedInSection = 0;
            int bmsonReparseFailedInSection = 0;
            int bmsonResourceReferencesReusedInSection = 0;
            List<BMSFile> filesInSection = [.. section.Where(file => file != null)];
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
                    string originalHash = file.hash;
                    var beforeSnapshot = MaintenanceSnapshot.FromFile(file);
                    int retryCount = 0;
                    while (true)
                    {
                        try
                        {
                            long healthStart = Stopwatch.GetTimestamp();
                            file.SetHealthStatusUsingLookupContext(resourceLookupContext, forceUpdate, memClear: true);
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
                    if (forceUpdate || string.IsNullOrWhiteSpace(file.maintenanceInfo.encoding))
                    {
                        long encodingStart = Stopwatch.GetTimestamp();
                        file.SetEncosingInfo();
                        AddElapsedTicks(ref encodingTicks, encodingStart);
                    }
                    if (!string.IsNullOrWhiteSpace(file.maintenanceInfo.encoding) && !file.maintenanceInfo.encoding.StartsWith("shift_jis") && !file.maintenanceInfo.encoding.EndsWith("?") && file.maintenanceInfo.encoding != "unknown")
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
                    else if (originalHash != file.hash)
                    {
                        lock (reloadedLock)
                        {
                            reloadedFiles.Add(file);
                        }
                    }
                    var afterSnapshot = MaintenanceSnapshot.FromFile(file);
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
                List<BMSFileMaintenanceInfo> maintenanceInfos = [.. filesInSection.Where(file => file.maintenanceInfo.IsInformationChecked()).Select(file => file.maintenanceInfo)];
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

    private MaintenanceWorkflowResult UpdateBmsonMaintenanceInfo(
        IEnumerable<LR2SongDBExtended.bmson_song> bmsonSongs,
        bool forceUpdate,
        BmsLibraryDbGateway dbGateway,
        IBmsLibraryDialogService dialogService,
        ResourceHealthLookupContext resourceLookupContext = null,
        Action<string> progressLogger = null,
        Action<MaintenanceWorkflowProgress> progressReporter = null,
        CancellationToken cancellationToken = default)
    {
        var result = new MaintenanceWorkflowResult();
        if (bmsonSongs == null || dbGateway == null)
        {
            return result;
        }
        var stopwatch = Stopwatch.StartNew();
        List<LR2SongDBExtended.bmson_song> sourceSongs = [.. bmsonSongs.Where(song => song != null && !string.IsNullOrWhiteSpace(song.path))];
        List<LR2SongDBExtended.bmson_song> targets = [];
        foreach (LR2SongDBExtended.bmson_song song in sourceSongs)
        {
            BMSFileMaintenanceInfo maintenanceInfo = GetBmsonMaintenanceInfoForWorkflow(song);
            bool missingInfo = maintenanceInfo?.IsInformationChecked() != true;
            bool isTarget = forceUpdate || missingInfo;
            if (!isTarget)
            {
                continue;
            }
            targets.Add(song);
            if (forceUpdate)
            {
                result.ForceTargetCount++;
            }
            if (missingInfo)
            {
                result.MissingInfoTargetCount++;
            }
            if (!HasFreshCurrentBmsonResourceReferences(song))
            {
                result.BmsonMissingFreshResourceReferenceCount++;
            }
        }
        result.CheckedFileCount = targets.Count;
        result.BmsonResourceTargetCount = targets.Count;
        result.HealthTargetCount = targets.Count;
        result.HealthDegree = maintenanceHealthDegree;
        resourceLookupContext ??= new ResourceHealthLookupContext(null);
        progressLogger?.Invoke("maintenance_target_summary total=" + targets.Count
            + " sourceCount=" + sourceSongs.Count
            + " force=" + result.ForceTargetCount
            + " missingInfo=" + result.MissingInfoTargetCount
            + " missingEncoding=0"
            + " bmsonMissingFreshRefs=" + result.BmsonMissingFreshResourceReferenceCount
            + " healthDegree=" + maintenanceHealthDegree);
        if (targets.Count <= 0)
        {
            stopwatch.Stop();
            ApplyLookupCounters(result, resourceLookupContext);
            result.TotalMs = stopwatch.ElapsedMilliseconds;
            progressLogger?.Invoke("maintenance_update no_targets sourceCount=" + sourceSongs.Count
                + " healthDegree=" + maintenanceHealthDegree
                + " elapsedMs=" + result.TotalMs);
            progressReporter?.Invoke(new MaintenanceWorkflowProgress
            {
                TotalCount = sourceSongs.Count,
                ProcessedCount = sourceSongs.Count,
                IsCompleted = true
            });
            return result;
        }

        long healthTicks = 0L;
        long bmsonRefreshTicks = 0L;
        int sectionIndex = 0;
        int processedCount = 0;
        progressReporter?.Invoke(new MaintenanceWorkflowProgress
        {
            TotalCount = targets.Count,
            ProcessedCount = 0,
            CurrentPath = targets.FirstOrDefault()?.path ?? string.Empty
        });
        foreach (IEnumerable<LR2SongDBExtended.bmson_song> section in targets.Section(1000))
        {
            if (cancellationToken.IsCancellationRequested)
            {
                result.Canceled = true;
                break;
            }
            sectionIndex++;
            var sectionStopwatch = Stopwatch.StartNew();
            object bmsonReparseLock = new();
            int bmsonReparsedInSection = 0;
            int bmsonReparseFailedInSection = 0;
            int bmsonResourceReferencesReusedInSection = 0;
            List<LR2SongDBExtended.bmson_song> songsInSection = [.. section.Where(song => song != null)];
            object maintenanceInfoLock = new();
            List<BMSFileMaintenanceInfo> updatedMaintenanceInfos = [];
            progressReporter?.Invoke(new MaintenanceWorkflowProgress
            {
                TotalCount = targets.Count,
                ProcessedCount = processedCount,
                CurrentPath = songsInSection.FirstOrDefault()?.path ?? string.Empty
            });
            progressLogger?.Invoke("maintenance_update section_start section=" + sectionIndex
                + " targetCount=" + songsInSection.Count
                + " total=" + targets.Count
                + " healthDegree=" + maintenanceHealthDegree);
            try
            {
                Parallel.ForEach(songsInSection, new ParallelOptions { MaxDegreeOfParallelism = maintenanceHealthDegree }, delegate (LR2SongDBExtended.bmson_song song)
                {
                    BMSFileMaintenanceInfo beforeInfo = GetBmsonMaintenanceInfoForWorkflow(song);
                    beforeInfo?.NormalizeForBmson(song.path, song.md5);
                    if (forceUpdate || beforeInfo?.IsInformationChecked() != true)
                    {
                        long bmsonRefreshStart = Stopwatch.GetTimestamp();
                        BmsonResourceRefreshResult refreshResult = TryRefreshBmsonResourceReferences(song, forceUpdate);
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
                        else if (refreshResult == BmsonResourceRefreshResult.NotApplicable)
                        {
                            return;
                        }
                    }

                    try
                    {
                        long healthStart = Stopwatch.GetTimestamp();
                        ChartFile chart = ChartFileProjection.FromBmsonSong(
                            song,
                            includeWarningSnapshot: false,
                            includeResourceReferences: false);
                        BMSFileMaintenanceInfo afterInfo = BuildResourceHealthMaintenanceInfo(chart, resourceLookupContext, forceUpdate);
                        AddElapsedTicks(ref healthTicks, healthStart);
                        if (afterInfo == null)
                        {
                            return;
                        }
                        if (!forceUpdate && IsCurrentBmsonMaintenanceInfo(song, beforeInfo))
                        {
                            afterInfo.is_files_warning_ignored = beforeInfo.is_files_warning_ignored;
                        }
                        afterInfo.NormalizeForBmson(song.path, song.md5);
                        song.MaintenanceInfo = afterInfo;
                        lock (maintenanceInfoLock)
                        {
                            updatedMaintenanceInfos.Add(afterInfo);
                        }
                    }
                    catch (Exception ex)
                    {
                        if (ex is DirectoryNotFoundException || ex is FileNotFoundException || ex is IOException || ex is PathTooLongException || ex is SecurityException || ex is UnauthorizedAccessException)
                        {
                            dialogService?.Show(string.Format(Resources.Error_BmsLoadFailedSkip, song.path, ex.Message), Resources.MessageBoxTitle_Error, MessageBoxButton.OK, MessageBoxImage.Hand, MessageBoxResult.OK);
                            return;
                        }
                        throw;
                    }
                });
                List<BMSFileMaintenanceInfo> maintenanceInfos = [.. updatedMaintenanceInfos.Where(info => info?.IsInformationChecked() == true)];
                if (maintenanceInfos.Count > 0)
                {
                    dbGateway.ExecuteSongDbTransaction(delegate (Models.LR2.LR2SongDBExtended songDb)
                    {
                        foreach (BMSFileMaintenanceInfo maintenanceInfo in maintenanceInfos)
                        {
                            songDb.InsertOrReplace(maintenanceInfo, typeof(Models.LR2.LR2SongDBExtended.maintenance));
                        }
                    });
                    result.HasUpdates = true;
                    result.MaintenanceInfoUpsertCount += maintenanceInfos.Count;
                }
                result.BmsonReparsedCount += bmsonReparsedInSection;
                result.BmsonReparseFailedCount += bmsonReparseFailedInSection;
                result.BmsonResourceReferenceReusedCount += bmsonResourceReferencesReusedInSection;
                processedCount += songsInSection.Count;
                sectionStopwatch.Stop();
                progressLogger?.Invoke("maintenance_update section_done section=" + sectionIndex
                    + " targetCount=" + songsInSection.Count
                    + " maintenanceUpserted=" + maintenanceInfos.Count
                    + " songReloaded=0"
                    + " bmsonReparsed=" + bmsonReparsedInSection
                    + " bmsonReparseFailed=" + bmsonReparseFailedInSection
                    + " bmsonResourceRefsReused=" + bmsonResourceReferencesReusedInSection
                    + " elapsedMs=" + sectionStopwatch.ElapsedMilliseconds);
                progressReporter?.Invoke(new MaintenanceWorkflowProgress
                {
                    TotalCount = targets.Count,
                    ProcessedCount = processedCount,
                    CurrentPath = songsInSection.LastOrDefault()?.path ?? string.Empty,
                    IsCanceled = result.Canceled
                });
            }
            catch (Exception ex)
            {
                sectionStopwatch.Stop();
                progressLogger?.Invoke("maintenance_update section_failed section=" + sectionIndex
                    + " targetCount=" + songsInSection.Count
                    + " elapsedMs=" + sectionStopwatch.ElapsedMilliseconds
                    + " message=" + SanitizeLogValue(ex.Message));
                throw;
            }
        }
        stopwatch.Stop();
        result.HealthMs = TicksToMilliseconds(Interlocked.Read(ref healthTicks));
        result.BmsonRefreshMs = TicksToMilliseconds(Interlocked.Read(ref bmsonRefreshTicks));
        ApplyLookupCounters(result, resourceLookupContext);
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

    private static BMSFileMaintenanceInfo GetBmsonMaintenanceInfoForWorkflow(LR2SongDBExtended.bmson_song song)
    {
        if (song == null)
        {
            return null;
        }
        BMSFileMaintenanceInfo maintenanceInfo = IsCurrentBmsonMaintenanceInfo(song, song.MaintenanceInfo)
            ? song.MaintenanceInfo
            : BMSFileMaintenanceInfo.CreateForBmson(song.path, song.md5);
        maintenanceInfo?.NormalizeForBmson(song.path, song.md5);
        return maintenanceInfo;
    }

    private static void ApplyLookupCounters(MaintenanceWorkflowResult result, ResourceHealthLookupContext resourceLookupContext)
    {
        result.HealthCacheHitCount = resourceLookupContext.CacheHitCount;
        result.HealthFileExistsFallbackCount = resourceLookupContext.FileExistsFallbackCount;
        result.HealthAudioFileExistsFallbackCount = resourceLookupContext.AudioFileExistsFallbackCount;
        result.HealthImageFileExistsFallbackCount = resourceLookupContext.ImageFileExistsFallbackCount;
        result.HealthMovieFileExistsFallbackCount = resourceLookupContext.MovieFileExistsFallbackCount;
        result.HealthOptionalImageFileExistsFallbackCount = resourceLookupContext.OptionalImageFileExistsFallbackCount;
    }

    private static MaintenanceWorkflowResult CombineResults(MaintenanceWorkflowResult first, MaintenanceWorkflowResult second)
    {
        if (first == null)
        {
            return second ?? new MaintenanceWorkflowResult();
        }
        if (second == null)
        {
            return first;
        }
        first.HasUpdates |= second.HasUpdates;
        first.CheckedFileCount += second.CheckedFileCount;
        first.BmsResourceTargetCount += second.BmsResourceTargetCount;
        first.BmsonResourceTargetCount += second.BmsonResourceTargetCount;
        first.HealthTargetCount += second.HealthTargetCount;
        first.HealthDegree = Math.Max(first.HealthDegree, second.HealthDegree);
        first.ForceTargetCount += second.ForceTargetCount;
        first.MissingInfoTargetCount += second.MissingInfoTargetCount;
        first.MissingEncodingTargetCount += second.MissingEncodingTargetCount;
        first.BmsonMissingFreshResourceReferenceCount += second.BmsonMissingFreshResourceReferenceCount;
        first.HealthMs += second.HealthMs;
        first.EncodingMs += second.EncodingMs;
        first.BmsonRefreshMs += second.BmsonRefreshMs;
        first.HealthCacheHitCount = second.HealthCacheHitCount;
        first.HealthFileExistsFallbackCount = second.HealthFileExistsFallbackCount;
        first.HealthAudioFileExistsFallbackCount = second.HealthAudioFileExistsFallbackCount;
        first.HealthImageFileExistsFallbackCount = second.HealthImageFileExistsFallbackCount;
        first.HealthMovieFileExistsFallbackCount = second.HealthMovieFileExistsFallbackCount;
        first.HealthOptionalImageFileExistsFallbackCount = second.HealthOptionalImageFileExistsFallbackCount;
        first.BmsonReparsedCount += second.BmsonReparsedCount;
        first.BmsonReparseFailedCount += second.BmsonReparseFailedCount;
        first.BmsonResourceReferenceReusedCount += second.BmsonResourceReferenceReusedCount;
        first.MaintenanceInfoUpsertCount += second.MaintenanceInfoUpsertCount;
        first.SongUpsertCount += second.SongUpsertCount;
        first.ReloadedSongCount += second.ReloadedSongCount;
        first.ZeroNoteChangedCount += second.ZeroNoteChangedCount;
        first.WarningReapplyTargets += second.WarningReapplyTargets;
        first.WarningChangedCount += second.WarningChangedCount;
        first.Canceled |= second.Canceled;
        first.TotalMs += second.TotalMs;
        return first;
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

    private static BmsonResourceRefreshResult TryRefreshBmsonResourceReferences(LR2SongDBExtended.bmson_song song, bool forceUpdate)
    {
        if (song == null || string.IsNullOrWhiteSpace(song.path) || !File.Exists(song.path))
        {
            return BmsonResourceRefreshResult.NotApplicable;
        }
        if (!forceUpdate && HasFreshCurrentBmsonResourceReferences(song))
        {
            return BmsonResourceRefreshResult.Reused;
        }
        try
        {
            LR2SongDBExtended.bmson_song parsed = BmsonSongParser.Parse(song.path);
            UpdateBmsonResourceReferences(song, parsed);
            return BmsonResourceRefreshResult.Success;
        }
        catch
        {
            return BmsonResourceRefreshResult.Failed;
        }
    }

    private static void UpdateBmsonResourceReferences(LR2SongDBExtended.bmson_song target, LR2SongDBExtended.bmson_song parsed)
    {
        if (target == null || parsed == null)
        {
            return;
        }
        target.stagefile = parsed.stagefile;
        target.banner = parsed.banner;
        target.backbmp = parsed.backbmp;
        target.preview_music = parsed.preview_music;
        target.wav_files = parsed.wav_files ?? [];
        target.bga_files = parsed.bga_files ?? [];
        target.HasFreshResourceReferences = parsed.HasFreshResourceReferences;
    }

    private static bool HasFreshCurrentBmsonResourceReferences(LR2SongDBExtended.bmson_song song)
    {
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
