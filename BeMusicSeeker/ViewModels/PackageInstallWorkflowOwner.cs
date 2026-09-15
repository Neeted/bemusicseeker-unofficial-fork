using System;
using System.Collections.Generic;
using System.Linq;
using System.IO;
using System.Runtime.ExceptionServices;
using System.Threading;
using System.Threading.Tasks;
using BeMusicSeeker.Models;
using BeMusicSeeker.Models.BmsLibraryInternal;
using BeMusicSeeker.Models.Utils;
using BeMusicSeeker.Views.Dialogs;
using Ribbit.Logging;

namespace BeMusicSeeker.ViewModels;

internal interface IPackageInstallMutationPort
{
    IReadOnlyList<ChartPackage> Install(
        BMSLibrary library,
        IEnumerable<string> installPaths,
        CancellationToken token,
        Action onEachPathProcessed,
        Action<string, int, int> onEachArchiveExtractStarted);
}

internal interface IPackageInstallTerminalMutationPort
{
    PackageInstallCommandResult InstallWithResult(
        BMSLibrary library,
        IEnumerable<string> installPaths,
        CancellationToken token,
        Action onEachPathProcessed,
        Action<string, int, int> onEachArchiveExtractStarted);
}

/// <summary>
/// Progress-aware package mutation seam.  The legacy mutation-port methods
/// remain available to test doubles and older composition code, while the
/// production owner uses this narrow writer-only boundary.
/// </summary>
internal interface IPackageInstallProgressMutationPort
{
    PackageInstallCommandResult InstallWithProgress(
        BMSLibrary library,
        IEnumerable<string> installPaths,
        CancellationToken token,
        IPackageInstallProgressWriter progressWriter);
}

internal sealed class BmsLibraryPackageInstallMutationPort :
    IPackageInstallMutationPort,
    IPackageInstallTerminalMutationPort,
    IPackageInstallProgressMutationPort
{
    public IReadOnlyList<ChartPackage> Install(
        BMSLibrary library,
        IEnumerable<string> installPaths,
        CancellationToken token,
        Action onEachPathProcessed,
        Action<string, int, int> onEachArchiveExtractStarted)
    {
        return library?.InstallChartPackagesAuto(
            installPaths,
            token) ?? [];
    }

    public PackageInstallCommandResult InstallWithResult(
        BMSLibrary library,
        IEnumerable<string> installPaths,
        CancellationToken token,
        Action onEachPathProcessed,
        Action<string, int, int> onEachArchiveExtractStarted)
    {
        return library?.InstallChartPackagesAutoWithProgress(
            installPaths,
            token,
            NullPackageInstallProgressWriter.Instance, reportAtTerminal: true)
            ?? new PackageInstallCommandResult([], null);
    }

    public PackageInstallCommandResult InstallWithProgress(
        BMSLibrary library,
        IEnumerable<string> installPaths,
        CancellationToken token,
        IPackageInstallProgressWriter progressWriter)
    {
        return library?.InstallChartPackagesAutoWithProgress(
            installPaths,
            token,
            progressWriter, reportAtTerminal: true)
            ?? new PackageInstallCommandResult([], null);
    }
}

internal sealed class PackageInstallRefreshSuppressionChangedEventArgs : EventArgs
{
    internal PackageInstallRefreshSuppressionChangedEventArgs(bool isSuppressed)
    {
        IsSuppressed = isSuppressed;
    }

    internal bool IsSuppressed { get; }
}

internal sealed class PackageInstallCompletionReceipt : EventArgs
{
    internal PackageInstallCompletionReceipt(
        long generation,
        IEnumerable<ChartPackage> packages,
        PackageInstallCommandResult commandResult = null)
    {
        Generation = generation;
        Packages = [.. (packages ?? []).Where(package => package != null)];
        SessionReceipt = commandResult?.SessionReceipt;
    }

    internal long Generation { get; }

    internal IReadOnlyList<ChartPackage> Packages { get; }

    /// <summary>package install command が返した canonical operation-scoped terminal facts。</summary>
    internal LibraryMutationSessionReceipt SessionReceipt { get; }

    /// <summary>パッケージ変更前に見つかった immutable な宛先型衝突を取得します。</summary>
    internal IReadOnlyList<FileDbMutationDestinationTypeConflict> DestinationTypeConflicts =>
        SessionReceipt?.DestinationTypeConflicts ?? [];

    internal bool HasDurableCommit => SessionReceipt?.DurableCommit == true;

    internal bool ManualRecoveryRequired => SessionReceipt?.ManualRecoveryRequired == true;

    /// <summary>Gets whether package finalization failed after durable state.</summary>
    internal bool HasDurableFinalizationFailure => SessionReceipt?.HasDurableFinalizationFailure == true;

    internal bool CompletedWithCleanupFailure => SessionReceipt?.CompletedWithCleanupFailure == true;

    internal IReadOnlyList<string> RecoveryPaths => SessionReceipt?.CandidatePaths ?? [];
}

internal sealed class PackageInstallFailure : EventArgs
{
    /// <summary>Retains a completed mutation's facts when outer workflow cleanup fails.</summary>
    internal PackageInstallFailure(long generation, IEnumerable<string> paths, Exception exception,
        PackageInstallCommandResult commandResult = null)
    {
        Generation = generation;
        Paths = [.. (paths ?? []).Where(path => !string.IsNullOrWhiteSpace(path))];
        Exception = exception ?? throw new ArgumentNullException(nameof(exception));
        CommandResult = commandResult;
    }

    internal long Generation { get; }

    internal IReadOnlyList<string> Paths { get; }

    internal Exception Exception { get; }

    /// <summary>Gets mutation facts produced before the lifecycle failure.</summary>
    internal PackageInstallCommandResult CommandResult { get; }
}

/// <summary>
/// Owns every package-install ingress and keeps queue, live-library generation,
/// completion, and failure ordering outside the shell ViewModel.
/// </summary>
internal sealed class PackageInstallWorkflowOwner
{
    private readonly object syncRoot = new();

    private readonly IUiDialogService dialogs;

    private readonly ChartFileOperationSynchronizer chartFileOperations;

    private readonly ChartMutationActivityOwner chartMutationActivity;

    private readonly IPackageInstallMutationPort mutationPort;

    private readonly DroppedInstallIngressMaterializer droppedInstallIngressMaterializer;

    private readonly Func<Action, bool> tryDispatchToUi;

    private readonly Action<Exception> reportNotificationFailure;

    private readonly List<QueueProcessorContext> queueProcessors = [];

    private BMSLibrary library;

    private long generation;

    private int shutdownState;

    private readonly object statusPublicationGate = new();

    private ActiveStatusPublication activeStatusPublication;

    private long latestStatusSequenceGeneration = long.MinValue;

    private long latestStatusSequence;

    /// <summary>共通の変更受付と UI 通知サービスを受け取り、導入予約から queue 終端までを所有します。</summary>
    internal PackageInstallWorkflowOwner(
        IUiDialogService dialogs,
        ChartFileOperationSynchronizer chartFileOperations,
        ChartMutationActivityOwner chartMutationActivity,
        IPackageInstallMutationPort mutationPort,
        Func<Action, bool> tryDispatchToUi,
        Action<Exception> reportNotificationFailure = null,
        DroppedInstallIngressMaterializer droppedInstallIngressMaterializer = null)
    {
        this.dialogs = dialogs ?? throw new ArgumentNullException(nameof(dialogs));
        this.chartFileOperations = chartFileOperations ?? throw new ArgumentNullException(nameof(chartFileOperations));
        this.chartMutationActivity = chartMutationActivity ?? throw new ArgumentNullException(nameof(chartMutationActivity));
        this.mutationPort = mutationPort ?? throw new ArgumentNullException(nameof(mutationPort));
        this.tryDispatchToUi = tryDispatchToUi ?? throw new ArgumentNullException(nameof(tryDispatchToUi));
        this.reportNotificationFailure = reportNotificationFailure;
        this.droppedInstallIngressMaterializer = droppedInstallIngressMaterializer
            ?? CreateProductionDroppedInstallIngressMaterializer();
        lock (syncRoot)
        {
            queueProcessors.Add(CreateQueueProcessorUnsafe(0, null));
        }
    }

    internal event Action<DropInstallQueueStatusSnapshot> StatusChanged;

    internal event Action<PackageInstallCompletionReceipt> CompletionPublished;

    internal event Action<PackageInstallFailure> FailurePublished;

    internal event EventHandler<PackageInstallRefreshSuppressionChangedEventArgs> RefreshSuppressionChanged;

    /// <summary>
    /// Reports that the package-install owner received a cancellation request.
    /// </summary>
    internal event Action CancelAllRequested;

    internal bool IsActive
    {
        get
        {
            lock (syncRoot)
            {
                PruneIdleRetiredQueuesUnsafe();
                if (queueProcessors.Count == 0)
                {
                    return false;
                }
                QueueProcessorContext current = queueProcessors[queueProcessors.Count - 1];
                return !current.Processor.IsIdle;
            }
        }
    }

    internal bool IsIdle
    {
        get
        {
            lock (syncRoot)
            {
                PruneIdleRetiredQueuesUnsafe();
                return queueProcessors.All(queue => queue.Processor.IsIdle);
            }
        }
    }

    /// <summary>
    /// Captures all current and retired queue lifecycles and completes when that snapshot is idle.
    /// </summary>
    internal Task WaitForIdleAsync()
    {
        Task[] idleTasks;
        lock (syncRoot)
        {
            idleTasks = [.. queueProcessors.Select(queue => queue.Processor.WaitForIdleAsync())];
            PruneIdleRetiredQueuesUnsafe();
        }

        return idleTasks.Length switch
        {
            0 => Task.CompletedTask,
            1 => idleTasks[0],
            _ => Task.WhenAll(idleTasks)
        };
    }

    internal void AttachLibrary(BMSLibrary nextLibrary)
    {
        QueueProcessorContext previousQueue;
        lock (syncRoot)
        {
            PruneIdleRetiredQueuesUnsafe();
            if (ReferenceEquals(library, nextLibrary))
            {
                return;
            }
            library = nextLibrary;
            Interlocked.Increment(ref generation);
            previousQueue = queueProcessors[queueProcessors.Count - 1];
            previousQueue.AcceptingAdmissions = false;
            queueProcessors.Add(CreateQueueProcessorUnsafe(generation, nextLibrary));
        }
        previousQueue.Processor.CancelAll();
        DispatchNotification(() => StatusChanged?.Invoke(new DropInstallQueueStatusSnapshot()));
    }

    internal void DetachLibrary()
    {
        AttachLibrary(null);
    }

    /// <summary>入力を導入 queue へ渡し、Busy・受付停止を含む未受理を呼出元へ返します。</summary>
    internal bool Enqueue(IEnumerable<string> paths)
    {
        string[] pathSnapshot = [.. (paths ?? []).Where(path => !string.IsNullOrWhiteSpace(path))];
        return pathSnapshot.Length > 0
            && TryEnqueue(new DroppedInstallBatchRequest(pathSnapshot));
    }

    /// <summary>
    /// 現行 generation の最初の drop は、queue へ挿入する前に共通の変更受付を取得します。
    /// 同じ queue への追加 drop は既存の ownership で受理し、拒否時の入力回収は lock 外で行います。
    /// </summary>
    internal bool TryEnqueue(DroppedInstallBatchRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        QueueProcessorContext queue = null;
        DropInstallQueueProcessor.EnqueueTransition transition = null;
        lock (syncRoot)
        {
            if (Volatile.Read(ref shutdownState) == 0)
            {
                QueueProcessorContext candidate = queueProcessors[queueProcessors.Count - 1];
                if (candidate.AcceptingAdmissions && candidate.Library != null)
                {
                    IDisposable queueLease = candidate.OperationLease;
                    bool acquiredLease = queueLease == null;
                    if (!acquiredLease || chartFileOperations.TryEnter(out queueLease))
                    {
                        queue = candidate;
                        transition = candidate.Processor.TryEnqueueCore(request);
                        if (transition.Accepted)
                        {
                            candidate.OperationLease = queueLease;
                        }
                        else if (acquiredLease)
                        {
                            // この lease の解放は atomic state 更新だけで、callback を呼ばない。
                            queueLease.Dispose();
                        }
                    }
                }
            }
        }
        if (queue == null || !transition.Accepted)
        {
            request.TryAbandonUnconsumedSources();
            return false;
        }

        queue.Processor.PublishEnqueueTransition(transition);
        return true;
    }

    /// <summary>単一 path の導入予約が受理されたかを返します。</summary>
    internal bool EnqueueSingle(string path)
    {
        return Enqueue(string.IsNullOrWhiteSpace(path) ? [] : [path]);
    }

    /// <summary>
    /// Synchronously acquires borrowed FileDrop paths and queues only a complete durable batch.
    /// </summary>
    internal DroppedInstallIngressAcquisitionResult AcquireAndTryEnqueueDroppedPaths(
        IEnumerable<string> paths)
    {
        DroppedInstallIngressAcquisitionResult acquisition =
            droppedInstallIngressMaterializer.Acquire(paths);
        if (!acquisition.Succeeded)
        {
            return acquisition;
        }
        if (TryEnqueue(acquisition.Request))
        {
            return acquisition;
        }
        return DroppedInstallIngressAcquisitionResult.Failure(
            DroppedInstallIngressFailureKind.QueueRejected,
            new InvalidOperationException("The package install queue is busy or not accepting requests."));
    }

    internal void CancelAll()
    {
        NotifyCancelAllRequested();
        QueueProcessorContext queue;
        lock (syncRoot)
        {
            queue = queueProcessors[queueProcessors.Count - 1];
        }
        queue.Processor.CancelAll();
    }

    private void NotifyCancelAllRequested()
    {
        try
        {
            CancelAllRequested?.Invoke();
        }
        catch (Exception exception)
        {
            ReportNotificationFailure(exception);
        }
    }

    internal void RequestShutdown()
    {
        QueueProcessorContext[] queues;
        lock (syncRoot)
        {
            Volatile.Write(ref shutdownState, 1);
            Interlocked.Increment(ref generation);
            queues = [.. queueProcessors];
            foreach (QueueProcessorContext queue in queues)
            {
                queue.AcceptingAdmissions = false;
            }
        }
        foreach (QueueProcessorContext queue in queues)
        {
            queue.Processor.CancelAll();
        }
    }

    private QueueProcessorContext CreateQueueProcessorUnsafe(long processorGeneration, BMSLibrary processorLibrary)
    {
        var context = new QueueProcessorContext(processorGeneration, processorLibrary);
        context.Processor = new DropInstallQueueProcessor(
            (request, token) =>
            {
                context.ActiveBatch = request;
                ProcessBatch(context, request, token);
            },
            snapshot => PublishQueueStatus(context, snapshot),
            exception => PublishBatchFailure(context, exception));
        return context;
    }

    private void ProcessBatch(QueueProcessorContext context, DroppedInstallBatchRequest request, CancellationToken token)
    {
        BMSLibrary currentLibrary;
        long currentGeneration;
        lock (syncRoot)
        {
            currentLibrary = context.Library;
            currentGeneration = context.Generation;
        }
        if (currentLibrary == null)
        {
            throw new InvalidOperationException("Package install requested before a library was attached.");
        }
        if (token.IsCancellationRequested || !IsCurrentGeneration(currentGeneration, currentLibrary))
        {
            return;
        }

        var progressWriter = new PackageInstallProgressWriter(this, context);
        int completedPathCount = 0;
        PackageInstallCommandResult commandResult = ExecuteInstallBatch(
            currentGeneration,
            currentLibrary,
            request,
            token,
            progressWriter,
            () => context.Processor.ReportActiveBatchProgress(++completedPathCount),
            (path, index, total) => context.Processor.ReportActiveBatchCurrentWork(
                index,
                total,
                GetInstallPathDisplayName(path)),
            out Exception terminalFailure);
        commandResult ??= new PackageInstallCommandResult([], null);
        IReadOnlyList<ChartPackage> packages = commandResult.RegisteredPackages;
        if (!IsCurrentGeneration(currentGeneration, currentLibrary))
        {
            return;
        }
        if (terminalFailure != null)
        {
            var failure = new PackageInstallFailure(currentGeneration, request.OriginalPaths,
                terminalFailure, commandResult);
            QueueTerminalNotification(context, () =>
            {
                if (IsCurrentGeneration(currentGeneration, currentLibrary))
                    FailurePublished?.Invoke(failure);
            }, terminalFailure);
            return;
        }
        if (packages.Count == 0
            && !commandResult.HasRequiredFailure
            && commandResult.DestinationTypeConflicts.Count == 0
            && !commandResult.CompletedWithCleanupFailure)
        {
            return;
        }
        var receipt = new PackageInstallCompletionReceipt(currentGeneration, packages, commandResult);
        QueueTerminalNotification(context, () =>
        {
            if (IsCurrentGeneration(currentGeneration, currentLibrary))
            {
                CompletionPublished?.Invoke(receipt);
            }
        });
    }

    private PackageInstallCommandResult ExecuteInstallBatch(
        long expectedGeneration,
        BMSLibrary library,
        DroppedInstallBatchRequest request,
        CancellationToken token,
        IPackageInstallProgressWriter progressWriter,
        Action onEachPathProcessed,
        Action<string, int, int> onEachArchiveExtractStarted,
        out Exception terminalFailure)
    {
        terminalFailure = null;
        string[] normalizedInstallPaths = [.. (request?.Paths ?? [])
            .Where(path => !string.IsNullOrWhiteSpace(path))];
        if (normalizedInstallPaths.Length == 0 || token.IsCancellationRequested)
        {
            return new PackageInstallCommandResult([], null);
        }

        BMSLibrary.OperationDialogScope dialogScope = null;
        IDisposable activityLease = null;
        bool suppressionStarted = false;
        bool mutationAllowed = true;
        var failures = new List<ExceptionDispatchInfo>();
        IReadOnlyList<ChartPackage> packages = [];
        PackageInstallCommandResult commandResult = null;
        try
        {
            // queue が受理から drain 終端まで共通受付を所有する。batch ごとに再取得しない。
            dialogScope = library.BeginOperationDialogScope();
            activityLease = chartMutationActivity.Enter();
            if (token.IsCancellationRequested
                || !IsCurrentGeneration(expectedGeneration, library))
            {
                mutationAllowed = false;
            }
            else
            {
                suppressionStarted = true;
                PublishRefreshSuppressionChanged(isSuppressed: true);
                if (!request.TransferSourceOwnershipToInstaller())
                {
                    mutationAllowed = false;
                }
                else
                {
                    if (mutationPort is IPackageInstallProgressMutationPort progressMutationPort)
                    {
                        commandResult = progressMutationPort.InstallWithProgress(
                            library,
                            normalizedInstallPaths,
                            token,
                            progressWriter);
                        packages = commandResult?.RegisteredPackages ?? [];
                    }
                    else if (mutationPort is IPackageInstallTerminalMutationPort terminalMutationPort)
                    {
                        commandResult = terminalMutationPort.InstallWithResult(
                            library,
                            normalizedInstallPaths,
                            token,
                            onEachPathProcessed,
                            onEachArchiveExtractStarted);
                        packages = commandResult?.RegisteredPackages ?? [];
                    }
                    else
                    {
                        packages = mutationPort.Install(
                            library,
                            normalizedInstallPaths,
                            token,
                            onEachPathProcessed,
                            onEachArchiveExtractStarted) ?? [];
                    }
                }
            }
        }
        catch (Exception exception)
        {
            failures.Add(ExceptionDispatchInfo.Capture(exception));
        }
        finally
        {
            if (progressWriter is PackageInstallProgressWriter packageProgressWriter)
            {
                packageProgressWriter.Seal();
            }
            if (suppressionStarted)
            {
                CaptureCleanupFailure(() => PublishRefreshSuppressionChanged(isSuppressed: false), failures);
            }
            if (activityLease != null)
            {
                CaptureCleanupFailure(activityLease.Dispose, failures);
            }
            if (dialogScope != null)
            {
                CaptureCleanupFailure(dialogScope.Dispose, failures);
                IReadOnlyList<BMSLibrary.OperationDialogMessage> messages = dialogScope.Messages;
                if (messages.Count > 0)
                {
                    // OK-only の情報通知で worker を止めない。確認が必要な入力は mutation 前に解決済み。
                    DispatchNotification(() =>
                    {
                        if (IsCurrentGeneration(expectedGeneration, library))
                        {
                            FileDbMutationReport.ShowOperationMessagesAsync(
                                dialogs, messages, reportNotificationFailure).ObserveFault();
                        }
                    });
                }
            }
        }

        if (failures.Count > 0 && commandResult?.SessionReceipt != null)
        {
            terminalFailure = failures.Count == 1 ? failures[0].SourceException
                : new AggregateException(failures.Select(failure => failure.SourceException));
            return commandResult;
        }
        switch (failures.Count)
        {
            case 0:
                return mutationAllowed
                    ? commandResult ?? new PackageInstallCommandResult(packages, null)
                    : new PackageInstallCommandResult([], commandResult?.SessionReceipt);
            case 1:
                failures[0].Throw();
                break;
            default:
                throw new AggregateException(failures.Select(failure => failure.SourceException));
        }
        return new PackageInstallCommandResult([], commandResult?.SessionReceipt);
    }

    private void PublishRefreshSuppressionChanged(bool isSuppressed)
    {
        RefreshSuppressionChanged?.Invoke(
            this,
            new PackageInstallRefreshSuppressionChangedEventArgs(isSuppressed));
    }

    private static void CaptureCleanupFailure(Action cleanup, List<ExceptionDispatchInfo> failures)
    {
        try
        {
            cleanup();
        }
        catch (Exception exception)
        {
            failures.Add(ExceptionDispatchInfo.Capture(exception));
        }
    }

    private void PublishQueueStatus(QueueProcessorContext context, DropInstallQueueStatusSnapshot snapshot)
    {
        DropInstallQueueStatusSnapshot copy = snapshot ?? new DropInstallQueueStatusSnapshot();
        (Action Notification, Exception Failure)[] terminalNotifications = [];
        if (!copy.IsActive)
        {
            lock (syncRoot)
            {
                // 表示の generation 判定より先に drain 済み lease を解放する。
                // 遅い inactive 通知の間に追加された batch の lease は解放しない。
                if (context.Processor.IsIdle)
                {
                    context.OperationLease?.Dispose();
                    context.OperationLease = null;
                    terminalNotifications = [.. context.TerminalNotifications];
                    context.TerminalNotifications.Clear();
                }
            }
        }
        // 後続 batch と source cleanup が受付を所有する間は terminal を呼ばない。
        // 受付解放と切り離した通知を lock 外で発行し、subscriber の再入も許可する。
        foreach ((Action notification, Exception failure) in terminalNotifications)
        {
            if (IsCurrentGeneration(context.Generation, context.Library))
            {
                DispatchNotification(notification, failure);
            }
            else if (failure != null)
            {
                ReportNotificationFailure(failure);
            }
        }
        if (!IsCurrentGeneration(context.Generation, null, allowNullLibrary: true))
        {
            return;
        }
        bool schedule;
        ActiveStatusPublication publication = null;
        lock (statusPublicationGate)
        {
            if (latestStatusSequenceGeneration != context.Generation)
            {
                latestStatusSequenceGeneration = context.Generation;
                latestStatusSequence = 0L;
            }
            if (copy.Sequence <= latestStatusSequence)
            {
                return;
            }
            latestStatusSequence = copy.Sequence;

            if (!copy.IsActive)
            {
                if (activeStatusPublication != null
                    && activeStatusPublication.Generation == context.Generation)
                {
                    activeStatusPublication.TerminalQueued = true;
                }
                schedule = false;
            }
            else
            {
                publication = activeStatusPublication;
                if (publication == null
                    || publication.Generation != context.Generation
                    || publication.TerminalQueued)
                {
                    if (publication != null)
                    {
                        publication.Superseded = true;
                    }
                    publication = new ActiveStatusPublication(context.Generation, copy);
                    activeStatusPublication = publication;
                    schedule = true;
                }
                else
                {
                    publication.Snapshot = copy;
                    schedule = false;
                }
            }
        }
        if (!copy.IsActive)
        {
            DispatchNotification(() =>
            {
                if (IsCurrentGeneration(context.Generation, null, allowNullLibrary: true))
                {
                    StatusChanged?.Invoke(copy);
                }
            });
            return;
        }
        if (!schedule)
        {
            return;
        }
        if (!DispatchNotification(() => DrainActiveQueueStatus(publication)))
        {
            lock (statusPublicationGate)
            {
                if (ReferenceEquals(activeStatusPublication, publication))
                {
                    activeStatusPublication = null;
                }
            }
        }
    }

    private void DrainActiveQueueStatus(ActiveStatusPublication publication)
    {
        DropInstallQueueStatusSnapshot snapshot;
        lock (statusPublicationGate)
        {
            if (publication.Superseded)
            {
                return;
            }
            snapshot = publication.Snapshot;
            if (ReferenceEquals(activeStatusPublication, publication))
            {
                activeStatusPublication = null;
            }
        }
        if (snapshot != null
            && IsCurrentGeneration(publication.Generation, null, allowNullLibrary: true))
        {
            StatusChanged?.Invoke(snapshot);
        }
    }

    private sealed class ActiveStatusPublication
    {
        internal ActiveStatusPublication(
            long generation,
            DropInstallQueueStatusSnapshot snapshot)
        {
            Generation = generation;
            Snapshot = snapshot;
        }

        internal long Generation { get; }

        internal DropInstallQueueStatusSnapshot Snapshot { get; set; }

        internal bool TerminalQueued { get; set; }

        internal bool Superseded { get; set; }
    }

    /// <summary>
    /// Converts immutable package progress facts into queue-owned status
    /// updates. The writer is sealed before terminal cleanup so a late source
    /// callback cannot update a later batch.
    /// </summary>
    private sealed class PackageInstallProgressWriter : IPackageInstallProgressWriter
    {
        private readonly PackageInstallWorkflowOwner owner;

        private readonly QueueProcessorContext context;

        private int completedPathCount;

        private int sealedState;

        internal PackageInstallProgressWriter(
            PackageInstallWorkflowOwner owner,
            QueueProcessorContext context)
        {
            this.owner = owner ?? throw new ArgumentNullException(nameof(owner));
            this.context = context ?? throw new ArgumentNullException(nameof(context));
        }

        public void TryWrite(PackageInstallProgressUpdate update)
        {
            if (Volatile.Read(ref sealedState) != 0)
            {
                return;
            }

            try
            {
                switch (update.Kind)
                {
                    case PackageInstallProgressKind.SourceProcessed:
                        int processed = Interlocked.Increment(ref completedPathCount);
                        context.Processor.ReportActiveBatchProgress(processed);
                        break;
                    case PackageInstallProgressKind.ArchiveExtractStarted:
                        context.Processor.ReportActiveBatchCurrentWork(
                            update.Index,
                            update.Total,
                            GetInstallPathDisplayName(update.Path));
                        break;
                }
            }
            catch (Exception exception)
            {
                owner.ReportNotificationFailure(exception);
            }
        }

        internal void Seal()
        {
            Interlocked.Exchange(ref sealedState, 1);
        }
    }

    private void PublishBatchFailure(QueueProcessorContext context, Exception exception)
    {
        if (exception == null)
        {
            return;
        }
        if (context.Library == null)
        {
            ReportNotificationFailure(exception);
            return;
        }
        if (!IsCurrentGeneration(context.Generation, context.Library))
        {
            ReportNotificationFailure(exception);
            return;
        }
        var failure = new PackageInstallFailure(context.Generation, context.ActiveBatch?.OriginalPaths, exception);
        QueueTerminalNotification(context, () =>
        {
            if (IsCurrentGeneration(context.Generation, context.Library))
            {
                FailurePublished?.Invoke(failure);
            }
            else
            {
                ReportNotificationFailure(exception);
            }
        }, exception);
    }

    private void QueueTerminalNotification(
        QueueProcessorContext context,
        Action notification,
        Exception failure = null)
    {
        lock (syncRoot)
        {
            context.TerminalNotifications.Add((notification, failure));
        }
    }

    private bool DispatchNotification(Action notification, Exception dispatchFailure = null)
    {
        if (notification == null)
        {
            return false;
        }
        try
        {
            bool dispatched = tryDispatchToUi(() =>
            {
                try
                {
                    notification();
                }
                catch (Exception exception)
                {
                    ReportNotificationFailure(dispatchFailure ?? exception);
                    if (dispatchFailure != null && !ReferenceEquals(dispatchFailure, exception))
                    {
                        ReportNotificationFailure(exception);
                    }
                }
            });
            if (!dispatched)
            {
                ReportNotificationFailure(dispatchFailure
                    ?? new InvalidOperationException("The UI dispatcher is shutting down."));
            }
            return dispatched;
        }
        catch (Exception exception)
        {
            ReportNotificationFailure(dispatchFailure ?? exception);
            if (dispatchFailure != null && !ReferenceEquals(dispatchFailure, exception))
            {
                ReportNotificationFailure(exception);
            }
            return false;
        }
    }

    private void ReportNotificationFailure(Exception exception)
    {
        if (exception == null || reportNotificationFailure == null)
        {
            return;
        }
        try
        {
            reportNotificationFailure(exception);
        }
        catch
        {
            // A reporting boundary must not corrupt the queue lifecycle.
        }
    }

    private bool IsCurrentGeneration(long expectedGeneration, BMSLibrary expectedLibrary, bool allowNullLibrary = false)
    {
        return Volatile.Read(ref shutdownState) == 0
            && Interlocked.Read(ref generation) == expectedGeneration
            && (allowNullLibrary || ReferenceEquals(library, expectedLibrary));
    }

    private void PruneIdleRetiredQueuesUnsafe()
    {
        for (int index = queueProcessors.Count - 2; index >= 0; index--)
        {
            if (queueProcessors[index].Processor.IsIdleReceiptCompleted)
            {
                queueProcessors.RemoveAt(index);
            }
        }
    }

    private sealed class QueueProcessorContext(long generation, BMSLibrary library)
    {
        internal long Generation { get; } = generation;

        internal BMSLibrary Library { get; } = library;

        internal DropInstallQueueProcessor Processor { get; set; }

        internal DroppedInstallBatchRequest ActiveBatch { get; set; }

        /// <summary>最初の受理から worker・未引渡し source cleanup の終端まで所有する共通受付。</summary>
        internal IDisposable OperationLease { get; set; }

        /// <summary>受理済み batch の結果を、共通受付の解放後に一度だけ発行するため保持します。</summary>
        internal List<(Action Notification, Exception Failure)> TerminalNotifications { get; } = [];

        /// <summary>
        /// Gets or sets whether the owner may linearize a new admission against this generation.
        /// </summary>
        internal bool AcceptingAdmissions { get; set; } = true;

    }

    private static string GetInstallPathDisplayName(string path)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            return string.Empty;
        }
        string trimmed = path.TrimEnd(System.IO.Path.DirectorySeparatorChar, System.IO.Path.AltDirectorySeparatorChar);
        string fileName = System.IO.Path.GetFileName(trimmed);
        return string.IsNullOrWhiteSpace(fileName) ? path : fileName;
    }

    private static DroppedInstallIngressMaterializer CreateProductionDroppedInstallIngressMaterializer()
    {
        return new DroppedInstallIngressMaterializer(
            Path.GetTempPath(),
            TempDirectoryPublisher.IsManagedPath,
            () => TempDirectoryPublisher.Get("drop-ingress"),
            root => TempDirectoryPublisher.TryDeleteManagedPath(
                root,
                info => NLogWrapper.FileLogger?.Info(info + " reason=drop_ingress_abandoned"),
                (path, exception) => NLogWrapper.FileLogger?.Warn(
                    exception,
                    "temp_cleanup_failed reason=drop_ingress_abandoned path=" + path)),
            (path, exception) => NLogWrapper.FileLogger?.Warn(
                exception,
                "temp_cleanup_failed reason=drop_ingress_abandoned path=" + path));
    }
}
