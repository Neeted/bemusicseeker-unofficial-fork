using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.ExceptionServices;
using System.Threading;
using BeMusicSeeker.Models;

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

internal sealed class BmsLibraryPackageInstallMutationPort : IPackageInstallMutationPort
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
            token,
            onEachPathProcessed,
            onEachArchiveExtractStarted) ?? [];
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
    internal PackageInstallCompletionReceipt(long generation, IEnumerable<ChartPackage> packages)
    {
        Generation = generation;
        Packages = [.. (packages ?? []).Where(package => package != null)];
    }

    internal long Generation { get; }

    internal IReadOnlyList<ChartPackage> Packages { get; }
}

internal sealed class PackageInstallFailure : EventArgs
{
    internal PackageInstallFailure(long generation, IEnumerable<string> paths, Exception exception)
    {
        Generation = generation;
        Paths = [.. (paths ?? []).Where(path => !string.IsNullOrWhiteSpace(path))];
        Exception = exception ?? throw new ArgumentNullException(nameof(exception));
    }

    internal long Generation { get; }

    internal IReadOnlyList<string> Paths { get; }

    internal Exception Exception { get; }
}

/// <summary>
/// Owns every package-install ingress and keeps queue, live-library generation,
/// completion, and failure ordering outside the shell ViewModel.
/// </summary>
internal sealed class PackageInstallWorkflowOwner
{
    private readonly object syncRoot = new();

    private readonly ChartFileOperationSynchronizer chartFileOperations;

    private readonly ChartMutationActivityOwner chartMutationActivity;

    private readonly IPackageInstallMutationPort mutationPort;

    private readonly Action<Action> dispatchToUi;

    private readonly Action<Exception> reportNotificationFailure;

    private readonly List<QueueProcessorContext> queueProcessors = [];

    private BMSLibrary library;

    private long generation;

    private int shutdownState;

    internal PackageInstallWorkflowOwner(
        ChartFileOperationSynchronizer chartFileOperations,
        ChartMutationActivityOwner chartMutationActivity,
        IPackageInstallMutationPort mutationPort,
        Action<Action> dispatchToUi,
        Action<Exception> reportNotificationFailure = null)
    {
        this.chartFileOperations = chartFileOperations ?? throw new ArgumentNullException(nameof(chartFileOperations));
        this.chartMutationActivity = chartMutationActivity ?? throw new ArgumentNullException(nameof(chartMutationActivity));
        this.mutationPort = mutationPort ?? throw new ArgumentNullException(nameof(mutationPort));
        this.dispatchToUi = dispatchToUi ?? throw new ArgumentNullException(nameof(dispatchToUi));
        this.reportNotificationFailure = reportNotificationFailure;
        lock (syncRoot)
        {
            queueProcessors.Add(CreateQueueProcessorUnsafe(0, null));
        }
    }

    internal event Action<DropInstallQueueStatusSnapshot> StatusChanged;

    internal event Action<PackageInstallCompletionReceipt> CompletionPublished;

    internal event Action<PackageInstallFailure> FailurePublished;

    internal event EventHandler<PackageInstallRefreshSuppressionChangedEventArgs> RefreshSuppressionChanged;

    internal bool IsActive
    {
        get
        {
            lock (syncRoot)
            {
                PruneIdleRetiredQueuesUnsafe();
                return queueProcessors.Count > 0 && !queueProcessors[queueProcessors.Count - 1].Processor.IsIdle;
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
            queueProcessors.Add(CreateQueueProcessorUnsafe(generation, nextLibrary));
        }
        previousQueue.Processor.CancelAll();
        DispatchNotification(() => StatusChanged?.Invoke(new DropInstallQueueStatusSnapshot()));
    }

    internal void DetachLibrary()
    {
        AttachLibrary(null);
    }

    internal void Enqueue(IEnumerable<string> paths)
    {
        string[] pathSnapshot = [.. (paths ?? []).Where(path => !string.IsNullOrWhiteSpace(path))];
        if (pathSnapshot.Length == 0)
        {
            return;
        }

        lock (syncRoot)
        {
            if (Volatile.Read(ref shutdownState) != 0)
            {
                return;
            }
            QueueProcessorContext queue = queueProcessors[queueProcessors.Count - 1];
            queue.Processor.Enqueue(pathSnapshot);
        }
    }

    internal void EnqueueSingle(string path)
    {
        Enqueue(string.IsNullOrWhiteSpace(path) ? [] : [path]);
    }

    internal void CancelAll()
    {
        QueueProcessorContext queue;
        lock (syncRoot)
        {
            queue = queueProcessors[queueProcessors.Count - 1];
        }
        queue.Processor.CancelAll();
    }

    internal void RequestShutdown()
    {
        QueueProcessorContext[] queues;
        lock (syncRoot)
        {
            Volatile.Write(ref shutdownState, 1);
            Interlocked.Increment(ref generation);
            queues = [.. queueProcessors];
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

        int completedPathCount = 0;
        IReadOnlyList<ChartPackage> packages = ExecuteInstallBatch(
            currentLibrary,
            request.Paths,
            token,
            () => context.Processor.ReportActiveBatchProgress(++completedPathCount),
            (path, index, total) => context.Processor.ReportActiveBatchCurrentWork(
                index,
                total,
                GetInstallPathDisplayName(path)));
        packages ??= [];
        if (!IsCurrentGeneration(currentGeneration, currentLibrary))
        {
            return;
        }
        if (packages.Count == 0)
        {
            return;
        }
        var receipt = new PackageInstallCompletionReceipt(currentGeneration, packages);
        DispatchNotification(() =>
        {
            if (IsCurrentGeneration(currentGeneration, currentLibrary))
            {
                CompletionPublished?.Invoke(receipt);
            }
        });
    }

    private IReadOnlyList<ChartPackage> ExecuteInstallBatch(
        BMSLibrary library,
        IEnumerable<string> installPaths,
        CancellationToken token,
        Action onEachPathProcessed,
        Action<string, int, int> onEachArchiveExtractStarted)
    {
        string[] normalizedInstallPaths = [.. (installPaths ?? [])
            .Where(path => !string.IsNullOrWhiteSpace(path))];
        if (normalizedInstallPaths.Length == 0 || token.IsCancellationRequested)
        {
            return [];
        }

        BMSLibrary.OperationDialogScope dialogScope = null;
        IDisposable activityLease = null;
        IDisposable operationGate = null;
        bool suppressionStarted = false;
        var failures = new List<ExceptionDispatchInfo>();
        IReadOnlyList<ChartPackage> packages = [];
        try
        {
            dialogScope = library.BeginOperationDialogScope();
            activityLease = chartMutationActivity.Enter();
            operationGate = chartFileOperations.Enter();
            suppressionStarted = true;
            PublishRefreshSuppressionChanged(isSuppressed: true);
            packages = mutationPort.Install(
                library,
                normalizedInstallPaths,
                token,
                onEachPathProcessed,
                onEachArchiveExtractStarted) ?? [];
        }
        catch (Exception exception)
        {
            failures.Add(ExceptionDispatchInfo.Capture(exception));
        }
        finally
        {
            if (suppressionStarted)
            {
                CaptureCleanupFailure(() => PublishRefreshSuppressionChanged(isSuppressed: false), failures);
            }
            if (operationGate != null)
            {
                CaptureCleanupFailure(operationGate.Dispose, failures);
            }
            if (activityLease != null)
            {
                CaptureCleanupFailure(activityLease.Dispose, failures);
            }
            if (dialogScope != null)
            {
                CaptureCleanupFailure(dialogScope.Dispose, failures);
                CaptureCleanupFailure(dialogScope.Flush, failures);
            }
        }

        switch (failures.Count)
        {
            case 0:
                return packages;
            case 1:
                failures[0].Throw();
                break;
            default:
                throw new AggregateException(failures.Select(failure => failure.SourceException));
        }
        return [];
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
        if (!IsCurrentGeneration(context.Generation, null, allowNullLibrary: true))
        {
            return;
        }
        DispatchNotification(() =>
        {
            if (IsCurrentGeneration(context.Generation, null, allowNullLibrary: true))
            {
                StatusChanged?.Invoke(copy);
            }
        });
    }

    private void PublishBatchFailure(QueueProcessorContext context, Exception exception)
    {
        if (context.Library == null || exception == null || !IsCurrentGeneration(context.Generation, context.Library))
        {
            return;
        }
        var failure = new PackageInstallFailure(context.Generation, context.ActiveBatch?.Paths, exception);
        DispatchNotification(() =>
        {
            if (IsCurrentGeneration(context.Generation, context.Library))
            {
                FailurePublished?.Invoke(failure);
            }
        });
    }

    private void DispatchNotification(Action notification)
    {
        if (notification == null)
        {
            return;
        }
        try
        {
            dispatchToUi(() =>
            {
                try
                {
                    notification();
                }
                catch (Exception exception)
                {
                    ReportNotificationFailure(exception);
                }
            });
        }
        catch (Exception exception)
        {
            ReportNotificationFailure(exception);
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
            if (queueProcessors[index].Processor.IsIdle)
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
}
