using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.ExceptionServices;
using System.Threading.Tasks;
using System.Windows;
using BeMusicSeeker.Models;
using BeMusicSeeker.Views.Dialogs;
using MessageBoxButton = BeMusicSeeker.Models.UiDialogButton;
using MessageBoxImage = BeMusicSeeker.Models.UiDialogIcon;
using MessageBoxResult = BeMusicSeeker.Models.UiDialogDefaultResult;

namespace BeMusicSeeker.ViewModels;

internal enum PackageCatalogMutationPhase
{
    RefreshSuppressionStarted,
    RefreshSuppressionEnded
}

internal sealed class PackageCatalogMutationPhaseEventArgs : EventArgs
{
    internal PackageCatalogMutationPhaseEventArgs(PackageCatalogMutationPhase phase)
    {
        Phase = phase;
    }

    internal PackageCatalogMutationPhase Phase { get; }
}

internal interface IPackageCatalogStore
{
    void RemoveAll(BMSLibrary library, PackageCatalogSection section);

    void RemovePackages(
        BMSLibrary library,
        PackageCatalogSection section,
        IReadOnlyList<ChartPackage> packages);

    IReadOnlyList<ChartPackage> ResolvePackages(
        BMSLibrary library,
        PackageCatalogSection section,
        IReadOnlyList<ChartOperationTarget> targets);

    bool IsSectionEmpty(BMSLibrary library, PackageCatalogSection section);
}

internal sealed class PackageCatalogMutationResult
{
    private PackageCatalogMutationResult(
        bool succeeded,
        Exception failure,
        bool shouldApplyView,
        PackageCatalogSection? emptySection)
    {
        Succeeded = succeeded;
        Failure = failure;
        ShouldApplyView = shouldApplyView;
        EmptySection = emptySection;
    }

    internal bool Succeeded { get; }

    internal Exception Failure { get; }

    internal bool ShouldApplyView { get; }

    internal PackageCatalogSection? EmptySection { get; }

    internal static PackageCatalogMutationResult Completed { get; } = new(true, null, true, null);

    internal static PackageCatalogMutationResult Rejected { get; } = new(false, null, false, null);

    internal static PackageCatalogMutationResult CompletedFor(PackageCatalogSection? emptySection)
    {
        return new PackageCatalogMutationResult(true, null, true, emptySection);
    }

    internal static PackageCatalogMutationResult Failed(
        Exception failure,
        PackageCatalogSection? emptySection = null)
    {
        return new PackageCatalogMutationResult(
            false,
            failure ?? throw new ArgumentNullException(nameof(failure)),
            true,
            emptySection);
    }

    internal static PackageCatalogMutationResult FailedBeforeMutation(Exception failure)
    {
        return new PackageCatalogMutationResult(
            false,
            failure ?? throw new ArgumentNullException(nameof(failure)),
            false,
            null);
    }
}

internal sealed class PackageCatalogWorkflowOwner
{
    private readonly Func<BMSLibrary> libraryProvider;
    private readonly ChartFileOperationSynchronizer chartFileOperations;
    private readonly ChartMutationActivityOwner chartMutationActivity;
    private readonly IUiDialogService dialogs;
    private readonly Func<Func<PackageCatalogMutationResult>, Task<PackageCatalogMutationResult>> backgroundMutationRunner;
    private readonly IPackageCatalogStore store;

    internal event EventHandler<PackageCatalogMutationPhaseEventArgs> MutationPhasePublished;

    /// <summary>
    /// Creates a package catalog workflow owner with an explicit background mutation runner.
    /// </summary>
    /// <param name="backgroundMutationRunner">
    /// Schedules a mutation and returns its result task while preserving mutation exceptions.
    /// </param>
    internal PackageCatalogWorkflowOwner(
        Func<BMSLibrary> libraryProvider,
        ChartFileOperationSynchronizer chartFileOperations,
        ChartMutationActivityOwner chartMutationActivity,
        IUiDialogService dialogs,
        Func<Func<PackageCatalogMutationResult>, Task<PackageCatalogMutationResult>> backgroundMutationRunner,
        IPackageCatalogStore store = null)
    {
        this.libraryProvider = libraryProvider ?? throw new ArgumentNullException(nameof(libraryProvider));
        this.chartFileOperations = chartFileOperations ?? throw new ArgumentNullException(nameof(chartFileOperations));
        this.chartMutationActivity = chartMutationActivity ?? throw new ArgumentNullException(nameof(chartMutationActivity));
        this.dialogs = dialogs ?? throw new ArgumentNullException(nameof(dialogs));
        this.backgroundMutationRunner = backgroundMutationRunner
            ?? throw new ArgumentNullException(nameof(backgroundMutationRunner));
        this.store = store ?? new BmsLibraryPackageCatalogStore();
    }

    internal Task<PackageCatalogMutationResult> ClearAllAsync(PackageCatalogSection section)
    {
        ValidateSection(section);
        if (!TryEnterOperation(section, out IDisposable operationGate))
        {
            return Task.FromResult(PackageCatalogMutationResult.Rejected);
        }
        PackageCatalogMutationResult confirmation = ConfirmRemoval(
            section == PackageCatalogSection.Pending
                ? BeMusicSeeker.Properties.Resources.Msg_clear_all_pendings
                : BeMusicSeeker.Properties.Resources.Msg_clear_all_installed,
            "Package catalog clear-all confirmation");
        if (!confirmation.Succeeded)
        {
            operationGate.Dispose();
            return Task.FromResult(confirmation);
        }
        return ScheduleWithAcquiredGate(
            operationGate,
            () => Execute(section, library => store.RemoveAll(library, section), operationGate));
    }

    internal Task<PackageCatalogMutationResult> RemovePackageAsync(
        PackageCatalogSection section,
        ChartPackage package)
    {
        ValidateSection(section);
        if (package == null)
        {
            throw new ArgumentNullException(nameof(package));
        }
        if (!TryEnterOperation(section, out IDisposable operationGate))
        {
            return Task.FromResult(PackageCatalogMutationResult.Rejected);
        }
        if (section == PackageCatalogSection.Pending)
        {
            PackageCatalogMutationResult confirmation = ConfirmRemoval(
                BeMusicSeeker.Properties.Resources.Msg_clear_pendings
                + Environment.NewLine
                + Environment.NewLine
                + (package.DisplayTitle ?? string.Empty),
                "Pending package catalog entry removal confirmation");
            if (!confirmation.Succeeded)
            {
                operationGate.Dispose();
                return Task.FromResult(confirmation);
            }
        }
        return ScheduleWithAcquiredGate(
            operationGate,
            () => Execute(section, library => store.RemovePackages(library, section, [package]), operationGate));
    }

    internal Task<PackageCatalogMutationResult> RemoveSelectionAsync(PackageCatalogRemovalRequest request)
    {
        if (request == null)
        {
            throw new ArgumentNullException(nameof(request));
        }
        ValidateSection(request.Section);
        if (!TryEnterOperation(request.Section, out IDisposable operationGate))
        {
            return Task.FromResult(PackageCatalogMutationResult.Rejected);
        }
        PackageCatalogMutationResult confirmation = ConfirmRemoval(
            request.IsPending
                ? BeMusicSeeker.Properties.Resources.Msg_clear_selected_pendings
                : BeMusicSeeker.Properties.Resources.Msg_clear_selected_installed,
            "Selected package catalog entry removal confirmation");
        if (!confirmation.Succeeded)
        {
            operationGate.Dispose();
            return Task.FromResult(confirmation);
        }
        return ScheduleWithAcquiredGate(
            operationGate,
            () => Execute(request.Section, library =>
            {
                IReadOnlyList<ChartPackage> packages = store.ResolvePackages(
                    library,
                    request.Section,
                    request.Targets);
                store.RemovePackages(library, request.Section, packages);
            }, operationGate));
    }

    private PackageCatalogMutationResult Execute(
        PackageCatalogSection section,
        Action<BMSLibrary> mutation,
        IDisposable acquiredOperationGate = null)
    {
        IDisposable operationGate = acquiredOperationGate;
        if (operationGate == null
            && !TryEnterOperation(section, out operationGate))
        {
            return PackageCatalogMutationResult.Rejected;
        }
        BMSLibrary library;
        try
        {
            library = libraryProvider();
        }
        catch
        {
            operationGate.Dispose();
            throw;
        }
        if (library == null)
        {
            operationGate.Dispose();
            return PackageCatalogMutationResult.Completed;
        }
        BMSLibrary.OperationDialogScope dialogScope = null;
        IDisposable activityLease = null;
        bool suppressionStarted = false;
        bool mutationAttempted = false;
        PackageCatalogSection? emptySection = null;
        var failures = new List<ExceptionDispatchInfo>();
        try
        {
            dialogScope = library.BeginOperationDialogScope();
            activityLease = chartMutationActivity.Enter();
            suppressionStarted = true;
            PublishMutationPhase(PackageCatalogMutationPhase.RefreshSuppressionStarted);
            mutationAttempted = true;
            try
            {
                mutation(library);
            }
            catch (Exception ex)
            {
                failures.Add(ExceptionDispatchInfo.Capture(ex));
            }
        }
        catch (Exception ex)
        {
            failures.Add(ExceptionDispatchInfo.Capture(ex));
        }
        finally
        {
            if (mutationAttempted)
            {
                CaptureCleanupFailure(
                    () =>
                    {
                        if (store.IsSectionEmpty(library, section))
                        {
                            emptySection = section;
                        }
                    },
                    failures);
            }
            if (suppressionStarted)
            {
                CaptureCleanupFailure(
                    () => PublishMutationPhase(PackageCatalogMutationPhase.RefreshSuppressionEnded),
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
        return failures.Count switch
        {
            0 => PackageCatalogMutationResult.CompletedFor(emptySection),
            1 => PackageCatalogMutationResult.Failed(failures[0].SourceException, emptySection),
            _ => PackageCatalogMutationResult.Failed(
                new AggregateException(failures.Select(failure => failure.SourceException)),
                emptySection),
        };
    }

    private Task<PackageCatalogMutationResult> ScheduleWithAcquiredGate(
        IDisposable operationGate,
        Func<PackageCatalogMutationResult> mutation)
    {
        try
        {
            Task<PackageCatalogMutationResult> task = backgroundMutationRunner(mutation);
            if (task == null)
            {
                operationGate.Dispose();
                return Task.FromResult(PackageCatalogMutationResult.FailedBeforeMutation(
                    new InvalidOperationException("Package catalog mutation runner returned no task.")));
            }
            return task;
        }
        catch (Exception exception)
        {
            operationGate.Dispose();
            return Task.FromResult(PackageCatalogMutationResult.FailedBeforeMutation(exception));
        }
    }

    private void PublishMutationPhase(PackageCatalogMutationPhase phase)
    {
        MutationPhasePublished?.Invoke(
            this,
            new PackageCatalogMutationPhaseEventArgs(phase));
    }

    private bool TryEnterOperation(
        PackageCatalogSection section,
        out IDisposable operationGate)
    {
        BMSLibrary library = libraryProvider();
        if (section == PackageCatalogSection.Pending
            && library?.IsPendingOperationAdmissionReady == true)
        {
            return library.TryEnterPendingOperation(out operationGate);
        }
        return chartFileOperations.TryEnter(out operationGate);
    }

    private PackageCatalogMutationResult ConfirmRemoval(string message, string routeName)
    {
        try
        {
            UiDialogResult result = dialogs.ConfirmAsync(new UiConfirmationRequest(
                message,
                BeMusicSeeker.Properties.Resources.Confirm,
                MessageBoxButton.OKCancel,
                MessageBoxImage.Question,
                MessageBoxResult.Cancel)).GetAwaiter().GetResult();
            if (result == null)
            {
                return PackageCatalogMutationResult.FailedBeforeMutation(
                    new InvalidOperationException(routeName + " returned no result."));
            }
            return result.Status switch
            {
                UiDialogStatus.Accepted => PackageCatalogMutationResult.Completed,
                UiDialogStatus.Rejected or UiDialogStatus.CancelledByUser => PackageCatalogMutationResult.Rejected,
                UiDialogStatus.ClosedByUser => result.IsPositive
                    ? PackageCatalogMutationResult.Completed
                    : PackageCatalogMutationResult.Rejected,
                _ => PackageCatalogMutationResult.FailedBeforeMutation(
                    new InvalidOperationException(
                        routeName + " could not be displayed (" + result.Status + ").",
                        result.Exception)),
            };
        }
        catch (Exception exception)
        {
            return PackageCatalogMutationResult.FailedBeforeMutation(exception);
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

    private static void ValidateSection(PackageCatalogSection section)
    {
        if (section != PackageCatalogSection.Pending
            && section != PackageCatalogSection.Installed)
        {
            throw new ArgumentOutOfRangeException(nameof(section), section, "Unsupported package catalog section.");
        }
    }
}

internal sealed class BmsLibraryPackageCatalogStore : IPackageCatalogStore
{
    public void RemoveAll(BMSLibrary library, PackageCatalogSection section)
    {
        if (section == PackageCatalogSection.Pending)
        {
            library.RemovePendingPackagesAll();
        }
        else
        {
            library.RemoveInstalledPackageRecordsAll();
        }
    }

    public void RemovePackages(
        BMSLibrary library,
        PackageCatalogSection section,
        IReadOnlyList<ChartPackage> packages)
    {
        if (section == PackageCatalogSection.Pending)
        {
            library.RemovePendingPackages(packages);
        }
        else
        {
            library.RemoveInstalledPackageRecords(packages);
        }
    }

    public IReadOnlyList<ChartPackage> ResolvePackages(
        BMSLibrary library,
        PackageCatalogSection section,
        IReadOnlyList<ChartOperationTarget> targets)
    {
        IEnumerable<ChartPackage> source = section == PackageCatalogSection.Pending
            ? library.ChartPackagesPending
            : library.ChartPackagesInstalled;
        source ??= Enumerable.Empty<ChartPackage>();
        return [.. targets
            .Select(target => source.FirstOrDefault(package => ContainsChartTarget(package, target)))
            .Where(package => package != null)
            .Distinct()];
    }

    public bool IsSectionEmpty(BMSLibrary library, PackageCatalogSection section)
    {
        if (library == null)
        {
            return false;
        }
        return section == PackageCatalogSection.Pending
            ? library.ChartPackagesPending?.Count == 0
            : library.ChartPackagesInstalled?.Count == 0;
    }

    private static bool ContainsChartTarget(ChartPackage package, ChartOperationTarget target)
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
}
