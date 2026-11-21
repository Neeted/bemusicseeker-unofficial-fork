using System.ComponentModel;
using System.Windows;
using BeMusicSeeker.Models;
using BeMusicSeeker.Models.BmsLibraryInternal;
using BeMusicSeeker.Models.Utils;
using BeMusicSeeker.Properties;
using BeMusicSeeker.TestSupport.Components;
using BeMusicSeeker.ViewModels;
using BeMusicSeeker.Views.Dialogs;
using static BeMusicSeeker.TestSupport.Components.DuplicateMaintenanceWorkflowOwnerTestSupport;
using static BeMusicSeeker.TestSupport.Components.SplitPlacementTestSupport;
using Lists = BeMusicSeeker.TestSupport.Components.DuplicateMergeCandidateListTestSupport;

namespace BeMusicSeeker.Tests;

/// <summary>
/// 分裂配置の統合候補一覧からの一括整理（<see cref="DuplicateMaintenanceWorkflowOwner.OrganizeSelectedSplitPlacementGroupsAsync"/>）を、
/// 実ライブラリ（一時DB）・その共通受付・実際の一覧の管理主体二つ（分裂配置と重複）・実際のワークフローの管理主体で確認します。
/// モデルの一括整理だけを保存窓口の代替へ差し替え、その中で所持集合を実際に置き換えて、分裂配置の結果を本番と同じ経路で失効させます。
/// 「UIスレッド」は専用の単一スレッドの同期の文脈で模し、進捗は実際の背景実行で動かします。
/// </summary>
[TestClass]
public sealed class DuplicateMaintenanceWorkflowOwnerSplitPlacementTests
{
    private const string Alpha1 = @"C:\BMS\ShowAlpha1";
    private const string Alpha2 = @"C:\BMS\ShowAlpha2";
    private const string Beta1 = @"C:\BMS\ShowBeta1";
    private const string Beta2 = @"C:\BMS\ShowBeta2";
    private const string Gamma1 = @"C:\BMS\HideGamma1";
    private const string Gamma2 = @"C:\BMS\HideGamma2";
    private const string Delta1 = @"C:\BMS\ShowDelta1";
    private const string Delta2 = @"C:\BMS\ShowDelta2";

    private static readonly TimeSpan Deadline = TimeSpan.FromSeconds(30);

    private readonly ApplicationDatabaseScope databaseScope = new();

    /// <summary>分裂配置の探索し直しの観測と失敗の注入、一括整理の要求の記録と結果の差し替えを持つ保存窓口の代替です。</summary>
    private sealed class SplitStore(List<string> events) : RecordingStore(events)
    {
        internal List<DuplicateBatchOrganizeRequest> Requests { get; } = [];

        internal Func<DuplicateBatchOrganizeRequest, Action<DuplicateBatchOrganizeProgress>?, CancellationToken, DuplicateBatchOrganizeResult>? Handler { get; set; }

        internal Exception? SearchFailure { get; set; }

        internal int SearchCalls { get; private set; }

        /// <summary>探索の呼出しの時点の受付が空いているか。受付の保持中に探索することの観測です。</summary>
        internal Func<bool>? AdmissionFree { get; set; }

        /// <summary>探索の呼出しの時点までに出た案内の件数。結果の案内より前に探索することの観測です。</summary>
        internal Func<int>? MessageCount { get; set; }

        /// <summary>探索が結果を格納して通知した時点（探索を呼んだスレッドでの通知）の観測です。</summary>
        internal List<(bool AdmissionFree, int Messages)> StoredNotifications { get; } = [];

        public override DuplicateBatchOrganizeResult OrganizeDuplicateGroups(
            BMSLibrary library, DuplicateBatchOrganizeRequest request, long operationId,
            LibraryOperationNotices notices, Action<DuplicateBatchOrganizeProgress>? progress, CancellationToken cancellationToken)
        {
            AddEvent("store-organize");
            Requests.Add(request);
            return Handler != null ? Handler(request, progress, cancellationToken) : Complete(request, progress);
        }

        public override void SearchSplitPlacementGroups(BMSLibrary library, SplitPlacementSearchCriteria criteria)
        {
            AddEvent("store-search-split");
            SearchCalls++;
            SearchedSplitPlacementCriteria.Add(criteria);
            if (SearchFailure != null) { throw SearchFailure; }
            int callerThread = Environment.CurrentManagedThreadId;
            PropertyChangedEventHandler handler = (_, args) =>
            {
                // 失効の通知は別のスレッドで非同期に届くため、探索を呼んだスレッドでの、結果が格納された後の通知だけを観測する。
                if (args.PropertyName != "SplitPlacementGroups" || Environment.CurrentManagedThreadId != callerThread
                    || library.SplitPlacementGroups == null) { return; }
                StoredNotifications.Add((AdmissionFree!(), MessageCount!()));
            };
            library.PropertyChanged += handler;
            try { library.SearchSplitPlacementGroups(criteria, null, CancellationToken.None); }
            finally { library.PropertyChanged -= handler; }
        }

        internal static DuplicateBatchOrganizeResult Complete(DuplicateBatchOrganizeRequest request, Action<DuplicateBatchOrganizeProgress>? progress)
        {
            for (int index = 0; index < request.Groups.Count; index++)
            {
                progress?.Invoke(new DuplicateBatchOrganizeProgress(index + 1, request.Groups.Count, request.Groups[index].KeepDirectory));
            }
            progress?.Invoke(new DuplicateBatchOrganizeProgress(request.Groups.Count, request.Groups.Count, null!, DuplicateBatchOrganizeProgressKind.Applying));
            var session = new LibraryMutationSessionReceipt([new LibraryMutationSessionTarget(@"C:\S\source", @"C:\S\keep")], durableCommit: true);
            return new DuplicateBatchOrganizeResult(
                new DuplicateMergeMaintenanceReceipt(true, ResourceHealthIndexUpdateMode.DeferOnUpdates, MaintenanceWorkflowResultFacts.From(null),
                    false, false, false, false, false, session),
                request.Groups.Select(group => new DuplicateBatchOrganizeGroupResult(
                    group.KeepDirectory, group.IsSingleFolder, DuplicateBatchOrganizeGroupStatus.Completed, DuplicateBatchOrganizeSkipReason.None,
                    null, group.SourceDirectories, null, 0, 0)),
                canceled: false, admitted: true);
        }
    }

    private sealed class Fixture : IDisposable
    {
        private readonly SingleThreadSynchronizationContext ui = new();

        internal Fixture(TestBmsLibrary library)
        {
            Library = library;
            Presentation = new RecordingPresentation(Events);
            Dialogs = new FakeUiDialogService { ConfirmationResult = UiDialogResult.FromMessageBoxResult(MessageBoxResult.OK), Events = Events };
            Dialogs.Progress.ReceiverContext = ui;
            Store = new SplitStore(Events) { AdmissionFree = () => Library.OperationAdmission.CanEnter, MessageCount = () => Dialogs.Messages.Count };
            Owner = new DuplicateMaintenanceWorkflowOwner(
                () => Library, Library.OperationAdmission, Activity, Presentation, Dialogs, () => true, _ => true, _ => new ExplorerOpenResult(), _ => null, Store,
                candidateList: DuplicateList, splitPlacementCandidateList: SplitList, splitPlacementCriteriaProvider: () => Applied);
            Owner.WorkflowChanged += Presentation.OnWorkflowChanged;
            Activity.ActivityChanged += Presentation.OnActivityChanged;
            PreparationCalls = [];
            Library.ConnectDuplicateDataPreparation((_, _, requireGroups) =>
            {
                lock (PreparationCalls) { PreparationCalls.Add(requireGroups); }
                return Task.CompletedTask;
            });
        }

        internal TestBmsLibrary Library { get; }

        internal List<string> Events { get; } = [];

        internal SplitStore Store { get; }

        internal RecordingPresentation Presentation { get; }

        internal FakeUiDialogService Dialogs { get; }

        internal ChartMutationActivityOwner Activity { get; } = new();

        internal DuplicateMaintenanceWorkflowOwner Owner { get; }

        internal DuplicateMergeCandidateListOwner SplitList { get; } = new(_ => false, _ => { });

        internal DuplicateMergeCandidateListOwner DuplicateList { get; } = new(_ => false, _ => { });

        /// <summary>重複結果の準備の窓口が受けた、明示準備の要否（<c>requireGroups</c>）の記録です。</summary>
        internal List<bool> PreparationCalls { get; }

        internal DuplicateGroup[] Groups { get; private set; } = [];

        /// <summary>適用中の分裂配置の探索条件です。本番では <see cref="SplitPlacementSearchWorkflowOwner"/> が所有する値の代替で、一括整理の後の探索し直しが読みます。</summary>
        internal SplitPlacementSearchCriteria Applied { get; set; } = SplitPlacementSearchCriteria.Default;

        internal Task<T> OnUi<T>(Func<Task<T>> operation) => ui.RunAsync(operation);

        /// <summary>現在の分裂配置の結果を、分裂配置の一覧へ採用します。</summary>
        internal void AdoptSplitList()
        {
            Groups = [.. Library.SplitPlacementGroups!];
            SplitList.AttachLibrary(Library);
            SplitList.SetTreeSelected(true);
            Assert.IsTrue(SplitList.Adopt(DuplicateMergeCandidateListOwner.Prepare(Library, Groups, Library, CancellationToken.None)));
        }

        internal void Write(DuplicateGroup group, DuplicateMergeVerificationResult result)
            => SplitList.WriteVerifications([new DuplicateMergeVerificationWrite(group, result)]);

        internal DuplicateMergeCandidateGroupState State(DuplicateGroup group)
        {
            Assert.IsTrue(SplitList.TryGetState(group, out DuplicateMergeCandidateGroupState? state));
            return state!;
        }

        internal (bool Selected, string? Keep, DuplicateMergeVerificationResult Verification)[] Snapshot()
            => [.. Groups.Select(group => { DuplicateMergeCandidateGroupState state = State(group); return (state.IsSelected, state.KeepFolder, state.Verification); })];

        /// <summary>
        /// モデルの一括整理の代替です。Alpha の統合を模して Alpha2 の音源を他と無関係にし、Delta の二フォルダが音源を共有するようにして、リソース索引の世代を進めます。
        /// 結果は null ではなく世代の古い状態になります（所持集合の置換は、受付を保持したままでは公開の窓口から行えません。
        /// 所持集合の変更による null の失効は、画面の整理のケースが実ファイルの統合で確かめます）。
        /// </summary>
        internal DuplicateBatchOrganizeResult MergeAlphaAndAddDelta(DuplicateBatchOrganizeRequest request, Action<DuplicateBatchOrganizeProgress>? progress)
        {
            Assert.IsFalse(Library.OperationAdmission.CanEnter, "モデルの呼出しの間は受付を保持している");
            SetIndex(Library, IndexFolders(changed: true));
            Assert.IsFalse(Library.HasCurrentSplitPlacementGroups(Applied), "前提：世代が進んで、有効な結果がない");
            return SplitStore.Complete(request, progress);
        }

        public void Dispose() => ui.Dispose();
    }

    /// <summary>
    /// 六つ（変更後は八つ）のフォルダの音源の索引です。変更前は Alpha・Beta・Gamma がそれぞれ分裂配置の関係で、Delta は無関係です。
    /// 変更後は Alpha2 が無関係になって Alpha のグループが解消し、Delta の二フォルダが関係になって新しいグループができます。
    /// </summary>
    private static IndexFolder[] IndexFolders(bool changed)
        =>
        [
            new(Alpha1, Keys(1, 10)), new(Alpha2, changed ? Keys(900, 10) : Keys(1, 10)),
            new(Beta1, Keys(100, 10)), new(Beta2, Keys(100, 10)), new(Gamma1, Keys(200, 10)), new(Gamma2, Keys(200, 10)),
            new(Delta1, Keys(300, 10)), new(Delta2, changed ? Keys(300, 10) : Keys(400, 10))
        ];

    private void Run(Action<Fixture> test)
    {
        BmsLibraryDuplicateServiceTestSupport.WithTemporarySongDb(databaseScope, songDbPath =>
        {
            var library = new TestBmsLibrary(databaseScope, songDbPath)
            {
                BmsCharts =
                [
                    Chart(Alpha1 + @"\a1.bms", 1, "Alpha"), Chart(Alpha2 + @"\a2.bms", 2, "Alpha2"),
                    Chart(Beta1 + @"\b1.bms", 3, "Beta"), Chart(Beta2 + @"\b2.bms", 4, "Beta2"),
                    Chart(Gamma1 + @"\g1.bms", 5, "Gamma"), Chart(Gamma2 + @"\g2.bms", 6, "Gamma2"),
                    Chart(Delta1 + @"\d1.bms", 7, "Delta"), Chart(Delta2 + @"\d2.bms", 8, "Delta2")
                ],
                BmsonCharts = []
            };
            try
            {
                SetIndex(library, IndexFolders(changed: false));
                library.SearchSplitPlacementGroups(SplitPlacementSearchCriteria.Default, null, CancellationToken.None);
                using var fixture = new Fixture(library);
                fixture.AdoptSplitList();
                Assert.AreEqual(3, fixture.Groups.Length, "前提：Alpha・Beta・Gamma の三グループが探索される");
                test(fixture);
            }
            finally { library.RequestShutdown("split-placement-batch-organize"); }
        });
    }

    private static readonly DuplicateMergeVerificationResult Safe = new(DuplicateMergeVerificationStage.Safe, []);

    private static DuplicateMergeVerificationResult Caution(string folder)
        => new(DuplicateMergeVerificationStage.Caution,
            [new DuplicateMergeVerificationReason(DuplicateMergeVerificationReasonKind.AudioExtension, [new DuplicateMergeFinding(folder, "a.wav")])]);

    private static void AssertNothingHappened(Fixture fixture)
    {
        Assert.IsNull(fixture.Dialogs.ConfirmationRequest, "確認を表示してはいけません。");
        Assert.AreEqual(0, fixture.Dialogs.Progress.Runs, "進捗を開いてはいけません。");
        Assert.AreEqual(0, fixture.Store.Requests.Count, "モデルを呼んではいけません。");
        Assert.AreEqual(0, fixture.Store.SearchCalls, "探索してはいけません。");
        CollectionAssert.AreEqual(Array.Empty<string>(), fixture.Events.Where(item => item != "message").ToArray(), "再生停止・抑制・活動・確認・進捗のどれも起きてはいけません。");
    }

    /// <summary>
    /// 保証：対象に未確認のグループが一つでもあれば（安全・要注意・未確認の混在でも）、新しいリソースの案内を一件だけ出して終わる。
    /// 受付の取得（他者が保持中でもBusy案内を出さない）・確認・再生停止・更新の抑制・進捗・モデルの呼出し・探索を行わず、SELECT・KEEP・確認結果・並べ替えを変えない。
    /// 検出する誤り：未確認を含む整理の続行、受付を取ってからの拒否（Busy案内の混入）、状態の書換え、案内の重複・欠落。
    /// </summary>
    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public void Organize_WithUnverifiedTarget_ShowsNoticeOnceAndChangesNothing(bool admissionHeldByOther)
    {
        Run(fixture =>
        {
            DuplicateGroup safe = fixture.Groups[0], caution = fixture.Groups[1], unverified = fixture.Groups[2];
            fixture.Write(safe, Safe);
            fixture.Write(caution, Caution(Beta1));
            fixture.SplitList.RequestSort("Path", ListSortDirection.Descending);
            fixture.SplitList.SelectVisible();
            Assert.AreEqual(3, fixture.SplitList.CaptureSelectedGroups().Count, "前提：三段階の混在した三グループが対象");
            Assert.AreEqual(DuplicateMergeVerificationStage.Unverified, fixture.State(unverified).Verification.Stage);
            (bool Selected, string? Keep, DuplicateMergeVerificationResult Verification)[] snapshot = fixture.Snapshot();
            List<DuplicateGroup> resultBefore = fixture.Library.SplitPlacementGroups!;
            IDisposable? held = null;
            if (admissionHeldByOther) { Assert.IsTrue(fixture.Library.OperationAdmission.TryEnter(out held)); }

            DuplicateBatchOrganizeRunResult run;
            using (held) { run = fixture.OnUi(async () => await fixture.Owner.OrganizeSelectedSplitPlacementGroupsAsync()).GetAwaiter().GetResult(); }

            Assert.AreEqual(DuplicateBatchOrganizeRunStatus.UnverifiedTargets, run.Status);
            Assert.AreEqual(Resources.Split_placement_batch_organize_unverified_notice, fixture.Dialogs.Messages.Single().MessageBoxText, "新しい案内が一件だけ");
            AssertNothingHappened(fixture);
            Assert.IsTrue(fixture.Library.OperationAdmission.CanEnter, "整理は受付を取らないので、他者の保持の解放後は取得できる");
            CollectionAssert.AreEqual(snapshot, fixture.Snapshot(), "SELECT・KEEP・確認結果は変わらない");
            Assert.AreEqual("Path", fixture.SplitList.SortColumnName);
            Assert.AreSame(resultBefore, fixture.Library.SplitPlacementGroups);
        });
    }

    /// <summary>
    /// 保証：対象0件は何もしない（案内・受付・確認なし。他者が受付を保持していてもBusy案内を出さない）。
    /// 未確認がなく受付が使用中ならBusy案内を一件だけ出して未開始（確認・再生停止・進捗・モデル・探索なし、予約なし）、受付が閉じていれば案内なしで未開始。
    /// 検出する誤り：0件での受付の取得・案内、使用中の黙った拒否や案内の重複、閉鎖時の案内、解放後の自動実行。
    /// </summary>
    [TestMethod]
    [DataRow("none")]
    [DataRow("busy")]
    [DataRow("closed")]
    public void Organize_NoTargetsBusyOrClosed_FollowsTheSharedAdmissionRules(string scenario)
    {
        Run(fixture =>
        {
            foreach (DuplicateGroup group in fixture.Groups) { fixture.Write(group, Safe); }
            if (scenario == "none") { fixture.SplitList.ClearVisible(); }
            else { fixture.SplitList.SelectVisible(); }
            Assert.AreEqual(scenario == "none" ? 0 : 3, fixture.SplitList.CaptureSelectedGroups().Count);
            IDisposable? held = null;
            if (scenario is "none" or "busy") { Assert.IsTrue(fixture.Library.OperationAdmission.TryEnter(out held)); }
            if (scenario == "closed") { fixture.Library.OperationAdmission.CloseAdmission(); }

            DuplicateBatchOrganizeRunResult run;
            using (held) { run = fixture.OnUi(async () => await fixture.Owner.OrganizeSelectedSplitPlacementGroupsAsync()).GetAwaiter().GetResult(); }

            switch (scenario)
            {
                case "none":
                    Assert.AreEqual(DuplicateBatchOrganizeRunStatus.NoTargets, run.Status);
                    Assert.AreEqual(0, fixture.Dialogs.Messages.Count, "案内しない");
                    break;
                case "busy":
                    Assert.AreEqual(DuplicateBatchOrganizeRunStatus.Busy, run.Status);
                    Assert.AreEqual(Resources.Warn_LibraryOperationBusy, fixture.Dialogs.Messages.Single().MessageBoxText, "Busy案内が一件");
                    Assert.IsTrue(fixture.Library.OperationAdmission.CanEnter, "予約しない");
                    break;
                default:
                    Assert.AreEqual(DuplicateBatchOrganizeRunStatus.NotStarted, run.Status);
                    Assert.AreEqual(0, fixture.Dialogs.Messages.Count, "閉鎖では案内しない");
                    break;
            }
            AssertNothingHappened(fixture);
        });
    }

    /// <summary>
    /// 保証：未確認がなく要注意を含む対象は、重複と同じ確認を一回出す（警告のアイコン、警告用の本文は要注意の件数だけ、承認チェック必須）。承認後に整理し、
    /// 要求は分裂配置の一覧の表示順の対象だけで、重複の一覧の選択を含めない。検索で非表示の未確認グループ（SELECTオン）は対象外で整理を妨げない。
    /// モデルが所持集合を変えなければ有効な結果が残るため、探索し直さない。
    /// 検出する誤り：要注意での確認の省略・種類違い、重複の一覧の選択の混入、非表示の未確認による拒否、不要な探索。
    /// </summary>
    [TestMethod]
    public void Organize_WithCautionTarget_ConfirmsOnceAndUsesOnlyTheSplitPlacementList()
    {
        Run(fixture =>
        {
            DuplicateGroup safe = fixture.Groups[0], caution = fixture.Groups[1], hiddenUnverified = fixture.Groups[2];
            DuplicateGroup duplicate = Lists.Group(@"C:\BMS\DupX1", @"C:\BMS\DupX2");
            Lists.Adopt(fixture.DuplicateList, fixture.Library, [duplicate]);
            fixture.DuplicateList.SelectVisible();
            Assert.AreEqual(1, fixture.DuplicateList.CaptureSelectedGroups().Count, "前提：重複の一覧にも選択がある");
            fixture.Write(safe, Safe);
            fixture.Write(caution, Caution(Beta1));
            fixture.SplitList.SelectVisible();
            fixture.SplitList.SearchText = "Show";
            Assert.IsTrue(fixture.SplitList.CaptureSelectedGroups().All(target => target.Group != hiddenUnverified), "前提：未確認のグループは非表示");
            Assert.IsTrue(fixture.State(hiddenUnverified).IsSelected, "前提：非表示でもSELECTはオン");

            DuplicateBatchOrganizeRunResult run = fixture.OnUi(async () => await fixture.Owner.OrganizeSelectedSplitPlacementGroupsAsync()).GetAwaiter().GetResult();

            Assert.AreEqual(DuplicateBatchOrganizeRunStatus.Completed, run.Status, run.Failure?.ToString());
            UiConfirmationRequest confirmation = fixture.Dialogs.ConfirmationRequest!;
            Assert.AreEqual(MessageBoxImage.Warning, confirmation.Icon);
            Assert.AreEqual(string.Format(Resources.Duplicate_batch_organize_confirm_caution_format, 1), confirmation.WarningMessageBoxText);
            Assert.AreEqual(Resources.Duplicate_batch_organize_acknowledgment, confirmation.AcknowledgmentText, "承認チェックが必須");
            Assert.AreEqual(1, fixture.Events.Count(item => item == "confirm"), "確認は一回");
            CollectionAssert.AreEqual(
                new[] { fixture.State(safe).KeepFolder, fixture.State(caution).KeepFolder },
                fixture.Store.Requests.Single().Groups.Select(group => group.KeepDirectory).ToArray(),
                "要求は分裂配置の一覧の表示順で、重複の一覧の選択を含まない");
            Assert.AreEqual(0, fixture.Store.SearchCalls, "有効な結果が残っていれば探索し直さない");
        });
    }

    /// <summary>
    /// 保証：整理の後に有効な結果がなければ、同じ受付の保持中（結果の案内より前）に、取消の合図を渡さず探索して結果を格納し、その後で受付を解放して案内を一回出す。
    /// 整理の取消を要求していても、この探索は取り消されない。解消したグループは結果から消え、引き継いだグループは同じインスタンス、新しいグループは新しいインスタンスになる。
    /// 重複結果は明示的に準備されない（準備の窓口へは <c>requireGroups: false</c> だけが届く）。
    /// 検出する誤り：探索の欠落・受付の解放後の探索・案内より後の探索、取消の巻き込み、インスタンスの引継ぎ漏れ、重複結果の明示準備。
    /// </summary>
    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public void Organize_WhenResultBecomesStale_SearchesAgainInsideTheAdmissionBeforeTheResultNotice(bool cancelRequested)
    {
        Run(fixture =>
        {
            DuplicateGroup alpha = fixture.Groups[0], beta = fixture.Groups[1], gamma = fixture.Groups[2];
            Assert.IsTrue(alpha.Folders.Contains(Alpha1, StringComparer.OrdinalIgnoreCase));
            fixture.Write(alpha, Safe);
            fixture.SplitList.ClearVisible();
            fixture.SplitList.SetSelectOfRows([fixture.SplitList.RowSet.GetFirstRowOfGroup(0)], selected: true);
            Assert.AreEqual(1, fixture.SplitList.CaptureSelectedGroups().Count);
            bool tokenCanceled = false;
            fixture.Store.Handler = (request, progress, token) =>
            {
                if (cancelRequested)
                {
                    fixture.Dialogs.Progress.RequestCancel();
                    tokenCanceled = token.WaitHandle.WaitOne(Deadline);
                }
                return fixture.MergeAlphaAndAddDelta(request, progress);
            };
            List<DuplicateGroup> before = fixture.Library.SplitPlacementGroups!;

            DuplicateBatchOrganizeRunResult run = fixture.OnUi(async () => await fixture.Owner.OrganizeSelectedSplitPlacementGroupsAsync()).GetAwaiter().GetResult();

            Assert.AreEqual(DuplicateBatchOrganizeRunStatus.Completed, run.Status, run.Failure?.ToString());
            if (cancelRequested) { Assert.IsTrue(tokenCanceled, "前提：整理の取消の合図は届いている"); }
            Assert.AreEqual(1, fixture.Store.SearchCalls, "有効な結果がないので一回探索する");
            (bool admissionFree, int messages) = fixture.Store.StoredNotifications.Single();
            Assert.IsFalse(admissionFree, "結果の格納の通知の時点で、受付は保持中");
            Assert.AreEqual(0, messages, "結果の格納の通知の時点で、結果の案内は出ていない");
            Assert.AreEqual(1, fixture.Dialogs.Messages.Count, "結果の案内は一回");
            Assert.IsTrue(fixture.Library.OperationAdmission.CanEnter, "案内の後は受付が解放されている");
            int search = fixture.Events.IndexOf("store-search-split");
            Assert.IsTrue(fixture.Events.IndexOf("store-organize") < search && search < fixture.Events.IndexOf("suppression-end"), "モデルの後、更新の抑制の内側で探索する");
            Assert.IsTrue(fixture.Events.IndexOf("suppression-end") < fixture.Events.IndexOf("message"), "案内は抑制と受付の解放の後");

            List<DuplicateGroup> after = fixture.Library.SplitPlacementGroups!;
            Assert.IsNotNull(after);
            Assert.AreNotSame(before, after);
            Assert.AreEqual(3, after.Count);
            Assert.IsFalse(after.Contains(alpha), "解消したグループは結果から消える");
            Assert.IsTrue(after.Contains(beta) && after.Contains(gamma), "引き継いだグループは同じインスタンス");
            DuplicateGroup added = after.Single(group => group != beta && group != gamma);
            Assert.IsFalse(before.Contains(added), "新しいグループは新しいインスタンス");
            CollectionAssert.AreEqual(new[] { false }, fixture.PreparationCalls.ToArray(), "重複結果の明示準備を行わない（準備の窓口へはrequireGroups:falseだけ）");
        });
    }

    private static readonly SplitPlacementSearchCriteria AppliedNonDefault = new(75, 30, SplitPlacementShareBasis.BothSides);

    /// <summary>
    /// 保証：既定値以外の探索条件を適用中のとき、整理の後に世代が古ければ、適用中の探索条件で探索し直す。
    /// 適用中の探索条件に対する有効な結果が残っていれば、既定値に対する結果でなくても探索しない。
    /// 検出する誤り：探索し直しが既定値を使う（結果の探索条件が巻き戻る）、適用中の値に対して有効な結果があるのに探索する。
    /// </summary>
    [TestMethod]
    [DataRow(true)]
    [DataRow(false)]
    public void Organize_AfterwardUsesAppliedCriteriaForValidityAndSearch(bool resultBecomesStale)
    {
        Run(fixture =>
        {
            fixture.Applied = AppliedNonDefault;
            fixture.Library.SearchSplitPlacementGroups(AppliedNonDefault, null, CancellationToken.None);
            Assert.IsFalse(fixture.Library.HasCurrentSplitPlacementGroups(SplitPlacementSearchCriteria.Default), "前提：結果は適用中の探索条件のもの");
            fixture.Write(fixture.Groups[0], Safe);
            fixture.SplitList.ClearVisible();
            fixture.SplitList.SetSelectOfRows([fixture.SplitList.RowSet.GetFirstRowOfGroup(0)], selected: true);
            fixture.Store.Handler = (request, progress, _) => resultBecomesStale
                ? fixture.MergeAlphaAndAddDelta(request, progress)
                : SplitStore.Complete(request, progress);

            DuplicateBatchOrganizeRunResult run = fixture.OnUi(async () => await fixture.Owner.OrganizeSelectedSplitPlacementGroupsAsync()).GetAwaiter().GetResult();

            Assert.AreEqual(DuplicateBatchOrganizeRunStatus.Completed, run.Status, run.Failure?.ToString());
            Assert.IsTrue(fixture.Library.HasCurrentSplitPlacementGroups(AppliedNonDefault), "適用中の探索条件に対する有効な結果がある");
            Assert.IsFalse(fixture.Library.HasCurrentSplitPlacementGroups(SplitPlacementSearchCriteria.Default));
            if (resultBecomesStale)
            {
                CollectionAssert.AreEqual(new[] { AppliedNonDefault }, fixture.Store.SearchedSplitPlacementCriteria, "適用中の探索条件で探索し直す");
            }
            else
            {
                Assert.AreEqual(0, fixture.Store.SearchCalls, "適用中の探索条件に対して有効な結果があれば探索しない");
            }
        });
    }

    /// <summary>
    /// 保証：探索し直しが失敗しても、受付を解放し、失敗の報告を一回と結果の案内を一回出す。有効な結果はないままで、重複結果は明示的に準備されない。
    /// 検出する誤り：失敗の握りつぶし・重複報告、結果の案内の省略、受付の解放漏れ。
    /// </summary>
    [TestMethod]
    public void Organize_WhenSearchAgainFails_ReportsFailureOnceAndStillNotifiesTheResult()
    {
        Run(fixture =>
        {
            fixture.Write(fixture.Groups[0], Safe);
            fixture.SplitList.ClearVisible();
            fixture.SplitList.SetSelectOfRows([fixture.SplitList.RowSet.GetFirstRowOfGroup(0)], selected: true);
            fixture.Store.SearchFailure = new IOException("split search failed");
            fixture.Store.Handler = (request, progress, _) => fixture.MergeAlphaAndAddDelta(request, progress);

            DuplicateBatchOrganizeRunResult run = fixture.OnUi(async () => await fixture.Owner.OrganizeSelectedSplitPlacementGroupsAsync()).GetAwaiter().GetResult();

            Assert.AreEqual(DuplicateBatchOrganizeRunStatus.Completed, run.Status);
            Assert.IsNotNull(run.Failure);
            Assert.AreEqual(1, fixture.Store.SearchCalls);
            Assert.AreEqual(2, fixture.Dialogs.Messages.Count, "失敗の報告が一回と、結果の案内が一回");
            UiMessageRequest report = fixture.Dialogs.Messages.Single(message => message.Caption == Resources.FileDbMutationReport_Title);
            StringAssert.Contains(report.MessageBoxText, "split search failed");
            Assert.AreEqual(1, fixture.Dialogs.Messages.Count(message => message.Caption == Resources.Duplicate_batch_organize_title), "結果の案内は一回");
            Assert.IsTrue(fixture.Library.OperationAdmission.CanEnter, "受付は解放されている");
            Assert.IsFalse(fixture.Library.HasCurrentSplitPlacementGroups(SplitPlacementSearchCriteria.Default), "有効な結果はないまま（探索が失敗した）");
            CollectionAssert.AreEqual(new[] { false }, fixture.PreparationCalls.ToArray());
        });
    }

    /// <summary>
    /// 保証：重複の一覧からの一括整理は従来どおり、未確認も承認で整理でき（未確認の案内を出さない）、整理後に重複結果を明示的に準備し（requireGroups:true）、
    /// 分裂配置の結果を自動では探索し直さない。
    /// 検出する誤り：未確認の拒否の重複側への波及、重複結果の準備の欠落、分裂配置の探索の波及。
    /// </summary>
    [TestMethod]
    public void OrganizeFromDuplicateList_KeepsExistingBehavior()
    {
        Run(fixture =>
        {
            DuplicateGroup duplicate = Lists.Group(@"C:\BMS\DupX1", @"C:\BMS\DupX2");
            Lists.Adopt(fixture.DuplicateList, fixture.Library, [duplicate]);
            fixture.DuplicateList.SelectVisible();
            Assert.AreEqual(DuplicateMergeVerificationStage.Unverified, fixture.DuplicateList.CaptureSelectedGroups().Single().Verification!.Stage);
            fixture.Store.Handler = (request, progress, _) => fixture.MergeAlphaAndAddDelta(request, progress);

            DuplicateBatchOrganizeRunResult run = fixture.OnUi(async () => await fixture.Owner.OrganizeSelectedGroupsAsync()).GetAwaiter().GetResult();

            Assert.AreEqual(DuplicateBatchOrganizeRunStatus.Completed, run.Status, run.Failure?.ToString());
            Assert.AreEqual(MessageBoxImage.Warning, fixture.Dialogs.ConfirmationRequest!.Icon, "未確認は承認の確認で整理する");
            Assert.AreEqual(1, fixture.Dialogs.Messages.Count, "未確認の案内は出さず、結果の案内だけ");
            Assert.AreEqual(Resources.Duplicate_batch_organize_title, fixture.Dialogs.Messages.Single().Caption);
            CollectionAssert.AreEqual(new[] { true }, fixture.PreparationCalls.ToArray(), "重複結果を明示的に準備する");
            Assert.AreEqual(0, fixture.Store.SearchCalls, "分裂配置の結果は自動で探索し直さない");
            Assert.IsFalse(fixture.Library.HasCurrentSplitPlacementGroups(SplitPlacementSearchCriteria.Default), "有効な結果がないまま（自動では探索し直さない）");
        });
    }
}
