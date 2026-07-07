using System;
using System.Collections.Generic;
using System.Threading;
using BeMusicSeeker.Models.Utils;

namespace BeMusicSeeker.Models.BmsLibraryInternal;

internal interface IPendingPackageSourceDeletionHost
{
    IDisposable AcquireBmsFilesInitializedAllReaderGuard();

    IDisposable AcquirePendingInstallChartsWriterGuard();

    IDisposable AcquireSongDbInstallWriterGuard();

    IEnumerable<ChartPackage> PendingPackages { get; }

    IFileMutationService FileMutationService { get; }

    FileMutationOptions TargetOnlyFileMutationOptions { get; }

    FileMutationOptions RecursiveDirectoryTreeFileMutationOptions { get; }

    void RemovePendingPackagesFromPendingListAndInstallRows(IEnumerable<ChartPackage> packages);

    void ShowSourceDeletionFailure(PendingPackageSourceDeletionFailure failure);

    void LogInfo(string info);
}

internal static class PendingPackageSourceDeletionCoordinator
{
    internal static void DeletePendingPackageSources(
        BmsLibraryPackageInstallService packageInstallService,
        IPendingPackageSourceDeletionHost host,
        IEnumerable<ChartPackage> packages,
        bool sendToRecycleBin = true,
        CancellationToken token = default,
        Action onEachProcessed = null)
    {
        if (packages == null)
        {
            throw new ArgumentNullException(nameof(packages));
        }
        int deferredProcessedCount = 0;
        try
        {
            using (host.AcquireBmsFilesInitializedAllReaderGuard())
            using (host.AcquirePendingInstallChartsWriterGuard())
            using (host.AcquireSongDbInstallWriterGuard())
            {
                List<ChartPackage> requestedPackages = packageInstallService.DeduplicatePackagesByPathOrReference(packages);
                bool deletePermanently = !sendToRecycleBin;
                host.LogInfo("advanced_pending_cleanup start requested=" + requestedPackages.Count + " permanent=" + deletePermanently);
                PendingPackageSourceDeletionResult result = packageInstallService.DeletePendingPackageSources(
                    requestedPackages,
                    host.PendingPackages,
                    sendToRecycleBin,
                    host.FileMutationService,
                    host.TargetOnlyFileMutationOptions,
                    host.RecursiveDirectoryTreeFileMutationOptions,
                    token,
                    () => deferredProcessedCount++,
                    host.LogInfo);
                foreach (PendingPackageSourceDeletionFailure failure in result.Failures)
                {
                    host.ShowSourceDeletionFailure(failure);
                }
                host.RemovePendingPackagesFromPendingListAndInstallRows(result.PackagesToRemove);
                host.LogInfo("advanced_pending_cleanup summary requested=" + result.Requested + " processed=" + result.Processed + " removed=" + result.Removed + " failed=" + result.Failed + " skipped=" + result.Skipped + " canceled=" + result.Canceled);
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
