using System;
using System.Collections.Generic;
using System.Linq;
using ManagedBass;
using ManagedBass.Wasapi;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Ribbit.Media;
using Ribbit.Media.Audio;

namespace BeMusicSeeker.Tests;

[TestClass]
public sealed class BassWasapiNegotiationTests
{
    private static readonly WasapiProcedure WasapiCallback = (buffer, length, user) => length;

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
        native.InitializationErrors.Enqueue(Errors.Busy);
        native.InitializationResults.Enqueue(false);
        native.InitializationErrors.Enqueue(Errors.SampleFormat);
        native.InitializationResults.Enqueue(true);
        BassAudioSession session = CreateWasapiSession(BassAudioPlayer.DeviceDriver.WASAPI_EXCLUSIVE);

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
            call.Flags.HasFlag(WasapiInitFlags.Exclusive)
            && call.Flags.HasFlag(WasapiInitFlags.AutoFormat)));
        Assert.IsTrue(native.InitializationCalls[0].Flags.HasFlag(WasapiInitFlags.EventDriven));
        Assert.IsFalse(native.InitializationCalls[1].Flags.HasFlag(WasapiInitFlags.EventDriven));
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
                isDefault: true,
                mixRate: 48000,
                mixChannels: 6),
            WasapiInfo = new BassWasapiInfoSnapshot(96000, 4, WasapiFormat.Float, 7680)
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
        Assert.IsTrue(native.MixerFlags.HasFlag(BassFlags.Float));
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
        BassAudioSession session = CreateWasapiSession(BassAudioPlayer.DeviceDriver.WASAPI_SHARED);
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
            CoreError = Errors.Handle
        };
        BassAudioSession session = CreateWasapiSession(BassAudioPlayer.DeviceDriver.WASAPI_SHARED);
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
        Assert.AreEqual(Errors.Handle, exception.NativeErrorCode);
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
            CoreError = Errors.Handle
        };
        BassAudioSession session = CreateWasapiSession(BassAudioPlayer.DeviceDriver.WASAPI_SHARED);
        native.InitializationResults.Enqueue(true);

        AudioInitializationException exception = Assert.ThrowsException<AudioInitializationException>(
            () => new BassWasapiNegotiator(native).Initialize(
                CreateWasapiRequest(BassAudioPlayer.DeviceDriver.WASAPI_SHARED),
                session,
                WasapiCallback,
                initialGain: 0.35f,
                eventModeRequested: false));

        Assert.AreEqual("BASS_ChannelSetFX(BASS_FX_BFX_VOLUME)", exception.Stage);
        Assert.AreEqual(Errors.Handle, exception.NativeErrorCode);
        CollectionAssert.AreEqual(
            new[] { "CreateMixer", "CreateVolumeEffect" },
            native.GraphCalls);
    }

    [TestMethod]
    public void WasapiShared_DynamicGainUsesTheSameMixerBoundary()
    {
        var native = new RecordingWasapiBoundary();
        BassAudioSession session = CreateWasapiSession(BassAudioPlayer.DeviceDriver.WASAPI_SHARED);
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
            CoreError = Errors.Handle
        };
        BassAudioSession session = CreateWasapiSession(BassAudioPlayer.DeviceDriver.WASAPI_SHARED);
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
        native.InitializationErrors.Enqueue(Errors.Busy);
        native.InitializationResults.Enqueue(false);
        native.InitializationErrors.Enqueue(Errors.NotAvailable);
        BassAudioSession session = CreateWasapiSession(BassAudioPlayer.DeviceDriver.WASAPI_SHARED);

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
        Assert.AreEqual(Errors.NotAvailable, exception.NativeErrorCode);
        Assert.AreEqual("BASS_WASAPI_Init", exception.Stage);
    }

    [TestMethod]
    public void WasapiSuccessfulInit_ClaimsOwnershipBeforeGetInfoReadback()
    {
        BassAudioSession session = CreateWasapiSession(BassAudioPlayer.DeviceDriver.WASAPI_SHARED);
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
    public void WasapiInfoFailure_ReportsBoundaryErrorAfterClaimingOwnership()
    {
        BassAudioSession session = CreateWasapiSession(BassAudioPlayer.DeviceDriver.WASAPI_SHARED);
        var native = new RecordingWasapiBoundary
        {
            GetWasapiInfoResult = false,
            WasapiInfoError = Errors.Init
        };
        native.InitializationResults.Enqueue(true);

        AudioInitializationException exception = Assert.ThrowsException<AudioInitializationException>(
            () => new BassWasapiNegotiator(native).Initialize(
                CreateWasapiRequest(BassAudioPlayer.DeviceDriver.WASAPI_SHARED),
                session,
                WasapiCallback,
                initialGain: 0.4f,
                eventModeRequested: false));

        Assert.IsTrue(session.CoreInitialized);
        Assert.IsTrue(session.WasapiInitialized);
        Assert.AreEqual("BASS_WASAPI_GetInfo", exception.Stage);
        Assert.AreEqual("BASSWASAPI", exception.NativeErrorSource);
        Assert.AreEqual(Errors.Init, exception.NativeErrorCode);
    }

    [TestMethod]
    public void WasapiShared_EventFailureFallsBackToNonEventNativeDefaults()
    {
        var native = new RecordingWasapiBoundary();
        native.InitializationResults.Enqueue(false);
        native.InitializationErrors.Enqueue(Errors.Busy);
        native.InitializationResults.Enqueue(true);
        BassAudioSession session = CreateWasapiSession(BassAudioPlayer.DeviceDriver.WASAPI_SHARED);

        BassAudioBackendResult result = new BassWasapiNegotiator(native).Initialize(
            CreateWasapiRequest(BassAudioPlayer.DeviceDriver.WASAPI_SHARED),
            session,
            WasapiCallback,
            initialGain: 0.35f,
            eventModeRequested: true);

        Assert.AreEqual(2, native.InitializationCalls.Count);
        Assert.IsTrue(native.InitializationCalls.All(call =>
            call.Kind == WasapiInitializationKind.SharedNoFormat));
        Assert.IsTrue(native.InitializationCalls[0].Flags.HasFlag(WasapiInitFlags.EventDriven));
        Assert.IsFalse(native.InitializationCalls[1].Flags.HasFlag(WasapiInitFlags.EventDriven));
        Assert.IsTrue(native.InitializationCalls.All(call =>
            !call.Flags.HasFlag(WasapiInitFlags.Exclusive)
            && !call.Flags.HasFlag(WasapiInitFlags.AutoFormat)));
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
                "explicit-id")
        };
        native.InitializationResults.Enqueue(true);
        BassAudioNegotiationRequest request = CreateWasapiRequest(
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
                    "stable-id"),
                CreateWasapiDevice(
                    "Default WASAPI",
                    "default-id",
                    isDefault: true)
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
                    "different-id"),
                CreateWasapiDevice(
                    "Default WASAPI",
                    "default-id",
                    isDefault: true)
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
                    "legacy-id"),
                CreateWasapiDevice(
                    "Default WASAPI",
                    "default-id",
                    isDefault: true)
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
                    "replacement-id"),
                CreateWasapiDevice(
                    "Default WASAPI",
                    "default-id",
                    isDefault: true)
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
                "non-default-id")
        };
        BassAudioNegotiationRequest request = CreateWasapiRequest(BassAudioPlayer.DeviceDriver.WASAPI_SHARED);

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

    private static BassWasapiDeviceSnapshot CreateWasapiDevice(
        string name,
        string id,
        bool isDefault = false,
        int mixRate = 48000,
        int mixChannels = 2) =>
        new(
            name,
            id,
            isDefault,
            IsEnabled: true,
            IsInput: false,
            IsLoopback: false,
            IsUnplugged: false,
            MinimumUpdatePeriod: 0.005d,
            MixFrequency: mixRate,
            MixChannels: mixChannels);

    private sealed class RecordingWasapiBoundary : IWasapiNegotiationNativeBoundary
    {
        private Errors wasapiError;

        internal Queue<bool> InitializationResults { get; } = new();

        internal Queue<Errors> InitializationErrors { get; } = new();

        internal List<WasapiInitializationCall> InitializationCalls { get; } = [];

        internal List<string> GraphCalls { get; } = [];

        internal List<(int EffectHandle, float Volume)> VolumeEffectCalls { get; } = [];

        internal BassWasapiDeviceSnapshot DeviceInfo { get; set; } = CreateWasapiDevice(
            "WASAPI Device",
            "wasapi-id",
            isDefault: true);

        internal BassWasapiDeviceSnapshot[]? DeviceInfos { get; set; }

        internal bool GetDeviceInfosResult { get; set; } = true;

        internal Errors DeviceInfosError { get; set; } = Errors.Device;

        internal bool GetDeviceInfoResult { get; set; } = true;

        internal Errors DeviceInfoError { get; set; } = Errors.Device;

        internal BassWasapiInfoSnapshot WasapiInfo { get; set; } =
            new(48000, 2, WasapiFormat.Float, 3840);

        internal bool GetWasapiInfoResult { get; set; } = true;

        internal Errors WasapiInfoError { get; set; } = Errors.Init;

        internal int MixerRate { get; private set; }

        internal int MixerChannels { get; private set; }

        internal BassFlags MixerFlags { get; private set; }

        internal Action? GetInfoObserver { get; set; }

        internal bool GetInfoObserved { get; private set; }

        internal bool SetVolumeEffectResult { get; set; } = true;

        internal Action? SetVolumeEffectObserver { get; set; }

        internal int CreateVolumeEffectResult { get; set; } = 234;

        internal Errors CoreError { get; set; } = Errors.OK;

        internal Action? StartObserver { get; set; }

        public bool InitializeCore() => true;

        public int GetCoreDevice() => 0;

        public void DisableCoreUpdatePeriod()
        {
        }

        public bool TryGetDeviceInfos(
            out BassWasapiDeviceSnapshot[] deviceInfos,
            out Errors error)
        {
            deviceInfos = DeviceInfos ?? [DeviceInfo];
            error = GetDeviceInfosResult ? Errors.OK : DeviceInfosError;
            return GetDeviceInfosResult;
        }

        public bool TryGetDeviceInfo(
            int deviceIndex,
            out BassWasapiDeviceSnapshot deviceInfo,
            out Errors error)
        {
            deviceInfo = DeviceInfos == null ? DeviceInfo : DeviceInfos[deviceIndex];
            error = GetDeviceInfoResult ? Errors.OK : DeviceInfoError;
            return GetDeviceInfoResult;
        }

        public bool InitializeSharedWasapi(
            int deviceIndex,
            int rate,
            int channels,
            bool eventDriven,
            WasapiProcedure callback) =>
            RecordInitialization(
                WasapiInitializationKind.SharedNoFormat,
                deviceIndex,
                rate,
                channels,
                eventDriven ? WasapiInitFlags.EventDriven : WasapiInitFlags.Shared,
                0f,
                0f);

        public bool InitializeExclusiveWasapi(
            int deviceIndex,
            int rate,
            int channels,
            WasapiInitFlags flags,
            float bufferSeconds,
            float periodSeconds,
            WasapiProcedure callback) =>
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
            WasapiInitFlags flags,
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
                ? Errors.OK
                : InitializationErrors.Dequeue();
            return result;
        }

        public int GetWasapiDevice() => 0;

        public bool TryGetWasapiInfo(
            out BassWasapiInfoSnapshot info,
            out Errors error)
        {
            GetInfoObserver?.Invoke();
            GetInfoObserved = true;
            info = WasapiInfo;
            error = GetWasapiInfoResult ? Errors.OK : WasapiInfoError;
            return GetWasapiInfoResult;
        }

        public int CreateMixer(int rate, int channels, BassFlags flags)
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

        public Errors GetCoreError() => CoreError;

        public Errors GetWasapiError() => wasapiError;
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
        WasapiInitFlags Flags,
        float BufferSeconds,
        float PeriodSeconds);


}
