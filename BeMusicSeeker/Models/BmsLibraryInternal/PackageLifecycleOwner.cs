using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using BeMusicSeeker.Models.LR2;
using BeMusicSeeker.Models.Utils;
using Livet;

namespace BeMusicSeeker.Models.BmsLibraryInternal;

/// <summary>
/// Owns the package collections and the installable lifecycle state used by the
/// library facade. Package estimation evaluation remains a capability supplied by
/// the composition root; queue, readiness, progress, and package state do not.
/// </summary>
internal sealed partial class PackageLifecycleOwner
{
    private readonly object queueStatusLock = new();

    private readonly object estimationProgressLock = new();

    private readonly SemaphoreSlim estimationExecutionGate = new(1, 1);

    private readonly PendingInstallEstimateQueueProcessor pendingEstimateQueueProcessor;

    private readonly BmsLibraryDbGateway dbGateway;

    private readonly PackageStateMutationApplier stateMutationApplier;

    private readonly StartupInstallReadinessState startupReadiness = new();

    private readonly Func<IEnumerable<ChartPackage>, DispatcherCollection<ChartPackage>> packageCollectionFactory;

    private readonly Action<string> raisePropertyChanged;

    private DispatcherCollection<ChartPackage> pendingPackages;

    private DispatcherCollection<ChartPackage> installedPackages;

    private PendingInstallEstimateQueueStatusSnapshot pendingEstimateQueueStatus = new();

    private InstallEstimationProgressSnapshot installEstimationProgress = new();

    private int pendingEstimateQueueStatusVersion;

    private int installEstimationProgressVersion;

    private long latestPendingEstimateQueueStatusSequence;

    internal PackageLifecycleOwner(
        BmsLibraryDbGateway dbGateway,
        Action<PendingInstallEstimateBatchRequest, CancellationToken> processPendingEstimateBatch,
        Action<Exception> pendingEstimateBatchFailed,
        Action<string> raisePropertyChanged,
        Func<IEnumerable<ChartPackage>, DispatcherCollection<ChartPackage>> packageCollectionFactory,
        Action raiseInstalledPackagesChanged)
    {
        this.dbGateway = dbGateway ?? throw new ArgumentNullException(nameof(dbGateway));
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
            packageCollectionFactory);
        pendingEstimateQueueProcessor = new PendingInstallEstimateQueueProcessor(
            processPendingEstimateBatch ?? throw new ArgumentNullException(nameof(processPendingEstimateBatch)),
            UpdatePendingEstimateQueueStatus,
            pendingEstimateBatchFailed);
    }

    internal DispatcherCollection<ChartPackage> PendingPackages => pendingPackages;

    internal DispatcherCollection<ChartPackage> InstalledPackages => installedPackages;

    internal StartupInstallReadinessState StartupReadiness => startupReadiness;

    internal int PendingEstimateQueueStatusVersion => pendingEstimateQueueStatusVersion;

    internal int InstallEstimationProgressVersion => installEstimationProgressVersion;

    internal bool IsPendingEstimateQueueIdle => pendingEstimateQueueProcessor.IsIdle;

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
        foreach (ChartPackage package in packages)
        {
            if (package != null)
            {
                pendingPackages.Add(package);
            }
        }
    }

    internal void AddInstalledPackages(IEnumerable<ChartPackage> packages)
    {
        if (packages == null)
        {
            throw new ArgumentNullException(nameof(packages));
        }
        foreach (ChartPackage package in packages)
        {
            if (package != null)
            {
                installedPackages.Add(package);
            }
        }
    }

    internal void RemoveInstalledPackages(IEnumerable<ChartPackage> packages)
    {
        if (packages == null)
        {
            throw new ArgumentNullException(nameof(packages));
        }
        installedPackages.Remove(packages);
    }

    internal void ClearInstalledPackages()
    {
        installedPackages.Clear();
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
        DispatcherCollection<ChartPackage> nextPendingPackages = CreatePackageCollection(replacedPendingPackages);

        dbGateway.ReplaceInstallRows(sourcePackageList.Select(package => package.path), regroupedPackage);
        SetPendingPackages(nextPendingPackages);
    }

    internal void SetPendingPackages(DispatcherCollection<ChartPackage> value)
    {
        if (value == null)
        {
            throw new ArgumentNullException(nameof(value));
        }
        if (ReferenceEquals(pendingPackages, value))
        {
            return;
        }

        pendingPackages = value;
        raisePropertyChanged("ChartPackagesPending");
    }

    internal void SetInstalledPackages(DispatcherCollection<ChartPackage> value)
    {
        if (value == null)
        {
            throw new ArgumentNullException(nameof(value));
        }
        if (ReferenceEquals(installedPackages, value))
        {
            return;
        }

        installedPackages = value;
        raisePropertyChanged("ChartPackagesInstalled");
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
        pendingPackages.Clear();
        installedPackages.Clear();
        AddPendingPackages(result.PendingPackages);
        return result;
    }

    private DispatcherCollection<ChartPackage> CreatePackageCollection(IEnumerable<ChartPackage> packages)
    {
        if (packages == null)
        {
            throw new ArgumentNullException(nameof(packages));
        }
        DispatcherCollection<ChartPackage> collection = packageCollectionFactory(packages);
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
}
