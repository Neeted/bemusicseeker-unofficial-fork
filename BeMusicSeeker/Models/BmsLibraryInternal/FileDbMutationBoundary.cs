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
    CompletedWithCleanupFailure,

    /// <summary>
    /// The filesystem and DB are durable, but a post-commit finalizer failed.
    /// </summary>
    DurableFinalizationFailed
}

/// <summary>
/// Filesystem/DB mutation の durable point と recovery path を immutable に返します。
/// </summary>
internal sealed class FileDbMutationReceipt
{
    /// <summary>
    /// Creates immutable terminal facts for one filesystem/DB mutation.
    /// Finalization and cleanup failures remain separate dimensions so a
    /// durable result is not confused with ordinary completion.
    /// </summary>
    /// <param name="operationId">The mutation operation identity.</param>
    /// <param name="terminalState">The authoritative terminal state.</param>
    /// <param name="durableCommit">Whether filesystem and DB state committed.</param>
    /// <param name="compensationAttemptCount">Number of compensation attempts.</param>
    /// <param name="cleanupAttemptCount">Number of post-commit cleanup attempts.</param>
    /// <param name="sourcePaths">Source paths associated with the mutation.</param>
    /// <param name="destinationPaths">Destination paths associated with the mutation.</param>
    /// <param name="stagingPaths">Staging paths retained by the mutation plan.</param>
    /// <param name="backupPaths">Backup paths retained by the mutation plan.</param>
    /// <param name="recoveryPaths">Paths that require manual recovery, if any.</param>
    /// <param name="failure">The primary terminal failure, if any.</param>
    /// <param name="finalizationFailure">A failure from post-commit finalization, if any.</param>
    /// <param name="cleanupFailure">A failure from post-commit cleanup, if any.</param>
    /// <param name="destinationTypeConflicts">パッケージの事前検証で見つけた read-only の宛先型衝突。</param>
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
        Exception failure = null,
        Exception finalizationFailure = null,
        Exception cleanupFailure = null,
        IEnumerable<FileDbMutationDestinationTypeConflict> destinationTypeConflicts = null)
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
        FinalizationFailure = finalizationFailure;
        CleanupFailure = cleanupFailure;
        DestinationTypeConflicts = FreezeDestinationTypeConflicts(destinationTypeConflicts);
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

    /// <summary>
    /// Gets the exception raised after the filesystem and DB became durable.
    /// This remains separate from cleanup failure so the durable outcome is
    /// never misreported as an ordinary completion.
    /// </summary>
    public Exception FinalizationFailure { get; }

    /// <summary>
    /// Gets the exception raised while best-effort cleanup was attempted.
    /// </summary>
    public Exception CleanupFailure { get; }

    /// <summary>
    /// パッケージの事前検証で検出した immutable な宛先型衝突を取得します。
    /// executor 実行中の失敗は通常の failure 経路へ渡します。
    /// </summary>
    public IReadOnlyList<FileDbMutationDestinationTypeConflict> DestinationTypeConflicts { get; }

    /// <summary>
    /// Gets whether cleanup produced an exception after the durable commit.
    /// </summary>
    public bool HasCleanupFailure => CleanupFailure != null;

    /// <summary>
    /// Returns a receipt that preserves all terminal dimensions while adding
    /// an additional failure to the primary failure fact.
    /// </summary>
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
            combinedFailure,
            FinalizationFailure,
            CleanupFailure,
            DestinationTypeConflicts);
    }

    private static IReadOnlyList<string> Freeze(IEnumerable<string> values)
    {
        return Array.AsReadOnly((values ?? [])
            .Where(value => !string.IsNullOrWhiteSpace(value))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray());
    }

    private static IReadOnlyList<FileDbMutationDestinationTypeConflict> FreezeDestinationTypeConflicts(
        IEnumerable<FileDbMutationDestinationTypeConflict> values)
    {
        return Array.AsReadOnly((values ?? [])
            .Where(value => value != null)
            .GroupBy(value => string.Join("\u001f",
                value.SourcePath,
                value.DestinationPath,
                value.ExpectedIsDirectory,
                value.ExistingIsDirectory), StringComparer.OrdinalIgnoreCase)
            .Select(group => group.First())
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
    /// <summary>
    /// Creates immutable terminal facts for a set of independent mutations.
    /// A non-null batch finalization failure describes a post-commit owner
    /// finalizer that ran after the individual mutation receipts were durable.
    /// </summary>
    /// <param name="receipts">The individual mutation receipts to retain.</param>
    /// <param name="finalizationFailure">
    /// An optional failure raised by a finalizer for the batch as a whole.
    /// </param>
    internal FileDbMutationBatchReceipt(
        IEnumerable<FileDbMutationReceipt> receipts,
        Exception finalizationFailure = null)
    {
        Receipts = Array.AsReadOnly([.. (receipts ?? []).Where(receipt => receipt != null)]);
        HasDurableCommit = Receipts.Any(receipt => receipt.DurableCommit);
        FinalizationFailure = finalizationFailure;
        ManualRecoveryRequired = Receipts.Any(receipt =>
            receipt.TerminalState == FileDbMutationTerminalState.ManualRecoveryRequired);
        HasDurableFinalizationFailure = FinalizationFailure != null
            || Receipts.Any(receipt =>
                receipt.TerminalState == FileDbMutationTerminalState.DurableFinalizationFailed);
        CompletedWithCleanupFailure = Receipts.Any(receipt =>
            receipt.TerminalState == FileDbMutationTerminalState.CompletedWithCleanupFailure);
        RecoveryPaths = Array.AsReadOnly(Receipts
            .SelectMany(receipt => receipt.RecoveryPaths ?? [])
            .Where(path => !string.IsNullOrWhiteSpace(path))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray());
        DestinationTypeConflicts = Array.AsReadOnly(Receipts
            .SelectMany(receipt => receipt.DestinationTypeConflicts ?? [])
            .Where(conflict => conflict != null)
            .GroupBy(conflict => string.Join("\u001f",
                conflict.SourcePath,
                conflict.DestinationPath,
                conflict.ExpectedIsDirectory,
                conflict.ExistingIsDirectory), StringComparer.OrdinalIgnoreCase)
            .Select(group => group.First())
            .ToArray());
    }

    internal IReadOnlyList<FileDbMutationReceipt> Receipts { get; }

    internal bool HasDurableCommit { get; }

    /// <summary>
    /// Gets the failure raised by a post-commit finalizer for the batch as a
    /// whole, when one exists.  Individual receipt finalization failures stay
    /// on their respective <see cref="FileDbMutationReceipt"/> instances.
    /// </summary>
    internal Exception FinalizationFailure { get; }

    internal bool ManualRecoveryRequired { get; }

    /// <summary>
    /// Gets whether any durable mutation or batch finalizer reached the typed
    /// durable-finalization-failure terminal state.
    /// </summary>
    internal bool HasDurableFinalizationFailure { get; }

    internal bool CompletedWithCleanupFailure { get; }

    internal IReadOnlyList<string> RecoveryPaths { get; }

    /// <summary>パッケージの事前検証で見つかった宛先型衝突を重複なしで取得します。</summary>
    internal IReadOnlyList<FileDbMutationDestinationTypeConflict> DestinationTypeConflicts { get; }

    /// <summary>
    /// Returns the same immutable batch facts with a failure from a
    /// post-commit finalizer that applies to the batch as a whole.
    /// </summary>
    internal FileDbMutationBatchReceipt WithFinalizationFailure(Exception failure)
    {
        return failure == null || FinalizationFailure != null
            ? this
            : new FileDbMutationBatchReceipt(Receipts, failure);
    }
}

/// <summary>
/// DB owner が durable commit と session-local finalizer を区別して返す結果です。
/// </summary>
internal sealed class FileDbMutationCommitResult
{
    private FileDbMutationCommitResult(
        bool durableCommit,
        Exception failure,
        Action durableFinalizer)
    {
        DurableCommit = durableCommit;
        Failure = failure;
        DurableFinalizer = durableFinalizer;
    }

    internal bool DurableCommit { get; }

    internal Exception Failure { get; }

    /// <summary>
    /// Runs once inside the existing outer mutation session after the durable
    /// DB callback succeeds.  This is reserved for canonical internal state;
    /// public notifications and dialogs are never supplied here.
    /// </summary>
    internal Action DurableFinalizer { get; }

    internal static FileDbMutationCommitResult Durable(Action durableFinalizer = null, Exception durableFailure = null)
    {
        return new FileDbMutationCommitResult(true, durableFailure, durableFinalizer);
    }

    internal static FileDbMutationCommitResult Failed(Exception failure)
    {
        return new FileDbMutationCommitResult(false, failure, null);
    }
}

/// <summary>
/// operation-scoped mutation session が durable apply 前後を分離して扱うための prepared filesystem mutation です。
/// </summary>
internal sealed class FileDbMutationPreparedCommit
{
    private readonly FileDbMutationExecutor executor;

    /// <summary>physical prepare の成立状態または prepare failure を immutable に保持します。</summary>
    /// <param name="executor">promotion 済み状態を所有し、durable 後 completion を実行する executor。</param>
    /// <param name="failureReceipt">prepare が成立しなかった場合の executor-local terminal receipt。</param>
    internal FileDbMutationPreparedCommit(
        FileDbMutationExecutor executor,
        FileDbMutationReceipt failureReceipt)
    {
        this.executor = executor;
        FailureReceipt = failureReceipt;
    }

    /// <summary>filesystem promotion が完了し、canonical durable apply を待っているかどうか。</summary>
    internal bool Prepared => executor != null && FailureReceipt == null;

    /// <summary>prepare が成立しなかった場合の terminal receipt。</summary>
    internal FileDbMutationReceipt FailureReceipt { get; }

    /// <summary>prepare 済み physical target を session terminal facts 用に返します。</summary>
    internal IReadOnlyList<LibraryMutationSessionTarget> ConfirmedTargets
        => executor?.CreateConfirmedTargets() ?? [];

    /// <summary>durable apply が成立しない場合に保持すべき recovery candidate path を返します。</summary>
    internal IReadOnlyList<string> RecoveryCandidatePaths
        => executor?.CreateRecoveryCandidatePaths() ?? FailureReceipt?.RecoveryPaths ?? [];

    /// <summary>durable apply 後の cleanup を実行し terminal receipt を確定します。</summary>
    /// <param name="finalizationFailure">durable point 後の required finalization failure。</param>
    internal FileDbMutationReceipt CompleteAfterDurableCommit(Exception finalizationFailure = null)
    {
        return Prepared
            ? executor.CompleteAfterDurableCommit(finalizationFailure)
            : FailureReceipt;
    }

    /// <summary>durable apply 前の失敗として一度だけ compensation へ渡します。</summary>
    /// <param name="failure">canonical durable apply が成立しなかった理由。</param>
    internal FileDbMutationReceipt FailBeforeDurableCommit(Exception failure)
    {
        return Prepared
            ? executor.FailPreparedBeforeDurableCommit(failure)
            : FailureReceipt;
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

    /// <summary>一つの immutable file mutation plan の physical stage/promotion/cleanup owner を作成します。</summary>
    /// <param name="plan">事前確定済みの source/destination mutation plan。</param>
    /// <param name="fileMutationService">filesystem mutation gateway。</param>
    /// <param name="targetOnlyOptions">単一 target 操作用 option。</param>
    /// <param name="recursiveDirectoryOptions">directory tree 操作用 option。</param>
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

    /// <summary>
    /// filesystem の stage/promotion までを実行し、canonical durable apply を session owner へ委譲します。
    /// </summary>
    internal FileDbMutationPreparedCommit Prepare()
    {
        try
        {
            // どの path も staging する前に immutable な計画全体を検証します。
            // 後半の型衝突で、前半の公開済み entry を残さないためです。
            FileDbMutationDestinationTypeGuard.ValidatePlan(plan);
            Stage();
            Promote();
            return new FileDbMutationPreparedCommit(this, failureReceipt: null);
        }
        catch (Exception exception)
        {
            return new FileDbMutationPreparedCommit(
                executor: null,
                HandlePrecommitFailure(exception));
        }
    }

    /// <summary>legacy route 向けに prepare、durable callback、post-commit completion を一続きで実行します。</summary>
    /// <param name="applyDurableCommit">physical promotion 後に canonical durable point を確定する callback。</param>
    /// <returns>physical/DB/finalization/cleanup を集約した executor-local receipt。</returns>
    internal FileDbMutationReceipt Execute(Func<FileDbMutationCommitResult> applyDurableCommit)
    {
        if (applyDurableCommit == null)
        {
            throw new ArgumentNullException(nameof(applyDurableCommit));
        }

        FileDbMutationPreparedCommit preparedCommit = Prepare();
        if (!preparedCommit.Prepared)
        {
            return preparedCommit.FailureReceipt;
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
            return preparedCommit.FailBeforeDurableCommit(
                commitResult.Failure ?? new InvalidOperationException("The durable DB receipt was not produced."));
        }

        // A durable finalizer is deliberately executed while the existing
        // mutation session is still held. Its failure remains a durable
        // result and therefore never re-enters compensation.
        Exception durableFinalizerFailure = null;
        try
        {
            commitResult.DurableFinalizer?.Invoke();
        }
        catch (Exception exception)
        {
            durableFinalizerFailure = exception;
        }

        Exception finalizerFailure = commitResult.Failure;
        if (durableFinalizerFailure != null)
        {
            finalizerFailure = finalizerFailure == null
                ? durableFinalizerFailure
                : new AggregateException(finalizerFailure, durableFinalizerFailure);
        }
        return preparedCommit.CompleteAfterDurableCommit(finalizerFailure);
    }

    /// <summary>prepare 済み physical target を immutable session fact に変換します。</summary>
    internal IReadOnlyList<LibraryMutationSessionTarget> CreateConfirmedTargets()
    {
        return Array.AsReadOnly(plan.Paths
            .Select(path => new LibraryMutationSessionTarget(path.SourcePath, path.DestinationPath))
            .ToArray());
    }

    /// <summary>durable point 前に operation が停止した場合の recovery candidate path を返します。</summary>
    internal IReadOnlyList<string> CreateRecoveryCandidatePaths()
    {
        return Array.AsReadOnly(plan.SourceCleanupPaths
            .Concat(plan.SourceCleanupDirectoryPaths.Select(path => path.Path))
            .Concat(stagedPaths.Select(path => path.StagingPath))
            .Concat(backedUpPaths.Select(path => path.BackupPath))
            .Concat(promotedPaths.Select(path => path.DestinationPath))
            .Where(path => !string.IsNullOrWhiteSpace(path))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray());
    }

    /// <summary>session の canonical durable apply 成功後に cleanup と terminal receipt を確定します。</summary>
    internal FileDbMutationReceipt CompleteAfterDurableCommit(Exception finalizationFailure = null)
    {
        return FinalizePostCommit(finalizationFailure);
    }

    /// <summary>session の canonical durable apply 前失敗を既存 compensation 契約へ渡します。</summary>
    internal FileDbMutationReceipt FailPreparedBeforeDurableCommit(Exception failure)
    {
        return HandlePrecommitFailure(
            failure ?? new InvalidOperationException("The durable DB receipt was not produced."));
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
            // 計画全体の事前検証後に宛先が変わる可能性があります。親の作成、退避、
            // 公開より前に確認し、外部変更は宛先の親を変更せず通常の executor
            // failure 契約へ渡します。
            FileDbMutationDestinationTypeGuard.ValidatePath(
                path.SourcePath,
                path.DestinationPath,
                path.IsDirectory);
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

        Exception cleanupFailure = cleanupFailures.Count == 0
            ? null
            : new AggregateException(cleanupFailures);
        Exception failure = postCommitFailure;
        if (cleanupFailure != null)
        {
            failure = failure == null
                ? new IOException("Filesystem cleanup failed after the durable DB commit.", cleanupFailure)
                : new AggregateException(failure, cleanupFailure);
        }

        return CreateReceipt(
            postCommitFailure != null
                ? FileDbMutationTerminalState.DurableFinalizationFailed
                : cleanupFailure != null
                    ? FileDbMutationTerminalState.CompletedWithCleanupFailure
                    : FileDbMutationTerminalState.Completed,
            durableCommit: true,
            failure,
            cleanupFailurePaths,
            finalizationFailure: postCommitFailure,
            cleanupFailure: cleanupFailure);
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
        IEnumerable<string> additionalRecoveryPaths = null,
        Exception finalizationFailure = null,
        Exception cleanupFailure = null)
    {
        List<string> recoveryPaths = [];
        if (terminalState == FileDbMutationTerminalState.ManualRecoveryRequired
            || terminalState == FileDbMutationTerminalState.CompletedWithCleanupFailure
            || (terminalState == FileDbMutationTerminalState.DurableFinalizationFailed
                && cleanupFailure != null))
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
            failure,
            finalizationFailure,
            cleanupFailure);
    }
}
