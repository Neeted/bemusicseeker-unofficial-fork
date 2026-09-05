using System;
using System.Globalization;
using System.Linq;
using System.Threading.Tasks;
using BeMusicSeeker.Models.BmsLibraryInternal;
using BeMusicSeeker.Properties;
using BeMusicSeeker.Views.Dialogs;
using Ribbit.Logging;

namespace BeMusicSeeker.ViewModels;

/// <summary>
/// Formats immutable mutation facts and presents one bounded, optional terminal
/// notification. Callers own the operation and release all leases before calling.
/// </summary>
internal static class FileDbMutationReport
{
    /// <summary>
    /// Creates a localized warning/error, or null for an entirely normal result.
    /// Candidate paths are not probed; counts describe receipt operations, not files.
    /// The optional culture also permits rendering without changing global resources.
    /// </summary>
    internal static UiMessageRequest Create(
        string operation,
        FileDbMutationBatchReceipt batch,
        Exception failure = null,
        CultureInfo culture = null)
    {
        if (batch == null)
            return null;

        var receipts = batch.Receipts;
        // The result may carry the same primary exception as its receipt. It is
        // not a second operation-wide failure and must not be labelled as one.
        if (failure != null && (ReferenceEquals(failure, batch.FinalizationFailure)
            || receipts.Any(receipt => ReferenceEquals(failure, receipt.Failure))))
            failure = null;
        int notCommitted = receipts.Count(receipt => !receipt.DurableCommit);
        int finalization = receipts.Count(receipt => receipt.FinalizationFailure != null
            || receipt.TerminalState == FileDbMutationTerminalState.DurableFinalizationFailed);
        int cleanup = receipts.Count(receipt => receipt.HasCleanupFailure
            || receipt.TerminalState == FileDbMutationTerminalState.CompletedWithCleanupFailure);
        int manual = receipts.Count(receipt => receipt.TerminalState == FileDbMutationTerminalState.ManualRecoveryRequired);
        bool hasError = notCommitted > 0 || finalization > 0 || manual > 0
            || batch.FinalizationFailure != null || failure != null;
        if (!hasError && cleanup == 0)
            return null;

        string Localized(string key) => Resources.ResourceManager.GetString(key, culture ?? Resources.Culture);
        string Format(string key, params object[] values) => string.Format(culture ?? CultureInfo.CurrentCulture, Localized(key), values);

        string body = Format(nameof(Resources.FileDbMutationReport_Operation), Limit(operation, 240))
            + Environment.NewLine
            + Format(nameof(Resources.FileDbMutationReport_Counts), receipts.Count,
                receipts.Count(receipt => receipt.DurableCommit), notCommitted, finalization, cleanup, manual);
        if (batch.FinalizationFailure != null || failure != null)
            body += Environment.NewLine + Localized(nameof(Resources.FileDbMutationReport_TerminalFailure));

        var paths = receipts.SelectMany(receipt => receipt.RecoveryPaths)
            .Concat(receipts.SelectMany(receipt => receipt.SourcePaths.Concat(receipt.DestinationPaths)))
            .Distinct(StringComparer.OrdinalIgnoreCase).Take(3);
        body += Environment.NewLine + Localized(nameof(Resources.FileDbMutationReport_CandidatePaths))
            + Environment.NewLine + string.Join(Environment.NewLine, paths.Select(path => Limit(path, 240)));

        var errors = new[] { failure, batch.FinalizationFailure }
            .Concat(receipts.SelectMany(receipt => new[] { receipt.Failure, receipt.FinalizationFailure, receipt.CleanupFailure }))
            .Where(error => error != null).Distinct().Take(3);
        foreach (Exception error in errors)
            body += Environment.NewLine + Format(nameof(Resources.FileDbMutationReport_Error), Limit(error.Message, 400));

        string guidance = Localized(nameof(Resources.FileDbMutationReport_Guidance));
        // Reserve the guidance even when localized text or input is unusually long.
        guidance = Limit(guidance, 1024);
        body = Limit(body, 4096 - Environment.NewLine.Length - guidance.Length)
            + Environment.NewLine + guidance;
        string title = Localized(nameof(Resources.FileDbMutationReport_Title));
        return hasError ? UiMessageRequest.CreateError(body, title) : UiMessageRequest.CreateWarning(body, title);
    }

    /// <summary>
    /// Logs full abnormal facts and awaits at most one dialog. Notification failure
    /// never replaces the caller's receipt/failure or triggers another mutation.
    /// </summary>
    internal static async Task ShowAsync(IUiDialogService dialogs, string operation,
        FileDbMutationBatchReceipt batch, Exception failure = null)
    {
        try
        {
            UiMessageRequest request = Create(operation, batch, failure);
            if (request == null)
                return;
            foreach (FileDbMutationReceipt receipt in batch.Receipts)
            {
                try
                {
                    NLogWrapper.FileLogger?.Warn(receipt.Failure,
                        "file_db_mutation_report operation=" + operation + " id=" + receipt.OperationId
                        + " state=" + receipt.TerminalState + " durable=" + receipt.DurableCommit
                        + " source=" + string.Join("|", receipt.SourcePaths)
                        + " destination=" + string.Join("|", receipt.DestinationPaths)
                        + " staging=" + string.Join("|", receipt.StagingPaths)
                        + " backup=" + string.Join("|", receipt.BackupPaths)
                        + " candidates=" + string.Join("|", receipt.RecoveryPaths)
                        + " finalization=" + receipt.FinalizationFailure + " cleanup=" + receipt.CleanupFailure);
                }
                catch { /* Diagnostic sinks must not affect terminal facts. */ }
            }
            if (batch.FinalizationFailure != null)
                LogNotificationFailure(batch.FinalizationFailure);
            if (failure != null)
                LogNotificationFailure(failure);
            UiDialogResult result = await dialogs.ShowMessageAsync(request).ConfigureAwait(false);
            if (result == null || result.Status is not (UiDialogStatus.Accepted or UiDialogStatus.Rejected
                or UiDialogStatus.CancelledByUser or UiDialogStatus.ClosedByUser))
                LogNotificationFailure(result?.Exception ?? new InvalidOperationException(
                    "Mutation report was not displayed: " + result?.Status));
        }
        catch (Exception exception)
        {
            LogNotificationFailure(exception);
        }
    }

    /// <summary>Runs optional terminal notification/observer cleanup without altering mutation facts.</summary>
    internal static void NotifyBestEffort(Action notification)
    {
        try { notification(); }
        catch (Exception exception) { LogNotificationFailure(exception); }
    }

    /// <summary>Records an optional notification failure through the existing diagnostic boundary.</summary>
    internal static void LogNotificationFailure(Exception exception)
    {
        try { NLogWrapper.FileLogger?.Warn(exception, "file_db_mutation_notification_failed"); }
        catch { /* Diagnostic sinks are optional too. */ }
    }

    private static string Limit(string value, int maximum) =>
        string.IsNullOrEmpty(value) || value.Length <= maximum ? value : value[..(maximum - 1)] + "…";
}
