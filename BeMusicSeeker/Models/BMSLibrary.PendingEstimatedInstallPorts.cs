using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows;
using BeMusicSeeker.Models.BmsLibraryInternal;
using BeMusicSeeker.Properties;
using MessageBoxButton = BeMusicSeeker.Models.UiDialogButton;
using MessageBoxImage = BeMusicSeeker.Models.UiDialogIcon;
using MessageBoxResult = BeMusicSeeker.Models.UiDialogDefaultResult;

namespace BeMusicSeeker.Models;

/// <summary>
/// Composition-only capabilities for the pending estimated-install workflow.
/// Each capability has one responsibility and deliberately does not retain a
/// reference to <see cref="BMSLibrary"/>.
/// </summary>
internal sealed class PendingEstimatedInstallPreparationCapability : IPendingEstimatedInstallPreparationPort
{
    private readonly Func<BmsLibraryOptionsSnapshot> optionsProvider;

    private readonly Func<IReadOnlyCollection<ChartPackage>> pendingPackageSnapshotProvider;

    private readonly PendingEstimatedInstallMutationGate mutationGate;

    private readonly Func<IPrimaryHashLookup> installedChartKeySnapshotProvider;

    private readonly Func<ChartPackage, string, ISet<string>, int> countComponentMoveTargets;

    private readonly Func<
        IEnumerable<ChartPackage>,
        string,
        List<ChartFile>,
        List<ChartPackage>,
        Dictionary<ChartPackage, HashSet<string>>,
        IPrimaryHashLookup,
        bool,
        bool,
        EstimatedInstallBatchApplyContext,
        List<ChartPackage>> installChartPackages;

    private readonly Func<ChartPackage, string, ChartPackage> createInstalledDisplayPackage;

    private readonly Func<ChartPackage, (bool Success, CleanupSourceKind SourceKind)> cleanupPendingPackageSource;

    internal PendingEstimatedInstallPreparationCapability(
        Func<BmsLibraryOptionsSnapshot> optionsProvider,
        Func<IReadOnlyCollection<ChartPackage>> pendingPackageSnapshotProvider,
        PendingEstimatedInstallMutationGate mutationGate,
        Func<IPrimaryHashLookup> installedChartKeySnapshotProvider,
        Func<ChartPackage, string, ISet<string>, int> countComponentMoveTargets,
        Func<
            IEnumerable<ChartPackage>,
            string,
            List<ChartFile>,
            List<ChartPackage>,
            Dictionary<ChartPackage, HashSet<string>>,
            IPrimaryHashLookup,
            bool,
            bool,
            EstimatedInstallBatchApplyContext,
            List<ChartPackage>> installChartPackages,
        Func<ChartPackage, string, ChartPackage> createInstalledDisplayPackage,
        Func<ChartPackage, (bool Success, CleanupSourceKind SourceKind)> cleanupPendingPackageSource)
    {
        this.optionsProvider = optionsProvider ?? throw new ArgumentNullException(nameof(optionsProvider));
        this.pendingPackageSnapshotProvider = pendingPackageSnapshotProvider ?? throw new ArgumentNullException(nameof(pendingPackageSnapshotProvider));
        this.mutationGate = mutationGate ?? throw new ArgumentNullException(nameof(mutationGate));
        this.installedChartKeySnapshotProvider = installedChartKeySnapshotProvider ?? throw new ArgumentNullException(nameof(installedChartKeySnapshotProvider));
        this.countComponentMoveTargets = countComponentMoveTargets ?? throw new ArgumentNullException(nameof(countComponentMoveTargets));
        this.installChartPackages = installChartPackages ?? throw new ArgumentNullException(nameof(installChartPackages));
        this.createInstalledDisplayPackage = createInstalledDisplayPackage ?? throw new ArgumentNullException(nameof(createInstalledDisplayPackage));
        this.cleanupPendingPackageSource = cleanupPendingPackageSource ?? throw new ArgumentNullException(nameof(cleanupPendingPackageSource));
    }

    BmsLibraryOptionsSnapshot IPendingEstimatedInstallPreparationPort.CurrentOptionsSnapshot => optionsProvider();

    IReadOnlyCollection<ChartPackage> IPendingEstimatedInstallPreparationPort.PendingPackageSnapshot => pendingPackageSnapshotProvider();

    IDisposable IPendingEstimatedInstallPreparationPort.EnterMutationLease() => mutationGate.Enter();

    IPrimaryHashLookup IPendingEstimatedInstallPreparationPort.CreateInstalledChartKeySnapshotForEstimatedInstall()
        => installedChartKeySnapshotProvider();

    int IPendingEstimatedInstallPreparationPort.CountComponentMoveTargetsForPackage(
        ChartPackage package,
        string destinationDirectory,
        ISet<string> excludedComponentPaths)
        => countComponentMoveTargets(package, destinationDirectory, excludedComponentPaths);

    List<ChartPackage> IPendingEstimatedInstallPreparationPort.InstallChartPackagesForEstimatedInstall(
        IEnumerable<ChartPackage> installPackages,
        string destinationDirectory,
        List<ChartFile> deferredMaintenanceCharts,
        List<ChartPackage> deferredInstalledPackages,
        Dictionary<ChartPackage, HashSet<string>> excludedComponentPathsByPackage,
        IPrimaryHashLookup existingHashes,
        bool skipInstalledPackageWhenNoBms,
        bool deleteSourceContentsAfterSuccessfulInstall,
        EstimatedInstallBatchApplyContext batchApplyContext)
        => installChartPackages(
            installPackages,
            destinationDirectory,
            deferredMaintenanceCharts,
            deferredInstalledPackages,
            excludedComponentPathsByPackage,
            existingHashes,
            skipInstalledPackageWhenNoBms,
            deleteSourceContentsAfterSuccessfulInstall,
            batchApplyContext);

    ChartPackage IPendingEstimatedInstallPreparationPort.CreateInstalledDisplayPackageForResourceOnlyMerge(
        ChartPackage originalPackage,
        string destinationDirectory)
        => createInstalledDisplayPackage(originalPackage, destinationDirectory);

    (bool Success, CleanupSourceKind SourceKind) IPendingEstimatedInstallPreparationPort.TryCleanupPendingPackageSourceForEstimatedInstall(
        ChartPackage package)
        => cleanupPendingPackageSource(package);
}

internal sealed class PendingEstimatedInstallCatalogCapability : IPendingEstimatedInstallCatalogPort
{
    private readonly Action<EstimatedInstallBatchApplyContext> applyBatchLibraryState;

    internal PendingEstimatedInstallCatalogCapability(Action<EstimatedInstallBatchApplyContext> applyBatchLibraryState)
    {
        this.applyBatchLibraryState = applyBatchLibraryState ?? throw new ArgumentNullException(nameof(applyBatchLibraryState));
    }

    void IPendingEstimatedInstallCatalogPort.ApplyEstimatedInstallBatchLibraryState(EstimatedInstallBatchApplyContext context)
        => applyBatchLibraryState(context);
}

internal sealed class PendingEstimatedInstallMaintenanceCapability : IPendingEstimatedInstallMaintenancePort
{
    private readonly Func<IEnumerable<ChartFile>, int> applyMaintenance;

    private readonly Action<IEnumerable<ChartFile>, IEnumerable<ChartFile>> buildInlineChartInfo;

    internal PendingEstimatedInstallMaintenanceCapability(
        Func<IEnumerable<ChartFile>, int> applyMaintenance,
        Action<IEnumerable<ChartFile>, IEnumerable<ChartFile>> buildInlineChartInfo)
    {
        this.applyMaintenance = applyMaintenance ?? throw new ArgumentNullException(nameof(applyMaintenance));
        this.buildInlineChartInfo = buildInlineChartInfo ?? throw new ArgumentNullException(nameof(buildInlineChartInfo));
    }

    int IPendingEstimatedInstallMaintenancePort.ApplyEstimatedInstallMaintenance(IEnumerable<ChartFile> deferredMaintenanceCharts)
        => applyMaintenance(deferredMaintenanceCharts);

    void IPendingEstimatedInstallMaintenancePort.BuildEstimatedInstallInlineChartInfo(
        IEnumerable<ChartFile> deferredMaintenanceCharts,
        IEnumerable<ChartFile> addedCharts)
        => buildInlineChartInfo(deferredMaintenanceCharts, addedCharts);
}

internal sealed class PendingEstimatedInstallNotificationCapability : IPendingEstimatedInstallNotificationPort
{
    private readonly Action<int> showCleanupOnlyCompletedWarning;

    private readonly Action<string> logPerformance;

    internal PendingEstimatedInstallNotificationCapability(
        Action<int> showCleanupOnlyCompletedWarning,
        Action<string> logPerformance)
    {
        this.showCleanupOnlyCompletedWarning = showCleanupOnlyCompletedWarning ?? throw new ArgumentNullException(nameof(showCleanupOnlyCompletedWarning));
        this.logPerformance = logPerformance ?? throw new ArgumentNullException(nameof(logPerformance));
    }

    void IPendingEstimatedInstallNotificationPort.ShowEstimatedCleanupOnlyCompletedWarning(int cleanupOnlySucceeded)
        => showCleanupOnlyCompletedWarning(cleanupOnlySucceeded);

    void IPendingEstimatedInstallNotificationPort.LogInstallPerformance(string message)
        => logPerformance(message);
}

public partial class BMSLibrary
{
    private PendingEstimatedInstallPreparationCapability CreatePendingEstimatedInstallPreparationCapability()
    {
        return new PendingEstimatedInstallPreparationCapability(
            () => CurrentOptionsSnapshot,
            () => [.. ChartPackagesPending.Where(package => package != null)],
            new PendingEstimatedInstallMutationGate(
                rwlockBMSFilesInitializedAll.GetReaderGuard,
                rwlockPendingInstallCharts.GetWriterGuard,
                rwlockBMSFiles.GetWriterGuard,
                rwlockSongDBInstall.GetWriterGuard),
            () => CreateInstalledChartKeySnapshotExcludingChartsUnsafe([], "install_pending_estimated_filter", 0L),
            CountComponentMoveTargetsForPackage,
            installChartPackages,
            CreateInstalledDisplayPackageForResourceOnlyMerge,
            package =>
            {
                bool cleanupSucceeded = TryCleanupPendingPackageSourceForEstimatedInstall(package, out CleanupSourceKind sourceKind);
                return (cleanupSucceeded, sourceKind);
            });
    }

    private PendingEstimatedInstallCatalogCapability CreatePendingEstimatedInstallCatalogCapability()
    {
        return new PendingEstimatedInstallCatalogCapability(
            context =>
            {
                ApplyEstimatedInstallBatchLibraryState(context);
            });
    }

    private PendingEstimatedInstallMaintenanceCapability CreatePendingEstimatedInstallMaintenanceCapability()
    {
        return new PendingEstimatedInstallMaintenanceCapability(
            deferredMaintenanceCharts =>
            {
                List<ChartFile> targets = BuildEstimatedInstallMaintenanceTargets(deferredMaintenanceCharts);
                if (targets.Count > 0)
                {
                    ApplyCatalogMaintenance(
                        targets,
                        forceUpdate: true,
                        resourceHealthIndexUpdateMode: ResourceHealthIndexUpdateMode.DeltaOnUpdates,
                        resourceHealthMutationReason: "install_package_estimated");
                }
                return targets.Count;
            },
            (deferredMaintenanceCharts, addedCharts) =>
            {
                List<ChartFile> targets = BuildEstimatedInstallMaintenanceTargets(deferredMaintenanceCharts);
                List<ChartFile> inlineTargets = BuildEstimatedInstallMaintenanceTargets(
                    targets.Concat(CreateAddedBmsonChartProjections(addedCharts)));
                BuildAndPersistInlineChartInfoForInstalledCharts(
                    "install_package_estimated_inline",
                    inlineTargets);
            });
    }

    private PendingEstimatedInstallNotificationCapability CreatePendingEstimatedInstallNotificationCapability()
    {
        return new PendingEstimatedInstallNotificationCapability(
            cleanupOnlySucceeded => ShowOperationDialog(
                string.Format(Resources.Warn_estimated_install_cleanup_only_completed, cleanupOnlySucceeded),
                Resources.MessageBoxTitle_Warning,
                MessageBoxButton.OK,
                MessageBoxImage.Exclamation,
                MessageBoxResult.OK),
            LogInstallPerformance);
    }
}
