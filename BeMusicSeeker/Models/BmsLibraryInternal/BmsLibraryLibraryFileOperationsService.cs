using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security;
using System.Security.Cryptography;
using System.Text;
using BeMusicSeeker.Models.Utils;

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
