using System.Collections.Generic;

namespace BeMusicSeeker.Models.BmsLibraryInternal;

internal sealed class StartupInstallReadinessState
{
    public bool CatalogLoaded { get; private set; }

    public bool DestinationResourceIndexReady { get; private set; }

    public bool PendingPackagesRestored { get; private set; }

    public bool InstallEstimationReady { get; private set; }

    public bool InstallReady { get; private set; }

    public void Reset()
    {
        CatalogLoaded = false;
        DestinationResourceIndexReady = false;
        PendingPackagesRestored = false;
        InstallEstimationReady = false;
        InstallReady = false;
    }

    public void MarkCatalogLoaded()
    {
        CatalogLoaded = true;
    }

    public void MarkDestinationResourceIndexReady()
    {
        DestinationResourceIndexReady = true;
    }

    public void MarkPendingPackagesRestored()
    {
        PendingPackagesRestored = true;
    }

    public bool CanStartInstallEstimation()
    {
        return CatalogLoaded && DestinationResourceIndexReady && PendingPackagesRestored;
    }

    public bool TryMarkInstallEstimationReady()
    {
        if (InstallEstimationReady || !CanStartInstallEstimation())
        {
            return false;
        }
        InstallEstimationReady = true;
        return true;
    }

    public bool TryMarkInstallReady()
    {
        if (InstallReady || !InstallEstimationReady)
        {
            return false;
        }
        InstallReady = true;
        return true;
    }

    public string GetInstallEstimationBlockedReason()
    {
        if (CanStartInstallEstimation())
        {
            return string.Empty;
        }
        List<string> missing = new List<string>();
        if (!CatalogLoaded)
        {
            missing.Add("catalog");
        }
        if (!DestinationResourceIndexReady)
        {
            missing.Add("resource_index");
        }
        if (!PendingPackagesRestored)
        {
            missing.Add("pending_packages");
        }
        return string.Join(",", missing);
    }
}
