using System.IO;
using System.Windows;
using System.Windows.Automation.Peers;
using System.Windows.Automation.Provider;
using System.Windows.Controls;
using System.Windows.Threading;
using BeMusicSeeker.Models;
using BeMusicSeeker.Models.Utils;
using BeMusicSeeker.Properties;
using BeMusicSeeker.TestSupport.Components;
using BeMusicSeeker.TestSupport.Wpf;
using BeMusicSeeker.Views;
using BeMusicSeeker.Views.Dialogs;

namespace BeMusicSeeker.Tests;

/// <summary>GitHubのスター依頼の実画面で、操作ごとの選択・リンクの起動・起動失敗の通知を確認します。</summary>
[TestClass]
// 実WindowがApplication.Resourcesとactive modal ownerを共有するため並列化しません。
[DoNotParallelize]
public sealed class GitHubStarPromptDialogTests
{
    /// <summary>
    /// 保証：スターはリポジトリのURLを一度だけ開いて Star を返し、今後表示しないは Decline、あとで・× は URL を開かずに Later を返す。
    /// どの操作でも通知は出さない。スターが既定（Enter）、あとでが取消（Esc）のボタンで、文言と所有者が画面へ届く。
    /// ×や取消を Star・Decline として依頼を終える誤り、別のURLを開く誤り、成功時に通知を出す誤りを検出する。
    /// </summary>
    /// <remarks>実coordinatorで所有者付きに表示し、ボタンは自動化の Invoke（実際の Click 経路）で押します。OSのキー入力は保証対象外で、Enter・Esc は WPF 標準の IsDefault・IsCancel の属性で確認します。</remarks>
    [TestMethod]
    [DataRow("Star", true, DisplayName = "スター")]
    [DataRow("Later", false, DisplayName = "あとで")]
    [DataRow("Never", true, DisplayName = "今後表示しない")]
    [DataRow("Close", false, DisplayName = "×")]
    public void Operation_ReturnsChoiceAndOpensRepositoryOnlyForStar(string operation, bool expectedAccepted)
    {
        TestUiDispatcherHost.RunWindowTest(scope =>
        {
            var shell = new RecordingShellGateway();
            var dialogs = new FileDbReportRecordingDialogs();
            PromptRun run = ShowPrompt(scope, shell, dialogs, operation, created =>
            {
                Assert.AreEqual(Resources.About_application_name, created.Title);
                Assert.AreEqual(Resources.GitHubStarPrompt_later_button, Button(created, "laterButton").Content);
                Assert.AreEqual(Resources.GitHubStarPrompt_never_button, Button(created, "neverButton").Content);
                Assert.IsTrue(Button(created, "starButton").IsDefault, "Enterはスターのボタンです。");
                Assert.IsFalse(Button(created, "starButton").IsCancel);
                Assert.IsTrue(Button(created, "laterButton").IsCancel, "Escはあとでのボタンです。");
                Assert.IsFalse(Button(created, "laterButton").IsDefault);
                Assert.IsFalse(Button(created, "neverButton").IsDefault || Button(created, "neverButton").IsCancel);
            });
            try
            {
                run.AwaitOperated();
                UiWindowDialogResult<GitHubStarPromptChoice> result = run.AwaitResult();

                GitHubStarPromptChoice expected = operation switch
                {
                    "Star" => GitHubStarPromptChoice.Star,
                    "Never" => GitHubStarPromptChoice.Decline,
                    _ => GitHubStarPromptChoice.Later,
                };
                Assert.AreEqual(expected, result.Value);
                Assert.AreEqual(expectedAccepted, result.IsAccepted);
                Assert.IsFalse(run.Window?.IsVisible ?? true);
                if (operation == "Star")
                {
                    Assert.AreEqual(1, shell.Requests.Count);
                    Assert.AreEqual(ExternalShellRequestKind.Url, shell.Requests.Single().Kind);
                    Assert.AreEqual("https://github.com/Neeted/bemusicseeker-unofficial-fork", shell.Requests.Single().Target);
                }
                else
                {
                    Assert.AreEqual(0, shell.Requests.Count, "スター以外ではリンクを開きません。");
                }
                Assert.AreEqual(0, dialogs.Messages.Count, "成功した操作では通知しません。");
            }
            finally
            {
                run.Cleanup();
            }
        });
    }

    /// <summary>
    /// 保証：リポジトリを開けない場合、画面を表示したまま、画面を所有者とする失敗の通知を一度だけ出し、通知の終了後に Later で閉じる。
    /// 通知の失敗（表示できない・待機後の例外・同期の例外）でも再通知せず、同じく Later で閉じる。
    /// 開けないのに Star を返して依頼を終える誤り、通知を待たずに閉じる誤り、通知の失敗で閉じられなくなる誤りを検出する。
    /// </summary>
    /// <remarks>
    /// 依頼の画面はモーダルで、閉じるまでテスト側の待機へ戻らないため、通知中の確認と通知の完了は画面の Dispatcher 上で行います。
    /// </remarks>
    [TestMethod]
    [DataRow(0, DisplayName = "通知を表示")]
    [DataRow(1, DisplayName = "通知を表示できない")]
    [DataRow(2, DisplayName = "通知の待機後の例外")]
    [DataRow(3, DisplayName = "通知の同期の例外")]
    public void LaunchFailure_NotifiesOnceWhileOpenAndClosesAsLater(int notificationKind)
    {
        TestUiDispatcherHost.RunWindowTest(scope =>
        {
            var launchFailure = new IOException("repository launch failed");
            var shell = new RecordingShellGateway { Failure = launchFailure };
            var noticeChecked = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var notice = new TaskCompletionSource<UiDialogResult>(TaskCreationOptions.RunContinuationsAsynchronously);
            PromptRun? run = null;
            var dialogs = new FileDbReportRecordingDialogs
            {
                MessageHandler = _ =>
                {
                    if (notificationKind == 3)
                    {
                        noticeChecked.TrySetResult();
                        throw new IOException("notice synchronous failure");
                    }
                    Dispatcher.CurrentDispatcher.BeginInvoke(DispatcherPriority.Background, new Action(() =>
                    {
                        try
                        {
                            Assert.IsTrue(run?.Window?.IsVisible == true, "通知の間は画面を閉じません。");
                            Assert.IsTrue(run?.Operation.IsCompleted == false, "通知の間は結果を返しません。");
                            noticeChecked.TrySetResult();
                        }
                        catch (Exception failure) { noticeChecked.TrySetException(failure); }
                        finally
                        {
                            switch (notificationKind)
                            {
                                case 0: notice.TrySetResult(UiDialogResult.FromMessageBoxResult(MessageBoxResult.OK)); break;
                                case 1: notice.TrySetResult(UiDialogResult.NotShown(UiDialogStatus.Failed)); break;
                                default: notice.TrySetException(new IOException("notice awaited failure")); break;
                            }
                        }
                    }));
                    return notice.Task;
                }
            };
            run = ShowPrompt(scope, shell, dialogs, "Star", _ => { });
            try
            {
                run.AwaitOperated();
                UiWindowDialogResult<GitHubStarPromptChoice> result = run.AwaitResult();
                TestUiDispatcherHost.AwaitTaskOnDispatcher(noticeChecked.Task, "star-prompt-notice-checked");

                Assert.AreEqual(GitHubStarPromptChoice.Later, result.Value);
                Assert.IsFalse(result.IsAccepted);
                Assert.IsFalse(run.Window?.IsVisible ?? true);
                Assert.AreEqual(1, shell.Requests.Count);
                Assert.AreEqual(1, dialogs.Messages.Count, "通知は一度だけです。");
                UiMessageRequest message = dialogs.Messages.Single();
                Assert.AreSame(run.Window, message.Owner);
                StringAssert.Contains(message.MessageBoxText, launchFailure.Message);
            }
            finally
            {
                notice.TrySetResult(UiDialogResult.FromMessageBoxResult(MessageBoxResult.OK));
                run.Cleanup();
            }
        });
    }

    private static PromptRun ShowPrompt(
        TestWindowPresentationScope scope,
        IExternalShellGateway shell,
        IUiDialogService dialogs,
        string operation,
        Action<GitHubStarPromptDialog> assertRendered)
    {
        var owner = new Window { Width = 240, Height = 90, Content = new Border() };
        scope.ShowAndWaitForContentRendered(owner);
        var coordinator = new UiDialogCoordinator(new UiDialogOwnerResolver(), window =>
        {
            scope.PrepareForOwnedPresentation(window);
            return UiDialogOwnerResolver.PushActiveModal(window);
        });
        var run = new PromptRun();
        var request = new UiWindowDialogRequest<GitHubStarPromptDialog, GitHubStarPromptChoice>(() =>
        {
            var created = new GitHubStarPromptDialog(shell, dialogs);
            run.Window = created;
            created.ContentRendered += (_, _) => created.Dispatcher.BeginInvoke(DispatcherPriority.Background, new Action(() =>
            {
                try
                {
                    Assert.AreSame(owner, created.Owner);
                    assertRendered(created);
                    if (operation == "Close") { created.Close(); }
                    else
                    {
                        // 自動化の Invoke は Click と同じ経路（OnClick）を通り、IsCancel のボタンでは取消の結果で閉じます。
                        Button target = Button(created, operation switch
                        {
                            "Star" => "starButton",
                            "Later" => "laterButton",
                            "Never" => "neverButton",
                            _ => throw new ArgumentOutOfRangeException(nameof(operation), operation, null),
                        });
                        ((IInvokeProvider)new ButtonAutomationPeer(target).GetPattern(PatternInterface.Invoke)!).Invoke();
                    }
                    run.Operated.TrySetResult();
                }
                catch (Exception failure) { run.Operated.TrySetException(failure); if (created.IsVisible) { created.Close(); } }
            }));
            return created;
        }, created => created.Choice, owner);
        run.Operation = owner.Dispatcher.InvokeAsync(() => coordinator.ShowWindowAsync(request)).Task.Unwrap();
        return run;
    }

    private static Button Button(Window window, string name) => (Button)window.FindName(name);

    /// <summary>表示中の依頼の画面・操作の完了・表示の結果を持ち、失敗時も画面を閉じて結果を回収します。</summary>
    private sealed class PromptRun
    {
        internal GitHubStarPromptDialog? Window { get; set; }

        internal TaskCompletionSource Operated { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        internal Task<UiWindowDialogResult<GitHubStarPromptChoice>> Operation { get; set; } = Task.FromException<UiWindowDialogResult<GitHubStarPromptChoice>>(new InvalidOperationException("not shown"));

        internal void AwaitOperated() =>
            TestUiDispatcherHost.AwaitTaskOnDispatcher(
                TestUiDispatcherHost.AwaitNotificationAsync(Operated.Task, Operation, "star-prompt-operation"),
                "star-prompt-operate");

        internal UiWindowDialogResult<GitHubStarPromptChoice> AwaitResult()
        {
            TestUiDispatcherHost.AwaitTaskOnDispatcher(Operation, "star-prompt-result");
            return Operation.GetAwaiter().GetResult();
        }

        internal void Cleanup()
        {
            if (Window?.IsVisible == true) { Window.Close(); }
            try { TestUiDispatcherHost.AwaitTaskOnDispatcher(Operation, "star-prompt-cleanup"); }
            catch (Exception) when (Operation.IsFaulted) { }
        }
    }

    private sealed class RecordingShellGateway : IExternalShellGateway
    {
        internal List<ExternalShellRequest> Requests { get; } = [];

        internal Exception? Failure { get; init; }

        public void Open(ExternalShellRequest request)
        {
            Requests.Add(request);
            if (Failure != null) { throw Failure; }
        }

        public ExplorerOpenResult OpenFileAndSelect(string filePath) => throw new NotSupportedException();

        public ExplorerOpenResult OpenDirectory(string directoryPath) => throw new NotSupportedException();

        public bool TryOpenDirectoryWithExplorerProcess(string directoryPath, out string failureReason)
        {
            failureReason = string.Empty;
            throw new NotSupportedException();
        }
    }
}
