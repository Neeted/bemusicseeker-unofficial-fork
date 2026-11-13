using BeMusicSeeker.Models;
using BeMusicSeeker.TestSupport.Files;
using BeMusicSeeker.ViewModels;

namespace BeMusicSeeker.Tests;

/// <summary>
/// 譜面ビューアの項目の共通判定を、本番の判定関数へ候補を渡して確認します。
/// ファイルの有無は <see cref="TestWorkspace"/> の実ファイルで作り、判定を再実装しません。
/// </summary>
[TestClass]
public sealed class ScoreViewerMenuDecisionTests
{
    private static readonly string Md5A = new('a', 32);
    private static readonly string Md5B = new('b', 32);
    private static readonly string Md5C = new('c', 32);

    public enum FileState
    {
        Exists,
        Missing,
        NoPath,
    }

    public enum MultipleScenario
    {
        TwoOwned,
        OwnedAndFileless,
        OwnedAndBmson,
        OwnedAndEmptyMd5,
        OwnedAndInvalidMd5,
        OwnedAndUnresolvedRow,
        OwnedDuplicateMd5,
        AllFileless,
        BmsonOnlyWithFile,
        BmsWithFileButEmptyMd5,
        BmsWithFileButInvalidMd5,
    }

    /// <summary>
    /// 1行だけを選んだとき、種類がBMSで有効なMD5（32桁の16進）を持てば、ファイルが実在しない・パスがない場合も含めて「開く」になり、
    /// BMSON・MD5が空・MD5が不正（31桁・16進でない文字・前後の空白）・種類を確定できない行は非表示になることを確認します。
    /// 所持を要件にして未所持を隠す誤り、MD5を空白判定だけで受理して不正なMD5を対象にする誤り、BMSONを対象にする誤りを検出します。
    /// </summary>
    [TestMethod]
    [DataRow(false, "valid", FileState.Exists, true, DisplayName = "BMSで有効なMD5でファイルあり")]
    [DataRow(false, "valid", FileState.Missing, true, DisplayName = "BMSで有効なMD5でファイルなし")]
    [DataRow(false, "valid", FileState.NoPath, true, DisplayName = "BMSで有効なMD5でパスなし")]
    [DataRow(true, "valid", FileState.Exists, false, DisplayName = "BMSONはファイルがあっても非表示")]
    [DataRow(false, "", FileState.Exists, false, DisplayName = "MD5が空は非表示")]
    [DataRow(false, null, FileState.Missing, false, DisplayName = "MD5がnullは非表示")]
    [DataRow(false, "   ", FileState.Missing, false, DisplayName = "MD5が空白だけは非表示")]
    [DataRow(false, "short", FileState.Exists, false, DisplayName = "MD5が短いと非表示")]
    [DataRow(false, "not-hex", FileState.Exists, false, DisplayName = "MD5が16進でないと非表示")]
    [DataRow(false, "padded", FileState.Exists, false, DisplayName = "MD5の前後に空白があると非表示")]
    public void Resolve_SingleCandidate_OpensBmsWithValidMd5Otherwise_Hidden(
        bool isBmson,
        string? md5Kind,
        FileState fileState,
        bool expectedOpen)
    {
        using var workspace = TestWorkspace.Create(TestRunDirectory.RootDirectory);
        string? md5 = md5Kind switch
        {
            "valid" => Md5A,
            "short" => new string('a', 31),
            "not-hex" => new string('g', 32),
            "padded" => " " + Md5A,
            _ => md5Kind,
        };
        ScoreViewerCandidate candidate = CreateCandidate(
            workspace,
            isBmson ? ChartFileKind.Bmson : ChartFileKind.Bms,
            md5,
            fileState,
            "single");

        var decision = ScoreViewerMenuDecision.Resolve([candidate]);

        Assert.AreEqual(expectedOpen ? ScoreViewerMenuMode.Open : ScoreViewerMenuMode.Hidden, decision.Mode);
        Assert.AreEqual(decision.Mode, ScoreViewerMenuDecision.ResolveMode([candidate]), "表示時の判定とクリック時の判定が食い違わない。");
        if (expectedOpen)
        {
            ScoreViewerTarget target = decision.Targets.Single();
            Assert.AreEqual(Md5A, target.Hash);
            Assert.AreEqual(candidate.Path, target.Path);
            Assert.AreEqual("single", target.Title);
        }
        else
        {
            Assert.AreEqual(0, decision.Targets.Count);
        }
    }

    /// <summary>
    /// 種類を確定できない1行（譜面操作の対象へ解決できない行や、種類を確定できない履歴行）は、MD5を持っていても非表示になることを確認します。
    /// 種類不明をBMS扱いして開こうとする誤りを検出します。
    /// </summary>
    [TestMethod]
    public void Resolve_SingleCandidateWithUnknownKind_IsHiddenEvenWithMd5()
    {
        var candidate = new ScoreViewerCandidate(Kind: null, Md5A, Path: null, Title: "unknown kind");

        Assert.AreEqual(ScoreViewerMenuMode.Hidden, ScoreViewerMenuDecision.Resolve([candidate]).Mode);
        Assert.AreEqual(ScoreViewerMenuMode.Hidden, ScoreViewerMenuDecision.Resolve([ScoreViewerCandidate.NotTarget]).Mode);
        Assert.AreEqual(ScoreViewerMenuMode.Hidden, ScoreViewerMenuDecision.Resolve([]).Mode, "候補がなければ非表示。");
    }

    /// <summary>
    /// 2行以上を選んだとき、ファイルが実在する所持BMSを1件以上含めば、BMSON・MD5なし・MD5不正・ファイルなし・解決できない行が混ざっても
    /// 「登録」になり、登録対象は所持BMSだけ（同じMD5の重複は畳まない）になることを確認します。
    /// 所持BMSを含まない選択（全件ファイルなし、BMSONだけ、ファイルがあってもMD5が空・不正）は非表示になることも確認します。
    /// 件数だけで登録を決める誤り、ファイルのない対象・BMSONを登録対象へ含める誤り、所持BMSがないのに項目を出す誤りを検出します。
    /// </summary>
    [TestMethod]
    [DataRow(MultipleScenario.TwoOwned, true, 2, DisplayName = "所持BMS2件は登録")]
    [DataRow(MultipleScenario.OwnedAndFileless, true, 1, DisplayName = "所持BMSとファイルなしの混在は所持だけを登録")]
    [DataRow(MultipleScenario.OwnedAndBmson, true, 1, DisplayName = "所持BMSとBMSONの混在は所持BMSだけを登録")]
    [DataRow(MultipleScenario.OwnedAndEmptyMd5, true, 1, DisplayName = "所持BMSとMD5なしの混在は所持BMSだけを登録")]
    [DataRow(MultipleScenario.OwnedAndInvalidMd5, true, 1, DisplayName = "所持BMSとMD5不正の混在は所持BMSだけを登録")]
    [DataRow(MultipleScenario.OwnedAndUnresolvedRow, true, 1, DisplayName = "所持BMSと解決できない行の混在は所持BMSだけを登録")]
    [DataRow(MultipleScenario.OwnedDuplicateMd5, true, 2, DisplayName = "同じMD5の所持BMS2件は畳まず2件")]
    [DataRow(MultipleScenario.AllFileless, false, 0, DisplayName = "全件ファイルなしは非表示")]
    [DataRow(MultipleScenario.BmsonOnlyWithFile, false, 0, DisplayName = "BMSONだけはファイルがあっても非表示")]
    [DataRow(MultipleScenario.BmsWithFileButEmptyMd5, false, 0, DisplayName = "ファイルがあってもMD5が空は非表示")]
    [DataRow(MultipleScenario.BmsWithFileButInvalidMd5, false, 0, DisplayName = "ファイルがあってもMD5が不正は非表示")]
    public void Resolve_MultipleCandidates_RegistersOwnedBmsOnly(
        MultipleScenario scenario,
        bool expectedRegister,
        int expectedTargetCount)
    {
        using var workspace = TestWorkspace.Create(TestRunDirectory.RootDirectory);
        ScoreViewerCandidate owned1 = CreateCandidate(workspace, ChartFileKind.Bms, Md5A, FileState.Exists, "owned1");
        ScoreViewerCandidate owned2 = CreateCandidate(workspace, ChartFileKind.Bms, Md5B, FileState.Exists, "owned2");
        ScoreViewerCandidate fileless = CreateCandidate(workspace, ChartFileKind.Bms, Md5C, FileState.Missing, "fileless");
        ScoreViewerCandidate[] candidates = scenario switch
        {
            MultipleScenario.TwoOwned => [owned1, owned2],
            MultipleScenario.OwnedAndFileless => [fileless, owned1],
            MultipleScenario.OwnedAndBmson =>
                [CreateCandidate(workspace, ChartFileKind.Bmson, Md5B, FileState.Exists, "bmson"), owned1],
            MultipleScenario.OwnedAndEmptyMd5 =>
                [CreateCandidate(workspace, ChartFileKind.Bms, string.Empty, FileState.Exists, "empty"), owned1],
            MultipleScenario.OwnedAndInvalidMd5 =>
                [CreateCandidate(workspace, ChartFileKind.Bms, "invalid", FileState.Exists, "invalid"), owned1],
            MultipleScenario.OwnedAndUnresolvedRow => [ScoreViewerCandidate.NotTarget, owned1],
            MultipleScenario.OwnedDuplicateMd5 =>
                [owned1, CreateCandidate(workspace, ChartFileKind.Bms, Md5A, FileState.Exists, "duplicate")],
            MultipleScenario.AllFileless =>
                [fileless, CreateCandidate(workspace, ChartFileKind.Bms, Md5B, FileState.NoPath, "no path")],
            MultipleScenario.BmsonOnlyWithFile =>
                [CreateCandidate(workspace, ChartFileKind.Bmson, Md5A, FileState.Exists, "a"),
                 CreateCandidate(workspace, ChartFileKind.Bmson, Md5B, FileState.Exists, "b")],
            MultipleScenario.BmsWithFileButEmptyMd5 =>
                [CreateCandidate(workspace, ChartFileKind.Bms, string.Empty, FileState.Exists, "a"),
                 CreateCandidate(workspace, ChartFileKind.Bms, null, FileState.Exists, "b")],
            _ =>
                [CreateCandidate(workspace, ChartFileKind.Bms, "invalid", FileState.Exists, "a"),
                 CreateCandidate(workspace, ChartFileKind.Bms, new string('z', 32), FileState.Exists, "b")],
        };

        var decision = ScoreViewerMenuDecision.Resolve(candidates);

        Assert.AreEqual(
            expectedRegister ? ScoreViewerMenuMode.Register : ScoreViewerMenuMode.Hidden,
            decision.Mode);
        Assert.AreEqual(decision.Mode, ScoreViewerMenuDecision.ResolveMode(candidates), "表示時の判定とクリック時の判定が食い違わない。");
        Assert.AreEqual(expectedTargetCount, decision.Targets.Count);
        string[] expectedPaths = [.. candidates.Where(candidate => candidate.IsOwnedTarget).Select(candidate => candidate.Path)];
        CollectionAssert.AreEqual(
            expectedRegister ? expectedPaths : Array.Empty<string>(),
            decision.Targets.Select(target => target.Path).ToArray(),
            "登録対象は選択順の所持BMSだけ。");
        Assert.IsTrue(decision.Targets.All(target => File.Exists(target.Path)), "ファイルのない対象を登録対象へ含めない。");
    }

    /// <summary>
    /// 判定の後にファイルの有無や選択が変わったとき、古い判定を使い回さず、その時点の候補から判定し直すことを確認します。
    /// 2件所持から1件が消えると所持1件の登録、両方が消えると非表示、選択が未所持の1件へ変わると開く、になります。
    /// 表示時の判定結果をクリック時へ使い回す誤りを検出します。
    /// </summary>
    [TestMethod]
    public void Resolve_AfterFileOrSelectionChanges_UsesTheCurrentCandidates()
    {
        using var workspace = TestWorkspace.Create(TestRunDirectory.RootDirectory);
        ScoreViewerCandidate first = CreateCandidate(workspace, ChartFileKind.Bms, Md5A, FileState.Exists, "first");
        ScoreViewerCandidate second = CreateCandidate(workspace, ChartFileKind.Bms, Md5B, FileState.Exists, "second");
        ScoreViewerCandidate[] selection = [first, second];

        var shown = ScoreViewerMenuDecision.Resolve(selection);
        Assert.AreEqual(ScoreViewerMenuMode.Register, shown.Mode);
        Assert.AreEqual(2, shown.Targets.Count);

        File.Delete(first.Path);
        var afterOneDeleted = ScoreViewerMenuDecision.Resolve(selection);
        Assert.AreEqual(ScoreViewerMenuMode.Register, afterOneDeleted.Mode);
        CollectionAssert.AreEqual(new[] { second.Path }, afterOneDeleted.Targets.Select(target => target.Path).ToArray());

        File.Delete(second.Path);
        Assert.AreEqual(ScoreViewerMenuMode.Hidden, ScoreViewerMenuDecision.Resolve(selection).Mode);

        var afterNarrowed = ScoreViewerMenuDecision.Resolve([first]);
        Assert.AreEqual(ScoreViewerMenuMode.Open, afterNarrowed.Mode);
        Assert.AreEqual(Md5A, afterNarrowed.Targets.Single().Hash);
    }

    private static ScoreViewerCandidate CreateCandidate(
        TestWorkspace workspace,
        ChartFileKind kind,
        string? md5,
        FileState fileState,
        string title)
    {
        string? path = null;
        if (fileState != FileState.NoPath)
        {
            path = workspace.GetPath(Guid.NewGuid().ToString("N") + (kind == ChartFileKind.Bms ? ".bms" : ".bmson"));
            if (fileState == FileState.Exists)
            {
                File.WriteAllText(path, "#TITLE test");
            }
        }
        return new ScoreViewerCandidate(kind, md5!, path!, title);
    }
}
