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
        Assert.AreEqual($"(TestCategory=Suite.chart-info-edge)&({filter})",
            VerificationPlan.Parse(["compatibility", "--filter", filter, "--suite", "chart-info-edge"], 2).Modules.Single().Filter);
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
        CollectionAssert.AreEqual(new[] { "SC-01", "SC-02", "SC-03", "SC-04", "SC-05", "SC-06", "SC-08", "SC-09", "SC-10A", "SC-10B", "SC-11", "SC-12", "SC-13", "SC-14" }, full.RequiredScenarioIds["Scenarios"].ToArray());
        CollectionAssert.AreEqual(new[] { "SC-15a", "SC-15b", "SC-16", "DT-01", "DT-02", "DT-03" }, full.RequiredScenarioIds["Desktop.E2E"].ToArray());
        Assert.IsTrue(VerificationPlan.Create("verify", 8).Format);
        Assert.IsFalse(VerificationPlan.Create("dev", 8).Format);
        Assert.AreEqual("Performance", VerificationPlan.Create("performance", 8, "scan-commit").Modules.Single().Name);
        Assert.Throws<ArgumentException>(() => VerificationPlan.Create("compatibility", 8));
    }

    /// <summary>正式Fullのfilterはplan生成時点で拒否し、準備・publish・test起動へ渡さないことを保証します。</summary>
    [TestMethod]
    [DataRow("TestCategory=Daily")]
    [DataRow("")]
    public void FullRejectsFilterBeforeCreatingAnExecutionPlan(string filter)
        => Assert.Throws<ArgumentException>(() => VerificationPlan.Parse(["full", "--filter", filter], 8));

    /// <summary>登録suiteだけをRelease・単一workerの標準MTPへ渡し、起動から回収までのsuite別上限を適用します。</summary>
    [TestMethod]
    [DataRow("chart-info-bms", 60)]
    [DataRow("chart-info-bmson", 60)]
    [DataRow("chart-info-production", 60)]
    [DataRow("chart-info-production-slow", 60)]
    [DataRow("chart-info-production-latest", 60)]
    [DataRow("chart-info-edge", 60)]
    [DataRow("chart-info-archive", 60)]
    [DataRow("chartstring-java", 60)]
    [DataRow("DriveRoot", 180)]
    [DataRow("DirectorySymbolicLink", 180)]
    [DataRow("CrossVolume", 60)]
    [DataRow("Junction", 60)]
    public void RegisteredCompatibilitySuiteSelectsOneReleaseModule(string suite, int timeoutSeconds)
    {
        var plan = VerificationPlan.Parse(["compatibility", "--suite", suite], 8);
        VerificationModule module = plan.Modules.Single();
        Assert.AreEqual(suite, plan.Suite);
        Assert.AreEqual("Compatibility", module.Name);
        Assert.AreEqual($"TestCategory=Suite.{suite}", module.Filter);
        Assert.AreEqual("Release", plan.Configuration);
        Assert.IsFalse(plan.Format);
        Assert.IsTrue(module.UsesUi);
        Assert.AreEqual(1, module.Workers);
        Assert.AreEqual("MethodLevel", module.CreateRunSettings().Descendants("Scope").Single().Value);
        Assert.AreEqual(TimeSpan.FromSeconds(timeoutSeconds), plan.TestTimeout);
        Assert.AreEqual("tests/verification-compatibility.slnf", plan.SolutionFilter);
        Assert.AreEqual(0, plan.RequiredScenarioIds.Count);
    }

    /// <summary>欠指定・空白・未知・一括名・大小文字違いは実行計画を作らず、準備前に拒否します。</summary>
    [TestMethod]
    [DataRow(null)]
    [DataRow("")]
    [DataRow(" ")]
    [DataRow("Everything")]
    [DataRow("all")]
    [DataRow("environment")]
    [DataRow("Chart-info-bms")]
    public void UnregisteredCompatibilitySuiteCannotCreatePlan(string? suite)
        => Assert.Throws<ArgumentException>(() => VerificationPlan.Parse(
            suite is null ? ["compatibility"] : ["compatibility", "--suite", suite], 8));
    /// <summary>正式性能suiteは全仕事量をRelease・UI排他・単一workerへ渡し、suite固有の監視上限を一回だけ設定します。</summary>
    [TestMethod]
    [DataRow("scan-commit", 180)]
    [DataRow("lr2-sync", 180)]
    [DataRow("folder-mutation", 180)]
    [DataRow("copy-tracking", 60)]
    [DataRow("bmson-continuation", 60)]
    [DataRow("row-cache", 180)]
    [DataRow("playlist-summary", 300)]
    [DataRow("install-estimation", 600)]
    [DataRow("audio", 300)]
    public void RegisteredPerformanceSuiteKeepsFixedWorkloadAndExecutionBoundary(string suite, int timeoutSeconds)
    {
        var plan = VerificationPlan.Parse(["performance", "--suite", suite], 16);
        VerificationModule module = plan.Modules.Single();
        Assert.AreEqual(suite, plan.Suite);
        Assert.AreEqual("Performance", module.Name);
        Assert.AreEqual($"TestCategory=Suite.{suite}", module.Filter);
        Assert.AreEqual("Release", plan.Configuration);
        Assert.IsFalse(plan.Format);
        Assert.IsTrue(module.UsesUi);
        Assert.AreEqual(1, module.Workers);
        Assert.AreEqual("1", module.CreateRunSettings().Descendants("Workers").Single().Value);
        Assert.AreEqual("MethodLevel", module.CreateRunSettings().Descendants("Scope").Single().Value);
        Assert.AreEqual(TimeSpan.FromSeconds(timeoutSeconds), plan.TestTimeout);
        Assert.AreEqual("tests/verification-performance.slnf", plan.SolutionFilter);
        Assert.AreEqual(0, plan.RequiredScenarioIds.Count);
    }

    /// <summary>未指定・未知suiteと部分filterを準備前に拒否し、全負荷を実行しない正式成功を作りません。</summary>
    [TestMethod]
    public void PerformanceRejectsMissingUnknownSuiteAndPartialFilter()
    {
        foreach (string? suite in new string?[] { null, "", " ", "all", "Scan", "Audio" })
        {
            Assert.Throws<ArgumentException>(() => VerificationPlan.Parse(
                suite is null ? ["performance"] : ["performance", "--suite", suite], 8));
        }
        foreach (string filter in new[] { "", "FullyQualifiedName~Single", "TestCategory=Suite.audio" })
        {
            Assert.Throws<ArgumentException>(() => VerificationPlan.Parse(
                ["performance", "--suite", "audio", "--filter", filter], 8));
        }
    }

}
