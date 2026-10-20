using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using BeMusicSeeker.Models.Utils;

namespace BeMusicSeeker.Models.BmsLibraryInternal;

/// <summary>
/// 捕捉済み使用資源と候補索引から導入先を評価します。同梱込みの充足と候補自身の一致を区別します。
/// </summary>
internal sealed class BmsLibraryInstallEstimationService(BmsLibraryOptionsSnapshot options)
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

        public int CandidateResourceCount { get; set; }

        public int SelfOwnedMatchedTotal { get; set; } = SelfOwnedMatchedTotalUncomputed;

        public int AudioHealth => ComputeHealth(AudioMatched, AudioDefined);

        public int VisualHealth => ComputeHealth(VisualMatched, VisualDefined);

        public int MovieHealth => ComputeHealth(MovieMatched, MovieDefined);

        public int OptionalImageHealth => ComputeHealth(OptionalImageMatched, OptionalImageDefined);

        public int AudioJaccard => ComputeJaccard(AudioExactMatched, AudioDefined, AudioCandidateCount);

        public int VisualJaccard => ComputeJaccard(VisualExactMatched, VisualDefined, VisualCandidateCount);

        public int MovieJaccard => ComputeJaccard(MovieExactMatched, MovieDefined, MovieCandidateCount);

        public int OptionalImageJaccard => ComputeJaccard(OptionalImageExactMatched, OptionalImageDefined, OptionalImageCandidateCount);

        public InstallEstimationMetadataMatch Metadata { get; set; }
        public int UniquePrimaryHashCount { get; set; }

        public string ToSummary() => $"dir={DirectoryPath} audio={AudioMatched}/{AudioDefined} M={AudioExactMatched} J={AudioJaccard} relation={Metadata?.Relation}";

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

        private static int ComputeHealth(int matched, int defined)
        {
            if (defined <= 0)
            {
                return 0;
            }
            return (int)((long)matched * 100 / defined);
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

    private readonly BmsLibraryOptionsSnapshot options = options ?? throw new ArgumentNullException(nameof(options));

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

    /// <summary>既所持ハッシュの一致数から候補を選びます。成功は単独最多の選択を表し、自動設定には別途対象群とのStrongと各譜面の音源許可条件が必要です。</summary>
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

    /// <summary>単独最多または一意に選択済みの所持先を、対象群との共通Strongで評価します。同梱と候補の和集合に各譜面の音源許可条件を適用し、同梱だけで条件を満たせば候補索引の未取得を理由に拒みません。</summary>
    /// <param name="destinationDirectory">ハッシュ候補選択が成功した所持先。</param>
    /// <param name="targetEntries">この推定要求が評価する譜面群。混在パッケージでは未所持群だけを渡します。</param>
    /// <param name="metadataProfileResolver">所持現在値の候補プロファイルを返す既存の解決処理。</param>
    internal InstallEstimationResult EvaluateInstalledChartHashDestination(string destinationDirectory, IEnumerable<PackageChartEntry> targetEntries,
        Func<string, InstallEstimationMetadataProfile> metadataProfileResolver, PackageInstallEstimationSnapshot resourceSnapshot = null,
        DirectoryResourceLookupCache directoryLookupCache = null, InstallEstimationCandidatePolicy candidatePolicy = InstallEstimationCandidatePolicy.RelatedOnly)
    {
        InstallEstimationMetadataProfile target = PackageInstallEstimationSnapshotBuilder.BuildTargetMetadataProfile(targetEntries);
        InstallEstimationMetadataProfile profile = metadataProfileResolver?.Invoke(destinationDirectory) ?? InstallEstimationMetadataProfile.Empty;
        var sourceAudio = InstallEstimationSourceAudio.Evaluate(resourceSnapshot?.DefinedResources, resourceSnapshot?.BundledResources,
            resourceSnapshot?.SourceSurfaceIsComplete == true, resourceSnapshot?.SourceSurfaceScanLimitExceeded == true);
        InstallEstimationMetadataMatch match = new InstallEstimationMetadataMatcher(target, sourceAudio.RelatedRule).Evaluate(profile);
        var result = new InstallEstimationResult
        {
            PendingDirectoryLookupCache = directoryLookupCache,
            CandidatePolicy = candidatePolicy,
            SourceAudio = sourceAudio,
            FinalEvaluationMode = InstallEstimationFinalEvaluationMode.InstalledChartHash,
            HasViableDestination = true,
            CandidateDirectoryCount = 1,
            PendingBundledResources = resourceSnapshot?.BundledResources,
            PendingBundledResourcesComplete = resourceSnapshot?.SourceSurfaceIsComplete == true
        };
        if (candidatePolicy == InstallEstimationCandidatePolicy.KeepUnrelated || match.Relation >= InstallEstimationMetadataRelation.Related)
        {
            result.Candidates.Add(new InstallEstimationCandidate
            {
                DirectoryPath = destinationDirectory,
                IsViableDestination = true,
                MetadataRelation = match.Relation,
                DisplayTitle = match.DisplayTitle,
                DisplayArtist = match.DisplayArtist
            });
            result.SuggestedDestinationDirectories.Add(destinationDirectory);
        }
        EvaluateAudioPermission(result, resourceSnapshot, destinationDirectory, resourceSnapshot?.BundledResources, directoryLookupCache);
        if (ApplySelectedCandidateMetadata(result, EvaluateSelectedCandidateMetadata(destinationDirectory, match)) && result.AudioAutoApplyAllowed)
        {
            result.DestinationDirectory = destinationDirectory;
            result.ShouldAutoApplyDestination = true;
            result.ConfidenceReason = "installed_chart_hash_strong";
            result.SuggestedDestinationDirectories.Clear();
        }
        return result;
    }

    /// <summary>リソース順位と実在ペアの強一致で評価し、強一致を自動適用の必要条件として同じペアの表記を表示へ渡します。</summary>
    public InstallEstimationResult EstimateInstallationDirectory(PackageInstallEstimationSnapshot snapshot, DirectoryResourceLookupCache directoryLookupCache, bool asParallel, ChartInstallationEstimateMode estimateMode, Func<string, InstallEstimationMetadataProfile> metadataProfileResolver = null, Func<string, int> candidateDirectoryUniquePrimaryHashCountResolver = null, InstallEstimationCandidatePolicy candidatePolicy = InstallEstimationCandidatePolicy.RelatedOnly)
    {
        return EstimateInstallationDirectory(snapshot, directoryLookupCache, ResolveCandidateEvaluationDegree(asParallel), estimateMode, metadataProfileResolver, candidateDirectoryUniquePrimaryHashCountResolver, candidateDirectoryOverride: null, candidatePolicy);
    }

    /// <summary>指定候補だけを評価し、共通プロファイルの強一致を自動適用の必要条件としてハッシュ数の補助順位を利用します。</summary>
    internal InstallEstimationResult EstimateInstallationDirectoryForCandidateDirectories(PackageInstallEstimationSnapshot snapshot, IReadOnlyCollection<string> candidateDirectories, DirectoryResourceLookupCache directoryLookupCache, bool asParallel, ChartInstallationEstimateMode estimateMode, Func<string, InstallEstimationMetadataProfile> metadataProfileResolver = null, Func<string, int> candidateDirectoryUniquePrimaryHashCountResolver = null, InstallEstimationCandidatePolicy candidatePolicy = InstallEstimationCandidatePolicy.RelatedOnly)
    {
        return EstimateInstallationDirectoryForCandidateDirectories(snapshot, candidateDirectories, directoryLookupCache, ResolveCandidateEvaluationDegree(asParallel), estimateMode, metadataProfileResolver, candidateDirectoryUniquePrimaryHashCountResolver, candidatePolicy);
    }

    /// <summary>指定候補を明示並列度で評価します。ハッシュ数は同順位の補助条件とし、実在ペアの強一致不成立を救済しません。</summary>
    internal InstallEstimationResult EstimateInstallationDirectoryForCandidateDirectories(PackageInstallEstimationSnapshot snapshot, IReadOnlyCollection<string> candidateDirectories, DirectoryResourceLookupCache directoryLookupCache, int candidateEvaluationDegree, ChartInstallationEstimateMode estimateMode, Func<string, InstallEstimationMetadataProfile> metadataProfileResolver = null, Func<string, int> candidateDirectoryUniquePrimaryHashCountResolver = null, InstallEstimationCandidatePolicy candidatePolicy = InstallEstimationCandidatePolicy.RelatedOnly)
    {
        return EstimateInstallationDirectory(
            snapshot,
            directoryLookupCache,
            candidateEvaluationDegree,
            estimateMode,
            metadataProfileResolver,
            candidateDirectoryUniquePrimaryHashCountResolver,
            candidateDirectoryOverride: candidateDirectories, candidatePolicy);
    }

    /// <summary>明示並列度で評価します。非空TITLE、空許容ARTISTの同値、TITLEのDice係数0.72以上を自動適用の必要条件とします。</summary>
    public InstallEstimationResult EstimateInstallationDirectory(PackageInstallEstimationSnapshot snapshot, DirectoryResourceLookupCache directoryLookupCache, int candidateEvaluationDegree, ChartInstallationEstimateMode estimateMode, Func<string, InstallEstimationMetadataProfile> metadataProfileResolver = null, Func<string, int> candidateDirectoryUniquePrimaryHashCountResolver = null, InstallEstimationCandidatePolicy candidatePolicy = InstallEstimationCandidatePolicy.RelatedOnly)
    {
        return EstimateInstallationDirectory(
            snapshot,
            directoryLookupCache,
            candidateEvaluationDegree,
            estimateMode,
            metadataProfileResolver,
            candidateDirectoryUniquePrimaryHashCountResolver,
            candidateDirectoryOverride: null, candidatePolicy);
    }

    private InstallEstimationResult EstimateInstallationDirectory(PackageInstallEstimationSnapshot snapshot, DirectoryResourceLookupCache directoryLookupCache,
        int candidateEvaluationDegree, ChartInstallationEstimateMode estimateMode, Func<string, InstallEstimationMetadataProfile> metadataProfileResolver,
        Func<string, int> candidateDirectoryUniquePrimaryHashCountResolver, IReadOnlyCollection<string> candidateDirectoryOverride, InstallEstimationCandidatePolicy candidatePolicy)
    {
        var result = new InstallEstimationResult
        {
            PendingDirectoryLookupCache = directoryLookupCache,
            CandidatePolicy = candidatePolicy,
            CandidateEvaluationDegree = NormalizeCandidateEvaluationDegree(candidateEvaluationDegree),
            AudioAutoApplyThreshold = options.InstallDestinationAutoApplyMinAudioHealth
        };
        bool reinstall = estimateMode == ChartInstallationEstimateMode.ReinstallCorrection;
        result.SourceAudio = reinstall ? new(0, null, InstallEstimationSourceAudioStatus.ReinstallNotApplicable)
            : InstallEstimationSourceAudio.Evaluate(snapshot?.DefinedResources, snapshot?.BundledResources, snapshot?.SourceSurfaceIsComplete == true,
                snapshot?.SourceSurfaceScanLimitExceeded == true);
        result.PendingBundledResources = reinstall ? null : snapshot?.BundledResources;
        result.PendingBundledResourcesComplete = reinstall || snapshot?.SourceSurfaceIsComplete == true;
        if (snapshot?.RepresentativeChart == null) { result.ConfidenceReason = "input_unavailable"; return result; }
        if (directoryLookupCache == null || directoryLookupCache.Count == 0)
        {
            result.ConfidenceReason = "resource_index_unavailable";
            result.CandidateMode = candidateDirectoryOverride == null ? "resource_index_unavailable" : "candidate_limited_resource_index_unavailable";
            result.CoarseFilterMode = "resource_index_unavailable"; return result;
        }
        ChartResourceSnapshot resources = snapshot.DefinedResources;
        if (resources == null) { result.ConfidenceReason = "resource_references_unavailable"; return result; }
        if (resources.TotalReferenceCount == 0) { result.ConfidenceReason = "no_resource_references"; return result; }
        DirectoryResourceLookupCache.Entry bundled = reinstall ? null : snapshot.BundledResources;
        result.TargetResourceCount = resources.TotalReferenceCount;
        result.TargetResourceHashCount = resources.EnumerateAllRelativePathHashes().Count;
        result.AudioReferenceCount = resources.AudioReferenceCount;
        result.VisualReferenceCount = resources.VisualReferenceCount;
        result.MovieReferenceCount = resources.MovieReferenceCount;
        result.OptionalImageReferenceCount = resources.OptionalImageReferenceCount;
        result.TargetPathAwareAudioHashCount = resources.AudioPathAwareReferenceCount;
        result.TargetPathAwareVisualHashCount = resources.VisualPathAwareReferenceCount;
        result.TargetPathAwareMovieHashCount = resources.MoviePathAwareReferenceCount;
        result.TargetPathAwareOptionalImageHashCount = resources.OptionalImagePathAwareReferenceCount;
        result.TargetPathAwareHashCount = result.TargetPathAwareAudioHashCount + result.TargetPathAwareVisualHashCount
            + result.TargetPathAwareMovieHashCount + result.TargetPathAwareOptionalImageHashCount;
        result.BundledAudioCount = bundled?.AudioFileNameHashCount ?? 0;
        result.BundledImageCount = bundled?.ImageFileNameHashCount ?? 0;
        result.BundledMovieCount = bundled?.MovieFileNameHashCount ?? 0;
        result.AudioMinimumMatchRequired = resources.AudioReferenceCount > 0 ? 1 : 0;
        result.CandidateMode = (reinstall ? "candidate_only" : "package_union") + "_" + candidatePolicy;
        result.CoarseFilterMode = "reverse_match_80_percent";
        HashSet<string> allowed = candidateDirectoryOverride == null ? null : new HashSet<string>(candidateDirectoryOverride, StringComparer.OrdinalIgnoreCase);
        CandidateEvaluation sourceBaseline = reinstall ? EvaluateCurrentDirectory(snapshot.SourceDirectory, resources, directoryLookupCache) : null;
        result.SourceBaselinePrimaryHealth = reinstall ? GetPrimaryHealth(resources, sourceBaseline) : 0;
        var stopwatch = Stopwatch.StartNew();
        Dictionary<string, CandidateEvaluation> matches = AggregateReverseMatches(resources, bundled, directoryLookupCache,
            snapshot.SourceDirectory, allowed, result);
        result.ResourceSummary = "chart=" + snapshot.RepresentativeChart.Path + " chartCount=" + snapshot.ChartCount + " " + result.ResourceSummary;
        result.CandidateDirectoryCountBeforeHashFilter = allowed?.Count ?? directoryLookupCache.Count;
        result.CandidateDirectoryCountAfterBroadFilter = matches.Count;
        List<CandidateEvaluation> candidates = [.. matches.Values.Where(evaluation => IsViableDestination(resources, evaluation))];
        result.CandidateDirectoryCountAfterAudioGate = candidates.Count;
        result.CandidateDirectoryCountAfterHashFilter = candidates.Count;
        result.CandidateDirectoryCount = candidates.Count;
        result.EvaluationMs = stopwatch.ElapsedMilliseconds;
        if (candidates.Count == 0) { SetNoViableDestinationResult(result); return result; }
        result.HasViableDestination = true;
        stopwatch.Restart();
        int comparedPairs = 0;
        void Classify(CandidateEvaluation candidate, InstallEstimationMetadataMatcher matcher)
        {
            InstallEstimationMetadataProfile profile = metadataProfileResolver?.Invoke(candidate.DirectoryPath) ?? InstallEstimationMetadataProfile.Empty;
            candidate.Metadata = matcher.Evaluate(profile);
            candidate.UniquePrimaryHashCount = GetDirectoryUniquePrimaryHashCount(candidateDirectoryUniquePrimaryHashCountResolver, candidate.DirectoryPath);
        }
        if (result.CandidateEvaluationDegree == 1)
        {
            var matcher = new InstallEstimationMetadataMatcher(snapshot.TargetMetadataProfile, result.SourceAudio.RelatedRule);
            foreach (CandidateEvaluation candidate in candidates) { Classify(candidate, matcher); }
            comparedPairs = matcher.PairComparisonCount;
        }
        else
        {
            // 外側がパッケージ並列の場合はdegree=1。単体だけで有界な候補並列を使い、対象依存cacheはworkerごとに閉じる。
            Parallel.ForEach(candidates, new ParallelOptions { MaxDegreeOfParallelism = result.CandidateEvaluationDegree },
                () => new InstallEstimationMetadataMatcher(snapshot.TargetMetadataProfile, result.SourceAudio.RelatedRule),
                (candidate, _, matcher) => { Classify(candidate, matcher); return matcher; },
                matcher => Interlocked.Add(ref comparedPairs, matcher.PairComparisonCount));
        }
        result.MetadataPairComparisonCount = comparedPairs;
        result.MetadataEvaluationMs = stopwatch.ElapsedMilliseconds;
        result.MetadataClassificationSummary = string.Join(" ", Enum.GetValues<InstallEstimationMetadataRelation>()
            .Select(relation => relation + "=" + candidates.Count(candidate => candidate.Metadata.Relation == relation)));
        if (candidatePolicy == InstallEstimationCandidatePolicy.RelatedOnly)
        { candidates.RemoveAll(candidate => candidate.Metadata.Relation < InstallEstimationMetadataRelation.Related); }
        if (candidates.Count == 0)
        { result.ConfidenceReason = "no_related_destination"; return result; }
        candidates.Sort((left, right) => CompareCandidates(left, right, candidatePolicy));
        CandidateHierarchyInfo hierarchy = BuildCandidateHierarchyInfo(candidates.Select(candidate => candidate.DirectoryPath));
        result.HierarchyCandidateDirectoryCount = hierarchy.HierarchyCandidateDirectoryCount;
        stopwatch.Restart();
        candidates = SuppressAncestorShadowCandidates(candidates, hierarchy, resources, directoryLookupCache,
            out int suppressed, out int lazyCount, candidatePolicy);
        result.AncestorShadowSuppressedCount = suppressed;
        result.LazySelfOwnedEvaluationCount = lazyCount;
        result.AncestorShadowEvaluationMs = hierarchy.HasHierarchy ? stopwatch.ElapsedMilliseconds : 0;
        foreach (CandidateEvaluation candidate in candidates.Take(3))
        {
            InstallEstimationCandidate display = CreateCandidate(candidate);
            display.IsViableDestination = true;
            result.Candidates.Add(display);
            result.SuggestedDestinationDirectories.Add(candidate.DirectoryPath);
        }
        CandidateEvaluation selected = candidates[0];
        result.HasViableDestination = true;
        result.SelectedCandidatePrimaryHealth = GetPrimaryHealth(resources, selected);
        result.SelectedCandidateSummary = selected.ToSummary();
        result.TopCandidateSummary = string.Join(" || ", result.Candidates.Select(candidate => candidate.ToSummary()));
        EvaluateAudioPermission(result, snapshot, selected.DirectoryPath, bundled, directoryLookupCache);
        MetadataValidationResult validation = EvaluateSelectedCandidateMetadata(selected.DirectoryPath, selected.Metadata);
        if (!ApplySelectedCandidateMetadata(result, validation)) { return result; }
        CandidateEvaluation competingStrong = candidates.FirstOrDefault(other => !ReferenceEquals(other, selected)
            && other.Metadata.Relation == InstallEstimationMetadataRelation.Strong
            && selected.HasSamePrimaryMetrics(other) && selected.UniquePrimaryHashCount == other.UniquePrimaryHashCount);
        bool ambiguous = competingStrong != null;
        result.DirectoryHashCountTieBreakApplied = !ambiguous && candidateDirectoryUniquePrimaryHashCountResolver != null
            && candidates.Any(other => !ReferenceEquals(other, selected) && other.Metadata.Relation == InstallEstimationMetadataRelation.Strong
                && selected.HasSamePrimaryMetrics(other) && other.UniquePrimaryHashCount != selected.UniquePrimaryHashCount);
        result.DirectoryHashCountTieBreakSummary = candidateDirectoryUniquePrimaryHashCountResolver == null ? string.Empty
            : string.Join(" || ", candidates.Take(3).Select(candidate => candidate.DirectoryPath + ":" + candidate.UniquePrimaryHashCount));
        result.AmbiguousCandidateDirectory = competingStrong?.DirectoryPath;
        if (reinstall && candidates.Count(candidate => candidate.Metadata.Relation == InstallEstimationMetadataRelation.Strong) > 1)
        {
            result.Confidence = InstallEstimationConfidence.Low;
            result.LowConfidenceKind = InstallEstimationLowConfidenceKind.AmbiguousCandidates;
            result.ConfidenceReason = "reinstall_multiple_strong_candidates";
            result.AmbiguousCandidateDirectory = candidates.First(candidate => !ReferenceEquals(candidate, selected) && candidate.Metadata.Relation == InstallEstimationMetadataRelation.Strong).DirectoryPath;
        }
        else if (reinstall)
        {

            if (CompareRatioMetric(GetPrimaryFulfillment(resources, selected), GetPrimaryFulfillment(resources, sourceBaseline)) <= 0)
            {
                result.Confidence = InstallEstimationConfidence.Low;
                result.LowConfidenceKind = InstallEstimationLowConfidenceKind.ReinstallNotImproved;
                result.ConfidenceReason = "reinstall_candidate_not_improved";
                return result;
            }
            result.ShouldAutoApplyDestination = result.AudioAutoApplyAllowed;
            result.ConfidenceReason = "reinstall_single_improved_candidate";
        }
        else
        {
            result.Confidence = ambiguous ? InstallEstimationConfidence.Low : InstallEstimationConfidence.High;
            result.LowConfidenceKind = ambiguous ? InstallEstimationLowConfidenceKind.AmbiguousCandidates : InstallEstimationLowConfidenceKind.None;
            result.ShouldAutoApplyDestination = result.AudioAutoApplyAllowed && (!ambiguous || options.AutoApplyAmbiguousInstallDestination);
            result.ConfidenceReason = ambiguous ? "tie_on_strong_primary_metrics" : "distinct_strong_primary_metrics";
        }
        result.DestinationDirectory = selected.DirectoryPath;
        if (result.ShouldAutoApplyDestination && result.Confidence == InstallEstimationConfidence.High)
        { result.SuggestedDestinationDirectories.Clear(); }
        return result;
    }

    /// <summary>要求キーの逆引き列挙で候補別のMとDを同時集計します。同梱のsはカテゴリごとに一度だけ求めます。</summary>
    private static Dictionary<string, CandidateEvaluation> AggregateReverseMatches(ChartResourceSnapshot resources,
        DirectoryResourceLookupCache.Entry bundled, DirectoryResourceLookupCache cache, string source, HashSet<string> allowed, InstallEstimationResult result)
    {
        var matches = new Dictionary<string, CandidateEvaluation>(StringComparer.OrdinalIgnoreCase);
        cache.EnsureAudioRelativeDirectoriesByHashes(resources.AudioReferences.Select(reference => reference.RelativePathHash));
        cache.EnsureImageRelativeDirectoriesByHashes(resources.VisualReferences.Concat(resources.OptionalImageReferences).Select(reference => reference.RelativePathHash));
        cache.EnsureMovieRelativeDirectoriesByHashes(resources.MovieReferences.Select(reference => reference.RelativePathHash));
        int sAudio = result.SourceAudio.ObservedMatched;
        int sVisual = CountBundled(resources.VisualReferences, bundled?.ImageRelativePathHashes);
        int sMovie = CountBundled(resources.MovieReferences, bundled?.MovieRelativePathHashes);
        int sOptional = CountBundled(resources.OptionalImageReferences, bundled?.ImageRelativePathHashes);
        void Aggregate(IReadOnlyCollection<ChartResourceSnapshot.ResourceReference> references, ISet<uint> supplied,
            Func<uint, IReadOnlyCollection<string>> lookup, int category)
        {
            foreach (ChartResourceSnapshot.ResourceReference reference in references)
            {
                bool hasBundled = supplied?.Contains(reference.RelativePathHash) == true;
                foreach (string directory in lookup(reference.RelativePathHash))
                {
                    result.ReverseLookupVisits++;
                    if (string.Equals(directory, source, StringComparison.OrdinalIgnoreCase) || allowed != null && !allowed.Contains(directory)) { continue; }
                    if (!matches.TryGetValue(directory, out CandidateEvaluation candidate))
                    {
                        DirectoryResourceLookupCache.Entry entry = cache.GetEntryOrNull(directory);
                        if (entry == null) { continue; }
                        candidate = new CandidateEvaluation
                        {
                            DirectoryPath = directory,
                            AudioDefined = resources.AudioReferenceCount,
                            VisualDefined = resources.VisualReferenceCount,
                            MovieDefined = resources.MovieReferenceCount,
                            OptionalImageDefined = resources.OptionalImageReferenceCount,
                            AudioMatched = sAudio,
                            VisualMatched = sVisual,
                            MovieMatched = sMovie,
                            OptionalImageMatched = sOptional,
                            AudioCandidateCount = entry.AudioFileNameHashCount,
                            VisualCandidateCount = entry.ImageFileNameHashCount,
                            MovieCandidateCount = entry.MovieFileNameHashCount,
                            OptionalImageCandidateCount = entry.ImageFileNameHashCount,
                            CandidateResourceCount = entry.AudioFileNameHashCount + entry.ImageFileNameHashCount + entry.MovieFileNameHashCount
                        };
                        matches.Add(directory, candidate);
                    }
                    switch (category)
                    {
                        case 0: candidate.AudioExactMatched++; if (!hasBundled) { candidate.AudioMatched++; } break;
                        case 1: candidate.VisualExactMatched++; if (!hasBundled) { candidate.VisualMatched++; } break;
                        case 2: candidate.MovieExactMatched++; if (!hasBundled) { candidate.MovieMatched++; } break;
                        case 3: candidate.OptionalImageExactMatched++; if (!hasBundled) { candidate.OptionalImageMatched++; } break;
                    }
                }
            }
        }
        Aggregate(resources.AudioReferences, bundled?.AudioRelativePathHashes, cache.GetDirectoriesByAudioRelativeHash, 0);
        Aggregate(resources.VisualReferences, bundled?.ImageRelativePathHashes, cache.GetDirectoriesByImageRelativeHash, 1);
        Aggregate(resources.MovieReferences, bundled?.MovieRelativePathHashes, cache.GetDirectoriesByMovieRelativeHash, 2);
        Aggregate(resources.OptionalImageReferences, bundled?.ImageRelativePathHashes, cache.GetDirectoriesByImageRelativeHash, 3);
        result.ResourceSummary = $"audioRefs={resources.AudioReferenceCount} visualRefs={resources.VisualReferenceCount} movieRefs={resources.MovieReferenceCount} optionalRefs={resources.OptionalImageReferenceCount} bundledRequiredAudio={sAudio} bundledRequiredVisual={sVisual} bundledRequiredMovie={sMovie} bundledRequiredOptional={sOptional}";
        return matches;
    }

    private static int CountBundled(IReadOnlyCollection<ChartResourceSnapshot.ResourceReference> references, ISet<uint> bundled)
        => bundled == null ? 0 : references.Count(reference => bundled.Contains(reference.RelativePathHash));

    /// <summary>各対象譜面の確認結果から完全性と設定許可を別々に集約します。設定外値を丸めません。</summary>
    private void EvaluateAudioPermission(InstallEstimationResult result, PackageInstallEstimationSnapshot snapshot, string directory,
        DirectoryResourceLookupCache.Entry bundled, DirectoryResourceLookupCache cache)
    {
        var placement = new ChartResourcePlacement(ChartResourcePlacementBasis.Destination, directory, bundled, cache?.GetEntryOrNull(directory),
            result.PendingBundledResourcesComplete);
        result.AudioAutoApplyThreshold = options.InstallDestinationAutoApplyMinAudioHealth;
        bool allowed = result.AudioAutoApplyThreshold is >= 90 and <= 100 && snapshot?.SourceSurfaceScanLimitExceeded != true;
        InstallAudioCompleteness completeness = InstallAudioCompleteness.Complete;
        IEnumerable<ChartResourceSnapshot> chartResources = snapshot?.TargetChartResources?.Count > 0
            ? snapshot.TargetChartResources : new[] { snapshot?.DefinedResources };
        foreach (ChartResourceSnapshot resources in chartResources)
        {
            InstallAudioEvaluation evaluation = placement.EvaluateAudioResources(resources);
            allowed &= evaluation.MeetsThreshold(result.AudioAutoApplyThreshold);
            if (evaluation.Completeness == InstallAudioCompleteness.Unverifiable) { completeness = InstallAudioCompleteness.Unverifiable; }
            else if (evaluation.Completeness == InstallAudioCompleteness.Missing && completeness != InstallAudioCompleteness.Unverifiable) { completeness = InstallAudioCompleteness.Missing; }
        }
        result.AudioCompleteness = completeness;
        result.AudioAutoApplyAllowed = allowed;
        result.AudioAutoApplyReason = result.AudioAutoApplyThreshold is < 90 or > 100 ? "invalid_audio_auto_apply_threshold"
            : completeness == InstallAudioCompleteness.Unverifiable ? "audio_unverifiable"
            : snapshot?.SourceSurfaceScanLimitExceeded == true ? "source_surface_limit_exceeded"
            : allowed ? "per_chart_audio_threshold_satisfied" : "per_chart_audio_threshold_not_met";
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

    /// <summary>再導入の現配置だけを比較します。候補全体の集計は逆引きで行い、この要求キー照合を再利用しません。</summary>
    private static CandidateEvaluation EvaluateCurrentDirectory(string directory, ChartResourceSnapshot resources, DirectoryResourceLookupCache cache)
    {
        DirectoryResourceLookupCache.Entry entry = cache?.GetEntryOrNull(directory);
        return new CandidateEvaluation
        {
            DirectoryPath = directory,
            IsSourceCandidate = true,
            AudioDefined = resources.AudioReferenceCount,
            VisualDefined = resources.VisualReferenceCount,
            MovieDefined = resources.MovieReferenceCount,
            OptionalImageDefined = resources.OptionalImageReferenceCount,
            AudioMatched = CountMatchedReferences(resources.AudioReferences, entry?.AudioRelativePathHashes, null),
            VisualMatched = CountMatchedReferences(resources.VisualReferences, entry?.ImageRelativePathHashes, null),
            MovieMatched = CountMatchedReferences(resources.MovieReferences, entry?.MovieRelativePathHashes, null),
            OptionalImageMatched = CountMatchedReferences(resources.OptionalImageReferences, entry?.ImageRelativePathHashes, null)
        };
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

        bool Above(int matched, int defined) => defined > 0 && (long)matched * 100 >= (long)defined * 80;
        return snapshot.AudioReferenceCount > 0 ? evaluation.AudioExactMatched >= 1 && Above(evaluation.AudioMatched, evaluation.AudioDefined)
            : Above(evaluation.VisualMatched, evaluation.VisualDefined) || Above(evaluation.MovieMatched, evaluation.MovieDefined)
                || Above(evaluation.OptionalImageMatched, evaluation.OptionalImageDefined);
    }

    /// <summary>同梱と候補の和集合で音声欠損ゼロを検査します。未取得や定義済み参照の照合不能を空成功へ置き換えません。</summary>
    internal static bool HasCompleteAudio(ChartResourceSnapshot resources, DirectoryResourceLookupCache.Entry bundled, DirectoryResourceLookupCache.Entry candidate)
    {
        return new ChartResourcePlacement(ChartResourcePlacementBasis.Destination, string.Empty, bundled, candidate)
            .EvaluateAudio(resources) == InstallAudioCompleteness.Complete;
    }

    private static MetadataValidationResult EvaluateSelectedCandidateMetadata(string directory, InstallEstimationMetadataMatch match)
        => new()
        {
            IsApplicable = match.Relation != InstallEstimationMetadataRelation.Unavailable,
            IsStrong = match.Relation == InstallEstimationMetadataRelation.Strong,
            Summary = $"selected={directory} relation={match.Relation} titleDice={match.TitleSimilarity} artistDice={match.ArtistSimilarity}"
        };

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
        return false;
    }

    private static int GetDirectoryUniquePrimaryHashCount(Func<string, int> candidateDirectoryUniquePrimaryHashCountResolver, string directoryPath)
    {
        if (candidateDirectoryUniquePrimaryHashCountResolver == null || string.IsNullOrWhiteSpace(directoryPath))
        {
            return 0;
        }
        return Math.Max(0, candidateDirectoryUniquePrimaryHashCountResolver(directoryPath));
    }

    private static int RelationRank(InstallEstimationMetadataRelation relation) => relation switch
    { InstallEstimationMetadataRelation.Strong => 2, InstallEstimationMetadataRelation.Related => 1, _ => 0 };

    private static int ComparePrimaryCategory(int leftMatched, int leftDefined, int leftOwn, int rightMatched, int rightDefined, int rightOwn)
    {
        int compare = CompareRatioForSort(BuildFulfillmentMetric(leftMatched, leftDefined), BuildFulfillmentMetric(rightMatched, rightDefined));
        return compare != 0 || leftDefined == 0 ? compare : CompareDescending(leftOwn, rightOwn);
    }

    private static int ComparePrimary(CandidateEvaluation left, CandidateEvaluation right)
    {
        int compare = ComparePrimaryCategory(left.AudioMatched, left.AudioDefined, left.AudioExactMatched, right.AudioMatched, right.AudioDefined, right.AudioExactMatched);
        if (compare != 0) { return compare; }
        compare = ComparePrimaryCategory(left.VisualMatched, left.VisualDefined, left.VisualExactMatched, right.VisualMatched, right.VisualDefined, right.VisualExactMatched);
        if (compare != 0) { return compare; }
        compare = ComparePrimaryCategory(left.MovieMatched, left.MovieDefined, left.MovieExactMatched, right.MovieMatched, right.MovieDefined, right.MovieExactMatched);
        return compare != 0 ? compare : ComparePrimaryCategory(left.OptionalImageMatched, left.OptionalImageDefined, left.OptionalImageExactMatched, right.OptionalImageMatched, right.OptionalImageDefined, right.OptionalImageExactMatched);
    }

    private static int CompareJaccard(CandidateEvaluation left, CandidateEvaluation right)
    {
        int compare = CompareRatioForSort(BuildJaccardMetric(left.AudioExactMatched, left.AudioDefined, left.AudioCandidateCount), BuildJaccardMetric(right.AudioExactMatched, right.AudioDefined, right.AudioCandidateCount));
        if (compare != 0) { return compare; }
        compare = CompareRatioForSort(BuildJaccardMetric(left.VisualExactMatched, left.VisualDefined, left.VisualCandidateCount), BuildJaccardMetric(right.VisualExactMatched, right.VisualDefined, right.VisualCandidateCount));
        if (compare != 0) { return compare; }
        compare = CompareRatioForSort(BuildJaccardMetric(left.MovieExactMatched, left.MovieDefined, left.MovieCandidateCount), BuildJaccardMetric(right.MovieExactMatched, right.MovieDefined, right.MovieCandidateCount));
        return compare != 0 ? compare : CompareRatioForSort(BuildJaccardMetric(left.OptionalImageExactMatched, left.OptionalImageDefined, left.OptionalImageCandidateCount), BuildJaccardMetric(right.OptionalImageExactMatched, right.OptionalImageDefined, right.OptionalImageCandidateCount));
    }

    private static int CompareCandidates(CandidateEvaluation left, CandidateEvaluation right, InstallEstimationCandidatePolicy policy)
    {
        int relation = CompareDescending(RelationRank(left.Metadata.Relation), RelationRank(right.Metadata.Relation));
        if (policy == InstallEstimationCandidatePolicy.RelatedOnly && relation != 0) { return relation; }
        int compare = ComparePrimary(left, right);
        if (compare != 0) { return compare; }
        if (policy == InstallEstimationCandidatePolicy.RelatedOnly && (compare = CompareDescending(left.UniquePrimaryHashCount, right.UniquePrimaryHashCount)) != 0) { return compare; }
        if ((compare = CompareJaccard(left, right)) != 0) { return compare; }
        if (relation != 0) { return relation; }
        if ((compare = CompareDescending(left.UniquePrimaryHashCount, right.UniquePrimaryHashCount)) != 0) { return compare; }
        return StringComparer.OrdinalIgnoreCase.Compare(left.DirectoryPath, right.DirectoryPath);
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
                    descendants.Add(candidateDir);
                    hierarchyInfo.HierarchyDirectories.Add(parentDirectory);
                    hierarchyInfo.HierarchyDirectories.Add(candidateDir);
                }

                parentDirectory = GetParentDirectoryPath(parentDirectory);
            }
        }

        return hierarchyInfo.HasHierarchy ? hierarchyInfo : CandidateHierarchyInfo.Empty;
    }

    private int GetOrComputeSelfOwnedMatchedTotal(CandidateEvaluation evaluation, ChartResourceSnapshot snapshot, DirectoryResourceLookupCache directoryLookupCache, Dictionary<string, int> matchedTotalsByDirectory, ref int lazyEvaluationCount)
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

        DirectoryResourceLookupCache.Entry entry = directoryLookupCache.GetEntryOrNull(directoryPath);
        int matchedTotal = CountMatchedReferences(snapshot.AudioReferences, entry?.SelfOwnedAudioRelativePathHashes, null)
            + CountMatchedReferences(snapshot.VisualReferences, entry?.SelfOwnedImageRelativePathHashes, null)
            + CountMatchedReferences(snapshot.MovieReferences, entry?.SelfOwnedMovieRelativePathHashes, null)
            + CountMatchedReferences(snapshot.OptionalImageReferences, entry?.SelfOwnedImageRelativePathHashes, null);
        evaluation.SelfOwnedMatchedTotal = matchedTotal;
        if (matchedTotalsByDirectory != null)
        {
            matchedTotalsByDirectory[directoryPath] = matchedTotal;
        }
        lazyEvaluationCount++;
        return matchedTotal;
    }

    private List<CandidateEvaluation> SuppressAncestorShadowCandidates(List<CandidateEvaluation> orderedCandidates, CandidateHierarchyInfo hierarchyInfo, ChartResourceSnapshot snapshot, DirectoryResourceLookupCache directoryLookupCache, out int ancestorShadowSuppressedCount, out int lazySelfOwnedEvaluationCount, InstallEstimationCandidatePolicy policy)
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
                .Where(descendant => descendant != null && IsViableDestination(snapshot, descendant)
                    && RelationRank(descendant.Metadata.Relation) >= RelationRank(ancestor.Metadata.Relation)
                    && (ComparePrimary(descendant, ancestor) == 0 || CompareCandidates(descendant, ancestor, policy) < 0))];
            if (viableDescendants.Count == 0)
            {
                continue;
            }

            if (GetOrComputeSelfOwnedMatchedTotal(ancestor, snapshot, directoryLookupCache, matchedTotalsByDirectory, ref lazySelfOwnedEvaluationCount) != 0)
            {
                continue;
            }

            foreach (CandidateEvaluation descendant in viableDescendants)
            {
                if (GetOrComputeSelfOwnedMatchedTotal(descendant, snapshot, directoryLookupCache, matchedTotalsByDirectory, ref lazySelfOwnedEvaluationCount) <= 0)
                {
                    continue;
                }
                suppressedDirectories.Add(ancestor.DirectoryPath ?? string.Empty);
                break;
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

    private static InstallEstimationCandidate CreateCandidate(CandidateEvaluation evaluation)
    {
        return new InstallEstimationCandidate
        {
            DirectoryPath = evaluation.DirectoryPath,
            IsSourceCandidate = evaluation.IsSourceCandidate,
            AudioHealth = evaluation.AudioHealth,
            AudioRequired = evaluation.AudioDefined,
            AudioMatched = evaluation.AudioMatched,
            AudioExactMatched = evaluation.AudioExactMatched,
            VisualHealth = evaluation.VisualHealth,
            VisualRequired = evaluation.VisualDefined,
            VisualMatched = evaluation.VisualMatched,
            VisualExactMatched = evaluation.VisualExactMatched,
            MovieHealth = evaluation.MovieHealth,
            MovieRequired = evaluation.MovieDefined,
            MovieMatched = evaluation.MovieMatched,
            MovieExactMatched = evaluation.MovieExactMatched,
            OptionalImageHealth = evaluation.OptionalImageHealth,
            OptionalImageRequired = evaluation.OptionalImageDefined,
            OptionalImageMatched = evaluation.OptionalImageMatched,
            OptionalImageExactMatched = evaluation.OptionalImageExactMatched,
            CandidateResourceCount = evaluation.CandidateResourceCount,
            AudioJaccard = evaluation.AudioJaccard,
            VisualJaccard = evaluation.VisualJaccard,
            MovieJaccard = evaluation.MovieJaccard,
            OptionalImageJaccard = evaluation.OptionalImageJaccard,
            DisplayTitle = evaluation.Metadata?.DisplayTitle ?? string.Empty,
            DisplayArtist = evaluation.Metadata?.DisplayArtist ?? string.Empty,
            MetadataRelation = evaluation.Metadata?.Relation ?? InstallEstimationMetadataRelation.Unavailable
        };
    }

}
