using BeMusicSeeker.Models;
using BeMusicSeeker.Properties;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Ribbit.Media.Audio;

namespace BeMusicSeeker.Tests;

[TestClass]
public sealed class PlayerSettingsGatewayTests
{
    private readonly BeMusicSeeker.Properties.Settings testSettings = new();
    [TestMethod]
    public void GatewayCapturesRequestedAudioSettingsWithoutNegotiatedWriteBack()
    {
        Settings settings = testSettings;
        var originalDriver = settings.PlayerDriver;
        string originalDevice = settings.PlayerDevice;
        string originalDeviceName = settings.PlayerDeviceName;
        SampleRate originalRate = settings.PlayerSampleRate;
        SampleFormat originalFormat = settings.PlayerFormat;
        int originalVolume = settings.uBMplayVolume;
        System.Windows.Point originalResolution = settings.LR2bodyResolution;
        try
        {
            settings.PlayerDriver = Ribbit.Media.BassAudioPlayer.DeviceDriver.DIRECT_SOUND;
            settings.PlayerDevice = "device-before";
            settings.PlayerDeviceName = "Device before";
            settings.PlayerSampleRate = SampleRate.SAMPLE_RATE_44100Hz;
            settings.PlayerFormat = SampleFormat.SAMPLE_INT_16BIT;
            settings.uBMplayVolume = 37;
            settings.LR2bodyResolution = new System.Windows.Point(1234.5, 678.25);

            var gateway = new SettingsPlayerSettingsGateway(() => settings);
            PlayerSettingsSnapshot snapshot = gateway.CaptureSnapshot();

            Assert.AreEqual(AudioDriver.WasapiShared, snapshot.PlayerDriver);
            Assert.IsNull(snapshot.PlayerDevice);
            Assert.IsNull(snapshot.PlayerDeviceName);
            Assert.AreEqual(SampleRate.SAMPLE_RATE_44100Hz, snapshot.PlayerSampleRate);
            Assert.AreEqual(SampleFormat.SAMPLE_INT_16BIT, snapshot.PlayerFormat);
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
