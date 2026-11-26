using BeMusicSeeker.Models;
using BeMusicSeeker.Models.Utils;
using BeMusicSeeker.Properties;
using BeMusicSeeker.TestSupport.Components;
using BeMusicSeeker.TestSupport.Files;
using BeMusicSeeker.ViewModels;
using static BeMusicSeeker.TestSupport.Components.BmsLibraryDuplicateServiceTestSupport;
using static BeMusicSeeker.TestSupport.Components.SelectedChartAudioConversionWorkflowOwnerTestSupport;
using static BeMusicSeeker.TestSupport.Components.SplitPlacementTestSupport;

namespace BeMusicSeeker.Tests;

/// <summary>
/// 重複音源のツリーを選んだときの探索の手順（有効な結果の再利用、受付の取得と保持、不定進捗、取消、失敗、遷移の一回の呼出し）を、
/// 実ライブラリ・本番の受付（<see cref="ChartFileOperationSynchronizer"/>）・実際の管理主体で確認します。
/// 一覧の中身は遷移の先が結果から決めるため、ここでは遷移の呼出しの回数・時点・文脈を確認します。「UIスレッド」は専用の単一スレッドの同期の文脈で模します。
/// </summary>
[TestClass]
public sealed class AudioDuplicateSearchWorkflowOwnerTests
{
    private readonly ApplicationDatabaseScope databaseScope = new();

    /// <summary>遷移の呼出しの時点の観測です。</summary>
    private sealed record Navigation(DuplicateViewContext Context, bool AdmissionFree, int ThreadId, bool HasResult);

    /// <summary>管理側の列挙を委譲し、指示があれば列挙を失敗させ、列挙のスレッドを記録する列挙役です。</summary>
    private sealed class SwitchableEnumerator : IRootFileEnumerator
    {
        private readonly FastRootFileEnumerator inner = new();
        internal bool Fail { get; set; }
        internal int ThreadId { get; private set; }

        public RootFileEnumerationResult EnumerateFiles(IEnumerable<string> rootDirectories, IEnumerable<RootFileEnumerationGroup> groups, bool verboseLog = false)
        {
            ThreadId = Environment.CurrentManagedThreadId;
            if (!Fail) { return inner.EnumerateFiles(rootDirectories, groups, verboseLog); }
            var result = new RootFileEnumerationResult { BackendName = "switchable" };
            result.MarkFailed("switchable_failure");
            return result;
        }
    }

    private sealed class Fixture : IDisposable
    {
        private readonly SingleThreadSynchronizationContext ui = new();

        internal Fixture(TestBmsLibrary library, SwitchableEnumerator enumerator)
        {
            Library = library;
            Enumerator = enumerator;
            Dialogs = new SelectedChartAudioConversionWorkflowOwnerTestSupport.RecordingDialogService(Events);
            Workflow = new AudioDuplicateSearchWorkflowOwner(
                () => Library, Admission, Dialogs, Navigate,
                (library, progress, token) => (SearchOverride ?? ((l, p, t) => l.SearchAudioDuplicateFolders(p, t)))(library, progress, token));
            UiThreadId = ui.RunAsync(() => Task.FromResult(Environment.CurrentManagedThreadId)).GetAwaiter().GetResult();
        }

        internal TestBmsLibrary Library { get; }

        internal SwitchableEnumerator Enumerator { get; }

        /// <summary>指定すると、探索の呼出しをこの処理へ差し替えます。取消の合図が探索へ届く順序を、待合せで決めるために使います。</summary>
        internal Action<BMSLibrary, IProgress<AudioDuplicateSearchProgress>, CancellationToken>? SearchOverride { get; set; }

        internal EventLog Events { get; } = new();

        internal SelectedChartAudioConversionWorkflowOwnerTestSupport.RecordingDialogService Dialogs { get; }

        internal ChartFileOperationSynchronizer Admission { get; } = new();

        internal AudioDuplicateSearchWorkflowOwner Workflow { get; }

        internal int UiThreadId { get; }

        internal List<Navigation> Navigations { get; } = [];

        private Task Navigate(DuplicateViewContext context)
        {
            lock (Navigations)
            {
                Navigations.Add(new Navigation(context, Admission.CanEnter, Environment.CurrentManagedThreadId, Library.HasCurrentAudioDuplicateFolders));
            }
            Events.Add("navigate");
            return Task.CompletedTask;
        }

        internal Task Select(object? parameter) => ui.RunAsync(async () =>
        {
            await Workflow.SelectAsync(parameter);
            return 0;
        });

        public void Dispose() => ui.Dispose();
    }

    /// <summary>ワークスペースに、WAVとOGGの組を持つ譜面フォルダ一つと、その譜面を所持する実ライブラリを作ります。</summary>
    private void Run(Action<Fixture> test)
    {
        WithTemporarySongDb(databaseScope, songDbPath =>
        {
            using var workspace = TestWorkspace.Create(TestRunDirectory.RootDirectory);
            string root = Path.Combine(workspace.RootDirectory, "BMS");
            string folder = Path.Combine(root, "A");
            Directory.CreateDirectory(folder);
            File.WriteAllBytes(Path.Combine(folder, "x.wav"), []);
            File.WriteAllBytes(Path.Combine(folder, "x.ogg"), []);
            var enumerator = new SwitchableEnumerator();
            var library = new TestBmsLibrary(databaseScope, songDbPath, null, null, new TestFileMutationService(), new BmsLibraryDuplicateServiceTestSupport.RecordingDialogService(),
                new TestUiScheduler(() => null), optionsSnapshotProvider: null,
                settings: TestBmsFactory.CreateRootSettings([root]), rootFileEnumerator: enumerator)
            { BmsCharts = [Chart(Path.Combine(folder, "a.bms"), 1)], BmsonCharts = [] };
            try
            {
                using var fixture = new Fixture(library, enumerator);
                test(fixture);
            }
            finally { library.RequestShutdown("audio-duplicate-search-workflow"); }
        });
    }

    /// <summary>
    /// 保証：有効な結果があれば、受付も進捗も使わず、探索せずに、選択時に捕捉した文脈で遷移を一回だけ呼ぶ。結果が0件でも有効な結果として扱う。
    /// 検出する誤り：有効な結果での再探索・受付の取得・進捗の表示、遷移の欠落・重複。
    /// </summary>
    [TestMethod]
    public void Select_WithCurrentResult_NavigatesWithoutAdmissionOrProgress()
    {
        Run(fixture =>
        {
            fixture.Library.SearchAudioDuplicateFolders(null, CancellationToken.None);
            Assert.IsTrue(fixture.Library.HasCurrentAudioDuplicateFolders);
            fixture.Enumerator.Fail = true;

            fixture.Select(null).GetAwaiter().GetResult();

            Assert.AreEqual(-1, fixture.Events.IndexOf("progress"), "進捗を出しません。");
            Assert.AreEqual(0, fixture.Dialogs.Messages.Count);
            Navigation navigation = fixture.Navigations.Single();
            Assert.AreEqual(DuplicateViewContextKind.Root, navigation.Context.Kind);
            Assert.AreEqual(fixture.UiThreadId, navigation.ThreadId);
        });
    }

    /// <summary>
    /// 保証：結果がなければ、共通の受付を取得して探索の終端まで保持し、取消ボタンつきの不定進捗の下で、UIスレッド外で探索する。
    /// 受付を解放した後に、結果が格納された状態で、選択時に捕捉した文脈（フォルダの選択）で遷移を一回だけ呼ぶ。
    /// 検出する誤り：受付の取得漏れ・早すぎる解放、UIスレッドでの探索、確定的な進捗の表示、取消ボタンの欠落、遷移の時点の受付の保持、遷移の重複。
    /// </summary>
    [TestMethod]
    public void Select_WithoutResult_SearchesOffTheUiThreadUnderAdmissionAndNavigatesOnceAfterRelease()
    {
        Run(fixture =>
        {
            bool? admissionFreeDuringSearch = null;
            fixture.Library.PropertyChanged += (_, args) =>
            {
                if (args.PropertyName != "AudioDuplicateFolders") { return; }
                admissionFreeDuringSearch = fixture.Admission.CanEnter;
            };

            fixture.Select(@"C:\any\folder").GetAwaiter().GetResult();

            Assert.AreEqual(false, admissionFreeDuringSearch, "探索の終端（結果の通知）までは受付を保持します。");
            Assert.AreNotEqual(fixture.UiThreadId, fixture.Enumerator.ThreadId, "探索はUIスレッド外です。");
            Assert.IsTrue(fixture.Dialogs.ProgressRequest!.Settings.ShowCancelButton);
            Assert.IsTrue(fixture.Dialogs.ProgressRequest.Settings.ShowProgressBarIndeterminate, "段階だけを示す不定進捗");
            Assert.AreEqual(Resources.Audio_duplicate_search_progress_title, fixture.Dialogs.ProgressRequest.Title);
            Navigation navigation = fixture.Navigations.Single();
            Assert.AreEqual(DuplicateViewContextKind.FolderPath, navigation.Context.Kind);
            Assert.IsTrue(navigation.AdmissionFree, "受付を解放してから遷移します。");
            Assert.IsTrue(navigation.HasResult);
            Assert.AreEqual(1, fixture.Library.AudioDuplicateFolders!.Count);
            Assert.AreEqual(0, fixture.Dialogs.Messages.Count, "成功では案内しません。");
        });
    }

    /// <summary>
    /// 保証：受付を取得できないときはBusyの案内を一件だけ出して遷移・進捗・探索をせず、予約もしない（解放後に自動では探索しない）。受付が閉じているときは案内も遷移もしない。
    /// 検出する誤り：Busy時の探索や遷移、案内の重複、自動の再試行、閉鎖をBusyとして案内する。
    /// </summary>
    [TestMethod]
    public void Select_WhenAdmissionIsBusyOrClosed_DoesNotSearchOrNavigate()
    {
        Run(fixture =>
        {
            Assert.IsTrue(fixture.Admission.TryEnter(out IDisposable? lease));

            fixture.Select(null).GetAwaiter().GetResult();
            lease!.Dispose();

            Assert.AreEqual(Resources.Warn_LibraryOperationBusy, fixture.Dialogs.Messages.Single().MessageBoxText);
            Assert.AreEqual(0, fixture.Navigations.Count);
            Assert.AreEqual(-1, fixture.Events.IndexOf("progress"));
            Assert.IsFalse(fixture.Library.HasCurrentAudioDuplicateFolders, "予約や自動の再試行はしない");

            fixture.Admission.CloseAdmission();
            fixture.Select(null).GetAwaiter().GetResult();
            Assert.AreEqual(1, fixture.Dialogs.Messages.Count, "閉鎖では案内しません。");
            Assert.AreEqual(0, fixture.Navigations.Count);
        });
    }

    /// <summary>
    /// 保証：取消を要求しても、探索が結果を格納して戻った場合は成功として扱い、案内を出さず、結果を保ったまま、受付の解放後に遷移を一回呼ぶ。
    /// 探索の呼出しを差し替え、結果の格納の後に取消ボタンを押し、取消の合図が手順から探索へ届いた（合図の待合せ）ことを確認してから探索を戻す。
    /// 探索は合図が届くまで戻らないため、取消の要求が手順で合図へ変換されることを判定に含む（届かなければ停止として、検証全体の制限時間で検出する）。
    /// 検出する誤り：格納済みの結果を取消として捨てる、取消の要求が手順で合図へ変換されない、取消を要求したことによる遷移・案内の欠落や重複。
    /// </summary>
    [TestMethod]
    public void Select_CancelRequestedAfterResultIsStored_IsSuccess()
    {
        Run(fixture =>
        {
            fixture.SearchOverride = (library, progress, token) =>
            {
                library.SearchAudioDuplicateFolders(progress, token);
                fixture.Dialogs.RequestProgressCancellation();
                // 取消の合図の到達を、期限なしに待機ハンドルの通知で待つ。
                token.WaitHandle.WaitOne();
            };

            fixture.Select(null).GetAwaiter().GetResult();

            Assert.IsTrue(fixture.Library.HasCurrentAudioDuplicateFolders, "格納済みの結果を保つ");
            Assert.AreEqual(0, fixture.Dialogs.Messages.Count, "案内しません。");
            Navigation navigation = fixture.Navigations.Single();
            Assert.IsTrue(navigation.AdmissionFree);
            Assert.IsTrue(navigation.HasResult);
        });
    }

    /// <summary>
    /// 保証：利用者の取消（開始前の取消の要求）では案内を出さず、結果を格納せずに、遷移を一回だけ呼ぶ（一覧は結果なしの空になる）。失敗（列挙の失敗）は報告を一回だけ出して遷移し、投げ直さない。
    /// どちらも受付を解放する。検出する誤り：取消の案内、取消後の遷移の欠落、失敗の報告の重複・欠落・呼出元への再送出、受付の解放漏れ。
    /// </summary>
    [TestMethod]
    public void Select_CancelIsSilent_AndFailureIsReportedOnce()
    {
        Run(fixture =>
        {
            fixture.Dialogs.CancelProgressBeforeOperation = true;
            fixture.Select(null).GetAwaiter().GetResult();

            Assert.AreEqual(0, fixture.Dialogs.Messages.Count, "取消は案内しません。");
            Assert.IsFalse(fixture.Library.HasCurrentAudioDuplicateFolders);
            Assert.AreEqual(1, fixture.Navigations.Count, "取消でも遷移を一回呼びます。");
            Assert.IsTrue(fixture.Admission.CanEnter);

            fixture.Dialogs.CancelProgressBeforeOperation = false;
            fixture.Enumerator.Fail = true;
            fixture.Select(null).GetAwaiter().GetResult();

            Assert.AreEqual(1, fixture.Dialogs.Messages.Count, "失敗の報告は一回だけです。");
            Assert.AreEqual(2, fixture.Navigations.Count);
            Assert.IsFalse(fixture.Library.HasCurrentAudioDuplicateFolders);
            Assert.IsTrue(fixture.Admission.CanEnter);
        });
    }
}
