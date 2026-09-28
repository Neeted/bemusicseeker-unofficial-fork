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
[DoNotParallelize]
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
    public void AudioOutputRequest_EqualityIncludesEveryOutputConditionButNotVolume()
    {
        PlayerSettingsSnapshot baselineSettings = CreatePlayerSettings(AudioDriver.WasapiExclusive);
        AudioOutputRequest baseline = baselineSettings.AudioOutputRequest;

        Assert.IsTrue(baseline.Equals(CreatePlayerSettings(
            AudioDriver.WasapiExclusive,
            playerVolume: 87).AudioOutputRequest));
        Assert.IsFalse(baseline.Equals(CreatePlayerSettings(
            AudioDriver.Asio,
            deviceIdentity: "device-a",
            deviceName: "Device A").AudioOutputRequest));
        Assert.IsFalse(baseline.Equals(CreatePlayerSettings(
            AudioDriver.WasapiExclusive,
            playerSampleRate: SampleRate.SAMPLE_RATE_44100Hz).AudioOutputRequest));
        Assert.IsFalse(baseline.Equals(CreatePlayerSettings(
            AudioDriver.WasapiExclusive,
            playerFormat: SampleFormat.SAMPLE_INT_24BIT).AudioOutputRequest));
        Assert.IsFalse(baseline.Equals(CreatePlayerSettings(
            AudioDriver.WasapiExclusive,
            playerBufferSize: 20).AudioOutputRequest));
        Assert.IsFalse(baseline.Equals(CreatePlayerSettings(
            AudioDriver.WasapiExclusive,
            playerWasapiParam: true).AudioOutputRequest));
        Assert.IsFalse(baseline.Equals(CreatePlayerSettings(
            AudioDriver.WasapiExclusive,
            sampleRateConversionQuality: 2).AudioOutputRequest));
        Assert.AreEqual(AudioOutputPurpose.Playback, baseline.Purpose);
    }

    [TestMethod]
    public void OutputRequests_IgnoreNonEditableModeConditionsWithoutChangingSavedIntent()
    {
        PlayerSettingsSnapshot shared = CreatePlayerSettings(AudioDriver.WasapiShared,
            playerSampleRate: SampleRate.SAMPLE_RATE_44100Hz,
            playerFormat: SampleFormat.SAMPLE_INT_16BIT, playerBufferSize: 20);
        Assert.AreEqual(SampleRate.AUTO, shared.AudioOutputRequest.Rate);
        Assert.AreEqual(SampleFormat.AUTO, shared.AudioOutputRequest.Format);
        Assert.AreEqual(0f, shared.AudioOutputRequest.BufferSize);
        Assert.AreEqual(SampleRate.SAMPLE_RATE_44100Hz, shared.PlayerSampleRate);
        Assert.AreEqual(SampleFormat.SAMPLE_INT_16BIT, shared.PlayerFormat);
        Assert.AreEqual(20f, shared.PlayerBufferSize);
        PlayerSettingsSnapshot asio = CreatePlayerSettings(AudioDriver.Asio,
            playerSampleRate: SampleRate.SAMPLE_RATE_44100Hz,
            playerFormat: SampleFormat.SAMPLE_INT_16BIT, playerWasapiParam: true);
        Assert.AreEqual(SampleRate.SAMPLE_RATE_44100Hz, asio.AudioOutputRequest.Rate);
        Assert.AreEqual(SampleFormat.AUTO, asio.AudioOutputRequest.Format);
        Assert.IsFalse(asio.AudioOutputRequest.EventMode);
        Assert.AreEqual(SampleFormat.SAMPLE_INT_16BIT, asio.PlayerFormat);
        Assert.IsTrue(asio.PlayerWASAPIParam);

        void AssertNoFallback(PlayerSettingsSnapshot settings, SampleRate actualRate)
        {
            BassAudioPlayer.DeviceDriver backend = BassAudioMapping.ToBassDriver(settings.PlayerDriver);
            var device = new BassAudioPlayer.DeviceDescriptor("Selected device", "selected-id");
            var session = new BassAudioSession(backend) { ActualBackend = backend, ActualDevice = device };
            var negotiated = new BassAudioBackendResult(
                new BassAudioNegotiationRequest(backend, device, settings.AudioOutputRequest.Rate,
                    settings.AudioOutputRequest.Format, settings.AudioOutputRequest.BufferSize),
                device, actualRate, SampleFormat.SAMPLE_FLOAT_32BIT, SampleFormat.SAMPLE_FLOAT_32BIT,
                10, 1, [], null);
            AudioPlaybackInitializationResult result = BassAudioPlaybackRuntime.CreateInitializationResult(
                settings.AudioOutputRequest, settings.PlayerVolume, session, negotiated);
            Assert.IsFalse(result.FallbackOccurred);
            Assert.AreEqual(settings.AudioOutputRequest.Rate, result.RequestedRate);
            Assert.AreEqual(SampleFormat.AUTO, result.RequestedFormat);
            Assert.AreEqual(SampleRate.SAMPLE_RATE_44100Hz, settings.PlayerSampleRate);
            Assert.AreEqual(SampleFormat.SAMPLE_INT_16BIT, settings.PlayerFormat);
        }
        AssertNoFallback(shared, SampleRate.SAMPLE_RATE_48000Hz);
        AssertNoFallback(asio, SampleRate.SAMPLE_RATE_44100Hz);
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
    public async Task InternalPlayer_LateCancelledStartDoesNotCloseTheReplacementSong()
    {
        string directory = Path.Combine(Path.GetTempPath(), "BeMusicSeeker-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        string oldPath = WritePlaybackChart(directory, "old.bms", bpm: 400, measure: 50);
        string newPath = WritePlaybackChart(directory, "new.bms", bpm: 400, measure: 1);
        var events = new List<string>();
        var runtime = new RecordingAudioPlaybackRuntime(events)
        {
            InitializeResult = CreatePlaybackInitializationResult(),
        };
        var player = new InternalBMSAutoPlayerSoundOnly(
            new SettingsPlayerSettingsGateway(() => testSettings),
            runtime,
            autoPlayerFactory: path => new LifecycleBMSAutoPlayer(new Ribbit.BMS.BMSFile(path)));
        int oldCallbackCount = 0;
        int newCallbackCount = 0;
        Task oldStart = player.PlayStart(oldPath, (_, _) => oldCallbackCount++);
        Task newStart = Task.CompletedTask;

        var failures = new List<Exception>();
        try
        {
            newStart = player.PlayStart(newPath, (_, _) => newCallbackCount++);
            Assert.IsTrue(player.Duration > TimeSpan.Zero);

            await Assert.ThrowsExceptionAsync<OperationCanceledException>(
                () => oldStart);

            Assert.IsTrue(player.Duration > TimeSpan.Zero, "The old start's delayed cleanup must leave the new song active.");
            Assert.AreEqual(0, oldCallbackCount);
            Assert.AreEqual(0, newCallbackCount);
            await newStart;
        }
        catch (Exception exception)
        {
            failures.Add(exception);
        }

        // Short replacement charts finish naturally; only a failed assertion needs CloseProcess to signal cancellation.
        if (!oldStart.IsCompleted || !newStart.IsCompleted)
        {
            CaptureCleanup(failures, player.CloseProcess);
        }
        await CaptureTaskCompletionAsync(oldStart, failures, exception => exception is OperationCanceledException);
        await CaptureTaskCompletionAsync(newStart, failures, exception => exception is OperationCanceledException);
        CaptureCleanup(failures, player.CloseProcess);
        CaptureCleanup(failures, () => Directory.Delete(directory, recursive: true));
        ThrowFailures(failures);
    }

    [TestMethod]
    public async Task InternalPlayer_ReleasesOldPlayerBeforeChangingOutputSession()
    {
        string directory = Path.Combine(Path.GetTempPath(), "BeMusicSeeker-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        string oldPath = WritePlaybackChart(directory, "old.bms", bpm: 400, measure: 50);
        string newPath = WritePlaybackChart(directory, "new.bms", bpm: 400, measure: 1);
        var events = new List<string>();
        var initializedSessions = new List<BassAudioSession>();
        BeMusicSeeker.Properties.Settings settings = CreateAudiblePlaybackSettings(SampleRate.SAMPLE_RATE_44100Hz);
        BassAudioPlaybackRuntime runtime = CreateInjectedBassAudioPlaybackRuntime(events, initializedSessions);
        ObservingBMSAutoPlayer? oldPlayer = null;
        int factoryCalls = 0;
        var player = new InternalBMSAutoPlayerSoundOnly(
            new SettingsPlayerSettingsGateway(() => settings),
            runtime,
            autoPlayerFactory: path =>
            {
                factoryCalls++;
                events.Add("player-factory:" + factoryCalls);
                if (oldPlayer == null)
                {
                    oldPlayer = new ObservingBMSAutoPlayer(new Ribbit.BMS.BMSFile(path), events);
                    return oldPlayer;
                }
                return new LifecycleBMSAutoPlayer(new Ribbit.BMS.BMSFile(path));
            });
        Task oldStart = player.PlayStart(oldPath);
        Task newStart = Task.CompletedTask;

        var failures = new List<Exception>();
        try
        {
            Assert.AreEqual(1, initializedSessions.Count);
            ObservingBMSAutoPlayer observedOldPlayer = oldPlayer
                ?? throw new AssertFailedException("The first BMS player was not created.");
            var userSeekTime = TimeSpan.FromMilliseconds(250);
            int moveCountBeforeUserSeek = observedOldPlayer.MoveToCount;
            player.CurrentTime = userSeekTime;
            Assert.AreEqual(moveCountBeforeUserSeek + 1, observedOldPlayer.MoveToCount,
                "公開CurrentTime setterは再生中の譜面へseekを届けます。");
            Assert.AreEqual(userSeekTime, observedOldPlayer.LastMoveToTime);
            int oldMoveCountBeforeReplacement = observedOldPlayer.MoveToCount;

            AudioOutputRequest firstRequest = initializedSessions[0].OutputRequest;
            settings.PlayerSampleRate = SampleRate.SAMPLE_RATE_48000Hz;

            newStart = player.PlayStart(newPath);

            Assert.AreEqual(oldMoveCountBeforeReplacement, observedOldPlayer.MoveToCount,
                "次曲切替時の表示リセットは旧曲へのseekを発生させません。");
            Assert.AreEqual(2, initializedSessions.Count);
            Assert.IsFalse(firstRequest.Equals(initializedSessions[1].OutputRequest));
            CollectionAssert.AreEqual(
                new[]
                {
                    "native-initialize:1",
                    "player-factory:1",
                    "old-stop-joined",
                    "old-source-release-complete",
                    "session-release:1",
                    "native-initialize:2",
                    "player-factory:2",
                },
                events);
            await Assert.ThrowsExceptionAsync<OperationCanceledException>(
                () => oldStart);
            await newStart;
        }
        catch (Exception exception)
        {
            failures.Add(exception);
        }

        if (!oldStart.IsCompleted || !newStart.IsCompleted)
        {
            CaptureCleanup(failures, player.CloseProcess);
        }
        await CaptureTaskCompletionAsync(oldStart, failures, exception => exception is OperationCanceledException);
        await CaptureTaskCompletionAsync(newStart, failures, exception => exception is OperationCanceledException);
        CaptureCleanup(failures, player.CloseProcess);
        CaptureCleanup(failures, () => Directory.Delete(directory, recursive: true));
        ThrowFailures(failures);
    }

    [TestMethod]
    public async Task InternalPlayer_SourceReleaseFailureStopsBeforeCallingNextPlayerFactory()
    {
        string directory = Path.Combine(Path.GetTempPath(), "BeMusicSeeker-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        string oldPath = WritePlaybackChart(directory, "old.bms", bpm: 400, measure: 1);
        string invalidNextPath = Path.Combine(directory, "invalid-next.bms");
        File.WriteAllText(invalidNextPath, "not a BMS chart", System.Text.Encoding.ASCII);
        var expectedFailure = new BassAudioPlaybackException(
            BassAudioPlaybackStage.SourceRelease,
            oldPath,
            41,
            17,
            0,
            "BASS_StreamFree",
            nativeErrorCode: null,
            message: BeMusicSeeker.Properties.Resources.AudioDeviceTestCleanupFailure);
        var events = new List<string>();
        var initializedSessions = new List<BassAudioSession>();
        BeMusicSeeker.Properties.Settings settings = CreateAudiblePlaybackSettings(SampleRate.SAMPLE_RATE_44100Hz);
        ThrowingReleaseBMSAutoPlayer? oldPlayer = null;
        int factoryCalls = 0;
        BassAudioPlaybackRuntime runtime = CreateInjectedBassAudioPlaybackRuntime(events, initializedSessions);
        var player = new InternalBMSAutoPlayerSoundOnly(
            new SettingsPlayerSettingsGateway(() => settings),
            runtime,
            autoPlayerFactory: path =>
            {
                factoryCalls++;
                events.Add("player-factory:" + factoryCalls);
                if (oldPlayer != null)
                {
                    return new LifecycleBMSAutoPlayer(new Ribbit.BMS.BMSFile(path));
                }
                oldPlayer = new ThrowingReleaseBMSAutoPlayer(
                    new Ribbit.BMS.BMSFile(path), expectedFailure, events);
                return oldPlayer;
            });
        Task oldStart = player.PlayStart(oldPath);
        Task rejectedStart = Task.CompletedTask;

        var failures = new List<Exception>();
        try
        {
            await oldStart;
            Assert.AreEqual(1, initializedSessions.Count);
            settings.PlayerSampleRate = SampleRate.SAMPLE_RATE_48000Hz;

            rejectedStart = player.PlayStart(invalidNextPath);
            BassAudioPlaybackException actual = await Assert.ThrowsExceptionAsync<BassAudioPlaybackException>(
                () => rejectedStart);

            Assert.AreSame(expectedFailure, actual);
            Assert.AreEqual(BassAudioPlaybackStage.SourceRelease, actual.Stage);
            Assert.AreEqual(1, factoryCalls, "The next BMS parser/player factory must not run after old-source release fails.");
            Assert.AreEqual(1, initializedSessions.Count, "A failed old-source release must not initialize the changed output request.");
            CollectionAssert.AreEqual(
                new[]
                {
                    "native-initialize:1",
                    "player-factory:1",
                    "old-stop-joined",
                    "old-source-release-complete",
                    "session-release:1",
                },
                events);
        }
        catch (Exception exception)
        {
            failures.Add(exception);
        }

        if (!oldStart.IsCompleted || !rejectedStart.IsCompleted)
        {
            CaptureCleanup(failures, player.CloseProcess);
        }
        await CaptureTaskCompletionAsync(oldStart, failures, exception => exception is OperationCanceledException);
        await CaptureTaskCompletionAsync(rejectedStart, failures, exception => ReferenceEquals(exception, expectedFailure));
        if (oldPlayer != null)
        {
            CaptureCleanup(failures, () => oldPlayer.FailNextRelease = false);
            CaptureCleanup(failures, oldPlayer.Dispose);
        }
        CaptureCleanup(failures, player.CloseProcess);
        CaptureCleanup(failures, () => Directory.Delete(directory, recursive: true));
        ThrowFailures(failures);
    }

    [TestMethod]
    public async Task InternalPlayer_DispatchesTheCapturedExitCallbackAfterTheNextSongStarts()
    {
        string directory = Path.Combine(Path.GetTempPath(), "BeMusicSeeker-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        string oldPath = WritePlaybackChart(directory, "old.bms", bpm: 400, measure: 1);
        string newPath = WritePlaybackChart(directory, "new.bms", bpm: 400, measure: 1);
        var capturedExit = new TaskCompletionSource<(Action<object, EventArgs> Callback, object Sender)>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var events = new List<string>();
        var runtime = new RecordingAudioPlaybackRuntime(events)
        {
            InitializeResult = CreatePlaybackInitializationResult(),
        };
        var player = new InternalBMSAutoPlayerSoundOnly(
            new SettingsPlayerSettingsGateway(() => testSettings),
            runtime,
            (callback, sender) => capturedExit.TrySetResult((callback, sender)),
            path => new LifecycleBMSAutoPlayer(new Ribbit.BMS.BMSFile(path)));
        int generation = 1;
        int oldCallbackCount = 0;
        int newCallbackCount = 0;
        Action<object, EventArgs> oldCallback = (_, _) =>
        {
            if (generation == 1)
            {
                oldCallbackCount++;
                player.CloseProcess();
            }
        };
        Task oldStart = player.PlayStart(oldPath, oldCallback);
        Task newStart = Task.CompletedTask;

        var failures = new List<Exception>();
        try
        {
            await oldStart;
            (Action<object, EventArgs> callback, object sender) = await capturedExit.Task;
            Assert.AreSame(oldCallback, callback);

            generation = 2;
            newStart = player.PlayStart(newPath, (_, _) => newCallbackCount++);
            TimeSpan newDuration = player.Duration;
            Assert.IsTrue(newDuration > TimeSpan.Zero);

            callback(sender, EventArgs.Empty);

            Assert.AreEqual(0, oldCallbackCount, "The application's existing generation guard must ignore the stale exit.");
            Assert.AreEqual(0, newCallbackCount, "The captured old callback must not dispatch the new callback.");
            Assert.AreEqual(newDuration, player.Duration, "A stale exit must not close the replacement song.");
            await newStart;
        }
        catch (Exception exception)
        {
            failures.Add(exception);
        }

        CaptureCleanup(failures, () => capturedExit.TrySetCanceled());
        await CaptureTaskCompletionAsync(capturedExit.Task, failures, exception => exception is OperationCanceledException);
        if (!oldStart.IsCompleted || !newStart.IsCompleted)
        {
            CaptureCleanup(failures, player.CloseProcess);
        }
        await CaptureTaskCompletionAsync(oldStart, failures, exception => exception is OperationCanceledException);
        await CaptureTaskCompletionAsync(newStart, failures, exception => exception is OperationCanceledException);
        CaptureCleanup(failures, player.CloseProcess);
        CaptureCleanup(failures, () => Directory.Delete(directory, recursive: true));
        ThrowFailures(failures);
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
            isSilentFallback: false,
            endpointContainerBits: 16,
            endpointEffectiveBits: 16,
            attempts:
            [
                new BassAudioBackendAttempt("BASS_ASIO_ChannelGetFormat", "BASSASIO", null, "mismatched"),
                new BassAudioBackendAttempt("BASS_WASAPI_GetInfo", "BASSWASAPI", null, "readback")
            ]);

        Assert.AreEqual(AudioDriver.Asio, result.RequestedBackend);
        Assert.AreEqual(AudioDriver.WasapiShared, result.ActualBackend);
        Assert.AreEqual("asio-requested", result.RequestedDevice);
        Assert.AreEqual("wasapi-actual", result.ActualDevice);
        Assert.AreEqual(SampleRate.SAMPLE_RATE_96000Hz, result.RequestedRate);
        Assert.AreEqual(SampleRate.SAMPLE_RATE_48000Hz, result.ActualRate);
        Assert.AreEqual(SampleFormat.SAMPLE_INT_24BIT, result.RequestedFormat);
        Assert.AreEqual(SampleFormat.SAMPLE_FLOAT_32BIT, result.EngineFormat);
        Assert.AreEqual(SampleFormat.SAMPLE_INT_16BIT, result.EndpointFormat);
        Assert.AreEqual(16, result.EndpointContainerBits);
        Assert.AreEqual(16, result.EndpointEffectiveBits);
        Assert.AreEqual(2, result.Attempts.Count);
        Assert.AreEqual("BASS_ASIO_ChannelGetFormat", result.Attempts[0].Stage);
        Assert.AreEqual("BASS_WASAPI_GetInfo", result.Attempts[1].Stage);
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
    public async Task PlaybackRuntime_TransientAudioRequestBusyIsTypedAndHasNoNativeSideEffects()
    {
        var accepted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var blocker = Task.Run(async () =>
        {
            try
            {
                if (!BassAudioRuntime.TryEnterAudioRequest(out IDisposable admission))
                {
                    throw new InvalidOperationException("The audio request gate was not available for the test blocker.");
                }
                using (admission)
                {
                    accepted.TrySetResult();
                    await release.Task;
                }
            }
            catch (Exception exception)
            {
                accepted.TrySetException(exception);
                throw;
            }
        });
        int initializeCalls = 0;
        int releaseCalls = 0;
        var runtime = new BassAudioPlaybackRuntime(
            (_, _) => initializeCalls++,
            _ =>
            {
                releaseCalls++;
                return true;
            });
        PlayerSettingsSnapshot settings = CreatePlayerSettings(
            AudioDriver.WasapiExclusive,
            deviceIdentity: "requested-device-id",
            deviceName: "Requested device",
            playerSampleRate: SampleRate.SAMPLE_RATE_44100Hz,
            playerFormat: SampleFormat.SAMPLE_INT_24BIT);

        try
        {
            await accepted.Task;

            AudioInitializationException exception = Assert.ThrowsException<AudioInitializationException>(
                () => runtime.Initialize(settings));

            Assert.AreEqual("audio request busy", exception.Stage);
            Assert.AreEqual("BassAudioOperationGate", exception.NativeErrorSource);
            Assert.AreEqual(BassAudioPlayer.DeviceDriver.WASAPI_EXCLUSIVE, exception.RequestedBackend);
            Assert.AreEqual("requested-device-id", exception.RequestedDevice.Driver);
            Assert.AreEqual("Requested device", exception.RequestedDevice.Name);
            Assert.AreEqual(0, initializeCalls);
            Assert.AreEqual(0, releaseCalls);
        }
        finally
        {
            release.TrySetResult();
            await blocker;
        }
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
                var session = new BassAudioSession(
                    backend,
                    device,
                    settings.SampleRateConversionQuality,
                    settings.AudioOutputRequest)
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
        Assert.AreSame(
            original,
            runtime.Initialize(CreatePlayerSettings(AudioDriver.WasapiShared, 4, playerVolume: 83)));
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
        var successfulAttempt = new BassAudioBackendAttempt(
            "BASS_WASAPI_Start",
            "BASSWASAPI",
            null,
            "started");
        var result = new BassAudioBackendResult(
            request,
            new BassAudioPlayer.DeviceDescriptor("Endpoint", "endpoint-id"),
            SampleRate.SAMPLE_RATE_48000Hz,
            SampleFormat.SAMPLE_FLOAT_32BIT,
            SampleFormat.SAMPLE_FLOAT_32BIT,
            10,
            42,
            [successfulAttempt],
            null,
            endpointContainerBits: 32,
            endpointEffectiveBits: 24);
        var firstFailedAttempt = new BassAudioBackendAttempt(
            "BASS_WASAPI_Init",
            "BASSWASAPI",
            Errors.Busy,
            "exclusive failed");
        var secondFailedAttempt = new BassAudioBackendAttempt(
            "BASS_WASAPI_GetDeviceInfo",
            "BASSWASAPI",
            Errors.Device,
            "selected endpoint failed");

        BassAudioBackendResult fallback = result.WithEarlierAttempts(
            [firstFailedAttempt, secondFailedAttempt],
            "attemptedBackend=WASAPI_EXCLUSIVE nativeErrorCode=BASS_ERROR_BUSY; "
            + "fallbackDestination=WASAPI_SHARED");

        Assert.AreEqual(3, fallback.Attempts.Count);
        Assert.AreSame(firstFailedAttempt, fallback.Attempts[0]);
        Assert.AreSame(secondFailedAttempt, fallback.Attempts[1]);
        Assert.AreSame(successfulAttempt, fallback.Attempts[2]);
        Assert.AreEqual(SampleRate.SAMPLE_RATE_48000Hz, fallback.ActualRate);
        Assert.AreEqual(SampleFormat.SAMPLE_FLOAT_32BIT, fallback.EngineFormat);
        Assert.AreEqual(32, fallback.EndpointContainerBits);
        Assert.AreEqual(24, fallback.EndpointEffectiveBits);
        StringAssert.Contains(fallback.FallbackReason, "BASS_ERROR_BUSY");
        StringAssert.Contains(fallback.FallbackReason, "fallbackDestination=WASAPI_SHARED");
    }

    private static PlayerSettingsSnapshot CreatePlayerSettings(
        AudioDriver driver,
        int sampleRateConversionQuality = AudioResamplingQuality.Default,
        string deviceIdentity = "",
        string deviceName = "",
        SampleRate playerSampleRate = SampleRate.AUTO,
        SampleFormat playerFormat = SampleFormat.AUTO,
        float playerBufferSize = 10,
        bool playerWasapiParam = false,
        int playerVolume = 50)
    {
        return new PlayerSettingsSnapshot(
            driver,
            deviceIdentity,
            deviceName,
            playerSampleRate,
            playerFormat,
            playerBufferSize,
            playerWasapiParam,
            playerVolume,
            new PlayerResolution(800, 600),
            false,
            default,
            sampleRateConversionQuality);
    }

    private static BeMusicSeeker.Properties.Settings CreateAudiblePlaybackSettings(SampleRate sampleRate) => new()
    {
        PlayerDriver = BassAudioPlayer.DeviceDriver.WASAPI_EXCLUSIVE,
        PlayerDevice = "audio-contract-test-device",
        PlayerDeviceName = "Audio contract test endpoint",
        PlayerSampleRate = sampleRate,
        PlayerFormat = SampleFormat.SAMPLE_FLOAT_32BIT,
        PlayerBufferSize = 10,
        PlayerWASAPIParam = false,
        PlayerResamplingQuality = AudioResamplingQuality.Default,
    };

    private static BassAudioPlaybackRuntime CreateInjectedBassAudioPlaybackRuntime(
        List<string> events,
        List<BassAudioSession> initializedSessions)
    {
        return new BassAudioPlaybackRuntime(
            (settings, captureSession) =>
            {
                BassAudioPlayer.DeviceDriver backend = BassAudioMapping.ToBassDriver(settings.PlayerDriver);
                var device = new BassAudioPlayer.DeviceDescriptor(
                    settings.PlayerDeviceName,
                    settings.PlayerDevice);
                var negotiation = new BassAudioBackendResult(
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
                    null);
                var session = new BassAudioSession(
                    backend,
                    device,
                    settings.SampleRateConversionQuality,
                    settings.AudioOutputRequest)
                {
                    ActualBackend = backend,
                    ActualDevice = device,
                    State = BassAudioSessionState.Active,
                    NegotiationResult = negotiation,
                };
                initializedSessions.Add(session);
                events.Add("native-initialize:" + initializedSessions.Count);
                captureSession(session);
            },
            session =>
            {
                events.Add("session-release:" + (initializedSessions.IndexOf(session) + 1));
                session.State = BassAudioSessionState.Released;
                return true;
            });
    }

    private static void AssertPersistedEnumValue<TEnum>(TEnum value, int expected)
        where TEnum : struct, Enum
    {
        Assert.AreEqual(expected, Convert.ToInt32(value, CultureInfo.InvariantCulture), value.ToString());
    }

    private static string WritePlaybackChart(string directory, string name, int bpm, int measure)
    {
        string path = Path.Combine(directory, name);
        File.WriteAllText(
            path,
            $"#PLAYER 1\n#TITLE lifecycle\n#ARTIST test\n#BPM {bpm}\n#{measure:000}11:01\n",
            System.Text.Encoding.ASCII);
        return path;
    }

    private static async Task CaptureTaskCompletionAsync(
        Task task,
        List<Exception> failures,
        Func<Exception, bool>? expectedException = null)
    {
        try
        {
            await task.ConfigureAwait(false);
        }
        catch (Exception exception) when (expectedException?.Invoke(exception) == true)
        {
        }
        catch (Exception exception)
        {
            failures.Add(exception);
        }
    }

    private static void CaptureCleanup(List<Exception> failures, Action cleanup)
    {
        try
        {
            cleanup();
        }
        catch (Exception exception)
        {
            failures.Add(exception);
        }
    }

    private static void ThrowFailures(List<Exception> failures)
    {
        if (failures.Count == 1)
        {
            System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(failures[0]).Throw();
        }
        else if (failures.Count > 1)
        {
            throw new AggregateException("The test failure and cleanup failures are retained.", failures);
        }
    }

    private static AudioPlaybackInitializationResult CreatePlaybackInitializationResult() => new(
        AudioDriver.NullDevice,
        string.Empty,
        string.Empty,
        SampleRate.AUTO,
        SampleFormat.AUTO,
        10,
        false,
        50,
        AudioDriver.NullDevice,
        string.Empty,
        string.Empty,
        SampleRate.SAMPLE_RATE_44100Hz,
        SampleFormat.SAMPLE_FLOAT_32BIT,
        SampleFormat.UNKNOWN,
        2,
        0,
        string.Empty,
        isSilentFallback: false);

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

        /// <summary>初期化成功時に返す結果です。</summary>
        internal AudioPlaybackInitializationResult? InitializeResult { get; init; }

        public AudioPlaybackInitializationResult Initialize(PlayerSettingsSnapshot settings)
        {
            events.Add("initialize");
            if (InitializeException != null)
            {
                throw InitializeException;
            }
            return InitializeResult
                ?? throw new AssertFailedException("Playback initialization should not be called by this lifecycle test.");
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

    private class LifecycleBMSAutoPlayer(Ribbit.BMS.BMSFile bms)
        : BMSAutoPlayer(bms)
    {
        public override void LoadResources()
        {
            // InternalBMSAutoPlayerSoundOnlyの寿命だけを検査するため、再生時間は
            // 音声deviceの初期化や存在しないWAV fixtureに依存させない。
            MusicDuration = TimeSpan.FromMilliseconds(500);
        }

        protected override void OnPlaybackStarting()
        {
        }
    }

    private class ObservingBMSAutoPlayer(Ribbit.BMS.BMSFile bms, List<string> events)
        : LifecycleBMSAutoPlayer(bms)
    {
        private int moveToCount;
        private long lastMoveToTicks;

        internal int MoveToCount => System.Threading.Volatile.Read(ref moveToCount);

        internal TimeSpan LastMoveToTime => TimeSpan.FromTicks(System.Threading.Interlocked.Read(ref lastMoveToTicks));

        protected override void MoveTo(TimeSpan time)
        {
            System.Threading.Interlocked.Exchange(ref lastMoveToTicks, time.Ticks);
            System.Threading.Interlocked.Increment(ref moveToCount);
            base.MoveTo(time);
        }

        public override void Stop()
        {
            base.Stop();
            events.Add("old-stop-joined");
        }

        internal override void DisposeBeforeNextSong()
        {
            base.DisposeBeforeNextSong();
            events.Add("old-source-release-complete");
        }
    }

    private sealed class ThrowingReleaseBMSAutoPlayer(
        Ribbit.BMS.BMSFile bms,
        BassAudioPlaybackException failure,
        List<string> events)
        : ObservingBMSAutoPlayer(bms, events)
    {
        internal bool FailNextRelease { get; set; } = true;

        internal override void DisposeBeforeNextSong()
        {
            if (FailNextRelease)
            {
                FailNextRelease = false;
                base.DisposeBeforeNextSong();
                throw failure;
            }
            base.DisposeBeforeNextSong();
        }
    }
}
