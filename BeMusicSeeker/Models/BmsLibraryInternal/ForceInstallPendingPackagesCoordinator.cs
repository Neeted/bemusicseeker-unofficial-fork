using System;
using System.Collections.Generic;
using System.Linq;

namespace BeMusicSeeker.Models.BmsLibraryInternal;

internal interface IForceInstallPendingPackagesHost
{
    bool TryBlockLr2SongDbSyncMutation(string operation);

    IDisposable AcquireBmsFilesInitializedAllReaderGuard();

    IDisposable AcquirePendingInstallChartsWriterGuard();

    IDisposable AcquireBmsFilesWriterGuard();

    IDisposable AcquireSongDbInstallWriterGuard();

    bool HasBmsFiles { get; }

    IEnumerable<ChartPackage> PendingPackages { get; }

    IEnumerable<ChartPackage> InstalledPackages { get; }

    bool ConfirmNormalInstallOverride();

    List<ChartPackage> InstallChartPackagesForForceInstall(IEnumerable<ChartPackage> packagesToInstall, List<ChartPackage> deferredInstalledPackages);

    void ApplyPendingPackageRemovals(IEnumerable<ChartPackage> packagesToRemove);

    void ReplaceInstalledPackages(IEnumerable<ChartPackage> packages);

    void LogInfo(string info);
}

internal static class ForceInstallPendingPackagesCoordinator
{
    internal static void ForceInstallPendingPackages(
        BmsLibraryPackageInstallService packageInstallService,
        IForceInstallPendingPackagesHost host,
        IEnumerable<ChartPackage> packages,
        bool? approveNormalInstallOverride,
        ISet<ChartPackage> approvedNormalInstallOverridePackages)
    {
        if (packages == null)
        {
            throw new ArgumentNullException(nameof(packages));
        }
        if (host.TryBlockLr2SongDbSyncMutation(nameof(ForceInstallPendingPackages)))
        {
            return;
        }
        using (host.AcquireBmsFilesInitializedAllReaderGuard())
        using (host.AcquirePendingInstallChartsWriterGuard())
        using (host.AcquireBmsFilesWriterGuard())
        using (host.AcquireSongDbInstallWriterGuard())
        {
            if (!host.HasBmsFiles)
            {
                return;
            }

            ForceInstallBatchResult result = packageInstallService.ForceInstallPackages(
                packages,
                host.PendingPackages,
                pendingPackage => ShouldApproveNormalInstallOverride(host, pendingPackage, approveNormalInstallOverride, approvedNormalInstallOverridePackages),
                host.InstallChartPackagesForForceInstall,
                host.LogInfo);
            if (result.Requested == 0)
            {
                return;
            }

            host.LogInfo("force_install_batch start requested=" + result.Requested);
            if (result.PendingPackagesToRemove.Count > 0)
            {
                host.ApplyPendingPackageRemovals(result.PendingPackagesToRemove);
            }

            int installedAdded = MergeDeferredInstalledPackages(host, result.DeferredInstalledPackages);
            host.LogInfo("force_install_batch summary requested=" + result.Requested + " processed=" + result.Processed + " succeeded=" + result.Succeeded + " failed=" + result.Failed + " skipped=" + result.Skipped + " pendingRemoved=" + result.PendingPackagesToRemove.Count + " installedAdded=" + installedAdded);
        }
    }

    private static bool ShouldApproveNormalInstallOverride(
        IForceInstallPendingPackagesHost host,
        ChartPackage pendingPackage,
        bool? approveNormalInstallOverride,
        ISet<ChartPackage> approvedNormalInstallOverridePackages)
    {
        if (approvedNormalInstallOverridePackages?.Contains(pendingPackage) == true
            || approvedNormalInstallOverridePackages?.Any(package =>
                package != null
                && pendingPackage != null
                && !string.IsNullOrWhiteSpace(package.path)
                && !string.IsNullOrWhiteSpace(pendingPackage.path)
                && string.Equals(package.path, pendingPackage.path, StringComparison.OrdinalIgnoreCase)) == true)
        {
            return true;
        }

        if (approveNormalInstallOverride == false)
        {
            return false;
        }

        return host.ConfirmNormalInstallOverride();
    }

    private static int MergeDeferredInstalledPackages(IForceInstallPendingPackagesHost host, IReadOnlyCollection<ChartPackage> deferredInstalledPackages)
    {
        if (deferredInstalledPackages.Count == 0)
        {
            return 0;
        }

        List<ChartPackage> installedPackages = [.. host.InstalledPackages.Where(pkg => pkg != null)];
        var installedSet = new HashSet<ChartPackage>(installedPackages);
        int installedAdded = 0;
        foreach (ChartPackage deferredInstalledPackage in deferredInstalledPackages)
        {
            if (deferredInstalledPackage != null && installedSet.Add(deferredInstalledPackage))
            {
                installedPackages.Add(deferredInstalledPackage);
                installedAdded++;
            }
        }
        if (installedAdded > 0)
        {
            host.ReplaceInstalledPackages(installedPackages);
        }
        return installedAdded;
    }
}
