using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using BeMusicSeeker.Models;
using BeMusicSeeker.Properties;
using BeMusicSeeker.ViewModels;
using ManagedBass;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Ribbit.Media;
using Ribbit.Media.Audio;

namespace BeMusicSeeker.Tests;

/// <summary>音声テストの受付・解放と、再生進捗から成功／失敗を決める処理を検証します。</summary>
/// <remarks>受付ケースは共有 BassAudioRuntime の排他が必要です。進捗判断は仮想時計と player 境界で独立して観測し、native 機器取得は下位 runtime テストへ分担します。</remarks>
[TestClass]
public sealed class AudioDeviceTestWorkflowOwnerTests
{
    /// <summary>保存側と候補側の既定機器null/空表現が違っても同義として扱い、実保存や保存失敗の再試行を発生させません。</summary>
    [DataTestMethod]
    [DataRow(null, "")]
    [DataRow("", null)]
    public void OutputSelection_DefaultRepresentationsDoNotSaveOrRequireRetry(string? saved, string? candidate)
    {
        string root = Path.Combine(Path.GetTempPath(), "AudioSaveMeaning-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        string path = Path.Combine(root, "user.config");
        try
        {
            Settings values = PortableSettingsPersistenceTests.OpenSettings(path);
            values.PlayerDriver = BassAudioMapping.ToBassDriver(AudioDriver.WasapiShared);
            values.PlayerDevice = saved;
            values.PlayerDeviceName = saved;
            values.Save();
            using AudioDeviceTestWorkflowOwner workflow = AudioDeviceTestWorkflowTestFactory.Create();
            workflow.ConfigurePresentation(() => values, new TestAudioDeviceCatalog());
            workflow.OutputSelection = new AudioOutputSelection(AudioDriver.WasapiShared, candidate, candidate);
            using (File.Open(path, FileMode.Open, FileAccess.Read, FileShare.Read))
            {
                Assert.IsFalse(workflow.HasOutputSettingsChanges);
                workflow.CommitOutputSettings();
                Assert.IsFalse(workflow.IsOutputSettingsSaveRetryRequired);
            }
        }
        finally { Directory.Delete(root, recursive: true); }
    }

    /// <summary>実行中照会へ取消を届け、Task 終端後の解放通知と次照会の受付を確認して、取消だけで受付を占有し続ける誤りを検出します。</summary>
    [TestMethod]
    [DoNotParallelize]
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
            await TestUiDispatcherHost.AwaitNotificationAsync(started.Task, first, "audio-device.query-start");
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

    /// <summary>共有音声要求が受付中ならテスト・照会・初期化を開始せず、解放後の明示テストを受理することを確認します。</summary>
    [TestMethod]
    [DoNotParallelize]
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
            await TestUiDispatcherHost.AwaitNotificationAsync(accepted.Task, blocker, "audio-device.request-blocker");
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

    /// <summary>player 解放が失敗しても成功した進捗や先行する再生失敗を置き換えず、解放例外を別に保持して一度だけ解放を試すことを確認します。</summary>
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

    /// <summary>runtime を gate で保持して停止→テストの順序と重複拒否を観測し、元 Task 完了後だけ次の明示要求を受けることを確認します。</summary>
    [TestMethod]
    [DoNotParallelize]
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
        using var owner = new AudioDeviceTestWorkflowOwner(
            new DelegateAudioDeviceTestPlaybackPort(() => events.Add("stop")),
            runtime);
        AudioDeviceTestRequest request = CreateRequest();

        Task<AudioDeviceTestResult> firstTask = owner.TryRunAsync(request);

        try
        {
            await TestUiDispatcherHost.AwaitNotificationAsync(runtimeStarted.Task, firstTask, "audio-device.runtime-start");
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

    /// <summary>共有 WASAPI の成功と初期化失敗で有効要求を AUTO／自動 buffer として報告し、利用者の保存要求を変更しないことを確認します。</summary>
    [TestMethod]
    [DoNotParallelize]
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
        using var owner = new AudioDeviceTestWorkflowOwner(
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

        // 初期化の成否によらず共有出力要求の正規化を表示する。
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
    }

    /// <summary>runtime の例外を失敗結果へ保持し、Task 終端で受付を解放して同じ owner の次要求を成功させることを確認します。</summary>
    [TestMethod]
    [DoNotParallelize]
    public async Task TryRunAsync_RuntimeFailureReleasesBusyState()
    {
        int attempts = 0;
        using var owner = new AudioDeviceTestWorkflowOwner(
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

    /// <summary>自然終了しない player の観測 timeout 後に player と受付を一度解放し、同じ owner の再テストを妨げないことを確認します。</summary>
    [TestMethod]
    [DoNotParallelize]
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

    /// <summary>照会を gate で保持した間は照会とテストの重複を拒否し、通常再生を停止せず照会結果と受付を返すことを確認します。</summary>
    [TestMethod]
    [DoNotParallelize]
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
        using var owner = new AudioDeviceTestWorkflowOwner(
            new DelegateAudioDeviceTestPlaybackPort(() => stopCount++),
            new RecordingAudioDeviceTestRuntime(() => CreateResult()),
            capabilityRuntime);
        AudioDeviceCapabilityRequest request = CreateCapabilityRequest();

        Task<AudioDeviceCapabilityResult> queryTask = owner.TryQueryCapabilitiesAsync(request);
        try
        {
            await TestUiDispatcherHost.AwaitNotificationAsync(queryStarted.Task, queryTask, "audio-device.capability-start");
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

    /// <summary>同じ owner で照会とテストを交互に繰り返し、照会では停止せず各テストで停止することを確認して受付の再使用漏れを検出します。</summary>
    [TestMethod]
    [DoNotParallelize]
    public async Task CapabilityQueryAndDeviceTestCanRepeatThroughOneWorkflowOwner()
    {
        int queryCount = 0;
        int testCount = 0;
        int stopCount = 0;
        using var owner = new AudioDeviceTestWorkflowOwner(
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

    /// <summary>再生中のまま位置が進まない入力を観測期限で失敗にし、位置ゼロを正常再生と誤認しないことを確認します。</summary>
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

    /// <summary>時計と等速で進む位置を測定して成功と速度比を返し、自然終了時に作成した player を一度解放することを確認します。</summary>
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

    /// <summary>必要な進捗を得た三秒音声でも自然終了まで player を所有し、進捗検証直後に再生を打ち切る誤りを検出します。</summary>
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

    /// <summary>位置取得と再生状態取得の間に自然終了した入力を再観測して成功にし、読み取り順による正常終了の誤判定を検出します。</summary>
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

    /// <summary>全位置を進んでも再生状態が終了しない入力は自然終了待ちで失敗にし、期限到達時に player を解放することを確認します。</summary>
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

    /// <summary>時計の二倍で進む位置は速度比二として失敗にし、位置が進むだけで正常再生と判断する誤りを検出します。</summary>
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

    /// <summary>一般例外で再生開始に失敗した入力を Unexpected と診断し、元の失敗内容を失わないことを確認します。</summary>
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

    /// <summary>MixerAttach の型付き再生例外を開始失敗へ分類し、native エラー・handle・backend・session の診断を保持することを確認します。</summary>
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

    /// <summary>開始後の位置観測で起きた型付き例外は Unexpected として、stage と native エラーを保持し開始失敗と混同しないことを確認します。</summary>
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

    /// <summary>音声 source 作成例外を player 作成失敗として報告し、player が未作成であることと stage／native エラーを確認します。</summary>
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

    /// <summary>テスト音声が存在しなければ利用不能として失敗を返し、player 作成へ進まないことを確認します。</summary>
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

    /// <summary>途中で位置がゼロへ戻る入力を逆行として拒否し、時計経過だけで正常進捗と判断する誤りを検出します。</summary>
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

    /// <summary>有効な進捗の後で位置が停止する入力も失敗にし、音声長を待ち切る前に player を解放することを確認します。</summary>
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

    /// <summary>検証に必要な進捗を得る前に停止する音声を、報告長に達しない失敗として扱うことを確認します。</summary>
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

    /// <summary>進捗が検証できても音声長の途中で停止すれば失敗にし、検証済みだけを理由に早期停止を成功と扱わないことを確認します。</summary>
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

    /// <summary>許容速度比の下限・上限は成功、その直外は失敗にし、境界の不等号や測定比の誤りを検出します。</summary>
    [DataTestMethod]
    // 許容下限・上限と、その直外を比較する。
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

        public Task StopPlayback()
        {
            stopPlayback();
            return Task.CompletedTask;
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
