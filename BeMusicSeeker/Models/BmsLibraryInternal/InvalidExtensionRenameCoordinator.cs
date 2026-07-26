using System;
using System.Collections.Generic;
using System.Linq;

namespace BeMusicSeeker.Models.BmsLibraryInternal;

internal static class InvalidExtensionRenameCoordinator
{
    internal static void RenameBMSFilesExtensions(
        LibraryFileOperationOwner host,
        IEnumerable<ChartFile> charts,
        string newExt,
        bool? unregister)
    {
        List<ChartFile> targetCharts = [.. (charts ?? []).Where(chart => chart?.GetBmsStorageOwner() != null)];
        host.RunWithNormalInvalidExtensionRenameWriteLocks(() =>
        {
            LibraryMutationDelta delta = host.RenameLibraryFileExtensions(
                targetCharts,
                newExt,
                unregister == true);
            foreach (LibraryDeleteFailure failure in delta.Failures)
            {
                host.ShowNormalRenameFailure(failure, newExt);
            }
            host.ApplyLibraryMutationDelta(delta);
            host.LogInfo("invalid_ext_rename summary scope=normal total=" + targetCharts.Count + " renamed=" + delta.RenamedCount + " deleted=" + delta.DuplicateDeletedCount + " skipped=" + delta.SkippedCount);
        });
    }

    internal static void RenamePendingBmsFormatChartFileExtensions(
        LibraryFileOperationOwner host,
        IEnumerable<ChartFile> charts,
        string newExt)
    {
        if (charts == null)
        {
            throw new ArgumentNullException(nameof(charts));
        }
        host.RunWithPendingInvalidExtensionRenameWriteLocks(() =>
        {
            PendingExtensionRenameReport result = host.RenamePendingBmsFormatChartFileExtensions(charts, newExt);
            foreach (PendingExtensionRenameFailureReport failure in result.Failures)
            {
                host.ShowPendingRenameFailure(failure);
            }
            host.RemovePendingChartsFromPendingPackagesAndInstallRows(result.ChartPathsToRemove);
            host.LogInfo("invalid_ext_rename summary scope=pending total=" + result.Total + " renamed=" + result.Renamed + " deleted=" + result.DuplicateDeleted + " skipped=" + result.Skipped + " failed=" + result.Failed + " totalMs=" + result.TotalMs);
        });
    }
}
