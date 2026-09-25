using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using ManagedBass;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Ribbit.Media;
using Ribbit.Media.Audio;

namespace BeMusicSeeker.Tests;

[TestClass]
[DoNotParallelize]
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
        Assert.AreEqual(
            BassFlags.MixerChanPause | BassFlags.MixerChanMatrix | BassFlags.MixerChanNoRampin,
            native.LastAddFlags);
        Assert.AreEqual(77, native.MixerHandle);
    }

    [DataTestMethod]
    [DataRow(2)]
    [DataRow(3)]
    [DataRow(4)]
    [DataRow(5)]
    [DataRow(6)]
    public void ConfigurePausedSource_SetsAndReadsBackConfiguredSrcThenAppliesExplicitMatrix(int quality)
    {
        var native = new FakeNativeBoundary
        {
            MixerHandle = 77,
            SourceInfo = new BassMixerChannelInfo(44100, 2, BassFlags.Float | BassFlags.Decode),
            MixerInfo = new BassMixerChannelInfo(48000, 2, BassFlags.Float),
            ReportedSrcQuality = quality
        };
        var controller = new BassMixerSourceController(native);

        controller.ConfigurePausedSource(
            77,
            12,
            AudioChannelLayout.CreateStandard(2),
            "test.wav",
            quality);

        CollectionAssert.AreEqual(
            new[] { "info-source", "info-mixer", "src-set", "src-get", "matrix" },
            native.ConfigurationCalls.ToArray());
        Assert.AreEqual((float)quality, native.SampleRateConversionQuality);
        float[,] matrix = native.LastMatrix
            ?? throw new AssertFailedException("The matrix must be set before source resume.");
        Assert.AreEqual(2, matrix.GetLength(0));
        Assert.AreEqual(2, matrix.GetLength(1));
        Assert.AreEqual(1f, matrix[0, 0]);
        Assert.AreEqual(0f, matrix[0, 1]);
        Assert.AreEqual(0f, matrix[1, 0]);
        Assert.AreEqual(1f, matrix[1, 1]);
    }

    [TestMethod]
    public void ConfigurePausedSource_SameRateSkipsSrcButStillSetsMatrix()
    {
        var native = new FakeNativeBoundary
        {
            MixerHandle = 77,
            SourceInfo = new BassMixerChannelInfo(48000, 2, BassFlags.Float | BassFlags.Decode),
            MixerInfo = new BassMixerChannelInfo(48000, 2, BassFlags.Float)
        };
        var controller = new BassMixerSourceController(native);

        controller.ConfigurePausedSource(
            77,
            12,
            AudioChannelLayout.CreateStandard(2),
            "test.wav");

        CollectionAssert.AreEqual(
            new[] { "info-source", "info-mixer", "matrix" },
            native.ConfigurationCalls.ToArray());
        Assert.IsNotNull(native.LastMatrix);
    }

    [TestMethod]
    public void ConfigurePausedSource_RejectsNonFloatOrNonDecodeSourceBeforePublishingMatrix()
    {
        var native = new FakeNativeBoundary
        {
            MixerHandle = 77,
            SourceInfo = new BassMixerChannelInfo(44100, 2, BassFlags.Decode),
            MixerInfo = new BassMixerChannelInfo(48000, 2, BassFlags.Float)
        };
        var controller = new BassMixerSourceController(native);

        BassAudioPlaybackException exception = Assert.ThrowsException<BassAudioPlaybackException>(
            () => controller.ConfigurePausedSource(
                77,
                12,
                AudioChannelLayout.CreateStandard(2),
                "test.wav"));

        Assert.AreEqual(BassAudioPlaybackStage.MixerSourceFormat, exception.Stage);
        Assert.AreEqual(0, native.SetMatrixCalls);
        Assert.AreEqual(0, native.SetSrcCalls);
    }

    [TestMethod]
    public void ConfigurePausedSource_RejectsSrcReadbackMismatchBeforeSettingMatrix()
    {
        var native = new FakeNativeBoundary
        {
            MixerHandle = 77,
            SourceInfo = new BassMixerChannelInfo(44100, 2, BassFlags.Float | BassFlags.Decode),
            MixerInfo = new BassMixerChannelInfo(48000, 2, BassFlags.Float),
            ReportedSrcQuality = 5f
        };
        var controller = new BassMixerSourceController(native);

        BassAudioPlaybackException exception = Assert.ThrowsException<BassAudioPlaybackException>(
            () => controller.ConfigurePausedSource(
                77,
                12,
                AudioChannelLayout.CreateStandard(2),
                "test.wav"));

        Assert.AreEqual(BassAudioPlaybackStage.MixerSourceFormat, exception.Stage);
        Assert.AreEqual(1, native.SetSrcCalls);
        Assert.AreEqual(0, native.SetMatrixCalls);
    }

    [TestMethod]
    public void ConfigurePausedSource_PreservesNativeErrorWhenSettingSrcFails()
    {
        var native = new FakeNativeBoundary
        {
            MixerHandle = 77,
            SourceInfo = new BassMixerChannelInfo(44100, 2, BassFlags.Float | BassFlags.Decode),
            MixerInfo = new BassMixerChannelInfo(48000, 2, BassFlags.Float),
            SetSrcResult = false,
            Error = Errors.NotAvailable
        };
        var controller = new BassMixerSourceController(native);

        BassAudioPlaybackException exception = Assert.ThrowsException<BassAudioPlaybackException>(
            () => controller.ConfigurePausedSource(
                77,
                12,
                AudioChannelLayout.CreateStandard(2),
                "test.wav",
                2));

        Assert.AreEqual(BassAudioPlaybackStage.MixerSourceFormat, exception.Stage);
        Assert.AreEqual(Errors.NotAvailable, exception.NativeErrorCode);
        CollectionAssert.AreEqual(new[] { "info-source", "info-mixer", "src-set" }, native.ConfigurationCalls.ToArray());
        Assert.AreEqual(0, native.SetMatrixCalls);
    }

    [TestMethod]
    public void ConfigurePausedSource_PreservesNativeErrorWhenReadingSrcFails()
    {
        var native = new FakeNativeBoundary
        {
            MixerHandle = 77,
            SourceInfo = new BassMixerChannelInfo(44100, 2, BassFlags.Float | BassFlags.Decode),
            MixerInfo = new BassMixerChannelInfo(48000, 2, BassFlags.Float),
            GetSrcResult = false,
            Error = Errors.Position
        };
        var controller = new BassMixerSourceController(native);

        BassAudioPlaybackException exception = Assert.ThrowsException<BassAudioPlaybackException>(
            () => controller.ConfigurePausedSource(
                77,
                12,
                AudioChannelLayout.CreateStandard(2),
                "test.wav",
                2));

        Assert.AreEqual(BassAudioPlaybackStage.MixerSourceFormat, exception.Stage);
        Assert.AreEqual(Errors.Position, exception.NativeErrorCode);
        CollectionAssert.AreEqual(
            new[] { "info-source", "info-mixer", "src-set", "src-get" },
            native.ConfigurationCalls.ToArray());
        Assert.AreEqual(0, native.SetMatrixCalls);
    }

    [DataTestMethod]
    [DataRow(1)]
    [DataRow(7)]
    public void ConfigurePausedSource_RejectsUnsupportedQualityBeforeNativeCalls(int quality)
    {
        var native = new FakeNativeBoundary { MixerHandle = 77 };
        var controller = new BassMixerSourceController(native);

        Assert.ThrowsException<ArgumentOutOfRangeException>(() => controller.ConfigurePausedSource(
            77,
            12,
            AudioChannelLayout.CreateStandard(2),
            "test.wav",
            quality));

        Assert.AreEqual(0, native.SetSrcCalls);
        Assert.AreEqual(0, native.SetMatrixCalls);
    }

    [TestMethod]
    public void ConfigurePausedSource_MatrixFailurePreservesNativeError()
    {
        var native = new FakeNativeBoundary
        {
            MixerHandle = 77,
            SourceInfo = new BassMixerChannelInfo(48000, 2, BassFlags.Float | BassFlags.Decode),
            MixerInfo = new BassMixerChannelInfo(48000, 2, BassFlags.Float),
            SetMatrixResult = false,
            Error = Errors.Position
        };
        var controller = new BassMixerSourceController(native);

        BassAudioPlaybackException exception = Assert.ThrowsException<BassAudioPlaybackException>(
            () => controller.ConfigurePausedSource(
                77,
                12,
                AudioChannelLayout.CreateStandard(2),
                "test.wav"));

        Assert.AreEqual(BassAudioPlaybackStage.MixerMatrix, exception.Stage);
        Assert.AreEqual(Errors.Position, exception.NativeErrorCode);
        CollectionAssert.AreEqual(
            new[] { "info-source", "info-mixer", "matrix" },
            native.ConfigurationCalls.ToArray());
    }

    [TestMethod]
    public void AudioChannelMatrix_MapsVorbisOrderAndAppliesDefinedDownmixCoefficients()
    {
        AudioChannelLayout vorbisSixChannel = new(
        [
            AudioSpeakerPosition.FrontLeft,
            AudioSpeakerPosition.FrontCenter,
            AudioSpeakerPosition.FrontRight,
            AudioSpeakerPosition.BackLeft,
            AudioSpeakerPosition.BackRight,
            AudioSpeakerPosition.LowFrequency
        ]);
        var bassSixChannel = AudioChannelLayout.CreateBassOutput(6);
        float[,] sixToStereo = AudioChannelMatrix.Create(vorbisSixChannel, AudioChannelLayout.CreateBassOutput(2));

        Assert.AreEqual(2, sixToStereo.GetLength(0));
        Assert.AreEqual(6, sixToStereo.GetLength(1));
        Assert.AreEqual(1f, sixToStereo[0, 0]);
        Assert.AreEqual(1f, sixToStereo[1, 2]);
        Assert.AreEqual(0.70710677f, sixToStereo[0, 1]);
        Assert.AreEqual(0.70710677f, sixToStereo[1, 1]);
        Assert.AreEqual(0.70710677f, sixToStereo[0, 3]);
        Assert.AreEqual(0.70710677f, sixToStereo[1, 4]);
        Assert.AreEqual(0f, sixToStereo[0, 5]);
        Assert.AreEqual(0f, sixToStereo[1, 5]);

        float[,] sixToMono = AudioChannelMatrix.Create(vorbisSixChannel, AudioChannelLayout.CreateBassOutput(1));
        Assert.AreEqual(1, sixToMono.GetLength(0));
        Assert.AreEqual(6, sixToMono.GetLength(1));
        Assert.AreEqual(0.5f, sixToMono[0, 0]);
        Assert.AreEqual(0.70710677f, sixToMono[0, 1]);
        Assert.AreEqual(0.70710677f * 0.5f, sixToMono[0, 3]);
        Assert.AreEqual(0.70710677f * 0.5f, sixToMono[0, 4]);
        Assert.AreEqual(0f, sixToMono[0, 5]);

        float[,] stereoToMultichannel = AudioChannelMatrix.Create(
            AudioChannelLayout.CreateStandard(2),
            bassSixChannel);
        Assert.AreEqual(6, stereoToMultichannel.GetLength(0));
        Assert.AreEqual(2, stereoToMultichannel.GetLength(1));
        Assert.AreEqual(1f, stereoToMultichannel[0, 0]);
        Assert.AreEqual(1f, stereoToMultichannel[1, 1]);
        for (int output = 2; output < 6; output++)
        {
            Assert.AreEqual(0f, stereoToMultichannel[output, 0]);
            Assert.AreEqual(0f, stereoToMultichannel[output, 1]);
        }

        var bassEightChannel = AudioChannelLayout.CreateBassOutput(8);
        _ = bassEightChannel.ToWaveOrder(out int[] waveIndexes);
        CollectionAssert.AreEqual(new[] { 0, 1, 2, 3, 4, 5, 6, 7 }, waveIndexes);
        float[,] eightToStereo = AudioChannelMatrix.Create(
            bassEightChannel,
            AudioChannelLayout.CreateBassOutput(2));
        Assert.AreEqual(2, eightToStereo.GetLength(0));
        Assert.AreEqual(8, eightToStereo.GetLength(1));
        Assert.AreEqual(0.70710677f, eightToStereo[0, 4]);
        Assert.AreEqual(0.70710677f, eightToStereo[1, 5]);
        Assert.AreEqual(0.70710677f, eightToStereo[0, 6]);
        Assert.AreEqual(0.70710677f, eightToStereo[1, 7]);
    }

    [TestMethod]
    public void AudioChannelMatrix_RejectsSpeakerPositionsWithoutARoutingRule()
    {
        var topFrontCenter = new AudioChannelLayout([AudioSpeakerPosition.TopFrontCenter]);

        Assert.ThrowsException<ArgumentException>(
            () => AudioChannelMatrix.Create(topFrontCenter, AudioChannelLayout.CreateBassOutput(2)));
    }

    [TestMethod]
    public void NativeMixer_AsymmetricSixChannelImpulsesUseNativeOutputRowsAndWaveSpeakerOrder()
    {
        SampleRate previousFrequency = BassAudioPlayer.Frequency;
        SampleFormat previousFormat = BassAudioPlayer.Format;
        float previousDefaultVolume = BassAudioPlayer.DefaultVolume;
        float previousDeviceVolume = BassAudioPlayer.DeviceVolume;
        bool previousMute = BassAudioPlayer.IsDeviceMuted;
        string path = Path.Combine(
            Path.GetTempPath(),
            "BeMusicSeeker.MixerMatrix." + Guid.NewGuid().ToString("N") + ".wav");
        BassAudioSession? session = null;
        BassAudioPlayer? player = null;
        try
        {
            BassAudioPlayer.Free();
            BassAudioRuntime.Shutdown();
            BassAudioPlayer.Frequency = SampleRate.SAMPLE_RATE_48000Hz;
            BassAudioPlayer.Format = SampleFormat.SAMPLE_FLOAT_32BIT;
            BassAudioPlayer.DefaultVolume = 1f;
            BassAudioPlayer.DeviceVolume = 1f;
            BassAudioPlayer.IsDeviceMuted = false;
            BassAudioPlayer.InitializeOwned(
                BassAudioPlayer.DeviceDriver.NULL_DEVICE,
                default,
                0f,
                out session);
            BassAudioPlayer.DefaultVolume = 1f;

            int mixerFrequency = Bass.ChannelGetInfo(session.MixerHandle).Frequency;
            WriteSixChannelImpulseWave(path, mixerFrequency);
            player = new BassAudioPlayer(path);
            player.Play();

            byte[] output = new byte[8 * sizeof(float)];
            int bytesRead = Bass.ChannelGetData(session.MixerHandle, output, output.Length);
            Assert.IsTrue(bytesRead >= 2 * sizeof(float), Bass.LastError.ToString());
            float[] expected =
            [
                0.5f * 0.70710677f, 0f,
                0f, 0.5f * 0.70710677f,
                0f, 0f,
                0.5f * 0.70710677f, 0.5f * 0.70710677f
            ];
            for (int playback = 0; playback < 2; playback++)
            {
                if (playback != 0)
                {
                    player.Play();
                    Assert.AreEqual(output.Length, Bass.ChannelGetData(session.MixerHandle, output, output.Length));
                }
                for (int sample = 0; sample < expected.Length; sample++)
                {
                    float actual = BitConverter.Int32BitsToSingle(
                        System.Buffers.Binary.BinaryPrimitives.ReadInt32LittleEndian(
                            output.AsSpan(sample * sizeof(float), sizeof(float))));
                    Assert.AreEqual(expected[sample], actual, 0.00001f, $"playback {playback}, output sample {sample}");
                }
            }
        }
        finally
        {
            try
            {
                player?.Dispose();
            }
            finally
            {
                try
                {
                    if (session == null)
                    {
                        BassAudioPlayer.Free();
                    }
                    else if (!BassAudioPlayer.Free(session))
                    {
                        throw new InvalidOperationException("The native mixer test session did not release.");
                    }
                }
                finally
                {
                    try
                    {
                        BassAudioRuntime.Shutdown();
                    }
                    finally
                    {
                        File.Delete(path);
                        BassAudioPlayer.IsDeviceMuted = previousMute;
                        BassAudioPlayer.DeviceVolume = previousDeviceVolume;
                        BassAudioPlayer.DefaultVolume = previousDefaultVolume;
                        BassAudioPlayer.Frequency = previousFrequency;
                        BassAudioPlayer.Format = previousFormat;
                    }
                }
            }
        }
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

    private static void WriteSixChannelImpulseWave(string path, int sampleRate)
    {
        const int channelCount = 6;
        const int frameCount = 1024;
        const int bytesPerSample = sizeof(float);
        int blockAlign = channelCount * bytesPerSample;
        int dataLength = frameCount * blockAlign;
        using FileStream stream = File.Create(path);
        using var writer = new BinaryWriter(stream, Encoding.ASCII, leaveOpen: true);
        writer.Write(Encoding.ASCII.GetBytes("RIFF"));
        writer.Write(checked(72 + dataLength));
        writer.Write(Encoding.ASCII.GetBytes("WAVE"));
        writer.Write(Encoding.ASCII.GetBytes("fmt "));
        writer.Write(40);
        writer.Write((ushort)0xFFFE);
        writer.Write((ushort)channelCount);
        writer.Write(sampleRate);
        writer.Write(checked(sampleRate * blockAlign));
        writer.Write((ushort)blockAlign);
        writer.Write((ushort)32);
        writer.Write((ushort)22);
        writer.Write((ushort)32);
        writer.Write(0x0000003Fu);
        writer.Write(new byte[]
        {
            0x03, 0, 0, 0, 0, 0, 0x10, 0,
            0x80, 0, 0, 0xAA, 0, 0x38, 0x9B, 0x71
        });
        writer.Write(Encoding.ASCII.GetBytes("fact"));
        writer.Write(4);
        writer.Write(frameCount);
        writer.Write(Encoding.ASCII.GetBytes("data"));
        writer.Write(dataLength);
        for (int frame = 0; frame < frameCount; frame++)
        {
            writer.Write(0f);
            writer.Write(0f);
            writer.Write(frame == 3 ? 0.5f : 0f);
            writer.Write(frame == 2 ? 0.5f : 0f);
            writer.Write(frame == 0 ? 0.5f : 0f);
            writer.Write(frame == 1 ? 0.5f : 0f);
        }
    }

    private sealed class FakeNativeBoundary : IBassMixerSourceNativeBoundary
    {
        internal int MixerHandle { get; set; }

        internal Errors Error { get; set; } = Errors.OK;

        internal bool AddResult { get; set; } = true;

        internal BassFlags FlagsResult { get; set; } = BassFlags.Default;

        internal BassMixerChannelInfo SourceInfo { get; set; } = new(
            44100,
            2,
            BassFlags.Float | BassFlags.Decode);

        internal BassMixerChannelInfo MixerInfo { get; set; } = new(
            48000,
            2,
            BassFlags.Float);

        internal float SampleRateConversionQuality { get; set; } = 6f;

        internal float ReportedSrcQuality { get; set; } = 6f;

        internal bool SetSrcResult { get; set; } = true;

        internal bool GetSrcResult { get; set; } = true;

        internal bool SetMatrixResult { get; set; } = true;

        internal bool SetPositionResult { get; set; } = true;

        internal Queue<int> MixerReads { get; } = new();

        internal List<string> ConfigurationCalls { get; } = new();

        internal Queue<Errors> ErrorReads { get; } = new();

        internal int AddCalls { get; private set; }

        internal int RemoveCalls { get; private set; }

        internal int SetSrcCalls { get; private set; }

        internal int SetMatrixCalls { get; private set; }

        internal BassFlags LastAddFlags { get; private set; }

        internal BassFlags LastFlags { get; private set; }

        internal BassFlags LastMask { get; private set; }

        internal long LastPosition { get; private set; }

        internal PositionFlags LastPositionFlags { get; private set; }

        internal float[,]? LastMatrix { get; private set; }

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
            ConfigurationCalls.Add("mixer-flags");
            LastFlags = flags;
            LastMask = mask;
            return FlagsResult;
        }

        public BassMixerChannelInfo GetChannelInfo(int channelHandle)
        {
            ConfigurationCalls.Add(channelHandle == 12 ? "info-source" : "info-mixer");
            return channelHandle == 12 ? SourceInfo : MixerInfo;
        }

        public bool SetSampleRateConversion(int sourceHandle, float quality)
        {
            ConfigurationCalls.Add("src-set");
            SetSrcCalls++;
            SampleRateConversionQuality = quality;
            return SetSrcResult;
        }

        public bool GetSampleRateConversion(int sourceHandle, out float quality)
        {
            ConfigurationCalls.Add("src-get");
            quality = ReportedSrcQuality;
            return GetSrcResult;
        }

        public bool SetMatrix(int sourceHandle, float[,] matrix)
        {
            ConfigurationCalls.Add("matrix");
            SetMatrixCalls++;
            LastMatrix = matrix;
            return SetMatrixResult;
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
