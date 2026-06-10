using System;
using System.Collections.Concurrent;
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

        string resourceSetSignature = BuildResourceHealthSetSignature(chart, resources);
        if (lookupContext?.TryGetResourceHealthCounts(
            chartDirectory,
            resourceSetSignature,
            out ResourceHealthLookupContext.ResourceHealthCounts cachedCounts) == true)
        {
            ApplyResourceHealthCounts(maintenanceInfo, cachedCounts);
        }
        else
        {
            ResourceHealthLookupContext.ResourceHealthCounts counts = ComputeResourceHealthCounts(
                chart,
                chartDirectory,
                resources,
                lookupContext);
            ApplyResourceHealthCounts(maintenanceInfo, counts);
            lookupContext?.SetResourceHealthCounts(chartDirectory, resourceSetSignature, counts);
        }
        if (forceUpdate)
        {
            maintenanceInfo.is_files_warning_ignored = false;
        }
        ApplyLr2CompatibilityFacts(chart, resources, maintenanceInfo);

        return maintenanceInfo;
    }

    private static ResourceHealthLookupContext.ResourceHealthCounts ComputeResourceHealthCounts(
        ChartFile chart,
        string chartDirectory,
        ChartResourceSnapshot resources,
        ResourceHealthLookupContext lookupContext)
    {
        int audioDefined = resources.AudioReferenceCount;
        int audioExisting = audioDefined > 0
            ? CountExistingResourceReferences(
                chartDirectory,
                resources.AudioReferences,
                ChartResourceExtensions.AudioExtensions,
                ChartResourceKind.Audio,
                lookupContext)
            : 0;
        int visualDefined = resources.VisualReferenceCount;
        int visualExisting = visualDefined > 0
            ? CountExistingResourceReferences(
                chartDirectory,
                resources.VisualReferences,
                ChartResourceExtensions.ImageExtensions,
                ChartResourceKind.Image,
                lookupContext)
            : 0;
        int movieDefined = resources.MovieReferenceCount;
        int movieExisting = movieDefined > 0
            ? CountExistingResourceReferences(
                chartDirectory,
                resources.MovieReferences,
                ChartResourceExtensions.MovieExtensions,
                ChartResourceKind.Movie,
                lookupContext)
            : 0;
        string stagefile = chart.Stagefile;
        bool stagefileDefined = !string.IsNullOrWhiteSpace(stagefile);
        bool stagefileExisting = stagefileDefined && ExistsOptionalImageResource(chartDirectory, stagefile, lookupContext);
        string backbmp = chart.Backbmp;
        bool backbmpDefined = !string.IsNullOrWhiteSpace(backbmp);
        bool backbmpExisting = backbmpDefined && ExistsOptionalImageResource(chartDirectory, backbmp, lookupContext);
        string banner = chart.Banner;
        bool bannerDefined = !string.IsNullOrWhiteSpace(banner);
        bool bannerExisting = bannerDefined && ExistsOptionalImageResource(chartDirectory, banner, lookupContext);
        return new ResourceHealthLookupContext.ResourceHealthCounts(
            audioDefined,
            audioExisting,
            visualDefined,
            visualExisting,
            movieDefined,
            movieExisting,
            stagefileDefined,
            stagefileExisting,
            backbmpDefined,
            backbmpExisting,
            bannerDefined,
            bannerExisting);
    }

    private static void ApplyResourceHealthCounts(
        BMSFileMaintenanceInfo maintenanceInfo,
        ResourceHealthLookupContext.ResourceHealthCounts counts)
    {
        maintenanceInfo.wav_files_defined = counts.AudioDefined;
        if (counts.AudioDefined > 0)
        {
            maintenanceInfo.wav_files_existing = counts.AudioExisting;
        }
        maintenanceInfo.bga_files_defined = counts.VisualDefined;
        if (counts.VisualDefined > 0)
        {
            maintenanceInfo.bga_files_existing = counts.VisualExisting;
        }
        maintenanceInfo.movie_files_defined = counts.MovieDefined;
        if (counts.MovieDefined > 0)
        {
            maintenanceInfo.movie_files_existing = counts.MovieExisting;
        }
        maintenanceInfo.is_stagefile_defined = counts.StagefileDefined;
        if (counts.StagefileDefined)
        {
            maintenanceInfo.is_stagefile_existing = counts.StagefileExisting;
        }
        maintenanceInfo.is_backbmp_defined = counts.BackbmpDefined;
        if (counts.BackbmpDefined)
        {
            maintenanceInfo.is_backbmp_existing = counts.BackbmpExisting;
        }
        maintenanceInfo.is_banner_defined = counts.BannerDefined;
        if (counts.BannerDefined)
        {
            maintenanceInfo.is_banner_existing = counts.BannerExisting;
        }
    }

    private static string BuildResourceHealthSetSignature(ChartFile chart, ChartResourceSnapshot resources)
    {
        if (resources == null)
        {
            return string.Empty;
        }

        var builder = new StringBuilder();
        AppendResourceSignatureSection(builder, 'A', resources.AudioReferences);
        AppendResourceSignatureSection(builder, 'I', resources.VisualReferences);
        AppendResourceSignatureSection(builder, 'M', resources.MovieReferences);
        AppendOptionalImageSignatureSection(builder, 'S', chart?.Stagefile);
        AppendOptionalImageSignatureSection(builder, 'B', chart?.Backbmp);
        AppendOptionalImageSignatureSection(builder, 'R', chart?.Banner);
        return builder.ToString();
    }

    private static void AppendResourceSignatureSection(
        StringBuilder builder,
        char section,
        IEnumerable<ChartResourceSnapshot.ResourceReference> references)
    {
        builder.Append(section).Append(':');
        foreach (string path in (references ?? [])
            .Select(reference => reference.NormalizedPath ?? string.Empty)
            .Where(path => !string.IsNullOrWhiteSpace(path))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(path => path, StringComparer.OrdinalIgnoreCase))
        {
            builder.Append(path.Length).Append('#').Append(path).Append(';');
        }
        builder.Append('|');
    }

    private static void AppendOptionalImageSignatureSection(StringBuilder builder, char section, string reference)
    {
        builder.Append(section).Append(':');
        string path = ChartResourcePathNormalizer.NormalizeResourceKeyForLookup(reference);
        if (!string.IsNullOrWhiteSpace(path))
        {
            builder.Append(path.Length).Append('#').Append(path).Append(';');
        }
        builder.Append('|');
    }

    private static void ApplyLr2CompatibilityFacts(
        ChartFile chart,
        ChartResourceSnapshot resources,
        BMSFileMaintenanceInfo maintenanceInfo)
    {
        if (chart?.Kind != ChartFileKind.Bms || maintenanceInfo == null)
        {
            return;
        }
        Lr2ChartPathEvaluation pathEvaluation = Lr2CompatibilityEvaluator.EvaluateChartPath(chart.Path);
        Lr2ResourceReferenceEvaluation resourceEvaluation = Lr2CompatibilityEvaluator.EvaluateResourceReferences(chart.Path, resources);
        maintenanceInfo.lr2_path_warning_flags = (int)pathEvaluation.WarningFlags;
        maintenanceInfo.lr2_chart_path_cp932_bytes = pathEvaluation.ChartPathCp932Bytes;
        maintenanceInfo.lr2_folder_scan_cp932_bytes = pathEvaluation.FolderScanPathCp932Bytes;
        maintenanceInfo.lr2_resource_warning_flags = (int)resourceEvaluation.WarningFlags;
        maintenanceInfo.lr2_resource_max_raw_cp932_bytes = resourceEvaluation.MaxRawCp932Bytes;
        maintenanceInfo.lr2_resource_max_resolved_cp932_bytes = resourceEvaluation.MaxResolvedCp932Bytes;
        maintenanceInfo.lr2_resource_unsupported_count = resourceEvaluation.UnsupportedCount;
    }

    internal static BMSFileMaintenanceInfo BuildBmsResourceHealthMaintenanceInfo(
        BMSFile file,
        ResourceHealthLookupContext lookupContext,
        bool forceUpdate = false,
        bool clearResourceReferences = false)
    {
        if (!ChartFileKindResolver.IsBmsChartFile(file) || !File.Exists(file.path))
        {
            return null;
        }
        try
        {
            ChartFile chart = ChartFileProjection.FromBmsFile(
                file,
                includeWarningSnapshot: false,
                includeResourceReferences: false,
                includeScoreSnapshot: false);
            return BuildResourceHealthMaintenanceInfo(chart, lookupContext, forceUpdate);
        }
        finally
        {
            if (clearResourceReferences)
            {
                file.ClearResourceReferenceCollections();
            }
        }
    }

    internal static bool ApplyBmsResourceHealthMaintenanceInfo(
        BMSFile file,
        ResourceHealthLookupContext lookupContext,
        bool forceUpdate,
        bool clearResourceReferences = true)
    {
        if (!ChartFileKindResolver.IsBmsChartFile(file))
        {
            return false;
        }
        if (!forceUpdate && file.maintenanceInfo.IsInformationChecked())
        {
            return true;
        }

        try
        {
            BMSFileMaintenanceInfo computedInfo = BuildBmsResourceHealthMaintenanceInfo(file, lookupContext, forceUpdate);
            if (computedInfo == null)
            {
                return false;
            }
            ApplyResourceHealthToBmsMaintenanceInfo(file, computedInfo, forceUpdate);
            return true;
        }
        finally
        {
            if (clearResourceReferences)
            {
                file.ClearResourceReferenceCollections();
            }
        }
    }

    private static void ApplyResourceHealthToBmsMaintenanceInfo(
        BMSFile file,
        BMSFileMaintenanceInfo computedInfo,
        bool forceUpdate)
    {
        BMSFileMaintenanceInfo targetInfo = file.maintenanceInfo;
        string encoding = targetInfo.encoding;
        bool isEncodingFixed = targetInfo.is_encoding_fixed;
        bool isFilesWarningIgnored = targetInfo.is_files_warning_ignored;
        using (BMSFileMaintenanceInfo.SuppressPropertyChangedScope())
        {
            targetInfo.path = computedInfo.path;
            targetInfo.hash = computedInfo.hash;
            targetInfo.wav_files_defined = computedInfo.wav_files_defined;
            targetInfo.wav_files_existing = computedInfo.wav_files_existing;
            targetInfo.bga_files_defined = computedInfo.bga_files_defined;
            targetInfo.bga_files_existing = computedInfo.bga_files_existing;
            targetInfo.movie_files_defined = computedInfo.movie_files_defined;
            targetInfo.movie_files_existing = computedInfo.movie_files_existing;
            targetInfo.is_stagefile_defined = computedInfo.is_stagefile_defined;
            targetInfo.is_stagefile_existing = computedInfo.is_stagefile_existing;
            targetInfo.is_banner_defined = computedInfo.is_banner_defined;
            targetInfo.is_banner_existing = computedInfo.is_banner_existing;
            targetInfo.is_backbmp_defined = computedInfo.is_backbmp_defined;
            targetInfo.is_backbmp_existing = computedInfo.is_backbmp_existing;
            targetInfo.lr2_path_warning_flags = computedInfo.lr2_path_warning_flags;
            targetInfo.lr2_chart_path_cp932_bytes = computedInfo.lr2_chart_path_cp932_bytes;
            targetInfo.lr2_folder_scan_cp932_bytes = computedInfo.lr2_folder_scan_cp932_bytes;
            targetInfo.lr2_resource_warning_flags = computedInfo.lr2_resource_warning_flags;
            targetInfo.lr2_resource_max_raw_cp932_bytes = computedInfo.lr2_resource_max_raw_cp932_bytes;
            targetInfo.lr2_resource_max_resolved_cp932_bytes = computedInfo.lr2_resource_max_resolved_cp932_bytes;
            targetInfo.lr2_resource_unsupported_count = computedInfo.lr2_resource_unsupported_count;
            targetInfo.encoding = encoding;
            targetInfo.is_encoding_fixed = isEncodingFixed;
            targetInfo.is_files_warning_ignored = forceUpdate ? false : isFilesWarningIgnored;
        }
        file.SetMaintenanceInfo(targetInfo, suppressPropertyChanged: true, origin: MaintenanceInfoOrigin.Calculated);
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
                lookupContext?.RecordResourceIndexHit();
                if (existsInCache)
                {
                    existingCount++;
                }
                continue;
            }

            if (lookupContext?.TryGetSharedResourceExists(
                lookupDirectory,
                resourceKind,
                reference.RelativePathHash,
                out bool sharedExistsInCache) == true)
            {
                lookupContext.RecordCacheHit();
                if (sharedExistsInCache)
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
        string normalizedPath = ChartResourcePathNormalizer.NormalizeResourceKeyForLookup(file);
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
            lookupContext?.RecordResourceIndexHit();
            return existsInCache;
        }

        if (lookupContext?.TryGetSharedResourceExists(
            lookupDirectory,
            ChartResourceKind.Image,
            reference.RelativePathHash,
            out bool sharedExistsInCache) == true)
        {
            lookupContext.RecordCacheHit();
            return sharedExistsInCache;
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
        existsInCache = resourceKind switch
        {
            ChartResourceKind.Audio => resourceEntry.ContainsAudioRelativePathHash(reference.RelativePathHash),
            ChartResourceKind.Image => resourceEntry.ContainsImageRelativePathHash(reference.RelativePathHash),
            ChartResourceKind.Movie => resourceEntry.ContainsMovieRelativePathHash(reference.RelativePathHash),
            _ => false
        };
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
        if (forceUpdate)
        {
            return UpdateMaintenanceInfoSnapshotPipeline(
                charts,
                dbGateway,
                dialogService,
                resourceLookupContext,
                progressLogger,
                progressReporter,
                cancellationToken);
        }

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

    private MaintenanceWorkflowResult UpdateMaintenanceInfoSnapshotPipeline(
        IEnumerable<ChartFile> charts,
        BmsLibraryDbGateway dbGateway,
        IBmsLibraryDialogService dialogService,
        ResourceHealthLookupContext resourceLookupContext = null,
        Action<string> progressLogger = null,
        Action<MaintenanceWorkflowProgress> progressReporter = null,
        CancellationToken cancellationToken = default)
    {
        var result = new MaintenanceWorkflowResult();
        if (charts == null || dbGateway == null)
        {
            return result;
        }

        var stopwatch = Stopwatch.StartNew();
        List<MaintenanceWorkflowTarget> targets = CreateSnapshotPipelineTargets(charts);
        result.CheckedFileCount = targets.Count;
        result.BmsResourceTargetCount = targets.Count(target => target.Kind == ChartFileKind.Bms);
        result.BmsonResourceTargetCount = targets.Count(target => target.Kind == ChartFileKind.Bmson);
        result.HealthTargetCount = targets.Count;
        result.HealthDegree = maintenanceHealthDegree;
        result.ReaderDegree = ChartFileReadPipelinePolicy.ResolveReaderDegree(Environment.ProcessorCount, targets.Count);
        result.ForceTargetCount = targets.Count;
        resourceLookupContext ??= new ResourceHealthLookupContext(null);
        const int chunkSize = 1000;
        int readQueueCapacity = ChartFileReadPipelinePolicy.ResolveReadQueueCapacity(maintenanceHealthDegree, result.ReaderDegree);
        int computedQueueCapacity = chunkSize * 2;
        result.ReadQueueCapacity = readQueueCapacity;
        result.ComputedQueueCapacity = computedQueueCapacity;
        progressLogger?.Invoke("maintenance_target_summary total=" + targets.Count
            + " sourceCount=" + targets.Count
            + " force=" + result.ForceTargetCount
            + " missingInfo=0"
            + " missingEncoding=0"
            + " bmsonMissingFreshRefs=" + targets.Count(target => target.Kind == ChartFileKind.Bmson && !HasFreshCurrentBmsonResourceReferences(target.BmsonSong))
            + " healthDegree=" + maintenanceHealthDegree
            + " readerDegree=" + result.ReaderDegree
            + " readQueueCapacity=" + readQueueCapacity
            + " computedQueueCapacity=" + computedQueueCapacity);
        if (targets.Count == 0)
        {
            stopwatch.Stop();
            ApplyLookupCounters(result, resourceLookupContext);
            result.TotalMs = stopwatch.ElapsedMilliseconds;
            progressReporter?.Invoke(new MaintenanceWorkflowProgress
            {
                TotalCount = 0,
                ProcessedCount = 0,
                IsCompleted = true
            });
            return result;
        }

        progressReporter?.Invoke(new MaintenanceWorkflowProgress
        {
            TotalCount = targets.Count,
            ProcessedCount = 0,
            CurrentPath = targets[0].Path
        });

        using var readQueue = new BlockingCollection<MaintenanceWorkflowCandidate>(readQueueCapacity);
        using var computedQueue = new BlockingCollection<MaintenanceWorkflowComputedItem>(computedQueueCapacity);
        long readTicks = 0L;
        long digestTicks = 0L;
        long computeTicks = 0L;
        long healthTicks = 0L;
        long encodingTicks = 0L;
        long bmsonRefreshTicks = 0L;
        long commitTicks = 0L;
        int chunkIndex = 0;
        int processedCount = 0;
        int evaluatedCount = 0;
        int writerFailed = 0;
        Exception writerFailure = null;

        void FlushChunk(List<MaintenanceWorkflowComputedItem> chunk)
        {
            if (chunk.Count == 0)
            {
                return;
            }

            int currentChunkIndex = ++chunkIndex;
            var chunkStopwatch = Stopwatch.StartNew();
            int bmsCount = 0;
            int bmsonCount = 0;
            int unchangedInChunk = 0;
            int bmsonReparsedInChunk = 0;
            int bmsonReparseFailedInChunk = 0;
            int bmsonResourceReferencesReusedInChunk = 0;
            var changedMaintenanceInfos = new List<BMSFileMaintenanceInfo>();
            var reloadedFiles = new List<BMSFile>();
            long chunkReadTicks = 0L;
            long chunkDigestTicks = 0L;
            long chunkComputeTicks = 0L;
            long chunkHealthTicks = 0L;
            long chunkEncodingTicks = 0L;
            long chunkBmsonRefreshTicks = 0L;
            long chunkCacheHitCount = 0L;
            long chunkResourceIndexHitCount = 0L;
            long chunkFileExistsFallbackCount = 0L;

            foreach (MaintenanceWorkflowComputedItem item in chunk)
            {
                if (item.Target.Kind == ChartFileKind.Bms)
                {
                    bmsCount++;
                }
                else
                {
                    bmsonCount++;
                }
                chunkReadTicks += item.ReadElapsedTicks;
                chunkDigestTicks += item.DigestElapsedTicks;
                chunkComputeTicks += item.ComputeElapsedTicks;
                MaintenanceEvaluationResult itemResult = item.Result;
                if (itemResult == null)
                {
                    continue;
                }
                chunkHealthTicks += itemResult.HealthElapsedTicks;
                chunkEncodingTicks += itemResult.EncodingElapsedTicks;
                chunkBmsonRefreshTicks += itemResult.BmsonRefreshElapsedTicks;
                chunkCacheHitCount += itemResult.CacheHitCount;
                chunkResourceIndexHitCount += itemResult.ResourceIndexHitCount;
                chunkFileExistsFallbackCount += itemResult.FileExistsFallbackCount;
                if (itemResult.MaintenanceInfoChanged && itemResult.MaintenanceInfo?.IsInformationChecked() == true)
                {
                    changedMaintenanceInfos.Add(itemResult.MaintenanceInfo);
                }
                else if (itemResult.MaintenanceInfo?.IsInformationChecked() == true)
                {
                    unchangedInChunk++;
                }
                if (itemResult.ReloadedBmsFile != null)
                {
                    reloadedFiles.Add(itemResult.ReloadedBmsFile);
                }
                switch (itemResult.BmsonRefreshResult)
                {
                    case BmsonResourceRefreshResult.Success:
                        bmsonReparsedInChunk++;
                        break;
                    case BmsonResourceRefreshResult.Failed:
                        bmsonReparseFailedInChunk++;
                        break;
                    case BmsonResourceRefreshResult.Reused:
                        bmsonResourceReferencesReusedInChunk++;
                        break;
                }
            }

            var commitStopwatch = Stopwatch.StartNew();
            if (changedMaintenanceInfos.Count > 0 || reloadedFiles.Count > 0)
            {
                dbGateway.ExecuteSongDbTransaction(delegate (Models.LR2.LR2SongDBExtended songDb)
                {
                    foreach (BMSFileMaintenanceInfo maintenanceInfo in changedMaintenanceInfos)
                    {
                        songDb.InsertOrReplace(maintenanceInfo, typeof(Models.LR2.LR2SongDBExtended.maintenance));
                    }
                    foreach (BMSFile reloadedFile in reloadedFiles)
                    {
                        Lr2SongDbWriter.UpsertGeneratedSong(songDb, reloadedFile);
                    }
                });
                result.HasUpdates = true;
                result.MaintenanceInfoUpsertCount += changedMaintenanceInfos.Count;
                result.ReloadedSongCount += reloadedFiles.Count;
                result.SongUpsertCount += reloadedFiles.Count;
            }
            commitStopwatch.Stop();

            readTicks += chunkReadTicks;
            digestTicks += chunkDigestTicks;
            computeTicks += chunkComputeTicks;
            healthTicks += chunkHealthTicks;
            encodingTicks += chunkEncodingTicks;
            bmsonRefreshTicks += chunkBmsonRefreshTicks;
            commitTicks += commitStopwatch.ElapsedTicks;
            result.MaintenanceInfoUnchangedCount += unchangedInChunk;
            result.BmsonReparsedCount += bmsonReparsedInChunk;
            result.BmsonReparseFailedCount += bmsonReparseFailedInChunk;
            result.BmsonResourceReferenceReusedCount += bmsonResourceReferencesReusedInChunk;
            processedCount += chunk.Count;
            chunkStopwatch.Stop();

            string currentPath = chunk[chunk.Count - 1].Target.Path ?? string.Empty;
            progressLogger?.Invoke("maintenance_rescan_chunk chunk=" + currentChunkIndex
                + " targetCount=" + chunk.Count
                + " total=" + targets.Count
                + " bmsCount=" + bmsCount
                + " bmsonCount=" + bmsonCount
                + " maintenanceUpserted=" + changedMaintenanceInfos.Count
                + " maintenanceUnchanged=" + unchangedInChunk
                + " songReloaded=" + reloadedFiles.Count
                + " bmsonReparsed=" + bmsonReparsedInChunk
                + " bmsonReparseFailed=" + bmsonReparseFailedInChunk
                + " bmsonResourceRefsReused=" + bmsonResourceReferencesReusedInChunk
                + " readMs=" + TicksToMilliseconds(chunkReadTicks)
                + " digestMs=" + TicksToMilliseconds(chunkDigestTicks)
                + " computeMs=" + TicksToMilliseconds(chunkComputeTicks)
                + " healthMs=" + TicksToMilliseconds(chunkHealthTicks)
                + " encodingMs=" + TicksToMilliseconds(chunkEncodingTicks)
                + " bmsonRefreshMs=" + TicksToMilliseconds(chunkBmsonRefreshTicks)
                + " cacheHit=" + chunkCacheHitCount
                + " resourceIndexHit=" + chunkResourceIndexHitCount
                + " fileExistsFallback=" + chunkFileExistsFallbackCount
                + " commitMs=" + commitStopwatch.ElapsedMilliseconds
                + " elapsedMs=" + chunkStopwatch.ElapsedMilliseconds);
            progressReporter?.Invoke(new MaintenanceWorkflowProgress
            {
                TotalCount = targets.Count,
                ProcessedCount = Volatile.Read(ref evaluatedCount),
                EvaluatedCount = Volatile.Read(ref evaluatedCount),
                CompletedCount = processedCount,
                CurrentPath = currentPath,
                IsCanceled = result.Canceled
            });
        }

        int nextReadIndex = -1;
        Task[] readerTasks = [.. Enumerable.Range(0, result.ReaderDegree)
            .Select(_ => Task.Run(delegate
            {
                try
                {
                    while (Volatile.Read(ref writerFailed) == 0)
                    {
                        if (cancellationToken.IsCancellationRequested)
                        {
                            result.Canceled = true;
                            break;
                        }

                        int targetIndex = Interlocked.Increment(ref nextReadIndex);
                        if (targetIndex >= targets.Count)
                        {
                            break;
                        }

                        readQueue.Add(ReadMaintenanceWorkflowCandidate(targets[targetIndex], dialogService), cancellationToken);
                    }
                }
                catch (OperationCanceledException)
                {
                    result.Canceled = true;
                }
            }))];
        Task readerCompletionTask = Task.WhenAll(readerTasks).ContinueWith(_ => readQueue.CompleteAdding());

        Task[] evaluatorTasks = [.. Enumerable.Range(0, maintenanceHealthDegree)
            .Select(_ => Task.Run(delegate
            {
                foreach (MaintenanceWorkflowCandidate candidate in readQueue.GetConsumingEnumerable())
                {
                    long digestElapsedTicks = 0L;
                    long computeStart = Stopwatch.GetTimestamp();
                    MaintenanceEvaluationResult itemResult = null;
                    ChartFileSnapshot snapshot = null;
                    if (candidate.Buffer != null)
                    {
                        long digestStart = Stopwatch.GetTimestamp();
                        snapshot = ChartFileContentReader.CreateSnapshot(candidate.Buffer);
                        digestElapsedTicks = Stopwatch.GetTimestamp() - digestStart;
                        ResourceHealthLookupContext itemLookupContext = resourceLookupContext.CreateCounterScope();
                        itemResult = EvaluateMaintenanceTarget(candidate.Target, snapshot, itemLookupContext, forceUpdate: true);
                        resourceLookupContext.AddCounters(itemLookupContext);
                    }
                    long computeElapsedTicks = candidate.Buffer == null
                        ? 0L
                        : Stopwatch.GetTimestamp() - computeStart;
                    if (Volatile.Read(ref writerFailed) != 0)
                    {
                        break;
                    }
                    try
                    {
                        int evaluated = Interlocked.Increment(ref evaluatedCount);
                        progressReporter?.Invoke(new MaintenanceWorkflowProgress
                        {
                            TotalCount = targets.Count,
                            ProcessedCount = evaluated,
                            EvaluatedCount = evaluated,
                            CompletedCount = Volatile.Read(ref processedCount),
                            CurrentPath = candidate.Target.Path ?? string.Empty,
                            IsCanceled = result.Canceled
                        });
                        computedQueue.Add(new MaintenanceWorkflowComputedItem(candidate.Target, itemResult, candidate.ReadElapsedTicks, digestElapsedTicks, computeElapsedTicks));
                    }
                    catch (InvalidOperationException) when (Volatile.Read(ref writerFailed) != 0)
                    {
                        break;
                    }
                }
            }))];
        Task evaluatorCompletionTask = Task.WhenAll(evaluatorTasks).ContinueWith(_ => computedQueue.CompleteAdding());

        Task writerTask = Task.Run(delegate
        {
            var chunk = new List<MaintenanceWorkflowComputedItem>(chunkSize);
            try
            {
                foreach (MaintenanceWorkflowComputedItem item in computedQueue.GetConsumingEnumerable())
                {
                    chunk.Add(item);
                    if (chunk.Count >= chunkSize)
                    {
                        FlushChunk(chunk);
                        chunk = new List<MaintenanceWorkflowComputedItem>(chunkSize);
                    }
                }
                FlushChunk(chunk);
            }
            catch (Exception ex)
            {
                writerFailure = ex;
                Volatile.Write(ref writerFailed, 1);
                try
                {
                    computedQueue.CompleteAdding();
                }
                catch (InvalidOperationException)
                {
                }
                progressLogger?.Invoke("maintenance_rescan_chunk_failed chunk=" + (chunkIndex + 1)
                    + " targetCount=" + chunk.Count
                    + " processed=" + processedCount
                    + " elapsedMs=" + stopwatch.ElapsedMilliseconds
                    + " message=" + SanitizeLogValue(ex.Message));
                throw;
            }
        });

        Exception pipelineException = null;
        try
        {
            Task.WaitAll(readerTasks);
            readerCompletionTask.Wait();
        }
        catch (AggregateException ex)
        {
            pipelineException = ex.Flatten().InnerExceptions.FirstOrDefault() ?? ex;
        }
        try
        {
            Task.WaitAll(evaluatorTasks);
        }
        catch (AggregateException ex)
        {
            pipelineException ??= ex.Flatten().InnerExceptions.FirstOrDefault() ?? ex;
        }
        try
        {
            evaluatorCompletionTask.Wait();
            writerTask.Wait();
        }
        catch (AggregateException ex)
        {
            pipelineException ??= ex.Flatten().InnerExceptions.FirstOrDefault() ?? ex;
        }
        if (pipelineException != null)
        {
            throw writerFailure ?? pipelineException;
        }

        stopwatch.Stop();
        result.ReadMs = TicksToMilliseconds(readTicks);
        result.DigestMs = TicksToMilliseconds(digestTicks);
        result.ComputeMs = TicksToMilliseconds(computeTicks);
        result.CommitMs = TicksToMilliseconds(commitTicks);
        result.HealthMs = TicksToMilliseconds(healthTicks);
        result.EncodingMs = TicksToMilliseconds(encodingTicks);
        result.BmsonRefreshMs = TicksToMilliseconds(bmsonRefreshTicks);
        ApplyLookupCounters(result, resourceLookupContext);
        result.TotalMs = stopwatch.ElapsedMilliseconds;
        progressReporter?.Invoke(new MaintenanceWorkflowProgress
        {
            TotalCount = targets.Count,
            ProcessedCount = Math.Max(Volatile.Read(ref evaluatedCount), processedCount),
            EvaluatedCount = Volatile.Read(ref evaluatedCount),
            CompletedCount = processedCount,
            CurrentPath = string.Empty,
            IsCompleted = !result.Canceled,
            IsCanceled = result.Canceled
        });
        return result;
    }

    private static List<MaintenanceWorkflowTarget> CreateSnapshotPipelineTargets(IEnumerable<ChartFile> charts)
    {
        List<MaintenanceWorkflowTarget> targets = [];
        var bmsonPaths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (ChartFile chart in charts ?? [])
        {
            if (chart == null || string.IsNullOrWhiteSpace(chart.Path))
            {
                continue;
            }

            BMSFile bmsFile = chart.GetBmsStorageOwner();
            if (ChartFileKindResolver.IsBmsChartFile(bmsFile))
            {
                targets.Add(MaintenanceWorkflowTarget.FromBms(bmsFile));
                continue;
            }

            LR2SongDBExtended.bmson_song bmsonSong = chart.GetBmsonStorageOwner();
            if (bmsonSong != null
                && !string.IsNullOrWhiteSpace(bmsonSong.path)
                && bmsonPaths.Add(bmsonSong.path))
            {
                targets.Add(MaintenanceWorkflowTarget.FromBmson(bmsonSong));
            }
        }
        return targets;
    }

    private static MaintenanceWorkflowCandidate ReadMaintenanceWorkflowCandidate(
        MaintenanceWorkflowTarget target,
        IBmsLibraryDialogService dialogService)
    {
        var stopwatch = Stopwatch.StartNew();
        try
        {
            ChartFileReadBuffer buffer = ChartFileContentReader.ReadBuffer(target.Path);
            stopwatch.Stop();
            return new MaintenanceWorkflowCandidate(target, buffer, null, stopwatch.ElapsedTicks);
        }
        catch (Exception ex) when (IsMaintenanceRecoverable(ex))
        {
            stopwatch.Stop();
            dialogService?.Show(string.Format(Resources.Error_BmsLoadFailedSkip, target.Path, ex.Message), Resources.MessageBoxTitle_Error, MessageBoxButton.OK, MessageBoxImage.Hand, MessageBoxResult.OK);
            return new MaintenanceWorkflowCandidate(target, null, ex, stopwatch.ElapsedTicks);
        }
    }

    private static MaintenanceEvaluationResult EvaluateMaintenanceTarget(
        MaintenanceWorkflowTarget target,
        ChartFileSnapshot snapshot,
        ResourceHealthLookupContext resourceLookupContext,
        bool forceUpdate)
    {
        return target.Kind == ChartFileKind.Bmson
            ? EvaluateBmsonMaintenance(target.BmsonSong, snapshot, resourceLookupContext, forceUpdate)
            : EvaluateBmsMaintenance(target.BmsFile, snapshot, resourceLookupContext, forceUpdate);
    }

    internal static MaintenanceEvaluationResult EvaluateBmsMaintenanceForInline(
        BMSFile file,
        ChartFileSnapshot snapshot,
        DirectoryResourceLookupCache lookupCache,
        bool forceUpdate = false)
    {
        return EvaluateBmsMaintenance(
            file,
            snapshot,
            new ResourceHealthLookupContext(lookupCache),
            forceUpdate,
            componentReferencesAlreadyApplied: false);
    }

    internal static MaintenanceEvaluationResult EvaluateBmsMaintenanceForInline(
        BMSFile file,
        ChartFileSnapshot snapshot,
        ResourceHealthLookupContext lookupContext,
        bool forceUpdate = false,
        bool componentReferencesAlreadyApplied = false)
    {
        return EvaluateBmsMaintenance(
            file,
            snapshot,
            lookupContext ?? new ResourceHealthLookupContext(null),
            forceUpdate,
            componentReferencesAlreadyApplied);
    }

    internal static MaintenanceEvaluationResult EvaluateBmsonMaintenanceForInline(
        LR2SongDBExtended.bmson_song song,
        DirectoryResourceLookupCache lookupCache)
    {
        return EvaluateBmsonMaintenance(song, null, new ResourceHealthLookupContext(lookupCache), forceUpdate: false);
    }

    internal static MaintenanceEvaluationResult EvaluateBmsonMaintenanceForInline(
        LR2SongDBExtended.bmson_song song,
        ResourceHealthLookupContext lookupContext)
    {
        return EvaluateBmsonMaintenance(song, null, lookupContext ?? new ResourceHealthLookupContext(null), forceUpdate: false);
    }

    private static MaintenanceEvaluationResult EvaluateBmsMaintenance(
        BMSFile file,
        ChartFileSnapshot snapshot,
        ResourceHealthLookupContext resourceLookupContext,
        bool forceUpdate,
        bool componentReferencesAlreadyApplied = false)
    {
        var result = new MaintenanceEvaluationResult();
        long cacheHitCountBefore = resourceLookupContext?.CacheHitCount ?? 0L;
        long resourceIndexHitCountBefore = resourceLookupContext?.ResourceIndexHitCount ?? 0L;
        long resourceHealthSetCacheHitCountBefore = resourceLookupContext?.ResourceHealthSetCacheHitCount ?? 0L;
        long fileExistsFallbackCountBefore = resourceLookupContext?.FileExistsFallbackCount ?? 0L;
        if (!ChartFileKindResolver.IsBmsChartFile(file))
        {
            return result;
        }

        BMSFileMaintenanceInfo beforeInfo = CloneMaintenanceInfo(file.TryGetMaintenanceInfoWithoutCreating());
        var beforeSnapshot = MaintenanceSnapshot.FromFile(file);
        if (snapshot != null && (!componentReferencesAlreadyApplied || !HasLoadedResourceReferenceCollections(file)))
        {
            BMSFile parsed = BMSFile.CreateBMSFileFromSnapshot(snapshot);
            file.ApplyComponentFilesFromParsedSnapshot(parsed);
        }

        long healthStart = Stopwatch.GetTimestamp();
        ApplyBmsResourceHealthMaintenanceInfo(file, resourceLookupContext, forceUpdate, clearResourceReferences: true);
        result.HealthElapsedTicks = Stopwatch.GetTimestamp() - healthStart;

        bool encodingNeeded = forceUpdate || string.IsNullOrWhiteSpace(file.maintenanceInfo.encoding);
        if (encodingNeeded)
        {
            long encodingStart = Stopwatch.GetTimestamp();
            BMSFile.BmsEncodingDetectionResult detectionResult = snapshot != null
                ? file.SetEncodingInfoFromSnapshotDetailed(snapshot)
                : file.SetEncodingInfoFromSnapshotDetailed(ChartFileContentReader.ReadSnapshot(file.path));
            result.EncodingDetectionResult = detectionResult;
            result.EncodingElapsedTicks += Stopwatch.GetTimestamp() - encodingStart;
            if (ShouldReloadBmsForFixedEncoding(file.maintenanceInfo?.encoding))
            {
                long reloadStart = Stopwatch.GetTimestamp();
                if (snapshot != null)
                {
                    BMSFile.ReloadBMSMetadataWithEncodingDetection(file, snapshot, detectionResult);
                }
                else
                {
                    BMSFile.ReloadBMSFileWithEncoding(file, file.maintenanceInfo.encoding);
                }
                file.maintenanceInfo.is_encoding_fixed = true;
                long reloadTicks = Stopwatch.GetTimestamp() - reloadStart;
                result.EncodingElapsedTicks += reloadTicks;
                result.EncodingReloadElapsedTicks = reloadTicks;
                result.EncodingReloadCount = 1;
                result.ReloadedBmsFile = file;
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

        result.MaintenanceInfo = file.TryGetMaintenanceInfoWithoutCreating();
        result.MaintenanceInfoChanged = !MaintenanceRowsEquivalent(beforeInfo, result.MaintenanceInfo);
        result.CacheHitCount = Math.Max(0L, (resourceLookupContext?.CacheHitCount ?? 0L) - cacheHitCountBefore);
        result.ResourceIndexHitCount = Math.Max(0L, (resourceLookupContext?.ResourceIndexHitCount ?? 0L) - resourceIndexHitCountBefore);
        result.ResourceHealthSetCacheHitCount = Math.Max(0L, (resourceLookupContext?.ResourceHealthSetCacheHitCount ?? 0L) - resourceHealthSetCacheHitCountBefore);
        result.FileExistsFallbackCount = Math.Max(0L, (resourceLookupContext?.FileExistsFallbackCount ?? 0L) - fileExistsFallbackCountBefore);
        return result;
    }

    private static bool HasLoadedResourceReferenceCollections(BMSFile file)
    {
        return file?.WAVfiles != null && file.BGAfiles != null;
    }

    private static MaintenanceEvaluationResult EvaluateBmsonMaintenance(
        LR2SongDBExtended.bmson_song song,
        ChartFileSnapshot snapshot,
        ResourceHealthLookupContext resourceLookupContext,
        bool forceUpdate)
    {
        var result = new MaintenanceEvaluationResult();
        long cacheHitCountBefore = resourceLookupContext?.CacheHitCount ?? 0L;
        long resourceIndexHitCountBefore = resourceLookupContext?.ResourceIndexHitCount ?? 0L;
        long resourceHealthSetCacheHitCountBefore = resourceLookupContext?.ResourceHealthSetCacheHitCount ?? 0L;
        long fileExistsFallbackCountBefore = resourceLookupContext?.FileExistsFallbackCount ?? 0L;
        if (song == null || string.IsNullOrWhiteSpace(song.path))
        {
            return result;
        }

        BMSFileMaintenanceInfo beforeInfo = CloneMaintenanceInfo(GetBmsonMaintenanceInfoForWorkflow(song));
        if (forceUpdate || !HasFreshCurrentBmsonResourceReferences(song))
        {
            long refreshStart = Stopwatch.GetTimestamp();
            result.BmsonRefreshResult = TryRefreshBmsonResourceReferences(song, forceUpdate, snapshot);
            result.BmsonRefreshElapsedTicks = Stopwatch.GetTimestamp() - refreshStart;
            if (result.BmsonRefreshResult == BmsonResourceRefreshResult.Failed
                || result.BmsonRefreshResult == BmsonResourceRefreshResult.NotApplicable)
            {
                return result;
            }
        }
        else
        {
            result.BmsonRefreshResult = BmsonResourceRefreshResult.Reused;
        }

        long healthStart = Stopwatch.GetTimestamp();
        ChartFile chart = ChartFileProjection.FromBmsonSong(
            song,
            includeWarningSnapshot: false,
            includeResourceReferences: false);
        BMSFileMaintenanceInfo afterInfo = BuildResourceHealthMaintenanceInfo(chart, resourceLookupContext, forceUpdate);
        result.HealthElapsedTicks = Stopwatch.GetTimestamp() - healthStart;
        if (afterInfo == null)
        {
            return result;
        }
        if (!forceUpdate && IsCurrentBmsonMaintenanceInfo(song, beforeInfo))
        {
            afterInfo.is_files_warning_ignored = beforeInfo.is_files_warning_ignored;
        }
        afterInfo.NormalizeForBmson(song.path, song.md5);
        song.MaintenanceInfo = afterInfo;
        result.MaintenanceInfo = afterInfo;
        result.MaintenanceInfoChanged = !MaintenanceRowsEquivalent(beforeInfo, afterInfo);
        result.CacheHitCount = Math.Max(0L, (resourceLookupContext?.CacheHitCount ?? 0L) - cacheHitCountBefore);
        result.ResourceIndexHitCount = Math.Max(0L, (resourceLookupContext?.ResourceIndexHitCount ?? 0L) - resourceIndexHitCountBefore);
        result.ResourceHealthSetCacheHitCount = Math.Max(0L, (resourceLookupContext?.ResourceHealthSetCacheHitCount ?? 0L) - resourceHealthSetCacheHitCountBefore);
        result.FileExistsFallbackCount = Math.Max(0L, (resourceLookupContext?.FileExistsFallbackCount ?? 0L) - fileExistsFallbackCountBefore);
        return result;
    }

    private static bool ShouldReloadBmsForFixedEncoding(string encoding)
    {
        return !string.IsNullOrWhiteSpace(encoding)
            && !encoding.StartsWith("shift_jis", StringComparison.OrdinalIgnoreCase)
            && !encoding.EndsWith("?", StringComparison.Ordinal)
            && !string.Equals(encoding, "unknown", StringComparison.OrdinalIgnoreCase);
    }

    private static bool MaintenanceRowsEquivalent(BMSFileMaintenanceInfo left, BMSFileMaintenanceInfo right)
    {
        if (left == null || right == null)
        {
            return left == null && right == null;
        }
        return string.Equals(left.hash ?? string.Empty, right.hash ?? string.Empty, StringComparison.OrdinalIgnoreCase)
            && string.Equals(left.path ?? string.Empty, right.path ?? string.Empty, StringComparison.OrdinalIgnoreCase)
            && string.Equals(left.encoding ?? string.Empty, right.encoding ?? string.Empty, StringComparison.Ordinal)
            && left.is_encoding_fixed == right.is_encoding_fixed
            && left.wav_files_existing == right.wav_files_existing
            && left.wav_files_defined == right.wav_files_defined
            && left.bga_files_existing == right.bga_files_existing
            && left.bga_files_defined == right.bga_files_defined
            && left.movie_files_existing == right.movie_files_existing
            && left.movie_files_defined == right.movie_files_defined
            && left.is_stagefile_existing == right.is_stagefile_existing
            && left.is_stagefile_defined == right.is_stagefile_defined
            && left.is_banner_existing == right.is_banner_existing
            && left.is_banner_defined == right.is_banner_defined
            && left.is_backbmp_existing == right.is_backbmp_existing
            && left.is_backbmp_defined == right.is_backbmp_defined
            && left.is_files_warning_ignored == right.is_files_warning_ignored
            && left.lr2_path_warning_flags == right.lr2_path_warning_flags
            && left.lr2_chart_path_cp932_bytes == right.lr2_chart_path_cp932_bytes
            && left.lr2_folder_scan_cp932_bytes == right.lr2_folder_scan_cp932_bytes
            && left.lr2_resource_warning_flags == right.lr2_resource_warning_flags
            && left.lr2_resource_max_raw_cp932_bytes == right.lr2_resource_max_raw_cp932_bytes
            && left.lr2_resource_max_resolved_cp932_bytes == right.lr2_resource_max_resolved_cp932_bytes
            && left.lr2_resource_unsupported_count == right.lr2_resource_unsupported_count;
    }

    private static BMSFileMaintenanceInfo CloneMaintenanceInfo(BMSFileMaintenanceInfo source)
    {
        if (source == null)
        {
            return null;
        }
        return new BMSFileMaintenanceInfo
        {
            hash = source.hash,
            path = source.path,
            encoding = source.encoding,
            is_encoding_fixed = source.is_encoding_fixed,
            wav_files_existing = source.wav_files_existing,
            wav_files_defined = source.wav_files_defined,
            bga_files_existing = source.bga_files_existing,
            bga_files_defined = source.bga_files_defined,
            movie_files_existing = source.movie_files_existing,
            movie_files_defined = source.movie_files_defined,
            is_stagefile_existing = source.is_stagefile_existing,
            is_stagefile_defined = source.is_stagefile_defined,
            is_banner_existing = source.is_banner_existing,
            is_banner_defined = source.is_banner_defined,
            is_backbmp_existing = source.is_backbmp_existing,
            is_backbmp_defined = source.is_backbmp_defined,
            is_files_warning_ignored = source.is_files_warning_ignored,
            lr2_path_warning_flags = source.lr2_path_warning_flags,
            lr2_chart_path_cp932_bytes = source.lr2_chart_path_cp932_bytes,
            lr2_folder_scan_cp932_bytes = source.lr2_folder_scan_cp932_bytes,
            lr2_resource_warning_flags = source.lr2_resource_warning_flags,
            lr2_resource_max_raw_cp932_bytes = source.lr2_resource_max_raw_cp932_bytes,
            lr2_resource_max_resolved_cp932_bytes = source.lr2_resource_max_resolved_cp932_bytes,
            lr2_resource_unsupported_count = source.lr2_resource_unsupported_count
        };
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
        int healthTargetCount = 0;
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
            if (forceUpdate || missingInfo)
            {
                healthTargetCount++;
            }
        }
        result.CheckedFileCount = targets.Count;
        result.BmsResourceTargetCount = healthTargetCount;
        result.BmsonResourceTargetCount = 0;
        result.HealthTargetCount = healthTargetCount;
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
                    var beforeSnapshot = MaintenanceSnapshot.FromFile(file);
                    bool healthNeeded = forceUpdate || file.maintenanceInfo.IsInformationChecked() != true;
                    int retryCount = 0;
                    while (healthNeeded)
                    {
                        try
                        {
                            long healthStart = Stopwatch.GetTimestamp();
                            ApplyBmsResourceHealthMaintenanceInfo(file, resourceLookupContext, forceUpdate);
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
                            Lr2SongDbWriter.UpsertGeneratedSong(songDb, reloadedFile);
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
        first.ReaderDegree = Math.Max(first.ReaderDegree, second.ReaderDegree);
        first.ReadQueueCapacity = Math.Max(first.ReadQueueCapacity, second.ReadQueueCapacity);
        first.ComputedQueueCapacity = Math.Max(first.ComputedQueueCapacity, second.ComputedQueueCapacity);
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
        first.MaintenanceInfoUnchangedCount += second.MaintenanceInfoUnchangedCount;
        first.SongUpsertCount += second.SongUpsertCount;
        first.ReloadedSongCount += second.ReloadedSongCount;
        first.ZeroNoteChangedCount += second.ZeroNoteChangedCount;
        first.ReadMs += second.ReadMs;
        first.DigestMs += second.DigestMs;
        first.ComputeMs += second.ComputeMs;
        first.CommitMs += second.CommitMs;
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

    private static bool IsMaintenanceRecoverable(Exception ex)
    {
        return ex is DirectoryNotFoundException
            || ex is FileNotFoundException
            || ex is IOException
            || ex is PathTooLongException
            || ex is SecurityException
            || ex is UnauthorizedAccessException;
    }

    private static long TicksToMilliseconds(long stopwatchTicks)
    {
        return stopwatchTicks <= 0L ? 0L : (long)(stopwatchTicks * 1000.0 / Stopwatch.Frequency);
    }

    private static BmsonResourceRefreshResult TryRefreshBmsonResourceReferences(
        LR2SongDBExtended.bmson_song song,
        bool forceUpdate,
        ChartFileSnapshot snapshot = null)
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
            LR2SongDBExtended.bmson_song parsed = snapshot != null
                ? BmsonSongParser.ParseSnapshot(snapshot)
                : BmsonSongParser.Parse(song.path);
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
        target.UnsupportedResourceReferences = [.. (parsed.UnsupportedResourceReferences ?? [])];
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

    internal enum BmsonResourceRefreshResult
    {
        NotApplicable,
        Reused,
        Success,
        Failed
    }

    private sealed class MaintenanceWorkflowTarget
    {
        private MaintenanceWorkflowTarget(ChartFileKind kind, BMSFile bmsFile, LR2SongDBExtended.bmson_song bmsonSong)
        {
            Kind = kind;
            BmsFile = bmsFile;
            BmsonSong = bmsonSong;
        }

        public ChartFileKind Kind { get; }

        public BMSFile BmsFile { get; }

        public LR2SongDBExtended.bmson_song BmsonSong { get; }

        public string Path => BmsFile?.path ?? BmsonSong?.path ?? string.Empty;

        public static MaintenanceWorkflowTarget FromBms(BMSFile file)
        {
            return new MaintenanceWorkflowTarget(ChartFileKind.Bms, file, null);
        }

        public static MaintenanceWorkflowTarget FromBmson(LR2SongDBExtended.bmson_song song)
        {
            return new MaintenanceWorkflowTarget(ChartFileKind.Bmson, null, song);
        }
    }

    private sealed class MaintenanceWorkflowCandidate(
        MaintenanceWorkflowTarget target,
        ChartFileReadBuffer buffer,
        Exception exception,
        long readElapsedTicks)
    {
        public MaintenanceWorkflowTarget Target { get; } = target;

        public ChartFileReadBuffer Buffer { get; } = buffer;

        public Exception Exception { get; } = exception;

        public long ReadElapsedTicks { get; } = readElapsedTicks;
    }

    private sealed class MaintenanceWorkflowComputedItem(
        MaintenanceWorkflowTarget target,
        MaintenanceEvaluationResult result,
        long readElapsedTicks,
        long digestElapsedTicks,
        long computeElapsedTicks)
    {
        public MaintenanceWorkflowTarget Target { get; } = target;

        public MaintenanceEvaluationResult Result { get; } = result;

        public long ReadElapsedTicks { get; } = readElapsedTicks;

        public long DigestElapsedTicks { get; } = digestElapsedTicks;

        public long ComputeElapsedTicks { get; } = computeElapsedTicks;
    }

    internal sealed class MaintenanceEvaluationResult
    {
        public BMSFileMaintenanceInfo MaintenanceInfo { get; set; }

        public bool MaintenanceInfoChanged { get; set; }

        public BMSFile ReloadedBmsFile { get; set; }

        public long HealthElapsedTicks { get; set; }

        public long EncodingElapsedTicks { get; set; }

        public long EncodingReloadElapsedTicks { get; set; }

        public long BmsonRefreshElapsedTicks { get; set; }

        public BMSFile.BmsEncodingDetectionResult EncodingDetectionResult { get; set; }

        public int EncodingReloadCount { get; set; }

        public long CacheHitCount { get; set; }

        public long ResourceIndexHitCount { get; set; }

        public long ResourceHealthSetCacheHitCount { get; set; }

        public long FileExistsFallbackCount { get; set; }

        public BmsonResourceRefreshResult BmsonRefreshResult { get; set; } = BmsonResourceRefreshResult.NotApplicable;
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
