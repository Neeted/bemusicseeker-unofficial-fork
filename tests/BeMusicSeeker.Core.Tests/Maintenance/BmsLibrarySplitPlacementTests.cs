using BeMusicSeeker.Models;
using BeMusicSeeker.Models.BmsLibraryInternal;
using BeMusicSeeker.Models.LR2;
using BeMusicSeeker.Models.Utils;
using BeMusicSeeker.TestSupport.Components;
using BeMusicSeeker.TestSupport.Files;
using static BeMusicSeeker.TestSupport.Components.BmsLibraryDuplicateServiceTestSupport;
using static BeMusicSeeker.TestSupport.Components.SplitPlacementTestSupport;

namespace BeMusicSeeker.Tests;

/// <summary>
/// 分裂配置の探索の入口（捕捉、フォルダと譜面の組立て、結果の保持・通知・失効・引継ぎ）を、実ライブラリで確認します。
/// 判定の数値の境界は <see cref="SplitPlacementSearchTests"/> が担当し、ここでは所持集合とリソース索引からの入力の作り方を確かめます。
/// </summary>
[TestClass]
public sealed class BmsLibrarySplitPlacementTests
{
    private static readonly string[] GateNames = ["rwlockBMSFilesInitializedMin", "rwlockBMSFiles", "rwlockDuplicateChartGroups"];

    private readonly ApplicationDatabaseScope databaseScope = new();

    private void Run(ChartFile[] bms, Action<TestBmsLibrary> test, ChartFile[]? bmson = null)
    {
        WithTemporarySongDb(databaseScope, songDbPath =>
        {
            var library = new TestBmsLibrary(databaseScope, songDbPath, null, null, new TestFileMutationService(), new RecordingDialogService(),
                new TestUiScheduler(() => null))
            { BmsCharts = bms, BmsonCharts = bmson ?? [] };
            try { test(library); }
            finally { library.RequestShutdown("split-placement"); }
        });
    }

    private static void Search(
        BMSLibrary library,
        IProgress<SplitPlacementSearchProgress>? progress = null,
        CancellationToken token = default,
        SplitPlacementSearchCriteria? criteria = null)
        => library.SearchSplitPlacementGroups(criteria ?? SplitPlacementSearchCriteria.Default, progress!, token);

    private static string[] FolderTexts(BMSLibrary library) => [.. library.SplitPlacementGroups!.Select(group => string.Join("|", group.Folders))];

    private sealed class Recorder : IProgress<SplitPlacementSearchProgress>
    {
        internal List<SplitPlacementSearchProgress> Reports { get; } = [];
        public void Report(SplitPlacementSearchProgress value) { lock (Reports) { Reports.Add(value); } }
    }

    private sealed class Delegating(Action<SplitPlacementSearchProgress> action) : IProgress<SplitPlacementSearchProgress>
    {
        public void Report(SplitPlacementSearchProgress value) => action(value);
    }

    /// <summary>グループ変更通知の配送スレッドと、そのときのライブラリのロックの保持を記録します。</summary>
    private sealed class NotificationLog
    {
        internal List<(int ThreadId, string[] Holds, List<DuplicateGroup>? Groups)> Entries { get; } = [];
        internal ManualResetEventSlim Signal { get; } = new();

        internal NotificationLog(BMSLibrary library)
        {
            library.PropertyChanged += (_, args) =>
            {
                if (args.PropertyName != "SplitPlacementGroups") { return; }
                lock (Entries)
                {
                    Entries.Add((Environment.CurrentManagedThreadId,
                        [.. GateNames.SelectMany(name => InstallServiceComponentTestSupport.CaptureLibraryGateHoldsOnCurrentThread(library, name).Select(hold => name + "の" + hold))],
                        library.SplitPlacementGroups));
                }
                Signal.Set();
            };
        }

        internal int Count { get { lock (Entries) { return Entries.Count; } } }
    }

    /// <summary>探索前は結果なし（null）で、有効な結果がない状態を空の一覧と取り違えないことを確認します。</summary>
    [TestMethod]
    public void SplitPlacementGroups_IsNullBeforeAnySearch()
    {
        Run(TwoRelatedFolders(), library => Assert.IsNull(library.SplitPlacementGroups));
    }

    /// <summary>
    /// 新しい結果の格納で、戻る前に呼出しスレッドで、ライブラリのロックを持たずに変更を一回通知し、通知時点で同じ一覧を読めること、
    /// 有効な結果があれば同じ一覧のまま通知も進捗もないことを確認します。
    /// </summary>
    [TestMethod]
    public void Search_PublishesOnceOnCallerWithoutLocksAndReusesValidResult()
    {
        Run(TwoRelatedFolders(), library =>
        {
            SetTwoRelatedIndex(library);
            var log = new NotificationLog(library);
            int caller = Environment.CurrentManagedThreadId;

            Search(library);

            Assert.AreEqual(1, log.Count);
            Assert.AreEqual(caller, log.Entries[0].ThreadId);
            Assert.AreEqual(0, log.Entries[0].Holds.Length, string.Join("、", log.Entries[0].Holds));
            List<DuplicateGroup> groups = library.SplitPlacementGroups!;
            Assert.AreSame(groups, log.Entries[0].Groups);
            CollectionAssert.AreEqual(new[] { @"C:\BMS\A|C:\BMS\B" }, FolderTexts(library));
            foreach (string name in GateNames)
            {
                Assert.AreEqual(0, InstallServiceComponentTestSupport.CaptureLibraryGateHoldsOnCurrentThread(library, name).Length, name);
            }

            var progress = new Recorder();
            Search(library, progress);

            Assert.AreSame(groups, library.SplitPlacementGroups);
            Assert.AreEqual(1, log.Count, "有効な結果では通知しない");
            Assert.AreEqual(0, progress.Reports.Count, "有効な結果では進捗を報告しない");
        });
    }

    /// <summary>0件の結果も新しい結果として、空の一覧（nullでない）を格納し、戻る前に一回通知し、次の呼出しでは再探索しないことを確認します。</summary>
    [TestMethod]
    public void Search_StoresEmptyListWhenNothingIsRelated()
    {
        Run(TwoRelatedFolders(), library =>
        {
            SetIndex(library, new(@"C:\BMS\A", Keys(1, 10)), new(@"C:\BMS\B", Keys(100, 10)));
            var log = new NotificationLog(library);

            Search(library);

            Assert.IsNotNull(library.SplitPlacementGroups);
            Assert.AreEqual(0, library.SplitPlacementGroups.Count);
            Assert.AreEqual(1, log.Count);
            List<DuplicateGroup> empty = library.SplitPlacementGroups;
            var progress = new Recorder();
            Search(library, progress);
            Assert.AreSame(empty, library.SplitPlacementGroups);
            Assert.AreEqual(1, log.Count);
            Assert.AreEqual(0, progress.Reports.Count);
        });
    }

    /// <summary>
    /// 自身の所有分だけで10個以上を数え、子の譜面フォルダの音源を足した全体の集合は使わないことを確認します。
    /// 親の自己所有が9個なら候補外で、10個なら関係になります。
    /// </summary>
    [TestMethod]
    [DataRow(9, false)]
    [DataRow(10, true)]
    public void Search_UsesOnlySelfOwnedKeysOfTheFolder(int selfCount, bool related)
    {
        Run([Chart(@"C:\BMS\A\a.bms", 1), Chart(@"C:\BMS\A\Sub\s.bms", 2), Chart(@"C:\BMS\B\b.bms", 3)], library =>
        {
            SetIndex(library,
                new(@"C:\BMS\A", Keys(1, selfCount), [.. Keys(1, selfCount), .. Keys(100, 6)]),
                new(@"C:\BMS\A\Sub", Keys(100, 6)),
                new(@"C:\BMS\B", Keys(1, 10)));

            Search(library);

            Assert.AreEqual(related ? 1 : 0, library.SplitPlacementGroups!.Count);
        });
    }

    /// <summary>親子のパスの組も、条件を満たせば関係になり、探索から除かれないことを確認します。</summary>
    [TestMethod]
    public void Search_RelatesParentAndChildFolders()
    {
        Run([Chart(@"C:\BMS\A\a.bms", 1), Chart(@"C:\BMS\A\Sub\s.bms", 2)], library =>
        {
            SetIndex(library, new(@"C:\BMS\A", Keys(1, 10)), new(@"C:\BMS\A\Sub", Keys(1, 10)));

            Search(library);

            CollectionAssert.AreEqual(new[] { @"C:\BMS\A|C:\BMS\A\Sub" }, FolderTexts(library));
        });
    }

    /// <summary>索引に項目のないフォルダは空の集合として扱い、例外を出さず、ほかの関係を妨げないことを確認します。</summary>
    [TestMethod]
    public void Search_TreatsFolderWithoutIndexEntryAsEmpty()
    {
        Run(TwoRelatedFolders(), library =>
        {
            SetIndex(library, new(@"C:\BMS\A", Keys(1, 10)), new(@"C:\BMS\B", Keys(1, 10)));

            Search(library);

            CollectionAssert.AreEqual(new[] { @"C:\BMS\A|C:\BMS\B" }, FolderTexts(library));
        });
    }

    /// <summary>
    /// 汎用名の数に、所持譜面のない索引項目を含めないことを確認します。
    /// キーを持つ対象フォルダが20個なら、所持譜面のない索引項目が一つあっても汎用名にならず、関係が残ります。
    /// </summary>
    [TestMethod]
    public void Search_DoesNotCountIndexEntriesWithoutChartsAsGenericHolders()
    {
        const uint generic = 7777;
        List<ChartFile> charts = [Chart(@"C:\BMS\F1\a.bms", 1), Chart(@"C:\BMS\F2\a.bms", 2)];
        List<IndexFolder> index = [new(@"C:\BMS\F1", [.. Keys(1, 9), generic]), new(@"C:\BMS\F2", [.. Keys(1, 9), generic])];
        for (int i = 0; i < 18; i++)
        {
            charts.Add(Chart($@"C:\BMS\G{i:00}\a.bms", 10 + i));
            index.Add(new($@"C:\BMS\G{i:00}", [.. Keys((uint)(1000 * (i + 1)), 9), generic]));
        }
        index.Add(new(@"C:\BMS\NoCharts", [.. Keys(90000, 9), generic]));
        Run([.. charts], library =>
        {
            SetIndex(library, [.. index]);

            Search(library);

            CollectionAssert.AreEqual(new[] { @"C:\BMS\F1|C:\BMS\F2" }, FolderTexts(library));
        });
    }

    /// <summary>
    /// 同じ主ハッシュが一方の子の譜面フォルダにだけある場合は関係になり、BMSとBMSONの間の共有は関係にしないことを確認します。
    /// </summary>
    [TestMethod]
    public void Search_ComparesPrimaryHashesOnlyAmongDirectChartsAcrossKinds()
    {
        Run([Chart(@"C:\BMS\X\x.bms", 1), Chart(@"C:\BMS\Y\y.bms", 2), Chart(@"C:\BMS\Y\Child\c.bms", 1)], library =>
        {
            SetIndex(library, new(@"C:\BMS\X", Keys(1, 10)), new(@"C:\BMS\Y", Keys(1, 10)), new(@"C:\BMS\Y\Child", Keys(900, 3)));
            Search(library);
            CollectionAssert.AreEqual(new[] { @"C:\BMS\X|C:\BMS\Y" }, FolderTexts(library), "子のフォルダだけにある共有は関係を妨げない");
        });

        Run([Chart(@"C:\BMS\X\x.bms", 1)], library =>
        {
            SetIndex(library, new(@"C:\BMS\X", Keys(1, 10)), new(@"C:\BMS\Y", Keys(1, 10)));
            Search(library);
            CollectionAssert.AreEqual(new[] { @"C:\BMS\X|C:\BMS\Y" }, FolderTexts(library), "BMSONと共有していない前提");
        }, bmson: [Chart(@"C:\BMS\Y\y.bmson", 2, kind: ChartFileKind.Bmson)]);

        Run([Chart(@"C:\BMS\X\x.bms", 1)], library =>
        {
            SetIndex(library, new(@"C:\BMS\X", Keys(1, 10)), new(@"C:\BMS\Y", Keys(1, 10)));
            Search(library);
            Assert.AreEqual(0, library.SplitPlacementGroups!.Count, "BMSとBMSONが同じ主ハッシュを持てば関係にしない");
        }, bmson: [Chart(@"C:\BMS\Y\y.bmson", 1, kind: ChartFileKind.Bmson)]);
    }

    /// <summary>
    /// 大文字小文字だけが違う表記は一つのフォルダで、自分自身との関係を作らず、フォルダはパスの昇順に並ぶことを確認します。
    /// 譜面は直下のすべて（音源と無関係な兄弟譜面とBMSONを含む）で、子フォルダの譜面は含みません。
    /// フォルダ内の譜面の順と最初に現れた表記は、同じライブラリの重複検索が同じフォルダについて返す順と表記に一致します。
    /// </summary>
    [TestMethod]
    public void Search_BuildsFoldersAndChartsInCapturedRowOrder()
    {
        ChartFile[] bms =
        [
            Chart(@"C:\BMS\Song\a.bms", 1, "SongA"),
            Chart(@"C:\BMS\Song\b.bms", 2, "SongB"),
            Chart(@"C:\BMS\SONG\e.bms", 5, "SongE"),
            Chart(@"C:\BMS\Song\Sub\d.bms", 4, "Sub"),
            Chart(@"C:\BMS\Other\o.bms", 9, "Other"),
            Chart(@"C:\BMS\Dup\d1.bms", 1),
            Chart(@"C:\BMS\Dup\d2.bms", 9),
        ];
        Run(bms, library =>
        {
            SetIndex(library, new(@"C:\BMS\Song", Keys(1, 10)), new(@"C:\BMS\Other", Keys(1, 10)));
            Search(library);

            DuplicateGroup group = library.SplitPlacementGroups!.Single();
            Assert.AreEqual(2, group.Folders.Count, "SongとSONGは一つのフォルダ");
            Assert.AreEqual(@"C:\BMS\Other", group.Folders[0]);
            Assert.IsTrue(string.Equals(@"C:\BMS\Song", group.Folders[1], StringComparison.OrdinalIgnoreCase));
            Assert.IsFalse(group.ChartFiles.Any(chart => chart.Path.Contains("Sub", StringComparison.Ordinal)), "子フォルダの譜面を含めない");
            Assert.AreEqual(4, group.ChartFiles.Count, "Otherの一譜面と、Song/SONGの直下の三譜面");

            library.SearchDuplicateChartGroups();
            DuplicateGroup duplicate = library.DuplicateChartGroups!.Single();
            foreach (string folder in group.Folders)
            {
                string duplicateFolder = duplicate.Folders.Single(name => string.Equals(name, folder, StringComparison.OrdinalIgnoreCase));
                Assert.AreEqual(duplicateFolder, folder, "最初に現れた表記");
                (OwnedChartToken?, string)[] expected = [.. duplicate.ChartFiles.Where(chart => InFolder(chart, folder)).Select(chart => (chart.Token, chart.Path))];
                (OwnedChartToken?, string)[] actual = [.. group.ChartFiles.Where(chart => InFolder(chart, folder)).Select(chart => (chart.Token, chart.Path))];
                CollectionAssert.AreEqual(expected, actual, folder);
            }
        });
    }

    private static bool InFolder(ChartFile chart, string folder)
        => string.Equals(Path.GetDirectoryName(chart.Path), folder, StringComparison.OrdinalIgnoreCase);

    /// <summary>BMSONと音源と無関係な兄弟譜面が、フォルダの全譜面として含まれることを確認します。</summary>
    [TestMethod]
    public void Search_IncludesBmsonAndUnrelatedSiblingCharts()
    {
        Run([Chart(@"C:\BMS\A\a.bms", 1), Chart(@"C:\BMS\A\sibling.bms", 2), Chart(@"C:\BMS\B\b.bms", 3)], library =>
        {
            SetIndex(library, new(@"C:\BMS\A", Keys(1, 10)), new(@"C:\BMS\B", Keys(1, 10)));
            Search(library);

            string[] names = [.. library.SplitPlacementGroups!.Single().ChartFiles.Select(chart => Path.GetFileName(chart.Path)).Order()];
            CollectionAssert.AreEqual(new[] { "a.bms", "b.bms", "b.bmson", "sibling.bms" }, names);
        }, bmson: [Chart(@"C:\BMS\B\b.bmson", 3, kind: ChartFileKind.Bmson)]);
    }

    /// <summary>見出しは先頭の譜面の題名で、"B"と"a"では"B"が先（序数）、同じ見出しは先頭フォルダのパスの昇順に並ぶことを確認します。</summary>
    [TestMethod]
    public void Search_OrdersGroupsByHeaderThenFirstFolder()
    {
        Run(
        [
            Chart(@"C:\BMS\X1\x.bms", 1, "B"), Chart(@"C:\BMS\X2\x.bms", 2, "zz"),
            Chart(@"C:\BMS\Y1\y.bms", 3, "a"), Chart(@"C:\BMS\Y2\y.bms", 4, "b"),
            Chart(@"C:\BMS\A1\a.bms", 5, "B"), Chart(@"C:\BMS\A2\a.bms", 6, "yy"),
        ], library =>
        {
            SetIndex(library,
                new(@"C:\BMS\X1", Keys(1, 10)), new(@"C:\BMS\X2", Keys(1, 10)),
                new(@"C:\BMS\Y1", Keys(100, 10)), new(@"C:\BMS\Y2", Keys(100, 10)),
                new(@"C:\BMS\A1", Keys(200, 10)), new(@"C:\BMS\A2", Keys(200, 10)));
            Search(library);

            DuplicateGroup[] groups = [.. library.SplitPlacementGroups!];
            CollectionAssert.AreEqual(new[] { "B", "B", "a" }, groups.Select(group => group.Header).ToArray(), "見出しの順");
            CollectionAssert.AreEqual(new[] { @"C:\BMS\A1", @"C:\BMS\X1", @"C:\BMS\Y1" }, groups.Select(group => group.Folders[0]).ToArray());
        });
    }

    /// <summary>
    /// 重複警告を持つ譜面は捕捉時の警告のままで、探索の前後でカタログの版、譜面の警告、重複検索の結果が変わらないことを確認します。
    /// </summary>
    [TestMethod]
    public void Search_DoesNotChangeCatalogWarningsOrDuplicateResult()
    {
        ChartFile warned = Chart(@"C:\BMS\A\a.bms", 1) with { Warnings = [ChartWarning.Create(ChartWarningKind.DuplicateChart)] };
        Run([warned, Chart(@"C:\BMS\B\b.bms", 2)], library =>
        {
            SetIndex(library, new(@"C:\BMS\A", Keys(1, 10)), new(@"C:\BMS\B", Keys(1, 10)));
            int revision = library.CatalogRevision;
            string[] before = [.. library.BmsCharts.Select(chart => chart.Path + ":" + string.Join(",", chart.Warnings.Select(warning => warning.Kind)))];

            Search(library);

            Assert.AreEqual(revision, library.CatalogRevision);
            CollectionAssert.AreEqual(before, library.BmsCharts.Select(chart => chart.Path + ":" + string.Join(",", chart.Warnings.Select(warning => warning.Kind))).ToArray());
            Assert.IsNull(library.DuplicateChartGroups);
            ChartFile inGroup = library.SplitPlacementGroups!.Single().ChartFiles.Single(chart => chart.Path.EndsWith("a.bms", StringComparison.Ordinal));
            Assert.IsTrue(inGroup.Warnings.Any(warning => warning.Kind == ChartWarningKind.DuplicateChart), "捕捉時の警告のまま");
            ChartFile other = library.SplitPlacementGroups.Single().ChartFiles.Single(chart => chart.Path.EndsWith("b.bms", StringComparison.Ordinal));
            Assert.IsFalse(other.Warnings.Any(warning => warning.Kind == ChartWarningKind.DuplicateChart));
        });
    }

    /// <summary>
    /// 事前の取消と進捗の報告の中での取消が <see cref="OperationCanceledException"/> になり、結果・通知を変えず、
    /// 次の成功した探索が取消の前の基底からインスタンスを引き継ぐことを確認します。
    /// 進捗の受け手が例外を投げた場合も状態を変えず、例外が呼出し側へ届きます。
    /// </summary>
    [TestMethod]
    public void Search_CancellationAndReceiverFailureKeepStateAndBase()
    {
        Run(TwoRelatedFolders(), library =>
        {
            SetTwoRelatedIndex(library);
            Search(library);
            DuplicateGroup first = library.SplitPlacementGroups!.Single();
            List<DuplicateGroup> firstList = library.SplitPlacementGroups;
            var log = new NotificationLog(library);

            // 索引の世代を進めて、有効な結果を失効扱いにする。結果の一覧は取消・失敗の後も変わらないことを確かめる。
            SetTwoRelatedIndex(library);
            using (var before = new CancellationTokenSource())
            {
                before.Cancel();
                Assert.ThrowsExactly<OperationCanceledException>(() => Search(library, null, before.Token));
            }
            using (var during = new CancellationTokenSource())
            {
                Assert.ThrowsExactly<OperationCanceledException>(() => Search(library, new Delegating(value => during.Cancel()), during.Token));
            }
            Assert.ThrowsExactly<InvalidOperationException>(() => Search(library, new Delegating(_ => throw new InvalidOperationException("receiver"))));

            Assert.AreSame(firstList, library.SplitPlacementGroups, "取消・失敗で結果を変えない");
            Assert.AreEqual(0, log.Count, "取消・失敗で通知しない");

            Search(library);

            Assert.AreNotSame(firstList, library.SplitPlacementGroups);
            Assert.AreSame(first, library.SplitPlacementGroups!.Single(), "取消の前の基底からインスタンスを引き継ぐ");
            Assert.AreEqual(1, log.Count);
        });
    }

    /// <summary>照合中にライブラリのロックを保持せず、進捗の受け手の中で止めている間に別スレッドの重複検索が完了できることを確認します。</summary>
    [TestMethod]
    public void Search_DoesNotHoldLibraryLocksWhileMatching()
    {
        Run(TwoRelatedFolders(), library =>
        {
            SetTwoRelatedIndex(library);
            Task? probe = null;
            var progress = new Delegating(value =>
            {
                if (value.Stage != SplitPlacementSearchStage.Match || probe != null) { return; }
                probe = ConcurrencyProbe.StartDedicated(library.SearchDuplicateChartGroups);
                ConcurrencyProbe.AssertCompletesWhileHeld(probe, "照合中の重複検索");
            });
            try
            {
                Search(library, progress);
            }
            finally
            {
                probe?.GetAwaiter().GetResult();
            }

            Assert.IsNotNull(probe);
            Assert.AreEqual(1, library.SplitPlacementGroups!.Count);
        });
    }

    /// <summary>
    /// 分裂配置の結果が非nullで重複検索の結果がnullのままでも、全置換で結果がnullになり変更通知が届くことを確認します。
    /// </summary>
    [TestMethod]
    public void Invalidation_FullBmsReplacementClearsResultAndNotifies()
    {
        Run(TwoRelatedFolders(), library =>
        {
            SetTwoRelatedIndex(library);
            Search(library);
            Assert.IsNull(library.DuplicateChartGroups);
            var log = new NotificationLog(library);

            library.BmsCharts = TwoRelatedFolders();

            WaitForInvalidation(library, log);
        });
    }

    /// <summary>譜面の削除の本番の変更経路で、探索済みの分裂配置の結果が失効し変更通知が届くことを確認します（実ファイル）。</summary>
    [TestMethod]
    public void Invalidation_ChartRemovalClearsResultAndNotifies()
    {
        using var workspace = TestWorkspace.Create(TestRunDirectory.RootDirectory);
        string a = Path.Combine(workspace.RootDirectory, "A"), b = Path.Combine(workspace.RootDirectory, "B");
        Directory.CreateDirectory(a);
        Directory.CreateDirectory(b);
        string removed = Path.Combine(a, "a.bmson"), kept = Path.Combine(b, "b.bmson");
        File.WriteAllText(removed, "{}");
        File.WriteAllText(kept, "{}");
        ChartFile target = Chart(removed, 1, kind: ChartFileKind.Bmson) with { Folder = a };
        Run([], library =>
        {
            library.BmsonCharts = [target, Chart(kept, 2, kind: ChartFileKind.Bmson) with { Folder = b }];
            SetIndex(library, new(a, Keys(1, 10)), new(b, Keys(1, 10)));
            Search(library);
            Assert.AreEqual(1, library.SplitPlacementGroups!.Count);
            var log = new NotificationLog(library);

            LibraryChartRemovalOutcome outcome = library.RemoveLibraryCharts(
                library.PrepareLibraryChartRemoval([LibraryChartRef.FromChartFile(target)]), sendToRecycleBin: false, approvedWholeFolderDeletePaths: [], notices: new LibraryOperationNotices());
            Assert.AreEqual(1, outcome.ConfirmedChartCount);

            WaitForInvalidation(library, log);
        });
    }

    /// <summary>フォルダの統合の本番の変更経路で、探索済みの分裂配置の結果が失効し変更通知が届くことを確認します（実ファイル、実SQLite）。</summary>
    [TestMethod]
    public void Invalidation_FolderMergeClearsResultAndNotifies()
    {
        WithTemporarySongDb(databaseScope, songDbPath =>
        {
            using var workspace = TestWorkspace.Create(TestRunDirectory.RootDirectory);
            string root = Path.Combine(workspace.RootDirectory, "Library");
            string source = Path.Combine(root, "S"), destination = Path.Combine(root, "D"), third = Path.Combine(root, "T");
            foreach (string directory in new[] { source, destination, third }) { Directory.CreateDirectory(directory); }
            File.WriteAllText(Path.Combine(source, "s.bms"), "#TITLE S\n#ARTIST S\n");
            File.WriteAllText(Path.Combine(destination, "d.bms"), "#TITLE D\n#ARTIST D\n");
            File.WriteAllText(Path.Combine(third, "t.bms"), "#TITLE T\n#ARTIST T\n");
            ChartFile[] owned = [.. Directory.EnumerateFiles(root, "*.bms", SearchOption.AllDirectories).Select(path => BmsChartFileParser.ParseSnapshot(ChartFileContentReader.ReadSnapshot(path)))];
            var library = new TestBmsLibrary(databaseScope, songDbPath, null, null, new TestFileMutationService(), new RecordingDialogService(),
                new TestUiScheduler(() => null), () => new BmsLibraryOptionsSnapshot { OperationModeLR2DB = false, BMSInstallDir = root, DeletePendingPackageSourceAfterInstall = false },
                settings: TestBmsFactory.CreateRootSettings([root]))
            { BmsCharts = owned, BmsonCharts = [] };
            try
            {
                BmsLibraryInitializationTestSupport.ExecuteSongDbFixtureTransaction(databaseScope, songDbPath, db =>
                { foreach (ChartFile chart in library.BmsCharts) { db.InsertOrReplace(ChartSongStorageMapping.ToBmsRow(chart), typeof(LR2SongDB.song)); } });
                SetIndex(library, new(source, Keys(1, 10)), new(destination, Keys(1, 10)), new(third, Keys(500, 10)));
                Search(library);
                Assert.AreEqual(1, library.SplitPlacementGroups!.Count);
                var log = new NotificationLog(library);

                DuplicateMergeMaintenanceReceipt merged = library.MergeChartDirectory(source, destination, notices: new LibraryOperationNotices(), operationId: 1);
                Assert.IsTrue(merged.MergeApplied);

                WaitForInvalidation(library, log);
            }
            finally { library.RequestShutdown("split-placement-merge"); }
        });
    }

    private static void WaitForInvalidation(BMSLibrary library, NotificationLog log)
    {
        Task never = new TaskCompletionSource().Task;
        ConcurrencyProbe.WaitForSignal(log.Signal, never, "分裂配置の失効通知");
        Assert.IsNull(library.SplitPlacementGroups);
        Assert.IsTrue(log.Count >= 1);
    }

    /// <summary>所持集合が同じでも、リソース索引の世代が進めば次の探索が新しい索引に基づく結果になることを確認します。</summary>
    [TestMethod]
    public void Search_RepeatsWhenOnlyResourceIndexGenerationChanged()
    {
        Run(TwoRelatedFolders(), library =>
        {
            SetTwoRelatedIndex(library);
            Search(library);
            List<DuplicateGroup> first = library.SplitPlacementGroups!;
            Assert.AreEqual(1, first.Count);
            var log = new NotificationLog(library);

            SetIndex(library, new(@"C:\BMS\A", Keys(1, 10)), new(@"C:\BMS\B", Keys(300, 10)));
            Search(library);

            Assert.AreNotSame(first, library.SplitPlacementGroups);
            Assert.AreEqual(0, library.SplitPlacementGroups!.Count);
            Assert.AreEqual(1, log.Count);
        });
    }

    /// <summary>
    /// 画面が探索の要否を読む <c>HasCurrentSplitPlacementGroups</c> が、探索前は偽、探索後は0件を含めて真、リソース索引の世代が進むと偽、
    /// 再探索で真、所持集合の失効で偽になることを確認します。結果の有無と世代の一致のどちらか一方だけを見る誤りを検出します。
    /// </summary>
    [TestMethod]
    public void HasCurrentSplitPlacementGroups_FollowsResultPresenceAndIndexGeneration()
    {
        Run(TwoRelatedFolders(), library =>
        {
            SetTwoRelatedIndex(library);
            Assert.IsFalse(library.HasCurrentSplitPlacementGroups(SplitPlacementSearchCriteria.Default), "探索前");

            Search(library);
            Assert.IsTrue(library.HasCurrentSplitPlacementGroups(SplitPlacementSearchCriteria.Default), "探索後");

            SetIndex(library, new(@"C:\BMS\A", Keys(1, 10)), new(@"C:\BMS\B", Keys(300, 10)));
            Assert.IsNotNull(library.SplitPlacementGroups, "世代が変わっても結果は保持する");
            Assert.IsFalse(library.HasCurrentSplitPlacementGroups(SplitPlacementSearchCriteria.Default), "世代の変化");

            Search(library);
            Assert.AreEqual(0, library.SplitPlacementGroups!.Count);
            Assert.IsTrue(library.HasCurrentSplitPlacementGroups(SplitPlacementSearchCriteria.Default), "0件の再探索");

            var log = new NotificationLog(library);
            library.BmsCharts = TwoRelatedFolders();
            WaitForInvalidation(library, log);
            Assert.IsFalse(library.HasCurrentSplitPlacementGroups(SplitPlacementSearchCriteria.Default), "失効");
        });
    }

    private static readonly SplitPlacementSearchCriteria CriteriaEighty = SplitPlacementSearchCriteria.Default;

    private static readonly SplitPlacementSearchCriteria CriteriaNinety = new(90, 20, SplitPlacementShareBasis.SmallerSide);

    /// <summary>A・B・Cが一つの集まり（CはA・Bと10個中8個を共有）で、D1・D2が別の集まりになる、五フォルダの入力です。</summary>
    private static ChartFile[] FiveFolderCharts() =>
    [
        Chart(@"C:\BMS\A\a.bms", 1, "A"), Chart(@"C:\BMS\B\b.bms", 2, "B"), Chart(@"C:\BMS\C\c.bms", 3, "C"),
        Chart(@"C:\BMS\D1\d.bms", 4, "D1"), Chart(@"C:\BMS\D2\d.bms", 5, "D2"),
    ];

    private static void SetFiveFolderIndex(BMSLibrary library)
        => SetIndex(
            library,
            new(@"C:\BMS\A", Keys(1, 10)), new(@"C:\BMS\B", Keys(1, 10)), new(@"C:\BMS\C", [.. Keys(1, 8), .. Keys(900, 2)]),
            new(@"C:\BMS\D1", Keys(100, 10)), new(@"C:\BMS\D2", Keys(100, 10)));

    /// <summary>
    /// 探索条件の有効性の判定を確認します。探索した探索条件と値の等しい別インスタンスでは真、値の異なる探索条件では偽、
    /// リソース索引の世代が進むと偽、所持集合の失効で偽になります。世代と結果の有無だけを見て探索条件を無視する誤りを検出します。
    /// </summary>
    [TestMethod]
    public void HasCurrentSplitPlacementGroups_RequiresEqualCriteria()
    {
        Run(FiveFolderCharts(), library =>
        {
            SetFiveFolderIndex(library);
            Search(library, criteria: CriteriaEighty);

            Assert.IsTrue(library.HasCurrentSplitPlacementGroups(CriteriaEighty));
            Assert.IsTrue(library.HasCurrentSplitPlacementGroups(new SplitPlacementSearchCriteria(80, 20, SplitPlacementShareBasis.SmallerSide)), "値の等しい別インスタンス");
            Assert.IsFalse(library.HasCurrentSplitPlacementGroups(CriteriaNinety), "共有率が違う");
            Assert.IsFalse(library.HasCurrentSplitPlacementGroups(new SplitPlacementSearchCriteria(80, 21, SplitPlacementShareBasis.SmallerSide)), "汎用名の数が違う");
            Assert.IsFalse(library.HasCurrentSplitPlacementGroups(new SplitPlacementSearchCriteria(80, 20, SplitPlacementShareBasis.BothSides)), "基準が違う");

            SetFiveFolderIndex(library);
            Assert.IsFalse(library.HasCurrentSplitPlacementGroups(CriteriaEighty), "世代が進んだ");

            Search(library, criteria: CriteriaEighty);
            var log = new NotificationLog(library);
            library.BmsCharts = FiveFolderCharts();
            WaitForInvalidation(library, log);
            Assert.IsFalse(library.HasCurrentSplitPlacementGroups(CriteriaEighty), "失効");
        });
    }

    /// <summary>
    /// 世代も探索条件も同じ探索は、探索も通知も進捗の報告もせず、同じ結果のまま戻ることを確認します。
    /// 値の等しい別インスタンスの探索条件でも同じです。
    /// </summary>
    [TestMethod]
    public void Search_WithSameGenerationAndEqualCriteria_DoesNotSearchAgain()
    {
        Run(FiveFolderCharts(), library =>
        {
            SetFiveFolderIndex(library);
            Search(library, criteria: CriteriaEighty);
            List<DuplicateGroup> first = library.SplitPlacementGroups!;
            var log = new NotificationLog(library);
            int reports = 0;

            Search(library, new Delegating(_ => reports++), criteria: new SplitPlacementSearchCriteria(80, 20, SplitPlacementShareBasis.SmallerSide));

            Assert.AreSame(first, library.SplitPlacementGroups);
            Assert.AreEqual(0, log.Count, "通知しない");
            Assert.AreEqual(0, reports, "進捗を報告しない");
        });
    }

    /// <summary>
    /// 同じ世代で探索条件だけを変えると探索し直して通知を一回行い、結果が新しい探索条件に従うことを確認します。
    /// 元の探索条件へ戻した探索も、有効な結果がないため再び探索します。
    /// </summary>
    [TestMethod]
    public void Search_WithOtherCriteria_SearchesAgainAndFollowsCriteria()
    {
        Run(FiveFolderCharts(), library =>
        {
            SetFiveFolderIndex(library);
            Search(library, criteria: CriteriaEighty);
            Assert.AreEqual(3, library.SplitPlacementGroups!.Single(group => group.Folders.Count == 3).Folders.Count, "共有率80ではA・B・Cが一つのグループ");
            var log = new NotificationLog(library);

            Search(library, criteria: CriteriaNinety);

            Assert.AreEqual(1, log.Count);
            Assert.IsTrue(library.HasCurrentSplitPlacementGroups(CriteriaNinety));
            Assert.IsFalse(library.HasCurrentSplitPlacementGroups(CriteriaEighty));
            CollectionAssert.AreEqual(new[] { 2, 2 }, library.SplitPlacementGroups!.Select(group => group.Folders.Count).ToArray(), "共有率90ではCが外れる");

            Search(library, criteria: CriteriaEighty);

            Assert.AreEqual(2, log.Count);
            Assert.IsTrue(library.HasCurrentSplitPlacementGroups(CriteriaEighty));
            Assert.IsTrue(library.SplitPlacementGroups!.Any(group => group.Folders.Count == 3));
        });
    }

    /// <summary>
    /// 探索条件を変えた探索の取消・失敗では、結果のインスタンス・探索条件（有効性）を保ち通知しないことと、
    /// その後の成功が保持していた結果を基底にインスタンスを引き継ぐことを確認します。
    /// </summary>
    [TestMethod]
    public void Search_CancelledOrFailedWithOtherCriteria_KeepsResultAndCriteria()
    {
        Run(FiveFolderCharts(), library =>
        {
            SetFiveFolderIndex(library);
            Search(library, criteria: CriteriaEighty);
            List<DuplicateGroup> held = library.SplitPlacementGroups!;
            DuplicateGroup independent = held.Single(group => group.Folders.Count == 2);
            var log = new NotificationLog(library);

            using (var cancelled = new CancellationTokenSource())
            {
                cancelled.Cancel();
                Assert.ThrowsExactly<OperationCanceledException>(() => Search(library, token: cancelled.Token, criteria: CriteriaNinety));
            }
            Assert.ThrowsExactly<InvalidOperationException>(() => Search(library, new Delegating(_ => throw new InvalidOperationException("receiver")), criteria: CriteriaNinety));

            Assert.AreSame(held, library.SplitPlacementGroups);
            Assert.IsTrue(library.HasCurrentSplitPlacementGroups(CriteriaEighty), "探索条件も保つ");
            Assert.IsFalse(library.HasCurrentSplitPlacementGroups(CriteriaNinety));
            Assert.AreEqual(0, log.Count, "取消・失敗で通知しない");

            Search(library, criteria: CriteriaNinety);

            Assert.AreSame(independent, library.SplitPlacementGroups!.Single(group => group.Folders[0].EndsWith("D1", StringComparison.Ordinal)), "保持していた結果を基底に引き継ぐ");
            Assert.AreEqual(1, log.Count);
        });
    }

    /// <summary>
    /// 探索条件を変えた再探索で、構成が同じグループ（D1・D2）は同じインスタンス、構成が変わったグループ（A・B・Cが A・B になる）は新しいインスタンスになることを確認します。
    /// 失効した後の探索でも、失効前の結果を基底として引き継ぎます。
    /// </summary>
    [TestMethod]
    public void Search_WithOtherCriteria_InheritsOnlyUnchangedGroups_AlsoAfterInvalidation()
    {
        Run(FiveFolderCharts(), library =>
        {
            SetFiveFolderIndex(library);
            Search(library, criteria: CriteriaEighty);
            DuplicateGroup[] first = [.. library.SplitPlacementGroups!];
            DuplicateGroup firstThree = first.Single(group => group.Folders.Count == 3);
            DuplicateGroup firstPair = first.Single(group => group.Folders.Count == 2);

            Search(library, criteria: CriteriaNinety);

            DuplicateGroup[] second = [.. library.SplitPlacementGroups!];
            Assert.AreSame(firstPair, second.Single(group => group.Folders[0].EndsWith("D1", StringComparison.Ordinal)), "構成が同じグループは同じインスタンス");
            Assert.IsFalse(second.Contains(firstThree));
            Assert.IsFalse(first.Contains(second.Single(group => group.Folders[0].EndsWith(@"\A", StringComparison.Ordinal))), "構成が変わったグループは新しいインスタンス");

            DuplicateGroup secondPair = second.Single(group => group.Folders[0].EndsWith("D1", StringComparison.Ordinal));
            var log = new NotificationLog(library);
            library.BmsCharts = [.. library.BmsCharts];
            WaitForInvalidation(library, log);
            Assert.IsNull(library.SplitPlacementGroups);

            Search(library, criteria: CriteriaNinety);

            Assert.AreSame(secondPair, library.SplitPlacementGroups!.Single(group => group.Folders[0].EndsWith("D1", StringComparison.Ordinal)), "失効後も直近の結果を基底にする");
        });
    }

    /// <summary>
    /// 再探索で、構成が同じグループは同じインスタンスのまま捕捉時の見出しと譜面値を保ち（題名だけの変更は反映しない）、
    /// 譜面の追加があれば新しいインスタンスになることを確認します。
    /// </summary>
    [TestMethod]
    public void Search_InheritsInstancesOfUnchangedGroupsOnly()
    {
        Run(
        [
            Chart(@"C:\BMS\A1\a.bms", 1, "first"), Chart(@"C:\BMS\A2\a.bms", 2, "second"),
            Chart(@"C:\BMS\B1\b.bms", 3, "third"), Chart(@"C:\BMS\B2\b.bms", 4, "fourth"),
        ], library =>
        {
            IndexFolder[] index = [new(@"C:\BMS\A1", Keys(1, 10)), new(@"C:\BMS\A2", Keys(1, 10)), new(@"C:\BMS\B1", Keys(100, 10)), new(@"C:\BMS\B2", Keys(100, 10))];
            SetIndex(library, index);
            Search(library);
            DuplicateGroup[] first = [.. library.SplitPlacementGroups!];
            Assert.AreEqual(2, first.Length);

            ChartFile current = library.BmsCharts.Single(chart => chart.Path.EndsWith(@"A1\a.bms", StringComparison.Ordinal));
            OwnedChartCollectionTestSupport.GetOwnedCollectionOwner(library).Commit(state => state.ApplyCurrentChartValue(current with { Title = "changed", RawTitle = "changed" }));
            SetIndex(library, index);
            Search(library);

            DuplicateGroup[] second = [.. library.SplitPlacementGroups!];
            Assert.AreSame(first[0], second[0]);
            Assert.AreSame(first[1], second[1]);
            Assert.AreEqual(first[0].Header, second[0].Header, "見出しは捕捉時のまま");

            library.BmsCharts = [.. library.BmsCharts, Chart(@"C:\BMS\B1\extra.bms", 99, "extra")];
            Search(library);

            DuplicateGroup[] third = [.. library.SplitPlacementGroups!];
            Assert.AreEqual(2, third.Length);
            DuplicateGroup unchanged = third.Single(group => group.Folders[0].EndsWith("A1", StringComparison.Ordinal));
            DuplicateGroup changed = third.Single(group => group.Folders[0].EndsWith("B1", StringComparison.Ordinal));
            Assert.AreSame(first.Single(group => group.Folders[0].EndsWith("A1", StringComparison.Ordinal)), unchanged);
            Assert.IsFalse(first.Contains(changed), "譜面が増えたグループは新しいインスタンス");
            Assert.AreEqual(3, changed.ChartFiles.Count);
        });
    }

    /// <summary>
    /// フォルダ集合の変化と、パスの大文字小文字だけの変化は、同じ識別の譜面でも新しいインスタンスにすることを確認します。
    /// </summary>
    [TestMethod]
    public void Search_CreatesNewInstanceWhenFolderSetOrPathCaseChanges()
    {
        ChartFile a1 = Chart(@"C:\BMS\A1\a.bms", 1), a2 = Chart(@"C:\BMS\A2\a.bms", 2);
        Run([a1, a2], library =>
        {
            IndexFolder[] index = [new(@"C:\BMS\A1", Keys(1, 10)), new(@"C:\BMS\A2", Keys(1, 10)), new(@"C:\BMS\A3", Keys(1, 10))];
            SetIndex(library, index);
            Search(library);
            DuplicateGroup first = library.SplitPlacementGroups!.Single();

            ChartFile current = library.BmsCharts.Single(chart => chart.Path.EndsWith(@"A2\a.bms", StringComparison.Ordinal));
            library.BmsCharts = [library.BmsCharts.Single(chart => chart.Path.EndsWith(@"A1\a.bms", StringComparison.Ordinal)),
                current with { Path = @"C:\BMS\a2\a.bms" }];
            Assert.AreSame(current.Token, library.BmsCharts.Single(chart => chart.Path.EndsWith(@"a2\a.bms", StringComparison.Ordinal)).Token, "識別が保たれる前提");
            Search(library);

            DuplicateGroup caseChanged = library.SplitPlacementGroups!.Single();
            Assert.AreNotSame(first, caseChanged, "パスの大文字小文字だけの変化");

            library.BmsCharts = [.. library.BmsCharts, Chart(@"C:\BMS\A3\a.bms", 3)];
            Search(library);

            DuplicateGroup grown = library.SplitPlacementGroups!.Single();
            Assert.AreNotSame(caseChanged, grown, "フォルダ集合の変化");
            Assert.AreEqual(3, grown.Folders.Count);
        });
    }

    /// <summary>
    /// 実走査のプレビュー名を確認します。直下の preview・PREVIEW・_Preview・__preview（拡張子は問わない）だけを除き、
    /// preview2・x_preview・___preview・サブフォルダの preview は残します。除外の有無で各フォルダが9個か10個になります。
    /// </summary>
    [TestMethod]
    public void Search_ExcludesOnlyDirectPreviewNamesFromRealScan()
    {
        WithTemporarySongDb(databaseScope, songDbPath =>
        {
            using var workspace = TestWorkspace.Create(TestRunDirectory.RootDirectory);
            string root = Path.Combine(workspace.RootDirectory, "Library");
            string[] normal = [.. Enumerable.Range(1, 9).Select(i => $"s{i}.wav")];
            void Make(string name, params string[] extra)
            {
                string directory = Path.Combine(root, name);
                Directory.CreateDirectory(directory);
                File.WriteAllText(Path.Combine(directory, "chart.bms"), "#TITLE " + name + "\n#ARTIST x\n");
                foreach (string file in normal.Concat(extra))
                {
                    string target = Path.Combine(directory, file);
                    Directory.CreateDirectory(Path.GetDirectoryName(target)!);
                    File.WriteAllText(target, "audio");
                }
            }
            Make("X1", "preview.wav", "PREVIEW.ogg", "_Preview.wav", "__preview.mp3");
            Make("X2", "preview.wav", "PREVIEW.ogg", "_Preview.wav", "__preview.mp3");
            Make("Y1", "preview2.wav");
            Make("Z1", "x_preview.wav");
            Make("Z2", "___preview.wav");
            Make("W1", Path.Combine("sub", "preview.wav"));
            ChartFile[] owned = [.. Directory.EnumerateFiles(root, "*.bms", SearchOption.AllDirectories).Select(path => BmsChartFileParser.ParseSnapshot(ChartFileContentReader.ReadSnapshot(path)))];
            var library = new TestBmsLibrary(databaseScope, songDbPath, null, null, new TestFileMutationService(), new RecordingDialogService(),
                new TestUiScheduler(() => null), () => new BmsLibraryOptionsSnapshot { OperationModeLR2DB = false, BMSInstallDir = root },
                settings: TestBmsFactory.CreateRootSettings([root]))
            { BmsCharts = owned, BmsonCharts = [] };
            try
            {
                LibraryResourceIndexTestSupport.GetOwner(library).Replace(LibraryResourceIndex.CreateFromScanResult(ChartDirectoryScanBuilder.BuildFromRoots([root])));

                Search(library);

                string[] names = [.. library.SplitPlacementGroups!.Single().Folders.Select(folder => Path.GetFileName(folder))];
                CollectionAssert.AreEqual(new[] { "W1", "Y1", "Z1", "Z2" }, names);
            }
            finally { library.RequestShutdown("split-placement-scan"); }
        });
    }
}
