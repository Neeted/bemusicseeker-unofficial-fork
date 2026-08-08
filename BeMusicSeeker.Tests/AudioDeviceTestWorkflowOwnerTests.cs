using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using BeMusicSeeker.Models;
using BeMusicSeeker.ViewModels;
using ManagedBass;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Ribbit.Media;
using Ribbit.Media.Audio;

namespace BeMusicSeeker.Tests;

[TestClass]
[DoNotParallelize]
public sealed class AudioDeviceTestWorkflowOwnerTests
{
    [TestMethod]
    public async Task TryRunAsync_StopsPlaybackBeforeRuntimeAndRejectsDuplicate()
    {
        var events = new List<string>();
        using var runtimeStarted = new ManualResetEventSlim();
        using var releaseRuntime = new ManualResetEventSlim();
        var runtime = new RecordingAudioDeviceTestRuntime(() =>
        {
            events.Add("runtime");
            runtimeStarted.Set();
            releaseRuntime.Wait();
            return CreateResult();
        });
        var owner = new AudioDeviceTestWorkflowOwner(
            new DelegateAudioDeviceTestPlaybackPort(() => events.Add("stop")),
            runtime);
        AudioDeviceTestRequest request = CreateRequest();

        Task<AudioDeviceTestResult> firstTask = owner.TryRunAsync(request);

        Assert.IsTrue(runtimeStarted.Wait(TimeSpan.FromSeconds(5)));
        Assert.IsTrue(owner.IsRunning);
        Assert.IsNull(await owner.TryRunAsync(request));

        releaseRuntime.Set();
        Assert.IsNotNull(await firstTask);
        Assert.IsFalse(owner.IsRunning);
        CollectionAssert.AreEqual(new[] { "stop", "runtime" }, events);
        Assert.AreEqual(1, runtime.CallCount);
    }

    [TestMethod]
    public async Task TryRunAsync_RuntimeFailureReleasesBusyState()
    {
        var owner = new AudioDeviceTestWorkflowOwner(
            new DelegateAudioDeviceTestPlaybackPort(() => { }),
            new RecordingAudioDeviceTestRuntime(() => throw new InvalidOperationException("audio test failed")));

        InvalidOperationException exception = await Assert.ThrowsExceptionAsync<InvalidOperationException>(
            () => owner.TryRunAsync(CreateRequest()));

        Assert.AreEqual("audio test failed", exception.Message);
        Assert.IsFalse(owner.IsRunning);
    }

    [TestMethod]
    public void StreamObserver_NoPlaybackMovement_ReturnsFailure()
    {
        var boundary = new FakeSoundBoundary(_ => TimeSpan.Zero)
        {
            StateProvider = _ => PlayState.Playing
        };

        AudioDeviceTestStreamObservation observation = AudioDeviceTestStreamObserver.Observe(
            "test.wav",
            boundary);

        Assert.IsFalse(observation.Succeeded);
        StringAssert.Contains(observation.FailureReason, "complete progress observation");
        Assert.IsTrue(boundary.Elapsed >= TimeSpan.FromSeconds(10));
        Assert.AreEqual(0d, observation.ProgressRatio.GetValueOrDefault());
    }

    [TestMethod]
    public void StreamObserver_RealTimeProgress_ReturnsMeasuredSuccess()
    {
        var boundary = new FakeSoundBoundary(elapsed => elapsed);

        AudioDeviceTestStreamObservation observation = AudioDeviceTestStreamObserver.Observe(
            "test.wav",
            boundary);

        Assert.IsTrue(observation.Succeeded);
        Assert.IsTrue(observation.WallClockDuration >= TimeSpan.FromSeconds(1));
        Assert.AreEqual(1d, observation.ProgressRatio.GetValueOrDefault(), 0.001);
        Assert.AreEqual(1, boundary.CreatedPlayerCount);
        Assert.AreEqual(1, boundary.PlayCount);
        Assert.AreEqual(1, boundary.DisposedPlayerCount);
        Assert.AreEqual(boundary.Duration, boundary.Elapsed);
        Assert.AreEqual(boundary.Elapsed, boundary.DisposedAt.GetValueOrDefault());
    }

    [TestMethod]
    public void StreamObserver_ValidatedProgress_DoesNotDisposeBeforeNaturalEnd()
    {
        var boundary = new FakeSoundBoundary(elapsed => elapsed)
        {
            Duration = TimeSpan.FromSeconds(3)
        };

        AudioDeviceTestStreamObservation observation = AudioDeviceTestStreamObserver.Observe(
            "test.wav",
            boundary);

        Assert.IsTrue(observation.Succeeded);
        Assert.AreEqual(1, boundary.CreatedPlayerCount);
        Assert.AreEqual(1, boundary.PlayCount);
        Assert.AreEqual(1, boundary.DisposedPlayerCount);
        Assert.AreEqual(TimeSpan.FromSeconds(3), boundary.DisposedAt.GetValueOrDefault());
    }

    [TestMethod]
    public void StreamObserver_NaturalEndBetweenPositionAndStateReads_ReturnsSuccess()
    {
        TimeSpan duration = TimeSpan.FromSeconds(2);
        var boundary = new FakeSoundBoundary(elapsed => elapsed)
        {
            Duration = duration,
            PositionReadProvider = (elapsed, readOrdinal) => elapsed < duration
                ? elapsed
                : readOrdinal == 0
                    ? duration - TimeSpan.FromMilliseconds(100)
                    : duration
        };

        AudioDeviceTestStreamObservation observation = AudioDeviceTestStreamObserver.Observe(
            "test.wav",
            boundary);

        Assert.IsTrue(observation.Succeeded);
        Assert.AreEqual(1d, observation.ProgressRatio.GetValueOrDefault(), 0.001);
        Assert.AreEqual(1, boundary.CreatedPlayerCount);
        Assert.AreEqual(1, boundary.PlayCount);
        Assert.AreEqual(1, boundary.DisposedPlayerCount);
        Assert.AreEqual(duration, boundary.DisposedAt.GetValueOrDefault());
    }

    [TestMethod]
    public void StreamObserver_NaturalEndTimeout_ReturnsFailureAndDisposesPlayer()
    {
        var boundary = new FakeSoundBoundary(elapsed => elapsed)
        {
            Duration = TimeSpan.FromSeconds(2),
            StateProvider = _ => PlayState.Playing
        };

        AudioDeviceTestStreamObservation observation = AudioDeviceTestStreamObserver.Observe(
            "test.wav",
            boundary);

        Assert.IsFalse(observation.Succeeded);
        StringAssert.Contains(observation.FailureReason, "natural end");
        Assert.IsTrue(boundary.Elapsed >= TimeSpan.FromSeconds(12));
        Assert.AreEqual(boundary.Elapsed, boundary.DisposedAt.GetValueOrDefault());
    }

    [TestMethod]
    public void StreamObserver_DoubleSpeedProgress_ReturnsFailure()
    {
        var boundary = new FakeSoundBoundary(elapsed => elapsed + elapsed)
        {
            Duration = TimeSpan.FromSeconds(4)
        };

        AudioDeviceTestStreamObservation observation = AudioDeviceTestStreamObserver.Observe(
            "test.wav",
            boundary);

        Assert.IsFalse(observation.Succeeded);
        Assert.AreEqual(2d, observation.ProgressRatio.GetValueOrDefault(), 0.001);
        StringAssert.Contains(observation.FailureReason, "outside the accepted range");
    }

    [TestMethod]
    public void StreamObserver_PlayerException_IsConvertedToUnexpectedFailure()
    {
        var boundary = new FakeSoundBoundary(_ => TimeSpan.Zero)
        {
            PlayException = new InvalidOperationException("play failed")
        };

        AudioDeviceTestStreamObservation observation = AudioDeviceTestStreamObserver.Observe(
            "test.wav",
            boundary);

        Assert.IsFalse(observation.Succeeded);
        Assert.AreEqual(AudioDeviceTestFailureKind.Unexpected, observation.FailureKind);
        StringAssert.Contains(observation.DiagnosticReason, "play failed");
    }

    [TestMethod]
    public void StreamObserver_TypedPlayerExceptionPreservesPlaybackDiagnostics()
    {
        var session = new BassAudioSession(BassAudioPlayer.DeviceDriver.WASAPI_SHARED)
        {
            ActualBackend = BassAudioPlayer.DeviceDriver.WASAPI_SHARED,
            CoreDeviceIndex = 6,
            State = BassAudioSessionState.Active
        };
        var boundary = new FakeSoundBoundary(_ => TimeSpan.Zero)
        {
            PlayException = new BassAudioPlaybackException(
                BassAudioPlaybackStage.MixerAttach,
                "test.wav",
                12,
                77,
                0,
                "BASS_Mixer_StreamAddChannel",
                Errors.Handle,
                "play failed",
                session: session)
        };

        AudioDeviceTestStreamObservation observation = AudioDeviceTestStreamObserver.Observe(
            "test.wav",
            boundary);

        Assert.IsFalse(observation.Succeeded);
        Assert.AreEqual(AudioDeviceTestFailureKind.PlaybackStartFailed, observation.FailureKind);
        Assert.AreEqual(BassAudioPlaybackStage.MixerAttach, observation.PlaybackStage);
        Assert.AreEqual("BASS_Mixer_StreamAddChannel", observation.NativeErrorSource);
        Assert.AreEqual(Errors.Handle, observation.NativeErrorCode);
        Assert.AreEqual(12, observation.PlaybackSourceHandle);
        Assert.AreEqual(77, observation.PlaybackExpectedMixerHandle);
        Assert.AreEqual(0, observation.PlaybackActualMixerHandle);
        Assert.AreEqual(BassAudioPlayer.DeviceDriver.WASAPI_SHARED, observation.PlaybackBackend);
        Assert.AreEqual(BassAudioSessionState.Active, observation.PlaybackSessionState);
        Assert.AreEqual(6, observation.PlaybackCoreDeviceIndex);
    }

    [TestMethod]
    public void AudioDeviceTestResult_PreservesTypedPlaybackDiagnostics()
    {
        AudioPlaybackInitializationResult initialization = CreateResult().Initialization;
        var result = new AudioDeviceTestResult(
            initialization,
            streamProgressRequired: true,
            streamProgressSucceeded: false,
            wallClockDuration: TimeSpan.Zero,
            playbackPositionDuration: TimeSpan.Zero,
            progressRatio: null,
            failureReason: "play failed",
            failureKind: AudioDeviceTestFailureKind.PlaybackStartFailed,
            playbackStage: BassAudioPlaybackStage.MixerAttach,
            nativeErrorSource: "BASS_Mixer_StreamAddChannel",
            nativeErrorCode: Errors.Handle,
            diagnosticReason: "play failed",
            playbackSourceHandle: 12,
            playbackExpectedMixerHandle: 77,
            playbackActualMixerHandle: 0,
            playbackBackend: BassAudioPlayer.DeviceDriver.WASAPI_SHARED,
            playbackSessionState: BassAudioSessionState.Active,
            playbackCoreDeviceIndex: 6);

        Assert.AreEqual(12, result.PlaybackSourceHandle);
        Assert.AreEqual(77, result.PlaybackExpectedMixerHandle);
        Assert.AreEqual(0, result.PlaybackActualMixerHandle);
        Assert.AreEqual(BassAudioPlayer.DeviceDriver.WASAPI_SHARED, result.PlaybackBackend);
        Assert.AreEqual(BassAudioSessionState.Active, result.PlaybackSessionState);
        Assert.AreEqual(6, result.PlaybackCoreDeviceIndex);
    }

    [TestMethod]
    public void StreamObserver_TypedObservationExceptionPreservesPlaybackDiagnostics()
    {
        var boundary = new FakeSoundBoundary(_ => TimeSpan.Zero)
        {
            PositionReadExceptionAfterPlay = new BassAudioPlaybackException(
                BassAudioPlaybackStage.SetPosition,
                "test.wav",
                12,
                77,
                77,
                "BASS_ChannelSetPosition",
                Errors.Handle,
                "observation failed")
        };

        AudioDeviceTestStreamObservation observation = AudioDeviceTestStreamObserver.Observe(
            "test.wav",
            boundary);

        Assert.IsFalse(observation.Succeeded);
        Assert.AreEqual(AudioDeviceTestFailureKind.Unexpected, observation.FailureKind);
        Assert.AreEqual(BassAudioPlaybackStage.SetPosition, observation.PlaybackStage);
        Assert.AreEqual("BASS_ChannelSetPosition", observation.NativeErrorSource);
        Assert.AreEqual(Errors.Handle, observation.NativeErrorCode);
    }

    [TestMethod]
    public void StreamObserver_PlayerCreationFailureIsReportedBeforePlayback()
    {
        var boundary = new FakeSoundBoundary(_ => TimeSpan.Zero)
        {
            CreateException = new BassAudioPlaybackException(
                BassAudioPlaybackStage.SourceCreate,
                "test.wav",
                0,
                77,
                0,
                "BASS_StreamCreateFile",
                Errors.FileOpen,
                "create failed")
        };

        AudioDeviceTestStreamObservation observation = AudioDeviceTestStreamObserver.Observe(
            "test.wav",
            boundary);

        Assert.IsFalse(observation.Succeeded);
        Assert.AreEqual(AudioDeviceTestFailureKind.PlayerCreationFailed, observation.FailureKind);
        Assert.AreEqual(BassAudioPlaybackStage.SourceCreate, observation.PlaybackStage);
        Assert.AreEqual(Errors.FileOpen, observation.NativeErrorCode);
        Assert.AreEqual(0, boundary.CreatedPlayerCount);
    }

    [TestMethod]
    public void StreamObserver_MissingTestSound_ReturnsFailureWithoutCreatingPlayer()
    {
        var boundary = new FakeSoundBoundary(_ => TimeSpan.Zero)
        {
            FileAvailable = false
        };

        AudioDeviceTestStreamObservation observation = AudioDeviceTestStreamObserver.Observe(
            "missing.wav",
            boundary);

        Assert.IsFalse(observation.Succeeded);
        StringAssert.Contains(observation.FailureReason, "unavailable");
        Assert.IsFalse(boundary.PlayerCreated);
    }

    [TestMethod]
    public void StreamObserver_PositionReversal_ReturnsFailure()
    {
        var boundary = new FakeSoundBoundary(elapsed =>
            elapsed <= TimeSpan.FromMilliseconds(100) ? elapsed : TimeSpan.Zero);

        AudioDeviceTestStreamObservation observation = AudioDeviceTestStreamObserver.Observe(
            "test.wav",
            boundary);

        Assert.IsFalse(observation.Succeeded);
        StringAssert.Contains(observation.FailureReason, "moved backwards");
    }

    [TestMethod]
    public void StreamObserver_ValidatedProgressThenPlateau_ReturnsFailureAndDisposesPlayer()
    {
        TimeSpan plateauPosition = TimeSpan.FromSeconds(1.2);
        var boundary = new FakeSoundBoundary(elapsed => elapsed <= plateauPosition
            ? elapsed
            : plateauPosition)
        {
            Duration = TimeSpan.FromSeconds(3),
            StateProvider = _ => PlayState.Playing
        };

        AudioDeviceTestStreamObservation observation = AudioDeviceTestStreamObserver.Observe(
            "test.wav",
            boundary);

        Assert.IsFalse(observation.Succeeded);
        StringAssert.Contains(observation.FailureReason, "stopped advancing");
        Assert.IsTrue(boundary.Elapsed < boundary.Duration);
        Assert.AreEqual(boundary.Elapsed, boundary.DisposedAt.GetValueOrDefault());
    }

    [TestMethod]
    public void StreamObserver_EarlyStop_ReturnsFailure()
    {
        var boundary = new FakeSoundBoundary(elapsed => elapsed)
        {
            StateProvider = elapsed => elapsed >= TimeSpan.FromMilliseconds(200)
                ? PlayState.Stopped
                : PlayState.Playing
        };

        AudioDeviceTestStreamObservation observation = AudioDeviceTestStreamObserver.Observe(
            "test.wav",
            boundary);

        Assert.IsFalse(observation.Succeeded);
        StringAssert.Contains(observation.FailureReason, "reported duration");
    }

    [TestMethod]
    public void StreamObserver_EarlyStopAfterValidatedProgress_ReturnsFailure()
    {
        var boundary = new FakeSoundBoundary(elapsed => elapsed)
        {
            Duration = TimeSpan.FromSeconds(3),
            StateProvider = elapsed => elapsed >= TimeSpan.FromSeconds(1.5)
                ? PlayState.Stopped
                : PlayState.Playing
        };

        AudioDeviceTestStreamObservation observation = AudioDeviceTestStreamObserver.Observe(
            "test.wav",
            boundary);

        Assert.IsFalse(observation.Succeeded);
        StringAssert.Contains(observation.FailureReason, "reported duration");
    }

    [DataTestMethod]
    [DataRow(0.75, true)]
    [DataRow(1.25, true)]
    [DataRow(0.74, false)]
    [DataRow(1.26, false)]
    public void StreamObserver_ProgressRatioBounds_AreInclusive(double ratio, bool expectedSuccess)
    {
        var boundary = new FakeSoundBoundary(elapsed =>
            TimeSpan.FromTicks((long)(elapsed.Ticks * ratio)));

        AudioDeviceTestStreamObservation observation = AudioDeviceTestStreamObserver.Observe(
            "test.wav",
            boundary);

        Assert.AreEqual(expectedSuccess, observation.Succeeded);
        Assert.AreEqual(ratio, observation.ProgressRatio.GetValueOrDefault(), 0.001);
    }

    private static AudioDeviceTestRequest CreateRequest()
    {
        return new AudioDeviceTestRequest(
            AudioDriver.WasapiShared,
            "driver",
            "Device",
            SampleRate.AUTO,
            SampleFormat.AUTO,
            10,
            false,
            50,
            playSound: false);
    }

    private static AudioDeviceTestResult CreateResult(
        AudioDriver driver = AudioDriver.WasapiShared)
    {
        AudioDeviceTestRequest request = CreateRequest();
        return AudioDeviceTestResultFactory.CreateSuccessful(
            request,
            actualBackend: driver,
            actualDevice: "new-driver",
            actualDeviceName: "New Device",
            actualRate: SampleRate.SAMPLE_RATE_44100Hz,
            engineFormat: SampleFormat.SAMPLE_INT_16BIT,
            latency: 12.5);
    }

    private sealed class DelegateAudioDeviceTestPlaybackPort : IAudioDeviceTestPlaybackPort
    {
        private readonly Action stopPlayback;

        internal DelegateAudioDeviceTestPlaybackPort(Action stopPlayback)
        {
            this.stopPlayback = stopPlayback;
        }

        public void StopPlayback()
        {
            stopPlayback();
        }
    }

    private sealed class RecordingAudioDeviceTestRuntime : IAudioDeviceTestRuntime
    {
        private readonly Func<AudioDeviceTestResult> run;

        internal RecordingAudioDeviceTestRuntime(Func<AudioDeviceTestResult> run)
        {
            this.run = run;
        }

        internal int CallCount { get; private set; }

        public AudioDeviceTestResult Run(AudioDeviceTestRequest request)
        {
            CallCount++;
            return run();
        }
    }

    private sealed class FakeSoundBoundary : IAudioDeviceTestSoundBoundary
    {
        private readonly Func<TimeSpan, TimeSpan> positionProvider;

        private long timestamp = 1;

        private int positionReadOrdinal;

        internal FakeSoundBoundary(Func<TimeSpan, TimeSpan> positionProvider)
        {
            this.positionProvider = positionProvider;
        }

        internal TimeSpan Elapsed => TimeSpan.FromTicks(timestamp - 1);

        internal Exception? PlayException { get; set; }

        internal Exception? CreateException { get; set; }

        internal Exception? PositionReadExceptionAfterPlay { get; set; }

        internal bool FileAvailable { get; set; } = true;

        internal bool PlayerCreated { get; private set; }

        internal int CreatedPlayerCount { get; private set; }

        internal int PlayCount { get; private set; }

        internal int DisposedPlayerCount { get; private set; }

        internal Func<TimeSpan, int, TimeSpan>? PositionReadProvider { get; set; }

        internal TimeSpan Duration { get; set; } = TimeSpan.FromSeconds(2);

        internal TimeSpan? DisposedAt { get; private set; }

        internal Func<TimeSpan, PlayState>? StateProvider { get; set; }

        public bool FileExists(string path) => FileAvailable;

        public IAudioPlayer CreatePlayer(string path)
        {
            if (CreateException is Exception createException)
            {
                throw createException;
            }
            PlayerCreated = true;
            CreatedPlayerCount++;
            positionReadOrdinal = 0;
            return new FakeAudioPlayer(
                ReadPosition,
                () =>
                {
                    TimeSpan statePosition = ReadPosition();
                    return StateProvider?.Invoke(Elapsed) ?? (statePosition >= Duration
                        ? PlayState.Stopped
                        : PlayState.Playing);
                },
                () => PlayException,
                () => Duration,
                () => PlayCount++,
                () =>
                {
                    DisposedPlayerCount++;
                    DisposedAt = Elapsed;
                });
        }

        public long GetTimestamp() => timestamp;

        public TimeSpan GetElapsedTime(long startTimestamp, long endTimestamp)
            => TimeSpan.FromTicks(endTimestamp - startTimestamp);

        public void Wait(TimeSpan interval)
        {
            timestamp += interval.Ticks;
            positionReadOrdinal = 0;
        }

        private TimeSpan ReadPosition()
        {
            if (PlayCount > 0 && PositionReadExceptionAfterPlay is Exception exception)
            {
                throw exception;
            }
            int readOrdinal = positionReadOrdinal++;
            return PositionReadProvider?.Invoke(Elapsed, readOrdinal) ?? positionProvider(Elapsed);
        }
    }

    private sealed class FakeAudioPlayer : IAudioPlayer
    {
        private readonly Func<TimeSpan> currentTime;

        private readonly Func<PlayState> playState;

        private readonly Func<Exception?> playException;

        private readonly Func<TimeSpan> duration;

        private readonly Action played;

        private readonly Action disposed;

        internal FakeAudioPlayer(
            Func<TimeSpan> currentTime,
            Func<PlayState> playState,
            Func<Exception?> playException,
            Func<TimeSpan> duration,
            Action played,
            Action disposed)
        {
            this.currentTime = currentTime;
            this.playState = playState;
            this.playException = playException;
            this.duration = duration;
            this.played = played;
            this.disposed = disposed;
        }

        public bool CanSeek => true;

        public TimeSpan CurrentTime
        {
            get => currentTime();
            set { }
        }

        public TimeSpan Duration => duration();

        public PlayState PlayState => playState();

        public float Volume { get; set; }

        public string FileName => "test.wav";

        public bool IsMuted { get; set; }

        public float PlaybackRate { get; set; }

        public void Pause()
        {
        }

        public void Play(PlayWith with = PlayWith.RESTART)
        {
            played();
            if (playException() is Exception exception)
            {
                throw exception;
            }
        }

        public void Stop()
        {
        }

        public void Dispose()
        {
            disposed();
        }
    }

}
