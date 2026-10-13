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
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using System.Xml.Linq;
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

    /// <summary>音量Sliderの変更を再生主体・設定・表示へ一回ずつ通知し、再生主体からの変更もSliderと表示へ戻すBindingを確認します。</summary>
    /// <remarks>実control間の接続の誤りを観測するためSettingsWindowを使います。音量値の判断は再生主体側で分担します。</remarks>
    [TestMethod]
    public void SettingDialogVolumeBinding_UsesComposedPlaybackOwner()
    {
        TestUiDispatcherHost.RunWindowTest(windowTest =>
        {
            string root = CreateTemporaryRoot();
            SettingsWindow? settingDialog = null;
            SettingsDialogViewModel? dialog = null;
            try
            {
                var settingsSession = new CountingSettingsEditSession(CreateValidStandaloneSettings(root));
                var player = new RecordingPlaybackPlayer();
                MainWindowViewModel viewModel = new ApplicationComposition(
                        settingsEditSession: settingsSession,
                        defaultBmsPlayerFactory: () => player,
                        uiScheduler: new WpfUiScheduler(() => Dispatcher.CurrentDispatcher), applicationLifetime: TestApplicationContext.CreateLifetime(), cultureCatalog: TestApplicationContext.CreateCultureCatalog())
                    .CreateMainWindowViewModel();
                dialog = viewModel.SettingDialog;
                settingDialog = new SettingsWindow
                {
                    DataContext = viewModel.SettingDialog,
                    PlaybackPanel = viewModel.PlaybackPanel
                };
                ((ListBox)settingDialog.FindName("settingsNavigation")).SelectedItem = settingDialog.FindName("navigationAudio");
                settingDialog.Measure(new Size(1000, 800));
                settingDialog.Arrange(new Rect(0, 0, 1000, 800));
                settingDialog.UpdateLayout();

                Slider volumeSlider = FindDescendants<Slider>(settingDialog)
                    .Single(slider => slider.GetBindingExpression(Slider.ValueProperty)?.ParentBinding.Path?.Path == "PlaybackPanel.PlayerVolume");
                TextBlock volumeText = FindDescendants<TextBlock>(settingDialog)
                    .Single(textBlock => textBlock.GetBindingExpression(TextBlock.TextProperty)?.ParentBinding.Path?.Path == "PlaybackPanel.PlayerVolume");

                int firstVolume = settingsSession.Values.uBMplayVolume == 100
                    ? settingsSession.Values.uBMplayVolume - 1
                    : settingsSession.Values.uBMplayVolume + 1;
                volumeSlider.Value = firstVolume;
                settingDialog.Dispatcher.Invoke(DispatcherPriority.DataBind, new Action(() => { }));

                Assert.AreEqual(firstVolume, viewModel.PlaybackPanel.PlayerVolume);
                Assert.AreEqual(firstVolume, settingsSession.Values.uBMplayVolume);
                Assert.AreEqual(1, player.VolumeChangedCount);
                Assert.AreEqual(firstVolume + "%", volumeText.Text);

                int secondVolume = firstVolume == 0 ? 1 : firstVolume - 1;
                viewModel.PlaybackPanel.PlayerVolume = secondVolume;
                settingDialog.Dispatcher.Invoke(DispatcherPriority.DataBind, new Action(() => { }));

                Assert.AreEqual(secondVolume, volumeSlider.Value);
                Assert.AreEqual(secondVolume + "%", volumeText.Text);
                Assert.AreEqual(2, player.VolumeChangedCount);
            }
            finally
            {
                try { settingDialog?.CloseForOwnerShutdown(); }
                finally
                {
                    dialog?.Dispose();
                    Directory.Delete(root, recursive: true);
                }
            }
        });
    }

    /// <summary>LR2／単独の保存済みモードで画面を繰り返し開閉しても保存要求を出さず、実 radio の承認クリックだけが一回のモード保存へ進むことを確認します。</summary>
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
                requestedMode.RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent, requestedMode));
                finalWindow.Dispatcher.Invoke(DispatcherPriority.DataBind, new Action(() => { }));

                Assert.AreEqual(1, dialogs.ConfirmationCount);
                Assert.AreEqual(1, settingsSession.SaveCount);
                Assert.AreEqual(0, lifetime.RestartCount, "動作モードの受付は shell が担当し、設定画面は process を直接起動しません。");
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

    /// <summary>両モードで実 radio の変更確認を取消すると、保存・再起動要求なしで元の checked 選択へ戻ることを確認し、取消後の表示と draft のずれを検出します。</summary>
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
                var settingsSession = new CountingSettingsEditSession(settings);
                var dialogs = new RecordingRootDialogService
                {
                    ConfirmationResult = UiDialogResult.FromMessageBoxResult(MessageBoxResult.Cancel)
                };
                var lifetime = new CountingApplicationLifetime();
                using SettingsDialogViewModel dialog = CreateOperationModeDialog(settingsSession, dialogs, lifetime);
                window = OpenSettingsWindow(windowTest, dialog);

                RadioButton requestedMode = FindOperationModeRadio(window, useLr2: !initialOperationMode);
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

    /// <summary>登録ルートの削除承認後に単独設定を保存し、残った検索対象を適用してから再読込みすることを確認して、保存前の反映や旧ルート残存を検出します。</summary>
    [TestMethod]
    public async Task RequestRemoveBmsSearchRootAsync_AcceptedStandaloneRoot_PersistsAndReloads()
    {
        string root = CreateTemporaryRoot();
        string secondRoot = Path.Combine(root, "second");
        string installRoot = CreateTemporaryRoot();
        Directory.CreateDirectory(secondRoot);
        Directory.CreateDirectory(installRoot);
        try
        {
            Settings settings = CreateValidStandaloneSettings(root);
            settings.BMSInstallDir = installRoot;
            settings.StandaloneBmsRootPaths = string.Join(Environment.NewLine, root, secondRoot);
            settings.BMSRootPath = root;
            var settingsSession = new CountingSettingsEditSession(settings);
            var dialogs = new RecordingRootDialogService
            {
                ConfirmationResult = UiDialogResult.FromMessageBoxResult(MessageBoxResult.OK)
            };
            int reloadCount = 0;
            var sequence = new List<string>();
            settingsSession.SaveObserved = () => sequence.Add("save");
            MainWindowViewModel owner = MainWindowViewModelTestFactory.Create();
            using SettingsDialogViewModel ownerDialogLifetime = owner.SettingDialog;
            var runtime = new RecordingSearchRootRuntimePort(sequence);
            using var dialog = new SettingsDialogViewModel(
                new TestSettingsDialogStatePort(
                    owner,
                    () => Task.FromResult(new StartupInitializationResult(StartupInitializationOutcome.Succeeded)),
                    reloadFileDiff: _ =>
                    {
                        reloadCount++;
                        sequence.Add("reload");
                        return Task.CompletedTask;
                    }),
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

            await dialog.RequestRemoveBmsSearchRootAsync(root);

            Assert.AreEqual(1, dialogs.ConfirmationCount);
            Assert.AreEqual(0, dialogs.MessageCount, dialogs.LastMessageText);
            CollectionAssert.DoesNotContain(
                SettingsDialogViewModel.DeserializeStandaloneBmsRootPaths(settings.StandaloneBmsRootPaths).ToArray(),
                root);
            Assert.AreEqual(secondRoot, settings.BMSRootPath, settings.BMSRootPath ?? "(null)");
            Assert.AreEqual(1, settingsSession.SaveCount);
            Assert.AreEqual(1, reloadCount);
            CollectionAssert.AreEqual(new[] { "save", "apply", "reload" }, sequence);
            CollectionAssert.AreEqual(
                new[] { secondRoot },
                runtime.LastSearchTargets.ToArray());
        }
        finally
        {
            Directory.Delete(root, recursive: true);
            Directory.Delete(installRoot, recursive: true);
        }
    }

    /// <summary>削除確認を取消すると登録文字列を保ち、保存を行わないことを確認して、取消を承認扱いする誤りを検出します。</summary>
    [TestMethod]
    public async Task RequestRemoveBmsSearchRootAsync_CancelLeavesStandaloneSettingsUnchanged()
    {
        string root = CreateTemporaryRoot();
        try
        {
            var settingsSession = new CountingSettingsEditSession(CreateValidStandaloneSettings(root));
            string before = settingsSession.Values.StandaloneBmsRootPaths;
            var dialogs = new RecordingRootDialogService
            {
                ConfirmationResult = UiDialogResult.FromMessageBoxResult(MessageBoxResult.Cancel)
            };
            MainWindowViewModel owner = MainWindowViewModelTestFactory.Create();
            using SettingsDialogViewModel ownerDialogLifetime = owner.SettingDialog;
            using var dialog = new SettingsDialogViewModel(
                new TestSettingsDialogStatePort(owner, () => Task.FromResult(new StartupInitializationResult(StartupInitializationOutcome.Succeeded))),
                owner.PlaylistWorkspace,
                owner.PlaylistWorkspace,
                owner.PlayHistory,
                owner.LibraryFolderTree,
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

            await dialog.RequestRemoveBmsSearchRootAsync(root);

            Assert.AreEqual(1, dialogs.ConfirmationCount);
            Assert.AreEqual(before, settingsSession.Values.StandaloneBmsRootPaths);
            Assert.AreEqual(0, settingsSession.SaveCount);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    /// <summary>空白要求は確認・保存をせず、存在しない登録ルートの明示削除は一回確認して保存することを確認し、欠損ルートを解除できなくする誤りを検出します。</summary>
    [TestMethod]
    public async Task RequestRemoveBmsSearchRootAsync_BlankRootIsIgnored_MissingRootCanBeUnregistered()
    {
        string root = CreateTemporaryRoot();
        string missing = Path.Combine(root, "missing");
        try
        {
            Settings settings = CreateValidStandaloneSettings(root);
            settings.StandaloneBmsRootPaths = string.Join(Environment.NewLine, root, missing);
            var settingsSession = new CountingSettingsEditSession(settings);
            var dialogs = new RecordingRootDialogService
            {
                ConfirmationResult = UiDialogResult.FromMessageBoxResult(MessageBoxResult.OK)
            };
            MainWindowViewModel owner = MainWindowViewModelTestFactory.Create();
            using SettingsDialogViewModel ownerDialogLifetime = owner.SettingDialog;
            using var dialog = new SettingsDialogViewModel(
                new TestSettingsDialogStatePort(owner, () => Task.FromResult(new StartupInitializationResult(StartupInitializationOutcome.Succeeded))),
                owner.PlaylistWorkspace,
                owner.PlaylistWorkspace,
                owner.PlayHistory,
                owner.LibraryFolderTree,
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

            await dialog.RequestRemoveBmsSearchRootAsync(string.Empty);
            await dialog.RequestRemoveBmsSearchRootAsync(missing);

            Assert.AreEqual(1, dialogs.ConfirmationCount);
            Assert.AreEqual(1, settingsSession.SaveCount);
            Assert.AreEqual(root, settingsSession.Values.StandaloneBmsRootPaths);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    /// <summary>削除確認の表示失敗を呼出元へ返し、保存せず登録ルートを保つことを確認して、表示失敗による削除実行を検出します。</summary>
    [TestMethod]
    public async Task RequestRemoveBmsSearchRootAsync_ConfirmationFailure_IsPropagatedBeforeMutation()
    {
        string root = CreateTemporaryRoot();
        try
        {
            var settingsSession = new CountingSettingsEditSession(CreateValidStandaloneSettings(root));
            var dialogs = new RecordingRootDialogService
            {
                ConfirmationResult = UiDialogResult.Failed(new InvalidOperationException("dialog failure"))
            };
            MainWindowViewModel owner = MainWindowViewModelTestFactory.Create();
            using SettingsDialogViewModel ownerDialogLifetime = owner.SettingDialog;
            using var dialog = new SettingsDialogViewModel(
                new TestSettingsDialogStatePort(owner, () => Task.FromResult(new StartupInitializationResult(StartupInitializationOutcome.Succeeded))),
                owner.PlaylistWorkspace,
                owner.PlaylistWorkspace,
                owner.PlayHistory,
                owner.LibraryFolderTree,
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

            Exception? exception = null;
            try
            {
                await dialog.RequestRemoveBmsSearchRootAsync(root);
            }
            catch (Exception ex)
            {
                exception = ex;
            }

            Assert.AreEqual(1, dialogs.ConfirmationCount);
            Assert.IsNotNull(exception);
            StringAssert.Contains(exception!.Message, "failed");
            Assert.AreEqual(0, settingsSession.SaveCount);
            CollectionAssert.Contains(
                SettingsDialogViewModel.DeserializeStandaloneBmsRootPaths(settingsSession.Values.StandaloneBmsRootPaths).ToArray(),
                root);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    /// <summary>LR2 登録削除を実 config に先に保存してから所持譜面を判定し、残るルートを保って検索対象適用・再読込みへ進むことを確認します。</summary>
    [TestMethod]
    public async Task RequestRemoveBmsSearchRootAsync_AcceptedLr2Root_SavesConfigWithoutSettingsSave()
    {
        string root = CreateTemporaryRoot();
        string bmsRoot = Path.Combine(root, "bms");
        string otherRoot = Path.Combine(root, "other");
        string installRoot = CreateTemporaryRoot();
        Directory.CreateDirectory(bmsRoot);
        Directory.CreateDirectory(otherRoot);
        Directory.CreateDirectory(installRoot);
        string configPath = Path.Combine(root, "config.xml");
        File.WriteAllText(configPath, "<config><system /><jukebox /></config>");
        try
        {
            Settings settings = CreateValidStandaloneSettings(bmsRoot);
            settings.OperationModeLR2DB = true;
            settings.LR2ConfigXmlPath = configPath;
            settings.BMSInstallDir = installRoot;
            var settingsSession = new CountingSettingsEditSession(settings);
            var dialogs = new RecordingRootDialogService
            {
                ConfirmationResult = UiDialogResult.FromMessageBoxResult(MessageBoxResult.OK)
            };
            var sequence = new List<string>();
            int reloadCount = 0;
            MainWindowViewModel owner = MainWindowViewModelTestFactory.Create();
            using SettingsDialogViewModel ownerDialogLifetime = owner.SettingDialog;
            var runtime = new RecordingSearchRootRuntimePort(sequence)
            {
                HasOwnedChartUnderRealPathHandler = directoryPath =>
                {
                    sequence.Add("query");
                    CollectionAssert.DoesNotContain(
                        new BeMusicSeeker.Models.LR2.LR2Config(configPath).GetBMSSearchDirectories().ToArray(),
                        directoryPath);
                    return true;
                }
            };
            var config = new BeMusicSeeker.Models.LR2.LR2Config(configPath);
            config.AddBMSSearchDirectories([bmsRoot, otherRoot]);
            config.Save(configPath);
            using var dialog = new SettingsDialogViewModel(
                new TestSettingsDialogStatePort(
                    owner,
                    () => Task.FromResult(new StartupInitializationResult(StartupInitializationOutcome.Succeeded)),
                    reloadFileDiff: _ =>
                    {
                        reloadCount++;
                        sequence.Add("reload");
                        return Task.CompletedTask;
                    }),
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
            await dialog.RequestRemoveBmsSearchRootAsync(bmsRoot);

            Assert.AreEqual(1, dialogs.ConfirmationCount);
            var persistedConfig = new BeMusicSeeker.Models.LR2.LR2Config(configPath);
            CollectionAssert.DoesNotContain(persistedConfig.GetBMSSearchDirectories().ToArray(), bmsRoot);
            CollectionAssert.Contains(persistedConfig.GetBMSSearchDirectories().ToArray(), otherRoot);
            Assert.AreEqual(0, settingsSession.SaveCount);
            Assert.AreEqual(1, reloadCount);
            CollectionAssert.AreEqual(new[] { "query", "apply", "reload" }, sequence);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
            Directory.Delete(installRoot, recursive: true);
        }
    }

    /// <summary>LR2 登録削除後に所持譜面がない場合は検索対象とフォルダ cache を更新し、譜面再読込みや利用者設定保存を行わないことを確認します。</summary>
    [TestMethod]
    public async Task RequestRemoveBmsSearchRootAsync_Lr2RootWithoutOwnedChart_InvalidatesFolderCache()
    {
        string root = CreateTemporaryRoot();
        string bmsRoot = Path.Combine(root, "bms");
        string otherRoot = Path.Combine(root, "other");
        string installRoot = CreateTemporaryRoot();
        Directory.CreateDirectory(bmsRoot);
        Directory.CreateDirectory(otherRoot);
        Directory.CreateDirectory(installRoot);
        string configPath = Path.Combine(root, "config.xml");
        File.WriteAllText(configPath, "<config><system /><jukebox /></config>");
        try
        {
            Settings settings = CreateValidStandaloneSettings(bmsRoot);
            settings.OperationModeLR2DB = true;
            settings.LR2ConfigXmlPath = configPath;
            settings.BMSInstallDir = installRoot;
            var settingsSession = new CountingSettingsEditSession(settings);
            var dialogs = new RecordingRootDialogService
            {
                ConfirmationResult = UiDialogResult.FromMessageBoxResult(MessageBoxResult.OK)
            };
            var sequence = new List<string>();
            int reloadCount = 0;
            var runtime = new RecordingSearchRootRuntimePort(sequence)
            {
                HasOwnedChartUnderRealPathHandler = _ =>
                {
                    sequence.Add("query");
                    return false;
                }
            };
            (SettingsDialogViewModel dialog, _) = CreateLr2RemovalDialog(
                settingsSession,
                dialogs,
                runtime,
                configPath,
                bmsRoot,
                otherRoot,
                () =>
                {
                    reloadCount++;
                    sequence.Add("reload");
                    return Task.CompletedTask;
                });

            using SettingsDialogViewModel dialogLifetime = dialog;

            await dialog.RequestRemoveBmsSearchRootAsync(bmsRoot);

            Assert.AreEqual(1, dialogs.ConfirmationCount);
            Assert.AreEqual(0, dialogs.MessageCount, dialogs.LastMessageText);
            Assert.AreEqual(0, settingsSession.SaveCount);
            Assert.AreEqual(0, reloadCount);
            CollectionAssert.AreEqual(new[] { "query", "apply", "invalidate" }, sequence);
            CollectionAssert.DoesNotContain(
                new BeMusicSeeker.Models.LR2.LR2Config(configPath).GetBMSSearchDirectories().ToArray(),
                bmsRoot);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
            Directory.Delete(installRoot, recursive: true);
        }
    }

    /// <summary>実 config の削除保存後に所持判定が失敗すると元の失敗を通知し、検索対象適用・再読込みを止めることを確認して、保存済み変更と未実行の反映を区別します。</summary>
    [TestMethod]
    public async Task RequestRemoveBmsSearchRootAsync_Lr2OwnedChartQueryFailure_PresentsFailureWithoutRuntimeApply()
    {
        string root = CreateTemporaryRoot();
        string bmsRoot = Path.Combine(root, "bms");
        string otherRoot = Path.Combine(root, "other");
        string installRoot = CreateTemporaryRoot();
        Directory.CreateDirectory(bmsRoot);
        Directory.CreateDirectory(otherRoot);
        Directory.CreateDirectory(installRoot);
        string configPath = Path.Combine(root, "config.xml");
        File.WriteAllText(configPath, "<config><system /><jukebox /></config>");
        try
        {
            Settings settings = CreateValidStandaloneSettings(bmsRoot);
            settings.OperationModeLR2DB = true;
            settings.LR2ConfigXmlPath = configPath;
            settings.BMSInstallDir = installRoot;
            var settingsSession = new CountingSettingsEditSession(settings);
            var dialogs = new RecordingRootDialogService
            {
                ConfirmationResult = UiDialogResult.FromMessageBoxResult(MessageBoxResult.OK)
            };
            var sequence = new List<string>();
            int reloadCount = 0;
            var runtime = new RecordingSearchRootRuntimePort(sequence)
            {
                HasOwnedChartUnderRealPathHandler = _ =>
                {
                    sequence.Add("query");
                    throw new InvalidOperationException("owned chart query failure");
                }
            };
            (SettingsDialogViewModel dialog, _) = CreateLr2RemovalDialog(
                settingsSession,
                dialogs,
                runtime,
                configPath,
                bmsRoot,
                otherRoot,
                () =>
                {
                    reloadCount++;
                    sequence.Add("reload");
                    return Task.CompletedTask;
                });

            using SettingsDialogViewModel dialogLifetime = dialog;

            await dialog.RequestRemoveBmsSearchRootAsync(bmsRoot);

            Assert.AreEqual(1, dialogs.ConfirmationCount);
            Assert.AreEqual(1, dialogs.MessageCount);
            StringAssert.Contains(dialogs.LastMessageText, "owned chart query failure");
            Assert.AreEqual(0, settingsSession.SaveCount);
            Assert.AreEqual(0, reloadCount);
            CollectionAssert.AreEqual(new[] { "query" }, sequence);
            CollectionAssert.DoesNotContain(
                new BeMusicSeeker.Models.LR2.LR2Config(configPath).GetBMSSearchDirectories().ToArray(),
                bmsRoot);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
            Directory.Delete(installRoot, recursive: true);
        }
    }

    /// <summary>単独登録の保存失敗を返し、設定文字列と表示リストのルートを元へ戻すことを確認して、未保存の削除だけが残る誤りを検出します。</summary>
    [TestMethod]
    public async Task RequestRemoveBmsSearchRootAsync_StandaloneSaveFailure_RestoresInMemoryState()
    {
        string root = CreateTemporaryRoot();
        string installRoot = CreateTemporaryRoot();
        try
        {
            Settings settings = CreateValidStandaloneSettings(root);
            settings.BMSInstallDir = installRoot;
            var settingsSession = new CountingSettingsEditSession(settings)
            {
                SaveFailure = new IOException("settings save failure")
            };
            var dialogs = new RecordingRootDialogService
            {
                ConfirmationResult = UiDialogResult.FromMessageBoxResult(MessageBoxResult.OK)
            };
            MainWindowViewModel owner = MainWindowViewModelTestFactory.Create();
            using SettingsDialogViewModel ownerDialogLifetime = owner.SettingDialog;
            using var dialog = new SettingsDialogViewModel(
                new TestSettingsDialogStatePort(owner, () => Task.FromResult(new StartupInitializationResult(StartupInitializationOutcome.Succeeded))),
                owner.PlaylistWorkspace,
                owner.PlaylistWorkspace,
                owner.PlayHistory,
                owner.LibraryFolderTree,
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

            Exception? exception = null;
            try
            {
                await dialog.RequestRemoveBmsSearchRootAsync(root);
            }
            catch (Exception ex)
            {
                exception = ex;
            }

            Assert.IsNotNull(exception);
            Assert.AreEqual(1, settingsSession.SaveCount);
            Assert.AreEqual(0, dialogs.MessageCount);
            CollectionAssert.Contains(
                SettingsDialogViewModel.DeserializeStandaloneBmsRootPaths(settings.StandaloneBmsRootPaths).ToArray(),
                root);
            CollectionAssert.Contains(dialog.StandaloneBmsRootPathList.ToArray(), root);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
            Directory.Delete(installRoot, recursive: true);
        }
    }

    /// <summary>config 保存先が消失した削除要求は失敗を通知し、両登録ルートをメモリに保ち、利用者設定保存で代替しないことを確認します。</summary>
    [TestMethod]
    public async Task RequestRemoveBmsSearchRootAsync_Lr2SaveFailure_RestoresConfigAndPresentsFailure()
    {
        string root = CreateTemporaryRoot();
        string bmsRoot = Path.Combine(root, "bms");
        string otherRoot = Path.Combine(root, "other");
        string installRoot = CreateTemporaryRoot();
        Directory.CreateDirectory(bmsRoot);
        Directory.CreateDirectory(otherRoot);
        Directory.CreateDirectory(installRoot);
        string configDirectory = Path.Combine(root, "config");
        Directory.CreateDirectory(configDirectory);
        string configPath = Path.Combine(configDirectory, "config.xml");
        File.WriteAllText(configPath, "<config><system /><jukebox /></config>");
        try
        {
            Settings settings = CreateValidStandaloneSettings(bmsRoot);
            settings.OperationModeLR2DB = true;
            settings.LR2ConfigXmlPath = configPath;
            settings.BMSInstallDir = installRoot;
            var settingsSession = new CountingSettingsEditSession(settings);
            var dialogs = new RecordingRootDialogService
            {
                ConfirmationResult = UiDialogResult.FromMessageBoxResult(MessageBoxResult.OK)
            };
            MainWindowViewModel owner = MainWindowViewModelTestFactory.Create();
            using SettingsDialogViewModel ownerDialogLifetime = owner.SettingDialog;
            var config = new BeMusicSeeker.Models.LR2.LR2Config(configPath);
            config.AddBMSSearchDirectories([bmsRoot, otherRoot]);
            config.Save(configPath);
            using var dialog = new SettingsDialogViewModel(
                new TestSettingsDialogStatePort(owner, () => Task.FromResult(new StartupInitializationResult(StartupInitializationOutcome.Succeeded))),
                owner.PlaylistWorkspace,
                owner.PlaylistWorkspace,
                owner.PlayHistory,
                owner.LibraryFolderTree,
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
            Directory.Delete(configDirectory, recursive: true);

            await dialog.RequestRemoveBmsSearchRootAsync(bmsRoot);

            Assert.AreEqual(1, dialogs.ConfirmationCount);
            Assert.AreEqual(1, dialogs.MessageCount);
            CollectionAssert.Contains(dialog.LR2ConfigBMSDirectories.ToArray(), bmsRoot);
            CollectionAssert.Contains(dialog.LR2ConfigBMSDirectories.ToArray(), otherRoot);
            Assert.AreEqual(0, settingsSession.SaveCount);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
            Directory.Delete(installRoot, recursive: true);
        }
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

    /// <summary>通常設定の変更は保存を一回完了してから Close 要求を出し、編集可能な終端へ戻ることを確認して、未保存のまま画面を閉じる誤りを検出します。</summary>
    [TestMethod]
    public async Task ApplySettingsAsync_ChangedNormalSetting_SavesBeforeClosing()
    {
        string root = CreateTemporaryRoot();
        try
        {
            var settingsSession = new CountingSettingsEditSession(CreateValidStandaloneSettings(root));
            var sequence = new List<string>();
            settingsSession.SaveObserved = () => sequence.Add("save");
            MainWindowViewModel viewModel = CreateViewModel(
                settingsSession,
                firstStartup: false);
            SetActiveLibraryProfile(viewModel, true);
            using SettingsDialogViewModel dialog = viewModel.SettingDialog;
            dialog.AttachPresentationPort(new RecordingSettingsDialogPresentationPort(sequence.Add));
            dialog.OverwritePlaylistUrlsWithCompletion = !dialog.OverwritePlaylistUrlsWithCompletion;

            await dialog.ApplySettingsAsync();

            CollectionAssert.AreEqual(new[] { "save", "close" }, sequence);
            Assert.AreEqual(1, settingsSession.SaveCount);
            Assert.IsFalse(dialog.HasPendingSettingChanges());
            Assert.IsTrue(dialog.IsEditCompletionEnabled);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    /// <summary>player 選択の保存と Close の後に捕捉設定で player を生成し、再生主体へ適用して通知する順序を確認して、旧設定での再構築や通知先行を検出します。</summary>
    [TestMethod]
    public async Task ApplySettingsAsync_PlayerRuntimeUsesFactoryThenApplyThenNotify()
    {
        string root = CreateTemporaryRoot();
        try
        {
            string playerPath = Path.Combine(root, "ubmplay.exe");
            File.WriteAllText(playerPath, string.Empty);
            var settingsSession = new CountingSettingsEditSession(CreateValidStandaloneSettings(root));
            var sequence = new List<string>();
            settingsSession.SaveObserved = () => sequence.Add("save");
            var factory = new TestSettingsDialogPlayerFactoryPort(sequence);
            var runtime = new TestSettingsDialogPlaybackRuntimePort(sequence);
            MainWindowViewModel viewModel = CreateViewModel(
                settingsSession,
                firstStartup: false,
                playerFactoryPort: factory,
                playbackRuntimePort: runtime);
            SetActiveLibraryProfile(viewModel, true);
            AttachPlaylistTables(viewModel, root);
            using SettingsDialogViewModel dialog = viewModel.SettingDialog;
            dialog.AttachPresentationPort(new RecordingSettingsDialogPresentationPort(sequence.Add));
            dialog.uBMplayPath = playerPath;
            dialog.UsePlayeruBMplay = true;

            await dialog.ApplySettingsAsync();

            CollectionAssert.AreEqual(
                new[] { "save", "close", "factory-configured", "apply", "notify" },
                sequence);
            Assert.AreEqual(1, settingsSession.SaveCount);
            Assert.IsNotNull(runtime.LastReplacementPlayer);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    /// <summary>品質・thread・event 条件の保存中も実行中の再生 Task と譜面を保ち、player 再構築・適用・通知を追加しないことを確認して、音声 draft 保存による再生中断を検出します。</summary>
    [DataTestMethod]
    [DataRow(false, false)]
    [DataRow(false, true)]
    [DataRow(true, true)]
    public void ApplySettingsAsync_AudioProcessingChangesDoNotReplaceCurrentPlaybackRuntime(bool originalEventMode, bool eventOnly)
    {
        TestUiDispatcherHost.Invoke(() =>
        {
            async Task VerifyAsync()
            {
                string root = CreateTemporaryRoot();
                PlaybackPanelViewModel? playingPanel = null;
                SettingsDialogViewModel? dialog = null;
                var completion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
                try
                {
                    Settings values = CreateValidStandaloneSettings(root);
                    values.PlayerResamplingQuality = 4;
                    values.PlayerMixerThreadCount = 1;
                    values.PlayerWASAPIParam = originalEventMode;
                    var settingsSession = new CountingSettingsEditSession(values);
                    var sequence = new List<string>();
                    settingsSession.SaveObserved = () => sequence.Add("save");
                    var factory = new TestSettingsDialogPlayerFactoryPort(sequence);
                    var runtime = new TestSettingsDialogPlaybackRuntimePort(sequence);
                    MainWindowViewModel viewModel = CreateViewModel(
                        settingsSession,
                        firstStartup: false,
                        playerFactoryPort: factory,
                        playbackRuntimePort: runtime);
                    SetActiveLibraryProfile(viewModel, true);
                    AttachPlaylistTables(viewModel, root);
                    var currentPlayer = new PlaybackPanelViewModelTests.PreloadBmsPlayer
                    {
                        BeginOperation = _ => new PlaybackStartOperation(Task.CompletedTask, completion.Task),
                        CloseCompletion = () => { completion.TrySetCanceled(); return Task.CompletedTask; }
                    };
                    playingPanel = viewModel.PlaybackPanel;
                    await playingPanel.ReplacePlayerAsync(currentPlayer);
                    string chartPath = Path.Combine(root, "current.bms");
                    File.WriteAllText(chartPath, "#PLAYER 1\n#BPM 120\n#00111:00\n");
                    ChartFile currentChart = (ChartTestValues.Empty() with { Path = chartPath });
                    viewModel.MainChartList.Rows = new List<object> { currentChart };
                    await playingPanel.StartAtIndex(0);
                    Assert.AreSame(currentChart, playingPanel.NowPlayingChart);
                    Assert.IsTrue(playingPanel.IsPlaying);
                    dialog = viewModel.SettingDialog;
                    dialog.AttachPresentationPort(new RecordingSettingsDialogPresentationPort(sequence.Add));
                    if (eventOnly) { dialog.PlayerWASAPIParam = !originalEventMode; }
                    else { dialog.PlayerResamplingQuality = 2; dialog.PlayerMixerThreadCount = 4; }

                    await dialog.ApplySettingsAsync();

                    CollectionAssert.AreEqual(
                        new[] { "save", "close" },
                        sequence);
                    Assert.AreEqual(eventOnly ? 4 : 2, values.PlayerResamplingQuality);
                    Assert.AreEqual(eventOnly ? 1 : 4, values.PlayerMixerThreadCount);
                    Assert.AreEqual(eventOnly ? !originalEventMode : originalEventMode, values.PlayerWASAPIParam);
                    Assert.AreEqual(0, runtime.ApplyCount);
                    Assert.AreEqual(0, runtime.NotifyCount);
                    Assert.AreEqual(1, settingsSession.SaveCount);
                    Assert.IsNull(runtime.LastReplacementPlayer);
                    Assert.AreSame(currentChart, playingPanel.NowPlayingChart);
                    Assert.IsTrue(playingPanel.IsPlaying);
                    Assert.IsFalse(completion.Task.IsCompleted);
                    CollectionAssert.AreEqual(new[] { chartPath }, currentPlayer.Starts.ToArray());
                }
                finally
                {
                    try
                    {
                        if (playingPanel != null) { await playingPanel.StopPlayback(closeProcess: true); }
                    }
                    finally
                    {
                        completion.TrySetCanceled();
                        try { await completion.Task; }
                        catch (OperationCanceledException) { }
                        finally
                        {
                            dialog?.Dispose();
                            Directory.Delete(root, recursive: true);
                        }
                    }
                }
            }
            TestUiDispatcherHost.AwaitTaskOnDispatcher(VerifyAsync(), "audio-settings-current-playback");
        });
    }

    /// <summary>thread 数だけの変更を保存して Close し、現在の player を交換しないことを確認して、品質変更と結合した場合だけ保存する誤りを検出します。</summary>
    [TestMethod]
    public async Task ApplySettingsAsync_PlayerMixerThreadOnlyChangeIsSaved()
    {
        string root = CreateTemporaryRoot();
        try
        {
            Settings values = CreateValidStandaloneSettings(root);
            values.PlayerMixerThreadCount = 1;
            var settingsSession = new CountingSettingsEditSession(values);
            var sequence = new List<string>();
            settingsSession.SaveObserved = () => sequence.Add("save");
            var runtime = new TestSettingsDialogPlaybackRuntimePort(sequence);
            MainWindowViewModel viewModel = CreateViewModel(
                settingsSession,
                firstStartup: false,
                playbackRuntimePort: runtime);
            SetActiveLibraryProfile(viewModel, true);
            AttachPlaylistTables(viewModel, root);
            using SettingsDialogViewModel dialog = viewModel.SettingDialog;
            dialog.AttachPresentationPort(new RecordingSettingsDialogPresentationPort(sequence.Add));
            dialog.PlayerMixerThreadCount = 3;

            Assert.IsTrue(dialog.HasPendingSettingChanges());
            await dialog.ApplySettingsAsync();

            CollectionAssert.AreEqual(new[] { "save", "close" }, sequence);
            Assert.AreEqual(3, values.PlayerMixerThreadCount);
            Assert.IsNull(runtime.LastReplacementPlayer);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    /// <summary>保存と Close 後の player 生成失敗を通知し、player 適用・設定通知・再表示を行わないことを確認して、失敗した交換を正常扱いする誤りを検出します。</summary>
    [TestMethod]
    public async Task ApplySettingsAsync_PlayerFactoryFailureLeavesPlaybackUntouched()
    {
        string root = CreateTemporaryRoot();
        try
        {
            string playerPath = Path.Combine(root, "ubmplay.exe");
            File.WriteAllText(playerPath, string.Empty);
            var settingsSession = new CountingSettingsEditSession(CreateValidStandaloneSettings(root));
            var sequence = new List<string>();
            settingsSession.SaveObserved = () => sequence.Add("save");
            var factory = new TestSettingsDialogPlayerFactoryPort(sequence)
            {
                ConfiguredFactoryFailure = new InvalidOperationException("configured player failure")
            };
            var runtime = new TestSettingsDialogPlaybackRuntimePort(sequence);
            Exception? reportedFailure = null;
            MainWindowViewModel viewModel = CreateViewModel(
                settingsSession,
                firstStartup: false,
                reportSettingsApplyFailure: exception => reportedFailure = exception,
                playerFactoryPort: factory,
                playbackRuntimePort: runtime);
            SetActiveLibraryProfile(viewModel, true);
            AttachPlaylistTables(viewModel, root);
            using SettingsDialogViewModel dialog = viewModel.SettingDialog;
            var presentation = new RecordingSettingsDialogPresentationPort(sequence.Add);
            dialog.AttachPresentationPort(presentation);
            dialog.uBMplayPath = playerPath;
            dialog.UsePlayeruBMplay = true;

            await dialog.ApplySettingsAsync();

            CollectionAssert.AreEqual(new[] { "save", "close", "factory-configured" }, sequence);
            Assert.IsNotNull(reportedFailure);
            StringAssert.Contains(reportedFailure!.Message, "configured player failure");
            Assert.AreEqual(0, runtime.ApplyCount);
            Assert.AreEqual(0, runtime.NotifyCount);
            CollectionAssert.Contains(presentation.Requests, "close");
            CollectionAssert.DoesNotContain(presentation.Requests, "open");
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    /// <summary>単独モードでも LR2 player 選択の捕捉設定を構成の factory に渡し、生成・適用・通知へ進むことを確認して、モードで外部 player を誤選択する接続不良を検出します。</summary>
    [TestMethod]
    public async Task ApplySettingsAsync_Lr2bodyReplacementUsesConfiguredFactoryInStandaloneMode()
    {
        string root = CreateTemporaryRoot();
        try
        {
            (string songDbPath, string configPath) = CreateValidLr2Layout(root);
            Settings settings = CreateValidStandaloneSettings(root);
            settings.OperationModeLR2DB = false;
            settings.LR2RootPath = root;
            settings.LR2SongDBPath = songDbPath;
            settings.LR2ConfigXmlPath = configPath;
            var settingsSession = new CountingSettingsEditSession(settings);
            var sequence = new List<string>();
            settingsSession.SaveObserved = () => sequence.Add("save");
            var factory = new TestSettingsDialogPlayerFactoryPort(sequence);
            var runtime = new TestSettingsDialogPlaybackRuntimePort(sequence);
            var composition = new ApplicationComposition(
                settingsEditSession: settingsSession,
                reportSettingsApplyFailure: _ => { },
                uiScheduler: new WpfUiScheduler(() => Dispatcher.CurrentDispatcher),
                applicationLifetime: TestApplicationContext.CreateLifetime(),
                cultureCatalog: TestApplicationContext.CreateCultureCatalog());
            MainWindowViewModel viewModel = composition.CreateMainWindowViewModel();
            viewModel.SettingDialog.Dispose();
            var workspace = new ComposedSettingsDialogWorkspacePort(settingsSession.Values);
            var state = new ActiveSettingsDialogStatePort();
            using SettingsDialogViewModel dialog = composition.CreateSettingDialogViewModel(
                state,
                workspace,
                workspace,
                viewModel.PlayHistory,
                viewModel.LibraryFolderTree,
                factory,
                runtime,
                viewModel.Lr2SongDbSyncWorkflow);
            dialog.AttachPresentationPort(new RecordingSettingsDialogPresentationPort(sequence.Add));
            dialog.UsePlayerLR2body = true;

            await dialog.ApplySettingsAsync();

            CollectionAssert.AreEqual(
                new[] { "save", "close", "factory-configured", "apply", "notify" },
                sequence);
            Assert.IsNotNull(factory.LastConfiguredSettings);
            Assert.IsFalse(factory.LastConfiguredSettings!.OperationModeLR2DB);
            Assert.IsTrue(factory.LastConfiguredSettings.UsePlayerLR2body);
            Assert.AreEqual(1, runtime.ApplyCount);

        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    /// <summary>URL 補完設定の保存は player を交換せず、Close 後に再生主体の設定変更通知だけを一回出すことを確認して、再生以外の保存通知漏れを検出します。</summary>
    [TestMethod]
    public async Task ApplySettingsAsync_NonPlaybackImpactStillNotifiesPlaybackOnce()
    {
        string root = CreateTemporaryRoot();
        try
        {
            var settingsSession = new CountingSettingsEditSession(CreateValidStandaloneSettings(root));
            var sequence = new List<string>();
            settingsSession.SaveObserved = () => sequence.Add("save");
            var runtime = new TestSettingsDialogPlaybackRuntimePort(sequence);
            MainWindowViewModel viewModel = CreateViewModel(
                settingsSession,
                firstStartup: false,
                playbackRuntimePort: runtime);
            SetActiveLibraryProfile(viewModel, true);
            AttachPlaylistTables(viewModel, root);
            using SettingsDialogViewModel dialog = viewModel.SettingDialog;
            dialog.AttachPresentationPort(new RecordingSettingsDialogPresentationPort(sequence.Add));
            dialog.OverwritePlaylistUrlsWithCompletion = !dialog.OverwritePlaylistUrlsWithCompletion;

            await dialog.ApplySettingsAsync();

            CollectionAssert.AreEqual(new[] { "save", "close", "notify" }, sequence);
            Assert.AreEqual(1, runtime.NotifyCount);
            Assert.AreEqual(0, runtime.ApplyCount);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    /// <summary>方式だけを変えたSave/Applyが設定provider保存から全表gzip出力まで待ち、正本hashと次回省略を保つことを確認します。</summary>
    [DataTestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public async Task BmtHashModeSaveAndApply_ReoutputsAllTablesAndKeepsDatabaseHashes(bool apply)
    {
        string root = CreateTemporaryRoot();
        try
        {
            string beatoraja = Path.Combine(root, "beatoraja");
            string output = Path.Combine(beatoraja, "table");
            Directory.CreateDirectory(output);
            File.WriteAllText(Path.Combine(beatoraja, "beatoraja.jar"), string.Empty);
            string config = Path.Combine(beatoraja, BeatorajaConfigService.ConfigFileName);
            File.WriteAllText(config, "{\"tablepath\":\"table\",\"tableURL\":[\"https://example.com/unmanaged\"]}");
            Settings values = CreateValidStandaloneSettings(root);
            values.BeatorajaRootPath = beatoraja;
            values.BeatorajaBmtTablePath = output;
            values.EnableBeatorajaBmtOutput = true;
            values.RegisterBeatorajaBmtUrls = true;
            values.BeatorajaBmtHashOutputMode = "Original";
            values.Save();
            var session = new CountingSettingsEditSession(values) { PersistOwnedValues = true };
            List<Exception> failures = [];
            MainWindowViewModel owner = CreateViewModel(session, firstStartup: false, reportSettingsApplyFailure: failures.Add,
                playbackRuntimePort: new TestSettingsDialogPlaybackRuntimePort());
            using SettingsDialogViewModel dialog = owner.SettingDialog;
            SetActiveLibraryProfile(owner, true);
            string db = BmsPlaylistTestSupport.CreateTempSongDbPath(root);
            PlaylistPersistenceRepository.EnsureSchema(db);
            BMSPlaylist playlist = AttachPlaylistTables(owner, root, existingDatabasePath: db);
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
            var presentation = new RecordingSettingsDialogPresentationPort();
            owner.SettingDialog.AttachPresentationPort(presentation);
            owner.SettingDialog.BeatorajaBmtHashOutputMode = "PreferSha256Only";
            if (apply) { await owner.SettingDialog.ApplySettingsAsync(); }
            else { await owner.SettingDialog.SaveSettings(); }
            Assert.AreEqual(0, failures.Count, string.Join(Environment.NewLine, failures));
            Assert.AreEqual(1, session.SaveCount);
            values.Reload();
            Assert.AreEqual("PreferSha256Only", values.BeatorajaBmtHashOutputMode);
            string[] files = Directory.GetFiles(output, "*.bmt");
            Assert.AreEqual(2, files.Length);
            foreach (string file in files)
            {
                using var stream = new System.IO.Compression.GZipStream(File.OpenRead(file), System.IO.Compression.CompressionMode.Decompress);
                using var reader = new StreamReader(stream);
                Newtonsoft.Json.Linq.JToken? song = Newtonsoft.Json.Linq.JObject.Parse(reader.ReadToEnd()).SelectToken("folder[0].songs[0]");
                Assert.IsNotNull(song);
                Assert.IsNull(song["md5"]);
                Assert.AreEqual(64, song.Value<string>("sha256")?.Length);
            }
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
            await playlist.BmtOutput.ExportTablesAsync(tables, "next-same-mode");
            foreach (string file in files)
            {
                CollectionAssert.AreEqual(bytes[file], File.ReadAllBytes(file));
                Assert.AreEqual(times[file], File.GetLastWriteTimeUtc(file));
            }
        }
        finally { Directory.Delete(root, recursive: true); }
    }

    /// <summary>保存と Close 後に必須 BMT 出力の実終端を待ち、任意 URL 補完の予約失敗を設定適用失敗にせず、同補完だけを scheduler へ渡すことを確認して、必要出力を予約だけで完了扱いする誤りを検出します。</summary>
    [DataTestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public async Task ApplySettingsAsync_AwaitsRequiredPlaylistOutputAndKeepsOptionalUrlCompletionIndependent(bool failScheduling)
    {
        string root = CreateTemporaryRoot();
        try
        {
            Settings settings = CreateValidStandaloneSettings(root);
            settings.RegisterBeatorajaBmtUrls = false;
            var settingsSession = new CountingSettingsEditSession(settings);
            var scheduled = new List<string>();
            var runtime = new TestSettingsDialogPlaybackRuntimePort();
            var failures = new List<Exception>();
            MainWindowViewModel viewModel = CreateViewModel(
                settingsSession,
                firstStartup: false,
                playbackRuntimePort: runtime,
                reportSettingsApplyFailure: failures.Add);
            SetActiveLibraryProfile(viewModel, true);
            BMSPlaylist playlist = AttachPlaylistTables(viewModel, root, scheduled);
            if (failScheduling)
            {
                playlist.StartupBackgroundTaskScheduler = (operation, reason, _, _) =>
                {
                    scheduled.Add(operation + ":" + reason);
                    throw new IOException("optional scheduler failed");
                };
            }
            var presentation = new RecordingSettingsDialogPresentationPort();
            viewModel.SettingDialog.AttachPresentationPort(presentation);
            int completedOutputs = 0;
            var startedOutputs = new List<(bool CloseRequested, int SaveCount)>();
            playlist.BmtOutput.RequestProgressReporter = (_, running) =>
            {
                if (running)
                {
                    // 任意通知境界に表明を置かず、開始時の事実を保存して実Applyの終端後に検証する。
                    startedOutputs.Add((presentation.Requests.Contains("close"), settingsSession.SaveCount));
                }
                else { completedOutputs++; }
            };
            using SettingsDialogViewModel dialog = viewModel.SettingDialog;
            dialog.OverwritePlaylistUrlsWithCompletion = !dialog.OverwritePlaylistUrlsWithCompletion;
            dialog.RegisterBeatorajaBmtUrls = !dialog.RegisterBeatorajaBmtUrls;

            await dialog.ApplySettingsAsync();

            Assert.AreEqual(1, startedOutputs.Count);
            Assert.IsTrue(startedOutputs[0].CloseRequested, "再読込み不要の実出力も保存とClose後に開始します。");
            Assert.AreEqual(1, startedOutputs[0].SaveCount);
            CollectionAssert.AreEqual(new[] { "playlist_url_completion:SettingDialog.SaveSettings" }, scheduled);
            Assert.AreEqual(1, completedOutputs, "必要BMT出力は外部schedulerへ予約せず、Save/Applyの実Taskが終端まで待ちます。");
            Assert.AreEqual(1, runtime.NotifyCount);
            Assert.AreEqual(0, failures.Count, "任意予約の失敗を、確定した設定・必須出力の失敗へ変えません。");
            Assert.AreEqual(1, settingsSession.SaveCount);
            Assert.IsFalse(dialog.IsEditCompletionInProgress);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    /// <summary>稼働中 profile の未変更適用は Close 要求だけを一回出し、編集中状態を解除することを確認して、無変更で処理を再実行する誤りを検出します。</summary>
    [TestMethod]
    public async Task ApplySettingsAsync_ActiveProfileWithoutChanges_PublishesSingleCloseRequest()
    {
        string root = CreateTemporaryRoot();
        try
        {
            var settingsSession = new CountingSettingsEditSession(CreateValidStandaloneSettings(root));
            MainWindowViewModel viewModel = CreateViewModel(settingsSession, firstStartup: false);
            SetActiveLibraryProfile(viewModel, true);
            using SettingsDialogViewModel dialog = viewModel.SettingDialog;
            var presentation = new RecordingSettingsDialogPresentationPort();
            dialog.AttachPresentationPort(presentation);

            await dialog.ApplySettingsAsync();

            CollectionAssert.AreEqual(
                new[] { "close" },
                presentation.Requests);
            Assert.IsFalse(dialog.IsEditCompletionInProgress);
            Assert.IsTrue(dialog.IsEditCompletionEnabled);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    /// <summary>音声テストの runtime が保留中は適用・取消・Close を拒み、実 Task 終端後に編集を再開することを確認して、native 使用中の保存や画面終了を検出します。</summary>
    [TestMethod]
    public async Task AudioDeviceTestBusy_BlocksSaveCancellationAndPresentationCloseUntilRelease()
    {
        string root = CreateTemporaryRoot();
        try
        {
            var settingsSession = new CountingSettingsEditSession(CreateValidStandaloneSettings(root));
            MainWindowViewModel viewModel = CreateViewModel(settingsSession, firstStartup: false);
            SetActiveLibraryProfile(viewModel, true);
            var runtimeStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var releaseRuntime = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var workflow = new AudioDeviceTestWorkflowOwner(
                new TestAudioDeviceTestPlaybackPort(),
                new BlockingAudioDeviceTestRuntime(runtimeStarted, releaseRuntime));
            viewModel.SettingDialog.Dispose();
            SettingsDialogViewModel dialog = new(
                viewModel,
                viewModel.PlaylistWorkspace,
                viewModel.PlaylistWorkspace,
                viewModel.PlayHistory,
                viewModel.LibraryFolderTree,
                new TestSettingsDialogPlayerFactoryPort(),
                new TestSettingsDialogPlaybackRuntimePort(),
                viewModel.Lr2SongDbSyncWorkflow,
                settingsSession,
                applicationLifetime: TestApplicationContext.CreateLifetime(),
                cultureCatalog: TestApplicationContext.CreateCultureCatalog(),
                audioDeviceTestWorkflow: workflow,
                externalShellGateway: ExternalShellGatewayPolicy.Current,
                applicationPathSnapshot: ApplicationPathPolicy.Current,
                audioDeviceCatalog: new TestAudioDeviceCatalog(),
                audioSettingsGateway: new TestAudioSettingsGateway());
            var presentation = new RecordingSettingsDialogPresentationPort();
            dialog.AttachPresentationPort(presentation);
            dialog.SetPresentationActive(active: true);

            Task? testTask = null;
            try
            {
                testTask = dialog.RunAudioDeviceTestAsync();
                await TestUiDispatcherHost.AwaitNotificationAsync(runtimeStarted.Task, testTask, "settings.audio-test-runtime");

                Assert.IsFalse(dialog.IsEditCompletionEnabled);
                Assert.IsFalse(dialog.IsEditCancellationEnabled);
                Assert.IsTrue(dialog.IsAudioDeviceTestInProgress);

                await dialog.ApplySettingsAsync();
                dialog.CancelCommand.Execute();
                Assert.AreEqual(0, settingsSession.SaveCount);
                CollectionAssert.DoesNotContain(presentation.Requests, "close");
                Assert.IsTrue(dialog.IsAudioDeviceTestInProgress);

                releaseRuntime.TrySetResult();
                await testTask;
                Assert.IsTrue(dialog.IsEditCompletionEnabled);
                Assert.IsTrue(dialog.IsEditCancellationEnabled);
                Assert.IsNotNull(dialog.AudioDeviceTestStatusMessage);
            }
            finally
            {
                releaseRuntime.TrySetResult();
                if (testTask != null)
                {
                    await testTask;
                }
                dialog.Dispose();
            }
        }
        finally
        {
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
            Assert.AreEqual(originalResamplingQuality, settings.PlayerResamplingQuality);
            Assert.AreEqual(originalMixerThreadCount, settings.PlayerMixerThreadCount);
            AssertExplicitAudioSettingsUnchanged(settings, audioGateway);
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
            Assert.AreEqual(SampleFormat.AUTO, settings.PlayerFormat);
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

    /// <summary>通常Folderは保存と実Close後に実差分を同じL/Pで処理し、DBと画面へ今回の譜面を反映します。</summary>
    [TestMethod]
    public void ApplySettingsAsync_FolderChange_CompletesRealFileDiffAfterClosing()
    {
        string root = CreateTemporaryRoot();
        string addedRoot = Path.Combine(root, "added");
        Directory.CreateDirectory(addedRoot);
        string chartA = Path.Combine(root, "a.bms");
        string chartB = Path.Combine(addedRoot, "b.bms");
        File.WriteAllText(chartA, "#PLAYER 1\n#TITLE Folder A\n#BPM 120\n#00111:01\n");
        using var release = new ManualResetEventSlim();
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        try
        {
            TestUiDispatcherHost.Invoke(() =>
            {
                Settings values = CreateValidStandaloneSettings(root);
                values.ScanBmsFilesOnStartup = true;
                values.StartupSelectInstallPending = false;
                var session = new CountingSettingsEditSession(values);
                var failures = new List<Exception>();
                var scanner = new SettingsChartFileScanner(CapturedChartFileScanner.FromFixture([chartA],
                    new Dictionary<string, IEnumerable<string>> { [root] = [] }, [root]));
                var ui = new TestUiScheduler(() => TestUiDispatcherHost.Dispatcher);
                int defaultPlayerCount = 0;
                var composition = new ApplicationComposition(settingsEditSession: session, reportSettingsApplyFailure: failures.Add,
                    uiScheduler: ui, applicationLifetime: TestApplicationContext.CreateLifetime(firstStartup: false),
                    cultureCatalog: TestApplicationContext.CreateCultureCatalog(), applicationPathSnapshot: ApplicationPathSnapshot.FromExecutablePath(Path.Combine(root, "BeMusicSeeker.exe")),
                    chartFileScanner: scanner, rootFileEnumerator: new FastRootFileEnumerator(),
                    defaultBmsPlayerFactory: () => { defaultPlayerCount++; return new RecordingPlaybackPlayer(); });
                var factory = new StartupLibraryConstructionTestSupport.RecordingDelegatingStartupLibraryFactory(composition, []) { InitialSearchTargets = [root] };
                var owner = new MainWindowViewModel(composition, factory);
                var presentation = new RecordingSettingsDialogPresentationPort();
                owner.SettingDialog.AttachPresentationPort(presentation);
                Task? apply = null;
                try
                {
                    Task<bool> initialize = owner.InitializeAsync();
                    TestUiDispatcherHost.AwaitTaskOnDispatcher(initialize, "folder-settings-initialize");
                    Assert.IsTrue(initialize.GetAwaiter().GetResult());
                    Assert.AreEqual(1, defaultPlayerCount, "単独動作の実構築は既定playerを一回接続します。");
                    File.WriteAllText(chartB, "#PLAYER 1\n#TITLE Folder B\n#BPM 120\n#00111:01\n");
                    var next = CapturedChartFileScanner.FromFixture([chartA, chartB],
                        new Dictionary<string, IEnumerable<string>> { [root] = [], [addedRoot] = [] }, [root]);
                    next.ScanObserved = () => { entered.TrySetResult(); release.Wait(); };
                    scanner.Current = next;
                    owner.SettingDialog.StandaloneBmsRootPathList.Add(addedRoot);
                    Assert.AreEqual(SettingsDialogViewModel.RestartMode.FolderOnly, owner.SettingDialog.IsNeedRestartForSaved());
                    apply = owner.SettingDialog.ApplySettingsAsync();
                    TestUiDispatcherHost.AwaitTaskOnDispatcher(TestUiDispatcherHost.AwaitNotificationAsync(entered.Task, apply, "folder-settings-scan"), "folder-settings-scan");
                    Assert.IsFalse(apply.IsCompleted);
                    Assert.IsTrue(composition.OperationAdmission.IsActive);
                    Assert.IsTrue(composition.PlaylistOperationAdmission.IsActive);
                    CollectionAssert.Contains(presentation.Requests, "close");
                    Assert.AreEqual(1, session.SaveCount);
                    release.Set();
                    TestUiDispatcherHost.AwaitTaskOnDispatcher(apply, "folder-settings-terminal");
                    Assert.AreEqual(0, failures.Count, string.Join(Environment.NewLine, failures));
                    Assert.AreEqual(1, defaultPlayerCount, "Folder段階はplayerの再構築を追加しません。");
                    BMSLibrary library = factory.CreatedLibrary ?? throw new InvalidOperationException("Actual library is unavailable.");
                    Assert.AreEqual("Folder B", library.BmsCharts.Single(chart => chart.Path == chartB).RawTitle);
                    using (LR2SongDBExtended db = new BmsLibraryDbGateway(composition.ApplicationPathSnapshot.StandaloneSongDbPath).OpenSongDbReadOnly())
                    { Assert.AreEqual("Folder B", db.Table<LR2SongDB.song>().Single(row => row.path == chartB).title); }
                    Assert.IsTrue(owner.MainChartList.Rows.OfType<LibraryChartRow>().Any(row => row.Title == "Folder B"));
                    Assert.IsFalse(composition.OperationAdmission.IsActive);
                    Assert.IsFalse(composition.PlaylistOperationAdmission.IsActive);
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

    /// <summary>保存後のディレクトリ失敗は L/P と編集受付を解放して警告し、自動再表示せず、次の明示表示・適用で保留再読込みを完了できることを確認します。</summary>
    [TestMethod]
    public async Task ApplySettingsAsync_DirectoryWarningReturnsAfterCleanupAndNextExplicitPresentationCanRetry()
    {
        string root = CreateTemporaryRoot();
        string addedRoot = CreateTemporaryRoot();
        MainWindowViewModel? viewModel = null;
        try
        {
            var settingsSession = new CountingSettingsEditSession(CreateValidStandaloneSettings(root));
            var failures = new List<Exception>();
            var dialogs = new RecordingRootDialogService();
            int reloadCount = 0;
            var directoryFailure = new LibraryDirectoryPreflightException(
                LibraryDirectoryPreflightUse.BmsRoot, Path.Combine(root, "missing"),
                LibraryDirectoryPreflightFailureCause.NotFound, "missing");
            viewModel = CreateViewModel(settingsSession, firstStartup: false,
                reloadFileDiff: _ => Task.FromResult(++reloadCount == 1
                    ? new StartupInitializationResult(StartupInitializationOutcome.OperationFailed, Failure: directoryFailure, OperationKind: StartupProgressOperationKind.ReloadFileDiff)
                    : new StartupInitializationResult(StartupInitializationOutcome.Succeeded, OperationKind: StartupProgressOperationKind.ReloadFileDiff)),
                reportSettingsApplyFailure: failures.Add, dialogs: dialogs);
            SetActiveLibraryProfile(viewModel, true);
            using SettingsDialogViewModel dialog = viewModel.SettingDialog;
            dialog.StandaloneBmsRootPathList.Add(addedRoot);
            var presentation = new RecordingSettingsDialogPresentationPort();
            dialog.AttachPresentationPort(presentation);
            bool cleanupObserved = false;
            bool retryCompleted = false;
            ApplicationComposition composition = MainWindowViewModelTestFactory.GetComposition(viewModel);
            dialogs.MessageObservedAsync = () =>
            {
                cleanupObserved = !dialog.IsEditCompletionInProgress && dialog.IsFileDiffReloadPending
                    && !composition.OperationAdmission.IsActive && !composition.PlaylistOperationAdmission.IsActive;
                return Task.CompletedTask;
            };

            await dialog.ApplySettingsAsync();
            Assert.IsTrue(cleanupObserved);
            CollectionAssert.Contains(presentation.Requests, "close");
            CollectionAssert.DoesNotContain(presentation.Requests, "open");
            dialog.RequestOpen();
            await dialog.ApplySettingsAsync();
            retryCompleted = !dialog.HasPendingSettingChanges() && !dialog.IsFileDiffReloadPending;

            Assert.IsTrue(cleanupObserved);
            Assert.IsTrue(retryCompleted);
            Assert.AreEqual(2, reloadCount);
            Assert.AreEqual(1, dialogs.MessageCount);
            Assert.AreEqual(0, failures.Count);
            CollectionAssert.Contains(presentation.Requests, "close");
        }
        finally
        {
            viewModel?.SettingDialog.Dispose();
            Directory.Delete(root, recursive: true);
            Directory.Delete(addedRoot, recursive: true);
        }
    }

    /// <summary>Failure結果のScore失敗は保存済み値とpendingを保持し、次の明示Score適用は追加保存せず再読込みします。Allへの変更時も最終成功だけが残ったpendingを解除します。</summary>
    [DataTestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public async Task ApplySettingsAsync_InitialRetryAfterFullReloadClearsScoreReloadPending(bool retryWithFullReload)
    {
        string root = CreateTemporaryRoot();
        try
        {
            var settingsSession = new CountingSettingsEditSession(CreateValidStandaloneSettings(root));
            int reloadCount = 0;
            int initializeCount = 0;
            MainWindowViewModel viewModel = CreateViewModel(
                settingsSession,
                firstStartup: false,
                initializeOwner: _ =>
                {
                    initializeCount++;
                    return Task.FromResult(initializeCount > 1);
                },
                reloadScoresOnly: _ => Task.FromResult(++reloadCount == 1
                    ? new StartupInitializationResult(StartupInitializationOutcome.OperationFailed, Failure: new InvalidOperationException("score reload failed"), OperationKind: StartupProgressOperationKind.ScoreOnly)
                    : new StartupInitializationResult(StartupInitializationOutcome.Succeeded, OperationKind: StartupProgressOperationKind.ScoreOnly)),
                reportSettingsApplyFailure: _ => { });
            SetActiveLibraryProfile(viewModel, true);
            using SettingsDialogViewModel dialog = viewModel.SettingDialog;
            dialog.BeatorajaPlayerId = "player2";

            await dialog.ApplySettingsAsync();

            Assert.AreEqual(1, reloadCount);
            Assert.IsTrue(dialog.HasPendingSettingChanges());
            Assert.IsTrue(dialog.IsScoreReloadPending);
            Assert.AreEqual("player2", settingsSession.Values.BeatorajaPlayerId);
            Assert.AreEqual(1, settingsSession.SaveCount);

            if (!retryWithFullReload)
            {
                dialog.RequestOpen();
                await dialog.ApplySettingsAsync();
                Assert.AreEqual(2, reloadCount);
                Assert.AreEqual(0, initializeCount);
                Assert.AreEqual(1, settingsSession.SaveCount);
                Assert.IsFalse(dialog.IsScoreReloadPending);
                Assert.IsFalse(dialog.HasPendingSettingChanges());
                return;
            }

            SetPrivateField(dialog, "tempOperationModeLR2DB", !dialog.OperationModeLR2DB);
            await dialog.ApplySettingsAsync();

            Assert.AreEqual(1, initializeCount);
            Assert.IsFalse(viewModel.HasActiveLibraryProfile);
            Assert.IsTrue(dialog.HasPendingSettingChanges());

            await dialog.ApplySettingsAsync();

            Assert.AreEqual(2, initializeCount);
            Assert.IsFalse(dialog.HasPendingSettingChanges());
            Assert.IsTrue(dialog.IsEditCancellationEnabled);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
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
    [DataRow("apply", false)] // 実Applyの保存・Close後に設定不備が判明し、警告失敗後も再編集へ戻ります。
    [DataRow("apply", true)] // 実ApplyのFailed結果でもcleanup・元ファイルと再編集を保持します。
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
            if (entry != "apply") { InvalidateConfig(); }
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
                    if (request == "close" && entry == "apply") { InvalidateConfig(); }
                    if (request == "open") { ObserveCleanup("open", string.Empty); }
                }));
                try
                {
                    if (entry == "apply")
                    {
                        Assert.IsTrue(dialog.CheckValidation(out string validationError), validationError);
                        dialog.ShowRecommUpdatedMsg = !dialog.ShowRecommUpdatedMsg;
                        Task operation = dialog.ApplySettingsAsync();
                        TestUiDispatcherHost.AwaitTaskOnDispatcher(operation, "invalid-lr2-warning-failed-apply");
                        Assert.AreEqual(1, session.SaveCount);
                    }
                    else if (entry == "typed")
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
                    Assert.AreEqual(entry == "typed" ? 2 : 3, cleanupObservations.Count);
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
                    CollectionAssert.AreEqual(entry == "apply" ? new[] { "close", "warning", "open" }
                        : entry == "startup" ? new[] { "warning", "open" } : new[] { "warning" }, sequence);
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
                var library = new TestBmsLibrary(
                    songDbPath,
                    () => config,
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
                TestBmsPlaylist playlist = MainWindowViewModelTestFactory.CreatePlaylist(songDbPath, settings, () => config, library: library);
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
                        Assert.IsFalse(viewModel.SettingDialog.CheckValidation(out string validationError));
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
                // 一つの補完や現在のleaseだけでは、後からLを取得する受理済み保守の終端を保証しません。
                // 必須起動成功の条件は変えず、次の明示操作の前提として全後続の終端を待ちます。
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
                        TestUiDispatcherHost.AwaitTaskOnDispatcher(backgroundTerminal.Task, "late-directory-startup-background-terminal");
                    }
                }
                finally
                {
                    viewModel.ProgressHub.PropertyChanged -= BackgroundChanged;
                }
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
                    Assert.IsTrue(dialog.CheckValidation(out string validationError), validationError);
                    Task operation = dialog.ApplySettingsAsync();
                    TestUiDispatcherHost.AwaitTaskOnDispatcher(operation, "startup-construction-failure-apply");
                    Assert.AreEqual(1, session.SaveCount);
                    Assert.AreEqual(savedValue, settings.ShowRecommUpdatedMsg);
                    Assert.IsFalse(dialog.HasPendingSettingChanges());
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
            CollectionAssert.AreEqual(entry == "apply" ? new[] { "save", "close", "error" } : new[] { "error" }, sequence);
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

    /// <summary>初回／通常と LR2／単独の設定適用で保存→実 Close 完了→初回通知完了→初期化を順に待ち、保留中の重複適用・取消を拒むことを確認します。</summary>
    [DataTestMethod]
    [DataRow(false, false)]
    [DataRow(true, false)]
    [DataRow(false, true)]
    [DataRow(true, true)]
    public async Task ApplySettingsAsync_InitialSettings_ClosesBeforeNotificationAndAwaitsInitialization(
        bool firstStartup,
        bool useLr2)
    {
        string root = CreateTemporaryRoot();
        var closeReleased = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var messageReleased = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var initializationReleased = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        Task? applyTask = null;
        SettingsDialogViewModel? dialog = null;
        try
        {
            Settings values = CreateValidStandaloneSettings(root);
            if (useLr2)
            {
                (string songDb, string configPath) = CreateValidLr2Layout(Path.Combine(root, "lr2"));
                string musicRoot = Path.Combine(root, "music");
                Directory.CreateDirectory(musicRoot);
                var config = new BeMusicSeeker.Models.LR2.LR2Config(configPath);
                config.AddBMSSearchDirectories([musicRoot]);
                config.Save();
                values.OperationModeLR2DB = true;
                values.LR2RootPath = Path.Combine(root, "lr2");
                values.LR2SongDBPath = songDb;
                values.LR2ConfigXmlPath = configPath;
                values.BMSInstallDir = musicRoot;
                values.LR2CustomFolderOutputBaseDir = Path.Combine(root, "output");
                values.LR2CustomFolderOutputBaseDirRootType = Path.Combine(root, "root-output");
                values.LR2CustomFolderAdditionalOutputBaseDirs = string.Empty;
            }
            var settingsSession = new CountingSettingsEditSession(values);
            var sequence = new List<string>();
            var firstBoundary = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
            var messageStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var initializationStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var failures = new List<Exception>();
            var dialogs = new RecordingRootDialogService
            {
                MessageObserved = () =>
                {
                    sequence.Add("completion-message");
                    firstBoundary.TrySetResult("message");
                    messageStarted.TrySetResult();
                },
                MessageObservedAsync = () => messageReleased.Task
            };
            int initializeCount = 0;
            MainWindowViewModel viewModel = CreateViewModel(
                settingsSession,
                firstStartup,
                initializeOwner: async _ =>
                {
                    initializeCount++;
                    sequence.Add("initialize-start");
                    firstBoundary.TrySetResult("initialize");
                    initializationStarted.TrySetResult();
                    await initializationReleased.Task;
                    sequence.Add("initialize-completed");
                    return true;
                },
                reportSettingsApplyFailure: failures.Add,
                dialogs: dialogs);
            dialog = viewModel.SettingDialog;
            settingsSession.SaveObserved = () => sequence.Add("save");
            dialog.AttachPresentationPort(new RecordingSettingsDialogPresentationPort(request =>
            {
                sequence.Add(request);
                firstBoundary.TrySetResult(request);
            })
            {
                WaitForClose = () => closeReleased.Task
            });
            dialog.ShowRecommUpdatedMsg = !dialog.ShowRecommUpdatedMsg;
            Assert.IsTrue(dialog.CheckValidation(out string validationError), validationError);

            applyTask = dialog.ApplySettingsAsync();
            await Task.WhenAny(firstBoundary.Task, applyTask);
            Assert.IsTrue(firstBoundary.Task.IsCompletedSuccessfully);
            Assert.AreEqual("close", await firstBoundary.Task);
            CollectionAssert.AreEqual(new[] { "save", "close" }, sequence);
            Assert.IsTrue(dialog.IsEditCompletionInProgress);
            Assert.IsFalse(applyTask.IsCompleted);
            Assert.AreEqual(0, dialogs.MessageCount);
            Assert.AreEqual(0, initializeCount);

            closeReleased.SetResult();
            if (firstStartup)
            {
                await Task.WhenAny(messageStarted.Task, initializationStarted.Task, applyTask);
                Assert.IsTrue(messageStarted.Task.IsCompletedSuccessfully);
                Assert.AreEqual(0, initializeCount);
                Assert.IsFalse(applyTask.IsCompleted);
                messageReleased.SetResult();
            }
            await Task.WhenAny(initializationStarted.Task, applyTask);
            Assert.IsTrue(initializationStarted.Task.IsCompletedSuccessfully);
            Assert.IsTrue(dialog.IsEditCompletionInProgress);
            Assert.IsFalse(applyTask.IsCompleted);
            await dialog.ApplySettingsAsync();
            dialog.CancelCommand.Execute();
            Assert.AreEqual(1, initializeCount);
            initializationReleased.SetResult();
            await applyTask;

            CollectionAssert.AreEqual(
                firstStartup
                    ? new[] { "save", "close", "completion-message", "initialize-start", "initialize-completed" }
                    : new[] { "save", "close", "initialize-start", "initialize-completed" },
                sequence);
            Assert.AreEqual(0, failures.Count);
            Assert.AreEqual(firstStartup ? 1 : 0, dialogs.MessageCount);
            Assert.AreEqual(1, settingsSession.SaveCount);
            Assert.IsFalse(dialog.HasPendingSettingChanges());
            Assert.IsTrue(dialog.IsEditCompletionEnabled);
        }
        finally
        {
            closeReleased.TrySetResult();
            messageReleased.TrySetResult();
            initializationReleased.TrySetResult();
            try
            {
                if (applyTask != null) { await applyTask; }
            }
            finally
            {
                dialog?.Dispose();
                Directory.Delete(root, recursive: true);
            }
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
            config.Save();
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

    /// <summary>保存とClose後の任意完了通知がFailed・owner不在・Task faultでも保存済み事実を保ち、必要初期化へ一回進み再通知・再表示しません。</summary>
    [DataTestMethod]
    [DataRow("Failed")]
    [DataRow("OwnerUnavailable")]
    [DataRow("TaskFault")]
    public async Task ApplySettingsAsync_InitialSettingsNoticeFailurePreservesSaveAndRequiredInitialization(string statusName)
    {
        string root = CreateTemporaryRoot();
        try
        {
            UiDialogStatus status = statusName == "TaskFault" ? UiDialogStatus.Accepted : Enum.Parse<UiDialogStatus>(statusName);
            var settingsSession = new CountingSettingsEditSession(CreateValidStandaloneSettings(root));
            var sequence = new List<string>();
            var dialogs = new RecordingRootDialogService
            {
                MessageResult = status == UiDialogStatus.Failed
                    ? UiDialogResult.Failed(new InvalidOperationException("completion dialog failed"))
                    : UiDialogResult.NotShown(UiDialogStatus.OwnerUnavailable)
            };
            dialogs.MessageObserved = () => sequence.Add("completion-message");
            if (statusName == "TaskFault")
            {
                dialogs.MessageObservedAsync = () => Task.FromException(new IOException("completion notice Task failed"));
            }
            Exception? reportedFailure = null;
            int initializeCount = 0;
            MainWindowViewModel viewModel = CreateViewModel(
                settingsSession,
                firstStartup: true,
                initializeOwner: _ =>
                {
                    initializeCount++;
                    sequence.Add("initialize");
                    return Task.FromResult(true);
                },
                reportSettingsApplyFailure: exception => reportedFailure = exception,
                dialogs: dialogs);
            using SettingsDialogViewModel dialog = viewModel.SettingDialog;
            settingsSession.SaveObserved = () => sequence.Add("save");
            dialog.AttachPresentationPort(new RecordingSettingsDialogPresentationPort(request =>
            {
                sequence.Add(request);
            }));
            dialog.ShowRecommUpdatedMsg = !dialog.ShowRecommUpdatedMsg;
            Assert.IsTrue(dialog.CheckValidation(out string validationError), validationError);

            await dialog.ApplySettingsAsync();

            Assert.IsNull(reportedFailure);
            Assert.AreEqual(1, settingsSession.SaveCount);
            Assert.AreEqual(1, dialogs.MessageCount);
            Assert.AreEqual(1, initializeCount);
            CollectionAssert.AreEqual(new[] { "save", "close", "completion-message", "initialize" }, sequence);
            Assert.IsTrue(dialog.IsEditCompletionEnabled);
            Assert.IsFalse(dialog.HasPendingSettingChanges());
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    /// <summary>保存保留中の二回目適用と取消を拒み、一回の保存・Close に終端することを確認して、完了 guard の再入許可を検出します。</summary>
    [TestMethod]
    public async Task ApplySettingsAsync_CompletionGuardRejectsConcurrentCall()
    {
        string root = CreateTemporaryRoot();
        CountingSettingsEditSession? settingsSession = null;
        SettingsDialogViewModel? dialog = null;
        Task? first = null;
        Task? second = null;
        try
        {
            Settings values = CreateValidStandaloneSettings(root);
            values.PlayerResamplingQuality = 4;
            values.PlayerMixerThreadCount = 2;
            settingsSession = new CountingSettingsEditSession(values)
            {
                BlockSave = true
            };
            MainWindowViewModel viewModel = CreateViewModel(settingsSession, firstStartup: false);
            SetActiveLibraryProfile(viewModel, true);
            dialog = viewModel.SettingDialog;
            var presentation = new RecordingSettingsDialogPresentationPort();
            dialog.AttachPresentationPort(presentation);
            dialog.ShowRecommUpdatedMsg = !dialog.ShowRecommUpdatedMsg;

            first = Task.Run(() => dialog.ApplySettingsAsync());
            settingsSession.SaveEntered.Wait();
            Assert.IsTrue(dialog.IsEditCompletionInProgress);
            second = dialog.ApplySettingsAsync();
            dialog.CancelCommand.Execute();
            settingsSession.ReleaseSave.Set();
            await Task.WhenAll(first, second);

            Assert.AreEqual(1, settingsSession.SaveCount);
            CollectionAssert.AreEqual(
                new[] { "close" },
                presentation.Requests);
            Assert.IsTrue(dialog.IsEditCompletionEnabled);
        }
        finally
        {
            settingsSession?.ReleaseSave.Set();
            try
            {
                if (first != null) { await first; }
                if (second != null) { await second; }
            }
            finally
            {
                dialog?.Dispose();
                settingsSession?.SaveEntered.Dispose();
                settingsSession?.ReleaseSave.Dispose();
                Directory.Delete(root, recursive: true);
            }
        }
    }

    /// <summary>同期／保留後の初期化の false／例外結果は編集受付の解放後に一回再表示し、例外の同一性と保存済み draft を保って明示再試行では追加保存しないことを確認します。</summary>
    [DataTestMethod]
    [DataRow(false, false)]
    [DataRow(false, true)]
    [DataRow(true, false)]
    [DataRow(true, true)]
    public async Task ApplySettingsAsync_InitialInitializationFailureReopensAfterCleanupAndCanRetry(
        bool throws,
        bool completesAsynchronously)
    {
        string root = CreateTemporaryRoot();
        var initializeEntered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var initializeReleased = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        Task? applyTask = null;
        SettingsDialogViewModel? dialog = null;
        try
        {
            var settingsSession = new CountingSettingsEditSession(CreateValidStandaloneSettings(root));
            var failure = new InvalidOperationException("initialization failed");
            var failures = new List<Exception>();
            var sequence = new List<string>();
            int initializeCount = 0;
            MainWindowViewModel viewModel = CreateViewModel(
                settingsSession,
                firstStartup: false,
                initializeOwner: async _ =>
                {
                    initializeCount++;
                    sequence.Add("initialize");
                    initializeEntered.TrySetResult();
                    if (completesAsynchronously)
                    {
                        await initializeReleased.Task;
                    }
                    if (initializeCount > 1)
                    {
                        return true;
                    }
                    if (throws)
                    {
                        throw failure;
                    }
                    return false;
                },
                reportSettingsApplyFailure: failures.Add);
            dialog = viewModel.SettingDialog;
            dialog.ShowRecommUpdatedMsg = !dialog.ShowRecommUpdatedMsg;
            var presentation = new RecordingSettingsDialogPresentationPort(request =>
            {
                if (request == "open")
                {
                    Assert.IsTrue(dialog.IsEditCompletionEnabled);
                    Assert.IsTrue(dialog.IsEditCancellationEnabled);
                    Assert.IsFalse(dialog.HasPendingSettingChanges());
                }
                sequence.Add(request);
            });
            dialog.AttachPresentationPort(presentation);

            applyTask = dialog.ApplySettingsAsync();
            await Task.WhenAny(initializeEntered.Task, applyTask);
            Assert.IsTrue(initializeEntered.Task.IsCompletedSuccessfully);
            if (completesAsynchronously)
            {
                CollectionAssert.AreEqual(new[] { "close", "initialize" }, sequence);
                Assert.IsTrue(dialog.IsEditCompletionInProgress);
                Assert.IsFalse(applyTask.IsCompleted);
            }
            initializeReleased.TrySetResult();
            await applyTask;

            CollectionAssert.AreEqual(new[] { "close", "initialize", "open" }, sequence);
            Assert.AreEqual(throws ? 1 : 0, failures.Count);
            if (throws)
            {
                Assert.AreSame(failure, failures[0]);
            }
            Assert.IsFalse(viewModel.HasActiveLibraryProfile);
            Assert.IsTrue(dialog.IsEditCompletionEnabled);

            await dialog.ApplySettingsAsync();

            Assert.AreEqual(2, initializeCount);
            Assert.AreEqual(1, settingsSession.SaveCount);
            CollectionAssert.AreEqual(new[] { "close", "initialize", "open", "close", "initialize" }, sequence);
            Assert.IsTrue(dialog.IsEditCancellationEnabled);
        }
        finally
        {
            initializeReleased.TrySetResult();
            try
            {
                if (applyTask != null) { await applyTask; }
            }
            finally
            {
                dialog?.Dispose();
                Directory.Delete(root, recursive: true);
            }
        }
    }

    /// <summary>標準設定portから保存と実Close後に同じL/Pを実ScoreOnlyモデルへ渡し、受理Taskの生存と公開済み実snapshotを確認します。</summary>
    [TestMethod]
    public void ApplySettingsAsync_DefaultScoreOnlyPublishesChangedRealScoreAfterClosing()
    {
        string root = CreateTemporaryRoot();
        string music = Path.Combine(root, "music");
        string beatoraja = Path.Combine(root, "beatoraja");
        Directory.CreateDirectory(music);
        Directory.CreateDirectory(beatoraja);
        File.WriteAllText(Path.Combine(beatoraja, "beatoraja.jar"), string.Empty);
        File.WriteAllText(Path.Combine(beatoraja, BeatorajaConfigService.ConfigFileName), "{}");
        string hash = new('a', 64);
        foreach ((string player, int score) in new[] { ("playerA", 10), ("playerB", 40) })
        {
            string playerDirectory = Path.Combine(beatoraja, "player", player);
            Directory.CreateDirectory(playerDirectory);
            using var db = new SQLite.SQLiteConnection(Path.Combine(playerDirectory, "score.db"));
            db.Execute("CREATE TABLE score (sha256 TEXT, mode INTEGER, clear INTEGER, epg INTEGER, lpg INTEGER, egr INTEGER, lgr INTEGER, notes INTEGER, combo INTEGER, minbp INTEGER, playcount INTEGER, clearcount INTEGER)");
            db.Execute("INSERT INTO score VALUES (?, 0, 4, ?, 0, 0, 0, 100, 50, 5, 1, 1)", hash, score);
        }
        using var release = new ManualResetEventSlim();
        var published = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        try
        {
            TestUiDispatcherHost.Invoke(() =>
            {
                Settings values = CreateValidStandaloneSettings(music);
                values.UseBeatorajaScoreDb = true;
                values.BeatorajaRootPath = beatoraja;
                values.BeatorajaPlayerId = "playerA";
                values.BeatorajaScoreDbPath = Path.Combine(beatoraja, "player", "playerA", "score.db");
                var session = new CountingSettingsEditSession(values);
                var failures = new List<Exception>();
                var composition = new ApplicationComposition(settingsEditSession: session,
                    reportSettingsApplyFailure: failures.Add,
                    uiScheduler: new TestUiScheduler(() => TestUiDispatcherHost.Dispatcher),
                    applicationLifetime: TestApplicationContext.CreateLifetime(firstStartup: false),
                    cultureCatalog: TestApplicationContext.CreateCultureCatalog(),
                    applicationPathSnapshot: TestBmsFactory.AvailableEverythingBridge,
                    rootFileEnumerator: new FastRootFileEnumerator());
                var factory = new StartupLibraryConstructionTestSupport.RecordingDelegatingStartupLibraryFactory(composition, [])
                { InitialSearchTargets = [music] };
                var viewModel = new MainWindowViewModel(composition, factory);
                var presentation = new RecordingSettingsDialogPresentationPort();
                viewModel.SettingDialog.AttachPresentationPort(presentation);
                Task? apply = null;
                BMSLibrary? library = null;
                Action<ScoreSnapshotChange> observer = _ => { published.TrySetResult(); release.Wait(); };
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
                    library = factory.CreatedLibrary;
                    Assert.IsNotNull(library);
                    Assert.AreEqual(20, library!.ResolveChartScoreSnapshot(ChartFileKind.Bms, Path.Combine(music, "chart.bms"), string.Empty, hash).Score);
                    library.ScoreSnapshotChanged += observer;
                    viewModel.SettingDialog.BeatorajaPlayerId = "playerB";
                    apply = viewModel.SettingDialog.ApplySettingsAsync();
                    TestUiDispatcherHost.AwaitTaskOnDispatcher(Task.WhenAny(published.Task, apply), "score-only-real-publish-or-failure");
                    if (!published.Task.IsCompleted) { TestUiDispatcherHost.AwaitTaskOnDispatcher(apply, "score-only-real-early-terminal"); Assert.Fail(string.Join(Environment.NewLine, failures)); }
                    Assert.IsFalse(apply.IsCompleted);
                    Assert.IsTrue(composition.OperationAdmission.IsActive);
                    Assert.IsTrue(composition.PlaylistOperationAdmission.IsActive);
                    Assert.AreEqual(1, session.SaveCount);
                    CollectionAssert.Contains(presentation.Requests, "close");
                    Assert.AreEqual(80, library.ResolveChartScoreSnapshot(ChartFileKind.Bms, Path.Combine(music, "chart.bms"), string.Empty, hash).Score,
                        "公開済みsnapshotの実値を読み、on-demandで未実行を補いません。");
                    release.Set();
                    TestUiDispatcherHost.AwaitTaskOnDispatcher(apply, "score-only-real-terminal");
                    Assert.AreEqual(0, failures.Count, string.Join(Environment.NewLine, failures));
                    CollectionAssert.Contains(presentation.Requests, "close");
                    Assert.IsFalse(composition.OperationAdmission.IsActive);
                }
                finally
                {
                    release.Set();
                    if (library != null) { library.ScoreSnapshotChanged -= observer; }
                    try { if (apply != null) { TestUiDispatcherHost.AwaitTaskOnDispatcher(apply, "score-only-real-cleanup-task"); } }
                    finally
                    {
                        TestUiDispatcherHost.AwaitTaskOnDispatcher(viewModel.ShellShutdownWorkflow.RequestWindowCloseAsync(), "score-only-real-cleanup");
                        viewModel.SettingDialog.Dispose();
                    }
                }
            });
        }
        finally { release.Set(); Directory.Delete(root, recursive: true); }
    }

    /// <summary>保存済配置Aを未保存draftから保ち、保存完了後は古いeditorもBを使い、Pの必須通知を実Closeが待ちます。</summary>
    [DataTestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public void DefaultComposition_OutputPlacementChangesAfterPersistenceAndPlaylistNotificationDrainsOnClose(bool failAfterPublication)
    {
        string root = CreateTemporaryRoot();
        using var releaseNotification = new ManualResetEventSlim();
        var notification = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        try
        {
            Settings template = CreateValidCustomFolderSettings(root, []);
            string configPath = Path.Combine(root, "user.config");
            Settings values = PortableSettingsPersistenceTests.OpenSettings(configPath);
            foreach (System.Configuration.SettingsProperty property in template.Properties) { values[property.Name] = template[property.Name]; }
            values.EnableBeatorajaBmtOutput = false;
            values.EnablePlaylistUrlCompletion = false;
            values.ScanBmsFilesOnStartup = true;
            values.Save();
            string outputA = values.LR2CustomFolderOutputBaseDir;
            string outputB = Path.Combine(root, "OutputB");
            Directory.CreateDirectory(outputB);
            string music = Path.Combine(root, "Songs");
            string chartPath = Path.Combine(music, "actual.bms");
            File.WriteAllText(chartPath, "#PLAYER 1\n#TITLE Placement Chart\n#ARTIST Fixture\n#BPM 120\n#00111:01\n");
            var failures = new List<Exception>();
            TestUiDispatcherHost.Invoke(() =>
            {
                var session = new CountingSettingsEditSession(values) { PersistOwnedValues = true };
                var composition = new ApplicationComposition(settingsEditSession: session, reportSettingsApplyFailure: failures.Add,
                    uiScheduler: new TestUiScheduler(() => TestUiDispatcherHost.Dispatcher),
                    applicationLifetime: TestApplicationContext.CreateLifetime(firstStartup: false),
                    cultureCatalog: TestApplicationContext.CreateCultureCatalog(),
                    applicationPathSnapshot: TestBmsFactory.AvailableEverythingBridge, rootFileEnumerator: new FastRootFileEnumerator(),
                    chartFileScanner: CapturedChartFileScanner.FromFixture([chartPath],
                        new Dictionary<string, IEnumerable<string>> { [music] = [] }, [music]));
                var factory = new StartupLibraryConstructionTestSupport.RecordingDelegatingStartupLibraryFactory(composition, [])
                { InitialSearchTargets = [music] };
                var owner = new MainWindowViewModel(composition, factory);
                var presentation = new RecordingSettingsDialogPresentationPort();
                owner.SettingDialog.AttachPresentationPort(presentation);
                var originalFailure = new IOException("required placement notification failed after commit");
                bool injectFailure = failAfterPublication;
                owner.PlaylistWorkspace.PlaylistOperationNotificationPresentationRequested += (_, request) =>
                {
                    if (injectFailure && request.RouteName == "custom folder output base sync notification") { throw originalFailure; }
                    if (request.RouteName != "playlist drop custom folder output notification") { return; }
                    notification.TrySetResult(); releaseNotification.Wait();
                };
                Task? drop = null;
                Task? close = null;
                PlaylistPropertyDialogViewModel? editor = null;
                try
                {
                    Task<bool> initialize = owner.InitializeAsync();
                    TestUiDispatcherHost.AwaitTaskOnDispatcher(initialize, "placement-real-initialize");
                    Assert.AreEqual(true, initialize.GetAwaiter().GetResult());
                    if (owner.ProgressHub.IsStartupBackgroundInitializationActive)
                    {
                        // 次の手動操作のfixture準備だけで後続の実終端を待ち、初期化成功の条件には加えません。
                        var terminal = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
                        PropertyChangedEventHandler finished = (_, args) => { if (args.PropertyName == nameof(OperationProgressHubViewModel.IsStartupBackgroundInitializationActive) && !owner.ProgressHub.IsStartupBackgroundInitializationActive) { terminal.TrySetResult(); } };
                        owner.ProgressHub.PropertyChanged += finished;
                        try
                        {
                            if (owner.ProgressHub.IsStartupBackgroundInitializationActive)
                            { TestUiDispatcherHost.AwaitTaskOnDispatcher(terminal.Task, "placement-followup-terminal-before-manual-request"); }
                        }
                        finally { owner.ProgressHub.PropertyChanged -= finished; }
                    }
                    BMSLibrary library = factory.CreatedLibrary ?? throw new InvalidOperationException("Actual library was not attached.");
                    BMSPlaylist playlist = factory.CreatedPlaylist ?? throw new InvalidOperationException("Actual playlist was not attached.");
                    var table = new BMSTable
                    {
                        name = "Placement",
                        symbol = "P",
                        Output_dir = "Placement",
                        ignore_folder_output = LR2SongDBExtended.playlist.CustomFolderType.AllFolders
                            & ~LR2SongDBExtended.playlist.CustomFolderType.UserFolder
                            & ~LR2SongDBExtended.playlist.CustomFolderType.AllSongsFolder,
                        entries = [BmsPlaylistTestSupport.CreateEntry(new string('a', 32), "Folder A")],
                        Folder_order = ["Folder A"]
                    };
                    TestUiDispatcherHost.AwaitTaskOnDispatcher(playlist.ExternalSyncOwner.RegistrateExternalTableAsync(table, false, "placement-registration"), "placement-registration");
                    Task<PlaylistPropertyDialogViewModel> open = owner.PlaylistWorkspace.OpenPropertyDialogAsync(table);
                    TestUiDispatcherHost.AwaitTaskOnDispatcher(open, "placement-old-editor");
                    editor = open.GetAwaiter().GetResult();
                    Assert.IsNotNull(editor);
                    owner.SettingDialog.LR2CustomFolderOutputDir = outputB;
                    Assert.AreEqual(outputA, composition.CustomFolderOutputSettingsProvider().LR2CustomFolderOutputBaseDir);
                    Assert.AreEqual(outputA, composition.BmsLibraryOptionsProvider().LR2CustomFolderOutputBaseDir);
                    playlist.ReOutputCustomFolderAndCommitToDB(table);
                    string relative = Path.Combine(table.Output_dir, "0001.lr2folder");
                    Assert.IsTrue(File.Exists(Path.Combine(outputA, relative)));
                    Assert.IsFalse(File.Exists(Path.Combine(outputB, relative)));
                    Assert.IsTrue(library.Lr2Synchronization.TryEnterManagedOutputMutation([Path.Combine(outputB, "future")], true, out LibraryFileMutationLease disjoint));
                    disjoint?.Dispose();
                    using (LibraryFileMutationLease heldP = playlist.AcquirePlaylistMutationLease("placement-intersection-observation"))
                    {
                        Assert.IsFalse(library.Lr2Synchronization.TryEnterManagedOutputMutation([Path.Combine(outputA, "future")], true, out _));
                    }
                    var runtime = new BmsLr2SongDbSyncWorkflowRuntime(() => library, () => playlist, () => true);
                    Task<bool> whole = runtime.QueueAsync("unsaved-placement", true, true);
                    TestUiDispatcherHost.AwaitTaskOnDispatcher(whole, "placement-whole");
                    Assert.IsTrue(whole.GetAwaiter().GetResult());
                    Assert.AreEqual(library.Lr2SongDbSyncRequestedVersion, library.Lr2SongDbSyncCompletedVersion, "未保存出力先draftはcurrentnessを失効させません。");
                    TestUiDispatcherHost.AwaitTaskOnDispatcher(failAfterPublication ? owner.SettingDialog.ApplySettingsAsync() : owner.SettingDialog.SaveSettings(), "placement-real-save");
                    Assert.AreEqual(failAfterPublication ? 1 : 0, failures.Count, string.Join(Environment.NewLine, failures));
                    if (failAfterPublication)
                    {
                        Assert.AreSame(originalFailure, failures.Single());
                        Assert.IsTrue(owner.SettingDialog.HasPendingSettingChanges());
                        CollectionAssert.Contains(presentation.Requests, "close");
                        CollectionAssert.DoesNotContain(presentation.Requests, "open");
                        Assert.IsFalse(composition.OperationAdmission.IsActive);
                        Assert.IsFalse(library.Lr2Synchronization.PlaylistOperationAdmission.IsActive);
                        injectFailure = false;
                    }
                    Assert.AreEqual(1, session.SaveCount);
                    Assert.AreEqual(outputB, PortableSettingsPersistenceTests.OpenSettings(configPath).LR2CustomFolderOutputBaseDir);
                    Assert.AreEqual(outputB, composition.CustomFolderOutputSettingsProvider().LR2CustomFolderOutputBaseDir);
                    Assert.AreEqual(outputB, composition.BmsLibraryOptionsProvider().LR2CustomFolderOutputBaseDir);
                    Assert.IsTrue(File.Exists(Path.Combine(outputB, relative)));
                    if (failAfterPublication)
                    {
                        TestUiDispatcherHost.AwaitTaskOnDispatcher(owner.SettingDialog.SaveSettings(), "placement-explicit-retry");
                        Assert.IsFalse(owner.SettingDialog.HasPendingSettingChanges());
                        Assert.AreEqual(2, session.SaveCount);
                    }
                    Directory.CreateDirectory(Path.Combine(outputA, table.Output_dir));
                    string residual = Path.Combine(outputA, table.Output_dir, "external-residual.txt");
                    File.WriteAllText(residual, "retain external remainder");
                    editor.name = "Edited after placement";
                    editor.output_dir = "Placement";
                    Task<PlaylistPropertyDialogOperationResult> saveEditor = editor.SaveAndApplyAsync();
                    TestUiDispatcherHost.AwaitTaskOnDispatcher(saveEditor, "placement-old-editor-save");
                    Assert.AreEqual(PlaylistPropertyDialogOperationResult.Completed, saveEditor.GetAwaiter().GetResult());
                    Assert.AreEqual("retain external remainder", File.ReadAllText(residual));
                    using (LR2SongDBExtended db = new BmsLibraryDbGateway(values.LR2SongDBPath).OpenSongDbReadOnly())
                    {
                        Assert.AreEqual("Edited after placement", db.Table<LR2SongDB.folder>().Single(row => row.path == Path.Combine(outputB, relative)).category);
                    }
                    ChartFile chart = library.BmsCharts.Single();
                    drop = Task.Run(() => owner.PlaylistWorkspace.AddRowsToFolderAsync([LibraryChartRow.FromChartFile(chart)], table, PlaylistFolderNode.CreateFolder("Folder A")));
                    TestUiDispatcherHost.AwaitTaskOnDispatcher(Task.WhenAny(notification.Task, drop), "playlist-close-notification-or-failure");
                    if (!notification.Task.IsCompleted) { TestUiDispatcherHost.AwaitTaskOnDispatcher(drop, "playlist-close-early-terminal"); Assert.Fail("実必須通知へ到達しませんでした。"); }
                    Assert.IsFalse(drop.IsCompleted);
                    Assert.IsTrue(library.Lr2Synchronization.PlaylistOperationAdmission.IsActive);
                    Assert.IsFalse(library.Lr2Synchronization.PlaylistOperationAdmission.TryEnter(out _));
                    close = owner.ShellShutdownWorkflow.RequestWindowCloseAsync();
                    Assert.IsFalse(close.IsCompleted, "CloseはPの通知・cleanup実終端を待ちます。");
                    releaseNotification.Set();
                    TestUiDispatcherHost.AwaitTaskOnDispatcher(drop, "playlist-close-actual-work");
                    TestUiDispatcherHost.AwaitTaskOnDispatcher(close, "playlist-close-actual-terminal");
                    Assert.IsFalse(library.Lr2Synchronization.PlaylistOperationAdmission.IsActive);
                    Assert.IsFalse(composition.OperationAdmission.IsActive);
                }
                finally
                {
                    releaseNotification.Set();
                    try { if (drop != null) { TestUiDispatcherHost.AwaitTaskOnDispatcher(drop, "placement-cleanup-drop"); } }
                    finally
                    {
                        TestUiDispatcherHost.AwaitTaskOnDispatcher(close ?? owner.ShellShutdownWorkflow.RequestWindowCloseAsync(), "placement-cleanup-close");
                        editor?.Dispose(); owner.SettingDialog.Dispose();
                    }
                }
            });
        }
        finally { releaseNotification.Set(); Directory.Delete(root, recursive: true); }
    }

    /// <summary>旧IR通信中の実All再構築を受理し、親解放後の実UIを待ち、新DBの実値・旧組停止・旧IR結果の非保存を確認します。</summary>
    [DataTestMethod]
    [DataRow("Succeeded")]
    [DataRow("Failed")]
    [DataRow("Shutdown")]
    public void ApplySettingsAsync_DefaultAllReconstructionBorrowsLivePlaylistAdmissionUntilActualInitializationEnds(string completion)
    {
        string root = CreateTemporaryRoot();
        using var release = new ManualResetEventSlim();
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var closeEntered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var closeRelease = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var originalFailure = new IOException("actual All scan failed after saved close");
        try
        {
            Settings values = CreateValidCustomFolderSettings(root, []);
            values.ScanBmsFilesOnStartup = true;
            values.StartupSelectInstallPending = false;
            values.EnableDownloadLr2IrScoreAndDetectUnsent = true;
            values.UpdateLr2IrRankingCacheOnStartup = false;
            var configDocument = XDocument.Load(values.LR2ConfigXmlPath);
            configDocument.Root?.Add(new XElement("player", new XElement("id", "ir-all")));
            configDocument.Save(values.LR2ConfigXmlPath);
            string scoreDb = Lr2ScoreDbPathResolver.BuildPlayerScoreDbPath(root, "ir-all");
            Directory.CreateDirectory(Path.GetDirectoryName(scoreDb)!);
            const string irHash = "abcdefabcdefabcdefabcdefabcdefab";
            using (var db = new LR2ScoreDBExtended(scoreDb))
            {
                db.CreateTable<LR2ScoreDB.player>();
                db.CreateTable<LR2ScoreDB.score>();
                db.Insert(new LR2ScoreDB.player { id = "ir-all", irid = 123 });
                db.Insert(new LR2ScoreDB.score { hash = irHash, perfect = 321, great = 45 });
            }
            var originalGateway = new BmsLibraryDbGateway(values.LR2SongDBPath, scoreDb);
            originalGateway.ReplaceIrScoreTable([new LR2IRScore { hash = irHash, pg = 123, gr = 67 }]);
            originalGateway.UpsertIrScoreRefreshMetadata(123, "retained-all-digest");
            var irClient = new BmsLibraryIrStartupTests.ControlledIrClient(false, false, ignoreCancellation: true);
            string music = Path.Combine(root, "Songs");
            string chartPath = Path.Combine(music, "actual.bms");
            File.WriteAllText(chartPath, "#PLAYER 1\n#TITLE Library A\n#BPM 120\n#00111:01\n");
            string databaseB = Path.Combine(root, "Second", "song.db");
            Directory.CreateDirectory(Path.GetDirectoryName(databaseB)!);
            File.WriteAllBytes(databaseB, []);
            string outputB = Path.Combine(root, "OutputB");
            Directory.CreateDirectory(outputB);
            var scanner = CapturedChartFileScanner.FromFixture([chartPath],
                new Dictionary<string, IEnumerable<string>> { [music] = [] }, [music]);
            bool hold = false;
            scanner.ScanObserved = () =>
            {
                if (hold)
                {
                    entered.TrySetResult(); release.Wait();
                    if (completion == "Failed") { throw originalFailure; }
                }
            };
            TestUiDispatcherHost.Invoke(() =>
            {
                var session = new CountingSettingsEditSession(values);
                var failures = new List<Exception>();
                var dialogs = new RecordingRootDialogService();
                var completionUi = new MainWindowViewModelStartupProgressTests.CompletionUiScheduler(new TestUiScheduler(() => TestUiDispatcherHost.Dispatcher));
                var composition = new ApplicationComposition(settingsEditSession: session,
                    reportSettingsApplyFailure: failures.Add,
                    uiScheduler: completionUi,
                    applicationLifetime: TestApplicationContext.CreateLifetime(firstStartup: false),
                    cultureCatalog: TestApplicationContext.CreateCultureCatalog(),
                    applicationPathSnapshot: TestBmsFactory.AvailableEverythingBridge,
                    rootFileEnumerator: new FastRootFileEnumerator(), chartFileScanner: scanner,
                    settingsDialogService: dialogs, fileDbMutationDialogService: dialogs);
                var factory = new StartupLibraryConstructionTestSupport.RecordingDelegatingStartupLibraryFactory(composition, [])
                {
                    InitialSearchTargets = [music],
                    LibraryCreator = profile => new BMSLibrary(profile.SongDbPath, profile.Lr2ConfigProvider,
                        profile.Lr2ScoreDbPath, profile.StartupRequiredFileScanReason,
                        composition.BmsLibraryOptionsProvider, completionUi, composition.ApplicationPathSnapshot,
                        chartFileScanner: scanner, rootFileEnumerator: new FastRootFileEnumerator(), irClient: irClient,
                        operationAdmission: composition.OperationAdmission, playlistOperationAdmission: composition.PlaylistOperationAdmission)
                };
                var owner = new MainWindowViewModel(composition, factory);
                var presentation = new RecordingSettingsDialogPresentationPort
                {
                    WaitForClose = async () => { closeEntered.TrySetResult(); await closeRelease.Task; }
                };
                owner.SettingDialog.AttachPresentationPort(presentation);
                owner.PlaylistWorkspace.PlaylistOperationNotificationPresentationRequested += (_, _) => { };
                completionUi.IsCompletionBoundary = () => !composition.OperationAdmission.IsActive && !composition.PlaylistOperationAdmission.IsActive
                    && owner.ProgressHub.StartupProgress.IsStartupUiInteractionBlocked && !owner.IsInitializationCompleted;
                Task? apply = null;
                Task? shutdown = null;
                try
                {
                    Task<bool> initialize = owner.InitializeAsync();
                    TestUiDispatcherHost.AwaitTaskOnDispatcher(initialize, "all-reconstruction-first-initialize");
                    Assert.IsTrue(initialize.GetAwaiter().GetResult());
                    TestUiDispatcherHost.AwaitTaskOnDispatcher(irClient.Started.Task, "all-reconstruction-old-ir-started");
                    Assert.IsFalse(owner.IsLibraryOperationInProgress, "完了余韻・IR通信を新設定のBusy条件にしません。");
                    BMSLibrary original = factory.CreatedLibrary ?? throw new InvalidOperationException("Initial library was not attached.");
                    BMSPlaylist originalPlaylist = factory.CreatedPlaylist ?? throw new InvalidOperationException("Initial playlist was not attached.");
                    var oldRankingTerminal = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
                    original.PropertyChanged += (_, args) =>
                    {
                        if (args.PropertyName == nameof(BMSLibrary.RankingRefreshRunning) && !original.RankingRefreshRunning) { oldRankingTerminal.TrySetResult(); }
                    };
                    Assert.AreEqual("Library A", original.BmsCharts.Single().RawTitle);
                    Assert.AreEqual("Library A", (owner.MainChartList.Rows[0] as LibraryChartRow ?? throw new InvalidOperationException("Initial library row was not applied.")).Title, "最初の実UIで現行Libraryの行を反映します。");
                    File.WriteAllText(chartPath, "#PLAYER 1\n#TITLE Library B\n#BPM 120\n#00111:01\n");
                    owner.SettingDialog.LR2SongDBPath = databaseB;
                    owner.SettingDialog.LR2CustomFolderOutputDir = outputB;
                    Assert.AreEqual(SettingsDialogViewModel.RestartMode.All, owner.SettingDialog.IsNeedRestartForSaved());
                    hold = true;
                    completionUi.Arm();
                    apply = owner.SettingDialog.ApplySettingsAsync();
                    TestUiDispatcherHost.AwaitTaskOnDispatcher(TestUiDispatcherHost.AwaitNotificationAsync(closeEntered.Task, apply, "all-reconstruction-close-arrival"), "all-reconstruction-close-arrival");
                    Assert.AreEqual(1, session.SaveCount);
                    Assert.AreEqual(databaseB, values.LR2SongDBPath);
                    Assert.AreEqual(outputB, composition.CustomFolderOutputSettingsProvider().LR2CustomFolderOutputBaseDir);
                    Assert.AreSame(original, factory.CreatedLibrary);
                    Assert.IsFalse(original.IsShutdownRequested);
                    Assert.IsFalse(entered.Task.IsCompleted, "実Close/modalcleanup前に後続走査を始めません。");
                    Assert.IsFalse(apply.IsCompleted);
                    Assert.IsTrue(composition.OperationAdmission.IsActive);
                    Assert.IsTrue(composition.PlaylistOperationAdmission.IsActive);
                    Task competing = owner.SettingDialog.ApplySettingsAsync();
                    TestUiDispatcherHost.AwaitTaskOnDispatcher(competing, "all-reconstruction-close-busy");
                    Assert.AreEqual(1, session.SaveCount, "Close待機中の重複OKは保存・再初期化を増やしません。");
                    closeRelease.TrySetResult();

                    TestUiDispatcherHost.AwaitTaskOnDispatcher(Task.WhenAny(entered.Task, apply), "all-reconstruction-input-or-failure");
                    if (!entered.Task.IsCompleted) { TestUiDispatcherHost.AwaitTaskOnDispatcher(apply, "all-reconstruction-early-terminal"); Assert.Fail(string.Join(Environment.NewLine, failures)); }
                    BMSLibrary current = factory.CreatedLibrary ?? throw new InvalidOperationException("New library was not attached.");
                    BMSPlaylist currentPlaylist = factory.CreatedPlaylist ?? throw new InvalidOperationException("New playlist was not attached.");
                    var currentRankingTerminal = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
                    current.PropertyChanged += (_, args) =>
                    {
                        if (args.PropertyName == nameof(BMSLibrary.RankingRefreshRunning) && !current.RankingRefreshRunning) { currentRankingTerminal.TrySetResult(); }
                    };
                    Assert.AreNotSame(original, current);
                    Assert.AreNotSame(originalPlaylist, currentPlaylist);
                    Assert.IsTrue(original.IsShutdownRequested);
                    Assert.IsTrue(originalPlaylist.IsShutdownRequested);
                    Assert.IsFalse(current.IsShutdownRequested);
                    Assert.IsFalse(currentPlaylist.IsShutdownRequested);
                    Assert.IsFalse(composition.OperationAdmission.IsAdmissionClosed);
                    Assert.IsFalse(composition.PlaylistOperationAdmission.IsAdmissionClosed);
                    Assert.AreSame(current, currentPlaylist.LibraryBindings.SourceLibrary);
                    Assert.AreSame(original.Lr2Synchronization.PlaylistOperationAdmission, current.Lr2Synchronization.PlaylistOperationAdmission);
                    Assert.IsFalse(apply.IsCompleted);
                    Assert.IsTrue(composition.OperationAdmission.IsActive);
                    Assert.IsTrue(composition.PlaylistOperationAdmission.IsActive);
                    Assert.AreEqual(1, session.SaveCount);
                    CollectionAssert.Contains(presentation.Requests, "close");
                    // この実走査を所有する親の終端を捕捉し、解放後に別の任意workerがLを取得しても同じ親だけを検査します。
                    Task parentLibraryTerminal = composition.OperationAdmission.WaitForIdleAsync();
                    Task parentPlaylistTerminal = composition.PlaylistOperationAdmission.WaitForIdleAsync();
                    using (LR2SongDBExtended db = new BmsLibraryDbGateway(databaseB).OpenSongDbReadOnly())
                    {
                        int headersBefore = db.Table<LR2SongDBExtended.playlist>().Count();
                        Assert.ThrowsException<InvalidOperationException>(() => currentPlaylist.RemoveBMSTable(new BMSTable { name = "competing" }));
                        Assert.AreEqual(headersBefore, db.Table<LR2SongDBExtended.playlist>().Count());
                    }
                    Assert.IsFalse(Directory.EnumerateFiles(outputB, "*.lr2folder", SearchOption.AllDirectories).Any());

                    if (completion != "Succeeded")
                    {
                        if (completion == "Shutdown")
                        {
                            shutdown = owner.ShellShutdownWorkflow.RequestWindowCloseAsync();
                            Assert.IsFalse(shutdown.IsCompleted, "正常Close後も受理済み実走査を終了cleanupまで保持します。");
                        }
                        release.Set();
                        irClient.Release.TrySetResult();
                        TestUiDispatcherHost.AwaitTaskOnDispatcher(apply, "all-reconstruction-postsave-failure-or-shutdown");
                        Assert.IsFalse(owner.IsInitializationCompleted);
                        Assert.IsFalse(completionUi.Entered.Task.IsCompleted);
                        CollectionAssert.DoesNotContain(presentation.Requests, "open");
                        Assert.AreEqual(1, presentation.Requests.Count(request => request == "close"));
                        Assert.AreEqual(databaseB, values.LR2SongDBPath);
                        Assert.AreEqual(outputB, composition.CustomFolderOutputSettingsProvider().LR2CustomFolderOutputBaseDir);
                        if (completion == "Failed") { Assert.AreEqual(1, failures.Count); Assert.AreSame(originalFailure, failures[0]); }
                        else { Assert.AreEqual(0, failures.Count); TestUiDispatcherHost.AwaitTaskOnDispatcher(shutdown!, "all-reconstruction-shutdown-terminal"); }
                        Assert.IsTrue(parentLibraryTerminal.IsCompletedSuccessfully);
                        Assert.IsTrue(parentPlaylistTerminal.IsCompletedSuccessfully);
                        return;
                    }
                    release.Set();
                    TestUiDispatcherHost.AwaitTaskOnDispatcher(TestUiDispatcherHost.AwaitNotificationAsync(completionUi.Entered.Task, apply, "all-reconstruction-required-ui"), "all-reconstruction-required-ui");
                    Assert.IsTrue(parentLibraryTerminal.IsCompletedSuccessfully);
                    Assert.IsTrue(parentPlaylistTerminal.IsCompletedSuccessfully);
                    Assert.IsFalse(owner.IsInitializationCompleted);
                    Assert.IsTrue(owner.ProgressHub.StartupProgress.IsStartupUiInteractionBlocked);
                    completionUi.Release.TrySetResult();
                    TestUiDispatcherHost.AwaitTaskOnDispatcher(apply, "all-reconstruction-actual-terminal");
                    Assert.AreEqual(0, failures.Count, string.Join(Environment.NewLine, failures));
                    Assert.AreEqual("Library B", current.BmsCharts.Single().RawTitle);
                    Assert.AreEqual("Library B", (owner.MainChartList.Rows[0] as LibraryChartRow ?? throw new InvalidOperationException("Reconstructed library row was not applied.")).Title, "Allの必須UI実Taskが新Libraryの表示を適用してから成功を公開します。");
                    using (LR2SongDBExtended db = new BmsLibraryDbGateway(databaseB).OpenSongDbReadOnly())
                    {
                        Assert.AreEqual("Library B", db.Table<LR2SongDB.song>().Single(row => row.path == chartPath).title);
                    }
                    CollectionAssert.Contains(presentation.Requests, "close");
                    Assert.AreEqual(0, dialogs.MessageCount, "同じ生存権限で初期化し、自己Busyを正常成功へ隠しません。");
                    Assert.IsFalse(irClient.Completed.Task.IsCompleted, "旧通信は親内の全idle待ちへ加えません。");
                    irClient.Release.TrySetResult();
                    TestUiDispatcherHost.AwaitTaskOnDispatcher(Task.WhenAll(oldRankingTerminal.Task, currentRankingTerminal.Task), "all-reconstruction-ir-terminals");
                    Assert.AreEqual(123, originalGateway.LoadIrScoreRows().Single().pg);
                    Assert.AreEqual("retained-all-digest", originalGateway.LoadIrScoreRefreshMetadata(123).score_digest_sha256);
                    Assert.AreEqual(500, new BmsLibraryDbGateway(databaseB, scoreDb).LoadIrScoreRows().Single().pg);
                    var next = new BMSTable { name = "fresh after all", Output_dir = "Fresh", entries = [], Folder_order = [] };
                    Task registration = currentPlaylist.ExternalSyncOwner.RegistrateExternalTableAsync(next, false, "fresh-after-all");
                    TestUiDispatcherHost.AwaitTaskOnDispatcher(registration, "all-reconstruction-next-explicit");
                    using (LR2SongDBExtended db = new BmsLibraryDbGateway(databaseB).OpenSongDbReadOnly())
                    {
                        Assert.IsTrue(db.Table<LR2SongDBExtended.playlist>().Any(row => row.name == next.name));
                    }
                    Assert.IsFalse(composition.PlaylistOperationAdmission.IsActive);
                }
                finally
                {
                    closeRelease.TrySetResult();
                    release.Set();
                    irClient.Release.TrySetResult();
                    completionUi.Release.TrySetResult();
                    try { if (apply != null) { TestUiDispatcherHost.AwaitTaskOnDispatcher(apply, "all-reconstruction-cleanup-apply"); } }
                    finally
                    {
                        TestUiDispatcherHost.AwaitTaskOnDispatcher(shutdown ?? owner.ShellShutdownWorkflow.RequestWindowCloseAsync(), "all-reconstruction-cleanup-close");
                        owner.SettingDialog.Dispose();
                    }
                }

            });
        }
        finally { closeRelease.TrySetResult(); release.Set(); Directory.Delete(root, recursive: true); }
    }

    /// <summary>標準設定の受理から実LR2初期化まで同じ生存権限を渡し、自己Busyなしで確定し、背景処理の実終端も回収します。</summary>
    [DataTestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public void ApplySettingsAsync_InitialLr2SettingsCompletesRealInitializationWithoutReacquiringAdmission(bool uiFails)
    {
        string root = CreateTemporaryRoot();
        try
        {
            Settings values = CreateValidCustomFolderSettings(root, []);
            var session = new CountingSettingsEditSession(values);
            var failures = new List<Exception>();
            var dialogs = new RecordingRootDialogService();
            TestUiDispatcherHost.Invoke(() =>
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
                viewModel.SettingDialog.AttachPresentationPort(new RecordingSettingsDialogPresentationPort());
                completionUi.IsCompletionBoundary = () => !composition.OperationAdmission.IsActive && !composition.PlaylistOperationAdmission.IsActive
                    && viewModel.ProgressHub.StartupProgress.IsStartupUiInteractionBlocked && !viewModel.IsInitializationCompleted;
                Task? apply = null;
                try
                {
                    viewModel.SettingDialog.ShowRecommUpdatedMsg = !viewModel.SettingDialog.ShowRecommUpdatedMsg;
                    completionUi.Arm();
                    apply = viewModel.SettingDialog.ApplySettingsAsync();
                    TestUiDispatcherHost.AwaitTaskOnDispatcher(TestUiDispatcherHost.AwaitNotificationAsync(completionUi.Entered.Task, apply, "initial-lr2-required-ui"), "initial-lr2-required-ui");
                    Assert.IsFalse(composition.OperationAdmission.IsActive);
                    Assert.IsFalse(composition.PlaylistOperationAdmission.IsActive);
                    Assert.IsFalse(viewModel.IsInitializationCompleted);
                    Assert.IsTrue(viewModel.ProgressHub.StartupProgress.IsStartupUiInteractionBlocked);
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
                        return;
                    }
                    Assert.AreEqual(0, failures.Count, string.Join(Environment.NewLine, failures));
                    Assert.AreEqual(1, session.SaveCount);
                    Assert.IsTrue(viewModel.HasActiveLibraryProfile);
                    Assert.IsTrue(viewModel.IsInitializationCompleted);
                    Assert.IsFalse(viewModel.SettingDialog.HasPendingSettingChanges());
                    Assert.AreEqual(0, dialogs.MessageCount, "受理済み初期化を自己Busyとして拒否しません。");
                    TestUiDispatcherHost.AwaitTaskOnDispatcher(composition.OperationAdmission.WaitForIdleAsync(), "initial-lr2-settings-library-terminal");
                    TestUiDispatcherHost.AwaitTaskOnDispatcher(composition.PlaylistOperationAdmission.WaitForIdleAsync(), "initial-lr2-settings-playlist-terminal");
                    Assert.IsFalse(composition.OperationAdmission.IsActive);
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
                    viewModel.SettingDialog.Dispose();
                }
                Assert.IsFalse(composition.OperationAdmission.IsActive, "全背景Taskとcleanupの実終端後に受付を解放します。");
            });
        }
        finally { Directory.Delete(root, recursive: true); }
    }

    /// <summary>保存検証後に root が消えた初期化は cleanup 後の警告・再表示へ戻り、初回通知の有無を分け、次の明示適用で保存値を重ねて保存しないことを確認します。</summary>
    [DataTestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public void ApplySettingsAsync_InitialDirectoryFailureReopensOnceAfterCleanupAndCanRetry(bool firstStartup)
    {
        string root = CreateTemporaryRoot();
        string musicRoot = Path.Combine(root, "music");
        string unavailableRoot = Path.Combine(root, "music-offline");
        Directory.CreateDirectory(musicRoot);
        try
        {
            TestUiDispatcherHost.Invoke(() =>
            {
                var session = new CountingSettingsEditSession(CreateValidStandaloneSettings(musicRoot));
                var dialogs = new RecordingRootDialogService();
                var failures = new List<Exception>();
                var sequence = new List<string>();
                session.SaveObserved = () => sequence.Add("save");
                dialogs.MessageObserved = () => sequence.Add(
                    dialogs.LastMessageText == Resources.Msg_initsetting_completed ? "completion-message" : "warning");
                MainWindowViewModel viewModel = new ApplicationComposition(
                    settingsEditSession: session,
                    reportSettingsApplyFailure: failures.Add,
                    uiScheduler: new TestUiScheduler(() => TestUiDispatcherHost.Dispatcher),
                    applicationLifetime: TestApplicationContext.CreateLifetime(firstStartup),
                    cultureCatalog: TestApplicationContext.CreateCultureCatalog(),
                    applicationPathSnapshot: ApplicationPathSnapshot.FromExecutablePath(
                        Path.Combine(root, "application", "BeMusicSeeker.exe")),
                    settingsDialogService: dialogs,
                    fileDbMutationDialogService: dialogs)
                    .CreateMainWindowViewModelForTest();
                ApplicationComposition composition = MainWindowViewModelTestFactory.GetComposition(viewModel);
                SettingsDialogViewModel settings = viewModel.SettingDialog;
                var reopenObservations = new List<(bool EditEnabled, bool CancellationEnabled, bool OperationInProgress, bool UiBlocked, bool PendingChanges, bool LibraryActive, bool PlaylistActive)>();
                try
                {
                    settings.AttachPresentationPort(new RecordingSettingsDialogPresentationPort(request =>
                    {
                        sequence.Add(request);
                        if (request == "close")
                        {
                            // 保存の検証後、初期化前に外部ドライブが利用できなくなる場合を再現する。
                            Directory.Move(musicRoot, unavailableRoot);
                        }
                        else if (request == "open")
                        {
                            reopenObservations.Add((settings.IsEditCompletionEnabled, settings.IsEditCancellationEnabled,
                                viewModel.IsLibraryOperationInProgress, viewModel.ProgressHub.StartupProgress.IsStartupUiInteractionBlocked,
                                settings.HasPendingSettingChanges(), composition.OperationAdmission.IsActive, composition.PlaylistOperationAdmission.IsActive));
                            // 再編集に戻った利用者が接続を復旧し、次の保存で再試行する。
                            Directory.Move(unavailableRoot, musicRoot);
                        }
                    }));
                    settings.ShowRecommUpdatedMsg = !settings.ShowRecommUpdatedMsg;
                    bool savedValue = settings.ShowRecommUpdatedMsg;
                    for (int attempt = 0; attempt < 2; attempt++)
                    {
                        sequence.Clear();
                        reopenObservations.Clear();
                        Assert.IsTrue(settings.CheckValidation(out string validationError), validationError);
                        Task apply = settings.ApplySettingsAsync();
                        TestUiDispatcherHost.AwaitTaskOnDispatcher(apply, "initial-settings-directory-failure");

                        var expected = new List<string>();
                        if (attempt == 0)
                        {
                            expected.Add("save");
                        }
                        expected.Add("close");
                        if (firstStartup)
                        {
                            expected.Add("completion-message");
                        }
                        expected.Add("warning");
                        expected.Add("open");
                        CollectionAssert.AreEqual(expected, sequence, $"attempt={attempt}; actual={string.Join(",", sequence)}");
                        Assert.AreEqual(0, failures.Count, string.Join(Environment.NewLine, failures));
                        Assert.AreEqual(1, reopenObservations.Count);
                        (bool EditEnabled, bool CancellationEnabled, bool OperationInProgress, bool UiBlocked, bool PendingChanges, bool LibraryActive, bool PlaylistActive) observation = reopenObservations.Single();
                        Assert.IsTrue(observation.EditEnabled);
                        Assert.IsTrue(observation.CancellationEnabled);
                        Assert.IsFalse(observation.OperationInProgress);
                        Assert.IsFalse(observation.UiBlocked);
                        Assert.IsFalse(observation.PendingChanges);
                        Assert.IsFalse(observation.LibraryActive);
                        Assert.IsFalse(observation.PlaylistActive);
                        Assert.AreEqual(1, session.SaveCount);
                        Assert.AreEqual(savedValue, settings.ShowRecommUpdatedMsg);
                        Assert.IsFalse(viewModel.HasActiveLibraryProfile);
                        Assert.IsFalse(viewModel.IsInitializationCompleted);
                        Assert.IsFalse(composition.OperationAdmission.IsActive);
                        Assert.IsFalse(composition.PlaylistOperationAdmission.IsActive);
                        Assert.IsFalse(settings.IsEditCompletionInProgress);
                        StringAssert.Contains(dialogs.LastMessageText, musicRoot);
                        StringAssert.Contains(dialogs.LastMessageText, Resources.LibraryDirectoryPreflightBmsRootRole);
                    }
                }
                finally
                {
                    settings.Dispose();
                }
            });
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    /// <summary>初回保存の元例外を一回報告し、Close・通知・初期化を始めず、未保存の品質・thread・通常設定 draft と元保存値を保つことを確認します。</summary>
    [TestMethod]
    public async Task ApplySettingsAsync_InitialSaveFailureKeepsDraftWithoutClosingOrInitializing()
    {
        string root = CreateTemporaryRoot();
        try
        {
            var failure = new IOException("settings save failure");
            Settings values = CreateValidStandaloneSettings(root);
            values.PlayerResamplingQuality = 4;
            values.PlayerMixerThreadCount = 2;
            var settingsSession = new CountingSettingsEditSession(values)
            {
                SaveFailure = failure
            };
            var failures = new List<Exception>();
            var dialogs = new RecordingRootDialogService();
            int initializeCount = 0;
            MainWindowViewModel viewModel = CreateViewModel(
                settingsSession,
                firstStartup: true,
                initializeOwner: _ =>
                {
                    initializeCount++;
                    return Task.FromResult(true);
                },
                reportSettingsApplyFailure: failures.Add,
                dialogs: dialogs);
            using SettingsDialogViewModel dialog = viewModel.SettingDialog;
            var presentation = new RecordingSettingsDialogPresentationPort();
            dialog.AttachPresentationPort(presentation);
            dialog.ShowRecommUpdatedMsg = !dialog.ShowRecommUpdatedMsg;
            bool editedValue = dialog.ShowRecommUpdatedMsg;
            dialog.PlayerResamplingQuality = 3;
            dialog.PlayerMixerThreadCount = 4;

            await dialog.ApplySettingsAsync();

            Assert.AreEqual(1, failures.Count);
            Assert.AreSame(failure, failures[0]);
            Assert.AreEqual(0, presentation.Requests.Count);
            Assert.AreEqual(0, initializeCount);
            Assert.AreEqual(0, dialogs.MessageCount);
            Assert.AreEqual(editedValue, dialog.ShowRecommUpdatedMsg);
            Assert.AreEqual(3, dialog.PlayerResamplingQuality);
            Assert.AreEqual(4, dialog.PlayerMixerThreadCount);
            Assert.AreEqual(4, values.PlayerResamplingQuality);
            Assert.AreEqual(2, values.PlayerMixerThreadCount);
            Assert.IsTrue(dialog.HasPendingSettingChanges());
            Assert.IsTrue(dialog.IsEditCancellationEnabled);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    /// <summary>品質・thread draft の取消は指定した保存値へ戻し、保存せず dirty 状態を解除することを確認します。</summary>
    [TestMethod]
    public void CancelSettings_RestoresResamplingQualityAndMixerThreadDraftsWithoutSaving()
    {
        string root = CreateTemporaryRoot();
        SettingsDialogViewModel? dialog = null;
        try
        {
            Settings values = CreateValidStandaloneSettings(root);
            values.PlayerResamplingQuality = 4;
            values.PlayerMixerThreadCount = 3;
            var settingsSession = new CountingSettingsEditSession(values);
            dialog = CreateViewModel(settingsSession, firstStartup: false).SettingDialog;

            dialog.PlayerResamplingQuality = 2;
            dialog.PlayerMixerThreadCount = 4;
            Assert.IsTrue(dialog.HasPendingSettingChanges());

            dialog.CancelCommand.Execute();

            Assert.AreEqual(4, dialog.PlayerResamplingQuality);
            Assert.AreEqual(4, values.PlayerResamplingQuality);
            Assert.AreEqual(3, dialog.PlayerMixerThreadCount);
            Assert.AreEqual(3, values.PlayerMixerThreadCount);
            Assert.AreEqual(0, settingsSession.SaveCount);
            Assert.IsFalse(dialog.HasPendingSettingChanges());
        }
        finally
        {
            dialog?.Dispose();
            Directory.Delete(root, recursive: true);
        }
    }

    /// <summary>品質の下限外／上限外入力を専用の検証メッセージで拒否し、保存を行わないことを確認します。</summary>
    [DataTestMethod]
    [DataRow(1)]
    [DataRow(7)]
    public void CheckValidation_RejectsUnsupportedResamplingQuality(int quality)
    {
        string root = CreateTemporaryRoot();
        SettingsDialogViewModel? dialog = null;
        try
        {
            Settings values = CreateValidStandaloneSettings(root);
            values.PlayerResamplingQuality = quality;
            var settingsSession = new CountingSettingsEditSession(values);
            dialog = CreateViewModel(settingsSession, firstStartup: false).SettingDialog;

            Assert.IsFalse(dialog.CheckValidation(out string error));
            StringAssert.Contains(error, Resources.Error_InvalidAudioResamplingQuality);
            Assert.AreEqual(0, settingsSession.SaveCount);
        }
        finally
        {
            dialog?.Dispose();
            Directory.Delete(root, recursive: true);
        }
    }

    /// <summary>thread 数の下限外／上限外入力を専用の検証メッセージで拒否し、保存を行わないことを確認します。</summary>
    [DataTestMethod]
    [DataRow(0)]
    [DataRow(5)]
    public void CheckValidation_RejectsUnsupportedPlayerMixerThreadCount(int threadCount)
    {
        string root = CreateTemporaryRoot();
        SettingsDialogViewModel? dialog = null;
        try
        {
            Settings values = CreateValidStandaloneSettings(root);
            values.PlayerMixerThreadCount = threadCount;
            var settingsSession = new CountingSettingsEditSession(values);
            dialog = CreateViewModel(settingsSession, firstStartup: false).SettingDialog;

            Assert.IsFalse(dialog.CheckValidation(out string error));
            StringAssert.Contains(error, Resources.Error_InvalidAudioMixerThreadCount);
            Assert.AreEqual(0, settingsSession.SaveCount);
        }
        finally
        {
            dialog?.Dispose();
            Directory.Delete(root, recursive: true);
        }
    }

    /// <summary>有効な個別 DB／config がある LR2 root 未設定は保存を妨げず、単独モードでは警告しないことと、不正候補の拒否・有効 root 再選択の警告解除を確認します。</summary>
    [TestMethod]
    public void Lr2RootPathEmpty_IsWarningOnlyAndDoesNotBlockSaving()
    {
        string root = CreateTemporaryRoot();
        SettingsDialogViewModel? dialog = null;
        try
        {
            Settings values = CreateValidCustomFolderSettings(root, []);
            values.LR2RootPath = string.Empty;
            var session = new CountingSettingsEditSession(values);
            dialog = CreateViewModel(session, firstStartup: false).SettingDialog;

            Assert.AreEqual("Warning", dialog.Lr2RootPathValidationStatus);
            Assert.AreEqual(Resources.Warning_LR2RootPathNotSet, dialog.Lr2RootPathValidationMessage);
            Assert.IsFalse(dialog.HasLr2PathSelectionError);
            Assert.IsTrue(dialog.CheckValidation(out string validationError), validationError);
            Assert.IsTrue(dialog.CheckValidationBeforeSave(out string saveError), saveError);

            dialog.OperationModeLR2DB = false;
            Assert.AreEqual(string.Empty, dialog.Lr2RootPathValidationStatus);
            Assert.AreEqual(string.Empty, dialog.Lr2RootPathValidationMessage);

            dialog.OperationModeLR2DB = true;
            dialog.LR2RootPath = Path.Combine(root, "missing-lr2-root");
            Assert.AreEqual("Warning", dialog.Lr2RootPathValidationStatus);
            Assert.AreEqual(Resources.Warning_LR2RootPathNotSet, dialog.Lr2RootPathValidationMessage);
            Assert.IsTrue(dialog.HasLr2PathSelectionError);

            dialog.LR2RootPath = root;

            Assert.AreEqual(string.Empty, dialog.Lr2RootPathValidationStatus);
            Assert.AreEqual(string.Empty, dialog.Lr2RootPathValidationMessage);
            Assert.IsFalse(dialog.HasLr2PathSelectionError);
        }
        finally
        {
            dialog?.Dispose();
            Directory.Delete(root, recursive: true);
        }
    }

    /// <summary>外部パネル画像の不正なパスは両方の検証入口で外観設定へ案内し、入力と保存状態を保持します。</summary>
    [TestMethod]
    public void ExternalPanelImageValidation_InvalidPathGuidesToAppearance()
    {
        string root = CreateTemporaryRoot();
        SettingsDialogViewModel? dialog = null;
        try
        {
            Settings values = CreateValidStandaloneSettings(root);
            string missingImagePath = Path.Combine(root, "missing-panel-image.png");
            values.UseExternalPanelImage = true;
            values.StagefilePath = missingImagePath;
            var session = new CountingSettingsEditSession(values);
            dialog = CreateViewModel(session, firstStartup: false).SettingDialog;
            string expectedMessage = string.Format(
                Resources.SettingValidation_SectionMessageFormat,
                Resources.Appearance,
                Resources.Error_InvalidStagefilePath);

            Assert.IsFalse(dialog.CheckValidation(out string validationError));
            StringAssert.Contains(validationError, expectedMessage);
            Assert.IsFalse(dialog.CheckValidationBeforeSave(out string saveError));
            StringAssert.Contains(saveError, expectedMessage);
            Assert.AreEqual(missingImagePath, dialog.StagefilePath);
            Assert.AreEqual(0, session.SaveCount);

            dialog.UseExternalPanelImage = false;

            Assert.IsTrue(dialog.CheckValidation(out validationError), validationError);
            Assert.IsTrue(dialog.CheckValidationBeforeSave(out saveError), saveError);
            Assert.AreEqual(missingImagePath, dialog.StagefilePath);
            Assert.AreEqual(0, session.SaveCount);
        }
        finally
        {
            dialog?.Dispose();
            Directory.Delete(root, recursive: true);
        }
    }

    /// <summary>導入先・通常／root 出力先の未設定は項目別 Error と保存拒否へ一致し、有効値の再選択で全て解除することを確認して、表示と保存可否の不一致を検出します。</summary>
    [TestMethod]
    public void RequiredSettingsValidationPresentation_UsesErrorStateForSaveBlockingFields()
    {
        string root = CreateTemporaryRoot();
        SettingsDialogViewModel? dialog = null;
        try
        {
            Settings values = CreateValidCustomFolderSettings(root, []);
            string validInstallDir = values.BMSInstallDir;
            string validNormalOutput = values.LR2CustomFolderOutputBaseDir;
            string validRootOutput = values.LR2CustomFolderOutputBaseDirRootType;
            values.BMSInstallDir = string.Empty;
            values.LR2CustomFolderOutputBaseDir = string.Empty;
            values.LR2CustomFolderOutputBaseDirRootType = string.Empty;
            var session = new CountingSettingsEditSession(values);
            dialog = CreateViewModel(session, firstStartup: false).SettingDialog;

            Assert.AreEqual("Error", dialog.BmsInstallDirValidationStatus);
            Assert.AreEqual(Resources.Error_InvalidBmsInstallDir, dialog.BmsInstallDirValidationMessage);
            Assert.AreEqual("Error", dialog.CustomFolderOutputDirValidationStatus);
            Assert.AreEqual(Resources.Error_CustomFolderOutputPathNotSet, dialog.CustomFolderOutputDirValidationMessage);
            Assert.AreEqual("Error", dialog.CustomFolderRootOutputDirValidationStatus);
            Assert.AreEqual(Resources.Error_CustomFolderRootOutputPathNotSet, dialog.CustomFolderRootOutputDirValidationMessage);
            Assert.IsFalse(dialog.CheckValidationBeforeSave(out string saveError));
            StringAssert.Contains(saveError, Resources.Error_InvalidBmsInstallDir);
            StringAssert.Contains(saveError, Resources.Error_CustomFolderOutputPathNotSet);
            StringAssert.Contains(saveError, Resources.Error_CustomFolderRootOutputPathNotSet);

            dialog.BMSInstallDir = validInstallDir;
            dialog.LR2CustomFolderOutputDir = validNormalOutput;
            dialog.LR2CustomFolderAsRootOutputDir = validRootOutput;

            Assert.AreEqual(string.Empty, dialog.BmsInstallDirValidationStatus);
            Assert.AreEqual(string.Empty, dialog.BmsInstallDirValidationMessage);
            Assert.AreEqual(string.Empty, dialog.CustomFolderOutputDirValidationStatus);
            Assert.AreEqual(string.Empty, dialog.CustomFolderOutputDirValidationMessage);
            Assert.AreEqual(string.Empty, dialog.CustomFolderRootOutputDirValidationStatus);
            Assert.AreEqual(string.Empty, dialog.CustomFolderRootOutputDirValidationMessage);
            Assert.IsTrue(dialog.CheckValidationBeforeSave(out saveError), saveError);
        }
        finally
        {
            dialog?.Dispose();
            Directory.Delete(root, recursive: true);
        }
    }

    /// <summary>標準 path から個別 DB への変更で由来表示を分け、取消 snapshot の復元で DB／config と標準由来表示を同時に戻すことを確認します。</summary>
    [TestMethod]
    public void Lr2PathPresentation_StandardAndIndividualDraftsFollowSharedCancelSnapshot()
    {
        string root = CreateTemporaryRoot();
        string standardSongDb = Path.Combine(root, "LR2files", "Database", "song.db");
        string standardConfig = Path.Combine(root, "LR2files", "Config", "config.xml");
        string individualSongDb = Path.Combine(root, "custom", "songs.db");
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(standardSongDb)!);
            Directory.CreateDirectory(Path.GetDirectoryName(standardConfig)!);
            Directory.CreateDirectory(Path.GetDirectoryName(individualSongDb)!);
            File.WriteAllBytes(standardSongDb, []);
            File.WriteAllText(standardConfig, "<config><system /><jukebox /></config>");
            File.WriteAllBytes(individualSongDb, []);
            File.WriteAllBytes(Path.Combine(root, "LR2body.exe"), []);
            Settings values = CreateValidStandaloneSettings(root);
            values.LR2RootPath = root;
            values.LR2SongDBPath = standardSongDb;
            values.LR2ConfigXmlPath = standardConfig;
            var session = new CountingSettingsEditSession(values);
            using SettingsDialogViewModel dialog = CreateViewModel(session, firstStartup: false).SettingDialog;

            Assert.AreEqual("Success", dialog.Lr2SongDbPathStatusKind);
            Assert.AreEqual(Resources.Settings_path_detected_from_lr2_root, dialog.Lr2SongDbPathStatusText);
            Assert.AreEqual(Resources.Settings_path_detected_from_lr2_root, dialog.Lr2ConfigPathStatusText);

            dialog.LR2SongDBPath = individualSongDb;

            Assert.AreEqual(Resources.Settings_path_detected_from_individual_setting, dialog.Lr2SongDbPathStatusText);
            Assert.AreEqual(Resources.Settings_path_detected_from_lr2_root, dialog.Lr2ConfigPathStatusText);

            dialog.ResetSettings();

            Assert.AreEqual(standardSongDb, dialog.LR2SongDBPath);
            Assert.AreEqual(standardConfig, dialog.LR2ConfigXmlPath);
            Assert.AreEqual(Resources.Settings_path_detected_from_lr2_root, dialog.Lr2SongDbPathStatusText);
            Assert.AreEqual(Resources.Settings_path_detected_from_lr2_root, dialog.Lr2ConfigPathStatusText);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
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

    /// <summary>欠損／壊れた保存済み config の raw tuple を開く・保存・再表示で保ち、別 root への編集取消も元 tuple へ戻ることを確認して、暗黙の標準 path 置換を検出します。</summary>
    [DataTestMethod]
    [DataRow(null)]
    [DataRow("<config>")]
    [DataRow("<config />")]
    [DataRow("<other><jukebox /></other>")]
    public async Task Lr2InvalidPersistedConfig_OpenSaveReopenAndParentCancelPreserveRawTuple(string? invalidXml)
    {
        string scope = CreateTemporaryRoot();
        try
        {
            string originalRoot = Path.Combine(scope, "original");
            (string originalSong, _) = CreateValidLr2Layout(originalRoot);
            string rawConfig = Path.Combine(scope, "custom", "Config", "config.xml");
            if (invalidXml != null)
            {
                Directory.CreateDirectory(Path.GetDirectoryName(rawConfig)!);
                File.WriteAllText(rawConfig, invalidXml);
            }
            string nextRoot = Path.Combine(scope, "next");
            CreateValidLr2Layout(nextRoot);

            Settings values = CreateValidStandaloneSettings(scope);
            values.LR2RootPath = originalRoot;
            values.LR2SongDBPath = originalSong;
            values.LR2ConfigXmlPath = rawConfig;
            var session = new CountingSettingsEditSession(values);
            using SettingsDialogViewModel opened = CreateViewModel(session, firstStartup: false).SettingDialog;

            Assert.AreEqual(rawConfig, opened.LR2ConfigXmlPath);
            Assert.AreEqual(invalidXml != null ? "Error" : "Warning", opened.Lr2ConfigPathStatusKind);
            await opened.SaveSettings();

            using SettingsDialogViewModel reopened = CreateViewModel(session, firstStartup: false).SettingDialog;
            Assert.AreEqual(originalRoot, reopened.LR2RootPath);
            Assert.AreEqual(originalSong, reopened.LR2SongDBPath);
            Assert.AreEqual(rawConfig, reopened.LR2ConfigXmlPath);

            reopened.LR2RootPath = nextRoot;
            Assert.AreEqual(nextRoot, reopened.LR2RootPath);
            reopened.ResetSettings();

            Assert.AreEqual(originalRoot, reopened.LR2RootPath);
            Assert.AreEqual(originalSong, reopened.LR2SongDBPath);
            Assert.AreEqual(rawConfig, reopened.LR2ConfigXmlPath);
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

    /// <summary>同じ有効 root の再選択で個別 DB／config を標準 tuple と由来へ戻し、同値 root として更新を省く誤りを検出します。</summary>
    [TestMethod]
    public void Lr2RootPicker_ReselectingSameRootRestoresStandardTupleAndSource()
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
            Assert.AreEqual(standardConfig, draft.LR2ConfigXmlPath);
            Assert.AreEqual(Resources.Settings_path_detected_from_lr2_root, draft.Lr2SongDbPathStatusText);
            Assert.AreEqual(Resources.Settings_path_detected_from_lr2_root, draft.Lr2ConfigPathStatusText);
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

    /// <summary>root／xml／xmh の同 path 再選択で外部編集の検索 root・player identity を通知し、後の実 config 保存でも外部 sentinel と追加 root を保つことを確認します。</summary>
    [DataTestMethod]
    [DataRow(nameof(SettingsDialogViewModel.LR2RootPath), "config.xml")]
    [DataRow(nameof(SettingsDialogViewModel.LR2ConfigXmlPath), "config.xml")]
    [DataRow(nameof(SettingsDialogViewModel.LR2ConfigXmlPath), "config.xmh")]
    public async Task Lr2PathPickers_ReselectingCurrentPathAdoptsExternalConfigBeforeLaterSave(
        string propertyName,
        string configFileName)
    {
        string scope = CreateTemporaryRoot();
        try
        {
            string root = Path.Combine(scope, "root");
            (string song, string configPath) = CreateValidLr2Layout(root);
            configPath = Path.Combine(Path.GetDirectoryName(configPath)!, configFileName);
            File.WriteAllText(configPath, "<config><system /><player><id>player1</id></player><jukebox /></config>");
            string externalRoot = Path.Combine(scope, "external");
            string addedRoot = Path.Combine(scope, "added");
            Directory.CreateDirectory(externalRoot);
            Directory.CreateDirectory(addedRoot);
            Settings values = CreateValidStandaloneSettings(scope);
            values.OperationModeLR2DB = true;
            values.LR2RootPath = root;
            values.LR2SongDBPath = song;
            values.LR2ConfigXmlPath = configPath;
            var session = new CountingSettingsEditSession(values);
            using SettingsDialogViewModel draft = CreateViewModel(session, firstStartup: false).SettingDialog;
            string scoreDirectory = Path.Combine(root, "LR2files", "Database", "Score");
            Assert.AreEqual(Path.Combine(scoreDirectory, "player1.db"), draft.Lr2PlayHistoryScoreDbPath);
            int directoryNotifications = 0;
            int historyTargetNotifications = 0;
            draft.PropertyChanged += (_, args) =>
            {
                if (args.PropertyName == nameof(draft.LR2ConfigBMSDirectories))
                {
                    directoryNotifications++;
                }
                if (args.PropertyName == nameof(draft.Lr2PlayHistoryScoreDbPath))
                {
                    historyTargetNotifications++;
                }
            };
            var externalDocument = new XDocument(
                new XElement("config",
                    new XElement("system"),
                    new XElement("player", new XElement("id", "player2")),
                    new XElement("sentinel", new XAttribute("source", "external")),
                    new XElement("jukebox", new XElement("path", externalRoot + Path.DirectorySeparatorChar))));
            externalDocument.Save(configPath);

            if (propertyName == nameof(draft.LR2RootPath))
            {
                draft.SetRootFolderPathFromPicker(propertyName, root);
            }
            else
            {
                draft.SetFilePathFromPicker(propertyName, configPath);
            }

            CollectionAssert.Contains(draft.LR2ConfigBMSDirectories.ToArray(), externalRoot);
            Assert.IsTrue(directoryNotifications > 0);
            Assert.AreEqual(Path.Combine(scoreDirectory, "player2.db"), draft.Lr2PlayHistoryScoreDbPath);
            Assert.IsTrue(historyTargetNotifications > 0);
            Assert.AreEqual(root, draft.LR2RootPath);
            Assert.AreEqual(song, draft.LR2SongDBPath);
            Assert.AreEqual(configPath, draft.LR2ConfigXmlPath);
            Assert.AreEqual(0, session.SaveCount);

            draft.AddBmsSearchRootPaths([addedRoot]);
            await draft.SaveSettings();

            var savedDocument = XDocument.Load(configPath);
            Assert.AreEqual("external", (string?)savedDocument.Root?.Element("sentinel")?.Attribute("source"));
            Assert.AreEqual("player2", (string?)savedDocument.Root?.Element("player")?.Element("id"));
            string[] savedRoots = savedDocument.Root?.Element("jukebox")?.Elements("path")
                .Select(element => element.Value.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar))
                .ToArray() ?? [];
            CollectionAssert.Contains(savedRoots, externalRoot);
            CollectionAssert.Contains(savedRoots, addedRoot);
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

    /// <summary>個別 DB／config の選択を共有 draft へ反映・保存し、再表示でも同 path と個別由来を保つことを確認して、選択を表示だけに閉じる誤りを検出します。</summary>
    [TestMethod]
    public async Task Lr2IndividualPickers_SelectedPathsStayInSharedDraftForSettingsSaveAndReopen()
    {
        string scope = CreateTemporaryRoot();
        try
        {
            string standardRoot = Path.Combine(scope, "standard");
            (string standardSong, string standardConfig) = CreateValidLr2Layout(standardRoot);
            string individualSong = Path.Combine(scope, "individual", "database", "songs.db");
            string individualConfig = Path.Combine(scope, "individual", "configuration", "config.xmh");
            Directory.CreateDirectory(Path.GetDirectoryName(individualSong)!);
            Directory.CreateDirectory(Path.GetDirectoryName(individualConfig)!);
            File.WriteAllBytes(individualSong, []);
            File.WriteAllText(individualConfig, "<config><system /><jukebox /></config>");

            Settings values = CreateValidStandaloneSettings(scope);
            values.LR2RootPath = standardRoot;
            values.LR2SongDBPath = standardSong;
            values.LR2ConfigXmlPath = standardConfig;
            var session = new CountingSettingsEditSession(values);
            using SettingsDialogViewModel draft = CreateViewModel(session, firstStartup: false).SettingDialog;

            draft.SetFilePathFromPicker(nameof(draft.LR2SongDBPath), individualSong);
            draft.SetFilePathFromPicker(nameof(draft.LR2ConfigXmlPath), individualConfig);

            Assert.AreEqual(individualSong, draft.LR2SongDBPath);
            Assert.AreEqual(individualConfig, draft.LR2ConfigXmlPath);
            Assert.AreEqual(Resources.Settings_path_detected_from_individual_setting, draft.Lr2SongDbPathStatusText);
            Assert.AreEqual(Resources.Settings_path_detected_from_individual_setting, draft.Lr2ConfigPathStatusText);

            await draft.SaveSettings();
            Assert.AreEqual(1, session.SaveCount);

            using SettingsDialogViewModel reopened = CreateViewModel(session, firstStartup: false).SettingDialog;
            Assert.AreEqual(individualSong, reopened.LR2SongDBPath);
            Assert.AreEqual(individualConfig, reopened.LR2ConfigXmlPath);
            Assert.AreEqual(Resources.Settings_path_detected_from_individual_setting, reopened.Lr2SongDbPathStatusText);
            Assert.AreEqual(Resources.Settings_path_detected_from_individual_setting, reopened.Lr2ConfigPathStatusText);
        }
        finally
        {
            Directory.Delete(scope, recursive: true);
        }
    }

    /// <summary>保存済み個別 DB／config を未編集で保存・再表示しても同 path と個別由来を保ち、標準 tuple へ戻す誤りを検出します。</summary>
    [TestMethod]
    public async Task Lr2IndividualPaths_OpenWithoutEditingSaveAndReopenPreservesRawValues()
    {
        string scope = CreateTemporaryRoot();
        try
        {
            string root = Path.Combine(scope, "standard");
            CreateValidLr2Layout(root);
            string customSong = Path.Combine(scope, "saved", "database", "songs.db");
            string customConfig = Path.Combine(scope, "saved", "configuration", "config.xml");
            Directory.CreateDirectory(Path.GetDirectoryName(customSong)!);
            Directory.CreateDirectory(Path.GetDirectoryName(customConfig)!);
            File.WriteAllBytes(customSong, []);
            File.WriteAllText(customConfig, "<config><system /><jukebox /></config>");

            Settings values = CreateValidStandaloneSettings(scope);
            values.LR2RootPath = root;
            values.LR2SongDBPath = customSong;
            values.LR2ConfigXmlPath = customConfig;
            var session = new CountingSettingsEditSession(values);
            using SettingsDialogViewModel opened = CreateViewModel(session, firstStartup: false).SettingDialog;

            Assert.AreEqual(Resources.Settings_path_detected_from_individual_setting, opened.Lr2SongDbPathStatusText);
            Assert.AreEqual(Resources.Settings_path_detected_from_individual_setting, opened.Lr2ConfigPathStatusText);
            await opened.SaveSettings();

            using SettingsDialogViewModel reopened = CreateViewModel(session, firstStartup: false).SettingDialog;
            Assert.AreEqual(customSong, reopened.LR2SongDBPath);
            Assert.AreEqual(customConfig, reopened.LR2ConfigXmlPath);
            Assert.AreEqual(Resources.Settings_path_detected_from_individual_setting, reopened.Lr2SongDbPathStatusText);
            Assert.AreEqual(Resources.Settings_path_detected_from_individual_setting, reopened.Lr2ConfigPathStatusText);
        }
        finally
        {
            Directory.Delete(scope, recursive: true);
        }
    }

    /// <summary>各出力役割の既登録 root 採用は削除済み登録も含め保存前に確認し、取消で config を保ち、承認後の継続保存は再確認しないことを確認します。</summary>
    [DataTestMethod]
    [DataRow("normal", false)]
    [DataRow("normal", true)]
    [DataRow("additional", false)]
    [DataRow("additional", true)]
    [DataRow("root", false)]
    [DataRow("root", true)]
    public async Task ApplySettingsAsync_RestoresRegisteredOutputWithCancellableConfirmation(string role, bool removeRegistrationFirst)
    {
        string root = CreateTemporaryRoot();
        SettingsDialogViewModel? dialog = null;
        try
        {
            string restoredOutput = Path.Combine(root, "RestoredOutput");
            string registeredRoot = role == "root" ? Path.Combine(restoredOutput, "OldPlaylist") : restoredOutput;
            Settings values = CreateValidCustomFolderSettings(root, [registeredRoot]);
            if (role == "normal")
            {
                values.LR2CustomFolderOutputBaseDir = string.Empty;
            }
            else if (role == "root")
            {
                values.LR2CustomFolderOutputBaseDirRootType = string.Empty;
            }
            var session = new CountingSettingsEditSession(values);
            var dialogs = new RecordingRootDialogService();
            var failures = new List<Exception>();
            int initializationCount = 0;
            MainWindowViewModel owner = CreateViewModel(session, firstStartup: false,
                initializeOwner: _ =>
                {
                    initializationCount++;
                    return Task.FromResult(true);
                },
                reportSettingsApplyFailure: failures.Add,
                dialogs: dialogs);
            dialog = owner.SettingDialog;
            byte[] savedXml = File.ReadAllBytes(values.LR2ConfigXmlPath);

            if (removeRegistrationFirst)
            {
                // 一般ページで登録を削除してから出力先に指定しても、保存時の採用確認は必要です。
                CollectionAssert.Contains(dialog.LR2ConfigBMSDirectories, registeredRoot);
                ((ICommand)dialog.RemoveDirCommand).Execute(registeredRoot);
                CollectionAssert.DoesNotContain(dialog.LR2ConfigBMSDirectories, registeredRoot);
                CollectionAssert.AreEqual(savedXml, File.ReadAllBytes(values.LR2ConfigXmlPath));
            }
            SetCustomFolderOutput(dialog, role, restoredOutput);

            Assert.AreEqual(0, dialogs.MessageCount, dialogs.LastMessageText);
            Assert.AreEqual(0, dialogs.ConfirmationCount);
            Assert.IsTrue(dialog.CheckValidationBeforeSave(out string validationError), validationError);
            CollectionAssert.DoesNotContain(dialog.LR2ConfigBMSDirectories, registeredRoot);
            await dialog.ApplySettingsAsync();

            Assert.AreEqual(1, dialogs.ConfirmationCount);
            string confirmation = dialogs.ConfirmationRequests.Single().MessageBoxText;
            StringAssert.Contains(confirmation, restoredOutput);
            StringAssert.Contains(confirmation, registeredRoot);
            string searchRemovalNotice = string.Format(Resources.Confirm_CustomFolderOutputSearchRootsRemovedFormat, registeredRoot);
            Assert.AreEqual(role != "normal", confirmation.Contains(searchRemovalNotice, StringComparison.Ordinal));
            Assert.AreEqual(0, session.SaveCount);
            Assert.AreEqual(0, initializationCount);
            Assert.IsTrue(dialog.HasPendingSettingChanges());
            CollectionAssert.AreEqual(savedXml, File.ReadAllBytes(values.LR2ConfigXmlPath));

            dialogs.ConfirmationResult = UiDialogResult.FromMessageBoxResult(MessageBoxResult.OK);
            await dialog.ApplySettingsAsync();

            Assert.AreEqual(2, dialogs.ConfirmationCount);
            Assert.AreEqual(1, session.SaveCount);
            Assert.AreEqual(1, initializationCount);
            Assert.IsFalse(dialog.HasPendingSettingChanges());
            Assert.AreEqual(0, failures.Count, string.Join(Environment.NewLine, failures));

            // 保存済みの役割を継続する限り、他の設定を保存しても採用確認を繰り返しません。
            values.ShowDuplicateFileCheckConfirmMsg = !values.ShowDuplicateFileCheckConfirmMsg;
            await dialog.ApplySettingsAsync();
            Assert.AreEqual(2, dialogs.ConfirmationCount);
            Assert.AreEqual(2, session.SaveCount);
            Assert.AreEqual(0, dialogs.MessageCount, dialogs.LastMessageText);
            Assert.AreEqual(0, failures.Count, string.Join(Environment.NewLine, failures));
        }
        finally
        {
            dialog?.Dispose();
            Directory.Delete(root, recursive: true);
        }
    }

    /// <summary>通常出力先を二度編集した採用確認は保存済み旧検索 root だけを示し、追加出力先として保持する場合も中間 draft を警告に混ぜず、取消で config を保つことを確認します。</summary>
    [DataTestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public async Task ApplySettingsAsync_NormalOutputChangeWarnsOnlyForSavedSearchRoot(bool keepAsAdditional)
    {
        string root = CreateTemporaryRoot();
        SettingsDialogViewModel? dialog = null;
        try
        {
            string previousOutput = Path.Combine(root, "NormalOutput");
            string intermediateOutput = Path.Combine(root, "Intermediate");
            string currentOutput = Path.Combine(root, "Current");
            Directory.CreateDirectory(intermediateOutput);
            Directory.CreateDirectory(currentOutput);
            Settings values = CreateValidCustomFolderSettings(root, [previousOutput]);
            var session = new CountingSettingsEditSession(values);
            var dialogs = new RecordingRootDialogService();
            MainWindowViewModel owner = CreateViewModel(session, firstStartup: false,
                initializeOwner: _ => Task.FromResult(true), dialogs: dialogs);
            dialog = owner.SettingDialog;
            byte[] savedXml = File.ReadAllBytes(values.LR2ConfigXmlPath);

            dialog.LR2CustomFolderOutputDir = intermediateOutput;
            dialog.LR2CustomFolderOutputDir = currentOutput;
            if (keepAsAdditional)
            {
                dialog.AddCustomFolderAdditionalOutputBaseDir(previousOutput);
            }
            Assert.AreEqual(0, dialogs.MessageCount, dialogs.LastMessageText);
            Assert.AreEqual(0, dialogs.ConfirmationCount);
            Assert.AreEqual(currentOutput, dialog.LR2CustomFolderOutputDir);
            Assert.IsTrue(dialog.CheckValidationBeforeSave(out string validationError), validationError);

            await dialog.ApplySettingsAsync();

            Assert.AreEqual(1, dialogs.ConfirmationCount);
            string confirmation = dialogs.ConfirmationRequests.Single().MessageBoxText;
            StringAssert.Contains(confirmation,
                string.Format(Resources.Confirm_CustomFolderOutputSearchRootsRemovedFormat, previousOutput));
            Assert.IsFalse(confirmation.Contains(intermediateOutput, StringComparison.OrdinalIgnoreCase));
            Assert.AreEqual(0, session.SaveCount);
            CollectionAssert.AreEqual(savedXml, File.ReadAllBytes(values.LR2ConfigXmlPath));
        }
        finally
        {
            dialog?.Dispose();
            Directory.Delete(root, recursive: true);
        }
    }

    /// <summary>各出力役割と BMS 登録 root の禁止される親子関係を選択と保存検証の両方で拒み、元出力先・登録・config を保つことを確認します。</summary>
    [DataTestMethod]
    [DataRow("normal", false)]
    [DataRow("normal", true)]
    [DataRow("additional", false)]
    [DataRow("additional", true)]
    [DataRow("root", false)]
    public void CustomFolderOutput_RejectsForbiddenBmsNestingAtSelectionAndSave(string role, bool outputIsParent)
    {
        string root = CreateTemporaryRoot();
        SettingsDialogViewModel? dialog = null;
        try
        {
            string parent = Path.Combine(root, "BMS");
            string registeredRoot = Path.Combine(parent, "Registered");
            string candidate = outputIsParent ? parent : Path.Combine(registeredRoot, "Custom");
            Settings values = CreateValidCustomFolderSettings(root, [registeredRoot]);
            Directory.CreateDirectory(candidate);
            var session = new CountingSettingsEditSession(values);
            var dialogs = new RecordingRootDialogService();
            MainWindowViewModel owner = CreateViewModel(session, firstStartup: false, dialogs: dialogs);
            dialog = owner.SettingDialog;
            string previousNormal = dialog.LR2CustomFolderOutputDir;
            string previousRoot = dialog.LR2CustomFolderAsRootOutputDir;
            byte[] savedXml = File.ReadAllBytes(values.LR2ConfigXmlPath);

            SetCustomFolderOutput(dialog, role, candidate);

            Assert.AreEqual(1, dialogs.MessageCount);
            Assert.AreEqual(0, dialogs.ConfirmationCount);
            StringAssert.Contains(dialogs.LastMessageText, registeredRoot);
            Assert.AreEqual(previousNormal, dialog.LR2CustomFolderOutputDir);
            Assert.AreEqual(previousRoot, dialog.LR2CustomFolderAsRootOutputDir);
            Assert.AreEqual(0, dialog.CustomFolderAdditionalOutputBaseDirList.Count);

            // XML選択前の入力や既存設定も、保存時には同じ条件で拒否します。
            switch (role)
            {
                case "normal": values.LR2CustomFolderOutputBaseDir = candidate; break;
                case "root": values.LR2CustomFolderOutputBaseDirRootType = candidate; break;
                case "additional": dialog.CustomFolderAdditionalOutputBaseDirList.Add(candidate); break;
                default: throw new ArgumentOutOfRangeException(nameof(role));
            }
            Assert.IsFalse(dialog.CheckValidationBeforeSave(out string saveError));
            StringAssert.Contains(saveError, registeredRoot);
            Assert.IsFalse(dialog.CheckValidation(out string initialError));
            StringAssert.Contains(initialError, registeredRoot);
            Assert.AreEqual(0, session.SaveCount);
            CollectionAssert.AreEqual(savedXml, File.ReadAllBytes(values.LR2ConfigXmlPath));
        }
        finally
        {
            dialog?.Dispose();
            Directory.Delete(root, recursive: true);
        }
    }

    /// <summary>通常・追加・root の役割組と同一／親／子関係のどちらの入力順でも二つ目を拒み、全出力先リストを保つことを確認して、順序依存の重複登録を検出します。</summary>
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
    // entry 0:初期Apply、1:稼働中All適用、2:通常startup、3:tree再初期化。二つのboolは確認中/通知中の終了を区別します。
    [DataRow(0, false, false)]
    [DataRow(1, false, false)]
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
            if (entry == 1)
            {
                targetDb = Path.Combine(root, "alternate", "LR2files", "Database", "song.db");
                Directory.CreateDirectory(Path.GetDirectoryName(targetDb)!);
                File.WriteAllBytes(targetDb, []);
            }
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
                var actualLibrary = new TestBmsLibrary(targetDb, () => config, null, mutation, null, scheduler,
                    () => BmsLibraryOptionsSnapshot.CreateCurrent(values), chartFileScanner: CapturedChartFileScanner.FromFixture([],
                    new Dictionary<string, IEnumerable<string>>(StringComparer.OrdinalIgnoreCase) { [Path.Combine(root, "Songs")] = [] }, [Path.Combine(root, "Songs")]),
                    operationAdmission: composition.OperationAdmission,
                    playlistOperationAdmission: composition.PlaylistOperationAdmission, settings: values, rootFileEnumerator: new FastRootFileEnumerator());
                library = actualLibrary;
                playlist = MainWindowViewModelTestFactory.CreatePlaylist(targetDb, values, () => config, library: actualLibrary);
                var activeOwner = new MainWindowViewModel(composition, new LateFailureStartupLibraryFactory(actualLibrary, playlist));
                owner = activeOwner;
                var presentation = new RecordingSettingsDialogPresentationPort();
                activeOwner.SettingDialog.AttachPresentationPort(presentation);
                if (entry is 1 or 3)
                {
                    SetPrivateField(activeOwner, "initializationCompleted", true);
                    SetPrivateField(activeOwner, "hasActiveLibraryProfile", true);
                }
                if (entry == 0) { activeOwner.SettingDialog.OverwritePlaylistUrlsWithCompletion = !activeOwner.SettingDialog.OverwritePlaylistUrlsWithCompletion; }
                if (entry == 1) { activeOwner.SettingDialog.LR2SongDBPath = targetDb; }
                if (entry == 3)
                {
                    // 既存の実接続を使い、任意保守は停止中のschedulerへ登録する。モデル単独fallbackへ迂回しない。
                    var applicationPort = (IStartupLibraryApplicationPort)activeOwner;
                    applicationPort.AttachStartupLibrary(actualLibrary);
                    applicationPort.AttachStartupServices(new StartupLibraryServices(new LibraryProfile(true, targetDb,
                        [Path.Combine(root, "Songs")], () => config, null, true, true, false, false, "leap-reinitialize-fixture"), actualLibrary, playlist));
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
                        0 => activeOwner.SettingDialog.ApplySettingsAsync(),
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
                        if (entry == 0 && dialogs.LastMessageText == Resources.Msg_initsetting_completed) { return; }
                        dialogs.MessageResult = UiDialogResult.NotShown(UiDialogStatus.AppClosing);
                        notificationEntered.TrySetResult();
                        await releaseNotification.Task;
                    };
                    Task? shutdown = null;
                    Task operation = entry switch
                    {
                        0 => activeOwner.SettingDialog.ApplySettingsAsync(),
                        2 => activeOwner.InitializeAsync(),
                        _ => activeOwner.ReinitializeLibraryAsync()
                    };
                    try
                    {
                        TestUiDispatcherHost.AwaitTaskOnDispatcher(TestUiDispatcherHost.AwaitNotificationAsync(notificationEntered.Task, operation,
                            "leap-notification-shutdown-entered"), "leap-notification-shutdown-arrival");
                        Assert.AreEqual(entry == 0 ? 2 : 1, notificationObservations.Count);
                        CollectionAssert.AreEqual(entry == 0
                            ? new[] { Resources.Msg_initsetting_completed, Resources.Warn_LR2LeapYearBugDetected }
                            : new[] { Resources.Warn_LR2LeapYearBugDetected }, notificationObservations.Select(value => value.Message).ToArray());
                        if (entry == 0)
                        {
                            Assert.IsFalse(notificationObservations[0].Changed);
                            Assert.IsFalse(notificationObservations[0].Warning);
                        }
                        Assert.IsFalse(operation.IsCompleted);
                        shutdown = activeOwner.ShellShutdownWorkflow.RequestWindowCloseAsync();
                        releaseNotification.TrySetResult();
                        TestUiDispatcherHost.AwaitTaskOnDispatcher(operation, "leap-notification-shutdown-operation");
                        TestUiDispatcherHost.AwaitTaskOnDispatcher(shutdown, "leap-notification-shutdown-terminal");
                        if (operation is Task<bool> initialization) { Assert.IsFalse(initialization.GetAwaiter().GetResult()); }
                        Assert.AreEqual(entry == 0 ? 1 : 0, settingsSession.SaveCount);
                        Assert.AreEqual(1, mutation.TimestampCalls.Count);
                        Assert.AreEqual(entry == 0 ? 2 : 1, dialogs.MessageCount);
                        CollectionAssert.AreEqual(entry == 0 ? new[] { "close" } : Array.Empty<string>(), presentation.Requests);
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
                if (entry is 0 or 1)
                {
                    // どちらのBusyでも候補確認・保存・修復はなく、解放後の新しい明示要求を受け付ける。
                    foreach (ChartFileOperationSynchronizer admission in new[] { composition.OperationAdmission, composition.PlaylistOperationAdmission })
                    {
                        Assert.IsTrue(admission.TryEnter(out IDisposable held));
                        using (held) { TestUiDispatcherHost.AwaitTaskOnDispatcher(activeOwner.SettingDialog.ApplySettingsAsync(), "leap-settings-busy"); }
                        Assert.AreEqual(0, settingsSession.SaveCount);
                        Assert.AreEqual(0, dialogs.ConfirmationCount);
                        Assert.AreEqual(0, mutation.TimestampCalls.Count);
                    }
                    // 確認中に他操作が先着した場合も予約を持たず、副作用前に拒否する。
                    if (entry == 0)
                    {
                        Action<UiConfirmationRequest>? assertConfirmation = dialogs.ConfirmationObserved;
                        dialogs.ConfirmationObserved = request =>
                        {
                            assertConfirmation?.Invoke(request);
                            Assert.IsTrue(composition.OperationAdmission.TryEnter(out competing));
                        };
                        TestUiDispatcherHost.AwaitTaskOnDispatcher(activeOwner.SettingDialog.ApplySettingsAsync(), "leap-settings-lost-admission");
                        Assert.AreEqual(0, settingsSession.SaveCount);
                        Assert.AreEqual(0, mutation.TimestampCalls.Count);
                        competing?.Dispose(); competing = null;
                        dialogs.ConfirmationObserved = assertConfirmation;
                        beforeAcceptedConfirmations = dialogs.ConfirmationCount;
                    }
                    TestUiDispatcherHost.AwaitTaskOnDispatcher(activeOwner.SettingDialog.ApplySettingsAsync(), "leap-settings-accepted");
                    Assert.AreEqual(1, settingsSession.SaveCount);
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
        config.Save();
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
                        ? (capability, approval, observer) => ((ISettingsDialogStatePort)viewModel).InitializeLibraryAsync(capability, approval, observer)
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
                settingsSession.SaveOperationModeForRestart(request.OperationMode, request.HistoryIdentity);
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
        AudioDriver expectedDriver = AudioDriver.WasapiShared)
    {
        Assert.AreEqual(expectedDriver, audioGateway.PlayerDriver);
        Assert.AreEqual("requested-device", audioGateway.OutputSelection.DeviceIdentity);
        Assert.AreEqual("Requested device", audioGateway.OutputSelection.DeviceName);
        Assert.AreEqual("requested-device", settings.PlayerDevice);
        Assert.AreEqual("Requested device", settings.PlayerDeviceName);
        Assert.AreEqual(SampleRate.SAMPLE_RATE_44100Hz, settings.PlayerSampleRate);
        Assert.AreEqual(SampleFormat.SAMPLE_INT_16BIT, settings.PlayerFormat);
        Assert.AreEqual(4, settings.PlayerResamplingQuality);
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
        public ChartScanExecutionResult Scan(IEnumerable<string> roots, IEnumerable<string> extensions, bool verboseLog = false,
            bool includeTextSurface = true, bool includeDirectorySurface = false)
            => Current.Scan(roots, extensions, verboseLog, includeTextSurface, includeDirectorySurface);
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
            throw new NotSupportedException();

        public Task<UiFolderPickerResult> PickFolderAsync(UiFolderPickerRequest request, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

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
            LeapYearFolderRepairApproval? leapYearRepairApproval = null, Action<LeapYearFolderRepairNotification>? repairNotificationObserver = null) => Task.FromResult(new StartupInitializationResult(StartupInitializationOutcome.Succeeded));

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
        private readonly ChartFileOperationSynchronizer outputAdmission = new();

        public bool CanBeginOutputOperation => outputAdmission.CanEnter;

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

        public Task ExportBeatorajaBmtAsync(string reason, string cleanupTablePath, LibraryFileMutationCapability capability)
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
            CustomFolderOutputSettingsSnapshot settings, LibraryFileMutationCapability? capability = null)
        {
        }

        public void ChangeCustomFolderBaseDirectoryRootWithSettings(
            string outputDirBaseBefore,
            string outputDirBaseAfter,
            CustomFolderOutputSettingsSnapshot settings, LibraryFileMutationCapability? capability = null)
        {
        }

        public bool SyncCustomFolderOutputSearchRootsAfterSettingsChangeWithSettings(
            string previousRootOutputBaseDirectory,
            CustomFolderOutputSettingsSnapshot settings) => false;

        public int ApplyCustomFolderAdditionalOutputBaseRegistrationChanges(
            string previousAdditionalOutputBaseDirectories,
            IReadOnlyDictionary<string, string> pendingRenames,
            CustomFolderOutputSettingsSnapshot settings, LibraryFileMutationCapability? capability = null) => 0;

#pragma warning disable CS0067 // インターフェイスのイベント面を満たすが、このテストダブルでは発火させない。
        public event EventHandler<PlaylistCatalogChangedEventArgs>? PlaylistCatalogChanged;
#pragma warning restore CS0067
    }

    private sealed class CountingSettingsEditSession : ISettingsEditSession
    {
        public void SaveOperationModeForRestart(bool operationMode, string historyIdentity)
        {
            Values.OperationModeLR2DB = operationMode;
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

        public void VolumeChanged()
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

        public Task ApplyPlayerSettingsAsync(IBMSPlayer replacementPlayer)
        {
            ApplyCount++;
            LastReplacementPlayer = replacementPlayer;
            sequence?.Add("apply");
            return Task.CompletedTask;
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

        public void ApplySearchTargets(IReadOnlyList<string> searchTargets)
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
