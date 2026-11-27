using System.ComponentModel;
using BeMusicSeeker.Models;
using BeMusicSeeker.Models.BmsLibraryInternal;
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

    /// <summary>登録ルートによる統合不可のSTATUSの文言を、現在の表示言語のリソースから作ります。</summary>
    private static string UnmergeableRegisteredRootStatus
        => string.Format(Resources.Duplicate_merge_candidate_status_reasons_format, Resources.Duplicate_merge_candidate_status_unmergeable, Resources.Duplicate_merge_candidate_reason_registered_root);

    private static string WithMismatch(string status, params string[] notes)
        => string.Format(Resources.Duplicate_merge_candidate_status_note_format, status, string.Join(", ", notes));

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

        /// <summary>管理主体が読むたびに取得する上書きの設定です。既定はアプリの既定（スマート上書きだけがオン）です。</summary>
        internal DuplicateMergeOverwriteSettings Overwrite { get; set; } = new(SmartOverwrite: true, KeepProtectedByRenaming: false);

        internal Fixture()
        {
            Owner = new DuplicateMergeCandidateListOwner(path => ExistingFolders.Contains(path), Opened.Add, () => Overwrite);
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
    /// STATUSを「統合不可：登録ルート」、SELECTをオフかつ無効にする。一フォルダのグループはKEEP固定。
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
            expectedUnmergeable ? UnmergeableRegisteredRootStatus : Resources.Duplicate_merge_candidate_status_unverified,
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
    /// 限界：既定の表示言語（日本語）では「未確認」が「統合不可：登録ルート」より前で、段階の宣言順と同じ向きのため、文言ではなく段階で並べる誤りは区別しない。
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
        string titleNote = Resources.Duplicate_merge_candidate_title_mismatch;
        string artistNote = Resources.Duplicate_merge_candidate_artist_mismatch;
        string blocked = WithMismatch(UnmergeableRegisteredRootStatus, titleNote, artistNote);
        Assert.AreEqual(blocked, fixture.Row(@"C:\S\Z\aa").StatusText, "登録ルートの理由と両方の付記が残ります。");
        if (expectedOrder == null)
        {
            string plain = Resources.Duplicate_merge_candidate_status_unverified;
            string[] statuses = [WithMismatch(plain, titleNote, artistNote), plain, blocked, plain];
            int[] indices = [0, 1, 2, 3];
            expectedOrder = descending
                ? [.. indices.OrderByDescending(i => statuses[i], StringComparer.OrdinalIgnoreCase)]
                : [.. indices.OrderBy(i => statuses[i], StringComparer.OrdinalIgnoreCase)];
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
    /// 保証：状態は <see cref="DuplicateGroup"/> のインスタンスに対応付ける。再検索で同じインスタンスが維持されたグループは、登録範囲の集合が変わらなければSELECT・KEEP・確認結果を保ち、
    /// 同じフォルダ構成でも新しいインスタンスは初期値になり、消えたグループの状態は破棄する。ライブラリの交換ですべて破棄し、旧ライブラリの構築は採用しない。
    /// 並べ替え・検索語・除外語は再検索で結果が変わってもライブラリを交換しても保ち、新しい結果へ当て直す（新しいインスタンスも除外の語で非表示になる）。
    /// 検出する誤り：フォルダ構成での照合、消えたグループの状態の残留、交換後の旧状態の復活、並べ替えの消失、除外語の消失・新しい結果への当て直しの漏れ。
    /// </summary>
    [TestMethod]
    public void StateLifetime_FollowsInstancesAndLibraryAndKeepsViewSettings()
    {
        var fixture = new Fixture();
        DuplicateGroup kept = Group((@"C:\S\k1", 1), (@"C:\S\k2", 2));
        DuplicateGroup vanished = Group(@"C:\S\v1", @"C:\S\v2");
        DuplicateGroup replaced = Group(@"C:\S\r1", @"C:\S\r2");
        DuplicateGroup excludedOld = Group(@"C:\S\x1", @"C:\S\x2");
        fixture.Adopt(kept, vanished, replaced, excludedOld);
        fixture.Owner.ToggleSelect(fixture.Row(@"C:\S\k1"));
        fixture.Owner.SetKeep(fixture.Row(@"C:\S\k1"));
        fixture.Owner.ToggleSelect(fixture.Row(@"C:\S\r1"));
        var verification = new DuplicateMergeVerificationResult(DuplicateMergeVerificationStage.Safe, []);
        Assert.AreEqual(1, fixture.Owner.WriteVerifications([new DuplicateMergeVerificationWrite(kept, verification)]).Count);
        Assert.IsFalse(fixture.Row(@"C:\S\k1").State.IsSelected, "前提：確認結果の書込みでSELECTはオフになります。");
        fixture.Owner.ToggleSelect(fixture.Row(@"C:\S\k1"));
        fixture.Owner.RequestSort("Path", ListSortDirection.Descending);
        fixture.Owner.SearchText = "S";
        fixture.Owner.ExcludeText = "x";
        Assert.AreEqual(3, fixture.Owner.VisibleGroupCount, "前提：除外の語に当たる行を持つグループは非表示です。");
        DuplicateMergeCandidateGroupState keptState = fixture.Row(@"C:\S\k1").State;
        DuplicateGroup replacement = Group(@"C:\S\r1", @"C:\S\r2");
        DuplicateGroup excludedNew = Group(@"C:\S\x1", @"C:\S\x2");

        fixture.Adopt(kept, replacement, excludedNew);

        Assert.AreSame(keptState, fixture.Row(@"C:\S\k1").State, "同じインスタンスは状態を保ちます。");
        Assert.IsTrue(keptState.IsSelected);
        Assert.AreEqual(@"C:\S\k1", keptState.KeepFolder);
        Assert.AreSame(verification, keptState.Verification);
        Assert.IsFalse(fixture.Owner.TryGetState(vanished, out _), "消えたグループの状態は破棄します。");
        Assert.IsFalse(fixture.Owner.TryGetState(replaced, out _));
        Assert.AreEqual(0, fixture.Owner.WriteVerifications([new DuplicateMergeVerificationWrite(vanished, verification)]).Count, "現在の状態表になければ捨てます。");
        Assert.IsTrue(fixture.Owner.TryGetState(replacement, out DuplicateMergeCandidateGroupState? fresh));
        Assert.IsFalse(fresh!.IsSelected, "同じフォルダ構成でも新しいインスタンスは初期値です。");
        Assert.AreEqual(
            ("Path", ListSortDirection.Descending, "S", "x"),
            (fixture.Owner.SortColumnName, fixture.Owner.SortDirection, fixture.Owner.SearchText, fixture.Owner.ExcludeText));
        CollectionAssert.AreEqual(new object[] { kept, replacement }.Reverse().ToArray(), fixture.Rows.Select(row => (object)row.State.Group).Distinct().ToArray(),
            "保った並べ替えと除外語を新しい結果へ当て直します（新しいインスタンスのx1・x2も非表示）。");
        Assert.IsTrue(fixture.Owner.TryGetState(excludedNew, out _), "除外で非表示でも、状態表には持ちます。");
        Assert.AreEqual(2, fixture.Owner.VisibleGroupCount);

        DuplicateMergeCandidateListPreparation stale = Fixture.PrepareOffThread(
            () => DuplicateMergeCandidateListOwner.Prepare(fixture.Library, [kept], fixture.Source, CancellationToken.None));
        fixture.Owner.AttachLibrary(new object());
        Assert.IsFalse(fixture.Owner.Adopt(stale), "旧ライブラリの構築は採用しません。");
        Assert.IsFalse(fixture.Owner.TryGetState(kept, out _), "ライブラリの交換ですべて破棄します。");
        Assert.AreEqual(0, fixture.Owner.RowSet.Count);
        Assert.IsFalse(fixture.Owner.IsViewActive);
        Assert.AreEqual(("S", "x"), (fixture.Owner.SearchText, fixture.Owner.ExcludeText), "検索語・除外語はライブラリの交換後も保ちます。");
    }

    /// <summary>
    /// 保証：同じインスタンスのグループでも、構築時に捕捉した登録範囲のフォルダの集合が変わると、採用時に利用者の状態（SELECT・KEEP・確認結果・走査の事実）を捨てて
    /// 新しい初期状態にする。登録範囲が一つならKEEPをそこへ固定し、二つ以上ならPATH昇順の先頭へ固定して「統合不可：登録ルート」・SELECT無効にし、
    /// なくなれば固定を解いて初期のKEEP（直下の所持譜面数が最多）に戻す。判定はグループごとで、同じ採用の中で登録範囲が変わらないグループは同じ状態を保つ。
    /// 検出する誤り：新しいインスタンスにしか初期値を当てない、KEEPだけ更新してSELECT・確認結果・固定・走査の事実を残す、件数だけで集合を比べる、
    /// 全グループを初期化する、固定が解けたKEEPをPATH昇順の先頭にする。
    /// </summary>
    [TestMethod]
    [DataRow("", "a", "a", true, true)]
    [DataRow("", "c,b", "b", true, false)]
    [DataRow("a", "", "c", false, true)]
    [DataRow("a,b", "", "c", false, true)]
    [DataRow("a", "c", "c", true, true)]
    [DataRow("a,b", "b", "b", true, true)]
    [DataRow("b", "a,b", "a", true, false)]
    public void StateLifetime_RegisteredRangeChangeAdoptsNewInitialStateAndUnchangedRangeKeepsState(
        string before, string after, string expectedKeep, bool expectedFixed, bool expectedSelectable)
    {
        static string[] Folders(string names)
            => [.. names.Split(',', StringSplitOptions.RemoveEmptyEntries).Select(name => @"C:\S\" + name)];
        var fixture = new Fixture();
        DuplicateGroup changed = Group((@"C:\S\a", 1), (@"C:\S\b", 1), (@"C:\S\c", 3));
        DuplicateGroup unchanged = Group(@"C:\S\u1", @"C:\S\u2");
        fixture.Source.RegisteredRoots.Add(@"C:\S\u1");
        fixture.Source.RegisteredRoots.UnionWith(Folders(before));
        fixture.Adopt(changed, unchanged);
        // 変更後の初期値と食い違う利用者の状態を、変更前の登録範囲が許す範囲で作ります。
        fixture.Owner.SetKeep(fixture.Row(@"C:\S\b"));
        var safe = new DuplicateMergeVerificationResult(DuplicateMergeVerificationStage.Safe, []);
        var inspection = new DuplicateMergeGroupInspection(
            new Dictionary<DuplicateMergeVerificationReasonKind, IReadOnlyList<DuplicateMergeFinding>>(),
            [],
            DuplicateMergeComparisonSummary.None,
            new Dictionary<string, DuplicateMergeKeepDependentFacts>(StringComparer.OrdinalIgnoreCase));
        if (fixture.Row(@"C:\S\a").State.CanSelect)
        {
            fixture.Owner.WriteVerifications([new DuplicateMergeVerificationWrite(changed, safe, inspection)]);
        }
        fixture.Owner.WriteVerifications([new DuplicateMergeVerificationWrite(unchanged, safe)]);
        DuplicateMergeCandidateGroupState unchangedState = fixture.Row(@"C:\S\u1").State;
        Assert.IsFalse(unchangedState.IsSelected, "前提：確認結果の書込みでSELECTはオフになります。");
        // 書込みの後に利用者がオンにした状態を作り、登録範囲の変更が捨てる状態と保つ状態を区別する。
        fixture.Owner.ToggleSelect(fixture.Row(@"C:\S\u1"));
        if (fixture.Row(@"C:\S\a").State.CanSelect) { fixture.Owner.ToggleSelect(fixture.Row(@"C:\S\a")); }
        Assert.IsTrue(unchangedState.IsSelected);
        fixture.Source.RegisteredRoots.ExceptWith(Folders(before));
        fixture.Source.RegisteredRoots.UnionWith(Folders(after));

        fixture.Adopt(changed, unchanged);

        DuplicateMergeCandidateGroupState state = fixture.Row(@"C:\S\a").State;
        Assert.AreEqual(@"C:\S\" + expectedKeep, state.KeepFolder);
        Assert.AreEqual(expectedFixed, state.KeepFixed);
        Assert.IsFalse(state.IsSelected, "登録範囲が変わったグループのSELECTは初期値のオフです。");
        Assert.AreEqual(
            expectedSelectable ? Resources.Duplicate_merge_candidate_status_unverified : UnmergeableRegisteredRootStatus,
            fixture.Row(@"C:\S\a").StatusText);
        DuplicateMergeCandidateGroupTarget target = fixture.Owner.CaptureGroupsOfRows([fixture.Row(@"C:\S\a")]).Single();
        CollectionAssert.AreEqual(Folders(after).Order(StringComparer.OrdinalIgnoreCase).ToArray(), target.RegisteredFolders.ToArray());
        Assert.AreEqual(expectedFixed, target.KeepFixed);
        DuplicateMergeCandidateRow other = fixture.Rows.First(row => ReferenceEquals(row.State, state) && !row.IsKeep);
        Assert.AreEqual(!expectedFixed, fixture.Owner.SetKeep(other), "固定のKEEPは変えられず、固定が解ければ変えられます。");
        if (expectedFixed) { Assert.AreEqual(@"C:\S\" + expectedKeep, state.KeepFolder); }
        Assert.AreEqual(
            expectedSelectable ? Resources.Duplicate_merge_candidate_status_unverified : UnmergeableRegisteredRootStatus,
            fixture.Row(@"C:\S\a").StatusText, "古い走査の事実から確認結果を導き直しません。");

        Assert.AreSame(unchangedState, fixture.Row(@"C:\S\u1").State, "登録範囲が変わらないグループは同じ状態を保ちます。");
        Assert.IsTrue(unchangedState.IsSelected);
        Assert.AreEqual(@"C:\S\u1", unchangedState.KeepFolder);
        Assert.AreSame(safe, unchangedState.Verification);
        Assert.AreEqual(1, fixture.Owner.SelectedGroupCount);
        CollectionAssert.AreEqual(new[] { unchanged }, fixture.Owner.CaptureSelectedGroups().Select(item => item.Group).ToArray());
        Assert.AreEqual(expectedSelectable, fixture.Owner.ToggleSelect(fixture.Row(@"C:\S\a")), "統合不可はオンにできません。");
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
            [new DuplicateMergeVerificationReason(DuplicateMergeVerificationReasonKind.RegisteredRoot, [new DuplicateMergeFinding(@"C:\S\b1", string.Empty)])]);
        fixture.Owner.ToggleSelect(fixture.Row(@"C:\S\b1"));
        Assert.AreEqual(1, fixture.Owner.WriteVerifications([new DuplicateMergeVerificationWrite(b, unmergeable)]).Count);
        Assert.IsFalse(fixture.Row(@"C:\S\b1").State.IsSelected, "統合不可を書き込んだグループのSELECTはオフです。");
        Assert.IsFalse(fixture.Owner.ToggleSelect(fixture.Row(@"C:\S\b1")));
        Assert.AreSame(unmergeable, fixture.Row(@"C:\S\b2").State.Verification);
    }

    /// <summary>
    /// 保証：新しい重複結果にないグループ（整理で解消したグループ）の状態と行は、一覧が別の画面で隠れている間も取り除き、結果が空なら行は0件になる。
    /// 結果に残るグループは同じ状態のインスタンスのまま、SELECT・KEEP・確認結果を保つ。解消したグループがなければ何も変えない。
    /// 検出する誤り：解消したグループの行の残り（結果が0件のときの古い行を含む）、残るグループの状態の消失・置換、何も解消していないときの行集合の入替え。
    /// </summary>
    [TestMethod]
    public void RemoveUnresolvedGroups_DropsResolvedGroupsWhileHiddenAndKeepsTheRest()
    {
        var fixture = new Fixture();
        DuplicateGroup resolved = Group(@"C:\S\a1", @"C:\S\a2"), surviving = Group(@"C:\S\b1", @"C:\S\b2");
        fixture.Adopt(resolved, surviving);
        var caution = new DuplicateMergeVerificationResult(
            DuplicateMergeVerificationStage.Caution,
            [new DuplicateMergeVerificationReason(DuplicateMergeVerificationReasonKind.AudioExtension, [new DuplicateMergeFinding(@"C:\S\b1", "x.wav")])]);
        fixture.Owner.WriteVerifications([new DuplicateMergeVerificationWrite(surviving, caution)]);
        fixture.Owner.SetKeep(fixture.Row(@"C:\S\b2"));
        fixture.Owner.ToggleSelect(fixture.Row(@"C:\S\a1"));
        DuplicateMergeCandidateGroupState survivingState = fixture.Row(@"C:\S\b1").State;
        DuplicateMergeCandidateRowSet unchanged = fixture.Owner.RowSet;
        fixture.Owner.RemoveUnresolvedGroups([resolved, surviving]);
        Assert.AreSame(unchanged, fixture.Owner.RowSet, "解消したグループがなければ、行集合を入れ替えません。");

        fixture.Owner.SetTreeSelected(false);
        fixture.Owner.RemoveUnresolvedGroups([surviving]);

        CollectionAssert.AreEqual(new[] { @"C:\S\b1", @"C:\S\b2" }, fixture.Paths, "別の画面で隠れていても、解消したグループの行を取り除きます。");
        Assert.AreSame(survivingState, fixture.Rows[0].State, "残るグループは同じ状態のインスタンスです。");
        Assert.AreEqual(@"C:\S\b2", survivingState.KeepFolder);
        Assert.AreSame(caution, survivingState.Verification);
        Assert.IsFalse(fixture.Owner.TryGetState(resolved, out _), "解消したグループの状態を破棄します。");
        Assert.AreEqual((1, 0), (fixture.Owner.VisibleGroupCount, fixture.Owner.SelectedGroupCount), "件数は残るグループだけを数えます。");

        fixture.Owner.RemoveUnresolvedGroups([]);

        Assert.AreEqual(0, fixture.Owner.RowSet.Count, "結果が空なら、行は0件になります。");
        Assert.AreEqual(0, fixture.Owner.VisibleGroupCount);
        Assert.IsFalse(fixture.Owner.TryGetState(surviving, out _));
    }

    /// <summary>
    /// 保証：後続の単位（一括整理）が使う捕捉は、並べ替えと検索を当てた後の表示順で対象を返し、各対象は捕捉した時点の確認結果を持つ。
    /// 捕捉後に確認結果を書き換えても、捕捉した値は変わらない。
    /// 検出する誤り：並べ替え前の結果順での捕捉（一括整理の要求の順序の誤り）、確認結果の欠落（確認文の未確認・要注意の件数の誤り）、捕捉後の状態の漏洩。
    /// </summary>
    [TestMethod]
    public void Capture_FollowsSortedDisplayOrderAndCarriesTheVerificationOfTheMoment()
    {
        var fixture = new Fixture();
        DuplicateGroup first = Group(@"C:\S\c1", @"C:\S\c2"), second = Group(@"C:\S\a1", @"C:\S\a2"), third = Group(@"C:\S\b1", @"C:\S\b2");
        fixture.Adopt(first, second, third);
        fixture.Owner.SelectVisible();
        var caution = new DuplicateMergeVerificationResult(
            DuplicateMergeVerificationStage.Caution,
            [new DuplicateMergeVerificationReason(DuplicateMergeVerificationReasonKind.AudioExtension, [new DuplicateMergeFinding(@"C:\S\a1", "x.wav")])]);

        fixture.Owner.RequestSort("Path", ListSortDirection.Ascending);
        IReadOnlyList<DuplicateMergeCandidateGroupTarget> ascending = fixture.Owner.CaptureSelectedGroups();
        fixture.Owner.RequestSort("Path", ListSortDirection.Descending);
        IReadOnlyList<DuplicateMergeCandidateGroupTarget> descending = fixture.Owner.CaptureSelectedGroups();

        CollectionAssert.AreEqual(new[] { second, third, first }, ascending.Select(target => target.Group).ToArray(), "並べ替えを当てた表示順（昇順）です。");
        CollectionAssert.AreEqual(new[] { first, third, second }, descending.Select(target => target.Group).ToArray(), "並べ替えを当てた表示順（降順）です。");
        Assert.IsTrue(ascending.All(target => ReferenceEquals(target.Verification, DuplicateMergeVerificationResult.Unverified)), "未確認の結果を持ちます。");

        fixture.Owner.WriteVerifications([new DuplicateMergeVerificationWrite(second, caution)]);
        fixture.Owner.SelectVisible();
        Assert.AreSame(caution, fixture.Owner.CaptureSelectedGroups().Single(target => ReferenceEquals(target.Group, second)).Verification);
        Assert.AreSame(DuplicateMergeVerificationResult.Unverified, ascending[0].Verification, "捕捉した値は、後の書込みに影響されません。");
    }

    /// <summary>
    /// 保証：並べ替え・検索・除外・SELECT・KEEP・優先統合先の操作では、TITLE・ARTISTの解決も登録範囲の捕捉も呼ばない。
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
        fixture.Owner.ExcludeText = "b";
        fixture.Owner.ToggleSelect(fixture.Rows[0]);
        fixture.Owner.SetKeep(fixture.Rows[1]);
        fixture.Owner.SelectVisible();
        fixture.Owner.ApplyPreferredDestinations([@"C:\S\a2"]);
        fixture.Owner.SearchText = string.Empty;
        fixture.Owner.ExcludeText = string.Empty;

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

    private static DuplicateMergeVerificationResult Result(
        DuplicateMergeVerificationStage stage, params (DuplicateMergeVerificationReasonKind Kind, int Findings)[] reasons)
        => new(stage, [.. reasons.Select(reason => new DuplicateMergeVerificationReason(
            reason.Kind, [.. Enumerable.Range(0, reason.Findings).Select(index => new DuplicateMergeFinding(@"C:\S\f", "item" + index.ToString("00")))]))]);

    /// <summary>
    /// 保証：複数グループの確認結果は一括の窓口で書き込み、SELECTは段階によらず（安全・要注意・統合不可のどれでも）オフになり、行集合の差し替えは全体で一回になる。
    /// STATUSの文言による検索は書き込み後の一覧に反映し、並べ替えも新しい値で当て直す。現在の状態表にないグループの結果は捨て、書き込んだ結果だけを返す。
    /// 検出する誤り：グループごとの行集合の差し替え（複数回）、検索・並べ替えの当て直しの漏れ、安全のSELECTの残留・自動のオン、消えたグループの書込み。
    /// </summary>
    [TestMethod]
    public void WriteVerifications_ClearsSelectOfEveryStageInOneRowSetReplacementAndReappliesSearchAndSort()
    {
        var fixture = new Fixture();
        DuplicateGroup safe = Group(@"C:\S\a1", @"C:\S\a2"), caution = Group(@"C:\S\b1", @"C:\S\b2"), blocked = Group(@"C:\S\c1", @"C:\S\c2");
        DuplicateGroup vanished = Group(@"C:\S\v1", @"C:\S\v2");
        fixture.Adopt(safe, caution, blocked);
        fixture.Owner.SelectVisible();
        fixture.Owner.SearchText = "status:" + Resources.Duplicate_merge_candidate_status_safe;
        Assert.AreEqual(0, fixture.Rows.Length, "前提：書込み前は、どのグループも「安全」ではありません。");
        fixture.Owner.SearchText = string.Empty;
        fixture.Owner.RequestSort("Select", ListSortDirection.Descending);
        fixture.Owner.SearchText = "status:" + Resources.Duplicate_merge_candidate_status_safe;
        var changed = new List<string?>();
        fixture.Owner.PropertyChanged += (_, args) => changed.Add(args.PropertyName);

        IReadOnlyList<DuplicateMergeVerificationWrite> accepted = fixture.Owner.WriteVerifications(
        [
            new(safe, Result(DuplicateMergeVerificationStage.Safe)),
            new(caution, Result(DuplicateMergeVerificationStage.Caution, (DuplicateMergeVerificationReasonKind.ParentReference, 1))),
            new(blocked, Result(DuplicateMergeVerificationStage.Unmergeable, (DuplicateMergeVerificationReasonKind.Link, 1))),
            new(vanished, Result(DuplicateMergeVerificationStage.Safe))
        ]);

        Assert.AreEqual(3, accepted.Count, "状態表にないグループの結果は捨てます。");
        Assert.AreEqual(1, changed.Count(name => name == nameof(DuplicateMergeCandidateListOwner.Rows)), "行集合の差し替えは全体で一回です。");
        CollectionAssert.AreEqual(new[] { safe }, fixture.Rows.Select(row => row.State.Group).Distinct().ToArray(), "STATUSの検索は書込み後の一覧に反映します。");
        Assert.IsFalse(fixture.Row(@"C:\S\a1").State.IsSelected, "安全のSELECTもオフです。");
        Assert.IsTrue(fixture.Owner.TryGetState(caution, out DuplicateMergeCandidateGroupState? cautionState));
        Assert.IsFalse(cautionState!.IsSelected, "要注意のSELECTはオフです。");
        Assert.IsTrue(fixture.Owner.TryGetState(blocked, out DuplicateMergeCandidateGroupState? blockedState));
        Assert.IsFalse(blockedState!.IsSelected, "統合不可のSELECTはオフです。");
        Assert.AreEqual(0, fixture.Owner.SelectedGroupCount);

        fixture.Owner.SearchText = string.Empty;
        fixture.Owner.RequestSort("Status", ListSortDirection.Ascending);
        string[] statuses = [.. fixture.Rows.Select(row => row.StatusText).Distinct()];
        CollectionAssert.AreEqual(statuses.OrderBy(text => text, StringComparer.OrdinalIgnoreCase).ToArray(), statuses, "並べ替えは新しいSTATUSの値で当て直します。");
    }

    /// <summary>
    /// 保証：検索と並べ替えがSTATUS・SELECTに依存しないときは、書き込んでも行集合を差し替えず、表示の無効化を一回だけ通知し、オンだったグループの選択件数が0になる。
    /// 検出する誤り：不要な行集合の作り直し（選択状態・スクロールの崩れ）、無効化の通知の欠落、選択件数の更新の漏れ（オンのままの表示）。
    /// </summary>
    [TestMethod]
    public void WriteVerifications_WithoutStatusDependentViewOnlyInvalidatesDisplay()
    {
        var fixture = new Fixture();
        DuplicateGroup a = Group(@"C:\S\a1", @"C:\S\a2"), b = Group(@"C:\S\b1", @"C:\S\b2");
        fixture.Adopt(a, b);
        fixture.Owner.SelectVisible();
        Assert.AreEqual(2, fixture.Owner.SelectedGroupCount, "前提：書込みの前は、表示中のグループがすべてオンです。");
        var changed = new List<string?>();
        fixture.Owner.PropertyChanged += (_, args) => changed.Add(args.PropertyName);
        int invalidated = 0;
        fixture.Owner.DisplayInvalidated += (_, _) => invalidated++;

        fixture.Owner.WriteVerifications([new(a, Result(DuplicateMergeVerificationStage.Safe)), new(b, Result(DuplicateMergeVerificationStage.Safe))]);

        Assert.AreEqual(0, changed.Count(name => name == nameof(DuplicateMergeCandidateListOwner.Rows)));
        Assert.AreEqual(1, invalidated);
        Assert.AreEqual(0, fixture.Owner.SelectedGroupCount, "オンだったグループの選択件数が0になります。");
    }

    /// <summary>
    /// 保証：確認で統合不可（解析不能）になったグループを再確認して安全を書き込むと、SELECTはオフのまま操作できる状態（無効でない）に戻り、STATUSは安全になる。
    /// 登録範囲のフォルダを持たないグループで確かめ、登録ルート由来の統合不可（安全で上書きされない）とは区別する。
    /// 検出する誤り：確認結果の代入より前に操作可否を評価して、統合不可だったグループを無効のまま残す、再確認で安全になったグループのSELECTを自動でオンにする、前の確認結果のSTATUSが残る。
    /// </summary>
    [TestMethod]
    public void WriteVerifications_SafeAfterUnreadableReenablesTheGroupWithSelectOff()
    {
        var fixture = new Fixture();
        DuplicateGroup group = Group(@"C:\S\a1", @"C:\S\a2");
        fixture.Adopt(group);
        DuplicateMergeCandidateGroupState state = fixture.Row(@"C:\S\a1").State;
        Assert.IsTrue(state.CanSelect, "前提：登録範囲のフォルダがなく、構築時は統合不可ではありません。");

        Assert.AreEqual(1, fixture.Owner.WriteVerifications(
            [new(group, Result(DuplicateMergeVerificationStage.Unmergeable, (DuplicateMergeVerificationReasonKind.Unreadable, 1)))]).Count);
        Assert.IsFalse(state.IsSelected, "前提：解析不能の統合不可でSELECTはオフです。");
        Assert.IsFalse(state.CanSelect, "前提：解析不能の統合不可でSELECTは無効です。");
        Assert.IsFalse(fixture.Owner.ToggleSelect(fixture.Row(@"C:\S\a1")), "前提：無効のSELECTは操作できません。");

        Assert.AreEqual(1, fixture.Owner.WriteVerifications([new(group, Result(DuplicateMergeVerificationStage.Safe))]).Count);

        Assert.AreEqual(Resources.Duplicate_merge_candidate_status_safe, fixture.Row(@"C:\S\a1").StatusText);
        Assert.IsTrue(state.CanSelect, "再確認で安全になれば、SELECTは有効に戻ります。");
        Assert.IsFalse(state.IsSelected, "再確認で安全になっても、SELECTはオフのままです。");
        Assert.AreEqual(0, fixture.Owner.SelectedGroupCount);
        Assert.IsTrue(fixture.Owner.ToggleSelect(fixture.Row(@"C:\S\a1")), "有効に戻ったSELECTは操作できます。");
        Assert.IsTrue(state.IsSelected);
        Assert.AreEqual(1, fixture.Owner.SelectedGroupCount);
    }

    /// <summary>
    /// 保証：KEEPを変えると、確認済みのグループは走査の事実からSTATUSを導き直し（親ディレクトリ参照を持つフォルダをKEEPにすれば安全へ、持たないフォルダをKEEPにすれば要注意へ）、
    /// SELECTは変えず、並べ替え・検索の当て直しはしない。表示は無効化して再描画する。
    /// 検出する誤り：KEEPの変更後に古いSTATUSが残る、KEEPの変更でSELECTが外れる、利用者操作での行集合の差し替え（並べ直し）。
    /// </summary>
    [TestMethod]
    public void SetKeep_RederivesVerificationFromInspectionWithoutChangingSelectOrRows()
    {
        var fixture = new Fixture();
        DuplicateGroup group = Group((@"C:\S\a", 1), (@"C:\S\b", 1));
        fixture.Adopt(group);
        var inspection = new DuplicateMergeGroupInspection(
            new Dictionary<DuplicateMergeVerificationReasonKind, IReadOnlyList<DuplicateMergeFinding>>(),
            [],
            DuplicateMergeComparisonSummary.None,
            new Dictionary<string, DuplicateMergeKeepDependentFacts>(StringComparer.OrdinalIgnoreCase)
            {
                [@"C:\S\a"] = new([new DuplicateMergeFinding(@"C:\S\a", "c.bms", "../x.wav")], []),
                [@"C:\S\b"] = DuplicateMergeKeepDependentFacts.Empty
            });
        Assert.AreEqual(@"C:\S\a", fixture.Row(@"C:\S\a").State.KeepFolder, "前提：既定のKEEPはPATH昇順の先頭です。");
        fixture.Owner.WriteVerifications([new(group, inspection.Evaluate(@"C:\S\a"), inspection)]);
        Assert.IsFalse(fixture.Row(@"C:\S\a").State.IsSelected, "前提：KEEP側の参照は該当せず安全で、確認の書込みでSELECTはオフです。");
        fixture.Owner.ToggleSelect(fixture.Row(@"C:\S\a"));
        Assert.IsTrue(fixture.Row(@"C:\S\a").State.IsSelected, "前提：確認の後に利用者がSELECTをオンにします。");
        var changed = new List<string?>();
        fixture.Owner.PropertyChanged += (_, args) => changed.Add(args.PropertyName);
        int invalidated = 0;
        fixture.Owner.DisplayInvalidated += (_, _) => invalidated++;

        Assert.IsTrue(fixture.Owner.SetKeep(fixture.Row(@"C:\S\b")));

        DuplicateMergeCandidateGroupState state = fixture.Row(@"C:\S\a").State;
        Assert.AreEqual(DuplicateMergeVerificationStage.Caution, state.Verification.Stage);
        Assert.AreEqual(
            string.Format(Resources.Duplicate_merge_candidate_status_reasons_format, Resources.Duplicate_merge_candidate_status_caution, Resources.Duplicate_merge_candidate_reason_parent_reference),
            fixture.Row(@"C:\S\a").StatusText);
        Assert.IsTrue(state.IsSelected, "KEEPの変更で段階が変わっても、SELECTは変えません。");
        Assert.AreEqual(0, changed.Count(name => name == nameof(DuplicateMergeCandidateListOwner.Rows)), "利用者操作では並べ直しません。");
        Assert.AreEqual(1, invalidated);

        Assert.IsTrue(fixture.Owner.SetKeep(fixture.Row(@"C:\S\a")));
        Assert.AreEqual(DuplicateMergeVerificationStage.Safe, state.Verification.Stage, "元のフォルダをKEEPにすれば安全へ戻ります。");
    }

    /// <summary>
    /// 保証：一括整理で処理したグループのSELECTをオフにする入口は、指定したグループだけをオフにし、表示中かどうかによらず（検索で非表示でも指定すれば）作用し、
    /// 指定しなかったグループ（非表示でオンのものを含む）は変えない。KEEPと確認結果は同じ参照のまま、状態表にないグループは捨てて状態を作らず、
    /// 選択件数の変更を通知する。SELECTで並べ替えていなければ、行集合は差し替えない。
    /// 検出する誤り：指定外のグループのSELECT・KEEP・確認結果の書換え、状態表にないグループの状態の生成、選択件数の通知の欠落、不要な行集合の差し替え。
    /// </summary>
    [TestMethod]
    public void ClearSelectOfGroups_ClearsOnlyTheGivenGroupsAndKeepsKeepAndVerification()
    {
        var fixture = new Fixture();
        DuplicateGroup a = Group(@"C:\S\a1", @"C:\S\a2"), b = Group(@"C:\S\b1", @"C:\S\b2"), hidden = Group(@"C:\S\h1");
        DuplicateGroup vanished = Group(@"C:\S\v1", @"C:\S\v2");
        fixture.Adopt(a, b, hidden);
        DuplicateMergeVerificationResult caution = Result(DuplicateMergeVerificationStage.Caution, (DuplicateMergeVerificationReasonKind.ParentReference, 1));
        fixture.Owner.WriteVerifications([new(a, caution)]);
        fixture.Owner.SetKeep(fixture.Row(@"C:\S\a2"));
        fixture.Owner.SelectVisible();
        fixture.Owner.SearchText = "-folder:h1";
        DuplicateMergeCandidateGroupState aState = fixture.Row(@"C:\S\a1").State, bState = fixture.Row(@"C:\S\b1").State;
        Assert.IsTrue(fixture.Owner.TryGetState(hidden, out DuplicateMergeCandidateGroupState? hiddenState));
        Assert.IsTrue(aState.IsSelected && bState.IsSelected && hiddenState!.IsSelected, "前提：三グループともSELECTがオンです。");
        Assert.AreEqual(2, fixture.Owner.SelectedGroupCount, "前提：非表示のグループは選択件数に数えません。");
        var changed = new List<string?>();
        fixture.Owner.PropertyChanged += (_, args) => changed.Add(args.PropertyName);
        int invalidated = 0;
        fixture.Owner.DisplayInvalidated += (_, _) => invalidated++;
        DuplicateMergeCandidateRowSet rowsBefore = fixture.Owner.RowSet;

        Assert.IsTrue(fixture.Owner.ClearSelectOfGroups([a, vanished]));

        Assert.IsFalse(aState.IsSelected, "指定したグループはオフです。");
        Assert.IsTrue(bState.IsSelected, "指定しなかったグループは変えません。");
        Assert.IsTrue(hiddenState!.IsSelected, "非表示でオンの、指定しなかったグループは変えません。");
        Assert.AreEqual(@"C:\S\a2", aState.KeepFolder, "KEEPは書き換えません。");
        Assert.AreSame(caution, aState.Verification, "確認結果は同じ参照のままです。");
        Assert.IsFalse(fixture.Owner.TryGetState(vanished, out _), "状態表にないグループは捨て、状態を作りません。");
        Assert.AreEqual(1, fixture.Owner.SelectedGroupCount);
        CollectionAssert.Contains(changed, nameof(DuplicateMergeCandidateListOwner.SelectedGroupCount));
        Assert.AreEqual(1, invalidated, "表示の無効化を通知します。");
        Assert.AreSame(rowsBefore, fixture.Owner.RowSet, "SELECTで並べ替えていなければ、行集合を差し替えません。");

        Assert.IsFalse(fixture.Owner.ClearSelectOfGroups([a, vanished]), "すでにオフなら変えず、通知しません。");
        Assert.AreEqual(1, invalidated);
        Assert.IsTrue(fixture.Owner.ClearSelectOfGroups([hidden]), "表示中かどうかによらず、指定したグループをオフにします。");
        Assert.IsFalse(hiddenState.IsSelected);
        Assert.AreEqual(1, fixture.Owner.SelectedGroupCount, "非表示のグループは選択件数に数えません。");
        Assert.AreEqual(@"C:\S\a2", aState.KeepFolder);
    }

    /// <summary>
    /// 保証：SELECTで並べ替えている間は、整理で処理したグループのSELECTをオフにする書込みで、行集合の差し替えが全体で一回になり、新しい値で並び直す。
    /// 検出する誤り：並べ替えの当て直しの漏れ（オフにしたグループが先頭に残る）、グループごとの行集合の差し替え。
    /// </summary>
    [TestMethod]
    public void ClearSelectOfGroups_WhileSortedBySelectReplacesRowSetOnceAndReappliesSort()
    {
        var fixture = new Fixture();
        DuplicateGroup a = Group(@"C:\S\a1", @"C:\S\a2"), b = Group(@"C:\S\b1", @"C:\S\b2"), c = Group(@"C:\S\c1", @"C:\S\c2");
        fixture.Adopt(a, b, c);
        Assert.IsTrue(fixture.Owner.SetSelectOfRows([fixture.Row(@"C:\S\b1")], selected: true));
        fixture.Owner.RequestSort("Select", ListSortDirection.Descending);
        Assert.AreSame(b, fixture.Rows[0].State.Group, "前提：オンのグループが先頭です。");
        var changed = new List<string?>();
        fixture.Owner.PropertyChanged += (_, args) => changed.Add(args.PropertyName);

        Assert.IsTrue(fixture.Owner.ClearSelectOfGroups([a, b, c]));

        Assert.AreEqual(1, changed.Count(name => name == nameof(DuplicateMergeCandidateListOwner.Rows)), "行集合の差し替えは全体で一回です。");
        CollectionAssert.AreEqual(new[] { a, b, c }, fixture.Rows.Select(row => row.State.Group).Distinct().ToArray(), "同値は結果の順を保ちます。");
        Assert.AreEqual(0, fixture.Owner.SelectedGroupCount);
    }

    /// <summary>
    /// 保証：補助情報のないSTATUSの段階の文言は、未確認「未確認」、安全「安全」、要注意・統合不可「（段階）：（理由, 理由…）」で、
    /// 成立した理由をすべて結果の順に、表示言語によらない「, 」で並べる（理由が一つなら一つだけ）。
    /// 統合不可と要注意の理由が混在する結果は、段階は統合不可のまま両方の理由を並べる。補助情報の付記は <see cref="StatusText_AppendsAuxiliaryNotesInOneBracketOnEveryStage"/> が分担する。
    /// 検出する誤り：最初の理由だけの表示・「ほか{n}件」への省略、理由の順の入れ替え、区切りの取り違え、段階と書式の取り違え。
    /// </summary>
    [TestMethod]
    public void StatusText_ListsAllReasonsInResultOrder()
    {
        Assert.AreEqual(Resources.Duplicate_merge_candidate_status_unverified, DuplicateMergeVerificationResult.Unverified.FormatStatus());
        Assert.AreEqual(Resources.Duplicate_merge_candidate_status_safe, Result(DuplicateMergeVerificationStage.Safe).FormatStatus());

        static string Caution(params string[] names)
            => string.Format(Resources.Duplicate_merge_candidate_status_reasons_format, Resources.Duplicate_merge_candidate_status_caution, string.Join(", ", names));
        static string Unmergeable(params string[] names)
            => string.Format(Resources.Duplicate_merge_candidate_status_reasons_format, Resources.Duplicate_merge_candidate_status_unmergeable, string.Join(", ", names));

        DuplicateMergeVerificationResult one = Result(DuplicateMergeVerificationStage.Caution, (DuplicateMergeVerificationReasonKind.AudioExtension, 1));
        Assert.AreEqual(Caution(Resources.Duplicate_merge_candidate_reason_audio_extension), one.FormatStatus(), "理由が一つなら従来と同じ表示です。");
        DuplicateMergeVerificationResult many = Result(
            DuplicateMergeVerificationStage.Caution,
            (DuplicateMergeVerificationReasonKind.ParentReference, 1), (DuplicateMergeVerificationReasonKind.ContentConflict, 2), (DuplicateMergeVerificationReasonKind.NestedChartFolder, 1));
        Assert.AreEqual(
            Caution(
                Resources.Duplicate_merge_candidate_reason_parent_reference,
                Resources.Duplicate_merge_candidate_reason_content_conflict,
                Resources.Duplicate_merge_candidate_reason_nested_chart_folder),
            many.FormatStatus());
        DuplicateMergeVerificationResult blocked = Result(
            DuplicateMergeVerificationStage.Unmergeable, (DuplicateMergeVerificationReasonKind.RegisteredRoot, 2), (DuplicateMergeVerificationReasonKind.Unreadable, 1));
        Assert.AreEqual(
            Unmergeable(Resources.Duplicate_merge_candidate_reason_registered_root, Resources.Duplicate_merge_candidate_reason_unreadable),
            blocked.FormatStatus());
        DuplicateMergeVerificationResult mixed = Result(
            DuplicateMergeVerificationStage.Unmergeable, (DuplicateMergeVerificationReasonKind.Link, 1), (DuplicateMergeVerificationReasonKind.ContentConflict, 1));
        Assert.AreEqual(
            Unmergeable(Resources.Duplicate_merge_candidate_reason_link, Resources.Duplicate_merge_candidate_reason_content_conflict),
            mixed.FormatStatus(),
            "統合不可と要注意の理由が混在しても、統合不可の理由、要注意の理由の順にすべて並べます。");
        Assert.AreEqual(
            Unmergeable(Resources.Duplicate_merge_candidate_reason_registered_root),
            DuplicateMergeVerificationResult.UnmergeableRegisteredRoot([@"C:\S\a", @"C:\S\b"]).FormatStatus());
    }

    /// <summary>代表値が3行目だけ異なる場合も、列ごとに表示文字列を比較し、全行の未確認STATUSに同じ付記を出すことを確認します。</summary>
    [TestMethod]
    [DataRow("Song", "Name", "Song", "Name", false, false)]
    [DataRow("", "", "", "", false, false)]
    [DataRow("Song", "", "song", "", true, false)]
    [DataRow("Song", "Name", "Song", "Ｎame", false, true)]
    [DataRow("Song", "", "Song", "Name", false, true)]
    [DataRow("", "", "Song", "", true, false)]
    [DataRow("Song", "Name", "Song ", "name", true, true)]
    public void StatusText_UsesEachRepresentativeDisplayValue(
        string title, string artist, string otherTitle, string otherArtist, bool titleDiffers, bool artistDiffers)
    {
        var fixture = new Fixture();
        DuplicateGroup group = Group(@"C:\S\a", @"C:\S\b", @"C:\S\c");
        fixture.Source.Pairs[group.Folders[0]] = fixture.Source.Pairs[group.Folders[1]] = (title, artist);
        fixture.Source.Pairs[group.Folders[2]] = (otherTitle, otherArtist);
        fixture.Adopt(group);

        var notes = new List<string>();
        if (titleDiffers) { notes.Add(Resources.Duplicate_merge_candidate_title_mismatch); }
        if (artistDiffers) { notes.Add(Resources.Duplicate_merge_candidate_artist_mismatch); }
        string plain = Resources.Duplicate_merge_candidate_status_unverified;
        string expected = notes.Count == 0 ? plain : WithMismatch(plain, [.. notes]);
        foreach (DuplicateMergeCandidateRow row in fixture.Rows) { Assert.AreEqual(expected, row.StatusText); }
    }

    /// <summary>安全性確認とKEEP変更の後も付記で検索・除外でき、再構築で列値が揃えば付記だけが消えることを確認します。</summary>
    [TestMethod]
    public void RepresentativeMismatch_FollowsCurrentPairsAcrossVerificationAndAdoption()
    {
        var fixture = new Fixture();
        DuplicateGroup group = Group(@"C:\S\a", @"C:\S\b"), single = Group(@"C:\S\single");
        string a = group.Folders[0], b = group.Folders[1];
        fixture.Source.Pairs[a] = ("Song", "Artist");
        fixture.Source.Pairs[b] = ("Song", "artist");
        fixture.Adopt(group, single);
        string note = Resources.Duplicate_merge_candidate_artist_mismatch;
        string query = "status:\"" + note + "\"";
        fixture.Owner.SearchText = query;
        CollectionAssert.AreEqual(new[] { a, b }, fixture.Paths);
        Assert.IsTrue(fixture.Owner.SetKeep(fixture.Row(b)));

        var safe = new DuplicateMergeVerificationResult(DuplicateMergeVerificationStage.Safe, [], ["readme.txt"]);
        foreach (DuplicateMergeVerificationResult? result in new[]
        {
            Result(DuplicateMergeVerificationStage.Caution, (DuplicateMergeVerificationReasonKind.AudioExtension, 1)),
            DuplicateMergeVerificationResult.UnmergeableRegisteredRoot(group.Folders),
            safe
        })
        {
            fixture.Owner.WriteVerifications([new(group, result)]);
            // 代表値の付記と.bmx/.txtの衝突は、段階の文言の後ろの一つの括弧へこの順に並ぶ。
            string stage = (result with { ProtectedNameCollisions = null }).FormatStatus();
            string expected = result.HasProtectedNameCollision
                ? WithMismatch(stage, note, Resources.Duplicate_merge_candidate_status_protected_collision)
                : WithMismatch(stage, note);
            Assert.AreEqual(expected, fixture.Row(b).StatusText);
            Assert.AreEqual(result.Stage != DuplicateMergeVerificationStage.Unmergeable, fixture.Row(b).State.CanSelect);
        }

        Assert.IsTrue(fixture.Owner.ToggleSelect(fixture.Row(b)));
        fixture.Owner.SearchText = string.Empty;
        fixture.Owner.ExcludeText = query;
        CollectionAssert.AreEqual(single.Folders, fixture.Paths);
        fixture.Source.Pairs[b] = fixture.Source.Pairs[a];
        fixture.Adopt(group, single);
        CollectionAssert.AreEqual(new[] { a, b, single.Folders[0] }, fixture.Paths);
        Assert.AreEqual(
            WithMismatch(Resources.Duplicate_merge_candidate_status_safe, Resources.Duplicate_merge_candidate_status_protected_collision),
            fixture.Row(b).StatusText);
        Assert.IsTrue(fixture.Row(b).IsKeep);
        Assert.IsTrue(fixture.Row(b).State.IsSelected);
    }

    /// <summary>
    /// 保証：STATUSの検索は、要注意・統合不可の二番目以降の理由名にも一致する（最初の理由だけを対象にしない）。検索なら一致するグループだけが残り、
    /// 除外なら一致するグループが消える。
    /// 検出する誤り：検索・除外が最初の理由だけを見る。
    /// </summary>
    [TestMethod]
    public void StatusSearchAndExclude_MatchEveryReasonName()
    {
        var fixture = new Fixture();
        DuplicateGroup multi = Group(@"C:\S\a1", @"C:\S\a2"), plain = Group(@"C:\S\b1", @"C:\S\b2");
        fixture.Adopt(multi, plain);
        fixture.Owner.WriteVerifications(
        [
            new(multi, Result(DuplicateMergeVerificationStage.Caution,
                (DuplicateMergeVerificationReasonKind.ParentReference, 1), (DuplicateMergeVerificationReasonKind.ContentConflict, 1))),
            new(plain, Result(DuplicateMergeVerificationStage.Caution, (DuplicateMergeVerificationReasonKind.ParentReference, 1)))
        ]);
        string second = "status:" + Resources.Duplicate_merge_candidate_reason_content_conflict;

        fixture.Owner.SearchText = second;
        CollectionAssert.AreEqual(new[] { multi }, fixture.Rows.Select(row => row.State.Group).Distinct().ToArray(), "検索なら二番目の理由名にも一致して残ります。");

        fixture.Owner.SearchText = string.Empty;
        fixture.Owner.ExcludeText = second;
        CollectionAssert.AreEqual(new[] { plain }, fixture.Rows.Select(row => row.State.Group).Distinct().ToArray(), "除外なら二番目の理由名に一致するグループが消えます。");
    }

    private static DuplicateMergeVerificationResult WithEquivalence(
        DuplicateMergeVerificationResult result, int names, bool all, bool protectedCollision = false)
        => result with
        {
            EquivalentAudioNames = [.. Enumerable.Range(0, names).Select(index => @"bgm\n" + index)],
            AllAudioCollisionsEquivalent = all,
            ProtectedNameCollisions = protectedCollision ? ["readme.txt"] : null
        };

    /// <summary>
    /// 保証：STATUSは、段階の文言の後ろに、補助情報（WAV/OGG同等、代表TITLE不一致、代表ARTIST不一致、.bmx/.txtの衝突）のうち成立したものを
    /// この順に「, 」でつないだ一つの括弧で付記し、段階（安全・要注意・統合不可）によらず同じ規則で付ける。「WAV/OGG同等」は全衝突が同等のときだけ成立し、
    /// 何も成立しなければ括弧を付けない。
    /// 検出する誤り：付記ごとの別の括弧、付記の順序の誤り、要注意・統合不可での付記の欠落、全同等でない結果への「WAV/OGG同等」、成立なしでの空の括弧。
    /// </summary>
    [TestMethod]
    public void StatusText_AppendsAuxiliaryNotesInOneBracketOnEveryStage()
    {
        var fixture = new Fixture();
        DuplicateGroup cautionTitle = Group(@"C:\S\a1", @"C:\S\a2"), safeArtist = Group(@"C:\S\b1", @"C:\S\b2"),
            blockedAll = Group(@"C:\S\c1", @"C:\S\c2"), safeTwo = Group(@"C:\S\d1", @"C:\S\d2"),
            conflictEquivalent = Group(@"C:\S\e1", @"C:\S\e2"), partial = Group(@"C:\S\f1", @"C:\S\f2"), plain = Group(@"C:\S\g1", @"C:\S\g2");
        fixture.Source.Pairs[@"C:\S\a1"] = ("Song", "Artist");
        fixture.Source.Pairs[@"C:\S\a2"] = ("Other", "Artist");
        fixture.Source.Pairs[@"C:\S\b1"] = ("Song", "Artist");
        fixture.Source.Pairs[@"C:\S\b2"] = ("Song", "Other");
        fixture.Source.Pairs[@"C:\S\c1"] = ("Song", "Artist");
        fixture.Source.Pairs[@"C:\S\c2"] = ("Other", "Other");
        fixture.Adopt(cautionTitle, safeArtist, blockedAll, safeTwo, conflictEquivalent, partial, plain);
        DuplicateMergeVerificationResult safe = Result(DuplicateMergeVerificationStage.Safe);
        DuplicateMergeVerificationResult blocked = Result(DuplicateMergeVerificationStage.Unmergeable, (DuplicateMergeVerificationReasonKind.Link, 1));
        DuplicateMergeVerificationResult conflict = Result(DuplicateMergeVerificationStage.Caution, (DuplicateMergeVerificationReasonKind.ContentConflict, 1));
        fixture.Owner.WriteVerifications(
        [
            new(cautionTitle, WithEquivalence(Result(DuplicateMergeVerificationStage.Caution, (DuplicateMergeVerificationReasonKind.AudioExtension, 1)), 0, false, protectedCollision: true)),
            new(safeArtist, WithEquivalence(safe, 1, true)),
            new(blockedAll, WithEquivalence(blocked, 1, true, protectedCollision: true)),
            new(safeTwo, WithEquivalence(safe, 2, true, protectedCollision: true)),
            new(conflictEquivalent, WithEquivalence(conflict, 1, true)),
            new(partial, WithEquivalence(safe, 1, false)),
            new(plain, safe)
        ]);

        Assert.AreEqual("要注意：同名音源の拡張子違い（代表TITLE不一致, .bmx/.txtの衝突）", fixture.Row(@"C:\S\a1").StatusText);
        Assert.AreEqual("安全（WAV/OGG同等, 代表ARTIST不一致）", fixture.Row(@"C:\S\b1").StatusText);
        Assert.AreEqual(
            WithMismatch(
                blocked.FormatStatus(),
                Resources.Duplicate_merge_candidate_status_audio_equivalent,
                Resources.Duplicate_merge_candidate_title_mismatch,
                Resources.Duplicate_merge_candidate_artist_mismatch,
                Resources.Duplicate_merge_candidate_status_protected_collision),
            fixture.Row(@"C:\S\c1").StatusText,
            "統合不可でも四つの補助情報をすべて、この順に一つの括弧で付記します。");
        Assert.AreEqual("安全（WAV/OGG同等, .bmx/.txtの衝突）", fixture.Row(@"C:\S\d1").StatusText, "付記ごとに別の括弧を付けません。");
        Assert.AreEqual(
            WithMismatch(conflict.FormatStatus(), Resources.Duplicate_merge_candidate_status_audio_equivalent),
            fixture.Row(@"C:\S\e1").StatusText,
            "要注意でも全衝突が同等なら「WAV/OGG同等」を付記します。");
        Assert.AreEqual(Resources.Duplicate_merge_candidate_status_safe, fixture.Row(@"C:\S\f1").StatusText, "全衝突が同等でなければ付記しません。");
        Assert.AreEqual(Resources.Duplicate_merge_candidate_status_safe, fixture.Row(@"C:\S\g1").StatusText, "成立した補助情報がなければ括弧を付けません。");
    }

    /// <summary>
    /// 保証：同等と判定した名前が一つ以上あれば、段階によらずツールチップの最後の行が「同名のWAVとOGG {n}組を同等と判定しました。」になり、0件なら出ない。
    /// 検出する誤り：段階による出し分け、件数の取り違え、0件での表示、末尾以外への配置。
    /// </summary>
    [TestMethod]
    public void StatusTooltip_EndsWithEquivalentAudioCountOnEveryStage()
    {
        DuplicateMergeVerificationResult[] stages =
        [
            Result(DuplicateMergeVerificationStage.Safe),
            Result(DuplicateMergeVerificationStage.Caution, (DuplicateMergeVerificationReasonKind.ParentReference, 1)),
            Result(DuplicateMergeVerificationStage.Unmergeable, (DuplicateMergeVerificationReasonKind.Link, 1))
        ];
        foreach (DuplicateMergeVerificationResult stage in stages)
        {
            foreach (int count in new[] { 0, 1, 3 })
            {
                string sentence = string.Format(Resources.Duplicate_merge_candidate_tooltip_audio_equivalent_format, count);
                string[] lines = WithEquivalence(stage, count, count > 0).FormatTooltip(default)!.Split(Environment.NewLine);
                if (count == 0) { CollectionAssert.DoesNotContain(lines, sentence); }
                else { Assert.AreEqual(sentence, lines[^1], stage.Stage + " n=" + count); }
            }
        }
        Assert.AreEqual(
            "同名のWAVとOGG 3組を同等と判定しました。",
            WithEquivalence(stages[0], 3, true).FormatTooltip(default)!.Split(Environment.NewLine)[^1]);
    }

    /// <summary>
    /// 保証：STATUSの補助情報の語「WAV/OGG同等」「.bmx/.txtの衝突」の検索と除外は、段階によらず、その付記を持つグループに一致する（他の付記と同じ括弧の中でも一致する）。
    /// 「安全」の検索は、付記を持つ安全なグループにも一致する。
    /// 検出する誤り：付記を検索・除外へ使わない、安全なグループだけへの一致、他の付記と並んだときの不一致、「安全」の検索からの脱落。
    /// </summary>
    [TestMethod]
    public void StatusSearchAndExclude_MatchAuxiliaryNotesOnEveryStage()
    {
        var fixture = new Fixture();
        DuplicateGroup safeEquivalent = Group(@"C:\S\a1", @"C:\S\a2"), cautionEquivalent = Group(@"C:\S\b1", @"C:\S\b2"),
            cautionProtected = Group(@"C:\S\c1", @"C:\S\c2"), safeProtectedTitle = Group(@"C:\S\d1", @"C:\S\d2"), plain = Group(@"C:\S\e1", @"C:\S\e2");
        fixture.Source.Pairs[@"C:\S\d1"] = ("Song", "Artist");
        fixture.Source.Pairs[@"C:\S\d2"] = ("Other", "Artist");
        fixture.Adopt(safeEquivalent, cautionEquivalent, cautionProtected, safeProtectedTitle, plain);
        DuplicateMergeVerificationResult caution = Result(DuplicateMergeVerificationStage.Caution, (DuplicateMergeVerificationReasonKind.ParentReference, 1));
        fixture.Owner.WriteVerifications(
        [
            new(safeEquivalent, WithEquivalence(Result(DuplicateMergeVerificationStage.Safe), 1, true)),
            new(cautionEquivalent, WithEquivalence(caution, 1, true)),
            new(cautionProtected, WithEquivalence(caution, 0, false, protectedCollision: true)),
            new(safeProtectedTitle, WithEquivalence(Result(DuplicateMergeVerificationStage.Safe), 0, false, protectedCollision: true)),
            new(plain, Result(DuplicateMergeVerificationStage.Safe))
        ]);
        DuplicateGroup[] Visible() => [.. fixture.Rows.Select(row => row.State.Group).Distinct()];

        string equivalent = "status:\"" + Resources.Duplicate_merge_candidate_status_audio_equivalent + "\"";
        fixture.Owner.SearchText = equivalent;
        CollectionAssert.AreEquivalent(new[] { safeEquivalent, cautionEquivalent }, Visible());
        fixture.Owner.SearchText = string.Empty;
        fixture.Owner.ExcludeText = equivalent;
        CollectionAssert.AreEquivalent(new[] { cautionProtected, safeProtectedTitle, plain }, Visible());

        string protectedCollision = "status:\"" + Resources.Duplicate_merge_candidate_status_protected_collision + "\"";
        fixture.Owner.ExcludeText = string.Empty;
        fixture.Owner.SearchText = protectedCollision;
        CollectionAssert.AreEquivalent(new[] { cautionProtected, safeProtectedTitle }, Visible(), "代表値の付記と同じ括弧の中でも一致します。");
        fixture.Owner.SearchText = string.Empty;
        fixture.Owner.ExcludeText = protectedCollision;
        CollectionAssert.AreEquivalent(new[] { safeEquivalent, cautionEquivalent, plain }, Visible());

        fixture.Owner.ExcludeText = string.Empty;
        fixture.Owner.SearchText = "status:" + Resources.Duplicate_merge_candidate_status_safe;
        CollectionAssert.AreEquivalent(new[] { safeEquivalent, safeProtectedTitle, plain }, Visible());
    }

    /// <summary>
    /// 保証：除外にSTATUSの語があるとき、確認結果の一括書込みで、行集合の差し替えは全体で一回になり、新しいSTATUSで除外を当て直して対象のグループが消える。
    /// 検出する誤り：書込みで除外を当て直さない（確認後も除外対象が残る）、グループごとの行集合の差し替え。
    /// </summary>
    [TestMethod]
    public void WriteVerifications_WithStatusExcludeReplacesRowSetOnceAndHidesMatchingGroups()
    {
        var fixture = new Fixture();
        DuplicateGroup hit = Group(@"C:\S\a1", @"C:\S\a2"), safe = Group(@"C:\S\b1", @"C:\S\b2"), other = Group(@"C:\S\c1");
        fixture.Adopt(hit, safe, other);
        fixture.Owner.ExcludeText = "status:" + Resources.Duplicate_merge_candidate_reason_content_conflict;
        Assert.AreEqual(3, fixture.Owner.VisibleGroupCount, "前提：書込み前は、どのグループも除外に当たりません。");
        var changed = new List<string?>();
        fixture.Owner.PropertyChanged += (_, args) => changed.Add(args.PropertyName);

        fixture.Owner.WriteVerifications(
        [
            new(hit, Result(DuplicateMergeVerificationStage.Caution,
                (DuplicateMergeVerificationReasonKind.ParentReference, 1), (DuplicateMergeVerificationReasonKind.ContentConflict, 1))),
            new(safe, Result(DuplicateMergeVerificationStage.Safe))
        ]);

        Assert.AreEqual(1, changed.Count(name => name == nameof(DuplicateMergeCandidateListOwner.Rows)), "行集合の差し替えは全体で一回です。");
        CollectionAssert.AreEqual(new[] { safe, other }, fixture.Rows.Select(row => row.State.Group).Distinct().ToArray());
        Assert.AreEqual(2, fixture.Owner.VisibleGroupCount);
    }

    /// <summary>
    /// 保証：除外は検索欄と同じ構文を行単位で評価し、条件を満たす行を一つでも持つグループを全行まとめて除く。各条件を別の行で満たすだけのグループ（AND）は除かれず、
    /// 否定は行単位（条件に当たらない行を持つグループをすべて除く）、空・空白は何も除かず、既存の検索との組合せは「検索で残り、除外で除かれない」グループを表示する。
    /// 件数・序数・表示中をすべて選択・解除・選択グループの捕捉は、除外後の表示中のグループだけを対象にし、除外で非表示のグループのSELECT・KEEP・確認結果は、
    /// 解除した後に同じインスタンスで残る。
    /// 検出する誤り：一致行だけの除外、グループ単位のAND評価、否定をグループ全体で評価する誤り、非表示のグループへの選択の作用・捕捉、除外解除後の状態の喪失、序数の飛び。
    /// </summary>
    [TestMethod]
    [DataRow("", "alpha", new[] { 1, 2 }, DisplayName = "alpha")]
    [DataRow("", "alpha gamma", new[] { 0, 1, 2 }, DisplayName = "行単位のANDを満たす行がなければ何も除かない")]
    [DataRow("", "-alpha", new int[0], DisplayName = "否定は行単位")]
    [DataRow("", "folder:epsilon", new[] { 0, 1 }, DisplayName = "folder")]
    [DataRow("", "title:moon", new[] { 0, 2 }, DisplayName = "title")]
    [DataRow("", "folder:re:^beta", new[] { 1, 2 }, DisplayName = "正規表現")]
    [DataRow("", "", new[] { 0, 1, 2 }, DisplayName = "空")]
    [DataRow("", "   ", new[] { 0, 1, 2 }, DisplayName = "空白")]
    [DataRow("song", "artist:sol", new[] { 1 }, DisplayName = "検索との組合せ")]
    public void Exclude_RemovesGroupsHavingAnyMatchingRowAndKeepsHiddenState(string search, string exclude, int[] expectedGroups)
    {
        var fixture = new Fixture();
        DuplicateGroup[] groups = [Group(@"C:\S\alpha1", @"C:\S\beta1"), Group(@"C:\S\gamma", @"C:\S\delta"), Group(@"C:\S\epsilon")];
        fixture.Source.Pairs[@"C:\S\gamma"] = ("Moon Song", "Luna");
        fixture.Source.Pairs[@"C:\S\epsilon"] = ("Sun Song", "Sol");
        fixture.Adopt(groups);
        // 除外の前の状態：g1のKEEPを変え、g2を安全・g3を要注意にする（確認の書込みでどちらもSELECTがオフになる）。
        // その後にg2をSELECTし、g1・g2がオン、g3だけがオフの状態にする。
        fixture.Owner.SelectVisible();
        fixture.Owner.SetKeep(fixture.Row(@"C:\S\beta1"));
        DuplicateMergeVerificationResult safe = Result(DuplicateMergeVerificationStage.Safe);
        DuplicateMergeVerificationResult caution = Result(DuplicateMergeVerificationStage.Caution, (DuplicateMergeVerificationReasonKind.ParentReference, 1));
        fixture.Owner.WriteVerifications([new(groups[1], safe), new(groups[2], caution)]);
        Assert.IsTrue(fixture.Owner.SetSelectOfRows([fixture.Row(@"C:\S\gamma")], selected: true));
        bool[] selected = [true, true, false];
        DuplicateMergeCandidateGroupState[] states =
        [
            .. groups.Select(group =>
            {
                Assert.IsTrue(fixture.Owner.TryGetState(group, out DuplicateMergeCandidateGroupState? state));
                return state!;
            })
        ];

        fixture.Owner.SearchText = search;
        fixture.Owner.ExcludeText = exclude;

        DuplicateGroup[] expected = [.. expectedGroups.Select(index => groups[index])];
        CollectionAssert.AreEqual(expected, fixture.Rows.Select(row => row.State.Group).Distinct().ToArray(), $"検索「{search}」・除外「{exclude}」");
        Assert.AreEqual(expected.Length, fixture.Owner.VisibleGroupCount);
        Assert.AreEqual(expectedGroups.Count(index => selected[index]), fixture.Owner.SelectedGroupCount, "選択の件数は表示中のグループだけを数えます。");
        DuplicateMergeCandidateRowSet rows = fixture.Owner.RowSet;
        for (int i = 0; i < rows.Count; i++) { Assert.AreEqual(rows.GetRowGroup(i).Ordinal, rows[i].GroupIndex); }
        if (rows.Count > 0) { Assert.AreEqual(expected.Length - 1, rows[^1].GroupIndex, "序数は除外後の表示順に振り直します。"); }
        CollectionAssert.AreEqual(
            expectedGroups.Where(index => selected[index]).Select(index => groups[index]).ToArray(),
            fixture.Owner.CaptureSelectedGroups().Select(target => target.Group).ToArray(),
            "捕捉は非表示のグループを含みません。");

        fixture.Owner.ClearVisible();
        foreach (int index in expectedGroups) { selected[index] = false; }
        CollectionAssert.AreEqual(selected, states.Select(state => state.IsSelected).ToArray(), "表示中をすべて解除は、非表示のグループへ作用しません。");
        fixture.Owner.SelectVisible();
        foreach (int index in expectedGroups) { selected[index] = true; }
        CollectionAssert.AreEqual(selected, states.Select(state => state.IsSelected).ToArray(), "表示中をすべて選択は、非表示のグループへ作用しません。");

        fixture.Owner.SearchText = string.Empty;
        fixture.Owner.ExcludeText = string.Empty;
        Assert.AreEqual(3, fixture.Owner.VisibleGroupCount);
        for (int index = 0; index < groups.Length; index++)
        {
            Assert.IsTrue(fixture.Owner.TryGetState(groups[index], out DuplicateMergeCandidateGroupState? state));
            Assert.AreSame(states[index], state, "除外を解除しても同じインスタンスです。");
            Assert.AreEqual(selected[index], state!.IsSelected);
        }
        Assert.AreEqual(@"C:\S\beta1", states[0].KeepFolder, "非表示の間もKEEPを保ちます。");
        Assert.AreSame(safe, states[1].Verification, "非表示の間も確認結果を保ちます。");
        Assert.AreSame(caution, states[2].Verification);
    }

    /// <summary>
    /// 保証：STATUSのツールチップは、理由ごとに説明と該当するフォルダ・相対パスを出し、一つの理由につき10件を超える分は「ほか{n}件」と省略する。
    /// 別内容ファイル衝突は統合したときの扱いを添え、.bmx/.txtの衝突はファイル名と扱いを付記として出す（要注意でも出る）。未確認はツールチップを持たない。
    /// 上書きの設定は一つ（スマート上書きがオフ）に固定し、設定ごとの分岐は <see cref="StatusTooltip_OverwriteNotesFollowTheSettingsAtReadTime"/> が確認する。
    /// 検出する誤り：件数の省略の誤り（11件目の混入、省略の注記の欠落）、理由の説明の欠落、扱いの説明の欠落、付記の脱落。
    /// </summary>
    [TestMethod]
    public void StatusTooltip_ListsReasonsWithOmissionNoteAndProtectedCollisions()
    {
        var overwrite = new DuplicateMergeOverwriteSettings(SmartOverwrite: false, KeepProtectedByRenaming: false);
        Assert.IsNull(DuplicateMergeVerificationResult.Unverified.FormatTooltip(overwrite));
        DuplicateMergeVerificationResult conflict = Result(
            DuplicateMergeVerificationStage.Caution, (DuplicateMergeVerificationReasonKind.ContentConflict, 12), (DuplicateMergeVerificationReasonKind.NoSharedContent, 1)) with
        { ProtectedNameCollisions = ["Data.BMX", "readme.txt"] };

        string[] lines = conflict.FormatTooltip(overwrite)!.Split(Environment.NewLine);

        Assert.AreEqual(Resources.Duplicate_merge_candidate_reason_content_conflict_description, lines[0]);
        Assert.AreEqual(Resources.Duplicate_merge_candidate_overwrite_note_source, lines[1]);
        string[] items = [.. lines.Skip(2).TakeWhile(line => line.StartsWith("- ", StringComparison.Ordinal))];
        Assert.AreEqual(10, items.Length, "一つの理由につき10件までです。");
        Assert.AreEqual(@"- C:\S\f\item00", items[0]);
        Assert.AreEqual(@"- C:\S\f\item09", items[9]);
        Assert.IsFalse(lines.Any(line => line.Contains("item10", StringComparison.Ordinal)), "11件目以降は出しません。");
        Assert.AreEqual(string.Format(Resources.Duplicate_merge_candidate_tooltip_more_format, 2), lines[2 + 10]);
        CollectionAssert.Contains(lines, Resources.Duplicate_merge_candidate_reason_no_shared_content_description);
        CollectionAssert.Contains(lines, Resources.Duplicate_merge_candidate_protected_collision_description);
        int protectedHeading = Array.IndexOf(lines, Resources.Duplicate_merge_candidate_protected_collision_description);
        Assert.AreEqual(Resources.Duplicate_merge_candidate_overwrite_note_source, lines[protectedHeading + 1]);
        CollectionAssert.Contains(lines, "- Data.BMX");
        CollectionAssert.Contains(lines, "- readme.txt");

        DuplicateMergeVerificationResult audio = Result(DuplicateMergeVerificationStage.Caution, (DuplicateMergeVerificationReasonKind.AudioExtension, 1));
        CollectionAssert.AreEqual(new[] { Resources.Duplicate_merge_candidate_reason_audio_extension_description, @"- C:\S\f\item00" },
            audio.FormatTooltip(overwrite)!.Split(Environment.NewLine));
    }

    /// <summary>
    /// 保証：STATUSのツールチップで、別内容ファイル衝突と.bmx/.txtの衝突に添える統合したときの扱いは、ツールチップを読む時点の上書きの設定で決まる。
    /// スマート上書きがオフなら両方が「移動元のファイルで上書き」（連番名での保持がオンでも同じ）、オンなら両方が「更新日時の新しい方」で、
    /// スマート上書きと連番名での保持がどちらもオンのときだけ、.bmx/.txtの衝突が「連番名で両方残る」になる。確認結果を書き直さずに設定だけを変えて行を読み直すと、新しい設定の扱いになる。
    /// 検出する誤り：連番名での保持の値だけでの分岐、確認結果の書込み時・構築時の設定の保持、別内容ファイル衝突への連番名の説明、設定によらない一つの文言。
    /// </summary>
    [TestMethod]
    [DataRow(false, false)]
    [DataRow(false, true)]
    [DataRow(true, false)]
    [DataRow(true, true)]
    public void StatusTooltip_OverwriteNotesFollowTheSettingsAtReadTime(bool smart, bool keepByRenaming)
    {
        // 書込みの時点では、行の値と反対の設定にしておく。
        var fixture = new Fixture { Overwrite = new(!smart, !keepByRenaming) };
        DuplicateGroup group = Group(@"C:\S\a", @"C:\S\b");
        fixture.Adopt(group);
        fixture.Owner.WriteVerifications(
        [
            new(group, Result(DuplicateMergeVerificationStage.Caution, (DuplicateMergeVerificationReasonKind.ContentConflict, 1)) with
            {
                ProtectedNameCollisions = ["readme.txt"]
            })
        ]);
        DuplicateMergeCandidateRow row = fixture.Owner.RowSet[0];
        Assert.IsNotNull(row.StatusTooltipText);

        fixture.Overwrite = new(smart, keepByRenaming);
        string[] lines = row.StatusTooltipText!.Split(Environment.NewLine);

        string onlyOneRemains = smart
            ? Resources.Duplicate_merge_candidate_overwrite_note_newer
            : Resources.Duplicate_merge_candidate_overwrite_note_source;
        string protectedNote = smart && keepByRenaming ? Resources.Duplicate_merge_candidate_overwrite_note_renamed : onlyOneRemains;
        Assert.AreEqual(Resources.Duplicate_merge_candidate_reason_content_conflict_description, lines[0]);
        Assert.AreEqual(onlyOneRemains, lines[1], "別内容ファイル衝突は、連番名での保持によらず一方だけが残ります。");
        int protectedHeading = Array.IndexOf(lines, Resources.Duplicate_merge_candidate_protected_collision_description);
        Assert.IsTrue(protectedHeading > 1);
        Assert.AreEqual(protectedNote, lines[protectedHeading + 1]);
        Assert.AreEqual("- readme.txt", lines[protectedHeading + 2]);
        string[] notes =
        [
            Resources.Duplicate_merge_candidate_overwrite_note_source,
            Resources.Duplicate_merge_candidate_overwrite_note_newer,
            Resources.Duplicate_merge_candidate_overwrite_note_renamed
        ];
        Assert.AreEqual(3, notes.Distinct().Count(), "三つの扱いは別の文言です。");
        Assert.AreEqual(2, lines.Count(line => notes.Contains(line)), "扱いの説明は、衝突ごとに一つだけです。");
    }

    /// <summary>
    /// 保証：安全のツールチップは、比較した同名の資源の組の数と方法を出し、方法が混在する場合は方法ごとの件数を出す。比較がなければその旨を出す。
    /// 検出する誤り：比較の数・方法の欠落、混在時の件数の合算、比較がないときの空のツールチップ。
    /// </summary>
    [TestMethod]
    public void SafeTooltip_ReportsComparedCountAndMethods()
    {
        string Tooltip(int sizeAndTime, int full, int partial)
            => new DuplicateMergeVerificationResult(
                DuplicateMergeVerificationStage.Safe, [], null, new DuplicateMergeComparisonSummary(sizeAndTime, full, partial)).FormatTooltip(default)!;

        Assert.AreEqual(Resources.Duplicate_merge_candidate_safe_not_compared, Tooltip(0, 0, 0));
        Assert.AreEqual(
            string.Format(Resources.Duplicate_merge_candidate_safe_compared_single_format, 3, Resources.Duplicate_merge_candidate_method_full), Tooltip(0, 3, 0));
        Assert.AreEqual(
            string.Format(Resources.Duplicate_merge_candidate_safe_compared_single_format, 2, Resources.Duplicate_merge_candidate_method_size_and_time), Tooltip(2, 0, 0));
        string[] mixed = Tooltip(2, 3, 1).Split(Environment.NewLine);
        Assert.AreEqual(string.Format(Resources.Duplicate_merge_candidate_safe_compared_header_format, 6), mixed[0]);
        CollectionAssert.AreEqual(
            new[]
            {
                "- " + string.Format(Resources.Duplicate_merge_candidate_safe_compared_method_format, Resources.Duplicate_merge_candidate_method_size_and_time, 2),
                "- " + string.Format(Resources.Duplicate_merge_candidate_safe_compared_method_format, Resources.Duplicate_merge_candidate_method_full, 3),
                "- " + string.Format(Resources.Duplicate_merge_candidate_safe_compared_method_format, Resources.Duplicate_merge_candidate_method_partial, 1)
            },
            mixed.Skip(1).ToArray());
    }

    /// <summary>
    /// 保証：簡易チェックの値は既定でオフで、有効にして変更を一回通知し、ライブラリを交換しても有効を保つ（アプリの起動中だけの値）。
    /// 検出する誤り：既定がオン、ライブラリの交換での値の消失・既定への復帰、変更の通知の欠落・重複。
    /// </summary>
    [TestMethod]
    public void QuickCompare_DefaultsOffAndKeepsEnabledValueAcrossLibraryExchange()
    {
        var fixture = new Fixture();
        var changed = new List<string?>();
        fixture.Owner.PropertyChanged += (_, args) => changed.Add(args.PropertyName);
        Assert.IsFalse(fixture.Owner.QuickCompareEnabled, "既定は無効です。");

        fixture.Owner.QuickCompareEnabled = true;
        fixture.Owner.AttachLibrary(new object());

        Assert.IsTrue(fixture.Owner.QuickCompareEnabled, "ライブラリの交換後も有効のまま残ります。");
        Assert.AreEqual(1, changed.Count(name => name == nameof(DuplicateMergeCandidateListOwner.QuickCompareEnabled)));
    }

    /// <summary>
    /// 保証：同等性検査の値は既定でオフで、有効にして変更を一回通知し、ライブラリを交換しても有効を保つ（アプリの起動中だけの値）。
    /// 検出する誤り：既定がオン、ライブラリの交換での値の消失、変更の通知の欠落・重複。
    /// </summary>
    [TestMethod]
    public void AudioEquivalence_DefaultsOffAndKeepsEnabledValueAcrossLibraryExchange()
    {
        var fixture = new Fixture();
        var changed = new List<string?>();
        fixture.Owner.PropertyChanged += (_, args) => changed.Add(args.PropertyName);
        Assert.IsFalse(fixture.Owner.AudioEquivalenceEnabled, "既定は無効です。");

        fixture.Owner.AudioEquivalenceEnabled = true;
        fixture.Owner.AttachLibrary(new object());

        Assert.IsTrue(fixture.Owner.AudioEquivalenceEnabled, "ライブラリの交換後も有効のまま残ります。");
        Assert.AreEqual(1, changed.Count(name => name == nameof(DuplicateMergeCandidateListOwner.AudioEquivalenceEnabled)));
    }

    /// <summary>
    /// 保証：衝突整理の値は既定で両方残すで、変更を一回通知し、ライブラリを交換しても保つ（アプリの起動中だけの値）。対象の捕捉は捕捉時点の値を持ち、以後の変更は捕捉した値へ影響しない。
    /// 検出する誤り：既定が両方残す以外、ライブラリの交換での値の消失・既定への復帰、変更の通知の欠落・重複、捕捉した値の後からの変化。
    /// </summary>
    [TestMethod]
    public void CollisionCleanup_DefaultsKeepBothAndKeepsSelectionAcrossLibraryExchange()
    {
        var fixture = new Fixture();
        var changed = new List<string?>();
        fixture.Owner.PropertyChanged += (_, args) => changed.Add(args.PropertyName);
        Assert.AreEqual(DuplicateMergeCollisionCleanupMode.KeepBoth, fixture.Owner.CollisionCleanupMode, "既定は両方残すです。");

        fixture.Adopt(Group(@"C:\S\a1", @"C:\S\a2"));
        fixture.Owner.SelectVisible();
        fixture.Owner.CollisionCleanupMode = DuplicateMergeCollisionCleanupMode.KeepOgg;
        fixture.Owner.CollisionCleanupMode = DuplicateMergeCollisionCleanupMode.KeepOgg;
        IReadOnlyList<DuplicateMergeCandidateGroupTarget> captured = fixture.Owner.CaptureSelectedGroups();
        fixture.Owner.CollisionCleanupMode = DuplicateMergeCollisionCleanupMode.KeepWav;
        Assert.AreEqual(DuplicateMergeCollisionCleanupMode.KeepOgg, captured.Single().CollisionCleanup, "捕捉した値は後の変更で変わりません。");
        Assert.AreEqual(DuplicateMergeCollisionCleanupMode.KeepWav, fixture.Owner.CaptureSelectedGroups().Single().CollisionCleanup);

        fixture.Owner.AttachLibrary(new object());

        Assert.AreEqual(DuplicateMergeCollisionCleanupMode.KeepWav, fixture.Owner.CollisionCleanupMode, "ライブラリの交換後も保ちます。");
        Assert.AreEqual(2, changed.Count(name => name == nameof(DuplicateMergeCandidateListOwner.CollisionCleanupMode)),
            "値が変わった二回だけ通知し、同じ値の設定と交換では通知しません。");
    }

    /// <summary>
    /// 保証：「削除」の値は既定でごみ箱へ移動で、変わったときだけ変更を通知し（同じ値の設定では通知しない）、ライブラリを交換しても保つ。対象の捕捉は捕捉時点の値を持ち、以後の変更は捕捉した値へ影響しない。
    /// 「衝突整理」の値とは独立している。
    /// 検出する誤り：既定がごみ箱へ移動以外、ライブラリの交換での値の消失・既定への復帰、変更の通知の欠落・重複、捕捉した値の後からの変化、衝突整理の値との連動。
    /// </summary>
    [TestMethod]
    public void DeleteMethod_DefaultsToRecycleBinAndKeepsValueAcrossLibraryExchange()
    {
        var fixture = new Fixture();
        var changed = new List<string?>();
        fixture.Owner.PropertyChanged += (_, args) => changed.Add(args.PropertyName);
        Assert.AreEqual(DuplicateOrganizeDeleteMethod.MoveToRecycleBin, fixture.Owner.DeleteMethod, "既定はごみ箱へ移動です。");

        fixture.Adopt(Group(@"C:\S\a1", @"C:\S\a2"));
        fixture.Owner.SelectVisible();
        fixture.Owner.DeleteMethod = DuplicateOrganizeDeleteMethod.MoveToRecycleBin;
        Assert.AreEqual(0, changed.Count(name => name == nameof(DuplicateMergeCandidateListOwner.DeleteMethod)), "既定と同じ値の設定では通知しません。");
        fixture.Owner.DeleteMethod = DuplicateOrganizeDeleteMethod.DeletePermanently;
        fixture.Owner.DeleteMethod = DuplicateOrganizeDeleteMethod.DeletePermanently;
        IReadOnlyList<DuplicateMergeCandidateGroupTarget> captured = fixture.Owner.CaptureSelectedGroups();
        fixture.Owner.DeleteMethod = DuplicateOrganizeDeleteMethod.MoveToRecycleBin;
        Assert.AreEqual(DuplicateOrganizeDeleteMethod.DeletePermanently, captured.Single().DeleteMethod, "捕捉した値は後の変更で変わりません。");
        Assert.AreEqual(DuplicateOrganizeDeleteMethod.MoveToRecycleBin, fixture.Owner.CaptureSelectedGroups().Single().DeleteMethod);
        Assert.AreEqual(DuplicateMergeCollisionCleanupMode.KeepBoth, fixture.Owner.CollisionCleanupMode, "衝突整理の値は連動しません。");

        fixture.Owner.DeleteMethod = DuplicateOrganizeDeleteMethod.DeletePermanently;
        fixture.Owner.AttachLibrary(new object());

        Assert.AreEqual(DuplicateOrganizeDeleteMethod.DeletePermanently, fixture.Owner.DeleteMethod, "ライブラリの交換後も保ちます。");
        Assert.AreEqual(3, changed.Count(name => name == nameof(DuplicateMergeCandidateListOwner.DeleteMethod)),
            "値が変わった三回だけ通知し、同じ値の設定と交換では通知しません。");
    }

    /// <summary>
    /// 保証：確認の入力の捕捉は、行が属するグループをグループごとに一度、SELECTの状態によらず行の順で返し、現在の状態表にない行は除く。
    /// 全グループのフォルダの捕捉は、検索で非表示のグループも含む。対象は登録範囲のフォルダを持つ。
    /// 検出する誤り：同じグループの重複捕捉、SELECTによる絞込み、非表示のグループの欠落（別のグループとの重なりの見落とし）、登録範囲の欠落。
    /// </summary>
    [TestMethod]
    public void Capture_ReturnsGroupsOfRowsOnceAndAllGroupFoldersIncludingHidden()
    {
        var fixture = new Fixture();
        fixture.Source.RegisteredRoots.UnionWith([@"C:\S\a1", @"C:\S\a2"]);
        DuplicateGroup a = Group(@"C:\S\a1", @"C:\S\a2"), b = Group(@"C:\S\b1", @"C:\S\b2");
        fixture.Adopt(a, b);
        fixture.Owner.SearchText = "folder:a";

        IReadOnlyList<DuplicateMergeCandidateGroupTarget> targets =
            fixture.Owner.CaptureGroupsOfRows([fixture.Row(@"C:\S\a2"), fixture.Row(@"C:\S\a1"), fixture.Row(@"C:\S\a2")]);
        IReadOnlyList<DuplicateMergeCandidateGroupFolders> all = fixture.Owner.CaptureAllGroupFolders();

        Assert.AreEqual(1, targets.Count);
        Assert.AreSame(a, targets[0].Group);
        CollectionAssert.AreEqual(new[] { @"C:\S\a1", @"C:\S\a2" }, targets[0].RegisteredFolders.ToArray());
        Assert.IsFalse(fixture.Row(@"C:\S\a1").State.IsSelected, "SELECTの状態によらず対象になります。");
        CollectionAssert.AreEquivalent(new[] { a, b }, all.Select(group => group.Group).ToArray(), "非表示のグループも含みます。");
    }

    /// <summary>
    /// 保証：結果なしの入口は、状態（SELECT・KEEP・確認結果）・並べ替え・検索語・除外語を保ったまま、行を0件・件数0・捕捉できる対象0件にして表示中でない状態にする。
    /// 構築も採用もしない。ライブラリが異なる呼出しは何も変えない。
    /// 検出する誤り：結果なしでの状態の破棄（再探索で引き継いだ状態が戻らない）、行の残存、並べ替え・検索語の消失、別ライブラリの呼出しによる変更。
    /// </summary>
    [TestMethod]
    public void ShowNoResult_EmptiesRowsAndKeepsStateSortSearchAndExclude()
    {
        var fixture = new Fixture();
        DuplicateGroup a = Group(@"C:\S\a1", @"C:\S\a2");
        DuplicateGroup b = Group(@"C:\S\b1", @"C:\S\b2");
        fixture.Adopt(a, b);
        fixture.Owner.SetKeep(fixture.Row(@"C:\S\a2"));
        fixture.Owner.ToggleSelect(fixture.Row(@"C:\S\a1"));
        fixture.Owner.RequestSort("Path", ListSortDirection.Descending);
        fixture.Owner.SearchText = "S";
        fixture.Owner.ExcludeText = "zzz";
        int resolveCalls = fixture.Source.ResolveCalls;
        int captureCalls = fixture.Source.CaptureCalls;

        Assert.IsFalse(fixture.Owner.ShowNoResult(new object()), "別のライブラリの呼出しは何も変えない");
        Assert.AreEqual(4, fixture.Owner.Rows.Count);
        Assert.IsTrue(fixture.Owner.ShowNoResult(fixture.Library));

        Assert.AreEqual(0, fixture.Owner.Rows.Count, "行は0件");
        Assert.AreEqual(0, fixture.Owner.VisibleGroupCount);
        Assert.AreEqual(0, fixture.Owner.SelectedGroupCount);
        Assert.IsFalse(fixture.Owner.IsViewActive, "表示中でない状態");
        Assert.AreEqual(0, fixture.Owner.CaptureSelectedGroups().Count, "捕捉できる対象も0件");
        Assert.AreEqual("Path", fixture.Owner.SortColumnName);
        Assert.AreEqual(ListSortDirection.Descending, fixture.Owner.SortDirection);
        Assert.AreEqual("S", fixture.Owner.SearchText);
        Assert.AreEqual("zzz", fixture.Owner.ExcludeText);
        Assert.AreEqual(resolveCalls, fixture.Source.ResolveCalls, "構築しない");
        Assert.AreEqual(captureCalls, fixture.Source.CaptureCalls, "構築しない");
        Assert.IsTrue(fixture.Owner.TryGetState(a, out DuplicateMergeCandidateGroupState? state), "状態は保つ");
        Assert.AreEqual(@"C:\S\a2", state!.KeepFolder);
    }

    /// <summary>
    /// 保証：結果なしの後の採用で、引き継いだインスタンスは状態（SELECT・KEEP）を保ち、新しいインスタンスは初期値になり、結果にないグループの状態は破棄される。
    /// 0件の結果の採用は行0件で、通常の採用として状態を捨てる。
    /// 検出する誤り：結果なしの間の状態の消失、新しいインスタンスへの状態の誤った引継ぎ、消えたグループの状態の残存、並べ替え・検索語の消失。
    /// </summary>
    [TestMethod]
    public void AdoptAfterNoResult_RestoresStateOfKeptInstancesAndDiscardsTheRest()
    {
        var fixture = new Fixture();
        DuplicateGroup kept = Group(@"C:\S\k1", @"C:\S\k2");
        DuplicateGroup gone = Group(@"C:\S\g1", @"C:\S\g2");
        fixture.Adopt(kept, gone);
        fixture.Owner.SetKeep(fixture.Row(@"C:\S\k2"));
        fixture.Owner.ToggleSelect(fixture.Row(@"C:\S\k1"));
        fixture.Owner.RequestSort("Path", ListSortDirection.Descending);
        Assert.IsTrue(fixture.Owner.ShowNoResult(fixture.Library));

        DuplicateGroup added = Group(@"C:\S\n1", @"C:\S\n2");
        fixture.Adopt(kept, added);

        Assert.IsTrue(fixture.Owner.IsViewActive);
        Assert.AreEqual(4, fixture.Owner.Rows.Count);
        Assert.IsTrue(fixture.Owner.TryGetState(kept, out DuplicateMergeCandidateGroupState? keptState));
        Assert.AreEqual(@"C:\S\k2", keptState!.KeepFolder, "引き継いだグループのKEEPを保つ");
        Assert.IsTrue(keptState.IsSelected, "引き継いだグループのSELECTを保つ");
        Assert.IsTrue(fixture.Owner.TryGetState(added, out DuplicateMergeCandidateGroupState? addedState));
        Assert.IsFalse(addedState!.IsSelected, "新しいインスタンスは初期値（SELECTオフ）");
        Assert.AreEqual(DuplicateMergeVerificationStage.Unverified, addedState.Verification.Stage, "新しいインスタンスは未確認");
        Assert.IsFalse(fixture.Owner.TryGetState(gone, out _), "結果にないグループの状態は破棄する");
        Assert.AreEqual("Path", fixture.Owner.SortColumnName, "並べ替えを保つ");

        Assert.IsTrue(fixture.Owner.ShowNoResult(fixture.Library));
        fixture.Adopt();
        Assert.AreEqual(0, fixture.Owner.Rows.Count, "0件の結果は行0件");
        Assert.IsFalse(fixture.Owner.TryGetState(kept, out _), "0件の結果の採用は通常どおり状態を捨てる");
    }
}
