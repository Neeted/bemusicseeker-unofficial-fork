using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Reflection;
using System.Windows.Threading;
using BeMusicSeeker;
using BeMusicSeeker.Models;
using BeMusicSeeker.ViewModels;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Ribbit.Media;

namespace BeMusicSeeker.Tests;

[TestClass]
[DoNotParallelize]
public sealed class SettingDialogOpenCommandTests
{
    [TestMethod]
    public void OpenCommand_PublishesExactlyOneOwnerRequest()
    {
        var catalog = new TestAudioDeviceCatalog();
        MainWindowViewModel viewModel = CreateViewModel(catalog);
        var presentation = new RecordingSettingsDialogPresentationPort();
        viewModel.SettingDialog.AttachPresentationPort(presentation);

        Assert.IsTrue(viewModel.SettingDialog.OpenCommand.CanExecute);
        viewModel.SettingDialog.OpenCommand.Execute();

        CollectionAssert.AreEqual(new[] { "open" }, presentation.Requests);
        Assert.AreEqual(1, catalog.RefreshCount);
    }

    [TestMethod]
    public void OpenCommand_WithoutShellSubscriber_IsNoOp()
    {
        MainWindowViewModel viewModel = CreateViewModel();

        viewModel.SettingDialog.OpenCommand.Execute();
    }

    [TestMethod]
    public void OpenAndCancel_UnavailableBackendReissuesWarningNotification()
    {
        var settings = new BeMusicSeeker.Properties.Settings
        {
            PlayerDriver = BassAudioPlayer.DeviceDriver.NULL_DEVICE
        };
        PropertyInfo availableCulturesProperty = GetAvailableCulturesProperty();
        object? previousAvailableCultures = EnsureAvailableCultures(availableCulturesProperty);
        try
        {
            MainWindowViewModel viewModel = CreateViewModel(
                settingsEditSession: new TestSettingsEditSession(settings));
            SettingsDialogViewModel dialog = viewModel.SettingDialog;
            var changedProperties = new List<string>();
            dialog.PropertyChanged += (_, args) => changedProperties.Add(args.PropertyName!);

            dialog.OpenCommand.Execute();

            CollectionAssert.Contains(changedProperties, nameof(SettingsDialogViewModel.UnavailablePlayerDriverDescription));
            Assert.IsFalse(string.IsNullOrWhiteSpace(dialog.UnavailablePlayerDriverDescription));

            changedProperties.Clear();
            dialog.ShowRecommUpdatedMsg = !settings.ShowRecommUpdatedMsg;
            dialog.CancelCommand.Execute();

            CollectionAssert.Contains(changedProperties, nameof(SettingsDialogViewModel.UnavailablePlayerDriverDescription));
            Assert.IsFalse(string.IsNullOrWhiteSpace(dialog.UnavailablePlayerDriverDescription));
        }
        finally
        {
            RestoreAvailableCultures(availableCulturesProperty, previousAvailableCultures);
        }
    }

    [TestMethod]
    public void Cancel_ChangedDeviceRestoresSavedUnavailableDeviceInRebuiltList()
    {
        var settings = new BeMusicSeeker.Properties.Settings
        {
            PlayerDriver = BassAudioPlayer.DeviceDriver.DIRECT_SOUND,
            PlayerDevice = "saved-missing-device",
            PlayerDeviceName = "Saved missing device"
        };
        PropertyInfo availableCulturesProperty = GetAvailableCulturesProperty();
        object? previousAvailableCultures = EnsureAvailableCultures(availableCulturesProperty);
        try
        {
            var catalog = new TestAudioDeviceCatalog
            {
                Devices =
                [
                    new AudioDeviceInfo("Default", string.Empty),
                    new AudioDeviceInfo("Current device", "current-device")
                ]
            };
            MainWindowViewModel viewModel = CreateViewModel(
                catalog,
                new TestSettingsEditSession(settings));
            SettingsDialogViewModel dialog = viewModel.SettingDialog;

            dialog.OpenCommand.Execute();
            dialog.PlayerDevice = "current-device";
            dialog.CancelCommand.Execute();

            Assert.AreEqual("saved-missing-device", dialog.PlayerDevice);
            AudioDeviceInfo restored = dialog.PlayerDeviceNames.Find(
                device => device.Driver == "saved-missing-device");
            Assert.AreEqual("saved-missing-device", restored.Driver);
            Assert.AreEqual("Saved missing device", restored.Name);
            Assert.IsFalse(restored.IsAvailable);
        }
        finally
        {
            RestoreAvailableCultures(availableCulturesProperty, previousAvailableCultures);
        }
    }

    [TestMethod]
    public void CancelCommand_ChangedDraft_ResetsDraftBeforeClosing()
    {
        bool previousShowRecommUpdatedMsg = BeMusicSeeker.Properties.Settings.Default.ShowRecommUpdatedMsg;
        PropertyInfo availableCulturesProperty = typeof(App).GetProperty("AvailableCultures", BindingFlags.Static | BindingFlags.Public)!;
        object previousAvailableCultures = availableCulturesProperty.GetValue(null);
        try
        {
            if (previousAvailableCultures == null)
            {
                availableCulturesProperty.SetValue(
                    null,
                    new ReadOnlyDictionary<string, string>(
                        new Dictionary<string, string> { ["ja-JP"] = "ja-JP" }));
            }

            MainWindowViewModel viewModel = CreateViewModel();
            SettingsDialogViewModel dialog = viewModel.SettingDialog;
            var presentation = new RecordingSettingsDialogPresentationPort();
            dialog.AttachPresentationPort(presentation);

            dialog.ShowRecommUpdatedMsg = !previousShowRecommUpdatedMsg;
            Assert.IsTrue(dialog.HasPendingSettingChanges());

            dialog.CancelCommand.Execute();

            CollectionAssert.AreEqual(
                new[]
                {
                    "refresh",
                    "close"
                },
                presentation.Requests);
            Assert.IsFalse(dialog.HasPendingSettingChanges());
            Assert.AreEqual(previousShowRecommUpdatedMsg, dialog.ShowRecommUpdatedMsg);
            Assert.IsFalse(dialog.IsEditCompletionInProgress);
        }
        finally
        {
            BeMusicSeeker.Properties.Settings.Default.ShowRecommUpdatedMsg = previousShowRecommUpdatedMsg;
            if (previousAvailableCultures == null)
            {
                availableCulturesProperty.SetValue(null, null);
            }
        }
    }

    private static MainWindowViewModel CreateViewModel(
        IAudioDeviceCatalog? audioDeviceCatalog = null,
        ISettingsEditSession? settingsEditSession = null)
    {
        return new ApplicationComposition(
            uiScheduler: new WpfUiScheduler(() => Dispatcher.CurrentDispatcher),
            applicationLifetime: TestApplicationContext.CreateLifetime(),
            cultureCatalog: TestApplicationContext.CreateCultureCatalog(),
            settingsEditSession: settingsEditSession,
            audioDeviceCatalog: audioDeviceCatalog ?? new TestAudioDeviceCatalog())
            .CreateMainWindowViewModel();
    }

    private static PropertyInfo GetAvailableCulturesProperty()
    {
        return typeof(App).GetProperty("AvailableCultures", BindingFlags.Static | BindingFlags.Public)!;
    }

    private static object? EnsureAvailableCultures(PropertyInfo property)
    {
        object? previous = property.GetValue(null);
        if (previous == null)
        {
            property.SetValue(
                null,
                new ReadOnlyDictionary<string, string>(
                    new Dictionary<string, string> { ["ja-JP"] = "ja-JP" }));
        }
        return previous;
    }

    private static void RestoreAvailableCultures(PropertyInfo property, object? previous)
    {
        if (previous == null)
        {
            property.SetValue(null, null);
        }
    }

    private sealed class TestSettingsEditSession : ISettingsEditSession
    {
        internal TestSettingsEditSession(BeMusicSeeker.Properties.Settings values)
        {
            Values = values;
        }

        public BeMusicSeeker.Properties.Settings Values { get; }

        public void Reload()
        {
        }

        public void Save()
        {
        }
    }

}
