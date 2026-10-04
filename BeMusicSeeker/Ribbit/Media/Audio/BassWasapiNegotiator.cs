using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using BeMusicSeeker.Models;
using ManagedBass;
using ManagedBass.Mix;
using ManagedBass.Wasapi;

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
internal interface IWasapiNegotiationNativeBoundary : IBassMixerThreadNativeBoundary
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

    /// <summary>指定機器・レート・チャンネル・形式の組を照会します。</summary>
    int CheckWasapiFormat(
        int deviceIndex,
        int rate,
        int channels,
        WasapiInitFlags flags,
        out Errors error);

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
    private static readonly int[] CapabilityRates =
        [48000, 44100, 96000, 88200, 192000, 176400, 352800, 384000, 32000, 22050, 11025];

    private static readonly (WasapiFormat NativeFormat, SampleFormat Format)[] CapabilityFormats =
    [
        (WasapiFormat.Float, SampleFormat.SAMPLE_FLOAT_32BIT),
        (WasapiFormat.Bit8, SampleFormat.SAMPLE_INT_8BIT),
        (WasapiFormat.Bit16, SampleFormat.SAMPLE_INT_16BIT),
        (WasapiFormat.Bit24, SampleFormat.SAMPLE_INT_24BIT),
        (WasapiFormat.Bit32, SampleFormat.SAMPLE_INT_32BIT)
    ];

    private readonly IWasapiNegotiationNativeBoundary native;

    /// <summary>Creates a WASAPI negotiator over a replaceable native boundary.</summary>
    internal BassWasapiNegotiator(IWasapiNegotiationNativeBoundary native)
    {
        this.native = native ?? throw new ArgumentNullException(nameof(native));
    }

    /// <summary>選択したWASAPI endpointの共有mix情報または排他の形式組を照会し、候補検査の間で取消を確認します。</summary>
    internal AudioDeviceCapabilityResult QueryCapabilities(
        AudioDeviceCapabilityRequest request, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        cancellationToken.ThrowIfCancellationRequested();
        if (request.Backend is not (AudioDriver.WasapiShared or AudioDriver.WasapiExclusive))
        {
            throw new ArgumentException("WASAPI capabilities require a WASAPI request.", nameof(request));
        }

        var attempts = new List<BassAudioBackendAttempt>();
        if (!native.TryGetDeviceInfos(
                out BassWasapiDeviceSnapshot[] deviceInfos,
                out Errors deviceInfosError))
        {
            return CapabilityFailure(request, "BASS_WASAPI_GetDeviceInfos", deviceInfosError, attempts);
        }

        IndexedWasapiDevice[] devices = deviceInfos
            .Select((info, index) => new IndexedWasapiDevice(index, info))
            .Where(device =>
                device.Info.IsEnabled
                && !device.Info.IsUnplugged
                && !device.Info.IsLoopback
                && !device.Info.IsInput)
            .ToArray();
        IndexedWasapiDevice selected = string.IsNullOrWhiteSpace(request.DeviceIdentity)
            ? devices.FirstOrDefault(device => device.Info.IsDefault)
            : devices.FirstOrDefault(device => string.Equals(
                device.Info.ID,
                request.DeviceIdentity,
                StringComparison.Ordinal));
        if (selected == null)
        {
            attempts.Add(new BassAudioBackendAttempt(
                "audio device selection",
                null,
                null,
                "unavailable identity=" + (request.DeviceIdentity ?? "Default")));
            return CapabilityFailure(request, "audio device selection", null, attempts);
        }

        if (!native.TryGetDeviceInfo(
                selected.NativeIndex,
                out BassWasapiDeviceSnapshot deviceInfo,
                out Errors deviceInfoError))
        {
            return CapabilityFailure(request, "BASS_WASAPI_GetDeviceInfo", deviceInfoError, attempts);
        }
        cancellationToken.ThrowIfCancellationRequested();

        if (request.Backend == AudioDriver.WasapiShared)
        {
            if (deviceInfo.MixFrequency <= 0 || deviceInfo.MixChannels <= 0)
            {
                attempts.Add(new BassAudioBackendAttempt(
                    "BASS_WASAPI_GetDeviceInfo",
                    "BASSWASAPI",
                    null,
                    "invalid shared mix rate=" + deviceInfo.MixFrequency
                    + " channels=" + deviceInfo.MixChannels));
                return CapabilityFailure(request, "BASS_WASAPI_GetDeviceInfo", null, attempts);
            }

            int rawFormat = native.CheckWasapiFormat(
                selected.NativeIndex,
                deviceInfo.MixFrequency,
                deviceInfo.MixChannels,
                WasapiInitFlags.Shared,
                out Errors formatError);
            if (rawFormat < 0)
            {
                attempts.Add(new BassAudioBackendAttempt(
                    "BASS_WASAPI_CheckFormat",
                    "BASSWASAPI",
                    formatError,
                    "unsupported shared mix rate=" + deviceInfo.MixFrequency
                    + " channels=" + deviceInfo.MixChannels));
                return formatError == Errors.SampleFormat
                    ? CreateWasapiResult(request, deviceInfo, AudioDeviceCapabilityStatus.Unsupported, attempts)
                    : CapabilityFailure(request, "BASS_WASAPI_CheckFormat", formatError, attempts);
            }

            SampleFormat mixFormat = FromWasapiFormat((WasapiFormat)rawFormat);
            attempts.Add(new BassAudioBackendAttempt(
                "BASS_WASAPI_CheckFormat",
                "BASSWASAPI",
                null,
                "readback shared mix rate=" + deviceInfo.MixFrequency
                + " channels=" + deviceInfo.MixChannels
                + " format=" + (WasapiFormat)rawFormat));
            if (mixFormat == SampleFormat.UNKNOWN)
            {
                return CapabilityFailure(request, "BASS_WASAPI_CheckFormat", null, attempts);
            }

            return new AudioDeviceCapabilityResult(
                request.Backend,
                deviceInfo.ID,
                deviceInfo.Name,
                AudioDeviceCapabilityStatus.Available,
                [(SampleRate)deviceInfo.MixFrequency],
                [new AudioDeviceFormatCapability(
                    (SampleRate)deviceInfo.MixFrequency,
                    mixFormat,
                    IsSupported: true)],
                mixFormat,
                endpointContainerBits: BitsPerSample((WasapiFormat)rawFormat),
                endpointEffectiveBits: BitsPerSample((WasapiFormat)rawFormat),
                endpointChannels: deviceInfo.MixChannels,
                attempts: attempts);
        }

        var formatCapabilities = new List<AudioDeviceFormatCapability>();
        var supportedRates = new HashSet<SampleRate>();
        foreach (int rate in GetCapabilityRates(request.SavedRate, deviceInfo.MixFrequency))
        {
            foreach ((WasapiFormat nativeFormat, SampleFormat format) in CapabilityFormats)
            {
                cancellationToken.ThrowIfCancellationRequested();
                int returnedFormat = CheckExclusiveFormat(
                    selected.NativeIndex, rate, nativeFormat, attempts, out Errors error);
                if (returnedFormat < 0)
                {
                    if (error == Errors.NotAvailable)
                    {
                        return CreateWasapiResult(
                            request,
                            deviceInfo,
                            AudioDeviceCapabilityStatus.Unsupported,
                            attempts,
                            failureStage: "BASS_WASAPI_CheckFormat",
                            nativeErrorCode: error);
                    }
                    if (error != Errors.SampleFormat)
                    {
                        return CapabilityFailure(request, "BASS_WASAPI_CheckFormat", error, attempts);
                    }
                    formatCapabilities.Add(new AudioDeviceFormatCapability(
                        (SampleRate)rate,
                        format,
                        IsSupported: false));
                    continue;
                }

                bool supported = returnedFormat == (int)nativeFormat;
                formatCapabilities.Add(new AudioDeviceFormatCapability(
                    (SampleRate)rate,
                    format,
                    supported));
                if (supported)
                {
                    supportedRates.Add((SampleRate)rate);
                }
            }
        }

        return new AudioDeviceCapabilityResult(
            request.Backend,
            deviceInfo.ID,
            deviceInfo.Name,
            supportedRates.Count == 0
                ? AudioDeviceCapabilityStatus.Unsupported
                : AudioDeviceCapabilityStatus.Available,
            supportedRates.ToArray(),
            formatCapabilities,
            attempts: attempts);
    }

    // CheckFormatも低精度形式を返し得るため、照会と開始は同じ完全一致判定を使います。
    private int CheckExclusiveFormat(int deviceIndex, int rate, WasapiFormat format,
        List<BassAudioBackendAttempt> attempts, out Errors error)
    {
        int returned = native.CheckWasapiFormat(deviceIndex, rate, 2,
            WasapiInitFlags.Exclusive | (WasapiInitFlags)((int)format << 16), out error);
        attempts.Add(new BassAudioBackendAttempt("BASS_WASAPI_CheckFormat", "BASSWASAPI",
            returned < 0 ? error : null,
            (returned == (int)format ? "supported" : returned < 0 ? "unsupported" : "mismatched")
            + " rate=" + rate + " requestedFormat=" + FromWasapiFormat(format)
            + (returned < 0 ? string.Empty : " returnedFormat=" + (WasapiFormat)returned)));
        return returned;
    }

    private static IReadOnlyList<int> GetCapabilityRates(
        SampleRate savedRate,
        int currentMixRate)
    {
        var result = new List<int>();
        var seen = new HashSet<int>();

        void Add(int rate)
        {
            if (rate > 0 && seen.Add(rate))
            {
                result.Add(rate);
            }
        }

        Add(currentMixRate);
        Add((int)savedRate);
        foreach (int rate in CapabilityRates)
        {
            Add(rate);
        }
        return result.AsReadOnly();
    }

    private static int BitsPerSample(WasapiFormat format) => format switch
    {
        WasapiFormat.Bit8 => 8,
        WasapiFormat.Bit16 => 16,
        WasapiFormat.Bit24 => 24,
        WasapiFormat.Bit32 or WasapiFormat.Float => 32,
        _ => 0
    };

    private static AudioDeviceCapabilityResult CreateWasapiResult(
        AudioDeviceCapabilityRequest request,
        BassWasapiDeviceSnapshot deviceInfo,
        AudioDeviceCapabilityStatus status,
        IReadOnlyList<BassAudioBackendAttempt> attempts,
        string failureStage = null,
        Errors? nativeErrorCode = null) =>
        new(
            request.Backend,
            deviceInfo.ID,
            deviceInfo.Name,
            status,
            failureStage: failureStage,
            nativeErrorSource: nativeErrorCode.HasValue ? "BASSWASAPI/BASS_ErrorGetCode" : null,
            nativeErrorCode: nativeErrorCode,
            attempts: attempts);

    private static AudioDeviceCapabilityResult CapabilityFailure(
        AudioDeviceCapabilityRequest request,
        string stage,
        Errors? error,
        IReadOnlyList<BassAudioBackendAttempt> attempts) =>
        new(
            request.Backend,
            request.DeviceIdentity,
            request.DeviceName,
            AudioDeviceCapabilityStatus.Failed,
            failureStage: stage,
            nativeErrorSource: error.HasValue ? "BASSWASAPI/BASS_ErrorGetCode" : null,
            nativeErrorCode: error,
            attempts: attempts);

    /// <summary>
    /// 用途に従って共有・排他の実効条件を選び、Float32 mixerとcallbackを初期化します。
    /// 共有出力を開始する前に指定音量を適用します。
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
                "BASS_Init failed: " + BassNativeErrorFormatter.Format(error),
                attempts: attempts);
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
                + BassNativeErrorFormatter.Format(deviceInfosError),
                attempts: attempts);
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
                "WASAPI device not found.",
                attempts: attempts);
        }

        (IndexedWasapiDevice selected, string deviceFallback) = SelectDevice(
            devices,
            request.Device,
            useStableIdentityAsAuthoritative: shared || request.RequiresExactSelection,
            requireExactDevice: request.RequiresExactSelection);
        if (selected == null)
        {
            throw Failure(
                request,
                session,
                "BASS_WASAPI_GetDeviceInfos",
                "BASSWASAPI",
                null,
                request.RequiresExactSelection
                    ? "The requested WASAPI device is unavailable."
                    : "WASAPI default device not found.",
                attempts: attempts);
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
                + BassNativeErrorFormatter.Format(deviceInfoError),
                attempts: attempts);
        }
        var actualDevice = new BassAudioPlayer.DeviceDescriptor(deviceInfo.Name, deviceInfo.ID);
        session.ActualDevice = actualDevice;
        session.WasapiDeviceIndex = selected.NativeIndex;
        AddFallbackReason(fallbackReasons, deviceFallback);

        int requestedRate = shared || request.Rate == SampleRate.AUTO
            ? deviceInfo.MixFrequency
            : (int)request.Rate;
        if (!shared && request.Rate == SampleRate.AUTO
            && TryGetWasapiFormat(request.Format, out WasapiFormat exactFormat))
        {
            bool found = false;
            foreach (int rate in GetCapabilityRates(SampleRate.AUTO, deviceInfo.MixFrequency))
            {
                int returned = CheckExclusiveFormat(selected.NativeIndex, rate, exactFormat, attempts, out Errors error);
                if (returned == (int)exactFormat)
                {
                    requestedRate = rate;
                    found = true;
                    break;
                }
                if (returned < 0 && error != Errors.SampleFormat)
                {
                    throw Failure(request, session, "BASS_WASAPI_CheckFormat", "BASSWASAPI", error,
                        "WASAPI format check failed.", attempts: attempts);
                }
            }
            if (!found && request.RequiresExactSelection)
            {
                throw Failure(request, session, "BASS_WASAPI_CheckFormat", "BASSWASAPI", Errors.SampleFormat,
                    "No rate supports the requested WASAPI endpoint format.", attempts: attempts);
            }
        }
        int requestedChannels = shared ? deviceInfo.MixChannels : 2;
        if (shared && request.Rate != SampleRate.AUTO && (int)request.Rate != requestedRate)
        {
            AddFallbackReason(
                fallbackReasons,
                "WASAPI shared mode prioritized endpoint mix rate " + requestedRate
                + " over requested rate " + (int)request.Rate + ".");
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
            (float)deviceInfo.MinimumUpdatePeriod,
            request.RequiresExactSelection);

        BassWasapiInitializationCandidate acceptedCandidate = null;
        Errors lastError = Errors.OK;
        foreach (BassWasapiInitializationCandidate candidate in candidates)
        {
            WasapiInitFlags exclusiveFlags = WasapiInitFlags.Exclusive | WasapiInitFlags.Dither;
            // AUTOFORMATはレートや形式を変え得るため、明示条件を検証するテストへ渡しません。
            if (!request.RequiresExactSelection || request.Format == SampleFormat.AUTO)
            {
                exclusiveFlags |= WasapiInitFlags.AutoFormat;
            }
            if (!shared && TryGetWasapiFormat(request.Format, out WasapiFormat requestedFormat))
            {
                exclusiveFlags |= (WasapiInitFlags)((int)requestedFormat << 16);
            }
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
                "BASS_WASAPI_Init failed: " + BassNativeErrorFormatter.Format(lastError),
                attempts: attempts);
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
                + BassNativeErrorFormatter.Format(infoError),
                attempts: attempts);
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
                + BassNativeErrorFormatter.Format(error),
                attempts: attempts);
        }

        if (request.RequiresExactSelection
            && !shared
            && request.Rate != SampleRate.AUTO
            && info.Frequency != requestedRate)
        {
            throw Failure(
                request,
                session,
                "BASS_WASAPI_GetInfo",
                "BASSWASAPI",
                null,
                "Requested WASAPI sample rate " + requestedRate
                + " was read back as " + info.Frequency + ".",
                attempts: attempts);
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
                "BASS_WASAPI_GetInfo returned an unknown sample format.",
                attempts: attempts);
        }
        if (request.Format != SampleFormat.AUTO && request.Format != endpointFormat)
        {
            if (request.RequiresExactSelection)
            {
                throw Failure(
                    request,
                    session,
                    "BASS_WASAPI_GetInfo",
                    "BASSWASAPI",
                    null,
                    "Requested WASAPI endpoint format " + request.Format
                    + " was read back as " + endpointFormat + ".",
                    attempts: attempts);
            }
            AddFallbackReason(
                fallbackReasons,
                "Requested WASAPI endpoint format " + request.Format
                + " was read back as " + endpointFormat + ".");
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
                "BASS_Mixer_StreamCreate failed: " + BassNativeErrorFormatter.Format(error),
                attempts: attempts);
        }
        session.MixerHandle = mixerHandle;
        session.TrackOutputHandle(mixerHandle);
        try
        {
            BassMixerThreadConfigurator.SetAndConfirm(
                mixerHandle,
                native,
                request.PlayerMixerThreadCount);
        }
        catch (BassMixerThreadConfigurationException exception)
        {
            throw Failure(
                request,
                session,
                exception.NativeApi,
                "BASS",
                exception.NativeErrorCode,
                exception.Message,
                exception,
                attempts);
        }
        session.OutputProcessor = new AudioOutputProcessor(info.Frequency, initialGain);
        session.CallbackPcmRenderer = new AudioPcmRenderer(mixerHandle, info.Frequency, info.Channels);
        if (!native.StartWasapi())
        {
            Errors error = native.GetWasapiError();
            throw Failure(
                request,
                session,
                "BASS_WASAPI_Start",
                "BASSWASAPI",
                error,
                "BASS_WASAPI_Start failed: " + BassNativeErrorFormatter.Format(error),
                attempts: attempts);
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
            callbackFormat: SampleFormat.SAMPLE_FLOAT_32BIT,
            endpointContainerBits: bytesPerSample * 8,
            endpointEffectiveBits: bytesPerSample * 8);
        session.NegotiationResult = result;
        return result;
    }

    /// <summary>重複しない開始候補を決定順で作成します。設定テストはイベント指定を維持し、同方式内のバッファ調整だけを許します。</summary>
    internal static IReadOnlyList<BassWasapiInitializationCandidate> GetInitializationCandidates(
        bool shared,
        bool eventModeRequested,
        float requestedBufferSeconds,
        float requestedPeriodSeconds,
        bool requireExactEventMode = false)
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
            if (!eventModeRequested || !requireExactEventMode)
            {
                Add(false, 0f, 0f, "shared non-event native default buffer/period");
            }
            return result.AsReadOnly();
        }

        if (eventModeRequested)
        {
            Add(true, requestedBufferSeconds, requestedPeriodSeconds, "event requested buffer/period");
            if (requireExactEventMode)
            {
                Add(true, 0f, 0f, "event native default buffer/period");
                return result.AsReadOnly();
            }
        }
        Add(false, requestedBufferSeconds, requestedPeriodSeconds, "non-event requested buffer/period");
        Add(false, 0f, 0f, "non-event native default buffer/period");
        return result.AsReadOnly();
    }

    private static (IndexedWasapiDevice Device, string FallbackReason) SelectDevice(
        IReadOnlyList<IndexedWasapiDevice> devices,
        BassAudioPlayer.DeviceDescriptor requestedDevice,
        bool useStableIdentityAsAuthoritative,
        bool requireExactDevice = false)
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
            if (requireExactDevice)
            {
                // Selection-only tests must not turn a missing stable identity into Default.
                // Shared playback still uses the existing default fallback policy.
                return (null, null);
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
            .Where(attempt => attempt.Stage == "BASS_WASAPI_Init" && attempt.NativeErrorCode.HasValue)
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

    private static bool TryGetWasapiFormat(SampleFormat format, out WasapiFormat wasapiFormat)
    {
        switch (format)
        {
            case SampleFormat.SAMPLE_FLOAT_32BIT:
                wasapiFormat = WasapiFormat.Float;
                return true;
            case SampleFormat.SAMPLE_INT_8BIT:
                wasapiFormat = WasapiFormat.Bit8;
                return true;
            case SampleFormat.SAMPLE_INT_16BIT:
                wasapiFormat = WasapiFormat.Bit16;
                return true;
            case SampleFormat.SAMPLE_INT_24BIT:
                wasapiFormat = WasapiFormat.Bit24;
                return true;
            case SampleFormat.SAMPLE_INT_32BIT:
                wasapiFormat = WasapiFormat.Bit32;
                return true;
            default:
                wasapiFormat = WasapiFormat.Unknown;
                return false;
        }
    }

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
        string message,
        Exception innerException = null,
        IReadOnlyList<BassAudioBackendAttempt> attempts = null) =>
        new(
            request.Backend,
            request.Backend,
            stage,
            request.Device,
            session.ActualDevice,
            source,
            error,
            message,
            innerException,
            attempts);

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
    private readonly IBassMixerThreadNativeBoundary mixerThreadNative = new BassMixerThreadNativeBoundary();
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
    public bool SetMixerThreadCount(int mixerHandle, float threadCount) =>
        mixerThreadNative.SetMixerThreadCount(mixerHandle, threadCount);

    /// <inheritdoc />
    public bool GetMixerThreadCount(int mixerHandle, out float threadCount) =>
        mixerThreadNative.GetMixerThreadCount(mixerHandle, out threadCount);

    /// <inheritdoc />
    public Errors GetMixerThreadError() => GetCoreError();

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

    /// <inheritdoc />
    public int CheckWasapiFormat(
        int deviceIndex,
        int rate,
        int channels,
        WasapiInitFlags flags,
        out Errors error)
    {
        wasapiErrorOverride = null;
        int format = (int)BassWasapi.CheckFormat(deviceIndex, rate, channels, flags);
        error = format < 0 ? Bass.LastError : Errors.OK;
        return format;
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
