using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows;
using BeMusicSeeker.Models.BmsLibraryInternal;
using BeMusicSeeker.Properties;
using Ribbit.Logging;
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

    private readonly PendingEstimatedInstallMutationGate snapshotGate;

    private readonly PendingEstimatedInstallMutationGate applyGate;

    private readonly Func<EstimatedInstallDeferredFeedback, IPrimaryHashLookup> installedChartKeySnapshotProvider;

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
        EstimatedInstallDeferredFeedback,
        List<ChartPackage>> installChartPackages;

    private readonly Func<ChartPackage, string, ChartPackage> createInstalledDisplayPackage;

    private readonly Func<
        ChartPackage,
        EstimatedInstallDeferredFeedback,
        (bool Success, CleanupSourceKind SourceKind)> cleanupPendingPackageSource;

    internal PendingEstimatedInstallPreparationCapability(
        Func<BmsLibraryOptionsSnapshot> optionsProvider,
        Func<IReadOnlyCollection<ChartPackage>> pendingPackageSnapshotProvider,
        PendingEstimatedInstallMutationGate snapshotGate,
        PendingEstimatedInstallMutationGate applyGate,
        Func<EstimatedInstallDeferredFeedback, IPrimaryHashLookup> installedChartKeySnapshotProvider,
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
            EstimatedInstallDeferredFeedback,
            List<ChartPackage>> installChartPackages,
        Func<ChartPackage, string, ChartPackage> createInstalledDisplayPackage,
        Func<
            ChartPackage,
            EstimatedInstallDeferredFeedback,
            (bool Success, CleanupSourceKind SourceKind)> cleanupPendingPackageSource)
    {
        this.optionsProvider = optionsProvider ?? throw new ArgumentNullException(nameof(optionsProvider));
        this.pendingPackageSnapshotProvider = pendingPackageSnapshotProvider ?? throw new ArgumentNullException(nameof(pendingPackageSnapshotProvider));
        this.snapshotGate = snapshotGate ?? throw new ArgumentNullException(nameof(snapshotGate));
        this.applyGate = applyGate ?? throw new ArgumentNullException(nameof(applyGate));
        this.installedChartKeySnapshotProvider = installedChartKeySnapshotProvider ?? throw new ArgumentNullException(nameof(installedChartKeySnapshotProvider));
        this.countComponentMoveTargets = countComponentMoveTargets ?? throw new ArgumentNullException(nameof(countComponentMoveTargets));
        this.installChartPackages = installChartPackages ?? throw new ArgumentNullException(nameof(installChartPackages));
        this.createInstalledDisplayPackage = createInstalledDisplayPackage ?? throw new ArgumentNullException(nameof(createInstalledDisplayPackage));
        this.cleanupPendingPackageSource = cleanupPendingPackageSource ?? throw new ArgumentNullException(nameof(cleanupPendingPackageSource));
    }

    BmsLibraryOptionsSnapshot IPendingEstimatedInstallPreparationPort.CurrentOptionsSnapshot => optionsProvider();

    IReadOnlyCollection<ChartPackage> IPendingEstimatedInstallPreparationPort.PendingPackageSnapshot => pendingPackageSnapshotProvider();

    IDisposable IPendingEstimatedInstallPreparationPort.EnterSnapshotLease() => snapshotGate.Enter();

    IDisposable IPendingEstimatedInstallPreparationPort.EnterApplyLease() => applyGate.Enter();

    IPrimaryHashLookup IPendingEstimatedInstallPreparationPort.CreateInstalledChartKeySnapshotForEstimatedInstall(
        EstimatedInstallDeferredFeedback deferredFeedback)
        => installedChartKeySnapshotProvider(deferredFeedback);

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
        EstimatedInstallBatchApplyContext batchApplyContext,
        EstimatedInstallDeferredFeedback deferredFeedback)
        => installChartPackages(
            installPackages,
            destinationDirectory,
            deferredMaintenanceCharts,
            deferredInstalledPackages,
            excludedComponentPathsByPackage,
            existingHashes,
            skipInstalledPackageWhenNoBms,
            deleteSourceContentsAfterSuccessfulInstall,
            batchApplyContext,
            deferredFeedback);

    ChartPackage IPendingEstimatedInstallPreparationPort.CreateInstalledDisplayPackageForResourceOnlyMerge(
        ChartPackage originalPackage,
        string destinationDirectory)
        => createInstalledDisplayPackage(originalPackage, destinationDirectory);

    (bool Success, CleanupSourceKind SourceKind) IPendingEstimatedInstallPreparationPort.TryCleanupPendingPackageSourceForEstimatedInstall(
        ChartPackage package,
        EstimatedInstallDeferredFeedback deferredFeedback)
        => cleanupPendingPackageSource(package, deferredFeedback);
}

internal sealed class PendingEstimatedInstallCatalogCapability : IPendingEstimatedInstallCatalogPort
{
    private readonly Func<EstimatedInstallBatchApplyContext, EstimatedInstallDeferredFeedback, PendingEstimatedInstallCatalogPreparation> prepareBatchLibraryState;

    private readonly Func<
        EstimatedInstallBatchApplyContext,
        PendingEstimatedInstallCatalogPreparation,
        EstimatedInstallDeferredFeedback,
        PendingEstimatedInstallCatalogApplyReceipt> applyBatchLibraryState;

    private readonly Action<PendingEstimatedInstallCatalogApplyReceipt> completeBatchLibraryStateUnderGuard;

    private readonly Action<PendingEstimatedInstallCatalogApplyReceipt> publishBatchLibraryState;

    internal PendingEstimatedInstallCatalogCapability(
        Func<EstimatedInstallBatchApplyContext, EstimatedInstallDeferredFeedback, PendingEstimatedInstallCatalogPreparation> prepareBatchLibraryState,
        Func<
            EstimatedInstallBatchApplyContext,
            PendingEstimatedInstallCatalogPreparation,
            EstimatedInstallDeferredFeedback,
            PendingEstimatedInstallCatalogApplyReceipt> applyBatchLibraryState,
        Action<PendingEstimatedInstallCatalogApplyReceipt> completeBatchLibraryStateUnderGuard,
        Action<PendingEstimatedInstallCatalogApplyReceipt> publishBatchLibraryState)
    {
        this.prepareBatchLibraryState = prepareBatchLibraryState ?? throw new ArgumentNullException(nameof(prepareBatchLibraryState));
        this.applyBatchLibraryState = applyBatchLibraryState ?? throw new ArgumentNullException(nameof(applyBatchLibraryState));
        this.completeBatchLibraryStateUnderGuard = completeBatchLibraryStateUnderGuard ?? throw new ArgumentNullException(nameof(completeBatchLibraryStateUnderGuard));
        this.publishBatchLibraryState = publishBatchLibraryState ?? throw new ArgumentNullException(nameof(publishBatchLibraryState));
    }

    PendingEstimatedInstallCatalogPreparation IPendingEstimatedInstallCatalogPort.PrepareEstimatedInstallBatchLibraryState(
        EstimatedInstallBatchApplyContext context,
        EstimatedInstallDeferredFeedback deferredFeedback)
        => prepareBatchLibraryState(context, deferredFeedback);

    PendingEstimatedInstallCatalogApplyReceipt IPendingEstimatedInstallCatalogPort.ApplyEstimatedInstallBatchLibraryState(
        EstimatedInstallBatchApplyContext context,
        PendingEstimatedInstallCatalogPreparation preparation,
        EstimatedInstallDeferredFeedback deferredFeedback)
        => applyBatchLibraryState(context, preparation, deferredFeedback);

    void IPendingEstimatedInstallCatalogPort.CompleteEstimatedInstallBatchLibraryStateUnderGuard(
        PendingEstimatedInstallCatalogApplyReceipt receipt)
        => completeBatchLibraryStateUnderGuard(receipt);

    void IPendingEstimatedInstallCatalogPort.PublishEstimatedInstallBatchLibraryState(
        PendingEstimatedInstallCatalogApplyReceipt receipt)
        => publishBatchLibraryState(receipt);
}

internal sealed class PendingEstimatedInstallMaintenanceCapability : IPendingEstimatedInstallMaintenancePort
{
    private readonly Func<
        IEnumerable<ChartFile>,
        EstimatedInstallDeferredFeedback,
        PendingEstimatedInstallPostGuardReceipt> applyMaintenance;

    private readonly Func<
        IEnumerable<ChartFile>,
        IEnumerable<ChartFile>,
        EstimatedInstallDeferredFeedback,
        PendingEstimatedInstallPostGuardReceipt> buildInlineChartInfo;

    internal PendingEstimatedInstallMaintenanceCapability(
        Func<
            IEnumerable<ChartFile>,
            EstimatedInstallDeferredFeedback,
            PendingEstimatedInstallPostGuardReceipt> applyMaintenance,
        Func<
            IEnumerable<ChartFile>,
            IEnumerable<ChartFile>,
            EstimatedInstallDeferredFeedback,
            PendingEstimatedInstallPostGuardReceipt> buildInlineChartInfo)
    {
        this.applyMaintenance = applyMaintenance ?? throw new ArgumentNullException(nameof(applyMaintenance));
        this.buildInlineChartInfo = buildInlineChartInfo ?? throw new ArgumentNullException(nameof(buildInlineChartInfo));
    }

    PendingEstimatedInstallPostGuardReceipt IPendingEstimatedInstallMaintenancePort.ApplyEstimatedInstallMaintenance(
        IEnumerable<ChartFile> deferredMaintenanceCharts,
        EstimatedInstallDeferredFeedback deferredFeedback)
        => applyMaintenance(deferredMaintenanceCharts, deferredFeedback);

    PendingEstimatedInstallPostGuardReceipt IPendingEstimatedInstallMaintenancePort.BuildEstimatedInstallInlineChartInfo(
        IEnumerable<ChartFile> deferredMaintenanceCharts,
        IEnumerable<ChartFile> addedCharts,
        EstimatedInstallDeferredFeedback deferredFeedback)
        => buildInlineChartInfo(deferredMaintenanceCharts, addedCharts, deferredFeedback);
}

internal sealed class PendingEstimatedInstallNotificationCapability : IPendingEstimatedInstallNotificationPort
{
    private readonly Func<string, string, UiDialogButton, UiDialogIcon, UiDialogDefaultResult, UiDialogDefaultResult> showOperationDialog;

    private readonly Action<int> showCleanupOnlyCompletedWarning;

    private readonly Action<string> logPerformance;

    private readonly Action<Exception, string> logWarning;

    internal PendingEstimatedInstallNotificationCapability(
        Func<string, string, UiDialogButton, UiDialogIcon, UiDialogDefaultResult, UiDialogDefaultResult> showOperationDialog,
        Action<int> showCleanupOnlyCompletedWarning,
        Action<string> logPerformance,
        Action<Exception, string> logWarning)
    {
        this.showOperationDialog = showOperationDialog ?? throw new ArgumentNullException(nameof(showOperationDialog));
        this.showCleanupOnlyCompletedWarning = showCleanupOnlyCompletedWarning ?? throw new ArgumentNullException(nameof(showCleanupOnlyCompletedWarning));
        this.logPerformance = logPerformance ?? throw new ArgumentNullException(nameof(logPerformance));
        this.logWarning = logWarning ?? throw new ArgumentNullException(nameof(logWarning));
    }

    UiDialogDefaultResult IPendingEstimatedInstallNotificationPort.ShowOperationDialog(
        string messageBoxText,
        string caption,
        UiDialogButton button,
        UiDialogIcon icon,
        UiDialogDefaultResult defaultResult)
        => showOperationDialog(messageBoxText, caption, button, icon, defaultResult);

    void IPendingEstimatedInstallNotificationPort.ShowEstimatedCleanupOnlyCompletedWarning(int cleanupOnlySucceeded)
        => showCleanupOnlyCompletedWarning(cleanupOnlySucceeded);

    void IPendingEstimatedInstallNotificationPort.LogInstallPerformance(string message)
        => logPerformance(message);

    void IPendingEstimatedInstallNotificationPort.LogInstallWarning(Exception exception, string message)
        => logWarning(exception, message);
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
                rwlockPendingInstallCharts.GetReaderGuard,
                rwlockBMSFiles.GetReaderGuard),
            new PendingEstimatedInstallMutationGate(
                rwlockBMSFilesInitializedAll.GetReaderGuard,
                rwlockPendingInstallCharts.GetWriterGuard,
                rwlockBMSFiles.GetWriterGuard,
                rwlockSongDBInstall.GetWriterGuard),
            deferredFeedback => CreateInstalledChartKeySnapshotExcludingChartsUnsafe(
                [],
                "install_pending_estimated_filter",
                0L,
                deferredFeedback.LogInstallPerformance),
            CountComponentMoveTargetsForPackage,
            installChartPackages,
            CreateInstalledDisplayPackageForResourceOnlyMerge,
            (package, deferredFeedback) =>
            {
                bool cleanupSucceeded = TryCleanupPendingPackageSourceForEstimatedInstall(
                    package,
                    out CleanupSourceKind sourceKind,
                    deferredFeedback);
                return (cleanupSucceeded, sourceKind);
            });
    }

    private PendingEstimatedInstallCatalogCapability CreatePendingEstimatedInstallCatalogCapability()
    {
        return new PendingEstimatedInstallCatalogCapability(
            PrepareEstimatedInstallBatchLibraryState,
            ApplyEstimatedInstallBatchLibraryState,
            receipt => receipt?.CompleteUnderGuard(),
            receipt => receipt?.PublishAfterGuard());
    }

    private PendingEstimatedInstallMaintenanceCapability CreatePendingEstimatedInstallMaintenanceCapability()
    {
        return new PendingEstimatedInstallMaintenanceCapability(
            ApplyEstimatedInstallMaintenanceForDeferredDispatch,
            (deferredMaintenanceCharts, addedCharts, deferredFeedback) =>
            {
                List<ChartFile> targets = BuildEstimatedInstallMaintenanceTargets(deferredMaintenanceCharts);
                List<ChartFile> inlineTargets = BuildEstimatedInstallMaintenanceTargets(
                    targets.Concat(CreateAddedBmsonChartProjections(addedCharts)));
                return BuildEstimatedInstallInlineChartInfoForDeferredDispatch(
                    "install_package_estimated_inline",
                    inlineTargets,
                    deferredFeedback);
            });
    }

    private PendingEstimatedInstallNotificationCapability CreatePendingEstimatedInstallNotificationCapability()
    {
        return new PendingEstimatedInstallNotificationCapability(
            ShowOperationDialog,
            cleanupOnlySucceeded => ShowOperationDialog(
                string.Format(Resources.Warn_estimated_install_cleanup_only_completed, cleanupOnlySucceeded),
                Resources.MessageBoxTitle_Warning,
                MessageBoxButton.OK,
                MessageBoxImage.Exclamation,
                MessageBoxResult.OK),
            LogInstallPerformance,
            (exception, message) => NLogWrapper.FileLogger?.Warn(exception, message));
    }
}
