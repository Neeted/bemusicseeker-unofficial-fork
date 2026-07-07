using System;
using System.Collections.Generic;
using System.Threading;

namespace BeMusicSeeker.Models.BmsLibraryInternal;

internal interface IPendingResourceOverwriteHost
{
    IDisposable AcquireBmsFilesInitializedAllReaderGuard();

    IDisposable AcquirePendingInstallChartsWriterGuard();

    IDisposable AcquireBmsFilesWriterGuard();

    IDisposable AcquireSongDbInstallWriterGuard();

    BmsLibraryOptionsSnapshot CurrentOptionsSnapshot { get; }

    IEnumerable<ChartPackage> PendingPackages { get; }

    int PendingPackageCount { get; }

    InstalledChartLookupIndexSnapshot CreateInstalledChartLookupSnapshot();

    int CountEligiblePackages(IEnumerable<ChartPackage> packages);

    InstalledOnlyPackageResolutionResult ResolveInstalledOnlyPackageDestination(ChartPackage pendingPackage, IInstalledChartLookupIndex installedDirectoryIndex);

    string DescribeSkipDetail(InstalledOnlyPackageResolutionResult resolution, ChartPackage pendingPackage, IInstalledChartLookupIndex installedDirectoryIndex);

    bool HasResourceOverwriteTargetsForInstalledOnlyPackage(ChartPackage package, string destinationDir);

    bool InstallPendingPackageToEstimatedDestination(ChartPackage pendingPackage, string destinationDir);

    (bool Success, CleanupSourceKind SourceKind) TryCleanupPendingPackageSourceForEstimatedInstall(ChartPackage package);

    bool IsPackageStillPending(ChartPackage package);

    void RemovePendingPackagesFromPendingListAndInstallRows(IEnumerable<ChartPackage> packages);

    void LogInfo(string info);
}

internal static class PendingResourceOverwriteCoordinator
{
    internal static PendingInstalledOnlyResourceOverwriteResult OverwritePendingInstalledOnlyPackagesResources(
        BmsLibraryPackageInstallService packageInstallService,
        IPendingResourceOverwriteHost host,
        IEnumerable<ChartPackage> packages,
        CancellationToken token = default,
        Action onEachProcessed = null)
    {
        if (packages == null)
        {
            throw new ArgumentNullException(nameof(packages));
        }
        BmsLibraryOptionsSnapshot options = host.CurrentOptionsSnapshot;
        int deferredProcessedCount = 0;
        try
        {
            using (host.AcquireBmsFilesInitializedAllReaderGuard())
            using (host.AcquirePendingInstallChartsWriterGuard())
            using (host.AcquireBmsFilesWriterGuard())
            using (host.AcquireSongDbInstallWriterGuard())
            {
                bool deletePendingPackageSourceAfterInstall = options.DeletePendingPackageSourceAfterInstall;
                InstalledChartLookupIndexSnapshot installedDirectoryIndexSnapshot = host.CreateInstalledChartLookupSnapshot();
                host.LogInfo("advanced_pending_resource_overwrite scan pendingTotal=" + host.PendingPackageCount + " eligible=" + host.CountEligiblePackages(packages));
                host.LogInfo("advanced_pending_resource_overwrite index_ready hashes=" + installedDirectoryIndexSnapshot.HashCount);
                PendingResourceOverwriteExecutionResult executionResult = packageInstallService.ExecuteInstalledOnlyResourceOverwrite(
                    packages,
                    host.PendingPackages,
                    deletePendingPackageSourceAfterInstall,
                    pendingPackage => host.ResolveInstalledOnlyPackageDestination(pendingPackage, installedDirectoryIndexSnapshot),
                    (resolution, pendingPackage) => host.DescribeSkipDetail(resolution, pendingPackage, installedDirectoryIndexSnapshot),
                    host.HasResourceOverwriteTargetsForInstalledOnlyPackage,
                    host.InstallPendingPackageToEstimatedDestination,
                    host.TryCleanupPendingPackageSourceForEstimatedInstall,
                    host.IsPackageStillPending,
                    token,
                    () => deferredProcessedCount++,
                    info =>
                    {
                        if (!string.IsNullOrWhiteSpace(info))
                        {
                            host.LogInfo(info);
                        }
                    });
                if (executionResult.PendingPackagesToRemove.Count > 0)
                {
                    host.RemovePendingPackagesFromPendingListAndInstallRows(executionResult.PendingPackagesToRemove);
                }
                PendingInstalledOnlyResourceOverwriteResult publicResult = executionResult.ToPublicResult();
                host.LogInfo("advanced_pending_resource_overwrite summary requested=" + publicResult.Requested + " processed=" + publicResult.Processed + " succeededInstall=" + publicResult.SucceededInstall + " succeededCleanupOnly=" + publicResult.SucceededCleanupOnly + " skippedNotPending=" + publicResult.SkippedNotPending + " skippedMissingInstlDst=" + publicResult.SkippedMissingInstlDst + " skippedMultiDst=" + publicResult.SkippedMultiDestination + " skippedNoComponentTarget=" + publicResult.SkippedNoComponentTarget + " failed=" + publicResult.Failed + " canceled=" + publicResult.Canceled);
                return publicResult;
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
