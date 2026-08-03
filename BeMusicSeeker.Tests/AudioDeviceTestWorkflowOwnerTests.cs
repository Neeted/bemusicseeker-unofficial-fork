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
        var boundary = new FakeSoundBoundary(_ => TimeSpan.Zero);

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
    }

    [TestMethod]
    public void StreamObserver_DoubleSpeedProgress_ReturnsFailure()
    {
        var boundary = new FakeSoundBoundary(elapsed => elapsed + elapsed);

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
        StringAssert.Contains(observation.FailureReason, "ended before");
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

        internal FakeSoundBoundary(Func<TimeSpan, TimeSpan> positionProvider)
        {
            this.positionProvider = positionProvider;
        }

        internal TimeSpan Elapsed => TimeSpan.FromTicks(timestamp - 1);

        internal Exception? PlayException { get; set; }

        internal bool FileAvailable { get; set; } = true;

        internal bool PlayerCreated { get; private set; }

        internal Func<TimeSpan, PlayState> StateProvider { get; set; }
            = _ => PlayState.Playing;

        public bool FileExists(string path) => FileAvailable;

        public IAudioPlayer CreatePlayer(string path)
        {
            PlayerCreated = true;
            return new FakeAudioPlayer(
                () => positionProvider(Elapsed),
                () => StateProvider(Elapsed),
                () => PlayException);
        }

        public long GetTimestamp() => timestamp;

        public TimeSpan GetElapsedTime(long startTimestamp, long endTimestamp)
            => TimeSpan.FromTicks(endTimestamp - startTimestamp);

        public void Wait(TimeSpan interval)
        {
            timestamp += interval.Ticks;
        }
    }

    private sealed class FakeAudioPlayer : IAudioPlayer
    {
        private readonly Func<TimeSpan> currentTime;

        private readonly Func<PlayState> playState;

        private readonly Func<Exception?> playException;

        internal FakeAudioPlayer(
            Func<TimeSpan> currentTime,
            Func<PlayState> playState,
            Func<Exception?> playException)
        {
            this.currentTime = currentTime;
            this.playState = playState;
            this.playException = playException;
        }

        public bool CanSeek => true;

        public TimeSpan CurrentTime
        {
            get => currentTime();
            set { }
        }

        public TimeSpan Duration => TimeSpan.FromSeconds(30);

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
        }
    }

}
