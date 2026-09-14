using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.ExceptionServices;
using Ribbit.Logging;

namespace BeMusicSeeker.Models.BmsLibraryInternal;

internal static class InvalidExtensionRenameCoordinator
{
    /// <summary>
    /// Runs one normal invalid-extension rename batch and preserves the legacy throwing contract
    /// after all post-lease notifications have been released.
    /// </summary>
    internal static void RenameBMSFilesExtensions(
        LibraryMutationOwner host,
        IEnumerable<ChartFile> charts,
        string newExt,
        bool? unregister)
    {
        LibraryMutationSessionReceipt receipt = RenameBMSFilesExtensionsWithReceipt(
            host,
            [new LibraryFileExtensionRenameBatch(charts, newExt)],
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
    /// Executes all normal invalid-extension rename batches inside one operation-scoped mutation
    /// session. Filesystem work remains batch-oriented, while canonical catalog/state apply occurs
    /// exactly once for all confirmed rename/delete facts.
    /// </summary>
    /// <param name="host">Library mutation owner holding the command boundary.</param>
    /// <param name="batches">Extension-family batches belonging to one user operation.</param>
    /// <param name="unregister">Whether successful renamed charts are removed from the catalog.</param>
    /// <returns>Immutable terminal facts from the single operation-scoped session.</returns>
    internal static LibraryMutationSessionReceipt RenameBMSFilesExtensionsWithReceipt(
        LibraryMutationOwner host,
        IEnumerable<LibraryFileExtensionRenameBatch> batches,
        bool? unregister)
    {
        ArgumentNullException.ThrowIfNull(host);
        List<LibraryFileExtensionRenameBatch> targetBatches = [.. (batches ?? [])
            .Where(batch => batch != null && batch.Charts.Count > 0)
            .Select(batch => new LibraryFileExtensionRenameBatch(
                batch.Charts.Where(chart => chart?.GetBmsStorageOwner() != null),
                batch.NewExtension))
            .Where(batch => batch.Charts.Count > 0)];
        if (targetBatches.Count == 0)
        {
            return LibraryMutationSessionReceipt.Empty;
        }

        var preflightTargets = targetBatches
            .Select(batch => host.CaptureNormalInvalidExtensionRenameTargets(batch.Charts, batch.NewExtension))
            .ToArray();
        List<Action> postLeaseNotifications = [];
        LibraryMutationSessionReceipt receipt = LibraryMutationSessionReceipt.Empty;
        try
        {
            host.RunWithNormalInvalidExtensionRenameWriteLocks(mutationCapability =>
            {
                LibraryMutationOwner.LibraryMutationSession session = host.BeginLibraryMutationSession(
                    mutationCapability,
                    "invalid_ext_rename",
                    postLeaseNotifications,
                    suppressNormalRefreshNotification: false,
                    suppressLr2NormalFolderSync: false);
                for (int index = 0; index < targetBatches.Count; index++)
                {
                    LibraryFileExtensionRenameBatch batch = targetBatches[index];
                    LibraryFileExtensionRenameResult result = host.RenameLibraryFileExtensionsAfterAdmission(
                        batch.Charts,
                        preflightTargets[index],
                        batch.NewExtension,
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
                        "invalid_ext_rename summary scope=normal total=" + batch.Charts.Count
                        + " renamed=" + result.Report.RenamedCount
                        + " deleted=" + result.Report.DuplicateDeletedCount
                        + " skipped=" + result.Report.SkippedCount));
                }
                receipt = session.Commit();
            });
        }
        finally
        {
            InvokePostLeaseNotificationsBestEffort(postLeaseNotifications);
        }
        return receipt;
    }

    internal static void RenamePendingBmsFormatChartFileExtensions(
        LibraryMutationOwner host,
        IEnumerable<ChartFile> charts,
        string newExt)
    {
        if (charts == null)
        {
            throw new ArgumentNullException(nameof(charts));
        }
        List<ChartFile> targetCharts = [.. charts.Where(chart => chart?.GetBmsStorageOwner() != null)];
        List<Action> postLeaseNotifications = [];
        try
        {
            host.RunWithPendingInvalidExtensionRenameWriteLocks(mutationCapability =>
            {
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
            });
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
