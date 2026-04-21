using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using BeMusicSeeker.Models.Utils;
using BeMusicSeeker.Models.LR2;

namespace BeMusicSeeker.Models.BmsLibraryInternal;

/// <summary>
/// Calculates installation destinations against snapshots owned by BMSLibrary.
/// The facade must acquire the required locks before invoking this service.
/// </summary>
internal sealed class BmsLibraryInstallEstimationService
{
    private sealed class CandidateEvaluation
    {
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
                OptionalImageHealth);
        }

        public bool HasSamePrimaryMetrics(CandidateEvaluation other)
        {
            return other != null
                && AudioHealth == other.AudioHealth
                && AudioMatched == other.AudioMatched
                && AudioExactMatched == other.AudioExactMatched
                && VisualHealth == other.VisualHealth
                && VisualMatched == other.VisualMatched
                && VisualExactMatched == other.VisualExactMatched
                && MovieHealth == other.MovieHealth
                && MovieMatched == other.MovieMatched
                && MovieExactMatched == other.MovieExactMatched
                && OptionalImageHealth == other.OptionalImageHealth
                && OptionalImageMatched == other.OptionalImageMatched
                && OptionalImageExactMatched == other.OptionalImageExactMatched;
        }

        public bool HasSameRankingMetrics(CandidateEvaluation other)
        {
            return HasSamePrimaryMetrics(other)
                && other != null
                && AudioJaccard == other.AudioJaccard
                && AudioPrecision == other.AudioPrecision
                && VisualJaccard == other.VisualJaccard
                && VisualPrecision == other.VisualPrecision
                && MovieJaccard == other.MovieJaccard
                && MoviePrecision == other.MoviePrecision
                && OptionalImageJaccard == other.OptionalImageJaccard
                && OptionalImagePrecision == other.OptionalImagePrecision;
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

    private readonly int innerWavHealthThreshold;

    public BmsLibraryInstallEstimationService(BmsLibraryOptionsSnapshot options, int innerWavHealthThreshold)
    {
        _ = options ?? throw new ArgumentNullException(nameof(options));
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
        BMSFile representativeFile = missingFiles.Where((BMSFile file) => file != null).OrderByDescending(GetDefinedResourceCount).FirstOrDefault();
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

    internal HashSet<uint> CollectTargetResourceHashes(IEnumerable<BMSFile> bmsFiles, HashSet<string> installedHashes, BmsInstallationEstimateMode estimateMode)
    {
        return CollectTargetResourceHashes(BuildLooseFileSnapshot(bmsFiles, installedHashes, estimateMode));
    }

    internal HashSet<uint> CollectTargetResourceHashes(PackageInstallEstimationSnapshot snapshot)
    {
        return snapshot?.DefinedResources?.EnumerateAllBaseNameHashes() ?? new HashSet<uint>();
    }

    public InstallEstimationResult EstimateInstallationDirectory(IEnumerable<BMSFile> bmsFiles, HashSet<string> installedHashes, BMSDirectoryFileNameHash folderAllFileList, DirectoryResourceLookupCache directoryLookupCache, bool asParallel, BmsInstallationEstimateMode estimateMode, Func<string, InstallDestinationRepresentativeMetadata> representativeMetadataResolver = null)
    {
        return EstimateInstallationDirectory(BuildLooseFileSnapshot(bmsFiles, installedHashes, estimateMode), folderAllFileList, directoryLookupCache, asParallel, estimateMode, representativeMetadataResolver);
    }

    public InstallEstimationResult EstimateInstallationDirectory(PackageInstallEstimationSnapshot snapshot, BMSDirectoryFileNameHash folderAllFileList, DirectoryResourceLookupCache directoryLookupCache, bool asParallel, BmsInstallationEstimateMode estimateMode, Func<string, InstallDestinationRepresentativeMetadata> representativeMetadataResolver = null)
    {
        InstallEstimationResult result = new InstallEstimationResult();
        bool isMergeMode = estimateMode == BmsInstallationEstimateMode.MergeNoSourceCompensation;
        if (snapshot?.RepresentativeFile == null || folderAllFileList == null)
        {
            return result;
        }
        ChartResourceSnapshot resourceSnapshot = snapshot.DefinedResources ?? new ChartResourceSnapshot();
        result.TargetResourceHashCount = resourceSnapshot.EnumerateAllBaseNameHashes().Count();
        result.BundledAudioCount = snapshot.BundledAudioCount;
        result.BundledImageCount = snapshot.BundledImageCount;
        result.BundledMovieCount = snapshot.BundledMovieCount;
        result.CandidateMode = "package_union";
        result.ResourceSummary = "chart=" + (snapshot.RepresentativeFile.path ?? string.Empty)
            + " chartCount=" + snapshot.ChartCount
            + " audioRefs=" + resourceSnapshot.AudioReferenceCount
            + " visualRefs=" + resourceSnapshot.VisualReferenceCount
            + " movieRefs=" + resourceSnapshot.MovieReferenceCount
            + " optionalRefs=" + resourceSnapshot.OptionalImageReferenceCount
            + " pathSegmentRefs=" + resourceSnapshot.PathSegmentReferenceCount
            + " bundledAudio=" + snapshot.BundledAudioCount
            + " bundledImage=" + snapshot.BundledImageCount
            + " bundledMovie=" + snapshot.BundledMovieCount
            + " candidateMode=" + result.CandidateMode;
        if (resourceSnapshot.TotalReferenceCount == 0 || result.TargetResourceHashCount == 0)
        {
            return result;
        }
        string sourceDir = snapshot.SourceDirectory;
        List<string> allCandidateDirs = folderAllFileList.Keys.ToList();
        if (!string.IsNullOrWhiteSpace(sourceDir) && !allCandidateDirs.Contains(sourceDir, StringComparer.OrdinalIgnoreCase))
        {
            allCandidateDirs.Add(sourceDir);
        }
        result.CandidateDirectoryCountBeforeHashFilter = allCandidateDirs.Count;
        if (allCandidateDirs.Count == 0)
        {
            return result;
        }
        HashSet<uint> targetFileHashes = resourceSnapshot.EnumerateAllBaseNameHashes();
        List<string> candidateDirList = allCandidateDirs;
        if (targetFileHashes.Count > 0)
        {
            if (directoryLookupCache != null)
            {
                directoryLookupCache.EnsureDirectoriesByHashes(targetFileHashes);
                HashSet<string> filteredDirectories = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                foreach (uint targetFileHash in targetFileHashes)
                {
                    filteredDirectories.UnionWith(directoryLookupCache.GetDirectoriesByHash(targetFileHash));
                }
                if (!string.IsNullOrWhiteSpace(sourceDir))
                {
                    filteredDirectories.Add(sourceDir);
                }
                candidateDirList = filteredDirectories.ToList();
            }
            else
            {
                IEnumerable<string> filtered = (asParallel ? allCandidateDirs.AsParallel() : allCandidateDirs.AsParallel().WithDegreeOfParallelism(1))
                    .Where(delegate (string dir)
                    {
                        uint[] fileNameHashArray = folderAllFileList.TryGetCachedFileNameHashArray(dir);
                        if (fileNameHashArray == null || fileNameHashArray.Length == 0)
                        {
                            return false;
                        }
                        for (int i = 0; i < fileNameHashArray.Length; i++)
                        {
                            if (targetFileHashes.Contains(fileNameHashArray[i]))
                            {
                                return true;
                            }
                        }
                        return false;
                    });
                candidateDirList = filtered.ToList();
            }
            result.CandidateDirectoryCountAfterHashFilter = candidateDirList.Count;
            if (candidateDirList.Count == 0)
            {
                candidateDirList = allCandidateDirs;
                result.UsedFallbackCandidateExpansion = true;
            }
        }
        else
        {
            result.CandidateDirectoryCountAfterHashFilter = candidateDirList.Count;
        }
        result.CandidateDirectoryCount = candidateDirList.Count;
        Stopwatch evaluationStopwatch = Stopwatch.StartNew();
        IEnumerable<string> candidateSource = asParallel ? candidateDirList.AsParallel() : candidateDirList.AsParallel().WithDegreeOfParallelism(1);
        List<CandidateEvaluation> candidateInfos = candidateSource
            .Select((string candidateDir) => EvaluateCandidate(
                candidateDir,
                resourceSnapshot,
                folderAllFileList.TryGetCachedFileNameHashArray(candidateDir),
                directoryLookupCache?.GetEntryOrNull(candidateDir),
                snapshot.BundledResources,
                string.Equals(candidateDir, sourceDir, StringComparison.OrdinalIgnoreCase) ? snapshot.SourceCandidateResources : null,
                string.Equals(candidateDir, sourceDir, StringComparison.OrdinalIgnoreCase)))
            .ToList();
        evaluationStopwatch.Stop();
        result.EvaluationMs = evaluationStopwatch.ElapsedMilliseconds;
        if (candidateInfos.Count == 0)
        {
            return result;
        }
        List<CandidateEvaluation> orderedCandidates = candidateInfos.OrderByDescending((CandidateEvaluation evaluation) => evaluation.AudioHealth)
            .ThenByDescending((CandidateEvaluation evaluation) => evaluation.AudioMatched)
            .ThenByDescending((CandidateEvaluation evaluation) => evaluation.AudioExactMatched)
            .ThenByDescending((CandidateEvaluation evaluation) => evaluation.AudioJaccard)
            .ThenByDescending((CandidateEvaluation evaluation) => evaluation.AudioPrecision)
            .ThenByDescending((CandidateEvaluation evaluation) => evaluation.VisualHealth)
            .ThenByDescending((CandidateEvaluation evaluation) => evaluation.VisualMatched)
            .ThenByDescending((CandidateEvaluation evaluation) => evaluation.VisualExactMatched)
            .ThenByDescending((CandidateEvaluation evaluation) => evaluation.VisualJaccard)
            .ThenByDescending((CandidateEvaluation evaluation) => evaluation.VisualPrecision)
            .ThenByDescending((CandidateEvaluation evaluation) => evaluation.MovieHealth)
            .ThenByDescending((CandidateEvaluation evaluation) => evaluation.MovieMatched)
            .ThenByDescending((CandidateEvaluation evaluation) => evaluation.MovieExactMatched)
            .ThenByDescending((CandidateEvaluation evaluation) => evaluation.MovieJaccard)
            .ThenByDescending((CandidateEvaluation evaluation) => evaluation.MoviePrecision)
            .ThenByDescending((CandidateEvaluation evaluation) => evaluation.OptionalImageHealth)
            .ThenByDescending((CandidateEvaluation evaluation) => evaluation.OptionalImageMatched)
            .ThenByDescending((CandidateEvaluation evaluation) => evaluation.OptionalImageExactMatched)
            .ThenByDescending((CandidateEvaluation evaluation) => evaluation.OptionalImageJaccard)
            .ThenByDescending((CandidateEvaluation evaluation) => evaluation.OptionalImagePrecision)
            .ThenBy((CandidateEvaluation evaluation) => evaluation.DirectoryPath, StringComparer.OrdinalIgnoreCase)
            .ToList();
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
        CandidateEvaluation secondCandidateEvaluation = orderedCandidates.Skip(1).FirstOrDefault();
        int selectedPrimaryHealth = GetPrimaryHealth(resourceSnapshot, selectedCandidateEvaluation);
        bool selectedViable = IsViableDestination(selectedPrimaryHealth);
        CandidateEvaluation bestNonSourceCandidateEvaluation = orderedCandidates.FirstOrDefault((CandidateEvaluation evaluation) => !evaluation.IsSourceCandidate);
        int bestNonSourcePrimaryHealth = GetPrimaryHealth(resourceSnapshot, bestNonSourceCandidateEvaluation);
        bool bestNonSourceViable = IsViableDestination(bestNonSourcePrimaryHealth);
        bool topTwoViableTie = secondCandidateEvaluation != null
            && selectedViable
            && IsViableDestination(GetPrimaryHealth(resourceSnapshot, secondCandidateEvaluation))
            && selectedCandidateEvaluation.HasSameRankingMetrics(secondCandidateEvaluation);
        bool sourceVsNonSourceViableTie = selectedCandidateEvaluation.IsSourceCandidate
            && selectedViable
            && bestNonSourceCandidateEvaluation != null
            && bestNonSourceViable
            && selectedCandidateEvaluation.HasSameRankingMetrics(bestNonSourceCandidateEvaluation);

        for (int i = 0; i < result.Candidates.Count && i < orderedCandidates.Count; i++)
        {
            result.Candidates[i].IsViableDestination = IsViableDestination(GetPrimaryHealth(resourceSnapshot, orderedCandidates[i]));
        }
        List<string> viableNonSourceSuggestions = result.Candidates
            .Where((InstallEstimationCandidate candidate) => candidate != null && !candidate.IsSourceCandidate && candidate.IsViableDestination)
            .Select((InstallEstimationCandidate candidate) => candidate.DirectoryPath)
            .Where((string path) => !string.IsNullOrWhiteSpace(path))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
        result.SuggestedDestinationDirectories.Clear();
        result.TopCandidateSummary = string.Join(" || ", result.Candidates.Select((InstallEstimationCandidate candidate) => candidate.ToSummary()));
        result.SelectedCandidateSummary = result.SelectedCandidate?.ToSummary() ?? selectedCandidateEvaluation.ToSummary();

        if (!selectedViable)
        {
            result.HasViableDestination = false;
            result.Confidence = InstallEstimationConfidence.High;
            result.ConfidenceReason = "no_viable_destination_below_threshold";
            result.DestinationDirectory = null;
            result.ShouldAutoApplyDestination = false;
        }
        else if (selectedCandidateEvaluation.IsSourceCandidate)
        {
            result.HasViableDestination = sourceVsNonSourceViableTie;
            result.Confidence = sourceVsNonSourceViableTie ? InstallEstimationConfidence.Low : InstallEstimationConfidence.High;
            result.ConfidenceReason = sourceVsNonSourceViableTie ? "tie_on_primary_metrics" : "source_directory_preferred_no_destination";
            result.DestinationDirectory = null;
            result.ShouldAutoApplyDestination = false;
            if (sourceVsNonSourceViableTie)
            {
                result.SuggestedDestinationDirectories.AddRange(viableNonSourceSuggestions);
            }
        }
        else if (topTwoViableTie)
        {
            result.HasViableDestination = true;
            result.Confidence = InstallEstimationConfidence.Low;
            result.ConfidenceReason = "tie_on_primary_metrics";
            result.DestinationDirectory = selectedCandidateEvaluation.DirectoryPath;
            result.ShouldAutoApplyDestination = false;
            result.SuggestedDestinationDirectories.AddRange(viableNonSourceSuggestions);
        }
        else
        {
            result.HasViableDestination = true;
            result.Confidence = InstallEstimationConfidence.High;
            result.ConfidenceReason = secondCandidateEvaluation == null ? "single_candidate" : "distinct_primary_metrics";
            result.DestinationDirectory = selectedCandidateEvaluation.DirectoryPath;
            result.ShouldAutoApplyDestination = true;
        }
        return result;
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
        bool isCorrectionLikeMode = estimateMode == BmsInstallationEstimateMode.Fix || estimateMode == BmsInstallationEstimateMode.MergeNoSourceCompensation;
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
        bool isFixMode = estimateMode == BmsInstallationEstimateMode.Fix;
        bool isMergeMode = estimateMode == BmsInstallationEstimateMode.MergeNoSourceCompensation;
        bool isCorrectionLikeMode = isFixMode || isMergeMode;
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
        foreach (BMSFile bmsFile in (bmsFiles ?? Enumerable.Empty<BMSFile>()).Where((BMSFile file) => file != null && (!string.IsNullOrWhiteSpace(file.instl_dst) || !string.IsNullOrWhiteSpace(file.InstallDestinationTitle) || !string.IsNullOrWhiteSpace(file.InstallDestinationArtist) || (file.InstallDestinationSuggestions?.Count ?? 0) > 0 || file.HasLowConfidenceInstallWarning)))
        {
            bmsFile.instl_dst = null;
            bmsFile.InstallDestinationTitle = string.Empty;
            bmsFile.InstallDestinationArtist = string.Empty;
            bmsFile.InstallDestinationSuggestions = Array.Empty<string>();
            bmsFile.HasLowConfidenceInstallWarning = false;
            bmsFile.IsInstallDestinationSuggestionPopupOpen = false;
        }
    }

    public PendingInstallDestinationSelectionResult ValidatePendingInstallDestination(BMSFile targetFile, IEnumerable<BMSPackage> pendingPackages, IEnumerable<string> knownChartDirectories, string destinationDirectory)
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
            result.WarningMessage = Properties.Resources.Warn_PendingPackageNotFound;
            return result;
        }
        result.TargetFiles.AddRange(package.BMSFiles.Where((BMSFile file) => file != null));
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

    private static CandidateEvaluation EvaluateCandidate(string candidateDir, ChartResourceSnapshot snapshot, uint[] fileNameHashes, DirectoryResourceLookupCache.Entry entry, DirectoryResourceLookupCache.Entry bundledResources, DirectoryResourceLookupCache.Entry transientCandidateEntry, bool isSourceCandidate)
    {
        DirectoryResourceLookupCache.Entry effectiveEntry = transientCandidateEntry ?? entry;
        ISet<uint> candidateHashes = effectiveEntry?.AllBaseNameHashes;
        if (candidateHashes == null && fileNameHashes != null && fileNameHashes.Length > 0)
        {
            candidateHashes = new HashSet<uint>(fileNameHashes);
        }
        ISet<uint> candidateAudioHashes = effectiveEntry?.AudioBaseNameHashes ?? candidateHashes;
        ISet<uint> candidateVisualHashes = effectiveEntry?.ImageBaseNameHashes ?? candidateHashes;
        ISet<uint> candidateMovieHashes = effectiveEntry?.MovieBaseNameHashes ?? candidateHashes;
        ISet<uint> candidateOptionalImageHashes = effectiveEntry?.ImageBaseNameHashes ?? candidateHashes;
        ISet<uint> candidateAudioRelativeHashes = effectiveEntry?.AudioRelativePathHashes;
        ISet<uint> candidateVisualRelativeHashes = effectiveEntry?.ImageRelativePathHashes;
        ISet<uint> candidateMovieRelativeHashes = effectiveEntry?.MovieRelativePathHashes;
        ISet<uint> candidateOptionalImageRelativeHashes = effectiveEntry?.ImageRelativePathHashes;
        ISet<uint> bundledAllHashes = bundledResources?.AllBaseNameHashes;
        ISet<uint> bundledAudioHashes = bundledResources?.AudioBaseNameHashes;
        ISet<uint> bundledVisualHashes = bundledResources?.ImageBaseNameHashes;
        ISet<uint> bundledMovieHashes = bundledResources?.MovieBaseNameHashes;
        ISet<uint> bundledOptionalImageHashes = bundledResources?.ImageBaseNameHashes;
        ISet<uint> bundledAudioRelativeHashes = bundledResources?.AudioRelativePathHashes;
        ISet<uint> bundledVisualRelativeHashes = bundledResources?.ImageRelativePathHashes;
        ISet<uint> bundledMovieRelativeHashes = bundledResources?.MovieRelativePathHashes;
        ISet<uint> bundledOptionalImageRelativeHashes = bundledResources?.ImageRelativePathHashes;
        CandidateEvaluation evaluation = new CandidateEvaluation
        {
            DirectoryPath = candidateDir,
            IsSourceCandidate = isSourceCandidate,
            AudioDefined = snapshot.AudioReferenceCount,
            VisualDefined = snapshot.VisualReferenceCount,
            MovieDefined = snapshot.MovieReferenceCount,
            OptionalImageDefined = snapshot.OptionalImageReferenceCount,
            AudioCandidateCount = CountUnion(candidateAudioHashes, bundledAudioHashes),
            VisualCandidateCount = CountUnion(candidateVisualHashes, bundledVisualHashes),
            MovieCandidateCount = CountUnion(candidateMovieHashes, bundledMovieHashes),
            OptionalImageCandidateCount = CountUnion(candidateOptionalImageHashes, bundledOptionalImageHashes),
            AudioFileCount = CountUnion(candidateHashes, bundledAllHashes)
        };
        if ((candidateHashes == null || candidateHashes.Count == 0) && (bundledAllHashes == null || bundledAllHashes.Count == 0))
        {
            return evaluation;
        }
        CountMatches(snapshot.AudioBaseNameHashes, snapshot.AudioRelativePathHashes, candidateAudioHashes, bundledAudioHashes, candidateAudioRelativeHashes, bundledAudioRelativeHashes, out int audioMatched, out int audioExactMatched);
        CountMatches(snapshot.VisualBaseNameHashes, snapshot.VisualRelativePathHashes, candidateVisualHashes, bundledVisualHashes, candidateVisualRelativeHashes, bundledVisualRelativeHashes, out int visualMatched, out int visualExactMatched);
        CountMatches(snapshot.MovieBaseNameHashes, snapshot.MovieRelativePathHashes, candidateMovieHashes, bundledMovieHashes, candidateMovieRelativeHashes, bundledMovieRelativeHashes, out int movieMatched, out int movieExactMatched);
        CountMatches(snapshot.OptionalImageBaseNameHashes, snapshot.OptionalImageRelativePathHashes, candidateOptionalImageHashes, bundledOptionalImageHashes, candidateOptionalImageRelativeHashes, bundledOptionalImageRelativeHashes, out int optionalMatched, out int optionalExactMatched);
        evaluation.AudioMatched = audioMatched;
        evaluation.AudioExactMatched = audioExactMatched;
        evaluation.VisualMatched = visualMatched;
        evaluation.VisualExactMatched = visualExactMatched;
        evaluation.MovieMatched = movieMatched;
        evaluation.MovieExactMatched = movieExactMatched;
        evaluation.OptionalImageMatched = optionalMatched;
        evaluation.OptionalImageExactMatched = optionalExactMatched;
        return evaluation;
    }

    private static void CountMatches(ISet<uint> baseNameHashes, ISet<uint> relativePathHashes, ISet<uint> candidateHashes, ISet<uint> bundledHashes, ISet<uint> candidateRelativePathHashes, ISet<uint> bundledRelativePathHashes, out int matched, out int exactMatched)
    {
        matched = CountUnionMatches(baseNameHashes, candidateHashes, bundledHashes);
        exactMatched = CountUnionMatches(relativePathHashes, candidateRelativePathHashes, bundledRelativePathHashes);
        if (exactMatched > matched)
        {
            matched = exactMatched;
        }
    }

    private static int CountUnionMatches(ISet<uint> targetHashes, ISet<uint> candidateHashes, ISet<uint> bundledHashes)
    {
        if (targetHashes == null || targetHashes.Count == 0)
        {
            return 0;
        }
        bool hasCandidate = candidateHashes != null && candidateHashes.Count > 0;
        bool hasBundled = bundledHashes != null && bundledHashes.Count > 0;
        if (!hasCandidate && !hasBundled)
        {
            return 0;
        }
        return targetHashes.Count((uint hash) => (hasCandidate && candidateHashes.Contains(hash)) || (hasBundled && bundledHashes.Contains(hash)));
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
