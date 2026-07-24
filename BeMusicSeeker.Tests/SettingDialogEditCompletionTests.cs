using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.ExceptionServices;
using System.Threading;
using System.Threading.Tasks;
using BeMusicSeeker.Models;
using BeMusicSeeker.Properties;
using BeMusicSeeker.ViewModels;
using BeMusicSeeker.Views;
using BeMusicSeeker.Views.Dialogs;
using Livet;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Threading;
using Ribbit.Media;
using Ribbit.Media.Audio;

namespace BeMusicSeeker.Tests;

[TestClass]
[DoNotParallelize]
public sealed class SettingDialogEditCompletionTests
{
    [TestMethod]
    public void PlaylistDialogs_UsePlaylistWorkspaceOwnerComposition()
    {
        RunOnStaDispatcherThread(() =>
        {
            MainWindowViewModel viewModel = MainWindowViewModelTestFactory.Create();
            var settingDialog = new SettingDialog
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
        RunOnStaDispatcherThread(() =>
        {
            string root = CreateTemporaryRoot();
            try
            {
                var settingsSession = new CountingSettingsEditSession(CreateValidStandaloneSettings(root));
                var player = new RecordingPlaybackPlayer();
                MainWindowViewModel viewModel = new ApplicationComposition(
                        settingsEditSession: settingsSession,
                        defaultBmsPlayerFactory: () => player,
                        uiDispatcherProvider: () => Dispatcher.CurrentDispatcher)
                    .CreateMainWindowViewModel();
                var settingDialog = new SettingDialog
                {
                    DataContext = viewModel.SettingDialog,
                    PlaybackPanel = viewModel.PlaybackPanel
                };
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
                owner,
                new TestFirstStartupStatePort(),
                owner.PlaylistWorkspace,
                owner.PlaylistWorkspace,
                owner.PlayHistory,
                runtime,
                new TestSettingsDialogPlayerFactoryPort(),
                new TestSettingsDialogPlaybackRuntimePort(),
                owner.Lr2SongDbSyncWorkflow,
                settingsSession.Reload,
                settingsSession.Save,
                settingsSession,
                reloadFileDiff: () =>
                {
                    reloadCount++;
                    sequence.Add("reload");
                    return Task.CompletedTask;
                },
                schemaDialogs: dialogs);

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
                owner,
                new TestFirstStartupStatePort(),
                owner.PlaylistWorkspace,
                owner.PlaylistWorkspace,
                owner.PlayHistory,
                owner.LibraryFolderTree,
                new TestSettingsDialogPlayerFactoryPort(),
                new TestSettingsDialogPlaybackRuntimePort(),
                owner.Lr2SongDbSyncWorkflow,
                settingsSession.Reload,
                settingsSession.Save,
                settingsSession,
                reloadFileDiff: () => Task.CompletedTask,
                schemaDialogs: dialogs);

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
    public async Task RequestRemoveBmsSearchRootAsync_BlankOrMissingRoot_DoesNotShowConfirmation()
    {
        string root = CreateTemporaryRoot();
        string missing = Path.Combine(root, "missing");
        try
        {
            var settingsSession = new CountingSettingsEditSession(CreateValidStandaloneSettings(root));
            var dialogs = new RecordingRootDialogService
            {
                ConfirmationResult = UiDialogResult.FromMessageBoxResult(MessageBoxResult.OK)
            };
            MainWindowViewModel owner = MainWindowViewModelTestFactory.Create();
            var dialog = new SettingsDialogViewModel(
                owner,
                new TestFirstStartupStatePort(),
                owner.PlaylistWorkspace,
                owner.PlaylistWorkspace,
                owner.PlayHistory,
                owner.LibraryFolderTree,
                new TestSettingsDialogPlayerFactoryPort(),
                new TestSettingsDialogPlaybackRuntimePort(),
                owner.Lr2SongDbSyncWorkflow,
                settingsSession.Reload,
                settingsSession.Save,
                settingsSession,
                reloadFileDiff: () => Task.CompletedTask,
                schemaDialogs: dialogs);

            await dialog.RequestRemoveBmsSearchRootAsync(string.Empty);
            await dialog.RequestRemoveBmsSearchRootAsync(missing);

            Assert.AreEqual(0, dialogs.ConfirmationCount);
            Assert.AreEqual(0, settingsSession.SaveCount);
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
                owner,
                new TestFirstStartupStatePort(),
                owner.PlaylistWorkspace,
                owner.PlaylistWorkspace,
                owner.PlayHistory,
                owner.LibraryFolderTree,
                new TestSettingsDialogPlayerFactoryPort(),
                new TestSettingsDialogPlaybackRuntimePort(),
                owner.Lr2SongDbSyncWorkflow,
                settingsSession.Reload,
                settingsSession.Save,
                settingsSession,
                reloadFileDiff: () => Task.CompletedTask,
                schemaDialogs: dialogs);

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
            var dialog = new SettingsDialogViewModel(
                owner,
                new TestFirstStartupStatePort(),
                owner.PlaylistWorkspace,
                owner.PlaylistWorkspace,
                owner.PlayHistory,
                runtime,
                new TestSettingsDialogPlayerFactoryPort(),
                new TestSettingsDialogPlaybackRuntimePort(),
                owner.Lr2SongDbSyncWorkflow,
                settingsSession.Reload,
                settingsSession.Save,
                settingsSession,
                reloadFileDiff: () =>
                {
                    reloadCount++;
                    sequence.Add("reload");
                    return Task.CompletedTask;
                },
                schemaDialogs: dialogs);
            var config = new BeMusicSeeker.Models.LR2.LR2Config(configPath);
            config.AddBMSSearchDirectories([bmsRoot, otherRoot]);
            typeof(SettingsDialogViewModel)
                .GetField("lr2ConfigValue", BindingFlags.Instance | BindingFlags.NonPublic)!
                .SetValue(dialog, config);

            await dialog.RequestRemoveBmsSearchRootAsync(bmsRoot);

            Assert.AreEqual(1, dialogs.ConfirmationCount);
            CollectionAssert.DoesNotContain(config.GetBMSSearchDirectories().ToArray(), bmsRoot);
            CollectionAssert.Contains(config.GetBMSSearchDirectories().ToArray(), otherRoot);
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
                owner,
                new TestFirstStartupStatePort(),
                owner.PlaylistWorkspace,
                owner.PlaylistWorkspace,
                owner.PlayHistory,
                owner.LibraryFolderTree,
                new TestSettingsDialogPlayerFactoryPort(),
                new TestSettingsDialogPlaybackRuntimePort(),
                owner.Lr2SongDbSyncWorkflow,
                settingsSession.Reload,
                settingsSession.Save,
                settingsSession,
                reloadFileDiff: () => Task.CompletedTask,
                schemaDialogs: dialogs);

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
            MainWindowViewModel owner = MainWindowViewModelTestFactory.Create();
            var dialog = new SettingsDialogViewModel(
                owner,
                new TestFirstStartupStatePort(),
                owner.PlaylistWorkspace,
                owner.PlaylistWorkspace,
                owner.PlayHistory,
                owner.LibraryFolderTree,
                new TestSettingsDialogPlayerFactoryPort(),
                new TestSettingsDialogPlaybackRuntimePort(),
                owner.Lr2SongDbSyncWorkflow,
                settingsSession.Reload,
                settingsSession.Save,
                settingsSession,
                reloadFileDiff: () => Task.CompletedTask,
                schemaDialogs: dialogs);
            var config = new BeMusicSeeker.Models.LR2.LR2Config(configPath);
            config.AddBMSSearchDirectories([bmsRoot, otherRoot]);
            typeof(SettingsDialogViewModel)
                .GetField("lr2ConfigValue", BindingFlags.Instance | BindingFlags.NonPublic)!
                .SetValue(dialog, config);
            SetPrivateField(config, "_configPath", Path.Combine(root, "missing", "config.xml"));

            await dialog.RequestRemoveBmsSearchRootAsync(bmsRoot);

            Assert.AreEqual(1, dialogs.ConfirmationCount);
            Assert.AreEqual(1, dialogs.MessageCount);
            CollectionAssert.Contains(config.GetBMSSearchDirectories().ToArray(), bmsRoot);
            CollectionAssert.Contains(config.GetBMSSearchDirectories().ToArray(), otherRoot);
            Assert.AreEqual(0, settingsSession.SaveCount);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
            Directory.Delete(installRoot, recursive: true);
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
                new TestFirstStartupStatePort(),
                viewModel.PlaylistWorkspace,
                viewModel.PlaylistWorkspace,
                viewModel.PlayHistory,
                viewModel.LibraryFolderTree,
                new TestSettingsDialogPlayerFactoryPort(),
                new TestSettingsDialogPlaybackRuntimePort(),
                viewModel.Lr2SongDbSyncWorkflow,
                settingsSession.Reload,
                settingsSession.Save,
                settingsSession,
                reloadFileDiff: () => Task.CompletedTask,
                audioDeviceTestWorkflow: workflow);
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

            InvokePrivateMethod(viewModel, "SetStartupUiInteractionBlocked", true);
            Assert.IsFalse(dialog.CanRequestLr2SongDbSyncDataResync);
            InvokePrivateMethod(viewModel, "SetStartupUiInteractionBlocked", false);
            Assert.IsTrue(dialog.CanRequestLr2SongDbSyncDataResync);

            await dialog.RequestLr2SongDbSyncAsync();
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [TestMethod]
    public void SettingDialogOkClick_AwaitsOwnerCompletionBeforeClosing()
    {
        RunOnStaDispatcherThread(() =>
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
                var settingDialog = new SettingDialog
                {
                    DataContext = settingDialogViewModel
                };
                Button button = (Button)typeof(SettingDialog)
                    .GetField("buttonOK", BindingFlags.Instance | BindingFlags.NonPublic)!
                    .GetValue(settingDialog)!;
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
            Assert.IsFalse(viewModel.IsStartupUiInteractionBlocked);
            Assert.IsFalse(viewModel.IsInitializationCompleted);
            Assert.IsFalse(viewModel.HasActiveLibraryProfile);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [TestMethod]
    public async Task ApplySettingsAsync_InitialSettings_SavesClosesAndInitializes()
    {
        string root = CreateTemporaryRoot();
        try
        {
            var settingsSession = new CountingSettingsEditSession(CreateValidStandaloneSettings(root));
            var sequence = new List<string>();
            using var initializationStarted = new ManualResetEventSlim();
            var initializationRelease = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            int initializeCount = 0;
            MainWindowViewModel viewModel = CreateViewModel(
                settingsSession,
                firstStartup: false,
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
                });
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
                new[] { "save", "initialize-start", "initialize-completed", "close" },
                sequence);
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

    private static MainWindowViewModel CreateViewModel(
        CountingSettingsEditSession settingsSession,
        bool firstStartup,
        Func<MainWindowViewModel, Task<bool>>? initializeOwner = null,
        Func<MainWindowViewModel, Task>? reloadScoresOnly = null,
        Action<Exception>? reportSettingsApplyFailure = null,
        Func<MainWindowViewModel, Task>? reloadFileDiff = null,
        ISettingsDialogPlayerFactoryPort? playerFactoryPort = null,
        ISettingsDialogPlaybackRuntimePort? playbackRuntimePort = null)
    {
        var composition = new ApplicationComposition(
            firstStartupProvider: () => firstStartup,
            completeFirstStartup: () => { },
            reloadSettings: settingsSession.Reload,
            saveSettings: settingsSession.Save,
            settingsEditSession: settingsSession,
            reportSettingsApplyFailure: reportSettingsApplyFailure ?? (_ => { }),
            reloadFileDiff: reloadFileDiff,
            uiDispatcherProvider: () => Dispatcher.CurrentDispatcher);
        MainWindowViewModel viewModel = composition.CreateMainWindowViewModel();
        if (initializeOwner != null || reloadScoresOnly != null)
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
                        : () => reloadScoresOnly(viewModel)),
                new TestFirstStartupStatePort(firstStartup),
                viewModel.PlaylistWorkspace,
                viewModel.PlaylistWorkspace,
                viewModel.PlayHistory,
                viewModel.LibraryFolderTree,
                new TestSettingsDialogPlayerFactoryPort(),
                new TestSettingsDialogPlaybackRuntimePort(),
                viewModel.Lr2SongDbSyncWorkflow,
                settingsSession.Reload,
                settingsSession.Save,
                settingsSession,
                reloadFileDiff: reloadFileDiff == null
                    ? () => Task.CompletedTask
                    : () => reloadFileDiff(viewModel),
                reportApplyFailure: reportSettingsApplyFailure ?? (_ => { }));
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
        var dialog = new SettingsDialogViewModel(
            owner,
            new TestFirstStartupStatePort(),
            owner.PlaylistWorkspace,
            owner.PlaylistWorkspace,
            owner.PlayHistory,
            runtime,
            new TestSettingsDialogPlayerFactoryPort(),
            new TestSettingsDialogPlaybackRuntimePort(),
            owner.Lr2SongDbSyncWorkflow,
            settingsSession.Reload,
            settingsSession.Save,
            settingsSession,
            reloadFileDiff: reloadFileDiff,
            schemaDialogs: dialogs);
        var config = new BeMusicSeeker.Models.LR2.LR2Config(configPath);
        config.AddBMSSearchDirectories([bmsRoot, otherRoot]);
        SetPrivateField(dialog, "lr2ConfigValue", config);
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
        var tables = new BMSPlaylist(databasePath)
        {
            BMSTables = new Livet.DispatcherCollection<BMSTable>(
                new System.Collections.ObjectModel.ObservableCollection<BMSTable>(),
                Dispatcher.CurrentDispatcher)
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

    private static void RunOnStaDispatcherThread(Action action)
    {
        Exception? exception = null;
        var thread = new Thread(() =>
        {
            try
            {
                action();
            }
            catch (Exception ex)
            {
                exception = ex;
            }
            finally
            {
                Dispatcher dispatcher = Dispatcher.FromThread(Thread.CurrentThread);
                if (dispatcher != null && !dispatcher.HasShutdownStarted)
                {
                    dispatcher.InvokeShutdown();
                }
            }
        })
        {
            IsBackground = true
        };
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        thread.Join();
        if (exception != null)
        {
            ExceptionDispatchInfo.Capture(exception).Throw();
        }
    }

    private sealed class RecordingRootDialogService : IUiDialogService
    {
        internal UiDialogResult ConfirmationResult { get; set; } = UiDialogResult.FromMessageBoxResult(MessageBoxResult.Cancel);

        internal int ConfirmationCount { get; private set; }

        internal int MessageCount { get; private set; }

        internal string LastMessageText { get; private set; } = string.Empty;

        public Task<UiDialogResult> ShowMessageAsync(UiMessageRequest request, CancellationToken cancellationToken = default)
        {
            MessageCount++;
            LastMessageText = request.MessageBoxText;
            return Task.FromResult(UiDialogResult.FromMessageBoxResult(MessageBoxResult.OK));
        }

        public Task<UiDialogResult> ConfirmAsync(UiConfirmationRequest request, CancellationToken cancellationToken = default)
        {
            ConfirmationCount++;
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

    private sealed class CountingSettingsEditSession : ISettingsEditSession
    {
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

    private sealed class RecordingPlaybackPlayer : IBMSPlayer
    {
        public event PropertyChangedEventHandler? PropertyChanged;

        public string ExePath { get; set; } = string.Empty;

        public IntPtr ParentHandle { private get; set; }

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

        public void PlayStart(string bmsFilePath, Action<object, EventArgs>? onExitEventHandler = null)
        {
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

        public IBMSPlayer CreateDefaultBmsPlayer()
        {
            sequence?.Add("factory-default");
            if (DefaultFactoryFailure != null)
            {
                throw DefaultFactoryFailure;
            }
            return new RecordingPlaybackPlayer();
        }

        public IBMSPlayer CreateBmsPlayerForSettings(Settings settings)
        {
            sequence?.Add("factory-configured");
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

        public void ApplyPlayerSettings(IBMSPlayer replacementPlayer)
        {
            ApplyCount++;
            LastReplacementPlayer = replacementPlayer;
            sequence?.Add("apply");
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
            return new AudioDeviceTestResult(
                BassAudioPlayer.DeviceDriver.DIRECT_SOUND,
                request.PlayerDevice,
                request.PlayerDeviceName,
                request.PlayerSampleRate,
                request.PlayerFormat,
                0);
        }
    }
}
