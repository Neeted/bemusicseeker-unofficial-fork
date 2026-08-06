using System;
using System.Collections.Generic;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Ribbit.Media;
using Ribbit.Media.Audio;
using Un4seen.Bass;

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
            Error = BASSError.BASS_ERROR_HANDLE
        };
        native.MixerReads.Enqueue(0);
        native.MixerReads.Enqueue(77);
        native.ErrorReads.Enqueue(BASSError.BASS_ERROR_HANDLE);
        var controller = new BassMixerSourceController(native);

        BassMixerSourceAttachment attachment = controller.EnsureAttachedPaused(77, 12, "test.wav");

        Assert.IsTrue(attachment.NewlyAttached);
        Assert.AreEqual(77, attachment.ActualMixerHandle);
        Assert.AreEqual(1, native.AddCalls);
        Assert.AreEqual(BASSFlag.BASS_MIXER_CHAN_PAUSE, native.LastAddFlags);
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
            Error = BASSError.BASS_ERROR_ALREADY,
            AddResult = false
        };
        native.MixerReads.Enqueue(0);
        native.MixerReads.Enqueue(77);
        native.ErrorReads.Enqueue(BASSError.BASS_ERROR_HANDLE);
        native.ErrorReads.Enqueue(BASSError.BASS_ERROR_ALREADY);
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
            Error = BASSError.BASS_ERROR_FILEOPEN,
            AddResult = false
        };
        native.MixerReads.Enqueue(0);
        native.ErrorReads.Enqueue(BASSError.BASS_ERROR_HANDLE);
        var controller = new BassMixerSourceController(native);

        BassAudioPlaybackException exception = Assert.ThrowsException<BassAudioPlaybackException>(
            () => controller.EnsureAttachedPaused(77, 12, "test.wav"));

        Assert.AreEqual(BassAudioPlaybackStage.MixerAttach, exception.Stage);
        Assert.AreEqual(BASSError.BASS_ERROR_FILEOPEN, exception.NativeErrorCode);
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
        native.ErrorReads.Enqueue(BASSError.BASS_ERROR_HANDLE);
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
        Assert.AreEqual(BASSFlag.BASS_MIXER_CHAN_PAUSE, native.LastFlags);
        Assert.AreEqual(BASSFlag.BASS_MIXER_CHAN_PAUSE, native.LastMask);

        controller.Resume(77, 12, "test.wav");
        Assert.AreEqual(BASSFlag.BASS_DEFAULT, native.LastFlags);

        native.FlagsResult = (BASSFlag)(-1);
        native.Error = BASSError.BASS_ERROR_HANDLE;
        BassAudioPlaybackException exception = Assert.ThrowsException<BassAudioPlaybackException>(
            () => controller.Resume(77, 12, "test.wav"));
        Assert.AreEqual(BassAudioPlaybackStage.MixerResume, exception.Stage);
        Assert.AreEqual(BASSError.BASS_ERROR_HANDLE, exception.NativeErrorCode);
    }

    [TestMethod]
    public void RemoveFromExpectedMixerIsVerifiedAndAlreadyDetachedIsIdempotent()
    {
        var native = new FakeNativeBoundary
        {
            MixerHandle = 77,
            Error = BASSError.BASS_ERROR_HANDLE
        };
        native.MixerReads.Enqueue(77);
        native.MixerReads.Enqueue(0);
        native.ErrorReads.Enqueue(BASSError.BASS_ERROR_HANDLE);
        native.ErrorReads.Enqueue(BASSError.BASS_ERROR_HANDLE);
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

        internal BASSError Error { get; set; } = BASSError.BASS_OK;

        internal bool AddResult { get; set; } = true;

        internal BASSFlag FlagsResult { get; set; } = BASSFlag.BASS_DEFAULT;

        internal Queue<int> MixerReads { get; } = new();

        internal Queue<BASSError> ErrorReads { get; } = new();

        internal int AddCalls { get; private set; }

        internal int RemoveCalls { get; private set; }

        internal BASSFlag LastAddFlags { get; private set; }

        internal BASSFlag LastFlags { get; private set; }

        internal BASSFlag LastMask { get; private set; }

        public int GetMixer(int sourceHandle)
            => MixerReads.Count == 0 ? MixerHandle : MixerReads.Dequeue();

        public bool AddChannel(int mixerHandle, int sourceHandle, BASSFlag flags)
        {
            AddCalls++;
            LastAddFlags = flags;
            if (AddResult)
            {
                MixerHandle = mixerHandle;
            }
            return AddResult;
        }

        public BASSFlag SetMixerChannelFlags(int sourceHandle, BASSFlag flags, BASSFlag mask)
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

        public bool SetPosition(int sourceHandle, long position) => true;

        public BASSError GetError()
            => ErrorReads.Count == 0 ? Error : ErrorReads.Dequeue();
    }
}
