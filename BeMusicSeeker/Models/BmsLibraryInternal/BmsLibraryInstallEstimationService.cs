using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading;
using BeMusicSeeker.Models.Utils;
using BeMusicSeeker.Models.LR2;

namespace BeMusicSeeker.Models.BmsLibraryInternal;

/// <summary>
/// Calculates installation destinations against snapshots owned by BMSLibrary.
/// The facade must acquire the required locks before invoking this service.
/// </summary>
internal sealed class BmsLibraryInstallEstimationService
{
    internal static int ResolveDefaultCandidateEvaluationDegree()
    {
        return Math.Max(1, Environment.ProcessorCount - 1);
    }

    internal static int NormalizeCandidateEvaluationDegree(int candidateEvaluationDegree)
    {
        return Math.Max(1, candidateEvaluationDegree);
    }

    internal static int ResolveCandidateEvaluationDegree(bool asParallel)
    {
        return asParallel ? ResolveDefaultCandidateEvaluationDegree() : 1;
    }

    private enum MetadataEvidenceStrength
    {
        None,
        Weak,
        Strong
    }

    internal sealed class SourceBaselineEvaluation
    {
        public int PrimaryHealth { get; set; }

        public bool IsViableDestination { get; set; }

        public string Summary { get; set; } = string.Empty;
    }

    private sealed class MetadataValidationResult
    {
        public bool IsApplicable { get; set; }

        public bool PairExactMatch { get; set; }

        public bool ArtistExactMatch { get; set; }

        public InstallEstimationMetadataTitleMatchKind TitleMatchKind { get; set; } = InstallEstimationMetadataTitleMatchKind.None;

        public MetadataEvidenceStrength Evidence { get; set; } = MetadataEvidenceStrength.None;

        public string Summary { get; set; } = string.Empty;
    }

    private readonly struct RatioMetric
    {
        internal RatioMetric(int state, long numerator, long denominator)
        {
            State = state;
            Numerator = numerator;
            Denominator = denominator;
        }

        internal int State { get; }

        internal long Numerator { get; }

        internal long Denominator { get; }
    }

    private sealed class CandidateEvaluation
    {
        public const int SelfOwnedMatchedTotalUncomputed = -1;

        public string DirectoryPath { get; set; }

        public bool IsSourceCandidate { get; set; }

        public int AudioMatched { get; set; }

        public int AudioExactMatched { get; set; }

        public int AudioDefined { get; set; }

        public int VisualMatched { get; set; }

        public int VisualExactMatched { get; set; }

        public int VisualDefined { get; set; }

        public int MovieMatched { get; set; }

        public int MovieExactMatched { get; set; }

        public int MovieDefined { get; set; }

        public int OptionalImageMatched { get; set; }

        public int OptionalImageExactMatched { get; set; }

        public int OptionalImageDefined { get; set; }

        public int AudioCandidateCount { get; set; }

        public int VisualCandidateCount { get; set; }

        public int MovieCandidateCount { get; set; }

        public int OptionalImageCandidateCount { get; set; }

        public int AudioFileCount { get; set; }

        public int SelfOwnedMatchedTotal { get; set; } = SelfOwnedMatchedTotalUncomputed;

        public int AudioHealth => ComputeHealth(AudioMatched, AudioDefined);

        public int VisualHealth => ComputeHealth(VisualMatched, VisualDefined);

        public int MovieHealth => ComputeHealth(MovieMatched, MovieDefined);

        public int OptionalImageHealth => ComputeHealth(OptionalImageMatched, OptionalImageDefined);

        public int AudioPrecision => ComputePrecision(AudioMatched, AudioDefined, AudioCandidateCount);

        public int VisualPrecision => ComputePrecision(VisualMatched, VisualDefined, VisualCandidateCount);

        public int MoviePrecision => ComputePrecision(MovieMatched, MovieDefined, MovieCandidateCount);

        public int OptionalImagePrecision => ComputePrecision(OptionalImageMatched, OptionalImageDefined, OptionalImageCandidateCount);

        public int AudioJaccard => ComputeJaccard(AudioMatched, AudioDefined, AudioCandidateCount);

        public int VisualJaccard => ComputeJaccard(VisualMatched, VisualDefined, VisualCandidateCount);

        public int MovieJaccard => ComputeJaccard(MovieMatched, MovieDefined, MovieCandidateCount);

        public int OptionalImageJaccard => ComputeJaccard(OptionalImageMatched, OptionalImageDefined, OptionalImageCandidateCount);

        public string ToSummary()
        {
            return string.Format(
                "dir={0} audio={1}/{2} exact={3} candidate={4} precision={5} jaccard={6} visual={7}/{8} exact={9} candidate={10} precision={11} jaccard={12} movie={13}/{14} exact={15} candidate={16} precision={17} jaccard={18} optional={19}/{20} exact={21} candidate={22} precision={23} jaccard={24} audioHealth={25} visualHealth={26} movieHealth={27} optionalHealth={28}",
                DirectoryPath ?? string.Empty,
                AudioMatched,
                AudioDefined,
                AudioExactMatched,
                AudioCandidateCount,
                AudioPrecision,
                AudioJaccard,
                VisualMatched,
                VisualDefined,
                VisualExactMatched,
                VisualCandidateCount,
                VisualPrecision,
                VisualJaccard,
                MovieMatched,
                MovieDefined,
                MovieExactMatched,
                MovieCandidateCount,
                MoviePrecision,
                MovieJaccard,
                OptionalImageMatched,
                OptionalImageDefined,
                OptionalImageExactMatched,
                OptionalImageCandidateCount,
                OptionalImagePrecision,
                OptionalImageJaccard,
                AudioHealth,
                VisualHealth,
                MovieHealth,
                OptionalImageHealth)
                + " selfOwnedMatched=" + ((SelfOwnedMatchedTotal == SelfOwnedMatchedTotalUncomputed) ? "na" : SelfOwnedMatchedTotal.ToString());
        }

        public bool HasSamePrimaryMetrics(CandidateEvaluation other)
        {
            return other != null
                && AudioHealth == other.AudioHealth
                && AudioMatched == other.AudioMatched
                && VisualHealth == other.VisualHealth
                && VisualMatched == other.VisualMatched
                && MovieHealth == other.MovieHealth
                && MovieMatched == other.MovieMatched
                && OptionalImageHealth == other.OptionalImageHealth
                && OptionalImageMatched == other.OptionalImageMatched;
        }

        public bool HasSameRankingMetrics(CandidateEvaluation other)
        {
            return HasSamePrimaryMetrics(other)
                && other != null
                && CompareRatioMetric(BuildJaccardMetric(AudioMatched, AudioDefined, AudioCandidateCount), BuildJaccardMetric(other.AudioMatched, other.AudioDefined, other.AudioCandidateCount)) == 0
                && CompareRatioMetric(BuildPrecisionMetric(AudioMatched, AudioDefined, AudioCandidateCount), BuildPrecisionMetric(other.AudioMatched, other.AudioDefined, other.AudioCandidateCount)) == 0
                && CompareRatioMetric(BuildJaccardMetric(VisualMatched, VisualDefined, VisualCandidateCount), BuildJaccardMetric(other.VisualMatched, other.VisualDefined, other.VisualCandidateCount)) == 0
                && CompareRatioMetric(BuildPrecisionMetric(VisualMatched, VisualDefined, VisualCandidateCount), BuildPrecisionMetric(other.VisualMatched, other.VisualDefined, other.VisualCandidateCount)) == 0
                && CompareRatioMetric(BuildJaccardMetric(MovieMatched, MovieDefined, MovieCandidateCount), BuildJaccardMetric(other.MovieMatched, other.MovieDefined, other.MovieCandidateCount)) == 0
                && CompareRatioMetric(BuildPrecisionMetric(MovieMatched, MovieDefined, MovieCandidateCount), BuildPrecisionMetric(other.MovieMatched, other.MovieDefined, other.MovieCandidateCount)) == 0
                && CompareRatioMetric(BuildJaccardMetric(OptionalImageMatched, OptionalImageDefined, OptionalImageCandidateCount), BuildJaccardMetric(other.OptionalImageMatched, other.OptionalImageDefined, other.OptionalImageCandidateCount)) == 0
                && CompareRatioMetric(BuildPrecisionMetric(OptionalImageMatched, OptionalImageDefined, OptionalImageCandidateCount), BuildPrecisionMetric(other.OptionalImageMatched, other.OptionalImageDefined, other.OptionalImageCandidateCount)) == 0;
        }

        private static int ComputeHealth(int matched, int defined)
        {
            if (defined <= 0)
            {
                return 100;
            }
            return (int)Math.Round(100.0 * matched / defined, MidpointRounding.AwayFromZero);
        }

        private static int ComputePrecision(int matched, int defined, int candidateCount)
        {
            if (defined <= 0)
            {
                return 100;
            }
            if (candidateCount <= 0)
            {
                return 0;
            }
            return (int)Math.Round(100.0 * matched / candidateCount, MidpointRounding.AwayFromZero);
        }

        private static int ComputeJaccard(int matched, int defined, int candidateCount)
        {
            if (defined <= 0)
            {
                return 100;
            }
            if (candidateCount <= 0)
            {
                return 0;
            }
            int unionCount = defined + candidateCount - matched;
            if (unionCount <= 0)
            {
                return 100;
            }
            return (int)Math.Round(100.0 * matched / unionCount, MidpointRounding.AwayFromZero);
        }
    }

    private sealed class CandidateResourceView
    {
        private static readonly ISet<uint> EmptyHashes = new HashSet<uint>();

        public static CandidateResourceView Empty { get; } = new CandidateResourceView();

        public ISet<uint> AllBaseNameHashes { get; set; } = EmptyHashes;

        public ISet<uint> AudioBaseNameHashes { get; set; } = EmptyHashes;

        public ISet<uint> VisualBaseNameHashes { get; set; } = EmptyHashes;

        public ISet<uint> MovieBaseNameHashes { get; set; } = EmptyHashes;

        public ISet<uint> AudioRelativePathHashes { get; set; } = EmptyHashes;

        public ISet<uint> VisualRelativePathHashes { get; set; } = EmptyHashes;

        public ISet<uint> MovieRelativePathHashes { get; set; } = EmptyHashes;

        public ISet<uint> SelfOwnedAllBaseNameHashes { get; set; } = EmptyHashes;

        public ISet<uint> SelfOwnedAudioBaseNameHashes { get; set; } = EmptyHashes;

        public ISet<uint> SelfOwnedVisualBaseNameHashes { get; set; } = EmptyHashes;

        public ISet<uint> SelfOwnedMovieBaseNameHashes { get; set; } = EmptyHashes;

        public ISet<uint> SelfOwnedAudioRelativePathHashes { get; set; } = EmptyHashes;

        public ISet<uint> SelfOwnedVisualRelativePathHashes { get; set; } = EmptyHashes;

        public ISet<uint> SelfOwnedMovieRelativePathHashes { get; set; } = EmptyHashes;

        public int AudioResourceCount { get; set; }

        public int VisualResourceCount { get; set; }

        public int MovieResourceCount { get; set; }

        public int TotalResourceCount { get; set; }

        public int SelfOwnedAudioResourceCount { get; set; }

        public int SelfOwnedVisualResourceCount { get; set; }

        public int SelfOwnedMovieResourceCount { get; set; }

        public int SelfOwnedTotalResourceCount { get; set; }
    }

    private sealed class CandidateHierarchyInfo
    {
        private static readonly IReadOnlyList<string> EmptyDescendants = Array.Empty<string>();

        public static CandidateHierarchyInfo Empty { get; } = new CandidateHierarchyInfo();

        public Dictionary<string, List<string>> DescendantsByAncestor { get; } = new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase);

        public HashSet<string> HierarchyDirectories { get; } = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        public bool HasHierarchy => DescendantsByAncestor.Count > 0;

        public int HierarchyCandidateDirectoryCount => HierarchyDirectories.Count;

        public IReadOnlyList<string> GetDescendants(string directoryPath)
        {
            return !string.IsNullOrWhiteSpace(directoryPath) && DescendantsByAncestor.TryGetValue(directoryPath, out List<string> descendants)
                ? descendants
                : EmptyDescendants;
        }
    }

    private readonly struct CandidateResourceSource
    {
        internal CandidateResourceSource(DirectoryResourceLookupCache.Entry cacheEntry, DirectoryRelativePathHashIndex.Entry relativeEntry, uint[] fallbackBaseNameHashes)
        {
            CacheEntry = cacheEntry;
            RelativeEntry = relativeEntry;
            FallbackBaseNameHashes = fallbackBaseNameHashes;
        }

        internal DirectoryResourceLookupCache.Entry CacheEntry { get; }

        internal DirectoryRelativePathHashIndex.Entry RelativeEntry { get; }

        internal uint[] FallbackBaseNameHashes { get; }
    }

    private sealed class EvaluationDiagnostics
    {
        private long candidateViewBuildMs;

        private long candidateMatchMs;

        private int candidateViewBuildCount;

        private int candidateViewFallbackCount;

        public long CandidateViewBuildMs => Interlocked.Read(ref candidateViewBuildMs);

        public long CandidateMatchMs => Interlocked.Read(ref candidateMatchMs);

        public int CandidateViewBuildCount => candidateViewBuildCount;

        public int CandidateViewFallbackCount => candidateViewFallbackCount;

        public void RecordCandidateViewBuild(long elapsedMs, bool usedFallback)
        {
            Interlocked.Add(ref candidateViewBuildMs, elapsedMs);
            Interlocked.Increment(ref candidateViewBuildCount);
            if (usedFallback)
            {
                Interlocked.Increment(ref candidateViewFallbackCount);
            }
        }

        public void RecordCandidateMatch(long elapsedMs)
        {
            Interlocked.Add(ref candidateMatchMs, elapsedMs);
        }
    }

    private readonly BmsLibraryOptionsSnapshot options;

    private readonly int innerWavHealthThreshold;

    public BmsLibraryInstallEstimationService(BmsLibraryOptionsSnapshot options, int innerWavHealthThreshold)
    {
        this.options = options ?? throw new ArgumentNullException(nameof(options));
        this.innerWavHealthThreshold = innerWavHealthThreshold;
    }

    public InstalledChartDirectoryIndexSnapshot BuildInstalledHashToDirectoryMap(IEnumerable<BMSFile> installedFiles, IEnumerable<LR2SongDBExtended.bmson_song> installedBmsonSongs = null)
    {
        InstalledChartDirectoryIndexSnapshot result = new InstalledChartDirectoryIndexSnapshot();
        Dictionary<string, HashSet<string>> md5Map = new Dictionary<string, HashSet<string>>(StringComparer.OrdinalIgnoreCase);
        Dictionary<string, HashSet<string>> sha256Map = new Dictionary<string, HashSet<string>>(StringComparer.OrdinalIgnoreCase);
        foreach (BMSFile item in installedFiles ?? Enumerable.Empty<BMSFile>())
        {
            if (item == null)
            {
                continue;
            }
            RegisterInstalledDirectory(md5Map, sha256Map, result.KnownChartDirectories, item.hash, item.sha256, item.path);
        }
        foreach (LR2SongDBExtended.bmson_song item2 in installedBmsonSongs ?? Enumerable.Empty<LR2SongDBExtended.bmson_song>())
        {
            if (item2 == null)
            {
                continue;
            }
            RegisterInstalledDirectory(md5Map, sha256Map, result.KnownChartDirectories, item2.md5, item2.sha256, item2.path);
        }
        foreach (KeyValuePair<string, HashSet<string>> item3 in md5Map)
        {
            result.Md5Directories[item3.Key] = item3.Value.OrderBy((string dir) => dir, StringComparer.OrdinalIgnoreCase).ToList();
        }
        foreach (KeyValuePair<string, HashSet<string>> item4 in sha256Map)
        {
            result.Sha256Directories[item4.Key] = item4.Value.OrderBy((string dir) => dir, StringComparer.OrdinalIgnoreCase).ToList();
        }
        return result;
    }

    public InstalledDirectoryLookupResult TryGetInstalledDirectoryByHash(IEnumerable<BMSFile> installedFiles, string hash)
    {
        InstalledDirectoryLookupResult result = new InstalledDirectoryLookupResult();
        if (!IsBmsHashAvailable(hash))
        {
            result.Reason = InstalledDirectoryResolveReason.InvalidInput;
            return result;
        }
        string installDirectory = (installedFiles ?? Enumerable.Empty<BMSFile>())
            .Where((BMSFile file) => file != null && IsBmsHashAvailable(file.hash) && file.hash.Equals(hash, StringComparison.OrdinalIgnoreCase) && !string.IsNullOrWhiteSpace(file.path))
            .Select((BMSFile file) => DirectoryExt.GetDirectoryNameSimple(file.path))
            .Where((string dir) => !string.IsNullOrWhiteSpace(dir) && Directory.Exists(dir))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy((string dir) => dir, StringComparer.OrdinalIgnoreCase)
            .FirstOrDefault();
        if (string.IsNullOrWhiteSpace(installDirectory))
        {
            result.Reason = InstalledDirectoryResolveReason.NoInstalledDirectoryMatch;
            return result;
        }
        result.InstallDirectory = installDirectory;
        return result;
    }

    public InstalledOnlyPackageResolutionResult TryPrepareInstalledOnlyPackageDestination(BMSPackage package, InstalledChartDirectoryIndexSnapshot installedDirectoryIndexSnapshot)
    {
        InstalledOnlyPackageResolutionResult result = new InstalledOnlyPackageResolutionResult();
        if (package == null)
        {
            result.Reason = InstalledDirectoryResolveReason.MissingInstallDestination;
            return result;
        }
        List<BMSFile> packageFiles = (package.BMSFiles ?? new List<BMSFile>()).Where((BMSFile file) => file != null).ToList();
        if (packageFiles.Count == 0 || installedDirectoryIndexSnapshot == null || installedDirectoryIndexSnapshot.HashCount == 0)
        {
            result.Reason = InstalledDirectoryResolveReason.MissingInstallDestination;
            return result;
        }
        HashSet<string> distinctDirectories = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (BMSFile item in packageFiles)
        {
            List<string> directoriesByHash = GetDistinctInstalledDirectoriesByHash(installedDirectoryIndexSnapshot, item);
            if (directoriesByHash.Count == 0)
            {
                result.Reason = InstalledDirectoryResolveReason.MissingInstallDestination;
                return result;
            }
            if (directoriesByHash.Count > 1)
            {
                result.Reason = InstalledDirectoryResolveReason.ChartHasMultipleInstalledDirectories;
                return result;
            }
            distinctDirectories.Add(directoriesByHash[0]);
            if (distinctDirectories.Count > 1)
            {
                result.Reason = InstalledDirectoryResolveReason.PackageHasSplitInstalledDirectories;
                return result;
            }
        }
        result.DestinationDirectory = distinctDirectories.FirstOrDefault();
        result.Reason = string.IsNullOrWhiteSpace(result.DestinationDirectory) ? InstalledDirectoryResolveReason.MissingInstallDestination : InstalledDirectoryResolveReason.None;
        return result;
    }

    public InstalledDirectoryLookupResult TryResolveInstalledDestinationFromPackage(BMSPackage package, List<BMSFile> missingFiles, InstalledChartDirectoryIndexSnapshot installedDirectoryIndexSnapshot, BMSDirectoryFileNameHash folderAllFileList)
    {
        InstalledDirectoryLookupResult result = new InstalledDirectoryLookupResult();
        if (package == null || missingFiles == null || missingFiles.Count == 0)
        {
            result.Reason = InstalledDirectoryResolveReason.InvalidInput;
            return result;
        }
        if (installedDirectoryIndexSnapshot == null || installedDirectoryIndexSnapshot.HashCount == 0)
        {
            result.Reason = InstalledDirectoryResolveReason.InstalledIndexEmpty;
            return result;
        }
        Dictionary<string, int> directoryScores = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        foreach (BMSFile item in package.BMSFiles ?? new List<BMSFile>())
        {
            List<string> directories = GetDistinctInstalledDirectoriesByHash(installedDirectoryIndexSnapshot, item);
            if (item == null || directories.Count == 0)
            {
                continue;
            }
            result.MatchedHashCount++;
            foreach (string directoryPath in directories)
            {
                if (!directoryScores.ContainsKey(directoryPath))
                {
                    directoryScores[directoryPath] = 0;
                }
                directoryScores[directoryPath]++;
            }
        }
        result.CandidateDirectoryCount = directoryScores.Count;
        if (directoryScores.Count == 0)
        {
            result.Reason = InstalledDirectoryResolveReason.NoInstalledDirectoryMatch;
            return result;
        }
        int maxMatchCount = directoryScores.Values.Max();
        List<string> topCandidateDirs = directoryScores.Where((KeyValuePair<string, int> kv) => kv.Value == maxMatchCount).Select((KeyValuePair<string, int> kv) => kv.Key).ToList();
        if (topCandidateDirs.Count == 1)
        {
            result.InstallDirectory = topCandidateDirs[0];
            return result;
        }
        BMSFile representativeFile = missingFiles.Where((BMSFile file) => PendingChartEntry.IsBmsChartFile(file)).OrderByDescending(GetDefinedResourceCount).FirstOrDefault();
        if (representativeFile == null)
        {
            result.Reason = InstalledDirectoryResolveReason.MissingRepresentative;
            return result;
        }
        List<BMSFileMaintenanceInfo> candidateInfos = new List<BMSFileMaintenanceInfo>();
        foreach (string topCandidateDir in topCandidateDirs)
        {
            BMSFileMaintenanceInfo candidateInfo = new BMSFileMaintenanceInfo(representativeFile)
            {
                path = Path.Combine(topCandidateDir, Path.GetFileName(representativeFile.path))
            };
            representativeFile.SetHealthStatus(folderAllFileList, forceUpdate: false, memClear: false, candidateInfo, topCandidateDir, null);
            if (candidateInfo.GetWAVHealth() > innerWavHealthThreshold)
            {
                candidateInfos.Add(candidateInfo);
            }
        }
        if (candidateInfos.Count == 0)
        {
            result.Reason = InstalledDirectoryResolveReason.TieHealthBelowThreshold;
            return result;
        }
        List<BMSFileMaintenanceInfo> orderedCandidates = candidateInfos.OrderByDescending((BMSFileMaintenanceInfo m) => m.GetWAVHealth())
            .ThenByDescending((BMSFileMaintenanceInfo m) => m.GetBGAHealth())
            .ThenByDescending((BMSFileMaintenanceInfo m) => m.GetMovieHealth())
            .ThenByDescending((BMSFileMaintenanceInfo m) => m.GetOptIMGHealth())
            .ToList();
        int? wavHealth = orderedCandidates[0].GetWAVHealth();
        int? bgaHealth = orderedCandidates[0].GetBGAHealth();
        int? movieHealth = orderedCandidates[0].GetMovieHealth();
        bool? optImgHealth = orderedCandidates[0].GetOptIMGHealth();
        string bestDirectory = orderedCandidates.Where((BMSFileMaintenanceInfo m) => m.GetWAVHealth() == wavHealth && m.GetBGAHealth() == bgaHealth && m.GetMovieHealth() == movieHealth && m.GetOptIMGHealth() == optImgHealth)
            .Select((BMSFileMaintenanceInfo m) => DirectoryExt.GetDirectoryNameSimple(m.path))
            .Where((string dir) => !string.IsNullOrWhiteSpace(dir))
            .OrderBy((string dir) => dir, StringComparer.OrdinalIgnoreCase)
            .FirstOrDefault();
        if (string.IsNullOrWhiteSpace(bestDirectory))
        {
            result.Reason = InstalledDirectoryResolveReason.TieBreakUnresolved;
            return result;
        }
        result.InstallDirectory = bestDirectory;
        return result;
    }

    public InstallEstimationResult EstimateInstallationDirectory(IEnumerable<BMSFile> bmsFiles, HashSet<string> installedHashes, BMSDirectoryFileNameHash folderAllFileList, bool asParallel, BmsInstallationEstimateMode estimateMode)
    {
        return EstimateInstallationDirectory(bmsFiles, installedHashes, folderAllFileList, null, asParallel, estimateMode, null);
    }

    private static InstallEstimationFinalEvaluationMode GetFinalEvaluationMode(ChartResourceSnapshot resourceSnapshot)
    {
        return InstallEstimationFinalEvaluationMode.RelativeStrict;
    }

    private static string GetFinalEvaluationModeLogValue(InstallEstimationFinalEvaluationMode mode)
    {
        return "relative_strict";
    }

    internal SourceBaselineEvaluation EvaluateSourceBaseline(PackageInstallEstimationSnapshot snapshot)
    {
        if (snapshot?.RepresentativeFile == null)
        {
            return new SourceBaselineEvaluation();
        }

        return EvaluateSourceBaseline(
            snapshot.DefinedResources,
            snapshot.SourceDirectory,
            snapshot.BundledResources,
            snapshot.SourceCandidateResources);
    }

    internal SourceBaselineEvaluation EvaluateSourceBaseline(
        ChartResourceSnapshot resourceSnapshot,
        string sourceDirectory,
        DirectoryResourceLookupCache.Entry bundledResources,
        DirectoryResourceLookupCache.Entry sourceCandidateResources)
    {
        SourceBaselineEvaluation result = new SourceBaselineEvaluation();
        ChartResourceSnapshot effectiveSnapshot = resourceSnapshot ?? new ChartResourceSnapshot();
        if (effectiveSnapshot.TotalReferenceCount == 0)
        {
            result.PrimaryHealth = 100;
            result.IsViableDestination = true;
            return result;
        }

        InstallEstimationFinalEvaluationMode evaluationMode = GetFinalEvaluationMode(effectiveSnapshot);
        DirectoryResourceLookupCache.Entry effectiveBundledResources = bundledResources ?? new DirectoryResourceLookupCache.Entry();
        CandidateResourceView bundledView = evaluationMode == InstallEstimationFinalEvaluationMode.RelativeStrict
            ? CreateCandidateResourceView(effectiveBundledResources, null, null)
            : null;
        CandidateEvaluation evaluation = EvaluateCandidate(
            sourceDirectory ?? string.Empty,
            effectiveSnapshot,
            null,
            null,
            effectiveBundledResources,
            bundledView,
            sourceCandidateResources ?? new DirectoryResourceLookupCache.Entry(),
            null,
            evaluationMode,
            diagnostics: null,
            isSourceCandidate: true);
        result.PrimaryHealth = GetPrimaryHealth(effectiveSnapshot, evaluation);
        result.IsViableDestination = IsViableDestination(result.PrimaryHealth);
        result.Summary = evaluation.ToSummary();
        return result;
    }

    public InstallEstimationResult EstimateInstallationDirectory(IEnumerable<BMSFile> bmsFiles, HashSet<string> installedHashes, BMSDirectoryFileNameHash folderAllFileList, DirectoryResourceLookupCache directoryLookupCache, bool asParallel, BmsInstallationEstimateMode estimateMode, Func<string, InstallDestinationRepresentativeMetadata> representativeMetadataResolver = null, Func<string, InstallEstimationMetadataProfile> metadataProfileResolver = null, DirectoryRelativePathHashIndex relativePathHashIndex = null)
    {
        return EstimateInstallationDirectory(BuildLooseFileSnapshot(bmsFiles, installedHashes, estimateMode), folderAllFileList, directoryLookupCache, ResolveCandidateEvaluationDegree(asParallel), estimateMode, representativeMetadataResolver, metadataProfileResolver, relativePathHashIndex);
    }

    public InstallEstimationResult EstimateInstallationDirectory(PackageInstallEstimationSnapshot snapshot, BMSDirectoryFileNameHash folderAllFileList, DirectoryResourceLookupCache directoryLookupCache, bool asParallel, BmsInstallationEstimateMode estimateMode, Func<string, InstallDestinationRepresentativeMetadata> representativeMetadataResolver = null, Func<string, InstallEstimationMetadataProfile> metadataProfileResolver = null, DirectoryRelativePathHashIndex relativePathHashIndex = null)
    {
        return EstimateInstallationDirectory(snapshot, folderAllFileList, directoryLookupCache, ResolveCandidateEvaluationDegree(asParallel), estimateMode, representativeMetadataResolver, metadataProfileResolver, relativePathHashIndex);
    }

    public InstallEstimationResult EstimateInstallationDirectory(IEnumerable<BMSFile> bmsFiles, HashSet<string> installedHashes, BMSDirectoryFileNameHash folderAllFileList, DirectoryResourceLookupCache directoryLookupCache, int candidateEvaluationDegree, BmsInstallationEstimateMode estimateMode, Func<string, InstallDestinationRepresentativeMetadata> representativeMetadataResolver = null, Func<string, InstallEstimationMetadataProfile> metadataProfileResolver = null, DirectoryRelativePathHashIndex relativePathHashIndex = null)
    {
        return EstimateInstallationDirectory(BuildLooseFileSnapshot(bmsFiles, installedHashes, estimateMode), folderAllFileList, directoryLookupCache, candidateEvaluationDegree, estimateMode, representativeMetadataResolver, metadataProfileResolver, relativePathHashIndex);
    }

    public InstallEstimationResult EstimateInstallationDirectory(PackageInstallEstimationSnapshot snapshot, BMSDirectoryFileNameHash folderAllFileList, DirectoryResourceLookupCache directoryLookupCache, int candidateEvaluationDegree, BmsInstallationEstimateMode estimateMode, Func<string, InstallDestinationRepresentativeMetadata> representativeMetadataResolver = null, Func<string, InstallEstimationMetadataProfile> metadataProfileResolver = null, DirectoryRelativePathHashIndex relativePathHashIndex = null)
    {
        InstallEstimationResult result = new InstallEstimationResult();
        int effectiveCandidateEvaluationDegree = NormalizeCandidateEvaluationDegree(candidateEvaluationDegree);
        result.CandidateEvaluationDegree = effectiveCandidateEvaluationDegree;
        bool isMergeMode = estimateMode == BmsInstallationEstimateMode.MergeCandidateOnly;
        bool isReinstallCorrectionMode = estimateMode == BmsInstallationEstimateMode.ReinstallCorrection;
        bool useBundledResources = !(isMergeMode || isReinstallCorrectionMode);
        if (snapshot?.RepresentativeFile == null || folderAllFileList == null)
        {
            return result;
        }
        ChartResourceSnapshot resourceSnapshot = snapshot.DefinedResources ?? new ChartResourceSnapshot();
        InstallEstimationFinalEvaluationMode evaluationMode = GetFinalEvaluationMode(resourceSnapshot);
        DirectoryResourceLookupCache.Entry bundledResources = useBundledResources ? snapshot.BundledResources : null;
        EvaluationDiagnostics diagnostics = new EvaluationDiagnostics();
        result.TargetResourceCount = resourceSnapshot.TotalReferenceCount;
        result.TargetResourceHashCount = resourceSnapshot.EnumerateAllRelativePathHashes().Count();
        result.TargetPathAwareAudioHashCount = resourceSnapshot.AudioPathAwareReferenceCount;
        result.TargetPathAwareVisualHashCount = resourceSnapshot.VisualPathAwareReferenceCount;
        result.TargetPathAwareMovieHashCount = resourceSnapshot.MoviePathAwareReferenceCount;
        result.TargetPathAwareOptionalImageHashCount = resourceSnapshot.OptionalImagePathAwareReferenceCount;
        result.TargetPathAwareHashCount = result.TargetPathAwareAudioHashCount
            + result.TargetPathAwareVisualHashCount
            + result.TargetPathAwareMovieHashCount
            + result.TargetPathAwareOptionalImageHashCount;
        result.AudioReferenceCount = resourceSnapshot.AudioReferenceCount;
        result.VisualReferenceCount = resourceSnapshot.VisualReferenceCount;
        result.MovieReferenceCount = resourceSnapshot.MovieReferenceCount;
        result.OptionalImageReferenceCount = resourceSnapshot.OptionalImageReferenceCount;
        result.BundledAudioCount = useBundledResources ? snapshot.BundledAudioCount : 0;
        result.BundledImageCount = useBundledResources ? snapshot.BundledImageCount : 0;
        result.BundledMovieCount = useBundledResources ? snapshot.BundledMovieCount : 0;
        result.FinalEvaluationMode = evaluationMode;
        result.CandidateMode = useBundledResources ? "package_union_source_excluded" : "candidate_only_source_excluded";
        result.CoarseFilterMode = (directoryLookupCache != null) ? "path_aware_audio_gated" : "path_aware_hash_only";
        result.AudioMinimumMatchRequired = GetAudioMinimumMatchRequired(resourceSnapshot);
        result.ResourceSummary = "chart=" + (snapshot.RepresentativeFile.path ?? string.Empty)
            + " chartCount=" + snapshot.ChartCount
            + " audioRefs=" + resourceSnapshot.AudioReferenceCount
            + " visualRefs=" + resourceSnapshot.VisualReferenceCount
            + " movieRefs=" + resourceSnapshot.MovieReferenceCount
            + " optionalRefs=" + resourceSnapshot.OptionalImageReferenceCount
            + " pathSegmentRefs=" + resourceSnapshot.PathSegmentReferenceCount
            + " pathAwareAudioRefs=" + result.TargetPathAwareAudioHashCount
            + " pathAwareVisualRefs=" + result.TargetPathAwareVisualHashCount
            + " pathAwareMovieRefs=" + result.TargetPathAwareMovieHashCount
            + " pathAwareOptionalRefs=" + result.TargetPathAwareOptionalImageHashCount
            + " bundledAudio=" + result.BundledAudioCount
            + " bundledImage=" + result.BundledImageCount
            + " bundledMovie=" + result.BundledMovieCount
            + " candidateMode=" + result.CandidateMode;
        if (resourceSnapshot.TotalReferenceCount == 0 || result.TargetResourceHashCount == 0)
        {
            return result;
        }
        string sourceDir = snapshot.SourceDirectory;
        CandidateEvaluation sourceBaselineEvaluation = null;
        if (isReinstallCorrectionMode && !string.IsNullOrWhiteSpace(sourceDir))
        {
            sourceBaselineEvaluation = EvaluateDirectoryCandidate(
                sourceDir,
                resourceSnapshot,
                folderAllFileList,
                directoryLookupCache,
                relativePathHashIndex,
                bundledResources: null,
                bundledView: null,
                evaluationMode,
                diagnostics: null,
                isSourceCandidate: true);
            result.SourceBaselinePrimaryHealth = GetPrimaryHealth(resourceSnapshot, sourceBaselineEvaluation);
        }
        List<string> allCandidateDirs = folderAllFileList.Keys
            .Where((string dir) => !string.Equals(dir, sourceDir, StringComparison.OrdinalIgnoreCase))
            .ToList();
        result.CandidateDirectoryCountBeforeHashFilter = allCandidateDirs.Count;
        if (allCandidateDirs.Count == 0)
        {
            return result;
        }
        HashSet<uint> targetFileHashes = resourceSnapshot.EnumerateAllRelativePathHashes();
        List<string> candidateDirList = allCandidateDirs;
        if (targetFileHashes.Count > 0 || result.TargetPathAwareHashCount > 0)
        {
            candidateDirList = directoryLookupCache != null
                ? ApplyPathAwareBroadFilter(allCandidateDirs, resourceSnapshot, directoryLookupCache)
                : ApplyPathAwareBroadFilter(allCandidateDirs, resourceSnapshot, folderAllFileList, relativePathHashIndex, effectiveCandidateEvaluationDegree);
            result.CandidateDirectoryCountAfterBroadFilter = candidateDirList.Count;
            if (candidateDirList.Count == 0)
            {
                result.CandidateDirectoryCountAfterAudioGate = 0;
                result.CandidateDirectoryCountAfterHashFilter = 0;
                result.CandidateDirectoryCount = 0;
                SetNoViableDestinationResult(result);
                return result;
            }
            if (directoryLookupCache != null)
            {
                candidateDirList = ApplyAudioCandidateGate(
                    candidateDirList,
                    resourceSnapshot,
                    directoryLookupCache,
                    bundledResources,
                    evaluationMode);
            }
            result.CandidateDirectoryCountAfterAudioGate = candidateDirList.Count;
            if (candidateDirList.Count == 0)
            {
                result.CandidateDirectoryCountAfterHashFilter = 0;
                result.CandidateDirectoryCount = 0;
                SetNoViableDestinationResult(result);
                return result;
            }
            result.CandidateDirectoryCountAfterHashFilter = candidateDirList.Count;
        }
        else
        {
            result.CandidateDirectoryCountAfterBroadFilter = candidateDirList.Count;
            result.CandidateDirectoryCountAfterAudioGate = candidateDirList.Count;
            result.CandidateDirectoryCountAfterHashFilter = candidateDirList.Count;
        }
        result.CandidateDirectoryCount = candidateDirList.Count;
        CandidateHierarchyInfo hierarchyInfo = BuildCandidateHierarchyInfo(candidateDirList);
        result.HierarchyCandidateDirectoryCount = hierarchyInfo.HierarchyCandidateDirectoryCount;
        CandidateResourceView bundledView = evaluationMode == InstallEstimationFinalEvaluationMode.RelativeStrict
            ? CreateCandidateResourceView(bundledResources, null, null)
            : null;
        Stopwatch evaluationStopwatch = Stopwatch.StartNew();
        IEnumerable<string> candidateSource = candidateDirList.AsParallel().WithDegreeOfParallelism(effectiveCandidateEvaluationDegree);
        List<CandidateEvaluation> candidateInfos = candidateSource
            .Select((string candidateDir) => EvaluateDirectoryCandidate(
                candidateDir,
                resourceSnapshot,
                folderAllFileList,
                directoryLookupCache,
                relativePathHashIndex,
                bundledResources,
                bundledView,
                evaluationMode,
                diagnostics,
                isSourceCandidate: false))
            .ToList();
        evaluationStopwatch.Stop();
        result.EvaluationMs = evaluationStopwatch.ElapsedMilliseconds;
        if (candidateInfos.Count == 0)
        {
            return result;
        }
        List<CandidateEvaluation> orderedCandidates = candidateInfos.ToList();
        orderedCandidates.Sort(CompareCandidateEvaluations);
        Stopwatch ancestorShadowStopwatch = Stopwatch.StartNew();
        orderedCandidates = SuppressAncestorShadowCandidates(
            orderedCandidates,
            hierarchyInfo,
            resourceSnapshot,
            folderAllFileList,
            directoryLookupCache,
            relativePathHashIndex,
            diagnostics,
            out int ancestorShadowSuppressedCount,
            out int lazySelfOwnedEvaluationCount);
        ancestorShadowStopwatch.Stop();
        result.AncestorShadowSuppressedCount = ancestorShadowSuppressedCount;
        result.LazySelfOwnedEvaluationCount = lazySelfOwnedEvaluationCount;
        result.CandidateViewBuildMs = diagnostics.CandidateViewBuildMs;
        result.CandidateMatchMs = diagnostics.CandidateMatchMs;
        result.CandidateViewBuildCount = diagnostics.CandidateViewBuildCount;
        result.CandidateViewFallbackCount = diagnostics.CandidateViewFallbackCount;
        result.AncestorShadowEvaluationMs = hierarchyInfo.HasHierarchy ? ancestorShadowStopwatch.ElapsedMilliseconds : 0L;
        if (orderedCandidates.Count > 0 && IsViableDestination(GetPrimaryHealth(resourceSnapshot, orderedCandidates[0])))
        {
            ApplyMetadataTieBreakFrontier(orderedCandidates, snapshot.TargetMetadataProfile, metadataProfileResolver, result);
        }
        foreach (InstallEstimationCandidate candidate in orderedCandidates
            .Take(3)
            .Select(delegate (CandidateEvaluation evaluation)
            {
                InstallDestinationRepresentativeMetadata representativeMetadata = representativeMetadataResolver?.Invoke(evaluation.DirectoryPath) ?? InstallDestinationRepresentativeMetadata.Empty;
                return CreateCandidate(evaluation, representativeMetadata);
            }))
        {
            result.Candidates.Add(candidate);
        }
        CandidateEvaluation selectedCandidateEvaluation = orderedCandidates.First();
        CandidateEvaluation secondCandidateEvaluation = orderedCandidates.FirstOrDefault((CandidateEvaluation evaluation) => !ReferenceEquals(evaluation, selectedCandidateEvaluation));
        int selectedPrimaryHealth = GetPrimaryHealth(resourceSnapshot, selectedCandidateEvaluation);
        result.SelectedCandidatePrimaryHealth = selectedPrimaryHealth;
        bool selectedViable = IsViableDestination(selectedPrimaryHealth);
        bool hasMultipleViableCandidates = selectedViable
            && orderedCandidates.Skip(1).Any((CandidateEvaluation evaluation) => IsViableDestination(GetPrimaryHealth(resourceSnapshot, evaluation)));
        bool topTwoViableTie = secondCandidateEvaluation != null
            && selectedViable
            && IsViableDestination(GetPrimaryHealth(resourceSnapshot, secondCandidateEvaluation))
            && selectedCandidateEvaluation.HasSameRankingMetrics(secondCandidateEvaluation);
        bool metadataTieBreakDistinct = !isReinstallCorrectionMode && topTwoViableTie && IsMetadataTieBreakDistinct(selectedCandidateEvaluation, secondCandidateEvaluation, snapshot.TargetMetadataProfile, metadataProfileResolver);
        MetadataValidationResult metadataValidation = selectedViable
            ? EvaluateSelectedCandidateMetadata(selectedCandidateEvaluation, snapshot.TargetMetadataProfile, metadataProfileResolver)
            : null;
        result.MetadataValidationSummary = metadataValidation?.Summary ?? string.Empty;

        for (int i = 0; i < result.Candidates.Count && i < orderedCandidates.Count; i++)
        {
            result.Candidates[i].IsViableDestination = IsViableDestination(GetPrimaryHealth(resourceSnapshot, orderedCandidates[i]));
        }
        List<string> viableNonSourceSuggestions = result.Candidates
            .Where((InstallEstimationCandidate candidate) => candidate != null && candidate.IsViableDestination)
            .Select((InstallEstimationCandidate candidate) => candidate.DirectoryPath)
            .Where((string path) => !string.IsNullOrWhiteSpace(path))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
        result.SuggestedDestinationDirectories.Clear();
        result.TopCandidateSummary = string.Join(" || ", result.Candidates.Select((InstallEstimationCandidate candidate) => candidate.ToSummary()));
        result.SelectedCandidateSummary = result.SelectedCandidate?.ToSummary() ?? selectedCandidateEvaluation.ToSummary();

        if (!selectedViable)
        {
            SetNoViableDestinationResult(result);
        }
        else if (isReinstallCorrectionMode && hasMultipleViableCandidates)
        {
            result.HasViableDestination = true;
            result.DestinationDirectory = selectedCandidateEvaluation.DirectoryPath;
            result.Confidence = InstallEstimationConfidence.Low;
            result.ConfidenceReason = "reinstall_multiple_viable_candidates";
            result.LowConfidenceKind = InstallEstimationLowConfidenceKind.AmbiguousCandidates;
            result.ShouldAutoApplyDestination = false;
            result.SuggestedDestinationDirectories.AddRange(viableNonSourceSuggestions);
        }
        else if (topTwoViableTie)
        {
            result.HasViableDestination = true;
            result.DestinationDirectory = selectedCandidateEvaluation.DirectoryPath;
            if (metadataTieBreakDistinct)
            {
                result.Confidence = InstallEstimationConfidence.High;
                result.ConfidenceReason = "metadata_tiebreak_distinct";
                result.LowConfidenceKind = InstallEstimationLowConfidenceKind.None;
                result.ShouldAutoApplyDestination = true;
            }
            else
            {
                bool autoApplyAmbiguousCandidate =
                    !isReinstallCorrectionMode
                    &&
                    options.AutoApplyAmbiguousInstallDestination
                    && metadataValidation?.IsApplicable == true
                    && metadataValidation.Evidence == MetadataEvidenceStrength.Strong;
                result.Confidence = InstallEstimationConfidence.Low;
                result.ConfidenceReason = autoApplyAmbiguousCandidate
                    ? "tie_on_viable_non_source_candidates_auto_apply_enabled"
                    : "tie_on_viable_non_source_candidates";
                result.LowConfidenceKind = InstallEstimationLowConfidenceKind.AmbiguousCandidates;
                result.ShouldAutoApplyDestination = autoApplyAmbiguousCandidate;
                result.SuggestedDestinationDirectories.AddRange(viableNonSourceSuggestions);
            }
        }
        else
        {
            result.HasViableDestination = true;
            bool metadataMismatch = metadataValidation?.IsApplicable == true && metadataValidation.Evidence != MetadataEvidenceStrength.Strong;
            if (metadataMismatch)
            {
                result.Confidence = InstallEstimationConfidence.Low;
                result.ConfidenceReason = secondCandidateEvaluation == null ? "single_viable_candidate_metadata_mismatch" : "distinct_primary_metrics_metadata_mismatch";
                result.LowConfidenceKind = InstallEstimationLowConfidenceKind.MetadataMismatch;
                result.DestinationDirectory = null;
                result.ShouldAutoApplyDestination = false;
                if (!string.IsNullOrWhiteSpace(selectedCandidateEvaluation.DirectoryPath))
                {
                    result.SuggestedDestinationDirectories.Add(selectedCandidateEvaluation.DirectoryPath);
                }
            }
            else
            {
                bool reinstallNotImproved = isReinstallCorrectionMode && selectedPrimaryHealth <= result.SourceBaselinePrimaryHealth;
                if (reinstallNotImproved)
                {
                    result.Confidence = InstallEstimationConfidence.Low;
                    result.ConfidenceReason = "reinstall_candidate_not_improved";
                    result.LowConfidenceKind = InstallEstimationLowConfidenceKind.ReinstallNotImproved;
                    result.DestinationDirectory = null;
                    result.ShouldAutoApplyDestination = false;
                    if (!string.IsNullOrWhiteSpace(selectedCandidateEvaluation.DirectoryPath))
                    {
                        result.SuggestedDestinationDirectories.Add(selectedCandidateEvaluation.DirectoryPath);
                    }
                    return result;
                }
                result.Confidence = InstallEstimationConfidence.High;
                result.ConfidenceReason = isReinstallCorrectionMode
                    ? "reinstall_single_improved_candidate"
                    : (secondCandidateEvaluation == null ? "single_candidate" : "distinct_primary_metrics");
                result.LowConfidenceKind = InstallEstimationLowConfidenceKind.None;
                result.DestinationDirectory = selectedCandidateEvaluation.DirectoryPath;
                result.ShouldAutoApplyDestination = true;
            }
        }
        return result;
    }

    private static int GetAudioMinimumMatchRequired(ChartResourceSnapshot resourceSnapshot)
    {
        if (resourceSnapshot == null)
        {
            return 0;
        }
        if (resourceSnapshot.AudioReferenceCount >= 2)
        {
            return 2;
        }
        if (resourceSnapshot.AudioReferenceCount == 1)
        {
            return 1;
        }
        return 0;
    }

    private static List<string> ApplyPathAwareBroadFilter(IEnumerable<string> candidateDirectories, ChartResourceSnapshot resourceSnapshot, DirectoryResourceLookupCache directoryLookupCache)
    {
        List<string> candidates = (candidateDirectories ?? Enumerable.Empty<string>())
            .Where((string dir) => !string.IsNullOrWhiteSpace(dir))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
        if (candidates.Count == 0 || resourceSnapshot == null || directoryLookupCache == null)
        {
            return candidates;
        }

        HashSet<string> filteredDirectories = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        if (resourceSnapshot.AudioRelativePathHashes.Count > 0)
        {
            directoryLookupCache.EnsureAudioRelativeDirectoriesByHashes(resourceSnapshot.AudioRelativePathHashes);
            foreach (uint relativeHash in resourceSnapshot.AudioRelativePathHashes)
            {
                filteredDirectories.UnionWith(directoryLookupCache.GetDirectoriesByAudioRelativeHash(relativeHash));
            }
        }
        if (resourceSnapshot.VisualRelativePathHashes.Count > 0 || resourceSnapshot.OptionalImageRelativePathHashes.Count > 0)
        {
            directoryLookupCache.EnsureImageRelativeDirectoriesByHashes(resourceSnapshot.VisualRelativePathHashes.Concat(resourceSnapshot.OptionalImageRelativePathHashes));
            foreach (uint relativeHash in resourceSnapshot.VisualRelativePathHashes)
            {
                filteredDirectories.UnionWith(directoryLookupCache.GetDirectoriesByImageRelativeHash(relativeHash));
            }
            foreach (uint relativeHash in resourceSnapshot.OptionalImageRelativePathHashes)
            {
                filteredDirectories.UnionWith(directoryLookupCache.GetDirectoriesByImageRelativeHash(relativeHash));
            }
        }
        if (resourceSnapshot.MovieRelativePathHashes.Count > 0)
        {
            directoryLookupCache.EnsureMovieRelativeDirectoriesByHashes(resourceSnapshot.MovieRelativePathHashes);
            foreach (uint relativeHash in resourceSnapshot.MovieRelativePathHashes)
            {
                filteredDirectories.UnionWith(directoryLookupCache.GetDirectoriesByMovieRelativeHash(relativeHash));
            }
        }

        return candidates
            .Where((string candidateDir) => filteredDirectories.Contains(candidateDir))
            .ToList();
    }

    private static List<string> ApplyPathAwareBroadFilter(IEnumerable<string> candidateDirectories, ChartResourceSnapshot resourceSnapshot, BMSDirectoryFileNameHash folderAllFileList, DirectoryRelativePathHashIndex relativePathHashIndex, int candidateEvaluationDegree)
    {
        List<string> candidates = (candidateDirectories ?? Enumerable.Empty<string>())
            .Where((string dir) => !string.IsNullOrWhiteSpace(dir))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
        if (candidates.Count == 0 || resourceSnapshot == null || folderAllFileList == null)
        {
            return candidates;
        }

        IEnumerable<string> filtered = candidates.AsParallel()
            .WithDegreeOfParallelism(NormalizeCandidateEvaluationDegree(candidateEvaluationDegree))
            .Where(delegate (string dir)
            {
                CandidateResourceSource candidateResourceSource = ResolveCandidateResourceSource(
                    dir,
                    folderAllFileList,
                    directoryLookupCache: null,
                    relativePathHashIndex,
                    includeRelativeWhenCachePresent: true);
                if (!HasChartRelativeSeedMatch(resourceSnapshot, candidateResourceSource.RelativeEntry))
                {
                    return false;
                }
                return true;
            });
        return filtered.ToList();
    }

    private static bool HasChartRelativeSeedMatch(ChartResourceSnapshot resourceSnapshot, DirectoryRelativePathHashIndex.Entry relativeEntry)
    {
        if (resourceSnapshot == null)
        {
            return false;
        }
        return MatchesAny(resourceSnapshot.AudioRelativePathHashes, relativeEntry?.AudioRelativePathHashes)
            || MatchesAny(resourceSnapshot.VisualRelativePathHashes, relativeEntry?.ImageRelativePathHashes)
            || MatchesAny(resourceSnapshot.MovieRelativePathHashes, relativeEntry?.MovieRelativePathHashes)
            || MatchesAny(resourceSnapshot.OptionalImageRelativePathHashes, relativeEntry?.ImageRelativePathHashes);
    }

    private static bool MatchesAny(ISet<uint> targetHashes, ISet<uint> candidateHashes)
    {
        if (targetHashes == null || targetHashes.Count == 0 || candidateHashes == null || candidateHashes.Count == 0)
        {
            return false;
        }
        foreach (uint targetHash in targetHashes)
        {
            if (candidateHashes.Contains(targetHash))
            {
                return true;
            }
        }
        return false;
    }

    private List<string> ApplyAudioCandidateGate(IEnumerable<string> candidateDirectories, ChartResourceSnapshot resourceSnapshot, DirectoryResourceLookupCache directoryLookupCache, DirectoryResourceLookupCache.Entry bundledResources, InstallEstimationFinalEvaluationMode evaluationMode)
    {
        List<string> candidates = (candidateDirectories ?? Enumerable.Empty<string>())
            .Where((string dir) => !string.IsNullOrWhiteSpace(dir))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
        if (candidates.Count == 0 || resourceSnapshot == null || directoryLookupCache == null)
        {
            return candidates;
        }

        int requiredAudioMatchCount = GetAudioMinimumMatchRequired(resourceSnapshot);
        if (requiredAudioMatchCount <= 0 || resourceSnapshot.AudioBaseNameHashes.Count == 0)
        {
            return candidates;
        }

        int requiredMatchedForViableHealth = GetRequiredMatchedForViableAudioHealth(resourceSnapshot.AudioReferenceCount);
        CandidateResourceView bundledView = CreateCandidateResourceView(bundledResources, null, null);

        return candidates
            .Where(delegate (string candidateDir)
            {
                DirectoryResourceLookupCache.Entry entry = directoryLookupCache.GetEntryOrNull(candidateDir);
                CandidateResourceView candidateView = CreateCandidateResourceView(entry, null, null);
                if (candidateView.AudioBaseNameHashes.Count == 0 && candidateView.AudioRelativePathHashes.Count == 0)
                {
                    return false;
                }
                if (!HasMinimumAudioReferenceMatches(resourceSnapshot.AudioReferences, candidateView, requiredAudioMatchCount))
                {
                    return false;
                }
                return HasRequiredAudioMatchesForViability(resourceSnapshot.AudioReferences, candidateView, bundledView, requiredMatchedForViableHealth);
            })
            .ToList();
    }

    private static bool HasMinimumAudioReferenceMatches(IReadOnlyCollection<ChartResourceSnapshot.ResourceReference> targetAudioReferences, CandidateResourceView candidateView, int requiredAudioMatchCount)
    {
        if (requiredAudioMatchCount <= 0)
        {
            return true;
        }
        if (targetAudioReferences == null || targetAudioReferences.Count == 0 || candidateView == null)
        {
            return false;
        }

        int matched = 0;
        foreach (ChartResourceSnapshot.ResourceReference reference in targetAudioReferences)
        {
            if (IsReferenceMatched(reference, candidateView.AudioBaseNameHashes, null, candidateView.AudioRelativePathHashes, null) && ++matched >= requiredAudioMatchCount)
            {
                return true;
            }
        }

        return false;
    }

    private int GetRequiredMatchedForViableAudioHealth(int defined)
    {
        if (defined <= 0)
        {
            return 0;
        }
        for (int matched = 0; matched <= defined; matched++)
        {
            if (ComputeHealth(matched, defined) > innerWavHealthThreshold)
            {
                return matched;
            }
        }
        return defined + 1;
    }

    private static bool HasRequiredAudioMatchesForViability(IReadOnlyCollection<ChartResourceSnapshot.ResourceReference> targetAudioReferences, CandidateResourceView candidateView, CandidateResourceView bundledView, int requiredMatchedForViableHealth)
    {
        if (targetAudioReferences == null || targetAudioReferences.Count == 0)
        {
            return true;
        }
        if (requiredMatchedForViableHealth <= 0)
        {
            return true;
        }
        if ((candidateView?.AudioBaseNameHashes == null || candidateView.AudioBaseNameHashes.Count == 0)
            && (candidateView?.AudioRelativePathHashes == null || candidateView.AudioRelativePathHashes.Count == 0)
            && (bundledView?.AudioBaseNameHashes == null || bundledView.AudioBaseNameHashes.Count == 0)
            && (bundledView?.AudioRelativePathHashes == null || bundledView.AudioRelativePathHashes.Count == 0))
        {
            return false;
        }

        int matched = 0;
        foreach (ChartResourceSnapshot.ResourceReference reference in targetAudioReferences)
        {
            if (IsReferenceMatched(reference, candidateView?.AudioBaseNameHashes, bundledView?.AudioBaseNameHashes, candidateView?.AudioRelativePathHashes, bundledView?.AudioRelativePathHashes))
            {
                matched++;
                if (matched >= requiredMatchedForViableHealth)
                {
                    return true;
                }
            }
        }

        return false;
    }

    private static int ComputeHealth(int matched, int defined)
    {
        if (defined <= 0)
        {
            return 100;
        }
        return (int)Math.Round(100.0 * matched / defined, MidpointRounding.AwayFromZero);
    }

    private static void SetNoViableDestinationResult(InstallEstimationResult result)
    {
        if (result == null)
        {
            return;
        }
        result.HasViableDestination = false;
        result.Confidence = InstallEstimationConfidence.High;
        result.ConfidenceReason = "no_viable_destination_below_threshold";
        result.LowConfidenceKind = InstallEstimationLowConfidenceKind.None;
        result.DestinationDirectory = null;
        result.ShouldAutoApplyDestination = false;
    }

    private static PackageInstallEstimationSnapshot BuildLooseFileSnapshot(IEnumerable<BMSFile> bmsFiles, HashSet<string> installedHashes, BmsInstallationEstimateMode estimateMode)
    {
        List<BMSFile> targetFiles = (bmsFiles ?? Enumerable.Empty<BMSFile>())
            .Where((BMSFile file) => file != null)
            .ToList();
        if (targetFiles.Count == 0 || targetFiles.Any((BMSFile bmsInfo) => !string.IsNullOrWhiteSpace(bmsInfo.instl_dst)))
        {
            return null;
        }
        bool isCorrectionLikeMode = estimateMode == BmsInstallationEstimateMode.ReinstallCorrection || estimateMode == BmsInstallationEstimateMode.MergeCandidateOnly;
        if (!isCorrectionLikeMode && installedHashes != null)
        {
            targetFiles = targetFiles.Where((BMSFile file) => !installedHashes.Contains(file.hash)).ToList();
        }
        return targetFiles.Count == 0 ? null : PackageInstallEstimationSnapshotBuilder.BuildForLooseFiles(targetFiles);
    }

    private static bool TrySelectRepresentativeFile(IEnumerable<BMSFile> bmsFiles, HashSet<string> installedHashes, BmsInstallationEstimateMode estimateMode, out BMSFile representativeFile)
    {
        representativeFile = null;
        List<BMSFile> targetFiles = (bmsFiles ?? Enumerable.Empty<BMSFile>()).Where((BMSFile bmsInfo) => bmsInfo != null).ToList();
        if (targetFiles.Count == 0 || targetFiles.Any((BMSFile bmsInfo) => !string.IsNullOrWhiteSpace(bmsInfo.instl_dst)))
        {
            return false;
        }
        bool isReinstallCorrectionMode = estimateMode == BmsInstallationEstimateMode.ReinstallCorrection;
        bool isMergeMode = estimateMode == BmsInstallationEstimateMode.MergeCandidateOnly;
        bool isCorrectionLikeMode = isReinstallCorrectionMode || isMergeMode;
        representativeFile = targetFiles
            .Where((BMSFile bmsFile) => bmsFile != null && (isCorrectionLikeMode || installedHashes == null || !installedHashes.Contains(bmsFile.hash)))
            .OrderByDescending(GetDefinedResourceCount)
            .FirstOrDefault();
        return representativeFile != null;
    }

    public void CorrectInstallationDirectory(IEnumerable<BMSFile> bmsFiles, Action<BMSFile> searchInstallDestination)
    {
        foreach (BMSFile bmsFile in (bmsFiles ?? Enumerable.Empty<BMSFile>()).Where((BMSFile file) => file != null))
        {
            lock (bmsFile)
            {
                if (!string.IsNullOrWhiteSpace(bmsFile.instl_dst))
                {
                    continue;
                }
                searchInstallDestination?.Invoke(bmsFile);
                if (!string.IsNullOrWhiteSpace(bmsFile.instl_dst) && bmsFile.instl_dst.Equals(DirectoryExt.GetDirectoryNameSimple(bmsFile.path), StringComparison.OrdinalIgnoreCase))
                {
                    bmsFile.instl_dst = null;
                }
            }
        }
    }

    public void ClearInstallDestinations(IEnumerable<BMSFile> bmsFiles)
    {
        foreach (BMSFile bmsFile in (bmsFiles ?? Enumerable.Empty<BMSFile>()).Where((BMSFile file) => file != null && (!string.IsNullOrWhiteSpace(file.instl_dst) || !string.IsNullOrWhiteSpace(file.InstallDestinationTitle) || !string.IsNullOrWhiteSpace(file.InstallDestinationArtist) || (file.InstallDestinationSuggestions?.Count ?? 0) > 0 || file.HasLowConfidenceInstallEstimationWarning())))
        {
            bmsFile.instl_dst = null;
            bmsFile.InstallDestinationTitle = string.Empty;
            bmsFile.InstallDestinationArtist = string.Empty;
            bmsFile.InstallDestinationSuggestions = Array.Empty<string>();
            bmsFile.ClearWarningsByCategory(ChartWarningCategory.InstallEstimation);
            bmsFile.IsInstallDestinationSuggestionPopupOpen = false;
        }
    }

    public PendingInstallDestinationSelectionResult ValidatePendingInstallDestination(BMSFile targetFile, IEnumerable<BMSPackage> pendingPackages, IEnumerable<string> knownChartDirectories, string destinationDirectory)
    {
        return ValidateInstallDestination(targetFile, pendingPackages, knownChartDirectories, destinationDirectory, allowStandaloneLibraryFile: false);
    }

    public PendingInstallDestinationSelectionResult ValidateInstallDestination(BMSFile targetFile, IEnumerable<BMSPackage> pendingPackages, IEnumerable<string> knownChartDirectories, string destinationDirectory, bool allowStandaloneLibraryFile)
    {
        PendingInstallDestinationSelectionResult result = new PendingInstallDestinationSelectionResult();
        if (targetFile == null)
        {
            return result;
        }
        BMSPackage package = (pendingPackages ?? Enumerable.Empty<BMSPackage>())
            .FirstOrDefault((BMSPackage pkg) => pkg != null && pkg.BMSFiles.Any((BMSFile file) => file != null && (ReferenceEquals(file, targetFile) || (!string.IsNullOrWhiteSpace(file.path) && !string.IsNullOrWhiteSpace(targetFile.path) && file.path.Equals(targetFile.path, StringComparison.OrdinalIgnoreCase)))));
        if (package == null)
        {
            if (!allowStandaloneLibraryFile)
            {
                result.WarningMessage = Properties.Resources.Warn_PendingPackageNotFound;
                return result;
            }
            result.TargetFiles.Add(targetFile);
        }
        else
        {
            result.TargetFiles.AddRange(package.BMSFiles.Where((BMSFile file) => file != null));
        }
        if (string.IsNullOrWhiteSpace(destinationDirectory))
        {
            result.Success = true;
            return result;
        }
        string normalizedInput;
        try
        {
            normalizedInput = Path.GetFullPath(destinationDirectory.Trim().Trim('"'));
        }
        catch (Exception ex)
        {
            result.WarningMessage = string.Format(Properties.Resources.Warn_InvalidInstallPath, destinationDirectory, ex.Message);
            return result;
        }
        string installDirectory = File.Exists(normalizedInput)
            ? DirectoryExt.GetDirectoryNameSimple(normalizedInput)
            : normalizedInput.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        if (!Directory.Exists(installDirectory))
        {
            result.WarningMessage = string.Format(Properties.Resources.Warn_InstallDirNotFound, installDirectory);
            return result;
        }
        HashSet<string> knownDirectories = new HashSet<string>((knownChartDirectories ?? Enumerable.Empty<string>()).Where((string path) => !string.IsNullOrWhiteSpace(path)), StringComparer.OrdinalIgnoreCase);
        if (!knownDirectories.Contains(installDirectory))
        {
            result.WarningMessage = string.Format(Properties.Resources.Warn_InstallDirMustContainBms, installDirectory);
            return result;
        }
        result.Success = true;
        result.ValidatedDestinationDirectory = installDirectory;
        return result;
    }

    public static List<string> GetDistinctInstalledDirectoriesByHash(InstalledChartDirectoryIndexSnapshot installedDirectoryIndexSnapshot, string md5, string sha256 = null)
    {
        if (installedDirectoryIndexSnapshot == null)
        {
            return new List<string>();
        }
        HashSet<string> directories = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        if (IsBmsHashAvailable(md5) && installedDirectoryIndexSnapshot.Md5Directories.TryGetValue(md5, out List<string> md5Directories) && md5Directories != null)
        {
            directories.UnionWith(md5Directories.Where((string dir) => !string.IsNullOrWhiteSpace(dir)));
        }
        if (!string.IsNullOrWhiteSpace(sha256) && installedDirectoryIndexSnapshot.Sha256Directories.TryGetValue(sha256, out List<string> shaDirectories) && shaDirectories != null)
        {
            directories.UnionWith(shaDirectories.Where((string dir) => !string.IsNullOrWhiteSpace(dir)));
        }
        return directories.ToList();
    }

    public static List<string> GetDistinctInstalledDirectoriesByHash(InstalledChartDirectoryIndexSnapshot installedDirectoryIndexSnapshot, BMSFile file)
    {
        return GetDistinctInstalledDirectoriesByPrimaryHash(installedDirectoryIndexSnapshot, PendingChartEntry.GetPrimaryLookupHash(file));
    }

    public static List<string> GetDistinctInstalledDirectoriesByPrimaryHash(InstalledChartDirectoryIndexSnapshot installedDirectoryIndexSnapshot, string lookupHash)
    {
        if (installedDirectoryIndexSnapshot == null || string.IsNullOrWhiteSpace(lookupHash))
        {
            return new List<string>();
        }
        if (installedDirectoryIndexSnapshot.Md5Directories.TryGetValue(lookupHash, out List<string> md5Directories) && md5Directories != null)
        {
            return md5Directories.Where((string dir) => !string.IsNullOrWhiteSpace(dir)).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        }
        if (installedDirectoryIndexSnapshot.Sha256Directories.TryGetValue(lookupHash, out List<string> shaDirectories) && shaDirectories != null)
        {
            return shaDirectories.Where((string dir) => !string.IsNullOrWhiteSpace(dir)).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        }
        return new List<string>();
    }

    public static BMSFile FindChartWithMissingInstalledDirectory(BMSPackage package, InstalledChartDirectoryIndexSnapshot installedDirectoryIndexSnapshot)
    {
        return (package?.BMSFiles ?? new List<BMSFile>()).FirstOrDefault((BMSFile file) => file == null || GetDistinctInstalledDirectoriesByHash(installedDirectoryIndexSnapshot, file).Count == 0);
    }

    public static BMSFile FindChartWithMultipleInstalledDirectories(BMSPackage package, InstalledChartDirectoryIndexSnapshot installedDirectoryIndexSnapshot)
    {
        return (package?.BMSFiles ?? new List<BMSFile>()).FirstOrDefault((BMSFile file) => file != null && GetDistinctInstalledDirectoriesByHash(installedDirectoryIndexSnapshot, file).Count > 1);
    }

    public static int CountDistinctInstalledDirectoriesForPackage(BMSPackage package, InstalledChartDirectoryIndexSnapshot installedDirectoryIndexSnapshot)
    {
        return (package?.BMSFiles ?? new List<BMSFile>())
            .Where((BMSFile file) => file != null)
            .SelectMany((BMSFile file) => GetDistinctInstalledDirectoriesByHash(installedDirectoryIndexSnapshot, file))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Count();
    }

    private static int GetDefinedResourceCount(BMSFile bmsFile)
    {
        return ChartResourceSnapshot.Create(bmsFile).TotalReferenceCount;
    }

    private static CandidateEvaluation EvaluateCandidate(string candidateDir, ChartResourceSnapshot snapshot, uint[] fileNameHashes, DirectoryResourceLookupCache.Entry entry, DirectoryResourceLookupCache.Entry bundledResources, CandidateResourceView bundledView, DirectoryResourceLookupCache.Entry transientCandidateEntry, DirectoryRelativePathHashIndex.Entry relativePathEntry, InstallEstimationFinalEvaluationMode evaluationMode, EvaluationDiagnostics diagnostics, bool isSourceCandidate)
    {
        DirectoryResourceLookupCache.Entry candidateEntry = transientCandidateEntry ?? entry;
        return EvaluateCandidateRelativeStrict(candidateDir, snapshot, fileNameHashes, candidateEntry, bundledView, relativePathEntry, diagnostics, isSourceCandidate);
    }

    private static CandidateEvaluation EvaluateDirectoryCandidate(string candidateDir, ChartResourceSnapshot snapshot, BMSDirectoryFileNameHash folderAllFileList, DirectoryResourceLookupCache directoryLookupCache, DirectoryRelativePathHashIndex relativePathHashIndex, DirectoryResourceLookupCache.Entry bundledResources, CandidateResourceView bundledView, InstallEstimationFinalEvaluationMode evaluationMode, EvaluationDiagnostics diagnostics, bool isSourceCandidate)
    {
        CandidateResourceSource candidateResourceSource = ResolveCandidateResourceSource(
            candidateDir,
            folderAllFileList,
            directoryLookupCache,
            relativePathHashIndex,
            includeRelativeWhenCachePresent: false);
        return EvaluateCandidate(
            candidateDir,
            snapshot,
            candidateResourceSource.FallbackBaseNameHashes,
            candidateResourceSource.CacheEntry,
            bundledResources,
            bundledView,
            null,
            candidateResourceSource.RelativeEntry,
            evaluationMode,
            diagnostics,
            isSourceCandidate);
    }

    private static CandidateEvaluation EvaluateCandidateRelativeStrict(string candidateDir, ChartResourceSnapshot snapshot, uint[] fileNameHashes, DirectoryResourceLookupCache.Entry entry, CandidateResourceView bundledView, DirectoryRelativePathHashIndex.Entry relativePathEntry, EvaluationDiagnostics diagnostics, bool isSourceCandidate)
    {
        CandidateResourceView candidateView = CreateCandidateResourceView(entry, relativePathEntry, fileNameHashes, diagnostics: diagnostics);
        if (bundledView == null)
        {
            bundledView = CandidateResourceView.Empty;
        }

        Stopwatch matchStopwatch = diagnostics == null ? null : Stopwatch.StartNew();
        CandidateEvaluation evaluation = new CandidateEvaluation
        {
            DirectoryPath = candidateDir,
            IsSourceCandidate = isSourceCandidate,
            AudioDefined = snapshot.AudioReferenceCount,
            VisualDefined = snapshot.VisualReferenceCount,
            MovieDefined = snapshot.MovieReferenceCount,
            OptionalImageDefined = snapshot.OptionalImageReferenceCount,
            AudioCandidateCount = CountUnion(candidateView.AudioRelativePathHashes, bundledView.AudioRelativePathHashes),
            VisualCandidateCount = CountUnion(candidateView.VisualRelativePathHashes, bundledView.VisualRelativePathHashes),
            MovieCandidateCount = CountUnion(candidateView.MovieRelativePathHashes, bundledView.MovieRelativePathHashes),
            OptionalImageCandidateCount = CountUnion(candidateView.VisualRelativePathHashes, bundledView.VisualRelativePathHashes),
            AudioFileCount = CountTotalResourceUnion(candidateView.AllBaseNameHashes, bundledView.AllBaseNameHashes, candidateView.AudioRelativePathHashes, candidateView.VisualRelativePathHashes, candidateView.MovieRelativePathHashes, bundledView.AudioRelativePathHashes, bundledView.VisualRelativePathHashes, bundledView.MovieRelativePathHashes)
        };
        if (candidateView.TotalResourceCount > 0 || bundledView.TotalResourceCount > 0 || candidateView.AllBaseNameHashes.Count > 0 || bundledView.AllBaseNameHashes.Count > 0)
        {
            int audioMatched = CountMatchedReferences(snapshot.AudioReferences, candidateView.AudioBaseNameHashes, bundledView.AudioBaseNameHashes, candidateView.AudioRelativePathHashes, bundledView.AudioRelativePathHashes);
            int visualMatched = CountMatchedReferences(snapshot.VisualReferences, candidateView.VisualBaseNameHashes, bundledView.VisualBaseNameHashes, candidateView.VisualRelativePathHashes, bundledView.VisualRelativePathHashes);
            int movieMatched = CountMatchedReferences(snapshot.MovieReferences, candidateView.MovieBaseNameHashes, bundledView.MovieBaseNameHashes, candidateView.MovieRelativePathHashes, bundledView.MovieRelativePathHashes);
            int optionalMatched = CountMatchedReferences(snapshot.OptionalImageReferences, candidateView.VisualBaseNameHashes, bundledView.VisualBaseNameHashes, candidateView.VisualRelativePathHashes, bundledView.VisualRelativePathHashes);
            evaluation.AudioMatched = audioMatched;
            evaluation.AudioExactMatched = audioMatched;
            evaluation.VisualMatched = visualMatched;
            evaluation.VisualExactMatched = visualMatched;
            evaluation.MovieMatched = movieMatched;
            evaluation.MovieExactMatched = movieMatched;
            evaluation.OptionalImageMatched = optionalMatched;
            evaluation.OptionalImageExactMatched = optionalMatched;
        }

        if (matchStopwatch != null)
        {
            matchStopwatch.Stop();
            diagnostics.RecordCandidateMatch(matchStopwatch.ElapsedMilliseconds);
        }

        return evaluation;
    }

    private static CandidateResourceView CreateCandidateResourceView(DirectoryResourceLookupCache.Entry entry, DirectoryRelativePathHashIndex.Entry relativePathEntry, uint[] fileNameHashes, EvaluationDiagnostics diagnostics = null)
    {
        return CreateCandidateResourceView(entry, relativePathEntry, fileNameHashes, includeSelfOwned: false, diagnostics: diagnostics);
    }

    private static CandidateResourceSource ResolveCandidateResourceSource(string directoryPath, BMSDirectoryFileNameHash folderAllFileList, DirectoryResourceLookupCache directoryLookupCache, DirectoryRelativePathHashIndex relativePathHashIndex, bool includeRelativeWhenCachePresent)
    {
        DirectoryResourceLookupCache.Entry cacheEntry = directoryLookupCache?.GetEntryOrNull(directoryPath);
        DirectoryRelativePathHashIndex.Entry relativeEntry = (includeRelativeWhenCachePresent || cacheEntry == null)
            ? relativePathHashIndex?.GetEntryOrNull(directoryPath)
            : null;
        uint[] fallbackBaseNameHashes = cacheEntry == null && relativeEntry == null
            ? folderAllFileList?.TryGetCachedFileNameHashArray(directoryPath)
            : null;
        return new CandidateResourceSource(cacheEntry, relativeEntry, fallbackBaseNameHashes);
    }

    private static CandidateResourceView CreateCandidateResourceView(DirectoryResourceLookupCache.Entry entry, DirectoryRelativePathHashIndex.Entry relativePathEntry, uint[] fileNameHashes, bool includeSelfOwned, EvaluationDiagnostics diagnostics = null)
    {
        if (entry == null && relativePathEntry == null && (fileNameHashes == null || fileNameHashes.Length == 0))
        {
            return CandidateResourceView.Empty;
        }

        Stopwatch buildStopwatch = diagnostics == null ? null : Stopwatch.StartNew();
        bool usedFallback = false;
        ISet<uint> aggregateBaseNameHashesFromRelativeIndex = relativePathEntry == null
            ? null
            : CreateUnionSet(relativePathEntry.AudioBaseNameHashes, relativePathEntry.ImageBaseNameHashes, relativePathEntry.MovieBaseNameHashes);
        ISet<uint> allBaseNameHashes = entry?.AllBaseNameHashes ?? aggregateBaseNameHashesFromRelativeIndex;
        if ((allBaseNameHashes == null || allBaseNameHashes.Count == 0) && fileNameHashes != null && fileNameHashes.Length > 0)
        {
            allBaseNameHashes = new HashSet<uint>(fileNameHashes);
            usedFallback = true;
        }

        ISet<uint> audioBaseNameHashes = entry?.AudioBaseNameHashes ?? relativePathEntry?.AudioBaseNameHashes ?? allBaseNameHashes ?? CandidateResourceView.Empty.AudioBaseNameHashes;
        ISet<uint> visualBaseNameHashes = entry?.ImageBaseNameHashes ?? relativePathEntry?.ImageBaseNameHashes ?? allBaseNameHashes ?? CandidateResourceView.Empty.VisualBaseNameHashes;
        ISet<uint> movieBaseNameHashes = entry?.MovieBaseNameHashes ?? relativePathEntry?.MovieBaseNameHashes ?? allBaseNameHashes ?? CandidateResourceView.Empty.MovieBaseNameHashes;
        ISet<uint> audioRelativePathHashes = entry?.AudioRelativePathHashes ?? relativePathEntry?.AudioRelativePathHashes ?? CandidateResourceView.Empty.AudioRelativePathHashes;
        ISet<uint> visualRelativePathHashes = entry?.ImageRelativePathHashes ?? relativePathEntry?.ImageRelativePathHashes ?? CandidateResourceView.Empty.VisualRelativePathHashes;
        ISet<uint> movieRelativePathHashes = entry?.MovieRelativePathHashes ?? relativePathEntry?.MovieRelativePathHashes ?? CandidateResourceView.Empty.MovieRelativePathHashes;

        CandidateResourceView view = new CandidateResourceView
        {
            AllBaseNameHashes = allBaseNameHashes ?? CreateUnionSet(audioBaseNameHashes, visualBaseNameHashes, movieBaseNameHashes),
            AudioBaseNameHashes = audioBaseNameHashes,
            VisualBaseNameHashes = visualBaseNameHashes,
            MovieBaseNameHashes = movieBaseNameHashes,
            AudioRelativePathHashes = audioRelativePathHashes,
            VisualRelativePathHashes = visualRelativePathHashes,
            MovieRelativePathHashes = movieRelativePathHashes,
            AudioResourceCount = entry?.AudioRelativePathHashArray.Length ?? relativePathEntry?.AudioRelativePathHashArray.Length ?? audioBaseNameHashes.Count,
            VisualResourceCount = entry?.ImageRelativePathHashArray.Length ?? relativePathEntry?.ImageRelativePathHashArray.Length ?? visualBaseNameHashes.Count,
            MovieResourceCount = entry?.MovieRelativePathHashArray.Length ?? relativePathEntry?.MovieRelativePathHashArray.Length ?? movieBaseNameHashes.Count,
            TotalResourceCount = Math.Max(
                CountUnion(audioRelativePathHashes, visualRelativePathHashes, movieRelativePathHashes),
                (allBaseNameHashes ?? CreateUnionSet(audioBaseNameHashes, visualBaseNameHashes, movieBaseNameHashes)).Count)
        };
        if (!includeSelfOwned)
        {
            if (buildStopwatch != null)
            {
                buildStopwatch.Stop();
                diagnostics.RecordCandidateViewBuild(buildStopwatch.ElapsedMilliseconds, usedFallback);
            }
            return view;
        }

        ISet<uint> selfOwnedAggregateBaseNameHashesFromRelativeIndex = relativePathEntry == null
            ? null
            : CreateUnionSet(relativePathEntry.SelfOwnedAudioBaseNameHashes, relativePathEntry.SelfOwnedImageBaseNameHashes, relativePathEntry.SelfOwnedMovieBaseNameHashes);
        ISet<uint> selfOwnedAllBaseNameHashes = entry?.SelfOwnedAllBaseNameHashes ?? selfOwnedAggregateBaseNameHashesFromRelativeIndex;
        if ((selfOwnedAllBaseNameHashes == null || selfOwnedAllBaseNameHashes.Count == 0) && fileNameHashes != null && fileNameHashes.Length > 0)
        {
            selfOwnedAllBaseNameHashes = new HashSet<uint>(fileNameHashes);
            usedFallback = true;
        }
        selfOwnedAllBaseNameHashes ??= view.AllBaseNameHashes;
        ISet<uint> selfOwnedAudioBaseNameHashes = entry?.SelfOwnedAudioBaseNameHashes ?? relativePathEntry?.SelfOwnedAudioBaseNameHashes ?? view.AudioBaseNameHashes;
        ISet<uint> selfOwnedVisualBaseNameHashes = entry?.SelfOwnedImageBaseNameHashes ?? relativePathEntry?.SelfOwnedImageBaseNameHashes ?? view.VisualBaseNameHashes;
        ISet<uint> selfOwnedMovieBaseNameHashes = entry?.SelfOwnedMovieBaseNameHashes ?? relativePathEntry?.SelfOwnedMovieBaseNameHashes ?? view.MovieBaseNameHashes;
        ISet<uint> selfOwnedAudioRelativePathHashes = entry?.SelfOwnedAudioRelativePathHashes ?? relativePathEntry?.SelfOwnedAudioRelativePathHashes ?? view.AudioRelativePathHashes;
        ISet<uint> selfOwnedVisualRelativePathHashes = entry?.SelfOwnedImageRelativePathHashes ?? relativePathEntry?.SelfOwnedImageRelativePathHashes ?? view.VisualRelativePathHashes;
        ISet<uint> selfOwnedMovieRelativePathHashes = entry?.SelfOwnedMovieRelativePathHashes ?? relativePathEntry?.SelfOwnedMovieRelativePathHashes ?? view.MovieRelativePathHashes;
        view.SelfOwnedAllBaseNameHashes = selfOwnedAllBaseNameHashes;
        view.SelfOwnedAudioBaseNameHashes = selfOwnedAudioBaseNameHashes;
        view.SelfOwnedVisualBaseNameHashes = selfOwnedVisualBaseNameHashes;
        view.SelfOwnedMovieBaseNameHashes = selfOwnedMovieBaseNameHashes;
        view.SelfOwnedAudioRelativePathHashes = selfOwnedAudioRelativePathHashes;
        view.SelfOwnedVisualRelativePathHashes = selfOwnedVisualRelativePathHashes;
        view.SelfOwnedMovieRelativePathHashes = selfOwnedMovieRelativePathHashes;
        view.SelfOwnedAudioResourceCount = entry?.SelfOwnedAudioRelativePathHashArray.Length ?? relativePathEntry?.SelfOwnedAudioRelativePathHashArray.Length ?? selfOwnedAudioBaseNameHashes.Count;
        view.SelfOwnedVisualResourceCount = entry?.SelfOwnedImageRelativePathHashArray.Length ?? relativePathEntry?.SelfOwnedImageRelativePathHashArray.Length ?? selfOwnedVisualBaseNameHashes.Count;
        view.SelfOwnedMovieResourceCount = entry?.SelfOwnedMovieRelativePathHashArray.Length ?? relativePathEntry?.SelfOwnedMovieRelativePathHashArray.Length ?? selfOwnedMovieBaseNameHashes.Count;
        view.SelfOwnedTotalResourceCount = Math.Max(
            CountUnion(selfOwnedAudioRelativePathHashes, selfOwnedVisualRelativePathHashes, selfOwnedMovieRelativePathHashes),
            selfOwnedAllBaseNameHashes.Count);
        if (buildStopwatch != null)
        {
            buildStopwatch.Stop();
            diagnostics.RecordCandidateViewBuild(buildStopwatch.ElapsedMilliseconds, usedFallback);
        }
        return view;
    }

    private static ISet<uint> CreateUnionSet(params ISet<uint>[] hashSets)
    {
        HashSet<uint> union = new HashSet<uint>();
        foreach (ISet<uint> hashSet in hashSets ?? Array.Empty<ISet<uint>>())
        {
            if (hashSet == null || hashSet.Count == 0)
            {
                continue;
            }
            union.UnionWith(hashSet);
        }
        return union;
    }

    private static int CountMatchedReferences(IReadOnlyCollection<ChartResourceSnapshot.ResourceReference> targetReferences, ISet<uint> candidateBaseNameHashes, ISet<uint> bundledBaseNameHashes, ISet<uint> candidateRelativePathHashes, ISet<uint> bundledRelativePathHashes)
    {
        if (targetReferences == null || targetReferences.Count == 0)
        {
            return 0;
        }

        int matched = 0;
        foreach (ChartResourceSnapshot.ResourceReference reference in targetReferences)
        {
            if (IsReferenceMatched(reference, candidateBaseNameHashes, bundledBaseNameHashes, candidateRelativePathHashes, bundledRelativePathHashes))
            {
                matched++;
            }
        }
        return matched;
    }

    private static bool IsReferenceMatched(ChartResourceSnapshot.ResourceReference reference, ISet<uint> candidateBaseNameHashes, ISet<uint> bundledBaseNameHashes, ISet<uint> candidateRelativePathHashes, ISet<uint> bundledRelativePathHashes)
    {
        return (candidateRelativePathHashes?.Contains(reference.RelativePathHash) ?? false)
            || (bundledRelativePathHashes?.Contains(reference.RelativePathHash) ?? false);
    }

    private static int CountUnion(ISet<uint> candidateHashes, ISet<uint> bundledHashes)
    {
        int candidateCount = candidateHashes?.Count ?? 0;
        int bundledCount = bundledHashes?.Count ?? 0;
        if (candidateCount == 0)
        {
            return bundledCount;
        }
        if (bundledCount == 0)
        {
            return candidateCount;
        }
        int total = candidateCount;
        foreach (uint bundledHash in bundledHashes)
        {
            if (!candidateHashes.Contains(bundledHash))
            {
                total++;
            }
        }
        return total;
    }

    private static int CountUnion(params ISet<uint>[] hashSets)
    {
        HashSet<uint> union = new HashSet<uint>();
        foreach (ISet<uint> hashSet in hashSets ?? Array.Empty<ISet<uint>>())
        {
            if (hashSet == null || hashSet.Count == 0)
            {
                continue;
            }
            union.UnionWith(hashSet);
        }
        return union.Count;
    }

    private static int CountTotalResourceUnion(ISet<uint> candidateAllBaseNameHashes, ISet<uint> bundledAllBaseNameHashes, params ISet<uint>[] relativeHashSets)
    {
        int relativeUnionCount = CountUnion(relativeHashSets);
        if (relativeUnionCount > 0)
        {
            return relativeUnionCount;
        }
        return CountUnion(candidateAllBaseNameHashes, bundledAllBaseNameHashes);
    }

    private static int GetPrimaryHealth(ChartResourceSnapshot snapshot, CandidateEvaluation evaluation)
    {
        if (evaluation == null)
        {
            return 0;
        }
        return snapshot.AudioReferenceCount > 0
            ? evaluation.AudioHealth
            : Math.Max(evaluation.VisualHealth, Math.Max(evaluation.MovieHealth, evaluation.OptionalImageHealth));
    }

    private bool IsViableDestination(int primaryHealth)
    {
        return primaryHealth > innerWavHealthThreshold;
    }

    private static int GetPrimaryMatched(ChartResourceSnapshot snapshot, CandidateEvaluation evaluation)
    {
        if (evaluation == null)
        {
            return 0;
        }
        return snapshot.AudioReferenceCount > 0
            ? evaluation.AudioMatched
            : (snapshot.VisualReferenceCount > 0
                ? evaluation.VisualMatched
                : (snapshot.MovieReferenceCount > 0 ? evaluation.MovieMatched : evaluation.OptionalImageMatched));
    }

    private static int GetPrimaryExactMatched(ChartResourceSnapshot snapshot, CandidateEvaluation evaluation)
    {
        if (evaluation == null)
        {
            return 0;
        }
        return snapshot.AudioReferenceCount > 0
            ? evaluation.AudioExactMatched
            : (snapshot.VisualReferenceCount > 0
                ? evaluation.VisualExactMatched
                : (snapshot.MovieReferenceCount > 0 ? evaluation.MovieExactMatched : evaluation.OptionalImageExactMatched));
    }

    private static void ApplyMetadataTieBreakFrontier(List<CandidateEvaluation> orderedCandidates, InstallEstimationMetadataProfile targetMetadataProfile, Func<string, InstallEstimationMetadataProfile> metadataProfileResolver, InstallEstimationResult result)
    {
        if (orderedCandidates == null || orderedCandidates.Count <= 1 || metadataProfileResolver == null || targetMetadataProfile == null || !targetMetadataProfile.HasAnySignal)
        {
            return;
        }

        CandidateEvaluation topCandidate = orderedCandidates[0];
        if (topCandidate == null || !topCandidate.HasSameRankingMetrics(orderedCandidates[1]))
        {
            return;
        }

        List<CandidateEvaluation> frontier = orderedCandidates
            .TakeWhile((CandidateEvaluation evaluation) => evaluation != null && topCandidate.HasSameRankingMetrics(evaluation))
            .Take(3)
            .ToList();
        if (frontier.Count <= 1)
        {
            return;
        }

        Dictionary<string, InstallEstimationMetadataProfile> metadataProfilesByDirectory = frontier
            .Select((CandidateEvaluation evaluation) => evaluation.DirectoryPath)
            .Where((string path) => !string.IsNullOrWhiteSpace(path))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToDictionary(
                (string path) => path,
                (string path) => metadataProfileResolver(path) ?? InstallEstimationMetadataProfile.Empty,
                StringComparer.OrdinalIgnoreCase);

        result.MetadataFrontierSummary = "candidates=" + string.Join(" || ", frontier.Select((CandidateEvaluation evaluation) => evaluation.DirectoryPath ?? string.Empty));
        frontier.Sort((CandidateEvaluation left, CandidateEvaluation right) => CompareCandidatesByMetadataTieBreak(left, right, targetMetadataProfile, metadataProfilesByDirectory));
        for (int i = 0; i < frontier.Count; i++)
        {
            orderedCandidates[i] = frontier[i];
        }

        CandidateEvaluation metadataWinner = frontier[0];
        InstallEstimationMetadataProfile winnerProfile = GetMetadataProfile(metadataProfilesByDirectory, metadataWinner?.DirectoryPath);
        result.MetadataTieBreakSummary = "winner=" + (metadataWinner?.DirectoryPath ?? string.Empty)
            + " pairMatch=" + (HasExactMetadataPairMatch(targetMetadataProfile, winnerProfile) ? 1 : 0)
            + " titleMatch=" + (HasExactMetadataTitleMatch(targetMetadataProfile, winnerProfile) ? 1 : 0)
            + " artistMatch=" + (HasExactMetadataArtistMatch(targetMetadataProfile, winnerProfile) ? 1 : 0);
    }

    private static bool IsMetadataTieBreakDistinct(CandidateEvaluation selectedCandidateEvaluation, CandidateEvaluation secondCandidateEvaluation, InstallEstimationMetadataProfile targetMetadataProfile, Func<string, InstallEstimationMetadataProfile> metadataProfileResolver)
    {
        if (selectedCandidateEvaluation == null || secondCandidateEvaluation == null || targetMetadataProfile == null || !targetMetadataProfile.HasAnySignal || metadataProfileResolver == null)
        {
            return false;
        }

        InstallEstimationMetadataProfile selectedProfile = metadataProfileResolver(selectedCandidateEvaluation.DirectoryPath) ?? InstallEstimationMetadataProfile.Empty;
        InstallEstimationMetadataProfile secondProfile = metadataProfileResolver(secondCandidateEvaluation.DirectoryPath) ?? InstallEstimationMetadataProfile.Empty;
        bool selectedPairMatch = HasExactMetadataPairMatch(targetMetadataProfile, selectedProfile);
        bool secondPairMatch = HasExactMetadataPairMatch(targetMetadataProfile, secondProfile);
        if (selectedPairMatch && !secondPairMatch && !HasExactMetadataTitleMatch(targetMetadataProfile, secondProfile) && !HasExactMetadataArtistMatch(targetMetadataProfile, secondProfile))
        {
            return true;
        }

        bool selectedTitleMatch = HasExactMetadataTitleMatch(targetMetadataProfile, selectedProfile);
        bool secondTitleMatch = HasExactMetadataTitleMatch(targetMetadataProfile, secondProfile);
        bool selectedArtistMatch = HasExactMetadataArtistMatch(targetMetadataProfile, selectedProfile);
        bool secondArtistMatch = HasExactMetadataArtistMatch(targetMetadataProfile, secondProfile);
        return selectedTitleMatch && !secondTitleMatch && !secondArtistMatch && (selectedPairMatch || selectedArtistMatch);
    }

    private static MetadataValidationResult EvaluateSelectedCandidateMetadata(CandidateEvaluation selectedCandidateEvaluation, InstallEstimationMetadataProfile targetMetadataProfile, Func<string, InstallEstimationMetadataProfile> metadataProfileResolver)
    {
        MetadataValidationResult result = new MetadataValidationResult();
        if (selectedCandidateEvaluation == null || metadataProfileResolver == null || targetMetadataProfile == null || !targetMetadataProfile.HasAnySignal)
        {
            return result;
        }

        InstallEstimationMetadataProfile selectedProfile = metadataProfileResolver(selectedCandidateEvaluation.DirectoryPath) ?? InstallEstimationMetadataProfile.Empty;
        result.IsApplicable = true;
        result.PairExactMatch = HasExactMetadataPairMatch(targetMetadataProfile, selectedProfile);
        result.ArtistExactMatch = HasExactMetadataArtistMatch(targetMetadataProfile, selectedProfile);
        result.TitleMatchKind = GetMetadataTitleMatchKind(targetMetadataProfile, selectedProfile);
        result.Evidence = ClassifyMetadataEvidence(result.PairExactMatch, result.ArtistExactMatch, result.TitleMatchKind);
        result.Summary = "selected=" + (selectedCandidateEvaluation.DirectoryPath ?? string.Empty)
            + " pairMatch=" + (result.PairExactMatch ? 1 : 0)
            + " titleMatch=" + result.TitleMatchKind.ToString().ToLowerInvariant()
            + " artistMatch=" + (result.ArtistExactMatch ? 1 : 0)
            + " evidence=" + result.Evidence.ToString().ToLowerInvariant();
        return result;
    }

    private static int CompareCandidatesByMetadataTieBreak(CandidateEvaluation left, CandidateEvaluation right, InstallEstimationMetadataProfile targetMetadataProfile, IReadOnlyDictionary<string, InstallEstimationMetadataProfile> metadataProfilesByDirectory)
    {
        InstallEstimationMetadataProfile leftProfile = GetMetadataProfile(metadataProfilesByDirectory, left?.DirectoryPath);
        InstallEstimationMetadataProfile rightProfile = GetMetadataProfile(metadataProfilesByDirectory, right?.DirectoryPath);

        int comparison = CompareDescending(HasExactMetadataPairMatch(targetMetadataProfile, leftProfile) ? 1 : 0, HasExactMetadataPairMatch(targetMetadataProfile, rightProfile) ? 1 : 0);
        if (comparison != 0)
        {
            return comparison;
        }

        comparison = CompareDescending(HasExactMetadataTitleMatch(targetMetadataProfile, leftProfile) ? 1 : 0, HasExactMetadataTitleMatch(targetMetadataProfile, rightProfile) ? 1 : 0);
        if (comparison != 0)
        {
            return comparison;
        }

        comparison = CompareDescending(HasExactMetadataArtistMatch(targetMetadataProfile, leftProfile) ? 1 : 0, HasExactMetadataArtistMatch(targetMetadataProfile, rightProfile) ? 1 : 0);
        if (comparison != 0)
        {
            return comparison;
        }

        comparison = CompareDescending((int)GetMetadataTitleMatchKind(targetMetadataProfile, leftProfile), (int)GetMetadataTitleMatchKind(targetMetadataProfile, rightProfile));
        if (comparison != 0)
        {
            return comparison;
        }

        comparison = CompareDescending(leftProfile.PairSupportCount, rightProfile.PairSupportCount);
        if (comparison != 0)
        {
            return comparison;
        }

        comparison = CompareDescending(leftProfile.TitleSupportCount, rightProfile.TitleSupportCount);
        if (comparison != 0)
        {
            return comparison;
        }

        comparison = CompareDescending(leftProfile.ArtistSupportCount, rightProfile.ArtistSupportCount);
        if (comparison != 0)
        {
            return comparison;
        }

        return CompareCandidateEvaluations(left, right);
    }

    private static InstallEstimationMetadataProfile GetMetadataProfile(IReadOnlyDictionary<string, InstallEstimationMetadataProfile> metadataProfilesByDirectory, string directoryPath)
    {
        if (metadataProfilesByDirectory != null && !string.IsNullOrWhiteSpace(directoryPath) && metadataProfilesByDirectory.TryGetValue(directoryPath, out InstallEstimationMetadataProfile profile))
        {
            return profile ?? InstallEstimationMetadataProfile.Empty;
        }
        return InstallEstimationMetadataProfile.Empty;
    }

    private static bool HasExactMetadataPairMatch(InstallEstimationMetadataProfile targetMetadataProfile, InstallEstimationMetadataProfile candidateMetadataProfile)
    {
        return targetMetadataProfile != null
            && candidateMetadataProfile != null
            && !string.IsNullOrWhiteSpace(targetMetadataProfile.DominantNormalizedTitleArtistPair)
            && string.Equals(targetMetadataProfile.DominantNormalizedTitleArtistPair, candidateMetadataProfile.DominantNormalizedTitleArtistPair, StringComparison.Ordinal);
    }

    private static bool HasExactMetadataTitleMatch(InstallEstimationMetadataProfile targetMetadataProfile, InstallEstimationMetadataProfile candidateMetadataProfile)
    {
        return targetMetadataProfile != null
            && candidateMetadataProfile != null
            && !string.IsNullOrWhiteSpace(targetMetadataProfile.DominantNormalizedTitle)
            && string.Equals(targetMetadataProfile.DominantNormalizedTitle, candidateMetadataProfile.DominantNormalizedTitle, StringComparison.Ordinal);
    }

    private static bool HasExactMetadataArtistMatch(InstallEstimationMetadataProfile targetMetadataProfile, InstallEstimationMetadataProfile candidateMetadataProfile)
    {
        return targetMetadataProfile != null
            && candidateMetadataProfile != null
            && !string.IsNullOrWhiteSpace(targetMetadataProfile.DominantNormalizedArtist)
            && string.Equals(targetMetadataProfile.DominantNormalizedArtist, candidateMetadataProfile.DominantNormalizedArtist, StringComparison.Ordinal);
    }

    private static InstallEstimationMetadataTitleMatchKind GetMetadataTitleMatchKind(InstallEstimationMetadataProfile targetMetadataProfile, InstallEstimationMetadataProfile candidateMetadataProfile)
    {
        if (targetMetadataProfile == null || candidateMetadataProfile == null)
        {
            return InstallEstimationMetadataTitleMatchKind.None;
        }
        return InstallEstimationMetadataNormalizer.ClassifyTitleMatch(
            targetMetadataProfile.DominantNormalizedTitle,
            candidateMetadataProfile.DominantNormalizedTitle);
    }

    private static MetadataEvidenceStrength ClassifyMetadataEvidence(bool pairExactMatch, bool artistExactMatch, InstallEstimationMetadataTitleMatchKind titleMatchKind)
    {
        if (pairExactMatch)
        {
            return MetadataEvidenceStrength.Strong;
        }
        if (titleMatchKind == InstallEstimationMetadataTitleMatchKind.Exact)
        {
            return MetadataEvidenceStrength.Strong;
        }
        if (titleMatchKind == InstallEstimationMetadataTitleMatchKind.FuzzyStrong && artistExactMatch)
        {
            return MetadataEvidenceStrength.Strong;
        }
        if (artistExactMatch || titleMatchKind == InstallEstimationMetadataTitleMatchKind.FuzzyStrong || titleMatchKind == InstallEstimationMetadataTitleMatchKind.Weak)
        {
            return MetadataEvidenceStrength.Weak;
        }
        return MetadataEvidenceStrength.None;
    }

    private static int CompareCandidateEvaluations(CandidateEvaluation left, CandidateEvaluation right)
    {
        if (ReferenceEquals(left, right))
        {
            return 0;
        }
        if (left == null)
        {
            return 1;
        }
        if (right == null)
        {
            return -1;
        }

        int comparison = CompareDescending(left.AudioHealth, right.AudioHealth);
        if (comparison != 0)
        {
            return comparison;
        }
        comparison = CompareDescending(left.AudioMatched, right.AudioMatched);
        if (comparison != 0)
        {
            return comparison;
        }
        comparison = CompareRatioForSort(BuildJaccardMetric(left.AudioMatched, left.AudioDefined, left.AudioCandidateCount), BuildJaccardMetric(right.AudioMatched, right.AudioDefined, right.AudioCandidateCount));
        if (comparison != 0)
        {
            return comparison;
        }
        comparison = CompareRatioForSort(BuildPrecisionMetric(left.AudioMatched, left.AudioDefined, left.AudioCandidateCount), BuildPrecisionMetric(right.AudioMatched, right.AudioDefined, right.AudioCandidateCount));
        if (comparison != 0)
        {
            return comparison;
        }

        comparison = CompareDescending(left.VisualHealth, right.VisualHealth);
        if (comparison != 0)
        {
            return comparison;
        }
        comparison = CompareDescending(left.VisualMatched, right.VisualMatched);
        if (comparison != 0)
        {
            return comparison;
        }
        comparison = CompareRatioForSort(BuildJaccardMetric(left.VisualMatched, left.VisualDefined, left.VisualCandidateCount), BuildJaccardMetric(right.VisualMatched, right.VisualDefined, right.VisualCandidateCount));
        if (comparison != 0)
        {
            return comparison;
        }
        comparison = CompareRatioForSort(BuildPrecisionMetric(left.VisualMatched, left.VisualDefined, left.VisualCandidateCount), BuildPrecisionMetric(right.VisualMatched, right.VisualDefined, right.VisualCandidateCount));
        if (comparison != 0)
        {
            return comparison;
        }

        comparison = CompareDescending(left.MovieHealth, right.MovieHealth);
        if (comparison != 0)
        {
            return comparison;
        }
        comparison = CompareDescending(left.MovieMatched, right.MovieMatched);
        if (comparison != 0)
        {
            return comparison;
        }
        comparison = CompareRatioForSort(BuildJaccardMetric(left.MovieMatched, left.MovieDefined, left.MovieCandidateCount), BuildJaccardMetric(right.MovieMatched, right.MovieDefined, right.MovieCandidateCount));
        if (comparison != 0)
        {
            return comparison;
        }
        comparison = CompareRatioForSort(BuildPrecisionMetric(left.MovieMatched, left.MovieDefined, left.MovieCandidateCount), BuildPrecisionMetric(right.MovieMatched, right.MovieDefined, right.MovieCandidateCount));
        if (comparison != 0)
        {
            return comparison;
        }

        comparison = CompareDescending(left.OptionalImageHealth, right.OptionalImageHealth);
        if (comparison != 0)
        {
            return comparison;
        }
        comparison = CompareDescending(left.OptionalImageMatched, right.OptionalImageMatched);
        if (comparison != 0)
        {
            return comparison;
        }
        comparison = CompareRatioForSort(BuildJaccardMetric(left.OptionalImageMatched, left.OptionalImageDefined, left.OptionalImageCandidateCount), BuildJaccardMetric(right.OptionalImageMatched, right.OptionalImageDefined, right.OptionalImageCandidateCount));
        if (comparison != 0)
        {
            return comparison;
        }
        comparison = CompareRatioForSort(BuildPrecisionMetric(left.OptionalImageMatched, left.OptionalImageDefined, left.OptionalImageCandidateCount), BuildPrecisionMetric(right.OptionalImageMatched, right.OptionalImageDefined, right.OptionalImageCandidateCount));
        if (comparison != 0)
        {
            return comparison;
        }

        return StringComparer.OrdinalIgnoreCase.Compare(left.DirectoryPath ?? string.Empty, right.DirectoryPath ?? string.Empty);
    }

    private CandidateHierarchyInfo BuildCandidateHierarchyInfo(IEnumerable<string> candidateDirectories)
    {
        List<string> candidates = (candidateDirectories ?? Enumerable.Empty<string>())
            .Where((string dir) => !string.IsNullOrWhiteSpace(dir))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
        if (candidates.Count <= 1)
        {
            return CandidateHierarchyInfo.Empty;
        }

        HashSet<string> candidateSet = new HashSet<string>(candidates, StringComparer.OrdinalIgnoreCase);
        CandidateHierarchyInfo hierarchyInfo = new CandidateHierarchyInfo();
        foreach (string candidateDir in candidates)
        {
            string parentDirectory = GetParentDirectoryPath(candidateDir);
            while (!string.IsNullOrWhiteSpace(parentDirectory))
            {
                if (candidateSet.Contains(parentDirectory))
                {
                    if (!hierarchyInfo.DescendantsByAncestor.TryGetValue(parentDirectory, out List<string> descendants))
                    {
                        descendants = new List<string>();
                        hierarchyInfo.DescendantsByAncestor[parentDirectory] = descendants;
                    }
                    if (!descendants.Any((string descendantDir) => descendantDir.Equals(candidateDir, StringComparison.OrdinalIgnoreCase)))
                    {
                        descendants.Add(candidateDir);
                    }
                    hierarchyInfo.HierarchyDirectories.Add(parentDirectory);
                    hierarchyInfo.HierarchyDirectories.Add(candidateDir);
                }

                parentDirectory = GetParentDirectoryPath(parentDirectory);
            }
        }

        return hierarchyInfo.HasHierarchy ? hierarchyInfo : CandidateHierarchyInfo.Empty;
    }

    private int GetOrComputeSelfOwnedMatchedTotal(CandidateEvaluation evaluation, ChartResourceSnapshot snapshot, BMSDirectoryFileNameHash folderAllFileList, DirectoryResourceLookupCache directoryLookupCache, DirectoryRelativePathHashIndex relativePathHashIndex, Dictionary<string, int> matchedTotalsByDirectory, EvaluationDiagnostics diagnostics, ref int lazyEvaluationCount)
    {
        if (evaluation == null)
        {
            return 0;
        }
        if (evaluation.SelfOwnedMatchedTotal != CandidateEvaluation.SelfOwnedMatchedTotalUncomputed)
        {
            return evaluation.SelfOwnedMatchedTotal;
        }

        string directoryPath = evaluation.DirectoryPath ?? string.Empty;
        if (matchedTotalsByDirectory != null && matchedTotalsByDirectory.TryGetValue(directoryPath, out int cachedMatchedTotal))
        {
            evaluation.SelfOwnedMatchedTotal = cachedMatchedTotal;
            return cachedMatchedTotal;
        }

        CandidateResourceSource candidateResourceSource = ResolveCandidateResourceSource(
            directoryPath,
            folderAllFileList,
            directoryLookupCache,
            relativePathHashIndex,
            includeRelativeWhenCachePresent: true);
        CandidateResourceView selfOwnedView = CreateCandidateResourceView(
            candidateResourceSource.CacheEntry,
            candidateResourceSource.RelativeEntry,
            candidateResourceSource.FallbackBaseNameHashes,
            includeSelfOwned: true,
            diagnostics: diagnostics);
        Stopwatch matchStopwatch = diagnostics == null ? null : Stopwatch.StartNew();
        int matchedTotal = CountMatchedReferences(snapshot.AudioReferences, selfOwnedView.SelfOwnedAudioBaseNameHashes, null, selfOwnedView.SelfOwnedAudioRelativePathHashes, null)
            + CountMatchedReferences(snapshot.VisualReferences, selfOwnedView.SelfOwnedVisualBaseNameHashes, null, selfOwnedView.SelfOwnedVisualRelativePathHashes, null)
            + CountMatchedReferences(snapshot.MovieReferences, selfOwnedView.SelfOwnedMovieBaseNameHashes, null, selfOwnedView.SelfOwnedMovieRelativePathHashes, null)
            + CountMatchedReferences(snapshot.OptionalImageReferences, selfOwnedView.SelfOwnedVisualBaseNameHashes, null, selfOwnedView.SelfOwnedVisualRelativePathHashes, null);
        if (matchStopwatch != null)
        {
            matchStopwatch.Stop();
            diagnostics.RecordCandidateMatch(matchStopwatch.ElapsedMilliseconds);
        }
        evaluation.SelfOwnedMatchedTotal = matchedTotal;
        if (matchedTotalsByDirectory != null)
        {
            matchedTotalsByDirectory[directoryPath] = matchedTotal;
        }
        lazyEvaluationCount++;
        return matchedTotal;
    }

    private List<CandidateEvaluation> SuppressAncestorShadowCandidates(List<CandidateEvaluation> orderedCandidates, CandidateHierarchyInfo hierarchyInfo, ChartResourceSnapshot snapshot, BMSDirectoryFileNameHash folderAllFileList, DirectoryResourceLookupCache directoryLookupCache, DirectoryRelativePathHashIndex relativePathHashIndex, EvaluationDiagnostics diagnostics, out int ancestorShadowSuppressedCount, out int lazySelfOwnedEvaluationCount)
    {
        ancestorShadowSuppressedCount = 0;
        lazySelfOwnedEvaluationCount = 0;
        if (orderedCandidates == null || orderedCandidates.Count <= 1 || snapshot == null || hierarchyInfo == null || !hierarchyInfo.HasHierarchy)
        {
            return orderedCandidates ?? new List<CandidateEvaluation>();
        }

        Dictionary<string, CandidateEvaluation> evaluationsByDirectory = orderedCandidates
            .Where((CandidateEvaluation evaluation) => evaluation != null && !string.IsNullOrWhiteSpace(evaluation.DirectoryPath))
            .GroupBy((CandidateEvaluation evaluation) => evaluation.DirectoryPath, StringComparer.OrdinalIgnoreCase)
            .ToDictionary((IGrouping<string, CandidateEvaluation> group) => group.Key, (IGrouping<string, CandidateEvaluation> group) => group.First(), StringComparer.OrdinalIgnoreCase);
        HashSet<string> suppressedDirectories = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        Dictionary<string, int> matchedTotalsByDirectory = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);

        foreach (CandidateEvaluation ancestor in orderedCandidates)
        {
            if (ancestor == null
                || suppressedDirectories.Contains(ancestor.DirectoryPath ?? string.Empty)
                || !IsViableDestination(GetPrimaryHealth(snapshot, ancestor)))
            {
                continue;
            }

            IReadOnlyList<string> descendantDirectories = hierarchyInfo.GetDescendants(ancestor.DirectoryPath);
            if (descendantDirectories.Count == 0)
            {
                continue;
            }

            List<CandidateEvaluation> viableDescendants = descendantDirectories
                .Where((string descendantDir) => !string.IsNullOrWhiteSpace(descendantDir) && !suppressedDirectories.Contains(descendantDir))
                .Select((string descendantDir) => evaluationsByDirectory.TryGetValue(descendantDir, out CandidateEvaluation descendant) ? descendant : null)
                .Where((CandidateEvaluation descendant) => descendant != null && IsViableDestination(GetPrimaryHealth(snapshot, descendant)))
                .ToList();
            if (viableDescendants.Count == 0)
            {
                continue;
            }

            if (GetOrComputeSelfOwnedMatchedTotal(ancestor, snapshot, folderAllFileList, directoryLookupCache, relativePathHashIndex, matchedTotalsByDirectory, diagnostics, ref lazySelfOwnedEvaluationCount) != 0)
            {
                continue;
            }

            foreach (CandidateEvaluation descendant in viableDescendants)
            {
                if (GetOrComputeSelfOwnedMatchedTotal(descendant, snapshot, folderAllFileList, directoryLookupCache, relativePathHashIndex, matchedTotalsByDirectory, diagnostics, ref lazySelfOwnedEvaluationCount) <= 0)
                {
                    continue;
                }
                if (descendant.HasSameRankingMetrics(ancestor) || CompareCandidateEvaluations(descendant, ancestor) < 0)
                {
                    suppressedDirectories.Add(ancestor.DirectoryPath ?? string.Empty);
                    break;
                }
            }
        }

        ancestorShadowSuppressedCount = suppressedDirectories.Count;
        if (ancestorShadowSuppressedCount == 0)
        {
            return orderedCandidates;
        }

        return orderedCandidates
            .Where((CandidateEvaluation evaluation) => evaluation != null && !suppressedDirectories.Contains(evaluation.DirectoryPath ?? string.Empty))
            .ToList();
    }

    private static bool IsAncestorDirectory(string ancestorPath, string descendantPath)
    {
        if (string.IsNullOrWhiteSpace(ancestorPath) || string.IsNullOrWhiteSpace(descendantPath))
        {
            return false;
        }
        string normalizedAncestor = ancestorPath.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        string normalizedDescendant = descendantPath.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        if (normalizedAncestor.Length == 0 || normalizedDescendant.Length <= normalizedAncestor.Length)
        {
            return false;
        }
        if (!normalizedDescendant.StartsWith(normalizedAncestor, StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }
        char separator = normalizedDescendant[normalizedAncestor.Length];
        return separator == Path.DirectorySeparatorChar || separator == Path.AltDirectorySeparatorChar;
    }

    private static string GetParentDirectoryPath(string path)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            return null;
        }
        string normalizedPath = path.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        if (normalizedPath.Length == 0)
        {
            return null;
        }
        string parentDirectory = Path.GetDirectoryName(normalizedPath);
        if (string.IsNullOrWhiteSpace(parentDirectory))
        {
            return null;
        }
        return parentDirectory.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
    }

    private static int CompareDescending(int left, int right)
    {
        return right.CompareTo(left);
    }

    private static RatioMetric BuildPrecisionMetric(int matched, int defined, int candidateCount)
    {
        if (defined <= 0)
        {
            return new RatioMetric(2, 1L, 1L);
        }
        if (candidateCount <= 0)
        {
            return new RatioMetric(0, 0L, 1L);
        }
        return new RatioMetric(1, matched, candidateCount);
    }

    private static RatioMetric BuildJaccardMetric(int matched, int defined, int candidateCount)
    {
        if (defined <= 0)
        {
            return new RatioMetric(2, 1L, 1L);
        }
        if (candidateCount <= 0)
        {
            return new RatioMetric(0, 0L, 1L);
        }
        int unionCount = defined + candidateCount - matched;
        if (unionCount <= 0)
        {
            return new RatioMetric(2, 1L, 1L);
        }
        return new RatioMetric(1, matched, unionCount);
    }

    private static int CompareRatioMetric(RatioMetric left, RatioMetric right)
    {
        if (left.State != right.State)
        {
            return left.State.CompareTo(right.State);
        }
        if (left.State != 1)
        {
            return 0;
        }
        long lhs = left.Numerator * right.Denominator;
        long rhs = right.Numerator * left.Denominator;
        return lhs.CompareTo(rhs);
    }

    private static int CompareRatioForSort(RatioMetric left, RatioMetric right)
    {
        return -CompareRatioMetric(left, right);
    }

    private static InstallEstimationCandidate CreateCandidate(CandidateEvaluation evaluation, InstallDestinationRepresentativeMetadata representativeMetadata)
    {
        return new InstallEstimationCandidate
        {
            DirectoryPath = evaluation.DirectoryPath,
            IsSourceCandidate = evaluation.IsSourceCandidate,
            AudioHealth = evaluation.AudioHealth,
            AudioMatched = evaluation.AudioMatched,
            AudioExactMatched = evaluation.AudioExactMatched,
            VisualHealth = evaluation.VisualHealth,
            VisualMatched = evaluation.VisualMatched,
            VisualExactMatched = evaluation.VisualExactMatched,
            MovieHealth = evaluation.MovieHealth,
            MovieMatched = evaluation.MovieMatched,
            MovieExactMatched = evaluation.MovieExactMatched,
            OptionalImageHealth = evaluation.OptionalImageHealth,
            OptionalImageMatched = evaluation.OptionalImageMatched,
            OptionalImageExactMatched = evaluation.OptionalImageExactMatched,
            AudioFileCount = evaluation.AudioFileCount,
            AudioPrecision = evaluation.AudioPrecision,
            AudioJaccard = evaluation.AudioJaccard,
            VisualPrecision = evaluation.VisualPrecision,
            VisualJaccard = evaluation.VisualJaccard,
            MoviePrecision = evaluation.MoviePrecision,
            MovieJaccard = evaluation.MovieJaccard,
            OptionalImagePrecision = evaluation.OptionalImagePrecision,
            OptionalImageJaccard = evaluation.OptionalImageJaccard,
            RepresentativeTitle = representativeMetadata?.Title ?? string.Empty,
            RepresentativeArtist = representativeMetadata?.Artist ?? string.Empty
        };
    }

    private static bool IsBmsHashAvailable(string hash)
    {
        return !string.IsNullOrWhiteSpace(hash);
    }

    private static void RegisterInstalledDirectory(Dictionary<string, HashSet<string>> md5Map, Dictionary<string, HashSet<string>> sha256Map, ISet<string> knownChartDirectories, string md5, string sha256, string chartPath)
    {
        string directoryPath = null;
        try
        {
            directoryPath = DirectoryExt.GetDirectoryNameSimple(chartPath);
        }
        catch
        {
        }
        if (string.IsNullOrWhiteSpace(directoryPath))
        {
            return;
        }
        knownChartDirectories?.Add(directoryPath);
        if (IsBmsHashAvailable(md5))
        {
            if (!md5Map.TryGetValue(md5, out HashSet<string> value))
            {
                value = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                md5Map[md5] = value;
            }
            value.Add(directoryPath);
        }
        if (!string.IsNullOrWhiteSpace(sha256))
        {
            if (!sha256Map.TryGetValue(sha256, out HashSet<string> value2))
            {
                value2 = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                sha256Map[sha256] = value2;
            }
            value2.Add(directoryPath);
        }
    }
}
