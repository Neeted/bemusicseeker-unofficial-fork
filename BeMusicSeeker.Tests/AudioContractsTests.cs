using System.Collections.Generic;
using BeMusicSeeker.Models;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace BeMusicSeeker.Tests;

[TestClass]
[DoNotParallelize]
public sealed class AudioContractsTests
{
    [TestMethod]
    public void SettingsAudioGateway_PreservesKnownAndUnknownPersistedValues()
    {
        BeMusicSeeker.Properties.Settings settings = BeMusicSeeker.Properties.Settings.Default;
        var originalDriver = settings.PlayerDriver;
        var originalNormalization = settings.EncoderNormalization;
        var originalEncoder = settings.Encoder;
        try
        {
            settings.PlayerDriver = (Ribbit.Media.BassAudioPlayer.DeviceDriver)47;
            settings.EncoderNormalization = (Ribbit.BMS.BMSAutoPlayWriter.Normalization)53;
            SettingsAudioGateway gateway = new(() => settings);

            Assert.AreEqual((AudioDriver)47, gateway.PlayerDriver);
            Assert.AreEqual((AudioNormalization)53, gateway.EncoderNormalization);

            gateway.PlayerDriver = (AudioDriver)61;
            gateway.EncoderNormalization = (AudioNormalization)67;
            gateway.ApplyEncoderFallback(Ribbit.Media.Audio.EncoderType.FLAC);

            Assert.AreEqual((Ribbit.Media.BassAudioPlayer.DeviceDriver)61, settings.PlayerDriver);
            Assert.AreEqual((Ribbit.BMS.BMSAutoPlayWriter.Normalization)67, settings.EncoderNormalization);
            Assert.AreEqual(Ribbit.Media.Audio.EncoderType.FLAC, settings.Encoder);
        }
        finally
        {
            settings.PlayerDriver = originalDriver;
            settings.EncoderNormalization = originalNormalization;
            settings.Encoder = originalEncoder;
        }
    }

    [TestMethod]
    public void SettingsAudioGateway_CapturesEncodingSettingsWithoutBmsTypes()
    {
        BeMusicSeeker.Properties.Settings settings = BeMusicSeeker.Properties.Settings.Default;
        var originalNormalization = settings.EncoderNormalization;
        try
        {
            settings.EncoderNormalization = Ribbit.BMS.BMSAutoPlayWriter.Normalization.RMS_VALUE;
            SettingsAudioGateway gateway = new(() => settings);

            AudioEncodingSettingsSnapshot snapshot = gateway.CaptureEncodingSettings();

            Assert.AreEqual(AudioNormalization.RmsValue, snapshot.EncoderNormalization);
            Assert.AreEqual(settings.Encoder, snapshot.Encoder);
            Assert.AreEqual(settings.EncoderSampleRate, snapshot.EncoderSampleRate);
            Assert.AreEqual(settings.EncoderFormat, snapshot.EncoderFormat);
            Assert.AreEqual(settings.EncoderExeDir, snapshot.EncoderExeDirectory);
            Assert.AreEqual(settings.EncodeFileNameFormat, snapshot.EncodeFileNameFormat);
        }
        finally
        {
            settings.EncoderNormalization = originalNormalization;
        }
    }

    [TestMethod]
    public void AudioDeviceInfo_PreservesDeviceIdentityAndDefaultDisplayName()
    {
        AudioDeviceInfo defaultDevice = new(null, null);
        AudioDeviceInfo namedDevice = new("device-name", "driver-id");

        Assert.AreEqual("(Default device)", defaultDevice.FriendlyName);
        Assert.IsNull(defaultDevice.Driver);
        Assert.AreEqual("device-name", namedDevice.FriendlyName);
        Assert.AreEqual("driver-id", namedDevice.Driver);
        Assert.AreEqual("device-name", namedDevice.Name);
    }

    [TestMethod]
    public void InternalPlayer_DelegatesVolumeAndCloseLifecycleToPlaybackRuntime()
    {
        BeMusicSeeker.Properties.Settings settings = BeMusicSeeker.Properties.Settings.Default;
        int originalVolume = settings.uBMplayVolume;
        try
        {
            settings.uBMplayVolume = 123;
            var events = new List<string>();
            var runtime = new RecordingAudioPlaybackRuntime(events);
            var player = new InternalBMSAutoPlayerSoundOnly(
                new SettingsPlayerSettingsGateway(() => settings),
                runtime);

            player.VolumeChanged();
            player.CloseProcess();

            CollectionAssert.AreEqual(
                new[] { "volume:123", "clear-max-voices", "free" },
                events);
        }
        finally
        {
            settings.uBMplayVolume = originalVolume;
        }
    }

    private sealed class RecordingAudioPlaybackRuntime : IAudioPlaybackRuntime
    {
        private readonly List<string> events;

        internal RecordingAudioPlaybackRuntime(List<string> events)
        {
            this.events = events;
        }

        public int CurrentVoices => 0;

        public int MaxVoices => 0;

        public AudioPlaybackInitializationResult Initialize(PlayerSettingsSnapshot settings)
        {
            throw new AssertFailedException("Playback initialization should not be called by this lifecycle test.");
        }

        public void ClearMaxVoices()
        {
            events.Add("clear-max-voices");
        }

        public void SetVolume(int volume)
        {
            events.Add("volume:" + volume);
        }

        public void Free()
        {
            events.Add("free");
        }
    }
}
