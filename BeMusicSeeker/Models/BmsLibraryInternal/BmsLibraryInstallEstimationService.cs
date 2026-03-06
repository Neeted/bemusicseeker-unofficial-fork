using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using BeMusicSeeker.Models.Utils;

namespace BeMusicSeeker.Models.BmsLibraryInternal;

internal sealed class BmsLibraryInstallEstimationService
{
    private readonly int innerWavHealthThreshold;

    public BmsLibraryInstallEstimationService(BmsLibraryOptionsSnapshot options, int innerWavHealthThreshold)
    {
        _ = options ?? throw new ArgumentNullException(nameof(options));
        this.innerWavHealthThreshold = innerWavHealthThreshold;
    }

    public Dictionary<string, List<string>> BuildInstalledHashToDirectoryMap(IEnumerable<BMSFile> installedFiles)
    {
        Dictionary<string, HashSet<string>> dictionary = new Dictionary<string, HashSet<string>>(StringComparer.OrdinalIgnoreCase);
        foreach (BMSFile item in installedFiles ?? Enumerable.Empty<BMSFile>())
        {
            if (item == null || !IsBmsHashAvailable(item.hash))
            {
                continue;
            }
            string directoryPath = null;
            try
            {
                directoryPath = DirectoryExt.GetDirectoryNameSimple(item.path);
            }
            catch
            {
            }
            if (string.IsNullOrWhiteSpace(directoryPath))
            {
                continue;
            }
            if (!dictionary.TryGetValue(item.hash, out HashSet<string> directories))
            {
                directories = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                dictionary[item.hash] = directories;
            }
            directories.Add(directoryPath);
        }
        return dictionary.ToDictionary((KeyValuePair<string, HashSet<string>> x) => x.Key, (KeyValuePair<string, HashSet<string>> x) => x.Value.OrderBy((string dir) => dir, StringComparer.OrdinalIgnoreCase).ToList(), StringComparer.OrdinalIgnoreCase);
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

    public InstalledOnlyPackageResolutionResult TryPrepareInstalledOnlyPackageDestination(BMSPackage package, Dictionary<string, List<string>> installedDirectoryIndexSnapshot)
    {
        InstalledOnlyPackageResolutionResult result = new InstalledOnlyPackageResolutionResult();
        if (package == null)
        {
            result.Reason = InstalledDirectoryResolveReason.MissingInstallDestination;
            return result;
        }
        List<BMSFile> packageFiles = (package.BMSFiles ?? new List<BMSFile>()).Where((BMSFile file) => file != null).ToList();
        if (packageFiles.Count == 0 || installedDirectoryIndexSnapshot == null || installedDirectoryIndexSnapshot.Count == 0)
        {
            result.Reason = InstalledDirectoryResolveReason.MissingInstallDestination;
            return result;
        }
        HashSet<string> distinctDirectories = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (BMSFile item in packageFiles)
        {
            List<string> directoriesByHash = GetDistinctInstalledDirectoriesByHash(installedDirectoryIndexSnapshot, item.hash);
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

    public InstalledDirectoryLookupResult TryResolveInstalledDestinationFromPackage(BMSPackage package, List<BMSFile> missingFiles, Dictionary<string, List<string>> installedDirectoryIndexSnapshot, BMSDirectoryFileNameHash folderAllFileList)
    {
        InstalledDirectoryLookupResult result = new InstalledDirectoryLookupResult();
        if (package == null || missingFiles == null || missingFiles.Count == 0)
        {
            result.Reason = InstalledDirectoryResolveReason.InvalidInput;
            return result;
        }
        if (installedDirectoryIndexSnapshot == null || installedDirectoryIndexSnapshot.Count == 0)
        {
            result.Reason = InstalledDirectoryResolveReason.InstalledIndexEmpty;
            return result;
        }
        Dictionary<string, int> directoryScores = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        foreach (BMSFile item in package.BMSFiles ?? new List<BMSFile>())
        {
            if (item == null || !IsBmsHashAvailable(item.hash) || !installedDirectoryIndexSnapshot.TryGetValue(item.hash, out List<string> directories))
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
            .Where((BMSFile bmsFile) => isCorrectionLikeMode || bmsFile.maintenanceInfo == null || bmsFile.maintenanceInfo.GetWAVHealth() <= innerWavHealthThreshold)
            .OrderByDescending(GetDefinedResourceCount)
            .FirstOrDefault();
        if (representativeFile == null)
        {
            return result;
        }
        string targetDir = Path.GetDirectoryName(representativeFile.path);
        uint[] currentDirFileHashes = isCorrectionLikeMode ? null : BMSDirectoryFileNameHash.GetFileNameHashArray(targetDir);
        List<string> allCandidateDirs = folderAllFileList.Keys.Where((string dir) => !dir.Equals(targetDir, StringComparison.OrdinalIgnoreCase)).ToList();
        if (allCandidateDirs.Count == 0)
        {
            return result;
        }

        HashSet<uint> targetFileHashes = new HashSet<uint>();
        bool hasNonLocalReference = false;
        Action<string, IEnumerable<string>> addTargetFileHashes = delegate (string fileName, IEnumerable<string> fallbackExtensions)
        {
            if (string.IsNullOrWhiteSpace(fileName))
            {
                return;
            }
            string normalized = fileName.Replace('/', Path.DirectorySeparatorChar).Trim();
            if (normalized.IndexOf(Path.DirectorySeparatorChar) >= 0 || normalized.IndexOf(Path.AltDirectorySeparatorChar) >= 0)
            {
                hasNonLocalReference = true;
            }
            normalized = normalized.TrimStart(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
            string normalizedFileName;
            try
            {
                normalizedFileName = Path.GetFileName(normalized);
            }
            catch
            {
                return;
            }
            if (string.IsNullOrWhiteSpace(normalizedFileName))
            {
                return;
            }
            targetFileHashes.Add(BMSDirectoryFileNameHash.GetFileNameHash(normalizedFileName));
            if (string.IsNullOrWhiteSpace(Path.GetExtension(normalizedFileName)) && fallbackExtensions != null)
            {
                foreach (string extension in fallbackExtensions)
                {
                    if (!string.IsNullOrWhiteSpace(extension))
                    {
                        targetFileHashes.Add(BMSDirectoryFileNameHash.GetFileNameHash(normalizedFileName + extension));
                    }
                }
            }
        };
        foreach (string wavFile in representativeFile.WAVfiles ?? Enumerable.Empty<string>())
        {
            addTargetFileHashes(wavFile, BMSFile.wavExtensions);
        }
        foreach (string bgaFile in representativeFile.BGAfiles ?? Enumerable.Empty<string>())
        {
            addTargetFileHashes(bgaFile, BMSFile.bgaAllExtensions);
        }
        addTargetFileHashes(representativeFile.backbmp, BMSFile.bgaImageExtensions);
        addTargetFileHashes(representativeFile.banner, BMSFile.bgaImageExtensions);
        addTargetFileHashes(representativeFile.stagefile, BMSFile.bgaImageExtensions);

        List<string> candidateDirList = allCandidateDirs;
        if (!hasNonLocalReference && targetFileHashes.Count > 0)
        {
            IEnumerable<string> filtered = (asParallel ? allCandidateDirs.AsParallel() : allCandidateDirs.AsParallel().WithDegreeOfParallelism(1))
                .Where(delegate (string dir)
                {
                    uint[] fileNameHashArray = folderAllFileList.GetFileNameHashArray(dir);
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
            if (candidateDirList.Count == 0)
            {
                candidateDirList = allCandidateDirs;
                result.UsedFallbackCandidateExpansion = true;
            }
        }
        result.CandidateDirectoryCount = candidateDirList.Count;
        IEnumerable<string> candidateSource = asParallel ? candidateDirList.AsParallel() : candidateDirList.AsParallel().WithDegreeOfParallelism(1);
        List<BMSFileMaintenanceInfo> candidateInfos = candidateSource.Select(delegate (string candidateDir)
        {
            BMSFileMaintenanceInfo maintenanceInfo = new BMSFileMaintenanceInfo(representativeFile)
            {
                path = Path.Combine(candidateDir, Path.GetFileName(representativeFile.path))
            };
            representativeFile.SetHealthStatus(folderAllFileList, forceUpdate: false, memClear: false, maintenanceInfo, candidateDir, currentDirFileHashes);
            return maintenanceInfo;
        }).Where((BMSFileMaintenanceInfo m) => m.GetWAVHealth() > innerWavHealthThreshold && (!isFixMode || m.GetBGAHealth() >= representativeFile.maintenanceInfo.GetBGAHealth()))
            .ToList();
        if (isMergeMode)
        {
            candidateInfos = candidateInfos.Where((BMSFileMaintenanceInfo m) => !string.Equals(DirectoryExt.GetDirectoryNameSimple(m.path), targetDir, StringComparison.OrdinalIgnoreCase)).ToList();
        }
        else
        {
            candidateInfos = candidateInfos.Concat(new BMSFileMaintenanceInfo[1] { representativeFile.maintenanceInfo }).Distinct().ToList();
        }
        if (candidateInfos.Count == 0)
        {
            return result;
        }
        List<BMSFileMaintenanceInfo> orderedCandidates = candidateInfos.OrderByDescending((BMSFileMaintenanceInfo m) => m.GetWAVHealth())
            .ThenByDescending((BMSFileMaintenanceInfo m) => m.GetBGAHealth())
            .ThenByDescending((BMSFileMaintenanceInfo m) => m.GetMovieHealth())
            .ThenByDescending((BMSFileMaintenanceInfo m) => m.GetOptIMGHealth())
            .ToList();
        int? wavHealthBest = orderedCandidates[0].GetWAVHealth();
        int? bgaHealthBest = orderedCandidates[0].GetBGAHealth();
        int? movieHealthBest = orderedCandidates[0].GetMovieHealth();
        bool? optImgHealthBest = orderedCandidates[0].GetOptIMGHealth();
        List<BMSFileMaintenanceInfo> tiedCandidates = orderedCandidates.Where((BMSFileMaintenanceInfo m) => m.GetWAVHealth() == wavHealthBest && m.GetBGAHealth() == bgaHealthBest && m.GetMovieHealth() == movieHealthBest && m.GetOptIMGHealth() == optImgHealthBest).ToList();
        Dictionary<string, int> wavFileCountsByDir = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        Func<string, int> getWavFileCount = delegate (string dir)
        {
            if (wavFileCountsByDir.TryGetValue(dir, out int value))
            {
                return value;
            }
            int wavFileCount = 0;
            try
            {
                wavFileCount = FastDirectoryEnumerator.GetFileNames(dir).Count((string file) => BMSFile.wavExtensions.Any((string ext) => file.EndsWith(ext, StringComparison.OrdinalIgnoreCase)));
            }
            catch
            {
            }
            wavFileCountsByDir[dir] = wavFileCount;
            return wavFileCount;
        };
        int maxWavFileCount = tiedCandidates.Max((BMSFileMaintenanceInfo m) => getWavFileCount(DirectoryExt.GetDirectoryNameSimple(m.path)));
        BMSFileMaintenanceInfo selectedCandidate = tiedCandidates.Where((BMSFileMaintenanceInfo m) => getWavFileCount(DirectoryExt.GetDirectoryNameSimple(m.path)) == maxWavFileCount)
            .OrderByDescending((BMSFileMaintenanceInfo m) => ReferenceEquals(m, representativeFile.maintenanceInfo) ? 1 : 0)
            .First();
        if (ReferenceEquals(selectedCandidate, representativeFile.maintenanceInfo))
        {
            return result;
        }
        result.DestinationDirectory = DirectoryExt.GetDirectoryNameSimple(selectedCandidate.path);
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
        foreach (BMSFile bmsFile in (bmsFiles ?? Enumerable.Empty<BMSFile>()).Where((BMSFile file) => file != null && !string.IsNullOrWhiteSpace(file.instl_dst)))
        {
            bmsFile.instl_dst = null;
        }
    }

    public PendingInstallDestinationSelectionResult ValidatePendingInstallDestination(BMSFile targetFile, IEnumerable<BMSPackage> pendingPackages, IEnumerable<string> knownBmsDirectories, string destinationDirectory)
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
        HashSet<string> knownDirectories = new HashSet<string>((knownBmsDirectories ?? Enumerable.Empty<string>()).Where((string path) => !string.IsNullOrWhiteSpace(path)), StringComparer.OrdinalIgnoreCase);
        if (!knownDirectories.Contains(installDirectory))
        {
            result.WarningMessage = string.Format(Properties.Resources.Warn_InstallDirMustContainBms, installDirectory);
            return result;
        }
        result.Success = true;
        result.ValidatedDestinationDirectory = installDirectory;
        return result;
    }

    public static List<string> GetDistinctInstalledDirectoriesByHash(Dictionary<string, List<string>> installedDirectoryIndexSnapshot, string hash)
    {
        if (installedDirectoryIndexSnapshot == null || !IsBmsHashAvailable(hash) || !installedDirectoryIndexSnapshot.TryGetValue(hash, out List<string> directories) || directories == null)
        {
            return new List<string>();
        }
        return directories.Where((string dir) => !string.IsNullOrWhiteSpace(dir)).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
    }

    public static BMSFile FindChartWithMissingInstalledDirectory(BMSPackage package, Dictionary<string, List<string>> installedDirectoryIndexSnapshot)
    {
        return (package?.BMSFiles ?? new List<BMSFile>()).FirstOrDefault((BMSFile file) => file == null || !IsBmsHashAvailable(file.hash) || GetDistinctInstalledDirectoriesByHash(installedDirectoryIndexSnapshot, file.hash).Count == 0);
    }

    public static BMSFile FindChartWithMultipleInstalledDirectories(BMSPackage package, Dictionary<string, List<string>> installedDirectoryIndexSnapshot)
    {
        return (package?.BMSFiles ?? new List<BMSFile>()).FirstOrDefault((BMSFile file) => file != null && IsBmsHashAvailable(file.hash) && GetDistinctInstalledDirectoriesByHash(installedDirectoryIndexSnapshot, file.hash).Count > 1);
    }

    public static int CountDistinctInstalledDirectoriesForPackage(BMSPackage package, Dictionary<string, List<string>> installedDirectoryIndexSnapshot)
    {
        return (package?.BMSFiles ?? new List<BMSFile>())
            .Where((BMSFile file) => file != null && IsBmsHashAvailable(file.hash))
            .SelectMany((BMSFile file) => GetDistinctInstalledDirectoriesByHash(installedDirectoryIndexSnapshot, file.hash))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Count();
    }

    private static int GetDefinedResourceCount(BMSFile bmsFile)
    {
        if (bmsFile.WAVfiles == null || bmsFile.BGAfiles == null)
        {
            bmsFile.SetHealthStatus(null, forceUpdate: true, memClear: false);
        }
        return ((bmsFile.WAVfiles != null) ? bmsFile.WAVfiles.Count() : 0)
            + ((bmsFile.BGAfiles != null) ? bmsFile.BGAfiles.Count() : 0)
            + ((!string.IsNullOrWhiteSpace(bmsFile.backbmp)) ? 1 : 0)
            + ((!string.IsNullOrWhiteSpace(bmsFile.banner)) ? 1 : 0)
            + ((!string.IsNullOrWhiteSpace(bmsFile.stagefile)) ? 1 : 0);
    }

    private static bool IsBmsHashAvailable(string hash)
    {
        return !string.IsNullOrWhiteSpace(hash);
    }
}
