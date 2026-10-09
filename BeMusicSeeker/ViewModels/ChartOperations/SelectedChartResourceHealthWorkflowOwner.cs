using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using BeMusicSeeker.Models;
using BeMusicSeeker.Models.BmsLibraryInternal;
using BeMusicSeeker.Models.Utils;
using BeMusicSeeker.Views.Dialogs;
using MessageBoxButton = BeMusicSeeker.Models.UiDialogButton;
using MessageBoxImage = BeMusicSeeker.Models.UiDialogIcon;
using MessageBoxResult = BeMusicSeeker.Models.UiDialogDefaultResult;

namespace BeMusicSeeker.ViewModels;

internal interface ISelectedChartResourceHealthStore
{
    MaintenanceWorkflowResult RescanResourceHealthCharts(
        BMSLibrary library,
        IReadOnlyList<ChartFile> charts, LibraryFileMutationCapability capability);

    void SetChartResourceWarningsIgnored(
        BMSLibrary library,
        IReadOnlyList<ChartFile> charts,
        bool unset, LibraryFileMutationCapability capability);
}

internal sealed class SelectedChartResourceHealthWorkflowResult
{
    private SelectedChartResourceHealthWorkflowResult(bool succeeded, bool canceled, Exception failure, bool busy = false)
    {
        Succeeded = succeeded;
        Canceled = canceled;
        Failure = failure;
        Busy = busy;
    }

    internal bool Succeeded { get; }

    internal bool Canceled { get; }

    internal bool Busy { get; }

    internal static SelectedChartResourceHealthWorkflowResult RejectedBusy { get; } = new(false, false, null, busy: true);

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
    private readonly ChartFileOperationSynchronizer operationAdmission;

    internal event EventHandler RescanCompleted;

    /// <summary>入力捕捉前のL受付と、生存権限を実モデルへ転送する保存窓口を接続します。</summary>
    internal SelectedChartResourceHealthWorkflowOwner(
        Func<BMSLibrary> libraryProvider,
        IUiDialogService dialogs,
        ISelectedChartResourceHealthStore store = null, ChartFileOperationSynchronizer operationAdmission = null)
    {
        this.libraryProvider = libraryProvider ?? throw new ArgumentNullException(nameof(libraryProvider));
        this.dialogs = dialogs ?? throw new ArgumentNullException(nameof(dialogs));
        this.store = store ?? new BmsLibrarySelectedChartResourceHealthStore();
        this.operationAdmission = operationAdmission;
    }

    /// <summary>同じL権限で入力の再検査・保存・必須参照通知まで待ちます。未受理Busyと取消を区別します。</summary>
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
            ChartFileOperationSynchronizer gate = operationAdmission ?? library.OperationAdmission;
            if (!gate.TryEnter(out IDisposable admission))
            {
                await ShowBlockedWarningAsync();
                return SelectedChartResourceHealthWorkflowResult.RejectedBusy;
            }
            using IDisposable accepted = admission;
            using LibraryFileMutationCapability capability = gate.CreateMutationCapability(admission);
            MaintenanceWorkflowResult workflowResult = await Task.Run(
                () => store.RescanResourceHealthCharts(library, request.Charts, capability));
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

    /// <summary>警告無視変更をLで非待機受理し、モデル保存と参照通知まで同じ権限を保持します。</summary>
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
            BMSLibrary library = RequireLibrary();
            ChartFileOperationSynchronizer gate = operationAdmission ?? library.OperationAdmission;
            if (!gate.TryEnter(out IDisposable admission))
            {
                ShowBlockedWarningAsync().ObserveFault("Resource warning Busy notification");
                return SelectedChartResourceHealthWorkflowResult.RejectedBusy;
            }
            using IDisposable accepted = admission;
            using LibraryFileMutationCapability capability = gate.CreateMutationCapability(admission);
            store.SetChartResourceWarningsIgnored(library, request.Charts, unset, capability);
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
            BeMusicSeeker.Properties.Resources.Warn_LibraryOperationBusy,
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
        IReadOnlyList<ChartFile> charts, LibraryFileMutationCapability capability)
    {
        return library.RescanResourceHealthCharts(charts, capability: capability);
    }

    public void SetChartResourceWarningsIgnored(
        BMSLibrary library,
        IReadOnlyList<ChartFile> charts,
        bool unset, LibraryFileMutationCapability capability)
    {
        library.SetChartResourceWarningsIgnored(charts, unset, capability);
    }
}
