using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.ExceptionServices;
using System.Threading.Tasks;
using System.Windows;
using BeMusicSeeker.Models;
using BeMusicSeeker.Models.BmsLibraryInternal;
using BeMusicSeeker.Views.Dialogs;

namespace BeMusicSeeker.ViewModels;

internal interface IInstallDestinationMutationPresentation
{
    void BeginActivity();

    void BeginRefreshSuppression();

    void EndRefreshSuppression();

    void EndActivity();

    void UpdateTransientStates(IEnumerable<ChartFile> charts);

    void InvalidateInstallDestinationSort();

    void RefreshIdentitySortKey();

    void RequestDisplayRefresh();
}

internal interface IInstallDestinationStore
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
}

internal sealed class InstallDestinationWorkflowOwner
{
    private readonly Func<BMSLibrary> libraryProvider;
    private readonly ChartFileOperationSynchronizer chartFileOperations;
    private readonly IInstallDestinationMutationPresentation presentation;
    private readonly IUiDialogService dialogs;
    private readonly IInstallDestinationStore store;

    internal InstallDestinationWorkflowOwner(
        Func<BMSLibrary> libraryProvider,
        ChartFileOperationSynchronizer chartFileOperations,
        IInstallDestinationMutationPresentation presentation,
        IUiDialogService dialogs,
        IInstallDestinationStore store = null)
    {
        this.libraryProvider = libraryProvider ?? throw new ArgumentNullException(nameof(libraryProvider));
        this.chartFileOperations = chartFileOperations ?? throw new ArgumentNullException(nameof(chartFileOperations));
        this.presentation = presentation ?? throw new ArgumentNullException(nameof(presentation));
        this.dialogs = dialogs ?? throw new ArgumentNullException(nameof(dialogs));
        this.store = store ?? new BmsLibraryInstallDestinationStore();
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
            presentation.InvalidateInstallDestinationSort();
            presentation.RefreshIdentitySortKey();
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
                presentation.UpdateTransientStates(changedCharts);
            });
            presentation.InvalidateInstallDestinationSort();
            presentation.RefreshIdentitySortKey();
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
                presentation.UpdateTransientStates(changedCharts);
            }, requiresLibrary: false);
            presentation.InvalidateInstallDestinationSort();
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
                presentation.UpdateTransientStates(changedCharts);
            }))
            {
                presentation.InvalidateInstallDestinationSort();
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
            presentation.UpdateTransientStates(changedCharts);
            presentation.InvalidateInstallDestinationSort();
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
                presentation.UpdateTransientStates(changedCharts);
            }))
            {
                presentation.InvalidateInstallDestinationSort();
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
                presentation.UpdateTransientStates([changedChart]);
                presentation.InvalidateInstallDestinationSort();
            }
            presentation.RequestDisplayRefresh();
        });
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
        if (!ToConfirmationDecision(result))
        {
            return;
        }
        await Task.Run(operation);
    }

    private static bool ToConfirmationDecision(UiDialogResult result)
    {
        if (result == null)
        {
            throw new InvalidOperationException("Merge destination confirmation returned no result.");
        }
        return result.Status switch
        {
            UiDialogStatus.Accepted => true,
            UiDialogStatus.Rejected or UiDialogStatus.CancelledByUser => false,
            UiDialogStatus.ClosedByUser => result.MessageBoxResult is MessageBoxResult.OK or MessageBoxResult.Yes,
            _ => throw new InvalidOperationException(
                "Merge destination confirmation could not be displayed (" + result.Status + ").",
                result.Exception),
        };
    }

    private bool Execute(Action<BMSLibrary> mutation, bool requiresLibrary = true)
    {
        BMSLibrary library = libraryProvider();
        if (requiresLibrary && library == null)
        {
            return false;
        }
        BMSLibrary.OperationDialogScope dialogScope = null;
        IDisposable operationGate = null;
        bool activityStarted = false;
        bool suppressionStarted = false;
        var failures = new List<ExceptionDispatchInfo>();
        try
        {
            dialogScope = library?.BeginOperationDialogScope();
            activityStarted = true;
            presentation.BeginActivity();
            operationGate = chartFileOperations.Enter();
            suppressionStarted = true;
            presentation.BeginRefreshSuppression();
            mutation(library);
        }
        catch (Exception ex)
        {
            failures.Add(ExceptionDispatchInfo.Capture(ex));
        }
        finally
        {
            if (suppressionStarted)
            {
                CaptureCleanupFailure(presentation.EndRefreshSuppression, failures);
            }
            if (operationGate != null)
            {
                CaptureCleanupFailure(operationGate.Dispose, failures);
            }
            if (activityStarted)
            {
                CaptureCleanupFailure(presentation.EndActivity, failures);
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

internal sealed class BmsLibraryInstallDestinationStore : IInstallDestinationStore
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
