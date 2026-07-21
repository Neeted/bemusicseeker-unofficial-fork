using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using System.Windows;
using BeMusicSeeker.Models;
using BeMusicSeeker.Views.Dialogs;

namespace BeMusicSeeker.ViewModels;

internal sealed class ChartInfoParseFailureRemovalRequest
{
    internal ChartInfoParseFailureRemovalRequest(IEnumerable<string> md5s)
    {
        Md5s = Array.AsReadOnly(BMSLibrary.NormalizeChartInfoParseFailureMd5s(md5s));
    }

    internal IReadOnlyList<string> Md5s { get; }

    internal bool HasTargets => Md5s.Count > 0;
}

internal enum ChartInfoParseFailureRemovalStatus
{
    Removed,
    Rejected,
    NotStarted,
    Failed
}

internal sealed class ChartInfoParseFailureRemovalResult
{
    private ChartInfoParseFailureRemovalResult(
        ChartInfoParseFailureRemovalStatus status,
        bool accepted,
        Exception failure)
    {
        Status = status;
        Accepted = accepted;
        Failure = failure;
    }

    internal ChartInfoParseFailureRemovalStatus Status { get; }

    internal bool Accepted { get; }

    internal Exception Failure { get; }

    internal static ChartInfoParseFailureRemovalResult Removed { get; } =
        new(ChartInfoParseFailureRemovalStatus.Removed, true, null);

    internal static ChartInfoParseFailureRemovalResult Rejected { get; } =
        new(ChartInfoParseFailureRemovalStatus.Rejected, false, null);

    internal static ChartInfoParseFailureRemovalResult NotStarted { get; } =
        new(ChartInfoParseFailureRemovalStatus.NotStarted, false, null);

    internal static ChartInfoParseFailureRemovalResult Failed(Exception failure, bool accepted)
    {
        return new ChartInfoParseFailureRemovalResult(
            ChartInfoParseFailureRemovalStatus.Failed,
            accepted,
            failure ?? throw new ArgumentNullException(nameof(failure)));
    }
}

internal interface IChartInfoParseFailureRemovalStore
{
    void Remove(BMSLibrary library, IReadOnlyList<string> md5s);
}

internal sealed class ChartInfoParseFailureRemovalWorkflowOwner
{
    private readonly Func<BMSLibrary> libraryProvider;

    private readonly IUiDialogService dialogs;

    private readonly Func<Action, Task> schedule;

    private readonly IChartInfoParseFailureRemovalStore store;

    internal ChartInfoParseFailureRemovalWorkflowOwner(
        Func<BMSLibrary> libraryProvider,
        IUiDialogService dialogs,
        Func<Action, Task> schedule,
        IChartInfoParseFailureRemovalStore store = null)
    {
        this.libraryProvider = libraryProvider ?? throw new ArgumentNullException(nameof(libraryProvider));
        this.dialogs = dialogs ?? throw new ArgumentNullException(nameof(dialogs));
        this.schedule = schedule ?? throw new ArgumentNullException(nameof(schedule));
        this.store = store ?? new BmsLibraryChartInfoParseFailureRemovalStore();
    }

    internal async Task<ChartInfoParseFailureRemovalResult> RemoveAsync(
        ChartInfoParseFailureRemovalRequest request,
        Action acceptedCallback = null)
    {
        if (request?.HasTargets != true)
        {
            return ChartInfoParseFailureRemovalResult.NotStarted;
        }

        bool accepted = false;
        try
        {
            UiDialogResult confirmation = await dialogs.ConfirmAsync(new UiConfirmationRequest(
                BeMusicSeeker.Properties.Resources.Msg_remove_chart_info_parse_failure_record,
                BeMusicSeeker.Properties.Resources.Confirm,
                MessageBoxButton.OKCancel,
                MessageBoxImage.Question,
                MessageBoxResult.Cancel));
            if (confirmation == null)
            {
                throw new InvalidOperationException("Chart-info parse-failure removal confirmation returned no result.");
            }
            if (!confirmation.IsAccepted)
            {
                if (confirmation.Status is UiDialogStatus.Rejected
                    or UiDialogStatus.CancelledByUser
                    or UiDialogStatus.ClosedByUser)
                {
                    return ChartInfoParseFailureRemovalResult.Rejected;
                }
                throw new InvalidOperationException(
                    "Chart-info parse-failure removal confirmation could not be displayed ("
                        + confirmation.Status
                        + ").",
                    confirmation.Exception);
            }

            accepted = true;
            acceptedCallback?.Invoke();
            BMSLibrary library = libraryProvider()
                ?? throw new InvalidOperationException("Chart-info parse-failure removal library is not available.");
            Task scheduled = schedule(() => store.Remove(library, request.Md5s));
            if (scheduled == null)
            {
                throw new InvalidOperationException("Chart-info parse-failure removal scheduler returned no task.");
            }
            await scheduled;
            return ChartInfoParseFailureRemovalResult.Removed;
        }
        catch (Exception exception)
        {
            return ChartInfoParseFailureRemovalResult.Failed(exception, accepted);
        }
    }
}

internal sealed class BmsLibraryChartInfoParseFailureRemovalStore : IChartInfoParseFailureRemovalStore
{
    public void Remove(BMSLibrary library, IReadOnlyList<string> md5s)
    {
        (library ?? throw new ArgumentNullException(nameof(library))).RemoveChartInfoParseFailuresByMd5(md5s);
    }
}
