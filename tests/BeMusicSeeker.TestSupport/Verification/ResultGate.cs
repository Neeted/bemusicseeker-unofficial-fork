using System.Globalization;
using System.Xml.Linq;

namespace BeMusicSeeker.TestSupport.Verification;

/// <summary>Nameを実行IDとして照合する、一実行の実終了と今回の標準reportの所在です。</summary>
internal sealed record ModuleExecution(string Name, string ReportPath, int? ExitCode, bool Completed,
    string? PrimaryFailure = null, string? CleanupFailure = null, double? ElapsedMs = null);

/// <summary>Nameを実行IDとして保持する標準reportの件数・不成功理由と、起動から回収完了までの時間です。未起動の時間はnullです。</summary>
internal sealed record ModuleOutcome(string Name, int Cases, IReadOnlyList<string> Errors, double? ElapsedMs = null,
    IReadOnlyList<string>? ScenarioIds = null)
{
    /// <summary>選択された全ケースとprocessが成功した場合だけ真です。</summary>
    internal bool Passed => Errors.Count == 0;
}

/// <summary>ケース発見を再実装せず、今回のprocessと標準TRXの全Passedを照合します。</summary>
internal static class ResultGate
{
    private static readonly XNamespace Trx = "http://microsoft.com/schemas/VisualStudio/TeamTest/2010";

    /// <summary>期待実行IDを別実行や以前の結果で補完せず検査します。</summary>
    internal static IReadOnlyList<ModuleOutcome> Evaluate(IReadOnlyList<string> expectedModules,
        IReadOnlyList<ModuleExecution> executions, IReadOnlyDictionary<string, IReadOnlyList<string>>? requiredScenarioIds = null)
    {
        var outcomes = new List<ModuleOutcome>();
        foreach (string name in expectedModules)
        {
            ModuleExecution[] matches = executions.Where(execution => execution.Name == name).ToArray();
            if (matches.Length != 1)
            {
                outcomes.Add(new(name, 0, ["期待moduleの実行結果が欠落または重複しています。"]));
                continue;
            }
            IReadOnlyList<string> required = requiredScenarioIds is not null && requiredScenarioIds.TryGetValue(name, out IReadOnlyList<string>? names) ? names : [];
            outcomes.Add(Read(matches[0], required));
        }
        foreach (ModuleExecution execution in executions.Where(execution => !expectedModules.Contains(execution.Name)))
        {
            outcomes.Add(new(execution.Name, 0, ["未選択moduleが実行されています。"]));
        }
        foreach (IGrouping<string, (string Id, string Module)>? duplicate in outcomes.SelectMany(outcome => (outcome.ScenarioIds ?? []).Select(id => (Id: id, Module: outcome.Name)))
            .GroupBy(item => item.Id, StringComparer.Ordinal).Where(group => group.Select(item => item.Module).Distinct(StringComparer.Ordinal).Count() > 1))
        {
            for (int index = 0; index < outcomes.Count; index++)
            {
                if (outcomes[index].ScenarioIds?.Contains(duplicate.Key) == true)
                {
                    outcomes[index] = outcomes[index] with { Errors = [.. outcomes[index].Errors, $"同じシナリオIDが複数moduleにあります: {duplicate.Key}"] };
                }
            }
        }
        return outcomes;
    }

    private static ModuleOutcome Read(ModuleExecution execution, IReadOnlyList<string> requiredScenarioIds)
    {
        var errors = new List<string>();
        if (!execution.Completed)
        {
            errors.Add("processと所有childの終端を確認できません。");
        }

        if (execution.ExitCode != 0)
        {
            errors.Add($"process終了コード: {execution.ExitCode?.ToString(CultureInfo.InvariantCulture) ?? "未取得"}");
        }

        if (execution.PrimaryFailure is not null)
        {
            errors.Add(execution.PrimaryFailure);
        }

        if (execution.CleanupFailure is not null)
        {
            errors.Add("回収失敗: " + execution.CleanupFailure);
        }

        int cases = 0;
        var scenarioIds = new HashSet<string>(StringComparer.Ordinal);
        try
        {
            var document = XDocument.Load(execution.ReportPath);
            XElement root = document.Root ?? throw new InvalidDataException("TRXのrootがありません。");
            if (root.Name != Trx + "TestRun")
            {
                throw new InvalidDataException("標準TRXのTestRunではありません。");
            }

            XElement summary = RequireSingleElement(root, "ResultSummary");
            if ((string?)summary.Attribute("outcome") is not ("Completed" or "Passed"))
            {
                errors.Add("test runが正常完了していません。");
            }

            XElement results = RequireSingleElement(root, "Results");
            XElement[] rows = results.Elements(Trx + "UnitTestResult").ToArray();
            cases = rows.Length;
            foreach (XElement row in rows)
            {
                string? id = ReadScenarioId((string?)row.Attribute("testName"));
                if (id is not null)
                {
                    scenarioIds.Add(id);
                }
            }
            if (cases == 0)
            {
                errors.Add("選択されたケースがゼロ件です。");
            }

            if (rows.Any(row => (string?)row.Attribute("outcome") != "Passed"))
            {
                errors.Add("選択ケースにPassed以外（skipを含む）があります。");
            }

            XElement counters = RequireSingleElement(summary, "Counters");
            foreach (string counter in new[] { "total", "executed", "passed" })
            {
                if (!int.TryParse((string?)counters.Attribute(counter), NumberStyles.None, CultureInfo.InvariantCulture, out int count) || count != cases)
                {
                    errors.Add($"TRXの{counter}件数と結果行が一致しません。");
                }
            }
            foreach (string counter in new[] { "failed", "error", "timeout", "aborted", "inconclusive", "passedButRunAborted", "notRunnable", "notExecuted", "disconnected", "warning", "completed", "inProgress", "pending" })
            {
                XAttribute? attribute = counters.Attribute(counter);
                if (attribute is not null && (!int.TryParse(attribute.Value, NumberStyles.None, CultureInfo.InvariantCulture, out int count) || count != 0))
                {
                    errors.Add($"TRXの{counter}件数が全Passedの結果と矛盾しています。");
                }
            }
            foreach (string required in requiredScenarioIds)
            {
                if (!scenarioIds.Contains(required))
                {
                    errors.Add($"必須シナリオが成功していません: {required}");
                }
            }
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or System.Xml.XmlException or InvalidDataException)
        {
            errors.Add("標準reportを回収できません: " + error.Message);
        }
        return new ModuleOutcome(execution.Name, cases, errors, execution.ElapsedMs, scenarioIds.Order(StringComparer.Ordinal).ToArray());
    }

    private static string? ReadScenarioId(string? testName)
    {
        if (testName is null || !testName.StartsWith("[", StringComparison.Ordinal))
        {
            return null;
        }
        int end = testName.IndexOf(']');
        if (end <= 1 || (end + 1 < testName.Length && !char.IsWhiteSpace(testName[end + 1]) && testName[end + 1] != '('))
        {
            return null;
        }
        string id = testName[1..end];
        return id.StartsWith("SC-", StringComparison.Ordinal) || id.StartsWith("DT-", StringComparison.Ordinal) ? id : null;
    }

    private static XElement RequireSingleElement(XElement parent, string name)
    {
        XElement[] elements = parent.Elements(Trx + name).ToArray();
        return elements.Length == 1 ? elements[0] : throw new InvalidDataException($"{name}が欠落または重複しています。");
    }
}
