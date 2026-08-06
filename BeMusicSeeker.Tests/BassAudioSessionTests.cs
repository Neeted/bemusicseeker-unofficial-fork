using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Ribbit.Media;
using Ribbit.Media.Audio;
using Un4seen.Bass;

namespace BeMusicSeeker.Tests;

[TestClass]
public sealed class BassAudioSessionTests
{
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
            CoreError = BASSError.BASS_ERROR_UNKNOWN
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
        session.AdditionalStreamHandles.Add(202);
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
            CoreError = BASSError.BASS_ERROR_DEVICE
        };

        Assert.IsFalse(BassAudioSessionCleanup.Release(session, native));

        Assert.AreEqual(BassAudioSessionState.CleanupPending, session.State);
        Assert.IsTrue(session.HasNativeOwnership);
        Assert.AreEqual(1, native.Count("SetCoreDevice"));
        Assert.AreEqual(0, native.Count("FreeStream"));
        Assert.AreEqual(0, native.Count("FreeCore"));
    }

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
            CoreError = BASSError.BASS_ERROR_INIT
        };

        Assert.IsTrue(BassAudioSessionCleanup.Release(session, native));
        Assert.IsTrue(BassAudioSessionCleanup.Release(session, native));

        Assert.IsTrue(session.IsReleased);
        Assert.AreEqual(1, native.Count("FreeCore"));
    }

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
        session.AdditionalStreamHandles.Add(43);
        session.TrackPlayerStream(44, new object(), _ => playerReleaseNotifications++);
        var native = new RecordingNativeBoundary
        {
            SetCoreDeviceResult = false,
            CoreError = BASSError.BASS_ERROR_INIT
        };

        Assert.IsTrue(BassAudioSessionCleanup.Release(session, native));

        Assert.IsTrue(session.IsReleased);
        Assert.AreEqual(7, native.SelectedCoreDeviceIndices.Single());
        Assert.AreEqual(0, native.Count("FreeStream"));
        Assert.AreEqual(0, native.Count("FreeCore"));
        Assert.AreEqual(1, playerReleaseNotifications);
    }

    [TestMethod]
    public void AliasedStreamHandle_IsFreedAndConfirmedExactlyOnce()
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
        session.AdditionalStreamHandles.Add(51);
        session.TrackPlayerStream(51, new object(), _ => playerReleaseNotifications++);
        var native = new RecordingNativeBoundary();

        Assert.IsTrue(BassAudioSessionCleanup.Release(session, native));

        Assert.IsTrue(session.IsReleased);
        Assert.AreEqual(1, native.Count("FreeStream"));
        Assert.AreEqual(1, native.Count("FreeCore"));
        Assert.AreEqual(1, playerReleaseNotifications);
    }

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

    [TestMethod]
    public void PublishedCallbackOutputReader_UsesSessionHandleAndClampsNativeResult()
    {
        var session = new BassAudioSession(BassAudioPlayer.DeviceDriver.ASIO);
        session.TrackOutputHandle(123);
        int observedHandle = 0;

        int bytesRead = BassAudioPlayer.ReadPublishedCallbackOutput(
            session,
            IntPtr.Zero,
            16,
            (handle, _, _) =>
            {
                observedHandle = handle;
                return 7;
            });

        Assert.AreEqual(123, observedHandle);
        Assert.AreEqual(7, bytesRead);

        Assert.AreEqual(
            0,
            BassAudioPlayer.ReadPublishedCallbackOutput(
                session,
                IntPtr.Zero,
                16,
                (_, _, _) => -1));
    }

    [TestMethod]
    public void PublishedCallbackOutputReader_DoesNotReadWithoutSessionHandle()
    {
        int readCalls = 0;
        var session = new BassAudioSession(BassAudioPlayer.DeviceDriver.ASIO);
        Func<int, IntPtr, int, int> readData = (_, _, _) =>
        {
            readCalls++;
            return 1;
        };

        Assert.AreEqual(0, BassAudioPlayer.ReadPublishedCallbackOutput(null, IntPtr.Zero, 16, readData));
        Assert.AreEqual(0, BassAudioPlayer.ReadPublishedCallbackOutput(session, IntPtr.Zero, 16, readData));
        Assert.AreEqual(0, readCalls);
    }

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
            WasapiError = BASSError.BASS_ERROR_UNKNOWN
        };

        Assert.IsFalse(BassAudioSessionCleanup.Release(session, native));
        Assert.AreEqual(123, session.CallbackOutputHandle);
        Assert.IsTrue(session.HasNativeOwnership);

        native.FreeWasapiResult = true;
        Assert.IsTrue(BassAudioSessionCleanup.Release(session, native));
        Assert.AreEqual(0, session.CallbackOutputHandle);
        Assert.IsTrue(session.IsReleased);
    }

    [TestMethod]
    public void CleanupFailure_RemainsOwnedUntilLaterRetrySucceeds()
    {
        var lifecycle = new BassAudioSessionLifecycle();
        var native = new RecordingNativeBoundary
        {
            FreeCoreResult = false,
            CoreError = BASSError.BASS_ERROR_UNKNOWN
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

    [TestMethod]
    public void OutputHandleReplacement_ForgetsOnlyConfirmedRelease()
    {
        var session = new BassAudioSession(BassAudioPlayer.DeviceDriver.WASAPI_SHARED)
        {
            MixerHandle = 10,
            OutputHandle = 20,
            VolumeEffectHandle = 30
        };

        session.ConfirmStreamReleased(20);
        session.TrackOutputHandle(30);

        Assert.AreEqual(10, session.MixerHandle);
        Assert.AreEqual(30, session.OutputHandle);
        Assert.AreEqual(30, session.VolumeEffectHandle);
        Assert.IsFalse(session.AdditionalStreamHandles.Contains(20));

        session.ConfirmStreamReleased(10);

        Assert.AreEqual(0, session.MixerHandle);
        Assert.AreEqual(0, session.VolumeEffectHandle);
    }

    [TestMethod]
    public void OperationGate_ShutdownAllowsNestedCallAndRejectsNewRootUntilDrain()
    {
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

    [TestMethod]
    public void OperationGate_SessionCleanupDrainsAllRootsAndRejectsSharedPromotion()
    {
        var gate = new BassAudioOperationGate(initiallyOpen: true);
        Assert.IsTrue(gate.TryEnterOperation(out BassAudioOperationLease ambient));
        using (ambient)
        {
            Assert.IsFalse(gate.TryEnterSessionCleanup(out _));
        }

        using var rootsReady = new Barrier(3);
        using var releaseRoots = new ManualResetEventSlim();
        Task CreateRoot()
        {
            return Task.Factory.StartNew(
                () =>
                {
                    Assert.IsTrue(gate.TryEnterOperation(out BassAudioOperationLease operation));
                    using (operation)
                    {
                        rootsReady.SignalAndWait();
                        releaseRoots.Wait();
                    }
                },
                CancellationToken.None,
                TaskCreationOptions.LongRunning,
                TaskScheduler.Default);
        }

        Task firstRoot = CreateRoot();
        Task secondRoot = CreateRoot();
        rootsReady.SignalAndWait();
        using var cleanupEntered = new ManualResetEventSlim();
        Task cleanup = Task.Factory.StartNew(
            () =>
            {
                Assert.IsTrue(gate.TryEnterSessionCleanup(out BassAudioExclusiveLease lifecycle));
                try
                {
                    cleanupEntered.Set();
                    lifecycle.Complete(success: true);
                }
                finally
                {
                    lifecycle.Dispose();
                }
            },
            CancellationToken.None,
            TaskCreationOptions.LongRunning,
            TaskScheduler.Default);

        try
        {
            Assert.IsFalse(cleanupEntered.IsSet);
            releaseRoots.Set();
            Assert.IsTrue(cleanupEntered.Wait(TimeSpan.FromSeconds(2)));
            Assert.IsTrue(Task.WaitAll([firstRoot, secondRoot, cleanup], TimeSpan.FromSeconds(2)));
        }
        finally
        {
            releaseRoots.Set();
        }
    }

    [TestMethod]
    public void OperationGate_NoSessionCleanupRemainsExclusiveAgainstInitialization()
    {
        var gate = new BassAudioOperationGate(initiallyOpen: true);
        using var cleanupEntered = new ManualResetEventSlim();
        using var releaseCleanup = new ManualResetEventSlim();
        using var initializationAttempted = new ManualResetEventSlim();
        using var initializationEntered = new ManualResetEventSlim();
        Task cleanup = Task.Factory.StartNew(
            () =>
            {
                Assert.IsTrue(gate.TryEnterSessionCleanup(out BassAudioExclusiveLease lifecycle));
                try
                {
                    cleanupEntered.Set();
                    releaseCleanup.Wait();
                    lifecycle.Complete(success: true);
                }
                finally
                {
                    lifecycle.Dispose();
                }
            },
            CancellationToken.None,
            TaskCreationOptions.LongRunning,
            TaskScheduler.Default);

        Assert.IsTrue(cleanupEntered.Wait(TimeSpan.FromSeconds(2)));
        Task initialization = Task.Factory.StartNew(
            () =>
            {
                initializationAttempted.Set();
                using BassAudioExclusiveLease lifecycle = gate.EnterSessionInitialization();
                initializationEntered.Set();
                lifecycle.Complete(success: true);
            },
            CancellationToken.None,
            TaskCreationOptions.LongRunning,
            TaskScheduler.Default);

        try
        {
            Assert.IsTrue(initializationAttempted.Wait(TimeSpan.FromSeconds(2)));
            Assert.IsFalse(initializationEntered.IsSet);
            releaseCleanup.Set();
            Assert.IsTrue(cleanup.Wait(TimeSpan.FromSeconds(2)));
            Assert.IsTrue(initializationEntered.Wait(TimeSpan.FromSeconds(2)));
            Assert.IsTrue(initialization.Wait(TimeSpan.FromSeconds(2)));
        }
        finally
        {
            releaseCleanup.Set();
        }
    }

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

    [TestMethod]
    public void OperationGate_WaitingShutdownKeepsAdmissionClosedWhenRuntimeInitializationCompletes()
    {
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

    [TestMethod]
    public void PlayerStreamCleanup_RetainsManagedOwnerUntilNativeReleaseSucceeds()
    {
        var session = new BassAudioSession(BassAudioPlayer.DeviceDriver.WASAPI_SHARED)
        {
            ActualBackend = BassAudioPlayer.DeviceDriver.WASAPI_SHARED,
            CoreInitialized = true,
            CoreDeviceIndex = 2
        };
        var owner = new object();
        int confirmations = 0;
        session.TrackPlayerStream(91, owner, _ => confirmations++);
        var native = new RecordingNativeBoundary
        {
            FreeStreamResult = false,
            CoreError = BASSError.BASS_ERROR_UNKNOWN
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

    [TestMethod]
    public void PlayerStreamCleanupFallback_RetainsOnlyUnownedHandle()
    {
        var session = new BassAudioSession(BassAudioPlayer.DeviceDriver.WASAPI_SHARED);
        var owner = new object();
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

        internal BASSError CoreError { get; set; } = BASSError.BASS_OK;

        internal BASSError WasapiError { get; set; } = BASSError.BASS_OK;

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

        public BASSError GetCoreError() => CoreError;

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

        public BASSError GetWasapiError() => WasapiError;

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

        public BASSError GetAsioError() => BASSError.BASS_OK;

        public bool FreeStream(int handle)
        {
            calls.Add("FreeStream");
            return FreeStreamResult;
        }

        public BASSError GetStreamError() => BASSError.BASS_OK;

        internal int Count(string operation) => calls.Count(call => call == operation);
    }
}
