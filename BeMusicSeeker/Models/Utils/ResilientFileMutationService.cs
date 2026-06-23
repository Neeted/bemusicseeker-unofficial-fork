using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using Microsoft.VisualBasic.FileIO;
using Ribbit.Logging;

namespace BeMusicSeeker.Models.Utils;

/// <summary>
/// BMSLibrary の変更系ファイル操作を ReadOnly 補正と短時間リトライ付きで実行します。
/// </summary>
internal sealed class ResilientFileMutationService : IFileMutationService
{
    private static readonly FileMutationOptions defaultOptions = new();

    /// <inheritdoc />
    public void EnsureDirectory(string directoryPath, FileMutationOptions options = null)
    {
        FileMutationOptions resolvedOptions = ResolveOptions(options);
        ExecuteMutation(
            FileMutationKind.EnsureDirectory,
            directoryPath,
            null,
            resolvedOptions,
            () => LongPathFileSystem.CreateDirectory(directoryPath),
            null,
            () => NormalizeNearestExistingAncestorDirectory(directoryPath));
    }

    /// <inheritdoc />
    public void MoveFile(string sourcePath, string destinationPath, bool overwrite, FileMutationOptions options = null)
    {
        FileMutationOptions resolvedOptions = ResolveOptions(options);
        ExecuteMutation(
            FileMutationKind.MoveFile,
            sourcePath,
            destinationPath,
            resolvedOptions,
            () => LongPathFileSystem.MoveFile(sourcePath, destinationPath, overwrite),
            () => NormalizeMoveFilePaths(sourcePath, destinationPath, overwrite, resolvedOptions),
            () => NormalizeMoveFilePaths(sourcePath, destinationPath, overwrite, resolvedOptions));
    }

    /// <inheritdoc />
    public void MoveDirectory(string sourcePath, string destinationPath, bool overwrite, FileMutationOptions options = null)
    {
        FileMutationOptions resolvedOptions = ResolveOptions(options);
        ExecuteMutation(
            FileMutationKind.MoveDirectory,
            sourcePath,
            destinationPath,
            resolvedOptions,
            () => LongPathFileSystem.MoveDirectory(sourcePath, destinationPath, overwrite),
            () => NormalizeMoveDirectoryPaths(sourcePath, destinationPath, overwrite, resolvedOptions),
            () => NormalizeMoveDirectoryPaths(sourcePath, destinationPath, overwrite, resolvedOptions));
    }

    /// <inheritdoc />
    public void DeleteFileDirect(string filePath, FileMutationOptions options = null)
    {
        FileMutationOptions resolvedOptions = ResolveOptions(options);
        ExecuteMutation(
            FileMutationKind.DeleteFileDirect,
            filePath,
            null,
            resolvedOptions,
            () => LongPathFileSystem.DeleteFile(filePath),
            () => NormalizePrimaryPath(filePath, resolvedOptions.ReadOnlyNormalizationScope),
            () => NormalizePrimaryPath(filePath, resolvedOptions.ReadOnlyNormalizationScope));
    }

    /// <inheritdoc />
    public void DeleteFileShell(string filePath, UIOption uiOption, RecycleOption recycleOption, FileMutationOptions options = null)
    {
        FileMutationOptions resolvedOptions = ResolveOptions(options);
        ExecuteMutation(
            FileMutationKind.DeleteFileShell,
            filePath,
            null,
            resolvedOptions,
            () =>
            {
                if (!LongPathFileSystem.FileExists(filePath))
                {
                    return;
                }
                if (recycleOption == RecycleOption.DeletePermanently && uiOption == UIOption.OnlyErrorDialogs)
                {
                    LongPathFileSystem.DeleteFile(filePath);
                    return;
                }

                FileSystem.DeleteFile(filePath, uiOption, recycleOption);
                if (LongPathFileSystem.FileExists(filePath))
                {
                    throw new IOException("Shell delete completed without removing the file.");
                }
            },
            () => NormalizePrimaryPath(filePath, resolvedOptions.ReadOnlyNormalizationScope),
            () => NormalizePrimaryPath(filePath, resolvedOptions.ReadOnlyNormalizationScope));
    }

    /// <inheritdoc />
    public void DeleteDirectoryDirect(string directoryPath, bool recursive, FileMutationOptions options = null)
    {
        FileMutationOptions resolvedOptions = ResolveOptions(options);
        ExecuteMutation(
            FileMutationKind.DeleteDirectoryDirect,
            directoryPath,
            null,
            resolvedOptions,
            () =>
            {
                if (!LongPathFileSystem.DirectoryExists(directoryPath))
                {
                    return;
                }
                LongPathFileSystem.DeleteDirectory(directoryPath, recursive);
            },
            () => NormalizePrimaryPath(directoryPath, resolvedOptions.ReadOnlyNormalizationScope),
            () => NormalizePrimaryPath(directoryPath, resolvedOptions.ReadOnlyNormalizationScope));
    }

    /// <inheritdoc />
    public void DeleteDirectoryShell(string directoryPath, UIOption uiOption, RecycleOption recycleOption, FileMutationOptions options = null)
    {
        FileMutationOptions resolvedOptions = ResolveOptions(options);
        ExecuteMutation(
            FileMutationKind.DeleteDirectoryShell,
            directoryPath,
            null,
            resolvedOptions,
            () =>
            {
                if (!LongPathFileSystem.DirectoryExists(directoryPath))
                {
                    return;
                }
                if (recycleOption == RecycleOption.DeletePermanently && uiOption == UIOption.OnlyErrorDialogs)
                {
                    LongPathFileSystem.DeleteDirectory(directoryPath, recursive: true);
                    return;
                }

                FileSystem.DeleteDirectory(directoryPath, uiOption, recycleOption);
                if (LongPathFileSystem.DirectoryExists(directoryPath))
                {
                    throw new IOException("Shell delete completed without removing the directory.");
                }
            },
            () => NormalizePrimaryPath(directoryPath, resolvedOptions.ReadOnlyNormalizationScope),
            () => NormalizePrimaryPath(directoryPath, resolvedOptions.ReadOnlyNormalizationScope));
    }

    /// <inheritdoc />
    public void SetTimestamps(string path, bool isDirectory, DateTime? creationTime, DateTime? lastWriteTime, FileMutationOptions options = null)
    {
        FileMutationOptions resolvedOptions = ResolveOptions(options);
        ExecuteMutation(
            FileMutationKind.SetTimestamps,
            path,
            null,
            resolvedOptions,
            () =>
            {
                if (creationTime.HasValue)
                {
                    LongPathFileSystem.SetCreationTime(path, isDirectory, creationTime.Value);
                }
                if (lastWriteTime.HasValue)
                {
                    LongPathFileSystem.SetLastWriteTime(path, isDirectory, lastWriteTime.Value);
                }
            },
            () => NormalizePrimaryPath(path, resolvedOptions.ReadOnlyNormalizationScope),
            () => NormalizePrimaryPath(path, resolvedOptions.ReadOnlyNormalizationScope));
    }

    private static FileMutationOptions ResolveOptions(FileMutationOptions options)
    {
        return options ?? defaultOptions;
    }

    private static void ExecuteMutation(
        FileMutationKind mutationKind,
        string primaryPath,
        string secondaryPath,
        FileMutationOptions options,
        Action mutationAction,
        Func<int> normalizeBeforeFirstAttempt,
        Func<int> normalizeAfterAccessDenied)
    {
        if (primaryPath == null)
        {
            throw new ArgumentNullException(nameof(primaryPath));
        }
        if (mutationAction == null)
        {
            throw new ArgumentNullException(nameof(mutationAction));
        }

        int normalizedReadOnlyCount = 0;
        int attemptCount = 0;
        int sharingRetryCount = 0;
        int accessDeniedRetryCount = 0;
        bool wasRetried = false;
        bool accessDeniedNormalizationAttempted = false;

        if (normalizeBeforeFirstAttempt != null)
        {
            normalizedReadOnlyCount += normalizeBeforeFirstAttempt();
        }

        while (true)
        {
            attemptCount++;
            try
            {
                mutationAction();
                if (normalizedReadOnlyCount > 0 || wasRetried)
                {
                    LogSuccess(mutationKind, primaryPath, secondaryPath, attemptCount, normalizedReadOnlyCount, wasRetried);
                }
                return;
            }
            catch (Exception exception)
            {
                int win32ErrorCode = ExtractWin32ErrorCodeFromHResult(exception);
                if (options.RetryOnAccessDenied && IsAccessDenied(exception))
                {
                    if (!accessDeniedNormalizationAttempted && normalizeAfterAccessDenied != null)
                    {
                        accessDeniedNormalizationAttempted = true;
                        normalizedReadOnlyCount += normalizeAfterAccessDenied();
                    }

                    if (accessDeniedRetryCount < options.MaxRetryCountOnAccessDenied)
                    {
                        accessDeniedRetryCount++;
                        wasRetried = true;
                        LogRetry(mutationKind, primaryPath, secondaryPath, attemptCount, normalizedReadOnlyCount, win32ErrorCode, exception, "access_denied");
                        SleepBeforeRetry(options);
                        continue;
                    }
                }

                if (options.RetryOnSharingViolation && IsSharingOrLockViolation(exception))
                {
                    if (sharingRetryCount < options.MaxRetryCountOnSharingViolation)
                    {
                        sharingRetryCount++;
                        wasRetried = true;
                        LogRetry(mutationKind, primaryPath, secondaryPath, attemptCount, normalizedReadOnlyCount, win32ErrorCode, exception, "sharing_or_lock");
                        SleepBeforeRetry(options);
                        continue;
                    }
                }

                var fileMutationException = new FileMutationException(
                    mutationKind,
                    primaryPath,
                    secondaryPath,
                    attemptCount,
                    normalizedReadOnlyCount,
                    win32ErrorCode,
                    wasRetried,
                    exception);
                LogFailure(fileMutationException);
                throw fileMutationException;
            }
        }
    }

    private static void SleepBeforeRetry(FileMutationOptions options)
    {
        if (options.RetryDelayMs > 0)
        {
            Thread.Sleep(options.RetryDelayMs);
        }
    }

    private static int NormalizeMoveFilePaths(string sourcePath, string destinationPath, bool overwrite, FileMutationOptions options)
    {
        int normalizedReadOnlyCount = NormalizePath(sourcePath, options.ReadOnlyNormalizationScope);
        if (overwrite && LongPathFileSystem.FileExists(destinationPath))
        {
            normalizedReadOnlyCount += NormalizePath(destinationPath, ReadOnlyNormalizationScope.TargetOnly);
        }
        return normalizedReadOnlyCount;
    }

    private static int NormalizeMoveDirectoryPaths(string sourcePath, string destinationPath, bool overwrite, FileMutationOptions options)
    {
        int normalizedReadOnlyCount = NormalizePath(sourcePath, options.ReadOnlyNormalizationScope);
        if (overwrite && LongPathFileSystem.DirectoryExists(destinationPath))
        {
            normalizedReadOnlyCount += NormalizePath(destinationPath, ReadOnlyNormalizationScope.RecursiveDirectoryTree);
        }
        return normalizedReadOnlyCount;
    }

    private static int NormalizePrimaryPath(string primaryPath, ReadOnlyNormalizationScope normalizationScope)
    {
        return NormalizePath(primaryPath, normalizationScope);
    }

    private static int NormalizeNearestExistingAncestorDirectory(string directoryPath)
    {
        string existingDirectoryPath = FindNearestExistingAncestorDirectory(directoryPath);
        if (string.IsNullOrWhiteSpace(existingDirectoryPath))
        {
            return 0;
        }
        return NormalizePath(existingDirectoryPath, ReadOnlyNormalizationScope.TargetOnly);
    }

    private static string FindNearestExistingAncestorDirectory(string directoryPath)
    {
        if (string.IsNullOrWhiteSpace(directoryPath))
        {
            return null;
        }

        string candidateDirectoryPath = directoryPath.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        while (!string.IsNullOrWhiteSpace(candidateDirectoryPath))
        {
            if (LongPathFileSystem.DirectoryExists(candidateDirectoryPath))
            {
                return candidateDirectoryPath;
            }

            string parentDirectoryPath = Path.GetDirectoryName(candidateDirectoryPath);
            if (string.Equals(parentDirectoryPath, candidateDirectoryPath, StringComparison.OrdinalIgnoreCase))
            {
                break;
            }
            candidateDirectoryPath = parentDirectoryPath;
        }

        return null;
    }

    private static int NormalizePath(string path, ReadOnlyNormalizationScope normalizationScope)
    {
        if (string.IsNullOrWhiteSpace(path) || normalizationScope == ReadOnlyNormalizationScope.None)
        {
            return 0;
        }

        return normalizationScope switch
        {
            ReadOnlyNormalizationScope.TargetOnly => TryNormalizeFileSystemReadOnlyAttribute(path) ? 1 : 0,
            ReadOnlyNormalizationScope.RecursiveDirectoryTree => NormalizeReadOnlyAttributesRecursively(path),
            _ => 0,
        };
    }

    private static bool TryNormalizeFileSystemReadOnlyAttribute(string fileSystemPath)
    {
        try
        {
            if (!LongPathFileSystem.EntryExists(fileSystemPath))
            {
                return false;
            }

            FileAttributes currentAttributes = LongPathFileSystem.GetAttributes(fileSystemPath);
            if ((currentAttributes & FileAttributes.ReadOnly) == 0)
            {
                return false;
            }

            LongPathFileSystem.SetAttributes(fileSystemPath, currentAttributes & ~FileAttributes.ReadOnly);
            return true;
        }
        catch
        {
            return false;
        }
    }

    private static int NormalizeReadOnlyAttributesRecursively(string rootDirectoryPath)
    {
        if (string.IsNullOrWhiteSpace(rootDirectoryPath) || !LongPathFileSystem.DirectoryExists(rootDirectoryPath))
        {
            return 0;
        }

        int normalizedReadOnlyCount = 0;
        var pendingDirectoryPaths = new Queue<string>();
        pendingDirectoryPaths.Enqueue(rootDirectoryPath);

        while (pendingDirectoryPaths.Count > 0)
        {
            string currentDirectoryPath = pendingDirectoryPaths.Dequeue();
            if (TryNormalizeFileSystemReadOnlyAttribute(currentDirectoryPath))
            {
                normalizedReadOnlyCount++;
            }

            string[] childDirectoryPaths = [];
            try
            {
                childDirectoryPaths = LongPathFileSystem.GetDirectories(currentDirectoryPath);
            }
            catch
            {
            }

            foreach (string childDirectoryPath in childDirectoryPaths)
            {
                pendingDirectoryPaths.Enqueue(childDirectoryPath);
            }

            string[] childFilePaths = [];
            try
            {
                childFilePaths = LongPathFileSystem.GetFiles(currentDirectoryPath);
            }
            catch
            {
            }

            foreach (string childFilePath in childFilePaths)
            {
                if (TryNormalizeFileSystemReadOnlyAttribute(childFilePath))
                {
                    normalizedReadOnlyCount++;
                }
            }
        }

        return normalizedReadOnlyCount;
    }

    private static Exception GetInnermostException(Exception exception)
    {
        Exception currentException = exception;
        while (currentException?.InnerException != null)
        {
            currentException = currentException.InnerException;
        }
        return currentException ?? exception;
    }

    private static int ExtractWin32ErrorCodeFromHResult(Exception exception)
    {
        Exception innermostException = GetInnermostException(exception);
        int hResult = innermostException.HResult;
        if ((hResult & -65536) == -2147024896)
        {
            return hResult & 0xFFFF;
        }
        return -1;
    }

    private static bool IsSharingOrLockViolation(Exception exception)
    {
        int win32ErrorCode = ExtractWin32ErrorCodeFromHResult(exception);
        return win32ErrorCode == 32 || win32ErrorCode == 33;
    }

    private static bool IsAccessDenied(Exception exception)
    {
        if (exception is UnauthorizedAccessException)
        {
            return true;
        }

        if (ExtractWin32ErrorCodeFromHResult(exception) == 5)
        {
            return true;
        }

        string errorMessage = GetInnermostException(exception).Message ?? string.Empty;
        return errorMessage.IndexOf("access is denied", StringComparison.OrdinalIgnoreCase) >= 0
            || errorMessage.IndexOf("アクセスが拒否されました", StringComparison.OrdinalIgnoreCase) >= 0;
    }

    private static void LogRetry(
        FileMutationKind mutationKind,
        string primaryPath,
        string secondaryPath,
        int attemptCount,
        int normalizedReadOnlyCount,
        int win32ErrorCode,
        Exception exception,
        string retryReason)
    {
        NLogWrapper.FileLogger?.Info(string.Format(
            "file_mutation retry kind={0} path={1} secondaryPath={2} reason={3} attempt={4} normalizedReadOnly={5} win32={6} errorType={7} error={8}",
            mutationKind,
            primaryPath,
            string.IsNullOrWhiteSpace(secondaryPath) ? "(none)" : secondaryPath,
            retryReason,
            attemptCount,
            normalizedReadOnlyCount,
            win32ErrorCode,
            exception.GetType().FullName,
            exception.Message));
    }

    private static void LogSuccess(
        FileMutationKind mutationKind,
        string primaryPath,
        string secondaryPath,
        int attemptCount,
        int normalizedReadOnlyCount,
        bool wasRetried)
    {
        NLogWrapper.FileLogger?.Info(string.Format(
            "file_mutation success kind={0} path={1} secondaryPath={2} attempts={3} normalizedReadOnly={4} retried={5}",
            mutationKind,
            primaryPath,
            string.IsNullOrWhiteSpace(secondaryPath) ? "(none)" : secondaryPath,
            attemptCount,
            normalizedReadOnlyCount,
            wasRetried));
    }

    private static void LogFailure(FileMutationException fileMutationException)
    {
        NLogWrapper.FileLogger?.Warn(string.Format(
            "file_mutation failed kind={0} path={1} secondaryPath={2} attempts={3} normalizedReadOnly={4} win32={5} retried={6} errorType={7} error={8}",
            fileMutationException.Kind,
            fileMutationException.PrimaryPath,
            string.IsNullOrWhiteSpace(fileMutationException.SecondaryPath) ? "(none)" : fileMutationException.SecondaryPath,
            fileMutationException.AttemptCount,
            fileMutationException.NormalizedReadOnlyCount,
            fileMutationException.Win32ErrorCode,
            fileMutationException.WasRetried,
            fileMutationException.RootCause.GetType().FullName,
            fileMutationException.RootCause.Message));
    }
}
