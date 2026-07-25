using BeMusicSeeker.Models;
using BeMusicSeeker.Properties;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Ribbit.Media.Audio;

namespace BeMusicSeeker.Tests;

[TestClass]
[DoNotParallelize]
public sealed class PlayerSettingsGatewayTests
{
    [TestMethod]
    public void GatewayCapturesAndAppliesNegotiatedAudioSettings()
    {
        Settings settings = Settings.Default;
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

            Assert.AreEqual(AudioDriver.DirectSound, snapshot.PlayerDriver);
            Assert.AreEqual("device-before", snapshot.PlayerDevice);
            Assert.AreEqual("Device before", snapshot.PlayerDeviceName);
            Assert.AreEqual(SampleRate.SAMPLE_RATE_44100Hz, snapshot.PlayerSampleRate);
            Assert.AreEqual(SampleFormat.SAMPLE_INT_16BIT, snapshot.PlayerFormat);
            Assert.AreEqual(37, snapshot.PlayerVolume);
            Assert.AreEqual(1234.5, snapshot.LR2bodyResolution.Width);
            Assert.AreEqual(678.25, snapshot.LR2bodyResolution.Height);

            gateway.ApplyNegotiatedAudioSettings(
                AudioDriver.Asio,
                "device-after",
                "Device after",
                SampleRate.SAMPLE_RATE_48000Hz,
                SampleFormat.SAMPLE_FLOAT_32BIT);

            Assert.AreEqual(Ribbit.Media.BassAudioPlayer.DeviceDriver.ASIO, settings.PlayerDriver);
            Assert.AreEqual("device-after", settings.PlayerDevice);
            Assert.AreEqual("Device after", settings.PlayerDeviceName);
            Assert.AreEqual(SampleRate.SAMPLE_RATE_48000Hz, settings.PlayerSampleRate);
            Assert.AreEqual(SampleFormat.SAMPLE_FLOAT_32BIT, settings.PlayerFormat);
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
    public void PlayerSettingsGatewayUsesTechnologyNeutralContracts()
    {
        string source = SourceTextTestHelper.ReadProductionSourceText(
            "BeMusicSeeker",
            "Models",
            "PlayerSettingsGateway.cs");

        Assert.IsFalse(source.Contains("using System.Windows;"));
        Assert.IsFalse(source.Contains("Win32API."));
        StringAssert.Contains(source, "PlayerResolution LR2bodyResolution");
        StringAssert.Contains(source, "WindowPlacement LR2bodyWindowPlacement");
    }

    [TestMethod]
    public void PlayerResolutionSettingsAdapterPreservesPersistedDimensions()
    {
        Settings settings = Settings.Default;
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
