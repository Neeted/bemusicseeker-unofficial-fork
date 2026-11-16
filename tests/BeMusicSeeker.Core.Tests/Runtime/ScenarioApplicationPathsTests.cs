using BeMusicSeeker.TestSupport.Files;
using BeMusicSeeker.TestSupport.Scenarios;

namespace BeMusicSeeker.Core.Tests.Runtime;

/// <summary>通常起動の資産選択だけを検査し、候補の実体検査と実起動はrunnerと既存シナリオへ分担します。</summary>
[TestClass]
public sealed class ScenarioApplicationPathsTests
{
    /// <summary>候補が明示された場合はその展開先を使い、両方未指定の日常実行だけ同じbuildのfixtureを使います。</summary>
    [TestMethod]
    public void CandidateAndDailyUseTheirOwnInstallation()
    {
        using var workspace = TestWorkspace.Create(TestRunDirectory.RootDirectory);
        string candidate = workspace.GetPath("candidate");
        string build = workspace.GetPath("build");
        Assert.AreEqual(candidate, ScenarioApplicationPaths.ResolveRoot(workspace.GetPath("catalog.json"), candidate, build));
        Assert.AreEqual(build, ScenarioApplicationPaths.ResolveRoot(null, null, build));
    }

    /// <summary>片方だけ・相対パス・空の候補指定は失敗とし、手元のbuildを検証した結果へ置き換えません。</summary>
    [TestMethod]
    [DataRow("catalog-only")]
    [DataRow("root-only")]
    [DataRow("relative-catalog")]
    [DataRow("relative-root")]
    [DataRow("empty-root")]
    public void IncompleteCandidateDoesNotFallBackToBuild(string input)
    {
        using var workspace = TestWorkspace.Create(TestRunDirectory.RootDirectory);
        string? catalog = input == "root-only" ? null : input == "relative-catalog" ? "catalog.json" : workspace.GetPath("catalog.json");
        string? root = input == "catalog-only" ? null : input == "relative-root" ? "candidate" : input == "empty-root" ? "" : workspace.GetPath("candidate");
        Assert.ThrowsExactly<InvalidOperationException>(() => ScenarioApplicationPaths.ResolveRoot(catalog, root, workspace.GetPath("build")));
    }
}
