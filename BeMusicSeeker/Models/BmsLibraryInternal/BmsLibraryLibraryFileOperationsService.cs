using System;
using System.Collections.Generic;
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
        IEnumerable<BMSFile> libraryFiles,
        IEnumerable<BMSPackage> pendingPackages,
        IEnumerable<BMSPackage> installedPackages,
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
        foreach (BMSFile item in (pendingPackages ?? Enumerable.Empty<BMSPackage>()).SelectMany((BMSPackage pkg) => pkg.BMSFiles).Concat((libraryFiles ?? Enumerable.Empty<BMSFile>()).Where((BMSFile file) => !string.IsNullOrWhiteSpace(file.instl_dst))))
        {
            if (!string.IsNullOrWhiteSpace(item.instl_dst) && (item.instl_dst + Path.DirectorySeparatorChar).StartsWith(srcDir + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
            {
                item.instl_dst = item.instl_dst.ReplaceFromStart(srcDir, dstDir, isIgnoreCase: true);
            }
        }
        foreach (BMSPackage item2 in installedPackages ?? Enumerable.Empty<BMSPackage>())
        {
            if (!string.IsNullOrWhiteSpace(item2.path) && (item2.path + Path.DirectorySeparatorChar).StartsWith(srcDir + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
            {
                item2.path = item2.path.ReplaceFromStart(srcDir, dstDir, isIgnoreCase: true);
            }
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
