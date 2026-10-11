using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
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
public sealed class BassAudioSessionTests
{
    /// <summary>照会と明示テストを反復し、部分初期化／cleanupの独立失敗を要求とともに保持して隔離し、明示回収後だけ再受付できる。</summary>
    [DataTestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public void DeviceTestRuntime_PartialFailureAndCleanupFailurePreserveRequestAndAllowExplicitRecovery(bool cleanupInInitialization)
    {
        var lifecycle = new BassAudioSessionLifecycle();
        var gate = new BassAudioOperationGate(initiallyOpen: true);
        var native = new RecordingNativeBoundary();
        bool failInitialization = false;
        int initializationCalls = 0;
        var primary = new InvalidOperationException("initialization failed after core acquisition");
        var request = new AudioDeviceTestRequest(AudioDriver.Asio, "device", "Device",
            SampleRate.AUTO, SampleFormat.SAMPLE_INT_16BIT, 10, false, 50, playSound: false);

        bool ReleaseSession(BassAudioSession session)
        {
            if (session.IsReleased)
            {
                return true;
            }
            if (!gate.TryEnterSessionCleanup(out BassAudioExclusiveLease exclusive))
            {
                return false;
            }
            using (exclusive)
            using (lifecycle.Enter())
            {
                bool released = BassAudioSessionCleanup.Release(session, native);
                lifecycle.CompleteCleanup(session);
                exclusive.Complete(released);
                return released;
            }
        }

        var runtime = new BassAudioDeviceTestRuntime(ApplicationPathPolicy.Current,
            new UnusedTestSoundBoundary(),
            (input, acquired) =>
            {
                initializationCalls++;
                using BassAudioExclusiveLease exclusive = gate.EnterSessionInitialization();
                exclusive.ObserveOwnership(() => !lifecycle.HasUnconfirmedOwnership);
                using (lifecycle.Enter())
                {
                    Assert.IsTrue(lifecycle.TryBegin(input.AudioOutputRequest, out BassAudioSession session));
                    acquired(session);
                    session.CoreInitialized = true;
                    session.CoreDeviceIndex = 0;
                    session.ActualBackend = BassAudioPlayer.DeviceDriver.ASIO;
                    session.AsioInitialized = true;
                    session.AsioDeviceIndex = 0;
                    session.ActualDevice = new BassAudioPlayer.DeviceDescriptor("Device", "device");
                    if (failInitialization)
                    {
                        if (cleanupInInitialization)
                        {
                            BassAudioSessionCleanup.Release(session, native, primary);
                            lifecycle.CompleteCleanup(session);
                        }
                        throw primary;
                    }
                    session.NegotiationResult = new BassAudioBackendResult(
                        new BassAudioNegotiationRequest(session.ActualBackend, session.ActualDevice,
                            SampleRate.AUTO, SampleFormat.AUTO, 10),
                        session.ActualDevice, SampleRate.SAMPLE_RATE_48000Hz,
                        SampleFormat.SAMPLE_FLOAT_32BIT, SampleFormat.SAMPLE_INT_24BIT, 12, 0, [], null);
                    lifecycle.MarkActive(session);
                }
            },
            ReleaseSession,
            gate);

        bool TryRunAdmitted(out AudioDeviceTestResult? result)
        {
            if (!gate.TryEnterRequest(out IDisposable admission))
            {
                result = null;
                return false;
            }
            using (admission)
            {
                result = runtime.Run(request);
                return true;
            }
        }

        void QueryCapabilities()
        {
            Assert.IsTrue(gate.TryEnterRequest(out IDisposable admission));
            using (admission)
            {
                using BassAudioExclusiveLease exclusive = gate.EnterSessionInitialization();
                exclusive.ObserveOwnership(() => !lifecycle.HasUnconfirmedOwnership);
                using (lifecycle.Enter())
                {
                    var query = new AudioDeviceCapabilityRequest(request.PlayerDriver, request.PlayerDevice,
                        request.PlayerDeviceName, SampleRate.AUTO, SampleFormat.AUTO);
                    AudioDeviceCapabilityResult result = BassAudioCapabilitySession.QueryAsio(query, lifecycle, native,
                        session =>
                        {
                            session.AsioInitialized = true;
                            session.AsioDeviceIndex = 0;
                            return new AudioDeviceCapabilityResult(query.Backend, query.DeviceIdentity, query.DeviceName,
                                AudioDeviceCapabilityStatus.Available, [SampleRate.SAMPLE_RATE_48000Hz]);
                        });
                    Assert.AreEqual(AudioDeviceCapabilityStatus.Available, result.Status);
                    Assert.IsNull(lifecycle.CurrentSession);
                }
            }
        }
        QueryCapabilities();
        Assert.IsTrue(TryRunAdmitted(out AudioDeviceTestResult? first));
        Assert.IsNotNull(first);
        Assert.IsTrue(first.Succeeded);
        Assert.IsNotNull(first.Initialization);
        Assert.IsFalse(first.Initialization.FallbackOccurred);
        Assert.AreEqual(SampleFormat.AUTO, first.Initialization.RequestedFormat);
        Assert.AreEqual(SampleFormat.SAMPLE_INT_16BIT, first.Request.PlayerFormat);
        Assert.IsNull(lifecycle.CurrentSession);
        Assert.IsFalse(gate.AdmissionClosed);
        QueryCapabilities();
        Assert.IsTrue(TryRunAdmitted(out AudioDeviceTestResult? second));
        Assert.IsNotNull(second);
        Assert.IsTrue(second.Succeeded);
        Assert.AreEqual(2, native.Count("FreeCore"));
        Assert.AreEqual(4, native.Count("FreeAsio"));
        Assert.IsTrue(TryRunAdmitted(out AudioDeviceTestResult? third));
        Assert.IsNotNull(third);
        Assert.IsTrue(third.Succeeded);
        Assert.IsNull(lifecycle.CurrentSession);

        native.FreeCoreResult = false;
        native.CoreError = Errors.Unknown;
        Assert.IsTrue(TryRunAdmitted(out AudioDeviceTestResult? cleanupFailed));
        Assert.IsNotNull(cleanupFailed);
        Assert.IsFalse(cleanupFailed.Succeeded);
        Assert.IsNotNull(cleanupFailed.Initialization);
        Assert.AreEqual(SampleRate.SAMPLE_RATE_48000Hz, cleanupFailed.ActualRate);
        Assert.AreEqual(12d, cleanupFailed.Latency);
        Assert.IsNotNull(cleanupFailed.CleanupFailure);
        Assert.IsNull(cleanupFailed.PrimaryFailure);
        Assert.IsTrue(lifecycle.HasCleanupPending);
        Assert.IsTrue(gate.AdmissionClosed);

        int rejectedRetryCoreSelections = native.Count("SetCoreDevice");
        int rejectedRetryCoreReleases = native.Count("FreeCore");
        int rejectedRetryInitializations = initializationCalls;
        BassAudioSession quarantinedSession = lifecycle.CurrentSession;
        native.FreeCoreResult = true;
        Assert.IsFalse(TryRunAdmitted(out _));
        Assert.AreEqual(rejectedRetryCoreSelections, native.Count("SetCoreDevice"));
        Assert.AreEqual(rejectedRetryCoreReleases, native.Count("FreeCore"));
        Assert.AreEqual(rejectedRetryInitializations, initializationCalls);
        Assert.AreSame(quarantinedSession, lifecycle.CurrentSession);
        Assert.IsTrue(lifecycle.HasCleanupPending);
        Assert.IsTrue(gate.AdmissionClosed);

        Assert.IsTrue(ReleaseSession(lifecycle.CurrentSession));
        Assert.IsNull(lifecycle.CurrentSession);
        Assert.IsFalse(gate.AdmissionClosed);
        Assert.IsTrue(TryRunAdmitted(out AudioDeviceTestResult? recovered));
        Assert.IsNotNull(recovered);
        Assert.IsTrue(recovered.Succeeded);
        failInitialization = true;
        native.FreeCoreResult = false;
        int previousCleanupCount = native.Count("FreeCore");
        Assert.IsTrue(TryRunAdmitted(out AudioDeviceTestResult? combined));
        Assert.IsNotNull(combined);
        Assert.AreEqual(previousCleanupCount + 1, native.Count("FreeCore"));
        Assert.AreSame(request, combined.Request);
        Assert.AreSame(primary, combined.PrimaryFailure);
        Assert.IsNotNull(combined.CleanupFailure);
        Assert.IsNull(combined.Initialization);
        Assert.IsNull(combined.ActualRate);
        Assert.IsNull(combined.Latency);
        Assert.IsTrue(lifecycle.HasCleanupPending);

        native.FreeCoreResult = true;
        failInitialization = false;
        Assert.IsTrue(ReleaseSession(lifecycle.CurrentSession));
        Assert.IsNull(lifecycle.CurrentSession);
        Assert.IsFalse(gate.AdmissionClosed);
        Assert.IsTrue(TryRunAdmitted(out AudioDeviceTestResult? finallyRecovered));
        Assert.IsNotNull(finallyRecovered);
        Assert.IsTrue(finallyRecovered.Succeeded);
        Assert.IsNull(lifecycle.CurrentSession);
        Assert.IsFalse(gate.AdmissionClosed);
    }

    private sealed class UnusedTestSoundBoundary : IAudioDeviceTestSoundBoundary
    {
        public bool FileExists(string path) => throw new AssertFailedException("Sound was not requested.");
        public IAudioPlayer CreatePlayer(string path) => throw new AssertFailedException("Sound was not requested.");
        public long GetTimestamp() => throw new AssertFailedException("Sound was not requested.");
        public TimeSpan GetElapsedTime(long start, long end) => throw new AssertFailedException("Sound was not requested.");
        public void Wait(TimeSpan interval) => throw new AssertFailedException("Sound was not requested.");
    }

    /// <summary>排他leaseの参照を複製しても完了とDisposeを一度だけ処理し、次の初期化受付を壊さない。</summary>
    [TestMethod]
    public void ExclusiveLease_CopiedReferenceSharesCompletionAndDisposesOnce()
    {
        var gate = new BassAudioOperationGate(initiallyOpen: true);
        BassAudioExclusiveLease lease = gate.EnterSessionInitialization();
        BassAudioExclusiveLease copied = lease;
        copied.ObserveOwnership(() => true);
        lease.Dispose();
        copied.Dispose();
        Assert.IsFalse(gate.AdmissionClosed);
        using BassAudioExclusiveLease next = gate.EnterSessionInitialization();
        next.ObserveOwnership(() => true);
    }

    /// <summary>callback故障と過大レベルの最初の値をsessionごとに一度だけ記録・回収し、後続値で原因を上書きしない。</summary>
    [TestMethod]
    public void CallbackOutputFailureAndOverLevelArePendingOncePerAudioSession()
    {
        var firstSession = new BassAudioSession(BassAudioPlayer.DeviceDriver.ASIO);
        var secondSession = new BassAudioSession(BassAudioPlayer.DeviceDriver.ASIO);

        Assert.IsTrue(firstSession.TryRecordCallbackOutputFailure(AudioPcmRenderStage.Stalled, Errors.OK));
        Assert.IsFalse(firstSession.TryRecordCallbackOutputFailure(AudioPcmRenderStage.NativeRead, Errors.Init));
        Assert.IsTrue(firstSession.HasCallbackOutputFailure);
        Assert.IsTrue(firstSession.TryConsumeCallbackOutputFailure(out AudioCallbackOutputFailure failure));
        Assert.AreEqual(AudioPcmRenderStage.Stalled, failure.RenderStage);
        Assert.IsNull(failure.ProcessStage);
        Assert.AreEqual(Errors.OK, failure.NativeError);
        Assert.IsFalse(firstSession.TryConsumeCallbackOutputFailure(out _));
        Assert.IsFalse(firstSession.TryRecordCallbackOutputFailure(AudioPcmRenderStage.Stalled));

        Assert.IsTrue(firstSession.TryMarkOutputOverLevelPending());
        Assert.IsFalse(firstSession.TryMarkOutputOverLevelPending());
        Assert.IsTrue(firstSession.TryConsumeOutputOverLevelPending());
        Assert.IsFalse(firstSession.TryConsumeOutputOverLevelPending());
        Assert.IsFalse(firstSession.TryMarkOutputOverLevelPending());
        Assert.IsTrue(secondSession.TryMarkOutputOverLevelPending());
    }

    /// <summary>callback gain故障を管理側の既存例外経路へ渡し、元の演算段階を失わない。</summary>
    [TestMethod]
    public void PendingCallbackGainFailureCanUseTheExistingManagementExceptionRoute()
    {
        var session = new BassAudioSession(BassAudioPlayer.DeviceDriver.WASAPI_SHARED);
        Assert.IsTrue(session.TryRecordCallbackOutputFailure(AudioOutputProcessFailure.NonFiniteOutput));

        AudioCallbackOutputFailureException exception = Assert.ThrowsException<AudioCallbackOutputFailureException>(
            session.ThrowPendingOutputFailure);

        Assert.AreEqual(AudioOutputProcessFailure.NonFiniteOutput, exception.Failure.ProcessStage);
        Assert.IsTrue(session.HasCallbackOutputFailure);
    }

    /// <summary>core未取得の初期化失敗ではdevice選択もFreeも呼ばず、取得していないnative資源を解放しない。</summary>
    [TestMethod]
    public void CoreInitializationFailure_DoesNotFreeUninitializedCore()
    {
        var session = new BassAudioSession(BassAudioPlayer.DeviceDriver.WASAPI_SHARED);
        var native = new RecordingNativeBoundary();

        bool released = BassAudioSessionCleanup.Release(
            session,
            native,
            new InvalidOperationException("BASS_Init failed"));

        Assert.IsTrue(released);
        Assert.IsTrue(session.IsReleased);
        Assert.AreEqual(0, native.Count("SetCoreDevice"));
        Assert.AreEqual(0, native.Count("FreeCore"));
    }

    /// <summary>初期化の主失敗にcleanupが重なっても元例外を置換せず、未確認所有をCleanupPendingへ残す。</summary>
    [TestMethod]
    public void CleanupFailure_DoesNotReplacePrimaryInitializationFailure()
    {
        var session = new BassAudioSession(BassAudioPlayer.DeviceDriver.WASAPI_SHARED)
        {
            ActualBackend = BassAudioPlayer.DeviceDriver.WASAPI_SHARED,
            CoreInitialized = true,
            CoreDeviceIndex = 2
        };
        var native = new RecordingNativeBoundary
        {
            FreeCoreResult = false,
            CoreError = Errors.Unknown
        };
        var primary = new InvalidOperationException("primary initialization failure");

        Exception? observed = null;
        try
        {
            try
            {
                throw primary;
            }
            catch (Exception exception)
            {
                BassAudioSessionCleanup.Release(session, native, exception);
                throw;
            }
        }
        catch (Exception exception)
        {
            observed = exception;
        }

        Assert.AreSame(primary, observed);
        Assert.AreEqual(BassAudioSessionState.CleanupPending, session.State);
        Assert.AreEqual(1, native.Count("FreeCore"));
    }

    /// <summary>ASIO・stream・coreを所有deviceで各一回解放し、再解放でnative処理やcallback所有を復活させない。</summary>
    [TestMethod]
    public void AsioSetupFailure_ReleasesAsioStreamsAndCoreExactlyOnce()
    {
        var session = new BassAudioSession(BassAudioPlayer.DeviceDriver.ASIO)
        {
            ActualBackend = BassAudioPlayer.DeviceDriver.ASIO,
            CoreInitialized = true,
            CoreDeviceIndex = 4,
            AsioInitialized = true,
            AsioDeviceIndex = 7,
            MixerHandle = 101,
            OutputHandle = 101,
            IsStarted = true
        };
        session.TrackOutputHandle(101);
        session.TrackAdditionalStreamHandle(202);
        var native = new RecordingNativeBoundary();

        Assert.IsTrue(BassAudioSessionCleanup.Release(session, native));
        Assert.IsTrue(BassAudioSessionCleanup.Release(session, native));

        Assert.AreEqual(1, native.Count("SetAsioDevice"));
        Assert.AreEqual(1, native.Count("StopAsio"));
        Assert.AreEqual(1, native.Count("FreeAsio"));
        Assert.AreEqual(1, native.Count("SetCoreDevice"));
        Assert.AreEqual(7, native.SelectedAsioDeviceIndices.Single());
        Assert.AreEqual(4, native.SelectedCoreDeviceIndices.Single());
        Assert.AreEqual(2, native.Count("FreeStream"));
        Assert.AreEqual(1, native.Count("FreeCore"));
        Assert.AreEqual(0, session.CallbackOutputHandle);
    }

    /// <summary>ASIO照会の途中取消では取得済みASIOとcoreを回収し、残存所有なく次の音声要求を受け入れる。</summary>
    [TestMethod]
    public void AsioCapabilityQuery_CancellationReleasesThePartiallyAcquiredSession()
    {
        var gate = new BassAudioOperationGate(initiallyOpen: true);
        var lifecycle = new BassAudioSessionLifecycle();
        var native = new RecordingNativeBoundary();
        using (BassAudioExclusiveLease exclusive = gate.EnterSessionInitialization())
        using (lifecycle.Enter())
        {
            AudioDeviceCapabilityResult result = BassAudioCapabilitySession.QueryAsio(
                CreateAsioCapabilityRequest(), lifecycle, native, session =>
                {
                    session.CoreInitialized = true;
                    session.CoreDeviceIndex = 4;
                    session.AsioInitialized = true;
                    session.AsioDeviceIndex = 2;
                    throw new OperationCanceledException();
                });
            Assert.AreEqual(AudioDeviceCapabilityStatus.Failed, result.Status);
            Assert.IsFalse(lifecycle.HasUnconfirmedOwnership);
            Assert.IsNull(lifecycle.CurrentSession);
            Assert.AreEqual(1, native.Count("FreeAsio"));
            Assert.AreEqual(1, native.Count("FreeCore"));
            exclusive.Complete(success: true);
        }
        Assert.IsTrue(gate.TryEnterRequest(out IDisposable next));
        next.Dispose();
    }

    /// <summary>active session中のASIO照会はBusyとして拒否し、照会とcleanupを呼ばず既存所有を維持する。</summary>
    [TestMethod]
    public void AsioCapabilityQuery_RejectsAnActiveSessionWithoutChangingOwnership()
    {
        var lifecycle = new BassAudioSessionLifecycle();
        var native = new RecordingNativeBoundary();
        AudioDeviceCapabilityRequest request = CreateAsioCapabilityRequest();
        int queryCalls = 0;
        BassAudioSession activeSession;
        AudioDeviceCapabilityResult result;

        using (lifecycle.Enter())
        {
            Assert.IsTrue(lifecycle.TryBegin(
                BassAudioPlayer.DeviceDriver.ASIO,
                new BassAudioPlayer.DeviceDescriptor("ASIO", "asio-id"),
                out activeSession));
            lifecycle.MarkActive(activeSession);

            result = BassAudioCapabilitySession.QueryAsio(
                request,
                lifecycle,
                native,
                _ =>
                {
                    queryCalls++;
                    return new AudioDeviceCapabilityResult(
                        request.Backend,
                        request.DeviceIdentity,
                        request.DeviceName,
                        AudioDeviceCapabilityStatus.Available);
                });

            Assert.AreSame(activeSession, lifecycle.CurrentSession);
            Assert.AreEqual(BassAudioSessionState.Active, activeSession.State);
        }

        Assert.AreEqual(AudioDeviceCapabilityStatus.Busy, result.Status);
        Assert.AreEqual(0, queryCalls);
        Assert.AreEqual(0, native.Count("SetCoreDevice"));
    }

    /// <summary>照会成功後もcleanup未確認なら結果を失敗にし、所有を隔離して次照会をBusyとして拒否する。</summary>
    [TestMethod]
    public void AsioCapabilityQuery_CleanupFailureFailsAndRetainsSessionOwnership()
    {
        var lifecycle = new BassAudioSessionLifecycle();
        var native = new RecordingNativeBoundary
        {
            SetCoreDeviceResult = false,
            CoreError = Errors.Device
        };
        AudioDeviceCapabilityRequest request = CreateAsioCapabilityRequest();
        BassAudioSession? ownedSession = null;
        AudioDeviceCapabilityResult result;

        using (lifecycle.Enter())
        {
            result = BassAudioCapabilitySession.QueryAsio(
                request,
                lifecycle,
                native,
                session =>
                {
                    ownedSession = session;
                    session.CoreInitialized = true;
                    session.CoreDeviceIndex = 4;
                    return new AudioDeviceCapabilityResult(
                        request.Backend,
                        request.DeviceIdentity,
                        request.DeviceName,
                        AudioDeviceCapabilityStatus.Available,
                        supportedRates: [SampleRate.SAMPLE_RATE_48000Hz]);
                });

            BassAudioSession cleanupPendingSession = ownedSession
                ?? throw new AssertFailedException("The ASIO query did not acquire a session.");
            Assert.AreSame(cleanupPendingSession, lifecycle.CurrentSession);
            Assert.AreEqual(BassAudioSessionState.CleanupPending, cleanupPendingSession.State);
            Assert.IsTrue(lifecycle.HasUnconfirmedOwnership);
        }

        BassAudioSession retainedSession = ownedSession
            ?? throw new AssertFailedException("The ASIO query did not retain its session.");
        Assert.AreEqual(AudioDeviceCapabilityStatus.Failed, result.Status);
        Assert.AreEqual("audio session cleanup", result.FailureStage);
        Assert.AreEqual(1, result.Attempts.Count);
        Assert.AreEqual("audio session cleanup", result.Attempts[0].Stage);
        Assert.AreEqual(0, native.Count("FreeCore"));

        using (lifecycle.Enter())
        {
            AudioDeviceCapabilityResult retry = BassAudioCapabilitySession.QueryAsio(
                request,
                lifecycle,
                native,
                _ => throw new AssertFailedException("A cleanup-pending session must remain owned."));
            Assert.AreEqual(AudioDeviceCapabilityStatus.Busy, retry.Status);
            Assert.AreSame(retainedSession, lifecycle.CurrentSession);
        }
    }

    /// <summary>照会native失敗とcleanup未確認が重なっても主native段階・番号を保持し、cleanupを併記して所有を隔離する。</summary>
    [TestMethod]
    public void AsioCapabilityQuery_CleanupFailurePreservesPrimaryNativeFailure()
    {
        var lifecycle = new BassAudioSessionLifecycle();
        var native = new RecordingNativeBoundary
        {
            SetCoreDeviceResult = false,
            CoreError = Errors.Device
        };
        AudioDeviceCapabilityRequest request = CreateAsioCapabilityRequest();
        BassAudioSession? ownedSession = null;
        AudioDeviceCapabilityResult result;

        using (lifecycle.Enter())
        {
            result = BassAudioCapabilitySession.QueryAsio(
                request,
                lifecycle,
                native,
                session =>
                {
                    ownedSession = session;
                    session.CoreInitialized = true;
                    session.CoreDeviceIndex = 4;
                    return new AudioDeviceCapabilityResult(
                        request.Backend,
                        request.DeviceIdentity,
                        request.DeviceName,
                        AudioDeviceCapabilityStatus.Failed,
                        failureStage: "BASS_ASIO_GetDeviceInfos",
                        nativeErrorSource: "BASSASIO",
                        nativeErrorCode: Errors.Init);
                });

            BassAudioSession cleanupPendingSession = ownedSession
                ?? throw new AssertFailedException("The ASIO query did not acquire a session.");
            Assert.AreSame(cleanupPendingSession, lifecycle.CurrentSession);
            Assert.AreEqual(BassAudioSessionState.CleanupPending, cleanupPendingSession.State);
            Assert.IsTrue(lifecycle.HasUnconfirmedOwnership);
        }

        BassAudioSession retainedSession = ownedSession
            ?? throw new AssertFailedException("The ASIO query did not retain its session.");
        Assert.AreEqual(AudioDeviceCapabilityStatus.Failed, result.Status);
        Assert.AreEqual("BASS_ASIO_GetDeviceInfos", result.FailureStage);
        Assert.AreEqual("BASSASIO", result.NativeErrorSource);
        Assert.AreEqual(Errors.Init, result.NativeErrorCode);
        Assert.AreEqual(1, result.Attempts.Count);
        Assert.AreEqual("audio session cleanup", result.Attempts[0].Stage);
        Assert.AreEqual(0, native.Count("FreeCore"));

        using (lifecycle.Enter())
        {
            AudioDeviceCapabilityResult retry = BassAudioCapabilitySession.QueryAsio(
                request,
                lifecycle,
                native,
                _ => throw new AssertFailedException("A cleanup-pending session must remain owned."));
            Assert.AreEqual(AudioDeviceCapabilityStatus.Busy, retry.Status);
            Assert.AreSame(retainedSession, lifecycle.CurrentSession);
        }
    }

    /// <summary>保存deviceへの選択に失敗したら所有を保持し、別deviceのstreamやcoreをFreeしない。</summary>
    [TestMethod]
    public void DeviceSelectionFailure_RetainsOwnershipWithoutFreeingUnknownDevice()
    {
        var session = new BassAudioSession(BassAudioPlayer.DeviceDriver.WASAPI_SHARED)
        {
            ActualBackend = BassAudioPlayer.DeviceDriver.WASAPI_SHARED,
            CoreInitialized = true,
            CoreDeviceIndex = 3,
            MixerHandle = 77
        };
        var native = new RecordingNativeBoundary
        {
            SetCoreDeviceResult = false,
            CoreError = Errors.Device
        };

        Assert.IsFalse(BassAudioSessionCleanup.Release(session, native));

        Assert.AreEqual(BassAudioSessionState.CleanupPending, session.State);
        Assert.IsTrue(session.HasNativeOwnership);
        Assert.AreEqual(1, native.Count("SetCoreDevice"));
        Assert.AreEqual(0, native.Count("FreeStream"));
        Assert.AreEqual(0, native.Count("FreeCore"));
    }

    /// <summary>native層が既解放を示すInitエラーは冪等cleanup成功として一度だけ扱う。</summary>
    [TestMethod]
    public void AlreadyReleasedNativeLayer_IsAnIdempotentCleanupSuccess()
    {
        var session = new BassAudioSession(BassAudioPlayer.DeviceDriver.WASAPI_SHARED)
        {
            ActualBackend = BassAudioPlayer.DeviceDriver.WASAPI_SHARED,
            CoreInitialized = true,
            CoreDeviceIndex = 2
        };
        var native = new RecordingNativeBoundary
        {
            FreeCoreResult = false,
            CoreError = Errors.Init
        };

        Assert.IsTrue(BassAudioSessionCleanup.Release(session, native));
        Assert.IsTrue(BassAudioSessionCleanup.Release(session, native));

        Assert.IsTrue(session.IsReleased);
        Assert.AreEqual(1, native.Count("FreeCore"));
    }

    /// <summary>Released表示でもnative所有が残る場合は実cleanupを行い、状態名だけで解放済みと誤判定しない。</summary>
    [TestMethod]
    public void ReleasedState_WithRecordedNativeOwnershipStillPerformsCleanup()
    {
        var session = new BassAudioSession(BassAudioPlayer.DeviceDriver.WASAPI_SHARED)
        {
            ActualBackend = BassAudioPlayer.DeviceDriver.WASAPI_SHARED,
            CoreInitialized = true,
            CoreDeviceIndex = 6,
            State = BassAudioSessionState.Released
        };
        var native = new RecordingNativeBoundary();

        Assert.IsFalse(session.IsReleased);
        Assert.IsTrue(BassAudioSessionCleanup.Release(session, native));

        Assert.IsTrue(session.IsReleased);
        Assert.AreEqual(1, native.Count("SetCoreDevice"));
        Assert.AreEqual(6, native.SelectedCoreDeviceIndices.Single());
        Assert.AreEqual(1, native.Count("FreeCore"));
    }

    /// <summary>保存core deviceが既解放なら未知deviceをFreeせず記録所有を解除し、playerへ解放を一回通知する。</summary>
    [TestMethod]
    public void CoreDeviceAlreadyReleased_ClearsRecordedOwnershipWithoutFreeingUnknownDevice()
    {
        int playerReleaseNotifications = 0;
        var session = new BassAudioSession(BassAudioPlayer.DeviceDriver.WASAPI_SHARED)
        {
            ActualBackend = BassAudioPlayer.DeviceDriver.WASAPI_SHARED,
            CoreInitialized = true,
            CoreDeviceIndex = 7,
            MixerHandle = 41,
            OutputHandle = 42,
            IsStarted = true
        };
        session.TrackAdditionalStreamHandle(43);
        session.TrackPlayerStream(44, new object(), _ => playerReleaseNotifications++);
        var native = new RecordingNativeBoundary
        {
            SetCoreDeviceResult = false,
            CoreError = Errors.Init
        };

        Assert.IsTrue(BassAudioSessionCleanup.Release(session, native));

        Assert.IsTrue(session.IsReleased);
        Assert.AreEqual(7, native.SelectedCoreDeviceIndices.Single());
        Assert.AreEqual(0, native.Count("FreeStream"));
        Assert.AreEqual(0, native.Count("FreeCore"));
        Assert.AreEqual(1, playerReleaseNotifications);
    }

    /// <summary>同じstream handleの重複所有を拒否し、統合所有内のhandleをnativeへ一度だけ解放する。</summary>
    [TestMethod]
    public void UnifiedStreamOwnership_RejectsDuplicateHandleAndReleasesOnce()
    {
        int playerReleaseNotifications = 0;
        var session = new BassAudioSession(BassAudioPlayer.DeviceDriver.WASAPI_SHARED)
        {
            ActualBackend = BassAudioPlayer.DeviceDriver.WASAPI_SHARED,
            CoreInitialized = true,
            CoreDeviceIndex = 8,
            MixerHandle = 51,
            OutputHandle = 51
        };
        session.TrackAdditionalStreamHandle(51);
        Assert.ThrowsException<InvalidOperationException>(
            () => session.TrackPlayerStream(51, new object(), _ => playerReleaseNotifications++));
        var native = new RecordingNativeBoundary();

        Assert.IsTrue(BassAudioSessionCleanup.Release(session, native));

        Assert.IsTrue(session.IsReleased);
        Assert.AreEqual(1, native.Count("FreeStream"));
        Assert.AreEqual(1, native.Count("FreeCore"));
        Assert.AreEqual(0, playerReleaseNotifications);
    }

    /// <summary>callback出力交換は旧解放前に新sourceを公開し、解放成功後も新callback先を保持する。</summary>
    [TestMethod]
    public void CallbackOutputReplacement_PublishesBeforeReleaseAndKeepsReplacementOnSuccess()
    {
        var session = new BassAudioSession(BassAudioPlayer.DeviceDriver.WASAPI_SHARED);
        session.TrackOutputHandle(101);

        bool released = session.TryPrepareCallbackOutputReplacement(
            101,
            202,
            handle =>
            {
                Assert.AreEqual(202, session.CallbackOutputHandle);
                session.ConfirmStreamReleased(handle);
                return true;
            });

        Assert.IsTrue(released);
        Assert.AreEqual(202, session.CallbackOutputHandle);
        Assert.AreEqual(0, session.OutputHandle);
    }

    /// <summary>旧出力解放がfalseならcallback先と所有handleを旧sourceへ戻し、未確認解放を採用しない。</summary>
    [TestMethod]
    public void CallbackOutputReplacement_RestoresPreviousSourceWhenReleaseFails()
    {
        var session = new BassAudioSession(BassAudioPlayer.DeviceDriver.WASAPI_SHARED);
        session.TrackOutputHandle(101);

        bool released = session.TryPrepareCallbackOutputReplacement(
            101,
            202,
            _ =>
            {
                Assert.AreEqual(202, session.CallbackOutputHandle);
                return false;
            });

        Assert.IsFalse(released);
        Assert.AreEqual(101, session.CallbackOutputHandle);
        Assert.AreEqual(101, session.OutputHandle);
    }

    /// <summary>旧出力解放が例外なら元失敗を返し、callback先と所有handleを旧sourceへ復元する。</summary>
    [TestMethod]
    public void CallbackOutputReplacement_RestoresPreviousSourceWhenReleaseThrows()
    {
        var session = new BassAudioSession(BassAudioPlayer.DeviceDriver.ASIO);
        session.TrackOutputHandle(101);

        Assert.ThrowsException<InvalidOperationException>(() =>
            session.TryPrepareCallbackOutputReplacement(
                101,
                202,
                _ => throw new InvalidOperationException("release failed")));

        Assert.AreEqual(101, session.CallbackOutputHandle);
        Assert.AreEqual(101, session.OutputHandle);
    }

    /// <summary>session不在または公開handleなしのcallbackはreadせずゼロを返し、無効pointerを使用しない。</summary>
    [TestMethod]
    public void PublishedCallbackOutputReader_DoesNotReadWithoutSessionHandle()
    {
        Assert.AreEqual(0, BassAudioPlayer.ReadPublishedCallbackOutput(null, IntPtr.Zero, 16));
        Assert.AreEqual(0, BassAudioPlayer.ReadPublishedCallbackOutput(
            new BassAudioSession(BassAudioPlayer.DeviceDriver.ASIO), IntPtr.Zero, 16));
    }

    /// <summary>callbackは公開handleをpullしてからgainを適用し保護byteを変えず、故障後は無音にして元原因を管理側へ渡す。</summary>
    [TestMethod]
    public void PublishedCallbackOutputReader_AppliesGainAfterPullAndPreservesBufferGuards()
    {
        var native = new CallbackPcmNative();
        var session = new BassAudioSession(BassAudioPlayer.DeviceDriver.ASIO)
        {
            OutputProcessor = new AudioOutputProcessor(48000, .5),
            CallbackPcmRenderer = new AudioPcmRenderer(123, 48000, 2, native)
        };
        session.TrackOutputHandle(456);
        IntPtr buffer = Marshal.AllocHGlobal(24);
        try
        {
            Marshal.WriteInt32(buffer, 0, 123456);
            Marshal.WriteInt32(buffer, 20, 654321);
            Assert.AreEqual(16, BassAudioPlayer.ReadPublishedCallbackOutput(session, IntPtr.Add(buffer, 4), 16));
            float[] samples = new float[4];
            Marshal.Copy(IntPtr.Add(buffer, 4), samples, 0, 4);
            CollectionAssert.AreEqual(new[] { .625f, -.625f, .625f, -.625f }, samples);
            Assert.AreEqual(456, native.LastHandle);
            Assert.AreEqual(123456, Marshal.ReadInt32(buffer, 0));
            Assert.AreEqual(654321, Marshal.ReadInt32(buffer, 20));

            native.Fail = true;
            Assert.AreEqual(16, BassAudioPlayer.ReadPublishedCallbackOutput(session, IntPtr.Add(buffer, 4), 16));
            Marshal.Copy(IntPtr.Add(buffer, 4), samples, 0, 4);
            CollectionAssert.AreEqual(new float[4], samples);
            Assert.IsTrue(session.HasCallbackOutputFailure);
            Assert.ThrowsException<AudioCallbackOutputFailureException>(session.ThrowPendingOutputFailure);
        }
        finally
        {
            Marshal.FreeHGlobal(buffer);
        }
    }

    /// <summary>正の初回callback frame数を公開してから非同期継続へ通知し、ゼロ・負frameを成功と扱わず観測済み通知を保持する。</summary>
    [TestMethod]
    public async Task FirstCallbackReady_PublishesRealFramesBeforeAsynchronousContinuationAndIsRetained()
    {
        var session = new BassAudioSession(BassAudioPlayer.DeviceDriver.ASIO)
        {
            ActualBackend = BassAudioPlayer.DeviceDriver.ASIO,
            OutputProcessor = new AudioOutputProcessor(48000, 1),
            CallbackPcmRenderer = new AudioPcmRenderer(123, 48000, 2, new CallbackPcmNative())
        };
        session.TrackOutputHandle(456);
        Task ready = session.WaitForOutputReadyAsync();
        Assert.IsFalse(ready.IsCompleted);
        session.ObserveCallbackPullSize(0);
        session.ObserveCallbackPullSize(-1);
        Assert.IsFalse(ready.IsCompleted);
        using var insideCallback = new ThreadLocal<bool>();
        Task continuation = ready.ContinueWith(_ =>
        {
            Assert.IsFalse(insideCallback.Value, "初回通知の継続をcallback内で同期実行しません。");
            Assert.AreEqual(2, session.MaximumCallbackFrames);
        }, CancellationToken.None, TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
        IntPtr buffer = Marshal.AllocHGlobal(16);
        try
        {
            insideCallback.Value = true;
            try { Assert.AreEqual(16, BassAudioPlayer.ReadPublishedCallbackOutput(session, buffer, 16)); }
            finally { insideCallback.Value = false; }
            await continuation;
            await ready;
            Assert.IsTrue(session.WaitForOutputReadyAsync().IsCompletedSuccessfully, "既観測sessionは追加callbackなしで通過します。");
        }
        finally { Marshal.FreeHGlobal(buffer); }
    }

    /// <summary>初回callbackの負長・非整列長は元故障で待機を起こし、最大frameを成功として増やさず再待機にも故障を返す。</summary>
    [DataTestMethod]
    [DataRow(-1, (int)AudioPcmRenderStage.InvalidReadLength)]
    [DataRow(1, (int)AudioPcmRenderStage.UnalignedFrame)]
    public async Task FirstCallbackReady_InvalidLengthWakesManagementWithOriginalFailure(int length, int expectedStage)
    {
        var session = new BassAudioSession(BassAudioPlayer.DeviceDriver.ASIO)
        {
            ActualBackend = BassAudioPlayer.DeviceDriver.ASIO,
            OutputProcessor = new AudioOutputProcessor(48000, 1),
            CallbackPcmRenderer = new AudioPcmRenderer(123, 48000, 2, new CallbackPcmNative())
        };
        session.TrackOutputHandle(456);
        Task ready = session.WaitForOutputReadyAsync();
        IntPtr buffer = Marshal.AllocHGlobal(16);
        try
        {
            _ = BassAudioPlayer.ReadPublishedCallbackOutput(session, buffer, length);
            AudioCallbackOutputFailureException failure = await Assert.ThrowsExceptionAsync<AudioCallbackOutputFailureException>(() => ready);
            Assert.AreEqual((AudioPcmRenderStage)expectedStage, failure.Failure.RenderStage);
            Assert.AreEqual(0, session.MaximumCallbackFrames);
            await Assert.ThrowsExceptionAsync<AudioCallbackOutputFailureException>(session.WaitForOutputReadyAsync);
        }
        finally { Marshal.FreeHGlobal(buffer); }
    }

    /// <summary>オフラインNullDeviceの準備通知は物理callbackを待たずに通過し、未観測frameを捏造しない。</summary>
    [TestMethod]
    public async Task FirstCallbackReady_NullDeviceDoesNotRequirePhysicalObservation()
    {
        var session = new BassAudioSession(BassAudioPlayer.DeviceDriver.NULL_DEVICE) { ActualBackend = BassAudioPlayer.DeviceDriver.NULL_DEVICE };
        Task ready = session.WaitForOutputReadyAsync();
        Assert.IsTrue(ready.IsCompletedSuccessfully);
        await ready;
        Assert.AreEqual(0, session.MaximumCallbackFrames);
    }

    internal sealed class CallbackPcmNative : IAudioPcmNative
    {
        internal int LastHandle { get; private set; }
        internal bool Fail { get; set; }
        public Errors LastError => Fail ? Errors.Handle : Errors.OK;
        public int ChannelGetData(int channel, float[] buffer, int lengthBytes)
        {
            LastHandle = channel;
            if (Fail)
            {
                return -1;
            }
            // 一回に一frameだけ返し、callbackが短いreadを継続することも確認します。
            buffer[0] = 1.25f;
            buffer[1] = -1.25f;
            return 8;
        }
    }

    /// <summary>ASIO tempo交換は旧解放前に新callbackを公開し、解放失敗では旧callbackとhandleへ戻す。</summary>
    [TestMethod]
    public void AsioTempoReplacement_PublishesBeforeReleaseAndRestoresOnFailure()
    {
        var session = new BassAudioSession(BassAudioPlayer.DeviceDriver.ASIO);
        session.TrackOutputHandle(101);

        bool released = BassAudioPlayer.TryReleaseTempoOutputForReset(
            session,
            BassAudioPlayer.DeviceDriver.ASIO,
            101,
            202,
            handle =>
            {
                Assert.AreEqual(202, session.CallbackOutputHandle);
                session.ConfirmStreamReleased(handle);
                return true;
            });

        Assert.IsTrue(released);
        Assert.AreEqual(202, session.CallbackOutputHandle);

        session.TrackOutputHandle(101);
        released = BassAudioPlayer.TryReleaseTempoOutputForReset(
            session,
            BassAudioPlayer.DeviceDriver.ASIO,
            101,
            202,
            _ => false);

        Assert.IsFalse(released);
        Assert.AreEqual(101, session.CallbackOutputHandle);
        Assert.AreEqual(101, session.OutputHandle);
    }

    /// <summary>callbackを持たないNullDeviceのtempo解放では新sourceをcallback先として公開しない。</summary>
    [TestMethod]
    public void NonCallbackTempoReplacement_ReleasesWithoutPublishingReplacement()
    {
        var session = new BassAudioSession(BassAudioPlayer.DeviceDriver.NULL_DEVICE);
        session.TrackOutputHandle(101);

        bool released = BassAudioPlayer.TryReleaseTempoOutputForReset(
            session,
            BassAudioPlayer.DeviceDriver.NULL_DEVICE,
            101,
            202,
            handle =>
            {
                Assert.AreEqual(101, session.CallbackOutputHandle);
                return handle == 101;
            });

        Assert.IsTrue(released);
        Assert.AreEqual(101, session.CallbackOutputHandle);
    }

    /// <summary>未開始WASAPIでも取得済みmixer・WASAPI・coreを解放し、未開始出力のStopを呼ばない。</summary>
    [TestMethod]
    public void UnstartedWasapiSession_ReleasesOwnedMixerWithoutStoppingOutput()
    {
        var session = new BassAudioSession(BassAudioPlayer.DeviceDriver.WASAPI_SHARED)
        {
            ActualBackend = BassAudioPlayer.DeviceDriver.WASAPI_SHARED,
            CoreInitialized = true,
            CoreDeviceIndex = 0,
            WasapiInitialized = true,
            WasapiDeviceIndex = 3,
            MixerHandle = 123
        };
        session.TrackOutputHandle(123);
        var native = new RecordingNativeBoundary();

        Assert.IsTrue(BassAudioSessionCleanup.Release(session, native));

        Assert.AreEqual(0, native.Count("StopWasapi"));
        Assert.AreEqual(1, native.Count("FreeWasapi"));
        Assert.AreEqual(1, native.Count("FreeStream"));
        Assert.AreEqual(1, native.Count("FreeCore"));
        Assert.AreEqual(0, session.CallbackOutputHandle);
        Assert.IsTrue(session.IsReleased);
    }

    /// <summary>WASAPI解放未確認ではcallback sourceと所有を保持し、明示再試行の成功後だけ解除する。</summary>
    [TestMethod]
    public void WasapiFreeFailure_RetainsCallbackSourceUntilRetrySucceeds()
    {
        var session = new BassAudioSession(BassAudioPlayer.DeviceDriver.WASAPI_SHARED)
        {
            ActualBackend = BassAudioPlayer.DeviceDriver.WASAPI_SHARED,
            CoreInitialized = true,
            CoreDeviceIndex = 0,
            WasapiInitialized = true,
            WasapiDeviceIndex = 3,
            MixerHandle = 123
        };
        session.TrackOutputHandle(123);
        var native = new RecordingNativeBoundary
        {
            FreeWasapiResult = false,
            WasapiError = Errors.Unknown
        };

        Assert.IsFalse(BassAudioSessionCleanup.Release(session, native));
        Assert.AreEqual(123, session.CallbackOutputHandle);
        Assert.IsTrue(session.HasNativeOwnership);

        native.FreeWasapiResult = true;
        Assert.IsTrue(BassAudioSessionCleanup.Release(session, native));
        Assert.AreEqual(0, session.CallbackOutputHandle);
        Assert.IsTrue(session.IsReleased);
    }

    /// <summary>cleanup未確認のsessionはlifecycleへ残し、明示解放確認後にだけ別sessionの取得を許す。</summary>
    [TestMethod]
    public void CleanupFailure_RemainsOwnedUntilLaterRetrySucceeds()
    {
        var lifecycle = new BassAudioSessionLifecycle();
        var native = new RecordingNativeBoundary
        {
            FreeCoreResult = false,
            CoreError = Errors.Unknown
        };

        using (lifecycle.Enter())
        {
            Assert.IsTrue(lifecycle.TryBegin(
                BassAudioPlayer.DeviceDriver.WASAPI_SHARED,
                default,
                out BassAudioSession session));
            session.ActualBackend = BassAudioPlayer.DeviceDriver.WASAPI_SHARED;
            session.CoreInitialized = true;
            session.CoreDeviceIndex = 2;
            lifecycle.MarkActive(session);

            Assert.IsFalse(BassAudioSessionCleanup.Release(session, native));
            lifecycle.CompleteCleanup(session);
            Assert.IsTrue(lifecycle.HasCleanupPending);
            Assert.AreSame(session, lifecycle.CurrentSession);

            native.FreeCoreResult = true;
            Assert.IsTrue(BassAudioSessionCleanup.Release(session, native));
            lifecycle.CompleteCleanup(session);
            Assert.IsNull(lifecycle.CurrentSession);

            Assert.IsTrue(lifecycle.TryBegin(
                BassAudioPlayer.DeviceDriver.WASAPI_SHARED,
                default,
                out BassAudioSession replacement));
            Assert.AreNotSame(session, replacement);
        }

        Assert.AreEqual(2, native.Count("FreeCore"));
    }

    /// <summary>scope付き解放tokenが古い場合は別の現sessionを選ばず、正しいtokenだけを解放対象とする。</summary>
    [TestMethod]
    public void ScopedReleaseToken_DoesNotSelectAnotherOwnedSession()
    {
        var lifecycle = new BassAudioSessionLifecycle();
        var staleSession = new BassAudioSession(BassAudioPlayer.DeviceDriver.NULL_DEVICE);

        using (lifecycle.Enter())
        {
            Assert.IsTrue(lifecycle.TryBegin(
                BassAudioPlayer.DeviceDriver.WASAPI_SHARED,
                default,
                out BassAudioSession ownedSession));
            ownedSession.ActualBackend = BassAudioPlayer.DeviceDriver.WASAPI_SHARED;
            lifecycle.MarkActive(ownedSession);

            Assert.IsFalse(lifecycle.TryGetForRelease(
                staleSession,
                allowAnySession: false,
                out BassAudioSession selectedSession));
            Assert.AreSame(ownedSession, selectedSession);
            Assert.AreSame(ownedSession, lifecycle.CurrentSession);
            Assert.IsTrue(lifecycle.TryGetForRelease(
                ownedSession,
                allowAnySession: false,
                out selectedSession));
            Assert.AreSame(ownedSession, selectedSession);
        }
    }

    /// <summary>session leaseは解放falseでtokenを保持し、明示解放成功でのみ解除する。</summary>
    [TestMethod]
    public void SessionLease_RetainsTokenUntilCleanupIsConfirmed()
    {
        var session = new BassAudioSession(BassAudioPlayer.DeviceDriver.NULL_DEVICE);
        var lease = new BassAudioSessionLease();
        int releaseCalls = 0;
        lease.Attach(session);

        Assert.IsFalse(lease.TryRelease(_ =>
        {
            releaseCalls++;
            return false;
        }));
        Assert.AreSame(session, lease.Session);

        Assert.IsTrue(lease.TryRelease(_ =>
        {
            releaseCalls++;
            return true;
        }));
        Assert.IsNull(lease.Session);
        Assert.AreEqual(2, releaseCalls);
    }

    /// <summary>解放確認済みtokenは回復後のsessionへ交換でき、旧tokenを現所有として残さない。</summary>
    [TestMethod]
    public void SessionLease_ReleasedTokenCanBeReplacedByRecoveredSession()
    {
        var released = new BassAudioSession(BassAudioPlayer.DeviceDriver.WASAPI_SHARED)
        {
            State = BassAudioSessionState.Released
        };
        var replacement = new BassAudioSession(BassAudioPlayer.DeviceDriver.WASAPI_SHARED)
        {
            State = BassAudioSessionState.Active
        };
        var lease = new BassAudioSessionLease();
        lease.Attach(released);

        lease.Attach(replacement);

        Assert.AreSame(replacement, lease.Session);
    }

    /// <summary>初期化中・active・cleanup保留のtoken交換を拒否し、未解放sessionの所有を維持する。</summary>
    [TestMethod]
    public void SessionLease_UnreleasedTokenRejectsReplacementAndRetainsOwner()
    {
        foreach (BassAudioSessionState state in new[]
                 {
                     BassAudioSessionState.Initializing,
                     BassAudioSessionState.Active,
                     BassAudioSessionState.CleanupPending
                 })
        {
            var owned = new BassAudioSession(BassAudioPlayer.DeviceDriver.WASAPI_SHARED)
            {
                State = state
            };
            var replacement = new BassAudioSession(BassAudioPlayer.DeviceDriver.WASAPI_SHARED);
            var lease = new BassAudioSessionLease();
            lease.Attach(owned);

            Assert.ThrowsException<InvalidOperationException>(() => lease.Attach(replacement));
            Assert.AreSame(owned, lease.Session, state.ToString());
        }
    }

    /// <summary>lease解放が例外でも元失敗とtokenを保持し、明示再試行の対象を失わない。</summary>
    [TestMethod]
    public void SessionLease_ReleaseExceptionRetainsTokenForRetry()
    {
        var session = new BassAudioSession(BassAudioPlayer.DeviceDriver.WASAPI_SHARED);
        var lease = new BassAudioSessionLease();
        var failure = new InvalidOperationException("release failed");
        lease.Attach(session);

        InvalidOperationException observed = Assert.ThrowsException<InvalidOperationException>(
            () => lease.TryRelease(_ => throw failure));

        Assert.AreSame(failure, observed);
        Assert.AreSame(session, lease.Session);
    }

    /// <summary>stream解放確認は該当handleだけを解除し、別mixerを残したまま新出力所有へ交換する。</summary>
    [TestMethod]
    public void OutputHandleReplacement_ForgetsOnlyConfirmedRelease()
    {
        var session = new BassAudioSession(BassAudioPlayer.DeviceDriver.WASAPI_SHARED)
        {
            MixerHandle = 10,
            OutputHandle = 20
        };

        session.ConfirmStreamReleased(20);
        session.TrackOutputHandle(30);

        Assert.AreEqual(10, session.MixerHandle);
        Assert.AreEqual(30, session.OutputHandle);
        Assert.IsFalse(session.AdditionalStreamHandles.Contains(20));

        session.ConfirmStreamReleased(10);

        Assert.AreEqual(0, session.MixerHandle);
    }

    /// <summary>shutdown要求後は新rootとcallback受付を拒否し、受理済みroot内の継続を許して全root終端後にshutdownする。</summary>
    [TestMethod]
    public void OperationGate_ShutdownAllowsNestedCallAndRejectsNewRootUntilDrain()
    {
        // 操作leaseとruntime shutdown間の排他・受付遷移が成立しない場合を検出するため、ここでは短い期限を使う。
        var gate = new BassAudioOperationGate(initiallyOpen: true);
        using var rootEntered = new ManualResetEventSlim();
        using var allowNested = new ManualResetEventSlim();
        using var nestedCompleted = new ManualResetEventSlim();
        using var releaseRoot = new ManualResetEventSlim();
        using var shutdownBodyEntered = new ManualResetEventSlim();
        Task rootTask = Task.Factory.StartNew(
            () =>
            {
                Assert.IsTrue(gate.TryEnterOperation(out BassAudioOperationLease root));
                using (root)
                {
                    rootEntered.Set();
                    allowNested.Wait();
                    Assert.IsTrue(gate.TryEnterOperation(out BassAudioOperationLease nested));
                    using (nested)
                    {
                        Assert.IsTrue(gate.TryEnterNonBlockingOperation(
                            out BassAudioOperationLease nestedCallback));
                        using (nestedCallback)
                        {
                            nestedCompleted.Set();
                        }
                    }
                    releaseRoot.Wait();
                }
            },
            CancellationToken.None,
            TaskCreationOptions.LongRunning,
            TaskScheduler.Default);

        Assert.IsTrue(rootEntered.Wait(TimeSpan.FromSeconds(2)));
        Task shutdownTask = Task.Factory.StartNew(
            () =>
            {
                using BassAudioExclusiveLease shutdown = gate.EnterRuntimeShutdown();
                shutdownBodyEntered.Set();
                shutdown.Complete(success: true);
            },
            CancellationToken.None,
            TaskCreationOptions.LongRunning,
            TaskScheduler.Default);

        try
        {
            Assert.IsTrue(gate.WaitForShutdownRequest(TimeSpan.FromSeconds(2)));
            Assert.IsFalse(gate.TryEnterOperation(out _));
            Task<bool> callbackAdmission = Task.Factory.StartNew(
                () =>
                {
                    bool entered = gate.TryEnterNonBlockingOperation(
                        out BassAudioOperationLease callback);
                    if (entered)
                    {
                        callback.Dispose();
                    }
                    return entered;
                },
                CancellationToken.None,
                TaskCreationOptions.LongRunning,
                TaskScheduler.Default);
            Assert.IsTrue(callbackAdmission.Wait(TimeSpan.FromSeconds(2)));
            Assert.IsFalse(callbackAdmission.Result);
            allowNested.Set();
            Assert.IsTrue(nestedCompleted.Wait(TimeSpan.FromSeconds(2)));
            Assert.IsFalse(shutdownBodyEntered.IsSet);
            releaseRoot.Set();
            Assert.IsTrue(shutdownBodyEntered.Wait(TimeSpan.FromSeconds(2)));
            Assert.IsTrue(Task.WaitAll([rootTask, shutdownTask], TimeSpan.FromSeconds(2)));
        }
        finally
        {
            allowNested.Set();
            releaseRoot.Set();
        }
    }

    /// <summary>初期化途中のsessionも未確認所有として扱い、cleanup終端後だけ現sessionと回復要求を解除する。</summary>
    [TestMethod]
    public void Lifecycle_InitializingSessionRequiresRecoveryUntilCleanupCompletes()
    {
        var lifecycle = new BassAudioSessionLifecycle();

        using (lifecycle.Enter())
        {
            Assert.IsTrue(lifecycle.TryBegin(
                BassAudioPlayer.DeviceDriver.WASAPI_SHARED,
                default,
                out BassAudioSession session));
            Assert.IsTrue(lifecycle.HasUnconfirmedOwnership);

            BassAudioSessionCleanup.Release(session, new RecordingNativeBoundary());
            lifecycle.CompleteCleanup(session);

            Assert.IsFalse(lifecycle.HasUnconfirmedOwnership);
            Assert.IsNull(lifecycle.CurrentSession);
        }
    }

    /// <summary>共有leaseからcleanupへの昇格を拒否し、取得直前到達後も二つ・一つのroot保持中はcleanupを取得せず、全解放後に取得・終端することを確認します。</summary>
    [TestMethod]
    public async Task OperationGate_SessionCleanupDrainsAllRootsAndRejectsSharedPromotion()
    {
        var gate = new BassAudioOperationGate(initiallyOpen: true);
        Assert.IsTrue(gate.TryEnterOperation(out BassAudioOperationLease ambient));
        using (ambient)
        {
            Assert.IsFalse(gate.TryEnterSessionCleanup(out _));
        }

        using var releaseFirstRoot = new ManualResetEventSlim();
        using var releaseSecondRoot = new ManualResetEventSlim();
        var firstEntered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var secondEntered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var cleanupAttempted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var cleanupEntered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        Task CreateRoot(TaskCompletionSource entered, ManualResetEventSlim release) =>
            Task.Factory.StartNew(
                () =>
                {
                    Assert.IsTrue(gate.TryEnterOperation(out BassAudioOperationLease operation));
                    using (operation)
                    {
                        entered.TrySetResult();
                        release.Wait();
                    }
                },
                CancellationToken.None,
                TaskCreationOptions.LongRunning,
                TaskScheduler.Default);

        Task firstRoot = Task.CompletedTask;
        Task secondRoot = Task.CompletedTask;
        Task cleanup = Task.CompletedTask;
        bool verified = false;
        try
        {
            firstRoot = CreateRoot(firstEntered, releaseFirstRoot);
            secondRoot = CreateRoot(secondEntered, releaseSecondRoot);
            await TestUiDispatcherHost.AwaitNotificationAsync(firstEntered.Task, firstRoot, "audio-cleanup.first-root");
            await TestUiDispatcherHost.AwaitNotificationAsync(secondEntered.Task, secondRoot, "audio-cleanup.second-root");
            cleanup = Task.Factory.StartNew(
                () =>
                {
                    cleanupAttempted.TrySetResult();
                    Assert.IsTrue(gate.TryEnterSessionCleanup(out BassAudioExclusiveLease lifecycle));
                    using (lifecycle)
                    {
                        cleanupEntered.TrySetResult();
                        lifecycle.Complete(success: true);
                    }
                },
                CancellationToken.None,
                TaskCreationOptions.LongRunning,
                TaskScheduler.Default);
            await TestUiDispatcherHost.AwaitNotificationAsync(cleanupAttempted.Task, cleanup, "audio-cleanup.before-acquire");
            // 取得直前通知後の禁止期間を観測する。通常完了を時間経過から推定しない。
            Assert.IsFalse(cleanup.Wait(TimeSpan.FromMilliseconds(100)));
            Assert.IsFalse(cleanupEntered.Task.IsCompleted);
            releaseFirstRoot.Set();
            await firstRoot;
            Assert.IsFalse(cleanup.Wait(TimeSpan.FromMilliseconds(100)));
            Assert.IsFalse(cleanupEntered.Task.IsCompleted);
            releaseSecondRoot.Set();
            await TestUiDispatcherHost.AwaitNotificationAsync(cleanupEntered.Task, cleanup, "audio-cleanup.acquired");
            await Task.WhenAll(secondRoot, cleanup);
            verified = true;
        }
        finally
        {
            releaseFirstRoot.Set();
            releaseSecondRoot.Set();
            try
            {
                await Task.WhenAll(firstRoot, secondRoot, cleanup);
            }
            catch when (!verified)
            {
                // 本体で観測した失敗を回収時のworker失敗で上書きしない。
            }
        }
    }

    /// <summary>cleanupの実lease保持中は初期化workerの取得直前到達後も取得・終端しないことを否定観測し、解放後の実取得と終端を確認します。</summary>
    [TestMethod]
    public async Task OperationGate_NoSessionCleanupRemainsExclusiveAgainstInitialization()
    {
        var gate = new BassAudioOperationGate(initiallyOpen: true);
        using var releaseCleanup = new ManualResetEventSlim();
        var cleanupEntered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var initializationAttempted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var initializationEntered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        Task cleanup = Task.CompletedTask;
        Task initialization = Task.CompletedTask;
        bool verified = false;
        try
        {
            cleanup = Task.Factory.StartNew(
                () =>
                {
                    Assert.IsTrue(gate.TryEnterSessionCleanup(out BassAudioExclusiveLease lifecycle));
                    using (lifecycle)
                    {
                        cleanupEntered.TrySetResult();
                        releaseCleanup.Wait();
                        lifecycle.Complete(success: true);
                    }
                },
                CancellationToken.None,
                TaskCreationOptions.LongRunning,
                TaskScheduler.Default);
            await TestUiDispatcherHost.AwaitNotificationAsync(cleanupEntered.Task, cleanup, "audio-initialization.cleanup-acquired");
            initialization = Task.Factory.StartNew(
                () =>
                {
                    initializationAttempted.TrySetResult();
                    using BassAudioExclusiveLease lifecycle = gate.EnterSessionInitialization();
                    initializationEntered.TrySetResult();
                    lifecycle.Complete(success: true);
                },
                CancellationToken.None,
                TaskCreationOptions.LongRunning,
                TaskScheduler.Default);
            await TestUiDispatcherHost.AwaitNotificationAsync(initializationAttempted.Task, initialization, "audio-initialization.before-acquire");
            // 取得直前通知後の禁止期間を観測する。通常完了を時間経過から推定しない。
            Assert.IsFalse(initialization.Wait(TimeSpan.FromMilliseconds(100)));
            Assert.IsFalse(initializationEntered.Task.IsCompleted);
            releaseCleanup.Set();
            await TestUiDispatcherHost.AwaitNotificationAsync(initializationEntered.Task, initialization, "audio-initialization.acquired");
            await Task.WhenAll(cleanup, initialization);
            verified = true;
        }
        finally
        {
            releaseCleanup.Set();
            try
            {
                await Task.WhenAll(cleanup, initialization);
            }
            catch when (!verified)
            {
                // 本体で観測した失敗を回収時のworker失敗で上書きしない。
            }
        }
    }

    /// <summary>runtime初期化ではcleanup隔離を解除できず、明示cleanup成功でだけ新要求を再開する。</summary>
    [TestMethod]
    public void OperationGate_RuntimeInitializationCannotClearCleanupQuarantine()
    {
        var gate = new BassAudioOperationGate(initiallyOpen: true);
        Assert.IsTrue(gate.TryEnterSessionCleanup(out BassAudioExclusiveLease failedCleanup));
        failedCleanup.Complete(success: false);
        failedCleanup.Dispose();

        Assert.IsTrue(gate.AdmissionClosed);
        Assert.ThrowsException<InvalidOperationException>(() =>
        {
            gate.EnterRuntimeInitialization();
        });
        Assert.IsTrue(gate.AdmissionClosed);

        Assert.IsTrue(gate.TryEnterSessionCleanup(out BassAudioExclusiveLease retryCleanup));
        retryCleanup.Complete(success: true);
        retryCleanup.Dispose();
        Assert.IsFalse(gate.AdmissionClosed);
    }

    /// <summary>cleanup隔離中は別rootを拒否し、受理済み要求の継続と明示cleanupは許して成功後に次要求を受け入れる。</summary>
    [TestMethod]
    public void OperationGate_CleanupQuarantineRejectsNewRootsButAllowsAcceptedContinuationAndCleanup()
    {
        var gate = new BassAudioOperationGate(initiallyOpen: true);
        Assert.IsTrue(gate.TryEnterRequest(out IDisposable admission));
        using (admission)
        {
            using (BassAudioExclusiveLease initialization = gate.EnterSessionInitialization())
            {
                initialization.Complete(success: false);
            }

            Assert.IsTrue(gate.IsCleanupQuarantined);
            Assert.IsTrue(gate.TryEnterRequest(out IDisposable continuation));
            continuation.Dispose();

            using var newRootAttempted = new ManualResetEventSlim();
            bool newRootAccepted = false;
            Exception? newRootFailure = null;
            ThreadPool.UnsafeQueueUserWorkItem(
                _ =>
                {
                    try
                    {
                        newRootAccepted = gate.TryEnterRequest(out IDisposable newRoot);
                        if (newRootAccepted)
                        {
                            newRoot.Dispose();
                        }
                    }
                    catch (Exception exception)
                    {
                        newRootFailure = exception;
                    }
                    finally
                    {
                        newRootAttempted.Set();
                    }
                },
                null);
            newRootAttempted.Wait();
            if (newRootFailure != null)
            {
                throw new InvalidOperationException("A separate audio request attempt failed.", newRootFailure);
            }
            Assert.IsFalse(newRootAccepted);

            Assert.IsTrue(gate.TryEnterSessionCleanup(out BassAudioExclusiveLease cleanup));
            using (cleanup)
            {
                cleanup.Complete(success: true);
            }
            Assert.IsFalse(gate.IsCleanupQuarantined);
        }

        Assert.IsTrue(gate.TryEnterRequest(out IDisposable nextRequest));
        nextRequest.Dispose();
    }

    /// <summary>初期化中のshutdown要求は初期化完了でも受付を再開せず、shutdown終端後も新操作を拒否する。</summary>
    [TestMethod]
    public void OperationGate_WaitingShutdownKeepsAdmissionClosedWhenRuntimeInitializationCompletes()
    {
        // runtime初期化とshutdown leaseの排他遷移が停止した場合を検出するため、遷移待ちに短い期限を使う。
        var gate = new BassAudioOperationGate();
        using var initializationEntered = new ManualResetEventSlim();
        using var releaseInitialization = new ManualResetEventSlim();
        using var shutdownEntered = new ManualResetEventSlim();
        Task initialization = Task.Factory.StartNew(
            () =>
            {
                using BassAudioExclusiveLease lifecycle = gate.EnterRuntimeInitialization();
                initializationEntered.Set();
                releaseInitialization.Wait();
                lifecycle.Complete(success: true);
            },
            CancellationToken.None,
            TaskCreationOptions.LongRunning,
            TaskScheduler.Default);

        Assert.IsTrue(initializationEntered.Wait(TimeSpan.FromSeconds(2)));
        Task shutdown = Task.Factory.StartNew(
            () =>
            {
                using BassAudioExclusiveLease lifecycle = gate.EnterRuntimeShutdown();
                shutdownEntered.Set();
                lifecycle.Complete(success: true);
            },
            CancellationToken.None,
            TaskCreationOptions.LongRunning,
            TaskScheduler.Default);

        try
        {
            Assert.IsTrue(gate.WaitForShutdownRequest(TimeSpan.FromSeconds(2)));
            Assert.IsFalse(gate.TryEnterOperation(out _));
            releaseInitialization.Set();
            Assert.IsTrue(shutdownEntered.Wait(TimeSpan.FromSeconds(2)));
            Assert.IsTrue(Task.WaitAll([initialization, shutdown], TimeSpan.FromSeconds(2)));
            Assert.IsFalse(gate.TryEnterOperation(out _));
        }
        finally
        {
            releaseInitialization.Set();
        }
    }

    /// <summary>player stream解放失敗ではmanaged ownerと未通知状態を保持し、native解放成功後に一回だけownerへ確認を渡す。</summary>
    [TestMethod]
    public void PlayerStreamCleanup_RetainsManagedOwnerUntilNativeReleaseSucceeds()
    {
        var session = new BassAudioSession(BassAudioPlayer.DeviceDriver.WASAPI_SHARED)
        {
            ActualBackend = BassAudioPlayer.DeviceDriver.WASAPI_SHARED,
            CoreInitialized = true,
            CoreDeviceIndex = 2
        };
        object owner = new object();
        int confirmations = 0;
        session.TrackPlayerStream(91, owner, _ => confirmations++);
        var native = new RecordingNativeBoundary
        {
            FreeStreamResult = false,
            CoreError = Errors.Unknown
        };

        Assert.IsFalse(BassAudioSessionCleanup.Release(session, native));
        Assert.AreEqual(1, session.GetPlayerStreams().Count);
        Assert.AreEqual(0, confirmations);

        native.FreeStreamResult = true;
        Assert.IsTrue(BassAudioSessionCleanup.Release(session, native));
        Assert.AreEqual(0, session.GetPlayerStreams().Count);
        Assert.AreEqual(1, confirmations);
        GC.KeepAlive(owner);
    }

    /// <summary>native入力bufferはstream解放未確認中に読める所有を保ち、明示解放成功後に新viewを拒否する。</summary>
    [TestMethod]
    public void PlayerStreamCleanup_RetainsNativeAudioInputUntilReleaseIsConfirmed()
    {
        string path = Path.GetTempFileName();
        AudioInputFile? input = null;
        try
        {
            File.WriteAllBytes(path, [1, 2, 3, 4]);
            input = AudioInputFile.Read(path);
            var session = new BassAudioSession(BassAudioPlayer.DeviceDriver.WASAPI_SHARED)
            {
                ActualBackend = BassAudioPlayer.DeviceDriver.WASAPI_SHARED,
                CoreInitialized = true,
                CoreDeviceIndex = 2
            };
            session.TrackPlayerStream(93, input, input.ConfirmNativeRelease);
            input.TransferToSession();
            var native = new RecordingNativeBoundary
            {
                FreeStreamResult = false,
                CoreError = Errors.Unknown
            };

            Assert.IsFalse(BassAudioSessionCleanup.Release(session, native));
            using (Stream retained = input.OpenReadView())
            {
                Assert.AreEqual(4L, retained.Length);
            }

            native.FreeStreamResult = true;
            Assert.IsTrue(BassAudioSessionCleanup.Release(session, native));
            Assert.IsTrue(session.IsReleased);
            Assert.ThrowsException<ObjectDisposedException>(() => input.OpenReadView());
        }
        finally
        {
            input?.Dispose();
            File.Delete(path);
        }
    }

    /// <summary>cleanup補助登録は未所有handleだけを受け取り、重複登録でownerを奪わず一回だけ解放通知する。</summary>
    [TestMethod]
    public void PlayerStreamCleanupFallback_RetainsOnlyUnownedHandle()
    {
        var session = new BassAudioSession(BassAudioPlayer.DeviceDriver.WASAPI_SHARED);
        object owner = new object();
        int notifications = 0;

        Assert.IsTrue(session.TryTrackPlayerStreamForCleanup(
            92,
            owner,
            _ => notifications++,
            out bool alreadyOwned));
        Assert.IsFalse(alreadyOwned);
        Assert.IsFalse(session.TryTrackPlayerStreamForCleanup(
            92,
            new object(),
            _ => notifications++,
            out alreadyOwned));
        Assert.IsTrue(alreadyOwned);
        Assert.AreEqual(1, session.GetPlayerStreams().Count);

        session.ConfirmPlayerStreamReleased(92);
        Assert.AreEqual(1, notifications);
        Assert.AreEqual(0, session.GetPlayerStreams().Count);
        GC.KeepAlive(owner);
    }

    /// <summary>stream解放callbackから別threadが所有一覧を読めることを確認し、registry lock保持中のowner呼出しによるdeadlockを検出する。</summary>
    [TestMethod]
    public async Task StreamReleaseCallbackRunsOutsideTheOwnershipRegistryLock()
    {
        var session = new BassAudioSession(BassAudioPlayer.DeviceDriver.WASAPI_SHARED);
        bool callbackCouldInspectFromAnotherThread = false;
        Task<IReadOnlyList<BassAudioOwnedStream>>? snapshot = null;
        session.TrackPlayerStream(94, new object(), _ =>
        {
            Task<IReadOnlyList<BassAudioOwnedStream>> inspection = Task.Run(session.GetOwnedStreams);
            snapshot = inspection;
            // callback中の別thread照会がregistry lockに塞がれる不具合を検出する期限。
            Assert.IsTrue(inspection.Wait(TimeSpan.FromSeconds(5)),
                "A release callback must not hold the stream registry lock while it calls its owner.");
            callbackCouldInspectFromAnotherThread = inspection.Result.Count == 0;
        });

        var failures = new List<Exception>();
        try
        {
            session.ConfirmPlayerStreamReleased(94);
            Assert.IsTrue(callbackCouldInspectFromAnotherThread);
        }
        catch (Exception exception)
        {
            failures.Add(exception);
        }

        if (snapshot != null)
        {
            try
            {
                await snapshot.ConfigureAwait(false);
            }
            catch (Exception exception)
            {
                failures.Add(exception);
            }
        }

        if (failures.Count == 1)
        {
            System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(failures[0]).Throw();
        }
        else if (failures.Count > 1)
        {
            throw new AggregateException("The test failure and task cleanup failure are retained.", failures);
        }
    }

    /// <summary>同時開始をlifecycleで直列化し、一つだけがnative所有を取得して二重初期化しない。</summary>
    [TestMethod]
    public async Task ConcurrentInitialization_OnlyOneSessionAcquiresNativeOwnership()
    {
        var lifecycle = new BassAudioSessionLifecycle();
        using var ready = new Barrier(2);
        int nativeInitializationCount = 0;

        Task<bool> TryInitializeAsync()
        {
            return Task.Run(() =>
            {
                ready.SignalAndWait();
                using (lifecycle.Enter())
                {
                    if (!lifecycle.TryBegin(
                        BassAudioPlayer.DeviceDriver.WASAPI_SHARED,
                        default,
                        out BassAudioSession session))
                    {
                        return false;
                    }

                    Interlocked.Increment(ref nativeInitializationCount);
                    session.ActualBackend = BassAudioPlayer.DeviceDriver.WASAPI_SHARED;
                    session.CoreInitialized = true;
                    session.CoreDeviceIndex = 1;
                    lifecycle.MarkActive(session);
                    return true;
                }
            });
        }

        bool[] results = await Task.WhenAll(TryInitializeAsync(), TryInitializeAsync());

        Assert.AreEqual(1, results.Count(result => result));
        Assert.AreEqual(1, nativeInitializationCount);
    }

    private sealed class RecordingNativeBoundary : IAudioSessionNativeBoundary
    {
        private readonly List<string> calls = [];

        internal List<int> SelectedCoreDeviceIndices { get; } = [];

        internal List<int> SelectedWasapiDeviceIndices { get; } = [];

        internal List<int> SelectedAsioDeviceIndices { get; } = [];

        internal bool SetCoreDeviceResult { get; set; } = true;

        internal bool FreeCoreResult { get; set; } = true;

        internal bool FreeStreamResult { get; set; } = true;

        internal bool FreeWasapiResult { get; set; } = true;

        internal Errors CoreError { get; set; } = Errors.OK;

        internal Errors WasapiError { get; set; } = Errors.OK;

        public bool SetCoreDevice(int deviceIndex)
        {
            calls.Add("SetCoreDevice");
            SelectedCoreDeviceIndices.Add(deviceIndex);
            return SetCoreDeviceResult;
        }

        public bool FreeCore()
        {
            calls.Add("FreeCore");
            return FreeCoreResult;
        }

        public Errors GetCoreError() => CoreError;

        public bool SetWasapiDevice(int deviceIndex)
        {
            calls.Add("SetWasapiDevice");
            SelectedWasapiDeviceIndices.Add(deviceIndex);
            return true;
        }

        public bool StopWasapi(bool reset)
        {
            calls.Add("StopWasapi");
            return true;
        }

        public bool FreeWasapi()
        {
            calls.Add("FreeWasapi");
            return FreeWasapiResult;
        }

        public Errors GetWasapiError() => WasapiError;

        public bool SetAsioDevice(int deviceIndex)
        {
            calls.Add("SetAsioDevice");
            SelectedAsioDeviceIndices.Add(deviceIndex);
            return true;
        }

        public bool StopAsio()
        {
            calls.Add("StopAsio");
            return true;
        }

        public bool FreeAsio()
        {
            calls.Add("FreeAsio");
            return true;
        }

        public Errors GetAsioError() => Errors.OK;

        public bool FreeStream(int handle)
        {
            calls.Add("FreeStream");
            return FreeStreamResult;
        }

        public Errors GetStreamError() => Errors.OK;

        internal int Count(string operation) => calls.Count(call => call == operation);
    }

    private static AudioDeviceCapabilityRequest CreateAsioCapabilityRequest() => new(
        AudioDriver.Asio,
        "asio-id",
        "ASIO",
        SampleRate.SAMPLE_RATE_48000Hz,
        SampleFormat.SAMPLE_INT_24BIT);
}
