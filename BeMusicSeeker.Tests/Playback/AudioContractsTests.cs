using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using BeMusicSeeker.Models;
using ManagedBass;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Ribbit.BMS;
using Ribbit.Media;
using Ribbit.Media.Audio;

namespace BeMusicSeeker.Tests;

[TestClass]
public sealed class AudioContractsTests
{
    private readonly BeMusicSeeker.Properties.Settings testSettings = new();
    [TestMethod]
    public void PersistedAudioEnumValuesRemainStable()
    {
        AssertPersistedEnumValue(EncoderType.WAVE, 0);
        AssertPersistedEnumValue(EncoderType.MP3_LAME, 1);
        AssertPersistedEnumValue(EncoderType.AAC_NERO, 2);
        AssertPersistedEnumValue(EncoderType.OPUS, 3);
        AssertPersistedEnumValue(EncoderType.FLAC, 4);
        AssertPersistedEnumValue(EncoderType.OGG_VORBIS, 5);

        AssertPersistedEnumValue(SampleFormat.UNKNOWN, -1);
        AssertPersistedEnumValue(SampleFormat.AUTO, 0);
        AssertPersistedEnumValue(SampleFormat.SAMPLE_INT_8BIT, 1);
        AssertPersistedEnumValue(SampleFormat.SAMPLE_INT_16BIT, 2);
        AssertPersistedEnumValue(SampleFormat.SAMPLE_INT_24BIT, 3);
        AssertPersistedEnumValue(SampleFormat.SAMPLE_INT_32BIT, 4);
        AssertPersistedEnumValue(SampleFormat.SAMPLE_FLOAT_32BIT, 5);

        AssertPersistedEnumValue(SampleRate.AUTO, 0);
        AssertPersistedEnumValue(SampleRate.SAMPLE_RATE_11025Hz, 11025);
        AssertPersistedEnumValue(SampleRate.SAMPLE_RATE_22050Hz, 22050);
        AssertPersistedEnumValue(SampleRate.SAMPLE_RATE_32000Hz, 32000);
        AssertPersistedEnumValue(SampleRate.SAMPLE_RATE_44100Hz, 44100);
        AssertPersistedEnumValue(SampleRate.SAMPLE_RATE_48000Hz, 48000);
        AssertPersistedEnumValue(SampleRate.SAMPLE_RATE_88200Hz, 88200);
        AssertPersistedEnumValue(SampleRate.SAMPLE_RATE_96000Hz, 96000);
        AssertPersistedEnumValue(SampleRate.SAMPLE_RATE_176400Hz, 176400);
        AssertPersistedEnumValue(SampleRate.SAMPLE_RATE_192000Hz, 192000);

        AssertPersistedEnumValue(AudioDriver.Invalid, -2);
        AssertPersistedEnumValue(AudioDriver.NullDevice, -1);
        AssertPersistedEnumValue(AudioDriver.DirectSound, 0);
        AssertPersistedEnumValue(AudioDriver.WasapiShared, 1);
        AssertPersistedEnumValue(AudioDriver.WasapiExclusive, 2);
        AssertPersistedEnumValue(AudioDriver.Asio, 3);

        AssertPersistedEnumValue(BassAudioPlayer.DeviceDriver.INVALID, -2);
        AssertPersistedEnumValue(BassAudioPlayer.DeviceDriver.NULL_DEVICE, -1);
        AssertPersistedEnumValue(BassAudioPlayer.DeviceDriver.DIRECT_SOUND, 0);
        AssertPersistedEnumValue(BassAudioPlayer.DeviceDriver.WASAPI_SHARED, 1);
        AssertPersistedEnumValue(BassAudioPlayer.DeviceDriver.WASAPI_EXCLUSIVE, 2);
        AssertPersistedEnumValue(BassAudioPlayer.DeviceDriver.ASIO, 3);
    }

    [TestMethod]
    public void EncoderExtensionsSeparatePersistedAndPhysicalAacContracts()
    {
        CollectionAssert.AreEqual(
            new[] { ".wav", ".mp3", ".aac", ".opus", ".flac", ".ogg" },
            Enum.GetValues<EncoderType>().Select(value => value.GetExtension()).ToArray());

        Assert.AreEqual(string.Empty, EncoderType.WAVE.SearchEncoderBinary());
        CollectionAssert.AreEqual(
            new[] { ".wav", ".mp3", ".m4a", ".opus", ".flac", ".ogg" },
            Enum.GetValues<EncoderType>().Select(value => value.GetEncoderOutputExtension()).ToArray());
    }

    [TestMethod]
    public void EncoderBinarySearchHonorsConfiguredDirectory()
    {
        string directoryPath = Path.Combine(Path.GetTempPath(), "BeMusicSeekerEncoderContracts", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directoryPath);
        try
        {
            File.WriteAllText(Path.Combine(directoryPath, EncoderType.MP3_LAME.GetEncoderFileName()), string.Empty);

            Assert.AreEqual(directoryPath, EncoderType.MP3_LAME.SearchEncoderBinary(directoryPath));
            Assert.IsNull(EncoderType.OPUS.SearchEncoderBinary(directoryPath));
        }
        finally
        {
            if (Directory.Exists(directoryPath))
            {
                Directory.Delete(directoryPath, recursive: true);
            }
        }
    }

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
        string legacyDisplayName = AudioDriverDisplayNames.Get(AudioDriver.DirectSound);
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
        BeMusicSeeker.Properties.Settings settings = testSettings;
        BassAudioPlayer.DeviceDriver originalDriver = settings.PlayerDriver;
        string originalDevice = settings.PlayerDevice;
        string originalDeviceName = settings.PlayerDeviceName;
        BMSAutoPlayWriter.Normalization originalNormalization = settings.EncoderNormalization;
        EncoderType originalEncoder = settings.Encoder;
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
        BeMusicSeeker.Properties.Settings settings = testSettings;
        BMSAutoPlayWriter.Normalization originalNormalization = settings.EncoderNormalization;
        int originalResamplingQuality = settings.PlayerResamplingQuality;
        try
        {
            settings.EncoderNormalization = Ribbit.BMS.BMSAutoPlayWriter.Normalization.RMS_VALUE;
            settings.PlayerResamplingQuality = 2;
            SettingsAudioGateway gateway = new(() => settings);

            AudioEncodingSettingsSnapshot snapshot = gateway.CaptureEncodingSettings();

            Assert.AreEqual(AudioNormalization.RmsValue, snapshot.EncoderNormalization);
            Assert.AreEqual(settings.Encoder, snapshot.Encoder);
            Assert.AreEqual(settings.EncoderSampleRate, snapshot.EncoderSampleRate);
            Assert.AreEqual(settings.EncoderFormat, snapshot.EncoderFormat);
            Assert.AreEqual(settings.EncoderExeDir, snapshot.EncoderExeDirectory);
            Assert.AreEqual(settings.EncodeFileNameFormat, snapshot.EncodeFileNameFormat);
            Assert.AreEqual(2, snapshot.SampleRateConversionQuality);
        }
        finally
        {
            settings.EncoderNormalization = originalNormalization;
            settings.PlayerResamplingQuality = originalResamplingQuality;
        }
    }

    [DataTestMethod]
    [DataRow(1)]
    [DataRow(7)]
    public void AudioResamplingQuality_RejectsUnsupportedPersistedValues(int quality)
    {
        BeMusicSeeker.Properties.Settings settings = testSettings;
        int originalResamplingQuality = settings.PlayerResamplingQuality;
        try
        {
            settings.PlayerResamplingQuality = quality;

            Assert.ThrowsException<ArgumentOutOfRangeException>(
                () => new SettingsAudioGateway(() => settings).CaptureEncodingSettings());
            Assert.ThrowsException<ArgumentOutOfRangeException>(
                () => new SettingsPlayerSettingsGateway(() => settings).CaptureSnapshot());
        }
        finally
        {
            settings.PlayerResamplingQuality = originalResamplingQuality;
        }
    }

    [TestMethod]
    public void AudioResamplingQuality_UsesFourAsTheMissingSettingDefault()
    {
        var settings = new BeMusicSeeker.Properties.Settings();

        Assert.AreEqual(4, settings.PlayerResamplingQuality);
        CollectionAssert.AreEqual(new[] { 16, 32, 64, 128, 256 },
            new[] { 2, 3, 4, 5, 6 }
                .Select(AudioResamplingQuality.GetSincPointCount)
                .ToArray());
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
    public void NativeErrorFormatter_PreservesLegacyBassSpellings()
    {
        Assert.AreEqual("BASS_ERROR_MEM", BassNativeErrorFormatter.Format(Errors.Memory));
        Assert.AreEqual("BASS_ERROR_NOPAUSE", BassNativeErrorFormatter.Format(Errors.NotPaused));
        Assert.AreEqual("BASS_ERROR_ILLTYPE", BassNativeErrorFormatter.Format(Errors.Type));
        Assert.AreEqual("BASS_ERROR_NOPLAY", BassNativeErrorFormatter.Format(Errors.NotPlaying));
    }

    [TestMethod]
    public void InternalPlayer_DelegatesVolumeAndCloseLifecycleToPlaybackRuntime()
    {
        BeMusicSeeker.Properties.Settings settings = testSettings;
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
    public async Task InternalPlayer_StartFailureCleansUpPlaybackRuntime()
    {
        string bmsFilePath = Path.GetTempFileName();
        try
        {
            var events = new List<string>();
            var startupFailure = new InvalidOperationException("playback initialization failed");
            var runtime = new RecordingAudioPlaybackRuntime(events)
            {
                InitializeException = startupFailure,
            };
            var player = new InternalBMSAutoPlayerSoundOnly(
                new SettingsPlayerSettingsGateway(() => testSettings),
                runtime);

            InvalidOperationException actual = await Assert.ThrowsExceptionAsync<InvalidOperationException>(
                () => player.PlayStart(bmsFilePath));

            Assert.AreSame(startupFailure, actual);
            CollectionAssert.AreEqual(
                new[] { "initialize", "clear-max-voices", "free" },
                events);
        }
        finally
        {
            File.Delete(bmsFilePath);
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
    public void PlaybackRuntime_ReleasesOldSessionWhenNextStartCapturesDifferentSourceQuality()
    {
        var initializedSessions = new List<BassAudioSession>();
        var releaseAttempts = new List<BassAudioSession>();
        bool confirmRelease = false;
        var runtime = new BassAudioPlaybackRuntime(
            (settings, captureSession) =>
            {
                BassAudioPlayer.DeviceDriver backend = BassAudioMapping.ToBassDriver(settings.PlayerDriver);
                var device = new BassAudioPlayer.DeviceDescriptor("Test endpoint", "test-endpoint");
                var session = new BassAudioSession(backend, device, settings.SampleRateConversionQuality)
                {
                    ActualBackend = backend,
                    ActualDevice = device,
                    State = BassAudioSessionState.Active,
                    NegotiationResult = new BassAudioBackendResult(
                        new BassAudioNegotiationRequest(
                            backend,
                            device,
                            settings.PlayerSampleRate,
                            settings.PlayerFormat,
                            settings.PlayerBufferSize),
                        device,
                        SampleRate.SAMPLE_RATE_48000Hz,
                        SampleFormat.SAMPLE_FLOAT_32BIT,
                        SampleFormat.SAMPLE_FLOAT_32BIT,
                        10,
                        1,
                        [],
                        null)
                };
                initializedSessions.Add(session);
                captureSession(session);
            },
            session =>
            {
                releaseAttempts.Add(session);
                if (!confirmRelease)
                {
                    return false;
                }

                session.State = BassAudioSessionState.Released;
                return true;
            });

        PlayerSettingsSnapshot originalSettings = CreatePlayerSettings(AudioDriver.WasapiShared, 4);
        AudioPlaybackInitializationResult original = runtime.Initialize(originalSettings);
        Assert.AreSame(original, runtime.Initialize(originalSettings));
        Assert.AreEqual(1, initializedSessions.Count);

        PlayerSettingsSnapshot savedSettingsForNextStart = CreatePlayerSettings(AudioDriver.WasapiShared, 2);
        Assert.ThrowsException<InvalidOperationException>(
            () => runtime.Initialize(savedSettingsForNextStart));
        Assert.AreSame(original, runtime.Initialize(originalSettings));
        Assert.AreEqual(1, initializedSessions.Count);
        Assert.AreSame(initializedSessions[0], releaseAttempts[0]);
        Assert.AreEqual(BassAudioSessionState.Active, initializedSessions[0].State);

        confirmRelease = true;
        AudioPlaybackInitializationResult replacement = runtime.Initialize(savedSettingsForNextStart);

        Assert.AreNotSame(original, replacement);
        Assert.AreEqual(2, initializedSessions.Count);
        Assert.AreEqual(4, initializedSessions[0].SampleRateConversionQuality);
        Assert.AreEqual(BassAudioSessionState.Released, initializedSessions[0].State);
        Assert.AreEqual(2, initializedSessions[1].SampleRateConversionQuality);
        Assert.AreSame(replacement, runtime.Initialize(savedSettingsForNextStart));

        runtime.Free();
        Assert.AreEqual(BassAudioSessionState.Released, initializedSessions[1].State);
        Assert.AreEqual(3, releaseAttempts.Count);
        Assert.AreSame(initializedSessions[1], releaseAttempts[2]);
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
            Errors.Busy,
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

    private static PlayerSettingsSnapshot CreatePlayerSettings(
        AudioDriver driver,
        int sampleRateConversionQuality = AudioResamplingQuality.Default)
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
            default,
            sampleRateConversionQuality);
    }

    private static void AssertPersistedEnumValue<TEnum>(TEnum value, int expected)
        where TEnum : struct, Enum
    {
        Assert.AreEqual(expected, Convert.ToInt32(value, CultureInfo.InvariantCulture), value.ToString());
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

        /// <summary>
        /// 初期化時に送出する例外。例外を注入しない場合は <see langword="null"/>。
        /// </summary>
        internal Exception? InitializeException { get; init; }

        public AudioPlaybackInitializationResult Initialize(PlayerSettingsSnapshot settings)
        {
            events.Add("initialize");
            if (InitializeException != null)
            {
                throw InitializeException;
            }
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
