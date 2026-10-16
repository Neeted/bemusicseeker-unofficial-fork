using System;
using System.Collections.Generic;
using System.Configuration;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Xml.Linq;
using BeMusicSeeker.Models;
using BeMusicSeeker.Models.BmsLibraryInternal;
using BeMusicSeeker.Models.LR2;
using BeMusicSeeker.Models.Utils;
using BeMusicSeeker.Properties;
using BeMusicSeeker.ViewModels;
using BeMusicSeeker.Views.Dialogs;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace BeMusicSeeker.Tests;

/// <summary>設定 draft の検証・保存・適用と、後続処理への配送を検証します。</summary>
/// <remarks>実 XML／LR2 config の失敗と境界差替えを併用し、保存済み値と draft の分離を確認します。実 Window は presentation、実 mode 保存と終了の構成接続は Admission／Shell が分担します。取消・Reset・適用が共有 theme／culture を変更するケースは非並列です。</remarks>
[TestClass]
public sealed class SettingsDialogBehaviorTests
{
    /// <summary>実 XML の置換失敗後も draft と実行値／元 XML を保ち、取消では saved 値へ戻し、明示再保存では変更と捕捉位置を保存することを確認します。</summary>
    [DataTestMethod]
    // 保存失敗後の利用者取消と明示再保存を分ける。
    [DataRow(true)]
    [DataRow(false)]
    [DoNotParallelize]
    public void FailedNormalSaveRetainsDraftUntilUserCancelsOrRetries(bool cancel)
    {
        RunOnUiDispatcher(
            () => FailedNormalSaveRetainsDraftUntilUserCancelsOrRetriesCoreAsync(cancel),
            nameof(FailedNormalSaveRetainsDraftUntilUserCancelsOrRetries));
    }

    private static async Task FailedNormalSaveRetainsDraftUntilUserCancelsOrRetriesCoreAsync(bool cancel)
    {
        using var directory = new TemporaryDirectory("failed-normal-save");
        string path = Path.Combine(directory.Path, "user.config");
        Settings settings = PortableSettingsPersistenceTests.OpenSettings(path);
        settings.OperationModeLR2DB = false;
        settings.BMSRootPath = directory.Path;
        settings.StandaloneBmsRootPaths = directory.Path;
        settings.BMSInstallDir = directory.Path;
        settings.ScanBmsFilesOnStartup = false;
        settings.PlayerResamplingQuality = 4;
        settings.PlayerMixerThreadCount = 3;
        settings.LR2CustomFolderOutputBaseDir = Path.Combine(directory.Path, "OutputA");
        settings.Save();
        byte[] original = File.ReadAllBytes(path);
        var session = new RecordingSettingsEditSession(settings, persistence: new SettingsEditSession(settings));
        var failures = new List<Exception>();
        var audio = new TestAudioSettingsGateway();
        AudioOutputSelection originalAudio = audio.OutputSelection;
        var composition = new ApplicationComposition(settingsEditSession: session, reportSettingsApplyFailure: failures.Add,
            uiScheduler: new TestUiScheduler(() => TestUiDispatcherHost.Dispatcher),
            audioDeviceCatalog: new TestAudioDeviceCatalog(),
            applicationLifetime: TestApplicationContext.CreateLifetime(), cultureCatalog: TestApplicationContext.CreateCultureCatalog());
        using var harness = SettingsDialogHarness.Create(settings, activeLibraryProfile: true, session: session,
            reportApplyFailure: failures.Add, audioSettings: audio, composition: composition);
        var presentation = new RecordingSettingsDialogPresentationPort();
        harness.Dialog.AttachPresentationPort(presentation);
        session.ClearCalls();
        harness.Dialog.ScanBmsFilesOnStartup = true;
        settings.LR2CustomFolderOutputBaseDir = Path.Combine(directory.Path, "OutputB");
        harness.Dialog.PlayerResamplingQuality = 2;
        harness.Dialog.PlayerMixerThreadCount = 4;
        var capture = new WindowPlacement(0, 1, 0, 0, 0, 0, 77, 88, 877, 688);
        new SettingsPlayerSettingsGateway(() => settings).UpdateWindowPlacement(capture);
        if (cancel)
        {
            harness.Dialog.PlayerDriverIndex = AudioDriverPolicy.IndexOf(
                AudioDriverPolicy.SelectableDrivers.First(driver => driver != originalAudio.Backend));
        }
        using (var blockReplacement = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite))
        {
            await harness.Dialog.ApplySettingsAsync();
        }
        Assert.AreEqual(Path.Combine(directory.Path, "OutputA"), composition.CustomFolderOutputSettingsProvider().LR2CustomFolderOutputBaseDir);
        Assert.AreEqual(Path.Combine(directory.Path, "OutputA"), composition.BmsLibraryOptionsProvider().LR2CustomFolderOutputBaseDir);
        Assert.AreEqual(1, session.SaveCount);
        Assert.AreEqual(0, session.ReloadCount);
        Assert.AreEqual(1, failures.Count);
        Assert.IsInstanceOfType<PortableSettingsException>(failures[0]);
        CollectionAssert.AreEqual(original, File.ReadAllBytes(path));
        Assert.IsTrue(harness.Dialog.ScanBmsFilesOnStartup);
        Assert.AreEqual(2, harness.Dialog.PlayerResamplingQuality);
        Assert.AreEqual(4, harness.Dialog.PlayerMixerThreadCount);
        Assert.IsTrue(harness.Dialog.HasPendingSettingChanges());
        Assert.AreEqual(originalAudio, audio.OutputSelection);
        Assert.AreEqual(0, harness.SearchRoots.ApplyCount);
        Assert.AreEqual(0, harness.State.FileDiffReloadCount);
        CollectionAssert.DoesNotContain(presentation.Requests, "close");
        if (cancel)
        {
            harness.Dialog.CancelCommand.Execute();
            Assert.IsFalse(harness.Dialog.ScanBmsFilesOnStartup);
            Assert.AreEqual(4, harness.Dialog.PlayerResamplingQuality);
            Assert.AreEqual(3, harness.Dialog.PlayerMixerThreadCount);
            Assert.AreEqual(1, session.SaveCount);
            Assert.AreEqual(Win32WindowPlacementAdapter.ToNative(capture), settings.LR2bodyWindowPlacement);
            CollectionAssert.AreEqual(original, File.ReadAllBytes(path));
        }
        else
        {
            await harness.Dialog.ApplySettingsAsync();
            Assert.AreEqual(2, session.SaveCount);
            Assert.AreEqual(Path.Combine(directory.Path, "OutputB"), composition.CustomFolderOutputSettingsProvider().LR2CustomFolderOutputBaseDir);
            Assert.AreEqual(Path.Combine(directory.Path, "OutputB"), composition.BmsLibraryOptionsProvider().LR2CustomFolderOutputBaseDir);
            Assert.AreEqual(1, failures.Count);
            Settings loaded = PortableSettingsPersistenceTests.OpenSettings(path);
            Assert.IsTrue(loaded.ScanBmsFilesOnStartup);
            Assert.AreEqual(2, loaded.PlayerResamplingQuality);
            Assert.AreEqual(4, loaded.PlayerMixerThreadCount);
            Assert.AreEqual(Win32WindowPlacementAdapter.ToNative(capture), loaded.LR2bodyWindowPlacement);
        }
        CollectionAssert.Contains(presentation.Requests, "close");
    }

    /// <summary>mode 最小保存の置換失敗では draft と XML を保ち、成功では mode／履歴選択／捕捉位置を保存して無関係 draft を混入させないことを確認します。</summary>
    [DataTestMethod]
    // 実ファイルの置換拒否と同じ保存要求の成功を比較する。
    [DataRow(true)]
    [DataRow(false)]
    public void ModeOnlyPersistencePreservesUnrelatedDraftOnFailureAndRuntimePlacementOnSuccess(bool fail)
    {
        using var directory = new TemporaryDirectory("mode-subset");
        string path = Path.Combine(directory.Path, "user.config");
        Settings settings = PortableSettingsPersistenceTests.OpenSettings(path);
        settings.OperationModeLR2DB = false;
        settings.ScanBmsFilesOnStartup = false;
        settings.Save();
        byte[] original = File.ReadAllBytes(path);
        settings.ScanBmsFilesOnStartup = true;
        var capture = new WindowPlacement(0, 1, 0, 0, 0, 0, 42, 53, 842, 653);
        new SettingsPlayerSettingsGateway(() => settings).UpdateWindowPlacement(capture);
        var session = new SettingsEditSession(settings);
        if (fail)
        {
            using var blocker = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
            Assert.ThrowsException<PortableSettingsException>(() => session.SaveOperationModeForRestart(true, "history-selection"));
            Assert.IsTrue(settings.ScanBmsFilesOnStartup);
            Assert.IsFalse(settings.OperationModeLR2DB);
            CollectionAssert.AreEqual(original, File.ReadAllBytes(path));
        }
        else
        {
            session.SaveOperationModeForRestart(true, "history-selection");
            Settings loaded = PortableSettingsPersistenceTests.OpenSettings(path);
            Assert.IsTrue(loaded.OperationModeLR2DB);
            Assert.AreEqual("history-selection", loaded.PlayHistorySelectedDisplayTargetIdentity);
            Assert.IsFalse(loaded.ScanBmsFilesOnStartup);
            Assert.IsFalse(settings.ScanBmsFilesOnStartup);
            Assert.AreEqual(Win32WindowPlacementAdapter.ToNative(capture), loaded.LR2bodyWindowPlacement);
        }
        Assert.AreEqual(Win32WindowPlacementAdapter.ToNative(capture), settings.LR2bodyWindowPlacement);
    }

    /// <summary>user.config 保存後の LR2 config 失敗を部分保存として報告し、既保存 XML を巻き戻さず draft を残して検索対象適用を始めないことを確認します。</summary>
    [TestMethod]
    [DoNotParallelize]
    public void Lr2SaveFailureReportsAlreadySavedUserConfigurationWithoutRollback()
    {
        RunOnUiDispatcher(
            Lr2SaveFailureReportsAlreadySavedUserConfigurationWithoutRollbackCoreAsync,
            nameof(Lr2SaveFailureReportsAlreadySavedUserConfigurationWithoutRollback));
    }

    private static async Task Lr2SaveFailureReportsAlreadySavedUserConfigurationWithoutRollbackCoreAsync()
    {
        using var directory = new TemporaryDirectory("partial-settings-save");
        (string? songDb, string? configPath, string? bmsRoot) = CreateValidLr2Layout(directory.Path);
        string path = Path.Combine(directory.Path, "user.config");
        Settings settings = PortableSettingsPersistenceTests.OpenSettings(path);
        settings.OperationModeLR2DB = true;
        settings.LR2RootPath = directory.Path;
        settings.LR2SongDBPath = songDb;
        settings.LR2ConfigXmlPath = configPath;
        settings.LR2CustomFolderOutputBaseDir = Path.Combine(directory.Path, "custom-output");
        settings.LR2CustomFolderOutputBaseDirRootType = Path.Combine(directory.Path, "custom-root-output");
        settings.BMSRootPath = bmsRoot;
        settings.BMSInstallDir = bmsRoot;
        settings.ScanBmsFilesOnStartup = false;
        settings.Save();
        var session = new RecordingSettingsEditSession(settings, persistence: new SettingsEditSession(settings));
        var failures = new List<Exception>();
        var composition = new ApplicationComposition(settingsEditSession: session, reportSettingsApplyFailure: failures.Add,
            uiScheduler: new TestUiScheduler(() => TestUiDispatcherHost.Dispatcher),
            audioDeviceCatalog: new TestAudioDeviceCatalog(),
            applicationLifetime: TestApplicationContext.CreateLifetime(), cultureCatalog: TestApplicationContext.CreateCultureCatalog());
        using var harness = SettingsDialogHarness.Create(settings, activeLibraryProfile: true, session: session, reportApplyFailure: failures.Add, composition: composition);
        var presentation = new RecordingSettingsDialogPresentationPort();
        harness.Dialog.AttachPresentationPort(presentation);
        session.ClearCalls();
        harness.Dialog.ScanBmsFilesOnStartup = true;
        settings.LR2CustomFolderOutputBaseDir = Path.Combine(directory.Path, "OutputB");
        string addedRoot = Path.Combine(directory.Path, "added");
        Directory.CreateDirectory(addedRoot);
        harness.Dialog.AddBmsSearchRootPaths([addedRoot]);
        Assert.IsTrue(harness.Dialog.CheckValidationBeforeSave(out string validationError), validationError);
        harness.Dialogs.ConfirmationResult = UiDialogResult.FromMessageBoxResult(MessageBoxResult.OK);
        using (var blocker = new FileStream(configPath, FileMode.Open, FileAccess.Read, FileShare.Read))
        {
            await harness.Dialog.ApplySettingsAsync();
        }
        Assert.AreEqual(Path.Combine(directory.Path, "custom-output"), composition.CustomFolderOutputSettingsProvider().LR2CustomFolderOutputBaseDir);
        Assert.AreEqual(Path.Combine(directory.Path, "custom-output"), composition.BmsLibraryOptionsProvider().LR2CustomFolderOutputBaseDir);
        Assert.AreEqual(Path.Combine(directory.Path, "OutputB"), PortableSettingsPersistenceTests.OpenSettings(path).LR2CustomFolderOutputBaseDir);
        Assert.AreEqual(1, session.SaveCount);
        Assert.AreEqual(1, failures.Count);
        Assert.IsInstanceOfType<PartialSettingsSaveException>(failures[0]);
        var partial = (PartialSettingsSaveException)failures[0];
        Assert.AreEqual(configPath, partial.FilePath);
        Assert.IsNotNull(partial.InnerException);
        StringAssert.Contains(SettingsFailureMessage.Format(partial), configPath);
        Assert.IsTrue(PortableSettingsPersistenceTests.OpenSettings(path).ScanBmsFilesOnStartup);
        Assert.IsTrue(harness.Dialog.HasPendingSettingChanges());
        CollectionAssert.DoesNotContain(presentation.Requests, "close");
        Assert.AreEqual(0, harness.SearchRoots.ApplyCount);
        harness.Dialog.CancelCommand.Execute();
        Assert.IsFalse(harness.Dialog.ScanBmsFilesOnStartup);
        Assert.IsTrue(PortableSettingsPersistenceTests.OpenSettings(path).ScanBmsFilesOnStartup);
    }

    /// <summary>mode 保存が失敗した場合は元 mode と他の draft を保ち、再起動・終了・画面 close を開始せず失敗を一回報告することを確認します。</summary>
    [TestMethod]
    [DoNotParallelize]
    public void OperationModeSaveFailureKeepsActiveModeAndOtherDraftsWithoutShutdown()
    {
        Settings persisted = CreateStandaloneSettings(@"C:\mode-persisted", @"C:\mode-persisted");
        Settings draft = CreateStandaloneSettings(@"C:\mode-draft", @"C:\mode-draft");
        var session = new RecordingSettingsEditSession(persisted, draft);
        var failures = new List<Exception>();
        using var harness = SettingsDialogHarness.Create(persisted, activeLibraryProfile: true, session: session, reportApplyFailure: failures.Add);
        session.SetDraft(draft);
        session.ClearCalls();
        bool draftScan = !harness.Dialog.ScanBmsFilesOnStartup;
        harness.Dialog.ScanBmsFilesOnStartup = draftScan;
        harness.Dialogs.ConfirmationResult = UiDialogResult.FromMessageBoxResult(MessageBoxResult.OK);
        harness.Dialogs.MessageResult = UiDialogResult.FromMessageBoxResult(MessageBoxResult.OK);
        session.SaveFailure = new IOException("mode save unavailable");
        var presentation = new RecordingSettingsDialogPresentationPort();
        harness.Dialog.AttachPresentationPort(presentation);

        harness.Dialog.OperationModeLR2DB = true;

        Assert.AreEqual(0, harness.Lifetime.RestartCount);
        Assert.AreEqual(0, harness.Lifetime.ShutdownCount);
        Assert.AreEqual(0, session.ReloadCount);
        Assert.AreSame(draft, session.Values);
        Assert.IsFalse(session.Values.OperationModeLR2DB);
        Assert.IsFalse(harness.Dialog.OperationModeLR2DB);
        Assert.IsTrue(harness.Dialog.IsStandaloneOperationMode);
        Assert.AreEqual(draftScan, harness.Dialog.ScanBmsFilesOnStartup);
        CollectionAssert.DoesNotContain(presentation.Requests, "close");
        Assert.AreEqual(1, failures.Count);
    }

    /// <summary>項目が未編集でも実行中に捕捉した位置を一回保存して画面を閉じ、再読込みした XML の位置が更新されることを確認します。</summary>
    [TestMethod]
    public async Task NormalSaveWithoutDialogEditsPersistsCapturedPlacement()
    {
        using var directory = new TemporaryDirectory("placement-normal-save");
        string path = Path.Combine(directory.Path, "user.config");
        Settings settings = PortableSettingsPersistenceTests.OpenSettings(path);
        settings.OperationModeLR2DB = false;
        settings.BMSRootPath = directory.Path;
        settings.StandaloneBmsRootPaths = directory.Path;
        settings.BMSInstallDir = directory.Path;
        var initial = new WindowPlacement(0, 1, 0, 0, 0, 0, 10, 20, 810, 620);
        var captured = new WindowPlacement(0, 1, 0, 0, 0, 0, 30, 40, 830, 640);
        settings.LR2bodyWindowPlacement = Win32WindowPlacementAdapter.ToNative(initial);
        settings.Save();
        var session = new RecordingSettingsEditSession(settings, persistence: new SettingsEditSession(settings));
        using var harness = SettingsDialogHarness.Create(settings, activeLibraryProfile: true, session: session);
        var presentation = new RecordingSettingsDialogPresentationPort();
        harness.Dialog.AttachPresentationPort(presentation);
        new SettingsPlayerSettingsGateway(() => settings).UpdateWindowPlacement(captured);
        session.ClearCalls();

        await harness.Dialog.ApplySettingsAsync();

        Assert.AreEqual(1, session.SaveCount);
        CollectionAssert.Contains(presentation.Requests, "close");
        Assert.AreEqual(Win32WindowPlacementAdapter.ToNative(captured), PortableSettingsPersistenceTests.OpenSettings(path).LR2bodyWindowPlacement);
    }

    /// <summary>null の URL draft は getter で既定 URL として読めても draft を書き換えず、保存や変更判定を発生させないことを確認します。</summary>
    [TestMethod]
    [DoNotParallelize]
    public void TableListUrlGetterUsesCurrentDefaultWithoutMutatingInvalidDraft()
    {
        Settings draft = CreateStandaloneSettings(
            root: @"C:\settings-behavior\invalid-table-list-draft",
            installDirectory: @"C:\settings-behavior\invalid-table-list-draft");
        draft.TableListURL = null;

        using var harness = SettingsDialogHarness.Create(draft);

        Uri actual = harness.Dialog.TableListURL;

        Assert.AreEqual(new Uri(Settings.DefaultTableListUrl), actual);
        Assert.IsNull(harness.Session.Values.TableListURL);
        Assert.AreEqual(0, harness.Session.SaveCount);
        Assert.IsFalse(harness.Dialog.HasPendingSettingChanges());
    }

    /// <summary>右クリック設定の既定復元は編集器だけへ反映し、取消では元 JSON を保ち、明示保存でだけ復元 JSON を確定することを確認します。</summary>
    [TestMethod]
    [DoNotParallelize]
    public void RightClickRestoreDefaultsIsDraftOnlyUntilSave()
    {
        RunOnUiDispatcher(
            RightClickRestoreDefaultsIsDraftOnlyUntilSaveCoreAsync,
            nameof(RightClickRestoreDefaultsIsDraftOnlyUntilSave));
    }

    private static async Task RightClickRestoreDefaultsIsDraftOnlyUntilSaveCoreAsync()
    {
        const string originalJson = """
        {"webActions":[{"id":"custom","name":"Custom","urlTemplate":"https://example.test/{md5}","enabled":true,"chartKind":"All"}],"programActions":[{"id":"viewer","name":"Viewer","executablePath":"C:\\Tools\\viewer.exe","argumentTemplate":"{filePath}","enabled":true}]}
        """;

        Settings cancelSettings = CreateStandaloneSettings(
            @"C:\settings-behavior\right-click-cancel",
            @"C:\settings-behavior\right-click-cancel");
        cancelSettings.RightClickActionsJson = originalJson;
        using (var cancelHarness = SettingsDialogHarness.Create(cancelSettings))
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
        using (var saveHarness = SettingsDialogHarness.Create(saveSettings))
        {
            saveHarness.Dialog.RightClickActionSettingsEditor.RestoreDefaults();

            await saveHarness.Dialog.SaveSettings();

            Assert.AreEqual(RightClickActionSettingsDefaults.SerializedJson, saveHarness.Session.Values.RightClickActionsJson);
            Assert.AreEqual(1, saveHarness.Session.SaveCount);
            Assert.IsFalse(saveHarness.Dialog.RightClickActionSettingsEditor.IsDirty);
        }
    }

    /// <summary>ファイル引数を欠く外部プログラム編集は保存を拒否し、引数を修正した明示保存でだけ JSON を確定して dirty を解除することを確認します。</summary>
    [TestMethod]
    public async Task InvalidProgramDraftBlocksSaveUntilItsFieldsAreCorrected()
    {
        const string originalJson = "{\"webActions\":[],\"programActions\":[{\"id\":\"viewer\",\"name\":\"Viewer\",\"executablePath\":\"C:\\\\Tools\\\\viewer.exe\",\"argumentTemplate\":\"{filePath}\",\"enabled\":true}]}";
        Settings settings = CreateStandaloneSettings(
            @"C:\settings-behavior\right-click-invalid-program",
            @"C:\settings-behavior\right-click-invalid-program");
        settings.RightClickActionsJson = originalJson;
        using var harness = SettingsDialogHarness.Create(settings);

        harness.Dialog.RightClickActionSettingsEditor.ProgramActions[0].ArgumentTemplate = "--fixed";
        await harness.Dialog.SaveSettings();

        Assert.AreEqual(0, harness.Session.SaveCount);
        Assert.AreEqual(originalJson, harness.Session.Values.RightClickActionsJson);

        harness.Dialog.RightClickActionSettingsEditor.ProgramActions[0].ArgumentTemplate = "{filePath}";
        await harness.Dialog.SaveSettings();

        Assert.AreEqual(1, harness.Session.SaveCount);
        Assert.AreEqual(originalJson, harness.Session.Values.RightClickActionsJson);
        Assert.IsFalse(harness.Dialog.RightClickActionSettingsEditor.IsDirty);
    }

    /// <summary>未稼働では mode を draft に留め、稼働中は確認後の要求へ配送します。取消と要求失敗では元 mode を保ち、報告 callback の有無で通知経路を選ぶことを確認します。</summary>
    [TestMethod]
    [DoNotParallelize]
    public void SettingDialogOperationModeChange_ConfirmsAndRoutesThroughShellRequest()
    {
        using (var inactiveHarness = SettingsDialogHarness.Create(
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
            Assert.AreEqual(0, inactiveHarness.OperationModeRestart.RequestCount);
        }

        using (var acceptedHarness = SettingsDialogHarness.Create(
            CreateStandaloneSettings(
                @"C:\settings-behavior\accepted-root",
                @"C:\settings-behavior\accepted-root"),
            activeLibraryProfile: true))
        {
            acceptedHarness.Dialogs.ConfirmationResult = UiDialogResult.FromMessageBoxResult(MessageBoxResult.OK);

            acceptedHarness.Dialog.OperationModeLR2DB = true;

            Assert.AreEqual(1, acceptedHarness.Dialogs.ConfirmationCount);
            Assert.AreEqual(1, acceptedHarness.Session.SaveCount);
            Assert.AreEqual(1, acceptedHarness.OperationModeRestart.RequestCount);
            Assert.IsTrue(acceptedHarness.OperationModeRestart.LastRequest!.OperationMode);
            Assert.AreEqual(0, acceptedHarness.Lifetime.RestartCount);
            Assert.IsTrue(acceptedHarness.Session.Values.OperationModeLR2DB);
        }

        using (var rejectedHarness = SettingsDialogHarness.Create(
            CreateStandaloneSettings(
                @"C:\settings-behavior\rejected-root",
                @"C:\settings-behavior\rejected-root"),
            activeLibraryProfile: true))
        {
            rejectedHarness.Dialogs.ConfirmationResult = UiDialogResult.FromMessageBoxResult(MessageBoxResult.Cancel);

            rejectedHarness.Dialog.OperationModeLR2DB = true;

            Assert.IsFalse(rejectedHarness.Dialog.OperationModeLR2DB);
            Assert.IsFalse(rejectedHarness.Session.Values.OperationModeLR2DB);
            Assert.AreEqual(0, rejectedHarness.Session.SaveCount);
            Assert.AreEqual(0, rejectedHarness.Lifetime.RestartCount);
            Assert.AreEqual(0, rejectedHarness.OperationModeRestart.RequestCount);
        }

        using (var failedRestartHarness = SettingsDialogHarness.Create(
            CreateStandaloneSettings(
                @"C:\settings-behavior\restart-failure-root",
                @"C:\settings-behavior\restart-failure-root"),
            activeLibraryProfile: true))
        {
            failedRestartHarness.Dialogs.ConfirmationResult = UiDialogResult.FromMessageBoxResult(MessageBoxResult.OK);
            // 再起動要求の失敗と通知自体の失敗を混ぜない。
            failedRestartHarness.Dialogs.MessageResult = UiDialogResult.FromMessageBoxResult(MessageBoxResult.OK);
            var requestFailure = new InvalidOperationException("restart request failed");
            failedRestartHarness.OperationModeRestart.Failure = requestFailure;

            failedRestartHarness.Dialog.OperationModeLR2DB = true;

            Assert.IsFalse(failedRestartHarness.Dialog.OperationModeLR2DB);
            Assert.IsFalse(failedRestartHarness.Session.Values.OperationModeLR2DB);
            Assert.IsTrue(failedRestartHarness.Dialog.IsEditCompletionEnabled);
            Assert.IsTrue(failedRestartHarness.Dialog.IsEditCancellationEnabled);
            Assert.AreEqual(1, failedRestartHarness.Dialogs.ConfirmationCount);
            Assert.AreEqual(1, failedRestartHarness.OperationModeRestart.RequestCount);
            Assert.AreEqual(0, failedRestartHarness.Lifetime.RestartCount);
            Assert.AreEqual(0, failedRestartHarness.Lifetime.ShutdownCount);
            Assert.AreEqual(1, failedRestartHarness.Dialogs.MessageCount);
            Assert.AreEqual(
                string.Format(Resources.SettingsApplyIncomplete, requestFailure.Message),
                failedRestartHarness.Dialogs.LastMessageText);
            Assert.AreEqual(0, failedRestartHarness.Session.SaveCount);
        }

        var failedRequestHarnessReportedFailures = new List<Exception>();
        using (var failedRequestHarness = SettingsDialogHarness.Create(
            CreateStandaloneSettings(
                @"C:\settings-behavior\restart-request-failure-root",
                @"C:\settings-behavior\restart-request-failure-root"),
            activeLibraryProfile: true,
            reportApplyFailure: exception => failedRequestHarnessReportedFailures.Add(exception)))
        {
            failedRequestHarness.Dialogs.ConfirmationResult = UiDialogResult.FromMessageBoxResult(MessageBoxResult.OK);
            var requestFailure = new InvalidOperationException("restart request failed");
            failedRequestHarness.OperationModeRestart.Failure = requestFailure;

            failedRequestHarness.Dialog.OperationModeLR2DB = true;

            Assert.IsFalse(failedRequestHarness.Dialog.OperationModeLR2DB);
            Assert.IsFalse(failedRequestHarness.Session.Values.OperationModeLR2DB);
            Assert.IsTrue(failedRequestHarness.Dialog.IsEditCompletionEnabled);
            Assert.IsTrue(failedRequestHarness.Dialog.IsEditCancellationEnabled);
            Assert.AreEqual(1, failedRequestHarness.Dialogs.ConfirmationCount);
            Assert.AreEqual(1, failedRequestHarness.OperationModeRestart.RequestCount);
            Assert.AreEqual(0, failedRequestHarness.Lifetime.RestartCount);
            Assert.AreEqual(0, failedRequestHarness.Lifetime.ShutdownCount);
            Assert.AreEqual(0, failedRequestHarness.Dialogs.MessageCount);
            Assert.AreEqual(0, failedRequestHarness.Session.SaveCount);
            Assert.AreEqual(1, failedRequestHarnessReportedFailures.Count);
            Assert.AreSame(requestFailure, failedRequestHarnessReportedFailures[0]);
        }
    }

    /// <summary>standalone は設定、LR2 は config の保存を先行させ、検索対象適用→差分再読込み→LR2 同期の順で追加 root を配送することを確認します。</summary>
    [TestMethod]
    [DoNotParallelize]
    public async Task SearchRootChanges_UpdateRuntimeSearchTargetsBeforeFileDiffReload()
    {
        string standaloneRoot = CreateTemporaryRoot("settings-picker-standalone");
        string standaloneAddedRoot = Path.Combine(standaloneRoot, "added");
        Directory.CreateDirectory(standaloneAddedRoot);
        try
        {
            using var harness = SettingsDialogHarness.Create(
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
                    return Task.FromResult(new LibraryFileInitializationResult());
                },
                harness.SyncWorkflow,
                _ => Task.CompletedTask);

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
            using var harness = SettingsDialogHarness.Create(settings, libraryAttached: true);
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
                    return Task.FromResult(new LibraryFileInitializationResult());
                },
                harness.SyncWorkflow,
                _ => Task.CompletedTask);

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

    /// <summary>standalone 保存・検索対象適用・LR2 config 保存の各失敗を呼出元へ返し、失敗した段階より後の適用／再読込みを始めないことを確認します。</summary>
    [TestMethod]
    [DoNotParallelize]
    public async Task SearchRootChanges_SaveAndApplyFailuresDoNotReload()
    {
        string saveFailureRoot = CreateTemporaryRoot("settings-picker-save-failure");
        string saveFailureAddedRoot = Path.Combine(saveFailureRoot, "added");
        Directory.CreateDirectory(saveFailureAddedRoot);
        try
        {
            using var harness = SettingsDialogHarness.Create(
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
            using var harness = SettingsDialogHarness.Create(
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
            using var harness = SettingsDialogHarness.Create(settings, libraryAttached: true);
            var sequence = new List<string>();
            harness.AttachSequence(sequence);
            string configDirectory = Path.GetDirectoryName(configPath)!;
            Directory.Delete(configDirectory, recursive: true);

            IOException saveFailure = await Assert.ThrowsExceptionAsync<IOException>(() =>
                harness.Dialog.AddBmsSearchRootPathFromMainWindowPicker(addedRoot));

            StringAssert.Contains(saveFailure.Message, configPath);
            CollectionAssert.AreEqual(Array.Empty<string>(), sequence);
            Assert.AreEqual(0, harness.SearchRoots.ApplyCount);
            Assert.AreEqual(0, harness.State.FileDiffReloadCount);
        }
        finally
        {
            TryDeleteDirectory(lr2SaveFailureRoot);
        }
    }

    /// <summary>config を一時書換えする preview の前後で検索 root を保存し、preview 中に生成した draft が元の表示・音量値と両方の追加 root を保持することを確認します。</summary>
    [TestMethod]
    public async Task Lr2SettingsDraftCreatedDuringPreviewRemainsSavedAfterPreviewEnds()
    {
        string root = CreateTemporaryRoot("settings-preview-draft");
        SettingsDialogHarness? harness = null;
        try
        {
            (string songDbPath, string configPath, string bmsRoot) = CreateValidLr2Layout(root);
            var configDocument = XDocument.Load(configPath);
            configDocument.Element("config")?.Element("system")?.Add(
                new XElement("windowsize_x", "640"),
                new XElement("windowsize_y", "480"),
                new XElement("screenmode", "0"));
            configDocument.Element("config")?.Add(
                new XElement("sound", new XElement("volumemaster", "23")));
            configDocument.Save(configPath, SaveOptions.None);

            Settings settings = CreateLr2Settings(root, bmsRoot);
            settings.LR2SongDBPath = songDbPath;
            settings.LR2ConfigXmlPath = configPath;
            string addedRoot = Path.Combine(root, "BMS-added");
            Directory.CreateDirectory(addedRoot);
            string preStartAddedRoot = Path.Combine(root, "BMS-before-preview");
            Directory.CreateDirectory(preStartAddedRoot);

            var gateway = new ExternalPlayerProcessGatewayTests.RecordingExternalPlayerProcessGateway();
            gateway.Session.KeepRunning = true;
            gateway.Session.MainWindowHandle = new ExternalWindowHandle(new IntPtr(21));
            gateway.Session.BeforeStart = () =>
            {
                harness = SettingsDialogHarness.Create(settings, libraryAttached: true);
            };
            string executablePath = Path.Combine(root, "LR2body.exe");
            string chartPath = Path.Combine(root, "preview.bms");
            File.WriteAllBytes(executablePath, []);
            File.WriteAllText(chartPath, "#BPM 120");
            var player = new LR2body(
                executablePath,
                new LR2Config(configPath),
                new ExternalPlayerProcessGatewayTests.RecordingPlayerSettingsGateway(),
                gateway);
            ((IExternalWindowPlayer)player).AttachWindowHost(
                new ExternalPlayerProcessGatewayTests.RecordingExternalPlayerWindowHost(
                    new ExternalWindowHandle(new IntPtr(99))));

            using var preStartHarness = SettingsDialogHarness.Create(
                CreateLr2Settings(root, bmsRoot),
                libraryAttached: true);
            await preStartHarness.Dialog.AddBmsSearchRootPathFromMainWindowPicker(preStartAddedRoot);

            var savedBeforePreview = XDocument.Load(configPath);
            CollectionAssert.Contains(
                savedBeforePreview.Element("config")?.Element("jukebox")?.Elements("path")
                    .Select(path => path.Value.TrimEnd('\\'))
                    .ToArray(),
                preStartAddedRoot);

            _ = player.PlayStart(chartPath, (EventHandler)null!);

            var savedAfterStart = XDocument.Load(configPath);
            CollectionAssert.Contains(
                savedAfterStart.Element("config")?.Element("jukebox")?.Elements("path")
                    .Select(path => path.Value.TrimEnd('\\'))
                    .ToArray(),
                preStartAddedRoot);
            gateway.Session.KeepRunning = false;
            gateway.Session.RaiseExited();

            Assert.IsNotNull(harness);
            await harness!.Dialog.AddBmsSearchRootPathFromMainWindowPicker(addedRoot);

            var savedDocument = XDocument.Load(configPath);
            Assert.AreEqual("640", savedDocument.Element("config")?.Element("system")?.Element("windowsize_x")?.Value);
            Assert.AreEqual("480", savedDocument.Element("config")?.Element("system")?.Element("windowsize_y")?.Value);
            Assert.AreEqual("0", savedDocument.Element("config")?.Element("system")?.Element("screenmode")?.Value);
            Assert.AreEqual("23", savedDocument.Element("config")?.Element("sound")?.Element("volumemaster")?.Value);
            CollectionAssert.Contains(
                savedDocument.Element("config")?.Element("jukebox")?.Elements("path")
                    .Select(path => path.Value.TrimEnd('\\'))
                    .ToArray(),
                addedRoot);
            CollectionAssert.Contains(
                savedDocument.Element("config")?.Element("jukebox")?.Elements("path")
                    .Select(path => path.Value.TrimEnd('\\'))
                    .ToArray(),
                preStartAddedRoot);
        }
        finally
        {
            harness?.Dispose();
            TryDeleteDirectory(root);
        }
    }

    /// <summary>差分再読込み失敗では同期を呼ばず、同期失敗も元例外で返して file reload pending を残し、後段へ成功扱いで進まないことを確認します。</summary>
    [TestMethod]
    [DoNotParallelize]
    public async Task SearchRootChanges_ReloadAndLr2SyncFailuresPropagateWithoutLaterQueue()
    {
        string root = CreateTemporaryRoot("settings-picker-reload-failure");
        string addedRoot = Path.Combine(root, "added");
        Directory.CreateDirectory(addedRoot);
        try
        {
            using var harness = SettingsDialogHarness.Create(
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
                    return Task.FromException<LibraryFileInitializationResult>(reloadFailure);
                },
                harness.SyncWorkflow,
                _ => Task.CompletedTask);

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
            using var harness = SettingsDialogHarness.Create(
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
                    return Task.FromResult(new LibraryFileInitializationResult());
                },
                harness.SyncWorkflow,
                _ => Task.CompletedTask);

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

    /// <summary>standalone でも利用者が LR2body を選べば指定実行ファイルの player を生成し、ライブラリ mode を理由に既定 player へ置き換えないことを確認します。</summary>
    [TestMethod]
    [DoNotParallelize]
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

    /// <summary>standalone でも LR2body 実行ファイル／root が不正なら設定エラーを示し、保存・player 作成・反映を始めないことを確認します。</summary>
    [TestMethod]
    [DoNotParallelize]
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

            using var harness = SettingsDialogHarness.Create(values, activeLibraryProfile: true);
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

    /// <summary>選択済み LR2 config の XML が壊れていれば選択と draft を保って config エラーを示し、保存や player 作成へ進まないことを確認します。</summary>
    [TestMethod]
    [DoNotParallelize]
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

            using var harness = SettingsDialogHarness.Create(values, activeLibraryProfile: true);
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

    /// <summary>両 mode で出力先と導入先を参照しても利用者の保存パスを消去せず、保存や再読込み pending を発生させないことを確認します。</summary>
    [DataTestMethod]
    // mode で参照対象から外れる値も、保存済みなら消去しない。
    [DataRow(false)]
    [DataRow(true)]
    [DoNotParallelize]
    public void SettingDialogModeSpecificGetters_DoNotClearPersistedSettings(bool operationModeLr2Db)
    {
        Settings values = CreateStandaloneSettings(
            root: @"C:\settings-behavior\root",
            installDirectory: @"C:\settings-behavior\root");
        values.OperationModeLR2DB = operationModeLr2Db;
        values.LR2CustomFolderOutputBaseDir = @"C:\settings-behavior\custom-output";
        values.LR2CustomFolderOutputBaseDirRootType = @"C:\settings-behavior\custom-root-output";
        values.BMSInstallDir = @"C:\settings-behavior\install";

        using var harness = SettingsDialogHarness.Create(values);
        SettingsDialogViewModel dialog = harness.Dialog;
        Settings sessionValues = harness.Session.Values;

        Assert.AreEqual(@"C:\settings-behavior\custom-output", dialog.LR2CustomFolderOutputDir);
        Assert.AreEqual(@"C:\settings-behavior\custom-root-output", dialog.LR2CustomFolderAsRootOutputDir);
        Assert.AreEqual(@"C:\settings-behavior\install", dialog.BMSInstallDir);

        // 選択中 mode で未使用のパスも、検証向け参照を理由に保存値を消さない。
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

    /// <summary>mode／score／検索 root／LR2 DB／出力先の変更で必要な再読込みだけを選び、Reset と保存後の解除、score 失敗後の再試行が再保存なしで完了することを確認します。</summary>
    [TestMethod]
    [DoNotParallelize]
    public void SettingDialogReloadDecision_UsesExplicitSettingDiffs()
    {
        RunOnUiDispatcher(
            SettingDialogReloadDecision_UsesExplicitSettingDiffsCoreAsync,
            nameof(SettingDialogReloadDecision_UsesExplicitSettingDiffs));
    }

    private static async Task SettingDialogReloadDecision_UsesExplicitSettingDiffsCoreAsync()
    {
        using (var modeHarness = SettingsDialogHarness.Create(CreateStandaloneSettings(
            @"C:\settings-behavior\mode-root",
            @"C:\settings-behavior\mode-root")))
        {
            modeHarness.Dialog.OperationModeLR2DB = true;
            Assert.AreEqual(SettingsDialogViewModel.RestartMode.All, modeHarness.Dialog.IsNeedRestartForSaved());
        }

        using (var scoreHarness = SettingsDialogHarness.Create(CreateStandaloneSettings(
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
            using var standaloneRootHarness = SettingsDialogHarness.Create(CreateStandaloneSettings(
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
            using var lr2SongDbHarness = SettingsDialogHarness.Create(settings);
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
            using var lr2AddHarness = SettingsDialogHarness.Create(settings);
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
            using var lr2RemoveHarness = SettingsDialogHarness.Create(settings);
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

        using (var customOutputHarness = SettingsDialogHarness.Create(CreateLr2Settings(
            @"C:\settings-behavior\custom-root",
            @"C:\settings-behavior\custom-root\BMS")))
        {
            customOutputHarness.Session.Values.LR2CustomFolderOutputBaseDir = @"C:\settings-behavior\custom-root\new-output";
            Assert.AreEqual(SettingsDialogViewModel.RestartMode.FolderOnly, customOutputHarness.Dialog.IsNeedRestartForSaved());
        }

        using (var resetHarness = SettingsDialogHarness.Create(CreateStandaloneSettings(
            @"C:\settings-behavior\reset-root",
            @"C:\settings-behavior\reset-root")))
        {
            resetHarness.Session.Values.FolderNameFormat = "draft-%TITLE%";
            Assert.IsTrue(resetHarness.Dialog.HasPendingSettingChanges());

            resetHarness.Dialog.ResetSettings();

            Assert.AreEqual(SettingsDialogViewModel.RestartMode.None, resetHarness.Dialog.IsNeedRestartForSaved());
            Assert.IsFalse(resetHarness.Dialog.HasPendingSettingChanges());
        }

        using (var saveHarness = SettingsDialogHarness.Create(CreateStandaloneSettings(
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
            using var retryHarness = SettingsDialogHarness.Create(
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

    /// <summary>両 mode で導入先が空なら導入先エラーを示し、有効な導入先を選ぶと他のエラーを残さず検証を通すことを確認します。</summary>
    [TestMethod]
    [DoNotParallelize]
    public void SettingDialogValidation_RequiresInstallDestinationInAllOperationModes()
    {
        string standaloneRoot = CreateTemporaryRoot("settings-validation-standalone");
        try
        {
            Settings standalone = CreateStandaloneSettings(standaloneRoot, installDirectory: string.Empty);
            using (var standaloneHarness = SettingsDialogHarness.Create(standalone))
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

            using var linkedHarness = SettingsDialogHarness.Create(linked);
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

    /// <summary>beatoraja root と player が有効でも score DB が欠ければ DB エラーを返し、root 不正と誤案内しないことを確認します。</summary>
    [TestMethod]
    [DoNotParallelize]
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

        using var harness = SettingsDialogHarness.Create(values);

        Assert.IsFalse(harness.Dialog.CheckValidation(out string error));
        StringAssert.Contains(error, Resources.Error_InvalidBeatorajaScoreDbPath);
        Assert.IsFalse(error.Contains(Resources.Error_InvalidBeatorajaRootPath, StringComparison.Ordinal));
    }

    /// <summary>standalone root 一覧が空ならその設定の翻訳済みエラーを返し、無関係な beatoraja エラーを出さないことを確認します。</summary>
    [TestMethod]
    [DoNotParallelize]
    public void StandaloneValidationReportsLocalizedRootErrorForEmptyRootList()
    {
        using var fixture = new TemporaryDirectory("settings-validation-empty-standalone");
        Settings values = CreateStandaloneSettings(fixture.Path, fixture.Path);
        values.StandaloneBmsRootPaths = string.Empty;

        using var harness = SettingsDialogHarness.Create(values);
        harness.Dialog.StandaloneBmsRootPathList.Clear();

        Assert.IsFalse(harness.Dialog.CheckValidation(out string error));
        StringAssert.Contains(error, Resources.Error_InvalidStandaloneBmsRootPaths);
        Assert.IsFalse(error.Contains(Resources.Error_InvalidBeatorajaRootPath, StringComparison.Ordinal));
    }

    /// <summary>大小文字・末尾区切りの重複だけを除き親子 root と追加導入先を残して draft 選択へ反映し、旧単一 root の保存値を変更しないことを確認します。</summary>
    [TestMethod]
    [DoNotParallelize]
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

            using var harness = SettingsDialogHarness.Create(values);
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

    private static void RunOnUiDispatcher(Func<Task> operation, string operationName)
    {
        TestUiDispatcherHost.Invoke(() =>
        {
            string previousTheme = Settings.Default.AppearanceTheme;
            CultureInfo? previousCulture = Resources.Culture;
            try
            {
                TestUiDispatcherHost.AwaitTaskOnDispatcher(operation(), operationName);
            }
            finally
            {
                Settings.Default.AppearanceTheme = previousTheme;
                AppThemeService.ApplyTheme(previousTheme);
                if (previousCulture is null)
                {
                    Resources.Culture = null;
                }
                else
                {
                    ResourceService.Current.ChangeCulture(previousCulture.Name);
                }
            }
        });
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
        return MainWindowViewModelTestFactory.CreateIsolatedSettings(values =>
            {
                values.OperationModeLR2DB = false;
                values.BMSRootPath = root;
                values.StandaloneBmsRootPaths = root;
                values.BMSInstallDir = installDirectory;
                values.TableListURL = new Uri("http://127.0.0.1:1/table-list.json");
                values.FolderNameFormat = "%TITLE%";
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
            });
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
            RecordingOperationModeRestartPort operationModeRestart)
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
            OperationModeRestart = operationModeRestart;
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

        internal RecordingOperationModeRestartPort OperationModeRestart { get; }


        internal static SettingsDialogHarness Create(
            Settings values,
            bool activeLibraryProfile = false,
            RecordingSettingsEditSession? session = null,
            Action<Exception>? reportApplyFailure = null,
            bool libraryAttached = false,
            Func<Task>? reloadFileDiff = null,
            TestAudioSettingsGateway? audioSettings = null,
            ApplicationComposition? composition = null)
        {
            session ??= new RecordingSettingsEditSession(values);
            var state = new RecordingSettingsStatePort(activeLibraryProfile);
            state.ReloadFileDiffHandler = reloadFileDiff;
            var lifetime = new RecordingApplicationLifetime();
            var dialogs = new RecordingDialogService();
            var workspace = new RecordingWorkspacePort();
            var customFolder = new RecordingCustomFolderOutputPort(
                () => composition?.CustomFolderOutputSettingsProvider() ?? CustomFolderOutputSettingsSnapshot.CreateCurrent(session.Values));
            var playHistory = new RecordingPlayHistoryPort();
            var searchRoots = new RecordingSearchRootRuntimePort();
            searchRoots.IsLibraryAttachedValue = libraryAttached;
            var playerFactory = new ThrowingPlayerFactoryPort();
            var playbackRuntime = new RecordingPlaybackRuntimePort();
            var syncRuntime = new NoOpLr2SongDbSyncRuntime();
            var syncWorkflow = new Lr2SongDbSyncWorkflowOwner(syncRuntime);
            var operationModeRestart = new RecordingOperationModeRestartPort(session);
            SettingsDialogViewModel dialog = composition != null
                ? composition.CreateSettingDialogViewModel(state, workspace, customFolder, playHistory, searchRoots,
                    playerFactory, playbackRuntime, syncWorkflow, operationModeRestart.RequestAsync)
                : new SettingsDialogViewModel(
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
                audioSettingsGateway: audioSettings ?? new TestAudioSettingsGateway(),
                requestOperationModeRestart: operationModeRestart.RequestAsync,
                audioDeviceTestWorkflow: AudioDeviceTestWorkflowTestFactory.Create());
            if (composition != null)
            {
                System.Reflection.BindingFlags flags = System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic;
                System.Reflection.FieldInfo gatewayField = typeof(SettingsDialogViewModel).GetField("audioSettingsGateway", flags)
                    ?? throw new InvalidOperationException("Settings audio dependency was not found.");
                System.Reflection.FieldInfo ownerField = typeof(SettingsDialogViewModel).GetField("audioDeviceTestWorkflow", flags)
                    ?? throw new InvalidOperationException("Settings audio owner was not found.");
                (ownerField.GetValue(dialog) as AudioDeviceTestWorkflowOwner)?.Dispose();
                gatewayField.SetValue(dialog, audioSettings ?? new TestAudioSettingsGateway());
                AudioDeviceTestWorkflowOwner audioOwner = AudioDeviceTestWorkflowTestFactory.Create();
                audioOwner.ConfigurePresentation(() => session.Values, new TestAudioDeviceCatalog());
                ownerField.SetValue(dialog, audioOwner);
                (typeof(SettingsDialogViewModel).GetMethod("backupSavedSettings", flags)
                    ?? throw new InvalidOperationException("Settings baseline initialization was not found.")).Invoke(dialog, null);
            }
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
                operationModeRestart);
        }

        internal void AttachSequence(IList<string> sequence)
        {
            Session.Sequence = sequence;
            Lifetime.Sequence = sequence;
            Dialogs.Sequence = sequence;
            SearchRoots.Sequence = sequence;
            OperationModeRestart.Sequence = sequence;
        }

        public void Dispose()
        {
            Dialog.Dispose();
        }
    }

    private sealed class RecordingOperationModeRestartPort
    {
        private readonly RecordingSettingsEditSession session;

        internal RecordingOperationModeRestartPort(RecordingSettingsEditSession session)
        {
            this.session = session ?? throw new ArgumentNullException(nameof(session));
        }

        internal int RequestCount { get; private set; }

        internal OperationModeRestartRequest? LastRequest { get; private set; }

        internal IList<string>? Sequence { get; set; }

        internal Exception? Failure { get; set; }

        internal Task<bool> RequestAsync(OperationModeRestartRequest request)
        {
            RequestCount++;
            LastRequest = request;
            Sequence?.Add("request");
            if (Failure != null)
            {
                return Task.FromException<bool>(Failure);
            }

            session.SaveOperationModeForRestart(request.OperationMode, request.HistoryIdentity);
            return Task.FromResult(true);
        }
    }

    private sealed class DirectSettingsEditSession : ISettingsEditSession
    {
        public void SaveOperationModeForRestart(bool operationMode, string historyIdentity)
        {
            Values.OperationModeLR2DB = operationMode;
            Values.PlayHistorySelectedDisplayTargetIdentity = historyIdentity;
            Save();
            Reload();
        }

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
        public void SaveOperationModeForRestart(bool operationMode, string historyIdentity)
        {
            SaveCount++;
            Calls.Add("save");
            Sequence?.Add("save");
            if (SaveFailure != null)
            {
                throw SaveFailure;
            }
            if (persistence != null)
            {
                persistence.SaveOperationModeForRestart(operationMode, historyIdentity);
            }
            else
            {
                persistedValues.OperationModeLR2DB = operationMode;
                persistedValues.PlayHistorySelectedDisplayTargetIdentity = historyIdentity;
            }
            Reload();
        }
        private readonly ISettingsEditSession? persistence;
        private readonly Settings persistedValues;
        private readonly bool replaceValuesOnReload;

        internal RecordingSettingsEditSession(Settings values, ISettingsEditSession? persistence = null)
        {
            this.persistence = persistence;
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
        }

        public void Reload()
        {
            ReloadCount++;
            Calls.Add("reload");
            Sequence?.Add("reload");
            persistence?.Reload();
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
            if (SaveFailure != null)
            {
                throw SaveFailure;
            }
            persistence?.Save();
            if (persistence == null)
            {
                foreach (SettingsPropertyValue value in Values.PropertyValues)
                {
                    value.IsDirty = false;
                }
            }
        }
    }

    private sealed class RecordingSettingsStatePort : ISettingsDialogStatePort
    {

        internal RecordingSettingsStatePort(bool hasActiveLibraryProfile)
        {
            this.hasActiveLibraryProfile = hasActiveLibraryProfile;
        }

        public bool HasActiveLibraryProfile
        {
            get
            {
                return hasActiveLibraryProfile;
            }
        }

        private bool isLibraryOperationInProgress;

        public bool IsLibraryOperationInProgress
        {
            get
            {
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

        public bool IsInitializationCompletionCurrent(long operationToken) => operationToken == 0L;

        public Task<StartupInitializationResult> CompleteRequiredInitializationAfterAdmissionAsync(StartupInitializationResult result)
            => Task.FromResult(result with { CompletionPublished = result.Outcome == StartupInitializationOutcome.Succeeded });


        public Task<LeapYearFolderRepairApproval?> PrepareLibraryInitializationAsync() => Task.FromResult<LeapYearFolderRepairApproval?>(null);

        public Task PresentLeapYearFolderRepairAsync(LeapYearFolderRepairNotification? notification) => Task.CompletedTask;

        public Task<StartupInitializationResult> InitializeLibraryAsync(LibraryFileMutationCapability? capability = null,
            LeapYearFolderRepairApproval? leapYearRepairApproval = null, Action<LeapYearFolderRepairNotification>? repairNotificationObserver = null, bool forceBeatorajaBmtProjection = false)
        {
            InitializeCount++;
            return Task.FromResult(new StartupInitializationResult(StartupInitializationOutcome.Succeeded));
        }

        public async Task<StartupInitializationResult> ReloadScoresOnlyAsync(LibraryFileMutationCapability capability)
        {
            ScoreReloadCount++;
            if (ThrowOnScoreReload)
            {
                throw new InvalidOperationException("score reload failed");
            }

            await Task.CompletedTask;
            return new(StartupInitializationOutcome.Succeeded);
        }

        public Task PresentLibraryDirectoryWarningAsync(BeMusicSeeker.Models.BmsLibraryInternal.LibraryDirectoryPreflightException failure) => Task.CompletedTask;

        public async Task<StartupInitializationResult> ReloadFileDiffAsync(LibraryFileMutationCapability? capability = null)
        {
            FileDiffReloadCount++;
            if (ReloadFileDiffWorkflowOwner != null)
            {
                await ReloadFileDiffWorkflowOwner.ReloadAsync(
                    new FileDiffReloadRequest("SettingsDialog.SearchRoot", 0L));
                return new(StartupInitializationOutcome.Succeeded);
            }
            await (ReloadFileDiffHandler?.Invoke() ?? Task.CompletedTask);
            return new(StartupInitializationOutcome.Succeeded);
        }

#pragma warning disable CS0067 // インターフェイスのイベント面を満たすが、このテストダブルでは発火させない。
        public event EventHandler? LibraryOperationAvailabilityChanged;

        public event Action<Lr2PlayHistorySchemaStatusSnapshot>? Lr2PlayHistorySchemaStatusChanged;
#pragma warning restore CS0067
    }

    private sealed class RecordingApplicationLifetime : IApplicationLifetimePort
    {

        public bool IsFirstStartup
        {
            get
            {
                return false;
            }
        }

        internal int RestartCount { get; private set; }

        internal int ShutdownCount { get; private set; }

        internal IList<string>? Sequence { get; set; }

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
            ShutdownCount++;
            Sequence?.Add("shutdown");
        }

        public Task RestartApplicationAsync()
        {
            RestartCount++;
            Sequence?.Add("restart");
            return Task.CompletedTask;
        }
    }

    private sealed class RecordingDialogService : IUiDialogService
    {

        internal int MessageCount { get; private set; }

        internal int ConfirmationCount { get; private set; }

        internal IList<string>? Sequence { get; set; }

        internal UiDialogResult MessageResult { get; set; }
            = UiDialogResult.Failed(new InvalidOperationException("unexpected settings behavior message"));

        internal string? LastMessageText { get; private set; }

        internal UiDialogResult ConfirmationResult { get; set; }
            = UiDialogResult.Failed(new InvalidOperationException("unexpected settings behavior confirmation"));

        public Task<UiDialogResult> ShowMessageAsync(UiMessageRequest request, CancellationToken cancellationToken = default)
        {
            MessageCount++;
            LastMessageText = request.MessageBoxText;
            Sequence?.Add("notify");
            return Task.FromResult(MessageResult);
        }

        public Task<UiDialogResult> ConfirmAsync(UiConfirmationRequest request, CancellationToken cancellationToken = default)
        {
            ConfirmationCount++;
            Sequence?.Add("confirm");
            return Task.FromResult(ConfirmationResult);
        }

        public Task<UiWindowDialogResult<TResult>> ShowWindowAsync<TWindow, TResult>(
            UiWindowDialogRequest<TWindow, TResult> request,
            CancellationToken cancellationToken = default)
            where TWindow : Window
        {
            return Task.FromResult(new UiWindowDialogResult<TResult>(UiDialogStatus.Failed, error: new InvalidOperationException("unexpected settings behavior window")));
        }

        public Task<UiFilePickerResult> PickFileAsync(UiFilePickerRequest request, CancellationToken cancellationToken = default)
        {
            return Task.FromResult(new UiFilePickerResult(UiDialogStatus.Failed, error: new InvalidOperationException("unexpected settings behavior file picker")));
        }

        public Task<UiFolderPickerResult> PickFolderAsync(UiFolderPickerRequest request, CancellationToken cancellationToken = default)
        {
            return Task.FromResult(new UiFolderPickerResult(UiDialogStatus.Failed, error: new InvalidOperationException("unexpected settings behavior folder picker")));
        }

        public Task<UiSaveFilePickerResult> PickSaveFileAsync(UiSaveFilePickerRequest request, CancellationToken cancellationToken = default)
        {
            return Task.FromResult(new UiSaveFilePickerResult(UiDialogStatus.Failed, error: new InvalidOperationException("unexpected settings behavior save picker")));
        }

        public Task<UiProgressResult> RunWithProgressAsync(
            UiProgressRequest request,
            Func<UiProgressContext, Task> operation,
            CancellationToken cancellationToken = default)
        {
            return Task.FromResult(new UiProgressResult(UiDialogStatus.Failed, error: new InvalidOperationException("unexpected settings behavior progress")));
        }
    }

    private sealed class RecordingWorkspacePort : ISettingsDialogWorkspacePort
    {

        public bool HasPlaylistTables
        {
            get
            {
                return false;
            }
        }

        public long PlaylistCatalogVersion
        {
            get
            {
                return 0;
            }
        }

        public IReadOnlyList<PlaylistTablePresentationSnapshot> CapturePlaylistPresentationSnapshots()
        {
            return [];
        }

        public bool HasUnimportedBeatorajaTableUrlsForBmtOutputGuide(string beatorajaRootPath)
        {
            return false;
        }

        public void SchedulePlaylistUrlCompletionRefresh(string reason)
        {
        }

        public Task ExportBeatorajaBmtAsync(string reason, string cleanupTablePath, LibraryFileMutationCapability capability, bool forceProjection = false)
        {
            return Task.CompletedTask;
        }

        public Task RunWithPlaylistOperationNotificationsAsync(Func<Task> operation, string operationName)
        {
            return operation();
        }

#pragma warning disable CS0067 // インターフェイスのイベント面を満たすが、このテストダブルでは発火させない。
        public event EventHandler<PlaylistCatalogChangedEventArgs>? PlaylistCatalogChanged;
#pragma warning restore CS0067
    }

    private sealed class RecordingCustomFolderOutputPort : ISettingsDialogCustomFolderOutputPort
    {
        private readonly ChartFileOperationSynchronizer outputAdmission = new();

        public bool CanBeginOutputOperation => outputAdmission.CanEnter;

        public LibraryFileMutationLease? TryBeginOutputOperation()
            => outputAdmission.TryEnter(out IDisposable lease) ? (LibraryFileMutationLease)lease : null;

        private readonly Func<CustomFolderOutputSettingsSnapshot> settingsProvider;

        internal RecordingCustomFolderOutputPort(
            Func<CustomFolderOutputSettingsSnapshot> settingsProvider)
        {
            this.settingsProvider = settingsProvider;
        }

        public CustomFolderOutputSettingsSnapshot CustomFolderOutputSettings
        {
            get
            {
                return settingsProvider();
            }
        }

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
            CustomFolderOutputSettingsSnapshot settings)
        {
            return false;
        }

        public int ApplyCustomFolderAdditionalOutputBaseRegistrationChanges(
            string previousAdditionalOutputBaseDirectories,
            IReadOnlyDictionary<string, string> pendingRenames,
            CustomFolderOutputSettingsSnapshot settings, LibraryFileMutationCapability? capability = null)
        {
            return 0;
        }
    }

    private sealed class RecordingPlayHistoryPort : ISettingsDialogPlayHistoryPort
    {

        public void InvalidateReadCache(string reason)
        {
        }

        public void RefreshDisplayTargetCatalog(bool queueRefreshWhenSelectionChanges = true)
        {
        }

        public void RefreshDisplayTargetSetsFromSettings(string serializedDisplayTargetSets, bool queueRefreshWhenSelectionChanges)
        {
        }
    }

    private sealed class RecordingSearchRootRuntimePort : ISettingsDialogSearchRootRuntimePort
    {

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
                return IsLibraryAttachedValue;
            }
        }

        public bool HasOwnedChartUnderRealPath(string directoryPath)
        {
            return false;
        }

        public void ApplySearchTargets(IReadOnlyList<string> searchTargets)
        {
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
            Sequence?.Add("invalidate");
        }
    }

    private sealed class ThrowingPlayerFactoryPort : ISettingsDialogPlayerFactoryPort
    {

        internal int DefaultCreateCount { get; private set; }

        internal int ConfiguredCreateCount { get; private set; }

        public IBMSPlayer CreateDefaultBmsPlayer()
        {
            DefaultCreateCount++;
            throw new InvalidOperationException("player creation is outside this settings behavior unit");
        }

        public IBMSPlayer CreateBmsPlayerForSettings(StartupSettingsSnapshot settings)
        {
            ConfiguredCreateCount++;
            throw new InvalidOperationException("player creation is outside this settings behavior unit");
        }
    }

    private sealed class RecordingPlaybackRuntimePort : ISettingsDialogPlaybackRuntimePort
    {

        internal int ApplyCount { get; private set; }

        internal int NotifyCount { get; private set; }

        public Task ApplyPlayerSettingsAsync(IBMSPlayer replacementPlayer)
        {
            ApplyCount++;
            return Task.CompletedTask;
        }

        public void NotifySettingsChanged()
        {
            NotifyCount++;
        }

        public Task StopPlayback()
        {
            return Task.CompletedTask;
        }
    }

    private sealed class NoOpLr2SongDbSyncRuntime : ILr2SongDbSyncWorkflowRuntime
    {

        internal bool IsLr2ModeEnabledValue { get; set; }

        internal bool IsLibraryAvailableValue { get; set; } = true;

        internal int QueueCount { get; private set; }

        internal Exception? QueueFailure { get; set; }

        internal Action? QueueObserved { get; set; }

        public bool IsLr2ModeEnabled => IsLr2ModeEnabledValue;

        public bool IsLibraryAvailable => IsLibraryAvailableValue;

        public async Task<bool> QueueAsync(
            string reason,
            bool force,
            bool prepareGeneratedData = false,
            bool allowIncompleteToQueue = true,
            LibraryFileInitializationResult? initializationResult = null, LibraryFileMutationCapability? capability = null, bool acceptedBackground = false, bool includeBuiltinGeneratedData = false, LibraryFileMutationCapability? playlistCapability = null, Lr2SongDbSyncPreparedDataSurface? preparedSurface = null, OperationProgressRequest? originatingRequest = null, BmsLibraryOptionsSnapshot? optionsSnapshot = null)
        {
            QueueCount++;
            QueueObserved?.Invoke();
            if (QueueFailure != null)
            {
                throw QueueFailure;
            }
            await Task.CompletedTask;
            return true;
        }



        public Task SyncExternalFolderRowsForCustomFolderOutputBaseChangeAsync(string reason, LibraryFileMutationCapability? capability = null, LibraryFileMutationCapability? playlistCapability = null)
        {
            return Task.CompletedTask;
        }

        public bool Cancel(string reason)
        {
            return false;
        }

    }
}
