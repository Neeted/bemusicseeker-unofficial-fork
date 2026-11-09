using System.Windows;
using BeMusicSeeker.Models;
using BeMusicSeeker.Models.BmsLibraryInternal;
using BeMusicSeeker.TestSupport.Components;
using BeMusicSeeker.TestSupport.Files;
using BeMusicSeeker.Views.Dialogs;

namespace BeMusicSeeker.Tests;

/// <summary>
/// モデルが持つ窓口の型と、構成が注入する View 側の実装の境界を検証します。
/// 結果の変換は注入した記録用の <see cref="IUiDialogService"/> で確かめ、実ウィンドウを使いません。
/// </summary>
[TestClass]
public sealed class BmsLibraryDialogServiceTests
{
    /// <summary>確認は確認経路だけへ渡し、受理・拒否・取消・閉鎖を元の既定結果のまま返します。</summary>
    [TestMethod]
    [DataRow((int)UiDialogButton.YesNo, MessageBoxResult.Yes, (int)UiDialogDefaultResult.Yes)]
    [DataRow((int)UiDialogButton.YesNo, MessageBoxResult.No, (int)UiDialogDefaultResult.No)]
    [DataRow((int)UiDialogButton.OKCancel, MessageBoxResult.Cancel, (int)UiDialogDefaultResult.Cancel)]
    [DataRow((int)UiDialogButton.YesNoCancel, MessageBoxResult.None, (int)UiDialogDefaultResult.None)]
    public void Confirm_RoutesToConfirmationAndReturnsUserChoice(int buttonValue, MessageBoxResult shown, int expectedValue)
    {
        // 列挙型は内部型のため、テストの引数は数値で受けます。
        var button = (UiDialogButton)buttonValue;
        var expected = (UiDialogDefaultResult)expectedValue;
        var inner = new RecordingUiDialogs(UiDialogResult.FromMessageBoxResult(shown));
        var service = new BmsLibraryDialogService(inner, TestUiScheduler.CreateDefault());

        UiDialogDefaultResult actual = service.Confirm("本文", "題名", button, UiDialogIcon.Question, UiDialogDefaultResult.No);

        Assert.AreEqual(expected, actual);
        Assert.AreEqual(0, inner.MessageRequests.Count);
        Assert.AreEqual(1, inner.ConfirmRequests.Count);
        UiMessageRequest request = inner.ConfirmRequests.Single();
        Assert.AreEqual("本文", request.MessageBoxText);
        Assert.AreEqual("題名", request.Caption);
        Assert.AreEqual(Enum.Parse<MessageBoxButton>(button.ToString()), request.Button);
        Assert.AreEqual(MessageBoxImage.Question, request.Icon);
        Assert.AreEqual(MessageBoxResult.No, request.DefaultResult);
    }

    /// <summary>
    /// OKだけの通知は確認ではないため、同期に表示せず例外にする。窓口への要求を一件も出さない。
    /// OKだけの通知を同期のモーダルとして表示する分岐が残る誤りを検出する（通知は操作の収集先か投函で届ける）。
    /// </summary>
    [TestMethod]
    public void Confirm_RejectsOkOnlyNoticeWithoutShowing()
    {
        var inner = new RecordingUiDialogs(UiDialogResult.FromMessageBoxResult(MessageBoxResult.OK));
        var service = new BmsLibraryDialogService(inner, TestUiScheduler.CreateDefault());

        Assert.ThrowsExactly<ArgumentException>(() => service.Confirm("本文", "題名", UiDialogButton.OK, UiDialogIcon.Information));

        Assert.AreEqual(0, inner.MessageRequests.Count);
        Assert.AreEqual(0, inner.ConfirmRequests.Count);
    }

    /// <summary>表示中の失敗は元例外を保持した失敗にし、表示されなかった結果は拒否や取消へ丸めず状態名つきの失敗にします。</summary>
    [TestMethod]
    public void Confirm_ConvertsFailureAndNotShownToExceptions()
    {
        var cause = new InvalidOperationException("表示失敗");
        var failed = new BmsLibraryDialogService(new RecordingUiDialogs(UiDialogResult.Failed(cause)), TestUiScheduler.CreateDefault());
        InvalidOperationException failure = Assert.ThrowsExactly<InvalidOperationException>(
            () => failed.Confirm("本文", "題名", UiDialogButton.OKCancel, UiDialogIcon.None));
        Assert.AreSame(cause, failure.InnerException);

        var notShown = new BmsLibraryDialogService(new RecordingUiDialogs(UiDialogResult.NotShown(UiDialogStatus.OwnerUnavailable)), TestUiScheduler.CreateDefault());
        InvalidOperationException hidden = Assert.ThrowsExactly<InvalidOperationException>(
            () => notShown.Confirm("本文", "題名", UiDialogButton.YesNo, UiDialogIcon.None));
        StringAssert.Contains(hidden.Message, nameof(UiDialogStatus.OwnerUnavailable));
        Assert.IsNull(hidden.InnerException);
    }

    /// <summary>
    /// 投函は予約だけで戻り、予約先が実行するまで表示を始めない。実行後は見出し・重要度ごとに一件へまとめ、
    /// 最初に現れた順に、先行の表示の完了を待って次の組を表示する。空の投函は何も予約しない。
    /// 投函の呼出し元のスレッドで表示を同期に始める誤りと、組を先行の完了前に重ねて表示する誤りを検出する。
    /// </summary>
    [TestMethod]
    public async Task Post_ReturnsAfterReservationAndShowsGroupsInOrderWaitingForPrecedingGroup()
    {
        var gate = new TaskCompletionSource<UiDialogResult>(TaskCreationOptions.RunContinuationsAsynchronously);
        var secondShown = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var inner = new RecordingUiDialogs(UiDialogResult.FromMessageBoxResult(MessageBoxResult.OK))
        {
            Handler = request =>
            {
                if (request.Caption == "capB") { secondShown.TrySetResult(); }
                return request.Caption == "capA" ? gate.Task : Task.FromResult(UiDialogResult.FromMessageBoxResult(MessageBoxResult.OK));
            }
        };
        var scheduler = new BmsLibraryLr2SongDbSyncTestSupport.QueuedUiScheduler();
        var service = new BmsLibraryDialogService(inner, scheduler);
        BMSLibrary.OperationDialogMessage a1 = Notice("a-1", "capA", UiDialogIcon.Information);
        BMSLibrary.OperationDialogMessage b1 = Notice("b-1", "capB", UiDialogIcon.Exclamation);
        BMSLibrary.OperationDialogMessage a2 = Notice("a-2", "capA", UiDialogIcon.Information);

        service.Post([a1, b1, a2], () => false);

        Assert.AreEqual(0, inner.MessageRequests.Count, "投函は呼出し元のスレッドで表示を始めない。");
        Assert.IsTrue(scheduler.PendingCount >= 1, "表示の開始は予約先へ渡す。");
        scheduler.Drain();
        Assert.AreEqual(1, inner.MessageRequests.Count, "最初の組だけを表示し、先行の完了を待つ。");
        Assert.AreEqual("capA", inner.MessageRequests[0].Caption);
        Assert.AreEqual(Enum.Parse<MessageBoxImage>(UiDialogIcon.Information.ToString()), inner.MessageRequests[0].Icon);
        gate.SetResult(UiDialogResult.FromMessageBoxResult(MessageBoxResult.OK));
        await secondShown.Task;
        Assert.AreEqual(2, inner.MessageRequests.Count);
        UiMessageRequest second = inner.MessageRequests[1];
        Assert.AreEqual(b1.MessageBoxText, second.MessageBoxText, "一件だけの組は元の通知のまま表示する。");
        Assert.AreEqual(b1.Caption, second.Caption);
        Assert.AreEqual(Enum.Parse<MessageBoxImage>(b1.Icon.ToString()), second.Icon);
        Assert.AreEqual(Enum.Parse<MessageBoxButton>(b1.Button.ToString()), second.Button);
        Assert.AreEqual(Enum.Parse<MessageBoxResult>(b1.DefaultResult.ToString()), second.DefaultResult);

        var emptyScheduler = new BmsLibraryLr2SongDbSyncTestSupport.QueuedUiScheduler();
        new BmsLibraryDialogService(new RecordingUiDialogs(UiDialogResult.FromMessageBoxResult(MessageBoxResult.OK)), emptyScheduler).Post([], () => false);
        Assert.AreEqual(0, emptyScheduler.ScheduleCount, "空の投函は何も予約しない。");
    }

    /// <summary>
    /// 窓口の失敗結果・失敗したTask・同期の例外・予約の拒否・予約中の例外は、投函した処理へ例外で伝えず、
    /// 表示の失敗では後続の組も表示し、そのあとの別の投函も通常どおり届ける。
    /// 通知の失敗が投函元の処理を壊す誤りと、一件の失敗で後続の通知を止める誤りを検出する。
    /// </summary>
    [TestMethod]
    [DataRow("failed-result")]
    [DataRow("faulted-task")]
    [DataRow("synchronous-throw")]
    [DataRow("rejected")]
    [DataRow("schedule-throws")]
    public void Post_DoesNotThrowOnDisplayOrReservationFailureAndKeepsLaterNotices(string shape)
    {
        var cause = new InvalidOperationException("表示失敗");
        bool failFirst = true;
        var inner = new RecordingUiDialogs(UiDialogResult.FromMessageBoxResult(MessageBoxResult.OK))
        {
            Handler = request =>
            {
                if (!failFirst || request.Caption != "capA") { return Task.FromResult(UiDialogResult.FromMessageBoxResult(MessageBoxResult.OK)); }
                failFirst = false;
                return shape switch
                {
                    "failed-result" => Task.FromResult(UiDialogResult.Failed(cause)),
                    "faulted-task" => Task.FromException<UiDialogResult>(cause),
                    "synchronous-throw" => throw cause,
                    _ => Task.FromResult(UiDialogResult.FromMessageBoxResult(MessageBoxResult.OK)),
                };
            }
        };
        var scheduler = new SwitchableUiScheduler
        {
            Mode = shape switch
            {
                "rejected" => SwitchableUiSchedulerMode.Reject,
                "schedule-throws" => SwitchableUiSchedulerMode.Throw,
                _ => SwitchableUiSchedulerMode.Accept,
            }
        };
        var service = new BmsLibraryDialogService(inner, scheduler);
        BMSLibrary.OperationDialogMessage a = Notice("a-1", "capA", UiDialogIcon.Information);
        BMSLibrary.OperationDialogMessage b = Notice("b-1", "capB", UiDialogIcon.Exclamation);

        service.Post([a, b], () => false);

        if (shape is "rejected" or "schedule-throws")
        {
            Assert.AreEqual(0, inner.MessageRequests.Count, "予約できなかった通知は別の手段で表示しない。");
            scheduler.Mode = SwitchableUiSchedulerMode.Accept;
        }
        else
        {
            Assert.AreEqual(2, inner.MessageRequests.Count, "失敗した組の後続の組も表示する。");
        }
        BMSLibrary.OperationDialogMessage c = Notice("c-1", "capC", UiDialogIcon.Hand);
        int before = inner.MessageRequests.Count;

        service.Post([c], () => false);

        Assert.AreEqual(before + 1, inner.MessageRequests.Count, "失敗の後の別の投函は通常どおり届く。");
        Assert.AreEqual("capC", inner.MessageRequests[^1].Caption);
    }

    /// <summary>
    /// 表示の開始の時点で抑止の判定が真なら、予約済みの通知も表示しない。依頼から表示までの間に始まった終了を止める。
    /// 判定を依頼の時点だけで行う誤りを検出する。
    /// </summary>
    [TestMethod]
    public void Post_SuppressedAtDisplayStartShowsNothing()
    {
        var inner = new RecordingUiDialogs(UiDialogResult.FromMessageBoxResult(MessageBoxResult.OK));
        var scheduler = new BmsLibraryLr2SongDbSyncTestSupport.QueuedUiScheduler();
        var service = new BmsLibraryDialogService(inner, scheduler);
        bool suppressed = false;

        service.Post([Notice("a-1", "capA", UiDialogIcon.Information)], () => suppressed);
        suppressed = true;
        scheduler.Drain();

        Assert.AreEqual(0, inner.MessageRequests.Count);
    }

    /// <summary>実装は注入された表示窓口と予約先を必須にし、自分で実物を作りません。</summary>
    [TestMethod]
    public void Constructor_RequiresInjectedUiDialogService()
    {
        IUiDialogService? missing = null;
        Assert.ThrowsExactly<ArgumentNullException>(() => new BmsLibraryDialogService(missing!, TestUiScheduler.CreateDefault()));
        IUiScheduler? missingScheduler = null;
        Assert.ThrowsExactly<ArgumentNullException>(() => new BmsLibraryDialogService(new RecordingUiDialogs(UiDialogResult.FromMessageBoxResult(MessageBoxResult.OK)), missingScheduler!));
    }

    private static BMSLibrary.OperationDialogMessage Notice(string text, string caption, UiDialogIcon icon) =>
        new(text, caption, UiDialogButton.OK, icon, UiDialogDefaultResult.OK);

    /// <summary>ライブラリは窓口を必須で受け取り、モデルが既定の実装を作って未注入を隠しません。</summary>
    [TestMethod]
    public void BMSLibrary_RejectsMissingDialogService()
    {
        // 窓口以外は有効な引数にし、引数検査の順序ではなく窓口の欠落そのものが拒否の原因であることを確かめます。
        using var workspace = TestWorkspace.Create(TestRunDirectory.RootDirectory);
        string songDbPath = Path.Combine(workspace.RootDirectory, "song.db");
        File.WriteAllBytes(songDbPath, []);
        BeMusicSeeker.Properties.Settings settings = TestSettingsFactory.CreateIsolatedSettings(values => values.OperationModeLR2DB = false);
        IBmsLibraryDialogService? missing = null;

        ArgumentNullException exception = Assert.ThrowsExactly<ArgumentNullException>(
            () => new BMSLibrary(
                new ApplicationDatabaseScope(),
                new BmsDatabaseLoadOptions(true, true, true),
                songDbPath,
                null,
                null,
                null,
                missing,
                null,
                () => BmsLibraryOptionsSnapshot.CreateCurrent(settings),
                TestUiScheduler.CreateDefault(),
                ApplicationPathSnapshot.FromExecutablePath(Path.Combine(workspace.RootDirectory, "BeMusicSeeker.exe")),
                new BmsRootRegistrationStore(settings, () => null)));
        Assert.AreEqual("dialogService", exception.ParamName);
    }

    /// <summary>通知と確認の要求だけを記録して固定の結果を返す、表示窓口の記録用実装です。</summary>
    private sealed class RecordingUiDialogs(UiDialogResult result) : IUiDialogService
    {
        internal List<UiMessageRequest> MessageRequests { get; } = [];
        internal List<UiConfirmationRequest> ConfirmRequests { get; } = [];
        internal Func<UiMessageRequest, Task<UiDialogResult>>? Handler { get; init; }

        public Task<UiDialogResult> ShowMessageAsync(UiMessageRequest request, CancellationToken cancellationToken = default)
        {
            MessageRequests.Add(request);
            return Handler?.Invoke(request) ?? Task.FromResult(result);
        }

        public Task<UiDialogResult> ConfirmAsync(UiConfirmationRequest request, CancellationToken cancellationToken = default)
        {
            ConfirmRequests.Add(request);
            return Task.FromResult(result);
        }

        public Task<UiWindowDialogResult<TResult>> ShowWindowAsync<TWindow, TResult>(UiWindowDialogRequest<TWindow, TResult> request, CancellationToken cancellationToken = default)
            where TWindow : Window => throw new NotSupportedException();
        public Task<UiFilePickerResult> PickFileAsync(UiFilePickerRequest request, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<UiFolderPickerResult> PickFolderAsync(UiFolderPickerRequest request, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<UiSaveFilePickerResult> PickSaveFileAsync(UiSaveFilePickerRequest request, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<UiProgressResult> RunWithProgressAsync(UiProgressRequest request, Func<UiProgressContext, Task> operation, CancellationToken cancellationToken = default) => throw new NotSupportedException();
    }

}
