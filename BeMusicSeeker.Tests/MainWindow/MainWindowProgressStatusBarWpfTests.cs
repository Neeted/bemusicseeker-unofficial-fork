using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Media;
using BeMusicSeeker.Models;
using BeMusicSeeker.Models.BmsLibraryInternal;
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
    /// <summary>実起動の後続Taskを保持し、実XAML Bindingが同じ要求の開始と終端を表示します。</summary>
    [TestMethod]
    public void StartupPostInitialization_ActualWorkReflectsThroughCompiledStatusBarBinding()
    {
        TestUiDispatcherHost.RunWindowTest(scope =>
        {
            var lifetime = new MainWindowPresentationTestHarness.PresentationApplicationLifetime();
            using var fixture = new MainWindowViewModelStartupProgressTests.CompletionFixture(lifetime: lifetime);
            fixture.Owner.StartupUpdateWorkflow.NotifyClosing();
            using var ownership = new MainWindowTestLifetime(fixture.Owner, lifetime.ShutdownRequested.Task);
            var terminals = new MainWindowProgressStatusBarTerminals(() => { }, () => { }, () => { });
            MainWindow window = ownership.CreateWindow(() => new MainWindow(fixture.Owner, settingsWindowCreated: null, libraryReloadMenuTerminal: null,
                regularLibraryTreeTerminal: null, maintenanceTreeTerminal: null, installTreeTerminal: null,
                zeroNoteRecheckTerminal: null, columnResetTerminal: null, rootFolderUnregisterTerminal: null,
                folderAutoRenameTerminal: null, duplicateMaintenanceTerminal: null, maintenanceRescanTerminal: null,
                packageCatalogTerminal: null, pendingInstallEstimationTerminal: null, pendingInstallationTerminal: null,
                installedLocationRepairTerminal: null, pendingBulkMaintenanceTerminal: null, mainChartCellEditTerminal: null,
                progressStatusBarTerminals: terminals));
            object content = window.Content;
            window.Content = null;
            var host = new Window { Width = 1100, Height = 700, Resources = window.Resources, Content = content, DataContext = fixture.Owner };
            try
            {
                scope.ShowAndWaitForContentRendered(host);
                Task<bool> initialization = fixture.Start();
                TestUiDispatcherHost.AwaitTaskOnDispatcher(initialization, "binding-real-startup");
                Assert.IsTrue(initialization.GetAwaiter().GetResult());
                TestUiDispatcherHost.AwaitTaskOnDispatcher(
                    TestUiDispatcherHost.AwaitNotificationAsync(fixture.OptionalEntered.Task, fixture.OptionalWork, "binding-optional-arrival"), "binding-optional-arrival");
                StatusBar statusBar = GetNamedElement<StatusBar>(window, "progressStatusBar");
                ItemsControl rows = GetNamedElement<ItemsControl>(window, "progressRows");
                host.UpdateLayout();
                Assert.IsTrue(fixture.Owner.ProgressHub.IsStartupBackgroundInitializationActive);
                Assert.IsTrue(statusBar.IsVisible);
                Assert.IsTrue(FindVisualChildren<ProgressBar>(rows).Any(bar =>
                    ((OperationProgressRow)bar.DataContext).Key == "startup_background" && bar.IsIndeterminate && bar.IsVisible));
                fixture.OptionalRelease.TrySetResult();
                TestUiDispatcherHost.AwaitTaskOnDispatcher(fixture.OptionalWork, "binding-optional-terminal");
                TestUiDispatcherHost.AwaitTaskOnDispatcher(fixture.WaitForBackgroundTerminalAsync(), "binding-scheduler-terminal");
                TestUiDispatcherHost.ProcessQueuedPresentation();
                host.UpdateLayout();
                Assert.IsFalse(fixture.Owner.ProgressHub.IsStartupBackgroundInitializationActive);
                Assert.IsFalse(FindVisualChildren<ProgressBar>(rows).Any(bar => ((OperationProgressRow)bar.DataContext).Key == "startup_background"));
            }
            finally
            {
                fixture.Ui.Release.TrySetResult();
                fixture.OptionalRelease.TrySetResult();
                host.Close();
            }
        });
    }

    [TestMethod]
    public void ProgressStatusBar_RendersConcurrentRowsWithFlexibleLabelWidthAndMeasuredHeight()
    {
        var terminals = new MainWindowProgressStatusBarTerminals(() => { }, () => { }, () => { });
        RunConstructorOnlyWithStatusBarTerminals(terminals, (viewModel, window, host) =>
        {
            OperationProgressHubViewModel hub = viewModel.ProgressHub;
            StatusBar statusBar = GetNamedElement<StatusBar>(window, "progressStatusBar");
            Assert.AreSame(hub, statusBar.DataContext);
            Assert.AreEqual(Visibility.Collapsed, statusBar.Visibility);
            long token = hub.StartupProgress.StartStartupProgressOperation(StartupProgressOperationKind.Startup);
            var request = new OperationProgressRequest(1, token, "chart_info_hydration", 1);
            hub.StartupProgress.TrackStartupProgressChartInfoHydrationRequested(1, request);
            hub.StartupProgress.ApplyPresentation(true, new string('あ', 200), "phase", 2, 13);
            host.UpdateLayout();
            double oneRowHeight = statusBar.ActualHeight;
            hub.UpdateInstallEstimationProgress(new BeMusicSeeker.Models.InstallEstimationProgressSnapshot
            {
                IsActive = true,
                TotalWorkCount = 8,
                CompletedWorkCount = 3,
                CurrentDisplayName = new string('長', 200)
            });
            hub.BeginBackgroundProgressGeneration(1);
            hub.UpdateBackgroundTaskProgress(new("chart_info_hydration", 1, 1, false, true, request));
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
            () => calls.Add("url"));
        RunConstructorOnlyWithStatusBarTerminals(terminals, (viewModel, window, host) =>
        {
            viewModel.ProgressHub.StartupProgress.ApplyPresentation(true, "startup", "", 1, 13);
            ItemsControl rows = GetNamedElement<ItemsControl>(window, "progressRows");
            rows.ItemsSource = new[]
            {
                new OperationProgressRow("install", "install", "", Action: OperationProgressAction.CancelInstall),
                new OperationProgressRow("url", "url", "", Action: OperationProgressAction.CancelUrlDownload),
                new OperationProgressRow("maintenance", "maintenance", "", Action: OperationProgressAction.CancelMaintenance)
            };
            rows.ApplyTemplate();
            host.UpdateLayout();
            foreach (Button button in FindVisualChildren<Button>(rows).Where(button => button.Visibility == Visibility.Visible))
            {
                button.RaiseEvent(new RoutedEventArgs(Button.ClickEvent, button));
            }
        });
        CollectionAssert.AreEqual(new[] { "install", "url", "maintenance" }, calls);
    }

    [TestMethod]
    public void ProgressStatusBarCoreFactory_UsesRowSpecificCancellationWhileAcquisitionRuns()
    {
        var calls = new List<string>();
        var terminals = MainWindowProgressStatusBarTerminals.CreateCore(
            () => calls.Add("url"), () => calls.Add("install"),
            () => calls.Add("maintenance"));
        terminals.Invoke(OperationProgressAction.CancelInstall);
        terminals.Invoke(OperationProgressAction.CancelUrlDownload);
        terminals.Invoke(OperationProgressAction.CancelMaintenance);
        CollectionAssert.AreEqual(new[] { "install", "url", "maintenance" }, calls);
    }

    [TestMethod]
    public async Task DefaultFactory_MapsEveryOwnerRouteAndKeepsCancellationIndependent()
    {
        Settings settings = MainWindowViewModelTestFactory.CreateIsolatedSettings(values =>
            {
                values.OperationModeLR2DB = false;
            });
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
            settings,
            viewModel);
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
        int playlistCancelableSnapshots = 0;
        int playlistCanceledSnapshots = 0;
        viewModel.PackageInstallWorkflow.CancelAllRequested += () => packageCancelRequests++;
        viewModel.MaintenanceRescanWorkflow.CancellationRequested += () => maintenanceCancelRequests++;
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

            Assert.AreEqual(1, maintenanceCancelRequests);

            await using var server = new BlockingLoopbackHttpServer();
            Task download = viewModel.PlaylistWorkspace.RunPlaylistUrlBatchAsync(
                [server.DownloadUri],
                isDiffUrl: false);
            await server.RequestAccepted.Task;
            Assert.IsTrue(viewModel.PlaylistWorkspace.IsPlaylistUrlDownloadRunning);
            Assert.AreEqual(1, Volatile.Read(ref playlistCancelableSnapshots));

            terminals.Invoke(OperationProgressAction.CancelInstall);
            Assert.AreEqual(2, packageCancelRequests);
            Assert.IsTrue(viewModel.PlaylistWorkspace.IsPlaylistUrlDownloadRunning);
            terminals.Invoke(OperationProgressAction.CancelUrlDownload);

            await download;
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
            var lifetime = new MainWindowPresentationTestHarness.PresentationApplicationLifetime();
            MainWindowViewModel viewModel = new ApplicationComposition(
                settingsEditSession: new NoOpSettingsEditSession(MainWindowViewModelTestFactory.CreateIsolatedSettings()),
                uiScheduler: uiScheduler ?? new TestUiScheduler(() => TestUiDispatcherHost.Dispatcher),
                applicationLifetime: lifetime,
                cultureCatalog: TestApplicationContext.CreateCultureCatalog())
                .CreateMainWindowViewModelForTest();
            var ownership = new MainWindowTestLifetime(viewModel, lifetime.ShutdownRequested.Task);
            ownership.Run(() =>
            {
                MainWindowPresentationTestHarness.PrepareConstructorOnlyShell(viewModel);
                viewModel.StartupUpdateWorkflow.NotifyClosing();
                viewModel.ProgressHub.StartupProgress.SetStartupUiInteractionBlocked(false);
                MainWindow window = ownership.CreateWindow(() => new MainWindow(
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
                    progressStatusBarTerminals: terminals));
                TestUiDispatcherHost.ProcessQueuedPresentation();
                object content = window.Content;
                window.Content = null;
                var host = new Window { Width = 1100, Height = 700, Resources = window.Resources, Content = content, DataContext = viewModel };
                try
                {
                    scope.ShowAndWaitForContentRendered(host);
                    test(viewModel, window, host);
                }
                finally { host.Close(); }
            });
        });
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
