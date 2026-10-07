using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Security;
using System.Threading;
using System.Threading.Tasks;
using BeMusicSeeker.Models.Utils;
using BeMusicSeeker.Properties;
using MessageBoxButton = BeMusicSeeker.Models.UiDialogButton;
using MessageBoxImage = BeMusicSeeker.Models.UiDialogIcon;
using MessageBoxResult = BeMusicSeeker.Models.UiDialogDefaultResult;

namespace BeMusicSeeker.Models.BmsLibraryInternal;

/// <summary>
/// 捕捉した共通譜面を評価し、不変の保守値と書込み要求を返します。現在値とDBは変更しません。
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

    /// <summary>捕捉済みの保守値から資源警告を返します。表示だけでは資源を読み取りません。</summary>
    public IReadOnlyList<ChartWarning> BuildResourceHealthWarnings(ChartFile chart)
        => BuildResourceHealthWarnings(GetResourceHealthSnapshot(chart));

    /// <summary>捕捉した警告除外状態を返します。</summary>
    internal static bool AreResourceHealthWarningsIgnored(ChartFile chart)
        => chart?.ResourceHealthMaintenanceSnapshot?.FilesWarningIgnored == true || chart?.ResourceHealthWarningsIgnored == true;

    /// <summary>現在のパスとハッシュに対応する捕捉済み保守値を返します。</summary>
    internal static ResourceHealthMaintenanceSnapshot GetResourceHealthSnapshot(ChartFile chart)
    {
        ResourceHealthMaintenanceSnapshot value = chart?.ResourceHealthMaintenanceSnapshot;
        return value != null && string.Equals(value.Path, chart.Path, StringComparison.OrdinalIgnoreCase)
            && string.Equals(value.Hash, chart.Md5, StringComparison.OrdinalIgnoreCase) ? value : null;
    }

    /// <summary>取得済み資源を評価し、不変の共通保守値を返します。保存行と譜面の現在値を変更しません。</summary>
    internal static ResourceHealthMaintenanceSnapshot BuildResourceHealthSnapshot(
        ChartFile chart, ResourceHealthLookupContext lookupContext = null, bool forceUpdate = false)
    {
        if (chart == null || string.IsNullOrWhiteSpace(chart.Path)
            || (chart.Resources == null && !LongPathFileSystem.FileExists(chart.Path)))
        {
            return null;
        }
        string chartDirectory = Path.GetDirectoryName(chart.Path);
        if (string.IsNullOrWhiteSpace(chartDirectory))
        {
            return null;
        }
        var resources = ChartResourceSnapshot.Create(chart);
        ResourceHealthLookupContext.ResourceHealthSetSignature signature = BuildResourceHealthSetSignature(chart, resources);
        ResourceHealthLookupContext.ResourceHealthCounts counts;
        if (lookupContext?.TryGetResourceHealthCounts(chartDirectory, signature, out counts) != true)
        {
            counts = ComputeResourceHealthCounts(chart, chartDirectory, resources, lookupContext);
            lookupContext?.SetResourceHealthCounts(chartDirectory, signature, counts);
        }
        Lr2ChartPathEvaluation path = chart.Kind == ChartFileKind.Bms
            ? Lr2CompatibilityEvaluator.EvaluateChartPath(chart.Path) : default;
        Lr2ResourceReferenceEvaluation resource = chart.Kind == ChartFileKind.Bms
            ? Lr2CompatibilityEvaluator.EvaluateResourceReferences(chart.Path, resources) : default;
        return new ResourceHealthMaintenanceSnapshot
        {
            Path = chart.Path,
            Hash = chart.Md5,
            Encoding = chart.Kind == ChartFileKind.Bmson ? "utf-8" : null,
            Origin = MaintenanceInfoOrigin.Calculated,
            WavFilesDefined = counts.AudioDefined,
            WavFilesExisting = counts.AudioDefined > 0 ? counts.AudioExisting : null,
            BgaFilesDefined = counts.VisualDefined,
            BgaFilesExisting = counts.VisualDefined > 0 ? counts.VisualExisting : null,
            MovieFilesDefined = counts.MovieDefined,
            MovieFilesExisting = counts.MovieDefined > 0 ? counts.MovieExisting : null,
            StagefileDefined = counts.StagefileDefined,
            StagefileExisting = counts.StagefileDefined ? counts.StagefileExisting : null,
            BackbmpDefined = counts.BackbmpDefined,
            BackbmpExisting = counts.BackbmpDefined ? counts.BackbmpExisting : null,
            BannerDefined = counts.BannerDefined,
            BannerExisting = counts.BannerDefined ? counts.BannerExisting : null,
            FilesWarningIgnored = !forceUpdate && chart.ResourceHealthWarningsIgnored,
            Lr2WarningFlags = chart.Kind == ChartFileKind.Bms ? (int)(path.WarningFlags | resource.WarningFlags) : null,
            Lr2ResourceMaxRelativeCp932Bytes = chart.Kind == ChartFileKind.Bms ? resource.MaxRelativeCp932Bytes : null,
            Lr2ResourceHasParentTraversal = chart.Kind == ChartFileKind.Bms ? resource.HasParentTraversal : null
        };
    }

    private static ResourceHealthLookupContext.ResourceHealthCounts ComputeResourceHealthCounts(
        ChartFile chart,
        string chartDirectory,
        ChartResourceSnapshot resources,
        ResourceHealthLookupContext lookupContext)
    {
        string lookupDirectory = NormalizeLookupDirectory(chartDirectory);
        DirectoryResourceLookupCache.Entry resourceEntry = lookupContext?.GetResourceEntryOrNull(lookupDirectory);
        var counters = new ResourceHealthLookupCounters();
        int audioDefined = resources.AudioReferenceCount;
        int audioExisting = audioDefined > 0
            ? CountExistingResourceReferences(
                chartDirectory,
                lookupDirectory,
                resourceEntry,
                resources.AudioReferences,
                ChartResourceExtensions.AudioExtensions,
                ChartResourceKind.Audio,
                lookupContext,
                ref counters)
            : 0;
        int visualDefined = resources.VisualReferenceCount;
        int visualExisting = visualDefined > 0
            ? CountExistingResourceReferences(
                chartDirectory,
                lookupDirectory,
                resourceEntry,
                resources.VisualReferences,
                ChartResourceExtensions.ImageExtensions,
                ChartResourceKind.Image,
                lookupContext,
                ref counters)
            : 0;
        int movieDefined = resources.MovieReferenceCount;
        int movieExisting = movieDefined > 0
            ? CountExistingResourceReferences(
                chartDirectory,
                lookupDirectory,
                resourceEntry,
                resources.MovieReferences,
                ChartResourceExtensions.MovieExtensions,
                ChartResourceKind.Movie,
                lookupContext,
                ref counters)
            : 0;
        ChartResourceReference? stagefile = resources.GetOptionalImage(ChartResourceUsage.Stagefile);
        bool stagefileDefined = IsOptionalImageDefined(chart.Kind, stagefile);
        bool stagefileExisting = stagefileDefined && ExistsOptionalImageResource(
            chartDirectory,
            lookupDirectory,
            resourceEntry,
            stagefile?.LookupKey,
            lookupContext,
            ref counters);
        ChartResourceReference? backbmp = resources.GetOptionalImage(ChartResourceUsage.Backbmp);
        bool backbmpDefined = IsOptionalImageDefined(chart.Kind, backbmp);
        bool backbmpExisting = backbmpDefined && ExistsOptionalImageResource(
            chartDirectory,
            lookupDirectory,
            resourceEntry,
            backbmp?.LookupKey,
            lookupContext,
            ref counters);
        ChartResourceReference? banner = resources.GetOptionalImage(ChartResourceUsage.Banner);
        bool bannerDefined = IsOptionalImageDefined(chart.Kind, banner);
        bool bannerExisting = bannerDefined && ExistsOptionalImageResource(
            chartDirectory,
            lookupDirectory,
            resourceEntry,
            banner?.LookupKey,
            lookupContext,
            ref counters);
        counters.Flush(lookupContext);
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

    private static ResourceHealthLookupContext.ResourceHealthSetSignature BuildResourceHealthSetSignature(ChartFile chart, ChartResourceSnapshot resources)
    {
        string stagefile = IsOptionalImageDefined(chart.Kind, resources.GetOptionalImage(ChartResourceUsage.Stagefile))
            ? resources.GetOptionalImage(ChartResourceUsage.Stagefile)?.LookupKey : null;
        string backbmp = IsOptionalImageDefined(chart.Kind, resources.GetOptionalImage(ChartResourceUsage.Backbmp))
            ? resources.GetOptionalImage(ChartResourceUsage.Backbmp)?.LookupKey : null;
        string banner = IsOptionalImageDefined(chart.Kind, resources.GetOptionalImage(ChartResourceUsage.Banner))
            ? resources.GetOptionalImage(ChartResourceUsage.Banner)?.LookupKey : null;
        return new ResourceHealthLookupContext.ResourceHealthSetSignature(
            resources?.AudioRelativePaths,
            resources?.VisualRelativePaths,
            resources?.MovieRelativePaths,
            stagefile,
            backbmp,
            banner);
    }

    /// <summary>BMSの原文定義とbmsonの有効パス定義という既存の形式差を維持します。</summary>
    private static bool IsOptionalImageDefined(ChartFileKind kind, ChartResourceReference? reference)
    {
        return reference.HasValue && (kind == ChartFileKind.Bms
            ? !string.IsNullOrWhiteSpace(reference.Value.RawPath)
            : reference.Value.Status == ChartResourcePathNormalizationStatus.Valid && !string.IsNullOrWhiteSpace(reference.Value.NormalizedPath));
    }

    internal static IReadOnlyList<ChartWarning> BuildResourceHealthWarnings(
        ResourceHealthMaintenanceSnapshot snapshot)
    {
        if (snapshot == null)
        {
            return [];
        }
        List<ChartWarning> warnings = [];
        AppendHealthWarning(warnings, snapshot.GetWavHealth(), snapshot.WavFilesDefined, snapshot.WavFilesExisting, ChartWarningKind.ResourceWavMissing, Resources.Warning_WavFilesNotFound);
        AppendHealthWarning(warnings, snapshot.GetBgaHealth(), snapshot.BgaFilesDefined, snapshot.BgaFilesExisting, ChartWarningKind.ResourceBgaMissing, Resources.Warning_BgaFilesNotFound);
        AppendHealthWarning(warnings, snapshot.GetMovieHealth(), snapshot.MovieFilesDefined, snapshot.MovieFilesExisting, ChartWarningKind.ResourceMovieMissing, Resources.Warning_MovieFilesNotFound);
        AppendFlagWarning(warnings, snapshot.GetStagefileHealth(), ChartWarningKind.ResourceStagefileMissing, Resources.Warning_StagefileNotFound);
        AppendFlagWarning(warnings, snapshot.GetBackbmpHealth(), ChartWarningKind.ResourceBackbmpMissing, Resources.Warning_BackbmpNotFound);
        AppendFlagWarning(warnings, snapshot.GetBannerHealth(), ChartWarningKind.ResourceBannerMissing, Resources.Warning_BannerNotFound);
        return warnings;
    }

    private struct ResourceHealthLookupCounters
    {
        private long cacheHits;

        private long resourceIndexHits;

        private long audioFileExistsFallbacks;

        private long imageFileExistsFallbacks;

        private long movieFileExistsFallbacks;

        private long optionalImageFileExistsFallbacks;

        private long unknownFileExistsFallbacks;

        public void RecordCacheHit()
        {
            cacheHits++;
        }

        public void RecordResourceIndexHit()
        {
            resourceIndexHits++;
        }

        public void RecordFileExistsFallback(ResourceHealthFallbackKind kind)
        {
            switch (kind)
            {
                case ResourceHealthFallbackKind.Audio:
                    audioFileExistsFallbacks++;
                    break;
                case ResourceHealthFallbackKind.Image:
                    imageFileExistsFallbacks++;
                    break;
                case ResourceHealthFallbackKind.Movie:
                    movieFileExistsFallbacks++;
                    break;
                case ResourceHealthFallbackKind.OptionalImage:
                    optionalImageFileExistsFallbacks++;
                    break;
                default:
                    unknownFileExistsFallbacks++;
                    break;
            }
        }

        public void Flush(ResourceHealthLookupContext lookupContext)
        {
            if (lookupContext == null)
            {
                return;
            }
            lookupContext.AddCacheHits(cacheHits + resourceIndexHits);
            lookupContext.AddResourceIndexHits(resourceIndexHits);
            lookupContext.AddFileExistsFallbacks(ResourceHealthFallbackKind.Audio, audioFileExistsFallbacks);
            lookupContext.AddFileExistsFallbacks(ResourceHealthFallbackKind.Image, imageFileExistsFallbacks);
            lookupContext.AddFileExistsFallbacks(ResourceHealthFallbackKind.Movie, movieFileExistsFallbacks);
            lookupContext.AddFileExistsFallbacks(ResourceHealthFallbackKind.OptionalImage, optionalImageFileExistsFallbacks);
            lookupContext.AddFileExistsFallbacks(ResourceHealthFallbackKind.Unknown, unknownFileExistsFallbacks);
        }
    }

    private static int CountExistingResourceReferences(
        string chartDirectory,
        string lookupDirectory,
        DirectoryResourceLookupCache.Entry resourceEntry,
        IEnumerable<ChartResourceSnapshot.ResourceReference> references,
        IEnumerable<string> extensions,
        ChartResourceKind resourceKind,
        ResourceHealthLookupContext lookupContext,
        ref ResourceHealthLookupCounters counters)
    {
        int existingCount = 0;
        foreach (ChartResourceSnapshot.ResourceReference reference in references ?? [])
        {
            if (TryResolveResourceReferenceFromCache(resourceEntry, reference, resourceKind, out bool existsInCache))
            {
                counters.RecordResourceIndexHit();
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
                counters.RecordCacheHit();
                if (sharedExistsInCache)
                {
                    existingCount++;
                }
                continue;
            }

            counters.RecordFileExistsFallback(GetFallbackKind(resourceKind));
            if (ExistsWithCompatibleExtensions(chartDirectory, reference.LookupKey, extensions))
            {
                existingCount++;
            }
        }
        return existingCount;
    }

    private static bool ExistsOptionalImageResource(
        string chartDirectory,
        string lookupDirectory,
        DirectoryResourceLookupCache.Entry resourceEntry,
        string normalizedPath,
        ResourceHealthLookupContext lookupContext,
        ref ResourceHealthLookupCounters counters)
    {
        if (string.IsNullOrWhiteSpace(normalizedPath))
        {
            return false;
        }
        var reference = new ChartResourceSnapshot.ResourceReference(
            normalizedPath,
            ChartResourceKeyHash.GetLookupHash(normalizedPath),
            normalizedPath.IndexOf(Path.DirectorySeparatorChar) >= 0 || normalizedPath.IndexOf(Path.AltDirectorySeparatorChar) >= 0);
        if (TryResolveResourceReferenceFromCache(resourceEntry, reference, ChartResourceKind.Image, out bool existsInCache))
        {
            counters.RecordResourceIndexHit();
            return existsInCache;
        }

        if (lookupContext?.TryGetSharedResourceExists(
            lookupDirectory,
            ChartResourceKind.Image,
            reference.RelativePathHash,
            out bool sharedExistsInCache) == true)
        {
            counters.RecordCacheHit();
            return sharedExistsInCache;
        }

        counters.RecordFileExistsFallback(ResourceHealthFallbackKind.OptionalImage);
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
            string fullPath = LongPathFileSystem.NormalizePathForStorage(Path.Combine(chartDirectory, file));
            string directory = Path.GetDirectoryName(fullPath) + Path.DirectorySeparatorChar;
            string basename = Path.GetFileName(fullPath);
            return LongPathFileSystem.FileExists(fullPath)
                || (extensions ?? []).Any(ext => !string.IsNullOrWhiteSpace(ext) && LongPathFileSystem.FileExists(directory + basename + ext));
        }
        catch
        {
            return false;
        }
    }

    /// <summary>文字化けの推定状態と修正済み状態に該当する共通譜面を返します。</summary>
    public List<ChartFile> GetGarbledFiles(IEnumerable<ChartFile> charts, bool isInFixedList)
        => [.. (charts ?? []).Where(chart => chart?.Kind == ChartFileKind.Bms)
            .Where(chart => GetResourceHealthSnapshot(chart) is { } info
                && !string.IsNullOrWhiteSpace(info.Encoding) && info.Encoding != "shift_jis" && info.Encoding != "shift_jis?"
                && info.EncodingFixed == isInFixedList)];

    /// <summary>詳細のノート数がゼロの共通譜面を返します。</summary>
    public List<ChartFile> GetZeroNoteCharts(IEnumerable<ChartFile> charts, Func<ChartFile, ChartDetails> resolver = null)
        => [.. (charts ?? []).Where(chart => chart?.Kind == ChartFileKind.Bms && ResolveChartInfo(chart, resolver)?.notes == 0)];

    /// <summary>資源警告除外を変更した不変値を返します。未計算の件数を補完しません。</summary>
    public List<ChartFile> SetChartResourceWarningsIgnored(IEnumerable<ChartFile> charts, bool unset)
    {
        var changes = new List<ChartFile>();
        foreach (ChartFile chart in (charts ?? []).Where(chart => chart != null))
        {
            ResourceHealthMaintenanceSnapshot info = GetResourceHealthSnapshot(chart);
            if (chart.Kind == ChartFileKind.Bmson && info?.IsInformationChecked != true && chart.Resources != null)
            {
                ResourceHealthMaintenanceSnapshot computed = BuildResourceHealthSnapshot(chart);
                info = computed == null ? info : computed with { FilesWarningIgnored = info?.FilesWarningIgnored == true };
            }
            info ??= new ResourceHealthMaintenanceSnapshot
            {
                Path = chart.Path,
                Hash = chart.Md5,
                Encoding = chart.Kind == ChartFileKind.Bmson ? "utf-8" : chart.EncodingName,
                Origin = MaintenanceInfoOrigin.Placeholder
            };
            if (info.FilesWarningIgnored == unset)
            {
                changes.Add(ChartFileProjection.WithMaintenance(chart, info with { FilesWarningIgnored = !unset }));
            }
        }
        return changes;
    }

    /// <summary>確定した文字コードを同じ入力の生メタデータへ適用した不変値を返します。</summary>
    public MaintenanceEncodingUpdateResult ApplyEncoding(IEnumerable<ChartFile> charts, string encoding)
    {
        var result = new MaintenanceEncodingUpdateResult();
        foreach (ChartFile chart in (charts ?? []).Where(chart => chart?.Kind == ChartFileKind.Bms))
        {
            ResourceHealthMaintenanceSnapshot info = GetResourceHealthSnapshot(chart)
                ?? new ResourceHealthMaintenanceSnapshot { Path = chart.Path, Hash = chart.Md5, Origin = MaintenanceInfoOrigin.Placeholder };
            string nextEncoding = string.IsNullOrWhiteSpace(encoding)
                ? ((info.Encoding?.EndsWith("?", StringComparison.Ordinal) == true && info.Encoding != "shift_jis?") || info.Encoding == "unknown" ? "shift_jis" : info.Encoding)
                : encoding;
            if (string.IsNullOrWhiteSpace(nextEncoding))
            {
                continue;
            }

            ChartFile updated = chart;
            if (!string.IsNullOrWhiteSpace(encoding) && LongPathFileSystem.FileExists(chart.Path))
            {
                ChartFileSnapshot snapshot = ChartFileContentReader.ReadSnapshot(chart.Path);
                updated = BmsChartFileParser.ApplyMetadataEncoding(snapshot, chart, nextEncoding);

            }
            if (!HasSameMetadata(chart, updated) || !string.Equals(info.Encoding, nextEncoding, StringComparison.Ordinal))
            {
                updated = ChartFileProjection.WithMaintenance(updated, info with { Encoding = nextEncoding, EncodingFixed = true });
                if (!HasSameMetadata(chart, updated))
                {
                    result.SongsToUpsert.Add(updated);
                }
                result.MaintenanceInfosToUpsert.Add(updated.ResourceHealthMaintenanceSnapshot);
                result.ChangedCharts.Add(updated);
            }
        }
        return result;
    }

    /// <summary>文字コード適用で変更する生メタデータと合成表示値を比較し、保守投影だけの変更を除きます。</summary>
    internal static bool HasSameMetadata(ChartFile left, ChartFile right)
        => left.RawTitle == right.RawTitle && left.RawSubtitle == right.RawSubtitle && left.RawArtist == right.RawArtist
            && left.Subartist == right.Subartist && left.Genre == right.Genre
            && left.Title == right.Title && left.Subtitle == right.Subtitle && left.Artist == right.Artist;

    /// <summary>ゼロノート警告の再検査結果と変更した共通値を返します。</summary>
    public ZeroNoteRecheckResult RecheckZeroNoteWarnings(IEnumerable<ChartFile> charts,
        Action<Exception, string> logWarn = null, Func<ChartFile, ChartDetails> chartInfoResolver = null)
    {
        var result = new ZeroNoteRecheckResult();
        foreach (ChartFile chart in (charts ?? []).Where(chart => chart?.Kind == ChartFileKind.Bms))
        {
            bool hadMismatch = chart.Warnings.Any(warning => warning.Kind == ChartWarningKind.ZeroNoteMismatch);
            bool mismatch = false;
            if (ResolveChartInfo(chart, chartInfoResolver)?.notes == 0 && !string.IsNullOrWhiteSpace(chart.Path))
            {
                result.Total++;
                try { mismatch = !BmsChartFileParser.IsZeroNote(chart.Path); }
                catch (Exception exception) when (IsMaintenanceRecoverable(exception))
                {
                    result.SkippedCount++;
                    logWarn?.Invoke(exception, "zero_note_recheck skipped: path=" + chart.Path);
                }
            }
            if (mismatch)
            {
                result.MismatchCount++;
            }

            if (hadMismatch == mismatch)
            {
                continue;
            }

            if (!mismatch)
            {
                result.ClearedCount++;
            }

            var nextWarnings = chart.Warnings.Where(warning => warning.Kind != ChartWarningKind.ZeroNoteMismatch).ToList();
            if (mismatch)
            {
                nextWarnings.Add(ChartWarning.Create(ChartWarningKind.ZeroNoteMismatch, Resources.Warning_ZeroNoteMismatch));
            }

            result.ChangedCharts.Add(chart with { Warnings = nextWarnings });
            result.ChangedCount++;
        }
        return result;
    }

    private static ChartDetails ResolveChartInfo(ChartFile chart, Func<ChartFile, ChartDetails> resolver)
        => chart == null ? null : resolver == null ? chart.ChartInfo : resolver(chart);

    /// <summary>未検出または再検査対象のモードを同じ捕捉入力から返します。</summary>
    public List<ChartFile> DetectModeChanges(IEnumerable<ChartFile> charts, bool forceUpdate)
        => [.. (charts ?? []).Where(chart => chart?.Kind == ChartFileKind.Bms && (forceUpdate || !chart.Mode.HasValue)
                && LongPathFileSystem.FileExists(chart.Path))
            .Select(chart => chart with { Mode = BmsChartFileParser.ParseSnapshot(ChartFileContentReader.ReadSnapshot(chart.Path)).Mode })];

    private static void ApplyDurableWrite(Func<CatalogMaintenanceWriteRequest, CatalogMaintenanceWriteReceipt> write, CatalogMaintenanceWriteRequest request)
    {
        if (request.HasChanges && write(request)?.Applied != true)
        {
            throw new InvalidOperationException("Maintenance changes were not persisted.");
        }
    }

    /// <summary>評価を完了してから一つの不変要求を確定します。取消時も評価済み対象を従来どおり確定します。</summary>
    public MaintenanceWorkflowResult UpdateMaintenanceInfo(IEnumerable<ChartFile> charts, bool forceUpdate,
        Func<CatalogMaintenanceWriteRequest, CatalogMaintenanceWriteReceipt> durableWrite, IBmsLibraryDialogService dialogService,
        ResourceHealthLookupContext resourceLookupContext = null, Action<string> progressLogger = null,
        Action<MaintenanceWorkflowProgress> progressReporter = null, CancellationToken cancellationToken = default,
        Action durableCommitCompleted = null, Action<Action> postCommitEffectsSink = null)
    {
        if (durableWrite == null)
        {
            return new MaintenanceWorkflowResult();
        }

        var pending = new MaintenanceWriteAccumulator();
        MaintenanceWorkflowProgress terminalProgress = null;
        MaintenanceWorkflowResult result = UpdateMaintenanceInfoSnapshotPipeline(charts, forceUpdate, pending.Capture, dialogService,
            resourceLookupContext, progressLogger, progress =>
            {
                if (progress?.IsCompleted == true || progress?.IsCanceled == true)
                {
                    terminalProgress = progress;
                }
                else
                {
                    progressReporter?.Invoke(progress);
                }
            }, cancellationToken);
        ApplyDurableWrite(durableWrite, pending.CreateRequest());
        durableCommitCompleted?.Invoke();
        result.ChangedCharts.AddRange(pending.ChangedCharts);
        Action effects = () => { if (terminalProgress != null) { progressReporter?.Invoke(terminalProgress); } };
        if (postCommitEffectsSink == null)
        {
            effects();
        }
        else
        {
            postCommitEffectsSink(effects);
        }

        return result;
    }

    private sealed class MaintenanceWriteAccumulator
    {
        private readonly List<ResourceHealthMaintenanceSnapshot> maintenanceInfos = [];
        private readonly List<ChartFile> songs = [];
        internal List<ChartFile> ChangedCharts { get; } = [];
        internal CatalogMaintenanceWriteReceipt Capture(CatalogMaintenanceWriteRequest request)
        {
            maintenanceInfos.AddRange(request.MaintenanceInfos);
            songs.AddRange(request.Songs);
            ChangedCharts.AddRange(request.CurrentValues);
            return new CatalogMaintenanceWriteReceipt(true, request.MaintenanceInfos.Count,
                request.Songs.Count(chart => chart.Kind == ChartFileKind.Bms), request.Songs.Count(chart => chart.Kind == ChartFileKind.Bmson), 0);
        }
        internal CatalogMaintenanceWriteRequest CreateRequest() => new(maintenanceInfos, songs, currentValues: ChangedCharts);
    }

    private MaintenanceWorkflowResult UpdateMaintenanceInfoSnapshotPipeline(
        IEnumerable<ChartFile> charts,
        bool forceUpdate,
        Func<CatalogMaintenanceWriteRequest, CatalogMaintenanceWriteReceipt> durableWrite,
        IBmsLibraryDialogService dialogService,
        ResourceHealthLookupContext resourceLookupContext = null,
        Action<string> progressLogger = null,
        Action<MaintenanceWorkflowProgress> progressReporter = null,
        CancellationToken cancellationToken = default)
    {
        var result = new MaintenanceWorkflowResult();
        if (charts == null || durableWrite == null)
        {
            return result;
        }

        var stopwatch = Stopwatch.StartNew();
        List<MaintenanceWorkflowTarget> targets = CreateSnapshotPipelineTargets(charts, forceUpdate);
        result.CheckedFileCount = targets.Count;
        result.BmsResourceTargetCount = targets.Count(target => target.Kind == ChartFileKind.Bms && (forceUpdate || GetResourceHealthSnapshot(target.Chart)?.IsInformationChecked != true));
        result.BmsonResourceTargetCount = targets.Count(target => target.Kind == ChartFileKind.Bmson);
        result.HealthTargetCount = result.BmsResourceTargetCount + result.BmsonResourceTargetCount;
        result.HealthDegree = maintenanceHealthDegree;
        result.ReaderDegree = ChartFileReadPipelinePolicy.ResolveReaderDegree(Environment.ProcessorCount, targets.Count);
        result.ForceTargetCount = forceUpdate ? targets.Count : 0;
        result.MissingInfoTargetCount = targets.Count(target => target.Chart.ResourceHealthMaintenanceSnapshot?.IsInformationChecked != true);
        result.MissingEncodingTargetCount = targets.Count(target => target.Kind == ChartFileKind.Bms && string.IsNullOrWhiteSpace(target.Chart.EncodingName));
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
            + " bmsonMissingFreshRefs=" + targets.Count(target => target.Kind == ChartFileKind.Bmson && target.Chart.Resources == null)
            + " healthDegree=" + maintenanceHealthDegree
            + " readerDegree=" + result.ReaderDegree
            + " readQueueCapacity=" + readQueueCapacity
            + " computedQueueCapacity=" + computedQueueCapacity);
        if (targets.Count == 0)
        {
            stopwatch.Stop();
            ApplyLookupCounters(result, resourceLookupContext);
            result.TotalMs = stopwatch.ElapsedMilliseconds;
            progressLogger?.Invoke("maintenance_update no_targets sourceCount=0 healthDegree=" + maintenanceHealthDegree + " elapsedMs=" + result.TotalMs);
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
            var changedMaintenanceInfos = new List<ResourceHealthMaintenanceSnapshot>();
            var reloadedFiles = new List<ChartFile>();
            var currentValues = new List<ChartFile>();
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
                if (itemResult.MaintenanceInfoChanged && itemResult.MaintenanceInfo?.IsInformationChecked == true)
                {
                    changedMaintenanceInfos.Add(itemResult.MaintenanceInfo);
                }
                else if (itemResult.MaintenanceInfo?.IsInformationChecked == true)
                {
                    unchangedInChunk++;
                }
                if (itemResult.ReloadedBmsChart != null)
                {
                    reloadedFiles.Add(itemResult.ReloadedBmsChart);
                }
                if (itemResult.CurrentChart != null && (itemResult.MaintenanceInfoChanged || itemResult.ReloadedBmsChart != null))
                {
                    currentValues.Add(itemResult.CurrentChart);
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
                ApplyDurableWrite(durableWrite, new CatalogMaintenanceWriteRequest(changedMaintenanceInfos, reloadedFiles, currentValues: currentValues));
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
                        itemResult = EvaluateMaintenanceTarget(candidate.Target, snapshot, itemLookupContext, forceUpdate);
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

        var writerTask = Task.Run(delegate
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

    private static List<MaintenanceWorkflowTarget> CreateSnapshotPipelineTargets(IEnumerable<ChartFile> charts, bool forceUpdate)
        => [.. (charts ?? []).Where(chart => chart != null && !string.IsNullOrWhiteSpace(chart.Path))
            .Where(chart => forceUpdate || GetResourceHealthSnapshot(chart)?.IsInformationChecked != true
                || (chart.Kind == ChartFileKind.Bms && string.IsNullOrWhiteSpace(chart.EncodingName)))
            .Select(chart => new MaintenanceWorkflowTarget(chart))];

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

    private static MaintenanceEvaluationResult EvaluateMaintenanceTarget(MaintenanceWorkflowTarget target,
        ChartFileSnapshot snapshot, ResourceHealthLookupContext lookupContext, bool forceUpdate)
        => EvaluateMaintenance(target.Chart, snapshot, lookupContext, forceUpdate);

    /// <summary>基本解析で捕捉した同じ入力と共通値を使って、導入・走査用の短命な保守結果を返します。</summary>
    internal static MaintenanceEvaluationResult EvaluateMaintenanceForInline(ChartFile chart, ChartFileSnapshot snapshot,
        ResourceHealthLookupContext lookupContext, bool forceUpdate = false, BmsEncodingDetectionResult detectedEncoding = null)
        => EvaluateMaintenance(chart, snapshot, lookupContext ?? new ResourceHealthLookupContext(null), forceUpdate, detectedEncoding, reuseCapturedResources: true);

    private static MaintenanceEvaluationResult EvaluateMaintenance(ChartFile chart, ChartFileSnapshot snapshot,
        ResourceHealthLookupContext lookup, bool forceUpdate, BmsEncodingDetectionResult detectedEncoding = null, bool reuseCapturedResources = false)
    {
        var result = new MaintenanceEvaluationResult();
        ResourceHealthMaintenanceSnapshot before = GetResourceHealthSnapshot(chart);
        ChartFile parsed = chart;
        long refreshStart = Stopwatch.GetTimestamp();
        bool needsHealth = forceUpdate || before?.IsInformationChecked != true;
        if (needsHealth && snapshot != null && ((forceUpdate && !reuseCapturedResources) || chart.Resources == null
            || (chart.Kind == ChartFileKind.Bmson && chart.LastWriteTimeUtc != snapshot.LastWriteTimeUtc)))
        {
            ChartFile resources;
            try
            {
                resources = chart.Kind == ChartFileKind.Bmson ? BmsonChartFileParser.ParseSnapshot(snapshot) : BmsChartFileParser.ParseSnapshot(snapshot);
            }
            catch when (chart.Kind == ChartFileKind.Bmson)
            {
                // bmson の資源再取得失敗は対象単位で分類し、古い検査結果を保存せず他の対象を継続します。
                result.BmsonRefreshResult = BmsonResourceRefreshResult.Failed;
                result.BmsonRefreshElapsedTicks = Stopwatch.GetTimestamp() - refreshStart;
                return result;
            }
            // 保守の資源読取りは内容識別を更新しません。基本ハッシュの収束は差分走査・詳細の入口が担当します。
            parsed = chart with
            {
                Resources = resources.Resources,
                Stagefile = chart.Kind == ChartFileKind.Bmson ? resources.Stagefile : chart.Stagefile,
                Banner = chart.Kind == ChartFileKind.Bmson ? resources.Banner : chart.Banner,
                Backbmp = chart.Kind == ChartFileKind.Bmson ? resources.Backbmp : chart.Backbmp,
                PreviewMusic = chart.Kind == ChartFileKind.Bmson ? resources.PreviewMusic : chart.PreviewMusic
            };
            if (chart.Kind == ChartFileKind.Bmson)
            {
                result.BmsonRefreshResult = BmsonResourceRefreshResult.Success;
            }
        }
        else if (chart.Kind == ChartFileKind.Bmson)
        {
            result.BmsonRefreshResult = BmsonResourceRefreshResult.Reused;
        }

        result.BmsonRefreshElapsedTicks = chart.Kind == ChartFileKind.Bmson ? Stopwatch.GetTimestamp() - refreshStart : 0;
        long healthStart = Stopwatch.GetTimestamp();
        ResourceHealthMaintenanceSnapshot after = needsHealth ? BuildResourceHealthSnapshot(parsed, lookup, forceUpdate) : before with { Origin = MaintenanceInfoOrigin.Calculated };
        result.HealthElapsedTicks = Stopwatch.GetTimestamp() - healthStart;
        if (after == null)
        {
            return result;
        }

        after = after with
        {
            FilesWarningIgnored = !forceUpdate && before?.FilesWarningIgnored == true,
            Encoding = chart.Kind == ChartFileKind.Bms ? before?.Encoding : "utf-8",
            EncodingFixed = before?.EncodingFixed == true
        };
        if (chart.Kind == ChartFileKind.Bms && (forceUpdate || string.IsNullOrWhiteSpace(after.Encoding)))
        {
            long encodingStart = Stopwatch.GetTimestamp();
            ChartFileSnapshot input = snapshot ?? ChartFileContentReader.ReadSnapshot(chart.Path);
            BmsEncodingDetectionResult detection = detectedEncoding ?? BmsEncodingDetector.Detect(input.Bytes);
            result.EncodingDetectionResult = detection;
            ChartFile detected = BmsChartFileParser.ApplyEncodingDetection(input, parsed, detection);
            bool reloaded = !string.IsNullOrWhiteSpace(detection.EncodingName) && !detection.EncodingName.EndsWith("?", StringComparison.Ordinal)
                && detection.EncodingName != "unknown" && System.Text.Encoding.GetEncoding(detection.EncodingName).CodePage != 932;
            if (reloaded)
            {
                parsed = parsed with
                {
                    Title = detected.Title,
                    Subtitle = detected.Subtitle,
                    Artist = detected.Artist,
                    RawTitle = detected.RawTitle,
                    RawSubtitle = detected.RawSubtitle,
                    RawArtist = detected.RawArtist,
                    Subartist = detected.Subartist,
                    Genre = detected.Genre
                };
                result.EncodingReloadCount = 1;
                result.ReloadedBmsChart = parsed;
            }
            after = after with { Encoding = detection.EncodingName, EncodingFixed = reloaded || after.EncodingFixed };
            result.EncodingElapsedTicks = Stopwatch.GetTimestamp() - encodingStart;
            result.EncodingReloadElapsedTicks = reloaded ? result.EncodingElapsedTicks : 0;
        }
        result.MaintenanceInfo = after;
        result.MaintenanceInfoChanged = !Equals(before is null ? null : before with { Origin = MaintenanceInfoOrigin.Calculated }, after);
        result.CurrentChart = ChartFileProjection.WithMaintenance(parsed with { Resources = null }, after);
        result.CacheHitCount = lookup?.CacheHitCount ?? 0;
        result.ResourceIndexHitCount = lookup?.ResourceIndexHitCount ?? 0;
        result.ResourceHealthSetCacheHitCount = lookup?.ResourceHealthSetCacheHitCount ?? 0;
        result.FileExistsFallbackCount = lookup?.FileExistsFallbackCount ?? 0;
        return result;
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

    private static string SanitizeLogValue(string value) => (value ?? string.Empty).Replace('\r', ' ').Replace('\n', ' ');

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

    internal enum BmsonResourceRefreshResult
    {
        NotApplicable,
        Reused,
        Success,
        Failed
    }

    private sealed class MaintenanceWorkflowTarget(ChartFile chart)
    {
        internal ChartFile Chart { get; } = chart;
        internal ChartFileKind Kind => Chart.Kind;
        internal string Path => Chart.Path;
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
        public ResourceHealthMaintenanceSnapshot MaintenanceInfo { get; set; }

        public ChartFile CurrentChart { get; set; }

        public bool MaintenanceInfoChanged { get; set; }

        public ChartFile ReloadedBmsChart { get; set; }

        public long HealthElapsedTicks { get; set; }

        public long EncodingElapsedTicks { get; set; }

        public long EncodingReloadElapsedTicks { get; set; }

        public long BmsonRefreshElapsedTicks { get; set; }

        public BmsEncodingDetectionResult EncodingDetectionResult { get; set; }

        public int EncodingReloadCount { get; set; }

        public long CacheHitCount { get; set; }

        public long ResourceIndexHitCount { get; set; }

        public long ResourceHealthSetCacheHitCount { get; set; }

        public long FileExistsFallbackCount { get; set; }

        public BmsonResourceRefreshResult BmsonRefreshResult { get; set; } = BmsonResourceRefreshResult.NotApplicable;
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
