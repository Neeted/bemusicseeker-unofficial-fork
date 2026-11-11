using System.Globalization;
using System.Text.RegularExpressions;
using System.Windows;
using BeMusicSeeker.Models.BmsLibraryInternal;
using BeMusicSeeker.Properties;
using BeMusicSeeker.TestSupport.Components;
using BeMusicSeeker.ViewModels;
using BeMusicSeeker.Views.Dialogs;

namespace BeMusicSeeker.Tests;

/// <summary>
/// プレイリスト側の操作通知を、受付の解放後に見出し・重要度ごとにまとめて待たずに依頼する表示役の規則を、偽の窓口で確認します。
/// 偽の窓口は本番の窓口と同じく、UI の文脈から呼ばれたときだけ同期にブロックし、ほかのスレッドからは未完了の Task を返します。
/// </summary>
[TestClass]
public sealed class PlaylistOperationNoticePresenterTests
{
    private static PlaylistOperationNotificationOwner.OperationNotification Notice(
        string messageResource,
        string captionResource,
        PlaylistOperationNotificationOwner.OperationNotificationSeverity severity)
        => new(PlaylistOperationNotificationOwner.OperationNotificationText.FromResource(messageResource), captionResource, severity);

    private static PlaylistOperationNotificationOwner.OperationNotificationReceipt Receipt(
        params PlaylistOperationNotificationOwner.OperationNotification[] notices)
        => PlaylistOperationNotificationOwner.OperationNotificationReceipt.Create(notices);

    /// <summary>
    /// 同じ見出しと重要度の通知を一件へまとめ、組を最初に現れた順に表示する。まとめた組は件数と標本の枠を持ち、一件だけの組は本文のままにする。
    /// 件数ごとの一件ずつの表示や、見出しの並べ替えで組の順が変わる誤りを検出する。
    /// </summary>
    [TestMethod]
    public async Task Request_GroupsByCaptionAndSeverityInFirstSeenOrder()
    {
        using var notices = new PlaylistNoticeProbe(() => false);
        var presenter = new PlaylistOperationNoticePresenter(notices.Dialogs, () => false);
        PlaylistOperationNotificationOwner.OperationNotificationSeverity warning = PlaylistOperationNotificationOwner.OperationNotificationSeverity.Warning;
        PlaylistOperationNotificationOwner.OperationNotificationSeverity error = PlaylistOperationNotificationOwner.OperationNotificationSeverity.Error;

        await presenter.BeginRequest(Receipt(
            Notice(nameof(Resources.Warn_PlaylistMutationBusy), nameof(Resources.MessageBoxTitle_Warning), warning),
            Notice(nameof(Resources.Msg_failed_add_playlist_entry), nameof(Resources.Error), error),
            Notice(nameof(Resources.Warn_PlaylistMutationStale), nameof(Resources.MessageBoxTitle_Warning), warning)),
            "test route");
        await notices.WaitForRequestsAsync(2);

        Assert.AreEqual(2, notices.Messages.Count);
        Assert.AreEqual(Resources.MessageBoxTitle_Warning, notices.Messages[0].Caption, "最初に現れた組から表示します。");
        Assert.AreEqual(MessageBoxImage.Exclamation, notices.Messages[0].Icon);
        StringAssert.Contains(notices.Messages[0].MessageBoxText, string.Format(Resources.Notice_GroupedCount, 2));
        StringAssert.Contains(
            notices.Messages[0].MessageBoxText,
            string.Join(" ", Resources.Warn_PlaylistMutationBusy.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)),
            "まとめた組は、本文の改行を空白へ畳んで一覧にします。");
        Assert.AreEqual(Resources.Msg_failed_add_playlist_entry, notices.Messages[1].MessageBoxText, "一件だけの組は本文のままです。");
        Assert.AreEqual(MessageBoxImage.Hand, notices.Messages[1].Icon);
        Assert.IsNull(notices.Messages[1].Owner, "Owner は窓口の解決に任せます。");
    }

    /// <summary>先行の組の表示が完了するまで次の組を依頼せず、完了後に続ける。表示の完了を待たずに全組を依頼する誤りを検出する。</summary>
    [TestMethod]
    public async Task Request_WaitsForPreviousGroupDisplayBeforeNextGroup()
    {
        using var notices = new PlaylistNoticeProbe(() => false) { HoldDisplay = true };
        var presenter = new PlaylistOperationNoticePresenter(notices.Dialogs, () => false);

        await presenter.BeginRequest(Receipt(
            Notice(nameof(Resources.Warn_PlaylistMutationBusy), nameof(Resources.MessageBoxTitle_Warning), PlaylistOperationNotificationOwner.OperationNotificationSeverity.Warning),
            Notice(nameof(Resources.Msg_failed_add_playlist_entry), nameof(Resources.Error), PlaylistOperationNotificationOwner.OperationNotificationSeverity.Error)),
            "test route");
        await notices.WaitForRequestsAsync(1);

        Assert.AreEqual(1, notices.Messages.Count, "先行の表示が未完了の間は、次の組を依頼しません。");
        notices.CompleteHeld();
        await notices.WaitForRequestsAsync(2);
        Assert.AreEqual(2, notices.Messages.Count);
    }

    /// <summary>
    /// 窓口の失敗（失敗の結果・失敗した Task・同期の例外）で、後続の組を表示し、操作を失敗させず、診断を一件だけ残し、未観測の例外を出さない。
    /// 一つの組の失敗で残りを捨てる誤りや、失敗を呼出し元へ伝える誤り、診断を残さない誤りを検出する。
    /// </summary>
    /// <remarks>共有のログ構成を一時的に差し替えるため、並列実行しない。</remarks>
    [TestMethod]
    [DoNotParallelize]
    [DataRow("result")]
    [DataRow("task")]
    [DataRow("throw")]
    public async Task Request_ContinuesAfterDisplayFailureWithoutThrowing(string shape)
    {
        var unobserved = new List<Exception>();
        void OnUnobserved(object? sender, UnobservedTaskExceptionEventArgs args)
        {
            lock (unobserved) { unobserved.AddRange(args.Exception.InnerExceptions.Where(exception => exception.Message.StartsWith("display ", StringComparison.Ordinal))); }
        }
        TaskScheduler.UnobservedTaskException += OnUnobserved;
        try
        {
            await ApplicationLoggingTestScope.RunAsync(async loggingScope =>
            {
                string logPath = loggingScope.ConfigureFileLog();
                using var notices = new PlaylistNoticeProbe(() => false)
                {
                    Behavior = (index, _) => index > 0
                        ? Task.FromResult(UiDialogResult.FromMessageBoxResult(MessageBoxResult.OK))
                        : shape switch
                        {
                            "result" => Task.FromResult(UiDialogResult.Failed(new InvalidOperationException("display result failed"))),
                            "task" => Task.FromException<UiDialogResult>(new InvalidOperationException("display task failed")),
                            _ => throw new InvalidOperationException("display threw"),
                        },
                };
                var presenter = new PlaylistOperationNoticePresenter(notices.Dialogs, () => false);

                await presenter.BeginRequest(Receipt(
                    Notice(nameof(Resources.Warn_PlaylistMutationBusy), nameof(Resources.MessageBoxTitle_Warning), PlaylistOperationNotificationOwner.OperationNotificationSeverity.Warning),
                    Notice(nameof(Resources.Msg_failed_add_playlist_entry), nameof(Resources.Error), PlaylistOperationNotificationOwner.OperationNotificationSeverity.Error)),
                    "test route");
                // 失敗した組の診断は、後続の組の依頼より前に残るため、後続の組の到達を終端の根拠にします。
                await notices.WaitForRequestsAsync(2);

                Assert.AreEqual(2, notices.Messages.Count, "失敗した組の後も、後続の組を表示します。");
                string log = ApplicationLoggingTestScope.ReadFileLog(logPath);
                Assert.AreEqual(1, Regex.Count(log, "file_db_mutation_notification_failed"), "失敗した組について診断をちょうど一件残します。");
                StringAssert.Contains(log, "display " + (shape == "throw" ? "threw" : shape + " failed"), "診断に元の例外が付きます。");
            });
            GC.Collect();
            GC.WaitForPendingFinalizers();
            GC.Collect();
            lock (unobserved) { Assert.AreEqual(0, unobserved.Count, "表示の失敗が未観測の Task の例外として残りません。"); }
        }
        finally { TaskScheduler.UnobservedTaskException -= OnUnobserved; }
    }

    /// <summary>通知を依頼の時点の言語で整形する。収集した時点の言語（言語A）へ固定すると、言語Bへ切り替えた後の本文が言語Aのままになる。</summary>
    /// <remarks>共有の <see cref="Resources.Culture"/> を一時的に変更するため、ほかの試験と並列実行しない。</remarks>
    [TestMethod]
    [DoNotParallelize]
    public async Task Request_FormatsInLanguageAtRequestTime()
    {
        CultureInfo? previous = Resources.Culture;
        try
        {
            Resources.Culture = CultureInfo.GetCultureInfo("ja-JP");
            using var notices = new PlaylistNoticeProbe(() => false);
            var presenter = new PlaylistOperationNoticePresenter(notices.Dialogs, () => false);
            PlaylistOperationNotificationOwner.OperationNotificationReceipt receipt = Receipt(
                Notice(nameof(Resources.Msg_failed_add_playlist_entry), nameof(Resources.Error), PlaylistOperationNotificationOwner.OperationNotificationSeverity.Error));
            string japanese = Resources.Msg_failed_add_playlist_entry;

            Resources.Culture = CultureInfo.GetCultureInfo("en-US");
            string english = Resources.Msg_failed_add_playlist_entry;
            Assert.AreNotEqual(japanese, english, "言語Aと言語Bの本文が異なる前提です。");
            await presenter.BeginRequest(receipt, "test route");
            await notices.WaitForRequestsAsync(1);

            Assert.AreEqual(english, notices.Messages.Single().MessageBoxText);
        }
        finally { Resources.Culture = previous; }
    }

    /// <summary>終了の要求の後は表示を依頼せず、診断のログへ残し、診断の対象として数える。終了の要求を確かめずに窓口へ依頼する誤りと、ログを残さない誤りを検出する。</summary>
    /// <remarks>共有のログ構成を一時的に差し替えるため、並列実行しない。</remarks>
    [TestMethod]
    [DoNotParallelize]
    public async Task Request_DoesNotPresentAfterShutdownRequest()
    {
        await ApplicationLoggingTestScope.RunAsync(async loggingScope =>
        {
            string logPath = loggingScope.ConfigureFileLog();
            using var notices = new PlaylistNoticeProbe(() => false);
            bool shutdown = true;
            var presenter = new PlaylistOperationNoticePresenter(notices.Dialogs, () => shutdown);

            await presenter.BeginRequest(Receipt(
                Notice(nameof(Resources.Msg_failed_add_playlist_entry), nameof(Resources.Error), PlaylistOperationNotificationOwner.OperationNotificationSeverity.Error)),
                "test route");

            Assert.AreEqual(0, notices.Messages.Count);
            Assert.AreEqual(1, presenter.RequestCount, "表示しなかった要求も観測口では数えます。");
            string log = ApplicationLoggingTestScope.ReadFileLog(logPath);
            StringAssert.Contains(log, "playlist_operation_notice_skipped reason=shutdown route=test route count=1");
        });
    }

    /// <summary>
    /// 収集先を閉じるときに、受け取られていない通知が残っていれば、失われたことを診断のログへ残す。
    /// 取得の後に積まれた通知が、ログなしで失われる誤りを検出する。
    /// </summary>
    /// <remarks>共有のログ構成を一時的に差し替えるため、並列実行しない。</remarks>
    [TestMethod]
    [DoNotParallelize]
    public async Task Session_DisposeWithUnpublishedNotices_LogsTheDiscard()
    {
        await ApplicationLoggingTestScope.RunAsync(loggingScope =>
        {
            string logPath = loggingScope.ConfigureFileLog();
            var owner = new PlaylistOperationNotificationOwner();
            using (PlaylistOperationNotificationOwner.OperationNotificationSession session = owner.BeginSession())
            {
                session.Add(Notice(nameof(Resources.Msg_failed_add_playlist_entry), nameof(Resources.Error), PlaylistOperationNotificationOwner.OperationNotificationSeverity.Error));
                Assert.IsFalse(session.TakeReceipt().IsEmpty);
                session.Add(Notice(nameof(Resources.Warn_PlaylistMutationBusy), nameof(Resources.MessageBoxTitle_Warning), PlaylistOperationNotificationOwner.OperationNotificationSeverity.Warning));
            }

            string log = ApplicationLoggingTestScope.ReadFileLog(logPath);
            StringAssert.Contains(log, "playlist_operation_notice_discarded reason=session_disposed_with_unpublished_notices count=1");
            return Task.CompletedTask;
        });
    }

    /// <summary>
    /// 外側の収集先がすでに閉じられている場合、内側の通知は外側へ移さず、内側へ残す。
    /// 外側の破棄との競合で、通知を失う誤りを検出する。
    /// </summary>
    [TestMethod]
    public void Session_ForwardToDisposedParent_KeepsTheNotices()
    {
        var owner = new PlaylistOperationNotificationOwner();
        PlaylistOperationNotificationOwner.OperationNotificationSession parent = owner.BeginSession();
        using PlaylistOperationNotificationOwner.OperationNotificationSession child = owner.BeginSession();
        child.Add(Notice(nameof(Resources.Msg_failed_add_playlist_entry), nameof(Resources.Error), PlaylistOperationNotificationOwner.OperationNotificationSeverity.Error));
        parent.Dispose();

        Assert.IsFalse(child.TryForwardToParent(), "閉じた外側へは移さない。");
        Assert.AreEqual(1, child.TakeReceipt().Notifications.Count, "移せなかった通知は内側に残る。");
    }

    /// <summary>表示役が未接続のまま通知が生じた場合は、通知を表示せず、失われたことを診断のログへ残す。無言で捨てる誤りを検出する。</summary>
    /// <remarks>共有のログ構成を一時的に差し替えるため、並列実行しない。</remarks>
    [TestMethod]
    [DoNotParallelize]
    public async Task Notify_WithoutPresenter_LogsTheDiscard()
    {
        await ApplicationLoggingTestScope.RunAsync(loggingScope =>
        {
            string logPath = loggingScope.ConfigureFileLog();

            PlaylistOperationNotices.Notify(null, null, PlaylistOperationNotices.InvalidOutputDirectory(), "test route");

            StringAssert.Contains(ApplicationLoggingTestScope.ReadFileLog(logPath),
                "playlist_operation_notice_discarded reason=presenter_not_connected route=test route count=1");
            return Task.CompletedTask;
        });
    }

    /// <summary>空の領収書では窓口へ依頼しない。</summary>
    [TestMethod]
    public async Task Request_DoesNotPresentEmptyReceipt()
    {
        using var notices = new PlaylistNoticeProbe(() => false);
        var presenter = new PlaylistOperationNoticePresenter(notices.Dialogs, () => false);

        await presenter.BeginRequest(Receipt(), "test route");

        Assert.AreEqual(0, notices.Messages.Count);
        Assert.AreEqual(0, presenter.RequestCount);
    }

    /// <summary>
    /// UI の文脈から依頼しても、窓口を UI の文脈の外から呼び、表示が未完了のまま戻る。
    /// UI の文脈のまま窓口へ依頼して同期のモーダルにする誤りや、表示の完了を待つ誤りを検出する。
    /// </summary>
    [TestMethod]
    public async Task BeginRequest_CalledFromUiContext_RequestsOffUiContextWithoutWaitingForDisplay()
    {
        using var uiContext = new SingleThreadSynchronizationContext();
        using var notices = new PlaylistNoticeProbe(() => SynchronizationContext.Current == uiContext) { HoldDisplay = true };
        var presenter = new PlaylistOperationNoticePresenter(notices.Dialogs, () => false);

        Task operation = uiContext.RunAsync(() => presenter.BeginRequest(Receipt(
            Notice(nameof(Resources.Msg_failed_add_playlist_entry), nameof(Resources.Error), PlaylistOperationNotificationOwner.OperationNotificationSeverity.Error)),
            "test route"));
        await ConcurrencyProbe.AssertCompletesWhileHeldAsync(operation, "notice-request-ui-context");
        await notices.WaitForRequestsAsync(1);

        Assert.IsFalse(notices.BlockedOnUiContext, "窓口の呼出しを UI の文脈から行いません。");
    }
}
