using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.ExceptionServices;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Input;
using BeMusicSeeker.Models;
using BeMusicSeeker.Models.Utils;
using BeMusicSeeker.ViewModels;
using BeMusicSeeker.Views.Dialogs;

namespace BeMusicSeeker.Views;

/// <summary>
/// Owns the two library-folder menu commands without exposing the shell as a callback host.
/// </summary>
internal sealed class MainWindowLibraryReloadMenuTerminal
{
    private readonly Func<Task> reloadFileDiff;
    private readonly Func<Task> reinitializeLibrary;

    internal MainWindowLibraryReloadMenuTerminal(
        Func<Task> reloadFileDiff,
        Func<Task> reinitializeLibrary)
    {
        this.reloadFileDiff = reloadFileDiff ?? throw new ArgumentNullException(nameof(reloadFileDiff));
        this.reinitializeLibrary = reinitializeLibrary ?? throw new ArgumentNullException(nameof(reinitializeLibrary));
    }

    internal Task ReloadFileDiffAsync() => reloadFileDiff();

    internal Task ReinitializeLibraryAsync() => reinitializeLibrary();

    internal static MainWindowLibraryReloadMenuTerminal Create(MainWindowViewModel viewModel)
    {
        ArgumentNullException.ThrowIfNull(viewModel);
        return new MainWindowLibraryReloadMenuTerminal(
            viewModel.ReloadFileDiffAsync,
            viewModel.ReinitializeLibraryAsync);
    }
}

/// <summary>
/// Owns regular-library tree navigation for the compiled MainWindow selection route.
/// </summary>
internal sealed class MainWindowRegularLibraryTreeTerminal
{
    private readonly Func<RegularChartFolderFilterKind?, string, bool> navigateTree;

    internal MainWindowRegularLibraryTreeTerminal(
        Func<RegularChartFolderFilterKind?, string, bool> navigateTree)
    {
        this.navigateTree = navigateTree ?? throw new ArgumentNullException(nameof(navigateTree));
    }

    internal bool NavigateTree(RegularChartFolderFilterKind? filterKind, string filterKey)
        => navigateTree(filterKind, filterKey);

    internal static MainWindowRegularLibraryTreeTerminal Create(MainWindowViewModel viewModel)
    {
        ArgumentNullException.ThrowIfNull(viewModel);
        return new MainWindowRegularLibraryTreeTerminal(viewModel.RegularChartList.NavigateTree);
    }
}

/// <summary>
/// Owns maintenance-tree navigation while preserving the asynchronous owner contract.
/// </summary>
internal sealed class MainWindowMaintenanceTreeTerminal
{
    private readonly Func<MainViewUpdateMode, object, Task<bool>> navigate;

    internal MainWindowMaintenanceTreeTerminal(
        Func<MainViewUpdateMode, object, Task<bool>> navigate)
    {
        this.navigate = navigate ?? throw new ArgumentNullException(nameof(navigate));
    }

    internal Task<bool> NavigateAsync(MainViewUpdateMode mode, object parameter = null)
        => navigate(mode, parameter);

    internal static MainWindowMaintenanceTreeTerminal Create(MainWindowViewModel viewModel)
    {
        ArgumentNullException.ThrowIfNull(viewModel);
        return new MainWindowMaintenanceTreeTerminal(
            (mode, parameter) => viewModel.RegularChartList.NavigateMaintenanceAsync(mode, parameter));
    }
}

/// <summary>
/// Owns install-tree navigation while preserving the asynchronous owner contract.
/// </summary>
internal sealed class MainWindowInstallTreeTerminal
{
    private readonly Func<MainViewUpdateMode, object, Task<bool>> navigate;

    internal MainWindowInstallTreeTerminal(
        Func<MainViewUpdateMode, object, Task<bool>> navigate)
    {
        this.navigate = navigate ?? throw new ArgumentNullException(nameof(navigate));
    }

    internal Task<bool> NavigateAsync(MainViewUpdateMode mode, object parameter = null)
        => navigate(mode, parameter);

    internal static MainWindowInstallTreeTerminal Create(MainWindowViewModel viewModel)
    {
        ArgumentNullException.ThrowIfNull(viewModel);
        return new MainWindowInstallTreeTerminal(
            (mode, parameter) => viewModel.RegularChartList.NavigateInstallAsync(mode, parameter));
    }
}

/// <summary>
/// Owns the zero-note recheck terminal and keeps its task result visible to the caller.
/// </summary>
internal sealed class MainWindowZeroNoteRecheckTerminal
{
    private readonly Func<Task<bool>> recheck;

    internal MainWindowZeroNoteRecheckTerminal(Func<Task<bool>> recheck)
    {
        this.recheck = recheck ?? throw new ArgumentNullException(nameof(recheck));
    }

    internal Task<bool> RecheckAsync() => recheck();

    internal static MainWindowZeroNoteRecheckTerminal Create(MainWindowViewModel viewModel)
    {
        ArgumentNullException.ThrowIfNull(viewModel);
        return new MainWindowZeroNoteRecheckTerminal(viewModel.ZeroNoteMaintenance.RecheckAsync);
    }
}

/// <summary>
/// Owns the column-reset command, including its existing confirmation terminal.
/// </summary>
internal sealed class MainWindowColumnResetTerminal
{
    private readonly Action<Window> reset;

    internal MainWindowColumnResetTerminal(Action<Window> reset)
    {
        this.reset = reset ?? throw new ArgumentNullException(nameof(reset));
    }

    internal void Reset(Window owner) => reset(owner);

    internal static MainWindowColumnResetTerminal Create(MainWindowViewModel viewModel)
    {
        ArgumentNullException.ThrowIfNull(viewModel);
        return new MainWindowColumnResetTerminal(owner =>
        {
            if (UiDialogRoute.ShowMessageBox(
                    owner,
                    BeMusicSeeker.Properties.Resources.Msg_init_column_settings,
                    BeMusicSeeker.Properties.Resources.Confirm,
                    MessageBoxButton.OKCancel,
                    MessageBoxImage.Question,
                    MessageBoxResult.Cancel) != MessageBoxResult.Cancel)
            {
                viewModel.RegularChartList.ResetCurrentColumnPresentation();
            }
        });
    }
}

/// <summary>
/// Owns removal of one library search root after the compiled context-menu route resolves its path.
/// </summary>
internal sealed class MainWindowRootFolderUnregisterTerminal
{
    private readonly Func<string, Task> unregister;

    internal MainWindowRootFolderUnregisterTerminal(Func<string, Task> unregister)
    {
        this.unregister = unregister ?? throw new ArgumentNullException(nameof(unregister));
    }

    internal Task UnregisterAsync(string path) => unregister(path);

    internal static MainWindowRootFolderUnregisterTerminal Create(MainWindowViewModel viewModel)
    {
        ArgumentNullException.ThrowIfNull(viewModel);
        return new MainWindowRootFolderUnregisterTerminal(
            viewModel.SettingDialog.RequestRemoveBmsSearchRootAsync);
    }
}

/// <summary>
/// Owns chart-target and library-folder auto-rename requests from the compiled shell menus.
/// </summary>
internal sealed class MainWindowFolderAutoRenameTerminal
{
    private readonly Func<IReadOnlyList<ChartOperationTarget>, bool> startSelected;
    private readonly Func<string, Task> startAll;

    internal MainWindowFolderAutoRenameTerminal(
        Func<IReadOnlyList<ChartOperationTarget>, bool> startSelected,
        Func<string, Task> startAll)
    {
        this.startSelected = startSelected ?? throw new ArgumentNullException(nameof(startSelected));
        this.startAll = startAll ?? throw new ArgumentNullException(nameof(startAll));
    }

    internal bool StartSelected(IReadOnlyList<ChartOperationTarget> targets)
        => startSelected(targets ?? throw new ArgumentNullException(nameof(targets)));

    internal Task StartAllAsync(string parentDirectory)
        => startAll(parentDirectory ?? throw new ArgumentNullException(nameof(parentDirectory)));

    internal static MainWindowFolderAutoRenameTerminal Create(MainWindowViewModel viewModel)
    {
        ArgumentNullException.ThrowIfNull(viewModel);
        return new MainWindowFolderAutoRenameTerminal(
            viewModel.FolderAutoRenameWorkflow.RequestStartSelected,
            viewModel.FolderAutoRenameWorkflow.RequestStartAllAsync);
    }
}

/// <summary>
/// Owns one duplicate-folder merge request after the compiled menu resolves its source and target.
/// </summary>
internal sealed class MainWindowDuplicateMaintenanceTerminal
{
    private readonly Func<string, string, DuplicateGroup, Task<DuplicateMaintenanceMutationResult>> mergeFolder;
    private readonly Func<DuplicateGroup, string, Task<DuplicateMaintenanceMutationResult>> cleanupHash;
    private readonly Func<bool> isExecuteShortcut;

    internal MainWindowDuplicateMaintenanceTerminal(
        Func<string, string, DuplicateGroup, Task<DuplicateMaintenanceMutationResult>> mergeFolder,
        Func<DuplicateGroup, string, Task<DuplicateMaintenanceMutationResult>> cleanupHash,
        Func<bool> isExecuteShortcut)
    {
        this.mergeFolder = mergeFolder ?? throw new ArgumentNullException(nameof(mergeFolder));
        this.cleanupHash = cleanupHash ?? throw new ArgumentNullException(nameof(cleanupHash));
        this.isExecuteShortcut = isExecuteShortcut ?? throw new ArgumentNullException(nameof(isExecuteShortcut));
    }

    internal bool IsExecuteShortcut => isExecuteShortcut();

    internal Task<DuplicateMaintenanceMutationResult> MergeFolderAsync(
        string sourcePath,
        string destinationPath,
        DuplicateGroup group)
        => mergeFolder(
            sourcePath ?? throw new ArgumentNullException(nameof(sourcePath)),
            destinationPath ?? throw new ArgumentNullException(nameof(destinationPath)),
            group ?? throw new ArgumentNullException(nameof(group)));

    internal Task<DuplicateMaintenanceMutationResult> CleanupHashAsync(
        DuplicateGroup group,
        string folderPath)
        => cleanupHash(
            group ?? throw new ArgumentNullException(nameof(group)),
            folderPath ?? throw new ArgumentNullException(nameof(folderPath)));

    internal static MainWindowDuplicateMaintenanceTerminal Create(MainWindowViewModel viewModel)
    {
        ArgumentNullException.ThrowIfNull(viewModel);
        return new MainWindowDuplicateMaintenanceTerminal(
            viewModel.DuplicateMaintenanceWorkflow.RunFolderMergeAsync,
            viewModel.DuplicateMaintenanceWorkflow.RunHashCleanupAsync,
            () => Keyboard.Modifiers == ModifierKeys.Control);
    }
}

/// <summary>
/// Owns the full-resource-health start request used by the table context menu.
/// </summary>
internal sealed class MainWindowMaintenanceRescanTerminal
{
    private readonly Func<Task<MaintenanceRescanStartResult>> requestStart;

    internal MainWindowMaintenanceRescanTerminal(
        Func<Task<MaintenanceRescanStartResult>> requestStart)
    {
        this.requestStart = requestStart ?? throw new ArgumentNullException(nameof(requestStart));
    }

    internal Task<MaintenanceRescanStartResult> RequestStartAsync() => requestStart();

    internal static MainWindowMaintenanceRescanTerminal Create(MainWindowViewModel viewModel)
    {
        ArgumentNullException.ThrowIfNull(viewModel);
        return new MainWindowMaintenanceRescanTerminal(
            viewModel.MaintenanceRescanWorkflow.RequestStartAsync);
    }
}

/// <summary>
/// Owns package-catalog section and package removal requests from compiled tree/table menus.
/// </summary>
internal sealed class MainWindowPackageCatalogTerminal
{
    private readonly Func<PackageCatalogSection, Task<PackageCatalogMutationResult>> clearAll;
    private readonly Func<PackageCatalogSection, ChartPackage, Task<PackageCatalogMutationResult>> removePackage;
    private readonly Func<PackageCatalogRemovalRequest, Task<PackageCatalogMutationResult>> removeSelection;

    internal MainWindowPackageCatalogTerminal(
        Func<PackageCatalogSection, Task<PackageCatalogMutationResult>> clearAll,
        Func<PackageCatalogSection, ChartPackage, Task<PackageCatalogMutationResult>> removePackage,
        Func<PackageCatalogRemovalRequest, Task<PackageCatalogMutationResult>> removeSelection)
    {
        this.clearAll = clearAll ?? throw new ArgumentNullException(nameof(clearAll));
        this.removePackage = removePackage ?? throw new ArgumentNullException(nameof(removePackage));
        this.removeSelection = removeSelection ?? throw new ArgumentNullException(nameof(removeSelection));
    }

    internal Task<PackageCatalogMutationResult> ClearAllAsync(PackageCatalogSection section)
        => clearAll(section);

    internal Task<PackageCatalogMutationResult> RemovePackageAsync(
        PackageCatalogSection section,
        ChartPackage package)
        => removePackage(section, package ?? throw new ArgumentNullException(nameof(package)));

    internal Task<PackageCatalogMutationResult> RemoveSelectionAsync(PackageCatalogRemovalRequest request)
        => removeSelection(request ?? throw new ArgumentNullException(nameof(request)));

    internal static MainWindowPackageCatalogTerminal Create(MainWindowViewModel viewModel)
    {
        ArgumentNullException.ThrowIfNull(viewModel);
        return new MainWindowPackageCatalogTerminal(
            viewModel.PackageCatalog.ClearAllAsync,
            viewModel.PackageCatalog.RemovePackageAsync,
            viewModel.PackageCatalog.RemoveSelectionAsync);
    }
}

/// <summary>
/// Owns pending-install destination estimation requests from compiled package and chart menus.
/// </summary>
internal sealed class MainWindowPendingInstallEstimationTerminal
{
    private readonly Func<PendingInstallDestinationSearchKind, IEnumerable<ChartPackage>, Task> searchPackages;
    private readonly Func<PendingInstallDestinationSearchRequest, Task> searchPending;
    private readonly Func<PendingInstallDestinationClearRequest, Task> clearPending;
    private readonly Func<IEnumerable<ChartPackage>, Task> clearPackages;

    internal MainWindowPendingInstallEstimationTerminal(
        Func<PendingInstallDestinationSearchKind, IEnumerable<ChartPackage>, Task> searchPackages,
        Func<PendingInstallDestinationSearchRequest, Task> searchPending,
        Func<PendingInstallDestinationClearRequest, Task> clearPending,
        Func<IEnumerable<ChartPackage>, Task> clearPackages)
    {
        this.searchPackages = searchPackages ?? throw new ArgumentNullException(nameof(searchPackages));
        this.searchPending = searchPending ?? throw new ArgumentNullException(nameof(searchPending));
        this.clearPending = clearPending ?? throw new ArgumentNullException(nameof(clearPending));
        this.clearPackages = clearPackages ?? throw new ArgumentNullException(nameof(clearPackages));
    }

    internal Task SearchPackagesAsync(PendingInstallDestinationSearchKind kind, IEnumerable<ChartPackage> packages)
        => searchPackages(kind, packages ?? throw new ArgumentNullException(nameof(packages)));

    internal Task SearchPendingAsync(PendingInstallDestinationSearchRequest request)
        => searchPending(request ?? throw new ArgumentNullException(nameof(request)));

    internal Task ClearPendingAsync(PendingInstallDestinationClearRequest request)
        => clearPending(request ?? throw new ArgumentNullException(nameof(request)));

    internal Task ClearPackagesAsync(IEnumerable<ChartPackage> packages)
        => clearPackages(packages ?? throw new ArgumentNullException(nameof(packages)));

    internal static MainWindowPendingInstallEstimationTerminal Create(MainWindowViewModel viewModel)
    {
        ArgumentNullException.ThrowIfNull(viewModel);
        return new MainWindowPendingInstallEstimationTerminal(
            viewModel.PendingPackages.SearchPackagesAsync,
            viewModel.PendingPackages.SearchPendingAsync,
            viewModel.PendingPackages.ClearPendingAsync,
            viewModel.PendingPackages.ClearPackagesAsync);
    }
}

/// <summary>
/// Owns package installation requests from compiled package and pending-chart menus.
/// </summary>
internal sealed class MainWindowPendingInstallationTerminal
{
    private readonly Func<IEnumerable<ChartPackage>, Task<PendingPackageMutationResult>> forceInstallPackages;
    private readonly Func<IEnumerable<ChartPackage>, Task<PendingPackageMutationResult>> manualInstallPackages;
    private readonly Func<PendingInstallPackageOperationRequest, Task<PendingPackageMutationResult>> installPending;

    internal MainWindowPendingInstallationTerminal(
        Func<IEnumerable<ChartPackage>, Task<PendingPackageMutationResult>> forceInstallPackages,
        Func<IEnumerable<ChartPackage>, Task<PendingPackageMutationResult>> manualInstallPackages,
        Func<PendingInstallPackageOperationRequest, Task<PendingPackageMutationResult>> installPending)
    {
        this.forceInstallPackages = forceInstallPackages ?? throw new ArgumentNullException(nameof(forceInstallPackages));
        this.manualInstallPackages = manualInstallPackages ?? throw new ArgumentNullException(nameof(manualInstallPackages));
        this.installPending = installPending ?? throw new ArgumentNullException(nameof(installPending));
    }

    internal Task<PendingPackageMutationResult> ForceInstallPackagesAsync(IEnumerable<ChartPackage> packages)
        => forceInstallPackages(packages ?? throw new ArgumentNullException(nameof(packages)));

    internal Task<PendingPackageMutationResult> ManualInstallPackagesAsync(IEnumerable<ChartPackage> packages)
        => manualInstallPackages(packages ?? throw new ArgumentNullException(nameof(packages)));

    internal Task<PendingPackageMutationResult> InstallPendingAsync(PendingInstallPackageOperationRequest request)
        => installPending(request ?? throw new ArgumentNullException(nameof(request)));

    internal static MainWindowPendingInstallationTerminal Create(MainWindowViewModel viewModel)
    {
        ArgumentNullException.ThrowIfNull(viewModel);
        return new MainWindowPendingInstallationTerminal(
            viewModel.PendingPackages.ForceInstallPackagesAsync,
            viewModel.PendingPackages.ManualInstallPackagesAsync,
            viewModel.PendingPackages.InstallPendingAsync);
    }
}

/// <summary>
/// Captures the immutable tree state observed after a pending-package view mutation.
/// </summary>
internal sealed class MainWindowPendingPackageMutationAppliedViewState
{
    /// <summary>
    /// Initializes a post-apply tree state snapshot.
    /// </summary>
    /// <param name="initiallySelected">Whether the section root was selected before applying the view.</param>
    /// <param name="remainsSelected">Whether the section root remains selected after applying the view.</param>
    /// <param name="remainingItemCount">The section-root item count observed after applying the view.</param>
    internal MainWindowPendingPackageMutationAppliedViewState(
        bool initiallySelected,
        bool remainsSelected,
        int remainingItemCount)
    {
        InitiallySelected = initiallySelected;
        RemainsSelected = remainsSelected;
        RemainingItemCount = remainingItemCount;
    }

    /// <summary>
    /// Gets whether the section root was selected before the view mutation.
    /// </summary>
    internal bool InitiallySelected { get; }

    /// <summary>
    /// Gets whether the section root remained selected after the view mutation.
    /// </summary>
    internal bool RemainsSelected { get; }

    /// <summary>
    /// Gets the number of items remaining in the section root after the view mutation.
    /// </summary>
    internal int RemainingItemCount { get; }

    /// <summary>
    /// Determines whether an empty-section navigation is allowed for the captured state.
    /// </summary>
    /// <param name="emptySection">The section reported empty by the mutation owner.</param>
    /// <param name="section">The section represented by the current compiled route.</param>
    /// <returns><see langword="true"/> only when the matching section was initially and finally selected and is empty.</returns>
    internal bool ShouldNavigateToEmptySection(
        PackageCatalogSection? emptySection,
        PackageCatalogSection section)
        => emptySection == section
            && InitiallySelected
            && RemainsSelected
            && RemainingItemCount == 0;
}

/// <summary>
/// Applies pending-package mutation presentation and preserves its failure contract.
/// </summary>
internal sealed class MainWindowPendingPackageMutationViewTerminal
{
    private readonly IUiDialogService dialogs;
    private readonly Func<bool> isSectionRootSelected;
    private readonly Func<int> getSectionRootItemCount;
    private readonly Func<MainViewUpdateMode, Task<bool>> navigateInstallAsync;

    /// <summary>
    /// Initializes the pending-package mutation view terminal.
    /// </summary>
    /// <param name="isSectionRootSelected">Reads the current UI selection state of the pending-package section root.</param>
    /// <param name="getSectionRootItemCount">Reads the current UI item count of the pending-package section root.</param>
    /// <param name="navigateInstallAsync">Navigates to the requested install view when the applied state is empty.</param>
    /// <param name="dialogs">Optional terminal-report dialog boundary.</param>
    internal MainWindowPendingPackageMutationViewTerminal(
        Func<bool> isSectionRootSelected,
        Func<int> getSectionRootItemCount,
        Func<MainViewUpdateMode, Task<bool>> navigateInstallAsync,
        IUiDialogService dialogs = null)
    {
        this.dialogs = dialogs;
        this.isSectionRootSelected = isSectionRootSelected
            ?? throw new ArgumentNullException(nameof(isSectionRootSelected));
        this.getSectionRootItemCount = getSectionRootItemCount
            ?? throw new ArgumentNullException(nameof(getSectionRootItemCount));
        this.navigateInstallAsync = navigateInstallAsync
            ?? throw new ArgumentNullException(nameof(navigateInstallAsync));
    }

    /// <summary>
    /// Applies one pending-package mutation result to the already-owned UI view.
    /// </summary>
    /// <param name="result">The mutation result; a <see langword="null"/> result is a no-op.</param>
    /// <param name="section">The package-catalog section represented by the route.</param>
    /// <param name="emptySectionMode">The navigation mode used when the applied section is empty.</param>
    /// <param name="routeName">The route name used for existing failure logging.</param>
    /// <param name="applyView">The caller-owned UI view and selection application action.</param>
    /// <returns>A task that completes after view application, optional navigation, and failure propagation.</returns>
    internal async Task ApplyAsync(
        PendingPackageMutationResult result,
        PackageCatalogSection section,
        MainViewUpdateMode emptySectionMode,
        string routeName,
        Action applyView = null)
    {
        if (result == null)
        {
            return;
        }
        ArgumentNullException.ThrowIfNull(routeName);

        bool initiallySelected = result.ShouldApplyView && isSectionRootSelected();
        MainWindowPendingPackageMutationAppliedViewState appliedViewState = null;
        Exception applyFailure = null;
        if (result.ShouldApplyView)
        {
            try
            {
                applyView?.Invoke();
                appliedViewState = new MainWindowPendingPackageMutationAppliedViewState(
                    initiallySelected,
                    isSectionRootSelected(),
                    getSectionRootItemCount());
            }
            catch (Exception exception)
            {
                applyFailure = exception;
            }
        }

        Exception navigationFailure = null;
        if (applyFailure == null
            && result.ShouldApplyView
            && appliedViewState?.ShouldNavigateToEmptySection(result.EmptySection, section) == true)
        {
            try
            {
                await navigateInstallAsync(emptySectionMode).LoggingAndPropagate(routeName);
            }
            catch (Exception exception)
            {
                navigationFailure = exception;
            }
        }

        await FileDbMutationReport.ShowAsync(dialogs,
            BeMusicSeeker.Properties.Resources.Install, result.MutationReceipt, result.Failure);

        Exception mutationFailure = null;
        try
        {
            if (result.Failure != null
                && !ReferenceEquals(result.Failure, result.MutationReceipt?.FinalizationFailure)
                && result.MutationReceipt?.Receipts.Any(receipt =>
                    ReferenceEquals(receipt.Failure, result.Failure)
                    || ReferenceEquals(receipt.FinalizationFailure, result.Failure)) != true)
            {
                await Task.FromException(result.Failure).LoggingAndPropagate(routeName);
            }
        }
        catch (Exception exception)
        {
            mutationFailure = exception;
        }

        var failures = new List<Exception>(capacity: 3);
        if (mutationFailure != null)
        {
            failures.Add(mutationFailure);
        }
        if (applyFailure != null)
        {
            failures.Add(applyFailure);
        }
        if (navigationFailure != null)
        {
            failures.Add(navigationFailure);
        }
        if (failures.Count == 1)
        {
            ExceptionDispatchInfo.Capture(failures[0]).Throw();
        }
        if (failures.Count > 1)
        {
            throw new AggregateException(failures);
        }
    }
}

/// <summary>
/// Owns repair and clearing requests for installed chart locations.
/// </summary>
internal sealed class MainWindowInstalledLocationRepairTerminal
{
    private readonly Func<RepairInstalledLocationRequest, Task> searchCorrect;
    private readonly Func<RepairInstalledLocationRequest, Task> clearCorrect;
    private readonly Func<RepairInstalledLocationRequest, Task> fixInstalledLocations;

    internal MainWindowInstalledLocationRepairTerminal(
        Func<RepairInstalledLocationRequest, Task> searchCorrect,
        Func<RepairInstalledLocationRequest, Task> clearCorrect,
        Func<RepairInstalledLocationRequest, Task> fixInstalledLocations)
    {
        this.searchCorrect = searchCorrect ?? throw new ArgumentNullException(nameof(searchCorrect));
        this.clearCorrect = clearCorrect ?? throw new ArgumentNullException(nameof(clearCorrect));
        this.fixInstalledLocations = fixInstalledLocations ?? throw new ArgumentNullException(nameof(fixInstalledLocations));
    }

    internal Task SearchCorrectAsync(RepairInstalledLocationRequest request)
        => searchCorrect(request ?? throw new ArgumentNullException(nameof(request)));

    internal Task ClearCorrectAsync(RepairInstalledLocationRequest request)
        => clearCorrect(request ?? throw new ArgumentNullException(nameof(request)));

    internal Task FixInstalledLocationsAsync(RepairInstalledLocationRequest request)
        => fixInstalledLocations(request ?? throw new ArgumentNullException(nameof(request)));

    internal static MainWindowInstalledLocationRepairTerminal Create(MainWindowViewModel viewModel)
    {
        ArgumentNullException.ThrowIfNull(viewModel);
        return new MainWindowInstalledLocationRepairTerminal(
            viewModel.PendingPackages.SearchCorrectAsync,
            viewModel.PendingPackages.ClearCorrectAsync,
            viewModel.PendingPackages.FixInstalledLocationsAsync);
    }
}

/// <summary>
/// Owns the three pending-package maintenance commands shown under the pending tree.
/// </summary>
internal sealed class MainWindowPendingBulkMaintenanceTerminal
{
    private readonly Func<Task> deleteInstalledOnlySources;
    private readonly Func<Task> renameZeroNoteCharts;
    private readonly Func<Task> overwriteInstalledOnlyResources;

    internal MainWindowPendingBulkMaintenanceTerminal(
        Func<Task> deleteInstalledOnlySources,
        Func<Task> renameZeroNoteCharts,
        Func<Task> overwriteInstalledOnlyResources)
    {
        this.deleteInstalledOnlySources = deleteInstalledOnlySources ?? throw new ArgumentNullException(nameof(deleteInstalledOnlySources));
        this.renameZeroNoteCharts = renameZeroNoteCharts ?? throw new ArgumentNullException(nameof(renameZeroNoteCharts));
        this.overwriteInstalledOnlyResources = overwriteInstalledOnlyResources ?? throw new ArgumentNullException(nameof(overwriteInstalledOnlyResources));
    }

    internal Task DeleteInstalledOnlySourcesAsync() => deleteInstalledOnlySources();

    internal Task RenameZeroNoteChartsAsync() => renameZeroNoteCharts();

    internal Task OverwriteInstalledOnlyResourcesAsync() => overwriteInstalledOnlyResources();

    internal static MainWindowPendingBulkMaintenanceTerminal Create(MainWindowViewModel viewModel)
    {
        ArgumentNullException.ThrowIfNull(viewModel);
        return new MainWindowPendingBulkMaintenanceTerminal(
            viewModel.PendingPackages.DeleteInstalledOnlyPendingPackageSourcesAsync,
            viewModel.PendingPackages.RenamePendingZeroNoteChartsAsync,
            viewModel.PendingPackages.OverwriteInstalledOnlyPendingPackageResourcesAsync);
    }
}

/// <summary>
/// Owns the main chart table edit lifecycle without exposing the shell as a callback host.
/// </summary>
internal sealed class MainWindowMainChartCellEditTerminal
{
    private readonly Func<object, string, bool> tryBegin;
    private readonly Action<object, string> notifyStarted;
    private readonly Action<object, string, string, bool> complete;

    internal MainWindowMainChartCellEditTerminal(
        Func<object, string, bool> tryBegin,
        Action<object, string> notifyStarted,
        Action<object, string, string, bool> complete)
    {
        this.tryBegin = tryBegin ?? throw new ArgumentNullException(nameof(tryBegin));
        this.notifyStarted = notifyStarted ?? throw new ArgumentNullException(nameof(notifyStarted));
        this.complete = complete ?? throw new ArgumentNullException(nameof(complete));
    }

    internal bool TryBegin(object row, string propertyName)
        => tryBegin(row, propertyName);

    internal void NotifyStarted(object row, string propertyName)
        => notifyStarted(row, propertyName);

    internal void Complete(object row, string propertyName, string text, bool commit)
        => complete(row, propertyName, text, commit);

    internal static MainWindowMainChartCellEditTerminal Create(MainWindowViewModel viewModel)
    {
        ArgumentNullException.ThrowIfNull(viewModel);
        return new MainWindowMainChartCellEditTerminal(
            viewModel.MainChartList.TryBeginCellEdit,
            viewModel.MainChartList.NotifyCellEditStarted,
            viewModel.MainChartList.RequestCellEditEnded);
    }
}
