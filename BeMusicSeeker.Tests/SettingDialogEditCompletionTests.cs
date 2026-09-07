using System;
using System.Collections.ObjectModel;
using System.Collections.Generic;
using System.ComponentModel;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;
using BeMusicSeeker.Models;
using BeMusicSeeker.Models.Utils;
using BeMusicSeeker.Properties;
using BeMusicSeeker.ViewModels;
using BeMusicSeeker.Views;
using BeMusicSeeker.Views.Dialogs;
using BeMusicSeeker.Views.Settings;
using Livet;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Data;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using System.Xml.Linq;
using BeMusicSeeker.Models.BmsLibraryInternal;
using ManagedBass;
using Ribbit.Media;
using Ribbit.Media.Audio;

namespace BeMusicSeeker.Tests;

[TestClass]
[DoNotParallelize]
public sealed class SettingDialogEditCompletionTests
{
    public TestContext TestContext { get; set; } = null!;

    [TestMethod]
    public void DisposedSettingsDialogStopsListeningToResourceServiceCultureChanges()
    {
        TestUiDispatcherHost.RunWindowTest(_ =>
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

    [TestMethod]
    public void PlaylistDialogs_UsePlaylistWorkspaceOwnerComposition()
    {
        TestUiDispatcherHost.RunWindowTest(windowTest =>
        {
            MainWindowViewModel viewModel = MainWindowViewModelTestFactory.Create();
            var settingDialog = new SettingsWindow
            {
                DataContext = viewModel.SettingDialog,
                PlaylistWorkspace = viewModel.PlaylistWorkspace
            };
            var uriDialog = new LoadPlaylistURIDialog
            {
                DataContext = viewModel.PlaylistWorkspace
            };

            Assert.AreSame(viewModel.PlaylistWorkspace, settingDialog.PlaylistWorkspace);
            Assert.AreSame(viewModel.PlaylistWorkspace, uriDialog.DataContext);
        });
    }

    [TestMethod]
    public void SettingDialogVolumeBinding_UsesComposedPlaybackOwner()
    {
        TestUiDispatcherHost.RunWindowTest(windowTest =>
        {
            string root = CreateTemporaryRoot();
            try
            {
                var settingsSession = new CountingSettingsEditSession(CreateValidStandaloneSettings(root));
                var player = new RecordingPlaybackPlayer();
                MainWindowViewModel viewModel = new ApplicationComposition(
                        settingsEditSession: settingsSession,
                        defaultBmsPlayerFactory: () => player,
                        uiScheduler: new WpfUiScheduler(() => Dispatcher.CurrentDispatcher), applicationLifetime: TestApplicationContext.CreateLifetime(), cultureCatalog: TestApplicationContext.CreateCultureCatalog())
                    .CreateMainWindowViewModel();
                var settingDialog = new SettingsWindow
                {
                    DataContext = viewModel.SettingDialog,
                    PlaybackPanel = viewModel.PlaybackPanel
                };
                ((ListBox)settingDialog.FindName("settingsNavigation")).SelectedIndex = 3;
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
                Directory.Delete(root, recursive: true);
            }
        });
    }

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
                SettingsDialogViewModel dialog = CreateOperationModeDialog(settingsSession, dialogs, lifetime);
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
                SettingsDialogViewModel dialog = CreateOperationModeDialog(settingsSession, dialogs, lifetime);
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
            var runtime = new RecordingSearchRootRuntimePort(sequence);
            var dialog = new SettingsDialogViewModel(
                new TestSettingsDialogStatePort(
                    owner,
                    () => Task.FromResult(true),
                    reloadFileDiff: () =>
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
            var dialog = new SettingsDialogViewModel(
                new TestSettingsDialogStatePort(owner, () => Task.FromResult(true)),
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
            var dialog = new SettingsDialogViewModel(
                new TestSettingsDialogStatePort(owner, () => Task.FromResult(true)),
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
            var dialog = new SettingsDialogViewModel(
                new TestSettingsDialogStatePort(owner, () => Task.FromResult(true)),
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
            var dialog = new SettingsDialogViewModel(
                new TestSettingsDialogStatePort(
                    owner,
                    () => Task.FromResult(true),
                    reloadFileDiff: () =>
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
            CollectionAssert.DoesNotContain(
                new BeMusicSeeker.Models.LR2.LR2Config(configPath).GetBMSSearchDirectories().ToArray(),
                bmsRoot);
            Assert.AreEqual(1, reloadCount);
            CollectionAssert.AreEqual(new[] { "query", "apply", "reload" }, sequence);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
            Directory.Delete(installRoot, recursive: true);
        }
    }

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
            var dialog = new SettingsDialogViewModel(
                new TestSettingsDialogStatePort(owner, () => Task.FromResult(true)),
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
            var config = new BeMusicSeeker.Models.LR2.LR2Config(configPath);
            config.AddBMSSearchDirectories([bmsRoot, otherRoot]);
            config.Save(configPath);
            var dialog = new SettingsDialogViewModel(
                new TestSettingsDialogStatePort(owner, () => Task.FromResult(true)),
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
            SettingsDialogViewModel dialog = CreateOperationModeDialog(
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
            SettingsDialogViewModel dialog = viewModel.SettingDialog;
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
            SettingsDialogViewModel dialog = viewModel.SettingDialog;
            dialog.AttachPresentationPort(new RecordingSettingsDialogPresentationPort(sequence.Add));
            dialog.uBMplayPath = playerPath;
            dialog.UsePlayeruBMplay = true;

            await dialog.ApplySettingsAsync();

            CollectionAssert.AreEqual(
                new[] { "save", "factory-configured", "apply", "notify", "close" },
                sequence);
            Assert.AreEqual(1, settingsSession.SaveCount);
            Assert.IsNotNull(runtime.LastReplacementPlayer);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

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
            SettingsDialogViewModel dialog = viewModel.SettingDialog;
            var presentation = new RecordingSettingsDialogPresentationPort(sequence.Add);
            dialog.AttachPresentationPort(presentation);
            dialog.uBMplayPath = playerPath;
            dialog.UsePlayeruBMplay = true;

            await dialog.ApplySettingsAsync();

            CollectionAssert.AreEqual(new[] { "save", "factory-configured" }, sequence);
            Assert.IsNotNull(reportedFailure);
            StringAssert.Contains(reportedFailure!.Message, "configured player failure");
            Assert.AreEqual(0, runtime.ApplyCount);
            Assert.AreEqual(0, runtime.NotifyCount);
            CollectionAssert.DoesNotContain(presentation.Requests, "close");
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [TestMethod]
    public async Task ApplySettingsAsync_StandaloneTransitionUsesDefaultPlayerFactory()
    {
        string root = CreateTemporaryRoot();
        string configPath = Path.Combine(root, "lr2config.xml");
        File.WriteAllText(configPath, "<config><system /><jukebox /></config>");
        try
        {
            Settings settings = CreateValidStandaloneSettings(root);
            settings.OperationModeLR2DB = true;
            settings.LR2RootPath = root;
            settings.LR2ConfigXmlPath = configPath;
            var settingsSession = new CountingSettingsEditSession(settings);
            var sequence = new List<string>();
            settingsSession.SaveObserved = () => sequence.Add("save");
            var factory = new TestSettingsDialogPlayerFactoryPort(sequence);
            var runtime = new TestSettingsDialogPlaybackRuntimePort(sequence);
            MainWindowViewModel viewModel = CreateViewModel(
                settingsSession,
                firstStartup: false,
                initializeOwner: _ => Task.FromResult(true),
                playerFactoryPort: factory,
                playbackRuntimePort: runtime);
            SetActiveLibraryProfile(viewModel, false);
            SettingsDialogViewModel dialog = viewModel.SettingDialog;
            dialog.OperationModeLR2DB = false;
            SetActiveLibraryProfile(viewModel, true);
            AttachPlaylistTables(viewModel, root);
            dialog.AttachPresentationPort(new RecordingSettingsDialogPresentationPort(sequence.Add));

            await dialog.ApplySettingsAsync();

            CollectionAssert.AreEqual(
                new[] { "save", "factory-default", "apply", "notify", "close" },
                sequence);
            Assert.IsFalse(settingsSession.Values.OperationModeLR2DB);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

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
            var workspace = new ComposedSettingsDialogWorkspacePort(settingsSession.Values);
            var state = new ActiveSettingsDialogStatePort();
            SettingsDialogViewModel dialog = composition.CreateSettingDialogViewModel(
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
                new[] { "save", "factory-configured", "apply", "notify", "close" },
                sequence);
            Assert.IsNotNull(factory.LastConfiguredSettings);
            Assert.IsFalse(factory.LastConfiguredSettings!.OperationModeLR2DB);
            Assert.IsTrue(factory.LastConfiguredSettings.UsePlayerLR2body);
            Assert.AreEqual(1, runtime.ApplyCount);

            dialog.Dispose();
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

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
            SettingsDialogViewModel dialog = viewModel.SettingDialog;
            dialog.AttachPresentationPort(new RecordingSettingsDialogPresentationPort(sequence.Add));
            dialog.OverwritePlaylistUrlsWithCompletion = !dialog.OverwritePlaylistUrlsWithCompletion;

            await dialog.ApplySettingsAsync();

            CollectionAssert.AreEqual(new[] { "save", "notify", "close" }, sequence);
            Assert.AreEqual(1, runtime.NotifyCount);
            Assert.AreEqual(0, runtime.ApplyCount);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [TestMethod]
    public async Task ApplySettingsAsync_PublishesPlaylistBackgroundRequestsThroughWorkspace()
    {
        string root = CreateTemporaryRoot();
        try
        {
            var settings = CreateValidStandaloneSettings(root);
            settings.RegisterBeatorajaBmtUrls = false;
            var settingsSession = new CountingSettingsEditSession(settings);
            var scheduled = new List<string>();
            var runtime = new TestSettingsDialogPlaybackRuntimePort();
            MainWindowViewModel viewModel = CreateViewModel(
                settingsSession,
                firstStartup: false,
                playbackRuntimePort: runtime);
            SetActiveLibraryProfile(viewModel, true);
            AttachPlaylistTables(viewModel, root, scheduled);
            SettingsDialogViewModel dialog = viewModel.SettingDialog;
            dialog.OverwritePlaylistUrlsWithCompletion = !dialog.OverwritePlaylistUrlsWithCompletion;
            dialog.RegisterBeatorajaBmtUrls = !dialog.RegisterBeatorajaBmtUrls;

            await dialog.ApplySettingsAsync();

            CollectionAssert.AreEqual(
                new[]
                {
                    "playlist_url_completion:SettingDialog.SaveSettings",
                    "beatoraja_bmt_export_all:SettingDialog.SaveSettings"
                },
                scheduled);
            Assert.AreEqual(1, runtime.NotifyCount);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [TestMethod]
    public async Task ApplySettingsAsync_ActiveProfileWithoutChanges_PublishesSingleCloseRequest()
    {
        string root = CreateTemporaryRoot();
        try
        {
            var settingsSession = new CountingSettingsEditSession(CreateValidStandaloneSettings(root));
            MainWindowViewModel viewModel = CreateViewModel(settingsSession, firstStartup: false);
            SetActiveLibraryProfile(viewModel, true);
            SettingsDialogViewModel dialog = viewModel.SettingDialog;
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

    [TestMethod]
    public async Task AudioDeviceTestBusy_BlocksEditCompletionAndCancellation()
    {
        string root = CreateTemporaryRoot();
        try
        {
            var settingsSession = new CountingSettingsEditSession(CreateValidStandaloneSettings(root));
            MainWindowViewModel viewModel = CreateViewModel(settingsSession, firstStartup: false);
            SetActiveLibraryProfile(viewModel, true);
            using var runtimeStarted = new ManualResetEventSlim();
            using var releaseRuntime = new ManualResetEventSlim();
            var workflow = new AudioDeviceTestWorkflowOwner(
                new TestAudioDeviceTestPlaybackPort(),
                new BlockingAudioDeviceTestRuntime(runtimeStarted, releaseRuntime));
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

            Task testTask = dialog.RunAudioDeviceTestAsync();

            Assert.IsTrue(runtimeStarted.Wait(TimeSpan.FromSeconds(5)));
            Assert.IsFalse(dialog.IsEditCompletionEnabled);
            Assert.IsFalse(dialog.IsEditCancellationEnabled);

            await dialog.ApplySettingsAsync();
            dialog.CancelCommand.Execute();
            Assert.AreEqual(0, settingsSession.SaveCount);
            CollectionAssert.DoesNotContain(
                presentation.Requests,
                "close");

            releaseRuntime.Set();
            await testTask;
            Assert.IsTrue(dialog.IsEditCompletionEnabled);
            Assert.IsTrue(dialog.IsEditCancellationEnabled);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [TestMethod]
    public async Task AudioDeviceTest_NoStreamProgress_DoesNotChangeEditedAudioValues()
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
            var workflow = new AudioDeviceTestWorkflowOwner(
                new TestAudioDeviceTestPlaybackPort(),
                new DelegateAudioDeviceTestRuntime(request =>
                    AudioDeviceTestResultFactory.CreateSuccessful(
                        request,
                        actualDeviceName: "Changed name",
                        latency: 21,
                        streamProgressSucceeded: false)));
            SettingsDialogViewModel dialog = CreateAudioDeviceTestDialog(
                viewModel,
                settingsSession,
                workflow,
                audioGateway);

            await dialog.RunAudioDeviceTestAsync();

            AssertExplicitAudioSettingsUnchanged(settings, audioGateway);
            Assert.AreEqual(0d, dialog.PlayerLatency);
            StringAssert.Contains(
                dialog.AudioDeviceTestStatusMessage,
                Resources.AudioDeviceTestStreamProgressFailureReason);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

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
                    AudioDriver.WasapiShared,
                    settings.PlayerDevice,
                    settings.PlayerDeviceName)
            };
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
                        fallbackReason: "fallbackDestination=WASAPI_SHARED")));
            SettingsDialogViewModel dialog = CreateAudioDeviceTestDialog(
                viewModel,
                settingsSession,
                workflow,
                audioGateway);

            await dialog.RunAudioDeviceTestAsync();

            AssertExplicitAudioSettingsUnchanged(settings, audioGateway);
            Assert.AreEqual(0d, dialog.PlayerLatency);
            StringAssert.Contains(dialog.AudioDeviceTestStatusMessage, "WASAPI");
            StringAssert.Contains(dialog.AudioDeviceTestStatusMessage, Resources.Shared);
            StringAssert.Contains(dialog.AudioDeviceTestStatusMessage, Resources.AudioDeviceTestFallbackReason);
            Assert.IsFalse(dialog.AudioDeviceTestStatusMessage.Contains("fallbackDestination=WASAPI_SHARED", StringComparison.Ordinal));
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

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
            SettingsDialogViewModel dialog = CreateAudioDeviceTestDialog(
                viewModel,
                settingsSession,
                workflow,
                audioGateway);

            await dialog.RunAudioDeviceTestAsync();

            Assert.IsNull(settings.PlayerDevice);
            Assert.IsNull(settings.PlayerDeviceName);
            Assert.AreEqual(SampleRate.AUTO, settings.PlayerSampleRate);
            Assert.AreEqual(SampleFormat.AUTO, settings.PlayerFormat);
            Assert.AreEqual(17d, dialog.PlayerLatency);
            StringAssert.Contains(dialog.AudioDeviceTestStatusMessage, "WASAPI");
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

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
                    AudioDriver.Asio,
                    settings.PlayerDevice,
                    settings.PlayerDeviceName)
            };
            var dialogs = new RecordingRootDialogService();
            var workflow = new AudioDeviceTestWorkflowOwner(
                new TestAudioDeviceTestPlaybackPort(),
                new DelegateAudioDeviceTestRuntime(_ => throw new AudioInitializationException(
                    BassAudioPlayer.DeviceDriver.ASIO,
                    BassAudioPlayer.DeviceDriver.ASIO,
                    "BASS_ASIO_Init",
                    new BassAudioPlayer.DeviceDescriptor("Requested device", "requested-device"),
                    default,
                    "BASSASIO",
                    Errors.Device,
                    "ASIO initialization failed")));
            SettingsDialogViewModel dialog = CreateAudioDeviceTestDialog(
                viewModel,
                settingsSession,
                workflow,
                audioGateway,
                dialogs);

            await dialog.RunAudioDeviceTestAsync();

            Assert.AreEqual(1, dialogs.MessageCount);
            StringAssert.Contains(dialogs.LastMessageText, "ASIO");
            StringAssert.Contains(dialogs.LastMessageText, "BASS_ASIO_Init");
            StringAssert.Contains(dialogs.LastMessageText, "BASS_ERROR_DEVICE");
            Assert.AreEqual(dialogs.LastMessageText, dialog.AudioDeviceTestStatusMessage);
            AssertExplicitAudioSettingsUnchanged(settings, audioGateway, AudioDriver.Asio);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

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
            SettingsDialogViewModel dialog = CreateAudioDeviceTestDialog(
                viewModel,
                settingsSession,
                workflow,
                audioGateway,
                dialogs);

            await dialog.RunAudioDeviceTestAsync();

            StringAssert.Contains(dialog.AudioDeviceTestStatusMessage, "MixerAttach");
            StringAssert.Contains(dialog.AudioDeviceTestStatusMessage, "BASS_Mixer_StreamAddChannel");
            StringAssert.Contains(dialog.AudioDeviceTestStatusMessage, "BASS_ERROR_HANDLE");
            Assert.AreEqual(0, dialogs.MessageCount);
            AssertExplicitAudioSettingsUnchanged(settings, audioGateway);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

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
            SettingsDialogViewModel dialog = CreateAudioDeviceTestDialog(
                viewModel,
                settingsSession,
                workflow,
                audioGateway,
                dialogs);

            await dialog.RunAudioDeviceTestAsync();

            StringAssert.Contains(dialog.AudioDeviceTestStatusMessage, "SourceCreate");
            StringAssert.Contains(dialog.AudioDeviceTestStatusMessage, "BASS_StreamCreateFile");
            StringAssert.Contains(dialog.AudioDeviceTestStatusMessage, "BASS_ERROR_FILEOPEN");
            Assert.AreEqual(0, dialogs.MessageCount);
            AssertExplicitAudioSettingsUnchanged(settings, audioGateway);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [TestMethod]
    public async Task AudioDeviceTest_UnexpectedRuntimeFailureIsLoggedAndShownLocally()
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
            SettingsDialogViewModel dialog = CreateAudioDeviceTestDialog(
                viewModel,
                settingsSession,
                workflow,
                audioGateway,
                dialogs);

            await dialog.RunAudioDeviceTestAsync();

            Assert.AreEqual(Resources.AudioDeviceTestUnexpectedFailureReason, dialog.AudioDeviceTestStatusMessage);
            Assert.AreEqual(1, dialogs.MessageCount);
            Assert.AreEqual(dialog.AudioDeviceTestStatusMessage, dialogs.LastMessageText);
            AssertExplicitAudioSettingsUnchanged(settings, audioGateway);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [TestMethod]
    public async Task AudioDeviceTest_RequestChangedWhileRunning_DoesNotApplyStaleResult()
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
            using var runtimeStarted = new ManualResetEventSlim();
            using var releaseRuntime = new ManualResetEventSlim();
            var workflow = new AudioDeviceTestWorkflowOwner(
                new TestAudioDeviceTestPlaybackPort(),
                new DelegateAudioDeviceTestRuntime(request =>
                {
                    runtimeStarted.Set();
                    releaseRuntime.Wait();
                    return AudioDeviceTestResultFactory.CreateSuccessful(
                        request,
                        actualDeviceName: "Stale result name",
                        latency: 25);
                }));
            SettingsDialogViewModel dialog = CreateAudioDeviceTestDialog(
                viewModel,
                settingsSession,
                workflow,
                audioGateway);

            Task testTask = dialog.RunAudioDeviceTestAsync();
            Assert.IsTrue(runtimeStarted.Wait(TimeSpan.FromSeconds(5)));
            dialog.PlayerDriverIndex = AudioDriverPolicy.IndexOf(AudioDriver.WasapiExclusive);
            releaseRuntime.Set();
            await testTask;

            Assert.AreEqual("Requested device", settings.PlayerDeviceName);
            Assert.AreEqual(AudioDriverPolicy.IndexOf(AudioDriver.WasapiExclusive), dialog.PlayerDriverIndex);
            Assert.AreEqual(0d, dialog.PlayerLatency);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

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
            dialog.PropertyChanged += (_, args) => changedProperties.Add(args.PropertyName);

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

    [TestMethod]
    public void SettingDialogOkClick_AwaitsOwnerCompletionBeforeClosing()
    {
        TestUiDispatcherHost.RunWindowTest(windowTest =>
        {
            Dispatcher previousDispatcher = DispatcherHelper.UIDispatcher;
            SynchronizationContext previousSynchronizationContext = SynchronizationContext.Current;
            Dispatcher dispatcher = Dispatcher.CurrentDispatcher;
            DispatcherHelper.UIDispatcher = dispatcher;
            SynchronizationContext.SetSynchronizationContext(new DispatcherSynchronizationContext(dispatcher));
            string root = CreateTemporaryRoot();
            try
            {
                var settingsSession = new CountingSettingsEditSession(CreateValidStandaloneSettings(root));
                using var reloadStarted = new ManualResetEventSlim();
                var reloadRelease = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
                MainWindowViewModel viewModel = CreateViewModel(
                    settingsSession,
                    firstStartup: false,
                    reloadScoresOnly: _ =>
                    {
                        reloadStarted.Set();
                        return reloadRelease.Task;
                    });
                SetActiveLibraryProfile(viewModel, true);
                SettingsDialogViewModel settingDialogViewModel = viewModel.SettingDialog;
                settingDialogViewModel.BeatorajaPlayerId = "player2";
                var settingDialog = new SettingsWindow
                {
                    DataContext = settingDialogViewModel
                };
                Button button = (Button)settingDialog.FindName("buttonOK")!;
                using var closeRequestObserved = new ManualResetEventSlim();
                DispatcherFrame? frame = null;
                settingDialogViewModel.AttachPresentationPort(new RecordingSettingsDialogPresentationPort(request =>
                {
                    if (request == "close")
                    {
                        closeRequestObserved.Set();
                        if (frame != null)
                        {
                            frame.Continue = false;
                        }
                    }
                }));

                button.RaiseEvent(new RoutedEventArgs(Button.ClickEvent, button));

                Assert.IsTrue(reloadStarted.Wait(TimeSpan.FromSeconds(5)));
                Assert.IsTrue(settingDialogViewModel.IsEditCompletionInProgress);
                Assert.IsFalse(closeRequestObserved.IsSet);

                reloadRelease.SetResult(true);
                frame = new DispatcherFrame();
                var timeoutTimer = new DispatcherTimer(TimeSpan.FromSeconds(5), DispatcherPriority.ApplicationIdle, (_, _) => frame.Continue = false, dispatcher);
                timeoutTimer.Start();
                Dispatcher.PushFrame(frame);
                timeoutTimer.Stop();
                Assert.IsTrue(closeRequestObserved.IsSet);
                Assert.IsFalse(settingDialogViewModel.IsEditCompletionInProgress);
            }
            finally
            {
                Directory.Delete(root, recursive: true);
                DispatcherHelper.UIDispatcher = previousDispatcher;
                SynchronizationContext.SetSynchronizationContext(previousSynchronizationContext);
            }
        });
    }

    [TestMethod]
    public async Task ApplySettingsAsync_ScoreSourceChange_AwaitsReloadBeforeClosing()
    {
        string root = CreateTemporaryRoot();
        try
        {
            var settingsSession = new CountingSettingsEditSession(CreateValidStandaloneSettings(root));
            var sequence = new List<string>();
            settingsSession.SaveObserved = () => sequence.Add("save");
            using var reloadStarted = new ManualResetEventSlim();
            var reloadRelease = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            MainWindowViewModel viewModel = CreateViewModel(
                settingsSession,
                firstStartup: false,
                reloadScoresOnly: _ =>
                {
                    sequence.Add("reload-start");
                    reloadStarted.Set();
                    return reloadRelease.Task.ContinueWith(
                        _ => sequence.Add("reload-completed"),
                        CancellationToken.None,
                        TaskContinuationOptions.ExecuteSynchronously,
                        TaskScheduler.Default);
                });
            SetActiveLibraryProfile(viewModel, true);
            SettingsDialogViewModel dialog = viewModel.SettingDialog;
            dialog.AttachPresentationPort(new RecordingSettingsDialogPresentationPort(sequence.Add));
            dialog.BeatorajaPlayerId = "player2";

            Task applyTask = dialog.ApplySettingsAsync();
            reloadStarted.Wait();
            Assert.IsTrue(dialog.IsEditCompletionInProgress);
            Assert.IsFalse(applyTask.IsCompleted);
            CollectionAssert.DoesNotContain(sequence, "close");

            reloadRelease.SetResult(true);
            await applyTask;

            Assert.IsTrue(sequence.Count >= 4, string.Join("|", sequence));
            Assert.AreEqual("save", sequence[0]);
            Assert.AreEqual("reload-start", sequence[1]);
            Assert.AreEqual("reload-completed", sequence[2]);
            Assert.AreEqual("close", sequence[3]);
            Assert.AreEqual(1, settingsSession.SaveCount);
            Assert.IsTrue(dialog.IsEditCompletionEnabled);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [TestMethod]
    public async Task ApplySettingsAsync_ScoreReloadFailureKeepsChangesForRetry()
    {
        string root = CreateTemporaryRoot();
        try
        {
            var settingsSession = new CountingSettingsEditSession(CreateValidStandaloneSettings(root));
            var sequence = new List<string>();
            int reloadCount = 0;
            settingsSession.SaveObserved = () => sequence.Add("save");
            MainWindowViewModel viewModel = CreateViewModel(
                settingsSession,
                firstStartup: false,
                reloadScoresOnly: _ =>
                {
                    reloadCount++;
                    sequence.Add("reload-" + reloadCount);
                    return reloadCount == 1
                        ? Task.FromException(new InvalidOperationException("score reload failed"))
                        : Task.CompletedTask;
                },
                reportSettingsApplyFailure: _ => sequence.Add("failure"));
            SetActiveLibraryProfile(viewModel, true);
            SettingsDialogViewModel dialog = viewModel.SettingDialog;
            var presentation = new RecordingSettingsDialogPresentationPort();
            dialog.AttachPresentationPort(presentation);
            dialog.BeatorajaPlayerId = "player2";

            await dialog.ApplySettingsAsync();

            Assert.AreEqual(1, reloadCount);
            Assert.AreEqual(1, settingsSession.SaveCount);
            Assert.IsTrue(dialog.HasPendingSettingChanges(), string.Join("|", sequence));
            CollectionAssert.DoesNotContain(
                presentation.Requests,
                "close");
            Assert.IsTrue(dialog.IsEditCompletionEnabled);
            Assert.IsFalse(dialog.IsEditCancellationEnabled);

            await dialog.ApplySettingsAsync();

            CollectionAssert.AreEqual(new[] { "save", "reload-1", "failure", "reload-2" }, sequence);
            Assert.AreEqual(1, settingsSession.SaveCount);
            Assert.AreEqual(2, reloadCount);
            Assert.IsFalse(dialog.HasPendingSettingChanges());
            CollectionAssert.Contains(
                presentation.Requests,
                "close");
            Assert.IsTrue(dialog.IsEditCompletionEnabled);
            Assert.IsTrue(dialog.IsEditCancellationEnabled);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [TestMethod]
    public async Task ApplySettingsAsync_FolderChange_AwaitsFileDiffBeforeClosing()
    {
        string root = CreateTemporaryRoot();
        string addedRoot = CreateTemporaryRoot();
        try
        {
            var settingsSession = new CountingSettingsEditSession(CreateValidStandaloneSettings(root));
            var sequence = new List<string>();
            settingsSession.SaveObserved = () => sequence.Add("save");
            using var reloadStarted = new ManualResetEventSlim();
            var reloadRelease = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            MainWindowViewModel viewModel = CreateViewModel(
                settingsSession,
                firstStartup: false,
                reloadFileDiff: _ =>
                {
                    sequence.Add("reload-start");
                    reloadStarted.Set();
                    return reloadRelease.Task.ContinueWith(
                        _ => sequence.Add("reload-completed"),
                        CancellationToken.None,
                        TaskContinuationOptions.ExecuteSynchronously,
                        TaskScheduler.Default);
                });
            SetActiveLibraryProfile(viewModel, true);
            SettingsDialogViewModel dialog = viewModel.SettingDialog;
            dialog.AttachPresentationPort(new RecordingSettingsDialogPresentationPort(sequence.Add));
            dialog.StandaloneBmsRootPathList.Add(addedRoot);

            Task applyTask = dialog.ApplySettingsAsync();
            reloadStarted.Wait();
            Assert.IsTrue(dialog.IsEditCompletionInProgress);
            Assert.IsFalse(applyTask.IsCompleted);
            CollectionAssert.DoesNotContain(sequence, "close");

            reloadRelease.SetResult(true);
            await applyTask;

            CollectionAssert.AreEqual(
                new[] { "save", "reload-start", "reload-completed", "close" },
                sequence,
                string.Join("|", sequence));
            Assert.IsFalse(dialog.HasPendingSettingChanges());
        }
        finally
        {
            Directory.Delete(root, recursive: true);
            Directory.Delete(addedRoot, recursive: true);
        }
    }

    [TestMethod]
    public async Task ApplySettingsAsync_FolderDiffFailureKeepsOverlayOpen()
    {
        string root = CreateTemporaryRoot();
        string addedRoot = CreateTemporaryRoot();
        try
        {
            var settingsSession = new CountingSettingsEditSession(CreateValidStandaloneSettings(root));
            var sequence = new List<string>();
            settingsSession.SaveObserved = () => sequence.Add("save");
            int reloadCount = 0;
            MainWindowViewModel viewModel = CreateViewModel(
                settingsSession,
                firstStartup: false,
                reloadFileDiff: _ =>
                {
                    reloadCount++;
                    sequence.Add("reload-" + reloadCount);
                    return reloadCount == 1
                        ? Task.FromException(new InvalidOperationException("file diff failed"))
                        : Task.CompletedTask;
                },
                reportSettingsApplyFailure: _ => sequence.Add("failure"));
            SetActiveLibraryProfile(viewModel, true);
            SettingsDialogViewModel dialog = viewModel.SettingDialog;
            dialog.StandaloneBmsRootPathList.Add(addedRoot);
            var presentation = new RecordingSettingsDialogPresentationPort();
            dialog.AttachPresentationPort(presentation);

            await dialog.ApplySettingsAsync();

            CollectionAssert.AreEqual(new[] { "save", "reload-1", "failure" }, sequence);
            Assert.IsTrue(dialog.HasPendingSettingChanges(), string.Join("|", sequence));
            Assert.IsFalse(dialog.IsEditCancellationEnabled);
            CollectionAssert.DoesNotContain(
                presentation.Requests,
                "close");

            await dialog.ApplySettingsAsync();

            CollectionAssert.AreEqual(
                new[] { "save", "reload-1", "failure", "reload-2" },
                sequence,
                string.Join("|", sequence));
            Assert.IsFalse(dialog.HasPendingSettingChanges());
            Assert.IsTrue(dialog.IsEditCancellationEnabled);
            CollectionAssert.Contains(
                presentation.Requests,
                "close");
        }
        finally
        {
            Directory.Delete(root, recursive: true);
            Directory.Delete(addedRoot, recursive: true);
        }
    }

    [TestMethod]
    public async Task ApplySettingsAsync_DirectoryPreflightFailureKeepsOverlayOpenWithoutGenericDuplicateAndRetries()
    {
        string root = CreateTemporaryRoot();
        string addedRoot = CreateTemporaryRoot();
        try
        {
            var settingsSession = new CountingSettingsEditSession(CreateValidStandaloneSettings(root));
            var failures = new List<Exception>();
            int reloadCount = 0;
            var directoryFailure = new LibraryDirectoryPreflightException(
                LibraryDirectoryPreflightUse.BmsRoot,
                Path.Combine(root, "missing"),
                LibraryDirectoryPreflightFailureCause.NotFound,
                "missing");
            MainWindowViewModel viewModel = CreateViewModel(
                settingsSession,
                firstStartup: false,
                reloadFileDiff: _ =>
                {
                    reloadCount++;
                    return reloadCount == 1
                        ? Task.FromException(directoryFailure)
                        : Task.CompletedTask;
                },
                reportSettingsApplyFailure: failures.Add);
            SetActiveLibraryProfile(viewModel, true);
            SettingsDialogViewModel dialog = viewModel.SettingDialog;
            dialog.StandaloneBmsRootPathList.Add(addedRoot);
            var presentation = new RecordingSettingsDialogPresentationPort();
            dialog.AttachPresentationPort(presentation);

            await dialog.ApplySettingsAsync();

            Assert.AreEqual(1, settingsSession.SaveCount);
            Assert.AreEqual(1, reloadCount);
            Assert.AreEqual(0, failures.Count);
            Assert.IsTrue(dialog.HasPendingSettingChanges());
            Assert.IsFalse(dialog.IsEditCancellationEnabled);
            CollectionAssert.DoesNotContain(presentation.Requests, "close");

            await dialog.ApplySettingsAsync();

            Assert.AreEqual(2, reloadCount);
            Assert.AreEqual(0, failures.Count);
            Assert.IsFalse(dialog.HasPendingSettingChanges());
            Assert.IsTrue(dialog.IsEditCancellationEnabled);
            CollectionAssert.Contains(presentation.Requests, "close");
        }
        finally
        {
            Directory.Delete(root, recursive: true);
            Directory.Delete(addedRoot, recursive: true);
        }
    }

    [TestMethod]
    public async Task ApplySettingsAsync_InitialRetryAfterFullReloadClearsScoreReloadPending()
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
                reloadScoresOnly: _ =>
                {
                    reloadCount++;
                    return reloadCount == 1
                        ? Task.FromException(new InvalidOperationException("score reload failed"))
                        : Task.CompletedTask;
                },
                reportSettingsApplyFailure: _ => { });
            SetActiveLibraryProfile(viewModel, true);
            SettingsDialogViewModel dialog = viewModel.SettingDialog;
            dialog.BeatorajaPlayerId = "player2";

            await dialog.ApplySettingsAsync();

            Assert.AreEqual(1, reloadCount);
            Assert.IsTrue(dialog.HasPendingSettingChanges());

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

    [TestMethod]
    public async Task InitializeAsync_FirstStartupValidationFailureCompletesFalseAndRequestsInitialSetup()
    {
        string root = CreateTemporaryRoot();
        try
        {
            Settings invalidSettings = CreateValidStandaloneSettings(root);
            invalidSettings.BMSRootPath = string.Empty;
            invalidSettings.StandaloneBmsRootPaths = string.Empty;
            var settingsSession = new CountingSettingsEditSession(invalidSettings);
            MainWindowViewModel viewModel = CreateViewModel(
                settingsSession,
                firstStartup: true);
            var presentation = new RecordingSettingsDialogPresentationPort();
            viewModel.SettingDialog.AttachPresentationPort(presentation);

            bool initialized = await viewModel.InitializeAsync();

            Assert.IsFalse(initialized);
            CollectionAssert.AreEqual(new[] { "initial-setup" }, presentation.Requests);
            Assert.IsFalse(viewModel.ProgressHub.StartupProgress.IsStartupUiInteractionBlocked);
            Assert.IsFalse(viewModel.IsInitializationCompleted);
            Assert.IsFalse(viewModel.HasActiveLibraryProfile);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

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
            ApplicationPathSnapshot applicationPath = ApplicationPathSnapshot.FromExecutablePath(
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
            ApplicationPathSnapshot applicationPath = ApplicationPathSnapshot.FromExecutablePath(
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

    [TestMethod]
    public void ReinitializeLibraryAsync_LateDirectoryFailureWarnsAfterCleanupAndRethrows()
    {
        string root = CreateTemporaryRoot();
        string rootA = Path.Combine(root, "BMS-A");
        string rootB = Path.Combine(root, "BMS-B");
        string unavailableRootB = Path.Combine(root, "BMS-B-unavailable");
        string applicationRoot = Path.Combine(root, "application");
        MainWindowViewModel viewModel = null;
        Exception primaryFailure = null;
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
            ApplicationPathSnapshot applicationPath = ApplicationPathSnapshot.FromExecutablePath(
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
                var library = new TestBmsLibrary(
                    database.SongDbPath,
                    getLR2Config: null,
                    _lr2ScoreDB: null,
                    startupRequiredFileScanReason: null,
                    optionsSnapshotProvider: () => BmsLibraryOptionsSnapshot.CreateCurrent(settings),
                    applicationPathSnapshot: TestBmsFactory.MissingEverythingBridge,
                    chartFileScanner: scanner);
                var playlist = MainWindowViewModelTestFactory.CreatePlaylist(database.SongDbPath, settings);
                var composition = new ApplicationComposition(
                    settingsEditSession: settingsSession,
                    uiScheduler: new TestUiScheduler(() => TestUiDispatcherHost.Dispatcher),
                    applicationLifetime: TestApplicationContext.CreateLifetime(firstStartup: false),
                    cultureCatalog: TestApplicationContext.CreateCultureCatalog(),
                    applicationPathSnapshot: applicationPath,
                    fileDbMutationDialogService: dialogs);
                viewModel = new MainWindowViewModel(
                    composition,
                    new LateFailureStartupLibraryFactory(library, playlist));
                var presentation = new RecordingSettingsDialogPresentationPort(sequence.Add);
                viewModel.SettingDialog.AttachPresentationPort(presentation);
            });

            bool initialized = false;
            TestUiDispatcherHost.Invoke(() =>
            {
                Task<bool> initialization = viewModel.InitializeAsync();
                TestUiDispatcherHost.AwaitTaskOnDispatcher(initialization, "late-directory-startup-initialization");
                initialized = initialization.GetAwaiter().GetResult();
            });
            Assert.IsTrue(initialized);
            Directory.Move(rootB, unavailableRootB);
            try
            {
                LibraryDirectoryPreflightException thrown = null;
                TestUiDispatcherHost.Invoke(() =>
                {
                    try
                    {
                        Task reinitialize = viewModel.ReinitializeLibraryAsync();
                        TestUiDispatcherHost.AwaitTaskOnDispatcher(reinitialize, "late-directory-reinitialize");
                        Assert.Fail("A missing registered root must fail the reinitialize operation.");
                    }
                    catch (LibraryDirectoryPreflightException exception)
                    {
                        thrown = exception;
                    }
                });

                Assert.IsNotNull(thrown);
                Assert.AreEqual(LibraryDirectoryPreflightUse.BmsRoot, thrown.Use);
                Assert.AreEqual(1, dialogs.MessageCount);
                CollectionAssert.AreEqual(new[] { "warning" }, sequence);
                StringAssert.Contains(dialogs.LastMessageText, rootB);
                StringAssert.Contains(
                    dialogs.LastMessageText,
                    Resources.LibraryDirectoryPreflightBmsRootRole);
                Assert.IsTrue(viewModel.ProgressHub.StartupProgress.IsFailed);
                Assert.IsTrue(viewModel.ProgressHub.StartupProgress.IsRetryableFailure);
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
                            // InitializeAsync leaves owned deferred work alive. Completing
                            // reinitialize's failure cleanup is not the shell shutdown signal.
                            TestUiDispatcherHost.AwaitTaskOnDispatcher(
                                viewModel.ShellShutdownWorkflow.RequestWindowCloseAsync(),
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

    [TestMethod]
    public void InitializeAsync_LateDirectoryFailureAfterConstructionWarnsAfterCleanupAndOpensSettings()
    {
        string root = CreateTemporaryRoot();
        string rootA = Path.Combine(root, "BMS-A");
        string rootB = Path.Combine(root, "BMS-B");
        string unavailableRootB = Path.Combine(root, "BMS-B-unavailable");
        string applicationRoot = Path.Combine(root, "application");
        MainWindowViewModel viewModel = null;
        Task<bool> retryInitialization = null;
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
            ApplicationPathSnapshot applicationPath = ApplicationPathSnapshot.FromExecutablePath(
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
            Exception retryFailure = null;
            Exception retryDrainFailure = null;
            dialogs.MessageObserved = () =>
            {
                sequence.Add("warning");
            };
            dialogs.MessageObservedAsync = async () =>
            {
                warningObservedWithOperationReleased = !viewModel.IsLibraryOperationInProgress;
                warningObservedWithProgressUnblocked = !viewModel.ProgressHub.StartupProgress.IsStartupUiInteractionBlocked;
                if (!retryRequested)
                {
                    retryRequested = true;
                    Directory.Move(unavailableRootB, rootB);
                    retryInitialization = viewModel.InitializeAsync();
                }
                try
                {
                    retrySucceededDuringWarning = await retryInitialization.WaitAsync(TimeSpan.FromSeconds(2));
                    retryCompletedDuringWarning = retryInitialization.IsCompleted;
                }
                catch (TimeoutException)
                {
                    retryTimedOutDuringWarning = true;
                    retryCompletedDuringWarning = retryInitialization.IsCompleted;
                }
                catch (Exception exception)
                {
                    retryFailure = exception;
                    retryCompletedDuringWarning = retryInitialization.IsCompleted;
                }
            };

            TestUiDispatcherHost.Invoke(() =>
            {
                var library = new TestBmsLibrary(
                    database.SongDbPath,
                    getLR2Config: null,
                    _lr2ScoreDB: null,
                    startupRequiredFileScanReason: null,
                    optionsSnapshotProvider: () => BmsLibraryOptionsSnapshot.CreateCurrent(settings),
                    applicationPathSnapshot: TestBmsFactory.MissingEverythingBridge,
                    chartFileScanner: scanner);
                var playlist = MainWindowViewModelTestFactory.CreatePlaylist(database.SongDbPath, settings);
                var composition = new ApplicationComposition(
                    settingsEditSession: settingsSession,
                    uiScheduler: new TestUiScheduler(() => TestUiDispatcherHost.Dispatcher),
                    applicationLifetime: TestApplicationContext.CreateLifetime(firstStartup: false),
                    cultureCatalog: TestApplicationContext.CreateCultureCatalog(),
                    applicationPathSnapshot: applicationPath,
                    fileDbMutationDialogService: dialogs);
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
                Task<bool> initialization = viewModel.InitializeAsync();
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
                                retryInitialization,
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
            Assert.IsTrue(viewModel.IsInitializationCompleted);
            Assert.IsTrue(viewModel.HasActiveLibraryProfile);
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
                                retryInitialization,
                                "late-directory-startup-retry-cleanup");
                        }
                        catch
                        {
                        }
                    }
                    TestUiDispatcherHost.AwaitTaskOnDispatcher(
                        viewModel.ShellShutdownWorkflow.RequestWindowCloseAsync(),
                        "late-directory-startup-shutdown");
                    viewModel.SettingDialog.Dispose();
                });
            }
            if (Directory.Exists(unavailableRootB) && !Directory.Exists(rootB))
            {
                Directory.Move(unavailableRootB, rootB);
            }
            Directory.Delete(root, recursive: true);
        }
    }

    [DataTestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public async Task ApplySettingsAsync_InitialSettings_SavesClosesAndInitializes(bool firstStartup)
    {
        string root = CreateTemporaryRoot();
        try
        {
            var settingsSession = new CountingSettingsEditSession(CreateValidStandaloneSettings(root));
            var sequence = new List<string>();
            var dialogs = new RecordingRootDialogService();
            dialogs.MessageObserved = () => sequence.Add("completion-message");
            using var initializationStarted = new ManualResetEventSlim();
            var initializationRelease = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            int initializeCount = 0;
            MainWindowViewModel viewModel = CreateViewModel(
                settingsSession,
                firstStartup,
                initializeOwner: _ =>
                {
                    initializeCount++;
                    sequence.Add("initialize-start");
                    initializationStarted.Set();
                    return initializationRelease.Task.ContinueWith(
                        _ =>
                        {
                            sequence.Add("initialize-completed");
                            return true;
                        },
                        CancellationToken.None,
                        TaskContinuationOptions.ExecuteSynchronously,
                        TaskScheduler.Default);
                },
                dialogs: dialogs);
            SettingsDialogViewModel dialog = viewModel.SettingDialog;
            settingsSession.SaveObserved = () => sequence.Add("save");
            dialog.AttachPresentationPort(new RecordingSettingsDialogPresentationPort(sequence.Add));
            dialog.ShowRecommUpdatedMsg = !dialog.ShowRecommUpdatedMsg;
            Assert.IsTrue(dialog.CheckValidation(out string validationError), validationError);

            Task applyTask = dialog.ApplySettingsAsync();
            initializationStarted.Wait();
            Assert.IsTrue(dialog.IsEditCompletionInProgress);
            Assert.IsFalse(applyTask.IsCompleted);
            initializationRelease.SetResult(true);
            await applyTask;

            CollectionAssert.AreEqual(
                firstStartup
                    ? new[] { "save", "completion-message", "initialize-start", "initialize-completed", "close" }
                    : new[] { "save", "initialize-start", "initialize-completed", "close" },
                sequence);
            Assert.AreEqual(firstStartup ? 1 : 0, dialogs.MessageCount);
            Assert.AreEqual(1, initializeCount);
            Assert.AreEqual(1, settingsSession.SaveCount);
            Assert.IsFalse(dialog.HasPendingSettingChanges());
            Assert.IsTrue(dialog.IsEditCompletionEnabled);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [DataTestMethod]
    [DataRow("Failed")]
    [DataRow("OwnerUnavailable")]
    public async Task ApplySettingsAsync_InitialSettingsDialogFailureIsReported(string statusName)
    {
        string root = CreateTemporaryRoot();
        try
        {
            UiDialogStatus status = Enum.Parse<UiDialogStatus>(statusName);
            var settingsSession = new CountingSettingsEditSession(CreateValidStandaloneSettings(root));
            var sequence = new List<string>();
            var dialogs = new RecordingRootDialogService
            {
                MessageResult = status == UiDialogStatus.Failed
                    ? UiDialogResult.Failed(new InvalidOperationException("completion dialog failed"))
                    : UiDialogResult.NotShown(UiDialogStatus.OwnerUnavailable)
            };
            dialogs.MessageObserved = () => sequence.Add("completion-message");
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
            SettingsDialogViewModel dialog = viewModel.SettingDialog;
            settingsSession.SaveObserved = () => sequence.Add("save");
            dialog.ShowRecommUpdatedMsg = !dialog.ShowRecommUpdatedMsg;
            Assert.IsTrue(dialog.CheckValidation(out string validationError), validationError);

            await dialog.ApplySettingsAsync();

            Assert.IsNotNull(reportedFailure);
            StringAssert.Contains(reportedFailure!.Message, status.ToString());
            Assert.AreEqual(1, settingsSession.SaveCount);
            Assert.AreEqual(1, dialogs.MessageCount);
            Assert.AreEqual(0, initializeCount);
            CollectionAssert.AreEqual(new[] { "save", "completion-message" }, sequence);
            Assert.IsTrue(dialog.IsEditCompletionEnabled);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [TestMethod]
    public async Task ApplySettingsAsync_CompletionGuardRejectsConcurrentCall()
    {
        string root = CreateTemporaryRoot();
        try
        {
            var settingsSession = new CountingSettingsEditSession(CreateValidStandaloneSettings(root))
            {
                BlockSave = true
            };
            MainWindowViewModel viewModel = CreateViewModel(settingsSession, firstStartup: false);
            SetActiveLibraryProfile(viewModel, true);
            SettingsDialogViewModel dialog = viewModel.SettingDialog;
            var presentation = new RecordingSettingsDialogPresentationPort();
            dialog.AttachPresentationPort(presentation);
            dialog.ShowRecommUpdatedMsg = !dialog.ShowRecommUpdatedMsg;

            Task first = Task.Run(() => dialog.ApplySettingsAsync());
            settingsSession.SaveEntered.Wait();
            Assert.IsTrue(dialog.IsEditCompletionInProgress);
            Task second = dialog.ApplySettingsAsync();
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
            Directory.Delete(root, recursive: true);
        }
    }

    [TestMethod]
    public async Task ApplySettingsAsync_FullRestartFailureKeepsOverlayOpen()
    {
        string root = CreateTemporaryRoot();
        try
        {
            var settingsSession = new CountingSettingsEditSession(CreateValidStandaloneSettings(root));
            int initializeCount = 0;
            MainWindowViewModel viewModel = CreateViewModel(
                settingsSession,
                firstStartup: false,
                initializeOwner: _ =>
                {
                    initializeCount++;
                    return Task.FromResult(initializeCount != 1);
                });
            SetActiveLibraryProfile(viewModel, true);
            SettingsDialogViewModel dialog = viewModel.SettingDialog;
            var presentation = new RecordingSettingsDialogPresentationPort();
            dialog.AttachPresentationPort(presentation);
            SetPrivateField(dialog, "tempOperationModeLR2DB", !dialog.OperationModeLR2DB);

            await dialog.ApplySettingsAsync();

            Assert.AreEqual(1, initializeCount);
            Assert.IsFalse(viewModel.HasActiveLibraryProfile);
            CollectionAssert.DoesNotContain(
                presentation.Requests,
                "close");
            Assert.IsTrue(dialog.IsEditCompletionEnabled);

            await dialog.ApplySettingsAsync();

            Assert.AreEqual(2, initializeCount);
            CollectionAssert.Contains(
                presentation.Requests,
                "close");
            Assert.IsTrue(dialog.IsEditCompletionEnabled);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [TestMethod]
    public async Task ApplySettingsAsync_InitialInitializationFailureKeepsOverlayOpen()
    {
        string root = CreateTemporaryRoot();
        try
        {
            var settingsSession = new CountingSettingsEditSession(CreateValidStandaloneSettings(root));
            int initializeCount = 0;
            MainWindowViewModel viewModel = CreateViewModel(
                settingsSession,
                firstStartup: false,
                initializeOwner: _ =>
                {
                    initializeCount++;
                    return Task.FromResult(false);
                });
            SettingsDialogViewModel dialog = viewModel.SettingDialog;
            var presentation = new RecordingSettingsDialogPresentationPort();
            dialog.AttachPresentationPort(presentation);

            await dialog.ApplySettingsAsync();

            Assert.AreEqual(1, initializeCount);
            Assert.IsFalse(viewModel.HasActiveLibraryProfile);
            CollectionAssert.DoesNotContain(
                presentation.Requests,
                "close");
            Assert.IsTrue(dialog.IsEditCompletionEnabled);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [TestMethod]
    public void Lr2PathPresentation_StandardAndCustomDraftsFollowSharedCancelSnapshot()
    {
        string root = CreateTemporaryRoot();
        string standardSongDb = Path.Combine(root, "LR2files", "Database", "song.db");
        string standardConfig = Path.Combine(root, "LR2files", "Config", "config.xml");
        string customSongDb = Path.Combine(root, "custom", "songs.db");
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(standardSongDb)!);
            Directory.CreateDirectory(Path.GetDirectoryName(standardConfig)!);
            Directory.CreateDirectory(Path.GetDirectoryName(customSongDb)!);
            File.WriteAllBytes(standardSongDb, []);
            File.WriteAllText(standardConfig, "<config><system /><jukebox /></config>");
            File.WriteAllBytes(customSongDb, []);
            File.WriteAllBytes(Path.Combine(root, "LR2body.exe"), []);
            Settings values = CreateValidStandaloneSettings(root);
            values.LR2RootPath = root;
            values.LR2SongDBPath = standardSongDb;
            values.LR2ConfigXmlPath = standardConfig;
            var session = new CountingSettingsEditSession(values);
            SettingsDialogViewModel dialog = CreateViewModel(session, firstStartup: false).SettingDialog;

            Assert.IsFalse(dialog.HasCustomLr2Paths);
            Assert.AreEqual("Success", dialog.Lr2SongDbPathStatusKind);
            dialog.LR2SongDBPath = customSongDb;
            Assert.IsTrue(dialog.HasCustomLr2Paths);

            dialog.ResetSettings();

            Assert.AreEqual(standardSongDb, dialog.LR2SongDBPath);
            Assert.AreEqual(standardConfig, dialog.LR2ConfigXmlPath);
            Assert.IsFalse(dialog.HasCustomLr2Paths);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [DataTestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public void Lr2AdvancedPathEntry_IsAvailableForStandardAndCustomLinkedConfigurations(bool useCustomPaths)
    {
        TestUiDispatcherHost.RunWindowTest(windowTest =>
        {
            string scope = CreateTemporaryRoot();
            SettingsWindow? window = null;
            try
            {
                string root = Path.Combine(scope, "root");
                (string standardSong, string standardConfig) = CreateValidLr2Layout(root);
                string customSong = Path.Combine(scope, "custom", "song.db");
                string customConfig = Path.Combine(scope, "custom", "config.xmh");
                Directory.CreateDirectory(Path.GetDirectoryName(customSong)!);
                File.WriteAllBytes(customSong, []);
                File.WriteAllText(customConfig, "<config><system /><jukebox /></config>");

                Settings values = CreateValidStandaloneSettings(scope);
                values.OperationModeLR2DB = true;
                values.LR2RootPath = root;
                values.LR2SongDBPath = useCustomPaths ? customSong : standardSong;
                values.LR2ConfigXmlPath = useCustomPaths ? customConfig : standardConfig;
                var session = new CountingSettingsEditSession(values);
                SettingsDialogViewModel dialog = CreateViewModel(session, firstStartup: false).SettingDialog;

                window = OpenSettingsWindow(windowTest, dialog);
                Button advancedPathsButton = FindDescendants<Button>(window)
                    .Single(button => button.Name == "buttonEditCustomLr2Paths");
                Button resyncButton = FindDescendants<Button>(window)
                    .Single(button => Equals(button.Content, Resources.Lr2_song_db_sync_data_resync));

                Assert.AreEqual(useCustomPaths, dialog.HasCustomLr2Paths);
                Assert.AreEqual(Visibility.Visible, advancedPathsButton.Visibility);
                Assert.IsTrue(advancedPathsButton.IsEnabled);
                Assert.IsFalse(string.IsNullOrWhiteSpace(resyncButton.Content as string));
                Assert.AreEqual(Resources.Lr2_song_db_sync_data_resync, resyncButton.Content);
            }
            finally
            {
                if (window?.IsLoaded == true)
                {
                    window.CloseForOwnerShutdown();
                }
                Directory.Delete(scope, recursive: true);
            }
        });
    }

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
            SettingsDialogViewModel dialog = CreateViewModel(session, firstStartup: false).SettingDialog;

            dialog.LR2RootPath = nextRoot;

            Assert.AreEqual(nextRoot, dialog.LR2RootPath);
            Assert.AreEqual(nextSong, dialog.LR2SongDBPath);
            Assert.AreEqual(nextConfig, dialog.LR2ConfigXmlPath);
            Assert.IsFalse(dialog.HasCustomLr2Paths);
        }
        finally
        {
            Directory.Delete(scope, recursive: true);
        }
    }

    [DataTestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public async Task Lr2InvalidPersistedConfig_OpenSaveReopenAndParentCancelPreserveRawTuple(bool createMalformedFile)
    {
        string scope = CreateTemporaryRoot();
        try
        {
            string originalRoot = Path.Combine(scope, "original");
            (string originalSong, _) = CreateValidLr2Layout(originalRoot);
            string rawConfig = Path.Combine(scope, "custom", "Config", "config.xml");
            if (createMalformedFile)
            {
                Directory.CreateDirectory(Path.GetDirectoryName(rawConfig)!);
                File.WriteAllText(rawConfig, "<config>");
            }
            string nextRoot = Path.Combine(scope, "next");
            CreateValidLr2Layout(nextRoot);

            Settings values = CreateValidStandaloneSettings(scope);
            values.LR2RootPath = originalRoot;
            values.LR2SongDBPath = originalSong;
            values.LR2ConfigXmlPath = rawConfig;
            var session = new CountingSettingsEditSession(values);
            SettingsDialogViewModel opened = CreateViewModel(session, firstStartup: false).SettingDialog;

            Assert.AreEqual(rawConfig, opened.LR2ConfigXmlPath);
            Assert.AreEqual(createMalformedFile ? "Error" : "Warning", opened.Lr2ConfigPathStatusKind);
            await opened.SaveSettings();

            SettingsDialogViewModel reopened = CreateViewModel(session, firstStartup: false).SettingDialog;
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

    [TestMethod]
    public void Lr2AdvancedConfigPicker_MalformedCandidateKeepsPreviousRawPathAndReportsFailure()
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
            SettingsDialogViewModel draft = CreateViewModel(session, firstStartup: false).SettingDialog;

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
            SettingsDialogViewModel draft = CreateViewModel(session, firstStartup: false).SettingDialog;

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

    [TestMethod]
    public void Lr2RootPicker_ReselectingSameRootRestoresStandardTupleAndClearsCustomState()
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
            SettingsDialogViewModel draft = CreateViewModel(session, firstStartup: false).SettingDialog;

            draft.SetRootFolderPathFromPicker(nameof(draft.LR2RootPath), root);

            Assert.AreEqual(root, draft.LR2RootPath);
            Assert.AreEqual(standardSong, draft.LR2SongDBPath);
            Assert.AreEqual(standardConfig, draft.LR2ConfigXmlPath);
            Assert.IsFalse(draft.HasCustomLr2Paths);
            Assert.IsFalse(draft.HasLr2PathSelectionError);
        }
        finally
        {
            Directory.Delete(scope, recursive: true);
        }
    }

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
            SettingsDialogViewModel draft = CreateViewModel(session, firstStartup: false).SettingDialog;

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
            SettingsDialogViewModel draft = CreateViewModel(session, firstStartup: false).SettingDialog;
            var changedProperties = new List<string>();
            draft.PropertyChanged += (_, args) => changedProperties.Add(args.PropertyName);

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
                nameof(draft.HasCustomLr2Paths),
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

    [TestMethod]
    public async Task Lr2RootPicker_ReselectingSameRootAdoptsExternalConfigBeforeLaterSave()
    {
        string scope = CreateTemporaryRoot();
        try
        {
            string root = Path.Combine(scope, "root");
            (string song, string configPath) = CreateValidLr2Layout(root);
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
            SettingsDialogViewModel draft = CreateViewModel(session, firstStartup: false).SettingDialog;
            int directoryNotifications = 0;
            draft.PropertyChanged += (_, args) =>
            {
                if (args.PropertyName == nameof(draft.LR2ConfigBMSDirectories))
                {
                    directoryNotifications++;
                }
            };
            var externalDocument = new XDocument(
                new XElement("config",
                    new XElement("system"),
                    new XElement("sentinel", new XAttribute("source", "external")),
                    new XElement("jukebox", new XElement("path", externalRoot + Path.DirectorySeparatorChar))));
            externalDocument.Save(configPath);

            draft.SetRootFolderPathFromPicker(nameof(draft.LR2RootPath), root);

            CollectionAssert.Contains(draft.LR2ConfigBMSDirectories.ToArray(), externalRoot);
            Assert.IsTrue(directoryNotifications > 0);

            draft.AddBmsSearchRootPaths([addedRoot]);
            await draft.SaveSettings();

            XDocument savedDocument = XDocument.Load(configPath);
            Assert.AreEqual("external", (string)savedDocument.Root?.Element("sentinel")?.Attribute("source"));
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

    [TestMethod]
    public void Lr2ChildPickers_ReselectingRetainedValidPathsClearsRejectedCandidateError()
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
            SettingsDialogViewModel draft = CreateViewModel(session, firstStartup: false).SettingDialog;

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

    [TestMethod]
    public async Task Lr2AdvancedPathsDialog_DoneKeepsSharedDraftForSettingsSaveAndReopen()
    {
        string scope = CreateTemporaryRoot();
        try
        {
            string standardRoot = Path.Combine(scope, "standard");
            (string standardSong, string standardConfig) = CreateValidLr2Layout(standardRoot);
            string customSong = Path.Combine(scope, "custom", "database", "songs.db");
            string customConfig = Path.Combine(scope, "custom", "configuration", "config.xmh");
            Directory.CreateDirectory(Path.GetDirectoryName(customSong)!);
            Directory.CreateDirectory(Path.GetDirectoryName(customConfig)!);
            File.WriteAllBytes(customSong, []);
            File.WriteAllText(customConfig, "<config><system /><jukebox /></config>");

            Settings values = CreateValidStandaloneSettings(scope);
            values.LR2RootPath = standardRoot;
            values.LR2SongDBPath = standardSong;
            values.LR2ConfigXmlPath = standardConfig;
            var session = new CountingSettingsEditSession(values);
            SettingsDialogViewModel draft = CreateViewModel(session, firstStartup: false).SettingDialog;

            bool? result = null;
            TestUiDispatcherHost.RunWindowTest(windowTest =>
                {
                    var advancedDialog = new Lr2AdvancedPathsDialog(draft);
                    advancedDialog.ContentRendered += (_, _) =>
                    {
                        SetLr2AdvancedPathText(advancedDialog, Resources.FilePath_songDB, customSong);
                        SetLr2AdvancedPathText(advancedDialog, Resources.FilePath_configXml, customConfig);
                        FindDescendants<Button>(advancedDialog)
                            .Single(button => button.IsDefault)
                            .RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent));
                    };
                    windowTest.PrepareForOwnedPresentation(advancedDialog);
                    result = advancedDialog.ShowDialog();
                });

            Assert.AreEqual(true, result);
            Assert.AreEqual(customSong, draft.LR2SongDBPath);
            Assert.AreEqual(customConfig, draft.LR2ConfigXmlPath);
            await draft.SaveSettings();
            Assert.AreEqual(1, session.SaveCount);

            SettingsDialogViewModel reopened = CreateViewModel(session, firstStartup: false).SettingDialog;
            Assert.AreEqual(customSong, reopened.LR2SongDBPath);
            Assert.AreEqual(customConfig, reopened.LR2ConfigXmlPath);
            Assert.IsTrue(reopened.HasCustomLr2Paths);
        }
        finally
        {
            Directory.Delete(scope, recursive: true);
        }
    }







    [DataTestMethod]
    [DataRow(true)]
    [DataRow(false)]
    public void TryApplyLr2AdvancedPathDraft_InvalidTupleDoesNotPartiallyMutate(bool rejectSong)
    {
        string scope = CreateTemporaryRoot();
        try
        {
            string originalRoot = Path.Combine(scope, "original");
            (string originalSong, string originalConfig) = CreateValidLr2Layout(originalRoot);
            string candidateRoot = Path.Combine(scope, "candidate");
            (string candidateSong, string candidateConfig) = CreateValidLr2Layout(candidateRoot);
            string malformedConfig = Path.Combine(scope, "malformed", "config.xml");
            Directory.CreateDirectory(Path.GetDirectoryName(malformedConfig)!);
            File.WriteAllText(malformedConfig, "<config>");

            Settings values = CreateValidStandaloneSettings(scope);
            values.LR2RootPath = originalRoot;
            values.LR2SongDBPath = originalSong;
            values.LR2ConfigXmlPath = originalConfig;
            var session = new CountingSettingsEditSession(values);
            SettingsDialogViewModel draft = CreateViewModel(session, firstStartup: false).SettingDialog;

            bool applied = draft.TryApplyLr2AdvancedPathDraft(
                rejectSong ? Path.Combine(scope, "missing", "song.db") : candidateSong,
                rejectSong ? candidateConfig : malformedConfig,
                out string rejectedPathPropertyName);

            Assert.IsFalse(applied);
            Assert.AreEqual(
                rejectSong ? nameof(draft.LR2SongDBPath) : nameof(draft.LR2ConfigXmlPath),
                rejectedPathPropertyName);
            Assert.AreEqual(originalSong, draft.LR2SongDBPath);
            Assert.AreEqual(originalConfig, draft.LR2ConfigXmlPath);
            Assert.IsTrue(draft.HasLr2PathSelectionError);
            Assert.AreEqual(0, session.SaveCount);
        }
        finally
        {
            Directory.Delete(scope, recursive: true);
        }
    }

    [TestMethod]
    public async Task Lr2CustomPaths_OpenWithoutEditingSaveAndReopenPreservesRawValues()
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
            SettingsDialogViewModel opened = CreateViewModel(session, firstStartup: false).SettingDialog;

            Assert.IsTrue(opened.HasCustomLr2Paths);
            await opened.SaveSettings();

            SettingsDialogViewModel reopened = CreateViewModel(session, firstStartup: false).SettingDialog;
            Assert.AreEqual(customSong, reopened.LR2SongDBPath);
            Assert.AreEqual(customConfig, reopened.LR2ConfigXmlPath);
            Assert.IsTrue(reopened.HasCustomLr2Paths);
        }
        finally
        {
            Directory.Delete(scope, recursive: true);
        }
    }

    [DataTestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public void Lr2AdvancedPathsDialog_CancelOrNativeCloseRestoresDraftBeforeReopen(bool useNativeClose)
    {
        string scope = CreateTemporaryRoot();
        try
        {
            string root = Path.Combine(scope, "standard");
            CreateValidLr2Layout(root);
            string savedSong = Path.Combine(scope, "saved", "database", "songs.db");
            string savedConfig = Path.Combine(scope, "saved", "configuration", "config.xml");
            string editedSong = Path.Combine(scope, "edited", "database", "songs.db");
            string editedConfig = Path.Combine(scope, "edited", "configuration", "config.xmh");
            foreach (string path in new[] { savedSong, savedConfig, editedSong, editedConfig })
            {
                Directory.CreateDirectory(Path.GetDirectoryName(path)!);
                File.WriteAllText(path, path.EndsWith(".db", StringComparison.OrdinalIgnoreCase)
                    ? string.Empty
                    : "<config><system /><jukebox /></config>");
            }

            Settings values = CreateValidStandaloneSettings(scope);
            values.LR2RootPath = root;
            values.LR2SongDBPath = savedSong;
            values.LR2ConfigXmlPath = savedConfig;
            var session = new CountingSettingsEditSession(values);
            SettingsDialogViewModel draft = CreateViewModel(session, firstStartup: false).SettingDialog;

            bool? result = null;
            TestUiDispatcherHost.RunWindowTest(windowTest =>
                {
                    var advancedDialog = new Lr2AdvancedPathsDialog(draft);
                    advancedDialog.ContentRendered += (_, _) =>
                    {
                        SetLr2AdvancedPathText(advancedDialog, Resources.FilePath_songDB, editedSong);
                        SetLr2AdvancedPathText(advancedDialog, Resources.FilePath_configXml, editedConfig);
                        if (useNativeClose)
                        {
                            advancedDialog.Close();
                        }
                        else
                        {
                            FindDescendants<Button>(advancedDialog)
                                .Single(button => button.IsCancel)
                                .RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent));
                        }
                    };
                    windowTest.PrepareForOwnedPresentation(advancedDialog);
                    result = advancedDialog.ShowDialog();
                });

            Assert.IsFalse(result == true);
            Assert.AreEqual(savedSong, draft.LR2SongDBPath);
            Assert.AreEqual(savedConfig, draft.LR2ConfigXmlPath);
            Assert.AreEqual(0, session.SaveCount);

            SettingsDialogViewModel reopened = CreateViewModel(session, firstStartup: false).SettingDialog;
            Assert.AreEqual(savedSong, reopened.LR2SongDBPath);
            Assert.AreEqual(savedConfig, reopened.LR2ConfigXmlPath);
            Assert.IsTrue(reopened.HasCustomLr2Paths);
        }
        finally
        {
            Directory.Delete(scope, recursive: true);
        }
    }

    private static void SetLr2AdvancedPathText(Lr2AdvancedPathsDialog dialog, string label, string path)
    {
        TextBox editor = GetLr2AdvancedPathEditor(dialog, label);
        SettingsPathPicker picker = FindDescendants<SettingsPathPicker>(dialog)
            .Single(candidate => candidate.Label == label);
        Assert.IsFalse(picker.IsPathReadOnly);
        Assert.IsFalse(editor.IsReadOnly);

        editor.Text = path;
        editor.GetBindingExpression(TextBox.TextProperty)!.UpdateSource();
        dialog.Dispatcher.Invoke(DispatcherPriority.DataBind, new Action(() => { }));
    }

    private static TextBox GetLr2AdvancedPathEditor(Lr2AdvancedPathsDialog dialog, string label)
    {
        SettingsPathPicker picker = FindDescendants<SettingsPathPicker>(dialog)
            .Single(candidate => candidate.Label == label);
        Assert.IsFalse(picker.IsPathReadOnly);
        TextBox editor = FindDescendants<TextBox>(picker).Single();
        Assert.IsFalse(editor.IsReadOnly);
        return editor;
    }

    private static void InvokeEnterAccessKey()
    {
        AccessKeyManager.ProcessKey(null, "\r", false);
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
        CountingSettingsEditSession settingsSession,
        bool firstStartup,
        Func<MainWindowViewModel, Task<bool>>? initializeOwner = null,
        Func<MainWindowViewModel, Task>? reloadScoresOnly = null,
        Action<Exception>? reportSettingsApplyFailure = null,
        Func<MainWindowViewModel, Task>? reloadFileDiff = null,
        ISettingsDialogPlayerFactoryPort? playerFactoryPort = null,
        ISettingsDialogPlaybackRuntimePort? playbackRuntimePort = null,
        IUiDialogService? dialogs = null)
    {
        var composition = new ApplicationComposition(
            settingsEditSession: settingsSession,
            reportSettingsApplyFailure: reportSettingsApplyFailure ?? (_ => { }),
            uiScheduler: new WpfUiScheduler(() => Dispatcher.CurrentDispatcher), applicationLifetime: TestApplicationContext.CreateLifetime(firstStartup), cultureCatalog: TestApplicationContext.CreateCultureCatalog());
        MainWindowViewModel viewModel = composition.CreateMainWindowViewModel();
        if (initializeOwner != null || reloadScoresOnly != null || reloadFileDiff != null)
        {
            SettingsDialogViewModel testDialog = new(
                new TestSettingsDialogStatePort(
                    viewModel,
                    initializeOwner == null
                        ? () => viewModel.InitializeAsync()
                        : () => initializeOwner(viewModel),
                    () =>
                    {
                        SetPrivateField(viewModel, "initializationCompleted", false);
                        SetPrivateField(viewModel, "hasActiveLibraryProfile", false);
                    },
                    reloadScoresOnly: reloadScoresOnly == null
                        ? () => Task.CompletedTask
                        : () => reloadScoresOnly(viewModel),
                    reloadFileDiff: reloadFileDiff == null
                        ? () => Task.CompletedTask
                        : () => reloadFileDiff(viewModel)),
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
                audioDeviceTestWorkflow: AudioDeviceTestWorkflowTestFactory.Create());
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
            new NoOpSettingsEditSession(new Settings()),
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
        SetActiveLibraryProfile(owner, true);
        return new SettingsDialogViewModel(
            new TestSettingsDialogStatePort(owner, () => Task.FromResult(true)),
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
        IUiDialogService? dialogs = null)
    {
        return new SettingsDialogViewModel(
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
            audioDeviceCatalog: new TestAudioDeviceCatalog(),
            audioSettingsGateway: audioGateway);
    }

    private static void ConfigureExplicitAudioSettings(Settings settings)
    {
        settings.PlayerDevice = "requested-device";
        settings.PlayerDeviceName = "Requested device";
        settings.PlayerSampleRate = SampleRate.SAMPLE_RATE_44100Hz;
        settings.PlayerFormat = SampleFormat.SAMPLE_INT_16BIT;
        settings.PlayerBufferSize = 10;
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
        var config = new BeMusicSeeker.Models.LR2.LR2Config(configPath);
        config.AddBMSSearchDirectories([bmsRoot, otherRoot]);
        config.Save(configPath);
        var dialog = new SettingsDialogViewModel(
            new TestSettingsDialogStatePort(
                owner,
                () => Task.FromResult(true),
                reloadFileDiff: reloadFileDiff),
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

    private static Settings CreateValidStandaloneSettings(string root)
    {
        var settings = new Settings
        {
            OperationModeLR2DB = false,
            BMSRootPath = root,
            StandaloneBmsRootPaths = root,
            BMSInstallDir = root,
            TableListURL = new Uri("http://127.0.0.1:1/table-list.json"),
            EnablePlaylistUrlCompletion = false,
            ScanBmsFilesOnStartup = false,
            SkipInitPlaylistLoad = true,
            UseBeatorajaScoreDb = false,
            EnableBeatorajaBmtOutput = false,
            UseExternalPanelImage = false,
            UsePlayeruBMplay = false,
            UsePlayerLR2body = false,
            UsePlayerBMIIDXView = false,
            IsLR2BackupEnabled = false
        };
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
        IList<string>? scheduledOperations = null)
    {
        string databasePath = Path.Combine(root, "settings-test-playlists.db");
        File.WriteAllBytes(databasePath, []);
        var tables = new TestBmsPlaylist(databasePath)
        {
            BMSTables = new ObservableCollection<BMSTable>(new System.Collections.ObjectModel.ObservableCollection<BMSTable>())
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
        private readonly Action beforeCreateBmsLibrary;

        internal LateFailureStartupLibraryFactory(
            BMSLibrary library,
            BMSPlaylist playlist,
            Action beforeCreateBmsLibrary = null)
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

        internal UiDialogResult MessageResult { get; set; } = UiDialogResult.FromMessageBoxResult(MessageBoxResult.OK);

        internal Action? MessageObserved { get; set; }

        internal Func<Task>? MessageObservedAsync { get; set; }

        internal List<UiConfirmationRequest> ConfirmationRequests { get; } = [];

        internal int ConfirmationCount { get; private set; }

        internal int MessageCount { get; private set; }

        internal string LastMessageText { get; private set; } = string.Empty;

        public Task<UiDialogResult> ShowMessageAsync(UiMessageRequest request, CancellationToken cancellationToken = default)
        {
            MessageCount++;
            LastMessageText = request.MessageBoxText;
            MessageObserved?.Invoke();
            return CompleteMessageAsync();
        }

        private async Task<UiDialogResult> CompleteMessageAsync()
        {
            if (MessageObservedAsync != null)
            {
                await MessageObservedAsync();
            }
            return MessageResult;
        }

        public Task<UiDialogResult> ConfirmAsync(UiConfirmationRequest request, CancellationToken cancellationToken = default)
        {
            ConfirmationCount++;
            ConfirmationRequests.Add(request);
            return Task.FromResult(ConfirmationResult);
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

        public Task<bool> InitializeLibraryAsync() => Task.FromResult(true);

        public Task ReloadScoresOnlyAsync() => Task.CompletedTask;

        public Task ReloadFileDiffAsync() => Task.CompletedTask;

        public event EventHandler? LibraryOperationAvailabilityChanged;

        public event Action<Lr2PlayHistorySchemaStatusSnapshot>? Lr2PlayHistorySchemaStatusChanged;
    }

    private sealed class ComposedSettingsDialogWorkspacePort :
        ISettingsDialogWorkspacePort,
        ISettingsDialogCustomFolderOutputPort
    {
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

        public void QueueBeatorajaBmtExportAll(string reason, string cleanupTablePath)
        {
        }

        public Task RunWithPlaylistOperationNotificationsAsync(Func<Task> operation, string operationName) =>
            operation();

        public void ChangeCustomFolderBaseDirectoryWithSettings(
            string outputDirBaseBefore,
            string outputDirBaseAfter,
            string additionalOutputBaseDirsBefore,
            string additionalOutputBaseDirsAfter,
            CustomFolderOutputSettingsSnapshot settings)
        {
        }

        public void ChangeCustomFolderBaseDirectoryRootWithSettings(
            string outputDirBaseBefore,
            string outputDirBaseAfter,
            CustomFolderOutputSettingsSnapshot settings)
        {
        }

        public bool SyncCustomFolderOutputSearchRootsAfterSettingsChangeWithSettings(
            string previousRootOutputBaseDirectory,
            CustomFolderOutputSettingsSnapshot settings) => false;

        public int ApplyCustomFolderAdditionalOutputBaseRegistrationChanges(
            string previousAdditionalOutputBaseDirectories,
            IReadOnlyDictionary<string, string> pendingRenames,
            CustomFolderOutputSettingsSnapshot settings) => 0;

        public event EventHandler<PlaylistCatalogChangedEventArgs>? PlaylistCatalogChanged;
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

        internal ManualResetEventSlim SaveEntered { get; } = new(false);

        internal ManualResetEventSlim ReleaseSave { get; } = new(false);

        internal int SaveCount { get; private set; }

        public Settings Values { get; }

        public void Reload()
        {
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

        public void OpenSettingsDialog()
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
        public void StopPlayback()
        {
        }
    }

    private sealed class TestSettingsDialogPlayerFactoryPort : ISettingsDialogPlayerFactoryPort
    {
        private readonly IList<string>? sequence;

        internal TestSettingsDialogPlayerFactoryPort(IList<string>? sequence = null)
        {
            this.sequence = sequence;
        }

        internal Exception? DefaultFactoryFailure { get; set; }

        internal Exception? ConfiguredFactoryFailure { get; set; }

        internal StartupSettingsSnapshot? LastConfiguredSettings { get; private set; }

        public IBMSPlayer CreateDefaultBmsPlayer()
        {
            sequence?.Add("factory-default");
            if (DefaultFactoryFailure != null)
            {
                throw DefaultFactoryFailure;
            }
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

        public void StopPlayback()
        {
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
        private readonly ManualResetEventSlim runtimeStarted;

        private readonly ManualResetEventSlim releaseRuntime;

        internal BlockingAudioDeviceTestRuntime(ManualResetEventSlim runtimeStarted, ManualResetEventSlim releaseRuntime)
        {
            this.runtimeStarted = runtimeStarted;
            this.releaseRuntime = releaseRuntime;
        }

        public AudioDeviceTestResult Run(AudioDeviceTestRequest request)
        {
            runtimeStarted.Set();
            releaseRuntime.Wait();
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

        public AudioDeviceTestResult Run(AudioDeviceTestRequest request) => run(request);
    }
}
