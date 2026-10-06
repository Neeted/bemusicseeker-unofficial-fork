#nullable enable annotations
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using BeMusicSeeker.Models;
using ManagedBass;
using Ribbit.Logging;
using Ribbit.Media;
using Ribbit.Media.Audio;

namespace BeMusicSeeker.ViewModels;

/// <summary>音声デバイステスト開始時に捕捉した設定を保持します。</summary>
internal sealed class AudioDeviceTestRequest
{
    /// <summary>デバイステストに使う音声設定を変更不能な値として初期化します。</summary>
    /// <param name="playerMixerThreadCount">テスト開始時に捕捉する1～4のnativeミキサーthread数です。</param>
    internal AudioDeviceTestRequest(
        AudioDriver playerDriver,
        string playerDevice,
        string playerDeviceName,
        SampleRate playerSampleRate,
        SampleFormat playerFormat,
        float playerBufferSize,
        bool playerWASAPIParam,
        int playerVolume,
        bool playSound,
        int sampleRateConversionQuality = AudioResamplingQuality.Default,
        int playerMixerThreadCount = BassMixerThreadConfigurator.RealtimeDefaultThreadCount)
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
        SampleRateConversionQuality = AudioResamplingQuality.Validate(sampleRateConversionQuality);
        PlayerMixerThreadCount = BassMixerThreadConfigurator.ValidateRealtimeThreadCount(playerMixerThreadCount);
        AudioOutputRequest = new AudioOutputRequest(
            PlayerDriver,
            PlayerDevice,
            PlayerDeviceName,
            PlayerSampleRate,
            PlayerFormat,
            PlayerBufferSize,
            PlayerWASAPIParam,
            SampleRateConversionQuality,
            AudioOutputPurpose.DeviceTest,
            PlayerMixerThreadCount);
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

    /// <summary>このテスト開始時に捕捉したサンプルレート変換品質です。</summary>
    internal int SampleRateConversionQuality { get; }

    /// <summary>このテスト開始時に捕捉したnativeミキサーthread数です。</summary>
    internal int PlayerMixerThreadCount { get; }

    /// <summary>選択条件のテストへ渡す一回分の音声出力要求を取得します。</summary>
    internal AudioOutputRequest AudioOutputRequest { get; }

    internal bool PlaySound { get; }
}

/// <summary>オーディオデバイステストで失敗した処理の境界を表します。</summary>
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
    Unexpected,
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
    /// <param name="cleanupDiagnostics">native cleanup stages and errors captured without exception messages.</param>
    internal AudioDeviceTestResult(
        AudioDeviceTestRequest request,
        AudioPlaybackInitializationResult? initialization,
        bool streamProgressRequired,
        bool streamProgressSucceeded,
        TimeSpan wallClockDuration,
        TimeSpan playbackPositionDuration,
        double? progressRatio,
        string? failureReason,
        AudioDeviceTestFailureKind failureKind = AudioDeviceTestFailureKind.None,
        BassAudioPlaybackStage? playbackStage = null,
        string? nativeErrorSource = null,
        Errors? nativeErrorCode = null,
        string? diagnosticReason = null,
        int? playbackSourceHandle = null,
        int? playbackExpectedMixerHandle = null,
        int? playbackActualMixerHandle = null,
        BassAudioPlayer.DeviceDriver? playbackBackend = null,
        BassAudioSessionState? playbackSessionState = null,
        int? playbackCoreDeviceIndex = null,
        Exception? primaryFailure = null,
        Exception? cleanupFailure = null,
        IReadOnlyList<BassAudioCleanupDiagnostic>? cleanupDiagnostics = null)
    {
        ArgumentNullException.ThrowIfNull(request);
        Initialization = initialization;
        Request = request;
        PrimaryFailure = primaryFailure;
        CleanupFailure = cleanupFailure;
        CleanupDiagnostics = Array.AsReadOnly(cleanupDiagnostics?.ToArray()
            ?? Array.Empty<BassAudioCleanupDiagnostic>());
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

    /// <summary>初期化失敗でも保持する、受理時に固定した要求です。</summary>
    internal AudioDeviceTestRequest Request { get; }

    /// <summary>解放に先行した初期化・再生・取消の失敗です。</summary>
    internal Exception? PrimaryFailure { get; }

    /// <summary>主失敗と独立して保持する、native解放の失敗です。</summary>
    internal Exception? CleanupFailure { get; }

    /// <summary>解放を確認できなかったnative段階とエラーを構造化して保持します。</summary>
    internal IReadOnlyList<BassAudioCleanupDiagnostic> CleanupDiagnostics { get; }

    /// <summary>Gets the requested and negotiated native initialization values.</summary>
    internal AudioPlaybackInitializationResult? Initialization { get; }

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
    internal bool Succeeded => PrimaryFailure == null && CleanupFailure == null && DeviceInitializationSucceeded
        && (!StreamProgressRequired || StreamProgressSucceeded);

    /// <summary>Gets the measured wall-clock interval after startup pre-roll.</summary>
    internal TimeSpan WallClockDuration { get; }

    /// <summary>Gets the measured playback-position interval after startup pre-roll.</summary>
    internal TimeSpan PlaybackPositionDuration { get; }

    /// <summary>Gets playback-position duration divided by wall-clock duration.</summary>
    internal double? ProgressRatio { get; }

    /// <summary>Gets why stream observation failed, if it failed.</summary>
    internal string? FailureReason { get; }

    /// <summary>Gets the feature boundary that produced the failure.</summary>
    internal AudioDeviceTestFailureKind FailureKind { get; }

    /// <summary>Gets the native playback stage, when the player supplied one.</summary>
    internal BassAudioPlaybackStage? PlaybackStage { get; }

    /// <summary>Gets the native API that supplied the playback error.</summary>
    internal string? NativeErrorSource { get; }

    /// <summary>Gets the native playback error code captured at the failure boundary.</summary>
    internal Errors? NativeErrorCode { get; }

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
    internal string? DiagnosticReason { get; }

    /// <summary>Gets the backend selected by the caller.</summary>
    internal AudioDriver RequestedBackend => Request.AudioOutputRequest.Backend;

    /// <summary>Gets the backend that owns the initialized native session.</summary>
    internal AudioDriver? ActualBackend => Initialization?.ActualBackend;

    /// <summary>Gets whether native negotiation used a fallback.</summary>
    internal bool FallbackOccurred => Initialization?.FallbackOccurred == true;

    /// <summary>Gets why native negotiation used a fallback.</summary>
    internal string? FallbackReason => Initialization?.FallbackReason;

    /// <summary>Gets the requested endpoint identity.</summary>
    internal string RequestedDevice => Request.AudioOutputRequest.DeviceIdentity;

    /// <summary>Gets the requested endpoint display name.</summary>
    internal string RequestedDeviceName => Request.AudioOutputRequest.DeviceName;

    /// <summary>Gets the negotiated endpoint identity.</summary>
    internal string? ActualDevice => Initialization?.ActualDevice;

    /// <summary>Gets the negotiated endpoint display name.</summary>
    internal string? ActualDeviceName => Initialization?.ActualDeviceName;

    /// <summary>Gets the requested sample rate.</summary>
    internal SampleRate RequestedRate => Request.AudioOutputRequest.Rate;

    /// <summary>Gets the requested sample format.</summary>
    internal SampleFormat RequestedFormat => Request.AudioOutputRequest.Format;

    /// <summary>Gets the requested buffer size in milliseconds.</summary>
    internal float RequestedBufferSize => Request.AudioOutputRequest.BufferSize;

    /// <summary>Gets whether event-driven WASAPI was requested.</summary>
    internal bool RequestedEventMode => Request.AudioOutputRequest.EventMode;

    /// <summary>Gets the requested output volume.</summary>
    internal int RequestedVolume => Request.PlayerVolume;

    /// <summary>Gets the negotiated sample rate.</summary>
    internal SampleRate? ActualRate => Initialization?.ActualRate;

    /// <summary>Gets the internal mixer format.</summary>
    internal SampleFormat? EngineFormat => Initialization?.EngineFormat;

    /// <summary>Gets the native endpoint or callback format.</summary>
    internal SampleFormat? EndpointFormat => Initialization?.EndpointFormat;

    /// <summary>Gets the channel count accepted by the endpoint or callback.</summary>
    internal int? ActualChannels => Initialization?.ActualChannels;

    /// <summary>Gets the negotiated latency in milliseconds.</summary>
    internal double? Latency => Initialization?.Latency;

    /// <summary>Gets whether a non-audible fallback was substituted.</summary>
    internal bool IsSilentFallback => Initialization?.IsSilentFallback == true;
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

    /// <summary>次の再生観測まで待機します。</summary>
    void Wait(TimeSpan interval);
}

/// <summary>テスト音声の観測をシステム時刻とBASSプレイヤーへ接続します。</summary>
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
    public void Wait(TimeSpan interval)
        => Thread.Sleep(interval);
}

/// <summary>
/// Contains one bounded observation of playback-position progress and natural completion.
/// </summary>
internal readonly record struct AudioDeviceTestStreamObservation
{
    /// <summary>再生の観測値を上書きしない、音源プレイヤー解放時の失敗です。</summary>
    internal Exception CleanupFailure { get; init; }

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
        Errors? nativeErrorCode = null,
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
    internal Errors? NativeErrorCode { get; }

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

    /// <summary>テスト音声を自然終了まで観測し、再生進行結果を返します。</summary>
    /// <param name="testSoundPath">観測するテスト音声のパス。</param>
    /// <param name="soundBoundary">時刻、プレイヤー、待機を提供する境界。</param>
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

        AudioDeviceTestStreamObservation observation;
        try
        {
            observation = ObservePlayer(player, soundBoundary);
        }
        catch (Exception exception)
        {
            observation = Failure("Observing the test-sound player failed unexpectedly.",
                diagnosticReason: exception.ToString());
        }
        try
        {
            player.Dispose();
        }
        catch (Exception exception)
        {
            observation = observation with { CleanupFailure = exception };
        }
        return observation;
    }

    private static AudioDeviceTestStreamObservation ObservePlayer(
        IAudioPlayer player, IAudioDeviceTestSoundBoundary soundBoundary)
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
    /// <summary>捕捉済み出力条件でテストを実行し、解放完了まで所有します。</summary>
    /// <param name="request">テスト開始時に捕捉した出力要求。</param>
    AudioDeviceTestResult Run(AudioDeviceTestRequest request);
}

internal interface IAudioDeviceTestPlaybackPort
{
    /// <summary>ファイル変更・runtime解放前に、再生と先読みの必要な停止を終端まで待ちます。</summary>
    Task StopPlayback();
}

/// <summary>音声backendの選択済み機器能力を一回照会する境界です。</summary>
internal interface IAudioDeviceCapabilityRuntime
{
    /// <summary>取消要求をnative呼出し間で確認し、取得した資源を解放して終了します。</summary>
    AudioDeviceCapabilityResult Query(AudioDeviceCapabilityRequest request, CancellationToken cancellationToken);
}

/// <summary>既存音声sessionの所有規則を通して機器能力を照会します。</summary>
internal sealed class BassAudioDeviceCapabilityRuntime : IAudioDeviceCapabilityRuntime
{
    /// <inheritdoc />
    public AudioDeviceCapabilityResult Query(AudioDeviceCapabilityRequest request, CancellationToken cancellationToken)
        => BassAudioPlayer.QueryAudioDeviceCapabilities(request, cancellationToken);
}

/// <summary>機器照会とテストの受付、解放完了を所有します。</summary>
internal sealed partial class AudioDeviceTestWorkflowOwner : Livet.ViewModel
{
    private readonly object syncRoot = new();

    private readonly IAudioDeviceTestPlaybackPort playbackPort;

    private readonly IAudioDeviceTestRuntime runtime;

    private readonly IAudioDeviceCapabilityRuntime capabilityRuntime;

    private CancellationTokenSource activeQueryCancellation;

    private int isRunning;

    private int isTestRunning;

    internal AudioDeviceTestWorkflowOwner(
        IAudioDeviceTestPlaybackPort playbackPort,
        IAudioDeviceTestRuntime runtime,
        IAudioDeviceCapabilityRuntime capabilityRuntime = null)
    {
        this.playbackPort = playbackPort ?? throw new ArgumentNullException(nameof(playbackPort));
        this.runtime = runtime ?? throw new ArgumentNullException(nameof(runtime));
        this.capabilityRuntime = capabilityRuntime ?? new BassAudioDeviceCapabilityRuntime();
    }

    /// <summary>機器照会またはテストが受付を使用中かどうかを取得します。</summary>
    internal bool IsRunning => Volatile.Read(ref isRunning) != 0;

    /// <summary>テストを実行中かどうかを取得します。</summary>
    internal bool IsTestRunning => Volatile.Read(ref isTestRunning) != 0;

    /// <summary>受理した照会またはテストの資源解放後、受付が可能になったことを通知します。</summary>
    internal event Action OperationReleased;

    /// <summary>通常出力を停止して一件のテストを受付・実行し、解放完了まで所有します。</summary>
    /// <param name="request">テスト開始時に捕捉した出力要求。</param>
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

        if (!BassAudioRuntime.TryEnterAudioRequest(out IDisposable admission))
        {
            Volatile.Write(ref isRunning, 0);
            if (!BassAudioRuntime.OperationGate.IsCleanupQuarantined)
            {
                return null;
            }

            AudioInitializationException cleanupFailure = BassAudioPlayer.GetCleanupPendingFailure(
                BassAudioMapping.ToBassDriver(request.AudioOutputRequest.Backend),
                new BassAudioPlayer.DeviceDescriptor(request.PlayerDeviceName, request.PlayerDevice));
            return cleanupFailure == null
                ? null
                : new AudioDeviceTestResult(
                    request,
                    null,
                    request.PlaySound,
                    false,
                    TimeSpan.Zero,
                    TimeSpan.Zero,
                    null,
                    cleanupFailure.Message,
                    AudioDeviceTestFailureKind.Unexpected,
                    primaryFailure: cleanupFailure);
        }
        using IDisposable requestAdmission = admission;

        Volatile.Write(ref isTestRunning, 1);
        try
        {
            return await Task.Run(async () =>
            {
                await playbackPort.StopPlayback().ConfigureAwait(false);
                return runtime.Run(request);
            });
        }
        catch (Exception exception)
        {
            return new AudioDeviceTestResult(request, null, request.PlaySound, false,
                TimeSpan.Zero, TimeSpan.Zero, null, exception.Message,
                AudioDeviceTestFailureKind.Unexpected,
                primaryFailure: exception);
        }
        finally
        {
            Volatile.Write(ref isTestRunning, 0);
            Volatile.Write(ref isRunning, 0);
            requestAdmission.Dispose();
            OperationReleased?.Invoke();
        }
    }

    /// <summary>古い選択や閉じた画面の照会を、native呼出し間で終了させます。</summary>
    internal void CancelCurrentQuery()
    {
        lock (syncRoot)
        {
            activeQueryCancellation?.Cancel();
        }
    }

    /// <summary>通常再生を止めずに同じworkflow受付を使って機器能力を照会します。</summary>
    internal async Task<AudioDeviceCapabilityResult> TryQueryCapabilitiesAsync(
        AudioDeviceCapabilityRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (Interlocked.Exchange(ref isRunning, 1) != 0)
        {
            return null;
        }

        using var cancellationSource = new CancellationTokenSource();
        lock (syncRoot)
        {
            activeQueryCancellation = cancellationSource;
        }
        try
        {
            return await Task.Run(() => capabilityRuntime.Query(request, cancellationSource.Token));
        }
        finally
        {
            lock (syncRoot)
            {
                if (ReferenceEquals(activeQueryCancellation, cancellationSource))
                {
                    activeQueryCancellation = null;
                }
            }
            Volatile.Write(ref isRunning, 0);
            OperationReleased?.Invoke();
        }
    }
}

internal sealed class BassAudioDeviceTestRuntime : IAudioDeviceTestRuntime
{
    private readonly ApplicationPathSnapshot applicationPathSnapshot;

    private readonly BassAudioSessionLease sessionLease = new();

    private readonly IAudioDeviceTestSoundBoundary soundBoundary;
    private readonly Action<AudioDeviceTestRequest, Action<BassAudioSession>> initializeSession;
    private readonly Func<BassAudioSession, bool> releaseSession;
    private readonly BassAudioOperationGate operationGate;

    internal BassAudioDeviceTestRuntime(ApplicationPathSnapshot applicationPathSnapshot)
        : this(applicationPathSnapshot, new SystemAudioDeviceTestSoundBoundary())
    {
    }

    /// <summary>本番の取得・解放と、差替え可能なテスト音声の観測境界を接続します。</summary>
    internal BassAudioDeviceTestRuntime(
        ApplicationPathSnapshot applicationPathSnapshot,
        IAudioDeviceTestSoundBoundary soundBoundary)
        : this(applicationPathSnapshot, soundBoundary, InitializeSession, BassAudioPlayer.Free, BassAudioRuntime.OperationGate)
    {
    }

    /// <summary>同じ受付・session所有経路を使い、外部のnative取得と解放だけを差し替えます。</summary>
    internal BassAudioDeviceTestRuntime(
        ApplicationPathSnapshot applicationPathSnapshot,
        IAudioDeviceTestSoundBoundary soundBoundary,
        Action<AudioDeviceTestRequest, Action<BassAudioSession>> initializeSession,
        Func<BassAudioSession, bool> releaseSession,
        BassAudioOperationGate operationGate)
    {
        this.applicationPathSnapshot = applicationPathSnapshot ?? throw new ArgumentNullException(nameof(applicationPathSnapshot));
        this.soundBoundary = soundBoundary ?? throw new ArgumentNullException(nameof(soundBoundary));
        this.initializeSession = initializeSession ?? throw new ArgumentNullException(nameof(initializeSession));
        this.releaseSession = releaseSession ?? throw new ArgumentNullException(nameof(releaseSession));
        this.operationGate = operationGate ?? throw new ArgumentNullException(nameof(operationGate));
    }

    private static void InitializeSession(AudioDeviceTestRequest request, Action<BassAudioSession> acquired)
    {
        BassAudioSession session = null;
        try
        {
            _ = BassAudioPlayer.InitializeOwned(request.AudioOutputRequest, request.PlayerVolume, out session);
        }
        finally
        {
            acquired(session);
        }
    }

    public AudioDeviceTestResult Run(AudioDeviceTestRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        BassAudioSession ownedSession = null;
        AudioPlaybackInitializationResult initialization = null;
        AudioDeviceTestStreamObservation observation = default;
        Exception primaryFailure = null;
        Exception cleanupFailure = null;
        try
        {
            if (sessionLease.Session != null && !sessionLease.TryRelease(releaseSession))
            {
                throw new InvalidOperationException("A previous audio device test still owns native resources.");
            }
            BassAudioPlaybackRuntime.ThrowIfAudiblePlaybackUsesNullDevice(
                request.PlayerDriver, request.PlayerDevice, request.PlayerDeviceName);
            initializeSession(request, session => ownedSession = session);
            sessionLease.Attach(ownedSession);
            if (!operationGate.TryEnterOperation(out BassAudioOperationLease admittedOperation))
            {
                throw new InvalidOperationException("The native audio operation gate rejected the admitted test.");
            }
            using BassAudioOperationLease operation = admittedOperation;
            BassAudioBackendResult negotiated = ownedSession.NegotiationResult
                ?? throw new InvalidOperationException("The audio device test has no negotiated result.");
            initialization = BassAudioPlaybackRuntime.CreateInitializationResult(
                request.AudioOutputRequest, request.PlayerVolume, ownedSession, negotiated);

            observation = request.PlaySound
                ? AudioDeviceTestStreamObserver.Observe(
                    applicationPathSnapshot.TestSoundPath,
                    soundBoundary)
                : new AudioDeviceTestStreamObservation(true, TimeSpan.Zero, TimeSpan.Zero, null, null);
            cleanupFailure = observation.CleanupFailure;

        }
        catch (Exception exception)
        {
            primaryFailure = exception;
            observation = new AudioDeviceTestStreamObservation(false, TimeSpan.Zero, TimeSpan.Zero, null,
                exception.Message, AudioDeviceTestFailureKind.Unexpected);
        }
        finally
        {
            try
            {
                if (ownedSession != null && sessionLease.Session == null)
                {
                    sessionLease.Attach(ownedSession);
                }
                if ((primaryFailure != null && sessionLease.Session?.State == BassAudioSessionState.CleanupPending)
                    || !sessionLease.TryRelease(releaseSession))
                {
                    var sessionFailure = new InvalidOperationException(
                        "The audio device test could not confirm native cleanup.");
                    cleanupFailure = cleanupFailure == null ? sessionFailure : new AggregateException(cleanupFailure, sessionFailure);
                }
            }
            catch (Exception exception)
            {
                cleanupFailure = cleanupFailure == null ? exception : new AggregateException(cleanupFailure, exception);
            }
        }
        var result = new AudioDeviceTestResult(
                request,
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
                observation.PlaybackCoreDeviceIndex,
                primaryFailure,
                cleanupFailure,
                sessionLease.Session?.CleanupDiagnostics);
        TryLogTestResult(result);
        return result;
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
                + " actualBackend=" + result.ActualBackend?.ToString()
                + " actualDevice=[name=" + result.ActualDeviceName + ",identity=" + result.ActualDevice + "]"
                + " actualRate=" + result.ActualRate
                + " actualChannels=" + result.ActualChannels
                + " engineFormat=" + result.EngineFormat
                + " endpointFormat=" + result.EndpointFormat
                + " endpointContainerBits=" + result.Initialization?.EndpointContainerBits
                + " endpointEffectiveBits=" + result.Initialization?.EndpointEffectiveBits
                + " latencyMs=" + result.Latency
                + " initializationSucceeded=" + result.DeviceInitializationSucceeded
                + " streamProgressRequired=" + result.StreamProgressRequired
                + " streamProgressSucceeded=" + result.StreamProgressSucceeded
                + " wallClockMs=" + result.WallClockDuration.TotalMilliseconds
                + " playbackPositionMs=" + result.PlaybackPositionDuration.TotalMilliseconds
                + " progressRatio=" + result.ProgressRatio
                + " fallbackOccurred=" + result.FallbackOccurred
                + " fallbackReason=" + result.FallbackReason
                + " attempts=" + string.Join(
                    " | ",
                    (result.Initialization?.Attempts ?? []).Select(attempt =>
                        attempt.Stage + " outcome=" + attempt.Outcome
                        + " nativeErrorSource=" + (attempt.NativeErrorSource ?? "none")
                        + " nativeErrorCode=" + BassNativeErrorFormatter.Format(attempt.NativeErrorCode)))
                + " isSilentFallback=" + result.IsSilentFallback
                 + " primaryFailure=" + result.PrimaryFailure
                 + " cleanupFailure=" + result.CleanupFailure
                 + " failureKind=" + result.FailureKind
                 + " playbackStage=" + result.PlaybackStage
                 + " nativeErrorSource=" + result.NativeErrorSource
                 + " nativeErrorCode="
                 + BassNativeErrorFormatter.Format(result.NativeErrorCode)
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
