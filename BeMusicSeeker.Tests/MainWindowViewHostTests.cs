using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Threading;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Data;
using System.Windows.Threading;
using BeMusicSeeker.Models;
using BeMusicSeeker.Properties;
using BeMusicSeeker.ViewModels;
using BeMusicSeeker.Views;
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

        internal RecordingApplicationLifetime(List<string>? events = null, Action? onShutdown = null)
        {
            this.events = events;
            this.onShutdown = onShutdown;
        }

        internal TaskCompletionSource<bool> ShutdownRequested { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        internal int RequestShutdownCount { get; private set; }

        public bool IsFirstStartup => false;

        public void CompleteFirstStartup()
        {
        }

        public void MarkCoordinatedShutdownStarted(string reason)
        {
        }

        public void RequestShutdown()
        {
            RequestShutdownCount++;
            onShutdown?.Invoke();
            events?.Add("application-shutdown");
            ShutdownRequested.TrySetResult(true);
        }

        public Task RestartApplicationAsync() => Task.CompletedTask;
    }
}
