using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using BeMusicSeeker.Models.Utils;

namespace BeMusicSeeker.Models.BmsLibraryInternal;

/// <summary>
/// Filesystem/DB mutation の preflight 結果を immutable に保持します。
/// </summary>
internal sealed class FileDbMutationPlan
{
    public FileDbMutationPlan(
        Guid operationId,
        IEnumerable<FileDbMutationPathPlan> paths,
        IEnumerable<string> sourceCleanupPaths,
        IEnumerable<FileDbMutationCleanupPathPlan> sourceCleanupDirectoryPaths,
        bool recursiveSourceCleanup)
    {
        OperationId = operationId == Guid.Empty ? Guid.NewGuid() : operationId;
        Paths = FreezePaths(paths);
        SourceCleanupPaths = FreezeStrings(sourceCleanupPaths);
        SourceCleanupDirectoryPaths = FreezeCleanupPaths(sourceCleanupDirectoryPaths);
        RecursiveSourceCleanup = recursiveSourceCleanup;
    }

    public Guid OperationId { get; }

    public IReadOnlyList<FileDbMutationPathPlan> Paths { get; }

    public IReadOnlyList<string> SourceCleanupPaths { get; }

    public IReadOnlyList<FileDbMutationCleanupPathPlan> SourceCleanupDirectoryPaths { get; }

    public bool RecursiveSourceCleanup { get; }

    private static IReadOnlyList<FileDbMutationPathPlan> FreezePaths(IEnumerable<FileDbMutationPathPlan> values)
    {
        return new List<FileDbMutationPathPlan>((values ?? []).Where(value => value != null)).AsReadOnly();
    }

    private static IReadOnlyList<string> FreezeStrings(IEnumerable<string> values)
    {
        return Array.AsReadOnly((values ?? [])
            .Where(value => !string.IsNullOrWhiteSpace(value))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray());
    }

    private static IReadOnlyList<FileDbMutationCleanupPathPlan> FreezeCleanupPaths(IEnumerable<FileDbMutationCleanupPathPlan> values)
    {
        return new List<FileDbMutationCleanupPathPlan>((values ?? [])
            .Where(value => value != null)
            .GroupBy(value => value.Path, StringComparer.OrdinalIgnoreCase)
            .Select(group => group.First())).AsReadOnly();
    }
}

/// <summary>
/// finalize 時に削除する source directory と削除方式を保持します。
/// </summary>
internal sealed class FileDbMutationCleanupPathPlan
{
    public FileDbMutationCleanupPathPlan(string path, bool recursive)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            throw new ArgumentException("A cleanup path is required.", nameof(path));
        }
        Path = path;
        Recursive = recursive;
    }

    public string Path { get; }

    public bool Recursive { get; }
}

/// <summary>
/// A single source/destination pair in a preflight plan.
/// </summary>
internal sealed class FileDbMutationPathPlan
{
    public FileDbMutationPathPlan(
        string sourcePath,
        string destinationPath,
        string stagingPath,
        string backupPath,
        bool isDirectory)
    {
        SourcePath = RequirePath(sourcePath, nameof(sourcePath));
        DestinationPath = RequirePath(destinationPath, nameof(destinationPath));
        StagingPath = RequirePath(stagingPath, nameof(stagingPath));
        BackupPath = string.IsNullOrWhiteSpace(backupPath) ? string.Empty : backupPath;
        EnsureDestinationSibling(DestinationPath, StagingPath, nameof(stagingPath));
        if (!string.IsNullOrWhiteSpace(BackupPath))
        {
            EnsureDestinationSibling(DestinationPath, BackupPath, nameof(backupPath));
        }
        IsDirectory = isDirectory;
    }

    public string SourcePath { get; }

    public string DestinationPath { get; }

    public string StagingPath { get; }

    public string BackupPath { get; }

    public bool IsDirectory { get; }

    private static string RequirePath(string path, string parameterName)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            throw new ArgumentException("A mutation path is required.", parameterName);
        }
        return path;
    }

    private static void EnsureDestinationSibling(string destinationPath, string candidatePath, string parameterName)
    {
        string destinationParent = Path.GetDirectoryName(LongPathFileSystem.NormalizePathForStorage(destinationPath)) ?? string.Empty;
        string candidateParent = Path.GetDirectoryName(LongPathFileSystem.NormalizePathForStorage(candidatePath)) ?? string.Empty;
        if (!string.Equals(destinationParent, candidateParent, StringComparison.OrdinalIgnoreCase))
        {
            throw new ArgumentException("Staging and backup paths must be siblings of the destination path.", parameterName);
        }
    }
}

/// <summary>
/// Filesystem/DB boundary の terminal state を表します。
/// </summary>
internal enum FileDbMutationTerminalState
{
    Failed,
    ManualRecoveryRequired,
    Completed,
    CompletedWithCleanupFailure
}

/// <summary>
/// Filesystem/DB mutation の durable point と recovery path を immutable に返します。
/// </summary>
internal sealed class FileDbMutationReceipt
{
    public FileDbMutationReceipt(
        Guid operationId,
        FileDbMutationTerminalState terminalState,
        bool durableCommit,
        int compensationAttemptCount,
        int cleanupAttemptCount,
        IEnumerable<string> sourcePaths,
        IEnumerable<string> destinationPaths,
        IEnumerable<string> stagingPaths,
        IEnumerable<string> backupPaths,
        IEnumerable<string> recoveryPaths,
        Exception failure = null)
    {
        OperationId = operationId;
        TerminalState = terminalState;
        DurableCommit = durableCommit;
        CompensationAttemptCount = compensationAttemptCount;
        CleanupAttemptCount = cleanupAttemptCount;
        SourcePaths = Freeze(sourcePaths);
        DestinationPaths = Freeze(destinationPaths);
        StagingPaths = Freeze(stagingPaths);
        BackupPaths = Freeze(backupPaths);
        RecoveryPaths = Freeze(recoveryPaths);
        Failure = failure;
    }

    public Guid OperationId { get; }

    public FileDbMutationTerminalState TerminalState { get; }

    public bool DurableCommit { get; }

    public int CompensationAttemptCount { get; }

    public int CleanupAttemptCount { get; }

    public IReadOnlyList<string> SourcePaths { get; }

    public IReadOnlyList<string> DestinationPaths { get; }

    public IReadOnlyList<string> StagingPaths { get; }

    public IReadOnlyList<string> BackupPaths { get; }

    public IReadOnlyList<string> RecoveryPaths { get; }

    public Exception Failure { get; }

    internal FileDbMutationReceipt WithFailure(Exception additionalFailure)
    {
        if (additionalFailure == null)
        {
            return this;
        }
        Exception combinedFailure = Failure == null
            ? additionalFailure
            : new AggregateException(Failure, additionalFailure);
        return new FileDbMutationReceipt(
            OperationId,
            TerminalState,
            DurableCommit,
            CompensationAttemptCount,
            CleanupAttemptCount,
            SourcePaths,
            DestinationPaths,
            StagingPaths,
            BackupPaths,
            RecoveryPaths,
            combinedFailure);
    }

    private static IReadOnlyList<string> Freeze(IEnumerable<string> values)
    {
        return Array.AsReadOnly((values ?? [])
            .Where(value => !string.IsNullOrWhiteSpace(value))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray());
    }
}

/// <summary>
/// Immutable terminal facts for a batch of independent file/DB mutations.
/// A durable package is retained in the result even when a later package
/// requires manual recovery; callers must not collapse the batch to a bool.
/// </summary>
internal sealed class FileDbMutationBatchReceipt
{
    internal FileDbMutationBatchReceipt(IEnumerable<FileDbMutationReceipt> receipts)
    {
        Receipts = Array.AsReadOnly([.. (receipts ?? []).Where(receipt => receipt != null)]);
        HasDurableCommit = Receipts.Any(receipt => receipt.DurableCommit);
        ManualRecoveryRequired = Receipts.Any(receipt =>
            receipt.TerminalState == FileDbMutationTerminalState.ManualRecoveryRequired);
        CompletedWithCleanupFailure = Receipts.Any(receipt =>
            receipt.TerminalState == FileDbMutationTerminalState.CompletedWithCleanupFailure);
        RecoveryPaths = Array.AsReadOnly(Receipts
            .SelectMany(receipt => receipt.RecoveryPaths ?? [])
            .Where(path => !string.IsNullOrWhiteSpace(path))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray());
    }

    internal IReadOnlyList<FileDbMutationReceipt> Receipts { get; }

    internal bool HasDurableCommit { get; }

    internal bool ManualRecoveryRequired { get; }

    internal bool CompletedWithCleanupFailure { get; }

    internal IReadOnlyList<string> RecoveryPaths { get; }
}

/// <summary>
/// DB owner が durable commit と post-commit failure を区別して返す結果です。
/// </summary>
internal sealed class FileDbMutationCommitResult
{
    private FileDbMutationCommitResult(bool durableCommit, Exception failure, Action postCommit)
    {
        DurableCommit = durableCommit;
        Failure = failure;
        PostCommit = postCommit;
    }

    internal bool DurableCommit { get; }

    internal Exception Failure { get; }

    internal Action PostCommit { get; }

    internal static FileDbMutationCommitResult Durable(Action postCommit = null, Exception postCommitFailure = null)
    {
        return new FileDbMutationCommitResult(true, postCommitFailure, postCommit);
    }

    internal static FileDbMutationCommitResult Failed(Exception failure)
    {
        return new FileDbMutationCommitResult(false, failure, null);
    }
}

/// <summary>
/// Destination-local staging、promotion、one-shot compensation、finalize を実行します。
/// </summary>
internal sealed class FileDbMutationExecutor
{
    private readonly FileDbMutationPlan plan;
    private readonly IFileMutationService fileMutationService;
    private readonly FileMutationOptions targetOnlyOptions;
    private readonly FileMutationOptions recursiveDirectoryOptions;
    private readonly List<FileDbMutationPathPlan> stagedPaths = [];
    private readonly List<FileDbMutationPathPlan> promotedPaths = [];
    private readonly List<FileDbMutationPathPlan> backedUpPaths = [];
    private int compensationAttemptCount;
    private int cleanupAttemptCount;
    private bool compensationAttempted;

    internal FileDbMutationExecutor(
        FileDbMutationPlan plan,
        IFileMutationService fileMutationService,
        FileMutationOptions targetOnlyOptions,
        FileMutationOptions recursiveDirectoryOptions)
    {
        this.plan = plan ?? throw new ArgumentNullException(nameof(plan));
        this.fileMutationService = fileMutationService ?? throw new ArgumentNullException(nameof(fileMutationService));
        this.targetOnlyOptions = targetOnlyOptions;
        this.recursiveDirectoryOptions = recursiveDirectoryOptions;
    }

    internal FileDbMutationReceipt Execute(Func<FileDbMutationCommitResult> applyDurableCommit)
    {
        if (applyDurableCommit == null)
        {
            throw new ArgumentNullException(nameof(applyDurableCommit));
        }

        try
        {
            Stage();
            Promote();
        }
        catch (Exception exception)
        {
            return HandlePrecommitFailure(exception);
        }

        FileDbMutationCommitResult commitResult;
        try
        {
            commitResult = applyDurableCommit() ?? FileDbMutationCommitResult.Failed(
                new InvalidOperationException("The DB mutation owner returned no commit result."));
        }
        catch (Exception exception)
        {
            commitResult = FileDbMutationCommitResult.Failed(exception);
        }

        if (!commitResult.DurableCommit)
        {
            return HandlePrecommitFailure(commitResult.Failure ?? new InvalidOperationException("The durable DB receipt was not produced."));
        }

        FileDbMutationReceipt receipt = FinalizePostCommit(commitResult.Failure);
        if (commitResult.PostCommit != null)
        {
            try
            {
                commitResult.PostCommit();
            }
            catch (Exception exception)
            {
                receipt = receipt.WithFailure(exception);
            }
        }
        return receipt;
    }

    private void Stage()
    {
        foreach (FileDbMutationPathPlan path in plan.Paths)
        {
            stagedPaths.Add(path);
            EnsureParentDirectory(path.StagingPath);
            if (path.IsDirectory)
            {
                fileMutationService.CopyDirectory(path.SourcePath, path.StagingPath, overwrite: false, recursiveDirectoryOptions);
            }
            else
            {
                fileMutationService.CopyFile(path.SourcePath, path.StagingPath, overwrite: false, targetOnlyOptions);
            }
        }
    }

    private void Promote()
    {
        foreach (FileDbMutationPathPlan path in plan.Paths)
        {
            EnsureParentDirectory(path.DestinationPath);
            if (LongPathFileSystem.EntryExists(path.DestinationPath))
            {
                if (string.IsNullOrWhiteSpace(path.BackupPath))
                {
                    throw new IOException("A destination collision has no immutable backup path.");
                }
                EnsureParentDirectory(path.BackupPath);
                MoveExistingDestinationToBackup(path);
                backedUpPaths.Add(path);
            }

            if (path.IsDirectory)
            {
                fileMutationService.MoveDirectory(path.StagingPath, path.DestinationPath, overwrite: false, recursiveDirectoryOptions);
            }
            else
            {
                fileMutationService.MoveFile(path.StagingPath, path.DestinationPath, overwrite: false, targetOnlyOptions);
            }
            promotedPaths.Add(path);
        }
    }

    private void MoveExistingDestinationToBackup(FileDbMutationPathPlan path)
    {
        if (LongPathFileSystem.DirectoryExists(path.DestinationPath))
        {
            fileMutationService.MoveDirectory(path.DestinationPath, path.BackupPath, overwrite: false, recursiveDirectoryOptions);
        }
        else
        {
            fileMutationService.MoveFile(path.DestinationPath, path.BackupPath, overwrite: false, targetOnlyOptions);
        }
    }

    private FileDbMutationReceipt HandlePrecommitFailure(Exception failure)
    {
        // Every failure before the durable DB receipt gets one and only one
        // compensation owner, including a partial stage copy that never reached
        // promotion.  The owner also treats residual stage/backup cleanup as
        // part of that single attempt so a cleanup failure cannot be hidden.
        FileDbMutationReceipt compensationReceipt = CompensateOnce(failure);
        if (compensationReceipt.TerminalState == FileDbMutationTerminalState.ManualRecoveryRequired)
        {
            return compensationReceipt;
        }

        List<Exception> cleanupFailures = [];
        BestEffortDeleteStaging(cleanupFailures);
        if (cleanupFailures.Count == 0)
        {
            BestEffortDeleteBackups(cleanupFailures);
        }
        if (cleanupFailures.Count > 0)
        {
            return CreateReceipt(
                FileDbMutationTerminalState.ManualRecoveryRequired,
                durableCommit: false,
                new IOException(
                    "Filesystem compensation cleanup failed; manual recovery is required.",
                    new AggregateException(cleanupFailures)));
        }
        return CreateReceipt(FileDbMutationTerminalState.Failed, durableCommit: false, failure);
    }

    private FileDbMutationReceipt CompensateOnce(Exception originalFailure)
    {
        if (compensationAttempted)
        {
            return CreateReceipt(
                FileDbMutationTerminalState.ManualRecoveryRequired,
                durableCommit: false,
                new InvalidOperationException("Compensation was requested more than once.", originalFailure));
        }

        compensationAttempted = true;
        compensationAttemptCount++;
        Exception compensationFailure = null;

        foreach (FileDbMutationPathPlan path in promotedPaths.AsEnumerable().Reverse())
        {
            try
            {
                DeleteDestination(path);
            }
            catch (Exception exception)
            {
                compensationFailure ??= exception;
                break;
            }
        }

        if (compensationFailure == null)
        {
            foreach (FileDbMutationPathPlan path in backedUpPaths.AsEnumerable().Reverse())
            {
                try
                {
                    RestoreBackup(path);
                }
                catch (Exception exception)
                {
                    compensationFailure ??= exception;
                    break;
                }
            }
        }

        if (compensationFailure != null)
        {
            return CreateReceipt(
                FileDbMutationTerminalState.ManualRecoveryRequired,
                durableCommit: false,
                new IOException("Filesystem compensation failed; manual recovery is required.", new AggregateException(originalFailure, compensationFailure)));
        }

        return CreateReceipt(FileDbMutationTerminalState.Failed, durableCommit: false, originalFailure);
    }

    private void DeleteDestination(FileDbMutationPathPlan path)
    {
        if (path.IsDirectory)
        {
            fileMutationService.DeleteDirectoryDirect(path.DestinationPath, recursive: true, recursiveDirectoryOptions);
        }
        else
        {
            fileMutationService.DeleteFileDirect(path.DestinationPath, targetOnlyOptions);
        }
    }

    private void RestoreBackup(FileDbMutationPathPlan path)
    {
        if (LongPathFileSystem.DirectoryExists(path.BackupPath))
        {
            fileMutationService.MoveDirectory(path.BackupPath, path.DestinationPath, overwrite: false, recursiveDirectoryOptions);
        }
        else
        {
            fileMutationService.MoveFile(path.BackupPath, path.DestinationPath, overwrite: false, targetOnlyOptions);
        }
    }

    private FileDbMutationReceipt FinalizePostCommit(Exception postCommitFailure)
    {
        List<Exception> cleanupFailures = [];
        List<string> cleanupFailurePaths = [];
        foreach (string sourcePath in plan.SourceCleanupPaths)
        {
            TryCleanupPath(
                sourcePath,
                isDirectory: false,
                plan.RecursiveSourceCleanup,
                cleanupFailures,
                cleanupFailurePaths);
        }
        foreach (FileDbMutationCleanupPathPlan sourceDirectoryPath in plan.SourceCleanupDirectoryPaths)
        {
            TryCleanupPath(
                sourceDirectoryPath.Path,
                isDirectory: true,
                sourceDirectoryPath.Recursive,
                cleanupFailures,
                cleanupFailurePaths);
        }
        BestEffortDeleteStaging(cleanupFailures, cleanupFailurePaths);
        BestEffortDeleteBackups(cleanupFailures, cleanupFailurePaths);

        Exception failure = postCommitFailure;
        if (cleanupFailures.Count > 0)
        {
            failure = failure == null
                ? new IOException("Filesystem cleanup failed after the durable DB commit.", new AggregateException(cleanupFailures))
                : new AggregateException(failure, new AggregateException(cleanupFailures));
        }

        return CreateReceipt(
            cleanupFailures.Count > 0
                ? FileDbMutationTerminalState.CompletedWithCleanupFailure
                : FileDbMutationTerminalState.Completed,
            durableCommit: true,
            failure,
            cleanupFailurePaths);
    }

    private void TryCleanupPath(
        string path,
        bool isDirectory,
        bool recursive,
        ICollection<Exception> failures,
        ICollection<string> failurePaths)
    {
        cleanupAttemptCount++;
        try
        {
            if (isDirectory)
            {
                if (LongPathFileSystem.DirectoryExists(path))
                {
                    // A non-recursive source-directory cleanup is best effort.  A
                    // retained excluded/unknown child is an intentional leftover,
                    // not a cleanup failure; only an attempted deletion failure is
                    // reported as CompletedWithCleanupFailure.
                    if (!recursive && HasRemainingEntries(path))
                    {
                        return;
                    }
                    fileMutationService.DeleteDirectoryDirect(path, recursive, recursive ? recursiveDirectoryOptions : targetOnlyOptions);
                }
            }
            else if (LongPathFileSystem.FileExists(path))
            {
                fileMutationService.DeleteFileDirect(path, targetOnlyOptions);
            }
        }
        catch (Exception exception)
        {
            failures.Add(exception);
            failurePaths?.Add(path);
        }
    }

    private static bool HasRemainingEntries(string directoryPath)
    {
        try
        {
            return LongPathFileSystem.EnumerateFileSystemEntries(
                directoryPath,
                "*",
                SearchOption.TopDirectoryOnly).Any();
        }
        catch (Exception exception) when (exception is IOException
            || exception is UnauthorizedAccessException
            || exception is ArgumentException
            || exception is NotSupportedException)
        {
            // Let the mutation service attempt the delete so that an inaccessible
            // directory remains an observable cleanup failure.
            return false;
        }
    }

    private void EnsureParentDirectory(string path)
    {
        string parentDirectoryPath = Path.GetDirectoryName(path);
        if (!string.IsNullOrWhiteSpace(parentDirectoryPath))
        {
            fileMutationService.EnsureDirectory(parentDirectoryPath, targetOnlyOptions);
        }
    }

    private void BestEffortDeleteStaging(
        ICollection<Exception> failures = null,
        ICollection<string> failurePaths = null)
    {
        foreach (FileDbMutationPathPlan path in stagedPaths.AsEnumerable().Reverse())
        {
            cleanupAttemptCount++;
            try
            {
                if (path.IsDirectory)
                {
                    if (LongPathFileSystem.DirectoryExists(path.StagingPath))
                    {
                        fileMutationService.DeleteDirectoryDirect(path.StagingPath, recursive: true, recursiveDirectoryOptions);
                    }
                }
                else if (LongPathFileSystem.FileExists(path.StagingPath))
                {
                    fileMutationService.DeleteFileDirect(path.StagingPath, targetOnlyOptions);
                }
            }
            catch (Exception exception)
            {
                failures?.Add(exception);
                failurePaths?.Add(path.StagingPath);
            }
        }
    }

    private void BestEffortDeleteBackups(
        ICollection<Exception> failures = null,
        ICollection<string> failurePaths = null)
    {
        foreach (FileDbMutationPathPlan path in backedUpPaths.AsEnumerable().Reverse())
        {
            cleanupAttemptCount++;
            try
            {
                if (LongPathFileSystem.DirectoryExists(path.BackupPath))
                {
                    fileMutationService.DeleteDirectoryDirect(path.BackupPath, recursive: true, recursiveDirectoryOptions);
                }
                else if (LongPathFileSystem.FileExists(path.BackupPath))
                {
                    fileMutationService.DeleteFileDirect(path.BackupPath, targetOnlyOptions);
                }
            }
            catch (Exception exception)
            {
                failures?.Add(exception);
                failurePaths?.Add(path.BackupPath);
            }
        }
    }

    private FileDbMutationReceipt CreateReceipt(
        FileDbMutationTerminalState terminalState,
        bool durableCommit,
        Exception failure,
        IEnumerable<string> additionalRecoveryPaths = null)
    {
        List<string> recoveryPaths = [];
        if (terminalState == FileDbMutationTerminalState.ManualRecoveryRequired
            || terminalState == FileDbMutationTerminalState.CompletedWithCleanupFailure)
        {
            recoveryPaths.AddRange(plan.SourceCleanupPaths);
            recoveryPaths.AddRange(plan.SourceCleanupDirectoryPaths.Select(path => path.Path));
            recoveryPaths.AddRange(stagedPaths.Select(path => path.StagingPath));
            recoveryPaths.AddRange(backedUpPaths.Select(path => path.BackupPath));
            recoveryPaths.AddRange(promotedPaths.Select(path => path.DestinationPath));
        }
        recoveryPaths.AddRange(additionalRecoveryPaths ?? []);
        return new FileDbMutationReceipt(
            plan.OperationId,
            terminalState,
            durableCommit,
            compensationAttemptCount,
            cleanupAttemptCount,
            plan.Paths.Select(path => path.SourcePath)
                .Concat(plan.SourceCleanupPaths)
                .Concat(plan.SourceCleanupDirectoryPaths.Select(path => path.Path)),
            plan.Paths.Select(path => path.DestinationPath),
            plan.Paths.Select(path => path.StagingPath),
            plan.Paths.Select(path => path.BackupPath),
            recoveryPaths,
            failure);
    }
}
