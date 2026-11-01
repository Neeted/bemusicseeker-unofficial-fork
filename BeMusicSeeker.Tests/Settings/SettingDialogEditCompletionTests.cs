using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Data;
using System.Windows.Threading;
using BeMusicSeeker.Models;
using BeMusicSeeker.Models.BmsLibraryInternal;
using BeMusicSeeker.Models.LR2;
using BeMusicSeeker.Models.Utils;
using BeMusicSeeker.Properties;
using BeMusicSeeker.ViewModels;
using BeMusicSeeker.Views;
using BeMusicSeeker.Views.Dialogs;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Newtonsoft.Json.Linq;

using static BeMusicSeeker.Tests.SettingsRootTestFixture;
using static BeMusicSeeker.Tests.SettingsWindowTestFixture;

namespace BeMusicSeeker.Tests;

/// <summary>設定適用の保存・後続処理・失敗／取消・実 owner 接続を検証します。</summary>
/// <remarks>共有 culture・theme・Bass 受付と composition 購読を使うため非並列にします。</remarks>
[TestClass]
[DoNotParallelize]
public sealed class SettingDialogEditCompletionTests
{
    /// <summary>このケースの準備とモデルで共有するDBの所有者です。</summary>
    private readonly ApplicationDatabaseScope databaseScope = new();

    private static SettingsDialogViewModel CreateOperationModeDialog(
        CountingSettingsEditSession settingsSession,
        RecordingRootDialogService dialogs,
        IApplicationLifetimePort applicationLifetime)
    {
        MainWindowViewModel owner = MainWindowViewModelTestFactory.Create();
        owner.SettingDialog.Dispose();
        SetActiveLibraryProfile(owner, true);
        return new SettingsDialogViewModel(MainWindowViewModelTestFactory.GetComposition(owner).DatabaseScope,
            new TestSettingsDialogStatePort(owner, () => Task.FromResult(new StartupInitializationResult(StartupInitializationOutcome.Succeeded))),
            owner.PlaylistWorkspace,
            owner.PlaylistWorkspace,
            owner.PlayHistory,
            owner.LibraryFolderTree,
            new TestSettingsDialogPlayerFactoryPort(),
            new TestSettingsDialogPlaybackRuntimePort(),
            owner.Lr2SongDbSyncWorkflow,
            settingsSession, new ApplicationDataUninstallWorkflowOwner(dialogs, new Lr2ApplicationDataUninstallStore(MainWindowViewModelTestFactory.GetComposition(owner).DatabaseScope)),
            requestOperationModeRestart: request =>
            {
                request.BeforeSave?.Invoke();
                settingsSession.SaveEnvironmentForRestart(request.Environment, request.HistoryIdentity);
                return Task.FromResult(true);
            },
            applicationLifetime: applicationLifetime,
            cultureCatalog: TestApplicationContext.CreateCultureCatalog(),
            schemaDialogs: dialogs,
            externalShellGateway: ExternalShellGatewayPolicy.Current,
            applicationPathSnapshot: ApplicationPathPolicy.Current,
            audioDeviceCatalog: new TestAudioDeviceCatalog(),
            audioSettingsGateway: new TestAudioSettingsGateway(),
            audioDeviceTestWorkflow: AudioDeviceTestWorkflowTestFactory.Create());
    }

    /// <summary>未設定・欠損・不正・占有 config／DB の起動は設定案内へ終端し、初回と通常の案内順を分け、元ファイルと raw path を保って修復・出力を始めないことを確認します。</summary>
    [TestMethod]
    [DataRow("null-path", false)]
    [DataRow("null-path", true)]
    [DataRow("empty-path", false)]
    [DataRow("empty-path", true)]
    [DataRow("whitespace-path", false)]
    [DataRow("whitespace-path", true)]
    [DataRow("missing-file", false)]
    [DataRow("missing-file", true)]
    [DataRow("invalid-path", false)]
    [DataRow("invalid-path", true)]
    [DataRow("malformed-xml", false)]
    [DataRow("malformed-xml", true)]
    [DataRow("missing-jukebox", false)]
    [DataRow("missing-jukebox", true)]
    [DataRow("wrong-root", false)]
    [DataRow("wrong-root", true)]
    [DataRow("locked-file", false)]
    [DataRow("locked-file", true)]
    [DataRow("missing-song-db", false)]
    [DataRow("missing-song-db", true)]
    public void InitializeAsync_InvalidLr2SettingsUseSettingsGuidanceAndPreserveFiles(
        string invalidSetting,
        bool firstStartup)
    {
        string root = CreateTemporaryRoot();
        string lr2Root = Path.Combine(root, "lr2");
        string outputBase = Path.Combine(root, "output-base");
        string rootOutputBase = Path.Combine(root, "root-output-base");
        var applicationPath = ApplicationPathSnapshot.FromExecutablePath(
            Path.Combine(root, "application", "BeMusicSeeker.exe"));
        try
        {
            (string songDb, string configPath) = CreateValidLr2Layout(lr2Root);
            switch (invalidSetting)
            {
                case "malformed-xml":
                    File.WriteAllText(configPath, "<config>");
                    break;
                case "missing-jukebox":
                    File.WriteAllText(configPath, "<config><system /></config>");
                    break;
                case "wrong-root":
                    File.WriteAllText(configPath, "<other><jukebox /></other>");
                    break;
            }
            byte[] configBefore = File.ReadAllBytes(configPath);
            byte[] songDbBefore = File.ReadAllBytes(songDb);
            string? rawConfigPath = invalidSetting switch
            {
                "null-path" => null,
                "empty-path" => string.Empty,
                "whitespace-path" => "   ",
                "missing-file" => Path.Combine(root, "missing", "config.xml"),
                "invalid-path" => "invalid\0path",
                _ => configPath
            };
            Settings settings = CreateValidStandaloneSettings(lr2Root);
            settings.OperationModeLR2DB = true;
            settings.LR2RootPath = lr2Root;
            settings.LR2ConfigXmlPath = rawConfigPath!;
            settings.LR2SongDBPath = invalidSetting == "missing-song-db"
                ? Path.Combine(root, "missing", "song.db")
                : songDb;
            settings.LR2CustomFolderOutputBaseDir = outputBase;
            settings.LR2CustomFolderOutputBaseDirRootType = rootOutputBase;
            settings.LR2CustomFolderAdditionalOutputBaseDirs = string.Empty;
            if (invalidSetting == "missing-song-db")
            {
                // ディレクトリ検査には通るが設定全体は無効な状態で、XML修復を開始しないことを確認します。
                Directory.CreateDirectory(outputBase);
                Directory.CreateDirectory(rootOutputBase);
            }
            var settingsSession = new CountingSettingsEditSession(settings);
            var dialogs = new RecordingRootDialogService();
            var sequence = new List<string>();
            using (FileStream? lockedConfig = invalidSetting == "locked-file"
                ? new FileStream(configPath, FileMode.Open, FileAccess.ReadWrite, FileShare.None)
                : null)
            {
                TestUiDispatcherHost.Invoke(() =>
                {
                    MainWindowViewModel viewModel = new ApplicationComposition(
                        settingsEditSession: settingsSession,
                        uiScheduler: new TestUiScheduler(() => TestUiDispatcherHost.Dispatcher),
                        applicationLifetime: TestApplicationContext.CreateLifetime(firstStartup),
                        cultureCatalog: TestApplicationContext.CreateCultureCatalog(),
                        runtimeOptions: MainWindowViewModelTestFactory.CreateRuntimeOptions(applicationPath),
                        fileDbMutationDialogService: dialogs)
                        .CreateMainWindowViewModel();
                    void ObserveGuidance(string request)
                    {
                        Assert.IsFalse(viewModel.ProgressHub.StartupProgress.IsStartupUiInteractionBlocked);
                        Assert.IsFalse(viewModel.IsLibraryOperationInProgress);
                        sequence.Add(request);
                    }
                    dialogs.MessageObserved = () => ObserveGuidance("warning");
                    var presentation = new RecordingSettingsDialogPresentationPort(ObserveGuidance);
                    viewModel.SettingDialog.AttachPresentationPort(presentation);
                    try
                    {
                        Assert.IsFalse(viewModel.SettingDialog.CheckValidationForInitialization(out string validationError));
                        StringAssert.Contains(validationError, Resources.Error_InvalidLR2SongDbOrConfigPath);

                        Task<bool> initialization = viewModel.InitializeAsync();
                        TestUiDispatcherHost.AwaitTaskOnDispatcher(initialization, "invalid-lr2-settings-startup");

                        Assert.IsFalse(initialization.GetAwaiter().GetResult());
                        Assert.IsFalse(viewModel.IsInitializationCompleted);
                        Assert.IsFalse(viewModel.HasActiveLibraryProfile);
                        Assert.IsFalse(viewModel.ProgressHub.StartupProgress.IsStartupUiInteractionBlocked);
                        Assert.AreEqual(firstStartup ? 0 : 1, dialogs.MessageCount);
                        Assert.AreEqual(0, dialogs.ConfirmationCount);
                        CollectionAssert.AreEqual(
                            firstStartup ? new[] { "initial-setup" } : new[] { "warning", "open" },
                            sequence);
                        if (!firstStartup)
                        {
                            StringAssert.Contains(dialogs.LastMessageText, Resources.Msg_init_settings_check);
                        }
                        Assert.AreEqual(rawConfigPath, viewModel.SettingDialog.LR2ConfigXmlPath);
                        Assert.AreEqual(lr2Root, viewModel.SettingDialog.LR2RootPath);
                        Assert.AreEqual(settings.LR2SongDBPath, viewModel.SettingDialog.LR2SongDBPath);
                        Assert.AreEqual(0, settingsSession.SaveCount);
                    }
                    finally
                    {
                        TestUiDispatcherHost.AwaitTaskOnDispatcher(
                            viewModel.ShellShutdownWorkflow.RequestWindowCloseAsync(),
                            "invalid-lr2-settings-shutdown");
                        viewModel.SettingDialog.Dispose();
                    }
                });
            }
            CollectionAssert.AreEqual(configBefore, File.ReadAllBytes(configPath));
            CollectionAssert.AreEqual(songDbBefore, File.ReadAllBytes(songDb));
            Assert.IsFalse(File.Exists(applicationPath.StandaloneSongDbPath));
            Assert.IsFalse(Directory.Exists(Path.Combine(root, "missing")));
            if (invalidSetting != "missing-song-db")
            {
                Assert.IsFalse(Directory.Exists(outputBase));
                Assert.IsFalse(Directory.Exists(rootOutputBase));
            }
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    // MSTest が各ケース開始前に注入する。fixture cleanup の診断に使用する。
    public TestContext TestContext { get; set; } = null!;

    /// <summary>culture変更で表示カタログと音声ownerから画面へ転送する二つの選択肢名を更新し、二度Disposeした後はどちらにも通知しないことを確認して、共有リソースへの購読漏れを検出します。</summary>
    [TestMethod]
    public void DisposedSettingsDialogStopsListeningToResourceServiceCultureChanges()
    {
        TestUiDispatcherHost.Invoke(() =>
        {
            string previousCulture = Resources.Culture?.Name ?? "ja-JP";
            SettingsDialogViewModel? dialog = null;
            try
            {
                AudioDeviceTestWorkflowOwner workflow = AudioDeviceTestWorkflowTestFactory.Create();
                Settings values = TestSettingsFactory.CreateIsolatedSettings();
                using var harness = SettingsDialogHarness.Create(values,
                    settingsEditSession: new NoOpSettingsEditSession(values),
                    audioDeviceTestWorkflow: workflow);
                dialog = harness.Dialog;
                var ownerNotifications = new List<string?>();
                var viewModelNotifications = new List<string?>();
                workflow.PropertyChanged += (_, args) => ownerNotifications.Add(args.PropertyName);
                dialog.PropertyChanged += (_, args) => viewModelNotifications.Add(args.PropertyName);

                ResourceService.Current.ChangeCulture("en-US");
                foreach (string name in new[] { nameof(SettingsDialogViewModel.PlayerResamplingQualityNames), nameof(SettingsDialogViewModel.PlayerMixerThreadCountNames) })
                {
                    CollectionAssert.Contains(ownerNotifications, name);
                    CollectionAssert.Contains(viewModelNotifications, name);
                }

                dialog.Dispose();
                dialog.Dispose();
                ownerNotifications.Clear();
                viewModelNotifications.Clear();

                ResourceService.Current.ChangeCulture("ja-JP");
                Assert.AreEqual(0, ownerNotifications.Count);
                Assert.AreEqual(0, viewModelNotifications.Count);
            }
            finally
            {
                dialog?.Dispose();
                ResourceService.Current.ChangeCulture(previousCulture);
            }
        });
    }

    /// <summary>実音量Sliderのpreviewは現在再生runtimeだけへ届き、操作終端で音量だけを実保存します。同値終端は保存0、失敗は表示・現在再生・readerを最新確定値へ戻します。</summary>
    /// <remarks>実provider・composition・Internal playerから音声runtime境界までを接続します。OS音声出力だけを固定代替し、別項目保存と終了時保存にもpreviewを混ぜません。dragの失敗行は先行保存60の後のpreview70を失敗させ、初期値50でなく最新確定60へ戻すことを確認します。</remarks>
    [TestMethod]
    [DataRow("drag", false)]
    [DataRow("mouse", false)]
    [DataRow("keyboard", false)]
    [DataRow("close", false)]
    [DataRow("drag", true)]
    [DataRow("close", true)]
    public void SettingDialogVolumeBinding_UsesComposedPlaybackOwner(string completion, bool failSave)
    {
        TestUiDispatcherHost.RunWindowTest(windowTest =>
        {
            string root = CreateTemporaryRoot();
            string path = Path.Combine(root, "user.config");
            SettingsWindow? window = null;
            SettingsDialogViewModel? dialog = null;
            MainWindowViewModel? owner = null;
            var runtime = new VolumePreviewPlaybackRuntime();
            try
            {
                Settings values = CreateValidStandaloneSettings(root, TestSettingsFactory.OpenSettings(path));
                values.uBMplayVolume = 50;
                values.Save();
                var gateway = new SettingsPlayerSettingsGateway(() => values);
                var player = new InternalBMSAutoPlayerSoundOnly(gateway, runtime);
                var failures = new List<Exception>();
                var composition = new ApplicationComposition(runtimeOptions: MainWindowViewModelTestFactory.CreateRuntimeOptions(), settingsEditSession: new SettingsEditSession(values),
                    defaultBmsPlayerFactory: () => player, reportSettingsApplyFailure: failures.Add,
                    uiScheduler: new WpfUiScheduler(() => Dispatcher.CurrentDispatcher),
                    applicationLifetime: TestApplicationContext.CreateLifetime(), cultureCatalog: TestApplicationContext.CreateCultureCatalog());
                owner = composition.CreateMainWindowViewModelForTest();
                owner.SettingDialog.Dispose();
                dialog = new SettingsDialogViewModel(composition.DatabaseScope, owner, owner.PlaylistWorkspace, owner.PlaylistWorkspace,
                    owner.PlayHistory, owner.LibraryFolderTree, composition, owner.PlaybackPanel, owner.Lr2SongDbSyncWorkflow,
                    new SettingsEditSession(values), new ApplicationDataUninstallWorkflowOwner(new UiDialogCoordinator(), new Lr2ApplicationDataUninstallStore(composition.DatabaseScope)), reportApplyFailure: failures.Add,
                    applicationLifetime: TestApplicationContext.CreateLifetime(), cultureCatalog: TestApplicationContext.CreateCultureCatalog(),
                    externalShellGateway: ExternalShellGatewayPolicy.Current, applicationPathSnapshot: ApplicationPathPolicy.Current,
                    audioDeviceCatalog: new TestAudioDeviceCatalog(), audioSettingsGateway: new SettingsAudioGateway(() => values),
                    audioDeviceTestWorkflow: AudioDeviceTestWorkflowTestFactory.Create());
                window = new SettingsWindow { DataContext = dialog, PlaybackPanel = owner.PlaybackPanel };
                dialog.AttachPresentationPort(new RecordingSettingsDialogPresentationPort(request =>
                { if (request == "close") { window.CloseFromPresentation(); } }));
                var closed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
                window.Closed += (_, _) => closed.TrySetResult();
                windowTest.ShowAndWaitForContentRendered(window);
                ((ListBox)window.FindName("settingsNavigation")).SelectedItem = window.FindName("navigationAudio");
                TestUiDispatcherHost.ProcessQueuedPresentation();
                Slider slider = FindDescendants<Slider>(window).Single(item => item.Name == "audioVolumeSlider");
                TextBlock text = FindDescendants<TextBlock>(window).Single(item =>
                    item.GetBindingExpression(TextBlock.TextProperty)?.ParentBinding.Path?.Path == "PlaybackPanel.PlayerVolume");
                if (completion == "keyboard") { slider.Focus(); System.Windows.Input.Keyboard.Focus(slider); }
                slider.SetCurrentValue(Slider.ValueProperty, 40d);
                slider.SetCurrentValue(Slider.ValueProperty, 60d);
                TestUiDispatcherHost.ProcessQueuedPresentation();
                CollectionAssert.AreEqual(new[] { 40, 60 }, runtime.Volumes.ToArray());
                Assert.AreEqual(60, owner.PlaybackPanel.PlayerVolume);
                Assert.AreEqual("60%", text.Text);
                Assert.AreEqual(50, values.uBMplayVolume);
                var composedGateway = (IPlayerSettingsGateway)(typeof(ApplicationComposition)
                    .GetField("playerSettingsGateway", BindingFlags.Instance | BindingFlags.NonPublic)?.GetValue(composition)
                    ?? throw new AssertFailedException("実構成のplayer readerがありません。"));
                Assert.AreEqual(50, composedGateway.CaptureSnapshot().PlayerVolume);
                dialog.UseBeatorajaScoreDb = true; // 不正な別候補は音量確定の対象ではありません。
                bool other = !dialog.ShowRecommUpdatedMsg;
                dialog.ShowRecommUpdatedMsg = other;
                values.Save();
                Settings before = TestSettingsFactory.OpenSettings(path);
                Assert.AreEqual(50, before.uBMplayVolume);
                Assert.AreEqual(other, before.ShowRecommUpdatedMsg);
                Assert.IsFalse(before.UseBeatorajaScoreDb);
                void CompleteInput()
                {
                    switch (completion)
                    {
                        case "drag": slider.RaiseEvent(new DragCompletedEventArgs(0, 0, false) { RoutedEvent = Thumb.DragCompletedEvent }); break;
                        case "mouse":
                            slider.RaiseEvent(new System.Windows.Input.MouseButtonEventArgs(System.Windows.Input.Mouse.PrimaryDevice, 0, System.Windows.Input.MouseButton.Left)
                            { RoutedEvent = System.Windows.Input.Mouse.PreviewMouseUpEvent }); break;
                        case "keyboard":
                            slider.RaiseEvent(new System.Windows.Input.KeyEventArgs(System.Windows.Input.Keyboard.PrimaryDevice, PresentationSource.FromVisual(window), 0, System.Windows.Input.Key.Right)
                            { RoutedEvent = System.Windows.Input.Keyboard.PreviewKeyUpEvent }); break;
                        case "close": window.Close(); TestUiDispatcherHost.AwaitPresentationOnDispatcher(closed.Task, "volume-close-final-value"); break;
                    }
                }
                if (failSave && completion == "drag")
                {
                    CompleteInput();
                    Assert.AreEqual(60, TestSettingsFactory.OpenSettings(path).uBMplayVolume);
                    Assert.AreEqual(60, composedGateway.CaptureSnapshot().PlayerVolume);
                    slider.SetCurrentValue(Slider.ValueProperty, 70d);
                    TestUiDispatcherHost.ProcessQueuedPresentation();
                    Assert.AreEqual(70, owner.PlaybackPanel.PlayerVolume);
                    Assert.AreEqual(70, runtime.Volumes.Last());
                    Assert.AreEqual(60, values.uBMplayVolume);
                    Assert.AreEqual(60, composedGateway.CaptureSnapshot().PlayerVolume);
                }
                using (FileStream? blocked = failSave ? new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite) : null) { CompleteInput(); }
                TestUiDispatcherHost.ProcessQueuedPresentation();
                int expected = failSave && completion != "drag" ? 50 : 60;
                Assert.AreEqual(expected, TestSettingsFactory.OpenSettings(path).uBMplayVolume);
                Assert.AreEqual(expected, values.uBMplayVolume);
                Assert.AreEqual(expected, gateway.CaptureSnapshot().PlayerVolume);
                Assert.AreEqual(expected, composedGateway.CaptureSnapshot().PlayerVolume);
                Assert.AreEqual(expected, owner.PlaybackPanel.PlayerVolume);
                Assert.AreEqual(expected, runtime.Volumes.Last());
                Assert.AreEqual(expected, slider.Value);
                Assert.AreEqual(expected + "%", text.Text);
                Assert.AreEqual(failSave ? 1 : 0, failures.Count);
                // 同じ終端が重なっても保存は再実行しません。実媒体を置換不能にして確認します。
                using (var blocked = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite))
                {
                    slider.RaiseEvent(new DragCompletedEventArgs(0, 0, false) { RoutedEvent = Thumb.DragCompletedEvent });
                    owner.PlaybackPanel.CompleteVolumeAdjustment();
                }
                Assert.AreEqual(failSave ? 1 : 0, failures.Count);
            }
            finally
            {
                if (window?.IsVisible == true) { window.CloseForOwnerShutdown(); }
                if (dialog != null) { TestUiDispatcherHost.AwaitTaskOnDispatcher(dialog.AudioSettingsCompletionTask, "volume-audio-owner-terminal"); dialog.Dispose(); }
                if (owner != null) { TestUiDispatcherHost.AwaitTaskOnDispatcher(owner.PlaybackPanel.CloseForShutdown(), "volume-player-owner-terminal"); }
                Directory.Delete(root, recursive: true);
            }
        });
    }

    /// <summary>音量通知の実consumerを残し、OS出力のみ固定する境界です。譜面開始はこの入力確定検証の対象外です。</summary>
    private sealed class VolumePreviewPlaybackRuntime : IAudioPlaybackRuntime
    {
        internal List<int> Volumes { get; } = [];
        public int CurrentVoices => 0;
        public int MaxVoices => 0;
        public AudioPlaybackInitializationResult Initialize(PlayerSettingsSnapshot settings) => throw new AssertFailedException("音量操作から再生を開始しません。");
        public Task WaitForOutputReadyAsync() => Task.CompletedTask;
        public void ClearMaxVoices() { }
        public void SetVolume(int volume) => Volumes.Add(volume);
        public void Free() { }
    }

    /// <summary>両モードで画面を繰り返し開閉しても保存要求を出さず、明示した選択と実RadioButtonのClickから確認一回・承認後の保存一回へ進むことを確認します。</summary>
    /// <remarks>標準の選択後と同じ状態をBindingを保って渡し、物理キー・マウス状態を参照するWPF入力処理は呼びません。</remarks>
    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public void OperationModeRadio_RepeatedWindowLifetimesDoNotRequestChangeUntilAcceptedClick(bool initialOperationMode)
    {
        TestUiDispatcherHost.RunWindowTest(windowTest =>
        {
            string root = CreateTemporaryRoot();
            var openedWindows = new List<SettingsWindow>();
            try
            {
                Settings settings = CreateValidStandaloneSettings(root);
                settings.OperationModeLR2DB = initialOperationMode;
                settings.LR2RootPath = string.Empty;
                if (initialOperationMode)
                { (settings.LR2SongDBPath, settings.LR2ConfigXmlPath) = CreateValidLr2Layout(Path.Combine(root, "lr2")); }
                else { settings.LR2SongDBPath = string.Empty; settings.LR2ConfigXmlPath = string.Empty; }
                var settingsSession = new CountingSettingsEditSession(settings);
                var dialogs = new RecordingRootDialogService
                {
                    ConfirmationResult = UiDialogResult.FromMessageBoxResult(MessageBoxResult.OK)
                };
                var lifetime = new CountingApplicationLifetime();
                using SettingsDialogViewModel dialog = CreateOperationModeDialog(settingsSession, dialogs, lifetime);
                var presentationPort = new WindowClosingPresentationPort();
                dialog.AttachPresentationPort(presentationPort);

                for (int presentation = 0; presentation < 3; presentation++)
                {
                    SettingsWindow window = OpenSettingsWindow(windowTest, dialog);
                    openedWindows.Add(window);
                    presentationPort.CurrentWindow = window;
                    AssertOperationModePresentation(window, initialOperationMode);
                    Assert.AreEqual(0, dialogs.ConfirmationCount);
                    Assert.AreEqual(0, settingsSession.SaveCount);
                    Assert.AreEqual(0, lifetime.RestartCount);

                    Button cancelButton = FindDescendants<Button>(window).Single(button => button.Name == "buttonCancel");
                    cancelButton.RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent, cancelButton));
                    Assert.IsNull(window.DataContext, "A closed settings Window must release the shared ViewModel binding graph.");
                }

                SettingsWindow finalWindow = OpenSettingsWindow(windowTest, dialog);
                openedWindows.Add(finalWindow);
                presentationPort.CurrentWindow = finalWindow;
                RadioButton requestedMode = FindOperationModeRadio(finalWindow, useLr2: !initialOperationMode);
                requestedMode.SetCurrentValue(ToggleButton.IsCheckedProperty, true);
                Assert.AreEqual(true, requestedMode.IsChecked);
                Assert.AreEqual(0, dialogs.ConfirmationCount, "プログラムによる選択表示だけでは確認しません。");
                Assert.AreEqual(0, settingsSession.SaveCount, "実Click前にはモードを保存しません。");
                requestedMode.RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent, requestedMode));
                finalWindow.Dispatcher.Invoke(DispatcherPriority.DataBind, new Action(() => { }));

                Assert.AreEqual(1, dialogs.ConfirmationCount);
                Assert.AreEqual(1, settingsSession.SaveCount);
                Assert.AreEqual(0, lifetime.RestartCount);
                Assert.AreEqual(!initialOperationMode, dialog.OperationModeLR2DB);
                Assert.AreEqual(!initialOperationMode, settings.OperationModeLR2DB);
                AssertOperationModePresentation(finalWindow, !initialOperationMode);
            }
            finally
            {
                foreach (SettingsWindow window in openedWindows.Where(window => window.IsLoaded))
                {
                    window.CloseForOwnerShutdown();
                }
                Directory.Delete(root, recursive: true);
            }
        });
    }

    /// <summary>両モードの実Radioで、確認取消と別実操作中の入口拒否を分け、保存・再起動なしで両選択表示を確定モードへ戻します。</summary>
    /// <remarks>確認前に反対の選択へ切り替え、取消が実際に選択表示を復元することをOS入力状態から分離して観測します。</remarks>
    [TestMethod]
    [DataRow(false, false)]
    [DataRow(false, true)]
    [DataRow(true, false)]
    [DataRow(true, true)]
    public void OperationModeRadio_RejectedClickRestoresSelectionWithoutSaveOrRestart(bool initialOperationMode, bool operationBusy)
    {
        TestUiDispatcherHost.RunWindowTest(windowTest =>
        {
            string root = CreateTemporaryRoot();
            SettingsWindow? window = null;
            try
            {
                Settings settings = CreateValidStandaloneSettings(root);
                settings.OperationModeLR2DB = initialOperationMode;
                settings.LR2RootPath = string.Empty;
                if (initialOperationMode)
                { (settings.LR2SongDBPath, settings.LR2ConfigXmlPath) = CreateValidLr2Layout(Path.Combine(root, "lr2")); }
                else { settings.LR2SongDBPath = string.Empty; settings.LR2ConfigXmlPath = string.Empty; }
                var settingsSession = new CountingSettingsEditSession(settings);
                var dialogs = new RecordingRootDialogService
                {
                    ConfirmationResult = UiDialogResult.FromMessageBoxResult(MessageBoxResult.Cancel)
                };
                var lifetime = new CountingApplicationLifetime();
                using SettingsDialogViewModel dialog = CreateOperationModeDialog(settingsSession, dialogs, lifetime);
                window = OpenSettingsWindow(windowTest, dialog);

                var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
                Task? operation = operationBusy ? dialog.RunSettingsOperationAsync(() => release.Task) : null;
                try
                {
                    RadioButton requestedMode = FindOperationModeRadio(window, useLr2: !initialOperationMode);
                    Assert.AreEqual(!operationBusy, requestedMode.IsEnabled);
                    requestedMode.SetCurrentValue(ToggleButton.IsCheckedProperty, true);
                    Assert.AreEqual(true, requestedMode.IsChecked);
                    requestedMode.RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent, requestedMode));
                    window.Dispatcher.Invoke(DispatcherPriority.DataBind, new Action(() => { }));

                    Assert.AreEqual(operationBusy ? 0 : 1, dialogs.ConfirmationCount);
                    Assert.AreEqual(0, settingsSession.SaveCount);
                    Assert.AreEqual(0, lifetime.RestartCount);
                    Assert.AreEqual(initialOperationMode, dialog.OperationModeLR2DB);
                    Assert.AreEqual(initialOperationMode, settings.OperationModeLR2DB);
                    AssertOperationModePresentation(window, initialOperationMode);
                }
                finally
                {
                    release.TrySetResult();
                    if (operation != null) { TestUiDispatcherHost.AwaitTaskOnDispatcher(operation, "mode-rejected-operation-terminal"); }
                }
            }
            finally
            {
                if (window?.IsLoaded == true)
                {
                    window.CloseForOwnerShutdown();
                }
                Directory.Delete(root, recursive: true);
            }
        });
    }

    /// <summary>明示した起動走査・初期 playlist・offline・IR の変更が、それぞれに対応する翻訳済み確認要求へ届くことを確認し、異なる設定の警告を配送する誤りを検出します。</summary>
    [TestMethod]
    public void AdvancedStartupOptions_AcceptedChangesPublishLocalizedConfirmationRequestsInOrder()
    {
        string root = CreateTemporaryRoot();
        try
        {
            Settings settings = CreateValidStandaloneSettings(root);
            settings.ScanBmsFilesOnStartup = true;
            settings.SkipInitPlaylistLoad = false;
            settings.EstimateOfflineScoreRanking = false;
            settings.UpdateLr2IrRankingCacheOnStartup = false;
            var settingsSession = new CountingSettingsEditSession(settings);
            var dialogs = new RecordingRootDialogService
            {
                ConfirmationResult = UiDialogResult.FromMessageBoxResult(MessageBoxResult.OK)
            };
            using var harness = SettingsDialogHarness.Create(settings, activeLibraryProfile: true,
                settingsEditSession: settingsSession, dialogs: dialogs);
            SettingsDialogViewModel dialog = harness.Dialog;

            dialog.ScanBmsFilesOnStartup = false;
            dialog.SkipInitPlaylistLoad = true;
            dialog.EstimateOfflineScoreRanking = true;
            dialog.UpdateLr2IrRankingCacheOnStartup = true;

            CollectionAssert.AreEqual(
                new[]
                {
                    Resources.Msg_confirm_disable_startup_file_scan,
                    Resources.Msg_confirm_skip_init_playlist_load,
                    Resources.Msg_confirm_enable_offline_score_ranking_estimation,
                    Resources.Msg_confirm_enable_lr2ir_ranking_cache_startup_update
                },
                dialogs.ConfirmationRequests
                    .Select(request => request.MessageBoxText)
                    .ToArray());
            foreach (UiConfirmationRequest request in dialogs.ConfirmationRequests)
            {
                Assert.AreEqual(Resources.Warning, request.Caption);
                Assert.AreEqual(MessageBoxButton.OKCancel, request.Button);
                Assert.AreEqual(MessageBoxImage.Exclamation, request.Icon);
                Assert.AreEqual(MessageBoxResult.None, request.DefaultResult);
                Assert.AreEqual(MessageBoxOptions.None, request.Options);
                Assert.IsNull(request.Owner);
                Assert.IsNull(request.WarningMessageBoxText);
            }

            Assert.IsFalse(settings.ScanBmsFilesOnStartup);
            Assert.IsTrue(settings.SkipInitPlaylistLoad);
            Assert.IsTrue(settings.EstimateOfflineScoreRanking);
            Assert.IsTrue(settings.UpdateLr2IrRankingCacheOnStartup);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    /// <summary>準備・設定保存・URL切替・旧先整理の実媒体故障を区別し、採用点、管理外保全、最新入力への次変更を保証します。同先の形式変更/再更新は全表と正本hashを確認します。</summary>
    [TestMethod]
    [DataRow("success")]
    [DataRow("prepare-file")]
    [DataRow("prepare-manifest")]
    [DataRow("save")]
    [DataRow("root-prepare-file")] // score使用ONの新rootでも、準備失敗は採用・score読込みより前に終えます。
    [DataRow("root-save")] // 新先の準備後に設定保存だけを失敗させ、旧root採用を保ちます。
    [DataRow("same-root-url")]
    [DataRow("new-root-url")]
    [DataRow("old-root-url")]
    [DataRow("cleanup")]
    [DataRow("shared-output")]
    [DataRow("same-output")]
    [DataRow("register-off")]
    [DataRow("disable-keep")]
    [DataRow("disable-remove")]
    [DataRow("disable-missing-runtime")] // 旧rootのjarだけが失われても、必要URL解除の無作用を成功にしません。
    [DataRow("old-root-missing-runtime")] // 別rootへの採用後も、旧URL解除未完了なら旧ファイルを保持します。
    [DataRow("keep-missing-runtime")] // 保持ONは構成対象外でもURL/ファイルを変更しません。
    [DataRow("no-owned-urls-missing-runtime")] // URL所有がないfiles-only台帳はURL解除を必要条件にしません。
    [DataRow("empty-old-root")] // 未指定の旧rootへURL同期を要求せず、有効な新rootへ移行できます。
    public async Task BmtSettingsAdoption_PreservesAdoptedMediaAndAllowsCurrentNextInput(string scenario)
    {
        string root = CreateTemporaryRoot();
        MainWindowViewModel? owner = null;
        BMSPlaylist? playlist = null;
        Exception? primaryFailure = null;
        var cleanupFailures = new List<Exception>();
        void Cleanup(Action action) { try { action(); } catch (Exception failure) { cleanupFailures.Add(failure); } }
        try
        {
            string oldRoot = Directory.CreateDirectory(Path.Combine(root, "beatoraja-A")).FullName;
            string nextRoot = scenario is "new-root-url" or "old-root-url" or "shared-output" or "root-prepare-file" or "root-save" or "old-root-missing-runtime" or "empty-old-root"
                ? Directory.CreateDirectory(Path.Combine(root, "beatoraja-B")).FullName : oldRoot;
            string oldOutput = Directory.CreateDirectory(Path.Combine(oldRoot, "table")).FullName;
            bool keepDisabled = scenario is "disable-keep" or "keep-missing-runtime";
            bool removeDisabled = scenario is "disable-remove" or "disable-missing-runtime" or "no-owned-urls-missing-runtime";
            bool sameOutput = scenario is "same-output" or "shared-output" or "register-off" || keepDisabled || removeDisabled;
            string output = sameOutput ? oldOutput : Directory.CreateDirectory(Path.Combine(nextRoot, "replacement")).FullName;
            const string unmanagedUrl = "https://example.invalid/user-first";
            const string otherUrl = "https://example.invalid/user-second";
            const string obsoleteUrl = "https://example.invalid/obsolete";
            foreach (string path in new[] { oldRoot, nextRoot }.Distinct())
            {
                File.WriteAllText(Path.Combine(path, "beatoraja.jar"), string.Empty);
                File.WriteAllText(Path.Combine(path, BeatorajaConfigService.ConfigFileName), new Newtonsoft.Json.Linq.JObject
                { ["tablepath"] = oldOutput, ["tableURL"] = new Newtonsoft.Json.Linq.JArray(unmanagedUrl, otherUrl) }.ToString());
            }
            string config = Path.Combine(nextRoot, BeatorajaConfigService.ConfigFileName);
            string oldConfig = Path.Combine(oldRoot, BeatorajaConfigService.ConfigFileName);
            string settingsPath = Path.Combine(root, "settings", "user.config");
            Settings values = CreateValidStandaloneSettings(root, TestSettingsFactory.OpenSettings(settingsPath));
            values.BeatorajaRootPath = oldRoot;
            values.BeatorajaBmtTablePath = oldOutput;
            values.EnableBeatorajaBmtOutput = true;
            values.RegisterBeatorajaBmtUrls = true;
            values.BeatorajaBmtHashOutputMode = "Original";
            bool originalKeep = values.KeepBeatorajaBmtFilesWhenOutputDisabled;
            if (scenario is "root-prepare-file" or "root-save")
            {
                foreach (string path in new[] { oldRoot, nextRoot })
                {
                    string playerPath = Directory.CreateDirectory(Path.Combine(path, "player", "player")).FullName;
                    File.WriteAllBytes(Path.Combine(playerPath, "score.db"), []);
                }
                values.UseBeatorajaScoreDb = true;
                values.BeatorajaPlayerId = "player";
                values.BeatorajaScoreDbPath = Path.Combine(oldRoot, "player", "player", "score.db");
            }
            values.Save();
            List<Exception> failures = [];
            TestUiDispatcherHost.Invoke(() => owner = CreateViewModel(new CountingSettingsEditSession(values) { PersistOwnedValues = true },
                firstStartup: false, reportSettingsApplyFailure: failures.Add, playbackRuntimePort: new TestSettingsDialogPlaybackRuntimePort(),
                dialogs: new RecordingRootDialogService { ConfirmationResult = UiDialogResult.FromMessageBoxResult(MessageBoxResult.OK) }));
            Assert.IsNotNull(owner);
            SetActiveLibraryProfile(owner, true);
            string db = BmsPlaylistTestSupport.CreateTempSongDbPath(MainWindowViewModelTestFactory.GetComposition(owner ?? throw new AssertFailedException()).DatabaseScope, root);
            PlaylistPersistenceRepository.EnsureSchema(MainWindowViewModelTestFactory.GetComposition(owner ?? throw new AssertFailedException()).DatabaseScope, db);
            playlist = AttachPlaylistTables(owner, root, existingDatabasePath: db);
            BMSTable[] tables = Enumerable.Range(1, 2).Select(id => new BMSTable
            {
                playlist_id = id,
                name = "Both " + id,
                bmt_sort = id,
                last_update = new DateTime(2024, 1, 1),
                Folder_order = ["Folder"],
                entries = [new BMSTableEntry { title = "Song " + id, md5 = new string((char)('a' + id), 32), sha256 = new string((char)('a' + id), 64), folder = "Folder" }]
            }).ToArray();
            foreach (BMSTable table in tables) { playlist.BMSTables.Add(table); }
            playlist.CommitBMSTablesWithEntriesToDB(tables);
            await playlist.BmtOutput.ExportAllAsync("initial-original");
            string oldManifestPath = Path.Combine(oldOutput, BmtTableExportService.ManifestFileName);
            var oldManifest = Newtonsoft.Json.Linq.JObject.Parse(File.ReadAllText(oldManifestPath));
            string obsoleteFile = Path.Combine(oldOutput, "obsolete.bmt");
            File.WriteAllText(obsoleteFile, "old managed");
            ((Newtonsoft.Json.Linq.JArray)oldManifest["files"]!).Add("obsolete.bmt");
            oldManifest["playlists"]!["obsolete"] = new Newtonsoft.Json.Linq.JObject { ["file"] = "obsolete.bmt", ["url"] = obsoleteUrl };
            File.WriteAllText(oldManifestPath, oldManifest.ToString());
            BeatorajaConfigService.SyncTableUrls(oldRoot, [.. BeatorajaConfigService.ReadTableUrls(oldRoot), obsoleteUrl], []);
            string unmanagedFile = Path.Combine(oldOutput, "user.bmt");
            File.WriteAllBytes(unmanagedFile, [1, 255, 42]);
            if (scenario is "no-owned-urls-missing-runtime" or "empty-old-root")
            {
                BeatorajaConfigService.SyncTableUrls(oldRoot, [], BmtTableExportService.ReadManagedTableUrls(oldOutput).Select(entry => entry.Url));
                if (scenario == "no-owned-urls-missing-runtime")
                {
                    oldManifest["playlists"] = new Newtonsoft.Json.Linq.JObject();
                    File.WriteAllText(oldManifestPath, oldManifest.ToString());
                }
                else { values.BeatorajaRootPath = string.Empty; values.Save(); }
            }
            if (scenario.EndsWith("missing-runtime", StringComparison.Ordinal)) { File.Delete(Path.Combine(oldRoot, "beatoraja.jar")); }
            Dictionary<string, byte[]> beforeFiles = Directory.GetFiles(oldOutput).ToDictionary(path => path, File.ReadAllBytes);
            string[] beforeUrls = BeatorajaConfigService.ReadTableUrls(oldRoot).ToArray();
            var configDocument = Newtonsoft.Json.Linq.JObject.Parse(File.ReadAllText(config));
            configDocument["tablepath"] = scenario == "same-output" ? output.ToUpperInvariant() + Path.DirectorySeparatorChar : output;
            File.WriteAllText(config, configDocument.ToString());
            byte[] configBefore = File.ReadAllBytes(config);
            string targetManifest = Path.Combine(output, BmtTableExportService.ManifestFileName);
            string? blockedPath = scenario switch
            {
                "prepare-file" or "root-prepare-file" => Path.Combine(output, BmtTableExportService.CreatePlaylistExportMetadata(tables[1]).FileName),
                "prepare-manifest" => targetManifest,
                "save" or "root-save" => settingsPath,
                "same-root-url" or "new-root-url" => config,
                "old-root-url" => oldConfig,
                "cleanup" or "disable-remove" => obsoleteFile,
                _ => null
            };
            if (scenario is "prepare-file" or "root-prepare-file" && blockedPath is string blockedFile) { File.Copy(Path.Combine(oldOutput, BmtTableExportService.CreatePlaylistExportMetadata(tables[1]).FileName), blockedFile); }
            if (scenario == "prepare-manifest") { File.WriteAllText(targetManifest, "{\"files\":[],\"playlists\":{}}"); }
            SettingsDialogViewModel dialog = owner.SettingDialog;
            dialog.UseExternalPanelImage = true;
            dialog.BeatorajaPlayerId = "invalid-score-draft";
            bool rootChange = nextRoot != oldRoot;
            if (!rootChange)
            {
                dialog.BeatorajaBmtHashOutputMode = "PreferSha256Only";
                dialog.RegisterBeatorajaBmtUrls = scenario != "register-off";
                dialog.KeepBeatorajaBmtFilesWhenOutputDisabled = keepDisabled;
                dialog.EnableBeatorajaBmtOutput = !keepDisabled && !removeDisabled;
            }
            bool? preparedAtAdoption = null, oldFilesAtAdoption = null, oldUrlsAtAdoption = null;
            void ObserveAdoption(object? sender, PropertyChangedEventArgs args)
            {
                if (args.PropertyName != (rootChange ? nameof(Settings.BeatorajaRootPath) : nameof(Settings.BeatorajaBmtTablePath))) { return; }
                preparedAtAdoption = File.Exists(targetManifest) && tables.All(table => File.Exists(Path.Combine(output, BmtTableExportService.CreatePlaylistExportMetadata(table).FileName)));
                oldFilesAtAdoption = File.Exists(obsoleteFile);
                oldUrlsAtAdoption = BeatorajaConfigService.ReadTableUrls(oldRoot).Contains(obsoleteUrl);
            }
            values.PropertyChanged += ObserveAdoption;
            try
            {
                using FileStream? blocked = blockedPath == null ? null : new FileStream(blockedPath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
                if (rootChange) { await dialog.CommitBeatorajaRootSettingsAsync(nextRoot); }
                else { await dialog.CommitBmtSettingsAsync(); }
                bool adopted = scenario is not ("prepare-file" or "prepare-manifest" or "save" or "root-prepare-file" or "root-save");
                Settings reader = TestSettingsFactory.OpenSettings(settingsPath);
                Assert.AreEqual(Path.TrimEndingDirectorySeparator(adopted ? output : oldOutput), Path.TrimEndingDirectorySeparator(reader.BeatorajaBmtTablePath), ignoreCase: true);
                Assert.AreEqual(adopted ? nextRoot : oldRoot, reader.BeatorajaRootPath);
                Assert.AreEqual(!adopted || !keepDisabled && !removeDisabled, reader.EnableBeatorajaBmtOutput);
                Assert.AreEqual(adopted && !rootChange ? keepDisabled : originalKeep, reader.KeepBeatorajaBmtFilesWhenOutputDisabled);
                Assert.AreEqual(!(adopted && scenario == "register-off"), reader.RegisterBeatorajaBmtUrls);
                Assert.AreEqual(adopted && !rootChange ? "PreferSha256Only" : "Original", reader.BeatorajaBmtHashOutputMode);
                Assert.IsTrue(dialog.UseExternalPanelImage);
                Assert.IsFalse(reader.UseExternalPanelImage);
                if (!rootChange) { Assert.AreEqual("invalid-score-draft", dialog.BeatorajaPlayerId); }
                if (adopted && !sameOutput)
                {
                    Assert.AreEqual(true, preparedAtAdoption, "採用公開時には必要ファイルと管理情報が既にあります。");
                    Assert.AreEqual(true, oldFilesAtAdoption);
                    Assert.AreEqual(beforeUrls.Contains(obsoleteUrl), oldUrlsAtAdoption);
                }
                bool urlFailure = scenario is "same-root-url" or "new-root-url" or "old-root-url" or "disable-missing-runtime" or "old-root-missing-runtime";
                bool hasFailure = !adopted || urlFailure || scenario is "cleanup" or "disable-remove";
                Assert.AreEqual(hasFailure ? 1 : 0, failures.Count, "old URL remains=" + BeatorajaConfigService.ReadTableUrls(oldRoot).Contains(obsoleteUrl)
                    + "; old file remains=" + File.Exists(obsoleteFile) + "; adopted enabled=" + reader.EnableBeatorajaBmtOutput
                    + Environment.NewLine + string.Join(Environment.NewLine, failures));
                if (!adopted || urlFailure)
                {
                    Assert.IsTrue(File.Exists(obsoleteFile));
                    foreach ((string path, byte[] bytes) in beforeFiles) { CollectionAssert.AreEqual(bytes, File.ReadAllBytes(path)); }
                    if (scenario is "prepare-file" or "prepare-manifest" or "root-prepare-file")
                    {
                        Assert.IsTrue(File.Exists(Path.Combine(output, BmtTableExportService.CreatePlaylistExportMetadata(tables[0]).FileName)), "新先の一部実生成後に失敗させます。");
                        if (scenario == "prepare-manifest") { Assert.AreEqual(0, BmtTableExportService.ReadManagedTableUrls(output).Count, "置換失敗で旧台帳を維持します。"); }
                    }
                    CollectionAssert.AreEqual(beforeUrls, BeatorajaConfigService.ReadTableUrls(oldRoot).ToArray());
                    if (scenario is "save" or "root-save") { Assert.IsTrue(File.Exists(targetManifest)); Assert.AreEqual(2, Directory.GetFiles(output, "*.bmt").Length); }
                    if (urlFailure) { StringAssert.Contains(failures[0].Message, Resources.Settings_bmt_url_switch_failed); }
                    if (scenario is "same-root-url" or "new-root-url") { CollectionAssert.AreEqual(configBefore, File.ReadAllBytes(config)); }
                    if (scenario is "old-root-url" or "old-root-missing-runtime") { CollectionAssert.Contains(BeatorajaConfigService.ReadTableUrls(nextRoot).ToArray(), "bemusicseeker://playlist/1"); }
                }
                else if (keepDisabled)
                {
                    foreach (KeyValuePair<string, byte[]> file in beforeFiles) { CollectionAssert.AreEqual(file.Value, File.ReadAllBytes(file.Key)); }
                    CollectionAssert.AreEqual(beforeUrls, BeatorajaConfigService.ReadTableUrls(oldRoot).ToArray());
                    Assert.IsFalse(reader.EnableBeatorajaBmtOutput);
                }
                else
                {
                    string[] expected = scenario == "register-off" || removeDisabled ? [unmanagedUrl, otherUrl]
                        : [unmanagedUrl, otherUrl, "bemusicseeker://playlist/1", "bemusicseeker://playlist/2"];
                    CollectionAssert.AreEqual(expected, BeatorajaConfigService.ReadTableUrls(nextRoot).ToArray());
                    if (rootChange) { CollectionAssert.AreEqual(new[] { unmanagedUrl, otherUrl }, BeatorajaConfigService.ReadTableUrls(oldRoot).ToArray()); }
                    if (scenario is "cleanup" or "disable-remove")
                    { Assert.IsTrue(File.Exists(obsoleteFile)); StringAssert.Contains(failures[0].Message, Resources.Settings_bmt_files_remain); }
                    else { Assert.IsFalse(File.Exists(obsoleteFile)); }
                    if (!removeDisabled) { AssertProjection(output, rootChange ? "Original" : "PreferSha256Only"); }
                }
                CollectionAssert.AreEqual(new byte[] { 1, 255, 42 }, File.ReadAllBytes(unmanagedFile));
                if (scenario == "cleanup")
                {
                    string finalOutput = Path.Combine(nextRoot, "C");
                    var currentConfig = Newtonsoft.Json.Linq.JObject.Parse(File.ReadAllText(config));
                    currentConfig["tablepath"] = finalOutput;
                    File.WriteAllText(config, currentConfig.ToString());
                    await dialog.CommitBmtSettingsAsync();
                    Assert.AreEqual(finalOutput, TestSettingsFactory.OpenSettings(settingsPath).BeatorajaBmtTablePath);
                    AssertProjection(finalOutput, "PreferSha256Only");
                    Assert.AreEqual(1, failures.Count, "過去のA残存は次のC確定を拒否しません。");
                }
            }
            finally { values.PropertyChanged -= ObserveAdoption; }
            if (scenario is "prepare-file" or "same-root-url")
            {
                string finalOutput = Path.Combine(nextRoot, "latest-C");
                var latestConfig = Newtonsoft.Json.Linq.JObject.Parse(File.ReadAllText(config));
                latestConfig["tablepath"] = finalOutput;
                File.WriteAllText(config, latestConfig.ToString());
                tables[0].entries.Single().title = "Latest saved song";
                playlist.CommitBMSTablesWithEntriesToDB(tables);
                dialog.BeatorajaBmtHashOutputMode = "PreferSha256Only";
                await dialog.CommitBmtSettingsAsync();
                Assert.AreEqual(finalOutput, TestSettingsFactory.OpenSettings(settingsPath).BeatorajaBmtTablePath);
                AssertProjection(finalOutput, "PreferSha256Only");
                Assert.AreEqual(1, failures.Count, "前回の準備/URL失敗で次の最新Cを固定・拒否しません。");
                if (scenario == "same-root-url") { Assert.IsTrue(File.Exists(obsoleteFile), "今回と無関係になった過去Aの回収を推測しません。"); }
            }
            if (scenario == "same-output")
            {
                File.WriteAllText(Path.Combine(output, BmtTableExportService.CreatePlaylistExportMetadata(tables[0]).FileName), "external corruption");
                await dialog.CommitBmtSettingsAsync();
                AssertProjection(output, "PreferSha256Only");
            }
            var repository = new PlaylistPersistenceRepository(MainWindowViewModelTestFactory.GetComposition(owner).DatabaseScope, db);
            foreach (BMSTable table in tables)
            {
                BMSTableEntry stored = repository.LoadPersistedPlaylistEntries(table.playlist_id, activeOnly: true).Single();
                Assert.AreEqual(table.entries.Single().md5, stored.md5);
                Assert.AreEqual(table.entries.Single().sha256, stored.sha256);
            }
            void AssertProjection(string path, string mode)
            {
                List<BmtTableExportService.ManagedTableUrlEntry> managed = BmtTableExportService.ReadManagedTableUrls(path);
                Assert.AreEqual(2, managed.Count);
                foreach (BMSTable table in tables)
                {
                    using var stream = new System.IO.Compression.GZipStream(File.OpenRead(Path.Combine(path, BmtTableExportService.CreatePlaylistExportMetadata(table).FileName)), System.IO.Compression.CompressionMode.Decompress);
                    using var reader = new StreamReader(stream);
                    JToken? song = Newtonsoft.Json.Linq.JObject.Parse(reader.ReadToEnd()).SelectToken("folder[0].songs[0]");
                    Assert.IsNotNull(song);
                    Assert.AreEqual(table.entries.Single().title, song.Value<string>("title"));
                    Assert.AreEqual(mode == "Original" ? table.entries.Single().md5 : null, song.Value<string>("md5"));
                    Assert.AreEqual(table.entries.Single().sha256, song.Value<string>("sha256"));
                }
            }
        }
        catch (Exception failure) { primaryFailure = failure; throw; }
        finally
        {
            Cleanup(() => playlist?.RequestShutdown("settings-bmt-media-fixture"));
            if (owner != null)
            {
                Cleanup(() => TestUiDispatcherHost.Invoke(() =>
                {
                    Cleanup(() => TestUiDispatcherHost.AwaitTaskOnDispatcher(owner.ShellShutdownWorkflow.RequestWindowCloseAsync(), "settings-bmt-media-owner-terminal"));
                    Cleanup(owner.SettingDialog.Dispose);
                }));
            }
            Cleanup(() => Directory.Delete(root, recursive: true));
            if (cleanupFailures.Count > 0)
            {
                if (primaryFailure != null) { primaryFailure.Data["CleanupFailures"] = new AggregateException(cleanupFailures); }
                else { throw new AggregateException(cleanupFailures); }
            }
        }
    }

    /// <summary>起動操作の block に再同期可否が追従し、無関係な title 変更では再通知せず、Dispose 後は可否購読を解除することを確認します。</summary>
    [TestMethod]
    public async Task SettingDialogViewModel_OwnsLr2ManualResyncAvailability()
    {
        string root = CreateTemporaryRoot();
        try
        {
            Settings settings = CreateValidStandaloneSettings(root);
            settings.OperationModeLR2DB = true;
            var settingsSession = new CountingSettingsEditSession(settings);
            MainWindowViewModel viewModel = CreateViewModel(settingsSession, firstStartup: false);
            SetActiveLibraryProfile(viewModel, true);
            SettingsDialogViewModel dialog = viewModel.SettingDialog;
            var changedProperties = new List<string>();
            dialog.PropertyChanged += (_, args) => changedProperties.Add(args.PropertyName!);

            Assert.IsTrue(dialog.CanRequestLr2SongDbSyncDataResync);

            viewModel.ProgressHub.StartupProgress.SetStartupUiInteractionBlocked(true);
            Assert.IsFalse(dialog.CanRequestLr2SongDbSyncDataResync);
            viewModel.ProgressHub.StartupProgress.SetStartupUiInteractionBlocked(false);
            Assert.IsTrue(dialog.CanRequestLr2SongDbSyncDataResync);

            changedProperties.Clear();
            viewModel.WindowTitle += " test";
            CollectionAssert.DoesNotContain(
                changedProperties,
                nameof(dialog.CanRequestLr2SongDbSyncDataResync));
            CollectionAssert.DoesNotContain(
                changedProperties,
                nameof(dialog.IsLr2SongDbSyncDataResyncBlockedByLibraryOperation));

            await dialog.RequestLr2SongDbSyncAsync();

            changedProperties.Clear();
            dialog.Dispose();
            viewModel.ProgressHub.StartupProgress.SetStartupUiInteractionBlocked(true);
            CollectionAssert.DoesNotContain(
                changedProperties,
                nameof(dialog.CanRequestLr2SongDbSyncDataResync));
            CollectionAssert.DoesNotContain(
                changedProperties,
                nameof(dialog.IsLr2SongDbSyncDataResyncBlockedByLibraryOperation));
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    /// <summary>BMS一覧の局所確定は同じL/Pで実差分を完了し、実score取得後も登録公開のcache失効・通知を維持します。成功時はDBと画面へ今回の譜面を反映し、走査失敗でも確定rootの保留解除通知を親L/P・モデル保護の解放後に完了します。</summary>
    /// <remarks>初期化後の独立保守と区別し、当該確定Taskの実終端でDBと必須UIを観測します。</remarks>
    [TestMethod]
    [DataRow(false, false)]
    [DataRow(true, false)]
    [DataRow(true, true)]
    public void BmsConfirmation_CompletesRealFileDiffBeforeClosing(bool lr2, bool failScan)
    {
        string root = CreateTemporaryRoot();
        string initialRoot = Path.Combine(root, "Songs");
        Directory.CreateDirectory(initialRoot);
        string addedRoot = Path.Combine(root, "added");
        Directory.CreateDirectory(addedRoot);
        string chartA = Path.Combine(initialRoot, "a.bms");
        string chartB = Path.Combine(addedRoot, "b.bms");
        File.WriteAllText(chartA, "#PLAYER 1\n#TITLE Folder A\n#BPM 120\n#00111:01\n");
        using var release = new ManualResetEventSlim();
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        try
        {
            TestUiDispatcherHost.Invoke(() =>
            {
                Settings values = lr2 ? CreateValidCustomFolderSettings(root, []) : CreateValidStandaloneSettings(initialRoot);
                values.ScanBmsFilesOnStartup = true;
                values.StartupSelectInstallPending = false;
                var session = new CountingSettingsEditSession(values);
                var failures = new List<Exception>();
                var scanFailure = new IOException("controlled root-confirmation scan failure");
                var scanWarningReceived = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
                var scanWarnings = new List<string>();
                var dialogs = new RecordingRootDialogService();
                dialogs.MessageObserved = () =>
                {
                    scanWarnings.Add(dialogs.LastMessageText);
                    if (dialogs.LastMessageText.Contains(scanFailure.Message, StringComparison.Ordinal)) { scanWarningReceived.TrySetResult(); }
                };
                var scanner = new SettingsChartFileScanner(CapturedChartFileScanner.FromFixture([chartA],
                    new Dictionary<string, IEnumerable<string>> { [initialRoot] = [] }, [initialRoot]));
                var ui = new TestUiScheduler(() => TestUiDispatcherHost.Dispatcher);
                int defaultPlayerCount = 0;
                var composition = new ApplicationComposition(settingsEditSession: session, reportSettingsApplyFailure: failures.Add,
                    uiScheduler: ui, applicationLifetime: TestApplicationContext.CreateLifetime(firstStartup: false),
                    cultureCatalog: TestApplicationContext.CreateCultureCatalog(), runtimeOptions: MainWindowViewModelTestFactory.CreateRuntimeOptions(ApplicationPathSnapshot.FromExecutablePath(Path.Combine(root, "BeMusicSeeker.exe"))),
                    chartFileScanner: scanner, rootFileEnumerator: new FastRootFileEnumerator(), fileDbMutationDialogService: dialogs,
                    defaultBmsPlayerFactory: () => { defaultPlayerCount++; return new RecordingPlaybackPlayer(); });
                var factory = new StartupLibraryConstructionTestSupport.RecordingDelegatingStartupLibraryFactory(composition, []);
                var owner = new MainWindowViewModel(composition, factory);
                Task? settingsLibraryTerminal = null;
                Task? settingsPlaylistTerminal = null;
                var presentation = new RecordingSettingsDialogPresentationPort
                {
                    WaitForClose = () =>
                    {
                        settingsLibraryTerminal = composition.OperationAdmission.WaitForIdleAsync();
                        settingsPlaylistTerminal = composition.PlaylistOperationAdmission.WaitForIdleAsync();
                        Assert.IsFalse(settingsLibraryTerminal.IsCompleted);
                        Assert.IsFalse(settingsPlaylistTerminal.IsCompleted);
                        return Task.CompletedTask;
                    }
                };
                owner.SettingDialog.AttachPresentationPort(presentation);
                Task? apply = null;
                Action? detachPendingNotifications = null;
                try
                {
                    Task<bool> initialize = owner.InitializeAsync();
                    TestUiDispatcherHost.AwaitTaskOnDispatcher(initialize, "folder-settings-initialize");
                    Assert.IsTrue(initialize.GetAwaiter().GetResult());
                    // 次の明示確定が起動後保守のL受付と競合しないよう、両動作modeで受理済み保守の実終端を待ちます。
                    AwaitStartupBackgroundCompletionOnDispatcher(owner, "bms-confirm-initial-background");
                    Assert.AreEqual(1, defaultPlayerCount, "単独動作の実構築は既定playerを一回接続します。");
                    BMSLibrary library = factory.CreatedLibrary ?? throw new InvalidOperationException("Actual library is unavailable.");
                    long scoreGeneration = library.GetScoreRuntimeStateForDiagnostics().SourceGeneration;
                    TestUiDispatcherHost.AwaitTaskOnDispatcher(owner.ReloadScoresOnlyAsync(), "bms-confirm-score-reload");
                    Assert.IsTrue(library.GetScoreRuntimeStateForDiagnostics().SourceGeneration > scoreGeneration,
                        "実score取得と採用を通した後の登録通知を検証します。");
                    int parentCacheVersion = library.BMSParentFolderListCacheVersion;
                    int parentCacheNotices = 0;
                    library.PropertyChanged += (_, args) =>
                    {
                        if (args.PropertyName == nameof(BMSLibrary.BMSParentFolderListCacheVersion)) { Interlocked.Increment(ref parentCacheNotices); }
                    };
                    // 同じ標準構成のstoreを直接確定し、後の譜面差分が起こす別のcache失効で購読切れを隠さないようにします。
                    composition.BmsRootRegistrations.SaveUserRoots(composition.BmsRootRegistrations.CaptureUserRoots());
                    TestUiDispatcherHost.ProcessQueuedPresentation();
                    Assert.IsTrue(library.BMSParentFolderListCacheVersion > parentCacheVersion,
                        "実score取得後も、譜面差分を伴わない登録公開がモデルのcacheを失効させます。");
                    Assert.IsTrue(Volatile.Read(ref parentCacheNotices) > 0, "同じ登録公開を既存UI schedulerのcache通知へ接続します。");
                    File.WriteAllText(chartB, "#PLAYER 1\n#TITLE Folder B\n#BPM 120\n#00111:01\n");
                    string unrelatedPath = Path.Combine(Directory.CreateDirectory(Path.Combine(root, "UnrelatedPending")).FullName, "pending.bms");
                    File.WriteAllText(unrelatedPath, "#TITLE Unrelated\n#BPM 120\n");
                    var becameOwned = ChartPackage.FromChartEntries([PackageChartEntry.FromChart(BmsChartFileParser.ParseSnapshot(ChartFileContentReader.ReadSnapshot(chartB)))]);
                    becameOwned.path = addedRoot;
                    var unrelated = ChartPackage.FromChartEntries([PackageChartEntry.FromChart(BmsChartFileParser.ParseSnapshot(ChartFileContentReader.ReadSnapshot(unrelatedPath)))]);
                    unrelated.path = Path.GetDirectoryName(unrelatedPath) ?? throw new InvalidOperationException();
                    library.ChartPackagesPending = new System.Collections.ObjectModel.ObservableCollection<ChartPackage>([becameOwned, unrelated]);
                    string pendingDatabase = lr2 ? values.LR2SongDBPath : composition.ApplicationPathSnapshot.StandaloneSongDbPath;
                    BmsLibraryInitializationTestSupport.ExecuteSongDbFixtureTransaction(composition.DatabaseScope, pendingDatabase, db =>
                    { db.InsertOrReplace(becameOwned, typeof(LR2SongDBExtended.install)); db.InsertOrReplace(unrelated, typeof(LR2SongDBExtended.install)); });
                    byte[] chartBefore = File.ReadAllBytes(chartB);
                    byte[] unrelatedBefore = File.ReadAllBytes(unrelatedPath);
                    // getterの直読だけでは通知欠落を検出できないため、実presentationと必須PropertyChangedの受信内容を捕捉します。
                    TestUiDispatcherHost.ProcessQueuedPresentation();
                    var presentationReceived = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
                    var propertyReceived = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
                    var notificationFailures = new List<Exception>();
                    var notifications = new List<(string Kind, bool Dispatcher, bool AdmissionsReleased, bool GuardsReleased,
                        string[] Current, string[] Persisted, string[] Presented, string[] ReadPaths)>();
                    ReaderWriterLockSlimWrapper[] modelGuards = new[] { "rwlockBMSFilesInitializedAll", "rwlockPendingInstallCharts", "rwlockSongDBInstall" }
                        .Select(name => typeof(BMSLibrary).GetField(name, BindingFlags.Instance | BindingFlags.NonPublic)?.GetValue(library)
                            as ReaderWriterLockSlimWrapper ?? throw new InvalidOperationException("Missing model guard: " + name)).ToArray();
                    void RecordPendingNotification(string kind, TaskCompletionSource received)
                    {
                        try
                        {
                            bool guardsReleased = modelGuards.All(guard => guard.LockingReadCount == 0 && guard.LockingWriteCount == 0 && !guard.IsUpgradeableReadLockHeld);
                            using LR2SongDBExtended pendingDb = new BmsLibraryDbGateway(composition.DatabaseScope, new BmsDatabaseLoadOptions(true, true, true), pendingDatabase).OpenSongDbReadOnly();
                            notifications.Add((kind, TestUiDispatcherHost.Dispatcher.CheckAccess(),
                                !composition.OperationAdmission.IsActive && !composition.PlaylistOperationAdmission.IsActive, guardsReleased,
                                library.ChartPackagesPending.Select(package => package.path).ToArray(),
                                pendingDb.Table<LR2SongDBExtended.install>().Select(row => row.path).ToArray(),
                                owner.InstallTree.ChartPackagesPending.Select(package => package.path).ToArray(),
                                guardsReleased ? library.GetPendingBmsFormatChartFilesSnapshot().Select(chart => chart.Path).ToArray() : []));
                        }
                        catch (Exception failure) { notificationFailures.Add(failure); }
                        finally { received.TrySetResult(); }
                    }
                    EventHandler<InstallTreePresentationChangedEventArgs> presentationHandler = (_, args) =>
                    {
                        if ((args.Sections & InstallTreePresentationSection.Pending) != 0) { RecordPendingNotification("presentation", presentationReceived); }
                    };
                    PropertyChangedEventHandler propertyHandler = (_, args) =>
                    {
                        if (args.PropertyName == nameof(InstallTreeViewModel.ChartPackagesPending)) { RecordPendingNotification("property", propertyReceived); }
                    };
                    owner.InstallTree.PresentationChanged += presentationHandler;
                    owner.InstallTree.PropertyChanged += propertyHandler;
                    detachPendingNotifications = () =>
                    {
                        owner.InstallTree.PresentationChanged -= presentationHandler;
                        owner.InstallTree.PropertyChanged -= propertyHandler;
                    };
                    var next = CapturedChartFileScanner.FromFixture([chartA, chartB],
                        new Dictionary<string, IEnumerable<string>> { [initialRoot] = [], [addedRoot] = [] }, [initialRoot]);
                    next.ScanObserved = () =>
                    {
                        settingsLibraryTerminal = composition.OperationAdmission.WaitForIdleAsync();
                        settingsPlaylistTerminal = composition.PlaylistOperationAdmission.WaitForIdleAsync();
                        entered.TrySetResult();
                        release.Wait();
                        if (failScan) { throw scanFailure; }
                    };
                    scanner.Current = next;
                    var capturedRoots = new List<IReadOnlyList<string>>();
                    scanner.RootsObserved = roots => capturedRoots.Add(roots);
                    string candidateRoot = Path.Combine(root, "ADDED");
                    owner.SettingDialog.BmsRootPathList.Add(candidateRoot);
                    owner.SettingDialog.ReplacementBmsInstallDir = candidateRoot;
                    // 同所の導入先も走査の確認表記へ揃うことを、標準compositionのstore通知と実差分で観測します。
                    values.BMSInstallDir = candidateRoot;
                    apply = owner.SettingDialog.CommitBmsRootsAsync();
                    TestUiDispatcherHost.AwaitTaskOnDispatcher(TestUiDispatcherHost.AwaitNotificationAsync(entered.Task, apply, "folder-settings-scan"), "folder-settings-scan");
                    Assert.IsFalse(apply.IsCompleted);
                    Assert.IsTrue(composition.BmsRootRegistrations.CaptureUserRoots().Contains(candidateRoot, StringComparer.OrdinalIgnoreCase),
                        "走査が保留中でも検索rootは保存・公開済みです。");
                    if (lr2) { Assert.IsTrue(new LR2Config(values.LR2ConfigXmlPath).GetBMSSearchDirectoriesForChangeTracking().Contains(candidateRoot, StringComparer.OrdinalIgnoreCase)); }
                    Assert.IsTrue(composition.OperationAdmission.IsActive);
                    Assert.IsTrue(composition.PlaylistOperationAdmission.IsActive);
                    CollectionAssert.DoesNotContain(presentation.Requests, "close");
                    Assert.IsNotNull(settingsLibraryTerminal);
                    Assert.IsNotNull(settingsPlaylistTerminal);
                    Assert.IsFalse(settingsLibraryTerminal.IsCompleted);
                    Assert.IsFalse(settingsPlaylistTerminal.IsCompleted);
                    release.Set();
                    TestUiDispatcherHost.AwaitTaskOnDispatcher(apply, "folder-settings-terminal");
                    TestUiDispatcherHost.AwaitTaskOnDispatcher(TestUiDispatcherHost.AwaitNotificationAsync(
                        Task.WhenAll(presentationReceived.Task, propertyReceived.Task), apply, "folder-settings-pending-notifications"), "folder-settings-pending-notifications");
                    Assert.AreEqual(0, notificationFailures.Count, string.Join(Environment.NewLine, notificationFailures));
                    Assert.IsTrue(notifications.Any(notice => notice.Kind == "presentation"));
                    Assert.IsTrue(notifications.Any(notice => notice.Kind == "property"));
                    foreach ((string Kind, bool Dispatcher, bool AdmissionsReleased, bool GuardsReleased, string[] Current, string[] Persisted, string[] Presented, string[] ReadPaths) notice in notifications)
                    {
                        Assert.IsTrue(notice.Dispatcher, notice.Kind);
                        Assert.IsTrue(notice.AdmissionsReleased, notice.Kind + ": 親L/Pの解放後に通知します。");
                        Assert.IsTrue(notice.GuardsReleased, notice.Kind + ": モデル保護の解放後に同期読取りできます。");
                        CollectionAssert.AreEqual(new[] { unrelated.path }, notice.Current, notice.Kind);
                        CollectionAssert.AreEqual(new[] { unrelated.path }, notice.Persisted, notice.Kind);
                        CollectionAssert.AreEqual(new[] { unrelated.path }, notice.Presented, notice.Kind);
                        CollectionAssert.AreEqual(new[] { unrelatedPath }, notice.ReadPaths, notice.Kind);
                    }
                    Assert.AreEqual(0, failures.Count, string.Join(Environment.NewLine, failures));
                    if (failScan)
                    {
                        // scanner例外は既存の不完全走査結果へ変換されます。必須警告の実受信で元原因の伝達を確認します。
                        TestUiDispatcherHost.AwaitTaskOnDispatcher(TestUiDispatcherHost.AwaitNotificationAsync(
                            scanWarningReceived.Task, apply, "folder-settings-incomplete-scan-warning"), "folder-settings-incomplete-scan-warning");
                        StringAssert.Contains(scanWarnings.Single(message => message.Contains(scanFailure.Message, StringComparison.Ordinal)),
                            "chart_file_scan_failed:" + scanFailure.Message);
                    }
                    Assert.IsTrue(owner.SettingDialog.ConfirmedBMSDirectories.Contains(addedRoot, StringComparer.OrdinalIgnoreCase));
                    Assert.AreEqual(failScan ? candidateRoot : addedRoot, owner.SettingDialog.BMSInstallDir);
                    CollectionAssert.Contains(owner.SettingDialog.BmsRootPathList.ToArray(), candidateRoot);
                    Assert.AreEqual(1, defaultPlayerCount, "Folder段階はplayerの再構築を追加しません。");
                    if (!failScan) { Assert.AreEqual("Folder B", library.BmsCharts.Single(chart => chart.Path == chartB).RawTitle); }
                    CollectionAssert.AreEqual(new[] { unrelated.path }, library.ChartPackagesPending.Select(package => package.path).ToArray());
                    CollectionAssert.AreEqual(new[] { unrelated.path }, owner.InstallTree.ChartPackagesPending.Select(package => package.path).ToArray());
                    CollectionAssert.AreEqual(chartBefore, File.ReadAllBytes(chartB));
                    CollectionAssert.AreEqual(unrelatedBefore, File.ReadAllBytes(unrelatedPath));
                    using (LR2SongDBExtended pendingDb = new BmsLibraryDbGateway(composition.DatabaseScope, new BmsDatabaseLoadOptions(true, true, true), pendingDatabase).OpenSongDbReadOnly())
                    { CollectionAssert.AreEqual(new[] { unrelated.path }, pendingDb.Table<LR2SongDBExtended.install>().Select(row => row.path).ToArray()); }

                    using (LR2SongDBExtended db = new BmsLibraryDbGateway(composition.DatabaseScope, new BmsDatabaseLoadOptions(true, true, true), lr2 ? values.LR2SongDBPath : composition.ApplicationPathSnapshot.StandaloneSongDbPath).OpenSongDbReadOnly())
                    {
                        if (failScan) { Assert.IsFalse(db.Table<LR2SongDB.song>().Any(row => row.path == chartB)); }
                        else { Assert.AreEqual("Folder B", db.Table<LR2SongDB.song>().Single(row => row.path == chartB).title); }
                    }
                    if (lr2)
                    {
                        BMSPlaylist playlist = factory.CreatedPlaylist ?? throw new InvalidOperationException("Actual playlist is unavailable.");
                        Assert.IsTrue(capturedRoots.Any(roots => roots.Contains(addedRoot, StringComparer.OrdinalIgnoreCase)), "同じ確定操作の実走査へ新登録を渡します。");
                        Assert.IsTrue(composition.ResolveLr2Config(values.LR2ConfigXmlPath).Capture().RegisteredRoots.Contains(addedRoot, StringComparer.OrdinalIgnoreCase));
                        Assert.IsTrue(new LR2Config(values.LR2ConfigXmlPath).GetBMSSearchDirectoriesForChangeTracking().Contains(addedRoot, StringComparer.OrdinalIgnoreCase));
                    }
                    Assert.AreEqual(!failScan, owner.MainChartList.Rows.OfType<LibraryChartRow>().Any(row => row.Title == "Folder B"));
                    Assert.IsTrue(settingsLibraryTerminal.IsCompletedSuccessfully);
                    Assert.IsTrue(settingsPlaylistTerminal.IsCompletedSuccessfully);
                    if (lr2 && !failScan)
                    {
                        detachPendingNotifications();
                        detachPendingNotifications = null;
                        next.ScanObserved = null;
                        capturedRoots.Clear();
                        File.WriteAllText(values.LR2ConfigXmlPath, "<external-broken");
                        TestUiDispatcherHost.AwaitTaskOnDispatcher(owner.ReloadFileDiffAsync(), "lr2-confirm-next-diff");
                        Assert.IsTrue(capturedRoots.Any(roots => roots.Contains(addedRoot, StringComparer.OrdinalIgnoreCase)), "次の差分も同じ公開済み登録を走査します。");
                        capturedRoots.Clear();
                        TestUiDispatcherHost.AwaitTaskOnDispatcher(owner.ReinitializeLibraryAsync(), "lr2-confirm-reinitialize");
                        Assert.IsTrue(capturedRoots.Any(roots => roots.Contains(addedRoot, StringComparer.OrdinalIgnoreCase)), "全再初期化も同じ公開済み登録を走査します。");
                        CollectionAssert.Contains(library.Lr2Synchronization.CaptureBmsDirectories().RequestedRoots.ToArray(), addedRoot);
                        Assert.AreEqual("Folder B", library.BmsCharts.Single(chart => chart.Path == chartB).RawTitle);
                        Assert.IsTrue(owner.MainChartList.Rows.OfType<LibraryChartRow>().Any(row => row.Title == "Folder B"));
                    }
                }
                finally
                {
                    release.Set();
                    try { if (apply != null) { TestUiDispatcherHost.AwaitTaskOnDispatcher(apply, "folder-settings-cleanup-task"); } }
                    finally
                    {
                        detachPendingNotifications?.Invoke();
                        TestUiDispatcherHost.AwaitTaskOnDispatcher(owner.ShellShutdownWorkflow.RequestWindowCloseAsync(), "folder-settings-cleanup");
                        owner.SettingDialog.Dispose();
                    }
                }
            });
        }
        finally { release.Set(); Directory.Delete(root, recursive: true); }
    }

    /// <summary>無効な検索ルートでは通常起動だけが初回言語選択／設定再表示を要求し、設定経由では重ねて表示せず、進捗 block と profile を残さないことを確認します。</summary>
    [TestMethod]
    [DataRow(true, false)]
    [DataRow(true, true)]
    [DataRow(false, false)]
    [DataRow(false, true)]
    public async Task InitializeLibrary_SettingsValidationFailureRoutesGuidanceByCaller(
        bool firstStartup, bool fromSettings)
    {
        string root = CreateTemporaryRoot();
        try
        {
            Settings invalidSettings = CreateValidStandaloneSettings(root);
            invalidSettings.BMSRootPath = string.Empty;
            invalidSettings.StandaloneBmsRootPaths = string.Empty;
            var settingsSession = new CountingSettingsEditSession(invalidSettings);
            var dialogs = new RecordingRootDialogService();
            MainWindowViewModel viewModel = CreateViewModel(
                settingsSession,
                firstStartup,
                dialogs: dialogs);
            var presentation = new RecordingSettingsDialogPresentationPort();
            viewModel.SettingDialog.AttachPresentationPort(presentation);

            if (fromSettings)
            {
                Assert.AreEqual(
                    StartupInitializationOutcome.SettingsRequired,
                    (await ((ISettingsDialogStatePort)viewModel).InitializeLibraryAsync(null)).Outcome);
            }
            else
            {
                Assert.IsFalse(await viewModel.InitializeAsync());
            }
            CollectionAssert.AreEqual(
                fromSettings ? Array.Empty<string>() : new[] { firstStartup ? "initial-setup" : "open" },
                presentation.Requests);
            bool languageSelection = firstStartup && !fromSettings;
            Assert.AreEqual(languageSelection ? 0 : 1, dialogs.MessageCount);
            if (!languageSelection)
            {
                Assert.AreEqual(Resources.Msg_init_settings_check, dialogs.LastMessageText);
            }
            Assert.IsFalse(viewModel.ProgressHub.StartupProgress.IsStartupUiInteractionBlocked);
            Assert.IsFalse(viewModel.IsInitializationCompleted);
            Assert.IsFalse(viewModel.HasActiveLibraryProfile);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    /// <summary>実LR2設定不備に対する任意警告が失敗しても設定修復の結果・呼出元の案内を保ち、再帰通知・構築・元XML/DBの変更を始めません。</summary>
    [TestMethod]
    [DataRow("startup", false)] // 通常起動の警告Task失敗でも設定を案内します。
    [DataRow("startup", true)] // 通常起動のFailed結果でも設定を案内します。
    [DataRow("typed", false)] // 型付き終端は警告Task失敗を設定検証結果へ混ぜません。
    [DataRow("typed", true)] // 型付き終端はFailed結果でもSettingsRequiredを維持します。
    public void InvalidLr2Settings_OptionalWarningFailureRetainsSettingsOutcomeAndGuidance(string entry, bool failedResult)
    {
        string root = CreateTemporaryRoot();
        try
        {
            Settings settings = CreateValidCustomFolderSettings(Path.Combine(root, "lr2"), []);
            string configPath = settings.LR2ConfigXmlPath;
            string songDbPath = settings.LR2SongDBPath;
            StartupLibraryConstructionTestSupport.CreateSongDatabase(databaseScope, songDbPath);
            byte[] songDbBefore = File.ReadAllBytes(songDbPath);
            byte[] invalidConfigBefore = [];
            void InvalidateConfig()
            {
                File.WriteAllText(configPath, "<config>");
                invalidConfigBefore = File.ReadAllBytes(configPath);
            }
            InvalidateConfig();
            TestUiDispatcherHost.Invoke(() =>
            {
                var session = new CountingSettingsEditSession(settings);
                var notificationFailure = new IOException("settings validation warning failed");
                var dialogs = new RecordingRootDialogService();
                if (failedResult) { dialogs.MessageResult = UiDialogResult.Failed(notificationFailure); }
                else { dialogs.MessageObservedAsync = () => Task.FromException(notificationFailure); }
                var sequence = new List<string>();
                var applyFailures = new List<Exception>();
                var constructionCalls = new List<string>();
                var failurePresenter = new MainWindowViewModelStartupProgressTests.RecordingStartupLibraryInitializationFailurePresenter();
                var composition = new ApplicationComposition(settingsEditSession: session,
                    reportSettingsApplyFailure: applyFailures.Add,
                    uiScheduler: new TestUiScheduler(() => TestUiDispatcherHost.Dispatcher),
                    applicationLifetime: TestApplicationContext.CreateLifetime(firstStartup: false),
                    cultureCatalog: TestApplicationContext.CreateCultureCatalog(),
                    runtimeOptions: MainWindowViewModelTestFactory.CreateRuntimeOptions(ApplicationPathSnapshot.FromExecutablePath(Path.Combine(root, "application", "BeMusicSeeker.exe"))),
                    settingsDialogService: dialogs, fileDbMutationDialogService: dialogs);
                var factory = new StartupLibraryConstructionTestSupport.RecordingDelegatingStartupLibraryFactory(composition, constructionCalls);
                var owner = new MainWindowViewModel(composition, factory, startupLibraryInitializationFailurePresenter: failurePresenter);
                SettingsDialogViewModel dialog = owner.SettingDialog;
                var cleanupObservations = new List<(string Phase, string Message, bool LibraryActive, bool PlaylistActive, bool UiBlocked, bool OperationInProgress, bool Editing)>();
                void ObserveCleanup(string phase, string message)
                    => cleanupObservations.Add((phase, message, composition.OperationAdmission.IsActive, composition.PlaylistOperationAdmission.IsActive,
                        owner.ProgressHub.StartupProgress.IsStartupUiInteractionBlocked, owner.IsLibraryOperationInProgress, dialog.IsEditCompletionInProgress));
                dialogs.MessageObserved = () =>
                {
                    ObserveCleanup("warning", dialogs.LastMessageText);
                    sequence.Add("warning");
                };
                dialog.AttachPresentationPort(new RecordingSettingsDialogPresentationPort(request =>
                {
                    sequence.Add(request);
                    if (request == "open") { ObserveCleanup("open", string.Empty); }
                }));
                try
                {
                    if (entry == "typed")
                    {
                        Task<StartupInitializationResult> operation = ((ISettingsDialogStatePort)owner).InitializeLibraryAsync(null);
                        TestUiDispatcherHost.AwaitTaskOnDispatcher(operation, "invalid-lr2-warning-failed-typed");
                        StartupInitializationResult result = operation.GetAwaiter().GetResult();
                        Assert.AreEqual(StartupInitializationOutcome.SettingsRequired, result.Outcome);
                        Assert.IsNotNull(result.ValidationFailure);
                        Assert.IsNull(result.Failure);
                        Assert.IsFalse(result.CompletionPublished);
                    }
                    else
                    {
                        Task<bool> operation = owner.InitializeAsync();
                        TestUiDispatcherHost.AwaitTaskOnDispatcher(operation, "invalid-lr2-warning-failed-startup");
                        Assert.IsFalse(operation.GetAwaiter().GetResult());
                    }
                    ObserveCleanup("terminal", string.Empty);
                    Assert.AreEqual(entry == "startup" ? 3 : 2, cleanupObservations.Count);
                    Assert.AreEqual(Resources.Msg_init_settings_check, cleanupObservations.Single(value => value.Phase == "warning").Message);
                    foreach ((string Phase, string Message, bool LibraryActive, bool PlaylistActive, bool UiBlocked, bool OperationInProgress, bool Editing) observation in cleanupObservations)
                    {
                        Assert.IsFalse(observation.LibraryActive, observation.Phase);
                        Assert.IsFalse(observation.PlaylistActive, observation.Phase);
                        Assert.IsFalse(observation.UiBlocked, observation.Phase);
                        Assert.IsFalse(observation.OperationInProgress, observation.Phase);
                        Assert.IsFalse(observation.Editing, observation.Phase);
                    }
                    Assert.IsFalse(owner.IsInitializationCompleted);
                    Assert.IsFalse(owner.HasActiveLibraryProfile);
                    Assert.AreEqual(1, dialogs.MessageCount);
                    Assert.AreEqual(0, applyFailures.Count);
                    Assert.AreEqual(0, failurePresenter.Presentations.Count);
                    Assert.AreEqual(0, constructionCalls.Count);
                    CollectionAssert.AreEqual(entry == "startup" ? new[] { "warning", "open" } : new[] { "warning" }, sequence);
                }
                finally
                {
                    TestUiDispatcherHost.AwaitTaskOnDispatcher(owner.ShellShutdownWorkflow.RequestWindowCloseAsync(), "invalid-lr2-warning-failed-shutdown");
                    dialog.Dispose();
                }
            });
            CollectionAssert.AreEqual(invalidConfigBefore, File.ReadAllBytes(configPath));
            CollectionAssert.AreEqual(songDbBefore, File.ReadAllBytes(songDbPath));
        }
        finally { Directory.Delete(root, recursive: true); }
    }

    /// <summary>有効な DB／config がある LR2 root 未設定は通常起動だけ警告し、設定経由は警告を重ねず、どちらも実初期化を成功できることを確認します。</summary>
    [TestMethod]
    [DataRow(false, false)]
    [DataRow(false, true)]
    [DataRow(true, false)]
    [DataRow(true, true)]
    public void InitializeLibrary_Lr2RootPathWarningIsLimitedToEmptyRootOnNormalStartup(bool rootPathEmpty, bool fromSettings)
    {
        string scope = CreateTemporaryRoot();
        string lr2LayoutRoot = Path.Combine(scope, "lr2-layout");
        string applicationRoot = Path.Combine(scope, "application");
        try
        {
            Settings settings = CreateValidCustomFolderSettings(lr2LayoutRoot, []);
            if (rootPathEmpty)
            {
                settings.LR2RootPath = string.Empty;
            }
            string songDbPath = settings.LR2SongDBPath;
            using (SQLiteConnectionEx db = databaseScope.OpenProviderConnection(songDbPath))
            {
                db.CreateTable<LR2SongDB.song>();
                db.CreateTable<LR2SongDB.folder>();
            }
            new BmsLibraryDbGateway(databaseScope, new BmsDatabaseLoadOptions(true, true, true), songDbPath).EnsureAppOwnedSchema();
            PlaylistPersistenceRepository.EnsureSchema(databaseScope, songDbPath);
            var settingsSession = new CountingSettingsEditSession(settings);
            var dialogs = new RecordingRootDialogService();
            var applicationPath = ApplicationPathSnapshot.FromExecutablePath(
                Path.Combine(applicationRoot, "BeMusicSeeker.exe"));

            TestUiDispatcherHost.Invoke(() =>
            {
                var config = new LR2Config(settings.LR2ConfigXmlPath);
                var composition = new ApplicationComposition(
                    settingsEditSession: settingsSession,
                    uiScheduler: new TestUiScheduler(() => TestUiDispatcherHost.Dispatcher),
                    applicationLifetime: TestApplicationContext.CreateLifetime(firstStartup: false),
                    cultureCatalog: TestApplicationContext.CreateCultureCatalog(),
                    runtimeOptions: MainWindowViewModelTestFactory.CreateRuntimeOptions(applicationPath),
                    rootFileEnumerator: new FastRootFileEnumerator(),
                    fileDbMutationDialogService: dialogs);
                Lr2ConfigOwner configOwner = composition.ResolveLr2Config(settings.LR2ConfigXmlPath);
                var library = new TestBmsLibrary(composition.DatabaseScope,
                    songDbPath,
                    configOwner,
                    _lr2ScoreDB: null,
                    startupRequiredFileScanReason: null,
                    optionsSnapshotProvider: () => BmsLibraryOptionsSnapshot.CreateCurrent(settings),
                    applicationPathSnapshot: applicationPath,
                    chartFileScanner: CapturedChartFileScanner.FromFixture([],
                        new Dictionary<string, IEnumerable<string>> { [Path.Combine(lr2LayoutRoot, "Songs")] = [] }, [Path.Combine(lr2LayoutRoot, "Songs")]),
                    uiScheduler: composition.UiScheduler,
                    rootFileEnumerator: new FastRootFileEnumerator(),
                    operationAdmission: composition.OperationAdmission,
                    playlistOperationAdmission: composition.PlaylistOperationAdmission);
                TestBmsPlaylist playlist = MainWindowViewModelTestFactory.CreatePlaylist(composition.DatabaseScope, songDbPath, settings, configOwner, library: library);
                MainWindowViewModel viewModel = new(
                    composition,
                    new LateFailureStartupLibraryFactory(library, playlist));
                var presentation = new RecordingSettingsDialogPresentationPort();
                viewModel.SettingDialog.AttachPresentationPort(presentation);
                try
                {
                    if (fromSettings)
                    {
                        Task<StartupInitializationResult> initialization =
                            ((ISettingsDialogStatePort)viewModel).InitializeLibraryAsync(null);
                        TestUiDispatcherHost.AwaitTaskOnDispatcher(initialization, "lr2-root-warning-settings-initialization");
                        Assert.AreEqual(StartupInitializationOutcome.Succeeded, initialization.GetAwaiter().GetResult().Outcome);
                        TestUiDispatcherHost.AwaitTaskOnDispatcher(((ISettingsDialogStatePort)viewModel).CompleteRequiredInitializationAfterAdmissionAsync(initialization.GetAwaiter().GetResult()), "lr2-root-warning-settings-completion");
                    }
                    else
                    {
                        Task<bool> initialization = viewModel.InitializeAsync();
                        TestUiDispatcherHost.AwaitTaskOnDispatcher(initialization, "lr2-root-warning-startup-initialization");
                        Assert.IsTrue(initialization.GetAwaiter().GetResult());
                    }

                    bool warningExpected = rootPathEmpty && !fromSettings;
                    Assert.AreEqual(warningExpected ? 1 : 0, dialogs.MessageCount);
                    if (warningExpected)
                    {
                        Assert.AreEqual(Resources.Warning_LR2RootPathNotSet, dialogs.LastMessageText);
                    }
                    Assert.IsTrue(viewModel.IsInitializationCompleted);
                    Assert.IsTrue(viewModel.HasActiveLibraryProfile);
                    Assert.AreEqual(0, presentation.Requests.Count);
                }
                finally
                {
                    TestUiDispatcherHost.AwaitTaskOnDispatcher(
                        viewModel.ShellShutdownWorkflow.RequestWindowCloseAsync(),
                        "lr2-root-warning-shutdown");
                    viewModel.SettingDialog.Dispose();
                }
            });
        }
        finally
        {
            if (Directory.Exists(scope))
            {
                Directory.Delete(scope, recursive: true);
            }
        }
    }

    /// <summary>起動走査の有無と既存 DB の有無によらず、欠損登録 root では警告後に設定を開き、新規 DB 作成や既存 DB の改変前に止まることを確認します。</summary>
    [TestMethod]
    [DataRow(false, false)]
    [DataRow(false, true)]
    [DataRow(true, false)]
    [DataRow(true, true)]
    public async Task InitializeAsync_MissingStandaloneRootStopsBeforePortableDbCreationAndShowsOneWarning(
        bool scanBmsFilesOnStartup,
        bool existingSongDb)
    {
        string root = CreateTemporaryRoot();
        string applicationRoot = Path.Combine(root, "application");
        string existingRoot = Path.Combine(root, "bms-existing");
        string missingRoot = Path.Combine(root, "bms-missing");
        Directory.CreateDirectory(applicationRoot);
        Directory.CreateDirectory(existingRoot);
        try
        {
            Settings settings = CreateValidStandaloneSettings(existingRoot);
            settings.ScanBmsFilesOnStartup = scanBmsFilesOnStartup;
            settings.BMSRootPath = existingRoot;
            settings.StandaloneBmsRootPaths = string.Join(
                Environment.NewLine,
                existingRoot,
                missingRoot);
            var settingsSession = new CountingSettingsEditSession(settings);
            var dialogs = new RecordingRootDialogService();
            var sequence = new List<string>();
            dialogs.MessageObserved = () => sequence.Add("warning");
            var applicationPath = ApplicationPathSnapshot.FromExecutablePath(
                Path.Combine(applicationRoot, "BeMusicSeeker.exe"));
            byte[] existingSongDbBytes = [0x41, 0x31, 0x2D, 0x44, 0x30, 0x38];
            if (existingSongDb)
            {
                Directory.CreateDirectory(applicationPath.DataDirectoryPath);
                File.WriteAllBytes(applicationPath.StandaloneSongDbPath, existingSongDbBytes);
            }
            MainWindowViewModel viewModel = new ApplicationComposition(
                settingsEditSession: settingsSession,
                uiScheduler: new WpfUiScheduler(() => Dispatcher.CurrentDispatcher),
                applicationLifetime: TestApplicationContext.CreateLifetime(firstStartup: false),
                cultureCatalog: TestApplicationContext.CreateCultureCatalog(),
                runtimeOptions: MainWindowViewModelTestFactory.CreateRuntimeOptions(applicationPath),
                fileDbMutationDialogService: dialogs)
                .CreateMainWindowViewModel();
            var presentation = new RecordingSettingsDialogPresentationPort(sequence.Add);
            viewModel.SettingDialog.AttachPresentationPort(presentation);

            bool initialized = await viewModel.InitializeAsync();

            Assert.IsFalse(initialized);
            Assert.AreEqual(1, dialogs.MessageCount);
            CollectionAssert.AreEqual(new[] { "warning", "open" }, sequence);
            StringAssert.Contains(dialogs.LastMessageText, missingRoot);
            StringAssert.Contains(
                dialogs.LastMessageText,
                Resources.LibraryDirectoryPreflightBmsRootRole);
            if (existingSongDb)
            {
                CollectionAssert.AreEqual(existingSongDbBytes, File.ReadAllBytes(applicationPath.StandaloneSongDbPath));
            }
            else
            {
                Assert.IsFalse(File.Exists(applicationPath.StandaloneSongDbPath));
            }
            Assert.IsFalse(viewModel.IsInitializationCompleted);
            Assert.IsFalse(viewModel.HasActiveLibraryProfile);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    /// <summary>LR2 config の登録 root が欠損すると警告と設定案内に終端し、実 config／DB を保って出力先修復を始めないことを確認します。</summary>
    [TestMethod]
    public async Task InitializeAsync_LinkedMissingRootStopsBeforeOutputRepairAndPreservesConfig()
    {
        string root = CreateTemporaryRoot();
        string applicationRoot = Path.Combine(root, "application");
        string lr2Root = Path.Combine(root, "lr2");
        string missingBmsRoot = Path.Combine(root, "bms-missing");
        string outputBase = Path.Combine(root, "output-base");
        Directory.CreateDirectory(applicationRoot);
        Directory.CreateDirectory(lr2Root);
        try
        {
            (string songDb, string configPath) = CreateValidLr2Layout(lr2Root);
            File.WriteAllText(
                configPath,
                "<config><system /><jukebox><path>"
                    + missingBmsRoot
                    + "\\</path></jukebox></config>");
            byte[] configBefore = File.ReadAllBytes(configPath);
            byte[] songDbBefore = File.ReadAllBytes(songDb);
            Settings settings = CreateValidStandaloneSettings(lr2Root);
            settings.OperationModeLR2DB = true;
            settings.LR2RootPath = lr2Root;
            settings.LR2ConfigXmlPath = configPath;
            settings.LR2SongDBPath = songDb;
            settings.LR2CustomFolderOutputBaseDir = outputBase;
            settings.LR2CustomFolderAdditionalOutputBaseDirs = string.Empty;
            settings.ScanBmsFilesOnStartup = false;
            settings.SkipInitPlaylistLoad = true;
            var settingsSession = new CountingSettingsEditSession(settings);
            var dialogs = new RecordingRootDialogService();
            var sequence = new List<string>();
            dialogs.MessageObserved = () => sequence.Add("warning");
            var applicationPath = ApplicationPathSnapshot.FromExecutablePath(
                Path.Combine(applicationRoot, "BeMusicSeeker.exe"));
            MainWindowViewModel viewModel = new ApplicationComposition(
                settingsEditSession: settingsSession,
                uiScheduler: new WpfUiScheduler(() => Dispatcher.CurrentDispatcher),
                applicationLifetime: TestApplicationContext.CreateLifetime(firstStartup: false),
                cultureCatalog: TestApplicationContext.CreateCultureCatalog(),
                runtimeOptions: MainWindowViewModelTestFactory.CreateRuntimeOptions(applicationPath),
                fileDbMutationDialogService: dialogs)
                .CreateMainWindowViewModel();
            var presentation = new RecordingSettingsDialogPresentationPort(sequence.Add);
            viewModel.SettingDialog.AttachPresentationPort(presentation);

            bool initialized = await viewModel.InitializeAsync();

            Assert.IsFalse(initialized);
            Assert.AreEqual(1, dialogs.MessageCount);
            CollectionAssert.AreEqual(new[] { "warning", "open" }, sequence);
            StringAssert.Contains(dialogs.LastMessageText, missingBmsRoot);
            StringAssert.Contains(
                dialogs.LastMessageText,
                Resources.LibraryDirectoryPreflightBmsRootRole);
            Assert.IsFalse(Directory.Exists(outputBase));
            CollectionAssert.AreEqual(configBefore, File.ReadAllBytes(configPath));
            CollectionAssert.AreEqual(songDbBefore, File.ReadAllBytes(songDb));
            Assert.IsFalse(viewModel.IsInitializationCompleted);
            Assert.IsFalse(viewModel.HasActiveLibraryProfile);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    /// <summary>欠落ルートの再初期化が、受付解放後の警告と元例外を保ち、再入可能な失敗へ終端することを確認します。</summary>
    /// <remarks>起動後処理全体の実終端を次の明示操作の前提とし、取得済み設定受付を借用する実差分処理と元失敗の回収を確認します。</remarks>
    [TestMethod]
    public void ReinitializeLibraryAsync_LateDirectoryFailureWarnsAfterCleanupAndRethrows()
    {
        string root = CreateTemporaryRoot();
        string rootA = Path.Combine(root, "BMS-A");
        string rootB = Path.Combine(root, "BMS-B");
        string unavailableRootB = Path.Combine(root, "BMS-B-unavailable");
        string applicationRoot = Path.Combine(root, "application");
        MainWindowViewModel? viewModel = null;
        ChartFileOperationSynchronizer? admission = null;
        Exception? primaryFailure = null;
        Directory.CreateDirectory(rootA);
        Directory.CreateDirectory(rootB);
        Directory.CreateDirectory(applicationRoot);
        try
        {
            Settings settings = CreateValidStandaloneSettings(rootA);
            settings.BMSRootPath = rootA;
            settings.StandaloneBmsRootPaths = string.Join(Environment.NewLine, rootA, rootB);
            settings.ScanBmsFilesOnStartup = false;
            settings.SkipInitPlaylistLoad = true;
            var settingsSession = new CountingSettingsEditSession(settings);
            var applicationPath = ApplicationPathSnapshot.FromExecutablePath(
                Path.Combine(applicationRoot, "BeMusicSeeker.exe"));
            StandaloneLibraryDatabaseEnsureResult database =
                StandaloneLibraryDatabase.EnsurePortableSongDb(databaseScope, applicationPath);
            PlaylistPersistenceRepository.EnsureSchema(databaseScope, database.SongDbPath);
            IChartFileScanner scanner = CapturedChartFileScanner.FromFixture(
                [],
                new Dictionary<string, IEnumerable<string>>(StringComparer.OrdinalIgnoreCase)
                {
                    [rootA] = [],
                    [rootB] = []
                },
                [rootA, rootB]);
            var dialogs = new RecordingRootDialogService();
            var sequence = new List<string>();
            dialogs.MessageObserved = () => sequence.Add("warning");
            TestUiDispatcherHost.Invoke(() =>
            {
                var composition = new ApplicationComposition(
                    settingsEditSession: settingsSession,
                    uiScheduler: new TestUiScheduler(() => TestUiDispatcherHost.Dispatcher),
                    applicationLifetime: TestApplicationContext.CreateLifetime(firstStartup: false),
                    cultureCatalog: TestApplicationContext.CreateCultureCatalog(),
                    runtimeOptions: MainWindowViewModelTestFactory.CreateRuntimeOptions(applicationPath),
                    fileDbMutationDialogService: dialogs);
                admission = composition.OperationAdmission;
                var library = new TestBmsLibrary(composition.DatabaseScope,
                    database.SongDbPath,
                    getLR2Config: null,
                    _lr2ScoreDB: null,
                    startupRequiredFileScanReason: null,
                    optionsSnapshotProvider: () => BmsLibraryOptionsSnapshot.CreateCurrent(settings),
                    settings: settings,
                    applicationPathSnapshot: TestBmsFactory.MissingEverythingBridge,
                    chartFileScanner: scanner,
                    operationAdmission: composition.OperationAdmission,
                    playlistOperationAdmission: composition.PlaylistOperationAdmission);
                TestBmsPlaylist playlist = MainWindowViewModelTestFactory.CreatePlaylist(composition.DatabaseScope, database.SongDbPath, settings, library: library);
                viewModel = new MainWindowViewModel(
                    composition,
                    new LateFailureStartupLibraryFactory(library, playlist));
                var presentation = new RecordingSettingsDialogPresentationPort(sequence.Add);
                viewModel.SettingDialog.AttachPresentationPort(presentation);
            });

            bool initialized = false;
            TestUiDispatcherHost.Invoke(() =>
            {
                Task<bool> initialization = viewModel!.InitializeAsync();
                TestUiDispatcherHost.AwaitTaskOnDispatcher(initialization, "late-directory-startup-initialization");
                initialized = initialization.GetAwaiter().GetResult();
            });
            Assert.IsTrue(initialized);
            TestUiDispatcherHost.Invoke(() =>
            {
                Assert.IsNotNull(admission);
                Assert.IsNotNull(viewModel);
                AwaitStartupBackgroundCompletionOnDispatcher(viewModel, "late-directory-startup-background-terminal");
                var acceptedReload = new TestSettingsDialogStatePort(viewModel,
                    () => ((ISettingsDialogStatePort)viewModel).InitializeLibraryAsync(null),
                    reloadFileDiff: capability => ((ISettingsDialogStatePort)viewModel).ReloadFileDiffAsync(capability));
                Assert.IsTrue(admission.TryEnter(out IDisposable reloadLease));
                using (reloadLease)
                {
                    using LibraryFileMutationCapability reloadCapability = admission.CreateMutationCapability(reloadLease);
                    Task<StartupInitializationResult> reload = acceptedReload.ReloadFileDiffAsync(reloadCapability);
                    TestUiDispatcherHost.AwaitTaskOnDispatcher(reload, "settings-real-accepted-file-diff-continuation");
                    StartupInitializationResult reloadResult = reload.GetAwaiter().GetResult();
                    Assert.AreEqual(StartupInitializationOutcome.Succeeded, reloadResult.Outcome);
                    Assert.IsNull(reloadResult.Failure);
                    Assert.IsNotNull(reloadResult.RequiredResult?.FileResult);
                    Assert.IsTrue(admission.IsActive, "実差分継続の借用は外側の設定受付を解放しません。");
                    reloadCapability.Validate(admission);
                }
            });
            Directory.Move(rootB, unavailableRootB);
            try
            {
                LibraryDirectoryPreflightException? thrown = null;
                TestUiDispatcherHost.Invoke(() =>
                {
                    try
                    {
                        Task reinitialize = viewModel!.ReinitializeLibraryAsync();
                        TestUiDispatcherHost.AwaitTaskOnDispatcher(reinitialize, "late-directory-reinitialize");
                        Assert.Fail("A missing registered root must fail the reinitialize operation.");
                    }
                    catch (LibraryDirectoryPreflightException exception)
                    {
                        thrown = exception;
                    }
                });

                Assert.IsNotNull(thrown);
                Assert.AreEqual(LibraryDirectoryPreflightUse.BmsRoot, thrown!.Use);
                Assert.AreEqual(1, dialogs.MessageCount);
                CollectionAssert.AreEqual(new[] { "warning" }, sequence);
                StringAssert.Contains(dialogs.LastMessageText, rootB);
                StringAssert.Contains(
                    dialogs.LastMessageText,
                    Resources.LibraryDirectoryPreflightBmsRootRole);
                Assert.IsTrue(viewModel!.ProgressHub.StartupProgress.IsFailed);
                Assert.IsTrue(viewModel!.ProgressHub.StartupProgress.IsRetryableFailure);
            }
            finally
            {
                Directory.Move(unavailableRootB, rootB);
            }
        }
        catch (Exception failure)
        {
            primaryFailure = failure;
            throw;
        }
        finally
        {
            try
            {
                if (viewModel != null)
                {
                    TestUiDispatcherHost.Invoke(() =>
                    {
                        try
                        {
                            // 起動の後続処理は再初期化失敗の cleanup 後も生存するため、実 shell 終了まで回収する。
                            TestUiDispatcherHost.AwaitTaskOnDispatcher(
                                viewModel!.ShellShutdownWorkflow.RequestWindowCloseAsync(),
                                "late-directory-reinitialize-shutdown");
                        }
                        finally
                        {
                            viewModel.SettingDialog.Dispose();
                        }
                    });
                }
                Directory.Delete(root, recursive: true);
            }
            catch (Exception cleanupFailure) when (primaryFailure != null)
            {
                TestContext.WriteLine("Late-directory fixture cleanup: " + cleanupFailure);
            }
        }
    }

    /// <summary>実構成入口の失敗通知中にL/Pと進捗が解放され、元の構成失敗を保持して設定を自動再表示しないことを確認します。</summary>
    [TestMethod]
    [DataRow("startup")] // 通常起動も実構成失敗で設定を自動再表示しません。
    [DataRow("typed")] // 型付き入口は元失敗とOperationFailedを返し、設定検証結果へ読み替えません。
    [DataRow("apply")] // 実設定適用も型付き処理失敗を設定修復へ読み替えず、保存・Close・cleanupを維持します。
    public void InitializeAsync_ConstructionFailurePresentsAfterBothAdmissionsAndProgressCleanup(string entry)
    {
        string root = CreateTemporaryRoot();
        string songsRoot = Path.Combine(root, "Songs");
        string applicationRoot = Path.Combine(root, "application");
        Directory.CreateDirectory(songsRoot);
        Directory.CreateDirectory(applicationRoot);
        MainWindowViewModel? viewModel = null;
        TestBmsLibrary? library = null;
        TestBmsPlaylist? playlist = null;
        try
        {
            Settings settings = CreateValidStandaloneSettings(songsRoot);
            var applicationPath = ApplicationPathSnapshot.FromExecutablePath(Path.Combine(applicationRoot, "BeMusicSeeker.exe"));
            StandaloneLibraryDatabaseEnsureResult database = StandaloneLibraryDatabase.EnsurePortableSongDb(databaseScope, applicationPath);
            PlaylistPersistenceRepository.EnsureSchema(databaseScope, database.SongDbPath);
            var dialogs = new RecordingRootDialogService();
            var sequence = new List<string>();
            var failure = new InvalidOperationException("controlled startup construction failure");
            var applyFailures = new List<Exception>();
            TestUiDispatcherHost.Invoke(() =>
            {
                var scheduler = new TestUiScheduler(() => TestUiDispatcherHost.Dispatcher);
                var session = new CountingSettingsEditSession(settings);
                var composition = new ApplicationComposition(settingsEditSession: session,
                    reportSettingsApplyFailure: exception =>
                    {
                        applyFailures.Add(exception);
                        dialogs.MessageObserved?.Invoke();
                    },
                    uiScheduler: scheduler, applicationLifetime: TestApplicationContext.CreateLifetime(firstStartup: false),
                    cultureCatalog: TestApplicationContext.CreateCultureCatalog(), runtimeOptions: MainWindowViewModelTestFactory.CreateRuntimeOptions(applicationPath),
                    fileDbMutationDialogService: dialogs);
                library = new TestBmsLibrary(composition.DatabaseScope, database.SongDbPath, null, null, null,
                    () => BmsLibraryOptionsSnapshot.CreateCurrent(settings), TestBmsFactory.MissingEverythingBridge,
                    operationAdmission: composition.OperationAdmission, playlistOperationAdmission: composition.PlaylistOperationAdmission,
                    uiScheduler: scheduler, settings: settings);
                playlist = MainWindowViewModelTestFactory.CreatePlaylist(composition.DatabaseScope, database.SongDbPath, settings, library: library);
                var owner = new MainWindowViewModel(composition, new LateFailureStartupLibraryFactory(library, playlist, () => throw failure));
                viewModel = owner;
                owner.SettingDialog.AttachPresentationPort(new RecordingSettingsDialogPresentationPort(sequence.Add));
                var notificationObservations = new List<(bool LibraryAvailable, bool PlaylistAvailable, bool OperationInProgress, bool UiBlocked, bool Retryable)>();
                var observationFailures = new List<Exception>();
                dialogs.MessageObserved = () =>
                {
                    sequence.Add("error");
                    try
                    {
                        bool libraryAvailable = composition.OperationAdmission.TryEnter(out IDisposable libraryLease);
                        bool playlistAvailable;
                        using (libraryLease)
                        {
                            playlistAvailable = composition.PlaylistOperationAdmission.TryEnter(out IDisposable playlistLease);
                            playlistLease?.Dispose();
                        }
                        notificationObservations.Add((libraryAvailable, playlistAvailable, owner.IsLibraryOperationInProgress,
                            owner.ProgressHub.StartupProgress.IsStartupUiInteractionBlocked, owner.ProgressHub.StartupProgress.IsRetryableFailure));
                    }
                    catch (Exception observationFailure) { observationFailures.Add(observationFailure); }
                };
                if (entry == "apply")
                {
                    SettingsDialogViewModel dialog = owner.SettingDialog;
                    session.SaveObserved = () => sequence.Add("save");
                    dialog.ShowRecommUpdatedMsg = !dialog.ShowRecommUpdatedMsg;
                    bool savedValue = dialog.ShowRecommUpdatedMsg;
                    Assert.IsTrue(dialog.CheckValidationForInitialization(out string validationError), validationError);
                    Task operation = dialog.StartConfirmedEnvironmentAsync();
                    TestUiDispatcherHost.AwaitTaskOnDispatcher(operation, "startup-construction-failure-apply");
                    Assert.AreEqual(savedValue, settings.ShowRecommUpdatedMsg);
                    Assert.IsTrue(dialog.IsEditCompletionEnabled);
                    Assert.IsTrue(dialog.IsEditCancellationEnabled);
                    Assert.IsFalse(dialog.IsEditCompletionInProgress);
                    Assert.IsFalse(owner.HasActiveLibraryProfile);
                    Assert.IsFalse(owner.IsInitializationCompleted);
                    Assert.IsFalse(composition.OperationAdmission.IsActive);
                    Assert.IsFalse(composition.PlaylistOperationAdmission.IsActive);
                    Assert.AreSame(failure, applyFailures.Single());
                }
                else if (entry == "typed")
                {
                    Task<StartupInitializationResult> operation = ((ISettingsDialogStatePort)owner).InitializeLibraryAsync(null);
                    TestUiDispatcherHost.AwaitTaskOnDispatcher(operation, "startup-construction-failure-typed");
                    StartupInitializationResult result = operation.GetAwaiter().GetResult();
                    Assert.AreEqual(StartupInitializationOutcome.OperationFailed, result.Outcome);
                    Assert.AreSame(failure, result.Failure);
                    Assert.IsNull(result.ValidationFailure);
                    Assert.IsFalse(result.CompletionPublished);
                }
                else
                {
                    Task<bool> operation = owner.InitializeAsync();
                    TestUiDispatcherHost.AwaitTaskOnDispatcher(operation, "startup-construction-failure");
                    Assert.IsFalse(operation.GetAwaiter().GetResult());
                }
                Assert.AreEqual(0, observationFailures.Count, string.Join(Environment.NewLine, observationFailures));
                Assert.AreEqual(1, notificationObservations.Count);
                (bool LibraryAvailable, bool PlaylistAvailable, bool OperationInProgress, bool UiBlocked, bool Retryable) observation = notificationObservations.Single();
                Assert.IsTrue(observation.LibraryAvailable);
                Assert.IsTrue(observation.PlaylistAvailable);
                Assert.IsFalse(observation.OperationInProgress);
                Assert.IsFalse(observation.UiBlocked);
                Assert.IsTrue(observation.Retryable);
            });
            Assert.AreEqual(entry == "apply" ? 0 : 1, dialogs.MessageCount);
            if (entry != "apply") { StringAssert.Contains(dialogs.LastMessageText, failure.Message); }
            CollectionAssert.AreEqual(new[] { "error" }, sequence);
        }
        finally
        {
            if (viewModel != null)
            {
                TestUiDispatcherHost.Invoke(() =>
                {
                    TestUiDispatcherHost.AwaitTaskOnDispatcher(viewModel.ShellShutdownWorkflow.RequestWindowCloseAsync(), "startup-construction-failure-close");
                    viewModel.SettingDialog.Dispose();
                });
            }
            library?.RequestShutdown("startup-construction-fixture-cleanup");
            playlist?.RequestShutdown("startup-construction-fixture-cleanup");
            Directory.Delete(root, recursive: true);
        }
    }

    /// <summary>通常起動と設定借用の実入口で終了し、ShutdownRequested相当・初回状態保持・親解禁/外部投入の中止と全cleanupを確認します。</summary>
    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public void InitializeLibrary_ShutdownRetainsFirstStartupAndDoesNotUnlockOrQueueExternalWork(bool fromSettings)
    {
        string root = CreateTemporaryRoot();
        string songsRoot = Path.Combine(root, "Songs");
        string applicationRoot = Path.Combine(root, "application");
        Directory.CreateDirectory(songsRoot);
        Directory.CreateDirectory(applicationRoot);
        MainWindowViewModel? viewModel = null;
        TestBmsLibrary? library = null;
        TestBmsPlaylist? playlist = null;
        Task<ShellShutdownWorkflowCompletionReceipt>? closeOperation = null;
        try
        {
            Settings settings = CreateValidStandaloneSettings(songsRoot);
            var applicationPath = ApplicationPathSnapshot.FromExecutablePath(Path.Combine(applicationRoot, "BeMusicSeeker.exe"));
            StandaloneLibraryDatabaseEnsureResult database = StandaloneLibraryDatabase.EnsurePortableSongDb(databaseScope, applicationPath);
            PlaylistPersistenceRepository.EnsureSchema(databaseScope, database.SongDbPath);
            var dialogs = new RecordingRootDialogService();
            var lifetime = new TestApplicationLifetime(firstStartup: true);
            var sequence = new List<string>();
            TestUiDispatcherHost.Invoke(() =>
            {
                var scheduler = new TestUiScheduler(() => TestUiDispatcherHost.Dispatcher);
                var composition = new ApplicationComposition(settingsEditSession: new CountingSettingsEditSession(settings),
                    uiScheduler: scheduler, applicationLifetime: lifetime, cultureCatalog: TestApplicationContext.CreateCultureCatalog(),
                    runtimeOptions: MainWindowViewModelTestFactory.CreateRuntimeOptions(applicationPath), fileDbMutationDialogService: dialogs, defaultBmsPlayerFactory: () => new RecordingPlaybackPlayer());
                library = new TestBmsLibrary(composition.DatabaseScope, database.SongDbPath, null, null, null,
                    () => BmsLibraryOptionsSnapshot.CreateCurrent(settings), TestBmsFactory.MissingEverythingBridge,
                    operationAdmission: composition.OperationAdmission, playlistOperationAdmission: composition.PlaylistOperationAdmission,
                    uiScheduler: scheduler, settings: settings);
                playlist = MainWindowViewModelTestFactory.CreatePlaylist(composition.DatabaseScope, database.SongDbPath, settings, library: library);
                var factory = new LateFailureStartupLibraryFactory(library, playlist, () =>
                {
                    MainWindowViewModel activeOwner = viewModel ?? throw new InvalidOperationException("startup owner not composed");
                    closeOperation = activeOwner.ShellShutdownWorkflow.RequestWindowCloseAsync();
                });
                var owner = new MainWindowViewModel(composition, factory);
                viewModel = owner;
                owner.SettingDialog.AttachPresentationPort(new RecordingSettingsDialogPresentationPort(sequence.Add));
                if (fromSettings)
                {
                    using var semaphore = new SemaphoreSlim(1, 1);
                    var requiredOwner = new StartupLibraryInitializationWorkflowOwner(composition.DatabaseScope, new BmsDatabaseLoadOptions(true, true, true), semaphore);
                    using (StartupRequiredOperationLease accepted = requiredOwner.AcquireRequiredOperation(composition.OperationAdmission, composition.PlaylistOperationAdmission))
                    {
                        Task<StartupInitializationResult> operation = ((ISettingsDialogStatePort)owner).InitializeLibraryAsync(accepted.Capability, new(database.SongDbPath), _ => { });
                        TestUiDispatcherHost.AwaitTaskOnDispatcher(operation, "settings-required-shutdown");
                        Assert.AreEqual(StartupInitializationOutcome.ShutdownRequested, operation.GetAwaiter().GetResult().Outcome);
                    }
                }
                else
                {
                    Task<bool> operation = owner.InitializeAsync();
                    TestUiDispatcherHost.AwaitTaskOnDispatcher(operation, "normal-required-shutdown");
                    Assert.IsFalse(operation.GetAwaiter().GetResult());
                }
                TestUiDispatcherHost.AwaitTaskOnDispatcher(((ISettingsDialogStatePort)owner).CompleteRequiredInitializationAfterAdmissionAsync(new(StartupInitializationOutcome.ShutdownRequested)), "settings-required-shutdown-completion");
                Assert.IsTrue(lifetime.IsFirstStartup);
                Assert.IsFalse(owner.IsInitializationCompleted);
                Assert.IsFalse(owner.HasActiveLibraryProfile);
                Assert.IsFalse(owner.ProgressHub.StartupProgress.IsStartupInitializationRequiredProgressComplete(owner.ProgressHub.StartupProgress.GetActiveStartupProgressOperationToken()));
                Assert.IsFalse(composition.OperationAdmission.IsActive);
                Assert.IsFalse(composition.PlaylistOperationAdmission.IsActive);
                Assert.IsNotNull(closeOperation);
                TestUiDispatcherHost.AwaitTaskOnDispatcher(closeOperation, "required-shutdown-terminal");
            });
            Assert.AreEqual(0, dialogs.MessageCount);
            CollectionAssert.AreEqual(Array.Empty<string>(), sequence);
        }
        finally
        {
            if (viewModel != null)
            {
                TestUiDispatcherHost.Invoke(() =>
                {
                    TestUiDispatcherHost.AwaitTaskOnDispatcher(viewModel.ShellShutdownWorkflow.RequestWindowCloseAsync(), "required-shutdown-close");
                    viewModel.SettingDialog.Dispose();
                });
            }
            library?.RequestShutdown("required-shutdown-fixture-cleanup");
            playlist?.RequestShutdown("required-shutdown-fixture-cleanup");
            Directory.Delete(root, recursive: true);
        }
    }

    /// <summary>構築直後に root が消えた起動は受付・進捗を解放して警告し、警告 callback 内の復旧と再起動を完了できることを確認して、通知中の自己 Busy を検出します。</summary>
    [TestMethod]
    public void InitializeAsync_LateDirectoryFailureAfterConstructionWarnsAfterCleanupAndOpensSettings()
    {
        string root = CreateTemporaryRoot();
        string rootA = Path.Combine(root, "BMS-A");
        string rootB = Path.Combine(root, "BMS-B");
        string unavailableRootB = Path.Combine(root, "BMS-B-unavailable");
        string applicationRoot = Path.Combine(root, "application");
        MainWindowViewModel? viewModel = null;
        Task<bool>? retryInitialization = null;
        Directory.CreateDirectory(rootA);
        Directory.CreateDirectory(rootB);
        Directory.CreateDirectory(applicationRoot);
        try
        {
            Settings settings = CreateValidStandaloneSettings(rootA);
            settings.BMSRootPath = rootA;
            settings.StandaloneBmsRootPaths = string.Join(Environment.NewLine, rootA, rootB);
            settings.ScanBmsFilesOnStartup = false;
            settings.SkipInitPlaylistLoad = true;
            var settingsSession = new CountingSettingsEditSession(settings);
            var applicationPath = ApplicationPathSnapshot.FromExecutablePath(
                Path.Combine(applicationRoot, "BeMusicSeeker.exe"));
            StandaloneLibraryDatabaseEnsureResult database =
                StandaloneLibraryDatabase.EnsurePortableSongDb(databaseScope, applicationPath);
            PlaylistPersistenceRepository.EnsureSchema(databaseScope, database.SongDbPath);
            IChartFileScanner scanner = CapturedChartFileScanner.FromFixture(
                [],
                new Dictionary<string, IEnumerable<string>>(StringComparer.OrdinalIgnoreCase)
                {
                    [rootA] = [],
                    [rootB] = []
                },
                [rootA, rootB]);
            var dialogs = new RecordingRootDialogService();
            var sequence = new List<string>();
            bool lateFailureInjected = false;
            bool retryRequested = false;
            bool warningObservedWithOperationReleased = false;
            bool warningObservedWithProgressUnblocked = false;
            bool retryCompletedDuringWarning = false;
            bool retrySucceededDuringWarning = false;
            bool retryTimedOutDuringWarning = false;
            Exception? retryFailure = null;
            Exception? retryDrainFailure = null;
            dialogs.MessageObserved = () =>
            {
                sequence.Add("warning");
            };
            dialogs.MessageObservedAsync = async () =>
            {
                warningObservedWithOperationReleased = !viewModel!.IsLibraryOperationInProgress;
                warningObservedWithProgressUnblocked = !viewModel!.ProgressHub.StartupProgress.IsStartupUiInteractionBlocked;
                if (!retryRequested)
                {
                    retryRequested = true;
                    Directory.Move(unavailableRootB, rootB);
                    retryInitialization = viewModel!.InitializeAsync();
                }
                try
                {
                    retrySucceededDuringWarning = await retryInitialization!;
                    retryCompletedDuringWarning = retryInitialization!.IsCompleted;
                }
                catch (TimeoutException)
                {
                    retryTimedOutDuringWarning = true;
                    retryCompletedDuringWarning = retryInitialization!.IsCompleted;
                }
                catch (Exception exception)
                {
                    retryFailure = exception;
                    retryCompletedDuringWarning = retryInitialization!.IsCompleted;
                }
            };

            TestUiDispatcherHost.Invoke(() =>
            {
                var composition = new ApplicationComposition(
                    settingsEditSession: settingsSession,
                    uiScheduler: new TestUiScheduler(() => TestUiDispatcherHost.Dispatcher),
                    applicationLifetime: TestApplicationContext.CreateLifetime(firstStartup: false),
                    cultureCatalog: TestApplicationContext.CreateCultureCatalog(),
                    runtimeOptions: MainWindowViewModelTestFactory.CreateRuntimeOptions(applicationPath),
                    fileDbMutationDialogService: dialogs);
                var library = new TestBmsLibrary(composition.DatabaseScope,
                    database.SongDbPath,
                    getLR2Config: null,
                    _lr2ScoreDB: null,
                    startupRequiredFileScanReason: null,
                    optionsSnapshotProvider: () => BmsLibraryOptionsSnapshot.CreateCurrent(settings),
                    settings: settings,
                    applicationPathSnapshot: TestBmsFactory.MissingEverythingBridge,
                    chartFileScanner: scanner,
                    operationAdmission: composition.OperationAdmission,
                    playlistOperationAdmission: composition.PlaylistOperationAdmission);
                TestBmsPlaylist playlist = MainWindowViewModelTestFactory.CreatePlaylist(composition.DatabaseScope, database.SongDbPath, settings, library: library);
                viewModel = new MainWindowViewModel(
                    composition,
                    new LateFailureStartupLibraryFactory(
                        library,
                        playlist,
                        () =>
                        {
                            if (!lateFailureInjected)
                            {
                                lateFailureInjected = true;
                                Directory.Move(rootB, unavailableRootB);
                            }
                        }));
                viewModel.SettingDialog.AttachPresentationPort(
                    new RecordingSettingsDialogPresentationPort(sequence.Add));
            });

            bool initialized = true;
            TestUiDispatcherHost.Invoke(() =>
            {
                Task<bool> initialization = viewModel!.InitializeAsync();
                TestUiDispatcherHost.AwaitTaskOnDispatcher(initialization, "late-directory-startup-failure");
                initialized = initialization.GetAwaiter().GetResult();
            });

            if (retryInitialization is { IsCompleted: false })
            {
                try
                {
                    TestUiDispatcherHost.Invoke(() =>
                    {
                        try
                        {
                            TestUiDispatcherHost.AwaitTaskOnDispatcher(
                                retryInitialization!,
                                "late-directory-startup-retry-drain");
                        }
                        catch (Exception exception)
                        {
                            retryDrainFailure = exception;
                        }
                    });
                }
                catch (Exception exception)
                {
                    retryDrainFailure = exception;
                }
            }

            Assert.IsFalse(initialized);
            Assert.IsNotNull(retryInitialization);
            Assert.IsTrue(warningObservedWithOperationReleased);
            Assert.IsTrue(warningObservedWithProgressUnblocked);
            Assert.IsTrue(retryCompletedDuringWarning);
            Assert.IsFalse(retryTimedOutDuringWarning);
            Assert.IsTrue(retrySucceededDuringWarning);
            Assert.IsNull(retryFailure);
            Assert.IsNull(retryDrainFailure);
            Assert.IsTrue(viewModel!.IsInitializationCompleted);
            Assert.IsTrue(viewModel!.HasActiveLibraryProfile);
            Assert.AreEqual(1, dialogs.MessageCount);
            CollectionAssert.AreEqual(new[] { "warning", "open" }, sequence);
            StringAssert.Contains(dialogs.LastMessageText, rootB);
            StringAssert.Contains(dialogs.LastMessageText, Resources.LibraryDirectoryPreflightBmsRootRole);
            Assert.IsTrue(Directory.Exists(rootB));
        }
        finally
        {
            if (viewModel != null)
            {
                TestUiDispatcherHost.Invoke(() =>
                {
                    if (retryInitialization is { IsCompleted: false })
                    {
                        try
                        {
                            TestUiDispatcherHost.AwaitTaskOnDispatcher(
                                retryInitialization!,
                                "late-directory-startup-retry-cleanup");
                        }
                        catch
                        {
                        }
                    }
                    TestUiDispatcherHost.AwaitTaskOnDispatcher(
                        viewModel!.ShellShutdownWorkflow.RequestWindowCloseAsync(),
                        "late-directory-startup-shutdown");
                    viewModel!.SettingDialog.Dispose();
                });
            }
            if (Directory.Exists(unavailableRootB) && !Directory.Exists(rootB))
            {
                Directory.Move(unavailableRootB, rootB);
            }
            Directory.Delete(root, recursive: true);
        }
    }

    /// <summary>出力先重複を注入通知先へ一回届け、通知成功・Failed・owner不在・Task faultのいずれも元の出力先とconfigを保ち、通知不能を元の入力拒否へ合成せず再通知しないことを確認します。</summary>
    [TestMethod]
    [DataRow("Accepted")]
    [DataRow("Failed")]
    [DataRow("OwnerUnavailable")]
    [DataRow("TaskFault")]
    public void CustomFolderOutputValidation_UsesInjectedDialogAndPreservesRejectedValue(string statusName)
    {
        string root = CreateTemporaryRoot();
        SettingsDialogViewModel? dialog = null;
        try
        {
            string musicRoot = Path.Combine(root, "Music");
            string normalOutput = Path.Combine(root, "NormalOutput");
            string rootOutput = Path.Combine(root, "RootOutput");
            foreach (string path in new[] { musicRoot, normalOutput, rootOutput })
            {
                Directory.CreateDirectory(path);
            }
            (string songDb, string configPath) = CreateValidLr2Layout(root);
            var config = new BeMusicSeeker.Models.LR2.LR2Config(configPath);
            config.AddBMSSearchDirectories([musicRoot]);
            config.Save(config.ConfigFilePath);
            Settings values = CreateValidStandaloneSettings(musicRoot);
            values.OperationModeLR2DB = true;
            values.LR2RootPath = root;
            values.LR2SongDBPath = songDb;
            values.LR2ConfigXmlPath = configPath;
            values.LR2CustomFolderOutputBaseDir = normalOutput;
            values.LR2CustomFolderOutputBaseDirRootType = rootOutput;
            values.LR2CustomFolderAdditionalOutputBaseDirs = "[]";
            var session = new CountingSettingsEditSession(values);
            var displayFailure = new IOException("設定検証ダイアログの表示失敗");
            var dialogs = new RecordingRootDialogService
            {
                MessageResult = statusName switch
                {
                    "Accepted" => UiDialogResult.FromMessageBoxResult(MessageBoxResult.OK),
                    "Failed" => UiDialogResult.Failed(displayFailure),
                    "OwnerUnavailable" => UiDialogResult.NotShown(UiDialogStatus.OwnerUnavailable),
                    "TaskFault" => UiDialogResult.FromMessageBoxResult(MessageBoxResult.OK),
                    _ => throw new ArgumentOutOfRangeException(nameof(statusName))
                }
            };
            if (statusName == "TaskFault") { dialogs.MessageObservedAsync = () => Task.FromException(displayFailure); }
            using var harness = SettingsDialogHarness.Create(values, settingsEditSession: session, dialogs: dialogs);
            dialog = harness.Dialog;
            byte[] savedXml = File.ReadAllBytes(configPath);

            dialog.LR2CustomFolderOutputDir = rootOutput;

            Assert.AreEqual(1, dialogs.MessageCount);
            Assert.AreEqual(0, dialogs.ConfirmationCount);
            StringAssert.Contains(dialogs.LastMessageText, Resources.Label_NormalOutputBase);
            StringAssert.Contains(dialogs.LastMessageText, Resources.Label_RootOutputBase);
            Assert.AreEqual(normalOutput, dialog.LR2CustomFolderOutputDir);
            Assert.AreEqual(rootOutput, dialog.LR2CustomFolderAsRootOutputDir);
            Assert.AreEqual(0, session.SaveCount);
            CollectionAssert.AreEqual(savedXml, File.ReadAllBytes(configPath));
        }
        finally
        {
            dialog?.Dispose();
            Directory.Delete(root, recursive: true);
        }
    }

    /// <summary>一般ページの実Table URLボタンで確定rootと別候補を識別し、破棄取消/実P拒否では保持、受理後は関係rootだけを戻して実通知終端まで同再操作を拒否します。通常Closeは同じTaskを保持して許可します。</summary>
    /// <remarks>実workspaceの既存URL入力を使ってHTTPを避け、DB/順序/BMTの詳細はPlaylist系列へ分担します。通知gateを実Taskより先に完了条件にしません。</remarks>
    [TestMethod]
    [DataRow("cancel")]
    [DataRow("busy")]
    [DataRow("accepted")]
    public void BeatorajaTableUrlButton_DeliversConfirmedRootToWorkspace(string outcome)
    {
        TestUiDispatcherHost.RunWindowTest(scope =>
        {
            using var fixture = new MainWindowViewModelStartupProgressTests.CompletionFixture(lifetime: TestApplicationContext.CreateLifetime(firstStartup: false));
            string root = fixture.Settings.BMSRootPath;
            string beatoraja = Directory.CreateDirectory(Path.Combine(root, "beatoraja")).FullName;
            string candidate = Directory.CreateDirectory(Path.Combine(root, "candidate-beatoraja")).FullName;
            foreach (string path in new[] { beatoraja, candidate }) { File.WriteAllText(Path.Combine(path, "beatoraja.jar"), string.Empty); }
            File.WriteAllText(Path.Combine(beatoraja, BeatorajaConfigService.ConfigFileName), "{\"tableURL\":[\"https://example.invalid/existing\"]}");
            File.WriteAllText(Path.Combine(candidate, BeatorajaConfigService.ConfigFileName), "{\"tableURL\":[\"https://example.invalid/candidate\"]}");
            fixture.OptionalRelease.TrySetResult();
            fixture.Settings.BeatorajaRootPath = beatoraja;
            Task<bool> initial = fixture.Start();
            TestUiDispatcherHost.AwaitTaskOnDispatcher(initial, "table-url-button-initialize");
            Assert.IsTrue(initial.GetAwaiter().GetResult());
            TestUiDispatcherHost.AwaitTaskOnDispatcher(fixture.WaitForBackgroundTerminalAsync(), "table-url-button-background");
            fixture.Playlist.BMSTables.Add(new BMSTable { playlist_id = 999, name = "Existing", Page_url = new Uri("https://example.invalid/existing") });
            fixture.Playlist.CommitBMSTableHeaderToDB(fixture.Playlist.BMSTables.Last());
            SettingsDialogViewModel settings = fixture.Owner.SettingDialog;
            fixture.Dialogs.ConfirmationResult = outcome == "cancel" ? MessageBoxResult.Cancel : MessageBoxResult.Yes;
            int confirmations = 0;
            fixture.Owner.PlaylistWorkspace.BeatorajaTableUrlImportConfirmationRequested += (_, request) => { confirmations++; StringAssert.Contains(request.Message, Resources.FilePath_beatoraja_root); request.Confirmed = outcome != "cancel"; };
            var summaryEntered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            using var release = new System.Threading.ManualResetEventSlim();
            BeatorajaTableUrlImportSummary? summary = null;
            fixture.Owner.PlaylistWorkspace.BeatorajaTableUrlImportSummaryReady += (_, request) =>
            {
                summary = request.Summary;
                summaryEntered.TrySetResult();
                release.Wait();
            };
            var notices = new List<string>();
            fixture.Owner.PlaylistWorkspace.BeatorajaTableUrlImportNotificationRequested += (_, request) => notices.Add(request.Message);
            var window = new SettingsWindow { DataContext = settings, PlaylistWorkspace = fixture.Owner.PlaylistWorkspace };
            IDisposable? held = null;
            Task? terminal = null;
            try
            {
                scope.ShowAndWaitForContentRendered(window);
                settings.BeatorajaRootPath = candidate;
                settings.EnableBeatorajaBmtOutput = !fixture.Settings.EnableBeatorajaBmtOutput;
                if (outcome == "busy") { Assert.IsTrue(fixture.Playlist.TryEnterPlaylistMutation(out held)); }
                Button button = FindDescendants<Button>(window).Single(button => System.Windows.Automation.AutomationProperties.GetAutomationId(button) == "SettingsImportBeatorajaTableUrls");
                button.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                terminal = settings.SettingsOperationCompletionTask;
                if (outcome == "accepted")
                {
                    TestUiDispatcherHost.AwaitTaskOnDispatcher(TestUiDispatcherHost.AwaitNotificationAsync(summaryEntered.Task, terminal, "table URL summary gate"), "table URL summary arrival");
                    Assert.AreEqual(beatoraja, settings.BeatorajaRootPath);
                    Assert.AreNotEqual(fixture.Settings.EnableBeatorajaBmtOutput, settings.EnableBeatorajaBmtOutput, "取込みと無関係な候補を保持します。");
                    Assert.IsTrue(settings.CanCancelBmtSettings, "取込みと無関係なBMT候補は同期取消できます。");
                    Button cancelBmt = FindDescendants<Button>(window).Single(candidateButton => Equals(candidateButton.Tag, "cancel:bmt"));
                    Assert.IsTrue(cancelBmt.IsEnabled);
                    cancelBmt.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                    Assert.AreEqual(fixture.Settings.EnableBeatorajaBmtOutput, settings.EnableBeatorajaBmtOutput);
                    Assert.IsFalse(settings.CanCancelBmtSettings);
                    Assert.AreSame(terminal, settings.SettingsOperationCompletionTask);
                    Assert.IsFalse(terminal.IsCompleted);
                    Assert.AreEqual(1, confirmations);
                    Assert.IsFalse(terminal.IsCompleted);
                    Assert.IsTrue(settings.IsEditCancellationEnabled, "通知未終端でも通常Closeは同じTaskを保持したまま許可します。");
                    button.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                    Assert.AreEqual(1, confirmations);
                    Assert.IsNotNull(summary);
                    Assert.AreEqual(1, summary.Outcomes.Count);
                    Assert.AreEqual(BeatorajaTableUrlImportOutcomeKind.Existing, summary.Outcomes[0].Kind);
                }
                release.Set();
                TestUiDispatcherHost.AwaitTaskOnDispatcher(terminal, "table URL actual terminal");
                Assert.AreEqual(1, confirmations);
                if (outcome != "accepted")
                {
                    Assert.AreEqual(candidate, settings.BeatorajaRootPath);
                    Assert.AreNotEqual(fixture.Settings.EnableBeatorajaBmtOutput, settings.EnableBeatorajaBmtOutput, "取込みと無関係な候補を保持します。");
                    Assert.IsTrue(settings.CanCancelBmtSettings, "取消・受付拒否の実終端後は候補操作を再開します。");
                    Assert.IsFalse(summaryEntered.Task.IsCompleted);
                }
                Assert.IsTrue(window.IsVisible);
                Assert.AreEqual(beatoraja, fixture.Settings.BeatorajaRootPath);
                held?.Dispose(); held = null;
                TestUiDispatcherHost.ProcessQueuedPresentation();
                Assert.AreEqual(1, confirmations);
            }
            finally
            {
                release.Set(); held?.Dispose();
                if (terminal != null) { TestUiDispatcherHost.AwaitTaskOnDispatcher(terminal, "table URL cleanup"); }
                window.CloseForOwnerShutdown();
            }
        });
    }

    /// <summary>スコア組の確定から同じL/Pを実ScoreOnlyモデルへ渡し、実親解放後の純通知と必須UIの実終端まで同じWindowの受理Taskを追跡します。</summary>
    /// <remarks>実compiled選択からWindowの受理Taskへ接続し、実設定・モデル・UI完了を確認します。純通知を保持点にせず既存UI gateで実Taskを待ちます。</remarks>
    [TestMethod]
    [DataRow(false)] // 同じroot内のplayer選択。
    [DataRow(true)] // root pickerの局所組変更。
    public void ScoreConfirmation_PublishesChangedRealScoreBeforeClosing(bool changeRoot)
    {
        string root = CreateTemporaryRoot();
        string music = Path.Combine(root, "music");
        string beatoraja = Path.Combine(root, "beatoraja");
        Directory.CreateDirectory(music);
        Directory.CreateDirectory(beatoraja);
        File.WriteAllText(Path.Combine(beatoraja, "beatoraja.jar"), string.Empty);
        File.WriteAllText(Path.Combine(beatoraja, BeatorajaConfigService.ConfigFileName), "{}");
        string hash = new('a', 64);
        string nextBeatoraja = Path.Combine(root, "next-beatoraja");
        Directory.CreateDirectory(nextBeatoraja);
        File.WriteAllText(Path.Combine(nextBeatoraja, "beatoraja.jar"), string.Empty);
        File.WriteAllText(Path.Combine(nextBeatoraja, BeatorajaConfigService.ConfigFileName), "{\"playername\":\"playerB\"}");
        foreach ((string playerRoot, string player, int score) in new[] { (beatoraja, "playerA", 10), (beatoraja, "playerB", 40), (nextBeatoraja, "playerB", 40) })
        {
            string playerDirectory = Path.Combine(playerRoot, "player", player);
            Directory.CreateDirectory(playerDirectory);
            using SQLiteConnectionEx db = databaseScope.OpenProviderConnection(Path.Combine(playerDirectory, "score.db"));
            db.Execute("CREATE TABLE score (sha256 TEXT, mode INTEGER, clear INTEGER, epg INTEGER, lpg INTEGER, egr INTEGER, lgr INTEGER, notes INTEGER, combo INTEGER, minbp INTEGER, playcount INTEGER, clearcount INTEGER)");
            db.Execute("INSERT INTO score VALUES (?, 0, 4, ?, 0, 0, 0, 100, 50, 5, 1, 1)", hash, score);
        }
        var published = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        try
        {
            TestUiDispatcherHost.RunWindowTest(scope =>
            {
                Settings values = CreateValidStandaloneSettings(music);
                values.UseBeatorajaScoreDb = true;
                values.BeatorajaRootPath = beatoraja;
                values.BeatorajaPlayerId = "playerA";
                values.BeatorajaScoreDbPath = Path.Combine(beatoraja, "player", "playerA", "score.db");
                var session = new CountingSettingsEditSession(values);
                var failures = new List<Exception>();
                var completionUi = new MainWindowViewModelStartupProgressTests.CompletionUiScheduler(new TestUiScheduler(() => TestUiDispatcherHost.Dispatcher));
                var composition = new ApplicationComposition(settingsEditSession: session,
                    reportSettingsApplyFailure: failures.Add,
                    uiScheduler: completionUi,
                    applicationLifetime: TestApplicationContext.CreateLifetime(firstStartup: false),
                    cultureCatalog: TestApplicationContext.CreateCultureCatalog(),
                    runtimeOptions: MainWindowViewModelTestFactory.CreateRuntimeOptions(TestBmsFactory.AvailableEverythingBridge),
                    rootFileEnumerator: new FastRootFileEnumerator());
                var factory = new StartupLibraryConstructionTestSupport.RecordingDelegatingStartupLibraryFactory(composition, [])
;
                var viewModel = new MainWindowViewModel(composition, factory);
                Task? parentLibraryTerminal = null;
                Task? parentPlaylistTerminal = null;
                var presentation = new RecordingSettingsDialogPresentationPort
                {
                    WaitForClose = () =>
                    {
                        parentLibraryTerminal = composition.OperationAdmission.WaitForIdleAsync();
                        parentPlaylistTerminal = composition.PlaylistOperationAdmission.WaitForIdleAsync();
                        return Task.CompletedTask;
                    }
                };
                viewModel.SettingDialog.AttachPresentationPort(presentation);
                Task? apply = null;
                SettingsWindow? window = null;
                BMSLibrary? library = null;
                var observations = new List<(bool AllFree, bool MinFree, bool LibraryFree, bool PlaylistFree, bool Ready, int Version, int RuntimeVersion, int Subscriptions, int? Score, bool Closed)>();
                PropertyChangedEventHandler observer = (_, args) =>
                {
                    if (args.PropertyName != nameof(BMSLibrary.ScoreSnapshotVersion)) { return; }
                    BMSLibrary actual = library ?? throw new InvalidOperationException("Score library was not attached.");
                    BMSLibrary.ScoreRuntimeState state = actual.GetScoreRuntimeStateForDiagnostics();
                    observations.Add((!actual.IsWriteLockHeldInitializeAll, !actual.IsWriteLockHeldInitializeMin,
                        !composition.OperationAdmission.IsActive, !composition.PlaylistOperationAdmission.IsActive,
                        state.SnapshotReady && actual.ScoreSnapshotReady, actual.ScoreSnapshotVersion, state.SnapshotVersion,
                        actual.ScoreSubscriptionCount, actual.ResolveChartScoreSnapshot(ChartFileKind.Bms, Path.Combine(music, "chart.bms"), string.Empty, hash).Score,
                        presentation.Requests.Contains("close")));
                    published.TrySetResult();
                };
                var scoreDelivered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
                EventHandler<NormalLibraryRefreshAppliedEventArgs> scoreDelivery = (_, args) =>
                {
                    if (args.NotificationBatch.ScoreChanges.Count > 0) { scoreDelivered.TrySetResult(); }
                };
                completionUi.IsCompletionBoundary = () => !composition.OperationAdmission.IsActive
                    && !composition.PlaylistOperationAdmission.IsActive;
                try
                {
                    Task<bool> initialize = viewModel.InitializeAsync();
                    TestUiDispatcherHost.AwaitTaskOnDispatcher(initialize, "score-only-real-initialize");
                    Assert.AreEqual(true, initialize.GetAwaiter().GetResult());
                    if (viewModel.IsLibraryOperationInProgress)
                    {
                        var idle = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
                        PropertyChangedEventHandler finished = (_, args) => { if (args.PropertyName == nameof(MainWindowViewModel.IsLibraryOperationInProgress) && !viewModel.IsLibraryOperationInProgress) { idle.TrySetResult(); } };
                        viewModel.PropertyChanged += finished;
                        try { TestUiDispatcherHost.AwaitTaskOnDispatcher(idle.Task, "placement-score-startup-actual-idle"); }
                        finally { viewModel.PropertyChanged -= finished; }
                    }
                    AwaitStartupBackgroundCompletionOnDispatcher(viewModel, "score-only-startup-background-terminal");
                    library = factory.CreatedLibrary ?? throw new AssertFailedException("Score library was not attached.");
                    Assert.AreEqual(20, library.ResolveChartScoreSnapshot(ChartFileKind.Bms, Path.Combine(music, "chart.bms"), string.Empty, hash).Score);
                    TestUiDispatcherHost.AwaitTaskOnDispatcher(viewModel.RegularChartList.DeliverPendingLibraryChangesAsync(library, CancellationToken.None), "score-only-real-initial-delivery");
                    library.PropertyChanged += observer;
                    viewModel.RegularChartList.NormalLibraryRefreshApplied += scoreDelivery;
                    var rootDialogs = new RecordingRootDialogService();
                    window = new SettingsWindow(rootDialogs) { DataContext = viewModel.SettingDialog, PlaylistWorkspace = viewModel.PlaylistWorkspace };
                    scope.ShowAndWaitForContentRendered(window);
                    ComboBox playerSelection = FindDescendants<ComboBox>(window).Single(combo =>
                        combo.GetBindingExpression(ComboBox.SelectedItemProperty)?.ParentBinding.Path?.Path == nameof(SettingsDialogViewModel.BeatorajaPlayerId));
                    completionUi.Arm();
                    int constructionCalls = factory.Calls.Count;
                    if (changeRoot)
                    {
                        rootDialogs.FolderResults.Enqueue(new UiFolderPickerResult(UiDialogStatus.Accepted, [nextBeatoraja]));
                        BeMusicSeeker.Views.Settings.SettingsPathPicker picker = FindDescendants<BeMusicSeeker.Views.Settings.SettingsPathPicker>(window)
                            .Single(item => item.GetBindingExpression(BeMusicSeeker.Views.Settings.SettingsPathPicker.PathProperty)?.ParentBinding.Path?.Path == nameof(SettingsDialogViewModel.BeatorajaRootPath));
                        FindDescendants<Button>(picker).Single().RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                    }
                    else { playerSelection.SetCurrentValue(ComboBox.SelectedItemProperty, "playerB"); }
                    apply = viewModel.SettingDialog.SettingsOperationCompletionTask;
                    TestUiDispatcherHost.AwaitTaskOnDispatcher(Task.WhenAny(completionUi.Entered.Task, apply), "score-only-real-publish-or-failure");
                    if (!completionUi.Entered.Task.IsCompleted) { TestUiDispatcherHost.AwaitTaskOnDispatcher(apply, "score-only-real-early-terminal"); Assert.Fail(string.Join(Environment.NewLine, failures)); }
                    Assert.IsFalse(apply.IsCompleted);
                    Assert.IsTrue(viewModel.SettingDialog.IsEditCancellationEnabled, "必須公開の実終端待ちと通常Closeを区別します。");
                    Assert.IsTrue(!composition.OperationAdmission.IsActive);
                    Assert.IsTrue(!composition.PlaylistOperationAdmission.IsActive);
                    Assert.IsTrue(published.Task.IsCompleted);
                    // 純通知の時点と、登録済みrangeを実UIへ届ける時点を混ぜず、同じownerの実配送終端を待ちます。
                    TestUiDispatcherHost.AwaitTaskOnDispatcher(viewModel.RegularChartList.DeliverPendingLibraryChangesAsync(library, CancellationToken.None), "score-only-real-score-delivery");
                    Assert.IsTrue(scoreDelivered.Task.IsCompleted);
                    Assert.IsTrue(observations.Count > 0);
                    foreach ((bool AllFree, bool MinFree, bool LibraryFree, bool PlaylistFree, bool Ready, int Version, int RuntimeVersion, int Subscriptions, int? Score, bool Closed) observation in observations)
                    {
                        Assert.IsTrue(observation.AllFree && observation.MinFree && observation.LibraryFree && observation.PlaylistFree, $"All={observation.AllFree}, Min={observation.MinFree}, L={observation.LibraryFree}, P={observation.PlaylistFree}, Ready={observation.Ready}, Version={observation.Version}");
                        Assert.IsTrue(observation.Ready && !observation.Closed);
                        Assert.IsTrue(observation.Version > 0);
                        Assert.AreEqual(observation.Version, observation.RuntimeVersion);
                        Assert.AreEqual(1, observation.Subscriptions);
                        Assert.AreEqual(80, observation.Score);
                    }
                    CollectionAssert.DoesNotContain(presentation.Requests, "close");
                    Assert.AreEqual(80, library.ResolveChartScoreSnapshot(ChartFileKind.Bms, Path.Combine(music, "chart.bms"), string.Empty, hash).Score,
                        "公開済みsnapshotの実値を読み、on-demandで未実行を補いません。");
                    completionUi.Release.TrySetResult();
                    TestUiDispatcherHost.AwaitTaskOnDispatcher(apply, "score-only-real-terminal");
                    Assert.AreEqual(0, failures.Count, string.Join(Environment.NewLine, failures));
                    Assert.AreEqual(constructionCalls, factory.Calls.Count, "root変更もライブラリ/playlistを再構築しません。");
                    Assert.AreEqual(changeRoot ? nextBeatoraja : beatoraja, values.BeatorajaRootPath);
                    Assert.AreEqual(Path.Combine(changeRoot ? nextBeatoraja : beatoraja, "player", "playerB", "score.db"), values.BeatorajaScoreDbPath);
                    CollectionAssert.DoesNotContain(presentation.Requests, "close");
                    Assert.IsTrue(!composition.OperationAdmission.IsActive);
                    Assert.IsTrue(!composition.PlaylistOperationAdmission.IsActive);

                }
                finally
                {
                    completionUi.Release.TrySetResult();
                    if (library != null) { library.PropertyChanged -= observer; }
                    viewModel.RegularChartList.NormalLibraryRefreshApplied -= scoreDelivery;
                    try { if (apply != null) { TestUiDispatcherHost.AwaitTaskOnDispatcher(apply, "score-only-real-cleanup-task"); } }
                    finally
                    {
                        TestUiDispatcherHost.AwaitTaskOnDispatcher(viewModel.ShellShutdownWorkflow.RequestWindowCloseAsync(), "score-only-real-cleanup");
                        if (window?.IsVisible == true) { window.CloseForOwnerShutdown(); }
                        viewModel.SettingDialog.Dispose();
                    }
                }
            });
        }
        finally { Directory.Delete(root, recursive: true); }
    }

    /// <summary>標準compositionのroot確定で旧rootのURL解除だけを失敗させ、新root採用・実SQLite score反映・必要UI終端と旧BMT保持を同じTaskで確認します。</summary>
    [TestMethod]
    public void BeatorajaRootConfirmation_UrlFailurePublishesRealNewRootScoreBeforeTerminal()
    {
        string root = CreateTemporaryRoot();
        Exception? primaryFailure = null;
        var cleanupFailures = new List<Exception>();
        void Cleanup(Action action) { try { action(); } catch (Exception failure) { cleanupFailures.Add(failure); } }
        try
        {
            string music = Directory.CreateDirectory(Path.Combine(root, "music")).FullName;
            string oldRoot = Directory.CreateDirectory(Path.Combine(root, "A")).FullName;
            string newRoot = Directory.CreateDirectory(Path.Combine(root, "B")).FullName;
            string hash = new('a', 64);
            foreach ((string playerRoot, int perfect) in new[] { (oldRoot, 10), (newRoot, 40) })
            {
                File.WriteAllText(Path.Combine(playerRoot, "beatoraja.jar"), string.Empty);
                File.WriteAllText(Path.Combine(playerRoot, BeatorajaConfigService.ConfigFileName), "{\"playername\":\"player\",\"tablepath\":\"table\",\"tableURL\":[\"https://example.invalid/user\"]}");
                string profile = Directory.CreateDirectory(Path.Combine(playerRoot, "player", "player")).FullName;
                using SQLiteConnectionEx db = databaseScope.OpenProviderConnection(Path.Combine(profile, "score.db"));
                db.Execute("CREATE TABLE score (sha256 TEXT, mode INTEGER, clear INTEGER, epg INTEGER, lpg INTEGER, egr INTEGER, lgr INTEGER, notes INTEGER, combo INTEGER, minbp INTEGER, playcount INTEGER, clearcount INTEGER)");
                db.Execute("INSERT INTO score VALUES (?, 0, 4, ?, 0, 0, 0, 100, 50, 5, 1, 1)", hash, perfect);
            }
            TestUiDispatcherHost.Invoke(() =>
            {
                string settingsPath = Path.Combine(root, "settings", "user.config");
                Settings values = CreateValidStandaloneSettings(music, TestSettingsFactory.OpenSettings(settingsPath));
                values.BeatorajaRootPath = oldRoot;
                values.BeatorajaPlayerId = "player";
                values.BeatorajaScoreDbPath = Path.Combine(oldRoot, "player", "player", "score.db");
                values.UseBeatorajaScoreDb = true;
                values.BeatorajaBmtTablePath = Path.Combine(oldRoot, "table");
                values.EnableBeatorajaBmtOutput = true;
                values.RegisterBeatorajaBmtUrls = true;
                values.Save();
                var failures = new List<Exception>();
                var dialogs = new RecordingRootDialogService();
                var completionUi = new MainWindowViewModelStartupProgressTests.CompletionUiScheduler(new TestUiScheduler(() => TestUiDispatcherHost.Dispatcher));
                var composition = new ApplicationComposition(settingsEditSession: new SettingsEditSession(values), reportSettingsApplyFailure: failures.Add,
                    uiScheduler: completionUi, applicationLifetime: TestApplicationContext.CreateLifetime(firstStartup: false),
                    cultureCatalog: TestApplicationContext.CreateCultureCatalog(), runtimeOptions: MainWindowViewModelTestFactory.CreateRuntimeOptions(TestBmsFactory.AvailableEverythingBridge),
                    rootFileEnumerator: new FastRootFileEnumerator(), fileDbMutationDialogService: dialogs, settingsDialogService: dialogs);
                var scoreDialogs = new BmsLibraryInitializationTestSupport.RecordingDialogService();
                var factory = new StartupLibraryConstructionTestSupport.RecordingDelegatingStartupLibraryFactory(composition, [])
                {
                    LibraryCreator = profile => new BMSLibrary(composition.DatabaseScope, new BmsDatabaseLoadOptions(true, true, true), profile.SongDbPath, profile.Lr2ConfigOwner, profile.Lr2ScoreDbPath,
                        null, scoreDialogs, profile.StartupRequiredFileScanReason, composition.BmsLibraryOptionsProvider, composition.UiScheduler,
                        composition.ApplicationPathSnapshot, composition.BmsRootRegistrations, rootFileEnumerator: new FastRootFileEnumerator(),
                        operationAdmission: composition.OperationAdmission, playlistOperationAdmission: composition.PlaylistOperationAdmission)
                };
                var owner = new MainWindowViewModel(composition, factory);
                Task? operation = null;
                try
                {
                    Task<bool> initialize = owner.InitializeAsync();
                    TestUiDispatcherHost.AwaitTaskOnDispatcher(initialize, "root-bmt-score-initialize");
                    Assert.IsTrue(initialize.GetAwaiter().GetResult());
                    AwaitStartupBackgroundCompletionOnDispatcher(owner, "root-bmt-score-background");
                    BMSLibrary library = factory.CreatedLibrary ?? throw new AssertFailedException("実libraryがありません。");
                    BMSPlaylist playlist = factory.CreatedPlaylist ?? throw new AssertFailedException("実playlistがありません。");
                    ChartScoreSnapshot Lookup() => library.ResolveChartScoreSnapshot(ChartFileKind.Bms, Path.Combine(music, "chart.bms"), string.Empty, hash);
                    Assert.AreEqual(20, Lookup().Score);
                    var table = new BMSTable
                    {
                        playlist_id = 1,
                        name = "Real root",
                        Folder_order = ["Folder"],
                        entries = [new BMSTableEntry { md5 = new string('b', 32), sha256 = hash, title = "Song", folder = "Folder" }]
                    };
                    playlist.BMSTables.Add(table);
                    playlist.CommitBMSTablesWithEntriesToDB([table]);
                    TestUiDispatcherHost.AwaitTaskOnDispatcher(playlist.BmtOutput.ExportAllAsync("root-bmt-initial"), "root-bmt-initial-output");
                    string oldFile = Path.Combine(values.BeatorajaBmtTablePath, BmtTableExportService.CreatePlaylistExportMetadata(table).FileName);
                    completionUi.IsCompletionBoundary = () => !composition.OperationAdmission.IsActive && !composition.PlaylistOperationAdmission.IsActive;
                    completionUi.Arm();
                    using (File.Open(Path.Combine(oldRoot, BeatorajaConfigService.ConfigFileName), FileMode.Open, FileAccess.Read, FileShare.ReadWrite))
                    {
                        operation = owner.SettingDialog.CommitBeatorajaRootSettingsAsync(newRoot);
                        TestUiDispatcherHost.AwaitTaskOnDispatcher(TestUiDispatcherHost.AwaitNotificationAsync(completionUi.Entered.Task, operation, "root-bmt-score-required-ui"), "root-bmt-score-ui-arrival");
                        Assert.IsFalse(operation.IsCompleted);
                        Assert.AreEqual(newRoot, TestSettingsFactory.OpenSettings(settingsPath).BeatorajaRootPath);
                        Assert.AreEqual(80, Lookup().Score);
                        Assert.IsTrue(File.Exists(oldFile));
                        CollectionAssert.Contains(BeatorajaConfigService.ReadTableUrls(newRoot).ToArray(), "bemusicseeker://playlist/1");
                        CollectionAssert.Contains(BeatorajaConfigService.ReadTableUrls(oldRoot).ToArray(), "bemusicseeker://playlist/1");
                        Assert.IsFalse(composition.OperationAdmission.IsActive);
                        Assert.IsFalse(composition.PlaylistOperationAdmission.IsActive);
                        completionUi.Release.TrySetResult();
                        TestUiDispatcherHost.AwaitTaskOnDispatcher(operation, "root-bmt-score-terminal");
                    }
                    Assert.AreEqual(1, failures.Count);
                    StringAssert.Contains(failures[0].Message, Resources.Settings_bmt_url_switch_failed);
                    Assert.AreEqual(80, Lookup().Score);
                    Assert.IsTrue(owner.SettingDialog.IsBeatorajaSelectionEnabled);
                }
                finally
                {
                    completionUi.Release.TrySetResult();
                    if (operation != null) { Cleanup(() => TestUiDispatcherHost.AwaitTaskOnDispatcher(operation, "root-bmt-score-operation-cleanup")); }
                    Cleanup(() => TestUiDispatcherHost.AwaitTaskOnDispatcher(owner.ShellShutdownWorkflow.RequestWindowCloseAsync(), "root-bmt-score-owner-cleanup"));
                    Cleanup(owner.SettingDialog.Dispose);
                }
            });
        }
        catch (Exception failure) { primaryFailure = failure; throw; }
        finally
        {
            Cleanup(() => Directory.Delete(root, recursive: true));
            if (cleanupFailures.Count > 0)
            {
                if (primaryFailure != null) { primaryFailure.Data["CleanupFailures"] = new AggregateException(cleanupFailures); }
                else { throw new AggregateException(cleanupFailures); }
            }
        }
    }

    /// <summary>実score DBのA成功→BのSELECT失敗→C成功を通常設定入口で処理し、確定rootのplayerpath、関連項目だけの保存と実BMT媒体・URLへの無影響を確認します。</summary>
    /// <remarks>標準compositionの実score通知・必要表示まで待ちます。Window接続とL/P解放後の必須UI待機は隣接するcompiled選択試験が分担します。</remarks>
    [TestMethod]
    public void ScoreConfirmation_ReadFailureAllowsAnotherPlayerWithoutChangingBmt()
    {
        string root = CreateTemporaryRoot();
        Exception? primaryFailure = null;
        var cleanupFailures = new List<Exception>();
        void Cleanup(Action action) { try { action(); } catch (Exception failure) { cleanupFailures.Add(failure); } }
        try
        {
            string music = Directory.CreateDirectory(Path.Combine(root, "music")).FullName;
            string beatoraja = Directory.CreateDirectory(Path.Combine(root, "beatoraja")).FullName;
            string draftRoot = Directory.CreateDirectory(Path.Combine(root, "draft-beatoraja")).FullName;
            string oldOutput = Directory.CreateDirectory(Path.Combine(beatoraja, "table")).FullName;
            string newOutput = Directory.CreateDirectory(Path.Combine(beatoraja, "other-table")).FullName;
            File.WriteAllText(Path.Combine(beatoraja, "beatoraja.jar"), string.Empty);
            File.WriteAllText(Path.Combine(draftRoot, "beatoraja.jar"), string.Empty);
            string config = Path.Combine(beatoraja, BeatorajaConfigService.ConfigFileName);
            File.WriteAllText(config, "{\"playername\":\"A\",\"playerpath\":\"profiles\",\"tablepath\":\"table\",\"tableURL\":[\"https://example.com/unmanaged\"]}");
            File.WriteAllText(Path.Combine(draftRoot, BeatorajaConfigService.ConfigFileName), "{}");
            string hash = new('a', 64);
            foreach ((string player, int perfect) in new[] { ("A", 10), ("B", 0), ("C", 40) })
            {
                string path = Path.Combine(Directory.CreateDirectory(Path.Combine(beatoraja, "profiles", player)).FullName, "score.db");
                using SQLiteConnectionEx db = databaseScope.OpenProviderConnection(path);
                if (player == "B") { db.Execute("CREATE TABLE unrelated (id INTEGER)"); }
                else
                {
                    db.Execute("CREATE TABLE score (sha256 TEXT, mode INTEGER, clear INTEGER, epg INTEGER, lpg INTEGER, egr INTEGER, lgr INTEGER, notes INTEGER, combo INTEGER, minbp INTEGER, playcount INTEGER, clearcount INTEGER)");
                    db.Execute("INSERT INTO score VALUES (?, 0, 4, ?, 0, 0, 0, 100, 50, 5, 1, 1)", hash, perfect);
                }
            }
            TestUiDispatcherHost.Invoke(() =>
            {
                string settingsPath = Path.Combine(root, "settings", "user.config");
                Settings values = CreateValidStandaloneSettings(music, TestSettingsFactory.OpenSettings(settingsPath));
                values.UseBeatorajaScoreDb = true;
                values.BeatorajaRootPath = beatoraja;
                values.BeatorajaPlayerId = "A";
                values.BeatorajaScoreDbPath = Path.Combine(beatoraja, "profiles", "A", "score.db");
                values.BeatorajaBmtTablePath = oldOutput;
                values.EnableBeatorajaBmtOutput = true;
                values.RegisterBeatorajaBmtUrls = true;
                values.BeatorajaBmtHashOutputMode = "Original";
                values.Save();
                var failures = new List<Exception>();
                var dialogs = new RecordingRootDialogService();
                var composition = new ApplicationComposition(settingsEditSession: new CountingSettingsEditSession(values) { PersistOwnedValues = true },
                    reportSettingsApplyFailure: failures.Add, uiScheduler: new TestUiScheduler(() => TestUiDispatcherHost.Dispatcher),
                    applicationLifetime: TestApplicationContext.CreateLifetime(firstStartup: false), cultureCatalog: TestApplicationContext.CreateCultureCatalog(),
                    runtimeOptions: MainWindowViewModelTestFactory.CreateRuntimeOptions(TestBmsFactory.AvailableEverythingBridge), rootFileEnumerator: new FastRootFileEnumerator(),
                    fileDbMutationDialogService: dialogs, settingsDialogService: dialogs);
                var scoreDialogs = new BmsLibraryInitializationTestSupport.RecordingDialogService();
                var factory = new StartupLibraryConstructionTestSupport.RecordingDelegatingStartupLibraryFactory(composition, [])
                {
                    // score読取り・投影は実モデルで行い、Windowの不要な警告終端だけを既存記録窓口へ接続します。
                    LibraryCreator = profile => new BMSLibrary(composition.DatabaseScope, new BmsDatabaseLoadOptions(true, true, true), profile.SongDbPath, profile.Lr2ConfigOwner, profile.Lr2ScoreDbPath,
                        null, scoreDialogs, profile.StartupRequiredFileScanReason, composition.BmsLibraryOptionsProvider,
                        composition.UiScheduler, composition.ApplicationPathSnapshot, composition.BmsRootRegistrations, rootFileEnumerator: new FastRootFileEnumerator(),
                        operationAdmission: composition.OperationAdmission, playlistOperationAdmission: composition.PlaylistOperationAdmission)
                };
                var owner = new MainWindowViewModel(composition, factory);
                Task? apply = null;
                try
                {
                    Task<bool> initialize = owner.InitializeAsync();
                    TestUiDispatcherHost.AwaitTaskOnDispatcher(initialize, "score-next-real-initialize");
                    Assert.IsTrue(initialize.GetAwaiter().GetResult());
                    AwaitStartupBackgroundCompletionOnDispatcher(owner, "score-next-startup-background");
                    BMSLibrary library = factory.CreatedLibrary ?? throw new AssertFailedException("Library was not attached.");
                    BMSPlaylist playlist = factory.CreatedPlaylist ?? throw new AssertFailedException("Playlist was not attached.");
                    ChartScoreSnapshot Lookup() => library.ResolveChartScoreSnapshot(ChartFileKind.Bms, Path.Combine(music, "chart.bms"), string.Empty, hash);
                    Assert.AreEqual(20, Lookup().Score);
                    var table = new BMSTable
                    {
                        playlist_id = 1,
                        name = "Score independent",
                        bmt_sort = 1,
                        last_update = new DateTime(2024, 1, 1),
                        Folder_order = ["Folder"],
                        entries = [new BMSTableEntry { title = "Song", md5 = new string('b', 32), sha256 = hash, folder = "Folder" }]
                    };
                    playlist.BMSTables.Add(table);
                    playlist.CommitBMSTablesWithEntriesToDB([table]);
                    TestUiDispatcherHost.AwaitTaskOnDispatcher(playlist.BmtOutput.ExportAllAsync("score-test-initial"), "score-next-initial-bmt");
                    Assert.IsTrue(File.Exists(Path.Combine(oldOutput, BmtTableExportService.ManifestFileName)));
                    File.WriteAllText(Path.Combine(oldOutput, "user.txt"), "old unmanaged");
                    File.WriteAllText(Path.Combine(newOutput, "user.txt"), "new unmanaged");
                    var externalConfig = Newtonsoft.Json.Linq.JObject.Parse(File.ReadAllText(config));
                    externalConfig["tablepath"] = "other-table";
                    File.WriteAllText(config, externalConfig.ToString());
                    var files = Directory.GetFiles(oldOutput).Concat(Directory.GetFiles(newOutput)).ToDictionary(path => path, File.ReadAllBytes);
                    byte[] configBefore = File.ReadAllBytes(config);
                    string[] urls = BeatorajaConfigService.ReadTableUrls(beatoraja).ToArray();
                    int messagesBefore = scoreDialogs.Calls.Count;
                    SettingsDialogViewModel dialog = owner.SettingDialog;
                    dialog.BeatorajaRootPath = draftRoot;
                    dialog.BeatorajaBmtHashOutputMode = "PreferSha256Only";
                    bool savedKeep = values.KeepBeatorajaBmtFilesWhenOutputDisabled;
                    dialog.KeepBeatorajaBmtFilesWhenOutputDisabled = !savedKeep;
                    values.Commit((nameof(Settings.ShowNewPackageInstallConfirmMsg), !values.ShowNewPackageInstallConfirmMsg));
                    bool independentValue = values.ShowNewPackageInstallConfirmMsg;
                    var row = LibraryChartRow.FromChartFile(new ChartFile(ChartFileKind.Bms, Path.Combine(music, "chart.bms"), string.Empty, hash,
                        "Title", "Title", "Artist", "Genre", "Folder", string.Empty, string.Empty, null, null, null));
                    row.SetScoreProjectionProvider(chart => library.ResolveChartScoreSnapshot(chart.Kind, chart.Path, chart.Md5, chart.Sha256), () => library.ScoreSnapshotVersion);
                    foreach (string player in new[] { "B", "C" })
                    {
                        dialog.BeatorajaPlayerId = player;
                        apply = dialog.CommitScoreSettingsAsync();
                        TestUiDispatcherHost.AwaitTaskOnDispatcher(apply, "score-next-" + player);
                        TestUiDispatcherHost.AwaitTaskOnDispatcher(owner.RegularChartList.DeliverPendingLibraryChangesAsync(library, CancellationToken.None), "score-next-delivery-" + player);
                        Settings fresh = TestSettingsFactory.OpenSettings(settingsPath);
                        Assert.AreEqual(player, fresh.BeatorajaPlayerId);
                        Assert.AreEqual(Path.Combine(beatoraja, "profiles", player, "score.db"), fresh.BeatorajaScoreDbPath);
                        Assert.IsTrue(fresh.UseBeatorajaScoreDb);
                        Assert.AreEqual(beatoraja, fresh.BeatorajaRootPath);
                        Assert.AreEqual(oldOutput, fresh.BeatorajaBmtTablePath);
                        Assert.AreEqual("Original", fresh.BeatorajaBmtHashOutputMode);
                        Assert.AreEqual(savedKeep, fresh.KeepBeatorajaBmtFilesWhenOutputDisabled);
                        Assert.AreEqual(independentValue, fresh.ShowNewPackageInstallConfirmMsg);
                        Assert.AreEqual(draftRoot, dialog.BeatorajaRootPath);
                        Assert.AreEqual("PreferSha256Only", dialog.BeatorajaBmtHashOutputMode);
                        Assert.AreEqual(!savedKeep, dialog.KeepBeatorajaBmtFilesWhenOutputDisabled);
                        Assert.AreEqual(player == "B" ? ScoreTableLoadStatus.Failed : ScoreTableLoadStatus.Loaded, library.GetScoreSnapshotForDiagnostics().LoadStatus);
                        Assert.AreEqual(player == "B" ? null : (int?)80, Lookup().Score);
                        Assert.AreEqual(player == "B" ? null : (int?)80, row.Chart.Score.Score);
                        Assert.AreEqual(messagesBefore + 1, scoreDialogs.Calls.Count);
                        Assert.AreEqual(Resources.Error_RecommendationScoreReadFailed, scoreDialogs.Calls.Last().Message);
                        CollectionAssert.AreEquivalent(files.Keys.ToArray(), Directory.GetFiles(oldOutput).Concat(Directory.GetFiles(newOutput)).ToArray());
                        foreach ((string path, byte[] bytes) in files) { CollectionAssert.AreEqual(bytes, File.ReadAllBytes(path)); }
                        CollectionAssert.AreEqual(configBefore, File.ReadAllBytes(config));
                        CollectionAssert.AreEqual(urls, BeatorajaConfigService.ReadTableUrls(beatoraja).ToArray());
                    }
                    Assert.AreEqual(0, dialogs.ConfirmationCount);
                    Assert.AreEqual(0, failures.Count, string.Join(Environment.NewLine, failures));
                }
                finally
                {
                    if (apply != null) { Cleanup(() => TestUiDispatcherHost.AwaitTaskOnDispatcher(apply, "score-next-operation-cleanup")); }
                    Cleanup(() => TestUiDispatcherHost.AwaitTaskOnDispatcher(owner.ShellShutdownWorkflow.RequestWindowCloseAsync(), "score-next-owner-cleanup"));
                    Cleanup(owner.SettingDialog.Dispose);
                }
            });
        }
        catch (Exception failure) { primaryFailure = failure; throw; }
        finally
        {
            Cleanup(() => Directory.Delete(root, recursive: true));
            if (cleanupFailures.Count > 0)
            {
                var cleanupFailure = new AggregateException("score confirmation cleanup", cleanupFailures);
                if (primaryFailure == null) { throw cleanupFailure; }
                primaryFailure.Data["ScoreConfirmationCleanupFailure"] = cleanupFailure.ToString();
            }
        }
    }

    /// <summary>実開始buttonの連打は一回だけ受理し、必須UI待ちで親権限の解放と同WindowのClose抑止を確認します。UI失敗は同Windowで修復再開始でき、成功後は別に任意背景の実終端とL/P解放を待ちます。</summary>
    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public void InitialEnvironmentStart_CompletesRealLr2InitializationWithoutReacquiringAdmission(bool uiFails)
    {
        string root = CreateTemporaryRoot();
        try
        {
            Settings values = CreateValidCustomFolderSettings(root, []);
            var session = new CountingSettingsEditSession(values);
            var failures = new List<Exception>();
            var dialogs = new RecordingRootDialogService();
            TestUiDispatcherHost.RunWindowTest(scope =>
            {
                var completionUi = new MainWindowViewModelStartupProgressTests.CompletionUiScheduler(new TestUiScheduler(() => TestUiDispatcherHost.Dispatcher));
                var composition = new ApplicationComposition(
                    settingsEditSession: session,
                    reportSettingsApplyFailure: failures.Add,
                    uiScheduler: completionUi,
                    applicationLifetime: TestApplicationContext.CreateLifetime(firstStartup: false),
                    cultureCatalog: TestApplicationContext.CreateCultureCatalog(),
                    runtimeOptions: MainWindowViewModelTestFactory.CreateRuntimeOptions(TestBmsFactory.MissingEverythingBridge),
                    rootFileEnumerator: new FastRootFileEnumerator(),
                    settingsDialogService: dialogs, fileDbMutationDialogService: dialogs);
                MainWindowViewModel viewModel = composition.CreateMainWindowViewModelForTest();
                var window = new SettingsWindow { DataContext = viewModel.SettingDialog };
                viewModel.SettingDialog.AttachPresentationPort(new WindowClosingPresentationPort { CurrentWindow = window });
                scope.ShowAndWaitForContentRendered(window);
                var start = (Button)window.FindName("buttonOK");
                bool closed = false;
                window.Closed += (_, _) => closed = true;
                completionUi.IsCompletionBoundary = () => !composition.OperationAdmission.IsActive && !composition.PlaylistOperationAdmission.IsActive
                    && viewModel.ProgressHub.StartupProgress.IsStartupUiInteractionBlocked && !viewModel.IsInitializationCompleted;
                Task? apply = null;
                try
                {
                    viewModel.SettingDialog.ShowRecommUpdatedMsg = !viewModel.SettingDialog.ShowRecommUpdatedMsg;
                    completionUi.Arm();
                    Assert.IsTrue(start.IsEnabled);
                    start.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                    start.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                    apply = viewModel.SettingDialog.SettingsOperationCompletionTask;
                    TestUiDispatcherHost.AwaitTaskOnDispatcher(Task.WhenAny(completionUi.Entered.Task, apply), "initial-lr2-ui-or-terminal");
                    if (!completionUi.Entered.Task.IsCompleted)
                    {
                        viewModel.SettingDialog.CheckValidationForInitialization(out string initialError);
                        Assert.Fail("初期化がUI公開前に終端: " + initialError + " / " + dialogs.LastMessageText + " / " + string.Join(Environment.NewLine, failures));
                    }
                    TestUiDispatcherHost.AwaitTaskOnDispatcher(TestUiDispatcherHost.AwaitNotificationAsync(completionUi.Entered.Task, apply, "initial-lr2-required-ui"), "initial-lr2-required-ui");
                    Assert.IsFalse(composition.OperationAdmission.IsActive);
                    Assert.IsFalse(composition.PlaylistOperationAdmission.IsActive);
                    Assert.IsFalse(viewModel.IsInitializationCompleted);
                    Assert.IsTrue(viewModel.ProgressHub.StartupProgress.IsStartupUiInteractionBlocked);
                    TestUiDispatcherHost.ProcessQueuedPresentation();
                    Assert.IsFalse(start.IsEnabled);
                    window.Close();
                    Assert.IsTrue(window.IsVisible);
                    Assert.IsFalse(closed);
                    var uiFailure = new IOException("controlled initial settings UI failure");
                    if (uiFails) { completionUi.Release.TrySetException(uiFailure); }
                    else { completionUi.Release.TrySetResult(); }
                    TestUiDispatcherHost.AwaitTaskOnDispatcher(apply, "initial-lr2-settings-real-initialization");
                    if (uiFails)
                    {
                        Assert.AreEqual(1, failures.Count);
                        Assert.AreSame(uiFailure, failures[0]);
                        Assert.IsTrue(viewModel.ProgressHub.StartupProgress.IsFailed);
                        Assert.IsTrue(viewModel.ProgressHub.StartupProgress.IsRetryableFailure);
                        Assert.IsFalse(viewModel.ProgressHub.StartupProgress.IsStartupUiInteractionBlocked);
                        Assert.IsFalse(viewModel.IsInitializationCompleted);
                        Assert.IsFalse(viewModel.HasActiveLibraryProfile);
                        Assert.IsTrue(window.IsVisible);
                        Assert.AreSame(viewModel.SettingDialog, window.DataContext);
                        TestUiDispatcherHost.ProcessQueuedPresentation();
                        Assert.IsTrue(start.IsEnabled);
                        completionUi.Arm();
                        start.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                        apply = viewModel.SettingDialog.SettingsOperationCompletionTask;
                        TestUiDispatcherHost.AwaitTaskOnDispatcher(TestUiDispatcherHost.AwaitNotificationAsync(completionUi.Entered.Task, apply, "initial-repaired-required-ui"), "initial-repaired-required-ui");
                        completionUi.Release.TrySetResult();
                        TestUiDispatcherHost.AwaitTaskOnDispatcher(apply, "initial-repaired-terminal");
                    }
                    Assert.AreEqual(uiFails ? 1 : 0, failures.Count, string.Join(Environment.NewLine, failures));
                    Assert.IsTrue(closed);
                    Assert.AreEqual(SettingsWindowCloseReason.InitializationCompleted, window.CloseReason);
                    Assert.IsTrue(viewModel.HasActiveLibraryProfile);
                    Assert.IsTrue(viewModel.IsInitializationCompleted);
                    Assert.AreEqual(0, dialogs.MessageCount, "受理済み初期化を自己Busyとして拒否しません。");
                    // 必須初期化の完了後も、登録済み保守はL/Pを新たに取得できます。現在のleaseだけでなく実仕事の終端を先に待ちます。
                    AwaitStartupBackgroundCompletionOnDispatcher(viewModel, "initial-lr2-settings-startup-background-terminal");
                    TestUiDispatcherHost.AwaitTaskOnDispatcher(composition.OperationAdmission.WaitForIdleAsync(), "initial-lr2-settings-library-terminal");
                    TestUiDispatcherHost.AwaitTaskOnDispatcher(composition.PlaylistOperationAdmission.WaitForIdleAsync(), "initial-lr2-settings-playlist-terminal");
                    Assert.IsFalse(composition.OperationAdmission.IsActive);
                    Assert.IsFalse(composition.PlaylistOperationAdmission.IsActive);
                    using LR2SongDBExtended db = new BmsLibraryDbGateway(composition.DatabaseScope, new BmsDatabaseLoadOptions(true, true, true), values.LR2SongDBPath).OpenSongDbReadOnly();
                    Assert.IsTrue(db.Table<LR2SongDB.song>().Any() || !values.ScanBmsFilesOnStartup,
                        "同権限の実初期化と背景の実終端を確認し、登録だけからwhole Completedを強制しません。");

                }
                finally
                {
                    completionUi.Release.TrySetResult();
                    if (apply != null) { TestUiDispatcherHost.AwaitTaskOnDispatcher(apply, "initial-lr2-apply-cleanup"); }
                    TestUiDispatcherHost.AwaitTaskOnDispatcher(viewModel.ShellShutdownWorkflow.RequestWindowCloseAsync(),
                        "initial-lr2-settings-real-cleanup");
                    if (window.IsVisible) { window.CloseForOwnerShutdown(); }
                    viewModel.SettingDialog.Dispose();
                }
                Assert.IsFalse(composition.OperationAdmission.IsActive, "全背景Taskとcleanupの実終端後に受付を解放します。");
                Assert.IsFalse(composition.PlaylistOperationAdmission.IsActive, "全背景Taskとcleanupの実終端後にPも解放します。");
            });
        }
        finally { Directory.Delete(root, recursive: true); }
    }

    /// <summary>通常・追加・ROOTの先の選択を実保存し、同一・親・子関係の次の選択を最新確定配置に対して拒みます。どちらの操作順でも保存済み配置と候補一覧を保持します。</summary>
    [TestMethod]
    [DataRow("normal", "additional")]
    [DataRow("additional", "normal")]
    [DataRow("normal", "root")]
    [DataRow("root", "normal")]
    [DataRow("additional", "root")]
    [DataRow("root", "additional")]
    [DataRow("additional", "additional")]
    public void CustomFolderOutputChoices_RejectOverlappingBasesInEitherEntryOrder(string firstRole, string secondRole)
    {
        foreach (string relation in new[] { "same", "parent", "child" })
        {
            string root = CreateTemporaryRoot();
            SettingsDialogViewModel? dialog = null;
            try
            {
                string firstOutput = Path.Combine(root, "Separate", "Output");
                string secondOutput = relation switch
                {
                    "same" => firstOutput,
                    "parent" => Path.GetDirectoryName(firstOutput)!,
                    _ => Path.Combine(firstOutput, "Child")
                };
                Directory.CreateDirectory(firstOutput);
                Directory.CreateDirectory(secondOutput);
                Settings values = CreateValidCustomFolderSettings(root, []);
                var session = new CountingSettingsEditSession(values);
                var dialogs = new RecordingRootDialogService();
                MainWindowViewModel owner = CreateViewModel(session, firstStartup: false, dialogs: dialogs);
                dialog = owner.SettingDialog;
                SetCustomFolderOutput(dialog, firstRole, firstOutput);
                dialog.CommitOutputSettingsAsync(firstRole).GetAwaiter().GetResult();
                Assert.AreEqual(0, dialogs.MessageCount, dialogs.LastMessageText);
                string normalBefore = dialog.LR2CustomFolderOutputDir;
                string rootBefore = dialog.LR2CustomFolderAsRootOutputDir;
                string[] additionalBefore = dialog.CustomFolderAdditionalOutputBaseDirList.ToArray();

                SetCustomFolderOutput(dialog, secondRole, secondOutput);

                Assert.AreEqual(1, dialogs.MessageCount, relation);
                Assert.AreEqual(0, dialogs.ConfirmationCount);
                Assert.AreEqual(normalBefore, dialog.LR2CustomFolderOutputDir);
                Assert.AreEqual(rootBefore, dialog.LR2CustomFolderAsRootOutputDir);
                CollectionAssert.AreEqual(additionalBefore, dialog.CustomFolderAdditionalOutputBaseDirList.ToArray());
            }
            finally
            {
                dialog?.Dispose();
                Directory.Delete(root, recursive: true);
            }
        }
    }

    /// <summary>作業スレッドの実候補読取と画面結果の分類を非WPFで確認し、終了フラグだけで元の画面障害を黙殺しないことを保証します。</summary>
    [TestMethod]
    // 0:承認後終了、1:AppClosing、2:元例外付きFailed、3:owner不在、4:通知境界から元例外。
    [DataRow(0)]
    [DataRow(1)]
    [DataRow(2)]
    [DataRow(3)]
    [DataRow(4)]
    public async Task LeapYearPreparation_ShutdownDistinguishesClosingAndPreservesActualDialogFailure(int resultKind)
    {
        string root = CreateTemporaryRoot();
        try
        {
            Settings settings = CreateValidCustomFolderSettings(root, []);
            string folder = Path.Combine(root, "LeapCandidate");
            Directory.CreateDirectory(folder);
            DateTime sentinel = new(2024, 2, 29, 12, 0, 0);
            Directory.SetLastWriteTime(folder, sentinel);
            using (LR2SongDBExtended db = databaseScope.OpenSongWrite(settings.LR2SongDBPath))
            {
                db.CreateTable<LR2SongDB.folder>();
                db.Insert(new LR2SongDB.folder { path = folder + Path.DirectorySeparatorChar, type = 1, date = null, adddate = 0 });
            }
            var original = new IOException("actual confirmation failure");
            bool shutdown = false;
            var dialogs = new RecordingRootDialogService
            {
                ConfirmationResult = resultKind switch
                {
                    1 => UiDialogResult.NotShown(UiDialogStatus.AppClosing),
                    2 => UiDialogResult.Failed(original),
                    3 => UiDialogResult.NotShown(UiDialogStatus.OwnerUnavailable),
                    _ => UiDialogResult.FromMessageBoxResult(MessageBoxResult.Yes)
                },
                ConfirmationObserved = _ =>
                {
                    shutdown = true;
                    if (resultKind == 4) { throw original; }
                }
            };
            using var gate = new SemaphoreSlim(1, 1);
            var owner = new StartupLibraryInitializationWorkflowOwner(databaseScope, new BmsDatabaseLoadOptions(true, true, true), gate);
            Task<LeapYearFolderRepairApproval> operation = owner.PrepareLeapYearFolderRepairAsync(settings.LR2SongDBPath, dialogs, () => shutdown);
            if (resultKind is 0 or 1) { await Assert.ThrowsExactlyAsync<OperationCanceledException>(() => operation); }
            else if (resultKind == 4) { Assert.AreSame(original, await Assert.ThrowsExactlyAsync<IOException>(() => operation)); }
            else
            {
                InvalidOperationException failure = await Assert.ThrowsExactlyAsync<InvalidOperationException>(() => operation);
                if (resultKind == 2) { Assert.AreSame(original, failure.InnerException); }
                else { StringAssert.Contains(failure.Message, nameof(UiDialogStatus.OwnerUnavailable)); }
            }
            Assert.AreEqual(1, dialogs.ConfirmationCount);
            Assert.AreEqual(0, dialogs.MessageCount);
            Assert.AreEqual(sentinel, Directory.GetLastWriteTime(folder));
            using LR2SongDBExtended verify = new BmsLibraryDbGateway(databaseScope, new BmsDatabaseLoadOptions(true, true, true), settings.LR2SongDBPath).OpenSongDbReadOnly();
            LR2SongDB.folder row = verify.Table<LR2SongDB.folder>().Single();
            Assert.AreEqual(0, row.adddate);
            Assert.IsNull(row.date);
        }
        finally { Directory.Delete(root, recursive: true); }
    }

    /// <summary>修復後の通知は表示失敗を局所処理し、終了要求がある場合だけ取消しを伝播します。通知結果だけで終了したことにはしません。</summary>
    [TestMethod]
    // warningは修復警告/修復失敗を区別。0:AppClosing、1:Failed、2:owner不在、3:null、4:元例外throw、5:OK、6:終了要求のないAppClosing。
    [DataRow(true, 0, true)]
    [DataRow(false, 0, true)]
    [DataRow(true, 1, false)]
    [DataRow(true, 1, true)]
    [DataRow(false, 2, false)]
    [DataRow(false, 2, true)]
    [DataRow(true, 3, false)]
    [DataRow(true, 3, true)]
    [DataRow(false, 4, false)]
    [DataRow(false, 4, true)]
    [DataRow(true, 5, true)]
    [DataRow(true, 6, false)]
    public async Task LeapYearNotification_OnlyShutdownCancelsAndDisplayFailuresStayLocal(bool warning, int resultKind, bool shutdownAfterDisplay)
    {
        var original = new IOException("actual repair notification failure");
        bool shutdown = false;
        var dialogs = new RecordingRootDialogService
        {
            MessageResult = resultKind switch
            {
                0 or 6 => UiDialogResult.NotShown(UiDialogStatus.AppClosing),
                1 => UiDialogResult.Failed(original),
                2 => UiDialogResult.NotShown(UiDialogStatus.OwnerUnavailable),
                3 => null,
                _ => UiDialogResult.FromMessageBoxResult(MessageBoxResult.OK)
            },
            MessageObserved = () =>
            {
                shutdown = shutdownAfterDisplay;
                if (resultKind == 4) { throw original; }
            }
        };
        using var gate = new SemaphoreSlim(1, 1);
        var owner = new StartupLibraryInitializationWorkflowOwner(databaseScope, new BmsDatabaseLoadOptions(true, true, true), gate);
        var notification = new LeapYearFolderRepairNotification(warning, warning ? [] : [("original folder", new IOException("timestamp repair failed"))]);
        Task operation = owner.PresentLeapYearFolderRepairAsync(notification, dialogs, () => shutdown);
        if (shutdownAfterDisplay) { await Assert.ThrowsExactlyAsync<OperationCanceledException>(() => operation); }
        else { await operation; }
        Assert.AreEqual(1, dialogs.MessageCount);
        Assert.AreEqual(0, dialogs.ConfirmationCount);
    }

    /// <summary>各実入口で確認中の終了は副作用を始めず、成功公開後の修復通知中の終了は既公開成功を保持し、新しい案内・後続・設定再表示を止めます。</summary>
    [TestMethod]
    // entry 0:初回の明示開始、2:通常startup、3:tree再初期化。二つのboolは確認中/通知中の終了を区別します。
    [DataRow(0, false, false)]
    [DataRow(2, false, false)]
    [DataRow(3, false, false)]
    [DataRow(0, true, false)]
    [DataRow(2, true, false)]
    [DataRow(3, true, false)]
    [DataRow(0, false, true)]
    [DataRow(2, false, true)]
    [DataRow(3, false, true)]
    public void LeapYearRepair_ActualInitializationEntriesConfirmBeforeBothAdmissionsAndNotifyAfterTermination(int entry, bool shutdownDuringConfirmation, bool shutdownDuringNotification)
    {
        string root = CreateTemporaryRoot();
        MainWindowViewModel? owner = null;
        BMSLibrary? library = null;
        BMSPlaylist? playlist = null;
        IDisposable? competing = null;
        try
        {
            Settings values = CreateValidCustomFolderSettings(root, []);
            string targetDb = values.LR2SongDBPath;
            string folder = Path.Combine(root, "LeapCandidate");
            Directory.CreateDirectory(folder);
            DateTime sentinel = new(2024, 2, 29, 12, 0, 0);
            Directory.SetLastWriteTime(folder, sentinel);
            new BmsLibraryDbGateway(databaseScope, new BmsDatabaseLoadOptions(true, true, true), targetDb).EnsureAppOwnedSchema();
            using (LR2SongDBExtended db = databaseScope.OpenSongWrite(targetDb))
            {
                db.CreateTable<LR2SongDB.song>();
                db.CreateTable<LR2SongDB.folder>();
                db.Insert(new LR2SongDB.folder { path = folder + Path.DirectorySeparatorChar, type = 1, date = null, adddate = 0, title = "saved identity", info_a = "retained" });
                Lr2SongDbSyncStatusService.MarkCompleted(db, "fixture-complete", 0, DateTime.UtcNow);
            }
            var settingsSession = new CountingSettingsEditSession(values);
            var dialogs = new RecordingRootDialogService { ConfirmationResult = UiDialogResult.FromMessageBoxResult(MessageBoxResult.Yes) };
            var mutation = new BmsLibraryInitializationTestSupport.RecordingFileMutationService();
            bool changed = false;
            bool warning = false;
            // 通知先の失敗が局所化されても検証失敗を隠さないよう、callbackでは記録し、実Taskの待機元で表明する。
            List<(bool LibraryActive, bool PlaylistActive, bool Changed, int TimestampCallCount, bool ReentryAccepted)> warningObservations = [];
            int beforeAcceptedConfirmations = 0;
            TestUiDispatcherHost.Invoke(() =>
            {
                var scheduler = new TestUiScheduler(() => TestUiDispatcherHost.Dispatcher);
                IApplicationLifetimePort lifetime = TestApplicationContext.CreateLifetime(firstStartup: shutdownDuringConfirmation || shutdownDuringNotification);
                var composition = new ApplicationComposition(runtimeOptions: MainWindowViewModelTestFactory.CreateRuntimeOptions(), settingsEditSession: settingsSession, uiScheduler: scheduler,
                    applicationLifetime: lifetime, cultureCatalog: TestApplicationContext.CreateCultureCatalog(),
                    settingsDialogService: dialogs, fileDbMutationDialogService: dialogs, defaultBmsPlayerFactory: () => new RecordingPlaybackPlayer());
                var config = new LR2Config(values.LR2ConfigXmlPath);
                Lr2ConfigOwner configOwner = composition.ResolveLr2Config(values.LR2ConfigXmlPath);
                var actualLibrary = new TestBmsLibrary(composition.DatabaseScope, targetDb, configOwner, null, mutation, null, scheduler,
                    () => BmsLibraryOptionsSnapshot.CreateCurrent(values), chartFileScanner: CapturedChartFileScanner.FromFixture([],
                    new Dictionary<string, IEnumerable<string>>(StringComparer.OrdinalIgnoreCase) { [Path.Combine(root, "Songs")] = [] }, [Path.Combine(root, "Songs")]),
                    operationAdmission: composition.OperationAdmission,
                    playlistOperationAdmission: composition.PlaylistOperationAdmission, settings: values, rootFileEnumerator: new FastRootFileEnumerator());
                library = actualLibrary;
                playlist = MainWindowViewModelTestFactory.CreatePlaylist(composition.DatabaseScope, targetDb, values, configOwner, library: actualLibrary);
                var activeOwner = new MainWindowViewModel(composition, new LateFailureStartupLibraryFactory(actualLibrary, playlist));
                owner = activeOwner;
                var presentation = new RecordingSettingsDialogPresentationPort();
                activeOwner.SettingDialog.AttachPresentationPort(presentation);
                if (entry == 3)
                {
                    SetPrivateField(activeOwner, "initializationCompleted", true);
                    SetPrivateField(activeOwner, "hasActiveLibraryProfile", true);
                }
                if (entry == 0) { activeOwner.SettingDialog.OverwritePlaylistUrlsWithCompletion = !activeOwner.SettingDialog.OverwritePlaylistUrlsWithCompletion; }
                if (entry == 3)
                {
                    // 既存の実接続を使い、任意保守は停止中のschedulerへ登録する。モデル単独fallbackへ迂回しない。
                    var applicationPort = (IStartupLibraryApplicationPort)activeOwner;
                    applicationPort.AttachStartupLibrary(actualLibrary);
                    applicationPort.AttachStartupServices(new StartupLibraryServices(new LibraryProfile(true, targetDb,
                        [Path.Combine(root, "Songs")], configOwner, null, true, true, false, false, "leap-reinitialize-fixture"), actualLibrary, playlist));
                }
                dialogs.ConfirmationObserved = request =>
                {
                    StringAssert.Contains(request.MessageBoxText, folder);
                    Assert.AreEqual(MessageBoxResult.No, request.DefaultResult);
                    Assert.IsFalse(composition.OperationAdmission.IsActive);
                    Assert.IsFalse(composition.PlaylistOperationAdmission.IsActive);
                    Assert.AreEqual(0, settingsSession.SaveCount);
                    Assert.AreEqual(sentinel, Directory.GetLastWriteTime(folder));
                };
                mutation.OnSetTimestamps = call =>
                {
                    Assert.IsTrue(composition.OperationAdmission.IsActive);
                    Assert.IsTrue(composition.PlaylistOperationAdmission.IsActive);
                    Assert.IsFalse(actualLibrary.IsWriteLockHeldInitializeAll);
                    Assert.IsFalse(actualLibrary.IsWriteLockHeldInitializeMin);
                    Assert.IsTrue(call.LastWriteTime.HasValue);
                    Directory.SetLastWriteTime(call.Path, call.LastWriteTime.GetValueOrDefault());
                    changed = true;
                };
                dialogs.MessageObserved = () =>
                {
                    if (dialogs.LastMessageText != Resources.Warn_LR2LeapYearBugDetected) { return; }
                    warning = true;
                    bool libraryActive = composition.OperationAdmission.IsActive;
                    bool playlistActive = composition.PlaylistOperationAdmission.IsActive;
                    bool changedAtWarning = changed;
                    int timestampCallCount = mutation.TimestampCalls.Count;
                    bool reentryAccepted = composition.OperationAdmission.TryEnter(out IDisposable reentry);
                    if (reentryAccepted) { reentry.Dispose(); }
                    warningObservations.Add((libraryActive, playlistActive, changedAtWarning, timestampCallCount, reentryAccepted));
                };
                void AssertWarningAfterAdmissionRelease()
                {
                    Assert.AreEqual(1, warningObservations.Count);
                    Assert.IsFalse(warningObservations[0].LibraryActive);
                    Assert.IsFalse(warningObservations[0].PlaylistActive);
                    Assert.IsTrue(warningObservations[0].Changed);
                    Assert.AreEqual(1, warningObservations[0].TimestampCallCount);
                    Assert.IsTrue(warningObservations[0].ReentryAccepted);
                }
                if (shutdownDuringConfirmation)
                {
                    var confirmationEntered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
                    var releaseConfirmation = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
                    dialogs.ConfirmationObservedAsync = async () =>
                    {
                        confirmationEntered.TrySetResult();
                        await releaseConfirmation.Task;
                    };
                    Task? shutdown = null;
                    Task operation = entry switch
                    {
                        0 => activeOwner.SettingDialog.StartConfirmedEnvironmentAsync(),
                        2 => activeOwner.InitializeAsync(),
                        _ => activeOwner.ReinitializeLibraryAsync()
                    };
                    try
                    {
                        TestUiDispatcherHost.AwaitTaskOnDispatcher(Task.WhenAny(confirmationEntered.Task, operation), "leap-confirmation-entered");
                        Assert.IsTrue(confirmationEntered.Task.IsCompletedSuccessfully);
                        Assert.IsFalse(operation.IsCompleted);
                        shutdown = activeOwner.ShellShutdownWorkflow.RequestWindowCloseAsync();
                        Assert.IsTrue(composition.OperationAdmission.IsAdmissionClosed);
                        Assert.IsTrue(composition.PlaylistOperationAdmission.IsAdmissionClosed);
                        releaseConfirmation.TrySetResult();
                        try { TestUiDispatcherHost.AwaitTaskOnDispatcher(operation, "leap-confirmation-shutdown-operation"); }
                        catch (OperationCanceledException) when (entry == 3 && operation.IsCanceled) { }
                        if (entry == 3) { Assert.IsTrue(operation.IsCanceled, "ツリー入口の事前確認取消を実Taskで観測します。"); }
                        TestUiDispatcherHost.AwaitTaskOnDispatcher(shutdown, "leap-confirmation-shutdown-terminal");
                        if (operation is Task<bool> initialization) { Assert.IsFalse(initialization.GetAwaiter().GetResult()); }
                        Assert.AreEqual(0, settingsSession.SaveCount);
                        Assert.AreEqual(0, mutation.TimestampCalls.Count);
                        Assert.AreEqual(0, dialogs.MessageCount);
                        Assert.AreEqual(0, presentation.Requests.Count);
                        Assert.IsTrue(lifetime.IsFirstStartup);
                        Assert.IsFalse(changed);
                        Assert.IsFalse(warning);
                        Assert.IsFalse(composition.OperationAdmission.IsActive);
                        Assert.IsFalse(composition.PlaylistOperationAdmission.IsActive);
                        if (entry != 3) { Assert.IsFalse(activeOwner.IsInitializationCompleted); Assert.IsFalse(activeOwner.HasActiveLibraryProfile); }
                    }
                    finally
                    {
                        releaseConfirmation.TrySetResult();
                        try { TestUiDispatcherHost.AwaitTaskOnDispatcher(operation, "leap-confirmation-operation-cleanup"); }
                        catch (OperationCanceledException) when (entry == 3 && operation.IsCanceled) { }
                        finally { if (shutdown != null) { TestUiDispatcherHost.AwaitTaskOnDispatcher(shutdown, "leap-confirmation-shutdown-cleanup"); } }
                    }
                    return;
                }
                if (shutdownDuringNotification)
                {
                    var notificationEntered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
                    var releaseNotification = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
                    List<(string Message, bool Changed, bool Warning)> notificationObservations = [];
                    dialogs.MessageObservedAsync = async () =>
                    {
                        notificationObservations.Add((dialogs.LastMessageText, changed, warning));
                        dialogs.MessageResult = UiDialogResult.NotShown(UiDialogStatus.AppClosing);
                        notificationEntered.TrySetResult();
                        await releaseNotification.Task;
                    };
                    Task? shutdown = null;
                    Task operation = entry switch
                    {
                        0 => activeOwner.SettingDialog.StartConfirmedEnvironmentAsync(),
                        2 => activeOwner.InitializeAsync(),
                        _ => activeOwner.ReinitializeLibraryAsync()
                    };
                    try
                    {
                        TestUiDispatcherHost.AwaitTaskOnDispatcher(TestUiDispatcherHost.AwaitNotificationAsync(notificationEntered.Task, operation,
                            "leap-notification-shutdown-entered"), "leap-notification-shutdown-arrival");
                        Assert.AreEqual(1, notificationObservations.Count);
                        CollectionAssert.AreEqual(new[] { Resources.Warn_LR2LeapYearBugDetected }, notificationObservations.Select(value => value.Message).ToArray());
                        Assert.IsFalse(operation.IsCompleted);
                        shutdown = activeOwner.ShellShutdownWorkflow.RequestWindowCloseAsync();
                        releaseNotification.TrySetResult();
                        TestUiDispatcherHost.AwaitTaskOnDispatcher(operation, "leap-notification-shutdown-operation");
                        TestUiDispatcherHost.AwaitTaskOnDispatcher(shutdown, "leap-notification-shutdown-terminal");
                        if (operation is Task<bool> initialization) { Assert.IsFalse(initialization.GetAwaiter().GetResult()); }
                        Assert.AreEqual(0, settingsSession.SaveCount);
                        Assert.AreEqual(1, mutation.TimestampCalls.Count);
                        Assert.AreEqual(1, dialogs.MessageCount);
                        CollectionAssert.AreEqual(Array.Empty<string>(), presentation.Requests);
                        Assert.AreEqual(entry == 3, lifetime.IsFirstStartup, "通常・初設定の成功公開後の終了は成立済み初回完了を取り消しません。");
                        Assert.IsTrue(changed);
                        Assert.IsTrue(warning);
                        AssertWarningAfterAdmissionRelease();
                        Assert.IsFalse(composition.OperationAdmission.IsActive);
                        Assert.IsFalse(composition.PlaylistOperationAdmission.IsActive);
                        Assert.IsFalse(activeOwner.ProgressHub.StartupProgress.IsFailed);
                        Assert.IsTrue(activeOwner.IsInitializationCompleted);
                        Assert.IsTrue(activeOwner.HasActiveLibraryProfile);
                    }
                    finally
                    {
                        releaseNotification.TrySetResult();
                        TestUiDispatcherHost.AwaitTaskOnDispatcher(operation, "leap-notification-operation-cleanup");
                        if (shutdown != null) { TestUiDispatcherHost.AwaitTaskOnDispatcher(shutdown, "leap-notification-shutdown-cleanup"); }
                    }
                    return;
                }
                if (entry == 0)
                {
                    // どちらのBusyでも候補確認・保存・修復はなく、解放後の新しい明示要求を受け付ける。
                    foreach (ChartFileOperationSynchronizer admission in new[] { composition.OperationAdmission, composition.PlaylistOperationAdmission })
                    {
                        Assert.IsTrue(admission.TryEnter(out IDisposable held));
                        using (held) { TestUiDispatcherHost.AwaitTaskOnDispatcher(activeOwner.SettingDialog.StartConfirmedEnvironmentAsync(), "leap-settings-busy"); }
                        Assert.AreEqual(0, settingsSession.SaveCount);
                        Assert.AreEqual(0, dialogs.ConfirmationCount);
                        Assert.AreEqual(0, mutation.TimestampCalls.Count);
                    }
                    // 確認中に他操作が先着した場合も予約を持たず、副作用前に拒否する。
                    Action<UiConfirmationRequest>? assertConfirmation = dialogs.ConfirmationObserved;
                    dialogs.ConfirmationObserved = request =>
                    {
                        assertConfirmation?.Invoke(request);
                        Assert.IsTrue(composition.OperationAdmission.TryEnter(out competing));
                    };
                    TestUiDispatcherHost.AwaitTaskOnDispatcher(activeOwner.SettingDialog.StartConfirmedEnvironmentAsync(), "leap-settings-lost-admission");
                    Assert.AreEqual(0, settingsSession.SaveCount);
                    Assert.AreEqual(0, mutation.TimestampCalls.Count);
                    competing?.Dispose(); competing = null;
                    dialogs.ConfirmationObserved = assertConfirmation;
                    beforeAcceptedConfirmations = dialogs.ConfirmationCount;
                    TestUiDispatcherHost.AwaitTaskOnDispatcher(activeOwner.SettingDialog.StartConfirmedEnvironmentAsync(), "leap-settings-accepted");
                    Assert.AreEqual(0, settingsSession.SaveCount);
                }
                else if (entry == 2)
                {
                    Task<bool> operation = activeOwner.InitializeAsync();
                    TestUiDispatcherHost.AwaitTaskOnDispatcher(operation, "leap-normal-startup");
                    Assert.IsTrue(operation.GetAwaiter().GetResult());
                }
                else { TestUiDispatcherHost.AwaitTaskOnDispatcher(activeOwner.ReinitializeLibraryAsync(), "leap-tree-reinitialize"); }
                Assert.IsTrue(changed);
                Assert.IsTrue(warning);
                AssertWarningAfterAdmissionRelease();
                Assert.AreEqual(beforeAcceptedConfirmations + 1, dialogs.ConfirmationCount);
                Assert.IsFalse(composition.OperationAdmission.IsActive);
                Assert.IsFalse(composition.PlaylistOperationAdmission.IsActive);
            });
            using LR2SongDBExtended verify = new BmsLibraryDbGateway(MainWindowViewModelTestFactory.GetComposition(owner ?? throw new AssertFailedException()).DatabaseScope, new BmsDatabaseLoadOptions(true, true, true), targetDb).OpenSongDbReadOnly();
            LR2SongDB.folder row = verify.Table<LR2SongDB.folder>().Single(value => value.path == folder + Path.DirectorySeparatorChar);
            if (shutdownDuringConfirmation) { Assert.AreEqual(0, row.adddate); Assert.AreEqual(sentinel, Directory.GetLastWriteTime(folder)); }
            else { Assert.IsNull(row.adddate); }
            Assert.IsNull(row.date);
            Assert.AreEqual("saved identity", row.title);
            Assert.AreEqual("retained", row.info_a);
        }
        finally
        {
            competing?.Dispose();
            if (owner != null)
            {
                TestUiDispatcherHost.Invoke(() =>
                {
                    TestUiDispatcherHost.AwaitTaskOnDispatcher(owner.ShellShutdownWorkflow.RequestWindowCloseAsync(), "leap-initialization-close");
                    owner.SettingDialog.Dispose();
                });
            }
            library?.RequestShutdown("leap-fixture-cleanup");
            playlist?.RequestShutdown("leap-fixture-cleanup");
            Directory.Delete(root, recursive: true);
        }
    }

    private static void AssertOperationModePresentation(SettingsWindow window, bool useLr2)
    {
        RadioButton useLr2Radio = FindOperationModeRadio(window, useLr2: true);
        RadioButton standaloneRadio = FindOperationModeRadio(window, useLr2: false);
        Assert.AreEqual(BindingMode.OneWay, useLr2Radio.GetBindingExpression(ToggleButton.IsCheckedProperty)?.ParentBinding.Mode);
        Assert.AreEqual(BindingMode.OneWay, standaloneRadio.GetBindingExpression(ToggleButton.IsCheckedProperty)?.ParentBinding.Mode);
        Assert.AreEqual(useLr2, useLr2Radio.IsChecked == true);
        Assert.AreEqual(!useLr2, standaloneRadio.IsChecked == true);
    }

    private static RadioButton FindOperationModeRadio(SettingsWindow window, bool useLr2)
    {
        string name = useLr2 ? "radioButtonUseLR2" : "radioButtonNotUseLR2";
        return FindDescendants<RadioButton>(window).Single(radioButton => radioButton.Name == name);
    }

    private static BMSPlaylist AttachPlaylistTables(
        MainWindowViewModel viewModel,
        string root,
        IList<string>? scheduledOperations = null,
        string? existingDatabasePath = null)
    {
        string databasePath = existingDatabasePath ?? Path.Combine(root, "settings-test-playlists.db");
        if (existingDatabasePath == null)
        {
            File.WriteAllBytes(databasePath, []);
        }
        ApplicationComposition composition = MainWindowViewModelTestFactory.GetComposition(viewModel);
        Settings values = composition.SettingsEditSession.Values;
        var tables = new TestBmsPlaylist(composition.DatabaseScope, databasePath, null, null, null,
            () => PlaylistUrlCompletionOptionsSnapshot.CreateCurrent(values),
            () => BeatorajaBmtOptionsSnapshot.CreateCurrent(values),
            composition.CustomFolderOutputSettingsProvider,
            mutationAdmission: composition.PlaylistOperationAdmission)
        {
            BMSTables = []
        };
        tables.StartupBackgroundTaskScheduler = (operation, reason, _, _) =>
        {
            scheduledOperations?.Add(operation + ":" + reason);
            return true;
        };
        SetPrivateField(viewModel, "tables", tables);
        return tables;
    }

    private sealed class LateFailureStartupLibraryFactory : IStartupLibraryFactory
    {
        private readonly BMSLibrary library;
        private readonly BMSPlaylist playlist;
        private readonly Action? beforeCreateBmsLibrary;

        internal LateFailureStartupLibraryFactory(
            BMSLibrary library,
            BMSPlaylist playlist,
            Action? beforeCreateBmsLibrary = null)
        {
            this.library = library ?? throw new ArgumentNullException(nameof(library));
            this.playlist = playlist ?? throw new ArgumentNullException(nameof(playlist));
            this.beforeCreateBmsLibrary = beforeCreateBmsLibrary;
        }

        public BMSLibrary CreateBmsLibrary(LibraryProfile libraryProfile)
        {
            beforeCreateBmsLibrary?.Invoke();
            return library;
        }

        public BMSPlaylist CreateBmsPlaylist(LibraryProfile libraryProfile, BMSLibrary library) => playlist;
    }

    private sealed class CountingApplicationLifetime : IApplicationLifetimePort
    {
        internal int RestartCount { get; private set; }

        public bool IsFirstStartup => false;

        public void CompleteFirstStartup()
        {
        }

        public void MarkCoordinatedShutdownStarted(string reason)
        {
        }

        public void ExitForUnrestoredDestination(System.Exception failure)
        => throw new System.InvalidOperationException("Unexpected unrestored destination exit.", failure);

        /// <summary>テストが所有する一時入力は各fixtureで回収し、アプリ全体の回収を行いません。</summary>
        public Task CompleteTemporaryDirectoriesAsync(Action<string, Exception> logWarning) => Task.CompletedTask;

        public void RequestShutdown()
        {
        }

        public Task RestartApplicationAsync()
        {
            RestartCount++;
            return Task.CompletedTask;
        }
    }
}
