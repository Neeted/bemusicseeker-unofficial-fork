using BeMusicSeeker.Models.Utils;
using BeMusicSeeker.TestSupport.Components;
using BeMusicSeeker.TestSupport.Files;

namespace BeMusicSeeker.Tests;

/// <remarks>
/// リンク切れの判定は、リンク自身の属性と配下の列挙の失敗というOSの実際の応答に依存するため、実ジャンクションで確認します。
/// リンクではない対象の不存在が不完全のままであることは、日常検証の <c>RootFileEnumerationTests</c> が分担します。
/// </remarks>
[TestClass]
[TestCategory("Suite.Junction")]
public sealed class FastRootFileEnumeratorBrokenLinkTests
{
    private const string ChartGroupName = ChartDirectoryScanBuilder.ChartGroupName;
    private const string DirectoryGroupName = RootFileEnumerationService.DirectoriesGroupName;

    /// <summary>
    /// 保証：登録ルートより下にあるリンク切れのジャンクションは、警告を残してリンク自身と配下だけを結果から除き、
    /// 走査を権威ある完了のまま返す。通常のディレクトリと、正常なジャンクションの先の譜面は結果に残る。
    /// リンク切れ1件で走査全体を不完全にする誤り、再解析ポイントを一律に除外する誤り、
    /// リンク自身をディレクトリの結果へ残す誤り、警告を残さない誤りを検出する。
    /// </summary>
    /// <remarks>共有のログ構成を一時的に差し替えるため、並列実行しない。ログの文言と理由の書式は固定しない。</remarks>
    [DoNotParallelize]
    [TestMethod]
    [TestCategory("ProcessIntegration")]
    [TestCategory("EnvironmentIntegration")]
    public async Task EnumerateFiles_BrokenJunctionBelowRootIsSkippedWithWarningAndResultStaysComplete()
    {
        await ApplicationLoggingTestScope.RunAsync(loggingScope =>
        {
            string logPath = loggingScope.ConfigureFileLog();
            using var workspace = TestWorkspace.Create(TestRunDirectory.RootDirectory);
            string root = Path.Combine(workspace.RootDirectory, "root");
            string pack = Path.Combine(root, "Pack");
            string chartPath = Path.Combine(pack, "chart.bms");
            string brokenLink = Path.Combine(pack, "Broken");
            string brokenTarget = Path.Combine(workspace.RootDirectory, "broken-target");
            string validLink = Path.Combine(root, "Valid");
            string validTarget = Path.Combine(workspace.RootDirectory, "valid-target");
            string linkedChartPath = Path.Combine(validLink, "v.bms");
            Directory.CreateDirectory(pack);
            Directory.CreateDirectory(brokenTarget);
            Directory.CreateDirectory(validTarget);
            File.WriteAllText(chartPath, "#PLAYER 1");
            File.WriteAllText(Path.Combine(brokenTarget, "lost.bms"), "#PLAYER 1");
            File.WriteAllText(Path.Combine(validTarget, "v.bms"), "#PLAYER 1");

            DirectoryJunctionFixture.Run(workspace, validLink, validTarget, () =>
                DirectoryJunctionFixture.Run(workspace, brokenLink, brokenTarget, () =>
                {
                    Directory.Delete(brokenTarget, recursive: true);
                    Assert.IsTrue((File.GetAttributes(brokenLink) & FileAttributes.ReparsePoint) != 0, "リンク自身は残り、リンク先だけが無い状態にします。");

                    RootFileEnumerationResult result = Enumerate(root);

                    Assert.IsTrue(RootFileEnumerationService.IsAuthoritativeComplete(result), result.IncompleteReason);
                    List<string> charts = [.. result.GetPaths(ChartGroupName)];
                    List<string> directories = [.. result.GetPaths(DirectoryGroupName)];
                    CollectionAssert.AreEquivalent(new[] { chartPath, linkedChartPath }, charts);
                    CollectionAssert.AreEquivalent(new[] { root, pack, validLink }, directories);
                    Assert.IsFalse(charts.Concat(directories).Any(path => path.StartsWith(brokenLink, StringComparison.OrdinalIgnoreCase)));
                    string[] warnings = [.. ApplicationLoggingTestScope.ReadFileLog(logPath)
                        .Split('\n')
                        .Where(line => line.Contains("[WARN]") && line.Contains(brokenLink, StringComparison.OrdinalIgnoreCase))];
                    Assert.IsTrue(warnings.Length >= 1, "飛ばしたリンクのパスを含む警告が残ります。");
                }));
            return Task.CompletedTask;
        });
    }

    /// <summary>
    /// 保証：登録ルート自身がリンク切れのジャンクションである場合は飛ばさず、ルートの不在として成功や権威ある完了にしない。
    /// 登録ルート全体を空の完全な結果へ読み替え、配下の登録を削除差分にする誤りを検出する。
    /// </summary>
    /// <remarks>
    /// 条件差：単独の登録ルートと、別の登録ルートの配下に入れ子で登録されたルート。
    /// 後者は、祖先のルートへ縮約されたルートを配下のリンク切れとして飛ばす誤りを検出する。
    /// </remarks>
    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    [TestCategory("ProcessIntegration")]
    [TestCategory("EnvironmentIntegration")]
    public void EnumerateFiles_BrokenJunctionRootRemainsIncomplete(bool nestedUnderAnotherRoot)
    {
        using var workspace = TestWorkspace.Create(TestRunDirectory.RootDirectory);
        string outerRoot = Path.Combine(workspace.RootDirectory, "outer-root");
        string root = Path.Combine(outerRoot, "root-link");
        string target = Path.Combine(workspace.RootDirectory, "root-target");
        Directory.CreateDirectory(outerRoot);
        Directory.CreateDirectory(target);
        File.WriteAllText(Path.Combine(outerRoot, "outer.bms"), "#PLAYER 1");
        File.WriteAllText(Path.Combine(target, "chart.bms"), "#PLAYER 1");

        DirectoryJunctionFixture.Run(workspace, root, target, () =>
        {
            Directory.Delete(target, recursive: true);
            Assert.IsTrue((File.GetAttributes(root) & FileAttributes.ReparsePoint) != 0, "リンク自身は残り、リンク先だけが無い状態にします。");

            RootFileEnumerationResult result = Enumerate(nestedUnderAnotherRoot ? [outerRoot, root] : [root]);

            Assert.IsFalse(result.Success);
            Assert.IsFalse(RootFileEnumerationService.IsAuthoritativeComplete(result));
            Assert.IsFalse(result.ScanLimitExceeded);
            StringAssert.Contains(RootFileEnumerationService.GetNonAuthoritativeReason(result), root);
        });
    }

    private static RootFileEnumerationResult Enumerate(params string[] roots)
        => new FastRootFileEnumerator().EnumerateFiles(
            roots,
            [
                new RootFileEnumerationGroup(ChartGroupName, [".bms"]),
                new RootFileEnumerationGroup(DirectoryGroupName, [], includeDirectories: true)
            ]);
}
