using System;
using System.ComponentModel;
using System.Globalization;
using System.IO;
using System.Threading.Tasks;
using System.Windows.Threading;
using BeMusicSeeker.Models;
using BeMusicSeeker.Properties;
using BeMusicSeeker.ViewModels;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Ribbit.Media;

namespace BeMusicSeeker.Tests;

[TestClass]
public sealed class SettingDialogOpenCommandTests
{
    /// <summary>保存済み音声値を変えずに Open を実行し、一回の表示要求と機器更新だけを行い勝手に保存する誤りを検出します。</summary>
    [TestMethod]
    public void OpenCommand_PublishesExactlyOneOwnerRequest()
    {
        var catalog = new TestAudioDeviceCatalog();
        var settingsSession = new TestSettingsEditSession(MainWindowViewModelTestFactory.CreateIsolatedSettings(values =>
            {
                values.RightClickActionsJson = RightClickActionSettingsDefaults.SerializedJson;
                values.PlayerResamplingQuality = 2;
                values.PlayerMixerThreadCount = 3;
            }));
        using SettingsDialogViewModel dialog = CreateViewModel(catalog, settingsSession).SettingDialog;
        var presentation = new RecordingSettingsDialogPresentationPort();
        dialog.AttachPresentationPort(presentation);

        Assert.AreEqual(2, dialog.PlayerResamplingQuality);
        Assert.AreEqual(3, dialog.PlayerMixerThreadCount);
        Assert.IsFalse(dialog.HasPendingSettingChanges());
        Assert.IsTrue(dialog.OpenCommand.CanExecute);
        dialog.OpenCommand.Execute();

        CollectionAssert.AreEqual(new[] { "open" }, presentation.Requests);
        Assert.AreEqual(1, catalog.RefreshCount);
        Assert.AreEqual(0, settingsSession.SaveCount);
    }

    /// <summary>共有 WASAPI・排他 WASAPI・ASIO を切り替え、形式・周波数・buffer の編集可否と読み取り専用表示が backend 能力に従うことを確認します。</summary>
    [TestMethod]
    public void PlayerDriverSelection_UsesTypedBackendCapabilities()
    {
        Settings settings = MainWindowViewModelTestFactory.CreateIsolatedSettings(values =>
            {
                values.RightClickActionsJson = RightClickActionSettingsDefaults.SerializedJson;
                values.PlayerDriver = BassAudioPlayer.DeviceDriver.WASAPI_SHARED;
                values.PlayerWASAPIParam = false;
            });
        using SettingsDialogViewModel dialog = CreateViewModel(
            settingsEditSession: new TestSettingsEditSession(settings)).SettingDialog;

        Assert.IsFalse(dialog.IsPlayerFormatSelectionEnabled);
        Assert.IsFalse(dialog.IsPlayerSampleRateSelectionEnabled);
        Assert.IsTrue(dialog.IsPlayerFormatReadOnly);
        Assert.IsTrue(dialog.IsPlayerSampleRateReadOnly);
        Assert.IsTrue(dialog.IsPlayerWasapiDriver);
        Assert.IsFalse(dialog.IsPlayerBufferControlEnabled);

        dialog.PlayerDriverIndex = AudioDriverPolicy.IndexOf(AudioDriver.WasapiExclusive);
        Assert.IsTrue(dialog.IsPlayerFormatSelectionEnabled);
        Assert.IsTrue(dialog.IsPlayerSampleRateSelectionEnabled);
        Assert.IsFalse(dialog.IsPlayerFormatReadOnly);
        Assert.IsFalse(dialog.IsPlayerSampleRateReadOnly);
        Assert.IsTrue(dialog.IsPlayerWasapiDriver);

        dialog.PlayerWASAPIParam = true;
        Assert.IsTrue(dialog.IsPlayerBufferControlEnabled);

        dialog.PlayerDriverIndex = AudioDriverPolicy.IndexOf(AudioDriver.WasapiShared);
        Assert.IsFalse(dialog.IsPlayerBufferControlEnabled);

        dialog.PlayerDriverIndex = AudioDriverPolicy.IndexOf(AudioDriver.Asio);
        Assert.IsFalse(dialog.IsPlayerFormatSelectionEnabled);
        Assert.IsTrue(dialog.IsPlayerSampleRateSelectionEnabled);
        Assert.IsTrue(dialog.IsPlayerFormatReadOnly);
        Assert.IsFalse(dialog.IsPlayerSampleRateReadOnly);
        Assert.IsFalse(dialog.IsPlayerWasapiDriver);
        Assert.IsTrue(dialog.IsPlayerBufferControlEnabled);
    }

    /// <summary>利用不能 backend と保存済み欠損機器の双方で編集後に取消し、閉鎖前の識別情報・警告通知復元と保存しないことを確認します。</summary>
    [DataTestMethod]
    [DataRow(true)]
    [DataRow(false)]
    [DoNotParallelize] // 取消が共有テーマと文化を復元するため非並列にします。
    public void Cancel_RestoresAudioDraftAndPresentationState(bool unavailableBackend)
    {
        TestUiDispatcherHost.Invoke(() =>
        {
            string previousTheme = BeMusicSeeker.Properties.Settings.Default.AppearanceTheme;
            CultureInfo? previousCulture = BeMusicSeeker.Properties.Resources.Culture;
            try
            {
                Settings settings = MainWindowViewModelTestFactory.CreateIsolatedSettings(values =>
            {
                values.RightClickActionsJson = RightClickActionSettingsDefaults.SerializedJson;
                values.PlayerDriver = unavailableBackend
                        ? BassAudioPlayer.DeviceDriver.NULL_DEVICE
                        : BassAudioPlayer.DeviceDriver.WASAPI_SHARED;
                values.PlayerDevice = unavailableBackend ? null : "saved-missing-device";
                values.PlayerDeviceName = unavailableBackend ? null : "Saved missing device";
            });
                var catalog = new TestAudioDeviceCatalog
                {
                    Devices =
                    [
                        new AudioDeviceInfo("Default", string.Empty),
                        new AudioDeviceInfo("Current device", "current-device")
                    ]
                };
                var settingsSession = new TestSettingsEditSession(settings);
                using SettingsDialogViewModel dialog = CreateViewModel(catalog, settingsSession).SettingDialog;
                int closeCount = 0;
                bool warningWasCleared = false;
                bool restoredWarningNotificationObserved = false;
                PropertyChangedEventHandler? warningHandler = null;
                if (unavailableBackend)
                {
                    warningHandler = (_, args) =>
                    {
                        if (string.Equals(
                                args.PropertyName,
                                nameof(SettingsDialogViewModel.UnavailablePlayerDriverDescription),
                                StringComparison.Ordinal)
                            && warningWasCleared
                            && !string.IsNullOrWhiteSpace(dialog.UnavailablePlayerDriverDescription))
                        {
                            restoredWarningNotificationObserved = true;
                        }
                    };
                    dialog.PropertyChanged += warningHandler;
                }

                try
                {
                    var presentation = new RecordingSettingsDialogPresentationPort(request =>
                    {
                        if (!string.Equals(request, "close", StringComparison.Ordinal))
                        {
                            return;
                        }

                        closeCount++;
                        Assert.IsFalse(dialog.HasPendingSettingChanges());
                        Assert.AreEqual(0, settingsSession.SaveCount);
                        if (unavailableBackend)
                        {
                            Assert.AreEqual(BassAudioPlayer.DeviceDriver.NULL_DEVICE, settings.PlayerDriver);
                            Assert.IsFalse(string.IsNullOrWhiteSpace(dialog.UnavailablePlayerDriverDescription));
                        }
                        else
                        {
                            Assert.AreEqual("saved-missing-device", dialog.PlayerDevice);
                            AudioDeviceInfo restored = dialog.PlayerDeviceNames.Find(
                                device => device.Driver == "saved-missing-device");
                            Assert.AreEqual("saved-missing-device", restored.Driver);
                            Assert.AreEqual("Saved missing device", restored.Name);
                            Assert.IsFalse(restored.IsAvailable);
                        }
                    });
                    dialog.AttachPresentationPort(presentation);
                    dialog.OpenCommand.Execute();

                    if (unavailableBackend)
                    {
                        Assert.IsFalse(string.IsNullOrWhiteSpace(dialog.UnavailablePlayerDriverDescription));
                        dialog.PlayerDriverIndex = AudioDriverPolicy.IndexOf(AudioDriver.WasapiShared);
                        Assert.IsTrue(string.IsNullOrWhiteSpace(dialog.UnavailablePlayerDriverDescription));
                        warningWasCleared = true;
                    }
                    else
                    {
                        dialog.PlayerDevice = "current-device";
                    }

                    Assert.IsTrue(dialog.HasPendingSettingChanges());
                    dialog.CancelCommand.Execute();

                    Assert.AreEqual(1, closeCount);
                    if (unavailableBackend)
                    {
                        Assert.IsTrue(restoredWarningNotificationObserved);
                    }
                }
                finally
                {
                    if (warningHandler != null)
                    {
                        dialog.PropertyChanged -= warningHandler;
                    }
                }
            }
            finally
            {
                BeMusicSeeker.Properties.Settings.Default.AppearanceTheme = previousTheme;
                AppThemeService.ApplyTheme(previousTheme);
                if (previousCulture is null)
                {
                    BeMusicSeeker.Properties.Resources.Culture = null;
                }
                else
                {
                    ResourceService.Current.ChangeCulture(previousCulture.Name);
                }
            }
        });
    }

    /// <summary>保存機器の表示を更新し未選択や未知機器が来ても保存済み識別を消さず、新 dialog でも同じ機器を選ぶことを確認します。</summary>
    [TestMethod]
    public void OpenCommand_SelectedDevicePreservesSavedIdentityAcrossRefreshAndNewViewModel()
    {
        Settings settings = MainWindowViewModelTestFactory.CreateIsolatedSettings(values =>
            {
                values.RightClickActionsJson = RightClickActionSettingsDefaults.SerializedJson;
                values.PlayerDriver = BassAudioPlayer.DeviceDriver.WASAPI_SHARED;
                values.PlayerDevice = "saved-device";
                values.PlayerDeviceName = "Saved device";
            });
        var catalog = new TestAudioDeviceCatalog
        {
            Devices =
            [
                new AudioDeviceInfo("Default", string.Empty),
                new AudioDeviceInfo("Saved device", "saved-device")
            ]
        };

        using SettingsDialogViewModel firstDialog = CreateViewModel(
            catalog,
            new TestSettingsEditSession(settings)).SettingDialog;
        firstDialog.OpenCommand.Execute();

        Assert.AreEqual("saved-device", firstDialog.SelectedPlayerDevice?.Driver);
        Assert.AreEqual("saved-device", settings.PlayerDevice);
        Assert.AreEqual("Saved device", settings.PlayerDeviceName);

        firstDialog.SelectedPlayerDevice = null;
        firstDialog.PlayerDevice = "device-that-is-not-in-the-catalog";

        Assert.AreEqual("saved-device", firstDialog.SelectedPlayerDevice?.Driver);
        Assert.AreEqual("saved-device", settings.PlayerDevice);
        Assert.AreEqual("Saved device", settings.PlayerDeviceName);

        using SettingsDialogViewModel secondDialog = CreateViewModel(
            catalog,
            new TestSettingsEditSession(settings)).SettingDialog;
        secondDialog.OpenCommand.Execute();

        Assert.AreEqual("saved-device", secondDialog.SelectedPlayerDevice?.Driver);
        Assert.AreEqual("saved-device", secondDialog.PlayerDevice);
    }

    /// <summary>空識別を既定機器として扱い、機器名との組を明示保存まで draft に保ち、既定へ戻す保存では両方を消すことを確認します。</summary>
    [TestMethod]
    public async Task SelectedPlayerDevice_TreatsEmptyIdentityAsDefaultAndPersistsPairOnlyOnSave()
    {
        Settings settings = MainWindowViewModelTestFactory.CreateIsolatedSettings(values =>
            {
                values.RightClickActionsJson = RightClickActionSettingsDefaults.SerializedJson;
                values.PlayerDriver = BassAudioPlayer.DeviceDriver.WASAPI_SHARED;
                values.PlayerDevice = string.Empty;
                values.PlayerDeviceName = "stale default name";
            });
        var catalog = new TestAudioDeviceCatalog
        {
            Devices =
            [
                new AudioDeviceInfo("Default", string.Empty),
                new AudioDeviceInfo("Current device", "current-device")
            ]
        };
        using SettingsDialogViewModel dialog = CreateViewModel(
            catalog,
            new TestSettingsEditSession(settings)).SettingDialog;
        dialog.OpenCommand.Execute();

        Assert.IsTrue(dialog.SelectedPlayerDevice?.IsDefaultPlaceholder);

        dialog.SelectedPlayerDevice = dialog.PlayerDeviceNames[1];
        Assert.AreEqual("current-device", dialog.SelectedPlayerDevice?.Driver);
        Assert.AreEqual(string.Empty, settings.PlayerDevice);
        Assert.AreEqual("stale default name", settings.PlayerDeviceName);

        await dialog.SaveSettings();
        Assert.AreEqual("current-device", settings.PlayerDevice);
        Assert.AreEqual("Current device", settings.PlayerDeviceName);

        dialog.SelectedPlayerDevice = dialog.PlayerDeviceNames[0];
        await dialog.SaveSettings();
        Assert.IsNull(settings.PlayerDevice);
        Assert.IsNull(settings.PlayerDeviceName);
        Assert.IsTrue(dialog.SelectedPlayerDevice?.IsDefaultPlaceholder);
    }

    /// <summary>backend と機器を変更して保存を失敗させ、保存済み三値を維持しながら選択 draft を失う誤りを検出します。</summary>
    [TestMethod]
    public async Task SaveSettings_DevicePersistenceFailureRestoresPersistedTripleAndKeepsDraft()
    {
        Settings settings = MainWindowViewModelTestFactory.CreateIsolatedSettings(values =>
            {
                values.RightClickActionsJson = RightClickActionSettingsDefaults.SerializedJson;
                values.PlayerDriver = BassAudioPlayer.DeviceDriver.WASAPI_SHARED;
                values.PlayerDevice = "saved-device";
                values.PlayerDeviceName = "Saved device";
            });
        var catalog = new TestAudioDeviceCatalog
        {
            Devices =
            [
                new AudioDeviceInfo("Default", string.Empty),
                new AudioDeviceInfo("Saved device", "saved-device"),
                new AudioDeviceInfo("Current device", "current-device")
            ]
        };
        var settingsSession = new TestSettingsEditSession(settings)
        {
            SaveFailure = new IOException("simulated save failure")
        };
        using SettingsDialogViewModel dialog = CreateViewModel(catalog, settingsSession).SettingDialog;
        dialog.OpenCommand.Execute();
        dialog.PlayerDriverIndex = AudioDriverPolicy.IndexOf(AudioDriver.Asio);
        dialog.PlayerDevice = "current-device";

        await Assert.ThrowsExceptionAsync<IOException>(() => dialog.SaveSettings());

        Assert.AreEqual(1, settingsSession.SaveCount);
        Assert.AreEqual(BassAudioPlayer.DeviceDriver.WASAPI_SHARED, settings.PlayerDriver);
        Assert.AreEqual("saved-device", settings.PlayerDevice);
        Assert.AreEqual("Saved device", settings.PlayerDeviceName);
        Assert.AreEqual(AudioDriverPolicy.IndexOf(AudioDriver.Asio), dialog.PlayerDriverIndex);
        Assert.AreEqual("current-device", dialog.SelectedPlayerDevice?.Driver);
        Assert.AreEqual("Current device", dialog.SelectedPlayerDevice?.Name);
    }

    private static MainWindowViewModel CreateViewModel(
        IAudioDeviceCatalog? audioDeviceCatalog = null,
        ISettingsEditSession? settingsEditSession = null)
    {
        return new ApplicationComposition(
            uiScheduler: new WpfUiScheduler(() => Dispatcher.CurrentDispatcher),
            applicationLifetime: TestApplicationContext.CreateLifetime(),
            cultureCatalog: TestApplicationContext.CreateCultureCatalog(),
            settingsEditSession: settingsEditSession
                ?? new TestSettingsEditSession(MainWindowViewModelTestFactory.CreateIsolatedSettings(values =>
            {
                values.RightClickActionsJson = RightClickActionSettingsDefaults.SerializedJson;
            })),
            audioDeviceCatalog: audioDeviceCatalog ?? new TestAudioDeviceCatalog())
            .CreateMainWindowViewModel();
    }

    private sealed class TestSettingsEditSession : ISettingsEditSession
    {
        public void SaveOperationModeForRestart(bool operationMode, string historyIdentity)
        {
            Values.OperationModeLR2DB = operationMode;
            Values.PlayHistorySelectedDisplayTargetIdentity = historyIdentity;
            Save();
            Reload();
        }

        internal TestSettingsEditSession(BeMusicSeeker.Properties.Settings values)
        {
            Values = values;
        }

        public BeMusicSeeker.Properties.Settings Values { get; }

        internal Exception? SaveFailure { get; set; }

        internal int SaveCount { get; private set; }

        public void Reload()
        {
        }

        public void Save()
        {
            SaveCount++;
            if (SaveFailure != null)
            {
                throw SaveFailure;
            }
        }
    }

}
