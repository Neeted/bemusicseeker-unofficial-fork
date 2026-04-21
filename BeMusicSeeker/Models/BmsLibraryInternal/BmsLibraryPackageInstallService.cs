using System;
using System.Collections;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.ExceptionServices;
using System.Threading;
using System.Windows;
using BeMusicSeeker.Models.LR2;
using BeMusicSeeker.Models.Utils;
using BeMusicSeeker.Properties;
using Microsoft.VisualBasic.FileIO;
using Ribbit.Util.Extensions;

namespace BeMusicSeeker.Models.BmsLibraryInternal;

internal enum ComponentMoveDecision
{
    Move,
    Overwrite,
    SkipSame,
    SkipOlderOrEqual
}

internal sealed class ComponentMovePlanItem
{
    public string SourcePath { get; set; }

    public string DestinationPath { get; set; }
}

internal sealed class ComponentMovePlanBuildResult
{
    public List<ComponentMovePlanItem> PlanItems { get; } = new List<ComponentMovePlanItem>();

    public int SkippedByExclusion { get; set; }
}

internal sealed class ComponentMoveSummary
{
    public int Total { get; set; }

    public int Moved { get; set; }

    public int Overwritten { get; set; }

    public int SkippedSame { get; set; }

    public int SkippedOlder { get; set; }

    public int DeletedAfterSkip { get; set; }

    public int SkippedByExclusion { get; set; }

    public int SkippedSamePath { get; set; }

    public int Failed { get; set; }

    public int RenamedKeep { get; set; }

    public int RenamedFromOverwrite { get; set; }

    public int RenamedFromSkipOlder { get; set; }

    public int HashChecked { get; set; }

    public int HashSameSkip { get; set; }

    public int HashDiffRenamed { get; set; }

    public int HashUnavailableRenamed { get; set; }
}

/// <summary>
/// Executes pending/package workflows against caller-owned state snapshots.
/// The facade must acquire the required locks before invoking this service.
/// </summary>
internal sealed class BmsLibraryPackageInstallService
{
    private sealed class ArchiveEntryMetadata
    {
        public string FileName { get; set; }

        public bool IsFolder { get; set; }

        public DateTime CreationTime { get; set; }

        public DateTime LastAccessTime { get; set; }

        public DateTime LastWriteTime { get; set; }
    }

    private sealed class RequiredArchiveMetadataRestoreException : Exception
    {
        public RequiredArchiveMetadataRestoreException(string restoredPath, string detailMessage)
            : base(detailMessage)
        {
            RestoredPath = restoredPath;
        }

        public string RestoredPath { get; }
    }

    public static readonly TimeSpan SmartComponentOverwriteTimeTolerance = TimeSpan.FromSeconds(2.0);

    private readonly BmsLibraryLibraryFileOperationsService libraryFileOperationsService = new BmsLibraryLibraryFileOperationsService();

    private static readonly HashSet<string> smartOverwriteProtectedExtensions = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
    {
        ".txt",
        ".bmx",
        ".pmx",
        ".bmson"
    };

    public bool IsSmartOverwriteProtectedExtension(string filePath)
    {
        if (string.IsNullOrWhiteSpace(filePath))
        {
            return false;
        }
        string extension = Path.GetExtension(filePath);
        return !string.IsNullOrWhiteSpace(extension) && smartOverwriteProtectedExtensions.Contains(extension);
    }

    public ComponentMoveDecision DecideComponentMove(string srcFilePath, string dstFilePath)
    {
        if (!File.Exists(dstFilePath))
        {
            return ComponentMoveDecision.Move;
        }
        FileInfo sourceInfo = new FileInfo(srcFilePath);
        FileInfo destinationInfo = new FileInfo(dstFilePath);
        DateTime sourceWriteTime = sourceInfo.LastWriteTimeUtc;
        DateTime destinationWriteTime = destinationInfo.LastWriteTimeUtc;
        if (sourceInfo.Length == destinationInfo.Length && Math.Abs((sourceWriteTime - destinationWriteTime).TotalSeconds) <= SmartComponentOverwriteTimeTolerance.TotalSeconds)
        {
            return ComponentMoveDecision.SkipSame;
        }
        return sourceWriteTime > destinationWriteTime ? ComponentMoveDecision.Overwrite : ComponentMoveDecision.SkipOlderOrEqual;
    }

    public bool IsSamePath(string path1, string path2)
    {
        try
        {
            return string.Equals(Path.GetFullPath(path1), Path.GetFullPath(path2), StringComparison.OrdinalIgnoreCase);
        }
        catch
        {
            return string.Equals(path1, path2, StringComparison.OrdinalIgnoreCase);
        }
    }

    public string GetRelativePathFromDirectory(string rootDirectory, string targetPath)
    {
        if (string.IsNullOrWhiteSpace(rootDirectory))
        {
            return Path.GetFileName(targetPath);
        }
        string normalizedRootDirectory = rootDirectory.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar) + Path.DirectorySeparatorChar;
        if (!targetPath.StartsWith(normalizedRootDirectory, StringComparison.OrdinalIgnoreCase))
        {
            return Path.GetFileName(targetPath);
        }
        return targetPath.Substring(normalizedRootDirectory.Length);
    }

    public ComponentMovePlanBuildResult BuildComponentMovePlan(IEnumerable<string> installComponentFiles, string destinationDirectory, ISet<string> excludedComponentPaths)
    {
        ComponentMovePlanBuildResult result = new ComponentMovePlanBuildResult();
        HashSet<string> excludedPathSet = ((excludedComponentPaths != null && excludedComponentPaths.Count > 0) ? new HashSet<string>(excludedComponentPaths, StringComparer.OrdinalIgnoreCase) : null);
        foreach (string installComponentFile in installComponentFiles ?? Enumerable.Empty<string>())
        {
            if (File.Exists(installComponentFile))
            {
                if (excludedPathSet != null && excludedPathSet.Contains(installComponentFile))
                {
                    result.SkippedByExclusion++;
                    continue;
                }
                result.PlanItems.Add(new ComponentMovePlanItem
                {
                    SourcePath = installComponentFile,
                    DestinationPath = Path.Combine(destinationDirectory, Path.GetFileName(installComponentFile))
                });
                continue;
            }
            if (!Directory.Exists(installComponentFile))
            {
                throw new FileNotFoundException("Component path was not found.", installComponentFile);
            }
            string destinationRoot = Path.Combine(destinationDirectory, Path.GetFileName(installComponentFile));
            foreach (string path in Directory.EnumerateFiles(installComponentFile, "*", System.IO.SearchOption.AllDirectories))
            {
                if (excludedPathSet != null && excludedPathSet.Contains(path))
                {
                    result.SkippedByExclusion++;
                    continue;
                }
                result.PlanItems.Add(new ComponentMovePlanItem
                {
                    SourcePath = path,
                    DestinationPath = Path.Combine(destinationRoot, GetRelativePathFromDirectory(installComponentFile, path))
                });
            }
        }
        return result;
    }

    public List<BMSPackage> DeduplicatePackagesByPathOrReference(IEnumerable<BMSPackage> packages)
    {
        List<BMSPackage> deduplicated = new List<BMSPackage>();
        HashSet<string> paths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        HashSet<BMSPackage> references = new HashSet<BMSPackage>();
        foreach (BMSPackage package in packages ?? Enumerable.Empty<BMSPackage>())
        {
            if (package == null)
            {
                continue;
            }
            if (!string.IsNullOrWhiteSpace(package.path))
            {
                if (!paths.Add(package.path))
                {
                    continue;
                }
            }
            else if (!references.Add(package))
            {
                continue;
            }
            deduplicated.Add(package);
        }
        return deduplicated;
    }

    public List<BMSFile> DeduplicateFilesByPathOrReference(IEnumerable<BMSFile> files)
    {
        List<BMSFile> deduplicated = new List<BMSFile>();
        HashSet<string> paths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        HashSet<BMSFile> references = new HashSet<BMSFile>();
        foreach (BMSFile file in files ?? Enumerable.Empty<BMSFile>())
        {
            if (file == null)
            {
                continue;
            }
            if (!string.IsNullOrWhiteSpace(file.path))
            {
                if (!paths.Add(file.path))
                {
                    continue;
                }
            }
            else if (!references.Add(file))
            {
                continue;
            }
            deduplicated.Add(file);
        }
        return deduplicated;
    }

    public List<BMSPackage> GetPendingPackagesContainingOnlyInstalledCharts(IEnumerable<BMSPackage> pendingPackages, Func<BMSFile, bool> isInstalledChart)
    {
        List<BMSPackage> result = new List<BMSPackage>();
        foreach (BMSPackage package in pendingPackages ?? Enumerable.Empty<BMSPackage>())
        {
            List<BMSFile> files = (package?.BMSFiles ?? new List<BMSFile>()).Where((BMSFile file) => file != null).ToList();
            if (files.Count == 0)
            {
                continue;
            }
            if (files.All((BMSFile file) => isInstalledChart != null && isInstalledChart(file)))
            {
                result.Add(package);
            }
        }
        return result;
    }

    public List<BMSFile> GetPendingBmsFilesSnapshot(IEnumerable<BMSPackage> pendingPackages)
    {
        return DeduplicateFilesByPathOrReference((pendingPackages ?? Enumerable.Empty<BMSPackage>())
            .Where((BMSPackage package) => package != null)
            .SelectMany((BMSPackage package) => package.BMSFiles)
            .Where((BMSFile file) => file != null && PendingChartEntry.IsBmsChartFile(file)));
    }

    public List<BMSPackage> SearchBmsFilesRecursively(string dirfullpath, double dupRateThreshInOnePkg, bool recursive = false)
    {
        return SearchBmsFilesRecursivelyWithMetadata(dirfullpath, dupRateThreshInOnePkg, recursive).Packages;
    }

    /// <summary>
    /// ディレクトリ探索で見つかった BMS パッケージと、再統合候補ディレクトリをまとめて返します。
    /// </summary>
    internal BmsPackageDiscoveryResult SearchBmsFilesRecursivelyWithMetadata(string dirfullpath, double dupRateThreshInOnePkg, bool recursive = false)
    {
        BmsPackageDiscoveryResult result = new BmsPackageDiscoveryResult();
        IEnumerable<string> fileSystemEntries;
        try
        {
            fileSystemEntries = Directory.EnumerateFileSystemEntries(dirfullpath, "*", System.IO.SearchOption.TopDirectoryOnly).ToList();
        }
        catch
        {
            return result;
        }
        if (fileSystemEntries == null || !fileSystemEntries.Any())
        {
            return result;
        }
        List<string> files = fileSystemEntries.Where(File.Exists).ToList();
        List<string> directories = fileSystemEntries.Where(Directory.Exists).ToList();
        List<string> chartFiles = files.Where((string file) => PendingChartEntry.IsSupportedChartFilePath(file) && File.Exists(file)).ToList();
        if (chartFiles.Count > 0)
        {
            if (chartFiles.Count == 1 || chartFiles.Any(delegate (string chartFilePath)
                {
                    BMSFile bMSFile = PendingChartEntry.CreateFromFilePath(chartFilePath);
                    if (bMSFile == null)
                    {
                        return false;
                    }
                    bMSFile.SetHealthStatus(null, forceUpdate: false, memClear: false);
                    return bMSFile.maintenanceInfo.wav_files_existing > 0 || bMSFile.maintenanceInfo.bga_files_existing > 0 || bMSFile.maintenanceInfo.movie_files_existing > 0;
                }))
            {
                result.Packages.Add(new BMSPackage
                {
                    path = dirfullpath,
                    delete_parent = false
                });
            }
            else
            {
                List<IEnumerable<string>> resourcesByChart = chartFiles
                    .Select((string filePath) => PendingChartEntry.CreateFromFilePath(filePath))
                    .Where((BMSFile bmsInfo) => bmsInfo != null)
                    .Select((BMSFile bmsInfo) => (bmsInfo.WAVfiles ?? new HashSet<string>(StringComparer.OrdinalIgnoreCase))
                        .Concat(bmsInfo.BGAfiles ?? new HashSet<string>(StringComparer.OrdinalIgnoreCase))
                        .Select((string path) => Path.GetFileNameWithoutExtension(path).ToUpperInvariant())
                        .Distinct()
                        .ToList()
                        .AsEnumerable())
                    .ToList();
                if ((double)resourcesByChart.Aggregate(Enumerable.Intersect).Count() / (double)resourcesByChart.Select((IEnumerable<string> resourceList) => resourceList.Count()).Min() >= dupRateThreshInOnePkg)
                {
                    result.Packages.Add(new BMSPackage
                    {
                        path = dirfullpath,
                        delete_parent = false
                    });
                }
                else
                {
                    result.Packages.AddRange(chartFiles.Select((string filePath) => new BMSPackage
                    {
                        path = filePath,
                        delete_parent = recursive
                    }));
                    result.RegroupEligibleSourceDirectories.Add(NormalizeDirectoryPath(dirfullpath));
                }
            }
        }
        else if (directories.Count > 0)
        {
            foreach (string dir in directories)
            {
                BmsPackageDiscoveryResult childResult = SearchBmsFilesRecursivelyWithMetadata(dir, dupRateThreshInOnePkg, recursive: true);
                result.Packages.AddRange(childResult.Packages);
                result.RegroupEligibleSourceDirectories.AddRange(childResult.RegroupEligibleSourceDirectories);
            }
        }
        List<string> distinctRegroupEligibleSourceDirectories = result.RegroupEligibleSourceDirectories
            .Where((string path) => !string.IsNullOrWhiteSpace(path))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
        result.RegroupEligibleSourceDirectories.Clear();
        result.RegroupEligibleSourceDirectories.AddRange(distinctRegroupEligibleSourceDirectories);
        return result;
    }

    private static string NormalizeDirectoryPath(string directoryPath)
    {
        return string.IsNullOrWhiteSpace(directoryPath)
            ? null
            : directoryPath.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
    }

    private static string ResolveBundledSevenZipLibraryPath()
    {
        string architectureDirectoryName = IntPtr.Size == 4 ? "x86" : "x64";
        string bundledLibraryPath = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "libs", architectureDirectoryName, "7z.dll");
        if (File.Exists(bundledLibraryPath))
        {
            return bundledLibraryPath;
        }

        throw new FileNotFoundException(Resources.Warn_ArchiveBundledSevenZipMissingDetail, bundledLibraryPath);
    }

    private static string ResolveBundledSevenZipExtractorAssemblyPath()
    {
        string managedAssemblyPath = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "libs", "SevenZipExtractor.dll");
        if (File.Exists(managedAssemblyPath))
        {
            return managedAssemblyPath;
        }

        throw new FileNotFoundException(Resources.Warn_ArchiveBundledSevenZipMissingDetail, managedAssemblyPath);
    }

    private static Type GetRequiredType(Assembly assembly, string typeName)
    {
        Type reflectedType = assembly?.GetType(typeName, throwOnError: false);
        if (reflectedType != null)
        {
            return reflectedType;
        }

        throw new MissingMemberException(assembly?.FullName ?? "(unknown assembly)", typeName);
    }

    private static ConstructorInfo GetRequiredConstructor(Type declaringType, params Type[] parameterTypes)
    {
        ConstructorInfo constructor = declaringType?.GetConstructor(parameterTypes);
        if (constructor != null)
        {
            return constructor;
        }

        throw new MissingMethodException(declaringType?.FullName ?? "(unknown type)", ".ctor");
    }

    private static MethodInfo GetRequiredMethod(Type declaringType, string methodName, params Type[] parameterTypes)
    {
        MethodInfo method = declaringType?.GetMethod(methodName, parameterTypes);
        if (method != null)
        {
            return method;
        }

        throw new MissingMethodException(declaringType?.FullName ?? "(unknown type)", methodName);
    }

    private static PropertyInfo GetRequiredProperty(Type declaringType, string propertyName)
    {
        PropertyInfo property = declaringType?.GetProperty(propertyName);
        if (property != null)
        {
            return property;
        }

        throw new MissingMemberException(declaringType?.FullName ?? "(unknown type)", propertyName);
    }

    private static object InvokeConstructor(ConstructorInfo constructor, params object[] arguments)
    {
        try
        {
            return constructor.Invoke(arguments);
        }
        catch (TargetInvocationException invocationException) when (invocationException.InnerException != null)
        {
            ExceptionDispatchInfo.Capture(invocationException.InnerException).Throw();
            throw;
        }
    }

    private static void InvokeMethod(MethodInfo method, object target, params object[] arguments)
    {
        try
        {
            method.Invoke(target, arguments);
        }
        catch (TargetInvocationException invocationException) when (invocationException.InnerException != null)
        {
            ExceptionDispatchInfo.Capture(invocationException.InnerException).Throw();
            throw;
        }
    }

    private static T GetPropertyValue<T>(PropertyInfo property, object target)
    {
        try
        {
            return (T)property.GetValue(target);
        }
        catch (TargetInvocationException invocationException) when (invocationException.InnerException != null)
        {
            ExceptionDispatchInfo.Capture(invocationException.InnerException).Throw();
            throw;
        }
    }

    private static List<ArchiveEntryMetadata> ExtractArchiveEntries(string archivePath, string nativeLibraryPath, string extractedTempDirectoryPath)
    {
        string managedAssemblyPath = ResolveBundledSevenZipExtractorAssemblyPath();
        Assembly managedAssembly = Assembly.LoadFrom(managedAssemblyPath);
        Type archiveFileType = GetRequiredType(managedAssembly, "SevenZipExtractor.ArchiveFile");
        Type entryType = GetRequiredType(managedAssembly, "SevenZipExtractor.Entry");
        ConstructorInfo archiveFileConstructor = GetRequiredConstructor(archiveFileType, typeof(string), typeof(string));
        MethodInfo extractMethod = GetRequiredMethod(archiveFileType, "Extract", typeof(string), typeof(bool), typeof(string));
        PropertyInfo entriesProperty = GetRequiredProperty(archiveFileType, "Entries");
        PropertyInfo fileNameProperty = GetRequiredProperty(entryType, "FileName");
        PropertyInfo isFolderProperty = GetRequiredProperty(entryType, "IsFolder");
        PropertyInfo creationTimeProperty = GetRequiredProperty(entryType, "CreationTime");
        PropertyInfo lastAccessTimeProperty = GetRequiredProperty(entryType, "LastAccessTime");
        PropertyInfo lastWriteTimeProperty = GetRequiredProperty(entryType, "LastWriteTime");
        List<ArchiveEntryMetadata> archiveEntries = new List<ArchiveEntryMetadata>();
        // NOTE:
        // We load the managed wrapper from the bundled libs directory instead of relying on
        // MSBuild's reference resolution because this repo carries historical release outputs
        // that can confuse assembly selection during local builds.
        using (IDisposable archiveFile = (IDisposable)InvokeConstructor(archiveFileConstructor, archivePath, nativeLibraryPath))
        {
            InvokeMethod(extractMethod, archiveFile, extractedTempDirectoryPath, false, null);
            IEnumerable reflectedEntries = GetPropertyValue<IEnumerable>(entriesProperty, archiveFile);
            if (reflectedEntries == null)
            {
                return archiveEntries;
            }

            foreach (object reflectedEntry in reflectedEntries)
            {
                archiveEntries.Add(new ArchiveEntryMetadata
                {
                    FileName = GetPropertyValue<string>(fileNameProperty, reflectedEntry),
                    IsFolder = GetPropertyValue<bool>(isFolderProperty, reflectedEntry),
                    CreationTime = GetPropertyValue<DateTime>(creationTimeProperty, reflectedEntry),
                    LastAccessTime = GetPropertyValue<DateTime>(lastAccessTimeProperty, reflectedEntry),
                    LastWriteTime = GetPropertyValue<DateTime>(lastWriteTimeProperty, reflectedEntry)
                });
            }
        }

        return archiveEntries;
    }

    private static bool IsPathUnderDirectory(string fullPath, string rootDirectoryPath)
    {
        if (string.IsNullOrWhiteSpace(fullPath) || string.IsNullOrWhiteSpace(rootDirectoryPath))
        {
            return false;
        }

        string normalizedRootDirectoryPath = rootDirectoryPath.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar) + Path.DirectorySeparatorChar;
        return fullPath.StartsWith(normalizedRootDirectoryPath, StringComparison.OrdinalIgnoreCase);
    }

    private static void DeleteExtractedTemporaryDirectory(string tempDirectoryPath, IFileMutationService fileMutationService, FileMutationOptions targetOnlyFileMutationOptions, Action<string> logInfo)
    {
        if (string.IsNullOrWhiteSpace(tempDirectoryPath) || !Directory.Exists(tempDirectoryPath))
        {
            return;
        }

        try
        {
            if (fileMutationService != null)
            {
                fileMutationService.DeleteDirectoryDirect(tempDirectoryPath, recursive: true, targetOnlyFileMutationOptions);
            }
            else
            {
                Directory.Delete(tempDirectoryPath, recursive: true);
            }
        }
        catch (Exception cleanupException)
        {
            logInfo?.Invoke("auto_install cleanup_failed path=" + tempDirectoryPath + " error=" + cleanupException.Message);
        }
    }

    public List<string> ExpandInstallSources(
        IEnumerable<string> installPaths,
        IFileMutationService fileMutationService,
        FileMutationOptions targetOnlyFileMutationOptions,
        Action<string> logInfo = null,
        IBmsLibraryDialogService dialogService = null,
        Action onEachSourceProcessed = null,
        CancellationToken token = default(CancellationToken))
    {
        string[] archiveExtensions = new string[4] { ".zip", ".7z", ".rar", ".lzh" };
        List<string> expandedPaths = new List<string>();
        foreach (string installPath in installPaths ?? Enumerable.Empty<string>())
        {
            if (token.IsCancellationRequested)
            {
                break;
            }
            string extractedTempDirectoryPath = null;
            try
            {
                if (!archiveExtensions.Any((string ext) => installPath.EndsWith(ext, StringComparison.OrdinalIgnoreCase)))
                {
                    expandedPaths.Add(installPath);
                }
                else
                {
                    extractedTempDirectoryPath = TempDirectoryPublisher.Get();
                    string sevenZipLibraryPath = ResolveBundledSevenZipLibraryPath();
                    // NOTE:
                    // The wrapper otherwise falls back to machine-level discovery.
                    // We always bind to the bundled managed/native pair so archive handling stays deterministic across environments.
                    foreach (ArchiveEntryMetadata entry in ExtractArchiveEntries(installPath, sevenZipLibraryPath, extractedTempDirectoryPath))
                    {
                        if (string.IsNullOrWhiteSpace(entry.FileName))
                        {
                            continue;
                        }

                        string entryPath = entry.FileName.Replace('/', Path.DirectorySeparatorChar);
                        string fullPath = Path.GetFullPath(Path.Combine(extractedTempDirectoryPath, entryPath));
                        if (!IsPathUnderDirectory(fullPath, extractedTempDirectoryPath))
                        {
                            continue;
                        }

                        bool isFile = !entry.IsFolder && File.Exists(fullPath);
                        bool isDirectory = entry.IsFolder && Directory.Exists(fullPath);
                        if (!isFile && !isDirectory)
                        {
                            continue;
                        }

                        if (isFile)
                        {
                            if (entry.LastWriteTime <= DateTime.MinValue)
                            {
                                logInfo?.Invoke("auto_install metadata_restore_required_failed archive=" + installPath + " path=" + fullPath + " reason=last_write_time_missing");
                                throw new RequiredArchiveMetadataRestoreException(fullPath, Resources.Warn_ArchiveLastWriteTimeMissingDetail);
                            }

                            try
                            {
                                fileMutationService.SetTimestamps(fullPath, false, null, entry.LastWriteTime, targetOnlyFileMutationOptions);
                            }
                            catch (Exception lastWriteTimeRestoreException)
                            {
                                logInfo?.Invoke("auto_install metadata_restore_required_failed archive=" + installPath + " path=" + fullPath + " error=" + lastWriteTimeRestoreException.Message);
                                throw new RequiredArchiveMetadataRestoreException(fullPath, lastWriteTimeRestoreException.Message);
                            }
                        }
                        else if (isDirectory && entry.LastWriteTime > DateTime.MinValue)
                        {
                            try
                            {
                                fileMutationService.SetTimestamps(fullPath, true, null, entry.LastWriteTime, targetOnlyFileMutationOptions);
                            }
                            catch (Exception directoryLastWriteRestoreException)
                            {
                                logInfo?.Invoke("auto_install metadata_restore_optional_failed archive=" + installPath + " path=" + fullPath + " target=directory_last_write_time error=" + directoryLastWriteRestoreException.Message);
                            }
                        }

                        DateTime? creationTimeToRestore = entry.CreationTime > DateTime.MinValue ? entry.CreationTime : (DateTime?)null;
                        if (creationTimeToRestore.HasValue)
                        {
                            try
                            {
                                fileMutationService.SetTimestamps(fullPath, isDirectory, creationTimeToRestore, null, targetOnlyFileMutationOptions);
                            }
                            catch (Exception creationTimeRestoreException)
                            {
                                logInfo?.Invoke("auto_install metadata_restore_optional_failed archive=" + installPath + " path=" + fullPath + " target=creation_time error=" + creationTimeRestoreException.Message);
                            }
                        }

                        if (entry.LastAccessTime > DateTime.MinValue)
                        {
                            try
                            {
                                if (isFile)
                                {
                                    File.SetLastAccessTime(fullPath, entry.LastAccessTime);
                                }
                                else
                                {
                                    Directory.SetLastAccessTime(fullPath, entry.LastAccessTime);
                                }
                            }
                            catch (Exception lastAccessTimeRestoreException)
                            {
                                logInfo?.Invoke("auto_install metadata_restore_optional_failed archive=" + installPath + " path=" + fullPath + " target=last_access_time error=" + lastAccessTimeRestoreException.Message);
                            }
                        }
                    }
                    expandedPaths.Add(extractedTempDirectoryPath);
                    extractedTempDirectoryPath = null;
                }
            }
            catch (RequiredArchiveMetadataRestoreException metadataRestoreException)
            {
                DeleteExtractedTemporaryDirectory(extractedTempDirectoryPath, fileMutationService, targetOnlyFileMutationOptions, logInfo);
                dialogService?.Show(
                    string.Format(Resources.Warn_ArchiveTimestampRestoreFailed, installPath, metadataRestoreException.RestoredPath, metadataRestoreException.Message),
                    Resources.MessageBoxTitle_Warning,
                    MessageBoxButton.OK,
                    MessageBoxImage.Exclamation,
                    MessageBoxResult.OK);
            }
            catch (Exception ex)
            {
                DeleteExtractedTemporaryDirectory(extractedTempDirectoryPath, fileMutationService, targetOnlyFileMutationOptions, logInfo);
                logInfo?.Invoke("auto_install extract_failed path=" + installPath + " error=" + ex.Message);
                dialogService?.Show(
                    string.Format(Resources.Warn_ArchiveExtractFailed, installPath, ex.Message),
                    Resources.MessageBoxTitle_Warning,
                    MessageBoxButton.OK,
                    MessageBoxImage.Exclamation,
                    MessageBoxResult.OK);
            }
            finally
            {
                onEachSourceProcessed?.Invoke();
            }
        }
        return expandedPaths;
    }

    public bool MovePackageFiles(
        BMSPackage package,
        string installationDirectory,
        BmsLibraryOptionsSnapshot options,
        Func<IEnumerable<BMSFile>, string, string, string> createFolderPath,
        Func<Exception, string> getDisplayedExceptionMessage,
        IFileMutationService fileMutationService,
        IBmsLibraryDialogService dialogService,
        FileMutationOptions targetOnlyFileMutationOptions,
        FileMutationOptions recursiveDirectoryTreeFileMutationOptions,
        Action<string> logInstallPerformance,
        bool showMessageBoxOnInstallFail = true,
        bool deleteAllContents = false,
        HashSet<string> existingHashes = null,
        ISet<string> excludedComponentPaths = null)
    {
        if (package == null)
        {
            throw new ArgumentNullException(nameof(package));
        }
        string sourcePath = package.path;
        string destinationDirectory = string.Empty;
        bool isSingleFile = false;
        bool isAutoNaming = false;
        List<string> installComponentFiles = new List<string>();
        List<BMSFile> installBmsFiles;
        if (File.Exists(sourcePath))
        {
            installComponentFiles.Add(sourcePath);
            isSingleFile = true;
        }
        else
        {
            if (!Directory.Exists(sourcePath))
            {
                return false;
            }
            installComponentFiles = Directory.EnumerateFileSystemEntries(sourcePath).ToList();
            isAutoNaming = string.IsNullOrWhiteSpace(installationDirectory);
        }

        HashSet<string> installComponentPathSet = new HashSet<string>(installComponentFiles, StringComparer.OrdinalIgnoreCase);
        installBmsFiles = package.BMSFiles.Where((BMSFile file) => installComponentPathSet.Contains(file.path)).ToList();
        HashSet<string> installBmsPathSet = new HashSet<string>(installBmsFiles.Select((BMSFile file) => file.path), StringComparer.OrdinalIgnoreCase);
        installComponentFiles = installComponentFiles.Where((string path) => !installBmsPathSet.Contains(path)).ToList();
        if (excludedComponentPaths != null && excludedComponentPaths.Count > 0)
        {
            installComponentFiles = installComponentFiles.Where((string path) => !excludedComponentPaths.Contains(path)).ToList();
        }

        if (!string.IsNullOrWhiteSpace(installationDirectory))
        {
            HashSet<string> hashSnapshot = existingHashes ?? new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            List<BMSFile> skippedBmsFiles = installBmsFiles
                .Where(delegate (BMSFile bmsFile)
                {
                    string lookupKey = PendingChartEntry.GetPrimaryLookupHash(bmsFile);
                    return !string.IsNullOrWhiteSpace(lookupKey) && hashSnapshot.Contains(lookupKey);
                })
                .ToList();
            if (skippedBmsFiles.Count > 0)
            {
                HashSet<string> skipPathSet = new HashSet<string>(skippedBmsFiles.Select((BMSFile file) => file.path), StringComparer.OrdinalIgnoreCase);
                installBmsFiles = installBmsFiles.Where((BMSFile file) => !skipPathSet.Contains(file.path)).ToList();
                package.BMSFiles.RemoveAll((BMSFile file) => skipPathSet.Contains(file.path));
            }
        }

        try
        {
            if (string.IsNullOrWhiteSpace(installationDirectory))
            {
                destinationDirectory = createFolderPath?.Invoke(
                    package.BMSFiles,
                    options?.BMSInstallDir,
                    installComponentFiles.Select(Path.GetFileName).OrderByDescending((string fileName) => fileName.Length).FirstOrDefault() ?? string.Empty);
            }
            else
            {
                destinationDirectory = installationDirectory;
            }

            if (string.IsNullOrWhiteSpace(installationDirectory))
            {
                int dirSuffix = 1;
                string baseDirName = destinationDirectory;
                while (Directory.Exists(destinationDirectory) || File.Exists(destinationDirectory))
                {
                    dirSuffix++;
                    destinationDirectory = baseDirName + "(" + dirSuffix + ")";
                }
                if (!isAutoNaming)
                {
                    fileMutationService.EnsureDirectory(destinationDirectory, targetOnlyFileMutationOptions);
                }
            }

            if (isAutoNaming)
            {
                if (!Directory.Exists(sourcePath))
                {
                    throw new DirectoryNotFoundException(string.Format(Resources.Error_RenameDestDirNotFound, sourcePath));
                }
                fileMutationService.MoveDirectory(sourcePath, destinationDirectory, overwrite: true, recursiveDirectoryTreeFileMutationOptions);
            }
            else
            {
                if (options?.EnableSmartComponentOverwrite == true)
                {
                    MovePackageComponentsSmart(
                        package,
                        installComponentFiles,
                        destinationDirectory,
                        excludedComponentPaths,
                        options.KeepSmartOverwriteProtectedFilesByRenaming,
                        fileMutationService,
                        targetOnlyFileMutationOptions,
                        logInstallPerformance);
                }
                else
                {
                    installComponentFiles.AsParallel().ForAll(delegate (string path)
                    {
                        string componentDestinationPath = Path.Combine(destinationDirectory, Path.GetFileName(path));
                        if (File.Exists(path))
                        {
                            fileMutationService.MoveFile(path, componentDestinationPath, overwrite: true, targetOnlyFileMutationOptions);
                        }
                        else
                        {
                            if (!Directory.Exists(path))
                            {
                                throw new FileNotFoundException(Resources.Error_FileNotFound, path);
                            }
                            fileMutationService.MoveDirectory(path, componentDestinationPath, overwrite: true, recursiveDirectoryTreeFileMutationOptions);
                        }
                    });
                }

                foreach (BMSFile bmsFile in installBmsFiles)
                {
                    string destinationBmsPath = Path.Combine(destinationDirectory, Path.GetFileName(bmsFile.path));
                    while (File.Exists(destinationBmsPath) || Directory.Exists(destinationBmsPath))
                    {
                        string renamedFileName = Path.GetFileNameWithoutExtension(destinationBmsPath) + "_" + Path.GetExtension(destinationBmsPath);
                        destinationBmsPath = Path.Combine(destinationDirectory, Path.GetFileName(renamedFileName));
                    }
                    if (!File.Exists(bmsFile.path))
                    {
                        throw new FileNotFoundException(Resources.Error_FileNotFound, bmsFile.path);
                    }
                    fileMutationService.MoveFile(bmsFile.path, destinationBmsPath, overwrite: true, targetOnlyFileMutationOptions);
                    bmsFile.path = bmsFile.path.ReplaceFromEnd(Path.GetFileName(bmsFile.path), Path.GetFileName(destinationBmsPath), isIgnoreCase: true);
                }
            }
        }
        catch (Exception ex)
        {
            if (!showMessageBoxOnInstallFail)
            {
                return false;
            }
            dialogService?.Show(
                string.Format(Resources.Error_InstallFailed, package.path, destinationDirectory, getDisplayedExceptionMessage?.Invoke(ex) ?? ex.Message),
                Resources.MessageBoxTitle_Error,
                MessageBoxButton.OK,
                MessageBoxImage.Hand,
                MessageBoxResult.OK);
            return false;
        }

        if (isSingleFile)
        {
            package.BMSFiles.ForEach(delegate (BMSFile bmsInfo)
            {
                bmsInfo.path = Path.Combine(destinationDirectory, Path.GetFileName(bmsInfo.path));
                bmsInfo.parent = null;
                bmsInfo.folder = null;
                bmsInfo.adddate = null;
                bmsInfo.date = null;
            });
            package.path = Path.Combine(destinationDirectory, Path.GetFileName(package.path));
        }
        else
        {
            if (!(Directory.Exists(sourcePath) || isAutoNaming))
            {
                return false;
            }
            package.BMSFiles.ForEach(delegate (BMSFile bmsInfo)
            {
                bmsInfo.path = bmsInfo.path.ReplaceFromStart(sourcePath + Path.DirectorySeparatorChar, destinationDirectory + Path.DirectorySeparatorChar, isIgnoreCase: true);
                bmsInfo.parent = null;
                bmsInfo.folder = null;
                bmsInfo.adddate = null;
                bmsInfo.date = null;
            });
            package.path = destinationDirectory;
        }

        string directoryToDelete = string.Empty;
        if (package.delete_parent)
        {
            directoryToDelete = Path.GetDirectoryName(sourcePath);
        }
        else if (Directory.Exists(sourcePath))
        {
            directoryToDelete = sourcePath;
        }
        if (!string.IsNullOrWhiteSpace(directoryToDelete) && Directory.Exists(directoryToDelete))
        {
            string folderDeletionDecisionReason = deleteAllContents
                ? string.Empty
                : "not_empty deleteAllContents=False";
            if (deleteAllContents)
            {
                if (!TryBuildInstalledHashSnapshotForSafeCleanup(existingHashes, package.BMSFiles, out HashSet<string> installedHashesForCleanup, out folderDeletionDecisionReason))
                {
                    logInstallPerformance?.Invoke("Folder deletion skipped: path=" + directoryToDelete + " reason=" + folderDeletionDecisionReason);
                    return true;
                }

                // NOTE:
                // delete_parent は探索時の親候補フラグに過ぎないため、通常インストールでは
                // 実際に残ったファイルを見て「空」または「既所持譜面のみ」の場合にだけ再帰削除します。
                if (!CanDeleteDirectoryAfterInstall(directoryToDelete, installedHashesForCleanup, out folderDeletionDecisionReason))
                {
                    logInstallPerformance?.Invoke("Folder deletion skipped: path=" + directoryToDelete + " reason=" + folderDeletionDecisionReason);
                    return true;
                }
            }
            else if (HasRemainingDirectoryEntries(directoryToDelete))
            {
                logInstallPerformance?.Invoke("Folder deletion skipped: path=" + directoryToDelete + " reason=" + folderDeletionDecisionReason);
                return true;
            }

            FileMutationOptions deleteOptions = deleteAllContents ? recursiveDirectoryTreeFileMutationOptions : targetOnlyFileMutationOptions;
            try
            {
                fileMutationService.DeleteDirectoryDirect(directoryToDelete, deleteAllContents, deleteOptions);
                logInstallPerformance?.Invoke("Folder deletion success: path=" + directoryToDelete + " deleteAllContents=" + deleteAllContents + " reason=" + folderDeletionDecisionReason);
            }
            catch (FileMutationException ex)
            {
                if (!deleteAllContents && HasRemainingDirectoryEntries(directoryToDelete))
                {
                    logInstallPerformance?.Invoke("Folder deletion skipped after failure: path=" + directoryToDelete + " reason=not_empty_after_failure deleteAllContents=False attempts=" + ex.AttemptCount);
                    return true;
                }

                logInstallPerformance?.Invoke(string.Format(
                    "Folder deletion failed: path={0} attempts={1} normalizedReadOnly={2} win32={3} errorType={4} error={5}",
                    directoryToDelete,
                    ex.AttemptCount,
                    ex.NormalizedReadOnlyCount,
                    ex.Win32ErrorCode,
                    ex.RootCause.GetType().FullName,
                    getDisplayedExceptionMessage?.Invoke(ex) ?? ex.Message));
                dialogService?.Show(
                    string.Format(Resources.Error_FolderDeleteFailed, directoryToDelete, getDisplayedExceptionMessage?.Invoke(ex) ?? ex.Message),
                    Resources.MessageBoxTitle_Error,
                    MessageBoxButton.OK,
                    MessageBoxImage.Hand,
                    MessageBoxResult.OK);
            }
        }
        return true;
    }

    private static bool TryBuildInstalledHashSnapshotForSafeCleanup(HashSet<string> existingHashes, IEnumerable<BMSFile> installedPackageFiles, out HashSet<string> installedHashes, out string reason)
    {
        installedHashes = existingHashes != null
            ? new HashSet<string>(existingHashes, StringComparer.OrdinalIgnoreCase)
            : new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (BMSFile installedPackageFile in installedPackageFiles ?? Enumerable.Empty<BMSFile>())
        {
            if (installedPackageFile == null)
            {
                continue;
            }
            string lookupKey = PendingChartEntry.GetPrimaryLookupHash(installedPackageFile);
            if (string.IsNullOrWhiteSpace(lookupKey))
            {
                reason = "current_package_hash_unavailable path=" + installedPackageFile.path;
                return false;
            }
            installedHashes.Add(lookupKey);
        }
        reason = "safe_cleanup_allowed pending_installed_hashes=" + installedHashes.Count;
        return true;
    }

    private static bool CanDeleteDirectoryAfterInstall(string directoryPath, ISet<string> installedHashes, out string reason)
    {
        List<string> remainingFiles;
        try
        {
            remainingFiles = Directory.EnumerateFiles(directoryPath, "*", System.IO.SearchOption.AllDirectories).ToList();
        }
        catch (Exception ex)
        {
            reason = "remaining_scan_failed path=" + directoryPath + " errorType=" + ex.GetType().FullName + " error=" + ex.Message;
            return false;
        }

        if (remainingFiles.Count == 0)
        {
            reason = "safe_cleanup_allowed no_remaining_files";
            return true;
        }

        foreach (string remainingFilePath in remainingFiles)
        {
            if (!IsSupportedChartFilePath(remainingFilePath))
            {
                reason = "remaining_non_chart_file path=" + remainingFilePath;
                return false;
            }

            BMSFile remainingChart;
            try
            {
                remainingChart = PendingChartEntry.CreateFromFilePath(remainingFilePath);
            }
            catch (Exception ex)
            {
                reason = "remaining_chart_load_failed path=" + remainingFilePath + " errorType=" + ex.GetType().FullName + " error=" + ex.Message;
                return false;
            }

            string remainingLookupKey = PendingChartEntry.GetPrimaryLookupHash(remainingChart);
            if (remainingChart == null || string.IsNullOrWhiteSpace(remainingLookupKey))
            {
                reason = "remaining_chart_hash_unavailable path=" + remainingFilePath;
                return false;
            }

            if (installedHashes == null || !installedHashes.Contains(remainingLookupKey))
            {
                reason = "remaining_chart_not_installed path=" + remainingFilePath + " hash=" + remainingLookupKey;
                return false;
            }
        }

        reason = "safe_cleanup_allowed remaining_files_all_installed_charts count=" + remainingFiles.Count;
        return true;
    }

    private static bool IsSupportedChartFilePath(string filePath)
    {
        return PendingChartEntry.IsSupportedChartFilePath(filePath);
    }

    public AutoInstallWorkflowResult PrepareAutoInstallWorkflow(
        IEnumerable<string> installPaths,
        IEnumerable<BMSPackage> currentPendingPackages,
        IEnumerable<string> knownChartDirectories,
        Func<BMSFile, bool> isInstalledChart,
        double dupRateThreshInOnePkg,
        Func<BMSFile, bool> requiresPendingWarning,
        CancellationToken token = default(CancellationToken))
    {
        AutoInstallWorkflowResult result = new AutoInstallWorkflowResult();
        Stopwatch totalStopwatch = Stopwatch.StartNew();
        Stopwatch discoveryStopwatch = Stopwatch.StartNew();
        List<string> normalizedInstallPaths = (installPaths ?? Enumerable.Empty<string>())
            .Where((string path) => !string.IsNullOrWhiteSpace(path) && (File.Exists(path) || Directory.Exists(path)))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
        if (token.IsCancellationRequested)
        {
            totalStopwatch.Stop();
            result.TotalMs = totalStopwatch.ElapsedMilliseconds;
            return result;
        }
        IEnumerable<IGrouping<string, string>> groupedPaths = from file in normalizedInstallPaths
                                                              group file by Path.GetDirectoryName(file)?.ToUpperInvariant();
        List<BMSPackage> discoveredPackages = new List<BMSPackage>();
        foreach (IGrouping<string, string> paths in groupedPaths)
        {
            if (string.IsNullOrWhiteSpace(paths.Key) || !Directory.Exists(paths.Key))
            {
                continue;
            }
            List<string> files = paths.Where(File.Exists).ToList();
            List<string> directories = paths.Where(Directory.Exists).ToList();
            List<string> chartFiles = files.Where((string file) => PendingChartEntry.IsSupportedChartFilePath(file) && File.Exists(file)).ToList();
            string[] topEntries = Directory.GetFileSystemEntries(paths.Key, "*", System.IO.SearchOption.TopDirectoryOnly);
            if (files.Count + directories.Count == topEntries.Length)
            {
                BmsPackageDiscoveryResult discoveryResult = SearchBmsFilesRecursivelyWithMetadata(paths.Key, dupRateThreshInOnePkg);
                discoveredPackages.AddRange(discoveryResult.Packages);
                result.RegroupEligibleSourceDirectories.AddRange(discoveryResult.RegroupEligibleSourceDirectories);
                continue;
            }
            if (chartFiles.Count > 0 && chartFiles.Any(delegate (string chartFilePath)
                {
                    BMSFile bMSFile = PendingChartEntry.CreateFromFilePath(chartFilePath);
                    if (bMSFile == null)
                    {
                        return false;
                    }
                    bMSFile.SetHealthStatus(null, forceUpdate: false, memClear: false);
                    return bMSFile.maintenanceInfo.wav_files_existing > 0 || bMSFile.maintenanceInfo.bga_files_existing > 0 || bMSFile.maintenanceInfo.movie_files_existing > 0;
                }))
            {
                BmsPackageDiscoveryResult discoveryResult = SearchBmsFilesRecursivelyWithMetadata(paths.Key, dupRateThreshInOnePkg);
                discoveredPackages.AddRange(discoveryResult.Packages);
                result.RegroupEligibleSourceDirectories.AddRange(discoveryResult.RegroupEligibleSourceDirectories);
                continue;
            }
            List<BMSPackage> singleFilePackages = chartFiles.Select((string filePath) => new BMSPackage
            {
                path = filePath,
                delete_parent = false
            }).ToList();
            List<BMSPackage> directoryPackages = new List<BMSPackage>();
            foreach (string dir in directories)
            {
                BmsPackageDiscoveryResult discoveryResult = SearchBmsFilesRecursivelyWithMetadata(dir, dupRateThreshInOnePkg);
                directoryPackages.AddRange(discoveryResult.Packages);
                result.RegroupEligibleSourceDirectories.AddRange(discoveryResult.RegroupEligibleSourceDirectories);
            }
            discoveredPackages.AddRange(singleFilePackages.Concat(directoryPackages));
        }
        discoveryStopwatch.Stop();
        result.DiscoveryMs = discoveryStopwatch.ElapsedMilliseconds;

        List<string> libraryDirectories = (knownChartDirectories ?? Enumerable.Empty<string>()).Where((string dir) => !string.IsNullOrWhiteSpace(dir)).ToList();
        List<BMSPackage> pendingPackages = (currentPendingPackages ?? Enumerable.Empty<BMSPackage>()).Where((BMSPackage pkg) => pkg != null).ToList();
        discoveredPackages = discoveredPackages
            .Where((BMSPackage pkg) => pkg != null && !libraryDirectories.Any((string dir) => pkg.path.StartsWith(dir + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)))
            .Where((BMSPackage newPkg) => !pendingPackages.Any((BMSPackage oldPkg) => !string.IsNullOrWhiteSpace(oldPkg.path) && newPkg.path.Equals(oldPkg.path, StringComparison.OrdinalIgnoreCase)))
            .Where((BMSPackage newPkg) => !pendingPackages.Any((BMSPackage oldPkg) => !string.IsNullOrWhiteSpace(oldPkg.path) && Directory.Exists(oldPkg.path) && newPkg.path.StartsWith(oldPkg.path + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)))
            .ToList();
        List<string> distinctWorkflowRegroupEligibleSourceDirectories = result.RegroupEligibleSourceDirectories
            .Where((string sourceDirectoryPath) => !string.IsNullOrWhiteSpace(sourceDirectoryPath))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
        result.RegroupEligibleSourceDirectories.Clear();
        result.RegroupEligibleSourceDirectories.AddRange(distinctWorkflowRegroupEligibleSourceDirectories);
        result.DiscoveredPackages.AddRange(discoveredPackages);
        if (discoveredPackages.Count == 0)
        {
            totalStopwatch.Stop();
            result.TotalMs = totalStopwatch.ElapsedMilliseconds;
            return result;
        }

        if (discoveredPackages.Any((BMSPackage newPkg) => !string.IsNullOrWhiteSpace(newPkg.path) && Directory.Exists(newPkg.path)))
        {
            List<BMSPackage> pendingPackagesToRemove = pendingPackages.Where(delegate (BMSPackage oldPkg)
            {
                IEnumerable<BMSPackage> sourcePackages = discoveredPackages.Where((BMSPackage newPkg) => !string.IsNullOrWhiteSpace(newPkg.path) && Directory.Exists(newPkg.path));
                string dir;
                if (Directory.Exists(oldPkg.path))
                {
                    dir = oldPkg.path + Path.DirectorySeparatorChar;
                }
                else
                {
                    if (!File.Exists(oldPkg.path))
                    {
                        return true;
                    }
                    dir = oldPkg.path;
                }
                return sourcePackages.Any((BMSPackage newPkg) => dir.StartsWith(newPkg.path + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase));
            }).ToList();
            result.PendingPackagesToRemove.AddRange(pendingPackagesToRemove);
        }

        Stopwatch classificationStopwatch = Stopwatch.StartNew();
        Stopwatch installedCheckStopwatch = Stopwatch.StartNew();
        Dictionary<BMSPackage, bool> pendingByPackage = new Dictionary<BMSPackage, bool>();
        foreach (BMSPackage pkg in discoveredPackages)
        {
            bool hasInstalledChart = false;
            foreach (BMSFile bmsFile in pkg.BMSFiles.Where((BMSFile file) => file != null))
            {
                if (isInstalledChart != null && isInstalledChart(bmsFile))
                {
                    bmsFile.warning = Resources.Warning_AlreadyInstalled;
                    hasInstalledChart = true;
                }
            }
            pendingByPackage[pkg] = hasInstalledChart;
        }
        installedCheckStopwatch.Stop();
        result.InstalledCheckMs = installedCheckStopwatch.ElapsedMilliseconds;

        Stopwatch warningClassificationStopwatch = Stopwatch.StartNew();
        foreach (BMSPackage pkg in discoveredPackages)
        {
            if (pendingByPackage[pkg])
            {
                continue;
            }
            bool isSingleFilePackage = !Directory.Exists(pkg.path);
            foreach (BMSFile bmsFile in pkg.BMSFiles.Where((BMSFile file) => file != null))
            {
                bool isBmson = PendingChartEntry.IsBmsonChartFile(bmsFile);
                if (isSingleFilePackage)
                {
                    bmsFile.warning = isBmson ? Resources.Warning_SingleBmsonFile : Resources.Warning_SingleBmsFile;
                    pendingByPackage[pkg] = true;
                    break;
                }
                bool hasDefinedResources = ChartResourceSnapshot.Create(bmsFile).TotalReferenceCount > 0;
                if (!hasDefinedResources)
                {
                    continue;
                }
                bmsFile.SetHealthStatus(null, forceUpdate: false, memClear: false);
                if (requiresPendingWarning != null && requiresPendingWarning(bmsFile))
                {
                    pendingByPackage[pkg] = true;
                    break;
                }
            }
        }
        warningClassificationStopwatch.Stop();
        result.WarningClassificationMs = warningClassificationStopwatch.ElapsedMilliseconds;
        foreach (BMSPackage discoveredPackage in discoveredPackages)
        {
            if (pendingByPackage.TryGetValue(discoveredPackage, out bool isPending) && isPending)
            {
                result.PendingPackagesToAdd.Add(discoveredPackage);
            }
            else
            {
                result.AutoInstallCandidates.Add(discoveredPackage);
            }
        }
        classificationStopwatch.Stop();
        result.ClassificationMs = classificationStopwatch.ElapsedMilliseconds;
        totalStopwatch.Stop();
        result.TotalMs = totalStopwatch.ElapsedMilliseconds;
        return result;
    }

    public AutoInstallApplyResult ApplyAutoInstallWorkflow(
        AutoInstallWorkflowResult workflow,
        bool keepInstallablePackagesPending,
        bool canAutoInstallImmediately,
        Func<IEnumerable<BMSPackage>, List<BMSPackage>> installPackages,
        CancellationToken token = default(CancellationToken))
    {
        AutoInstallApplyResult result = new AutoInstallApplyResult();
        if (workflow == null)
        {
            return result;
        }
        Stopwatch totalStopwatch = Stopwatch.StartNew();
        Stopwatch installStopwatch = Stopwatch.StartNew();
        if (workflow.PendingPackagesToRemove.Count > 0)
        {
            result.PendingPackagesToRemove.AddRange(workflow.PendingPackagesToRemove.Where((BMSPackage pkg) => pkg != null));
            result.InstallRowsToDelete.AddRange(
                workflow.PendingPackagesToRemove
                    .Where((BMSPackage pkg) => pkg != null && !string.IsNullOrWhiteSpace(pkg.path))
                    .Select((BMSPackage pkg) => pkg.path)
                    .Distinct(StringComparer.OrdinalIgnoreCase));
        }
        List<BMSPackage> pendingPackagesToAdd = workflow.PendingPackagesToAdd.Where((BMSPackage pkg) => pkg != null).ToList();
        if (workflow.AutoInstallCandidates.Count > 0)
        {
            if (token.IsCancellationRequested)
            {
                installStopwatch.Stop();
                result.InstallMs = installStopwatch.ElapsedMilliseconds;
                totalStopwatch.Stop();
                result.TotalMs = totalStopwatch.ElapsedMilliseconds;
                return result;
            }
            if (!keepInstallablePackagesPending && canAutoInstallImmediately)
            {
                List<BMSPackage> failedPackages = installPackages?.Invoke(workflow.AutoInstallCandidates) ?? new List<BMSPackage>();
                HashSet<BMSPackage> failedSet = new HashSet<BMSPackage>(failedPackages);
                result.AutoInstallFailures.AddRange(failedPackages.Where((BMSPackage pkg) => pkg != null));
                result.AutoInstalledPackages.AddRange(workflow.AutoInstallCandidates.Where((BMSPackage pkg) => pkg != null && !failedSet.Contains(pkg)));
                pendingPackagesToAdd = pendingPackagesToAdd.Concat(failedPackages).ToList();
            }
            else
            {
                pendingPackagesToAdd = pendingPackagesToAdd.Concat(workflow.AutoInstallCandidates.Where((BMSPackage pkg) => pkg != null)).ToList();
            }
        }
        installStopwatch.Stop();
        result.InstallMs = installStopwatch.ElapsedMilliseconds;

        Stopwatch applyStopwatch = Stopwatch.StartNew();
        result.PendingPackagesToAdd.AddRange(pendingPackagesToAdd);
        result.InstallRowsToUpsert.AddRange(pendingPackagesToAdd.Where((BMSPackage pkg) => !string.IsNullOrWhiteSpace(pkg.path)));
        result.EstimateTargets.AddRange(pendingPackagesToAdd);
        applyStopwatch.Stop();
        result.ApplyMs = applyStopwatch.ElapsedMilliseconds;
        totalStopwatch.Stop();
        result.TotalMs = totalStopwatch.ElapsedMilliseconds;
        return result;
    }

    public PendingPackageMutationDelta BuildPendingPackageMutationDelta(IEnumerable<BMSPackage> pendingPackages, IEnumerable<BMSPackage> packagesToRemove = null, IEnumerable<BMSFile> filesToRemove = null, bool clearAll = false)
    {
        List<BMSPackage> currentPending = (pendingPackages ?? Enumerable.Empty<BMSPackage>()).Where((BMSPackage pkg) => pkg != null).ToList();
        PendingPackageMutationDelta delta = new PendingPackageMutationDelta();
        if (clearAll)
        {
            delta.HasChanges = currentPending.Count > 0;
            delta.InstallPathsToDelete = currentPending.Where((BMSPackage pkg) => !string.IsNullOrWhiteSpace(pkg.path)).Select((BMSPackage pkg) => pkg.path).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
            return delta;
        }
        HashSet<BMSPackage> removedPackages = new HashSet<BMSPackage>((packagesToRemove ?? Enumerable.Empty<BMSPackage>()).Where((BMSPackage pkg) => pkg != null));
        HashSet<string> removedPackagePaths = new HashSet<string>((packagesToRemove ?? Enumerable.Empty<BMSPackage>()).Where((BMSPackage pkg) => pkg != null && !string.IsNullOrWhiteSpace(pkg.path)).Select((BMSPackage pkg) => pkg.path), StringComparer.OrdinalIgnoreCase);
        List<BMSFile> removedFileList = (filesToRemove ?? Enumerable.Empty<BMSFile>()).Where((BMSFile file) => file != null).ToList();
        HashSet<string> removedPaths = new HashSet<string>(removedFileList.Where((BMSFile file) => !string.IsNullOrWhiteSpace(file.path)).Select((BMSFile file) => file.path), StringComparer.OrdinalIgnoreCase);
        HashSet<BMSFile> removedFileRefs = new HashSet<BMSFile>(removedFileList);
        bool removeFiles = removedFileList.Count > 0;
        HashSet<string> installPathsToDelete = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (BMSPackage package in currentPending)
        {
            if (removedPackages.Contains(package) || (!string.IsNullOrWhiteSpace(package.path) && removedPackagePaths.Contains(package.path)))
            {
                delta.HasChanges = true;
                if (!string.IsNullOrWhiteSpace(package.path))
                {
                    installPathsToDelete.Add(package.path);
                }
                continue;
            }
            if (!removeFiles)
            {
                delta.RemainingPackages.Add(package);
                continue;
            }
            List<BMSFile> packageFiles = package.BMSFiles.Where((BMSFile file) => file != null).ToList();
            if (packageFiles.Count == 0)
            {
                delta.RemainingPackages.Add(package);
                continue;
            }
            List<BMSFile> remainingFiles = packageFiles.Where((BMSFile file) => !IsMatchedRemovedFile(file, removedPaths, removedFileRefs)).ToList();
            if (remainingFiles.Count == packageFiles.Count)
            {
                delta.RemainingPackages.Add(package);
                continue;
            }
            delta.HasChanges = true;
            if (remainingFiles.Count == 0)
            {
                if (!string.IsNullOrWhiteSpace(package.path))
                {
                    installPathsToDelete.Add(package.path);
                }
                continue;
            }
            package.BMSFiles.Clear();
            package.BMSFiles.AddRange(remainingFiles);
            delta.RemainingPackages.Add(package);
        }
        delta.InstallPathsToDelete = installPathsToDelete.ToList();
        return delta;
    }

    public PendingInstallBatchPlan BuildEstimatedInstallBatchPlan(IEnumerable<BMSPackage> requestedPackages, IEnumerable<BMSPackage> currentPendingPackages, IEnumerable<BMSFile> installedFiles, IEnumerable<LR2SongDBExtended.bmson_song> installedBmsonSongs, bool deletePendingPackageSourceAfterInstall, Func<BMSPackage, string, ISet<string>, int> countComponentMoveTargets)
    {
        Stopwatch planStopwatch = Stopwatch.StartNew();
        PendingInstallBatchPlan plan = new PendingInstallBatchPlan();
        Stopwatch filterStopwatch = Stopwatch.StartNew();
        List<BMSPackage> pendingSnapshot = (currentPendingPackages ?? Enumerable.Empty<BMSPackage>()).Where((BMSPackage pkg) => pkg != null).ToList();
        HashSet<BMSPackage> pendingPackageSet = new HashSet<BMSPackage>(pendingSnapshot);
        plan.SelectedPendingPackages.AddRange(DeduplicatePackagesByPathOrReference(requestedPackages).Where((BMSPackage pkg) => pendingPackageSet.Contains(pkg)));
        filterStopwatch.Stop();
        plan.FilterMs = filterStopwatch.ElapsedMilliseconds;
        plan.SelectedPendingCount = plan.SelectedPendingPackages.Count;

        Stopwatch groupBuildStopwatch = Stopwatch.StartNew();
        HashSet<string> installedHashes = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (BMSFile installedFile in installedFiles ?? Enumerable.Empty<BMSFile>())
        {
            string key = PendingChartEntry.GetPrimaryLookupHash(installedFile);
            if (!string.IsNullOrWhiteSpace(key))
            {
                installedHashes.Add(key);
            }
        }
        foreach (LR2SongDBExtended.bmson_song installedBmsonSong in installedBmsonSongs ?? Enumerable.Empty<LR2SongDBExtended.bmson_song>())
        {
            string key2 = PendingChartEntry.GetPrimaryLookupHash(installedBmsonSong);
            if (!string.IsNullOrWhiteSpace(key2))
            {
                installedHashes.Add(key2);
            }
        }
        HashSet<string> moveGuardHashes = new HashSet<string>(installedHashes, StringComparer.OrdinalIgnoreCase);
        HashSet<string> reservedHashes = new HashSet<string>(moveGuardHashes, StringComparer.OrdinalIgnoreCase);
        Dictionary<string, PendingInstallBatchGroup> groupsByDestination = new Dictionary<string, PendingInstallBatchGroup>(StringComparer.OrdinalIgnoreCase);
        foreach (BMSPackage originalPackage in plan.SelectedPendingPackages)
        {
            List<BMSFile> packageFiles = (originalPackage.BMSFiles ?? new List<BMSFile>()).Where((BMSFile file) => file != null).ToList();
            if (packageFiles.Count == 0)
            {
                continue;
            }
            if (originalPackage.DeferredEstimateReason != PendingEstimateDeferredReason.None
                && packageFiles.All((BMSFile file) => string.IsNullOrWhiteSpace(file.instl_dst)))
            {
                plan.DeferredManualHoldCount++;
                continue;
            }
            List<BMSFile> installedInLibraryFiles = new List<BMSFile>();
            List<BMSFile> installTargetPackageFiles = new List<BMSFile>();
            List<BMSFile> duplicateInBatchFiles = new List<BMSFile>();
            foreach (BMSFile packageFile in packageFiles)
            {
                string lookupKey = PendingChartEntry.GetPrimaryLookupHash(packageFile);
                if (string.IsNullOrWhiteSpace(lookupKey))
                {
                    installTargetPackageFiles.Add(packageFile);
                }
                else if (installedHashes.Contains(lookupKey))
                {
                    installedInLibraryFiles.Add(packageFile);
                }
                else if (reservedHashes.Contains(lookupKey))
                {
                    duplicateInBatchFiles.Add(packageFile);
                }
                else
                {
                    reservedHashes.Add(lookupKey);
                    installTargetPackageFiles.Add(packageFile);
                }
            }
            ApplyAlreadyInstalledWarning(installedInLibraryFiles);
            ApplyAlreadyInstalledWarning(duplicateInBatchFiles);
            List<BMSFile> installWorkPackageFiles = installTargetPackageFiles;
            bool isResourceOnlyInstall = false;
            string destinationDirectory = null;
            if (installTargetPackageFiles.Count == 0)
            {
                if (installedInLibraryFiles.Count == 0)
                {
                    continue;
                }
                List<string> destinations = packageFiles.Select((BMSFile file) => file.instl_dst).Where((string dst) => !string.IsNullOrWhiteSpace(dst)).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
                if (destinations.Count != 1)
                {
                    continue;
                }
                destinationDirectory = destinations[0];
                isResourceOnlyInstall = true;
                installWorkPackageFiles = packageFiles;
            }
            else
            {
                if (installTargetPackageFiles.Any((BMSFile file) => string.IsNullOrWhiteSpace(file.instl_dst)))
                {
                    continue;
                }
                destinationDirectory = installTargetPackageFiles.Select((BMSFile file) => file.instl_dst).FirstOrDefault();
                if (string.IsNullOrWhiteSpace(destinationDirectory) || installTargetPackageFiles.Any((BMSFile file) => !string.Equals(file.instl_dst, destinationDirectory, StringComparison.OrdinalIgnoreCase)))
                {
                    continue;
                }
            }
            BMSPackage installWorkPackage = new BMSPackage(installWorkPackageFiles)
            {
                path = originalPackage.path,
                delete_parent = originalPackage.delete_parent
            };
            HashSet<string> excludedPaths = null;
            if (installedInLibraryFiles.Count > 0 || duplicateInBatchFiles.Count > 0 || isResourceOnlyInstall)
            {
                excludedPaths = new HashSet<string>(installedInLibraryFiles.Where((BMSFile file) => !string.IsNullOrWhiteSpace(file.path)).Select((BMSFile file) => file.path), StringComparer.OrdinalIgnoreCase);
                foreach (BMSFile duplicateFile in duplicateInBatchFiles)
                {
                    if (!string.IsNullOrWhiteSpace(duplicateFile?.path))
                    {
                        excludedPaths.Add(duplicateFile.path);
                    }
                }
                if (isResourceOnlyInstall)
                {
                    foreach (BMSFile packageFile in packageFiles)
                    {
                        if (!string.IsNullOrWhiteSpace(packageFile?.path))
                        {
                            excludedPaths.Add(packageFile.path);
                        }
                    }
                }
                if (excludedPaths.Count == 0)
                {
                    excludedPaths = null;
                }
            }
            if (isResourceOnlyInstall)
            {
                int componentMoveTargetCount = countComponentMoveTargets?.Invoke(installWorkPackage, destinationDirectory, excludedPaths) ?? 0;
                if (componentMoveTargetCount == 0)
                {
                    if (deletePendingPackageSourceAfterInstall)
                    {
                        plan.CleanupOnlyCandidates.Add(originalPackage);
                    }
                    continue;
                }
            }
            if (!groupsByDestination.TryGetValue(destinationDirectory, out PendingInstallBatchGroup group))
            {
                group = new PendingInstallBatchGroup
                {
                    DestinationDirectory = destinationDirectory
                };
                groupsByDestination[destinationDirectory] = group;
                plan.Groups.Add(group);
            }
            group.Items.Add(new PendingInstallBatchItem
            {
                OriginalPackage = originalPackage,
                InstallWorkPackage = installWorkPackage,
                DestinationDirectory = destinationDirectory,
                ExcludedComponentPaths = excludedPaths,
                IsResourceOnlyInstall = isResourceOnlyInstall,
                InstallTargetFileCount = installTargetPackageFiles.Count
            });
            plan.GroupedPackageCount++;
            plan.InstallTargetFileCount += installTargetPackageFiles.Count;
        }
        groupBuildStopwatch.Stop();
        plan.GroupBuildMs = groupBuildStopwatch.ElapsedMilliseconds;
        plan.CleanupOnlyCandidateCount = plan.CleanupOnlyCandidates.Count;
        plan.MoveGuardHashes = moveGuardHashes;
        planStopwatch.Stop();
        plan.PlanBuildMs = planStopwatch.ElapsedMilliseconds;
        return plan;
    }

    public PendingInstallBatchResult ExecuteEstimatedInstallBatchPlan(
        PendingInstallBatchPlan plan,
        bool deletePendingPackageSourceAfterInstall,
        Func<IEnumerable<BMSPackage>, string, List<BMSFile>, List<BMSPackage>, Dictionary<BMSPackage, HashSet<string>>, HashSet<string>, bool, bool, List<BMSPackage>> installPackages,
        Func<BMSPackage, string, BMSPackage> createInstalledDisplayPackage,
        Func<BMSPackage, (bool Success, CleanupSourceKind SourceKind)> cleanupPendingPackageSource,
        Action<string> logInfo = null)
    {
        PendingInstallBatchResult result = new PendingInstallBatchResult();
        if (plan == null)
        {
            return result;
        }
        foreach (PendingInstallBatchGroup groupEntry in plan.Groups)
        {
            Stopwatch groupStopwatch = Stopwatch.StartNew();
            List<BMSPackage> destinationPackages = groupEntry.Items.Select((PendingInstallBatchItem item) => item.OriginalPackage).Where((BMSPackage pkg) => pkg != null).ToList();
            List<BMSPackage> installWorkPackages = groupEntry.Items.Select((PendingInstallBatchItem item) => item.InstallWorkPackage).Where((BMSPackage pkg) => pkg != null).ToList();
            Dictionary<BMSPackage, HashSet<string>> excludedComponentPathsByWorkPackage = groupEntry.Items
                .Where((PendingInstallBatchItem item) => item.ExcludedComponentPaths != null && item.InstallWorkPackage != null)
                .ToDictionary((PendingInstallBatchItem item) => item.InstallWorkPackage, (PendingInstallBatchItem item) => item.ExcludedComponentPaths);
            Stopwatch installStopwatch = Stopwatch.StartNew();
            List<BMSPackage> failedInstallWorkPackages = installPackages?.Invoke(
                installWorkPackages,
                groupEntry.DestinationDirectory,
                result.DeferredMaintenanceTargets,
                result.DeferredInstalledPackages,
                excludedComponentPathsByWorkPackage,
                plan.MoveGuardHashes,
                true,
                deletePendingPackageSourceAfterInstall) ?? new List<BMSPackage>();
            installStopwatch.Stop();
            Dictionary<BMSPackage, PendingInstallBatchItem> itemByInstallWorkPackage = groupEntry.Items
                .Where((PendingInstallBatchItem item) => item.InstallWorkPackage != null)
                .ToDictionary((PendingInstallBatchItem item) => item.InstallWorkPackage);
            HashSet<BMSPackage> failedOriginalPackages = new HashSet<BMSPackage>(
                failedInstallWorkPackages.Where((BMSPackage workPkg) => itemByInstallWorkPackage.ContainsKey(workPkg))
                    .Select((BMSPackage workPkg) => itemByInstallWorkPackage[workPkg].OriginalPackage));
            foreach (BMSPackage failedOriginalPackage in failedOriginalPackages)
            {
                if (failedOriginalPackage != null)
                {
                    result.FailedPackages.Add(failedOriginalPackage);
                }
            }
            Stopwatch installDbStopwatch = Stopwatch.StartNew();
            List<string> installRowsToDelete = new List<string>();
            foreach (PendingInstallBatchItem item in groupEntry.Items)
            {
                if (item == null || item.OriginalPackage == null || failedOriginalPackages.Contains(item.OriginalPackage))
                {
                    continue;
                }
                if (!string.IsNullOrWhiteSpace(item.OriginalPackage.path))
                {
                    installRowsToDelete.Add(item.OriginalPackage.path);
                    result.InstallRowsToDelete.Add(item.OriginalPackage.path);
                }
                if (item.IsResourceOnlyInstall)
                {
                    BMSPackage installedDisplayPackage = createInstalledDisplayPackage?.Invoke(item.OriginalPackage, groupEntry.DestinationDirectory);
                    if (installedDisplayPackage != null && installedDisplayPackage.BMSFiles != null && installedDisplayPackage.BMSFiles.Count > 0)
                    {
                        result.DeferredInstalledPackages.Add(installedDisplayPackage);
                    }
                }
                foreach (BMSFile packageFile in item.OriginalPackage.BMSFiles ?? Enumerable.Empty<BMSFile>())
                {
                    if (packageFile != null)
                    {
                        packageFile.instl_dst = null;
                    }
                }
            }
            installDbStopwatch.Stop();
            Stopwatch pendingMarkStopwatch = Stopwatch.StartNew();
            int pendingCountBeforeRemove = plan.SelectedPendingPackages.Count;
            int removedPendingCount = 0;
            foreach (BMSPackage pendingPackage in destinationPackages)
            {
                if (pendingPackage != null && result.PendingPackagesToRemove.Add(pendingPackage))
                {
                    removedPendingCount++;
                }
            }
            pendingMarkStopwatch.Stop();
            groupStopwatch.Stop();
            logInfo?.Invoke(
                "InstallBMSPackagesToEstimatedDir group dst=" + groupEntry.DestinationDirectory +
                " packages=" + destinationPackages.Count +
                " workPackages=" + installWorkPackages.Count +
                " failedPackages=" + failedInstallWorkPackages.Count +
                " installMs=" + installStopwatch.ElapsedMilliseconds +
                " installDbMs=" + installDbStopwatch.ElapsedMilliseconds +
                " pendingMarkMs=" + pendingMarkStopwatch.ElapsedMilliseconds +
                " pendingBefore=" + pendingCountBeforeRemove +
                " pendingMarked=" + removedPendingCount +
                " totalGroupMs=" + groupStopwatch.ElapsedMilliseconds);
        }
        if (deletePendingPackageSourceAfterInstall && plan.CleanupOnlyCandidates.Count > 0)
        {
            foreach (BMSPackage cleanupOnlyPackage in plan.CleanupOnlyCandidates.Distinct())
            {
                if (cleanupOnlyPackage == null)
                {
                    continue;
                }
                (bool Success, CleanupSourceKind SourceKind) cleanupResult = cleanupPendingPackageSource != null
                    ? cleanupPendingPackageSource(cleanupOnlyPackage)
                    : (false, CleanupSourceKind.MissingSource);
                if (cleanupResult.Success)
                {
                    result.CleanupOnlySucceeded++;
                    if (cleanupResult.SourceKind == CleanupSourceKind.MissingSource)
                    {
                        result.CleanupOnlyMissingSource++;
                    }
                    if (!string.IsNullOrWhiteSpace(cleanupOnlyPackage.path))
                    {
                        result.InstallRowsToDelete.Add(cleanupOnlyPackage.path);
                    }
                    result.PendingPackagesToRemove.Add(cleanupOnlyPackage);
                    foreach (BMSFile packageFile in cleanupOnlyPackage.BMSFiles ?? Enumerable.Empty<BMSFile>())
                    {
                        if (packageFile != null)
                        {
                            packageFile.instl_dst = null;
                        }
                    }
                    logInfo?.Invoke("estimated_install_cleanup_only_success package=" + cleanupOnlyPackage.path + " kind=" + cleanupResult.SourceKind.ToString().ToLowerInvariant());
                }
                else
                {
                    result.CleanupOnlyFailed++;
                    logInfo?.Invoke("estimated_install_cleanup_only_failed package=" + cleanupOnlyPackage.path);
                }
            }
        }
        return result;
    }

    public PackageInstallExecutionResult InstallPackages(
        IEnumerable<BMSPackage> bmsPackagesInstall,
        string installationDirectory,
        Func<BMSPackage, string, bool, HashSet<string>, ISet<string>, bool> movePackageFiles,
        Action<IEnumerable<BMSFile>> upsertSongs,
        Action<IEnumerable<BMSFile>> updateMaintenance,
        Action<IEnumerable<BMSFile>> updateZeroNote,
        Action<IEnumerable<BMSFile>> applyScores,
        Action<IEnumerable<BMSFile>> applyState,
        Dictionary<BMSPackage, HashSet<string>> excludedComponentPathsByPackage = null,
        HashSet<string> existingHashes = null,
        bool skipInstalledPackageWhenNoBms = false,
        bool deleteSourceContentsAfterSuccessfulInstall = false)
    {
        PackageInstallExecutionResult result = new PackageInstallExecutionResult();
        List<BMSPackage> packages = (bmsPackagesInstall ?? Enumerable.Empty<BMSPackage>()).Where((BMSPackage package) => package != null).ToList();
        Stopwatch totalStopwatch = Stopwatch.StartNew();
        Stopwatch moveStopwatch = Stopwatch.StartNew();
        foreach (BMSPackage package in packages)
        {
            HashSet<string> excludedComponentPaths = null;
            excludedComponentPathsByPackage?.TryGetValue(package, out excludedComponentPaths);
            if (movePackageFiles != null && movePackageFiles(package, installationDirectory, deleteSourceContentsAfterSuccessfulInstall, existingHashes, excludedComponentPaths))
            {
                result.AddedFiles.AddRange((package.BMSFiles ?? new List<BMSFile>()).Where((BMSFile file) => file != null));
                if (existingHashes != null)
                {
                    foreach (BMSFile bmsFile in package.BMSFiles ?? Enumerable.Empty<BMSFile>())
                    {
                        string lookupKey = PendingChartEntry.GetPrimaryLookupHash(bmsFile);
                        if (!string.IsNullOrWhiteSpace(lookupKey))
                        {
                            existingHashes.Add(lookupKey);
                        }
                    }
                }
                bool shouldSkipInstalledPackageRegistration = skipInstalledPackageWhenNoBms && (package.BMSFiles == null || package.BMSFiles.Count == 0);
                if (!shouldSkipInstalledPackageRegistration)
                {
                    result.InstalledPackagesToRegister.Add(package);
                }
            }
            else if (!string.IsNullOrWhiteSpace(package.path) && (File.Exists(package.path) || Directory.Exists(package.path)))
            {
                result.FailedPackages.Add(package);
            }
        }
        moveStopwatch.Stop();
        result.MoveMs = moveStopwatch.ElapsedMilliseconds;

        Stopwatch songDbStopwatch = Stopwatch.StartNew();
        List<BMSFile> addedBmsFiles = result.AddedFiles.Where((BMSFile file) => PendingChartEntry.IsBmsChartFile(file)).ToList();
        upsertSongs?.Invoke(addedBmsFiles);
        songDbStopwatch.Stop();
        result.SongDbMs = songDbStopwatch.ElapsedMilliseconds;

        Stopwatch maintenanceStopwatch = Stopwatch.StartNew();
        updateMaintenance?.Invoke(addedBmsFiles);
        maintenanceStopwatch.Stop();
        result.MaintenanceMs = maintenanceStopwatch.ElapsedMilliseconds;

        Stopwatch zeroNoteStopwatch = Stopwatch.StartNew();
        updateZeroNote?.Invoke(addedBmsFiles);
        zeroNoteStopwatch.Stop();
        result.ZeroNoteMs = zeroNoteStopwatch.ElapsedMilliseconds;

        Stopwatch scoreStopwatch = Stopwatch.StartNew();
        applyScores?.Invoke(addedBmsFiles);
        scoreStopwatch.Stop();
        result.ScoreMs = scoreStopwatch.ElapsedMilliseconds;

        Stopwatch applyStopwatch = Stopwatch.StartNew();
        applyState?.Invoke(result.AddedFiles);
        applyStopwatch.Stop();
        result.ApplyMs = applyStopwatch.ElapsedMilliseconds;

        totalStopwatch.Stop();
        result.TotalMs = totalStopwatch.ElapsedMilliseconds;
        return result;
    }

    public ForceInstallBatchResult ForceInstallPackages(
        IEnumerable<BMSPackage> packages,
        IEnumerable<BMSPackage> currentPendingPackages,
        Func<BMSPackage, bool> confirmNormalInstallOverride,
        Func<IEnumerable<BMSPackage>, List<BMSPackage>, List<BMSPackage>> installPackages,
        Action<string> logInfo = null)
    {
        ForceInstallBatchResult result = new ForceInstallBatchResult();
        List<BMSPackage> requestedPackages = DeduplicatePackagesByPathOrReference(packages);
        List<BMSPackage> pendingPackages = (currentPendingPackages ?? Enumerable.Empty<BMSPackage>()).Where((BMSPackage pkg) => pkg != null).ToList();
        result.Requested = requestedPackages.Count;
        foreach (BMSPackage requestedPackage in requestedPackages)
        {
            BMSPackage pendingPackage = pendingPackages.FirstOrDefault((BMSPackage pkg) => ReferenceEquals(pkg, requestedPackage) || (!string.IsNullOrWhiteSpace(pkg.path) && !string.IsNullOrWhiteSpace(requestedPackage.path) && pkg.path.Equals(requestedPackage.path, StringComparison.OrdinalIgnoreCase)));
            if (pendingPackage == null)
            {
                result.Skipped++;
                logInfo?.Invoke("force_install_batch skip_not_pending path=" + (requestedPackage.path ?? "(null)"));
                continue;
            }
            List<BMSFile> packageFiles = (pendingPackage.BMSFiles ?? new List<BMSFile>()).Where((BMSFile file) => file != null).ToList();
            if (packageFiles.Any((BMSFile file) => !string.IsNullOrWhiteSpace(file.instl_dst)) && confirmNormalInstallOverride != null && !confirmNormalInstallOverride(pendingPackage))
            {
                result.Skipped++;
                logInfo?.Invoke("force_install_batch skipped_by_confirm path=" + (pendingPackage.path ?? "(null)"));
                continue;
            }
            List<BMSPackage> deferredInstalledPackages = new List<BMSPackage>();
            List<BMSPackage> failedPackages = installPackages?.Invoke(new BMSPackage[1] { pendingPackage }, deferredInstalledPackages) ?? new List<BMSPackage>();
            result.Processed++;
            if (failedPackages.Count == 0)
            {
                result.PendingPackagesToRemove.Add(pendingPackage);
                result.DeferredInstalledPackages.AddRange(deferredInstalledPackages.Where((BMSPackage pkg) => pkg != null));
                result.Succeeded++;
                foreach (BMSFile packageFile in packageFiles)
                {
                    packageFile.instl_dst = null;
                }
                logInfo?.Invoke("force_install_batch success path=" + (pendingPackage.path ?? "(null)"));
            }
            else
            {
                result.Failed++;
                logInfo?.Invoke("force_install_batch failed path=" + (pendingPackage.path ?? "(null)"));
            }
        }
        return result;
    }

    public PendingResourceOverwriteExecutionResult ExecuteInstalledOnlyResourceOverwrite(
        IEnumerable<BMSPackage> packages,
        IEnumerable<BMSPackage> currentPendingPackages,
        bool deletePendingPackageSourceAfterInstall,
        Func<BMSPackage, InstalledOnlyPackageResolutionResult> resolveDestination,
        Func<InstalledOnlyPackageResolutionResult, BMSPackage, string> describeSkipDetail,
        Func<BMSPackage, string, bool> hasResourceOverwriteTargets,
        Func<BMSPackage, string, bool> installPackageToEstimatedDestination,
        Func<BMSPackage, (bool Success, CleanupSourceKind SourceKind)> cleanupPendingPackageSource,
        Func<BMSPackage, bool> isPackageStillPending,
        CancellationToken token = default,
        Action onEachProcessed = null,
        Action<string> logInfo = null)
    {
        PendingResourceOverwriteExecutionResult result = new PendingResourceOverwriteExecutionResult();
        List<BMSPackage> requestedPackages = DeduplicatePackagesByPathOrReference(packages);
        List<BMSPackage> pendingPackages = (currentPendingPackages ?? Enumerable.Empty<BMSPackage>()).Where((BMSPackage pkg) => pkg != null).ToList();
        result.Requested = requestedPackages.Count;
        foreach (BMSPackage requestedPackage in requestedPackages)
        {
            if (token.IsCancellationRequested)
            {
                result.Canceled = true;
                break;
            }
            BMSPackage pendingPackage = pendingPackages.FirstOrDefault((BMSPackage pkg) => ReferenceEquals(pkg, requestedPackage) || (!string.IsNullOrWhiteSpace(pkg.path) && !string.IsNullOrWhiteSpace(requestedPackage.path) && pkg.path.Equals(requestedPackage.path, StringComparison.OrdinalIgnoreCase)));
            if (pendingPackage == null)
            {
                result.SkippedNotPending++;
                result.Processed++;
                logInfo?.Invoke("advanced_pending_resource_overwrite skip_not_pending path=" + requestedPackage.path);
                onEachProcessed?.Invoke();
                continue;
            }
            InstalledOnlyPackageResolutionResult resolution = resolveDestination?.Invoke(pendingPackage) ?? new InstalledOnlyPackageResolutionResult();
            if (!resolution.Success)
            {
                if (resolution.Reason == InstalledDirectoryResolveReason.ChartHasMultipleInstalledDirectories || resolution.Reason == InstalledDirectoryResolveReason.PackageHasSplitInstalledDirectories)
                {
                    result.SkippedMultiDestination++;
                }
                else
                {
                    result.SkippedMissingInstlDst++;
                }
                logInfo?.Invoke(describeSkipDetail?.Invoke(resolution, pendingPackage));
                result.Processed++;
                onEachProcessed?.Invoke();
                continue;
            }
            string destinationDir = resolution.DestinationDirectory;
            logInfo?.Invoke("advanced_pending_resource_overwrite resolve_selected path=" + pendingPackage.path + " dst=" + destinationDir + " charts=" + (pendingPackage.BMSFiles ?? new List<BMSFile>()).Count((BMSFile f) => f != null));
            if (!hasResourceOverwriteTargets(pendingPackage, destinationDir))
            {
                if (deletePendingPackageSourceAfterInstall)
                {
                    (bool Success, CleanupSourceKind SourceKind) cleanupResult = cleanupPendingPackageSource != null
                        ? cleanupPendingPackageSource(pendingPackage)
                        : (false, CleanupSourceKind.MissingSource);
                    if (cleanupResult.Success)
                    {
                        result.SucceededCleanupOnly++;
                        result.PendingPackagesToRemove.Add(pendingPackage);
                        if (!string.IsNullOrWhiteSpace(pendingPackage.path))
                        {
                            result.InstallRowsToDelete.Add(pendingPackage.path);
                        }
                        logInfo?.Invoke("advanced_pending_resource_overwrite cleanup_only_success path=" + pendingPackage.path + " kind=" + cleanupResult.SourceKind.ToString().ToLowerInvariant());
                    }
                    else
                    {
                        result.Failed++;
                        logInfo?.Invoke("advanced_pending_resource_overwrite cleanup_only_failed path=" + pendingPackage.path);
                    }
                }
                else
                {
                    result.SkippedNoComponentTarget++;
                    logInfo?.Invoke("advanced_pending_resource_overwrite skip_no_component_target path=" + pendingPackage.path);
                }
                result.Processed++;
                onEachProcessed?.Invoke();
                continue;
            }
            List<BMSFile> packageFiles = (pendingPackage.BMSFiles ?? new List<BMSFile>()).Where((BMSFile file) => file != null).ToList();
            Dictionary<BMSFile, string> installDestinations = packageFiles.ToDictionary((BMSFile file) => file, (BMSFile file) => file.instl_dst);
            foreach (BMSFile packageFile in packageFiles)
            {
                packageFile.instl_dst = destinationDir;
            }
            bool installSucceeded = false;
            try
            {
                installSucceeded = installPackageToEstimatedDestination != null && installPackageToEstimatedDestination(pendingPackage, destinationDir);
            }
            finally
            {
                if (isPackageStillPending != null && isPackageStillPending(pendingPackage))
                {
                    foreach (KeyValuePair<BMSFile, string> item in installDestinations)
                    {
                        item.Key.instl_dst = item.Value;
                    }
                }
            }
            if (!installSucceeded || (isPackageStillPending != null && isPackageStillPending(pendingPackage)))
            {
                result.Failed++;
                logInfo?.Invoke("advanced_pending_resource_overwrite install_failed path=" + pendingPackage.path + " dst=" + destinationDir);
            }
            else
            {
                result.SucceededInstall++;
                logInfo?.Invoke("advanced_pending_resource_overwrite install_success path=" + pendingPackage.path + " dst=" + destinationDir);
            }
            result.Processed++;
            onEachProcessed?.Invoke();
        }
        return result;
    }

    public PendingZeroNoteRenameResult RenamePendingZeroNoteChartsToInvalidExtensions(
        IEnumerable<BMSFile> targetFiles,
        Func<BMSFile, string, RenameInvalidExtensionOutcome> processRename,
        CancellationToken token = default,
        Action onEachProcessed = null,
        Action<string> logInfo = null)
    {
        PendingZeroNoteRenameResult result = new PendingZeroNoteRenameResult();
        List<BMSFile> files = DeduplicateFilesByPathOrReference(targetFiles);
        result.Total = files.Count;
        foreach (BMSFile file in files)
        {
            if (token.IsCancellationRequested)
            {
                result.Canceled = true;
                break;
            }
            string extension = Path.GetExtension(file.path);
            string targetExtension = null;
            if (!string.IsNullOrWhiteSpace(extension))
            {
                if (extension.StartsWith(".b", StringComparison.OrdinalIgnoreCase))
                {
                    targetExtension = ".bmx";
                }
                else if (extension.StartsWith(".p", StringComparison.OrdinalIgnoreCase))
                {
                    targetExtension = ".pmx";
                }
            }
            if (string.IsNullOrWhiteSpace(targetExtension) || extension.Equals(targetExtension, StringComparison.OrdinalIgnoreCase))
            {
                result.Skipped++;
                result.Processed++;
                logInfo?.Invoke("advanced_pending_zero_note_rename skip_unsupported_ext path=" + file.path + " ext=" + extension);
                onEachProcessed?.Invoke();
                continue;
            }
            bool isZeroNote;
            try
            {
                isZeroNote = BMSFile.IsZeroNoteBMSFile(file.path);
            }
            catch
            {
                result.Skipped++;
                result.Processed++;
                onEachProcessed?.Invoke();
                continue;
            }
            if (!isZeroNote)
            {
                result.Skipped++;
                result.Processed++;
                logInfo?.Invoke("advanced_pending_zero_note_rename skip_not_zero path=" + file.path);
                onEachProcessed?.Invoke();
                continue;
            }
            result.ZeroNote++;
            string requestedPath = Path.Combine(Path.GetDirectoryName(file.path), Path.GetFileNameWithoutExtension(file.path) + targetExtension);
            RenameInvalidExtensionOutcome renameResult = processRename?.Invoke(file, requestedPath) ?? new RenameInvalidExtensionOutcome();
            switch (renameResult.Action)
            {
                case RenameInvalidExtensionAction.Renamed:
                    result.FilesToRemove.Add(file);
                    result.Renamed++;
                    break;
                case RenameInvalidExtensionAction.DeletedAsDuplicate:
                    result.FilesToRemove.Add(file);
                    result.DuplicateDeleted++;
                    break;
                default:
                    result.Failed++;
                    result.Failures.Add(new PendingZeroNoteRenameFailure
                    {
                        File = file,
                        Outcome = renameResult
                    });
                    break;
            }
            result.Processed++;
            onEachProcessed?.Invoke();
        }
        return result;
    }

    public PendingExtensionRenameResult RenamePendingFileExtensions(
        IEnumerable<BMSFile> targetFiles,
        string newExt,
        Func<BMSFile, string, RenameInvalidExtensionOutcome> processRename)
    {
        PendingExtensionRenameResult result = new PendingExtensionRenameResult();
        Stopwatch stopwatch = Stopwatch.StartNew();
        List<BMSFile> files = DeduplicateFilesByPathOrReference(
            (targetFiles ?? Enumerable.Empty<BMSFile>()).Where((BMSFile file) => file != null && File.Exists(file.path)));
        result.Total = files.Count;
        foreach (BMSFile file in files)
        {
            string requestedPath = Path.Combine(Path.GetDirectoryName(file.path), Path.GetFileNameWithoutExtension(file.path) + newExt);
            RenameInvalidExtensionOutcome renameResult = processRename?.Invoke(file, requestedPath) ?? new RenameInvalidExtensionOutcome();
            switch (renameResult.Action)
            {
                case RenameInvalidExtensionAction.Renamed:
                    result.Renamed++;
                    result.FilesToRemove.Add(file);
                    break;
                case RenameInvalidExtensionAction.DeletedAsDuplicate:
                    result.DuplicateDeleted++;
                    result.FilesToRemove.Add(file);
                    break;
                default:
                    result.Skipped++;
                    if (renameResult.FailureException != null)
                    {
                        result.Failed++;
                        result.Failures.Add(new PendingExtensionRenameFailure
                        {
                            File = file,
                            Outcome = renameResult
                        });
                    }
                    break;
            }
        }
        stopwatch.Stop();
        result.TotalMs = stopwatch.ElapsedMilliseconds;
        return result;
    }

    public PendingPackageSourceDeletionResult DeletePendingPackageSources(
        IEnumerable<BMSPackage> packages,
        IEnumerable<BMSPackage> currentPendingPackages,
        bool sendToRecycleBin,
        IFileMutationService fileMutationService,
        FileMutationOptions targetOnlyFileMutationOptions,
        FileMutationOptions recursiveDirectoryTreeFileMutationOptions,
        CancellationToken token = default,
        Action onEachProcessed = null,
        Action<string> logInfo = null)
    {
        PendingPackageSourceDeletionResult result = new PendingPackageSourceDeletionResult();
        List<BMSPackage> requestedPackages = DeduplicatePackagesByPathOrReference(packages);
        List<BMSPackage> pendingPackages = (currentPendingPackages ?? Enumerable.Empty<BMSPackage>()).Where((BMSPackage pkg) => pkg != null).ToList();
        result.Requested = requestedPackages.Count;
        RecycleOption recycleOption = sendToRecycleBin ? RecycleOption.SendToRecycleBin : RecycleOption.DeletePermanently;
        foreach (BMSPackage requestedPackage in requestedPackages)
        {
            if (token.IsCancellationRequested)
            {
                result.Canceled = true;
                break;
            }
            BMSPackage pendingPackage = pendingPackages.FirstOrDefault((BMSPackage pkg) => ReferenceEquals(pkg, requestedPackage) || (!string.IsNullOrWhiteSpace(pkg.path) && !string.IsNullOrWhiteSpace(requestedPackage.path) && pkg.path.Equals(requestedPackage.path, StringComparison.OrdinalIgnoreCase)));
            if (pendingPackage == null)
            {
                result.Skipped++;
                result.Processed++;
                logInfo?.Invoke("advanced_pending_cleanup skipped_not_pending path=" + requestedPackage.path);
                onEachProcessed?.Invoke();
                continue;
            }
            bool isDirectory = Directory.Exists(pendingPackage.path);
            bool isFile = !isDirectory && File.Exists(pendingPackage.path);
            try
            {
                if (isDirectory)
                {
                    if (sendToRecycleBin)
                    {
                        fileMutationService.DeleteDirectoryShell(pendingPackage.path, UIOption.OnlyErrorDialogs, recycleOption, recursiveDirectoryTreeFileMutationOptions);
                    }
                    else
                    {
                        fileMutationService.DeleteDirectoryDirect(pendingPackage.path, recursive: true, recursiveDirectoryTreeFileMutationOptions);
                    }
                    logInfo?.Invoke("advanced_pending_cleanup deleted path=" + pendingPackage.path + " kind=directory");
                }
                else if (isFile)
                {
                    if (sendToRecycleBin)
                    {
                        fileMutationService.DeleteFileShell(pendingPackage.path, UIOption.OnlyErrorDialogs, recycleOption, targetOnlyFileMutationOptions);
                    }
                    else
                    {
                        fileMutationService.DeleteFileDirect(pendingPackage.path, targetOnlyFileMutationOptions);
                    }
                    logInfo?.Invoke("advanced_pending_cleanup deleted path=" + pendingPackage.path + " kind=file");
                }
                else
                {
                    logInfo?.Invoke("advanced_pending_cleanup missing_source_removed path=" + pendingPackage.path);
                }
                result.PackagesToRemove.Add(pendingPackage);
                result.Removed++;
            }
            catch (Exception ex)
            {
                result.Failed++;
                result.Failures.Add(new PendingPackageSourceDeletionFailure
                {
                    Package = pendingPackage,
                    Exception = ex,
                    IsDirectory = isDirectory
                });
            }
            result.Processed++;
            onEachProcessed?.Invoke();
        }
        return result;
    }

    public PendingFileDeletionResult DeletePendingFiles(
        IEnumerable<BMSFile> bmsFiles,
        IEnumerable<BMSPackage> pendingPackages,
        bool sendToRecycleBin,
        bool deleteContainingPackageFoldersWhenNoBms,
        IFileMutationService fileMutationService,
        FileMutationOptions targetOnlyFileMutationOptions,
        FileMutationOptions recursiveDirectoryTreeFileMutationOptions)
    {
        PendingFileDeletionResult result = new PendingFileDeletionResult();
        List<BMSFile> selectedFiles = DeduplicateFilesByPathOrReference(bmsFiles);
        result.Requested = selectedFiles.Count;
        if (selectedFiles.Count == 0)
        {
            return result;
        }

        HashSet<string> selectedPaths = new HashSet<string>(
            selectedFiles.Where((BMSFile file) => !string.IsNullOrWhiteSpace(file.path)).Select((BMSFile file) => file.path),
            StringComparer.OrdinalIgnoreCase);
        HashSet<BMSFile> selectedFileRefs = new HashSet<BMSFile>(selectedFiles);
        HashSet<string> handledByFolderDeletePaths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        HashSet<string> blockedByFailedFolderDeletePaths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        RecycleOption recycleOption = sendToRecycleBin ? RecycleOption.SendToRecycleBin : RecycleOption.DeletePermanently;

        if (deleteContainingPackageFoldersWhenNoBms)
        {
            foreach (BMSPackage pendingPackage in libraryFileOperationsService.GetPendingPackagesFullyCoveredBySelection(pendingPackages, selectedPaths, selectedFileRefs))
            {
                if (!Directory.Exists(pendingPackage.path))
                {
                    continue;
                }
                List<BMSFile> packageFiles = pendingPackage.BMSFiles.Where((BMSFile file) => file != null).ToList();
                try
                {
                    fileMutationService.DeleteDirectoryShell(pendingPackage.path, UIOption.OnlyErrorDialogs, recycleOption, recursiveDirectoryTreeFileMutationOptions);
                    foreach (BMSFile packageFile in packageFiles)
                    {
                        result.FilesToRemove.Add(packageFile);
                        if (!string.IsNullOrWhiteSpace(packageFile.path))
                        {
                            handledByFolderDeletePaths.Add(packageFile.path);
                        }
                    }
                    result.Processed += packageFiles.Count;
                    result.Removed += packageFiles.Count;
                }
                catch (Exception ex)
                {
                    foreach (BMSFile packageFile in packageFiles)
                    {
                        if (!string.IsNullOrWhiteSpace(packageFile.path))
                        {
                            blockedByFailedFolderDeletePaths.Add(packageFile.path);
                        }
                    }
                    result.Processed += packageFiles.Count;
                    result.Failed += packageFiles.Count;
                    result.Failures.Add(new PendingFileDeletionFailure
                    {
                        Path = pendingPackage.path,
                        Exception = ex,
                        IsDirectory = true
                    });
                }
            }
        }

        foreach (BMSFile pendingFile in selectedFiles)
        {
            if (!string.IsNullOrWhiteSpace(pendingFile.path) && (handledByFolderDeletePaths.Contains(pendingFile.path) || blockedByFailedFolderDeletePaths.Contains(pendingFile.path)))
            {
                continue;
            }

            result.Processed++;
            try
            {
                if (File.Exists(pendingFile.path))
                {
                    fileMutationService.DeleteFileShell(pendingFile.path, UIOption.OnlyErrorDialogs, recycleOption, targetOnlyFileMutationOptions);
                    result.FilesToRemove.Add(pendingFile);
                    result.Removed++;
                }
                else
                {
                    result.Skipped++;
                }
            }
            catch (Exception ex2)
            {
                result.Failed++;
                result.Failures.Add(new PendingFileDeletionFailure
                {
                    Path = pendingFile.path,
                    Exception = ex2,
                    IsDirectory = false
                });
            }
        }

        return result;
    }

    private static void ApplyAlreadyInstalledWarning(IEnumerable<BMSFile> files)
    {
        foreach (BMSFile file in files ?? Enumerable.Empty<BMSFile>())
        {
            if (file != null)
            {
                file.warning = Properties.Resources.Warning_AlreadyInstalled;
            }
        }
    }

    private static bool IsMatchedRemovedFile(BMSFile file, HashSet<string> removedPaths, HashSet<BMSFile> removedFiles)
    {
        if (file == null)
        {
            return false;
        }
        if (removedFiles != null && removedFiles.Contains(file))
        {
            return true;
        }
        return !string.IsNullOrWhiteSpace(file.path) && removedPaths.Contains(file.path);
    }

    private static bool IsBmsHashAvailable(string hash)
    {
        return !string.IsNullOrWhiteSpace(hash);
    }

    private void MovePackageComponentsSmart(
        BMSPackage package,
        List<string> installComponentFiles,
        string destinationDirectory,
        ISet<string> excludedComponentPaths,
        bool keepProtectedFilesByRenaming,
        IFileMutationService fileMutationService,
        FileMutationOptions targetOnlyFileMutationOptions,
        Action<string> logInstallPerformance)
    {
        ComponentMoveSummary componentMoveSummary = new ComponentMoveSummary();
        ComponentMovePlanBuildResult movePlanResult = BuildComponentMovePlan(installComponentFiles, destinationDirectory, excludedComponentPaths);
        List<ComponentMovePlanItem> movePlanItems = movePlanResult.PlanItems;
        componentMoveSummary.Total = movePlanItems.Count;
        componentMoveSummary.SkippedByExclusion = movePlanResult.SkippedByExclusion;

        foreach (ComponentMovePlanItem planItem in movePlanItems)
        {
            string srcFilePath = planItem.SourcePath;
            string dstFilePath = planItem.DestinationPath;
            if (!File.Exists(srcFilePath))
            {
                throw new FileNotFoundException(Resources.Error_FileNotFound, srcFilePath);
            }
            if (IsSamePath(srcFilePath, dstFilePath))
            {
                componentMoveSummary.SkippedSame++;
                componentMoveSummary.SkippedSamePath++;
                continue;
            }
            string dstDirPath = Path.GetDirectoryName(dstFilePath);
            if (!string.IsNullOrWhiteSpace(dstDirPath))
            {
                fileMutationService.EnsureDirectory(dstDirPath, targetOnlyFileMutationOptions);
            }

            ComponentMoveDecision moveDecision = DecideComponentMove(srcFilePath, dstFilePath);
            bool keepByRename = keepProtectedFilesByRenaming && File.Exists(dstFilePath) && IsSmartOverwriteProtectedExtension(srcFilePath) && moveDecision != ComponentMoveDecision.SkipSame;
            if (keepByRename)
            {
                FileCollisionResolutionResult resolution = libraryFileOperationsService.ResolveFileCollisionWithSuffix(srcFilePath, dstFilePath, null, "smart_overwrite", logInstallPerformance);
                if (resolution.EncounteredFileCollision)
                {
                    componentMoveSummary.HashChecked++;
                }
                if (resolution.DuplicateMatched)
                {
                    fileMutationService.DeleteFileDirect(srcFilePath, targetOnlyFileMutationOptions);
                    componentMoveSummary.SkippedSame++;
                    componentMoveSummary.DeletedAfterSkip++;
                    componentMoveSummary.HashSameSkip++;
                    continue;
                }
                fileMutationService.MoveFile(srcFilePath, resolution.FinalPath, overwrite: false, targetOnlyFileMutationOptions);
                componentMoveSummary.Moved++;
                componentMoveSummary.RenamedKeep++;
                if (resolution.AnyHashUnavailable)
                {
                    componentMoveSummary.HashUnavailableRenamed++;
                }
                else
                {
                    componentMoveSummary.HashDiffRenamed++;
                }
                if (moveDecision == ComponentMoveDecision.Overwrite)
                {
                    componentMoveSummary.RenamedFromOverwrite++;
                }
                else
                {
                    componentMoveSummary.RenamedFromSkipOlder++;
                }
                continue;
            }

            switch (moveDecision)
            {
                case ComponentMoveDecision.Move:
                    fileMutationService.MoveFile(srcFilePath, dstFilePath, overwrite: true, targetOnlyFileMutationOptions);
                    componentMoveSummary.Moved++;
                    break;
                case ComponentMoveDecision.Overwrite:
                    fileMutationService.MoveFile(srcFilePath, dstFilePath, overwrite: true, targetOnlyFileMutationOptions);
                    componentMoveSummary.Moved++;
                    componentMoveSummary.Overwritten++;
                    break;
                case ComponentMoveDecision.SkipSame:
                    fileMutationService.DeleteFileDirect(srcFilePath, targetOnlyFileMutationOptions);
                    componentMoveSummary.SkippedSame++;
                    componentMoveSummary.DeletedAfterSkip++;
                    break;
                default:
                    fileMutationService.DeleteFileDirect(srcFilePath, targetOnlyFileMutationOptions);
                    componentMoveSummary.SkippedOlder++;
                    componentMoveSummary.DeletedAfterSkip++;
                    break;
            }
        }
        CleanupEmptyComponentDirectories(installComponentFiles, fileMutationService, targetOnlyFileMutationOptions);
        int movedNewCount = Math.Max(0, componentMoveSummary.Moved - componentMoveSummary.Overwritten);
        logInstallPerformance?.Invoke("component_move_summary package=" + package.path + " total=" + componentMoveSummary.Total + " moved=" + componentMoveSummary.Moved + " moved_new=" + movedNewCount + " overwritten=" + componentMoveSummary.Overwritten + " skipped_same=" + componentMoveSummary.SkippedSame + " skipped_same_path=" + componentMoveSummary.SkippedSamePath + " skipped_older=" + componentMoveSummary.SkippedOlder + " skipped_by_exclusion=" + componentMoveSummary.SkippedByExclusion + " deleted_after_skip=" + componentMoveSummary.DeletedAfterSkip + " renamed_keep=" + componentMoveSummary.RenamedKeep + " renamed_from_overwrite=" + componentMoveSummary.RenamedFromOverwrite + " renamed_from_skip_older=" + componentMoveSummary.RenamedFromSkipOlder + " hash_checked=" + componentMoveSummary.HashChecked + " hash_same_skip=" + componentMoveSummary.HashSameSkip + " hash_diff_renamed=" + componentMoveSummary.HashDiffRenamed + " hash_unavailable_renamed=" + componentMoveSummary.HashUnavailableRenamed + " failed=" + componentMoveSummary.Failed);
    }

    private static void CleanupEmptyComponentDirectories(IEnumerable<string> installComponentDirectories, IFileMutationService fileMutationService, FileMutationOptions targetOnlyFileMutationOptions)
    {
        foreach (string installComponentDirectory in installComponentDirectories.Where((string path) => Directory.Exists(path)).OrderByDescending((string path) => path.Length))
        {
            TryDeleteEmptyDirectoryTree(installComponentDirectory, fileMutationService, targetOnlyFileMutationOptions);
        }
    }

    private static void TryDeleteEmptyDirectoryTree(string rootDirectoryPath, IFileMutationService fileMutationService, FileMutationOptions targetOnlyFileMutationOptions)
    {
        try
        {
            if (!Directory.Exists(rootDirectoryPath))
            {
                return;
            }
            foreach (string childDirectoryPath in Directory.EnumerateDirectories(rootDirectoryPath).ToList())
            {
                TryDeleteEmptyDirectoryTree(childDirectoryPath, fileMutationService, targetOnlyFileMutationOptions);
            }
            if (!Directory.EnumerateFileSystemEntries(rootDirectoryPath).Any())
            {
                fileMutationService.DeleteDirectoryDirect(rootDirectoryPath, recursive: false, targetOnlyFileMutationOptions);
            }
        }
        catch
        {
        }
    }

    private static bool HasRemainingDirectoryEntries(string directoryPath)
    {
        try
        {
            return Directory.Exists(directoryPath) && Directory.EnumerateFileSystemEntries(directoryPath).Any();
        }
        catch
        {
            return false;
        }
    }
}
