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

    [TestMethod]
    public void WasapiCandidates_UseNativeDefaultsForSharedAndRetainExclusiveDegradation()
    {
        IReadOnlyList<BassWasapiInitializationCandidate> shared =
            BassWasapiNegotiator.GetInitializationCandidates(
                shared: true,
                eventModeRequested: true,
                requestedBufferSeconds: 0.02f,
                requestedPeriodSeconds: 0.005f);

        Assert.AreEqual(2, shared.Count);
        Assert.IsTrue(shared[0].EventDriven);
        Assert.IsFalse(shared[1].EventDriven);
        Assert.IsTrue(shared.All(candidate => candidate.BufferSeconds == 0f));
        Assert.IsTrue(shared.All(candidate => candidate.PeriodSeconds == 0f));

        IReadOnlyList<BassWasapiInitializationCandidate> exclusive =
            BassWasapiNegotiator.GetInitializationCandidates(
                shared: false,
                eventModeRequested: true,
                requestedBufferSeconds: 0.02f,
                requestedPeriodSeconds: 0.005f);

        Assert.AreEqual(3, exclusive.Count);
        Assert.IsTrue(exclusive[0].EventDriven);
        Assert.IsFalse(exclusive[1].EventDriven);
        Assert.AreEqual(0.02f, exclusive[1].BufferSeconds);
        Assert.AreEqual(0.005f, exclusive[1].PeriodSeconds);
        Assert.AreEqual(0f, exclusive[2].BufferSeconds);
        Assert.AreEqual(0f, exclusive[2].PeriodSeconds);
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
        Assert.IsTrue(native.InitializationCalls.All(call =>
            call.Kind == WasapiInitializationKind.ExclusiveFormatted));
        Assert.IsTrue(native.InitializationCalls.All(call =>
            call.Flags.HasFlag(BASSWASAPIInit.BASS_WASAPI_EXCLUSIVE)
            && call.Flags.HasFlag(BASSWASAPIInit.BASS_WASAPI_AUTOFORMAT)));
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
        Assert.AreEqual(WasapiInitializationKind.SharedNoFormat, call.Kind);
        Assert.AreEqual(0, (int)call.Flags);
        Assert.AreEqual(0f, call.BufferSeconds);
        Assert.AreEqual(0f, call.PeriodSeconds);
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
        native.SetVolumeEffectObserver = () => Assert.AreEqual(0, session.CallbackOutputHandle);
        native.StartObserver = () => Assert.AreEqual(123, session.CallbackOutputHandle);

        new BassWasapiNegotiator(native).Initialize(
            CreateWasapiRequest(BassAudioPlayer.DeviceDriver.WASAPI_SHARED),
            session,
            WasapiCallback,
            initialGain: 0.35f,
            eventModeRequested: false);

        CollectionAssert.AreEqual(
            new[]
            {
                "CreateMixer",
                "CreateVolumeEffect",
                "SetVolumeEffect",
                "StartWasapi"
            },
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
        Assert.AreEqual(0, session.CallbackOutputHandle);
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
                eventModeRequested: true));

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
    public void WasapiShared_EventFailureFallsBackToNonEventNativeDefaults()
    {
        var native = new RecordingWasapiBoundary();
        native.InitializationResults.Enqueue(false);
        native.InitializationErrors.Enqueue(BASSError.BASS_ERROR_BUSY);
        native.InitializationResults.Enqueue(true);
        var session = CreateWasapiSession(BassAudioPlayer.DeviceDriver.WASAPI_SHARED);

        BassAudioBackendResult result = new BassWasapiNegotiator(native).Initialize(
            CreateWasapiRequest(BassAudioPlayer.DeviceDriver.WASAPI_SHARED),
            session,
            WasapiCallback,
            initialGain: 0.35f,
            eventModeRequested: true);

        Assert.AreEqual(2, native.InitializationCalls.Count);
        Assert.IsTrue(native.InitializationCalls.All(call =>
            call.Kind == WasapiInitializationKind.SharedNoFormat));
        Assert.IsTrue(native.InitializationCalls[0].Flags.HasFlag(BASSWASAPIInit.BASS_WASAPI_EVENT));
        Assert.IsFalse(native.InitializationCalls[1].Flags.HasFlag(BASSWASAPIInit.BASS_WASAPI_EVENT));
        Assert.IsTrue(native.InitializationCalls.All(call =>
            !call.Flags.HasFlag(BASSWASAPIInit.BASS_WASAPI_EXCLUSIVE)
            && !call.Flags.HasFlag(BASSWASAPIInit.BASS_WASAPI_AUTOFORMAT)));
        Assert.IsTrue(native.InitializationCalls.All(call =>
            call.BufferSeconds == 0f && call.PeriodSeconds == 0f));
        Assert.AreEqual((234, 0.35f), native.VolumeEffectCalls.Single());
        StringAssert.Contains(result.FallbackReason, "nativeErrorCode=BASS_ERROR_BUSY");
        StringAssert.Contains(result.FallbackReason, "shared non-event native default");
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
    public void WasapiStableIdentitySelection_IgnoresChangedDisplayName()
    {
        var native = new RecordingWasapiBoundary
        {
            DeviceInfos =
            [
                CreateWasapiDevice(
                    "Renamed WASAPI",
                    "stable-id",
                    BASSWASAPIDeviceInfo.BASS_DEVICE_ENABLED),
                CreateWasapiDevice(
                    "Default WASAPI",
                    "default-id",
                    BASSWASAPIDeviceInfo.BASS_DEVICE_ENABLED | BASSWASAPIDeviceInfo.BASS_DEVICE_DEFAULT)
            ]
        };
        native.InitializationResults.Enqueue(true);

        BassAudioBackendResult result = new BassWasapiNegotiator(native).Initialize(
            CreateWasapiRequest(
                BassAudioPlayer.DeviceDriver.WASAPI_SHARED,
                device: new BassAudioPlayer.DeviceDescriptor("Old WASAPI Name", "stable-id")),
            CreateWasapiSession(BassAudioPlayer.DeviceDriver.WASAPI_SHARED),
            WasapiCallback,
            initialGain: 0.4f,
            eventModeRequested: false);

        Assert.AreEqual(0, native.InitializationCalls.Single().DeviceIndex);
        Assert.AreEqual("Renamed WASAPI", result.ActualDevice.Name);
        Assert.IsTrue(string.IsNullOrWhiteSpace(result.FallbackReason));
    }

    [TestMethod]
    public void WasapiStaleStableIdentity_DoesNotSelectSameNameOnAnotherEndpoint()
    {
        var native = new RecordingWasapiBoundary
        {
            DeviceInfos =
            [
                CreateWasapiDevice(
                    "Speakers",
                    "different-id",
                    BASSWASAPIDeviceInfo.BASS_DEVICE_ENABLED),
                CreateWasapiDevice(
                    "Default WASAPI",
                    "default-id",
                    BASSWASAPIDeviceInfo.BASS_DEVICE_ENABLED | BASSWASAPIDeviceInfo.BASS_DEVICE_DEFAULT)
            ]
        };
        native.InitializationResults.Enqueue(true);

        BassAudioBackendResult result = new BassWasapiNegotiator(native).Initialize(
            CreateWasapiRequest(
                BassAudioPlayer.DeviceDriver.WASAPI_SHARED,
                device: new BassAudioPlayer.DeviceDescriptor("Speakers", "stale-id")),
            CreateWasapiSession(BassAudioPlayer.DeviceDriver.WASAPI_SHARED),
            WasapiCallback,
            initialGain: 0.4f,
            eventModeRequested: false);

        Assert.AreEqual(1, native.InitializationCalls.Single().DeviceIndex);
        Assert.AreEqual("default-id", result.ActualDevice.Driver);
        StringAssert.Contains(result.FallbackReason, "unavailable");
    }

    [TestMethod]
    public void WasapiLegacyNameOnlySelection_UsesNameFallback()
    {
        var native = new RecordingWasapiBoundary
        {
            DeviceInfos =
            [
                CreateWasapiDevice(
                    "Legacy Speakers",
                    "legacy-id",
                    BASSWASAPIDeviceInfo.BASS_DEVICE_ENABLED),
                CreateWasapiDevice(
                    "Default WASAPI",
                    "default-id",
                    BASSWASAPIDeviceInfo.BASS_DEVICE_ENABLED | BASSWASAPIDeviceInfo.BASS_DEVICE_DEFAULT)
            ]
        };
        native.InitializationResults.Enqueue(true);

        BassAudioBackendResult result = new BassWasapiNegotiator(native).Initialize(
            CreateWasapiRequest(
                BassAudioPlayer.DeviceDriver.WASAPI_SHARED,
                device: new BassAudioPlayer.DeviceDescriptor("Legacy Speakers", null)),
            CreateWasapiSession(BassAudioPlayer.DeviceDriver.WASAPI_SHARED),
            WasapiCallback,
            initialGain: 0.4f,
            eventModeRequested: false);

        Assert.AreEqual(0, native.InitializationCalls.Single().DeviceIndex);
        Assert.AreEqual("legacy-id", result.ActualDevice.Driver);
        StringAssert.Contains(result.FallbackReason, "no stable identity");
    }

    [TestMethod]
    public void WasapiExclusive_StaleIdentityRetainsCompatibleNameFallback()
    {
        var native = new RecordingWasapiBoundary
        {
            DeviceInfos =
            [
                CreateWasapiDevice(
                    "Exclusive Speakers",
                    "replacement-id",
                    BASSWASAPIDeviceInfo.BASS_DEVICE_ENABLED),
                CreateWasapiDevice(
                    "Default WASAPI",
                    "default-id",
                    BASSWASAPIDeviceInfo.BASS_DEVICE_ENABLED | BASSWASAPIDeviceInfo.BASS_DEVICE_DEFAULT)
            ]
        };
        native.InitializationResults.Enqueue(true);

        BassAudioBackendResult result = new BassWasapiNegotiator(native).Initialize(
            CreateWasapiRequest(
                BassAudioPlayer.DeviceDriver.WASAPI_EXCLUSIVE,
                device: new BassAudioPlayer.DeviceDescriptor("Exclusive Speakers", "stale-id")),
            CreateWasapiSession(BassAudioPlayer.DeviceDriver.WASAPI_EXCLUSIVE),
            WasapiCallback,
            initialGain: 0.4f,
            eventModeRequested: false);

        Assert.AreEqual(0, native.InitializationCalls.Single().DeviceIndex);
        Assert.AreEqual("replacement-id", result.ActualDevice.Driver);
        StringAssert.Contains(result.FallbackReason, "compatible name match");
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

    private static BassAudioNegotiationRequest CreateWasapiRequest(
        BassAudioPlayer.DeviceDriver backend,
        SampleRate rate = SampleRate.AUTO,
        BassAudioPlayer.DeviceDescriptor device = default) =>
        new(backend, device, rate, SampleFormat.AUTO, 20f);

    private static BassAudioSession CreateWasapiSession(BassAudioPlayer.DeviceDriver backend) =>
        new(backend) { ActualBackend = backend };

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

        internal BASS_WASAPI_DEVICEINFO[]? DeviceInfos { get; set; }

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

        internal Action? SetVolumeEffectObserver { get; set; }

        internal int CreateVolumeEffectResult { get; set; } = 234;

        internal BASSError CoreError { get; set; } = BASSError.BASS_OK;

        internal Action? StartObserver { get; set; }

        public bool InitializeCore() => true;

        public int GetCoreDevice() => 0;

        public void DisableCoreUpdatePeriod()
        {
        }

        public BASS_WASAPI_DEVICEINFO[] GetDeviceInfos() => DeviceInfos ?? [DeviceInfo];

        public BASS_WASAPI_DEVICEINFO GetDeviceInfo(int deviceIndex) =>
            DeviceInfos == null ? DeviceInfo : DeviceInfos[deviceIndex];

        public bool InitializeSharedWasapi(
            int deviceIndex,
            int rate,
            int channels,
            bool eventDriven,
            WASAPIPROC callback) =>
            RecordInitialization(
                WasapiInitializationKind.SharedNoFormat,
                deviceIndex,
                rate,
                channels,
                eventDriven ? BASSWASAPIInit.BASS_WASAPI_EVENT : 0,
                0f,
                0f);

        public bool InitializeExclusiveWasapi(
            int deviceIndex,
            int rate,
            int channels,
            BASSWASAPIInit flags,
            float bufferSeconds,
            float periodSeconds,
            WASAPIPROC callback) =>
            RecordInitialization(
                WasapiInitializationKind.ExclusiveFormatted,
                deviceIndex,
                rate,
                channels,
                flags,
                bufferSeconds,
                periodSeconds);

        private bool RecordInitialization(
            WasapiInitializationKind kind,
            int deviceIndex,
            int rate,
            int channels,
            BASSWASAPIInit flags,
            float bufferSeconds,
            float periodSeconds)
        {
            InitializationCalls.Add(new WasapiInitializationCall(
                kind,
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
            SetVolumeEffectObserver?.Invoke();
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

    private enum WasapiInitializationKind
    {
        SharedNoFormat,
        ExclusiveFormatted
    }

    private sealed record WasapiInitializationCall(
        WasapiInitializationKind Kind,
        int DeviceIndex,
        int Rate,
        int Channels,
        BASSWASAPIInit Flags,
        float BufferSeconds,
        float PeriodSeconds);
}
