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
    public async Task CapabilityQuery_CancellationReachesRuntimeAndReleasesAdmission()
    {
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        int calls = 0;
        int released = 0;
        using var owner = new AudioDeviceTestWorkflowOwner(
            new DelegateAudioDeviceTestPlaybackPort(() => Assert.Fail("Query must not stop playback.")),
            new RecordingAudioDeviceTestRuntime(() => CreateResult()),
            new DelegateAudioDeviceCapabilityRuntime((request, token) =>
            {
                if (Interlocked.Increment(ref calls) == 1)
                {
                    started.TrySetResult();
                    token.WaitHandle.WaitOne();
                    token.ThrowIfCancellationRequested();
                }
                return new AudioDeviceCapabilityResult(request.Backend, request.DeviceIdentity,
                    request.DeviceName, AudioDeviceCapabilityStatus.Available);
            }));
        owner.OperationReleased += () => released++;
        Task<AudioDeviceCapabilityResult> first = owner.TryQueryCapabilitiesAsync(CreateCapabilityRequest());
        try
        {
            await started.Task;
            Assert.IsTrue(owner.IsRunning);
            owner.CancelCurrentQuery();
            await Assert.ThrowsExceptionAsync<OperationCanceledException>(async () => await first);
        }
        finally
        {
            owner.CancelCurrentQuery();
            try { await first; } catch (OperationCanceledException) { }
        }
        Assert.IsFalse(owner.IsRunning);
        Assert.AreEqual(1, released);
        Assert.AreEqual(AudioDeviceCapabilityStatus.Available,
            (await owner.TryQueryCapabilitiesAsync(CreateCapabilityRequest())).Status);
        Assert.AreEqual(2, calls);
        Assert.AreEqual(2, released);
    }

    [TestMethod]
    public async Task ProcessAudioRequestBusy_RejectsTestAndQueryBeforeStoppingOrInitializing()
    {
        var accepted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var blocker = Task.Run(async () =>
        {
            Assert.IsTrue(BassAudioRuntime.TryEnterAudioRequest(out IDisposable admission));
            using (admission)
            {
                accepted.TrySetResult();
                await release.Task;
            }
        });
        int stops = 0;
        var runtime = new RecordingAudioDeviceTestRuntime(() => CreateResult());
        using var owner = new AudioDeviceTestWorkflowOwner(
            new DelegateAudioDeviceTestPlaybackPort(() => stops++), runtime);
        try
        {
            await accepted.Task;
            Assert.IsNull(await owner.TryRunAsync(CreateRequest()));
            Assert.AreEqual(0, stops);
            Assert.AreEqual(0, runtime.CallCount);
            AudioDeviceCapabilityResult query = BassAudioPlayer.QueryAudioDeviceCapabilities(CreateCapabilityRequest());
            Assert.AreEqual(AudioDeviceCapabilityStatus.Busy, query.Status);
            AudioDeviceTestRequest request = CreateRequest();
            AudioInitializationException busy = Assert.ThrowsException<AudioInitializationException>(() =>
                BassAudioPlayer.InitializeOwned(request.AudioOutputRequest, 50, out _));
            Assert.AreEqual("audio request busy", busy.Stage);
            Assert.AreEqual("BassAudioOperationGate", busy.NativeErrorSource);
            Assert.AreEqual(BassAudioPlayer.DeviceDriver.WASAPI_SHARED, busy.RequestedBackend);
            Assert.AreEqual("driver", busy.RequestedDevice.Driver);
            Assert.AreEqual("Device", busy.RequestedDevice.Name);
        }
        finally
        {
            release.TrySetResult();
            await blocker;
        }
        Assert.IsNotNull(await owner.TryRunAsync(CreateRequest()));
        Assert.AreEqual(1, stops);
        Assert.AreEqual(1, runtime.CallCount);
    }

    [TestMethod]
    public void StreamObserver_CleanupFailurePreservesEarlierFailureAndProgress()
    {
        var cleanup = new InvalidOperationException("source cleanup failed");
        var successful = new FakeSoundBoundary(elapsed => elapsed) { DisposeException = cleanup };
        AudioDeviceTestStreamObservation progress = AudioDeviceTestStreamObserver.Observe("test.wav", successful);
        Assert.IsTrue(progress.Succeeded);
        Assert.IsTrue(progress.WallClockDuration >= TimeSpan.FromSeconds(1));
        Assert.AreSame(cleanup, progress.CleanupFailure);
        Assert.AreEqual(1, successful.DisposedPlayerCount);

        var failed = new FakeSoundBoundary(elapsed => elapsed)
        {
            PlayException = new InvalidOperationException("play failed"),
            DisposeException = cleanup
        };
        AudioDeviceTestStreamObservation failure = AudioDeviceTestStreamObserver.Observe("test.wav", failed);
        Assert.IsFalse(failure.Succeeded);
        StringAssert.Contains(failure.DiagnosticReason, "play failed");
        Assert.AreSame(cleanup, failure.CleanupFailure);
        Assert.AreEqual(1, failed.DisposedPlayerCount);
    }

    [TestMethod]
    public async Task TryRunAsync_StopsPlaybackBeforeRuntimeAndRejectsDuplicate()
    {
        var events = new List<string>();
        var runtimeStarted = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseRuntime = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var runtime = new RecordingAudioDeviceTestRuntime(() =>
        {
            events.Add("runtime");
            runtimeStarted.TrySetResult();
            releaseRuntime.Task.GetAwaiter().GetResult();
            return CreateResult();
        });
        var owner = new AudioDeviceTestWorkflowOwner(
            new DelegateAudioDeviceTestPlaybackPort(() => events.Add("stop")),
            runtime);
        AudioDeviceTestRequest request = CreateRequest();

        Task<AudioDeviceTestResult> firstTask = owner.TryRunAsync(request);

        try
        {
            await runtimeStarted.Task;
            Assert.IsTrue(owner.IsRunning);
            Assert.IsNull(await owner.TryRunAsync(request));

            releaseRuntime.TrySetResult();
            Assert.IsNotNull(await firstTask);
            Assert.IsFalse(owner.IsRunning);
            CollectionAssert.AreEqual(new[] { "stop", "runtime" }, events);
            Assert.AreEqual(1, runtime.CallCount);

            AudioDeviceTestResult retry = await owner.TryRunAsync(request);
            Assert.IsTrue(retry.Succeeded);
            Assert.IsFalse(owner.IsRunning);
            CollectionAssert.AreEqual(new[] { "stop", "runtime", "stop", "runtime" }, events);
            Assert.AreEqual(2, runtime.CallCount);
        }
        finally
        {
            releaseRuntime.TrySetResult();
            await firstTask;
        }
    }

    [TestMethod]
    public async Task TryRunAsync_ResultKeepsSavedValuesButReportsEffectiveSharedRequest()
    {
        var request = new AudioDeviceTestRequest(
            AudioDriver.WasapiShared,
            "shared-device",
            "Shared device",
            SampleRate.SAMPLE_RATE_44100Hz,
            SampleFormat.SAMPLE_INT_16BIT,
            37,
            playerWASAPIParam: true,
            playerVolume: 50,
            playSound: false,
            playerMixerThreadCount: 3);
        var owner = new AudioDeviceTestWorkflowOwner(
            new DelegateAudioDeviceTestPlaybackPort(() => { }),
            new RecordingAudioDeviceTestRuntime(() => AudioDeviceTestResultFactory.CreateSuccessful(request)));

        AudioDeviceTestResult success = await owner.TryRunAsync(request);
        var failed = new AudioDeviceTestResult(
            request,
            initialization: null,
            streamProgressRequired: false,
            streamProgressSucceeded: false,
            TimeSpan.Zero,
            TimeSpan.Zero,
            progressRatio: null,
            failureReason: "initialization failed",
            failureKind: AudioDeviceTestFailureKind.Unexpected,
            primaryFailure: new InvalidOperationException("initialization failed"),
            cleanupFailure: new InvalidOperationException("cleanup failed"));

        foreach (AudioDeviceTestResult result in new[] { success, failed })
        {
            Assert.AreEqual(SampleRate.AUTO, result.RequestedRate);
            Assert.AreEqual(SampleFormat.AUTO, result.RequestedFormat);
            Assert.AreEqual(0f, result.RequestedBufferSize);
            Assert.IsTrue(result.RequestedEventMode);
        }
        Assert.AreEqual(SampleRate.SAMPLE_RATE_44100Hz, request.PlayerSampleRate);
        Assert.AreEqual(SampleFormat.SAMPLE_INT_16BIT, request.PlayerFormat);
        Assert.AreEqual(37f, request.PlayerBufferSize);
        Assert.IsTrue(request.PlayerWASAPIParam);
        Assert.AreEqual(3, request.PlayerMixerThreadCount);
        Assert.AreEqual(3, request.AudioOutputRequest.PlayerMixerThreadCount);
    }

    [TestMethod]
    public async Task TryRunAsync_RuntimeFailureReleasesBusyState()
    {
        int attempts = 0;
        var owner = new AudioDeviceTestWorkflowOwner(
            new DelegateAudioDeviceTestPlaybackPort(() => { }),
            new RecordingAudioDeviceTestRuntime(request =>
            {
                if (Interlocked.Increment(ref attempts) == 1)
                {
                    throw new InvalidOperationException("audio test failed");
                }
                return AudioDeviceTestResultFactory.CreateSuccessful(request);
            }));

        AudioDeviceTestResult result = await owner.TryRunAsync(CreateRequest());
        Assert.IsNotNull(result);
        Assert.IsFalse(result.Succeeded);
        Assert.IsInstanceOfType<InvalidOperationException>(result.PrimaryFailure);
        Assert.AreEqual("audio test failed", result.PrimaryFailure.Message);
        Assert.IsNotNull(result.Request);
        Assert.IsNull(result.Initialization);
        Assert.IsFalse(owner.IsRunning);
        AudioDeviceTestResult retry = await owner.TryRunAsync(CreateRequest());
        Assert.IsTrue(retry.Succeeded);
        Assert.AreEqual(2, attempts);
        Assert.IsFalse(owner.IsRunning);
    }

    [TestMethod]
    public async Task TryRunAsync_ObservationTimeoutReleasesPlayerAndAllowsSameOwnerRetry()
    {
        int attempts = 0;
        var timeoutBoundary = new FakeSoundBoundary(elapsed => elapsed)
        {
            Duration = TimeSpan.FromSeconds(2),
            StateProvider = _ => PlayState.Playing
        };
        var runtime = new RecordingAudioDeviceTestRuntime(request =>
        {
            if (Interlocked.Increment(ref attempts) > 1)
            {
                return AudioDeviceTestResultFactory.CreateSuccessful(request);
            }

            AudioDeviceTestStreamObservation observation = AudioDeviceTestStreamObserver.Observe(
                "test.wav",
                timeoutBoundary);
            return new AudioDeviceTestResult(
                request,
                initialization: null,
                streamProgressRequired: request.PlaySound,
                streamProgressSucceeded: observation.Succeeded,
                wallClockDuration: observation.WallClockDuration,
                playbackPositionDuration: observation.PlaybackPositionDuration,
                progressRatio: observation.ProgressRatio,
                failureReason: observation.FailureReason,
                failureKind: observation.FailureKind,
                cleanupFailure: observation.CleanupFailure);
        });
        using var owner = new AudioDeviceTestWorkflowOwner(
            new DelegateAudioDeviceTestPlaybackPort(() => { }),
            runtime);
        int releaseNotifications = 0;
        owner.OperationReleased += () => releaseNotifications++;

        AudioDeviceTestResult timeout = await owner.TryRunAsync(CreateRequest(playSound: true));

        Assert.AreEqual(AudioDeviceTestFailureKind.ObservationTimedOut, timeout.FailureKind);
        Assert.AreEqual(1, timeoutBoundary.DisposedPlayerCount);
        Assert.IsFalse(owner.IsRunning);
        Assert.AreEqual(1, releaseNotifications);

        AudioDeviceTestResult retry = await owner.TryRunAsync(CreateRequest(playSound: true));

        Assert.IsTrue(retry.Succeeded);
        Assert.IsFalse(owner.IsRunning);
        Assert.AreEqual(2, attempts);
        Assert.AreEqual(2, releaseNotifications);
    }

    [TestMethod]
    public async Task CapabilityQuery_SharesAcceptanceWithoutStoppingPlayback()
    {
        var queryStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseQuery = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        int stopCount = 0;
        int queryCount = 0;
        var capabilityRuntime = new DelegateAudioDeviceCapabilityRuntime(request =>
        {
            queryCount++;
            queryStarted.TrySetResult();
            releaseQuery.Task.GetAwaiter().GetResult();
            return new AudioDeviceCapabilityResult(
                request.Backend,
                request.DeviceIdentity,
                request.DeviceName,
                AudioDeviceCapabilityStatus.Available,
                [SampleRate.SAMPLE_RATE_48000Hz]);
        });
        var owner = new AudioDeviceTestWorkflowOwner(
            new DelegateAudioDeviceTestPlaybackPort(() => stopCount++),
            new RecordingAudioDeviceTestRuntime(() => CreateResult()),
            capabilityRuntime);
        AudioDeviceCapabilityRequest request = CreateCapabilityRequest();

        Task<AudioDeviceCapabilityResult> queryTask = owner.TryQueryCapabilitiesAsync(request);
        try
        {
            await queryStarted.Task;
            Assert.IsTrue(owner.IsRunning);
            Assert.IsNull(await owner.TryQueryCapabilitiesAsync(request));
            Assert.IsNull(await owner.TryRunAsync(CreateRequest()));
            Assert.AreEqual(0, stopCount);
            Assert.AreEqual(1, queryCount);

            releaseQuery.TrySetResult();
            AudioDeviceCapabilityResult result = await queryTask;
            Assert.AreEqual(AudioDeviceCapabilityStatus.Available, result.Status);
            Assert.IsFalse(owner.IsRunning);
        }
        finally
        {
            releaseQuery.TrySetResult();
            await queryTask;
        }
    }

    [TestMethod]
    public async Task CapabilityQueryAndDeviceTestCanRepeatThroughOneWorkflowOwner()
    {
        int queryCount = 0;
        int testCount = 0;
        int stopCount = 0;
        var owner = new AudioDeviceTestWorkflowOwner(
            new DelegateAudioDeviceTestPlaybackPort(() => stopCount++),
            new RecordingAudioDeviceTestRuntime(() =>
            {
                testCount++;
                return CreateResult();
            }),
            new DelegateAudioDeviceCapabilityRuntime(request =>
            {
                queryCount++;
                return new AudioDeviceCapabilityResult(
                    request.Backend,
                    request.DeviceIdentity,
                    request.DeviceName,
                    AudioDeviceCapabilityStatus.Available,
                    [SampleRate.SAMPLE_RATE_48000Hz]);
            }));

        for (int repetition = 0; repetition < 2; repetition++)
        {
            AudioDeviceCapabilityResult capability = await owner.TryQueryCapabilitiesAsync(
                CreateCapabilityRequest());
            Assert.AreEqual(AudioDeviceCapabilityStatus.Available, capability.Status);
            AudioDeviceTestResult test = await owner.TryRunAsync(CreateRequest());
            Assert.IsTrue(test.Succeeded);
            Assert.IsFalse(owner.IsRunning);
        }

        AudioDeviceTestResult repeatedTest = await owner.TryRunAsync(CreateRequest());

        Assert.IsTrue(repeatedTest.Succeeded);
        Assert.AreEqual(2, queryCount);
        Assert.AreEqual(3, testCount);
        Assert.AreEqual(3, stopCount);
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
        var duration = TimeSpan.FromSeconds(2);
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
        AudioPlaybackInitializationResult initialization = CreateResult().Initialization
            ?? throw new AssertFailedException("The successful test must retain its initialization.");
        var result = new AudioDeviceTestResult(
            CreateRequest(),
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
        var plateauPosition = TimeSpan.FromSeconds(1.2);
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

    private static AudioDeviceTestRequest CreateRequest(bool playSound = false)
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
            playSound: playSound);
    }

    private static AudioDeviceCapabilityRequest CreateCapabilityRequest()
        => new(
            AudioDriver.Asio,
            "asio-device",
            "ASIO Device",
            SampleRate.SAMPLE_RATE_48000Hz,
            SampleFormat.SAMPLE_INT_16BIT);

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
        private readonly Func<AudioDeviceTestRequest, AudioDeviceTestResult> run;

        internal RecordingAudioDeviceTestRuntime(Func<AudioDeviceTestResult> run)
        {
            this.run = _ => run();
        }

        internal RecordingAudioDeviceTestRuntime(Func<AudioDeviceTestRequest, AudioDeviceTestResult> run)
        {
            this.run = run;
        }

        internal int CallCount { get; private set; }

        public AudioDeviceTestResult Run(AudioDeviceTestRequest request)
        {
            CallCount++;
            return run(request);
        }
    }

    private sealed class DelegateAudioDeviceCapabilityRuntime : IAudioDeviceCapabilityRuntime
    {
        private readonly Func<AudioDeviceCapabilityRequest, CancellationToken, AudioDeviceCapabilityResult> query;

        internal DelegateAudioDeviceCapabilityRuntime(
            Func<AudioDeviceCapabilityRequest, AudioDeviceCapabilityResult> query)
            : this((request, _) => query(request))
        {
        }

        internal DelegateAudioDeviceCapabilityRuntime(
            Func<AudioDeviceCapabilityRequest, CancellationToken, AudioDeviceCapabilityResult> query)
        {
            this.query = query;
        }

        public AudioDeviceCapabilityResult Query(AudioDeviceCapabilityRequest request, CancellationToken cancellationToken)
            => query(request, cancellationToken);
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

        internal Exception? DisposeException { get; set; }

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
            var player = new FakeAudioPlayer(
                ReadPosition,
                () =>
                {
                    TimeSpan statePosition = ReadPosition();
                    return StateProvider?.Invoke(Elapsed) ?? (statePosition >= Duration
                        ? PlayState.Stopped
                        : PlayState.Playing);
                },
                () => PlayException,
                () =>
                {
                    return Duration;
                },
                () => PlayCount++,
                () =>
                {
                    DisposedPlayerCount++;
                    DisposedAt = Elapsed;
                    if (DisposeException != null)
                    {
                        throw DisposeException;
                    }
                });
            return player;
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
