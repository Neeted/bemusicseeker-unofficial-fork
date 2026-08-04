using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using BeMusicSeeker.Models;
using BeMusicSeeker.ViewModels;
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
        Assert.IsTrue(observation.SessionDuration >= TimeSpan.FromSeconds(8));
        Assert.IsTrue(boundary.Elapsed >= TimeSpan.FromSeconds(8));
        Assert.AreEqual(4, boundary.CreatedPlayerCount);
        Assert.AreEqual(4, boundary.PlayCount);
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
        Assert.IsTrue(observation.SessionDuration >= TimeSpan.FromSeconds(8));
        Assert.AreEqual(3, boundary.CreatedPlayerCount);
        Assert.AreEqual(TimeSpan.FromSeconds(9), boundary.DisposedAt.GetValueOrDefault());
    }

    [TestMethod]
    public void StreamObserver_SecondPlaybackEarlyStop_FailsOverallObservation()
    {
        var boundary = new FakeSoundBoundary(elapsed => elapsed)
        {
            PlaybackStateProvider = (playbackNumber, elapsed) =>
                playbackNumber == 2 && elapsed >= TimeSpan.FromMilliseconds(200)
                    ? PlayState.Stopped
                    : elapsed >= TimeSpan.FromSeconds(2)
                        ? PlayState.Stopped
                        : PlayState.Playing
        };

        AudioDeviceTestStreamObservation observation = AudioDeviceTestStreamObserver.Observe(
            "test.wav",
            boundary);

        Assert.IsFalse(observation.Succeeded);
        StringAssert.Contains(observation.FailureReason, "reported duration");
        Assert.AreEqual(2, boundary.CreatedPlayerCount);
        Assert.AreEqual(2, boundary.DisposedPlayerCount);
        Assert.AreEqual(boundary.Elapsed, observation.SessionDuration);
    }

    [TestMethod]
    public void StreamObserver_SecondPlaybackException_IsNotSwallowedAndDisposesPlayers()
    {
        var boundary = new FakeSoundBoundary(elapsed => elapsed)
        {
            PlaybackPlayExceptionProvider = playbackNumber => playbackNumber == 2
                ? new InvalidOperationException("second playback failed")
                : null
        };

        InvalidOperationException exception = Assert.ThrowsException<InvalidOperationException>(
            () => AudioDeviceTestStreamObserver.Observe("test.wav", boundary));

        Assert.AreEqual("second playback failed", exception.Message);
        Assert.AreEqual(2, boundary.CreatedPlayerCount);
        Assert.AreEqual(2, boundary.DisposedPlayerCount);
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
        Assert.IsTrue(observation.SessionDuration >= TimeSpan.FromSeconds(8));
        Assert.AreEqual(4, boundary.CreatedPlayerCount);
        Assert.AreEqual(TimeSpan.FromSeconds(8), boundary.DisposedAt.GetValueOrDefault());
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
    public void StreamObserver_PlayerException_IsNotSwallowed()
    {
        var boundary = new FakeSoundBoundary(_ => TimeSpan.Zero)
        {
            PlayException = new InvalidOperationException("play failed")
        };

        InvalidOperationException exception = Assert.ThrowsException<InvalidOperationException>(
            () => AudioDeviceTestStreamObserver.Observe("test.wav", boundary));

        Assert.AreEqual("play failed", exception.Message);
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
            AudioDriver.DirectSound,
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
        AudioDriver driver = AudioDriver.DirectSound)
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

        private TimeSpan playerStart;

        internal FakeSoundBoundary(Func<TimeSpan, TimeSpan> positionProvider)
        {
            this.positionProvider = positionProvider;
        }

        internal TimeSpan Elapsed => TimeSpan.FromTicks(timestamp - 1);

        internal Exception? PlayException { get; set; }

        internal bool FileAvailable { get; set; } = true;

        internal bool PlayerCreated { get; private set; }

        internal int CreatedPlayerCount { get; private set; }

        internal int PlayCount { get; private set; }

        internal int DisposedPlayerCount { get; private set; }

        internal Func<TimeSpan, int, TimeSpan>? PositionReadProvider { get; set; }

        internal TimeSpan Duration { get; set; } = TimeSpan.FromSeconds(2);

        internal TimeSpan? DisposedAt { get; private set; }

        internal Func<TimeSpan, PlayState>? StateProvider { get; set; }

        internal Func<int, TimeSpan, PlayState>? PlaybackStateProvider { get; set; }

        internal Func<int, Exception?>? PlaybackPlayExceptionProvider { get; set; }

        public bool FileExists(string path) => FileAvailable;

        public IAudioPlayer CreatePlayer(string path)
        {
            PlayerCreated = true;
            CreatedPlayerCount++;
            int playbackNumber = CreatedPlayerCount;
            playerStart = Elapsed;
            positionReadOrdinal = 0;
            return new FakeAudioPlayer(
                ReadPosition,
                () =>
                {
                    TimeSpan statePosition = ReadPosition();
                    return PlaybackStateProvider?.Invoke(playbackNumber, PlayerElapsed)
                        ?? StateProvider?.Invoke(PlayerElapsed)
                        ?? (statePosition >= Duration
                            ? PlayState.Stopped
                            : PlayState.Playing);
                },
                () => PlaybackPlayExceptionProvider?.Invoke(playbackNumber) ?? PlayException,
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
            int readOrdinal = positionReadOrdinal++;
            return PositionReadProvider?.Invoke(PlayerElapsed, readOrdinal) ?? positionProvider(PlayerElapsed);
        }

        private TimeSpan PlayerElapsed => Elapsed - playerStart;
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
