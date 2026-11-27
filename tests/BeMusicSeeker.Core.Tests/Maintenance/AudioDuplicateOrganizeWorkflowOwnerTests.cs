using BeMusicSeeker.Models;
using BeMusicSeeker.Models.BmsLibraryInternal;
using BeMusicSeeker.Properties;
using BeMusicSeeker.TestSupport.Components;
using BeMusicSeeker.ViewModels;
using BeMusicSeeker.Views.Dialogs;
using static BeMusicSeeker.TestSupport.Components.AudioDuplicateCandidateTestSupport;
using static BeMusicSeeker.TestSupport.Components.SelectedChartAudioConversionWorkflowOwnerTestSupport;
using MessageBoxResult = BeMusicSeeker.Models.UiDialogDefaultResult;

namespace BeMusicSeeker.Tests;

/// <summary>
/// 整理候補一覧の「選択フォルダを整理」の手順（対象0件、未確認の案内、受付、確認ダイアログ、再生停止とモデルの呼出しの順、終端ごとの案内と報告、SELECTの消費）を、
/// 本番の受付（<see cref="ChartFileOperationSynchronizer"/>）・実際の管理主体・記録用の整理の窓口で確認します。
/// 状態表の読み書きを行う「UIスレッド」は専用の単一スレッドの同期の文脈で模します。
/// </summary>
[TestClass]
public sealed class AudioDuplicateOrganizeWorkflowOwnerTests
{
    private readonly ApplicationDatabaseScope databaseScope = new();

    /// <summary>整理の要求・呼出しの文脈を記録し、結果を指定できる整理の窓口の代替です。</summary>
    private sealed class RecordingStore(EventLog events) : IAudioDuplicateOrganizeStore
    {
        internal Func<AudioDuplicateOrganizeRequest, LibraryOperationNotices, AudioDuplicateOrganizeResult>? Handler { get; set; }

        internal List<AudioDuplicateOrganizeRequest> Requests { get; } = [];

        internal int ThreadId { get; private set; }

        internal SynchronizationContext? Context { get; private set; }

        /// <summary>モデルの呼出しの時点で、本番の受付が取得できる（解放済み）かを返す観測です。受付をモデルの終端まで保持することの検査に使います。</summary>
        internal Func<bool>? AdmissionFree { get; set; }

        /// <summary>モデルの呼出しの時点で受付が解放済みだったかです。</summary>
        internal bool? AdmissionFreeInside { get; private set; }

        /// <summary>モデルへ渡された取消の合図です。</summary>
        internal CancellationToken Token { get; private set; }

        /// <summary>モデルの呼出しの時点で、取消の合図が取消済みだったかです。</summary>
        internal bool? CanceledAtCall { get; private set; }

        public AudioDuplicateOrganizeResult Organize(
            BMSLibrary library, AudioDuplicateOrganizeRequest request, long operationId, LibraryOperationNotices notices,
            Action<AudioDuplicateOrganizeProgress> progress, CancellationToken cancellationToken, LibraryFileMutationCapability capability)
        {
            events.Add("store");
            Requests.Add(request);
            ThreadId = Environment.CurrentManagedThreadId;
            Context = SynchronizationContext.Current;
            AdmissionFreeInside = AdmissionFree?.Invoke();
            Token = cancellationToken;
            CanceledAtCall = cancellationToken.IsCancellationRequested;
            progress?.Invoke(new AudioDuplicateOrganizeProgress(1, request.Folders.Count, request.Folders[0].Folder));
            return Handler?.Invoke(request, notices)
                ?? new AudioDuplicateOrganizeResult(request.Folders.Select(folder => new AudioDuplicateOrganizeFolderResult(
                    folder.Folder, AudioDuplicateOrganizeStatus.Completed, DuplicateBatchOrganizeSkipReason.None, null, 1, [])), canceled: false, admitted: true);
        }
    }

    private sealed class RecordingPlayback(EventLog events) : IChartMutationPlaybackPort
    {
        internal Exception? Failure { get; set; }

        internal int ThreadId { get; private set; }

        /// <summary>再生の停止の途中（停止の完了の前）に行う処理です。停止の間に取消ボタンが押された状況を作ります。</summary>
        internal Action? DuringStop { get; set; }

        public Task StopPlaybackForMutationAsync()
        {
            events.Add("stop");
            ThreadId = Environment.CurrentManagedThreadId;
            DuringStop?.Invoke();
            return Failure == null ? Task.CompletedTask : Task.FromException(Failure);
        }
    }

    private sealed class Fixture : IDisposable
    {
        private readonly SingleThreadSynchronizationContext ui = new();

        internal Fixture(TestBmsLibrary library, params AudioDuplicateFolder[] folders)
        {
            Library = library;
            Folders = folders;
            Dialogs = new RecordingDialogService(Events);
            Store = new RecordingStore(Events) { AdmissionFree = () => Admission.CanEnter };
            Playback = new RecordingPlayback(Events);
            Owner = new AudioDuplicateCandidateListOwner(_ => false, _ => { });
            Workflow = new AudioDuplicateOrganizeWorkflowOwner(Owner, () => Library, Admission, new ChartMutationActivityOwner(), Playback, Dialogs, Store);
            UiThreadId = Ui(() => Environment.CurrentManagedThreadId);
            Ui(() => Adopt(Owner, library, folders));
        }

        internal TestBmsLibrary Library { get; }

        internal AudioDuplicateFolder[] Folders { get; }

        internal EventLog Events { get; } = new();

        internal RecordingDialogService Dialogs { get; }

        internal RecordingStore Store { get; }

        internal RecordingPlayback Playback { get; }

        internal ChartFileOperationSynchronizer Admission { get; } = new();

        internal AudioDuplicateCandidateListOwner Owner { get; }

        internal AudioDuplicateOrganizeWorkflowOwner Workflow { get; }

        internal int UiThreadId { get; }

        internal T Ui<T>(Func<T> work) => ui.RunAsync(() => Task.FromResult(work())).GetAwaiter().GetResult();

        internal void Ui(Action work) => ui.RunAsync(work).GetAwaiter().GetResult();

        internal Task<AudioDuplicateOrganizeRunResult> Organize() => ui.RunAsync(() => Workflow.OrganizeSelectedAsync());

        internal AudioDuplicateCandidateState State(int index) => AudioDuplicateCandidateTestSupport.State(Owner, Folders[index]);

        /// <summary>確認結果を書き込み、続けてSELECTをオンにします（確認はSELECTを消費するため）。</summary>
        internal void VerifyAndSelect(int index, AudioDuplicateFolderInspection inspection)
        {
            Ui(() =>
            {
                AudioDuplicateCandidateTestSupport.Verify(Owner, Folders[index], inspection);
                Owner.SetSelectOfRows(Owner.RowSet.Where(row => row.Path == Folders[index].Folder), selected: true);
            });
        }

        public void Dispose() => ui.Dispose();
    }

    private void Run(Func<Fixture, Task> test, params AudioDuplicateFolder[] folders)
        => BmsLibraryDuplicateServiceTestSupport.WithTemporarySongDb(databaseScope, songDbPath =>
        {
            var library = new TestBmsLibrary(databaseScope, songDbPath) { BmsCharts = [], BmsonCharts = [] };
            try
            {
                using var fixture = new Fixture(library, folders);
                test(fixture).GetAwaiter().GetResult();
            }
            finally { library.RequestShutdown("audio-duplicate-organize-workflow"); }
        });

    private static AudioDuplicateOrganizeResult Result(Fixture fixture, bool canceled, bool admitted, params (int Index, AudioDuplicateOrganizeStatus Status, int Deleted)[] items)
        => new(
            items.Select(item => new AudioDuplicateOrganizeFolderResult(
                fixture.Folders[item.Index].Folder, item.Status, item.Status == AudioDuplicateOrganizeStatus.Skipped ? DuplicateBatchOrganizeSkipReason.FolderMissing : DuplicateBatchOrganizeSkipReason.None,
                null, item.Deleted, item.Status == AudioDuplicateOrganizeStatus.PartiallyCompleted ? ["x"] : [])),
            canceled, admitted);

    /// <summary>
    /// 保証：対象が0件なら、受付・確認・案内・再生停止・進捗・モデルの呼出しのどれも行わない。対象に未確認のフォルダが一つでもあれば、他者が受付を保持していても同じく、
    /// 受付の取得より前に案内を一件だけ出し、確認・再生停止・モデルの呼出しを行わず、SELECTと確認結果を変えない。
    /// 検出する誤り：0件での受付の取得、未確認の整理の実行、未確認の案内の欠落・重複、Busy案内との取り違え、状態の変更。
    /// </summary>
    [TestMethod]
    public void Organize_WithNoTargets_OrUnverifiedTargets_DoesNotStartAnything()
    {
        Run(async fixture =>
        {
            Assert.AreEqual(AudioDuplicateOrganizeRunResult.NoTargets, await fixture.Organize());
            Assert.AreEqual(0, fixture.Dialogs.Messages.Count + fixture.Dialogs.ConfirmCalls);

            fixture.Ui(() => fixture.Owner.SelectVisible());
            Assert.IsTrue(fixture.Admission.TryEnter(out IDisposable? lease), "他者が受付を保持している状態でも、未確認の案内が先です。");
            AudioDuplicateOrganizeRunResult result = await fixture.Organize();
            lease!.Dispose();

            Assert.AreEqual(AudioDuplicateOrganizeRunResult.UnverifiedTargets, result);
            Assert.AreEqual(Resources.Audio_duplicate_organize_unverified_notice, fixture.Dialogs.Messages.Single().MessageBoxText);
            Assert.AreEqual(0, fixture.Dialogs.ConfirmCalls);
            Assert.AreEqual(-1, fixture.Events.IndexOf("stop"));
            Assert.AreEqual(-1, fixture.Events.IndexOf("store"));
            Assert.IsTrue(fixture.State(0).IsSelected && fixture.State(0).Inspection == null);
        }, Folder(@"C:\S\a"));
    }

    /// <summary>
    /// 保証：受付を取得できないときはBusyの案内を一件だけ出して未開始とし、状態を変えない。受付が閉じているときは案内せずに未開始とする。いずれも確認・再生停止・モデルの呼出しを行わない。
    /// 検出する誤り：競合時の確認や実行、Busy案内の重複・欠落、閉鎖をBusyとして案内する。
    /// </summary>
    [TestMethod]
    public void Organize_WhenAdmissionIsBusyOrClosed_DoesNotStart()
    {
        Run(async fixture =>
        {
            fixture.VerifyAndSelect(0, Inspection(AudioDuplicateInspectionStage.Safe, ["x"]));
            Assert.IsTrue(fixture.Admission.TryEnter(out IDisposable? lease));

            Assert.AreEqual(AudioDuplicateOrganizeRunResult.Busy, await fixture.Organize());
            Assert.AreEqual(Resources.Warn_LibraryOperationBusy, fixture.Dialogs.Messages.Single().MessageBoxText);
            lease!.Dispose();
            fixture.Admission.CloseAdmission();
            Assert.AreEqual(AudioDuplicateOrganizeRunResult.NotStarted, await fixture.Organize());

            Assert.AreEqual(1, fixture.Dialogs.Messages.Count, "閉鎖では案内しません。");
            Assert.AreEqual(0, fixture.Dialogs.ConfirmCalls);
            Assert.AreEqual(-1, fixture.Events.IndexOf("store"));
            Assert.IsTrue(fixture.State(0).IsSelected);
        }, Folder(@"C:\S\a"));
    }

    /// <summary>
    /// 保証：承認の前の確認は必ず一回表示し（OKとキャンセル、既定はキャンセル）、通常の本文はフォルダ数と衝突名の合計の行と残す形式の行、
    /// 要注意があるときだけ警告のアイコン・警告用の本文（件数の行と、同等でない組も一方だけが残る行）・続行のチェックを持つ。「WAVを残す」では形式の文が入れ替わる。
    /// キャンセル・閉じる・表示の失敗では、再生停止・進捗・モデルの呼出しへ進まず、受付を解放して状態を変えず、表示の失敗は一回だけ報告する。
    /// 検出する誤り：確認の省略、要注意のないときの警告、続行のチェックの欠落、形式の文の取り違え、キャンセル後の実行、受付の解放漏れ。
    /// </summary>
    [TestMethod]
    public void Organize_Confirmation_ShowsTheContractedTextAndDeclineChangesNothing()
    {
        Run(async fixture =>
        {
            fixture.VerifyAndSelect(0, Inspection(AudioDuplicateInspectionStage.Safe, ["x", "y"]));
            fixture.VerifyAndSelect(1, Inspection(AudioDuplicateInspectionStage.Caution, ["z"], []));
            fixture.Dialogs.ConfirmResult = UiDialogResult.FromMessageBoxResult(System.Windows.MessageBoxResult.Cancel);

            Assert.AreEqual(AudioDuplicateOrganizeRunResult.Declined, await fixture.Organize());

            UiConfirmationRequest request = fixture.Dialogs.ConfirmRequest!;
            Assert.AreEqual(
                string.Join(Environment.NewLine, string.Format(Resources.Audio_duplicate_organize_confirm_pairs_format, 2, 3), Resources.Audio_duplicate_organize_confirm_keep_ogg),
                request.MessageBoxText);
            Assert.AreEqual(
                string.Join(Environment.NewLine, string.Format(Resources.Audio_duplicate_organize_confirm_caution_format, 1), string.Format(Resources.Audio_duplicate_organize_confirm_non_equivalent_format, 1)),
                request.WarningMessageBoxText);
            Assert.AreEqual(System.Windows.MessageBoxImage.Warning, request.Icon);
            Assert.AreEqual(System.Windows.MessageBoxButton.OKCancel, request.Button);
            Assert.AreEqual(System.Windows.MessageBoxResult.Cancel, request.DefaultResult);
            Assert.AreEqual(Resources.Audio_duplicate_organize_acknowledgment, request.AcknowledgmentText);
            Assert.AreEqual(-1, fixture.Events.IndexOf("stop"));
            Assert.AreEqual(-1, fixture.Events.IndexOf("progress"));
            Assert.AreEqual(-1, fixture.Events.IndexOf("store"));
            Assert.IsTrue(fixture.State(0).IsSelected && fixture.State(1).IsSelected, "キャンセルで状態を変えない");
            Assert.IsTrue(fixture.Admission.CanEnter, "受付は解放済み");

            // 要注意のないとき・「WAVを残す」。
            fixture.Ui(() => fixture.Owner.ClearVisible());
            fixture.VerifyAndSelect(0, Inspection(AudioDuplicateInspectionStage.Safe, ["x", "y"]));
            fixture.Ui(() => fixture.Owner.CollisionCleanupMode = AudioDuplicateCollisionCleanupMode.KeepWav);
            fixture.Dialogs.ConfirmResult = UiDialogResult.ClosedByUser(System.Windows.MessageBoxResult.Cancel);
            Assert.AreEqual(AudioDuplicateOrganizeRunResult.Declined, await fixture.Organize());
            request = fixture.Dialogs.ConfirmRequest!;
            StringAssert.EndsWith(request.MessageBoxText, Resources.Audio_duplicate_organize_confirm_keep_wav);
            Assert.IsNull(request.WarningMessageBoxText);
            Assert.AreEqual(System.Windows.MessageBoxImage.Question, request.Icon);
            Assert.IsNull(request.AcknowledgmentText, "続行のチェックは要注意があるときだけ");

            // 表示の失敗は一回だけ報告する。
            fixture.Dialogs.ConfirmResult = UiDialogResult.NotShown(UiDialogStatus.Failed);
            AudioDuplicateOrganizeRunResult failed = await fixture.Organize();
            Assert.AreEqual(AudioDuplicateOrganizeRunStatus.Failed, failed.Status);
            Assert.AreEqual(1, fixture.Dialogs.Messages.Count, "報告は一回だけ");
            Assert.AreEqual(-1, fixture.Events.IndexOf("store"));
            Assert.IsTrue(fixture.State(0).IsSelected);
            Assert.IsTrue(fixture.Admission.CanEnter);
        }, Folder(@"C:\S\a", "x", "y"), Folder(@"C:\S\b", "z"));
    }

    /// <summary>
    /// 保証：「削除」が完全に削除のとき、要求の削除方法は完全に削除になり、確認文の残す形式の行は「完全に削除します」の文言（OGGを残す・WAVを残す）になる。
    /// 警告用の本文は先頭に「元に戻せない」旨の行を置き、要注意があるときはその後に従来の二行が続く。他の警告がなくても警告用の本文が出て、アイコンは常に警告。
    /// OKとキャンセルで既定はキャンセル。続行のチェックは要注意があるときだけで、完全に削除だけでは求めない。
    /// 確認の表示中に一覧の値をごみ箱へ移動へ変えても、要求と結果の案内は捕捉時点の完全に削除のまま。結果の案内の音源の行は「完全に削除しました」の文言。
    /// 検出する誤り：削除方法を要求へ渡さない、ごみ箱用の文言との取り違え、警告の先頭行の欠落・位置の誤り、アイコンの取り違え、完全に削除だけでチェックを求める、
    /// 確認の後に一覧の値を読み直す。
    /// </summary>
    [TestMethod]
    [DataRow(AudioDuplicateCollisionCleanupMode.KeepOgg, false, DisplayName = "OGGを残す・他の警告なし")]
    [DataRow(AudioDuplicateCollisionCleanupMode.KeepWav, false, DisplayName = "WAVを残す・他の警告なし")]
    [DataRow(AudioDuplicateCollisionCleanupMode.KeepOgg, true, DisplayName = "OGGを残す・要注意あり")]
    [DataRow(AudioDuplicateCollisionCleanupMode.KeepWav, true, DisplayName = "WAVを残す・要注意あり")]
    public void Organize_PermanentDeleteUsesPermanentWordingWarningAndTheCapturedRequest(AudioDuplicateCollisionCleanupMode mode, bool caution)
    {
        Run(async fixture =>
        {
            fixture.VerifyAndSelect(0, Inspection(AudioDuplicateInspectionStage.Safe, ["x", "y"]));
            fixture.VerifyAndSelect(1, caution ? Inspection(AudioDuplicateInspectionStage.Caution, ["z"], []) : Inspection(AudioDuplicateInspectionStage.Safe, ["z"]));
            fixture.Ui(() =>
            {
                fixture.Owner.CollisionCleanupMode = mode;
                fixture.Owner.DeleteMethod = DuplicateOrganizeDeleteMethod.DeletePermanently;
            });
            // 確認の表示中に、一覧の値をごみ箱へ移動へ変えます（その整理には影響しません）。
            fixture.Dialogs.ConfirmHandler = () =>
            {
                fixture.Owner.DeleteMethod = DuplicateOrganizeDeleteMethod.MoveToRecycleBin;
                return Task.FromResult(UiDialogResult.FromMessageBoxResult(System.Windows.MessageBoxResult.OK));
            };

            AudioDuplicateOrganizeRunResult result = await fixture.Organize();

            Assert.AreEqual(AudioDuplicateOrganizeRunStatus.Completed, result.Status);
            UiConfirmationRequest request = fixture.Dialogs.ConfirmRequest!;
            Assert.AreEqual(
                string.Join(
                    Environment.NewLine,
                    string.Format(Resources.Audio_duplicate_organize_confirm_pairs_format, 2, 3),
                    mode == AudioDuplicateCollisionCleanupMode.KeepOgg
                        ? Resources.Audio_duplicate_organize_confirm_keep_ogg_permanent
                        : Resources.Audio_duplicate_organize_confirm_keep_wav_permanent),
                request.MessageBoxText);
            Assert.AreNotEqual(Resources.Audio_duplicate_organize_confirm_keep_ogg, Resources.Audio_duplicate_organize_confirm_keep_ogg_permanent);
            string[] expectedWarning = caution
                ? [Resources.Duplicate_batch_organize_confirm_permanent_warning,
                    string.Format(Resources.Audio_duplicate_organize_confirm_caution_format, 1),
                    string.Format(Resources.Audio_duplicate_organize_confirm_non_equivalent_format, 1)]
                : [Resources.Duplicate_batch_organize_confirm_permanent_warning];
            Assert.AreEqual(string.Join(Environment.NewLine, expectedWarning), request.WarningMessageBoxText);
            Assert.AreEqual(System.Windows.MessageBoxImage.Warning, request.Icon, "他の警告がなくても警告のアイコン");
            Assert.AreEqual(caution ? Resources.Audio_duplicate_organize_acknowledgment : null, request.AcknowledgmentText, "続行のチェックは要注意があるときだけ");
            Assert.AreEqual(System.Windows.MessageBoxButton.OKCancel, request.Button);
            Assert.AreEqual(System.Windows.MessageBoxResult.Cancel, request.DefaultResult);
            Assert.AreEqual(DuplicateOrganizeDeleteMethod.DeletePermanently, fixture.Store.Requests.Single().DeleteMethod, "確認の表示中の変更は影響しない");
            StringAssert.Contains(fixture.Dialogs.Messages.Single().MessageBoxText, string.Format(Resources.Audio_duplicate_organize_result_deleted_format, 2));
            Assert.AreEqual(-1, fixture.Dialogs.Messages.Single().MessageBoxText.IndexOf(
                string.Format(Resources.Audio_duplicate_organize_result_trashed_format, 2), StringComparison.Ordinal), "ごみ箱用の文言は使わない");
        }, Folder(@"C:\S\a", "x", "y"), Folder(@"C:\S\b", "z"));
    }

    /// <summary>
    /// 保証：承認後は、モーダルの進捗（取消ボタンつき）の内側で、再生の停止、モデルの呼出し（UIの文脈の外）の順に進み、要求は表示順のフォルダごとに捨てる形式（OGGを残すなら.wav）と
    /// 確認が確定した衝突名のすべて（同等でない名前を含む）を持つ。受付はモデルの終端まで保持し（モデルへは受付の権限を渡す）、結果の案内の前に解放する。
    /// 受理された結果では「未処理」以外のSELECTがオフになり、結果の案内は一回だけ（全区分の件数、音源の合計行、完了以外のフォルダの行）になる。
    /// 検出する誤り：停止とモデルの順の逆転、UIの文脈での呼出し、要求の形式・名前の誤り、受付の早すぎる解放、SELECTの消費漏れ、案内の重複。
    /// </summary>
    [TestMethod]
    public void Organize_Accepted_StopsPlaybackThenCallsTheModelOutsideTheUiContext_AndAnnouncesTheResultOnce()
    {
        Run(async fixture =>
        {
            Enumerable.Range(0, 3).ToList().ForEach(index => fixture.VerifyAndSelect(
                index, Inspection(index == 1 ? AudioDuplicateInspectionStage.Caution : AudioDuplicateInspectionStage.Safe, ["n" + index, "m" + index], index == 1 ? ["n1"] : null)));
            bool? admissionFreeAtResult = null;
            fixture.Dialogs.MessageObserver = _ => admissionFreeAtResult ??= fixture.Admission.CanEnter;
            fixture.Store.Handler = (_, _) => Result(
                fixture, canceled: true, admitted: true,
                (0, AudioDuplicateOrganizeStatus.Completed, 2), (1, AudioDuplicateOrganizeStatus.PartiallyCompleted, 1), (2, AudioDuplicateOrganizeStatus.Unprocessed, 0));

            AudioDuplicateOrganizeRunResult result = await fixture.Organize();

            Assert.AreEqual(AudioDuplicateOrganizeRunStatus.Completed, result.Status);
            Assert.IsTrue(fixture.Events.IndexOf("confirm") < fixture.Events.IndexOf("progress"));
            Assert.IsTrue(fixture.Events.IndexOf("progress") < fixture.Events.IndexOf("stop"));
            Assert.IsTrue(fixture.Events.IndexOf("stop") < fixture.Events.IndexOf("store"), "再生の停止の後にモデルを呼ぶ");
            Assert.IsTrue(fixture.Dialogs.ProgressRequest!.Settings.ShowCancelButton);
            Assert.IsTrue(fixture.Dialogs.ProgressReports.Contains((0, "[1/3] a")), "進捗は「[何番目/総数] フォルダ名」を示す");
            Assert.AreNotEqual(fixture.UiThreadId, fixture.Store.ThreadId, "モデルはUIスレッドの外で呼ぶ");
            Assert.IsNull(fixture.Store.Context, "UIの文脈の外");
            Assert.AreEqual(false, fixture.Store.AdmissionFreeInside, "モデルの終端まで受付を保持する（モデルの実行中は他の取得が失敗する）");
            AudioDuplicateOrganizeRequest request = fixture.Store.Requests.Single();
            CollectionAssert.AreEqual(new[] { @"C:\S\a", @"C:\S\b", @"C:\S\c" }, request.Folders.Select(folder => folder.Folder).ToArray(), "表示順");
            Assert.IsTrue(request.Folders.All(folder => folder.Cleanup.DiscardExtension == ".wav"), "OGGを残すなら.wavを捨てる");
            CollectionAssert.AreEqual(new[] { "n1", "m1" }, request.Folders[1].Cleanup.Names.ToArray(), "同等でない名前を含む、確認が確定した衝突名のすべて");
            Assert.AreEqual(true, admissionFreeAtResult, "結果の案内の前に受付を解放する");
            Assert.IsFalse(fixture.State(0).IsSelected || fixture.State(1).IsSelected, "未処理以外のSELECTはオフ");
            Assert.IsTrue(fixture.State(2).IsSelected, "未処理のSELECTは変えない");
            string message = fixture.Dialogs.Messages.Single().MessageBoxText;
            StringAssert.StartsWith(message, string.Format(Resources.Audio_duplicate_organize_result_format, 3, 1, 1, 0, 1));
            StringAssert.Contains(message, string.Format(Resources.Audio_duplicate_organize_result_trashed_format, 3));
            StringAssert.Contains(message, Resources.Duplicate_batch_organize_result_canceled);
            StringAssert.Contains(message, string.Format(Resources.Duplicate_batch_organize_result_group_reason_format, @"C:\S\b", Resources.Duplicate_batch_organize_status_partial, Resources.Duplicate_batch_organize_reason_delete_failure));
            StringAssert.Contains(message, string.Format(Resources.Duplicate_batch_organize_result_group_format, @"C:\S\c", Resources.Duplicate_batch_organize_status_unprocessed));
            Assert.AreEqual(-1, message.IndexOf(@"C:\S\a：", StringComparison.Ordinal), "完了のフォルダの行は出さない");
        }, Folder(@"C:\S\a", "n0", "m0"), Folder(@"C:\S\b", "n1", "m1"), Folder(@"C:\S\c", "n2", "m2"));
    }

    /// <summary>
    /// 保証：完了以外のフォルダの行は最大20件までで、超える分は「ほか{n}フォルダ…」に省略する（20件はそのまま、21件で一件省略）。音源の合計が0件なら合計の行を出さない。
    /// 検出する誤り：行数の上限の誤り、省略行の欠落、0件の合計行の表示。
    /// </summary>
    [TestMethod]
    public void ResultMessage_LimitsTheIncompleteFolderLinesToTwenty()
    {
        AudioDuplicateOrganizeResult Build(int count) => new(
            Enumerable.Range(0, count).Select(index => new AudioDuplicateOrganizeFolderResult(
                @"C:\S\f" + index, AudioDuplicateOrganizeStatus.Skipped, DuplicateBatchOrganizeSkipReason.LinkFound, null, 0, [])),
            canceled: false, admitted: true);

        string twenty = AudioDuplicateOrganizePresentation.CreateResultMessage(Build(20), DuplicateOrganizeDeleteMethod.MoveToRecycleBin, out bool allCompleted20);
        string twentyOne = AudioDuplicateOrganizePresentation.CreateResultMessage(Build(21), DuplicateOrganizeDeleteMethod.MoveToRecycleBin, out _);

        Assert.IsFalse(allCompleted20);
        Assert.AreEqual(20, Enumerable.Range(0, 20).Count(index => twenty.Contains(@"C:\S\f" + index + "：", StringComparison.Ordinal)));
        Assert.IsFalse(twenty.Contains(string.Format(Resources.Audio_duplicate_organize_result_more_format, 1), StringComparison.Ordinal));
        Assert.IsFalse(twentyOne.Contains(@"C:\S\f20：", StringComparison.Ordinal), "21件目は行を出さない");
        StringAssert.Contains(twentyOne, string.Format(Resources.Audio_duplicate_organize_result_more_format, 1));
        Assert.IsFalse(twenty.Contains(Resources.Audio_duplicate_organize_result_trashed_format.Split("{0}")[0], StringComparison.Ordinal), "音源が0件なら合計の行を出さない");
        StringAssert.Contains(twenty, Resources.Duplicate_batch_organize_reason_link);
    }

    /// <summary>
    /// 保証：再生の停止の失敗ではモデルを呼ばず、モデルの例外とともに、報告を一回だけ出して結果の案内を出さない（SELECTは変えない）。
    /// モデルが操作全体を受理しなかった場合は、既存の拒否の案内（モデルが積んだ案内）だけを一回出し、結果の案内を出さず、SELECTを変えない。
    /// 検出する誤り：停止の失敗後のモデルの呼出し、報告の重複・欠落、失敗経路での結果の案内、未受理でのSELECTの消費。
    /// </summary>
    [TestMethod]
    public void Organize_FailurePaths_ReportOnceWithoutResultNotice()
    {
        Run(async fixture =>
        {
            fixture.VerifyAndSelect(0, Inspection(AudioDuplicateInspectionStage.Safe, ["x"]));

            fixture.Playback.Failure = new InvalidOperationException("stop");
            AudioDuplicateOrganizeRunResult stopFailure = await fixture.Organize();
            Assert.AreEqual(AudioDuplicateOrganizeRunStatus.Failed, stopFailure.Status);
            Assert.AreEqual(-1, fixture.Events.IndexOf("store"), "停止の失敗ではモデルを呼ばない");
            Assert.AreEqual(1, fixture.Dialogs.Messages.Count, "報告は一回だけで、結果の案内はない");
            Assert.IsTrue(fixture.Admission.CanEnter);

            fixture.Playback.Failure = null;
            fixture.Store.Handler = (_, _) => throw new InvalidOperationException("model");
            AudioDuplicateOrganizeRunResult modelFailure = await fixture.Organize();
            Assert.AreEqual(AudioDuplicateOrganizeRunStatus.Failed, modelFailure.Status);
            Assert.AreEqual(2, fixture.Dialogs.Messages.Count, "モデルの例外の報告も一回だけ");
            Assert.IsTrue(fixture.State(0).IsSelected, "結果がない経路ではSELECTを変えない");

            var rejectionShown = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            fixture.Dialogs.MessageObserver = message => { if (message.MessageBoxText == "rejected") { rejectionShown.TrySetResult(); } };
            fixture.Store.Handler = (request, notices) =>
            {
                notices.Add(new BMSLibrary.OperationDialogMessage("rejected", "caption", UiDialogButton.OK, UiDialogIcon.Warning, MessageBoxResult.OK));
                return AudioDuplicateOrganizeResult.NotAdmitted(request);
            };
            AudioDuplicateOrganizeRunResult notAdmitted = await fixture.Organize();
            Assert.AreEqual(AudioDuplicateOrganizeRunStatus.NotAdmitted, notAdmitted.Status);
            await rejectionShown.Task;
            Assert.AreEqual(3, fixture.Dialogs.Messages.Count, "拒否の案内が一回");
            Assert.AreEqual("rejected", fixture.Dialogs.Messages[2].MessageBoxText);
            Assert.IsTrue(fixture.State(0).IsSelected, "未受理ではSELECTを変えない");
        }, Folder(@"C:\S\a"));
    }

    /// <summary>
    /// 保証：進捗画面の取消ボタンは、手順からモデルへ渡す取消の合図へ伝わり、モデルが取消として返した結果は、取消の行を含む一回の結果の案内になる。
    /// モデルの側で取消ボタンを押し、合図の到達を期限なしに待機ハンドルの通知で待つ。モデルは合図が届くまで戻らないため、
    /// 取消ボタンが合図へ届くことを判定に含む（届かなければ停止として、検証全体の制限時間で検出する）。
    /// 検出する誤り：取消ボタンが合図へ変換されない、取消された結果の案内の欠落。
    /// </summary>
    [TestMethod]
    public void Organize_CancelButtonReachesTheModelCancellationToken()
    {
        Run(async fixture =>
        {
            fixture.VerifyAndSelect(0, Inspection(AudioDuplicateInspectionStage.Safe, ["x"]));
            fixture.Store.Handler = (request, _) =>
            {
                fixture.Dialogs.RequestProgressCancellation();
                fixture.Store.Token.WaitHandle.WaitOne();
                return new AudioDuplicateOrganizeResult(
                    request.Folders.Select(folder => AudioDuplicateOrganizeFolderResult.CreateUnprocessed(folder.Folder)), canceled: true, admitted: true);
            };

            AudioDuplicateOrganizeRunResult result = await fixture.Organize();

            Assert.AreEqual(AudioDuplicateOrganizeRunStatus.Completed, result.Status);
            StringAssert.Contains(fixture.Dialogs.Messages.Single().MessageBoxText, Resources.Duplicate_batch_organize_result_canceled);
            Assert.IsTrue(fixture.State(0).IsSelected, "未処理のSELECTは変えない");
        }, Folder(@"C:\S\a"));
    }

    /// <summary>
    /// 保証：再生の停止の前（操作の開始前）または再生の停止の間に取消が要求された場合、手順は再生の停止の後に、取消済みの合図でモデルを呼ぶ。
    /// モデル（合図が取消済みなら全フォルダを未処理で返す代替）の結果により、全フォルダのSELECTと確認結果は変わらず、「取り消されました。」を含む結果の案内が一回だけ出る。
    /// 停止の間の取消は、再生の停止の代替の内側で取消ボタンを押して作り、手順の画面側の再確認（一定間隔）の時機に依存せずに判定する。
    /// 検出する誤り：停止の前後の取消をモデルへ渡さずに整理を始める（画面側の再確認を待つ間に最初のフォルダの処理が始まる）、取消でモデルを呼ばない、
    /// 取消の結果でのSELECTの消費、取消の行の欠落、案内の重複。
    /// </summary>
    [TestMethod]
    [DataRow(false, DisplayName = "操作の開始前に取消済み")]
    [DataRow(true, DisplayName = "再生の停止の間に取消")]
    public void Organize_CancelBeforeOrDuringPlaybackStop_CallsTheModelWithACanceledToken(bool cancelDuringStop)
    {
        Run(async fixture =>
        {
            fixture.VerifyAndSelect(0, Inspection(AudioDuplicateInspectionStage.Safe, ["x"]));
            fixture.VerifyAndSelect(1, Inspection(AudioDuplicateInspectionStage.Safe, ["y"]));
            AudioDuplicateFolderInspection?[] inspections = [fixture.State(0).Inspection, fixture.State(1).Inspection];
            if (cancelDuringStop) { fixture.Playback.DuringStop = fixture.Dialogs.RequestProgressCancellation; }
            else { fixture.Dialogs.CancelProgressBeforeOperation = true; }
            // モデルの代替は、合図が取消済みなら全フォルダを未処理で返し、そうでなければ全フォルダを完了にする。
            fixture.Store.Handler = (request, _) => fixture.Store.Token.IsCancellationRequested
                ? new AudioDuplicateOrganizeResult(
                    request.Folders.Select(folder => AudioDuplicateOrganizeFolderResult.CreateUnprocessed(folder.Folder)), canceled: true, admitted: true)
                : new AudioDuplicateOrganizeResult(
                    request.Folders.Select(folder => new AudioDuplicateOrganizeFolderResult(
                        folder.Folder, AudioDuplicateOrganizeStatus.Completed, DuplicateBatchOrganizeSkipReason.None, null, 1, [])), canceled: false, admitted: true);

            AudioDuplicateOrganizeRunResult result = await fixture.Organize();

            Assert.IsTrue(fixture.Events.IndexOf("stop") >= 0 && fixture.Events.IndexOf("stop") < fixture.Events.IndexOf("store"), "再生の停止の後にモデルを呼ぶ");
            Assert.AreEqual(1, fixture.Store.Requests.Count, "取消でもモデルを一回呼ぶ");
            Assert.AreEqual(true, fixture.Store.CanceledAtCall, "モデルへ渡った時点で合図は取消済み");
            Assert.AreEqual(AudioDuplicateOrganizeRunStatus.Completed, result.Status);
            Assert.IsTrue(result.Result!.Folders.All(folder => folder.Status == AudioDuplicateOrganizeStatus.Unprocessed), "全フォルダが未処理");
            Assert.IsTrue(fixture.State(0).IsSelected && fixture.State(1).IsSelected, "未処理のSELECTは変えない");
            Assert.AreSame(inspections[0], fixture.State(0).Inspection, "確認結果は変えない");
            Assert.AreSame(inspections[1], fixture.State(1).Inspection, "確認結果は変えない");
            string message = fixture.Dialogs.Messages.Single().MessageBoxText;
            StringAssert.Contains(message, Resources.Duplicate_batch_organize_result_canceled);
            StringAssert.StartsWith(message, string.Format(Resources.Audio_duplicate_organize_result_format, 2, 0, 0, 0, 2));
            Assert.IsTrue(fixture.Admission.CanEnter, "受付は解放済み");
        }, Folder(@"C:\S\a", "x"), Folder(@"C:\S\b", "y"));
    }
}
