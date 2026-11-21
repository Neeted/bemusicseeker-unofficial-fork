using BeMusicSeeker.Models;
using BeMusicSeeker.Models.BmsLibraryInternal;
using BeMusicSeeker.Properties;
using BeMusicSeeker.TestSupport.Components;
using BeMusicSeeker.ViewModels;
using BeMusicSeeker.Views.Dialogs;
using static BeMusicSeeker.TestSupport.Components.SelectedChartAudioConversionWorkflowOwnerTestSupport;
using static BeMusicSeeker.TestSupport.Components.SplitPlacementTestSupport;

namespace BeMusicSeeker.Tests;

/// <summary>
/// 分裂配置のツリーを選んだときの探索の手順（有効な結果の再利用、受付の取得と保持、進捗、取消、失敗、遷移の一回の呼出し）を、
/// 実ライブラリ・本番の受付（<see cref="ChartFileOperationSynchronizer"/>）・実際の管理主体で確認します。
/// 一覧の中身は遷移の先が結果から決めるため、ここでは遷移の呼出しの回数・時点・文脈を確認します。
/// 受付の保持は、モデルが戻る前に探索スレッドで届く結果の変更通知の中で観測し、「UIスレッド」は専用の単一スレッドの同期の文脈で模します。
/// </summary>
[TestClass]
public sealed class SplitPlacementSearchWorkflowOwnerTests
{
    private readonly ApplicationDatabaseScope databaseScope = new();

    /// <summary>遷移の呼出しの時点の観測です。</summary>
    private sealed record Navigation(DuplicateViewContext Context, bool AdmissionFree, int ThreadId, List<DuplicateGroup>? Groups);

    private sealed class Fixture : IDisposable
    {
        private readonly SingleThreadSynchronizationContext ui = new();

        internal Fixture(TestBmsLibrary library)
        {
            Library = library;
            Dialogs = new RecordingDialogService(Events);
            Workflow = new SplitPlacementSearchWorkflowOwner(() => Detached ? default : Library, Admission, Dialogs, Navigate);
            UiThreadId = ui.RunAsync(() => Task.FromResult(Environment.CurrentManagedThreadId)).GetAwaiter().GetResult();
        }

        internal TestBmsLibrary Library { get; }

        /// <summary>真の間、ライブラリが未接続（ライブラリの交換の途中）であるものとして扱います。</summary>
        internal bool Detached { get; set; }

        internal EventLog Events { get; } = new();

        internal RecordingDialogService Dialogs { get; }

        internal ChartFileOperationSynchronizer Admission { get; } = new();

        internal SplitPlacementSearchWorkflowOwner Workflow { get; }

        internal int UiThreadId { get; }

        internal List<Navigation> Navigations { get; } = [];

        private Task Navigate(DuplicateViewContext context)
        {
            lock (Navigations)
            {
                Navigations.Add(new Navigation(context, Admission.CanEnter, Environment.CurrentManagedThreadId, Library.SplitPlacementGroups));
            }
            Events.Add("navigate");
            return Task.CompletedTask;
        }

        /// <summary>「UIスレッド」の代替で、ツリーの選択の入口を開始します。</summary>
        internal Task Select(object? parameter) => ui.RunAsync(async () =>
        {
            await Workflow.SelectAsync(parameter);
            return 0;
        });

        /// <summary>「UIスレッド」の代替で、探索条件の行の「再探索」の入口を開始します。</summary>
        internal Task Research() => ui.RunAsync(async () =>
        {
            await Workflow.ResearchAsync();
            return 0;
        });

        /// <summary>
        /// 探索を行う入口を開始します。「select」はルートの選択、「research」は入力中の共有率を75へ変えた再探索で、どちらも探索して成功します。
        /// </summary>
        internal Task Enter(string entry)
        {
            if (entry != "research") { return Select(null); }
            Workflow.SharePercent = 75;
            return Research();
        }

        public void Dispose() => ui.Dispose();
    }

    /// <summary>探索の入口が戻る直前に探索スレッドで届く、結果の変更通知の記録です。</summary>
    private sealed class NotificationLog
    {
        private readonly List<(bool Free, bool AcquiredByOther, int ThreadId)> entries = [];

        internal NotificationLog(Fixture fixture, Action? inside = null)
        {
            fixture.Library.PropertyChanged += (_, args) =>
            {
                if (args.PropertyName != "SplitPlacementGroups") { return; }
                bool free = fixture.Admission.CanEnter;
                bool acquired = fixture.Admission.TryEnter(out IDisposable? lease);
                lease?.Dispose();
                lock (entries) { entries.Add((free, acquired, Environment.CurrentManagedThreadId)); }
                inside?.Invoke();
            };
        }

        internal IReadOnlyList<(bool Free, bool AcquiredByOther, int ThreadId)> Entries { get { lock (entries) { return [.. entries]; } } }
    }

    private void Run(Action<Fixture> test, bool relatedFolders = true)
    {
        BmsLibraryDuplicateServiceTestSupport.WithTemporarySongDb(databaseScope, songDbPath =>
        {
            var library = new TestBmsLibrary(databaseScope, songDbPath) { BmsCharts = TwoRelatedFolders(), BmsonCharts = [] };
            try
            {
                if (relatedFolders) { SetTwoRelatedIndex(library); }
                else { SetIndex(library, new(@"C:\BMS\A", Keys(1, 10)), new(@"C:\BMS\B", Keys(300, 10))); }
                using var fixture = new Fixture(library);
                test(fixture);
            }
            finally { library.RequestShutdown("split-placement-workflow"); }
        });
    }

    private static void Search(BMSLibrary library) => library.SearchSplitPlacementGroups(SplitPlacementSearchCriteria.Default, null, CancellationToken.None);

    private static async Task WaitForSignalAsync(Task signal, Task run, string name)
    {
        await Task.WhenAny(signal, run);
        if (!signal.IsCompleted)
        {
            await run;
            Assert.Fail(name + "の通知より先に操作の全体が終端しました。");
        }
    }

    /// <summary>
    /// 保証：有効な結果がある（0件を含む）とき、受付が他者に保持中でも閉鎖済みでも、受付の取得・進捗・探索・案内なしに、遷移を一回だけ行う。
    /// 検出する誤り：有効な結果があるのに受付を要求してBusyや未開始になる、再探索、不要な進捗・案内、遷移の欠落・重複。
    /// </summary>
    [TestMethod]
    [DataRow(false, "free", false)]
    [DataRow(false, "held", false)]
    [DataRow(false, "closed", false)]
    [DataRow(true, "free", false)]
    [DataRow(true, "held", false)]
    [DataRow(true, "closed", false)]
    [DataRow(false, "free", true)]
    [DataRow(false, "held", true)]
    [DataRow(false, "closed", true)]
    public void Select_WithCurrentResult_NavigatesOnceWithoutAdmissionProgressSearchOrNotice(bool empty, string admission, bool candidateList)
    {
        Run(fixture =>
        {
            Search(fixture.Library);
            List<DuplicateGroup> result = fixture.Library.SplitPlacementGroups!;
            Assert.AreEqual(empty ? 0 : 1, result.Count);
            var log = new NotificationLog(fixture);
            IDisposable? held = null;
            if (admission == "held") { Assert.IsTrue(fixture.Admission.TryEnter(out held)); }
            if (admission == "closed") { fixture.Admission.CloseAdmission(); }

            fixture.Select(candidateList ? DuplicateViewContext.ForCandidateList() : null).GetAwaiter().GetResult();

            Assert.AreEqual(1, fixture.Navigations.Count);
            Assert.AreEqual(candidateList ? DuplicateViewContextKind.CandidateList : DuplicateViewContextKind.Root, fixture.Navigations[0].Context.Kind);
            Assert.AreEqual(0, fixture.Dialogs.Messages.Count, "案内しません。");
            Assert.IsNull(fixture.Dialogs.ProgressRequest, "進捗を表示しません。");
            Assert.AreEqual(0, log.Entries.Count, "探索しません。");
            Assert.AreSame(result, fixture.Library.SplitPlacementGroups);
            Assert.AreEqual(admission == "free", fixture.Admission.CanEnter, "受付の状態を変えません。");
            held?.Dispose();
        }, relatedFolders: !empty);
    }

    /// <summary>
    /// 保証：結果がない（null）場合と、結果の世代が古い場合は探索し、進捗を表示して、遷移を一回行う。
    /// 検出する誤り：古い結果の再利用、結果なしでの探索の省略。
    /// </summary>
    [TestMethod]
    [DataRow("null")]
    [DataRow("stale")]
    public void Select_WithoutCurrentResult_Searches(string condition)
    {
        Run(fixture =>
        {
            List<DuplicateGroup>? before = null;
            if (condition == "stale")
            {
                Search(fixture.Library);
                before = fixture.Library.SplitPlacementGroups;
                SetIndex(fixture.Library, new(@"C:\BMS\A", Keys(1, 10)), new(@"C:\BMS\B", Keys(300, 10)));
                Assert.IsFalse(fixture.Library.HasCurrentSplitPlacementGroups(SplitPlacementSearchCriteria.Default));
            }

            fixture.Select(null).GetAwaiter().GetResult();

            Assert.IsNotNull(fixture.Dialogs.ProgressRequest, "進捗を表示します。");
            Assert.AreEqual(1, fixture.Navigations.Count);
            Assert.IsTrue(fixture.Library.HasCurrentSplitPlacementGroups(SplitPlacementSearchCriteria.Default));
            Assert.IsNotNull(fixture.Library.SplitPlacementGroups);
            if (before != null)
            {
                Assert.AreNotSame(before, fixture.Library.SplitPlacementGroups);
                Assert.AreEqual(0, fixture.Library.SplitPlacementGroups!.Count, "新しい索引に基づく結果");
            }
        });
    }

    /// <summary>
    /// 保証：受付が使用中なら、Busyの案内を一件だけ出し、探索・進捗・遷移を行わず、待たずに戻る。他者が解放した後も探索は始まらない（予約しない）。
    /// 検出する誤り：競合時の待機・探索の開始、Busy案内の重複・欠落、遷移の呼出し、解放後の自動の再試行。
    /// </summary>
    [TestMethod]
    public void Select_WhenAdmissionIsBusy_ShowsBusyNoticeOnceAndDoesNotReserve()
    {
        Run(fixture =>
        {
            var log = new NotificationLog(fixture);
            Assert.IsTrue(fixture.Admission.TryEnter(out IDisposable? lease));

            fixture.Select(null).GetAwaiter().GetResult();

            Assert.AreEqual(1, fixture.Dialogs.Messages.Count);
            Assert.AreEqual(Resources.Warn_LibraryOperationBusy, fixture.Dialogs.Messages[0].MessageBoxText);
            Assert.IsNull(fixture.Dialogs.ProgressRequest, "進捗を表示しません。");
            Assert.AreEqual(0, fixture.Navigations.Count, "遷移しません。");
            lease!.Dispose();
            Assert.IsNull(fixture.Library.SplitPlacementGroups, "他者の解放後も探索は始まらない。");
            Assert.AreEqual(0, log.Entries.Count);
            Assert.IsNull(fixture.Dialogs.ProgressRequest);
            Assert.IsTrue(fixture.Admission.CanEnter);
        });
    }

    /// <summary>
    /// 保証：受付が閉じている（終了の要求中など）ときは、案内・進捗・探索・遷移のどれも行わない。
    /// 検出する誤り：閉鎖による不成立のBusyとしての案内、閉じた受付のもとでの探索。
    /// </summary>
    [TestMethod]
    public void Select_WhenAdmissionIsClosed_DoesNothing()
    {
        Run(fixture =>
        {
            var log = new NotificationLog(fixture);
            fixture.Admission.CloseAdmission();

            fixture.Select(null).GetAwaiter().GetResult();

            Assert.AreEqual(0, fixture.Dialogs.Messages.Count);
            Assert.IsNull(fixture.Dialogs.ProgressRequest);
            Assert.AreEqual(0, fixture.Navigations.Count);
            Assert.AreEqual(0, log.Entries.Count);
            Assert.IsNull(fixture.Library.SplitPlacementGroups);
        });
    }

    /// <summary>
    /// 保証：探索中は共通の受付を保持して（結果の変更通知の時点で他の取得が失敗する）、進捗は取消ボタンつきで題名・文言がリソースの値であり、
    /// 探索はUIスレッド以外で動く。検出する誤り：受付の取得漏れ、取消できない進捗、UIスレッドでの探索。
    /// </summary>
    [TestMethod]
    [DataRow("select")]
    [DataRow("research")]
    public void Search_HoldsAdmissionOnWorkerThreadUnderCancellableProgress(string entry)
    {
        Run(fixture =>
        {
            var log = new NotificationLog(fixture);

            fixture.Enter(entry).GetAwaiter().GetResult();

            Assert.AreEqual(1, log.Entries.Count);
            Assert.IsFalse(log.Entries[0].Free, "探索の終端（モデルが戻る前）には受付を保持している。");
            Assert.IsFalse(log.Entries[0].AcquiredByOther, "他の取得は失敗する。");
            Assert.AreNotEqual(fixture.UiThreadId, log.Entries[0].ThreadId, "探索はUIスレッドの外。");
            Assert.AreNotEqual(Environment.CurrentManagedThreadId, log.Entries[0].ThreadId);
            UiProgressRequest progress = fixture.Dialogs.ProgressRequest!;
            Assert.AreEqual(Resources.Split_placement_search_progress_title, progress.Title);
            Assert.AreEqual(Resources.Split_placement_search_progress_label, progress.Label);
            Assert.IsTrue(progress.Settings.ShowCancelButton, "取消ボタンつき。");
        });
    }

    /// <summary>
    /// 保証：進捗画面が先に閉じても、探索が終わるまで受付を解放せず、遷移は探索の終端の後に、受付を解放してから一回だけ呼ぶ。
    /// 検出する誤り：進捗画面の終了での受付の早すぎる解放（探索中に別の変更が受理される）、解放前の遷移。
    /// </summary>
    [TestMethod]
    [DataRow("select")]
    [DataRow("research")]
    public void Search_KeepsAdmissionUntilSearchEndsEvenIfProgressClosesFirst_AndReleasesBeforeNavigation(string entry)
    {
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        bool? runWasReleased = null;
        BmsLibraryDuplicateServiceTestSupport.WithTemporarySongDb(databaseScope, songDbPath =>
        {
            var library = new TestBmsLibrary(databaseScope, songDbPath) { BmsCharts = TwoRelatedFolders(), BmsonCharts = [] };
            try
            {
                SetTwoRelatedIndex(library);
                using var fixture = new Fixture(library);
                // 進捗画面の代替は、操作の終端を待たずに閉じる。
                fixture.Dialogs.ProgressHandler = _ => Task.FromResult(new UiProgressResult(UiDialogStatus.Accepted));
                _ = new NotificationLog(fixture, () =>
                {
                    entered.TrySetResult();
                    release.Task.GetAwaiter().GetResult();
                });

                Task run = fixture.Enter(entry);
                WaitForSignalAsync(entered.Task, run, "探索の終端").GetAwaiter().GetResult();

                Assert.IsFalse(fixture.Admission.CanEnter, "進捗画面が閉じた後も探索が終わるまで受付を保持する。");
                Assert.IsFalse(run.IsCompleted);
                Assert.AreEqual(0, fixture.Navigations.Count, "探索の終端の前に遷移しません。");
                release.SetResult();
                run.GetAwaiter().GetResult();

                Assert.AreEqual(1, fixture.Navigations.Count);
                runWasReleased = fixture.Navigations[0].AdmissionFree;
                Assert.IsTrue(fixture.Admission.CanEnter);
            }
            finally { library.RequestShutdown("split-placement-workflow"); }
        });
        Assert.AreEqual(true, runWasReleased, "遷移を呼ぶ時点では受付を解放済み。");
    }

    /// <summary>
    /// 保証：成功では、新しい結果を格納した後に、選択時に捕捉した文脈（ルート・グループは所属フォルダの値・フォルダのパス）で、受付を解放してから遷移を一回呼ぶ。
    /// 検出する誤り：グループのインスタンス（探索で置き換わる）での遷移、結果の格納前の遷移、受付の保持中の遷移。
    /// </summary>
    [TestMethod]
    [DataRow("root")]
    [DataRow("group")]
    [DataRow("folder")]
    [DataRow("candidateList")]
    public void Select_OnSuccess_NavigatesOnceWithCapturedContextAfterReleasingAdmission(string selection)
    {
        Run(fixture =>
        {
            object? parameter = selection switch
            {
                "group" => new DuplicateGroup([], [@"C:\BMS\A"]),
                "folder" => @"C:\BMS\B",
                "candidateList" => DuplicateViewContext.ForCandidateList(),
                _ => null
            };

            fixture.Select(parameter).GetAwaiter().GetResult();

            Assert.AreEqual(1, fixture.Navigations.Count);
            Navigation navigation = fixture.Navigations[0];
            Assert.IsTrue(navigation.AdmissionFree, "受付を解放してから遷移する。");
            Assert.AreEqual(1, navigation.Groups?.Count, "新しい結果を格納した後に遷移する。");
            Assert.AreEqual(selection switch
            {
                "group" => DuplicateViewContextKind.GroupFolder,
                "folder" => DuplicateViewContextKind.FolderPath,
                "candidateList" => DuplicateViewContextKind.CandidateList,
                _ => DuplicateViewContextKind.Root
            }, navigation.Context.Kind);
            Assert.AreEqual(selection switch { "group" => @"C:\BMS\A", "folder" => @"C:\BMS\B", _ => string.Empty }, navigation.Context.Value);
            Assert.AreEqual(0, fixture.Dialogs.Messages.Count, "成功では案内しません。");
        });
    }

    /// <summary>
    /// 保証：探索の開始前に取消が要求された場合（結果がない状態から）、案内せず、モデルの状態を変えずに、受付を解放してから遷移を一回呼ぶ。
    /// 検出する誤り：取消の案内、取消後の結果の格納、遷移の欠落（一覧が更新されない）、受付の解放漏れ。
    /// </summary>
    [TestMethod]
    public void Select_WhenCancelledBeforeStart_NavigatesOnceWithoutNoticeAndKeepsModelState()
    {
        Run(fixture =>
        {
            fixture.Dialogs.CancelProgressBeforeOperation = true;
            var log = new NotificationLog(fixture);

            fixture.Select(null).GetAwaiter().GetResult();

            Assert.AreEqual(0, fixture.Dialogs.Messages.Count, "取消は案内しません。");
            Assert.AreEqual(1, fixture.Navigations.Count);
            Assert.IsTrue(fixture.Navigations[0].AdmissionFree);
            Assert.IsNull(fixture.Navigations[0].Groups, "結果がない状態のまま（一覧は空）。");
            Assert.IsNull(fixture.Library.SplitPlacementGroups);
            Assert.AreEqual(0, log.Entries.Count, "結果を格納せず、通知もしません。");
            Assert.IsTrue(fixture.Admission.CanEnter);
        });
    }

    /// <summary>
    /// 保証：取消を要求しても、モデルが結果を格納して正常に戻った場合は成功として扱い、案内せず、格納された結果で遷移を一回呼ぶ。
    /// 検出する誤り：取消の要求だけで結果を取消として扱い、格納済みの結果を表示しない。
    /// </summary>
    [TestMethod]
    public void Select_WhenCancelRequestedButSearchCompleted_TreatsAsSuccess()
    {
        Run(fixture =>
        {
            _ = new NotificationLog(fixture, fixture.Dialogs.RequestProgressCancellation);

            fixture.Select(null).GetAwaiter().GetResult();

            Assert.AreEqual(0, fixture.Dialogs.Messages.Count);
            Assert.AreEqual(1, fixture.Navigations.Count);
            Assert.AreEqual(1, fixture.Navigations[0].Groups?.Count, "格納された結果で遷移する。");
        });
    }

    /// <summary>
    /// 保証：進捗画面を表示できなかった場合は失敗として一回だけ報告し（Busyの警告ではない）、呼出し元へ例外を投げ直さず、受付を解放してから遷移を一回呼ぶ。
    /// 検出する誤り：失敗の無報告・重複報告、例外の再送出、遷移の欠落、受付の解放漏れ、Busyとの取り違え。
    /// </summary>
    [TestMethod]
    public void Select_WhenProgressCannotBeShown_ReportsFailureOnceAndStillNavigates()
    {
        Run(fixture =>
        {
            fixture.Dialogs.ProgressDisplayed = false;
            fixture.Dialogs.ProgressResult = new UiProgressResult(UiDialogStatus.NotShown);

            fixture.Select(null).GetAwaiter().GetResult();

            Assert.AreEqual(1, fixture.Dialogs.Messages.Count, "失敗の報告は一回。");
            Assert.AreEqual(Resources.Msg_error_unexpected, fixture.Dialogs.Messages[0].MessageBoxText);
            Assert.AreNotEqual(Resources.Warn_LibraryOperationBusy, fixture.Dialogs.Messages[0].MessageBoxText);
            Assert.AreEqual(1, fixture.Navigations.Count);
            Assert.IsTrue(fixture.Navigations[0].AdmissionFree);
            Assert.IsNull(fixture.Navigations[0].Groups, "結果がない状態のまま（一覧は空）。");
            Assert.IsTrue(fixture.Admission.CanEnter);
        });
    }

    private static readonly SplitPlacementSearchCriteria Strict = new(90, 20, SplitPlacementShareBasis.SmallerSide);

    private static readonly SplitPlacementSearchCriteria Loose = new(75, 20, SplitPlacementShareBasis.SmallerSide);

    /// <summary>AとBが10個中8個を共有する索引です。共有率80%（既定値）では関係があり、90%では関係がありません。</summary>
    private static void SetPartialIndex(BMSLibrary library)
        => SetIndex(library, new(@"C:\BMS\A", Keys(1, 10)), new(@"C:\BMS\B", [.. Keys(1, 8), .. Keys(900, 2)]), new(@"C:\BMS\C", Keys(500, 10)));

    private static void SearchWith(BMSLibrary library, SplitPlacementSearchCriteria criteria)
        => library.SearchSplitPlacementGroups(criteria, null, CancellationToken.None);

    /// <summary>
    /// 保証：起動直後は適用中の探索条件も入力中の値も既定値（共有率80、汎用名20、音源の少ない方）で、入力は適用中と同じなので強調しない。
    /// 検出する誤り：既定値の取り違え、起動直後からの強調。
    /// </summary>
    [TestMethod]
    public void NewOwner_StartsWithDefaultCriteriaAndNoUnappliedInput()
    {
        Run(fixture =>
        {
            Assert.AreEqual(SplitPlacementSearchCriteria.Default, fixture.Workflow.AppliedCriteria);
            Assert.AreEqual(SplitPlacementSearchCriteria.Default, fixture.Workflow.InputCriteria);
            Assert.AreEqual(80, fixture.Workflow.SharePercent);
            Assert.AreEqual(20, fixture.Workflow.GenericFolderLimit);
            Assert.AreEqual(SplitPlacementShareBasis.SmallerSide, fixture.Workflow.Basis);
            Assert.IsFalse(fixture.Workflow.HasUnappliedInput);
        });
    }

    /// <summary>
    /// 保証：入力を変えても、適用中の探索条件・結果・一覧は変わらず、探索・案内・遷移を行わない。入力が適用中と異なる間は「入力中の値が異なる」を真にし、
    /// 同じ値へ戻すと偽にして、それぞれ変更を通知する。範囲の外の値は拒否し、入力を変えない。
    /// 検出する誤り：入力だけでの適用や再探索、通知の欠落、異なる値の取り違え、不正値の受理。
    /// </summary>
    [TestMethod]
    public void ChangingInput_DoesNotApplySearchOrNavigate_AndTracksUnappliedState()
    {
        Run(fixture =>
        {
            Search(fixture.Library);
            List<DuplicateGroup> result = fixture.Library.SplitPlacementGroups!;
            var changed = new List<string?>();
            fixture.Workflow.PropertyChanged += (_, args) => changed.Add(args.PropertyName);

            fixture.Workflow.SharePercent = 60;
            fixture.Workflow.GenericFolderLimit = 30;
            fixture.Workflow.Basis = SplitPlacementShareBasis.BothSides;

            CollectionAssert.AreEqual(
                new[] { nameof(SplitPlacementSearchWorkflowOwner.SharePercent), nameof(SplitPlacementSearchWorkflowOwner.HasUnappliedInput),
                    nameof(SplitPlacementSearchWorkflowOwner.GenericFolderLimit), nameof(SplitPlacementSearchWorkflowOwner.Basis) },
                changed);
            Assert.IsTrue(fixture.Workflow.HasUnappliedInput);
            Assert.AreEqual(new SplitPlacementSearchCriteria(60, 30, SplitPlacementShareBasis.BothSides), fixture.Workflow.InputCriteria);
            Assert.AreEqual(SplitPlacementSearchCriteria.Default, fixture.Workflow.AppliedCriteria);
            Assert.IsTrue(fixture.Workflow.HasCurrentResult, "適用中の探索条件に対する結果は有効なまま。");
            Assert.AreSame(result, fixture.Library.SplitPlacementGroups);
            Assert.AreEqual(0, fixture.Navigations.Count);
            Assert.AreEqual(0, fixture.Dialogs.Messages.Count);
            Assert.IsNull(fixture.Dialogs.ProgressRequest);

            Assert.ThrowsExactly<ArgumentOutOfRangeException>(() => fixture.Workflow.SharePercent = 52);
            Assert.AreEqual(60, fixture.Workflow.SharePercent, "不正な値は入力を変えない。");

            changed.Clear();
            fixture.Workflow.SharePercent = 80;
            fixture.Workflow.GenericFolderLimit = 20;
            fixture.Workflow.Basis = SplitPlacementShareBasis.SmallerSide;

            Assert.IsFalse(fixture.Workflow.HasUnappliedInput, "適用中と同じ値へ戻すと強調を解く。");
            Assert.AreEqual(1, changed.Count(name => name == nameof(SplitPlacementSearchWorkflowOwner.HasUnappliedInput)), "最後の入力で一回通知する。");
            Assert.AreEqual(0, fixture.Navigations.Count);
        });
    }

    /// <summary>
    /// 保証：入力中の値に対する有効な結果があれば、受付が空き・他者が保持中・閉鎖のいずれでも、受付を取らず、進捗・案内・探索・遷移・変更を行わずに戻る。
    /// 検出する誤り：有効な結果があるのに受付を要求してBusyや未開始になる、再探索、不要な進捗・案内、適用中の探索条件の変更。
    /// </summary>
    [TestMethod]
    [DataRow("free")]
    [DataRow("held")]
    [DataRow("closed")]
    public void Research_WithCurrentResultForInput_ChangesNothing(string admission)
    {
        Run(fixture =>
        {
            SearchWith(fixture.Library, Loose);
            List<DuplicateGroup> result = fixture.Library.SplitPlacementGroups!;
            fixture.Workflow.SharePercent = 75;
            var log = new NotificationLog(fixture);
            IDisposable? held = null;
            if (admission == "held") { Assert.IsTrue(fixture.Admission.TryEnter(out held)); }
            if (admission == "closed") { fixture.Admission.CloseAdmission(); }

            fixture.Research().GetAwaiter().GetResult();

            Assert.AreEqual(0, fixture.Navigations.Count);
            Assert.AreEqual(0, fixture.Dialogs.Messages.Count);
            Assert.IsNull(fixture.Dialogs.ProgressRequest);
            Assert.AreEqual(0, log.Entries.Count);
            Assert.AreSame(result, fixture.Library.SplitPlacementGroups);
            Assert.AreEqual(SplitPlacementSearchCriteria.Default, fixture.Workflow.AppliedCriteria, "適用中は変わらない。");
            Assert.AreEqual(Loose, fixture.Workflow.InputCriteria);
            Assert.AreEqual(admission == "free", fixture.Admission.CanEnter);
            held?.Dispose();
        });
    }

    /// <summary>
    /// 保証：入力は適用中と同じで、結果の世代だけが古い場合は探索し、成功して統合候補一覧の文脈で一回遷移する。
    /// 検出する誤り：探索条件が同じだというだけで古い結果を有効とみなす。
    /// </summary>
    [TestMethod]
    public void Research_WithSameCriteriaButStaleGeneration_Searches()
    {
        Run(fixture =>
        {
            Search(fixture.Library);
            List<DuplicateGroup> before = fixture.Library.SplitPlacementGroups!;
            SetTwoRelatedIndex(fixture.Library);
            Assert.IsFalse(fixture.Workflow.HasUnappliedInput);

            fixture.Research().GetAwaiter().GetResult();

            Assert.IsNotNull(fixture.Dialogs.ProgressRequest);
            Assert.AreEqual(1, fixture.Navigations.Count);
            Assert.AreEqual(DuplicateViewContextKind.CandidateList, fixture.Navigations[0].Context.Kind);
            Assert.AreNotSame(before, fixture.Library.SplitPlacementGroups);
            Assert.IsTrue(fixture.Library.HasCurrentSplitPlacementGroups(SplitPlacementSearchCriteria.Default));
        });
    }

    /// <summary>
    /// 保証：受付が使用中なら、Busyの案内を一件だけ出し、探索・進捗・遷移を行わず、適用中・入力中・結果を変えず、強調も保ち、解放後も探索しない（予約しない）。
    /// 検出する誤り：競合時の待機・探索の開始、Busy案内の重複・欠落、入力の巻き戻し、解放後の自動の再試行。
    /// </summary>
    [TestMethod]
    public void Research_WhenAdmissionIsBusy_ShowsBusyNoticeOnceAndChangesNothing()
    {
        Run(fixture =>
        {
            fixture.Workflow.SharePercent = 75;
            var log = new NotificationLog(fixture);
            Assert.IsTrue(fixture.Admission.TryEnter(out IDisposable? lease));

            fixture.Research().GetAwaiter().GetResult();

            Assert.AreEqual(1, fixture.Dialogs.Messages.Count);
            Assert.AreEqual(Resources.Warn_LibraryOperationBusy, fixture.Dialogs.Messages[0].MessageBoxText);
            Assert.IsNull(fixture.Dialogs.ProgressRequest);
            Assert.AreEqual(0, fixture.Navigations.Count);
            Assert.AreEqual(SplitPlacementSearchCriteria.Default, fixture.Workflow.AppliedCriteria);
            Assert.AreEqual(Loose, fixture.Workflow.InputCriteria);
            Assert.IsTrue(fixture.Workflow.HasUnappliedInput);
            lease!.Dispose();
            Assert.IsNull(fixture.Library.SplitPlacementGroups, "解放後も探索は始まらない。");
            Assert.AreEqual(0, log.Entries.Count);
            Assert.IsNull(fixture.Dialogs.ProgressRequest);
        });
    }

    /// <summary>保証：受付が閉じているときは、案内・進捗・探索・遷移・変更のどれも行わない。検出する誤り：閉鎖のBusyとしての案内。</summary>
    [TestMethod]
    public void Research_WhenAdmissionIsClosed_DoesNothing()
    {
        Run(fixture =>
        {
            fixture.Workflow.SharePercent = 75;
            fixture.Admission.CloseAdmission();

            fixture.Research().GetAwaiter().GetResult();

            Assert.AreEqual(0, fixture.Dialogs.Messages.Count);
            Assert.IsNull(fixture.Dialogs.ProgressRequest);
            Assert.AreEqual(0, fixture.Navigations.Count);
            Assert.IsNull(fixture.Library.SplitPlacementGroups);
            Assert.AreEqual(SplitPlacementSearchCriteria.Default, fixture.Workflow.AppliedCriteria);
            Assert.AreEqual(Loose, fixture.Workflow.InputCriteria);
        });
    }

    /// <summary>
    /// 保証：成功では、入力中の値で探索して適用中の探索条件にし（強調を解く）、受付を解放してから統合候補一覧の文脈で遷移を一回呼ぶ。
    /// 検出する誤り：適用の欠落、入力中の値と異なる条件での探索、遷移の文脈の取り違え、受付の保持中の遷移。
    /// </summary>
    [TestMethod]
    public void Research_OnSuccess_AppliesInputAndNavigatesToCandidateListAfterReleasingAdmission()
    {
        Run(fixture =>
        {
            SetPartialIndex(fixture.Library);
            fixture.Workflow.SharePercent = 90;
            Assert.IsTrue(fixture.Workflow.HasUnappliedInput);
            var changed = new List<string?>();
            fixture.Workflow.PropertyChanged += (_, args) => changed.Add(args.PropertyName);

            fixture.Research().GetAwaiter().GetResult();

            Assert.AreEqual(Strict, fixture.Workflow.AppliedCriteria);
            Assert.IsFalse(fixture.Workflow.HasUnappliedInput);
            CollectionAssert.Contains(changed, nameof(SplitPlacementSearchWorkflowOwner.HasUnappliedInput));
            Assert.AreEqual(1, fixture.Navigations.Count);
            Assert.AreEqual(DuplicateViewContextKind.CandidateList, fixture.Navigations[0].Context.Kind);
            Assert.IsTrue(fixture.Navigations[0].AdmissionFree, "受付を解放してから遷移する。");
            Assert.AreEqual(0, fixture.Navigations[0].Groups?.Count, "入力中の値（共有率90）に従う結果（0件）を格納してから遷移する。");
            Assert.IsTrue(fixture.Workflow.HasCurrentResult);
            Assert.AreEqual(0, fixture.Dialogs.Messages.Count);
        });
    }

    /// <summary>
    /// 保証：開始前の取消では、案内せず、遷移せず、適用中・結果・入力中を変えない。
    /// 検出する誤り：取消の案内、取消でも入力を適用する、取消後の遷移。
    /// </summary>
    [TestMethod]
    public void Research_WhenCancelledBeforeStart_ChangesNothingWithoutNotice()
    {
        Run(fixture =>
        {
            Search(fixture.Library);
            List<DuplicateGroup> result = fixture.Library.SplitPlacementGroups!;
            fixture.Workflow.SharePercent = 75;
            fixture.Dialogs.CancelProgressBeforeOperation = true;

            fixture.Research().GetAwaiter().GetResult();

            Assert.AreEqual(0, fixture.Dialogs.Messages.Count);
            Assert.AreEqual(0, fixture.Navigations.Count);
            Assert.AreSame(result, fixture.Library.SplitPlacementGroups);
            Assert.IsTrue(fixture.Library.HasCurrentSplitPlacementGroups(SplitPlacementSearchCriteria.Default));
            Assert.AreEqual(SplitPlacementSearchCriteria.Default, fixture.Workflow.AppliedCriteria);
            Assert.AreEqual(Loose, fixture.Workflow.InputCriteria);
            Assert.IsTrue(fixture.Workflow.HasUnappliedInput);
            Assert.IsTrue(fixture.Admission.CanEnter);
        });
    }

    /// <summary>保証：取消を要求しても結果を格納して終わった場合は成功として扱い、適用して遷移する。検出する誤り：格納済みの結果を取消として捨てる。</summary>
    [TestMethod]
    public void Research_WhenCancelRequestedButSearchCompleted_TreatsAsSuccess()
    {
        Run(fixture =>
        {
            fixture.Workflow.SharePercent = 75;
            _ = new NotificationLog(fixture, fixture.Dialogs.RequestProgressCancellation);

            fixture.Research().GetAwaiter().GetResult();

            Assert.AreEqual(0, fixture.Dialogs.Messages.Count);
            Assert.AreEqual(Loose, fixture.Workflow.AppliedCriteria);
            Assert.AreEqual(1, fixture.Navigations.Count);
        });
    }

    /// <summary>
    /// 保証：進捗画面を表示できない失敗では、一回だけ報告し、例外を投げ直さず、遷移せず、適用中・結果・入力中を変えない。
    /// 検出する誤り：失敗の無報告・重複報告、失敗でも入力を適用する、失敗後の遷移。
    /// </summary>
    [TestMethod]
    public void Research_WhenProgressCannotBeShown_ReportsFailureOnceAndChangesNothing()
    {
        Run(fixture =>
        {
            Search(fixture.Library);
            List<DuplicateGroup> result = fixture.Library.SplitPlacementGroups!;
            fixture.Workflow.SharePercent = 75;
            fixture.Dialogs.ProgressDisplayed = false;
            fixture.Dialogs.ProgressResult = new UiProgressResult(UiDialogStatus.NotShown);

            fixture.Research().GetAwaiter().GetResult();

            Assert.AreEqual(1, fixture.Dialogs.Messages.Count);
            Assert.AreEqual(Resources.Msg_error_unexpected, fixture.Dialogs.Messages[0].MessageBoxText);
            Assert.AreEqual(0, fixture.Navigations.Count);
            Assert.AreSame(result, fixture.Library.SplitPlacementGroups);
            Assert.AreEqual(SplitPlacementSearchCriteria.Default, fixture.Workflow.AppliedCriteria);
            Assert.AreEqual(Loose, fixture.Workflow.InputCriteria);
            Assert.IsTrue(fixture.Admission.CanEnter);
        });
    }

    /// <summary>
    /// 保証：ルートの選択と、選択済みのルートの再操作の判定（有効な結果の有無）は、適用中の探索条件を使い、入力中（未適用）の値を使わない。
    /// 検出する誤り：入力中の値での探索、入力中の値に対する結果を適用中の結果と取り違える。
    /// </summary>
    [TestMethod]
    public void Select_UsesAppliedCriteriaNotInput()
    {
        Run(fixture =>
        {
            SetPartialIndex(fixture.Library);
            fixture.Workflow.SharePercent = 90;
            SearchWith(fixture.Library, Strict);
            Assert.IsFalse(fixture.Workflow.HasCurrentResult, "入力中の値に対する結果は、適用中の結果として扱わない。");

            fixture.Select(null).GetAwaiter().GetResult();

            Assert.IsNotNull(fixture.Dialogs.ProgressRequest, "適用中の探索条件で探索する。");
            Assert.IsTrue(fixture.Library.HasCurrentSplitPlacementGroups(SplitPlacementSearchCriteria.Default));
            Assert.AreEqual(1, fixture.Library.SplitPlacementGroups!.Count, "既定値（共有率80）では関係がある。");
            Assert.IsTrue(fixture.Workflow.HasCurrentResult);
            Assert.AreEqual(SplitPlacementSearchCriteria.Default, fixture.Workflow.AppliedCriteria);
            Assert.AreEqual(Strict, fixture.Workflow.InputCriteria);
        });
    }

    /// <summary>
    /// 保証：適用中と入力中の値は、ライブラリの未接続・再接続（交換）と画面の行き来（選択の繰返し）の後も保たれる。
    /// 検出する誤り：ライブラリの交換での探索条件の巻き戻し。
    /// </summary>
    [TestMethod]
    public void Criteria_SurviveLibraryExchangeAndRepeatedSelection()
    {
        Run(fixture =>
        {
            fixture.Workflow.SharePercent = 75;
            fixture.Research().GetAwaiter().GetResult();
            fixture.Workflow.GenericFolderLimit = 30;

            fixture.Detached = true;
            Assert.IsFalse(fixture.Workflow.HasCurrentResult);
            fixture.Select(null).GetAwaiter().GetResult();
            fixture.Detached = false;
            fixture.Select(null).GetAwaiter().GetResult();

            Assert.AreEqual(Loose, fixture.Workflow.AppliedCriteria);
            Assert.AreEqual(new SplitPlacementSearchCriteria(75, 30, SplitPlacementShareBasis.SmallerSide), fixture.Workflow.InputCriteria);
            Assert.IsTrue(fixture.Workflow.HasUnappliedInput);
        });
    }
}
