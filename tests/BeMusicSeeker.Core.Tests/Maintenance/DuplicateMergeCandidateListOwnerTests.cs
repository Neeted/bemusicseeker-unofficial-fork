using System.ComponentModel;
using BeMusicSeeker.Models;
using BeMusicSeeker.Properties;
using BeMusicSeeker.ViewModels;
using BeMusicSeeker.Views;

namespace BeMusicSeeker.Tests;

/// <summary>
/// 統合候補一覧の管理主体を、メモリ上の <see cref="DuplicateGroup"/> と、TITLE・ARTISTの解決・登録範囲の述語だけを代替した入力で検証します。
/// 表・ツリー・ライブラリの接続は別のケースが担当し、ここでは状態・並び・検索・操作・寿命・解決の呼出し回数だけを確かめます。
/// </summary>
[TestClass]
public sealed class DuplicateMergeCandidateListOwnerTests
{
    private const string Md5 = "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa";

    private static ChartFile Chart(string folder, string name)
        => new(ChartFileKind.Bms, Path.Combine(folder, name + ".bms"), Md5, null, "title", "raw", "artist", string.Empty,
            Path.GetFileName(folder), string.Empty, string.Empty, null, 0, null);

    /// <summary>フォルダごとの直下の譜面数を指定してグループを作ります。登録順がそのまま <see cref="DuplicateGroup.Folders"/> の順になります。</summary>
    private static DuplicateGroup Group(params (string Folder, int Charts)[] folders)
        => new(
            [.. folders.SelectMany(folder => Enumerable.Range(0, folder.Charts).Select(index => Chart(folder.Folder, "chart" + index)))],
            [.. folders.Select(folder => folder.Folder)]);

    private static DuplicateGroup Group(params string[] folders) => Group([.. folders.Select(folder => (folder, 1))]);

    /// <summary>解決と登録範囲の捕捉の呼出しを記録する入力の代替です。</summary>
    private sealed class Source : IDuplicateMergeCandidateListSource
    {
        internal HashSet<string> RegisteredRoots { get; } = new(StringComparer.OrdinalIgnoreCase);

        internal Dictionary<string, (string Title, string Artist)> Pairs { get; } = new(StringComparer.OrdinalIgnoreCase);

        internal int ResolveCalls;

        internal int CaptureCalls;

        internal List<int> ResolveThreadIds { get; } = [];

        public IReadOnlyDictionary<string, (string Title, string Artist)> ResolveDestinationDisplayPairs(
            IReadOnlyList<string> folders, CancellationToken cancellationToken)
        {
            Interlocked.Increment(ref ResolveCalls);
            lock (ResolveThreadIds) { ResolveThreadIds.Add(Environment.CurrentManagedThreadId); }
            return folders.Distinct(StringComparer.OrdinalIgnoreCase)
                .ToDictionary(
                    folder => folder,
                    folder => Pairs.TryGetValue(folder, out (string Title, string Artist) pair) ? pair : (string.Empty, string.Empty),
                    StringComparer.OrdinalIgnoreCase);
        }

        public Func<string, bool> CaptureRegisteredBmsRootPredicate()
        {
            Interlocked.Increment(ref CaptureCalls);
            HashSet<string> captured = [.. RegisteredRoots];
            return folder => captured.Contains(folder);
        }
    }

    private sealed class Fixture
    {
        internal object Library { get; set; } = new();

        internal Source Source { get; } = new();

        internal List<string> Opened { get; } = [];

        internal HashSet<string> ExistingFolders { get; } = new(StringComparer.OrdinalIgnoreCase);

        internal DuplicateMergeCandidateListOwner Owner { get; }

        internal Fixture()
        {
            Owner = new DuplicateMergeCandidateListOwner(path => ExistingFolders.Contains(path), Opened.Add);
            Owner.AttachLibrary(Library);
            Owner.SetTreeSelected(true);
        }

        /// <summary>専用のスレッドで実行します。スレッドプールのTaskは、待機側のスレッドで実行（インライン化）されることがあるためです。</summary>
        internal static T PrepareOffThread<T>(Func<T> work)
        {
            T result = default!;
            Exception? failure = null;
            var thread = new Thread(() => { try { result = work(); } catch (Exception exception) { failure = exception; } });
            thread.Start();
            thread.Join();
            if (failure != null) { System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(failure).Throw(); }
            return result;
        }

        /// <summary>UIスレッド外で構築し、採用します。呼び出し元のスレッドは状態表を読み書きするUIスレッドの代替です。</summary>
        internal void Adopt(params DuplicateGroup[] groups)
        {
            DuplicateMergeCandidateListPreparation preparation = PrepareOffThread(
                () => DuplicateMergeCandidateListOwner.Prepare(Library, groups, Source, CancellationToken.None));
            Assert.IsTrue(Owner.Adopt(preparation));
        }

        internal DuplicateMergeCandidateRow[] Rows => [.. Owner.RowSet];

        internal DuplicateMergeCandidateRow Row(string folder)
            => Rows.Single(row => string.Equals(row.Path, folder, StringComparison.OrdinalIgnoreCase));

        internal string[] Paths => [.. Rows.Select(row => row.Path)];
    }

    /// <summary>
    /// 保証：一行は一フォルダで、グループ内の行はPATHの昇順（大文字小文字を無視）、グループの既定の並びは重複検索結果の順になる。
    /// 一フォルダだけのグループも表示する。行グループの提供元は表示順で連続した範囲と0始まりの序数を返し、行ごとの問合せはグループの大きさに依存しない。
    /// TITLE・ARTISTは解決した値を行へ出し、有効なペアがないフォルダは両方空である。構築ごとの解決は一回で、UIスレッド外で行う。
    /// 検出する誤り：入力順のままの行、大文字小文字を区別する並び、一フォルダのグループの欠落、境界・序数のずれ、解決の繰返し・UIスレッドでの解決。
    /// </summary>
    [TestMethod]
    public void Build_OrdersRowsByPathAndGroupsByResultOrderWithResolvedPairs()
    {
        var fixture = new Fixture();
        fixture.Source.Pairs[@"C:\Songs\b"] = ("Title B", "Artist B");
        DuplicateGroup first = Group(@"C:\Songs\c", @"C:\Songs\B", @"C:\Songs\a");
        DuplicateGroup single = Group(@"C:\Songs\Single");
        DuplicateGroup last = Group(@"C:\Songs\z2", @"C:\Songs\Z1");

        fixture.Adopt(first, single, last);

        CollectionAssert.AreEqual(
            new[] { @"C:\Songs\a", @"C:\Songs\B", @"C:\Songs\c", @"C:\Songs\Single", @"C:\Songs\Z1", @"C:\Songs\z2" }, fixture.Paths);
        DuplicateMergeCandidateRowSet rows = fixture.Owner.RowSet;
        (int First, int Count, int Ordinal)[] expectedGroups = [(0, 3, 0), (3, 1, 1), (4, 2, 2)];
        for (int index = 0; index < rows.Count; index++)
        {
            CustomTableRowGroup group = rows.GetRowGroup(index);
            (int expectedFirst, int expectedCount, int expectedOrdinal) = expectedGroups[index switch { < 3 => 0, 3 => 1, _ => 2 }];
            Assert.AreEqual(new CustomTableRowGroup(expectedFirst, expectedCount, expectedOrdinal), group, "行" + index);
        }
        Assert.AreEqual(("Title B", "Artist B"), (fixture.Row(@"C:\Songs\B").Title, fixture.Row(@"C:\Songs\B").Artist));
        Assert.AreEqual(string.Empty, fixture.Row(@"C:\Songs\a").Title, "有効なペアがなければ空です。");
        Assert.AreEqual(1, fixture.Source.ResolveCalls, "構築ごとの解決は一回です。");
        Assert.AreEqual(1, fixture.Source.CaptureCalls, "登録範囲の捕捉は構築ごとに一回です。");
        Assert.AreNotEqual(Environment.CurrentManagedThreadId, fixture.Source.ResolveThreadIds.Single(), "解決はUIスレッド外で行います。");
        Assert.AreEqual(3, fixture.Owner.VisibleGroupCount);
    }

    /// <summary>
    /// 保証：初期のKEEPは、グループの譜面（兄弟譜面を含む）のうちそのフォルダ直下にある所持譜面数が最多のフォルダで、同数ならPATH昇順（大文字小文字を無視）の先頭。
    /// 登録範囲のフォルダが一つならKEEPをそこへ固定し、SELECTは操作できる。二つ以上なら、PATH昇順の先頭へ固定し、
    /// STATUSを「統合不可（登録ルート）」、SELECTをオフかつ無効にする。一フォルダのグループはKEEP固定。
    /// 検出する誤り：登録順の先頭を選ぶ、同数で大文字小文字を区別して並べる、兄弟譜面を数えない、固定の漏れ、統合不可の見落とし。
    /// </summary>
    [TestMethod]
    [DataRow("most", @"C:\S\b", false, false)]
    [DataRow("tieCaseInsensitive", @"C:\S\a", false, false)]
    [DataRow("singleRegistered", @"C:\S\c", true, false)]
    [DataRow("twoRegistered", @"C:\S\b", true, true)]
    [DataRow("singleFolder", @"C:\S\only", true, false)]
    public void InitialKeepAndFixing_FollowCountsAndRegisteredRange(string scenario, string expectedKeep, bool expectedFixed, bool expectedUnmergeable)
    {
        var fixture = new Fixture();
        DuplicateGroup group = scenario switch
        {
            "most" => Group((@"C:\S\c", 1), (@"C:\S\b", 3), (@"C:\S\a", 2)),
            "tieCaseInsensitive" => Group((@"C:\S\b", 2), (@"C:\S\A", 2), (@"C:\S\c", 1)),
            "singleRegistered" => Group((@"C:\S\a", 5), (@"C:\S\c", 1)),
            "twoRegistered" => Group((@"C:\S\c", 9), (@"C:\S\b", 1), (@"C:\S\a", 5)),
            _ => Group((@"C:\S\only", 3))
        };
        if (scenario == "singleRegistered") { fixture.Source.RegisteredRoots.Add(@"C:\S\c"); }
        if (scenario == "twoRegistered") { fixture.Source.RegisteredRoots.UnionWith([@"C:\S\c", @"C:\S\b"]); }

        fixture.Adopt(group);

        Assert.IsTrue(fixture.Owner.TryGetState(group, out DuplicateMergeCandidateGroupState? state));
        Assert.IsTrue(string.Equals(expectedKeep, state!.KeepFolder, StringComparison.OrdinalIgnoreCase), "KEEP: " + state.KeepFolder);
        Assert.AreEqual(expectedFixed, state.KeepFixed);
        Assert.AreEqual(!expectedUnmergeable, state.CanSelect);
        Assert.IsFalse(state.IsSelected, "SELECTの初期値はオフです。");
        Assert.AreEqual(
            expectedUnmergeable ? Resources.Duplicate_merge_candidate_status_unmergeable_registered_root : Resources.Duplicate_merge_candidate_status_unverified,
            fixture.Rows[0].StatusText);
        Assert.AreEqual(1, fixture.Rows.Count(row => row.IsKeep), "KEEPはグループで常に一つです。");
        Assert.AreEqual(expectedUnmergeable, !fixture.Owner.ToggleSelect(fixture.Rows[0]), "統合不可はオンにできません。");
    }

    /// <summary>
    /// 保証：列見出しの並べ替えは、グループの先頭行（PATH昇順の先頭）の値を使ってグループ単位に並べ、SELECTはオフが先、STATUSは表示文言の比較で、
    /// KEEPは対象外。同値は既定の結果順を保ち、グループ内の行順は変えない。比較は大文字小文字を無視し、昇順・降順の両方を持つ。
    /// STATUSは、統合不可のグループ（グループ2）と未確認のグループを混ぜ、期待の順を現在の表示言語の文言の比較から求める。
    /// 検出する誤り：行単位の並べ替え（グループが分かれる）、全行の値で並べる、同値で順が入れ替わる、降順での同値の逆転、KEEPでの並べ替え、
    /// STATUSを並べ替えない（結果順のまま）誤り、STATUSの降順で同値の結果順を崩す誤り。
    /// 限界：既定の表示言語（日本語）では「未確認」が「統合不可（登録ルート）」より前で、段階の宣言順と同じ向きのため、文言ではなく段階で並べる誤りは区別しない。
    /// </summary>
    [TestMethod]
    [DataRow("Path", false, new[] { 1, 0, 3, 2 })]
    [DataRow("Path", true, new[] { 2, 3, 0, 1 })]
    [DataRow("Folder", false, new[] { 2, 1, 3, 0 })]
    [DataRow("Title", false, new[] { 0, 1, 3, 2 })]
    [DataRow("Artist", true, new[] { 2, 0, 1, 3 })]
    [DataRow("Select", false, new[] { 2, 3, 0, 1 })]
    [DataRow("Select", true, new[] { 0, 1, 2, 3 })]
    [DataRow("Status", false, null)]
    [DataRow("Status", true, null)]
    [DataRow("Keep", false, new[] { 0, 1, 2, 3 })]
    public void Sort_UsesFirstRowOfEachGroupAndKeepsTiesInResultOrder(string column, bool descending, int[]? expectedOrder)
    {
        var fixture = new Fixture();
        DuplicateGroup[] groups =
        [
            Group(@"C:\S\m\one", @"C:\S\m\zzz"),
            Group(@"C:\S\a\bb"),
            Group(@"C:\S\Z\aa", @"C:\S\Z\ab"),
            Group(@"c:\s\x\cc"),
        ];
        // 先頭行（PATH昇順の先頭）: 0=m\one, 1=a\bb, 2=Z\aa, 3=x\cc。FOLDER: one, bb, aa, cc。
        fixture.Source.Pairs[@"C:\S\m\one"] = ("alpha", "same");
        fixture.Source.Pairs[@"C:\S\a\bb"] = ("Alpha", "SAME");
        fixture.Source.Pairs[@"C:\S\Z\aa"] = ("zeta", "same2");
        fixture.Source.Pairs[@"c:\s\x\cc"] = ("beta", "same");
        // グループ2は登録範囲のフォルダを二つ持つため統合不可（SELECTはオフのまま）。STATUS以外の列の期待順には影響しない。
        fixture.Source.RegisteredRoots.UnionWith([@"C:\S\Z\aa", @"C:\S\Z\ab"]);
        fixture.Adopt(groups);
        Assert.AreEqual(Resources.Duplicate_merge_candidate_status_unmergeable_registered_root, fixture.Row(@"C:\S\Z\aa").StatusText, "前提：グループ2は統合不可です。");
        if (expectedOrder == null)
        {
            // 未確認（0・1・3）と統合不可（2）の二値。同値の未確認は昇順・降順とも結果順を保つ。
            bool unmergeableFirst = StringComparer.OrdinalIgnoreCase.Compare(
                Resources.Duplicate_merge_candidate_status_unmergeable_registered_root, Resources.Duplicate_merge_candidate_status_unverified) < 0;
            expectedOrder = unmergeableFirst != descending ? [2, 0, 1, 3] : [0, 1, 3, 2];
        }
        // SELECT：グループ0と1をオン。昇順はオフが先、降順はオンが先。
        fixture.Owner.ToggleSelect(fixture.Rows[0]);
        fixture.Owner.ToggleSelect(fixture.Row(@"C:\S\a\bb"));

        fixture.Owner.RequestSort(column, descending ? ListSortDirection.Descending : ListSortDirection.Ascending);

        DuplicateGroup[] shown = [.. fixture.Rows.Select(row => row.State.Group).Distinct()];
        // 並び替え対象外（Keep）は何も変えない。それ以外は期待した順。
        int[] actual = [.. shown.Select(group => Array.IndexOf(groups, group))];
        CollectionAssert.AreEqual(expectedOrder, actual, "並べ替えの列 " + column + (descending ? " 降順" : " 昇順"));
        foreach (DuplicateGroup group in groups)
        {
            string[] expectedInGroup = [.. group.Folders.Order(StringComparer.OrdinalIgnoreCase)];
            CollectionAssert.AreEqual(expectedInGroup, fixture.Rows.Where(row => row.State.Group == group).Select(row => row.Path).ToArray(),
                "グループ内の行順はPATH昇順のままです。");
        }
        DuplicateMergeCandidateRowSet rows = fixture.Owner.RowSet;
        for (int index = 0; index < rows.Count; index++)
        {
            Assert.AreEqual(rows[index].GroupIndex, rows.GetRowGroup(index).Ordinal, "序数は表示順に振り直します。");
        }
    }

    /// <summary>
    /// 保証：検索は既存のキーワード検索の構文を使い、条件は行単位で評価して、一致した行を一つでも持つグループの全行を残す。
    /// 各条件を別の行で満たすだけのグループ（AND）は残らず、否定も行単位で、項目はpath・folder・title・artist・statusで、
    /// 項目を指定しない語は五項目のいずれかへの一致。STATUSの表示文言も対象で、検索後の序数は表示順に振り直す。
    /// 検出する誤り：一致行だけの表示、グループ単位のAND評価、否定をグループ全体で評価する誤り、STATUSの未対応、序数の飛び。
    /// </summary>
    [TestMethod]
    public void Search_KeepsWholeGroupsOfMatchingRowsWithRowLevelConditions()
    {
        var fixture = new Fixture();
        DuplicateGroup g1 = Group(@"C:\S\alpha1", @"C:\S\beta1");
        DuplicateGroup g2 = Group(@"C:\S\gamma", @"C:\S\delta");
        DuplicateGroup g3 = Group(@"C:\S\epsilon");
        fixture.Source.Pairs[@"C:\S\gamma"] = ("Moon Song", "Luna");
        fixture.Source.Pairs[@"C:\S\epsilon"] = ("Sun Song", "Sol");
        fixture.Adopt(g1, g2, g3);

        void Search(string text, params DuplicateGroup[] expected)
        {
            fixture.Owner.SearchText = text;
            CollectionAssert.AreEqual(expected, fixture.Rows.Select(row => row.State.Group).Distinct().ToArray(), "検索語 " + text);
            Assert.AreEqual(expected.Length, fixture.Owner.VisibleGroupCount);
            DuplicateMergeCandidateRowSet rows = fixture.Owner.RowSet;
            for (int i = 0; i < rows.Count; i++) { Assert.AreEqual(rows.GetRowGroup(i).Ordinal, rows[i].GroupIndex); }
            if (expected.Length > 0) { Assert.AreEqual(expected.Length - 1, rows[^1].GroupIndex, "序数は表示順に振り直します。"); }
        }

        Search("alpha", g1);
        Assert.AreEqual(2, fixture.Rows.Length, "一致した行を持つグループの全行を残します。");
        Search("alpha gamma");
        Search("alpha beta");
        Search("alpha 1", g1);
        Search("-alpha", g1, g2, g3);
        Search("-alpha -beta", g2, g3);
        Search("title:moon", g2);
        Search("artist:sol", g3);
        Search("path:epsilon -title:moon", g3);
        Search("moon luna", g2);
        Search("folder:alpha1", g1);
        Search("status:" + Resources.Duplicate_merge_candidate_status_unverified, g1, g2, g3);
        Search("re:^alpha\\d$ folder:re:^beta");
        Search("folder:re:^alpha\\d$", g1);
        Search("unknownfield:x");
        Search(string.Empty, g1, g2, g3);
    }

    /// <summary>
    /// 保証：件数表示は、表示中のグループ数と、表示中でSELECTがオンのグループ数を示す。非表示のグループのSELECTは選択に数えない。
    /// 検出する誤り：全グループのSELECTを数える、行数をグループ数として数える、変更通知の欠落。
    /// </summary>
    [TestMethod]
    public void Counts_ReportVisibleGroupsAndVisibleSelectedGroups()
    {
        var fixture = new Fixture();
        DuplicateGroup a = Group(@"C:\S\a1", @"C:\S\a2"), b = Group(@"C:\S\b1"), c = Group(@"C:\S\c1");
        fixture.Adopt(a, b, c);
        var changed = new List<string?>();
        fixture.Owner.PropertyChanged += (_, args) => changed.Add(args.PropertyName);

        fixture.Owner.ToggleSelect(fixture.Row(@"C:\S\a1"));
        fixture.Owner.ToggleSelect(fixture.Row(@"C:\S\c1"));
        Assert.AreEqual((3, 2), (fixture.Owner.VisibleGroupCount, fixture.Owner.SelectedGroupCount));
        CollectionAssert.Contains(changed, nameof(DuplicateMergeCandidateListOwner.SelectedGroupCount));

        fixture.Owner.SearchText = "a1";
        Assert.AreEqual((1, 1), (fixture.Owner.VisibleGroupCount, fixture.Owner.SelectedGroupCount), "非表示のcは選択に数えません。");
        fixture.Owner.SearchText = string.Empty;
        Assert.AreEqual((3, 2), (fixture.Owner.VisibleGroupCount, fixture.Owner.SelectedGroupCount));
    }

    /// <summary>
    /// 保証：SELECTのクリックでそのグループのオン・オフを切り替え、KEEPのクリックでそのフォルダをKEEPにする（グループ内で常に一つ）。
    /// 固定のKEEPと統合不可のSELECTは操作しても変わらない。結合列では先頭でない行でも同じグループへ作用する。
    /// 検出する誤り：先頭行でしか作用しない、KEEPが複数になる、固定の無視、統合不可がオンになる。
    /// </summary>
    [TestMethod]
    public void CellOperations_ToggleSelectAndMoveKeepWithinTheRowsGroup()
    {
        var fixture = new Fixture();
        DuplicateGroup normal = Group((@"C:\S\a", 3), (@"C:\S\b", 1), (@"C:\S\c", 1));
        DuplicateGroup fixedGroup = Group(@"C:\S\x1", @"C:\S\x2");
        fixture.Source.RegisteredRoots.Add(@"C:\S\x2");
        fixture.Adopt(normal, fixedGroup);

        Assert.IsTrue(fixture.Owner.ToggleSelect(fixture.Row(@"C:\S\c")), "先頭でない行のセルでも同じです。");
        Assert.IsTrue(fixture.Rows.Where(row => row.State.Group == normal).All(row => row.State.IsSelected));
        Assert.IsTrue(fixture.Owner.ToggleSelect(fixture.Row(@"C:\S\a")));
        Assert.IsFalse(fixture.Row(@"C:\S\b").State.IsSelected);

        Assert.IsTrue(fixture.Owner.SetKeep(fixture.Row(@"C:\S\c")));
        Assert.AreEqual(@"C:\S\c", fixture.Row(@"C:\S\a").State.KeepFolder);
        Assert.AreEqual(1, fixture.Rows.Count(row => row.State.Group == normal && row.IsKeep));
        Assert.IsFalse(fixture.Owner.SetKeep(fixture.Row(@"C:\S\c")), "すでにKEEPのフォルダは変わりません。");

        Assert.IsFalse(fixture.Owner.SetKeep(fixture.Row(@"C:\S\x1")), "固定のKEEPは変わりません。");
        Assert.AreEqual(@"C:\S\x2", fixture.Row(@"C:\S\x1").State.KeepFolder);
        Assert.IsTrue(fixture.Owner.ToggleSelect(fixture.Row(@"C:\S\x2")), "登録範囲が一つのグループのSELECTは操作できます。");
    }

    /// <summary>
    /// 保証：Spaceキーは、選択行が属するグループごとに一度だけ作用し、統合不可を除いたグループにオンとオフが混在していればすべてオンにする。
    /// すべてオンならすべてオフ、すべてオフならすべてオンで、統合不可は変えない。
    /// 検出する誤り：行ごとに切り替えて同じグループが元へ戻る、混在でオフになる、統合不可がオンになる。
    /// </summary>
    [TestMethod]
    public void Space_TogglesEachGroupOnceAndTurnsMixedSelectionOn()
    {
        var fixture = new Fixture();
        DuplicateGroup a = Group(@"C:\S\a1", @"C:\S\a2"), b = Group(@"C:\S\b1"), blocked = Group(@"C:\S\r1", @"C:\S\r2");
        fixture.Source.RegisteredRoots.UnionWith([@"C:\S\r1", @"C:\S\r2"]);
        fixture.Adopt(a, b, blocked);
        DuplicateMergeCandidateRow[] bothRowsOfAAndB = [fixture.Row(@"C:\S\a1"), fixture.Row(@"C:\S\a2"), fixture.Row(@"C:\S\b1"), fixture.Row(@"C:\S\r1")];

        Assert.IsTrue(fixture.Owner.ToggleSelectOfRows(bothRowsOfAAndB), "すべてオフならすべてオンです。");
        Assert.IsTrue(fixture.Row(@"C:\S\a1").State.IsSelected && fixture.Row(@"C:\S\b1").State.IsSelected);
        Assert.IsFalse(fixture.Row(@"C:\S\r1").State.IsSelected, "統合不可は変えません。");

        Assert.IsTrue(fixture.Owner.ToggleSelectOfRows(bothRowsOfAAndB), "すべてオンならすべてオフです。");
        Assert.IsFalse(fixture.Row(@"C:\S\a1").State.IsSelected || fixture.Row(@"C:\S\b1").State.IsSelected);

        fixture.Owner.ToggleSelect(fixture.Row(@"C:\S\a1"));
        Assert.IsTrue(fixture.Owner.ToggleSelectOfRows(bothRowsOfAAndB), "混在はすべてオンです。");
        Assert.IsTrue(fixture.Row(@"C:\S\a2").State.IsSelected && fixture.Row(@"C:\S\b1").State.IsSelected);

        Assert.IsFalse(fixture.Owner.ToggleSelectOfRows([fixture.Row(@"C:\S\r1"), fixture.Row(@"C:\S\r2")]), "統合不可だけなら何も変わりません。");
    }

    /// <summary>
    /// 保証：右クリックメニューの「チェックを入れる」「チェックを外す」は選択行のグループごとに一度作用し、統合不可は変えない。
    /// ツールバーの「表示中をすべて選択」「表示中をすべて解除」は検索で表示中のグループだけに作用し、非表示のグループのSELECTは変えず、統合不可は選択しない。
    /// 検出する誤り：非表示のグループの巻込み、統合不可の選択、同じグループへの重複作用。
    /// </summary>
    [TestMethod]
    public void MenuAndToolbar_ActOnSelectedRowsAndVisibleGroupsOnly()
    {
        var fixture = new Fixture();
        DuplicateGroup a = Group(@"C:\S\a1", @"C:\S\a2"), b = Group(@"C:\S\b1"), blocked = Group(@"C:\S\r1", @"C:\S\r2");
        fixture.Source.RegisteredRoots.UnionWith([@"C:\S\r1", @"C:\S\r2"]);
        fixture.Adopt(a, b, blocked);

        Assert.IsTrue(fixture.Owner.SetSelectOfRows([fixture.Row(@"C:\S\a1"), fixture.Row(@"C:\S\a2"), fixture.Row(@"C:\S\r1")], selected: true));
        Assert.IsTrue(fixture.Row(@"C:\S\a1").State.IsSelected);
        Assert.IsFalse(fixture.Row(@"C:\S\r1").State.IsSelected, "統合不可は変えません。");
        Assert.IsFalse(fixture.Owner.SetSelectOfRows([fixture.Row(@"C:\S\a2")], selected: true), "すでにオンなら変わりません。");
        Assert.IsTrue(fixture.Owner.SetSelectOfRows([fixture.Row(@"C:\S\a1")], selected: false));
        Assert.IsFalse(fixture.Row(@"C:\S\a2").State.IsSelected);

        fixture.Owner.ToggleSelect(fixture.Row(@"C:\S\b1"));
        fixture.Owner.SearchText = "a1";
        Assert.IsTrue(fixture.Owner.SelectVisible());
        Assert.IsTrue(fixture.Row(@"C:\S\a1").State.IsSelected);
        fixture.Owner.SearchText = string.Empty;
        Assert.IsTrue(fixture.Row(@"C:\S\b1").State.IsSelected, "非表示のグループのSELECTは変えません。");

        fixture.Owner.SearchText = "folder:r1";
        Assert.IsFalse(fixture.Owner.SelectVisible(), "統合不可は選択しません。");
        fixture.Owner.SearchText = "a1";
        Assert.IsTrue(fixture.Owner.ClearVisible());
        fixture.Owner.SearchText = string.Empty;
        Assert.IsFalse(fixture.Row(@"C:\S\a1").State.IsSelected);
        Assert.IsTrue(fixture.Row(@"C:\S\b1").State.IsSelected, "非表示のグループの解除はしません。");
    }

    /// <summary>
    /// 保証：優先統合先は、確定すると一覧の全グループ（非表示を含む）へ適用し、登録したどれかの優先先と同じかその配下にあるフォルダがちょうど一つならKEEPにする。
    /// 該当なし・二つ以上（別々の優先先に一つずつ当たる場合を含む）・KEEPが固定のグループは変えない。判定はパス成分の境界で行い、大文字小文字と末尾の区切りを無視する。
    /// 登録内容は保持し（ライブラリの交換後も残る）、確定後に現れた新しいインスタンスへは自動で当てない。
    /// 検出する誤り：接頭辞だけの一致（C:\a と C:\ab）、複数該当での上書き、固定の無視、非表示のグループの対象外、登録内容の消失。
    /// </summary>
    [TestMethod]
    public void PreferredDestinations_ApplyToExactlyOneMatchingFolderOnComponentBoundaries()
    {
        var fixture = new Fixture();
        // 既定のKEEPが優先先の判定結果と食い違うよう、直下の譜面数で既定を決めておく。
        DuplicateGroup boundary = Group((@"C:\a\one", 1), (@"C:\ab\two", 2));
        DuplicateGroup twoInSame = Group((@"C:\pref\x", 1), (@"C:\pref\y\deep", 1), (@"D:\other", 2));
        DuplicateGroup twoInSeparate = Group((@"C:\p1\x", 1), (@"C:\p2\x", 2));
        DuplicateGroup none = Group((@"E:\a", 1), (@"E:\b", 2));
        DuplicateGroup hidden = Group((@"C:\PREF\hidden\z", 1), (@"F:\f", 2));
        DuplicateGroup fixedKeep = Group((@"C:\pref\r1", 3), (@"G:\g", 1));
        DuplicateGroup driveRoot = Group((@"X:\a\one", 1), (@"Y:\b", 2));
        fixture.Source.RegisteredRoots.Add(@"G:\g");
        fixture.Adopt(boundary, twoInSame, twoInSeparate, none, hidden, fixedKeep, driveRoot);
        string Keep(DuplicateGroup group)
        {
            Assert.IsTrue(fixture.Owner.TryGetState(group, out DuplicateMergeCandidateGroupState? state));
            return state!.KeepFolder;
        }

        Assert.AreEqual(@"C:\ab\two", Keep(boundary), "前提：既定のKEEPは譜面数が多い方です。");
        fixture.Owner.SearchText = "one";

        fixture.Owner.ApplyPreferredDestinations([@"c:\A\", @"C:\pref", @"C:\p1", @"C:\p2", @"X:\"]);

        Assert.AreEqual(@"C:\a\one", Keep(boundary), @"C:\a は C:\ab に当たらず、C:\a の配下の一つだけに当たります。");
        Assert.AreEqual(@"D:\other", Keep(twoInSame), "同じ優先先に二つ当たる場合は変えません。");
        Assert.AreEqual(@"C:\p2\x", Keep(twoInSeparate), "別々の優先先へ一つずつ当たる場合は変えません。");
        Assert.AreEqual(@"E:\b", Keep(none), "該当なしは変えません。");
        Assert.AreEqual(@"C:\PREF\hidden\z", Keep(hidden), "非表示のグループにも適用します。大文字小文字を無視します。");
        Assert.AreEqual(@"G:\g", Keep(fixedKeep), "KEEPが固定のグループは変えません。");
        Assert.AreEqual(@"X:\a\one", Keep(driveRoot), "ドライブのルートの優先先は、そのドライブ配下に当たり、別のドライブには当たりません。");
        string[] expectedRegistered = [@"c:\A\", @"C:\pref", @"C:\p1", @"C:\p2", @"X:\"];
        CollectionAssert.AreEqual(expectedRegistered, fixture.Owner.PreferredDestinations.ToArray());

        fixture.Library = new object();
        fixture.Owner.AttachLibrary(fixture.Library);
        CollectionAssert.AreEqual(expectedRegistered, fixture.Owner.PreferredDestinations.ToArray(), "ライブラリの交換後も残ります。");
        fixture.Owner.SetTreeSelected(true);
        DuplicateGroup newcomer = Group((@"C:\pref\new", 1), (@"H:\h", 2));
        fixture.Adopt(newcomer);
        Assert.AreEqual(@"H:\h", Keep(newcomer), "確定後に現れた新しいインスタンスへは自動で当てません（既定のまま）。");
    }

    /// <summary>
    /// 保証：状態は <see cref="DuplicateGroup"/> のインスタンスに対応付ける。再検索で同じインスタンスが維持されたグループはSELECT・KEEP・確認結果を保ち、
    /// 同じフォルダ構成でも新しいインスタンスは初期値になり、消えたグループの状態は破棄する。ライブラリの交換ですべて破棄し、旧ライブラリの構築は採用しない。
    /// 並べ替えと検索語は再検索で結果が変わっても保ち、新しい結果へ当て直す。
    /// 検出する誤り：フォルダ構成での照合、消えたグループの状態の残留、交換後の旧状態の復活、並べ替えの消失。
    /// </summary>
    [TestMethod]
    public void StateLifetime_FollowsInstancesAndLibraryAndKeepsViewSettings()
    {
        var fixture = new Fixture();
        DuplicateGroup kept = Group((@"C:\S\k1", 1), (@"C:\S\k2", 2));
        DuplicateGroup vanished = Group(@"C:\S\v1", @"C:\S\v2");
        DuplicateGroup replaced = Group(@"C:\S\r1", @"C:\S\r2");
        fixture.Adopt(kept, vanished, replaced);
        fixture.Owner.ToggleSelect(fixture.Row(@"C:\S\k1"));
        fixture.Owner.SetKeep(fixture.Row(@"C:\S\k1"));
        fixture.Owner.ToggleSelect(fixture.Row(@"C:\S\r1"));
        var verification = new DuplicateMergeVerificationResult(DuplicateMergeVerificationStage.Safe, []);
        Assert.IsTrue(fixture.Owner.TryWriteVerification(kept, verification));
        fixture.Owner.RequestSort("Path", ListSortDirection.Descending);
        fixture.Owner.SearchText = "S";
        DuplicateMergeCandidateGroupState keptState = fixture.Row(@"C:\S\k1").State;
        DuplicateGroup replacement = Group(@"C:\S\r1", @"C:\S\r2");

        fixture.Adopt(kept, replacement);

        Assert.AreSame(keptState, fixture.Row(@"C:\S\k1").State, "同じインスタンスは状態を保ちます。");
        Assert.IsTrue(keptState.IsSelected);
        Assert.AreEqual(@"C:\S\k1", keptState.KeepFolder);
        Assert.AreSame(verification, keptState.Verification);
        Assert.IsFalse(fixture.Owner.TryGetState(vanished, out _), "消えたグループの状態は破棄します。");
        Assert.IsFalse(fixture.Owner.TryGetState(replaced, out _));
        Assert.IsFalse(fixture.Owner.TryWriteVerification(vanished, verification), "現在の状態表になければ捨てます。");
        Assert.IsTrue(fixture.Owner.TryGetState(replacement, out DuplicateMergeCandidateGroupState? fresh));
        Assert.IsFalse(fresh!.IsSelected, "同じフォルダ構成でも新しいインスタンスは初期値です。");
        Assert.AreEqual(("Path", ListSortDirection.Descending, "S"), (fixture.Owner.SortColumnName, fixture.Owner.SortDirection, fixture.Owner.SearchText));
        CollectionAssert.AreEqual(new object[] { kept, replacement }.Reverse().ToArray(), fixture.Rows.Select(row => (object)row.State.Group).Distinct().ToArray(),
            "保った並べ替えを新しい結果へ当て直します。");

        DuplicateMergeCandidateListPreparation stale = Fixture.PrepareOffThread(
            () => DuplicateMergeCandidateListOwner.Prepare(fixture.Library, [kept], fixture.Source, CancellationToken.None));
        fixture.Owner.AttachLibrary(new object());
        Assert.IsFalse(fixture.Owner.Adopt(stale), "旧ライブラリの構築は採用しません。");
        Assert.IsFalse(fixture.Owner.TryGetState(kept, out _), "ライブラリの交換ですべて破棄します。");
        Assert.AreEqual(0, fixture.Owner.RowSet.Count);
        Assert.IsFalse(fixture.Owner.IsViewActive);
    }

    /// <summary>
    /// 保証：一覧は、重複ツリーで選択され、その画面の一覧を採用した後にだけ表示中になる。他の画面へ移るとただちに非表示になり、選び直した直後は新しい一覧を採用するまで表示しない。
    /// 検出する誤り：選択前の表示、別画面へ移った後の表示の残り、再選択直後の古い一覧の表示。
    /// </summary>
    [TestMethod]
    public void ViewActivity_FollowsTreeSelectionAndAdoption()
    {
        var fixture = new Fixture();
        Assert.IsFalse(fixture.Owner.IsViewActive, "採用前は表示しません。");
        DuplicateGroup group = Group(@"C:\S\a", @"C:\S\b");
        fixture.Adopt(group);
        Assert.IsTrue(fixture.Owner.IsViewActive);

        fixture.Owner.SetTreeSelected(false);
        Assert.IsFalse(fixture.Owner.IsViewActive);
        fixture.Owner.SetTreeSelected(true);
        Assert.IsFalse(fixture.Owner.IsViewActive, "再選択直後は、新しい一覧を採用するまで表示しません。");
        fixture.Adopt(group);
        Assert.IsTrue(fixture.Owner.IsViewActive);
        Assert.AreEqual(2, fixture.Owner.Rows.Count, "別の画面へ移って戻っても状態を保ちます。");
    }

    /// <summary>
    /// 保証：後続の単位が使う窓口は、表示中でSELECTがオンのグループを、グループ・PATH昇順のフォルダ・KEEP・固定の有無を持つ変更不能な対象として表示順に捕捉し、
    /// 捕捉後の状態の変更に影響されない。確認結果は段階と理由の一覧を持て、書き込んだグループの統合不可はSELECTをオフにする。
    /// 検出する誤り：非表示のグループの捕捉、可変な共有の漏洩、フォルダの昇順の欠落、統合不可のSELECTの残留。
    /// </summary>
    [TestMethod]
    public void Capture_ReturnsImmutableSelectedVisibleTargetsAndWritesVerification()
    {
        var fixture = new Fixture();
        DuplicateGroup a = Group((@"C:\S\a2", 1), (@"C:\S\a1", 2)), b = Group(@"C:\S\b1", @"C:\S\b2"), hidden = Group(@"C:\S\h1");
        fixture.Adopt(a, b, hidden);
        fixture.Owner.ToggleSelect(fixture.Row(@"C:\S\a1"));
        fixture.Owner.ToggleSelect(fixture.Row(@"C:\S\b1"));
        fixture.Owner.ToggleSelect(fixture.Row(@"C:\S\h1"));
        fixture.Owner.SearchText = "-folder:h1";

        IReadOnlyList<DuplicateMergeCandidateGroupTarget> captured = fixture.Owner.CaptureSelectedGroups();

        Assert.AreEqual(2, captured.Count, "非表示のグループは捕捉しません。");
        Assert.AreSame(a, captured[0].Group);
        CollectionAssert.AreEqual(new[] { @"C:\S\a1", @"C:\S\a2" }, captured[0].Folders.ToArray());
        Assert.AreEqual(@"C:\S\a1", captured[0].KeepFolder);
        Assert.IsFalse(captured[0].KeepFixed);
        fixture.Owner.SetKeep(fixture.Row(@"C:\S\a2"));
        fixture.Owner.ToggleSelect(fixture.Row(@"C:\S\a1"));
        Assert.AreEqual(@"C:\S\a1", captured[0].KeepFolder, "捕捉後の変更は影響しません。");

        var unmergeable = new DuplicateMergeVerificationResult(
            DuplicateMergeVerificationStage.Unmergeable,
            [new DuplicateMergeVerificationReason(DuplicateMergeVerificationReasonKind.RegisteredRoot, [@"C:\S\b1"])]);
        fixture.Owner.ToggleSelect(fixture.Row(@"C:\S\b1"));
        Assert.IsTrue(fixture.Owner.TryWriteVerification(b, unmergeable));
        Assert.IsFalse(fixture.Row(@"C:\S\b1").State.IsSelected, "統合不可を書き込んだグループのSELECTはオフです。");
        Assert.IsFalse(fixture.Owner.ToggleSelect(fixture.Row(@"C:\S\b1")));
        Assert.AreSame(unmergeable, fixture.Row(@"C:\S\b2").State.Verification);
    }

    /// <summary>
    /// 保証：並べ替え・検索・SELECT・KEEP・優先統合先の操作では、TITLE・ARTISTの解決も登録範囲の捕捉も呼ばない。
    /// 検出する誤り：操作のたびにライブラリへ問い合わせる（表示の遅延、ロックの長期保持）。
    /// </summary>
    [TestMethod]
    public void Operations_DoNotResolveOrCaptureAgain()
    {
        var fixture = new Fixture();
        DuplicateGroup a = Group(@"C:\S\a1", @"C:\S\a2"), b = Group(@"C:\S\b1");
        fixture.Adopt(a, b);

        fixture.Owner.RequestSort("Title", ListSortDirection.Descending);
        fixture.Owner.SearchText = "a";
        fixture.Owner.ToggleSelect(fixture.Rows[0]);
        fixture.Owner.SetKeep(fixture.Rows[1]);
        fixture.Owner.SelectVisible();
        fixture.Owner.ApplyPreferredDestinations([@"C:\S\a2"]);
        fixture.Owner.SearchText = string.Empty;

        Assert.AreEqual((1, 1), (fixture.Source.ResolveCalls, fixture.Source.CaptureCalls));
    }

    /// <summary>
    /// 保証：「エクスプローラーで開く」は、存在する選択フォルダごとに一度だけ開き、存在しないフォルダは開かず例外も出さない。
    /// 検出する誤り：同じフォルダを複数回開く、存在しないフォルダを開く。
    /// </summary>
    [TestMethod]
    public void OpenInExplorer_OpensEachExistingSelectedFolderOnce()
    {
        var fixture = new Fixture();
        DuplicateGroup a = Group(@"C:\S\a1", @"C:\S\a2");
        fixture.Adopt(a);
        fixture.ExistingFolders.Add(@"C:\S\a1");

        fixture.Owner.OpenInExplorer([fixture.Row(@"C:\S\a1"), fixture.Row(@"C:\S\a1"), fixture.Row(@"C:\S\a2")]);

        CollectionAssert.AreEqual(new[] { @"C:\S\a1" }, fixture.Opened);
    }
}
