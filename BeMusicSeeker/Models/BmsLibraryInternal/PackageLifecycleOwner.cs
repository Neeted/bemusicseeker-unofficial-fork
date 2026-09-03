using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
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
    /// <summary>
    /// Attempts to normalize a detached pending-package source path before publication.
    /// </summary>
    /// <param name="path">Source path supplied by discovery or regrouping.</param>
    /// <returns><see langword="true"/> when a canonical path was produced.</returns>
    internal static bool TryNormalizePendingPackagePath(string path, out string normalizedPath)
    {
        normalizedPath = null;
        if (string.IsNullOrWhiteSpace(path))
        {
            return false;
        }
        try
        {
            normalizedPath = LongPathFileSystem.TrimTrailingDirectorySeparators(
                LongPathFileSystem.NormalizePathForStorage(path.Trim()));
            return !string.IsNullOrWhiteSpace(normalizedPath);
        }
        catch (Exception exception) when (
            exception is ArgumentException
            or NotSupportedException
            or PathTooLongException
            or IOException)
        {
            normalizedPath = null;
            return false;
        }
    }

    /// <summary>
    /// Validates canonical source-path uniqueness without changing package state.
    /// </summary>
    /// <param name="pendingPackages">The complete pending collection candidate.</param>
    /// <param name="installRowsToUpsert">
    /// Optional detached install-row projections.  A projection must correspond
    /// to a candidate package path, but is not counted as another pending
    /// package when it is the same object as a candidate.
    /// </param>
    internal static void ValidatePendingPackagePathUniqueness(
        IEnumerable<ChartPackage> pendingPackages,
        IEnumerable<ChartPackage> installRowsToUpsert = null)
    {
        var canonicalPaths = new HashSet<string>(StringComparer.Ordinal);
        var seenPackages = new HashSet<ChartPackage>(ReferenceEqualityComparer.Instance);
        foreach (ChartPackage package in pendingPackages ?? [])
        {
            if (package == null)
            {
                continue;
            }
            if (!seenPackages.Add(package))
            {
                throw new InvalidOperationException(
                    "Pending package ingress was rejected because the same package instance appears more than once.");
            }

            if (!TryNormalizePendingPackagePath(package.path, out string canonicalPath))
            {
                throw new InvalidOperationException(
                    "Pending package ingress was rejected because its source path could not be normalized.");
            }
            if (!canonicalPaths.Add(canonicalPath))
            {
                throw new InvalidOperationException(
                    "Pending package ingress was rejected because the source path is already owned.");
            }
        }

        var seenInstallRowPaths = new HashSet<string>(StringComparer.Ordinal);
        foreach (ChartPackage installRow in installRowsToUpsert ?? [])
        {
            if (installRow == null)
            {
                continue;
            }
            if (!TryNormalizePendingPackagePath(installRow.path, out string canonicalPath))
            {
                throw new InvalidOperationException(
                    "Pending install-row projection was rejected because its source path could not be normalized.");
            }

            // Auto-install currently passes the pending object itself as the
            // install-row projection.  It was already validated above; do not
            // count that legitimate second view as another package.
            if (seenPackages.Contains(installRow))
            {
                continue;
            }
            if (!canonicalPaths.Contains(canonicalPath))
            {
                throw new InvalidOperationException(
                    "Pending install-row projection was rejected because it does not correspond to a candidate package.");
            }
            if (!seenInstallRowPaths.Add(canonicalPath))
            {
                throw new InvalidOperationException(
                    "Pending install-row projection was rejected because the source path is repeated.");
            }
        }
    }

    private readonly object queueStatusLock = new();

    private readonly object installableMaintenanceLock = new();

    private readonly object estimationProgressLock = new();

    private readonly object packageCollectionStateLock = new();

    private readonly SemaphoreSlim estimationExecutionGate = new(1, 1);

    // Pending filesystem mutations and pending-estimate publication must
    // reserve the same owner-owned admission.  This is intentionally a
    // fail-fast token rather than a waitable semaphore: UI operations hold it
    // while resolving and confirming their target, so a background batch can
    // report deferral instead of retaining a model or database lock.
    private readonly PendingOperationAdmission pendingOperationAdmission = new();

    private readonly PendingInstallEstimateQueueProcessor pendingEstimateQueueProcessor;

    private readonly BmsLibraryDbGateway dbGateway;

    private readonly IUiScheduler uiScheduler;

    private readonly PackageStateMutationApplier stateMutationApplier;

    private readonly StartupInstallReadinessState startupReadiness = new();

    private readonly Func<IEnumerable<ChartPackage>, ObservableCollection<ChartPackage>> packageCollectionFactory;

    private readonly Action<string> raisePropertyChanged;

    private readonly Action<Exception> collectionPublicationFailed;

    private ObservableCollection<ChartPackage> pendingPackages;

    private ReadOnlyObservableCollection<ChartPackage> pendingPackagesView;

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
        Action raiseInstalledPackagesChanged,
        Action<Exception> collectionPublicationFailed)
    {
        this.dbGateway = dbGateway ?? throw new ArgumentNullException(nameof(dbGateway));
        this.uiScheduler = uiScheduler ?? throw new ArgumentNullException(nameof(uiScheduler));
        this.raisePropertyChanged = raisePropertyChanged ?? throw new ArgumentNullException(nameof(raisePropertyChanged));
        this.collectionPublicationFailed = collectionPublicationFailed
            ?? throw new ArgumentNullException(nameof(collectionPublicationFailed));
        this.packageCollectionFactory = packageCollectionFactory ?? throw new ArgumentNullException(nameof(packageCollectionFactory));
        pendingPackages = this.packageCollectionFactory([]);
        installedPackages = this.packageCollectionFactory([]);
        if (pendingPackages == null || installedPackages == null)
        {
            throw new InvalidOperationException("Package collection factory returned null.");
        }
        pendingPackagesView = new ReadOnlyObservableCollection<ChartPackage>(pendingPackages);
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

    internal ReadOnlyObservableCollection<ChartPackage> PendingPackagesView => pendingPackagesView;

    internal bool TryEnterPendingOperation(out IDisposable lease)
    {
        return pendingOperationAdmission.TryEnter(out lease);
    }

    internal ObservableCollection<ChartPackage> InstalledPackages => installedPackages;

    internal IDisposable BeginCollectionMutationScope(bool queuePublication = false)
    {
        CollectionMutationDeferral previous = collectionMutationDeferral.Value;
        if (previous?.IsCompleted == true)
        {
            previous = null;
        }
        CollectionMutationDeferral current = new();
        collectionMutationDeferral.Value = current;
        return new CollectionMutationScopeLease(this, current, previous, queuePublication);
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
            QueuePropertyChanged("InstallableMaintenanceDeferredRequestedVersion");
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

    internal (
        PendingEstimatedInstallCollectionApplyResult Pending,
        PendingEstimatedInstallCollectionApplyResult Installed,
        long PendingApplyMs,
        long InstalledApplyMs)
        ApplyEstimatedInstallCollections(
            PendingPackageMutationDelta pendingDelta,
            int pendingRemovedCount,
            IReadOnlyCollection<ChartPackage> deferredInstalledPackages)
    {
        var pendingStopwatch = System.Diagnostics.Stopwatch.StartNew();
        int pendingCountBeforeApply = pendingPackages.Count;
        if (pendingDelta?.HasChanges == true)
        {
            ApplyPendingPackageMutationDelta(pendingDelta);
        }
        var pendingResult = new PendingEstimatedInstallCollectionApplyResult
        {
            Before = pendingCountBeforeApply,
            Changed = pendingRemovedCount,
            After = pendingPackages.Count
        };
        pendingStopwatch.Stop();

        var installedStopwatch = System.Diagnostics.Stopwatch.StartNew();
        int installedCountBeforeApply = installedPackages.Count;
        int installedAddedCount = deferredInstalledPackages?.Count ?? 0;
        if (installedAddedCount > 0)
        {
            List<ChartPackage> mergedInstalled = [.. installedPackages.Where(package => package != null)];
            var installedSet = new HashSet<ChartPackage>(mergedInstalled);
            foreach (ChartPackage installedPackage in deferredInstalledPackages)
            {
                if (installedPackage != null && installedSet.Add(installedPackage))
                {
                    mergedInstalled.Add(installedPackage);
                }
            }
            ReplaceInstalledPackages(mergedInstalled);
        }
        installedStopwatch.Stop();

        return (
            pendingResult,
            new PendingEstimatedInstallCollectionApplyResult
            {
                Before = installedCountBeforeApply,
                Changed = installedAddedCount,
                After = installedPackages.Count
            },
            pendingStopwatch.ElapsedMilliseconds,
            installedStopwatch.ElapsedMilliseconds);
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

        ValidatePendingPackagePathUniqueness(nextPendingPackages);
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
        // StalePackages identifies rows that were classified for pruning. A
        // raw path in StaleInstallPaths that is not in this set is the
        // detached primary key of an otherwise valid survivor alias.
        var classifiedStalePaths = new HashSet<string>(
            result.StalePackages
                .Where(package => package != null && !string.IsNullOrWhiteSpace(package.path))
                .Select(package => package.path),
            StringComparer.Ordinal);
        var pendingCanonicalPaths = new HashSet<string>(StringComparer.Ordinal);
        foreach (ChartPackage pendingPackage in result.PendingPackages)
        {
            if (pendingPackage != null
                && TryNormalizePendingPackagePath(pendingPackage.path, out string canonicalPath))
            {
                pendingCanonicalPaths.Add(canonicalPath);
            }
        }

        var survivorAliasCanonicalPaths = new HashSet<string>(StringComparer.Ordinal);
        foreach (string rawPath in result.StaleInstallPaths)
        {
            if (string.IsNullOrWhiteSpace(rawPath)
                || classifiedStalePaths.Contains(rawPath)
                || !TryNormalizePendingPackagePath(rawPath, out string canonicalPath)
                || string.Equals(rawPath, canonicalPath, StringComparison.Ordinal)
                || !pendingCanonicalPaths.Contains(canonicalPath))
            {
                continue;
            }
            survivorAliasCanonicalPaths.Add(canonicalPath);
        }

        var aliasTransactionPaths = new List<string>();
        foreach (string rawPath in result.StaleInstallPaths)
        {
            if (!string.IsNullOrWhiteSpace(rawPath)
                && TryNormalizePendingPackagePath(rawPath, out string canonicalPath)
                && survivorAliasCanonicalPaths.Contains(canonicalPath))
            {
                aliasTransactionPaths.Add(rawPath);
            }
        }

        if (aliasTransactionPaths.Count > 0)
        {
            List<ChartPackage> survivorPackages = [.. result.PendingPackages.Where(package =>
                package != null
                && TryNormalizePendingPackagePath(package.path, out string canonicalPath)
                && survivorAliasCanonicalPaths.Contains(canonicalPath))];
            dbGateway.ApplyInstallTableMutation(
                aliasTransactionPaths,
                survivorPackages);
        }

        List<string> staleInstallPaths = [.. result.StaleInstallPaths
            .Where(path => !aliasTransactionPaths.Contains(path, StringComparer.Ordinal))];
        if (staleInstallPaths.Count > 0)
        {
            dbGateway.DeleteInstallRows(staleInstallPaths);
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
        pendingPackagesView = new ReadOnlyObservableCollection<ChartPackage>(pendingPackages);
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
        CollectionMutationDeferral previous,
        bool queuePublication)
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
        if (!queuePublication)
        {
            foreach (Action mutation in mutations)
            {
                InvokeOnUi(mutation);
            }
            return;
        }

        try
        {
            IUiScheduledOperation operation = uiScheduler.Schedule(
                () =>
                {
                    try
                    {
                        foreach (Action mutation in mutations)
                        {
                            mutation();
                        }
                    }
                    catch (Exception exception)
                    {
                        collectionPublicationFailed(exception);
                    }
                });
            if (!operation.IsAccepted)
            {
                collectionPublicationFailed(new InvalidOperationException(
                    "Package collection publication was rejected: " + operation.RejectionReason));
                return;
            }
            _ = operation.Completion.ContinueWith(
                task =>
                {
                    if (task.IsFaulted)
                    {
                        collectionPublicationFailed(task.Exception?.GetBaseException()
                            ?? new InvalidOperationException("Package collection publication failed."));
                    }
                    else if (task.IsCanceled || operation.IsAborted)
                    {
                        collectionPublicationFailed(new OperationCanceledException(
                            "Package collection publication was canceled after scheduling."));
                    }
                },
                CancellationToken.None,
                TaskContinuationOptions.ExecuteSynchronously,
                TaskScheduler.Default);
        }
        catch (Exception exception)
        {
            collectionPublicationFailed(exception);
        }
    }

    private void QueuePropertyChanged(string propertyName)
    {
        try
        {
            IUiScheduledOperation operation = uiScheduler.Schedule(
                () =>
                {
                    try
                    {
                        raisePropertyChanged(propertyName);
                    }
                    catch (Exception exception)
                    {
                        collectionPublicationFailed(exception);
                    }
                });
            if (!operation.IsAccepted)
            {
                collectionPublicationFailed(new InvalidOperationException(
                    "Package lifecycle property publication was rejected: "
                    + operation.RejectionReason));
                return;
            }
            _ = operation.Completion.ContinueWith(
                task =>
                {
                    if (task.IsFaulted)
                    {
                        collectionPublicationFailed(task.Exception?.GetBaseException()
                            ?? new InvalidOperationException("Package lifecycle property publication failed."));
                    }
                    else if (task.IsCanceled || operation.IsAborted)
                    {
                        collectionPublicationFailed(new OperationCanceledException(
                            "Package lifecycle property publication was canceled after scheduling."));
                    }
                },
                CancellationToken.None,
                TaskContinuationOptions.ExecuteSynchronously,
                TaskScheduler.Default);
        }
        catch (Exception exception)
        {
            collectionPublicationFailed(exception);
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

        private readonly bool queuePublication;
        private int disposed;

        internal CollectionMutationScopeLease(
            PackageLifecycleOwner owner,
            CollectionMutationDeferral current,
            CollectionMutationDeferral previous,
            bool queuePublication)
        {
            this.owner = owner;
            this.current = current;
            this.previous = previous;
            this.queuePublication = queuePublication;
        }

        public void Dispose()
        {
            if (Interlocked.Exchange(ref disposed, 1) == 0)
            {
                owner.CompleteCollectionMutationScope(current, previous, queuePublication);
            }
        }
    }

    private sealed class PendingEstimateExecutionScope(SemaphoreSlim gate) : IDisposable
    {
        private SemaphoreSlim gate = gate;

        public void Dispose()
        {
            Interlocked.Exchange(ref gate, null)?.Release();
        }
    }

    private sealed class PendingOperationAdmission
    {
        private object activeToken;

        internal bool TryEnter(out IDisposable lease)
        {
            object token = new();
            if (Interlocked.CompareExchange(ref activeToken, token, null) != null)
            {
                lease = null;
                return false;
            }

            lease = new AdmissionLease(this, token);
            return true;
        }

        private void Release(object token)
        {
            Interlocked.CompareExchange(ref activeToken, null, token);
        }

        private sealed class AdmissionLease(PendingOperationAdmission owner, object token) : IDisposable
        {
            private PendingOperationAdmission owner = owner;

            private readonly object token = token;

            public void Dispose()
            {
                Interlocked.Exchange(ref owner, null)?.Release(token);
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
        Func<string, string, bool> shouldSkipForShutdown,
        Action requestAccepted = null)
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

        pendingEstimateQueueProcessor.Enqueue(request, requestAccepted);
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
        using (EnterPendingEstimateExecutionScope())
        {
            action?.Invoke();
        }
    }

    internal IDisposable EnterPendingEstimateExecutionScope()
    {
        estimationExecutionGate.Wait();
        return new PendingEstimateExecutionScope(estimationExecutionGate);
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
            QueuePropertyChanged("PendingEstimateQueueStatusVersion");
        }
    }

    private void UpdateInstallEstimationProgress(InstallEstimationProgressSnapshot snapshot)
    {
        lock (estimationProgressLock)
        {
            installEstimationProgress = snapshot?.Clone() ?? new InstallEstimationProgressSnapshot();
            installEstimationProgressVersion++;
            QueuePropertyChanged("InstallEstimationProgressVersion");
        }
    }

    private void SetInstallableMaintenanceRunning(bool value)
    {
        if (installableMaintenanceRunning == value)
        {
            return;
        }
        installableMaintenanceRunning = value;
        QueuePropertyChanged("InstallableMaintenanceDeferredRunning");
    }

    private void SetInstallableMaintenanceCompletedVersion(int value)
    {
        if (installableMaintenanceCompletedVersion == value)
        {
            return;
        }
        installableMaintenanceCompletedVersion = value;
        QueuePropertyChanged("InstallableMaintenanceDeferredCompletedVersion");
    }
}
