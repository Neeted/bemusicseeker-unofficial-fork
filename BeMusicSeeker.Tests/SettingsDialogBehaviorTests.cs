using System;
using System.Collections;
using System.Collections.Generic;
using System.Configuration;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using BeMusicSeeker.Models;
using BeMusicSeeker.Models.BmsLibraryInternal;
using BeMusicSeeker.Models.LR2;
using BeMusicSeeker.Models.Utils;
using BeMusicSeeker.Properties;
using BeMusicSeeker.ViewModels;
using BeMusicSeeker.Views.Dialogs;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Ribbit.Media.Audio;

namespace BeMusicSeeker.Tests;

[TestClass]
public sealed class SettingsDialogBehaviorTests
{
    [TestMethod]
    public void TableListUrlGetterUsesCurrentDefaultWithoutMutatingInvalidDraft()
    {
        Settings draft = CreateStandaloneSettings(
            root: @"C:\settings-behavior\invalid-table-list-draft",
            installDirectory: @"C:\settings-behavior\invalid-table-list-draft");
        draft.TableListURL = null;

        using SettingsDialogHarness harness = SettingsDialogHarness.Create(draft);

        Uri actual = harness.Dialog.TableListURL;

        Assert.AreEqual(new Uri(Settings.DefaultTableListUrl), actual);
        Assert.IsNull(harness.Session.Values.TableListURL);
        Assert.AreEqual(0, harness.Session.SaveCount);
        Assert.IsFalse(harness.Dialog.HasPendingSettingChanges());
    }

    [TestMethod]
    public async Task RightClickRestoreDefaultsIsDraftOnlyUntilSave()
    {
        const string originalJson = """
        {"webActions":[{"id":"custom","name":"Custom","urlTemplate":"https://example.test/{md5}","enabled":true,"chartKind":"All"}],"programActions":[{"id":"viewer","name":"Viewer","executablePath":"C:\\Tools\\viewer.exe","argumentTemplate":"{filePath}","enabled":true}]}
        """;

        Settings cancelSettings = CreateStandaloneSettings(
            @"C:\settings-behavior\right-click-cancel",
            @"C:\settings-behavior\right-click-cancel");
        cancelSettings.RightClickActionsJson = originalJson;
        using (SettingsDialogHarness cancelHarness = SettingsDialogHarness.Create(cancelSettings))
        {
            cancelHarness.Dialog.RightClickActionSettingsEditor.RestoreDefaults();

            Assert.IsTrue(cancelHarness.Dialog.RightClickActionSettingsEditor.IsDirty);
            Assert.AreEqual(originalJson, cancelHarness.Session.Values.RightClickActionsJson);

            cancelHarness.Dialog.CancelCommand.Execute();

            Assert.AreEqual(originalJson, cancelHarness.Session.Values.RightClickActionsJson);
            Assert.AreEqual(0, cancelHarness.Session.SaveCount);
            Assert.IsFalse(cancelHarness.Dialog.RightClickActionSettingsEditor.IsDirty);
        }

        Settings saveSettings = CreateStandaloneSettings(
            @"C:\settings-behavior\right-click-save",
            @"C:\settings-behavior\right-click-save");
        saveSettings.RightClickActionsJson = originalJson;
        using (SettingsDialogHarness saveHarness = SettingsDialogHarness.Create(saveSettings))
        {
            saveHarness.Dialog.RightClickActionSettingsEditor.RestoreDefaults();

            await saveHarness.Dialog.SaveSettings();

            Assert.AreEqual(RightClickActionSettingsDefaults.SerializedJson, saveHarness.Session.Values.RightClickActionsJson);
            Assert.AreEqual(1, saveHarness.Session.SaveCount);
            Assert.IsFalse(saveHarness.Dialog.RightClickActionSettingsEditor.IsDirty);
        }
    }

    [TestMethod]
    public async Task SettingDialogOperationModeChange_ConfirmsAndRestartsAfterInitialization()
    {
        using (SettingsDialogHarness inactiveHarness = SettingsDialogHarness.Create(
            CreateStandaloneSettings(
                @"C:\settings-behavior\inactive-root",
                @"C:\settings-behavior\inactive-root")))
        {
            inactiveHarness.Dialog.OperationModeLR2DB = true;

            Assert.IsTrue(inactiveHarness.Dialog.OperationModeLR2DB);
            Assert.IsFalse(inactiveHarness.Session.Values.OperationModeLR2DB);
            Assert.AreEqual(0, inactiveHarness.Dialogs.ConfirmationCount);
            Assert.AreEqual(0, inactiveHarness.Session.SaveCount);
            Assert.AreEqual(0, inactiveHarness.Lifetime.RestartCount);
        }

        using (SettingsDialogHarness acceptedHarness = SettingsDialogHarness.Create(
            CreateStandaloneSettings(
                @"C:\settings-behavior\accepted-root",
                @"C:\settings-behavior\accepted-root"),
            activeLibraryProfile: true))
        {
            var sequence = new List<string>();
            acceptedHarness.AttachSequence(sequence);
            acceptedHarness.Dialogs.ConfirmationResult = UiDialogResult.FromMessageBoxResult(MessageBoxResult.OK);

            acceptedHarness.Dialog.OperationModeLR2DB = true;

            CollectionAssert.AreEqual(new[] { "confirm", "reload", "save", "restart" }, sequence);
            Assert.AreEqual(Resources.Confirm_RestartForOperationModeChange, acceptedHarness.Dialogs.LastConfirmationRequest?.MessageBoxText);
            Assert.AreEqual(1, acceptedHarness.Dialogs.ConfirmationCount);
            Assert.AreEqual(1, acceptedHarness.Session.SaveCount);
            Assert.AreEqual(1, acceptedHarness.Lifetime.RestartCount);
            Assert.IsTrue(acceptedHarness.Session.Values.OperationModeLR2DB);
        }

        using (SettingsDialogHarness rejectedHarness = SettingsDialogHarness.Create(
            CreateStandaloneSettings(
                @"C:\settings-behavior\rejected-root",
                @"C:\settings-behavior\rejected-root"),
            activeLibraryProfile: true))
        {
            var sequence = new List<string>();
            rejectedHarness.AttachSequence(sequence);
            rejectedHarness.Dialogs.ConfirmationResult = UiDialogResult.FromMessageBoxResult(MessageBoxResult.Cancel);

            rejectedHarness.Dialog.OperationModeLR2DB = true;

            CollectionAssert.AreEqual(new[] { "confirm" }, sequence);
            Assert.IsFalse(rejectedHarness.Dialog.OperationModeLR2DB);
            Assert.IsFalse(rejectedHarness.Session.Values.OperationModeLR2DB);
            Assert.AreEqual(0, rejectedHarness.Session.SaveCount);
            Assert.AreEqual(0, rejectedHarness.Lifetime.RestartCount);
        }

        using (SettingsDialogHarness failedRestartHarness = SettingsDialogHarness.Create(
            CreateStandaloneSettings(
                @"C:\settings-behavior\restart-failure-root",
                @"C:\settings-behavior\restart-failure-root"),
            activeLibraryProfile: true))
        {
            var sequence = new List<string>();
            failedRestartHarness.AttachSequence(sequence);
            failedRestartHarness.Dialogs.ConfirmationResult = UiDialogResult.FromMessageBoxResult(MessageBoxResult.OK);
            failedRestartHarness.Dialogs.MessageResult = UiDialogResult.FromMessageBoxResult(MessageBoxResult.OK);
            failedRestartHarness.Dialogs.MessageCompletionSource =
                new TaskCompletionSource<UiDialogResult>(TaskCreationOptions.RunContinuationsAsynchronously);
            failedRestartHarness.Lifetime.RestartFailure = new InvalidOperationException("restart failed");

            failedRestartHarness.Dialog.OperationModeLR2DB = true;

            CollectionAssert.AreEqual(
                new[] { "confirm", "reload", "save", "restart", "notify" },
                sequence);
            Assert.AreEqual(0, failedRestartHarness.Lifetime.ShutdownCount);
            Assert.AreEqual(1, failedRestartHarness.Dialogs.MessageCount);
            StringAssert.Contains(failedRestartHarness.Dialogs.LastMessageText, Resources.Error_RestartApplicationFailed);

            failedRestartHarness.Dialogs.MessageCompletionSource!.SetResult(
                UiDialogResult.FromMessageBoxResult(MessageBoxResult.OK));
            await failedRestartHarness.Lifetime.ShutdownObserved.Task;

            CollectionAssert.AreEqual(
                new[] { "confirm", "reload", "save", "restart", "notify", "shutdown" },
                sequence);
            Assert.AreEqual(1, failedRestartHarness.Lifetime.ShutdownCount);
            Assert.AreEqual(1, failedRestartHarness.Session.SaveCount);
        }

        var failedNotificationHarnessReportedFailures = new List<Exception>();
        using (SettingsDialogHarness failedNotificationHarness = SettingsDialogHarness.Create(
            CreateStandaloneSettings(
                @"C:\settings-behavior\restart-notification-failure-root",
                @"C:\settings-behavior\restart-notification-failure-root"),
            activeLibraryProfile: true,
            reportApplyFailure: exception => failedNotificationHarnessReportedFailures.Add(exception)))
        {
            var sequence = new List<string>();
            failedNotificationHarness.AttachSequence(sequence);
            failedNotificationHarness.Dialogs.ConfirmationResult = UiDialogResult.FromMessageBoxResult(MessageBoxResult.OK);
            var dialogFailure = new InvalidOperationException("restart notification failed");
            failedNotificationHarness.Dialogs.MessageFailure = dialogFailure;
            failedNotificationHarness.Lifetime.RestartFailure = new InvalidOperationException("restart failed");

            failedNotificationHarness.Dialog.OperationModeLR2DB = true;

            await failedNotificationHarness.Lifetime.ShutdownObserved.Task;

            CollectionAssert.AreEqual(
                new[] { "confirm", "reload", "save", "restart", "notify", "shutdown" },
                sequence);
            Assert.AreEqual(1, failedNotificationHarness.Lifetime.ShutdownCount);
            Assert.AreEqual(1, failedNotificationHarnessReportedFailures.Count);
            Assert.AreSame(dialogFailure, failedNotificationHarnessReportedFailures[0]);
        }
    }

    [TestMethod]
    public async Task SearchRootChanges_UpdateRuntimeSearchTargetsBeforeFileDiffReload()
    {
        string standaloneRoot = CreateTemporaryRoot("settings-picker-standalone");
        string standaloneAddedRoot = Path.Combine(standaloneRoot, "added");
        Directory.CreateDirectory(standaloneAddedRoot);
        try
        {
            using SettingsDialogHarness harness = SettingsDialogHarness.Create(
                CreateStandaloneSettings(standaloneRoot, standaloneRoot),
                libraryAttached: true);
            var sequence = new List<string>();
            harness.AttachSequence(sequence);
            harness.State.ReloadFileDiffHandler = () =>
            {
                sequence.Add("reload");
                return Task.CompletedTask;
            };
            harness.SyncRuntime.IsLr2ModeEnabledValue = true;
            harness.SyncRuntime.QueueObserved = () => sequence.Add("sync");
            harness.State.ReloadFileDiffWorkflowOwner = new FileDiffReloadWorkflowOwner(
                _ =>
                {
                    sequence.Add("reload");
                    return Task.CompletedTask;
                },
                harness.SyncWorkflow,
                _ => { });

            await harness.Dialog.AddBmsSearchRootPathFromMainWindowPicker(
                standaloneAddedRoot + Path.DirectorySeparatorChar);

            CollectionAssert.AreEqual(new[] { "save", "apply", "reload", "sync" }, sequence);
            CollectionAssert.AreEqual(
                new[] { standaloneRoot, standaloneAddedRoot },
                harness.SearchRoots.LastSearchTargets.ToArray());
            CollectionAssert.AreEqual(
                new[] { standaloneRoot, standaloneAddedRoot },
                SettingsDialogViewModel.DeserializeStandaloneBmsRootPaths(
                    harness.Session.Values.StandaloneBmsRootPaths).ToArray());
            Assert.AreEqual(1, harness.Session.SaveCount);
            Assert.AreEqual(1, harness.State.FileDiffReloadCount);
        }
        finally
        {
            TryDeleteDirectory(standaloneRoot);
        }

        string lr2Root = CreateTemporaryRoot("settings-picker-lr2");
        try
        {
            (string songDbPath, string configPath, string bmsRoot) = CreateValidLr2Layout(lr2Root);
            string addedRoot = Path.Combine(lr2Root, "BMS-added");
            Directory.CreateDirectory(addedRoot);
            Settings settings = CreateLr2Settings(lr2Root, bmsRoot);
            settings.LR2SongDBPath = songDbPath;
            settings.LR2ConfigXmlPath = configPath;
            using SettingsDialogHarness harness = SettingsDialogHarness.Create(settings, libraryAttached: true);
            var sequence = new List<string>();
            harness.AttachSequence(sequence);
            harness.SearchRoots.ApplyObserved = _ =>
            {
                var persistedConfig = new BeMusicSeeker.Models.LR2.LR2Config(configPath);
                CollectionAssert.Contains(persistedConfig.GetBMSSearchDirectories(), addedRoot);
            };
            harness.State.ReloadFileDiffHandler = () =>
            {
                sequence.Add("reload");
                return Task.CompletedTask;
            };
            harness.SyncRuntime.IsLr2ModeEnabledValue = true;
            harness.SyncRuntime.QueueObserved = () => sequence.Add("sync");
            harness.State.ReloadFileDiffWorkflowOwner = new FileDiffReloadWorkflowOwner(
                _ =>
                {
                    sequence.Add("reload");
                    return Task.CompletedTask;
                },
                harness.SyncWorkflow,
                _ => { });

            await harness.Dialog.AddBmsSearchRootPathFromMainWindowPicker(addedRoot);

            CollectionAssert.AreEqual(new[] { "apply", "reload", "sync" }, sequence);
            CollectionAssert.AreEqual(
                new[] { bmsRoot, addedRoot },
                harness.SearchRoots.LastSearchTargets.ToArray());
            Assert.AreEqual(0, harness.Session.SaveCount);
            Assert.AreEqual(1, harness.State.FileDiffReloadCount);
        }
        finally
        {
            TryDeleteDirectory(lr2Root);
        }
    }

    [TestMethod]
    public async Task SearchRootChanges_SaveAndApplyFailuresDoNotReload()
    {
        string saveFailureRoot = CreateTemporaryRoot("settings-picker-save-failure");
        string saveFailureAddedRoot = Path.Combine(saveFailureRoot, "added");
        Directory.CreateDirectory(saveFailureAddedRoot);
        try
        {
            using SettingsDialogHarness harness = SettingsDialogHarness.Create(
                CreateStandaloneSettings(saveFailureRoot, saveFailureRoot),
                libraryAttached: true);
            var sequence = new List<string>();
            harness.AttachSequence(sequence);
            harness.Session.SaveFailure = new IOException("settings save failed");
            harness.State.ReloadFileDiffHandler = () =>
            {
                sequence.Add("reload");
                return Task.CompletedTask;
            };

            await Assert.ThrowsExceptionAsync<IOException>(() =>
                harness.Dialog.AddBmsSearchRootPathFromMainWindowPicker(saveFailureAddedRoot));

            CollectionAssert.AreEqual(new[] { "save" }, sequence);
            Assert.AreEqual(0, harness.SearchRoots.ApplyCount);
            Assert.AreEqual(0, harness.State.FileDiffReloadCount);
        }
        finally
        {
            TryDeleteDirectory(saveFailureRoot);
        }

        string applyFailureRoot = CreateTemporaryRoot("settings-picker-apply-failure");
        string applyFailureAddedRoot = Path.Combine(applyFailureRoot, "added");
        Directory.CreateDirectory(applyFailureAddedRoot);
        try
        {
            using SettingsDialogHarness harness = SettingsDialogHarness.Create(
                CreateStandaloneSettings(applyFailureRoot, applyFailureRoot),
                libraryAttached: true);
            var sequence = new List<string>();
            harness.AttachSequence(sequence);
            harness.SearchRoots.ApplyFailure = new InvalidOperationException("runtime apply failed");
            harness.State.ReloadFileDiffHandler = () =>
            {
                sequence.Add("reload");
                return Task.CompletedTask;
            };

            await Assert.ThrowsExceptionAsync<InvalidOperationException>(() =>
                harness.Dialog.AddBmsSearchRootPathFromMainWindowPicker(applyFailureAddedRoot));

            CollectionAssert.AreEqual(new[] { "save", "apply" }, sequence);
            Assert.AreEqual(1, harness.Session.SaveCount);
            Assert.AreEqual(1, harness.SearchRoots.ApplyCount);
            Assert.AreEqual(0, harness.State.FileDiffReloadCount);
        }
        finally
        {
            TryDeleteDirectory(applyFailureRoot);
        }

        string lr2SaveFailureRoot = CreateTemporaryRoot("settings-picker-lr2-save-failure");
        try
        {
            (string _, string configPath, string bmsRoot) = CreateValidLr2Layout(lr2SaveFailureRoot);
            string addedRoot = Path.Combine(lr2SaveFailureRoot, "added");
            Directory.CreateDirectory(addedRoot);
            Settings settings = CreateLr2Settings(lr2SaveFailureRoot, bmsRoot);
            settings.LR2ConfigXmlPath = configPath;
            using SettingsDialogHarness harness = SettingsDialogHarness.Create(settings, libraryAttached: true);
            var sequence = new List<string>();
            harness.AttachSequence(sequence);
            string configDirectory = Path.GetDirectoryName(configPath)!;
            Directory.Delete(configDirectory, recursive: true);

            await Assert.ThrowsExceptionAsync<DirectoryNotFoundException>(() =>
                harness.Dialog.AddBmsSearchRootPathFromMainWindowPicker(addedRoot));

            CollectionAssert.AreEqual(Array.Empty<string>(), sequence);
            Assert.AreEqual(0, harness.SearchRoots.ApplyCount);
            Assert.AreEqual(0, harness.State.FileDiffReloadCount);
        }
        finally
        {
            TryDeleteDirectory(lr2SaveFailureRoot);
        }
    }

    [TestMethod]
    public async Task SearchRootChanges_ReloadAndLr2SyncFailuresPropagateWithoutLaterQueue()
    {
        string root = CreateTemporaryRoot("settings-picker-reload-failure");
        string addedRoot = Path.Combine(root, "added");
        Directory.CreateDirectory(addedRoot);
        try
        {
            using SettingsDialogHarness harness = SettingsDialogHarness.Create(
                CreateStandaloneSettings(root, root),
                libraryAttached: true);
            var sequence = new List<string>();
            harness.AttachSequence(sequence);
            harness.SyncRuntime.IsLr2ModeEnabledValue = true;
            var reloadFailure = new IOException("file diff reload failed");
            harness.State.ReloadFileDiffWorkflowOwner = new FileDiffReloadWorkflowOwner(
                _ =>
                {
                    sequence.Add("reload");
                    return Task.FromException(reloadFailure);
                },
                harness.SyncWorkflow,
                _ => { });

            IOException thrown = await Assert.ThrowsExceptionAsync<IOException>(() =>
                harness.Dialog.AddBmsSearchRootPathFromMainWindowPicker(addedRoot));

            Assert.AreSame(reloadFailure, thrown);
            CollectionAssert.AreEqual(new[] { "save", "apply", "reload" }, sequence);
            Assert.AreEqual(0, harness.SyncRuntime.QueueCount);
            Assert.IsTrue(harness.Dialog.IsFileDiffReloadPending);
        }
        finally
        {
            TryDeleteDirectory(root);
        }

        root = CreateTemporaryRoot("settings-picker-sync-failure");
        addedRoot = Path.Combine(root, "added");
        Directory.CreateDirectory(addedRoot);
        try
        {
            using SettingsDialogHarness harness = SettingsDialogHarness.Create(
                CreateStandaloneSettings(root, root),
                libraryAttached: true);
            var sequence = new List<string>();
            harness.AttachSequence(sequence);
            harness.SyncRuntime.IsLr2ModeEnabledValue = true;
            var syncFailure = new InvalidOperationException("LR2 sync failed");
            harness.SyncRuntime.QueueFailure = syncFailure;
            harness.SyncRuntime.QueueObserved = () => sequence.Add("sync");
            harness.State.ReloadFileDiffWorkflowOwner = new FileDiffReloadWorkflowOwner(
                _ =>
                {
                    sequence.Add("reload");
                    return Task.CompletedTask;
                },
                harness.SyncWorkflow,
                _ => { });

            InvalidOperationException thrown = await Assert.ThrowsExceptionAsync<InvalidOperationException>(() =>
                harness.Dialog.AddBmsSearchRootPathFromMainWindowPicker(addedRoot));

            Assert.AreSame(syncFailure, thrown);
            CollectionAssert.AreEqual(new[] { "save", "apply", "reload", "sync" }, sequence);
            Assert.AreEqual(1, harness.SyncRuntime.QueueCount);
            Assert.IsTrue(harness.Dialog.IsFileDiffReloadPending);
        }
        finally
        {
            TryDeleteDirectory(root);
        }
    }

    [TestMethod]
    public void Lr2PlaybackPlayer_IsIndependentFromLibraryOperationMode()
    {
        string root = CreateTemporaryRoot("settings-player-standalone");
        try
        {
            (string songDbPath, string configPath, _) = CreateValidLr2Layout(root);
            Settings values = CreateStandaloneSettings(root, root);
            values.OperationModeLR2DB = false;
            values.LR2RootPath = root;
            values.LR2SongDBPath = songDbPath;
            values.LR2ConfigXmlPath = configPath;
            values.UsePlayerLR2body = true;
            File.WriteAllBytes(Path.Combine(root, "LR2body.exe"), []);

            var composition = new ApplicationComposition(
                settingsEditSession: new DirectSettingsEditSession(values),
                defaultBmsPlayerFactory: () => throw new InvalidOperationException("default player fallback was used"),
                uiScheduler: new WpfUiScheduler(() => System.Windows.Threading.Dispatcher.CurrentDispatcher),
                applicationLifetime: TestApplicationContext.CreateLifetime(),
                cultureCatalog: TestApplicationContext.CreateCultureCatalog());

            IBMSPlayer player = composition.CreateBmsPlayerForSettings(
                StartupSettingsSnapshot.CreateCurrent(values));

            Assert.IsInstanceOfType(player, typeof(LR2body));
            Assert.AreEqual(Path.Combine(root, "LR2body.exe"), player.ExePath);
        }
        finally
        {
            TryDeleteDirectory(root);
        }
    }

    [TestMethod]
    public async Task StandaloneLr2bodyValidationRejectsInvalidExecutableAndConfigBeforeApply()
    {
        string root = CreateTemporaryRoot("settings-player-invalid-standalone");
        try
        {
            Settings values = CreateStandaloneSettings(root, root);
            values.OperationModeLR2DB = false;
            values.LR2RootPath = Path.Combine(root, "missing-lr2");
            values.LR2SongDBPath = Path.Combine(values.LR2RootPath, "LR2files", "Database", "song.db");
            values.LR2ConfigXmlPath = Path.Combine(values.LR2RootPath, "LR2files", "Config", "config.xml");
            values.UsePlayerLR2body = false;

            using SettingsDialogHarness harness = SettingsDialogHarness.Create(values, activeLibraryProfile: true);
            harness.Dialogs.MessageResult = UiDialogResult.FromMessageBoxResult(MessageBoxResult.OK);
            harness.Dialog.UsePlayerLR2body = true;

            Assert.IsFalse(harness.Dialog.CheckValidation(out string validationError));
            StringAssert.Contains(validationError, Resources.Error_InvalidLR2RootPath);
            StringAssert.Contains(
                validationError,
                string.Format(Resources.Error_LR2ExecutableNotFoundFormat, harness.Dialog.LR2bodyPath));

            await harness.Dialog.ApplySettingsAsync();

            Assert.AreEqual(0, harness.Session.SaveCount);
            Assert.AreEqual(0, harness.PlayerFactory.ConfiguredCreateCount);
            Assert.AreEqual(0, harness.PlayerFactory.DefaultCreateCount);
            Assert.AreEqual(0, harness.PlaybackRuntime.ApplyCount);
        }
        finally
        {
            TryDeleteDirectory(root);
        }
    }

    [TestMethod]
    public async Task StandaloneLr2bodyValidationRejectsMalformedExistingConfigBeforeApply()
    {
        string root = CreateTemporaryRoot("settings-player-malformed-config");
        try
        {
            (string songDbPath, _, string bmsRoot) = CreateValidLr2Layout(root);
            string malformedConfigPath = Path.Combine(root, "selected-malformed-config.xml");
            File.WriteAllText(malformedConfigPath, "<config>");
            File.WriteAllBytes(Path.Combine(root, "LR2body.exe"), []);

            Settings values = CreateStandaloneSettings(bmsRoot, root);
            values.OperationModeLR2DB = false;
            values.LR2RootPath = root;
            values.LR2SongDBPath = songDbPath;
            values.LR2ConfigXmlPath = malformedConfigPath;
            values.UsePlayerLR2body = false;

            using SettingsDialogHarness harness = SettingsDialogHarness.Create(values, activeLibraryProfile: true);
            harness.Dialogs.MessageResult = UiDialogResult.FromMessageBoxResult(MessageBoxResult.OK);
            harness.Dialog.UsePlayerLR2body = true;

            Assert.IsFalse(harness.Dialog.CheckValidation(out string validationError));
            StringAssert.Contains(validationError, Resources.Error_InvalidLR2SongDbOrConfigPath);

            await harness.Dialog.ApplySettingsAsync();

            Assert.AreEqual(0, harness.Session.SaveCount);
            Assert.AreEqual(0, harness.PlayerFactory.ConfiguredCreateCount);
            Assert.AreEqual(0, harness.PlayerFactory.DefaultCreateCount);
            Assert.AreEqual(0, harness.PlaybackRuntime.ApplyCount);
            Assert.AreEqual(malformedConfigPath, harness.Dialog.LR2ConfigXmlPath);
            Assert.IsTrue(harness.Dialog.UsePlayerLR2body);
            Assert.AreEqual(Resources.Error_InvalidLR2SongDbOrConfigPath, harness.Dialog.Lr2ConfigPathStatusText);
        }
        finally
        {
            TryDeleteDirectory(root);
        }
    }

    [DataTestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public void SettingDialogModeSpecificGetters_DoNotClearPersistedSettings(bool operationModeLr2Db)
    {
        Settings values = CreateStandaloneSettings(
            root: @"C:\settings-behavior\root",
            installDirectory: @"C:\settings-behavior\root");
        values.OperationModeLR2DB = operationModeLr2Db;
        values.LR2CustomFolderOutputBaseDir = @"C:\settings-behavior\custom-output";
        values.LR2CustomFolderOutputBaseDirRootType = @"C:\settings-behavior\custom-root-output";
        values.BMSInstallDir = @"C:\settings-behavior\install";

        using SettingsDialogHarness harness = SettingsDialogHarness.Create(values);
        SettingsDialogViewModel dialog = harness.Dialog;
        Settings sessionValues = harness.Session.Values;

        Assert.AreEqual(@"C:\settings-behavior\custom-output", dialog.LR2CustomFolderOutputDir);
        Assert.AreEqual(@"C:\settings-behavior\custom-root-output", dialog.LR2CustomFolderAsRootOutputDir);
        Assert.AreEqual(@"C:\settings-behavior\install", dialog.BMSInstallDir);

        // A validation-oriented read may not rewrite the session, even when the
        // selected operation mode does not use one of these paths.
        _ = dialog.LR2CustomFolderOutputDir;
        _ = dialog.LR2CustomFolderAsRootOutputDir;
        _ = dialog.BMSInstallDir;

        Assert.AreEqual(@"C:\settings-behavior\custom-output", sessionValues.LR2CustomFolderOutputBaseDir);
        Assert.AreEqual(@"C:\settings-behavior\custom-root-output", sessionValues.LR2CustomFolderOutputBaseDirRootType);
        Assert.AreEqual(@"C:\settings-behavior\install", sessionValues.BMSInstallDir);
        Assert.AreEqual(0, harness.Session.SaveCount);
        Assert.IsFalse(dialog.IsScoreReloadPending);
        Assert.IsFalse(dialog.IsFileDiffReloadPending);
        Assert.IsFalse(dialog.HasPendingSettingChanges());
    }

    [TestMethod]
    public void SettingDialogOperationModeRestartSave_SavesOnlyOperationMode()
    {
        using TemporaryDirectory beatorajaFixture = new("settings-restart-beatoraja");
        string persistedBeatorajaRoot = beatorajaFixture.Path;
        const string persistedBeatorajaPlayer = "persisted-player";
        string persistedBeatorajaScoreDb = Path.Combine(
            persistedBeatorajaRoot,
            "player",
            persistedBeatorajaPlayer,
            "score.db");
        Directory.CreateDirectory(Path.GetDirectoryName(persistedBeatorajaScoreDb)!);
        File.WriteAllText(Path.Combine(persistedBeatorajaRoot, "beatoraja.jar"), string.Empty);
        File.WriteAllText(Path.Combine(persistedBeatorajaRoot, "config_sys.json"), "{}");
        File.WriteAllText(persistedBeatorajaScoreDb, string.Empty);

        const string persistedBmsRoot = @"C:\settings-behavior\persisted-bms-root";
        string persistedStandaloneRoots = string.Join(
            Environment.NewLine,
            @"C:\settings-behavior\persisted-bms-root",
            @"C:\settings-behavior\persisted-bms-root-2");
        const string persistedInstallDirectory = @"C:\settings-behavior\persisted-install";
        const string persistedCustomOutput = @"C:\settings-behavior\persisted-custom-output";
        const string persistedCustomRootOutput = @"C:\settings-behavior\persisted-custom-root-output";
        const string persistedAdditionalOutput = @"C:\settings-behavior\persisted-additional-output";
        const string persistedLr2Root = @"C:\settings-behavior\persisted-lr2-root";
        const string persistedSongDb = @"C:\settings-behavior\persisted-song.db";
        const string persistedConfig = @"C:\settings-behavior\persisted-config.xml";
        const string persistedFolderNameFormat = "persisted-%TITLE%";
        Uri persistedTableListUrl = new("http://127.0.0.1:2/persisted-table.json");

        Settings persisted = CreateStandaloneSettings(
            root: persistedBmsRoot,
            installDirectory: persistedInstallDirectory);
        persisted.OperationModeLR2DB = false;
        persisted.BMSRootPath = persistedBmsRoot;
        persisted.StandaloneBmsRootPaths = persistedStandaloneRoots;
        persisted.BMSInstallDir = persistedInstallDirectory;
        persisted.LR2CustomFolderOutputBaseDir = persistedCustomOutput;
        persisted.LR2CustomFolderOutputBaseDirRootType = persistedCustomRootOutput;
        persisted.LR2CustomFolderAdditionalOutputBaseDirs = persistedAdditionalOutput;
        persisted.LR2RootPath = persistedLr2Root;
        persisted.LR2SongDBPath = persistedSongDb;
        persisted.LR2ConfigXmlPath = persistedConfig;
        persisted.BeatorajaRootPath = persistedBeatorajaRoot;
        persisted.BeatorajaPlayerId = persistedBeatorajaPlayer;
        persisted.BeatorajaScoreDbPath = persistedBeatorajaScoreDb;
        persisted.UseBeatorajaScoreDb = false;
        persisted.EnableBeatorajaBmtOutput = false;
        persisted.FolderNameFormat = persistedFolderNameFormat;
        persisted.TableListURL = persistedTableListUrl;
        persisted.PlayHistorySelectedDisplayTargetIdentity = "saved-identity";

        Settings draft = CreateStandaloneSettings(
            root: @"C:\settings-behavior\draft-bms-root",
            installDirectory: @"C:\settings-behavior\draft-install");
        draft.OperationModeLR2DB = false;
        draft.BMSRootPath = @"C:\settings-behavior\draft-bms-root";
        draft.StandaloneBmsRootPaths = string.Join(
            Environment.NewLine,
            @"C:\settings-behavior\draft-bms-root",
            @"C:\settings-behavior\draft-bms-root-2");
        draft.BMSInstallDir = @"C:\settings-behavior\draft-install";
        draft.LR2CustomFolderOutputBaseDir = @"C:\settings-behavior\draft-custom-output";
        draft.LR2CustomFolderOutputBaseDirRootType = @"C:\settings-behavior\draft-custom-root-output";
        draft.LR2CustomFolderAdditionalOutputBaseDirs = @"C:\settings-behavior\draft-additional-output";
        draft.LR2RootPath = @"C:\settings-behavior\draft-lr2-root";
        draft.LR2SongDBPath = @"C:\settings-behavior\draft-song.db";
        draft.LR2ConfigXmlPath = @"C:\settings-behavior\draft-config.xml";
        draft.BeatorajaRootPath = @"C:\settings-behavior\draft-beatoraja";
        draft.BeatorajaPlayerId = "draft-player";
        draft.BeatorajaScoreDbPath = @"C:\settings-behavior\draft-score.db";
        draft.UseBeatorajaScoreDb = true;
        draft.EnableBeatorajaBmtOutput = true;
        draft.FolderNameFormat = "draft-%TITLE%";
        draft.TableListURL = new Uri("http://127.0.0.1:3/draft-table.json");
        draft.PlayHistorySelectedDisplayTargetIdentity = "current-identity";

        using SettingsDialogHarness harness = SettingsDialogHarness.Create(
            persisted,
            session: new RecordingSettingsEditSession(persisted, draft));
        SettingsSemanticSnapshot persistedSnapshot = SettingsSemanticSnapshot.Capture(harness.Session.Values);
        harness.Session.SetDraft(draft);
        harness.Session.ClearCalls();
        harness.RuntimeCalls.BeginCapture();

        harness.Dialog.SaveOperationModeForRestart(operationMode: true);

        CollectionAssert.AreEqual(new[] { "reload", "save" }, harness.Session.Calls);
        Assert.AreEqual(0, harness.RuntimeCalls.Calls.Count);
        Assert.AreEqual(1, harness.Session.SaveCount);
        Assert.AreEqual(1, harness.Session.ReloadCount);
        Assert.IsTrue(harness.Session.Values.OperationModeLR2DB);
        Assert.AreEqual(persistedBmsRoot, harness.Session.Values.BMSRootPath);
        Assert.AreEqual(persistedStandaloneRoots, harness.Session.Values.StandaloneBmsRootPaths);
        Assert.AreEqual(persistedInstallDirectory, harness.Session.Values.BMSInstallDir);
        Assert.AreEqual(persistedCustomOutput, harness.Session.Values.LR2CustomFolderOutputBaseDir);
        Assert.AreEqual(persistedCustomRootOutput, harness.Session.Values.LR2CustomFolderOutputBaseDirRootType);
        Assert.AreEqual(persistedAdditionalOutput, harness.Session.Values.LR2CustomFolderAdditionalOutputBaseDirs);
        Assert.AreEqual(persistedLr2Root, harness.Session.Values.LR2RootPath);
        Assert.AreEqual(persistedSongDb, harness.Session.Values.LR2SongDBPath);
        Assert.AreEqual(persistedConfig, harness.Session.Values.LR2ConfigXmlPath);
        Assert.AreEqual(persistedBeatorajaRoot, harness.Session.Values.BeatorajaRootPath);
        Assert.AreEqual(persistedBeatorajaPlayer, harness.Session.Values.BeatorajaPlayerId);
        Assert.AreEqual(persistedBeatorajaScoreDb, harness.Session.Values.BeatorajaScoreDbPath);
        Assert.IsFalse(harness.Session.Values.UseBeatorajaScoreDb);
        Assert.IsFalse(harness.Session.Values.EnableBeatorajaBmtOutput);
        Assert.AreEqual(persistedFolderNameFormat, harness.Session.Values.FolderNameFormat);
        Assert.AreEqual(persistedTableListUrl, harness.Session.Values.TableListURL);
        Assert.AreEqual("current-identity", harness.Session.Values.PlayHistorySelectedDisplayTargetIdentity);
        Assert.AreEqual(0, harness.State.InitializeCount);
        Assert.AreEqual(0, harness.State.ScoreReloadCount);
        Assert.AreEqual(0, harness.State.FileDiffReloadCount);
        Assert.AreEqual(0, harness.Lifetime.RestartCount);
        Assert.AreEqual(0, harness.Dialogs.MessageCount);
        Assert.AreEqual(0, harness.Dialogs.ConfirmationCount);

        Assert.IsNotNull(harness.Session.SaveSnapshot);
        SettingsSemanticSnapshot saveSnapshot = harness.Session.SaveSnapshot!;
        CollectionAssert.AreEquivalent(
            new[]
            {
                nameof(Settings.OperationModeLR2DB),
                nameof(Settings.PlayHistorySelectedDisplayTargetIdentity)
            },
            saveSnapshot.GetDifferingPropertyNames(persistedSnapshot).ToArray(),
            "Unexpected save-time differences: "
                + string.Join(", ", saveSnapshot.GetDifferingPropertyNames(persistedSnapshot)));
        saveSnapshot.AssertSemanticallyEqualExcept(
            persistedSnapshot,
            nameof(Settings.OperationModeLR2DB),
            nameof(Settings.PlayHistorySelectedDisplayTargetIdentity));
        saveSnapshot.AssertPropertySemanticValue(nameof(Settings.OperationModeLR2DB), true);
        saveSnapshot.AssertPropertySemanticValue(
            nameof(Settings.PlayHistorySelectedDisplayTargetIdentity),
            "current-identity");
    }

    [TestMethod]
    public async Task SettingDialogReloadDecision_UsesExplicitSettingDiffs()
    {
        using (SettingsDialogHarness modeHarness = SettingsDialogHarness.Create(CreateStandaloneSettings(
            @"C:\settings-behavior\mode-root",
            @"C:\settings-behavior\mode-root")))
        {
            modeHarness.Dialog.OperationModeLR2DB = true;
            Assert.AreEqual(SettingsDialogViewModel.RestartMode.All, modeHarness.Dialog.IsNeedRestartForSaved());
        }

        using (SettingsDialogHarness scoreHarness = SettingsDialogHarness.Create(CreateStandaloneSettings(
            @"C:\settings-behavior\score-root",
            @"C:\settings-behavior\score-root")))
        {
            scoreHarness.Session.Values.BeatorajaScoreDbPath = @"C:\settings-behavior\score.db";
            Assert.AreEqual(SettingsDialogViewModel.RestartMode.ScoreOnly, scoreHarness.Dialog.IsNeedRestartForSaved());
        }

        string standaloneRoot = CreateTemporaryRoot("settings-reload-standalone");
        try
        {
            string secondRoot = Path.Combine(standaloneRoot, "second-root");
            Directory.CreateDirectory(secondRoot);
            using SettingsDialogHarness standaloneRootHarness = SettingsDialogHarness.Create(CreateStandaloneSettings(
                standaloneRoot,
                standaloneRoot));
            standaloneRootHarness.Dialog.AddStandaloneBmsRootPaths([secondRoot]);
            Assert.AreEqual(SettingsDialogViewModel.RestartMode.FolderOnly, standaloneRootHarness.Dialog.IsNeedRestartForSaved());
        }
        finally
        {
            TryDeleteDirectory(standaloneRoot);
        }

        string lr2SongDbRoot = CreateTemporaryRoot("settings-reload-lr2-song-db");
        try
        {
            (string songDbPath, string configPath, string bmsRoot) = CreateValidLr2Layout(lr2SongDbRoot);
            Settings settings = CreateLr2Settings(lr2SongDbRoot, bmsRoot);
            settings.LR2SongDBPath = songDbPath;
            settings.LR2ConfigXmlPath = configPath;
            using SettingsDialogHarness lr2SongDbHarness = SettingsDialogHarness.Create(settings);
            lr2SongDbHarness.Session.Values.LR2SongDBPath = Path.Combine(lr2SongDbRoot, "alternate-song.db");
            Assert.AreEqual(SettingsDialogViewModel.RestartMode.All, lr2SongDbHarness.Dialog.IsNeedRestartForSaved());
        }
        finally
        {
            TryDeleteDirectory(lr2SongDbRoot);
        }

        string lr2AddRoot = CreateTemporaryRoot("settings-reload-lr2-add");
        try
        {
            (string songDbPath, string configPath, string bmsRoot) = CreateValidLr2Layout(lr2AddRoot);
            string addedRoot = Path.Combine(lr2AddRoot, "BMS-added");
            Directory.CreateDirectory(addedRoot);
            Settings settings = CreateLr2Settings(lr2AddRoot, bmsRoot);
            settings.LR2SongDBPath = songDbPath;
            settings.LR2ConfigXmlPath = configPath;
            using SettingsDialogHarness lr2AddHarness = SettingsDialogHarness.Create(settings);
            lr2AddHarness.Dialog.AddBmsSearchRootPaths([addedRoot]);

            CollectionAssert.AreEqual(new[] { bmsRoot, addedRoot }, lr2AddHarness.Dialog.LR2ConfigBMSDirectories);
            Assert.AreEqual(SettingsDialogViewModel.RestartMode.FolderOnly, lr2AddHarness.Dialog.IsNeedRestartForSaved());
        }
        finally
        {
            TryDeleteDirectory(lr2AddRoot);
        }

        string lr2RemoveRoot = CreateTemporaryRoot("settings-reload-lr2-remove");
        try
        {
            (string songDbPath, string configPath, string bmsRoot) = CreateValidLr2Layout(lr2RemoveRoot);
            string removedRoot = Path.Combine(lr2RemoveRoot, "BMS-removed");
            Directory.CreateDirectory(removedRoot);
            (songDbPath, configPath, bmsRoot) = CreateValidLr2Layout(
                lr2RemoveRoot,
                [bmsRoot, removedRoot]);
            Settings settings = CreateLr2Settings(lr2RemoveRoot, bmsRoot);
            settings.LR2SongDBPath = songDbPath;
            settings.LR2ConfigXmlPath = configPath;
            using SettingsDialogHarness lr2RemoveHarness = SettingsDialogHarness.Create(settings);
            lr2RemoveHarness.Dialogs.ConfirmationResult = UiDialogResult.FromMessageBoxResult(MessageBoxResult.OK);
            await lr2RemoveHarness.Dialog.RequestRemoveBmsSearchRootAsync(removedRoot);

            CollectionAssert.AreEqual(new[] { bmsRoot }, lr2RemoveHarness.Dialog.LR2ConfigBMSDirectories);
            Assert.AreEqual(SettingsDialogViewModel.RestartMode.FolderOnly, lr2RemoveHarness.Dialog.IsNeedRestartForSaved());
            Assert.AreEqual(0, lr2RemoveHarness.Session.SaveCount);
            Assert.AreEqual(1, lr2RemoveHarness.Dialogs.ConfirmationCount);
            Assert.AreEqual(0, lr2RemoveHarness.Dialogs.MessageCount);
        }
        finally
        {
            TryDeleteDirectory(lr2RemoveRoot);
        }

        using (SettingsDialogHarness customOutputHarness = SettingsDialogHarness.Create(CreateLr2Settings(
            @"C:\settings-behavior\custom-root",
            @"C:\settings-behavior\custom-root\BMS")))
        {
            customOutputHarness.Session.Values.LR2CustomFolderOutputBaseDir = @"C:\settings-behavior\custom-root\new-output";
            Assert.AreEqual(SettingsDialogViewModel.RestartMode.FolderOnly, customOutputHarness.Dialog.IsNeedRestartForSaved());
        }

        using (SettingsDialogHarness resetHarness = SettingsDialogHarness.Create(CreateStandaloneSettings(
            @"C:\settings-behavior\reset-root",
            @"C:\settings-behavior\reset-root")))
        {
            resetHarness.Session.Values.FolderNameFormat = "draft-%TITLE%";
            Assert.IsTrue(resetHarness.Dialog.HasPendingSettingChanges());

            resetHarness.Dialog.ResetSettings();

            Assert.AreEqual(SettingsDialogViewModel.RestartMode.None, resetHarness.Dialog.IsNeedRestartForSaved());
            Assert.IsFalse(resetHarness.Dialog.HasPendingSettingChanges());
        }

        using (SettingsDialogHarness saveHarness = SettingsDialogHarness.Create(CreateStandaloneSettings(
            @"C:\settings-behavior\save-root",
            @"C:\settings-behavior\save-root")))
        {
            saveHarness.Session.Values.FolderNameFormat = "draft-%TITLE%";
            await saveHarness.Dialog.SaveSettings();

            Assert.AreEqual(1, saveHarness.Session.SaveCount);
            Assert.AreEqual(SettingsDialogViewModel.RestartMode.None, saveHarness.Dialog.IsNeedRestartForSaved());
            Assert.IsFalse(saveHarness.Dialog.HasPendingSettingChanges());
        }

        string retryRoot = CreateTemporaryRoot("settings-reload-retry");
        try
        {
            Settings retrySettings = CreateStandaloneSettings(retryRoot, retryRoot);
            List<Exception> failures = [];
            using SettingsDialogHarness retryHarness = SettingsDialogHarness.Create(
                retrySettings,
                activeLibraryProfile: true,
                reportApplyFailure: failures.Add);
            retryHarness.Session.Values.BeatorajaScoreDbPath = @"C:\settings-behavior\retry-score.db";
            retryHarness.State.ThrowOnScoreReload = true;

            await retryHarness.Dialog.ApplySettingsAsync();

            Assert.AreEqual(1, retryHarness.State.ScoreReloadCount);
            Assert.AreEqual(1, retryHarness.Session.SaveCount);
            Assert.AreEqual(1, failures.Count);
            Assert.IsTrue(retryHarness.Dialog.IsScoreReloadPending);
            Assert.IsFalse(retryHarness.Dialog.IsEditCancellationEnabled);
            Assert.AreEqual(SettingsDialogViewModel.RestartMode.ScoreOnly, retryHarness.Dialog.IsNeedRestartForSaved());

            retryHarness.State.ThrowOnScoreReload = false;
            await retryHarness.Dialog.ApplySettingsAsync();

            Assert.AreEqual(2, retryHarness.State.ScoreReloadCount);
            Assert.AreEqual(1, retryHarness.Session.SaveCount);
            Assert.IsFalse(retryHarness.Dialog.IsScoreReloadPending);
            Assert.IsTrue(retryHarness.Dialog.IsEditCancellationEnabled);
            Assert.AreEqual(SettingsDialogViewModel.RestartMode.None, retryHarness.Dialog.IsNeedRestartForSaved());
        }
        finally
        {
            TryDeleteDirectory(retryRoot);
        }
    }

    [TestMethod]
    public void SettingDialogValidation_RequiresInstallDestinationInAllOperationModes()
    {
        string standaloneRoot = CreateTemporaryRoot("settings-validation-standalone");
        try
        {
            Settings standalone = CreateStandaloneSettings(standaloneRoot, installDirectory: string.Empty);
            using (SettingsDialogHarness standaloneHarness = SettingsDialogHarness.Create(standalone))
            {
                AssertInstallDestinationValidation(standaloneHarness.Dialog);
                standaloneHarness.Session.Values.BMSInstallDir = standaloneRoot;
                Assert.IsTrue(standaloneHarness.Dialog.CheckValidation(out string validError), validError);
                Assert.AreEqual(string.Empty, validError);
            }
        }
        finally
        {
            TryDeleteDirectory(standaloneRoot);
        }

        string tempRoot = CreateTemporaryRoot("settings-validation-lr2");
        try
        {
            (string songDbPath, string configPath, string bmsRoot) = CreateValidLr2Layout(tempRoot);
            Settings linked = CreateLr2Settings(tempRoot, bmsRoot);
            linked.LR2RootPath = tempRoot;
            linked.LR2SongDBPath = songDbPath;
            linked.LR2ConfigXmlPath = configPath;
            linked.BMSInstallDir = string.Empty;

            using SettingsDialogHarness linkedHarness = SettingsDialogHarness.Create(linked);
            AssertInstallDestinationValidation(linkedHarness.Dialog);
            linkedHarness.Session.Values.BMSInstallDir = bmsRoot;
            Assert.IsTrue(linkedHarness.Dialog.CheckValidation(out string validError), validError);
            Assert.AreEqual(string.Empty, validError);
        }
        finally
        {
            TryDeleteDirectory(tempRoot);
        }
    }

    [TestMethod]
    public void BeatorajaScoreDbValidationUsesScoreDbErrorWhenRootAndPlayerAreValid()
    {
        using var fixture = new TemporaryDirectory("settings-validation-beatoraja");
        string beatorajaRoot = fixture.Path;
        string playerDirectory = Path.Combine(beatorajaRoot, "player", "player1");
        string bmsRoot = Path.Combine(fixture.Path, "bms");
        Directory.CreateDirectory(playerDirectory);
        Directory.CreateDirectory(bmsRoot);
        File.WriteAllText(Path.Combine(beatorajaRoot, "beatoraja.jar"), string.Empty);
        File.WriteAllText(
            Path.Combine(beatorajaRoot, BeatorajaConfigService.ConfigFileName),
            "{\"playerpath\":\"player\",\"playername\":\"player1\"}");

        Settings values = CreateStandaloneSettings(bmsRoot, bmsRoot);
        values.BeatorajaRootPath = beatorajaRoot;
        values.BeatorajaPlayerId = "player1";
        values.BeatorajaScoreDbPath = Path.Combine(playerDirectory, "score.db");
        values.UseBeatorajaScoreDb = true;

        using SettingsDialogHarness harness = SettingsDialogHarness.Create(values);

        Assert.IsFalse(harness.Dialog.CheckValidation(out string error));
        StringAssert.Contains(error, Resources.Error_InvalidBeatorajaScoreDbPath);
        Assert.IsFalse(error.Contains(Resources.Error_InvalidBeatorajaRootPath, StringComparison.Ordinal));
    }

    [TestMethod]
    public void StandaloneValidationReportsLocalizedRootErrorForEmptyRootList()
    {
        using var fixture = new TemporaryDirectory("settings-validation-empty-standalone");
        Settings values = CreateStandaloneSettings(fixture.Path, fixture.Path);
        values.StandaloneBmsRootPaths = string.Empty;

        using SettingsDialogHarness harness = SettingsDialogHarness.Create(values);
        harness.Dialog.StandaloneBmsRootPathList.Clear();

        Assert.IsFalse(harness.Dialog.CheckValidation(out string error));
        StringAssert.Contains(error, Resources.Error_InvalidStandaloneBmsRootPaths);
        Assert.IsFalse(error.Contains(Resources.Error_InvalidBeatorajaRootPath, StringComparison.Ordinal));
    }

    [TestMethod]
    public void StandaloneRootNormalization_PreservesExistingRootsWhenAddingInstallDestination()
    {
        string root = CreateTemporaryRoot("settings-normalization");
        try
        {
            string parentRoot = Path.Combine(root, "library");
            string childRoot = Path.Combine(parentRoot, "child");
            string installRoot = Path.Combine(root, "install");
            string legacyRoot = Path.Combine(root, "legacy");
            Directory.CreateDirectory(parentRoot);
            Directory.CreateDirectory(childRoot);
            Directory.CreateDirectory(installRoot);
            Directory.CreateDirectory(legacyRoot);
            Settings values = CreateStandaloneSettings(parentRoot, installDirectory: string.Empty);
            values.BMSRootPath = legacyRoot;
            values.StandaloneBmsRootPaths = string.Join(
                Environment.NewLine,
                parentRoot + Path.DirectorySeparatorChar,
                childRoot,
                parentRoot.ToUpperInvariant());

            using SettingsDialogHarness harness = SettingsDialogHarness.Create(values);
            harness.Dialog.AddBmsSearchRootPathFromPicker(nameof(SettingsDialogViewModel.BMSInstallDir), installRoot);

            CollectionAssert.AreEqual(
                new[] { parentRoot, childRoot, installRoot },
                harness.Dialog.StandaloneBmsRootPathList.ToArray(),
                "The parent, child, and newly selected roots must remain distinct after normalization.");
            Assert.AreEqual(installRoot, harness.Dialog.SelectedStandaloneBmsRootPath);
            Assert.AreEqual(installRoot, harness.Dialog.BMSInstallDir);
            Assert.AreEqual(legacyRoot, harness.Session.Values.BMSRootPath);
            Assert.AreEqual(0, harness.Session.SaveCount);
            Assert.IsTrue(harness.Dialog.HasPendingSettingChanges());
        }
        finally
        {
            TryDeleteDirectory(root);
        }
    }

    private static void AssertInstallDestinationValidation(SettingsDialogViewModel dialog)
    {
        Assert.IsFalse(dialog.CheckValidation(out string error));
        string expected = string.Format(
            Resources.SettingValidation_SectionMessageFormat,
            Resources.Install,
            Resources.Error_InvalidBmsInstallDir) + Environment.NewLine;
        Assert.AreEqual(expected, error);
    }

    private static Settings CreateStandaloneSettings(string root, string installDirectory)
    {
        return new Settings
        {
            OperationModeLR2DB = false,
            BMSRootPath = root,
            StandaloneBmsRootPaths = root,
            BMSInstallDir = installDirectory,
            TableListURL = new Uri("http://127.0.0.1:1/table-list.json"),
            FolderNameFormat = "%TITLE%",
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
    }

    private static Settings CreateLr2Settings(string root, string bmsRoot)
    {
        Settings settings = CreateStandaloneSettings(bmsRoot, bmsRoot);
        settings.OperationModeLR2DB = true;
        settings.LR2RootPath = root;
        settings.LR2SongDBPath = Path.Combine(root, "LR2files", "Database", "song.db");
        settings.LR2ConfigXmlPath = Path.Combine(root, "LR2files", "Config", "config.xml");
        settings.LR2CustomFolderOutputBaseDir = Path.Combine(root, "custom-output");
        settings.LR2CustomFolderOutputBaseDirRootType = Path.Combine(root, "custom-root-output");
        settings.LR2CustomFolderAdditionalOutputBaseDirs = string.Empty;
        return settings;
    }

    private static (string SongDbPath, string ConfigPath, string BmsRoot) CreateValidLr2Layout(
        string root,
        IReadOnlyList<string>? bmsRoots = null)
    {
        string songDbPath = Path.Combine(root, "LR2files", "Database", "song.db");
        string configPath = Path.Combine(root, "LR2files", "Config", "config.xml");
        string bmsRoot = Path.Combine(root, "BMS");
        IReadOnlyList<string> searchRoots = bmsRoots ?? [bmsRoot];
        Directory.CreateDirectory(Path.GetDirectoryName(songDbPath)!);
        Directory.CreateDirectory(Path.GetDirectoryName(configPath)!);
        foreach (string searchRoot in searchRoots)
        {
            Directory.CreateDirectory(searchRoot);
        }
        File.WriteAllBytes(songDbPath, []);
        File.WriteAllText(configPath, "<config><system /><jukebox /></config>");

        var config = new BeMusicSeeker.Models.LR2.LR2Config(configPath);
        config.SetBMSSearchDirectories(searchRoots);
        config.Save();
        return (songDbPath, configPath, bmsRoot);
    }

    private static string CreateTemporaryRoot(string prefix)
    {
        string path = Path.Combine(Path.GetTempPath(), "BeMusicSeeker_" + prefix + "_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(path);
        return path;
    }

    private static void TryDeleteDirectory(string path)
    {
        if (!Directory.Exists(path))
        {
            return;
        }

        Directory.Delete(path, recursive: true);
    }

    private sealed class TemporaryDirectory : IDisposable
    {
        internal TemporaryDirectory(string prefix)
        {
            Path = CreateTemporaryRoot(prefix);
        }

        internal string Path { get; }

        public void Dispose()
        {
            TryDeleteDirectory(Path);
        }
    }

    private sealed class SettingsSemanticSnapshot
    {
        private readonly IReadOnlyDictionary<string, string> values;

        private SettingsSemanticSnapshot(IReadOnlyDictionary<string, string> values)
        {
            this.values = values;
        }

        internal static SettingsSemanticSnapshot Capture(Settings settings)
        {
            var values = new Dictionary<string, string>(StringComparer.Ordinal);
            foreach (SettingsProperty property in settings.Properties)
            {
                if (property.Attributes[typeof(UserScopedSettingAttribute)] is null)
                {
                    continue;
                }

                _ = settings[property.Name];
                SettingsPropertyValue propertyValue = settings.PropertyValues[property.Name];
                object serializedValue = propertyValue?.SerializedValue;
                values.Add(property.Name, Canonicalize(serializedValue ?? propertyValue?.PropertyValue));
            }

            return new SettingsSemanticSnapshot(
                new System.Collections.ObjectModel.ReadOnlyDictionary<string, string>(values));
        }

        internal IEnumerable<string> GetDifferingPropertyNames(SettingsSemanticSnapshot other)
        {
            ArgumentNullException.ThrowIfNull(other);
            return values.Keys
                .Union(other.values.Keys, StringComparer.Ordinal)
                .Where(propertyName => !values.TryGetValue(propertyName, out string value)
                    || !other.values.TryGetValue(propertyName, out string otherValue)
                    || !string.Equals(value, otherValue, StringComparison.Ordinal))
                .OrderBy(propertyName => propertyName, StringComparer.Ordinal);
        }

        internal void AssertSemanticallyEqualExcept(SettingsSemanticSnapshot expected, params string[] excludedPropertyNames)
        {
            ArgumentNullException.ThrowIfNull(expected);
            HashSet<string> excluded = new(excludedPropertyNames, StringComparer.Ordinal);
            CollectionAssert.AreEquivalent(expected.values.Keys.ToArray(), values.Keys.ToArray());
            foreach (string propertyName in values.Keys)
            {
                if (excluded.Contains(propertyName))
                {
                    continue;
                }

                Assert.AreEqual(
                    expected.values[propertyName],
                    values[propertyName],
                    "Unexpected save-time change for user-scoped setting " + propertyName);
            }
        }

        internal void AssertPropertySemanticValue(string propertyName, object expectedValue)
        {
            Assert.IsTrue(values.TryGetValue(propertyName, out string actualValue), propertyName);
            Assert.AreEqual(Canonicalize(expectedValue), actualValue, propertyName);
        }

        private static string Canonicalize(object value)
        {
            if (value is null)
            {
                return "<null>";
            }

            if (value is byte[] bytes)
            {
                return "byte[]:" + Convert.ToBase64String(bytes);
            }

            if (value is string text)
            {
                return text;
            }

            if (value is IDictionary dictionary)
            {
                List<(string Key, string Value)> entries = [];
                foreach (DictionaryEntry entry in dictionary)
                {
                    entries.Add((Canonicalize(entry.Key), Canonicalize(entry.Value)));
                }

                entries.Sort((left, right) => string.CompareOrdinal(left.Key, right.Key));
                return "dictionary:["
                    + string.Join(",", entries.Select(entry => entry.Key + "=" + entry.Value))
                    + "]";
            }

            if (value is IEnumerable sequence && value is not string)
            {
                return "sequence:["
                    + string.Join(",", sequence.Cast<object>().Select(Canonicalize))
                    + "]";
            }

            if (value is Uri uri)
            {
                return "Uri:" + uri.ToString();
            }

            if (value is IConvertible convertible)
            {
                return convertible.ToString(System.Globalization.CultureInfo.InvariantCulture);
            }

            if (value is IFormattable formattable)
            {
                return formattable.ToString(null, System.Globalization.CultureInfo.InvariantCulture);
            }

            return value.ToString() ?? string.Empty;
        }
    }

    private sealed class SettingsDialogRuntimeCallLedger
    {
        private bool captureUnexpectedCalls;

        internal List<string> Calls { get; } = [];

        internal void BeginCapture()
        {
            captureUnexpectedCalls = true;
        }

        internal void ThrowIfUnexpected(string route)
        {
            if (!captureUnexpectedCalls)
            {
                return;
            }

            Calls.Add(route);
            throw new InvalidOperationException("Unexpected settings dialog runtime call: " + route);
        }
    }

    private sealed class SettingsDialogHarness : IDisposable
    {
        private SettingsDialogHarness(
            SettingsDialogViewModel dialog,
            RecordingSettingsEditSession session,
            RecordingSettingsStatePort state,
            RecordingApplicationLifetime lifetime,
            RecordingDialogService dialogs,
            RecordingSearchRootRuntimePort searchRoots,
            ThrowingPlayerFactoryPort playerFactory,
            RecordingPlaybackRuntimePort playbackRuntime,
            NoOpLr2SongDbSyncRuntime syncRuntime,
            Lr2SongDbSyncWorkflowOwner syncWorkflow,
            SettingsDialogRuntimeCallLedger runtimeCalls)
        {
            Dialog = dialog;
            Session = session;
            State = state;
            Lifetime = lifetime;
            Dialogs = dialogs;
            SearchRoots = searchRoots;
            PlayerFactory = playerFactory;
            PlaybackRuntime = playbackRuntime;
            SyncRuntime = syncRuntime;
            SyncWorkflow = syncWorkflow;
            RuntimeCalls = runtimeCalls;
        }

        internal SettingsDialogViewModel Dialog { get; }

        internal RecordingSettingsEditSession Session { get; }

        internal RecordingSettingsStatePort State { get; }

        internal RecordingApplicationLifetime Lifetime { get; }

        internal RecordingDialogService Dialogs { get; }

        internal RecordingSearchRootRuntimePort SearchRoots { get; }

        internal ThrowingPlayerFactoryPort PlayerFactory { get; }

        internal RecordingPlaybackRuntimePort PlaybackRuntime { get; }

        internal NoOpLr2SongDbSyncRuntime SyncRuntime { get; }

        internal Lr2SongDbSyncWorkflowOwner SyncWorkflow { get; }

        internal SettingsDialogRuntimeCallLedger RuntimeCalls { get; }

        internal static SettingsDialogHarness Create(
            Settings values,
            bool activeLibraryProfile = false,
            RecordingSettingsEditSession? session = null,
            Action<Exception>? reportApplyFailure = null,
            bool libraryAttached = false,
            Func<Task>? reloadFileDiff = null)
        {
            session ??= new RecordingSettingsEditSession(values);
            var runtimeCalls = new SettingsDialogRuntimeCallLedger();
            var state = new RecordingSettingsStatePort(activeLibraryProfile, runtimeCalls);
            state.ReloadFileDiffHandler = reloadFileDiff;
            var lifetime = new RecordingApplicationLifetime(runtimeCalls);
            var dialogs = new RecordingDialogService(runtimeCalls);
            var workspace = new RecordingWorkspacePort(runtimeCalls);
            var customFolder = new RecordingCustomFolderOutputPort(
                () => CustomFolderOutputSettingsSnapshot.CreateCurrent(session.Values),
                runtimeCalls);
            var playHistory = new RecordingPlayHistoryPort(runtimeCalls);
            var searchRoots = new RecordingSearchRootRuntimePort(runtimeCalls);
            searchRoots.IsLibraryAttachedValue = libraryAttached;
            var playerFactory = new ThrowingPlayerFactoryPort(runtimeCalls);
            var playbackRuntime = new RecordingPlaybackRuntimePort(runtimeCalls);
            var syncRuntime = new NoOpLr2SongDbSyncRuntime(runtimeCalls);
            var syncWorkflow = new Lr2SongDbSyncWorkflowOwner(
                syncRuntime,
                dialogs,
                backgroundScheduler: action =>
                {
                    action();
                    return Task.CompletedTask;
                });
            var dialog = new SettingsDialogViewModel(
                state,
                workspace,
                customFolder,
                playHistory,
                searchRoots,
                playerFactory,
                playbackRuntime,
                syncWorkflow,
                session,
                applicationLifetime: lifetime,
                cultureCatalog: TestApplicationContext.CreateCultureCatalog(),
                schemaDialogs: dialogs,
                reportApplyFailure: reportApplyFailure,
                externalShellGateway: ExternalShellGatewayPolicy.Current,
                applicationPathSnapshot: ApplicationPathPolicy.Current,
                audioDeviceCatalog: new TestAudioDeviceCatalog(),
                audioSettingsGateway: new TestAudioSettingsGateway(),
                audioDeviceTestWorkflow: AudioDeviceTestWorkflowTestFactory.Create());
            return new SettingsDialogHarness(
                dialog,
                session,
                state,
                lifetime,
                dialogs,
                searchRoots,
                playerFactory,
                playbackRuntime,
                syncRuntime,
                syncWorkflow,
                runtimeCalls);
        }

        internal void AttachSequence(IList<string> sequence)
        {
            Session.Sequence = sequence;
            Lifetime.Sequence = sequence;
            Dialogs.Sequence = sequence;
            SearchRoots.Sequence = sequence;
        }

        public void Dispose()
        {
            Dialog.Dispose();
        }
    }

    private sealed class DirectSettingsEditSession : ISettingsEditSession
    {
        internal DirectSettingsEditSession(Settings values)
        {
            Values = values ?? throw new ArgumentNullException(nameof(values));
        }

        public Settings Values { get; }

        public void Reload()
        {
        }

        public void Save()
        {
        }
    }

    private sealed class RecordingSettingsEditSession : ISettingsEditSession
    {
        private readonly Settings persistedValues;
        private readonly bool replaceValuesOnReload;

        internal RecordingSettingsEditSession(Settings values)
        {
            persistedValues = values ?? throw new ArgumentNullException(nameof(values));
            Values = values;
        }

        internal RecordingSettingsEditSession(Settings persistedValues, Settings draftValues)
        {
            this.persistedValues = persistedValues ?? throw new ArgumentNullException(nameof(persistedValues));
            Values = draftValues ?? throw new ArgumentNullException(nameof(draftValues));
            replaceValuesOnReload = true;
        }

        public Settings Values { get; private set; }

        internal int SaveCount { get; private set; }

        internal int ReloadCount { get; private set; }

        internal SettingsSemanticSnapshot? SaveSnapshot { get; private set; }

        internal List<string> Calls { get; } = [];

        internal IList<string>? Sequence { get; set; }

        internal Exception? SaveFailure { get; set; }

        internal void SetDraft(Settings values)
        {
            Values = values ?? throw new ArgumentNullException(nameof(values));
        }

        internal void ClearCalls()
        {
            Calls.Clear();
            SaveCount = 0;
            ReloadCount = 0;
            SaveSnapshot = null;
        }

        public void Reload()
        {
            ReloadCount++;
            Calls.Add("reload");
            Sequence?.Add("reload");
            if (replaceValuesOnReload)
            {
                Values = persistedValues;
            }
        }

        public void Save()
        {
            SaveCount++;
            Calls.Add("save");
            Sequence?.Add("save");
            SaveSnapshot = SettingsSemanticSnapshot.Capture(Values);
            if (SaveFailure != null)
            {
                throw SaveFailure;
            }
        }
    }

    private sealed class RecordingSettingsStatePort : ISettingsDialogStatePort
    {
        private readonly SettingsDialogRuntimeCallLedger runtimeCalls;

        internal RecordingSettingsStatePort(bool hasActiveLibraryProfile, SettingsDialogRuntimeCallLedger runtimeCalls)
        {
            this.hasActiveLibraryProfile = hasActiveLibraryProfile;
            this.runtimeCalls = runtimeCalls;
        }

        public bool HasActiveLibraryProfile
        {
            get
            {
                runtimeCalls.ThrowIfUnexpected(nameof(HasActiveLibraryProfile));
                return hasActiveLibraryProfile;
            }
        }

        private bool isLibraryOperationInProgress;

        public bool IsLibraryOperationInProgress
        {
            get
            {
                runtimeCalls.ThrowIfUnexpected(nameof(IsLibraryOperationInProgress));
                return isLibraryOperationInProgress;
            }
            set => isLibraryOperationInProgress = value;
        }

        private readonly bool hasActiveLibraryProfile;

        internal int InitializeCount { get; private set; }

        internal int ScoreReloadCount { get; private set; }

        internal int FileDiffReloadCount { get; private set; }

        internal bool ThrowOnScoreReload { get; set; }

        internal Func<Task>? ReloadFileDiffHandler { get; set; }

        internal FileDiffReloadWorkflowOwner? ReloadFileDiffWorkflowOwner { get; set; }

        public Task<bool> InitializeLibraryAsync()
        {
            runtimeCalls.ThrowIfUnexpected(nameof(InitializeLibraryAsync));
            InitializeCount++;
            return Task.FromResult(true);
        }

        public Task ReloadScoresOnlyAsync()
        {
            runtimeCalls.ThrowIfUnexpected(nameof(ReloadScoresOnlyAsync));
            ScoreReloadCount++;
            if (ThrowOnScoreReload)
            {
                return Task.FromException(new InvalidOperationException("score reload failed"));
            }

            return Task.CompletedTask;
        }

        public Task ReloadFileDiffAsync()
        {
            runtimeCalls.ThrowIfUnexpected(nameof(ReloadFileDiffAsync));
            FileDiffReloadCount++;
            if (ReloadFileDiffWorkflowOwner != null)
            {
                return ReloadFileDiffWorkflowOwner.ReloadAsync(
                    new FileDiffReloadRequest("SettingsDialog.SearchRoot", 0L));
            }
            return ReloadFileDiffHandler?.Invoke() ?? Task.CompletedTask;
        }

        public event EventHandler? LibraryOperationAvailabilityChanged;

        public event Action<Lr2PlayHistorySchemaStatusSnapshot>? Lr2PlayHistorySchemaStatusChanged;
    }

    private sealed class RecordingApplicationLifetime : IApplicationLifetimePort
    {
        private readonly SettingsDialogRuntimeCallLedger runtimeCalls;

        internal RecordingApplicationLifetime(SettingsDialogRuntimeCallLedger runtimeCalls)
        {
            this.runtimeCalls = runtimeCalls;
        }

        public bool IsFirstStartup
        {
            get
            {
                runtimeCalls.ThrowIfUnexpected(nameof(IsFirstStartup));
                return false;
            }
        }

        internal int RestartCount { get; private set; }

        internal int ShutdownCount { get; private set; }

        internal IList<string>? Sequence { get; set; }

        internal Exception? RestartFailure { get; set; }

        internal TaskCompletionSource<bool> ShutdownObserved { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public void CompleteFirstStartup()
        {
            runtimeCalls.ThrowIfUnexpected(nameof(CompleteFirstStartup));
        }

        public void MarkCoordinatedShutdownStarted(string reason)
        {
            runtimeCalls.ThrowIfUnexpected(nameof(MarkCoordinatedShutdownStarted));
        }

        public void RequestShutdown()
        {
            runtimeCalls.ThrowIfUnexpected(nameof(RequestShutdown));
            ShutdownCount++;
            Sequence?.Add("shutdown");
            ShutdownObserved.TrySetResult(true);
        }

        public Task RestartApplicationAsync()
        {
            runtimeCalls.ThrowIfUnexpected(nameof(RestartApplicationAsync));
            RestartCount++;
            Sequence?.Add("restart");
            return RestartFailure == null
                ? Task.CompletedTask
                : Task.FromException(RestartFailure);
        }
    }

    private sealed class RecordingDialogService : IUiDialogService
    {
        private readonly SettingsDialogRuntimeCallLedger runtimeCalls;

        internal RecordingDialogService(SettingsDialogRuntimeCallLedger runtimeCalls)
        {
            this.runtimeCalls = runtimeCalls;
        }

        internal int MessageCount { get; private set; }

        internal int ConfirmationCount { get; private set; }

        internal IList<string>? Sequence { get; set; }

        internal UiDialogResult MessageResult { get; set; }
            = UiDialogResult.Failed(new InvalidOperationException("unexpected settings behavior message"));

        internal Exception? MessageFailure { get; set; }

        internal TaskCompletionSource<UiDialogResult>? MessageCompletionSource { get; set; }

        internal UiConfirmationRequest? LastConfirmationRequest { get; private set; }

        internal string? LastMessageText { get; private set; }

        internal UiDialogResult ConfirmationResult { get; set; }
            = UiDialogResult.Failed(new InvalidOperationException("unexpected settings behavior confirmation"));

        public Task<UiDialogResult> ShowMessageAsync(UiMessageRequest request, CancellationToken cancellationToken = default)
        {
            runtimeCalls.ThrowIfUnexpected(nameof(ShowMessageAsync));
            MessageCount++;
            LastMessageText = request.MessageBoxText;
            Sequence?.Add("notify");
            if (MessageFailure != null)
            {
                return Task.FromException<UiDialogResult>(MessageFailure);
            }
            if (MessageCompletionSource != null)
            {
                return MessageCompletionSource.Task;
            }
            return Task.FromResult(MessageResult);
        }

        public Task<UiDialogResult> ConfirmAsync(UiConfirmationRequest request, CancellationToken cancellationToken = default)
        {
            runtimeCalls.ThrowIfUnexpected(nameof(ConfirmAsync));
            ConfirmationCount++;
            LastConfirmationRequest = request;
            Sequence?.Add("confirm");
            return Task.FromResult(ConfirmationResult);
        }

        public Task<UiWindowDialogResult<TResult>> ShowWindowAsync<TWindow, TResult>(
            UiWindowDialogRequest<TWindow, TResult> request,
            CancellationToken cancellationToken = default)
            where TWindow : Window
        {
            runtimeCalls.ThrowIfUnexpected(nameof(ShowWindowAsync));
            return Task.FromResult(new UiWindowDialogResult<TResult>(UiDialogStatus.Failed, error: new InvalidOperationException("unexpected settings behavior window")));
        }

        public Task<UiFilePickerResult> PickFileAsync(UiFilePickerRequest request, CancellationToken cancellationToken = default)
        {
            runtimeCalls.ThrowIfUnexpected(nameof(PickFileAsync));
            return Task.FromResult(new UiFilePickerResult(UiDialogStatus.Failed, error: new InvalidOperationException("unexpected settings behavior file picker")));
        }

        public Task<UiFolderPickerResult> PickFolderAsync(UiFolderPickerRequest request, CancellationToken cancellationToken = default)
        {
            runtimeCalls.ThrowIfUnexpected(nameof(PickFolderAsync));
            return Task.FromResult(new UiFolderPickerResult(UiDialogStatus.Failed, error: new InvalidOperationException("unexpected settings behavior folder picker")));
        }

        public Task<UiSaveFilePickerResult> PickSaveFileAsync(UiSaveFilePickerRequest request, CancellationToken cancellationToken = default)
        {
            runtimeCalls.ThrowIfUnexpected(nameof(PickSaveFileAsync));
            return Task.FromResult(new UiSaveFilePickerResult(UiDialogStatus.Failed, error: new InvalidOperationException("unexpected settings behavior save picker")));
        }

        public Task<UiProgressResult> RunWithProgressAsync(
            UiProgressRequest request,
            Func<UiProgressContext, Task> operation,
            CancellationToken cancellationToken = default)
        {
            runtimeCalls.ThrowIfUnexpected(nameof(RunWithProgressAsync));
            return Task.FromResult(new UiProgressResult(UiDialogStatus.Failed, error: new InvalidOperationException("unexpected settings behavior progress")));
        }
    }

    private sealed class RecordingWorkspacePort : ISettingsDialogWorkspacePort
    {
        private readonly SettingsDialogRuntimeCallLedger runtimeCalls;

        internal RecordingWorkspacePort(SettingsDialogRuntimeCallLedger runtimeCalls)
        {
            this.runtimeCalls = runtimeCalls;
        }

        public bool HasPlaylistTables
        {
            get
            {
                runtimeCalls.ThrowIfUnexpected(nameof(HasPlaylistTables));
                return false;
            }
        }

        public long PlaylistCatalogVersion
        {
            get
            {
                runtimeCalls.ThrowIfUnexpected(nameof(PlaylistCatalogVersion));
                return 0;
            }
        }

        public IReadOnlyList<PlaylistTablePresentationSnapshot> CapturePlaylistPresentationSnapshots()
        {
            runtimeCalls.ThrowIfUnexpected(nameof(CapturePlaylistPresentationSnapshots));
            return [];
        }

        public bool HasUnimportedBeatorajaTableUrlsForBmtOutputGuide(string beatorajaRootPath)
        {
            runtimeCalls.ThrowIfUnexpected(nameof(HasUnimportedBeatorajaTableUrlsForBmtOutputGuide));
            return false;
        }

        public void SchedulePlaylistUrlCompletionRefresh(string reason)
        {
            runtimeCalls.ThrowIfUnexpected(nameof(SchedulePlaylistUrlCompletionRefresh));
        }

        public void QueueBeatorajaBmtExportAll(string reason, string cleanupTablePath)
        {
            runtimeCalls.ThrowIfUnexpected(nameof(QueueBeatorajaBmtExportAll));
        }

        public Task RunWithPlaylistOperationNotificationsAsync(Func<Task> operation, string operationName)
        {
            runtimeCalls.ThrowIfUnexpected(nameof(RunWithPlaylistOperationNotificationsAsync));
            return operation();
        }

        public event EventHandler<PlaylistCatalogChangedEventArgs>? PlaylistCatalogChanged;
    }

    private sealed class RecordingCustomFolderOutputPort : ISettingsDialogCustomFolderOutputPort
    {
        private readonly Func<CustomFolderOutputSettingsSnapshot> settingsProvider;
        private readonly SettingsDialogRuntimeCallLedger runtimeCalls;

        internal RecordingCustomFolderOutputPort(
            Func<CustomFolderOutputSettingsSnapshot> settingsProvider,
            SettingsDialogRuntimeCallLedger runtimeCalls)
        {
            this.settingsProvider = settingsProvider;
            this.runtimeCalls = runtimeCalls;
        }

        public CustomFolderOutputSettingsSnapshot CustomFolderOutputSettings
        {
            get
            {
                runtimeCalls.ThrowIfUnexpected(nameof(CustomFolderOutputSettings));
                return settingsProvider();
            }
        }

        public void ChangeCustomFolderBaseDirectoryWithSettings(
            string outputDirBaseBefore,
            string outputDirBaseAfter,
            string additionalOutputBaseDirsBefore,
            string additionalOutputBaseDirsAfter,
            CustomFolderOutputSettingsSnapshot settings)
        {
            runtimeCalls.ThrowIfUnexpected(nameof(ChangeCustomFolderBaseDirectoryWithSettings));
        }

        public void ChangeCustomFolderBaseDirectoryRootWithSettings(
            string outputDirBaseBefore,
            string outputDirBaseAfter,
            CustomFolderOutputSettingsSnapshot settings)
        {
            runtimeCalls.ThrowIfUnexpected(nameof(ChangeCustomFolderBaseDirectoryRootWithSettings));
        }

        public bool SyncCustomFolderOutputSearchRootsAfterSettingsChangeWithSettings(
            string previousRootOutputBaseDirectory,
            CustomFolderOutputSettingsSnapshot settings)
        {
            runtimeCalls.ThrowIfUnexpected(nameof(SyncCustomFolderOutputSearchRootsAfterSettingsChangeWithSettings));
            return false;
        }

        public int ApplyCustomFolderAdditionalOutputBaseRegistrationChanges(
            string previousAdditionalOutputBaseDirectories,
            IReadOnlyDictionary<string, string> pendingRenames,
            CustomFolderOutputSettingsSnapshot settings)
        {
            runtimeCalls.ThrowIfUnexpected(nameof(ApplyCustomFolderAdditionalOutputBaseRegistrationChanges));
            return 0;
        }
    }

    private sealed class RecordingPlayHistoryPort : ISettingsDialogPlayHistoryPort
    {
        private readonly SettingsDialogRuntimeCallLedger runtimeCalls;

        internal RecordingPlayHistoryPort(SettingsDialogRuntimeCallLedger runtimeCalls)
        {
            this.runtimeCalls = runtimeCalls;
        }

        public void InvalidateReadCache(string reason)
        {
            runtimeCalls.ThrowIfUnexpected(nameof(InvalidateReadCache));
        }

        public void RefreshDisplayTargetCatalog(bool queueRefreshWhenSelectionChanges = true)
        {
            runtimeCalls.ThrowIfUnexpected(nameof(RefreshDisplayTargetCatalog));
        }

        public void RefreshDisplayTargetSetsFromSettings(string serializedDisplayTargetSets, bool queueRefreshWhenSelectionChanges)
        {
            runtimeCalls.ThrowIfUnexpected(nameof(RefreshDisplayTargetSetsFromSettings));
        }
    }

    private sealed class RecordingSearchRootRuntimePort : ISettingsDialogSearchRootRuntimePort
    {
        private readonly SettingsDialogRuntimeCallLedger runtimeCalls;

        internal RecordingSearchRootRuntimePort(SettingsDialogRuntimeCallLedger runtimeCalls)
        {
            this.runtimeCalls = runtimeCalls;
        }

        internal bool IsLibraryAttachedValue { get; set; }

        internal IList<string>? Sequence { get; set; }

        internal int ApplyCount { get; private set; }

        internal IReadOnlyList<string> LastSearchTargets { get; private set; } = [];

        internal Exception? ApplyFailure { get; set; }

        internal Action<IReadOnlyList<string>>? ApplyObserved { get; set; }

        public bool IsLibraryAttached
        {
            get
            {
                runtimeCalls.ThrowIfUnexpected(nameof(IsLibraryAttached));
                return IsLibraryAttachedValue;
            }
        }

        public bool HasOwnedChartUnderRealPath(string directoryPath)
        {
            runtimeCalls.ThrowIfUnexpected(nameof(HasOwnedChartUnderRealPath));
            return false;
        }

        public void ApplySearchTargets(IReadOnlyList<string> searchTargets)
        {
            runtimeCalls.ThrowIfUnexpected(nameof(ApplySearchTargets));
            ApplyCount++;
            LastSearchTargets = [.. searchTargets];
            Sequence?.Add("apply");
            ApplyObserved?.Invoke(LastSearchTargets);
            if (ApplyFailure != null)
            {
                throw ApplyFailure;
            }
        }

        public void InvalidateLibraryFolderCache()
        {
            runtimeCalls.ThrowIfUnexpected(nameof(InvalidateLibraryFolderCache));
            Sequence?.Add("invalidate");
        }
    }

    private sealed class ThrowingPlayerFactoryPort : ISettingsDialogPlayerFactoryPort
    {
        private readonly SettingsDialogRuntimeCallLedger runtimeCalls;

        internal ThrowingPlayerFactoryPort(SettingsDialogRuntimeCallLedger runtimeCalls)
        {
            this.runtimeCalls = runtimeCalls;
        }

        internal int DefaultCreateCount { get; private set; }

        internal int ConfiguredCreateCount { get; private set; }

        public IBMSPlayer CreateDefaultBmsPlayer()
        {
            DefaultCreateCount++;
            runtimeCalls.ThrowIfUnexpected(nameof(CreateDefaultBmsPlayer));
            throw new InvalidOperationException("player creation is outside this settings behavior unit");
        }

        public IBMSPlayer CreateBmsPlayerForSettings(StartupSettingsSnapshot settings)
        {
            ConfiguredCreateCount++;
            runtimeCalls.ThrowIfUnexpected(nameof(CreateBmsPlayerForSettings));
            throw new InvalidOperationException("player creation is outside this settings behavior unit");
        }
    }

    private sealed class RecordingPlaybackRuntimePort : ISettingsDialogPlaybackRuntimePort
    {
        private readonly SettingsDialogRuntimeCallLedger runtimeCalls;

        internal RecordingPlaybackRuntimePort(SettingsDialogRuntimeCallLedger runtimeCalls)
        {
            this.runtimeCalls = runtimeCalls;
        }

        internal int ApplyCount { get; private set; }

        internal int NotifyCount { get; private set; }

        public Task ApplyPlayerSettingsAsync(IBMSPlayer replacementPlayer)
        {
            ApplyCount++;
            runtimeCalls.ThrowIfUnexpected(nameof(ApplyPlayerSettingsAsync));
            return Task.CompletedTask;
        }

        public void NotifySettingsChanged()
        {
            NotifyCount++;
            runtimeCalls.ThrowIfUnexpected(nameof(NotifySettingsChanged));
        }

        public void StopPlayback()
        {
            runtimeCalls.ThrowIfUnexpected(nameof(StopPlayback));
        }
    }

    private sealed class NoOpLr2SongDbSyncRuntime : ILr2SongDbSyncWorkflowRuntime
    {
        private readonly SettingsDialogRuntimeCallLedger runtimeCalls;

        internal NoOpLr2SongDbSyncRuntime(SettingsDialogRuntimeCallLedger runtimeCalls)
        {
            this.runtimeCalls = runtimeCalls;
        }

        internal bool IsLr2ModeEnabledValue { get; set; }

        internal bool IsLibraryAvailableValue { get; set; } = true;

        internal int QueueCount { get; private set; }

        internal Exception? QueueFailure { get; set; }

        internal Action? QueueObserved { get; set; }

        public bool IsLr2ModeEnabled => IsLr2ModeEnabledValue;

        public bool IsLibraryAvailable => IsLibraryAvailableValue;

        public void Queue(string reason, bool force, Func<Lr2SongDbSyncPreparedDataSurface>? prepareGeneratedData = null, bool allowIncompleteToQueue = true)
        {
            QueueCount++;
            QueueObserved?.Invoke();
            if (QueueFailure != null)
            {
                throw QueueFailure;
            }
            prepareGeneratedData?.Invoke();
        }

        public bool TryRunDataPreparation(string reason, Func<Lr2SongDbSyncPreparedDataSurface> prepareGeneratedData, Action? queueAfterPreparation = null)
        {
            runtimeCalls.ThrowIfUnexpected(nameof(TryRunDataPreparation));
            return false;
        }

        public Lr2SongDbSyncPreparedDataSurface PreparePlaylistGeneratedData(string reason)
        {
            runtimeCalls.ThrowIfUnexpected(nameof(PreparePlaylistGeneratedData));
            return Lr2SongDbSyncPreparedDataSurface.Empty;
        }

        public Lr2SongDbSyncPreparedDataSurface PrepareBuiltinGeneratedData(string reason)
        {
            runtimeCalls.ThrowIfUnexpected(nameof(PrepareBuiltinGeneratedData));
            return Lr2SongDbSyncPreparedDataSurface.Empty;
        }

        public void SyncExternalFolderRowsForCustomFolderOutputBaseChange(string reason)
        {
            runtimeCalls.ThrowIfUnexpected(nameof(SyncExternalFolderRowsForCustomFolderOutputBaseChange));
        }

        public bool Cancel(string reason)
        {
            runtimeCalls.ThrowIfUnexpected(nameof(Cancel));
            return false;
        }

        public Lr2StartupScanBlockerCleanupResult CleanupStartupScanBlockerFolderRows(string reason)
        {
            runtimeCalls.ThrowIfUnexpected(nameof(CleanupStartupScanBlockerFolderRows));
            return new(null, 0, null);
        }
    }
}
