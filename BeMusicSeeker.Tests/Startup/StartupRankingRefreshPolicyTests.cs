using BeMusicSeeker.Models.BmsLibraryInternal;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace BeMusicSeeker.Tests;

[TestClass]
public sealed class StartupRankingRefreshPolicyTests
{
    [TestMethod]
    public void CreateWorkPlanMapsTheFourIndependentFlagCombinations()
    {
        (bool RefreshIrScore, bool RefreshRankingCache, bool HasWork)[] cases =
        [
            (false, false, false),
            (false, true, true),
            (true, false, true),
            (true, true, true)
        ];

        foreach ((bool refreshIrScore, bool refreshRankingCache, bool hasWork) in cases)
        {
            BmsLibraryOptionsSnapshot options = new()
            {
                EnableDownloadLr2IrScoreAndDetectUnsent = refreshIrScore,
                UpdateLr2IrRankingCacheOnStartup = refreshRankingCache
            };

            StartupRankingRefreshWorkPlan plan = StartupRankingRefreshPolicy.CreateWorkPlan(options);

            Assert.AreEqual(refreshIrScore, plan.RefreshIrScore);
            Assert.AreEqual(refreshRankingCache, plan.RefreshRankingCache);
            Assert.AreEqual(hasWork, plan.HasWork);
        }
    }

    [TestMethod]
    public void ShouldQueueRequiresOnlyTheExistingQueueInputs()
    {
        StartupRankingRefreshWorkPlan plan = new(
            RefreshIrScore: true,
            RefreshRankingCache: false);

        Assert.IsTrue(StartupRankingRefreshPolicy.ShouldQueue(
            updateRequested: true,
            activeScoreSource: ActiveScoreSource.Lr2,
            hasLr2ScoreDatabase: true,
            plan: plan));
        Assert.IsFalse(StartupRankingRefreshPolicy.ShouldQueue(
            updateRequested: false,
            activeScoreSource: ActiveScoreSource.Lr2,
            hasLr2ScoreDatabase: true,
            plan: plan));
        Assert.IsFalse(StartupRankingRefreshPolicy.ShouldQueue(
            updateRequested: true,
            activeScoreSource: ActiveScoreSource.None,
            hasLr2ScoreDatabase: true,
            plan: plan));
        Assert.IsFalse(StartupRankingRefreshPolicy.ShouldQueue(
            updateRequested: true,
            activeScoreSource: ActiveScoreSource.Beatoraja,
            hasLr2ScoreDatabase: true,
            plan: plan));
        Assert.IsFalse(StartupRankingRefreshPolicy.ShouldQueue(
            updateRequested: true,
            activeScoreSource: ActiveScoreSource.Lr2,
            hasLr2ScoreDatabase: false,
            plan: plan));
    }

    [TestMethod]
    public void ShouldQueueSuppressesWhenBothRefreshFlagsAreDisabled()
    {
        StartupRankingRefreshWorkPlan plan = new(
            RefreshIrScore: false,
            RefreshRankingCache: false);

        Assert.IsFalse(StartupRankingRefreshPolicy.ShouldQueue(
            updateRequested: true,
            activeScoreSource: ActiveScoreSource.Lr2,
            hasLr2ScoreDatabase: true,
            plan: plan));
    }

    [TestMethod]
    public void EachRefreshFlagIsIndependent()
    {
        StartupRankingRefreshWorkPlan irOnly = StartupRankingRefreshPolicy.CreateWorkPlan(new BmsLibraryOptionsSnapshot
        {
            EnableDownloadLr2IrScoreAndDetectUnsent = true
        });
        StartupRankingRefreshWorkPlan cacheOnly = StartupRankingRefreshPolicy.CreateWorkPlan(new BmsLibraryOptionsSnapshot
        {
            UpdateLr2IrRankingCacheOnStartup = true
        });

        Assert.IsTrue(irOnly.RefreshIrScore);
        Assert.IsFalse(irOnly.RefreshRankingCache);
        Assert.IsFalse(cacheOnly.RefreshIrScore);
        Assert.IsTrue(cacheOnly.RefreshRankingCache);
        Assert.IsTrue(StartupRankingRefreshPolicy.ShouldQueue(true, ActiveScoreSource.Lr2, true, irOnly));
        Assert.IsTrue(StartupRankingRefreshPolicy.ShouldQueue(true, ActiveScoreSource.Lr2, true, cacheOnly));
    }

    [TestMethod]
    public void PlanIgnoresOperationModeAndOfflineEstimateAndCanBeReevaluated()
    {
        BmsLibraryOptionsSnapshot initialOptions = new()
        {
            OperationModeLR2DB = false,
            EstimateOfflineScoreRanking = false,
            EnableDownloadLr2IrScoreAndDetectUnsent = true
        };
        BmsLibraryOptionsSnapshot latestOptions = new()
        {
            OperationModeLR2DB = true,
            EstimateOfflineScoreRanking = true,
            UpdateLr2IrRankingCacheOnStartup = true
        };

        StartupRankingRefreshWorkPlan initialPlan = StartupRankingRefreshPolicy.CreateWorkPlan(initialOptions);
        StartupRankingRefreshWorkPlan latestPlan = StartupRankingRefreshPolicy.CreateWorkPlan(latestOptions);

        Assert.IsTrue(initialPlan.RefreshIrScore);
        Assert.IsFalse(initialPlan.RefreshRankingCache);
        Assert.IsFalse(latestPlan.RefreshIrScore);
        Assert.IsTrue(latestPlan.RefreshRankingCache);
        Assert.IsTrue(StartupRankingRefreshPolicy.ShouldQueue(true, ActiveScoreSource.Lr2, true, initialPlan));
        Assert.IsTrue(StartupRankingRefreshPolicy.ShouldQueue(true, ActiveScoreSource.Lr2, true, latestPlan));
    }
}
