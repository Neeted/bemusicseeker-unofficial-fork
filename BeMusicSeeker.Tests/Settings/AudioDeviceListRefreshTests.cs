using System;
using System.ComponentModel;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using BeMusicSeeker.Models;
using BeMusicSeeker.Models.Utils;
using BeMusicSeeker.Properties;
using BeMusicSeeker.ViewModels;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Ribbit.Media;
using Ribbit.Media.Audio;

namespace BeMusicSeeker.Tests;

/// <summary>実ownerの一覧取得とdraft・照会・保存の境界を、workerのgateで順序制御します。</summary>
[TestClass]
[DoNotParallelize] // 取消・保存の外観復元と共有音声runtimeの受付を準備から解放まで所有する。
public sealed class AudioDeviceListRefreshTests
{
    /// <summary>可視通知の到着順によらず初回だけ取得し、列挙中の編集とCloseを許可し、旧結果を閉画面へ通知しません。再表示中なら同じTaskから現在draftを表示します。</summary>
    [DataTestMethod]
    [DataRow(false, false)]
    [DataRow(true, false)]
    [DataRow(false, true)]
    [DataRow(true, true)]
    public void FirstAudioVisit_DeduplicatesPendingRefreshAndPreservesCurrentDraft(bool visibleBeforeActive, bool reopenBeforeCompletion)
    {
        TestUiDispatcherHost.Invoke(() =>
        {
            using var release = new ManualResetEventSlim();
            var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var catalog = new TestAudioDeviceCatalog
            {
                Devices = [new AudioDeviceInfo("Saved", "saved"), new AudioDeviceInfo("Current", "current")],
                RefreshAction = () => { started.TrySetResult(); release.Wait(); }
            };
            Settings settings = MainWindowViewModelTestFactory.CreateIsolatedSettings();
            var selection = new AudioOutputSelection(AudioDriver.WasapiExclusive, "saved", "Saved");
            using SettingsDialogViewModel dialog = CreateDialog(settings, catalog, selection);
            int listNotifications = 0;
            PropertyChangedEventHandler observer = (_, args) =>
            {
                if (args.PropertyName == nameof(SettingsDialogViewModel.PlayerDeviceNames)) { listNotifications++; }
            };
            dialog.PropertyChanged += observer;
            try
            {
                dialog.OpenCommand.Execute();
                Assert.AreEqual(0, catalog.RefreshCount);
                if (visibleBeforeActive) { dialog.SetAudioSettingsPageVisible(true); }
                dialog.SetPresentationActive(true);
                if (!visibleBeforeActive) { dialog.SetAudioSettingsPageVisible(true); }
                TestUiDispatcherHost.AwaitTaskOnDispatcher(started.Task, "audio-list-started");
                Assert.AreEqual(1, catalog.RefreshCount);
                Assert.AreEqual(Resources.AudioDeviceListLoading, dialog.AudioDeviceListStatusMessage);
                Assert.IsFalse(dialog.IsAudioDeviceTestAvailable);
                Assert.IsFalse(dialog.IsAudioDeviceTestInProgress);
                Assert.IsTrue(dialog.IsAudioOutputSelectionEnabled);
                Assert.IsTrue(dialog.IsEditCompletionEnabled);
                Assert.IsTrue(dialog.IsEditCancellationEnabled);
                TestUiDispatcherHost.AwaitTaskOnDispatcher(dialog.RunAudioDeviceTestAsync(), "test-rejected-during-list");

                dialog.PlayerDevice = "current";
                string resetReason = dialog.AudioOutputSelectionResetMessage;
                dialog.SelectedPlayerDevice = null;
                dialog.SetAudioSettingsPageVisible(false);
                dialog.SetAudioSettingsPageVisible(true);
                dialog.SetPresentationActive(false);
                dialog.SetAudioSettingsPageVisible(false);
                if (reopenBeforeCompletion)
                {
                    dialog.SetPresentationActive(true);
                    dialog.SetAudioSettingsPageVisible(true);
                }
                Assert.AreEqual(1, catalog.RefreshCount);
                int beforeCompletion = listNotifications;
                release.Set();
                TestUiDispatcherHost.AwaitTaskOnDispatcher(dialog.AudioSettingsCompletionTask, "audio-list-consumer");
                if (reopenBeforeCompletion)
                {
                    Assert.IsTrue(listNotifications > beforeCompletion);
                    Assert.AreEqual("current", dialog.SelectedPlayerDevice?.Driver);
                    Assert.AreEqual(resetReason, dialog.AudioOutputSelectionResetMessage);
                    // 列挙後に開始した現在選択の照会も、表示を止めてから実終端を回収する。
                    dialog.SetPresentationActive(false);
                    TestUiDispatcherHost.AwaitTaskOnDispatcher(dialog.AudioSettingsCompletionTask, "audio-list-current-query");
                }
                else
                {
                    Assert.AreEqual(beforeCompletion, listNotifications);
                }
                Assert.AreEqual("current", dialog.PlayerDevice);
                Assert.AreEqual(1, catalog.RefreshCount, "未完了取得の再利用後に追加取得を予約しません。");
                dialog.SetAudioSettingsPageVisible(false);
                dialog.SetPresentationActive(true);
                dialog.SetAudioSettingsPageVisible(true);
                dialog.SetPresentationActive(false);
                TestUiDispatcherHost.AwaitTaskOnDispatcher(dialog.AudioSettingsCompletionTask, "audio-list-next-window");
                Assert.AreEqual(2, catalog.RefreshCount, "新しい表示の初回訪問は新しく取得します。");
            }
            finally
            {
                release.Set();
                dialog.SetPresentationActive(false);
                TestUiDispatcherHost.AwaitTaskOnDispatcher(dialog.AudioSettingsCompletionTask, "audio-list-cleanup");
                dialog.PropertyChanged -= observer;
            }
        });
    }

    /// <summary>旧能力照会の非取消区間が終わるまで新一覧を始めず、Close後も旧照会と待機中の列挙を同じownerが実終端まで追跡します。</summary>
    [TestMethod]
    public void Reopen_WaitsForPriorCapabilityQueryBeforeStartingNewEnumeration()
    {
        TestUiDispatcherHost.Invoke(() =>
        {
            using var queryRelease = new ManualResetEventSlim();
            using var listRelease = new ManualResetEventSlim();
            var queryStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var secondListStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var catalog = new TestAudioDeviceCatalog();
            catalog.RefreshAction = () =>
            {
                if (catalog.RefreshCount == 2) { secondListStarted.TrySetResult(); listRelease.Wait(); }
            };
            var capability = new DelegateAudioDeviceCapabilityRuntime(request =>
            {
                queryStarted.TrySetResult();
                queryRelease.Wait();
                return new AudioDeviceCapabilityResult(request.Backend, request.DeviceIdentity, request.DeviceName,
                    AudioDeviceCapabilityStatus.Available, [SampleRate.SAMPLE_RATE_44100Hz]);
            });
            using SettingsDialogViewModel dialog = CreateDialog(
                MainWindowViewModelTestFactory.CreateIsolatedSettings(), catalog,
                new AudioOutputSelection(AudioDriver.WasapiExclusive, "saved", "Saved"), capability);
            try
            {
                dialog.SetPresentationActive(true);
                dialog.SetAudioSettingsPageVisible(true);
                TestUiDispatcherHost.AwaitTaskOnDispatcher(queryStarted.Task, "prior-query-started");
                dialog.SetPresentationActive(false);
                dialog.SetPresentationActive(true);
                Assert.AreEqual(1, catalog.RefreshCount);
                Assert.AreEqual(Resources.AudioDeviceListLoading, dialog.AudioDeviceListStatusMessage);
                dialog.SetPresentationActive(false);
                Task terminal = dialog.AudioSettingsCompletionTask;
                Assert.IsFalse(terminal.IsCompleted);
                queryRelease.Set();
                TestUiDispatcherHost.AwaitTaskOnDispatcher(secondListStarted.Task, "new-list-after-query");
                Assert.IsFalse(terminal.IsCompleted, "終了時に捕捉したTaskは照会後の列挙も含みます。");
                listRelease.Set();
                TestUiDispatcherHost.AwaitTaskOnDispatcher(terminal, "prior-query-and-list-terminal");
                Assert.AreEqual(2, catalog.RefreshCount);
                Assert.IsNull(dialog.AudioDeviceCapabilityStatusMessage);
            }
            finally
            {
                queryRelease.Set();
                listRelease.Set();
                dialog.SetPresentationActive(false);
                TestUiDispatcherHost.AwaitTaskOnDispatcher(dialog.AudioSettingsCompletionTask, "query-list-cleanup");
            }
        });
    }

    /// <summary>音声未訪問・取得中・取得失敗でも他設定を実保存し、新readerで未知方式/未接続機器と音声条件を保持します。失敗の再訪問は再取得せず、明示テストは再び受付可能になります。</summary>
    [DataTestMethod]
    [DataRow(0, false)]
    [DataRow(0, true)]
    [DataRow(1, false)]
    [DataRow(1, true)]
    [DataRow(2, false)]
    [DataRow(2, true)]
    public void SaveOtherSettings_PreservesUnavailableAudioSelectionBeforeDuringAndAfterFailedRefresh(int stage, bool unknownBackend)
    {
        TestUiDispatcherHost.Invoke(() =>
        {
            string root = Path.Combine(Path.GetTempPath(), "audio-list-save-" + Guid.NewGuid().ToString("N"));
            using var release = new ManualResetEventSlim();
            var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var catalog = new TestAudioDeviceCatalog
            {
                RefreshAction = () =>
                {
                    started.TrySetResult();
                    release.Wait();
                    if (stage == 2) { throw new IOException("list refresh failed"); }
                }
            };
            string settingsPath = Path.Combine(root, "user.config");
            Directory.CreateDirectory(root);
            Settings settings = PortableSettingsPersistenceTests.OpenSettings(settingsPath);
            settings.RightClickActionsJson = RightClickActionSettingsDefaults.SerializedJson;
            settings.PlayerSampleRate = SampleRate.SAMPLE_RATE_44100Hz;
            settings.PlayerFormat = SampleFormat.SAMPLE_INT_16BIT;
            var selection = new AudioOutputSelection(unknownBackend ? (AudioDriver)999 : AudioDriver.WasapiExclusive,
                "missing-device", "Missing device");
            settings.PlayerDriver = (BassAudioPlayer.DeviceDriver)(int)selection.Backend;
            settings.PlayerDevice = selection.DeviceIdentity;
            settings.PlayerDeviceName = selection.DeviceName;
            settings.Save();
            using SettingsDialogViewModel dialog = CreateDialog(settings, catalog, selection,
                session: new SettingsEditSession(settings));
            try
            {
                dialog.SetPresentationActive(true);
                if (stage > 0)
                {
                    dialog.SetAudioSettingsPageVisible(true);
                    TestUiDispatcherHost.AwaitTaskOnDispatcher(started.Task, "save-list-started");
                    if (stage == 2)
                    {
                        release.Set();
                        TestUiDispatcherHost.AwaitTaskOnDispatcher(dialog.AudioSettingsCompletionTask, "failed-list-consumer");
                        Assert.AreEqual(Resources.AudioDeviceListRefreshFailed, dialog.AudioDeviceListStatusMessage);
                        dialog.SetAudioSettingsPageVisible(false);
                        dialog.SetAudioSettingsPageVisible(true);
                        Assert.AreEqual(Resources.AudioDeviceListRefreshFailed, dialog.AudioDeviceListStatusMessage);
                        dialog.SetAudioSettingsPageVisible(false);
                        TestUiDispatcherHost.AwaitTaskOnDispatcher(dialog.AudioSettingsCompletionTask, "failed-list-query");
                        Assert.IsTrue(dialog.IsAudioDeviceTestAvailable);
                    }
                }
                dialog.ShowRecommUpdatedMsg = !dialog.ShowRecommUpdatedMsg;
                bool savedOtherValue = dialog.ShowRecommUpdatedMsg;
                TestUiDispatcherHost.AwaitTaskOnDispatcher(dialog.SaveSettings(), "save-during-audio-list");
                Settings reader = PortableSettingsPersistenceTests.OpenSettings(settingsPath);
                Assert.AreEqual(savedOtherValue, reader.ShowRecommUpdatedMsg);
                Assert.AreEqual((int)selection.Backend, (int)reader.PlayerDriver);
                Assert.AreEqual("missing-device", reader.PlayerDevice);
                Assert.AreEqual("Missing device", reader.PlayerDeviceName);
                Assert.AreEqual(SampleRate.SAMPLE_RATE_44100Hz, reader.PlayerSampleRate);
                Assert.AreEqual(SampleFormat.SAMPLE_INT_16BIT, reader.PlayerFormat);
                Assert.AreEqual(stage == 0 ? 0 : 1, catalog.RefreshCount);
            }
            finally
            {
                release.Set();
                dialog.SetPresentationActive(false);
                TestUiDispatcherHost.AwaitTaskOnDispatcher(dialog.AudioSettingsCompletionTask, "save-list-cleanup");
                Directory.Delete(root, recursive: true);
            }
        });
    }

    /// <summary>テスト専用の設定providerと実音声gatewayを、gateで制御する実ownerへ接続します。</summary>
    internal static SettingsDialogViewModel CreateDialog(Settings settings, IAudioDeviceCatalog catalog,
        AudioOutputSelection selection, IAudioDeviceCapabilityRuntime? capability = null, ISettingsEditSession? session = null)
    {
        settings.PlayerDriver = (BassAudioPlayer.DeviceDriver)(int)selection.Backend;
        settings.PlayerDevice = selection.DeviceIdentity;
        settings.PlayerDeviceName = selection.DeviceName;
        MainWindowViewModel owner = MainWindowViewModelTestFactory.Create(settings);
        owner.SettingDialog.Dispose();
        var workflow = new AudioDeviceTestWorkflowOwner(new NoOpAudioDeviceTestPlaybackPort(),
            new SuccessfulAudioDeviceTestRuntime(), capability ?? new DelegateAudioDeviceCapabilityRuntime(request =>
                new AudioDeviceCapabilityResult(request.Backend, request.DeviceIdentity, request.DeviceName,
                    AudioDeviceCapabilityStatus.Unsupported, [])));
        return new SettingsDialogViewModel(owner, owner.PlaylistWorkspace, owner.PlaylistWorkspace,
            owner.PlayHistory, owner.LibraryFolderTree, new TestSettingsDialogPlayerFactoryPort(),
            new TestSettingsDialogPlaybackRuntimePort(), owner.Lr2SongDbSyncWorkflow,
            session ?? new NoOpSettingsEditSession(settings),
            applicationLifetime: TestApplicationContext.CreateLifetime(),
            cultureCatalog: TestApplicationContext.CreateCultureCatalog(), audioDeviceTestWorkflow: workflow,
            externalShellGateway: ExternalShellGatewayPolicy.Current,
            applicationPathSnapshot: ApplicationPathPolicy.Current, audioDeviceCatalog: catalog,
            audioSettingsGateway: new SettingsAudioGateway(() => settings));
    }
}
