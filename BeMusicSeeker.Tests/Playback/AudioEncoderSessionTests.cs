using System;
using ManagedBass;
using ManagedBass.Enc;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Ribbit.Media.Audio;

namespace BeMusicSeeker.Tests;

/// <summary>
/// Verifies encoder handle ownership and state transitions through the narrow native boundary.
/// </summary>
[TestClass]
public sealed class AudioEncoderSessionTests
{
    [TestMethod]
    public void StartPublishesStartedOnlyAfterNotifyRegistration()
    {
        FakeNative native = new();
        using AudioEncoderSession session = CreateSession(native);

        session.Start();

        Assert.AreEqual(AudioEncoderSessionState.Started, session.State);
        Assert.AreEqual(7, session.EncoderHandle);
        Assert.IsTrue(session.NotifyRegistered);
        Assert.IsNotNull(native.NotifyProcedure);
    }

    [TestMethod]
    public void ZeroHandleStartRemainsStoppedAtWriterBoundaryAndRetainsNativeError()
    {
        FakeNative native = new() { NextHandle = 0, LastError = Errors.FileOpen };
        using AudioEncoderSession session = CreateSession(native);

        AudioEncoderException exception = Assert.ThrowsException<AudioEncoderException>(session.Start);

        Assert.AreEqual(AudioEncoderFailureStage.Start, exception.Stage);
        Assert.AreEqual(Errors.FileOpen, exception.NativeError);
        Assert.AreEqual(AudioEncoderSessionState.Faulted, session.State);
        Assert.AreEqual(0, session.EncoderHandle);
    }

    [TestMethod]
    public void NotifyRegistrationFailureReleasesTheStartedHandle()
    {
        FakeNative native = new() { NotifySucceeds = false, LastError = Errors.Parameter };
        using AudioEncoderSession session = CreateSession(native);

        AudioEncoderException exception = Assert.ThrowsException<AudioEncoderException>(session.Start);

        Assert.AreEqual(AudioEncoderFailureStage.NotifyRegistration, exception.Stage);
        Assert.AreEqual(1, native.StopCalls);
        Assert.AreEqual(AudioEncoderSessionState.Faulted, session.State);
        Assert.AreEqual(0, session.EncoderHandle);
    }

    [TestMethod]
    public void StopFailureLeavesNativeOwnershipInStartedState()
    {
        FakeNative native = new() { StopSucceeds = false, LastError = Errors.Handle };
        using AudioEncoderSession session = CreateSession(native);
        session.Start();

        AudioEncoderException exception = Assert.ThrowsException<AudioEncoderException>(session.Stop);

        Assert.AreEqual(AudioEncoderFailureStage.Stop, exception.Stage);
        Assert.AreEqual(Errors.Handle, exception.NativeError);
        Assert.AreEqual(AudioEncoderSessionState.Started, session.State);
        Assert.AreEqual(7, session.EncoderHandle);

        native.StopSucceeds = true;
        session.Stop();
    }

    [TestMethod]
    public void DisposeFailureRetainsNativeOwnershipForRetry()
    {
        FakeNative native = new() { StopSucceeds = false, LastError = Errors.Handle };
        AudioEncoderSession session = CreateSession(native);
        session.Start();

        AudioEncoderException exception = Assert.ThrowsException<AudioEncoderException>(session.Dispose);

        Assert.AreEqual(AudioEncoderFailureStage.Dispose, exception.Stage);
        Assert.AreEqual(AudioEncoderSessionState.Started, session.State);
        Assert.AreEqual(7, session.EncoderHandle);

        native.StopSucceeds = true;
        session.Dispose();

        Assert.AreEqual(AudioEncoderSessionState.Disposed, session.State);
        Assert.AreEqual(0, session.EncoderHandle);
    }

    [TestMethod]
    public void EncoderDeathNotificationIsRetainedAsTypedFailure()
    {
        FakeNative native = new();
        using AudioEncoderSession session = CreateSession(native);
        session.Start();

        native.NotifyProcedure(7, EncodeNotifyStatus.EncoderDied, IntPtr.Zero);

        Assert.AreEqual(EncodeNotifyStatus.EncoderDied, session.LastNotifyStatus);
        session.Stop();
        Assert.AreEqual(AudioEncoderSessionState.Stopped, session.State);
    }

    [TestMethod]
    public void EnsureActiveAfterRenderAcceptsAPlayingEncoder()
    {
        FakeNative native = new();
        using AudioEncoderSession session = CreateSession(native);
        session.Start();

        session.EnsureActiveAfterRender();

        Assert.AreEqual(AudioEncoderSessionState.Started, session.State);
        Assert.AreEqual(1, native.EncodeIsActiveCalls);
    }

    [TestMethod]
    public void EnsureActiveAfterRenderRaisesTypedFailureWhenEncoderStops()
    {
        FakeNative native = new()
        {
            ActiveState = PlaybackState.Stopped,
            LastError = Errors.Handle
        };
        using AudioEncoderSession session = CreateSession(native);
        session.Start();

        AudioEncoderException exception = Assert.ThrowsException<AudioEncoderException>(
            session.EnsureActiveAfterRender);

        Assert.AreEqual(AudioEncoderFailureStage.EncoderDied, exception.Stage);
        Assert.AreEqual(Errors.Handle, exception.NativeError);
        Assert.AreEqual(AudioEncoderSessionState.Faulted, session.State);
    }

    [TestMethod]
    public void EnsureActiveAfterRenderRaisesTypedFailureWhenNotifyReportsEncoderDeath()
    {
        FakeNative native = new();
        using AudioEncoderSession session = CreateSession(native);
        session.Start();
        native.NotifyProcedure(7, EncodeNotifyStatus.EncoderDied, IntPtr.Zero);

        AudioEncoderException exception = Assert.ThrowsException<AudioEncoderException>(
            session.EnsureActiveAfterRender);

        Assert.AreEqual(AudioEncoderFailureStage.EncoderDied, exception.Stage);
        Assert.AreEqual(EncodeNotifyStatus.EncoderDied, exception.NotifyStatus);
        Assert.AreEqual(AudioEncoderSessionState.Faulted, session.State);
        Assert.AreEqual(0, native.EncodeIsActiveCalls);
    }

    [TestMethod]
    public void DisposeFailureAfterEncoderDeathRetainsFaultedStateAndHandleForRetry()
    {
        FakeNative native = new()
        {
            ActiveState = PlaybackState.Stopped,
            LastError = Errors.Handle
        };
        AudioEncoderSession session = CreateSession(native);
        session.Start();
        Assert.ThrowsException<AudioEncoderException>(session.EnsureActiveAfterRender);

        native.StopSucceeds = false;
        Assert.ThrowsException<AudioEncoderException>(session.Dispose);

        Assert.AreEqual(AudioEncoderSessionState.Faulted, session.State);
        Assert.AreEqual(7, session.EncoderHandle);

        native.StopSucceeds = true;
        session.Dispose();

        Assert.AreEqual(AudioEncoderSessionState.Disposed, session.State);
        Assert.AreEqual(0, session.EncoderHandle);
    }

    [TestMethod]
    public void DoubleStartAndStopAreRejected()
    {
        FakeNative native = new();
        using AudioEncoderSession session = CreateSession(native);
        session.Start();

        Assert.ThrowsException<InvalidOperationException>(session.Start);
        session.Stop();
        Assert.ThrowsException<InvalidOperationException>(session.Stop);
    }

    [TestMethod]
    public void MetadataCanBeReplacedBeforeStartAndIsReflectedInDiagnostics()
    {
        FakeNative native = new();
        using AudioEncoderSession session = CreateSession(native);

        session.SetTagInfo(new AudioTagInfo("artist", "title", "genre", 1d, "120", "source", "comment"));

        StringAssert.Contains(session.CommandLine, "--tt \"title\"");
        StringAssert.Contains(session.CommandLine, "--ta \"artist\"");
        StringAssert.Contains(session.CommandLine, "--tc \"comment\"");
        StringAssert.Contains(session.CommandLine, "--tg \"genre\"");
    }

    private static AudioEncoderSession CreateSession(FakeNative native)
    {
        return new AudioEncoderSession(
            11,
            new AudioEncoderCommandRequest(
                EncoderType.MP3_LAME,
                @"C:\encoder tools",
                @"C:\output\sample.mp3",
                44100,
                2,
                SampleFormat.SAMPLE_INT_16BIT,
                0.4f,
                AudioTagInfo.Empty),
            native);
    }

    private sealed class FakeNative : IAudioEncoderNative
    {
        internal int NextHandle { get; init; } = 7;

        internal bool NotifySucceeds { get; init; } = true;

        internal bool StopSucceeds { get; set; } = true;

        internal int StopCalls { get; private set; }

        internal int EncodeIsActiveCalls { get; private set; }

        internal EncodeNotifyProcedure NotifyProcedure { get; private set; } = null!;

        internal PlaybackState ActiveState { get; init; } = PlaybackState.Playing;

        public Errors LastError { get; set; } = Errors.OK;

        public int EncodeStart(int channel, string commandLine, EncodeFlags flags, EncodeProcedure procedure)
        {
            return NextHandle;
        }

        public bool EncodeSetNotify(int encoderHandle, EncodeNotifyProcedure procedure)
        {
            NotifyProcedure = procedure;
            return NotifySucceeds;
        }

        public PlaybackState EncodeIsActive(int encoderHandle)
        {
            EncodeIsActiveCalls++;
            return ActiveState;
        }

        public bool EncodeStop(int encoderHandle)
        {
            StopCalls++;
            return StopSucceeds;
        }
    }
}
