using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.IO;
using System.Linq;
using System.Runtime.ExceptionServices;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Automation.Peers;
using System.Windows.Automation.Provider;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
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

/// <remarks>実Close・描画後host接続・設定確認と終了の競合はWindowのイベント接続を通して検査します。通知判断と保存失敗後の終端はShellShutdownWorkflowOwnerTests、L/PのBusy条件はApplicationCompositionAdmissionTestsが分担します。</remarks>
[TestClass]
// 実WindowはApplication.Resourcesの画面VMを共有し、音声runtimeとログ構成も一時変更するため所有範囲を並列化しません。
[DoNotParallelize]
public sealed partial class MainWindowViewHostTests
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
                    null, null, false, false, false, false, "notification-terminal"));
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

    /// <summary>実Closeは変更した画面幅を一回保存してからアプリ終了へ進み、終了受付前後の設定表示要求を抑止し、終了callback失敗はClose終端の元例外として返します。</summary>
    [DataTestMethod]
    [DataRow(false, false)] // 終了受付後の即時設定表示を拒否します。
    [DataRow(true, false)] // 終了前にqueueした遅延表示も実行時に拒否します。
    [DataRow(false, true)] // 終了callback失敗をCloseCompletionへ伝え、保存を繰り返しません。
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

                // 未表示WindowのClose終端を対象とし、起動処理は既存の終了gateで抑止します。
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

                // fixtureの終了要求は実プロセスを止めないため、要求終端後に所有Windowを閉じます。
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

    /// <summary>実MainWindowの最初のCloseで全独立窓と新規受付を閉じ、先に個別Closeした成功readerも通常失敗readerも最終終了前にjoinします。shell準備完了後も最後のgateまで終端せず、finallyで全gate・Task・Windowを回収します。</summary>
    [TestMethod]
    public void MainWindowClose_JoinsAllChartPropertyReadsIncludingIndividuallyClosedWindow()
    {
        TestUiDispatcherHost.RunWindowTest(scope =>
        {
            using var firstRelease = new ManualResetEventSlim();
            using var secondRelease = new ManualResetEventSlim();
            TaskCompletionSource[] entered = new[]
            {
                new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously),
                new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously)
            };
            var children = new List<ChartPropertiesWindow>();
            var lifetime = new RecordingApplicationLifetime();
            Settings settings = MainWindowViewModelTestFactory.CreateIsolatedSettings();
            ApplicationComposition composition = CreateComposition(new RecordingSettingsEditSession(settings, () => { }), lifetime);
            MainWindowViewModel viewModel = composition.CreateMainWindowViewModel();
            MainWindowPresentationTestHarness.PrepareConstructorOnlyShell(viewModel);
            viewModel.StartupUpdateWorkflow.NotifyClosing();
            var ownership = new MainWindowTestLifetime(viewModel, lifetime.ShutdownRequested.Task);
            ownership.Run(() =>
            {
                MainWindow window = ownership.CreateWindow(() => new MainWindow(viewModel, settingsWindowCreated: null,
                    chartPropertiesManagerFactory: owner => new ChartPropertiesWindowManager(owner, (chart, placement) =>
                    {
                        int index = children.Count;
                        var model = new ChartPropertiesViewModel(chart, () =>
                        {
                            entered[index].TrySetResult();
                            (index == 0 ? firstRelease : secondRelease).Wait();
                            if (index == 1) { throw new IOException("late property read during main shutdown"); }
                            return new ChartResourceDiagnostics("late title", "", [], [], []);
                        });
                        var child = new ChartPropertiesWindow(model);
                        children.Add(child);
                        return child;
                    }, scope.PrepareForOwnedPresentation)));
                try
                {
                    ShowMainWindow(scope, window);
                    window.ChartPropertiesWindows.Open(ChartTestValues.Empty());
                    window.ChartPropertiesWindows.Open(ChartTestValues.Empty());
                    Task reads = window.ChartPropertiesWindows.ReadCompletion;
                    foreach (TaskCompletionSource notification in entered)
                    {
                        TestUiDispatcherHost.AwaitTaskOnDispatcher(TestUiDispatcherHost.AwaitNotificationAsync(
                            notification.Task, reads, "main-properties-read-entered"), "main-properties-read-entered");
                    }
                    children[0].Close();
                    var secondClosed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
                    children[1].Closed += (_, _) => secondClosed.TrySetResult();
                    window.Close();
                    Assert.IsTrue(viewModel.ShellShutdownWorkflow.IsClosingOrClosed);
                    window.ChartPropertiesWindows.Open(ChartTestValues.Empty());
                    Assert.AreEqual(2, children.Count);
                    TestUiDispatcherHost.AwaitPresentationOnDispatcher(secondClosed.Task, "main-properties-second-closed");
                    TestUiDispatcherHost.AwaitTaskOnDispatcher(viewModel.ShellShutdownWorkflow.RequestWindowCloseAsync(), "main-properties-shell-prepared");
                    TestUiDispatcherHost.AwaitTaskOnDispatcher(window.Dispatcher.InvokeAsync(() => { }, DispatcherPriority.ApplicationIdle).Task, "main-properties-terminal-dispatched");
                    Assert.IsFalse(window.CloseCompletion.IsCompleted);
                    Assert.IsFalse(lifetime.ShutdownRequested.Task.IsCompleted);
                    firstRelease.Set();
                    // readerの到達は上で観測済み。同じ実Taskを取得して最初の終端を待ちます。
                    TestUiDispatcherHost.AwaitTaskOnDispatcher(((ChartPropertiesViewModel)children[0].DataContext).LoadAsync(), "main-properties-first-read-complete");
                    TestUiDispatcherHost.AwaitTaskOnDispatcher(window.Dispatcher.InvokeAsync(() => { }).Task, "main-properties-after-first-read");
                    Assert.IsFalse(window.CloseCompletion.IsCompleted);
                    Assert.IsFalse(lifetime.ShutdownRequested.Task.IsCompleted);
                    secondRelease.Set();
                    TestUiDispatcherHost.AwaitTaskOnDispatcher(window.CloseCompletion, "main-properties-close-complete");
                    Assert.IsTrue(lifetime.ShutdownRequested.Task.IsCompletedSuccessfully);
                    Assert.AreEqual(1, lifetime.RequestShutdownCount);
                    Assert.AreEqual(string.Empty, ((ChartPropertiesViewModel)children[0].DataContext).Title);
                    Assert.AreEqual(string.Empty, ((ChartPropertiesViewModel)children[1].DataContext).Status);
                }
                finally
                {
                    firstRelease.Set();
                    secondRelease.Set();
                    TestUiDispatcherHost.AwaitTaskOnDispatcher(window.ChartPropertiesWindows.CompleteShutdownAsync(), "main-properties-finally");
                }
            });
        });
    }

    /// <summary>利用者が保存した行・見出し・文字サイズと初期選択を実画面へ反映し、操作後の画面幅を実Close時に同じ設定主体へ保存します。</summary>
    [TestMethod]
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
                // 保存済み初期選択を実Controlへ配送し、起動前の選択handlerは既存gateで抑止します。
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

    /// <summary>初回設定要求でcompiled言語overlayに実候補・選択・継続commandを接続し、明示非表示とClose後の再表示抑止を確認します。</summary>
    [TestMethod]
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
        // 固定factoryと受付前の候補読取が同じ専用の単独動作DBを指すよう、実配置のdata/song.dbへ揃える。
        var fixturePath = ApplicationPathSnapshot.FromExecutablePath(Path.Combine(directory, "BeMusicSeeker.exe"));
        string songDbPath = fixturePath.StandaloneSongDbPath;
        Directory.CreateDirectory(fixturePath.DataDirectoryPath);
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
        string songDbPath = ApplicationPathSnapshot.FromExecutablePath(Path.Combine(directory, "BeMusicSeeker.exe")).StandaloneSongDbPath;
        var library = new TestBmsLibrary(songDbPath, getLR2Config: null, _lr2ScoreDB: null,
            fileMutationService: null, dialogService: null,
            uiScheduler: new WpfUiScheduler(() => Dispatcher.CurrentDispatcher),
            optionsSnapshotProvider: mutationPreparation == null
                ? () => BmsLibraryOptionsSnapshot.CreateCurrent(settings)
                : mutationPreparation.CaptureOptions,
            operationAdmission: composition.OperationAdmission,
            playlistOperationAdmission: composition.PlaylistOperationAdmission, settings: settings);
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
                        if (name is not ("chart_info_hydration" or "chart_info_backfill" or "maintenance_hydration" or "installable_maintenance"))
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

        /// <summary>初期化の登録終端後、受理済み読込み・任意補完・保守をlease解放まで回収します。GC等は含めません。</summary>
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

    /// <summary>保存配置復元後も共通scopeの保護を維持し、実MainWindowの描画完了を待ちます。</summary>
    internal static void ShowMainWindow(TestWindowPresentationScope scope, MainWindow window)
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

    /// <summary>描画準備後のhost接続入口ではL/P解放済みかつ成功未公開であり、成功propertyの実通知時には接続済みの所有HWNDがあることを確認します。</summary>
    /// <remarks>必須UI全体の終端・失敗・任意処理順はStartupProgressテストが分担し、このケースは実Attachと成功公開の順序を直接観測します。</remarks>
    [TestMethod]
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
            ApplicationComposition? composition = null;
            Task? activation = null;
            var ui = new WpfUiScheduler(() => Dispatcher.CurrentDispatcher);
            ExternalPlayerHwndHost? playbackHost = null;
            var completionPublished = new TaskCompletionSource<(bool HostAttached, IExternalPlayerWindowHost? WindowHost, IntPtr Handle)>(
                TaskCreationOptions.RunContinuationsAsynchronously);
            PropertyChangedEventHandler? completionObserver = null;
            RoutedEventHandler? beginActivation = null;
            var player = new FakeBmsPlayer(onAttach: _ =>
            {
                Assert.IsNotNull(composition);
                Assert.IsNotNull(viewModel);
                Assert.IsFalse(composition.OperationAdmission.IsActive);
                Assert.IsFalse(composition.PlaylistOperationAdmission.IsActive);
                Assert.IsFalse(viewModel.IsInitializationCompleted, "実host接続は成功公開の前に完了します。");
            });
            var lifetime = new RecordingApplicationLifetime();
            try
            {
                composition = new ApplicationComposition(
                    settingsEditSession: new PersistentSettingsEditSession(settings),
                    defaultBmsPlayerFactory: () => player,
                    uiScheduler: ui,
                    applicationLifetime: lifetime,
                    applicationPathSnapshot: ApplicationPathSnapshot.FromExecutablePath(Path.Combine(directory.Path, "BeMusicSeeker.exe")),
                    cultureCatalog: TestApplicationContext.CreateCultureCatalog());
                viewModel = CreatePlaybackHostViewModel(composition, settings, directory.Path);
                viewModel.StartupUpdateWorkflow.NotifyClosing();
                ownership = new MainWindowTestLifetime(viewModel, lifetime.ShutdownRequested.Task);
                completionObserver = (_, args) =>
                {
                    if (args.PropertyName == nameof(MainWindowViewModel.IsInitializationCompleted) && viewModel.IsInitializationCompleted)
                    {
                        completionPublished.TrySetResult((player.HostAttached.Task.IsCompleted, player.WindowHost, playbackHost?.Handle ?? IntPtr.Zero));
                    }
                };
                viewModel.PropertyChanged += completionObserver;
                window = ownership.CreateWindow(() => new MainWindow(viewModel));
                beginActivation = (sender, _) =>
                {
                    if (activation != null) { return; }
                    var loadedWindow = (MainWindow)sender;
                    loadedWindow.UpdateLayout();
                    PlaybackPanelView playbackView = GetNamedElement<PlaybackPanelView>(loadedWindow, "playbackPanelView");
                    playbackHost = (ExternalPlayerHwndHost)playbackView.FindName("externalPlayerHost");
                    Assert.AreNotEqual(IntPtr.Zero, playbackHost.Handle, "実hostを生成してから起動要求を開始します。");
                    // 基底のContentRenderedより先に同じ一回受付へ渡し、実要求Taskを所有します。
                    activation = viewModel.ShellActivationWorkflow.ActivateRenderedShell(() => { }, _ => { }, () => false);
                };
                window.Loaded += beginActivation;
                window.Closed += (_, _) =>
                {
                    closed = true;
                    windowClosed.TrySetResult();
                };
                Assert.IsFalse(viewModel.IsInitializationCompleted);
                Assert.IsFalse(player.HostAttached.Task.IsCompleted);
                ShowMainWindow(scope, window);
                Assert.IsNotNull(activation, "実host生成後に開始した要求Taskを所有します。");
                TestUiDispatcherHost.AwaitTaskOnDispatcher(
                    TestUiDispatcherHost.AwaitNotificationAsync(completionPublished.Task, activation, "rendered-host-success-publication"),
                    "rendered-host-success-publication");
                (bool HostAttached, IExternalPlayerWindowHost? WindowHost, nint Handle) published = completionPublished.Task.GetAwaiter().GetResult();
                Assert.IsTrue(published.HostAttached, "成功公開の瞬間に実Attachが完了しています。");
                Assert.IsNotNull(published.WindowHost);
                Assert.AreNotEqual(IntPtr.Zero, published.Handle);
                Assert.AreEqual(published.Handle, published.WindowHost.ParentHandle.NativeValue);
                TestUiDispatcherHost.AwaitTaskOnDispatcher(activation, "rendered-playback-activation-terminal");
                Assert.IsTrue(viewModel.IsInitializationCompleted);
                PlaybackPanelView playbackView = GetNamedElement<PlaybackPanelView>(window, "playbackPanelView");
                var host = (ExternalPlayerHwndHost)playbackView.FindName("externalPlayerHost");
                Assert.AreEqual(published.Handle, host.Handle);
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
                try { if (activation != null) { TestUiDispatcherHost.AwaitTaskOnDispatcher(activation, "rendered-host-activation-cleanup"); } }
                finally
                {
                    if (window != null && beginActivation != null) { window.Loaded -= beginActivation; }
                    if (viewModel != null && completionObserver != null) { viewModel.PropertyChanged -= completionObserver; }
                    ownership?.DisposeAfterBodyFailure(bodyFailure);
                }
            }
        });
    }

    /// <summary>必須起動BMT入力のworker待機中に実Closeすると、起動取消とL/P終端を待ってWindowを閉じ、成功公開や遅延失敗画面を追加しません。</summary>
    [TestMethod]
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
                    applicationPathSnapshot: ApplicationPathSnapshot.FromExecutablePath(Path.Combine(directory.Path, "BeMusicSeeker.exe")),
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
                ShowMainWindow(scope, window);
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
                Assert.IsFalse(viewModel.HasActiveLibraryProfile);
                // 終了取消はShutdownRequestedとして終え、通常失敗の表示にも起動完了にも変換しない。
                Assert.IsFalse(viewModel.ProgressHub.StartupProgress.IsFailed);
                Assert.IsFalse(viewModel.ProgressHub.StartupProgress.IsStartupInitializationRequiredProgressComplete(
                    viewModel.ProgressHub.StartupProgress.GetActiveStartupProgressOperationToken()));
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

    /// <summary>通常行で実キャプションの最大化・復元・最小化を確認し、両行のキャプションCloseでplayer停止中もDispatcherとHWNDを維持して、導入終端・画面捕捉・player終了・UI保存・音声解放・終了の順序を守ります。</summary>
    /// <remarks>実MainWindowのcompiledボタンからAutomation・Command・終了ownerを通し、表示状態とCloseの実終端を別々に観測します。判断・保存失敗・取消・再起動競合は既存owner系列へ分担し、OSポインターのhit-testやresizeは対象に含めません。</remarks>
    [TestMethod]
    [DataRow(false)] // 通常行はキャプション状態操作を経て、導入workerとplayerの両方の排出を待ちます。
    [DataRow(true)] // 更新準備済みの行もキャプションCloseからplayer排出と終端を待ちます。
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
            Task? initialization = null;
            Task? packageIdle = null;
            Task? closeCompletion = null;
            var captionProbes = new List<Task>();
            var captionSubscriptions = new List<(Button Button, RoutedEventHandler Handler)>();
            var stateSubscriptions = new List<(MainWindow Window, EventHandler Handler)>();
            var observedClose = new TaskCompletionSource<Task>(TaskCreationOptions.RunContinuationsAsynchronously);
            void InvokeCaption(MainWindow target, string role, string operationName)
            {
                StackPanel captionPanel = GetNamedElement<StackPanel>(target, "windowCaptionPanel");
                // 表示役割から選び、Commandが誤配線でも正しいCommandを持つ別ボタンへ読み替えません。
                Button button = captionPanel.Children.OfType<Button>().Single(candidate =>
                    candidate.IsVisible && string.Equals(candidate.Content as string, role, StringComparison.Ordinal));
                var clickedProbe = new TaskCompletionSource<Task>(TaskCreationOptions.RunContinuationsAsynchronously);
                RoutedEventHandler clicked = (_, _) =>
                {
                    // ClickはOnClick内のCommand実行より先なので、後続UI operationまで回収します。
                    // Binding・描画後のCommand確認にはLoadedを使い、無関係なWin32入力待ちでgate解放を止めません。
                    Task probe = dispatcher.InvokeAsync(() => { }, DispatcherPriority.Loaded).Task;
                    captionProbes.Add(probe);
                    clickedProbe.TrySetResult(probe);
                };
                button.Click += clicked;
                captionSubscriptions.Add((button, clicked));
                AutomationPeer peer = UIElementAutomationPeer.CreatePeerForElement(button)
                    ?? throw new AssertFailedException("キャプション操作のAutomation peerがありません。");
                IInvokeProvider invoke = peer.GetPattern(PatternInterface.Invoke) as IInvokeProvider
                    ?? throw new AssertFailedException("キャプション操作のInvokeがありません。");
                invoke.Invoke();
                TestUiDispatcherHost.AwaitPresentationOnDispatcher(clickedProbe.Task, operationName + ".click");
                TestUiDispatcherHost.AwaitTaskOnDispatcher(clickedProbe.Task.GetAwaiter().GetResult(), operationName + ".command-probe");
                button.Click -= clicked;
                captionSubscriptions.Remove((button, clicked));
            }
            void AssertCaptionState(MainWindow target, string role, WindowState expectedState, string operationName)
            {
                var changed = new TaskCompletionSource<WindowState>(TaskCreationOptions.RunContinuationsAsynchronously);
                EventHandler stateChanged = (_, _) => changed.TrySetResult(target.WindowState);
                target.StateChanged += stateChanged;
                stateSubscriptions.Add((target, stateChanged));
                InvokeCaption(target, role, operationName);
                TestUiDispatcherHost.AwaitPresentationOnDispatcher(changed.Task, operationName + ".state-changed");
                Assert.AreEqual(expectedState, changed.Task.GetAwaiter().GetResult());
                Assert.AreEqual(expectedState, target.WindowState);
                target.StateChanged -= stateChanged;
                stateSubscriptions.Remove((target, stateChanged));
            }
            try
            {
                Ribbit.Media.Audio.BassAudioRuntime.Initialize();
                var startupPlayer = new FakeBmsPlayer();
                var composition = new ApplicationComposition(
                    settingsEditSession: session,
                    defaultBmsPlayerFactory: () => startupPlayer,
                    uiScheduler: new WpfUiScheduler(() => dispatcher),
                    applicationLifetime: lifetime,
                    applicationPathSnapshot: ApplicationPathSnapshot.FromExecutablePath(Path.Combine(directory.Path, "BeMusicSeeker.exe")),
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
                initialization = viewModel.ShellActivationWorkflow.ActivateRenderedShell(
                    () => { }, action => action(), () => false);
                ShowMainWindow(scope, window);
                TestUiDispatcherHost.AwaitTaskOnDispatcher(
                    TestUiDispatcherHost.AwaitNotificationAsync(startupPlayer.HostAttached.Task, initialization, "player-drain.initialization-host"),
                    "player-drain-initialization");
                TestUiDispatcherHost.AwaitTaskOnDispatcher(initialization, "player-drain-initialization-terminal");
                mutationPreparation.JoinAcceptedMutations();
                PlaybackPanelView playbackView = GetNamedElement<PlaybackPanelView>(window, "playbackPanelView");
                IntPtr hostHandle = playbackView.PlayerHostHandle;
                ExternalPlayerHostObservation.AssertOwnedChild(hostHandle, window);
                if (!prepareForUpdate)
                {
                    window.WindowState = WindowState.Normal;
                    TestUiDispatcherHost.ProcessQueuedPresentation();
                    AssertCaptionState(window, "1", WindowState.Maximized, "caption-maximize");
                    AssertCaptionState(window, "2", WindowState.Normal, "caption-restore");
                    AssertCaptionState(window, "0", WindowState.Minimized, "caption-minimize");
                    // 最小化後のNormalは後続の幅変更の準備であり、キャプション復元の保証ではありません。
                    window.WindowState = WindowState.Normal;
                    TestUiDispatcherHost.ProcessQueuedPresentation();
                }
                ColumnDefinition treeColumn = GetNamedElement<ColumnDefinition>(window, "gridColumn0");
                treeColumn.Width = new GridLength(359d);
                // 終了時の捕捉は実幅を優先するため、利用者が操作を終えた実レイアウトを先に確定します。
                window.UpdateLayout();
                Assert.AreEqual(359d, treeColumn.ActualWidth, "停止順の観測前に変更した画面幅を確定します。");
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
                        // 初回Command後の実Close Taskを捕捉するまでplayer gateを解放させません。
                        Task actualClose = await observedClose.Task;
                        await TestUiDispatcherHost.AwaitNotificationAsync(entered.Task, actualClose, "player-drain.close-player");
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
                        // observer 自身の終端で解放します。初回Closeの捕捉前にUIが同期停止すると、ここには到達しません。
                        release.Set();
                    }
                });
                InvokeCaption(window, "r", "caption-close");
                closeCompletion = window.CloseCompletion;
                Assert.IsFalse(closeCompletion.IsCompleted, "実キャプションCloseもplayer排出前に終端しません。");
                Assert.IsFalse(composition.OperationAdmission.TryEnter(out _), "Close開始時に新規L受付を閉じます。");
                Assert.IsFalse(composition.PlaylistOperationAdmission.TryEnter(out _), "Close開始時に新規P受付を閉じます。");
                observedClose.TrySetResult(closeCompletion);
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
                TestUiDispatcherHost.AwaitTaskOnDispatcher(closeCompletion, "caption-close-terminal");
                TestUiDispatcherHost.AwaitTaskOnDispatcher(lifetime.ShutdownRequested.Task, "application-shutdown-terminal");
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
                // RecordingLifetimeは実Appを終了しないため、認可後も同じキャプションからactual Closedへ進めます。
                InvokeCaption(window, "r", "caption-authorized-close");
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
                packageRelease.Set();
                release.Set();
                observedClose.TrySetResult(window?.CloseCompletion ?? Task.CompletedTask);
                Exception? cleanupFailure = null;
                void Recover(Action cleanup, string diagnosticName)
                {
                    try { cleanup(); }
                    catch (Exception failure)
                    {
                        Exception? primaryFailure = bodyFailure ?? cleanupFailure;
                        if (primaryFailure != null)
                        {
                            primaryFailure.Data[diagnosticName] = failure.ToString();
                        }
                        else { cleanupFailure = failure; }
                    }
                }
                if (observation != null) { Recover(() => TestUiDispatcherHost.AwaitTaskOnDispatcher(observation, "marker-cleanup"), "CaptionObservationCleanupFailure"); }
                if (marker != null) { Recover(() => TestUiDispatcherHost.AwaitTaskOnDispatcher(marker, "queued-marker-cleanup"), "CaptionMarkerCleanupFailure"); }
                if (initialization != null) { Recover(() => TestUiDispatcherHost.AwaitTaskOnDispatcher(initialization, "initialization-cleanup"), "CaptionInitializationCleanupFailure"); }
                if (packageIdle != null) { Recover(() => TestUiDispatcherHost.AwaitTaskOnDispatcher(packageIdle, "close-package-task-cleanup"), "CaptionPackageTaskCleanupFailure"); }
                if (viewModel != null) { Recover(() => TestUiDispatcherHost.AwaitTaskOnDispatcher(viewModel.PackageInstallWorkflow.WaitForIdleAsync(), "close-package-cleanup"), "CaptionPackageCleanupFailure"); }
                if (closeCompletion != null) { Recover(() => TestUiDispatcherHost.AwaitTaskOnDispatcher(closeCompletion, "close-completion-cleanup"), "CaptionCloseCleanupFailure"); }
                // 他Taskの合流中に到達したClickのprobeも含め、購読解除より前に回収します。
                for (int index = 0; index < captionProbes.Count; index++)
                {
                    Task probe = captionProbes[index];
                    Recover(() => TestUiDispatcherHost.AwaitTaskOnDispatcher(probe, "caption-probe-cleanup"), "CaptionProbeCleanupFailure" + index);
                }
                foreach ((Button button, RoutedEventHandler handler) in captionSubscriptions) { button.Click -= handler; }
                foreach ((MainWindow target, EventHandler handler) in stateSubscriptions) { target.StateChanged -= handler; }
                Recover(() => ownership?.DisposeAfterBodyFailure(bodyFailure ?? cleanupFailure), "CaptionWindowCleanupFailure");
                Recover(Ribbit.Media.Audio.BassAudioRuntime.Shutdown, "CaptionAudioCleanupFailure");
                if (bodyFailure == null && cleanupFailure != null) { ExceptionDispatchInfo.Capture(cleanupFailure).Throw(); }
            }
        });
    }


    /// <summary>未設定LR2への実モード切替も旧standaloneを維持し、player終了をUI停止せず待ち、モードと履歴を保存して後継起動後に旧Windowを終端します。</summary>
    [TestMethod]
    public void MainWindowOperationModeRestartDrainsPlayerBeforeStartAndShutdown()
    {
        RunMainWindowOperationModeRestartScenario();
    }

    /// <summary>モード確認中に実Closeが先着すると今回の選択を確定値へ戻し、モード保存と再起動を追加実行せず、先行Closeの保存と終端だけを完了します。</summary>
    [TestMethod]
    public void MainWindowCloseDuringModeConfirmationPreservesEarlierCloseWithoutModeSave()
    {
        RunMainWindowOperationModeRestartScenario(closeDuringConfirmation: true);
    }

    /// <summary>モードの実XML保存が失敗した時点では停止準備・再起動を始めず、元XMLと編集値を維持して専用保存例外を一回報告します。</summary>
    [TestMethod]
    public void MainWindowOperationModeRestartSaveFailureRetainsSettingsWithoutShutdown()
    {
        RunMainWindowOperationModeRestartScenario(
            operationModeSaveFailure: true);
    }

    /// <summary>実モード保存の失敗後は今回入力を保持します。保存先の解放だけでは再実行せず、同じ入力の明示再操作で保存と通常の再起動終端を完了します。</summary>
    [TestMethod]
    public void MainWindowOperationModeRestartSaveFailureAllowsExplicitRetry()
    {
        RunMainWindowOperationModeRestartScenario(operationModeSaveFailure: true, retryAfterSaveFailure: true);
    }

    private static void RunMainWindowOperationModeRestartScenario(
        bool closeDuringConfirmation = false,
        bool operationModeSaveFailure = false,
        bool retryAfterSaveFailure = false)
    {
        bool restartExpected = !closeDuringConfirmation && (!operationModeSaveFailure || retryAfterSaveFailure);
        using var directory = new TestTemporaryDirectory("main-window-mode-restart");
        string settingsPath = Path.Combine(directory.Path, "user.config");
        // 本番Settings.Defaultと同じく、UIのReloadと起動後の背景読取りが共有する同一instanceを同期化します。
        var settings = (Settings)System.Configuration.SettingsBase.Synchronized(
            PortableSettingsPersistenceTests.OpenSettings(settingsPath));
        settings.OperationModeLR2DB = false;
        settings.LR2RootPath = string.Empty;
        settings.LR2SongDBPath = string.Empty;
        settings.LR2ConfigXmlPath = string.Empty;
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
        var fixturePath = ApplicationPathSnapshot.FromExecutablePath(Path.Combine(directory.Path, "BeMusicSeeker.exe"));
        string songDbPath = fixturePath.StandaloneSongDbPath;
        Directory.CreateDirectory(fixturePath.DataDirectoryPath);
        StartupLibraryConstructionTestSupport.CreateSongDatabase(songDbPath);
        PlaylistPersistenceRepository.EnsureSchema(songDbPath);

        using var playerRelease = new ManualResetEventSlim(false);
        var playerEntered = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var restartEntered = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var settingsSaveFailureReported = new TaskCompletionSource<Exception>(TaskCreationOptions.RunContinuationsAsynchronously);
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

        var dialogs = new AcceptedModeDialogService();
        var lifetime = new RecordingApplicationLifetime(
            onShutdown: () => AddEvent("shutdown"),
            restartApplication: () =>
            {
                AddEvent("restart");
                restartEntered.TrySetResult(true);
                return Task.CompletedTask;
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
                    applicationPathSnapshot: ApplicationPathSnapshot.FromExecutablePath(Path.Combine(directory.Path, "BeMusicSeeker.exe")),
                    cultureCatalog: TestApplicationContext.CreateCultureCatalog(),
                    settingsDialogService: settingsDialogs,
                    restartFailureDialogs: dialogs,
                    reportSettingsApplyFailure: exception =>
                    {
                        reportedSettingsFailures.Add(exception);
                        settingsSaveFailureReported.TrySetResult(exception);
                    });
                library = new TestBmsLibrary(
                    songDbPath,
                    getLR2Config: null,
                    _lr2ScoreDB: null,
                    fileMutationService: null, dialogService: null,
                    uiScheduler: new WpfUiScheduler(() => dispatcher),
                    optionsSnapshotProvider: mutationPreparation.CaptureOptions,
                    operationAdmission: composition.OperationAdmission,
                    playlistOperationAdmission: composition.PlaylistOperationAdmission, settings: settings);
                mutationPreparation.Library = library;
                TestBmsPlaylist playlist = MainWindowViewModelTestFactory.CreatePlaylist(songDbPath, settings, library: library);
                var startupFactory = new FixedStartupLibraryFactory(library, playlist);
                viewModel = new MainWindowViewModel(composition, startupFactory);
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

                // 後続要求に先立つ受理済みL保守は上の準備で実work終端まで回収済みです。
                Assert.IsFalse(composition.OperationAdmission.IsActive, "受理済み起動保守の準備終端後にmode保存を要求します。");
                Task modeOperation = viewModel.SettingDialog.SelectOperationModeAsync(true);
                TestUiDispatcherHost.AwaitTaskOnDispatcher(modeOperation, "operation mode automatic confirmation");
                if (operationModeSaveFailure)
                {
                    TestUiDispatcherHost.AwaitTaskOnDispatcher(
                        TestUiDispatcherHost.AwaitNotificationAsync(settingsSaveFailureReported.Task, modeOperation, "mode-restart-save-failure"),
                        "MainWindowViewHostTests.mode-restart-save-failure");
                }
                // 保存前のClose先着拒否は確定表示へ戻し、実保存の失敗では今回入力を保持します。
                Assert.AreEqual(
                    !closeDuringConfirmation,
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
                Assert.IsTrue(viewModel.HasActiveLibraryProfile, "モード保存で同一プロセスの稼働環境を差し替えません。");
                Assert.AreEqual(1, startupFactory.LibraryCallCount);
                Assert.AreEqual(1, startupFactory.PlaylistCallCount);
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
                    Assert.IsTrue(viewModel.SettingDialog.OperationModeLR2DB);
                    Assert.IsTrue(viewModel.SettingDialog.ShowScoreViewerRegisterConfirmMsg);
                }
                // 待機中の Close 再入も同じ terminal を共有します。
                settingsBlocker?.Dispose();
                settingsBlocker = null;
                if (retryAfterSaveFailure)
                {
                    TestUiDispatcherHost.ProcessQueuedPresentation();
                    Assert.AreEqual(1, settingsSession.ModeSaveCount, "保存先の解放だけでは失敗した要求を再実行しません。");
                    Assert.IsFalse(settings.OperationModeLR2DB);
                    TestUiDispatcherHost.AwaitTaskOnDispatcher(viewModel.SettingDialog.SelectOperationModeAsync(true), "operation mode explicit retry after save failure");
                    Assert.AreEqual(2, settingsSession.ModeSaveCount);
                    Assert.AreEqual(2, settingsDialogs.ConfirmationCount);
                    Assert.IsTrue(PortableSettingsPersistenceTests.OpenSettings(settingsPath).OperationModeLR2DB);
                    Assert.AreEqual(1, reportedSettingsFailures.Count, "明示再操作の成功は最初の保存失敗を重複報告しません。");
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

                window.Close();
                observedClose.TrySetResult(window.CloseCompletion);

                TestUiDispatcherHost.AwaitTaskOnDispatcher(observation, "MainWindowViewHostTests.mode-restart-dispatcher-marker");
                if (restartExpected)
                {
                    TestUiDispatcherHost.AwaitTaskOnDispatcher(
                        TestUiDispatcherHost.AwaitNotificationAsync(restartEntered.Task, window.CloseCompletion, "mode-restart.restart-start"),
                        "MainWindowViewHostTests.mode-restart-start");
                }
                TestUiDispatcherHost.AwaitTaskOnDispatcher(TestUiDispatcherHost.AwaitNotificationAsync(lifetime.ShutdownRequested.Task, window.CloseCompletion, "MainWindowViewHostTests.mode-restart-shutdown"), "MainWindowViewHostTests.mode-restart-shutdown");

                Assert.IsTrue(dispatcherMarkerObserved);
                Assert.AreEqual(1, settingsSession.SaveCount);
                Assert.AreEqual(restartExpected ? 1 : 0, lifetime.RestartCount);
                Assert.AreEqual(1, lifetime.RequestShutdownCount);
                if (restartExpected)
                {
                    Assert.IsTrue(IndexOfEvent(events, "restart") < IndexOfEvent(events, "shutdown"));
                }
                Settings persisted = PortableSettingsPersistenceTests.OpenSettings(settingsPath);
                Assert.AreEqual(restartExpected, persisted.OperationModeLR2DB);
                Assert.AreEqual(string.Empty, persisted.LR2RootPath);
                Assert.AreEqual(string.Empty, persisted.LR2SongDBPath);
                Assert.AreEqual(string.Empty, persisted.LR2ConfigXmlPath);
                Assert.AreEqual("history-initial", persisted.PlayHistorySelectedDisplayTargetIdentity);
                Assert.IsTrue(persisted.ShowScoreViewerRegisterConfirmMsg);

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
                // 失敗時の未開始保守を破棄し、開始済みworkを同じshutdown終端で回収します。
                ownership?.DisposeAfterBodyFailure(bodyFailure);
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

        internal int LibraryCallCount { get; private set; }

        internal int PlaylistCallCount { get; private set; }

        public BMSLibrary CreateBmsLibrary(LibraryProfile libraryProfile) { LibraryCallCount++; return library; }

        public BMSPlaylist CreateBmsPlaylist(LibraryProfile libraryProfile, BMSLibrary library) { PlaylistCallCount++; return playlist; }
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

        public void SaveEnvironmentForRestart(EnvironmentSettingsChange environment, string historyIdentity)
        {
            ModeSaveCount++;
            inner.SaveEnvironmentForRestart(environment, historyIdentity);
        }
    }

    private sealed class AcceptedModeDialogService : IUiDialogService
    {
        internal int ConfirmationCount { get; private set; }

        internal Action? OnConfirmation { get; set; }

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
            Messages.Add(request);
            return Task.FromResult(UiDialogResult.FromMessageBoxResult(MessageBoxResult.OK));
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
        private readonly Action<IExternalPlayerWindowHost>? onAttach;

        internal FakeBmsPlayer(Action? onClose = null, Action<IExternalPlayerWindowHost>? onAttach = null)
        {
            this.onClose = onClose;
            this.onAttach = onAttach;
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
            onAttach?.Invoke(windowHost);
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
        public void VolumeChanged(int volume) { }
    }

    private sealed class RecordingSettingsEditSession : ISettingsEditSession
    {
        public void SaveEnvironmentForRestart(EnvironmentSettingsChange environment, string historyIdentity)
        {
            environment.Commit(Values);
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

        public void ExitForUnrestoredDestination(System.Exception failure)
        => throw new System.InvalidOperationException("Unexpected unrestored destination exit.", failure);

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
