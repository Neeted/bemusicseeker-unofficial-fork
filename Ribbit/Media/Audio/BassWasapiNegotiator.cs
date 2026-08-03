using System;
using System.Collections.Generic;
using System.Linq;
using Ribbit.Media;
using Un4seen.Bass;
using Un4seen.Bass.AddOn.Mix;
using Un4seen.BassWasapi;

namespace Ribbit.Media.Audio;

/// <summary>
/// Exposes the native calls required to negotiate one WASAPI output graph.
/// </summary>
internal interface IWasapiNegotiationNativeBoundary
{
    /// <summary>Initializes the decode-only BASS core device.</summary>
    bool InitializeCore();

    /// <summary>Gets the current BASS core device.</summary>
    int GetCoreDevice();

    /// <summary>Disables periodic BASS core updates for callback-driven output.</summary>
    void DisableCoreUpdatePeriod();

    /// <summary>Gets all WASAPI endpoint descriptors.</summary>
    BASS_WASAPI_DEVICEINFO[] GetDeviceInfos();

    /// <summary>Gets one WASAPI endpoint descriptor.</summary>
    BASS_WASAPI_DEVICEINFO GetDeviceInfo(int deviceIndex);

    /// <summary>Initializes one WASAPI endpoint with a Float32 callback.</summary>
    bool InitializeWasapi(
        int deviceIndex,
        int rate,
        int channels,
        BASSWASAPIInit flags,
        float bufferSeconds,
        float periodSeconds,
        WASAPIPROC callback);

    /// <summary>Gets the current WASAPI endpoint.</summary>
    int GetWasapiDevice();

    /// <summary>Reads back the format and buffer accepted by WASAPI.</summary>
    BASS_WASAPI_INFO GetWasapiInfo();

    /// <summary>Creates the Float32 decode mixer that supplies the WASAPI callback.</summary>
    int CreateMixer(int rate, int channels, BASSFlag flags);

    /// <summary>Starts WASAPI output.</summary>
    bool StartWasapi();

    /// <summary>Gets the BASS core error immediately after a failed core or mixer call.</summary>
    BASSError GetCoreError();

    /// <summary>Gets the BASSWASAPI error immediately after a failed WASAPI call.</summary>
    BASSError GetWasapiError();
}

/// <summary>Describes one duplicate-free WASAPI initialization candidate.</summary>
internal sealed class BassWasapiInitializationCandidate
{
    /// <summary>Creates one event/buffer/period combination.</summary>
    internal BassWasapiInitializationCandidate(
        bool eventDriven,
        float bufferSeconds,
        float periodSeconds,
        string description)
    {
        EventDriven = eventDriven;
        BufferSeconds = bufferSeconds;
        PeriodSeconds = periodSeconds;
        Description = description;
    }

    /// <summary>Gets whether the candidate requests event-driven operation.</summary>
    internal bool EventDriven { get; }

    /// <summary>Gets the requested buffer length in seconds, or zero for the native default.</summary>
    internal float BufferSeconds { get; }

    /// <summary>Gets the requested period in seconds, or zero for the native default.</summary>
    internal float PeriodSeconds { get; }

    /// <summary>Gets the diagnostic candidate description.</summary>
    internal string Description { get; }
}

/// <summary>
/// Negotiates deterministic same-backend WASAPI degradation while retaining every native failure.
/// </summary>
internal sealed class BassWasapiNegotiator
{
    private readonly IWasapiNegotiationNativeBoundary native;

    /// <summary>Creates a WASAPI negotiator over a replaceable native boundary.</summary>
    internal BassWasapiNegotiator(IWasapiNegotiationNativeBoundary native)
    {
        this.native = native ?? throw new ArgumentNullException(nameof(native));
    }

    /// <summary>
    /// Initializes one shared or exclusive WASAPI graph with a Float32 mixer and callback.
    /// </summary>
    internal BassAudioBackendResult Initialize(
        BassAudioNegotiationRequest request,
        BassAudioSession session,
        WASAPIPROC callback,
        bool eventModeRequested)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(session);
        ArgumentNullException.ThrowIfNull(callback);
        bool shared = request.Backend == BassAudioPlayer.DeviceDriver.WASAPI_SHARED;
        if (!shared && request.Backend != BassAudioPlayer.DeviceDriver.WASAPI_EXCLUSIVE)
        {
            throw new ArgumentException("The WASAPI negotiator requires a WASAPI request.", nameof(request));
        }

        var attempts = new List<BassAudioBackendAttempt>();
        var fallbackReasons = new List<string>();
        if (!native.InitializeCore())
        {
            BASSError error = native.GetCoreError();
            throw Failure(request, session, "BASS_Init", "BASS", error, "BASS_Init failed: " + error);
        }
        session.CoreInitialized = true;
        session.CoreDeviceIndex = native.GetCoreDevice();
        native.DisableCoreUpdatePeriod();

        IndexedWasapiDevice[] devices = native.GetDeviceInfos()
            .Select((info, index) => new IndexedWasapiDevice(index, info))
            .Where(device =>
                device.Info.IsEnabled
                && !device.Info.IsUnplugged
                && !device.Info.IsLoopback
                && !device.Info.IsInput)
            .ToArray();
        if (devices.Length == 0)
        {
            throw Failure(
                request,
                session,
                "BASS_WASAPI_GetDeviceInfos",
                "BASSWASAPI",
                null,
                "WASAPI device not found.");
        }

        (IndexedWasapiDevice selected, string deviceFallback) = SelectDevice(devices, request.Device);
        if (selected == null)
        {
            throw Failure(
                request,
                session,
                "BASS_WASAPI_GetDeviceInfos",
                "BASSWASAPI",
                null,
                "WASAPI default device not found.");
        }
        BASS_WASAPI_DEVICEINFO deviceInfo = native.GetDeviceInfo(selected.NativeIndex);
        var actualDevice = new BassAudioPlayer.DeviceDescriptor(deviceInfo.name, deviceInfo.id);
        session.ActualDevice = actualDevice;
        session.WasapiDeviceIndex = selected.NativeIndex;
        AddFallbackReason(fallbackReasons, deviceFallback);

        int requestedRate = shared || request.Rate == SampleRate.AUTO
            ? deviceInfo.mixfreq
            : (int)request.Rate;
        int requestedChannels = shared ? deviceInfo.mixchans : 2;
        if (shared && request.Rate != SampleRate.AUTO && (int)request.Rate != requestedRate)
        {
            AddFallbackReason(
                fallbackReasons,
                "WASAPI shared mode prioritized endpoint mix rate " + requestedRate
                + " over requested rate " + (int)request.Rate + ".");
        }
        if (request.Format is not SampleFormat.AUTO and not SampleFormat.SAMPLE_FLOAT_32BIT)
        {
            AddFallbackReason(
                fallbackReasons,
                "Requested WASAPI format " + request.Format
                + " was normalized to Float32 to keep mixer and callback byte widths equal.");
        }

        float requestedLatencySeconds = request.LatencyMilliseconds > 0f
            ? request.LatencyMilliseconds / 1000f
            : 0.016f;
        float requestedBufferSeconds = System.Math.Max(
            deviceInfo.minperiod + 0.001f,
            requestedLatencySeconds);
        IReadOnlyList<BassWasapiInitializationCandidate> candidates = GetInitializationCandidates(
            eventModeRequested,
            requestedBufferSeconds,
            deviceInfo.minperiod);

        BassWasapiInitializationCandidate acceptedCandidate = null;
        BASSError lastError = BASSError.BASS_OK;
        foreach (BassWasapiInitializationCandidate candidate in candidates)
        {
            BASSWASAPIInit flags = shared
                ? BASSWASAPIInit.BASS_WASAPI_AUTOFORMAT
                : BASSWASAPIInit.BASS_WASAPI_EXCLUSIVE | BASSWASAPIInit.BASS_WASAPI_AUTOFORMAT;
            if (candidate.EventDriven)
            {
                flags |= BASSWASAPIInit.BASS_WASAPI_EVENT;
            }

            if (!native.InitializeWasapi(
                    selected.NativeIndex,
                    requestedRate,
                    requestedChannels,
                    flags,
                    candidate.BufferSeconds,
                    candidate.PeriodSeconds,
                    callback))
            {
                lastError = native.GetWasapiError();
                attempts.Add(new BassAudioBackendAttempt(
                    "BASS_WASAPI_Init",
                    "BASSWASAPI",
                    lastError,
                    "rejected " + candidate.Description));
                continue;
            }

            // Cleanup must see ownership before any readback or graph construction can fail.
            session.WasapiInitialized = true;
            session.WasapiDeviceIndex = native.GetWasapiDevice();
            acceptedCandidate = candidate;
            attempts.Add(new BassAudioBackendAttempt(
                "BASS_WASAPI_Init",
                "BASSWASAPI",
                null,
                "accepted " + candidate.Description));
            break;
        }

        if (acceptedCandidate == null)
        {
            throw Failure(
                request,
                session,
                "BASS_WASAPI_Init",
                "BASSWASAPI",
                lastError,
                "BASS_WASAPI_Init failed: " + lastError);
        }
        AddInitializationFallbackReason(fallbackReasons, attempts, acceptedCandidate);

        BASS_WASAPI_INFO info = native.GetWasapiInfo();
        if (info == null || info.freq <= 0 || info.chans <= 0)
        {
            BASSError error = native.GetWasapiError();
            throw Failure(
                request,
                session,
                "BASS_WASAPI_GetInfo",
                "BASSWASAPI",
                error,
                "BASS_WASAPI_GetInfo returned an invalid format: " + error);
        }

        SampleFormat endpointFormat = FromWasapiFormat(info.format);
        int bytesPerSample = BytesPerSample(info.format);
        if (bytesPerSample == 0)
        {
            throw Failure(
                request,
                session,
                "BASS_WASAPI_GetInfo",
                "BASSWASAPI",
                null,
                "BASS_WASAPI_GetInfo returned an unknown sample format.");
        }

        attempts.Add(new BassAudioBackendAttempt(
            "BASS_WASAPI_GetInfo",
            "BASSWASAPI",
            null,
            "readback rate=" + info.freq
            + " channels=" + info.chans
            + " format=" + info.format
            + " bufferBytes=" + info.buflen));

        BASSFlag mixerFlags = BASSFlag.BASS_SAMPLE_FLOAT
            | BASSFlag.BASS_STREAM_PRESCAN
            | BASSFlag.BASS_STREAM_DECODE;
        int mixerHandle = native.CreateMixer(info.freq, info.chans, mixerFlags);
        if (mixerHandle == 0)
        {
            BASSError error = native.GetCoreError();
            throw Failure(
                request,
                session,
                "BASS_Mixer_StreamCreate",
                "BASS",
                error,
                "BASS_Mixer_StreamCreate failed: " + error);
        }
        session.MixerHandle = mixerHandle;
        session.OutputHandle = mixerHandle;

        if (!native.StartWasapi())
        {
            BASSError error = native.GetWasapiError();
            throw Failure(
                request,
                session,
                "BASS_WASAPI_Start",
                "BASSWASAPI",
                error,
                "BASS_WASAPI_Start failed: " + error);
        }
        session.IsStarted = true;

        double latencyMilliseconds = info.buflen * 1000d
            / bytesPerSample
            / info.freq
            / info.chans;
        var result = new BassAudioBackendResult(
            request,
            actualDevice,
            (SampleRate)info.freq,
            SampleFormat.SAMPLE_FLOAT_32BIT,
            endpointFormat,
            latencyMilliseconds,
            mixerHandle,
            attempts.AsReadOnly(),
            fallbackReasons.Count == 0 ? null : string.Join(" ", fallbackReasons));
        session.NegotiationResult = result;
        return result;
    }

    /// <summary>Builds the deterministic, duplicate-free WASAPI mode and period candidates.</summary>
    internal static IReadOnlyList<BassWasapiInitializationCandidate> GetInitializationCandidates(
        bool eventModeRequested,
        float requestedBufferSeconds,
        float requestedPeriodSeconds)
    {
        var result = new List<BassWasapiInitializationCandidate>();
        var seen = new HashSet<(bool EventDriven, float BufferSeconds, float PeriodSeconds)>();

        void Add(bool eventDriven, float bufferSeconds, float periodSeconds, string description)
        {
            if (seen.Add((eventDriven, bufferSeconds, periodSeconds)))
            {
                result.Add(new BassWasapiInitializationCandidate(
                    eventDriven,
                    bufferSeconds,
                    periodSeconds,
                    description));
            }
        }

        if (eventModeRequested)
        {
            Add(true, requestedBufferSeconds, requestedPeriodSeconds, "event requested buffer/period");
        }
        Add(false, requestedBufferSeconds, requestedPeriodSeconds, "non-event requested buffer/period");
        Add(false, 0f, 0f, "non-event native default buffer/period");
        return result.AsReadOnly();
    }

    private static (IndexedWasapiDevice Device, string FallbackReason) SelectDevice(
        IReadOnlyList<IndexedWasapiDevice> devices,
        BassAudioPlayer.DeviceDescriptor requestedDevice)
    {
        if (!requestedDevice.Equals(default(BassAudioPlayer.DeviceDescriptor)))
        {
            IndexedWasapiDevice exact = devices.FirstOrDefault(device =>
                requestedDevice.Name == device.Info.name
                && requestedDevice.Driver == device.Info.id);
            if (exact != null)
            {
                return (exact, null);
            }

            IndexedWasapiDevice compatibleName = devices.FirstOrDefault(device =>
                requestedDevice.Name == device.Info.name);
            if (compatibleName != null)
            {
                return (
                    compatibleName,
                    "Requested WASAPI device identity was stale; a compatible name match was used.");
            }
        }

        IndexedWasapiDevice defaultDevice = devices.FirstOrDefault(device => device.Info.IsDefault);
        if (requestedDevice.Equals(default(BassAudioPlayer.DeviceDescriptor)))
        {
            return (defaultDevice, null);
        }
        return (
            defaultDevice,
            "Requested WASAPI device was unavailable; the backend default device was used.");
    }

    private static void AddInitializationFallbackReason(
        List<string> fallbackReasons,
        IReadOnlyList<BassAudioBackendAttempt> attempts,
        BassWasapiInitializationCandidate acceptedCandidate)
    {
        BassAudioBackendAttempt[] rejected = attempts
            .Where(attempt => attempt.NativeErrorCode.HasValue)
            .ToArray();
        if (rejected.Length == 0)
        {
            return;
        }

        string failures = string.Join(
            "; ",
            rejected.Select(attempt =>
                attempt.Outcome
                + " nativeErrorSource=" + attempt.NativeErrorSource
                + " nativeErrorCode=" + attempt.NativeErrorCode));
        AddFallbackReason(
            fallbackReasons,
            "WASAPI same-backend fallback retained failures: " + failures
            + "; accepted " + acceptedCandidate.Description + ".");
    }

    private static void AddFallbackReason(List<string> reasons, string reason)
    {
        if (!string.IsNullOrWhiteSpace(reason))
        {
            reasons.Add(reason);
        }
    }

    private static SampleFormat FromWasapiFormat(BASSWASAPIFormat format) => format switch
    {
        BASSWASAPIFormat.BASS_WASAPI_FORMAT_FLOAT => SampleFormat.SAMPLE_FLOAT_32BIT,
        BASSWASAPIFormat.BASS_WASAPI_FORMAT_8BIT => SampleFormat.SAMPLE_INT_8BIT,
        BASSWASAPIFormat.BASS_WASAPI_FORMAT_16BIT => SampleFormat.SAMPLE_INT_16BIT,
        BASSWASAPIFormat.BASS_WASAPI_FORMAT_24BIT => SampleFormat.SAMPLE_INT_24BIT,
        BASSWASAPIFormat.BASS_WASAPI_FORMAT_32BIT => SampleFormat.SAMPLE_INT_32BIT,
        _ => SampleFormat.UNKNOWN
    };

    private static int BytesPerSample(BASSWASAPIFormat format) => format switch
    {
        BASSWASAPIFormat.BASS_WASAPI_FORMAT_8BIT => 1,
        BASSWASAPIFormat.BASS_WASAPI_FORMAT_16BIT => 2,
        BASSWASAPIFormat.BASS_WASAPI_FORMAT_24BIT => 3,
        BASSWASAPIFormat.BASS_WASAPI_FORMAT_32BIT => 4,
        BASSWASAPIFormat.BASS_WASAPI_FORMAT_FLOAT => 4,
        _ => 0
    };

    private static AudioInitializationException Failure(
        BassAudioNegotiationRequest request,
        BassAudioSession session,
        string stage,
        string source,
        BASSError? error,
        string message) =>
        new(
            request.Backend,
            request.Backend,
            stage,
            request.Device,
            session.ActualDevice,
            source,
            error,
            message);

    private sealed class IndexedWasapiDevice
    {
        internal IndexedWasapiDevice(int nativeIndex, BASS_WASAPI_DEVICEINFO info)
        {
            NativeIndex = nativeIndex;
            Info = info;
        }

        internal int NativeIndex { get; }

        internal BASS_WASAPI_DEVICEINFO Info { get; }
    }
}

/// <summary>Forwards WASAPI negotiation calls to the currently bundled Bass.Net API.</summary>
internal sealed class BassWasapiNegotiationNativeBoundary : IWasapiNegotiationNativeBoundary
{
    /// <inheritdoc />
    public bool InitializeCore() =>
        Bass.BASS_Init(0, 44100, BASSInit.BASS_DEVICE_DEFAULT, IntPtr.Zero);

    /// <inheritdoc />
    public int GetCoreDevice() => Bass.BASS_GetDevice();

    /// <inheritdoc />
    public void DisableCoreUpdatePeriod() =>
        Bass.BASS_SetConfig(BASSConfig.BASS_CONFIG_UPDATEPERIOD, 0);

    /// <inheritdoc />
    public BASS_WASAPI_DEVICEINFO[] GetDeviceInfos() => BassWasapi.BASS_WASAPI_GetDeviceInfos();

    /// <inheritdoc />
    public BASS_WASAPI_DEVICEINFO GetDeviceInfo(int deviceIndex) =>
        BassWasapi.BASS_WASAPI_GetDeviceInfo(deviceIndex);

    /// <inheritdoc />
    public bool InitializeWasapi(
        int deviceIndex,
        int rate,
        int channels,
        BASSWASAPIInit flags,
        float bufferSeconds,
        float periodSeconds,
        WASAPIPROC callback) =>
        BassWasapi.BASS_WASAPI_Init(
            deviceIndex,
            rate,
            channels,
            flags,
            BASSWASAPIFormat.BASS_WASAPI_FORMAT_FLOAT,
            bufferSeconds,
            periodSeconds,
            callback,
            IntPtr.Zero);

    /// <inheritdoc />
    public int GetWasapiDevice() => BassWasapi.BASS_WASAPI_GetDevice();

    /// <inheritdoc />
    public BASS_WASAPI_INFO GetWasapiInfo() => BassWasapi.BASS_WASAPI_GetInfo();

    /// <inheritdoc />
    public int CreateMixer(int rate, int channels, BASSFlag flags) =>
        BassMix.BASS_Mixer_StreamCreate(rate, channels, flags);

    /// <inheritdoc />
    public bool StartWasapi() => BassWasapi.BASS_WASAPI_Start();

    /// <inheritdoc />
    public BASSError GetCoreError() => Bass.BASS_ErrorGetCode();

    /// <inheritdoc />
    public BASSError GetWasapiError() => Bass.BASS_ErrorGetCode();
}
