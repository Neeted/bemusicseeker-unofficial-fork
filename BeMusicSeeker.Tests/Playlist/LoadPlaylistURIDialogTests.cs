using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Media;
using System.Windows.Threading;
using BeMusicSeeker.Views;
using BeMusicSeeker.Views.Dialogs;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace BeMusicSeeker.Tests;

/// <summary>URI入力の追記と実JSON picker接続・取消・応答性を検証する。</summary>
/// <remarks>compiledボタンからpicker要求と入力へ渡す接続を実Controlで確認する。表示領域だけ実Window表示を使い、ダウンロード・登録は取込み所有者へ分担する。</remarks>
[TestClass]
public sealed class LoadPlaylistURIDialogTests
{
    /// <summary>既存URIへ空白を除いた新URIを改行で追加し、空入力と空白だけの追加を区別する。既存内容の上書きと空行の増殖を検出する。</summary>
    [TestMethod]
    public void AppendUriInputLine_AppendsToEndWithNewLine()
    {
        string appended = LoadPlaylistURIDialog.AppendUriInputLine("https://example.com/first.json", "  file:///C:/tables/second.json  ");

        Assert.AreEqual("https://example.com/first.json" + Environment.NewLine + "file:///C:/tables/second.json", appended);
        Assert.AreEqual("file:///C:/tables/first.json", LoadPlaylistURIDialog.AppendUriInputLine(string.Empty, "file:///C:/tables/first.json"));
        Assert.AreEqual("existing", LoadPlaylistURIDialog.AppendUriInputLine("existing", " "));
    }

    /// <summary>標準と拡大フォントの実描画でURI入力と全footerが領域内に収まり重ならない。文字拡大による入力・操作の欠落を検出する。</summary>
    [DoNotParallelize]
    [TestMethod]
    public void Presentation_ContainsUriInputAboveFooterAtDefaultAndFontScale()
    {
        TestUiDispatcherHost.RunWindowTest(windowTest =>
        {
            var dialog = new LoadPlaylistURIDialog();
            var host = new Window
            {
                Content = dialog,
                ShowInTaskbar = false,
                WindowStyle = WindowStyle.None,
                Width = 640,
                Height = 320
            };
            try
            {
                windowTest.ShowAndWaitForContentRendered(host);
                var input = (TextBox)dialog.FindName("textBoxURIInput");
                Border content = FindDialogContentBorder(dialog);
                Button[] footerButtons = FindVisualDescendants<Button>(dialog)
                    .Where(button => button.TemplatedParent == null)
                    .Where(button => AutomationProperties.GetAutomationId(button).StartsWith(
                        "LoadPlaylistUri",
                        StringComparison.Ordinal))
                    .ToArray();
                Assert.IsTrue(footerButtons.Length > 0);

                // 標準相当の文字サイズと3倍の拡大で、入力と全操作が領域内に収まることを確認する。
                foreach (double fontSize in new[] { 12d, 36d })
                {
                    TextElement.SetFontSize(dialog, fontSize);
                    host.UpdateLayout();

                    Assert.IsTrue(content.ActualWidth > 0d && content.ActualHeight > 0d);
                    AssertVisualBoundsInside(content, input);
                    foreach (Button footerButton in footerButtons)
                    {
                        AssertVisualBoundsInside(content, footerButton);
                    }

                    Rect inputBounds = GetVisualBounds(content, input);
                    double footerTop = footerButtons
                        .Select(button => GetVisualBounds(content, button).Top)
                        .Min();
                    Assert.IsTrue(
                        inputBounds.Bottom <= footerTop + 0.5d,
                        $"フォント{fontSize}で入力領域{inputBounds}がfooter上端{footerTop}を越えない。");
                }
            }
            finally
            {
                if (host.IsVisible)
                {
                    host.Close();
                }
            }
        });
    }

    /// <summary>compiledボタンは正しい所有WindowとJSON限定条件をpickerへ一回渡し、受理パスを既存URIへ追記する。誤った選択形式・所有者・入力先への配送を検出する。</summary>
    [TestMethod]
    public void OpenLocalFile_UsesInjectedJsonPickerAndAppendsAcceptedPathWithoutShowingModal()
    {
        TestUiDispatcherHost.Invoke(() =>
        {
            const string selectedPath = @"C:\tables\accepted.json";
            var dialogs = new RecordingLoadPlaylistDialogService(
                new UiFilePickerResult(UiDialogStatus.Accepted, [selectedPath]));
            var dialog = new LoadPlaylistURIDialog(dialogs);
            var host = new Window
            {
                Content = dialog,
                ShowInTaskbar = false,
                WindowStyle = WindowStyle.None,
                Width = 640,
                Height = 320
            };
            try
            {
                Materialize(host);
                var input = (TextBox)dialog.FindName("textBoxURIInput");
                input.Text = "https://example.test/first.json";

                ((Button)dialog.FindName("openLocalFileButton")).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));

                Assert.AreEqual(
                    "https://example.test/first.json" + Environment.NewLine + selectedPath,
                    input.Text);
                Assert.AreEqual(1, dialogs.FileRequests.Count);
                Assert.AreEqual(".json", dialogs.FileRequests[0].DefaultExtension);
                Assert.AreEqual(BeMusicSeeker.Properties.Resources.Json_file_exts, dialogs.FileRequests[0].Filter);
                // 翻訳される表示名とは別に、JSONだけを選択する形式指定を検証する。
                string[] filterParts = dialogs.FileRequests[0].Filter.Split('|');
                Assert.AreEqual(2, filterParts.Length);
                Assert.AreEqual("*.json", filterParts[1]);
                Assert.AreSame(host, dialogs.FileRequests[0].Owner);
                Assert.AreEqual(0, dialogs.MessageRequestCount);
            }
            finally
            {
                if (host.IsVisible)
                {
                    host.Close();
                }
            }
        });
    }

    /// <summary>pickerの取消と閉鎖は既存入力を保持し、メッセージ表示を起こさない。非受理結果での追記や誤通知を検出する。</summary>
    [DataTestMethod]
    // 明示取消とWindowの閉鎖はどちらも受理パスなしとして入力を維持する。
    [DataRow(UiDialogStatus.CancelledByUser)]
    [DataRow(UiDialogStatus.ClosedByUser)]
    public void OpenLocalFile_CancelOrClosedLeavesExistingInputUnchanged(object statusValue)
    {
        var status = (UiDialogStatus)statusValue;
        TestUiDispatcherHost.Invoke(() =>
        {
            var dialogs = new RecordingLoadPlaylistDialogService(new UiFilePickerResult(status));
            var dialog = new LoadPlaylistURIDialog(dialogs);
            var host = new Window
            {
                Content = dialog,
                ShowInTaskbar = false,
                WindowStyle = WindowStyle.None,
                Width = 640,
                Height = 320
            };
            try
            {
                Materialize(host);
                var input = (TextBox)dialog.FindName("textBoxURIInput");
                input.Text = "existing input";

                ((Button)dialog.FindName("openLocalFileButton")).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));

                Assert.AreEqual("existing input", input.Text);
                Assert.AreEqual(0, dialogs.MessageRequestCount);
            }
            finally
            {
                if (host.IsVisible)
                {
                    host.Close();
                }
            }
        });
    }

    /// <summary>picker失敗は元例外を保持して呼出元へ返し、既存入力と通知件数を変えない。失敗を取消へ隠す処理と二重通知を検出する。</summary>
    [TestMethod]
    public void OpenLocalFile_FailedPickerPropagatesCauseWithoutMutatingInputOrShowingModal()
    {
        TestUiDispatcherHost.Invoke(() =>
        {
            var sentinel = new IOException("picker sentinel");
            var dialogs = new RecordingLoadPlaylistDialogService(
                new UiFilePickerResult(UiDialogStatus.Failed, error: sentinel));
            var dialog = new LoadPlaylistURIDialog(dialogs);
            var host = new Window
            {
                Content = dialog,
                ShowInTaskbar = false,
                WindowStyle = WindowStyle.None,
                Width = 640,
                Height = 320
            };
            try
            {
                Materialize(host);
                var input = (TextBox)dialog.FindName("textBoxURIInput");
                input.Text = "existing input";

                InvalidOperationException failure = Assert.ThrowsException<InvalidOperationException>(
                    () => dialog.HandleOpenLocalFileAsync().GetAwaiter().GetResult());

                Assert.AreSame(sentinel, failure.InnerException);
                Assert.AreEqual("existing input", input.Text);
                Assert.AreEqual(0, dialogs.MessageRequestCount);
            }
            finally
            {
                if (host.IsVisible)
                {
                    host.Close();
                }
            }
        });
    }

    /// <summary>picker gate中もDispatcher処理を進め、完了後にだけ受理パスを追記する。UI同期待ちと早期追記を検出する。</summary>
    [TestMethod]
    public void OpenLocalFileAsync_AwaitsPickerWithoutBlockingDispatcherAndAppliesCompletion()
    {
        TestUiDispatcherHost.Invoke(() =>
        {
            var completion = new TaskCompletionSource<UiFilePickerResult>(
                TaskCreationOptions.RunContinuationsAsynchronously);
            var dialogs = new RecordingLoadPlaylistDialogService(
                new UiFilePickerResult(UiDialogStatus.CancelledByUser))
            {
                FileTask = completion.Task
            };
            var dialog = new LoadPlaylistURIDialog(dialogs);
            var host = new Window
            {
                Content = dialog,
                ShowInTaskbar = false,
                WindowStyle = WindowStyle.None,
                Width = 640,
                Height = 320
            };
            SynchronizationContext? previousContext = SynchronizationContext.Current;
            Task? route = null;
            try
            {
                Materialize(host);
                var input = (TextBox)dialog.FindName("textBoxURIInput");
                input.Text = "existing input";
                SynchronizationContext.SetSynchronizationContext(new DispatcherSynchronizationContext(host.Dispatcher));

                route = dialog.HandleOpenLocalFileAsync();

                Assert.IsFalse(route.IsCompleted);
                bool dispatcherWorkCompleted = false;
                DispatcherOperation dispatcherWork = host.Dispatcher.BeginInvoke(
                    DispatcherPriority.Background,
                    new Action(() => dispatcherWorkCompleted = true));
                TestUiDispatcherHost.AwaitTaskOnDispatcher(dispatcherWork.Task, "URI-picker-dispatcher-work");
                Assert.IsTrue(dispatcherWorkCompleted);

                completion.TrySetResult(new UiFilePickerResult(
                    UiDialogStatus.Accepted,
                    [@"C:\tables\pending.json"]));
                TestUiDispatcherHost.AwaitTaskOnDispatcher(route, "URI-picker-route-terminal");

                Assert.AreEqual(
                    "existing input" + Environment.NewLine + @"C:\tables\pending.json",
                    input.Text);
            }
            finally
            {
                try
                {
                    completion.TrySetResult(new UiFilePickerResult(UiDialogStatus.CancelledByUser));
                    if (route != null) { TestUiDispatcherHost.AwaitTaskOnDispatcher(route, "URI-picker-finally-route"); }
                    if (host.IsVisible)
                    {
                        host.Close();
                    }
                }
                finally
                {
                    SynchronizationContext.SetSynchronizationContext(previousContext);
                }
            }
        });
    }

    private static void Materialize(Window host)
    {
        host.Measure(new Size(640, 320));
        host.Arrange(new Rect(0, 0, 640, 320));
        host.UpdateLayout();
        TestUiDispatcherHost.ProcessQueuedPresentation();
    }

    private static Border FindDialogContentBorder(FrameworkElement root)
    {
        Border[] candidates = FindVisualDescendants<Border>(root)
            .Where(border => border.TryFindResource("App.Canonical.DialogContentStyle") is Style contentStyle
                && StyleChainContains(border.Style, contentStyle))
            .ToArray();
        Assert.AreEqual(1, candidates.Length, "URI入力の表示領域を一つ特定できる。");
        return candidates[0];
    }

    private static void AssertVisualBoundsInside(Visual ancestor, FrameworkElement descendant)
    {
        Rect bounds = GetVisualBounds(ancestor, descendant);
        var ancestorBounds = new Rect(0d, 0d, ((FrameworkElement)ancestor).ActualWidth, ((FrameworkElement)ancestor).ActualHeight);
        const double tolerance = 0.5d;
        Assert.IsTrue(
            bounds.Left >= ancestorBounds.Left - tolerance
                && bounds.Top >= ancestorBounds.Top - tolerance
                && bounds.Right <= ancestorBounds.Right + tolerance
                && bounds.Bottom <= ancestorBounds.Bottom + tolerance,
            $"{descendant.Name}の領域{bounds}が内容領域{ancestorBounds}からはみ出している。");
    }

    private static Rect GetVisualBounds(Visual ancestor, FrameworkElement descendant)
        => descendant.TransformToAncestor(ancestor)
            .TransformBounds(new Rect(0d, 0d, descendant.ActualWidth, descendant.ActualHeight));

    private static IEnumerable<T> FindVisualDescendants<T>(DependencyObject root)
        where T : DependencyObject
    {
        for (int index = 0; index < VisualTreeHelper.GetChildrenCount(root); index++)
        {
            DependencyObject child = VisualTreeHelper.GetChild(root, index);
            if (child is T match)
            {
                yield return match;
            }

            foreach (T descendant in FindVisualDescendants<T>(child))
            {
                yield return descendant;
            }
        }
    }

    private static bool StyleChainContains(Style actual, Style expected)
    {
        for (Style candidate = actual; candidate != null; candidate = candidate.BasedOn)
        {
            if (ReferenceEquals(candidate, expected))
            {
                return true;
            }
        }

        return false;
    }

    private sealed class RecordingLoadPlaylistDialogService : IUiDialogService
    {
        private readonly UiFilePickerResult fileResult;

        internal RecordingLoadPlaylistDialogService(UiFilePickerResult fileResult)
        {
            this.fileResult = fileResult;
        }

        internal List<UiFilePickerRequest> FileRequests { get; } = [];

        internal Task<UiFilePickerResult>? FileTask { get; set; }

        internal int MessageRequestCount { get; private set; }

        public Task<UiDialogResult> ShowMessageAsync(
            UiMessageRequest request,
            CancellationToken cancellationToken = default)
        {
            MessageRequestCount++;
            throw new InvalidOperationException("ローカルファイル選択は別のモーダル通知を表示しない。");
        }

        public Task<UiDialogResult> ConfirmAsync(
            UiConfirmationRequest request,
            CancellationToken cancellationToken = default) => throw new NotSupportedException();

        public Task<UiWindowDialogResult<TResult>> ShowWindowAsync<TWindow, TResult>(
            UiWindowDialogRequest<TWindow, TResult> request,
            CancellationToken cancellationToken = default)
            where TWindow : Window => throw new NotSupportedException();

        public Task<UiFilePickerResult> PickFileAsync(
            UiFilePickerRequest request,
            CancellationToken cancellationToken = default)
        {
            FileRequests.Add(request);
            return FileTask ?? Task.FromResult(fileResult);
        }

        public Task<UiFolderPickerResult> PickFolderAsync(
            UiFolderPickerRequest request,
            CancellationToken cancellationToken = default) => throw new NotSupportedException();

        public Task<UiSaveFilePickerResult> PickSaveFileAsync(
            UiSaveFilePickerRequest request,
            CancellationToken cancellationToken = default) => throw new NotSupportedException();

        public Task<UiProgressResult> RunWithProgressAsync(
            UiProgressRequest request,
            Func<UiProgressContext, Task> operation,
            CancellationToken cancellationToken = default) => throw new NotSupportedException();
    }
}
