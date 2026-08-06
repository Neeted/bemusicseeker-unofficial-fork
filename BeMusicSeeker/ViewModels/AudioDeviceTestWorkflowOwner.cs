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
using Un4seen.Bass;

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
        AudioOutputSelection normalized = AudioDriverPolicy.NormalizePersistedSelection(
            new AudioOutputSelection(playerDriver, playerDevice, playerDeviceName));
        PlayerDriver = normalized.Backend;
        PlayerDevice = normalized.DeviceIdentity;
        PlayerDeviceName = normalized.DeviceName;
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

/// <summary>Identifies the feature boundary at which an audio-device test failed.</summary>
internal enum AudioDeviceTestFailureKind
{
    /// <summary>The test completed without a failure.</summary>
    None,

    /// <summary>The configured test-sound file was not available.</summary>
    TestSoundUnavailable,

    /// <summary>The test-sound player could not be created.</summary>
    PlayerCreationFailed,

    /// <summary>The created player reported an unusable duration.</summary>
    InvalidDuration,

    /// <summary>The test-sound player rejected its playback start operation.</summary>
    PlaybackStartFailed,

    /// <summary>The playback position moved backwards during observation.</summary>
    PlaybackPositionMovedBackwards,

    /// <summary>The test sound stopped before the expected progress or duration.</summary>
    PlaybackStoppedEarly,

    /// <summary>The playback position stopped advancing.</summary>
    PlaybackDidNotAdvance,

    /// <summary>The observed playback rate was outside the accepted range.</summary>
    PlaybackRateOutOfRange,

    /// <summary>The bounded observation did not complete in time.</summary>
    ObservationTimedOut,

    /// <summary>An unexpected test-sound operation failed.</summary>
    Unexpected
}

/// <summary>
/// Reports native initialization and observed stream movement as separate outcomes.
/// </summary>
internal sealed class AudioDeviceTestResult
{
    /// <summary>
    /// Creates a device-test result from native initialization and an explicitly classified
    /// stream observation.
    /// </summary>
    internal AudioDeviceTestResult(
        AudioPlaybackInitializationResult initialization,
        bool streamProgressRequired,
        bool streamProgressSucceeded,
        TimeSpan wallClockDuration,
        TimeSpan playbackPositionDuration,
        double? progressRatio,
        string failureReason,
        AudioDeviceTestFailureKind failureKind = AudioDeviceTestFailureKind.None,
        BassAudioPlaybackStage? playbackStage = null,
        string nativeErrorSource = null,
        BASSError? nativeErrorCode = null,
        string diagnosticReason = null,
        int? playbackSourceHandle = null,
        int? playbackExpectedMixerHandle = null,
        int? playbackActualMixerHandle = null,
        BassAudioPlayer.DeviceDriver? playbackBackend = null,
        BassAudioSessionState? playbackSessionState = null,
        int? playbackCoreDeviceIndex = null)
    {
        Initialization = initialization ?? throw new ArgumentNullException(nameof(initialization));
        if (streamProgressRequired
            && !streamProgressSucceeded
            && failureKind == AudioDeviceTestFailureKind.None)
        {
            throw new ArgumentException(
                "A failed stream observation must specify its failure kind.",
                nameof(failureKind));
        }
        StreamProgressRequired = streamProgressRequired;
        StreamProgressSucceeded = streamProgressSucceeded;
        WallClockDuration = wallClockDuration;
        PlaybackPositionDuration = playbackPositionDuration;
        ProgressRatio = progressRatio;
        FailureReason = failureReason;
        FailureKind = failureKind;
        PlaybackStage = playbackStage;
        NativeErrorSource = nativeErrorSource;
        NativeErrorCode = nativeErrorCode;
        DiagnosticReason = diagnosticReason ?? failureReason;
        PlaybackSourceHandle = playbackSourceHandle;
        PlaybackExpectedMixerHandle = playbackExpectedMixerHandle;
        PlaybackActualMixerHandle = playbackActualMixerHandle;
        PlaybackBackend = playbackBackend;
        PlaybackSessionState = playbackSessionState;
        PlaybackCoreDeviceIndex = playbackCoreDeviceIndex;
    }

    /// <summary>Gets the requested and negotiated native initialization values.</summary>
    internal AudioPlaybackInitializationResult Initialization { get; }

    /// <summary>Gets whether native device initialization completed.</summary>
    internal bool DeviceInitializationSucceeded => Initialization != null;

    /// <summary>Gets whether this request required observed test-sound progress.</summary>
    internal bool StreamProgressRequired { get; }

    /// <summary>
    /// Gets whether monotonic, real-time stream progress was observed and the test sound reached
    /// its natural end.
    /// </summary>
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

    /// <summary>Gets the feature boundary that produced the failure.</summary>
    internal AudioDeviceTestFailureKind FailureKind { get; }

    /// <summary>Gets the native playback stage, when the player supplied one.</summary>
    internal BassAudioPlaybackStage? PlaybackStage { get; }

    /// <summary>Gets the native API that supplied the playback error.</summary>
    internal string NativeErrorSource { get; }

    /// <summary>Gets the native playback error code captured at the failure boundary.</summary>
    internal BASSError? NativeErrorCode { get; }

    /// <summary>Gets the source handle captured at the playback failure boundary.</summary>
    internal int? PlaybackSourceHandle { get; }

    /// <summary>Gets the expected mixer handle captured at the playback failure boundary.</summary>
    internal int? PlaybackExpectedMixerHandle { get; }

    /// <summary>Gets the observed mixer handle captured at the playback failure boundary.</summary>
    internal int? PlaybackActualMixerHandle { get; }

    /// <summary>Gets the backend captured from the owning playback session.</summary>
    internal BassAudioPlayer.DeviceDriver? PlaybackBackend { get; }

    /// <summary>Gets the session lifecycle state captured at the playback failure boundary.</summary>
    internal BassAudioSessionState? PlaybackSessionState { get; }

    /// <summary>Gets the BASS core device captured from the owning playback session.</summary>
    internal int? PlaybackCoreDeviceIndex { get; }

    /// <summary>Gets a diagnostic reason retained for logs and non-user-facing diagnostics.</summary>
    internal string DiagnosticReason { get; }

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

    /// <summary>Gets the channel count accepted by the endpoint or callback.</summary>
    internal int ActualChannels => Initialization.ActualChannels;

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

/// <summary>
/// Contains one bounded observation of playback-position progress and natural completion.
/// </summary>
internal readonly struct AudioDeviceTestStreamObservation
{
    /// <summary>Creates an immutable stream observation.</summary>
    internal AudioDeviceTestStreamObservation(
        bool succeeded,
        TimeSpan wallClockDuration,
        TimeSpan playbackPositionDuration,
        double? progressRatio,
        string failureReason,
        AudioDeviceTestFailureKind failureKind = AudioDeviceTestFailureKind.None,
        BassAudioPlaybackStage? playbackStage = null,
        string nativeErrorSource = null,
        BASSError? nativeErrorCode = null,
        string diagnosticReason = null,
        int? playbackSourceHandle = null,
        int? playbackExpectedMixerHandle = null,
        int? playbackActualMixerHandle = null,
        BassAudioPlayer.DeviceDriver? playbackBackend = null,
        BassAudioSessionState? playbackSessionState = null,
        int? playbackCoreDeviceIndex = null)
    {
        Succeeded = succeeded;
        WallClockDuration = wallClockDuration;
        PlaybackPositionDuration = playbackPositionDuration;
        ProgressRatio = progressRatio;
        FailureReason = failureReason;
        FailureKind = failureKind;
        PlaybackStage = playbackStage;
        NativeErrorSource = nativeErrorSource;
        NativeErrorCode = nativeErrorCode;
        DiagnosticReason = diagnosticReason ?? failureReason;
        PlaybackSourceHandle = playbackSourceHandle;
        PlaybackExpectedMixerHandle = playbackExpectedMixerHandle;
        PlaybackActualMixerHandle = playbackActualMixerHandle;
        PlaybackBackend = playbackBackend;
        PlaybackSessionState = playbackSessionState;
        PlaybackCoreDeviceIndex = playbackCoreDeviceIndex;
    }

    /// <summary>
    /// Gets whether stream movement met every progress criterion and playback ended naturally.
    /// </summary>
    internal bool Succeeded { get; }

    /// <summary>Gets the measured wall-clock interval.</summary>
    internal TimeSpan WallClockDuration { get; }

    /// <summary>Gets the measured playback-position interval.</summary>
    internal TimeSpan PlaybackPositionDuration { get; }

    /// <summary>Gets playback-position duration divided by wall-clock duration.</summary>
    internal double? ProgressRatio { get; }

    /// <summary>Gets the observation failure reason.</summary>
    internal string FailureReason { get; }

    /// <summary>Gets the feature boundary that produced the observation failure.</summary>
    internal AudioDeviceTestFailureKind FailureKind { get; }

    /// <summary>Gets the native playback stage, when available.</summary>
    internal BassAudioPlaybackStage? PlaybackStage { get; }

    /// <summary>Gets the native API that supplied the playback error.</summary>
    internal string NativeErrorSource { get; }

    /// <summary>Gets the native playback error code captured at the failure boundary.</summary>
    internal BASSError? NativeErrorCode { get; }

    /// <summary>Gets the source handle captured at the playback failure boundary.</summary>
    internal int? PlaybackSourceHandle { get; }

    /// <summary>Gets the expected mixer handle captured at the playback failure boundary.</summary>
    internal int? PlaybackExpectedMixerHandle { get; }

    /// <summary>Gets the observed mixer handle captured at the playback failure boundary.</summary>
    internal int? PlaybackActualMixerHandle { get; }

    /// <summary>Gets the backend captured from the owning playback session.</summary>
    internal BassAudioPlayer.DeviceDriver? PlaybackBackend { get; }

    /// <summary>Gets the session lifecycle state captured at the playback failure boundary.</summary>
    internal BassAudioSessionState? PlaybackSessionState { get; }

    /// <summary>Gets the BASS core device captured from the owning playback session.</summary>
    internal int? PlaybackCoreDeviceIndex { get; }

    /// <summary>Gets a diagnostic reason retained for logs and non-user-facing diagnostics.</summary>
    internal string DiagnosticReason { get; }
}

/// <summary>Evaluates bounded playback movement independently from native device initialization.</summary>
internal static class AudioDeviceTestStreamObserver
{
    private static readonly TimeSpan PollInterval = TimeSpan.FromMilliseconds(100);

    private static readonly TimeSpan RequiredProgressInterval = TimeSpan.FromSeconds(1);

    private static readonly TimeSpan ObservationTimeout = TimeSpan.FromSeconds(10);

    /// <summary>
    /// Observes one test-sound playback through natural completion and reports bounded real-time
    /// progress after startup pre-roll.
    /// </summary>
    internal static AudioDeviceTestStreamObservation Observe(
        string testSoundPath,
        IAudioDeviceTestSoundBoundary soundBoundary)
    {
        ArgumentNullException.ThrowIfNull(soundBoundary);
        if (!soundBoundary.FileExists(testSoundPath))
        {
            return Failure(
                "The configured test sound file is unavailable.",
                AudioDeviceTestFailureKind.TestSoundUnavailable);
        }

        IAudioPlayer player;
        try
        {
            player = soundBoundary.CreatePlayer(testSoundPath);
        }
        catch (BassAudioPlaybackException exception)
        {
            return Failure(
                "Creating the test-sound player failed.",
                AudioDeviceTestFailureKind.PlayerCreationFailed,
                exception);
        }
        catch (Exception exception)
        {
            return Failure(
                "Creating the test-sound player failed unexpectedly.",
                AudioDeviceTestFailureKind.PlayerCreationFailed,
                diagnosticReason: exception.ToString());
        }

        using (player)
        {
            TimeSpan duration;
            try
            {
                duration = player.Duration;
            }
            catch (Exception exception)
            {
                if (exception is BassAudioPlaybackException playbackException)
                {
                    return Failure(
                        "Reading the test-sound duration failed.",
                        AudioDeviceTestFailureKind.Unexpected,
                        playbackException);
                }
                return Failure(
                    "Reading the test-sound duration failed unexpectedly.",
                    AudioDeviceTestFailureKind.Unexpected,
                    diagnosticReason: exception.ToString());
            }
            if (duration <= TimeSpan.Zero)
            {
                return Failure(
                    "The test sound did not report a valid duration.",
                    AudioDeviceTestFailureKind.InvalidDuration);
            }

            TimeSpan completionTimeout = duration > TimeSpan.MaxValue - ObservationTimeout
                ? TimeSpan.MaxValue
                : duration + ObservationTimeout;
            TimeSpan initialPosition;
            try
            {
                initialPosition = player.CurrentTime;
            }
            catch (Exception exception)
            {
                if (exception is BassAudioPlaybackException playbackException)
                {
                    return Failure(
                        "Reading the initial test-sound position failed.",
                        AudioDeviceTestFailureKind.Unexpected,
                        playbackException);
                }
                return Failure(
                    "Reading the initial test-sound position failed unexpectedly.",
                    AudioDeviceTestFailureKind.Unexpected,
                    diagnosticReason: exception.ToString());
            }
            TimeSpan previousPosition = initialPosition;
            long overallStart = soundBoundary.GetTimestamp();
            long progressStart = 0;
            TimeSpan progressStartPosition = TimeSpan.Zero;
            bool progressValidated = false;
            TimeSpan validatedWallClockDuration = TimeSpan.Zero;
            TimeSpan validatedPlaybackDuration = TimeSpan.Zero;
            double? validatedProgressRatio = null;
            try
            {
                player.Play();
            }
            catch (BassAudioPlaybackException exception)
            {
                return Failure(
                    "Starting the test-sound player failed.",
                    AudioDeviceTestFailureKind.PlaybackStartFailed,
                    exception);
            }
            catch (Exception exception)
            {
                return Failure(
                    "Starting the test-sound player failed unexpectedly.",
                    AudioDeviceTestFailureKind.Unexpected,
                    diagnosticReason: exception.ToString());
            }
            while (true)
            {
                soundBoundary.Wait(PollInterval);
                long now = soundBoundary.GetTimestamp();
                TimeSpan currentPosition;
                PlayState playState;
                try
                {
                    currentPosition = player.CurrentTime;
                    playState = player.PlayState;
                }
                catch (Exception exception)
                {
                    if (exception is BassAudioPlaybackException playbackException)
                    {
                        return Failure(
                            "Observing the test-sound player failed.",
                            AudioDeviceTestFailureKind.Unexpected,
                            playbackException);
                    }
                    return Failure(
                        "Observing the test-sound player failed unexpectedly.",
                        AudioDeviceTestFailureKind.Unexpected,
                        diagnosticReason: exception.ToString());
                }
                TimeSpan completionPosition = currentPosition;
                if (playState == PlayState.Stopped)
                {
                    try
                    {
                        completionPosition = player.CurrentTime;
                    }
                    catch (Exception exception)
                    {
                        if (exception is BassAudioPlaybackException playbackException)
                        {
                            return Failure(
                                "Reading the completed test-sound position failed.",
                                AudioDeviceTestFailureKind.Unexpected,
                                playbackException);
                        }
                        return Failure(
                            "Reading the completed test-sound position failed unexpectedly.",
                            AudioDeviceTestFailureKind.Unexpected,
                            diagnosticReason: exception.ToString());
                    }
                }
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
                        "The test-sound playback position moved backwards.",
                        AudioDeviceTestFailureKind.PlaybackPositionMovedBackwards);
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
                            "The test sound stopped before playback progress was observed.",
                            AudioDeviceTestFailureKind.PlaybackStoppedEarly);
                    }
                }
                else
                {
                    TimeSpan wallClockDuration = soundBoundary.GetElapsedTime(progressStart, now);
                    TimeSpan playbackDuration = currentPosition - progressStartPosition;
                    if (playState == PlayState.Stopped)
                    {
                        if (completionPosition < duration)
                        {
                            return new AudioDeviceTestStreamObservation(
                                false,
                                wallClockDuration,
                                playbackDuration,
                                CalculateRatio(playbackDuration, wallClockDuration),
                                "The test sound stopped before reaching its reported duration.",
                                AudioDeviceTestFailureKind.PlaybackStoppedEarly);
                        }

                        if (!progressValidated)
                        {
                            if (wallClockDuration < RequiredProgressInterval)
                            {
                                return new AudioDeviceTestStreamObservation(
                                    false,
                                    wallClockDuration,
                                    playbackDuration,
                                    CalculateRatio(playbackDuration, wallClockDuration),
                                    "The test sound ended before a complete progress interval was observed.",
                                    AudioDeviceTestFailureKind.PlaybackStoppedEarly);
                            }

                            double terminalRatio = CalculateRatio(playbackDuration, wallClockDuration) ?? 0;
                            if (terminalRatio < 0.75 || terminalRatio > 1.25)
                            {
                                return new AudioDeviceTestStreamObservation(
                                    false,
                                    wallClockDuration,
                                    playbackDuration,
                                    terminalRatio,
                                    "The test-sound progress ratio was outside the accepted range.",
                                    AudioDeviceTestFailureKind.PlaybackRateOutOfRange);
                            }

                            validatedWallClockDuration = wallClockDuration;
                            validatedPlaybackDuration = playbackDuration;
                            validatedProgressRatio = terminalRatio;
                        }

                        return new AudioDeviceTestStreamObservation(
                            true,
                            validatedWallClockDuration,
                            validatedPlaybackDuration,
                            validatedProgressRatio,
                            null,
                            AudioDeviceTestFailureKind.None);
                    }

                    if (currentPosition <= previousPosition)
                    {
                        return new AudioDeviceTestStreamObservation(
                            false,
                            wallClockDuration,
                            playbackDuration,
                            CalculateRatio(playbackDuration, wallClockDuration),
                            "The test-sound playback position stopped advancing.",
                            AudioDeviceTestFailureKind.PlaybackDidNotAdvance);
                    }
                    if (!progressValidated && wallClockDuration >= RequiredProgressInterval)
                    {
                        double ratio = CalculateRatio(playbackDuration, wallClockDuration) ?? 0;
                        if (ratio < 0.75 || ratio > 1.25)
                        {
                            return new AudioDeviceTestStreamObservation(
                                false,
                                wallClockDuration,
                                playbackDuration,
                                ratio,
                                "The test-sound progress ratio was outside the accepted range.",
                                AudioDeviceTestFailureKind.PlaybackRateOutOfRange);
                        }

                        progressValidated = true;
                        validatedWallClockDuration = wallClockDuration;
                        validatedPlaybackDuration = playbackDuration;
                        validatedProgressRatio = ratio;
                    }
                }

                previousPosition = currentPosition;
                TimeSpan overallElapsed = soundBoundary.GetElapsedTime(overallStart, now);
                if (!progressValidated && overallElapsed >= ObservationTimeout)
                {
                    TimeSpan playbackDuration = currentPosition - initialPosition;
                    return new AudioDeviceTestStreamObservation(
                        false,
                        overallElapsed,
                        playbackDuration,
                        CalculateRatio(playbackDuration, overallElapsed),
                        "The test sound did not produce a complete progress observation before timeout.",
                        AudioDeviceTestFailureKind.ObservationTimedOut);
                }
                if (progressValidated && overallElapsed >= completionTimeout)
                {
                    return new AudioDeviceTestStreamObservation(
                        false,
                        validatedWallClockDuration,
                        validatedPlaybackDuration,
                        validatedProgressRatio,
                        "The test sound did not reach its natural end before timeout.",
                        AudioDeviceTestFailureKind.ObservationTimedOut);
                }
            }
        }
    }

    private static double? CalculateRatio(TimeSpan playbackDuration, TimeSpan wallClockDuration)
    {
        return wallClockDuration > TimeSpan.Zero
            ? playbackDuration.TotalSeconds / wallClockDuration.TotalSeconds
            : null;
    }

    private static AudioDeviceTestStreamObservation Failure(
        string reason,
        AudioDeviceTestFailureKind failureKind = AudioDeviceTestFailureKind.Unexpected,
        BassAudioPlaybackException exception = null,
        string diagnosticReason = null)
        => new(
            false,
            TimeSpan.Zero,
            TimeSpan.Zero,
            null,
            reason,
            failureKind,
            exception?.Stage,
            exception?.NativeErrorSource,
            exception?.NativeErrorCode,
            diagnosticReason ?? exception?.ToString() ?? reason,
            exception?.SourceHandle,
            exception?.ExpectedMixerHandle,
            exception?.ActualMixerHandle,
            exception?.Backend,
            exception?.SessionState,
            exception?.CoreDeviceIndex);
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
            using BassAudioOperationLease operation = Ribbit.Media.Audio.BassNet.EnterAudioOperation();
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
                negotiated.ActualChannels,
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
                observation.FailureReason,
                observation.FailureKind,
                observation.PlaybackStage,
                observation.NativeErrorSource,
                observation.NativeErrorCode,
                observation.DiagnosticReason,
                observation.PlaybackSourceHandle,
                observation.PlaybackExpectedMixerHandle,
                observation.PlaybackActualMixerHandle,
                observation.PlaybackBackend,
                observation.PlaybackSessionState,
                observation.PlaybackCoreDeviceIndex);
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
            NLogWrapper.GetLogger(nameof(BassAudioDeviceTestRuntime)).Info(
                "Audio device test result. requestedBackend=" + AudioDriverDisplayNames.Get(result.RequestedBackend)
                + " requestedDevice=[name=" + result.RequestedDeviceName + ",identity=" + result.RequestedDevice + "]"
                + " requestedRate=" + result.RequestedRate
                + " requestedFormat=" + result.RequestedFormat
                + " requestedBufferMs=" + result.RequestedBufferSize
                + " requestedEventMode=" + result.RequestedEventMode
                + " actualBackend=" + AudioDriverDisplayNames.Get(result.ActualBackend)
                + " actualDevice=[name=" + result.ActualDeviceName + ",identity=" + result.ActualDevice + "]"
                + " actualRate=" + result.ActualRate
                + " actualChannels=" + result.ActualChannels
                + " engineFormat=" + result.EngineFormat
                + " endpointFormat=" + result.EndpointFormat
                + " latencyMs=" + result.Latency
                + " initializationSucceeded=" + result.DeviceInitializationSucceeded
                + " streamProgressRequired=" + result.StreamProgressRequired
                + " streamProgressSucceeded=" + result.StreamProgressSucceeded
                + " wallClockMs=" + result.WallClockDuration.TotalMilliseconds
                + " playbackPositionMs=" + result.PlaybackPositionDuration.TotalMilliseconds
                + " progressRatio=" + result.ProgressRatio
                + " fallbackOccurred=" + result.FallbackOccurred
                + " fallbackReason=" + result.FallbackReason
                + " isSilentFallback=" + result.IsSilentFallback
                 + " failureKind=" + result.FailureKind
                 + " playbackStage=" + result.PlaybackStage
                 + " nativeErrorSource=" + result.NativeErrorSource
                 + " nativeErrorCode=" + result.NativeErrorCode
                 + " playbackSourceHandle=" + result.PlaybackSourceHandle
                 + " playbackExpectedMixerHandle=" + result.PlaybackExpectedMixerHandle
                 + " playbackActualMixerHandle=" + result.PlaybackActualMixerHandle
                 + " playbackBackend=" + result.PlaybackBackend
                 + " playbackSessionState=" + result.PlaybackSessionState
                 + " playbackCoreDeviceIndex=" + result.PlaybackCoreDeviceIndex
                 + " failureReason=" + result.FailureReason
                 + " diagnosticReason=" + result.DiagnosticReason);
        }
        catch
        {
            // Diagnostics must not change the device-test result.
        }
    }
}
