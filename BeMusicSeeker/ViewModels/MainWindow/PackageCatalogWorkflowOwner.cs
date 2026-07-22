using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.ExceptionServices;
using System.Threading.Tasks;
using System.Windows;
using BeMusicSeeker.Models;
using BeMusicSeeker.Views.Dialogs;

namespace BeMusicSeeker.ViewModels;

internal interface IPackageCatalogMutationPresentation
{
    void BeginActivity();

    void BeginRefreshSuppression();

    void EndRefreshSuppression();

    void EndActivity();
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
}

internal sealed class PackageCatalogMutationResult
{
    private PackageCatalogMutationResult(bool succeeded, Exception failure, bool shouldApplyView)
    {
        Succeeded = succeeded;
        Failure = failure;
        ShouldApplyView = shouldApplyView;
    }

    internal bool Succeeded { get; }

    internal Exception Failure { get; }

    internal bool ShouldApplyView { get; }

    internal static PackageCatalogMutationResult Completed { get; } = new(true, null, true);

    internal static PackageCatalogMutationResult Rejected { get; } = new(false, null, false);

    internal static PackageCatalogMutationResult Failed(Exception failure)
    {
        return new PackageCatalogMutationResult(
            false,
            failure ?? throw new ArgumentNullException(nameof(failure)),
            true);
    }

    internal static PackageCatalogMutationResult FailedBeforeMutation(Exception failure)
    {
        return new PackageCatalogMutationResult(
            false,
            failure ?? throw new ArgumentNullException(nameof(failure)),
            false);
    }
}

internal sealed class PackageCatalogConfirmationResult
{
    private PackageCatalogConfirmationResult(bool accepted, Exception failure)
    {
        Accepted = accepted;
        Failure = failure;
    }

    internal bool Accepted { get; }

    internal Exception Failure { get; }

    internal static PackageCatalogConfirmationResult AcceptedResult { get; } = new(true, null);

    internal static PackageCatalogConfirmationResult Rejected { get; } = new(false, null);

    internal static PackageCatalogConfirmationResult Failed(Exception failure)
    {
        return new PackageCatalogConfirmationResult(
            false,
            failure ?? throw new ArgumentNullException(nameof(failure)));
    }
}

internal sealed class PackageCatalogWorkflowOwner
{
    private readonly Func<BMSLibrary> libraryProvider;
    private readonly ChartFileOperationSynchronizer chartFileOperations;
    private readonly IPackageCatalogMutationPresentation presentation;
    private readonly IUiDialogService dialogs;
    private readonly IPackageCatalogStore store;

    internal PackageCatalogWorkflowOwner(
        Func<BMSLibrary> libraryProvider,
        ChartFileOperationSynchronizer chartFileOperations,
        IPackageCatalogMutationPresentation presentation,
        IUiDialogService dialogs,
        IPackageCatalogStore store = null)
    {
        this.libraryProvider = libraryProvider ?? throw new ArgumentNullException(nameof(libraryProvider));
        this.chartFileOperations = chartFileOperations ?? throw new ArgumentNullException(nameof(chartFileOperations));
        this.presentation = presentation ?? throw new ArgumentNullException(nameof(presentation));
        this.dialogs = dialogs ?? throw new ArgumentNullException(nameof(dialogs));
        this.store = store ?? new BmsLibraryPackageCatalogStore();
    }

    internal PackageCatalogConfirmationResult ConfirmClearAll(PackageCatalogSection section)
    {
        ValidateSection(section);
        string message = section == PackageCatalogSection.Pending
            ? BeMusicSeeker.Properties.Resources.Msg_clear_all_pendings
            : BeMusicSeeker.Properties.Resources.Msg_clear_all_installed;
        return ConfirmRemoval(message, "Package catalog clear-all confirmation");
    }

    internal Task<PackageCatalogMutationResult> ClearAllAsync(PackageCatalogSection section)
    {
        ValidateSection(section);
        return Task.Run(() => Execute(library => store.RemoveAll(library, section)));
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
        if (section == PackageCatalogSection.Pending)
        {
            PackageCatalogConfirmationResult confirmation = ConfirmRemoval(
                BeMusicSeeker.Properties.Resources.Msg_clear_pendings
                + Environment.NewLine
                + Environment.NewLine
                + (package.DisplayTitle ?? string.Empty),
                "Pending package catalog entry removal confirmation");
            if (!confirmation.Accepted)
            {
                return Task.FromResult(
                    confirmation.Failure == null
                        ? PackageCatalogMutationResult.Rejected
                        : PackageCatalogMutationResult.FailedBeforeMutation(confirmation.Failure));
            }
        }
        return Task.Run(() => Execute(library => store.RemovePackages(library, section, [package])));
    }

    internal PackageCatalogConfirmationResult ConfirmRemoveSelection(PackageCatalogRemovalRequest request)
    {
        if (request == null)
        {
            throw new ArgumentNullException(nameof(request));
        }
        string message = request.IsPending
            ? BeMusicSeeker.Properties.Resources.Msg_clear_selected_pendings
            : BeMusicSeeker.Properties.Resources.Msg_clear_selected_installed;
        return ConfirmRemoval(message, "Selected package catalog entry removal confirmation");
    }

    internal Task<PackageCatalogMutationResult> RemoveSelectionAsync(PackageCatalogRemovalRequest request)
    {
        if (request == null)
        {
            throw new ArgumentNullException(nameof(request));
        }
        return Task.Run(() => Execute(library =>
        {
            IReadOnlyList<ChartPackage> packages = store.ResolvePackages(
                library,
                request.Section,
                request.Targets);
            store.RemovePackages(library, request.Section, packages);
        }));
    }

    private PackageCatalogMutationResult Execute(Action<BMSLibrary> mutation)
    {
        BMSLibrary library = libraryProvider();
        if (library == null)
        {
            return PackageCatalogMutationResult.Completed;
        }
        BMSLibrary.OperationDialogScope dialogScope = null;
        IDisposable operationGate = null;
        bool activityStarted = false;
        bool suppressionStarted = false;
        var failures = new List<ExceptionDispatchInfo>();
        try
        {
            dialogScope = library.BeginOperationDialogScope();
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
        return failures.Count switch
        {
            0 => PackageCatalogMutationResult.Completed,
            1 => PackageCatalogMutationResult.Failed(failures[0].SourceException),
            _ => PackageCatalogMutationResult.Failed(
                new AggregateException(failures.Select(failure => failure.SourceException))),
        };
    }

    private PackageCatalogConfirmationResult ConfirmRemoval(string message, string routeName)
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
                return PackageCatalogConfirmationResult.Failed(
                    new InvalidOperationException(routeName + " returned no result."));
            }
            return result.Status switch
            {
                UiDialogStatus.Accepted => PackageCatalogConfirmationResult.AcceptedResult,
                UiDialogStatus.Rejected or UiDialogStatus.CancelledByUser => PackageCatalogConfirmationResult.Rejected,
                UiDialogStatus.ClosedByUser => result.MessageBoxResult is MessageBoxResult.OK or MessageBoxResult.Yes
                    ? PackageCatalogConfirmationResult.AcceptedResult
                    : PackageCatalogConfirmationResult.Rejected,
                _ => PackageCatalogConfirmationResult.Failed(
                    new InvalidOperationException(
                        routeName + " could not be displayed (" + result.Status + ").",
                        result.Exception)),
            };
        }
        catch (Exception exception)
        {
            return PackageCatalogConfirmationResult.Failed(exception);
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

internal sealed class NoOpPackageCatalogMutationPresentation : IPackageCatalogMutationPresentation
{
    public void BeginActivity()
    {
    }

    public void BeginRefreshSuppression()
    {
    }

    public void EndRefreshSuppression()
    {
    }

    public void EndActivity()
    {
    }
}
