using BeMusicSeeker.Models;
using BeMusicSeeker.Properties;
using BeMusicSeeker.TestSupport.Components;
using BeMusicSeeker.ViewModels;
using static BeMusicSeeker.TestSupport.Components.AudioDuplicateCandidateTestSupport;
using static BeMusicSeeker.TestSupport.Components.SelectedChartAudioConversionWorkflowOwnerTestSupport;

namespace BeMusicSeeker.Tests;

/// <summary>
/// 整理候補一覧の確認の手順（受付、進捗、取消、書込み、案内）を、本番の受付（<see cref="ChartFileOperationSynchronizer"/>）と実際の管理主体で確認します。
/// 検査はフォルダごとに待合せできる代替の検査器で時機を制御し、状態表の読み書きを行う「UIスレッド」は専用の単一スレッドの同期の文脈で模します。
/// </summary>
[TestClass]
public sealed class AudioDuplicateVerificationWorkflowOwnerTests
{
    /// <summary>フォルダごとの検査の時機と結果を制御し、呼出しを記録する検査器の代替です。</summary>
    private sealed class ScriptedInspector : IAudioDuplicateFolderInspector
    {
        internal Func<int, AudioDuplicateInspectionRequest, AudioDuplicateFolderInspection> Handler { get; set; }
            = (_, _) => Inspection(AudioDuplicateInspectionStage.Safe, ["x"]);

        internal List<(AudioDuplicateInspectionRequest Request, int ThreadId)> Calls { get; } = [];

        public AudioDuplicateFolderInspection Inspect(AudioDuplicateInspectionRequest request)
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

    /// <summary>検査の開始と再開を、フォルダ単位で制御する待合せです。</summary>
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

    private sealed class Fixture : IDisposable
    {
        private readonly SingleThreadSynchronizationContext ui = new();
        private readonly object library = new();
        private readonly Func<string, bool> predicate = _ => false;

        internal Fixture(ScriptedInspector inspector, params AudioDuplicateFolder[] folders)
        {
            Inspector = inspector;
            Dialogs = new RecordingDialogService(Events);
            Owner = new AudioDuplicateCandidateListOwner(_ => false, _ => { });
            Workflow = new AudioDuplicateVerificationWorkflowOwner(Owner, () => Inspector, () => { PredicateCaptures++; return predicate; }, Admission, Dialogs);
            UiThreadId = Ui(() => Environment.CurrentManagedThreadId);
            Ui(() => Adopt(Owner, library, folders));
            Folders = folders;
            Predicate = predicate;
        }

        internal object Library => library;

        internal ScriptedInspector Inspector { get; }

        internal EventLog Events { get; } = new();

        internal RecordingDialogService Dialogs { get; }

        internal ChartFileOperationSynchronizer Admission { get; } = new();

        internal AudioDuplicateCandidateListOwner Owner { get; }

        internal AudioDuplicateVerificationWorkflowOwner Workflow { get; }

        internal AudioDuplicateFolder[] Folders { get; }

        internal Func<string, bool> Predicate { get; }

        internal int PredicateCaptures { get; private set; }

        internal int UiThreadId { get; }

        internal T Ui<T>(Func<T> work) => ui.RunAsync(() => Task.FromResult(work())).GetAwaiter().GetResult();

        internal void Ui(Action work) => ui.RunAsync(work).GetAwaiter().GetResult();

        internal Task<T> UiAsync<T>(Func<Task<T>> work) => ui.RunAsync(work);

        internal AudioDuplicateCandidateState State(int index) => AudioDuplicateCandidateTestSupport.State(Owner, Folders[index]);

        public void Dispose() => ui.Dispose();
    }

    private static async Task WaitForSignalAsync(Task signal, Task run, string name)
    {
        await Task.WhenAny(signal, run);
        if (!signal.IsCompleted)
        {
            await run;
            Assert.Fail(name + "の通知より先に確認が終端しました。");
        }
    }

    private static Task FirstProgressReport(RecordingDialogService dialogs)
    {
        var reported = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        dialogs.ProgressReported = (_, _) => reported.TrySetResult();
        return reported.Task;
    }

    private static T Run<T>(Action action, T value)
    {
        action();
        return value;
    }

    /// <summary>
    /// 保証：確認中は共通の受付を保持して（他の取得が失敗する）、進捗に「何番目／総数」とフォルダ名を示し、結果の書込み（UIスレッド）の時点ではまだ受付を保持し、その後に解放してから、結果を一度だけ案内する。
    /// 検査はUIスレッド以外で、フォルダごとに逐次、配下の別の対象フォルダの判定を確認の開始時に一回だけ捕捉して渡し、書込みで確認を終えたフォルダのSELECTを段階によらずオフにする。
    /// 検出する誤り：受付の取得漏れ、書込みより前の解放、遅すぎる解放、進捗の欠落、UIスレッドでの検査、判定の捕捉の重複・欠落、案内の重複、SELECTの消費漏れ。
    /// </summary>
    [TestMethod]
    public async Task Verify_HoldsAdmissionWhileCheckingAndReleasesBeforeTheSingleResultNotice()
    {
        var inspector = new ScriptedInspector();
        AudioDuplicateFolder a = Folder(@"C:\S\alpha"), b = Folder(@"C:\S\beta"), c = Folder(@"C:\S\gamma");
        using var fixture = new Fixture(inspector, a, b, c);
        fixture.Ui(() => fixture.Owner.SelectVisible());
        var gate = new Gate();
        inspector.Handler = (index, _) => index switch
        {
            0 => Run(gate.Block, Inspection(AudioDuplicateInspectionStage.Safe, ["x"])),
            1 => Inspection(AudioDuplicateInspectionStage.Caution, ["x", "y"], ["x"]),
            _ => Unorganizable(DuplicateMergeVerificationReasonKind.Link, c.Folder)
        };
        bool? admissionFreeAtNotice = null;
        fixture.Dialogs.MessageObserver = _ => admissionFreeAtNotice ??= fixture.Admission.CanEnter;
        var writeThreads = new List<int>();
        var admissionHeldAtWrite = new List<bool>();
        fixture.Owner.DisplayInvalidated += (_, _) =>
        {
            writeThreads.Add(Environment.CurrentManagedThreadId);
            bool acquired = fixture.Admission.TryEnter(out IDisposable? probe);
            probe?.Dispose();
            admissionHeldAtWrite.Add(!acquired);
        };
        Task firstReport = FirstProgressReport(fixture.Dialogs);

        Task<AudioDuplicateVerificationRunResult> run = fixture.UiAsync(() => fixture.Workflow.VerifySelectedAsync());
        try
        {
            await WaitForSignalAsync(gate.Entered.Task, run, "検査の開始");
            await WaitForSignalAsync(firstReport, run, "進捗の最初の報告");

            Assert.IsFalse(fixture.Admission.CanEnter, "確認中は受付を保持します。");
            Assert.AreEqual(Resources.Audio_duplicate_verify_progress_title, fixture.Dialogs.ProgressRequest!.Title);
            Assert.AreEqual((0, "[1/3] alpha"), fixture.Dialogs.ProgressReports[0]);
        }
        finally { gate.Release.TrySetResult(true); }
        AudioDuplicateVerificationRunResult result = await run;

        Assert.AreEqual(new AudioDuplicateVerificationRunResult(DuplicateMergeVerificationRunStatus.Completed, 3, 1, 1, 1, 0), result);
        Assert.AreEqual(true, admissionFreeAtNotice, "案内の時点では受付を解放済みです。");
        Assert.AreEqual(1, fixture.Dialogs.Messages.Count, "結果の案内は一度だけです。");
        Assert.AreEqual(string.Format(Resources.Audio_duplicate_verify_result_format, 3, 1, 1, 1), fixture.Dialogs.Messages[0].MessageBoxText);
        Assert.IsTrue(fixture.Inspector.Calls.All(call => call.ThreadId != fixture.UiThreadId), "検査はUIスレッド以外です。");
        Assert.AreEqual(1, fixture.PredicateCaptures, "判定の捕捉は確認の開始時に一回だけです。");
        Assert.IsTrue(fixture.Inspector.Calls.All(call => ReferenceEquals(call.Request.IsOtherTargetFolder, fixture.Predicate)));
        CollectionAssert.AreEqual(new[] { a.Folder, b.Folder, c.Folder }, fixture.Inspector.Calls.Select(call => call.Request.Folder).ToArray(), "逐次、表示順");
        Assert.IsTrue(writeThreads.Count > 0 && writeThreads.All(id => id == fixture.UiThreadId), "書込みはUIスレッドです。");
        Assert.IsTrue(admissionHeldAtWrite.Count > 0 && admissionHeldAtWrite.All(held => held), "結果の書込みの時点では受付を保持しています。");
        Assert.IsTrue(Enumerable.Range(0, 3).All(index => !fixture.State(index).IsSelected), "確認を終えたフォルダのSELECTは段階によらずオフ");
        Assert.IsFalse(fixture.State(2).CanSelect);
        Assert.AreEqual(0, fixture.Ui(() => fixture.Owner.CaptureSelectedFolders().Count));
        Assert.IsTrue(fixture.Admission.CanEnter, "終端後は次の確認を受け付けます。");
    }

    /// <summary>
    /// 保証：共通の受付を取得できないときは、Busyの案内を一件だけ出し、検査・進捗・状態の変更をしない。受付が閉じているときは案内も出さず未開始で返す。
    /// 受付を解放すれば、次の確認を受け付ける。検出する誤り：競合時に検査や進捗を始める、Busy案内の重複・欠落、閉鎖をBusyとして案内する、状態の変更。
    /// </summary>
    [TestMethod]
    public async Task Verify_WhenAdmissionIsBusyOrClosed_ChangesNothing()
    {
        var inspector = new ScriptedInspector();
        AudioDuplicateFolder a = Folder(@"C:\S\a");
        using var fixture = new Fixture(inspector, a);
        fixture.Ui(() => fixture.Owner.SelectVisible());
        Assert.IsTrue(fixture.Admission.TryEnter(out IDisposable? lease));

        AudioDuplicateVerificationRunResult busy = await fixture.UiAsync(() => fixture.Workflow.VerifySelectedAsync());

        Assert.AreEqual(AudioDuplicateVerificationRunResult.Busy, busy);
        Assert.AreEqual(1, fixture.Dialogs.Messages.Count);
        Assert.AreEqual(Resources.Warn_LibraryOperationBusy, fixture.Dialogs.Messages[0].MessageBoxText);
        Assert.IsNull(fixture.Dialogs.ProgressRequest);
        Assert.AreEqual(0, inspector.Calls.Count);
        Assert.IsTrue(fixture.State(0).IsSelected && fixture.State(0).Inspection == null);

        lease!.Dispose();
        fixture.Admission.CloseAdmission();
        AudioDuplicateVerificationRunResult toolbar = await fixture.UiAsync(() => fixture.Workflow.VerifySelectedAsync());
        AudioDuplicateVerificationRunResult menu = await fixture.UiAsync(() => fixture.Workflow.VerifyFoldersOfRowsAsync([fixture.Owner.RowSet[0]]));
        Assert.AreEqual(AudioDuplicateVerificationRunResult.NotStarted, toolbar);
        Assert.AreEqual(AudioDuplicateVerificationRunResult.NotStarted, menu);
        Assert.AreEqual(1, fixture.Dialogs.Messages.Count, "閉鎖では案内しません。");
        Assert.AreEqual(0, inspector.Calls.Count);
    }

    /// <summary>
    /// 保証：対象が0件なら、受付の取得も進捗も案内もしない（ツールバー・右クリックメニューのどちらでも）。右クリックの確認はSELECTの状態によらず行のフォルダを確認する。
    /// 検出する誤り：0件でも受付を取得する・進捗を出す・空の結果の案内、右クリックのSELECTによる絞込み。
    /// </summary>
    [TestMethod]
    public async Task Verify_WithNoTargets_DoesNothing_AndMenuVerifiesRowsRegardlessOfSelect()
    {
        var inspector = new ScriptedInspector();
        using var fixture = new Fixture(inspector, Folder(@"C:\S\a"));

        Assert.AreEqual(AudioDuplicateVerificationRunResult.NoTargets, await fixture.UiAsync(() => fixture.Workflow.VerifySelectedAsync()));
        Assert.AreEqual(AudioDuplicateVerificationRunResult.NoTargets, await fixture.UiAsync(() => fixture.Workflow.VerifyFoldersOfRowsAsync([])));
        Assert.AreEqual(-1, fixture.Events.IndexOf("progress"));
        Assert.AreEqual(-1, fixture.Events.IndexOf("message"));
        Assert.AreEqual(0, inspector.Calls.Count);

        AudioDuplicateVerificationRunResult menu = await fixture.UiAsync(() => fixture.Workflow.VerifyFoldersOfRowsAsync([fixture.Owner.RowSet[0], fixture.Owner.RowSet[0]]));
        Assert.AreEqual(1, menu.Checked, "SELECTがオフでも、フォルダごとに一度確認します。");
        Assert.AreEqual(1, inspector.Calls.Count);
    }

    /// <summary>
    /// 保証：取消は、処理中のフォルダを最後まで処理して書き込んでから止まり、着手しなかったフォルダの確認結果とSELECTは変えない。案内は「未確認 {d}」を加えた書式になる。
    /// 検出する誤り：処理中のフォルダの結果の破棄、着手しなかったフォルダの状態の変更、取消後の検査の継続、取消の書式の欠落。
    /// </summary>
    [TestMethod]
    public async Task Verify_Cancel_FinishesTheCurrentFolderThenStops()
    {
        var inspector = new ScriptedInspector();
        AudioDuplicateFolder a = Folder(@"C:\S\a"), b = Folder(@"C:\S\b"), c = Folder(@"C:\S\c");
        using var fixture = new Fixture(inspector, a, b, c);
        fixture.Ui(() => fixture.Owner.SelectVisible());
        var gate = new Gate();
        inspector.Handler = (index, _) => index == 0
            ? Run(gate.Block, Inspection(AudioDuplicateInspectionStage.Safe, ["x"]))
            : Inspection(AudioDuplicateInspectionStage.Safe, ["x"]);

        Task<AudioDuplicateVerificationRunResult> run = fixture.UiAsync(() => fixture.Workflow.VerifySelectedAsync());
        try
        {
            // 取消ボタンを、最初のフォルダの検査中に押す。
            await WaitForSignalAsync(gate.Entered.Task, run, "検査の開始");
            fixture.Dialogs.RequestProgressCancellation();
            ConcurrencyProbe.WaitUntil(() => fixture.Dialogs.ProgressCancellationObserved, run, "進捗画面の取消が検知されること");
            // 取消の検知（進捗画面の引数への記録）は、走査への取消の受渡しより先に起こる。両者はUIスレッドの一続きの処理なので、
            // UIスレッドの代替を一巡させて、受渡しまで終えてから処理中のフォルダを解放する。解放が受渡しより先だと、走査が次のフォルダへ進める。
            await fixture.UiAsync(() => Task.FromResult(true));
        }
        finally { gate.Release.TrySetResult(true); }
        AudioDuplicateVerificationRunResult result = await run;

        Assert.AreEqual(DuplicateMergeVerificationRunStatus.Cancelled, result.Status);
        Assert.AreEqual(1, result.Checked, "処理中のフォルダは最後まで処理して書き込む");
        Assert.AreEqual(2, result.Unverified);
        Assert.AreEqual(1, inspector.Calls.Count, "取消後は次のフォルダへ進まない");
        Assert.IsNotNull(fixture.State(0).Inspection);
        Assert.IsFalse(fixture.State(0).IsSelected);
        Assert.IsTrue(fixture.State(1).IsSelected && fixture.State(1).Inspection == null && fixture.State(2).IsSelected, "着手しなかったフォルダは変えない");
        Assert.AreEqual(string.Format(Resources.Audio_duplicate_verify_result_cancelled_format, 1, 1, 0, 0, 2), fixture.Dialogs.Messages.Single().MessageBoxText);
    }

    /// <summary>
    /// 保証：確認中に状態表から消えたフォルダの結果は捨て、案内のどの件数にも数えず、全件が消えたときは案内を出さない。
    /// 検出する誤り：消えたフォルダの書込み・件数への算入、確認したフォルダが0件の案内。
    /// </summary>
    [TestMethod]
    public async Task Verify_FolderRemovedDuringCheck_IsNotCounted()
    {
        var inspector = new ScriptedInspector();
        AudioDuplicateFolder a = Folder(@"C:\S\a"), b = Folder(@"C:\S\b");
        using var fixture = new Fixture(inspector, a, b);
        fixture.Ui(() => fixture.Owner.SelectVisible());
        var gate = new Gate();
        inspector.Handler = (index, _) => index == 0 ? Run(gate.Block, Inspection(AudioDuplicateInspectionStage.Safe, ["x"])) : Inspection(AudioDuplicateInspectionStage.Safe, ["x"]);
        Task firstReport = FirstProgressReport(fixture.Dialogs);

        Task<AudioDuplicateVerificationRunResult> run = fixture.UiAsync(() => fixture.Workflow.VerifySelectedAsync());
        try
        {
            await WaitForSignalAsync(gate.Entered.Task, run, "検査の開始");
            await WaitForSignalAsync(firstReport, run, "進捗の最初の報告");
            fixture.Ui(() => Assert.IsTrue(fixture.Owner.Adopt(AudioDuplicateCandidateListOwner.Prepare(
                fixture.Library, [b], new MemorySource(), CancellationToken.None))));
        }
        finally { gate.Release.TrySetResult(true); }
        AudioDuplicateVerificationRunResult result = await run;

        Assert.AreEqual(1, result.Checked, "消えたフォルダ（a）は数えない");
        Assert.AreEqual(0, result.Unverified);
        Assert.AreEqual(string.Format(Resources.Audio_duplicate_verify_result_format, 1, 1, 0, 0), fixture.Dialogs.Messages.Single().MessageBoxText);
    }

    /// <summary>
    /// 保証：検査器の予期しない例外は、完了済みのフォルダの結果を書き込み、受付を解放した後に呼出元へ伝える（結果の案内は出さない）。
    /// 検出する誤り：例外での受付の保持、完了済みの結果の喪失、例外の握りつぶし。
    /// </summary>
    [TestMethod]
    public async Task Verify_UnexpectedInspectorFailure_WritesCompletedFoldersAndPropagatesAfterRelease()
    {
        var inspector = new ScriptedInspector();
        AudioDuplicateFolder a = Folder(@"C:\S\a"), b = Folder(@"C:\S\b");
        using var fixture = new Fixture(inspector, a, b);
        fixture.Ui(() => fixture.Owner.SelectVisible());
        inspector.Handler = (index, _) => index == 0 ? Inspection(AudioDuplicateInspectionStage.Safe, ["x"]) : throw new InvalidOperationException("boom");

        InvalidOperationException failure = await Assert.ThrowsExactlyAsync<InvalidOperationException>(
            () => fixture.UiAsync(() => fixture.Workflow.VerifySelectedAsync()));

        Assert.AreEqual("boom", failure.Message);
        Assert.IsNotNull(fixture.State(0).Inspection, "完了済みのフォルダの結果は書き込む");
        Assert.IsNull(fixture.State(1).Inspection);
        Assert.IsTrue(fixture.State(1).IsSelected, "確認を終えなかったフォルダのSELECTは変えない");
        Assert.IsTrue(fixture.Admission.CanEnter, "受付は解放済み");
        Assert.AreEqual(0, fixture.Dialogs.Messages.Count);
    }
}
