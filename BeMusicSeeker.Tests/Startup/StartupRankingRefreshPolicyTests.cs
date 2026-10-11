using BeMusicSeeker.Models.BmsLibraryInternal;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace BeMusicSeeker.Tests;

[TestClass]
public sealed class StartupRankingRefreshPolicyTests
{
    /// <summary>
    /// 保証：IR取得とランキングキャッシュ更新の四組合せを別々の要求へ写し、両方無効の場合だけ作業なしとして不要な同期を防ぐ。
    /// </summary>
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

    /// <summary>
    /// 保証：明示更新・LR2参照・LR2スコアDBがそろう場合だけ起動時ランキング処理を予約し、別スコア元やDB欠落で予約しない。
    /// </summary>
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

    /// <summary>
    /// 保証：両更新フラグが無効なら他の予約条件がそろっても作業を開始せず、無設定の外部同期を防ぐ。
    /// </summary>
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

    /// <summary>
    /// 保証：IR取得のみ・キャッシュ更新のみを独立して予約でき、一方の設定を他方の前提として扱わない。
    /// </summary>
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

    /// <summary>
    /// 保証：操作モードとオフライン推定に左右されず更新フラグを再評価し、先の起動計画を後の要求へ流用しない。
    /// </summary>
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
