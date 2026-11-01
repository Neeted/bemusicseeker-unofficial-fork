using System.Xml.Linq;
using BeMusicSeeker.TestSupport.Files;
using BeMusicSeeker.TestSupport.Verification;

namespace BeMusicSeeker.Core.Tests.Verification;

/// <summary>標準reportとprocessの結果が一致する場合だけ成功する境界を検査します。</summary>
[TestClass]
public sealed class ResultGateTests
{
    /// <summary>標準reportの短IDをDataRow名から集約し、同moduleの複数成功を許しながら欠落・skip・別module重複を拒否します。</summary>
    [TestMethod]
    [DataRow("single", true)]
    [DataRow("rows", true)]
    [DataRow("missing", false)]
    [DataRow("prefix", false)]
    [DataRow("skip", false)]
    [DataRow("wrong-module", false)]
    [DataRow("duplicate-module", false)]
    public void ScenarioIdsMatchRequiredFlowsWithoutLimitingDataRows(string condition, bool expected)
    {
        using var workspace = TestWorkspace.Create(TestRunDirectory.RootDirectory);
        string core = workspace.GetPath("core.trx");
        string scenarios = workspace.GetPath("scenarios.trx");
        CreateReport(["Passed"], [condition is "wrong-module" or "duplicate-module" ? "[SC-01] misplaced" : "Core component"]).Save(core);
        string[] names = condition switch
        {
            "rows" => ["[SC-01] flow (1)", "[SC-01] flow (2)"],
            "missing" or "wrong-module" => ["[SC-02] other flow"],
            "prefix" => ["[SC-010] neighbor"],
            _ => ["[SC-01] flow"],
        };
        CreateReport(names.Select(_ => condition == "skip" ? "NotExecuted" : "Passed").ToArray(), names).Save(scenarios);
        IReadOnlyList<ModuleOutcome> results = ResultGate.Evaluate(["Core", "Scenarios"],
            [new("Core", core, 0, true), new("Scenarios", scenarios, 0, true)],
            new Dictionary<string, IReadOnlyList<string>> { ["Scenarios"] = ["SC-01"] });
        Assert.AreEqual(expected, results.All(result => result.Passed), string.Join(";", results.SelectMany(result => result.Errors)));
        if (condition == "rows")
        {
            Assert.AreEqual(2, results.Single(result => result.Name == "Scenarios").Cases);
        }
    }

    /// <summary>実行完了・exit0・全行Passedが揃う場合は、DataRow相当の複数結果を落とさず成功させます。</summary>
    [TestMethod]
    [DataRow(1)]
    [DataRow(3)]
    public void CompletePassedReportCountsAllRows(int count)
    {
        using var workspace = TestWorkspace.Create(TestRunDirectory.RootDirectory);
        string path = workspace.GetPath("result.trx");
        CreateReport(Enumerable.Repeat("Passed", count).ToArray()).Save(path);
        ModuleOutcome result = ResultGate.Evaluate(["Core"], [new("Core", path, 0, true)]).Single();
        Assert.IsTrue(result.Passed, string.Join(";", result.Errors));
        Assert.AreEqual(count, result.Cases);
    }

    /// <summary>reportの欠落・破損・未完了・ゼロ件・部分skip・全skipとprocess失敗を、他の成功情報で補完しないことを保証します。</summary>
    [TestMethod]
    [DataRow("missing")]
    [DataRow("malformed")]
    [DataRow("zero")]
    [DataRow("skip")]
    [DataRow("partial-skip")]
    [DataRow("failed")]
    [DataRow("counter")]
    [DataRow("failed-counter")]
    [DataRow("skipped-counter")]
    [DataRow("invalid-counter")]
    [DataRow("summary")]
    [DataRow("duplicate-results")]
    [DataRow("duplicate-summary")]
    [DataRow("duplicate-counters")]
    [DataRow("exit")]
    [DataRow("incomplete")]
    [DataRow("timeout")]
    [DataRow("cleanup")]
    [DataRow("duplicate")]
    [DataRow("wrong-module")]
    public void NonSuccessCannotBeHiddenBySuccessfulReportOrOtherModule(string condition)
    {
        using var workspace = TestWorkspace.Create(TestRunDirectory.RootDirectory);
        string path = workspace.GetPath("result.trx");
        string[] rows = condition switch { "zero" => [], "skip" => ["NotExecuted"], "partial-skip" => ["Passed", "NotExecuted"], "failed" => ["Failed"], _ => ["Passed"] };
        XDocument document = CreateReport(rows);
        XNamespace ns = document.Root?.Name.Namespace ?? throw new InvalidOperationException();
        if (condition == "counter")
        {
            document.Descendants(ns + "Counters").Single().SetAttributeValue("total", 5);
        }
        if (condition is "failed-counter" or "skipped-counter" or "invalid-counter")
        {
            document.Descendants(ns + "Counters").Single().SetAttributeValue(condition == "skipped-counter" ? "notExecuted" : "failed",
                condition == "invalid-counter" ? "invalid" : "1");
        }

        if (condition == "summary")
        {
            document.Descendants(ns + "ResultSummary").Single().SetAttributeValue("outcome", "Aborted");
        }

        string? duplicatedElement = condition switch
        {
            "duplicate-results" => "Results",
            "duplicate-summary" => "ResultSummary",
            "duplicate-counters" => "Counters",
            _ => null,
        };
        if (duplicatedElement is not null)
        {
            XElement original = document.Descendants(ns + duplicatedElement).Single();
            var duplicate = new XElement(original);
            if (duplicatedElement == "Results") { duplicate.Elements().Single().SetAttributeValue("outcome", "Failed"); }
            else { duplicate.SetAttributeValue(duplicatedElement == "Counters" ? "failed" : "outcome", duplicatedElement == "Counters" ? "1" : "Aborted"); }
            original.AddAfterSelf(duplicate);
        }

        if (condition != "missing")
        {
            document.Save(path);
        }

        if (condition == "malformed")
        {
            File.WriteAllText(path, "<TestRun>");
        }

        var execution = new ModuleExecution(condition == "wrong-module" ? "Windows" : "Core", path,
            condition == "exit" ? 1 : 0, condition != "incomplete",
            condition == "timeout" ? "期限超過" : null, condition == "cleanup" ? "回収失敗" : null);
        IReadOnlyList<ModuleOutcome> results = ResultGate.Evaluate(["Core"], condition == "duplicate" ? [execution, execution] : [execution]);
        Assert.IsTrue(results.Any(result => !result.Passed));
    }

    /// <summary>必須シナリオの未出現は他ケースの全Passedで埋めず、未選択suiteを実行済みと報告しません。</summary>
    [TestMethod]
    public void RequiredScenarioMustAppearInSelectedModule()
    {
        using var workspace = TestWorkspace.Create(TestRunDirectory.RootDirectory);
        string path = workspace.GetPath("result.trx");
        CreateReport(["Passed"]).Save(path);
        IReadOnlyList<ModuleOutcome> results = ResultGate.Evaluate(["Scenarios"], [new("Scenarios", path, 0, true)],
            new Dictionary<string, IReadOnlyList<string>> { ["Scenarios"] = ["SC-01"] });
        Assert.IsFalse(results.Single().Passed);
        Assert.AreEqual("Scenarios", results.Single().Name);
    }

    private static XDocument CreateReport(string[] outcomes, string[]? names = null)
    {
        XNamespace ns = "http://microsoft.com/schemas/VisualStudio/TeamTest/2010";
        return new XDocument(new XElement(ns + "TestRun", new XElement(ns + "Results", outcomes.Select((outcome, index) =>
            new XElement(ns + "UnitTestResult", new XAttribute("testName", names is null ? "case-" + index : names[index]), new XAttribute("outcome", outcome)))),
            new XElement(ns + "ResultSummary", new XAttribute("outcome", "Completed"),
                new XElement(ns + "Counters", new XAttribute("total", outcomes.Length), new XAttribute("executed", outcomes.Length), new XAttribute("passed", outcomes.Count(outcome => outcome == "Passed"))))));
    }
}
