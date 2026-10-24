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
using System.Windows.Media;
using System.Windows.Threading;
using BeMusicSeeker.Models;
using BeMusicSeeker.Models.BmsLibraryInternal;
using BeMusicSeeker.Models.LR2;
using BeMusicSeeker.Models.Utils;
using BeMusicSeeker.Properties;
using BeMusicSeeker.ViewModels;
using BeMusicSeeker.Views;
using BeMusicSeeker.Views.Dialogs;
using ManagedBass;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Ribbit.Media;
using Ribbit.Media.Audio;
using SQLite;

namespace BeMusicSeeker.Tests;

/// <summary>設定適用の保存・後続処理・失敗／取消・実 owner 接続を検証します。</summary>
/// <remarks>判断と port 配送は Window なし、radio／音量の BAML 接続と実 DB／出力の統合は該当ケースで分担します。共有 culture・theme・Bass 受付と composition 購読を使うため非並列にします。</remarks>
[TestClass]
[DoNotParallelize]
public sealed partial class SettingDialogEditCompletionTests
{
    private static SettingsDialogViewModel CreateOperationModeDialog(
        CountingSettingsEditSession settingsSession,
        RecordingRootDialogService dialogs,
        IApplicationLifetimePort applicationLifetime)
    {
        MainWindowViewModel owner = MainWindowViewModelTestFactory.Create();
        owner.SettingDialog.Dispose();
        SetActiveLibraryProfile(owner, true);
        return new SettingsDialogViewModel(
            new TestSettingsDialogStatePort(owner, () => Task.FromResult(new StartupInitializationResult(StartupInitializationOutcome.Succeeded))),
            owner.PlaylistWorkspace,
            owner.PlaylistWorkspace,
            owner.PlayHistory,
            owner.LibraryFolderTree,
            new TestSettingsDialogPlayerFactoryPort(),
            new TestSettingsDialogPlaybackRuntimePort(),
            owner.Lr2SongDbSyncWorkflow,
            settingsSession,
            requestOperationModeRestart: request =>
            {
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
    [DataTestMethod]
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
                        applicationPathSnapshot: applicationPath,
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

    /// <summary>culture 変更で表示カタログを一回更新し、二度 Dispose した後は更新しないことを確認して、共有リソースへの購読漏れを検出します。</summary>
    [TestMethod]
    public void DisposedSettingsDialogStopsListeningToResourceServiceCultureChanges()
    {
        TestUiDispatcherHost.Invoke(() =>
        {
            string previousCulture = Resources.Culture?.Name ?? "ja-JP";
            MainWindowViewModel owner = MainWindowViewModelTestFactory.Create();
            owner.SettingDialog.Dispose();
            var playHistoryPort = new RecordingResourceRefreshPlayHistoryPort();
            SettingsDialogViewModel? dialog = null;
            try
            {
                dialog = CreateResourceListeningDialog(owner, playHistoryPort);

                ResourceService.Current.ChangeCulture("en-US");
                Assert.AreEqual(1, playHistoryPort.RefreshDisplayTargetCatalogCount);

                dialog.Dispose();
                dialog.Dispose();

                ResourceService.Current.ChangeCulture("ja-JP");
                Assert.AreEqual(1, playHistoryPort.RefreshDisplayTargetCatalogCount);
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
    [DataTestMethod]
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
                Settings values = CreateValidStandaloneSettings(root, PortableSettingsPersistenceTests.OpenSettings(path));
                values.uBMplayVolume = 50;
                values.Save();
                var gateway = new SettingsPlayerSettingsGateway(() => values);
                var player = new InternalBMSAutoPlayerSoundOnly(gateway, runtime);
                var failures = new List<Exception>();
                var composition = new ApplicationComposition(settingsEditSession: new SettingsEditSession(values),
                    defaultBmsPlayerFactory: () => player, reportSettingsApplyFailure: failures.Add,
                    uiScheduler: new WpfUiScheduler(() => Dispatcher.CurrentDispatcher),
                    applicationLifetime: TestApplicationContext.CreateLifetime(), cultureCatalog: TestApplicationContext.CreateCultureCatalog());
                owner = composition.CreateMainWindowViewModelForTest();
                owner.SettingDialog.Dispose();
                dialog = new SettingsDialogViewModel(owner, owner.PlaylistWorkspace, owner.PlaylistWorkspace,
                    owner.PlayHistory, owner.LibraryFolderTree, composition, owner.PlaybackPanel, owner.Lr2SongDbSyncWorkflow,
                    new SettingsEditSession(values), reportApplyFailure: failures.Add,
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
                Settings before = PortableSettingsPersistenceTests.OpenSettings(path);
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
                    Assert.AreEqual(60, PortableSettingsPersistenceTests.OpenSettings(path).uBMplayVolume);
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
                Assert.AreEqual(expected, PortableSettingsPersistenceTests.OpenSettings(path).uBMplayVolume);
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
    [DataTestMethod]
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
                (settings.LR2SongDBPath, settings.LR2ConfigXmlPath) = CreateValidLr2Layout(Path.Combine(root, "lr2"));
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

    /// <summary>両モードで選択済みの実RadioButtonから明示Clickによる確認を取り消すと、保存・再起動要求なしで元の選択へ戻ることを確認します。</summary>
    /// <remarks>確認前に反対の選択へ切り替え、取消が実際に選択表示を復元することをOS入力状態から分離して観測します。</remarks>
    [DataTestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public void OperationModeRadio_RejectedClickRestoresSelectionWithoutSaveOrRestart(bool initialOperationMode)
    {
        TestUiDispatcherHost.RunWindowTest(windowTest =>
        {
            string root = CreateTemporaryRoot();
            SettingsWindow? window = null;
            try
            {
                Settings settings = CreateValidStandaloneSettings(root);
                settings.OperationModeLR2DB = initialOperationMode;
                (settings.LR2SongDBPath, settings.LR2ConfigXmlPath) = CreateValidLr2Layout(Path.Combine(root, "lr2"));
                var settingsSession = new CountingSettingsEditSession(settings);
                var dialogs = new RecordingRootDialogService
                {
                    ConfirmationResult = UiDialogResult.FromMessageBoxResult(MessageBoxResult.Cancel)
                };
                var lifetime = new CountingApplicationLifetime();
                using SettingsDialogViewModel dialog = CreateOperationModeDialog(settingsSession, dialogs, lifetime);
                window = OpenSettingsWindow(windowTest, dialog);

                RadioButton requestedMode = FindOperationModeRadio(window, useLr2: !initialOperationMode);
                requestedMode.SetCurrentValue(ToggleButton.IsCheckedProperty, true);
                Assert.AreEqual(true, requestedMode.IsChecked);
                requestedMode.RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent, requestedMode));
                window.Dispatcher.Invoke(DispatcherPriority.DataBind, new Action(() => { }));

                Assert.AreEqual(1, dialogs.ConfirmationCount);
                Assert.AreEqual(0, settingsSession.SaveCount);
                Assert.AreEqual(0, lifetime.RestartCount);
                Assert.AreEqual(initialOperationMode, dialog.OperationModeLR2DB);
                Assert.AreEqual(initialOperationMode, settings.OperationModeLR2DB);
                AssertOperationModePresentation(window, initialOperationMode);
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
            using SettingsDialogViewModel dialog = CreateOperationModeDialog(
                settingsSession,
                dialogs,
                TestApplicationContext.CreateLifetime());

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

    /// <summary>同rootのA→B→Cと現在先の再設定は各確定Task内で旧管理物・両表の内容とhash方式・台帳・URL・設定を揃え、管理外を保持します。保存後失敗は捕捉済みの先とhash方式で修復し、無効保持・正本hash・同値の非再保存も保ちます。</summary>
    [DataTestMethod]
    [DataRow(false, false)]
    [DataRow(true, false)]
    [DataRow(false, true)]
    public async Task BmtConfirmation_ReoutputsAllTablesAndRepairsPartialProjectionWithoutSavingAgain(bool failSecondWrite, bool keepDisabledOutput)
    {
        string root = CreateTemporaryRoot();
        MainWindowViewModel? owner = null;
        BMSPlaylist? playlist = null;
        try
        {
            string beatoraja = Path.Combine(root, "beatoraja");
            string previousOutput = Path.Combine(beatoraja, "table");
            string output = Path.Combine(beatoraja, "replacement-table");
            Directory.CreateDirectory(previousOutput);
            Directory.CreateDirectory(output);
            File.WriteAllText(Path.Combine(beatoraja, "beatoraja.jar"), string.Empty);
            string config = Path.Combine(beatoraja, BeatorajaConfigService.ConfigFileName);
            File.WriteAllText(config, "{\"tablepath\":\"table\",\"tableURL\":[\"https://example.com/unmanaged\"]}");
            string settingsPath = Path.Combine(root, "settings", "user.config");
            Settings values = CreateValidStandaloneSettings(root, PortableSettingsPersistenceTests.OpenSettings(settingsPath));
            values.BeatorajaRootPath = beatoraja;
            values.BeatorajaBmtTablePath = previousOutput;
            values.EnableBeatorajaBmtOutput = true;
            values.RegisterBeatorajaBmtUrls = true;
            values.BeatorajaBmtHashOutputMode = "Original";
            bool initialKeepDisabledFiles = values.KeepBeatorajaBmtFilesWhenOutputDisabled;
            values.Save();
            var session = new CountingSettingsEditSession(values) { PersistOwnedValues = true };
            List<Exception> failures = [];
            TestUiDispatcherHost.Invoke(() => owner = CreateViewModel(session, firstStartup: false, reportSettingsApplyFailure: failures.Add,
                playbackRuntimePort: new TestSettingsDialogPlaybackRuntimePort()));
            Assert.IsNotNull(owner);
            SettingsDialogViewModel dialog = owner.SettingDialog;
            SetActiveLibraryProfile(owner, true);
            string db = BmsPlaylistTestSupport.CreateTempSongDbPath(root);
            PlaylistPersistenceRepository.EnsureSchema(db);
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
            string previousManifestPath = Path.Combine(previousOutput, BmtTableExportService.ManifestFileName);
            var previousManifest = Newtonsoft.Json.Linq.JObject.Parse(File.ReadAllText(previousManifestPath));
            // 現在の表にはない旧管理URLを含め、新旧に共通するURLの再登録だけでは整理漏れを隠せない入力にします。
            const string obsoleteUrl = "https://example.com/previously-managed";
            File.WriteAllText(Path.Combine(previousOutput, "obsolete.bmt"), "old managed output");
            ((Newtonsoft.Json.Linq.JArray)previousManifest["files"]!).Add("obsolete.bmt");
            previousManifest["playlists"]!["obsolete"] = new Newtonsoft.Json.Linq.JObject { ["file"] = "obsolete.bmt", ["url"] = obsoleteUrl };
            File.WriteAllText(previousManifestPath, previousManifest.ToString());
            BeatorajaConfigService.SyncTableUrls(beatoraja, [.. BeatorajaConfigService.ReadTableUrls(beatoraja), obsoleteUrl], []);
            string oldUnmanaged = Path.Combine(previousOutput, "user.txt");
            string newUnmanaged = Path.Combine(output, "user.txt");
            File.WriteAllText(oldUnmanaged, "old unmanaged");
            File.WriteAllText(newUnmanaged, "new unmanaged");
            var configDocument = Newtonsoft.Json.Linq.JObject.Parse(File.ReadAllText(config));
            configDocument["tablepath"] = "replacement-table";
            File.WriteAllText(config, configDocument.ToString());
            var presentation = new RecordingSettingsDialogPresentationPort();
            owner.SettingDialog.AttachPresentationPort(presentation);
            if (keepDisabledOutput)
            {
                Dictionary<string, byte[]> previousFiles = Directory.GetFiles(previousOutput).ToDictionary(path => path, File.ReadAllBytes);
                string[] previousUrls = BeatorajaConfigService.ReadTableUrls(beatoraja).ToArray();
                dialog.EnableBeatorajaBmtOutput = false;
                dialog.KeepBeatorajaBmtFilesWhenOutputDisabled = true;
                await dialog.CommitBmtSettingsAsync();
                Assert.AreEqual(0, failures.Count, string.Join(Environment.NewLine, failures));
                Assert.IsFalse(dialog.IsBmtProcessingRetryRequired);
                Assert.IsFalse(PortableSettingsPersistenceTests.OpenSettings(settingsPath).EnableBeatorajaBmtOutput);
                foreach ((string path, byte[] contents) in previousFiles) { CollectionAssert.AreEqual(contents, File.ReadAllBytes(path)); }
                CollectionAssert.AreEquivalent(previousFiles.Keys.ToArray(), Directory.GetFiles(previousOutput));
                CollectionAssert.AreEqual(previousUrls, BeatorajaConfigService.ReadTableUrls(beatoraja).ToArray());
                Assert.IsFalse(File.Exists(Path.Combine(output, BmtTableExportService.ManifestFileName)));
                Assert.AreEqual("new unmanaged", File.ReadAllText(newUnmanaged));
                return;
            }
            owner.SettingDialog.BeatorajaBmtHashOutputMode = "PreferSha256Only";
            string secondName = previousManifest["playlists"]?["2"]?["file"]?.ToObject<string>() ?? throw new AssertFailedException("二表目の実出力台帳がありません。");
            string secondFile = Path.Combine(output, secondName);
            File.Copy(Path.Combine(previousOutput, secondName), secondFile);
            int outputWarnings = 0;
            owner.PlaylistWorkspace.PlaylistOperationNotificationPresentationRequested += (_, request) => outputWarnings += request.Receipt.Notifications.Count;
            using (FileStream? blockedSecond = failSecondWrite ? new FileStream(secondFile, FileMode.Open, FileAccess.Read, FileShare.ReadWrite) : null)
            { await owner.SettingDialog.CommitBmtSettingsAsync(); }
            if (!failSecondWrite) { AssertCompletedOutput(output, previousOutput); }
            if (failSecondWrite)
            {
                Assert.IsTrue(outputWarnings + failures.Count > 0, "実出力の途中失敗を通知先へ渡します。");
                Assert.IsTrue(dialog.IsBmtProcessingRetryRequired, "実投影失敗はBMT欄の再試行案内にも残します。");
                Assert.IsFalse(dialog.IsBeatorajaSelectionEnabled);
                Assert.IsTrue(dialog.CanConfirmBmtSettings, "同じrootのBMT修正・再試行は受け付けます。");
                dialog.BeatorajaRootPath = Path.Combine(root, "other-beatoraja");
                Assert.AreEqual(beatoraja, dialog.BeatorajaRootPath, "未了の出力先をroot変更で取りこぼしません。");
                configDocument = Newtonsoft.Json.Linq.JObject.Parse(File.ReadAllText(config));
                configDocument["tablepath"] = "moved-again";
                File.WriteAllText(config, configDocument.ToString());
                dialog.SetPresentationActive(false);
                dialog.SetPresentationActive(true);
                dialog.RequireCompleteUsedResourcesForAutoInstall = !dialog.RequireCompleteUsedResourcesForAutoInstall;
                Assert.IsTrue(dialog.IsBmtProcessingRetryRequired, "再表示や別項目の保存は未了投影を再開しません。");
                Settings saved = PortableSettingsPersistenceTests.OpenSettings(settingsPath);
                Assert.AreEqual(output, saved.BeatorajaBmtTablePath);
                Assert.AreEqual("PreferSha256Only", saved.BeatorajaBmtHashOutputMode);
                string currentConfig = File.ReadAllText(config);
                try
                {
                    File.WriteAllText(config, "{ invalid JSON");
                    dialog.SetPresentationActive(false);
                    dialog.SetPresentationActive(true);
                    Assert.IsTrue(dialog.CanConfirmBmtSettings, "保存済み要求の再試行入口は現在configの新規確定用検証で閉じません。");
                    int failuresBeforeInvalidConfigRetry = outputWarnings + failures.Count;
                    await dialog.CommitBmtSettingsAsync();
                    Assert.IsTrue(outputWarnings + failures.Count > failuresBeforeInvalidConfigRetry, "必要なURL同期の読取失敗を成功へ置き換えません。");
                    Assert.IsTrue(dialog.IsBmtProcessingRetryRequired, "同期失敗後も当初の旧新出力先を明示再試行できます。");
                    Assert.IsTrue(dialog.CanConfirmBmtSettings);
                }
                finally
                {
                    File.WriteAllText(config, currentConfig);
                }
                using var oldStream = new System.IO.Compression.GZipStream(File.OpenRead(secondFile), System.IO.Compression.CompressionMode.Decompress);
                using var oldReader = new StreamReader(oldStream);
                Assert.IsNotNull(Newtonsoft.Json.Linq.JObject.Parse(oldReader.ReadToEnd()).SelectToken("folder[0].songs[0].md5"));
            }
            // 同値の明示再更新は成功媒体を再保存せず、既存ファイルに外部変更があっても全表を再投影します。
            string firstFile = Directory.GetFiles(output, "*.bmt").Single(path => path != secondFile);
            int failuresBeforeRepair = failures.Count;
            if (!failSecondWrite) { Assert.AreEqual(0, failuresBeforeRepair); }
            File.WriteAllText(firstFile, "projection requires repair");
            using (var blockedSettings = new FileStream(settingsPath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite))
            { await owner.SettingDialog.CommitBmtSettingsAsync(); }
            AssertCompletedOutput(output, previousOutput);
            Assert.IsFalse(dialog.IsBmtProcessingRetryRequired, "全表の明示再更新が成功した時だけ案内を解消します。");
            Assert.IsTrue(dialog.IsBeatorajaSelectionEnabled);
            Assert.AreEqual(failuresBeforeRepair, failures.Count, string.Join(Environment.NewLine, failures));
            Assert.IsFalse(File.Exists(previousManifestPath));
            Assert.AreEqual(0, Directory.GetFiles(previousOutput, "*.bmt").Length);
            Assert.AreEqual("old unmanaged", File.ReadAllText(oldUnmanaged));
            Assert.AreEqual("new unmanaged", File.ReadAllText(newUnmanaged));
            Assert.IsFalse(Directory.Exists(Path.Combine(beatoraja, "moved-again")), "外部configの後続変更は未了要求の新先を置き換えません。");
            values.Reload();
            Assert.AreEqual("PreferSha256Only", values.BeatorajaBmtHashOutputMode);
            Assert.AreEqual(output, values.BeatorajaBmtTablePath);
            string[] files = Directory.GetFiles(output, "*.bmt");
            Assert.AreEqual(2, files.Length);
            var repository = new PlaylistPersistenceRepository(db);
            foreach (BMSTable table in tables)
            {
                BMSTableEntry persisted = repository.LoadPersistedPlaylistEntries(table.playlist_id, activeOnly: true).Single();
                int id = table.playlist_id ?? throw new InvalidOperationException("入力表のIDがありません。");
                Assert.AreEqual(id, persisted.playlist_id);
                Assert.AreEqual(new string((char)('a' + id), 32), persisted.md5);
                Assert.AreEqual(new string((char)('a' + id), 64), persisted.sha256);
            }
            CollectionAssert.AreEqual(new[] { "https://example.com/unmanaged", "bemusicseeker://playlist/1", "bemusicseeker://playlist/2" },
                BeatorajaConfigService.ReadTableUrls(beatoraja).ToArray());
            Dictionary<string, byte[]> bytes = files.ToDictionary(path => path, File.ReadAllBytes);
            Dictionary<string, DateTime> times = files.ToDictionary(path => path, File.GetLastWriteTimeUtc);
            configDocument = Newtonsoft.Json.Linq.JObject.Parse(File.ReadAllText(config));
            configDocument["tablepath"] = "replacement-table";
            File.WriteAllText(config, configDocument.ToString());
            await playlist.BmtOutput.ExportTablesAsync(tables, "next-same-mode");
            foreach (string file in files)
            {
                CollectionAssert.AreEqual(bytes[file], File.ReadAllBytes(file));
                Assert.AreEqual(times[file], File.GetLastWriteTimeUtc(file));
            }
            string finalOutput = Directory.CreateDirectory(Path.Combine(beatoraja, "final-table")).FullName;
            string finalUnmanaged = Path.Combine(finalOutput, "user.txt");
            File.WriteAllBytes(finalUnmanaged, [0, 255, 17, 42]);
            configDocument["tablepath"] = "final-table";
            File.WriteAllText(config, configDocument.ToString());
            await dialog.CommitBmtSettingsAsync();
            AssertCompletedOutput(finalOutput, output);
            // 現在先を再設定した要求も、実ファイルを全表修復してから終端します。
            File.WriteAllText(Directory.GetFiles(finalOutput, "*.bmt")[0], "repair current output");
            using (var blockedSettings = new FileStream(settingsPath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite))
            { await dialog.CommitBmtSettingsAsync(); }
            AssertCompletedOutput(finalOutput, output);

            void AssertCompletedOutput(string currentOutput, string oldOutput)
            {
                Assert.IsFalse(dialog.IsBmtProcessingRetryRequired);
                Settings saved = PortableSettingsPersistenceTests.OpenSettings(settingsPath);
                Assert.AreEqual(beatoraja, saved.BeatorajaRootPath);
                Assert.AreEqual(currentOutput, saved.BeatorajaBmtTablePath);
                Assert.IsTrue(saved.EnableBeatorajaBmtOutput);
                Assert.IsTrue(saved.RegisterBeatorajaBmtUrls);
                Assert.AreEqual(initialKeepDisabledFiles, saved.KeepBeatorajaBmtFilesWhenOutputDisabled);
                Assert.AreEqual("PreferSha256Only", saved.BeatorajaBmtHashOutputMode);
                Assert.IsFalse(File.Exists(Path.Combine(oldOutput, BmtTableExportService.ManifestFileName)));
                Assert.AreEqual(0, Directory.GetFiles(oldOutput, "*.bmt").Length);
                var manifest = Newtonsoft.Json.Linq.JObject.Parse(File.ReadAllText(Path.Combine(currentOutput, BmtTableExportService.ManifestFileName)));
                var managedFiles = manifest["files"] as Newtonsoft.Json.Linq.JArray;
                var managedPlaylists = manifest["playlists"] as Newtonsoft.Json.Linq.JObject;
                Assert.IsNotNull(managedFiles);
                Assert.IsNotNull(managedPlaylists);
                Assert.AreEqual(2, managedFiles.Count);
                Assert.AreEqual(2, managedPlaylists.Count);
                CollectionAssert.AreEquivalent(new[] { "1", "2" }, managedPlaylists.Properties().Select(property => property.Name).ToArray());
                string[] currentFiles = Directory.GetFiles(currentOutput, "*.bmt");
                Assert.AreEqual(2, currentFiles.Length);
                string[]? managedFileNames = managedFiles.ToObject<string[]>();
                Assert.IsNotNull(managedFileNames);
                CollectionAssert.AreEquivalent(currentFiles.Select(Path.GetFileName).ToArray(), managedFileNames);
                foreach (int id in new[] { 1, 2 })
                {
                    string expectedUrl = "bemusicseeker://playlist/" + id;
                    var managedPlaylist = managedPlaylists[id.ToString()] as Newtonsoft.Json.Linq.JObject;
                    Assert.IsNotNull(managedPlaylist);
                    Assert.AreEqual(expectedUrl, managedPlaylist.Value<string>("url"));
                    string? fileName = managedPlaylist.Value<string>("file");
                    Assert.IsNotNull(fileName);
                    CollectionAssert.Contains(managedFileNames, fileName);
                    using var stream = new System.IO.Compression.GZipStream(File.OpenRead(Path.Combine(currentOutput, fileName)), System.IO.Compression.CompressionMode.Decompress);
                    using var reader = new StreamReader(stream);
                    var document = Newtonsoft.Json.Linq.JObject.Parse(reader.ReadToEnd());
                    Assert.AreEqual(expectedUrl, document.Value<string>("url"));
                    Assert.AreEqual("Both " + id, document.Value<string>("name"));
                    var folders = document["folder"] as Newtonsoft.Json.Linq.JArray;
                    Assert.IsNotNull(folders);
                    Assert.AreEqual(1, folders.Count);
                    Assert.AreEqual("Folder", folders[0].Value<string>("name"));
                    var songs = folders[0]["songs"] as Newtonsoft.Json.Linq.JArray;
                    Assert.IsNotNull(songs);
                    Assert.AreEqual(1, songs.Count);
                    Assert.AreEqual("Song " + id, songs[0].Value<string>("title"));
                    Assert.IsNull(songs[0]["md5"], "各確定Taskの終端時点で両表をPreferSha256Onlyへ更新します。");
                    Assert.AreEqual(new string((char)('a' + id), 64), songs[0].Value<string>("sha256"));
                }
                CollectionAssert.AreEqual(new[] { "https://example.com/unmanaged", "bemusicseeker://playlist/1", "bemusicseeker://playlist/2" },
                    BeatorajaConfigService.ReadTableUrls(beatoraja).ToArray());
                CollectionAssert.AreEqual(System.Text.Encoding.UTF8.GetBytes("old unmanaged"), File.ReadAllBytes(oldUnmanaged));
                CollectionAssert.AreEqual(System.Text.Encoding.UTF8.GetBytes("new unmanaged"), File.ReadAllBytes(newUnmanaged));
                if (currentOutput != output) { CollectionAssert.AreEqual(new byte[] { 0, 255, 17, 42 }, File.ReadAllBytes(Path.Combine(currentOutput, "user.txt"))); }
            }
        }
        finally
        {
            playlist?.RequestShutdown("settings-bmt-projection-fixture");
            if (owner != null)
            {
                TestUiDispatcherHost.Invoke(() =>
                {
                    TestUiDispatcherHost.AwaitTaskOnDispatcher(owner.ShellShutdownWorkflow.RequestWindowCloseAsync(), "settings-bmt-projection-owner-terminal");
                    owner.SettingDialog.Dispose();
                });
            }
            Directory.Delete(root, recursive: true);
        }
    }

    /// <summary>同一先のBMT無効化でURL更新が失敗しても台帳を失わず、再表示・別保存では再開せず、明示再試行だけで管理URLと管理物を除去します。</summary>
    [TestMethod]
    public async Task BmtDisable_SameOutputUrlFailureRetainsOwnershipUntilExplicitRetry()
    {
        string root = CreateTemporaryRoot();
        MainWindowViewModel? owner = null;
        BMSPlaylist? playlist = null;
        try
        {
            string beatoraja = Directory.CreateDirectory(Path.Combine(root, "beatoraja")).FullName;
            string output = Directory.CreateDirectory(Path.Combine(beatoraja, "table")).FullName;
            File.WriteAllText(Path.Combine(beatoraja, "beatoraja.jar"), string.Empty);
            string config = Path.Combine(beatoraja, BeatorajaConfigService.ConfigFileName);
            const string unmanagedUrl = "https://example.com/unmanaged";
            const string oldManagedUrl = "https://example.com/previously-managed";
            File.WriteAllText(config, "{\"tablepath\":\"table\",\"tableURL\":[\"" + unmanagedUrl + "\",\"" + oldManagedUrl + "\"]}");
            string managedFile = Path.Combine(output, "obsolete.bmt");
            string unmanagedFile = Path.Combine(output, "user.bmt");
            File.WriteAllText(managedFile, "managed");
            File.WriteAllText(unmanagedFile, "unmanaged");
            string manifest = Path.Combine(output, BmtTableExportService.ManifestFileName);
            File.WriteAllText(manifest, new Newtonsoft.Json.Linq.JObject
            {
                ["files"] = new Newtonsoft.Json.Linq.JArray("obsolete.bmt"),
                ["playlists"] = new Newtonsoft.Json.Linq.JObject
                {
                    ["obsolete"] = new Newtonsoft.Json.Linq.JObject { ["url"] = oldManagedUrl, ["file"] = "obsolete.bmt" }
                }
            }.ToString());
            byte[] originalManifest = File.ReadAllBytes(manifest);
            byte[] originalConfig = File.ReadAllBytes(config);
            string settingsPath = Path.Combine(root, "settings", "user.config");
            Settings values = CreateValidStandaloneSettings(root, PortableSettingsPersistenceTests.OpenSettings(settingsPath));
            values.BeatorajaRootPath = beatoraja;
            values.BeatorajaBmtTablePath = output;
            values.EnableBeatorajaBmtOutput = true;
            values.KeepBeatorajaBmtFilesWhenOutputDisabled = false;
            values.RegisterBeatorajaBmtUrls = true;
            values.Save();
            var session = new CountingSettingsEditSession(values) { PersistOwnedValues = true };
            List<Exception> failures = [];
            TestUiDispatcherHost.Invoke(() => owner = CreateViewModel(session, firstStartup: false, reportSettingsApplyFailure: failures.Add,
                playbackRuntimePort: new TestSettingsDialogPlaybackRuntimePort()));
            Assert.IsNotNull(owner);
            SetActiveLibraryProfile(owner, true);
            string db = BmsPlaylistTestSupport.CreateTempSongDbPath(root);
            PlaylistPersistenceRepository.EnsureSchema(db);
            playlist = AttachPlaylistTables(owner, root, existingDatabasePath: db);
            SettingsDialogViewModel dialog = owner.SettingDialog;
            dialog.EnableBeatorajaBmtOutput = false;
            using (File.Open(config, FileMode.Open, FileAccess.Read, FileShare.Read))
            {
                await dialog.CommitBmtSettingsAsync();
            }
            Assert.AreEqual(1, failures.Count, "必要URL更新の実置換失敗を設定操作の失敗として通知します。");
            Assert.IsFalse(PortableSettingsPersistenceTests.OpenSettings(settingsPath).EnableBeatorajaBmtOutput);
            Assert.IsTrue(dialog.IsBmtProcessingRetryRequired);
            Assert.IsTrue(dialog.CanConfirmBmtSettings);
            CollectionAssert.AreEqual(originalManifest, File.ReadAllBytes(manifest), "URL更新前に台帳を消すと再試行入力を失います。");
            CollectionAssert.AreEqual(originalConfig, File.ReadAllBytes(config));
            Assert.AreEqual("managed", File.ReadAllText(managedFile));

            dialog.SetPresentationActive(false);
            dialog.SetPresentationActive(true);
            dialog.RequireCompleteUsedResourcesForAutoInstall = !dialog.RequireCompleteUsedResourcesForAutoInstall;
            Assert.IsTrue(dialog.IsBmtProcessingRetryRequired);
            CollectionAssert.AreEqual(originalManifest, File.ReadAllBytes(manifest));
            CollectionAssert.AreEqual(originalConfig, File.ReadAllBytes(config));
            int savesBeforeRetry = session.SaveCount;
            using (File.Open(settingsPath, FileMode.Open, FileAccess.Read, FileShare.Read))
            {
                await dialog.CommitBmtSettingsAsync();
            }
            Assert.AreEqual(savesBeforeRetry, session.SaveCount, "保存済み無効化は再保存せず修復します。");
            Assert.AreEqual(1, failures.Count, string.Join(Environment.NewLine, failures));
            Assert.IsFalse(dialog.IsBmtProcessingRetryRequired);
            Assert.IsFalse(File.Exists(manifest));
            Assert.IsFalse(File.Exists(managedFile));
            Assert.AreEqual("unmanaged", File.ReadAllText(unmanagedFile));
            CollectionAssert.AreEqual(new[] { unmanagedUrl }, BeatorajaConfigService.ReadTableUrls(beatoraja).ToArray());
        }
        finally
        {
            playlist?.RequestShutdown("settings-bmt-disable-fixture");
            if (owner != null)
            {
                TestUiDispatcherHost.Invoke(() =>
                {
                    TestUiDispatcherHost.AwaitTaskOnDispatcher(owner.ShellShutdownWorkflow.RequestWindowCloseAsync(), "settings-bmt-disable-owner-terminal");
                    owner.SettingDialog.Dispose();
                });
            }
            Directory.Delete(root, recursive: true);
        }
    }

    /// <summary>音声テストへ品質・thread の draft を渡し、進捗不成立でも保存済み音声値を保って有効な AUTO 要求の失敗表示を出すことを確認します。</summary>
    [TestMethod]
    public async Task AudioDeviceTest_NoStreamProgress_DoesNotChangeEditedAudioValues()
    {
        string root = CreateTemporaryRoot();
        try
        {
            Settings settings = CreateValidStandaloneSettings(root);
            ConfigureExplicitAudioSettings(settings);
            int originalResamplingQuality = settings.PlayerResamplingQuality;
            int originalMixerThreadCount = settings.PlayerMixerThreadCount;
            var settingsSession = new CountingSettingsEditSession(settings);
            MainWindowViewModel viewModel = CreateViewModel(settingsSession, firstStartup: false);
            var audioGateway = new TestAudioSettingsGateway
            {
                OutputSelection = new AudioOutputSelection(
                    AudioDriver.WasapiShared,
                    settings.PlayerDevice,
                    settings.PlayerDeviceName)
            };
            AudioDeviceTestRequest? capturedRequest = null;
            var workflow = new AudioDeviceTestWorkflowOwner(
                new TestAudioDeviceTestPlaybackPort(),
                new DelegateAudioDeviceTestRuntime(request =>
                {
                    capturedRequest = request;
                    return AudioDeviceTestResultFactory.CreateSuccessful(
                            request,
                            actualRate: SampleRate.SAMPLE_RATE_48000Hz,
                            engineFormat: SampleFormat.SAMPLE_FLOAT_32BIT,
                            endpointFormat: SampleFormat.SAMPLE_FLOAT_32BIT,
                            actualDeviceName: "Changed name",
                            latency: 21,
                            streamProgressSucceeded: false);
                }));
            using SettingsDialogViewModel dialog = CreateAudioDeviceTestDialog(
                viewModel,
                settingsSession,
                workflow,
                audioGateway);
            dialog.PlayerResamplingQuality = 3;
            dialog.PlayerMixerThreadCount = 4;

            await dialog.RunAudioDeviceTestAsync();

            Assert.AreEqual(3, capturedRequest?.SampleRateConversionQuality);
            Assert.AreEqual(4, capturedRequest?.PlayerMixerThreadCount);
            Assert.AreEqual(3, capturedRequest?.AudioOutputRequest.SampleRateConversionQuality);
            Assert.AreEqual(4, capturedRequest?.AudioOutputRequest.PlayerMixerThreadCount);
            Assert.AreEqual(3, settings.PlayerResamplingQuality);
            Assert.AreEqual(4, settings.PlayerMixerThreadCount);
            AssertExplicitAudioSettingsUnchanged(settings, audioGateway, expectedResamplingQuality: 3);
            string status = dialog.AudioDeviceTestStatusMessage;
            StringAssert.Contains(status, Resources.AudioDeviceTestStreamProgressFailureReason);
            StringAssert.Contains(status, Resources.AudioDeviceCapabilityAuto);
            StringAssert.Contains(status, SampleFormat.AUTO.ToString());
            Assert.IsFalse(status.Contains(
                string.Format(Resources.AudioSampleRateOptionFormat, 44100),
                StringComparison.Ordinal));
            Assert.IsFalse(status.Contains(Resources.AudioSampleFormat16Bit, StringComparison.Ordinal));
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    /// <summary>要求と異なる backend・機器・形式でのテスト結果を実値と診断に区別して表示し、保存済み要求を上書きしないことを確認して、fallback による利用者意図の消失を検出します。</summary>
    [TestMethod]
    public async Task AudioDeviceTest_BackendFallback_DoesNotChangeEditedAudioValues()
    {
        string root = CreateTemporaryRoot();
        try
        {
            Settings settings = CreateValidStandaloneSettings(root);
            ConfigureExplicitAudioSettings(settings);
            var settingsSession = new CountingSettingsEditSession(settings);
            MainWindowViewModel viewModel = CreateViewModel(settingsSession, firstStartup: false);
            var audioGateway = new TestAudioSettingsGateway
            {
                OutputSelection = new AudioOutputSelection(
                    AudioDriver.Asio,
                    settings.PlayerDevice,
                    settings.PlayerDeviceName)
            };
            const string fallbackReason =
                "Requested ASIO device identity was stale; a compatible name match was used. "
                + "Requested ASIO endpoint format SAMPLE_INT_16BIT differs from native endpoint format SAMPLE_INT_24BIT.";
            var workflow = new AudioDeviceTestWorkflowOwner(
                new TestAudioDeviceTestPlaybackPort(),
                new DelegateAudioDeviceTestRuntime(request =>
                    AudioDeviceTestResultFactory.CreateSuccessful(
                        request,
                        actualBackend: AudioDriver.WasapiShared,
                        actualDevice: "fallback-device",
                        actualDeviceName: "Fallback device",
                        actualRate: SampleRate.SAMPLE_RATE_48000Hz,
                        engineFormat: SampleFormat.SAMPLE_FLOAT_32BIT,
                        endpointFormat: SampleFormat.SAMPLE_INT_24BIT,
                        endpointContainerBits: 24,
                        endpointEffectiveBits: 24,
                        latency: 16.5,
                        fallbackReason: fallbackReason)));
            using SettingsDialogViewModel dialog = CreateAudioDeviceTestDialog(
                viewModel,
                settingsSession,
                workflow,
                audioGateway);

            await dialog.RunAudioDeviceTestAsync();

            AssertExplicitAudioSettingsUnchanged(settings, audioGateway, AudioDriver.Asio);
            string resultMessage = dialog.AudioDeviceTestStatusMessage;
            StringAssert.Contains(resultMessage, AudioDriverDisplayNames.Get(AudioDriver.Asio));
            StringAssert.Contains(resultMessage, "Requested device");
            StringAssert.Contains(resultMessage, string.Format(Resources.AudioSampleRateOptionFormat, 44100));
            StringAssert.Contains(resultMessage, SampleFormat.AUTO.ToString());
            StringAssert.Contains(resultMessage, AudioDriverDisplayNames.Get(AudioDriver.WasapiShared));
            StringAssert.Contains(resultMessage, "Fallback device");
            StringAssert.Contains(resultMessage, string.Format(Resources.AudioSampleRateOptionFormat, 48000));
            StringAssert.Contains(resultMessage, Resources.AudioSampleFormatFloat32);
            StringAssert.Contains(resultMessage, Resources.AudioSampleFormat24Bit);
            StringAssert.Contains(
                resultMessage,
                string.Format(Resources.AudioDeviceCapabilityPrecisionPairFormat, 24, 24));
            StringAssert.Contains(resultMessage, Resources.AudioDeviceTestFallbackReason);
            StringAssert.Contains(
                resultMessage,
                string.Format(System.Globalization.CultureInfo.CurrentCulture, "{0:N1}", 16.5));
            StringAssert.Contains(dialog.AudioDeviceTestDiagnosticMessage ?? string.Empty, fallbackReason);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    /// <summary>音声ページの再表示だけで照会し、同じ表示中は重複せず、保存済み非対応値を表示に残して rate ごとの対応形式を選び直すことを確認します。</summary>
    [TestMethod]
    public async Task AudioDeviceCapabilityQuery_AudioPageVisibilityRefreshesAndFiltersFormatsByRate()
    {
        string root = CreateTemporaryRoot();
        AudioDeviceTestWorkflowOwner? workflow = null;
        SettingsDialogViewModel? dialog = null;
        PropertyChangedEventHandler? appliedObserver = null;
        var firstReleased = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var secondReleased = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        int releasedCount = 0;
        int queryCount = 0;
        Action observeReleased = () =>
        {
            if (Interlocked.Increment(ref releasedCount) == 1) { firstReleased.TrySetResult(); }
            else { secondReleased.TrySetResult(); }
        };
        try
        {
            Settings settings = CreateValidStandaloneSettings(root);
            ConfigureExplicitAudioSettings(settings);
            var settingsSession = new CountingSettingsEditSession(settings);
            MainWindowViewModel viewModel = CreateViewModel(settingsSession, firstStartup: false);
            var audioGateway = new TestAudioSettingsGateway
            {
                OutputSelection = new AudioOutputSelection(
                    AudioDriver.WasapiExclusive,
                    settings.PlayerDevice,
                    settings.PlayerDeviceName)
            };
            AudioDeviceCapabilityRequest? observedRequest = null;
            var capabilityRuntime = new DelegateAudioDeviceCapabilityRuntime(request =>
            {
                observedRequest = request;
                Interlocked.Increment(ref queryCount);
                return new AudioDeviceCapabilityResult(
                    request.Backend,
                    request.DeviceIdentity,
                    request.DeviceName,
                    AudioDeviceCapabilityStatus.Available,
                    [SampleRate.SAMPLE_RATE_352800Hz, SampleRate.SAMPLE_RATE_384000Hz],
                    [
                        new(SampleRate.SAMPLE_RATE_352800Hz, SampleFormat.SAMPLE_FLOAT_32BIT, true),
                        new(SampleRate.SAMPLE_RATE_352800Hz, SampleFormat.SAMPLE_INT_16BIT, false),
                        new(SampleRate.SAMPLE_RATE_384000Hz, SampleFormat.SAMPLE_INT_16BIT, true)
                    ]);
            });
            workflow = new AudioDeviceTestWorkflowOwner(
                new TestAudioDeviceTestPlaybackPort(),
                new DelegateAudioDeviceTestRuntime(_ => throw new InvalidOperationException("test runtime unused")),
                capabilityRuntime);
            workflow.OperationReleased += observeReleased;
            dialog = CreateAudioDeviceTestDialog(
                viewModel,
                settingsSession,
                workflow,
                audioGateway);
            var firstApplied = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var secondApplied = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            int appliedCount = 0;
            appliedObserver = (_, args) =>
            {
                if (args.PropertyName == nameof(SettingsDialogViewModel.PlayerSampleRateNames)
                    && dialog.PlayerSampleRateNames.ContainsKey(SampleRate.SAMPLE_RATE_384000Hz))
                {
                    if (Interlocked.Increment(ref appliedCount) == 1)
                    {
                        firstApplied.TrySetResult();
                    }
                    else
                    {
                        secondApplied.TrySetResult();
                    }
                }
            };
            dialog.PropertyChanged += appliedObserver;
            dialog.SetPresentationActive(active: true);
            Assert.AreEqual(0, queryCount);
            dialog.SetAudioSettingsPageVisible(visible: true);
            dialog.SetAudioSettingsPageVisible(visible: true);
            await firstApplied.Task;
            Assert.AreEqual(1, queryCount);
            dialog.SetAudioSettingsPageVisible(visible: false);
            Assert.IsNull(dialog.AudioDeviceCapabilityStatusMessage);
            dialog.SetAudioSettingsPageVisible(visible: true);
            await secondApplied.Task;
            Assert.AreEqual(2, queryCount);

            AudioDeviceCapabilityRequest capturedRequest = observedRequest
                ?? throw new AssertFailedException("Capability query did not reach its runtime.");
            Assert.AreEqual(AudioDriver.WasapiExclusive, capturedRequest.Backend);
            Assert.AreEqual("requested-device", capturedRequest.DeviceIdentity);
            Assert.AreEqual(SampleRate.SAMPLE_RATE_44100Hz, capturedRequest.SavedRate);
            Assert.AreEqual(SampleFormat.SAMPLE_INT_16BIT, capturedRequest.SavedFormat);
            Assert.IsNull(dialog.AudioDeviceCapabilityStatusMessage);
            Assert.AreEqual(SampleRate.SAMPLE_RATE_44100Hz, settings.PlayerSampleRate);
            Assert.AreEqual(SampleFormat.SAMPLE_INT_16BIT, settings.PlayerFormat);
            Assert.IsTrue(dialog.PlayerSampleRateNames.ContainsKey(SampleRate.SAMPLE_RATE_384000Hz));
            Assert.IsTrue(dialog.PlayerSampleRateNames.ContainsKey(SampleRate.SAMPLE_RATE_352800Hz));
            StringAssert.Contains(
                dialog.PlayerSampleRateNames[SampleRate.SAMPLE_RATE_44100Hz],
                Resources.AudioDeviceCapabilityUnsupportedChoice);
            Assert.IsTrue(dialog.PlayerFormatNames.ContainsKey(SampleFormat.SAMPLE_INT_16BIT));
            StringAssert.Contains(
                dialog.PlayerFormatNames[SampleFormat.SAMPLE_INT_16BIT],
                Resources.AudioDeviceCapabilityUnsupportedChoice);
            Assert.IsFalse(dialog.EncoderSampleRateNames.ContainsKey(SampleRate.SAMPLE_RATE_352800Hz));
            Assert.IsTrue(dialog.EncoderFormatNames.ContainsKey(SampleFormat.SAMPLE_INT_8BIT));

            dialog.PlayerSampleRate = SampleRate.SAMPLE_RATE_384000Hz;
            Assert.AreEqual(SampleFormat.AUTO, dialog.PlayerFormat);
            Assert.AreEqual(SampleFormat.SAMPLE_INT_16BIT, settings.PlayerFormat);
            CollectionAssert.AreEquivalent(
                new[] { SampleFormat.AUTO, SampleFormat.SAMPLE_INT_16BIT },
                dialog.PlayerFormatNames.Keys.ToArray());

            dialog.SetPresentationActive(active: false);
            Assert.IsNull(dialog.AudioDeviceCapabilityStatusMessage);
            Assert.IsNull(dialog.AudioDeviceCapabilityDiagnosticMessage);
            Assert.AreEqual(Resources.AudioDeviceCapabilityNotQueried,
                dialog.AudioDeviceCapabilityFormatDescription);
        }
        finally
        {
            // Dispose が次の照会と表示を止める。OperationReleased は runtime 資源解放であり、表示 wrapper の Task 終端ではない。
            dialog?.Dispose();
            if (queryCount > 0) { await firstReleased.Task; }
            if (queryCount > 1) { await secondReleased.Task; }
            if (dialog != null && appliedObserver != null) { dialog.PropertyChanged -= appliedObserver; }
            if (workflow != null) { workflow.OperationReleased -= observeReleased; }
            Directory.Delete(root, recursive: true);
        }
    }

    /// <summary>照会保留中の二回のbackend変更は途中選択の照会を始めず、解放後に最終選択への照会を一回追加して最終backend選択を保持することを確認します。</summary>
    [TestMethod]
    public async Task AudioDeviceCapabilityQuery_SelectionChangesDuringQueryRunsOnlyFinalSelection()
    {
        string root = CreateTemporaryRoot();
        AudioDeviceTestWorkflowOwner? workflow = null;
        SettingsDialogViewModel? dialog = null;
        PropertyChangedEventHandler? appliedObserver = null;
        var firstReleased = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var secondReleased = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        int releasedCount = 0;
        Action observeReleased = () =>
        {
            if (Interlocked.Increment(ref releasedCount) == 1) { firstReleased.TrySetResult(); }
            else { secondReleased.TrySetResult(); }
        };
        var firstQueryStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseFirstQuery = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var finalQueryStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var finalQueryApplied = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var requestedBackends = new List<AudioDriver>();
        object requestedBackendsSync = new();
        try
        {
            Settings settings = CreateValidStandaloneSettings(root);
            ConfigureExplicitAudioSettings(settings);
            var settingsSession = new CountingSettingsEditSession(settings);
            MainWindowViewModel viewModel = CreateViewModel(settingsSession, firstStartup: false);
            var audioGateway = new TestAudioSettingsGateway
            {
                OutputSelection = new AudioOutputSelection(
                    AudioDriver.WasapiExclusive,
                    settings.PlayerDevice,
                    settings.PlayerDeviceName)
            };
            var capabilityRuntime = new DelegateAudioDeviceCapabilityRuntime(request =>
            {
                int queryOrdinal;
                lock (requestedBackendsSync)
                {
                    requestedBackends.Add(request.Backend);
                    queryOrdinal = requestedBackends.Count;
                }
                if (queryOrdinal == 1)
                {
                    firstQueryStarted.TrySetResult();
                    releaseFirstQuery.Task.GetAwaiter().GetResult();
                }
                else
                {
                    finalQueryStarted.TrySetResult();
                }
                return new AudioDeviceCapabilityResult(
                    request.Backend,
                    request.DeviceIdentity,
                    request.DeviceName,
                    AudioDeviceCapabilityStatus.Available,
                    [SampleRate.SAMPLE_RATE_384000Hz]);
            });
            workflow = new AudioDeviceTestWorkflowOwner(
                new TestAudioDeviceTestPlaybackPort(),
                new DelegateAudioDeviceTestRuntime(_ => throw new InvalidOperationException("test runtime unused")),
                capabilityRuntime);
            workflow.OperationReleased += observeReleased;
            dialog = CreateAudioDeviceTestDialog(
                viewModel,
                settingsSession,
                workflow,
                audioGateway);
            appliedObserver = (_, args) =>
            {
                if (args.PropertyName == nameof(SettingsDialogViewModel.PlayerSampleRateNames)
                    && dialog.PlayerSampleRateNames.ContainsKey(SampleRate.SAMPLE_RATE_384000Hz))
                {
                    finalQueryApplied.TrySetResult();
                }
            };
            dialog.PropertyChanged += appliedObserver;
            dialog.SetPresentationActive(active: true);
            dialog.SetAudioSettingsPageVisible(visible: true);
            await firstQueryStarted.Task;
            dialog.PlayerDriverIndex = AudioDriverPolicy.IndexOf(AudioDriver.WasapiShared);
            dialog.PlayerDriverIndex = AudioDriverPolicy.IndexOf(AudioDriver.Asio);
            lock (requestedBackendsSync)
            {
                CollectionAssert.AreEqual(
                    new[] { AudioDriver.WasapiExclusive },
                    requestedBackends);
            }
            releaseFirstQuery.TrySetResult();
            await finalQueryStarted.Task;
            await finalQueryApplied.Task;

            lock (requestedBackendsSync)
            {
                CollectionAssert.AreEqual(
                    new[] { AudioDriver.WasapiExclusive, AudioDriver.Asio },
                    requestedBackends);
            }
            Assert.AreEqual(AudioDriverPolicy.IndexOf(AudioDriver.Asio), dialog.PlayerDriverIndex);
        }
        finally
        {
            // Dispose が次の照会と表示を止める。OperationReleased は runtime 資源解放であり、表示 wrapper の Task 終端ではない。
            dialog?.Dispose();
            releaseFirstQuery.TrySetResult();
            if (firstQueryStarted.Task.IsCompleted) { await firstReleased.Task; }
            if (finalQueryStarted.Task.IsCompleted) { await secondReleased.Task; }
            if (dialog != null && appliedObserver != null) { dialog.PropertyChanged -= appliedObserver; }
            if (workflow != null) { workflow.OperationReleased -= observeReleased; }
            Directory.Delete(root, recursive: true);
        }
    }

    /// <summary>既定機器・AUTO rate／format で成功しても、解決済み機器と実測値を保存設定へ固定しないことを確認します。</summary>
    [TestMethod]
    public async Task AudioDeviceTest_DefaultAndAutoSuccess_PreservesUserIntent()
    {
        string root = CreateTemporaryRoot();
        try
        {
            Settings settings = CreateValidStandaloneSettings(root);
            settings.PlayerDevice = null;
            settings.PlayerDeviceName = null;
            settings.PlayerSampleRate = SampleRate.AUTO;
            settings.PlayerFormat = SampleFormat.AUTO;
            settings.PlayerBufferSize = 10;
            settings.PlayerWASAPIParam = false;
            settings.uBMplayVolume = 50;
            var settingsSession = new CountingSettingsEditSession(settings);
            MainWindowViewModel viewModel = CreateViewModel(settingsSession, firstStartup: false);
            var audioGateway = new TestAudioSettingsGateway { PlayerDriver = AudioDriver.WasapiShared };
            var workflow = new AudioDeviceTestWorkflowOwner(
                new TestAudioDeviceTestPlaybackPort(),
                new DelegateAudioDeviceTestRuntime(request =>
                    AudioDeviceTestResultFactory.CreateSuccessful(
                        request,
                        actualDevice: "resolved-default",
                        actualDeviceName: "Resolved default",
                        actualRate: SampleRate.SAMPLE_RATE_48000Hz,
                        engineFormat: SampleFormat.SAMPLE_FLOAT_32BIT,
                        endpointFormat: SampleFormat.SAMPLE_FLOAT_32BIT,
                        latency: 17)));
            using SettingsDialogViewModel dialog = CreateAudioDeviceTestDialog(
                viewModel,
                settingsSession,
                workflow,
                audioGateway);

            await dialog.RunAudioDeviceTestAsync();

            Assert.IsNull(settings.PlayerDevice);
            Assert.IsNull(settings.PlayerDeviceName);
            Assert.AreEqual(SampleRate.AUTO, settings.PlayerSampleRate);
            Assert.AreEqual(SampleFormat.AUTO, settings.PlayerFormat);
            StringAssert.Contains(dialog.AudioDeviceTestStatusMessage, "WASAPI");
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    /// <summary>typed 初期化失敗を要求機器と試行機器の翻訳済み表示・native stage／error 診断へ分け、modal 通知なしで元設定を保つことを確認します。</summary>
    [TestMethod]
    public async Task AudioDeviceTest_KnownInitializationFailureShowsLocalizedDiagnosticWithoutChangingSettings()
    {
        string root = CreateTemporaryRoot();
        try
        {
            Settings settings = CreateValidStandaloneSettings(root);
            ConfigureExplicitAudioSettings(settings);
            var settingsSession = new CountingSettingsEditSession(settings);
            MainWindowViewModel viewModel = CreateViewModel(settingsSession, firstStartup: false);
            var audioGateway = new TestAudioSettingsGateway
            {
                OutputSelection = new AudioOutputSelection(
                    AudioDriver.WasapiShared,
                    settings.PlayerDevice,
                    settings.PlayerDeviceName)
            };
            var dialogs = new RecordingRootDialogService();
            var workflow = new AudioDeviceTestWorkflowOwner(
                new TestAudioDeviceTestPlaybackPort(),
                new DelegateAudioDeviceTestRuntime(_ => throw new AudioInitializationException(
                    BassAudioPlayer.DeviceDriver.WASAPI_SHARED,
                    BassAudioPlayer.DeviceDriver.WASAPI_SHARED,
                    "BASS_WASAPI_Init",
                    new BassAudioPlayer.DeviceDescriptor("Requested device", "requested-device"),
                    new BassAudioPlayer.DeviceDescriptor("Attempted device B", "attempted-device-b"),
                    "BASSWASAPI",
                    Errors.Device,
                    "WASAPI initialization failed")));
            using SettingsDialogViewModel dialog = CreateAudioDeviceTestDialog(
                viewModel,
                settingsSession,
                workflow,
                audioGateway,
                dialogs);

            await dialog.RunAudioDeviceTestAsync();

            Assert.AreEqual(0, dialogs.MessageCount);
            string expectedMessage = string.Format(
                Resources.AudioDeviceTestInitializationErrorFormat,
                AudioDriverDisplayNames.Get(AudioDriver.WasapiShared),
                "Requested device",
                SampleRate.AUTO,
                SampleFormat.AUTO,
                AudioDriverDisplayNames.Get(AudioDriver.WasapiShared),
                "Attempted device B");
            Assert.AreEqual(expectedMessage, dialog.AudioDeviceTestStatusMessage);
            StringAssert.Contains(dialog.AudioDeviceTestDiagnosticMessage, "BASS_WASAPI_Init");
            StringAssert.Contains(dialog.AudioDeviceTestDiagnosticMessage, "BASS_ERROR_DEVICE");
            AssertExplicitAudioSettingsUnchanged(settings, audioGateway, AudioDriver.WasapiShared);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    /// <summary>初期化と cleanup の同時失敗を両方表示し、未取得の実値を捏造せず有効要求を保ち、例外の私有パスを表示・診断へ漏らさないことを確認します。</summary>
    [TestMethod]
    public async Task AudioDeviceTest_InitializationAndCleanupFailureKeepsEffectiveRequestWithoutActualValues()
    {
        string root = CreateTemporaryRoot();
        try
        {
            Settings settings = CreateValidStandaloneSettings(root);
            ConfigureExplicitAudioSettings(settings);
            var settingsSession = new CountingSettingsEditSession(settings);
            MainWindowViewModel viewModel = CreateViewModel(settingsSession, firstStartup: false);
            var audioGateway = new TestAudioSettingsGateway
            {
                OutputSelection = new AudioOutputSelection(
                    AudioDriver.WasapiShared,
                    settings.PlayerDevice,
                    settings.PlayerDeviceName)
            };
            const string secretPath = @"C:\private\native\audio\device-state.bin";
            var primaryFailure = new AudioInitializationException(
                BassAudioPlayer.DeviceDriver.WASAPI_SHARED,
                BassAudioPlayer.DeviceDriver.WASAPI_SHARED,
                "BASS_WASAPI_Init",
                new BassAudioPlayer.DeviceDescriptor("Requested device", "requested-device"),
                default,
                "BASSWASAPI",
                Errors.Device,
                "WASAPI initialization failed");
            var cleanupFailure = new InvalidOperationException("Cleanup failed for " + secretPath);
            var workflow = new AudioDeviceTestWorkflowOwner(
                new TestAudioDeviceTestPlaybackPort(),
                new DelegateAudioDeviceTestRuntime(request =>
                    new AudioDeviceTestResult(
                        request,
                        initialization: null,
                        streamProgressRequired: false,
                        streamProgressSucceeded: false,
                        TimeSpan.Zero,
                        TimeSpan.Zero,
                        progressRatio: null,
                        failureReason: "Initialization failed before cleanup.",
                        failureKind: AudioDeviceTestFailureKind.Unexpected,
                        primaryFailure: primaryFailure,
                        cleanupFailure: cleanupFailure)));
            using SettingsDialogViewModel dialog = CreateAudioDeviceTestDialog(
                viewModel,
                settingsSession,
                workflow,
                audioGateway);

            await dialog.RunAudioDeviceTestAsync();

            string expectedInitializationFailure = string.Format(
                Resources.AudioDeviceTestInitializationErrorFormat,
                AudioDriverDisplayNames.Get(AudioDriver.WasapiShared),
                "Requested device",
                SampleRate.AUTO,
                SampleFormat.AUTO,
                AudioDriverDisplayNames.Get(AudioDriver.WasapiShared),
                Resources.AudioDeviceDefault);
            Assert.AreEqual(
                Resources.AudioDeviceTestCleanupFailure + Environment.NewLine + expectedInitializationFailure,
                dialog.AudioDeviceTestStatusMessage);
            StringAssert.Contains(dialog.AudioDeviceTestDiagnosticMessage, "BASS_WASAPI_Init");
            StringAssert.Contains(dialog.AudioDeviceTestDiagnosticMessage, "BASS_ERROR_DEVICE");
            StringAssert.Contains(dialog.AudioDeviceTestDiagnosticMessage, "cleanupFailure=InvalidOperationException");
            Assert.IsFalse(dialog.AudioDeviceTestStatusMessage.Contains(secretPath, StringComparison.Ordinal));
            Assert.IsFalse(dialog.AudioDeviceTestDiagnosticMessage.Contains(secretPath, StringComparison.Ordinal));
            AssertExplicitAudioSettingsUnchanged(settings, audioGateway, AudioDriver.WasapiShared);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    /// <summary>実 native cleanup 境界の解放失敗は受付を隔離し、取得済み音声値と構造化 stage／error を保って例外の私有パスを漏らさないことを確認します。</summary>
    [TestMethod]
    public async Task AudioDeviceTest_NativeCleanupFailureKeepsNegotiatedValuesAndSafeStructuredDetails()
    {
        string root = CreateTemporaryRoot();
        try
        {
            Settings settings = CreateValidStandaloneSettings(root);
            ConfigureExplicitAudioSettings(settings);
            var settingsSession = new CountingSettingsEditSession(settings);
            MainWindowViewModel viewModel = CreateViewModel(settingsSession, firstStartup: false);
            var audioGateway = new TestAudioSettingsGateway
            {
                OutputSelection = new AudioOutputSelection(
                    AudioDriver.WasapiShared,
                    settings.PlayerDevice,
                    settings.PlayerDeviceName)
            };
            const string secretPath = @"C:\private\native\audio\session-state.bin";
            var lifecycle = new BassAudioSessionLifecycle();
            var operationGate = new BassAudioOperationGate(initiallyOpen: true);
            var native = new AudioDeviceTestCleanupNativeBoundary("Failed at " + secretPath);

            bool ReleaseSession(BassAudioSession session)
            {
                if (session.IsReleased)
                {
                    return true;
                }
                if (!operationGate.TryEnterSessionCleanup(out BassAudioExclusiveLease exclusive))
                {
                    return false;
                }
                using (exclusive)
                using (lifecycle.Enter())
                {
                    bool released = BassAudioSessionCleanup.Release(session, native);
                    lifecycle.CompleteCleanup(session);
                    exclusive.Complete(released);
                    return released;
                }
            }

            var runtime = new BassAudioDeviceTestRuntime(
                ApplicationPathPolicy.Current,
                new AudioDeviceTestSoundCreationFailureBoundary(),
                (request, acquired) =>
                {
                    using BassAudioExclusiveLease exclusive = operationGate.EnterSessionInitialization();
                    exclusive.ObserveOwnership(() => !lifecycle.HasUnconfirmedOwnership);
                    using (lifecycle.Enter())
                    {
                        Assert.IsTrue(lifecycle.TryBegin(request.AudioOutputRequest, out BassAudioSession session));
                        acquired(session);
                        session.CoreInitialized = true;
                        session.CoreDeviceIndex = 0;
                        session.WasapiInitialized = true;
                        session.WasapiDeviceIndex = 0;
                        session.ActualBackend = BassAudioPlayer.DeviceDriver.WASAPI_SHARED;
                        session.ActualDevice = new BassAudioPlayer.DeviceDescriptor("Actual device", "actual-device");
                        session.MixerHandle = 201;
                        session.OutputHandle = 202;
                        session.NegotiationResult = new BassAudioBackendResult(
                            new BassAudioNegotiationRequest(
                                session.ActualBackend,
                                session.ActualDevice,
                                SampleRate.AUTO,
                                SampleFormat.AUTO,
                                10),
                            session.ActualDevice,
                            SampleRate.SAMPLE_RATE_48000Hz,
                            SampleFormat.SAMPLE_FLOAT_32BIT,
                            SampleFormat.SAMPLE_INT_24BIT,
                            16.5,
                            session.MixerHandle,
                            [],
                            null,
                            endpointContainerBits: 24,
                            endpointEffectiveBits: 24);
                        lifecycle.MarkActive(session);
                    }
                },
                ReleaseSession,
                operationGate);
            var workflow = new AudioDeviceTestWorkflowOwner(new TestAudioDeviceTestPlaybackPort(), runtime);
            using SettingsDialogViewModel dialog = CreateAudioDeviceTestDialog(
                viewModel,
                settingsSession,
                workflow,
                audioGateway);

            await dialog.RunAudioDeviceTestAsync();

            Assert.AreEqual(2, native.StreamFreeCount);
            Assert.IsTrue(lifecycle.HasCleanupPending);
            Assert.IsTrue(operationGate.IsCleanupQuarantined);
            Assert.AreEqual(Resources.AudioDeviceTestCleanupFailure,
                dialog.AudioDeviceTestStatusMessage.Split(Environment.NewLine)[0]);
            StringAssert.Contains(dialog.AudioDeviceTestStatusMessage, "Actual device");
            StringAssert.Contains(
                dialog.AudioDeviceTestStatusMessage,
                string.Format(Resources.AudioSampleRateOptionFormat, (int)SampleRate.SAMPLE_RATE_48000Hz));
            StringAssert.Contains(dialog.AudioDeviceTestStatusMessage, Resources.AudioSampleFormatFloat32);
            StringAssert.Contains(dialog.AudioDeviceTestStatusMessage, Resources.AudioSampleFormat24Bit);
            StringAssert.Contains(dialog.AudioDeviceTestStatusMessage, "16.5");
            StringAssert.Contains(dialog.AudioDeviceTestDiagnosticMessage, "cleanupFailure=InvalidOperationException");
            StringAssert.Contains(dialog.AudioDeviceTestDiagnosticMessage, "stage=BASS_StreamFree(");
            StringAssert.Contains(dialog.AudioDeviceTestDiagnosticMessage, "nativeErrorSource=BASS");
            StringAssert.Contains(dialog.AudioDeviceTestDiagnosticMessage, "BASS_ERROR_UNKNOWN");
            StringAssert.Contains(dialog.AudioDeviceTestDiagnosticMessage, "exceptionType=InvalidOperationException");
            Assert.IsFalse(dialog.AudioDeviceTestStatusMessage.Contains(secretPath, StringComparison.Ordinal));
            Assert.IsFalse(dialog.AudioDeviceTestDiagnosticMessage.Contains(secretPath, StringComparison.Ordinal));
            AssertExplicitAudioSettingsUnchanged(settings, audioGateway, AudioDriver.WasapiShared);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    /// <summary>MixerAttach の typed 失敗を局所表示と native stage／HANDLE 診断へ届け、modal 通知や保存音声値の変更を行わないことを確認します。</summary>
    [TestMethod]
    public async Task AudioDeviceTest_PlaybackStartFailureIsShownWithStageAndNativeError()
    {
        string root = CreateTemporaryRoot();
        try
        {
            Settings settings = CreateValidStandaloneSettings(root);
            ConfigureExplicitAudioSettings(settings);
            var settingsSession = new CountingSettingsEditSession(settings);
            MainWindowViewModel viewModel = CreateViewModel(settingsSession, firstStartup: false);
            var audioGateway = new TestAudioSettingsGateway
            {
                OutputSelection = new AudioOutputSelection(
                    AudioDriver.WasapiShared,
                    settings.PlayerDevice,
                    settings.PlayerDeviceName)
            };
            var dialogs = new RecordingRootDialogService();
            var workflow = new AudioDeviceTestWorkflowOwner(
                new TestAudioDeviceTestPlaybackPort(),
                new DelegateAudioDeviceTestRuntime(request =>
                    AudioDeviceTestResultFactory.CreateSuccessful(
                        request,
                        streamProgressSucceeded: false,
                        failureKind: AudioDeviceTestFailureKind.PlaybackStartFailed,
                        playbackStage: BassAudioPlaybackStage.MixerAttach,
                        nativeErrorSource: "BASS_Mixer_StreamAddChannel",
                        nativeErrorCode: Errors.Handle)));
            using SettingsDialogViewModel dialog = CreateAudioDeviceTestDialog(
                viewModel,
                settingsSession,
                workflow,
                audioGateway,
                dialogs);

            await dialog.RunAudioDeviceTestAsync();

            Assert.AreEqual(Resources.AudioDeviceTestPlaybackFailureReason,
                dialog.AudioDeviceTestStatusMessage.Split(Environment.NewLine)[0]);
            StringAssert.Contains(dialog.AudioDeviceTestDiagnosticMessage, "MixerAttach");
            StringAssert.Contains(dialog.AudioDeviceTestDiagnosticMessage, "BASS_Mixer_StreamAddChannel");
            StringAssert.Contains(dialog.AudioDeviceTestDiagnosticMessage, "BASS_ERROR_HANDLE");
            Assert.AreEqual(0, dialogs.MessageCount);
            AssertExplicitAudioSettingsUnchanged(settings, audioGateway);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    /// <summary>SourceCreate の typed 失敗を生成失敗表示と FILEOPEN 診断へ届け、再生開始失敗と混同せず保存音声値を保つことを確認します。</summary>
    [TestMethod]
    public async Task AudioDeviceTest_PlayerCreationFailureIsShownWithStageAndNativeError()
    {
        string root = CreateTemporaryRoot();
        try
        {
            Settings settings = CreateValidStandaloneSettings(root);
            ConfigureExplicitAudioSettings(settings);
            var settingsSession = new CountingSettingsEditSession(settings);
            MainWindowViewModel viewModel = CreateViewModel(settingsSession, firstStartup: false);
            var audioGateway = new TestAudioSettingsGateway
            {
                OutputSelection = new AudioOutputSelection(
                    AudioDriver.WasapiShared,
                    settings.PlayerDevice,
                    settings.PlayerDeviceName)
            };
            var dialogs = new RecordingRootDialogService();
            var workflow = new AudioDeviceTestWorkflowOwner(
                new TestAudioDeviceTestPlaybackPort(),
                new DelegateAudioDeviceTestRuntime(request =>
                    AudioDeviceTestResultFactory.CreateSuccessful(
                        request,
                        streamProgressSucceeded: false,
                        failureKind: AudioDeviceTestFailureKind.PlayerCreationFailed,
                        playbackStage: BassAudioPlaybackStage.SourceCreate,
                        nativeErrorSource: "BASS_StreamCreateFile",
                        nativeErrorCode: Errors.FileOpen)));
            using SettingsDialogViewModel dialog = CreateAudioDeviceTestDialog(
                viewModel,
                settingsSession,
                workflow,
                audioGateway,
                dialogs);

            await dialog.RunAudioDeviceTestAsync();

            StringAssert.Contains(dialog.AudioDeviceTestStatusMessage, Resources.AudioDeviceTestPlayerCreationFailureReason);
            StringAssert.Contains(dialog.AudioDeviceTestDiagnosticMessage, "SourceCreate");
            StringAssert.Contains(dialog.AudioDeviceTestDiagnosticMessage, "BASS_StreamCreateFile");
            StringAssert.Contains(dialog.AudioDeviceTestDiagnosticMessage, "BASS_ERROR_FILEOPEN");
            Assert.AreEqual(0, dialogs.MessageCount);
            AssertExplicitAudioSettingsUnchanged(settings, audioGateway);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    /// <summary>予期しない runtime 例外を局所失敗表示と例外型の診断に変換し、modal 通知なしで音声設定を保つことを確認します。</summary>
    [TestMethod]
    public async Task AudioDeviceTest_UnexpectedRuntimeFailureShowsLocalDiagnosticWithoutDialog()
    {
        string root = CreateTemporaryRoot();
        try
        {
            Settings settings = CreateValidStandaloneSettings(root);
            ConfigureExplicitAudioSettings(settings);
            var settingsSession = new CountingSettingsEditSession(settings);
            MainWindowViewModel viewModel = CreateViewModel(settingsSession, firstStartup: false);
            var audioGateway = new TestAudioSettingsGateway
            {
                OutputSelection = new AudioOutputSelection(
                    AudioDriver.WasapiShared,
                    settings.PlayerDevice,
                    settings.PlayerDeviceName)
            };
            var dialogs = new RecordingRootDialogService();
            var workflow = new AudioDeviceTestWorkflowOwner(
                new TestAudioDeviceTestPlaybackPort(),
                new DelegateAudioDeviceTestRuntime(_ => throw new InvalidOperationException("unexpected test failure")));
            using SettingsDialogViewModel dialog = CreateAudioDeviceTestDialog(
                viewModel,
                settingsSession,
                workflow,
                audioGateway,
                dialogs);

            await dialog.RunAudioDeviceTestAsync();

            Assert.AreEqual(Resources.AudioDeviceTestUnexpectedFailureReason, dialog.AudioDeviceTestStatusMessage);
            Assert.AreEqual(0, dialogs.MessageCount);
            StringAssert.Contains(dialog.AudioDeviceTestDiagnosticMessage, "InvalidOperationException");
            AssertExplicitAudioSettingsUnchanged(settings, audioGateway);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    /// <summary>score 保留・起動操作の block に再同期可否が追従し、無関係な title 変更では再通知せず、Dispose 後は可否購読を解除することを確認します。</summary>
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

            InvokePrivateMethod(dialog, "SetScoreReloadPending", true);

            Assert.IsFalse(dialog.CanRequestLr2SongDbSyncDataResync);
            CollectionAssert.Contains(changedProperties, nameof(dialog.CanRequestLr2SongDbSyncDataResync));

            changedProperties.Clear();
            InvokePrivateMethod(dialog, "SetScoreReloadPending", false);
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

    /// <summary>BMS一覧の局所確定は実差分を同じL/Pで処理し、DBと画面へ今回の譜面を反映するまで閉鎖を要求しません。</summary>
    /// <remarks>初期化後の独立保守と区別し、当該確定Taskの実終端でDBと必須UIを観測します。</remarks>
    [DataTestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public void BmsConfirmation_CompletesRealFileDiffBeforeClosing(bool lr2)
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
                var scanner = new SettingsChartFileScanner(CapturedChartFileScanner.FromFixture([chartA],
                    new Dictionary<string, IEnumerable<string>> { [initialRoot] = [] }, [initialRoot]));
                var ui = new TestUiScheduler(() => TestUiDispatcherHost.Dispatcher);
                int defaultPlayerCount = 0;
                var composition = new ApplicationComposition(settingsEditSession: session, reportSettingsApplyFailure: failures.Add,
                    uiScheduler: ui, applicationLifetime: TestApplicationContext.CreateLifetime(firstStartup: false),
                    cultureCatalog: TestApplicationContext.CreateCultureCatalog(), applicationPathSnapshot: ApplicationPathSnapshot.FromExecutablePath(Path.Combine(root, "BeMusicSeeker.exe")),
                    chartFileScanner: scanner, rootFileEnumerator: new FastRootFileEnumerator(),
                    defaultBmsPlayerFactory: () => { defaultPlayerCount++; return new RecordingPlaybackPlayer(); });
                var factory = new StartupLibraryConstructionTestSupport.RecordingDelegatingStartupLibraryFactory(composition, []) { InitialSearchTargets = lr2 ? [] : [initialRoot] };
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
                try
                {
                    Task<bool> initialize = owner.InitializeAsync();
                    TestUiDispatcherHost.AwaitTaskOnDispatcher(initialize, "folder-settings-initialize");
                    Assert.IsTrue(initialize.GetAwaiter().GetResult());
                    if (lr2) { AwaitStartupBackgroundCompletionOnDispatcher(owner, "lr2-confirm-initial-background"); }
                    Assert.AreEqual(1, defaultPlayerCount, "単独動作の実構築は既定playerを一回接続します。");
                    File.WriteAllText(chartB, "#PLAYER 1\n#TITLE Folder B\n#BPM 120\n#00111:01\n");
                    var next = CapturedChartFileScanner.FromFixture([chartA, chartB],
                        new Dictionary<string, IEnumerable<string>> { [initialRoot] = [], [addedRoot] = [] }, [initialRoot]);
                    next.ScanObserved = () => { settingsLibraryTerminal = composition.OperationAdmission.WaitForIdleAsync(); settingsPlaylistTerminal = composition.PlaylistOperationAdmission.WaitForIdleAsync(); entered.TrySetResult(); release.Wait(); };
                    scanner.Current = next;
                    var capturedRoots = new List<IReadOnlyList<string>>();
                    scanner.RootsObserved = roots => capturedRoots.Add(roots);
                    owner.SettingDialog.BmsRootPathList.Add(addedRoot);
                    apply = owner.SettingDialog.CommitBmsRootsAsync();
                    TestUiDispatcherHost.AwaitTaskOnDispatcher(TestUiDispatcherHost.AwaitNotificationAsync(entered.Task, apply, "folder-settings-scan"), "folder-settings-scan");
                    Assert.IsFalse(apply.IsCompleted);
                    Assert.IsTrue(composition.OperationAdmission.IsActive);
                    Assert.IsTrue(composition.PlaylistOperationAdmission.IsActive);
                    CollectionAssert.DoesNotContain(presentation.Requests, "close");
                    Assert.IsNotNull(settingsLibraryTerminal);
                    Assert.IsNotNull(settingsPlaylistTerminal);
                    Assert.IsFalse(settingsLibraryTerminal.IsCompleted);
                    Assert.IsFalse(settingsPlaylistTerminal.IsCompleted);
                    release.Set();
                    TestUiDispatcherHost.AwaitTaskOnDispatcher(apply, "folder-settings-terminal");
                    Assert.AreEqual(0, failures.Count, string.Join(Environment.NewLine, failures));
                    Assert.AreEqual(1, defaultPlayerCount, "Folder段階はplayerの再構築を追加しません。");
                    BMSLibrary library = factory.CreatedLibrary ?? throw new InvalidOperationException("Actual library is unavailable.");
                    Assert.AreEqual("Folder B", library.BmsCharts.Single(chart => chart.Path == chartB).RawTitle);
                    using (LR2SongDBExtended db = new BmsLibraryDbGateway(lr2 ? values.LR2SongDBPath : composition.ApplicationPathSnapshot.StandaloneSongDbPath).OpenSongDbReadOnly())
                    { Assert.AreEqual("Folder B", db.Table<LR2SongDB.song>().Single(row => row.path == chartB).title); }
                    if (lr2)
                    {
                        BMSPlaylist playlist = factory.CreatedPlaylist ?? throw new InvalidOperationException("Actual playlist is unavailable.");
                        Assert.IsTrue(capturedRoots.Any(roots => roots.Contains(addedRoot, StringComparer.OrdinalIgnoreCase)), "同じ確定操作の実走査へ新登録を渡します。");
                        CollectionAssert.Contains(composition.ResolveLr2Config(values.LR2ConfigXmlPath).Capture().RegisteredRoots.ToArray(), addedRoot);
                        CollectionAssert.Contains(new LR2Config(values.LR2ConfigXmlPath).GetBMSSearchDirectoriesForChangeTracking(), addedRoot);
                    }
                    Assert.IsTrue(owner.MainChartList.Rows.OfType<LibraryChartRow>().Any(row => row.Title == "Folder B"));
                    Assert.IsTrue(settingsLibraryTerminal.IsCompletedSuccessfully);
                    Assert.IsTrue(settingsPlaylistTerminal.IsCompletedSuccessfully);
                    if (lr2)
                    {
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
                    finally { TestUiDispatcherHost.AwaitTaskOnDispatcher(owner.ShellShutdownWorkflow.RequestWindowCloseAsync(), "folder-settings-cleanup"); owner.SettingDialog.Dispose(); }
                }
            });
        }
        finally { release.Set(); Directory.Delete(root, recursive: true); }
    }

    /// <summary>無効な検索ルートでは通常起動だけが初回言語選択／設定再表示を要求し、設定経由では重ねて表示せず、進捗 block と profile を残さないことを確認します。</summary>
    [DataTestMethod]
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
    [DataTestMethod]
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
            StartupLibraryConstructionTestSupport.CreateSongDatabase(songDbPath);
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
                    applicationPathSnapshot: ApplicationPathSnapshot.FromExecutablePath(Path.Combine(root, "application", "BeMusicSeeker.exe")),
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
    [DataTestMethod]
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
            using (var db = new SQLiteConnection(songDbPath, storeDateTimeAsTicks: true))
            {
                db.CreateTable<LR2SongDB.song>();
                db.CreateTable<LR2SongDB.folder>();
            }
            new BmsLibraryDbGateway(songDbPath).EnsureAppOwnedSchema();
            PlaylistPersistenceRepository.EnsureSchema(songDbPath);
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
                    applicationPathSnapshot: applicationPath,
                    rootFileEnumerator: new FastRootFileEnumerator(),
                    fileDbMutationDialogService: dialogs);
                Lr2ConfigOwner configOwner = composition.ResolveLr2Config(settings.LR2ConfigXmlPath);
                var library = new TestBmsLibrary(
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
                TestBmsPlaylist playlist = MainWindowViewModelTestFactory.CreatePlaylist(songDbPath, settings, configOwner, library: library);
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
    [DataTestMethod]
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
                applicationPathSnapshot: applicationPath,
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
                applicationPathSnapshot: applicationPath,
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
                StandaloneLibraryDatabase.EnsurePortableSongDb(applicationPath);
            PlaylistPersistenceRepository.EnsureSchema(database.SongDbPath);
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
                    applicationPathSnapshot: applicationPath,
                    fileDbMutationDialogService: dialogs);
                admission = composition.OperationAdmission;
                var library = new TestBmsLibrary(
                    database.SongDbPath,
                    getLR2Config: null,
                    _lr2ScoreDB: null,
                    startupRequiredFileScanReason: null,
                    optionsSnapshotProvider: () => BmsLibraryOptionsSnapshot.CreateCurrent(settings),
                    applicationPathSnapshot: TestBmsFactory.MissingEverythingBridge,
                    chartFileScanner: scanner,
                    operationAdmission: composition.OperationAdmission,
                    playlistOperationAdmission: composition.PlaylistOperationAdmission);
                TestBmsPlaylist playlist = MainWindowViewModelTestFactory.CreatePlaylist(database.SongDbPath, settings, library: library);
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
    [DataTestMethod]
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
            StandaloneLibraryDatabaseEnsureResult database = StandaloneLibraryDatabase.EnsurePortableSongDb(applicationPath);
            PlaylistPersistenceRepository.EnsureSchema(database.SongDbPath);
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
                    cultureCatalog: TestApplicationContext.CreateCultureCatalog(), applicationPathSnapshot: applicationPath,
                    fileDbMutationDialogService: dialogs);
                library = new TestBmsLibrary(database.SongDbPath, null, null, null,
                    () => BmsLibraryOptionsSnapshot.CreateCurrent(settings), TestBmsFactory.MissingEverythingBridge,
                    operationAdmission: composition.OperationAdmission, playlistOperationAdmission: composition.PlaylistOperationAdmission,
                    uiScheduler: scheduler);
                playlist = MainWindowViewModelTestFactory.CreatePlaylist(database.SongDbPath, settings, library: library);
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
    [DataTestMethod]
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
            StandaloneLibraryDatabaseEnsureResult database = StandaloneLibraryDatabase.EnsurePortableSongDb(applicationPath);
            PlaylistPersistenceRepository.EnsureSchema(database.SongDbPath);
            var dialogs = new RecordingRootDialogService();
            var lifetime = new TestApplicationLifetime(firstStartup: true);
            var sequence = new List<string>();
            TestUiDispatcherHost.Invoke(() =>
            {
                var scheduler = new TestUiScheduler(() => TestUiDispatcherHost.Dispatcher);
                var composition = new ApplicationComposition(settingsEditSession: new CountingSettingsEditSession(settings),
                    uiScheduler: scheduler, applicationLifetime: lifetime, cultureCatalog: TestApplicationContext.CreateCultureCatalog(),
                    applicationPathSnapshot: applicationPath, fileDbMutationDialogService: dialogs, defaultBmsPlayerFactory: () => new RecordingPlaybackPlayer());
                library = new TestBmsLibrary(database.SongDbPath, null, null, null,
                    () => BmsLibraryOptionsSnapshot.CreateCurrent(settings), TestBmsFactory.MissingEverythingBridge,
                    operationAdmission: composition.OperationAdmission, playlistOperationAdmission: composition.PlaylistOperationAdmission,
                    uiScheduler: scheduler);
                playlist = MainWindowViewModelTestFactory.CreatePlaylist(database.SongDbPath, settings, library: library);
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
                    var requiredOwner = new StartupLibraryInitializationWorkflowOwner(semaphore);
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
                StandaloneLibraryDatabase.EnsurePortableSongDb(applicationPath);
            PlaylistPersistenceRepository.EnsureSchema(database.SongDbPath);
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
                    applicationPathSnapshot: applicationPath,
                    fileDbMutationDialogService: dialogs);
                var library = new TestBmsLibrary(
                    database.SongDbPath,
                    getLR2Config: null,
                    _lr2ScoreDB: null,
                    startupRequiredFileScanReason: null,
                    optionsSnapshotProvider: () => BmsLibraryOptionsSnapshot.CreateCurrent(settings),
                    applicationPathSnapshot: TestBmsFactory.MissingEverythingBridge,
                    chartFileScanner: scanner,
                    operationAdmission: composition.OperationAdmission,
                    playlistOperationAdmission: composition.PlaylistOperationAdmission);
                TestBmsPlaylist playlist = MainWindowViewModelTestFactory.CreatePlaylist(database.SongDbPath, settings, library: library);
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
    [DataTestMethod]
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
            dialog = CreateViewModel(session, firstStartup: false, dialogs: dialogs).SettingDialog;
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

    /// <summary>スコア組の確定から同じL/Pを実ScoreOnlyモデルへ渡し、実親解放後の純通知と必須UIの実終端まで同じWindowの受理Taskを追跡します。</summary>
    /// <remarks>実compiled選択からWindowの受理Taskへ接続し、実設定・モデル・UI完了を確認します。純通知を保持点にせず既存UI gateで実Taskを待ちます。</remarks>
    [DataTestMethod]
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
            using var db = new SQLite.SQLiteConnection(Path.Combine(playerDirectory, "score.db"));
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
                    applicationPathSnapshot: TestBmsFactory.AvailableEverythingBridge,
                    rootFileEnumerator: new FastRootFileEnumerator());
                var factory = new StartupLibraryConstructionTestSupport.RecordingDelegatingStartupLibraryFactory(composition, [])
                { InitialSearchTargets = [music] };
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
                    apply = window.WaitForOperationCompletionAsync();
                    TestUiDispatcherHost.AwaitTaskOnDispatcher(Task.WhenAny(completionUi.Entered.Task, apply), "score-only-real-publish-or-failure");
                    if (!completionUi.Entered.Task.IsCompleted) { TestUiDispatcherHost.AwaitTaskOnDispatcher(apply, "score-only-real-early-terminal"); Assert.Fail(string.Join(Environment.NewLine, failures)); }
                    Assert.IsFalse(apply.IsCompleted);
                    window.Close();
                    Assert.IsTrue(window.IsVisible);
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
                    if (changeRoot)
                    {
                        // 取込みbuttonは、未適用root候補ではなく確定rootの空URL一覧を読みます。
                        var importNotices = new List<string>();
                        int confirmations = 0;
                        viewModel.PlaylistWorkspace.BeatorajaTableUrlImportConfirmationRequested += (_, request) => { confirmations++; request.Confirmed = true; };
                        viewModel.PlaylistWorkspace.BeatorajaTableUrlImportNotificationRequested += (_, request) => importNotices.Add(request.Message);
                        viewModel.SettingDialog.BeatorajaRootPath = Path.Combine(root, "unconfirmed-root");
                        FindDescendants<Button>(window).Single(button => System.Windows.Automation.AutomationProperties.GetAutomationId(button) == "SettingsImportBeatorajaTableUrls")
                            .RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                        Assert.AreEqual(1, confirmations);
                        CollectionAssert.AreEqual(new[] { Resources.Beatoraja_table_url_import_no_urls }, importNotices);
                        Assert.AreEqual(nextBeatoraja, values.BeatorajaRootPath);
                    }
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

    /// <summary>実開始buttonの連打は一回だけ受理し、必須UI待ちで親権限の解放と同WindowのClose抑止を確認します。UI失敗は同Windowで修復再開始でき、成功後は別に任意背景の実終端とL/P解放を待ちます。</summary>
    [DataTestMethod]
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
                    applicationPathSnapshot: TestBmsFactory.MissingEverythingBridge,
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
                    apply = window.WaitForOperationCompletionAsync();
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
                        apply = window.WaitForOperationCompletionAsync();
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
                    using LR2SongDBExtended db = new BmsLibraryDbGateway(values.LR2SongDBPath).OpenSongDbReadOnly();
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

    /// <summary>標準／個別の欠損 DB を同じ path に復旧して再選択すると status を通知・更新し、path tuple と config を保存せず保つことを確認します。</summary>
    [DataTestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public void Lr2SongDbPicker_ReselectingRestoredFileRefreshesStatusWithoutSaving(bool useIndividualPath)
    {
        string scope = CreateTemporaryRoot();
        try
        {
            string root = Path.Combine(scope, "root");
            (string standardSong, string configPath) = CreateValidLr2Layout(root);
            string songPath = useIndividualPath ? Path.Combine(scope, "songs.db") : standardSong;
            File.WriteAllBytes(songPath, []);
            File.Delete(songPath);
            byte[] configBefore = File.ReadAllBytes(configPath);

            Settings values = CreateValidStandaloneSettings(scope);
            values.OperationModeLR2DB = true;
            values.LR2RootPath = root;
            values.LR2SongDBPath = songPath;
            values.LR2ConfigXmlPath = configPath;
            var session = new CountingSettingsEditSession(values);
            using SettingsDialogViewModel draft = CreateViewModel(session, firstStartup: false).SettingDialog;
            Assert.AreEqual(Resources.Settings_path_missing, draft.Lr2SongDbPathStatusText);
            Assert.IsFalse(draft.HasLr2PathSelectionError);
            var notifications = new List<string>();
            draft.PropertyChanged += (_, args) => notifications.Add(args.PropertyName ?? string.Empty);

            File.WriteAllBytes(songPath, []);
            draft.SetFilePathFromPicker(nameof(draft.LR2SongDBPath), songPath);

            CollectionAssert.Contains(notifications, nameof(draft.Lr2SongDbPathStatusText));
            CollectionAssert.Contains(notifications, nameof(draft.Lr2SongDbPathStatusKind));
            CollectionAssert.Contains(notifications, nameof(draft.Lr2SongDbPathStatusIcon));
            Assert.AreEqual("Success", draft.Lr2SongDbPathStatusKind);
            Assert.AreEqual(
                useIndividualPath
                    ? Resources.Settings_path_detected_from_individual_setting
                    : Resources.Settings_path_detected_from_lr2_root,
                draft.Lr2SongDbPathStatusText);
            Assert.AreEqual(root, draft.LR2RootPath);
            Assert.AreEqual(songPath, draft.LR2SongDBPath);
            Assert.AreEqual(configPath, draft.LR2ConfigXmlPath);
            Assert.IsFalse(draft.HasLr2PathSelectionError);
            Assert.AreEqual(0, session.SaveCount);
            CollectionAssert.AreEqual(configBefore, File.ReadAllBytes(configPath));
        }
        finally
        {
            Directory.Delete(scope, recursive: true);
        }
    }

    /// <summary>個別 path と標準 song.db／config.xmh を往復選択すると現 path の由来を表示し、過去の選択履歴を引き継ぐ誤りを検出します。</summary>
    [TestMethod]
    public void Lr2PathSource_UsesCurrentStandardLayoutInsteadOfSelectionHistory()
    {
        string scope = CreateTemporaryRoot();
        try
        {
            string root = Path.Combine(scope, "root");
            (string standardSong, _) = CreateValidLr2Layout(root);
            string standardXmh = Path.Combine(root, "LR2files", "Config", "config.xmh");
            File.WriteAllText(standardXmh, "<config><system /><jukebox /></config>");
            string individualSong = Path.Combine(scope, "individual", "database", "songs.db");
            string individualConfig = Path.Combine(scope, "individual", "configuration", "config.xml");
            Directory.CreateDirectory(Path.GetDirectoryName(individualSong)!);
            Directory.CreateDirectory(Path.GetDirectoryName(individualConfig)!);
            File.WriteAllBytes(individualSong, []);
            File.WriteAllText(individualConfig, "<config><system /><jukebox /></config>");

            Settings values = CreateValidStandaloneSettings(scope);
            values.LR2RootPath = root;
            values.LR2SongDBPath = individualSong;
            values.LR2ConfigXmlPath = individualConfig;
            var session = new CountingSettingsEditSession(values);
            using SettingsDialogViewModel dialog = CreateViewModel(session, firstStartup: false).SettingDialog;

            Assert.AreEqual(Resources.Settings_path_detected_from_individual_setting, dialog.Lr2SongDbPathStatusText);
            Assert.AreEqual(Resources.Settings_path_detected_from_individual_setting, dialog.Lr2ConfigPathStatusText);

            dialog.SetFilePathFromPicker(nameof(dialog.LR2SongDBPath), standardSong);
            dialog.SetFilePathFromPicker(nameof(dialog.LR2ConfigXmlPath), standardXmh);

            Assert.AreEqual(Resources.Settings_path_detected_from_lr2_root, dialog.Lr2SongDbPathStatusText);
            Assert.AreEqual(Resources.Settings_path_detected_from_lr2_root, dialog.Lr2ConfigPathStatusText);

            dialog.SetFilePathFromPicker(nameof(dialog.LR2SongDBPath), individualSong);
            dialog.SetFilePathFromPicker(nameof(dialog.LR2ConfigXmlPath), individualConfig);

            Assert.AreEqual(Resources.Settings_path_detected_from_individual_setting, dialog.Lr2SongDbPathStatusText);
            Assert.AreEqual(Resources.Settings_path_detected_from_individual_setting, dialog.Lr2ConfigPathStatusText);
        }
        finally
        {
            Directory.Delete(scope, recursive: true);
        }
    }

    /// <summary>別の有効 LR2 root を選択すると root・DB・config の標準 tuple と由来表示をまとめて置換し、旧 DB だけを保持する誤りを検出します。</summary>
    [TestMethod]
    public void Lr2RootSelection_ReplacesTheWholeStandardTupleWithoutKeepingStaleSongDb()
    {
        string scope = CreateTemporaryRoot();
        string firstRoot = Path.Combine(scope, "first");
        string nextRoot = Path.Combine(scope, "next");
        try
        {
            (string firstSong, string firstConfig) = CreateValidLr2Layout(firstRoot);
            (string nextSong, string nextConfig) = CreateValidLr2Layout(nextRoot);
            Settings values = CreateValidStandaloneSettings(scope);
            values.LR2RootPath = firstRoot;
            values.LR2SongDBPath = firstSong;
            values.LR2ConfigXmlPath = firstConfig;
            var session = new CountingSettingsEditSession(values);
            using SettingsDialogViewModel dialog = CreateViewModel(session, firstStartup: false).SettingDialog;

            dialog.LR2RootPath = nextRoot;

            Assert.AreEqual(nextRoot, dialog.LR2RootPath);
            Assert.AreEqual(nextSong, dialog.LR2SongDBPath);
            Assert.AreEqual(nextConfig, dialog.LR2ConfigXmlPath);
            Assert.AreEqual(Resources.Settings_path_detected_from_lr2_root, dialog.Lr2SongDbPathStatusText);
            Assert.AreEqual(Resources.Settings_path_detected_from_lr2_root, dialog.Lr2ConfigPathStatusText);
        }
        finally
        {
            Directory.Delete(scope, recursive: true);
        }
    }

    /// <summary>壊れた config 候補を拒否して元の有効 path と成功状態を保ち、候補拒否の診断を別に出すことを確認します。</summary>
    [TestMethod]
    public void Lr2ConfigPicker_MalformedCandidateKeepsPreviousRawPathAndReportsFailure()
    {
        string scope = CreateTemporaryRoot();
        try
        {
            string root = Path.Combine(scope, "root");
            (string song, string config) = CreateValidLr2Layout(root);
            string malformedConfig = Path.Combine(scope, "malformed", "config.xml");
            Directory.CreateDirectory(Path.GetDirectoryName(malformedConfig)!);
            File.WriteAllText(malformedConfig, "<config>");
            Settings values = CreateValidStandaloneSettings(scope);
            values.LR2RootPath = root;
            values.LR2SongDBPath = song;
            values.LR2ConfigXmlPath = config;
            var session = new CountingSettingsEditSession(values);
            using SettingsDialogViewModel draft = CreateViewModel(session, firstStartup: false).SettingDialog;

            draft.SetFilePathFromPicker(nameof(draft.LR2ConfigXmlPath), malformedConfig);

            Assert.AreEqual(config, draft.LR2ConfigXmlPath);
            Assert.IsTrue(draft.HasLr2PathSelectionError);
            StringAssert.Contains(draft.Lr2PathSelectionError, Resources.Error_InvalidLR2SongDbOrConfigPath);
            Assert.AreEqual("Success", draft.Lr2ConfigPathStatusKind);
        }
        finally
        {
            Directory.Delete(scope, recursive: true);
        }
    }

    /// <summary>不正 root 候補を拒否して root／DB／config 全 tuple を保ち、root 選択エラーを出すことを確認します。</summary>
    [TestMethod]
    public void Lr2RootPicker_InvalidCandidateLeavesWholeTupleUnchangedAndReportsFailure()
    {
        string scope = CreateTemporaryRoot();
        try
        {
            string root = Path.Combine(scope, "root");
            (string song, string config) = CreateValidLr2Layout(root);
            Settings values = CreateValidStandaloneSettings(scope);
            values.LR2RootPath = root;
            values.LR2SongDBPath = song;
            values.LR2ConfigXmlPath = config;
            var session = new CountingSettingsEditSession(values);
            using SettingsDialogViewModel draft = CreateViewModel(session, firstStartup: false).SettingDialog;

            draft.SetRootFolderPathFromPicker(nameof(draft.LR2RootPath), Path.Combine(scope, "invalid"));

            Assert.AreEqual(root, draft.LR2RootPath);
            Assert.AreEqual(song, draft.LR2SongDBPath);
            Assert.AreEqual(config, draft.LR2ConfigXmlPath);
            Assert.IsTrue(draft.HasLr2PathSelectionError);
            StringAssert.Contains(draft.Lr2PathSelectionError, Resources.Error_InvalidLR2RootPath);
        }
        finally
        {
            Directory.Delete(scope, recursive: true);
        }
    }

    /// <summary>同じ有効rootの再選択では採用済み個別XMLを維持し、DBの標準配置への復帰とXMLの個別指定の表示を区別します。</summary>
    [TestMethod]
    public void Lr2RootPicker_ReselectingSameRootKeepsAdoptedXmlAndRestoresStandardSongDb()
    {
        string scope = CreateTemporaryRoot();
        try
        {
            string root = Path.Combine(scope, "root");
            (string standardSong, string standardConfig) = CreateValidLr2Layout(root);
            string customSong = Path.Combine(scope, "custom", "song.db");
            string customConfig = Path.Combine(scope, "custom", "config.xml");
            Directory.CreateDirectory(Path.GetDirectoryName(customSong)!);
            File.WriteAllBytes(customSong, []);
            File.WriteAllText(customConfig, "<config><system /><jukebox /></config>");
            Settings values = CreateValidStandaloneSettings(scope);
            values.LR2RootPath = root;
            values.LR2SongDBPath = customSong;
            values.LR2ConfigXmlPath = customConfig;
            var session = new CountingSettingsEditSession(values);
            using SettingsDialogViewModel draft = CreateViewModel(session, firstStartup: false).SettingDialog;

            draft.SetRootFolderPathFromPicker(nameof(draft.LR2RootPath), root);

            Assert.AreEqual(root, draft.LR2RootPath);
            Assert.AreEqual(standardSong, draft.LR2SongDBPath);
            Assert.AreEqual(customConfig, draft.LR2ConfigXmlPath);
            Assert.AreEqual(Resources.Settings_path_detected_from_lr2_root, draft.Lr2SongDbPathStatusText);
            Assert.AreEqual(Resources.Settings_path_detected_from_individual_setting, draft.Lr2ConfigPathStatusText);
            Assert.IsFalse(draft.HasLr2PathSelectionError);
        }
        finally
        {
            Directory.Delete(scope, recursive: true);
        }
    }

    /// <summary>不正 root の拒否後に現在の有効 root を再選択すると拒否状態だけを解除し、元 tuple を保つことを確認します。</summary>
    [TestMethod]
    public void Lr2RootPicker_ReselectingCurrentValidRootClearsPriorInvalidSelectionError()
    {
        string scope = CreateTemporaryRoot();
        try
        {
            string root = Path.Combine(scope, "root");
            (string song, string config) = CreateValidLr2Layout(root);
            Settings values = CreateValidStandaloneSettings(scope);
            values.LR2RootPath = root;
            values.LR2SongDBPath = song;
            values.LR2ConfigXmlPath = config;
            var session = new CountingSettingsEditSession(values);
            using SettingsDialogViewModel draft = CreateViewModel(session, firstStartup: false).SettingDialog;

            draft.SetRootFolderPathFromPicker(nameof(draft.LR2RootPath), Path.Combine(scope, "invalid"));
            Assert.IsTrue(draft.HasLr2PathSelectionError);

            draft.SetRootFolderPathFromPicker(nameof(draft.LR2RootPath), root);

            Assert.IsFalse(draft.HasLr2PathSelectionError);
            Assert.AreEqual(root, draft.LR2RootPath);
            Assert.AreEqual(song, draft.LR2SongDBPath);
            Assert.AreEqual(config, draft.LR2ConfigXmlPath);
        }
        finally
        {
            Directory.Delete(scope, recursive: true);
        }
    }

    /// <summary>同じ／別の有効 root に復旧すると raw tuple を依存表示より先に一回ずつ通知し、拒否状態を解除することを確認して、途中の不整合表示や重複通知を検出します。</summary>
    [DataTestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public void Lr2RootPicker_RecoveryNotifiesRawTupleBeforeDependentPresentationExactlyOnce(bool useDifferentRoot)
    {
        string scope = CreateTemporaryRoot();
        try
        {
            string originalRoot = Path.Combine(scope, "original");
            (string originalSong, string originalConfig) = CreateValidLr2Layout(originalRoot);
            string recoveryRoot = useDifferentRoot ? Path.Combine(scope, "different") : originalRoot;
            (string recoverySong, string recoveryConfig) = useDifferentRoot
                ? CreateValidLr2Layout(recoveryRoot)
                : (originalSong, originalConfig);
            Settings values = CreateValidStandaloneSettings(scope);
            values.LR2RootPath = originalRoot;
            values.LR2SongDBPath = originalSong;
            values.LR2ConfigXmlPath = originalConfig;
            var session = new CountingSettingsEditSession(values);
            using SettingsDialogViewModel draft = CreateViewModel(session, firstStartup: false).SettingDialog;
            var changedProperties = new List<string>();
            draft.PropertyChanged += (_, args) => changedProperties.Add(args.PropertyName!);

            draft.SetRootFolderPathFromPicker(nameof(draft.LR2RootPath), Path.Combine(scope, "invalid"));
            Assert.IsTrue(draft.HasLr2PathSelectionError);
            changedProperties.Clear();

            draft.SetRootFolderPathFromPicker(nameof(draft.LR2RootPath), recoveryRoot);

            string[] expectedSequence =
            [
                nameof(draft.LR2RootPath),
                nameof(draft.LR2SongDBPath),
                nameof(draft.LR2ConfigXmlPath),
                nameof(draft.LR2bodyPath),
                nameof(draft.Lr2SongDbPathStatusIcon),
                nameof(draft.Lr2SongDbPathStatusKind),
                nameof(draft.Lr2SongDbPathStatusText),
                nameof(draft.Lr2ConfigPathStatusIcon),
                nameof(draft.Lr2ConfigPathStatusKind),
                nameof(draft.Lr2ConfigPathStatusText),
                nameof(draft.Lr2PathSelectionError),
                nameof(draft.HasLr2PathSelectionError)
            ];
            HashSet<string> observedContractProperties = [.. expectedSequence];
            string[] actualSequence = changedProperties
                .Where(observedContractProperties.Contains)
                .ToArray();
            CollectionAssert.AreEqual(expectedSequence, actualSequence);
            Assert.AreEqual(recoveryRoot, draft.LR2RootPath);
            Assert.AreEqual(recoverySong, draft.LR2SongDBPath);
            Assert.AreEqual(recoveryConfig, draft.LR2ConfigXmlPath);
            Assert.IsFalse(draft.HasLr2PathSelectionError);
        }
        finally
        {
            Directory.Delete(scope, recursive: true);
        }
    }

    /// <summary>DB／config 候補の拒否後に保持中の有効 path を再選択すると両方の拒否状態を解除し、成功 status を保つことを確認します。</summary>
    [TestMethod]
    public void Lr2IndividualPickers_ReselectingRetainedValidPathsClearsRejectedCandidateError()
    {
        string scope = CreateTemporaryRoot();
        try
        {
            string root = Path.Combine(scope, "root");
            (string song, string config) = CreateValidLr2Layout(root);
            string invalidSong = Path.Combine(scope, "missing.db");
            string invalidConfig = Path.Combine(scope, "malformed.xml");
            File.WriteAllText(invalidConfig, "<config>");
            Settings values = CreateValidStandaloneSettings(scope);
            values.LR2RootPath = root;
            values.LR2SongDBPath = song;
            values.LR2ConfigXmlPath = config;
            var session = new CountingSettingsEditSession(values);
            using SettingsDialogViewModel draft = CreateViewModel(session, firstStartup: false).SettingDialog;

            draft.SetFilePathFromPicker(nameof(draft.LR2SongDBPath), invalidSong);
            Assert.AreEqual(song, draft.LR2SongDBPath);
            Assert.IsTrue(draft.HasLr2PathSelectionError);
            draft.SetFilePathFromPicker(nameof(draft.LR2SongDBPath), song);
            Assert.IsFalse(draft.HasLr2PathSelectionError);
            Assert.AreEqual("Success", draft.Lr2SongDbPathStatusKind);

            draft.SetFilePathFromPicker(nameof(draft.LR2ConfigXmlPath), invalidConfig);
            Assert.AreEqual(config, draft.LR2ConfigXmlPath);
            Assert.IsTrue(draft.HasLr2PathSelectionError);
            draft.SetFilePathFromPicker(nameof(draft.LR2ConfigXmlPath), config);
            Assert.IsFalse(draft.HasLr2PathSelectionError);
            Assert.AreEqual("Success", draft.Lr2ConfigPathStatusKind);
        }
        finally
        {
            Directory.Delete(scope, recursive: true);
        }
    }

    /// <summary>通常・追加・ROOTの先の選択を実保存し、同一・親・子関係の次の選択を最新確定配置に対して拒みます。どちらの操作順でも保存済み配置と候補一覧を保持します。</summary>
    [DataTestMethod]
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
    [DataTestMethod]
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
            using (var db = new LR2SongDBExtended(settings.LR2SongDBPath))
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
            var owner = new StartupLibraryInitializationWorkflowOwner(gate);
            Task<LeapYearFolderRepairApproval> operation = owner.PrepareLeapYearFolderRepairAsync(settings.LR2SongDBPath, dialogs, () => shutdown);
            if (resultKind is 0 or 1) { await Assert.ThrowsExceptionAsync<OperationCanceledException>(() => operation); }
            else if (resultKind == 4) { Assert.AreSame(original, await Assert.ThrowsExceptionAsync<IOException>(() => operation)); }
            else
            {
                InvalidOperationException failure = await Assert.ThrowsExceptionAsync<InvalidOperationException>(() => operation);
                if (resultKind == 2) { Assert.AreSame(original, failure.InnerException); }
                else { StringAssert.Contains(failure.Message, nameof(UiDialogStatus.OwnerUnavailable)); }
            }
            Assert.AreEqual(1, dialogs.ConfirmationCount);
            Assert.AreEqual(0, dialogs.MessageCount);
            Assert.AreEqual(sentinel, Directory.GetLastWriteTime(folder));
            using LR2SongDBExtended verify = new BmsLibraryDbGateway(settings.LR2SongDBPath).OpenSongDbReadOnly();
            LR2SongDB.folder row = verify.Table<LR2SongDB.folder>().Single();
            Assert.AreEqual(0, row.adddate);
            Assert.IsNull(row.date);
        }
        finally { Directory.Delete(root, recursive: true); }
    }

    /// <summary>修復後の通知は表示失敗を局所処理し、終了要求がある場合だけ取消しを伝播します。通知結果だけで終了したことにはしません。</summary>
    [DataTestMethod]
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
        var owner = new StartupLibraryInitializationWorkflowOwner(gate);
        var notification = new LeapYearFolderRepairNotification(warning, warning ? [] : [("original folder", new IOException("timestamp repair failed"))]);
        Task operation = owner.PresentLeapYearFolderRepairAsync(notification, dialogs, () => shutdown);
        if (shutdownAfterDisplay) { await Assert.ThrowsExceptionAsync<OperationCanceledException>(() => operation); }
        else { await operation; }
        Assert.AreEqual(1, dialogs.MessageCount);
        Assert.AreEqual(0, dialogs.ConfirmationCount);
    }

    /// <summary>各実入口で確認中の終了は副作用を始めず、成功公開後の修復通知中の終了は既公開成功を保持し、新しい案内・後続・設定再表示を止めます。</summary>
    [DataTestMethod]
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
            new BmsLibraryDbGateway(targetDb).EnsureAppOwnedSchema();
            using (var db = new LR2SongDBExtended(targetDb))
            {
                db.CreateTable<LR2SongDB.song>();
                db.CreateTable<LR2SongDB.folder>();
                db.Insert(new LR2SongDB.folder { path = folder + Path.DirectorySeparatorChar, type = 1, date = null, adddate = 0, title = "saved identity", info_a = "retained" });
                Lr2SongDbSyncStatusService.MarkCompleted(db, Lr2SongDbSyncSignatureBuilder.Build(BmsLibraryOptionsSnapshot.CreateCurrent(values)), "fixture-complete", 0, DateTime.UtcNow);
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
                var composition = new ApplicationComposition(settingsEditSession: settingsSession, uiScheduler: scheduler,
                    applicationLifetime: lifetime, cultureCatalog: TestApplicationContext.CreateCultureCatalog(),
                    settingsDialogService: dialogs, fileDbMutationDialogService: dialogs, defaultBmsPlayerFactory: () => new RecordingPlaybackPlayer());
                var config = new LR2Config(values.LR2ConfigXmlPath);
                Lr2ConfigOwner configOwner = composition.ResolveLr2Config(values.LR2ConfigXmlPath);
                var actualLibrary = new TestBmsLibrary(targetDb, configOwner, null, mutation, null, scheduler,
                    () => BmsLibraryOptionsSnapshot.CreateCurrent(values), chartFileScanner: CapturedChartFileScanner.FromFixture([],
                    new Dictionary<string, IEnumerable<string>>(StringComparer.OrdinalIgnoreCase) { [Path.Combine(root, "Songs")] = [] }, [Path.Combine(root, "Songs")]),
                    operationAdmission: composition.OperationAdmission,
                    playlistOperationAdmission: composition.PlaylistOperationAdmission, settings: values, rootFileEnumerator: new FastRootFileEnumerator());
                library = actualLibrary;
                playlist = MainWindowViewModelTestFactory.CreatePlaylist(targetDb, values, configOwner, library: actualLibrary);
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
            using LR2SongDBExtended verify = new BmsLibraryDbGateway(targetDb).OpenSongDbReadOnly();
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

    private static Settings CreateValidCustomFolderSettings(string root, IEnumerable<string> registeredRoots, Settings? ownedValues = null)
    {
        string songs = Path.Combine(root, "Songs");
        Directory.CreateDirectory(songs);
        (string songDb, string configPath) = CreateValidLr2Layout(root);
        Settings values = CreateValidStandaloneSettings(songs, ownedValues);
        values.OperationModeLR2DB = true;
        values.LR2RootPath = root;
        values.LR2SongDBPath = songDb;
        values.LR2ConfigXmlPath = configPath;
        values.LR2CustomFolderOutputBaseDir = Path.Combine(root, "NormalOutput");
        values.LR2CustomFolderOutputBaseDirRootType = Path.Combine(root, "RootOutput");
        values.LR2CustomFolderAdditionalOutputBaseDirs = "[]";
        Directory.CreateDirectory(values.LR2CustomFolderOutputBaseDir);
        Directory.CreateDirectory(values.LR2CustomFolderOutputBaseDirRootType);
        string[] roots = new[] { songs }.Concat(registeredRoots).ToArray();
        foreach (string path in roots)
        {
            Directory.CreateDirectory(path);
        }
        var config = new BeMusicSeeker.Models.LR2.LR2Config(configPath);
        config.AddBMSSearchDirectories(roots);
        config.Save(config.ConfigFilePath);
        return values;
    }

    private static void SetCustomFolderOutput(SettingsDialogViewModel dialog, string role, string path)
    {
        switch (role)
        {
            case "normal": dialog.LR2CustomFolderOutputDir = path; break;
            case "additional": dialog.AddCustomFolderAdditionalOutputBaseDir(path); break;
            case "root": dialog.LR2CustomFolderAsRootOutputDir = path; break;
            default: throw new ArgumentOutOfRangeException(nameof(role));
        }
    }

    private static (string SongDb, string Config) CreateValidLr2Layout(string root)
    {
        string songDb = Path.Combine(root, "LR2files", "Database", "song.db");
        string config = Path.Combine(root, "LR2files", "Config", "config.xml");
        Directory.CreateDirectory(Path.GetDirectoryName(songDb)!);
        Directory.CreateDirectory(Path.GetDirectoryName(config)!);
        File.WriteAllBytes(songDb, []);
        File.WriteAllText(config, "<config><system /><jukebox /></config>");
        File.WriteAllBytes(Path.Combine(root, "LR2body.exe"), []);
        return (songDb, config);
    }

    private static MainWindowViewModel CreateViewModel(
        ISettingsEditSession settingsSession,
        bool firstStartup,
        Func<MainWindowViewModel, Task<bool>>? initializeOwner = null,
        Func<MainWindowViewModel, Task>? reloadScoresOnly = null,
        Action<Exception>? reportSettingsApplyFailure = null,
        Func<MainWindowViewModel, Task>? reloadFileDiff = null,
        ISettingsDialogPlayerFactoryPort? playerFactoryPort = null,
        ISettingsDialogPlaybackRuntimePort? playbackRuntimePort = null,
        IUiDialogService? dialogs = null,
        Func<MainWindowViewModel, LibraryFileMutationCapability, Task>? reloadScoresOnlyUnderAdmission = null)
    {
        var composition = new ApplicationComposition(
            settingsEditSession: settingsSession,
            reportSettingsApplyFailure: reportSettingsApplyFailure ?? (_ => { }),
            uiScheduler: new WpfUiScheduler(() => Dispatcher.CurrentDispatcher),
            applicationLifetime: TestApplicationContext.CreateLifetime(firstStartup),
            cultureCatalog: TestApplicationContext.CreateCultureCatalog(),
            settingsDialogService: dialogs,
            fileDbMutationDialogService: dialogs);
        MainWindowViewModel viewModel = composition.CreateMainWindowViewModel();
        if (initializeOwner != null || reloadScoresOnly != null || reloadFileDiff != null || reloadScoresOnlyUnderAdmission != null)
        {
            SettingsDialogViewModel testDialog = new(
                new TestSettingsDialogStatePort(
                    viewModel,
                    initializeOwner == null
                        ? () => ((ISettingsDialogStatePort)viewModel).InitializeLibraryAsync(null)
                        : async () => new StartupInitializationResult(await initializeOwner(viewModel)
                            ? StartupInitializationOutcome.Succeeded
                            : StartupInitializationOutcome.SettingsRequired),
                    () =>
                    {
                        SetPrivateField(viewModel, "initializationCompleted", false);
                        SetPrivateField(viewModel, "hasActiveLibraryProfile", false);
                    },
                    reloadScoresOnly: capability => reloadScoresOnlyUnderAdmission != null
                        ? reloadScoresOnlyUnderAdmission(viewModel, capability ?? throw new InvalidOperationException("ScoreOnly continuation requires accepted capability."))
                        : reloadScoresOnly == null ? Task.CompletedTask : reloadScoresOnly(viewModel),
                    reloadFileDiff: reloadFileDiff == null
                        ? _ => Task.CompletedTask
                        : _ => reloadFileDiff(viewModel),
                    initializeAcceptedLibrary: initializeOwner == null
                        ? (capability, approval, observer, forceProjection) => ((ISettingsDialogStatePort)viewModel).InitializeLibraryAsync(capability, approval, observer, forceProjection)
                        : null),
                viewModel.PlaylistWorkspace,
                viewModel.PlaylistWorkspace,
                viewModel.PlayHistory,
                viewModel.LibraryFolderTree,
                new TestSettingsDialogPlayerFactoryPort(),
                new TestSettingsDialogPlaybackRuntimePort(),
                viewModel.Lr2SongDbSyncWorkflow,
                settingsSession,
                applicationLifetime: TestApplicationContext.CreateLifetime(firstStartup),
                cultureCatalog: TestApplicationContext.CreateCultureCatalog(),
                schemaDialogs: dialogs,
                reportApplyFailure: reportSettingsApplyFailure ?? (_ => { }),
                externalShellGateway: ExternalShellGatewayPolicy.Current,
                applicationPathSnapshot: ApplicationPathPolicy.Current,
                audioDeviceCatalog: new TestAudioDeviceCatalog(),
                audioSettingsGateway: new TestAudioSettingsGateway(),
                audioDeviceTestWorkflow: AudioDeviceTestWorkflowTestFactory.Create(),
                operationAdmission: composition.OperationAdmission);
            viewModel.SettingDialog.Dispose();
            typeof(MainWindowViewModel)
                .GetProperty("SettingDialog", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)!
                .SetValue(viewModel, testDialog);
        }
        typeof(SettingsDialogViewModel)
            .GetField("playerFactoryPort", BindingFlags.Instance | BindingFlags.NonPublic)!
            .SetValue(viewModel.SettingDialog, playerFactoryPort ?? new TestSettingsDialogPlayerFactoryPort());
        typeof(SettingsDialogViewModel)
            .GetField("playbackRuntimePort", BindingFlags.Instance | BindingFlags.NonPublic)!
            .SetValue(viewModel.SettingDialog, playbackRuntimePort ?? new TestSettingsDialogPlaybackRuntimePort());
        return viewModel;
    }

    private static SettingsDialogViewModel CreateResourceListeningDialog(
        MainWindowViewModel owner,
        ISettingsDialogPlayHistoryPort playHistoryPort)
    {
        return new SettingsDialogViewModel(
            (ISettingsDialogStatePort)owner,
            owner.PlaylistWorkspace,
            owner.PlaylistWorkspace,
            playHistoryPort,
            owner.LibraryFolderTree,
            new TestSettingsDialogPlayerFactoryPort(),
            new TestSettingsDialogPlaybackRuntimePort(),
            owner.Lr2SongDbSyncWorkflow,
            new NoOpSettingsEditSession(MainWindowViewModelTestFactory.CreateIsolatedSettings()),
            applicationLifetime: TestApplicationContext.CreateLifetime(),
            cultureCatalog: TestApplicationContext.CreateCultureCatalog(),
            externalShellGateway: ExternalShellGatewayPolicy.Current,
            applicationPathSnapshot: ApplicationPathPolicy.Current,
            audioDeviceCatalog: new TestAudioDeviceCatalog(),
            audioSettingsGateway: new TestAudioSettingsGateway(),
            audioDeviceTestWorkflow: AudioDeviceTestWorkflowTestFactory.Create());
    }

    private static SettingsWindow OpenSettingsWindow(
        TestWindowPresentationScope windowTest,
        SettingsDialogViewModel dialog)
    {
        var window = new SettingsWindow { DataContext = dialog };
        windowTest.ShowAndWaitForContentRendered(window);
        return window;
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

    private static SettingsDialogViewModel CreateAudioDeviceTestDialog(
        MainWindowViewModel viewModel,
        CountingSettingsEditSession settingsSession,
        AudioDeviceTestWorkflowOwner workflow,
        TestAudioSettingsGateway audioGateway,
        IUiDialogService? dialogs = null,
        IAudioDeviceCatalog? audioDeviceCatalog = null)
    {
        viewModel.SettingDialog.Dispose();
        var dialog = new SettingsDialogViewModel(
            viewModel,
            viewModel.PlaylistWorkspace,
            viewModel.PlaylistWorkspace,
            viewModel.PlayHistory,
            viewModel.LibraryFolderTree,
            new TestSettingsDialogPlayerFactoryPort(),
            new TestSettingsDialogPlaybackRuntimePort(),
            viewModel.Lr2SongDbSyncWorkflow,
            settingsSession,
            schemaDialogs: dialogs,
            applicationLifetime: TestApplicationContext.CreateLifetime(),
            cultureCatalog: TestApplicationContext.CreateCultureCatalog(),
            audioDeviceTestWorkflow: workflow,
            externalShellGateway: ExternalShellGatewayPolicy.Current,
            applicationPathSnapshot: ApplicationPathPolicy.Current,
            audioDeviceCatalog: audioDeviceCatalog ?? new TestAudioDeviceCatalog(),
            audioSettingsGateway: audioGateway);
        dialog.SetPresentationActive(active: true);
        return dialog;
    }

    private static void ConfigureExplicitAudioSettings(Settings settings)
    {
        settings.PlayerDevice = "requested-device";
        settings.PlayerDeviceName = "Requested device";
        settings.PlayerSampleRate = SampleRate.SAMPLE_RATE_44100Hz;
        settings.PlayerFormat = SampleFormat.SAMPLE_INT_16BIT;
        settings.PlayerBufferSize = 10;
        settings.PlayerResamplingQuality = 4;
        settings.PlayerWASAPIParam = false;
        settings.uBMplayVolume = 50;
    }

    private static void AssertExplicitAudioSettingsUnchanged(
        Settings settings,
        TestAudioSettingsGateway audioGateway,
        AudioDriver expectedDriver = AudioDriver.WasapiShared, int expectedResamplingQuality = 4)
    {
        Assert.AreEqual(expectedDriver, audioGateway.PlayerDriver);
        Assert.AreEqual("requested-device", audioGateway.OutputSelection.DeviceIdentity);
        Assert.AreEqual("Requested device", audioGateway.OutputSelection.DeviceName);
        Assert.AreEqual("requested-device", settings.PlayerDevice);
        Assert.AreEqual("Requested device", settings.PlayerDeviceName);
        Assert.AreEqual(SampleRate.SAMPLE_RATE_44100Hz, settings.PlayerSampleRate);
        Assert.AreEqual(SampleFormat.SAMPLE_INT_16BIT, settings.PlayerFormat);
        Assert.AreEqual(expectedResamplingQuality, settings.PlayerResamplingQuality);
    }

    private static (SettingsDialogViewModel Dialog, BeMusicSeeker.Models.LR2.LR2Config Config) CreateLr2RemovalDialog(
        CountingSettingsEditSession settingsSession,
        RecordingRootDialogService dialogs,
        ISettingsDialogSearchRootRuntimePort runtime,
        string configPath,
        string bmsRoot,
        string otherRoot,
        Func<Task> reloadFileDiff)
    {
        MainWindowViewModel owner = MainWindowViewModelTestFactory.Create();
        owner.SettingDialog.Dispose();
        var config = new BeMusicSeeker.Models.LR2.LR2Config(configPath);
        config.AddBMSSearchDirectories([bmsRoot, otherRoot]);
        config.Save(configPath);
        var dialog = new SettingsDialogViewModel(
            new TestSettingsDialogStatePort(
                owner,
                () => Task.FromResult(new StartupInitializationResult(StartupInitializationOutcome.Succeeded)),
                reloadFileDiff: _ => reloadFileDiff()),
            owner.PlaylistWorkspace,
            owner.PlaylistWorkspace,
            owner.PlayHistory,
            runtime,
            new TestSettingsDialogPlayerFactoryPort(),
            new TestSettingsDialogPlaybackRuntimePort(),
            owner.Lr2SongDbSyncWorkflow,
                settingsSession,
                applicationLifetime: TestApplicationContext.CreateLifetime(),
                cultureCatalog: TestApplicationContext.CreateCultureCatalog(),
                schemaDialogs: dialogs,
                externalShellGateway: ExternalShellGatewayPolicy.Current,
                applicationPathSnapshot: ApplicationPathPolicy.Current,
                audioDeviceCatalog: new TestAudioDeviceCatalog(),
                audioSettingsGateway: new TestAudioSettingsGateway(),
                audioDeviceTestWorkflow: AudioDeviceTestWorkflowTestFactory.Create());
        return (dialog, config);
    }

    /// <summary>各操作の前に捕捉した実走査入力へ切り替える、設定・モーダル代表の限定fixtureです。</summary>
    internal sealed class SettingsChartFileScanner(IChartFileScanner initial) : IChartFileScanner
    {
        /// <summary>次の実走査へ渡す、fixtureで捕捉した現在のファイル入力です。</summary>
        internal IChartFileScanner Current { get; set; } = initial;
        /// <summary>実走査へ渡したrootを、固定走査結果と独立に観測します。</summary>
        internal Action<IReadOnlyList<string>>? RootsObserved { get; set; }
        public ChartScanExecutionResult Scan(IEnumerable<string> roots, IEnumerable<string> extensions, bool verboseLog = false,
            bool includeTextSurface = true, bool includeDirectorySurface = false)
        {
            string[] capturedRoots = roots.ToArray();
            RootsObserved?.Invoke(capturedRoots);
            return Current.Scan(capturedRoots, extensions, verboseLog, includeTextSurface, includeDirectorySurface);
        }
    }

    private static Settings CreateValidStandaloneSettings(string root, Settings? ownedValues = null)
    {
        Settings settings = ownedValues ?? MainWindowViewModelTestFactory.CreateIsolatedSettings();
        void Configure(Settings values)
        {
            values.OperationModeLR2DB = false;
            values.BMSRootPath = root;
            values.StandaloneBmsRootPaths = root;
            values.BMSInstallDir = root;
            values.TableListURL = new Uri("http://127.0.0.1:1/table-list.json");
            values.EnablePlaylistUrlCompletion = false;
            values.ScanBmsFilesOnStartup = false;
            values.SkipInitPlaylistLoad = true;
            values.UseBeatorajaScoreDb = false;
            values.EnableBeatorajaBmtOutput = false;
            values.UseExternalPanelImage = false;
            values.UsePlayeruBMplay = false;
            values.UsePlayerLR2body = false;
            values.UsePlayerBMIIDXView = false;
            values.IsLR2BackupEnabled = false;
            values.RightClickActionsJson = RightClickActionSettingsDefaults.SerializedJson;
        }
        Configure(settings);
        return settings;
    }

    private static string CreateTemporaryRoot()
    {
        string root = Path.Combine(Path.GetTempPath(), "BeMusicSeeker_SettingDialogEditCompletion_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        return root;
    }

    /// <summary>標準schedulerが開始した仕事の同じ実Taskを捕捉し、同期開始失敗も待ち手へ伝えます。</summary>
    private static Task CaptureStartupBackgroundWorkTask(Func<Task> work, TaskCompletionSource<Task> capture)
    {
        try
        {
            Task task = work();
            capture.TrySetResult(task);
            return task;
        }
        catch (Exception failure)
        {
            capture.TrySetException(failure);
            throw;
        }
    }

    /// <summary>次の明示操作の準備として、起動後にLを取得し得る受理済み保守の実終端を待ちます。必須起動成功の条件は変更しません。</summary>
    private static void AwaitStartupBackgroundCompletionOnDispatcher(MainWindowViewModel viewModel, string operationName)
    {
        StartupBackgroundTaskSchedulerOwner scheduler = typeof(MainWindowViewModel)
            .GetField("startupBackgroundTaskScheduler", BindingFlags.Instance | BindingFlags.NonPublic)?.GetValue(viewModel)
            as StartupBackgroundTaskSchedulerOwner ?? throw new InvalidOperationException("Actual startup scheduler is unavailable.");
        var backgroundTerminal = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        void BackgroundChanged(object? sender, PropertyChangedEventArgs args)
        {
            if (!viewModel.ProgressHub.IsStartupBackgroundInitializationActive && scheduler.IsFullyIdle)
            {
                backgroundTerminal.TrySetResult();
            }
        }
        viewModel.ProgressHub.PropertyChanged += BackgroundChanged;
        try
        {
            if (viewModel.ProgressHub.IsStartupBackgroundInitializationActive || !scheduler.IsFullyIdle)
            {
                TestUiDispatcherHost.AwaitTaskOnDispatcher(backgroundTerminal.Task, operationName);
            }
        }
        finally
        {
            viewModel.ProgressHub.PropertyChanged -= BackgroundChanged;
        }
    }

    private static void SetActiveLibraryProfile(MainWindowViewModel viewModel, bool value)
    {
        typeof(MainWindowViewModel)
            .GetField("hasActiveLibraryProfile", BindingFlags.Instance | BindingFlags.NonPublic)!
            .SetValue(viewModel, value);
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
        var tables = new TestBmsPlaylist(databasePath, null, null, null,
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

    private static void SetPrivateField(object instance, string fieldName, object value)
    {
        instance.GetType()
            .GetField(fieldName, BindingFlags.Instance | BindingFlags.NonPublic)!
            .SetValue(instance, value);
    }

    private static void InvokePrivateMethod(object instance, string methodName, params object[] arguments)
    {
        instance.GetType()
            .GetMethod(methodName, BindingFlags.Instance | BindingFlags.NonPublic)!
            .Invoke(instance, arguments);
    }

    private static IEnumerable<T> FindDescendants<T>(DependencyObject root)
        where T : DependencyObject
    {
        var pending = new Stack<DependencyObject>();
        var visited = new HashSet<DependencyObject>();
        pending.Push(root);
        while (pending.Count > 0)
        {
            DependencyObject current = pending.Pop();
            if (!visited.Add(current))
            {
                continue;
            }

            if (current is T typedCurrent)
            {
                yield return typedCurrent;
            }

            if (current is Visual || current is System.Windows.Media.Media3D.Visual3D)
            {
                for (int index = 0; index < VisualTreeHelper.GetChildrenCount(current); index++)
                {
                    pending.Push(VisualTreeHelper.GetChild(current, index));
                }
            }

            foreach (object logicalChild in LogicalTreeHelper.GetChildren(current))
            {
                if (logicalChild is DependencyObject dependencyObject)
                {
                    pending.Push(dependencyObject);
                }
            }
        }
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

    private sealed class RecordingRootDialogService : IUiDialogService
    {
        internal Queue<UiFilePickerResult> FileResults { get; } = [];
        internal Queue<UiFolderPickerResult> FolderResults { get; } = [];
        internal List<UiFolderPickerRequest> FolderRequests { get; } = [];

        internal UiDialogResult ConfirmationResult { get; set; } = UiDialogResult.FromMessageBoxResult(MessageBoxResult.Cancel);

        internal UiDialogResult? MessageResult { get; set; } = UiDialogResult.FromMessageBoxResult(MessageBoxResult.OK);

        internal Action? MessageObserved { get; set; }

        internal Action<UiConfirmationRequest>? ConfirmationObserved { get; set; }

        internal Func<Task>? ConfirmationObservedAsync { get; set; }

        internal Func<Task>? MessageObservedAsync { get; set; }

        internal List<UiConfirmationRequest> ConfirmationRequests { get; } = [];

        internal int ConfirmationCount { get; private set; }

        internal int MessageCount { get; private set; }

        internal string LastMessageText { get; private set; } = string.Empty;

        public Task<UiDialogResult?> ShowMessageAsync(UiMessageRequest request, CancellationToken cancellationToken = default)
        {
            MessageCount++;
            LastMessageText = request.MessageBoxText;
            MessageObserved?.Invoke();
            return CompleteMessageAsync();
        }

        private async Task<UiDialogResult?> CompleteMessageAsync()
        {
            if (MessageObservedAsync != null)
            {
                await MessageObservedAsync();
            }
            return MessageResult;
        }

        public async Task<UiDialogResult> ConfirmAsync(UiConfirmationRequest request, CancellationToken cancellationToken = default)
        {
            ConfirmationCount++;
            ConfirmationRequests.Add(request);
            ConfirmationObserved?.Invoke(request);
            if (ConfirmationObservedAsync != null) { await ConfirmationObservedAsync(); }
            return ConfirmationResult;
        }

        public Task<UiWindowDialogResult<TResult>> ShowWindowAsync<TWindow, TResult>(
            UiWindowDialogRequest<TWindow, TResult> request,
            CancellationToken cancellationToken = default)
            where TWindow : Window => throw new NotSupportedException();

        public Task<UiFilePickerResult> PickFileAsync(UiFilePickerRequest request, CancellationToken cancellationToken = default) =>
            FileResults.Count > 0 ? Task.FromResult(FileResults.Dequeue()) : throw new NotSupportedException();

        public Task<UiFolderPickerResult> PickFolderAsync(UiFolderPickerRequest request, CancellationToken cancellationToken = default)
        {
            FolderRequests.Add(request);
            return FolderResults.Count > 0 ? Task.FromResult(FolderResults.Dequeue()) : throw new NotSupportedException();
        }

        public Task<UiSaveFilePickerResult> PickSaveFileAsync(UiSaveFilePickerRequest request, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public Task<UiProgressResult> RunWithProgressAsync(
            UiProgressRequest request,
            Func<UiProgressContext, Task> operation,
            CancellationToken cancellationToken = default) => throw new NotSupportedException();
    }

    private sealed class RecordingResourceRefreshPlayHistoryPort : ISettingsDialogPlayHistoryPort
    {
        internal int RefreshDisplayTargetCatalogCount { get; private set; }

        public void InvalidateReadCache(string reason)
        {
        }

        public void RefreshDisplayTargetCatalog(bool queueRefreshWhenSelectionChanges = true)
        {
            RefreshDisplayTargetCatalogCount++;
        }

        public void RefreshDisplayTargetSetsFromSettings(
            string serializedDisplayTargetSets,
            bool queueRefreshWhenSelectionChanges)
        {
        }
    }

    private sealed class ActiveSettingsDialogStatePort : ISettingsDialogStatePort
    {
        public bool HasActiveLibraryProfile => true;

        public bool IsLibraryOperationInProgress => false;

        public bool IsInitializationCompletionCurrent(long operationToken) => operationToken == 0L;

        public Task<StartupInitializationResult> CompleteRequiredInitializationAfterAdmissionAsync(StartupInitializationResult result)
            => Task.FromResult(result with { CompletionPublished = result.Outcome == StartupInitializationOutcome.Succeeded });

        public Task<LeapYearFolderRepairApproval?> PrepareLibraryInitializationAsync() => Task.FromResult<LeapYearFolderRepairApproval?>(null);

        public Task PresentLeapYearFolderRepairAsync(LeapYearFolderRepairNotification? notification) => Task.CompletedTask;

        public Task<StartupInitializationResult> InitializeLibraryAsync(LibraryFileMutationCapability? capability = null,
            LeapYearFolderRepairApproval? leapYearRepairApproval = null, Action<LeapYearFolderRepairNotification>? repairNotificationObserver = null, bool forceBeatorajaBmtProjection = false) => Task.FromResult(new StartupInitializationResult(StartupInitializationOutcome.Succeeded));

        public Task<StartupInitializationResult> ReloadScoresOnlyAsync(LibraryFileMutationCapability capability) => Task.FromResult(new StartupInitializationResult(StartupInitializationOutcome.Succeeded));

        public Task PresentLibraryDirectoryWarningAsync(BeMusicSeeker.Models.BmsLibraryInternal.LibraryDirectoryPreflightException failure) => Task.CompletedTask;

        public Task<StartupInitializationResult> ReloadFileDiffAsync(LibraryFileMutationCapability? capability = null) => Task.FromResult(new StartupInitializationResult(StartupInitializationOutcome.Succeeded));

#pragma warning disable CS0067 // インターフェイスのイベント面を満たすが、このテストダブルでは発火させない。
        public event EventHandler? LibraryOperationAvailabilityChanged;

        public event Action<Lr2PlayHistorySchemaStatusSnapshot>? Lr2PlayHistorySchemaStatusChanged;
#pragma warning restore CS0067
    }

    private sealed class ComposedSettingsDialogWorkspacePort :
        ISettingsDialogWorkspacePort,
        ISettingsDialogCustomFolderOutputPort
    {
        public void RetryCustomFolderMigrations(IReadOnlyList<CustomFolderMigrationReceipt> receipts,
            CustomFolderOutputSettingsSnapshot settings, LibraryFileMutationCapability capability)
        { }

        private readonly ChartFileOperationSynchronizer outputAdmission = new();

        public bool CanBeginOutputOperation => outputAdmission.CanEnter;
        public event EventHandler? OutputOperationAvailabilityChanged { add => outputAdmission.AvailabilityChanged += value; remove => outputAdmission.AvailabilityChanged -= value; }

        public LibraryFileMutationLease? TryBeginOutputOperation()
            => outputAdmission.TryEnter(out IDisposable lease) ? (LibraryFileMutationLease)lease : null;

        private readonly Settings values;

        internal ComposedSettingsDialogWorkspacePort(Settings values)
        {
            this.values = values ?? throw new ArgumentNullException(nameof(values));
        }

        public bool HasPlaylistTables => true;

        public long PlaylistCatalogVersion => 0L;

        public CustomFolderOutputSettingsSnapshot CustomFolderOutputSettings =>
            CustomFolderOutputSettingsSnapshot.CreateCurrent(values);

        public IReadOnlyList<PlaylistTablePresentationSnapshot> CapturePlaylistPresentationSnapshots() => [];

        public bool HasUnimportedBeatorajaTableUrlsForBmtOutputGuide(string beatorajaRootPath) => false;

        public void SchedulePlaylistUrlCompletionRefresh(string reason)
        {
        }

        public Task ExportBeatorajaBmtAsync(string reason, string cleanupTablePath, LibraryFileMutationCapability capability, bool forceProjection = false, BeatorajaBmtOptionsSnapshot? options = null)
        {
            return Task.CompletedTask;
        }

        public Task RunWithPlaylistOperationNotificationsAsync(Func<Task> operation, string operationName) =>
            operation();

        public void ChangeCustomFolderBaseDirectoryWithSettings(
            string outputDirBaseBefore,
            string outputDirBaseAfter,
            string additionalOutputBaseDirsBefore,
            string additionalOutputBaseDirsAfter,
            CustomFolderOutputSettingsSnapshot settings, LibraryFileMutationCapability? capability = null, Action<CustomFolderMigrationReceipt>? migrationObserver = null,
            IReadOnlyDictionary<string, string>? outputBaseRenames = null)
        {
        }

        public void ChangeCustomFolderBaseDirectoryRootWithSettings(
            string outputDirBaseBefore,
            string outputDirBaseAfter,
            CustomFolderOutputSettingsSnapshot settings, LibraryFileMutationCapability? capability = null, Action<CustomFolderMigrationReceipt>? migrationObserver = null)
        {
        }

        public void ValidateCustomFolderOutputRegistration(CustomFolderOutputSettingsSnapshot settings,
            IReadOnlyDictionary<string, string> renames)
        { }

        public IReadOnlyList<string> PrepareCustomFolderOutputSearchRoots(string previousRootOutputBaseDirectory,
            CustomFolderOutputSettingsSnapshot settings, IReadOnlyList<string> currentDirectories, IReadOnlyDictionary<string, string>? renames = null) => currentDirectories;

        public int ApplyCustomFolderAdditionalOutputBaseRegistrationChanges(
            string previousAdditionalOutputBaseDirectories,
            IReadOnlyDictionary<string, string> pendingRenames,
            CustomFolderOutputSettingsSnapshot settings, LibraryFileMutationCapability? capability = null, Action<CustomFolderMigrationReceipt>? migrationObserver = null) => 0;

#pragma warning disable CS0067 // インターフェイスのイベント面を満たすが、このテストダブルでは発火させない。
        public event EventHandler<PlaylistCatalogChangedEventArgs>? PlaylistCatalogChanged;
#pragma warning restore CS0067
    }

    private sealed class CountingSettingsEditSession : ISettingsEditSession
    {
        public void SaveEnvironmentForRestart(EnvironmentSettingsChange environment, string historyIdentity)
        {
            environment.Commit(Values);
            Values.PlayHistorySelectedDisplayTargetIdentity = historyIdentity;
            Save();
            Reload();
        }

        internal CountingSettingsEditSession(Settings values)
        {
            Values = values;
        }

        internal Action? SaveObserved { get; set; }

        internal Exception? SaveFailure { get; set; }

        internal bool BlockSave { get; set; }

        internal bool PersistOwnedValues { get; init; }

        internal ManualResetEventSlim SaveEntered { get; } = new(false);

        internal ManualResetEventSlim ReleaseSave { get; } = new(false);

        internal int SaveCount { get; private set; }

        public Settings Values { get; }

        public void Reload()
        {
            if (PersistOwnedValues) { Values.Reload(); }
        }

        public void Save()
        {
            SaveCount++;
            SaveObserved?.Invoke();
            if (SaveFailure != null)
            {
                throw SaveFailure;
            }
            if (BlockSave)
            {
                SaveEntered.Set();
                ReleaseSave.Wait();
            }
            if (PersistOwnedValues) { Values.Save(); }
        }
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

        public void RequestShutdown()
        {
        }

        public Task RestartApplicationAsync()
        {
            RestartCount++;
            return Task.CompletedTask;
        }
    }

    private sealed class WindowClosingPresentationPort : ISettingDialogPresentationPort
    {
        internal SettingsWindow? CurrentWindow { get; set; }

        public void OpenSettingsDialog(bool deferPresentation = false)
        {
        }

        public void OpenInitialSetupLanguageDialog()
        {
        }

        public void CloseSettingsDialog()
        {
            (CurrentWindow ?? throw new InvalidOperationException("No settings Window is active."))
                .CloseFromPresentation();
        }

        public Task CloseSettingsDialogAsync()
        {
            CloseSettingsDialog();
            return Task.CompletedTask;
        }

        public void RefreshAppearanceSelection()
        {
        }
    }

    private sealed class RecordingPlaybackPlayer : IBMSPlayer
    {
        public event PropertyChangedEventHandler? PropertyChanged;

        public string ExePath { get; set; } = string.Empty;

        public TimeSpan Duration => TimeSpan.Zero;

        public TimeSpan CurrentTime { get; set; }

        public TimeSpan StopTime => TimeSpan.Zero;

        public TimeSpan BmsDuration => TimeSpan.Zero;

        public TimeSpan MusicDuration => TimeSpan.Zero;

        public int CurrentVoices => 0;

        public int MaxVoices => 0;

        public int NoteDensity => 0;

        public int NoteDensityMax => 0;

        public int Bpm => 0;

        public int MinBpm => 0;

        public int MaxBpm => 0;

        public double Total => 0;

        public int Combo => 0;

        public int Notes => 0;

        public int Measure => 0;

        public int LastMeasure => 0;

        public int VolumeChangedCount { get; private set; }

        public void Raise(string propertyName)
        {
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
        }

        public void CloseProcess()
        {
        }

        public Task PlayStart(string bmsFilePath, Action<object, EventArgs>? onExitEventHandler = null)
        {
            return Task.CompletedTask;
        }

        public void RestartPlayingBMSfile()
        {
        }

        public void PausePlayingBMSfileToggle()
        {
        }

        public void FastForwardPlayingBMSfileStart()
        {
        }

        public void FastForwardPlayingBMSfileEnd()
        {
        }

        public void FastBackwardPlayingBMSfileStart()
        {
        }

        public void FastBackwardPlayingBMSfileEnd()
        {
        }

        public void ShowInfo()
        {
        }

        public void ShowEffect()
        {
        }

        public void ChangePlayside()
        {
        }

        public void IncreaseHighSpeed()
        {
        }

        public void DecreaseHighSpeed()
        {
        }

        public void VolumeChanged(int volume)
        {
            VolumeChangedCount++;
        }
    }

    private sealed class TestAudioDeviceTestPlaybackPort : IAudioDeviceTestPlaybackPort
    {
        public Task StopPlayback()
        {
            return Task.CompletedTask;
        }
    }

    private sealed class TestSettingsDialogPlayerFactoryPort : ISettingsDialogPlayerFactoryPort
    {
        private readonly IList<string>? sequence;

        internal TestSettingsDialogPlayerFactoryPort(IList<string>? sequence = null)
        {
            this.sequence = sequence;
        }

        internal Exception? ConfiguredFactoryFailure { get; set; }

        internal StartupSettingsSnapshot? LastConfiguredSettings { get; private set; }

        public IBMSPlayer CreateDefaultBmsPlayer()
        {
            sequence?.Add("factory-default");
            return new RecordingPlaybackPlayer();
        }

        public IBMSPlayer CreateBmsPlayerForSettings(StartupSettingsSnapshot settings)
        {
            sequence?.Add("factory-configured");
            LastConfiguredSettings = settings;
            if (ConfiguredFactoryFailure != null)
            {
                throw ConfiguredFactoryFailure;
            }
            return new RecordingPlaybackPlayer();
        }

    }

    private sealed class TestSettingsDialogPlaybackRuntimePort : ISettingsDialogPlaybackRuntimePort
    {
        private readonly IList<string>? sequence;

        internal TestSettingsDialogPlaybackRuntimePort(IList<string>? sequence = null)
        {
            this.sequence = sequence;
        }

        internal int ApplyCount { get; private set; }

        internal int NotifyCount { get; private set; }

        internal IBMSPlayer? LastReplacementPlayer { get; private set; }

        public void UsePlayerForNextPlayback(Func<IBMSPlayer> createPlayer, bool supportsBmson)
        {
            ApplyCount++;
            LastReplacementPlayer = null;
            sequence?.Add("apply");
            return;
        }

        public void NotifySettingsChanged()
        {
            NotifyCount++;
            sequence?.Add("notify");
        }

        public Task StopPlayback()
        {
            return Task.CompletedTask;
        }
    }

    private sealed class RecordingSearchRootRuntimePort : ISettingsDialogSearchRootRuntimePort
    {
        private readonly IList<string> sequence;

        internal RecordingSearchRootRuntimePort(IList<string> sequence)
        {
            this.sequence = sequence;
        }

        public bool IsLibraryAttached => true;

        internal Func<string, bool> HasOwnedChartUnderRealPathHandler { get; set; } = _ => false;

        public bool HasOwnedChartUnderRealPath(string directoryPath)
            => HasOwnedChartUnderRealPathHandler(directoryPath);

        internal IReadOnlyList<string> LastSearchTargets { get; private set; } = [];

        public void ApplyStandaloneSearchTargets(IReadOnlyList<string> searchTargets)
        {
            LastSearchTargets = [.. (searchTargets ?? [])];
            sequence.Add("apply");
        }

        public void InvalidateLibraryFolderCache()
        {
            sequence.Add("invalidate");
        }
    }

    private sealed class BlockingAudioDeviceTestRuntime : IAudioDeviceTestRuntime
    {
        private readonly TaskCompletionSource runtimeStarted;

        private readonly TaskCompletionSource releaseRuntime;

        internal BlockingAudioDeviceTestRuntime(TaskCompletionSource runtimeStarted, TaskCompletionSource releaseRuntime)
        {
            this.runtimeStarted = runtimeStarted;
            this.releaseRuntime = releaseRuntime;
        }

        public AudioDeviceTestResult Run(AudioDeviceTestRequest request)
        {
            runtimeStarted.TrySetResult();
            releaseRuntime.Task.GetAwaiter().GetResult();
            return AudioDeviceTestResultFactory.CreateSuccessful(request);
        }
    }

    private sealed class DelegateAudioDeviceTestRuntime : IAudioDeviceTestRuntime
    {
        private readonly Func<AudioDeviceTestRequest, AudioDeviceTestResult> run;

        internal DelegateAudioDeviceTestRuntime(Func<AudioDeviceTestRequest, AudioDeviceTestResult> run)
        {
            this.run = run;
        }

        public AudioDeviceTestResult Run(AudioDeviceTestRequest request)
            => run(request);
    }
}
