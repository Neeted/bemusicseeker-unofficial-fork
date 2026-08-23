using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
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
