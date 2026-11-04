using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Globalization;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Data;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Effects;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using BeMusicSeeker.Models;
using BeMusicSeeker.Models.BmsLibraryInternal;
using BeMusicSeeker.Models.Utils;
using BeMusicSeeker.Properties;
using BeMusicSeeker.TestSupport.Components;
using BeMusicSeeker.TestSupport.Files;
using BeMusicSeeker.TestSupport.Wpf;
using BeMusicSeeker.ViewModels;
using BeMusicSeeker.Views;
using NLog;
using NLog.Config;
using NLog.Targets;
using Ribbit.Logging;
using static BeMusicSeeker.TestSupport.Components.PlaybackPanelTestSupport;

namespace BeMusicSeeker.Tests;

[TestClass]
public sealed class PlaybackPanelViewModelTests
{
    private readonly BeMusicSeeker.Properties.Settings testSettings = TestSettingsFactory.CreateIsolatedSettings();

    /// <summary>実音量Popupで0〜100%の表示桁数によらずSlider配置と軌道・つまみの中央を保ち、previewを漏らさずマウス終端と終了開始で最終音量を保存します。</summary>
    /// <remarks>実Popupのbindingとイベント配送を観測し、音量通知から音声runtimeへの接続は設定画面とAudioContractsの代表ケースへ分担します。</remarks>
    [TestMethod]
    [DoNotParallelize]
    public void PlaybackPanelView_VolumeMenuCommitsOnMouseCompletionAndShutdown()
    {
        var workspace = TestWorkspace.Create(TestRunDirectory.RootDirectory);
        bool workspaceDisposed = false;
        void DisposeWorkspace()
        {
            if (workspaceDisposed) { return; }
            workspaceDisposed = true;
            workspace.Dispose();
        }
        try
        {
            TestUiDispatcherHost.RunWindowTest(scope =>
            {
                Dispatcher previousDispatcher = Livet.DispatcherHelper.UIDispatcher;
                Livet.DispatcherHelper.UIDispatcher = System.Windows.Threading.Dispatcher.CurrentDispatcher;
                string directory = workspace.RootDirectory;
                Directory.CreateDirectory(directory);
                string path = Path.Combine(directory, "user.config");
                Settings values = TestSettingsFactory.OpenSettings(path);
                values.uBMplayVolume = 50;
                values.Save();
                var player = new FakeBmsPlayer();
                PlaybackPanelViewModel panel = CreatePanel(testSettings, player, new SettingsPlaybackSettingsStore(() => values));
                var view = new PlaybackPanelView { DataContext = panel };
                var window = new Window { Content = view, Width = 640, Height = 360, ShowInTaskbar = false };
                ContextMenu? menu = null;
                IEnumerable<DependencyObject> Descendants(DependencyObject item)
                {
                    for (int index = 0; index < VisualTreeHelper.GetChildrenCount(item); index++)
                    {
                        DependencyObject child = VisualTreeHelper.GetChild(item, index);
                        yield return child;
                        foreach (DependencyObject descendant in Descendants(child)) { yield return descendant; }
                    }
                }
                try
                {
                    scope.ShowAndWaitForContentRendered(window);
                    DropDownMenuButton dropdown = Descendants(view).OfType<DropDownMenuButton>().Single();
                    menu = dropdown.DropDownContextMenu;
                    menu.PlacementTarget = dropdown;
                    menu.IsOpen = true;
                    TestUiDispatcherHost.ProcessQueuedPresentation();
                    Slider slider = Descendants(menu).OfType<Slider>().Single(item => item.Name == "playerVolumeSlider");
                    TextBlock percentage = Descendants(menu).OfType<TextBlock>().Single(item => item.Name == "playerVolumePercentage");
                    var layout = (Grid)slider.Parent;
                    Rect? sliderBounds = null;
                    foreach (int volume in new[] { 0, 9, 10, 99, 100 })
                    {
                        slider.SetCurrentValue(Slider.ValueProperty, (double)volume);
                        menu.UpdateLayout();
                        TestUiDispatcherHost.ProcessQueuedPresentation();
                        Assert.AreEqual(volume + "%", percentage.Text);
                        Rect bounds = slider.TransformToAncestor(layout).TransformBounds(new Rect(slider.RenderSize));
                        if (sliderBounds is Rect previous)
                        {
                            Assert.AreEqual(previous.Left, bounds.Left, 0.01);
                            Assert.AreEqual(previous.Right, bounds.Right, 0.01);
                            Assert.AreEqual(previous.Width, bounds.Width, 0.01);
                        }
                        sliderBounds = bounds;
                        var track = (Track)slider.Template.FindName("PART_Track", slider);
                        var rail = (Border)track.DecreaseRepeatButton.Template.FindName("RailChrome", track.DecreaseRepeatButton);
                        Rect railBounds = rail.TransformToAncestor(layout).TransformBounds(new Rect(rail.RenderSize));
                        Rect thumbBounds = track.Thumb.TransformToAncestor(layout).TransformBounds(new Rect(track.Thumb.RenderSize));
                        Assert.AreEqual(railBounds.Top + railBounds.Height / 2, thumbBounds.Top + thumbBounds.Height / 2, 0.01);
                    }
                    Assert.IsNull(slider.FocusVisualStyle);
                    Assert.IsTrue(slider.Focusable && slider.IsTabStop && slider.IsEnabled);
                    slider.SetCurrentValue(Slider.ValueProperty, 37d);
                    Assert.AreEqual(37, panel.PlayerVolume);
                    Assert.AreEqual(50, values.uBMplayVolume);
                    Assert.AreEqual(37, player.LastVolume);
                    slider.RaiseEvent(new System.Windows.Input.MouseButtonEventArgs(System.Windows.Input.Mouse.PrimaryDevice, 0,
                        System.Windows.Input.MouseButton.Left)
                    { RoutedEvent = UIElement.PreviewMouseLeftButtonUpEvent });
                    Assert.AreEqual(37, TestSettingsFactory.OpenSettings(path).uBMplayVolume);
                    slider.SetCurrentValue(Slider.ValueProperty, 44d);
                    Assert.AreEqual(37, values.uBMplayVolume);
                    panel.BeginShutdown();
                    Assert.AreEqual(44, TestSettingsFactory.OpenSettings(path).uBMplayVolume);
                }
                finally
                {
                    try
                    {
                        if (menu != null) { menu.IsOpen = false; }
                        TestUiDispatcherHost.AwaitTaskOnDispatcher(panel.CloseForShutdown(), "main-volume-terminal");
                        view.DisposePlayerHost();
                        window.Content = null;
                        window.Close();
                        DisposeWorkspace();
                    }
                    finally { Livet.DispatcherHelper.UIDispatcher = previousDispatcher; }
                }
            });
        }
        finally { DisposeWorkspace(); }
    }

    /// <summary>停止中に確定した次回BMIIDXViewへ明示開始すると、旧playerの解放中にDispatcherが動いても面要求を失わず、通常・一時配置で実効面と保存状態を一回切り替えます。</summary>
    /// <remarks>実owner・View・DispatcherとBMIIDXViewを接続し、processとwindow操作だけを既存gatewayで代替します。旧Closeの待機中に実UI操作を回収し、要求Taskと面callback自身、後置したUI操作の終端を別々に待ちます。共有WPF資源を回収まで所有するため非並列です。</remarks>
    [TestMethod]
    [DoNotParallelize]
    [DataRow(false, true)]
    [DataRow(false, false)]
    [DataRow(true, false)]
    public void SurfaceRequest_PendingPlayerReplacementSelectsAvailableSurface(bool temporaryInstall, bool playButton)
    {
        var workspace = TestWorkspace.Create(TestRunDirectory.RootDirectory);
        bool workspaceDisposed = false;
        void DisposeWorkspace()
        {
            if (workspaceDisposed) { return; }
            workspaceDisposed = true;
            workspace.Dispose();
        }
        try
        {
            var databaseScope = new ApplicationDatabaseScope();
            TestUiDispatcherHost.RunWindowTest(scope =>
            {
                string directory = workspace.RootDirectory;
                string path = Path.Combine(directory, "chart.bms");
                string destination = Directory.CreateDirectory(Path.Combine(directory, "destination")).FullName;
                File.WriteAllText(path, "#TITLE 次回方式の明示再生");
                var closeEntered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
                var releaseClose = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
                var surfaceReceived = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
                var previous = new PreloadBmsPlayer
                {
                    CloseCompletion = () => { closeEntered.TrySetResult(); return releaseClose.Task; }
                };
                var processGateway = new RecordingExternalPlayerProcessGateway();
                processGateway.Session.KeepRunning = true;
                processGateway.Session.MainWindowHandle = new ExternalWindowHandle(new IntPtr(21));
                int starts = 0;
                processGateway.Session.BeforeStart = () => Interlocked.Increment(ref starts);
                BMIIDXView2015 replacement = CreateBmiPlayer(directory, processGateway);
                ChartFile chart = ChartTestValues.Empty() with { Path = path, RawTitle = "次回方式の明示再生" };
                if (temporaryInstall) { chart = ChartPackageTestExtensions.CreateEntryWithInstallDestination(chart, destination).Chart; }
                var rows = new MainChartListViewModel { Rows = new List<object> { chart }, SelectedIndex = 0 };
                var settings = new InMemoryPlaybackSettingsStore { PlayerPanelState = PlayerPanelState.TITLE_LARGE };
                var dialogs = new FakePlaybackDialogService();
                var panel = new PlaybackPanelViewModel(previous,
                    new WpfPlaybackUiDispatcher(new WpfUiScheduler(() => TestUiDispatcherHost.Dispatcher)),
                    new MainChartListPlaybackQueue(rows), settings, dialogs, _ => Assert.Fail("入力不良へ読み替えません。"), new ChartFileOperationSynchronizer());
                if (temporaryInstall)
                {
                    string songDb = Path.Combine(directory, "song.db");
                    File.WriteAllBytes(songDb, []);
                    panel.AttachLibrary(new TestBmsLibrary(databaseScope, songDb, settings: testSettings));
                }
                var view = new PlaybackPanelView { DataContext = panel };
                var window = new Window { Content = view, Width = 640, Height = 360, ShowInTaskbar = false };
                Task? request = null;
                DispatcherOperation? closeProbe = null;
                DispatcherOperation? surfaceDrain = null;
                int surfaceRequests = 0;
                try
                {
                    scope.ShowAndWaitForContentRendered(window);
                    panel.AttachWindowHost(new RecordingExternalPlayerWindowHost(new ExternalWindowHandle(view.PlayerHostHandle)));
                    Assert.IsTrue(panel.IsStoppedOrPaused);
                    Assert.AreEqual(PlayerPanelState.TITLE_LARGE, settings.PlayerPanelState);
                    Assert.AreEqual(PlayerPanelState.TITLE_LARGE, view.EffectivePlayerPanelState);
                    ((ISettingsDialogPlaybackRuntimePort)panel).UsePlayerForNextPlayback(() => replacement, supportsBmson: false);
                    Action selectSurface = view.CaptureBmsPlayerSurfaceRequest();
                    void ReceiveSurface()
                    {
                        surfaceRequests++;
                        try
                        {
                            selectSurface();
                            surfaceReceived.TrySetResult();
                        }
                        catch (Exception failure)
                        {
                            surfaceReceived.TrySetException(failure);
                            throw;
                        }
                    }
                    request = playButton
                        ? panel.HandlePlayButton(ReceiveSurface)
                        : panel.HandleTableRowActivation(0, chart, ReceiveSurface);
                    TestUiDispatcherHost.AwaitTaskOnDispatcher(
                        TestUiDispatcherHost.AwaitNotificationAsync(closeEntered.Task, request, "pending-surface-old-close"), "pending-surface-old-close");
                    // 面要求を旧playerの解放中に投稿する誤りを検出するため、DataBindの実配送後に解放します。
                    closeProbe = window.Dispatcher.InvokeAsync(() => { }, DispatcherPriority.Background);
                    TestUiDispatcherHost.AwaitTaskOnDispatcher(closeProbe.Task, "pending-surface-close-probe");
                    releaseClose.TrySetResult();
                    TestUiDispatcherHost.AwaitTaskOnDispatcher(request, "pending-surface-request-terminal");
                    surfaceDrain = window.Dispatcher.InvokeAsync(() => { }, DispatcherPriority.DataBind);
                    TestUiDispatcherHost.AwaitTaskOnDispatcher(surfaceDrain.Task, "pending-surface-posted-ui-terminal");
                    TestUiDispatcherHost.AwaitTaskOnDispatcher(
                        TestUiDispatcherHost.AwaitNotificationAsync(surfaceReceived.Task, surfaceDrain.Task, "pending-surface-callback"), "pending-surface-callback");
                    Assert.AreEqual(1, surfaceRequests);
                    Assert.IsTrue(view.EffectivePlayerPanelState.HasFlag(PlayerPanelState.BMS_PLAYER));
                    Assert.IsTrue(settings.PlayerPanelState.HasFlag(PlayerPanelState.BMS_PLAYER));
                    Assert.AreSame(chart, panel.NowPlayingChart);
                    Assert.AreEqual(1, starts);
                    string playbackPath = temporaryInstall ? Path.Combine(destination, Path.GetFileName(path)) : path;
                    StringAssert.Contains(processGateway.LaunchRequest.Arguments, playbackPath);
                    Assert.AreEqual(0, dialogs.PlaybackFailureNotificationCount);
                }
                finally
                {
                    releaseClose.TrySetResult();
                    previous.CloseCompletion = null;
                    try
                    {
                        if (closeProbe != null) { TestUiDispatcherHost.AwaitTaskOnDispatcher(closeProbe.Task, "pending-surface-close-probe-cleanup"); }
                        if (request != null)
                        {
                            TestUiDispatcherHost.AwaitTaskOnDispatcher(request, "pending-surface-request-cleanup");
                            surfaceDrain ??= window.Dispatcher.InvokeAsync(() => { }, DispatcherPriority.DataBind);
                            TestUiDispatcherHost.AwaitTaskOnDispatcher(surfaceDrain.Task, "pending-surface-ui-cleanup");
                            TestUiDispatcherHost.AwaitTaskOnDispatcher(
                                TestUiDispatcherHost.AwaitNotificationAsync(surfaceReceived.Task, surfaceDrain.Task, "pending-surface-callback-cleanup"), "pending-surface-callback-cleanup");
                        }
                    }
                    finally
                    {
                        processGateway.Session.KeepRunning = false;
                        panel.BeginShutdown();
                        try { TestUiDispatcherHost.AwaitTaskOnDispatcher(panel.CloseForShutdown(), "pending-surface-player-shutdown"); }
                        finally
                        {
                            view.DisposePlayerHost();
                            window.Content = null;
                            window.Close();
                            DisposeWorkspace();
                        }
                    }
                }
            });
        }
        finally { DisposeWorkspace(); }
    }

    /// <summary>実uBMplayの自然終了時の原子復元失敗を一回通知・診断し、自動次曲へ進みません。開始中の同期終了では同じ失敗の通知を開始側が所有します。</summary>
    /// <remarks>Naturalは終了単独、PreviousStartはAの終了通知未配送でBを明示開始、CurrentStartは現開始中の同期終了です。Stop/ShutdownBeforeReturnは実復元故障確定後のB返却を保留し実停止/終了で世代を失効、共有拒否を先に解放してから全Taskを回収します。元原因・INI path・起動阻止・明示再試行と旧snapshotの表示保全を確認し、通知故障も封じます。NLog共有構成を復元まで非並列で所有します。</remarks>
    [TestMethod]
    [DoNotParallelize]
    [DataRow("Natural", false)]
    [DataRow("Natural", true)]
    [DataRow("PreviousStart", false)]
    [DataRow("PreviousStart", true)]
    [DataRow("CurrentStart", false)]
    [DataRow("CurrentStart", true)]
    [DataRow("StopBeforeReturn", false)]
    [DataRow("ShutdownBeforeReturn", false)]
    public async Task NaturalExit_UbmplayRestoreFailureNotifiesOnceWithoutAdvancing(string exitTiming, bool failNotification)
    {
        var workspace = TestWorkspace.Create(TestRunDirectory.RootDirectory);
        bool workspaceDisposed = false;
        void DisposeWorkspace()
        {
            if (workspaceDisposed) { return; }
            workspaceDisposed = true;
            workspace.Dispose();
        }
        try
        {
            string root = workspace.RootDirectory;
            Directory.CreateDirectory(root);
            string executable = Path.Combine(root, "uBMplay.exe");
            string chart = Path.Combine(root, "chart.bms");
            string replacement = Path.Combine(root, "replacement.bms");
            string ini = Path.Combine(root, "ubm.ini");
            File.WriteAllText(executable, "");
            File.WriteAllText(chart, "chart");
            File.WriteAllText(replacement, "replacement");
            File.WriteAllText(ini, "[Main]\nAlwaysOnTop=True\nVSYNC=True\n[Option]\nBGA=1\nAutoSeparate=False\nSkinType=2\nVolume=12\n");
            byte[] original = File.ReadAllBytes(ini);
            var gateway = new RecordingExternalPlayerProcessGateway();
            gateway.Session.KeepRunning = true;
            gateway.Session.MainWindowHandle = new ExternalWindowHandle(new IntPtr(31));
            var host = new RecordingExternalPlayerWindowHost(new ExternalWindowHandle(new IntPtr(99)))
            {
                EnumeratedWindows = [gateway.Session.MainWindowHandle],
                WindowClassName = "ThunderRT6FormDC",
                WindowTitle = chart
            };
            var player = new uBMplay(executable, new RecordingPlayerSettingsGateway(), gateway);
            ((IExternalWindowPlayer)player).AttachWindowHost(host);
            var dialogs = new FakePlaybackDialogService { PlaybackFailureException = failNotification ? new IOException("通知故障") : null };
            var rows = new MainChartListViewModel { Rows = new List<object> { ChartTestValues.Empty() with { Path = chart }, ChartTestValues.Empty() with { Path = replacement } } };
            var panel = new PlaybackPanelViewModel(player, new ImmediatePlaybackUiDispatcher(), new MainChartListPlaybackQueue(rows),
                new InMemoryPlaybackSettingsStore(), dialogs, _ => { }, new ChartFileOperationSynchronizer());
            Logger diagnosticLogger = NLogWrapper.GetLogger(typeof(PlaybackPanelViewModelTests));
            System.Reflection.FieldInfo? fileLoggerProvider = typeof(NLogWrapper).GetField("_FileLogger",
                System.Reflection.BindingFlags.Static | System.Reflection.BindingFlags.NonPublic);
            Assert.IsNotNull(fileLoggerProvider);
            var originalFileLogger = (Func<Logger>?)fileLoggerProvider.GetValue(null);
            LoggingConfiguration? originalLogging = LogManager.Configuration;
            using var logs = new MemoryTarget { Layout = "${message}|${exception:format=tostring}" };
            var logging = new LoggingConfiguration();
            logging.AddRule(LogLevel.Warn, LogLevel.Fatal, logs);
            LogManager.Configuration = logging;
            fileLoggerProvider.SetValue(null, (Func<Logger>)(() => diagnosticLogger));
            FileStream? heldIni = null;
            Action? deliverExit = null;
            using var releaseStartReturn = new ManualResetEventSlim();
            var restoreAttempted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            Task? replacementStart = null;
            Task? close = null;
            bool interruptReturn = exitTiming is "StopBeforeReturn" or "ShutdownBeforeReturn";
            bool shuttingDown = exitTiming == "ShutdownBeforeReturn";
            try
            {
                if (exitTiming == "CurrentStart")
                {
                    gateway.Session.BeforeStart = () =>
                    {
                        heldIni = new FileStream(ini, FileMode.Open, FileAccess.Read, FileShare.Read);
                        gateway.Session.KeepRunning = false;
                        deliverExit = gateway.Session.CaptureExitedNotification();
                        gateway.Session.OnNextHasExited = deliverExit;
                    };
                }
                await panel.StartAtIndex(0);
                if (exitTiming != "CurrentStart")
                {
                    deliverExit = gateway.Session.CaptureExitedNotification();
                    heldIni = new FileStream(ini, FileMode.Open, FileAccess.Read, FileShare.Read);
                    gateway.Session.KeepRunning = false;
                    if (interruptReturn)
                    {
                        Action oldExit = deliverExit;
                        gateway.Session.OnNextHasExited = () =>
                        {
                            oldExit();
                            restoreAttempted.TrySetResult();
                            releaseStartReturn.Wait();
                        };
                        replacementStart = Task.Run(() => panel.StartAtIndex(1));
                        await TestUiDispatcherHost.AwaitNotificationAsync(restoreAttempted.Task, replacementStart, "ubm-restore-failed-before-start-return");
                        Assert.AreEqual(0, dialogs.PlaybackFailureNotificationCount, "最外要求の返却前には終了snapshotを配送しません。");
                        Assert.IsFalse(original.SequenceEqual(File.ReadAllBytes(ini)), "共有拒否で実復元が失敗した一時bytesを観測します。");
                        if (shuttingDown)
                        {
                            panel.BeginShutdown();
                            close = panel.CloseForShutdown();
                        }
                        else { close = panel.StopPlayback(closeProcess: true); }
                        ChartFile? expectedChart = panel.NowPlayingChart;
                        int expectedStatus = panel.PlaybackStatusValue;
                        string expectedHeader = panel.PlayerHeaderTitle;
                        Assert.IsFalse(close.IsCompleted, "実closeはBの開始終端を待ちます。");
                        heldIni.Dispose();
                        releaseStartReturn.Set();
                        await Task.WhenAll(replacementStart, close);
                        Assert.AreSame(expectedChart, panel.NowPlayingChart, "失効したBの故障は停止/終了後の表示対象を変えません。");
                        Assert.AreEqual(expectedStatus, panel.PlaybackStatusValue);
                        Assert.AreEqual(expectedHeader, panel.PlayerHeaderTitle);
                    }
                    else if (exitTiming == "PreviousStart")
                    {
                        gateway.Session.OnNextHasExited = deliverExit;
                        Assert.AreEqual(0, dialogs.PlaybackFailureNotificationCount);
                        await panel.StartAtIndex(1);
                    }
                    else { deliverExit(); }
                }
                Assert.IsNotNull(deliverExit);
                deliverExit();
                Assert.AreEqual(1, dialogs.PlaybackFailureNotificationCount);
                Assert.IsNotNull(dialogs.LastPlaybackFailure);
                Exception failure = dialogs.LastPlaybackFailure;
                IOException? restorationFailure = Causes(failure).OfType<IOException>().FirstOrDefault(cause => cause.Message.Contains(ini, StringComparison.Ordinal));
                Assert.IsNotNull(restorationFailure, "包装形式によらず実INI置換失敗とpathを保持します。");
                Assert.IsNotNull(restorationFailure.InnerException, "実INI置換失敗の元原因を保持します。");
                StringAssert.Contains(failure.ToString(), ini);
                if (!shuttingDown) { Assert.IsNull(panel.NowPlayingChart); }
                Assert.IsFalse(panel.IsPlaying);
                if (!shuttingDown) { Assert.AreEqual((int)ChartFileStatus.NONE, panel.PlaybackStatusValue); }
                Assert.AreEqual(exitTiming == "PreviousStart" || interruptReturn ? 1 : 0, rows.SelectedIndex);
                Assert.AreEqual(1, gateway.PrepareCount, "復元故障では自動次曲や新mainの開始へ進みません。");
                if (interruptReturn) { CollectionAssert.AreEqual(original, File.ReadAllBytes(ini), "後段closeは共有拒否解除後に成功し、別の復元失敗通知を作りません。"); }
                else { Assert.IsFalse(original.SequenceEqual(File.ReadAllBytes(ini)), "復元失敗時は一時bytesとbackupを保持します。"); }
                LogManager.Flush();
                string[] failureLogs = logs.Logs.Where(log => log.StartsWith("playback_failed|", StringComparison.Ordinal)).ToArray();
                Assert.AreEqual(1, failureLogs.Length, "同じ復元故障を終了と開始で重ねて診断しません。");
                StringAssert.Contains(failureLogs[0], ini);
                StringAssert.Contains(failureLogs[0], restorationFailure.InnerException.Message);

                if (shuttingDown) { return; }

                heldIni?.Dispose();
                var retry = new RecordingExternalPlayerProcessSession
                {
                    KeepRunning = true,
                    MainWindowHandle = new ExternalWindowHandle(new IntPtr(32))
                };
                gateway.EnqueuePreparedSession(retry);
                host.EnumeratedWindows = [retry.MainWindowHandle];
                host.WindowTitle = replacement;
                await panel.StartAtIndex(1);
                Assert.AreEqual(2, gateway.PrepareCount);
                Assert.IsNotNull(panel.NowPlayingChart);
                Assert.AreEqual(replacement, panel.NowPlayingChart.Path);
                Assert.IsTrue(panel.IsPlaying);
                deliverExit();
                Assert.AreEqual(1, dialogs.PlaybackFailureNotificationCount);
                Assert.AreEqual(replacement, panel.NowPlayingChart.Path, "旧snapshotは再試行で始めた現曲を消しません。");
                retry.KeepRunning = false;
                await panel.StopPlayback(closeProcess: true);
                CollectionAssert.AreEqual(original, File.ReadAllBytes(ini));
            }
            finally
            {
                try
                {
                    heldIni?.Dispose();
                    releaseStartReturn.Set();
                    try
                    {
                        try { if (replacementStart != null) { await replacementStart; } }
                        finally { if (close != null) { await close; } }
                    }
                    finally
                    {
                        try { await panel.CloseForShutdown(); }
                        finally { DisposeWorkspace(); }
                    }
                }
                finally
                {
                    LogManager.Flush();
                    fileLoggerProvider.SetValue(null, originalFileLogger);
                    LogManager.Configuration = originalLogging;
                }
            }

            static IEnumerable<Exception> Causes(Exception failure)
            {
                yield return failure;
                IEnumerable<Exception> innerCauses = failure is AggregateException aggregate ? aggregate.InnerExceptions
                    : failure.InnerException is Exception cause ? [cause] : [];
                foreach (Exception inner in innerCauses)
                {
                    foreach (Exception nested in Causes(inner)) { yield return nested; }
                }
            }
        }
        finally { DisposeWorkspace(); }
    }

    /// <summary>
    /// 実一時コピーを二件残留させ、元名復元と変更受付の解放後に一度だけ通知します。
    /// 通知Taskの終端を待ち、開始原因と独立した回収・復元原因を一回の失敗通知へ保持します。残留通知の故障で原因を置換せず、第三者fileと退避元を保全します。
    /// </summary>
    /// <remarks>Windowsの削除共有拒否だけを実FSで作り、playerのReadyと通知の寿命は既存の代替実装で制御します。</remarks>
    [TestMethod]
    [DoNotParallelize]
    [DataRow(false, false, false)]
    [DataRow(false, true, false)]
    [DataRow(true, true, false)]
    [DataRow(true, false, true)]
    public async Task TemporaryCopy_ResidueNotifiesOnceAfterCleanupWithoutChangingPlayback(bool failStart, bool failNotification, bool failRestore)
    {
        var workspace = TestWorkspace.Create(TestRunDirectory.RootDirectory);
        bool workspaceDisposed = false;
        void DisposeWorkspace()
        {
            if (workspaceDisposed) { return; }
            workspaceDisposed = true;
            workspace.Dispose();
        }
        try
        {
            if (!OperatingSystem.IsWindows())
            {
                Assert.Inconclusive("Windowsの削除共有規則を使う実FSケースです。");
            }
            string directory = workspace.RootDirectory;
            string source = Path.Combine(directory, "source");
            string destination = Path.Combine(directory, "destination");
            Directory.CreateDirectory(source);
            Directory.CreateDirectory(destination);
            string chartPath = Path.Combine(source, "chart.bms");
            string audioPath = Path.Combine(destination, "kick.wav");
            File.WriteAllText(chartPath, "#WAV01 kick.wav");
            File.WriteAllText(Path.Combine(source, "kick.wav"), "audio");
            File.WriteAllText(Path.Combine(destination, "chart.bms"), "existing chart");
            string songDb = Path.Combine(directory, "song.db");
            File.WriteAllBytes(songDb, []);
            var ready = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var completion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var notified = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var releaseNotification = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var player = new PreloadBmsPlayer { Ready = ready.Task, Completion = completion.Task };
            var operations = new ChartFileOperationSynchronizer();
            bool gateActiveAtNotification = true;
            bool sourceRestoredAtNotification = false;
            string? originalBytesAtNotification = null;
            string? retreatedBytesAtNotification = null;
            var startFailure = new InvalidDataException("一時試聴の開始失敗");
            string? notifiedDirectory = null;
            var dialogs = new FakePlaybackDialogService
            {
                TemporaryFilesRemainNotification = path =>
                {
                    notifiedDirectory = path;
                    gateActiveAtNotification = operations.IsActive;
                    sourceRestoredAtNotification = File.Exists(chartPath) && !File.Exists(Path.Combine(source, "chart_.bms"));
                    originalBytesAtNotification = File.ReadAllText(chartPath);
                    string retreated = Path.Combine(source, "chart_.bms");
                    retreatedBytesAtNotification = File.Exists(retreated) ? File.ReadAllText(retreated) : null;
                    notified.TrySetResult();
                    return failNotification
                        ? Task.FromException(new InvalidOperationException("残留通知の表示失敗"))
                        : releaseNotification.Task;
                }
            };
            PlaybackPanelViewModel panel = CreateTemporaryInstallPanel(testSettings, chartPath, destination, songDb,
                player, dialogs, out _, operations, packageRoot: source);
            Logger diagnosticLogger = NLogWrapper.GetLogger(typeof(PlaybackPanelViewModelTests));
            System.Reflection.FieldInfo? fileLoggerProvider = typeof(NLogWrapper).GetField("_FileLogger",
                System.Reflection.BindingFlags.Static | System.Reflection.BindingFlags.NonPublic);
            Assert.IsNotNull(fileLoggerProvider);
            var originalFileLogger = (Func<Logger>?)fileLoggerProvider.GetValue(null);
            LoggingConfiguration? originalLogging = LogManager.Configuration;
            using var logs = new MemoryTarget { Layout = "${message}|${exception:format=tostring}" };
            var logging = new LoggingConfiguration();
            logging.AddRule(LogLevel.Warn, LogLevel.Fatal, logs);
            LogManager.Configuration = logging;
            fileLoggerProvider.SetValue(null, (Func<Logger>)(() => diagnosticLogger));
            Task start = panel.StartAtIndex(0);
            FileStream? heldChart = null;
            FileStream? heldAudio = null;
            try
            {
                await TestUiDispatcherHost.AwaitNotificationAsync(player.StartObserved.Task, start, "temporary-residue-start");
                string copiedPath = await player.StartObserved.Task;
                heldChart = new FileStream(copiedPath, FileMode.Open, FileAccess.Read, FileShare.Read);
                heldAudio = new FileStream(audioPath, FileMode.Open, FileAccess.Read, FileShare.Read);
                if (failRestore) { File.WriteAllText(chartPath, "third party chart"); }
                if (failStart) { ready.SetException(startFailure); }
                else { ready.SetResult(); }
                await TestUiDispatcherHost.AwaitNotificationAsync(notified.Task, start, "temporary-residue-notification");
                if (!failNotification) { Assert.IsFalse(start.IsCompleted, "通知の表示終端を待ちます。"); }
                releaseNotification.TrySetResult();
                await start;

                Assert.AreEqual(1, dialogs.TemporaryFilesRemainNotificationCount);
                Assert.AreEqual(destination, notifiedDirectory);
                Assert.IsFalse(gateActiveAtNotification);
                Assert.AreEqual(!failRestore, sourceRestoredAtNotification);
                if (failStart)
                {
                    var aggregate = dialogs.LastPlaybackFailure as AggregateException;
                    Assert.IsNotNull(aggregate);
                    Exception[] causes = aggregate.Flatten().InnerExceptions.ToArray();
                    Assert.AreEqual(failRestore ? 4 : 3, causes.Length);
                    Assert.AreEqual(1, causes.Count(cause => ReferenceEquals(cause, startFailure)));
                    Assert.AreEqual(failRestore ? 3 : 2, causes.Count(cause => cause is IOException));
                    Assert.IsFalse(causes.Any(cause => cause.Message == "残留通知の表示失敗"), "任意の通知故障は開始・回収・復元の原因へ置き換えません。");
                    Assert.IsNull(panel.NowPlayingChart);
                }
                if (failRestore)
                {
                    Assert.AreEqual("third party chart", originalBytesAtNotification);
                    Assert.AreEqual("#WAV01 kick.wav", retreatedBytesAtNotification);
                    LogManager.Flush();
                    Assert.IsTrue(logs.Logs.Any(log => log.Contains("temporary_preview_restore_failed", StringComparison.Ordinal)
                        && log.Contains(chartPath, StringComparison.Ordinal)
                        && log.Contains(Path.Combine(source, "chart_.bms"), StringComparison.Ordinal)
                        && log.Contains(nameof(IOException), StringComparison.Ordinal)));
                }
                LogManager.Flush();
                Assert.AreEqual(2, logs.Logs.Count(log => log.Contains("temporary_preview_cleanup_failed", StringComparison.Ordinal)
                    && log.Contains(nameof(IOException), StringComparison.Ordinal)), "二件の回収失敗は集約通知とともに元path付き診断にも残します。");
                Assert.AreEqual(!failStart, panel.IsPlaying);
                Assert.AreEqual(failStart ? 1 : 0, dialogs.PlaybackFailureNotificationCount);
                Assert.AreEqual("existing chart", File.ReadAllText(Path.Combine(destination, "chart.bms")));
                Assert.IsTrue(File.Exists(copiedPath));
                Assert.IsTrue(File.Exists(audioPath));
                heldChart.Dispose();
                heldAudio.Dispose();
                await panel.StopPlayback(closeProcess: true);
                Assert.IsTrue(File.Exists(copiedPath), "停止時に回収を再試行しません。");
                Assert.IsTrue(File.Exists(audioPath));
                Assert.AreEqual(1, dialogs.TemporaryFilesRemainNotificationCount);
            }
            finally
            {
                try
                {
                    releaseNotification.TrySetResult();
                    ready.TrySetCanceled();
                    completion.TrySetResult();
                    try { await start; }
                    finally
                    {
                        try { await panel.StopPlayback(closeProcess: true); }
                        finally
                        {
                            heldChart?.Dispose();
                            heldAudio?.Dispose();
                            DisposeWorkspace();
                        }
                    }
                }
                finally
                {
                    LogManager.Flush();
                    fileLoggerProvider.SetValue(null, originalFileLogger);
                    LogManager.Configuration = originalLogging;
                }
            }
        }
        finally { DisposeWorkspace(); }
    }

    /// <summary>実素材の読取り失敗にplayer停止失敗が重なってもcopy元原因を一度通知し、停止原因と譜面pathを診断して所有物・元名・Lを回収します。</summary>
    [TestMethod]
    [DoNotParallelize]
    public async Task TemporaryCopy_CopyFailurePreservesPrimaryWhenStopAlsoFails()
    {
        var workspace = TestWorkspace.Create(TestRunDirectory.RootDirectory);
        bool workspaceDisposed = false;
        void DisposeWorkspace()
        {
            if (workspaceDisposed) { return; }
            workspaceDisposed = true;
            workspace.Dispose();
        }
        try
        {
            string root = workspace.RootDirectory;
            string source = Path.Combine(root, "source");
            string destination = Path.Combine(root, "destination");
            Directory.CreateDirectory(source);
            Directory.CreateDirectory(destination);
            string chart = Path.Combine(source, "chart.bms");
            string material = Path.Combine(source, "unreadable.wav");
            string songDb = Path.Combine(root, "song.db");
            File.WriteAllText(chart, "chart original");
            File.WriteAllText(material, "material original");
            File.WriteAllText(Path.Combine(destination, "chart.bms"), "existing chart");
            File.WriteAllBytes(songDb, []);
            var stopFailure = new InvalidOperationException("停止の二次故障");
            var player = new FakeBmsPlayer { BeforeClose = () => throw stopFailure };
            var dialogs = new FakePlaybackDialogService();
            var operations = new ChartFileOperationSynchronizer();
            PlaybackPanelViewModel panel = CreateTemporaryInstallPanel(testSettings, chart, destination, songDb, player, dialogs, out _, operations, packageRoot: source);
            Logger diagnosticLogger = NLogWrapper.GetLogger(typeof(PlaybackPanelViewModelTests));
            System.Reflection.FieldInfo? fileLoggerProvider = typeof(NLogWrapper).GetField("_FileLogger",
                System.Reflection.BindingFlags.Static | System.Reflection.BindingFlags.NonPublic);
            Assert.IsNotNull(fileLoggerProvider);
            var originalFileLogger = (Func<Logger>?)fileLoggerProvider.GetValue(null);
            LoggingConfiguration? originalLogging = LogManager.Configuration;
            using var logs = new MemoryTarget { Layout = "${message}|${exception:format=tostring}" };
            var logging = new LoggingConfiguration();
            logging.AddRule(LogLevel.Warn, LogLevel.Fatal, logs);
            LogManager.Configuration = logging;
            fileLoggerProvider.SetValue(null, (Func<Logger>)(() => diagnosticLogger));
            try
            {
                using (var unreadable = new FileStream(material, FileMode.Open, FileAccess.Read, FileShare.None))
                {
                    await panel.HandlePlayButton(null!);
                }
                Assert.AreEqual(1, dialogs.PlaybackFailureNotificationCount);
                Assert.IsInstanceOfType<AggregateException>(dialogs.LastPlaybackFailure);
                Assert.IsTrue(((AggregateException)dialogs.LastPlaybackFailure).Flatten().InnerExceptions
                    .Any(failure => failure is IOException && failure.Message.Contains(material, StringComparison.Ordinal)));
                Assert.IsFalse(player.Commands.Any(command => command.StartsWith("PlayStart:", StringComparison.Ordinal)));
                Assert.IsFalse(operations.IsActive);
                Assert.AreEqual("chart original", File.ReadAllText(chart));
                Assert.IsFalse(File.Exists(Path.Combine(source, "chart_.bms")));
                CollectionAssert.AreEqual(new[] { Path.Combine(destination, "chart.bms") }, Directory.GetFiles(destination));
                LogManager.Flush();
                Assert.IsTrue(logs.Logs.Any(log => log.Contains("temporary_preview_stop_failed", StringComparison.Ordinal)
                    && log.Contains(chart, StringComparison.Ordinal) && log.Contains(stopFailure.Message, StringComparison.Ordinal)));
            }
            finally
            {
                player.BeforeClose = null;
                try { await panel.CloseForShutdown(); }
                finally
                {
                    LogManager.Flush();
                    fileLoggerProvider.SetValue(null, originalFileLogger);
                    LogManager.Configuration = originalLogging;
                    DisposeWorkspace();
                }
            }
        }
        finally { DisposeWorkspace(); }
    }

    /// <summary>ボタンhover背景に対する前面描画・入力、実Trackの換算・Bindingを保ち、PLAYの連続seekと明示Pause後のclick・drag・反復mouse-upで再生状態を変えません。</summary>
    /// <remarks>実Windowの配置・描画・hit testと双方向Bindingを確認し、座標入力は本番MouseMoveと共通の入口を使います。非同期toggleと押下mouse-leave接続の不在は静的確認、engineの状態・時計保持は既存Realtimeテストへ分担します。</remarks>
    [TestMethod]
    [DoNotParallelize]
    public void PlaybackPanelView_SeekPreservesTrackBindingAndPlaybackState()
    {
        using var workspace = TestWorkspace.Create(TestRunDirectory.RootDirectory);
        List<Exception> cleanupFailures = [];
        try
        {
            TestUiDispatcherHost.RunWindowTest(scope =>
            {
                Dispatcher previousDispatcher = Livet.DispatcherHelper.UIDispatcher;
                string? path = null;
                PlaybackPanelViewModel? panel = null;
                PlaybackPanelView? view = null;
                Window? window = null;
                Task selection = Task.CompletedTask;
                void Cleanup(Action cleanup)
                {
                    try { cleanup(); }
                    catch (Exception failure) { cleanupFailures.Add(failure); }
                }
                try
                {
                    Livet.DispatcherHelper.UIDispatcher = Dispatcher.CurrentDispatcher;
                    path = workspace.GetPath("path.tmp");
                    File.WriteAllBytes(path, []);
                    var player = new FakeBmsPlayer { Duration = TimeSpan.FromSeconds(120) };
                    var rows = new MainChartListViewModel { Rows = new List<object> { ChartTestValues.Empty() with { Path = path } } };
                    panel = new PlaybackPanelViewModel(player, new ImmediatePlaybackUiDispatcher(), new MainChartListPlaybackQueue(rows),
                        new InMemoryPlaybackSettingsStore(), new FakePlaybackDialogService(), _ => Assert.Fail("Unexpected playback failure."), new ChartFileOperationSynchronizer());
                    view = new PlaybackPanelView();
                    view.DataContext = panel;
                    window = new Window();
                    window.Content = view;
                    window.Width = 800;
                    window.Height = 360;
                    window.ShowInTaskbar = false;
                    scope.ShowAndWaitForContentRendered(window);
                    selection = panel.StartAtIndex(0);
                    TestUiDispatcherHost.AwaitTaskOnDispatcher(selection, "seek-start");
                    TestUiDispatcherHost.ProcessQueuedPresentation();
                    var slider = (Slider)view.FindName("sliderPlayer");
                    slider.ApplyTemplate();
                    window.UpdateLayout();
                    var track = (Track)slider.Template.FindName("PART_Track", slider);
                    Assert.IsNull(slider.FocusVisualStyle);
                    Assert.IsTrue(slider.Focusable && slider.IsTabStop && slider.IsEnabled);
                    Assert.IsFalse(Stylus.GetIsPressAndHoldEnabled(slider));
                    BindingExpression binding = slider.GetBindingExpression(Slider.ValueProperty)
                    ?? throw new AssertFailedException("シークの双方向Bindingがありません。");
                    Assert.AreEqual(BindingMode.TwoWay, binding.ParentBinding.Mode);
                    player.CurrentTime = TimeSpan.FromSeconds(60);
                    player.Raise(nameof(IBMSPlayer.CurrentTime));
                    TestUiDispatcherHost.ProcessQueuedPresentation();
                    window.UpdateLayout();
                    var controls = (DockPanel)slider.Parent;
                    var play = (Button)view.FindName("buttonBMSPlayerControlsPlayAndPauseButton");
                    Button stop = ((StackPanel)play.Parent).Children.OfType<Button>().Skip(1).First();
                    stop.ApplyTemplate();
                    var buttonSurface = (Border)stop.Template.FindName("ButtonSurface", stop);
                    buttonSurface.Background = (Brush)view.FindResource("Playback.ControlHoverBrush");
                    Rect buttonBounds = buttonSurface.TransformToAncestor(controls).TransformBounds(new Rect(buttonSurface.RenderSize));
                    var overlap = Rect.Intersect(buttonBounds, slider.TransformToAncestor(controls).TransformBounds(new Rect(slider.RenderSize)));
                    Assert.IsFalse(overlap.IsEmpty, "実配置でseekの入力領域とボタンが重なります。");
                    var hit = controls.InputHitTest(new Point(overlap.Left + overlap.Width / 2, overlap.Top + overlap.Height / 2)) as DependencyObject;
                    while (hit != null && !ReferenceEquals(hit, slider)) { hit = VisualTreeHelper.GetParent(hit); }
                    Assert.AreSame(slider, hit, "重なりの入力はボタンよりseekを優先します。");
                    // 入力点にはつまみがなく、つまみだけを前面へ移した誤りも上のhit testで検出します。
                    Point buttonCenter = buttonSurface.TranslatePoint(new Point(buttonSurface.ActualWidth / 2, buttonSurface.ActualHeight / 2), track);
                    player.CurrentTime = TimeSpan.FromSeconds(track.ValueFromPoint(buttonCenter));
                    player.Raise(nameof(IBMSPlayer.CurrentTime));
                    TestUiDispatcherHost.ProcessQueuedPresentation();
                    window.UpdateLayout();
                    var overlappingGrip = (System.Windows.Shapes.Ellipse)track.Thumb.Template.FindName("ThumbChrome", track.Thumb);
                    var gripOverlap = Rect.Intersect(buttonBounds, overlappingGrip.TransformToAncestor(controls).TransformBounds(new Rect(overlappingGrip.RenderSize)));
                    Assert.IsFalse(gripOverlap.IsEmpty, "実配置でつまみの描画とhover背景が重なります。");
                    // 重なり内側のつまみ中央を拡大描画し、楕円境界のアンチエイリアスを判定対象にしません。
                    const int scale = 4;
                    TestUiDispatcherHost.ProcessQueuedPresentation();
                    // 子だけの画像化では親内の配置offsetが残るため、表示したWindow全体から実座標で採ります。
                    var rendered = new RenderTargetBitmap((int)Math.Ceiling(window.ActualWidth * scale), (int)Math.Ceiling(window.ActualHeight * scale), 96 * scale, 96 * scale, PixelFormats.Pbgra32);
                    rendered.Render(window);
                    byte[] pixel = new byte[4];
                    Point sample = controls.TranslatePoint(new Point(gripOverlap.Left + gripOverlap.Width / 2, gripOverlap.Top + gripOverlap.Height / 2), window);
                    rendered.CopyPixels(new Int32Rect((int)(sample.X * scale), (int)(sample.Y * scale), 1, 1), pixel, 4, 0);
                    Color gripColor = ((SolidColorBrush)overlappingGrip.Fill).Color;
                    CollectionAssert.AreEqual(new byte[] { gripColor.B, gripColor.G, gripColor.R, gripColor.A }, pixel,
                    $"ボタン背景よりseekが前面に描画されます。期待={gripColor}, 実BGRA={Convert.ToHexString(pixel)}, 重なり={gripOverlap}, 採取点={sample}");
                    Assert.IsTrue(panel.IsPlaying);
                    Point pausedClickPosition = default;
                    foreach ((double width, double thumbWidth) in new[] { (300d, 11d), (480d, 25d) })
                    {
                        slider.Width = width;
                        track.Thumb.Width = thumbWidth;
                        window.UpdateLayout();
                        Assert.IsTrue(track.ActualWidth > track.Thumb.ActualWidth);
                        foreach (double expected in new[] { 0d, 60d, 120d })
                        {
                            player.CurrentTime = TimeSpan.FromSeconds(expected);
                            player.Raise(nameof(IBMSPlayer.CurrentTime));
                            TestUiDispatcherHost.ProcessQueuedPresentation();
                            window.UpdateLayout();
                            Assert.AreEqual(expected, slider.Value, 0.001, "player通知が同じSliderへ届きます。");
                            Point center = track.Thumb.TranslatePoint(new Point(track.Thumb.ActualWidth / 2, track.Thumb.ActualHeight / 2), slider);
                            if (expected == 60d) { pausedClickPosition = center; }
                            slider.SetCurrentValue(Slider.ValueProperty, 0d);
                            window.UpdateLayout();
                            view.SeekFromPoint(center);
                            Assert.AreEqual(expected, slider.Value, 0.001);
                            Assert.AreEqual(expected, player.CurrentTime.TotalSeconds, 0.001);
                            Assert.IsTrue(panel.IsPlaying, "再生中の連続seekはPLAYを維持します。");
                            Assert.AreSame(binding, slider.GetBindingExpression(Slider.ValueProperty));
                        }
                        view.SeekFromPoint(new Point(-100, 9));
                        Assert.AreEqual(0d, player.CurrentTime.TotalSeconds, 0.001);
                        Assert.IsTrue(panel.IsPlaying);
                        view.SeekFromPoint(new Point(slider.ActualWidth + 100, 9));
                        Assert.AreEqual(120d, player.CurrentTime.TotalSeconds, 0.001);
                        Assert.IsTrue(panel.IsPlaying);
                    }
                    var rail = (Border)track.DecreaseRepeatButton.Template.FindName("RailChrome", track.DecreaseRepeatButton);
                    Assert.IsTrue(rail.ActualHeight > 0 && rail.ActualHeight < slider.ActualHeight);
                    Assert.AreEqual(((SolidColorBrush)slider.FindResource("App.AccentBrush")).Color, ((SolidColorBrush)rail.Background).Color);
                    Assert.AreEqual(0, player.Commands.Count(command => command == "Pause"));
                    panel.TogglePause();
                    Assert.IsTrue(panel.IsPaused, "seekとは別の明示操作でPauseへ切り替えます。");
                    Assert.AreEqual(1, player.Commands.Count(command => command == "Pause"));
                    view.SeekFromPoint(pausedClickPosition);
                    Assert.AreEqual(60d, player.CurrentTime.TotalSeconds, 0.001, "明示Pause後のclickで位置だけを変えます。");
                    Assert.IsTrue(panel.IsPaused);
                    foreach ((Point position, double expected) in new[]
                    {
                        (new Point(-100, 9), 0d),
                        (pausedClickPosition, 60d),
                        (new Point(slider.ActualWidth + 100, 9), 120d)
                    })
                    {
                        view.SeekFromPoint(position);
                        Assert.AreEqual(expected, slider.Value, 0.001);
                        Assert.AreEqual(expected, player.CurrentTime.TotalSeconds, 0.001);
                        Assert.IsTrue(panel.IsPaused, "一時停止中の連続seekはPauseを維持します。");
                        Assert.AreSame(binding, slider.GetBindingExpression(Slider.ValueProperty));
                    }
                    for (int release = 0; release < 2; release++)
                    {
                        slider.RaiseEvent(new MouseButtonEventArgs(Mouse.PrimaryDevice, 0, MouseButton.Left)
                        { RoutedEvent = UIElement.PreviewMouseLeftButtonUpEvent });
                        Assert.AreEqual(120d, player.CurrentTime.TotalSeconds, 0.001);
                        Assert.IsTrue(panel.IsPaused, "mouse-upを繰り返しても明示Pauseを再開しません。");
                        Assert.AreEqual(1, player.Commands.Count(command => command == "Pause"));
                    }
                    Assert.AreEqual(1, player.Commands.Count(command => command.StartsWith("PlayStart:", StringComparison.Ordinal)));
                    Assert.AreSame(binding, slider.GetBindingExpression(Slider.ValueProperty));
                }
                finally
                {
                    // 部分setupも回収し、一つの二次故障で残りの資源回収・共有状態の復元を飛ばしません。
                    if (panel != null)
                    {
                        Cleanup(panel.BeginShutdown);
                        Cleanup(() => TestUiDispatcherHost.AwaitTaskOnDispatcher(panel.CloseForShutdown(), "seek-shutdown"));
                    }
                    Cleanup(() => TestUiDispatcherHost.AwaitTaskOnDispatcher(selection, "seek-start-cleanup"));
                    if (view != null) { Cleanup(view.DisposePlayerHost); }
                    if (window != null)
                    {
                        Cleanup(() => window.Content = null);
                        Cleanup(() =>
                        {
                            if (!Application.Current.Windows.Cast<Window>().Contains(window)) { return; }
                            var closed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
                            EventHandler onClosed = (_, _) => closed.TrySetResult();
                            window.Closed += onClosed;
                            try
                            {
                                window.Close();
                                TestUiDispatcherHost.AwaitPresentationOnDispatcher(closed.Task, "seek-window-closed");
                            }
                            finally { window.Closed -= onClosed; }
                        });
                    }
                    if (path != null) { Cleanup(() => File.Delete(path)); }
                    Cleanup(() => Livet.DispatcherHelper.UIDispatcher = previousDispatcher);
                }
            });
        }
        catch (Exception failure)
        {
            // scopeが選ぶ本文・表示観測の失敗を優先し、局所cleanupの二次故障を添付します。
            if (cleanupFailures.Count != 0)
            {
                failure.Data["SeekCleanupFailures"] = new AggregateException(cleanupFailures).ToString();
            }
            throw;
        }
        if (cleanupFailures.Count != 0) { throw new AggregateException("Seek fixture cleanup failed.", cleanupFailures); }
    }

    private static string ArtworkSignature(ImageSource image)
    {
        Assert.IsInstanceOfType<BitmapSource>(image);
        var converted = new FormatConvertedBitmap((BitmapSource)image, PixelFormats.Bgra32, null, 0);
        int stride = converted.PixelWidth * 4;
        byte[] pixels = new byte[stride * converted.PixelHeight];
        converted.CopyPixels(pixels, stride, 0);
        return converted.PixelWidth + "x" + converted.PixelHeight + ":" + Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(pixels));
    }

    private static void AssertThemeArtwork(PlaybackPanelView view, Image artwork) =>
        Assert.AreEqual(ArtworkSignature((ImageSource)view.FindResource("Playback.DefaultImage")), ArtworkSignature(artwork.Source));

    /// <summary>同じcompiled Viewで全テーマの既定画像と操作色を更新し、実選曲A→B→素材なしで画像・バナーの除去とstage・外部・既定の優先順位を画素で確認します。</summary>
    /// <remarks>実WindowのLoaded・compiled binding・子HWNDまたはanimationを観測するため表示し、共有WPF資源を使うこのケースだけ非並列にします。</remarks>
    [TestMethod]
    [DoNotParallelize]
    public void PlaybackPanelView_ChartSelectionReplacesArtworkAndRestoresDefaultWhenAbsent()
    {
        var workspace = TestWorkspace.Create(TestRunDirectory.RootDirectory);
        bool workspaceDisposed = false;
        void DisposeWorkspace()
        {
            if (workspaceDisposed) { return; }
            workspaceDisposed = true;
            workspace.Dispose();
        }
        try
        {
            int unexpectedNotificationCount = 0;
            TestUiDispatcherHost.RunWindowTest(scope =>
            {
                string directory = workspace.RootDirectory;
                Directory.CreateDirectory(directory);
                var player = new PreloadBmsPlayer { SupportsBmson = true };
                var rows = new MainChartListViewModel();
                var settings = new InMemoryPlaybackSettingsStore();
                var panel = new PlaybackPanelViewModel(player, new ImmediatePlaybackUiDispatcher(), new MainChartListPlaybackQueue(rows),
                    settings, new FakePlaybackDialogService(), _ => System.Threading.Interlocked.Increment(ref unexpectedNotificationCount), new ChartFileOperationSynchronizer());
                var view = new PlaybackPanelView { DataContext = panel };
                var window = new Window { Content = view, Width = 640, Height = 360, ShowInTaskbar = false };
                Task selection = Task.CompletedTask;
                var artwork = (Image)view.FindName("gridBMSPlayerImage");
                var banner = (Border)view.FindName("gridBMSPlayerControlsBanner");
                ResourceDictionary[] originalDictionaries = Application.Current.Resources.MergedDictionaries.ToArray();
                int displayedChanges = 0;
                PropertyChangedEventHandler displayedHandler = (_, args) =>
                {
                    if (args.PropertyName == nameof(PlaybackPanelViewModel.DisplayedChart))
                    {
                        Interlocked.Increment(ref displayedChanges);
                    }
                };
                void WriteImage(string name, byte blue)
                {
                    var encoder = new PngBitmapEncoder();
                    encoder.Frames.Add(BitmapFrame.Create(BitmapSource.Create(1, 1, 96, 96, PixelFormats.Bgra32, null, new byte[] { blue, 30, 40, 255 }, 4)));
                    using FileStream file = File.Create(Path.Combine(directory, name));
                    encoder.Save(file);
                }
                byte Blue(ImageSource image)
                {
                    Assert.IsInstanceOfType<BitmapSource>(image);
                    var converted = new FormatConvertedBitmap((BitmapSource)image, PixelFormats.Bgra32, null, 0);
                    byte[] pixel = new byte[4];
                    converted.CopyPixels(new Int32Rect(0, 0, 1, 1), pixel, 4, 0);
                    return pixel[0];
                }
                try
                {
                    WriteImage("stage-a.png", 50);
                    WriteImage("banner-a.png", 60);
                    WriteImage("stage-b.png", 150);
                    WriteImage("banner-b.png", 160);
                    WriteImage("external.png", 210);
                    AppThemeService.ApplyTheme(AppThemeService.Light);
                    string[] paths = [Path.Combine(directory, "first.bms"), Path.Combine(directory, "second.bmson"), Path.Combine(directory, "third.bms")];
                    foreach (string path in paths)
                    {
                        File.WriteAllText(path, "");
                    }

                    ChartFile[] charts = [
                        ((ChartTestValues.Empty() with { Path = paths[0], Stagefile = "stage-a.png", Banner = "banner-a.png" })),
                        (ChartTestValues.Empty(ChartFileKind.Bmson) with { Path = paths[1], Stagefile = "stage-b.png", Banner = "banner-b.png" }),
                        ((ChartTestValues.Empty() with { Path = paths[2] }))
                    ];
                    rows.Rows = charts.Select(chart => (object)rows.RowProjection.CreateLibraryRow(null, chart)).ToList();
                    scope.ShowAndWaitForContentRendered(window);
                    window.UpdateLayout();
                    TestUiDispatcherHost.ProcessQueuedPresentation();
                    Assert.AreEqual(Stretch.UniformToFill, artwork.Stretch);
                    var signatures = new HashSet<string>(StringComparer.Ordinal);
                    foreach (string theme in AppThemeService.AvailableThemes)
                    {
                        AppThemeService.ApplyTheme(theme);
                        TestUiDispatcherHost.ProcessQueuedPresentation();
                        AssertThemeArtwork(view, artwork);
                        Assert.IsTrue(signatures.Add(ArtworkSignature(artwork.Source)), "各テーマには異なる同梱画像を接続します: " + theme);
                        var controls = (DockPanel)view.FindName("gridBMSPlayerControls");
                        Assert.AreEqual(((SolidColorBrush)view.FindResource("Playback.ControlBackgroundBrush")).Color, ((SolidColorBrush)controls.Background).Color);
                        var smallTitle = (TextBlock)view.FindName("gridBMSPlayerControlsTitle2");
                        Assert.AreEqual(((SolidColorBrush)view.FindResource("Playback.ControlForegroundBrush")).Color, ((SolidColorBrush)smallTitle.Foreground).Color);
                    }
                    AppThemeService.ApplyTheme(AppThemeService.Light);
                    TestUiDispatcherHost.ProcessQueuedPresentation();
                    AssertThemeArtwork(view, artwork);
                    settings.UseExternalPanelImage = true;
                    settings.StagefilePath = Path.Combine(directory, "external.png");
                    panel.NotifySettingsChanged();
                    TestUiDispatcherHost.ProcessQueuedPresentation();
                    ImageSource externalImage = artwork.Source;
                    foreach (string theme in new[] { "Light", "Dark", "Light" })
                    {
                        AppThemeService.ApplyTheme(theme);
                        TestUiDispatcherHost.ProcessQueuedPresentation();
                        Assert.AreEqual((byte)210, Blue(artwork.Source));
                        Assert.AreSame(externalImage, artwork.Source);
                    }
                    Assert.IsNull(banner.Background);
                    panel.PropertyChanged += displayedHandler;
                    for (int index = 0; index < charts.Length; index++)
                    {
                        selection = index == 0 ? panel.StartAtIndex(0) : panel.Next();
                        TestUiDispatcherHost.AwaitTaskOnDispatcher(selection, "ChartArtwork.selection");
                        window.UpdateLayout();
                        TestUiDispatcherHost.ProcessQueuedPresentation();
                        Assert.IsNotNull(panel.DisplayedChart);
                        Assert.AreEqual(charts[index].Kind, panel.DisplayedChart.Kind);
                        Assert.AreEqual(charts[index].Path, panel.DisplayedChart.Path);
                        if (index < 2)
                        {
                            Assert.AreEqual(index == 0 ? (byte)50 : (byte)150, Blue(artwork.Source));
                            ImageSource stageImage = artwork.Source;
                            foreach (string theme in new[] { "Light", "Dark", "Light" })
                            {
                                AppThemeService.ApplyTheme(theme);
                                TestUiDispatcherHost.ProcessQueuedPresentation();
                                Assert.AreEqual(index == 0 ? (byte)50 : (byte)150, Blue(artwork.Source));
                                Assert.AreSame(stageImage, artwork.Source);
                            }
                            Assert.IsInstanceOfType<ImageBrush>(banner.Background);
                            Assert.AreEqual(index == 0 ? (byte)60 : (byte)160, Blue(((ImageBrush)banner.Background).ImageSource));
                        }
                        else
                        {
                            Assert.AreEqual((byte)210, Blue(artwork.Source));
                            settings.UseExternalPanelImage = false;
                            panel.NotifySettingsChanged();
                            TestUiDispatcherHost.ProcessQueuedPresentation();
                            AssertThemeArtwork(view, artwork);
                            Assert.IsNull(banner.Background);
                        }
                    }
                    Assert.AreEqual(3, displayedChanges);
                    view.DataContext = null;
                    TestUiDispatcherHost.ProcessQueuedPresentation();
                    Assert.IsNull(artwork.Source);
                    Assert.IsNull(banner.Background);
                    Assert.AreEqual(new Thickness(0), banner.BorderThickness);
                }
                finally
                {
                    panel.PropertyChanged -= displayedHandler;
                    panel.BeginShutdown();
                    try
                    {
                        TestUiDispatcherHost.AwaitTaskOnDispatcher(panel.CloseForShutdown(), "ChartArtwork.shutdown");
                        TestUiDispatcherHost.AwaitTaskOnDispatcher(selection, "ChartArtwork.selection-cleanup");
                    }
                    finally
                    {
                        try
                        {
                            view.DisposePlayerHost();
                            window.Content = null;
                            window.Close();
                            DisposeWorkspace();
                        }
                        finally
                        {
                            Application.Current.Resources.MergedDictionaries.Clear();
                            foreach (ResourceDictionary dictionary in originalDictionaries) { Application.Current.Resources.MergedDictionaries.Add(dictionary); }
                            CustomTablePalette.Invalidate();
                        }
                    }
                }
            });
            Assert.AreEqual(0, unexpectedNotificationCount, "想定外の通知を呼び出しません。");
        }
        finally { DisposeWorkspace(); }
    }

    /// <summary>正常Aから破損・欠落素材のBへ実選曲して前曲を除去し、同曲stage・既定画像へのfallbackとPLAY・一回の開始を保ちます。</summary>
    /// <remarks>compiled Viewの実decoder・画素・元path診断を観測するため表示します。NLog構成を準備から復元まで所有します。</remarks>
    [TestMethod]
    [DoNotParallelize]
    [DataRow("broken-stage")]
    [DataRow("broken-banner")]
    [DataRow("broken-both")]
    [DataRow("missing-both")]
    [DataRow("missing-external")]
    [DataRow("broken-external")]
    public void PlaybackPanelView_ArtworkFailureKeepsCurrentSelectionAndPlayback(string scenario)
    {
        var workspace = TestWorkspace.Create(TestRunDirectory.RootDirectory);
        bool workspaceDisposed = false;
        void DisposeWorkspace()
        {
            if (workspaceDisposed) { return; }
            workspaceDisposed = true;
            workspace.Dispose();
        }
        try
        {
            int unexpectedNotificationCount = 0;
            TestUiDispatcherHost.RunWindowTest(scope =>
            {
                string directory = workspace.RootDirectory;
                LoggingConfiguration? originalLogging = null;
                bool loggingSnapshotCaptured = false;
                MemoryTarget? logs = null;
                PlaybackPanelViewModel? panel = null;
                PlaybackPanelView? view = null;
                Window? window = null;
                Image? artwork = null;
                Task selection = Task.CompletedTask;
                Exception? primaryFailure = null;
                List<Exception> cleanupFailures = [];
                void Cleanup(Action cleanup)
                {
                    try
                    {
                        cleanup();
                    }
                    catch (Exception exception)
                    {
                        cleanupFailures.Add(exception);
                    }
                }
                void WriteImage(string name, byte blue)
                {
                    var encoder = new PngBitmapEncoder();
                    encoder.Frames.Add(BitmapFrame.Create(BitmapSource.Create(1, 1, 96, 96, PixelFormats.Bgra32, null, new byte[] { blue, 30, 40, 255 }, 4)));
                    using FileStream file = File.Create(Path.Combine(directory, name));
                    encoder.Save(file);
                }
                byte Blue(ImageSource image)
                {
                    Assert.IsInstanceOfType<BitmapSource>(image);
                    var converted = new FormatConvertedBitmap((BitmapSource)image, PixelFormats.Bgra32, null, 0);
                    byte[] pixel = new byte[4];
                    converted.CopyPixels(new Int32Rect(0, 0, 1, 1), pixel, 4, 0);
                    return pixel[0];
                }
                try
                {
                    Directory.CreateDirectory(directory);
                    _ = NLogWrapper.GetLogger(typeof(PlaybackPanelView));
                    // 一回限りの初期化を復元で取り消さないよう、成功後の構成だけを退避します。
                    originalLogging = LogManager.Configuration;
                    loggingSnapshotCaptured = true;
                    logs = new MemoryTarget();
                    logs.Layout = "${message}|${exception:format=tostring}";
                    var configuration = new LoggingConfiguration();
                    string loggerName = typeof(PlaybackPanelView).FullName ?? throw new InvalidOperationException("Playback panel logger name is unavailable.");
                    configuration.AddRule(LogLevel.Warn, LogLevel.Fatal, logs, loggerName);
                    LogManager.Configuration = configuration;
                    var settings = new InMemoryPlaybackSettingsStore
                    {
                        UseExternalPanelImage = scenario.EndsWith("external", StringComparison.Ordinal),
                        StagefilePath = Path.Combine(directory, "external.png")
                    };
                    var player = new PreloadBmsPlayer { SupportsBmson = true };
                    var rows = new MainChartListViewModel();
                    var dialogs = new FakePlaybackDialogService();
                    panel = new PlaybackPanelViewModel(player, new ImmediatePlaybackUiDispatcher(), new MainChartListPlaybackQueue(rows),
                        settings, dialogs, _ => System.Threading.Interlocked.Increment(ref unexpectedNotificationCount), new ChartFileOperationSynchronizer());
                    view = new PlaybackPanelView();
                    view.DataContext = panel;
                    window = new Window();
                    window.Content = view;
                    window.Width = 640;
                    window.Height = 360;
                    window.ShowInTaskbar = false;
                    artwork = (Image)view.FindName("gridBMSPlayerImage");
                    var banner = (Border)view.FindName("gridBMSPlayerControlsBanner");
                    WriteImage("stage-a.png", 50);
                    WriteImage("banner-a.png", 60);
                    WriteImage("stage-b.png", 150);
                    WriteImage("banner-b.png", 160);
                    if (scenario is "broken-stage" or "broken-both")
                    {
                        File.WriteAllText(Path.Combine(directory, "stage-b.png"), "broken stage image");
                    }
                    if (scenario is "broken-banner" or "broken-both")
                    {
                        File.WriteAllText(Path.Combine(directory, "banner-b.png"), "broken banner image");
                    }
                    if (scenario == "broken-external")
                    {
                        File.WriteAllText(settings.StagefilePath, "broken external image");
                    }
                    string[] paths = [Path.Combine(directory, "first.bms"), Path.Combine(directory, "second.bmson")];
                    foreach (string path in paths)
                    {
                        File.WriteAllText(path, "");
                    }
                    bool chartMaterialsAbsent = scenario is "missing-both" or "missing-external" or "broken-external";
                    ChartFile[] charts = [
                        ChartTestValues.Empty() with { Path = paths[0], Stagefile = "stage-a.png", Banner = "banner-a.png" },
                        ChartTestValues.Empty(ChartFileKind.Bmson) with
                        {
                            Path = paths[1],
                            Stagefile = chartMaterialsAbsent ? "missing-stage.png" : "stage-b.png",
                            Banner = chartMaterialsAbsent ? "missing-banner.png" : "banner-b.png"
                        }
                    ];
                    rows.Rows = charts.Select(chart => (object)rows.RowProjection.CreateLibraryRow(null, chart)).ToList();
                    scope.ShowAndWaitForContentRendered(window);
                    window.UpdateLayout();
                    TestUiDispatcherHost.ProcessQueuedPresentation();
                    AssertThemeArtwork(view, artwork);
                    Assert.IsNull(banner.Background);
                    for (int index = 0; index < charts.Length; index++)
                    {
                        selection = index == 0 ? panel.StartAtIndex(0) : panel.Next();
                        TestUiDispatcherHost.AwaitTaskOnDispatcher(selection, "ArtworkFailure.selection");
                        window.UpdateLayout();
                        TestUiDispatcherHost.ProcessQueuedPresentation();
                        Assert.IsTrue(selection.IsCompletedSuccessfully);
                        Assert.AreEqual(charts[index].Path, panel.DisplayedChart?.Path);
                        Assert.AreEqual(charts[index].Path, panel.NowPlayingChart?.Path);
                        Assert.AreEqual(ChartFileStatus.PLAY, panel.GetPlaybackStatus(charts[index]));
                        Assert.AreEqual(index + 1, player.Starts.Count);
                        if (index == 0)
                        {
                            Assert.AreEqual((byte)50, Blue(artwork.Source));
                            Assert.IsInstanceOfType<ImageBrush>(banner.Background);
                            Assert.AreEqual((byte)60, Blue(((ImageBrush)banner.Background).ImageSource));
                        }
                        else if (scenario == "broken-stage")
                        {
                            AssertThemeArtwork(view, artwork);
                            Assert.IsInstanceOfType<ImageBrush>(banner.Background);
                            Assert.AreEqual((byte)160, Blue(((ImageBrush)banner.Background).ImageSource));
                            Assert.AreEqual(Stretch.Fill, ((ImageBrush)banner.Background).Stretch);
                        }
                        else if (scenario == "broken-banner")
                        {
                            Assert.AreEqual((byte)150, Blue(artwork.Source));
                            Assert.IsInstanceOfType<ImageBrush>(banner.Background);
                            Assert.AreEqual((byte)150, Blue(((ImageBrush)banner.Background).ImageSource));
                            Assert.AreEqual(Stretch.UniformToFill, ((ImageBrush)banner.Background).Stretch);
                        }
                        else
                        {
                            AssertThemeArtwork(view, artwork);
                            Assert.IsNull(banner.Background);
                            Assert.AreEqual(new Thickness(0), banner.BorderThickness);
                        }
                    }
                    Assert.AreEqual(0, dialogs.PlaybackFailureNotificationCount);
                    CollectionAssert.AreEqual(paths, player.Starts.ToArray());
                    LogManager.Flush();
                    string diagnostics = string.Join(Environment.NewLine, logs.Logs);
                    string expectedPath = scenario.EndsWith("external", StringComparison.Ordinal)
                        ? settings.StagefilePath
                        : Path.Combine(directory, scenario == "broken-banner" ? "banner-b.png" : scenario == "missing-both" ? "missing-stage.png" : "stage-b.png");
                    string expectedDiagnostic = logs.Logs.FirstOrDefault(entry => entry.Contains(expectedPath, StringComparison.Ordinal))
                        ?? throw new AssertFailedException("失敗素材の元pathを診断に残していません: " + expectedPath);
                    StringAssert.Contains(expectedDiagnostic, "playback_artwork failed");
                    if (scenario is "broken-both" or "missing-both")
                    {
                        StringAssert.Contains(diagnostics, Path.Combine(directory, scenario == "broken-both" ? "banner-b.png" : "missing-banner.png"));
                    }
                    if (scenario is "missing-both" or "missing-external")
                    {
                        StringAssert.Contains(expectedDiagnostic, nameof(FileNotFoundException));
                    }
                    else
                    {
                        Assert.IsTrue(expectedDiagnostic.Contains("Exception", StringComparison.Ordinal), "実decoderの元例外を同じpathの診断に残します。");
                    }
                }
                catch (Exception exception)
                {
                    primaryFailure = exception;
                    throw;
                }
                finally
                {
                    // 部分setupも回収し、後片付けの二次故障で元のsetup・本文故障を置き換えません。
                    if (panel != null)
                    {
                        Cleanup(panel.BeginShutdown);
                        Cleanup(() => TestUiDispatcherHost.AwaitTaskOnDispatcher(panel.CloseForShutdown(), "ArtworkFailure.shutdown"));
                    }
                    Cleanup(() => TestUiDispatcherHost.AwaitTaskOnDispatcher(selection, "ArtworkFailure.selection-cleanup"));
                    if (view != null)
                    {
                        Cleanup(view.DisposePlayerHost);
                    }
                    if (window != null)
                    {
                        Cleanup(() => window.Content = null);
                        Cleanup(window.Close);
                    }
                    if (Directory.Exists(directory))
                    {
                        Cleanup(() => DisposeWorkspace());
                    }
                    Cleanup(() => LogManager.Flush());
                    if (loggingSnapshotCaptured)
                    {
                        Cleanup(() => LogManager.Configuration = originalLogging);
                    }
                    if (logs != null)
                    {
                        Cleanup(logs.Dispose);
                    }
                    if (cleanupFailures.Count != 0)
                    {
                        var cleanupFailure = new AggregateException("ArtworkFailure fixture cleanup failed.", cleanupFailures);
                        if (primaryFailure != null)
                        {
                            primaryFailure.Data["ArtworkFailureCleanupFailures"] = cleanupFailure.ToString();
                        }
                        else
                        {
                            throw cleanupFailure;
                        }
                    }
                }
            });
            Assert.AreEqual(0, unexpectedNotificationCount, "想定外の通知を呼び出しません。");
        }
        finally { DisposeWorkspace(); }
    }

    /// <summary>compiled Viewを表示して初期Collapsedの子HWNDを所有UIスレッド上に生成し、指定した題名・字幕・作者を実Textへ反映することを確認します。</summary>
    /// <remarks>実WindowのLoaded・compiled binding・子HWNDまたはanimationを観測するため表示し、共有WPF資源を使うこのケースだけ非並列にします。</remarks>
    [TestMethod]
    [DoNotParallelize]
    public void PlaybackPanelView_CompiledTreeMaterializesCurrentSurfaceAndHeader()
    {
        var workspace = TestWorkspace.Create(TestRunDirectory.RootDirectory);
        bool workspaceDisposed = false;
        void DisposeWorkspace()
        {
            if (workspaceDisposed) { return; }
            workspaceDisposed = true;
            workspace.Dispose();
        }
        try
        {
            TestUiDispatcherHost.RunWindowTest(windowTest =>
            {
                string directory = workspace.RootDirectory;
                var window = new Window
                {
                    Width = 640d,
                    Height = 360d,
                    ShowInTaskbar = false,
                    WindowStyle = WindowStyle.None
                };
                try
                {
                    var settings = new InMemoryPlaybackSettingsStore
                    {
                        PlayerPanelState = PlayerPanelState.BMS_PLAYER
                    };
                    PlaybackPanelViewModel panel = CreatePanel(testSettings, CreateBmiPlayer(directory), settings);
                    string[] values = new string[29];
                    values[0] = "0123456789abcdef0123456789abcdef";
                    values[1] = "Current Title";
                    values[2] = "[Current Subtitle]";
                    values[3] = "Current Artist";
                    values[7] = Path.Combine(Path.GetTempPath(), "current.bms");
                    ChartFile current = ChartSongStorageMapping.FromBmsRow(ChartSongStorageMapping.FromRawSongValues(values));
                    panel.SetBmsPlayerHeader(current);

                    var view = new PlaybackPanelView { DataContext = panel };
                    window.Content = view;
                    windowTest.ShowAndWaitForContentRendered(window);
                    window.UpdateLayout();
                    TestUiDispatcherHost.ProcessQueuedPresentation();

                    var playerHost = (ExternalPlayerHwndHost)view.FindName("externalPlayerHost");
                    Assert.AreEqual(Visibility.Collapsed, playerHost.Visibility);
                    ExternalPlayerHostObservation.AssertOwnedChild(playerHost.Handle, window);
                    Assert.AreEqual(playerHost.Handle, view.PlayerHostHandle);
                    var title = (TextBlock)view.FindName("gridBMSPlayerControlsTitle");
                    var subtitle = (TextBlock)view.FindName("gridBMSPlayerControlsSubtitle");
                    var artist = (TextBlock)view.FindName("gridBMSPlayerControlsArtist");

                    Assert.AreEqual("Current Title", title.Text);
                    Assert.AreEqual("[Current Subtitle]", subtitle.Text);
                    Assert.AreEqual("Current Artist", artist.Text);
                    var titleSurface = (DockPanel)view.FindName("gridBMSPlayerTitlePanel");
                    Assert.AreEqual(Color.FromArgb(204, 0, 0, 0), ((SolidColorBrush)titleSurface.Background).Color);
                    Assert.AreEqual(Colors.White, ((SolidColorBrush)title.Foreground).Color);
                    var statistics = (Grid)view.FindName("gridPlayngBmsInfo");
                    TextBlock statisticsText = statistics.Children.OfType<TextBlock>().Single();
                    Assert.AreEqual(Colors.White, ((SolidColorBrush)statisticsText.Foreground).Color);
                    Assert.AreEqual(Color.FromArgb(51, 0, 0, 0), ((SolidColorBrush)statisticsText.Background).Color);
                    Assert.IsNull(statisticsText.Effect);
                    var play = (Button)view.FindName("buttonBMSPlayerControlsPlayAndPauseButton");
                    Assert.IsNull(play.FocusVisualStyle);
                    Assert.IsTrue(play.Focusable && play.IsTabStop);
                    Assert.AreEqual(((SolidColorBrush)view.FindResource("Playback.ControlForegroundBrush")).Color, ((SolidColorBrush)play.Foreground).Color);
                }
                finally
                {
                    (window.Content as PlaybackPanelView)?.DisposePlayerHost();
                    window.Content = null;
                    window.Close();
                    DisposeWorkspace();
                }
            });
        }
        finally { DisposeWorkspace(); }
    }

    /// <summary>player面・overlay・切離しと再接続で同じ子HWNDを保持し、外部player互換の物理寸法・DPI配置と選択面復元を確認します。</summary>
    /// <remarks>実WindowのLoaded・compiled binding・子HWNDまたはanimationを観測するため表示し、共有WPF資源を使うこのケースだけ非並列にします。</remarks>
    [TestMethod]
    [DoNotParallelize]
    public void PlaybackPanelView_PlayerSurfaceAndOverlayPreserveHostAndPhysicalLayout()
    {
        var workspace = TestWorkspace.Create(TestRunDirectory.RootDirectory);
        bool workspaceDisposed = false;
        void DisposeWorkspace()
        {
            if (workspaceDisposed) { return; }
            workspaceDisposed = true;
            workspace.Dispose();
        }
        try
        {
            int unexpectedNotificationCount = 0;
            TestUiDispatcherHost.RunWindowTest(scope =>
            {
                string directory = workspace.RootDirectory;
                string path = Path.Combine(directory, "playing.bms");
                File.WriteAllText(path, "#TITLE external surface");
                var gateway = new RecordingExternalPlayerProcessGateway();
                gateway.Session.KeepRunning = true;
                gateway.Session.MainWindowHandle = new ExternalWindowHandle(new IntPtr(21));
                BMIIDXView2015 player = CreateBmiPlayer(directory, gateway);
                var rows = new MainChartListViewModel { Rows = new List<object> { ChartTestValues.Empty() with { Path = path } } };
                var panel = new PlaybackPanelViewModel(player, new ImmediatePlaybackUiDispatcher(), new MainChartListPlaybackQueue(rows),
                    new InMemoryPlaybackSettingsStore(), new FakePlaybackDialogService(),
                    _ => System.Threading.Interlocked.Increment(ref unexpectedNotificationCount), new ChartFileOperationSynchronizer());
                var view = new PlaybackPanelView { DataContext = panel };
                var window = new Window { Content = view, Width = 800d, Height = 400d, WindowStyle = WindowStyle.None };
                try
                {
                    scope.ShowAndWaitForContentRendered(window);
                    var host = (ExternalPlayerHwndHost)view.FindName("externalPlayerHost");
                    IntPtr handle = host.Handle;
                    ExternalPlayerHostObservation.AssertOwnedChild(handle, window);
                    panel.AttachWindowHost(new RecordingExternalPlayerWindowHost(new ExternalWindowHandle(handle)));
                    TestUiDispatcherHost.AwaitTaskOnDispatcher(panel.StartAtIndex(0), "external-surface-start");
                    Assert.IsTrue(gateway.Session.Started);
                    Assert.AreEqual(path, panel.NowPlayingChart?.Path);
                    panel.PlayerPanelState = PlayerPanelState.BMS_PLAYER;
                    window.UpdateLayout();
                    TestUiDispatcherHost.ProcessQueuedPresentation();
                    Assert.AreEqual(Visibility.Visible, host.Visibility);
                    AssertPlaybackPanelHasNoAnimationClocks(view);
                    Assert.AreEqual(587d, host.MinWidth);
                    Assert.AreEqual(256d, host.Height);
                    Assert.AreEqual(256d, host.MaxHeight);
                    Assert.AreEqual(HorizontalAlignment.Center, host.HorizontalAlignment);
                    Assert.AreEqual(VerticalAlignment.Bottom, host.VerticalAlignment);
                    DpiScale dpi = VisualTreeHelper.GetDpi(host);
                    Console.WriteLine($"再生ホスト検証DPI: x={dpi.DpiScaleX}, y={dpi.DpiScaleY}");
                    Rect rectangle = ExternalPlayerHostObservation.GetRectangle(handle);
                    Assert.AreEqual(587d, rectangle.Width, 1d);
                    Assert.AreEqual(256d, rectangle.Height, 1d);
                    Assert.AreEqual(587d, host.RenderSize.Width * dpi.DpiScaleX, 1d);
                    Assert.AreEqual(256d, host.RenderSize.Height * dpi.DpiScaleY, 1d);
                    var body = (Grid)view.FindName("gridBMSPlayerBody");
                    Point bodyOrigin = body.PointToScreen(new Point());
                    Assert.AreEqual(bodyOrigin.X + body.ActualWidth * dpi.DpiScaleX / 2d, rectangle.X + rectangle.Width / 2d, 1d);
                    Assert.AreEqual(bodyOrigin.Y + body.ActualHeight * dpi.DpiScaleY, rectangle.Bottom, 1d);

                    foreach (Visibility overlay in new[] { Visibility.Visible, Visibility.Hidden, Visibility.Collapsed })
                    {
                        view.OverlayVisibility = overlay;
                        if (overlay != Visibility.Visible)
                        {
                            // MainWindowのOverlay終了入口は、選択面を復元してから通常表示へ戻します。
                            view.RestoreSelectedSurface();
                        }
                        window.UpdateLayout();
                        TestUiDispatcherHost.ProcessQueuedPresentation();
                        Assert.AreEqual(overlay == Visibility.Visible ? Visibility.Collapsed : Visibility.Visible, host.Visibility);
                        Assert.AreEqual(handle, host.Handle);
                        Assert.IsTrue(ExternalPlayerHostObservation.IsWindow(handle));
                        AssertPlaybackPanelHasNoAnimationClocks(view);
                    }
                    panel.PlayerPanelState = PlayerPanelState.TITLE_LARGE;
                    window.UpdateLayout();
                    TestUiDispatcherHost.ProcessQueuedPresentation();
                    Assert.AreEqual(Visibility.Collapsed, host.Visibility);
                    Assert.AreEqual(handle, host.Handle);
                    Assert.IsTrue(ExternalPlayerHostObservation.IsWindow(handle));
                    AssertPlaybackPanelHasNoAnimationClocks(view);

                    panel.PlayerPanelState = PlayerPanelState.TITLE_SMALL | PlayerPanelState.BMS_PLAYER;
                    window.UpdateLayout();
                    TestUiDispatcherHost.ProcessQueuedPresentation();
                    AssertPlaybackPanelHasTransitionClocks(view, compactTransition: true);
                    window.Content = null;
                    window.UpdateLayout();
                    TestUiDispatcherHost.ProcessQueuedPresentation();
                    Assert.IsTrue(ExternalPlayerHostObservation.IsWindow(handle));
                    window.Content = view;
                    window.UpdateLayout();
                    TestUiDispatcherHost.ProcessQueuedPresentation();
                    Assert.AreEqual(PlayerPanelState.TITLE_SMALL | PlayerPanelState.BMS_PLAYER, panel.PlayerPanelState);
                    Assert.AreEqual(panel.PlayerPanelState, view.EffectivePlayerPanelState);
                    AssertPlaybackPanelFinalState(view, compact: true);
                    AssertPlaybackPanelHasNoAnimationClocks(view);
                    Assert.AreEqual(handle, host.Handle);
                    ExternalPlayerHostObservation.AssertOwnedChild(handle, window);
                }
                finally
                {
                    panel.BeginShutdown();
                    gateway.Session.KeepRunning = false;
                    try { TestUiDispatcherHost.AwaitTaskOnDispatcher(panel.CloseForShutdown(), "external-surface-shutdown"); }
                    finally
                    {
                        view.DisposePlayerHost();
                        window.Content = null;
                        window.Close();
                        DisposeWorkspace();
                    }
                }
            });
            Assert.AreEqual(0, unexpectedNotificationCount, "想定外の通知を呼び出しません。");
        }
        finally { DisposeWorkspace(); }
    }

    /// <summary>画像／内蔵再生の現在モードと再生中／停止中の4条件で、言語を往復しても表示切替ボタンのtooltipが各段の現在語になり、表示モード・再生・playerを変えないことを確認します。</summary>
    /// <remarks>内蔵再生面は BMIIDXView2015 でだけ有効になるため、記録gatewayを持つ実playerを使います。再生の開始・停止・playerの再生成なしは、gatewayの起動準備回数、sessionの起動状態と終了要求（CloseMainWindow・Kill）の回数、NowPlayingChartの参照、IsPlayingとPlayerPanelStateの通知有無で観測します（playerはケース内で一つだけ生成します）。tooltipの区切り「: 」は現行挙動を期待値に固定し、言語変更は共通資源のため文化圏を必ず復元します。</remarks>
    [TestMethod]
    [DoNotParallelize]
    [DataRow(false, false)]
    [DataRow(false, true)]
    [DataRow(true, false)]
    [DataRow(true, true)]
    public void PlaybackPanelView_ViewModeTooltipFollowsLanguageWithoutChangingSurfaceOrPlayback(bool builtInPlayer, bool playing)
    {
        var workspace = TestWorkspace.Create(TestRunDirectory.RootDirectory);
        try
        {
            TestUiDispatcherHost.RunWindowTest(scope =>
            {
                CultureInfo? previousCulture = Resources.Culture;
                string directory = workspace.RootDirectory;
                string path = Path.Combine(directory, "tooltip.bms");
                File.WriteAllText(path, "#TITLE tooltip");
                var gateway = new RecordingExternalPlayerProcessGateway();
                gateway.Session.KeepRunning = true;
                gateway.Session.MainWindowHandle = new ExternalWindowHandle(new IntPtr(21));
                BMIIDXView2015 player = CreateBmiPlayer(directory, gateway);
                var rows = new MainChartListViewModel { Rows = new List<object> { ChartTestValues.Empty() with { Path = path } } };
                var settings = new InMemoryPlaybackSettingsStore { PlayerPanelState = PlayerPanelState.TITLE_LARGE };
                var panel = new PlaybackPanelViewModel(player, new ImmediatePlaybackUiDispatcher(), new MainChartListPlaybackQueue(rows),
                    settings, new FakePlaybackDialogService(), _ => Assert.Fail("想定外の再生失敗通知です。"), new ChartFileOperationSynchronizer());
                var view = new PlaybackPanelView { DataContext = panel };
                var window = new Window { Content = view, Width = 800d, Height = 400d, ShowInTaskbar = false };
                try
                {
                    ResourceService.Current.ChangeCulture("ja-JP");
                    scope.ShowAndWaitForContentRendered(window);
                    var host = (ExternalPlayerHwndHost)view.FindName("externalPlayerHost");
                    panel.AttachWindowHost(new RecordingExternalPlayerWindowHost(new ExternalWindowHandle(host.Handle)));
                    if (playing)
                    {
                        TestUiDispatcherHost.AwaitTaskOnDispatcher(panel.StartAtIndex(0), "tooltip-start");
                    }
                    panel.PlayerPanelState = builtInPlayer ? PlayerPanelState.BMS_PLAYER : PlayerPanelState.TITLE_LARGE;
                    window.UpdateLayout();
                    TestUiDispatcherHost.ProcessQueuedPresentation();
                    // 前提: 要求した組合せで、実効の表示モードと再生状態が対象になっている。
                    Assert.AreEqual(builtInPlayer, view.EffectivePlayerPanelState.HasFlag(PlayerPanelState.BMS_PLAYER));
                    Assert.AreEqual(playing, panel.IsPlaying);

                    var button = (Button)view.FindName("buttonBMSPlayerControlsRotatePanelState");
                    string Expected() => Resources.Tooltip_view_mode + ": " + (builtInPlayer ? Resources.Tooltip_bms_player : Resources.Tooltip_image_file);
                    PlayerPanelState requested = panel.PlayerPanelState, effective = view.EffectivePlayerPanelState;
                    object? content = button.Content;
                    object? chart = panel.NowPlayingChart;
                    int prepares = gateway.PrepareCount;
                    int closes = gateway.Session.CloseMainWindowCount, kills = gateway.Session.KillCount;
                    bool started = gateway.Session.Started;
                    var changes = new List<string>();
                    System.ComponentModel.PropertyChangedEventHandler recorder = (_, args) => changes.Add(args.PropertyName ?? string.Empty);
                    panel.PropertyChanged += recorder;
                    var tooltips = new List<string>();
                    try
                    {
                        Assert.AreEqual(Expected(), button.ToolTip);
                        tooltips.Add((string)button.ToolTip);
                        foreach (string culture in new[] { "en-US", "fr-FR", "ja-JP" })
                        {
                            ResourceService.Current.ChangeCulture(culture);
                            TestUiDispatcherHost.ProcessQueuedPresentation();
                            Assert.AreEqual(Expected(), button.ToolTip, culture);
                            tooltips.Add((string)button.ToolTip);
                        }
                    }
                    finally { panel.PropertyChanged -= recorder; }
                    Assert.AreNotEqual(tooltips[0], tooltips[1], "日本語と英語でtooltipが切り替わります。");
                    Assert.AreNotEqual(tooltips[1], tooltips[2], "英語とフランス語でtooltipが切り替わります。");
                    Assert.AreEqual(tooltips[0], tooltips[3]);
                    Assert.AreEqual(requested, panel.PlayerPanelState);
                    Assert.AreEqual(effective, view.EffectivePlayerPanelState);
                    Assert.AreEqual(requested, settings.PlayerPanelState);
                    Assert.AreEqual(content, button.Content);
                    Assert.AreEqual(playing, panel.IsPlaying);
                    Assert.AreSame(chart, panel.NowPlayingChart);
                    Assert.AreEqual(prepares, gateway.PrepareCount);
                    Assert.AreEqual(closes, gateway.Session.CloseMainWindowCount, "言語変更でplayerを閉じません。");
                    Assert.AreEqual(kills, gateway.Session.KillCount, "言語変更でplayerを強制終了しません。");
                    Assert.AreEqual(started, gateway.Session.Started);
                    CollectionAssert.DoesNotContain(changes, nameof(PlaybackPanelViewModel.PlayerPanelState));
                    CollectionAssert.DoesNotContain(changes, nameof(PlaybackPanelViewModel.IsPlaying));
                    CollectionAssert.DoesNotContain(changes, nameof(PlaybackPanelViewModel.NowPlayingChart));
                }
                finally
                {
                    ResourceService.Current.ChangeCulture(previousCulture?.Name ?? "en-US");
                    Resources.Culture = previousCulture;
                    panel.BeginShutdown();
                    gateway.Session.KeepRunning = false;
                    try { TestUiDispatcherHost.AwaitTaskOnDispatcher(panel.CloseForShutdown(), "tooltip-shutdown"); }
                    finally
                    {
                        view.DisposePlayerHost();
                        window.Content = null;
                        window.Close();
                    }
                }
            });
        }
        finally { workspace.Dispose(); }
    }

    /// <summary>小型と利用不能player面を指定してViewを表示すると、最初から画像の小型最終値へ同期しanimation clockを残さないことを確認します。</summary>
    /// <remarks>実WindowのLoaded・compiled binding・子HWNDまたはanimationを観測するため表示し、共有WPF資源を使うこのケースだけ非並列にします。</remarks>
    [TestMethod]
    [DoNotParallelize]
    public void PlaybackPanelView_InitiallySynchronizesCompactStateWithoutAnimation()
    {
        TestUiDispatcherHost.RunWindowTest(windowTest =>
        {
            var window = new Window { Width = 640d, Height = 360d, ShowInTaskbar = false, WindowStyle = WindowStyle.None };
            try
            {
                PlaybackPanelViewModel panel = CreatePanel(testSettings, new FakeBmsPlayer(), new InMemoryPlaybackSettingsStore());
                panel.PlayerPanelState = PlayerPanelState.TITLE_SMALL | PlayerPanelState.BMS_PLAYER;
                var view = new PlaybackPanelView { DataContext = panel };
                window.Content = view;

                windowTest.ShowAndWaitForContentRendered(window);
                window.UpdateLayout();
                TestUiDispatcherHost.ProcessQueuedPresentation();

                Assert.AreEqual(PlayerPanelState.TITLE_SMALL, view.EffectivePlayerPanelState);
                AssertPlaybackPanelFinalState(view, compact: true);
                AssertPlaybackPanelHasNoAnimationClocks(view);
            }
            finally
            {
                (window.Content as PlaybackPanelView)?.DisposePlayerHost();
                window.Content = null;
                window.Close();
            }
        });
    }

    /// <summary>拡大型を明示した初回表示は最終値へ即時同期し、初期値からのanimationで表示を遅らせないことを確認します。</summary>
    /// <remarks>実WindowのLoaded・compiled binding・子HWNDまたはanimationを観測するため表示し、共有WPF資源を使うこのケースだけ非並列にします。</remarks>
    [TestMethod]
    [DoNotParallelize]
    public void PlaybackPanelView_InitiallySynchronizesExpandedStateWithoutAnimation()
    {
        TestUiDispatcherHost.RunWindowTest(windowTest =>
        {
            var window = new Window { Width = 640d, Height = 360d, ShowInTaskbar = false, WindowStyle = WindowStyle.None };
            try
            {
                PlaybackPanelViewModel panel = CreatePanel(testSettings, new FakeBmsPlayer(), new InMemoryPlaybackSettingsStore());
                panel.PlayerPanelState = PlayerPanelState.TITLE_LARGE;
                var view = new PlaybackPanelView { DataContext = panel };
                window.Content = view;

                windowTest.ShowAndWaitForContentRendered(window);
                window.UpdateLayout();
                TestUiDispatcherHost.ProcessQueuedPresentation();

                Assert.AreEqual(PlayerPanelState.TITLE_LARGE, view.EffectivePlayerPanelState);
                AssertPlaybackPanelFinalState(view, compact: false);
                AssertPlaybackPanelHasNoAnimationClocks(view);
            }
            finally
            {
                (window.Content as PlaybackPanelView)?.DisposePlayerHost();
                window.Content = null;
                window.Close();
            }
        });
    }

    /// <summary>表示済みViewのDataContextを小型・拡大型の双方へ交換し、旧VMから遷移せず各最終値を即時適用することを確認します。</summary>
    /// <remarks>実WindowのLoaded・compiled binding・子HWNDまたはanimationを観測するため表示し、共有WPF資源を使うこのケースだけ非並列にします。</remarks>
    [TestMethod]
    [DoNotParallelize]
    public void PlaybackPanelView_DataContextReplacementSynchronizesBothDirectionsWithoutAnimation()
    {
        TestUiDispatcherHost.RunWindowTest(windowTest =>
        {
            var window = new Window { Width = 640d, Height = 360d, ShowInTaskbar = false, WindowStyle = WindowStyle.None };
            try
            {
                PlaybackPanelViewModel expandedPanel = CreatePanel(testSettings, new FakeBmsPlayer(), new InMemoryPlaybackSettingsStore());
                expandedPanel.PlayerPanelState = PlayerPanelState.TITLE_LARGE;
                PlaybackPanelViewModel compactPanel = CreatePanel(testSettings, new FakeBmsPlayer(), new InMemoryPlaybackSettingsStore());
                compactPanel.PlayerPanelState = PlayerPanelState.TITLE_SMALL;
                var view = new PlaybackPanelView { DataContext = expandedPanel };
                window.Content = view;
                windowTest.ShowAndWaitForContentRendered(window);
                window.UpdateLayout();
                TestUiDispatcherHost.ProcessQueuedPresentation();

                view.DataContext = compactPanel;
                AssertPlaybackPanelFinalState(view, compact: true);
                AssertPlaybackPanelHasNoAnimationClocks(view);

                view.DataContext = expandedPanel;
                AssertPlaybackPanelFinalState(view, compact: false);
                AssertPlaybackPanelHasNoAnimationClocks(view);
            }
            finally
            {
                (window.Content as PlaybackPanelView)?.DisposePlayerHost();
                window.Content = null;
                window.Close();
            }
        });
    }

    /// <summary>同じVMの小型変更だけは遷移し、非表示でclockを解除して再読込み時は最新最終値へ即時同期し同じHWNDを保つことを確認します。</summary>
    /// <remarks>実WindowのLoaded・compiled binding・子HWNDまたはanimationを観測するため表示し、共有WPF資源を使うこのケースだけ非並列にします。</remarks>
    [TestMethod]
    [DoNotParallelize]
    public void PlaybackPanelView_SameViewModelStateChangesUseTransitionsAndReloadSynchronizesImmediately()
    {
        TestUiDispatcherHost.RunWindowTest(windowTest =>
        {
            var window = new Window { Width = 640d, Height = 360d, ShowInTaskbar = false, WindowStyle = WindowStyle.None };
            var view = new PlaybackPanelView();
            try
            {
                PlaybackPanelViewModel panel = CreatePanel(testSettings, new FakeBmsPlayer(), new InMemoryPlaybackSettingsStore());
                panel.PlayerPanelState = PlayerPanelState.TITLE_LARGE;
                view.DataContext = panel;
                window.Content = view;
                windowTest.ShowAndWaitForContentRendered(window);
                window.UpdateLayout();
                TestUiDispatcherHost.ProcessQueuedPresentation();

                IntPtr hostHandle = view.PlayerHostHandle;
                ExternalPlayerHostObservation.AssertOwnedChild(hostHandle, window);
                panel.PlayerPanelState = PlayerPanelState.TITLE_SMALL;
                window.UpdateLayout();
                TestUiDispatcherHost.ProcessQueuedPresentation();
                AssertPlaybackPanelHasTransitionClocks(view, compactTransition: true);

                panel.PlayerPanelState = PlayerPanelState.TITLE_LARGE;
                window.UpdateLayout();
                TestUiDispatcherHost.ProcessQueuedPresentation();
                AssertPlaybackPanelHasTransitionClocks(view, compactTransition: false);

                window.Content = null;
                window.UpdateLayout();
                TestUiDispatcherHost.ProcessQueuedPresentation();
                Assert.IsFalse(view.IsLoaded);
                Assert.IsTrue(ExternalPlayerHostObservation.IsWindow(hostHandle));
                Assert.AreEqual(hostHandle, view.PlayerHostHandle);
                AssertPlaybackPanelHasNoAnimationClocks(view);

                panel.PlayerPanelState = PlayerPanelState.TITLE_SMALL;
                window.Content = view;
                window.UpdateLayout();
                TestUiDispatcherHost.ProcessQueuedPresentation();
                Assert.IsTrue(view.IsLoaded);
                Assert.AreEqual(hostHandle, view.PlayerHostHandle);
                ExternalPlayerHostObservation.AssertOwnedChild(hostHandle, window);
                AssertPlaybackPanelFinalState(view, compact: true);
                AssertPlaybackPanelHasNoAnimationClocks(view);
            }
            finally
            {
                view.DisposePlayerHost();
                window.Content = null;
                window.Close();
            }
        });
    }

    /// <summary>Viewを再読込みした後は旧購読期間の開始通知と受理済み面要求を破棄し、新しい期間の通知と停止中の面要求だけ適用します。</summary>
    /// <remarks>実WindowのLoaded・compiled binding・子HWNDまたはanimationを観測するため表示し、共有WPF資源を使うこのケースだけ非並列にします。</remarks>
    [TestMethod]
    [DoNotParallelize]
    public void PlaybackPanelView_ReloadRejectsEventsQueuedByThePreviousSubscription()
    {
        var workspace = TestWorkspace.Create(TestRunDirectory.RootDirectory);
        bool workspaceDisposed = false;
        void DisposeWorkspace()
        {
            if (workspaceDisposed) { return; }
            workspaceDisposed = true;
            workspace.Dispose();
        }
        try
        {
            TestUiDispatcherHost.RunWindowTest(windowTest =>
            {
                string directory = workspace.RootDirectory;
                var queuedDispatcher = new QueuedPlaybackUiDispatcher();
                var panel = new PlaybackPanelViewModel(
                    CreateBmiPlayer(directory),
                    queuedDispatcher,
                    new MainChartListPlaybackQueue(new MainChartListViewModel()),
                    new InMemoryPlaybackSettingsStore(),
                    new FakePlaybackDialogService(),
                    _ => { },
                    new ChartFileOperationSynchronizer());
                var view = new PlaybackPanelView { DataContext = panel };
                var window = new Window
                {
                    Width = 640d,
                    Height = 360d,
                    Content = view,
                    ShowInTaskbar = false,
                    WindowStyle = WindowStyle.None
                };
                int startingCount = 0;
                int startedCount = 0;
                view.PlaybackStarting += (_, _) => startingCount++;
                view.PlaybackStarted += (_, _) => startedCount++;

                try
                {
                    windowTest.ShowAndWaitForContentRendered(window);
                    window.UpdateLayout();
                    TestUiDispatcherHost.ProcessQueuedPresentation();

                    panel.PlayerPanelState = PlayerPanelState.TITLE_LARGE;
                    TestUiDispatcherHost.AwaitTaskOnDispatcher(panel.Start(requestPlayerSurface: view.CaptureBmsPlayerSurfaceRequest()), "old-surface-request");
                    long oldGeneration = panel.BeginPlayback(ChartTestValues.Empty(), 0);
                    panel.NotifyPlaybackStarted(oldGeneration);
                    window.Content = null;
                    window.UpdateLayout();
                    TestUiDispatcherHost.ProcessQueuedPresentation();
                    window.Content = view;
                    window.UpdateLayout();
                    TestUiDispatcherHost.ProcessQueuedPresentation();

                    queuedDispatcher.RunAll();
                    Assert.AreEqual(0, startingCount);
                    Assert.AreEqual(0, startedCount);
                    Assert.AreEqual(PlayerPanelState.TITLE_LARGE, panel.PlayerPanelState);

                    TestUiDispatcherHost.AwaitTaskOnDispatcher(panel.StopPlayback(), "old-notification-session-stop");
                    queuedDispatcher.RunAll();
                    TestUiDispatcherHost.AwaitTaskOnDispatcher(panel.Start(requestPlayerSurface: view.CaptureBmsPlayerSurfaceRequest()), "current-surface-request");
                    long currentGeneration = panel.BeginPlayback(ChartTestValues.Empty(), 0);
                    panel.NotifyPlaybackStarted(currentGeneration);
                    queuedDispatcher.RunAll();
                    Assert.AreEqual(1, startingCount);
                    Assert.AreEqual(1, startedCount);
                    Assert.IsTrue(panel.PlayerPanelState.HasFlag(PlayerPanelState.BMS_PLAYER));
                }
                finally
                {
                    panel.BeginShutdown();
                    try { TestUiDispatcherHost.AwaitTaskOnDispatcher(panel.CloseForShutdown(), "reload-subscription-shutdown"); }
                    finally
                    {
                        view.DisposePlayerHost();
                        window.Content = null;
                        window.Close();
                        DisposeWorkspace();
                    }
                }
            });
        }
        finally { DisposeWorkspace(); }
    }

    /// <summary>DataContext交換・Unloadedで遅延した前曲ボタンの再開を取消し、旧購読通知とバナーを持ち越さず新VMの通知もUIへ安全に渡すことを確認します。</summary>
    /// <remarks>実WindowのLoaded・compiled binding・子HWNDまたはanimationを観測するため表示し、時計だけを明示配送へ置換します。共有WPF資源を使うため非並列にします。</remarks>
    [TestMethod]
    [DoNotParallelize]
    public void PlaybackPanelView_UnloadedCancelsPendingPreviousButtonRestart()
    {
        TestUiDispatcherHost.RunWindowTest(windowTest =>
        {
            var player = new FakeBmsPlayer();
            PlaybackPanelViewModel panel = CreatePanel(testSettings, player);
            var replacementPlayer = new FakeBmsPlayer();
            PlaybackPanelViewModel replacementPanel = CreatePanel(testSettings, replacementPlayer);
            var timers = new List<ManualPreviousButtonTimer>();
            var panels = new List<PlaybackPanelViewModel> { panel, replacementPanel };
            var view = new PlaybackPanelView(_ =>
            {
                var timer = new ManualPreviousButtonTimer();
                timers.Add(timer);
                return timer;
            })
            { DataContext = panel };
            int viewStartingCount = 0;
            int viewStartedCount = 0;
            view.PlaybackStarting += (_, _) => viewStartingCount++;
            view.PlaybackStarted += (_, _) => viewStartedCount++;
            var window = new Window
            {
                Width = 640d,
                Height = 360d,
                Content = view,
                ShowInTaskbar = false,
                WindowStyle = WindowStyle.None
            };

            Dispatcher previousDispatcher = Livet.DispatcherHelper.UIDispatcher;
            try
            {
                // 終了受付のコマンド通知も、このケースが所有するUI Dispatcherへ配送します。
                Livet.DispatcherHelper.UIDispatcher = Dispatcher.CurrentDispatcher;
                windowTest.ShowAndWaitForContentRendered(window);
                Assert.IsTrue(view.IsLoaded);

                long generation = panel.BeginPlayback(ChartTestValues.Empty(), 0);
                panel.NotifyPlaybackStarted(generation);
                Assert.AreEqual(1, viewStartingCount);
                Assert.AreEqual(1, viewStartedCount);

                Task restartObserved = player.WaitForCommandAsync("Restart");
                view.HandlePreviousButtonClick(1);
                ManualPreviousButtonTimer initialTimer = timers.Single();
                Assert.AreEqual(1, initialTimer.StartCount);
                Assert.AreEqual(1, initialTimer.SubscriberCount);
                initialTimer.DeliverTick();
                TestUiDispatcherHost.AwaitTaskOnDispatcher(
                    restartObserved,
                    "PlaybackPanelView_UnloadedCancelsPendingPreviousButtonRestart.initial-restart");
                while (player.Commands.TryDequeue(out _))
                {
                }

                Assert.AreEqual(1, initialTimer.DeliveryCount);
                view.HandlePreviousButtonClick(1);
                Assert.AreEqual(2, initialTimer.StartCount);
                int initialStops = initialTimer.StopCount;
                view.DataContext = replacementPanel;
                AssertPreviousButtonTimerCancelled(initialTimer, initialStops);
                Assert.IsFalse(player.Commands.Contains("Restart"));
                Assert.IsFalse(replacementPlayer.Commands.Contains("Restart"));

                Task replacementRestartObserved = replacementPlayer.WaitForCommandAsync("Restart");
                view.HandlePreviousButtonClick(1);
                ManualPreviousButtonTimer replacementTimer = timers.Last();
                Assert.AreNotSame(initialTimer, replacementTimer);
                Assert.AreEqual(1, replacementTimer.StartCount);
                Assert.AreEqual(1, replacementTimer.SubscriberCount);
                replacementTimer.DeliverTick();
                TestUiDispatcherHost.AwaitTaskOnDispatcher(
                    replacementRestartObserved,
                    "PlaybackPanelView_UnloadedCancelsPendingPreviousButtonRestart.replacement-restart");
                while (replacementPlayer.Commands.TryDequeue(out _))
                {
                }

                var queuedDispatcher = new QueuedPlaybackUiDispatcher();
                var queuedPanel = new PlaybackPanelViewModel(
                    new FakeBmsPlayer(),
                    queuedDispatcher,
                    new MainChartListPlaybackQueue(new MainChartListViewModel()),
                    new SettingsPlaybackSettingsStore(() => testSettings),
                    new FakePlaybackDialogService(),
                    _ => { },
                    new ChartFileOperationSynchronizer());
                panels.Add(queuedPanel);
                view.DataContext = queuedPanel;
                long queuedGeneration = queuedPanel.BeginPlayback(ChartTestValues.Empty(), 0);
                queuedPanel.NotifyPlaybackStarted(queuedGeneration);
                view.DataContext = replacementPanel;
                queuedDispatcher.RunAll();
                Assert.AreEqual(1, viewStartingCount);
                Assert.AreEqual(1, viewStartedCount);

                var banner = (Border)view.FindName("gridBMSPlayerControlsBanner");
                banner.Background = Brushes.Red;
                banner.BorderThickness = new Thickness(1);
                PlaybackPanelViewModel bannerPanel = CreatePanel(testSettings, new FakeBmsPlayer());
                panels.Add(bannerPanel);
                view.DataContext = bannerPanel;
                Assert.IsNull(banner.Background);
                Assert.AreEqual(new Thickness(0), banner.BorderThickness);
                view.DataContext = replacementPanel;

                Exception? backgroundException = null;
                var backgroundThread = new Thread(() =>
                {
                    try
                    {
                        replacementPanel.SetBmsPlayerHeader(ChartTestValues.Empty());
                    }
                    catch (Exception ex)
                    {
                        backgroundException = ex;
                    }
                });
                backgroundThread.Start();
                backgroundThread.Join();
                TestUiDispatcherHost.ProcessQueuedPresentation();
                Assert.IsNull(backgroundException);

                view.HandlePreviousButtonClick(1);
                ManualPreviousButtonTimer unloadedTimer = timers.Last();
                Assert.AreEqual(1, unloadedTimer.StartCount);
                Assert.AreEqual(1, unloadedTimer.SubscriberCount);
                int unloadedStops = unloadedTimer.StopCount;
                window.Content = null;
                window.UpdateLayout();
                TestUiDispatcherHost.ProcessQueuedPresentation();

                Assert.IsFalse(view.IsLoaded);
                AssertPreviousButtonTimerCancelled(unloadedTimer, unloadedStops);
                Assert.IsFalse(replacementPlayer.Commands.Contains("Restart"));

                window.Content = view;
                window.UpdateLayout();
                TestUiDispatcherHost.ProcessQueuedPresentation();
                Assert.IsTrue(view.IsLoaded);
                Task reloadedRestartObserved = replacementPlayer.WaitForCommandAsync("Restart");
                view.HandlePreviousButtonClick(1);
                ManualPreviousButtonTimer reloadedTimer = timers.Last();
                Assert.AreNotSame(unloadedTimer, reloadedTimer);
                Assert.AreEqual(1, reloadedTimer.StartCount);
                Assert.AreEqual(1, reloadedTimer.SubscriberCount);
                unloadedTimer.DeliverTick();
                Assert.AreEqual(0, unloadedTimer.DeliveryCount);
                Assert.IsFalse(unloadedTimer.IsRunning);
                reloadedTimer.DeliverTick();
                TestUiDispatcherHost.AwaitTaskOnDispatcher(
                    reloadedRestartObserved,
                    "PlaybackPanelView_UnloadedCancelsPendingPreviousButtonRestart.reloaded-restart");
                Assert.AreEqual(1, reloadedTimer.DeliveryCount);
            }
            finally
            {
                try
                {
                    window.Content = null;
                    view.RaiseEvent(new RoutedEventArgs(FrameworkElement.UnloadedEvent));
                    foreach (PlaybackPanelViewModel createdPanel in panels) { createdPanel.BeginShutdown(); }
                    TestUiDispatcherHost.AwaitTaskOnDispatcher(
                        Task.WhenAll(panels.Select(createdPanel => createdPanel.CloseForShutdown())),
                        "previous-button-cancellation-shutdown");
                }
                finally
                {
                    try { view.DisposePlayerHost(); }
                    finally
                    {
                        try { window.Close(); }
                        finally { Livet.DispatcherHelper.UIDispatcher = previousDispatcher; }
                    }
                }
            }
        });
    }

    /// <summary>公開の既定構成で500msの実Dispatcherタイマーから再開コマンドへ接続することを確認します。</summary>
    /// <remarks>実Loadedと既定時計の接続だけを担い、取消しは明示配送、クリック判定はCoreのジェスチャーテストへ分担します。</remarks>
    [TestMethod]
    [DoNotParallelize]
    public void PlaybackPanelView_DefaultPreviousButtonTimerRestartsPlayback()
    {
        TestUiDispatcherHost.RunWindowTest(windowTest =>
        {
            var player = new FakeBmsPlayer();
            PlaybackPanelViewModel panel = CreatePanel(testSettings, player);
            var view = new PlaybackPanelView { DataContext = panel };
            var window = new Window
            {
                Width = 640d,
                Height = 360d,
                Content = view,
                ShowInTaskbar = false,
                WindowStyle = WindowStyle.None
            };
            Dispatcher previousDispatcher = Livet.DispatcherHelper.UIDispatcher;
            try
            {
                // 終了受付のコマンド通知も、このケースが所有するUI Dispatcherへ配送します。
                Livet.DispatcherHelper.UIDispatcher = Dispatcher.CurrentDispatcher;
                windowTest.ShowAndWaitForContentRendered(window);
                Assert.IsTrue(view.IsLoaded);
                Task restartObserved = player.WaitForCommandAsync("Restart");
                view.HandlePreviousButtonClick(1);
                TestUiDispatcherHost.AwaitTaskOnDispatcher(
                    restartObserved, "PlaybackPanelView_DefaultPreviousButtonTimerRestartsPlayback.restart");
                Assert.AreEqual(1, player.Commands.Count(command => command == "Restart"));
            }
            finally
            {
                try
                {
                    window.Content = null;
                    view.RaiseEvent(new RoutedEventArgs(FrameworkElement.UnloadedEvent));
                    panel.BeginShutdown();
                    TestUiDispatcherHost.AwaitTaskOnDispatcher(panel.CloseForShutdown(), "previous-button-default-shutdown");
                }
                finally
                {
                    try { view.DisposePlayerHost(); }
                    finally
                    {
                        try { window.Close(); }
                        finally { Livet.DispatcherHelper.UIDispatcher = previousDispatcher; }
                    }
                }
            }
        });
    }

    private static void AssertPreviousButtonTimerCancelled(ManualPreviousButtonTimer timer, int previousStopCount)
    {
        Assert.AreEqual(previousStopCount + 1, timer.StopCount, "Viewが保留中の時計を停止します。");
        Assert.IsFalse(timer.IsRunning);
        Assert.AreEqual(0, timer.SubscriberCount, "停止とは別にViewがTick購読を解除します。");
        int attempts = timer.DeliveryAttemptCount;
        int deliveries = timer.DeliveryCount;
        timer.DeliverTick();
        Assert.AreEqual(attempts + 1, timer.DeliveryAttemptCount);
        Assert.AreEqual(deliveries, timer.DeliveryCount, "旧時計から明示配送してもViewは受信しません。");
    }

    /// <summary>停止と購読解除を独立して観測し、停止後も旧通知の配送を試せる前曲ボタン専用時計。</summary>
    private sealed class ManualPreviousButtonTimer : PlaybackPanelView.IPreviousButtonTimer
    {
        public event EventHandler? Tick;
        internal int StartCount { get; private set; }
        internal int StopCount { get; private set; }
        internal int DeliveryAttemptCount { get; private set; }
        internal int DeliveryCount { get; private set; }
        internal bool IsRunning { get; private set; }
        internal int SubscriberCount => Tick?.GetInvocationList().Length ?? 0;

        public void Start()
        {
            StartCount++;
            IsRunning = true;
        }

        public void Stop()
        {
            StopCount++;
            IsRunning = false;
        }

        internal void DeliverTick()
        {
            DeliveryAttemptCount++;
            // Stopは購読を消さず、配送も抑えません。解除漏れを停止状態で隠さないためです。
            foreach (EventHandler handler in Tick?.GetInvocationList() ?? [])
            {
                DeliveryCount++;
                handler(this, EventArgs.Empty);
            }
        }
    }

    private static void AssertPlaybackPanelFinalState(PlaybackPanelView view, bool compact)
    {
        var image = (Image)view.FindName("gridBMSPlayerImage");
        var blurEffect = (BlurEffect)image.Effect;
        var titlePanel = (DockPanel)view.FindName("gridBMSPlayerTitlePanel");
        var banner = (Border)view.FindName("gridBMSPlayerControlsBanner");
        var compactTitle = (TextBlock)view.FindName("gridBMSPlayerControlsTitle2");

        Assert.AreEqual(compact ? 110d : 286d, view.Height, 0.001d);
        Assert.AreEqual(compact ? 20d : 0d, blurEffect.Radius, 0.001d);
        Assert.AreEqual(compact ? new Thickness(0d, 0d, 0d, -40d) : new Thickness(0d), titlePanel.Margin);
        Assert.AreEqual(compact ? 1d : 0d, banner.Opacity, 0.001d);
        Assert.AreEqual(1d, compactTitle.Opacity, 0.001d);
    }

    private static void AssertPlaybackPanelHasNoAnimationClocks(PlaybackPanelView view)
    {
        var image = (Image)view.FindName("gridBMSPlayerImage");
        var blurEffect = (BlurEffect)image.Effect;
        var titlePanel = (DockPanel)view.FindName("gridBMSPlayerTitlePanel");
        var banner = (Border)view.FindName("gridBMSPlayerControlsBanner");
        var compactTitle = (TextBlock)view.FindName("gridBMSPlayerControlsTitle2");

        Assert.IsFalse(IsAnimated(view, FrameworkElement.HeightProperty), "Height must not have an AnimationClock.");
        Assert.IsFalse(IsAnimated(blurEffect, BlurEffect.RadiusProperty), "Blur radius must not have an AnimationClock.");
        Assert.IsFalse(IsAnimated(titlePanel, FrameworkElement.MarginProperty), "Large title margin must not have an AnimationClock.");
        Assert.IsFalse(IsAnimated(banner, UIElement.OpacityProperty), "Banner opacity must not have an AnimationClock.");
        Assert.IsFalse(IsAnimated(compactTitle, UIElement.OpacityProperty), "Compact title opacity must not have an AnimationClock.");
    }

    private static void AssertPlaybackPanelHasTransitionClocks(PlaybackPanelView view, bool compactTransition)
    {
        var image = (Image)view.FindName("gridBMSPlayerImage");
        var blurEffect = (BlurEffect)image.Effect;
        var titlePanel = (DockPanel)view.FindName("gridBMSPlayerTitlePanel");
        var banner = (Border)view.FindName("gridBMSPlayerControlsBanner");
        var compactTitle = (TextBlock)view.FindName("gridBMSPlayerControlsTitle2");

        Assert.IsTrue(IsAnimated(view, FrameworkElement.HeightProperty), "Height must enter the transition route.");
        Assert.IsTrue(IsAnimated(blurEffect, BlurEffect.RadiusProperty), "Blur radius must enter the transition route.");
        Assert.IsTrue(IsAnimated(titlePanel, FrameworkElement.MarginProperty), "Large title margin must enter the transition route.");
        Assert.IsTrue(IsAnimated(banner, UIElement.OpacityProperty), "Banner opacity must enter the transition route.");
        Assert.AreEqual(
            compactTransition,
            IsAnimated(compactTitle, UIElement.OpacityProperty),
            "Compact title fades only while entering compact mode.");
    }

    private static bool IsAnimated(DependencyObject target, DependencyProperty property) =>
        DependencyPropertyHelper.GetValueSource(target, property).IsAnimated;

    /// <summary>Pause中・演奏完了後にも管理入力の解除は停止へ収束し、停止待ち中の試聴を予約せず、次の明示要求だけを開始します。</summary>
    /// <remarks>実panelと共通受付・目録store・SQLite/FSを接続します。playerのclose待機は状態・受付の保証で、実decode終端はAudioContractsTestsが担当します。</remarks>
    [TestMethod]
    // 独立Lを持つfixtureが同一processの共通session markerを初期公開するため、準備から開始Task終端・固有root回収まで非並列で所有します。本番の同一Lによる直列化と、case内のBusy・非予約・次明示要求の判定は維持します。
    [DoNotParallelize]
    [DataRow(false)] // Pauseした現在曲と、異なる管理入力の先読み候補。
    [DataRow(true)] // 完了して現在曲が外れた後でも管理入力を回収。
    public async Task PendingRemoval_ConvergesPausedOrCompletedPlaybackAndRejectsNewStart(bool completed)
    {
        var workspace = TestWorkspace.Create(TestRunDirectory.RootDirectory);
        bool workspaceDisposed = false;
        void DisposeWorkspace()
        {
            if (workspaceDisposed) { return; }
            workspaceDisposed = true;
            workspace.Dispose();
        }
        try
        {
            var databaseScope = new ApplicationDatabaseScope();
            string root = workspace.RootDirectory;
            string managed = TempDirectoryPublisher.Get("pending-panel");
            Directory.CreateDirectory(root);
            string current = Path.Combine(root, "current.bms");
            string pendingPath = Path.Combine(managed, "pending.bms");
            File.WriteAllText(current, "#TITLE current");
            File.WriteAllText(pendingPath, "#TITLE pending");
            string db = Path.Combine(root, "song.db");
            new BmsLibraryDbGateway(databaseScope, new BmsDatabaseLoadOptions(true, true, true), db).EnsureAppOwnedSchema();
            new BmsLibraryDbGateway(databaseScope, new BmsDatabaseLoadOptions(true, true, true), db).EnsureLibraryStartupSchema();
            var library = new TestBmsLibrary(databaseScope, db, null, null, new ResilientFileMutationService(),
                new FileDbReportRecordingDialogs(), new TestUiScheduler(() => null));
            library.ChartPackagesPending = new ObservableCollection<ChartPackage>([new ChartPackage { path = managed }]);
            var completion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var cleanup = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var player = new PreloadBmsPlayer { Completion = completion.Task };
            var rows = new MainChartListViewModel { Rows = new List<object> { ChartTestValues.Empty() with { Path = current }, ChartTestValues.Empty() with { Path = pendingPath } } };
            var panel = new PlaybackPanelViewModel(player, new ImmediatePlaybackUiDispatcher(), new MainChartListPlaybackQueue(rows),
                new InMemoryPlaybackSettingsStore { SinglePlay = true }, new FakePlaybackDialogService(), failure => Assert.Fail(failure.ToString()), library.OperationAdmission);
            var owner = new PackageCatalogWorkflowOwner(() => library, library.OperationAdmission, new ChartMutationActivityOwner(),
                new FileDbReportRecordingDialogs(), panel, mutation => Task.Run(mutation));
            Task<PackageCatalogMutationResult>? removal = null;
            try
            {
                await panel.StartAtIndex(0);
                if (completed)
                {
                    completion.SetResult();
                    await panel.Next(player);
                    Assert.IsNull(panel.NowPlayingChart);
                }
                else
                {
                    panel.TogglePause();
                    Assert.IsTrue(panel.IsPaused);
                }
                player.CloseCompletion = () => { entered.TrySetResult(); return cleanup.Task; };
                int preparations = player.Preloads.Count;
                removal = owner.ClearAllAsync(PackageCatalogSection.Pending);
                await TestUiDispatcherHost.AwaitNotificationAsync(entered.Task, removal, "pending-panel-stop");
                Assert.IsTrue(File.Exists(pendingPath));
                Assert.IsTrue(library.OperationAdmission.IsActive);
                await panel.StartAtIndex(0);
                Assert.AreEqual(1, player.Starts.Count);
                cleanup.SetResult();
                Assert.IsTrue((await removal).Succeeded);
                Assert.IsFalse(Directory.Exists(managed));
                Assert.IsNull(panel.NowPlayingChart);
                Assert.IsFalse(panel.IsPlaying);
                Assert.IsFalse(panel.IsPaused);
                Assert.IsTrue(panel.IsStoppedOrPaused);
                Assert.AreEqual(0, panel.PlaybackStatusValue & (int)ChartFileStatus.PLAYALL);
                Assert.AreEqual(preparations, player.Preloads.Count, "停止後に先読みを再登録しません。");
                Assert.AreEqual(1, player.Starts.Count, "停止後に自動再開しません。");
                Assert.IsFalse(library.OperationAdmission.IsActive);
                player.CloseCompletion = null;
                await panel.StartAtIndex(0);
                Assert.AreEqual(2, player.Starts.Count);
            }
            finally
            {
                cleanup.TrySetResult();
                if (removal != null) { await removal; }
                player.CloseCompletion = null;
                completion.TrySetResult();
                await panel.CloseForShutdown();
                await completion.Task;
                library.RequestShutdown("pending-panel-test");
                TempDirectoryPublisher.TryDeleteManagedPath(managed);
                DisposeWorkspace();
            }
        }
        finally { DisposeWorkspace(); }
    }
}
