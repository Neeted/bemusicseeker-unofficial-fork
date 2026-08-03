using System;
using System.Diagnostics;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using BeMusicSeeker.Models;
using BeMusicSeeker.Models.Utils;
using Ribbit.Media;
using Ribbit.Media.Audio;
using Ribbit.Logging;

namespace BeMusicSeeker.ViewModels;

internal sealed class AudioDeviceTestRequest
{
    internal AudioDeviceTestRequest(
        AudioDriver playerDriver,
        string playerDevice,
        string playerDeviceName,
        SampleRate playerSampleRate,
        SampleFormat playerFormat,
        float playerBufferSize,
        bool playerWASAPIParam,
        int playerVolume,
        bool playSound)
    {
        PlayerDriver = playerDriver;
        PlayerDevice = playerDevice;
        PlayerDeviceName = playerDeviceName;
        PlayerSampleRate = playerSampleRate;
        PlayerFormat = playerFormat;
        PlayerBufferSize = playerBufferSize;
        PlayerWASAPIParam = playerWASAPIParam;
        PlayerVolume = playerVolume;
        PlaySound = playSound;
    }

    internal AudioDriver PlayerDriver { get; }

    internal string PlayerDevice { get; }

    internal string PlayerDeviceName { get; }

    internal SampleRate PlayerSampleRate { get; }

    internal SampleFormat PlayerFormat { get; }

    internal float PlayerBufferSize { get; }

    internal bool PlayerWASAPIParam { get; }

    internal int PlayerVolume { get; }

    internal bool PlaySound { get; }
}

/// <summary>
/// Reports native initialization and observed stream movement as separate outcomes.
/// </summary>
internal sealed class AudioDeviceTestResult
{
    /// <summary>Creates a device-test result from one successful native initialization.</summary>
    internal AudioDeviceTestResult(
        AudioPlaybackInitializationResult initialization,
        bool streamProgressRequired,
        bool streamProgressSucceeded,
        TimeSpan wallClockDuration,
        TimeSpan playbackPositionDuration,
        double? progressRatio,
        string failureReason)
    {
        Initialization = initialization ?? throw new ArgumentNullException(nameof(initialization));
        StreamProgressRequired = streamProgressRequired;
        StreamProgressSucceeded = streamProgressSucceeded;
        WallClockDuration = wallClockDuration;
        PlaybackPositionDuration = playbackPositionDuration;
        ProgressRatio = progressRatio;
        FailureReason = failureReason;
    }

    /// <summary>Gets the requested and negotiated native initialization values.</summary>
    internal AudioPlaybackInitializationResult Initialization { get; }

    /// <summary>Gets whether native device initialization completed.</summary>
    internal bool DeviceInitializationSucceeded => Initialization != null;

    /// <summary>Gets whether this request required observed test-sound progress.</summary>
    internal bool StreamProgressRequired { get; }

    /// <summary>Gets whether monotonic, real-time stream progress was observed.</summary>
    internal bool StreamProgressSucceeded { get; }

    /// <summary>Gets whether every result required by the request succeeded.</summary>
    internal bool Succeeded => DeviceInitializationSucceeded
        && (!StreamProgressRequired || StreamProgressSucceeded);

    /// <summary>Gets the measured wall-clock interval after startup pre-roll.</summary>
    internal TimeSpan WallClockDuration { get; }

    /// <summary>Gets the measured playback-position interval after startup pre-roll.</summary>
    internal TimeSpan PlaybackPositionDuration { get; }

    /// <summary>Gets playback-position duration divided by wall-clock duration.</summary>
    internal double? ProgressRatio { get; }

    /// <summary>Gets why stream observation failed, if it failed.</summary>
    internal string FailureReason { get; }

    /// <summary>Gets the backend selected by the caller.</summary>
    internal AudioDriver RequestedBackend => Initialization.RequestedBackend;

    /// <summary>Gets the backend that owns the initialized native session.</summary>
    internal AudioDriver ActualBackend => Initialization.ActualBackend;

    /// <summary>Gets whether native negotiation used a fallback.</summary>
    internal bool FallbackOccurred => Initialization.FallbackOccurred;

    /// <summary>Gets why native negotiation used a fallback.</summary>
    internal string FallbackReason => Initialization.FallbackReason;

    /// <summary>Gets the requested endpoint identity.</summary>
    internal string RequestedDevice => Initialization.RequestedDevice;

    /// <summary>Gets the requested endpoint display name.</summary>
    internal string RequestedDeviceName => Initialization.RequestedDeviceName;

    /// <summary>Gets the negotiated endpoint identity.</summary>
    internal string ActualDevice => Initialization.ActualDevice;

    /// <summary>Gets the negotiated endpoint display name.</summary>
    internal string ActualDeviceName => Initialization.ActualDeviceName;

    /// <summary>Gets the requested sample rate.</summary>
    internal SampleRate RequestedRate => Initialization.RequestedRate;

    /// <summary>Gets the requested sample format.</summary>
    internal SampleFormat RequestedFormat => Initialization.RequestedFormat;

    /// <summary>Gets the requested buffer size in milliseconds.</summary>
    internal float RequestedBufferSize => Initialization.RequestedBufferSize;

    /// <summary>Gets whether event-driven WASAPI was requested.</summary>
    internal bool RequestedEventMode => Initialization.RequestedEventMode;

    /// <summary>Gets the requested output volume.</summary>
    internal int RequestedVolume => Initialization.RequestedVolume;

    /// <summary>Gets the negotiated sample rate.</summary>
    internal SampleRate ActualRate => Initialization.ActualRate;

    /// <summary>Gets the internal mixer format.</summary>
    internal SampleFormat EngineFormat => Initialization.EngineFormat;

    /// <summary>Gets the native endpoint or callback format.</summary>
    internal SampleFormat EndpointFormat => Initialization.EndpointFormat;

    /// <summary>Gets the negotiated latency in milliseconds.</summary>
    internal double Latency => Initialization.Latency;

    /// <summary>Gets whether a non-audible fallback was substituted.</summary>
    internal bool IsSilentFallback => Initialization.IsSilentFallback;
}

/// <summary>Abstracts file, player, monotonic-clock, and wait operations used by the test sound.</summary>
internal interface IAudioDeviceTestSoundBoundary
{
    /// <summary>Checks whether the configured test sound exists.</summary>
    bool FileExists(string path);

    /// <summary>Creates a player for the configured test sound.</summary>
    IAudioPlayer CreatePlayer(string path);

    /// <summary>Gets a monotonic timestamp.</summary>
    long GetTimestamp();

    /// <summary>Gets elapsed monotonic time between two timestamps.</summary>
    TimeSpan GetElapsedTime(long startTimestamp, long endTimestamp);

    /// <summary>Waits before polling the stream again.</summary>
    void Wait(TimeSpan interval);
}

/// <summary>Binds test-sound observation to the system clock and BASS player.</summary>
internal sealed class SystemAudioDeviceTestSoundBoundary : IAudioDeviceTestSoundBoundary
{
    /// <inheritdoc />
    public bool FileExists(string path) => File.Exists(path);

    /// <inheritdoc />
    public IAudioPlayer CreatePlayer(string path) => new BassAudioPlayer(path);

    /// <inheritdoc />
    public long GetTimestamp() => Stopwatch.GetTimestamp();

    /// <inheritdoc />
    public TimeSpan GetElapsedTime(long startTimestamp, long endTimestamp)
        => Stopwatch.GetElapsedTime(startTimestamp, endTimestamp);

    /// <inheritdoc />
    public void Wait(TimeSpan interval) => Thread.Sleep(interval);
}

/// <summary>Contains one bounded observation of playback-position progress.</summary>
internal readonly struct AudioDeviceTestStreamObservation
{
    /// <summary>Creates an immutable stream observation.</summary>
    internal AudioDeviceTestStreamObservation(
        bool succeeded,
        TimeSpan wallClockDuration,
        TimeSpan playbackPositionDuration,
        double? progressRatio,
        string failureReason)
    {
        Succeeded = succeeded;
        WallClockDuration = wallClockDuration;
        PlaybackPositionDuration = playbackPositionDuration;
        ProgressRatio = progressRatio;
        FailureReason = failureReason;
    }

    /// <summary>Gets whether stream movement met every progress criterion.</summary>
    internal bool Succeeded { get; }

    /// <summary>Gets the measured wall-clock interval.</summary>
    internal TimeSpan WallClockDuration { get; }

    /// <summary>Gets the measured playback-position interval.</summary>
    internal TimeSpan PlaybackPositionDuration { get; }

    /// <summary>Gets playback-position duration divided by wall-clock duration.</summary>
    internal double? ProgressRatio { get; }

    /// <summary>Gets the observation failure reason.</summary>
    internal string FailureReason { get; }
}

/// <summary>Evaluates bounded playback movement independently from native device initialization.</summary>
internal static class AudioDeviceTestStreamObserver
{
    private static readonly TimeSpan PollInterval = TimeSpan.FromMilliseconds(100);

    private static readonly TimeSpan RequiredProgressInterval = TimeSpan.FromSeconds(1);

    private static readonly TimeSpan ObservationTimeout = TimeSpan.FromSeconds(10);

    /// <summary>Observes a test sound and reports monotonic real-time playback progress.</summary>
    internal static AudioDeviceTestStreamObservation Observe(
        string testSoundPath,
        IAudioDeviceTestSoundBoundary soundBoundary)
    {
        ArgumentNullException.ThrowIfNull(soundBoundary);
        if (!soundBoundary.FileExists(testSoundPath))
        {
            return Failure("The configured test sound file is unavailable.");
        }

        using IAudioPlayer player = soundBoundary.CreatePlayer(testSoundPath);
        TimeSpan initialPosition = player.CurrentTime;
        TimeSpan previousPosition = initialPosition;
        long overallStart = soundBoundary.GetTimestamp();
        long progressStart = 0;
        TimeSpan progressStartPosition = TimeSpan.Zero;
        player.Play();
        while (true)
        {
            soundBoundary.Wait(PollInterval);
            long now = soundBoundary.GetTimestamp();
            TimeSpan currentPosition = player.CurrentTime;
            PlayState playState = player.PlayState;
            if (currentPosition < previousPosition)
            {
                TimeSpan wallClockDuration = progressStart == 0
                    ? soundBoundary.GetElapsedTime(overallStart, now)
                    : soundBoundary.GetElapsedTime(progressStart, now);
                TimeSpan playbackDuration = progressStart == 0
                    ? currentPosition - initialPosition
                    : currentPosition - progressStartPosition;
                return new AudioDeviceTestStreamObservation(
                    false,
                    wallClockDuration,
                    playbackDuration,
                    CalculateRatio(playbackDuration, wallClockDuration),
                    "The test-sound playback position moved backwards.");
            }

            if (progressStart == 0)
            {
                if (currentPosition > previousPosition)
                {
                    progressStart = now;
                    progressStartPosition = currentPosition;
                }
                else if (playState == PlayState.Stopped)
                {
                    TimeSpan wallClockDuration = soundBoundary.GetElapsedTime(overallStart, now);
                    TimeSpan playbackDuration = currentPosition - initialPosition;
                    return new AudioDeviceTestStreamObservation(
                        false,
                        wallClockDuration,
                        playbackDuration,
                        CalculateRatio(playbackDuration, wallClockDuration),
                        "The test sound stopped before playback progress was observed.");
                }
            }
            else
            {
                TimeSpan wallClockDuration = soundBoundary.GetElapsedTime(progressStart, now);
                TimeSpan playbackDuration = currentPosition - progressStartPosition;
                if (currentPosition <= previousPosition)
                {
                    return new AudioDeviceTestStreamObservation(
                        false,
                        wallClockDuration,
                        playbackDuration,
                        CalculateRatio(playbackDuration, wallClockDuration),
                        "The test-sound playback position stopped advancing.");
                }
                if (playState == PlayState.Stopped && wallClockDuration < RequiredProgressInterval)
                {
                    return new AudioDeviceTestStreamObservation(
                        false,
                        wallClockDuration,
                        playbackDuration,
                        CalculateRatio(playbackDuration, wallClockDuration),
                        "The test sound ended before a complete progress interval was observed.");
                }
                if (wallClockDuration >= RequiredProgressInterval)
                {
                    double ratio = CalculateRatio(playbackDuration, wallClockDuration) ?? 0;
                    bool succeeded = ratio >= 0.75 && ratio <= 1.25;
                    return new AudioDeviceTestStreamObservation(
                        succeeded,
                        wallClockDuration,
                        playbackDuration,
                        ratio,
                        succeeded
                            ? null
                            : "The test-sound progress ratio was outside the accepted range.");
                }
            }

            previousPosition = currentPosition;
            if (soundBoundary.GetElapsedTime(overallStart, now) >= ObservationTimeout)
            {
                TimeSpan wallClockDuration = soundBoundary.GetElapsedTime(overallStart, now);
                TimeSpan playbackDuration = currentPosition - initialPosition;
                return new AudioDeviceTestStreamObservation(
                    false,
                    wallClockDuration,
                    playbackDuration,
                    CalculateRatio(playbackDuration, wallClockDuration),
                    "The test sound did not produce a complete progress observation before timeout.");
            }
        }
    }

    private static double? CalculateRatio(TimeSpan playbackDuration, TimeSpan wallClockDuration)
    {
        return wallClockDuration > TimeSpan.Zero
            ? playbackDuration.TotalSeconds / wallClockDuration.TotalSeconds
            : null;
    }

    private static AudioDeviceTestStreamObservation Failure(string reason)
        => new(false, TimeSpan.Zero, TimeSpan.Zero, null, reason);
}

internal interface IAudioDeviceTestRuntime
{
    AudioDeviceTestResult Run(AudioDeviceTestRequest request);
}

internal interface IAudioDeviceTestPlaybackPort
{
    void StopPlayback();
}

internal sealed class AudioDeviceTestWorkflowOwner
{
    private readonly IAudioDeviceTestPlaybackPort playbackPort;

    private readonly IAudioDeviceTestRuntime runtime;

    private int isRunning;

    internal AudioDeviceTestWorkflowOwner(
        IAudioDeviceTestPlaybackPort playbackPort,
        IAudioDeviceTestRuntime runtime)
    {
        this.playbackPort = playbackPort ?? throw new ArgumentNullException(nameof(playbackPort));
        this.runtime = runtime ?? throw new ArgumentNullException(nameof(runtime));
    }

    internal bool IsRunning => Volatile.Read(ref isRunning) != 0;

    internal async Task<AudioDeviceTestResult> TryRunAsync(AudioDeviceTestRequest request)
    {
        if (request == null)
        {
            throw new ArgumentNullException(nameof(request));
        }

        if (Interlocked.Exchange(ref isRunning, 1) != 0)
        {
            return null;
        }

        try
        {
            playbackPort.StopPlayback();
            return await Task.Run(() => runtime.Run(request));
        }
        finally
        {
            Volatile.Write(ref isRunning, 0);
        }
    }
}

internal sealed class BassAudioDeviceTestRuntime : IAudioDeviceTestRuntime
{
    private readonly ApplicationPathSnapshot applicationPathSnapshot;

    private readonly BassAudioSessionLease sessionLease = new();

    private readonly IAudioDeviceTestSoundBoundary soundBoundary;

    internal BassAudioDeviceTestRuntime(ApplicationPathSnapshot applicationPathSnapshot)
        : this(applicationPathSnapshot, new SystemAudioDeviceTestSoundBoundary())
    {
    }

    /// <summary>Creates a runtime with a replaceable test-sound observation boundary.</summary>
    internal BassAudioDeviceTestRuntime(
        ApplicationPathSnapshot applicationPathSnapshot,
        IAudioDeviceTestSoundBoundary soundBoundary)
    {
        this.applicationPathSnapshot = applicationPathSnapshot ?? throw new ArgumentNullException(nameof(applicationPathSnapshot));
        this.soundBoundary = soundBoundary ?? throw new ArgumentNullException(nameof(soundBoundary));
    }

    public AudioDeviceTestResult Run(AudioDeviceTestRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (!sessionLease.TryRelease(BassAudioPlayer.Free))
        {
            throw new InvalidOperationException(
                "A previous audio device test still owns native resources after cleanup failed.");
        }
        BassAudioPlaybackRuntime.ThrowIfAudiblePlaybackUsesNullDevice(
            request.PlayerDriver,
            request.PlayerDevice,
            request.PlayerDeviceName);

        BassAudioPlayer.DeviceDescriptor descriptor = string.IsNullOrWhiteSpace(request.PlayerDevice)
            ? default
            : new BassAudioPlayer.DeviceDescriptor(request.PlayerDeviceName, request.PlayerDevice);
        BassAudioSession ownedSession = null;
        Exception primaryException = null;
        try
        {
            BassAudioPlayer.Frequency = request.PlayerSampleRate;
            BassAudioPlayer.Format = request.PlayerFormat;
            BassAudioPlayer.DeviceVolume = Math.Min(100, Math.Max(0, request.PlayerVolume)) / 100f;
            descriptor = BassAudioPlayer.InitializeOwned(
                BassAudioMapping.ToBassDriver(request.PlayerDriver),
                descriptor,
                request.PlayerBufferSize,
                out ownedSession,
                request.PlayerWASAPIParam);
            sessionLease.Attach(ownedSession);
            using BassAudioOperationLease operation = BassNet.EnterAudioOperation();
            BassAudioBackendResult negotiated = ownedSession.NegotiationResult
                ?? throw new InvalidOperationException(
                    "An audible BASS device test completed without a negotiated backend result.");
            var initialization = new AudioPlaybackInitializationResult(
                request.PlayerDriver,
                request.PlayerDevice,
                request.PlayerDeviceName,
                request.PlayerSampleRate,
                request.PlayerFormat,
                request.PlayerBufferSize,
                request.PlayerWASAPIParam,
                request.PlayerVolume,
                BassAudioMapping.FromBassDriver(ownedSession.ActualBackend),
                ownedSession.ActualDevice.Driver,
                ownedSession.ActualDevice.Name,
                negotiated.ActualRate,
                negotiated.EngineFormat,
                negotiated.EndpointFormat,
                negotiated.LatencyMilliseconds,
                negotiated.FallbackReason,
                ownedSession.ActualBackend == BassAudioPlayer.DeviceDriver.NULL_DEVICE);

            AudioDeviceTestStreamObservation observation = request.PlaySound
                ? AudioDeviceTestStreamObserver.Observe(applicationPathSnapshot.TestSoundPath, soundBoundary)
                : new AudioDeviceTestStreamObservation(true, TimeSpan.Zero, TimeSpan.Zero, null, null);
            var result = new AudioDeviceTestResult(
                initialization,
                request.PlaySound,
                observation.Succeeded,
                observation.WallClockDuration,
                observation.PlaybackPositionDuration,
                observation.ProgressRatio,
                observation.FailureReason);
            TryLogTestResult(result);
            return result;
        }
        catch (Exception exception)
        {
            primaryException = exception;
            throw;
        }
        finally
        {
            ReleaseTestSession(ownedSession, primaryException);
        }
    }

    private void ReleaseTestSession(BassAudioSession ownedSession, Exception primaryException)
    {
        try
        {
            if (ownedSession != null && sessionLease.Session == null)
            {
                sessionLease.Attach(ownedSession);
            }
            if (!sessionLease.TryRelease(BassAudioPlayer.Free) && primaryException == null)
            {
                throw new InvalidOperationException(
                    "The audio device test completed without confirming native cleanup.");
            }
        }
        catch (Exception cleanupException) when (primaryException != null)
        {
            try
            {
                NLogWrapper.GetLogger(nameof(BassAudioDeviceTestRuntime)).Warn(
                    "Audio device test cleanup failed while preserving the primary error: "
                    + cleanupException.Message);
            }
            catch
            {
                // Diagnostics must not replace the device-test exception.
            }
        }
    }

    private static void TryLogTestResult(AudioDeviceTestResult result)
    {
        try
        {
            NLogWrapper.GetLogger(nameof(BassAudioDeviceTestRuntime)).Debug(
                "Audio device test result. requestedBackend=" + result.RequestedBackend
                + " actualBackend=" + result.ActualBackend
                + " initializationSucceeded=" + result.DeviceInitializationSucceeded
                + " streamProgressRequired=" + result.StreamProgressRequired
                + " streamProgressSucceeded=" + result.StreamProgressSucceeded
                + " wallClockMs=" + result.WallClockDuration.TotalMilliseconds
                + " playbackPositionMs=" + result.PlaybackPositionDuration.TotalMilliseconds
                + " progressRatio=" + result.ProgressRatio
                + " fallbackOccurred=" + result.FallbackOccurred
                + " fallbackReason=" + result.FallbackReason
                + " failureReason=" + result.FailureReason);
        }
        catch
        {
            // Diagnostics must not change the device-test result.
        }
    }
}
