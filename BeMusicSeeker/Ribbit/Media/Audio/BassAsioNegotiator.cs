using System;
using System.Collections.Generic;
using System.Linq;
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
internal interface IAsioNegotiationNativeBoundary
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

    /// <summary>Checks whether the ASIO driver accepts a sample rate.</summary>
    bool CheckRate(double rate);

    /// <summary>Sets the ASIO driver sample rate.</summary>
    bool SetRate(double rate);

    /// <summary>Sets the output channel rate, where zero follows the driver rate.</summary>
    bool SetChannelRate(double rate);

    /// <summary>Sets the ASIO callback sample format.</summary>
    bool SetChannelFormat(AsioSampleFormat format);

    /// <summary>Gets the ASIO callback sample format accepted by the driver.</summary>
    AsioSampleFormat GetChannelFormat();

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

    private static readonly int[] StandardRates =
        [48000, 44100, 96000, 88200, 192000, 176400, 32000, 22050, 11025];

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
                "BASS_Init failed: " + BassNativeErrorFormatter.Format(error));
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
                + BassNativeErrorFormatter.Format(deviceInfosError));
        }
        if (devices.Length == 0)
        {
            throw Failure(
                request,
                session,
                "BASS_ASIO_GetDeviceInfos",
                "BASSASIO",
                null,
                "ASIO device not found.");
        }

        (int deviceIndex, string deviceFallbackReason) = SelectDevice(devices, request.Device);
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
                + BassNativeErrorFormatter.Format(deviceInfoError));
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
                "BASS_ASIO_Init failed: " + BassNativeErrorFormatter.Format(error));
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
                "BASS_ASIO_ChannelSetRate failed: " + BassNativeErrorFormatter.Format(error));
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
                + BassNativeErrorFormatter.Format(endpointFormatError));
        }

        SampleFormat endpointFormat = FromAsioFormat(endpointNativeFormat);
        if (endpointFormat == SampleFormat.UNKNOWN)
        {
            throw Failure(
                request,
                session,
                "BASS_ASIO_ChannelGetInfo",
                "BASSASIO",
                null,
                "ASIO endpoint format " + endpointNativeFormat
                + " is not supported by the Float32 PCM output path.");
        }

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
                "BASS_Mixer_StreamCreate failed: " + BassNativeErrorFormatter.Format(error));
        }
        session.MixerHandle = mixerHandle;
        session.TrackOutputHandle(mixerHandle);
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
                "BASS_ASIO_ChannelEnable failed: " + BassNativeErrorFormatter.Format(error));
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
                "BASS_ASIO_ChannelJoin failed: " + BassNativeErrorFormatter.Format(error));
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
                "BASS_ASIO_Start failed: " + BassNativeErrorFormatter.Format(error));
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
            callbackFormat: SampleFormat.SAMPLE_FLOAT_32BIT);
        session.NegotiationResult = result;
        return result;
    }

    /// <summary>Builds the deterministic, duplicate-free ASIO sample-rate candidate list.</summary>
    internal static IReadOnlyList<int> GetRateCandidates(SampleRate requestedRate, double driverCurrentRate)
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
        }
        Add(driverCurrentRate);
        foreach (int standardRate in StandardRates)
        {
            Add(standardRate);
        }
        return result.AsReadOnly();
    }

    private double NegotiateRate(
        BassAudioNegotiationRequest request,
        BassAudioSession session,
        double driverCurrentRate,
        List<BassAudioBackendAttempt> attempts,
        List<string> fallbackReasons)
    {
        foreach (int candidate in GetRateCandidates(request.Rate, driverCurrentRate))
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
                attempts.Add(new BassAudioBackendAttempt(
                    "BASS_ASIO_GetRate",
                    "BASSASIO",
                    null,
                    "accepted rate=" + actualRate));
                if (request.Rate != SampleRate.AUTO
                    && (int)request.Rate != (int)System.Math.Round(actualRate))
                {
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
            + BassNativeErrorFormatter.Format(finalError));
    }

    private SampleFormat NegotiateFormat(
        BassAudioNegotiationRequest request,
        BassAudioSession session,
        AsioSampleFormat endpointNativeFormat,
        List<BassAudioBackendAttempt> attempts,
        List<string> fallbackReasons)
    {
        bool needsDither = endpointNativeFormat != AsioSampleFormat.Float;
        AsioSampleFormat callbackFormat = needsDither
            ? (AsioSampleFormat)((int)AsioSampleFormat.Float | AsioFormatDitherFlag)
            : AsioSampleFormat.Float;
        if (!TrySetAndReadFormat(callbackFormat, attempts))
        {
            Errors error = native.GetAsioError();
            throw Failure(
                request,
                session,
                "BASS_ASIO_ChannelSetFormat",
                "BASSASIO",
                error,
                "ASIO Float32 callback format negotiation failed: "
                + BassNativeErrorFormatter.Format(error));
        }

        if (request.Format is SampleFormat.SAMPLE_INT_8BIT
            or SampleFormat.SAMPLE_INT_16BIT
            or SampleFormat.SAMPLE_INT_24BIT
            or SampleFormat.SAMPLE_INT_32BIT)
        {
            AddFallbackReason(
                fallbackReasons,
                "Requested ASIO format " + request.Format
                + " was normalized to a Float32 mixer and callback.");
        }

        return SampleFormat.SAMPLE_FLOAT_32BIT;
    }
    private bool TrySetAndReadFormat(
        AsioSampleFormat candidate,
        List<BassAudioBackendAttempt> attempts)
    {
        if (!native.SetChannelFormat(candidate))
        {
            Errors error = native.GetAsioError();
            attempts.Add(new BassAudioBackendAttempt(
                "BASS_ASIO_ChannelSetFormat",
                "BASSASIO",
                error,
                "rejected callbackFormat="
                + (AsioSampleFormat)((int)candidate & ~AsioFormatDitherFlag)
                + " dither=" + (((int)candidate & AsioFormatDitherFlag) != 0)));
            return false;
        }

        AsioSampleFormat actual = native.GetChannelFormat();
        bool accepted = actual == AsioSampleFormat.Float;
        attempts.Add(new BassAudioBackendAttempt(
            "BASS_ASIO_ChannelGetFormat",
            "BASSASIO",
            accepted ? null : native.GetAsioError(),
            (accepted ? "accepted" : "mismatched") + " callbackFormat=" + actual
            + " dither=" + (((int)candidate & AsioFormatDitherFlag) != 0)));
        return accepted;
    }

    private static (int DeviceIndex, string FallbackReason) SelectDevice(
        IReadOnlyList<BassAsioDeviceSnapshot> devices,
        BassAudioPlayer.DeviceDescriptor requestedDevice)
    {
        if (requestedDevice.Equals(default(BassAudioPlayer.DeviceDescriptor)))
        {
            return (0, null);
        }

        int exact = devices
            .Select((device, index) => new { device, index })
            .FirstOrDefault(entry =>
                requestedDevice.Name == entry.device.Name
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
            return (
                compatibleName,
                "Requested ASIO device identity was stale; a compatible name match was used.");
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

    private static SampleFormat FromAsioFormat(AsioSampleFormat format) => format switch
    {
        AsioSampleFormat.Float => SampleFormat.SAMPLE_FLOAT_32BIT,
        AsioSampleFormat.Bit16 => SampleFormat.SAMPLE_INT_16BIT,
        AsioSampleFormat.Bit24 => SampleFormat.SAMPLE_INT_24BIT,
        AsioSampleFormat.Bit32 => SampleFormat.SAMPLE_INT_32BIT,
        _ => SampleFormat.UNKNOWN
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
            BassAudioPlayer.DeviceDriver.ASIO,
            stage,
            request.Device,
            session.ActualDevice,
            source,
            error,
            message);
}

/// <summary>Forwards ASIO negotiation calls to ManagedBass.</summary>
internal sealed class BassAsioNegotiationNativeBoundary : IAsioNegotiationNativeBoundary
{
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
    public AsioSampleFormat GetChannelFormat() => BassAsio.ChannelGetFormat(false, 0);

    /// <inheritdoc />
    public bool TryGetEndpointNativeFormat(out AsioSampleFormat format, out Errors error)
    {
        format = AsioSampleFormat.Unknown;
        error = Errors.Unknown;
        try
        {
            if (!BassAsio.ChannelGetInfo(false, 0, out AsioChannelInfo info))
            {
                error = BassAsio.LastError;
                return false;
            }

            format = info.Format;
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
