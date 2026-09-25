using BeMusicSeeker.Models;
using BeMusicSeeker.Models.Utils;
using BeMusicSeeker.Properties;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Ribbit.Media;
using Ribbit.Media.Audio;

namespace BeMusicSeeker.Tests;

[TestClass]
public sealed class PlayerSettingsGatewayTests
{
    // PORTABLE-SETTINGS-FAILURE-20260905 P08d: stop capture is memory-only, including no Save request.
    [TestMethod]
    public void CapturedPlacementUpdatesMemoryWithoutRequestingOrWritingSettingsSave()
    {
        string directory = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "BmsPlacement-" + System.Guid.NewGuid().ToString("N"));
        System.IO.Directory.CreateDirectory(directory);
        string path = System.IO.Path.Combine(directory, "user.config");
        try
        {
            Settings settings = PortableSettingsPersistenceTests.OpenSettings(path);
            var first = new WindowPlacement(0, 1, 0, 0, 0, 0, 10, 20, 810, 620);
            var captured = new WindowPlacement(0, 1, 0, 0, 0, 0, 30, 40, 830, 640);
            settings.LR2bodyWindowPlacement = BeMusicSeeker.Models.Utils.Win32WindowPlacementAdapter.ToNative(first);
            settings.Save();
            byte[] before = System.IO.File.ReadAllBytes(path);
            int saves = 0;
            settings.SettingsSaving += (_, _) => saves++;

            new SettingsPlayerSettingsGateway(() => settings).UpdateWindowPlacement(captured);

            Assert.AreEqual(0, saves);
            Assert.AreEqual(Win32WindowPlacementAdapter.ToNative(captured), settings.LR2bodyWindowPlacement);
            CollectionAssert.AreEqual(before, System.IO.File.ReadAllBytes(path));
            Assert.AreEqual(Win32WindowPlacementAdapter.ToNative(first), PortableSettingsPersistenceTests.OpenSettings(path).LR2bodyWindowPlacement);
        }
        finally { System.IO.Directory.Delete(directory, true); }
    }

    private readonly BeMusicSeeker.Properties.Settings testSettings = new();
    [TestMethod]
    public void GatewayCapturesRequestedAudioSettingsWithoutNegotiatedWriteBack()
    {
        Settings settings = testSettings;
        BassAudioPlayer.DeviceDriver originalDriver = settings.PlayerDriver;
        string originalDevice = settings.PlayerDevice;
        string originalDeviceName = settings.PlayerDeviceName;
        SampleRate originalRate = settings.PlayerSampleRate;
        SampleFormat originalFormat = settings.PlayerFormat;
        int originalResamplingQuality = settings.PlayerResamplingQuality;
        int originalVolume = settings.uBMplayVolume;
        System.Windows.Point originalResolution = settings.LR2bodyResolution;
        try
        {
            settings.PlayerDriver = Ribbit.Media.BassAudioPlayer.DeviceDriver.DIRECT_SOUND;
            settings.PlayerDevice = "device-before";
            settings.PlayerDeviceName = "Device before";
            settings.PlayerSampleRate = SampleRate.SAMPLE_RATE_44100Hz;
            settings.PlayerFormat = SampleFormat.SAMPLE_INT_16BIT;
            settings.PlayerResamplingQuality = 2;
            settings.uBMplayVolume = 37;
            settings.LR2bodyResolution = new System.Windows.Point(1234.5, 678.25);

            var gateway = new SettingsPlayerSettingsGateway(() => settings);
            PlayerSettingsSnapshot snapshot = gateway.CaptureSnapshot();

            Assert.AreEqual(AudioDriver.WasapiShared, snapshot.PlayerDriver);
            Assert.IsNull(snapshot.PlayerDevice);
            Assert.IsNull(snapshot.PlayerDeviceName);
            Assert.AreEqual(SampleRate.SAMPLE_RATE_44100Hz, snapshot.PlayerSampleRate);
            Assert.AreEqual(SampleFormat.SAMPLE_INT_16BIT, snapshot.PlayerFormat);
            Assert.AreEqual(2, snapshot.SampleRateConversionQuality);
            Assert.AreEqual(37, snapshot.PlayerVolume);
            Assert.AreEqual(1234.5, snapshot.LR2bodyResolution.Width);
            Assert.AreEqual(678.25, snapshot.LR2bodyResolution.Height);

            Assert.AreEqual(Ribbit.Media.BassAudioPlayer.DeviceDriver.DIRECT_SOUND, settings.PlayerDriver);
            Assert.AreEqual("device-before", settings.PlayerDevice);
            Assert.AreEqual("Device before", settings.PlayerDeviceName);
            Assert.AreEqual(SampleRate.SAMPLE_RATE_44100Hz, settings.PlayerSampleRate);
            Assert.AreEqual(SampleFormat.SAMPLE_INT_16BIT, settings.PlayerFormat);
        }
        finally
        {
            settings.PlayerDriver = originalDriver;
            settings.PlayerDevice = originalDevice;
            settings.PlayerDeviceName = originalDeviceName;
            settings.PlayerSampleRate = originalRate;
            settings.PlayerFormat = originalFormat;
            settings.PlayerResamplingQuality = originalResamplingQuality;
            settings.uBMplayVolume = originalVolume;
            settings.LR2bodyResolution = originalResolution;
        }
    }

    [TestMethod]
    public void PlayerResolutionSettingsAdapterPreservesPersistedDimensions()
    {
        Settings settings = testSettings;
        System.Windows.Point originalResolution = settings.LR2bodyResolution;
        try
        {
            PlayerResolutionSettingsAdapter.SaveToSettings(settings, new PlayerResolution(1111.5, 777.25));

            Assert.AreEqual(1111.5, settings.LR2bodyResolution.X);
            Assert.AreEqual(777.25, settings.LR2bodyResolution.Y);

            PlayerResolution captured = PlayerResolutionSettingsAdapter.FromSettings(settings);
            Assert.AreEqual(1111.5, captured.Width);
            Assert.AreEqual(777.25, captured.Height);
        }
        finally
        {
            settings.LR2bodyResolution = originalResolution;
        }
    }
}
