using System;
using System.ComponentModel;
using System.Globalization;
using System.IO;
using System.Threading.Tasks;
using System.Windows.Threading;
using BeMusicSeeker.Models;
using BeMusicSeeker.ViewModels;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Ribbit.Media;

namespace BeMusicSeeker.Tests;

[TestClass]
public sealed class SettingDialogOpenCommandTests
{
    [TestMethod]
    public void OpenCommand_PublishesExactlyOneOwnerRequest()
    {
        var catalog = new TestAudioDeviceCatalog();
        var settingsSession = new TestSettingsEditSession(new BeMusicSeeker.Properties.Settings
        {
            RightClickActionsJson = RightClickActionSettingsDefaults.SerializedJson
        });
        using SettingsDialogViewModel dialog = CreateViewModel(catalog, settingsSession).SettingDialog;
        var presentation = new RecordingSettingsDialogPresentationPort();
        dialog.AttachPresentationPort(presentation);

        Assert.IsTrue(dialog.OpenCommand.CanExecute);
        dialog.OpenCommand.Execute();

        CollectionAssert.AreEqual(new[] { "open" }, presentation.Requests);
        Assert.AreEqual(1, catalog.RefreshCount);
        Assert.AreEqual(0, settingsSession.SaveCount);
    }

    [TestMethod]
    public void PlayerDriverSelection_UsesTypedBackendCapabilities()
    {
        var settings = new BeMusicSeeker.Properties.Settings
        {
            RightClickActionsJson = RightClickActionSettingsDefaults.SerializedJson,
            PlayerDriver = BassAudioPlayer.DeviceDriver.WASAPI_SHARED,
            PlayerWASAPIParam = false
        };
        using SettingsDialogViewModel dialog = CreateViewModel(
            settingsEditSession: new TestSettingsEditSession(settings)).SettingDialog;

        Assert.AreEqual(3, dialog.PlayerDriverNames.Count);
        Assert.IsFalse(dialog.IsPlayerFormatSelectionEnabled);
        Assert.IsTrue(dialog.IsPlayerWasapiDriver);
        Assert.IsTrue(dialog.IsPlayerBufferControlEnabled);

        dialog.PlayerDriverIndex = AudioDriverPolicy.IndexOf(AudioDriver.WasapiExclusive);
        Assert.IsTrue(dialog.IsPlayerFormatSelectionEnabled);
        Assert.IsTrue(dialog.IsPlayerWasapiDriver);

        dialog.PlayerWASAPIParam = true;
        Assert.IsTrue(dialog.IsPlayerBufferControlEnabled);

        dialog.PlayerDriverIndex = AudioDriverPolicy.IndexOf(AudioDriver.WasapiShared);
        Assert.IsFalse(dialog.IsPlayerBufferControlEnabled);

        dialog.PlayerDriverIndex = AudioDriverPolicy.IndexOf(AudioDriver.Asio);
        Assert.IsTrue(dialog.IsPlayerFormatSelectionEnabled);
        Assert.IsFalse(dialog.IsPlayerWasapiDriver);
        Assert.IsTrue(dialog.IsPlayerBufferControlEnabled);
    }

    [DataTestMethod]
    [DataRow(true)]
    [DataRow(false)]
    [DoNotParallelize]
    public void Cancel_RestoresAudioDraftAndPresentationState(bool unavailableBackend)
    {
        TestUiDispatcherHost.Invoke(() =>
        {
            string previousTheme = BeMusicSeeker.Properties.Settings.Default.AppearanceTheme;
            CultureInfo? previousCulture = BeMusicSeeker.Properties.Resources.Culture;
            try
            {
                var settings = new BeMusicSeeker.Properties.Settings
                {
                    RightClickActionsJson = RightClickActionSettingsDefaults.SerializedJson,
                    PlayerDriver = unavailableBackend
                        ? BassAudioPlayer.DeviceDriver.NULL_DEVICE
                        : BassAudioPlayer.DeviceDriver.WASAPI_SHARED,
                    PlayerDevice = unavailableBackend ? null : "saved-missing-device",
                    PlayerDeviceName = unavailableBackend ? null : "Saved missing device"
                };
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

    [TestMethod]
    public void OpenCommand_SelectedDevicePreservesSavedIdentityAcrossRefreshAndNewViewModel()
    {
        var settings = new BeMusicSeeker.Properties.Settings
        {
            RightClickActionsJson = RightClickActionSettingsDefaults.SerializedJson,
            PlayerDriver = BassAudioPlayer.DeviceDriver.WASAPI_SHARED,
            PlayerDevice = "saved-device",
            PlayerDeviceName = "Saved device"
        };
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

    [TestMethod]
    public async Task SelectedPlayerDevice_TreatsEmptyIdentityAsDefaultAndPersistsPairOnlyOnSave()
    {
        var settings = new BeMusicSeeker.Properties.Settings
        {
            RightClickActionsJson = RightClickActionSettingsDefaults.SerializedJson,
            PlayerDriver = BassAudioPlayer.DeviceDriver.WASAPI_SHARED,
            PlayerDevice = string.Empty,
            PlayerDeviceName = "stale default name"
        };
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

    [TestMethod]
    public async Task SaveSettings_DevicePersistenceFailureRestoresPersistedTripleAndKeepsDraft()
    {
        var settings = new BeMusicSeeker.Properties.Settings
        {
            RightClickActionsJson = RightClickActionSettingsDefaults.SerializedJson,
            PlayerDriver = BassAudioPlayer.DeviceDriver.WASAPI_SHARED,
            PlayerDevice = "saved-device",
            PlayerDeviceName = "Saved device"
        };
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
                ?? new TestSettingsEditSession(new BeMusicSeeker.Properties.Settings
                {
                    RightClickActionsJson = RightClickActionSettingsDefaults.SerializedJson
                }),
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
