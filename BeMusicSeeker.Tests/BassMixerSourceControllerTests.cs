using System;
using System.Collections.Generic;
using ManagedBass;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Ribbit.Media;
using Ribbit.Media.Audio;

namespace BeMusicSeeker.Tests;

[TestClass]
public sealed class BassMixerSourceControllerTests
{
    [TestMethod]
    public void EnsureAttachedPaused_UnattachedSourceUsesPauseAndVerifiesMembership()
    {
        var native = new FakeNativeBoundary
        {
            MixerHandle = 0,
            Error = Errors.Handle
        };
        native.MixerReads.Enqueue(0);
        native.MixerReads.Enqueue(77);
        native.ErrorReads.Enqueue(Errors.Handle);
        var controller = new BassMixerSourceController(native);

        BassMixerSourceAttachment attachment = controller.EnsureAttachedPaused(77, 12, "test.wav");

        Assert.IsTrue(attachment.NewlyAttached);
        Assert.AreEqual(77, attachment.ActualMixerHandle);
        Assert.AreEqual(1, native.AddCalls);
        Assert.AreEqual(BassFlags.MixerChanPause, native.LastAddFlags);
        Assert.AreEqual(77, native.MixerHandle);
    }

    [TestMethod]
    public void EnsureAttachedPaused_ExpectedMembershipDoesNotAddAgain()
    {
        var native = new FakeNativeBoundary { MixerHandle = 77 };
        var controller = new BassMixerSourceController(native);

        BassMixerSourceAttachment attachment = controller.EnsureAttachedPaused(77, 12, "test.wav");

        Assert.IsFalse(attachment.NewlyAttached);
        Assert.AreEqual(77, attachment.ActualMixerHandle);
        Assert.AreEqual(0, native.AddCalls);
    }

    [TestMethod]
    public void EnsureAttachedPaused_DifferentMembershipFailsWithoutRemovingIt()
    {
        var native = new FakeNativeBoundary { MixerHandle = 88 };
        var controller = new BassMixerSourceController(native);

        BassAudioPlaybackException exception = Assert.ThrowsException<BassAudioPlaybackException>(
            () => controller.EnsureAttachedPaused(77, 12, "test.wav"));

        Assert.AreEqual(BassAudioPlaybackStage.MixerMembership, exception.Stage);
        Assert.AreEqual(77, exception.ExpectedMixerHandle);
        Assert.AreEqual(88, exception.ActualMixerHandle);
        Assert.AreEqual(0, native.RemoveCalls);
        Assert.AreEqual(0, native.AddCalls);
    }

    [TestMethod]
    public void MixerFailureCapturesOwningSessionDiagnostics()
    {
        var session = new BassAudioSession(BassAudioPlayer.DeviceDriver.WASAPI_SHARED)
        {
            ActualBackend = BassAudioPlayer.DeviceDriver.WASAPI_SHARED,
            CoreDeviceIndex = 6,
            State = BassAudioSessionState.Active
        };
        var native = new FakeNativeBoundary { MixerHandle = 88 };
        var controller = new BassMixerSourceController(native, () => session);

        BassAudioPlaybackException exception = Assert.ThrowsException<BassAudioPlaybackException>(
            () => controller.EnsureAttachedPaused(77, 12, "test.wav"));

        Assert.AreEqual(BassAudioPlayer.DeviceDriver.WASAPI_SHARED, exception.Backend);
        Assert.AreEqual(BassAudioSessionState.Active, exception.SessionState);
        Assert.AreEqual(6, exception.CoreDeviceIndex);
    }

    [TestMethod]
    public void EnsureAttachedPaused_AlreadyRaceIsAcceptedOnlyAfterExpectedMembershipIsObserved()
    {
        var native = new FakeNativeBoundary
        {
            MixerHandle = 0,
            Error = Errors.Already,
            AddResult = false
        };
        native.MixerReads.Enqueue(0);
        native.MixerReads.Enqueue(77);
        native.ErrorReads.Enqueue(Errors.Handle);
        native.ErrorReads.Enqueue(Errors.Already);
        var controller = new BassMixerSourceController(native);

        BassMixerSourceAttachment attachment = controller.EnsureAttachedPaused(77, 12, "test.wav");

        Assert.IsFalse(attachment.NewlyAttached);
        Assert.AreEqual(77, attachment.ActualMixerHandle);
        Assert.AreEqual(1, native.AddCalls);
    }

    [TestMethod]
    public void EnsureAttachedPaused_AddFailurePreservesCapturedError()
    {
        var native = new FakeNativeBoundary
        {
            MixerHandle = 0,
            Error = Errors.FileOpen,
            AddResult = false
        };
        native.MixerReads.Enqueue(0);
        native.ErrorReads.Enqueue(Errors.Handle);
        var controller = new BassMixerSourceController(native);

        BassAudioPlaybackException exception = Assert.ThrowsException<BassAudioPlaybackException>(
            () => controller.EnsureAttachedPaused(77, 12, "test.wav"));

        Assert.AreEqual(BassAudioPlaybackStage.MixerAttach, exception.Stage);
        Assert.AreEqual(Errors.FileOpen, exception.NativeErrorCode);
    }

    [TestMethod]
    public void EnsureAttachedPaused_SuccessThatCannotBeVerifiedFails()
    {
        var native = new FakeNativeBoundary
        {
            MixerHandle = 0,
            AddResult = true
        };
        native.MixerReads.Enqueue(0);
        native.MixerReads.Enqueue(88);
        native.ErrorReads.Enqueue(Errors.Handle);
        var controller = new BassMixerSourceController(native);

        BassAudioPlaybackException exception = Assert.ThrowsException<BassAudioPlaybackException>(
            () => controller.EnsureAttachedPaused(77, 12, "test.wav"));

        Assert.AreEqual(BassAudioPlaybackStage.MixerAttach, exception.Stage);
        Assert.AreEqual(88, exception.ActualMixerHandle);
    }

    [TestMethod]
    public void PauseAndResumeUseMixerPauseFlagAndRejectNativeFailure()
    {
        var native = new FakeNativeBoundary { MixerHandle = 77 };
        var controller = new BassMixerSourceController(native);

        controller.Pause(77, 12, "test.wav");
        Assert.AreEqual(BassFlags.MixerChanPause, native.LastFlags);
        Assert.AreEqual(BassFlags.MixerChanPause, native.LastMask);

        controller.Resume(77, 12, "test.wav");
        Assert.AreEqual(BassFlags.Default, native.LastFlags);

        native.FlagsResult = unchecked((BassFlags)(-1));
        native.Error = Errors.Handle;
        BassAudioPlaybackException exception = Assert.ThrowsException<BassAudioPlaybackException>(
            () => controller.Resume(77, 12, "test.wav"));
        Assert.AreEqual(BassAudioPlaybackStage.MixerResume, exception.Stage);
        Assert.AreEqual(Errors.Handle, exception.NativeErrorCode);
    }

    [TestMethod]
    public void SetPositionUsesByteModeAndPreservesNativeFailure()
    {
        var native = new FakeNativeBoundary
        {
            SetPositionResult = false,
            Error = Errors.Position
        };
        var controller = new BassMixerSourceController(native);

        BassAudioPlaybackException exception = Assert.ThrowsException<BassAudioPlaybackException>(
            () => controller.SetPosition(12, 4096, "test.wav", 77));

        Assert.AreEqual(PositionFlags.Bytes, native.LastPositionFlags);
        Assert.AreEqual(4096L, native.LastPosition);
        Assert.AreEqual(BassAudioPlaybackStage.SetPosition, exception.Stage);
        Assert.AreEqual(Errors.Position, exception.NativeErrorCode);
    }

    [TestMethod]
    public void RemoveFromExpectedMixerIsVerifiedAndAlreadyDetachedIsIdempotent()
    {
        var native = new FakeNativeBoundary
        {
            MixerHandle = 77,
            Error = Errors.Handle
        };
        native.MixerReads.Enqueue(77);
        native.MixerReads.Enqueue(0);
        native.ErrorReads.Enqueue(Errors.Handle);
        native.ErrorReads.Enqueue(Errors.Handle);
        var controller = new BassMixerSourceController(native);

        BassMixerSourceRemoval removed = controller.RemoveFromExpectedMixer(77, 12, "test.wav");
        Assert.IsFalse(removed.AlreadyDetached);
        Assert.AreEqual(1, native.RemoveCalls);
        Assert.AreEqual(0, native.MixerHandle);

        BassMixerSourceRemoval alreadyDetached = controller.RemoveFromExpectedMixer(77, 12, "test.wav");
        Assert.IsTrue(alreadyDetached.AlreadyDetached);
        Assert.AreEqual(1, native.RemoveCalls);
    }

    private sealed class FakeNativeBoundary : IBassMixerSourceNativeBoundary
    {
        internal int MixerHandle { get; set; }

        internal Errors Error { get; set; } = Errors.OK;

        internal bool AddResult { get; set; } = true;

        internal BassFlags FlagsResult { get; set; } = BassFlags.Default;

        internal bool SetPositionResult { get; set; } = true;

        internal Queue<int> MixerReads { get; } = new();

        internal Queue<Errors> ErrorReads { get; } = new();

        internal int AddCalls { get; private set; }

        internal int RemoveCalls { get; private set; }

        internal BassFlags LastAddFlags { get; private set; }

        internal BassFlags LastFlags { get; private set; }

        internal BassFlags LastMask { get; private set; }

        internal long LastPosition { get; private set; }

        internal PositionFlags LastPositionFlags { get; private set; }

        public int GetMixer(int sourceHandle)
            => MixerReads.Count == 0 ? MixerHandle : MixerReads.Dequeue();

        public bool AddChannel(int mixerHandle, int sourceHandle, BassFlags flags)
        {
            AddCalls++;
            LastAddFlags = flags;
            if (AddResult)
            {
                MixerHandle = mixerHandle;
            }
            return AddResult;
        }

        public BassFlags SetMixerChannelFlags(int sourceHandle, BassFlags flags, BassFlags mask)
        {
            LastFlags = flags;
            LastMask = mask;
            return FlagsResult;
        }

        public bool RemoveChannel(int sourceHandle)
        {
            RemoveCalls++;
            MixerHandle = 0;
            return true;
        }

        public bool SetPosition(int sourceHandle, long position, PositionFlags mode)
        {
            LastPosition = position;
            LastPositionFlags = mode;
            return SetPositionResult;
        }

        public Errors GetError()
            => ErrorReads.Count == 0 ? Error : ErrorReads.Dequeue();
    }
}
