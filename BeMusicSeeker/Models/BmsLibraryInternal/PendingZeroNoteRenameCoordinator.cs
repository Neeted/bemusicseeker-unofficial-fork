using System;
using System.Collections.Generic;
using System.Threading;

namespace BeMusicSeeker.Models.BmsLibraryInternal;

internal interface IPendingZeroNoteRenameHost
{
    IDisposable AcquireBmsFilesInitializedMinReaderGuard();

    IDisposable AcquirePendingInstallChartsWriterGuard();

    IDisposable AcquireSongDbInstallWriterGuard();

    IEnumerable<ChartFile> GetPendingBmsFormatChartFilesSnapshot();

    RenameInvalidExtensionOutcome ProcessInvalidExtensionRename(BMSFile file, string requestedPath);

    void ShowRenameFailure(PendingZeroNoteRenameFailure failure);

    void RemovePendingChartsFromPendingPackagesAndInstallRows(IEnumerable<string> chartPaths);

    void LogInfo(string info);
}

internal static class PendingZeroNoteRenameCoordinator
{
    internal static void RenamePendingZeroNoteBmsFormatChartsToInvalidExtensions(
        BmsLibraryPackageInstallService packageInstallService,
        IPendingZeroNoteRenameHost host,
        IEnumerable<ChartFile> targetCharts,
        CancellationToken token = default,
        Action onEachProcessed = null)
    {
        int deferredProcessedCount = 0;
        try
        {
            using (host.AcquireBmsFilesInitializedMinReaderGuard())
            using (host.AcquirePendingInstallChartsWriterGuard())
            using (host.AcquireSongDbInstallWriterGuard())
            {
                IEnumerable<ChartFile> charts = targetCharts ?? host.GetPendingBmsFormatChartFilesSnapshot();
                PendingZeroNoteRenameResult result = packageInstallService.RenamePendingZeroNoteBmsFormatChartsToInvalidExtensions(
                    charts,
                    host.ProcessInvalidExtensionRename,
                    token,
                    () => deferredProcessedCount++,
                    host.LogInfo);
                foreach (PendingZeroNoteRenameFailure failure in result.Failures)
                {
                    host.ShowRenameFailure(failure);
                }
                host.RemovePendingChartsFromPendingPackagesAndInstallRows(result.ChartPathsToRemove);
                host.LogInfo("advanced_pending_zero_note_rename summary total=" + result.Total + " processed=" + result.Processed + " zeroNote=" + result.ZeroNote + " renamed=" + result.Renamed + " duplicateDeleted=" + result.DuplicateDeleted + " skipped=" + result.Skipped + " failed=" + result.Failed + " canceled=" + result.Canceled);
            }
        }
        finally
        {
            InvokeDeferredProcessedCallbacks(onEachProcessed, deferredProcessedCount);
        }
    }

    private static void InvokeDeferredProcessedCallbacks(Action onEachProcessed, int count)
    {
        if (onEachProcessed == null || count <= 0)
        {
            return;
        }
        for (int i = 0; i < count; i++)
        {
            onEachProcessed();
        }
    }
}
