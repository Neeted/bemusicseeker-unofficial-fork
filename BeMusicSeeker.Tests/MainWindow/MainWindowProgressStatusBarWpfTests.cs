using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Sockets;
using System.Reflection;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Media;
using BeMusicSeeker.Models;
using BeMusicSeeker.Properties;
using BeMusicSeeker.ViewModels;
using BeMusicSeeker.Views;
using BeMusicSeeker.Views.Dialogs;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace BeMusicSeeker.Tests;

[TestClass]
[DoNotParallelize]
public sealed class MainWindowProgressStatusBarWpfTests
{
    [DataTestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public void StartupPostInitializationCompositeTerminal_ReflectsOnUiAndRejectsPreviousStartup(bool beginNewStartup)
    {
        var scheduler = new HoldingTerminalUiScheduler();
        var terminals = new MainWindowProgressStatusBarTerminals(() => { }, () => { }, () => { }, () => { });
        RunConstructorOnlyWithStatusBarTerminals(terminals, (owner, window, host) =>
        {
            StatusBar statusBar = GetNamedElement<StatusBar>(window, "progressStatusBar");
            ItemsControl rows = GetNamedElement<ItemsControl>(window, "progressRows");
            BeginReadyOperableStartup(owner);
            statusBar.ApplyTemplate();
            host.UpdateLayout();
            Assert.IsTrue(owner.ProgressHub.IsStartupBackgroundInitializationActive);
            Assert.IsTrue(statusBar.IsVisible);
            Assert.IsTrue(FindVisualChildren<ProgressBar>(rows)
                .Any(bar => ((OperationProgressRow)bar.DataContext).Key == "startup_background" && bar.IsIndeterminate
                    && bar.IsVisible && bar.ActualWidth > 0 && bar.ActualHeight > 0));

            StartupBackgroundTaskSchedulerOwner background = GetPrivateField<StartupBackgroundTaskSchedulerOwner>(
                owner, "startupBackgroundTaskScheduler");
            background.MarkPostInitializationSchedulingComplete();
            SetPrivateField(owner, "startupInitializationCompleteLogged", true);
            SetPrivateField(owner, "startupPostInitializationWarmupScheduled", true);
            SetPrivateField(owner, "startupPostInitializationWarmupCompleted", true);
            var notificationThreads = new ConcurrentQueue<(string Name, bool IsUi)>();
            owner.ProgressHub.PropertyChanged += (_, args) =>
            {
                if (args.PropertyName is nameof(OperationProgressHubViewModel.Rows) or nameof(OperationProgressHubViewModel.HasRows))
                {
                    notificationThreads.Enqueue((args.PropertyName, host.Dispatcher.CheckAccess()));
                }
            };
            scheduler.HoldNextNormal();
            try
            {
                var terminal = Task.Run(() => InvokePrivate(owner, "TryLogStartupPostInitializationComplete", []));
                TestUiDispatcherHost.AwaitTaskOnDispatcher(terminal, "startup worker composite terminal");
                terminal.GetAwaiter().GetResult();
                Assert.IsTrue(GetPrivateField<bool>(owner, "startupPostInitializationCompletionLogged"));
                Assert.IsTrue(background.IsFullyIdle);
                Assert.AreEqual(1, scheduler.HeldCount);
                Assert.IsTrue(owner.ProgressHub.IsStartupBackgroundInitializationActive,
                    "Worker completion must not wait for presentation or apply its Hub update off the UI thread.");
                Assert.IsTrue(notificationThreads.IsEmpty);

                if (beginNewStartup)
                {
                    // 設定保存の RestartMode.All は Startup の全初期化を再受付し、この ready-operable 境界へ戻る。
                    BeginReadyOperableStartup(owner);
                    host.UpdateLayout();
                }
                OperationProgressRow before = owner.ProgressHub.Rows.Single(row => row.Key == "startup_background");
                notificationThreads.Clear();
                scheduler.ReleaseHeld();
                host.UpdateLayout();
                if (beginNewStartup)
                {
                    Assert.IsTrue(owner.ProgressHub.IsStartupBackgroundInitializationActive);
                    OperationProgressRow after = owner.ProgressHub.Rows.Single(row => row.Key == "startup_background");
                    Assert.AreEqual(before.Value, after.Value);
                    Assert.AreEqual(before.Maximum, after.Maximum);
                    Assert.IsTrue(statusBar.IsVisible);
                    Assert.IsTrue(FindVisualChildren<ProgressBar>(rows)
                        .Any(bar => ((OperationProgressRow)bar.DataContext).Key == "startup_background" && bar.IsIndeterminate
                            && bar.IsVisible && bar.ActualWidth > 0 && bar.ActualHeight > 0));
                    Assert.IsTrue(notificationThreads.IsEmpty);
                }
                else
                {
                    Assert.IsFalse(owner.ProgressHub.IsStartupBackgroundInitializationActive);
                    Assert.IsFalse(owner.ProgressHub.HasRows);
                    Assert.IsFalse(FindVisualChildren<ProgressBar>(rows)
                        .Any(bar => ((OperationProgressRow)bar.DataContext).Key == "startup_background"));
                    Assert.AreEqual(Visibility.Collapsed, statusBar.Visibility);
                    Assert.IsTrue(notificationThreads.Any(item => item.Name == nameof(OperationProgressHubViewModel.Rows)));
                    Assert.IsTrue(notificationThreads.Any(item => item.Name == nameof(OperationProgressHubViewModel.HasRows)));
                    Assert.IsTrue(notificationThreads.All(item => item.IsUi));
                }
            }
            finally
            {
                scheduler.ReleaseHeld();
            }
        }, scheduler);
    }

    [TestMethod]
    public void ProgressStatusBar_RendersConcurrentRowsWithFlexibleLabelWidthAndMeasuredHeight()
    {
        var terminals = new MainWindowProgressStatusBarTerminals(() => { }, () => { }, () => { }, () => { });
        RunConstructorOnlyWithStatusBarTerminals(terminals, (viewModel, window, host) =>
        {
            OperationProgressHubViewModel hub = viewModel.ProgressHub;
            StatusBar statusBar = GetNamedElement<StatusBar>(window, "progressStatusBar");
            Assert.AreSame(hub, statusBar.DataContext);
            Assert.AreEqual(Visibility.Collapsed, statusBar.Visibility);
            hub.StartupProgress.ApplyPresentation(true, new string('あ', 200), "phase", 2, 13);
            host.UpdateLayout();
            double oneRowHeight = statusBar.ActualHeight;
            hub.UpdatePendingEstimateQueueStatus(new BeMusicSeeker.Models.PendingInstallEstimateQueueStatusSnapshot
            {
                IsActive = true,
                CurrentPackageCount = 8,
                CompletedPackageCount = 3,
                CurrentDisplayName = new string('長', 200)
            });
            hub.BeginBackgroundProgressGeneration(1);
            hub.UpdateBackgroundTaskProgress(new("chart_info_hydration", 1, 1, false, true));
            statusBar.ApplyTemplate();
            host.UpdateLayout();
            Assert.AreEqual(Visibility.Visible, statusBar.Visibility);
            Assert.IsTrue(double.IsNaN(statusBar.Height));
            Assert.IsTrue(statusBar.ActualHeight >= oneRowHeight * 2);
            ProgressBar[] bars = FindVisualChildren<ProgressBar>(statusBar).ToArray();
            Assert.AreEqual(3, bars.Length);
            Assert.AreEqual(13d, bars.Single(bar => ((OperationProgressRow)bar.DataContext).Key == "startup").Maximum);
            TextBlock[] labels = FindVisualChildren<TextBlock>(statusBar)
                .Where(text => text.DataContext is OperationProgressRow && text.Inlines.Count > 0).ToArray();
            Assert.AreEqual(3, labels.Length);
            Assert.IsTrue(labels.All(text => text.ActualWidth > 200));
            TextBlock child = labels.Single(text => ((OperationProgressRow)text.DataContext).IsChild);
            var childGrid = (Grid)VisualTreeHelper.GetParent(child);
            Assert.AreEqual(20d, childGrid.Margin.Left);
            StringAssert.Contains((string)childGrid.ToolTip, ((OperationProgressRow)child.DataContext).Label);
            TextBlock longLabel = labels.Single(text => ((OperationProgressRow)text.DataContext).Key == "startup");
            StringAssert.Contains((string)((Grid)VisualTreeHelper.GetParent(longLabel)).ToolTip, new string('あ', 200));
            double wideLabelWidth = labels[0].ActualWidth;
            host.Width = 800;
            host.UpdateLayout();
            Assert.IsTrue(labels[0].ActualWidth < wideLabelWidth);
            Assert.IsTrue(labels.All(text => text.ActualWidth > 0));
            Assert.IsTrue(window.MinHeight >= statusBar.ActualHeight);
        });
    }

    [TestMethod]
    public void ProgressStatusBarButtons_InvokeEachRowsOwnerExactlyOnce()
    {
        var calls = new List<string>();
        var terminals = new MainWindowProgressStatusBarTerminals(
            () => calls.Add("install"), () => calls.Add("maintenance"),
            () => calls.Add("retry"), () => calls.Add("url"));
        RunConstructorOnlyWithStatusBarTerminals(terminals, (viewModel, window, host) =>
        {
            viewModel.ProgressHub.StartupProgress.ApplyPresentation(true, "startup", "", 1, 13);
            ItemsControl rows = GetNamedElement<ItemsControl>(window, "progressRows");
            rows.ItemsSource = new[]
            {
                new OperationProgressRow("install", "install", "", Action: OperationProgressAction.CancelInstall),
                new OperationProgressRow("url", "url", "", Action: OperationProgressAction.CancelUrlDownload),
                new OperationProgressRow("maintenance", "maintenance", "", Action: OperationProgressAction.CancelMaintenance),
                new OperationProgressRow("lr2", "lr2", "", Action: OperationProgressAction.RetryLr2)
            };
            rows.ApplyTemplate();
            host.UpdateLayout();
            foreach (Button button in FindVisualChildren<Button>(rows).Where(button => button.Visibility == Visibility.Visible))
            {
                button.RaiseEvent(new RoutedEventArgs(Button.ClickEvent, button));
            }
        });
        CollectionAssert.AreEqual(new[] { "install", "url", "maintenance", "retry" }, calls);
    }

    [TestMethod]
    public void ProgressStatusBarCoreFactory_UsesRowSpecificCancellationWhileAcquisitionRuns()
    {
        var calls = new List<string>();
        var terminals = MainWindowProgressStatusBarTerminals.CreateCore(
            () => calls.Add("url"), () => calls.Add("install"),
            () => calls.Add("maintenance"), () => calls.Add("retry"));
        terminals.Invoke(OperationProgressAction.CancelInstall);
        terminals.Invoke(OperationProgressAction.CancelUrlDownload);
        terminals.Invoke(OperationProgressAction.CancelMaintenance);
        terminals.Invoke(OperationProgressAction.RetryLr2);
        CollectionAssert.AreEqual(new[] { "install", "url", "maintenance", "retry" }, calls);
    }

    [TestMethod]
    public async Task DefaultFactory_MapsEveryOwnerRouteAndKeepsCancellationIndependent()
    {
        var settings = new Settings { OperationModeLR2DB = false };
        using PlaylistWorkspaceTestPorts.OwnedPlaylistStore ownedPlaylistStore =
            PlaylistWorkspaceTestPorts.CreateOwnedPlaylistStore();
        var composition = new ApplicationComposition(
            settingsEditSession: new NoOpSettingsEditSession(settings),
            playlistWorkspaceDialogService: new AcceptedStatusBarTestDialogService(),
            uiScheduler: new TestUiScheduler(() => TestUiDispatcherHost.Dispatcher),
            applicationLifetime: TestApplicationContext.CreateLifetime(),
            cultureCatalog: TestApplicationContext.CreateCultureCatalog());
        MainWindowViewModel viewModel = composition.CreateMainWindowViewModelForTest();
        TestBmsLibrary library = MainWindowViewModelTestFactory.CreateLibrary(
            ownedPlaylistStore.SongDbPath,
            settings);
        IStartupLibraryApplicationPort applicationPort = viewModel;
        applicationPort.AttachStartupLibrary(library);
        applicationPort.AttachStartupServices(
            new StartupLibraryServices(
                StartupLibraryConstructionTestSupport.CreateProfile(
                    Path.GetDirectoryName(ownedPlaylistStore.SongDbPath)!,
                    ownedPlaylistStore.SongDbPath),
                library,
                ownedPlaylistStore.Store));
        var terminals = MainWindowProgressStatusBarTerminals.Create(viewModel);
        int packageCancelRequests = 0;
        int maintenanceCancelRequests = 0;
        int lr2RetryRequests = 0;
        int playlistCancelableSnapshots = 0;
        int playlistCanceledSnapshots = 0;
        viewModel.PackageInstallWorkflow.CancelAllRequested += () => packageCancelRequests++;
        viewModel.MaintenanceRescanWorkflow.CancellationRequested += () => maintenanceCancelRequests++;
        viewModel.Lr2SongDbSyncWorkflow.StatusBarRetryRequested += () => lr2RetryRequests++;
        viewModel.PlaylistWorkspace.PlaylistUrlDownloadStatusChanged += (_, snapshot) =>
        {
            if (snapshot.IsActive && snapshot.CanCancel)
            {
                Interlocked.Increment(ref playlistCancelableSnapshots);
            }
            else if (snapshot.IsActive)
            {
                Interlocked.Increment(ref playlistCanceledSnapshots);
            }
        };

        try
        {
            Assert.IsFalse(viewModel.PlaylistWorkspace.IsPlaylistUrlDownloadRunning);
            terminals.CancelInstallPipeline();
            Assert.AreEqual(1, packageCancelRequests);

            terminals.CancelMaintenanceRescan();
            Assert.AreEqual(1, maintenanceCancelRequests);
            Assert.AreEqual(0, lr2RetryRequests);

            terminals.RetryLr2Sync();
            Assert.AreEqual(1, maintenanceCancelRequests);
            Assert.AreEqual(1, lr2RetryRequests);

            await using var server = new BlockingLoopbackHttpServer();
            Task download = viewModel.PlaylistWorkspace.RunPlaylistUrlBatchAsync(
                [server.DownloadUri],
                isDiffUrl: false);
            await server.RequestAccepted.Task.WaitAsync(TimeSpan.FromSeconds(5));
            Assert.IsTrue(viewModel.PlaylistWorkspace.IsPlaylistUrlDownloadRunning);
            Assert.AreEqual(1, Volatile.Read(ref playlistCancelableSnapshots));

            terminals.Invoke(OperationProgressAction.CancelInstall);
            Assert.AreEqual(2, packageCancelRequests);
            Assert.IsTrue(viewModel.PlaylistWorkspace.IsPlaylistUrlDownloadRunning);
            terminals.Invoke(OperationProgressAction.CancelUrlDownload);

            await download.WaitAsync(TimeSpan.FromSeconds(5));
            Assert.IsFalse(viewModel.PlaylistWorkspace.IsPlaylistUrlDownloadRunning);
            Assert.AreEqual(2, packageCancelRequests);
            Assert.AreEqual(1, Volatile.Read(ref playlistCanceledSnapshots));
        }
        finally
        {
            viewModel.SettingDialog.Dispose();
        }
    }

    private static void RunConstructorOnlyWithStatusBarTerminals(
        MainWindowProgressStatusBarTerminals terminals,
        Action<MainWindowViewModel, MainWindow, Window> test,
        IUiScheduler? uiScheduler = null)
    {
        TestUiDispatcherHost.RunWindowTest(scope =>
        {
            MainWindowViewModel? viewModel = null;
            MainWindow? window = null;
            var windowClosed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var lifetime = new MainWindowPresentationTestHarness.PresentationApplicationLifetime();
            bool hadPreviousViewModelResource = Application.Current.Resources.Contains("vm");
            object? previousViewModelResource = hadPreviousViewModelResource
                ? Application.Current.Resources["vm"]
                : null;
            try
            {
                viewModel = new ApplicationComposition(
                    settingsEditSession: new NoOpSettingsEditSession(new Settings()),
                    uiScheduler: uiScheduler ?? new TestUiScheduler(() => TestUiDispatcherHost.Dispatcher),
                    applicationLifetime: lifetime,
                    cultureCatalog: TestApplicationContext.CreateCultureCatalog())
                    .CreateMainWindowViewModelForTest();
                viewModel.StartupUpdateWorkflow.NotifyClosing();
                viewModel.ProgressHub.StartupProgress.SetStartupUiInteractionBlocked(false);
                Application.Current.Resources["vm"] = viewModel;
                window = new MainWindow(
                    viewModel,
                    settingsWindowCreated: null,
                    libraryReloadMenuTerminal: null,
                    regularLibraryTreeTerminal: null,
                    maintenanceTreeTerminal: null,
                    installTreeTerminal: null,
                    zeroNoteRecheckTerminal: null,
                    columnResetTerminal: null,
                    rootFolderUnregisterTerminal: null,
                    folderAutoRenameTerminal: null,
                    duplicateMaintenanceTerminal: null,
                    maintenanceRescanTerminal: null,
                    packageCatalogTerminal: null,
                    pendingInstallEstimationTerminal: null,
                    pendingInstallationTerminal: null,
                    installedLocationRepairTerminal: null,
                    pendingBulkMaintenanceTerminal: null,
                    mainChartCellEditTerminal: null,
                    selectedChartContextMenuTerminals: null,
                    playbackTerminal: null,
                    playlistWorkspaceTerminals: null,
                    progressStatusBarTerminals: terminals);
                window.Closed += (_, _) => windowClosed.TrySetResult();
                TestUiDispatcherHost.Drain();
                object content = window.Content;
                window.Content = null;
                var host = new Window { Width = 1100, Height = 700, Resources = window.Resources, Content = content, DataContext = viewModel };
                try
                {
                    scope.ShowAndWaitForContentRendered(host);
                    test(viewModel, window, host);
                }
                finally
                {
                    host.Close();
                }
            }
            finally
            {
                try
                {
                    if (window != null && !windowClosed.Task.IsCompleted)
                    {
                        window.Close();
                        TestUiDispatcherHost.AwaitTaskOnDispatcher(
                            lifetime.ShutdownRequested.Task,
                            "MainWindowProgressStatusBarWpfTests.terminal-shutdown");
                        window.Close();
                        TestUiDispatcherHost.AwaitTaskOnDispatcher(
                            windowClosed.Task,
                            "MainWindowProgressStatusBarWpfTests.window-closed");
                    }
                }
                finally
                {
                    try
                    {
                        viewModel?.SettingDialog.Dispose();
                    }
                    finally
                    {
                        if (hadPreviousViewModelResource)
                        {
                            Application.Current.Resources["vm"] = previousViewModelResource;
                        }
                        else
                        {
                            Application.Current.Resources.Remove("vm");
                        }
                    }
                }
            }
        });
    }

    private static void BeginReadyOperableStartup(MainWindowViewModel owner)
    {
        StartupProgressWorkflowOwner progress = owner.ProgressHub.StartupProgress;
        long token = progress.StartStartupProgressOperation(StartupProgressOperationKind.Startup);
        SetPrivateField(owner, "startupReadyUiReached", true);
        SetPrivateField(owner, "startupReadyOperableStopwatch", Stopwatch.StartNew());
        InvokePrivate(owner, "TryLogStartupReadyOperable", [token]);
        progress.ApplyPresentation(false, null, null, 0, 1);
        SetPrivateField(owner, "startupCompletionContinuationToken", token);
    }

    private static T GetPrivateField<T>(object target, string name) => (T)target.GetType()
        .GetField(name, BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(target)!;

    private static void SetPrivateField(object target, string name, object value) => target.GetType()
        .GetField(name, BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(target, value);

    private static void InvokePrivate(object target, string name, object[] arguments) => target.GetType()
        .GetMethods(BindingFlags.Instance | BindingFlags.NonPublic)
        .Single(method => method.Name == name && method.GetParameters().Length == arguments.Length)
        .Invoke(target, arguments);

    private sealed class HoldingTerminalUiScheduler : IUiScheduler
    {
        private readonly TestUiScheduler inner = new(() => TestUiDispatcherHost.Dispatcher);
        private readonly ConcurrentQueue<Action> held = new();
        private int holdNextNormal;
        internal int HeldCount => held.Count;
        internal void HoldNextNormal() => Interlocked.Exchange(ref holdNextNormal, 1);
        internal void ReleaseHeld()
        {
            Interlocked.Exchange(ref holdNextNormal, 0);
            while (held.TryDequeue(out Action? action))
            {
                action();
            }
        }
        public bool IsAvailable => inner.IsAvailable;
        public bool CanExecuteInline => inner.CanExecuteInline;
        public bool CheckAccess() => inner.CheckAccess();
        public IUiScheduledOperation Schedule(Action action, UiSchedulePriority priority = UiSchedulePriority.Normal)
        {
            if (priority == UiSchedulePriority.Normal && Interlocked.Exchange(ref holdNextNormal, 0) == 1)
            {
                var operation = new RegularChartListOwnerTestSupport.ActionQueueUiScheduledOperation();
                held.Enqueue(() => operation.Execute(action));
                return operation;
            }
            return inner.Schedule(action, priority);
        }
        public void Invoke(Action action, UiSchedulePriority priority = UiSchedulePriority.Normal) => inner.Invoke(action, priority);
        public T Invoke<T>(Func<T> action, UiSchedulePriority priority = UiSchedulePriority.Normal) => inner.Invoke(action, priority);
        public Task InvokeAsync(Action action, UiSchedulePriority priority = UiSchedulePriority.Normal) => inner.InvokeAsync(action, priority);
        public Task InvokeAsync(Func<Task> action, UiSchedulePriority priority = UiSchedulePriority.Normal) => inner.InvokeAsync(action, priority);
    }

    private static IReadOnlyList<T> FindVisualChildren<T>(DependencyObject root)
        where T : DependencyObject
    {
        var result = new List<T>();
        Visit(root, result, new HashSet<DependencyObject>());
        return result;
    }

    private static void Visit<T>(DependencyObject current, List<T> result, HashSet<DependencyObject> visited)
        where T : DependencyObject
    {
        if (current == null || !visited.Add(current))
        {
            return;
        }
        if (current is T match)
        {
            result.Add(match);
        }
        if (current is Visual || current is System.Windows.Media.Media3D.Visual3D)
        {
            for (int index = 0; index < VisualTreeHelper.GetChildrenCount(current); index++)
            {
                Visit(VisualTreeHelper.GetChild(current, index), result, visited);
            }
        }
        foreach (object childValue in LogicalTreeHelper.GetChildren(current))
        {
            if (childValue is DependencyObject child)
            {
                Visit(child, result, visited);
            }
        }
    }

    private static T GetNamedElement<T>(MainWindow window, string name)
        where T : class
    {
        object? element = window.FindName(name);
        Assert.IsInstanceOfType(element, typeof(T), name);
        return (T)element!;
    }

    private sealed class AcceptedStatusBarTestDialogService : IUiDialogService
    {
        public Task<UiDialogResult> ShowMessageAsync(
            UiMessageRequest request,
            CancellationToken cancellationToken = default) =>
            Task.FromResult(UiDialogResult.FromMessageBoxResult(MessageBoxResult.OK));

        public Task<UiDialogResult> ConfirmAsync(
            UiConfirmationRequest request,
            CancellationToken cancellationToken = default) =>
            Task.FromResult(UiDialogResult.FromMessageBoxResult(MessageBoxResult.OK));

        public Task<UiWindowDialogResult<TResult>> ShowWindowAsync<TWindow, TResult>(
            UiWindowDialogRequest<TWindow, TResult> request,
            CancellationToken cancellationToken = default)
            where TWindow : Window => throw new NotSupportedException();

        public Task<UiFilePickerResult> PickFileAsync(
            UiFilePickerRequest request,
            CancellationToken cancellationToken = default) => throw new NotSupportedException();

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

    private sealed class BlockingLoopbackHttpServer : IAsyncDisposable
    {
        private readonly TcpListener listener = new(IPAddress.Loopback, 0);
        private readonly CancellationTokenSource lifetime = new();
        private readonly Task serverTask;

        internal BlockingLoopbackHttpServer()
        {
            listener.Start();
            int port = ((IPEndPoint)listener.LocalEndpoint).Port;
            DownloadUri = new Uri($"http://127.0.0.1:{port}/status-bar-owner.zip");
            serverTask = HoldAcceptedRequestAsync();
        }

        internal Uri DownloadUri { get; }

        internal TaskCompletionSource<object?> RequestAccepted { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public async ValueTask DisposeAsync()
        {
            lifetime.Cancel();
            listener.Stop();
            try
            {
                await serverTask.ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (lifetime.IsCancellationRequested)
            {
            }
            catch (ObjectDisposedException) when (lifetime.IsCancellationRequested)
            {
            }
            catch (SocketException) when (lifetime.IsCancellationRequested)
            {
            }
            finally
            {
                lifetime.Dispose();
            }
        }

        private async Task HoldAcceptedRequestAsync()
        {
            using TcpClient client = await listener.AcceptTcpClientAsync(lifetime.Token).ConfigureAwait(false);
            NetworkStream stream = client.GetStream();
            byte[] requestBuffer = new byte[4096];
            int requestLength = 0;
            while (!ContainsHeaderTerminator(requestBuffer, requestLength))
            {
                int read = await stream.ReadAsync(
                    requestBuffer.AsMemory(requestLength, requestBuffer.Length - requestLength),
                    lifetime.Token).ConfigureAwait(false);
                if (read == 0)
                {
                    throw new IOException("The playlist download client closed before sending an HTTP request.");
                }
                requestLength += read;
                if (requestLength == requestBuffer.Length && !ContainsHeaderTerminator(requestBuffer, requestLength))
                {
                    throw new IOException("The playlist download request headers exceeded the test server limit.");
                }
            }

            byte[] responseHeaders = Encoding.ASCII.GetBytes(
                "HTTP/1.1 200 OK\r\n"
                + "Content-Type: application/zip\r\n"
                + "Content-Disposition: attachment; filename=status-bar-owner.zip\r\n"
                + "Content-Length: 1048576\r\n"
                + "Connection: close\r\n\r\n");
            await stream.WriteAsync(responseHeaders, lifetime.Token).ConfigureAwait(false);
            await stream.FlushAsync(lifetime.Token).ConfigureAwait(false);
            RequestAccepted.TrySetResult(null);
            await Task.Delay(Timeout.InfiniteTimeSpan, lifetime.Token).ConfigureAwait(false);
        }

        private static bool ContainsHeaderTerminator(byte[] buffer, int length)
        {
            for (int index = 3; index < length; index++)
            {
                if (buffer[index - 3] == '\r'
                    && buffer[index - 2] == '\n'
                    && buffer[index - 1] == '\r'
                    && buffer[index] == '\n')
                {
                    return true;
                }
            }
            return false;
        }
    }
}
