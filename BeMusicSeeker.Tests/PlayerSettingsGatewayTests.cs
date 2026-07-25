using BeMusicSeeker.Models;
using BeMusicSeeker.Properties;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Ribbit.Media;
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
        BassAudioPlayer.DeviceDriver originalDriver = settings.PlayerDriver;
        string originalDevice = settings.PlayerDevice;
        string originalDeviceName = settings.PlayerDeviceName;
        SampleRate originalRate = settings.PlayerSampleRate;
        SampleFormat originalFormat = settings.PlayerFormat;
        int originalVolume = settings.uBMplayVolume;
        try
        {
            settings.PlayerDriver = BassAudioPlayer.DeviceDriver.DIRECT_SOUND;
            settings.PlayerDevice = "device-before";
            settings.PlayerDeviceName = "Device before";
            settings.PlayerSampleRate = SampleRate.SAMPLE_RATE_44100Hz;
            settings.PlayerFormat = SampleFormat.SAMPLE_INT_16BIT;
            settings.uBMplayVolume = 37;

            var gateway = new SettingsPlayerSettingsGateway(() => settings);
            PlayerSettingsSnapshot snapshot = gateway.CaptureSnapshot();

            Assert.AreEqual(BassAudioPlayer.DeviceDriver.DIRECT_SOUND, snapshot.PlayerDriver);
            Assert.AreEqual("device-before", snapshot.PlayerDevice);
            Assert.AreEqual("Device before", snapshot.PlayerDeviceName);
            Assert.AreEqual(SampleRate.SAMPLE_RATE_44100Hz, snapshot.PlayerSampleRate);
            Assert.AreEqual(SampleFormat.SAMPLE_INT_16BIT, snapshot.PlayerFormat);
            Assert.AreEqual(37, snapshot.PlayerVolume);

            gateway.ApplyNegotiatedAudioSettings(
                BassAudioPlayer.DeviceDriver.ASIO,
                "device-after",
                "Device after",
                SampleRate.SAMPLE_RATE_48000Hz,
                SampleFormat.SAMPLE_FLOAT_32BIT);

            Assert.AreEqual(BassAudioPlayer.DeviceDriver.ASIO, settings.PlayerDriver);
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
        }
    }
}
