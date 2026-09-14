using System;
using System.Collections.Generic;
using System.Linq;
using Ribbit.Logging;

namespace BeMusicSeeker.Models.BmsLibraryInternal;

internal static class InvalidExtensionRenameCoordinator
{
    internal static void RenameBMSFilesExtensions(
        LibraryMutationOwner host,
        IEnumerable<ChartFile> charts,
        string newExt,
        bool? unregister)
    {
        List<ChartFile> targetCharts = [.. (charts ?? []).Where(chart => chart?.GetBmsStorageOwner() != null)];
        List<LibraryFileOperationTargetSnapshot> preflightTargets = host.CaptureNormalInvalidExtensionRenameTargets(targetCharts, newExt);
        List<Action> postLeaseNotifications = [];
        try
        {
            host.RunWithNormalInvalidExtensionRenameWriteLocks(mutationCapability =>
            {
                LibraryFileExtensionRenameResult result = host.RenameLibraryFileExtensionsAfterAdmission(
                    targetCharts,
                    preflightTargets,
                    newExt,
                    unregister == true);
                foreach (LibraryDeleteFailure failure in result.Report.Failures)
                {
                    postLeaseNotifications.Add(() => host.ShowNormalRenameFailure(failure, newExt));
                }
                host.ApplyLibraryMutationFactsUnderExistingReservation(
                    result.CatalogFacts,
                    LibraryPackageReferenceFacts.Empty,
                    "invalid_ext_rename",
                    mutationCapability,
                    postLeaseNotifications);
                postLeaseNotifications.Add(() => host.LogInfo("invalid_ext_rename summary scope=normal total=" + targetCharts.Count + " renamed=" + result.Report.RenamedCount + " deleted=" + result.Report.DuplicateDeletedCount + " skipped=" + result.Report.SkippedCount));
            });
        }
        finally
        {
            InvokePostLeaseNotificationsBestEffort(postLeaseNotifications);
        }
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
