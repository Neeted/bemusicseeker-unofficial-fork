using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading;
using BeMusicSeeker.Models.Utils;

namespace BeMusicSeeker.Models.BmsLibraryInternal;

/// <summary>
/// 捕捉済み使用資源と候補索引から導入先を評価します。同梱込みの充足と候補自身の一致を区別します。
/// </summary>
internal sealed class BmsLibraryInstallEstimationService(BmsLibraryOptionsSnapshot options, int innerWavHealthThreshold)
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

    private sealed class MetadataValidationResult
    {
        public bool IsApplicable { get; set; }

        public bool IsStrong { get; set; }

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

        public int AudioPrecision => ComputePrecision(AudioExactMatched, AudioDefined, AudioCandidateCount);

        public int VisualPrecision => ComputePrecision(VisualExactMatched, VisualDefined, VisualCandidateCount);

        public int MoviePrecision => ComputePrecision(MovieExactMatched, MovieDefined, MovieCandidateCount);

        public int OptionalImagePrecision => ComputePrecision(OptionalImageExactMatched, OptionalImageDefined, OptionalImageCandidateCount);

        public int AudioJaccard => ComputeJaccard(AudioExactMatched, AudioDefined, AudioCandidateCount);

        public int VisualJaccard => ComputeJaccard(VisualExactMatched, VisualDefined, VisualCandidateCount);

        public int MovieJaccard => ComputeJaccard(MovieExactMatched, MovieDefined, MovieCandidateCount);

        public int OptionalImageJaccard => ComputeJaccard(OptionalImageExactMatched, OptionalImageDefined, OptionalImageCandidateCount);

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
                && CompareRatioMetric(BuildFulfillmentMetric(AudioMatched, AudioDefined), BuildFulfillmentMetric(other.AudioMatched, other.AudioDefined)) == 0
                && AudioExactMatched == other.AudioExactMatched
                && CompareRatioMetric(BuildFulfillmentMetric(VisualMatched, VisualDefined), BuildFulfillmentMetric(other.VisualMatched, other.VisualDefined)) == 0
                && VisualExactMatched == other.VisualExactMatched
                && CompareRatioMetric(BuildFulfillmentMetric(MovieMatched, MovieDefined), BuildFulfillmentMetric(other.MovieMatched, other.MovieDefined)) == 0
                && MovieExactMatched == other.MovieExactMatched
                && CompareRatioMetric(BuildFulfillmentMetric(OptionalImageMatched, OptionalImageDefined), BuildFulfillmentMetric(other.OptionalImageMatched, other.OptionalImageDefined)) == 0
                && OptionalImageExactMatched == other.OptionalImageExactMatched;
        }

        public bool HasSameRankingMetrics(CandidateEvaluation other)
        {
            return HasSamePrimaryMetrics(other)
                && other != null
                && CompareRatioMetric(BuildJaccardMetric(AudioExactMatched, AudioDefined, AudioCandidateCount), BuildJaccardMetric(other.AudioExactMatched, other.AudioDefined, other.AudioCandidateCount)) == 0
                && CompareRatioMetric(BuildPrecisionMetric(AudioExactMatched, AudioDefined, AudioCandidateCount), BuildPrecisionMetric(other.AudioExactMatched, other.AudioDefined, other.AudioCandidateCount)) == 0
                && CompareRatioMetric(BuildJaccardMetric(VisualExactMatched, VisualDefined, VisualCandidateCount), BuildJaccardMetric(other.VisualExactMatched, other.VisualDefined, other.VisualCandidateCount)) == 0
                && CompareRatioMetric(BuildPrecisionMetric(VisualExactMatched, VisualDefined, VisualCandidateCount), BuildPrecisionMetric(other.VisualExactMatched, other.VisualDefined, other.VisualCandidateCount)) == 0
                && CompareRatioMetric(BuildJaccardMetric(MovieExactMatched, MovieDefined, MovieCandidateCount), BuildJaccardMetric(other.MovieExactMatched, other.MovieDefined, other.MovieCandidateCount)) == 0
                && CompareRatioMetric(BuildPrecisionMetric(MovieExactMatched, MovieDefined, MovieCandidateCount), BuildPrecisionMetric(other.MovieExactMatched, other.MovieDefined, other.MovieCandidateCount)) == 0
                && CompareRatioMetric(BuildJaccardMetric(OptionalImageExactMatched, OptionalImageDefined, OptionalImageCandidateCount), BuildJaccardMetric(other.OptionalImageExactMatched, other.OptionalImageDefined, other.OptionalImageCandidateCount)) == 0
                && CompareRatioMetric(BuildPrecisionMetric(OptionalImageExactMatched, OptionalImageDefined, OptionalImageCandidateCount), BuildPrecisionMetric(other.OptionalImageExactMatched, other.OptionalImageDefined, other.OptionalImageCandidateCount)) == 0;
        }

        private static int ComputeHealth(int matched, int defined)
        {
            if (defined <= 0)
            {
                return 0;
            }
            return (int)((long)matched * 100 / defined);
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
            long unionCount = (long)defined + candidateCount - matched;
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

        public ISet<uint> AudioRelativePathHashes { get; set; } = EmptyHashes;

        public ISet<uint> VisualRelativePathHashes { get; set; } = EmptyHashes;

        public ISet<uint> MovieRelativePathHashes { get; set; } = EmptyHashes;

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
        private static readonly IReadOnlyList<string> EmptyDescendants = [];

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
        internal CandidateResourceSource(DirectoryResourceLookupCache.Entry cacheEntry)
        {
            CacheEntry = cacheEntry;
        }

        internal DirectoryResourceLookupCache.Entry CacheEntry { get; }
    }

    private sealed class EvaluationDiagnostics
    {
        private long candidateViewBuildMs;

        private long candidateMatchMs;

        private int candidateViewBuildCount;

        public long CandidateViewBuildMs => Interlocked.Read(ref candidateViewBuildMs);

        public long CandidateMatchMs => Interlocked.Read(ref candidateMatchMs);

        public int CandidateViewBuildCount => candidateViewBuildCount;

        public void RecordCandidateViewBuild(long elapsedMs)
        {
            Interlocked.Add(ref candidateViewBuildMs, elapsedMs);
            Interlocked.Increment(ref candidateViewBuildCount);
        }

        public void RecordCandidateMatch(long elapsedMs)
        {
            Interlocked.Add(ref candidateMatchMs, elapsedMs);
        }
    }

    private readonly BmsLibraryOptionsSnapshot options = options ?? throw new ArgumentNullException(nameof(options));

    private readonly int innerWavHealthThreshold = innerWavHealthThreshold;

    public InstalledOnlyPackageResolutionResult TryPrepareInstalledOnlyPackageDestination(ChartPackage package, IInstalledChartLookupIndex installedDirectoryIndex)
    {
        var result = new InstalledOnlyPackageResolutionResult();
        if (package == null)
        {
            result.Reason = InstalledDirectoryResolveReason.MissingInstallDestination;
            return result;
        }
        List<PackageChartEntry> packageEntries = [.. (package.ChartEntries ?? [])];
        if (packageEntries.Count == 0 || installedDirectoryIndex == null || installedDirectoryIndex.HashCount == 0)
        {
            result.Reason = InstalledDirectoryResolveReason.MissingInstallDestination;
            return result;
        }
        var distinctDirectories = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (PackageChartEntry item in packageEntries)
        {
            List<string> chartDirectories = GetDistinctInstalledDirectoriesForChart(installedDirectoryIndex, item.Chart);
            if (chartDirectories.Count == 0)
            {
                result.Reason = InstalledDirectoryResolveReason.MissingInstallDestination;
                return result;
            }
            if (chartDirectories.Count > 1)
            {
                result.Reason = InstalledDirectoryResolveReason.ChartHasMultipleInstalledDirectories;
                return result;
            }
            distinctDirectories.Add(chartDirectories[0]);
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

    /// <summary>既所持ハッシュの一致数から候補を選びます。成功は単独最多の選択を表し、自動設定には別途対象群とのStrongと使用音源完全性が必要です。</summary>
    public InstalledDirectoryLookupResult TryResolveInstalledDestinationFromPackage(ChartPackage package, IReadOnlyCollection<PackageChartEntry> missingEntries, IInstalledChartLookupIndex installedDirectoryIndex)
    {
        var result = new InstalledDirectoryLookupResult();
        if (package == null || missingEntries == null || missingEntries.Count == 0)
        {
            result.Reason = InstalledDirectoryResolveReason.InvalidInput;
            return result;
        }
        if (installedDirectoryIndex == null || installedDirectoryIndex.HashCount == 0)
        {
            result.Reason = InstalledDirectoryResolveReason.InstalledIndexEmpty;
            return result;
        }
        var directoryScores = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        foreach (PackageChartEntry item in package.ChartEntries ?? [])
        {
            List<string> directories = GetDistinctInstalledDirectoriesForChart(installedDirectoryIndex, item?.Chart);
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
        List<string> topCandidateDirs = [.. directoryScores
            .Where(kv => kv.Value == maxMatchCount)
            .Select(kv => kv.Key)
            .OrderBy(dir => dir, StringComparer.OrdinalIgnoreCase)];
        result.CandidateDirectories.AddRange(topCandidateDirs);
        foreach (string directoryPath in topCandidateDirs)
        {
            result.CandidateDirectoryUniquePrimaryHashCounts[directoryPath] = installedDirectoryIndex.GetUniquePrimaryHashCountByDirectory(directoryPath);
        }
        if (topCandidateDirs.Count == 1)
        {
            result.InstallDirectory = topCandidateDirs[0];
            return result;
        }
        result.Reason = InstalledDirectoryResolveReason.MultipleCandidateDirectories;
        return result;
    }

    /// <summary>単独最多または一意に選択済みの所持先を、対象群との共通Strongで評価します。同梱と候補の和集合による音声完全性を自動確定の必要条件とし、同梱だけで完全なら候補索引の未取得を理由に拒みません。</summary>
    /// <param name="destinationDirectory">ハッシュ候補選択が成功した所持先。</param>
    /// <param name="targetEntries">この推定要求が評価する譜面群。混在パッケージでは未所持群だけを渡します。</param>
    /// <param name="metadataProfileResolver">所持現在値の候補プロファイルを返す既存の解決処理。</param>
    internal InstallEstimationResult EvaluateInstalledChartHashDestination(string destinationDirectory, IEnumerable<PackageChartEntry> targetEntries, Func<string, InstallEstimationMetadataProfile> metadataProfileResolver, PackageInstallEstimationSnapshot resourceSnapshot = null, DirectoryResourceLookupCache directoryLookupCache = null)
    {
        InstallEstimationMetadataProfile targetProfile = PackageInstallEstimationSnapshotBuilder.BuildTargetMetadataProfile(targetEntries);
        InstallEstimationMetadataProfile candidateProfile = metadataProfileResolver?.Invoke(destinationDirectory) ?? InstallEstimationMetadataProfile.Empty;
        var result = new InstallEstimationResult
        {
            PendingDirectoryLookupCache = directoryLookupCache,
            FinalEvaluationMode = InstallEstimationFinalEvaluationMode.InstalledChartHash,
            HasViableDestination = true,
            PendingBundledResources = resourceSnapshot?.BundledResources,
            PendingBundledResourcesComplete = resourceSnapshot?.SourceSurfaceIsComplete == true,
            CandidateDirectoryCount = 1
        };
        result.Candidates.Add(new InstallEstimationCandidate
        {
            DirectoryPath = destinationDirectory,
            IsViableDestination = true,
            RepresentativeTitle = candidateProfile.DominantPairDisplayTitle,
            RepresentativeArtist = candidateProfile.DominantPairDisplayArtist
        });
        MetadataValidationResult validation = EvaluateSelectedCandidateMetadata(destinationDirectory, targetProfile, candidateProfile);
        result.AudioCompleteness = new ChartResourcePlacement(ChartResourcePlacementBasis.Destination, destinationDirectory,
            resourceSnapshot?.BundledResources, directoryLookupCache?.GetEntryOrNull(destinationDirectory),
            resourceSnapshot?.SourceSurfaceIsComplete == true).EvaluateAudio(resourceSnapshot?.DefinedResources);
        if (ApplySelectedCandidateMetadata(result, validation) && resourceSnapshot?.SourceSurfaceScanLimitExceeded != true
            && result.AudioCompleteness == InstallAudioCompleteness.Complete)
        {
            result.DestinationDirectory = destinationDirectory;
            result.ShouldAutoApplyDestination = true;
            result.ConfidenceReason = "installed_chart_hash_strong";
        }
        if (!result.ShouldAutoApplyDestination && !result.SuggestedDestinationDirectories.Contains(destinationDirectory))
        {
            result.SuggestedDestinationDirectories.Add(destinationDirectory);
        }

        return result;
    }

    private static InstallEstimationFinalEvaluationMode GetFinalEvaluationMode(ChartResourceSnapshot resourceSnapshot)
    {
        return InstallEstimationFinalEvaluationMode.RelativeStrict;
    }


    /// <summary>リソース順位と実在ペアの強一致で評価し、強一致を自動適用の必要条件として同じペアの表記を表示へ渡します。</summary>
    public InstallEstimationResult EstimateInstallationDirectory(PackageInstallEstimationSnapshot snapshot, DirectoryResourceLookupCache directoryLookupCache, bool asParallel, ChartInstallationEstimateMode estimateMode, Func<string, InstallEstimationMetadataProfile> metadataProfileResolver = null, Func<string, int> candidateDirectoryUniquePrimaryHashCountResolver = null)
    {
        return EstimateInstallationDirectory(snapshot, directoryLookupCache, ResolveCandidateEvaluationDegree(asParallel), estimateMode, metadataProfileResolver, candidateDirectoryUniquePrimaryHashCountResolver, candidateDirectoryOverride: null);
    }

    /// <summary>指定候補だけを評価し、共通プロファイルの強一致を自動適用の必要条件としてハッシュ数の補助順位を利用します。</summary>
    internal InstallEstimationResult EstimateInstallationDirectoryForCandidateDirectories(PackageInstallEstimationSnapshot snapshot, IReadOnlyCollection<string> candidateDirectories, DirectoryResourceLookupCache directoryLookupCache, bool asParallel, ChartInstallationEstimateMode estimateMode, Func<string, InstallEstimationMetadataProfile> metadataProfileResolver = null, Func<string, int> candidateDirectoryUniquePrimaryHashCountResolver = null)
    {
        return EstimateInstallationDirectoryForCandidateDirectories(snapshot, candidateDirectories, directoryLookupCache, ResolveCandidateEvaluationDegree(asParallel), estimateMode, metadataProfileResolver, candidateDirectoryUniquePrimaryHashCountResolver);
    }

    /// <summary>指定候補を明示並列度で評価します。ハッシュ数は同順位の補助条件とし、実在ペアの強一致不成立を救済しません。</summary>
    internal InstallEstimationResult EstimateInstallationDirectoryForCandidateDirectories(PackageInstallEstimationSnapshot snapshot, IReadOnlyCollection<string> candidateDirectories, DirectoryResourceLookupCache directoryLookupCache, int candidateEvaluationDegree, ChartInstallationEstimateMode estimateMode, Func<string, InstallEstimationMetadataProfile> metadataProfileResolver = null, Func<string, int> candidateDirectoryUniquePrimaryHashCountResolver = null)
    {
        return EstimateInstallationDirectory(
            snapshot,
            directoryLookupCache,
            candidateEvaluationDegree,
            estimateMode,
            metadataProfileResolver,
            candidateDirectoryUniquePrimaryHashCountResolver,
            candidateDirectoryOverride: candidateDirectories);
    }

    /// <summary>明示並列度で評価します。非空TITLE、空許容ARTISTの同値、TITLEのDice係数0.88以上を自動適用の必要条件とします。</summary>
    public InstallEstimationResult EstimateInstallationDirectory(PackageInstallEstimationSnapshot snapshot, DirectoryResourceLookupCache directoryLookupCache, int candidateEvaluationDegree, ChartInstallationEstimateMode estimateMode, Func<string, InstallEstimationMetadataProfile> metadataProfileResolver = null, Func<string, int> candidateDirectoryUniquePrimaryHashCountResolver = null)
    {
        return EstimateInstallationDirectory(
            snapshot,
            directoryLookupCache,
            candidateEvaluationDegree,
            estimateMode,
            metadataProfileResolver,
            candidateDirectoryUniquePrimaryHashCountResolver,
            candidateDirectoryOverride: null);
    }

    private InstallEstimationResult EstimateInstallationDirectory(PackageInstallEstimationSnapshot snapshot, DirectoryResourceLookupCache directoryLookupCache, int candidateEvaluationDegree, ChartInstallationEstimateMode estimateMode, Func<string, InstallEstimationMetadataProfile> metadataProfileResolver, Func<string, int> candidateDirectoryUniquePrimaryHashCountResolver, IReadOnlyCollection<string> candidateDirectoryOverride)
    {
        var result = new InstallEstimationResult { PendingDirectoryLookupCache = directoryLookupCache };
        int effectiveCandidateEvaluationDegree = NormalizeCandidateEvaluationDegree(candidateEvaluationDegree);
        result.CandidateEvaluationDegree = effectiveCandidateEvaluationDegree;
        bool isReinstallCorrectionMode = estimateMode == ChartInstallationEstimateMode.ReinstallCorrection;
        bool useBundledResources = !isReinstallCorrectionMode;
        if (snapshot?.RepresentativeChart == null)
        {
            return result;
        }
        if (directoryLookupCache == null || directoryLookupCache.Count == 0)
        {
            result.ConfidenceReason = "resource_index_unavailable";
            result.CandidateMode = candidateDirectoryOverride == null
                ? "resource_index_unavailable"
                : "candidate_limited_resource_index_unavailable";
            result.CoarseFilterMode = "resource_index_unavailable";
            return result;
        }
        ChartResourceSnapshot resourceSnapshot = snapshot.DefinedResources ?? new ChartResourceSnapshot();
        InstallEstimationFinalEvaluationMode evaluationMode = GetFinalEvaluationMode(resourceSnapshot);
        DirectoryResourceLookupCache.Entry bundledResources = useBundledResources ? snapshot.BundledResources : null;
        var diagnostics = new EvaluationDiagnostics();
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
        result.PendingBundledResources = bundledResources;
        result.PendingBundledResourcesComplete = !useBundledResources || snapshot.SourceSurfaceIsComplete;
        result.BundledAudioCount = useBundledResources ? snapshot.BundledAudioCount : 0;
        result.BundledImageCount = useBundledResources ? snapshot.BundledImageCount : 0;
        result.BundledMovieCount = useBundledResources ? snapshot.BundledMovieCount : 0;
        result.FinalEvaluationMode = evaluationMode;
        result.CandidateMode = useBundledResources ? "package_union_source_excluded" : "candidate_only_source_excluded";
        if (candidateDirectoryOverride != null)
        {
            result.CandidateMode = "candidate_limited_" + result.CandidateMode;
        }
        result.CoarseFilterMode = "path_aware_audio_gated";
        result.AudioMinimumMatchRequired = GetAudioMinimumMatchRequired(resourceSnapshot);
        result.ResourceSummary = "chart=" + (snapshot.RepresentativeChart.Path ?? string.Empty)
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
                directoryLookupCache,
                bundledResources: null,
                bundledView: null,
                candidateView: null,
                evaluationMode,
                diagnostics: null,
                isSourceCandidate: true);
            result.SourceBaselinePrimaryHealth = GetPrimaryHealth(resourceSnapshot, sourceBaselineEvaluation);
        }
        List<string> allCandidateDirs = [.. (candidateDirectoryOverride ?? directoryLookupCache.Keys)
            .Where(dir => !string.IsNullOrWhiteSpace(dir))
            .Where(dir => !string.Equals(dir, sourceDir, StringComparison.OrdinalIgnoreCase))
            .Distinct(StringComparer.OrdinalIgnoreCase)];
        result.CandidateDirectoryCountBeforeHashFilter = allCandidateDirs.Count;
        if (allCandidateDirs.Count == 0)
        {
            return result;
        }
        HashSet<uint> targetFileHashes = resourceSnapshot.EnumerateAllRelativePathHashes();
        List<string> candidateDirList = allCandidateDirs;
        CandidateResourceView bundledView = evaluationMode == InstallEstimationFinalEvaluationMode.RelativeStrict
            ? CreateCandidateResourceView(bundledResources)
            : null;
        Dictionary<string, CandidateResourceView> candidateViews = null;
        if (targetFileHashes.Count > 0 || result.TargetPathAwareHashCount > 0)
        {
            candidateDirList = ApplyPathAwareBroadFilter(allCandidateDirs, resourceSnapshot, directoryLookupCache);
            result.CandidateDirectoryCountAfterBroadFilter = candidateDirList.Count;
            if (candidateDirList.Count == 0)
            {
                result.CandidateDirectoryCountAfterAudioGate = 0;
                result.CandidateDirectoryCountAfterHashFilter = 0;
                result.CandidateDirectoryCount = 0;
                SetNoViableDestinationResult(result);
                return result;
            }
            candidateViews = CreateCandidateResourceViews(
                candidateDirList,
                directoryLookupCache,
                diagnostics);
            candidateDirList = ApplyAudioCandidateGate(
                candidateDirList,
                resourceSnapshot,
                candidateViews,
                bundledView);
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
        candidateViews ??= CreateCandidateResourceViews(
            candidateDirList,
            directoryLookupCache,
            diagnostics);
        result.CandidateDirectoryCount = candidateDirList.Count;
        CandidateHierarchyInfo hierarchyInfo = BuildCandidateHierarchyInfo(candidateDirList);
        result.HierarchyCandidateDirectoryCount = hierarchyInfo.HierarchyCandidateDirectoryCount;
        var evaluationStopwatch = Stopwatch.StartNew();
        List<CandidateEvaluation> candidateInfos = effectiveCandidateEvaluationDegree == 1
            ? [.. candidateDirList.Select(candidateDir => EvaluateDirectoryCandidate(
                candidateDir,
                resourceSnapshot,
                directoryLookupCache,
                bundledResources,
                bundledView,
                candidateViews[candidateDir],
                evaluationMode,
                diagnostics,
                isSourceCandidate: false))]
            : [.. candidateDirList
                .AsParallel()
                .WithDegreeOfParallelism(effectiveCandidateEvaluationDegree)
                .Select(candidateDir => EvaluateDirectoryCandidate(
                    candidateDir,
                    resourceSnapshot,
                    directoryLookupCache,
                    bundledResources,
                    bundledView,
                    candidateViews[candidateDir],
                    evaluationMode,
                    diagnostics,
                    isSourceCandidate: false))];
        evaluationStopwatch.Stop();
        result.EvaluationMs = evaluationStopwatch.ElapsedMilliseconds;
        if (candidateInfos.Count == 0)
        {
            return result;
        }
        List<CandidateEvaluation> orderedCandidates = [.. candidateInfos];
        orderedCandidates.Sort(CompareCandidateEvaluations);
        var ancestorShadowStopwatch = Stopwatch.StartNew();
        orderedCandidates = SuppressAncestorShadowCandidates(
            orderedCandidates,
            hierarchyInfo,
            resourceSnapshot,
            directoryLookupCache,
            diagnostics,
            out int ancestorShadowSuppressedCount,
            out int lazySelfOwnedEvaluationCount);
        ancestorShadowStopwatch.Stop();
        result.AncestorShadowSuppressedCount = ancestorShadowSuppressedCount;
        result.LazySelfOwnedEvaluationCount = lazySelfOwnedEvaluationCount;
        result.CandidateViewBuildMs = diagnostics.CandidateViewBuildMs;
        result.CandidateMatchMs = diagnostics.CandidateMatchMs;
        result.CandidateViewBuildCount = diagnostics.CandidateViewBuildCount;
        result.AncestorShadowEvaluationMs = hierarchyInfo.HasHierarchy ? ancestorShadowStopwatch.ElapsedMilliseconds : 0L;
        if (orderedCandidates.Count > 0 && IsViableDestination(resourceSnapshot, orderedCandidates[0]))
        {
            ApplyMetadataTieBreakFrontier(orderedCandidates, snapshot.TargetMetadataProfile, metadataProfileResolver, candidateDirectoryUniquePrimaryHashCountResolver, result);
        }
        foreach (InstallEstimationCandidate candidate in orderedCandidates
            .Take(3)
            .Select(delegate (CandidateEvaluation evaluation)
            {
                InstallEstimationMetadataProfile metadataProfile = metadataProfileResolver?.Invoke(evaluation.DirectoryPath) ?? InstallEstimationMetadataProfile.Empty;
                return CreateCandidate(evaluation, metadataProfile);
            }))
        {
            result.Candidates.Add(candidate);
        }
        CandidateEvaluation selectedCandidateEvaluation = orderedCandidates.First();
        CandidateEvaluation secondCandidateEvaluation = orderedCandidates.FirstOrDefault(evaluation => !ReferenceEquals(evaluation, selectedCandidateEvaluation));
        int selectedPrimaryHealth = GetPrimaryHealth(resourceSnapshot, selectedCandidateEvaluation);
        result.SelectedCandidatePrimaryHealth = selectedPrimaryHealth;
        bool selectedViable = IsViableDestination(resourceSnapshot, selectedCandidateEvaluation);
        bool hasMultipleViableCandidates = selectedViable
            && orderedCandidates.Skip(1).Any(evaluation => IsViableDestination(resourceSnapshot, evaluation));
        bool topTwoViableTie = secondCandidateEvaluation != null
            && selectedViable
            && IsViableDestination(resourceSnapshot, secondCandidateEvaluation)
            && selectedCandidateEvaluation.HasSameRankingMetrics(secondCandidateEvaluation);
        bool metadataTieBreakDistinct = !isReinstallCorrectionMode && topTwoViableTie && IsMetadataTieBreakDistinct(selectedCandidateEvaluation, secondCandidateEvaluation, snapshot.TargetMetadataProfile, metadataProfileResolver);
        bool directoryHashCountTieBreakDistinct = !isReinstallCorrectionMode && topTwoViableTie && IsDirectoryHashCountTieBreakDistinct(selectedCandidateEvaluation, secondCandidateEvaluation, snapshot.TargetMetadataProfile, metadataProfileResolver, candidateDirectoryUniquePrimaryHashCountResolver);
        MetadataValidationResult metadataValidation = selectedViable
            ? EvaluateSelectedCandidateMetadata(selectedCandidateEvaluation.DirectoryPath, snapshot.TargetMetadataProfile, metadataProfileResolver?.Invoke(selectedCandidateEvaluation.DirectoryPath) ?? InstallEstimationMetadataProfile.Empty)
            : null;

        for (int i = 0; i < result.Candidates.Count && i < orderedCandidates.Count; i++)
        {
            result.Candidates[i].IsViableDestination = IsViableDestination(resourceSnapshot, orderedCandidates[i]);
        }
        List<string> viableNonSourceSuggestions = [.. result.Candidates
            .Where(candidate => candidate != null && candidate.IsViableDestination)
            .Select(candidate => candidate.DirectoryPath)
            .Where(path => !string.IsNullOrWhiteSpace(path))
            .Distinct(StringComparer.OrdinalIgnoreCase)];
        result.SuggestedDestinationDirectories.Clear();
        result.TopCandidateSummary = string.Join(" || ", result.Candidates.Select(candidate => candidate.ToSummary()));
        result.SelectedCandidateSummary = result.SelectedCandidate?.ToSummary() ?? selectedCandidateEvaluation.ToSummary();

        if (selectedViable)
        {
            result.AudioCompleteness = new ChartResourcePlacement(ChartResourcePlacementBasis.Destination, selectedCandidateEvaluation.DirectoryPath,
                bundledResources, directoryLookupCache.GetEntryOrNull(selectedCandidateEvaluation.DirectoryPath),
                result.PendingBundledResourcesComplete).EvaluateAudio(resourceSnapshot);
        }
        if (!selectedViable)
        {
            SetNoViableDestinationResult(result);
        }
        else if (!ApplySelectedCandidateMetadata(result, metadataValidation))
        {
            result.SuggestedDestinationDirectories.Clear();
            result.SuggestedDestinationDirectories.AddRange(viableNonSourceSuggestions);
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
            else if (directoryHashCountTieBreakDistinct)
            {
                result.Confidence = InstallEstimationConfidence.High;
                result.ConfidenceReason = "directory_hash_count_tiebreak_distinct";
                result.LowConfidenceKind = InstallEstimationLowConfidenceKind.None;
                result.ShouldAutoApplyDestination = true;
                result.DirectoryHashCountTieBreakApplied = true;
            }
            else
            {
                bool autoApplyAmbiguousCandidate =
                    !isReinstallCorrectionMode
                    &&
                    options.AutoApplyAmbiguousInstallDestination
                    && result.SelectedCandidateMetadataEvidenceStrong;
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
            bool reinstallNotImproved = isReinstallCorrectionMode && CompareRatioMetric(GetPrimaryFulfillment(resourceSnapshot, selectedCandidateEvaluation), GetPrimaryFulfillment(resourceSnapshot, sourceBaselineEvaluation)) <= 0;
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
        if (selectedViable && result.AudioCompleteness != InstallAudioCompleteness.Complete)
        {
            result.ShouldAutoApplyDestination = false;
            if (!result.SuggestedDestinationDirectories.Contains(selectedCandidateEvaluation.DirectoryPath))
            {
                result.SuggestedDestinationDirectories.Add(selectedCandidateEvaluation.DirectoryPath);
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
        List<string> candidates = [.. (candidateDirectories ?? [])
            .Where(dir => !string.IsNullOrWhiteSpace(dir))
            .Distinct(StringComparer.OrdinalIgnoreCase)];
        if (candidates.Count == 0 || resourceSnapshot == null || directoryLookupCache == null)
        {
            return candidates;
        }

        var filteredDirectories = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
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

        return [.. candidates.Where(candidateDir => filteredDirectories.Contains(candidateDir))];
    }

    private List<string> ApplyAudioCandidateGate(
        IEnumerable<string> candidateDirectories,
        ChartResourceSnapshot resourceSnapshot,
        IReadOnlyDictionary<string, CandidateResourceView> candidateViews,
        CandidateResourceView bundledView)
    {
        List<string> candidates = [.. (candidateDirectories ?? [])
            .Where(dir => !string.IsNullOrWhiteSpace(dir))
            .Distinct(StringComparer.OrdinalIgnoreCase)];
        if (candidates.Count == 0 || resourceSnapshot == null || candidateViews == null)
        {
            return candidates;
        }

        int requiredAudioMatchCount = GetAudioMinimumMatchRequired(resourceSnapshot);
        if (requiredAudioMatchCount <= 0 || resourceSnapshot.AudioRelativePathHashes.Count == 0)
        {
            return candidates;
        }

        int requiredMatchedForViableHealth = GetRequiredMatchedForViableAudioHealth(resourceSnapshot.AudioReferenceCount);
        bundledView ??= CandidateResourceView.Empty;

        return [.. candidates
            .Where(delegate (string candidateDir)
            {
                CandidateResourceView candidateView = candidateViews[candidateDir];
                if (candidateView.AudioRelativePathHashes.Count == 0)
                {
                    return false;
                }
                if (!HasMinimumAudioReferenceMatches(resourceSnapshot.AudioReferences, candidateView, requiredAudioMatchCount))
                {
                    return false;
                }
                return HasRequiredAudioMatchesForViability(resourceSnapshot.AudioReferences, candidateView, bundledView, requiredMatchedForViableHealth);
            })];
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
            if (IsReferenceMatched(reference, candidateView.AudioRelativePathHashes, null) && ++matched >= requiredAudioMatchCount)
            {
                return true;
            }
        }

        return false;
    }

    private int GetRequiredMatchedForViableAudioHealth(int defined) => defined <= 0
        ? 0 : (int)((long)defined * innerWavHealthThreshold / 100) + 1;

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
        if ((candidateView?.AudioRelativePathHashes == null || candidateView.AudioRelativePathHashes.Count == 0)
            && (bundledView?.AudioRelativePathHashes == null || bundledView.AudioRelativePathHashes.Count == 0))
        {
            return false;
        }

        int matched = 0;
        foreach (ChartResourceSnapshot.ResourceReference reference in targetAudioReferences)
        {
            if (IsReferenceMatched(reference, candidateView?.AudioRelativePathHashes, bundledView?.AudioRelativePathHashes))
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

    public void CorrectChartInstallationDirectory(IEnumerable<PackageChartEntry> chartEntries, Action<PackageChartEntry> searchInstallDestination)
    {
        foreach (PackageChartEntry chartEntry in (chartEntries ?? []).Where(entry => entry?.Chart != null))
        {
            using IDisposable notificationDeferral = chartEntry.DeferPropertyChangedNotifications();
            lock (chartEntry.GetMutationSyncRoot())
            {
                ChartFile chart = chartEntry.Chart;
                if (!string.IsNullOrWhiteSpace(chart.InstallDestination))
                {
                    continue;
                }
                searchInstallDestination?.Invoke(chartEntry);
                chart = chartEntry.Chart;
                if (!string.IsNullOrWhiteSpace(chart.InstallDestination) && chart.InstallDestination.Equals(DirectoryExt.GetDirectoryNameSimple(chart.Path), StringComparison.OrdinalIgnoreCase))
                {
                    chartEntry.SetInstallDestinationPathOnly(null);
                }
            }
        }
    }

    public void ClearInstallDestinations(IEnumerable<PackageChartEntry> chartEntries)
    {
        foreach (PackageChartEntry chartEntry in (chartEntries ?? []).Where(entry => HasInstallDestinationState(entry)))
        {
            chartEntry.ClearInstallDestination();
        }
    }

    private static bool HasInstallDestinationState(PackageChartEntry entry)
    {
        ChartFile chart = entry?.Chart;
        return chart != null
            && (!string.IsNullOrWhiteSpace(chart.InstallDestination)
                || !string.IsNullOrWhiteSpace(chart.InstallDestinationTitle)
                || !string.IsNullOrWhiteSpace(chart.InstallDestinationArtist)
                || (chart.InstallDestinationSuggestions?.Count ?? 0) > 0
                || (chart.Warnings ?? []).Any(warning => warning != null && warning.Category == ChartWarningCategory.InstallEstimation));
    }

    public PendingInstallDestinationSelectionResult ValidateInstallDestination(PackageChartEntry targetEntry, IEnumerable<ChartPackage> pendingPackages, IEnumerable<string> knownChartDirectories, string destinationDirectory, bool allowStandaloneLibraryChart = false)
    {
        var result = new PendingInstallDestinationSelectionResult();
        if (targetEntry?.Chart == null)
        {
            return result;
        }
        ChartPackage package = (pendingPackages ?? [])
            .FirstOrDefault(pkg => pkg != null && pkg.ChartEntries.Any(entry => IsSameChartTarget(entry, targetEntry)));
        result.Package = package;
        if (package == null)
        {
            if (!allowStandaloneLibraryChart)
            {
                result.WarningMessage = Properties.Resources.Warn_PendingPackageNotFound;
                return result;
            }
            result.TargetEntries.Add(targetEntry);
        }
        else
        {
            result.TargetEntries.AddRange(package.ChartEntries.Where(entry => entry?.Chart != null));
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
        string installDirectory = LongPathFileSystem.FileExists(normalizedInput)
            ? DirectoryExt.GetDirectoryNameSimple(normalizedInput)
            : normalizedInput.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        if (!LongPathFileSystem.DirectoryExists(installDirectory))
        {
            result.WarningMessage = string.Format(Properties.Resources.Warn_InstallDirNotFound, installDirectory);
            return result;
        }
        var knownDirectories = new HashSet<string>((knownChartDirectories ?? []).Where(path => !string.IsNullOrWhiteSpace(path)), StringComparer.OrdinalIgnoreCase);
        if (!knownDirectories.Contains(installDirectory))
        {
            result.WarningMessage = string.Format(Properties.Resources.Warn_InstallDirMustContainBms, installDirectory);
            return result;
        }
        result.Success = true;
        result.ValidatedDestinationDirectory = installDirectory;
        return result;
    }

    public static List<string> GetDistinctInstalledDirectoriesForChart(IInstalledChartLookupIndex installedDirectoryIndex, ChartFile chart)
    {
        return GetDistinctInstalledDirectoriesByPrimaryHash(installedDirectoryIndex, ChartLookupKey.GetPrimaryHash(chart));
    }

    public static List<string> GetDistinctInstalledDirectoriesByPrimaryHash(IInstalledChartLookupIndex installedDirectoryIndex, string lookupHash)
    {
        if (installedDirectoryIndex == null || string.IsNullOrWhiteSpace(lookupHash))
        {
            return [];
        }
        return [.. installedDirectoryIndex.GetDistinctDirectoriesByPrimaryHash(lookupHash)];
    }

    public static ChartFile FindChartWithMissingInstalledDirectory(ChartPackage package, IInstalledChartLookupIndex installedDirectoryIndex)
    {
        return (package?.ChartEntries ?? [])
            .FirstOrDefault(entry => entry?.Chart != null && GetDistinctInstalledDirectoriesForChart(installedDirectoryIndex, entry.Chart).Count == 0)
            ?.Chart;
    }

    public static ChartFile FindChartWithMultipleInstalledDirectories(ChartPackage package, IInstalledChartLookupIndex installedDirectoryIndex)
    {
        return (package?.ChartEntries ?? [])
            .FirstOrDefault(entry => entry?.Chart != null && GetDistinctInstalledDirectoriesForChart(installedDirectoryIndex, entry.Chart).Count > 1)
            ?.Chart;
    }

    public static int CountDistinctInstalledDirectoriesForPackage(ChartPackage package, IInstalledChartLookupIndex installedDirectoryIndex)
    {
        return (package?.ChartEntries ?? [])
            .Where(entry => entry?.Chart != null)
            .SelectMany(entry => GetDistinctInstalledDirectoriesForChart(installedDirectoryIndex, entry.Chart))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Count();
    }

    private static bool IsSameChartTarget(PackageChartEntry entry, PackageChartEntry targetEntry)
    {
        return entry?.IsSameChartTarget(targetEntry) == true;
    }

    private static CandidateEvaluation EvaluateCandidate(string candidateDir, ChartResourceSnapshot snapshot, DirectoryResourceLookupCache.Entry entry, DirectoryResourceLookupCache.Entry bundledResources, CandidateResourceView bundledView, DirectoryResourceLookupCache.Entry transientCandidateEntry, CandidateResourceView candidateView, InstallEstimationFinalEvaluationMode evaluationMode, EvaluationDiagnostics diagnostics, bool isSourceCandidate)
    {
        DirectoryResourceLookupCache.Entry candidateEntry = transientCandidateEntry ?? entry;
        return EvaluateCandidateRelativeStrict(
            candidateDir,
            snapshot,
            candidateEntry,
            candidateView,
            bundledView,
            diagnostics,
            isSourceCandidate);
    }

    private static CandidateEvaluation EvaluateDirectoryCandidate(string candidateDir, ChartResourceSnapshot snapshot, DirectoryResourceLookupCache directoryLookupCache, DirectoryResourceLookupCache.Entry bundledResources, CandidateResourceView bundledView, CandidateResourceView candidateView, InstallEstimationFinalEvaluationMode evaluationMode, EvaluationDiagnostics diagnostics, bool isSourceCandidate)
    {
        CandidateResourceSource candidateResourceSource = ResolveCandidateResourceSource(
            candidateDir,
            directoryLookupCache);
        return EvaluateCandidate(
            candidateDir,
            snapshot,
            candidateResourceSource.CacheEntry,
            bundledResources,
            bundledView,
            null,
            candidateView,
            evaluationMode,
            diagnostics,
            isSourceCandidate);
    }

    private static CandidateEvaluation EvaluateCandidateRelativeStrict(string candidateDir, ChartResourceSnapshot snapshot, DirectoryResourceLookupCache.Entry entry, CandidateResourceView candidateView, CandidateResourceView bundledView, EvaluationDiagnostics diagnostics, bool isSourceCandidate)
    {
        candidateView ??= CreateCandidateResourceView(entry, diagnostics: diagnostics);
        bundledView ??= CandidateResourceView.Empty;

        Stopwatch matchStopwatch = diagnostics == null ? null : Stopwatch.StartNew();
        var evaluation = new CandidateEvaluation
        {
            DirectoryPath = candidateDir,
            IsSourceCandidate = isSourceCandidate,
            AudioDefined = snapshot.AudioReferenceCount,
            VisualDefined = snapshot.VisualReferenceCount,
            MovieDefined = snapshot.MovieReferenceCount,
            OptionalImageDefined = snapshot.OptionalImageReferenceCount,
            AudioCandidateCount = candidateView.AudioRelativePathHashes.Count,
            VisualCandidateCount = candidateView.VisualRelativePathHashes.Count,
            MovieCandidateCount = candidateView.MovieRelativePathHashes.Count,
            OptionalImageCandidateCount = candidateView.VisualRelativePathHashes.Count,
            AudioFileCount = CountUnion(candidateView.AudioRelativePathHashes, candidateView.VisualRelativePathHashes, candidateView.MovieRelativePathHashes, bundledView.AudioRelativePathHashes, bundledView.VisualRelativePathHashes, bundledView.MovieRelativePathHashes)
        };
        if (candidateView.TotalResourceCount > 0 || bundledView.TotalResourceCount > 0)
        {
            int audioMatched = CountMatchedReferences(snapshot.AudioReferences, candidateView.AudioRelativePathHashes, bundledView.AudioRelativePathHashes);
            int visualMatched = CountMatchedReferences(snapshot.VisualReferences, candidateView.VisualRelativePathHashes, bundledView.VisualRelativePathHashes);
            int movieMatched = CountMatchedReferences(snapshot.MovieReferences, candidateView.MovieRelativePathHashes, bundledView.MovieRelativePathHashes);
            int optionalMatched = CountMatchedReferences(snapshot.OptionalImageReferences, candidateView.VisualRelativePathHashes, bundledView.VisualRelativePathHashes);
            evaluation.AudioMatched = audioMatched;
            evaluation.AudioExactMatched = CountMatchedReferences(snapshot.AudioReferences, candidateView.AudioRelativePathHashes, null);
            evaluation.VisualMatched = visualMatched;
            evaluation.VisualExactMatched = CountMatchedReferences(snapshot.VisualReferences, candidateView.VisualRelativePathHashes, null);
            evaluation.MovieMatched = movieMatched;
            evaluation.MovieExactMatched = CountMatchedReferences(snapshot.MovieReferences, candidateView.MovieRelativePathHashes, null);
            evaluation.OptionalImageMatched = optionalMatched;
            evaluation.OptionalImageExactMatched = CountMatchedReferences(snapshot.OptionalImageReferences, candidateView.VisualRelativePathHashes, null);
        }

        if (matchStopwatch != null)
        {
            matchStopwatch.Stop();
            diagnostics.RecordCandidateMatch(matchStopwatch.ElapsedMilliseconds);
        }

        return evaluation;
    }

    private static CandidateResourceView CreateCandidateResourceView(DirectoryResourceLookupCache.Entry entry, EvaluationDiagnostics diagnostics = null)
    {
        return CreateCandidateResourceView(entry, includeSelfOwned: false, diagnostics: diagnostics);
    }

    private static CandidateResourceSource ResolveCandidateResourceSource(string directoryPath, DirectoryResourceLookupCache directoryLookupCache)
    {
        DirectoryResourceLookupCache.Entry cacheEntry = directoryLookupCache?.GetEntryOrNull(directoryPath);
        return new CandidateResourceSource(cacheEntry);
    }

    private static CandidateResourceView CreateCandidateResourceView(DirectoryResourceLookupCache.Entry entry, bool includeSelfOwned, EvaluationDiagnostics diagnostics = null)
    {
        if (entry == null)
        {
            return CandidateResourceView.Empty;
        }

        Stopwatch buildStopwatch = diagnostics == null ? null : Stopwatch.StartNew();
        ISet<uint> audioRelativePathHashes = entry.AudioRelativePathHashes;
        ISet<uint> visualRelativePathHashes = entry.ImageRelativePathHashes;
        ISet<uint> movieRelativePathHashes = entry.MovieRelativePathHashes;

        var view = new CandidateResourceView
        {
            AudioRelativePathHashes = audioRelativePathHashes,
            VisualRelativePathHashes = visualRelativePathHashes,
            MovieRelativePathHashes = movieRelativePathHashes,
            AudioResourceCount = entry.AudioRelativePathHashArray.Length,
            VisualResourceCount = entry.ImageRelativePathHashArray.Length,
            MovieResourceCount = entry.MovieRelativePathHashArray.Length,
            TotalResourceCount = CountUnion(audioRelativePathHashes, visualRelativePathHashes, movieRelativePathHashes)
        };
        if (!includeSelfOwned)
        {
            if (buildStopwatch != null)
            {
                buildStopwatch.Stop();
                diagnostics.RecordCandidateViewBuild(buildStopwatch.ElapsedMilliseconds);
            }
            return view;
        }

        ISet<uint> selfOwnedAudioRelativePathHashes = entry.SelfOwnedAudioRelativePathHashes;
        ISet<uint> selfOwnedVisualRelativePathHashes = entry.SelfOwnedImageRelativePathHashes;
        ISet<uint> selfOwnedMovieRelativePathHashes = entry.SelfOwnedMovieRelativePathHashes;
        view.SelfOwnedAudioRelativePathHashes = selfOwnedAudioRelativePathHashes;
        view.SelfOwnedVisualRelativePathHashes = selfOwnedVisualRelativePathHashes;
        view.SelfOwnedMovieRelativePathHashes = selfOwnedMovieRelativePathHashes;
        view.SelfOwnedAudioResourceCount = entry.SelfOwnedAudioRelativePathHashArray.Length;
        view.SelfOwnedVisualResourceCount = entry.SelfOwnedImageRelativePathHashArray.Length;
        view.SelfOwnedMovieResourceCount = entry.SelfOwnedMovieRelativePathHashArray.Length;
        view.SelfOwnedTotalResourceCount = CountUnion(selfOwnedAudioRelativePathHashes, selfOwnedVisualRelativePathHashes, selfOwnedMovieRelativePathHashes);
        if (buildStopwatch != null)
        {
            buildStopwatch.Stop();
            diagnostics.RecordCandidateViewBuild(buildStopwatch.ElapsedMilliseconds);
        }
        return view;
    }

    private static Dictionary<string, CandidateResourceView> CreateCandidateResourceViews(
        IEnumerable<string> candidateDirectories,
        DirectoryResourceLookupCache directoryLookupCache,
        EvaluationDiagnostics diagnostics)
    {
        var result = new Dictionary<string, CandidateResourceView>(StringComparer.OrdinalIgnoreCase);
        foreach (string candidateDirectory in candidateDirectories ?? [])
        {
            result[candidateDirectory] = CreateCandidateResourceView(
                directoryLookupCache?.GetEntryOrNull(candidateDirectory),
                diagnostics);
        }
        return result;
    }

    private static int CountMatchedReferences(IReadOnlyCollection<ChartResourceSnapshot.ResourceReference> targetReferences, ISet<uint> candidateRelativePathHashes, ISet<uint> bundledRelativePathHashes)
    {
        if (targetReferences == null || targetReferences.Count == 0)
        {
            return 0;
        }

        int matched = 0;
        foreach (ChartResourceSnapshot.ResourceReference reference in targetReferences)
        {
            if (IsReferenceMatched(reference, candidateRelativePathHashes, bundledRelativePathHashes))
            {
                matched++;
            }
        }
        return matched;
    }

    private static bool IsReferenceMatched(ChartResourceSnapshot.ResourceReference reference, ISet<uint> candidateRelativePathHashes, ISet<uint> bundledRelativePathHashes)
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
        int total = 0;
        ISet<uint>[] sets = hashSets ?? [];
        for (int setIndex = 0; setIndex < sets.Length; setIndex++)
        {
            ISet<uint> hashSet = sets[setIndex];
            if (hashSet == null || hashSet.Count == 0)
            {
                continue;
            }
            foreach (uint hash in hashSet)
            {
                bool alreadyCounted = false;
                for (int previousIndex = 0; previousIndex < setIndex; previousIndex++)
                {
                    if (sets[previousIndex]?.Contains(hash) == true)
                    {
                        alreadyCounted = true;
                        break;
                    }
                }
                if (!alreadyCounted)
                {
                    total++;
                }
            }
        }
        return total;
    }

    private static RatioMetric GetPrimaryFulfillment(ChartResourceSnapshot snapshot, CandidateEvaluation evaluation)
    {
        if (evaluation == null)
        {
            return new RatioMetric(0, 0, 1);
        }

        if (snapshot.AudioReferenceCount > 0)
        {
            return BuildFulfillmentMetric(evaluation.AudioMatched, evaluation.AudioDefined);
        }

        RatioMetric best = BuildFulfillmentMetric(evaluation.VisualMatched, evaluation.VisualDefined);
        foreach (RatioMetric next in new[] { BuildFulfillmentMetric(evaluation.MovieMatched, evaluation.MovieDefined), BuildFulfillmentMetric(evaluation.OptionalImageMatched, evaluation.OptionalImageDefined) })
        {
            if (CompareRatioMetric(next, best) > 0)
            {
                best = next;
            }
        }

        return best;
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

    private bool IsViableDestination(ChartResourceSnapshot snapshot, CandidateEvaluation evaluation)
    {
        if (evaluation == null)
        {
            return false;
        }

        bool Above(int matched, int defined) => defined > 0 && (long)matched * 100 > (long)defined * innerWavHealthThreshold;
        return snapshot.AudioReferenceCount > 0 ? Above(evaluation.AudioMatched, evaluation.AudioDefined)
            : Above(evaluation.VisualMatched, evaluation.VisualDefined) || Above(evaluation.MovieMatched, evaluation.MovieDefined)
                || Above(evaluation.OptionalImageMatched, evaluation.OptionalImageDefined);
    }

    /// <summary>同梱と候補の和集合で音声欠損ゼロを検査します。未取得や定義済み参照の照合不能を空成功へ置き換えません。</summary>
    internal static bool HasCompleteAudio(ChartResourceSnapshot resources, DirectoryResourceLookupCache.Entry bundled, DirectoryResourceLookupCache.Entry candidate)
    {
        return new ChartResourcePlacement(ChartResourcePlacementBasis.Destination, string.Empty, bundled, candidate)
            .EvaluateAudio(resources) == InstallAudioCompleteness.Complete;
    }

    private static void ApplyMetadataTieBreakFrontier(List<CandidateEvaluation> orderedCandidates, InstallEstimationMetadataProfile targetMetadataProfile, Func<string, InstallEstimationMetadataProfile> metadataProfileResolver, Func<string, int> candidateDirectoryUniquePrimaryHashCountResolver, InstallEstimationResult result)
    {
        if (orderedCandidates == null || orderedCandidates.Count <= 1)
        {
            return;
        }
        bool hasMetadataTieBreakInput = metadataProfileResolver != null && targetMetadataProfile != null && targetMetadataProfile.HasPair;
        if (!hasMetadataTieBreakInput && candidateDirectoryUniquePrimaryHashCountResolver == null)
        {
            return;
        }

        CandidateEvaluation topCandidate = orderedCandidates[0];
        if (topCandidate == null || !topCandidate.HasSameRankingMetrics(orderedCandidates[1]))
        {
            return;
        }

        IEnumerable<CandidateEvaluation> frontierSource = orderedCandidates
            .TakeWhile(evaluation => evaluation != null && topCandidate.HasSameRankingMetrics(evaluation));
        if (candidateDirectoryUniquePrimaryHashCountResolver == null)
        {
            frontierSource = frontierSource.Take(3);
        }
        List<CandidateEvaluation> frontier = [.. frontierSource];
        if (frontier.Count <= 1)
        {
            return;
        }

        var metadataProfilesByDirectory = frontier
            .Select(evaluation => evaluation.DirectoryPath)
            .Where(path => !string.IsNullOrWhiteSpace(path))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToDictionary(
                path => path,
                path => hasMetadataTieBreakInput ? metadataProfileResolver(path) ?? InstallEstimationMetadataProfile.Empty : InstallEstimationMetadataProfile.Empty,
                StringComparer.OrdinalIgnoreCase);

        result.MetadataFrontierSummary = "candidates=" + string.Join(" || ", frontier.Select(evaluation => evaluation.DirectoryPath ?? string.Empty));
        frontier.Sort((left, right) => CompareCandidatesByMetadataTieBreak(left, right, targetMetadataProfile, metadataProfilesByDirectory, candidateDirectoryUniquePrimaryHashCountResolver));
        for (int i = 0; i < frontier.Count; i++)
        {
            orderedCandidates[i] = frontier[i];
        }

        CandidateEvaluation metadataWinner = frontier[0];
        InstallEstimationMetadataProfile winnerProfile = GetMetadataProfile(metadataProfilesByDirectory, metadataWinner?.DirectoryPath);
        result.MetadataTieBreakSummary = "winner=" + (metadataWinner?.DirectoryPath ?? string.Empty)
            + " strong=" + (InstallEstimationMetadataNormalizer.IsStrongMatch(targetMetadataProfile, winnerProfile) ? 1 : 0);
        if (candidateDirectoryUniquePrimaryHashCountResolver != null)
        {
            result.DirectoryHashCountTieBreakSummary = "candidates=" + string.Join(" || ", frontier.Select(evaluation => (evaluation?.DirectoryPath ?? string.Empty) + ":" + GetDirectoryUniquePrimaryHashCount(candidateDirectoryUniquePrimaryHashCountResolver, evaluation?.DirectoryPath)));
        }
    }

    private static bool IsMetadataTieBreakDistinct(CandidateEvaluation selectedCandidateEvaluation, CandidateEvaluation secondCandidateEvaluation, InstallEstimationMetadataProfile targetMetadataProfile, Func<string, InstallEstimationMetadataProfile> metadataProfileResolver)
    {
        if (selectedCandidateEvaluation == null || secondCandidateEvaluation == null || targetMetadataProfile == null || !targetMetadataProfile.HasPair || metadataProfileResolver == null)
        {
            return false;
        }

        InstallEstimationMetadataProfile selectedProfile = metadataProfileResolver(selectedCandidateEvaluation.DirectoryPath) ?? InstallEstimationMetadataProfile.Empty;
        InstallEstimationMetadataProfile secondProfile = metadataProfileResolver(secondCandidateEvaluation.DirectoryPath) ?? InstallEstimationMetadataProfile.Empty;
        return InstallEstimationMetadataNormalizer.IsStrongMatch(targetMetadataProfile, selectedProfile)
            && !InstallEstimationMetadataNormalizer.IsStrongMatch(targetMetadataProfile, secondProfile);
    }

    private static MetadataValidationResult EvaluateSelectedCandidateMetadata(string selectedDirectory, InstallEstimationMetadataProfile targetMetadataProfile, InstallEstimationMetadataProfile selectedProfile)
    {
        bool applicable = targetMetadataProfile?.HasPair == true && selectedProfile.HasPair;
        bool strong = InstallEstimationMetadataNormalizer.IsStrongMatch(targetMetadataProfile, selectedProfile);
        return new MetadataValidationResult
        {
            IsApplicable = applicable,
            IsStrong = strong,
            Summary = "selected=" + (selectedDirectory ?? string.Empty)
                + " applicable=" + (applicable ? 1 : 0)
                + " strong=" + (strong ? 1 : 0)
        };
    }

    /// <summary>共通Strongの診断と拒否結果を反映します。候補選択の成功は維持し、別候補への再探索は行いません。</summary>
    private static bool ApplySelectedCandidateMetadata(InstallEstimationResult result, MetadataValidationResult validation)
    {
        result.MetadataValidationSummary = validation?.Summary ?? string.Empty;
        result.SelectedCandidateMetadataEvidenceStrong = validation?.IsStrong == true;
        if (result.SelectedCandidateMetadataEvidenceStrong)
        {
            return true;
        }
        result.HasViableDestination = true;
        result.DestinationDirectory = null;
        result.Confidence = InstallEstimationConfidence.Low;
        result.LowConfidenceKind = InstallEstimationLowConfidenceKind.MetadataMismatch;
        result.ConfidenceReason = validation?.IsApplicable == true ? "selected_candidate_metadata_mismatch" : "metadata_pair_unavailable";
        result.ShouldAutoApplyDestination = false;
        result.SuggestedDestinationDirectories.AddRange(result.Candidates
            .Where(candidate => candidate.IsViableDestination)
            .Select(candidate => candidate.DirectoryPath));
        return false;
    }

    private static bool IsDirectoryHashCountTieBreakDistinct(CandidateEvaluation selectedCandidateEvaluation, CandidateEvaluation secondCandidateEvaluation, InstallEstimationMetadataProfile targetMetadataProfile, Func<string, InstallEstimationMetadataProfile> metadataProfileResolver, Func<string, int> candidateDirectoryUniquePrimaryHashCountResolver)
    {
        if (selectedCandidateEvaluation == null
            || secondCandidateEvaluation == null
            || candidateDirectoryUniquePrimaryHashCountResolver == null)
        {
            return false;
        }

        var metadataProfilesByDirectory = new Dictionary<string, InstallEstimationMetadataProfile>(StringComparer.OrdinalIgnoreCase);
        bool hasMetadataTieBreakInput = metadataProfileResolver != null && targetMetadataProfile != null && targetMetadataProfile.HasPair;
        if (!string.IsNullOrWhiteSpace(selectedCandidateEvaluation.DirectoryPath))
        {
            metadataProfilesByDirectory[selectedCandidateEvaluation.DirectoryPath] = hasMetadataTieBreakInput ? metadataProfileResolver(selectedCandidateEvaluation.DirectoryPath) ?? InstallEstimationMetadataProfile.Empty : InstallEstimationMetadataProfile.Empty;
        }
        if (!string.IsNullOrWhiteSpace(secondCandidateEvaluation.DirectoryPath))
        {
            metadataProfilesByDirectory[secondCandidateEvaluation.DirectoryPath] = hasMetadataTieBreakInput ? metadataProfileResolver(secondCandidateEvaluation.DirectoryPath) ?? InstallEstimationMetadataProfile.Empty : InstallEstimationMetadataProfile.Empty;
        }
        if (CompareCandidatesByMetadataOnly(selectedCandidateEvaluation, secondCandidateEvaluation, targetMetadataProfile, metadataProfilesByDirectory) != 0)
        {
            return false;
        }
        return GetDirectoryUniquePrimaryHashCount(candidateDirectoryUniquePrimaryHashCountResolver, selectedCandidateEvaluation.DirectoryPath)
            > GetDirectoryUniquePrimaryHashCount(candidateDirectoryUniquePrimaryHashCountResolver, secondCandidateEvaluation.DirectoryPath);
    }

    private static int CompareCandidatesByMetadataTieBreak(CandidateEvaluation left, CandidateEvaluation right, InstallEstimationMetadataProfile targetMetadataProfile, IReadOnlyDictionary<string, InstallEstimationMetadataProfile> metadataProfilesByDirectory, Func<string, int> candidateDirectoryUniquePrimaryHashCountResolver)
    {
        int comparison = CompareCandidatesByMetadataOnly(left, right, targetMetadataProfile, metadataProfilesByDirectory);
        if (comparison != 0)
        {
            return comparison;
        }

        if (candidateDirectoryUniquePrimaryHashCountResolver != null)
        {
            comparison = CompareDescending(
                GetDirectoryUniquePrimaryHashCount(candidateDirectoryUniquePrimaryHashCountResolver, left?.DirectoryPath),
                GetDirectoryUniquePrimaryHashCount(candidateDirectoryUniquePrimaryHashCountResolver, right?.DirectoryPath));
            if (comparison != 0)
            {
                return comparison;
            }
        }

        return CompareCandidateEvaluations(left, right);
    }

    private static int CompareCandidatesByMetadataOnly(CandidateEvaluation left, CandidateEvaluation right, InstallEstimationMetadataProfile targetMetadataProfile, IReadOnlyDictionary<string, InstallEstimationMetadataProfile> metadataProfilesByDirectory)
    {
        InstallEstimationMetadataProfile leftProfile = GetMetadataProfile(metadataProfilesByDirectory, left?.DirectoryPath);
        InstallEstimationMetadataProfile rightProfile = GetMetadataProfile(metadataProfilesByDirectory, right?.DirectoryPath);

        return CompareDescending(
            InstallEstimationMetadataNormalizer.IsStrongMatch(targetMetadataProfile, leftProfile) ? 1 : 0,
            InstallEstimationMetadataNormalizer.IsStrongMatch(targetMetadataProfile, rightProfile) ? 1 : 0);
    }

    private static int GetDirectoryUniquePrimaryHashCount(Func<string, int> candidateDirectoryUniquePrimaryHashCountResolver, string directoryPath)
    {
        if (candidateDirectoryUniquePrimaryHashCountResolver == null || string.IsNullOrWhiteSpace(directoryPath))
        {
            return 0;
        }
        return Math.Max(0, candidateDirectoryUniquePrimaryHashCountResolver(directoryPath));
    }

    private static InstallEstimationMetadataProfile GetMetadataProfile(IReadOnlyDictionary<string, InstallEstimationMetadataProfile> metadataProfilesByDirectory, string directoryPath)
    {
        if (metadataProfilesByDirectory != null && !string.IsNullOrWhiteSpace(directoryPath) && metadataProfilesByDirectory.TryGetValue(directoryPath, out InstallEstimationMetadataProfile profile))
        {
            return profile ?? InstallEstimationMetadataProfile.Empty;
        }
        return InstallEstimationMetadataProfile.Empty;
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

        int comparison = CompareRatioForSort(BuildFulfillmentMetric(left.AudioMatched, left.AudioDefined), BuildFulfillmentMetric(right.AudioMatched, right.AudioDefined));
        if (comparison != 0)
        {
            return comparison;
        }
        comparison = CompareDescending(left.AudioExactMatched, right.AudioExactMatched);
        if (comparison != 0)
        {
            return comparison;
        }
        comparison = CompareRatioForSort(BuildJaccardMetric(left.AudioExactMatched, left.AudioDefined, left.AudioCandidateCount), BuildJaccardMetric(right.AudioExactMatched, right.AudioDefined, right.AudioCandidateCount));
        if (comparison != 0)
        {
            return comparison;
        }
        comparison = CompareRatioForSort(BuildPrecisionMetric(left.AudioExactMatched, left.AudioDefined, left.AudioCandidateCount), BuildPrecisionMetric(right.AudioExactMatched, right.AudioDefined, right.AudioCandidateCount));
        if (comparison != 0)
        {
            return comparison;
        }

        comparison = CompareRatioForSort(BuildFulfillmentMetric(left.VisualMatched, left.VisualDefined), BuildFulfillmentMetric(right.VisualMatched, right.VisualDefined));
        if (comparison != 0)
        {
            return comparison;
        }
        comparison = CompareDescending(left.VisualExactMatched, right.VisualExactMatched);
        if (comparison != 0)
        {
            return comparison;
        }
        comparison = CompareRatioForSort(BuildJaccardMetric(left.VisualExactMatched, left.VisualDefined, left.VisualCandidateCount), BuildJaccardMetric(right.VisualExactMatched, right.VisualDefined, right.VisualCandidateCount));
        if (comparison != 0)
        {
            return comparison;
        }
        comparison = CompareRatioForSort(BuildPrecisionMetric(left.VisualExactMatched, left.VisualDefined, left.VisualCandidateCount), BuildPrecisionMetric(right.VisualExactMatched, right.VisualDefined, right.VisualCandidateCount));
        if (comparison != 0)
        {
            return comparison;
        }

        comparison = CompareRatioForSort(BuildFulfillmentMetric(left.MovieMatched, left.MovieDefined), BuildFulfillmentMetric(right.MovieMatched, right.MovieDefined));
        if (comparison != 0)
        {
            return comparison;
        }
        comparison = CompareDescending(left.MovieExactMatched, right.MovieExactMatched);
        if (comparison != 0)
        {
            return comparison;
        }
        comparison = CompareRatioForSort(BuildJaccardMetric(left.MovieExactMatched, left.MovieDefined, left.MovieCandidateCount), BuildJaccardMetric(right.MovieExactMatched, right.MovieDefined, right.MovieCandidateCount));
        if (comparison != 0)
        {
            return comparison;
        }
        comparison = CompareRatioForSort(BuildPrecisionMetric(left.MovieExactMatched, left.MovieDefined, left.MovieCandidateCount), BuildPrecisionMetric(right.MovieExactMatched, right.MovieDefined, right.MovieCandidateCount));
        if (comparison != 0)
        {
            return comparison;
        }

        comparison = CompareRatioForSort(BuildFulfillmentMetric(left.OptionalImageMatched, left.OptionalImageDefined), BuildFulfillmentMetric(right.OptionalImageMatched, right.OptionalImageDefined));
        if (comparison != 0)
        {
            return comparison;
        }
        comparison = CompareDescending(left.OptionalImageExactMatched, right.OptionalImageExactMatched);
        if (comparison != 0)
        {
            return comparison;
        }
        comparison = CompareRatioForSort(BuildJaccardMetric(left.OptionalImageExactMatched, left.OptionalImageDefined, left.OptionalImageCandidateCount), BuildJaccardMetric(right.OptionalImageExactMatched, right.OptionalImageDefined, right.OptionalImageCandidateCount));
        if (comparison != 0)
        {
            return comparison;
        }
        comparison = CompareRatioForSort(BuildPrecisionMetric(left.OptionalImageExactMatched, left.OptionalImageDefined, left.OptionalImageCandidateCount), BuildPrecisionMetric(right.OptionalImageExactMatched, right.OptionalImageDefined, right.OptionalImageCandidateCount));
        if (comparison != 0)
        {
            return comparison;
        }

        return StringComparer.OrdinalIgnoreCase.Compare(left.DirectoryPath ?? string.Empty, right.DirectoryPath ?? string.Empty);
    }

    private CandidateHierarchyInfo BuildCandidateHierarchyInfo(IEnumerable<string> candidateDirectories)
    {
        List<string> candidates = [.. (candidateDirectories ?? [])
            .Where(dir => !string.IsNullOrWhiteSpace(dir))
            .Distinct(StringComparer.OrdinalIgnoreCase)];
        if (candidates.Count <= 1)
        {
            return CandidateHierarchyInfo.Empty;
        }

        var candidateSet = new HashSet<string>(candidates, StringComparer.OrdinalIgnoreCase);
        var hierarchyInfo = new CandidateHierarchyInfo();
        foreach (string candidateDir in candidates)
        {
            string parentDirectory = GetParentDirectoryPath(candidateDir);
            while (!string.IsNullOrWhiteSpace(parentDirectory))
            {
                if (candidateSet.Contains(parentDirectory))
                {
                    if (!hierarchyInfo.DescendantsByAncestor.TryGetValue(parentDirectory, out List<string> descendants))
                    {
                        descendants = [];
                        hierarchyInfo.DescendantsByAncestor[parentDirectory] = descendants;
                    }
                    if (!descendants.Any(descendantDir => descendantDir.Equals(candidateDir, StringComparison.OrdinalIgnoreCase)))
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

    private int GetOrComputeSelfOwnedMatchedTotal(CandidateEvaluation evaluation, ChartResourceSnapshot snapshot, DirectoryResourceLookupCache directoryLookupCache, Dictionary<string, int> matchedTotalsByDirectory, EvaluationDiagnostics diagnostics, ref int lazyEvaluationCount)
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
            directoryLookupCache);
        CandidateResourceView selfOwnedView = CreateCandidateResourceView(
            candidateResourceSource.CacheEntry,
            includeSelfOwned: true,
            diagnostics: diagnostics);
        Stopwatch matchStopwatch = diagnostics == null ? null : Stopwatch.StartNew();
        int matchedTotal = CountMatchedReferences(snapshot.AudioReferences, selfOwnedView.SelfOwnedAudioRelativePathHashes, null)
            + CountMatchedReferences(snapshot.VisualReferences, selfOwnedView.SelfOwnedVisualRelativePathHashes, null)
            + CountMatchedReferences(snapshot.MovieReferences, selfOwnedView.SelfOwnedMovieRelativePathHashes, null)
            + CountMatchedReferences(snapshot.OptionalImageReferences, selfOwnedView.SelfOwnedVisualRelativePathHashes, null);
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

    private List<CandidateEvaluation> SuppressAncestorShadowCandidates(List<CandidateEvaluation> orderedCandidates, CandidateHierarchyInfo hierarchyInfo, ChartResourceSnapshot snapshot, DirectoryResourceLookupCache directoryLookupCache, EvaluationDiagnostics diagnostics, out int ancestorShadowSuppressedCount, out int lazySelfOwnedEvaluationCount)
    {
        ancestorShadowSuppressedCount = 0;
        lazySelfOwnedEvaluationCount = 0;
        if (orderedCandidates == null || orderedCandidates.Count <= 1 || snapshot == null || hierarchyInfo == null || !hierarchyInfo.HasHierarchy)
        {
            return orderedCandidates ?? [];
        }

        var evaluationsByDirectory = orderedCandidates
            .Where(evaluation => evaluation != null && !string.IsNullOrWhiteSpace(evaluation.DirectoryPath))
            .GroupBy(evaluation => evaluation.DirectoryPath, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(group => group.Key, group => group.First(), StringComparer.OrdinalIgnoreCase);
        var suppressedDirectories = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var matchedTotalsByDirectory = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);

        foreach (CandidateEvaluation ancestor in orderedCandidates)
        {
            if (ancestor == null
                || suppressedDirectories.Contains(ancestor.DirectoryPath ?? string.Empty)
                || !IsViableDestination(snapshot, ancestor))
            {
                continue;
            }

            IReadOnlyList<string> descendantDirectories = hierarchyInfo.GetDescendants(ancestor.DirectoryPath);
            if (descendantDirectories.Count == 0)
            {
                continue;
            }

            List<CandidateEvaluation> viableDescendants = [.. descendantDirectories
                .Where(descendantDir => !string.IsNullOrWhiteSpace(descendantDir) && !suppressedDirectories.Contains(descendantDir))
                .Select(descendantDir => evaluationsByDirectory.TryGetValue(descendantDir, out CandidateEvaluation descendant) ? descendant : null)
                .Where(descendant => descendant != null && IsViableDestination(snapshot, descendant))];
            if (viableDescendants.Count == 0)
            {
                continue;
            }

            if (GetOrComputeSelfOwnedMatchedTotal(ancestor, snapshot, directoryLookupCache, matchedTotalsByDirectory, diagnostics, ref lazySelfOwnedEvaluationCount) != 0)
            {
                continue;
            }

            foreach (CandidateEvaluation descendant in viableDescendants)
            {
                if (GetOrComputeSelfOwnedMatchedTotal(descendant, snapshot, directoryLookupCache, matchedTotalsByDirectory, diagnostics, ref lazySelfOwnedEvaluationCount) <= 0)
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

        return [.. orderedCandidates.Where(evaluation => evaluation != null && !suppressedDirectories.Contains(evaluation.DirectoryPath ?? string.Empty))];
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

    private static RatioMetric BuildFulfillmentMetric(int matched, int defined) => defined <= 0
        ? new RatioMetric(0, 0, 1) : new RatioMetric(1, matched, defined);

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
        long unionCount = (long)defined + candidateCount - matched;
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

    private static InstallEstimationCandidate CreateCandidate(CandidateEvaluation evaluation, InstallEstimationMetadataProfile metadataProfile)
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
            RepresentativeTitle = metadataProfile?.DominantPairDisplayTitle ?? string.Empty,
            RepresentativeArtist = metadataProfile?.DominantPairDisplayArtist ?? string.Empty
        };
    }

}
