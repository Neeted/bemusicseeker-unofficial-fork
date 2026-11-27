using BeMusicSeeker.Models;
using BeMusicSeeker.Models.BmsLibraryInternal;
using BeMusicSeeker.TestSupport.Components;
using BeMusicSeeker.ViewModels;
using static BeMusicSeeker.TestSupport.Components.PlaylistCatalogViewTestSupport;

namespace BeMusicSeeker.Tests;

/// <summary>
/// プレイリスト詳細の未所持行が、本体フォルダを所持していると推定できるときの表示判定とメニューの候補を、
/// 実譜面・SQLite・実の詳細データ元・ワークスペースを通して確認します。照合規則そのものは索引の試験で確認します。
/// </summary>
[TestClass]
public sealed class PlaylistDetailBaseCandidateTests
{
    private static readonly Uri AbsoluteUrl = new("https://example.test/diff");

    private static int sequence;

    /// <summary>
    /// 未所持で、URL2が絶対URIで、候補が1件以上の行だけがURL1の強調を求め、所持行・URL2なし・URL2が相対・候補なし・索引が未構築の行は従来どおりになる。
    /// 索引の構築が完了すると、実体化済みの未所持行へ変更通知が届き、判定が現在の索引へ収束する。
    /// </summary>
    [TestMethod]
    public async Task Url1Highlight_RequiresUnownedAbsoluteUrl2AndCandidate()
    {
        await using var fixture = CreateFixture(16);
        ApplyCandidateCharts(fixture);
        await fixture.Refresh();
        var rows = (PlaylistDetailVirtualView)fixture.Workspace.DetailViewState.View.Rows;
        BMSTableEntry[] entries = [.. fixture.Playlist.BMSTables.Single().entries];
        PlaylistDetailRow owned = RowFor(rows, entries[0]);
        PlaylistDetailRow highlighted = RowFor(rows, entries[1]);
        PlaylistDetailRow withoutUrl2 = RowFor(rows, entries[2]);
        PlaylistDetailRow relativeUrl2 = RowFor(rows, entries[3]);
        PlaylistDetailRow noCandidate = RowFor(rows, entries[4]);
        var received = new List<string?>();
        highlighted.PropertyChanged += (_, args) => received.Add(args.PropertyName);
        var ownedReceived = new List<string?>();
        owned.PropertyChanged += (_, args) => ownedReceived.Add(args.PropertyName);

        // 索引が未構築の間は、条件を満たす行も従来の表示のままで、例外にならない。
        Assert.IsFalse(highlighted.HasBaseCandidateUrl1Highlight);
        Assert.AreEqual(0, highlighted.GetBaseCandidates().Count);

        fixture.Workspace.SchedulePlaylistLibraryIndexPrewarm("base_candidate_test");
        await fixture.Workspace.GetPlaylistLibraryIndexPrewarmTask();

        CollectionAssert.Contains(received, nameof(PlaylistDetailRow.HasBaseCandidateUrl1Highlight));
        Assert.AreEqual(0, ownedReceived.Count, "所持行へは変更通知を出さない。");
        Assert.IsTrue(highlighted.HasBaseCandidateUrl1Highlight);
        Assert.IsFalse(owned.HasBaseCandidateUrl1Highlight, "所持行は題名・作者が一致しても強調しない。");
        Assert.IsFalse(withoutUrl2.HasBaseCandidateUrl1Highlight, "URL2がなければ強調しない。");
        Assert.IsFalse(relativeUrl2.HasBaseCandidateUrl1Highlight, "URL2が絶対URIでなければ強調しない。");
        Assert.IsFalse(noCandidate.HasBaseCandidateUrl1Highlight, "候補が0件なら強調しない。");

        // 全置換で索引が破棄されると、候補は空になり従来の表示へ戻る。
        OwnedChartCollectionTestSupport.GetOwnedCollectionOwner(fixture.Library).ReplaceCharts(
            fixture.Library.BmsCharts.Where(chart => !chart.Path.StartsWith(@"C:\BaseCandidates\", StringComparison.Ordinal)), []);
        Assert.IsFalse(highlighted.HasBaseCandidateUrl1Highlight);
    }

    /// <summary>
    /// メニューの候補は、メニューを開いた未所持行のものを、一致譜面数の降順・同数はパスの昇順・先頭5件で返し、URL2の有無に依存しない。
    /// 所持行と索引が未構築の間は空になる。
    /// </summary>
    [TestMethod]
    public async Task MenuCandidates_UseContextRowIndependentOfUrl2()
    {
        await using var fixture = CreateFixture(16);
        ApplyCandidateCharts(fixture);
        await fixture.Refresh();
        var rows = (PlaylistDetailVirtualView)fixture.Workspace.DetailViewState.View.Rows;
        BMSTableEntry[] entries = [.. fixture.Playlist.BMSTables.Single().entries];
        PlaylistDetailRow withUrl2 = RowFor(rows, entries[1]);
        PlaylistDetailRow withoutUrl2 = RowFor(rows, entries[2]);
        PlaylistDetailRow relativeUrl2 = RowFor(rows, entries[3]);
        PlaylistDetailRow noCandidate = RowFor(rows, entries[4]);
        PlaylistDetailRow owned = RowFor(rows, entries[0]);
        Assert.AreEqual(0, fixture.Workspace.CaptureBaseCandidates(withUrl2).Count, "索引が未構築の間は空。");

        fixture.Workspace.SchedulePlaylistLibraryIndexPrewarm("base_candidate_test");
        await fixture.Workspace.GetPlaylistLibraryIndexPrewarmTask();

        string[] expected =
        [
            @"C:\BaseCandidates\F1|3", @"C:\BaseCandidates\F2|2", @"C:\BaseCandidates\F3|1",
            @"C:\BaseCandidates\F4|1", @"C:\BaseCandidates\F5|1"
        ];
        foreach (PlaylistDetailRow row in new[] { withUrl2, withoutUrl2, relativeUrl2 })
        {
            CollectionAssert.AreEqual(expected, fixture.Workspace.CaptureBaseCandidates(row).Select(candidate => candidate.Folder + "|" + candidate.ChartCount).ToArray());
        }
        Assert.AreEqual(0, fixture.Workspace.CaptureBaseCandidates(noCandidate).Count);
        Assert.AreEqual(0, fixture.Workspace.CaptureBaseCandidates(owned).Count, "所持行には候補を出さない。");
        Assert.AreEqual(0, fixture.Workspace.CaptureBaseCandidates(new object()).Count);
    }

    /// <summary>
    /// 所持譜面の原題・作者・所属の確定の後に、実体化済みの未所持行の判定が現在の索引へ収束する。
    /// 無関係な確定（表示用の題名だけの変更）では変更通知を出さない。背景の所持譜面が16件でも128件でも同じ。
    /// </summary>
    [TestMethod]
    [DataRow(16)]
    [DataRow(128)]
    public async Task CatalogCommits_ConvergeRealizedUnownedRows(int backgroundCount)
    {
        await using var fixture = CreateFixture(backgroundCount, regularDelivery: true);
        await fixture.DrainLibraryChanges();
        CatalogOwnedCollectionOwner catalog = OwnedChartCollectionTestSupport.GetOwnedCollectionOwner(fixture.Library);
        ApplyCandidateCharts(fixture);
        await fixture.Refresh();
        await fixture.DrainLibraryChanges();
        var rows = (PlaylistDetailVirtualView)fixture.Workspace.DetailViewState.View.Rows;
        BMSTableEntry[] entries = [.. fixture.Playlist.BMSTables.Single().entries];
        PlaylistDetailRow target = RowFor(rows, entries[1]);
        PlaylistDetailRow owned = RowFor(rows, entries[0]);
        fixture.Workspace.SchedulePlaylistLibraryIndexPrewarm("base_candidate_test");
        await fixture.Workspace.GetPlaylistLibraryIndexPrewarmTask();
        Assert.IsTrue(target.HasBaseCandidateUrl1Highlight);
        var received = new List<string?>();
        target.PropertyChanged += (_, args) => received.Add(args.PropertyName);
        var ownedReceived = new List<string?>();
        owned.PropertyChanged += (_, args) => ownedReceived.Add(args.PropertyName);

        // 一致する所持譜面が全部消えると、判定は偽へ収束する。
        ChartFile[] current = [.. fixture.Library.BmsCharts.Where(chart => chart.Path.StartsWith(@"C:\BaseCandidates\", StringComparison.Ordinal))];
        catalog.ApplyMutation([.. current.Select(OwnedChartRemoveRequest.FromChart)], null);
        await fixture.DrainLibraryChanges();
        CollectionAssert.Contains(received, nameof(PlaylistDetailRow.HasBaseCandidateUrl1Highlight));
        Assert.IsFalse(target.HasBaseCandidateUrl1Highlight);

        // 一致する所持譜面が加わると、真へ収束する。
        received.Clear();
        ChartFile added = Candidate(@"C:\BaseCandidates\Again");
        catalog.ApplyCommittedChartUpsert([added], out _);
        await fixture.DrainLibraryChanges();
        CollectionAssert.Contains(received, nameof(PlaylistDetailRow.HasBaseCandidateUrl1Highlight));
        Assert.IsTrue(target.HasBaseCandidateUrl1Highlight);
        Assert.AreEqual(0, ownedReceived.Count(name => name == nameof(PlaylistDetailRow.HasBaseCandidateUrl1Highlight)), "所持行へは本体候補の変更通知を出さない。");

        // 原題・作者・所属に関わらない確定では、本体候補の変更通知を出さない。
        received.Clear();
        ChartFile unchanged = fixture.Library.BmsCharts.Single(chart => chart.Path == added.Path);
        catalog.CommitCurrentValues([unchanged], (chart, _) => chart with { Title = "display only" });
        await fixture.DrainLibraryChanges();
        Assert.IsFalse(received.Contains(nameof(PlaylistDetailRow.HasBaseCandidateUrl1Highlight)));
        Assert.IsTrue(target.HasBaseCandidateUrl1Highlight);
    }

    private static Fixture CreateFixture(int count, bool regularDelivery = false)
        => new(count, regularDelivery: regularDelivery, prepareTable: PrepareEntries, modelUiScheduler: new TestUiScheduler(() => null));

    /// <summary>
    /// 添字0は所持行（題名・作者が一致し、URL2が絶対）、1は強調される未所持行、2はURL2なし、3はURL2が相対、4は候補のない題名です。
    /// </summary>
    private static void PrepareEntries(BMSTable table, List<BMSTableEntry> entries)
    {
        entries[0].title = "Song";
        entries[0].artist = "abc";
        entries[0].Url_diff = AbsoluteUrl;
        Unowned(entries[1], 1, "Song", "abc", AbsoluteUrl);
        Unowned(entries[2], 2, "Song", "abc", null);
        Unowned(entries[3], 3, "Song", "abc", new Uri("diff/relative.zip", UriKind.Relative));
        Unowned(entries[4], 4, "Nothing", "abc", AbsoluteUrl);
    }

    private static void Unowned(BMSTableEntry entry, int number, string title, string artist, Uri? urlDiff)
    {
        entry.md5 = new string('9', 31) + number.ToString("x");
        entry.sha256 = null;
        entry.title = title;
        entry.artist = artist;
        entry.Url_diff = urlDiff;
    }

    private static PlaylistDetailRow RowFor(PlaylistDetailVirtualView rows, BMSTableEntry entry)
        => Enumerable.Range(0, rows.Count).Select(index => (PlaylistDetailRow)rows[index]).Single(row => ReferenceEquals(row.Entry, entry));

    /// <summary>
    /// 一致する所持譜面を、F1に3件・F2に2件・F3からF6に1件ずつ加えます。項目（Song・abc）の候補は、F1・F2・F3・F4・F5の順で先頭5件になります。
    /// </summary>
    private static ChartFile[] ApplyCandidateCharts(Fixture fixture)
    {
        ChartFile[] charts =
        [
            .. Enumerable.Range(0, 3).Select(_ => Candidate(@"C:\BaseCandidates\F1")),
            .. Enumerable.Range(0, 2).Select(_ => Candidate(@"C:\BaseCandidates\F2")),
            Candidate(@"C:\BaseCandidates\F3"),
            Candidate(@"C:\BaseCandidates\F4"),
            Candidate(@"C:\BaseCandidates\F5"),
            Candidate(@"C:\BaseCandidates\F6")
        ];
        OwnedChartCollectionTestSupport.GetOwnedCollectionOwner(fixture.Library).ApplyCommittedChartUpsert(charts, out _);
        return charts;
    }

    private static ChartFile Candidate(string folder)
    {
        int number = Interlocked.Increment(ref sequence);
        return ChartTestValues.Empty() with
        {
            Token = new OwnedChartToken(),
            Path = $@"{folder}\{number}.bms",
            Md5 = "c" + number.ToString("x31"),
            Sha256 = "c" + number.ToString("x63"),
            Title = "Song",
            RawTitle = "Song",
            Artist = "abc",
            RawArtist = "abc"
        };
    }
}
