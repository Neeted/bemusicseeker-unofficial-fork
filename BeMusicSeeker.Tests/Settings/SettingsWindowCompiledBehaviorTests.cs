using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Data;
using System.Windows.Media;
using BeMusicSeeker.Models;
using BeMusicSeeker.Models.Utils;
using BeMusicSeeker.Properties;
using BeMusicSeeker.ViewModels;
using BeMusicSeeker.Views;
using BeMusicSeeker.Views.Dialogs;
using BeMusicSeeker.Views.Settings;
using BeMusicSeeker.Views.Settings.Pages;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using NLog;
using NLog.Config;
using NLog.Targets;
using Ribbit.Media.Audio;

namespace BeMusicSeeker.Tests;

/// <summary>
/// compiled 設定ページの draft・入力判断・表示と操作先への接続を確認します。
/// </summary>
/// <remarks>条件網羅は設定・音声 owner が担当し、この系列は実 BAML の Binding とイベント配送を担当します。共有辞書・文化・テーマを変更するため非並列です。</remarks>
[TestClass]
[DoNotParallelize]
public sealed class SettingsWindowCompiledBehaviorTests
{
    [TestInitialize]
    public void MaterializeCanonicalApplicationResources()
    {
        TestUiDispatcherHost.Invoke(EnsureCanonicalApplicationResources);
    }

    /// <summary>実navigationの初回選択でだけページを構築し、再訪問で未確定入力を保持し、実閉鎖で全ページの編集元を解放します。</summary>
    /// <remarks>構築診断と実BAMLのBinding・navigation・Closeを観測します。音声一覧と能力は固定結果とし、native処理の寿命は音声workflowのケースが分担します。</remarks>
    [TestMethod]
    public void EverySettingsNavigationPageMaterializesWithoutCreatingPendingChanges()
    {
        TestUiDispatcherHost.RunWindowTest(scope =>
        {
            Settings settingsValues = MainWindowViewModelTestFactory.CreateIsolatedSettings(values =>
            {
                values.OperationModeLR2DB = false;
                values.BMSRootPath = Path.GetTempPath();
                values.StandaloneBmsRootPaths = Path.GetTempPath();
                values.BMSInstallDir = Path.GetTempPath();
                values.ScanBmsFilesOnStartup = false;
                values.SkipInitPlaylistLoad = true;
                values.UseBeatorajaScoreDb = false;
                values.EnableBeatorajaBmtOutput = false;
                values.UsePlayeruBMplay = false;
                values.UsePlayerLR2body = false;
                values.UsePlayerBMIIDXView = false;
            });
            settingsValues.Save();
            MainWindowViewModel owner = MainWindowViewModelTestFactory.Create(settingsValues);
            owner.SettingDialog.Dispose();
            using var dialog = new SettingsDialogViewModel(
                owner, owner.PlaylistWorkspace, owner.PlaylistWorkspace, owner.PlayHistory, owner.LibraryFolderTree,
                new TestSettingsDialogPlayerFactoryPort(), new TestSettingsDialogPlaybackRuntimePort(),
                owner.Lr2SongDbSyncWorkflow, new SettingsEditSession(settingsValues),
                applicationLifetime: TestApplicationContext.CreateLifetime(),
                cultureCatalog: TestApplicationContext.CreateCultureCatalog(),
                audioDeviceTestWorkflow: new AudioDeviceTestWorkflowOwner(
                    new NoOpAudioDeviceTestPlaybackPort(), new SuccessfulAudioDeviceTestRuntime(),
                    new DelegateAudioDeviceCapabilityRuntime(request => new AudioDeviceCapabilityResult(
                        request.Backend, request.DeviceIdentity, request.DeviceName,
                        AudioDeviceCapabilityStatus.Unsupported, []))),
                externalShellGateway: ExternalShellGatewayPolicy.Current,
                applicationPathSnapshot: ApplicationPathPolicy.Current,
                audioDeviceCatalog: new TestAudioDeviceCatalog(),
                audioSettingsGateway: new SettingsAudioGateway(() => settingsValues));
            var persistedRequests = new CountingSettingsProvider(settingsValues.Properties[nameof(Settings.PlayerDriver)].Provider);
            foreach (System.Configuration.SettingsProperty property in settingsValues.Properties) { property.Provider = persistedRequests; }
            Logger logger = Ribbit.Logging.NLogWrapper.GetLogger(typeof(SettingsWindowCompiledBehaviorTests));
            FieldInfo fileLoggerProvider = typeof(Ribbit.Logging.NLogWrapper)
                .GetField("_FileLogger", BindingFlags.Static | BindingFlags.NonPublic)
                ?? throw new AssertFailedException("File logger provider is unavailable.");
            object? previousFileLogger = fileLoggerProvider.GetValue(null);
            LoggingConfiguration? previousLogging = LogManager.Configuration;
            using var logs = new MemoryTarget { Layout = "${message}" };
            SettingsWindow? window = null;
            try
            {
                var configuration = new LoggingConfiguration();
                configuration.AddRule(LogLevel.Info, LogLevel.Fatal, logs, logger.Name);
                LogManager.Configuration = configuration;
                fileLoggerProvider.SetValue(null, (Func<Logger>)(() => logger));
                window = new SettingsWindow
                {
                    DataContext = dialog,
                    PlaybackPanel = owner.PlaybackPanel
                };
                scope.ShowAndWaitForContentRendered(window);
                Assert.AreEqual(1, logs.Logs.Count(message => message.Contains("settings_page_construct_start")));
                Assert.AreEqual(1, logs.Logs.Count(message => message.Contains("settings_page_construct_end")));

                var navigation = (ListBox)window.FindName("settingsNavigation");
                var content = (ContentControl)window.FindName("settingsPageContent");
                int visitedCount = 0;
                var pages = new List<FrameworkElement>();
                foreach (object item in navigation.Items.Cast<object>().ToArray())
                {
                    navigation.SelectedItem = item;
                    Materialize(window);
                    visitedCount++;
                    Assert.AreEqual(visitedCount, logs.Logs.Count(message => message.Contains("settings_page_construct_start")));
                    Assert.AreEqual(visitedCount, logs.Logs.Count(message => message.Contains("settings_page_construct_end")));
                    Assert.IsNotNull(content.Content);
                    var page = (FrameworkElement)content.Content;
                    pages.Add(page);
                    Assert.AreSame(dialog, page.DataContext);
                }
                Assert.AreEqual(0, persistedRequests.SetRequests, "開く・全lazy page接続は実providerへの保存要求0です。");
                navigation.SelectedItem = window.FindName("navigationPlaylist");
                Materialize(window);
                var playlistPage = (PlaylistSettingsPage)content.Content;
                var urlEditor = (TextBox)playlistPage.FindName("tableListUrlTextBox");
                Uri originalUrl = dialog.TableListURL;
                urlEditor.Text = "uncommitted URL input";
                Assert.AreEqual(originalUrl, dialog.TableListURL, "LostFocus前の入力はsourceへ未確定です。");
                navigation.SelectedItem = window.FindName("navigationGeneral");
                Materialize(window);
                navigation.SelectedItem = window.FindName("navigationPlaylist");
                Materialize(window);
                Assert.AreSame(playlistPage, content.Content);
                Assert.AreEqual(originalUrl, dialog.TableListURL);
                Assert.AreEqual("uncommitted URL input",
                    ((TextBox)((PlaylistSettingsPage)content.Content).FindName("tableListUrlTextBox")).Text);
                Assert.AreEqual(navigation.Items.Count, logs.Logs.Count(message => message.Contains("settings_page_construct_start")));
                var closed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
                window.Closed += (_, _) => closed.TrySetResult();
                window.CloseForOwnerShutdown();
                TestUiDispatcherHost.AwaitPresentationOnDispatcher(closed.Task, "settings-navigation-closed");
                Materialize(window);
                Assert.IsNull(window.DataContext);
                foreach (FrameworkElement page in pages)
                {
                    Assert.IsNull(page.DataContext, "表示中・非表示の作成済みページをすべて解放します。");
                }
            }
            finally
            {
                try
                {
                    if (window?.IsVisible == true) { window.CloseForOwnerShutdown(); }
                    TestUiDispatcherHost.AwaitTaskOnDispatcher(dialog.AudioSettingsCompletionTask, "settings-navigation-audio-cleanup");
                }
                finally
                {
                    fileLoggerProvider.SetValue(null, previousFileLogger);
                    LogManager.Configuration = previousLogging;
                }
            }
        });
    }

    /// <summary>内部・uBMplay・BMIIDXView・LR2 の四選択が、それぞれの意味を持つ設定 Binding と同じ draft に接続されることを確認し誤配線を検出します。</summary>
    [TestMethod]
    public void PlaybackPageCompiledTreeBindsPlayerChoicesToSettingsDraft()
    {
        TestUiDispatcherHost.Invoke(() =>
        {
            MainWindowViewModel owner = MainWindowViewModelTestFactory.Create(MainWindowViewModelTestFactory.CreateIsolatedSettings(values =>
            {
                values.OperationModeLR2DB = false;
                values.BMSRootPath = Path.GetTempPath();
                values.StandaloneBmsRootPaths = Path.GetTempPath();
                values.BMSInstallDir = Path.GetTempPath();
                values.ScanBmsFilesOnStartup = false;
                values.SkipInitPlaylistLoad = true;
                values.UsePlayeruBMplay = false;
                values.UsePlayerLR2body = false;
                values.UsePlayerBMIIDXView = false;
            }));
            var window = new SettingsWindow
            {
                DataContext = owner.SettingDialog,
                PlaybackPanel = owner.PlaybackPanel
            };
            try
            {
                Materialize(window);
                var navigation = (ListBox)window.FindName("settingsNavigation");
                var content = (ContentControl)window.FindName("settingsPageContent");
                navigation.SelectedItem = window.FindName("navigationPlayback");
                Materialize(window);

                var page = (PlaybackSettingsPage)content.Content;
                var internalPlayer = (RadioButton)page.FindName("radioButtonInternalPlayer");
                var ubmplayPlayer = (RadioButton)page.FindName("radioButtonPlayuBMplay");
                var bmiidxPlayer = (RadioButton)page.FindName("radioButtonPlayBMIIDXView");
                var lr2Player = (RadioButton)page.FindName("radioButtonPlayLR2body");

                Assert.AreSame(owner.SettingDialog, page.DataContext);
                Assert.AreSame(owner.SettingDialog, internalPlayer.DataContext);
                Assert.AreSame(owner.SettingDialog, ubmplayPlayer.DataContext);
                Assert.AreSame(owner.SettingDialog, bmiidxPlayer.DataContext);
                Assert.AreSame(owner.SettingDialog, lr2Player.DataContext);
                Assert.AreEqual(nameof(SettingsDialogViewModel.UseInternalPlayer), GetBindingPath(internalPlayer, ToggleButton.IsCheckedProperty));
                Assert.AreEqual(nameof(SettingsDialogViewModel.UsePlayeruBMplay), GetBindingPath(ubmplayPlayer, ToggleButton.IsCheckedProperty));
                Assert.AreEqual(nameof(SettingsDialogViewModel.UsePlayerBMIIDXView), GetBindingPath(bmiidxPlayer, ToggleButton.IsCheckedProperty));
                Assert.AreEqual(nameof(SettingsDialogViewModel.UsePlayerLR2body), GetBindingPath(lr2Player, ToggleButton.IsCheckedProperty));
            }
            finally
            {
                owner.SettingDialog.Dispose();
            }
        });
    }

    /// <summary>実ComboBoxの4値選択をVMへ渡し、取消の復元・言語変更後の値保持と表示更新、不正値の無選択・警告を確認します。</summary>
    /// <remarks>BAMLのBindingとResourceService通知を実Windowで観測し、保存拒否の条件網羅は編集sessionの系列へ分担します。</remarks>
    [TestMethod]
    public void AdvancedPageStartupSortCachePriorityBindsRestoresAndLocalizesSelection()
    {
        TestUiDispatcherHost.RunWindowTest(scope =>
        {
            string previousCulture = Resources.Culture?.Name ?? "ja-JP";
            MainWindowViewModel? owner = null;
            SettingsWindow? window = null;
            try
            {
                ResourceService.Current.ChangeCulture("en-US");
                Settings values = MainWindowViewModelTestFactory.CreateIsolatedSettings(settings => settings.StartupSortCachePrewarmPriority = 4);
                owner = MainWindowViewModelTestFactory.Create(values);
                window = new SettingsWindow { DataContext = owner.SettingDialog, PlaybackPanel = owner.PlaybackPanel };
                scope.ShowAndWaitForContentRendered(window);
                ((ListBox)window.FindName("settingsNavigation")).SelectedItem = window.FindName("navigationAdvanced");
                Materialize(window);
                var page = (AdvancedSettingsPage)((ContentControl)window.FindName("settingsPageContent")).Content;
                ComboBox selector = FindLogicalDescendants<ComboBox>(page).Single(control => GetBindingPath(control, Selector.SelectedValueProperty) == nameof(SettingsDialogViewModel.StartupSortCachePrewarmPriority));
                SettingsStatusBanner warning = FindLogicalDescendants<SettingsStatusBanner>(page).Single(control => GetBindingPath(control, ContentControl.ContentProperty) == nameof(SettingsDialogViewModel.StartupSortCachePrewarmWarning));
                Assert.AreEqual(-1, selector.SelectedIndex);
                Assert.IsNull(selector.SelectedValue);
                Assert.AreEqual(string.Format(Resources.Startup_sort_cache_invalid, 4), warning.Content);
                Assert.IsTrue(warning.IsVisible);
                foreach (int priority in new[] { 0, 1, 2, 3 })
                {
                    selector.SetCurrentValue(Selector.SelectedValueProperty, priority);
                    Materialize(window);
                    Assert.AreEqual(priority, values.StartupSortCachePrewarmPriority);
                    Assert.AreEqual(priority, owner.SettingDialog.StartupSortCachePrewarmPriority);
                }
                string english = ((KeyValuePair<int, string>)selector.SelectedItem).Value;
                ResourceService.Current.ChangeCulture("ja-JP");
                Materialize(window);
                Assert.AreEqual(3, selector.SelectedValue);
                Assert.AreEqual(Resources.Startup_sort_cache_all, ((KeyValuePair<int, string>)selector.SelectedItem).Value);
                Assert.AreNotEqual(english, ((KeyValuePair<int, string>)selector.SelectedItem).Value);
                Assert.AreEqual(3, values.StartupSortCachePrewarmPriority);
                Assert.AreEqual(3, selector.SelectedValue);
                Assert.IsFalse(warning.IsVisible);
            }
            finally
            {
                window?.CloseForOwnerShutdown();
                owner?.SettingDialog.Dispose();
                ResourceService.Current.ChangeCulture(previousCulture);
            }
        });
    }

    /// <summary>新規導入の確認欄を実操作して値を変え Reset で戻し、別の導入確認値を巻き込む誤りや復元が control に届かない不備を検出します。</summary>
    [TestMethod]
    public void InstallPageNewPackageConfirmationBindsDefaultAndRestoresIndependently()
    {
        TestUiDispatcherHost.RunWindowTest(scope =>
        {
            string previousCulture = Resources.Culture?.Name ?? "ja-JP";
            MainWindowViewModel? owner = null;
            SettingsWindow? window = null;
            try
            {
                ResourceService.Current.ChangeCulture("en-US");
                Settings settings = MainWindowViewModelTestFactory.CreateIsolatedSettings();
                settings.OperationModeLR2DB = false;
                settings.BMSRootPath = Path.GetTempPath();
                settings.StandaloneBmsRootPaths = Path.GetTempPath();
                settings.BMSInstallDir = Path.GetTempPath();
                settings.ScanBmsFilesOnStartup = false;
                settings.SkipInitPlaylistLoad = true;
                settings.UsePlayeruBMplay = false;
                settings.UsePlayerLR2body = false;
                settings.UsePlayerBMIIDXView = false;
                settings.ShowDiffBMSInstallConfirmMsg = false;
                Assert.IsTrue(settings.ShowNewPackageInstallConfirmMsg);
                owner = MainWindowViewModelTestFactory.Create(settings);
                window = new SettingsWindow
                {
                    DataContext = owner.SettingDialog,
                    PlaybackPanel = owner.PlaybackPanel
                };
                scope.ShowAndWaitForContentRendered(window);
                ((ListBox)window.FindName("settingsNavigation")).SelectedItem = window.FindName("navigationInstall");
                Materialize(window);

                var page = (InstallSettingsPage)((ContentControl)window.FindName("settingsPageContent")).Content;
                CheckBox confirmation = FindLogicalDescendants<CheckBox>(page).Single(checkBox =>
                    GetBindingPath(checkBox, ToggleButton.IsCheckedProperty)
                        == nameof(SettingsDialogViewModel.ShowNewPackageInstallConfirmMsg));
                CheckBox diffConfirmation = FindLogicalDescendants<CheckBox>(page).Single(checkBox =>
                    GetBindingPath(checkBox, ToggleButton.IsCheckedProperty)
                        == nameof(SettingsDialogViewModel.ShowDiffBMSInstallConfirmMsg));
                Assert.AreSame(owner.SettingDialog, confirmation.DataContext);
                Assert.IsTrue(confirmation.IsVisible);
                Assert.IsTrue(confirmation.ActualWidth > 0 && confirmation.ActualHeight > 0);
                Assert.AreEqual(Resources.Details_show_diag_new_install, confirmation.Content);
                Assert.IsTrue(confirmation.IsChecked == true);
                Assert.IsTrue(owner.SettingDialog.ShowNewPackageInstallConfirmMsg);

                confirmation.SetCurrentValue(ToggleButton.IsCheckedProperty, false);
                Materialize(window);
                Assert.IsFalse(owner.SettingDialog.ShowNewPackageInstallConfirmMsg);
                Assert.IsFalse(settings.ShowNewPackageInstallConfirmMsg);
                Assert.IsFalse(owner.SettingDialog.ShowDiffBMSInstallConfirmMsg);
                Assert.IsFalse(settings.ShowDiffBMSInstallConfirmMsg);
                Assert.IsTrue(diffConfirmation.IsChecked == false);

                Assert.IsTrue(confirmation.IsChecked == false);
                Assert.IsFalse(owner.SettingDialog.ShowNewPackageInstallConfirmMsg);
                Assert.IsFalse(settings.ShowNewPackageInstallConfirmMsg);
                Assert.IsTrue(diffConfirmation.IsChecked == false);
                Assert.IsFalse(settings.ShowDiffBMSInstallConfirmMsg);
            }
            finally
            {
                window?.CloseForOwnerShutdown();
                owner?.SettingDialog.Dispose();
                ResourceService.Current.ChangeCulture(previousCulture);
            }
        });
    }

    /// <summary>実 audio control で backend・rate・format と mixer draft を変更し、能力に従う表示・AUTO への再評価・一時 null の非適用と保存前値の保持を確認します。</summary>
    [TestMethod]
    public void AudioPagesBindPlayerCapabilitiesAndIndependentEncodingOptions()
    {
        TestUiDispatcherHost.Invoke(() =>
        {
            Settings settings = MainWindowViewModelTestFactory.CreateIsolatedSettings(values =>
            {
                values.OperationModeLR2DB = false;
                values.BMSRootPath = Path.GetTempPath();
                values.StandaloneBmsRootPaths = Path.GetTempPath();
                values.BMSInstallDir = Path.GetTempPath();
                values.PlayerSampleRate = SampleRate.SAMPLE_RATE_44100Hz;
                values.PlayerFormat = SampleFormat.SAMPLE_INT_16BIT;
                values.PlayerResamplingQuality = 4;
                values.PlayerMixerThreadCount = 2;
                values.ScanBmsFilesOnStartup = false;
                values.SkipInitPlaylistLoad = true;
                values.UsePlayeruBMplay = false;
                values.UsePlayerLR2body = false;
                values.UsePlayerBMIIDXView = false;
            });
            MainWindowViewModel owner = MainWindowViewModelTestFactory.Create(settings);
            using BeMusicSeeker.ViewModels.SettingsDialogViewModel ownerSettingsLifetime = owner.SettingDialog;
            var window = new SettingsWindow
            {
                DataContext = owner.SettingDialog,
                PlaybackPanel = owner.PlaybackPanel
            };
            try
            {
                Materialize(window);
                owner.SettingDialog.SetPresentationActive(active: false);
                var navigation = (ListBox)window.FindName("settingsNavigation");
                var content = (ContentControl)window.FindName("settingsPageContent");
                navigation.SelectedItem = window.FindName("navigationAudio");
                Materialize(window);

                var audioPage = (AudioSettingsPage)content.Content;
                var testButton = (Button)audioPage.FindName("buttonPlayerTest");
                var eventCheck = (CheckBox)audioPage.FindName("checkBoxWasapiLowLatency");
                var eventDescription = (TextBlock)audioPage.FindName("textWasapiLowLatencyDescription");
                var playerSampleRate = (ComboBox)audioPage.FindName("comboBoxPlayerSampleRate");
                var playerFormat = (ComboBox)audioPage.FindName("comboBoxPlayerFormat");
                var playerResamplingQuality = (ComboBox)audioPage.FindName("comboBoxPlayerResamplingQuality");
                var playerMixerThreadCount = (ComboBox)audioPage.FindName("comboBoxPlayerMixerThreadCount");
                var qualityDescription = (TextBlock)audioPage.FindName("textPlayerResamplingQualityDescription");
                var mixerThreadDescription = (TextBlock)audioPage.FindName("textPlayerMixerThreadCountDescription");
                var endpointFormat = (TextBlock)audioPage.FindName("textPlayerCapabilityFormat");
                var sharedRate = (TextBlock)audioPage.FindName("textPlayerCapabilityRate");

                Assert.AreEqual(nameof(SettingsDialogViewModel.PlayerSampleRateNames),
                    playerSampleRate.GetBindingExpression(ItemsControl.ItemsSourceProperty)?.ParentBinding.Path.Path);
                Assert.AreEqual(nameof(SettingsDialogViewModel.PlayerFormatNames),
                    playerFormat.GetBindingExpression(ItemsControl.ItemsSourceProperty)?.ParentBinding.Path.Path);
                Assert.AreEqual(nameof(SettingsDialogViewModel.AudioDeviceCapabilityFormatDescription),
                    endpointFormat.GetBindingExpression(TextBlock.TextProperty)?.ParentBinding.Path.Path);
                Assert.AreEqual(nameof(SettingsDialogViewModel.AudioDeviceCapabilityRateDescription),
                    sharedRate.GetBindingExpression(TextBlock.TextProperty)?.ParentBinding.Path.Path);
                Assert.AreEqual(nameof(SettingsDialogViewModel.SelectedPlayerSampleRate),
                    playerSampleRate.GetBindingExpression(Selector.SelectedValueProperty)?.ParentBinding.Path.Path);
                Assert.AreEqual(nameof(SettingsDialogViewModel.PlayerResamplingQualityNames),
                    playerResamplingQuality.GetBindingExpression(ItemsControl.ItemsSourceProperty)?.ParentBinding.Path.Path);
                Assert.AreEqual(nameof(SettingsDialogViewModel.PlayerResamplingQuality),
                    playerResamplingQuality.GetBindingExpression(Selector.SelectedValueProperty)?.ParentBinding.Path.Path);
                Assert.AreEqual(nameof(SettingsDialogViewModel.PlayerMixerThreadCountNames),
                    playerMixerThreadCount.GetBindingExpression(ItemsControl.ItemsSourceProperty)?.ParentBinding.Path.Path);
                Assert.AreEqual(nameof(SettingsDialogViewModel.PlayerMixerThreadCount),
                    playerMixerThreadCount.GetBindingExpression(Selector.SelectedValueProperty)?.ParentBinding.Path.Path);
                Assert.AreEqual(nameof(SettingsDialogViewModel.PlayerWASAPIParam),
                    eventCheck.GetBindingExpression(ToggleButton.IsCheckedProperty)?.ParentBinding.Path.Path);
                Assert.AreEqual("Resources.Device_setting_lowlatency_desc",
                    eventDescription.GetBindingExpression(TextBlock.TextProperty)?.ParentBinding.Path.Path);
                Assert.AreEqual("Resources.Device_setting_resampling_quality_desc",
                    qualityDescription.GetBindingExpression(TextBlock.TextProperty)?.ParentBinding.Path.Path);
                Assert.AreEqual("Resources.Device_setting_resampling_parallelism_desc",
                    mixerThreadDescription.GetBindingExpression(TextBlock.TextProperty)?.ParentBinding.Path.Path);
                Assert.AreEqual(Resources.Device_setting_lowlatency_desc, eventDescription.Text);
                Assert.AreEqual(Resources.Device_setting_resampling_quality_desc, qualityDescription.Text);
                Assert.AreEqual(Resources.Device_setting_resampling_parallelism_desc, mixerThreadDescription.Text);
                Assert.AreEqual(Visibility.Visible, eventCheck.Visibility);
                Assert.AreEqual(Visibility.Visible, eventDescription.Visibility);
                Assert.AreEqual(4, playerResamplingQuality.SelectedValue);
                Assert.AreEqual(2, playerMixerThreadCount.SelectedValue);
                playerMixerThreadCount.SelectedValue = 3;
                Materialize(window);
                Assert.AreEqual(3, owner.SettingDialog.PlayerMixerThreadCount);
                Assert.AreEqual(3, settings.PlayerMixerThreadCount, "独立した並列度は項目確定で保存します。");
                Assert.AreEqual(Resources.AudioDeviceTestStartButton, testButton.Content);
                Assert.AreEqual(nameof(SettingsDialogViewModel.AudioDeviceTestButtonContent),
                    testButton.GetBindingExpression(ContentControl.ContentProperty)?.ParentBinding.Path.Path);

                int savedDriverIndex = owner.SettingDialog.PlayerDriverIndex;
                var driverSelector = (ComboBox)audioPage.FindName("comboBoxPlayerDriver");
                ComboBox deviceSelector = FindLogicalDescendants<ComboBox>(audioPage).Single(comboBox =>
                    GetBindingPath(comboBox, Selector.SelectedItemProperty)
                        == nameof(SettingsDialogViewModel.SelectedPlayerDevice));
                Assert.AreEqual(SampleRate.SAMPLE_RATE_44100Hz, playerSampleRate.SelectedValue);
                Assert.AreEqual(SampleFormat.SAMPLE_INT_16BIT, playerFormat.SelectedValue);

                driverSelector.SelectedIndex = AudioDriverPolicy.IndexOf(AudioDriver.WasapiExclusive);
                Materialize(window);
                Assert.AreEqual(Visibility.Visible, eventCheck.Visibility);
                Assert.AreEqual(Visibility.Visible, eventDescription.Visibility);
                Assert.AreEqual(SampleRate.AUTO, owner.SettingDialog.PlayerSampleRate);
                Assert.AreEqual(SampleFormat.AUTO, owner.SettingDialog.PlayerFormat);
                Assert.AreEqual(Visibility.Visible, playerSampleRate.Visibility);
                Assert.AreEqual(Visibility.Visible, playerFormat.Visibility);

                playerSampleRate.SelectedValue = SampleRate.SAMPLE_RATE_44100Hz;
                playerFormat.SelectedValue = SampleFormat.SAMPLE_INT_16BIT;
                Assert.AreEqual(SampleRate.SAMPLE_RATE_44100Hz, owner.SettingDialog.PlayerSampleRate);
                Assert.AreEqual(SampleFormat.SAMPLE_INT_16BIT, owner.SettingDialog.PlayerFormat);

                playerSampleRate.SelectedValue = null;
                playerFormat.SelectedValue = null;
                deviceSelector.SelectedItem = null;
                Materialize(window);
                Assert.AreEqual(SampleRate.SAMPLE_RATE_44100Hz, owner.SettingDialog.PlayerSampleRate);
                Assert.AreEqual(SampleFormat.SAMPLE_INT_16BIT, owner.SettingDialog.PlayerFormat);
                playerSampleRate.GetBindingExpression(Selector.SelectedValueProperty)?.UpdateTarget();
                playerFormat.GetBindingExpression(Selector.SelectedValueProperty)?.UpdateTarget();
                deviceSelector.GetBindingExpression(Selector.SelectedItemProperty)?.UpdateTarget();

                var formatItems = new Dictionary<SampleFormat, string>(owner.SettingDialog.PlayerFormatNames);
                playerFormat.SetCurrentValue(ItemsControl.ItemsSourceProperty, formatItems);
                playerFormat.GetBindingExpression(ItemsControl.ItemsSourceProperty)?.UpdateTarget();
                playerSampleRate.SelectedValue = SampleRate.SAMPLE_RATE_44100Hz;
                playerFormat.SelectedValue = SampleFormat.SAMPLE_INT_16BIT;
                driverSelector.GetBindingExpression(Selector.SelectedIndexProperty)?.UpdateTarget();
                deviceSelector.GetBindingExpression(Selector.SelectedItemProperty)?.UpdateTarget();
                Assert.AreEqual(SampleRate.SAMPLE_RATE_44100Hz, owner.SettingDialog.PlayerSampleRate);
                Assert.AreEqual(SampleFormat.SAMPLE_INT_16BIT, owner.SettingDialog.PlayerFormat);

                driverSelector.SelectedIndex = AudioDriverPolicy.IndexOf(AudioDriver.Asio);
                Materialize(window);
                Assert.AreEqual(SampleRate.AUTO, owner.SettingDialog.PlayerSampleRate);
                Assert.AreEqual(SampleFormat.AUTO, owner.SettingDialog.PlayerFormat);
                Assert.AreEqual(Visibility.Visible, playerSampleRate.Visibility);
                Assert.AreEqual(Visibility.Collapsed, playerFormat.Visibility);
                Assert.AreEqual(Visibility.Visible, endpointFormat.Visibility);
                Assert.AreEqual(Visibility.Collapsed, eventCheck.Visibility);
                Assert.AreEqual(Visibility.Collapsed, eventDescription.Visibility);
                Assert.AreEqual(Resources.AudioDeviceCapabilityNotQueried, endpointFormat.Text);
                owner.SettingDialog.PlayerDriverIndex = savedDriverIndex;
                Materialize(window);

                navigation.SelectedItem = window.FindName("navigationRecording");
                Materialize(window);
                var recordingPage = (RecordingSettingsPage)content.Content;
                var encoderSampleRate = (ComboBox)recordingPage.FindName("comboBoxEncoderSampleRate");
                var encoderFormat = (ComboBox)recordingPage.FindName("comboBoxEncoderFormat");
                Assert.AreEqual(nameof(SettingsDialogViewModel.EncoderSampleRateNames),
                    encoderSampleRate.GetBindingExpression(ItemsControl.ItemsSourceProperty)?.ParentBinding.Path.Path);
                Assert.AreEqual(nameof(SettingsDialogViewModel.EncoderFormatNames),
                    encoderFormat.GetBindingExpression(ItemsControl.ItemsSourceProperty)?.ParentBinding.Path.Path);
                Assert.IsFalse(((SettingsDialogViewModel)owner.SettingDialog).EncoderSampleRateNames
                    .ContainsKey(SampleRate.SAMPLE_RATE_384000Hz));
            }
            finally
            {
                window.CloseForOwnerShutdown();
                owner.SettingDialog.Dispose();
            }
        });
    }

    /// <summary>encoder欠損時も実録音ページに保存済みMP3を表示し、明示WAVEの保存・再表示と、利用不能形式の拒否後の表示を確定値へ揃えます。</summary>
    /// <remarks>実BAMLのSelectedIndex双方向Binding、provider保存、変換前snapshotを接続して検査します。拒否条件の組合せは非Windowケースが担当し、実encoder検索・起動・変換は対象外です。</remarks>
    [TestMethod]
    public void RecordingPage_PreservesUnavailableSavedEncoderAndPersistsExplicitWaveAcrossReopen()
    {
        TestUiDispatcherHost.RunWindowTest(scope =>
        {
            string configPath = Path.Combine(MainWindowViewModelTestFactory.CreateIsolatedSettingsDirectory(), "user.config");
            Settings settings = PortableSettingsPersistenceTests.OpenSettings(configPath);
            settings.OperationModeLR2DB = false;
            settings.BMSRootPath = Path.GetTempPath();
            settings.StandaloneBmsRootPaths = Path.GetTempPath();
            settings.BMSInstallDir = Path.GetTempPath();
            settings.ScanBmsFilesOnStartup = false;
            settings.SkipInitPlaylistLoad = true;
            settings.UseBeatorajaScoreDb = false;
            settings.EnableBeatorajaBmtOutput = false;
            settings.UsePlayeruBMplay = false;
            settings.UsePlayerLR2body = false;
            settings.UsePlayerBMIIDXView = false;
            settings.Encoder = EncoderType.MP3_LAME;
            settings.Save();
            MainWindowViewModel owner = MainWindowViewModelTestFactory.Create(settings);
            owner.SettingDialog.Dispose();
            var persistedRequests = new CountingSettingsProvider(settings.Properties[nameof(Settings.Encoder)].Provider);
            foreach (System.Configuration.SettingsProperty property in settings.Properties) { property.Provider = persistedRequests; }
            var dialogs = new SettingsDialogBehaviorTests.RecordingDialogService
            {
                MessageResult = UiDialogResult.FromMessageBoxResult(MessageBoxResult.OK)
            };
            var gateway = new SettingsAudioGateway(() => settings);
            using var dialog = new SettingsDialogViewModel(
                owner, owner.PlaylistWorkspace, owner.PlaylistWorkspace, owner.PlayHistory, owner.LibraryFolderTree,
                new TestSettingsDialogPlayerFactoryPort(), new TestSettingsDialogPlaybackRuntimePort(),
                owner.Lr2SongDbSyncWorkflow, new SettingsEditSession(settings),
                applicationLifetime: TestApplicationContext.CreateLifetime(),
                cultureCatalog: TestApplicationContext.CreateCultureCatalog(), schemaDialogs: dialogs,
                audioDeviceTestWorkflow: AudioDeviceTestWorkflowTestFactory.Create(),
                externalShellGateway: ExternalShellGatewayPolicy.Current,
                applicationPathSnapshot: ApplicationPathPolicy.Current,
                audioDeviceCatalog: new SettingsDialogBehaviorTests.WaveOnlyAudioDeviceCatalog(),
                audioSettingsGateway: gateway);
            SettingsWindow? window = null;
            try
            {
                window = new SettingsWindow { DataContext = dialog, PlaybackPanel = owner.PlaybackPanel };
                scope.ShowAndWaitForContentRendered(window);
                ((ListBox)window.FindName("settingsNavigation")).SelectedItem = window.FindName("navigationRecording");
                Materialize(window);
                var page = (RecordingSettingsPage)((ContentControl)window.FindName("settingsPageContent")).Content;
                var encoder = (ComboBox)page.FindName("comboBoxEncoder");
                Assert.IsTrue(BindingOperations.IsDataBound(encoder, Selector.SelectedIndexProperty));
                Assert.AreEqual((int)EncoderType.MP3_LAME, encoder.SelectedIndex, "実行ファイル欠損で保存済みMP3をWAVE表示へ変えません。");
                Assert.AreEqual(EncoderType.MP3_LAME, SelectedChartAudioConversionSettingsSnapshot.CreateCurrent(gateway.CaptureEncodingSettings()).Encoder);
                Assert.AreEqual(0, persistedRequests.SetRequests, "初期表示とsnapshot取得は保存しません。");
                Assert.AreEqual(0, dialogs.MessageCount);

                encoder.SetCurrentValue(Selector.SelectedIndexProperty, (int)EncoderType.WAVE);
                Materialize(window);
                Assert.AreEqual(1, persistedRequests.SetRequests, "明示WAVE選択だけを一回保存します。");
                Assert.AreEqual(EncoderType.WAVE, settings.Encoder);
                Assert.AreEqual(EncoderType.WAVE, PortableSettingsPersistenceTests.OpenSettings(configPath).Encoder);
                Assert.AreEqual((int)EncoderType.WAVE, encoder.SelectedIndex);
                Assert.AreEqual(EncoderType.WAVE, SelectedChartAudioConversionSettingsSnapshot.CreateCurrent(gateway.CaptureEncodingSettings()).Encoder);
                window.CloseForOwnerShutdown();

                window = new SettingsWindow { DataContext = dialog, PlaybackPanel = owner.PlaybackPanel };
                scope.ShowAndWaitForContentRendered(window);
                ((ListBox)window.FindName("settingsNavigation")).SelectedItem = window.FindName("navigationRecording");
                Materialize(window);
                page = (RecordingSettingsPage)((ContentControl)window.FindName("settingsPageContent")).Content;
                encoder = (ComboBox)page.FindName("comboBoxEncoder");
                Assert.AreEqual((int)EncoderType.WAVE, encoder.SelectedIndex);
                Assert.AreEqual(1, persistedRequests.SetRequests, "再表示で保存を追加しません。");
                byte[] savedWave = File.ReadAllBytes(configPath);

                encoder.SetCurrentValue(Selector.SelectedIndexProperty, (int)EncoderType.AAC_NERO);
                Materialize(window);
                Assert.AreEqual(1, dialogs.MessageCount);
                Assert.AreEqual(string.Format(CultureInfo.CurrentCulture, Resources.Warn_EncoderExecutableNotFoundFormat, EncoderType.AAC_NERO.GetEncoderFileName()), dialogs.LastMessageText);
                Assert.IsTrue(BindingOperations.IsDataBound(encoder, Selector.SelectedIndexProperty));
                Assert.AreEqual((int)EncoderType.WAVE, encoder.SelectedIndex, "拒否candidateをcontrolに残しません。");
                Assert.AreEqual((int)EncoderType.WAVE, dialog.EncoderIndex);
                Assert.AreEqual(EncoderType.WAVE, settings.Encoder);
                Assert.AreEqual(EncoderType.WAVE, PortableSettingsPersistenceTests.OpenSettings(configPath).Encoder);
                Assert.AreEqual(EncoderType.WAVE, SelectedChartAudioConversionSettingsSnapshot.CreateCurrent(gateway.CaptureEncodingSettings()).Encoder);
                Assert.AreEqual(1, persistedRequests.SetRequests, "利用不能な別形式の拒否は保存しません。");
                CollectionAssert.AreEqual(savedWave, File.ReadAllBytes(configPath));
            }
            finally
            {
                try
                {
                    if (window?.IsVisible == true) { window.CloseForOwnerShutdown(); }
                    TestUiDispatcherHost.AwaitTaskOnDispatcher(dialog.AudioSettingsCompletionTask, "recording-page-audio-cleanup");
                }
                finally
                {
                    TestUiDispatcherHost.AwaitTaskOnDispatcher(owner.ShellShutdownWorkflow.RequestWindowCloseAsync(), "recording-page-owner-cleanup");
                }
            }
        });
    }

    /// <summary>注入した排他機器の能力を実 Window に反映し、format で rate を狭めず、rate 選択時には不適合 format を AUTO へ戻す接続を確認します。</summary>
    [TestMethod]
    public void ExclusiveRateChoicesRemainIndependentFromFormatAndRealBindingResetsFormatToAuto()
    {
        TestUiDispatcherHost.RunWindowTest(scope =>
        {
            Settings settings = MainWindowViewModelTestFactory.CreateIsolatedSettings(values =>
            {
                values.OperationModeLR2DB = false;
                values.BMSRootPath = Path.GetTempPath();
                values.StandaloneBmsRootPaths = Path.GetTempPath();
                values.BMSInstallDir = Path.GetTempPath();
                values.PlayerSampleRate = SampleRate.AUTO;
                values.PlayerFormat = SampleFormat.AUTO;
                values.ScanBmsFilesOnStartup = false;
                values.SkipInitPlaylistLoad = true;
            });
            MainWindowViewModel owner = MainWindowViewModelTestFactory.Create(settings);
            using BeMusicSeeker.ViewModels.SettingsDialogViewModel ownerSettingsLifetime = owner.SettingDialog;
            var queryApplied = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var capabilityRuntime = new DelegateAudioDeviceCapabilityRuntime(request =>
                new AudioDeviceCapabilityResult(
                    request.Backend,
                    request.DeviceIdentity,
                    request.DeviceName,
                    AudioDeviceCapabilityStatus.Available,
                    [SampleRate.SAMPLE_RATE_44100Hz, SampleRate.SAMPLE_RATE_48000Hz],
                    [
                        new(SampleRate.SAMPLE_RATE_44100Hz, SampleFormat.SAMPLE_INT_16BIT, true),
                        new(SampleRate.SAMPLE_RATE_48000Hz, SampleFormat.SAMPLE_INT_24BIT, true)
                    ]));
            var workflow = new AudioDeviceTestWorkflowOwner(
                new NoOpAudioDeviceTestPlaybackPort(),
                new SuccessfulAudioDeviceTestRuntime(),
                capabilityRuntime);
            var audioSettingsGateway = new TestAudioSettingsGateway
            {
                OutputSelection = new AudioOutputSelection(AudioDriver.WasapiExclusive, null, null)
            };
            var dialog = new SettingsDialogViewModel(
                owner,
                owner.PlaylistWorkspace,
                owner.PlaylistWorkspace,
                owner.PlayHistory,
                owner.LibraryFolderTree,
                new TestSettingsDialogPlayerFactoryPort(),
                new TestSettingsDialogPlaybackRuntimePort(),
                owner.Lr2SongDbSyncWorkflow,
                new NoOpSettingsEditSession(settings),
                schemaDialogs: new UiDialogCoordinator(),
                applicationLifetime: TestApplicationContext.CreateLifetime(),
                cultureCatalog: TestApplicationContext.CreateCultureCatalog(),
                audioDeviceTestWorkflow: workflow,
                externalShellGateway: ExternalShellGatewayPolicy.Current,
                applicationPathSnapshot: ApplicationPathPolicy.Current,
                audioDeviceCatalog: new TestAudioDeviceCatalog(),
                audioSettingsGateway: audioSettingsGateway);
            dialog.PropertyChanged += (_, args) =>
            {
                if (args.PropertyName == nameof(SettingsDialogViewModel.PlayerSampleRateNames)
                    && dialog.PlayerSampleRateNames.ContainsKey(SampleRate.SAMPLE_RATE_48000Hz))
                {
                    queryApplied.TrySetResult();
                }
            };
            var window = new SettingsWindow
            {
                DataContext = dialog,
                PlaybackPanel = owner.PlaybackPanel
            };
            try
            {
                scope.ShowAndWaitForContentRendered(window);
                ((ListBox)window.FindName("settingsNavigation")).SelectedItem = window.FindName("navigationAudio");
                Materialize(window);
                TestUiDispatcherHost.AwaitTaskOnDispatcher(queryApplied.Task, "exclusive-audio-capability-query");

                var page = (AudioSettingsPage)((ContentControl)window.FindName("settingsPageContent")).Content;
                var rates = (ComboBox)page.FindName("comboBoxPlayerSampleRate");
                var formats = (ComboBox)page.FindName("comboBoxPlayerFormat");
                Assert.AreEqual(nameof(SettingsDialogViewModel.PlayerSampleRateNames),
                    rates.GetBindingExpression(ItemsControl.ItemsSourceProperty)?.ParentBinding.Path.Path);
                Assert.AreEqual(nameof(SettingsDialogViewModel.PlayerFormatNames),
                    formats.GetBindingExpression(ItemsControl.ItemsSourceProperty)?.ParentBinding.Path.Path);
                CollectionAssert.AreEquivalent(
                    new[] { SampleRate.AUTO, SampleRate.SAMPLE_RATE_44100Hz, SampleRate.SAMPLE_RATE_48000Hz },
                    rates.Items.OfType<KeyValuePair<SampleRate, string>>().Select(item => item.Key).ToArray());
                CollectionAssert.AreEquivalent(
                    new[] { SampleFormat.AUTO, SampleFormat.SAMPLE_INT_16BIT, SampleFormat.SAMPLE_INT_24BIT },
                    formats.Items.OfType<KeyValuePair<SampleFormat, string>>().Select(item => item.Key).ToArray());

                formats.SelectedValue = SampleFormat.SAMPLE_INT_24BIT;
                Materialize(window);
                Assert.AreEqual(SampleFormat.SAMPLE_INT_24BIT, dialog.PlayerFormat);
                Assert.AreEqual(SampleRate.AUTO, dialog.PlayerSampleRate);

                rates.SelectedValue = SampleRate.SAMPLE_RATE_44100Hz;
                Materialize(window);
                Assert.AreEqual(SampleRate.SAMPLE_RATE_44100Hz, dialog.PlayerSampleRate);
                Assert.AreEqual(SampleFormat.AUTO, dialog.PlayerFormat);
                Assert.AreEqual(SampleFormat.AUTO, formats.SelectedValue);
                CollectionAssert.AreEquivalent(
                    new[] { SampleFormat.AUTO, SampleFormat.SAMPLE_INT_16BIT },
                    formats.Items.OfType<KeyValuePair<SampleFormat, string>>().Select(item => item.Key).ToArray());
            }
            finally
            {
                if (window.IsVisible)
                {
                    window.CloseForOwnerShutdown();
                }
                dialog.Dispose();
                owner.SettingDialog.Dispose();
            }
        });
    }

    /// <summary>実 audio ページを表示して選択出力の照会が一回始まり、成功した能力結果を反映することを確認します。</summary>
    [TestMethod]
    public void VisibleAudioSettingsPageAutomaticallyQueriesTheSelectedOutput()
    {
        TestUiDispatcherHost.RunWindowTest(scope =>
        {
            Settings settings = MainWindowViewModelTestFactory.CreateIsolatedSettings(values =>
            {
                values.OperationModeLR2DB = false;
                values.BMSRootPath = Path.GetTempPath();
                values.StandaloneBmsRootPaths = Path.GetTempPath();
                values.BMSInstallDir = Path.GetTempPath();
                values.ScanBmsFilesOnStartup = false;
                values.SkipInitPlaylistLoad = true;
            });
            MainWindowViewModel owner = MainWindowViewModelTestFactory.Create(settings);
            using BeMusicSeeker.ViewModels.SettingsDialogViewModel ownerSettingsLifetime = owner.SettingDialog;
            var queryApplied = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            int queryCount = 0;
            AudioDeviceCapabilityRequest? capturedRequest = null;
            var capabilityRuntime = new DelegateAudioDeviceCapabilityRuntime(request =>
            {
                capturedRequest = request;
                queryCount++;
                return new AudioDeviceCapabilityResult(
                    request.Backend,
                    request.DeviceIdentity,
                    request.DeviceName,
                    AudioDeviceCapabilityStatus.Available,
                    [SampleRate.SAMPLE_RATE_44100Hz]);
            });
            var workflow = new AudioDeviceTestWorkflowOwner(
                new NoOpAudioDeviceTestPlaybackPort(),
                new SuccessfulAudioDeviceTestRuntime(),
                capabilityRuntime);
            var dialog = new SettingsDialogViewModel(
                owner,
                owner.PlaylistWorkspace,
                owner.PlaylistWorkspace,
                owner.PlayHistory,
                owner.LibraryFolderTree,
                new TestSettingsDialogPlayerFactoryPort(),
                new TestSettingsDialogPlaybackRuntimePort(),
                owner.Lr2SongDbSyncWorkflow,
                new NoOpSettingsEditSession(settings),
                schemaDialogs: new UiDialogCoordinator(),
                applicationLifetime: TestApplicationContext.CreateLifetime(),
                cultureCatalog: TestApplicationContext.CreateCultureCatalog(),
                audioDeviceTestWorkflow: workflow,
                externalShellGateway: ExternalShellGatewayPolicy.Current,
                applicationPathSnapshot: ApplicationPathPolicy.Current,
                audioDeviceCatalog: new TestAudioDeviceCatalog(),
                audioSettingsGateway: new TestAudioSettingsGateway());
            dialog.PropertyChanged += (_, args) =>
            {
                if (args.PropertyName == nameof(SettingsDialogViewModel.PlayerSampleRateNames)
                    && dialog.PlayerSampleRateNames.Count > 1)
                {
                    queryApplied.TrySetResult();
                }
            };
            var window = new SettingsWindow
            {
                DataContext = dialog,
                PlaybackPanel = owner.PlaybackPanel
            };
            try
            {
                scope.ShowAndWaitForContentRendered(window);
                var navigation = (ListBox)window.FindName("settingsNavigation");
                navigation.SelectedItem = window.FindName("navigationAudio");
                Materialize(window);
                TestUiDispatcherHost.AwaitTaskOnDispatcher(queryApplied.Task, "visible-audio-page-capability-query");

                Assert.AreEqual(1, queryCount);
                AudioDeviceCapabilityRequest request = capturedRequest
                    ?? throw new AssertFailedException("Visible audio page did not query the selected output.");
                Assert.AreEqual(AudioDriver.WasapiShared, request.Backend);
                Assert.IsNull(request.DeviceIdentity);
                Assert.IsNull(dialog.AudioDeviceCapabilityStatusMessage);
            }
            finally
            {
                if (window.IsVisible)
                {
                    window.CloseForOwnerShutdown();
                }
                dialog.Dispose();
                owner.SettingDialog.Dispose();
            }
        });
    }

    /// <summary>空の利用者 action 設定を実ページへ表示し、二種類の一覧を同じ子 editor へ接続して draft を変更しないことを確認します。</summary>
    [TestMethod]
    public void RightClickPageBindsTheChildEditorWithoutMutatingSettingsOnOpen()
    {
        TestUiDispatcherHost.Invoke(() =>
        {
            MainWindowViewModel owner = MainWindowViewModelTestFactory.Create(MainWindowViewModelTestFactory.CreateIsolatedSettings(values =>
            {
                values.OperationModeLR2DB = false;
                values.BMSRootPath = Path.GetTempPath();
                values.StandaloneBmsRootPaths = Path.GetTempPath();
                values.BMSInstallDir = Path.GetTempPath();
                values.ScanBmsFilesOnStartup = false;
                values.SkipInitPlaylistLoad = true;
                values.RightClickActionsJson = "{\"webActions\":[],\"programActions\":[]}";
            }));
            var window = new SettingsWindow
            {
                DataContext = owner.SettingDialog,
                PlaybackPanel = owner.PlaybackPanel
            };
            try
            {
                Materialize(window);
                var navigation = (ListBox)window.FindName("settingsNavigation");
                var content = (ContentControl)window.FindName("settingsPageContent");
                navigation.SelectedItem = window.FindName("navigationRightClick");
                Materialize(window);

                Assert.IsInstanceOfType<RightClickSettingsPage>(content.Content);
                Assert.AreSame(owner.SettingDialog, ((FrameworkElement)content.Content).DataContext);
                var page = (RightClickSettingsPage)content.Content;
                var webActions = (ListBox)page.FindName("webActionsListBox");
                var programActions = (ListBox)page.FindName("programActionsListBox");
                Assert.AreEqual(0, webActions.Items.Count);
                Assert.AreEqual(0, programActions.Items.Count);
                Assert.IsNotNull(owner.SettingDialog.RightClickActionSettingsEditor);
            }
            finally
            {
                owner.SettingDialog.Dispose();
            }
        });
    }

    /// <summary>新規 Web action の空の名前・URL に対して実 editor の Error と対応するリソースの案内が届くことを確認します。</summary>
    [TestMethod]
    public void RightClickPageMarksNewWebActionRequiredFieldsAsErrors()
    {
        TestUiDispatcherHost.Invoke(() =>
        {
            MainWindowViewModel owner = MainWindowViewModelTestFactory.Create(MainWindowViewModelTestFactory.CreateIsolatedSettings(values =>
            {
                values.OperationModeLR2DB = false;
                values.BMSRootPath = Path.GetTempPath();
                values.StandaloneBmsRootPaths = Path.GetTempPath();
                values.BMSInstallDir = Path.GetTempPath();
                values.ScanBmsFilesOnStartup = false;
                values.SkipInitPlaylistLoad = true;
                values.RightClickActionsJson = RightClickActionSettingsDefaults.SerializedJson;
            }));
            var window = new SettingsWindow
            {
                DataContext = owner.SettingDialog,
                PlaybackPanel = owner.PlaybackPanel
            };
            try
            {
                Materialize(window);
                var navigation = (ListBox)window.FindName("settingsNavigation");
                var content = (ContentControl)window.FindName("settingsPageContent");
                navigation.SelectedItem = window.FindName("navigationRightClick");
                Materialize(window);

                owner.SettingDialog.RightClickActionSettingsEditor.AddWebAction();
                Materialize(window);

                var page = (RightClickSettingsPage)content.Content;
                TextBox nameEditor = FindLogicalDescendants<TextBox>(page).Single(textBox =>
                    AutomationProperties.GetAutomationId(textBox) == "RightClickWebName");
                TextBox urlEditor = FindLogicalDescendants<TextBox>(page).Single(textBox =>
                    AutomationProperties.GetAutomationId(textBox) == "RightClickWebUrl");

                Assert.AreEqual(string.Empty, nameEditor.Text);
                Assert.AreEqual("Error", AutomationProperties.GetItemStatus(nameEditor));
                Assert.AreEqual(Resources.RightClick_name_required, AutomationProperties.GetHelpText(nameEditor));
                Assert.AreEqual(string.Empty, urlEditor.Text);
                Assert.AreEqual("Error", AutomationProperties.GetItemStatus(urlEditor));
                Assert.AreEqual(Resources.RightClick_url_required, AutomationProperties.GetHelpText(urlEditor));
            }
            finally
            {
                owner.SettingDialog.Dispose();
            }
        });
    }

    /// <summary>実追加・編集で名前と実行先のエラーを解消し、必須 placeholder 不足の案内を言語変更とページ再表示でも更新・保持し、有効入力で解除することを確認します。</summary>
    [TestMethod]
    public void RightClickPageShowsProgramValidationForAllFieldsAndRefreshesItAfterLanguageChange()
    {
        string previousCulture = Resources.Culture?.Name ?? "ja-JP";
        try
        {
            ResourceService.Current.ChangeCulture("en-US");
            TestUiDispatcherHost.Invoke(() =>
            {
                MainWindowViewModel owner = MainWindowViewModelTestFactory.Create(MainWindowViewModelTestFactory.CreateIsolatedSettings(values =>
            {
                values.OperationModeLR2DB = false;
                values.BMSRootPath = Path.GetTempPath();
                values.StandaloneBmsRootPaths = Path.GetTempPath();
                values.BMSInstallDir = Path.GetTempPath();
                values.ScanBmsFilesOnStartup = false;
                values.SkipInitPlaylistLoad = true;
                values.RightClickActionsJson = "{\"webActions\":[],\"programActions\":[]}";
            }));
                var window = new SettingsWindow
                {
                    DataContext = owner.SettingDialog,
                    PlaybackPanel = owner.PlaybackPanel
                };
                try
                {
                    Materialize(window);
                    var navigation = (ListBox)window.FindName("settingsNavigation");
                    var content = (ContentControl)window.FindName("settingsPageContent");
                    navigation.SelectedItem = window.FindName("navigationRightClick");
                    Materialize(window);

                    var page = (RightClickSettingsPage)content.Content;
                    Button programAddButton = FindLogicalDescendants<Button>(page).Single(button =>
                        AutomationProperties.GetAutomationId(button) == "RightClickProgramAdd");
                    programAddButton.RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent, programAddButton));
                    Materialize(window);
                    page = (RightClickSettingsPage)content.Content;
                    TextBox nameEditor = FindLogicalDescendants<TextBox>(page).Single(textBox =>
                        AutomationProperties.GetAutomationId(textBox) == "RightClickProgramName");
                    TextBox executableEditor = FindLogicalDescendants<TextBox>(page).Single(textBox =>
                        AutomationProperties.GetAutomationId(textBox) == "RightClickProgramExecutable");
                    TextBox argumentsEditor = FindLogicalDescendants<TextBox>(page).Single(textBox =>
                        AutomationProperties.GetAutomationId(textBox) == "RightClickProgramArguments");

                    Assert.AreEqual(Resources.RightClick_name_required, AutomationProperties.GetHelpText(nameEditor));
                    Assert.AreEqual(Resources.RightClick_executable_required, AutomationProperties.GetHelpText(executableEditor));
                    Assert.AreEqual(string.Empty, AutomationProperties.GetHelpText(argumentsEditor));
                    Assert.AreEqual("Error", AutomationProperties.GetItemStatus(nameEditor));
                    Assert.AreEqual("Error", AutomationProperties.GetItemStatus(executableEditor));
                    Assert.IsTrue(executableEditor.IsReadOnly);

                    nameEditor.Text = "Viewer";
                    owner.SettingDialog.RightClickActionSettingsEditor.SelectedProgramAction
                        .SetExecutablePathFromPicker(@"C:\Tools\viewer.exe");
                    argumentsEditor.Text = "--fixed";
                    Materialize(window);
                    Assert.AreEqual(string.Empty, AutomationProperties.GetHelpText(nameEditor));
                    Assert.AreEqual(string.Empty, AutomationProperties.GetHelpText(executableEditor));
                    string expectedMissingPlaceholderMessage = string.Format(
                        CultureInfo.CurrentCulture,
                        Resources.RightClick_arguments_file_path_required,
                        "{filePath}");
                    Assert.AreEqual(expectedMissingPlaceholderMessage, AutomationProperties.GetHelpText(argumentsEditor));
                    string englishArgumentMessage = AutomationProperties.GetHelpText(argumentsEditor);

                    ResourceService.Current.ChangeCulture("ja-JP");
                    Materialize(window);
                    Assert.AreEqual(
                        string.Format(
                            CultureInfo.CurrentCulture,
                            Resources.RightClick_arguments_file_path_required,
                            "{filePath}"),
                        AutomationProperties.GetHelpText(argumentsEditor));
                    Assert.AreNotEqual(englishArgumentMessage, AutomationProperties.GetHelpText(argumentsEditor));

                    navigation.SelectedItem = window.FindName("navigationGeneral");
                    Materialize(window);
                    navigation.SelectedItem = window.FindName("navigationRightClick");
                    Materialize(window);
                    page = (RightClickSettingsPage)content.Content;
                    nameEditor = FindLogicalDescendants<TextBox>(page).Single(textBox =>
                        AutomationProperties.GetAutomationId(textBox) == "RightClickProgramName");
                    executableEditor = FindLogicalDescendants<TextBox>(page).Single(textBox =>
                        AutomationProperties.GetAutomationId(textBox) == "RightClickProgramExecutable");
                    argumentsEditor = FindLogicalDescendants<TextBox>(page).Single(textBox =>
                        AutomationProperties.GetAutomationId(textBox) == "RightClickProgramArguments");

                    Assert.AreEqual("Viewer", nameEditor.Text);
                    Assert.AreEqual(@"C:\Tools\viewer.exe", executableEditor.Text);
                    Assert.AreEqual("--fixed", argumentsEditor.Text);
                    Assert.AreEqual(
                        string.Format(
                            CultureInfo.CurrentCulture,
                            Resources.RightClick_arguments_file_path_required,
                            "{filePath}"),
                        AutomationProperties.GetHelpText(argumentsEditor));
                    Assert.AreEqual("Error", AutomationProperties.GetItemStatus(argumentsEditor));

                    argumentsEditor.Text = "{filePath}";
                    Materialize(window);
                    Assert.AreEqual("{filePath}", argumentsEditor.Text);
                    Assert.AreEqual(string.Empty, AutomationProperties.GetHelpText(argumentsEditor));
                    Assert.AreEqual(string.Empty, AutomationProperties.GetItemStatus(argumentsEditor));
                }
                finally
                {
                    window.CloseForOwnerShutdown();
                    owner.SettingDialog.Dispose();
                }
            });
        }
        finally
        {
            ResourceService.Current.ChangeCulture(previousCulture);
        }
    }

    /// <summary>実スコア使用選択を有効にして不正ルートを拒否し、文化変更が欄・hash 選択肢・通知へ届いて既存 option を再作成しないことを確認します。</summary>
    [TestMethod]
    public void BeatorajaScoreDbControlsUseLocalizedResourcesAndValidation()
    {
        TestUiDispatcherHost.Invoke(() =>
        {
            string previousCulture = Resources.Culture?.Name ?? "ja-JP";
            MainWindowViewModel? owner = null;
            SettingsWindow? window = null;
            try
            {
                ResourceService.Current.ChangeCulture("en-US");
                owner = MainWindowViewModelTestFactory.Create(MainWindowViewModelTestFactory.CreateIsolatedSettings(values =>
            {
                values.OperationModeLR2DB = false;
                values.BMSRootPath = Path.GetTempPath();
                values.StandaloneBmsRootPaths = Path.GetTempPath();
                values.BMSInstallDir = Path.GetTempPath();
                values.ScanBmsFilesOnStartup = false;
                values.SkipInitPlaylistLoad = true;
                values.UseBeatorajaScoreDb = false;
                values.EnableBeatorajaBmtOutput = false;
                values.RightClickActionsJson = RightClickActionSettingsDefaults.SerializedJson;
            }));
                window = new SettingsWindow
                {
                    DataContext = owner.SettingDialog,
                    PlaybackPanel = owner.PlaybackPanel
                };
                Materialize(window);

                ((ListBox)window.FindName("settingsNavigation")).SelectedItem = window.FindName("navigationGeneral");
                Materialize(window);
                var generalPage = (FrameworkElement)((ContentControl)window.FindName("settingsPageContent")).Content;
                var scoreDb = (CheckBox)generalPage.FindName("useBeatorajaScoreDbCheckBox");
                var hashMode = (ComboBox)generalPage.FindName("beatorajaHashOutputModeComboBox");
                var hashModeField = (SettingsField)generalPage.FindName("beatorajaHashOutputModeField");

                scoreDb.GetBindingExpression(ContentControl.ContentProperty)?.UpdateTarget();
                hashMode.GetBindingExpression(ItemsControl.ItemsSourceProperty)?.UpdateTarget();
                hashModeField.GetBindingExpression(HeaderedContentControl.HeaderProperty)?.UpdateTarget();

                Assert.AreEqual(Resources.Use_beatoraja_scoreDB, scoreDb.Content);
                Assert.AreEqual(Resources.Beatoraja_bmt_hash_output_mode, hashModeField.Header);
                Assert.AreEqual(nameof(SettingsDialogViewModel.BeatorajaBmtHashOutputModeOption.DisplayName), hashMode.DisplayMemberPath);
                Assert.AreEqual(nameof(SettingsDialogViewModel.BeatorajaBmtHashOutputModeOption.Key), hashMode.SelectedValuePath);
                CollectionAssert.AreEquivalent(
                    owner.SettingDialog.BeatorajaBmtHashOutputModeOptions.ToArray(),
                    hashMode.Items.Cast<SettingsDialogViewModel.BeatorajaBmtHashOutputModeOption>().ToArray());
                SettingsDialogViewModel.BeatorajaBmtHashOutputModeOption[] englishOptions =
                    hashMode.Items.Cast<SettingsDialogViewModel.BeatorajaBmtHashOutputModeOption>().ToArray();
                AssertHashOptionDisplayNames(englishOptions);
                var displayNameNotifications = new HashSet<SettingsDialogViewModel.BeatorajaBmtHashOutputModeOption>();
                foreach (SettingsDialogViewModel.BeatorajaBmtHashOutputModeOption option in englishOptions)
                {
                    option.PropertyChanged += (_, args) =>
                    {
                        if (args.PropertyName == nameof(SettingsDialogViewModel.BeatorajaBmtHashOutputModeOption.DisplayName))
                        {
                            displayNameNotifications.Add(option);
                        }
                    };
                }

                scoreDb.IsChecked = true;
                Materialize(window);
                Assert.IsFalse(MainWindowViewModelTestFactory.GetComposition(owner).StartupSettingsProvider().UseBeatorajaScoreDb, "スコア候補を選ぶだけでは確定値を変更しません。");

                ResourceService.Current.ChangeCulture("ja-JP");
                Materialize(window);
                scoreDb.GetBindingExpression(ContentControl.ContentProperty)?.UpdateTarget();
                hashModeField.GetBindingExpression(HeaderedContentControl.HeaderProperty)?.UpdateTarget();
                CollectionAssert.AreEquivalent(englishOptions, displayNameNotifications.ToArray());
                Assert.AreEqual(Resources.Use_beatoraja_scoreDB, scoreDb.Content);
                Assert.AreEqual(Resources.Beatoraja_bmt_hash_output_mode, hashModeField.Header);
                SettingsDialogViewModel.BeatorajaBmtHashOutputModeOption[] japaneseOptions =
                    hashMode.Items.Cast<SettingsDialogViewModel.BeatorajaBmtHashOutputModeOption>().ToArray();
                Assert.AreEqual(englishOptions.Length, japaneseOptions.Length);
                for (int index = 0; index < englishOptions.Length; index++)
                {
                    Assert.AreSame(englishOptions[index], japaneseOptions[index]);
                }
                AssertHashOptionDisplayNames(japaneseOptions);

                Materialize(window);
                Assert.IsFalse(MainWindowViewModelTestFactory.GetComposition(owner).StartupSettingsProvider().UseBeatorajaScoreDb, "スコア候補を選ぶだけでは確定値を変更しません。");
            }
            finally
            {
                window?.CloseForOwnerShutdown();
                owner?.SettingDialog.Dispose();
                ResourceService.Current.ChangeCulture(previousCulture);
            }
        });
    }

    /// <summary>一般の言語とプレイリストの URL・補完欄が正しい設定へ Binding され、URL は編集確定時に更新する接続を確認します。</summary>
    [TestMethod]
    public void GeneralAndPlaylistPagesExposeDistinctCompiledBindings()
    {
        TestUiDispatcherHost.Invoke(() =>
        {
            MainWindowViewModel owner = MainWindowViewModelTestFactory.Create(MainWindowViewModelTestFactory.CreateIsolatedSettings(values =>
            {
                values.OperationModeLR2DB = false;
                values.BMSRootPath = Path.GetTempPath();
                values.StandaloneBmsRootPaths = Path.GetTempPath();
                values.BMSInstallDir = Path.GetTempPath();
                values.ScanBmsFilesOnStartup = false;
                values.SkipInitPlaylistLoad = true;
            }));
            var window = new SettingsWindow
            {
                DataContext = owner.SettingDialog,
                PlaybackPanel = owner.PlaybackPanel
            };
            try
            {
                Materialize(window);
                var navigation = (ListBox)window.FindName("settingsNavigation");
                var content = (ContentControl)window.FindName("settingsPageContent");
                navigation.SelectedItem = window.FindName("navigationGeneral");
                Materialize(window);
                Assert.IsInstanceOfType<GeneralSettingsPage>(content.Content);
                Assert.AreSame(owner.SettingDialog, ((FrameworkElement)content.Content).DataContext);
                var language = (ComboBox)((FrameworkElement)content.Content).FindName("languageComboBox");
                Assert.AreEqual(nameof(SettingsDialogViewModel.Languages),
                    GetBindingPath(language, ItemsControl.ItemsSourceProperty));

                navigation.SelectedItem = window.FindName("navigationPlaylist");
                Materialize(window);
                Assert.IsInstanceOfType<PlaylistSettingsPage>(content.Content);
                Assert.AreSame(owner.SettingDialog, ((FrameworkElement)content.Content).DataContext);
                var tableListUrl = (TextBox)((FrameworkElement)content.Content).FindName("tableListUrlTextBox");
                var urlCompletion = (CheckBox)((FrameworkElement)content.Content).FindName("enablePlaylistUrlCompletionCheckBox");
                Binding? tableListUrlBinding = BindingOperations.GetBinding(tableListUrl, TextBox.TextProperty);
                Binding? urlCompletionBinding = BindingOperations.GetBinding(urlCompletion, ToggleButton.IsCheckedProperty);
                Assert.AreEqual(nameof(SettingsDialogViewModel.TableListURL), tableListUrlBinding?.Path?.Path);
                Assert.AreEqual(UpdateSourceTrigger.LostFocus, tableListUrlBinding?.UpdateSourceTrigger);
                Assert.AreEqual(nameof(SettingsDialogViewModel.EnablePlaylistUrlCompletion), urlCompletionBinding?.Path?.Path);
            }
            finally
            {
                owner.SettingDialog.Dispose();
            }
        });
    }

    /// <summary>実 LR2 手動同期と履歴 schema の状態・修復操作がそれぞれの意味を持つ見出しの下に配置され、利用者が操作目的を取り違える配置を検出します。</summary>
    [TestMethod]
    public void GeneralLr2MaintenanceActionsHaveDistinctSemanticSectionOwners()
    {
        TestUiDispatcherHost.Invoke(() =>
        {
            MainWindowViewModel owner = MainWindowViewModelTestFactory.Create();
            var window = new SettingsWindow
            {
                DataContext = owner.SettingDialog,
                PlaybackPanel = owner.PlaybackPanel
            };
            try
            {
                Materialize(window);
                ((ListBox)window.FindName("settingsNavigation")).SelectedItem = window.FindName("navigationGeneral");
                Materialize(window);
                var page = (GeneralSettingsPage)((ContentControl)window.FindName("settingsPageContent")).Content;
                Button resyncButton = FindLogicalDescendants<Button>(page).Single(button =>
                    GetBindingPath(button, ContentControl.ContentProperty) == "Resources.Lr2_song_db_sync_data_resync");
                SettingsStatusBanner schemaBanner = FindLogicalDescendants<SettingsStatusBanner>(page).Single(banner =>
                    GetBindingPath(banner, ContentControl.ContentProperty) == nameof(SettingsDialogViewModel.Lr2PlayHistorySchemaStatusText));
                Button schemaButton = FindLogicalDescendants<Button>(page).Single(button =>
                    GetBindingPath(button, ContentControl.ContentProperty) == nameof(SettingsDialogViewModel.Lr2PlayHistorySchemaInstallOrRepairButtonText));

                SettingsSection? resyncSection = FindNearestSettingsSection(resyncButton);
                SettingsSection? schemaSection = FindNearestSettingsSection(schemaBanner);
                Assert.IsNotNull(resyncSection);
                Assert.IsNotNull(schemaSection);
                Assert.AreNotSame(resyncSection, schemaSection);
                Assert.IsFalse(string.IsNullOrWhiteSpace(resyncSection!.Header?.ToString()));
                Assert.IsFalse(string.IsNullOrWhiteSpace(schemaSection!.Header?.ToString()));
                Assert.AreNotEqual(resyncSection!.Header?.ToString(), schemaSection!.Header?.ToString());
                Assert.AreSame(schemaSection, FindNearestSettingsSection(schemaButton));
                Assert.AreEqual("Resources.Lr2_song_db_sync_data_resync",
                    GetBindingPath(resyncSection!, HeaderedContentControl.HeaderProperty));
                Assert.AreEqual("Resources.Lr2_play_history_schema_label",
                    GetBindingPath(schemaSection!, HeaderedContentControl.HeaderProperty));
            }
            finally
            {
                owner.SettingDialog.Dispose();
            }
        });
    }

    /// <summary>実履歴 preset editor が MD5 対応表と別の見出しに属し、二重見出しや説明の重複を表示しないことを確認します。</summary>
    [TestMethod]
    public void PlayHistoryPresetEditorUsesItsOwnLocalizedSectionOutsideMd5Mapping()
    {
        TestUiDispatcherHost.Invoke(() =>
        {
            MainWindowViewModel owner = MainWindowViewModelTestFactory.Create();
            var window = new SettingsWindow
            {
                DataContext = owner.SettingDialog,
                PlaybackPanel = owner.PlaybackPanel
            };
            try
            {
                Materialize(window);
                var navigation = (ListBox)window.FindName("settingsNavigation");
                var content = (ContentControl)window.FindName("settingsPageContent");
                navigation.SelectedItem = window.FindName("navigationPlaylist");
                Materialize(window);
                var page = (PlaylistSettingsPage)content.Content;
                var presetList = (ListBox)page.FindName("playHistoryPresetList");
                SettingsListEditor presetEditor = FindLogicalDescendants<SettingsListEditor>(page).Single(editor =>
                    FindLogicalDescendants<ListBox>(editor).Any(list => ReferenceEquals(list, presetList)));
                SettingsSection? presetSection = FindNearestSettingsSection(presetEditor);
                SettingsSection md5Section = FindLogicalDescendants<SettingsSection>(page).Single(section =>
                    GetBindingPath(section, HeaderedContentControl.HeaderProperty) == "Resources.Playlist_md5_url_mapping_tsv_uri");

                Assert.IsNotNull(presetSection);
                Assert.AreNotSame(md5Section, presetSection);
                Assert.IsFalse(string.IsNullOrWhiteSpace(presetSection!.Header?.ToString()));
                Assert.AreEqual("Resources.Play_history_folder_display_preset",
                    GetBindingPath(presetSection!, HeaderedContentControl.HeaderProperty));
                Assert.IsTrue(string.IsNullOrWhiteSpace(presetEditor.Header?.ToString()),
                    "The preset section owns the heading; the nested list editor must not duplicate it.");
                Assert.IsTrue(string.IsNullOrWhiteSpace(presetEditor.Description?.ToString()),
                    "The preset section owns the description; the nested list editor must not duplicate it.");
            }
            finally
            {
                owner.SettingDialog.Dispose();
            }
        });
    }

    /// <summary>保存済みルートを実一覧へ表示して追加・選択解除を配送し、言語変更で操作文言を更新しながら対象を保持することを確認します。</summary>
    [TestMethod]
    public void StandaloneBmsRootsUseLocalizedListAddAndRemoveBindings()
    {
        TestUiDispatcherHost.Invoke(() =>
        {
            string previousCulture = Resources.Culture?.Name ?? "ja-JP";
            string scope = Path.Combine(Path.GetTempPath(), "bemusicseeker-settings-roots-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(scope);
            string firstRoot = Path.Combine(scope, "first");
            string secondRoot = Path.Combine(scope, "second");
            string addedRoot = Path.Combine(scope, "added");
            Directory.CreateDirectory(firstRoot);
            Directory.CreateDirectory(secondRoot);
            Directory.CreateDirectory(addedRoot);
            MainWindowViewModel? owner = null;
            try
            {
                ResourceService.Current.ChangeCulture("en-US");
                owner = MainWindowViewModelTestFactory.Create(MainWindowViewModelTestFactory.CreateIsolatedSettings(values =>
            {
                values.OperationModeLR2DB = false;
                values.BMSRootPath = firstRoot;
                values.StandaloneBmsRootPaths = string.Join(Environment.NewLine, firstRoot, secondRoot);
                values.BMSInstallDir = firstRoot;
                values.ScanBmsFilesOnStartup = false;
                values.SkipInitPlaylistLoad = true;
            }));
                var window = new SettingsWindow
                {
                    DataContext = owner.SettingDialog,
                    PlaybackPanel = owner.PlaybackPanel
                };
                Materialize(window);
                ((ListBox)window.FindName("settingsNavigation")).SelectedItem = window.FindName("navigationGeneral");
                Materialize(window);
                var page = (FrameworkElement)((ContentControl)window.FindName("settingsPageContent")).Content;
                var roots = (ListBox)page.FindName("bmsSearchRootPathListBox");
                Assert.AreEqual(2, roots.Items.Count);
                Assert.AreEqual(nameof(SettingsDialogViewModel.AvailableBMSDirectories),
                    roots.GetBindingExpression(ItemsControl.ItemsSourceProperty)?.ParentBinding.Path?.Path);
                var add = (Button)page.FindName("addBmsSearchRootButton");
                var remove = (Button)page.FindName("removeBmsSearchRootButton");
                add.GetBindingExpression(ContentControl.ContentProperty)?.UpdateTarget();
                remove.GetBindingExpression(ContentControl.ContentProperty)?.UpdateTarget();
                Assert.AreEqual(Resources.Add_BMSDirectory, add.Content);
                Assert.AreEqual(Resources.Remove_BMSDirectory, remove.Content);

                owner.SettingDialog.AddBmsSearchRootPathsAsync([addedRoot]).GetAwaiter().GetResult();
                Materialize(window);
                Assert.AreEqual(3, roots.Items.Count);
                Assert.IsTrue(roots.Items.Cast<string>().Any(path => string.Equals(path, addedRoot, StringComparison.OrdinalIgnoreCase)));

                roots.SelectedItem = secondRoot;
                Materialize(window);
                roots.GetBindingExpression(Selector.SelectedItemProperty)?.UpdateSource();
                remove.RaiseEvent(new RoutedEventArgs(Button.ClickEvent, remove));
                TestUiDispatcherHost.AwaitTaskOnDispatcher(owner.SettingDialog.SettingsOperationCompletionTask, "compiled-bms-removal-terminal");
                Materialize(window);
                Assert.IsFalse(roots.Items.Cast<string>().Any(path => string.Equals(path, secondRoot, StringComparison.OrdinalIgnoreCase)));

                ResourceService.Current.ChangeCulture("ja-JP");
                Materialize(window);
                add.GetBindingExpression(ContentControl.ContentProperty)?.UpdateTarget();
                remove.GetBindingExpression(ContentControl.ContentProperty)?.UpdateTarget();
                Assert.AreEqual(Resources.Add_BMSDirectory, add.Content);
                Assert.AreEqual(Resources.Remove_BMSDirectory, remove.Content);
            }
            finally
            {
                owner?.SettingDialog.Dispose();
                ResourceService.Current.ChangeCulture(previousCulture);
                Directory.Delete(scope, recursive: true);
            }
        });
    }

    /// <summary>実backup/restoreとschema・撤去・明示音声テストは単一操作中に無効表示と入口拒否でpicker・確認・通知を始めません。録音の独立保存とページ移動を維持し、終端後のbackup/restoreだけ既存の所有pickerへ一回配送します。</summary>
    [TestMethod]
    public void BackupPageCompiledActionsUseSettingsOwnerForSaveAndRestore()
    {
        TestUiDispatcherHost.Invoke(() =>
        {
            MainWindowViewModel? owner = null;
            SettingsWindow? window = null;
            var dialogs = new PlaylistWorkspaceTestPorts.PlaylistWorkspaceDialogService
            {
                ConfirmationResult = UiDialogResult.FromMessageBoxResult(MessageBoxResult.No)
            };
            dialogs.SaveFilePickerResults.Enqueue(new UiSaveFilePickerResult(UiDialogStatus.CancelledByUser));

            try
            {
                owner = MainWindowViewModelTestFactory.Create();
                window = new SettingsWindow(dialogs)
                {
                    DataContext = owner.SettingDialog,
                    PlaybackPanel = owner.PlaybackPanel,
                    PlaylistWorkspace = owner.PlaylistWorkspace
                };
                Materialize(window);

                var navigation = (ListBox)window.FindName("settingsNavigation");
                var content = (ContentControl)window.FindName("settingsPageContent");

                navigation.SelectedItem = window.FindName("navigationBackup");
                Materialize(window);
                var backupPage = (BackupSettingsPage)content.Content;
                var backupButtons = FindLogicalDescendants<Button>(backupPage).ToList();
                Button backupButton = backupButtons.Single(button =>
                    GetBindingPath(button, ContentControl.ContentProperty) == "Resources.Backup_playlist");
                Button restoreButton = backupButtons.Single(button =>
                    GetBindingPath(button, ContentControl.ContentProperty) == "Resources.Backup_restore_playlist");
                var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
                Task operation = owner.SettingDialog.RunSettingsOperationAsync(() => release.Task);
                Task terminal = owner.SettingDialog.SettingsOperationCompletionTask;
                try
                {
                    TestUiDispatcherHost.ProcessQueuedPresentation();
                    Assert.IsFalse(backupButton.IsEnabled);
                    Assert.IsFalse(restoreButton.IsEnabled);
                    backupButton.RaiseEvent(new RoutedEventArgs(Button.ClickEvent, backupButton));
                    restoreButton.RaiseEvent(new RoutedEventArgs(Button.ClickEvent, restoreButton));
                    TestUiDispatcherHost.AwaitTaskOnDispatcher(window.HandleInstallOrRepairLr2PlayHistorySchemaAsync(), "busy-schema-install");
                    TestUiDispatcherHost.AwaitTaskOnDispatcher(window.HandleUninstallLr2PlayHistorySchemaAsync(), "busy-schema-remove");
                    TestUiDispatcherHost.AwaitTaskOnDispatcher(window.HandleApplicationDataUninstallAsync(), "busy-application-uninstall");
                    TestUiDispatcherHost.AwaitTaskOnDispatcher(owner.SettingDialog.RunAudioDeviceTestAsync(), "busy-audio-test");
                    Assert.IsFalse(owner.SettingDialog.IsAudioDeviceTestInProgress);
                    Assert.IsFalse(owner.SettingDialog.IsAudioDeviceTestAvailable);
                    navigation.SelectedItem = window.FindName("navigationAdvanced");
                    Materialize(window);
                    var advanced = (AdvancedSettingsPage)content.Content;
                    Button[] dangerButtons = FindLogicalDescendants<Button>(advanced).Where(button =>
                        GetBindingPath(button, ContentControl.ContentProperty) is "Resources.Settings_uninstall_application_data" or "Resources.Lr2_play_history_schema_uninstall")
                        .ToArray();
                    Assert.AreEqual(2, dangerButtons.Length);
                    Assert.IsTrue(dangerButtons.All(button => !button.IsEnabled));
                    float quality = owner.SettingDialog.EncoderQuality == 0.5f ? 0.6f : 0.5f;
                    owner.SettingDialog.EncoderQuality = quality;
                    Assert.AreEqual(quality, owner.SettingDialog.EncoderQuality);
                    Assert.AreSame(terminal, owner.SettingDialog.SettingsOperationCompletionTask);
                    Assert.IsFalse(terminal.IsCompleted);
                    Assert.AreEqual(0, dialogs.SaveFilePickerRequests.Count);
                    Assert.IsNull(dialogs.LastConfirmationRequest);
                    navigation.SelectedItem = window.FindName("navigationBackup");
                    Materialize(window);
                }
                finally
                {
                    release.TrySetResult();
                    TestUiDispatcherHost.AwaitTaskOnDispatcher(operation, "compiled-busy-operation-terminal");
                }
                Assert.IsTrue(backupButton.IsEnabled);
                Assert.IsTrue(restoreButton.IsEnabled);
                backupButton.RaiseEvent(new RoutedEventArgs(Button.ClickEvent, backupButton));
                restoreButton.RaiseEvent(new RoutedEventArgs(Button.ClickEvent, restoreButton));
                TestUiDispatcherHost.ProcessQueuedPresentation();

                Assert.AreEqual(1, dialogs.SaveFilePickerRequests.Count);
                UiSaveFilePickerRequest saveRequest = dialogs.SaveFilePickerRequests.Single();
                Assert.AreSame(window, saveRequest.Owner);
                Assert.AreEqual("BeMusicSeeker_backup.sql", saveRequest.FileName);
                Assert.AreEqual(".sql", saveRequest.DefaultExtension);
                Assert.AreEqual(BeMusicSeeker.Properties.Resources.Sql_file_exts, saveRequest.Filter);
                Assert.IsTrue(saveRequest.AddExtension);
                Assert.AreSame(window, dialogs.LastConfirmationRequest.Owner);

            }
            finally
            {
                if (window?.IsVisible == true)
                {
                    window.CloseForOwnerShutdown();
                }
                owner?.SettingDialog.Dispose();
            }
        });
    }

    private static void Materialize(SettingsWindow window)
    {
        window.Measure(new Size(820, 760));
        window.Arrange(new Rect(0, 0, 820, 760));
        window.UpdateLayout();
        TestUiDispatcherHost.ProcessQueuedPresentation();
    }

    private static string? GetBindingPath(DependencyObject target, DependencyProperty property)
    {
        return BindingOperations.GetBinding(target, property)?.Path?.Path
            ?? BindingOperations.GetBindingExpression(target, property)?.ParentBinding.Path?.Path;
    }

    private static SettingsSection? FindNearestSettingsSection(DependencyObject element)
    {
        for (DependencyObject current = element; current != null;)
        {
            if (current is SettingsSection section)
            {
                return section;
            }

            DependencyObject? parent = current is Visual or System.Windows.Media.Media3D.Visual3D
                ? VisualTreeHelper.GetParent(current)
                : null;
            current = parent ?? LogicalTreeHelper.GetParent(current);
        }

        return null;
    }

    private static void EnsureCanonicalApplicationResources()
    {
        Application application = Application.Current;
        Assert.IsNotNull(application);
        AddCanonicalResourceIfMissing(
            application,
            "/BeMusicSeeker;component/Themes/CanonicalDialogStyles.xaml");
    }

    private static void AddCanonicalResourceIfMissing(Application application, string source)
    {
        if (application.Resources.MergedDictionaries.Any(dictionary =>
            string.Equals(dictionary.Source?.OriginalString, source, StringComparison.OrdinalIgnoreCase)))
        {
            return;
        }

        application.Resources.MergedDictionaries.Add(new ResourceDictionary
        {
            Source = new Uri(source, UriKind.RelativeOrAbsolute)
        });
    }

    private static void AssertHashOptionDisplayNames(
        IReadOnlyList<SettingsDialogViewModel.BeatorajaBmtHashOutputModeOption> options)
    {
        CollectionAssert.AreEqual(
            new[]
            {
                Resources.Beatoraja_bmt_hash_output_original,
                Resources.Beatoraja_bmt_hash_output_fill_missing,
                Resources.Beatoraja_bmt_hash_output_prefer_sha256_only
            },
            options.Select(option => option.DisplayName).ToArray());
    }

    private static IEnumerable<T> FindLogicalDescendants<T>(DependencyObject root)
        where T : DependencyObject
    {
        if (root is T match)
        {
            yield return match;
        }

        foreach (object? child in LogicalTreeHelper.GetChildren(root))
        {
            if (child is not DependencyObject dependencyObject)
            {
                continue;
            }

            foreach (T descendant in FindLogicalDescendants<T>(dependencyObject))
            {
                yield return descendant;
            }
        }
    }

    /// <summary>保存判断と媒体処理を実providerへ通したまま、UI初期接続による保存要求だけを数えます。</summary>
    private sealed class CountingSettingsProvider : System.Configuration.SettingsProvider
    {
        private readonly System.Configuration.SettingsProvider inner;
        internal CountingSettingsProvider(System.Configuration.SettingsProvider inner)
        {
            this.inner = inner;
            Initialize(nameof(CountingSettingsProvider), new System.Collections.Specialized.NameValueCollection());
        }
        internal int SetRequests { get; private set; }
        public override string ApplicationName { get => inner.ApplicationName; set => inner.ApplicationName = value; }
        public override System.Configuration.SettingsPropertyValueCollection GetPropertyValues(System.Configuration.SettingsContext context, System.Configuration.SettingsPropertyCollection properties)
            => inner.GetPropertyValues(context, properties);
        public override void SetPropertyValues(System.Configuration.SettingsContext context, System.Configuration.SettingsPropertyValueCollection values)
        {
            SetRequests++;
            inner.SetPropertyValues(context, values);
        }
    }
}
