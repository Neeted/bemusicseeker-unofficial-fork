using System;
using System.Globalization;
using System.Linq;
using System.Threading.Tasks;
using BeMusicSeeker.Models.BmsLibraryInternal;
using BeMusicSeeker.Properties;
using BeMusicSeeker.Views.Dialogs;
using Ribbit.Logging;

namespace BeMusicSeeker.ViewModels;

/// <summary>One optional, bounded deletion report after the operation has released its leases.</summary>
internal static class LibraryChartRemovalReport
{
    /// <summary>Renders confirmed and unconfirmed facts without probing paths or promising recovery.</summary>
    internal static UiMessageRequest Create(LibraryChartRemovalOutcome outcome, Exception failure = null,
        CultureInfo culture = null)
    {
        if (outcome == null || (!outcome.HasError && failure == null))
            return null;
        string Localized(string key) => Resources.ResourceManager.GetString(key, culture ?? Resources.Culture);
        string Format(string key, params object[] values) => string.Format(culture ?? CultureInfo.CurrentCulture, Localized(key), values);
        string body = Format(nameof(Resources.LibraryChartRemovalReport_Counts), outcome.ConfirmedChartCount,
            outcome.Targets.Count(target => target.State == LibraryChartRemovalState.NotExecuted),
            outcome.Targets.Count(target => target.State == LibraryChartRemovalState.Unconfirmed),
            outcome.Targets.Count(target => target.State is LibraryChartRemovalState.Stale or LibraryChartRemovalState.Unresolved));
        body += Environment.NewLine + Localized(!outcome.CatalogApplyAttempted
            ? nameof(Resources.LibraryChartRemovalReport_CatalogNotAttempted)
            : outcome.RequiredFinalizationFailed ? nameof(Resources.LibraryChartRemovalReport_FinalizationFailed)
            : outcome.CatalogDurable ? nameof(Resources.LibraryChartRemovalReport_CatalogDurable)
            : nameof(Resources.LibraryChartRemovalReport_CatalogUnconfirmed));
        body += Environment.NewLine + Localized(nameof(Resources.LibraryChartRemovalReport_Targets))
            + Environment.NewLine + string.Join(Environment.NewLine, outcome.Targets.Select(target => target.Path)
                .Distinct(StringComparer.OrdinalIgnoreCase).Take(3).Select(path => FileDbMutationReport.Limit(path, 240)));
        foreach (Exception error in new[] { failure, outcome.CatalogFailure }.Concat(outcome.Targets.Select(target => target.Failure))
            .Where(error => error != null).Distinct().Take(3))
            body += Environment.NewLine + FileDbMutationReport.Limit(error.Message, 400);
        string guidance = FileDbMutationReport.Limit(Localized(nameof(Resources.LibraryChartRemovalReport_Guidance)), 1024);
        body = FileDbMutationReport.Limit(body, 4096 - Environment.NewLine.Length - guidance.Length)
            + Environment.NewLine + guidance;
        return UiMessageRequest.CreateError(body, Localized(nameof(Resources.LibraryChartRemovalReport_Title)));
    }

    /// <summary>Reports at most once; notification failure cannot change or replay deletion.</summary>
    internal static async Task ShowAsync(IUiDialogService dialogs, LibraryChartRemovalOutcome outcome, Exception failure = null)
    {
        try
        {
            UiMessageRequest request = Create(outcome, failure);
            if (request == null)
                return;
            FileDbMutationReport.NotifyBestEffort(() =>
            {
                NLogWrapper.FileLogger?.Warn(outcome.CatalogFailure,
                    "library_chart_removal_report attempted=" + outcome.CatalogApplyAttempted
                    + " durable=" + outcome.CatalogDurable + " confirmed=" + outcome.ConfirmedChartCount);
                foreach (LibraryChartRemovalTarget target in outcome.Targets)
                    NLogWrapper.FileLogger?.Warn(target.Failure, "library_chart_removal_target path=" + target.Path + " state=" + target.State);
            });
            if (failure != null)
                FileDbMutationReport.LogNotificationFailure(failure);
            UiDialogResult result = await dialogs.ShowMessageAsync(request).ConfigureAwait(false);
            if (result == null || result.Status is not (UiDialogStatus.Accepted or UiDialogStatus.Rejected
                or UiDialogStatus.CancelledByUser or UiDialogStatus.ClosedByUser))
                FileDbMutationReport.LogNotificationFailure(result?.Exception
                    ?? new InvalidOperationException("Library deletion report was not displayed: " + result?.Status));
        }
        catch (Exception exception)
        {
            FileDbMutationReport.LogNotificationFailure(exception);
        }
    }
}
