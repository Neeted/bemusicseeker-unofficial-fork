using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Threading;
using System.Linq;
using System.IO;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Data;
using System.Windows.Threading;
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
public sealed class MainWindowViewHostTests
{
    [TestMethod]
    public void MainWindowShutdownCapturePrecedesShellCompletion()
    {
        const double initialTreeViewWidth = 281d;
        const double capturedTreeViewWidth = 347d;
        var settings = CreateSettings(
            initialTreeViewWidth,
            startupSelectInstallPending: false,
            customTableRowHeight: 23d,
            customTableHeaderHeight: 25d,
            customTableFontSize: 13d);
        var events = new List<string>();
        var settingsSession = new RecordingSettingsEditSession(settings, () =>
        {
            events.Add("save");
        });
        var applicationLifetime = new RecordingApplicationLifetime(events);

        TestUiDispatcherHost.RunWindowTest(_ =>
        {
            MainWindowViewModel? viewModel = null;
            MainWindow? window = null;
            bool windowClosed = false;
            bool hadPreviousViewModelResource = Application.Current.Resources.Contains("vm");
            object? previousViewModelResource = hadPreviousViewModelResource
                ? Application.Current.Resources["vm"]
                : null;
            try
            {
                ApplicationComposition composition = CreateComposition(settingsSession, applicationLifetime);
                viewModel = composition.CreateMainWindowViewModel();

                // Constructor activation is intentionally suppressed; this test exercises only
                // the unshown view-host construction and close terminal, never startup rendering.
                viewModel.StartupUpdateWorkflow.NotifyClosing();
                Application.Current.Resources["vm"] = viewModel;
                window = new MainWindow(viewModel);
                window.Closed += (_, _) => events.Add("window-closed");

                ColumnDefinition treeColumn = GetNamedElement<ColumnDefinition>(window, "gridColumn0");
                treeColumn.Width = new GridLength(capturedTreeViewWidth, GridUnitType.Pixel);
                window.Close();

                Task<ShellShutdownWorkflowCompletionReceipt> closeRequest =
                    viewModel.ShellShutdownWorkflow.RequestWindowCloseAsync();
                TestUiDispatcherHost.AwaitTaskOnDispatcher(
                    closeRequest,
                    "MainWindowViewHostTests.MainWindowShutdownCapturePrecedesShellCompletion.window-close");
                TestUiDispatcherHost.AwaitTaskOnDispatcher(
                    applicationLifetime.ShutdownRequested.Task,
                    "MainWindowViewHostTests.MainWindowShutdownCapturePrecedesShellCompletion.application-shutdown");

                Assert.AreEqual(capturedTreeViewWidth, settingsSession.TreeViewWidthAtSave);
                Assert.AreEqual(1, settingsSession.SaveCount);
                CollectionAssert.AreEqual(
                    new[] { "save", "application-shutdown" },
                    events);
                Assert.AreEqual(1, applicationLifetime.RequestShutdownCount);

                // The test lifetime is deliberately non-terminating. Complete the actual Window
                // close after the shell has requested application termination.
                window.Close();
                windowClosed = true;

                CollectionAssert.AreEqual(
                    new[] { "save", "application-shutdown", "window-closed" },
                    events);
            }
            finally
            {
                CleanupViewHost(
                    window,
                    windowClosed,
                    viewModel,
                    hadPreviousViewModelResource,
                    previousViewModelResource);
            }
        });
    }

    [TestMethod]
    public void MainWindowViewSettingsBindAndCaptureThroughHostStore()
    {
        const double initialTreeViewWidth = 286d;
        const double capturedTreeViewWidth = 361d;
        const double rowHeight = 24d;
        const double headerHeight = 27d;
        const double fontSize = 14d;
        var settings = CreateSettings(
            initialTreeViewWidth,
            startupSelectInstallPending: true,
            customTableRowHeight: rowHeight,
            customTableHeaderHeight: headerHeight,
            customTableFontSize: fontSize);
        var settingsSession = new RecordingSettingsEditSession(settings);
        var applicationLifetime = new RecordingApplicationLifetime();

        TestUiDispatcherHost.RunWindowTest(_ =>
        {
            MainWindowViewModel? viewModel = null;
            MainWindow? window = null;
            bool windowClosed = false;
            bool hadPreviousViewModelResource = Application.Current.Resources.Contains("vm");
            object? previousViewModelResource = hadPreviousViewModelResource
                ? Application.Current.Resources["vm"]
                : null;
            try
            {
                ApplicationComposition composition = CreateComposition(settingsSession, applicationLifetime);
                viewModel = composition.CreateMainWindowViewModel();
                // A pending selection is a valid startup setting, but the constructor-only test
                // keeps the corresponding selection handler behind the existing startup gate.
                viewModel.ProgressHub.StartupProgress.SetStartupUiInteractionBlocked(true);
                viewModel.StartupUpdateWorkflow.NotifyClosing();
                Application.Current.Resources["vm"] = viewModel;
                window = new MainWindow(viewModel);
                TestUiDispatcherHost.Drain();

                ColumnDefinition treeColumn = GetNamedElement<ColumnDefinition>(window, "gridColumn0");
                CustomTableView mainTable = GetNamedElement<CustomTableView>(window, "customTableView");
                CustomTableView summaryTable = GetNamedElement<CustomTableView>(window, "customTablePlaylistSummary");
                TreeViewItem pendingItem = GetNamedElement<TreeViewItem>(window, "treeViewItemInstallPending");

                Assert.AreEqual(initialTreeViewWidth, treeColumn.Width.Value);
                Assert.AreEqual(rowHeight, mainTable.RowHeight);
                Assert.AreEqual(headerHeight, mainTable.HeaderHeight);
                Assert.AreEqual(fontSize, mainTable.TextFontSize);
                Assert.AreEqual(rowHeight, summaryTable.RowHeight);
                Assert.AreEqual(headerHeight, summaryTable.HeaderHeight);
                Assert.AreEqual(fontSize, summaryTable.TextFontSize);
                Assert.IsTrue(pendingItem.IsSelected);
                Assert.IsTrue(viewModel.ViewSettings.StartupSelectInstallPending);

                treeColumn.Width = new GridLength(capturedTreeViewWidth, GridUnitType.Pixel);
                window.Close();
                Task<ShellShutdownWorkflowCompletionReceipt> closeRequest =
                    viewModel.ShellShutdownWorkflow.RequestWindowCloseAsync();
                TestUiDispatcherHost.AwaitTaskOnDispatcher(
                    closeRequest,
                    "MainWindowViewHostTests.MainWindowViewSettingsBindAndCaptureThroughHostStore.window-close");
                TestUiDispatcherHost.AwaitTaskOnDispatcher(
                    applicationLifetime.ShutdownRequested.Task,
                    "MainWindowViewHostTests.MainWindowViewSettingsBindAndCaptureThroughHostStore.application-shutdown");

                Assert.AreEqual(capturedTreeViewWidth, settingsSession.TreeViewWidthAtSave);
                Assert.AreEqual(capturedTreeViewWidth, settings.TreeViewWidth);
                Assert.AreEqual(1, settingsSession.SaveCount);

                window.Close();
                windowClosed = true;
            }
            finally
            {
                CleanupViewHost(
                    window,
                    windowClosed,
                    viewModel,
                    hadPreviousViewModelResource,
                    previousViewModelResource);
            }
        });
    }

    [TestMethod]
    public void MainWindowConstructorOnlyPresentsInitialSetupThroughCompiledOverlay()
    {
        var settings = CreateSettings(
            treeViewWidth: 280d,
            startupSelectInstallPending: false,
            customTableRowHeight: 23d,
            customTableHeaderHeight: 25d,
            customTableFontSize: 13d);
        var settingsSession = new RecordingSettingsEditSession(settings);
        var applicationLifetime = new RecordingApplicationLifetime();

        TestUiDispatcherHost.RunWindowTest(_ =>
        {
            MainWindowViewModel? viewModel = null;
            MainWindow? window = null;
            bool windowClosed = false;
            bool hadPreviousViewModelResource = Application.Current.Resources.Contains("vm");
            object? previousViewModelResource = hadPreviousViewModelResource
                ? Application.Current.Resources["vm"]
                : null;
            try
            {
                ApplicationComposition composition = CreateComposition(settingsSession, applicationLifetime);
                viewModel = composition.CreateMainWindowViewModel();
                viewModel.StartupUpdateWorkflow.NotifyClosing();
                viewModel.ProgressHub.StartupProgress.SetStartupUiInteractionBlocked(false);
                Application.Current.Resources["vm"] = viewModel;

                window = new MainWindow(viewModel);
                InitialSetupLanguageDialog overlay = GetNamedElement<InitialSetupLanguageDialog>(
                    window,
                    "initialSetupLanguageDialog");
                StatusBar progressStatusBar = GetNamedElement<StatusBar>(window, "progressStatusBar");
                Assert.IsFalse(window.IsVisible);
                Assert.AreEqual(Visibility.Hidden, overlay.Visibility);
                Assert.AreSame(viewModel, window.DataContext);
                Assert.AreSame(viewModel.SettingDialog, overlay.DataContext);
                Assert.AreSame(viewModel.ProgressHub, progressStatusBar.DataContext);

                ComboBox languageSelector = FindLogicalDescendants<ComboBox>(overlay).Single();
                Button continueButton = FindLogicalDescendants<Button>(overlay).Single();
                Binding itemsSourceBinding = BindingOperations.GetBinding(
                    languageSelector,
                    ItemsControl.ItemsSourceProperty);
                Binding selectedItemBinding = BindingOperations.GetBinding(
                    languageSelector,
                    Selector.SelectedItemProperty);
                Binding commandBinding = BindingOperations.GetBinding(
                    continueButton,
                    Button.CommandProperty);
                Assert.AreEqual(nameof(SettingsDialogViewModel.Languages), itemsSourceBinding.Path.Path);
                Assert.AreEqual(nameof(SettingsDialogViewModel.Language), selectedItemBinding.Path.Path);
                Assert.AreEqual(nameof(SettingsDialogViewModel.OpenCommand), commandBinding.Path.Path);

                viewModel.SettingDialog.RequestInitialSetupLanguageDialog();
                TestUiDispatcherHost.Drain();
                Assert.AreEqual(Visibility.Visible, overlay.Visibility);
                Assert.AreSame(viewModel.SettingDialog, overlay.DataContext);
                string[] expectedLanguages = viewModel.SettingDialog.Languages.ToArray();
                string[] actualLanguages = (languageSelector.ItemsSource as IEnumerable<string>)?.ToArray()
                    ?? Array.Empty<string>();
                CollectionAssert.AreEquivalent(
                    expectedLanguages,
                    actualLanguages,
                    $"expected=[{string.Join(",", expectedLanguages)}] actual=[{string.Join(",", actualLanguages)}]");
                Assert.AreEqual(viewModel.SettingDialog.Language, languageSelector.SelectedItem);
                Assert.AreSame(viewModel.SettingDialog.OpenCommand, continueButton.Command);

                window.HideOverlayDialog(overlay);
                Assert.AreEqual(Visibility.Hidden, overlay.Visibility);
                window.Close();
                Task<ShellShutdownWorkflowCompletionReceipt> closeRequest =
                    viewModel.ShellShutdownWorkflow.RequestWindowCloseAsync();
                TestUiDispatcherHost.AwaitTaskOnDispatcher(
                    closeRequest,
                    "MainWindowViewHostTests.MainWindowConstructorOnlyPresentsInitialSetupThroughCompiledOverlay.window-close");
                TestUiDispatcherHost.AwaitTaskOnDispatcher(applicationLifetime.ShutdownRequested.Task, "overlay-terminal-shutdown");
                window.Close();
                windowClosed = true;

                viewModel.SettingDialog.RequestInitialSetupLanguageDialog();
                Assert.AreEqual(
                    Visibility.Hidden,
                    overlay.Visibility,
                    "A detached presentation port must not show the compiled overlay again.");
            }
            finally
            {
                CleanupViewHost(
                    window,
                    windowClosed,
                    viewModel,
                    hadPreviousViewModelResource,
                    previousViewModelResource);
            }
        });
    }

    private static ApplicationComposition CreateComposition(
        ISettingsEditSession settingsSession,
        IApplicationLifetimePort applicationLifetime)
    {
        return new ApplicationComposition(
            settingsEditSession: settingsSession,
            uiScheduler: new WpfUiScheduler(() => Dispatcher.CurrentDispatcher),
            applicationLifetime: applicationLifetime,
            cultureCatalog: TestApplicationContext.CreateCultureCatalog());
    }

    private static Settings CreateSettings(
        double treeViewWidth,
        bool startupSelectInstallPending,
        double customTableRowHeight,
        double customTableHeaderHeight,
        double customTableFontSize)
    {
        var settings = new Settings
        {
            TreeViewWidth = treeViewWidth,
            StartupSelectInstallPending = startupSelectInstallPending,
            CustomTableRowHeight = customTableRowHeight,
            CustomTableHeaderHeight = customTableHeaderHeight,
            CustomTableFontSize = customTableFontSize
        };
        return settings;
    }

    private static T GetNamedElement<T>(MainWindow window, string name)
        where T : class
    {
        object? element = window.FindName(name);
        Assert.IsInstanceOfType(element, typeof(T), name);
        return (T)element!;
    }

    private static IEnumerable<T> FindLogicalDescendants<T>(DependencyObject root)
        where T : DependencyObject
    {
        foreach (object childValue in LogicalTreeHelper.GetChildren(root))
        {
            if (childValue is not DependencyObject child)
            {
                continue;
            }

            if (child is T match)
            {
                yield return match;
            }

            foreach (T descendant in FindLogicalDescendants<T>(child))
            {
                yield return descendant;
            }
        }
    }

    private static void RestoreViewModelResource(bool hadPreviousResource, object? previousResource)
    {
        if (hadPreviousResource)
        {
            Application.Current.Resources["vm"] = previousResource;
        }
        else
        {
            Application.Current.Resources.Remove("vm");
        }
    }

    // U3-T1/T2/T3: 実 Close から入り、同期 player 待機中も UI と終了順序を守る。
    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public void MainWindowPlayerDrainKeepsDispatcherResponsiveAndDefersTerminalClose(bool prepareForUpdate)
    {
        var settings = CreateSettings(279d, false, 23d, 25d, 13d);
        using var release = new ManualResetEventSlim();
        var entered = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var completed = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        TestUiDispatcherHost.RunWindowTest(_ =>
        {
            MainWindowViewModel? viewModel = null;
            MainWindow? window = null;
            bool closed = false;
            bool hadResource = Application.Current.Resources.Contains("vm");
            object? previous = hadResource ? Application.Current.Resources["vm"] : null;
            var dispatcher = Dispatcher.CurrentDispatcher;
            bool saveOnUi = false;
            bool audioActiveAtSave = false;
            bool audioInactiveAtShutdown = false;
            bool shutdownOnUi = false;
            bool capturedBeforePlayer = false;
            bool playerCompletedBeforeSave = false;
            var session = new RecordingSettingsEditSession(settings, () =>
            {
                saveOnUi = dispatcher.CheckAccess();
                audioActiveAtSave = CanEnterAudioOperation();
                playerCompletedBeforeSave = completed.Task.IsCompleted;
            });
            var lifetime = new RecordingApplicationLifetime(onShutdown: () =>
            {
                audioInactiveAtShutdown = !CanEnterAudioOperation();
                shutdownOnUi = dispatcher.CheckAccess();
            });
            var player = new FakeBmsPlayer(() =>
            {
                capturedBeforePlayer = settings.TreeViewWidth == 359d;
                entered.TrySetResult(true);
                release.Wait();
                completed.TrySetResult(true);
            });
            Task? observation = null;
            Task? marker = null;
            try
            {
                Ribbit.Media.Audio.BassAudioRuntime.Initialize();
                viewModel = CreateComposition(session, lifetime).CreateMainWindowViewModel();
                viewModel.StartupUpdateWorkflow.NotifyClosing();
                Application.Current.Resources["vm"] = viewModel;
                window = new MainWindow(viewModel);
                window.Closed += (_, _) => closed = true;
                TestUiDispatcherHost.AwaitTaskOnDispatcher(viewModel.PlaybackPanel.ReplacePlayerAsync(player), "attach-player");
                GetNamedElement<ColumnDefinition>(window, "gridColumn0").Width = new GridLength(359d);
                if (prepareForUpdate)
                {
                    TestUiDispatcherHost.AwaitTaskOnDispatcher(viewModel.ShellShutdownWorkflow.PrepareForStartupUpdateAsync("update"), "update-preparation");
                }
                observation = Task.Run(async () =>
                {
                    try
                    {
                        await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
                        marker = dispatcher.InvokeAsync(() =>
                        {
                            Assert.IsFalse(completed.Task.IsCompleted);
                            Assert.IsTrue(CanEnterAudioOperation(), "player drain 中は audio runtime を解放しない。");
                            Assert.AreEqual(0, session.SaveCount);
                            Assert.AreEqual(0, lifetime.RequestShutdownCount);
                            Assert.IsFalse(closed);
                            Assert.IsFalse(viewModel.PlaybackPanel.NextCommand.CanExecute, "終了開始後は次の再生操作を受け付けない。");
                            Assert.IsFalse(viewModel.PlaybackPanel.StartCommand.CanExecute);
                            Task first = viewModel.ShellShutdownWorkflow.CompleteTerminalShutdownAsync();
                            Task second = viewModel.ShellShutdownWorkflow.CompleteTerminalShutdownAsync();
                            Assert.AreSame(first, second);
                            Assert.IsFalse(first.IsCompleted);
                            window.Close();
                            Assert.IsFalse(closed, "準備完了だけで再入 Close を通さない。");
                        }).Task;
                        await marker.WaitAsync(TimeSpan.FromSeconds(5));
                    }
                    finally
                    {
                        // UI が同期 close で停止する mutant でも外部 coordinator が必ず解放する。
                        release.Set();
                    }
                });
                window.Close();
                TestUiDispatcherHost.AwaitTaskOnDispatcher(observation, "player-drain-marker");
                TestUiDispatcherHost.AwaitTaskOnDispatcher(lifetime.ShutdownRequested.Task, "terminal-shutdown");
                Assert.IsTrue(capturedBeforePlayer);
                Assert.IsTrue(playerCompletedBeforeSave);
                Assert.IsTrue(saveOnUi);
                Assert.IsTrue(audioActiveAtSave);
                Assert.IsTrue(audioInactiveAtShutdown);
                Assert.IsTrue(shutdownOnUi);
                Assert.AreEqual(1, session.SaveCount);
                Assert.AreEqual(1, player.CloseProcessCount);
                Assert.AreEqual(1, lifetime.RequestShutdownCount);
                window.Close();
                Assert.IsTrue(closed);
                Assert.AreEqual(1, player.CloseProcessCount);
                Assert.AreEqual(1, session.SaveCount);
            }
            finally
            {
                release.Set();
                try
                {
                    if (observation != null)
                    {
                        try { TestUiDispatcherHost.AwaitTaskOnDispatcher(observation, "marker-cleanup"); }
                        catch { /* 本体の失敗を保持し、下で所有資源を回収する。 */ }
                        if (marker != null)
                        {
                            try { TestUiDispatcherHost.AwaitTaskOnDispatcher(marker, "queued-marker-cleanup"); }
                            catch { /* coordinator に伝播済みの assertion。 */ }
                        }
                    }
                    if (viewModel != null)
                    {
                        // 早期 Window close の mutant で terminal が未開始でも実 owner を drain する。
                        TestUiDispatcherHost.AwaitTaskOnDispatcher(
                            viewModel.ShellShutdownWorkflow.CompleteTerminalShutdownAsync(), "owner-cleanup");
                    }
                    if (observation != null)
                    {
                        TestUiDispatcherHost.AwaitTaskOnDispatcher(completed.Task, "player-cleanup");
                        if (!closed)
                        {
                            TestUiDispatcherHost.AwaitTaskOnDispatcher(lifetime.ShutdownRequested.Task, "terminal-cleanup");
                        }
                    }
                }
                finally
                {
                    try { CleanupViewHost(window, closed, viewModel, hadResource, previous); }
                    finally { Ribbit.Media.Audio.BassAudioRuntime.Shutdown(); }
                }
            }
        });
    }

    [TestMethod]
    public void MainWindowOperationModeRestartDrainsTrackedWorkerAndPlayerBeforeStartAndShutdown()
    {
        RunMainWindowOperationModeRestartScenario(restartFailure: null);
    }

    [TestMethod]
    public void MainWindowOperationModeRestartFailureAwaitsNotificationBeforeShutdown()
    {
        RunMainWindowOperationModeRestartScenario(
            new InvalidOperationException("replacement process could not start"));
    }

    [TestMethod]
    public void MainWindowOperationModeRestartNotificationFailureStillShutsDown()
    {
        RunMainWindowOperationModeRestartScenario(
            new InvalidOperationException("replacement process could not start"),
            UiDialogResult.NotShown(UiDialogStatus.OwnerUnavailable));
    }

    [TestMethod]
    public void MainWindowCloseDuringModeConfirmationPreservesEarlierCloseWithoutModeSave()
    {
        RunMainWindowOperationModeRestartScenario(restartFailure: null, closeDuringConfirmation: true);
    }

    [TestMethod]
    public void MainWindowOperationModeRestartNotificationExceptionStillShutsDown()
    {
        RunMainWindowOperationModeRestartScenario(
            new InvalidOperationException("restart failed"),
            notificationFailure: new InvalidOperationException("notification failed"));
    }

    [TestMethod]
    public void MainWindowOperationModeRestartSaveFailureRetainsSettingsWithoutShutdown()
    {
        RunMainWindowOperationModeRestartScenario(
            restartFailure: null,
            operationModeSaveFailure: true);
    }

    private static void RunMainWindowOperationModeRestartScenario(
        Exception? restartFailure,
        UiDialogResult? notificationResult = null,
        Exception? notificationFailure = null,
        bool closeDuringConfirmation = false,
        bool operationModeSaveFailure = false)
    {
        using var directory = new TestTemporaryDirectory("main-window-mode-restart");
        string settingsPath = Path.Combine(directory.Path, "user.config");
        Settings settings = PortableSettingsPersistenceTests.OpenSettings(settingsPath);
        settings.OperationModeLR2DB = false;
        settings.PlayHistorySelectedDisplayTargetIdentity = "history-initial";
        settings.BMSRootPath = directory.Path;
        settings.StandaloneBmsRootPaths = directory.Path;
        settings.BMSInstallDir = directory.Path;
        settings.TableListURL = new Uri("http://127.0.0.1:1/table-list.json");
        settings.EnablePlaylistUrlCompletion = false;
        settings.ScanBmsFilesOnStartup = false;
        settings.SkipInitPlaylistLoad = true;
        settings.UseBeatorajaScoreDb = false;
        settings.EnableBeatorajaBmtOutput = false;
        settings.UseExternalPanelImage = false;
        settings.UsePlayeruBMplay = false;
        settings.UsePlayerLR2body = false;
        settings.UsePlayerBMIIDXView = false;
        settings.IsLR2BackupEnabled = false;
        settings.ShowScoreViewerRegisterConfirmMsg = false;
        settings.Save();
        byte[] originalSettingsBytes = File.ReadAllBytes(settingsPath);
        string songDbPath = Path.Combine(directory.Path, "song.db");
        StartupLibraryConstructionTestSupport.CreateSongDatabase(songDbPath);
        PlaylistPersistenceRepository.EnsureSchema(songDbPath);

        using var packageEntered = new ManualResetEventSlim(false);
        using var packageRelease = new ManualResetEventSlim(false);
        using var playerRelease = new ManualResetEventSlim(false);
        var playerEntered = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var restartEntered = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var notificationEntered = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var notificationRelease = new TaskCompletionSource<UiDialogResult>(TaskCreationOptions.RunContinuationsAsynchronously);
        var settingsSaveFailureReported = new TaskCompletionSource<Exception>(TaskCreationOptions.RunContinuationsAsynchronously);
        Exception? reportedRestartFailure = null;
        var reportedSettingsFailures = new List<Exception>();
        var events = new List<string>();
        object eventsLock = new();
        void AddEvent(string value)
        {
            lock (eventsLock)
            {
                events.Add(value);
            }
        }

        var dialogs = new AcceptedModeDialogService(
            restartFailure == null ? null : notificationEntered,
            notificationRelease,
            notificationResult);
        int packageCallCount = 0;
        var packageMutation = new DelegatePackageInstallMutationPort((_, _, _, _, _) =>
        {
            packageCallCount++;
            packageEntered.Set();
            packageRelease.Wait();
            return Array.Empty<ChartPackage>();
        });
        var lifetime = new RecordingApplicationLifetime(
            onShutdown: () => AddEvent("shutdown"),
            restartApplication: () =>
            {
                AddEvent("restart");
                restartEntered.TrySetResult(true);
                return restartFailure == null
                    ? Task.CompletedTask
                    : Task.FromException(restartFailure);
            });
        var settingsSession = new PersistentSettingsEditSession(
            settings,
            beforeSave: () => AddEvent("save"));

        TestUiDispatcherHost.RunWindowTest(_ =>
        {
            MainWindowViewModel? viewModel = null;
            MainWindow? window = null;
            bool windowClosed = false;
            FileStream? settingsBlocker = null;
            bool hadPreviousViewModelResource = Application.Current.Resources.Contains("vm");
            object? previousViewModelResource = hadPreviousViewModelResource
                ? Application.Current.Resources["vm"]
                : null;
            Task? observation = null;
            bool dispatcherMarkerObserved = false;
            try
            {
                Dispatcher dispatcher = Dispatcher.CurrentDispatcher;
                ApplicationComposition composition = new(
                    settingsEditSession: settingsSession,
                    defaultBmsPlayerFactory: () => new FakeBmsPlayer(),
                    uiScheduler: new WpfUiScheduler(() => dispatcher),
                    applicationLifetime: lifetime,
                    cultureCatalog: TestApplicationContext.CreateCultureCatalog(),
                    settingsDialogService: dialogs,
                    restartFailureDialogs: dialogs,
                    reportSettingsApplyFailure: exception =>
                    {
                        reportedSettingsFailures.Add(exception);
                        settingsSaveFailureReported.TrySetResult(exception);
                    },
                    reportRestartFailure: exception => reportedRestartFailure = exception,
                    packageInstallMutationPort: packageMutation);
                var library = new TestBmsLibrary(
                    songDbPath,
                    getLR2Config: null,
                    _lr2ScoreDB: null,
                    startupRequiredFileScanReason: null,
                    optionsSnapshotProvider: () => BmsLibraryOptionsSnapshot.CreateCurrent(settings));
                TestBmsPlaylist playlist = MainWindowViewModelTestFactory.CreatePlaylist(songDbPath, settings);
                viewModel = new MainWindowViewModel(
                    composition,
                    new FixedStartupLibraryFactory(library, playlist));
                viewModel.StartupUpdateWorkflow.NotifyClosing();
                viewModel.ProgressHub.StartupProgress.SetStartupUiInteractionBlocked(false);
                Application.Current.Resources["vm"] = viewModel;
                TestUiDispatcherHost.AwaitTaskOnDispatcher(
                    viewModel.ShellActivationWorkflow.ActivateRenderedShell(
                        () => { },
                        action => action(),
                        () => false),
                    "MainWindowViewHostTests.mode-restart-initialization");
                Assert.IsTrue(viewModel.IsInitializationCompleted);
                Assert.IsTrue(viewModel.HasActiveLibraryProfile);

                settings.ShowScoreViewerRegisterConfirmMsg = true;
                window = new MainWindow(viewModel);
                window.Closed += (_, _) => windowClosed = true;
                if (closeDuringConfirmation)
                {
                    dialogs.OnConfirmation = window.Close;
                }
                TestUiDispatcherHost.AwaitTaskOnDispatcher(
                    viewModel.PlaybackPanel.ReplacePlayerAsync(new FakeBmsPlayer(() =>
                    {
                        playerEntered.TrySetResult(true);
                        playerRelease.Wait();
                    })),
                    "MainWindowViewHostTests.mode-restart-player");
                viewModel.PackageInstallWorkflow.EnqueueSingle(Path.Combine(directory.Path, "pending.zip"));
                Assert.IsTrue(packageEntered.Wait(TimeSpan.FromSeconds(5)), "The tracked package worker did not enter.");
                if (operationModeSaveFailure)
                {
                    // provider は user.config を置換して公開するため、削除/名前変更を拒否して
                    // production の設定 session を実際の保存失敗経路へ通します。
                    settingsBlocker = new FileStream(
                        settingsPath,
                        FileMode.Open,
                        FileAccess.Read,
                        FileShare.ReadWrite);
                }

                observation = Task.Run(async () =>
                {
                    try
                    {
                        await playerEntered.Task.WaitAsync(TimeSpan.FromSeconds(5));
                        DispatcherOperation marker = dispatcher.InvokeAsync(() =>
                        {
                            dispatcherMarkerObserved = true;
                            Assert.AreEqual(0, lifetime.RestartCount);
                            Assert.AreEqual(0, lifetime.RequestShutdownCount);
                            Assert.AreEqual(0, settingsSession.SaveCount);
                        }, DispatcherPriority.ApplicationIdle);
                        await marker.Task.WaitAsync(TimeSpan.FromSeconds(5));
                    }
                    finally
                    {
                        playerRelease.Set();
                    }
                });

                viewModel.SettingDialog.OperationModeLR2DB = true;
                if (operationModeSaveFailure)
                {
                    TestUiDispatcherHost.AwaitTaskOnDispatcher(
                        settingsSaveFailureReported.Task,
                        "MainWindowViewHostTests.mode-restart-save-failure");
                }
                Assert.AreEqual(
                    !closeDuringConfirmation && !operationModeSaveFailure,
                    viewModel.SettingDialog.OperationModeLR2DB);
                Assert.AreEqual(1, dialogs.ConfirmationCount);
                Assert.AreEqual(closeDuringConfirmation ? 0 : 1, settingsSession.ModeSaveCount);
                Assert.AreEqual(0, lifetime.RestartCount);
                Assert.AreEqual(0, lifetime.RequestShutdownCount);
                if (operationModeSaveFailure)
                {
                    Assert.AreEqual(1, reportedSettingsFailures.Count);
                    Assert.IsInstanceOfType<PortableSettingsException>(reportedSettingsFailures[0]);
                    Assert.AreEqual(0, lifetime.CoordinatedShutdownStartCount);
                    Assert.IsFalse(viewModel.ShellShutdownWorkflow.IsShutdownPreparationStarted);
                    Assert.IsFalse(viewModel.ShellShutdownWorkflow.IsClosingOrClosed);
                    Assert.AreEqual(0, lifetime.RestartCount);
                    Assert.AreEqual(0, lifetime.RequestShutdownCount);
                    CollectionAssert.AreEqual(originalSettingsBytes, File.ReadAllBytes(settingsPath));
                    Assert.IsFalse(settings.OperationModeLR2DB);
                    Assert.IsFalse(viewModel.SettingDialog.OperationModeLR2DB);
                    Assert.IsTrue(viewModel.SettingDialog.ShowScoreViewerRegisterConfirmMsg);
                }
                // 待機中の Close 再入も同じ terminal を共有します。
                settingsBlocker?.Dispose();
                settingsBlocker = null;
                window.Close();

                packageRelease.Set();
                TestUiDispatcherHost.AwaitTaskOnDispatcher(observation, "MainWindowViewHostTests.mode-restart-dispatcher-marker");
                if (!closeDuringConfirmation && !operationModeSaveFailure)
                {
                    TestUiDispatcherHost.AwaitTaskOnDispatcher(
                        restartEntered.Task,
                        "MainWindowViewHostTests.mode-restart-start");
                }
                if (restartFailure != null)
                {
                    TestUiDispatcherHost.AwaitTaskOnDispatcher(
                        notificationEntered.Task,
                        "MainWindowViewHostTests.mode-restart-failure-notification");
                    // 通知を await しない誤実装にも terminal を進める機会を与えてから確認します。
                    dispatcher.Invoke(DispatcherPriority.ApplicationIdle, new Action(() => { }));
                    Assert.IsFalse(lifetime.ShutdownRequested.Task.IsCompleted);
                    Assert.AreEqual(1, settingsSession.SaveCount);
                    if (notificationFailure != null)
                    {
                        notificationRelease.TrySetException(notificationFailure);
                    }
                    else
                    {
                        notificationRelease.TrySetResult(
                            notificationResult ?? UiDialogResult.FromMessageBoxResult(MessageBoxResult.OK));
                    }
                }
                TestUiDispatcherHost.AwaitTaskOnDispatcher(
                    lifetime.ShutdownRequested.Task,
                    "MainWindowViewHostTests.mode-restart-shutdown");

                Assert.IsTrue(dispatcherMarkerObserved);
                Assert.AreEqual(1, packageCallCount);
                Assert.AreEqual(1, settingsSession.SaveCount);
                Assert.AreEqual(closeDuringConfirmation || operationModeSaveFailure ? 0 : 1, lifetime.RestartCount);
                Assert.AreEqual(1, lifetime.RequestShutdownCount);
                if (notificationResult != null || notificationFailure != null)
                {
                    Assert.IsNotNull(reportedRestartFailure);
                }
                if (!closeDuringConfirmation && !operationModeSaveFailure)
                {
                    Assert.IsTrue(IndexOfEvent(events, "restart") < IndexOfEvent(events, "shutdown"));
                }
                Settings persisted = PortableSettingsPersistenceTests.OpenSettings(settingsPath);
                Assert.AreEqual(!closeDuringConfirmation && !operationModeSaveFailure, persisted.OperationModeLR2DB);
                Assert.AreEqual("history-initial", persisted.PlayHistorySelectedDisplayTargetIdentity);
                Assert.AreEqual(operationModeSaveFailure || closeDuringConfirmation, persisted.ShowScoreViewerRegisterConfirmMsg);
                Assert.AreEqual(restartFailure != null && !operationModeSaveFailure, dialogs.MessageShown);

                if (!windowClosed)
                {
                    window.Close();
                    windowClosed = true;
                }
            }
            finally
            {
                packageRelease.Set();
                playerRelease.Set();
                notificationRelease?.TrySetResult(
                    UiDialogResult.FromMessageBoxResult(MessageBoxResult.OK));
                settingsBlocker?.Dispose();
                if (observation != null)
                {
                    try
                    {
                        TestUiDispatcherHost.AwaitTaskOnDispatcher(observation, "MainWindowViewHostTests.mode-restart-observation-cleanup");
                    }
                    catch
                    {
                    }
                }
                CleanupViewHost(
                    window,
                    windowClosed,
                    viewModel,
                    hadPreviousViewModelResource,
                    previousViewModelResource);
            }
        });
    }

    private sealed class TestTemporaryDirectory : IDisposable
    {
        internal TestTemporaryDirectory(string prefix)
        {
            Path = System.IO.Path.Combine(
                System.IO.Path.GetTempPath(),
                prefix + "_" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(Path);
        }

        internal string Path { get; }

        public void Dispose()
        {
            if (Directory.Exists(Path))
            {
                Directory.Delete(Path, recursive: true);
            }
        }
    }

    private static int IndexOfEvent(IReadOnlyList<string> events, string value)
    {
        for (int index = 0; index < events.Count; index++)
        {
            if (string.Equals(events[index], value, StringComparison.Ordinal))
            {
                return index;
            }
        }
        return -1;
    }

    private sealed class FixedStartupLibraryFactory : IStartupLibraryFactory
    {
        private readonly BMSLibrary library;
        private readonly BMSPlaylist playlist;

        internal FixedStartupLibraryFactory(BMSLibrary library, BMSPlaylist playlist)
        {
            this.library = library ?? throw new ArgumentNullException(nameof(library));
            this.playlist = playlist ?? throw new ArgumentNullException(nameof(playlist));
        }

        public BMSLibrary CreateBmsLibrary(LibraryProfile libraryProfile) => library;

        public BMSPlaylist CreateBmsPlaylist(LibraryProfile libraryProfile, BMSLibrary library) => playlist;
    }

    private sealed class PersistentSettingsEditSession : ISettingsEditSession
    {
        private readonly SettingsEditSession inner;
        private readonly Action? beforeSave;

        internal PersistentSettingsEditSession(Settings values, Action? beforeSave = null)
        {
            inner = new SettingsEditSession(values ?? throw new ArgumentNullException(nameof(values)));
            this.beforeSave = beforeSave;
        }

        internal int SaveCount { get; private set; }

        internal int ModeSaveCount { get; private set; }

        public Settings Values => inner.Values;

        public void Reload()
        {
            inner.Reload();
        }

        public void Save()
        {
            SaveCount++;
            beforeSave?.Invoke();
            inner.Save();
        }

        public void SaveOperationModeForRestart(bool operationMode, string historyIdentity)
        {
            ModeSaveCount++;
            inner.SaveOperationModeForRestart(operationMode, historyIdentity);
        }
    }

    private sealed class AcceptedModeDialogService : IUiDialogService
    {
        private readonly TaskCompletionSource<bool>? messageEntered;
        private readonly TaskCompletionSource<UiDialogResult>? messageCompletion;
        private readonly UiDialogResult? messageResult;

        internal AcceptedModeDialogService(
            TaskCompletionSource<bool>? messageEntered = null,
            TaskCompletionSource<UiDialogResult>? messageCompletion = null,
            UiDialogResult? messageResult = null)
        {
            this.messageEntered = messageEntered;
            this.messageCompletion = messageCompletion;
            this.messageResult = messageResult;
        }

        internal int ConfirmationCount { get; private set; }

        internal Action? OnConfirmation { get; set; }

        internal bool MessageShown { get; private set; }

        public Task<UiDialogResult> ConfirmAsync(
            UiConfirmationRequest request,
            CancellationToken cancellationToken = default)
        {
            ConfirmationCount++;
            OnConfirmation?.Invoke();
            return Task.FromResult(UiDialogResult.FromMessageBoxResult(MessageBoxResult.OK));
        }

        public Task<UiDialogResult> ShowMessageAsync(
            UiMessageRequest request,
            CancellationToken cancellationToken = default)
        {
            MessageShown = true;
            messageEntered?.TrySetResult(true);
            if (messageCompletion != null)
            {
                return messageCompletion.Task;
            }
            return Task.FromResult(
                messageResult ?? UiDialogResult.FromMessageBoxResult(MessageBoxResult.OK));
        }

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

    private static bool CanEnterAudioOperation()
    {
        if (!Ribbit.Media.Audio.BassAudioRuntime.TryEnterAudioOperation(out var lease))
        {
            return false;
        }
        lease.Dispose();
        return true;
    }

    private sealed class FakeBmsPlayer : IBMSPlayer
    {
        private readonly Action? onClose;

        internal FakeBmsPlayer(Action? onClose = null)
        {
            this.onClose = onClose;
        }

        public event PropertyChangedEventHandler? PropertyChanged;

        public string ExePath { get; set; } = string.Empty;
        public TimeSpan Duration { get; set; }
        public TimeSpan CurrentTime { get; set; }
        public TimeSpan StopTime { get; set; }
        public TimeSpan BmsDuration { get; set; }
        public TimeSpan MusicDuration { get; set; }
        public int CurrentVoices { get; set; }
        public int MaxVoices { get; set; }
        public int NoteDensity { get; set; }
        public int NoteDensityMax { get; set; }
        public int Bpm { get; set; }
        public int MinBpm { get; set; }
        public int MaxBpm { get; set; }
        public double Total { get; set; }
        public int Combo { get; set; }
        public int Notes { get; set; }
        public int Measure { get; set; }
        public int LastMeasure { get; set; }
        public int CloseProcessCount { get; private set; }

        public void CloseProcess()
        {
            CloseProcessCount++;
            onClose?.Invoke();
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(CurrentTime)));
        }
        public Task PlayStart(string bmsFilePath, Action<object, EventArgs>? onExitEventHandler = null) => Task.CompletedTask;
        public void RestartPlayingBMSfile() { }
        public void PausePlayingBMSfileToggle() { }
        public void FastForwardPlayingBMSfileStart() { }
        public void FastForwardPlayingBMSfileEnd() { }
        public void FastBackwardPlayingBMSfileStart() { }
        public void FastBackwardPlayingBMSfileEnd() { }
        public void ShowInfo() { }
        public void ShowEffect() { }
        public void ChangePlayside() { }
        public void IncreaseHighSpeed() { }
        public void DecreaseHighSpeed() { }
        public void VolumeChanged() { }
    }

    private static void CleanupViewHost(
        MainWindow? window,
        bool windowClosed,
        MainWindowViewModel? viewModel,
        bool hadPreviousViewModelResource,
        object? previousViewModelResource)
    {
        try
        {
            if (window != null && !windowClosed)
            {
                window.Close();
            }
        }
        finally
        {
            try
            {
                // SettingsDialog subscribes to the process-wide ResourceService. Its lifetime must
                // not extend past this dispatcher-owned view host or later culture changes can
                // update a CollectionView from a different MSTest worker thread.
                viewModel?.SettingDialog.Dispose();
            }
            finally
            {
                RestoreViewModelResource(hadPreviousViewModelResource, previousViewModelResource);
            }
        }
    }

    private sealed class RecordingSettingsEditSession : ISettingsEditSession
    {
        public void SaveOperationModeForRestart(bool operationMode, string historyIdentity)
        {
            Values.OperationModeLR2DB = operationMode;
            Values.PlayHistorySelectedDisplayTargetIdentity = historyIdentity;
            Save();
            Reload();
        }

        private readonly Action? beforeSave;

        internal RecordingSettingsEditSession(Settings values, Action? beforeSave = null)
        {
            Values = values ?? throw new ArgumentNullException(nameof(values));
            this.beforeSave = beforeSave;
        }

        internal int SaveCount { get; private set; }

        internal double TreeViewWidthAtSave { get; private set; } = double.NaN;

        public Settings Values { get; }

        public void Reload()
        {
        }

        public void Save()
        {
            SaveCount++;
            TreeViewWidthAtSave = Values.TreeViewWidth;
            beforeSave?.Invoke();
        }
    }

    private sealed class RecordingApplicationLifetime : IApplicationLifetimePort
    {
        private readonly List<string>? events;
        private readonly Action? onShutdown;
        private readonly Func<Task>? restartApplication;

        internal RecordingApplicationLifetime(
            List<string>? events = null,
            Action? onShutdown = null,
            Func<Task>? restartApplication = null)
        {
            this.events = events;
            this.onShutdown = onShutdown;
            this.restartApplication = restartApplication;
        }

        internal TaskCompletionSource<bool> ShutdownRequested { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        internal int RequestShutdownCount { get; private set; }

        internal int RestartCount { get; private set; }

        internal int CoordinatedShutdownStartCount { get; private set; }

        public bool IsFirstStartup => false;

        public void CompleteFirstStartup()
        {
        }

        public void MarkCoordinatedShutdownStarted(string reason)
        {
            CoordinatedShutdownStartCount++;
        }

        public void RequestShutdown()
        {
            RequestShutdownCount++;
            onShutdown?.Invoke();
            events?.Add("application-shutdown");
            ShutdownRequested.TrySetResult(true);
        }

        public Task RestartApplicationAsync()
        {
            RestartCount++;
            events?.Add("restart");
            return restartApplication?.Invoke() ?? Task.CompletedTask;
        }
    }
}
