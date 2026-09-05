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
using System.Windows.Data;
using System.Windows.Interop;
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
    [TestMethod]
    public void ProgressStatusBar_BindsExactHubAndEnumeratesOwnerTriggers()
    {
        MainWindowPackageMaintenanceTestHarness.RunConstructorOnly(
            new Settings(),
            (viewModel, window) =>
            {
                using HwndSource visualHost = CreateVisualHost(window, "MainWindowProgressStatusBarBindings");
                StatusBar statusBar = GetNamedElement<StatusBar>(window, "progressStatusBar");
                Assert.AreSame(viewModel.ProgressHub, statusBar.DataContext);

                CollectionAssert.IsSubsetOf(
                    new[]
                    {
                        "StartupProgress.IsActive",
                        "IsInstallPipelineStatusActive",
                        "IsPlaylistSyncProgressActive",
                        "IsMaintenanceRescanProgressActive",
                        "IsFolderAutoRenameProgressActive",
                        "IsLr2SongDbSyncStatusActive",
                        "IsStartupBackgroundInitializationActive"
                    },
                    GetTriggerBindingPaths(statusBar.Style));

                var ownerStyles = new Dictionary<string, string[]>
                {
                    ["styleStartupProgressStatusBarItem"] = new[] { "StartupProgress.IsActive" },
                    ["styleInstallPipelineStatusBarItem"] = new[] { "IsInstallPipelineStatusActive" },
                    ["stylePlaylistSyncStatusBarItem"] = new[] { "IsPlaylistSyncProgressActive" },
                    ["styleMaintenanceRescanStatusBarItem"] = new[] { "IsMaintenanceRescanProgressActive" },
                    ["styleFolderAutoRenameStatusBarItem"] = new[] { "IsFolderAutoRenameProgressActive" },
                    ["styleStartupBackgroundInitializationStatusBarItem"] = new[] { "IsStartupBackgroundInitializationActive" },
                    ["styleLr2SongDbSyncStatusBarItem"] = new[] { "IsLr2SongDbSyncStatusActive" },
                    ["styleLr2SongDbSyncRetryStatusBarItem"] = new[] { "IsLr2SongDbSyncRetryVisible" },
                    ["styleLr2SongDbSyncProgressStatusBarItem"] = new[] { "IsLr2SongDbSyncStatusProgressVisible" },
                    ["styleInstallPipelineCancelButton"] = new[] { "InstallPipelineCanCancel" },
                    ["styleMaintenanceRescanCancelButton"] = new[] { "MaintenanceRescanCanCancel" }
                };
                foreach ((string key, string[] paths) in ownerStyles)
                {
                    Assert.IsNotNull(statusBar.Resources[key], key);
                    CollectionAssert.AreEquivalent(paths, GetTriggerBindingPaths((Style)statusBar.Resources[key]));
                }

                Assert.IsNull(statusBar.Resources["styleLr2SongDbSyncCancelStatusBarItem"]);
                Assert.IsNull(window.FindName("lr2SongDbSyncCancelButton"));
                Assert.IsNull(statusBar.Resources["styleLr2SongDbSyncCleanupStatusBarItem"]);
                Assert.IsNull(window.FindName("lr2SongDbSyncCleanupButton"));

                var boundPaths = new HashSet<string>(StringComparer.Ordinal);
                foreach (TextBlock textBlock in FindVisualChildren<TextBlock>(statusBar))
                {
                    if (BindingOperations.GetBinding(textBlock, TextBlock.TextProperty) is Binding binding
                        && binding.Path?.Path is string path)
                    {
                        boundPaths.Add(path);
                    }
                    AddBindingPath(boundPaths, textBlock, FrameworkElement.ToolTipProperty);
                }
                foreach (ProgressBar progressBar in FindVisualChildren<ProgressBar>(statusBar))
                {
                    AddBindingPath(boundPaths, progressBar, RangeBase.MaximumProperty);
                    AddBindingPath(boundPaths, progressBar, RangeBase.ValueProperty);
                }

                CollectionAssert.IsSubsetOf(
                    new[]
                    {
                        "StartupProgress.Label",
                        "StartupProgress.SubLabel",
                        "StartupProgress.Maximum",
                        "StartupProgress.Value",
                        "InstallPipelineLabel",
                        "InstallPipelineSubLabel",
                        "InstallPipelineMaximum",
                        "InstallPipelineValue",
                        "PlaylistSyncProgressLabel",
                        "PlaylistSyncProgressSubLabel",
                        "PlaylistSyncProgressMaximum",
                        "PlaylistSyncProgressValue",
                        "MaintenanceRescanLabel",
                        "MaintenanceRescanSubLabel",
                        "MaintenanceRescanMaximum",
                        "MaintenanceRescanValue",
                        "FolderAutoRenameProgressLabel",
                        "FolderAutoRenameProgressSubLabel",
                        "FolderAutoRenameProgressMaximum",
                        "FolderAutoRenameProgressValue",
                        "StartupBackgroundInitializationLabel",
                        "Lr2SongDbSyncStatusLabel",
                        "Lr2SongDbSyncStatusSubLabel",
                        "Lr2SongDbSyncStatusToolTip",
                        "Lr2SongDbSyncStatusProgressMaximum",
                        "Lr2SongDbSyncStatusProgressValue"
                    },
                    boundPaths.ToArray());
            });
    }

    [TestMethod]
    public void ProgressStatusBar_ReflectsRepresentativeHubStateForEveryOwner()
    {
        MainWindowPackageMaintenanceTestHarness.RunConstructorOnly(
            new Settings(),
            (viewModel, window) =>
            {
                using HwndSource visualHost = CreateVisualHost(window, "MainWindowProgressStatusBarState");
                StatusBar statusBar = GetNamedElement<StatusBar>(window, "progressStatusBar");
                OperationProgressHubViewModel hub = viewModel.ProgressHub;

                ResetProgressHub(hub);
                hub.StartupProgress.ApplyPresentation(true, "startup-label", "startup-detail", 2, 5);
                TestUiDispatcherHost.Drain();
                AssertOwnerPresentation(
                    statusBar,
                    "StartupProgress.Label",
                    "startup-label",
                    "StartupProgress.Maximum",
                    5,
                    "StartupProgress.Value",
                    2,
                    "styleStartupProgressStatusBarItem");

                ResetProgressHub(hub);
                hub.IsInstallPipelineStatusActive = true;
                hub.InstallPipelineLabel = "install-label";
                hub.InstallPipelineSubLabel = "install-detail";
                hub.InstallPipelineMaximum = 8;
                hub.InstallPipelineValue = 3;
                hub.InstallPipelineCanCancel = true;
                TestUiDispatcherHost.Drain();
                AssertOwnerPresentation(
                    statusBar,
                    "InstallPipelineLabel",
                    "install-label",
                    "InstallPipelineMaximum",
                    8,
                    "InstallPipelineValue",
                    3,
                    "styleInstallPipelineStatusBarItem");
                Assert.AreEqual(
                    Visibility.Visible,
                    GetNamedElement<Button>(window, "installPipelineCancelButton").Visibility);

                ResetProgressHub(hub);
                hub.IsPlaylistSyncProgressActive = true;
                hub.PlaylistSyncProgressLabel = "playlist-label";
                hub.PlaylistSyncProgressSubLabel = "playlist-detail";
                hub.PlaylistSyncProgressMaximum = 9;
                hub.PlaylistSyncProgressValue = 4;
                TestUiDispatcherHost.Drain();
                AssertOwnerPresentation(
                    statusBar,
                    "PlaylistSyncProgressLabel",
                    "playlist-label",
                    "PlaylistSyncProgressMaximum",
                    9,
                    "PlaylistSyncProgressValue",
                    4,
                    "stylePlaylistSyncStatusBarItem");

                ResetProgressHub(hub);
                hub.IsMaintenanceRescanProgressActive = true;
                hub.MaintenanceRescanLabel = "maintenance-label";
                hub.MaintenanceRescanSubLabel = "maintenance-detail";
                hub.MaintenanceRescanMaximum = 10;
                hub.MaintenanceRescanValue = 5;
                hub.MaintenanceRescanCanCancel = true;
                TestUiDispatcherHost.Drain();
                AssertOwnerPresentation(
                    statusBar,
                    "MaintenanceRescanLabel",
                    "maintenance-label",
                    "MaintenanceRescanMaximum",
                    10,
                    "MaintenanceRescanValue",
                    5,
                    "styleMaintenanceRescanStatusBarItem");
                Assert.AreEqual(
                    Visibility.Visible,
                    GetNamedElement<Button>(window, "maintenanceRescanCancelButton").Visibility);

                ResetProgressHub(hub);
                hub.IsFolderAutoRenameProgressActive = true;
                hub.FolderAutoRenameProgressLabel = "rename-label";
                hub.FolderAutoRenameProgressSubLabel = "rename-detail";
                hub.FolderAutoRenameProgressMaximum = 11;
                hub.FolderAutoRenameProgressValue = 6;
                TestUiDispatcherHost.Drain();
                AssertOwnerPresentation(
                    statusBar,
                    "FolderAutoRenameProgressLabel",
                    "rename-label",
                    "FolderAutoRenameProgressMaximum",
                    11,
                    "FolderAutoRenameProgressValue",
                    6,
                    "styleFolderAutoRenameStatusBarItem");

                ResetProgressHub(hub);
                hub.IsLr2SongDbSyncStatusActive = true;
                hub.Lr2SongDbSyncStatusLabel = "lr2-label";
                hub.Lr2SongDbSyncStatusSubLabel = "lr2-detail";
                hub.Lr2SongDbSyncStatusToolTip = "lr2-tooltip";
                hub.Lr2SongDbSyncStatusProgressMaximum = 12;
                hub.Lr2SongDbSyncStatusProgressValue = 7;
                hub.IsLr2SongDbSyncStatusProgressVisible = true;
                hub.IsLr2SongDbSyncRetryVisible = true;
                TestUiDispatcherHost.Drain();
                AssertOwnerPresentation(
                    statusBar,
                    "Lr2SongDbSyncStatusLabel",
                    "lr2-label",
                    "Lr2SongDbSyncStatusProgressMaximum",
                    12,
                    "Lr2SongDbSyncStatusProgressValue",
                    7,
                    "styleLr2SongDbSyncStatusBarItem");
                Assert.AreEqual(
                    "lr2-tooltip",
                    FindBoundText(statusBar, "Lr2SongDbSyncStatusSubLabel").ToolTip);
                Assert.AreEqual(
                    Visibility.Visible,
                    FindStatusBarItem(statusBar, "styleLr2SongDbSyncProgressStatusBarItem").Visibility);
                Assert.AreEqual(
                    Visibility.Visible,
                    FindStatusBarItem(statusBar, "styleLr2SongDbSyncRetryStatusBarItem").Visibility);

                Style backgroundItemStyle = (Style)statusBar.Resources[
                    "styleStartupBackgroundInitializationStatusBarItem"];
                StatusBarItem backgroundItem = statusBar.Items
                    .OfType<StatusBarItem>()
                    .Where(item => ReferenceEquals(item.Style, backgroundItemStyle))
                    .Single(item => item.Content is ProgressBar);
                Assert.IsInstanceOfType(backgroundItem.Content, typeof(ProgressBar));
                ProgressBar backgroundProgress = (ProgressBar)backgroundItem.Content;
                Assert.IsTrue(backgroundProgress.IsIndeterminate);
                // The indeterminate animation can starve ApplicationIdle while this test drives
                // the precedence transitions. Keep the compiled-content contract assertion,
                // then disable animation on this test-owned control before any transition drain.
                backgroundProgress.IsIndeterminate = false;

                ResetProgressHub(hub);
                hub.BeginStartupBackgroundInitializationPresentation();
                TestUiDispatcherHost.Drain();
                Assert.AreEqual(Visibility.Visible, statusBar.Visibility);
                Assert.AreEqual(Visibility.Visible, backgroundItem.Visibility);
                TextBlock backgroundLabel = FindBoundText(
                    statusBar,
                    "StartupBackgroundInitializationLabel");
                Assert.IsFalse(string.IsNullOrWhiteSpace(backgroundLabel.Text));

                hub.IsPlaylistSyncProgressActive = true;
                TestUiDispatcherHost.Drain();
                Assert.AreEqual(Visibility.Collapsed, backgroundItem.Visibility);

                hub.IsPlaylistSyncProgressActive = false;
                TestUiDispatcherHost.Drain();
                Assert.AreEqual(Visibility.Visible, backgroundItem.Visibility);

                hub.IsLr2SongDbSyncStatusActive = true;
                TestUiDispatcherHost.Drain();
                Assert.AreEqual(Visibility.Collapsed, backgroundItem.Visibility);
            });
    }

    [TestMethod]
    public void ProgressStatusBarButtons_InvokeInjectedTerminalsExactlyOnce()
    {
        var calls = new List<string>();
        var terminals = new MainWindowProgressStatusBarTerminals(
            () => calls.Add("install"),
            () => calls.Add("maintenance"),
            () => calls.Add("retry"));

        RunConstructorOnlyWithStatusBarTerminals(
            terminals,
            (viewModel, window) =>
            {
                RaiseButtonClick(window, "installPipelineCancelButton");
                RaiseButtonClick(window, "maintenanceRescanCancelButton");
                RaiseButtonClick(window, "lr2SongDbSyncRetryButton");
            });

        CollectionAssert.AreEqual(
            new[] { "install", "maintenance", "retry" },
            calls);
    }

    [TestMethod]
    public void ProgressStatusBarCoreFactory_PreservesPlaylistPriorityAndMapsEveryOwnerActionOnce()
    {
        bool playlistUrlDownloadIsRunning = true;
        var calls = new List<string>();
        MainWindowProgressStatusBarTerminals terminals = MainWindowProgressStatusBarTerminals.CreateCore(
            () => playlistUrlDownloadIsRunning,
            () => calls.Add("playlist-cancel"),
            () => calls.Add("package-cancel"),
            () => calls.Add("maintenance-cancel"),
            () => calls.Add("lr2-retry"));

        terminals.CancelInstallPipeline();
        CollectionAssert.AreEqual(new[] { "playlist-cancel" }, calls);

        playlistUrlDownloadIsRunning = false;
        terminals.CancelInstallPipeline();
        terminals.CancelMaintenanceRescan();
        terminals.RetryLr2Sync();

        CollectionAssert.AreEqual(
            new[]
            {
                "playlist-cancel",
                "package-cancel",
                "maintenance-cancel",
                "lr2-retry"
            },
            calls);
    }

    [TestMethod]
    public async Task DefaultFactory_MapsEveryOwnerRouteAndPreservesPlaylistCancellationPriority()
    {
        var settings = new Settings { OperationModeLR2DB = false };
        var composition = new ApplicationComposition(
            settingsEditSession: new NoOpSettingsEditSession(settings),
            playlistWorkspaceDialogService: new AcceptedStatusBarTestDialogService(),
            uiScheduler: new TestUiScheduler(() => TestUiDispatcherHost.Dispatcher),
            applicationLifetime: TestApplicationContext.CreateLifetime(),
            cultureCatalog: TestApplicationContext.CreateCultureCatalog());
        MainWindowViewModel viewModel = composition.CreateMainWindowViewModelForTest();
        MainWindowProgressStatusBarTerminals terminals = MainWindowProgressStatusBarTerminals.Create(viewModel);
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

            terminals.CancelInstallPipeline();

            await download.WaitAsync(TimeSpan.FromSeconds(5));
            Assert.IsFalse(viewModel.PlaylistWorkspace.IsPlaylistUrlDownloadRunning);
            Assert.AreEqual(1, packageCancelRequests, "Playlist download cancellation must have priority over package installation.");
            Assert.AreEqual(1, Volatile.Read(ref playlistCanceledSnapshots));
        }
        finally
        {
            viewModel.SettingDialog.Dispose();
        }
    }

    private static void RunConstructorOnlyWithStatusBarTerminals(
        MainWindowProgressStatusBarTerminals terminals,
        Action<MainWindowViewModel, MainWindow> test)
    {
        TestUiDispatcherHost.RunWindowTest(_ =>
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
                    uiScheduler: new TestUiScheduler(() => TestUiDispatcherHost.Dispatcher),
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
                test(viewModel, window);
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

    private static void ResetProgressHub(OperationProgressHubViewModel hub)
    {
        hub.ResetStartupBackgroundInitializationPresentation();
        hub.StartupProgress.ApplyPresentation(false, string.Empty, string.Empty, 0, 1);
        hub.IsInstallPipelineStatusActive = false;
        hub.InstallPipelineCanCancel = false;
        hub.IsPlaylistSyncProgressActive = false;
        hub.IsMaintenanceRescanProgressActive = false;
        hub.MaintenanceRescanCanCancel = false;
        hub.IsFolderAutoRenameProgressActive = false;
        hub.IsLr2SongDbSyncStatusActive = false;
        hub.IsLr2SongDbSyncStatusProgressVisible = false;
        hub.IsLr2SongDbSyncRetryVisible = false;
        TestUiDispatcherHost.Drain();
    }

    private static void AssertOwnerPresentation(
        StatusBar statusBar,
        string labelPath,
        string expectedLabel,
        string maximumPath,
        double expectedMaximum,
        string valuePath,
        double expectedValue,
        string itemStyleKey)
    {
        Assert.AreEqual(Visibility.Visible, statusBar.Visibility, labelPath);
        TextBlock label = FindBoundText(statusBar, labelPath);
        Assert.AreEqual(expectedLabel, label.Text, labelPath);
        ProgressBar progressBar = FindBoundProgressBar(statusBar, maximumPath, valuePath);
        Assert.AreEqual(expectedMaximum, progressBar.Maximum, maximumPath);
        Assert.AreEqual(expectedValue, progressBar.Value, valuePath);
        Assert.AreEqual(Visibility.Visible, FindStatusBarItem(statusBar, itemStyleKey).Visibility, itemStyleKey);
    }

    private static string[] GetTriggerBindingPaths(Style style)
    {
        return style.Triggers
            .OfType<DataTrigger>()
            .Select(trigger => (trigger.Binding as Binding)?.Path?.Path)
            .Where(path => path != null)
            .Cast<string>()
            .ToArray();
    }

    private static void AddBindingPath(
        ISet<string> paths,
        DependencyObject target,
        DependencyProperty property)
    {
        if (BindingOperations.GetBinding(target, property) is Binding binding
            && binding.Path?.Path is string path)
        {
            paths.Add(path);
        }
    }

    private static TextBlock FindBoundText(StatusBar statusBar, string path)
    {
        return FindVisualChildren<TextBlock>(statusBar)
            .Single(textBlock => BindingOperations.GetBinding(textBlock, TextBlock.TextProperty) is Binding binding
                && string.Equals(binding.Path?.Path, path, StringComparison.Ordinal));
    }

    private static ProgressBar FindBoundProgressBar(StatusBar statusBar, string maximumPath, string valuePath)
    {
        return FindVisualChildren<ProgressBar>(statusBar)
            .Single(progressBar => string.Equals(
                    (BindingOperations.GetBinding(progressBar, RangeBase.MaximumProperty) as Binding)?.Path?.Path,
                    maximumPath,
                    StringComparison.Ordinal)
                && string.Equals(
                    (BindingOperations.GetBinding(progressBar, RangeBase.ValueProperty) as Binding)?.Path?.Path,
                    valuePath,
                    StringComparison.Ordinal));
    }

    private static StatusBarItem FindStatusBarItem(StatusBar statusBar, string styleKey)
    {
        Style style = (Style)statusBar.Resources[styleKey];
        return statusBar.Items
            .OfType<StatusBarItem>()
            .First(item => ReferenceEquals(item.Style, style));
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

    private static void RaiseButtonClick(MainWindow window, string name)
    {
        Button button = GetNamedElement<Button>(window, name);
        button.RaiseEvent(new RoutedEventArgs(Button.ClickEvent, button));
    }

    private static T GetNamedElement<T>(MainWindow window, string name)
        where T : class
    {
        object? element = window.FindName(name);
        Assert.IsInstanceOfType(element, typeof(T), name);
        return (T)element!;
    }

    private static HwndSource CreateVisualHost(MainWindow window, string name)
    {
        var source = new HwndSource(new HwndSourceParameters(name)
        {
            Width = 1000,
            Height = 700,
            PositionX = 0,
            PositionY = 0
        });
        source.RootVisual = (Visual)window.Content;
        window.Measure(new Size(1000d, 700d));
        window.Arrange(new Rect(0d, 0d, 1000d, 700d));
        window.UpdateLayout();
        return source;
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
