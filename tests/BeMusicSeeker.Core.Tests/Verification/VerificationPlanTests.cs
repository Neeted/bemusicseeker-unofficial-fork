using BeMusicSeeker.TestSupport.Verification;

namespace BeMusicSeeker.Core.Tests.Verification;

/// <summary>module集合と標準worker設定を、ケース発見から独立して検査します。</summary>
[TestClass]
public sealed class VerificationPlanTests
{
    /// <summary>標準filterを独自解釈せず渡し、OR条件全体にDaily制約を結合してmoduleや必須フローを除外しないことを確認します。</summary>
    [TestMethod]
    public void StandardFilterKeepsModuleSelectionAndRequiredDailyScenarios()
    {
        const string filter = "FullyQualifiedName~A|TestCategory=Named";
        var plan = VerificationPlan.Parse(["verify", "--filter", filter], 2);
        CollectionAssert.AreEqual(new[] { "Core", "Windows", "Scenarios" }, plan.Modules.Select(module => module.Name).ToArray());
        Assert.AreEqual(filter, plan.Modules[0].Filter);
        Assert.AreEqual(filter, plan.Modules[1].Filter);
        Assert.AreEqual($"(TestCategory=Daily)&({filter})", plan.Modules[2].Filter);
        CollectionAssert.AreEqual(new[] { "SC-01", "SC-02", "SC-09" }, plan.RequiredScenarioIds["Scenarios"].ToArray());
        Assert.AreEqual($"(TestCategory=Suite.Everything)&({filter})",
            VerificationPlan.Parse(["compatibility", "--filter", filter, "--suite", "Everything"], 2).Modules.Single().Filter);
        Assert.Throws<ArgumentException>(() => VerificationPlan.Parse(["dev", "--filter"], 2));
        Assert.Throws<ArgumentException>(() => VerificationPlan.Parse(["dev", "--filter", filter, "--filter", filter], 2));
    }

    /// <summary>CPU予算をCoreと一つのUI laneへ分け、単一CPUと上限超過でも過剰なworkerを要求しないことを保証します。</summary>
    [TestMethod]
    [DataRow(1, 1, 1)]
    [DataRow(2, 2, 1)]
    [DataRow(32, 16, 15)]
    public void DailyModulesShareFiniteBudget(int processors, int expectedBudget, int coreWorkers)
    {
        var plan = VerificationPlan.Create("dev", processors);
        CollectionAssert.AreEqual(new[] { "Core", "Windows", "Scenarios" }, plan.Modules.Select(module => module.Name).ToArray());
        Assert.AreEqual(expectedBudget, plan.Budget);
        Assert.AreEqual(coreWorkers, plan.Modules[0].Workers);
        Assert.AreEqual("TestCategory=Daily", plan.Modules[2].Filter);
        Assert.IsTrue(plan.Modules.Skip(1).All(module => module.UsesUi && module.Workers == 1));
        Assert.AreEqual(coreWorkers.ToString(), plan.Modules[0].CreateRunSettings().Descendants("Workers").Single().Value);
    }

    /// <summary>FullはReleaseの和集合で日常moduleを重複せず、明示環境・性能suiteを混入させないことを保証します。</summary>
    [TestMethod]
    public void FullAndExplicitSuitesKeepTheirOwnCoverage()
    {
        var full = VerificationPlan.Create("full", 8);
        CollectionAssert.AreEqual(new[] { "Core", "Windows", "Scenarios", "Desktop.E2E" }, full.Modules.Select(module => module.Name).ToArray());
        Assert.IsNull(full.Modules[2].Filter);
        Assert.AreEqual("Release", full.Configuration);
        Assert.IsTrue(full.Format);
        Assert.AreEqual(TimeSpan.FromSeconds(1380), full.TestTimeout);
        CollectionAssert.AreEqual(new[] { "SC-01", "SC-02", "SC-03", "SC-04", "SC-05", "SC-06", "SC-08", "SC-09", "SC-10", "SC-11", "SC-12", "SC-13", "SC-14" }, full.RequiredScenarioIds["Scenarios"].ToArray());
        CollectionAssert.AreEqual(new[] { "SC-15a", "SC-15b", "SC-16", "DT-01", "DT-02", "DT-03" }, full.RequiredScenarioIds["Desktop.E2E"].ToArray());
        Assert.IsTrue(VerificationPlan.Create("verify", 8).Format);
        Assert.IsFalse(VerificationPlan.Create("dev", 8).Format);
        Assert.AreEqual("TestCategory=Suite.Everything", VerificationPlan.Create("compatibility", 8, "Everything").Modules.Single().Filter);
        Assert.AreEqual("Performance", VerificationPlan.Create("performance", 8, "Scan").Modules.Single().Name);
        Assert.Throws<ArgumentException>(() => VerificationPlan.Create("compatibility", 8));
    }

    /// <summary>正式Fullのfilterはplan生成時点で拒否し、準備・publish・test起動へ渡さないことを保証します。</summary>
    [TestMethod]
    [DataRow("TestCategory=Daily")]
    [DataRow("")]
    public void FullRejectsFilterBeforeCreatingAnExecutionPlan(string filter)
        => Assert.Throws<ArgumentException>(() => VerificationPlan.Parse(["full", "--filter", filter], 8));
}
