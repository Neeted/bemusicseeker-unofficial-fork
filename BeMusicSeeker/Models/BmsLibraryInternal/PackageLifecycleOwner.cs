using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Threading;
using BeMusicSeeker.Models;
using BeMusicSeeker.Models.LR2;
using BeMusicSeeker.Models.Utils;

namespace BeMusicSeeker.Models.BmsLibraryInternal;

/// <summary>
/// Owns the package collections and the installable lifecycle state used by the
/// library facade. Package estimation evaluation remains a capability supplied by
/// the composition root; queue, readiness, progress, and package state do not.
/// </summary>
internal sealed partial class PackageLifecycleOwner
{
    private readonly object queueStatusLock = new();

    private readonly object installableMaintenanceLock = new();

    private readonly object estimationProgressLock = new();

    private readonly object packageCollectionStateLock = new();

    private readonly SemaphoreSlim estimationExecutionGate = new(1, 1);

    private readonly PendingInstallEstimateQueueProcessor pendingEstimateQueueProcessor;

    private readonly BmsLibraryDbGateway dbGateway;

    private readonly IUiScheduler uiScheduler;

    private readonly PackageStateMutationApplier stateMutationApplier;

    private readonly StartupInstallReadinessState startupReadiness = new();

    private readonly Func<IEnumerable<ChartPackage>, ObservableCollection<ChartPackage>> packageCollectionFactory;

    private readonly Action<string> raisePropertyChanged;

    private ObservableCollection<ChartPackage> pendingPackages;

    private ObservableCollection<ChartPackage> installedPackages;

    private PendingInstallEstimateQueueStatusSnapshot pendingEstimateQueueStatus = new();

    private readonly AsyncLocal<CollectionMutationDeferral> collectionMutationDeferral = new();

    private InstallEstimationProgressSnapshot installEstimationProgress = new();

    private int pendingEstimateQueueStatusVersion;

    private int installEstimationProgressVersion;

    private long latestPendingEstimateQueueStatusSequence;

    private int installableMaintenanceRequestedVersion;

    private int installableMaintenanceCompletedVersion;

    private bool installableMaintenanceRunning;

    private long installableMaintenanceCriticalElapsedMs;

    internal PackageLifecycleOwner(
        BmsLibraryDbGateway dbGateway,
        IUiScheduler uiScheduler,
        Action<PendingInstallEstimateBatchRequest, CancellationToken> processPendingEstimateBatch,
        Action<Exception> pendingEstimateBatchFailed,
        Action<string> raisePropertyChanged,
        Func<IEnumerable<ChartPackage>, ObservableCollection<ChartPackage>> packageCollectionFactory,
        Action raiseInstalledPackagesChanged)
    {
        this.dbGateway = dbGateway ?? throw new ArgumentNullException(nameof(dbGateway));
        this.uiScheduler = uiScheduler ?? throw new ArgumentNullException(nameof(uiScheduler));
        this.raisePropertyChanged = raisePropertyChanged ?? throw new ArgumentNullException(nameof(raisePropertyChanged));
        this.packageCollectionFactory = packageCollectionFactory ?? throw new ArgumentNullException(nameof(packageCollectionFactory));
        pendingPackages = this.packageCollectionFactory([]);
        installedPackages = this.packageCollectionFactory([]);
        if (pendingPackages == null || installedPackages == null)
        {
            throw new InvalidOperationException("Package collection factory returned null.");
        }
        stateMutationApplier = new PackageStateMutationApplier(
            this.dbGateway,
            () => pendingPackages,
            SetPendingPackages,
            () => installedPackages,
            SetInstalledPackages,
            raiseInstalledPackagesChanged ?? throw new ArgumentNullException(nameof(raiseInstalledPackagesChanged)),
            packageCollectionFactory,
            this.uiScheduler,
            TryDeferCollectionMutation,
            RunPackageCollectionStateMutation);
        pendingEstimateQueueProcessor = new PendingInstallEstimateQueueProcessor(
            processPendingEstimateBatch ?? throw new ArgumentNullException(nameof(processPendingEstimateBatch)),
            UpdatePendingEstimateQueueStatus,
            pendingEstimateBatchFailed);
    }

    internal ObservableCollection<ChartPackage> PendingPackages => pendingPackages;

    internal ObservableCollection<ChartPackage> InstalledPackages => installedPackages;

    internal IDisposable BeginCollectionMutationScope()
    {
        CollectionMutationDeferral previous = collectionMutationDeferral.Value;
        if (previous?.IsCompleted == true)
        {
            previous = null;
        }
        CollectionMutationDeferral current = new();
        collectionMutationDeferral.Value = current;
        return new CollectionMutationScopeLease(this, current, previous);
    }

    internal StartupInstallReadinessState StartupReadiness => startupReadiness;

    internal int PendingEstimateQueueStatusVersion => pendingEstimateQueueStatusVersion;

    internal int InstallEstimationProgressVersion => installEstimationProgressVersion;

    internal bool IsPendingEstimateQueueIdle => pendingEstimateQueueProcessor.IsIdle;

    internal bool IsInstallableMaintenanceRunning => installableMaintenanceRunning;

    internal int InstallableMaintenanceRequestedVersion => installableMaintenanceRequestedVersion;

    internal int InstallableMaintenanceCompletedVersion => installableMaintenanceCompletedVersion;

    internal (int Version, bool ShouldStartWorker) QueueInstallableMaintenanceRequest(long criticalElapsedMs)
    {
        lock (installableMaintenanceLock)
        {
            installableMaintenanceRequestedVersion++;
            bool shouldStartWorker = !installableMaintenanceRunning;
            installableMaintenanceCriticalElapsedMs = criticalElapsedMs;
            raisePropertyChanged("InstallableMaintenanceDeferredRequestedVersion");
            if (shouldStartWorker)
            {
                SetInstallableMaintenanceRunning(true);
            }
            return (installableMaintenanceRequestedVersion, shouldStartWorker);
        }
    }

    internal (int Version, long CriticalElapsedMs) GetInstallableMaintenanceRequest()
    {
        lock (installableMaintenanceLock)
        {
            return (installableMaintenanceRequestedVersion, installableMaintenanceCriticalElapsedMs);
        }
    }

    internal int CompleteInstallableMaintenanceForShutdown()
    {
        lock (installableMaintenanceLock)
        {
            int requestVersion = installableMaintenanceRequestedVersion;
            SetInstallableMaintenanceRunning(false);
            SetInstallableMaintenanceCompletedVersion(requestVersion);
            return requestVersion;
        }
    }

    internal void MarkInstallableMaintenanceSkipped(int requestVersion)
    {
        lock (installableMaintenanceLock)
        {
            SetInstallableMaintenanceCompletedVersion(requestVersion);
            SetInstallableMaintenanceRunning(false);
        }
    }

    internal bool CompleteInstallableMaintenanceRequest(int requestVersion)
    {
        lock (installableMaintenanceLock)
        {
            SetInstallableMaintenanceCompletedVersion(requestVersion);
            if (requestVersion == installableMaintenanceRequestedVersion)
            {
                SetInstallableMaintenanceRunning(false);
                return true;
            }
        }
        return false;
    }

    internal void ApplyPendingPackageMutationDelta(PendingPackageMutationDelta delta)
    {
        stateMutationApplier.ApplyPendingPackageMutationDelta(delta);
    }

    internal void ApplyPendingPackageMutationDelta(
        PendingPackageMutationDelta delta,
        IEnumerable<ChartPackage> packagesToAdd,
        IEnumerable<ChartPackage> installRowsToUpsert)
    {
        stateMutationApplier.ApplyPendingPackageMutationDelta(delta, packagesToAdd, installRowsToUpsert);
    }

    internal BmsLibraryStateApplyResult ApplyLibraryMutationDelta(
        LibraryMutationDelta delta,
        IEnumerable<CatalogChartMutationFact> committedRemovalFacts = null,
        IEnumerable<CatalogRelocationPathFact> protectedPathFacts = null)
    {
        return stateMutationApplier.ApplyLibraryMutationDelta(delta, committedRemovalFacts, protectedPathFacts);
    }

    internal void AddPendingPackages(IEnumerable<ChartPackage> packages)
    {
        if (packages == null)
        {
            throw new ArgumentNullException(nameof(packages));
        }
        List<ChartPackage> packageList = [.. packages.Where(package => package != null)];
        bool changed;
        lock (packageCollectionStateLock)
        {
            changed = ReplacePendingPackagesCore(CreatePackageCollection(pendingPackages.Concat(packageList)));
        }
        if (changed)
        {
            RaisePendingPackagesChanged();
        }
    }

    internal void AddInstalledPackages(IEnumerable<ChartPackage> packages)
    {
        if (packages == null)
        {
            throw new ArgumentNullException(nameof(packages));
        }
        List<ChartPackage> packageList = [.. packages.Where(package => package != null)];
        bool changed;
        lock (packageCollectionStateLock)
        {
            changed = ReplaceInstalledPackagesCore(CreatePackageCollection(installedPackages.Concat(packageList)));
        }
        if (changed)
        {
            RaiseInstalledPackagesChangedProperty();
        }
    }

    internal void RemoveInstalledPackages(IEnumerable<ChartPackage> packages)
    {
        if (packages == null)
        {
            throw new ArgumentNullException(nameof(packages));
        }
        List<ChartPackage> packageList = [.. packages.Where(package => package != null)];
        bool changed;
        lock (packageCollectionStateLock)
        {
            changed = ReplaceInstalledPackagesCore(CreatePackageCollection(installedPackages.Where(package => !packageList.Contains(package))));
        }
        if (changed)
        {
            RaiseInstalledPackagesChangedProperty();
        }
    }

    internal void ClearInstalledPackages()
    {
        bool changed;
        lock (packageCollectionStateLock)
        {
            changed = ReplaceInstalledPackagesCore(CreatePackageCollection([]));
        }
        if (changed)
        {
            RaiseInstalledPackagesChangedProperty();
        }
    }

    internal void ReplacePendingPackages(IEnumerable<ChartPackage> packages)
    {
        SetPendingPackages(CreatePackageCollection(packages));
    }

    internal void ReplaceInstalledPackages(IEnumerable<ChartPackage> packages)
    {
        SetInstalledPackages(CreatePackageCollection(packages));
    }

    internal void ReplacePendingPackagesWithRegroupedPackage(
        IEnumerable<ChartPackage> sourcePackages,
        ChartPackage regroupedPackage)
    {
        List<ChartPackage> sourcePackageList = [.. (sourcePackages ?? []).Where(package => package != null)];
        if (sourcePackageList.Count == 0)
        {
            throw new ArgumentException("At least one source package is required.", nameof(sourcePackages));
        }
        if (regroupedPackage == null)
        {
            throw new ArgumentNullException(nameof(regroupedPackage));
        }

        List<ChartPackage> currentPendingPackages = [.. pendingPackages.Where(package => package != null)];
        int insertIndex = currentPendingPackages.FindIndex(package => sourcePackageList.Contains(package));
        if (insertIndex < 0)
        {
            insertIndex = currentPendingPackages.Count;
        }
        List<ChartPackage> replacedPendingPackages = [.. currentPendingPackages.Where(package => !sourcePackageList.Contains(package))];
        replacedPendingPackages.Insert(insertIndex, regroupedPackage);
        ObservableCollection<ChartPackage> nextPendingPackages = CreatePackageCollection(replacedPendingPackages);

        dbGateway.ReplaceInstallRows(sourcePackageList.Select(package => package.path), regroupedPackage);
        SetPendingPackages(nextPendingPackages);
    }

    internal void SetPendingPackages(ObservableCollection<ChartPackage> value)
    {
        if (value == null)
        {
            throw new ArgumentNullException(nameof(value));
        }
        bool changed;
        lock (packageCollectionStateLock)
        {
            changed = ReplacePendingPackagesCore(value);
        }
        if (changed)
        {
            RaisePendingPackagesChanged();
        }
    }

    internal void SetInstalledPackages(ObservableCollection<ChartPackage> value)
    {
        if (value == null)
        {
            throw new ArgumentNullException(nameof(value));
        }
        bool changed;
        lock (packageCollectionStateLock)
        {
            changed = ReplaceInstalledPackagesCore(value);
        }
        if (changed)
        {
            RaiseInstalledPackagesChangedProperty();
        }
    }

    internal InstallTableLoadResult ReloadInstallTable(
        BmsLibraryInitializationService initializationService,
        BmsLibraryDbGateway dbGateway,
        Func<ChartFile, bool> isInstalledChart)
    {
        if (initializationService == null)
        {
            throw new ArgumentNullException(nameof(initializationService));
        }
        if (dbGateway == null)
        {
            throw new ArgumentNullException(nameof(dbGateway));
        }

        InstallTableLoadResult result = initializationService.LoadInstallTable(dbGateway, isInstalledChart);
        if (result.StaleInstallPaths.Count > 0)
        {
            dbGateway.DeleteInstallRows(result.StaleInstallPaths);
        }
        SetPendingPackages(CreatePackageCollection([]));
        SetInstalledPackages(CreatePackageCollection([]));
        AddPendingPackages(result.PendingPackages);
        return result;
    }

    private void InvokeOnUi(Action mutation)
    {
        if (mutation == null)
        {
            throw new ArgumentNullException(nameof(mutation));
        }
        if (TryDeferCollectionMutation(mutation))
        {
            return;
        }
        uiScheduler.Invoke(mutation);
    }

    private bool ReplacePendingPackagesCore(ObservableCollection<ChartPackage> value)
    {
        if (ReferenceEquals(pendingPackages, value))
        {
            return false;
        }
        pendingPackages = value;
        return true;
    }

    private bool ReplaceInstalledPackagesCore(ObservableCollection<ChartPackage> value)
    {
        if (ReferenceEquals(installedPackages, value))
        {
            return false;
        }
        installedPackages = value;
        return true;
    }

    private void RaisePendingPackagesChanged()
    {
        InvokeOnUi(() => raisePropertyChanged("ChartPackagesPending"));
    }

    private void RaiseInstalledPackagesChangedProperty()
    {
        InvokeOnUi(() => raisePropertyChanged("ChartPackagesInstalled"));
    }

    private void RunPackageCollectionStateMutation(Action mutation)
    {
        if (mutation == null)
        {
            throw new ArgumentNullException(nameof(mutation));
        }
        lock (packageCollectionStateLock)
        {
            mutation();
        }
    }

    private bool TryDeferCollectionMutation(Action mutation)
    {
        CollectionMutationDeferral current = collectionMutationDeferral.Value;
        if (current == null)
        {
            return false;
        }
        return current.TryEnqueue(mutation);
    }

    private void CompleteCollectionMutationScope(
        CollectionMutationDeferral current,
        CollectionMutationDeferral previous)
    {
        if (!ReferenceEquals(collectionMutationDeferral.Value, current))
        {
            throw new InvalidOperationException("Package collection mutation scopes must complete in LIFO order.");
        }
        List<Action> mutations = current.Complete();
        collectionMutationDeferral.Value = previous;
        if (previous != null)
        {
            foreach (Action mutation in mutations)
            {
                if (!previous.TryEnqueue(mutation))
                {
                    InvokeOnUi(mutation);
                }
            }
            return;
        }
        foreach (Action mutation in mutations)
        {
            InvokeOnUi(mutation);
        }
    }

    private sealed class CollectionMutationDeferral
    {
        private readonly object syncRoot = new();

        private List<Action> mutations = [];

        private bool completed;

        internal bool IsCompleted
        {
            get
            {
                lock (syncRoot)
                {
                    return completed;
                }
            }
        }

        internal bool TryEnqueue(Action mutation)
        {
            if (mutation == null)
            {
                throw new ArgumentNullException(nameof(mutation));
            }
            lock (syncRoot)
            {
                if (completed)
                {
                    return false;
                }
                mutations.Add(mutation);
                return true;
            }
        }

        internal List<Action> Complete()
        {
            lock (syncRoot)
            {
                completed = true;
                List<Action> completedMutations = mutations;
                mutations = [];
                return completedMutations;
            }
        }
    }

    private sealed class CollectionMutationScopeLease : IDisposable
    {
        private readonly PackageLifecycleOwner owner;
        private readonly CollectionMutationDeferral current;
        private readonly CollectionMutationDeferral previous;
        private int disposed;

        internal CollectionMutationScopeLease(
            PackageLifecycleOwner owner,
            CollectionMutationDeferral current,
            CollectionMutationDeferral previous)
        {
            this.owner = owner;
            this.current = current;
            this.previous = previous;
        }

        public void Dispose()
        {
            if (Interlocked.Exchange(ref disposed, 1) == 0)
            {
                owner.CompleteCollectionMutationScope(current, previous);
            }
        }
    }

    private ObservableCollection<ChartPackage> CreatePackageCollection(IEnumerable<ChartPackage> packages)
    {
        if (packages == null)
        {
            throw new ArgumentNullException(nameof(packages));
        }
        ObservableCollection<ChartPackage> collection = packageCollectionFactory(packages);
        if (collection == null)
        {
            throw new InvalidOperationException("Package collection factory returned null.");
        }
        return collection;
    }

    internal bool TryEnqueuePendingEstimateBatch(
        PendingInstallEstimateBatchRequest request,
        Func<string, string, bool> shouldSkipForShutdown)
    {
        if (request == null || request.PackageCount == 0)
        {
            return false;
        }
        if (shouldSkipForShutdown != null
            && shouldSkipForShutdown("pending_estimate_batch", request.Source.ToString()))
        {
            return false;
        }

        pendingEstimateQueueProcessor.Enqueue(request);
        return true;
    }

    internal void CancelPendingEstimateQueue()
    {
        pendingEstimateQueueProcessor.CancelAll();
    }

    internal PendingInstallEstimateQueueStatusSnapshot GetPendingEstimateQueueStatusSnapshot()
    {
        lock (queueStatusLock)
        {
            return pendingEstimateQueueStatus?.Clone() ?? new PendingInstallEstimateQueueStatusSnapshot();
        }
    }

    internal InstallEstimationProgressSnapshot GetInstallEstimationProgressSnapshot()
    {
        lock (estimationProgressLock)
        {
            return installEstimationProgress?.Clone() ?? new InstallEstimationProgressSnapshot();
        }
    }

    internal void SetInstallEstimationProgress(
        InstallEstimationProgressSource source,
        int totalWorkCount,
        int completedWorkCount,
        string currentDisplayName)
    {
        UpdateInstallEstimationProgress(new InstallEstimationProgressSnapshot
        {
            IsActive = totalWorkCount > 0,
            Source = source,
            TotalWorkCount = Math.Max(totalWorkCount, 0),
            CompletedWorkCount = Math.Max(0, Math.Min(completedWorkCount, Math.Max(totalWorkCount, 0))),
            CurrentDisplayName = currentDisplayName ?? string.Empty
        });
    }

    internal void ClearInstallEstimationProgress()
    {
        UpdateInstallEstimationProgress(new InstallEstimationProgressSnapshot());
    }

    internal void ReportPendingEstimateBatchProgress(int completedPackageCount)
    {
        pendingEstimateQueueProcessor.ReportActiveBatchProgress(completedPackageCount);
    }

    internal void RunPendingEstimateExclusive(Action action)
    {
        estimationExecutionGate.Wait();
        try
        {
            action?.Invoke();
        }
        finally
        {
            estimationExecutionGate.Release();
        }
    }

    private void UpdatePendingEstimateQueueStatus(PendingInstallEstimateQueueStatusSnapshot snapshot)
    {
        lock (queueStatusLock)
        {
            PendingInstallEstimateQueueStatusSnapshot nextSnapshot = snapshot?.Clone() ?? new PendingInstallEstimateQueueStatusSnapshot();
            if (nextSnapshot.Sequence < latestPendingEstimateQueueStatusSequence)
            {
                return;
            }

            latestPendingEstimateQueueStatusSequence = nextSnapshot.Sequence;
            pendingEstimateQueueStatus = nextSnapshot;
            pendingEstimateQueueStatusVersion++;
            raisePropertyChanged("PendingEstimateQueueStatusVersion");
        }
    }

    private void UpdateInstallEstimationProgress(InstallEstimationProgressSnapshot snapshot)
    {
        lock (estimationProgressLock)
        {
            installEstimationProgress = snapshot?.Clone() ?? new InstallEstimationProgressSnapshot();
            installEstimationProgressVersion++;
            raisePropertyChanged("InstallEstimationProgressVersion");
        }
    }

    private void SetInstallableMaintenanceRunning(bool value)
    {
        if (installableMaintenanceRunning == value)
        {
            return;
        }
        installableMaintenanceRunning = value;
        raisePropertyChanged("InstallableMaintenanceDeferredRunning");
    }

    private void SetInstallableMaintenanceCompletedVersion(int value)
    {
        if (installableMaintenanceCompletedVersion == value)
        {
            return;
        }
        installableMaintenanceCompletedVersion = value;
        raisePropertyChanged("InstallableMaintenanceDeferredCompletedVersion");
    }
}
