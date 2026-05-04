using BeMusicSeeker.Models.BmsLibraryInternal;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace BeMusicSeeker.Tests;

[TestClass]
public sealed class StartupInstallReadinessStateTests
{
    [TestMethod]
    public void CanStartInstallEstimation_RequiresCatalogResourceIndexAndPendingPackages()
    {
        StartupInstallReadinessState state = new StartupInstallReadinessState();

        Assert.IsFalse(state.CanStartInstallEstimation());
        Assert.AreEqual("catalog,resource_index,pending_packages", state.GetInstallEstimationBlockedReason());

        state.MarkCatalogLoaded();
        Assert.IsFalse(state.CanStartInstallEstimation());
        Assert.AreEqual("resource_index,pending_packages", state.GetInstallEstimationBlockedReason());

        state.MarkDestinationResourceIndexReady();
        Assert.IsFalse(state.CanStartInstallEstimation());
        Assert.AreEqual("pending_packages", state.GetInstallEstimationBlockedReason());

        state.MarkPendingPackagesRestored();
        Assert.IsTrue(state.CanStartInstallEstimation());
        Assert.AreEqual(string.Empty, state.GetInstallEstimationBlockedReason());
    }

    [TestMethod]
    public void TryMarkInstallEstimationReady_CompletesOnlyOnceAfterPrerequisites()
    {
        StartupInstallReadinessState state = new StartupInstallReadinessState();

        Assert.IsFalse(state.TryMarkInstallEstimationReady());
        Assert.IsFalse(state.InstallEstimationReady);

        state.MarkCatalogLoaded();
        state.MarkDestinationResourceIndexReady();
        state.MarkPendingPackagesRestored();

        Assert.IsTrue(state.TryMarkInstallEstimationReady());
        Assert.IsTrue(state.InstallEstimationReady);
        Assert.IsFalse(state.TryMarkInstallEstimationReady());
    }

    [TestMethod]
    public void TryMarkInstallReady_CompletesOnlyOnceAfterInstallEstimationReady()
    {
        StartupInstallReadinessState state = new StartupInstallReadinessState();

        Assert.IsFalse(state.TryMarkInstallReady());
        Assert.IsFalse(state.InstallReady);

        state.MarkCatalogLoaded();
        state.MarkDestinationResourceIndexReady();
        state.MarkPendingPackagesRestored();
        Assert.IsTrue(state.TryMarkInstallEstimationReady());

        Assert.IsTrue(state.TryMarkInstallReady());
        Assert.IsTrue(state.InstallReady);
        Assert.IsFalse(state.TryMarkInstallReady());
    }

    [TestMethod]
    public void Reset_ClearsAllReadinessTokens()
    {
        StartupInstallReadinessState state = new StartupInstallReadinessState();
        state.MarkCatalogLoaded();
        state.MarkDestinationResourceIndexReady();
        state.MarkPendingPackagesRestored();
        state.TryMarkInstallEstimationReady();
        state.TryMarkInstallReady();

        state.Reset();

        Assert.IsFalse(state.CatalogLoaded);
        Assert.IsFalse(state.DestinationResourceIndexReady);
        Assert.IsFalse(state.PendingPackagesRestored);
        Assert.IsFalse(state.InstallEstimationReady);
        Assert.IsFalse(state.InstallReady);
    }
}
