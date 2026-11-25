using System.Reflection;
using BeMusicSeeker.Models;
using BeMusicSeeker.Models.BmsLibraryInternal;
using BeMusicSeeker.TestSupport.Components;
using BeMusicSeeker.TestSupport.Files;
using BeMusicSeeker.ViewModels;
using static BeMusicSeeker.TestSupport.Components.BmsLibraryDuplicateServiceTestSupport;

namespace BeMusicSeeker.Tests;

/// <summary>
/// 統合候補一覧の構築入力を、実ライブラリの実検索・実登録（<c>StandaloneBmsRootPaths</c>）から確認します。
/// TITLE・ARTISTの集計と表示値、登録範囲の判定の接続、構築前にプロフィールを作らないことを分担し、一覧の状態・操作は管理主体のケースが担当します。
/// </summary>
[TestClass]
public sealed class DuplicateMergeCandidateListLibraryTests
{
    private readonly ApplicationDatabaseScope databaseScope = new();

    private static ChartFile Chart(string path, string md5, string rawTitle, string rawArtist)
        => CreateFile(md5, path) with { RawTitle = rawTitle, Title = rawTitle, Artist = rawArtist, RawArtist = rawArtist };

    private static long ProfileBuildCount(BMSLibrary library)
    {
        FieldInfo field = typeof(BMSLibrary).GetField("catalogOwnedCollectionOwner", BindingFlags.NonPublic | BindingFlags.Instance)
            ?? throw new InvalidOperationException("既存のプロフィール所有者が見つかりません。");
        var owner = (CatalogOwnedCollectionOwner)field.GetValue(library)!;
        return Interlocked.Read(ref owner.MetadataProfileBuildCount);
    }

    private TestBmsLibrary CreateLibrary(string songDbPath, IEnumerable<string> roots, params ChartFile[] charts)
        => new(databaseScope, songDbPath, null, null, new TestFileMutationService(), new RecordingDialogService(), new TestUiScheduler(() => null),
            settings: TestBmsFactory.CreateRootSettings(roots))
        { BmsCharts = charts };

    /// <summary>
    /// 保証：TITLE・ARTISTは、フォルダ（子孫を含む所持範囲）の導入先候補プロフィールの実在最頻ペアの、整理済みの表示値である。
    /// 子孫の譜面も集計し（最頻のペアを選ぶ）、副役割を除いた表示ARTISTを返し、有効なペアがないフォルダは両方空である。
    /// 解決はプロフィールのキャッシュを共有し、構築の前にはプロフィールを作らず、同じフォルダを繰り返しても再構築しない。
    /// 検出する誤り：直下だけの集計、元の表記のままのARTIST、有効なペアがないフォルダへの別フォルダの値の混入、構築前のプロフィール作成、毎回の再構築。
    /// </summary>
    [TestMethod]
    public void ResolveDestinationDisplayPairs_UsesDescendantsDisplayValuesAndSharedProfiles()
    {
        WithTemporarySongDb(databaseScope, songDbPath =>
        {
            string parent = @"C:\BMS\Parent", child = parent + @"\Child", empty = @"C:\BMS\Empty", noTitle = @"C:\BMS\NoTitle";
            ChartFile[] charts =
            [
                Chart(parent + @"\a.bms", "11111111111111111111111111111111", "Parent Song", "P Artist"),
                Chart(child + @"\b.bms", "22222222222222222222222222222222", "Child Song", "C Artist feat. Guest"),
                Chart(child + @"\c.bms", "33333333333333333333333333333333", "Child Song", "C Artist feat. Guest"),
                Chart(noTitle + @"\d.bms", "44444444444444444444444444444444", string.Empty, "Nobody"),
            ];
            TestBmsLibrary library = CreateLibrary(songDbPath, [@"C:\BMS"], charts);
            try
            {
                var source = (IDuplicateMergeCandidateListSource)library;
                long before = ProfileBuildCount(library);

                IReadOnlyDictionary<string, (string Title, string Artist)> pairs = source.ResolveDestinationDisplayPairs(
                    [parent, child, empty, noTitle, parent.ToUpperInvariant()], CancellationToken.None);

                Assert.AreEqual(("Child Song", "C Artist"), pairs[parent], "子孫を含む実在最頻ペアの整理済みの表示値");
                Assert.AreEqual(("Child Song", "C Artist"), pairs[child]);
                Assert.AreEqual((string.Empty, string.Empty), pairs[empty], "所持譜面のないフォルダ");
                Assert.AreEqual((string.Empty, string.Empty), pairs[noTitle], "有効なペアがないフォルダ");
                Assert.AreEqual(4, ProfileBuildCount(library) - before, "異なるフォルダごとに一度だけ作ります（大文字小文字だけが違う指定は同じフォルダです）。");

                source.ResolveDestinationDisplayPairs([parent, child], CancellationToken.None);
                Assert.AreEqual(4, ProfileBuildCount(library) - before, "既存のキャッシュを共有し、再構築しません。");
            }
            finally { library.RequestShutdown("duplicate-merge-candidate-pairs"); }
        });
    }

    /// <summary>
    /// 保証：構築の前（重複検索・一覧の準備・登録範囲の捕捉）には、導入先候補プロフィールを作らない。
    /// 検出する誤り：一覧を構築する前の事前計算（ライブラリの全フォルダ分のプロフィール構築）。
    /// </summary>
    [TestMethod]
    public void SearchAndRegisteredRangeCapture_DoNotBuildProfiles()
    {
        WithTemporarySongDb(databaseScope, songDbPath =>
        {
            ChartFile[] charts =
            [
                Chart(@"C:\BMS\A\a.bms", "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa", "Same", "Artist"),
                Chart(@"C:\BMS\B\b.bms", "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa", "Same", "Artist"),
            ];
            TestBmsLibrary library = CreateLibrary(songDbPath, [@"C:\BMS"], charts);
            try
            {
                long before = ProfileBuildCount(library);
                library.SearchDuplicateChartGroups();
                _ = ((IDuplicateMergeCandidateListSource)library).CaptureRegisteredBmsRootPredicate();
                Assert.IsNotNull(library.DuplicateChartGroups);
                Assert.AreEqual(before, ProfileBuildCount(library), "構築前にプロフィールを作りません。");
            }
            finally { library.RequestShutdown("duplicate-merge-candidate-no-precompute"); }
        });
    }

    /// <summary>
    /// 保証：登録範囲の判定は、手動統合が移動元として拒否するのと同じ判定である。登録ルート本体と、その登録子を含む祖先は登録範囲に当たり、
    /// 登録配下の通常の子と、名前の先頭が同じ兄弟は当たらない。グループに登録範囲のフォルダが一つならKEEPを固定し、二つ以上なら統合不可にする。
    /// 検出する誤り：接頭辞だけの一致（Songs と Songs2）、祖先の見落とし、通常の子の誤った固定、登録範囲が二つのグループの統合可。
    /// </summary>
    [TestMethod]
    [DataRow(@"C:\BMS\Songs", true)]
    [DataRow(@"C:\BMS", true)]
    [DataRow(@"C:\BMS\Songs\Pack", false)]
    [DataRow(@"C:\BMS\Songs2", false)]
    [DataRow(@"C:\Other", false)]
    public void RegisteredRangePredicate_FollowsManualMergeRejectionBoundaries(string folder, bool expectedRegistered)
    {
        WithTemporarySongDb(databaseScope, songDbPath =>
        {
            TestBmsLibrary library = CreateLibrary(songDbPath, [@"C:\BMS\Songs"]);
            try
            {
                Func<string, bool> predicate = ((IDuplicateMergeCandidateListSource)library).CaptureRegisteredBmsRootPredicate();

                Assert.AreEqual(expectedRegistered, predicate(folder));
                FieldInfo ownerField = typeof(BMSLibrary).GetField("libraryMutationOwner", BindingFlags.NonPublic | BindingFlags.Instance)
                    ?? throw new InvalidOperationException("既存の変更所有者が見つかりません。");
                var manualMerge = (LibraryMutationOwner)ownerField.GetValue(library)!;
                Assert.AreEqual(expectedRegistered, manualMerge.ContainsRegisteredBmsRoot(folder), "手動統合の拒否判定と一致します。");
            }
            finally { library.RequestShutdown("duplicate-merge-candidate-registered-range"); }
        });
    }

    /// <summary>
    /// 保証：実ライブラリの入力から構築した初期状態は、登録範囲のフォルダが一つのグループでKEEPをそのフォルダへ固定し、二つ以上のグループは統合不可にし、
    /// 通常の子と名前の先頭が同じ兄弟だけのグループは固定しない。
    /// 検出する誤り：登録の捕捉を構築へ接続していない、グループの固定・統合不可の判定の食い違い。
    /// </summary>
    [TestMethod]
    public void PrepareWithRealLibrary_FixesAndBlocksByRegisteredRange()
    {
        WithTemporarySongDb(databaseScope, songDbPath =>
        {
            TestBmsLibrary library = CreateLibrary(songDbPath, [@"C:\BMS\Songs", @"C:\BMS\Other"]);
            try
            {
                var oneRegistered = new DuplicateGroup([], [@"C:\BMS\Songs\Pack", @"C:\BMS\Songs", @"C:\BMS\Songs2"]);
                var twoRegistered = new DuplicateGroup([], [@"C:\BMS\Songs", @"C:\BMS\Other"]);
                var none = new DuplicateGroup([], [@"C:\BMS\Songs\Pack", @"C:\BMS\Songs2"]);

                DuplicateMergeCandidateListPreparation preparation = DuplicateMergeCandidateListOwner.Prepare(
                    library, [oneRegistered, twoRegistered, none], library, CancellationToken.None);

                DuplicateMergeCandidateGroupInitial[] groups = [.. preparation.Groups];
                Assert.AreEqual((@"C:\BMS\Songs", true, false), (groups[0].KeepFolder, groups[0].KeepFixed, groups[0].Unmergeable));
                Assert.AreEqual((@"C:\BMS\Other", true, true), (groups[1].KeepFolder, groups[1].KeepFixed, groups[1].Unmergeable), "PATH昇順の先頭へ固定します。");
                Assert.IsFalse(groups[2].KeepFixed);
                Assert.IsFalse(groups[2].Unmergeable);
                Assert.AreSame(library, preparation.Library);
            }
            finally { library.RequestShutdown("duplicate-merge-candidate-prepare"); }
        });
    }

    /// <summary>
    /// 保証：所持譜面の取得口は、指定フォルダの直下と配下の所持譜面だけを返し、名前の先頭が同じ兄弟・別のフォルダ・所持していない譜面ファイルを含まない。
    /// 実ライブラリの取得口を実検査器へ渡し、実際のBMS解析（使用中の <c>#WAV01 ../x.wav</c>）から、KEEP以外のフォルダの親ディレクトリ参照を原文つきで、
    /// 子孫の所持フォルダを相対パスで返す。使用していない定義は該当せず、実ファイルのない所持譜面は解析不能になる。
    /// 検出する誤り：範囲外の譜面の混入（兄弟・別フォルダ）、所持していない譜面の混入、未使用の定義の誤検出、解析の失敗の黙殺、KEEP側の事実の誤検出。
    /// </summary>
    [TestMethod]
    public void CaptureOwnedCharts_FeedsInspectorWithRealParsingAndOnlyTheFolderScope()
    {
        using var workspace = TestWorkspace.Create(TestRunDirectory.RootDirectory);
        string root = workspace.GetPath("bms");
        string a = Path.Combine(root, "A"), b = Path.Combine(root, "B"), c = Path.Combine(root, "C"), a2 = Path.Combine(root, "A2"), other = Path.Combine(root, "Other");
        string Write(string folder, string name, string text)
        {
            Directory.CreateDirectory(folder);
            string path = Path.Combine(folder, name);
            File.WriteAllText(path, text);
            return path;
        }
        const string parentReference = "#TITLE parent\n#WAV01 ../x.wav\n#00111:01\n";
        const string unusedParentReference = "#TITLE unused\n#WAV01 ../y.wav\n#00111:00\n";
        string aParent = Write(a, "parent.bms", parentReference);
        string aUnused = Write(a, "unused.bms", unusedParentReference);
        string aNested = Write(Path.Combine(a, "sub"), "nested.bms", "#TITLE nested\n#WAV01 ok.wav\n#00111:01\n");
        Write(a, "unowned.bms", parentReference);
        string bOk = Write(b, "ok.bms", "#TITLE ok\n#WAV01 snd/a.wav\n#00111:01\n");
        string a2Chart = Write(a2, "sibling.bms", parentReference);
        string otherChart = Write(other, "out.bms", parentReference);
        Directory.CreateDirectory(c);
        string cGone = Path.Combine(c, "gone.bms");
        ChartFile Owned(string path, string md5) => Chart(path, md5, "T", "A");
        WithTemporarySongDb(databaseScope, songDbPath =>
        {
            TestBmsLibrary library = CreateLibrary(songDbPath, [root],
                Owned(aParent, "a1a1a1a1a1a1a1a1a1a1a1a1a1a1a1a1"), Owned(aUnused, "a2a2a2a2a2a2a2a2a2a2a2a2a2a2a2a2"),
                Owned(aNested, "a3a3a3a3a3a3a3a3a3a3a3a3a3a3a3a3"), Owned(bOk, "b1b1b1b1b1b1b1b1b1b1b1b1b1b1b1b1"),
                Owned(a2Chart, "a4a4a4a4a4a4a4a4a4a4a4a4a4a4a4a4"), Owned(otherChart, "c1c1c1c1c1c1c1c1c1c1c1c1c1c1c1c1"),
                Owned(cGone, "d1d1d1d1d1d1d1d1d1d1d1d1d1d1d1d1"));
            try
            {
                var source = (IDuplicateMergeOwnedChartSource)library;

                string[] captured = [.. source.CaptureOwnedCharts(a).Select(chart => chart.Path).Order(StringComparer.OrdinalIgnoreCase)];

                CollectionAssert.AreEqual(new[] { aNested, aParent, aUnused }.Order(StringComparer.OrdinalIgnoreCase).ToArray(), captured,
                    "直下と配下の所持譜面だけです（兄弟・別フォルダ・所持していない譜面を含みません）。");

                var inspector = new DuplicateMergeSafetyInspector(library);
                object ownerGroup = new object();
                DuplicateMergeGroupInspection inspection = inspector.Inspect(new DuplicateMergeInspectionRequest(
                    [a, b], [], new DuplicateMergeFolderIndex([(ownerGroup, [a, b])]), ownerGroup, SizeAndTimeOnly: false, AudioEquivalence: false));
                DuplicateMergeVerificationResult keepB = inspection.Evaluate(b);
                DuplicateMergeVerificationResult keepA = inspection.Evaluate(a);

                CollectionAssert.AreEqual(
                    new[] { new DuplicateMergeFinding(a, "parent.bms", "../x.wav") },
                    keepB.Reasons.Single(reason => reason.Kind == DuplicateMergeVerificationReasonKind.ParentReference).Findings.ToArray(),
                    "使用中の親ディレクトリ参照だけを、譜面とその原文で返します（未使用の定義と、所持していない・範囲外の譜面は含みません）。");
                CollectionAssert.AreEqual(
                    new[] { new DuplicateMergeFinding(a, "sub") },
                    keepB.Reasons.Single(reason => reason.Kind == DuplicateMergeVerificationReasonKind.NestedChartFolder).Findings.ToArray());
                Assert.IsFalse(keepA.Reasons.Any(reason => reason.Kind is DuplicateMergeVerificationReasonKind.ParentReference or DuplicateMergeVerificationReasonKind.NestedChartFolder),
                    "KEEP側の事実は該当しません。");

                DuplicateMergeGroupInspection stale = inspector.Inspect(new DuplicateMergeInspectionRequest(
                    [b, c], [], new DuplicateMergeFolderIndex([(ownerGroup, [b, c])]), ownerGroup, SizeAndTimeOnly: false, AudioEquivalence: false));
                CollectionAssert.AreEqual(
                    new[] { new DuplicateMergeFinding(c, "gone.bms") },
                    stale.Evaluate(b).Reasons.Single(reason => reason.Kind == DuplicateMergeVerificationReasonKind.Unreadable).Findings.ToArray(),
                    "実ファイルのない所持譜面は、成功へ読み替えず解析不能です。");
            }
            finally { library.RequestShutdown("duplicate-merge-owned-chart-source"); }
        });
    }
}
