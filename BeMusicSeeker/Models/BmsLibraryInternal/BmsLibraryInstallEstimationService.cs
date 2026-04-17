using System;
using System.Collections.Generic;
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
