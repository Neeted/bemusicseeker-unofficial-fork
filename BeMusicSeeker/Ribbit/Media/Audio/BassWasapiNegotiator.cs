using System;
using System.Collections.Generic;
using System.Linq;
using ManagedBass;
using ManagedBass.Mix;
using ManagedBass.Wasapi;
using Ribbit.Media;

namespace Ribbit.Media.Audio;

/// <summary>Copies the WASAPI endpoint fields used by negotiation.</summary>
internal readonly record struct BassWasapiDeviceSnapshot(
    string Name,
    string ID,
    bool IsDefault,
    bool IsEnabled,
    bool IsInput,
    bool IsLoopback,
    bool IsUnplugged,
    double MinimumUpdatePeriod,
    int MixFrequency,
    int MixChannels);

/// <summary>Copies the WASAPI format and buffer accepted by initialization.</summary>
internal readonly record struct BassWasapiInfoSnapshot(
    int Frequency,
    int Channels,
    WasapiFormat Format,
    int BufferLength);

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

    /// <summary>Gets all WASAPI endpoint descriptors and captures the native failure on false.</summary>
    bool TryGetDeviceInfos(out BassWasapiDeviceSnapshot[] deviceInfos, out Errors error);

    /// <summary>Gets one WASAPI endpoint descriptor and captures the native failure on false.</summary>
    bool TryGetDeviceInfo(
        int deviceIndex,
        out BassWasapiDeviceSnapshot deviceInfo,
        out Errors error);

    /// <summary>
    /// Initializes one shared WASAPI endpoint without encoding an exclusive sample format.
    /// </summary>
    bool InitializeSharedWasapi(
        int deviceIndex,
        int rate,
        int channels,
        bool eventDriven,
        WasapiProcedure callback);

    /// <summary>Float32 sample formatを使ってexclusive WASAPI endpointを初期化します。</summary>
    bool InitializeExclusiveWasapi(
        int deviceIndex,
        int rate,
        int channels,
        WasapiInitFlags flags,
        float bufferSeconds,
        float periodSeconds,
        WasapiProcedure callback);

    /// <summary>Gets the current WASAPI endpoint.</summary>
    int GetWasapiDevice();

    /// <summary>Reads back the format and buffer accepted by WASAPI.</summary>
    bool TryGetWasapiInfo(out BassWasapiInfoSnapshot info, out Errors error);

    /// <summary>WASAPI callbackへ供給するFloat32 decode mixerを作成します。</summary>
    int CreateMixer(int rate, int channels, BassFlags flags);

    /// <summary>Starts WASAPI output.</summary>
    bool StartWasapi();

    /// <summary>Gets the BASS core error immediately after a failed core or mixer call.</summary>
    Errors GetCoreError();

    /// <summary>Gets the BASSWASAPI error immediately after a failed WASAPI call.</summary>
    Errors GetWasapiError();
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
    /// Initializes one shared or exclusive WASAPI graph with a Float32 mixer and callback,
    /// applying the requested application gain before shared-mode output starts.
    /// </summary>
    internal BassAudioBackendResult Initialize(
        BassAudioNegotiationRequest request,
        BassAudioSession session,
        WasapiProcedure callback,
        float initialGain,
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
            Errors error = native.GetCoreError();
            throw Failure(
                request,
                session,
                "BASS_Init",
                "BASS",
                error,
                "BASS_Init failed: " + BassNativeErrorFormatter.Format(error));
        }
        session.CoreInitialized = true;
        session.CoreDeviceIndex = native.GetCoreDevice();
        native.DisableCoreUpdatePeriod();

        if (!native.TryGetDeviceInfos(
                out BassWasapiDeviceSnapshot[] deviceInfos,
                out Errors deviceInfosError))
        {
            throw Failure(
                request,
                session,
                "BASS_WASAPI_GetDeviceInfos",
                "BASSWASAPI",
                deviceInfosError,
                "BASS_WASAPI_GetDeviceInfos failed: "
                + BassNativeErrorFormatter.Format(deviceInfosError));
        }

        IndexedWasapiDevice[] devices = deviceInfos
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

        (IndexedWasapiDevice selected, string deviceFallback) = SelectDevice(
            devices,
            request.Device,
            useStableIdentityAsAuthoritative: shared);
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
        if (!native.TryGetDeviceInfo(
                selected.NativeIndex,
                out BassWasapiDeviceSnapshot deviceInfo,
                out Errors deviceInfoError))
        {
            throw Failure(
                request,
                session,
                "BASS_WASAPI_GetDeviceInfo",
                "BASSWASAPI",
                deviceInfoError,
                "BASS_WASAPI_GetDeviceInfo failed: "
                + BassNativeErrorFormatter.Format(deviceInfoError));
        }
        var actualDevice = new BassAudioPlayer.DeviceDescriptor(deviceInfo.Name, deviceInfo.ID);
        session.ActualDevice = actualDevice;
        session.WasapiDeviceIndex = selected.NativeIndex;
        AddFallbackReason(fallbackReasons, deviceFallback);

        int requestedRate = shared || request.Rate == SampleRate.AUTO
            ? deviceInfo.MixFrequency
            : (int)request.Rate;
        int requestedChannels = shared ? deviceInfo.MixChannels : 2;
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
            (float)deviceInfo.MinimumUpdatePeriod + 0.001f,
            requestedLatencySeconds);
        IReadOnlyList<BassWasapiInitializationCandidate> candidates = GetInitializationCandidates(
            shared,
            eventModeRequested,
            requestedBufferSeconds,
            (float)deviceInfo.MinimumUpdatePeriod);

        BassWasapiInitializationCandidate acceptedCandidate = null;
        Errors lastError = Errors.OK;
        foreach (BassWasapiInitializationCandidate candidate in candidates)
        {
            WasapiInitFlags exclusiveFlags = WasapiInitFlags.Exclusive
                | WasapiInitFlags.AutoFormat
                | WasapiInitFlags.Dither;
            if (candidate.EventDriven)
            {
                exclusiveFlags |= WasapiInitFlags.EventDriven;
            }

            bool initialized = shared
                ? native.InitializeSharedWasapi(
                    selected.NativeIndex,
                    requestedRate,
                    requestedChannels,
                    candidate.EventDriven,
                    callback)
                : native.InitializeExclusiveWasapi(
                    selected.NativeIndex,
                    requestedRate,
                    requestedChannels,
                    exclusiveFlags,
                    candidate.BufferSeconds,
                    candidate.PeriodSeconds,
                    callback);
            if (!initialized)
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
                "BASS_WASAPI_Init failed: " + BassNativeErrorFormatter.Format(lastError));
        }
        AddInitializationFallbackReason(fallbackReasons, attempts, acceptedCandidate);

        if (!native.TryGetWasapiInfo(
                out BassWasapiInfoSnapshot info,
                out Errors infoError))
        {
            throw Failure(
                request,
                session,
                "BASS_WASAPI_GetInfo",
                "BASSWASAPI",
                infoError,
                "BASS_WASAPI_GetInfo failed: "
                + BassNativeErrorFormatter.Format(infoError));
        }
        if (info.Frequency <= 0 || info.Channels <= 0)
        {
            Errors error = native.GetWasapiError();
            throw Failure(
                request,
                session,
                "BASS_WASAPI_GetInfo",
                "BASSWASAPI",
                error,
                "BASS_WASAPI_GetInfo returned an invalid format: "
                + BassNativeErrorFormatter.Format(error));
        }

        SampleFormat endpointFormat = FromWasapiFormat(info.Format);
        int bytesPerSample = BytesPerSample(info.Format);
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
            "readback rate=" + info.Frequency
            + " channels=" + info.Channels
            + " format=" + info.Format
            + " bufferBytes=" + info.BufferLength));

        BassFlags mixerFlags = BassFlags.Float
            | BassFlags.Prescan
            | BassFlags.Decode
            | BassFlags.MixerNonStop;
        int mixerHandle = native.CreateMixer(info.Frequency, info.Channels, mixerFlags);
        if (mixerHandle == 0)
        {
            Errors error = native.GetCoreError();
            throw Failure(
                request,
                session,
                "BASS_Mixer_StreamCreate",
                "BASS",
                error,
                "BASS_Mixer_StreamCreate failed: " + BassNativeErrorFormatter.Format(error));
        }
        session.MixerHandle = mixerHandle;
        session.OutputProcessor = new AudioOutputProcessor(info.Frequency, initialGain);
        session.CallbackPcmRenderer = new AudioPcmRenderer(mixerHandle, info.Frequency, info.Channels);

        session.TrackOutputHandle(mixerHandle);
        if (!native.StartWasapi())
        {
            Errors error = native.GetWasapiError();
            throw Failure(
                request,
                session,
                "BASS_WASAPI_Start",
                "BASSWASAPI",
                error,
                "BASS_WASAPI_Start failed: " + BassNativeErrorFormatter.Format(error));
        }
        session.IsStarted = true;

        double latencyMilliseconds = info.BufferLength * 1000d
            / bytesPerSample
            / info.Frequency
            / info.Channels;
        var result = new BassAudioBackendResult(
            request,
            actualDevice,
            (SampleRate)info.Frequency,
            SampleFormat.SAMPLE_FLOAT_32BIT,
            endpointFormat,
            latencyMilliseconds,
            mixerHandle,
            attempts.AsReadOnly(),
            fallbackReasons.Count == 0 ? null : string.Join(" ", fallbackReasons),
            info.Channels,
            callbackFormat: SampleFormat.SAMPLE_FLOAT_32BIT);
        session.NegotiationResult = result;
        return result;
    }

    /// <summary>WASAPI modeとperiodの重複しないcandidateを決定順で作成します。</summary>
    internal static IReadOnlyList<BassWasapiInitializationCandidate> GetInitializationCandidates(
        bool shared,
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

        if (shared)
        {
            if (eventModeRequested)
            {
                Add(true, 0f, 0f, "shared event native default buffer/period");
            }
            Add(false, 0f, 0f, "shared non-event native default buffer/period");
            return result.AsReadOnly();
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
        BassAudioPlayer.DeviceDescriptor requestedDevice,
        bool useStableIdentityAsAuthoritative)
    {
        if (useStableIdentityAsAuthoritative
            && !string.IsNullOrWhiteSpace(requestedDevice.Driver))
        {
            IndexedWasapiDevice exact = devices.FirstOrDefault(device =>
                string.Equals(requestedDevice.Driver, device.Info.ID, StringComparison.Ordinal));
            if (exact != null)
            {
                return (exact, null);
            }
        }
        else if (!requestedDevice.Equals(default(BassAudioPlayer.DeviceDescriptor)))
        {
            IndexedWasapiDevice exact = devices.FirstOrDefault(device =>
                string.Equals(requestedDevice.Name, device.Info.Name, StringComparison.Ordinal)
                && string.Equals(requestedDevice.Driver, device.Info.ID, StringComparison.Ordinal));
            if (exact != null)
            {
                return (exact, null);
            }

            IndexedWasapiDevice compatibleName = devices.FirstOrDefault(device =>
                string.Equals(requestedDevice.Name, device.Info.Name, StringComparison.Ordinal));
            if (compatibleName != null)
            {
                return (
                    compatibleName,
                    useStableIdentityAsAuthoritative
                        ? "Legacy WASAPI device selection had no stable identity; a name match was used."
                        : "Requested WASAPI device identity was stale; a compatible name match was used.");
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
                + " nativeErrorCode=" + BassNativeErrorFormatter.Format(attempt.NativeErrorCode)));
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

    private static SampleFormat FromWasapiFormat(WasapiFormat format) => format switch
    {
        WasapiFormat.Float => SampleFormat.SAMPLE_FLOAT_32BIT,
        WasapiFormat.Bit8 => SampleFormat.SAMPLE_INT_8BIT,
        WasapiFormat.Bit16 => SampleFormat.SAMPLE_INT_16BIT,
        WasapiFormat.Bit24 => SampleFormat.SAMPLE_INT_24BIT,
        WasapiFormat.Bit32 => SampleFormat.SAMPLE_INT_32BIT,
        _ => SampleFormat.UNKNOWN
    };

    private static int BytesPerSample(WasapiFormat format) => format switch
    {
        WasapiFormat.Bit8 => 1,
        WasapiFormat.Bit16 => 2,
        WasapiFormat.Bit24 => 3,
        WasapiFormat.Bit32 => 4,
        WasapiFormat.Float => 4,
        _ => 0
    };

    private static AudioInitializationException Failure(
        BassAudioNegotiationRequest request,
        BassAudioSession session,
        string stage,
        string source,
        Errors? error,
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
        internal IndexedWasapiDevice(int nativeIndex, BassWasapiDeviceSnapshot info)
        {
            NativeIndex = nativeIndex;
            Info = info;
        }

        internal int NativeIndex { get; }

        internal BassWasapiDeviceSnapshot Info { get; }
    }
}

/// <summary>Forwards WASAPI negotiation calls to ManagedBass.</summary>
internal sealed class BassWasapiNegotiationNativeBoundary : IWasapiNegotiationNativeBoundary
{
    private Errors? coreErrorOverride;
    private Errors? wasapiErrorOverride;

    /// <inheritdoc />
    public bool InitializeCore()
    {
        coreErrorOverride = null;
        try
        {
            return Bass.Init(0, 44100, DeviceInitFlags.Default, IntPtr.Zero, IntPtr.Zero);
        }
        catch (BassException exception)
        {
            coreErrorOverride = exception.ErrorCode;
            return false;
        }
    }

    /// <inheritdoc />
    public int GetCoreDevice() => Bass.CurrentDevice;

    /// <inheritdoc />
    public void DisableCoreUpdatePeriod()
    {
        try
        {
            Bass.UpdatePeriod = 0;
        }
        catch (BassException exception)
        {
            coreErrorOverride = exception.ErrorCode;
        }
    }

    /// <inheritdoc />
    public bool TryGetDeviceInfos(
        out BassWasapiDeviceSnapshot[] deviceInfos,
        out Errors error)
    {
        if (!BassAudioDeviceEnumeration.TryEnumerate(
                TryReadDeviceInfo,
                out WasapiDeviceInfo[] nativeDevices,
                out error))
        {
            deviceInfos = [];
            return false;
        }

        deviceInfos = Array.ConvertAll(nativeDevices, ToDeviceSnapshot);
        return true;
    }

    private static bool TryReadDeviceInfo(
        int index,
        out WasapiDeviceInfo deviceInfo,
        out Errors error)
    {
        if (BassWasapi.GetDeviceInfo(index, out deviceInfo))
        {
            error = Errors.OK;
            return true;
        }

        error = Bass.LastError;
        return false;
    }

    /// <inheritdoc />
    public bool TryGetDeviceInfo(
        int deviceIndex,
        out BassWasapiDeviceSnapshot deviceInfo,
        out Errors error)
    {
        deviceInfo = default;
        error = Errors.Unknown;
        try
        {
            if (!BassWasapi.GetDeviceInfo(deviceIndex, out WasapiDeviceInfo info))
            {
                error = Bass.LastError;
                return false;
            }

            deviceInfo = ToDeviceSnapshot(info);
            error = Errors.OK;
            return true;
        }
        catch (BassException exception)
        {
            error = exception.ErrorCode;
            return false;
        }
    }

    /// <inheritdoc />
    public bool InitializeSharedWasapi(
        int deviceIndex,
        int rate,
        int channels,
        bool eventDriven,
        WasapiProcedure callback)
    {
        wasapiErrorOverride = null;
        // Shared mode is represented by the absence of the Exclusive flag. The endpoint owns
        // the shared buffer and period, so both timing values remain at their native defaults.
        return BassWasapi.Init(
            deviceIndex,
            rate,
            channels,
            eventDriven ? WasapiInitFlags.EventDriven : WasapiInitFlags.Shared,
            0f,
            0f,
            callback,
            IntPtr.Zero);
    }

    /// <inheritdoc />
    public bool InitializeExclusiveWasapi(
        int deviceIndex,
        int rate,
        int channels,
        WasapiInitFlags flags,
        float bufferSeconds,
        float periodSeconds,
        WasapiProcedure callback)
    {
        wasapiErrorOverride = null;
        return BassWasapi.Init(
            deviceIndex,
            rate,
            channels,
            flags,
            bufferSeconds,
            periodSeconds,
            callback,
            IntPtr.Zero);
    }

    /// <inheritdoc />
    public int GetWasapiDevice() => BassWasapi.CurrentDevice;

    /// <inheritdoc />
    public bool TryGetWasapiInfo(
        out BassWasapiInfoSnapshot infoSnapshot,
        out Errors error)
    {
        infoSnapshot = default;
        error = Errors.Unknown;
        try
        {
            if (!BassWasapi.GetInfo(out WasapiInfo info))
            {
                error = Bass.LastError;
                return false;
            }

            infoSnapshot = new BassWasapiInfoSnapshot(
                info.Frequency,
                info.Channels,
                info.Format,
                info.BufferLength);
            error = Errors.OK;
            return true;
        }
        catch (BassException exception)
        {
            error = exception.ErrorCode;
            return false;
        }
    }

    private static BassWasapiDeviceSnapshot ToDeviceSnapshot(WasapiDeviceInfo info) =>
        new(
            info.Name,
            info.ID,
            info.IsDefault,
            info.IsEnabled,
            info.IsInput,
            info.IsLoopback,
            info.IsUnplugged,
            info.MinimumUpdatePeriod,
            info.MixFrequency,
            info.MixChannels);

    /// <inheritdoc />
    public int CreateMixer(int rate, int channels, BassFlags flags)
    {
        coreErrorOverride = null;
        return BassMix.CreateMixerStream(rate, channels, flags);
    }

    /// <inheritdoc />
    public bool StartWasapi()
    {
        wasapiErrorOverride = null;
        return BassWasapi.Start();
    }

    /// <inheritdoc />
    public Errors GetCoreError() => coreErrorOverride ?? Bass.LastError;

    /// <inheritdoc />
    public Errors GetWasapiError() => wasapiErrorOverride ?? Bass.LastError;
}
