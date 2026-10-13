using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace BeMusicSeeker.Tests;

/// <summary>整形範囲の選択と、整形から標準検証への接続を確かめます。</summary>
[TestClass]
[TestCategory("ProcessIntegration")]
public sealed class RepositoryFormattingTests
{
    /// <summary>実Gitのindex・作業ツリー・未追跡から対象を選び、削除・生成物・変更外を含めないことを確かめます。</summary>
    /// <remarks>GitのUTF-8出力と空白・角括弧を含むパスを実処理へ通し、formatterへの引数を観測します。SDKの修正内容はこのケースの対象外です。</remarks>
    [TestMethod]
    public void ChangedScopeIncludesIndexWorktreeAndUntrackedFilesWithoutWideningScope()
    {
        using JsonDocument document = RunProbe("Selection");
        JsonElement commands = document.RootElement.GetProperty("commands");
        Assert.AreEqual(2, commands.GetArrayLength());
        string[] expected =
        [
            "./Both.cs", "./Cancelled.cs", "./File [1].cs", "./Renamed file.cs",
            "./Staged.cs", "./Unstaged.cs", "./新 規.cs"
        ];
        foreach (JsonElement command in commands.EnumerateArray())
        {
            string[] arguments = ReadStrings(command);
            int includeIndex = Array.IndexOf(arguments, "--include");
            Assert.IsTrue(includeIndex >= 0);
            CollectionAssert.AreEquivalent(expected, arguments[(includeIndex + 1)..]);
            CollectionAssert.DoesNotContain(arguments, "--verify-no-changes");
        }
        Assert.AreEqual("style", commands[0][1].GetString());
        Assert.AreEqual("whitespace", commands[1][1].GetString());
    }

    /// <summary>自動整形後の内容をビルドへ渡し、対象ゼロと実行失敗を区別して、失敗後に後続処理へ進まないことを確かめます。</summary>
    /// <remarks>実際の検証関数を呼び、Git一覧と外部コマンドを代替します。通常・フィルター付きQuick・Full・手動の接続、共有期限、検査の二重起動を観測します。</remarks>
    [TestMethod]
    public void FormattingPrecedesBuildAndFailuresStopLaterStages()
    {
        using JsonDocument document = RunProbe("Pipeline");
        Assert.AreEqual(8, document.RootElement.GetArrayLength());
        foreach (JsonElement result in document.RootElement.EnumerateArray())
        {
            string scenario = result.GetProperty("scenario").GetString() ?? throw new AssertFailedException("シナリオ名がありません。");
            string[] expectedEvents = scenario switch
            {
                "Quick" => ["restore", "style", "whitespace", "git-whitespace", "test", "outputs"],
                "Empty" => ["restore", "git-whitespace", "build", "outputs", "tests"],
                "GitFailure" => ["restore"],
                "StyleFailure" => ["restore", "style"],
                "WhitespaceFailure" => ["restore", "style", "whitespace"],
                "Manual" => ["restore", "style", "whitespace"],
                "Functional" or "Full" => ["restore", "style", "whitespace", "git-whitespace", "build", "outputs", "tests"],
                _ => throw new AssertFailedException($"未知のシナリオです: {scenario}")
            };
            CollectionAssert.AreEqual(expectedEvents, ReadStrings(result.GetProperty("events")), scenario);
            string expectedError = scenario switch
            {
                "GitFailure" => "probe-git-failure",
                "StyleFailure" => "probe-style-failure",
                "WhitespaceFailure" => "probe-whitespace-failure",
                _ => string.Empty
            };
            Assert.AreEqual(expectedError, result.GetProperty("error").GetString(), scenario);
            string[] expectedInputs = scenario switch
            {
                "Empty" => ["unformatted"],
                "Functional" or "Quick" or "Full" => ["formatted"],
                _ => []
            };
            CollectionAssert.AreEqual(expectedInputs, ReadStrings(result.GetProperty("buildInputs")), scenario);
            foreach (JsonElement command in result.GetProperty("commands").EnumerateArray())
            {
                string[] arguments = ReadStrings(command);
                if (arguments[0] != "format")
                {
                    continue;
                }
                CollectionAssert.DoesNotContain(arguments, "--verify-no-changes", scenario);
                Assert.AreEqual(scenario != "Full", arguments.Contains("--include"), scenario);
            }
        }
    }

    private static string[] ReadStrings(JsonElement array) =>
        array.EnumerateArray().Select(value => value.GetString() ?? throw new AssertFailedException("文字列がありません。")).ToArray();

    private static JsonDocument RunProbe(string probe)
    {
        string repositoryRoot = PowerShellTestProcess.FindRepositoryRoot();
        PowerShellTestResult result = PowerShellTestProcess.Run(
            repositoryRoot,
            "& $env:BMS_FORMAT_PROBE -Probe $env:BMS_FORMAT_SCENARIO",
            "Repository formatting",
            new Dictionary<string, string>
            {
                ["BMS_FORMAT_PROBE"] = Path.Combine(repositoryRoot, "scripts", "test-fixtures", "repository-format-probe.ps1"),
                ["BMS_FORMAT_SCENARIO"] = probe
            });
        Assert.IsTrue(string.IsNullOrWhiteSpace(result.Error), $"整形プローブの標準エラー: {result.Error}");
        return JsonDocument.Parse(result.Output.Trim());
    }
}
