using System;
using System.Collections.Generic;
using BeMusicSeeker.Properties;
using Ribbit.Logging;
using Ribbit.Media;
using Ribbit.Media.Audio;

namespace BeMusicSeeker.Models;

public enum AudioDriver
{
    Invalid = -2,
    NullDevice = -1,
    DirectSound = 0,
    WasapiShared = 1,
    WasapiExclusive = 2,
    Asio = 3
}

/// <summary>
/// Provides user-facing backend names without changing persisted enum values or internal identifiers.
/// </summary>
internal static class AudioDriverDisplayNames
{
    /// <summary>Returns the localized display name for one persisted audio backend.</summary>
    internal static string Get(AudioDriver driver) => driver switch
    {
        AudioDriver.DirectSound or AudioDriver.WasapiShared => "WASAPI (" + Resources.Shared + ")",
        AudioDriver.WasapiExclusive => "WASAPI (" + Resources.Exclusive + ")",
        AudioDriver.Asio => "ASIO",
        _ => driver.ToString()
    };

    /// <summary>Returns the display name for a native BASS backend identifier.</summary>
    internal static string Get(Ribbit.Media.BassAudioPlayer.DeviceDriver driver) =>
        Get(BassAudioMapping.FromBassDriver(driver));
}

/// <summary>
/// Defines the selectable audible backends and the safe migration for the legacy value 0.
/// </summary>
internal static class AudioDriverPolicy
{
    private static readonly IReadOnlyList<AudioDriver> selectableDrivers = Array.AsReadOnly(
        new[]
        {
            AudioDriver.WasapiShared,
            AudioDriver.WasapiExclusive,
            AudioDriver.Asio
        });

    /// <summary>Gets the ordered list exposed by settings and device-test selection.</summary>
    internal static IReadOnlyList<AudioDriver> SelectableDrivers => selectableDrivers;

    /// <summary>Gets the default audible backend for new settings.</summary>
    internal static AudioDriver DefaultDriver => AudioDriver.WasapiShared;

    /// <summary>Returns whether a backend is selectable as an audible output.</summary>
    internal static bool IsSelectable(AudioDriver driver)
    {
        for (int index = 0; index < selectableDrivers.Count; index++)
        {
            if (selectableDrivers[index] == driver)
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>Returns the zero-based settings index for a selectable backend, or -1.</summary>
    internal static int IndexOf(AudioDriver driver)
    {
        for (int index = 0; index < selectableDrivers.Count; index++)
        {
            if (selectableDrivers[index] == driver)
            {
                return index;
            }
        }

        return -1;
    }

    /// <summary>
    /// Migrates only the legacy DirectSound identifier to shared WASAPI Default.
    /// Unknown, invalid, and NullDevice values remain unchanged.
    /// </summary>
    internal static AudioOutputSelection NormalizePersistedSelection(AudioOutputSelection selection)
    {
        return selection.Backend == AudioDriver.DirectSound
            ? new AudioOutputSelection(DefaultDriver, null, null)
            : selection;
    }
}

public enum AudioNormalization
{
    None = 0,
    PeakLevel = 1,
    RmsValue = 2
}

public readonly struct AudioDeviceInfo
{
    internal AudioDeviceInfo(string name, string driver)
        : this(name, driver, -1, isDefaultPlaceholder: string.IsNullOrWhiteSpace(driver), isNativeDefault: false, isAvailable: true)
    {
    }

    /// <summary>Creates one catalog option without conflating Default and unavailable saved endpoints.</summary>
    internal AudioDeviceInfo(
        string name,
        string driver,
        int nativeIndex,
        bool isDefaultPlaceholder,
        bool isNativeDefault,
        bool isAvailable)
    {
        Name = name;
        Driver = driver;
        NativeIndex = nativeIndex;
        IsDefaultPlaceholder = isDefaultPlaceholder;
        IsNativeDefault = isNativeDefault;
        IsAvailable = isAvailable;
    }

    public string Name { get; }

    public string Driver { get; }

    /// <summary>Gets the backend-native index, or -1 for a non-native option.</summary>
    internal int NativeIndex { get; }

    /// <summary>Gets whether this option represents the caller's Default intent.</summary>
    internal bool IsDefaultPlaceholder { get; }

    /// <summary>Gets whether the native API marks this concrete endpoint as default.</summary>
    internal bool IsNativeDefault { get; }

    /// <summary>Gets whether the endpoint was present in the latest successful refresh.</summary>
    internal bool IsAvailable { get; }

    public string FriendlyName => IsDefaultPlaceholder
        ? Resources.AudioDeviceDefault
        : IsAvailable
            ? Name
            : string.Format(Resources.AudioDeviceUnavailableFormat, Name);
}

/// <summary>
/// Represents one requested output backend and its backend-specific endpoint identity as a
/// single immutable settings value.
/// </summary>
internal readonly struct AudioOutputSelection : IEquatable<AudioOutputSelection>
{
    /// <summary>Creates a normalized output selection. A blank identity represents Default.</summary>
    internal AudioOutputSelection(AudioDriver backend, string deviceIdentity, string deviceName)
    {
        Backend = backend;
        if (string.IsNullOrWhiteSpace(deviceIdentity))
        {
            DeviceIdentity = null;
            DeviceName = null;
        }
        else
        {
            DeviceIdentity = deviceIdentity;
            DeviceName = string.IsNullOrWhiteSpace(deviceName) ? null : deviceName;
        }
    }

    /// <summary>Gets the requested output backend.</summary>
    internal AudioDriver Backend { get; }

    /// <summary>Gets the backend-specific stable endpoint identity, or null for Default.</summary>
    internal string DeviceIdentity { get; }

    /// <summary>Gets the saved endpoint display name, or null for Default.</summary>
    internal string DeviceName { get; }

    /// <summary>Gets whether this selection requests the backend's current default endpoint.</summary>
    internal bool IsDefault => DeviceIdentity == null;

    /// <inheritdoc />
    public bool Equals(AudioOutputSelection other) =>
        Backend == other.Backend
        && string.Equals(DeviceIdentity, other.DeviceIdentity, StringComparison.Ordinal)
        && string.Equals(DeviceName, other.DeviceName, StringComparison.Ordinal);

    /// <inheritdoc />
    public override bool Equals(object obj) => obj is AudioOutputSelection other && Equals(other);

    /// <inheritdoc />
    public override int GetHashCode() => HashCode.Combine(Backend, DeviceIdentity, DeviceName);

    /// <summary>Compares two normalized output selections.</summary>
    public static bool operator ==(AudioOutputSelection left, AudioOutputSelection right) => left.Equals(right);

    /// <summary>Compares two normalized output selections.</summary>
    public static bool operator !=(AudioOutputSelection left, AudioOutputSelection right) => !left.Equals(right);
}

internal interface IAudioDeviceCatalog
{
    /// <summary>Refreshes every audible backend independently while retaining failed backends' last good list.</summary>
    void Refresh();

    IReadOnlyList<AudioDeviceInfo> GetDevices(AudioDriver driver);

    bool IsEncoderAvailable(EncoderType encoder, string encoderDirectory);
}

internal sealed class BassAudioDeviceCatalog : IAudioDeviceCatalog
{
    private static IReadOnlyList<AudioDriver> AudibleBackends => AudioDriverPolicy.SelectableDrivers;

    private readonly object syncRoot = new();
    private readonly IBassAudioDeviceEnumerator enumerator;
    private readonly Dictionary<AudioDriver, IReadOnlyList<AudioDeviceInfo>> lastGood = [];

    internal BassAudioDeviceCatalog()
        : this(new BassAudioDeviceEnumerator())
    {
    }

    /// <summary>Creates a catalog with a replaceable native enumerator.</summary>
    internal BassAudioDeviceCatalog(IBassAudioDeviceEnumerator enumerator)
    {
        this.enumerator = enumerator ?? throw new ArgumentNullException(nameof(enumerator));
        foreach (AudioDriver backend in AudibleBackends)
        {
            lastGood[backend] = [CreateDefaultOption()];
        }
    }

    public void Refresh()
    {
        foreach (AudioDriver backend in AudibleBackends)
        {
            try
            {
                IReadOnlyList<BassAudioEnumeratedDevice> nativeDevices = enumerator.Enumerate(
                    BassAudioMapping.ToBassDriver(backend));
                var refreshed = new List<AudioDeviceInfo>(nativeDevices.Count + 1)
                {
                    CreateDefaultOption()
                };
                foreach (BassAudioEnumeratedDevice device in nativeDevices)
                {
                    refreshed.Add(new AudioDeviceInfo(
                        device.Name,
                        device.Identity,
                        device.NativeIndex,
                        isDefaultPlaceholder: false,
                        device.IsDefault,
                        isAvailable: true));
                }
                lock (syncRoot)
                {
                    lastGood[backend] = refreshed.AsReadOnly();
                }
            }
            catch (Exception exception)
            {
                TryLogEnumerationFailure(backend, exception);
            }
        }
    }

    public IReadOnlyList<AudioDeviceInfo> GetDevices(AudioDriver driver)
    {
        lock (syncRoot)
        {
            return lastGood.TryGetValue(driver, out IReadOnlyList<AudioDeviceInfo> devices)
                ? devices
                : [];
        }
    }

    public bool IsEncoderAvailable(EncoderType encoder, string encoderDirectory)
    {
        string previousDirectory = Ribbit.Media.BassAudioWriter.EncoderDirectory;
        try
        {
            Ribbit.Media.BassAudioWriter.EncoderDirectory = encoderDirectory;
            return Ribbit.Media.BassAudioWriter.IsEncoderAvailable(encoder);
        }
        finally
        {
            Ribbit.Media.BassAudioWriter.EncoderDirectory = previousDirectory;
        }
    }

    private static AudioDeviceInfo CreateDefaultOption()
        => new(null, null, -1, isDefaultPlaceholder: true, isNativeDefault: false, isAvailable: true);

    private static void TryLogEnumerationFailure(AudioDriver backend, Exception exception)
    {
        try
        {
            NLogWrapper.GetLogger(nameof(BassAudioDeviceCatalog)).Warn(
                "Audio device enumeration failed; retaining last good list. backend="
                + backend + " error=" + exception.Message);
        }
        catch
        {
            // Diagnostics must not turn one backend's enumeration failure into a catalog failure.
        }
    }
}

internal static class BassAudioMapping
{
    internal static Ribbit.BMS.BMSAutoPlayWriter.Normalization ToBassNormalization(AudioNormalization normalization)
    {
        return (Ribbit.BMS.BMSAutoPlayWriter.Normalization)(int)normalization;
    }

    internal static AudioNormalization FromBassNormalization(Ribbit.BMS.BMSAutoPlayWriter.Normalization normalization)
    {
        return (AudioNormalization)(int)normalization;
    }

    internal static Ribbit.Media.BassAudioPlayer.DeviceDriver ToBassDriver(AudioDriver driver)
    {
        AudioOutputSelection normalized = AudioDriverPolicy.NormalizePersistedSelection(
            new AudioOutputSelection(driver, null, null));
        return (Ribbit.Media.BassAudioPlayer.DeviceDriver)(int)normalized.Backend;
    }

    internal static AudioDriver FromBassDriver(Ribbit.Media.BassAudioPlayer.DeviceDriver driver)
    {
        return (AudioDriver)(int)driver;
    }
}

internal sealed class AudioEncodingSettingsSnapshot
{
    /// <summary>音声変換に使う設定を変更不能な値として初期化します。</summary>
    internal AudioEncodingSettingsSnapshot(
        EncoderType encoder,
        SampleRate encoderSampleRate,
        SampleFormat encoderFormat,
        AudioNormalization encoderNormalization,
        float encoderQuality,
        string encoderExeDirectory,
        float encoderAmplifier,
        string encodeFileNameFormat,
        int sampleRateConversionQuality = AudioResamplingQuality.Default)
    {
        Encoder = encoder;
        EncoderSampleRate = encoderSampleRate;
        EncoderFormat = encoderFormat;
        EncoderNormalization = encoderNormalization;
        EncoderQuality = encoderQuality;
        EncoderExeDirectory = encoderExeDirectory;
        EncoderAmplifier = encoderAmplifier;
        EncodeFileNameFormat = encodeFileNameFormat;
        SampleRateConversionQuality = AudioResamplingQuality.Validate(sampleRateConversionQuality);
    }

    internal EncoderType Encoder { get; }

    internal SampleRate EncoderSampleRate { get; }

    internal SampleFormat EncoderFormat { get; }

    internal AudioNormalization EncoderNormalization { get; }

    internal float EncoderQuality { get; }

    internal string EncoderExeDirectory { get; }

    internal float EncoderAmplifier { get; }

    internal string EncodeFileNameFormat { get; }

    /// <summary>音声変換時に捕捉したサンプルレート変換品質を取得します。</summary>
    internal int SampleRateConversionQuality { get; }
}

internal interface IAudioSettingsGateway
{
    /// <summary>Captures the persisted backend/device/name triple as one normalized value.</summary>
    AudioOutputSelection CaptureOutputSelection();

    /// <summary>Applies one normalized backend/device/name triple to the settings object.</summary>
    void ApplyOutputSelection(AudioOutputSelection selection);

    AudioNormalization EncoderNormalization { get; set; }

    AudioEncodingSettingsSnapshot CaptureEncodingSettings();

    void ApplyEncoderFallback(EncoderType encoder);
}

internal sealed class SettingsAudioGateway : IAudioSettingsGateway
{
    private readonly Func<Settings> settingsProvider;

    internal SettingsAudioGateway(Func<Settings> settingsProvider)
    {
        this.settingsProvider = settingsProvider ?? throw new ArgumentNullException(nameof(settingsProvider));
    }

    private Settings Values => settingsProvider()
        ?? throw new InvalidOperationException("Audio settings provider returned null.");

    /// <inheritdoc />
    public AudioOutputSelection CaptureOutputSelection()
    {
        Settings values = Values;
        return AudioDriverPolicy.NormalizePersistedSelection(new AudioOutputSelection(
            BassAudioMapping.FromBassDriver(values.PlayerDriver),
            values.PlayerDevice,
            values.PlayerDeviceName));
    }

    /// <inheritdoc />
    public void ApplyOutputSelection(AudioOutputSelection selection)
    {
        Settings values = Values;
        AudioOutputSelection normalized = AudioDriverPolicy.NormalizePersistedSelection(selection);
        values.PlayerDriver = BassAudioMapping.ToBassDriver(normalized.Backend);
        values.PlayerDevice = normalized.DeviceIdentity;
        values.PlayerDeviceName = normalized.DeviceName;
    }

    public AudioNormalization EncoderNormalization
    {
        get => BassAudioMapping.FromBassNormalization(Values.EncoderNormalization);
        set => Values.EncoderNormalization = BassAudioMapping.ToBassNormalization(value);
    }

    public AudioEncodingSettingsSnapshot CaptureEncodingSettings()
    {
        Settings values = Values;
        return new AudioEncodingSettingsSnapshot(
            values.Encoder,
            values.EncoderSampleRate,
            values.EncoderFormat,
            BassAudioMapping.FromBassNormalization(values.EncoderNormalization),
            values.EncoderQuality,
            values.EncoderExeDir,
            values.EncoderAmplifier,
            values.EncodeFileNameFormat,
            values.PlayerResamplingQuality);
    }

    public void ApplyEncoderFallback(EncoderType encoder)
    {
        Values.Encoder = encoder;
    }
}

/// <summary>
/// Separates the immutable playback request from values negotiated with the native backend.
/// </summary>
internal sealed class AudioPlaybackInitializationResult
{
    /// <summary>Creates a successful audible initialization result.</summary>
    internal AudioPlaybackInitializationResult(
        AudioDriver requestedBackend,
        string requestedDevice,
        string requestedDeviceName,
        SampleRate requestedRate,
        SampleFormat requestedFormat,
        float requestedBufferSize,
        bool requestedEventMode,
        int requestedVolume,
        AudioDriver actualBackend,
        string actualDevice,
        string actualDeviceName,
        SampleRate actualRate,
        SampleFormat engineFormat,
        SampleFormat endpointFormat,
        int actualChannels,
        double latency,
        string fallbackReason,
        bool isSilentFallback)
    {
        RequestedBackend = requestedBackend;
        RequestedDevice = requestedDevice;
        RequestedDeviceName = requestedDeviceName;
        RequestedRate = requestedRate;
        RequestedFormat = requestedFormat;
        RequestedBufferSize = requestedBufferSize;
        RequestedEventMode = requestedEventMode;
        RequestedVolume = requestedVolume;
        ActualBackend = actualBackend;
        ActualDevice = actualDevice;
        ActualDeviceName = actualDeviceName;
        ActualRate = actualRate;
        EngineFormat = engineFormat;
        EndpointFormat = endpointFormat;
        ActualChannels = actualChannels;
        Latency = latency;
        FallbackReason = fallbackReason;
        IsSilentFallback = isSilentFallback;
        FallbackOccurred = requestedBackend != actualBackend
            || !string.IsNullOrWhiteSpace(fallbackReason)
            || (!string.IsNullOrWhiteSpace(requestedDevice)
                && !string.Equals(requestedDevice, actualDevice, StringComparison.Ordinal))
            || (requestedRate != SampleRate.AUTO && requestedRate != actualRate)
            || (requestedFormat != SampleFormat.AUTO && requestedFormat != engineFormat);
    }

    /// <summary>Gets the backend selected by the caller.</summary>
    internal AudioDriver RequestedBackend { get; }

    /// <summary>Gets the requested backend-specific endpoint identity.</summary>
    internal string RequestedDevice { get; }

    /// <summary>Gets the requested endpoint display name.</summary>
    internal string RequestedDeviceName { get; }

    /// <summary>Gets the requested sample rate, including Auto intent.</summary>
    internal SampleRate RequestedRate { get; }

    /// <summary>Gets the requested sample format, including Auto intent.</summary>
    internal SampleFormat RequestedFormat { get; }

    /// <summary>Gets the requested output buffer size in milliseconds.</summary>
    internal float RequestedBufferSize { get; }

    /// <summary>Gets whether event-driven WASAPI was requested.</summary>
    internal bool RequestedEventMode { get; }

    /// <summary>Gets the requested output volume.</summary>
    internal int RequestedVolume { get; }

    /// <summary>Gets the backend that owns the negotiated native session.</summary>
    internal AudioDriver ActualBackend { get; }

    /// <summary>Gets the negotiated endpoint identity.</summary>
    internal string ActualDevice { get; }

    /// <summary>Gets the negotiated endpoint display name.</summary>
    internal string ActualDeviceName { get; }

    /// <summary>Gets the sample rate read back from the native backend.</summary>
    internal SampleRate ActualRate { get; }

    /// <summary>Gets the sample format supplied by the internal mixer.</summary>
    internal SampleFormat EngineFormat { get; }

    /// <summary>
    /// Gets the endpoint format reported by the backend, or <see cref="SampleFormat.UNKNOWN"/>
    /// when the backend did not observe the endpoint bit depth.
    /// </summary>
    internal SampleFormat EndpointFormat { get; }

    /// <summary>Gets the channel count accepted by the endpoint or callback.</summary>
    internal int ActualChannels { get; }

    /// <summary>Gets the negotiated output latency in milliseconds.</summary>
    internal double Latency { get; }

    /// <summary>Gets whether backend, endpoint, rate, format, mode, or period degraded.</summary>
    internal bool FallbackOccurred { get; }

    /// <summary>Gets the ordered native negotiation reason for a fallback.</summary>
    internal string FallbackReason { get; }

    /// <summary>Gets whether the result substituted a non-audible backend.</summary>
    internal bool IsSilentFallback { get; }
}

internal interface IAudioPlaybackRuntime
{
    AudioPlaybackInitializationResult Initialize(PlayerSettingsSnapshot settings);

    int CurrentVoices { get; }

    int MaxVoices { get; }

    void ClearMaxVoices();

    void SetVolume(int volume);

    void Free();
}

internal sealed class BassAudioPlaybackRuntime : IAudioPlaybackRuntime
{
    private readonly BassAudioSessionLease sessionLease = new();
    private readonly Action<PlayerSettingsSnapshot, Action<BassAudioSession>> initializeNativeSession;
    private readonly Func<BassAudioSession, bool> releaseNativeSession;

    private AudioPlaybackInitializationResult activeInitialization;

    internal BassAudioPlaybackRuntime()
        : this(InitializeNativeSession, Ribbit.Media.BassAudioPlayer.Free)
    {
    }

    /// <summary>ネイティブ境界を差し替え、再生sessionの再利用と解放を検証できるruntimeを作成します。</summary>
    internal BassAudioPlaybackRuntime(
        Action<PlayerSettingsSnapshot, Action<BassAudioSession>> initializeNativeSession,
        Func<BassAudioSession, bool> releaseNativeSession)
    {
        this.initializeNativeSession = initializeNativeSession
            ?? throw new ArgumentNullException(nameof(initializeNativeSession));
        this.releaseNativeSession = releaseNativeSession
            ?? throw new ArgumentNullException(nameof(releaseNativeSession));
    }

    public AudioPlaybackInitializationResult Initialize(PlayerSettingsSnapshot settings)
    {
        ArgumentNullException.ThrowIfNull(settings);
        ThrowIfAudiblePlaybackUsesNullDevice(settings.PlayerDriver, settings.PlayerDevice, settings.PlayerDeviceName);

        BassAudioSession retainedSession = sessionLease.Session;
        if (retainedSession != null)
        {
            if (retainedSession.State == BassAudioSessionState.Active
                && activeInitialization != null)
            {
                if (retainedSession.SampleRateConversionQuality == settings.SampleRateConversionQuality)
                {
                    return activeInitialization;
                }

                if (!sessionLease.TryRelease(releaseNativeSession))
                {
                    throw new InvalidOperationException(
                        "The playback runtime could not release its active audio session to apply the requested sample-rate conversion quality.");
                }

                activeInitialization = null;
            }
            else
            {
                throw new InvalidOperationException(
                    "The playback runtime still owns an audio session that is not active or fully released.");
            }
        }

        BassAudioSession initializedSession = null;
        try
        {
            initializeNativeSession(settings, session => initializedSession = session);
            sessionLease.Attach(initializedSession);
            BassAudioBackendResult negotiated = initializedSession.NegotiationResult
                ?? throw new InvalidOperationException(
                    "An audible BASS session completed without a negotiated backend result.");
            activeInitialization = CreateInitializationResult(settings, initializedSession, negotiated);
            return activeInitialization;
        }
        catch
        {
            TryRetainAndRetryFailedInitialization(initializedSession);
            throw;
        }
    }

    public int CurrentVoices => Ribbit.Media.BassAudioPlayer.CurrentVoices;

    public int MaxVoices => Ribbit.Media.BassAudioPlayer.MaxVoices;

    public void ClearMaxVoices()
    {
        Ribbit.Media.BassAudioPlayer.ClearMaxVoices();
    }

    public void SetVolume(int volume)
    {
        Ribbit.Media.BassAudioPlayer.DeviceVolume = Math.Min(100, Math.Max(0, volume)) / 100f;
    }

    public void Free()
    {
        if (sessionLease.TryRelease(releaseNativeSession))
        {
            activeInitialization = null;
        }
    }

    /// <summary>Builds the application contract from the retained native session.</summary>
    internal static AudioPlaybackInitializationResult CreateInitializationResult(
        PlayerSettingsSnapshot settings,
        BassAudioSession session,
        BassAudioBackendResult negotiated)
    {
        ArgumentNullException.ThrowIfNull(settings);
        ArgumentNullException.ThrowIfNull(session);
        ArgumentNullException.ThrowIfNull(negotiated);
        return new AudioPlaybackInitializationResult(
            settings.PlayerDriver,
            settings.PlayerDevice,
            settings.PlayerDeviceName,
            settings.PlayerSampleRate,
            settings.PlayerFormat,
            settings.PlayerBufferSize,
            settings.PlayerWASAPIParam,
            settings.PlayerVolume,
            BassAudioMapping.FromBassDriver(session.ActualBackend),
            session.ActualDevice.Driver,
            session.ActualDevice.Name,
            negotiated.ActualRate,
            negotiated.EngineFormat,
            negotiated.EndpointFormat,
            negotiated.ActualChannels,
            negotiated.LatencyMilliseconds,
            negotiated.FallbackReason,
            session.ActualBackend == Ribbit.Media.BassAudioPlayer.DeviceDriver.NULL_DEVICE);
    }

    /// <summary>Rejects the offline-only NullDevice before any audible native initialization.</summary>
    internal static void ThrowIfAudiblePlaybackUsesNullDevice(
        AudioDriver requestedBackend,
        string requestedDevice,
        string requestedDeviceName)
    {
        if (requestedBackend != AudioDriver.NullDevice)
        {
            return;
        }

        var descriptor = new Ribbit.Media.BassAudioPlayer.DeviceDescriptor(
            requestedDeviceName,
            requestedDevice);
        throw new AudioInitializationException(
            Ribbit.Media.BassAudioPlayer.DeviceDriver.NULL_DEVICE,
            Ribbit.Media.BassAudioPlayer.DeviceDriver.INVALID,
            "backend selection",
            descriptor,
            default,
            nameof(BassAudioPlaybackRuntime),
            null,
            "NullDevice is reserved for offline conversion and cannot initialize audible playback.");
    }

    /// <summary>捕捉した再生設定でBASS sessionを初期化し、失敗時も取得済み所有権を呼び出し元へ渡します。</summary>
    private static void InitializeNativeSession(
        PlayerSettingsSnapshot settings,
        Action<BassAudioSession> captureAcquiredSession)
    {
        Ribbit.Media.BassAudioPlayer.DeviceDescriptor descriptor = string.IsNullOrWhiteSpace(settings.PlayerDevice)
            ? default
            : new Ribbit.Media.BassAudioPlayer.DeviceDescriptor(settings.PlayerDeviceName, settings.PlayerDevice);
        Ribbit.Media.BassAudioPlayer.Frequency = settings.PlayerSampleRate;
        Ribbit.Media.BassAudioPlayer.Format = settings.PlayerFormat;
        Ribbit.Media.BassAudioPlayer.DeviceVolume = Math.Min(100, Math.Max(0, settings.PlayerVolume)) / 100f;
        BassAudioSession initializedSession = null;
        try
        {
            Ribbit.Media.BassAudioPlayer.InitializeOwned(
                BassAudioMapping.ToBassDriver(settings.PlayerDriver),
                descriptor,
                settings.PlayerBufferSize,
                out initializedSession,
                settings.SampleRateConversionQuality,
                settings.PlayerWASAPIParam);
        }
        finally
        {
            captureAcquiredSession(initializedSession);
        }
    }

    private void TryRetainAndRetryFailedInitialization(BassAudioSession initializedSession)
    {
        if (initializedSession == null)
        {
            return;
        }

        try
        {
            sessionLease.Attach(initializedSession);
            if (sessionLease.TryRelease(releaseNativeSession))
            {
                activeInitialization = null;
            }
        }
        catch (Exception cleanupException)
        {
            try
            {
                Ribbit.Logging.NLogWrapper.GetLogger(nameof(BassAudioPlaybackRuntime)).Warn(
                    "Playback initialization cleanup failed while preserving the primary error: "
                    + cleanupException.Message);
            }
            catch
            {
                // Diagnostics must not replace the initialization exception.
            }
        }
    }

}
