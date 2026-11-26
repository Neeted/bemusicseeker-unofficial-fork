using BeMusicSeeker.Models;
using BeMusicSeeker.Models.BmsLibraryInternal;
using BeMusicSeeker.Models.LR2;
using BeMusicSeeker.Models.Utils;
using BeMusicSeeker.Properties;
using BeMusicSeeker.TestSupport.Components;
using BeMusicSeeker.TestSupport.Files;
using static BeMusicSeeker.TestSupport.Components.BmsLibraryDuplicateServiceTestSupport;
using static BeMusicSeeker.TestSupport.Components.SplitPlacementTestSupport;

namespace BeMusicSeeker.Tests;

/// <summary>
/// 重複音源の探索の入口（捕捉、組の対象フォルダへの割当て、結果の組立て・保持・通知・失効・引継ぎ）を、実ライブラリと
/// TestWorkspace の実ファイルで確認します。列挙は <see cref="FastRootFileEnumerator"/> を明示し、外部の Everything に依存しません。
/// 集計規則の条件表と経路の選択は <see cref="WavOggCollisionFinderTests"/> が担当します。
/// </summary>
[TestClass]
public sealed class BmsLibraryAudioDuplicateTests
{
    private static readonly string[] GateNames = ["rwlockBMSFilesInitializedMin", "rwlockBMSFiles", "rwlockDuplicateChartGroups"];

    private readonly ApplicationDatabaseScope databaseScope = new();

    /// <summary>ケースのワークスペースに <paramref name="files"/>（登録ルートからの相対パス）を作り、登録ルートを指す実ライブラリを渡します。</summary>
    private void Run(
        string[] files,
        Func<string, ChartFile[]> bms,
        Action<TestBmsLibrary, string> test,
        Func<string, ChartFile[]>? bmson = null,
        IRootFileEnumerator? enumerator = null,
        bool createRoot = true,
        bool addMissingRoot = false)
    {
        WithTemporarySongDb(databaseScope, songDbPath =>
        {
            using var workspace = TestWorkspace.Create(TestRunDirectory.RootDirectory);
            string root = Path.Combine(workspace.RootDirectory, "BMS");
            if (createRoot) { Directory.CreateDirectory(root); }
            foreach (string file in files)
            {
                string path = Path.Combine(root, file);
                Directory.CreateDirectory(Path.GetDirectoryName(path)!);
                File.WriteAllBytes(path, []);
            }
            string[] roots = addMissingRoot ? [root, Path.Combine(workspace.RootDirectory, "Missing")] : [root];
            var library = new TestBmsLibrary(databaseScope, songDbPath, null, null, new TestFileMutationService(), new RecordingDialogService(),
                new TestUiScheduler(() => null), optionsSnapshotProvider: null,
                settings: TestBmsFactory.CreateRootSettings(roots), rootFileEnumerator: enumerator ?? new FastRootFileEnumerator())
            { BmsCharts = bms(root), BmsonCharts = bmson?.Invoke(root) ?? [] };
            try { test(library, root); }
            finally { library.RequestShutdown("audio-duplicates"); }
        });
    }

    private static string P(string root, string relative) => Path.Combine(root, relative);

    private static void Search(
        BMSLibrary library, IProgress<AudioDuplicateSearchProgress>? progress = null, CancellationToken token = default)
        => library.SearchAudioDuplicateFolders(progress!, token);

    private static string Rel(string root, string folder) => folder.Length == root.Length ? "." : folder[(root.Length + 1)..];

    private static string[] Describe(BMSLibrary library, string root)
        => [.. library.AudioDuplicateFolders!.Select(folder => Rel(root, folder.Folder) + ": " + string.Join(" | ", folder.CollisionNames))];

    private sealed class Recorder : IProgress<AudioDuplicateSearchProgress>
    {
        internal List<AudioDuplicateSearchStage> Stages { get; } = [];
        public void Report(AudioDuplicateSearchProgress value) { lock (Stages) { Stages.Add(value.Stage); } }
    }

    private sealed class Delegating(Action<AudioDuplicateSearchProgress> action) : IProgress<AudioDuplicateSearchProgress>
    {
        public void Report(AudioDuplicateSearchProgress value) => action(value);
    }

    /// <summary>管理側の列挙を委譲し、指示があれば列挙を失敗させる列挙役です。</summary>
    private sealed class SwitchableEnumerator : IRootFileEnumerator
    {
        private readonly FastRootFileEnumerator inner = new();
        internal bool Fail { get; set; }
        internal int Calls { get; private set; }

        public RootFileEnumerationResult EnumerateFiles(IEnumerable<string> rootDirectories, IEnumerable<RootFileEnumerationGroup> groups, bool verboseLog = false)
        {
            Calls++;
            if (!Fail) { return inner.EnumerateFiles(rootDirectories, groups, verboseLog); }
            var result = new RootFileEnumerationResult { BackendName = "switchable" };
            result.MarkFailed("switchable_failure");
            return result;
        }
    }

    /// <summary>結果変更通知の配送スレッドと、そのときのライブラリのロックの保持を記録します。</summary>
    private sealed class NotificationLog
    {
        internal List<(int ThreadId, string[] Holds, List<AudioDuplicateFolder>? Folders)> Entries { get; } = [];
        internal ManualResetEventSlim Signal { get; } = new();

        internal NotificationLog(BMSLibrary library)
        {
            library.PropertyChanged += (_, args) =>
            {
                if (args.PropertyName != "AudioDuplicateFolders") { return; }
                lock (Entries)
                {
                    Entries.Add((Environment.CurrentManagedThreadId,
                        [.. GateNames.SelectMany(name => InstallServiceComponentTestSupport.CaptureLibraryGateHoldsOnCurrentThread(library, name).Select(hold => name + "の" + hold))],
                        library.AudioDuplicateFolders));
                }
                Signal.Set();
            };
        }

        internal int Count { get { lock (Entries) { return Entries.Count; } } }
    }

    /// <summary>探索前は結果なし（null）で、有効な結果がない状態を空の一覧と取り違えないことを確認します。</summary>
    [TestMethod]
    public void AudioDuplicateFolders_IsNullBeforeAnySearch()
    {
        Run([], root => [Chart(P(root, @"A\a.bms"), 1)], (library, _) =>
        {
            Assert.IsNull(library.AudioDuplicateFolders);
            Assert.IsFalse(library.HasCurrentAudioDuplicateFolders);
        });
    }

    /// <summary>
    /// 保証：同じディレクトリで拡張子なしの名前が大文字小文字を無視して一致する WAV と OGG が衝突名になり、
    /// 大文字の拡張子・複数ピリオドの名前・mp3/flac が並ぶ名前は組になり、空の名前・片方だけ・別のディレクトリ・WAV という名前のディレクトリは組にならない。
    /// 衝突名は対象フォルダからの相対ディレクトリと名前を <c>\</c> でつなぎ、大文字小文字を無視した昇順に並ぶ。
    /// 検出する誤り：列挙した実ファイルから集計への接続の欠落、相対の書式・並びの誤り。
    /// </summary>
    [TestMethod]
    public void Search_CollectsCollisionNamesFromRealFilesByTheCollisionRule()
    {
        string[] files =
        [
            @"A\a.bms", @"A\Foo.wav", @"A\foo.ogg", @"A\UP.WAV", @"A\up.OGG", @"A\foo.v2.wav", @"A\foo.v2.ogg",
            @"A\m.wav", @"A\m.ogg", @"A\m.mp3", @"A\m.flac", @"A\.wav", @"A\.ogg", @"A\only.wav", @"A\solo.ogg",
            @"A\x.wav\z.wav", @"A\x.ogg", @"A\sub\s.wav", @"A\other\s.ogg", @"A\sub\t.wav", @"A\sub\t.ogg",
        ];
        Run(files, root => [Chart(P(root, @"A\a.bms"), 1, "A")], (library, root) =>
        {
            Search(library);

            CollectionAssert.AreEqual(new[] { @"A: Foo | foo.v2 | m | sub\t | UP" }, Describe(library, root));
        });
    }

    /// <summary>
    /// 保証：組のあるディレクトリは、自身から祖先へたどって最初に見つかる対象フォルダへ割り当てる（自身・配下・内側の対象フォルダの分は内側）。
    /// 祖先に対象フォルダがないディレクトリの組は含めず、BMSON だけを直下に持つフォルダも対象フォルダになる。
    /// 並びは見出しの序数順で、同じ見出しはフォルダのパスの昇順。
    /// 検出する誤り：配下の組を外側へ割り当てる、対象フォルダのない組の混入、BMSON だけのフォルダの取りこぼし、並びの誤り。
    /// </summary>
    [TestMethod]
    public void Search_AssignsCollisionsToNearestTargetFolderAndOrdersByHeaderThenPath()
    {
        string[] files =
        [
            @"Own\own.bms", @"Own\a.wav", @"Own\a.ogg", @"Own\Sub\b.wav", @"Own\Sub\b.ogg",
            @"Own\Inner\in.bms", @"Own\Inner\c.wav", @"Own\Inner\c.ogg", @"Own\Inner\Deep\d.wav", @"Own\Inner\Deep\d.ogg",
            @"NoChart\n.wav", @"NoChart\n.ogg",
            @"BmsonOnly\x.bmson", @"BmsonOnly\y.wav", @"BmsonOnly\y.ogg",
        ];
        Run(files,
            root => [Chart(P(root, @"Own\own.bms"), 1, "Beta"), Chart(P(root, @"Own\Inner\in.bms"), 2, "Alpha")],
            (library, root) =>
            {
                Search(library);

                CollectionAssert.AreEqual(
                    new[] { @"BmsonOnly: y", @"Own\Inner: c | Deep\d", @"Own: a | Sub\b" }, Describe(library, root));
                CollectionAssert.AreEqual(new[] { "Alpha", "Alpha", "Beta" }, library.AudioDuplicateFolders!.Select(folder => folder.Group.Header).ToArray());
            },
            bmson: root => [Chart(P(root, @"BmsonOnly\x.bmson"), 3, "Alpha", ChartFileKind.Bmson)]);
    }

    /// <summary>
    /// 保証：結果の一件は一フォルダの <c>DuplicateGroup</c>（フォルダは一件、譜面はフォルダ直下の所持譜面すべてを捕捉した行の順、見出しは先頭の題名）で、
    /// フォルダの表記は所持集合の行で最初に現れた表記、探索の後も譜面の警告・カタログ・重複検索の結果は変わらない。
    /// 検出する誤り：子フォルダの譜面の混入、行の順・表記の誤り、探索による警告や重複検索の結果の変更。
    /// </summary>
    [TestMethod]
    public void Search_BuildsOneFolderGroupFromCapturedRowsAndChangesNothingElse()
    {
        string[] files = [@"Song\a.bms", @"Song\k.wav", @"Song\k.ogg"];
        Run(files, root =>
        [
            Chart(P(root, @"Song\b.bms"), 1, "Second") with { Warnings = [ChartWarning.Create(ChartWarningKind.DuplicateChart)] },
            Chart(P(root, @"Song\a.bms"), 1, "First"),
            Chart(P(root, @"SONG\c.bms"), 1, "Third"),
            Chart(P(root, @"Song\Sub\s.bms"), 4, "Sub"),
            Chart(P(root, @"Elsewhere\e.bms"), 1, "Dup"),
        ], (library, root) =>
        {
            library.SearchDuplicateChartGroups();
            List<DuplicateGroup>? duplicates = library.DuplicateChartGroups;
            int revision = library.CatalogRevision;
            ChartFile[] before = [.. library.BmsCharts];

            Search(library);

            DuplicateGroup group = library.AudioDuplicateFolders!.Single().Group;
            Assert.AreEqual(1, group.Folders.Count);
            Assert.AreEqual(P(root, "Song"), group.Folders[0], "最初に現れた表記");
            Assert.AreEqual(group.ChartFiles[0].Title, group.Header, "先頭の譜面の題名");
            CollectionAssert.AreEquivalent(new[] { "a.bms", "b.bms", "c.bms" }, group.ChartFiles.Select(chart => Path.GetFileName(chart.Path)).ToArray(), "直下の譜面だけ（子フォルダの譜面を含めない）");
            // 同じライブラリの重複検索が同じフォルダについて返す譜面の順（同じ所持集合の行の順）と一致する。
            string[] duplicateOrder = [.. library.DuplicateChartGroups!.Single().ChartFiles
                .Where(chart => string.Equals(Path.GetDirectoryName(chart.Path), group.Folders[0], StringComparison.OrdinalIgnoreCase))
                .Select(chart => chart.Path)];
            CollectionAssert.AreEqual(duplicateOrder, group.ChartFiles.Select(chart => chart.Path).ToArray(), "捕捉した行の順");
            Assert.IsTrue(group.ChartFiles[0].Warnings.Any(warning => warning.Kind == ChartWarningKind.DuplicateChart), "捕捉時の警告のまま");
            Assert.AreEqual(revision, library.CatalogRevision);
            Assert.AreSame(duplicates, library.DuplicateChartGroups, "重複検索の結果を変えない");
            CollectionAssert.AreEqual(before, library.BmsCharts.ToArray(), "譜面と警告を変えない");
        });
    }

    /// <summary>0件の結果は空の一覧（nullでない）で有効な結果になり、次の呼出しでは探索も通知も進捗もないことを確認します。</summary>
    [TestMethod]
    public void Search_StoresEmptyListAndReusesValidResultWithoutNotifying()
    {
        Run([@"A\a.bms", @"A\only.wav"], root => [Chart(P(root, @"A\a.bms"), 1)], (library, _) =>
        {
            var log = new NotificationLog(library);

            Search(library);

            Assert.IsNotNull(library.AudioDuplicateFolders);
            Assert.AreEqual(0, library.AudioDuplicateFolders.Count);
            Assert.IsTrue(library.HasCurrentAudioDuplicateFolders);
            Assert.AreEqual(1, log.Count);
            List<AudioDuplicateFolder> empty = library.AudioDuplicateFolders;
            var progress = new Recorder();

            Search(library, progress);

            Assert.AreSame(empty, library.AudioDuplicateFolders);
            Assert.AreEqual(1, log.Count, "有効な結果では通知しない");
            Assert.AreEqual(0, progress.Stages.Count, "有効な結果では進捗を報告しない");
        });
    }

    /// <summary>
    /// 新しい結果の格納で、戻る前に呼出しスレッドで、ライブラリのロックを持たずに変更を一回通知し、通知時点で同じ一覧を読めること、
    /// 進捗が列挙、集計の段階だけであることを確認します。
    /// </summary>
    [TestMethod]
    public void Search_PublishesOnceOnCallerWithoutLocksAndReportsOnlyStages()
    {
        Run([@"A\a.bms", @"A\k.wav", @"A\k.ogg"], root => [Chart(P(root, @"A\a.bms"), 1)], (library, _) =>
        {
            var log = new NotificationLog(library);
            var progress = new Recorder();
            int caller = Environment.CurrentManagedThreadId;

            Search(library, progress);

            Assert.AreEqual(1, log.Count);
            Assert.AreEqual(caller, log.Entries[0].ThreadId);
            Assert.AreEqual(0, log.Entries[0].Holds.Length, string.Join("、", log.Entries[0].Holds));
            Assert.AreSame(library.AudioDuplicateFolders, log.Entries[0].Folders);
            CollectionAssert.AreEqual(new[] { AudioDuplicateSearchStage.Enumerate, AudioDuplicateSearchStage.Aggregate }, progress.Stages);
        });
    }

    /// <summary>
    /// 列挙と集計の間（進捗の受け手の中）でライブラリのロックを保持せず、別スレッドの重複検索が完了できることを確認します。
    /// </summary>
    [TestMethod]
    public void Search_DoesNotHoldLibraryLocksWhileEnumeratingAndAggregating()
    {
        Run([@"A\a.bms", @"A\k.wav", @"A\k.ogg"], root => [Chart(P(root, @"A\a.bms"), 1)], (library, _) =>
        {
            Task? probe = null;
            var progress = new Delegating(value =>
            {
                if (value.Stage != AudioDuplicateSearchStage.Aggregate || probe != null) { return; }
                probe = ConcurrencyProbe.StartDedicated(library.SearchDuplicateChartGroups);
                ConcurrencyProbe.AssertCompletesWhileHeld(probe, "集計中の重複検索");
            });
            try { Search(library, progress); }
            finally { probe?.GetAwaiter().GetResult(); }

            Assert.IsNotNull(probe);
            Assert.AreEqual(1, library.AudioDuplicateFolders!.Count);
        });
    }

    /// <summary>
    /// 取消（事前・列挙の段階・集計の段階）は <see cref="OperationCanceledException"/>、進捗の受け手の例外は探索の失敗になり、
    /// どれも結果・通知を変えず、次の成功した探索が取消の前の基底からインスタンスを引き継ぐことを確認します。
    /// 検出する誤り：途中の結果の公開、状態の更新、基底の消失。
    /// </summary>
    [TestMethod]
    public void Search_CancellationAndReceiverFailureKeepStateAndBase()
    {
        Run([@"A\a.bms", @"A\k.wav", @"A\k.ogg"], root => [Chart(P(root, @"A\a.bms"), 1)], (library, _) =>
        {
            Search(library);
            AudioDuplicateFolder first = library.AudioDuplicateFolders!.Single();
            List<AudioDuplicateFolder> firstList = library.AudioDuplicateFolders;
            var log = new NotificationLog(library);

            // 索引の世代を進めて、有効な結果を失効扱いにする。取消・失敗の後も結果の一覧は変わらない。
            SetIndex(library);
            using (var before = new CancellationTokenSource())
            {
                before.Cancel();
                Assert.ThrowsExactly<OperationCanceledException>(() => Search(library, null, before.Token));
            }
            foreach (AudioDuplicateSearchStage stage in new[] { AudioDuplicateSearchStage.Enumerate, AudioDuplicateSearchStage.Aggregate })
            {
                using var during = new CancellationTokenSource();
                Assert.ThrowsExactly<OperationCanceledException>(() =>
                    Search(library, new Delegating(value => { if (value.Stage == stage) { during.Cancel(); } }), during.Token), stage.ToString());
            }
            Assert.ThrowsExactly<InvalidOperationException>(() => Search(library, new Delegating(_ => throw new InvalidOperationException("receiver"))));

            Assert.AreSame(firstList, library.AudioDuplicateFolders, "取消・失敗で結果を変えない");
            Assert.IsFalse(library.HasCurrentAudioDuplicateFolders, "世代は変わらず失効のまま");
            Assert.AreEqual(0, log.Count, "取消・失敗で通知しない");

            Search(library);

            Assert.AreNotSame(firstList, library.AudioDuplicateFolders);
            Assert.AreSame(first.Group, library.AudioDuplicateFolders!.Single().Group, "取消の前の基底からインスタンスを引き継ぐ");
            Assert.AreEqual(1, log.Count);
        });
    }

    /// <summary>
    /// 管理側の列挙が不完全に終わった場合は、結果を公開せず、保持している結果・通知・基底を変えずに探索の失敗になり、
    /// 列挙が回復すれば次の探索で基底のインスタンスを引き継ぐことを確認します。
    /// </summary>
    [TestMethod]
    public void Search_EnumerationFailureThrowsAndKeepsPreviousResultAndBase()
    {
        var enumerator = new SwitchableEnumerator();
        Run([@"A\a.bms", @"A\k.wav", @"A\k.ogg"], root => [Chart(P(root, @"A\a.bms"), 1)], (library, _) =>
        {
            Search(library);
            List<AudioDuplicateFolder> firstList = library.AudioDuplicateFolders!;
            AudioDuplicateFolder first = firstList.Single();
            var log = new NotificationLog(library);
            SetIndex(library);
            enumerator.Fail = true;

            Assert.ThrowsExactly<InvalidOperationException>(() => Search(library));

            Assert.AreSame(firstList, library.AudioDuplicateFolders);
            Assert.IsFalse(library.HasCurrentAudioDuplicateFolders);
            Assert.AreEqual(0, log.Count);
            enumerator.Fail = false;

            Search(library);

            Assert.AreSame(first.Group, library.AudioDuplicateFolders!.Single().Group);
        }, enumerator: enumerator);
    }

    /// <summary>
    /// 管理側の列挙が登録ルートの不存在で不完全になる場合（登録の一部、または全部が存在しない場合）は探索の失敗で、結果を公開しないことを確認します。
    /// 存在しない登録ルートを、存在するルートだけの成功へ縮約する誤りを検出します。
    /// </summary>
    [TestMethod]
    [DataRow(false, false)]
    [DataRow(true, true)]
    public void Search_FailsWhenARegisteredRootDoesNotExist(bool createRoot, bool addMissingRoot)
    {
        Run([], root => [Chart(P(root, @"A\a.bms"), 1)], (library, _) =>
        {
            var log = new NotificationLog(library);

            Assert.ThrowsExactly<InvalidOperationException>(() => Search(library));

            Assert.IsNull(library.AudioDuplicateFolders);
            Assert.IsFalse(library.HasCurrentAudioDuplicateFolders);
            Assert.AreEqual(0, log.Count);
        }, createRoot: createRoot, addMissingRoot: addMissingRoot);
    }

    /// <summary>
    /// 保証：存在しない登録ルートのうち、ライブラリの更新前検査が存在を必須とするもの（出力基点の配下を除く登録ルート）は探索の失敗になり、
    /// 更新前検査が必須としないもの（LR2 の出力基点の配下の未作成のルート）は範囲から外れて探索が成功する。
    /// 検出する誤り：存在しない登録をすべて失敗にする、または必須のルートの不存在を成功へ縮約する。
    /// </summary>
    [TestMethod]
    [DataRow(false, false)]
    [DataRow(true, true)]
    public void Search_TreatsMissingRootAsFailureOnlyWhenPreflightRequiresIt(bool missingRootIsUnderOutputBase, bool expectsSuccess)
    {
        WithTemporarySongDb(databaseScope, songDbPath =>
        {
            using var workspace = TestWorkspace.Create(TestRunDirectory.RootDirectory);
            string root = Path.Combine(workspace.RootDirectory, "BMS");
            string outputBase = Path.Combine(workspace.RootDirectory, "OutputBase");
            Directory.CreateDirectory(root);
            Directory.CreateDirectory(outputBase);
            string missing = missingRootIsUnderOutputBase ? Path.Combine(outputBase, "NotCreated") : Path.Combine(workspace.RootDirectory, "Missing");
            File.WriteAllBytes(Path.Combine(root, "a.bms"), []);
            File.WriteAllBytes(Path.Combine(root, "k.wav"), []);
            File.WriteAllBytes(Path.Combine(root, "k.ogg"), []);
            Settings settings = TestSettingsFactory.CreateIsolatedSettings(values => values.OperationModeLR2DB = true);
            settings.LR2CustomFolderOutputBaseDir = outputBase;
            var library = new TestBmsLibrary(databaseScope, songDbPath, null, null, new TestFileMutationService(), new RecordingDialogService(),
                new TestUiScheduler(() => null), optionsSnapshotProvider: () => BmsLibraryOptionsSnapshot.CreateCurrent(settings),
                settings: TestBmsFactory.CreateRootSettings([root, missing], settings), rootFileEnumerator: new FastRootFileEnumerator())
            { BmsCharts = [Chart(Path.Combine(root, "a.bms"), 1)], BmsonCharts = [] };
            try
            {
                if (expectsSuccess)
                {
                    Search(library);
                    Assert.AreEqual(1, library.AudioDuplicateFolders!.Count);
                }
                else
                {
                    Assert.ThrowsExactly<InvalidOperationException>(() => Search(library));
                    Assert.IsNull(library.AudioDuplicateFolders);
                }
            }
            finally { library.RequestShutdown("audio-duplicates-preflight-roots"); }
        });
    }

    /// <summary>全置換（所持集合の差し替え）で、結果が失効してnullになり、変更通知が届くことを確認します。重複検索の結果がnullのままでも失効します。</summary>
    [TestMethod]
    public void Invalidation_FullBmsReplacementClearsResultAndNotifies()
    {
        Run([@"A\a.bms", @"A\k.wav", @"A\k.ogg"], root => [Chart(P(root, @"A\a.bms"), 1)], (library, root) =>
        {
            Search(library);
            Assert.IsNull(library.DuplicateChartGroups);
            var log = new NotificationLog(library);

            library.BmsCharts = [Chart(P(root, @"A\a.bms"), 1)];

            WaitForInvalidation(library, log);
        });
    }

    /// <summary>譜面の削除の本番の変更経路で、探索済みの結果が失効し変更通知が届くことを確認します（実ファイル）。</summary>
    [TestMethod]
    public void Invalidation_ChartRemovalClearsResultAndNotifies()
    {
        string[] files = [@"A\a.bmson", @"A\k.wav", @"A\k.ogg", @"B\b.bmson"];
        Run(files, root => [], (library, root) =>
        {
            ChartFile target = Chart(P(root, @"A\a.bmson"), 1, kind: ChartFileKind.Bmson) with { Folder = P(root, "A") };
            library.BmsonCharts = [target, Chart(P(root, @"B\b.bmson"), 2, kind: ChartFileKind.Bmson) with { Folder = P(root, "B") }];
            Search(library);
            Assert.AreEqual(1, library.AudioDuplicateFolders!.Count);
            var log = new NotificationLog(library);

            LibraryChartRemovalOutcome outcome = library.RemoveLibraryCharts(
                library.PrepareLibraryChartRemoval([LibraryChartRef.FromChartFile(target)]), sendToRecycleBin: false, approvedWholeFolderDeletePaths: [], notices: new LibraryOperationNotices());
            Assert.AreEqual(1, outcome.ConfirmedChartCount);

            WaitForInvalidation(library, log);
        });
    }

    /// <summary>フォルダの統合の本番の変更経路で、探索済みの結果が失効し変更通知が届くことを確認します（実ファイル、実SQLite）。</summary>
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
            File.WriteAllBytes(Path.Combine(source, "k.wav"), []);
            File.WriteAllBytes(Path.Combine(source, "k.ogg"), []);
            ChartFile[] owned = [.. Directory.EnumerateFiles(root, "*.bms", SearchOption.AllDirectories).Select(path => BmsChartFileParser.ParseSnapshot(ChartFileContentReader.ReadSnapshot(path)))];
            var library = new TestBmsLibrary(databaseScope, songDbPath, null, null, new TestFileMutationService(), new RecordingDialogService(),
                new TestUiScheduler(() => null), () => new BmsLibraryOptionsSnapshot { OperationModeLR2DB = false, BMSInstallDir = root, StandaloneBmsRootPaths = [root], DeletePendingPackageSourceAfterInstall = false },
                settings: TestBmsFactory.CreateRootSettings([root]), rootFileEnumerator: new FastRootFileEnumerator())
            { BmsCharts = owned, BmsonCharts = [] };
            try
            {
                BmsLibraryInitializationTestSupport.ExecuteSongDbFixtureTransaction(databaseScope, songDbPath, db =>
                { foreach (ChartFile chart in library.BmsCharts) { db.InsertOrReplace(ChartSongStorageMapping.ToBmsRow(chart), typeof(LR2SongDB.song)); } });
                Search(library);
                Assert.AreEqual(1, library.AudioDuplicateFolders!.Count);
                var log = new NotificationLog(library);

                DuplicateMergeMaintenanceReceipt merged = library.MergeChartDirectory(source, destination, notices: new LibraryOperationNotices(), operationId: 1);
                Assert.IsTrue(merged.MergeApplied);

                WaitForInvalidation(library, log);
            }
            finally { library.RequestShutdown("audio-duplicates-merge"); }
        });
    }

    private static void WaitForInvalidation(BMSLibrary library, NotificationLog log)
    {
        Task never = new TaskCompletionSource().Task;
        ConcurrencyProbe.WaitForSignal(log.Signal, never, "重複音源の失効通知");
        Assert.IsNull(library.AudioDuplicateFolders);
        Assert.IsFalse(library.HasCurrentAudioDuplicateFolders);
        Assert.IsTrue(log.Count >= 1);
    }

    /// <summary>
    /// 画面が探索の要否を読む <c>HasCurrentAudioDuplicateFolders</c> が、探索前は偽、探索後は0件を含めて真、リソース索引の世代が進むと偽（結果は保持）、
    /// 次の要求の探索で真になり、失効後も基底のインスタンスを引き継ぐことを確認します。
    /// 検出する誤り：結果の有無と世代の一致のどちらか一方だけを見る、世代の違う結果での探索の省略。
    /// </summary>
    [TestMethod]
    public void HasCurrentAudioDuplicateFolders_FollowsResultPresenceAndIndexGeneration()
    {
        Run([@"A\a.bms", @"A\k.wav", @"A\k.ogg"], root => [Chart(P(root, @"A\a.bms"), 1)], (library, root) =>
        {
            Assert.IsFalse(library.HasCurrentAudioDuplicateFolders, "探索前");

            Search(library);
            AudioDuplicateFolder first = library.AudioDuplicateFolders!.Single();
            Assert.IsTrue(library.HasCurrentAudioDuplicateFolders, "探索後");

            SetIndex(library);
            Assert.IsNotNull(library.AudioDuplicateFolders, "世代が変わっても結果は保持する");
            Assert.IsFalse(library.HasCurrentAudioDuplicateFolders, "世代の変化");

            Search(library);
            Assert.IsTrue(library.HasCurrentAudioDuplicateFolders, "再探索");
            Assert.AreSame(first.Group, library.AudioDuplicateFolders!.Single().Group, "世代だけが違う再探索は基底のインスタンスを引き継ぐ");

            library.BmsCharts = [Chart(P(root, @"A\a.bms"), 1)];
            Assert.IsFalse(library.HasCurrentAudioDuplicateFolders, "所持集合の失効");
            Search(library);
            Assert.AreNotSame(first.Group, library.AudioDuplicateFolders!.Single().Group, "譜面の所持項目の識別が違えば新しいインスタンス");
        });
    }

    /// <summary>
    /// 再探索で、フォルダ・譜面の識別とパスと順・衝突名の集合がすべて一致するフォルダだけが同じインスタンスのまま捕捉時の見出しを保ち、
    /// 衝突名が増えたフォルダと譜面が増えたフォルダは新しいインスタンスになり、衝突名の一覧は新しい探索のものになることを確認します。
    /// </summary>
    [TestMethod]
    public void Search_InheritsInstancesOfUnchangedFoldersOnly()
    {
        string[] files =
        [
            @"A\a.bms", @"A\c.wav", @"A\c.ogg", @"B\b.bms", @"B\d.wav", @"B\d.ogg", @"C\c.bms", @"C\f.wav", @"C\f.ogg",
        ];
        Run(files, root =>
        [
            Chart(P(root, @"A\a.bms"), 1, "first"), Chart(P(root, @"B\b.bms"), 2, "second"), Chart(P(root, @"C\c.bms"), 3, "third"),
        ], (library, root) =>
        {
            Search(library);
            var first = library.AudioDuplicateFolders!.ToDictionary(folder => Rel(root, folder.Folder));
            Assert.AreEqual(3, first.Count);

            // A の衝突名を増やし、B の題名だけを変え、C に譜面を足す。
            File.WriteAllBytes(P(root, @"A\e.wav"), []);
            File.WriteAllBytes(P(root, @"A\e.ogg"), []);
            ChartFile current = library.BmsCharts.Single(chart => chart.Path.EndsWith(@"B\b.bms", StringComparison.Ordinal));
            OwnedChartCollectionTestSupport.GetOwnedCollectionOwner(library).Commit(state => state.ApplyCurrentChartValue(current with { Title = "changed", RawTitle = "changed" }));
            SetIndex(library);
            Search(library);
            var second = library.AudioDuplicateFolders!.ToDictionary(folder => Rel(root, folder.Folder));

            Assert.AreNotSame(first["A"].Group, second["A"].Group, "衝突名の集合が変わった");
            CollectionAssert.AreEqual(new[] { "c", "e" }, second["A"].CollisionNames.ToArray());
            Assert.AreSame(first["B"].Group, second["B"].Group, "構成が同じなら同じインスタンス");
            Assert.AreEqual("second", second["B"].Group.Header, "見出しは捕捉時のまま");
            Assert.AreSame(first["C"].Group, second["C"].Group);

            library.BmsCharts = [.. library.BmsCharts, Chart(P(root, @"C\extra.bms"), 99, "extra")];
            Search(library);
            var third = library.AudioDuplicateFolders!.ToDictionary(folder => Rel(root, folder.Folder));

            Assert.AreNotSame(second["C"].Group, third["C"].Group, "譜面が増えた");
            Assert.AreEqual(2, third["C"].Group.ChartFiles.Count);
        });
    }
}
