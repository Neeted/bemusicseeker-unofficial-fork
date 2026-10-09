using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Data;
using System.Windows.Threading;
using BeMusicSeeker.Models;
using BeMusicSeeker.Models.BmsLibraryInternal;
using BeMusicSeeker.Models.Utils;
using BeMusicSeeker.Properties;
using BeMusicSeeker.ViewModels;
using BeMusicSeeker.Views;
using BeMusicSeeker.Views.Dialogs;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using NLog;
using NLog.Config;
using NLog.Targets;
using Ribbit.Logging;
using Ribbit.Media.Audio;

namespace BeMusicSeeker.Tests;

[TestClass]
[DoNotParallelize]
public sealed class MainWindowViewHostTests
{
    /// <summary>実receiverのawait後の通知失敗を報告し、元の操作失敗と受付終端、Dispatcherと次要求を維持します。</summary>
    [TestMethod]
    public async Task PackageInstallAsyncNotificationFailure_IsReportedWithoutEndingDispatcherAndNextRequestSucceeds()
    {

        using var directory = new TestTemporaryDirectory("package-notification-terminal");
        string source = Path.Combine(directory.Path, "source");
        Directory.CreateDirectory(source);
        string songDbPath = Path.Combine(directory.Path, "song.db");
        var originalFailure = new InvalidDataException("installation failed");
        var notificationFailure = new IOException("notification failed after await");
        var dialogEntered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var dialogRelease = new TaskCompletionSource<UiDialogResult>(TaskCreationOptions.RunContinuationsAsynchronously);
        var operationFailed = new TaskCompletionSource<PackageInstallFailure>(TaskCreationOptions.RunContinuationsAsynchronously);
        var capture = new PackageNotificationFailureTarget();
        var dialogs = new FileDbReportRecordingDialogs
        {
            MessageHandler = _ =>
            {
                dialogEntered.TrySetResult();
                return dialogRelease.Task;
            }
        };
        Settings settings = MainWindowViewModelTestFactory.CreateIsolatedSettings(values =>
            {
                values.OperationModeLR2DB = false;
            });
        int installationCount = 0;
        int failureCount = 0;
        MainWindowViewModel? viewModel = null;
        ApplicationComposition? composition = null;
        LoggingConfiguration? previousConfiguration = LogManager.Configuration;
        try
        {
            // process共通のログ設定はDoNotParallelizeのfixtureだけで差し替え、finallyで戻します。
            NLogWrapper.ConfigureApplicationFileLogging(directory.Path, LogLevel.Info, enableInstallPerformanceLogging: false);
            var configuration = new LoggingConfiguration();
            configuration.AddRule(LogLevel.Error, LogLevel.Fatal, capture);
            LogManager.Configuration = configuration;
            TestUiDispatcherHost.Invoke(() =>
            {
                using (var db = new BeMusicSeeker.Models.LR2.LR2SongDBExtended(songDbPath)) { }
                Dispatcher dispatcher = Dispatcher.CurrentDispatcher;
                composition = new ApplicationComposition(
                    settingsEditSession: new RecordingSettingsEditSession(settings),
                    defaultBmsPlayerFactory: () => new FakeBmsPlayer(),
                    uiScheduler: new WpfUiScheduler(() => dispatcher),
                    applicationLifetime: TestApplicationContext.CreateLifetime(),
                    cultureCatalog: TestApplicationContext.CreateCultureCatalog(),
                    fileDbMutationDialogService: dialogs,
                    packageInstallMutationPort: new DelegatePackageInstallMutationPort((_, _, _, _) =>
                    {
                        if (Interlocked.Increment(ref installationCount) == 1) { throw originalFailure; }
                        return new PackageInstallCommandResult([], null);
                    }));
                viewModel = composition.CreateMainWindowViewModel();
                BMSLibrary library = composition.CreateBmsLibrary(new LibraryProfile(false, songDbPath, [],
                    () => null!, null, false, false, false, false, "notification-terminal"));
                viewModel.PackageInstallWorkflow.AttachLibrary(library);
                viewModel.PackageInstallWorkflow.FailurePublished += failure =>
                {
                    failureCount++;
                    operationFailed.TrySetResult(failure);
                };
                Assert.IsTrue(viewModel.PackageInstallWorkflow.TryEnqueue(new DroppedInstallBatchRequest([source])));
            });
            Assert.IsNotNull(viewModel);
            Assert.IsNotNull(composition);
            // 操作idle後に別途予約するUI通知は、receiver自身の失敗報告と競合させて観測します。
            Task failurePublication = await Task.WhenAny(operationFailed.Task, capture.Reported.Task);
            if (failurePublication == capture.Reported.Task) { throw await capture.Reported.Task; }
            PackageInstallFailure failure = await operationFailed.Task;
            Task notificationArrival = await Task.WhenAny(dialogEntered.Task, capture.Reported.Task);
            if (notificationArrival == capture.Reported.Task) { throw await capture.Reported.Task; }
            await dialogEntered.Task;
            await viewModel.PackageInstallWorkflow.WaitForIdleAsync();
            Assert.AreSame(originalFailure, failure.Exception);
            Assert.IsFalse(composition.OperationAdmission.IsActive);
            Assert.IsFalse(capture.Reported.Task.IsCompleted);

            // 同期throwではなく、receiverがawaitした後に失敗する通知境界を通します。
            dialogRelease.SetException(notificationFailure);
            Assert.AreSame(notificationFailure, await capture.Reported.Task);
            TestUiDispatcherHost.Invoke(() =>
                Assert.IsTrue(viewModel.PackageInstallWorkflow.TryEnqueue(new DroppedInstallBatchRequest([source]))));
            await viewModel.PackageInstallWorkflow.WaitForIdleAsync();
            TestUiDispatcherHost.Invoke(() => Assert.AreEqual(1, failureCount));
            Assert.AreEqual(2, installationCount);
            Assert.IsFalse(composition.OperationAdmission.IsActive);
        }
        finally
        {
            dialogRelease.TrySetResult(UiDialogResult.FromMessageBoxResult(MessageBoxResult.OK));
            if (viewModel != null)
            {
                await viewModel.PackageInstallWorkflow.WaitForIdleAsync();
                TestUiDispatcherHost.Invoke(() =>
                {
                    TestUiDispatcherHost.AwaitTaskOnDispatcher(
                        viewModel.ShellShutdownWorkflow.RequestWindowCloseAsync(), "package-notification-cleanup");
                    viewModel.SettingDialog.Dispose();
                    viewModel.RegularChartList.Dispose();
                });
            }
            LogManager.Flush();
            LogManager.Configuration = previousConfiguration;
        }
    }

    private sealed class PackageNotificationFailureTarget : Target
    {
        internal TaskCompletionSource<Exception> Reported { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        protected override void Write(LogEventInfo logEvent)
        {
            if (logEvent.Message == "package_install_workflow_notification_failed" && logEvent.Exception != null)
            {
                Reported.TrySetResult(logEvent.Exception);
            }
        }
    }

    [DataTestMethod]
    [DataRow(false, false)]
    [DataRow(true, false)]
    [DataRow(false, true)]
    [DoNotParallelize]
    public void MainWindowShutdownCapturePrecedesShellCompletion(bool deferSettingsPresentation, bool failShutdownNotification)
    {
        const double initialTreeViewWidth = 281d;
        const double capturedTreeViewWidth = 347d;
        Settings settings = CreateSettings(
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
        var notificationFailure = new InvalidOperationException("terminal shutdown failed before its success notification");
        var applicationLifetime = new RecordingApplicationLifetime(events,
            onShutdown: () => { if (failShutdownNotification) { throw notificationFailure; } });

        TestUiDispatcherHost.RunWindowTest(_ =>
        {
            MainWindowViewModel? viewModel = null;
            MainWindow? window = null;
            MainWindowTestLifetime? ownership = null;
            Exception? bodyFailure = null;
            try
            {
                ApplicationComposition composition = CreateComposition(settingsSession, applicationLifetime);
                viewModel = composition.CreateMainWindowViewModel();

                // Constructor activation is intentionally suppressed; this test exercises only
                // the unshown view-host construction and close terminal, never startup rendering.
                viewModel.StartupUpdateWorkflow.NotifyClosing();
                ownership = new MainWindowTestLifetime(viewModel, applicationLifetime.ShutdownRequested.Task);
                int settingsPresentationCount = 0;
                window = ownership.CreateWindow(() => new MainWindow(viewModel, settingsWindow =>
                {
                    settingsPresentationCount++;
                    settingsWindow.ContentRendered += (_, _) => settingsWindow.CloseForOwnerShutdown();
                }));
                window.Closed += (_, _) => events.Add("window-closed");

                ColumnDefinition treeColumn = GetNamedElement<ColumnDefinition>(window, "gridColumn0");
                treeColumn.Width = new GridLength(capturedTreeViewWidth, GridUnitType.Pixel);
                var settingsPresentation = (ISettingDialogPresentationPort)window;
                Assert.IsFalse(viewModel.ShellShutdownWorkflow.IsClosingOrClosed);
                if (deferSettingsPresentation)
                {
                    // 要求時は終了前、実行時は終了受付後になる順序を明示する。
                    settingsPresentation.OpenSettingsDialog(deferPresentation: true);
                }
                window.Close();
                Assert.IsTrue(viewModel.ShellShutdownWorkflow.IsClosingOrClosed);
                if (!deferSettingsPresentation)
                {
                    settingsPresentation.OpenSettingsDialog();
                }
                TestUiDispatcherHost.AwaitTaskOnDispatcher(
                    window.Dispatcher.InvokeAsync(() => { }, DispatcherPriority.Normal).Task,
                    "settings-presentation-after-shutdown");
                Assert.AreEqual(0, settingsPresentationCount);

                Task<ShellShutdownWorkflowCompletionReceipt> closeRequest =
                    viewModel.ShellShutdownWorkflow.RequestWindowCloseAsync();
                TestUiDispatcherHost.AwaitTaskOnDispatcher(
                    closeRequest,
                    "MainWindowViewHostTests.MainWindowShutdownCapturePrecedesShellCompletion.window-close");
                if (failShutdownNotification)
                {
                    Assert.AreSame(notificationFailure,
                        Assert.ThrowsException<InvalidOperationException>(() => ownership.Dispose()));
                    Assert.IsFalse(applicationLifetime.ShutdownRequested.Task.IsCompleted);
                    Assert.IsTrue(window.CloseCompletion.IsFaulted);
                    Assert.AreEqual(capturedTreeViewWidth, settingsSession.TreeViewWidthAtSave);
                    Assert.AreEqual(1, settingsSession.SaveCount);
                    CollectionAssert.AreEqual(new[] { "save", "window-closed" }, events);
                    return;
                }
                TestUiDispatcherHost.AwaitTaskOnDispatcher(TestUiDispatcherHost.AwaitNotificationAsync(applicationLifetime.ShutdownRequested.Task, window.CloseCompletion, "MainWindowViewHostTests.MainWindowShutdownCapturePrecedesShellCompletion.application-shutdown"), "MainWindowViewHostTests.MainWindowShutdownCapturePrecedesShellCompletion.application-shutdown");

                Assert.AreEqual(capturedTreeViewWidth, settingsSession.TreeViewWidthAtSave);
                Assert.AreEqual(1, settingsSession.SaveCount);
                CollectionAssert.AreEqual(
                    new[] { "save", "application-shutdown" },
                    events);
                Assert.AreEqual(1, applicationLifetime.RequestShutdownCount);

                // The test lifetime is deliberately non-terminating. Complete the actual Window
                // close after the shell has requested application termination.
                window.Close();

                CollectionAssert.AreEqual(
                    new[] { "save", "application-shutdown", "window-closed" },
                    events);
            }
            catch (Exception exception)
            {
                bodyFailure = exception;
                throw;
            }
            finally
            {
                ownership?.DisposeAfterBodyFailure(bodyFailure);
            }
        });
    }

    [TestMethod]
    [DoNotParallelize]
    public void MainWindowViewSettingsBindAndCaptureThroughHostStore()
    {
        const double initialTreeViewWidth = 286d;
        const double capturedTreeViewWidth = 361d;
        const double rowHeight = 24d;
        const double headerHeight = 27d;
        const double fontSize = 14d;
        Settings settings = CreateSettings(
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
            MainWindowTestLifetime? ownership = null;
            Exception? bodyFailure = null;
            try
            {
                ApplicationComposition composition = CreateComposition(settingsSession, applicationLifetime);
                viewModel = composition.CreateMainWindowViewModel();
                // A pending selection is a valid startup setting, but the constructor-only test
                // keeps the corresponding selection handler behind the existing startup gate.
                viewModel.ProgressHub.StartupProgress.SetStartupUiInteractionBlocked(true);
                viewModel.StartupUpdateWorkflow.NotifyClosing();
                ownership = new MainWindowTestLifetime(viewModel, applicationLifetime.ShutdownRequested.Task);
                window = ownership.CreateWindow(() => new MainWindow(viewModel));
                TestUiDispatcherHost.ProcessQueuedPresentation();

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
                TestUiDispatcherHost.AwaitTaskOnDispatcher(TestUiDispatcherHost.AwaitNotificationAsync(applicationLifetime.ShutdownRequested.Task, window.CloseCompletion, "MainWindowViewHostTests.MainWindowViewSettingsBindAndCaptureThroughHostStore.application-shutdown"), "MainWindowViewHostTests.MainWindowViewSettingsBindAndCaptureThroughHostStore.application-shutdown");

                Assert.AreEqual(capturedTreeViewWidth, settingsSession.TreeViewWidthAtSave);
                Assert.AreEqual(capturedTreeViewWidth, settings.TreeViewWidth);
                Assert.AreEqual(1, settingsSession.SaveCount);

                window.Close();
            }
            catch (Exception exception)
            {
                bodyFailure = exception;
                throw;
            }
            finally
            {
                ownership?.DisposeAfterBodyFailure(bodyFailure);
            }
        });
    }

    [TestMethod]
    [DoNotParallelize]
    public void MainWindowConstructorOnlyPresentsInitialSetupThroughCompiledOverlay()
    {
        Settings settings = CreateSettings(
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
            MainWindowTestLifetime? ownership = null;
            Exception? bodyFailure = null;
            try
            {
                ApplicationComposition composition = CreateComposition(settingsSession, applicationLifetime);
                viewModel = composition.CreateMainWindowViewModel();
                viewModel.StartupUpdateWorkflow.NotifyClosing();
                viewModel.ProgressHub.StartupProgress.SetStartupUiInteractionBlocked(false);
                ownership = new MainWindowTestLifetime(viewModel, applicationLifetime.ShutdownRequested.Task);

                window = ownership.CreateWindow(() => new MainWindow(viewModel));
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
                TestUiDispatcherHost.ProcessQueuedPresentation();
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
                TestUiDispatcherHost.AwaitTaskOnDispatcher(TestUiDispatcherHost.AwaitNotificationAsync(applicationLifetime.ShutdownRequested.Task, window.CloseCompletion, "overlay-terminal-shutdown"), "overlay-terminal-shutdown");
                window.Close();

                viewModel.SettingDialog.RequestInitialSetupLanguageDialog();
                Assert.AreEqual(
                    Visibility.Hidden,
                    overlay.Visibility,
                    "A detached presentation port must not show the compiled overlay again.");
            }
            catch (Exception exception)
            {
                bodyFailure = exception;
                throw;
            }
            finally
            {
                ownership?.DisposeAfterBodyFailure(bodyFailure);
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
        Settings settings = MainWindowViewModelTestFactory.CreateIsolatedSettings(values =>
            {
                values.TreeViewWidth = treeViewWidth;
                values.StartupSelectInstallPending = startupSelectInstallPending;
                values.CustomTableRowHeight = customTableRowHeight;
                values.CustomTableHeaderHeight = customTableHeaderHeight;
                values.CustomTableFontSize = customTableFontSize;
            });
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

    private static Settings CreatePlaybackHostSettings(string directory)
    {
        var settings = (Settings)System.Configuration.SettingsBase.Synchronized(
            PortableSettingsPersistenceTests.OpenSettings(Path.Combine(directory, "user.config")));
        settings.OperationModeLR2DB = false;
        settings.BMSRootPath = directory;
        settings.StandaloneBmsRootPaths = directory;
        settings.BMSInstallDir = directory;
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
        settings.Save();
        string songDbPath = Path.Combine(directory, "song.db");
        StartupLibraryConstructionTestSupport.CreateSongDatabase(songDbPath);
        PlaylistPersistenceRepository.EnsureSchema(songDbPath);
        return settings;
    }

    private static MainWindowViewModel CreatePlaybackHostViewModel(
        ApplicationComposition composition, Settings settings, string directory,
        Func<BeatorajaBmtOptionsSnapshot>? bmtOptionsProvider = null,
        IStartupLibraryInitializationFailurePresenter? failurePresenter = null,
        StartupLibraryMutationPreparation? mutationPreparation = null)
    {
        string songDbPath = Path.Combine(directory, "song.db");
        var library = new TestBmsLibrary(songDbPath, getLR2Config: null, _lr2ScoreDB: null,
            fileMutationService: null, dialogService: null,
            uiScheduler: new WpfUiScheduler(() => Dispatcher.CurrentDispatcher),
            optionsSnapshotProvider: mutationPreparation == null
                ? () => BmsLibraryOptionsSnapshot.CreateCurrent(settings)
                : mutationPreparation.CaptureOptions,
            operationAdmission: composition.OperationAdmission,
            playlistOperationAdmission: composition.PlaylistOperationAdmission);
        if (mutationPreparation != null) { mutationPreparation.Library = library; }
        var playlist = new TestBmsPlaylist(
            new BmsPlaylistLibraryBindings(library), songDbPath,
            () => CustomFolderOutputSettingsSnapshot.CreateCurrent(settings),
            playlistUrlCompletionOptionsProvider: () => PlaylistUrlCompletionOptionsSnapshot.CreateCurrent(settings),
            beatorajaBmtOptionsProvider: bmtOptionsProvider ?? (() => BeatorajaBmtOptionsSnapshot.CreateCurrent(settings)));
        return new MainWindowViewModel(composition, new FixedStartupLibraryFactory(library, playlist),
            startupLibraryInitializationFailurePresenter: failurePresenter);
    }

    /// <summary>後続要求を開始するfixture準備として、実起動が受理したL保守のwork終端だけを回収します。</summary>
    private sealed class StartupLibraryMutationPreparation(Settings settings)
    {
        private readonly object syncRoot = new();
        private readonly List<Task> acceptedMutationTasks = [];
        private bool trackingAttached;

        /// <summary>既存options捕捉の時点で実schedulerへ接続する対象です。</summary>
        internal TestBmsLibrary? Library { private get; set; }

        /// <summary>実初期化が接続したschedulerを維持し、登録前の既存入力境界で保守を追跡します。</summary>
        internal BmsLibraryOptionsSnapshot CaptureOptions()
        {
            lock (syncRoot)
            {
                if (!trackingAttached && Library?.StartupBackgroundTaskScheduler is { } scheduler)
                {
                    trackingAttached = true;
                    Library.StartupBackgroundTaskScheduler = (name, reason, dependency, work) =>
                    {
                        if (name is not ("chart_info_hydration" or "maintenance_hydration" or "installable_maintenance"))
                        {
                            return scheduler(name, reason, dependency, work);
                        }
                        var terminal = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
                        bool accepted = scheduler(name, reason, dependency, async () =>
                        {
                            try { await work(); terminal.TrySetResult(); }
                            catch (Exception failure) { terminal.TrySetException(failure); throw; }
                        });
                        if (accepted) { lock (syncRoot) { acceptedMutationTasks.Add(terminal.Task); } }
                        return accepted;
                    };
                }
            }
            return BmsLibraryOptionsSnapshot.CreateCurrent(settings);
        }

        /// <summary>初期化の登録終端後、受理済み3種類の保守をlease解放まで回収します。GC等は含めません。</summary>
        internal void JoinAcceptedMutations()
        {
            Task[] captured;
            lock (syncRoot)
            {
                Assert.IsTrue(trackingAttached, "実初期化のscheduler登録前に保守の回収を接続します。");
                captured = acceptedMutationTasks.ToArray();
            }
            // 初期化Taskの終端は独立保守の終端ではないため、後続要求の準備を別に所有します。
            TestUiDispatcherHost.AwaitTaskOnDispatcher(Task.WhenAll(captured), "startup-library-mutation-preparation");
        }
    }

    private static void ShowPlaybackHostMainWindow(TestWindowPresentationScope scope, MainWindow window)
    {
        // 保存配置の復元がLoadedの画面外配置を後から更新するため、既存MainWindow表示テストと同じRender入口で再配置します。
        RoutedEventHandler position = (_, _) => window.Dispatcher.BeginInvoke(DispatcherPriority.Render, new Action(() =>
        {
            window.Left = SystemParameters.VirtualScreenLeft + SystemParameters.VirtualScreenWidth * 4d + 4096d;
            window.Top = SystemParameters.VirtualScreenTop + SystemParameters.VirtualScreenHeight * 4d + 4096d;
        }));
        window.Loaded += position;
        try { scope.ShowAndWaitForContentRendered(window); }
        finally { window.Loaded -= position; }
    }

    [TestMethod]
    [DoNotParallelize]
    public void MainWindowRenderedInitializationAttachesCreatedPlaybackHost()
    {
        using var directory = new TestTemporaryDirectory("main-window-playback-host");
        Settings settings = CreatePlaybackHostSettings(directory.Path);
        TestUiDispatcherHost.RunWindowTest(scope =>
        {
            MainWindowViewModel? viewModel = null;
            MainWindow? window = null;
            bool closed = false;
            var windowClosed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            MainWindowTestLifetime? ownership = null;
            Exception? bodyFailure = null;
            var player = new FakeBmsPlayer();
            var lifetime = new RecordingApplicationLifetime();
            try
            {
                var composition = new ApplicationComposition(
                    settingsEditSession: new PersistentSettingsEditSession(settings),
                    defaultBmsPlayerFactory: () => player,
                    uiScheduler: new WpfUiScheduler(() => Dispatcher.CurrentDispatcher),
                    applicationLifetime: lifetime,
                    cultureCatalog: TestApplicationContext.CreateCultureCatalog());
                viewModel = CreatePlaybackHostViewModel(composition, settings, directory.Path);
                viewModel.StartupUpdateWorkflow.NotifyClosing();
                ownership = new MainWindowTestLifetime(viewModel, lifetime.ShutdownRequested.Task);
                window = ownership.CreateWindow(() => new MainWindow(viewModel));
                window.Closed += (_, _) =>
                {
                    closed = true;
                    windowClosed.TrySetResult();
                };
                Assert.IsFalse(viewModel.IsInitializationCompleted);
                Assert.IsFalse(player.HostAttached.Task.IsCompleted);
                ShowPlaybackHostMainWindow(scope, window);
                TestUiDispatcherHost.AwaitTaskOnDispatcher(player.HostAttached.Task, "rendered-playback-host-attachment");
                Assert.IsTrue(viewModel.IsInitializationCompleted);
                PlaybackPanelView playbackView = GetNamedElement<PlaybackPanelView>(window, "playbackPanelView");
                var host = (ExternalPlayerHwndHost)playbackView.FindName("externalPlayerHost");
                Assert.AreEqual(Visibility.Collapsed, host.Visibility);
                ExternalPlayerHostObservation.AssertOwnedChild(host.Handle, window);
                Assert.IsNotNull(player.WindowHost);
                Assert.AreEqual(host.Handle, player.WindowHost.ParentHandle.NativeValue);
                window.Close();
                TestUiDispatcherHost.AwaitTaskOnDispatcher(TestUiDispatcherHost.AwaitNotificationAsync(lifetime.ShutdownRequested.Task, window.CloseCompletion, "rendered-playback-host-shutdown"), "rendered-playback-host-shutdown");
                window.Close();
                TestUiDispatcherHost.AwaitPresentationOnDispatcher(windowClosed.Task, "rendered-playback-host-window-closed");
                Assert.IsTrue(closed);
            }
            catch (Exception exception)
            {
                bodyFailure = exception;
                throw;
            }
            finally
            {
                ownership?.DisposeAfterBodyFailure(bodyFailure);
            }
        });
    }

    [TestMethod]
    [DoNotParallelize]
    public void MainWindowCloseDuringRequiredStartupBmtCancellationJoinsAndDoesNotPresentLateFailure()
    {
        using var directory = new TestTemporaryDirectory("main-window-startup-bmt-close");
        Settings settings = CreatePlaybackHostSettings(directory.Path);
        using var releaseBmtPreparation = new ManualResetEventSlim();
        var bmtPreparationEntered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        TestUiDispatcherHost.RunWindowTest(scope =>
        {
            MainWindowViewModel? viewModel = null;
            MainWindow? window = null;
            Task? initialization = null;
            Task? shutdown = null;
            Exception? bodyFailure = null;
            bool closed = false;
            var windowClosed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            MainWindowTestLifetime? ownership = null;
            Window[] baselineWindows = Application.Current.Windows.Cast<Window>().ToArray();
            var failurePresenter = new MainWindowViewModelStartupProgressTests.RecordingStartupLibraryInitializationFailurePresenter();
            var lifetime = new RecordingApplicationLifetime();
            try
            {
                var composition = new ApplicationComposition(
                    settingsEditSession: new NoOpSettingsEditSession(settings),
                    uiScheduler: new WpfUiScheduler(() => TestUiDispatcherHost.Dispatcher),
                    applicationLifetime: lifetime,
                    cultureCatalog: TestApplicationContext.CreateCultureCatalog());
                viewModel = CreatePlaybackHostViewModel(composition, settings, directory.Path,
                    bmtOptionsProvider: () =>
                    {
                        // 必須hydrateの非同期継続で実BMT入力を捕捉する境界を止めます。
                        Assert.IsFalse(TestUiDispatcherHost.Dispatcher.CheckAccess());
                        bmtPreparationEntered.TrySetResult();
                        releaseBmtPreparation.Wait();
                        return BeatorajaBmtOptionsSnapshot.CreateCurrent(settings);
                    }, failurePresenter: failurePresenter);
                viewModel.StartupUpdateWorkflow.NotifyClosing();
                ownership = new MainWindowTestLifetime(viewModel, lifetime.ShutdownRequested.Task);
                window = ownership.CreateWindow(() => new MainWindow(viewModel));
                window.Closed += (_, _) =>
                {
                    closed = true;
                    windowClosed.TrySetResult();
                };
                initialization = viewModel.ShellActivationWorkflow.ActivateRenderedShell(
                    () => { }, action => action(), () => false);
                ShowPlaybackHostMainWindow(scope, window);
                TestUiDispatcherHost.AwaitTaskOnDispatcher(
                    TestUiDispatcherHost.AwaitNotificationAsync(bmtPreparationEntered.Task, initialization, "startup-bmt-preparation"),
                    "startup-bmt-preparation-entered");
                Assert.IsFalse(initialization.IsCompleted);
                window.Close();
                shutdown = viewModel.ShellShutdownWorkflow.RequestWindowCloseAsync();
                Assert.IsTrue(viewModel.ShellShutdownWorkflow.IsClosingOrClosed);
                Assert.IsFalse(closed);
                Assert.IsFalse(shutdown.IsCompleted);
                releaseBmtPreparation.Set();
                TestUiDispatcherHost.AwaitTaskOnDispatcher(initialization, "cancelled-startup-initialization-terminal");
                TestUiDispatcherHost.AwaitTaskOnDispatcher(shutdown, "cancelled-startup-close-preparation-terminal");
                TestUiDispatcherHost.AwaitTaskOnDispatcher(TestUiDispatcherHost.AwaitNotificationAsync(lifetime.ShutdownRequested.Task, window.CloseCompletion, "cancelled-startup-terminal-shutdown"), "cancelled-startup-terminal-shutdown");
                window.Close();
                TestUiDispatcherHost.AwaitPresentationOnDispatcher(windowClosed.Task, "cancelled-startup-window-closed");
                Assert.IsTrue(closed);
                Assert.IsTrue(initialization.IsCompletedSuccessfully);
                Assert.IsFalse(((Task<bool>)initialization).GetAwaiter().GetResult());
                Assert.IsFalse(viewModel.IsInitializationCompleted);
                Assert.IsTrue(viewModel.ProgressHub.StartupProgress.IsFailed);
                StringAssert.Contains(viewModel.ProgressHub.StartupProgress.SubLabel, "Playlist BMT output was cancelled by shutdown.");
                Assert.AreEqual(0, failurePresenter.Presentations.Count);
                Assert.AreEqual(1, lifetime.RequestShutdownCount);
                Assert.IsFalse(Application.Current.Windows.Cast<Window>().Except(baselineWindows).Any());
                Assert.IsTrue(composition.OperationAdmission.WaitForIdleAsync().IsCompletedSuccessfully);
                Assert.IsTrue(composition.PlaylistOperationAdmission.WaitForIdleAsync().IsCompletedSuccessfully);
            }
            catch (Exception exception)
            {
                bodyFailure = exception;
                throw;
            }
            finally
            {
                releaseBmtPreparation.Set();
                try
                {
                    if (initialization != null)
                    {
                        TestUiDispatcherHost.AwaitTaskOnDispatcher(initialization, "startup-bmt-finally-initialization");
                    }
                    if (shutdown != null)
                    {
                        TestUiDispatcherHost.AwaitTaskOnDispatcher(shutdown, "startup-bmt-finally-shutdown");
                    }
                }
                catch (Exception cleanupFailure) when (bodyFailure != null)
                {
                    bodyFailure.Data["StartupBmtJoinFailure"] = cleanupFailure.ToString();
                }
                finally
                {
                    try { ownership?.DisposeAfterBodyFailure(bodyFailure); }
                    catch (Exception cleanupFailure) when (bodyFailure != null)
                    {
                        bodyFailure.Data["StartupBmtWindowCleanupFailure"] = cleanupFailure.ToString();
                    }
                }
            }
        });
    }

    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    [DoNotParallelize]
    public void MainWindowPlayerDrainKeepsDispatcherResponsiveAndDefersTerminalClose(bool prepareForUpdate)
    {
        using var directory = new TestTemporaryDirectory("main-window-player-drain");
        Settings settings = CreatePlaybackHostSettings(directory.Path);
        settings.TreeViewWidth = 279d;
        settings.StartupSelectInstallPending = false;
        using var release = new ManualResetEventSlim();
        var packageEntered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var packageCompleted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var packageRelease = new ManualResetEventSlim();
        var entered = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var completed = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        TestUiDispatcherHost.RunWindowTest(scope =>
        {
            MainWindowViewModel? viewModel = null;
            MainWindow? window = null;
            bool closed = false;
            var windowClosed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            MainWindowTestLifetime? ownership = null;
            Exception? bodyFailure = null;
            Dispatcher dispatcher = Dispatcher.CurrentDispatcher;
            bool saveOnUi = false;
            bool audioActiveAtSave = false;
            bool audioInactiveAtShutdown = false;
            bool shutdownOnUi = false;
            bool capturedBeforePlayer = false;
            double treeWidthAtPlayerClose = double.NaN;
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
                treeWidthAtPlayerClose = settings.TreeViewWidth;
                capturedBeforePlayer = treeWidthAtPlayerClose == 359d;
                entered.TrySetResult(true);
                release.Wait();
                completed.TrySetResult(true);
            });
            var mutationPreparation = new StartupLibraryMutationPreparation(settings);
            Task? observation = null;
            Task? marker = null;
            var observedClose = new TaskCompletionSource<Task>(TaskCreationOptions.RunContinuationsAsynchronously);
            try
            {
                Ribbit.Media.Audio.BassAudioRuntime.Initialize();
                var startupPlayer = new FakeBmsPlayer();
                var composition = new ApplicationComposition(
                    settingsEditSession: session,
                    defaultBmsPlayerFactory: () => startupPlayer,
                    uiScheduler: new WpfUiScheduler(() => dispatcher),
                    applicationLifetime: lifetime,
                    cultureCatalog: TestApplicationContext.CreateCultureCatalog(),
                    packageInstallMutationPort: new DelegatePackageInstallMutationPort((_, _, _, _) =>
                    {
                        packageEntered.TrySetResult();
                        packageRelease.Wait();
                        packageCompleted.TrySetResult();
                        return new PackageInstallCommandResult([], null);
                    }));
                viewModel = CreatePlaybackHostViewModel(composition, settings, directory.Path,
                    mutationPreparation: mutationPreparation);
                viewModel.StartupUpdateWorkflow.NotifyClosing();
                ownership = new MainWindowTestLifetime(viewModel, lifetime.ShutdownRequested.Task);
                window = ownership.CreateWindow(() => new MainWindow(viewModel));
                window.Closed += (_, _) =>
                {
                    closed = true;
                    windowClosed.TrySetResult();
                };
                Task initialization = viewModel.ShellActivationWorkflow.ActivateRenderedShell(
                    () => { }, action => action(), () => false);
                ShowPlaybackHostMainWindow(scope, window);
                TestUiDispatcherHost.AwaitTaskOnDispatcher(
                    TestUiDispatcherHost.AwaitNotificationAsync(startupPlayer.HostAttached.Task, initialization, "player-drain.initialization-host"),
                    "player-drain-initialization");
                TestUiDispatcherHost.AwaitTaskOnDispatcher(initialization, "player-drain-initialization-terminal");
                mutationPreparation.JoinAcceptedMutations();
                PlaybackPanelView playbackView = GetNamedElement<PlaybackPanelView>(window, "playbackPanelView");
                IntPtr hostHandle = playbackView.PlayerHostHandle;
                ExternalPlayerHostObservation.AssertOwnedChild(hostHandle, window);
                ColumnDefinition treeColumn = GetNamedElement<ColumnDefinition>(window, "gridColumn0");
                treeColumn.Width = new GridLength(359d);
                // 終了時の捕捉は実幅を優先するため、利用者が操作を終えた実レイアウトを先に確定します。
                window.UpdateLayout();
                Assert.AreEqual(359d, treeColumn.ActualWidth, "停止順の観測前に変更した画面幅を確定します。");
                Task? packageIdle = null;
                if (!prepareForUpdate)
                {
                    Assert.IsTrue(viewModel.PackageInstallWorkflow.EnqueueSingle(Path.Combine(directory.Path, "pending.zip")), "受理済み起動保守の準備終端後に導入要求を受理します。");
                    packageIdle = viewModel.PackageInstallWorkflow.WaitForIdleAsync();
                    TestUiDispatcherHost.AwaitTaskOnDispatcher(Task.WhenAny(packageEntered.Task, packageIdle), "close-package-arrival");
                    Assert.IsTrue(packageEntered.Task.IsCompleted);
                    Assert.IsFalse(packageIdle.IsCompleted);
                    Assert.IsTrue(composition.OperationAdmission.IsActive);
                }
                if (prepareForUpdate)
                {
                    TestUiDispatcherHost.AwaitTaskOnDispatcher(viewModel.ShellShutdownWorkflow.PrepareForStartupUpdateAsync("update"), "update-preparation");
                }
                // 導入の準備停止が済んで実workerへ到達した後に、終端停止を保持するplayerを接続する。
                TestUiDispatcherHost.AwaitTaskOnDispatcher(viewModel.PlaybackPanel.ReplacePlayerAsync(player), "attach-player");
                observation = Task.Run(async () =>
                {
                    try
                    {
                        await TestUiDispatcherHost.AwaitNotificationAsync(entered.Task, observedClose.Task.Unwrap(), "player-drain.close-player");
                        marker = dispatcher.InvokeAsync(() =>
                        {
                            Assert.IsFalse(completed.Task.IsCompleted);
                            Assert.IsTrue(CanEnterAudioOperation(), "player drain 中は audio runtime を解放しない。");
                            Assert.AreEqual(0, session.SaveCount);
                            Assert.AreEqual(0, lifetime.RequestShutdownCount);
                            Assert.IsFalse(closed);
                            Assert.IsTrue(ExternalPlayerHostObservation.IsWindow(hostHandle));
                            Assert.IsFalse(viewModel.PlaybackPanel.NextCommand.CanExecute, "終了開始後は次の再生操作を受け付けない。");
                            Assert.IsFalse(viewModel.PlaybackPanel.StartCommand.CanExecute);
                            Task first = viewModel.ShellShutdownWorkflow.CompleteTerminalShutdownAsync();
                            Task second = viewModel.ShellShutdownWorkflow.CompleteTerminalShutdownAsync();
                            Assert.AreSame(first, second);
                            Assert.IsFalse(first.IsCompleted);
                            window.Close();
                            Assert.IsFalse(closed, "準備完了だけで再入 Close を通さない。");
                        }).Task;
                        await marker;
                    }
                    finally
                    {
                        // UI が同期 close で停止する mutant でも外部 coordinator が必ず解放する。
                        release.Set();
                    }
                });
                window.Close();
                observedClose.TrySetResult(window.CloseCompletion);
                Assert.IsFalse(composition.OperationAdmission.TryEnter(out _), "Close開始時に新規L受付を閉じます。");
                Assert.IsFalse(composition.PlaylistOperationAdmission.TryEnter(out _), "Close開始時に新規P受付を閉じます。");
                if (packageIdle != null)
                {
                    Assert.IsFalse(entered.Task.IsCompleted, "追跡導入の実終端前にplayer終端へ進みません。");
                    Assert.AreEqual(0, session.SaveCount);
                    Assert.AreEqual(0, lifetime.RequestShutdownCount);
                    Assert.IsFalse(closed);
                    packageRelease.Set();
                    TestUiDispatcherHost.AwaitTaskOnDispatcher(packageIdle, "close-package-terminal");
                    Assert.IsTrue(packageCompleted.Task.IsCompleted);
                }
                TestUiDispatcherHost.AwaitTaskOnDispatcher(observation, "player-drain-marker");
                TestUiDispatcherHost.AwaitTaskOnDispatcher(TestUiDispatcherHost.AwaitNotificationAsync(lifetime.ShutdownRequested.Task, window.CloseCompletion, "terminal-shutdown"), "terminal-shutdown");
                Assert.IsTrue(capturedBeforePlayer, $"player停止入口の捕捉幅={treeWidthAtPlayerClose}、指定幅=359。");
                Assert.IsTrue(playerCompletedBeforeSave);
                Assert.IsTrue(saveOnUi);
                Assert.IsTrue(audioActiveAtSave);
                Assert.IsTrue(audioInactiveAtShutdown);
                Assert.IsTrue(shutdownOnUi);
                Assert.AreEqual(1, session.SaveCount);
                Assert.AreEqual(1, player.CloseProcessCount);
                Assert.AreEqual(1, lifetime.RequestShutdownCount);
                Assert.IsFalse(composition.OperationAdmission.TryEnter(out _), "実終端後もL受付を再開しません。");
                Assert.IsFalse(composition.PlaylistOperationAdmission.TryEnter(out _), "実終端後もP受付を再開しません。");
                window.Close();
                TestUiDispatcherHost.AwaitPresentationOnDispatcher(windowClosed.Task, "player-drain-window-closed");
                Assert.IsTrue(closed);
                Assert.IsFalse(ExternalPlayerHostObservation.IsWindow(hostHandle));
                Assert.AreEqual(1, player.CloseProcessCount);
                Assert.AreEqual(1, session.SaveCount);
            }
            catch (Exception exception)
            {
                bodyFailure = exception;
                throw;
            }
            finally
            {
                observedClose.TrySetResult(window?.CloseCompletion ?? Task.CompletedTask);
                packageRelease.Set();
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
                        TestUiDispatcherHost.AwaitTaskOnDispatcher(viewModel.PackageInstallWorkflow.WaitForIdleAsync(), "close-package-cleanup");
                    }

                }
                finally
                {
                    try { ownership?.DisposeAfterBodyFailure(bodyFailure); }
                    finally { Ribbit.Media.Audio.BassAudioRuntime.Shutdown(); }
                }
            }
        });
    }

    /// <summary>実導入中のmode保存はBusy通知で終端し、編集済みdraftとXMLを保ち、元worker終端後の明示保存だけを実行します。</summary>
    [DataTestMethod]
    [DataRow(false)]
    [DataRow(true)]
    [DoNotParallelize]
    public void MainWindowOperationModeSaveWhilePackageRunsReportsBusyAndRetainsDraft(bool saveBlocked)
    {
        using var directory = new TestTemporaryDirectory("mode-save-busy");
        Settings settings = CreatePlaybackHostSettings(directory.Path);
        settings.ShowScoreViewerRegisterConfirmMsg = false;
        settings.Save();
        string settingsPath = Path.Combine(directory.Path, "user.config");
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var release = new ManualResetEventSlim();
        TestUiDispatcherHost.RunWindowTest(_ =>
        {
            MainWindowViewModel? viewModel = null;
            FileStream? blocker = null;
            int workerCount = 0;
            var player = new FakeBmsPlayer();
            var lifetime = new RecordingApplicationLifetime();
            var session = new PersistentSettingsEditSession(settings);
            var dialogs = new AcceptedModeDialogService();
            var failures = new List<Exception>();
            try
            {
                var composition = new ApplicationComposition(
                    settingsEditSession: session,
                    defaultBmsPlayerFactory: () => player,
                    uiScheduler: new WpfUiScheduler(() => Dispatcher.CurrentDispatcher),
                    applicationLifetime: lifetime,
                    cultureCatalog: TestApplicationContext.CreateCultureCatalog(),
                    settingsDialogService: dialogs,
                    reportSettingsApplyFailure: failures.Add,
                    packageInstallMutationPort: new DelegatePackageInstallMutationPort((_, _, _, _) =>
                    {
                        Interlocked.Increment(ref workerCount);
                        entered.TrySetResult();
                        release.Wait();
                        return new PackageInstallCommandResult([], null);
                    }));
                viewModel = composition.CreateMainWindowViewModel();
                viewModel.StartupUpdateWorkflow.NotifyClosing();
                BMSLibrary library = composition.CreateBmsLibrary(new LibraryProfile(false,
                    Path.Combine(directory.Path, "song.db"), [], () => null!, null,
                    false, false, false, false, "mode-save-busy"));
                viewModel.PackageInstallWorkflow.AttachLibrary(library);
                viewModel.PackageInstallWorkflow.EnqueueSingle(Path.Combine(directory.Path, "pending.zip"));
                Task idle = viewModel.PackageInstallWorkflow.WaitForIdleAsync();
                TestUiDispatcherHost.AwaitTaskOnDispatcher(Task.WhenAny(entered.Task, idle), "mode-busy-worker-arrival");
                Assert.IsTrue(entered.Task.IsCompleted, "実workerへ到達せず操作が終端しました。");
                Assert.IsFalse(idle.IsCompleted);
                Assert.IsTrue(composition.OperationAdmission.IsActive);
                // 初回設定ではmodeはdraftであり、編集中も実導入の受付を解除しません。
                viewModel.SettingDialog.OperationModeLR2DB = true;
                viewModel.SettingDialog.ShowScoreViewerRegisterConfirmMsg = true;
                byte[] beforeSave = File.ReadAllBytes(settingsPath);
                bool beforeMode = settings.OperationModeLR2DB;
                int stopsBeforeSave = player.CloseProcessCount;
                if (saveBlocked) { blocker = new FileStream(settingsPath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite); }

                viewModel.SettingDialog.SaveOperationModeForRestart(viewModel.SettingDialog.OperationModeLR2DB);

                Assert.AreEqual(1, dialogs.Messages.Count);
                Assert.AreEqual(BeMusicSeeker.Properties.Resources.Warn_LibraryOperationBusy, dialogs.Messages.Single().MessageBoxText);
                Assert.AreEqual(0, dialogs.ConfirmationCount);
                Assert.AreEqual(0, session.ModeSaveCount);
                Assert.AreEqual(0, session.SaveCount);
                Assert.AreEqual(0, lifetime.RestartCount);
                Assert.AreEqual(0, lifetime.RequestShutdownCount);
                Assert.AreEqual(stopsBeforeSave, player.CloseProcessCount, "Busy保存は先行導入の停止に加えてplayerを停止しません。");
                Assert.AreEqual(0, failures.Count);
                Assert.IsFalse(viewModel.ShellShutdownWorkflow.IsShutdownPreparationStarted);
                Assert.AreEqual(beforeMode, settings.OperationModeLR2DB);
                Assert.IsTrue(viewModel.SettingDialog.OperationModeLR2DB);
                Assert.IsTrue(viewModel.SettingDialog.ShowScoreViewerRegisterConfirmMsg);
                CollectionAssert.AreEqual(beforeSave, File.ReadAllBytes(settingsPath));
                Assert.IsFalse(idle.IsCompleted);
                Assert.IsTrue(composition.OperationAdmission.IsActive);
                blocker?.Dispose();
                blocker = null;
                release.Set();
                TestUiDispatcherHost.AwaitTaskOnDispatcher(idle, "mode-busy-worker-terminal");
                Assert.AreEqual(1, workerCount);
                Assert.AreEqual(0, session.ModeSaveCount, "Busy保存は元処理の終端後も再実行しません。");
                Assert.IsFalse(composition.OperationAdmission.IsActive);
                viewModel.SettingDialog.SaveOperationModeForRestart(viewModel.SettingDialog.OperationModeLR2DB);
                Assert.AreEqual(1, session.ModeSaveCount);
                Assert.AreEqual(0, session.SaveCount);
                Assert.IsTrue(PortableSettingsPersistenceTests.OpenSettings(settingsPath).OperationModeLR2DB);
                Assert.IsFalse(PortableSettingsPersistenceTests.OpenSettings(settingsPath).ShowScoreViewerRegisterConfirmMsg);
            }
            finally
            {
                release.Set();
                blocker?.Dispose();
                if (viewModel != null)
                {
                    TestUiDispatcherHost.AwaitTaskOnDispatcher(viewModel.PackageInstallWorkflow.WaitForIdleAsync(), "mode-busy-worker-cleanup");
                    TestUiDispatcherHost.AwaitTaskOnDispatcher(viewModel.ShellShutdownWorkflow.RequestWindowCloseAsync(), "mode-busy-close");
                    viewModel.SettingDialog.Dispose();
                    viewModel.RegularChartList.Dispose();
                }
            }
        });
    }

    [TestMethod]
    [DoNotParallelize]
    public void MainWindowOperationModeRestartDrainsPlayerBeforeStartAndShutdown()
    {
        RunMainWindowOperationModeRestartScenario(restartFailure: null);
    }

    [TestMethod]
    [DoNotParallelize]
    public void MainWindowOperationModeRestartFailureAwaitsNotificationBeforeShutdown()
    {
        RunMainWindowOperationModeRestartScenario(
            new InvalidOperationException("replacement process could not start"));
    }

    [TestMethod]
    [DoNotParallelize]
    public void MainWindowOperationModeRestartNotificationFailureStillShutsDown()
    {
        RunMainWindowOperationModeRestartScenario(
            new InvalidOperationException("replacement process could not start"),
            UiDialogResult.NotShown(UiDialogStatus.OwnerUnavailable));
    }

    [TestMethod]
    [DoNotParallelize]
    public void MainWindowCloseDuringModeConfirmationPreservesEarlierCloseWithoutModeSave()
    {
        RunMainWindowOperationModeRestartScenario(restartFailure: null, closeDuringConfirmation: true);
    }

    [TestMethod]
    [DoNotParallelize]
    public void MainWindowOperationModeRestartNotificationExceptionStillShutsDown()
    {
        RunMainWindowOperationModeRestartScenario(
            new InvalidOperationException("restart failed"),
            notificationFailure: new InvalidOperationException("notification failed"));
    }

    [TestMethod]
    [DoNotParallelize]
    public void MainWindowOperationModeRestartSaveFailureRetainsSettingsWithoutShutdown()
    {
        RunMainWindowOperationModeRestartScenario(
            restartFailure: null,
            operationModeSaveFailure: true);
    }

    /// <summary>稼働profileのmode選択もL/P Busyではdraftを保ち、保存・確認・再起動を始めず、次の明示保存だけが成功します。</summary>
    [DataTestMethod]
    [DataRow("library")]
    [DataRow("playlist")]
    [DoNotParallelize]
    public void MainWindowOperationModeSelectionBusyRetainsDraftWithoutSavingOrRestarting(string admission)
        => RunMainWindowOperationModeRestartScenario(restartFailure: null, busyAdmission: admission);

    private static void RunMainWindowOperationModeRestartScenario(
        Exception? restartFailure,
        UiDialogResult? notificationResult = null,
        Exception? notificationFailure = null,
        bool closeDuringConfirmation = false,
        bool operationModeSaveFailure = false,
        string? busyAdmission = null)
    {
        using var directory = new TestTemporaryDirectory("main-window-mode-restart");
        string settingsPath = Path.Combine(directory.Path, "user.config");
        // 本番Settings.Defaultと同じく、UIのReloadと起動後の背景読取りが共有する同一instanceを同期化します。
        var settings = (Settings)System.Configuration.SettingsBase.Synchronized(
            PortableSettingsPersistenceTests.OpenSettings(settingsPath));
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
            MainWindowTestLifetime? ownership = null;
            Exception? bodyFailure = null;
            Task? observation = null;
            bool dispatcherMarkerObserved = false;
            var observedClose = new TaskCompletionSource<Task>(TaskCreationOptions.RunContinuationsAsynchronously);
            TestBmsLibrary? library = null;
            var mutationPreparation = new StartupLibraryMutationPreparation(settings);
            try
            {
                Dispatcher dispatcher = Dispatcher.CurrentDispatcher;
                var settingsDialogs = new AcceptedModeDialogService();
                ApplicationComposition composition = new(
                    settingsEditSession: settingsSession,
                    defaultBmsPlayerFactory: () => new FakeBmsPlayer(),
                    uiScheduler: new WpfUiScheduler(() => dispatcher),
                    applicationLifetime: lifetime,
                    cultureCatalog: TestApplicationContext.CreateCultureCatalog(),
                    settingsDialogService: settingsDialogs,
                    restartFailureDialogs: dialogs,
                    reportSettingsApplyFailure: exception =>
                    {
                        reportedSettingsFailures.Add(exception);
                        settingsSaveFailureReported.TrySetResult(exception);
                    },
                    reportRestartFailure: exception => reportedRestartFailure = exception);
                library = new TestBmsLibrary(
                    songDbPath,
                    getLR2Config: null,
                    _lr2ScoreDB: null,
                    fileMutationService: null, dialogService: null,
                    uiScheduler: new WpfUiScheduler(() => dispatcher),
                    optionsSnapshotProvider: mutationPreparation.CaptureOptions,
                    operationAdmission: composition.OperationAdmission,
                    playlistOperationAdmission: composition.PlaylistOperationAdmission);
                mutationPreparation.Library = library;
                TestBmsPlaylist playlist = MainWindowViewModelTestFactory.CreatePlaylist(songDbPath, settings, library: library);
                viewModel = new MainWindowViewModel(
                    composition,
                    new FixedStartupLibraryFactory(library, playlist));
                viewModel.StartupUpdateWorkflow.NotifyClosing();
                viewModel.ProgressHub.StartupProgress.SetStartupUiInteractionBlocked(false);
                ownership = new MainWindowTestLifetime(viewModel, lifetime.ShutdownRequested.Task);
                TestUiDispatcherHost.AwaitTaskOnDispatcher(
                    viewModel.ShellActivationWorkflow.ActivateRenderedShell(
                        () => { },
                        action => action(),
                        () => false),
                    "MainWindowViewHostTests.mode-restart-initialization");
                mutationPreparation.JoinAcceptedMutations();
                Assert.IsTrue(viewModel.IsInitializationCompleted);
                Assert.IsTrue(viewModel.HasActiveLibraryProfile);

                settings.ShowScoreViewerRegisterConfirmMsg = true;
                window = ownership.CreateWindow(() => new MainWindow(viewModel));
                window.Closed += (_, _) => windowClosed = true;
                if (closeDuringConfirmation) { settingsDialogs.OnConfirmation = window.Close; }
                if (busyAdmission != null)
                {
                    byte[] before = File.ReadAllBytes(settingsPath);
                    bool appliedMode = composition.CustomFolderOutputSettingsProvider().OperationModeLR2DB;
                    ChartFileOperationSynchronizer blocker = busyAdmission == "library"
                        ? composition.OperationAdmission : composition.PlaylistOperationAdmission;
                    Assert.IsTrue(blocker.TryEnter(out IDisposable lease));
                    using (lease)
                    {
                        viewModel.SettingDialog.OperationModeLR2DB = true;
                        Assert.IsTrue(viewModel.SettingDialog.OperationModeLR2DB);
                        Assert.IsFalse(settings.OperationModeLR2DB);
                        Assert.AreEqual(appliedMode, composition.CustomFolderOutputSettingsProvider().OperationModeLR2DB);
                        Assert.AreEqual(Resources.Warn_LibraryOperationBusy, settingsDialogs.Messages.Single().MessageBoxText);
                        Assert.AreEqual(0, settingsDialogs.ConfirmationCount);
                        Assert.AreEqual(0, settingsSession.ModeSaveCount);
                        Assert.AreEqual(0, settingsSession.SaveCount);
                        Assert.AreEqual(0, lifetime.RestartCount);
                        Assert.AreEqual(0, lifetime.CoordinatedShutdownStartCount);
                        Assert.IsFalse(viewModel.ShellShutdownWorkflow.IsShutdownPreparationStarted);
                        CollectionAssert.AreEqual(before, File.ReadAllBytes(settingsPath));
                        if (busyAdmission == "playlist") { Assert.IsFalse(composition.OperationAdmission.IsActive); }
                    }
                    Assert.AreEqual(0, settingsSession.ModeSaveCount, "拒否要求を終端後に再実行しません。");
                    viewModel.SettingDialog.SaveOperationModeForRestart(viewModel.SettingDialog.OperationModeLR2DB);
                    Assert.AreEqual(1, settingsSession.ModeSaveCount);
                    Assert.AreEqual(0, settingsSession.SaveCount);
                    Assert.IsTrue(PortableSettingsPersistenceTests.OpenSettings(settingsPath).OperationModeLR2DB);
                    window.Close();
                    TestUiDispatcherHost.AwaitTaskOnDispatcher(TestUiDispatcherHost.AwaitNotificationAsync(lifetime.ShutdownRequested.Task, window.CloseCompletion, "mode-selection-busy-normal-cleanup"), "mode-selection-busy-normal-cleanup");
                    window.Close();
                    Assert.IsTrue(windowClosed);
                    return;
                }
                Assert.IsFalse(composition.OperationAdmission.IsActive, "mode保存は先行操作がない条件で要求します。");
                TestUiDispatcherHost.AwaitTaskOnDispatcher(
                    viewModel.PlaybackPanel.ReplacePlayerAsync(new FakeBmsPlayer(() =>
                    {
                        playerEntered.TrySetResult(true);
                        playerRelease.Wait();
                    })),
                    "MainWindowViewHostTests.mode-restart-player");
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
                        await TestUiDispatcherHost.AwaitNotificationAsync(playerEntered.Task, observedClose.Task.Unwrap(), "mode-restart.close-player");
                        DispatcherOperation marker = dispatcher.InvokeAsync(() =>
                        {
                            dispatcherMarkerObserved = true;
                            Assert.AreEqual(0, lifetime.RestartCount);
                            Assert.AreEqual(0, lifetime.RequestShutdownCount);
                            Assert.AreEqual(0, settingsSession.SaveCount);
                        }, DispatcherPriority.Normal);
                        await marker.Task;
                    }
                    finally
                    {
                        playerRelease.Set();
                    }
                });

                // 後続要求に先立つ受理済みL保守は上の準備で実work終端まで回収済みです。
                Assert.IsFalse(composition.OperationAdmission.IsActive, "受理済み起動保守の準備終端後にmode保存を要求します。");
                viewModel.SettingDialog.OperationModeLR2DB = true;
                if (operationModeSaveFailure)
                {
                    TestUiDispatcherHost.AwaitTaskOnDispatcher(
                        settingsSaveFailureReported.Task,
                        "MainWindowViewHostTests.mode-restart-save-failure");
                }
                Assert.AreEqual(
                    !closeDuringConfirmation && !operationModeSaveFailure,
                    viewModel.SettingDialog.OperationModeLR2DB,
                    $"確認回数={settingsDialogs.ConfirmationCount}, モード保存回数={settingsSession.ModeSaveCount}, "
                        + $"編集完了中={viewModel.SettingDialog.IsEditCompletionInProgress}, "
                        + $"有効プロファイル={viewModel.HasActiveLibraryProfile}, "
                        + $"終了準備中={viewModel.ShellShutdownWorkflow.IsShutdownPreparationStarted}, "
                        + $"終了中={viewModel.ShellShutdownWorkflow.IsClosingOrClosed}"
                        + Environment.NewLine
                        + string.Join(Environment.NewLine, reportedSettingsFailures.Select(exception => exception.ToString())));
                Assert.AreEqual(1, settingsDialogs.ConfirmationCount);
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
                observedClose.TrySetResult(window.CloseCompletion);

                TestUiDispatcherHost.AwaitTaskOnDispatcher(observation, "MainWindowViewHostTests.mode-restart-dispatcher-marker");
                if (!closeDuringConfirmation && !operationModeSaveFailure)
                {
                    TestUiDispatcherHost.AwaitTaskOnDispatcher(
                        TestUiDispatcherHost.AwaitNotificationAsync(restartEntered.Task, window.CloseCompletion, "mode-restart.restart-start"),
                        "MainWindowViewHostTests.mode-restart-start");
                }
                if (restartFailure != null)
                {
                    TestUiDispatcherHost.AwaitTaskOnDispatcher(
                        TestUiDispatcherHost.AwaitNotificationAsync(notificationEntered.Task, window.CloseCompletion, "mode-restart.failure-notification"),
                        "MainWindowViewHostTests.mode-restart-failure-notification");
                    // 通知到達gateを保持した区間に終了が進まないことを観測します。
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
                TestUiDispatcherHost.AwaitTaskOnDispatcher(TestUiDispatcherHost.AwaitNotificationAsync(lifetime.ShutdownRequested.Task, window.CloseCompletion, "MainWindowViewHostTests.mode-restart-shutdown"), "MainWindowViewHostTests.mode-restart-shutdown");

                Assert.IsTrue(dispatcherMarkerObserved);
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
            catch (Exception exception)
            {
                bodyFailure = exception;
                throw;
            }
            finally
            {
                observedClose.TrySetResult(window?.CloseCompletion ?? Task.CompletedTask);
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
                try
                {
                    // 失敗時の未開始保守はshutdownが破棄し、開始済みworkは同じshutdown終端で回収します。
                }
                finally
                {
                    ownership?.DisposeAfterBodyFailure(bodyFailure);
                }
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

        /// <summary>実際に表示した通知を記録し、Busyと再起動失敗の接続を区別します。</summary>
        internal List<UiMessageRequest> Messages { get; } = [];

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
            Messages.Add(request);
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
        if (!Ribbit.Media.Audio.BassAudioRuntime.TryEnterAudioOperation(out BassAudioOperationLease lease))
        {
            return false;
        }
        lease.Dispose();
        return true;
    }

    private sealed class FakeBmsPlayer : IBMSPlayer, IExternalWindowPlayer
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
        internal IExternalPlayerWindowHost? WindowHost { get; private set; }
        internal TaskCompletionSource HostAttached { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public void AttachWindowHost(IExternalPlayerWindowHost windowHost)
        {
            WindowHost = windowHost;
            HostAttached.TrySetResult();
        }

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
