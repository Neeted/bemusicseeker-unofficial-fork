using System;
using System.Collections.Generic;
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
        Assert.IsTrue(session.OwnsProcessHandle);
        Assert.IsTrue(session.NotifyRegistered);
        Assert.IsNotNull(native.NotifyProcedure);
        Assert.IsTrue(native.StartFlags.HasFlag(EncodeFlags.Pause));
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
        Assert.IsFalse(session.OwnsProcessHandle);
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
        AudioEncoderException exception = Assert.ThrowsException<AudioEncoderException>(session.Stop);
        Assert.AreEqual(AudioEncoderFailureStage.Stop, exception.Stage);
        Assert.AreEqual(AudioEncoderSessionState.Faulted, session.State);
        Assert.AreEqual(0, session.EncoderHandle);
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
    public void EnsureActiveAfterRenderAcceptsPausedManualEncoder()
    {
        FakeNative native = new() { ActiveState = PlaybackState.Paused };
        using AudioEncoderSession session = CreateSession(native);
        session.Start();

        session.EnsureActiveAfterRender();

        Assert.AreEqual(AudioEncoderSessionState.Started, session.State);
    }

    [TestMethod]
    public void EncodeWriteUsesEncoderHandleAndExactFloatByteLength()
    {
        FakeNative native = new();
        using AudioEncoderSession session = CreateSession(native);
        session.Start();
        float[] samples = [0.125f, -0.5f, 0.75f, 1f];

        session.EncodeWrite(samples, sampleOffset: 1, sampleCount: 2);

        Assert.AreEqual(7, native.LastWriteHandle);
        Assert.AreEqual(1, native.LastWriteSampleOffset);
        Assert.AreEqual(8, native.LastWriteLengthBytes);
        CollectionAssert.AreEqual(new[] { -0.5f, 0.75f }, native.LastWriteSamples);
    }

    [TestMethod]
    public void EncodeWriteFailureRetainsTypedNativeError()
    {
        FakeNative native = new() { WriteSucceeds = false, LastError = Errors.Ended };
        using AudioEncoderSession session = CreateSession(native);
        session.Start();

        AudioEncoderException exception = Assert.ThrowsException<AudioEncoderException>(
            () => session.EncodeWrite([0.5f, -0.25f], sampleOffset: 0, sampleCount: 2));

        Assert.AreEqual(AudioEncoderFailureStage.Write, exception.Stage);
        Assert.AreEqual(Errors.Ended, exception.NativeError);
        Assert.AreEqual(AudioEncoderSessionState.Faulted, session.State);
    }

    [TestMethod]
    public void EncodeWriteRejectsIncompleteInterleavedFrame()
    {
        FakeNative native = new();
        using AudioEncoderSession session = CreateSession(native);
        session.Start();

        Assert.ThrowsException<ArgumentException>(
            () => session.EncodeWrite([0.5f], sampleOffset: 0, sampleCount: 1));
        Assert.AreEqual(0, native.LastWriteSamples.Length);
        Assert.AreEqual(AudioEncoderSessionState.Started, session.State);
    }

    [TestMethod]
    public void MaximumManualWriteLengthFitsWholeInterleavedFrames()
    {
        const int channelCount = 6;

        int samples = AudioEncoderSession.GetMaxSamplesPerWrite(channelCount);
        int byteLength = checked(samples * sizeof(float));

        Assert.AreEqual(0, samples % channelCount);
        Assert.IsTrue(byteLength <= int.MaxValue);
        Assert.IsTrue(samples + channelCount > int.MaxValue / sizeof(float));
        Assert.ThrowsException<ArgumentOutOfRangeException>(
            () => AudioEncoderSession.GetMaxSamplesPerWrite(int.MaxValue));
    }

    [TestMethod]
    public void TwentyFourBitManualWriteDithersOnlyRequestedSamplesWithoutMutatingSource()
    {
        FakeNative native = new();
        using AudioEncoderSession session = CreateSession(
            native,
            EncoderType.WAVE,
            SampleFormat.SAMPLE_FLOAT_32BIT,
            SampleFormat.SAMPLE_INT_24BIT);
        session.Start();
        float[] samples = [-0.75f, -0.25f / (1 << 23), 0f, 0.25f / (1 << 23), 0.75f, 0.5f];
        float[] original = (float[])samples.Clone();

        session.EncodeWrite(samples, sampleOffset: 1, sampleCount: 4);

        CollectionAssert.AreEqual(original, samples);
        Assert.AreEqual(7, native.LastWriteHandle);
        Assert.AreEqual(0, native.LastWriteSampleOffset);
        Assert.AreEqual(4 * sizeof(float), native.LastWriteLengthBytes);
        Assert.AreEqual(4, native.LastWriteSamples.Length);
        double lsb = 1d / (1 << 23);
        for (int sampleIndex = 0; sampleIndex < native.LastWriteSamples.Length; sampleIndex++)
        {
            Assert.IsTrue(float.IsFinite(native.LastWriteSamples[sampleIndex]));
            Assert.IsTrue(System.Math.Abs(native.LastWriteSamples[sampleIndex] - samples[sampleIndex + 1]) <= 2d * lsb);
        }
        Assert.AreEqual(0.5f, samples[^1]);
        Assert.IsFalse(session.OwnsProcessHandle);
        Assert.AreEqual(0, native.ProcessHandleDuplicateCalls);
        Assert.AreEqual(0, native.ProcessExitCodeQueryCalls);
    }

    [TestMethod]
    public void TwentyFourBitManualWriteUsesBoundedFrameAlignedScratchChunks()
    {
        const int channelCount = 2;
        const int sampleOffset = 3;
        FakeNative native = new();
        using AudioEncoderSession session = CreateSession(
            native,
            EncoderType.WAVE,
            SampleFormat.SAMPLE_FLOAT_32BIT,
            SampleFormat.SAMPLE_INT_24BIT);
        session.Start();

        int chunkSize = AudioEncoderSession.GetMaxPcm24SamplesPerWrite(channelCount);
        int sampleCount = checked(chunkSize * 2 + channelCount * 3);
        float[] samples = new float[checked(sampleOffset + sampleCount + 4)];
        Array.Fill(samples, 0.9f, startIndex: 0, count: sampleOffset);
        for (int sampleIndex = 0; sampleIndex < sampleCount; sampleIndex++)
        {
            samples[sampleOffset + sampleIndex] = (sampleIndex % 1001 - 500) / 1000f;
        }
        Array.Fill(samples, -0.9f, startIndex: sampleOffset + sampleCount, count: 4);
        float[] original = (float[])samples.Clone();

        session.EncodeWrite(samples, sampleOffset, sampleCount);

        CollectionAssert.AreEqual(original, samples);
        CollectionAssert.AreEqual(
            new[] { chunkSize, chunkSize, channelCount * 3 },
            native.WriteBlockSampleCounts.ToArray());
        Assert.AreEqual(sampleCount, native.WriteBlockSampleCounts[0]
            + native.WriteBlockSampleCounts[1]
            + native.WriteBlockSampleCounts[2]);
        Assert.AreEqual(chunkSize, native.WriteBuffers[0].Length);
        Assert.AreSame(native.WriteBuffers[0], native.WriteBuffers[1]);
        Assert.AreSame(native.WriteBuffers[0], native.WriteBuffers[2]);

        double lsb = 1d / (1 << 23);
        int writtenSampleOffset = 0;
        bool allBlocksMatchRequestedInput = true;
        foreach (float[] block in native.WrittenBlocks)
        {
            allBlocksMatchRequestedInput &= block.Length <= chunkSize;
            allBlocksMatchRequestedInput &= block.Length % channelCount == 0;
            for (int blockSampleIndex = 0; blockSampleIndex < block.Length; blockSampleIndex++)
            {
                float expected = samples[sampleOffset + writtenSampleOffset + blockSampleIndex];
                double quantized = (double)block[blockSampleIndex] * (1 << 23);
                allBlocksMatchRequestedInput &= float.IsFinite(block[blockSampleIndex]);
                allBlocksMatchRequestedInput &= System.Math.Abs(block[blockSampleIndex] - expected) <= 2d * lsb;
                allBlocksMatchRequestedInput &= quantized == System.Math.Round(quantized);
            }

            writtenSampleOffset += block.Length;
        }

        Assert.IsTrue(allBlocksMatchRequestedInput);
        Assert.AreEqual(sampleCount, writtenSampleOffset);
    }

    [TestMethod]
    public void TwentyFourBitQuantizerLeavesSourceAndScratchSurplusUntouched()
    {
        float[] source = [-1f, -0.25f / (1 << 23), 0.25f / (1 << 23), 1f];
        float[] original = (float[])source.Clone();
        float[] scratch = [12f, 13f, 14f, 15f];

        AudioPcm24Quantizer.Apply(source.AsSpan(1, 2), scratch.AsSpan(1, 2), new Random(12345));

        CollectionAssert.AreEqual(original, source);
        Assert.AreEqual(12f, scratch[0]);
        Assert.AreEqual(15f, scratch[^1]);
        Assert.AreEqual(4, scratch.Length);
    }

    [TestMethod]
    public void ExitCodeZeroCompletesExternalEncoderSuccessfully()
    {
        FakeNative native = new() { ProcessExitCode = 0 };
        using AudioEncoderSession session = CreateSession(native);
        session.Start();

        session.Stop();

        Assert.AreEqual(AudioEncoderSessionState.Stopped, session.State);
        Assert.IsNull(session.OutputFailure);
        Assert.IsFalse(session.OwnsProcessHandle);
        Assert.IsTrue(native.LastProcessHandle.IsDisposed);
    }

    [TestMethod]
    public void NonzeroExitCodeIsACompletedEncoderFailureAndReleasesProcessHandle()
    {
        FakeNative native = new() { ProcessExitCode = 7 };
        using AudioEncoderSession session = CreateSession(native);
        session.Start();

        AudioEncoderException failure = Assert.ThrowsException<AudioEncoderException>(session.Stop);

        Assert.AreEqual(AudioEncoderFailureStage.ProcessExit, failure.Stage);
        Assert.AreEqual((uint)7, failure.ProcessExitCode);
        Assert.IsNull(failure.ProcessWin32Error);
        Assert.AreEqual(AudioEncoderSessionState.Faulted, session.State);
        Assert.IsFalse(session.OwnsProcessHandle);
        Assert.IsTrue(native.LastProcessHandle.IsDisposed);
    }

    [TestMethod]
    public void StillActiveExitCodeIsRetainedAndDisposeRechecksWithoutErasingFailure()
    {
        FakeNative native = new() { ProcessExitCode = 259 };
        AudioEncoderSession session = CreateSession(native);
        session.Start();

        AudioEncoderException failure = Assert.ThrowsException<AudioEncoderException>(session.Stop);
        Assert.AreEqual((uint)259, failure.ProcessExitCode);
        Assert.AreEqual(0, session.EncoderHandle);
        Assert.IsTrue(session.OwnsProcessHandle);
        Assert.IsFalse(native.LastProcessHandle.IsDisposed);
        Assert.ThrowsException<AudioEncoderException>(session.Dispose);
        Assert.IsTrue(session.OwnsProcessHandle);

        native.ProcessExitCode = 0;
        session.Dispose();

        Assert.AreEqual(AudioEncoderSessionState.Disposed, session.State);
        Assert.IsFalse(session.OwnsProcessHandle);
        Assert.AreSame(failure, session.OutputFailure);
        Assert.AreSame(failure, Assert.ThrowsException<AudioEncoderException>(session.ThrowPendingOutputFailure));
    }

    [TestMethod]
    public void ExitCodeQueryFailureRetainsProcessHandleAndWin32Diagnostic()
    {
        FakeNative native = new() { ProcessExitQuerySucceeds = false, ProcessExitWin32Error = 5 };
        AudioEncoderSession session = CreateSession(native);
        session.Start();

        AudioEncoderException failure = Assert.ThrowsException<AudioEncoderException>(session.Stop);

        Assert.AreEqual(AudioEncoderFailureStage.ProcessExit, failure.Stage);
        Assert.IsNull(failure.ProcessExitCode);
        Assert.AreEqual(5, failure.ProcessWin32Error);
        Assert.IsTrue(session.OwnsProcessHandle);
        Assert.ThrowsException<AudioEncoderException>(session.Dispose);
        Assert.IsFalse(native.LastProcessHandle.IsDisposed);

        native.ProcessExitQuerySucceeds = true;
        native.ProcessExitCode = 7;
        session.Dispose();

        Assert.AreEqual(AudioEncoderSessionState.Disposed, session.State);
        Assert.IsFalse(session.OwnsProcessHandle);
        Assert.AreSame(failure, session.OutputFailure);
        Assert.AreEqual(5, session.OutputFailure.ProcessWin32Error);
    }

    [TestMethod]
    public void FailedStartPreservesPrimaryFailureAndBothHandleOwnersUntilCleanupSucceeds()
    {
        FakeNative native = new()
        {
            NotifySucceeds = false,
            StopSucceeds = false,
            LastError = Errors.Parameter
        };
        AudioEncoderSession session = CreateSession(native);

        AudioEncoderException failure = Assert.ThrowsException<AudioEncoderException>(session.Start);

        Assert.AreEqual(AudioEncoderFailureStage.NotifyRegistration, failure.Stage);
        Assert.AreEqual(7, session.EncoderHandle);
        Assert.IsTrue(session.OwnsProcessHandle);
        Assert.IsFalse(native.LastProcessHandle.IsDisposed);
        Assert.ThrowsException<AudioEncoderException>(session.Dispose);
        Assert.AreEqual(7, session.EncoderHandle);
        Assert.IsTrue(session.OwnsProcessHandle);

        native.StopSucceeds = true;
        native.ProcessExitCode = 0;
        session.Dispose();

        Assert.AreEqual(AudioEncoderSessionState.Disposed, session.State);
        Assert.AreEqual(0, session.EncoderHandle);
        Assert.IsFalse(session.OwnsProcessHandle);
        Assert.AreSame(failure, session.OutputFailure);
        Assert.AreEqual(AudioEncoderFailureStage.NotifyRegistration, session.OutputFailure.Stage);
    }

    [TestMethod]
    public void FailedProcessHandleDuplicationRetainsNativeHandleWhenCleanupFails()
    {
        FakeNative native = new()
        {
            DuplicateProcessHandleSucceeds = false,
            DuplicateProcessHandleWin32Error = 6,
            StopSucceeds = false
        };
        AudioEncoderSession session = CreateSession(native);

        AudioEncoderException failure = Assert.ThrowsException<AudioEncoderException>(session.Start);

        Assert.AreEqual(AudioEncoderFailureStage.ProcessHandleDuplicate, failure.Stage);
        Assert.AreEqual(6, failure.ProcessWin32Error);
        Assert.AreEqual(7, session.EncoderHandle);
        Assert.IsFalse(session.OwnsProcessHandle);
        Assert.ThrowsException<AudioEncoderException>(session.Dispose);
        Assert.AreEqual(7, session.EncoderHandle);

        native.StopSucceeds = true;
        session.Dispose();
        Assert.AreEqual(AudioEncoderSessionState.Disposed, session.State);
        Assert.AreSame(failure, session.OutputFailure);
    }

    [TestMethod]
    public void NotifyFailureRemainsPrimaryWhenExternalProcessExitIsZero()
    {
        FakeNative native = new()
        {
            NotifySucceeds = false,
            LastError = Errors.Parameter,
            ProcessExitCode = 0
        };
        using AudioEncoderSession session = CreateSession(native);

        AudioEncoderException failure = Assert.ThrowsException<AudioEncoderException>(session.Start);

        Assert.AreEqual(AudioEncoderFailureStage.NotifyRegistration, failure.Stage);
        Assert.AreEqual(Errors.Parameter, failure.NativeError);
        Assert.AreEqual((uint)0, failure.ProcessExitCode);
        Assert.AreSame(failure, session.OutputFailure);
        Assert.IsTrue(native.LastProcessHandle.IsDisposed);
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

    private static AudioEncoderSession CreateSession(
        FakeNative native,
        EncoderType encoderType = EncoderType.MP3_LAME,
        SampleFormat inputFormat = SampleFormat.SAMPLE_INT_16BIT,
        SampleFormat outputFormat = SampleFormat.UNKNOWN)
    {
        return new AudioEncoderSession(
            11,
            new AudioEncoderCommandRequest(
                encoderType,
                @"C:\encoder tools",
                @"C:\output\sample" + encoderType.GetEncoderOutputExtension(),
                44100,
                2,
                inputFormat,
                0.4f,
                AudioTagInfo.Empty,
                outputFormat),
            native);
    }

    [TestMethod]
    public void StopReportsForcedTerminationEvenWhenFreeNotificationFollows()
    {
        FakeNative native = new() { TerminateOnStop = true };
        using AudioEncoderSession session = CreateSession(native);
        session.Start();

        AudioEncoderException failure = Assert.ThrowsException<AudioEncoderException>(session.Stop);

        Assert.AreEqual(AudioEncoderFailureStage.Stop, failure.Stage);
        Assert.AreEqual((EncodeNotifyStatus)0x10003, failure.NotifyStatus);
        Assert.AreEqual(0, session.EncoderHandle);
        Assert.AreEqual(AudioEncoderSessionState.Faulted, session.State);
    }

    private sealed class FakeNative : IAudioEncoderNative
    {
        internal int NextHandle { get; init; } = 7;

        internal bool NotifySucceeds { get; init; } = true;

        internal bool DuplicateProcessHandleSucceeds { get; init; } = true;

        internal int DuplicateProcessHandleWin32Error { get; init; } = 6;

        internal bool StopSucceeds { get; set; } = true;
        internal bool TerminateOnStop { get; init; }

        internal int StopCalls { get; private set; }

        internal int EncodeIsActiveCalls { get; private set; }

        internal EncodeFlags StartFlags { get; private set; }

        internal bool WriteSucceeds { get; init; } = true;

        internal bool ProcessExitQuerySucceeds { get; set; } = true;

        internal int ProcessExitWin32Error { get; set; } = 6;

        internal uint ProcessExitCode { get; set; }

        internal int ProcessHandleDuplicateCalls { get; private set; }

        internal int ProcessExitCodeQueryCalls { get; private set; }

        private FakeProcessHandle? lastProcessHandle;

        internal FakeProcessHandle LastProcessHandle => lastProcessHandle
            ?? throw new InvalidOperationException("No process handle was duplicated.");

        internal int LastWriteHandle { get; private set; }

        internal int LastWriteSampleOffset { get; private set; }

        internal int LastWriteLengthBytes { get; private set; }

        internal float[] LastWriteSamples { get; private set; } = [];

        internal List<float[]> WriteBuffers { get; } = [];

        internal List<int> WriteBlockSampleCounts { get; } = [];

        internal List<float[]> WrittenBlocks { get; } = [];

        internal EncodeNotifyProcedure NotifyProcedure { get; private set; } = null!;

        internal PlaybackState ActiveState { get; set; } = PlaybackState.Playing;

        public Errors LastError { get; set; } = Errors.OK;

        public int EncodeStart(int channel, string commandLine, EncodeFlags flags, EncodeProcedure procedure)
        {
            StartFlags = flags;
            return NextHandle;
        }

        public bool EncodeWrite(int encoderHandle, float[] samples, int sampleOffset, int lengthBytes)
        {
            LastWriteHandle = encoderHandle;
            LastWriteSampleOffset = sampleOffset;
            LastWriteLengthBytes = lengthBytes;
            int sampleCount = lengthBytes / sizeof(float);
            LastWriteSamples = new float[sampleCount];
            Array.Copy(samples, sampleOffset, LastWriteSamples, 0, LastWriteSamples.Length);
            WriteBuffers.Add(samples);
            WriteBlockSampleCounts.Add(sampleCount);
            WrittenBlocks.Add(LastWriteSamples);
            return WriteSucceeds;
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
            if (TerminateOnStop)
            {
                NotifyProcedure(encoderHandle, (EncodeNotifyStatus)0x10003, IntPtr.Zero);
                NotifyProcedure(encoderHandle, EncodeNotifyStatus.Free, IntPtr.Zero);
            }
            return StopSucceeds;
        }

        public bool TryDuplicateProcessHandle(
            int encoderHandle,
            out IAudioEncoderProcessHandle? processHandle,
            out int win32Error)
        {
            ProcessHandleDuplicateCalls++;
            if (!DuplicateProcessHandleSucceeds)
            {
                processHandle = null;
                win32Error = DuplicateProcessHandleWin32Error;
                return false;
            }

            lastProcessHandle = new FakeProcessHandle();
            processHandle = LastProcessHandle;
            win32Error = 0;
            return true;
        }

        public bool TryGetProcessExitCode(
            IAudioEncoderProcessHandle processHandle,
            out uint exitCode,
            out int win32Error)
        {
            ProcessExitCodeQueryCalls++;
            Assert.AreSame(LastProcessHandle, processHandle);
            exitCode = ProcessExitCode;
            win32Error = ProcessExitWin32Error;
            return ProcessExitQuerySucceeds;
        }
    }

    private sealed class FakeProcessHandle : IAudioEncoderProcessHandle
    {
        internal bool IsDisposed { get; private set; }

        public void Dispose() => IsDisposed = true;
    }
}
