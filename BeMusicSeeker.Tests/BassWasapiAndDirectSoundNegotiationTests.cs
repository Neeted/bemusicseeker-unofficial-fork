using System;
using System.Collections.Generic;
using System.Linq;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Ribbit.Media;
using Ribbit.Media.Audio;
using Un4seen.Bass;
using Un4seen.BassWasapi;

namespace BeMusicSeeker.Tests;

[TestClass]
public sealed class BassWasapiAndDirectSoundNegotiationTests
{
    private static readonly WASAPIPROC WasapiCallback = (buffer, length, user) => length;

    private static readonly STREAMPROC DirectSoundCallback = (handle, buffer, length, user) => length;

    [TestMethod]
    public void WasapiCandidates_DegradeWithinBackendAndRemoveDuplicates()
    {
        IReadOnlyList<BassWasapiInitializationCandidate> requested =
            BassWasapiNegotiator.GetInitializationCandidates(true, 0.02f, 0.005f);

        Assert.AreEqual(3, requested.Count);
        Assert.IsTrue(requested[0].EventDriven);
        Assert.IsFalse(requested[1].EventDriven);
        Assert.AreEqual(0.02f, requested[1].BufferSeconds);
        Assert.AreEqual(0.005f, requested[1].PeriodSeconds);
        Assert.AreEqual(0f, requested[2].BufferSeconds);
        Assert.AreEqual(0f, requested[2].PeriodSeconds);

        IReadOnlyList<BassWasapiInitializationCandidate> duplicateDefaults =
            BassWasapiNegotiator.GetInitializationCandidates(false, 0f, 0f);

        Assert.AreEqual(1, duplicateDefaults.Count);
        Assert.IsFalse(duplicateDefaults[0].EventDriven);
    }

    [TestMethod]
    public void WasapiEventAndPeriodFailures_AreRetainedWhenNativeDefaultSucceeds()
    {
        var native = new RecordingWasapiBoundary();
        native.InitializationResults.Enqueue(false);
        native.InitializationErrors.Enqueue(BASSError.BASS_ERROR_BUSY);
        native.InitializationResults.Enqueue(false);
        native.InitializationErrors.Enqueue(BASSError.BASS_ERROR_FORMAT);
        native.InitializationResults.Enqueue(true);
        var session = CreateWasapiSession(BassAudioPlayer.DeviceDriver.WASAPI_EXCLUSIVE);

        BassAudioBackendResult result = new BassWasapiNegotiator(native).Initialize(
            CreateWasapiRequest(BassAudioPlayer.DeviceDriver.WASAPI_EXCLUSIVE),
            session,
            WasapiCallback,
            initialGain: 0.4f,
            eventModeRequested: true);

        Assert.AreEqual(3, native.InitializationCalls.Count);
        Assert.IsTrue(native.InitializationCalls[0].Flags.HasFlag(BASSWASAPIInit.BASS_WASAPI_EVENT));
        Assert.IsFalse(native.InitializationCalls[1].Flags.HasFlag(BASSWASAPIInit.BASS_WASAPI_EVENT));
        Assert.AreEqual(0f, native.InitializationCalls[2].BufferSeconds);
        Assert.AreEqual(0f, native.InitializationCalls[2].PeriodSeconds);
        Assert.AreEqual(3, result.Attempts.Count(attempt => attempt.Stage == "BASS_WASAPI_Init"));
        StringAssert.Contains(result.FallbackReason, "nativeErrorSource=BASSWASAPI");
        StringAssert.Contains(result.FallbackReason, "nativeErrorCode=BASS_ERROR_BUSY");
        StringAssert.Contains(result.FallbackReason, "nativeErrorCode=BASS_ERROR_FORMAT");
        StringAssert.Contains(result.FallbackReason, "native default");
        Assert.IsTrue(session.WasapiInitialized);
    }

    [TestMethod]
    public void WasapiShared_UsesMixShapeAndGetInfoReadbackForResultMixerAndLatency()
    {
        var native = new RecordingWasapiBoundary
        {
            DeviceInfo = CreateWasapiDevice(
                "Shared Device",
                "shared-id",
                BASSWASAPIDeviceInfo.BASS_DEVICE_ENABLED | BASSWASAPIDeviceInfo.BASS_DEVICE_DEFAULT,
                mixRate: 48000,
                mixChannels: 6),
            WasapiInfo = new BASS_WASAPI_INFO
            {
                freq = 96000,
                chans = 4,
                format = BASSWASAPIFormat.BASS_WASAPI_FORMAT_FLOAT,
                buflen = 7680
            }
        };
        native.InitializationResults.Enqueue(true);

        BassAudioBackendResult result = new BassWasapiNegotiator(native).Initialize(
            CreateWasapiRequest(
                BassAudioPlayer.DeviceDriver.WASAPI_SHARED,
                SampleRate.SAMPLE_RATE_44100Hz),
            CreateWasapiSession(BassAudioPlayer.DeviceDriver.WASAPI_SHARED),
            WasapiCallback,
            initialGain: 0.4f,
            eventModeRequested: false);

        WasapiInitializationCall call = native.InitializationCalls.Single();
        Assert.AreEqual(48000, call.Rate);
        Assert.AreEqual(6, call.Channels);
        Assert.AreEqual(96000, native.MixerRate);
        Assert.AreEqual(4, native.MixerChannels);
        Assert.IsTrue(native.MixerFlags.HasFlag(BASSFlag.BASS_SAMPLE_FLOAT));
        Assert.AreEqual(SampleRate.SAMPLE_RATE_96000Hz, result.ActualRate);
        Assert.AreEqual(SampleFormat.SAMPLE_FLOAT_32BIT, result.EngineFormat);
        Assert.AreEqual(SampleFormat.SAMPLE_FLOAT_32BIT, result.EndpointFormat);
        Assert.AreEqual(5d, result.LatencyMilliseconds, 0.0001d);
        StringAssert.Contains(result.FallbackReason, "endpoint mix rate 48000");
    }

    [TestMethod]
    public void WasapiShared_PublishesMixerAndAppliesInitialGainBeforeStart()
    {
        var native = new RecordingWasapiBoundary();
        var session = CreateWasapiSession(BassAudioPlayer.DeviceDriver.WASAPI_SHARED);
        native.InitializationResults.Enqueue(true);
        native.StartObserver = () => Assert.AreEqual(123, session.CallbackOutputHandle);

        new BassWasapiNegotiator(native).Initialize(
            CreateWasapiRequest(BassAudioPlayer.DeviceDriver.WASAPI_SHARED),
            session,
            WasapiCallback,
            initialGain: 0.35f,
            eventModeRequested: false);

        CollectionAssert.AreEqual(
            new[] { "CreateMixer", "CreateVolumeEffect", "SetVolumeEffect", "StartWasapi" },
            native.GraphCalls);
        Assert.AreEqual((234, 0.35f), native.VolumeEffectCalls.Single());
        Assert.AreEqual(123, session.CallbackOutputHandle);
        Assert.AreEqual(234, session.VolumeEffectHandle);
    }

    [TestMethod]
    public void WasapiShared_MixerGainFailureRetainsOwnershipAndDoesNotStart()
    {
        var native = new RecordingWasapiBoundary
        {
            SetVolumeEffectResult = false,
            CoreError = BASSError.BASS_ERROR_HANDLE
        };
        var session = CreateWasapiSession(BassAudioPlayer.DeviceDriver.WASAPI_SHARED);
        native.InitializationResults.Enqueue(true);

        AudioInitializationException exception = Assert.ThrowsException<AudioInitializationException>(
            () => new BassWasapiNegotiator(native).Initialize(
                CreateWasapiRequest(BassAudioPlayer.DeviceDriver.WASAPI_SHARED),
                session,
                WasapiCallback,
                initialGain: 0.25f,
                eventModeRequested: false));

        Assert.AreEqual("BASS_FXSetParameters(BASS_FX_BFX_VOLUME)", exception.Stage);
        Assert.AreEqual("BASS", exception.NativeErrorSource);
        Assert.AreEqual(BASSError.BASS_ERROR_HANDLE, exception.NativeErrorCode);
        CollectionAssert.AreEqual(
            new[] { "CreateMixer", "CreateVolumeEffect", "SetVolumeEffect" },
            native.GraphCalls);
        Assert.IsTrue(session.CoreInitialized);
        Assert.IsTrue(session.WasapiInitialized);
        Assert.AreEqual(123, session.MixerHandle);
        Assert.AreEqual(234, session.VolumeEffectHandle);
        Assert.AreEqual(123, session.CallbackOutputHandle);
        Assert.IsFalse(session.IsStarted);
    }

    [TestMethod]
    public void WasapiShared_VolumeEffectCreationFailureReportsCreationStage()
    {
        var native = new RecordingWasapiBoundary
        {
            CreateVolumeEffectResult = 0,
            CoreError = BASSError.BASS_ERROR_HANDLE
        };
        var session = CreateWasapiSession(BassAudioPlayer.DeviceDriver.WASAPI_SHARED);
        native.InitializationResults.Enqueue(true);

        AudioInitializationException exception = Assert.ThrowsException<AudioInitializationException>(
            () => new BassWasapiNegotiator(native).Initialize(
                CreateWasapiRequest(BassAudioPlayer.DeviceDriver.WASAPI_SHARED),
                session,
                WasapiCallback,
                initialGain: 0.35f,
                eventModeRequested: false));

        Assert.AreEqual("BASS_ChannelSetFX(BASS_FX_BFX_VOLUME)", exception.Stage);
        Assert.AreEqual(BASSError.BASS_ERROR_HANDLE, exception.NativeErrorCode);
        CollectionAssert.AreEqual(
            new[] { "CreateMixer", "CreateVolumeEffect" },
            native.GraphCalls);
    }

    [TestMethod]
    public void WasapiShared_DynamicGainUsesTheSameMixerBoundary()
    {
        var native = new RecordingWasapiBoundary();
        var session = CreateWasapiSession(BassAudioPlayer.DeviceDriver.WASAPI_SHARED);
        native.InitializationResults.Enqueue(true);
        var negotiator = new BassWasapiNegotiator(native);
        negotiator.Initialize(
            CreateWasapiRequest(BassAudioPlayer.DeviceDriver.WASAPI_SHARED),
            session,
            WasapiCallback,
            initialGain: 0.4f,
            eventModeRequested: false);

        var warnings = new List<string>();
        BassAudioPlayer.ApplyWasapiSharedDeviceVolume(
            negotiator,
            session,
            0.15f,
            warnings.Add);

        Assert.AreEqual(0, warnings.Count);
        CollectionAssert.AreEqual(
            new[] { (234, 0.4f), (234, 0.15f) },
            native.VolumeEffectCalls);
    }

    [TestMethod]
    public void WasapiShared_DynamicGainFailureIsReportedWithoutThrowing()
    {
        var native = new RecordingWasapiBoundary
        {
            SetVolumeEffectResult = false,
            CoreError = BASSError.BASS_ERROR_HANDLE
        };
        var session = CreateWasapiSession(BassAudioPlayer.DeviceDriver.WASAPI_SHARED);
        session.MixerHandle = 123;
        var warnings = new List<string>();

        BassAudioPlayer.ApplyWasapiSharedDeviceVolume(
            new BassWasapiNegotiator(native),
            session,
            0.2f,
            warnings.Add);

        Assert.AreEqual(1, warnings.Count);
        StringAssert.Contains(warnings[0], "BASS_ERROR_HANDLE");
    }

    [TestMethod]
    public void WasapiInitialGain_UsesMuteStateWithoutChangingVolumeScale()
    {
        Assert.AreEqual(
            0.35f,
            BassAudioPlayer.GetEffectiveDeviceVolumeForInitialization(0.35f, isMuted: false));
        Assert.AreEqual(
            0f,
            BassAudioPlayer.GetEffectiveDeviceVolumeForInitialization(0.35f, isMuted: true));
    }

    [TestMethod]
    public void WasapiExclusive_DoesNotApplySharedMixerGain()
    {
        var native = new RecordingWasapiBoundary();
        native.InitializationResults.Enqueue(true);

        new BassWasapiNegotiator(native).Initialize(
            CreateWasapiRequest(BassAudioPlayer.DeviceDriver.WASAPI_EXCLUSIVE),
            CreateWasapiSession(BassAudioPlayer.DeviceDriver.WASAPI_EXCLUSIVE),
            WasapiCallback,
            initialGain: 0.2f,
            eventModeRequested: false);

        Assert.AreEqual(0, native.VolumeEffectCalls.Count);
    }

    [TestMethod]
    public void WasapiFailedInit_DoesNotClaimWasapiOwnershipAndCapturesWasapiError()
    {
        var native = new RecordingWasapiBoundary();
        native.InitializationResults.Enqueue(false);
        native.InitializationErrors.Enqueue(BASSError.BASS_ERROR_BUSY);
        native.InitializationResults.Enqueue(false);
        native.InitializationErrors.Enqueue(BASSError.BASS_ERROR_NOTAVAIL);
        var session = CreateWasapiSession(BassAudioPlayer.DeviceDriver.WASAPI_SHARED);

        AudioInitializationException exception = Assert.ThrowsException<AudioInitializationException>(
            () => new BassWasapiNegotiator(native).Initialize(
                CreateWasapiRequest(BassAudioPlayer.DeviceDriver.WASAPI_SHARED),
                session,
                WasapiCallback,
                initialGain: 0.4f,
                eventModeRequested: false));

        Assert.IsTrue(session.CoreInitialized);
        Assert.IsFalse(session.WasapiInitialized);
        Assert.AreEqual("BASSWASAPI", exception.NativeErrorSource);
        Assert.AreEqual(BASSError.BASS_ERROR_NOTAVAIL, exception.NativeErrorCode);
        Assert.AreEqual("BASS_WASAPI_Init", exception.Stage);
    }

    [TestMethod]
    public void WasapiSuccessfulInit_ClaimsOwnershipBeforeGetInfoReadback()
    {
        var session = CreateWasapiSession(BassAudioPlayer.DeviceDriver.WASAPI_SHARED);
        var native = new RecordingWasapiBoundary
        {
            GetInfoObserver = () => Assert.IsTrue(session.WasapiInitialized)
        };
        native.InitializationResults.Enqueue(true);

        new BassWasapiNegotiator(native).Initialize(
            CreateWasapiRequest(BassAudioPlayer.DeviceDriver.WASAPI_SHARED),
            session,
            WasapiCallback,
            initialGain: 0.4f,
            eventModeRequested: false);

        Assert.IsTrue(native.GetInfoObserved);
    }

    [TestMethod]
    public void WasapiExactSelection_DoesNotRequireDefaultFlag()
    {
        var native = new RecordingWasapiBoundary
        {
            DeviceInfo = CreateWasapiDevice(
                "Explicit WASAPI",
                "explicit-id",
                BASSWASAPIDeviceInfo.BASS_DEVICE_ENABLED)
        };
        native.InitializationResults.Enqueue(true);
        var request = CreateWasapiRequest(
            BassAudioPlayer.DeviceDriver.WASAPI_SHARED,
            device: new BassAudioPlayer.DeviceDescriptor("Explicit WASAPI", "explicit-id"));

        BassAudioBackendResult result = new BassWasapiNegotiator(native).Initialize(
            request,
            CreateWasapiSession(BassAudioPlayer.DeviceDriver.WASAPI_SHARED),
            WasapiCallback,
            initialGain: 0.4f,
            eventModeRequested: false);

        Assert.AreEqual(0, native.InitializationCalls.Single().DeviceIndex);
        Assert.AreEqual("Explicit WASAPI", result.ActualDevice.Name);
    }

    [TestMethod]
    public void WasapiDefaultWithoutDefaultFlag_ThrowsContextualInitializationFailure()
    {
        var native = new RecordingWasapiBoundary
        {
            DeviceInfo = CreateWasapiDevice(
                "Non-default WASAPI",
                "non-default-id",
                BASSWASAPIDeviceInfo.BASS_DEVICE_ENABLED)
        };
        var request = CreateWasapiRequest(BassAudioPlayer.DeviceDriver.WASAPI_SHARED);

        AudioInitializationException exception = Assert.ThrowsException<AudioInitializationException>(
            () => new BassWasapiNegotiator(native).Initialize(
                request,
                CreateWasapiSession(BassAudioPlayer.DeviceDriver.WASAPI_SHARED),
                WasapiCallback,
                initialGain: 0.4f,
                eventModeRequested: false));

        Assert.AreEqual(BassAudioPlayer.DeviceDriver.WASAPI_SHARED, exception.RequestedBackend);
        Assert.AreEqual(BassAudioPlayer.DeviceDriver.WASAPI_SHARED, exception.ActualBackend);
        Assert.AreEqual("BASS_WASAPI_GetDeviceInfos", exception.Stage);
        Assert.AreEqual("BASSWASAPI", exception.NativeErrorSource);
    }

    [TestMethod]
    public void DirectSoundExactSelection_FiltersInvalidEntriesAndUsesOriginalNativeIndex()
    {
        var native = new RecordingDirectSoundBoundary
        {
            Devices =
            [
                Device(0, "No sound", null!, enabled: true),
                Device(2, "Disabled", "disabled-driver", enabled: false),
                Device(5, "Missing driver", null!, enabled: true),
                Device(7, "Speakers", "speaker-driver", enabled: true)
            ],
            CurrentDeviceIndex = 7,
            ActualDeviceInfo = new BASS_DEVICEINFO
            {
                name = "Speakers",
                driver = "speaker-driver"
            }
        };
        var request = CreateDirectSoundRequest(
            new BassAudioPlayer.DeviceDescriptor("Speakers", "speaker-driver"));

        BassAudioBackendResult result = new BassDirectSoundNegotiator(native).Initialize(
            request,
            CreateDirectSoundSession(),
            DirectSoundCallback,
            initialGain: 0.4f);

        CollectionAssert.AreEqual(new[] { 7 }, native.InitializationIndices);
        Assert.AreEqual("Speakers", result.ActualDevice.Name);
        Assert.AreEqual("speaker-driver", result.ActualDevice.Driver);
    }

    [TestMethod]
    public void DirectSoundNameFallback_PreservesMatchedNativeIndex()
    {
        var native = CreateDirectSoundBoundary();
        var request = CreateDirectSoundRequest(
            new BassAudioPlayer.DeviceDescriptor("Speakers", "stale-driver"));

        BassAudioBackendResult result = new BassDirectSoundNegotiator(native).Initialize(
            request,
            CreateDirectSoundSession(),
            DirectSoundCallback,
            initialGain: 0.4f);

        CollectionAssert.AreEqual(new[] { 4 }, native.InitializationIndices);
        StringAssert.Contains(result.FallbackReason, "compatible name match");
    }

    [TestMethod]
    public void DirectSoundDefault_InitializesMinusOneAndReadsBackActualDescriptor()
    {
        var native = CreateDirectSoundBoundary();
        native.Devices =
        [
            Device(4, "Selectable but not default", "speaker-driver", enabled: true)
        ];
        native.CurrentDeviceIndex = 9;
        native.ActualDeviceInfo = new BASS_DEVICEINFO
        {
            name = "Current Default",
            driver = "current-default-driver"
        };

        BassAudioBackendResult result = new BassDirectSoundNegotiator(native).Initialize(
            CreateDirectSoundRequest(default),
            CreateDirectSoundSession(),
            DirectSoundCallback,
            initialGain: 0.4f);

        CollectionAssert.AreEqual(new[] { -1 }, native.InitializationIndices);
        Assert.AreEqual(9, native.ReadbackDeviceIndices.Single());
        Assert.AreEqual("Current Default", result.ActualDevice.Name);
        Assert.AreEqual("current-default-driver", result.ActualDevice.Driver);
    }

    [TestMethod]
    public void DirectSound_AppliesMixerGainBeforePlaybackAndReusesOwnedEffect()
    {
        var native = CreateDirectSoundBoundary();
        var session = CreateDirectSoundSession();

        new BassDirectSoundNegotiator(native).Initialize(
            CreateDirectSoundRequest(default),
            session,
            DirectSoundCallback,
            initialGain: 0.35f);

        CollectionAssert.AreEqual(
            new[]
            {
                "CreateMixer",
                "CreateVolumeEffect",
                "SetVolumeEffect",
                "CreateOutputStream",
                "Play"
            },
            native.GraphCalls);
        CollectionAssert.AreEqual(
            new[] { (789, 0.35f) },
            native.VolumeEffectCalls);

        Assert.IsTrue(
            new BassDirectSoundNegotiator(native).TrySetMixerGain(
                session,
                0.15f,
                out BASSError error));
        Assert.AreEqual(BASSError.BASS_OK, error);
        CollectionAssert.AreEqual(
            new[] { (789, 0.35f), (789, 0.15f) },
            native.VolumeEffectCalls);
    }

    [TestMethod]
    public void DirectSound_VolumeEffectCreationFailureReportsContextAndRetainsOwnership()
    {
        var native = CreateDirectSoundBoundary();
        native.CreateVolumeEffectResult = 0;
        native.CoreError = BASSError.BASS_ERROR_HANDLE;
        var session = CreateDirectSoundSession();

        AudioInitializationException exception = Assert.ThrowsException<AudioInitializationException>(
            () => new BassDirectSoundNegotiator(native).Initialize(
                CreateDirectSoundRequest(default),
                session,
                DirectSoundCallback,
                initialGain: 0.4f));

        Assert.AreEqual("BASS_ChannelSetFX(BASS_FX_BFX_VOLUME)", exception.Stage);
        Assert.AreEqual("BASS", exception.NativeErrorSource);
        Assert.AreEqual(BASSError.BASS_ERROR_HANDLE, exception.NativeErrorCode);
        Assert.IsTrue(session.CoreInitialized);
        Assert.AreEqual(123, session.MixerHandle);
        Assert.AreEqual(0, session.VolumeEffectHandle);
        Assert.AreEqual(0, session.OutputHandle);
        Assert.IsFalse(session.IsStarted);
        CollectionAssert.AreEqual(
            new[] { "CreateMixer", "CreateVolumeEffect" },
            native.GraphCalls);
    }

    [TestMethod]
    public void DirectSound_VolumeEffectParameterFailureReportsContextAndRetainsOwnership()
    {
        var native = CreateDirectSoundBoundary();
        native.SetVolumeEffectResult = false;
        native.CoreError = BASSError.BASS_ERROR_HANDLE;
        var session = CreateDirectSoundSession();

        AudioInitializationException exception = Assert.ThrowsException<AudioInitializationException>(
            () => new BassDirectSoundNegotiator(native).Initialize(
                CreateDirectSoundRequest(default),
                session,
                DirectSoundCallback,
                initialGain: 0.4f));

        Assert.AreEqual("BASS_FXSetParameters(BASS_FX_BFX_VOLUME)", exception.Stage);
        Assert.AreEqual("BASS", exception.NativeErrorSource);
        Assert.AreEqual(BASSError.BASS_ERROR_HANDLE, exception.NativeErrorCode);
        Assert.IsTrue(session.CoreInitialized);
        Assert.AreEqual(123, session.MixerHandle);
        Assert.AreEqual(789, session.VolumeEffectHandle);
        Assert.AreEqual(0, session.OutputHandle);
        Assert.IsFalse(session.IsStarted);
        CollectionAssert.AreEqual(
            new[] { "CreateMixer", "CreateVolumeEffect", "SetVolumeEffect" },
            native.GraphCalls);
    }

    private static BassAudioNegotiationRequest CreateWasapiRequest(
        BassAudioPlayer.DeviceDriver backend,
        SampleRate rate = SampleRate.AUTO,
        BassAudioPlayer.DeviceDescriptor device = default) =>
        new(backend, device, rate, SampleFormat.AUTO, 20f);

    private static BassAudioSession CreateWasapiSession(BassAudioPlayer.DeviceDriver backend) =>
        new(backend) { ActualBackend = backend };

    private static BassAudioNegotiationRequest CreateDirectSoundRequest(
        BassAudioPlayer.DeviceDescriptor descriptor) =>
        new(
            BassAudioPlayer.DeviceDriver.DIRECT_SOUND,
            descriptor,
            SampleRate.AUTO,
            SampleFormat.AUTO,
            20f);

    private static BassAudioSession CreateDirectSoundSession() =>
        new(BassAudioPlayer.DeviceDriver.DIRECT_SOUND)
        {
            ActualBackend = BassAudioPlayer.DeviceDriver.DIRECT_SOUND
        };

    private static BASS_WASAPI_DEVICEINFO CreateWasapiDevice(
        string name,
        string id,
        BASSWASAPIDeviceInfo flags,
        int mixRate = 48000,
        int mixChannels = 2) =>
        new()
        {
            name = name,
            id = id,
            flags = flags,
            mixfreq = mixRate,
            mixchans = mixChannels,
            minperiod = 0.005f
        };

    private static BassDirectSoundDevice Device(
        int nativeIndex,
        string name,
        string driver,
        bool enabled,
        bool isDefault = false) =>
        new(
            nativeIndex,
            new BassAudioPlayer.DeviceDescriptor(name, driver),
            enabled,
            isDefault);

    private static RecordingDirectSoundBoundary CreateDirectSoundBoundary() =>
        new()
        {
            Devices =
            [
                Device(0, "No sound", null!, enabled: true),
                Device(4, "Speakers", "speaker-driver", enabled: true, isDefault: true)
            ],
            CurrentDeviceIndex = 4,
            ActualDeviceInfo = new BASS_DEVICEINFO
            {
                name = "Speakers",
                driver = "speaker-driver"
            }
        };

    private sealed class RecordingWasapiBoundary : IWasapiNegotiationNativeBoundary
    {
        private BASSError wasapiError;

        internal Queue<bool> InitializationResults { get; } = new();

        internal Queue<BASSError> InitializationErrors { get; } = new();

        internal List<WasapiInitializationCall> InitializationCalls { get; } = [];

        internal List<string> GraphCalls { get; } = [];

        internal List<(int EffectHandle, float Volume)> VolumeEffectCalls { get; } = [];

        internal BASS_WASAPI_DEVICEINFO DeviceInfo { get; set; } = CreateWasapiDevice(
            "WASAPI Device",
            "wasapi-id",
            BASSWASAPIDeviceInfo.BASS_DEVICE_ENABLED | BASSWASAPIDeviceInfo.BASS_DEVICE_DEFAULT);

        internal BASS_WASAPI_INFO WasapiInfo { get; set; } = new()
        {
            freq = 48000,
            chans = 2,
            format = BASSWASAPIFormat.BASS_WASAPI_FORMAT_FLOAT,
            buflen = 3840
        };

        internal int MixerRate { get; private set; }

        internal int MixerChannels { get; private set; }

        internal BASSFlag MixerFlags { get; private set; }

        internal Action? GetInfoObserver { get; set; }

        internal bool GetInfoObserved { get; private set; }

        internal bool SetVolumeEffectResult { get; set; } = true;

        internal int CreateVolumeEffectResult { get; set; } = 234;

        internal BASSError CoreError { get; set; } = BASSError.BASS_OK;

        internal Action? StartObserver { get; set; }

        public bool InitializeCore() => true;

        public int GetCoreDevice() => 0;

        public void DisableCoreUpdatePeriod()
        {
        }

        public BASS_WASAPI_DEVICEINFO[] GetDeviceInfos() => [DeviceInfo];

        public BASS_WASAPI_DEVICEINFO GetDeviceInfo(int deviceIndex) => DeviceInfo;

        public bool InitializeWasapi(
            int deviceIndex,
            int rate,
            int channels,
            BASSWASAPIInit flags,
            float bufferSeconds,
            float periodSeconds,
            WASAPIPROC callback)
        {
            InitializationCalls.Add(new WasapiInitializationCall(
                deviceIndex,
                rate,
                channels,
                flags,
                bufferSeconds,
                periodSeconds));
            bool result = InitializationResults.Count == 0 || InitializationResults.Dequeue();
            wasapiError = result || InitializationErrors.Count == 0
                ? BASSError.BASS_OK
                : InitializationErrors.Dequeue();
            return result;
        }

        public int GetWasapiDevice() => 0;

        public BASS_WASAPI_INFO GetWasapiInfo()
        {
            GetInfoObserver?.Invoke();
            GetInfoObserved = true;
            return WasapiInfo;
        }

        public int CreateMixer(int rate, int channels, BASSFlag flags)
        {
            GraphCalls.Add("CreateMixer");
            MixerRate = rate;
            MixerChannels = channels;
            MixerFlags = flags;
            return 123;
        }

        public int CreateVolumeEffect(int mixerHandle)
        {
            GraphCalls.Add("CreateVolumeEffect");
            return CreateVolumeEffectResult;
        }

        public bool SetVolumeEffect(int effectHandle, float volume)
        {
            GraphCalls.Add("SetVolumeEffect");
            VolumeEffectCalls.Add((effectHandle, volume));
            return SetVolumeEffectResult;
        }

        public bool StartWasapi()
        {
            GraphCalls.Add("StartWasapi");
            StartObserver?.Invoke();
            return true;
        }

        public BASSError GetCoreError() => CoreError;

        public BASSError GetWasapiError() => wasapiError;
    }

    private sealed class RecordingDirectSoundBoundary : IDirectSoundNegotiationNativeBoundary
    {
        private readonly Dictionary<BASSConfig, int> config = new()
        {
            [BASSConfig.BASS_CONFIG_UPDATEPERIOD] = 5,
            [BASSConfig.BASS_CONFIG_BUFFER] = 100
        };

        internal IReadOnlyList<BassDirectSoundDevice> Devices { get; set; } = [];

        internal List<int> InitializationIndices { get; } = [];

        internal List<int> ReadbackDeviceIndices { get; } = [];

        internal int CurrentDeviceIndex { get; set; } = 4;

        internal List<string> GraphCalls { get; } = [];

        internal List<(int EffectHandle, float Volume)> VolumeEffectCalls { get; } = [];

        internal bool SetVolumeEffectResult { get; set; } = true;

        internal int CreateVolumeEffectResult { get; set; } = 789;

        internal BASSError CoreError { get; set; } = BASSError.BASS_OK;

        internal BASS_DEVICEINFO ActualDeviceInfo { get; set; } = new();

        public IReadOnlyList<BassDirectSoundDevice> GetDevices() => Devices;

        public bool InitializeCore(int deviceIndex, int rate)
        {
            InitializationIndices.Add(deviceIndex);
            return true;
        }

        public int GetCoreDevice() => CurrentDeviceIndex;

        public BASS_DEVICEINFO GetDeviceInfo(int deviceIndex)
        {
            ReadbackDeviceIndices.Add(deviceIndex);
            return ActualDeviceInfo;
        }

        public BASS_INFO GetInfo() => new() { freq = 48000 };

        public bool SetConfig(BASSConfig option, int value)
        {
            config[option] = value;
            return true;
        }

        public int GetConfig(BASSConfig option) => config[option];

        public int CreateMixer(int rate, int channels, BASSFlag flags)
        {
            GraphCalls.Add("CreateMixer");
            return 123;
        }

        public int CreateOutputStream(int rate, int channels, BASSFlag flags, STREAMPROC callback)
        {
            GraphCalls.Add("CreateOutputStream");
            return 456;
        }

        public bool Play(int streamHandle)
        {
            GraphCalls.Add("Play");
            return true;
        }

        public int CreateVolumeEffect(int mixerHandle)
        {
            GraphCalls.Add("CreateVolumeEffect");
            return CreateVolumeEffectResult;
        }

        public bool SetVolumeEffect(int effectHandle, float volume)
        {
            GraphCalls.Add("SetVolumeEffect");
            VolumeEffectCalls.Add((effectHandle, volume));
            return SetVolumeEffectResult;
        }

        public BASSError GetCoreError() => CoreError;
    }

    private sealed record WasapiInitializationCall(
        int DeviceIndex,
        int Rate,
        int Channels,
        BASSWASAPIInit Flags,
        float BufferSeconds,
        float PeriodSeconds);
}
