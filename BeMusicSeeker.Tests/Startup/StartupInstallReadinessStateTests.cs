using BeMusicSeeker.Models.BmsLibraryInternal;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace BeMusicSeeker.Tests;

[TestClass]
public sealed class StartupInstallReadinessStateTests
{
    /// <summary>
    /// 保証：カタログ・リソース索引・保留パッケージがすべて準備できるまで推定を禁止し、不足する準備を診断へ残す。
    /// </summary>
    [TestMethod]
    public void CanStartInstallEstimation_RequiresCatalogResourceIndexAndPendingPackages()
    {
        var state = new StartupInstallReadinessState();

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

    /// <summary>
    /// 保証：前提が不足する間は推定準備完了へ遷移せず、全前提成立後も完了への遷移を一度だけ受理する。
    /// </summary>
    [TestMethod]
    public void TryMarkInstallEstimationReady_CompletesOnlyOnceAfterPrerequisites()
    {
        var state = new StartupInstallReadinessState();

        Assert.IsFalse(state.TryMarkInstallEstimationReady());
        Assert.IsFalse(state.InstallEstimationReady);

        state.MarkCatalogLoaded();
        state.MarkDestinationResourceIndexReady();
        state.MarkPendingPackagesRestored();

        Assert.IsTrue(state.TryMarkInstallEstimationReady());
        Assert.IsTrue(state.InstallEstimationReady);
        Assert.IsFalse(state.TryMarkInstallEstimationReady());
    }

    /// <summary>
    /// 保証：推定準備完了前は導入を許可せず、推定完了後の導入準備完了への遷移を一度だけ受理する。
    /// </summary>
    [TestMethod]
    public void TryMarkInstallReady_CompletesOnlyOnceAfterInstallEstimationReady()
    {
        var state = new StartupInstallReadinessState();

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

    /// <summary>
    /// 保証：完了済みの起動状態をリセットすると全準備条件を未完了へ戻し、旧ライブラリの準備完了を再利用しない。
    /// </summary>
    [TestMethod]
    public void Reset_ClearsAllReadinessTokens()
    {
        var state = new StartupInstallReadinessState();
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
