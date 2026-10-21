using System;
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

    /// <summary>保存済み音声値を変えずに Open を実行し、機器列挙なしで一回の表示要求だけを行い勝手に保存する誤りを検出します。</summary>
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
        Assert.IsTrue(dialog.OpenCommand.CanExecute);
        dialog.OpenCommand.Execute();

        CollectionAssert.AreEqual(new[] { "open" }, presentation.Requests);
        Assert.AreEqual(0, catalog.RefreshCount);
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
        public void SaveEnvironmentForRestart(EnvironmentSettingsChange environment, string historyIdentity)
        {
            environment.Commit(Values);
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
