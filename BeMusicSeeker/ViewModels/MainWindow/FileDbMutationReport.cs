using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Threading.Tasks;
using BeMusicSeeker.Models;
using BeMusicSeeker.Models.BmsLibraryInternal;
using BeMusicSeeker.Properties;
using BeMusicSeeker.Views.Dialogs;
using Ribbit.Logging;

namespace BeMusicSeeker.ViewModels;

/// <summary>
/// 変更結果の bounded terminal と、model が蓄積した OK-only 情報通知の表示を担当します。
/// terminal は操作の受付解放後に呼び、情報通知は scope を閉じて非同期に渡します。
/// いずれも表示失敗を確定済み mutation の成否へ混ぜません。
/// </summary>
internal static class FileDbMutationReport
{
    private const int MaximumMessageLength = 4096;
    private const int ConflictOperationLimit = 160;
    private const int ConflictDetailPathLimit = 72;
    private const int ConflictRecoveryPathLimit = 96;
    private const int ConflictErrorLimit = 160;
    private const int ConflictGuidanceLimit = 512;

    /// <summary>
    /// 一つの session の confirmed / failed / unprocessed facts から bounded terminal を作成します。
    /// 操作別の表示を選択しても per-item durable receipt は生成しません。
    /// </summary>
    /// <param name="operation">Localized operation label.</param>
    /// <param name="session">The immutable session terminal facts.</param>
    /// <param name="failure">An outer workflow failure not already retained by the session.</param>
    /// <param name="culture">Optional report culture.</param>
    /// <param name="mergeOperation">型衝突の表示に既存のマージ専用リソースを使用するかどうか。</param>
    internal static UiMessageRequest Create(
        string operation,
        LibraryMutationSessionReceipt session,
        Exception failure = null,
        CultureInfo culture = null,
        bool mergeOperation = false)
    {
        if (session == null)
        {
            return null;
        }

        string Localized(string key) => Resources.ResourceManager.GetString(key, culture ?? Resources.Culture);
        string Format(string key, params object[] values) => string.Format(
            culture ?? CultureInfo.CurrentCulture,
            Localized(key),
            values);

        if (failure != null && (ReferenceEquals(failure, session.PhysicalFailure)
            || ReferenceEquals(failure, session.ApplyFailure)
            || ReferenceEquals(failure, session.FinalizationFailure)
            || ReferenceEquals(failure, session.CleanupFailure)
            || session.ItemFailures.Any(item => ReferenceEquals(failure, item.Failure))))
        {
            failure = null;
        }

        bool hasError = session.HasRequiredFailure || failure != null;
        bool hasCleanupFailure = session.CleanupFailure != null;
        IReadOnlyList<FileDbMutationDestinationTypeConflict> destinationTypeConflicts =
            session.DestinationTypeConflicts;
        if (destinationTypeConflicts.Count > 0)
        {
            bool hasNonConflictFailure = hasError;
            (LibraryMutationSessionItemFailure Item, FileDbMutationDestinationTypeConflict Conflict)[] conflictDetails = session.ItemFailures
                .SelectMany(item => item.DestinationTypeConflicts
                    .Select(conflict => (Item: item, Conflict: conflict)))
                .GroupBy(item => string.Join("\u001f",
                    item.Conflict.SourcePath,
                    item.Conflict.DestinationPath,
                    item.Conflict.ExpectedIsDirectory,
                    item.Conflict.ExistingIsDirectory), StringComparer.OrdinalIgnoreCase)
                .Select(group => group.First())
                .ToArray();
            var conflictLines = new List<string>
            {
                Format(nameof(Resources.FileDbMutationReport_Operation), Limit(operation, ConflictOperationLimit)),
                Format(mergeOperation
                    ? nameof(Resources.FileDbMutationReport_DestinationTypeConflict_MergeCounts)
                    : nameof(Resources.FileDbMutationReport_DestinationTypeConflict_Counts),
                    destinationTypeConflicts.Count)
            };
            if (session.DurableCommit && session.ConfirmedChangeCount > 0)
            {
                conflictLines.Add(Format(
                    mergeOperation
                        ? nameof(Resources.FileDbMutationReport_DestinationTypeConflict_MergeSuccesses)
                        : nameof(Resources.FileDbMutationReport_DestinationTypeConflict_Successes),
                    session.ConfirmedChangeCount));
            }
            conflictLines.Add(Localized(mergeOperation
                ? nameof(Resources.FileDbMutationReport_DestinationTypeConflict_MergeReason)
                : nameof(Resources.FileDbMutationReport_DestinationTypeConflict_Reason)));
            foreach ((LibraryMutationSessionItemFailure Item, FileDbMutationDestinationTypeConflict Conflict) detail
                in conflictDetails.Take(5))
            {
                conflictLines.Add(Format(
                    nameof(Resources.FileDbMutationReport_DestinationTypeConflict_Detail),
                    Limit(detail.Item.Target.SourcePath, ConflictDetailPathLimit),
                    Limit(detail.Item.Target.DestinationPath, ConflictDetailPathLimit),
                    Limit(detail.Conflict.SourcePath, ConflictDetailPathLimit),
                    Limit(detail.Conflict.DestinationPath, ConflictDetailPathLimit),
                    Localized(detail.Conflict.ExpectedIsDirectory
                        ? nameof(Resources.FileDbMutationReport_Directory)
                        : nameof(Resources.FileDbMutationReport_File)),
                    Localized(detail.Conflict.ExistingIsDirectory
                        ? nameof(Resources.FileDbMutationReport_Directory)
                        : nameof(Resources.FileDbMutationReport_File))));
            }
            if (destinationTypeConflicts.Count > 5)
            {
                conflictLines.Add(Format(
                    nameof(Resources.FileDbMutationReport_DestinationTypeConflict_More),
                    destinationTypeConflicts.Count - 5));
            }
            if (hasCleanupFailure)
            {
                conflictLines.Add(Format(
                    nameof(Resources.FileDbMutationReport_DestinationTypeConflict_Cleanup),
                    1));
            }
            if (hasNonConflictFailure)
            {
                conflictLines.Add(Localized(nameof(Resources.FileDbMutationReport_TerminalFailure)));
            }
            if (hasCleanupFailure || hasNonConflictFailure)
            {
                string[] recoveryPaths = session.CandidatePaths
                    .Where(path => !string.IsNullOrWhiteSpace(path))
                    .Distinct(StringComparer.OrdinalIgnoreCase)
                    .Take(3)
                    .Select(path => Limit(path, ConflictRecoveryPathLimit))
                    .ToArray();
                if (recoveryPaths.Length > 0)
                {
                    conflictLines.Add(Localized(nameof(Resources.FileDbMutationReport_CandidatePaths)));
                    conflictLines.AddRange(recoveryPaths);
                }
                IEnumerable<Exception> conflictErrors = new[]
                {
                    failure,
                    session.PhysicalFailure,
                    session.ApplyFailure,
                    session.FinalizationFailure,
                    session.CleanupFailure
                }
                    .Concat(session.ItemFailures
                        .Where(item => !item.IsDestinationTypeConflictRefusal)
                        .Select(item => item.Failure))
                    .Where(error => error != null)
                    .Distinct()
                    .Take(3);
                foreach (Exception error in conflictErrors)
                {
                    conflictLines.Add(Format(
                        nameof(Resources.FileDbMutationReport_Error),
                        Limit(error.Message, ConflictErrorLimit)));
                }
            }
            string conflictGuidance = Limit(
                Localized(mergeOperation
                    ? nameof(Resources.FileDbMutationReport_DestinationTypeConflict_MergeGuidance)
                    : nameof(Resources.FileDbMutationReport_DestinationTypeConflict_Guidance)),
                ConflictGuidanceLimit);
            string conflictBody = string.Join(Environment.NewLine, conflictLines)
                + Environment.NewLine + conflictGuidance;
            if (conflictBody.Length > MaximumMessageLength)
            {
                conflictBody = Limit(
                    string.Join(Environment.NewLine, conflictLines),
                    MaximumMessageLength - Environment.NewLine.Length - conflictGuidance.Length)
                    + Environment.NewLine + conflictGuidance;
            }
            string conflictTitle = Localized(mergeOperation
                ? nameof(Resources.FileDbMutationReport_DestinationTypeConflict_MergeTitle)
                : nameof(Resources.FileDbMutationReport_Title));
            return hasNonConflictFailure
                ? UiMessageRequest.CreateError(conflictBody, conflictTitle)
                : UiMessageRequest.CreateWarning(conflictBody, conflictTitle);
        }
        if (!hasError && !hasCleanupFailure)
        {
            return null;
        }

        int durableChangeCount = session.DurableCommit ? session.ConfirmedChangeCount : 0;
        int requiredItemFailureCount = session.ItemFailures.Count(item => !item.IsDestinationTypeConflictRefusal);
        int notCommittedChangeCount = (session.DurableCommit ? 0 : session.ConfirmedChangeCount)
            + (session.FailedTarget == null ? 0 : 1)
            + requiredItemFailureCount;
        int requiredApplyFailureCount = (session.ApplyFailure == null ? 0 : 1)
            + (session.FinalizationFailure == null ? 0 : 1);
        int cleanupFailureCount = session.CleanupFailure == null ? 0 : 1;
        string body = Format(nameof(Resources.FileDbMutationReport_Operation), Limit(operation, 240))
            + Environment.NewLine
            + Format(
                nameof(Resources.LibraryMutationSessionReport_Counts),
                session.ConfirmedChangeCount,
                durableChangeCount,
                notCommittedChangeCount,
                requiredApplyFailureCount,
                cleanupFailureCount,
                session.UnprocessedTargets.Count);
        if (hasError)
        {
            body += Environment.NewLine + Localized(nameof(Resources.FileDbMutationReport_TerminalFailure));
        }

        string[] paths = session.CandidatePaths
            .Where(path => !string.IsNullOrWhiteSpace(path))
            .Take(3)
            .Select(path => Limit(path, 240))
            .ToArray();
        if (paths.Length > 0)
        {
            body += Environment.NewLine + Localized(nameof(Resources.FileDbMutationReport_CandidatePaths))
                + Environment.NewLine + string.Join(Environment.NewLine, paths);
        }

        IEnumerable<Exception> errors = new[]
        {
            failure,
            session.PhysicalFailure,
            session.ApplyFailure,
            session.FinalizationFailure,
            session.CleanupFailure
        }
            .Concat(session.ItemFailures
                .Where(item => !item.IsDestinationTypeConflictRefusal)
                .Select(item => item.Failure))
            .Where(error => error != null)
            .Distinct()
            .Take(3);
        foreach (Exception error in errors)
        {
            body += Environment.NewLine
                + Format(nameof(Resources.FileDbMutationReport_Error), Limit(error.Message, 400));
        }

        string guidance = Limit(Localized(nameof(Resources.FileDbMutationReport_Guidance)), 1024);
        body = Limit(body, MaximumMessageLength - Environment.NewLine.Length - guidance.Length)
            + Environment.NewLine + guidance;
        string title = Localized(nameof(Resources.FileDbMutationReport_Title));
        return hasError ? UiMessageRequest.CreateError(body, title) : UiMessageRequest.CreateWarning(body, title);
    }

    /// <summary>
    /// 操作単位の session を記録し、資源解放後に一回だけ terminal dialog を表示します。
    /// </summary>
    /// <param name="dialogs">UI dialog boundary used after all mutation leases are released.</param>
    /// <param name="operation">Localized operation label.</param>
    /// <param name="session">The immutable session terminal facts.</param>
    /// <param name="failure">An outer workflow failure not already retained by the session.</param>
    /// <param name="mergeOperation">型衝突の表示に既存のマージ専用リソースを使用するかどうか。</param>
    internal static async Task ShowAsync(
        IUiDialogService dialogs,
        string operation,
        LibraryMutationSessionReceipt session,
        Exception failure = null,
        bool mergeOperation = false)
    {
        try
        {
            UiMessageRequest request = Create(operation, session, failure, mergeOperation: mergeOperation);
            if (request == null)
            {
                return;
            }

            try
            {
                NLogWrapper.FileLogger?.Warn(session.PrimaryFailure
                    ?? session.ItemFailures.FirstOrDefault()?.Failure
                    ?? failure,
                    "library_mutation_session_report operation=" + operation
                    + " durable=" + session.DurableCommit
                    + " confirmed=" + session.ConfirmedChangeCount
                    + " catalogRemovals=" + session.CatalogChartRemovalCount
                    + " catalogPathChanges=" + session.CatalogChartPathChangeCount
                    + " catalogFolderChanges=" + session.CatalogFolderPathChangeCount
                    + " packageDestinationChanges=" + session.PackageInstallDestinationChangeCount
                    + " packagePathChanges=" + session.PackageInstalledPathChangeCount
                    + " reverseLookupMoves=" + session.FolderReferenceMoveCount
                    + " reverseLookupRemovals=" + session.ResourceDirectoryRemovalCount
                    + " itemFailures=" + session.ItemFailures.Count
                    + " failedSource=" + session.FailedTarget?.SourcePath
                    + " failedDestination=" + session.FailedTarget?.DestinationPath
                    + " unprocessed=" + session.UnprocessedTargets.Count
                    + " candidates=" + string.Join("|", session.CandidatePaths)
                    + " applyFailure=" + session.ApplyFailure
                    + " finalizationFailure=" + session.FinalizationFailure
                    + " cleanupFailure=" + session.CleanupFailure);
            }
            catch
            {
                // Diagnostic sinks must not affect terminal facts.
            }

            foreach (LibraryMutationSessionItemFailure itemFailure in session.ItemFailures)
            {
                try
                {
                    NLogWrapper.FileLogger?.Warn(
                        itemFailure.Failure,
                        "library_mutation_session_item_failure source=" + itemFailure.Target.SourcePath
                        + " destination=" + itemFailure.Target.DestinationPath);
                }
                catch
                {
                    // Keep logging best-effort for every retained item failure.
                }
            }

            if (failure != null && !ReferenceEquals(failure, session.PrimaryFailure))
            {
                LogNotificationFailure(failure);
            }
            UiDialogResult result = await dialogs.ShowMessageAsync(request).ConfigureAwait(false);
            if (result == null || result.Status is not (UiDialogStatus.Accepted or UiDialogStatus.Rejected
                or UiDialogStatus.CancelledByUser or UiDialogStatus.ClosedByUser))
            {
                LogNotificationFailure(result?.Exception ?? new InvalidOperationException(
                    "Mutation session report was not displayed: " + result?.Status));
            }
        }
        catch (Exception exception)
        {
            LogNotificationFailure(exception);
        }
    }

    /// <summary>
    /// model が蓄積した OK-only 情報通知を順番に表示します。操作 scope を閉じた後に呼び、
    /// worker はこの Task の完了を待ちません。表示失敗は診断に残して後続通知へ進み、変更結果へ混ぜません。
    /// </summary>
    internal static async Task ShowOperationMessagesAsync(
        IUiDialogService dialogs,
        IReadOnlyList<BMSLibrary.OperationDialogMessage> messages,
        Action<Exception> reportFailure = null)
    {
        foreach (BMSLibrary.OperationDialogMessage message in messages)
        {
            try
            {
                UiDialogResult result = await dialogs.ShowMessageAsync(new UiMessageRequest(
                    message.MessageBoxText, message.Caption, message.Button, message.Icon, message.DefaultResult))
                    .ConfigureAwait(false);
                if (result == null || result.Status is not (UiDialogStatus.Accepted
                    or UiDialogStatus.CancelledByUser or UiDialogStatus.ClosedByUser))
                {
                    throw new InvalidOperationException("Operation notification was not displayed: " + result?.Status,
                        result?.Exception);
                }
            }
            catch (Exception exception)
            {
                try { (reportFailure ?? LogNotificationFailure)(exception); }
                catch (Exception diagnosticFailure) { LogNotificationFailure(diagnosticFailure); }
            }
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

    /// <summary>Bounds a displayed field without altering the retained diagnostic facts.</summary>
    internal static string Limit(string value, int maximum) =>
        string.IsNullOrEmpty(value) || value.Length <= maximum ? value : value[..(maximum - 1)] + "…";

}
