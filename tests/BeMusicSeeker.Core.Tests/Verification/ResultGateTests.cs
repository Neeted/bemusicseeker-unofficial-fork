using System.Xml.Linq;
using BeMusicSeeker.TestSupport.Files;
using BeMusicSeeker.TestSupport.Verification;

namespace BeMusicSeeker.Core.Tests.Verification;

/// <summary>標準reportとprocessの結果が一致する場合だけ成功する境界を検査します。</summary>
[TestClass]
public sealed class ResultGateTests
{
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

    private static XDocument CreateReport(string[] outcomes)
    {
        XNamespace ns = "http://microsoft.com/schemas/VisualStudio/TeamTest/2010";
        return new XDocument(new XElement(ns + "TestRun", new XElement(ns + "Results", outcomes.Select((outcome, index) =>
            new XElement(ns + "UnitTestResult", new XAttribute("testName", "case-" + index), new XAttribute("outcome", outcome)))),
            new XElement(ns + "ResultSummary", new XAttribute("outcome", "Completed"),
                new XElement(ns + "Counters", new XAttribute("total", outcomes.Length), new XAttribute("executed", outcomes.Length), new XAttribute("passed", outcomes.Count(outcome => outcome == "Passed"))))));
    }
}
