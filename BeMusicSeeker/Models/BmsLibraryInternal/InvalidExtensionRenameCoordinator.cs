using System;
using System.Collections.Generic;
using System.Linq;

namespace BeMusicSeeker.Models.BmsLibraryInternal;

internal interface IInvalidExtensionRenameHost
{
    bool TryBlockLr2SongDbSyncMutation(string operation);

    void RunWithNormalInvalidExtensionRenameWriteLocks(Action action);

    void RunWithPendingInvalidExtensionRenameWriteLocks(Action action);

    LibraryMutationDelta RenameLibraryFileExtensions(IEnumerable<ChartFile> targetCharts, string newExt, bool unregister);

    PendingExtensionRenameResult RenamePendingBmsFormatChartFileExtensions(IEnumerable<ChartFile> charts, string newExt);

    void ApplyLibraryMutationDelta(LibraryMutationDelta delta);

    void RemovePendingChartsFromPendingPackagesAndInstallRows(IEnumerable<string> chartPaths);

    void ShowNormalRenameFailure(LibraryDeleteFailure failure, string newExt);

    void ShowPendingRenameFailure(PendingExtensionRenameFailure failure);

    void LogInfo(string info);
}

internal static class InvalidExtensionRenameCoordinator
{
    internal static void RenameBMSFilesExtensions(
        IInvalidExtensionRenameHost host,
        IEnumerable<ChartFile> charts,
        string newExt,
        bool? unregister)
    {
        List<ChartFile> targetCharts = [.. (charts ?? []).Where(chart => chart?.GetBmsStorageOwner() != null)];
        if (host.TryBlockLr2SongDbSyncMutation(nameof(BMSLibrary.RenameBMSFilesExtensions)))
        {
            return;
        }
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
        IInvalidExtensionRenameHost host,
        IEnumerable<ChartFile> charts,
        string newExt)
    {
        if (charts == null)
        {
            throw new ArgumentNullException(nameof(charts));
        }
        host.RunWithPendingInvalidExtensionRenameWriteLocks(() =>
        {
            PendingExtensionRenameResult result = host.RenamePendingBmsFormatChartFileExtensions(charts, newExt);
            foreach (PendingExtensionRenameFailure failure in result.Failures)
            {
                host.ShowPendingRenameFailure(failure);
            }
            host.RemovePendingChartsFromPendingPackagesAndInstallRows(result.ChartPathsToRemove);
            host.LogInfo("invalid_ext_rename summary scope=pending total=" + result.Total + " renamed=" + result.Renamed + " deleted=" + result.DuplicateDeleted + " skipped=" + result.Skipped + " failed=" + result.Failed + " totalMs=" + result.TotalMs);
        });
    }
}
