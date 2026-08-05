using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.IO;
using System.Linq;
using System.Runtime;
using System.Runtime.InteropServices;
using System.Threading;
using BeMusicSeeker.Models.Utils;
using NVorbis;
using Ribbit.Cryptography;
using Ribbit.Logging;
using Ribbit.Media.Audio;
using Ribbit.Util;
using Un4seen.Bass;
using Un4seen.Bass.AddOn.Enc;
using Un4seen.Bass.AddOn.Fx;
using Un4seen.Bass.AddOn.Mix;
using Un4seen.BassAsio;
using Un4seen.BassWasapi;

namespace Ribbit.Media;

public class BassAudioPlayer : IAudioPlayer, IDisposable
{
    private class CachedData
    {
        internal int RefCount { get; set; }

        internal byte[] Data { get; }

        internal CachedData(byte[] data)
        {
            Data = data;
            RefCount = 1;
        }
    }

    public struct DeviceDescriptor(string name, string driver)
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
        NULL_DEVICE,
        DIRECT_SOUND,
        WASAPI_SHARED,
        WASAPI_EXCLUSIVE,
        ASIO
    }

    private static readonly object StaticLockObject;

    private static readonly Dictionary<int, object> InstanceLocks;

    private static readonly NamedLocks<uint> Locks;

    private static readonly Dictionary<uint, CachedData> OnMemoryFileCache;

    protected static int inputMixer;

    protected static int outputMixer;

    protected static int procChannel;

    protected static int tempoChanger;

    protected static int volumeEffect;

    protected static int equalizer;

    private static readonly WASAPIPROC WasapiProc;

    private static readonly ASIOPROC AsioProc;

    private static readonly STREAMPROC StreamProc;

    private static readonly SYNCPROC EndProc;

    private static float playbackRate;

    private static readonly ReadOnlyDictionary<Type, BASSFXType> FxParameterTypeToBASSFXType;

    private static readonly ConcurrentDictionary<BASSFXType, Tuple<int, object>> FxParameters;

    public static readonly ReadOnlyCollection<float> EqualizerFrequencies;

    private static List<float> equalizerGains;

    private static readonly BassAudioSessionLifecycle SessionLifecycle;

    private static readonly IAudioSessionNativeBoundary SessionNative;

    private static readonly BassWasapiNegotiator WasapiNegotiator;

    private static readonly BassDirectSoundNegotiator DirectSoundNegotiator;

    private static string initializationStage;

    private static float latencyParam;

    private static SampleRate _frequency;

    private static SampleFormat _format;

    private static float _defaultVolume;

    private const float _volumeInitValue = 0.4f;

    private static float _deviceVolume;

    private static bool _isDeviceMuted;

    private static float _prevMasterVolume;

    private byte[] _sampleBuffer;

    private int _sampleBufferPos;

    private readonly BASS_FILEPROCS fileProc;

    private PlayState playState;

    private bool isMuted;

    private float prevVolume;

    private int _handle;

    private float _volume;

    private readonly uint fileNameHash;

    private readonly object disposeSync = new();

    private BassAudioSession owningSession;

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
        inputMixer = (outputMixer = (procChannel = (tempoChanger = 0)));
        volumeEffect = (equalizer = 0);
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
                Ribbit.Media.Audio.BassNet.EnterAudioOperation();
            long pos = Bass.BASS_ChannelGetPosition(_handle);
            return TimeSpan.FromSeconds(Bass.BASS_ChannelBytes2Seconds(_handle, pos));
        }
        set
        {
            using BassAudioOperationLease operation =
                Ribbit.Media.Audio.BassNet.EnterAudioOperation();
            long pos = Bass.BASS_ChannelSeconds2Bytes(_handle, value.TotalSeconds);
            Bass.BASS_ChannelSetPosition(_handle, pos);
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
                        Ribbit.Media.Audio.BassNet.EnterAudioOperation();
                    Bass.BASS_ChannelSetAttribute(_handle, BASSAttribute.BASS_ATTRIB_VOL, value);
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
                        Ribbit.Media.Audio.BassNet.EnterAudioOperation();
                    Bass.BASS_ChannelSetAttribute(_handle, BASSAttribute.BASS_ATTRIB_VOL, 0f);
                }
                else
                {
                    using BassAudioOperationLease operation =
                        Ribbit.Media.Audio.BassNet.EnterAudioOperation();
                    Bass.BASS_ChannelSetAttribute(_handle, BASSAttribute.BASS_ATTRIB_VOL, prevVolume);
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
    internal static bool Free(BassAudioSession expectedSession)
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

        if (!Ribbit.Media.Audio.BassNet.TryEnterAudioSessionCleanup(
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
    /// Initializes one audio session while preserving the legacy backend order.
    /// </summary>
    public static DeviceDescriptor Initialize(
        DeviceDriver driver = DeviceDriver.WASAPI_EXCLUSIVE,
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
        try
        {
            Ribbit.Media.Audio.BassNet.Initialize();
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
                foreach (DeviceDriver backend in GetLegacyInitializationOrder(driver))
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
                            DeviceDriver.DIRECT_SOUND => InitializeDirectSoundNegotiated(attemptDevice),
                            DeviceDriver.NULL_DEVICE => InitializeNullDevice(),
                            _ => throw new ArgumentOutOfRangeException(nameof(driver))
                        };
                        if (session.NegotiationResult != null && earlierAttempts.Count != 0)
                        {
                            string earlierFallbackReason = string.Join(
                                "; ",
                                crossBackendFallbackReasons)
                                + "; fallbackDestination=" + backend;
                            session.NegotiationResult = session.NegotiationResult.WithEarlierAttempts(
                                earlierAttempts,
                                earlierFallbackReason);
                        }
                        session.ActualDevice = CurrentSession.ActualDevice.Equals(default(DeviceDescriptor))
                            ? actualDescriptor
                            : CurrentSession.ActualDevice;
                        SessionLifecycle.MarkActive(session);
                        if (backend == DeviceDriver.NULL_DEVICE)
                        {
                            DeviceVolume = 0.4f;
                            DefaultVolume = 0.4f;
                        }
                        else
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
                            "backend=" + backend + " failed: " + contextual.Message));
                        crossBackendFallbackReasons.Add(
                            "attemptedBackend=" + backend
                            + " stage=" + contextual.Stage
                            + " nativeErrorSource=" + contextual.NativeErrorSource
                            + " nativeErrorCode=" + contextual.NativeErrorCode);
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
                "Audio initialization attempt failed. requestedBackend=" + requestedBackend
                + " requestedDevice=" + DescribeDevice(requestedDevice)
                + " requestedRate=" + requestedRate
                + " requestedFormat=" + requestedFormat
                + " requestedBufferMs=" + requestedBuffer
                + " requestedEventMode=" + requestedEventMode
                + " attemptedBackend=" + attemptedBackend
                + " stage=" + exception.Stage
                + " actualDevice=" + DescribeDevice(exception.ActualDevice)
                + " nativeErrorSource=" + exception.NativeErrorSource
                + " nativeErrorCode=" + exception.NativeErrorCode
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
                "Audio initialization attempt started. requestedBackend=" + requestedBackend
                + " requestedDevice=" + DescribeDevice(requestedDevice)
                + " requestedRate=" + requestedRate
                + " requestedFormat=" + requestedFormat
                + " requestedBufferMs=" + requestedBuffer
                + " requestedEventMode=" + requestedEventMode
                + " attemptedBackend=" + attemptedBackend
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
        return "Audio initialization attempt succeeded. requestedBackend=" + requestedBackend
                + " requestedDevice=" + DescribeDevice(requestedDevice)
                + " requestedRate=" + requestedRate
                + " requestedFormat=" + requestedFormat
                + " requestedBufferMs=" + requestedBuffer
                + " requestedEventMode=" + requestedEventMode
                + " attemptedBackend=" + attemptedBackend
                + " stage=completed nativeErrorSource=none nativeErrorCode=none"
                + " actualBackend=" + session.ActualBackend
                + " actualDevice=" + DescribeDevice(session.ActualDevice)
                + " actualRate=" + result?.ActualRate
                + " actualChannels=" + result?.ActualChannels
                + " engineFormat=" + result?.EngineFormat
                + " endpointFormat=" + result?.EndpointFormat
                + " latencyMs=" + result?.LatencyMilliseconds
                + " fallbackOccurred=" + fallbackOccurred
                + " fallbackDestination=" + (fallbackOccurred ? session.ActualBackend.ToString() : "none")
                + " fallbackReason=" + (fallbackOccurred ? result?.FallbackReason : "none")
                + " " + runtimeVersionDiagnostics;
    }

    private static string DescribeDevice(DeviceDescriptor device)
    {
        return device.Equals(default(DeviceDescriptor))
            ? "<default>"
            : "[name=" + device.Name + ",identity=" + device.Driver + "]";
    }

    private static string GetRuntimeVersionDiagnostics()
    {
        return BuildRuntimeVersionDiagnostics(
            Environment.OSVersion.VersionString,
            Bass.BASS_GetVersion(),
            BassWasapi.BASS_WASAPI_GetVersion(),
            BassAsio.BASS_ASIO_GetVersion(),
            BassMix.BASS_Mixer_GetVersion(),
            BassFx.BASS_FX_GetVersion(),
            BassEnc.BASS_Encode_GetVersion());
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
            return Ribbit.Media.Audio.BassNet.EnterAudioSessionInitialization();
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

    private static IReadOnlyList<DeviceDriver> GetLegacyInitializationOrder(DeviceDriver driver)
    {
        return driver switch
        {
            DeviceDriver.ASIO =>
                [DeviceDriver.ASIO, DeviceDriver.WASAPI_EXCLUSIVE, DeviceDriver.WASAPI_SHARED, DeviceDriver.DIRECT_SOUND],
            DeviceDriver.WASAPI_EXCLUSIVE =>
                [DeviceDriver.WASAPI_EXCLUSIVE, DeviceDriver.WASAPI_SHARED, DeviceDriver.DIRECT_SOUND],
            DeviceDriver.WASAPI_SHARED =>
                [DeviceDriver.WASAPI_SHARED, DeviceDriver.DIRECT_SOUND],
            DeviceDriver.DIRECT_SOUND => [DeviceDriver.DIRECT_SOUND],
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
        if (procChannel != 0 && !session.AdditionalStreamHandles.Contains(procChannel))
        {
            session.AdditionalStreamHandles.Add(procChannel);
        }
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
        BASSError? error;
        if (initializationStage.StartsWith("BASS_ASIO", StringComparison.Ordinal))
        {
            source = "BASSASIO";
            error = BassAsio.BASS_ASIO_ErrorGetCode();
        }
        else
        {
            source = initializationStage.StartsWith("BASS_WASAPI", StringComparison.Ordinal)
                ? "BASSWASAPI/BASS_ErrorGetCode"
                : "BASS";
            error = Bass.BASS_ErrorGetCode();
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
        DirectSoundNegotiator = new BassDirectSoundNegotiator(new BassDirectSoundNegotiationNativeBoundary());
        StaticLockObject = new object();
        InstanceLocks = [];
        Locks = new NamedLocks<uint>();
        OnMemoryFileCache = [];
        WasapiProc = (buffer, length, user) => ReadCallbackOutput(buffer, length);
        AsioProc = (input, channel, buffer, length, user) => ReadCallbackOutput(buffer, length);
        StreamProc = delegate (int handle, IntPtr buffer, int length, IntPtr user)
        {
            if (!Ribbit.Media.Audio.BassNet.TryEnterAudioCallbackOperation(
                out BassAudioOperationLease operation))
            {
                return 0;
            }
            using (operation)
            {
                int val = Bass.BASS_ChannelGetData(inputMixer, buffer, length);
                return System.Math.Max(0, val);
            }
        };
        EndProc = delegate (int handle, int channel, int data, IntPtr user)
        {
            if (!Ribbit.Media.Audio.BassNet.TryEnterAudioCallbackOperation(
                out BassAudioOperationLease operation))
            {
                return;
            }
            using (operation)
            {
                object obj2;
                lock (StaticLockObject)
                {
                    obj2 = InstanceLocks[channel];
                }
                if (!Monitor.TryEnter(obj2))
                {
                    NLogWrapper.TraceLogger?.Trace("EndProc get lock failed");
                    return;
                }
                try
                {
                    if (BassMix.BASS_Mixer_ChannelRemove(channel))
                    {
                        lock (StaticLockObject)
                        {
                            CurrentVoices--;
                            return;
                        }
                    }
                    BASSError bASSError = Bass.BASS_ErrorGetCode();
                    NLogWrapper.TraceLogger?.Warn(string.Concat("BASS_Mixer_ChannelRemove failed: ", bASSError, channel.ToString()));
                }
                catch
                {
                    throw;
                }
                finally
                {
                    Monitor.Exit(obj2);
                }
            }
        };
        playbackRate = 1f;
#pragma warning disable CS0618
        FxParameterTypeToBASSFXType = new ReadOnlyDictionary<Type, BASSFXType>(new Dictionary<Type, BASSFXType>
        {
            {
                typeof(BASS_DX8_CHORUS),
                BASSFXType.BASS_FX_DX8_CHORUS
            },
            {
                typeof(BASS_DX8_COMPRESSOR),
                BASSFXType.BASS_FX_DX8_COMPRESSOR
            },
            {
                typeof(BASS_DX8_DISTORTION),
                BASSFXType.BASS_FX_DX8_DISTORTION
            },
            {
                typeof(BASS_DX8_ECHO),
                BASSFXType.BASS_FX_DX8_ECHO
            },
            {
                typeof(BASS_DX8_FLANGER),
                BASSFXType.BASS_FX_DX8_FLANGER
            },
            {
                typeof(BASS_DX8_GARGLE),
                BASSFXType.BASS_FX_DX8_GARGLE
            },
            {
                typeof(BASS_DX8_I3DL2REVERB),
                BASSFXType.BASS_FX_DX8_I3DL2REVERB
            },
            {
                typeof(BASS_DX8_PARAMEQ),
                BASSFXType.BASS_FX_DX8_PARAMEQ
            },
            {
                typeof(BASS_DX8_REVERB),
                BASSFXType.BASS_FX_DX8_REVERB
            },
            {
                typeof(BASS_BFX_ROTATE),
                BASSFXType.BASS_FX_BFX_ROTATE
            },
            {
                typeof(BASS_BFX_ECHO),
                BASSFXType.BASS_FX_BFX_ECHO
            },
            {
                typeof(BASS_BFX_FLANGER),
                BASSFXType.BASS_FX_BFX_FLANGER
            },
            {
                typeof(BASS_BFX_VOLUME),
                BASSFXType.BASS_FX_BFX_VOLUME
            },
            {
                typeof(BASS_BFX_PEAKEQ),
                BASSFXType.BASS_FX_BFX_PEAKEQ
            },
            {
                typeof(BASS_BFX_REVERB),
                BASSFXType.BASS_FX_BFX_REVERB
            },
            {
                typeof(BASS_BFX_LPF),
                BASSFXType.BASS_FX_BFX_LPF
            },
            {
                typeof(BASS_BFX_MIX),
                BASSFXType.BASS_FX_BFX_MIX
            },
            {
                typeof(BASS_BFX_DAMP),
                BASSFXType.BASS_FX_BFX_DAMP
            },
            {
                typeof(BASS_BFX_AUTOWAH),
                BASSFXType.BASS_FX_BFX_AUTOWAH
            },
            {
                typeof(BASS_BFX_ECHO2),
                BASSFXType.BASS_FX_BFX_ECHO2
            },
            {
                typeof(BASS_BFX_PHASER),
                BASSFXType.BASS_FX_BFX_PHASER
            },
            {
                typeof(BASS_BFX_ECHO3),
                BASSFXType.BASS_FX_BFX_ECHO3
            },
            {
                typeof(BASS_BFX_CHORUS),
                BASSFXType.BASS_FX_BFX_CHORUS
            },
            {
                typeof(BASS_BFX_APF),
                BASSFXType.BASS_FX_BFX_APF
            },
            {
                typeof(BASS_BFX_COMPRESSOR),
                BASSFXType.BASS_FX_BFX_COMPRESSOR
            },
            {
                typeof(BASS_BFX_DISTORTION),
                BASSFXType.BASS_FX_BFX_DISTORTION
            },
            {
                typeof(BASS_BFX_COMPRESSOR2),
                BASSFXType.BASS_FX_BFX_COMPRESSOR2
            },
            {
                typeof(BASS_BFX_VOLUME_ENV),
                BASSFXType.BASS_FX_BFX_VOLUME_ENV
            },
            {
                typeof(BASS_BFX_BQF),
                BASSFXType.BASS_FX_BFX_BQF
            },
            {
                typeof(BASS_BFX_ECHO4),
                BASSFXType.BASS_FX_BFX_ECHO4
            },
            {
                typeof(BASS_BFX_PITCHSHIFT),
                BASSFXType.BASS_FX_BFX_PITCHSHIFT
            },
            {
                typeof(BASS_BFX_FREEVERB),
                BASSFXType.BASS_FX_BFX_FREEVERB
            }
        });
#pragma warning restore CS0618
        FxParameters = new ConcurrentDictionary<BASSFXType, Tuple<int, object>>();
        EqualizerFrequencies = new List<float> { 32f, 64f, 125f, 250f, 500f, 1000f, 2000f, 4000f, 8000f, 16000f }.AsReadOnly();
        equalizerGains = new float[10].ToList();
        DriverType = DeviceDriver.INVALID;
        _frequency = SampleRate.AUTO;
        _format = SampleFormat.AUTO;
        _defaultVolume = 0.4f;
        _deviceVolume = 0.4f;
        Ribbit.Media.Audio.BassNet.RegisterAudioSessionShutdown(ReleaseCurrentSessionUnderExclusive);
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
            new BassAsioNegotiationNativeBoundary()).Initialize(request, CurrentSession, AsioProc);

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
        if (!isSharedMode)
        {
            volumeEffect = Bass.BASS_ChannelSetFX(inputMixer, BASSFXType.BASS_FX_BFX_VOLUME, 1);
            CurrentSession.VolumeEffectHandle = volumeEffect;
        }
        return desc.Equals(default(DeviceDescriptor)) ? default : result.ActualDevice;
    }

    private static DeviceDescriptor InitializeDirectSoundNegotiated(DeviceDescriptor desc = default)
    {
        initializationStage = "DirectSound negotiation";
        var request = new BassAudioNegotiationRequest(
            DeviceDriver.DIRECT_SOUND,
            desc,
            _frequency,
            _format,
            latencyParam);
        BassAudioBackendResult result = DirectSoundNegotiator.Initialize(
            request,
            CurrentSession,
            StreamProc,
            GetEffectiveDeviceVolumeForInitialization(_deviceVolume, IsDeviceMuted));

        inputMixer = result.MixerHandle;
        outputMixer = CurrentSession.OutputHandle;
        volumeEffect = CurrentSession.VolumeEffectHandle;
        procChannel = outputMixer;
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
        if (!Bass.BASS_Init(0, 44100, BASSInit.BASS_DEVICE_DEFAULT, IntPtr.Zero))
        {
            BASSError bASSError = Bass.BASS_ErrorGetCode();
            throw new Exception("BASS_Init failed: " + bASSError);
        }
        CurrentSession.CoreInitialized = true;
        CurrentSession.CoreDeviceIndex = Bass.BASS_GetDevice();
        Frequency = ((Frequency == SampleRate.AUTO) ? SampleRate.SAMPLE_RATE_44100Hz : Frequency);
        Format = ((Format == SampleFormat.AUTO) ? SampleFormat.SAMPLE_INT_16BIT : Format);
        Latency = 0.0;
        BASSFlag flags = BASSFlag.BASS_SAMPLE_FLOAT | BASSFlag.BASS_STREAM_PRESCAN | BASSFlag.BASS_STREAM_DECODE;
        initializationStage = "BASS_Mixer_StreamCreate";
        inputMixer = BassMix.BASS_Mixer_StreamCreate((int)Frequency, 2, flags);
        if (inputMixer == 0)
        {
            BASSError bASSError2 = Bass.BASS_ErrorGetCode();
            throw new Exception("BASS_Mixer_StreamCreate failed: " + bASSError2);
        }
        volumeEffect = Bass.BASS_ChannelSetFX(inputMixer, BASSFXType.BASS_FX_BFX_VOLUME, 1);
        CurrentSession.VolumeEffectHandle = volumeEffect;
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
        if (!Ribbit.Media.Audio.BassNet.TryEnterAudioOperation(
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
            BASSFlag flags = BASSFlag.BASS_STREAM_DECODE;
            switch (DriverType)
            {
                case DeviceDriver.NULL_DEVICE:
                case DeviceDriver.WASAPI_SHARED:
                case DeviceDriver.WASAPI_EXCLUSIVE:
                case DeviceDriver.ASIO:
                    outputMixer = (tempoChanger = BassFx.BASS_FX_TempoCreate(inputMixer, flags));
                    if (outputMixer == 0)
                    {
                        BASSError bASSError4 = Bass.BASS_ErrorGetCode();
                        throw new Exception("BASS_FX_TempoCreate failed: " + bASSError4);
                    }
                    CurrentSession.TrackOutputHandle(outputMixer);
                    break;
                case DeviceDriver.DIRECT_SOUND:
                    flags = BASSFlag.BASS_DEFAULT;
                    int oldProcChannel = procChannel;
                    if (!TryReleaseTrackedStream(oldProcChannel, "BASS_StreamFree for procChannel"))
                    {
                        return;
                    }
                    procChannel = 0;
                    outputMixer = (tempoChanger = BassFx.BASS_FX_TempoCreate(inputMixer, flags));
                    if (tempoChanger == 0)
                    {
                        BASSError bASSError2 = Bass.BASS_ErrorGetCode();
                        throw new Exception("BASS_FX_TempoCreate failed: " + bASSError2);
                    }
                    CurrentSession.TrackOutputHandle(outputMixer);
                    if (!Bass.BASS_ChannelPlay(outputMixer, restart: false))
                    {
                        BASSError bASSError3 = Bass.BASS_ErrorGetCode();
                        throw new Exception("BASS_ChannelPlay failed: " + bASSError3);
                    }
                    CurrentSession.IsStarted = true;
                    break;
                default:
                    throw new ArgumentOutOfRangeException();
            }
            Bass.BASS_ChannelSetAttribute(tempoChanger, BASSAttribute.BASS_ATTRIB_TEMPO_OPTION_SEQUENCE_MS, 33f);
            Bass.BASS_ChannelSetAttribute(tempoChanger, BASSAttribute.BASS_ATTRIB_TEMPO_OPTION_SEEKWINDOW_MS, 10f);
        }
        if (changeFreq)
        {
            float value = -1f;
            Bass.BASS_ChannelGetAttribute(inputMixer, BASSAttribute.BASS_ATTRIB_FREQ, ref value);
            Bass.BASS_ChannelSetAttribute(tempoChanger, BASSAttribute.BASS_ATTRIB_TEMPO_FREQ, speed * value);
        }
        else
        {
            Bass.BASS_ChannelSetAttribute(tempoChanger, BASSAttribute.BASS_ATTRIB_TEMPO, 100f * (speed - 1f));
        }
        playbackRate = speed;
    }

    /// <summary>Restores the active output graph to its original tempo.</summary>
    public static void ResetTempoChange()
    {
        if (!Ribbit.Media.Audio.BassNet.TryEnterAudioOperation(
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
            case DeviceDriver.DIRECT_SOUND:
                outputMixer = (procChannel = Bass.BASS_StreamCreate((int)Frequency, 2, BASSFlag.BASS_SAMPLE_FLOAT, StreamProc, IntPtr.Zero));
                CurrentSession.TrackOutputHandle(outputMixer);
                if (!Bass.BASS_ChannelPlay(outputMixer, restart: false))
                {
                    BASSError bASSError2 = Bass.BASS_ErrorGetCode();
                    throw new Exception("BASS_ChannelPlay failed: " + bASSError2);
                }
                CurrentSession.IsStarted = true;
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
        if (Bass.BASS_StreamFree(handle))
        {
            CurrentSession.ConfirmStreamReleased(handle);
            return true;
        }

        BASSError error = Bass.BASS_ErrorGetCode();
        if (error == BASSError.BASS_ERROR_INIT)
        {
            TryLogAudioSessionDebug(
                operation + " returned BASS_ERROR_INIT and was treated as already released.");
            CurrentSession.ConfirmStreamReleased(handle);
            return true;
        }

        TryLogAudioSessionWarning(operation + " failed: " + error);
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

    public static void CreateFX(object parameter)
    {
        using BassAudioOperationLease operation =
            Ribbit.Media.Audio.BassNet.EnterAudioOperation();
        if (!IsInitialized)
        {
            return;
        }
        if (parameter == null)
        {
            throw new ArgumentNullException("parameter");
        }
        if (!FxParameterTypeToBASSFXType.TryGetValue(parameter.GetType(), out BASSFXType value))
        {
            return;
        }
        if (FxParameters.TryGetValue(value, out Tuple<int, object> value2) && value2.Item1 != 0)
        {
            FxParameters[value] = new Tuple<int, object>(value2.Item1, parameter);
            if (!Bass.BASS_FXSetParameters(value2.Item1, parameter))
            {
                BASSError bASSError = Bass.BASS_ErrorGetCode();
                NLogWrapper.TraceLogger?.Warn("BASS_FXSetParameters failed: " + bASSError);
            }
        }
        else
        {
            value2 = new Tuple<int, object>(0, parameter);
            FxParameters[value] = value2;
        }
    }

    public static void RemoveFX(BASSFXType fxType)
    {
        using BassAudioOperationLease operation =
            Ribbit.Media.Audio.BassNet.EnterAudioOperation();
        if (IsInitialized && FxParameters.TryRemove(fxType, out Tuple<int, object> value) && value.Item1 != 0 && !Bass.BASS_ChannelRemoveFX(inputMixer, value.Item1))
        {
            BASSError bASSError = Bass.BASS_ErrorGetCode();
            NLogWrapper.TraceLogger?.Warn("BASS_ChannelRemoveFX failed: " + bASSError);
        }
    }

    public static void RemoveFX()
    {
        using BassAudioOperationLease operation =
            Ribbit.Media.Audio.BassNet.EnterAudioOperation();
        if (IsInitialized)
        {
            KeyValuePair<BASSFXType, Tuple<int, object>>[] array = [.. FxParameters];
            foreach (KeyValuePair<BASSFXType, Tuple<int, object>> keyValuePair in array)
            {
                RemoveFX(keyValuePair.Key);
            }
        }
    }

    public static void DisableFX(BASSFXType fxType)
    {
        using BassAudioOperationLease operation =
            Ribbit.Media.Audio.BassNet.EnterAudioOperation();
        if (IsInitialized && FxParameters.TryGetValue(fxType, out Tuple<int, object> value) && value.Item1 != 0)
        {
            if (!Bass.BASS_ChannelRemoveFX(inputMixer, value.Item1))
            {
                BASSError bASSError = Bass.BASS_ErrorGetCode();
                NLogWrapper.TraceLogger?.Warn("BASS_ChannelRemoveFX failed: " + bASSError);
            }
            else
            {
                value = new Tuple<int, object>(0, value.Item2);
                FxParameters[fxType] = value;
            }
        }
    }

    public static void DisableFX()
    {
        using BassAudioOperationLease operation =
            Ribbit.Media.Audio.BassNet.EnterAudioOperation();
        if (IsInitialized)
        {
            KeyValuePair<BASSFXType, Tuple<int, object>>[] array = [.. FxParameters];
            foreach (KeyValuePair<BASSFXType, Tuple<int, object>> keyValuePair in array)
            {
                DisableFX(keyValuePair.Key);
            }
        }
    }

    public static void EnableFX(BASSFXType fxType)
    {
        using BassAudioOperationLease operation =
            Ribbit.Media.Audio.BassNet.EnterAudioOperation();
        if (IsInitialized && FxParameters.TryGetValue(fxType, out Tuple<int, object> value) && value.Item1 == 0)
        {
            value = new Tuple<int, object>(Bass.BASS_ChannelSetFX(inputMixer, fxType, 0), value.Item2);
            if (value.Item1 == 0)
            {
                BASSError bASSError = Bass.BASS_ErrorGetCode();
                NLogWrapper.TraceLogger?.Warn("BASS_ChannelSetFX failed: " + bASSError);
            }
            else
            {
                FxParameters[fxType] = value;
                Bass.BASS_FXSetParameters(value.Item1, value.Item2);
            }
        }
    }

    public static void EnableFX()
    {
        using BassAudioOperationLease operation =
            Ribbit.Media.Audio.BassNet.EnterAudioOperation();
        if (IsInitialized)
        {
            KeyValuePair<BASSFXType, Tuple<int, object>>[] array = [.. FxParameters];
            foreach (KeyValuePair<BASSFXType, Tuple<int, object>> keyValuePair in array)
            {
                EnableFX(keyValuePair.Key);
            }
        }
    }

    public static bool FXCreated(BASSFXType fxType)
    {
        if (FxParameters.TryGetValue(fxType, out Tuple<int, object> _))
        {
            return true;
        }
        return false;
    }

    public static bool FXEnabled(BASSFXType fxType)
    {
        if (FxParameters.TryGetValue(fxType, out Tuple<int, object> value) && value.Item1 != 0)
        {
            return true;
        }
        return false;
    }

    public static void EnableEQ()
    {
        using BassAudioOperationLease operation =
            Ribbit.Media.Audio.BassNet.EnterAudioOperation();
        if (!IsInitialized || EQEnabled)
        {
            return;
        }
        equalizer = Bass.BASS_ChannelSetFX(inputMixer, BASSFXType.BASS_FX_BFX_PEAKEQ, 0);
        var bASS_BFX_PEAKEQ = new BASS_BFX_PEAKEQ
        {
            fQ = 0f,
            fBandwidth = 2.5f,
            lChannel = BASSFXChan.BASS_BFX_CHANALL
        };
        for (int i = 0; i < EqualizerFrequencies.Count; i++)
        {
            bASS_BFX_PEAKEQ.lBand = i;
            bASS_BFX_PEAKEQ.fCenter = EqualizerFrequencies[i];
            bASS_BFX_PEAKEQ.fGain = equalizerGains[i];
            if (!Bass.BASS_FXSetParameters(equalizer, bASS_BFX_PEAKEQ))
            {
                BASSError bASSError = Bass.BASS_ErrorGetCode();
                NLogWrapper.TraceLogger?.Warn("BASS_FXSetParameters (EQ) failed: " + bASSError);
            }
        }
    }

    public static void DisableEQ()
    {
        using BassAudioOperationLease operation =
            Ribbit.Media.Audio.BassNet.EnterAudioOperation();
        if (IsInitialized && EQEnabled)
        {
            if (!Bass.BASS_ChannelRemoveFX(inputMixer, equalizer))
            {
                BASSError bASSError = Bass.BASS_ErrorGetCode();
                NLogWrapper.TraceLogger?.Warn("BASS_ChannelRemoveFX (EQ) failed: " + bASSError);
            }
            else
            {
                equalizer = 0;
            }
        }
    }

    public static void UpdateEQ(int slot, float gain)
    {
        if (slot < 0 || slot > EqualizerFrequencies.Count)
        {
            throw new ArgumentOutOfRangeException("slot");
        }
        using BassAudioOperationLease operation =
            Ribbit.Media.Audio.BassNet.EnterAudioOperation();
        if (EQEnabled)
        {
            var bASS_BFX_PEAKEQ = new BASS_BFX_PEAKEQ
            {
                lBand = slot
            };
            if (!Bass.BASS_FXGetParameters(equalizer, bASS_BFX_PEAKEQ))
            {
                BASSError bASSError = Bass.BASS_ErrorGetCode();
                NLogWrapper.TraceLogger?.Warn("BASS_FXGetParameters (EQ) failed: " + bASSError);
                return;
            }
            bASS_BFX_PEAKEQ.fGain = gain;
            if (!Bass.BASS_FXSetParameters(equalizer, bASS_BFX_PEAKEQ))
            {
                BASSError bASSError2 = Bass.BASS_ErrorGetCode();
                NLogWrapper.TraceLogger?.Warn("BASS_FXSetParameters (EQ) failed: " + bASSError2);
                return;
            }
        }
        equalizerGains[slot] = gain;
    }

    public static void ResetEQ()
    {
        using BassAudioOperationLease operation =
            Ribbit.Media.Audio.BassNet.EnterAudioOperation();
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

    /// <summary>Resolves the application gain that must be applied before output starts.</summary>
    internal static float GetEffectiveDeviceVolumeForInitialization(
        float deviceVolume,
        bool isMuted) =>
        isMuted ? 0f : deviceVolume;

    /// <summary>
    /// Reads the stream published by a callback-driven session and clamps native short reads
    /// or failures to zero. A missing session or published handle produces silence.
    /// </summary>
    internal static int ReadPublishedCallbackOutput(
        BassAudioSession session,
        IntPtr buffer,
        int length,
        Func<int, IntPtr, int, int> readData)
    {
        ArgumentNullException.ThrowIfNull(readData);
        int callbackHandle = session?.CallbackOutputHandle ?? 0;
        if (callbackHandle == 0)
        {
            return 0;
        }

        return System.Math.Max(0, readData(callbackHandle, buffer, length));
    }

    private static int ReadCallbackData(int handle, IntPtr buffer, int length) =>
        Bass.BASS_ChannelGetData(handle, buffer, length);

    private static int ReadCallbackOutput(IntPtr buffer, int length)
    {
        if (!Ribbit.Media.Audio.BassNet.TryEnterAudioCallbackOperation(
            out BassAudioOperationLease operation))
        {
            return 0;
        }

        using (operation)
        {
            return ReadPublishedCallbackOutput(
                SessionLifecycle.CurrentSessionForAdmittedOperation,
                buffer,
                length,
                ReadCallbackData);
        }
    }

    private static void TryApplyEffectiveDeviceVolumeToActiveSession()
    {
        if (!Ribbit.Media.Audio.BassNet.TryEnterAudioOperation(
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
                GetEffectiveDeviceVolumeForInitialization(_deviceVolume, _isDeviceMuted));
        }
    }

    /// <summary>
    /// Applies WASAPI shared application volume through the mixer boundary and reports a
    /// non-fatal native failure through the supplied logging action.
    /// </summary>
    internal static void ApplyWasapiSharedDeviceVolume(
        BassWasapiNegotiator negotiator,
        BassAudioSession session,
        float volume,
        Action<string> logWarning)
    {
        ArgumentNullException.ThrowIfNull(negotiator);
        ArgumentNullException.ThrowIfNull(logWarning);
        BASSError error = BASSError.BASS_ERROR_INIT;
        string failedStage = "WASAPI shared mixer volume";
        if (session == null
            || !negotiator.TrySetSharedMixerGain(
                session,
                volume,
                out error,
                out failedStage))
        {
            logWarning(
                failedStage + " for WASAPI shared mixer volume failed: "
                + error);
        }
    }

    private static void SetDeviceMasterVolume(float vol)
    {
        if (!IsInitialized)
        {
            return;
        }
        switch (DriverType)
        {
            case DeviceDriver.NULL_DEVICE:
                if (!Bass.BASS_FXSetParameters(volumeEffect, new BASS_BFX_VOLUME(vol)))
                {
                    BASSError bASSError5 = Bass.BASS_ErrorGetCode();
                    NLogWrapper.TraceLogger?.Warn("BASS_FXSetParameters failed: " + bASSError5);
                }
                break;
            case DeviceDriver.DIRECT_SOUND:
                {
                    BassAudioSession directSoundSession = SessionLifecycle.CurrentSessionForAdmittedOperation;
                    if (!DirectSoundNegotiator.TrySetMixerGain(
                            directSoundSession,
                            vol,
                            out BASSError directSoundError))
                    {
                        NLogWrapper.TraceLogger?.Warn("BASS_FXSetParameters failed: " + directSoundError);
                    }
                    break;
                }
            case DeviceDriver.WASAPI_EXCLUSIVE:
                if (!Bass.BASS_FXSetParameters(volumeEffect, new BASS_BFX_VOLUME(vol)))
                {
                    BASSError bASSError2 = Bass.BASS_ErrorGetCode();
                    NLogWrapper.TraceLogger?.Warn("BASS_FXSetParameters failed: " + bASSError2);
                }
                break;
            case DeviceDriver.WASAPI_SHARED:
                BassAudioSession session = SessionLifecycle.CurrentSessionForAdmittedOperation;
                ApplyWasapiSharedDeviceVolume(
                    WasapiNegotiator,
                    session,
                    vol,
                    TryLogAudioSessionWarning);
                break;
            case DeviceDriver.ASIO:
                {
                    for (int i = 0; i < 2; i++)
                    {
                        if (!BassAsio.BASS_ASIO_ChannelSetVolume(input: false, i, vol))
                        {
                            BASSError bASSError = BassAsio.BASS_ASIO_ErrorGetCode();
                            NLogWrapper.TraceLogger?.Warn("BASS_ASIO_ChannelSetVolume failed: " + bASSError);
                        }
                    }
                    break;
                }
            case DeviceDriver.INVALID:
                break;
        }
    }

    public BassAudioPlayer(string fileName, bool onMemory = true)
    {
        using BassAudioOperationLease operation =
            Ribbit.Media.Audio.BassNet.EnterAudioOperation();
        if (!IsInitialized)
        {
            throw new InvalidOperationException("BassAudioPlayer is not initialized.");
        }
        if (fileName == null)
        {
            throw new ArgumentNullException("fileName");
        }
        if (!LongPathFileSystem.FileExists(fileName))
        {
            throw new FileNotFoundException(fileName);
        }
        FileName = fileName;
        fileNameHash = xxHash32.CalculateHash(fileName.ToUpperInvariant());
        lock (Locks.GetLockObject(fileNameHash))
        {
            if (OnMemoryFileCache.ContainsKey(fileNameHash))
            {
                lock (StaticLockObject)
                {
                    CachedData cachedData = OnMemoryFileCache[fileNameHash];
                    _sampleBuffer = cachedData.Data;
                    cachedData.RefCount++;
                }
            }
            else if (onMemory)
            {
                string text = Path.GetExtension(fileName).ToLowerInvariant();
                if (text == ".ogg")
                {
                    try
                    {
                        using FileStream fileStream = LongPathFileSystem.OpenRead(fileName);
                        _sampleBuffer = DecodeOggToWave(fileStream);
                    }
                    catch (Exception ex)
                    {
                        NLogWrapper.GetLogger()?.Warn("Ogg Decode failed: " + ex);
                        _sampleBuffer = null;
                        goto end_IL_0060;
                    }
                }
                else
                {
                    _sampleBuffer = LongPathFileSystem.ReadAllBytes(fileName);
                }
                lock (StaticLockObject)
                {
                    OnMemoryFileCache[fileNameHash] = new CachedData(_sampleBuffer);
                }
            }
        end_IL_0060:;
        }
        if (_sampleBuffer != null)
        {
            fileProc = new BASS_FILEPROCS(FileProcClose, FileProcLength, FileProcRead, fileProcSeek);
            _handle = Bass.BASS_StreamCreateFileUser(BASSStreamSystem.STREAMFILE_NOBUFFER, BASSFlag.BASS_SAMPLE_FLOAT | BASSFlag.BASS_STREAM_PRESCAN | BASSFlag.BASS_STREAM_DECODE, fileProc, IntPtr.Zero);
            if (_handle == 0)
            {
                BASSError bASSError = Bass.BASS_ErrorGetCode();
                throw new Exception("BASS_StreamCreateFileUser failed: " + fileName + " " + bASSError);
            }
        }
        else
        {
            _handle = Bass.BASS_StreamCreateFile(LongPathFileSystem.ToExtendedPath(fileName), 0L, 0L, BASSFlag.BASS_SAMPLE_FLOAT | BASSFlag.BASS_STREAM_PRESCAN | BASSFlag.BASS_STREAM_DECODE);
            if (_handle == 0)
            {
                BASSError bASSError2 = Bass.BASS_ErrorGetCode();
                throw new Exception("BASS_StreamCreateFile failed: " + fileName + " " + bASSError2);
            }
        }
        using (SessionLifecycle.Enter())
        {
            owningSession = SessionLifecycle.CurrentSession;
            if (owningSession?.State != BassAudioSessionState.Active)
            {
                throw new InvalidOperationException(
                    "The BASS source stream was created without an active audio session owner.");
            }
            owningSession.TrackPlayerStream(_handle, this, ConfirmNativeStreamReleased);
        }
        long pos = Bass.BASS_ChannelGetLength(_handle);
        double value = Bass.BASS_ChannelBytes2Seconds(_handle, pos);
        Duration = TimeSpan.FromSeconds(value);
        Volume = DefaultVolume;
        Bass.BASS_ChannelSetSync(_handle, BASSSync.BASS_SYNC_END | BASSSync.BASS_SYNC_MIXTIME, 0L, EndProc, IntPtr.Zero);
        playState = PlayState.Stopped;
        lock (StaticLockObject)
        {
            InstanceLocks[_handle] = new object();
        }
    }

    internal static byte[] DecodeOggToWave(Stream source)
    {
        using var stream = new VorbisReader(source, closeOnDispose: false);
        stream.ClipSamples = true;
        // Discover every logical stream before allocating the output buffer.  The old
        // decoder used ov_pcm_total(-1), so a concatenated OGG must not be truncated
        // to its first link or silently cached as a successful partial decode.
        while (stream.FindNextStream())
        {
        }

        if (stream.Streams.Count == 0)
        {
            throw new InvalidDataException("Decoded OGG stream did not contain a logical stream.");
        }

        var firstStream = stream.Streams[0];
        int sampleRate = firstStream.SampleRate;
        int channelCount = firstStream.Channels;
        if (sampleRate <= 0 || channelCount <= 0)
        {
            throw new InvalidDataException("Decoded OGG stream has invalid audio format metadata.");
        }

        long totalSampleCountLong = 0;
        for (int streamIndex = 0; streamIndex < stream.Streams.Count; streamIndex++)
        {
            var logicalStream = stream.Streams[streamIndex];
            if (logicalStream.SampleRate != sampleRate || logicalStream.Channels != channelCount)
            {
                throw new InvalidDataException("Concatenated OGG streams must use one audio format.");
            }

            long totalFrames = logicalStream.TotalSamples;
            if (totalFrames < 0)
            {
                throw new InvalidDataException("Decoded OGG stream does not have a supported sample count.");
            }

            totalSampleCountLong = checked(totalSampleCountLong + checked(totalFrames * channelCount));
        }

        if (totalSampleCountLong > int.MaxValue)
        {
            throw new InvalidDataException("Decoded OGG stream does not have a supported sample count.");
        }

        int totalSampleCount = (int)totalSampleCountLong;
        long pcmByteCount = checked(totalSampleCountLong * sizeof(short));
        if (pcmByteCount > int.MaxValue - 44)
        {
            throw new InvalidDataException("Decoded OGG stream is too large for an in-memory WAV buffer.");
        }

        using (new MemoryFailPoint(1 + (int)pcmByteCount / 1024 / 1024))
        {
        }

        byte[] sampleBufferBytes = new byte[(int)pcmByteCount + 44];
        int chunkSampleCount = 4096 - (4096 % channelCount);
        if (chunkSampleCount < channelCount)
        {
            chunkSampleCount = channelCount;
        }

        float[] sampleBuffer = new float[System.Math.Min(chunkSampleCount, totalSampleCount)];
        int samplesWritten = 0;
        for (int streamIndex = 0; streamIndex < stream.Streams.Count; streamIndex++)
        {
            var logicalStream = stream.Streams[streamIndex];
            stream.SwitchStreams(streamIndex);
            int logicalSampleCount = checked((int)checked(logicalStream.TotalSamples * channelCount));
            int logicalSamplesWritten = 0;
            while (logicalSamplesWritten < logicalSampleCount)
            {
                int samplesRead = stream.ReadSamples(
                    sampleBuffer,
                    0,
                    System.Math.Min(sampleBuffer.Length, logicalSampleCount - logicalSamplesWritten));
                if (samplesRead <= 0)
                {
                    throw new EndOfStreamException("Decoded OGG stream ended before its declared sample count.");
                }

                for (int i = 0; i < samplesRead; i++)
                {
                    // The legacy decoder emitted signed 16-bit PCM using 32768 scaling and symmetric rounding.
                    int pcmSample = (int)System.Math.Round(sampleBuffer[i] * 32768.0, MidpointRounding.AwayFromZero);
                    pcmSample = System.Math.Max(short.MinValue, System.Math.Min(short.MaxValue, pcmSample));
                    int byteOffset = 44 + (samplesWritten + i) * sizeof(short);
                    sampleBufferBytes[byteOffset] = (byte)(pcmSample & 0xff);
                    sampleBufferBytes[byteOffset + 1] = (byte)((pcmSample >> 8) & 0xff);
                }

                samplesWritten += samplesRead;
                logicalSamplesWritten += samplesRead;
            }

            if (logicalSamplesWritten != logicalSampleCount)
            {
                throw new EndOfStreamException("Decoded OGG stream ended before its declared sample count.");
            }
        }

        using var targetStream = new MemoryStream(sampleBufferBytes, 0, 44);
        WavFile.WriteHeader(targetStream, sampleBufferBytes.Length - 44, channelCount, sampleRate);
        return sampleBufferBytes;
    }

    private void FileProcClose(IntPtr user)
    {
    }

    private long FileProcLength(IntPtr user)
    {
        return (_sampleBuffer != null) ? _sampleBuffer.Length : 0;
    }

    private int FileProcRead(IntPtr buffer, int length, IntPtr user)
    {
        if (_sampleBuffer == null)
        {
            return -1;
        }
        if (_sampleBufferPos >= _sampleBuffer.Length)
        {
            return -1;
        }
        try
        {
            int num = System.Math.Min(length, _sampleBuffer.Length - _sampleBufferPos);
            Marshal.Copy(_sampleBuffer, _sampleBufferPos, buffer, num);
            _sampleBufferPos += num;
            return num;
        }
        catch
        {
            return -1;
        }
    }

    private bool fileProcSeek(long offset, IntPtr user)
    {
        if (_sampleBuffer == null)
        {
            return false;
        }
        if (offset >= _sampleBuffer.Length)
        {
            return false;
        }
        _sampleBufferPos = (int)offset;
        return true;
    }

    public void Pause()
    {
        using BassAudioOperationLease operation =
            Ribbit.Media.Audio.BassNet.EnterAudioOperation();
        if (playState != PlayState.Stopped)
        {
            if (playState == PlayState.Playing)
            {
                playState = PlayState.Paused;
                BassMix.BASS_Mixer_ChannelPause(_handle);
            }
            else if (playState == PlayState.Paused)
            {
                playState = PlayState.Playing;
                BassMix.BASS_Mixer_ChannelPlay(_handle);
            }
        }
    }

    /// <summary>
    /// Starts or pauses this stream and fails after a bounded retry when the mixer rejects it.
    /// </summary>
    public void Play(PlayWith flagPlayWith = PlayWith.RESTART)
    {
        using BassAudioOperationLease operation =
            Ribbit.Media.Audio.BassNet.EnterAudioOperation();
        IsMuted = flagPlayWith.HasFlag(PlayWith.MUTE);
        bool flag = false;
        lock (InstanceLocks[_handle])
        {
            for (int attempt = 0; attempt < 2; attempt++)
            {
                BASSActive bASSActive = BassMix.BASS_Mixer_ChannelIsActive(_handle);
                if (bASSActive == BASSActive.BASS_ACTIVE_STOPPED)
                {
                    if (BassMix.BASS_Mixer_StreamAddChannel(inputMixer, _handle, BASSFlag.BASS_STREAM_PRESCAN))
                    {
                        lock (StaticLockObject)
                        {
                            CurrentVoices++;
                            if (MaxVoices < CurrentVoices)
                            {
                                MaxVoices = CurrentVoices;
                            }
                        }
                        flag = true;
                    }
                    else
                    {
                        BASSError bASSError = Bass.BASS_ErrorGetCode();
                        NLogWrapper.TraceLogger?.Warn(string.Concat("BASS_Mixer_StreamAddChannel failed: ", bASSError, " :", FileName));
                        throw new InvalidOperationException(
                            "BASS_Mixer_StreamAddChannel failed: " + bASSError + " :" + FileName);
                    }
                    bASSActive = BASSActive.BASS_ACTIVE_PAUSED;
                }
                if (playState == PlayState.Playing && flagPlayWith.HasFlag(PlayWith.RESTART))
                {
                    CurrentTime = TimeSpan.Zero;
                }
                if (flagPlayWith.HasFlag(PlayWith.PAUSE))
                {
                    if (bASSActive == BASSActive.BASS_ACTIVE_PLAYING)
                    {
                        BassMix.BASS_Mixer_ChannelPause(_handle);
                    }
                    playState = PlayState.Paused;
                    return;
                }
                if (!BassMix.BASS_Mixer_ChannelPlay(_handle))
                {
                    BASSError bASSError2 = Bass.BASS_ErrorGetCode();
                    NLogWrapper.TraceLogger?.Warn(string.Concat("BASS_Mixer_ChannelPlay failed: ", bASSError2, " :", FileName, flag ? " add channel failed?" : " channel is removed?"));
                    if (flag)
                    {
                        lock (StaticLockObject)
                        {
                            CurrentVoices--;
                        }
                        throw new InvalidOperationException(
                            "BASS_Mixer_ChannelPlay failed after adding the channel: "
                            + bASSError2 + " :" + FileName);
                    }
                    continue;
                }
                playState = PlayState.Playing;
                return;
            }

            BASSError error = Bass.BASS_ErrorGetCode();
            throw new InvalidOperationException(
                "BASS_Mixer_ChannelPlay failed after a bounded retry: " + error + " :" + FileName);
        }
    }

    public void Stop()
    {
        using BassAudioOperationLease operation =
            Ribbit.Media.Audio.BassNet.EnterAudioOperation();
        if (playState == PlayState.Stopped)
        {
            return;
        }
        playState = PlayState.Stopped;
        CurrentTime = TimeSpan.Zero;
        lock (InstanceLocks[_handle])
        {
            if (BassMix.BASS_Mixer_ChannelIsActive(_handle) == BASSActive.BASS_ACTIVE_STOPPED)
            {
                return;
            }
            if (BassMix.BASS_Mixer_ChannelRemove(_handle))
            {
                lock (StaticLockObject)
                {
                    CurrentVoices--;
                    return;
                }
            }
            BASSError bASSError = Bass.BASS_ErrorGetCode();
            NLogWrapper.TraceLogger?.Warn("BASS_Mixer_ChannelRemove failed: " + bASSError);
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
            ? Ribbit.Media.Audio.BassNet.TryEnterAudioOperation(out operation)
            : Ribbit.Media.Audio.BassNet.TryEnterAudioCallbackOperation(out operation);
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

                    bool released = Bass.BASS_StreamFree(ownedHandle);
                    if (!released)
                    {
                        BASSError error = Bass.BASS_ErrorGetCode();
                        released = error == BASSError.BASS_ERROR_INIT;
                        if (!released)
                        {
                            TryLogPlayerCleanupFailure(
                                "BASS_StreamFree(" + ownedHandle + ") failed: " + error,
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
                    else
                    {
                        ConfirmNativeStreamReleased(ownedHandle);
                    }
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
            disposedValue = true;
        }

        if (_sampleBuffer != null)
        {
            _sampleBuffer = null;
            lock (StaticLockObject)
            {
                if (OnMemoryFileCache.ContainsKey(fileNameHash))
                {
                    CachedData cachedData = OnMemoryFileCache[fileNameHash];
                    cachedData.RefCount--;
                    if (cachedData.RefCount == 0)
                    {
                        OnMemoryFileCache.Remove(fileNameHash);
                    }
                }
            }
        }
        lock (StaticLockObject)
        {
            InstanceLocks.Remove(releasedHandle);
        }
        GC.SuppressFinalize(this);
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
