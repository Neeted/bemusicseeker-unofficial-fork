using System;
using System.Collections.Generic;
using System.Linq;

namespace BeMusicSeeker.Models.BmsLibraryInternal;

internal interface ILibraryChartRemovalHost
{
    bool TryBlockLr2SongDbSyncMutation(string operation);

    void RunWithLibraryChartRemovalWriteLocks(Action action);

    LibraryRemovalResult DeleteLibraryCharts(
        IEnumerable<LibraryChartRef> charts,
        bool sendToRecycleBin,
        Func<string, bool> confirmDeleteWholeFolder);

    void LogInstallPerformance(string message);

    void LogReverseLookupMutationAndQueueWarmupIfNeeded(string reason, DirectoryResourceLookupCache.ReverseLookupMutationResult mutationResult);

    void ApplyLibraryMutationDelta(LibraryMutationDelta delta);

    bool ConfirmDeleteWholeFolder(string folderPath);

    void ShowDeleteFailure(LibraryDeleteFailure failure);
}

internal static class LibraryChartRemovalCoordinator
{
    internal static void RemoveLibraryCharts(
        ILibraryChartRemovalHost host,
        IEnumerable<LibraryChartRef> charts,
        bool sendToRecycleBin,
        IEnumerable<string> approvedWholeFolderDeletePaths)
    {
        if (host.TryBlockLr2SongDbSyncMutation(nameof(BMSLibrary.RemoveLibraryCharts)))
        {
            return;
        }
        HashSet<string> approvedWholeFolderDeletes = approvedWholeFolderDeletePaths == null
            ? null
            : new HashSet<string>(approvedWholeFolderDeletePaths.Where(path => !string.IsNullOrWhiteSpace(path)), StringComparer.OrdinalIgnoreCase);

        host.RunWithLibraryChartRemovalWriteLocks(() =>
        {
            LibraryRemovalResult result = host.DeleteLibraryCharts(
                charts,
                sendToRecycleBin,
                folderPath => approvedWholeFolderDeletes != null
                    ? approvedWholeFolderDeletes.Contains(folderPath)
                    : host.ConfirmDeleteWholeFolder(folderPath));
            host.LogInstallPerformance("delete_library_result input=" + result.InputChartCount
                + " canonical=" + result.CanonicalChartCount
                + " unresolved=" + result.UnresolvedChartCount
                + " pathOnly=" + result.PathOnlyInputCount
                + " removed=" + result.RemovedCharts.Count
                + " failures=" + result.Failures.Count
                + " folderDeletes=" + result.FolderDeleteCount
                + " fileDeletes=" + result.FileDeleteCount);
            host.LogReverseLookupMutationAndQueueWarmupIfNeeded("delete_library", result.ResourceIndexMutation);
            host.ApplyLibraryMutationDelta(result.MutationDelta);
            foreach (LibraryDeleteFailure failure in result.Failures)
            {
                host.ShowDeleteFailure(failure);
            }
        });
    }
}
