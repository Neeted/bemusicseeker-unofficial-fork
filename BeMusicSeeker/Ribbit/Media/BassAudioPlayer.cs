#nullable enable annotations
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.IO;
using System.Linq;
using System.Threading;
using BeMusicSeeker.Models.Utils;
using ManagedBass;
using ManagedBass.Fx;
using ManagedBass.Mix;
using Ribbit.Logging;
using Ribbit.Media.Audio;
using Ribbit.Util;

namespace Ribbit.Media;

public class BassAudioPlayer : IAudioPlayer, IDisposable
{
    public struct DeviceDescriptor(string? name, string? driver)
    {
        public string Name { get; set; } = name;

        public string Driver { get; set; } = driver;

        public readonly string FriendlyName
        {
            get
            {
                if (!string.IsNullOrWhiteSpace(Name))
                {
                    return Name;
                }
                return "(Default device)";
            }
        }
    }

    public enum DeviceDriver
    {
        INVALID = -2,
        NULL_DEVICE = -1,
        DIRECT_SOUND = 0,
        WASAPI_SHARED = 1,
        WASAPI_EXCLUSIVE = 2,
        ASIO = 3
    }

    private static readonly object StaticLockObject;

    protected static int inputMixer;

    protected static int outputMixer;

    protected static int tempoChanger;


    protected static int equalizer;

    private static readonly ManagedBass.Wasapi.WasapiProcedure WasapiProc;

    private static readonly ManagedBass.Asio.AsioProcedure AsioProc;

    private static readonly SyncProcedure EndProc;

    private static float playbackRate;

    private static readonly ConcurrentDictionary<BassAudioEffectType, Tuple<int, IEffectParameter>> FxParameters;

    public static readonly ReadOnlyCollection<float> EqualizerFrequencies;

    private static List<float> equalizerGains;

    private static readonly BassAudioSessionLifecycle SessionLifecycle;

    private static readonly IAudioSessionNativeBoundary SessionNative;

    private static readonly BassWasapiNegotiator WasapiNegotiator;

    private static string initializationStage;

    private static float latencyParam;

    private static SampleRate _frequency;

    private static SampleFormat _format;

    private static float _defaultVolume;

    private const float _volumeInitValue = 0.4f;

    private static float _deviceVolume;

    private static bool _isDeviceMuted;

    private static float _prevMasterVolume;

    private FloatWaveSource? _floatWaveSource;

    private FileProcedures _fileProcedures;

    private PlayState playState;

    private bool isMuted;

    private float prevVolume;

    private int _handle;

    private float _volume;

    private readonly object disposeSync = new();

    private readonly object mixerSourceSync = new();

    private readonly BassMixerSourceController mixerSourceController;

    private BassAudioSession owningSession;

    private bool voiceCounted;

    private bool sourceMatrixConfigured;

    private int pendingEndGeneration;

    private int playbackGeneration;

    private int endSyncHandle;

    private bool disposedValue;

    public static ReadOnlyCollection<float> EqualizerGains => equalizerGains.AsReadOnly();

    public static bool EQEnabled => equalizer != 0;

    public static DeviceDriver DriverType { get; private set; }

    /// <summary>
    /// Gets whether the lifecycle owner currently holds an active native audio graph.
    /// </summary>
    protected static bool IsInitialized
    {
        get
        {
            using (SessionLifecycle.Enter())
            {
                return SessionLifecycle.IsActive;
            }
        }
    }

    /// <summary>
    /// Gets the session currently owned by the lifecycle manager for diagnostics and scoped release.
    /// </summary>
    internal static BassAudioSession ActiveSession
    {
        get
        {
            using (SessionLifecycle.Enter())
            {
                return SessionLifecycle.CurrentSession?.State == BassAudioSessionState.Active
                    ? SessionLifecycle.CurrentSession
                    : null;
            }
        }
    }

    /// <summary>Gets whether shutdown must be deferred until native cleanup succeeds.</summary>
    internal static bool HasUnconfirmedNativeCleanup
    {
        get
        {
            using (SessionLifecycle.Enter())
            {
                return SessionLifecycle.HasUnconfirmedOwnership;
            }
        }
    }

    private static BassAudioSession CurrentSession => SessionLifecycle.CurrentSession;

    private static void ResetManagedState()
    {
        DriverType = DeviceDriver.INVALID;
        _format = SampleFormat.AUTO;
        _frequency = SampleRate.AUTO;
        Latency = 0.0;
        CurrentVoices = 0;
        ClearMaxVoices();
        inputMixer = (outputMixer = (tempoChanger = 0));
        equalizer = 0;
        playbackRate = 1f;
        FxParameters.Clear();
        equalizerGains = new float[10].ToList();
    }

    public static double Latency { get; private set; }

    public static SampleRate Frequency
    {
        get
        {
            return _frequency;
        }
        set
        {
            if (!IsInitialized)
            {
                _frequency = value;
            }
        }
    }

    public static SampleFormat Format
    {
        get
        {
            return _format;
        }
        set
        {
            if (!IsInitialized)
            {
                _format = ((value != SampleFormat.UNKNOWN) ? value : SampleFormat.AUTO);
            }
        }
    }

    public static int CurrentVoices { get; private set; }

    public static int MaxVoices { get; private set; }

    public static float DeviceVolume
    {
        get
        {
            return _deviceVolume;
        }
        set
        {
            if (!(_deviceVolume < 0f))
            {
                _deviceVolume = value;
                if (IsDeviceMuted)
                {
                    _prevMasterVolume = value;
                }
                TryApplyEffectiveDeviceVolumeToActiveSession();
            }
        }
    }

    public static float DefaultVolume
    {
        get
        {
            return _defaultVolume;
        }
        set
        {
            if (!(_defaultVolume < 0f))
            {
                _defaultVolume = value;
            }
        }
    }

    public static bool IsDeviceMuted
    {
        get
        {
            return _isDeviceMuted;
        }
        set
        {
            if (_isDeviceMuted != value)
            {
                _isDeviceMuted = value;
                if (value)
                {
                    _prevMasterVolume = DeviceVolume;
                }
                TryApplyEffectiveDeviceVolumeToActiveSession();
            }
        }
    }

    public bool CanSeek => true;

    public TimeSpan CurrentTime
    {
        get
        {
            using BassAudioOperationLease operation =
                Ribbit.Media.Audio.BassAudioRuntime.EnterAudioOperation();
            CheckOutputHealth();
            long pos = Bass.ChannelGetPosition(_handle, PositionFlags.Bytes);
            return TimeSpan.FromSeconds(Bass.ChannelBytes2Seconds(_handle, pos));
        }
        set
        {
            using BassAudioOperationLease operation =
                Ribbit.Media.Audio.BassAudioRuntime.EnterAudioOperation();
            long pos = Bass.ChannelSeconds2Bytes(_handle, value.TotalSeconds);
            if (pos < 0)
            {
                Errors error = Bass.LastError;
                throw CreatePlaybackException(
                    BassAudioPlaybackStage.SetPosition,
                    FileName,
                    _handle,
                    owningSession?.MixerHandle ?? 0,
                    0,
                    "BASS_ChannelSeconds2Bytes",
                    error,
                    "Converting the requested source position failed.");
            }

            mixerSourceController.SetPosition(
                _handle,
                pos,
                FileName,
                owningSession?.MixerHandle ?? 0);
        }
    }

    public TimeSpan Duration { get; }

    public PlayState PlayState
    {
        get
        {
            if (!(CurrentTime == Duration))
            {
                return playState;
            }
            return PlayState.Stopped;
        }
    }

    public float Volume
    {
        get
        {
            return _volume;
        }
        set
        {
            if (!(value < 0f))
            {
                _volume = value;
                if (IsMuted)
                {
                    prevVolume = value;
                }
                else
                {
                    using BassAudioOperationLease operation =
                        Ribbit.Media.Audio.BassAudioRuntime.EnterAudioOperation();
                    Bass.ChannelSetAttribute(_handle, ChannelAttribute.Volume, value);
                }
            }
        }
    }

    public bool IsMuted
    {
        get
        {
            return isMuted;
        }
        set
        {
            if (isMuted != value)
            {
                isMuted = value;
                if (value)
                {
                    prevVolume = Volume;
                    using BassAudioOperationLease operation =
                        Ribbit.Media.Audio.BassAudioRuntime.EnterAudioOperation();
                    Bass.ChannelSetAttribute(_handle, ChannelAttribute.Volume, 0f);
                }
                else
                {
                    using BassAudioOperationLease operation =
                        Ribbit.Media.Audio.BassAudioRuntime.EnterAudioOperation();
                    Bass.ChannelSetAttribute(_handle, ChannelAttribute.Volume, prevVolume);
                }
            }
        }
    }

    public string FileName { get; private set; }

    public float PlaybackRate
    {
        get
        {
            return playbackRate;
        }
        set
        {
            SetTempoChange(value);
        }
    }

    /// <summary>
    /// Releases the lifecycle-owned audio session. Repeated calls are safe.
    /// </summary>
    public static void Free()
    {
        FreeOwnedSession(null, allowAnySession: true);
    }

    /// <summary>
    /// Releases a session only when it is still the lifecycle-owned graph.
    /// </summary>
    /// <returns><see langword="true"/> when the caller no longer owns native resources.</returns>
    internal static bool Free(BassAudioSession? expectedSession)
    {
        return expectedSession == null
            || FreeOwnedSession(expectedSession, allowAnySession: false);
    }

    private static bool FreeOwnedSession(BassAudioSession expectedSession, bool allowAnySession)
    {
        if (expectedSession?.IsReleased == true)
        {
            return true;
        }

        if (!Ribbit.Media.Audio.BassAudioRuntime.TryEnterAudioSessionCleanup(
            out BassAudioExclusiveLease lifecycle))
        {
            return expectedSession?.IsReleased == true;
        }

        try
        {
            return ReleaseCurrentSessionUnderExclusive(expectedSession, allowAnySession);
        }
        finally
        {
            lifecycle.Complete(!HasUnconfirmedNativeCleanup);
            lifecycle.Dispose();
        }
    }

    /// <summary>
    /// Initializes one audio session using the selected backend's supported fallback order.
    /// </summary>
    public static DeviceDescriptor Initialize(
        DeviceDriver driver = DeviceDriver.WASAPI_SHARED,
        DeviceDescriptor desc = default,
        float lParam = 0f,
        params object[] param)
    {
        return InitializeOwned(driver, desc, lParam, out _, param);
    }

    /// <summary>
    /// Initializes an audio graph and publishes the session token as soon as native ownership
    /// is acquired, so callers can retain cleanup responsibility even when initialization throws.
    /// </summary>
    internal static DeviceDescriptor InitializeOwned(
        DeviceDriver driver,
        DeviceDescriptor desc,
        float lParam,
        out BassAudioSession ownedSession,
        params object[] param)
    {
        ownedSession = null;
        if (driver == DeviceDriver.DIRECT_SOUND)
        {
            driver = DeviceDriver.WASAPI_SHARED;
            desc = default;
        }
        try
        {
            Ribbit.Media.Audio.BassAudioRuntime.Initialize();
        }
        catch (Exception exception)
        {
            throw new AudioInitializationException(
                driver,
                DeviceDriver.INVALID,
                "native runtime",
                desc,
                default,
                "BassNativeRuntime",
                null,
                "BASS native runtime initialization failed: " + exception.Message,
                exception);
        }
        using BassAudioExclusiveLease lifecycle = EnterAudioSessionInitialization(driver, desc);
        try
        {
            using (SessionLifecycle.Enter())
            {
                if (SessionLifecycle.IsActive)
                {
                    BassAudioSession active = SessionLifecycle.CurrentSession;
                    throw new AudioInitializationException(
                        driver,
                        active.ActualBackend,
                        "audio session lifecycle",
                        desc,
                        active.ActualDevice,
                        "BassAudioSession",
                        null,
                        "Audio initialization was rejected because another session is active.");
                }
                if (SessionLifecycle.HasCleanupPending)
                {
                    BassAudioSession pending = SessionLifecycle.CurrentSession;
                    throw new AudioInitializationException(
                        driver,
                        pending.ActualBackend,
                        "audio session cleanup",
                        desc,
                        pending.ActualDevice,
                        "BassAudioSession",
                        null,
                        "A previous audio session still owns native resources after cleanup failed.");
                }
                if (driver == DeviceDriver.INVALID)
                {
                    throw new AudioInitializationException(
                        driver,
                        DeviceDriver.INVALID,
                        "backend selection",
                        desc,
                        default,
                        "BassAudioPlayer",
                        null,
                        "Audio initialization was requested with an invalid backend.");
                }

                SampleRate requestedFrequency = _frequency;
                SampleFormat requestedFormat = _format;
                bool requestedEventMode = param.Length != 0
                    && param[0] is bool eventMode
                    && eventMode;
                Exception primaryException = null;
                var earlierAttempts = new List<BassAudioBackendAttempt>();
                var crossBackendFallbackReasons = new List<string>();
                foreach (DeviceDriver backend in GetInitializationOrder(driver))
                {
                    DeviceDescriptor attemptDevice = backend == driver ? desc : default;
                    if (!SessionLifecycle.TryBegin(driver, desc, out BassAudioSession session))
                    {
                        throw new InvalidOperationException("The audio lifecycle already owns a session.");
                    }
                    // Publish ownership as soon as the lifecycle acquires it so the
                    // consumer can retain and retry cleanup even when initialization throws.
                    ownedSession = session;

                    session.ActualBackend = backend;
                    _frequency = requestedFrequency;
                    _format = requestedFormat;
                    latencyParam = lParam;
                    DriverType = backend;
                    initializationStage = "begin";
                    TryLogInitializationAttemptStart(
                        driver,
                        backend,
                        desc,
                        attemptDevice,
                        requestedFrequency,
                        requestedFormat,
                        lParam,
                        requestedEventMode);
                    try
                    {
                        DeviceDescriptor actualDescriptor = backend switch
                        {
                            DeviceDriver.ASIO => InitializeAsio(attemptDevice),
                            DeviceDriver.WASAPI_EXCLUSIVE => InitializeWasapiNegotiated(attemptDevice, isSharedMode: false, param),
                            DeviceDriver.WASAPI_SHARED => InitializeWasapiNegotiated(attemptDevice, isSharedMode: true, param),
                            DeviceDriver.NULL_DEVICE => InitializeNullDevice(),
                            _ => throw new ArgumentOutOfRangeException(nameof(driver))
                        };
                        if (session.NegotiationResult != null && earlierAttempts.Count != 0)
                        {
                            string earlierFallbackReason = string.Join(
                                "; ",
                                crossBackendFallbackReasons)
                                + "; fallbackDestination=" + DescribeBackendForDiagnostics(backend);
                            session.NegotiationResult = session.NegotiationResult.WithEarlierAttempts(
                                earlierAttempts,
                                earlierFallbackReason);
                        }
                        session.ActualDevice = CurrentSession.ActualDevice.Equals(default(DeviceDescriptor))
                            ? actualDescriptor
                            : CurrentSession.ActualDevice;
                        SessionLifecycle.MarkActive(session);
                        if (backend != DeviceDriver.NULL_DEVICE)
                        {
                            DefaultVolume = _defaultVolume;
                            TryApplyEffectiveDeviceVolumeToActiveSession();
                        }
                        TryLogInitializationSuccess(
                            driver,
                            backend,
                            desc,
                            session,
                            requestedFrequency,
                            requestedFormat,
                            lParam,
                            requestedEventMode);
                        return actualDescriptor;
                    }
                    catch (Exception exception)
                    {
                        AudioInitializationException contextual = AddInitializationContext(exception, session);
                        primaryException ??= contextual;
                        earlierAttempts.Add(new BassAudioBackendAttempt(
                            contextual.Stage,
                            contextual.NativeErrorSource,
                            contextual.NativeErrorCode,
                            "backend=" + DescribeBackendForDiagnostics(backend) + " failed: " + contextual.Message));
                        crossBackendFallbackReasons.Add(
                            "attemptedBackend=" + DescribeBackendForDiagnostics(backend)
                            + " stage=" + contextual.Stage
                            + " nativeErrorSource=" + contextual.NativeErrorSource
                            + " nativeErrorCode="
                            + Ribbit.Media.Audio.BassNativeErrorFormatter.Format(
                                contextual.NativeErrorCode));
                        TryLogInitializationAttemptFailure(
                            driver,
                            backend,
                            desc,
                            requestedFrequency,
                            requestedFormat,
                            lParam,
                            requestedEventMode,
                            contextual);

                        CaptureManagedHandles(session);
                        BassAudioSessionCleanup.Release(session, SessionNative, primaryException);
                        SessionLifecycle.CompleteCleanup(session);
                        ResetManagedState();
                        if (SessionLifecycle.HasCleanupPending)
                        {
                            throw primaryException;
                        }
                    }
                }

                throw primaryException ?? new InvalidOperationException("No audio backend was available.");
            }
        }
        finally
        {
            lifecycle.Complete(!HasUnconfirmedNativeCleanup);
        }
    }

    private static void TryLogInitializationAttemptFailure(
        DeviceDriver requestedBackend,
        DeviceDriver attemptedBackend,
        DeviceDescriptor requestedDevice,
        SampleRate requestedRate,
        SampleFormat requestedFormat,
        float requestedBuffer,
        bool requestedEventMode,
        AudioInitializationException exception)
    {
        try
        {
            TryLogAudioSessionWarning(
                "Audio initialization attempt failed. requestedBackend=" + DescribeBackendForDiagnostics(requestedBackend)
                + " requestedDevice=" + DescribeDevice(requestedDevice)
                + " requestedRate=" + requestedRate
                + " requestedFormat=" + requestedFormat
                + " requestedBufferMs=" + requestedBuffer
                + " requestedEventMode=" + requestedEventMode
                + " attemptedBackend=" + DescribeBackendForDiagnostics(attemptedBackend)
                + " stage=" + exception.Stage
                + " actualDevice=" + DescribeDevice(exception.ActualDevice)
                + " nativeErrorSource=" + exception.NativeErrorSource
                + " nativeErrorCode="
                + Ribbit.Media.Audio.BassNativeErrorFormatter.Format(exception.NativeErrorCode)
                + " " + GetRuntimeVersionDiagnostics()
                + " error=" + exception.Message);
        }
        catch
        {
            // Message construction must not replace the primary native failure or skip its cleanup.
        }
    }

    private static void TryLogInitializationAttemptStart(
        DeviceDriver requestedBackend,
        DeviceDriver attemptedBackend,
        DeviceDescriptor requestedDevice,
        DeviceDescriptor attemptedDevice,
        SampleRate requestedRate,
        SampleFormat requestedFormat,
        float requestedBuffer,
        bool requestedEventMode)
    {
        try
        {
            TryLogAudioSessionInfo(
                "Audio initialization attempt started. requestedBackend=" + DescribeBackendForDiagnostics(requestedBackend)
                + " requestedDevice=" + DescribeDevice(requestedDevice)
                + " requestedRate=" + requestedRate
                + " requestedFormat=" + requestedFormat
                + " requestedBufferMs=" + requestedBuffer
                + " requestedEventMode=" + requestedEventMode
                + " attemptedBackend=" + DescribeBackendForDiagnostics(attemptedBackend)
                + " attemptedDevice=" + DescribeDevice(attemptedDevice)
                + " stage=begin nativeErrorSource=none nativeErrorCode=none "
                + GetRuntimeVersionDiagnostics());
        }
        catch
        {
            // Diagnostics must not alter initialization or cleanup behavior.
        }
    }

    private static void TryLogInitializationSuccess(
        DeviceDriver requestedBackend,
        DeviceDriver attemptedBackend,
        DeviceDescriptor requestedDevice,
        BassAudioSession session,
        SampleRate requestedRate,
        SampleFormat requestedFormat,
        float requestedBuffer,
        bool requestedEventMode)
    {
        try
        {
            TryLogAudioSessionInfo(BuildInitializationSuccessDiagnostics(
                requestedBackend,
                attemptedBackend,
                requestedDevice,
                session,
                requestedRate,
                requestedFormat,
                requestedBuffer,
                requestedEventMode,
                GetRuntimeVersionDiagnostics()));
        }
        catch
        {
            // Diagnostics must not alter initialization or cleanup behavior.
        }
    }

    /// <summary>Builds one deterministic successful-initialization diagnostic record.</summary>
    internal static string BuildInitializationSuccessDiagnostics(
        DeviceDriver requestedBackend,
        DeviceDriver attemptedBackend,
        DeviceDescriptor requestedDevice,
        BassAudioSession session,
        SampleRate requestedRate,
        SampleFormat requestedFormat,
        float requestedBuffer,
        bool requestedEventMode,
        string runtimeVersionDiagnostics)
    {
        ArgumentNullException.ThrowIfNull(session);
        BassAudioBackendResult result = session.NegotiationResult;
        bool fallbackOccurred = requestedBackend != session.ActualBackend
            || !string.IsNullOrWhiteSpace(result?.FallbackReason)
            || (!requestedDevice.Equals(default(DeviceDescriptor))
                && !string.Equals(requestedDevice.Driver, session.ActualDevice.Driver, StringComparison.Ordinal))
            || (requestedRate != SampleRate.AUTO && requestedRate != result?.ActualRate)
            || (requestedFormat != SampleFormat.AUTO && requestedFormat != result?.EngineFormat);
        return "Audio initialization attempt succeeded. requestedBackend=" + DescribeBackendForDiagnostics(requestedBackend)
                + " requestedDevice=" + DescribeDevice(requestedDevice)
                + " requestedRate=" + requestedRate
                + " requestedFormat=" + requestedFormat
                + " requestedBufferMs=" + requestedBuffer
                + " requestedEventMode=" + requestedEventMode
                + " attemptedBackend=" + DescribeBackendForDiagnostics(attemptedBackend)
                + " stage=completed nativeErrorSource=none nativeErrorCode=none"
                + " actualBackend=" + DescribeBackendForDiagnostics(session.ActualBackend)
                + " actualDevice=" + DescribeDevice(session.ActualDevice)
                + " actualRate=" + result?.ActualRate
                + " actualChannels=" + result?.ActualChannels
                + " engineFormat=" + result?.EngineFormat
                + " endpointFormat=" + result?.EndpointFormat
                + " latencyMs=" + result?.LatencyMilliseconds
                + " fallbackOccurred=" + fallbackOccurred
                + " fallbackDestination=" + (fallbackOccurred ? DescribeBackendForDiagnostics(session.ActualBackend) : "none")
                + " fallbackReason=" + (fallbackOccurred ? result?.FallbackReason : "none")
                + " " + runtimeVersionDiagnostics;
    }

    private static string DescribeDevice(DeviceDescriptor device)
    {
        return device.Equals(default(DeviceDescriptor))
            ? "<default>"
            : "[name=" + device.Name + ",identity=" + device.Driver + "]";
    }

    private static string DescribeBackendForDiagnostics(DeviceDriver backend) =>
        backend == DeviceDriver.DIRECT_SOUND ? "WASAPI_SHARED" : backend.ToString();

    private static string GetRuntimeVersionDiagnostics()
    {
        return BuildRuntimeVersionDiagnostics(
            Environment.OSVersion.VersionString,
            unchecked((int)BassVersionPacking.Pack(ManagedBass.Bass.Version)),
            unchecked((int)BassVersionPacking.Pack(ManagedBass.Wasapi.BassWasapi.Version)),
            unchecked((int)BassVersionPacking.Pack(ManagedBass.Asio.BassAsio.Version)),
            unchecked((int)BassVersionPacking.Pack(ManagedBass.Mix.BassMix.Version)),
            unchecked((int)BassVersionPacking.Pack(ManagedBass.Fx.BassFx.Version)),
            unchecked((int)BassVersionPacking.Pack(ManagedBass.Enc.BassEnc.Version)));
    }

    /// <summary>Formats the OS and complete supported BASS native-family version snapshot.</summary>
    internal static string BuildRuntimeVersionDiagnostics(
        string osVersion,
        int bassVersion,
        int bassWasapiVersion,
        int bassAsioVersion,
        int bassMixVersion,
        int bassFxVersion,
        int bassEncVersion)
    {
        return "os=" + osVersion
            + " bassVersion=0x" + bassVersion.ToString("X8")
            + " bassWasapiVersion=0x" + bassWasapiVersion.ToString("X8")
            + " bassAsioVersion=0x" + bassAsioVersion.ToString("X8")
            + " bassMixVersion=0x" + bassMixVersion.ToString("X8")
            + " bassFxVersion=0x" + bassFxVersion.ToString("X8")
            + " bassEncVersion=0x" + bassEncVersion.ToString("X8");
    }

    private static BassAudioExclusiveLease EnterAudioSessionInitialization(
        DeviceDriver requestedBackend,
        DeviceDescriptor requestedDevice)
    {
        try
        {
            return Ribbit.Media.Audio.BassAudioRuntime.EnterAudioSessionInitialization();
        }
        catch (Exception exception)
        {
            throw new AudioInitializationException(
                requestedBackend,
                DeviceDriver.INVALID,
                "audio session lifecycle",
                requestedDevice,
                default,
                "BassAudioOperationGate",
                null,
                "Audio initialization was rejected by the native lifecycle gate: " + exception.Message,
                exception);
        }
    }

    /// <summary>
    /// Returns the supported backend fallback order. Audible playback never falls back to the
    /// legacy BASS core identifier or to the offline-only NullDevice.
    /// </summary>
    internal static IReadOnlyList<DeviceDriver> GetInitializationOrder(DeviceDriver driver)
    {
        return driver switch
        {
            DeviceDriver.ASIO =>
                [DeviceDriver.ASIO, DeviceDriver.WASAPI_EXCLUSIVE, DeviceDriver.WASAPI_SHARED],
            DeviceDriver.WASAPI_EXCLUSIVE =>
                [DeviceDriver.WASAPI_EXCLUSIVE, DeviceDriver.WASAPI_SHARED],
            DeviceDriver.WASAPI_SHARED =>
                [DeviceDriver.WASAPI_SHARED],
            DeviceDriver.NULL_DEVICE => [DeviceDriver.NULL_DEVICE],
            _ => []
        };
    }

    private static bool ReleaseCurrentSessionUnderExclusive()
    {
        return ReleaseCurrentSessionUnderExclusive(null, allowAnySession: true);
    }

    private static bool ReleaseCurrentSessionUnderExclusive(
        BassAudioSession expectedSession,
        bool allowAnySession)
    {
        using (SessionLifecycle.Enter())
        {
            if (!SessionLifecycle.TryGetForRelease(
                expectedSession,
                allowAnySession,
                out BassAudioSession session))
            {
                if (SessionLifecycle.CurrentSession == null)
                {
                    ResetManagedState();
                    return true;
                }

                TryLogAudioSessionDebug(
                    "Audio session release was ignored because lifecycle ownership changed.");
                return true;
            }

            try
            {
                CaptureManagedHandles(session);
                BassAudioSessionCleanup.Release(session, SessionNative);
                SessionLifecycle.CompleteCleanup(session);
            }
            finally
            {
                ResetManagedState();
            }

            return !SessionLifecycle.HasCleanupPending;
        }
    }

    private static void CaptureManagedHandles(BassAudioSession session)
    {
        session.MixerHandle = session.MixerHandle == 0 ? inputMixer : session.MixerHandle;
        session.OutputHandle = session.OutputHandle == 0 ? outputMixer : session.OutputHandle;
        if (tempoChanger != 0 && !session.AdditionalStreamHandles.Contains(tempoChanger))
        {
            session.AdditionalStreamHandles.Add(tempoChanger);
        }
    }

    private static AudioInitializationException AddInitializationContext(
        Exception exception,
        BassAudioSession session)
    {
        if (exception is AudioInitializationException contextual)
        {
            return contextual;
        }

        string source;
        ManagedBass.Errors error;
        if (initializationStage.StartsWith("BASS_ASIO", StringComparison.Ordinal))
        {
            source = "BASSASIO";
            error = ManagedBass.Asio.BassAsio.LastError;
        }
        else
        {
            source = initializationStage.StartsWith("BASS_WASAPI", StringComparison.Ordinal)
                ? "BASSWASAPI/BASS_ErrorGetCode"
                : "BASS";
            error = ManagedBass.Bass.LastError;
        }

        return new AudioInitializationException(
            session.RequestedBackend,
            session.ActualBackend,
            initializationStage,
            session.RequestedDevice,
            session.ActualDevice,
            source,
            error,
            exception.Message,
            exception);
    }

    static BassAudioPlayer()
    {
        SessionLifecycle = new BassAudioSessionLifecycle();
        SessionNative = new BassAudioSessionNativeBoundary();
        WasapiNegotiator = new BassWasapiNegotiator(new BassWasapiNegotiationNativeBoundary());
        StaticLockObject = new object();
        WasapiProc = (buffer, length, user) => ReadCallbackOutput(buffer, length);
        AsioProc = (input, channel, buffer, length, user) => ReadCallbackOutput(buffer, length);
        EndProc = delegate (int handle, int channel, int data, IntPtr user)
        {
            try
            {
                if (!Ribbit.Media.Audio.BassAudioRuntime.TryEnterAudioCallbackOperation(
                    out BassAudioOperationLease operation))
                {
                    return;
                }
                using (operation)
                {
                    BassAudioSession session = SessionLifecycle.CurrentSessionForAdmittedOperation;
                    if (session == null
                        || !session.TryGetPlayerStreamOwner(channel, out BassAudioPlayer player))
                    {
                        return;
                    }

                    player.HandleNaturalEndCallback(session, channel, user.ToInt32());
                }
            }
            catch (Exception exception)
            {
                TryLogPlayerPlaybackFailure(
                    "BASS source end callback failed",
                    exception,
                    fileName: null,
                    sourceHandle: channel,
                    expectedMixerHandle: 0,
                    actualMixerHandle: 0,
                    session: null,
                    managedPlayState: null,
                    voiceCounted: false,
                    endCleanupPending: false,
                    newlyAttached: false,
                    rollbackAttempted: false,
                    rollbackSucceeded: false);
            }
        };
        playbackRate = 1f;
        FxParameters = new ConcurrentDictionary<BassAudioEffectType, Tuple<int, IEffectParameter>>();
        EqualizerFrequencies = new List<float> { 32f, 64f, 125f, 250f, 500f, 1000f, 2000f, 4000f, 8000f, 16000f }.AsReadOnly();
        equalizerGains = new float[10].ToList();
        DriverType = DeviceDriver.INVALID;
        _frequency = SampleRate.AUTO;
        _format = SampleFormat.AUTO;
        _defaultVolume = 0.4f;
        _deviceVolume = 0.4f;
        Ribbit.Media.Audio.BassAudioRuntime.RegisterAudioSessionShutdown(ReleaseCurrentSessionUnderExclusive);
    }

    private static DeviceDescriptor InitializeAsio(DeviceDescriptor desc = default)
    {
        initializationStage = "ASIO negotiation";
        var request = new BassAudioNegotiationRequest(
            DeviceDriver.ASIO,
            desc,
            _frequency,
            _format,
            latencyParam);
        BassAudioBackendResult result = new BassAsioNegotiator(
            new BassAsioNegotiationNativeBoundary()).Initialize(
                request, CurrentSession, AsioProc,
                GetEffectiveDeviceVolumeForInitialization(_deviceVolume, IsDeviceMuted));

        inputMixer = result.MixerHandle;
        outputMixer = result.MixerHandle;
        _frequency = result.ActualRate;
        _format = result.EngineFormat;
        Latency = result.LatencyMilliseconds;
        return desc.Equals(default(DeviceDescriptor)) ? default : result.ActualDevice;
    }

    private static DeviceDescriptor InitializeWasapiNegotiated(
        DeviceDescriptor desc = default,
        bool isSharedMode = false,
        params object[] param)
    {
        initializationStage = "WASAPI negotiation";
        bool eventModeRequested = param is { Length: > 0 } && param[0] is true;
        DeviceDriver backend = isSharedMode
            ? DeviceDriver.WASAPI_SHARED
            : DeviceDriver.WASAPI_EXCLUSIVE;
        var request = new BassAudioNegotiationRequest(
            backend,
            desc,
            _frequency,
            _format,
            latencyParam);
        float initialGain = GetEffectiveDeviceVolumeForInitialization(
            _deviceVolume,
            IsDeviceMuted);
        BassAudioBackendResult result = WasapiNegotiator.Initialize(
                request,
                CurrentSession,
                WasapiProc,
                initialGain,
                eventModeRequested);

        inputMixer = result.MixerHandle;
        outputMixer = result.MixerHandle;
        _frequency = result.ActualRate;
        _format = result.EngineFormat;
        Latency = result.LatencyMilliseconds;
        return desc.Equals(default(DeviceDescriptor)) ? default : result.ActualDevice;
    }

    private static DeviceDescriptor InitializeNullDevice(DeviceDescriptor desc = default)
    {
        SampleRate requestedRate = Frequency;
        SampleFormat requestedFormat = Format;
        var request = new BassAudioNegotiationRequest(
            DeviceDriver.NULL_DEVICE,
            desc,
            requestedRate,
            requestedFormat,
            latencyParam);
        initializationStage = "BASS_Init";
        if (!Bass.Init(0, 44100, DeviceInitFlags.Default, IntPtr.Zero, IntPtr.Zero))
        {
            Errors error = Bass.LastError;
            throw new Exception("BASS_Init failed: " + BassNativeErrorFormatter.Format(error));
        }
        CurrentSession.CoreInitialized = true;
        CurrentSession.CoreDeviceIndex = Bass.CurrentDevice;
        Frequency = ((Frequency == SampleRate.AUTO) ? SampleRate.SAMPLE_RATE_44100Hz : Frequency);
        Format = ((Format == SampleFormat.AUTO) ? SampleFormat.SAMPLE_INT_16BIT : Format);
        Latency = 0.0;
        BassFlags flags = BassFlags.Float | BassFlags.Prescan | BassFlags.Decode | BassFlags.MixerNonStop;
        initializationStage = "BASS_Mixer_StreamCreate";
        inputMixer = BassMix.CreateMixerStream((int)Frequency, 2, flags);
        if (inputMixer == 0)
        {
            Errors error = Bass.LastError;
            throw new Exception("BASS_Mixer_StreamCreate failed: " + BassNativeErrorFormatter.Format(error));
        }
        outputMixer = inputMixer;
        CurrentSession.MixerHandle = inputMixer;
        CurrentSession.OutputHandle = outputMixer;
        CurrentSession.ActualDevice = default;
        string fallbackReason = requestedFormat != SampleFormat.AUTO
            && requestedFormat != SampleFormat.SAMPLE_FLOAT_32BIT
                ? "Null device decode mixer normalized to Float32."
                : null;
        CurrentSession.NegotiationResult = new BassAudioBackendResult(
            request,
            default,
            Frequency,
            SampleFormat.SAMPLE_FLOAT_32BIT,
            SampleFormat.SAMPLE_FLOAT_32BIT,
            0.0,
            inputMixer,
            Array.Empty<BassAudioBackendAttempt>(),
            fallbackReason,
            actualChannels: 2);
        return default;
    }

    /// <summary>Updates the active output graph to play at the requested tempo.</summary>
    public static void SetTempoChange(float speed, bool changeFreq = false)
    {
        if (!Ribbit.Media.Audio.BassAudioRuntime.TryEnterAudioOperation(
            out BassAudioOperationLease operation))
        {
            return;
        }

        using (operation)
        {
            using (SessionLifecycle.Enter())
            {
                SetTempoChangeCore(speed, changeFreq);
            }
        }
    }

    private static void SetTempoChangeCore(float speed, bool changeFreq)
    {
        if (!SessionLifecycle.IsActive)
        {
            return;
        }
        if ((double)speed < 0.05 || speed > 50f)
        {
            throw new ArgumentOutOfRangeException("speed");
        }
        if (speed == 1f)
        {
            ResetTempoChangeCore();
            return;
        }
        if (tempoChanger == 0)
        {
            BassFlags flags = BassFlags.Decode;
            switch (DriverType)
            {
                case DeviceDriver.NULL_DEVICE:
                case DeviceDriver.WASAPI_SHARED:
                case DeviceDriver.WASAPI_EXCLUSIVE:
                case DeviceDriver.ASIO:
                    outputMixer = (tempoChanger = BassFx.TempoCreate(inputMixer, flags));
                    if (outputMixer == 0)
                    {
                        Errors error = Bass.LastError;
                        throw new Exception("BASS_FX_TempoCreate failed: " + BassNativeErrorFormatter.Format(error));
                    }
                    CurrentSession.TrackOutputHandle(outputMixer);
                    break;
                default:
                    throw new ArgumentOutOfRangeException();
            }
            Bass.ChannelSetAttribute(tempoChanger, ChannelAttribute.TempoSequenceMilliseconds, 33f);
            Bass.ChannelSetAttribute(tempoChanger, ChannelAttribute.TempoSeekWindowMilliseconds, 10f);
        }
        if (changeFreq)
        {
            float value;
            Bass.ChannelGetAttribute(inputMixer, ChannelAttribute.Frequency, out value);
            Bass.ChannelSetAttribute(tempoChanger, ChannelAttribute.TempoFrequency, speed * value);
        }
        else
        {
            Bass.ChannelSetAttribute(tempoChanger, ChannelAttribute.Tempo, 100f * (speed - 1f));
        }
        playbackRate = speed;
    }

    /// <summary>Restores the active output graph to its original tempo.</summary>
    public static void ResetTempoChange()
    {
        if (!Ribbit.Media.Audio.BassAudioRuntime.TryEnterAudioOperation(
            out BassAudioOperationLease operation))
        {
            return;
        }

        using (operation)
        {
            using (SessionLifecycle.Enter())
            {
                ResetTempoChangeCore();
            }
        }
    }

    private static void ResetTempoChangeCore()
    {
        if (!SessionLifecycle.IsActive || tempoChanger == 0)
        {
            return;
        }
        int oldTempoChanger = tempoChanger;
        bool released = TryReleaseTempoOutputForReset(
            CurrentSession,
            DriverType,
            oldTempoChanger,
            inputMixer,
            handle => TryReleaseTrackedStream(handle, "BASS_StreamFree for ResetTempoChanger"));
        if (!released)
        {
            return;
        }
        tempoChanger = 0;
        switch (DriverType)
        {
            case DeviceDriver.NULL_DEVICE:
            case DeviceDriver.WASAPI_SHARED:
            case DeviceDriver.WASAPI_EXCLUSIVE:
            case DeviceDriver.ASIO:
                outputMixer = inputMixer;
                CurrentSession.TrackOutputHandle(outputMixer);
                break;
            default:
                throw new ArgumentOutOfRangeException();
        }
        playbackRate = 1f;
    }

    private static bool TryReleaseTrackedStream(int handle, string operation)
    {
        if (handle == 0)
        {
            return true;
        }
        if (Bass.StreamFree(handle))
        {
            CurrentSession.ConfirmStreamReleased(handle);
            return true;
        }

        Errors error = Bass.LastError;
        if (error == Errors.Init)
        {
            TryLogAudioSessionDebug(
                operation + " returned BASS_ERROR_INIT and was treated as already released.");
            CurrentSession.ConfirmStreamReleased(handle);
            return true;
        }

        TryLogAudioSessionWarning(
            operation + " failed: " + BassNativeErrorFormatter.Format(error));
        return false;
    }

    /// <summary>
    /// Releases a tempo output while preserving a valid callback source for callback-driven
    /// backends. The callback source is restored when the previous stream cannot be released.
    /// </summary>
    internal static bool TryReleaseTempoOutputForReset(
        BassAudioSession session,
        DeviceDriver backend,
        int previousHandle,
        int replacementHandle,
        Func<int, bool> releasePrevious)
    {
        ArgumentNullException.ThrowIfNull(session);
        ArgumentNullException.ThrowIfNull(releasePrevious);
        bool callbackDrivenOutput = backend is
            DeviceDriver.WASAPI_SHARED or DeviceDriver.WASAPI_EXCLUSIVE or DeviceDriver.ASIO;
        return callbackDrivenOutput
            ? session.TryPrepareCallbackOutputReplacement(
                previousHandle,
                replacementHandle,
                releasePrevious)
            : releasePrevious(previousHandle);
    }

    private static void TryLogAudioSessionDebug(string message)
    {
        try
        {
            NLogWrapper.GetLogger("AudioSession").Debug(message);
        }
        catch
        {
            // Cleanup and idempotent release semantics must not depend on diagnostics.
        }
    }

    private static void TryLogAudioSessionInfo(string message)
    {
        try
        {
            NLogWrapper.GetLogger("AudioSession").Info(message);
        }
        catch
        {
            // Initialization and cleanup semantics must not depend on diagnostics.
        }
    }

    private static void TryLogAudioSessionWarning(string message)
    {
        try
        {
            NLogWrapper.GetLogger("AudioSession").Warn(message);
        }
        catch
        {
            // Cleanup and primary failure semantics must not depend on diagnostics.
        }
    }

    internal static void CreateFX(IEffectParameter parameter)
    {
        using BassAudioOperationLease operation =
            Ribbit.Media.Audio.BassAudioRuntime.EnterAudioOperation();
        if (!IsInitialized)
        {
            return;
        }
        ArgumentNullException.ThrowIfNull(parameter);
        BassAudioEffectDefinition definition = BassAudioEffectCatalog.Definitions
            .FirstOrDefault(candidate => candidate.ParameterType == parameter.GetType());
        if (definition is null)
        {
            return;
        }
        BassAudioEffectType effectType = definition.Type;
        if (FxParameters.TryGetValue(
                effectType,
                out Tuple<int, IEffectParameter> value)
            && value.Item1 != 0)
        {
            FxParameters[effectType] = new Tuple<int, IEffectParameter>(value.Item1, parameter);
            if (!BassAudioEffectCatalog.SetParameters(value.Item1, parameter))
            {
                LogEffectFailure("Bass.FXSetParameters", Bass.LastError);
            }
        }
        else
        {
            FxParameters[effectType] = new Tuple<int, IEffectParameter>(0, parameter);
        }
    }

    internal static void RemoveFX(BassAudioEffectType fxType)
    {
        using BassAudioOperationLease operation =
            Ribbit.Media.Audio.BassAudioRuntime.EnterAudioOperation();
        if (IsInitialized
            && FxParameters.TryRemove(
                fxType,
                out Tuple<int, IEffectParameter> value)
            && value.Item1 != 0
            && !Bass.ChannelRemoveFX(inputMixer, value.Item1))
        {
            LogEffectFailure("Bass.ChannelRemoveFX", Bass.LastError);
        }
    }

    internal static void RemoveFX()
    {
        using BassAudioOperationLease operation =
            Ribbit.Media.Audio.BassAudioRuntime.EnterAudioOperation();
        if (IsInitialized)
        {
            KeyValuePair<BassAudioEffectType, Tuple<int, IEffectParameter>>[] array = [.. FxParameters];
            foreach (KeyValuePair<BassAudioEffectType, Tuple<int, IEffectParameter>> keyValuePair in array)
            {
                RemoveFX(keyValuePair.Key);
            }
        }
    }

    internal static void DisableFX(BassAudioEffectType fxType)
    {
        using BassAudioOperationLease operation =
            Ribbit.Media.Audio.BassAudioRuntime.EnterAudioOperation();
        if (IsInitialized
            && FxParameters.TryGetValue(
                fxType,
                out Tuple<int, IEffectParameter> value)
            && value.Item1 != 0)
        {
            if (!Bass.ChannelRemoveFX(inputMixer, value.Item1))
            {
                LogEffectFailure("Bass.ChannelRemoveFX", Bass.LastError);
            }
            else
            {
                value = new Tuple<int, IEffectParameter>(0, value.Item2);
                FxParameters[fxType] = value;
            }
        }
    }

    internal static void DisableFX()
    {
        using BassAudioOperationLease operation =
            Ribbit.Media.Audio.BassAudioRuntime.EnterAudioOperation();
        if (IsInitialized)
        {
            KeyValuePair<BassAudioEffectType, Tuple<int, IEffectParameter>>[] array = [.. FxParameters];
            foreach (KeyValuePair<BassAudioEffectType, Tuple<int, IEffectParameter>> keyValuePair in array)
            {
                DisableFX(keyValuePair.Key);
            }
        }
    }

    internal static void EnableFX(BassAudioEffectType fxType)
    {
        using BassAudioOperationLease operation =
            Ribbit.Media.Audio.BassAudioRuntime.EnterAudioOperation();
        if (IsInitialized
            && FxParameters.TryGetValue(
                fxType,
                out Tuple<int, IEffectParameter> value)
            && value.Item1 == 0)
        {
            int effectHandle = BassAudioEffectCatalog.ChannelSetFX(inputMixer, fxType, 0);
            if (effectHandle == 0)
            {
                LogEffectFailure("Bass.ChannelSetFX", Bass.LastError);
            }
            else
            {
                value = new Tuple<int, IEffectParameter>(effectHandle, value.Item2);
                FxParameters[fxType] = value;
                if (!BassAudioEffectCatalog.SetParameters(value.Item1, value.Item2))
                {
                    LogEffectFailure("Bass.FXSetParameters", Bass.LastError);
                }
            }
        }
    }

    internal static void EnableFX()
    {
        using BassAudioOperationLease operation =
            Ribbit.Media.Audio.BassAudioRuntime.EnterAudioOperation();
        if (IsInitialized)
        {
            KeyValuePair<BassAudioEffectType, Tuple<int, IEffectParameter>>[] array = [.. FxParameters];
            foreach (KeyValuePair<BassAudioEffectType, Tuple<int, IEffectParameter>> keyValuePair in array)
            {
                EnableFX(keyValuePair.Key);
            }
        }
    }

    internal static bool FXCreated(BassAudioEffectType fxType)
    {
        return FxParameters.ContainsKey(fxType);
    }

    internal static bool FXEnabled(BassAudioEffectType fxType)
    {
        return FxParameters.TryGetValue(
            fxType,
            out Tuple<int, IEffectParameter> value)
            && value.Item1 != 0;
    }

    private static void LogEffectFailure(string operation, Errors error)
    {
        NLogWrapper.TraceLogger?.Warn(
            operation + " failed: " + BassNativeErrorFormatter.Format(error));
    }

    public static void EnableEQ()
    {
        using BassAudioOperationLease operation =
            Ribbit.Media.Audio.BassAudioRuntime.EnterAudioOperation();
        if (!IsInitialized || EQEnabled)
        {
            return;
        }
        equalizer = BassAudioEffectCatalog.ChannelSetFX(
            inputMixer,
            BassAudioEffectType.BfxPeakEq,
            0);
        if (equalizer == 0)
        {
            LogEffectFailure("Bass.ChannelSetFX (EQ)", Bass.LastError);
            return;
        }

        var peakEqParameters = new PeakEQParameters
        {
            fQ = 0f,
            fBandwidth = 2.5f,
            lChannel = FXChannelFlags.All
        };
        for (int i = 0; i < EqualizerFrequencies.Count; i++)
        {
            peakEqParameters.lBand = i;
            peakEqParameters.fCenter = EqualizerFrequencies[i];
            peakEqParameters.fGain = equalizerGains[i];
            if (!BassAudioEffectCatalog.SetParameters(equalizer, peakEqParameters))
            {
                LogEffectFailure("Bass.FXSetParameters (EQ)", Bass.LastError);
            }
        }
    }

    public static void DisableEQ()
    {
        using BassAudioOperationLease operation =
            Ribbit.Media.Audio.BassAudioRuntime.EnterAudioOperation();
        if (IsInitialized && EQEnabled)
        {
            if (!Bass.ChannelRemoveFX(inputMixer, equalizer))
            {
                LogEffectFailure("Bass.ChannelRemoveFX (EQ)", Bass.LastError);
            }
            else
            {
                equalizer = 0;
            }
        }
    }

    public static void UpdateEQ(int slot, float gain)
    {
        if (slot < 0 || slot >= EqualizerFrequencies.Count)
        {
            throw new ArgumentOutOfRangeException("slot");
        }
        using BassAudioOperationLease operation =
            Ribbit.Media.Audio.BassAudioRuntime.EnterAudioOperation();
        if (EQEnabled)
        {
            var peakEqParameters = new PeakEQParameters
            {
                lBand = slot
            };
            if (!BassAudioEffectCatalog.GetParameters(equalizer, peakEqParameters))
            {
                LogEffectFailure("Bass.FXGetParameters (EQ)", Bass.LastError);
                return;
            }
            peakEqParameters.fGain = gain;
            if (!BassAudioEffectCatalog.SetParameters(equalizer, peakEqParameters))
            {
                LogEffectFailure("Bass.FXSetParameters (EQ)", Bass.LastError);
                return;
            }
        }
        equalizerGains[slot] = gain;
    }

    public static void ResetEQ()
    {
        using BassAudioOperationLease operation =
            Ribbit.Media.Audio.BassAudioRuntime.EnterAudioOperation();
        if (IsInitialized)
        {
            for (int i = 0; i < EqualizerFrequencies.Count; i++)
            {
                UpdateEQ(i, 0f);
            }
        }
    }

    public static void ClearMaxVoices()
    {
        MaxVoices = 0;
    }

    /// <summary>Resolves the effective gain for an audible backend before output starts.</summary>
    internal static float GetEffectiveDeviceVolumeForInitialization(
        float deviceVolume,
        bool isMuted) =>
        isMuted ? 0f : deviceVolume;

    /// <summary>
    /// Resolves the effective gain for a backend, keeping the offline NullDevice render gain
    /// independent from the mute state of an audible device.
    /// </summary>
    internal static float GetEffectiveDeviceVolumeForBackend(
        DeviceDriver backend,
        float deviceVolume,
        bool isMuted) =>
        backend == DeviceDriver.NULL_DEVICE
            ? 1f
            : GetEffectiveDeviceVolumeForInitialization(deviceVolume, isMuted);

    /// <summary>公開されたfloat出力を読み、後段gainを適用します。故障は管理側へ保留します。</summary>
    internal static unsafe int ReadPublishedCallbackOutput(BassAudioSession? session, IntPtr buffer, int length)
    {
        if (buffer == IntPtr.Zero || length < 0)
        {
            session?.TryRecordCallbackOutputFailure(AudioPcmRenderStage.InvalidReadLength);
            return 0;
        }
        Span<byte> bytes = new((void*)buffer, length);
        bytes.Clear();
        if (session == null || session.CallbackOutputHandle == 0)
        {
            return 0;
        }
        if (session.HasCallbackOutputFailure)
        {
            return length;
        }
        try
        {
            AudioPcmRenderer renderer = session.CallbackPcmRenderer;
            AudioOutputProcessor processor = session.OutputProcessor;
            if (renderer == null || processor == null || length % (sizeof(float) * renderer.ChannelCount) != 0)
            {
                session.TryRecordCallbackOutputFailure(AudioPcmRenderStage.UnalignedFrame);
                return length;
            }
            Span<float> pcm = new((void*)buffer, length / sizeof(float));
            if (!renderer.TryReadFrames(session.CallbackOutputHandle, pcm, pcm.Length / renderer.ChannelCount,
                    out _, out AudioPcmRenderStage stage, out Errors? nativeError))
            {
                session.TryRecordCallbackOutputFailure(stage, nativeError);
                bytes.Clear();
                return length;
            }
            if (!processor.TryProcess(pcm, renderer.ChannelCount,
                    out AudioOutputProcessResult processed, out AudioOutputProcessFailure failure))
            {
                session.TryRecordCallbackOutputFailure(failure);
                bytes.Clear();
                return length;
            }
            if (processed.ExceededFullScale)
            {
                session.TryMarkOutputOverLevelPending();
            }
            return length;
        }
        catch
        {
            session.TryRecordCallbackOutputFailure(AudioPcmRenderStage.NativeRead);
            bytes.Clear();
            return length;
        }
    }

    private static int ReadCallbackOutput(IntPtr buffer, int length)
    {
        try
        {
            if (!Ribbit.Media.Audio.BassAudioRuntime.TryEnterAudioCallbackOperation(out BassAudioOperationLease operation))
            {
                return 0;
            }
            using (operation)
            {
                return ReadPublishedCallbackOutput(SessionLifecycle.CurrentSessionForAdmittedOperation, buffer, length);
            }
        }
        catch
        {
            return 0;
        }
    }

    /// <summary>通常の再生観測で出力診断を回収し、故障を既存の停止・後片付けへ伝えます。</summary>
    internal static void CheckOutputHealth()
    {
        if (!Ribbit.Media.Audio.BassAudioRuntime.TryEnterAudioOperation(out BassAudioOperationLease operation))
        {
            return;
        }
        using (operation)
        {
            BassAudioSession session = SessionLifecycle.CurrentSessionForAdmittedOperation;
            if (session == null)
            {
                return;
            }
            if (session.TryConsumeOutputOverLevelPending())
            {
                TryLogAudioSessionWarning("Audio output exceeded full scale at the device boundary.");
            }
            session.ThrowPendingOutputFailure();
        }
    }

    private static void TryApplyEffectiveDeviceVolumeToActiveSession()
    {
        if (!Ribbit.Media.Audio.BassAudioRuntime.TryEnterAudioOperation(
            out BassAudioOperationLease operation))
        {
            return;
        }

        using (operation)
        {
            BassAudioSession session = SessionLifecycle.CurrentSessionForAdmittedOperation;
            if (session?.State != BassAudioSessionState.Active)
            {
                return;
            }

            SetDeviceMasterVolume(
                session,
                GetEffectiveDeviceVolumeForBackend(
                    session.ActualBackend,
                    _deviceVolume,
                    _isDeviceMuted));
        }
    }

    private static void SetDeviceMasterVolume(BassAudioSession session, float vol)
    {
        if (session?.State == BassAudioSessionState.Active && session.ActualBackend != DeviceDriver.NULL_DEVICE)
        {
            session.OutputProcessor?.SetTargetGain(vol);
        }
    }

    /// <summary>対応する音声ファイルから再生音源を作成します。</summary>
    public BassAudioPlayer(string fileName)
        : this(fileName, new AudioSourceCache(), new BassMixerSourceNativeBoundary())
    {
    }

    /// <summary>一回の曲ロードが所有するcacheを使って再生音源を作成します。</summary>
    internal BassAudioPlayer(
        string fileName,
        AudioSourceCache sourceCache)
        : this(fileName, sourceCache, new BassMixerSourceNativeBoundary())
    {
    }

    /// <summary>差し替え可能なmixer source境界を使って再生音源を作成します。</summary>
    internal BassAudioPlayer(
        string fileName,
        AudioSourceCache sourceCache,
        IBassMixerSourceNativeBoundary mixerSourceNative)
    {
        mixerSourceController = new BassMixerSourceController(
            mixerSourceNative,
            () => owningSession);
        using BassAudioOperationLease operation =
            Ribbit.Media.Audio.BassAudioRuntime.EnterAudioOperation();
        owningSession = SessionLifecycle.CurrentSessionForAdmittedOperation;
        if (owningSession?.State != BassAudioSessionState.Active)
        {
            throw CreatePlaybackException(
                BassAudioPlaybackStage.SourceDeviceSelection,
                fileName,
                0,
                0,
                0,
                "BassAudioSession",
                null,
                "BassAudioPlayer is not owned by an active audio session.");
        }
        if (fileName == null)
        {
            throw new ArgumentNullException("fileName");
        }
        ArgumentNullException.ThrowIfNull(sourceCache);
        if (!LongPathFileSystem.FileExists(fileName))
        {
            throw new FileNotFoundException(fileName);
        }
        FileName = fileName;
        int expectedMixerHandle = owningSession.MixerHandle;
        if (expectedMixerHandle == 0)
        {
            throw CreatePlaybackException(
                BassAudioPlaybackStage.SourceDeviceSelection,
                fileName,
                0,
                expectedMixerHandle,
                0,
                "BassAudioSession",
                null,
                "The active audio session does not expose a mixer handle.");
        }

        if (owningSession.CoreDeviceIndex >= 0)
        {
            try
            {
                Bass.CurrentDevice = owningSession.CoreDeviceIndex;
            }
            catch (BassException exception)
            {
                throw CreatePlaybackException(
                    BassAudioPlaybackStage.SourceDeviceSelection,
                    fileName,
                    0,
                    expectedMixerHandle,
                    0,
                    "BASS_SetDevice",
                    exception.ErrorCode,
                    "Selecting the owning BASS core device failed.",
                    exception);
            }
        }

        bool streamTracked = false;
        try
        {
            DecodedAudio source = sourceCache.GetOrLoad(fileName);
            _floatWaveSource = new FloatWaveSource(source, fileName);
            _fileProcedures = new FileProcedures
            {
                Close = FileProcClose,
                Length = FileProcLength,
                Read = FileProcRead,
                Seek = FileProcSeek
            };
            _handle = Bass.CreateStream(
                StreamSystem.NoBuffer,
                BassFlags.Float | BassFlags.Prescan | BassFlags.Decode,
                _fileProcedures,
                IntPtr.Zero);
            if (_handle == 0)
            {
                Errors error = Bass.LastError;
                throw CreatePlaybackException(
                    BassAudioPlaybackStage.SourceCreate,
                    fileName,
                    0,
                    expectedMixerHandle,
                    0,
                    "BASS_StreamCreateFileUser",
                    error,
                    "Creating the float WAVE BASS source stream failed.");
            }

            try
            {
                owningSession.TrackPlayerStream(_handle, this, ConfirmNativeStreamReleased);
                streamTracked = true;
            }
            catch (Exception exception)
            {
                throw CreatePlaybackException(
                    BassAudioPlaybackStage.SourceTracking,
                    fileName,
                    _handle,
                    expectedMixerHandle,
                    0,
                    nameof(BassAudioSession.TrackPlayerStream),
                    null,
                    "Retaining the BASS source stream in its owning session failed.",
                    exception);
            }

            ChannelInfo sourceInfo = Bass.ChannelGetInfo(_handle);
            long sourceLength = Bass.ChannelGetLength(_handle, PositionFlags.Bytes);
            long expectedLength = checked(source.FrameCount * source.ChannelCount * sizeof(float));
            if ((sourceInfo.Flags & (BassFlags.Float | BassFlags.Decode)) != (BassFlags.Float | BassFlags.Decode)
                || sourceInfo.Frequency != source.SampleRate
                || sourceInfo.Channels != source.ChannelCount
                || sourceLength != expectedLength)
            {
                throw CreatePlaybackException(
                    BassAudioPlaybackStage.SourceCreate,
                    fileName,
                    _handle,
                    expectedMixerHandle,
                    0,
                    "BASS_StreamCreateFileUser",
                    Bass.LastError,
                    "The float WAVE source format or finite length does not match its decoded PCM.");
            }
            Duration = TimeSpan.FromSeconds((double)source.FrameCount / source.SampleRate);
            Volume = DefaultVolume;
            playState = PlayState.Stopped;
        }
        catch
        {
            bool nativeStreamReleasedOrAlreadyOwned = false;
            bool streamAlreadyOwned = false;
            if (_handle != 0 && !streamTracked && owningSession != null)
            {
                try
                {
                    streamTracked = owningSession.TryTrackPlayerStreamForCleanup(
                        _handle,
                        this,
                        ConfirmNativeStreamReleased,
                        out streamAlreadyOwned);
                }
                catch (Exception exception)
                {
                    TryLogPlayerCleanupFailure(
                        "Failed to retain a source stream for construction cleanup",
                        exception);
                }
            }

            if (_handle != 0 && streamTracked)
            {
                int trackedHandle = _handle;
                bool released = false;
                try
                {
                    released = Bass.StreamFree(trackedHandle);
                    if (!released)
                    {
                        Errors error = Bass.LastError;
                        released = error == Errors.Init;
                        if (!released)
                        {
                            TryLogPlayerCleanupFailure(
                                "Failed to clean up a tracked source stream during construction: "
                                + BassNativeErrorFormatter.Format(error),
                                null);
                        }
                    }
                }
                catch (Exception exception)
                {
                    TryLogPlayerCleanupFailure(
                        "Failed to clean up a tracked source stream during construction",
                        exception);
                }

                if (released)
                {
                    owningSession.ConfirmPlayerStreamReleased(trackedHandle);
                }
            }
            else if (_handle != 0)
            {
                if (streamAlreadyOwned)
                {
                    nativeStreamReleasedOrAlreadyOwned = true;
                }
                else
                {
                    bool released = false;
                    try
                    {
                        released = Bass.StreamFree(_handle);
                        if (!released)
                        {
                            Errors error = Bass.LastError;
                            released = error == Errors.Init;
                            if (!released)
                            {
                                TryLogPlayerCleanupFailure(
                                    "Failed to clean up an untracked source stream: "
                                    + BassNativeErrorFormatter.Format(error),
                                    null);
                            }
                        }
                    }
                    catch (Exception exception)
                    {
                        TryLogPlayerCleanupFailure(
                            "Failed to clean up an untracked source stream",
                            exception);
                    }

                    nativeStreamReleasedOrAlreadyOwned = released;
                }

                if (nativeStreamReleasedOrAlreadyOwned)
                {
                    _handle = 0;
                }
            }

            if (!streamTracked && (_handle == 0 || nativeStreamReleasedOrAlreadyOwned))
            {
                ReleaseInputSource();
            }

            throw;
        }
    }

    private void FileProcClose(IntPtr user)
    {
    }

    private long FileProcLength(IntPtr user)
    {
        return _floatWaveSource?.TotalLength ?? 0;
    }

    private int FileProcRead(IntPtr buffer, int length, IntPtr user)
    {
        try
        {
            return _floatWaveSource?.Read(buffer, length) ?? -1;
        }
        catch (Exception exception)
        {
            TryLogPlayerCleanupFailure("BASS float WAVE source read failed", exception);
            return -1;
        }
    }

    private bool FileProcSeek(long offset, IntPtr user)
    {
        try
        {
            return _floatWaveSource?.Seek(offset) ?? false;
        }
        catch (Exception exception)
        {
            TryLogPlayerCleanupFailure("BASS float WAVE source seek failed", exception);
            return false;
        }
    }

    public void Pause()
    {
        using BassAudioOperationLease operation =
            Ribbit.Media.Audio.BassAudioRuntime.EnterAudioOperation();
        lock (mixerSourceSync)
        {
            ProcessPendingEndCleanup();
            if (playState == PlayState.Stopped)
            {
                return;
            }

            BassAudioSession session = GetOwningSessionForOperation();
            if (playState == PlayState.Playing)
            {
                mixerSourceController.Pause(
                    session.MixerHandle,
                    _handle,
                    FileName);
                playState = PlayState.Paused;
            }
            else if (playState == PlayState.Paused)
            {
                mixerSourceController.Resume(
                    session.MixerHandle,
                    _handle,
                    FileName);
                playState = PlayState.Playing;
            }
        }
    }

    /// <summary>
    /// Starts or pauses this stream after verifying its owning mixer membership.
    /// </summary>
    public void Play(PlayWith flagPlayWith = PlayWith.RESTART)
    {
        using BassAudioOperationLease operation =
            Ribbit.Media.Audio.BassAudioRuntime.EnterAudioOperation();
        lock (mixerSourceSync)
        {
            ProcessPendingEndCleanup();
            BassAudioSession session = GetOwningSessionForOperation();
            IsMuted = flagPlayWith.HasFlag(PlayWith.MUTE);
            BassMixerSourceAttachment attachment;
            try
            {
                attachment = mixerSourceController.EnsureAttachedPaused(
                    session.MixerHandle,
                    _handle,
                    FileName);
            }
            catch (BassAudioPlaybackException exception)
            {
                TryLogPlayerPlaybackFailure(
                    "BASS player mixer attachment failed",
                    exception,
                    FileName,
                    _handle,
                    session.MixerHandle,
                    exception.ActualMixerHandle,
                    session,
                    playState,
                    voiceCounted,
                    HasPendingEndCleanup,
                    newlyAttached: false,
                    rollbackAttempted: false,
                    rollbackSucceeded: false);
                throw;
            }
            // A verified existing membership may be the result of a benign add race.  Voice
            // accounting is idempotent and must reflect the observed membership, not which
            // thread won the native add call.
            MarkVoiceAttachedOnce();

            try
            {
                if (attachment.NewlyAttached || !sourceMatrixConfigured)
                {
                    if (!attachment.NewlyAttached)
                    {
                        mixerSourceController.Pause(session.MixerHandle, _handle, FileName);
                    }

                    FloatWaveSource sourceAdapter = _floatWaveSource
                        ?? throw new InvalidOperationException("The player has no float WAVE source adapter.");
                    mixerSourceController.ConfigurePausedSource(
                        session.MixerHandle,
                        _handle,
                        sourceAdapter.ChannelLayout,
                        FileName);
                    sourceMatrixConfigured = true;
                }

                bool resetGeneration = flagPlayWith.HasFlag(PlayWith.RESTART);
                if (resetGeneration)
                {
                    SetPositionCore(TimeSpan.Zero, session);
                }

                EnsureEndSyncForPlayback(session);
                if (flagPlayWith.HasFlag(PlayWith.PAUSE))
                {
                    mixerSourceController.Pause(session.MixerHandle, _handle, FileName);
                    playState = PlayState.Paused;
                    return;
                }

                mixerSourceController.Resume(session.MixerHandle, _handle, FileName);
                playState = PlayState.Playing;
                return;
            }
            catch (BassAudioPlaybackException exception)
            {
                bool rollbackAttempted = false;
                bool rollbackSucceeded = false;
                if (attachment.NewlyAttached)
                {
                    rollbackAttempted = true;
                    rollbackSucceeded = TryRollbackNewAttachment(attachment, session, exception);
                    if (rollbackSucceeded)
                    {
                        sourceMatrixConfigured = false;
                    }
                }

                TryLogPlayerPlaybackFailure(
                    "BASS player playback operation failed",
                    exception,
                    FileName,
                    _handle,
                    session.MixerHandle,
                    attachment.ActualMixerHandle,
                    session,
                    playState,
                    voiceCounted,
                    HasPendingEndCleanup,
                    attachment.NewlyAttached,
                    rollbackAttempted,
                    rollbackSucceeded);
                throw;
            }
        }
    }

    public void Stop()
    {
        using BassAudioOperationLease operation =
            Ribbit.Media.Audio.BassAudioRuntime.EnterAudioOperation();
        lock (mixerSourceSync)
        {
            ProcessPendingEndCleanup();
            if (_handle == 0)
            {
                return;
            }

            BassAudioSession session = GetOwningSessionForOperation();
            InvalidateEndSync(session);
            BassMixerSourceRemoval removal = mixerSourceController.RemoveFromExpectedMixer(
                session.MixerHandle,
                _handle,
                FileName);
            sourceMatrixConfigured = false;
            if (removal.AlreadyDetached)
            {
                MarkVoiceDetachedOnce();
                playState = PlayState.Stopped;
                SetPositionCore(TimeSpan.Zero, session);
                return;
            }

            MarkVoiceDetachedOnce();
            playState = PlayState.Stopped;
            SetPositionCore(TimeSpan.Zero, session);
        }
    }

    private BassAudioSession GetOwningSessionForOperation()
    {
        BassAudioSession admittedSession = SessionLifecycle.CurrentSessionForAdmittedOperation;
        if (admittedSession == null
            || !ReferenceEquals(admittedSession, owningSession)
            || admittedSession.State != BassAudioSessionState.Active)
        {
            throw CreatePlaybackException(
                BassAudioPlaybackStage.MixerMembership,
                FileName,
                _handle,
                owningSession?.MixerHandle ?? 0,
                0,
                nameof(BassAudioSessionLifecycle),
                null,
                "The player no longer belongs to the admitted active audio session.");
        }

        if (admittedSession.MixerHandle == 0)
        {
            throw CreatePlaybackException(
                BassAudioPlaybackStage.MixerMembership,
                FileName,
                _handle,
                0,
                0,
                nameof(BassAudioSession),
                null,
                "The owning audio session does not expose a mixer handle.");
        }

        return admittedSession;
    }

    private void SetPositionCore(TimeSpan position, BassAudioSession session)
    {
        long nativePosition = Bass.ChannelSeconds2Bytes(_handle, position.TotalSeconds);
        if (nativePosition < 0)
        {
            Errors error = Bass.LastError;
            throw CreatePlaybackException(
                BassAudioPlaybackStage.SetPosition,
                FileName,
                _handle,
                session?.MixerHandle ?? 0,
                0,
                "BASS_ChannelSeconds2Bytes",
                error,
                "Converting the requested source position failed.");
        }

        mixerSourceController.SetPosition(
            _handle,
            nativePosition,
            FileName,
            session?.MixerHandle ?? 0);
    }

    private void EnsureEndSyncForPlayback(BassAudioSession session)
    {
        // Every Play call establishes a new logical playback activation.  This is required even
        // for PAUSE/DEFAULT: a callback that lost the mixer lock during the previous activation
        // may publish pending cleanup after this method's initial pending check.
        AdvancePlaybackGeneration();
        RemoveEndSync(session);
        ClearStalePendingEndCleanup();

        int syncHandle = Bass.ChannelSetSync(
            _handle,
            SyncFlags.End | SyncFlags.Mixtime | SyncFlags.Onetime,
            0L,
            EndProc,
            new IntPtr(playbackGeneration));
        if (syncHandle == 0)
        {
            Errors error = Bass.LastError;
            throw CreatePlaybackException(
                BassAudioPlaybackStage.SourceTracking,
                FileName,
                _handle,
                session?.MixerHandle ?? 0,
                0,
                "BASS_ChannelSetSync",
                error,
                "Registering the source end callback failed.",
                session: session);
        }

        endSyncHandle = syncHandle;
    }

    private void InvalidateEndSync(BassAudioSession session)
    {
        AdvancePlaybackGeneration();
        RemoveEndSync(session);
        ClearStalePendingEndCleanup();
    }

    private void RemoveEndSync(BassAudioSession session)
    {
        if (endSyncHandle == 0)
        {
            return;
        }

        int syncHandle = endSyncHandle;
        if (!Bass.ChannelRemoveSync(_handle, syncHandle))
        {
            Errors error = Bass.LastError;
            if (error != Errors.Handle
                && error != Errors.Init)
            {
                throw CreatePlaybackException(
                    BassAudioPlaybackStage.SourceTracking,
                    FileName,
                    _handle,
                    session?.MixerHandle ?? 0,
                    0,
                    "BASS_ChannelRemoveSync",
                    error,
                    "Removing the previous source end callback failed.",
                    session: session);
            }
        }

        endSyncHandle = 0;
    }

    private void AdvancePlaybackGeneration()
    {
        Volatile.Write(
            ref playbackGeneration,
            GetNextPlaybackGeneration(Volatile.Read(ref playbackGeneration)));
    }

    /// <summary>
    /// Returns the next non-zero playback generation used to reject stale end callbacks.
    /// </summary>
    internal static int GetNextPlaybackGeneration(int currentGeneration)
    {
        int nextGeneration = unchecked(currentGeneration + 1);
        return nextGeneration == 0 ? 1 : nextGeneration;
    }

    /// <summary>
    /// Determines whether a native end callback belongs to the current playback generation.
    /// </summary>
    internal static bool IsCurrentPlaybackGeneration(int currentGeneration, int callbackGeneration)
        => currentGeneration == callbackGeneration;

    /// <summary>
    /// Determines whether a pending end cleanup may publish without replacing a newer callback.
    /// </summary>
    internal static bool ShouldPublishPendingEndCleanup(
        int currentGeneration,
        int pendingGeneration,
        int callbackGeneration)
    {
        if (callbackGeneration == 0
            || (!IsCurrentPlaybackGeneration(currentGeneration, callbackGeneration)
                && !IsPlaybackGenerationNewer(callbackGeneration, currentGeneration)))
        {
            return false;
        }

        return pendingGeneration == 0
            || pendingGeneration == callbackGeneration
            || IsPlaybackGenerationNewer(callbackGeneration, pendingGeneration);
    }

    private static bool IsPlaybackGenerationNewer(int candidateGeneration, int existingGeneration)
        => unchecked(candidateGeneration - existingGeneration) > 0;

    private bool HasPendingEndCleanup => Volatile.Read(ref pendingEndGeneration) != 0;

    private void PublishPendingEndCleanup(int callbackGeneration)
    {
        while (true)
        {
            int currentGeneration = Volatile.Read(ref playbackGeneration);
            int pendingGeneration = Volatile.Read(ref pendingEndGeneration);
            if (!ShouldPublishPendingEndCleanup(
                    currentGeneration,
                    pendingGeneration,
                    callbackGeneration))
            {
                return;
            }

            if (pendingGeneration == callbackGeneration)
            {
                return;
            }

            if (Interlocked.CompareExchange(
                    ref pendingEndGeneration,
                    callbackGeneration,
                    pendingGeneration) != pendingGeneration)
            {
                continue;
            }

            // A new Play may have advanced the generation immediately after the CAS.  Clear
            // only our own stale value; a newer callback's pending value is never overwritten.
            int observedGeneration = Volatile.Read(ref playbackGeneration);
            if (!IsCurrentPlaybackGeneration(observedGeneration, callbackGeneration)
                && IsPlaybackGenerationNewer(observedGeneration, callbackGeneration))
            {
                Interlocked.CompareExchange(
                    ref pendingEndGeneration,
                    0,
                    callbackGeneration);
            }

            return;
        }
    }

    private void ClearStalePendingEndCleanup()
    {
        int currentGeneration = Volatile.Read(ref playbackGeneration);
        int pendingGeneration = Volatile.Read(ref pendingEndGeneration);
        if (pendingGeneration != 0
            && !IsCurrentPlaybackGeneration(currentGeneration, pendingGeneration))
        {
            Interlocked.CompareExchange(
                ref pendingEndGeneration,
                0,
                pendingGeneration);
        }
    }

    private bool ProcessPendingEndCleanup()
    {
        int pendingGeneration = Volatile.Read(ref pendingEndGeneration);
        if (pendingGeneration == 0)
        {
            return false;
        }

        if (!IsCurrentPlaybackGeneration(
                Volatile.Read(ref playbackGeneration),
                pendingGeneration))
        {
            Interlocked.CompareExchange(ref pendingEndGeneration, 0, pendingGeneration);
            return false;
        }

        if (_handle == 0)
        {
            MarkVoiceDetachedOnce();
            playState = PlayState.Stopped;
            Interlocked.CompareExchange(ref pendingEndGeneration, 0, pendingGeneration);
            return true;
        }

        // source ENDはSRC先読み時点です。接続を残し、出力へ残りのPCMを渡します。
        MarkVoiceDetachedOnce();
        playState = PlayState.Stopped;
        Interlocked.CompareExchange(ref pendingEndGeneration, 0, pendingGeneration);
        return true;
    }

    private bool TryRollbackNewAttachment(
        BassMixerSourceAttachment attachment,
        BassAudioSession session,
        BassAudioPlaybackException primaryException)
    {
        try
        {
            InvalidateEndSync(session);
            BassMixerSourceRemoval removal = mixerSourceController.RemoveFromExpectedMixer(
                session.MixerHandle,
                _handle,
                FileName);
            MarkVoiceDetachedOnce();
            return true;
        }
        catch (Exception rollbackException)
        {
            TryLogPlayerPlaybackFailure(
                "BASS player playback rollback failed",
                rollbackException,
                FileName,
                _handle,
                session.MixerHandle,
                attachment.ActualMixerHandle,
                session,
                playState,
                voiceCounted,
                HasPendingEndCleanup,
                attachment.NewlyAttached,
                rollbackAttempted: true,
                rollbackSucceeded: false);
            TryLogPlayerPlaybackFailure(
                "BASS player playback primary failure retained after rollback failure",
                primaryException,
                FileName,
                _handle,
                session.MixerHandle,
                attachment.ActualMixerHandle,
                session,
                playState,
                voiceCounted,
                HasPendingEndCleanup,
                attachment.NewlyAttached,
                 rollbackAttempted: true,
                 rollbackSucceeded: false);
            return false;
        }
    }

    private void HandleNaturalEndCallback(
        BassAudioSession callbackSession,
        int callbackHandle,
        int callbackGeneration)
    {
        if (!ReferenceEquals(callbackSession, owningSession)
            || _handle != callbackHandle
            || !IsCurrentPlaybackGeneration(
                Volatile.Read(ref playbackGeneration),
                callbackGeneration))
        {
            return;
        }

        if (!Monitor.TryEnter(mixerSourceSync))
        {
            PublishPendingEndCleanup(callbackGeneration);
            return;
        }

        try
        {
            try
            {
                if (!ReferenceEquals(callbackSession, owningSession)
                    || _handle != callbackHandle
                    || !IsCurrentPlaybackGeneration(playbackGeneration, callbackGeneration))
                {
                    return;
                }

                // BASS_SYNC_ONETIME removes the native synchronizer before invoking us.  Drop
                // only the matching managed handle; a newer playback may already have armed
                // another synchronizer.
                endSyncHandle = 0;
                // 復号終了と出力終端は異なるため、SRC尾部を消すdetachをここで行いません。

                MarkVoiceDetachedOnce();
                playState = PlayState.Stopped;
                Interlocked.CompareExchange(
                    ref pendingEndGeneration,
                    0,
                    callbackGeneration);
            }
            catch (Exception exception)
            {
                PublishPendingEndCleanup(callbackGeneration);
                TryLogPlayerPlaybackFailure(
                    "BASS source natural-end cleanup failed",
                    exception,
                    FileName,
                    callbackHandle,
                    callbackSession.MixerHandle,
                    0,
                    callbackSession,
                    playState,
                    voiceCounted,
                    HasPendingEndCleanup,
                    newlyAttached: false,
                    rollbackAttempted: false,
                    rollbackSucceeded: false);
            }
        }
        finally
        {
            Monitor.Exit(mixerSourceSync);
        }
    }

    private void MarkVoiceAttachedOnce()
    {
        lock (mixerSourceSync)
        {
            if (voiceCounted)
            {
                return;
            }

            voiceCounted = true;
            lock (StaticLockObject)
            {
                CurrentVoices++;
                if (MaxVoices < CurrentVoices)
                {
                    MaxVoices = CurrentVoices;
                }
            }
        }
    }

    private void MarkVoiceDetachedOnce()
    {
        lock (mixerSourceSync)
        {
            if (!voiceCounted)
            {
                return;
            }

            voiceCounted = false;
            lock (StaticLockObject)
            {
                if (CurrentVoices > 0)
                {
                    CurrentVoices--;
                }
                else
                {
                    TryLogPlayerCleanupFailure(
                        "BASS player voice-count invariant was already zero while detaching",
                        null);
                }
            }
        }
    }

    private bool Dispose(bool disposing)
    {
        lock (disposeSync)
        {
            if (disposedValue)
            {
                return true;
            }
        }

        BassAudioOperationLease operation;
        bool entered = disposing
            ? Ribbit.Media.Audio.BassAudioRuntime.TryEnterAudioOperation(out operation)
            : Ribbit.Media.Audio.BassAudioRuntime.TryEnterAudioCallbackOperation(out operation);
        if (!entered)
        {
            lock (disposeSync)
            {
                return disposedValue;
            }
        }

        using (operation)
            try
            {
                // Keep the same instance lifecycle boundary from Stop through native free.  A
                // concurrent Play must either finish before disposal enters this lock or observe
                // the confirmed disposed handle afterwards; it must not reattach between them.
                lock (mixerSourceSync)
                {
                    int ownedHandle;
                    lock (disposeSync)
                    {
                        if (disposedValue)
                        {
                            return true;
                        }
                        ownedHandle = _handle;
                    }

                    if (ownedHandle != 0)
                    {
                        try
                        {
                            Stop();
                        }
                        catch (Exception exception)
                        {
                            TryLogPlayerCleanupFailure("BASS source stream stop failed", exception);
                        }

                        bool released = Bass.StreamFree(ownedHandle);
                        if (!released)
                        {
                            Errors error = Bass.LastError;
                            released = error == Errors.Init;
                            if (!released)
                            {
                                TryLogPlayerCleanupFailure(
                                    "BASS_StreamFree(" + ownedHandle + ") failed: "
                                    + BassNativeErrorFormatter.Format(error),
                                    null);
                            }
                        }

                        if (!released)
                        {
                            return false;
                        }

                        if (owningSession != null)
                        {
                            owningSession.ConfirmPlayerStreamReleased(ownedHandle);
                        }
                        ConfirmNativeStreamReleased(ownedHandle);
                    }
                    else
                    {
                        ConfirmNativeStreamReleased(ownedHandle);
                    }

                    lock (disposeSync)
                    {
                        return disposedValue;
                    }
                }
            }
            catch (Exception exception) when (!disposing)
            {
                TryLogPlayerCleanupFailure("BASS source stream finalizer cleanup failed", exception);
                return false;
            }
    }

    private void ConfirmNativeStreamReleased(int releasedHandle)
    {
        lock (disposeSync)
        {
            if (disposedValue)
            {
                return;
            }
            if (_handle != 0 && releasedHandle != 0 && _handle != releasedHandle)
            {
                return;
            }

            _handle = 0;
            owningSession = null;
            Interlocked.Exchange(ref pendingEndGeneration, 0);
            disposedValue = true;
        }

        MarkVoiceDetachedOnce();
        ReleaseInputSource();
        GC.SuppressFinalize(this);
    }

    private void ReleaseInputSource()
    {
        _floatWaveSource = null;
        _fileProcedures = default;
    }

    private static BassAudioPlaybackException CreatePlaybackException(
        BassAudioPlaybackStage stage,
        string fileName,
        int sourceHandle,
        int expectedMixerHandle,
        int actualMixerHandle,
        string nativeErrorSource,
        Errors? nativeErrorCode,
        string message,
        Exception innerException = null,
        BassAudioSession session = null)
        => new(
            stage,
            fileName,
            sourceHandle,
            expectedMixerHandle,
            actualMixerHandle,
            nativeErrorSource,
            nativeErrorCode,
            message,
            session ?? TryGetCurrentSessionForDiagnostics(),
            innerException);

    private static BassAudioSession TryGetCurrentSessionForDiagnostics()
    {
        try
        {
            return SessionLifecycle.CurrentSessionForAdmittedOperation;
        }
        catch
        {
            return null;
        }
    }

    private static void TryLogPlayerPlaybackFailure(
        string operation,
        Exception exception,
        string fileName,
        int sourceHandle,
        int expectedMixerHandle,
        int actualMixerHandle,
        BassAudioSession session,
        PlayState? managedPlayState,
        bool voiceCounted,
        bool endCleanupPending,
        bool newlyAttached,
        bool rollbackAttempted,
        bool rollbackSucceeded)
    {
        try
        {
            var playbackException = exception as BassAudioPlaybackException;
            string nativeErrorSource = playbackException?.NativeErrorSource ?? "none";
            ManagedBass.Errors? nativeErrorCode = playbackException?.NativeErrorCode;
            string stage = playbackException?.Stage.ToString() ?? "unknown";
            string backend = playbackException?.Backend?.ToString()
                ?? session?.ActualBackend.ToString()
                ?? "unknown";
            string sessionState = playbackException?.SessionState?.ToString()
                ?? session?.State.ToString()
                ?? "unknown";
            string coreDevice = playbackException?.CoreDeviceIndex?.ToString()
                ?? session?.CoreDeviceIndex.ToString()
                ?? "unknown";
            NLogWrapper.GetLogger(nameof(BassAudioPlayer)).Warn(
                operation
                + " stage=" + stage
                + " file=" + fileName
                + " sourceHandle=" + sourceHandle
                + " expectedMixerHandle=" + expectedMixerHandle
                + " actualMixerHandle=" + actualMixerHandle
                + " backend=" + backend
                + " sessionState=" + sessionState
                + " coreDevice=" + coreDevice
                + " owningBackend=" + (session?.ActualBackend.ToString() ?? "unknown")
                + " owningSessionState=" + (session?.State.ToString() ?? "unknown")
                + " coreDeviceIndex=" + (session?.CoreDeviceIndex.ToString() ?? "unknown")
                + " managedPlayState=" + (managedPlayState?.ToString() ?? "unknown")
                + " voiceCounted=" + voiceCounted
                + " endCleanupPending=" + endCleanupPending
                + " nativeErrorSource=" + nativeErrorSource
                + " nativeErrorCode="
                + Ribbit.Media.Audio.BassNativeErrorFormatter.Format(nativeErrorCode)
                + " newlyAttached=" + newlyAttached
                + " rollbackAttempted=" + rollbackAttempted
                + " rollbackSucceeded=" + rollbackSucceeded
                + " error=" + (exception?.Message ?? "none"));
        }
        catch
        {
            // Playback diagnostics must never replace the primary playback failure.
        }
    }

    private static void TryLogPlayerCleanupFailure(string message, Exception exception)
    {
        try
        {
            NLogWrapper.GetLogger("AudioSession").Warn(
                exception == null ? message : message + ": " + exception.Message);
        }
        catch
        {
            // Finalizer and cleanup diagnostics must not replace the ownership contract.
        }
    }

    ~BassAudioPlayer()
    {
        try
        {
            Dispose(disposing: false);
        }
        catch
        {
            // A finalizer must never terminate the process; the session retains unconfirmed ownership.
        }
    }

    public void Dispose()
    {
        if (Dispose(disposing: true))
        {
            GC.SuppressFinalize(this);
        }
    }
}
