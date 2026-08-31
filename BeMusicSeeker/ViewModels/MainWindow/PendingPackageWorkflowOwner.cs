using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.ExceptionServices;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using BeMusicSeeker.Models;
using BeMusicSeeker.Models.BmsLibraryInternal;
using BeMusicSeeker.Models.Utils;
using BeMusicSeeker.Views.Dialogs;
using MessageBoxButton = BeMusicSeeker.Models.UiDialogButton;
using MessageBoxImage = BeMusicSeeker.Models.UiDialogIcon;
using MessageBoxResult = BeMusicSeeker.Models.UiDialogDefaultResult;

namespace BeMusicSeeker.ViewModels;

internal enum PendingPackageRefreshScope
{
    DestinationState,
    PackageMutation
}

internal abstract class PendingPackageWorkflowChangedEventArgs : EventArgs
{
}

internal sealed class PendingPackageRefreshSuppressionChangedEventArgs : PendingPackageWorkflowChangedEventArgs
{
    internal PendingPackageRefreshSuppressionChangedEventArgs(
        bool isSuppressed,
        PendingPackageRefreshScope? scope)
    {
        IsSuppressed = isSuppressed;
        Scope = scope;
    }

    internal bool IsSuppressed { get; }

    internal PendingPackageRefreshScope? Scope { get; }
}

internal sealed class PendingPackageMutationAppliedEventArgs : PendingPackageWorkflowChangedEventArgs
{
    internal PendingPackageMutationAppliedEventArgs(
        IEnumerable<ChartFile> changedCharts = null,
        bool installDestinationStateChanged = false,
        bool identitySortKeyChanged = false,
        bool displayStateChanged = false)
    {
        ChangedCharts = Array.AsReadOnly(changedCharts?.ToArray() ?? Array.Empty<ChartFile>());
        InstallDestinationStateChanged = installDestinationStateChanged;
        IdentitySortKeyChanged = identitySortKeyChanged;
        DisplayStateChanged = displayStateChanged;
    }

    internal IReadOnlyList<ChartFile> ChangedCharts { get; }

    internal bool InstallDestinationStateChanged { get; }

    internal bool IdentitySortKeyChanged { get; }

    internal bool DisplayStateChanged { get; }
}

internal sealed class PendingPackageMutationResult
{
    private PendingPackageMutationResult(
        bool succeeded,
        Exception failure,
        bool shouldApplyView,
        PackageCatalogSection? emptySection,
        FileDbMutationBatchReceipt mutationReceipt = null)
    {
        Succeeded = succeeded;
        Failure = failure;
        ShouldApplyView = shouldApplyView;
        EmptySection = emptySection;
        MutationReceipt = mutationReceipt;
    }

    internal bool Succeeded { get; }

    internal Exception Failure { get; }

    internal bool ShouldApplyView { get; }

    internal PackageCatalogSection? EmptySection { get; }

    internal FileDbMutationBatchReceipt MutationReceipt { get; }

    internal bool HasDurableCommit => MutationReceipt?.HasDurableCommit == true;

    internal bool ManualRecoveryRequired => MutationReceipt?.ManualRecoveryRequired == true;

    internal bool CompletedWithCleanupFailure => MutationReceipt?.CompletedWithCleanupFailure == true;

    internal IReadOnlyList<string> RecoveryPaths => MutationReceipt?.RecoveryPaths ?? [];

    internal static PendingPackageMutationResult Completed { get; } = new(true, null, true, null);

    internal static PendingPackageMutationResult Rejected { get; } = new(false, null, false, null);

    internal static PendingPackageMutationResult CompletedFor(
        PackageCatalogSection? emptySection,
        FileDbMutationBatchReceipt mutationReceipt = null)
    {
        return new PendingPackageMutationResult(true, null, true, emptySection, mutationReceipt);
    }

    internal static PendingPackageMutationResult FailedBeforeMutation(Exception failure)
    {
        return new PendingPackageMutationResult(
            false,
            failure ?? throw new ArgumentNullException(nameof(failure)),
            false,
            null);
    }

    internal static PendingPackageMutationResult FailedAfterMutation(
        Exception failure,
        PackageCatalogSection? emptySection = null,
        FileDbMutationBatchReceipt mutationReceipt = null)
    {
        return new PendingPackageMutationResult(
            false,
            failure ?? throw new ArgumentNullException(nameof(failure)),
            true,
            emptySection,
            mutationReceipt);
    }

    internal static PendingPackageMutationResult FromTerminal(
        FileDbMutationBatchReceipt mutationReceipt,
        PackageCatalogSection? emptySection = null)
    {
        if (mutationReceipt?.ManualRecoveryRequired == true)
        {
            return new PendingPackageMutationResult(
                false,
                null,
                true,
                emptySection,
                mutationReceipt);
        }
        return CompletedFor(emptySection, mutationReceipt);
    }
}

internal interface IPendingPackageMutationPlaybackPort
{

    void StopIfPlayingCharts(IReadOnlyList<ChartFile> charts);
}

internal interface IPendingPackageStore
{
    void SearchPackages(
        BMSLibrary library,
        PendingInstallDestinationSearchKind kind,
        IReadOnlyList<ChartPackage> packages);

    IReadOnlyList<ChartFile> SearchPending(
        BMSLibrary library,
        PendingInstallDestinationSearchRequest request);

    IReadOnlyList<ChartFile> ClearPackages(IReadOnlyList<ChartPackage> packages);

    IReadOnlyList<ChartFile> ClearPending(
        BMSLibrary library,
        PendingInstallDestinationClearRequest request);

    IReadOnlyList<ChartFile> SearchCorrect(
        BMSLibrary library,
        RepairInstalledLocationRequest request);

    IReadOnlyList<ChartFile> ClearCorrect(
        BMSLibrary library,
        RepairInstalledLocationRequest request);

    ChartFile SetPending(
        BMSLibrary library,
        PendingInstallDestinationEditRequest request,
        string destinationDirectory);

    IReadOnlyList<ChartPackage> ResolvePendingPackages(
        BMSLibrary library,
        IReadOnlyList<ChartOperationTarget> targets);

    void ForceInstallPackages(
        BMSLibrary library,
        IReadOnlyList<ChartPackage> packages,
        ISet<ChartPackage> approvedNormalInstallOverridePackages);

    void ManualInstallPackages(
        BMSLibrary library,
        IReadOnlyList<ChartPackage> packages);

    bool IsPendingSectionEmpty(BMSLibrary library);

    IReadOnlyList<BMSLibrary.DuplicateInstallRepairConfirmation> GetDuplicateInstallRepairConfirmations(
        BMSLibrary library,
        IReadOnlyList<ChartFile> repairCharts);

    void FixInstalledLocations(
        BMSLibrary library,
        IReadOnlyList<ChartFile> repairCharts,
        IReadOnlyList<string> approvedDuplicateRemovalChartPaths);

    IReadOnlyList<ChartPackage> GetInstalledOnlyPendingPackages(BMSLibrary library);

    IReadOnlyList<ChartFile> GetPendingBmsFormatCharts(BMSLibrary library);

    void DeletePendingPackageSources(
        BMSLibrary library,
        IReadOnlyList<ChartPackage> packages,
        CancellationToken cancellationToken,
        Action onEachProcessed);

    void RenamePendingZeroNoteCharts(
        BMSLibrary library,
        IReadOnlyList<ChartFile> charts,
        CancellationToken cancellationToken,
        Action onEachProcessed);

    PendingInstalledOnlyResourceOverwriteResult OverwriteInstalledOnlyPendingPackageResources(
        BMSLibrary library,
        IReadOnlyList<ChartPackage> packages,
        CancellationToken cancellationToken,
        Action onEachProcessed);
}

internal interface IPendingPackageTerminalMutationStore
{
    FileDbMutationBatchReceipt ForceInstallPackagesWithReceipt(
        BMSLibrary library,
        IReadOnlyList<ChartPackage> packages,
        ISet<ChartPackage> approvedNormalInstallOverridePackages);

    PendingInstallBatchResult ManualInstallPackagesWithReceipt(
        BMSLibrary library,
        IReadOnlyList<ChartPackage> packages);
}

internal sealed class PendingPackageWorkflowOwner
{
    private readonly Func<BMSLibrary> libraryProvider;
    private readonly ChartFileOperationSynchronizer chartFileOperations;
    private readonly ChartMutationActivityOwner chartMutationActivity;
    private readonly IPendingPackageMutationPlaybackPort playback;
    private readonly IUiDialogService dialogs;
    private readonly IPendingPackageStore store;
    private readonly Func<InstallDestinationWorkflowSettingsSnapshot> settingsProvider;
    private readonly IExternalShellGateway externalShellGateway;

    internal event EventHandler<PendingPackageWorkflowChangedEventArgs> WorkflowChanged;

    internal PendingPackageWorkflowOwner(
        Func<BMSLibrary> libraryProvider,
        ChartFileOperationSynchronizer chartFileOperations,
        ChartMutationActivityOwner chartMutationActivity,
        IPendingPackageMutationPlaybackPort playback,
        IUiDialogService dialogs,
        Func<InstallDestinationWorkflowSettingsSnapshot> settingsProvider,
        IExternalShellGateway externalShellGateway,
        IPendingPackageStore store = null)
    {
        this.libraryProvider = libraryProvider ?? throw new ArgumentNullException(nameof(libraryProvider));
        this.chartFileOperations = chartFileOperations ?? throw new ArgumentNullException(nameof(chartFileOperations));
        this.chartMutationActivity = chartMutationActivity ?? throw new ArgumentNullException(nameof(chartMutationActivity));
        this.playback = playback ?? throw new ArgumentNullException(nameof(playback));
        this.dialogs = dialogs ?? throw new ArgumentNullException(nameof(dialogs));
        this.settingsProvider = settingsProvider ?? throw new ArgumentNullException(nameof(settingsProvider));
        this.store = store ?? new BmsLibraryPendingPackageStore();
        this.externalShellGateway = externalShellGateway
            ?? throw new ArgumentNullException(nameof(externalShellGateway));
    }

    internal bool CanOpenInstallDestination(
        ChartOperationTarget rowTarget,
        IEnumerable<ChartOperationTarget> selectedTargets,
        bool isPendingSection,
        bool isPlaylistRow)
    {
        if (!isPendingSection || isPlaylistRow || rowTarget?.IsPlaylistMissing == true)
        {
            return false;
        }

        IReadOnlyList<ChartOperationTarget> targets = [.. (selectedTargets ?? [])];
        if (targets.Count == 0 && rowTarget != null)
        {
            targets = [rowTarget];
        }
        return targets.Any(target => target?.HasCapability(ChartOperationCapabilities.UpdateInstallDestination) == true);
    }

    internal async Task OpenInstallDestinationForChartsAsync(
        IEnumerable<ChartOperationTarget> targets)
    {
        if (targets == null)
        {
            throw new ArgumentNullException(nameof(targets));
        }
        IReadOnlyList<ChartOperationTarget> targetSnapshot = [.. targets];
        if (targetSnapshot.Count == 0)
        {
            return;
        }
        if (targetSnapshot.Count > 1)
        {
            await ShowMessageAsync(
                BeMusicSeeker.Properties.Resources.Msg_open_install_destination_multiple_selected,
                BeMusicSeeker.Properties.Resources.Information,
                MessageBoxImage.Information,
                "Open install destination multiple selection notice");
        }
        if (!TryResolveInstallDestination(
            targetSnapshot[0]?.Chart,
            out string installDirectory,
            out string reason))
        {
            await ShowMessageAsync(
                reason,
                BeMusicSeeker.Properties.Resources.Warning,
                MessageBoxImage.Exclamation,
                "Open install destination warning");
            return;
        }
        OpenInstallDestination(installDirectory);
    }

    internal async Task OpenInstallDestinationForPackageAsync(ChartPackage package)
    {
        if (package == null)
        {
            throw new ArgumentNullException(nameof(package));
        }
        string reason = null;
        IReadOnlyList<PackageChartEntry> entrySnapshot = [.. package.ChartEntries ?? []];
        foreach (PackageChartEntry entry in entrySnapshot)
        {
            if (TryResolveInstallDestination(entry?.Chart, out string installDirectory, out reason))
            {
                OpenInstallDestination(installDirectory);
                return;
            }
        }
        await ShowMessageAsync(
            string.IsNullOrWhiteSpace(reason)
                ? BeMusicSeeker.Properties.Resources.Msg_open_install_destination_missing
                : reason,
            BeMusicSeeker.Properties.Resources.Warning,
            MessageBoxImage.Exclamation,
            "Open install destination warning");
    }

    internal void OpenPackageSourceInExplorer(ChartPackage package)
    {
        if (package == null)
        {
            return;
        }
        string packagePath = package.path;
        if (LongPathFileSystem.DirectoryExists(packagePath))
        {
            _ = externalShellGateway.OpenDirectory(packagePath);
            return;
        }
        if (!LongPathFileSystem.FileExists(packagePath))
        {
            return;
        }
        _ = externalShellGateway.OpenFileAndSelect(packagePath);
    }

    internal Task SearchPackagesAsync(
        PendingInstallDestinationSearchKind kind,
        IEnumerable<ChartPackage> packages)
    {
        ValidateKind(kind);
        if (packages == null)
        {
            throw new ArgumentNullException(nameof(packages));
        }
        IReadOnlyList<ChartPackage> packageSnapshot = [.. packages.Where(package => package != null)];
        return RunSearchAsync(kind, () =>
        {
            Execute(library => store.SearchPackages(library, kind, packageSnapshot));
            PublishMutationApplied(
                installDestinationStateChanged: true,
                identitySortKeyChanged: true);
        });
    }

    internal Task SearchPendingAsync(PendingInstallDestinationSearchRequest request)
    {
        if (request == null)
        {
            throw new ArgumentNullException(nameof(request));
        }
        ValidateKind(request.Kind);
        return RunSearchAsync(request.Kind, () =>
        {
            Execute(library =>
            {
                IReadOnlyList<ChartFile> changedCharts = store.SearchPending(library, request);
                PublishChangedCharts(changedCharts);
            });
            PublishMutationApplied(
                installDestinationStateChanged: true,
                identitySortKeyChanged: true);
        });
    }

    internal Task ClearPackagesAsync(IEnumerable<ChartPackage> packages)
    {
        if (packages == null)
        {
            throw new ArgumentNullException(nameof(packages));
        }
        IReadOnlyList<ChartPackage> packageSnapshot = [.. packages.Where(package => package != null)];
        return Task.Run(() =>
        {
            Execute(library =>
            {
                IReadOnlyList<ChartFile> changedCharts = store.ClearPackages(packageSnapshot);
                PublishChangedCharts(changedCharts);
            }, requiresLibrary: false);
            PublishMutationApplied(installDestinationStateChanged: true);
        });
    }

    internal Task ClearPendingAsync(PendingInstallDestinationClearRequest request)
    {
        if (request == null)
        {
            throw new ArgumentNullException(nameof(request));
        }
        request.MaterializeLooseEntries();
        return Task.Run(() =>
        {
            if (Execute(library =>
            {
                IReadOnlyList<ChartFile> changedCharts = store.ClearPending(library, request);
                PublishChangedCharts(changedCharts);
            }))
            {
                PublishMutationApplied(installDestinationStateChanged: true);
            }
        });
    }

    internal Task SearchCorrectAsync(RepairInstalledLocationRequest request)
    {
        if (request == null)
        {
            throw new ArgumentNullException(nameof(request));
        }
        request.MaterializeRepairEntries();
        return Task.Run(() => Execute(library =>
        {
            IReadOnlyList<ChartFile> changedCharts = store.SearchCorrect(library, request);
            PublishChangedCharts(changedCharts);
            PublishMutationApplied(installDestinationStateChanged: true);
        }));
    }

    internal Task ClearCorrectAsync(RepairInstalledLocationRequest request)
    {
        if (request == null)
        {
            throw new ArgumentNullException(nameof(request));
        }
        request.MaterializeRepairEntries();
        return Task.Run(() =>
        {
            if (Execute(library =>
            {
                IReadOnlyList<ChartFile> changedCharts = store.ClearCorrect(library, request);
                PublishChangedCharts(changedCharts);
            }))
            {
                PublishMutationApplied(installDestinationStateChanged: true);
            }
        });
    }

    internal Task SetPendingAsync(
        PendingInstallDestinationEditRequest request,
        string destinationDirectory)
    {
        if (request == null)
        {
            throw new ArgumentNullException(nameof(request));
        }
        return Task.Run(() =>
        {
            ChartFile changedChart = null;
            Execute(library => changedChart = store.SetPending(library, request, destinationDirectory));
            if (changedChart != null)
            {
                PublishChangedCharts([changedChart]);
            }
            PublishMutationApplied(
                installDestinationStateChanged: changedChart != null,
                displayStateChanged: true);
        });
    }

    internal Task<PendingPackageMutationResult> ForceInstallPackagesAsync(
        IEnumerable<ChartPackage> packages)
    {
        return InstallPackagesAsync(
            PendingInstallPackageOperationKind.ForceInstall,
            MaterializePackages(packages));
    }

    internal Task<PendingPackageMutationResult> ManualInstallPackagesAsync(
        IEnumerable<ChartPackage> packages)
    {
        return InstallPackagesAsync(
            PendingInstallPackageOperationKind.ManualInstall,
            MaterializePackages(packages));
    }

    internal async Task<PendingPackageMutationResult> InstallPendingAsync(
        PendingInstallPackageOperationRequest request)
    {
        if (request == null)
        {
            throw new ArgumentNullException(nameof(request));
        }
        try
        {
            if (request.IsManualInstall && !await ConfirmManualInstallAsync())
            {
                return PendingPackageMutationResult.Rejected;
            }
            IReadOnlyList<ChartPackage> packages = await Task.Run(() =>
                Read(library => store.ResolvePendingPackages(library, request.Targets))) ?? [];
            return await InstallResolvedPackagesAsync(request.Kind, packages);
        }
        catch (Exception exception)
        {
            return PendingPackageMutationResult.FailedBeforeMutation(exception);
        }
    }

    internal async Task FixInstalledLocationsAsync(RepairInstalledLocationRequest request)
    {
        if (request == null)
        {
            throw new ArgumentNullException(nameof(request));
        }
        if (!request.HasTargets)
        {
            return;
        }
        if (!request.HasInstallDestination)
        {
            UiDialogResult warningResult = await dialogs.ShowMessageAsync(new UiMessageRequest(
                BeMusicSeeker.Properties.Resources.Msg_fix_installation_warning,
                BeMusicSeeker.Properties.Resources.Warning,
                MessageBoxButton.OK,
                MessageBoxImage.Exclamation,
                MessageBoxResult.OK));
            EnsureMessageWasShown(warningResult, "Installed-location repair warning");
            return;
        }
        UiDialogResult repairConfirmation = await dialogs.ConfirmAsync(new UiConfirmationRequest(
            BeMusicSeeker.Properties.Resources.Msg_fix_installation,
            BeMusicSeeker.Properties.Resources.Confirm,
            MessageBoxButton.OKCancel,
            MessageBoxImage.Question,
            MessageBoxResult.Cancel));
        if (!ToConfirmationDecision(repairConfirmation, "Installed-location repair confirmation"))
        {
            return;
        }

        request.MaterializeRepairEntries();
        IReadOnlyList<ChartFile> repairCharts = request.RepairCharts;
        IReadOnlyList<BMSLibrary.DuplicateInstallRepairConfirmation> duplicateConfirmations =
            await Task.Run(() => Read(library =>
                store.GetDuplicateInstallRepairConfirmations(library, repairCharts))) ?? [];
        var approvedDuplicateRemovalChartPaths = new List<string>();
        foreach (BMSLibrary.DuplicateInstallRepairConfirmation duplicateConfirmation in duplicateConfirmations)
        {
            ChartFile chart = duplicateConfirmation.Chart;
            if (chart == null || string.IsNullOrWhiteSpace(chart.Path))
            {
                continue;
            }
            UiDialogResult duplicateConfirmationResult = await dialogs.ConfirmAsync(new UiConfirmationRequest(
                string.Format(
                    BeMusicSeeker.Properties.Resources.Confirm_DuplicateReinstallSkipped,
                    chart.Path,
                    string.Join(Environment.NewLine, duplicateConfirmation.DuplicatePaths)),
                BeMusicSeeker.Properties.Resources.MessageBoxTitle_Confirm,
                MessageBoxButton.YesNo,
                MessageBoxImage.Question,
                MessageBoxResult.Yes));
            if (ToConfirmationDecision(
                duplicateConfirmationResult,
                "Duplicate reinstall repair confirmation"))
            {
                approvedDuplicateRemovalChartPaths.Add(chart.Path);
            }
        }

        await Task.Run(() => Execute(
            library => store.FixInstalledLocations(
                library,
                repairCharts,
                approvedDuplicateRemovalChartPaths),
            PendingPackageRefreshScope.PackageMutation,
            [.. repairCharts.Where(ChartFileKindResolver.IsBmsChartFile)]));
    }

    internal async Task DeleteInstalledOnlyPendingPackageSourcesAsync()
    {
        IReadOnlyList<ChartPackage> packages = await Task.Run(() =>
            Read(store.GetInstalledOnlyPendingPackages)) ?? [];
        if (packages.Count == 0)
        {
            await ShowMessageAsync(
                BeMusicSeeker.Properties.Resources.Warn_no_pending_installed_only_packages,
                BeMusicSeeker.Properties.Resources.Warning,
                MessageBoxImage.Exclamation,
                "Installed-only pending-package source warning");
            return;
        }
        UiDialogResult confirmation = await dialogs.ConfirmAsync(new UiConfirmationRequest(
            string.Format(
                BeMusicSeeker.Properties.Resources.Msg_delete_pending_installed_only_packages_permanently,
                packages.Count),
            BeMusicSeeker.Properties.Resources.Confirm,
            MessageBoxButton.OKCancel,
            MessageBoxImage.Warning,
            MessageBoxResult.Cancel));
        if (!ToConfirmationDecision(
            confirmation,
            "Installed-only pending-package source deletion confirmation"))
        {
            return;
        }

        await RunBulkOperationAsync(
            packages,
            BeMusicSeeker.Properties.Resources.Remove,
            package => package.path,
            (cancellationToken, onEachProcessed) => Execute(library =>
                store.DeletePendingPackageSources(
                    library,
                    packages,
                    cancellationToken,
                    onEachProcessed)));
    }

    internal async Task RenamePendingZeroNoteChartsAsync()
    {
        IReadOnlyList<ChartFile> charts = await Task.Run(() =>
            Read(store.GetPendingBmsFormatCharts)) ?? [];
        if (charts.Count == 0)
        {
            await ShowMessageAsync(
                BeMusicSeeker.Properties.Resources.Warn_no_pending_charts,
                BeMusicSeeker.Properties.Resources.Warning,
                MessageBoxImage.Exclamation,
                "Pending chart rename warning");
            return;
        }
        UiDialogResult confirmation = await dialogs.ConfirmAsync(new UiConfirmationRequest(
            string.Format(
                BeMusicSeeker.Properties.Resources.Msg_rename_pending_zero_note_to_invalid_ext,
                charts.Count),
            BeMusicSeeker.Properties.Resources.Confirm,
            MessageBoxButton.OKCancel,
            MessageBoxImage.Warning,
            MessageBoxResult.Cancel));
        if (!ToConfirmationDecision(confirmation, "Pending zero-note chart rename confirmation"))
        {
            return;
        }

        await RunBulkOperationAsync(
            charts,
            BeMusicSeeker.Properties.Resources.Rename_invalid_ext,
            chart => chart.Path,
            (cancellationToken, onEachProcessed) => Execute(
                library => store.RenamePendingZeroNoteCharts(
                    library,
                    charts,
                    cancellationToken,
                    onEachProcessed),
                playbackTargets: charts));
    }

    internal async Task OverwriteInstalledOnlyPendingPackageResourcesAsync()
    {
        IReadOnlyList<ChartPackage> packages = await Task.Run(() =>
            Read(store.GetInstalledOnlyPendingPackages)) ?? [];
        if (packages.Count == 0)
        {
            await ShowMessageAsync(
                BeMusicSeeker.Properties.Resources.Warn_no_pending_installed_only_packages,
                BeMusicSeeker.Properties.Resources.Warning,
                MessageBoxImage.Exclamation,
                "Installed-only pending-package overwrite warning");
            return;
        }
        UiDialogResult confirmation = await dialogs.ConfirmAsync(new UiConfirmationRequest(
            string.Format(
                BeMusicSeeker.Properties.Resources.Msg_overwrite_pending_installed_only_packages_resources,
                packages.Count),
            BeMusicSeeker.Properties.Resources.Confirm,
            MessageBoxButton.OKCancel,
            MessageBoxImage.Warning,
            MessageBoxResult.Cancel));
        if (!ToConfirmationDecision(
            confirmation,
            "Installed-only pending-package resource overwrite confirmation"))
        {
            return;
        }

        PendingInstalledOnlyResourceOverwriteResult overwriteResult = null;
        await RunBulkOperationAsync(
            packages,
            BeMusicSeeker.Properties.Resources.Install_to_estimation,
            package => package.path,
            (cancellationToken, onEachProcessed) => Execute(
                library => overwriteResult = store.OverwriteInstalledOnlyPendingPackageResources(
                    library,
                    packages,
                    cancellationToken,
                    onEachProcessed),
                playbackTargets: CreatePlaybackTargetSnapshot(packages)));
        if (overwriteResult == null)
        {
            return;
        }
        await ShowMessageAsync(
            string.Format(
                BeMusicSeeker.Properties.Resources.Warn_overwrite_pending_installed_only_packages_summary,
                overwriteResult.Requested,
                overwriteResult.Processed,
                overwriteResult.SucceededInstall,
                overwriteResult.SucceededCleanupOnly,
                overwriteResult.SkippedNotPending,
                overwriteResult.SkippedMissingInstlDst,
                overwriteResult.SkippedMultiDestination,
                overwriteResult.SkippedNoComponentTarget,
                overwriteResult.Failed,
                overwriteResult.Canceled),
            BeMusicSeeker.Properties.Resources.Warning,
            MessageBoxImage.Exclamation,
            "Installed-only pending-package resource overwrite summary");
    }

    private async Task<PendingPackageMutationResult> InstallPackagesAsync(
        PendingInstallPackageOperationKind kind,
        IReadOnlyList<ChartPackage> packages)
    {
        try
        {
            if (kind == PendingInstallPackageOperationKind.ManualInstall
                && !await ConfirmManualInstallAsync())
            {
                return PendingPackageMutationResult.Rejected;
            }
            return await InstallResolvedPackagesAsync(kind, packages);
        }
        catch (Exception exception)
        {
            return PendingPackageMutationResult.FailedBeforeMutation(exception);
        }
    }

    private async Task<PendingPackageMutationResult> InstallResolvedPackagesAsync(
        PendingInstallPackageOperationKind kind,
        IReadOnlyList<ChartPackage> packages)
    {
        try
        {
            switch (kind)
            {
                case PendingInstallPackageOperationKind.ForceInstall:
                    ISet<ChartPackage> approvedPackages = await ConfirmNormalInstallOverridesAsync(packages);
                    if (store is IPendingPackageTerminalMutationStore terminalStore)
                    {
                        return await ExecuteInstallAsync(
                            library => terminalStore.ForceInstallPackagesWithReceipt(
                                library,
                                packages,
                                approvedPackages),
                            packages);
                    }
                    return await ExecuteInstallAsync(
                        library => store.ForceInstallPackages(library, packages, approvedPackages),
                        packages);
                case PendingInstallPackageOperationKind.ManualInstall:
                    if (store is IPendingPackageTerminalMutationStore terminalManualStore)
                    {
                        return await ExecuteInstallAsync(
                            library => terminalManualStore.ManualInstallPackagesWithReceipt(library, packages)?.MutationReceipt,
                            packages);
                    }
                    return await ExecuteInstallAsync(
                        library => store.ManualInstallPackages(library, packages),
                        packages);
                default:
                    throw new ArgumentOutOfRangeException(nameof(kind), kind, "Unsupported pending install package operation.");
            }
        }
        catch (Exception exception)
        {
            return PendingPackageMutationResult.FailedBeforeMutation(exception);
        }
    }

    private async Task<PendingPackageMutationResult> ExecuteInstallAsync(
        Action<BMSLibrary> mutation,
        IReadOnlyList<ChartPackage> packages)
    {
        return await ExecuteInstallAsync(
            library =>
            {
                mutation(library);
                return null;
            },
            packages);
    }

    private async Task<PendingPackageMutationResult> ExecuteInstallAsync(
        Func<BMSLibrary, FileDbMutationBatchReceipt> mutationWithReceipt,
        IReadOnlyList<ChartPackage> packages)
    {
        bool pendingSectionEmpty = false;
        FileDbMutationBatchReceipt mutationReceipt = null;
        try
        {
            await Task.Run(() => Execute(
                library => mutationReceipt = mutationWithReceipt(library),
                PendingPackageRefreshScope.PackageMutation,
                CreatePlaybackTargetSnapshot(packages),
                captureMutationFacts: library => pendingSectionEmpty = store.IsPendingSectionEmpty(library)));
            return PendingPackageMutationResult.FromTerminal(
                mutationReceipt,
                pendingSectionEmpty ? PackageCatalogSection.Pending : null);
        }
        catch (Exception exception)
        {
            return PendingPackageMutationResult.FailedAfterMutation(
                exception,
                pendingSectionEmpty ? PackageCatalogSection.Pending : null,
                mutationReceipt);
        }
    }

    private async Task<ISet<ChartPackage>> ConfirmNormalInstallOverridesAsync(
        IReadOnlyList<ChartPackage> packages)
    {
        var approvedPackages = new HashSet<ChartPackage>();
        foreach (ChartPackage package in packages.Where(package =>
            (package.ChartEntries ?? []).Any(entry =>
                !string.IsNullOrWhiteSpace(entry?.Chart?.InstallDestination))))
        {
            UiDialogResult result = await dialogs.ConfirmAsync(new UiConfirmationRequest(
                BeMusicSeeker.Properties.Resources.Confirm_NormalInstallOverride,
                BeMusicSeeker.Properties.Resources.Confirm_NormalInstallTitle,
                MessageBoxButton.YesNo,
                MessageBoxImage.Question,
                MessageBoxResult.Yes));
            if (ToConfirmationDecision(result, "Pending package normal install override confirmation"))
            {
                approvedPackages.Add(package);
            }
        }
        return approvedPackages;
    }

    private async Task<bool> ConfirmManualInstallAsync()
    {
        InstallDestinationWorkflowSettingsSnapshot settings = settingsProvider()
            ?? throw new InvalidOperationException("Install-destination workflow settings provider returned null.");
        if (!settings.ShowManualInstallConfirmation)
        {
            return true;
        }
        string message = settings.DeletePendingPackageSourceAfterInstall
            ? BeMusicSeeker.Properties.Resources.Msg_manual_installation_delete_source
            : BeMusicSeeker.Properties.Resources.Msg_manual_installation;
        UiDialogResult result = await dialogs.ConfirmAsync(new UiConfirmationRequest(
            message,
            BeMusicSeeker.Properties.Resources.Confirm,
            MessageBoxButton.OKCancel,
            MessageBoxImage.Asterisk));
        return ToConfirmationDecision(result, "Manual pending-package installation confirmation");
    }

    private async Task RunBulkOperationAsync<T>(
        IReadOnlyList<T> items,
        string title,
        Func<T, string> itemLabel,
        Action<CancellationToken, Action> operation)
    {
        if (items.Count == 1)
        {
            await Task.Run(() => operation(CancellationToken.None, null));
            return;
        }

        using var cancellationTokenSource = new CancellationTokenSource();
        int processedCount = 0;
        Task operationTask = Task.Run(() =>
            operation(
                cancellationTokenSource.Token,
                () => Interlocked.Increment(ref processedCount)));
        UiProgressResult progressResult;
        try
        {
            progressResult = await dialogs.RunWithProgressAsync(
                new UiProgressRequest(
                    title,
                    string.Empty,
                    new Parago.Windows.ProgressDialogSettings(
                        showSubLabel: true,
                        showCancelButton: true,
                        showProgressBarIndeterminate: false)),
                async context =>
                {
                    while (!operationTask.IsCompleted)
                    {
                        int currentProcessedCount = Volatile.Read(ref processedCount);
                        try
                        {
                            int currentIndex = Math.Min(currentProcessedCount, items.Count - 1);
                            context.ReportWithCancellationCheck(
                                100 * currentProcessedCount / items.Count,
                                "[{0}/{1}] {2}",
                                Math.Min(currentProcessedCount + 1, items.Count),
                                items.Count,
                                itemLabel(items[currentIndex]) ?? "(null)");
                        }
                        catch (OperationCanceledException)
                        {
                            cancellationTokenSource.Cancel();
                            ExceptionDispatchInfo operationFailure = await CaptureOperationFailureAsync(
                                operationTask,
                                cancellationTokenSource.Token);
                            operationFailure?.Throw();
                            break;
                        }
                        catch
                        {
                            cancellationTokenSource.Cancel();
                            throw;
                        }
                        await Task.Delay(100);
                    }
                });
        }
        catch (Exception ex)
        {
            cancellationTokenSource.Cancel();
            ExceptionDispatchInfo operationFailure = await CaptureOperationFailureAsync(
                operationTask,
                cancellationTokenSource.Token);
            ThrowProgressFailures(ExceptionDispatchInfo.Capture(ex), operationFailure);
            return;
        }
        if (progressResult == null)
        {
            cancellationTokenSource.Cancel();
            ExceptionDispatchInfo operationFailure = await CaptureOperationFailureAsync(
                operationTask,
                cancellationTokenSource.Token);
            ThrowProgressFailures(
                ExceptionDispatchInfo.Capture(new InvalidOperationException(
                    "Pending-package progress dialog route returned no result.")),
                operationFailure);
            return;
        }
        if (progressResult.Status == UiDialogStatus.Accepted)
        {
            ExceptionDispatchInfo operationFailure = await CaptureOperationFailureAsync(
                operationTask,
                cancellationTokenSource.Token);
            operationFailure?.Throw();
            return;
        }
        cancellationTokenSource.Cancel();
        ExceptionDispatchInfo terminalOperationFailure = await CaptureOperationFailureAsync(
            operationTask,
            cancellationTokenSource.Token);
        if (progressResult.Status == UiDialogStatus.CancelledByUser)
        {
            terminalOperationFailure?.Throw();
            return;
        }
        ThrowProgressFailures(
            ExceptionDispatchInfo.Capture(new InvalidOperationException(
                "Pending-package progress dialog route failed: " + progressResult.Status,
                progressResult.Error)),
            terminalOperationFailure);
    }

    private async Task ShowMessageAsync(
        string message,
        string title,
        MessageBoxImage image,
        string routeName)
    {
        UiDialogResult result = await dialogs.ShowMessageAsync(new UiMessageRequest(
            message,
            title,
            MessageBoxButton.OK,
            image,
            MessageBoxResult.OK));
        EnsureMessageWasShown(result, routeName);
    }

    private bool TryResolveInstallDestination(
        ChartFile chart,
        out string installDirectory,
        out string reason)
    {
        installDirectory = null;
        reason = null;
        if (chart == null)
        {
            reason = BeMusicSeeker.Properties.Resources.Msg_open_install_destination_missing;
            return false;
        }
        if (!string.IsNullOrWhiteSpace(chart.InstallDestination))
        {
            if (LongPathFileSystem.DirectoryExists(chart.InstallDestination))
            {
                installDirectory = chart.InstallDestination;
                return true;
            }
            reason = string.Format(
                BeMusicSeeker.Properties.Resources.Msg_open_install_destination_not_found,
                chart.InstallDestination);
            return false;
        }
        string lookupHash = ChartLookupKey.GetPrimaryHash(chart);
        if (!string.IsNullOrWhiteSpace(lookupHash)
            && TryGetInstalledDirectoryByHash(lookupHash, out string resolvedDirectory))
        {
            installDirectory = resolvedDirectory;
            return true;
        }
        reason = BeMusicSeeker.Properties.Resources.Msg_open_install_destination_missing;
        return false;
    }

    private bool TryGetInstalledDirectoryByHash(string hash, out string installDirectory)
    {
        installDirectory = null;
        BMSLibrary library = libraryProvider();
        if (library == null)
        {
            return false;
        }
        using (chartFileOperations.Enter())
        {
            return library.TryGetInstalledDirectoryByHash(hash, out installDirectory);
        }
    }

    private void OpenInstallDestination(string installDirectory)
    {
        if (string.IsNullOrWhiteSpace(installDirectory))
        {
            return;
        }
        _ = externalShellGateway.OpenDirectory(installDirectory);
    }

    private static async Task<ExceptionDispatchInfo> CaptureOperationFailureAsync(
        Task task,
        CancellationToken expectedCancellationToken)
    {
        try
        {
            await task;
            return null;
        }
        catch (OperationCanceledException) when (expectedCancellationToken.IsCancellationRequested)
        {
            return null;
        }
        catch (Exception ex)
        {
            return ExceptionDispatchInfo.Capture(ex);
        }
    }

    private static void ThrowProgressFailures(
        ExceptionDispatchInfo primaryFailure,
        ExceptionDispatchInfo operationFailure)
    {
        if (operationFailure == null)
        {
            primaryFailure.Throw();
        }
        throw new AggregateException(
            "Pending-package progress dialog and mutation both failed.",
            primaryFailure.SourceException,
            operationFailure.SourceException);
    }

    private static IReadOnlyList<ChartPackage> MaterializePackages(IEnumerable<ChartPackage> packages)
    {
        if (packages == null)
        {
            throw new ArgumentNullException(nameof(packages));
        }
        return [.. packages.Where(package => package != null)];
    }

    private static IReadOnlyList<ChartFile> CreatePlaybackTargetSnapshot(
        IEnumerable<ChartPackage> packages)
    {
        return [.. (packages ?? [])
            .Where(package => package != null)
            .SelectMany(package => package.ChartEntries ?? [])
            .Select(entry => entry?.Chart)
            .Where(chart => chart != null)];
    }

    private Task RunSearchAsync(PendingInstallDestinationSearchKind kind, Action operation)
    {
        return kind == PendingInstallDestinationSearchKind.MergeDestination
            ? ConfirmAndRunSearchAsync(operation)
            : Task.Run(operation);
    }

    private async Task ConfirmAndRunSearchAsync(Action operation)
    {
        UiDialogResult result = await dialogs.ConfirmAsync(new UiConfirmationRequest(
            BeMusicSeeker.Properties.Resources.Msg_estimate_merge_confirm,
            BeMusicSeeker.Properties.Resources.Confirm,
            MessageBoxButton.OKCancel,
            MessageBoxImage.Asterisk));
        if (!ToConfirmationDecision(result, "Merge destination confirmation"))
        {
            return;
        }
        await Task.Run(operation);
    }

    private static bool ToConfirmationDecision(UiDialogResult result, string routeName)
    {
        if (result == null)
        {
            throw new InvalidOperationException(routeName + " returned no result.");
        }
        return result.Status switch
        {
            UiDialogStatus.Accepted => true,
            UiDialogStatus.Rejected or UiDialogStatus.CancelledByUser => false,
            UiDialogStatus.ClosedByUser => result.IsPositive,
            _ => throw new InvalidOperationException(
                routeName + " could not be displayed (" + result.Status + ").",
                result.Exception),
        };
    }

    private static void EnsureMessageWasShown(UiDialogResult result, string routeName)
    {
        if (result == null)
        {
            throw new InvalidOperationException(routeName + " returned no result.");
        }
        if (result.Status is UiDialogStatus.Accepted
            or UiDialogStatus.CancelledByUser
            or UiDialogStatus.ClosedByUser)
        {
            return;
        }
        throw new InvalidOperationException(
            routeName + " could not be displayed (" + result.Status + ").",
            result.Exception);
    }

    private bool Execute(
        Action<BMSLibrary> mutation,
        PendingPackageRefreshScope refreshScope = PendingPackageRefreshScope.DestinationState,
        IReadOnlyList<ChartFile> playbackTargets = null,
        bool requiresLibrary = true,
        Action<BMSLibrary> captureMutationFacts = null)
    {
        BMSLibrary library = libraryProvider();
        if (requiresLibrary && library == null)
        {
            return false;
        }
        BMSLibrary.OperationDialogScope dialogScope = null;
        IDisposable operationGate = null;
        IDisposable activityLease = null;
        bool suppressionStarted = false;
        bool mutationAttempted = false;
        var failures = new List<ExceptionDispatchInfo>();
        try
        {
            dialogScope = library?.BeginOperationDialogScope();
            activityLease = chartMutationActivity.Enter();
            operationGate = chartFileOperations.Enter();
            if (playbackTargets != null)
            {
                playback.StopIfPlayingCharts(playbackTargets);
            }
            suppressionStarted = true;
            PublishRefreshSuppressionChanged(isSuppressed: true, refreshScope);
            mutationAttempted = true;
            mutation(library);
        }
        catch (Exception ex)
        {
            failures.Add(ExceptionDispatchInfo.Capture(ex));
        }
        finally
        {
            if (mutationAttempted && captureMutationFacts != null && library != null)
            {
                CaptureCleanupFailure(() => captureMutationFacts(library), failures);
            }
            if (suppressionStarted)
            {
                CaptureCleanupFailure(
                    () => PublishRefreshSuppressionChanged(isSuppressed: false, refreshScope: null),
                    failures);
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
        ThrowFailures(failures);
        return true;
    }

    private void PublishRefreshSuppressionChanged(
        bool isSuppressed,
        PendingPackageRefreshScope? refreshScope)
    {
        WorkflowChanged?.Invoke(
            this,
            new PendingPackageRefreshSuppressionChangedEventArgs(isSuppressed, refreshScope));
    }

    private void PublishChangedCharts(IEnumerable<ChartFile> changedCharts)
    {
        PublishMutationApplied(changedCharts: changedCharts);
    }

    private void PublishMutationApplied(
        IEnumerable<ChartFile> changedCharts = null,
        bool installDestinationStateChanged = false,
        bool identitySortKeyChanged = false,
        bool displayStateChanged = false)
    {
        WorkflowChanged?.Invoke(
            this,
            new PendingPackageMutationAppliedEventArgs(
                changedCharts,
                installDestinationStateChanged,
                identitySortKeyChanged,
                displayStateChanged));
    }

    private T Read<T>(Func<BMSLibrary, T> operation)
    {
        if (operation == null)
        {
            throw new ArgumentNullException(nameof(operation));
        }
        BMSLibrary library = libraryProvider();
        if (library == null)
        {
            return default;
        }
        using (chartFileOperations.Enter())
        {
            return operation(library);
        }
    }

    private static void CaptureCleanupFailure(Action cleanup, List<ExceptionDispatchInfo> failures)
    {
        try
        {
            cleanup();
        }
        catch (Exception ex)
        {
            failures.Add(ExceptionDispatchInfo.Capture(ex));
        }
    }

    private static void ThrowFailures(IReadOnlyList<ExceptionDispatchInfo> failures)
    {
        if (failures.Count == 1)
        {
            failures[0].Throw();
        }
        if (failures.Count > 1)
        {
            throw new AggregateException(failures.Select(failure => failure.SourceException));
        }
    }

    private static void ValidateKind(PendingInstallDestinationSearchKind kind)
    {
        if (kind != PendingInstallDestinationSearchKind.InstallDestination
            && kind != PendingInstallDestinationSearchKind.MergeDestination)
        {
            throw new ArgumentOutOfRangeException(nameof(kind), kind, "Unsupported install destination search kind.");
        }
    }
}

internal sealed class BmsLibraryPendingPackageStore : IPendingPackageStore, IPendingPackageTerminalMutationStore
{
    public void SearchPackages(
        BMSLibrary library,
        PendingInstallDestinationSearchKind kind,
        IReadOnlyList<ChartPackage> packages)
    {
        if (kind == PendingInstallDestinationSearchKind.InstallDestination)
        {
            library.SearchEstimatedInstallationDirectory(packages);
            return;
        }
        foreach (ChartPackage package in packages)
        {
            library.SearchMergeDestinationForPendingPackage(package);
        }
    }

    public IReadOnlyList<ChartFile> SearchPending(
        BMSLibrary library,
        PendingInstallDestinationSearchRequest request)
    {
        IReadOnlyList<ChartPackage> packages = ResolvePackages(library, request.PackageTargets);
        if (request.Kind == PendingInstallDestinationSearchKind.InstallDestination)
        {
            if (packages.Count > 0)
            {
                library.SearchEstimatedInstallationDirectory(packages);
            }
            if (request.LooseEntries.Count > 0)
            {
                library.SearchEstimatedInstallationDirectoryForLooseCharts(request.LooseEntries);
            }
        }
        else
        {
            foreach (ChartPackage package in packages)
            {
                library.SearchMergeDestinationForPendingPackage(package);
            }
            if (request.LooseEntries.Count > 0)
            {
                library.SearchMergeDestinationForPendingCharts(request.LooseEntries);
            }
        }
        return [.. request.LooseEntries.Select(entry => entry?.Chart).Where(chart => chart != null)];
    }

    public IReadOnlyList<ChartFile> ClearPackages(IReadOnlyList<ChartPackage> packages)
    {
        IReadOnlyList<PackageChartEntry> entries = [.. packages
            .SelectMany(package => package?.ChartEntries ?? [])
            .Where(entry => entry?.Chart != null)];
        foreach (ChartPackage package in packages)
        {
            ClearPackage(package);
        }
        return [.. entries.Select(entry => entry.Chart)];
    }

    public IReadOnlyList<ChartFile> ClearPending(
        BMSLibrary library,
        PendingInstallDestinationClearRequest request)
    {
        foreach (ChartPackage package in ResolvePackages(library, request.PackageTargets))
        {
            ClearPackage(package);
        }
        library.RemoveInstallDestination(request.LooseEntries);
        return [.. request.LooseEntries.Select(entry => entry?.Chart).Where(chart => chart != null)];
    }

    public IReadOnlyList<ChartFile> SearchCorrect(
        BMSLibrary library,
        RepairInstalledLocationRequest request)
    {
        library.SearchCorrectInstallationDirectoryCharts(request.RepairEntries);
        return [.. request.RepairEntries.Select(entry => entry?.Chart).Where(chart => chart != null)];
    }

    public IReadOnlyList<ChartFile> ClearCorrect(
        BMSLibrary library,
        RepairInstalledLocationRequest request)
    {
        List<PackageChartEntry> remainingEntries = [.. request.RepairEntries.Where(entry => entry?.Chart != null)];
        foreach (ChartPackage package in ResolvePackages(library, ref remainingEntries))
        {
            ClearPackage(package);
        }
        library.RemoveInstallDestination(remainingEntries);
        return [.. remainingEntries.Select(entry => entry.Chart)];
    }

    public ChartFile SetPending(
        BMSLibrary library,
        PendingInstallDestinationEditRequest request,
        string destinationDirectory)
    {
        PackageChartEntry entry = request.PackageEntry ?? request.GetOrCreateChartEntry();
        return entry != null && library.SetPendingInstallDestination(entry, destinationDirectory)
            ? entry.Chart
            : null;
    }

    public IReadOnlyList<ChartPackage> ResolvePendingPackages(
        BMSLibrary library,
        IReadOnlyList<ChartOperationTarget> targets)
    {
        return ResolvePackages(library, targets);
    }

    public void ForceInstallPackages(
        BMSLibrary library,
        IReadOnlyList<ChartPackage> packages,
        ISet<ChartPackage> approvedNormalInstallOverridePackages)
    {
        library.ForceInstallPendingPackages(
            packages,
            approveNormalInstallOverride: false,
            approvedNormalInstallOverridePackages: approvedNormalInstallOverridePackages);
    }

    public void ManualInstallPackages(
        BMSLibrary library,
        IReadOnlyList<ChartPackage> packages)
    {
        library.InstallPendingPackagesToEstimatedDestinations(packages);
    }

    public FileDbMutationBatchReceipt ForceInstallPackagesWithReceipt(
        BMSLibrary library,
        IReadOnlyList<ChartPackage> packages,
        ISet<ChartPackage> approvedNormalInstallOverridePackages)
    {
        return library.ForceInstallPendingPackagesWithReceipt(
            packages,
            approveNormalInstallOverride: false,
            approvedNormalInstallOverridePackages: approvedNormalInstallOverridePackages);
    }

    public PendingInstallBatchResult ManualInstallPackagesWithReceipt(
        BMSLibrary library,
        IReadOnlyList<ChartPackage> packages)
    {
        return library.InstallPendingPackagesToEstimatedDestinationsWithReceipt(packages);
    }

    public bool IsPendingSectionEmpty(BMSLibrary library)
    {
        return library?.ChartPackagesPending?.Count == 0;
    }

    public IReadOnlyList<BMSLibrary.DuplicateInstallRepairConfirmation> GetDuplicateInstallRepairConfirmations(
        BMSLibrary library,
        IReadOnlyList<ChartFile> repairCharts)
    {
        return library.GetDuplicateInstallRepairConfirmations(repairCharts);
    }

    public void FixInstalledLocations(
        BMSLibrary library,
        IReadOnlyList<ChartFile> repairCharts,
        IReadOnlyList<string> approvedDuplicateRemovalChartPaths)
    {
        library.FixInstallationDirectoryCharts(
            repairCharts,
            approvedDuplicateRemovalChartPaths);
    }

    public IReadOnlyList<ChartPackage> GetInstalledOnlyPendingPackages(BMSLibrary library)
    {
        return library.GetPendingPackagesContainingOnlyInstalledCharts();
    }

    public IReadOnlyList<ChartFile> GetPendingBmsFormatCharts(BMSLibrary library)
    {
        return library.GetPendingBmsFormatChartFilesSnapshot();
    }

    public void DeletePendingPackageSources(
        BMSLibrary library,
        IReadOnlyList<ChartPackage> packages,
        CancellationToken cancellationToken,
        Action onEachProcessed)
    {
        library.DeletePendingPackageSources(
            packages,
            sendToRecycleBin: false,
            cancellationToken,
            onEachProcessed);
    }

    public void RenamePendingZeroNoteCharts(
        BMSLibrary library,
        IReadOnlyList<ChartFile> charts,
        CancellationToken cancellationToken,
        Action onEachProcessed)
    {
        library.RenamePendingZeroNoteBmsFormatChartsToInvalidExtensions(
            charts,
            cancellationToken,
            onEachProcessed);
    }

    public PendingInstalledOnlyResourceOverwriteResult OverwriteInstalledOnlyPendingPackageResources(
        BMSLibrary library,
        IReadOnlyList<ChartPackage> packages,
        CancellationToken cancellationToken,
        Action onEachProcessed)
    {
        return library.OverwritePendingInstalledOnlyPackagesResources(
            packages,
            cancellationToken,
            onEachProcessed);
    }

    private static IReadOnlyList<ChartPackage> ResolvePackages(
        BMSLibrary library,
        IReadOnlyList<ChartOperationTarget> targets)
    {
        IEnumerable<ChartPackage> source = library.ChartPackagesPending;
        source ??= Enumerable.Empty<ChartPackage>();
        return [.. targets
            .Select(target => source.FirstOrDefault(package => ContainsTarget(package, target)))
            .Where(package => package != null)
            .Distinct()];
    }

    private static IReadOnlyList<ChartPackage> ResolvePackages(
        BMSLibrary library,
        ref List<PackageChartEntry> entries)
    {
        IEnumerable<ChartPackage> source = library.ChartPackagesPending;
        source ??= Enumerable.Empty<ChartPackage>();
        var remainingEntries = new List<PackageChartEntry>();
        var packages = new List<ChartPackage>();
        foreach (PackageChartEntry entry in entries)
        {
            ChartPackage package = source.FirstOrDefault(candidate => ContainsTarget(candidate, entry));
            if (package == null)
            {
                remainingEntries.Add(entry);
            }
            else
            {
                packages.Add(package);
            }
        }
        entries = remainingEntries;
        return [.. packages.Distinct()];
    }

    private static bool ContainsTarget(ChartPackage package, ChartOperationTarget target)
    {
        if (package == null || target?.Chart == null)
        {
            return false;
        }
        if (target.PackageEntry != null)
        {
            return (package.ChartEntries ?? []).Any(entry => entry?.IsSameChartTarget(target.PackageEntry) == true);
        }
        return (package.ChartEntries ?? []).Any(entry => entry?.IsSameChartTarget(target.Chart) == true);
    }

    private static bool ContainsTarget(ChartPackage package, PackageChartEntry target)
    {
        return package != null
            && target?.Chart != null
            && (package.ChartEntries ?? []).Any(entry => entry?.IsSameChartTarget(target) == true);
    }

    private static void ClearPackage(ChartPackage package)
    {
        foreach (PackageChartEntry entry in package?.ChartEntries ?? [])
        {
            entry?.ClearInstallDestination();
        }
    }
}
