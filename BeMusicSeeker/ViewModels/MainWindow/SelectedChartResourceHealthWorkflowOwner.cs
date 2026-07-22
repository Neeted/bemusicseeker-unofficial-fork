using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using BeMusicSeeker.Models;
using BeMusicSeeker.Models.BmsLibraryInternal;
using BeMusicSeeker.Views.Dialogs;

namespace BeMusicSeeker.ViewModels;

internal interface ISelectedChartResourceHealthStore
{
    MaintenanceWorkflowResult RescanResourceHealthCharts(
        BMSLibrary library,
        IReadOnlyList<ChartFile> charts);

    void SetChartResourceWarningsIgnored(
        BMSLibrary library,
        IReadOnlyList<ChartFile> charts,
        bool unset);
}

internal sealed class SelectedChartResourceHealthWorkflowResult
{
    private SelectedChartResourceHealthWorkflowResult(bool succeeded, bool canceled, Exception failure)
    {
        Succeeded = succeeded;
        Canceled = canceled;
        Failure = failure;
    }

    internal bool Succeeded { get; }

    internal bool Canceled { get; }

    internal Exception Failure { get; }

    internal static SelectedChartResourceHealthWorkflowResult Completed { get; } =
        new(true, false, null);

    internal static SelectedChartResourceHealthWorkflowResult CanceledByLibrary { get; } =
        new(false, true, null);

    internal static SelectedChartResourceHealthWorkflowResult Failed(Exception failure)
    {
        return new SelectedChartResourceHealthWorkflowResult(
            false,
            false,
            failure ?? throw new ArgumentNullException(nameof(failure)));
    }
}

internal sealed class SelectedChartResourceHealthWorkflowOwner
{
    private readonly Func<BMSLibrary> libraryProvider;
    private readonly IUiDialogService dialogs;
    private readonly ISelectedChartResourceHealthStore store;

    internal event EventHandler RescanCompleted;

    internal SelectedChartResourceHealthWorkflowOwner(
        Func<BMSLibrary> libraryProvider,
        IUiDialogService dialogs,
        ISelectedChartResourceHealthStore store = null)
    {
        this.libraryProvider = libraryProvider ?? throw new ArgumentNullException(nameof(libraryProvider));
        this.dialogs = dialogs ?? throw new ArgumentNullException(nameof(dialogs));
        this.store = store ?? new BmsLibrarySelectedChartResourceHealthStore();
    }

    internal async Task<SelectedChartResourceHealthWorkflowResult> RescanAsync(
        ChartResourceHealthRequest request)
    {
        if (request?.HasTargets != true)
        {
            return SelectedChartResourceHealthWorkflowResult.Completed;
        }

        try
        {
            BMSLibrary library = RequireLibrary();
            MaintenanceWorkflowResult workflowResult = await Task.Run(
                () => store.RescanResourceHealthCharts(library, request.Charts));
            if (workflowResult == null)
            {
                return SelectedChartResourceHealthWorkflowResult.Failed(
                    new InvalidOperationException("Resource health rescan returned no result."));
            }
            if (workflowResult.Canceled)
            {
                await ShowBlockedWarningAsync();
                return SelectedChartResourceHealthWorkflowResult.CanceledByLibrary;
            }

            RescanCompleted?.Invoke(this, EventArgs.Empty);
            return SelectedChartResourceHealthWorkflowResult.Completed;
        }
        catch (Exception ex)
        {
            return SelectedChartResourceHealthWorkflowResult.Failed(ex);
        }
    }

    internal SelectedChartResourceHealthWorkflowResult SetWarningsIgnored(
        ChartResourceHealthRequest request,
        bool unset = false)
    {
        if (request?.HasTargets != true)
        {
            return SelectedChartResourceHealthWorkflowResult.Completed;
        }

        try
        {
            store.SetChartResourceWarningsIgnored(
                RequireLibrary(),
                request.Charts,
                unset);
            return SelectedChartResourceHealthWorkflowResult.Completed;
        }
        catch (Exception ex)
        {
            return SelectedChartResourceHealthWorkflowResult.Failed(ex);
        }
    }

    private async Task ShowBlockedWarningAsync()
    {
        UiDialogResult result = await dialogs.ShowMessageAsync(new UiMessageRequest(
            BeMusicSeeker.Properties.Resources.Warn_Lr2SongDbSyncRunning,
            BeMusicSeeker.Properties.Resources.Warning,
            MessageBoxButton.OK,
            MessageBoxImage.Exclamation,
            MessageBoxResult.OK));
        EnsureMessageWasShown(result, "Resource health rescan blocked notification");
    }

    private BMSLibrary RequireLibrary()
    {
        return libraryProvider()
            ?? throw new InvalidOperationException("Selected chart resource-health library is not available.");
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
}

internal sealed class BmsLibrarySelectedChartResourceHealthStore : ISelectedChartResourceHealthStore
{
    public MaintenanceWorkflowResult RescanResourceHealthCharts(
        BMSLibrary library,
        IReadOnlyList<ChartFile> charts)
    {
        return library.RescanResourceHealthCharts(charts);
    }

    public void SetChartResourceWarningsIgnored(
        BMSLibrary library,
        IReadOnlyList<ChartFile> charts,
        bool unset)
    {
        library.SetChartResourceWarningsIgnored(charts, unset);
    }
}
