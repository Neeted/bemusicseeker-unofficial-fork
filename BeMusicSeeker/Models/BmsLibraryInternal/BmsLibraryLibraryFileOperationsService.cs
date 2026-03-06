using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Security;
using System.Security.Cryptography;
using System.Text;
using Microsoft.VisualBasic.FileIO;
using BeMusicSeeker.Models.Utils;
using Ribbit.Util.Extensions;

namespace BeMusicSeeker.Models.BmsLibraryInternal;

internal enum RenameInvalidExtensionAction
{
    Renamed,
    DeletedAsDuplicate,
    Skipped
}

internal sealed class RenameInvalidExtensionOutcome
{
    public RenameInvalidExtensionAction Action { get; set; }

    public string FinalPath { get; set; }

    public Exception FailureException { get; set; }

    public bool FailedDuringDelete { get; set; }
}

internal sealed class BmsLibraryLibraryFileOperationsService
{
    public void MoveFolderAndUpdateReferences(
        string srcDir,
        string dstDir,
        BMSDirectoryFileNameHash folderAllFileList,
        IFileMutationService fileMutationService,
        FileMutationOptions recursiveDirectoryTreeFileMutationOptions)
    {
        fileMutationService.MoveDirectory(srcDir, dstDir, overwrite: false, recursiveDirectoryTreeFileMutationOptions);
        foreach (string item in folderAllFileList.Keys.Where((string f) => (f + Path.DirectorySeparatorChar).StartsWith(srcDir + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)))
        {
            string newKey = item.ReplaceFromStart(srcDir, dstDir, isIgnoreCase: true);
            folderAllFileList.ReplaceDir(item, newKey);
        }
    }

    public void MoveFileOnDisk(BMSFile bmsFile, string dstPath, IFileMutationService fileMutationService, FileMutationOptions targetOnlyFileMutationOptions)
    {
        fileMutationService.MoveFile(bmsFile.path, dstPath, overwrite: false, targetOnlyFileMutationOptions);
    }

    public LibraryRemovalResult DeleteLibraryFiles(
        IEnumerable<BMSFile> bmsFiles,
        IEnumerable<BMSFile> libraryFiles,
        IEnumerable<BMSPackage> pendingPackages,
        BMSDirectoryFileNameHash folderAllFileList,
        bool sendToRecycleBin,
        Func<string, bool> confirmDeleteWholeFolder,
        IFileMutationService fileMutationService,
        FileMutationOptions targetOnlyFileMutationOptions,
        FileMutationOptions recursiveDirectoryTreeFileMutationOptions)
    {
        LibraryRemovalResult result = new LibraryRemovalResult();
        List<BMSFile> currentLibraryFiles = (libraryFiles ?? Enumerable.Empty<BMSFile>()).Where((BMSFile file) => file != null).ToList();
        RecycleOption recycleOption = sendToRecycleBin ? RecycleOption.SendToRecycleBin : RecycleOption.DeletePermanently;
        foreach (IGrouping<string, BMSFile> folderGroup in from groupedFiles in (bmsFiles ?? Enumerable.Empty<BMSFile>()).Where((BMSFile file) => file != null).GroupBy((BMSFile bmsInfo) => DirectoryExt.GetDirectoryNameSimple(bmsInfo.path), StringComparer.OrdinalIgnoreCase)
                                                          orderby groupedFiles.Key.Length descending
                                                          select groupedFiles)
        {
            bool shouldDeleteWholeFolder = currentLibraryFiles.Where((BMSFile bmsInfo) => bmsInfo.path.StartsWith(folderGroup.Key + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)).Except(result.RemovedFiles).Count() == folderGroup.Count()
                && (confirmDeleteWholeFolder?.Invoke(folderGroup.Key) ?? false);
            if (shouldDeleteWholeFolder)
            {
                if (!Directory.Exists(folderGroup.Key))
                {
                    continue;
                }
                try
                {
                    fileMutationService.DeleteDirectoryShell(folderGroup.Key, UIOption.OnlyErrorDialogs, recycleOption, recursiveDirectoryTreeFileMutationOptions);
                    foreach (string indexedDirectoryPath in folderAllFileList.Keys.Where((string directoryPath) => (directoryPath + Path.DirectorySeparatorChar).StartsWith(folderGroup.Key + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)))
                    {
                        folderAllFileList.RemoveDir(indexedDirectoryPath);
                    }
                    foreach (BMSFile installLinkedBmsFile in (pendingPackages ?? Enumerable.Empty<BMSPackage>()).SelectMany((BMSPackage pkg) => pkg.BMSFiles).Concat(currentLibraryFiles.Where((BMSFile bmsInfo) => !string.IsNullOrWhiteSpace(bmsInfo.instl_dst))))
                    {
                        if (!string.IsNullOrWhiteSpace(installLinkedBmsFile.instl_dst) && (installLinkedBmsFile.instl_dst + Path.DirectorySeparatorChar).StartsWith(folderGroup.Key + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
                        {
                            installLinkedBmsFile.instl_dst = null;
                        }
                    }
                    result.RemovedFiles.AddRange(folderGroup);
                }
                catch (Exception ex)
                {
                    result.Failures.Add(new LibraryDeleteFailure
                    {
                        Path = folderGroup.Key,
                        Exception = ex,
                        IsDirectory = true
                    });
                }
                continue;
            }
            foreach (BMSFile selectedBmsFile in folderGroup)
            {
                try
                {
                    if (File.Exists(selectedBmsFile.path))
                    {
                        fileMutationService.DeleteFileShell(selectedBmsFile.path, UIOption.OnlyErrorDialogs, recycleOption, targetOnlyFileMutationOptions);
                        result.RemovedFiles.Add(selectedBmsFile);
                    }
                }
                catch (Exception ex2)
                {
                    result.Failures.Add(new LibraryDeleteFailure
                    {
                        Path = selectedBmsFile.path,
                        Exception = ex2,
                        IsDirectory = false
                    });
                }
            }
        }
        return result;
    }

    public LibraryMutationDelta BuildFolderMoveDelta(
        string srcDir,
        string dstDir,
        IEnumerable<BMSFile> libraryFiles,
        IEnumerable<BMSPackage> pendingPackages,
        IEnumerable<BMSPackage> installedPackages,
        bool unregister)
    {
        LibraryMutationDelta delta = new LibraryMutationDelta();
        List<BMSFile> targetFiles = (libraryFiles ?? Enumerable.Empty<BMSFile>())
            .Where((BMSFile file) => file != null && !string.IsNullOrWhiteSpace(file.path) && file.path.StartsWith(srcDir + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
            .ToList();
        if (unregister)
        {
            delta.FilesToUnregister.AddRange(targetFiles);
            delta.InvalidateBMSHashIndex = targetFiles.Count > 0;
            delta.InvalidateInstalledDirectoryIndex = targetFiles.Count > 0;
            delta.InvalidateParentFolderCache = targetFiles.Count > 0;
            return delta;
        }
        foreach (BMSFile installLinkedFile in (pendingPackages ?? Enumerable.Empty<BMSPackage>())
            .Where((BMSPackage pkg) => pkg != null)
            .SelectMany((BMSPackage pkg) => pkg.BMSFiles)
            .Concat((libraryFiles ?? Enumerable.Empty<BMSFile>()).Where((BMSFile file) => !string.IsNullOrWhiteSpace(file.instl_dst))))
        {
            if (!string.IsNullOrWhiteSpace(installLinkedFile?.instl_dst)
                && (installLinkedFile.instl_dst + Path.DirectorySeparatorChar).StartsWith(srcDir + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
            {
                delta.UpdatedInstallDestinations.Add(new LibraryInstallDestinationChange
                {
                    File = installLinkedFile,
                    NewInstallDestination = installLinkedFile.instl_dst.ReplaceFromStart(srcDir, dstDir, isIgnoreCase: true)
                });
            }
        }
        foreach (BMSPackage installedPackage in installedPackages ?? Enumerable.Empty<BMSPackage>())
        {
            if (!string.IsNullOrWhiteSpace(installedPackage?.path)
                && (installedPackage.path + Path.DirectorySeparatorChar).StartsWith(srcDir + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
            {
                delta.UpdatedInstalledPackagePaths.Add(new LibraryInstalledPackagePathChange
                {
                    Package = installedPackage,
                    NewPath = installedPackage.path.ReplaceFromStart(srcDir, dstDir, isIgnoreCase: true)
                });
            }
        }
        foreach (IGrouping<string, BMSFile> group in targetFiles.GroupBy((BMSFile target) => Path.GetDirectoryName(target.path)))
        {
            string newFolderPath = group.Key.ReplaceFromStart(srcDir, dstDir, isIgnoreCase: true);
            delta.FolderPathChanges.Add(new LibraryFolderPathChange
            {
                NewFolderPath = newFolderPath,
                OldFolderPath = group.Key
            });
            foreach (BMSFile file in group)
            {
                delta.FilePathChanges.Add(new LibraryFilePathChange
                {
                    File = file,
                    NewPath = file.path.ReplaceFromStart(srcDir, dstDir, isIgnoreCase: true)
                });
            }
        }
        delta.RaiseBmsFilesChanged = delta.FilePathChanges.Count > 0;
        delta.RaiseInstalledPackagesChanged = delta.UpdatedInstalledPackagePaths.Count > 0;
        delta.InvalidateInstalledDirectoryIndex = delta.FilePathChanges.Count > 0 || delta.UpdatedInstallDestinations.Count > 0 || delta.UpdatedInstalledPackagePaths.Count > 0;
        delta.InvalidateParentFolderCache = delta.FilePathChanges.Count > 0;
        delta.ClearDuplicatedCache = delta.FilePathChanges.Count > 0 || delta.UpdatedInstallDestinations.Count > 0 || delta.UpdatedInstalledPackagePaths.Count > 0;
        return delta;
    }

    public List<FolderAutoRenamePlan> BuildAutoRenamePlans(
        IEnumerable<BMSFile> selectedFiles,
        IEnumerable<BMSFile> libraryFiles,
        IEnumerable<string> rootFolders,
        bool renameRootFolder,
        Func<IEnumerable<BMSFile>, string, string, string> createFolderPath)
    {
        List<string> sourceFolders = (from d in (selectedFiles ?? Enumerable.Empty<BMSFile>()).Where((BMSFile f) => f != null).Select((BMSFile f) => DirectoryExt.GetDirectoryNameSimple(f.path)).Distinct(StringComparer.OrdinalIgnoreCase)
                                      orderby d.Length
                                      select d).ToList();
        List<string> effectiveRootFolders = (rootFolders ?? Enumerable.Empty<string>()).Where((string folder) => !string.IsNullOrWhiteSpace(folder)).ToList();
        List<string> targetFolders = new List<string>();
        foreach (string folder in sourceFolders.Where((string folder) => renameRootFolder || !effectiveRootFolders.Contains(folder, StringComparer.OrdinalIgnoreCase)))
        {
            if (!targetFolders.Any((string existingFolder) => folder.StartsWith(existingFolder + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)))
            {
                targetFolders.Add(folder);
            }
        }
        List<FolderAutoRenamePlan> plans = new List<FolderAutoRenamePlan>();
        foreach (string folder in targetFolders)
        {
            FolderAutoRenamePlan plan = new FolderAutoRenamePlan
            {
                SourceDirectory = folder
            };
            try
            {
                if (!Directory.Exists(folder) || Path.GetPathRoot(folder).Equals(folder, StringComparison.OrdinalIgnoreCase))
                {
                    plans.Add(plan);
                    continue;
                }
                List<BMSFile> directChildren = (from f in libraryFiles ?? Enumerable.Empty<BMSFile>()
                                                where f != null
                                                    && f.path.StartsWith(folder, StringComparison.OrdinalIgnoreCase)
                                                    && DirectoryExt.GetDirectoryNameSimple(f.path).Equals(folder, StringComparison.OrdinalIgnoreCase)
                                                select f).ToList();
                string longestFileName = (from f in FastDirectoryEnumerator.GetFileNames(folder)
                                          orderby f.Length descending
                                          select f).FirstOrDefault() ?? string.Empty;
                string requestedPath = createFolderPath?.Invoke(directChildren, DirectoryExt.GetDirectoryNameSimple(folder), longestFileName);
                if (!string.IsNullOrWhiteSpace(requestedPath) && !folder.Equals(requestedPath, StringComparison.OrdinalIgnoreCase))
                {
                    int suffix = 1;
                    string candidate = requestedPath;
                    while (File.Exists(candidate) || Directory.Exists(candidate))
                    {
                        suffix++;
                        candidate = requestedPath + " (" + suffix + ")";
                    }
                    plan.DestinationDirectory = candidate;
                }
            }
            catch (Exception ex)
            {
                plan.FailureException = ex;
            }
            plans.Add(plan);
        }
        return plans;
    }

    public List<FolderAutoRenamePlan> BuildRootFolderMovePlans(IEnumerable<BMSFile> selectedFiles, string destinationRootDirectory)
    {
        List<string> sourceFolders = (from d in (selectedFiles ?? Enumerable.Empty<BMSFile>()).Where((BMSFile f) => f != null).Select((BMSFile f) => DirectoryExt.GetDirectoryNameSimple(f.path)).Distinct(StringComparer.OrdinalIgnoreCase)
                                      orderby d.Length
                                      select d).ToList();
        List<string> targetFolders = new List<string>();
        foreach (string folder in sourceFolders)
        {
            if (!targetFolders.Any((string existingFolder) => folder.StartsWith(existingFolder + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)))
            {
                targetFolders.Add(folder);
            }
        }
        return targetFolders
            .Where((string folder) => !Path.GetPathRoot(folder).Equals(folder, StringComparison.OrdinalIgnoreCase) && Directory.Exists(folder))
            .Select((string folder) => new FolderAutoRenamePlan
            {
                SourceDirectory = folder,
                DestinationDirectory = Path.Combine(destinationRootDirectory, Path.GetFileName(folder))
            })
            .ToList();
    }

    public LibraryMergeResult PrepareMergeDirectory(
        string srcDir,
        string dstDir,
        IEnumerable<BMSFile> libraryFiles,
        IEnumerable<BMSPackage> pendingPackages,
        IEnumerable<BMSPackage> installedPackages,
        Func<IEnumerable<BMSFile>, HashSet<string>> createHashSnapshotExcluding)
    {
        LibraryMergeResult result = new LibraryMergeResult();
        if (!Directory.Exists(srcDir) || !Directory.Exists(dstDir) || srcDir.Equals(dstDir, StringComparison.OrdinalIgnoreCase))
        {
            return result;
        }
        result.SourceFiles.AddRange((libraryFiles ?? Enumerable.Empty<BMSFile>())
            .Where((BMSFile file) => file != null && file.path.StartsWith(srcDir + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)));
        result.Repackage = new BMSPackage(result.SourceFiles)
        {
            path = srcDir,
            delete_parent = false
        };
        result.ExistingHashes = createHashSnapshotExcluding?.Invoke(result.SourceFiles) ?? new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (BMSFile installLinkedFile in (pendingPackages ?? Enumerable.Empty<BMSPackage>())
            .Where((BMSPackage pkg) => pkg != null)
            .SelectMany((BMSPackage pkg) => pkg.BMSFiles)
            .Concat((libraryFiles ?? Enumerable.Empty<BMSFile>()).Where((BMSFile file) => !string.IsNullOrWhiteSpace(file.instl_dst))))
        {
            if (!string.IsNullOrWhiteSpace(installLinkedFile?.instl_dst)
                && (installLinkedFile.instl_dst + Path.DirectorySeparatorChar).StartsWith(srcDir + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
            {
                result.ReferenceMutationDelta.UpdatedInstallDestinations.Add(new LibraryInstallDestinationChange
                {
                    File = installLinkedFile,
                    NewInstallDestination = installLinkedFile.instl_dst.ReplaceFromStart(srcDir, dstDir, isIgnoreCase: true)
                });
            }
        }
        foreach (BMSPackage installedPackage in installedPackages ?? Enumerable.Empty<BMSPackage>())
        {
            if (!string.IsNullOrWhiteSpace(installedPackage?.path)
                && (installedPackage.path + Path.DirectorySeparatorChar).StartsWith(srcDir + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
            {
                result.ReferenceMutationDelta.UpdatedInstalledPackagePaths.Add(new LibraryInstalledPackagePathChange
                {
                    Package = installedPackage,
                    NewPath = installedPackage.path.ReplaceFromStart(srcDir, dstDir, isIgnoreCase: true)
                });
            }
        }
        result.ReferenceMutationDelta.RaiseInstalledPackagesChanged = result.ReferenceMutationDelta.UpdatedInstalledPackagePaths.Count > 0;
        result.ReferenceMutationDelta.InvalidateInstalledDirectoryIndex = result.ReferenceMutationDelta.UpdatedInstallDestinations.Count > 0 || result.ReferenceMutationDelta.UpdatedInstalledPackagePaths.Count > 0;
        result.ReferenceMutationDelta.ClearDuplicatedCache = result.ReferenceMutationDelta.UpdatedInstallDestinations.Count > 0 || result.ReferenceMutationDelta.UpdatedInstalledPackagePaths.Count > 0;
        result.Success = result.SourceFiles.Count > 0;
        return result;
    }

    public LibraryFixInstallationResult FixInstallationDirectory(
        IEnumerable<BMSFile> bmsFiles,
        HashSet<string> existingHashes,
        Func<BMSPackage, string, bool> movePackageFiles,
        Func<BMSFile, bool> confirmDuplicateRemoval)
    {
        LibraryFixInstallationResult result = new LibraryFixInstallationResult();
        Stopwatch stopwatch = Stopwatch.StartNew();
        List<BMSFile> files = (bmsFiles ?? Enumerable.Empty<BMSFile>()).Where((BMSFile file) => file != null && !string.IsNullOrWhiteSpace(file.instl_dst)).ToList();
        result.RequestedCount = files.Count;
        foreach (BMSFile file in files)
        {
            BMSPackage installPackage = new BMSPackage(file)
            {
                delete_parent = false
            };
            string oldPath = file.path;
            if (!(movePackageFiles?.Invoke(installPackage, file.instl_dst) ?? false))
            {
                continue;
            }
            if (installPackage.BMSFiles.Count == 0)
            {
                result.DuplicateSkippedCount++;
                if (confirmDuplicateRemoval != null && confirmDuplicateRemoval(file))
                {
                    result.FilesToRemove.Add(file);
                }
                continue;
            }
            file.instl_dst = null;
            result.MutationDelta.FilePathChanges.Add(new LibraryFilePathChange
            {
                File = file,
                NewPath = file.path,
                OldPath = oldPath
            });
            result.MutationDelta.RaiseBmsFilesChanged = true;
            result.MutationDelta.InvalidateInstalledDirectoryIndex = true;
            result.MutationDelta.InvalidateParentFolderCache = true;
            result.MutationDelta.ClearDuplicatedCache = true;
            result.MaintenanceTargets.Add(file);
            result.MovedCount++;
        }
        stopwatch.Stop();
        result.TotalMs = stopwatch.ElapsedMilliseconds;
        result.MutationDelta.TotalMs = result.TotalMs;
        return result;
    }

    public LibraryMutationDelta BuildFileMoveDelta(BMSFile bmsFile, string dstPath, bool unregister)
    {
        LibraryMutationDelta delta = new LibraryMutationDelta();
        if (bmsFile == null)
        {
            return delta;
        }
        if (unregister)
        {
            delta.FilesToUnregister.Add(bmsFile);
            delta.InvalidateBMSHashIndex = true;
            delta.InvalidateInstalledDirectoryIndex = true;
            delta.InvalidateParentFolderCache = true;
            return delta;
        }
        delta.FilePathChanges.Add(new LibraryFilePathChange
        {
            File = bmsFile,
            NewPath = dstPath
        });
        delta.RaiseBmsFilesChanged = true;
        delta.InvalidateInstalledDirectoryIndex = true;
        delta.InvalidateParentFolderCache = true;
        return delta;
    }

    public LibraryMutationDelta RenameLibraryFileExtensions(
        IEnumerable<BMSFile> bmsFiles,
        string newExt,
        bool unregister,
        Func<BMSFile, string, RenameInvalidExtensionOutcome> processRename)
    {
        LibraryMutationDelta delta = new LibraryMutationDelta();
        Stopwatch stopwatch = Stopwatch.StartNew();
        foreach (BMSFile file in (bmsFiles ?? Enumerable.Empty<BMSFile>()).Where((BMSFile file) => file != null && File.Exists(file.path)))
        {
            string requestedPath = Path.Combine(Path.GetDirectoryName(file.path), Path.GetFileNameWithoutExtension(file.path) + newExt);
            RenameInvalidExtensionOutcome renameResult = processRename?.Invoke(file, requestedPath) ?? new RenameInvalidExtensionOutcome();
            switch (renameResult.Action)
            {
                case RenameInvalidExtensionAction.Renamed:
                    delta.RenamedCount++;
                    if (unregister)
                    {
                        delta.FilesToUnregister.Add(file);
                        delta.InvalidateBMSHashIndex = true;
                    }
                    else
                    {
                        delta.FilePathChanges.Add(new LibraryFilePathChange
                        {
                            File = file,
                            NewPath = renameResult.FinalPath
                        });
                        delta.RaiseBmsFilesChanged = true;
                    }
                    break;
                case RenameInvalidExtensionAction.DeletedAsDuplicate:
                    delta.DuplicateDeletedCount++;
                    delta.FilesToUnregister.Add(file);
                    delta.InvalidateBMSHashIndex = true;
                    break;
                default:
                    delta.SkippedCount++;
                    if (renameResult.FailureException != null)
                    {
                        delta.Failures.Add(new LibraryDeleteFailure
                        {
                            Path = file.path,
                            Exception = renameResult.FailureException,
                            IsDirectory = false
                        });
                    }
                    break;
            }
        }
        delta.InvalidateInstalledDirectoryIndex = delta.FilePathChanges.Count > 0 || delta.FilesToUnregister.Count > 0;
        delta.InvalidateParentFolderCache = delta.FilePathChanges.Count > 0 || delta.FilesToUnregister.Count > 0;
        stopwatch.Stop();
        delta.TotalMs = stopwatch.ElapsedMilliseconds;
        return delta;
    }

    public RenameInvalidExtensionOutcome ProcessInvalidExtensionRename(BMSFile sourceFile, string requestedPath, IFileMutationService fileMutationService, FileMutationOptions targetOnlyFileMutationOptions, Action<string> logInfo = null, Action<Exception, string> logWarn = null)
    {
        RenameInvalidExtensionOutcome outcome = new RenameInvalidExtensionOutcome
        {
            Action = RenameInvalidExtensionAction.Skipped,
            FinalPath = requestedPath
        };
        if (sourceFile == null || string.IsNullOrWhiteSpace(sourceFile.path) || string.IsNullOrWhiteSpace(requestedPath) || !File.Exists(sourceFile.path))
        {
            return outcome;
        }
        string finalPath = requestedPath;
        if (Directory.Exists(requestedPath))
        {
            logInfo?.Invoke("invalid_ext_rename collision_detected source=" + sourceFile.path + " requested=" + requestedPath + " existsType=directory");
            finalPath = GetNonConflictingPathWithSuffix(requestedPath);
            logInfo?.Invoke("invalid_ext_rename renamed_with_suffix source=" + sourceFile.path + " requested=" + requestedPath + " resolved=" + finalPath);
        }
        else if (File.Exists(requestedPath))
        {
            logInfo?.Invoke("invalid_ext_rename collision_detected source=" + sourceFile.path + " requested=" + requestedPath + " existsType=file");
            string sourceHash = TryGetSourceHashForInvalidExtensionRename(sourceFile);
            string destinationHash = TryComputeFileMd5ForPath(requestedPath, "invalid_ext_rename", logInfo);
            if (!string.IsNullOrWhiteSpace(sourceHash) && !string.IsNullOrWhiteSpace(destinationHash) && sourceHash.Equals(destinationHash, StringComparison.OrdinalIgnoreCase))
            {
                try
                {
                    fileMutationService.DeleteFileDirect(sourceFile.path, targetOnlyFileMutationOptions);
                    logInfo?.Invoke("invalid_ext_rename duplicate_deleted source=" + sourceFile.path + " existing=" + requestedPath + " hash=" + sourceHash);
                    outcome.Action = RenameInvalidExtensionAction.DeletedAsDuplicate;
                    return outcome;
                }
                catch (Exception ex)
                {
                    outcome.FailureException = ex;
                    outcome.FailedDuringDelete = true;
                    logWarn?.Invoke(ex, "invalid_ext_rename delete_failed source=" + sourceFile.path + " existing=" + requestedPath);
                    return outcome;
                }
            }
            if (string.IsNullOrWhiteSpace(sourceHash) || string.IsNullOrWhiteSpace(destinationHash))
            {
                logInfo?.Invoke("invalid_ext_rename hash_compare_unavailable source=" + sourceFile.path + " requested=" + requestedPath + " reason=" + (string.IsNullOrWhiteSpace(sourceHash) ? "source_hash_unavailable" : "dest_hash_unavailable"));
            }
            finalPath = GetNonConflictingPathWithSuffix(requestedPath);
            logInfo?.Invoke("invalid_ext_rename renamed_with_suffix source=" + sourceFile.path + " requested=" + requestedPath + " resolved=" + finalPath);
        }
        try
        {
            fileMutationService.MoveFile(sourceFile.path, finalPath, overwrite: false, targetOnlyFileMutationOptions);
            outcome.Action = RenameInvalidExtensionAction.Renamed;
            outcome.FinalPath = finalPath;
            return outcome;
        }
        catch (Exception ex2)
        {
            outcome.FailureException = ex2;
            outcome.FinalPath = finalPath;
            outcome.FailedDuringDelete = false;
            logWarn?.Invoke(ex2, "invalid_ext_rename move_failed source=" + sourceFile.path + " target=" + finalPath);
            return outcome;
        }
    }

    public string GetNonConflictingPathWithSuffix(string requestedPath)
    {
        if (string.IsNullOrWhiteSpace(requestedPath))
        {
            return requestedPath;
        }
        string directoryName = Path.GetDirectoryName(requestedPath);
        string fileNameWithoutExtension = Path.GetFileNameWithoutExtension(requestedPath);
        string extension = Path.GetExtension(requestedPath);
        int suffix = 1;
        string candidate = requestedPath;
        while (File.Exists(candidate) || Directory.Exists(candidate))
        {
            string renamedFileName = fileNameWithoutExtension + "(" + suffix + ")" + extension;
            candidate = string.IsNullOrWhiteSpace(directoryName) ? renamedFileName : Path.Combine(directoryName, renamedFileName);
            suffix++;
        }
        return candidate;
    }

    public string TryComputeFileMd5ForPath(string filePath, string logCategory = null, Action<string> logInfo = null)
    {
        if (string.IsNullOrWhiteSpace(filePath) || !File.Exists(filePath))
        {
            return null;
        }
        try
        {
            using MD5 md5 = MD5.Create();
            byte[] hashBytes;
            using (FileStream inputStream = new FileStream(filePath, FileMode.Open, FileAccess.Read, FileShare.Read))
            {
                hashBytes = md5.ComputeHash(inputStream);
            }
            StringBuilder stringBuilder = new StringBuilder();
            foreach (byte b in hashBytes)
            {
                stringBuilder.Append(b.ToString("x2"));
            }
            return stringBuilder.ToString();
        }
        catch (Exception ex) when (ex is DirectoryNotFoundException || ex is FileNotFoundException || ex is IOException || ex is PathTooLongException || ex is SecurityException || ex is UnauthorizedAccessException)
        {
            if (!string.IsNullOrWhiteSpace(logCategory))
            {
                logInfo?.Invoke(logCategory + " hash_unavailable path=" + filePath + " error=" + ex.Message);
            }
            return null;
        }
    }

    public List<BMSPackage> GetPendingPackagesFullyCoveredBySelection(IEnumerable<BMSPackage> pendingPackages, HashSet<string> selectedPaths, HashSet<BMSFile> selectedFileRefs)
    {
        return (pendingPackages ?? Enumerable.Empty<BMSPackage>())
            .Where((BMSPackage pkg) => pkg != null && pkg.BMSFiles.Count > 0 && pkg.BMSFiles.All((BMSFile file) => IsMatchedRemovedFile(file, selectedPaths, selectedFileRefs)))
            .ToList();
    }

    public bool IsMatchedRemovedFile(BMSFile file, HashSet<string> removedPaths, HashSet<BMSFile> removedFiles)
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

    private string TryGetSourceHashForInvalidExtensionRename(BMSFile sourceFile)
    {
        if (sourceFile == null || string.IsNullOrWhiteSpace(sourceFile.hash))
        {
            return TryComputeFileMd5ForPath(sourceFile?.path);
        }
        return sourceFile.hash;
    }
}
