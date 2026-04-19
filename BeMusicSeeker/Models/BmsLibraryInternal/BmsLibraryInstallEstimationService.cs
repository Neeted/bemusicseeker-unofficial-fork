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

        public int AudioFileCount { get; set; }

        public int AudioHealth => ComputeHealth(AudioMatched, AudioDefined);

        public int VisualHealth => ComputeHealth(VisualMatched, VisualDefined);

        public int MovieHealth => ComputeHealth(MovieMatched, MovieDefined);

        public int OptionalImageHealth => ComputeHealth(OptionalImageMatched, OptionalImageDefined);

        public string ToSummary()
        {
            return string.Format(
                "dir={0} audio={1}/{2} exact={3} visual={4}/{5} exact={6} movie={7}/{8} exact={9} optional={10}/{11} exact={12} audioHealth={13} visualHealth={14} movieHealth={15} optionalHealth={16}",
                DirectoryPath ?? string.Empty,
                AudioMatched,
                AudioDefined,
                AudioExactMatched,
                VisualMatched,
                VisualDefined,
                VisualExactMatched,
                MovieMatched,
                MovieDefined,
                MovieExactMatched,
                OptionalImageMatched,
                OptionalImageDefined,
                OptionalImageExactMatched,
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

        private static int ComputeHealth(int matched, int defined)
        {
            if (defined <= 0)
            {
                return 100;
            }
            return (int)Math.Round(100.0 * matched / defined, MidpointRounding.AwayFromZero);
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

    public InstallEstimationResult EstimateInstallationDirectory(IEnumerable<BMSFile> bmsFiles, HashSet<string> installedHashes, BMSDirectoryFileNameHash folderAllFileList, DirectoryResourceLookupCache directoryLookupCache, bool asParallel, BmsInstallationEstimateMode estimateMode, Func<string, InstallDestinationRepresentativeMetadata> representativeMetadataResolver = null)
    {
        InstallEstimationResult result = new InstallEstimationResult();
        List<BMSFile> targetFiles = (bmsFiles ?? Enumerable.Empty<BMSFile>()).Where((BMSFile bmsInfo) => bmsInfo != null).ToList();
        if (targetFiles.Count == 0 || targetFiles.Any((BMSFile bmsInfo) => !string.IsNullOrWhiteSpace(bmsInfo.instl_dst)))
        {
            return result;
        }
        bool isFixMode = estimateMode == BmsInstallationEstimateMode.Fix;
        bool isMergeMode = estimateMode == BmsInstallationEstimateMode.MergeNoSourceCompensation;
        bool isCorrectionLikeMode = isFixMode || isMergeMode;
        BMSFile representativeFile = targetFiles
            .Where((BMSFile bmsFile) => bmsFile != null && (isCorrectionLikeMode || installedHashes == null || !installedHashes.Contains(bmsFile.hash)))
            .OrderByDescending(GetDefinedResourceCount)
            .FirstOrDefault();
        if (representativeFile == null)
        {
            return result;
        }
        ChartResourceSnapshot resourceSnapshot = ChartResourceSnapshot.Create(representativeFile);
        result.TargetResourceHashCount = resourceSnapshot.EnumerateAllBaseNameHashes().Count();
        result.ResourceSummary = "chart=" + (representativeFile.path ?? string.Empty)
            + " audioRefs=" + resourceSnapshot.AudioReferenceCount
            + " visualRefs=" + resourceSnapshot.VisualReferenceCount
            + " movieRefs=" + resourceSnapshot.MovieReferenceCount
            + " optionalRefs=" + resourceSnapshot.OptionalImageReferenceCount
            + " pathSegmentRefs=" + resourceSnapshot.PathSegmentReferenceCount;
        if (resourceSnapshot.TotalReferenceCount == 0)
        {
            return result;
        }
        string targetDir = Path.GetDirectoryName(representativeFile.path);
        List<string> allCandidateDirs = folderAllFileList.Keys.Where((string dir) => !dir.Equals(targetDir, StringComparison.OrdinalIgnoreCase)).ToList();
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
                HashSet<string> filteredDirectories = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                foreach (uint targetFileHash in targetFileHashes)
                {
                    filteredDirectories.UnionWith(directoryLookupCache.GetDirectoriesByHash(targetFileHash));
                }
                filteredDirectories.Remove(targetDir);
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
        CandidateEvaluation sourceEvaluation = null;
        if (!string.IsNullOrWhiteSpace(targetDir))
        {
            sourceEvaluation = EvaluateCandidate(targetDir, resourceSnapshot, folderAllFileList.TryGetCachedFileNameHashArray(targetDir), directoryLookupCache?.GetEntryOrNull(targetDir));
        }
        IEnumerable<string> candidateSource = asParallel ? candidateDirList.AsParallel() : candidateDirList.AsParallel().WithDegreeOfParallelism(1);
        List<CandidateEvaluation> candidateInfos = candidateSource.Select((string candidateDir) => EvaluateCandidate(candidateDir, resourceSnapshot, folderAllFileList.TryGetCachedFileNameHashArray(candidateDir), directoryLookupCache?.GetEntryOrNull(candidateDir)))
            .Where(delegate (CandidateEvaluation evaluation)
            {
                int primaryHealth = resourceSnapshot.AudioReferenceCount > 0
                    ? evaluation.AudioHealth
                    : Math.Max(evaluation.VisualHealth, Math.Max(evaluation.MovieHealth, evaluation.OptionalImageHealth));
                if (primaryHealth <= innerWavHealthThreshold)
                {
                    return false;
                }
                if (!isFixMode || sourceEvaluation == null)
                {
                    return true;
                }
                return evaluation.VisualHealth >= sourceEvaluation.VisualHealth;
            })
            .ToList();
        if (isMergeMode)
        {
            candidateInfos = candidateInfos.Where((CandidateEvaluation evaluation) => !string.Equals(evaluation.DirectoryPath, targetDir, StringComparison.OrdinalIgnoreCase)).ToList();
        }
        else if (sourceEvaluation != null)
        {
            candidateInfos.Add(sourceEvaluation);
        }
        evaluationStopwatch.Stop();
        result.EvaluationMs = evaluationStopwatch.ElapsedMilliseconds;
        if (candidateInfos.Count == 0)
        {
            return result;
        }
        List<CandidateEvaluation> orderedCandidates = candidateInfos.OrderByDescending((CandidateEvaluation evaluation) => evaluation.AudioHealth)
            .ThenByDescending((CandidateEvaluation evaluation) => evaluation.AudioMatched)
            .ThenByDescending((CandidateEvaluation evaluation) => evaluation.AudioExactMatched)
            .ThenByDescending((CandidateEvaluation evaluation) => evaluation.VisualHealth)
            .ThenByDescending((CandidateEvaluation evaluation) => evaluation.VisualMatched)
            .ThenByDescending((CandidateEvaluation evaluation) => evaluation.VisualExactMatched)
            .ThenByDescending((CandidateEvaluation evaluation) => evaluation.MovieHealth)
            .ThenByDescending((CandidateEvaluation evaluation) => evaluation.MovieMatched)
            .ThenByDescending((CandidateEvaluation evaluation) => evaluation.MovieExactMatched)
            .ThenByDescending((CandidateEvaluation evaluation) => evaluation.OptionalImageHealth)
            .ThenByDescending((CandidateEvaluation evaluation) => evaluation.OptionalImageMatched)
            .ThenByDescending((CandidateEvaluation evaluation) => evaluation.OptionalImageExactMatched)
            .ThenByDescending((CandidateEvaluation evaluation) => evaluation.AudioFileCount)
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
        result.TopCandidateSummary = string.Join(" || ", result.Candidates.Select((InstallEstimationCandidate candidate) => candidate.ToSummary()));
        CandidateEvaluation selectedCandidateEvaluation = orderedCandidates.First();
        result.SelectedCandidateSummary = result.SelectedCandidate?.ToSummary() ?? selectedCandidateEvaluation.ToSummary();
        CandidateEvaluation secondCandidateEvaluation = orderedCandidates.Skip(1).FirstOrDefault();
        bool isLowConfidence = secondCandidateEvaluation != null && selectedCandidateEvaluation.HasSamePrimaryMetrics(secondCandidateEvaluation);
        result.Confidence = isLowConfidence ? InstallEstimationConfidence.Low : InstallEstimationConfidence.High;
        result.ConfidenceReason = secondCandidateEvaluation == null ? "single_candidate" : (isLowConfidence ? "tie_on_primary_metrics" : "distinct_primary_metrics");
        if (!isMergeMode && string.Equals(selectedCandidateEvaluation.DirectoryPath, targetDir, StringComparison.OrdinalIgnoreCase))
        {
            result.Candidates.Clear();
            result.SelectedCandidateSummary = string.Empty;
            result.TopCandidateSummary = string.Empty;
            result.Confidence = InstallEstimationConfidence.High;
            result.ConfidenceReason = "selected_source_directory";
            return result;
        }
        result.DestinationDirectory = selectedCandidateEvaluation.DirectoryPath;
        result.ShouldAutoApplyDestination = result.Confidence == InstallEstimationConfidence.High;
        return result;
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
        foreach (BMSFile bmsFile in (bmsFiles ?? Enumerable.Empty<BMSFile>()).Where((BMSFile file) => file != null && (!string.IsNullOrWhiteSpace(file.instl_dst) || !string.IsNullOrWhiteSpace(file.InstallDestinationTitle) || !string.IsNullOrWhiteSpace(file.InstallDestinationArtist))))
        {
            bmsFile.instl_dst = null;
            bmsFile.InstallDestinationTitle = string.Empty;
            bmsFile.InstallDestinationArtist = string.Empty;
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

    private static CandidateEvaluation EvaluateCandidate(string candidateDir, ChartResourceSnapshot snapshot, uint[] fileNameHashes, DirectoryResourceLookupCache.Entry entry)
    {
        ISet<uint> candidateHashes = entry?.FileNameHashes;
        if (candidateHashes == null && fileNameHashes != null && fileNameHashes.Length > 0)
        {
            candidateHashes = new HashSet<uint>(fileNameHashes);
        }
        HashSet<string> candidateBaseNames = entry?.NormalizedBaseNames;
        CandidateEvaluation evaluation = new CandidateEvaluation
        {
            DirectoryPath = candidateDir,
            AudioDefined = snapshot.AudioReferenceCount,
            VisualDefined = snapshot.VisualReferenceCount,
            MovieDefined = snapshot.MovieReferenceCount,
            OptionalImageDefined = snapshot.OptionalImageReferenceCount,
            AudioFileCount = entry?.FileNameHashCount ?? fileNameHashes?.Length ?? 0
        };
        if ((candidateHashes == null || candidateHashes.Count == 0) && (candidateBaseNames == null || candidateBaseNames.Count == 0))
        {
            return evaluation;
        }
        CountMatches(snapshot.AudioBaseNames, snapshot.AudioBaseNameHashes, candidateHashes, candidateBaseNames, out int audioMatched, out int audioExactMatched);
        CountMatches(snapshot.VisualBaseNames, snapshot.VisualBaseNameHashes, candidateHashes, candidateBaseNames, out int visualMatched, out int visualExactMatched);
        CountMatches(snapshot.MovieBaseNames, snapshot.MovieBaseNameHashes, candidateHashes, candidateBaseNames, out int movieMatched, out int movieExactMatched);
        CountMatches(snapshot.OptionalImageBaseNames, snapshot.OptionalImageBaseNameHashes, candidateHashes, candidateBaseNames, out int optionalMatched, out int optionalExactMatched);
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

    private static void CountMatches(ISet<string> baseNames, ISet<uint> baseNameHashes, ISet<uint> candidateHashes, ISet<string> candidateBaseNames, out int matched, out int exactMatched)
    {
        matched = 0;
        if (baseNameHashes != null && candidateHashes != null && baseNameHashes.Count > 0 && candidateHashes.Count > 0)
        {
            matched = baseNameHashes.Count(candidateHashes.Contains);
        }
        exactMatched = (candidateBaseNames == null || baseNames == null || candidateBaseNames.Count == 0 || baseNames.Count == 0)
            ? 0
            : baseNames.Count(candidateBaseNames.Contains);
        if (exactMatched > matched)
        {
            matched = exactMatched;
        }
    }

    private static InstallEstimationCandidate CreateCandidate(CandidateEvaluation evaluation, InstallDestinationRepresentativeMetadata representativeMetadata)
    {
        return new InstallEstimationCandidate
        {
            DirectoryPath = evaluation.DirectoryPath,
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
