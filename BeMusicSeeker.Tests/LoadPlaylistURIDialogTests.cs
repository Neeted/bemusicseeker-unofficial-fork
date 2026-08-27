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

[TestClass]
[DoNotParallelize]
public sealed class LoadPlaylistURIDialogTests
{
    [TestMethod]
    public void AppendUriInputLine_AppendsToEndWithNewLine()
    {
        string appended = LoadPlaylistURIDialog.AppendUriInputLine("https://example.com/first.json", "  file:///C:/tables/second.json  ");

        Assert.AreEqual("https://example.com/first.json" + Environment.NewLine + "file:///C:/tables/second.json", appended);
        Assert.AreEqual("file:///C:/tables/first.json", LoadPlaylistURIDialog.AppendUriInputLine(string.Empty, "file:///C:/tables/first.json"));
        Assert.AreEqual("existing", LoadPlaylistURIDialog.AppendUriInputLine("existing", " "));
    }

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
                TextBox input = (TextBox)dialog.FindName("textBoxURIInput");
                Border content = FindDialogContentBorder(dialog);
                Button[] footerButtons = FindVisualDescendants<Button>(dialog)
                    .Where(button => button.TemplatedParent == null)
                    .Where(button => AutomationProperties.GetAutomationId(button).StartsWith(
                        "LoadPlaylistUri",
                        StringComparison.Ordinal))
                    .ToArray();
                Assert.AreEqual(3, footerButtons.Length);

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
                        $"URI input bounds {inputBounds} must remain above the footer (top={footerTop}) at font size {fontSize}.");
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

    [TestMethod]
    public void OpenLocalFile_UsesInjectedJsonPickerAndAppendsAcceptedPathWithoutShowingModal()
    {
        TestUiDispatcherHost.RunWindowTest(windowTest =>
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
                Assert.AreEqual("Jsonファイル(*.json)|*.json", dialogs.FileRequests[0].Filter);
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

    [DataTestMethod]
    [DataRow((int)UiDialogStatus.CancelledByUser)]
    [DataRow((int)UiDialogStatus.ClosedByUser)]
    public void OpenLocalFile_CancelOrClosedLeavesExistingInputUnchanged(int statusValue)
    {
        TestUiDispatcherHost.RunWindowTest(windowTest =>
        {
            UiDialogStatus status = (UiDialogStatus)statusValue;
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

    [TestMethod]
    public void OpenLocalFile_FailedPickerPropagatesCauseWithoutMutatingInputOrShowingModal()
    {
        TestUiDispatcherHost.RunWindowTest(windowTest =>
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

    [TestMethod]
    public void OpenLocalFileAsync_AwaitsPickerWithoutBlockingDispatcherAndAppliesCompletion()
    {
        TestUiDispatcherHost.RunWindowTest(windowTest =>
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
            SynchronizationContext previousContext = SynchronizationContext.Current;
            try
            {
                Materialize(host);
                var input = (TextBox)dialog.FindName("textBoxURIInput");
                input.Text = "existing input";
                SynchronizationContext.SetSynchronizationContext(new DispatcherSynchronizationContext(host.Dispatcher));

                Task route = dialog.HandleOpenLocalFileAsync();

                Assert.IsFalse(route.IsCompleted);
                bool dispatcherWorkCompleted = false;
                host.Dispatcher.BeginInvoke(
                    DispatcherPriority.Background,
                    new Action(() => dispatcherWorkCompleted = true));
                TestUiDispatcherHost.Drain();
                Assert.IsTrue(dispatcherWorkCompleted);

                completion.TrySetResult(new UiFilePickerResult(
                    UiDialogStatus.Accepted,
                    [@"C:\tables\pending.json"]));
                TestUiDispatcherHost.Drain();
                route.GetAwaiter().GetResult();

                Assert.AreEqual(
                    "existing input" + Environment.NewLine + @"C:\tables\pending.json",
                    input.Text);
            }
            finally
            {
                try
                {
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
        TestUiDispatcherHost.Drain();
    }

    private static Border FindDialogContentBorder(FrameworkElement root)
    {
        Border[] candidates = FindVisualDescendants<Border>(root)
            .Where(border => border.TryFindResource("App.Canonical.DialogContentStyle") is Style contentStyle
                && StyleChainContains(border.Style, contentStyle))
            .ToArray();
        Assert.AreEqual(1, candidates.Length, "The URI overlay must have one canonical content surface.");
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
            $"{descendant.Name} bounds {bounds} exceed content bounds {ancestorBounds}.");
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

        internal Task<UiFilePickerResult> FileTask { get; set; }

        internal int MessageRequestCount { get; private set; }

        public Task<UiDialogResult> ShowMessageAsync(
            UiMessageRequest request,
            CancellationToken cancellationToken = default)
        {
            MessageRequestCount++;
            throw new InvalidOperationException("The local file route must not show a modal message.");
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
