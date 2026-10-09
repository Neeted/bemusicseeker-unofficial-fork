using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.ExceptionServices;
using Ribbit.Logging;

namespace BeMusicSeeker.Models.BmsLibraryInternal;

internal static class InvalidExtensionRenameCoordinator
{
    /// <summary>
    /// 通常の拡張子変更を事前捕捉して実行し、排他解放後の通知を済ませてから従来どおり失敗を送出します。
    /// </summary>
    internal static void RenameBMSFilesExtensions(
        LibraryMutationOwner host,
        IEnumerable<ChartFile> charts,
        string newExt,
        bool? unregister)
    {
        LibraryMutationSessionReceipt receipt = RenameBMSFilesExtensionsWithReceipt(
            host,
            [host.PrepareLibraryFileExtensionRenameBatch(charts, newExt)],
            unregister);
        foreach (LibraryMutationSessionItemFailure failure in receipt.ItemFailures)
        {
            host.ShowNormalRenameFailure(
                new LibraryDeleteFailure
                {
                    Path = failure.Target.SourcePath,
                    Exception = failure.Failure,
                    IsDirectory = false
                },
                newExt);
        }
        ThrowForRequiredSessionFailure(receipt);
    }

    /// <summary>
    /// 確認前に固定した通常の拡張子変更を、一つの変更セッションで実行します。
    /// 受付後に全batchの結び付きを先に検査し、成功した変更だけを一回のカタログ適用へ集約します。
    /// </summary>
    /// <param name="host">処理と受付境界を所有するライブラリ変更主体。</param>
    /// <param name="batches">一操作で確認する固定対象と変更先拡張子。</param>
    /// <param name="unregister">変更に成功した譜面をカタログから登録解除するか。</param>
    /// <returns>一つの変更セッションの変更不能な確定結果。</returns>
    internal static LibraryMutationSessionReceipt RenameBMSFilesExtensionsWithReceipt(
        LibraryMutationOwner host,
        IEnumerable<LibraryFileExtensionRenameBatch> batches,
        bool? unregister, LibraryFileMutationCapability capability = null)
    {
        ArgumentNullException.ThrowIfNull(host);
        List<LibraryFileExtensionRenameBatch> targetBatches = [.. (batches ?? [])
            .Where(batch => batch != null && (batch.InputCount > 0 || batch.Targets.Count > 0))];
        if (targetBatches.Count == 0) { return LibraryMutationSessionReceipt.Empty; }

        List<Action> postLeaseNotifications = [];
        LibraryMutationSessionReceipt receipt = LibraryMutationSessionReceipt.Empty;
        try
        {
            host.RunWithNormalInvalidExtensionRenameWriteLocks(mutationCapability =>
            {
                IReadOnlyList<LibraryFileExtensionRenameBatch> resolvedBatches = host.ResolvePreparedLibraryFileExtensionRenameBatches(targetBatches);
                using LibraryMutationOwner.LibraryMutationSession session = host.BeginLibraryMutationSession(
                    mutationCapability,
                    "invalid_ext_rename",
                    postLeaseNotifications);
                session.ProtectManagedOutput(resolvedBatches.SelectMany(batch => batch.Targets.SelectMany(chart => new[] { chart.Path, System.IO.Path.ChangeExtension(chart.Path, batch.NewExtension) })), recursive: false);
                for (int index = 0; index < resolvedBatches.Count; index++)
                {
                    LibraryFileExtensionRenameBatch batch = resolvedBatches[index];
                    LibraryFileExtensionRenameResult result = host.RenameLibraryFileExtensionsAfterAdmission(
                        batch,
                        unregister == true);
                    session.AppendItemFailures(result.Report.Failures
                        .Where(failure => failure?.Exception != null)
                        .Select(failure => new LibraryMutationSessionItemFailure(
                            new LibraryMutationSessionTarget(failure.Path, string.Empty),
                            failure.Exception)));
                    session.AppendCatalogChange(
                        result.CatalogFacts,
                        LibraryPackageReferenceFacts.Empty,
                        result.ConfirmedTargets);
                    postLeaseNotifications.Add(() => host.LogInfo(
                        "invalid_ext_rename summary scope=normal total=" + batch.InputCount
                        + " renamed=" + result.Report.RenamedCount
                        + " deleted=" + result.Report.DuplicateDeletedCount
                        + " skipped=" + result.Report.SkippedCount));
                }
                receipt = session.Commit();
            }, capability);
        }
        finally
        {
            InvokePostLeaseNotificationsBestEffort(postLeaseNotifications);
        }
        return receipt;
    }

    /// <summary>保留譜面の拡張子を同じ受理済み権限で変更し、実変更を一つのセッションへ反映します。</summary>
    internal static void RenamePendingBmsFormatChartFileExtensions(
        LibraryMutationOwner host,
        IEnumerable<ChartFile> charts,
        string newExt, LibraryFileMutationCapability capability = null)
    {
        if (charts == null)
        {
            throw new ArgumentNullException(nameof(charts));
        }
        List<ChartFile> targetCharts = [.. charts.Where(chart => chart?.Kind == ChartFileKind.Bms)];
        List<Action> postLeaseNotifications = [];
        try
        {
            host.RunWithPendingInvalidExtensionRenameWriteLocks(mutationCapability =>
            {
                if (!host.TryEnterManagedOutputMutation(targetCharts.SelectMany(chart => new[] { chart.Path, System.IO.Path.ChangeExtension(chart.Path, newExt) }), recursive: false, out LibraryFileMutationLease playlistLease, mutationCapability)) { return; }
                using LibraryFileMutationLease heldPlaylist = playlistLease;
                PendingExtensionRenameReport result = host.RenamePendingBmsFormatChartFileExtensionsAfterAdmission(
                    targetCharts,
                    newExt);
                foreach (PendingExtensionRenameFailureReport failure in result.Failures)
                {
                    postLeaseNotifications.Add(() => host.ShowPendingRenameFailure(failure));
                }
                // Pending rename owns only package/install lifecycle state. It intentionally does not
                // create a LibraryMutationSession because no owned library catalog rows are changed.
                host.RemovePendingChartsFromPendingPackagesAndInstallRows(
                    result.ChartPathsToRemove,
                    mutationCapability,
                    postLeaseNotifications);
                postLeaseNotifications.Add(() => host.LogInfo("invalid_ext_rename summary scope=pending total=" + result.Total + " renamed=" + result.Renamed + " deleted=" + result.DuplicateDeleted + " skipped=" + result.Skipped + " failed=" + result.Failed + " totalMs=" + result.TotalMs));
            }, capability);
        }
        finally
        {
            InvokePostLeaseNotificationsBestEffort(postLeaseNotifications);
        }
    }

    private static void ThrowForRequiredSessionFailure(LibraryMutationSessionReceipt receipt)
    {
        Exception failure = receipt?.PhysicalFailure
            ?? receipt?.ApplyFailure
            ?? receipt?.FinalizationFailure;
        if (failure != null)
        {
            ExceptionDispatchInfo.Capture(failure).Throw();
        }
        if (receipt?.ConfirmedChangeCount > 0 && receipt.DurableCommit == false)
        {
            throw new InvalidOperationException(
                "Invalid-extension rename did not produce a durable session receipt.");
        }
    }

    private static void InvokePostLeaseNotificationsBestEffort(IEnumerable<Action> notifications)
    {
        foreach (Action notification in notifications ?? [])
        {
            try
            {
                notification?.Invoke();
            }
            catch (Exception exception)
            {
                // Notification failures are intentionally diagnostic-only.
                // The canonical mutation has already completed under the
                // existing session and must not be reclassified or retried.
                try
                {
                    NLogWrapper.FileLogger?.Warn(
                        exception,
                        "file_mutation_post_lease_notification_failed");
                }
                catch
                {
                }
            }
        }
    }
}
