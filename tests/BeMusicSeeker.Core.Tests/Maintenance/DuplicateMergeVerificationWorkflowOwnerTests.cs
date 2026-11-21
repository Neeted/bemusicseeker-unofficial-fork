using BeMusicSeeker.Models;
using BeMusicSeeker.Properties;
using BeMusicSeeker.TestSupport.Components;
using BeMusicSeeker.TestSupport.Files;
using BeMusicSeeker.ViewModels;
using static BeMusicSeeker.TestSupport.Components.DuplicateMergeSafetyTestSupport;
using static BeMusicSeeker.TestSupport.Components.SelectedChartAudioConversionWorkflowOwnerTestSupport;

namespace BeMusicSeeker.Tests;

/// <summary>
/// 統合候補一覧の安全性確認の手順（受付、進捗、取消、書込み、案内）を、本番の受付（<see cref="ChartFileOperationSynchronizer"/>）と実際の管理主体で確認します。
/// 走査はグループごとに待合せできる代替の検査器で時機を制御し、全グループを渡すことは実検査器と実ファイルで確認します。
/// 状態表の読み書きを行う「UIスレッド」は、専用の単一スレッドの同期の文脈で模します。
/// </summary>
[TestClass]
public sealed class DuplicateMergeVerificationWorkflowOwnerTests
{
    private TestWorkspace? ownedWorkspace;

    private TestWorkspace Workspace => ownedWorkspace ?? throw new InvalidOperationException("ケースの可変媒体はTestInitialize後に利用します。");

    [TestInitialize]
    public void InitializeWorkspace() => ownedWorkspace = TestWorkspace.Create(TestRunDirectory.RootDirectory);

    [TestCleanup]
    public void CleanupWorkspace() => ownedWorkspace?.Dispose();

    private static DuplicateMergeGroupInspection Safe()
        => new(new Dictionary<DuplicateMergeVerificationReasonKind, IReadOnlyList<DuplicateMergeFinding>>(), [], DuplicateMergeComparisonSummary.None,
            new Dictionary<string, DuplicateMergeKeepDependentFacts>());

    private static DuplicateMergeGroupInspection WithReason(DuplicateMergeVerificationReasonKind kind)
        => new(new Dictionary<DuplicateMergeVerificationReasonKind, IReadOnlyList<DuplicateMergeFinding>> { [kind] = [new DuplicateMergeFinding(@"C:\S", "x")] },
            [], DuplicateMergeComparisonSummary.None, new Dictionary<string, DuplicateMergeKeepDependentFacts>());

    private static ChartFile Chart(string folder, string name)
        => new(ChartFileKind.Bms, Path.Combine(folder, name + ".bms"), "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa", null, "title", "raw", "artist", string.Empty,
            Path.GetFileName(folder), string.Empty, string.Empty, null, 0, null);

    private static DuplicateGroup Group(params (string Folder, int Charts)[] folders)
        => new(
            [.. folders.SelectMany(folder => Enumerable.Range(0, folder.Charts).Select(index => Chart(folder.Folder, "chart" + index)))],
            [.. folders.Select(folder => folder.Folder)]);

    private static DuplicateGroup Group(params string[] folders) => Group([.. folders.Select(folder => (folder, 1))]);

    /// <summary>ライブラリへの問合せを持たない、一覧の構築入力の代替です。</summary>
    private sealed class Source(IEnumerable<string>? registered = null) : IDuplicateMergeCandidateListSource
    {
        private readonly HashSet<string> registeredRoots = new(registered ?? [], StringComparer.OrdinalIgnoreCase);

        public IReadOnlyDictionary<string, (string Title, string Artist)> ResolveDestinationDisplayPairs(IReadOnlyList<string> folders, CancellationToken cancellationToken)
            => folders.Distinct(StringComparer.OrdinalIgnoreCase).ToDictionary(folder => folder, _ => (string.Empty, string.Empty), StringComparer.OrdinalIgnoreCase);

        public Func<string, bool> CaptureRegisteredBmsRootPredicate() => registeredRoots.Contains;
    }

    /// <summary>グループごとの検査の時機と結果を制御し、呼出しを記録する検査器の代替です。</summary>
    private sealed class ScriptedInspector : IDuplicateMergeSafetyInspector
    {
        internal Func<int, DuplicateMergeInspectionRequest, DuplicateMergeGroupInspection> Handler { get; set; } = (_, _) => Safe();

        internal List<(DuplicateMergeInspectionRequest Request, int ThreadId)> Calls { get; } = [];

        public DuplicateMergeGroupInspection Inspect(DuplicateMergeInspectionRequest request)
        {
            int index;
            lock (Calls)
            {
                index = Calls.Count;
                Calls.Add((request, Environment.CurrentManagedThreadId));
            }
            return Handler(index, request);
        }
    }

    private sealed class Fixture : IDisposable
    {
        private readonly SingleThreadSynchronizationContext ui = new();
        private readonly object library = new();

        internal Fixture(IDuplicateMergeSafetyInspector inspector, params DuplicateGroup[] groups) : this(inspector, new Source(), groups)
        {
        }

        internal Fixture(IDuplicateMergeSafetyInspector inspector, Source source, params DuplicateGroup[] groups)
        {
            Inspector = inspector;
            Dialogs = new RecordingDialogService(Events);
            Owner = new DuplicateMergeCandidateListOwner(_ => false, _ => { });
            Workflow = new DuplicateMergeVerificationWorkflowOwner(Owner, () => Inspector, Admission, Dialogs);
            UiThreadId = Ui(() => Environment.CurrentManagedThreadId);
            Ui(() =>
            {
                Owner.AttachLibrary(library);
                Owner.SetTreeSelected(true);
            });
            Adopt(source, groups);
        }

        internal IDuplicateMergeSafetyInspector Inspector { get; }

        internal EventLog Events { get; } = new();

        internal RecordingDialogService Dialogs { get; }

        internal ChartFileOperationSynchronizer Admission { get; } = new();

        internal DuplicateMergeCandidateListOwner Owner { get; }

        internal DuplicateMergeVerificationWorkflowOwner Workflow { get; }

        internal int UiThreadId { get; }

        /// <summary>UIスレッドの代替で同期の処理を実行します。</summary>
        internal T Ui<T>(Func<T> work) => ui.RunAsync(() => Task.FromResult(work())).GetAwaiter().GetResult();

        internal void Ui(Action work) => ui.RunAsync(work).GetAwaiter().GetResult();

        /// <summary>UIスレッドの代替で非同期の処理を開始します。</summary>
        internal Task<T> UiAsync<T>(Func<Task<T>> work) => ui.RunAsync(work);

        internal void Adopt(Source source, params DuplicateGroup[] groups)
        {
            DuplicateMergeCandidateListPreparation preparation = DuplicateMergeCandidateListOwner.Prepare(library, groups, source, CancellationToken.None);
            Ui(() => Assert.IsTrue(Owner.Adopt(preparation)));
        }

        internal DuplicateMergeCandidateGroupState State(DuplicateGroup group)
        {
            Assert.IsTrue(Owner.TryGetState(group, out DuplicateMergeCandidateGroupState? state));
            return state!;
        }

        public void Dispose() => ui.Dispose();
    }

    /// <summary>検査の開始と再開を、グループ単位で制御する待合せです。</summary>
    private sealed class Gate
    {
        internal TaskCompletionSource<bool> Entered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        internal TaskCompletionSource<bool> Release { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        /// <summary>検査器の側で呼びます。到達を通知し、解放まで待ちます。</summary>
        internal void Block()
        {
            Entered.TrySetResult(true);
            Release.Task.GetAwaiter().GetResult();
        }
    }

    /// <summary>検査器への到達を待ちます。通知が来る前に操作の全体が終端した場合は、その結果（失敗）を伝えて失敗にします。</summary>
    private static Task WaitForEnteredAsync(Gate gate, Task run) => WaitForSignalAsync(gate.Entered.Task, run, "検査の開始");

    /// <summary>通知の到達を期限なしで待ちます。通知が来る前に操作の全体が終端した場合は、その結果（失敗）を伝えて失敗にします。</summary>
    private static async Task WaitForSignalAsync(Task signal, Task run, string name)
    {
        await Task.WhenAny(signal, run);
        if (!signal.IsCompleted)
        {
            await run;
            Assert.Fail(name + "の通知より先に確認が終端しました。");
        }
    }

    /// <summary>進捗画面の代替が、最初の報告を記録したときに完了する通知を登録します。操作を開始する前に呼びます。</summary>
    private static Task FirstProgressReport(RecordingDialogService dialogs)
    {
        var reported = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        dialogs.ProgressReported = (_, _) => reported.TrySetResult();
        return reported.Task;
    }

    /// <summary>
    /// 保証：確認中は共通の受付を保持して（他の取得が失敗する）、進捗に「何番目／総数」と代表フォルダ名（グループのKEEPのフォルダ名）を示し、
    /// 結果の書込み（UIスレッド）の時点ではまだ受付を保持し、その後に解放してから、結果を一度だけ案内する。走査はUIスレッド以外で、書込みで確認を終えたグループのSELECTを段階によらずオフにする（三グループとも前提でオン）。
    /// 検出する誤り：受付の取得漏れ、書込みより前の解放（書込みの前に別の変更が受理される）、遅すぎる解放（案内の時点で保持している）、進捗の欠落、KEEPでない代表名、UIスレッドでの走査、書込みのUIスレッド外実行、案内の重複、
    /// 安全のSELECTを残す・オンにする、案内に「チェックを入れました」の文を含める、確認の後に選択件数と捕捉件数が0にならない。
    /// </summary>
    [TestMethod]
    public async Task Verify_HoldsAdmissionWhileCheckingAndReleasesBeforeTheSingleResultNotice()
    {
        var inspector = new ScriptedInspector();
        DuplicateGroup g1 = Group((@"C:\S\alpha1", 1), (@"C:\S\alpha2", 2)), g2 = Group(@"C:\S\beta1", @"C:\S\beta2"), g3 = Group(@"C:\S\gamma1", @"C:\S\gamma2");
        using var fixture = new Fixture(inspector, g1, g2, g3);
        fixture.Ui(() => fixture.Owner.SelectVisible());
        var gate = new Gate();
        inspector.Handler = (index, _) => index switch
        {
            0 => Run(() => gate.Block(), Safe()),
            1 => WithReason(DuplicateMergeVerificationReasonKind.AudioExtension),
            _ => WithReason(DuplicateMergeVerificationReasonKind.Link)
        };
        bool? admissionFreeAtNotice = null;
        fixture.Dialogs.MessageObserver = _ => admissionFreeAtNotice ??= fixture.Admission.CanEnter;
        var writeThreads = new List<int>();
        var admissionHeldAtWrite = new List<bool>();
        fixture.Owner.DisplayInvalidated += (_, _) =>
        {
            writeThreads.Add(Environment.CurrentManagedThreadId);
            // 書込みの時点で他の取得を試み、取得できてしまった場合は直ちに返して後続の判定を乱さない。
            bool acquired = fixture.Admission.TryEnter(out IDisposable? probe);
            probe?.Dispose();
            admissionHeldAtWrite.Add(!acquired);
        };
        Task firstReport = FirstProgressReport(fixture.Dialogs);

        Task<DuplicateMergeVerificationRunResult> run = fixture.UiAsync(() => fixture.Workflow.VerifySelectedAsync());
        try
        {
            await WaitForEnteredAsync(gate, run);
            // 進捗の報告（UIスレッド）と検査器への到達（走査のスレッド）の順序は保証されないため、報告の到達も待つ。
            await WaitForSignalAsync(firstReport, run, "進捗の最初の報告");

            Assert.IsFalse(fixture.Admission.CanEnter, "確認中は受付を保持します。");
            Assert.IsFalse(fixture.Admission.TryEnter(out _));
            Assert.AreEqual(Resources.Duplicate_merge_candidate_verify_progress_title, fixture.Dialogs.ProgressRequest!.Title);
            Assert.AreEqual(0, fixture.Dialogs.ProgressReports.Count(report => report.Message.StartsWith("[2/", StringComparison.Ordinal)));
            Assert.AreEqual((0, "[1/3] alpha2"), fixture.Dialogs.ProgressReports[0], "代表フォルダ名はKEEPのフォルダ名です。");
        }
        finally { gate.Release.TrySetResult(true); }
        DuplicateMergeVerificationRunResult result = await run;

        Assert.AreEqual(new DuplicateMergeVerificationRunResult(DuplicateMergeVerificationRunStatus.Completed, 3, 1, 1, 1, 0), result);
        Assert.AreEqual(true, admissionFreeAtNotice, "案内の時点では受付を解放済みです。");
        Assert.AreEqual(1, fixture.Dialogs.Messages.Count, "結果の案内は一度だけです。");
        Assert.AreEqual("3グループを確認しました。安全 1／要注意 1／統合不可 1。", fixture.Dialogs.Messages[0].MessageBoxText, "ja-JP の書式は「チェックを入れました」の文を含みません。");
        Assert.AreEqual(string.Format(Resources.Duplicate_merge_candidate_verify_result_format, 3, 1, 1, 1), fixture.Dialogs.Messages[0].MessageBoxText);
        Assert.IsTrue(inspector.Calls.All(call => call.ThreadId != fixture.UiThreadId), "走査はUIスレッド以外です。");
        Assert.IsTrue(writeThreads.Count > 0 && writeThreads.All(id => id == fixture.UiThreadId), "書込みはUIスレッドです。");
        Assert.IsTrue(admissionHeldAtWrite.Count > 0 && admissionHeldAtWrite.All(held => held), "結果の書込みの時点では受付を保持しています（他の取得が失敗します）。");
        Assert.IsFalse(fixture.State(g1).IsSelected, "安全のSELECTも確認で消費してオフにします。");
        Assert.IsFalse(fixture.State(g2).IsSelected, "要注意のSELECTはオフです。");
        Assert.IsFalse(fixture.State(g3).IsSelected, "統合不可のSELECTはオフです。");
        Assert.IsFalse(fixture.State(g3).CanSelect, "統合不可は無効のままです。");
        Assert.AreEqual(0, fixture.Owner.SelectedGroupCount);
        Assert.AreEqual(0, fixture.Ui(() => fixture.Owner.CaptureSelectedGroups().Count), "続けて整理を押しても対象がありません。");
        Assert.AreEqual(Resources.Duplicate_merge_candidate_status_safe, fixture.State(g1).Verification.FormatStatus());
        Assert.IsTrue(fixture.Admission.CanEnter, "終端後は次の確認を受け付けます。");
    }

    private static T Run<T>(Action action, T value)
    {
        action();
        return value;
    }

    /// <summary>
    /// 保証：共通の受付を取得できないときは、Busyの案内を一件だけ出し、検査・進捗・状態の変更をしない。受付を握っていた側を解放すれば、次の確認を受け付ける。
    /// 検出する誤り：競合時に検査や進捗を始める、Busy案内の重複・欠落、状態（SELECT・確認結果）の変更、競合した確認による受付の奪取。
    /// </summary>
    [TestMethod]
    public async Task Verify_WhenAdmissionIsBusy_ShowsBusyNoticeOnceAndChangesNothing()
    {
        var inspector = new ScriptedInspector();
        DuplicateGroup group = Group(@"C:\S\a1", @"C:\S\a2");
        using var fixture = new Fixture(inspector, group);
        fixture.Ui(() => fixture.Owner.SelectVisible());
        Assert.IsTrue(fixture.Admission.TryEnter(out IDisposable? lease));

        DuplicateMergeVerificationRunResult busy = await fixture.UiAsync(() => fixture.Workflow.VerifySelectedAsync());

        Assert.AreEqual(DuplicateMergeVerificationRunResult.Busy, busy);
        Assert.AreEqual(1, fixture.Dialogs.Messages.Count);
        Assert.AreEqual(Resources.Warn_LibraryOperationBusy, fixture.Dialogs.Messages[0].MessageBoxText);
        Assert.IsNull(fixture.Dialogs.ProgressRequest, "進捗を表示しません。");
        Assert.AreEqual(0, inspector.Calls.Count, "検査しません。");
        Assert.IsTrue(fixture.State(group).IsSelected, "SELECTは変えません。");
        Assert.AreEqual(Resources.Duplicate_merge_candidate_status_unverified, fixture.State(group).Verification.FormatStatus());

        lease!.Dispose();
        Assert.AreEqual(DuplicateMergeVerificationRunStatus.Completed, (await fixture.UiAsync(() => fixture.Workflow.VerifySelectedAsync())).Status, "解放後は受け付けます。");
    }

    /// <summary>
    /// 保証：共通の受付が閉じている（終了の要求中など）ときは、Busyの案内を含めて何も案内せず、検査・進捗・状態の変更をせずに未開始で返す。
    /// 検出する誤り：受付の閉鎖による不成立をBusyとして案内する、閉じた受付のもとで検査や進捗を始める、状態（SELECT・確認結果）の変更。
    /// </summary>
    [TestMethod]
    public async Task Verify_WhenAdmissionIsClosed_ReturnsNotStartedWithoutNotice()
    {
        var inspector = new ScriptedInspector();
        DuplicateGroup group = Group(@"C:\S\a1", @"C:\S\a2");
        using var fixture = new Fixture(inspector, group);
        fixture.Ui(() => fixture.Owner.SelectVisible());
        fixture.Admission.CloseAdmission();

        DuplicateMergeVerificationRunResult toolbar = await fixture.UiAsync(() => fixture.Workflow.VerifySelectedAsync());
        DuplicateMergeVerificationRunResult menu = await fixture.UiAsync(() => fixture.Workflow.VerifyGroupsOfRowsAsync([fixture.Owner.RowSet[0]]));

        Assert.AreEqual(DuplicateMergeVerificationRunResult.NotStarted, toolbar);
        Assert.AreEqual(DuplicateMergeVerificationRunResult.NotStarted, menu);
        Assert.AreEqual(-1, fixture.Events.IndexOf("message"), "受付の閉鎖による不成立は案内しません。");
        Assert.AreEqual(-1, fixture.Events.IndexOf("progress"), "進捗を表示しません。");
        Assert.AreEqual(0, inspector.Calls.Count, "検査しません。");
        Assert.IsTrue(fixture.State(group).IsSelected, "SELECTは変えません。");
        Assert.AreEqual(Resources.Duplicate_merge_candidate_status_unverified, fixture.State(group).Verification.FormatStatus());
    }

    /// <summary>
    /// 保証：対象が0件なら、受付の取得も進捗も案内もしない（ツールバー・右クリックメニューのどちらでも）。
    /// 検出する誤り：0件でも受付を取得する・進捗を出す、空の結果の案内。
    /// </summary>
    [TestMethod]
    public async Task Verify_WithNoTargets_DoesNothing()
    {
        var inspector = new ScriptedInspector();
        using var fixture = new Fixture(inspector, Group(@"C:\S\a1", @"C:\S\a2"));
        bool admissionEntered = false;
        fixture.Admission.AvailabilityChanged += (_, _) => admissionEntered = true;

        DuplicateMergeVerificationRunResult toolbar = await fixture.UiAsync(() => fixture.Workflow.VerifySelectedAsync());
        DuplicateMergeVerificationRunResult menu = await fixture.UiAsync(() => fixture.Workflow.VerifyGroupsOfRowsAsync([]));

        Assert.AreEqual(DuplicateMergeVerificationRunResult.NoTargets, toolbar);
        Assert.AreEqual(DuplicateMergeVerificationRunResult.NoTargets, menu);
        Assert.IsFalse(admissionEntered, "受付を取得しません。");
        Assert.AreEqual(-1, fixture.Events.IndexOf("progress"), "進捗を出しません。");
        Assert.AreEqual(-1, fixture.Events.IndexOf("message"), "案内を出しません。");
        Assert.AreEqual(0, inspector.Calls.Count);
    }

    /// <summary>
    /// 保証：右クリックメニューの確認は、選択行が属するグループを、グループごとに一度、SELECTの状態によらず確認する。登録範囲のフォルダと、
    /// 簡易確認の値（確認の開始時の値。既定の有効と異なる無効を設定する）を、案内なしですべての要求で検査器へ渡し、確認前にオンのグループも、確認後のSELECTはオフになる。
    /// 検出する誤り：同じグループの重複確認、SELECTがオンのグループへの絞込み、登録範囲・簡易確認の値の受渡しの欠落（既定値のままの受渡し）、無効にする操作での案内。
    /// </summary>
    [TestMethod]
    public async Task VerifyGroupsOfRows_ChecksEachGroupOnceWithRegisteredFoldersAndQuickCompareValue()
    {
        var inspector = new ScriptedInspector();
        DuplicateGroup a = Group(@"C:\S\a1", @"C:\S\a2"), b = Group(@"C:\S\b1", @"C:\S\b2");
        using var fixture = new Fixture(inspector, new Source([@"C:\S\a1", @"C:\S\a2"]), a, b);
        await fixture.Workflow.SetQuickCompareAsync(false);
        Assert.AreEqual(0, fixture.Dialogs.Messages.Count, "無効にする操作では案内しません。");
        Assert.IsFalse(fixture.State(a).CanSelect, "前提：登録範囲を二つ持つグループは統合不可で、SELECTできません。");
        fixture.Ui(() => fixture.Owner.ToggleSelect(fixture.Owner.RowSet[2]));
        Assert.IsTrue(fixture.State(b).IsSelected, "前提：確認の前にbだけがオンです。");

        DuplicateMergeVerificationRunResult result = await fixture.UiAsync(() => fixture.Workflow.VerifyGroupsOfRowsAsync(
            [fixture.Owner.RowSet[0], fixture.Owner.RowSet[1], fixture.Owner.RowSet[2]]));

        Assert.AreEqual(2, result.Checked, "選択した三行は二つのグループに属し、SELECTの状態によらず確認します。");
        Assert.AreEqual(2, inspector.Calls.Count);
        Assert.IsTrue(inspector.Calls.All(call => !call.Request.SizeAndTimeOnly), "簡易確認の値（無効）をすべての要求へ渡します。");
        DuplicateMergeInspectionRequest registeredRequest = inspector.Calls.Select(call => call.Request).Single(request => request.RegisteredFolders.Count == 2);
        CollectionAssert.AreEqual(new[] { @"C:\S\a1", @"C:\S\a2" }, registeredRequest.RegisteredFolders.ToArray());
        Assert.IsFalse(fixture.State(b).IsSelected, "確認前にオンでも、確認後はオフです。");
        Assert.IsFalse(fixture.State(a).IsSelected, "統合不可のグループは確認後もオフ（安全でもオンにしません）で、無効のままです。");
    }

    /// <summary>
    /// 保証：簡易確認の値は、管理主体を新しく作った直後に有効で、組立てまでに注意の案内を出さない。チェックを外す操作では案内せず、入れる操作のたびに一件案内する。
    /// 検出する誤り：既定がオフ、起動時の既定の有効での案内、外す操作での案内、入れる操作での案内の欠落・重複、値の不保持。
    /// </summary>
    [TestMethod]
    public async Task QuickCompare_DefaultsOnWithoutNoticeAndNotifiesOnlyWhenTheCheckIsTurnedOn()
    {
        var inspector = new ScriptedInspector();
        using var fixture = new Fixture(inspector, Group(@"C:\S\a1", @"C:\S\a2"));
        Assert.IsTrue(fixture.Owner.QuickCompareEnabled, "生成直後の値は有効です。");
        Assert.AreEqual(0, fixture.Dialogs.Messages.Count, "起動から構成の組立てまでに注意の案内を出しません。");

        await fixture.Workflow.SetQuickCompareAsync(false);
        Assert.IsFalse(fixture.Owner.QuickCompareEnabled);
        Assert.AreEqual(0, fixture.Dialogs.Messages.Count, "外す操作では案内しません。");

        await fixture.Workflow.SetQuickCompareAsync(true);
        Assert.IsTrue(fixture.Owner.QuickCompareEnabled);
        Assert.AreEqual(1, fixture.Dialogs.Messages.Count, "入れる操作では一件案内します。");
        Assert.AreEqual(Resources.Duplicate_merge_candidate_quick_compare_notice, fixture.Dialogs.Messages[0].MessageBoxText);

        await fixture.Workflow.SetQuickCompareAsync(false);
        await fixture.Workflow.SetQuickCompareAsync(true);
        Assert.AreEqual(2, fixture.Dialogs.Messages.Count, "入れる操作のたびに一件です。");
    }

    /// <summary>
    /// 保証：チェックに触れずに確認を実行すると、すべての要求で簡易確認が有効（既定値）として検査器へ渡り、確認の実行では注意の案内を出さない。
    /// 検出する誤り：既定のオフのまま検査器へ渡す、既定の値を確認の入力へ反映しない、確認で注意の案内を出す。
    /// </summary>
    [TestMethod]
    public async Task Verify_WithoutTouchingTheCheck_PassesTheEnabledDefaultToEveryRequest()
    {
        var inspector = new ScriptedInspector();
        DuplicateGroup a = Group(@"C:\S\a1", @"C:\S\a2"), b = Group(@"C:\S\b1", @"C:\S\b2");
        using var fixture = new Fixture(inspector, a, b);
        fixture.Ui(() => fixture.Owner.SelectVisible());

        DuplicateMergeVerificationRunResult result = await fixture.UiAsync(() => fixture.Workflow.VerifySelectedAsync());

        Assert.AreEqual(2, result.Checked);
        Assert.AreEqual(2, inspector.Calls.Count);
        Assert.IsTrue(inspector.Calls.All(call => call.Request.SizeAndTimeOnly), "既定の有効をすべての要求へ渡します。");
        Assert.IsFalse(
            fixture.Dialogs.Messages.Any(message => message.MessageBoxText == Resources.Duplicate_merge_candidate_quick_compare_notice),
            "確認の実行は注意の案内を出しません。");
    }

    /// <summary>
    /// 保証：取消は、処理中のグループを最後まで処理して書き込み（SELECTはオフ）、着手しなかったグループの確認結果とSELECTは確認前のままにする。結果の案内は「未確認」の件数を加える。
    /// 検出する誤り：処理中のグループの破棄、未着手のグループの書込み・SELECTの変更、案内の未確認の欠落、取消後も走査を続ける。
    /// </summary>
    [TestMethod]
    public async Task Verify_Cancel_FinishesCurrentGroupAndLeavesUnstartedGroupsUntouched()
    {
        var inspector = new ScriptedInspector();
        DuplicateGroup g1 = Group(@"C:\S\a1", @"C:\S\a2"), g2 = Group(@"C:\S\b1", @"C:\S\b2"), g3 = Group(@"C:\S\c1", @"C:\S\c2");
        using var fixture = new Fixture(inspector, g1, g2, g3);
        fixture.Ui(() => fixture.Owner.SelectVisible());
        var gate = new Gate();
        inspector.Handler = (index, _) => index == 0 ? Run(() => gate.Block(), Safe()) : Safe();

        Task<DuplicateMergeVerificationRunResult> run = fixture.UiAsync(() => fixture.Workflow.VerifySelectedAsync());
        try
        {
            await WaitForEnteredAsync(gate, run);
            fixture.Dialogs.RequestProgressCancellation();
            ConcurrencyProbe.WaitUntil(() => fixture.Dialogs.ProgressCancellationObserved, run, "進捗画面の取消が検知されること");
            // 取消の検知（進捗画面の引数への記録）は、走査への取消の受渡しより先に起こる。両者はUIスレッドの一続きの処理なので、
            // UIスレッドの代替を一巡させて、受渡しまで終えてから処理中のグループを解放する。解放が受渡しより先だと、走査が次のグループへ進める。
            await fixture.UiAsync(() => Task.FromResult(true));
        }
        finally { gate.Release.TrySetResult(true); }
        DuplicateMergeVerificationRunResult result = await run;

        Assert.AreEqual(new DuplicateMergeVerificationRunResult(DuplicateMergeVerificationRunStatus.Cancelled, 1, 1, 0, 0, 2), result);
        Assert.AreEqual(1, inspector.Calls.Count, "取消後は新しいグループを始めません。");
        Assert.AreEqual(Resources.Duplicate_merge_candidate_status_safe, fixture.State(g1).Verification.FormatStatus(), "処理中のグループは最後まで処理して書き込みます。");
        Assert.IsFalse(fixture.State(g1).IsSelected, "処理したグループのSELECTはオフです。");
        foreach (DuplicateGroup untouched in new[] { g2, g3 })
        {
            Assert.AreEqual(Resources.Duplicate_merge_candidate_status_unverified, fixture.State(untouched).Verification.FormatStatus());
            Assert.IsTrue(fixture.State(untouched).IsSelected, "未着手のグループのSELECTは確認前のままです。");
        }
        Assert.AreEqual(1, fixture.Dialogs.Messages.Count);
        Assert.AreEqual(string.Format(Resources.Duplicate_merge_candidate_verify_result_cancelled_format, 1, 1, 0, 0, 2), fixture.Dialogs.Messages[0].MessageBoxText);
        Assert.AreEqual("1グループを確認しました。安全 1／要注意 0／統合不可 0／未確認 2。", fixture.Dialogs.Messages[0].MessageBoxText);
        Assert.IsTrue(fixture.Admission.CanEnter);
    }

    /// <summary>
    /// 保証：確認中に状態表から消えたグループ（結果が失効して対象が消えた）の結果は捨て、結果の案内のどの件数にも数えない。残ったグループは書き込む。
    /// 検出する誤り：消えたグループの結果の書込み、案内の件数への算入。
    /// </summary>
    [TestMethod]
    public async Task Verify_DiscardsResultsOfGroupsThatVanishedDuringTheCheck()
    {
        var inspector = new ScriptedInspector();
        DuplicateGroup g1 = Group(@"C:\S\a1", @"C:\S\a2"), g2 = Group(@"C:\S\b1", @"C:\S\b2");
        var source = new Source();
        using var fixture = new Fixture(inspector, source, g1, g2);
        fixture.Ui(() => fixture.Owner.SelectVisible());
        var gate = new Gate();
        inspector.Handler = (index, _) => index == 0 ? Run(() => gate.Block(), Safe()) : WithReason(DuplicateMergeVerificationReasonKind.NestedChartFolder);

        Task<DuplicateMergeVerificationRunResult> run = fixture.UiAsync(() => fixture.Workflow.VerifySelectedAsync());
        try
        {
            await WaitForEnteredAsync(gate, run);
            fixture.Adopt(source, g2);
        }
        finally { gate.Release.TrySetResult(true); }
        DuplicateMergeVerificationRunResult result = await run;

        Assert.AreEqual(new DuplicateMergeVerificationRunResult(DuplicateMergeVerificationRunStatus.Completed, 1, 0, 1, 0, 0), result);
        Assert.IsFalse(fixture.Owner.TryGetState(g1, out _));
        Assert.AreEqual(string.Format(Resources.Duplicate_merge_candidate_verify_result_format, 1, 0, 1, 0), fixture.Dialogs.Messages.Single().MessageBoxText,
            "消えたグループの結果は案内の件数へ数えません。");
    }

    /// <summary>
    /// 保証：入出力以外の予期しない例外は、完了済みのグループの結果を書き込み、受付を解放した後に呼出元へ伝える（結果の案内は出さず、失敗は呼出元が一度だけ通知する）。
    /// 検出する誤り：例外での受付の保持、完了済みのグループの結果の破棄、例外の握りつぶし、失敗後の結果の案内。
    /// </summary>
    [TestMethod]
    public async Task Verify_UnexpectedFailure_WritesCompletedGroupsReleasesAdmissionAndRethrows()
    {
        var inspector = new ScriptedInspector();
        DuplicateGroup g1 = Group(@"C:\S\a1", @"C:\S\a2"), g2 = Group(@"C:\S\b1", @"C:\S\b2"), g3 = Group(@"C:\S\c1", @"C:\S\c2");
        using var fixture = new Fixture(inspector, g1, g2, g3);
        fixture.Ui(() => fixture.Owner.SelectVisible());
        var failure = new InvalidOperationException("scripted failure");
        inspector.Handler = (index, _) => index == 1 ? throw failure : Safe();

        InvalidOperationException thrown = await Assert.ThrowsExactlyAsync<InvalidOperationException>(() => fixture.UiAsync(() => fixture.Workflow.VerifySelectedAsync()));

        Assert.AreSame(failure, thrown);
        Assert.AreEqual(Resources.Duplicate_merge_candidate_status_safe, fixture.State(g1).Verification.FormatStatus(), "完了済みのグループの結果は書き込みます。");
        Assert.AreEqual(Resources.Duplicate_merge_candidate_status_unverified, fixture.State(g2).Verification.FormatStatus());
        Assert.AreEqual(Resources.Duplicate_merge_candidate_status_unverified, fixture.State(g3).Verification.FormatStatus());
        Assert.IsFalse(fixture.State(g1).IsSelected, "完了済みのグループのSELECTはオフです。");
        Assert.IsTrue(fixture.State(g2).IsSelected, "例外を出したグループのSELECTは変えません。");
        Assert.IsTrue(fixture.State(g3).IsSelected, "未着手のグループのSELECTは変えません。");
        Assert.IsTrue(fixture.Admission.CanEnter, "受付を解放します。");
        Assert.AreEqual(0, fixture.Dialogs.Messages.Count, "結果の案内は出しません。");
    }

    /// <summary>
    /// 保証：別のグループとの重なりの判定には、現在の重複結果の全グループ（確認対象外・検索で非表示のグループを含む）のフォルダを渡す。
    /// 実検査器と実ファイルで、SELECTがオフで非表示の別グループのフォルダが子孫にあるグループが、統合不可（別のグループと重複）になる。
    /// 検出する誤り：確認対象のグループだけを渡す・表示中のグループだけを渡す（別グループの見落とし）。
    /// </summary>
    [TestMethod]
    public async Task Verify_PassesAllGroupFoldersIncludingHiddenAndUnselectedGroups()
    {
        string a = Workspace.GetPath("g/a"), b = Workspace.GetPath("g/b"), hiddenFolder = Path.Combine(a, "inner");
        Directory.CreateDirectory(hiddenFolder);
        Directory.CreateDirectory(b);
        DuplicateGroup target = Group((a, 1), (b, 1)), hidden = Group(hiddenFolder);
        using var fixture = new Fixture(new DuplicateMergeSafetyInspector(new MemoryOwnedChartSource()), target, hidden);
        fixture.Ui(() =>
        {
            fixture.Owner.SelectVisible();
            fixture.Owner.SearchText = "folder:" + Path.GetFileName(b);
        });
        Assert.IsFalse(fixture.Owner.RowSet.Any(row => ReferenceEquals(row.State.Group, hidden)), "前提：別のグループは検索で非表示です。");

        DuplicateMergeVerificationRunResult result = await fixture.UiAsync(() => fixture.Workflow.VerifySelectedAsync());

        Assert.AreEqual(1, result.Unmergeable);
        Assert.AreEqual(
            string.Format(Resources.Duplicate_merge_candidate_status_unmergeable_format, Resources.Duplicate_merge_candidate_reason_other_group),
            fixture.State(target).Verification.FormatStatus());
        Assert.IsFalse(fixture.State(target).IsSelected);
        Assert.AreEqual(Resources.Duplicate_merge_candidate_status_unverified, fixture.State(hidden).Verification.FormatStatus(), "確認対象外のグループは変えません。");
        Assert.IsTrue(fixture.State(hidden).IsSelected, "検索で非表示で確認対象外のグループは、SELECTがオンのまま変わりません。");
    }

    /// <summary>
    /// 保証：登録ルートによる統合不可のグループを右クリックから確認しても、確認後に登録ルートの理由を保ち、SELECTは無効（オフ）のままになる。
    /// 検出する誤り：確認の結果で登録ルートの統合不可を上書きして消す（安全・要注意へ変わる、SELECTが有効になる）。
    /// </summary>
    [TestMethod]
    public async Task Verify_KeepsRegisteredRootUnmergeableAfterVerification()
    {
        string a = Workspace.GetPath("g/a"), b = Workspace.GetPath("g/b");
        Directory.CreateDirectory(a);
        Directory.CreateDirectory(b);
        DuplicateGroup group = Group((a, 1), (b, 1));
        using var fixture = new Fixture(new DuplicateMergeSafetyInspector(new MemoryOwnedChartSource()), new Source([a, b]), group);

        await fixture.UiAsync(() => fixture.Workflow.VerifyGroupsOfRowsAsync([fixture.Owner.RowSet[0]]));

        DuplicateMergeCandidateGroupState state = fixture.State(group);
        Assert.AreEqual(DuplicateMergeVerificationStage.Unmergeable, state.Verification.Stage);
        Assert.AreEqual(DuplicateMergeVerificationReasonKind.RegisteredRoot, state.Verification.Reasons[0].Kind);
        Assert.IsFalse(state.CanSelect);
        Assert.IsFalse(state.IsSelected);
        Assert.IsFalse(fixture.Owner.ToggleSelect(fixture.Owner.RowSet[0]), "SELECTは無効のままです。");
    }
}
