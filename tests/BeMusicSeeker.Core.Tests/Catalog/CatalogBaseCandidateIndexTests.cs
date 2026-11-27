using BeMusicSeeker.Models;
using BeMusicSeeker.Models.BmsLibraryInternal;
using BeMusicSeeker.TestSupport.Components;

namespace BeMusicSeeker.Tests;

/// <summary>プレイリスト未所持行の本体候補を引く、所持譜面の派生索引の照合規則・差分反映・構築の契約を確認します。</summary>
[TestClass]
public sealed class CatalogBaseCandidateIndexTests
{
    private static int sequence;

    /// <summary>
    /// 題名キーの完全一致と、作者キーの一致・項目側の空・類似度0.50以上の条件、直接の親フォルダへの集約、件数降順・同数はパスの昇順・先頭5件を、
    /// 原題・元作者の射影と正規化を含めて確認する。表示用の値だけが一致する譜面や題名が少し違う譜面は候補にしない。
    /// </summary>
    [TestMethod]
    public void FullBuild_AppliesTitleAndArtistMatchingRules()
    {
        CatalogOwnedCollectionOwner owner = Prepared(StandardBms(), StandardBmson());

        // 同じ作者・近い作者・正規化後に同じ作者のフォルダだけが、一致譜面数の降順・同数はパスの昇順で先頭5件に並ぶ。
        CollectionAssert.AreEqual(
            new[] { @"C:\Lib\a|2", @"C:\Lib\a2|1", @"C:\Lib\B|1", @"C:\Lib\C|1", @"C:\Lib\J\sub|1" },
            Candidates(owner, "Song", "abc"));
        // 作者が空の項目は譜面の作者によらず一致し、題名の副題は両側で取り除かれる。原題が空・題名違いは対象外。
        CollectionAssert.AreEqual(
            new[] { @"C:\Lib\a|2", @"C:\Lib\a2|1", @"C:\Lib\B|1", @"C:\Lib\C|1", @"C:\Lib\D|1" },
            Candidates(owner, "Song [SPA]", string.Empty));
        // 題名が空白の項目は照合しない。
        Assert.AreEqual(0, Candidates(owner, "  ", "abc").Length);
        Assert.AreEqual(0, Candidates(owner, "Nothing", "abc").Length);

        // 題名キーが少し違うだけの譜面は、作者が一致しても候補にしない。
        Assert.AreEqual(0, Candidates(Prepared([Bms(@"C:\L\s", "Song", "abc")]), "Songs", "abc").Length);
        // 作者の完全一致と、大文字小文字・クレジット表記の違いは同じ作者として扱う。
        CollectionAssert.AreEqual(new[] { @"C:\L\s|1" }, Candidates(Prepared([Bms(@"C:\L\s", "Song", "abc")]), "Song", "abc"));
        CollectionAssert.AreEqual(new[] { @"C:\L\s|1" }, Candidates(Prepared([Bms(@"C:\L\s", "Song [Another]", "ABC")]), "Song", "abc"));
        CollectionAssert.AreEqual(new[] { @"C:\L\s|1" }, Candidates(Prepared([Bms(@"C:\L\s", "Song", "abc")]), "Song [SPA]", "abc feat. Guest Illust&obj:Diff"));
        // 項目側の作者が空なら、譜面の作者が空でも他の作者でも候補になる。
        CollectionAssert.AreEqual(new[] { @"C:\L\e|1", @"C:\L\z|1" },
            Candidates(Prepared([Bms(@"C:\L\e", "Song", string.Empty), Bms(@"C:\L\z", "Song", "zzz")]), "Song", string.Empty));
        // 作者の文字2-gramの類似度がちょうど0.50なら候補にし、0.40では候補にしない。
        CollectionAssert.AreEqual(new[] { @"C:\L\s|1" }, Candidates(Prepared([Bms(@"C:\L\s", "Song", "abd")]), "Song", "abc"));
        Assert.AreEqual(0, Candidates(Prepared([Bms(@"C:\L\s", "Song", "abde")]), "Song", "abc").Length);
        // 項目側の作者が非空で、譜面の作者が空なら候補にしない。
        Assert.AreEqual(0, Candidates(Prepared([Bms(@"C:\L\s", "Song", string.Empty)]), "Song", "abc").Length);
        // BMSは原題・元作者だけを使い、表示用の題名・作者だけが一致しても使わない。
        Assert.AreEqual(0, Candidates(Prepared([Bms(@"C:\L\s", string.Empty, "abc", title: "Song")]), "Song", "abc").Length);
        Assert.AreEqual(0, Candidates(Prepared([Bms(@"C:\L\s", "Song", "xyz", artist: "abc")]), "Song", "abc").Length);
        // bmsonは原題と現在の作者を使い、元作者だけが一致しても使わない。
        CollectionAssert.AreEqual(new[] { @"C:\L\m|1" }, Candidates(Prepared([], [Bmson(@"C:\L\m", "Song", "abc feat. Guest", "unrelated")]), "Song", "abc"));
        Assert.AreEqual(0, Candidates(Prepared([], [Bmson(@"C:\L\m", "Song", "xyz", "abc")]), "Song", "abc").Length);
        // 候補は一致した譜面の直接の親フォルダで、同じフォルダの譜面は一件にまとめる。
        CollectionAssert.AreEqual(new[] { @"C:\L\x\y|2" },
            Candidates(Prepared([Bms(@"C:\L\x\y", "Song", "abc"), Bms(@"C:\L\x\y", "Song [Another]", "abc")]), "Song", "abc"));
    }

    /// <summary>
    /// 追加・削除・移動・原題の変更・作者の変更（BMSは元の作者、bmsonは現在の作者）・大文字小文字だけのフォルダ名の変更の確定の後に、
    /// 索引が変更後の所持譜面から新しい所有者で全構築した内容と一致し、期待する候補とも一致する。
    /// 最後の一致譜面が消えた入れ物は残らない。
    /// </summary>
    [TestMethod]
    [DataRow("add")]
    [DataRow("remove_folder_member")]
    [DataRow("remove_last_of_artist")]
    [DataRow("remove_last_of_title")]
    [DataRow("move_into_folder")]
    [DataRow("retitle")]
    [DataRow("rename_artist_bms")]
    [DataRow("rename_artist_bmson")]
    [DataRow("folder_case_rename")]
    public void LocalCommits_MatchFullBuildAfterEachDependentChange(string change)
    {
        ChartFile a2 = Bms(@"C:\Lib\a2", "Song", "abc");
        ChartFile e = Bms(@"C:\Lib\E", "Song", string.Empty);
        ChartFile unique = Bms(@"C:\Lib\Z", "Unique", "u");
        ChartFile bmsonC = Bmson(@"C:\Lib\C", "Song", "abc feat. Guest", "unrelated");
        ChartFile b = Bms(@"C:\Lib\B", "Song", "ABC");
        ChartFile[] bms = [.. StandardBms().Where(chart => chart.Path.Contains(@"\a2\") == false && chart.Path.Contains(@"\B\") == false && chart.Path.Contains(@"\E\") == false && chart.Path.Contains(@"\Z\") == false), a2, b, e, unique];
        ChartFile[] bmson = [bmsonC, .. StandardBmson().Where(chart => chart.Path.Contains(@"\C\") == false)];
        CatalogOwnedCollectionOwner owner = Prepared(bms, bmson);
        (int Titles, int Artists, int Folders) before = owner.BaseCandidateIndex.Shape;
        string[] expected;
        switch (change)
        {
            case "add":
                owner.ApplyCommittedChartUpsert([Bms(@"C:\Lib\B", "Song", "abc")], out _);
                expected = [@"C:\Lib\a|2", @"C:\Lib\B|2", @"C:\Lib\a2|1", @"C:\Lib\C|1", @"C:\Lib\J\sub|1"];
                break;
            case "remove_folder_member":
                owner.ApplyMutation([OwnedChartRemoveRequest.FromChart(Current(owner, a2))], null);
                expected = [@"C:\Lib\a|2", @"C:\Lib\B|1", @"C:\Lib\C|1", @"C:\Lib\J\sub|1", @"C:\Lib\K|1"];
                break;
            case "remove_last_of_artist":
                owner.ApplyMutation([OwnedChartRemoveRequest.FromChart(Current(owner, e))], null);
                expected = [@"C:\Lib\a|2", @"C:\Lib\a2|1", @"C:\Lib\B|1", @"C:\Lib\C|1", @"C:\Lib\J\sub|1"];
                Assert.AreEqual(before.Artists - 1, owner.BaseCandidateIndex.Shape.Artists);
                break;
            case "remove_last_of_title":
                owner.ApplyMutation([OwnedChartRemoveRequest.FromChart(Current(owner, unique))], null);
                expected = [@"C:\Lib\a|2", @"C:\Lib\a2|1", @"C:\Lib\B|1", @"C:\Lib\C|1", @"C:\Lib\J\sub|1"];
                Assert.AreEqual(before.Titles - 1, owner.BaseCandidateIndex.Shape.Titles);
                break;
            case "move_into_folder":
                owner.ApplyMutation(null, [new LibraryChartPathChange { Chart = Current(owner, a2), OldPath = a2.Path, NewPath = @"C:\Lib\B\moved.bms" }]);
                expected = [@"C:\Lib\a|2", @"C:\Lib\B|2", @"C:\Lib\C|1", @"C:\Lib\J\sub|1", @"C:\Lib\K|1"];
                break;
            case "retitle":
                owner.CommitCurrentValues([Current(owner, a2)], (current, _) => current with { RawTitle = "Other" });
                expected = [@"C:\Lib\a|2", @"C:\Lib\B|1", @"C:\Lib\C|1", @"C:\Lib\J\sub|1", @"C:\Lib\K|1"];
                break;
            case "rename_artist_bms":
                owner.CommitCurrentValues([Current(owner, a2)], (current, _) => current with { RawArtist = "qqq" });
                expected = [@"C:\Lib\a|2", @"C:\Lib\B|1", @"C:\Lib\C|1", @"C:\Lib\J\sub|1", @"C:\Lib\K|1"];
                break;
            case "rename_artist_bmson":
                owner.CommitCurrentValues([Current(owner, bmsonC)], (current, _) => current with { Artist = "qqq" });
                expected = [@"C:\Lib\a|2", @"C:\Lib\a2|1", @"C:\Lib\B|1", @"C:\Lib\J\sub|1", @"C:\Lib\K|1"];
                break;
            case "folder_case_rename":
                owner.ApplyMutation(null, [new LibraryChartPathChange { Chart = Current(owner, b), OldPath = b.Path, NewPath = @"C:\Lib\b\" + Path.GetFileName(b.Path) }]);
                expected = [@"C:\Lib\a|2", @"C:\Lib\a2|1", @"C:\Lib\b|1", @"C:\Lib\C|1", @"C:\Lib\J\sub|1"];
                break;
            default:
                throw new AssertFailedException(change);
        }

        CollectionAssert.AreEqual(expected, Candidates(owner, "Song", "abc"));
        AssertSameAsFullBuild(owner);
    }

    /// <summary>
    /// 原題・作者・パスに関わらない変更（BMSの表示用の題名・作者、bmsonの元作者、ハッシュ）では、索引の内容も参照も変わらない。
    /// </summary>
    [TestMethod]
    public void UnrelatedChanges_KeepContents()
    {
        ChartFile bmsChart = Bms(@"C:\Lib\a2", "Song", "abc");
        ChartFile bmsonChart = Bmson(@"C:\Lib\C", "Song", "abc", "unrelated");
        CatalogOwnedCollectionOwner owner = Prepared([bmsChart, Bms(@"C:\Lib\a", "Song", "abc")], [bmsonChart]);
        CatalogBaseCandidateIndex index = owner.BaseCandidateIndex;
        var contents = index.Enumerate().ToArray();

        owner.CommitCurrentValues([Current(owner, bmsChart)], (current, _) => current with { Title = "Display title", Artist = "Display artist" });
        owner.CommitCurrentValues([Current(owner, bmsonChart)], (current, _) => current with { RawArtist = "other raw artist" });
        owner.ApplyDigestChanges([new LibraryChartDigestChange(ChartFileKind.Bms, bmsChart.Path, bmsChart.Md5, bmsChart.Sha256, new string('f', 32), new string('e', 64))]);

        Assert.AreSame(index, owner.BaseCandidateIndex);
        CollectionAssert.AreEqual(contents, owner.BaseCandidateIndex.Enumerate().ToArray());
    }

    /// <summary>
    /// 全置換では索引を破棄して候補は空になり（例外にならない）、未構築の間の局所確定は索引を構築せず、次の準備で現在の所持譜面から再構築する。
    /// </summary>
    [TestMethod]
    public void FullReplacement_DiscardsAndNextPrepareRebuilds()
    {
        var work = new List<string>();
        CatalogOwnedCollectionOwner owner = Prepared(StandardBms(), StandardBmson());
        owner.BaseCandidateIndexWorkObserver = work.Add;
        Assert.AreEqual(5, Candidates(owner, "Song", "abc").Length);

        owner.ReplaceCharts([Bms(@"C:\Lib\Only", "Song", "abc")], []);

        Assert.IsNull(owner.BaseCandidateIndex);
        Assert.AreEqual(0, Candidates(owner, "Song", "abc").Length);
        // 未構築の間の局所確定では構築しない。
        owner.ApplyCommittedChartUpsert([Bms(@"C:\Lib\Later", "Song", "abc")], out _);
        Assert.IsNull(owner.BaseCandidateIndex);
        Assert.IsFalse(work.Contains("base_candidate_install_pending"));
        Assert.AreEqual(BaseCandidateIndexPrepareResult.Installed, owner.PrepareBaseCandidateIndex(CancellationToken.None));
        CollectionAssert.AreEqual(new[] { @"C:\Lib\Later|1", @"C:\Lib\Only|1" }, Candidates(owner, "Song", "abc"));
        Assert.AreEqual(BaseCandidateIndexPrepareResult.Cached, owner.PrepareBaseCandidateIndex(CancellationToken.None));
    }

    /// <summary>
    /// 構築を所持譜面の取込みの後・設置の前で止め、その間に局所確定（追加と削除）を行っても、設置される内容は設置時点の所持譜面からの全構築と一致する。
    /// </summary>
    [TestMethod]
    public async Task CommitDuringBuild_InstalledContentMatchesCurrentFullBuild()
    {
        ChartFile removed = Bms(@"C:\Lib\a2", "Song", "abc");
        CatalogOwnedCollectionOwner owner = new();
        owner.ReplaceCharts([.. StandardBms().Where(chart => !chart.Path.Contains(@"\a2\")), removed], StandardBmson());
        using var reached = new ManualResetEventSlim();
        using var release = new ManualResetEventSlim();
        owner.BaseCandidateIndexWorkObserver = work =>
        {
            if (work == "base_candidate_install_pending") { reached.Set(); release.Wait(); }
        };
        Task<BaseCandidateIndexPrepareResult> build = ConcurrencyProbe.StartDedicated(() => owner.PrepareBaseCandidateIndex(CancellationToken.None));
        try
        {
            ConcurrencyProbe.WaitForSignal(reached, build, "build reached install");
            Task commits = ConcurrencyProbe.StartDedicated(() =>
            {
                owner.ApplyCommittedChartUpsert([Bms(@"C:\Lib\B", "Song", "abc")], out _);
                owner.ApplyMutation([OwnedChartRemoveRequest.FromChart(Current(owner, removed))], null);
            });
            ConcurrencyProbe.AssertCompletesWhileHeld(commits, "commits during build", build);
            Assert.IsNull(owner.BaseCandidateIndex, "設置前は未構築のまま。");
        }
        finally
        {
            release.Set();
        }

        Assert.AreEqual(BaseCandidateIndexPrepareResult.Installed, await build);
        CollectionAssert.AreEqual(new[] { @"C:\Lib\a|2", @"C:\Lib\B|2", @"C:\Lib\C|1", @"C:\Lib\J\sub|1", @"C:\Lib\K|1" }, Candidates(owner, "Song", "abc"));
        AssertSameAsFullBuild(owner);
    }

    /// <summary>構築を設置の前で止めている間に全置換があれば、その結果を設置せず破棄し、索引は未構築のままになる。</summary>
    [TestMethod]
    public async Task FullReplacementDuringBuild_DiscardsResult()
    {
        CatalogOwnedCollectionOwner owner = new();
        owner.ReplaceCharts(StandardBms(), StandardBmson());
        using var reached = new ManualResetEventSlim();
        using var release = new ManualResetEventSlim();
        owner.BaseCandidateIndexWorkObserver = work =>
        {
            if (work == "base_candidate_install_pending") { reached.Set(); release.Wait(); }
        };
        Task<BaseCandidateIndexPrepareResult> build = ConcurrencyProbe.StartDedicated(() => owner.PrepareBaseCandidateIndex(CancellationToken.None));
        try
        {
            ConcurrencyProbe.WaitForSignal(reached, build, "build reached install");
            Task replace = ConcurrencyProbe.StartDedicated(() => owner.ReplaceCharts([Bms(@"C:\Lib\Only", "Song", "abc")], []));
            ConcurrencyProbe.AssertCompletesWhileHeld(replace, "replacement during build", build);
        }
        finally
        {
            release.Set();
        }

        Assert.AreEqual(BaseCandidateIndexPrepareResult.Discarded, await build);
        Assert.IsNull(owner.BaseCandidateIndex);
        Assert.AreEqual(0, Candidates(owner, "Song", "abc").Length);
    }

    /// <summary>取消された構築は設置せず例外で終わり、索引は未構築のままになる。次の構築は成功する。</summary>
    [TestMethod]
    public async Task CancelledBuild_RemainsCold()
    {
        CatalogOwnedCollectionOwner owner = new();
        owner.ReplaceCharts(StandardBms(), StandardBmson());
        using var cancellation = new CancellationTokenSource();
        using var reached = new ManualResetEventSlim();
        using var release = new ManualResetEventSlim();
        owner.BaseCandidateIndexWorkObserver = work =>
        {
            if (work == "base_candidate_install_pending") { reached.Set(); release.Wait(); }
        };
        Task<BaseCandidateIndexPrepareResult> build = ConcurrencyProbe.StartDedicated(() => owner.PrepareBaseCandidateIndex(cancellation.Token));
        try
        {
            ConcurrencyProbe.WaitForSignal(reached, build, "build reached install");
            cancellation.Cancel();
        }
        finally
        {
            release.Set();
        }

        bool cancelled = false;
        try { await build; }
        catch (OperationCanceledException) { cancelled = true; }
        Assert.IsTrue(cancelled);
        Assert.IsNull(owner.BaseCandidateIndex);

        owner.BaseCandidateIndexWorkObserver = null;
        Assert.AreEqual(BaseCandidateIndexPrepareResult.Installed, owner.PrepareBaseCandidateIndex(CancellationToken.None));
        Assert.AreEqual(5, Candidates(owner, "Song", "abc").Length);
    }

    /// <summary>未構築の間の局所確定は索引を構築しない。</summary>
    [TestMethod]
    public void ColdIndex_LocalCommitDoesNotBuild()
    {
        var work = new List<string>();
        CatalogOwnedCollectionOwner owner = new() { BaseCandidateIndexWorkObserver = work.Add };
        owner.ReplaceCharts([Bms(@"C:\Lib\a", "Song", "abc")], []);

        owner.ApplyCommittedChartUpsert([Bms(@"C:\Lib\b", "Song", "abc")], out _);

        Assert.IsNull(owner.BaseCandidateIndex);
        Assert.AreEqual(0, work.Count);
        Assert.AreEqual(0, Candidates(owner, "Song", "abc").Length);
    }

    /// <summary>並列度が1でも複数でも、構築した索引の内容は同じになる。</summary>
    [TestMethod]
    public void ParallelAndSequentialBuilds_AreEqual()
    {
        CatalogOwnedCollectionOwner sequential = Prepared(StandardBms(), StandardBmson(), degree: 1);
        CatalogOwnedCollectionOwner parallel = Prepared(StandardBms(), StandardBmson(), degree: 4);

        CollectionAssert.AreEqual(sequential.BaseCandidateIndex.Enumerate().ToArray(), parallel.BaseCandidateIndex.Enumerate().ToArray());
        Assert.AreEqual(sequential.BaseCandidateIndex.Shape, parallel.BaseCandidateIndex.Shape);
        CollectionAssert.AreEqual(Candidates(sequential, "Song", "abc"), Candidates(parallel, "Song", "abc"));
    }

    /// <summary>譜面一件の追加・削除の処理量（正規化した譜面と反映した組）は、背景の所持譜面が16件でも128件でも同じで、全構築を起こさない。</summary>
    [TestMethod]
    [DataRow(16)]
    [DataRow(128)]
    public void SingleChange_WorkDoesNotScaleWithBackground(int backgroundCount)
    {
        ChartFile[] background = [.. Enumerable.Range(1, backgroundCount).Select(index => Bms($@"C:\Lib\bg{index}", "Title" + index, "artist" + index))];
        CatalogOwnedCollectionOwner owner = Prepared(background);
        var work = new List<string>();
        owner.BaseCandidateIndexWorkObserver = work.Add;
        ChartFile added = Bms(@"C:\Lib\New", "Song", "abc");

        owner.ApplyCommittedChartUpsert([added], out _);
        CollectionAssert.AreEqual(new[] { "base_candidate_normalize", "base_candidate_delta_apply" }, work);
        work.Clear();
        owner.ApplyMutation([OwnedChartRemoveRequest.FromChart(Current(owner, added))], null);
        CollectionAssert.AreEqual(new[] { "base_candidate_normalize", "base_candidate_delta_apply" }, work);
        AssertSameAsFullBuild(owner);
    }

    /// <summary>所有者が現在持つ、同じパスの譜面を返します。確定経路が識別を必要とするため、投入時の値ではなく現在値を使います。</summary>
    private static ChartFile Current(CatalogOwnedCollectionOwner owner, ChartFile chart)
        => owner.BmsRows.Concat(owner.BmsonRows).Single(candidate => candidate.Path == chart.Path);

    private static CatalogOwnedCollectionOwner Prepared(IEnumerable<ChartFile> bms, IEnumerable<ChartFile>? bmson = null, int? degree = null)
    {
        var owner = new CatalogOwnedCollectionOwner();
        owner.ReplaceCharts(bms, bmson ?? []);
        Assert.AreEqual(BaseCandidateIndexPrepareResult.Installed, owner.PrepareBaseCandidateIndex(CancellationToken.None, degree));
        return owner;
    }

    /// <summary>差分で更新した索引が、現在の所持譜面から新しい所有者で全構築した索引と、内容・入れ物の数ともに一致することを確認します。</summary>
    private static void AssertSameAsFullBuild(CatalogOwnedCollectionOwner owner)
    {
        CatalogOwnedCollectionOwner fresh = Prepared(owner.BmsRows, owner.BmsonRows);
        Assert.IsNotNull(owner.BaseCandidateIndex);
        CollectionAssert.AreEqual(fresh.BaseCandidateIndex.Enumerate().ToArray(), owner.BaseCandidateIndex.Enumerate().ToArray());
        Assert.AreEqual(fresh.BaseCandidateIndex.Shape, owner.BaseCandidateIndex.Shape);
    }

    /// <summary>項目の題名・作者から候補を引き、「フォルダ|一致譜面数」の列で返します。</summary>
    private static string[] Candidates(CatalogOwnedCollectionOwner owner, string title, string artist)
    {
        (string Title, string Artist)? key = InstallEstimationMetadataNormalizer.BuildPairKey(title, artist);
        return key == null ? [] : [.. owner.FindBaseCandidates(key.Value.Title, key.Value.Artist).Select(candidate => candidate.Folder + "|" + candidate.ChartCount)];
    }

    private static ChartFile Bms(string folder, string rawTitle, string rawArtist, string? title = null, string? artist = null)
    {
        int number = Interlocked.Increment(ref sequence);
        return ChartTestValues.Empty() with
        {
            Token = new OwnedChartToken(),
            Path = $@"{folder}\{number}.bms",
            Md5 = number.ToString("x32"),
            Sha256 = number.ToString("x64"),
            Title = title ?? rawTitle,
            RawTitle = rawTitle,
            Artist = artist ?? rawArtist,
            RawArtist = rawArtist
        };
    }

    private static ChartFile Bmson(string folder, string rawTitle, string artist, string rawArtist)
    {
        int number = Interlocked.Increment(ref sequence);
        return ChartTestValues.Empty(ChartFileKind.Bmson) with
        {
            Token = new OwnedChartToken(),
            Path = $@"{folder}\{number}.bmson",
            Md5 = number.ToString("x32"),
            Sha256 = number.ToString("x64"),
            Title = rawTitle,
            RawTitle = rawTitle,
            Artist = artist,
            RawArtist = rawArtist
        };
    }

    /// <summary>項目（題名 Song・作者 abc）に対する一致譜面数が 2・1・1・1・1・1 の六フォルダと、除外される五つのフォルダ、無関係な題名のフォルダです。</summary>
    private static ChartFile[] StandardBms() =>
    [
        Bms(@"C:\Lib\a", "Song", "abc"),
        Bms(@"C:\Lib\a", "Song [Another]", "abd"),
        Bms(@"C:\Lib\a2", "Song", "abc"),
        Bms(@"C:\Lib\B", "Song", "ABC"),
        Bms(@"C:\Lib\J\sub", "Song", "abc"),
        Bms(@"C:\Lib\K", "Song", "abc"),
        Bms(@"C:\Lib\D", "Song", "abde"),
        Bms(@"C:\Lib\E", "Song", string.Empty),
        Bms(@"C:\Lib\F", string.Empty, "abc", title: "Song"),
        Bms(@"C:\Lib\G", "Songs", "abc"),
        Bms(@"C:\Lib\H", "Song", "xyz", artist: "abc"),
        Bms(@"C:\Lib\Z", "Unique", "u")
    ];

    private static ChartFile[] StandardBmson() =>
    [
        Bmson(@"C:\Lib\C", "Song", "abc feat. Guest", "unrelated"),
        Bmson(@"C:\Lib\I", "Song", "xyz", "abc")
    ];
}
