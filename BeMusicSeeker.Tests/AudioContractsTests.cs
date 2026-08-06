using System.Collections.Generic;
using System.Linq;
using BeMusicSeeker.Models;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Ribbit.Media;
using Ribbit.Media.Audio;
using Un4seen.Bass;

namespace BeMusicSeeker.Tests;

[TestClass]
[DoNotParallelize]
public sealed class AudioContractsTests
{
    [TestMethod]
    public void AudioDriverPolicyExposesThreeSelectableBackendsWithoutLegacyDisplay()
    {
        Assert.AreEqual(0, (int)AudioDriver.DirectSound);
        Assert.AreEqual(0, (int)BassAudioPlayer.DeviceDriver.DIRECT_SOUND);
        CollectionAssert.AreEqual(
            new[] { AudioDriver.WasapiShared, AudioDriver.WasapiExclusive, AudioDriver.Asio },
            AudioDriverPolicy.SelectableDrivers.ToArray());
        Assert.AreEqual(AudioDriver.WasapiShared, AudioDriverPolicy.DefaultDriver);
        Assert.AreEqual(
            "WASAPI (" + BeMusicSeeker.Properties.Resources.Shared + ")",
            AudioDriverDisplayNames.Get(AudioDriver.DirectSound));
        var legacyDisplayName = AudioDriverDisplayNames.Get(AudioDriver.DirectSound);
        Assert.IsFalse(legacyDisplayName.Contains("BASS", System.StringComparison.Ordinal));
        Assert.IsFalse(legacyDisplayName.Contains("DirectSound", System.StringComparison.Ordinal));
    }

    [TestMethod]
    public void AudioDriverPolicy_NormalizesOnlyLegacyDirectSound()
    {
        AudioOutputSelection normalized = AudioDriverPolicy.NormalizePersistedSelection(
            new AudioOutputSelection(AudioDriver.DirectSound, "legacy-id", "Legacy name"));

        Assert.AreEqual(AudioDriver.WasapiShared, normalized.Backend);
        Assert.IsTrue(normalized.IsDefault);
        Assert.IsNull(normalized.DeviceIdentity);
        Assert.IsNull(normalized.DeviceName);

        AudioOutputSelection unknown = new((AudioDriver)47, "unknown-id", "Unknown name");
        AudioOutputSelection invalid = new(AudioDriver.Invalid, "invalid-id", "Invalid name");
        AudioOutputSelection nullDevice = new(AudioDriver.NullDevice, "null-id", "Null name");
        Assert.AreEqual(unknown, AudioDriverPolicy.NormalizePersistedSelection(unknown));
        Assert.AreEqual(invalid, AudioDriverPolicy.NormalizePersistedSelection(invalid));
        Assert.AreEqual(nullDevice, AudioDriverPolicy.NormalizePersistedSelection(nullDevice));

        Assert.AreEqual(0, AudioDriverPolicy.IndexOf(AudioDriver.WasapiShared));
        Assert.AreEqual(1, AudioDriverPolicy.IndexOf(AudioDriver.WasapiExclusive));
        Assert.AreEqual(2, AudioDriverPolicy.IndexOf(AudioDriver.Asio));
        Assert.AreEqual(-1, AudioDriverPolicy.IndexOf(AudioDriver.DirectSound));
    }

    [TestMethod]
    public void SettingsAudioGateway_PreservesKnownAndUnknownPersistedValues()
    {
        BeMusicSeeker.Properties.Settings settings = BeMusicSeeker.Properties.Settings.Default;
        var originalDriver = settings.PlayerDriver;
        string originalDevice = settings.PlayerDevice;
        string originalDeviceName = settings.PlayerDeviceName;
        var originalNormalization = settings.EncoderNormalization;
        var originalEncoder = settings.Encoder;
        try
        {
            settings.PlayerDriver = Ribbit.Media.BassAudioPlayer.DeviceDriver.DIRECT_SOUND;
            settings.PlayerDevice = "legacy-device";
            settings.PlayerDeviceName = "Legacy Device";
            SettingsAudioGateway gateway = new(() => settings);

            AudioOutputSelection selection = gateway.CaptureOutputSelection();
            Assert.AreEqual(AudioDriver.WasapiShared, selection.Backend);
            Assert.IsTrue(selection.IsDefault);
            Assert.AreEqual(Ribbit.Media.BassAudioPlayer.DeviceDriver.DIRECT_SOUND, settings.PlayerDriver);

            settings.PlayerDriver = (Ribbit.Media.BassAudioPlayer.DeviceDriver)47;
            settings.PlayerDevice = "persisted-device";
            settings.PlayerDeviceName = "Persisted Device";
            settings.EncoderNormalization = (Ribbit.BMS.BMSAutoPlayWriter.Normalization)53;

            selection = gateway.CaptureOutputSelection();
            Assert.AreEqual((AudioDriver)47, selection.Backend);
            Assert.AreEqual("persisted-device", selection.DeviceIdentity);
            Assert.AreEqual("Persisted Device", selection.DeviceName);
            Assert.AreEqual((AudioNormalization)53, gateway.EncoderNormalization);

            gateway.ApplyOutputSelection(new AudioOutputSelection(
                (AudioDriver)61,
                "replacement-device",
                "Replacement Device"));
            gateway.EncoderNormalization = (AudioNormalization)67;
            gateway.ApplyEncoderFallback(Ribbit.Media.Audio.EncoderType.FLAC);

            Assert.AreEqual((Ribbit.Media.BassAudioPlayer.DeviceDriver)61, settings.PlayerDriver);
            Assert.AreEqual("replacement-device", settings.PlayerDevice);
            Assert.AreEqual("Replacement Device", settings.PlayerDeviceName);
            Assert.AreEqual((Ribbit.BMS.BMSAutoPlayWriter.Normalization)67, settings.EncoderNormalization);
            Assert.AreEqual(Ribbit.Media.Audio.EncoderType.FLAC, settings.Encoder);
        }
        finally
        {
            settings.PlayerDriver = originalDriver;
            settings.PlayerDevice = originalDevice;
            settings.PlayerDeviceName = originalDeviceName;
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

        Assert.AreEqual(BeMusicSeeker.Properties.Resources.AudioDeviceDefault, defaultDevice.FriendlyName);
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

    [TestMethod]
    public void PlaybackInitializationResult_SeparatesRequestedAndNegotiatedValues()
    {
        var result = new AudioPlaybackInitializationResult(
            AudioDriver.Asio,
            "asio-requested",
            "Requested ASIO",
            SampleRate.SAMPLE_RATE_96000Hz,
            SampleFormat.SAMPLE_INT_24BIT,
            12,
            true,
            73,
            AudioDriver.WasapiShared,
            "wasapi-actual",
            "Actual WASAPI",
            SampleRate.SAMPLE_RATE_48000Hz,
            SampleFormat.SAMPLE_FLOAT_32BIT,
            SampleFormat.SAMPLE_INT_16BIT,
            6,
            18.5,
            "attemptedBackend=ASIO; fallbackDestination=WASAPI_SHARED",
            isSilentFallback: false);

        Assert.AreEqual(AudioDriver.Asio, result.RequestedBackend);
        Assert.AreEqual(AudioDriver.WasapiShared, result.ActualBackend);
        Assert.AreEqual("asio-requested", result.RequestedDevice);
        Assert.AreEqual("wasapi-actual", result.ActualDevice);
        Assert.AreEqual(SampleRate.SAMPLE_RATE_96000Hz, result.RequestedRate);
        Assert.AreEqual(SampleRate.SAMPLE_RATE_48000Hz, result.ActualRate);
        Assert.AreEqual(SampleFormat.SAMPLE_INT_24BIT, result.RequestedFormat);
        Assert.AreEqual(SampleFormat.SAMPLE_FLOAT_32BIT, result.EngineFormat);
        Assert.AreEqual(SampleFormat.SAMPLE_INT_16BIT, result.EndpointFormat);
        Assert.AreEqual(6, result.ActualChannels);
        Assert.IsTrue(result.FallbackOccurred);
        Assert.IsFalse(result.IsSilentFallback);
        StringAssert.Contains(result.FallbackReason, "fallbackDestination=WASAPI_SHARED");
    }

    [TestMethod]
    public void UnknownWasapiEndpointFormat_DoesNotImplyFallback()
    {
        var result = new AudioPlaybackInitializationResult(
            AudioDriver.WasapiShared,
            string.Empty,
            "Default WASAPI",
            SampleRate.AUTO,
            SampleFormat.SAMPLE_FLOAT_32BIT,
            10,
            false,
            50,
            AudioDriver.WasapiShared,
            string.Empty,
            "Default WASAPI",
            SampleRate.SAMPLE_RATE_48000Hz,
            SampleFormat.SAMPLE_FLOAT_32BIT,
            SampleFormat.UNKNOWN,
            2,
            10,
            null,
            isSilentFallback: false);

        Assert.AreEqual(SampleFormat.SAMPLE_FLOAT_32BIT, result.EngineFormat);
        Assert.AreEqual(SampleFormat.UNKNOWN, result.EndpointFormat);
        Assert.IsFalse(result.FallbackOccurred);
    }

    [TestMethod]
    public void PlaybackRuntime_NullDeviceRequestFailsBeforeAudibleInitialization()
    {
        var runtime = new BassAudioPlaybackRuntime();
        PlayerSettingsSnapshot settings = CreatePlayerSettings(AudioDriver.NullDevice);

        AudioInitializationException exception = Assert.ThrowsException<AudioInitializationException>(
            () => runtime.Initialize(settings));

        Assert.AreEqual(BassAudioPlayer.DeviceDriver.NULL_DEVICE, exception.RequestedBackend);
        Assert.AreEqual(BassAudioPlayer.DeviceDriver.INVALID, exception.ActualBackend);
        Assert.AreEqual("backend selection", exception.Stage);
        Assert.AreNotEqual(BassAudioPlayer.DeviceDriver.DIRECT_SOUND, exception.ActualBackend);
    }

    [TestMethod]
    public void BackendResult_PreservesEarlierFailureWhenFallbackSucceeds()
    {
        var request = new BassAudioNegotiationRequest(
            BassAudioPlayer.DeviceDriver.WASAPI_SHARED,
            default,
            SampleRate.SAMPLE_RATE_48000Hz,
            SampleFormat.SAMPLE_FLOAT_32BIT,
            10);
        var result = new BassAudioBackendResult(
            request,
            new BassAudioPlayer.DeviceDescriptor("Endpoint", "endpoint-id"),
            SampleRate.SAMPLE_RATE_48000Hz,
            SampleFormat.SAMPLE_FLOAT_32BIT,
            SampleFormat.SAMPLE_FLOAT_32BIT,
            10,
            42,
            [],
            null);
        var failedAttempt = new BassAudioBackendAttempt(
            "BASS_WASAPI_Init",
            "BASSWASAPI",
            BASSError.BASS_ERROR_BUSY,
            "exclusive failed");

        BassAudioBackendResult fallback = result.WithEarlierAttempts(
            [failedAttempt],
            "attemptedBackend=WASAPI_EXCLUSIVE nativeErrorCode=BASS_ERROR_BUSY; "
            + "fallbackDestination=WASAPI_SHARED");

        Assert.AreEqual(1, fallback.Attempts.Count);
        Assert.AreSame(failedAttempt, fallback.Attempts[0]);
        StringAssert.Contains(fallback.FallbackReason, "BASS_ERROR_BUSY");
        StringAssert.Contains(fallback.FallbackReason, "fallbackDestination=WASAPI_SHARED");
    }

    private static PlayerSettingsSnapshot CreatePlayerSettings(AudioDriver driver)
    {
        return new PlayerSettingsSnapshot(
            driver,
            string.Empty,
            string.Empty,
            SampleRate.AUTO,
            SampleFormat.AUTO,
            10,
            false,
            50,
            new PlayerResolution(800, 600),
            false,
            default);
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
