using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using BeMusicSeeker.Models;
using ManagedBass;
using ManagedBass.Asio;
using ManagedBass.Mix;
using Ribbit.Media;

namespace Ribbit.Media.Audio;

/// <summary>Copies the ASIO device identity returned by the native boundary.</summary>
internal readonly record struct BassAsioDeviceSnapshot(string Name, string Driver);

/// <summary>
/// Exposes the native calls required to negotiate one ASIO output graph.
/// </summary>
internal interface IAsioNegotiationNativeBoundary : IBassMixerThreadNativeBoundary
{
    /// <summary>Initializes the decode-only BASS core device.</summary>
    bool InitializeCore();

    /// <summary>Gets the current BASS core device.</summary>
    int GetCoreDevice();

    /// <summary>Disables periodic BASS core updates for callback-driven output.</summary>
    void DisableCoreUpdatePeriod();

    /// <summary>Gets all ASIO device descriptors and captures the native failure on false.</summary>
    bool TryGetDeviceInfos(out BassAsioDeviceSnapshot[] deviceInfos, out Errors error);

    /// <summary>Gets one ASIO device descriptor and captures the native failure on false.</summary>
    bool TryGetDeviceInfo(
        int deviceIndex,
        out BassAsioDeviceSnapshot deviceInfo,
        out Errors error);

    /// <summary>Initializes one ASIO device.</summary>
    bool InitializeAsio(int deviceIndex);

    /// <summary>Gets the current ASIO device.</summary>
    int GetAsioDevice();

    /// <summary>Gets the ASIO driver's current sample rate.</summary>
    double GetRate();

    /// <summary>ASIO現在レートを読み取り、無効値の場合はAPIエラーを取得します。</summary>
    bool TryGetRate(out double rate, out Errors error);

    /// <summary>Checks whether the ASIO driver accepts a sample rate.</summary>
    bool CheckRate(double rate);

    /// <summary>Sets the ASIO driver sample rate.</summary>
    bool SetRate(double rate);

    /// <summary>Sets the output channel rate, where zero follows the driver rate.</summary>
    bool SetChannelRate(double rate);

    /// <summary>Sets the ASIO callback sample format.</summary>
    bool SetChannelFormat(AsioSampleFormat format);

    /// <summary>指定チャンネルの機器形式を読み取り、失敗時はネイティブエラーを取得します。</summary>
    bool TryGetChannelNativeFormat(
        bool input,
        int channel,
        out AsioSampleFormat format,
        out Errors error);

    /// <summary>callback形式のraw値を読み取り、API失敗時はネイティブエラーを取得します。</summary>
    bool TryGetChannelFormat(out AsioSampleFormat format, out Errors error);

    /// <summary>callback negotiationでは変化しないendpointのnative formatを読み取ります。</summary>
    bool TryGetEndpointNativeFormat(out AsioSampleFormat format, out Errors error);

    /// <summary>Creates the decode mixer that supplies the ASIO callback.</summary>
    int CreateMixer(int rate, int channels, BassFlags flags);

    /// <summary>Enables the first ASIO output channel with the existing managed callback.</summary>
    bool EnableOutputChannel(AsioProcedure callback);

    /// <summary>Joins another ASIO output channel to the first callback channel.</summary>
    bool JoinOutputChannel(int channel);

    /// <summary>Starts ASIO output.</summary>
    bool Start(int bufferLength, int threads);

    /// <summary>Gets ASIO output latency in samples.</summary>
    int GetOutputLatency();

    /// <summary>Gets the BASS core error immediately after a failed core or mixer call.</summary>
    Errors GetCoreError();

    /// <summary>Gets the BASSASIO error immediately after a failed ASIO call.</summary>
    Errors GetAsioError();
}

/// <summary>
/// Negotiates a byte-width-safe ASIO callback graph and records acquired ownership immediately.
/// </summary>
internal sealed class BassAsioNegotiator
{
    // ManagedBass.Asio 4.0.2の公開enumにBASS_ASIO_FORMAT_DITHERがないため、native定数を指定します。
    private const int AsioFormatDitherFlag = 0x100;

    // ManagedBass.Asio 4.0.2の公開enumにない32-bit容器PCM形式です。
    // 値はBASSASIO公式ヘッダーc/bassasio.hのBASS_ASIO_FORMAT_32BIT16～32BIT24に従います。
    // https://www.un4seen.com/files/bassasio14.zip
    private const int AsioFormat32Bit16 = 24;
    private const int AsioFormat32Bit18 = 25;
    private const int AsioFormat32Bit20 = 26;
    private const int AsioFormat32Bit24 = 27;

    private static readonly int[] StandardRates =
        [48000, 44100, 96000, 88200, 192000, 176400, 352800, 384000, 32000, 22050, 11025];

    private readonly IAsioNegotiationNativeBoundary native;

    /// <summary>Creates an ASIO negotiator over a replaceable native boundary.</summary>
    internal BassAsioNegotiator(IAsioNegotiationNativeBoundary native)
    {
        this.native = native ?? throw new ArgumentNullException(nameof(native));
    }

    /// <summary>
    /// Initializes one ASIO graph with a Float32 engine and callback, and returns the
    /// endpoint's native format separately from callback negotiation.
    /// </summary>
    internal BassAudioBackendResult Initialize(
        BassAudioNegotiationRequest request,
        BassAudioSession session,
        AsioProcedure callback,
        double initialGain = 1d)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(session);
        ArgumentNullException.ThrowIfNull(callback);
        if (request.Backend != BassAudioPlayer.DeviceDriver.ASIO)
        {
            throw new ArgumentException("The ASIO negotiator requires an ASIO request.", nameof(request));
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
                out BassAsioDeviceSnapshot[] devices,
                out Errors deviceInfosError))
        {
            throw Failure(
                request,
                session,
                "BASS_ASIO_GetDeviceInfos",
                "BASSASIO",
                deviceInfosError,
                "BASS_ASIO_GetDeviceInfos failed: "
                + BassNativeErrorFormatter.Format(deviceInfosError),
                attempts: attempts);
        }
        if (devices.Length == 0)
        {
            throw Failure(
                request,
                session,
                "BASS_ASIO_GetDeviceInfos",
                "BASSASIO",
                null,
                "ASIO device not found.",
                attempts: attempts);
        }

        (int deviceIndex, string deviceFallbackReason) = SelectDevice(
            devices,
            request.Device,
            request.RequiresExactSelection);
        if (deviceIndex < 0)
        {
            throw Failure(
                request,
                session,
                "BASS_ASIO_GetDeviceInfos",
                "BASSASIO",
                null,
                "The requested ASIO device is unavailable.",
                attempts: attempts);
        }
        if (!native.TryGetDeviceInfo(
                deviceIndex,
                out BassAsioDeviceSnapshot deviceInfo,
                out Errors deviceInfoError))
        {
            throw Failure(
                request,
                session,
                "BASS_ASIO_GetDeviceInfo",
                "BASSASIO",
                deviceInfoError,
                "BASS_ASIO_GetDeviceInfo failed: "
                + BassNativeErrorFormatter.Format(deviceInfoError),
                attempts: attempts);
        }
        var actualDevice = new BassAudioPlayer.DeviceDescriptor(deviceInfo.Name, deviceInfo.Driver);
        AddFallbackReason(fallbackReasons, deviceFallbackReason);
        session.ActualDevice = actualDevice;
        session.AsioDeviceIndex = deviceIndex;

        if (!native.InitializeAsio(deviceIndex))
        {
            Errors error = native.GetAsioError();
            throw Failure(
                request,
                session,
                "BASS_ASIO_Init",
                "BASSASIO",
                error,
                "BASS_ASIO_Init failed: " + BassNativeErrorFormatter.Format(error),
                attempts: attempts);
        }
        session.AsioInitialized = true;
        session.AsioDeviceIndex = native.GetAsioDevice();

        double driverCurrentRate = native.GetRate();
        double actualRate = NegotiateRate(
            request,
            session,
            driverCurrentRate,
            attempts,
            fallbackReasons);
        if (!native.SetChannelRate(0d))
        {
            Errors error = native.GetAsioError();
            throw Failure(
                request,
                session,
                "BASS_ASIO_ChannelSetRate",
                "BASSASIO",
                error,
                "BASS_ASIO_ChannelSetRate failed: " + BassNativeErrorFormatter.Format(error),
                attempts: attempts);
        }

        if (!native.TryGetEndpointNativeFormat(
                out AsioSampleFormat endpointNativeFormat,
                out Errors endpointFormatError))
        {
            throw Failure(
                request,
                session,
                "BASS_ASIO_ChannelGetInfo",
                "BASSASIO",
                endpointFormatError,
                "BASS_ASIO_ChannelGetInfo failed: "
                + BassNativeErrorFormatter.Format(endpointFormatError),
                attempts: attempts);
        }

        (SampleFormat endpointFormat, int containerBits, int effectiveBits) =
            DescribeNativeFormat(endpointNativeFormat);
        if (endpointFormat == SampleFormat.UNKNOWN)
        {
            throw Failure(
                request,
                session,
                "BASS_ASIO_ChannelGetInfo",
                "BASSASIO",
                null,
                "ASIO endpoint format " + endpointNativeFormat
                + " is not supported by the Float32 PCM output path.",
                attempts: attempts);
        }

        attempts.Add(new BassAudioBackendAttempt(
            "BASS_ASIO_ChannelGetInfo",
            "BASSASIO",
            null,
            "accepted nativeFormat=" + (int)endpointNativeFormat
            + " containerBits=" + containerBits
            + " effectiveBits=" + effectiveBits));

        SampleFormat engineFormat = NegotiateFormat(
            request,
            session,
            endpointNativeFormat,
            attempts,
            fallbackReasons);
        BassFlags mixerFlags = BassFlags.Float | BassFlags.Decode | BassFlags.MixerNonStop;
        int negotiatedRate = checked((int)System.Math.Round(actualRate, MidpointRounding.ToEven));
        int mixerHandle = native.CreateMixer(negotiatedRate, 2, mixerFlags);
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
        session.OutputProcessor = new AudioOutputProcessor(negotiatedRate, initialGain);
        session.CallbackPcmRenderer = new AudioPcmRenderer(mixerHandle, negotiatedRate, channelCount: 2);

        if (!native.EnableOutputChannel(callback))
        {
            Errors error = native.GetAsioError();
            throw Failure(
                request,
                session,
                "BASS_ASIO_ChannelEnable",
                "BASSASIO",
                error,
                "BASS_ASIO_ChannelEnable failed: " + BassNativeErrorFormatter.Format(error),
                attempts: attempts);
        }
        if (!native.JoinOutputChannel(1))
        {
            Errors error = native.GetAsioError();
            throw Failure(
                request,
                session,
                "BASS_ASIO_ChannelJoin",
                "BASSASIO",
                error,
                "BASS_ASIO_ChannelJoin failed: " + BassNativeErrorFormatter.Format(error),
                attempts: attempts);
        }

        int requestedBufferLength = (int)System.Math.Max(
            0d,
            request.LatencyMilliseconds * actualRate / 1000d);
        if (!native.Start(requestedBufferLength, 4))
        {
            Errors error = native.GetAsioError();
            throw Failure(
                request,
                session,
                "BASS_ASIO_Start",
                "BASSASIO",
                error,
                "BASS_ASIO_Start failed: " + BassNativeErrorFormatter.Format(error),
                attempts: attempts);
        }
        session.IsStarted = true;

        double latencyMilliseconds = native.GetOutputLatency() * 1000d / actualRate;
        var result = new BassAudioBackendResult(
            request,
            actualDevice,
            (SampleRate)(int)System.Math.Round(actualRate),
            engineFormat,
            endpointFormat,
            latencyMilliseconds,
            mixerHandle,
            attempts.AsReadOnly(),
            fallbackReasons.Count == 0 ? null : string.Join(" ", fallbackReasons),
            actualChannels: 2,
            callbackFormat: SampleFormat.SAMPLE_FLOAT_32BIT,
            endpointContainerBits: containerBits,
            endpointEffectiveBits: effectiveBits);
        session.NegotiationResult = result;
        return result;
    }

    /// <summary>Builds the deterministic, duplicate-free ASIO sample-rate candidate list.</summary>
    internal static IReadOnlyList<int> GetRateCandidates(
        SampleRate requestedRate,
        double driverCurrentRate,
        bool allowExplicitRateFallback = true)
    {
        var result = new List<int>();
        var seen = new HashSet<int>();

        void Add(double rate)
        {
            int rounded = (int)System.Math.Round(rate);
            if (rounded > 0 && seen.Add(rounded))
            {
                result.Add(rounded);
            }
        }

        if (requestedRate != SampleRate.AUTO)
        {
            Add((int)requestedRate);
            if (!allowExplicitRateFallback)
            {
                return result.AsReadOnly();
            }
        }
        Add(driverCurrentRate);
        foreach (int standardRate in StandardRates)
        {
            Add(standardRate);
        }
        return result.AsReadOnly();
    }

    /// <summary>選択済みASIOドライバーの能力を、rateやcallback形式を変更せずに照会します。取消はnative初期化・候補検査の間で確認します。</summary>
    internal AudioDeviceCapabilityResult QueryCapabilities(
        AudioDeviceCapabilityRequest request,
        BassAudioSession session, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        cancellationToken.ThrowIfCancellationRequested();
        ArgumentNullException.ThrowIfNull(session);
        if (request.Backend != AudioDriver.Asio)
        {
            throw new ArgumentException("ASIO capabilities require an ASIO request.", nameof(request));
        }

        var attempts = new List<BassAudioBackendAttempt>();
        if (!native.InitializeCore())
        {
            Errors error = native.GetCoreError();
            return CapabilityFailure(request, "BASS_Init", "BASS", error, attempts);
        }
        session.CoreInitialized = true;
        session.CoreDeviceIndex = native.GetCoreDevice();
        native.DisableCoreUpdatePeriod();

        if (!native.TryGetDeviceInfos(
                out BassAsioDeviceSnapshot[] devices,
                out Errors deviceInfosError))
        {
            return CapabilityFailure(
                request,
                "BASS_ASIO_GetDeviceInfos",
                "BASSASIO",
                deviceInfosError,
                attempts);
        }

        int deviceIndex = string.IsNullOrWhiteSpace(request.DeviceIdentity)
            ? (devices.Length == 0 ? -1 : 0)
            : Array.FindIndex(devices, device => string.Equals(
                device.Driver,
                request.DeviceIdentity,
                StringComparison.Ordinal));
        if (deviceIndex < 0)
        {
            attempts.Add(new BassAudioBackendAttempt(
                "audio device selection",
                null,
                null,
                "unavailable identity=" + (request.DeviceIdentity ?? "Default")));
            return CapabilityFailure(request, "audio device selection", null, null, attempts);
        }

        if (!native.TryGetDeviceInfo(
                deviceIndex,
                out BassAsioDeviceSnapshot deviceInfo,
                out Errors deviceInfoError))
        {
            return CapabilityFailure(
                request,
                "BASS_ASIO_GetDeviceInfo",
                "BASSASIO",
                deviceInfoError,
                attempts);
        }
        session.ActualDevice = new BassAudioPlayer.DeviceDescriptor(deviceInfo.Name, deviceInfo.Driver);
        session.AsioDeviceIndex = deviceIndex;
        cancellationToken.ThrowIfCancellationRequested();

        if (!native.InitializeAsio(deviceIndex))
        {
            Errors error = native.GetAsioError();
            return CapabilityFailure(request, "BASS_ASIO_Init", "BASSASIO", error, attempts);
        }
        session.AsioInitialized = true;
        session.AsioDeviceIndex = native.GetAsioDevice();

        if (!native.TryGetRate(out double driverRate, out Errors rateError))
        {
            return CapabilityFailure(request, "BASS_ASIO_GetRate", "BASSASIO", rateError, attempts);
        }
        attempts.Add(new BassAudioBackendAttempt(
            "BASS_ASIO_GetRate",
            "BASSASIO",
            null,
            "readback rate=" + driverRate));

        IReadOnlyList<int> candidates = GetCapabilityRateCandidates(request.SavedRate, driverRate);
        var supportedRates = new List<SampleRate>();
        foreach (int candidate in candidates)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (native.CheckRate(candidate))
            {
                supportedRates.Add((SampleRate)candidate);
                attempts.Add(new BassAudioBackendAttempt(
                    "BASS_ASIO_CheckRate",
                    "BASSASIO",
                    null,
                    "supported rate=" + candidate));
                continue;
            }

            Errors error = native.GetAsioError();
            attempts.Add(new BassAudioBackendAttempt(
                "BASS_ASIO_CheckRate",
                "BASSASIO",
                error,
                "unsupported rate=" + candidate));
            if (error != Errors.NotAvailable)
            {
                return CapabilityFailure(request, "BASS_ASIO_CheckRate", "BASSASIO", error, attempts);
            }
        }

        if (!native.TryGetChannelNativeFormat(false, 0, out AsioSampleFormat leftFormat, out Errors leftError))
        {
            attempts.Add(new BassAudioBackendAttempt(
                "BASS_ASIO_ChannelGetInfo",
                "BASSASIO",
                leftError,
                "failed outputChannel=0"));
            return CapabilityFailure(request, "BASS_ASIO_ChannelGetInfo", "BASSASIO", leftError, attempts);
        }
        if (!native.TryGetChannelNativeFormat(false, 1, out AsioSampleFormat rightFormat, out Errors rightError))
        {
            attempts.Add(new BassAudioBackendAttempt(
                "BASS_ASIO_ChannelGetInfo",
                "BASSASIO",
                rightError,
                "failed outputChannel=1"));
            return CapabilityFailure(request, "BASS_ASIO_ChannelGetInfo", "BASSASIO", rightError, attempts);
        }

        (SampleFormat leftSampleFormat, int leftContainerBits, int leftEffectiveBits) = DescribeNativeFormat(leftFormat);
        (SampleFormat rightSampleFormat, int rightContainerBits, int rightEffectiveBits) = DescribeNativeFormat(rightFormat);
        if (leftSampleFormat == SampleFormat.UNKNOWN || rightSampleFormat == SampleFormat.UNKNOWN)
        {
            attempts.Add(new BassAudioBackendAttempt(
                "BASS_ASIO_ChannelGetInfo",
                "BASSASIO",
                null,
                "unsupported native output format leftRaw=" + (int)leftFormat
                + " rightRaw=" + (int)rightFormat));
            return CapabilityFailure(request, "BASS_ASIO_ChannelGetInfo", "BASSASIO", null, attempts);
        }
        attempts.Add(new BassAudioBackendAttempt(
            "BASS_ASIO_ChannelGetInfo",
            "BASSASIO",
            null,
            "readback leftRaw=" + (int)leftFormat
            + " rightRaw=" + (int)rightFormat
            + " leftContainerBits=" + leftContainerBits
            + " leftEffectiveBits=" + leftEffectiveBits
            + " rightContainerBits=" + rightContainerBits
            + " rightEffectiveBits=" + rightEffectiveBits));

        return new AudioDeviceCapabilityResult(
            request.Backend,
            deviceInfo.Driver,
            deviceInfo.Name,
            supportedRates.Count == 0
                ? AudioDeviceCapabilityStatus.Unsupported
                : AudioDeviceCapabilityStatus.Available,
            supportedRates,
            endpointFormat: leftSampleFormat,
            rightEndpointFormat: rightSampleFormat,
            endpointContainerBits: leftContainerBits,
            endpointEffectiveBits: leftEffectiveBits,
            rightEndpointContainerBits: rightContainerBits,
            rightEndpointEffectiveBits: rightEffectiveBits,
            endpointChannels: 2,
            attempts: attempts);
    }

    private static IReadOnlyList<int> GetCapabilityRateCandidates(
        SampleRate savedRate,
        double driverCurrentRate)
    {
        var result = new List<int>();
        var seen = new HashSet<int>();

        void Add(double rate)
        {
            int rounded = (int)System.Math.Round(rate);
            if (rounded > 0 && seen.Add(rounded))
            {
                result.Add(rounded);
            }
        }

        Add(driverCurrentRate);
        Add((int)savedRate);
        foreach (int rate in StandardRates)
        {
            Add(rate);
        }
        return result.AsReadOnly();
    }

    private static AudioDeviceCapabilityResult CapabilityFailure(
        AudioDeviceCapabilityRequest request,
        string stage,
        string nativeErrorSource,
        Errors? nativeErrorCode,
        IReadOnlyList<BassAudioBackendAttempt> attempts) =>
        new(
            request.Backend,
            request.DeviceIdentity,
            request.DeviceName,
            AudioDeviceCapabilityStatus.Failed,
            failureStage: stage,
            nativeErrorSource: nativeErrorSource,
            nativeErrorCode: nativeErrorCode,
            attempts: attempts);

    private double NegotiateRate(
        BassAudioNegotiationRequest request,
        BassAudioSession session,
        double driverCurrentRate,
        List<BassAudioBackendAttempt> attempts,
        List<string> fallbackReasons)
    {
        foreach (int candidate in GetRateCandidates(
                     request.Rate,
                     driverCurrentRate,
                     allowExplicitRateFallback: !request.RequiresExactSelection))
        {
            if (!native.CheckRate(candidate))
            {
                Errors error = native.GetAsioError();
                attempts.Add(new BassAudioBackendAttempt(
                    "BASS_ASIO_CheckRate",
                    "BASSASIO",
                    error,
                    "rejected rate=" + candidate));
                continue;
            }
            if (!native.SetRate(candidate))
            {
                Errors error = native.GetAsioError();
                attempts.Add(new BassAudioBackendAttempt(
                    "BASS_ASIO_SetRate",
                    "BASSASIO",
                    error,
                    "rejected rate=" + candidate));
                continue;
            }

            double actualRate = native.GetRate();
            if (actualRate > 0d)
            {
                bool explicitRateMismatch = request.Rate != SampleRate.AUTO
                    && (int)request.Rate != (int)System.Math.Round(actualRate);
                attempts.Add(new BassAudioBackendAttempt(
                    "BASS_ASIO_GetRate",
                    "BASSASIO",
                    null,
                    explicitRateMismatch
                        ? "mismatched requestedRate=" + (int)request.Rate + " actualRate=" + actualRate
                        : "accepted rate=" + actualRate));
                if (explicitRateMismatch)
                {
                    if (request.RequiresExactSelection)
                    {
                        throw Failure(
                            request,
                            session,
                            "BASS_ASIO_GetRate",
                            "BASSASIO",
                            null,
                            "Requested ASIO sample rate " + (int)request.Rate
                            + " was read back as " + actualRate + ".",
                            attempts: attempts);
                    }

                    string rejectedErrors = string.Join(
                        ", ",
                        attempts
                            .Where(attempt =>
                                attempt.Stage is "BASS_ASIO_CheckRate" or "BASS_ASIO_SetRate"
                                && attempt.NativeErrorCode.HasValue)
                            .Select(attempt =>
                                attempt.NativeErrorSource + "/"
                                + BassNativeErrorFormatter.Format(attempt.NativeErrorCode)));
                    AddFallbackReason(
                        fallbackReasons,
                        "Requested ASIO rate " + (int)request.Rate
                        + " was normalized to " + actualRate
                        + (string.IsNullOrEmpty(rejectedErrors)
                            ? "."
                            : " after " + rejectedErrors + "."));
                }
                return actualRate;
            }
        }

        Errors finalError = native.GetAsioError();
        throw Failure(
            request,
            session,
            "BASS_ASIO_SetRate",
            "BASSASIO",
            finalError,
            "BASS_ASIO sample-rate negotiation failed: "
            + BassNativeErrorFormatter.Format(finalError),
            attempts: attempts);
    }

    private SampleFormat NegotiateFormat(
        BassAudioNegotiationRequest request,
        BassAudioSession session,
        AsioSampleFormat endpointNativeFormat,
        List<BassAudioBackendAttempt> attempts,
        List<string> fallbackReasons)
    {
        SampleFormat endpointFormat = DescribeNativeFormat(endpointNativeFormat).EndpointFormat;
        bool needsDither = endpointNativeFormat != AsioSampleFormat.Float;
        AsioSampleFormat callbackFormat = needsDither
            ? (AsioSampleFormat)((int)AsioSampleFormat.Float | AsioFormatDitherFlag)
            : AsioSampleFormat.Float;
        if (!TrySetAndReadFormat(
                callbackFormat,
                attempts,
                out string failureStage,
                out Errors? failureError))
        {
            throw Failure(
                request,
                session,
                failureStage,
                "BASSASIO",
                failureError,
                "ASIO Float32 callback format negotiation failed. requestedRaw=0x"
                + ((int)callbackFormat).ToString("X")
                + " nativeError=" + BassNativeErrorFormatter.Format(failureError),
                attempts: attempts);
        }

        if (request.Format != SampleFormat.AUTO && request.Format != endpointFormat)
        {
            if (request.RequiresExactSelection)
            {
                throw Failure(
                    request,
                    session,
                    "BASS_ASIO_ChannelGetInfo",
                    "BASSASIO",
                    null,
                    "Requested ASIO endpoint format " + request.Format
                    + " differs from native endpoint format " + endpointFormat + ".",
                    attempts: attempts);
            }
            AddFallbackReason(
                fallbackReasons,
                "Requested ASIO endpoint format " + request.Format
                + " differs from native endpoint format " + endpointFormat + ".");
        }

        return SampleFormat.SAMPLE_FLOAT_32BIT;
    }
    private bool TrySetAndReadFormat(
        AsioSampleFormat candidate,
        List<BassAudioBackendAttempt> attempts,
        out string failureStage,
        out Errors? failureError)
    {
        failureStage = "BASS_ASIO_ChannelSetFormat";
        failureError = null;
        if (!native.SetChannelFormat(candidate))
        {
            Errors error = native.GetAsioError();
            attempts.Add(new BassAudioBackendAttempt(
                "BASS_ASIO_ChannelSetFormat",
                "BASSASIO",
                error,
                "rejected requestedRaw=0x" + ((int)candidate).ToString("X")));
            failureError = error;
            return false;
        }

        failureStage = "BASS_ASIO_ChannelGetFormat";
        if (!native.TryGetChannelFormat(out AsioSampleFormat actual, out Errors getFormatError))
        {
            attempts.Add(new BassAudioBackendAttempt(
                "BASS_ASIO_ChannelGetFormat",
                "BASSASIO",
                getFormatError,
                "failed requestedRaw=0x" + ((int)candidate).ToString("X")
                + " actualRaw=0x" + ((int)actual).ToString("X")));
            failureError = getFormatError;
            return false;
        }

        bool accepted = (int)actual == (int)candidate;
        attempts.Add(new BassAudioBackendAttempt(
            "BASS_ASIO_ChannelGetFormat",
            nativeErrorSource: null,
            nativeErrorCode: null,
            outcome: (accepted ? "accepted" : "mismatched") + " requestedRaw=0x"
                + ((int)candidate).ToString("X")
                + " actualRaw=0x" + ((int)actual).ToString("X")));
        return accepted;
    }

    private static (int DeviceIndex, string FallbackReason) SelectDevice(
        IReadOnlyList<BassAsioDeviceSnapshot> devices,
        BassAudioPlayer.DeviceDescriptor requestedDevice,
        bool requireExactDevice)
    {
        if (requestedDevice.Equals(default(BassAudioPlayer.DeviceDescriptor)))
        {
            return (0, null);
        }

        int exact = devices
            .Select((device, index) => new { device, index })
            .FirstOrDefault(entry =>
                !string.IsNullOrWhiteSpace(requestedDevice.Driver)
                && requestedDevice.Driver == entry.device.Driver)
            ?.index ?? -1;
        if (exact >= 0)
        {
            return (exact, null);
        }

        int compatibleName = devices
            .Select((device, index) => new { device, index })
            .FirstOrDefault(entry => requestedDevice.Name == entry.device.Name)
            ?.index ?? -1;
        if (compatibleName >= 0)
        {
            if (requireExactDevice)
            {
                return (-1, null);
            }
            return (
                compatibleName,
                "Requested ASIO device identity was stale; a compatible name match was used.");
        }

        if (requireExactDevice)
        {
            return (-1, null);
        }
        return (
            0,
            "Requested ASIO device was unavailable; the backend default device was used.");
    }

    private static void AddFallbackReason(List<string> reasons, string reason)
    {
        if (!string.IsNullOrWhiteSpace(reason))
        {
            reasons.Add(reason);
        }
    }

    private static (SampleFormat EndpointFormat, int ContainerBits, int EffectiveBits) DescribeNativeFormat(
        AsioSampleFormat format) => format switch
        {
            AsioSampleFormat.Float => (SampleFormat.SAMPLE_FLOAT_32BIT, 32, 32),
            AsioSampleFormat.Bit16 => (SampleFormat.SAMPLE_INT_16BIT, 16, 16),
            AsioSampleFormat.Bit24 => (SampleFormat.SAMPLE_INT_24BIT, 24, 24),
            AsioSampleFormat.Bit32 => (SampleFormat.SAMPLE_INT_32BIT, 32, 32),
            (AsioSampleFormat)AsioFormat32Bit16 => (SampleFormat.SAMPLE_INT_32BIT, 32, 16),
            (AsioSampleFormat)AsioFormat32Bit18 => (SampleFormat.SAMPLE_INT_32BIT, 32, 18),
            (AsioSampleFormat)AsioFormat32Bit20 => (SampleFormat.SAMPLE_INT_32BIT, 32, 20),
            (AsioSampleFormat)AsioFormat32Bit24 => (SampleFormat.SAMPLE_INT_32BIT, 32, 24),
            _ => (SampleFormat.UNKNOWN, 0, 0)
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
            BassAudioPlayer.DeviceDriver.ASIO,
            stage,
            request.Device,
            session.ActualDevice,
            source,
            error,
            message,
            innerException,
            attempts);
}

/// <summary>Forwards ASIO negotiation calls to ManagedBass.</summary>
internal sealed class BassAsioNegotiationNativeBoundary : IAsioNegotiationNativeBoundary
{
    private readonly IBassMixerThreadNativeBoundary mixerThreadNative = new BassMixerThreadNativeBoundary();
    private Errors? coreErrorOverride;
    private Errors? asioErrorOverride;

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
        out BassAsioDeviceSnapshot[] deviceInfos,
        out Errors error)
    {
        if (!BassAudioDeviceEnumeration.TryEnumerate(
                TryReadDeviceInfo,
                out AsioDeviceInfo[] nativeDevices,
                out error))
        {
            deviceInfos = [];
            return false;
        }

        deviceInfos = Array.ConvertAll(nativeDevices, ToSnapshot);
        return true;
    }

    private static bool TryReadDeviceInfo(
        int index,
        out AsioDeviceInfo deviceInfo,
        out Errors error)
    {
        if (BassAsio.GetDeviceInfo(index, out deviceInfo))
        {
            error = Errors.OK;
            return true;
        }

        error = BassAsio.LastError;
        return false;
    }

    /// <inheritdoc />
    public bool TryGetDeviceInfo(
        int deviceIndex,
        out BassAsioDeviceSnapshot deviceInfo,
        out Errors error)
    {
        deviceInfo = default;
        error = Errors.Unknown;
        try
        {
            if (!BassAsio.GetDeviceInfo(deviceIndex, out AsioDeviceInfo info))
            {
                error = BassAsio.LastError;
                return false;
            }

            deviceInfo = ToSnapshot(info);
            error = Errors.OK;
            return true;
        }
        catch (BassException exception)
        {
            error = exception.ErrorCode;
            return false;
        }
    }

    private static BassAsioDeviceSnapshot ToSnapshot(AsioDeviceInfo info) =>
        new(info.Name, info.Driver);

    /// <inheritdoc />
    public bool InitializeAsio(int deviceIndex)
    {
        asioErrorOverride = null;
        return BassAsio.Init(deviceIndex, AsioInitFlags.Thread);
    }

    /// <inheritdoc />
    public int GetAsioDevice() => BassAsio.CurrentDevice;

    /// <inheritdoc />
    public double GetRate() => BassAsio.Rate;

    /// <inheritdoc />
    public bool TryGetRate(out double rate, out Errors error)
    {
        asioErrorOverride = null;
        rate = 0d;
        try
        {
            rate = BassAsio.Rate;
            if (rate <= 0d || double.IsNaN(rate) || double.IsInfinity(rate))
            {
                error = BassAsio.LastError;
                return false;
            }

            error = Errors.OK;
            return true;
        }
        catch (BassException exception)
        {
            asioErrorOverride = exception.ErrorCode;
            error = exception.ErrorCode;
            return false;
        }
    }

    /// <inheritdoc />
    public bool CheckRate(double rate)
    {
        asioErrorOverride = null;
        return BassAsio.CheckRate(rate);
    }

    /// <inheritdoc />
    public bool SetRate(double rate)
    {
        asioErrorOverride = null;
        try
        {
            BassAsio.Rate = rate;
            return true;
        }
        catch (BassException exception)
        {
            asioErrorOverride = exception.ErrorCode;
            return false;
        }
    }

    /// <inheritdoc />
    public bool SetChannelRate(double rate)
    {
        asioErrorOverride = null;
        return BassAsio.ChannelSetRate(false, 0, rate);
    }

    /// <inheritdoc />
    public bool SetChannelFormat(AsioSampleFormat format)
    {
        asioErrorOverride = null;
        return BassAsio.ChannelSetFormat(false, 0, format);
    }

    /// <inheritdoc />
    public bool TryGetChannelFormat(out AsioSampleFormat format, out Errors error)
    {
        asioErrorOverride = null;
        format = AsioSampleFormat.Unknown;
        try
        {
            format = BassAsio.ChannelGetFormat(false, 0);
            if (format == AsioSampleFormat.Unknown)
            {
                error = BassAsio.LastError;
                return false;
            }

            error = Errors.OK;
            return true;
        }
        catch (BassException exception)
        {
            asioErrorOverride = exception.ErrorCode;
            error = exception.ErrorCode;
            return false;
        }
    }

    /// <inheritdoc />
    public bool TryGetEndpointNativeFormat(out AsioSampleFormat format, out Errors error)
        => TryGetChannelNativeFormat(false, 0, out format, out error);

    /// <inheritdoc />
    public bool TryGetChannelNativeFormat(
        bool input,
        int channel,
        out AsioSampleFormat format,
        out Errors error)
    {
        format = AsioSampleFormat.Unknown;
        error = Errors.Unknown;
        try
        {
            if (!BassAsio.ChannelGetInfo(input, channel, out AsioChannelInfo info))
            {
                error = BassAsio.LastError;
                return false;
            }

            format = info.Format;
            if (format == AsioSampleFormat.Unknown)
            {
                error = BassAsio.LastError;
                return false;
            }
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
    public int CreateMixer(int rate, int channels, BassFlags flags)
    {
        coreErrorOverride = null;
        return BassMix.CreateMixerStream(rate, channels, flags);
    }

    /// <inheritdoc />
    public bool EnableOutputChannel(AsioProcedure callback)
    {
        // Keep the callback boundary on the ASIO channel API and guarantee its byte width by
        // negotiating the mixer format above.
        asioErrorOverride = null;
        return BassAsio.ChannelEnable(false, 0, callback, IntPtr.Zero);
    }

    /// <inheritdoc />
    public bool JoinOutputChannel(int channel)
    {
        asioErrorOverride = null;
        return BassAsio.ChannelJoin(false, channel, 0);
    }

    /// <inheritdoc />
    public bool Start(int bufferLength, int threads)
    {
        asioErrorOverride = null;
        return BassAsio.Start(bufferLength, threads);
    }

    /// <inheritdoc />
    public int GetOutputLatency() => BassAsio.GetLatency(false);

    /// <inheritdoc />
    public Errors GetCoreError() => coreErrorOverride ?? Bass.LastError;

    /// <inheritdoc />
    public Errors GetAsioError() => asioErrorOverride ?? BassAsio.LastError;
}
