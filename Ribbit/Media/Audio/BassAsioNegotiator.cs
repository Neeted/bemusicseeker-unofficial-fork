using System;
using System.Collections.Generic;
using System.Linq;
using Ribbit.Media;
using Un4seen.Bass;
using Un4seen.Bass.AddOn.Mix;
using Un4seen.BassAsio;

namespace Ribbit.Media.Audio;

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

    /// <summary>Gets all ASIO device descriptors.</summary>
    BASS_ASIO_DEVICEINFO[] GetDeviceInfos();

    /// <summary>Gets one ASIO device descriptor.</summary>
    BASS_ASIO_DEVICEINFO GetDeviceInfo(int deviceIndex);

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
    bool SetChannelFormat(BASSASIOFormat format);

    /// <summary>Gets the ASIO callback sample format accepted by the driver.</summary>
    BASSASIOFormat GetChannelFormat();

    /// <summary>Creates the decode mixer that supplies the ASIO callback.</summary>
    int CreateMixer(int rate, int channels, BASSFlag flags);

    /// <summary>Enables the first ASIO output channel with the existing managed callback.</summary>
    bool EnableOutputChannel(ASIOPROC callback);

    /// <summary>Joins another ASIO output channel to the first callback channel.</summary>
    bool JoinOutputChannel(int channel);

    /// <summary>Starts ASIO output.</summary>
    bool Start(int bufferLength, int threads);

    /// <summary>Gets ASIO output latency in samples.</summary>
    int GetOutputLatency();

    /// <summary>Gets the BASS core error immediately after a failed core or mixer call.</summary>
    BASSError GetCoreError();

    /// <summary>Gets the BASSASIO error immediately after a failed ASIO call.</summary>
    BASSError GetAsioError();
}

/// <summary>
/// Negotiates a byte-width-safe ASIO callback graph and records acquired ownership immediately.
/// </summary>
internal sealed class BassAsioNegotiator
{
    private static readonly int[] StandardRates =
        [48000, 44100, 96000, 88200, 192000, 176400, 32000, 22050, 11025];

    private readonly IAsioNegotiationNativeBoundary native;

    /// <summary>Creates an ASIO negotiator over a replaceable native boundary.</summary>
    internal BassAsioNegotiator(IAsioNegotiationNativeBoundary native)
    {
        this.native = native ?? throw new ArgumentNullException(nameof(native));
    }

    /// <summary>
    /// Initializes one ASIO graph using Float32 end-to-end, or Int16 end-to-end when Float32
    /// is unavailable, and returns values read back from the driver.
    /// </summary>
    internal BassAudioBackendResult Initialize(
        BassAudioNegotiationRequest request,
        BassAudioSession session,
        ASIOPROC callback)
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
            BASSError error = native.GetCoreError();
            throw Failure(request, session, "BASS_Init", "BASS", error, "BASS_Init failed: " + error);
        }
        session.CoreInitialized = true;
        session.CoreDeviceIndex = native.GetCoreDevice();
        native.DisableCoreUpdatePeriod();

        BASS_ASIO_DEVICEINFO[] devices = native.GetDeviceInfos();
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
        BASS_ASIO_DEVICEINFO deviceInfo = native.GetDeviceInfo(deviceIndex);
        var actualDevice = new BassAudioPlayer.DeviceDescriptor(deviceInfo.name, deviceInfo.driver);
        AddFallbackReason(fallbackReasons, deviceFallbackReason);
        session.ActualDevice = actualDevice;
        session.AsioDeviceIndex = deviceIndex;

        if (!native.InitializeAsio(deviceIndex))
        {
            BASSError error = native.GetAsioError();
            throw Failure(
                request,
                session,
                "BASS_ASIO_Init",
                "BASSASIO",
                error,
                "BASS_ASIO_Init failed: " + error);
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
            BASSError error = native.GetAsioError();
            throw Failure(
                request,
                session,
                "BASS_ASIO_ChannelSetRate",
                "BASSASIO",
                error,
                "BASS_ASIO_ChannelSetRate failed: " + error);
        }

        (SampleFormat engineFormat, BASSASIOFormat asioFormat) =
            NegotiateFormat(request, session, attempts, fallbackReasons);
        BASSFlag mixerFlags = BASSFlag.BASS_STREAM_DECODE | BASSFlag.BASS_MIXER_NONSTOP;
        if (engineFormat == SampleFormat.SAMPLE_FLOAT_32BIT)
        {
            mixerFlags |= BASSFlag.BASS_SAMPLE_FLOAT;
        }

        int mixerHandle = native.CreateMixer((int)System.Math.Round(actualRate), 2, mixerFlags);
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

        if (!native.EnableOutputChannel(callback))
        {
            BASSError error = native.GetAsioError();
            throw Failure(
                request,
                session,
                "BASS_ASIO_ChannelEnable",
                "BASSASIO",
                error,
                "BASS_ASIO_ChannelEnable failed: " + error);
        }
        if (!native.JoinOutputChannel(1))
        {
            BASSError error = native.GetAsioError();
            throw Failure(
                request,
                session,
                "BASS_ASIO_ChannelJoin",
                "BASSASIO",
                error,
                "BASS_ASIO_ChannelJoin failed: " + error);
        }

        int requestedBufferLength = (int)System.Math.Max(
            0d,
            request.LatencyMilliseconds * actualRate / 1000d);
        if (!native.Start(requestedBufferLength, 4))
        {
            BASSError error = native.GetAsioError();
            throw Failure(
                request,
                session,
                "BASS_ASIO_Start",
                "BASSASIO",
                error,
                "BASS_ASIO_Start failed: " + error);
        }
        session.IsStarted = true;

        double latencyMilliseconds = native.GetOutputLatency() * 1000d / actualRate;
        SampleFormat endpointFormat = FromAsioFormat(asioFormat);
        var result = new BassAudioBackendResult(
            request,
            actualDevice,
            (SampleRate)(int)System.Math.Round(actualRate),
            engineFormat,
            endpointFormat,
            latencyMilliseconds,
            mixerHandle,
            attempts.AsReadOnly(),
            fallbackReasons.Count == 0 ? null : string.Join(" ", fallbackReasons));
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
                BASSError error = native.GetAsioError();
                attempts.Add(new BassAudioBackendAttempt(
                    "BASS_ASIO_CheckRate",
                    "BASSASIO",
                    error,
                    "rejected rate=" + candidate));
                continue;
            }
            if (!native.SetRate(candidate))
            {
                BASSError error = native.GetAsioError();
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
                                attempt.NativeErrorSource + "/" + attempt.NativeErrorCode));
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

        BASSError finalError = native.GetAsioError();
        throw Failure(
            request,
            session,
            "BASS_ASIO_SetRate",
            "BASSASIO",
            finalError,
            "BASS_ASIO sample-rate negotiation failed: " + finalError);
    }

    private (SampleFormat EngineFormat, BASSASIOFormat AsioFormat)
        NegotiateFormat(
            BassAudioNegotiationRequest request,
            BassAudioSession session,
            List<BassAudioBackendAttempt> attempts,
            List<string> fallbackReasons)
    {
        if (TrySetAndReadFormat(BASSASIOFormat.BASS_ASIO_FORMAT_FLOAT, attempts))
        {
            if (request.Format is SampleFormat.SAMPLE_INT_8BIT
                or SampleFormat.SAMPLE_INT_16BIT
                or SampleFormat.SAMPLE_INT_24BIT
                or SampleFormat.SAMPLE_INT_32BIT)
            {
                AddFallbackReason(
                    fallbackReasons,
                    "Requested ASIO format " + request.Format
                    + " was normalized to Float32 to keep mixer and callback byte widths equal.");
            }
            return (
                SampleFormat.SAMPLE_FLOAT_32BIT,
                BASSASIOFormat.BASS_ASIO_FORMAT_FLOAT);
        }

        if (TrySetAndReadFormat(BASSASIOFormat.BASS_ASIO_FORMAT_16BIT, attempts))
        {
            BassAudioBackendAttempt floatFailure = attempts.First(attempt =>
                attempt.Stage is "BASS_ASIO_ChannelSetFormat" or "BASS_ASIO_ChannelGetFormat");
            AddFallbackReason(
                fallbackReasons,
                "ASIO Float32 callback format was unavailable (nativeErrorSource="
                + floatFailure.NativeErrorSource
                + " nativeErrorCode=" + floatFailure.NativeErrorCode
                + "); mixer and callback both use Int16.");
            return (
                SampleFormat.SAMPLE_INT_16BIT,
                BASSASIOFormat.BASS_ASIO_FORMAT_16BIT);
        }

        BASSError error = native.GetAsioError();
        throw Failure(
            request,
            session,
            "BASS_ASIO_ChannelSetFormat",
            "BASSASIO",
            error,
            "BASS_ASIO callback format negotiation failed: " + error);
    }

    private bool TrySetAndReadFormat(
        BASSASIOFormat candidate,
        List<BassAudioBackendAttempt> attempts)
    {
        if (!native.SetChannelFormat(candidate))
        {
            BASSError error = native.GetAsioError();
            attempts.Add(new BassAudioBackendAttempt(
                "BASS_ASIO_ChannelSetFormat",
                "BASSASIO",
                error,
                "rejected format=" + candidate));
            return false;
        }

        BASSASIOFormat actual = native.GetChannelFormat();
        bool accepted = actual == candidate;
        attempts.Add(new BassAudioBackendAttempt(
            "BASS_ASIO_ChannelGetFormat",
            "BASSASIO",
            accepted ? null : native.GetAsioError(),
            (accepted ? "accepted" : "mismatched") + " format=" + actual));
        return accepted;
    }

    private static (int DeviceIndex, string FallbackReason) SelectDevice(
        IReadOnlyList<BASS_ASIO_DEVICEINFO> devices,
        BassAudioPlayer.DeviceDescriptor requestedDevice)
    {
        if (requestedDevice.Equals(default(BassAudioPlayer.DeviceDescriptor)))
        {
            return (0, null);
        }

        int exact = devices
            .Select((device, index) => new { device, index })
            .FirstOrDefault(entry =>
                requestedDevice.Name == entry.device.name
                && requestedDevice.Driver == entry.device.driver)
            ?.index ?? -1;
        if (exact >= 0)
        {
            return (exact, null);
        }

        int compatibleName = devices
            .Select((device, index) => new { device, index })
            .FirstOrDefault(entry => requestedDevice.Name == entry.device.name)
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

    private static SampleFormat FromAsioFormat(BASSASIOFormat format) => format switch
    {
        BASSASIOFormat.BASS_ASIO_FORMAT_FLOAT => SampleFormat.SAMPLE_FLOAT_32BIT,
        BASSASIOFormat.BASS_ASIO_FORMAT_16BIT => SampleFormat.SAMPLE_INT_16BIT,
        BASSASIOFormat.BASS_ASIO_FORMAT_24BIT => SampleFormat.SAMPLE_INT_24BIT,
        BASSASIOFormat.BASS_ASIO_FORMAT_32BIT => SampleFormat.SAMPLE_INT_32BIT,
        _ => SampleFormat.UNKNOWN
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
            BassAudioPlayer.DeviceDriver.ASIO,
            stage,
            request.Device,
            session.ActualDevice,
            source,
            error,
            message);
}

/// <summary>Forwards ASIO negotiation calls to the currently bundled Bass.Net API.</summary>
internal sealed class BassAsioNegotiationNativeBoundary : IAsioNegotiationNativeBoundary
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
    public BASS_ASIO_DEVICEINFO[] GetDeviceInfos() => BassAsio.BASS_ASIO_GetDeviceInfos();

    /// <inheritdoc />
    public BASS_ASIO_DEVICEINFO GetDeviceInfo(int deviceIndex) =>
        BassAsio.BASS_ASIO_GetDeviceInfo(deviceIndex);

    /// <inheritdoc />
    public bool InitializeAsio(int deviceIndex) =>
        BassAsio.BASS_ASIO_Init(deviceIndex, BASSASIOInit.BASS_ASIO_THREAD);

    /// <inheritdoc />
    public int GetAsioDevice() => BassAsio.BASS_ASIO_GetDevice();

    /// <inheritdoc />
    public double GetRate() => BassAsio.BASS_ASIO_GetRate();

    /// <inheritdoc />
    public bool CheckRate(double rate) => BassAsio.BASS_ASIO_CheckRate(rate);

    /// <inheritdoc />
    public bool SetRate(double rate) => BassAsio.BASS_ASIO_SetRate(rate);

    /// <inheritdoc />
    public bool SetChannelRate(double rate) =>
        BassAsio.BASS_ASIO_ChannelSetRate(input: false, 0, rate);

    /// <inheritdoc />
    public bool SetChannelFormat(BASSASIOFormat format) =>
        BassAsio.BASS_ASIO_ChannelSetFormat(input: false, 0, format);

    /// <inheritdoc />
    public BASSASIOFormat GetChannelFormat() =>
        BassAsio.BASS_ASIO_ChannelGetFormat(input: false, 0);

    /// <inheritdoc />
    public int CreateMixer(int rate, int channels, BASSFlag flags) =>
        BassMix.BASS_Mixer_StreamCreate(rate, channels, flags);

    /// <inheritdoc />
    public bool EnableOutputChannel(ASIOPROC callback) =>
        // Bass.Net 2.4.12.1 does not expose BASS_ASIO_ChannelEnableBASS. Keep the existing
        // callback boundary and guarantee its byte width by negotiating the mixer format above.
        BassAsio.BASS_ASIO_ChannelEnable(input: false, 0, callback, IntPtr.Zero);

    /// <inheritdoc />
    public bool JoinOutputChannel(int channel) =>
        BassAsio.BASS_ASIO_ChannelJoin(input: false, channel, 0);

    /// <inheritdoc />
    public bool Start(int bufferLength, int threads) =>
        BassAsio.BASS_ASIO_Start(bufferLength, threads);

    /// <inheritdoc />
    public int GetOutputLatency() => BassAsio.BASS_ASIO_GetLatency(input: false);

    /// <inheritdoc />
    public BASSError GetCoreError() => Bass.BASS_ErrorGetCode();

    /// <inheritdoc />
    public BASSError GetAsioError() => BassAsio.BASS_ASIO_ErrorGetCode();
}
