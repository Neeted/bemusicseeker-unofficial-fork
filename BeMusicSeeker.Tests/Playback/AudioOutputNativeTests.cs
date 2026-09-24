using System;
using System.Buffers.Binary;
using System.IO;
using System.Runtime.ExceptionServices;
using BeMusicSeeker.Tests.Helpers;
using ManagedBass;
using ManagedBass.Enc;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Ribbit.Media;
using Ribbit.Media.Audio;

namespace BeMusicSeeker.Tests;

/// <summary>共有BASS null-device graphを使ってencoderの最終PCM変換を確認します。</summary>
[TestClass]
[DoNotParallelize]
public sealed class AudioOutputNativeTests
{
    [DataTestMethod]
    [DataRow(16)]
    [DataRow(24)]
    public void IntegerWaveDitherHasNearZeroMeanAndBoundedSampleError(int bitsPerSample)
    {
        SampleFormat format = bitsPerSample switch
        {
            16 => SampleFormat.SAMPLE_INT_16BIT,
            24 => SampleFormat.SAMPLE_INT_24BIT,
            _ => throw new ArgumentOutOfRangeException(nameof(bitsPerSample))
        };
        using var outputDirectory = new TemporaryDirectory();
        using var session = NativeWriterSession.Start(format);
        string outputPathWithoutExtension = outputDirectory.File("dither-" + bitsPerSample);

        const int sampleCountPerSignal = 1 << 20;
        double inputLsb = 0.25d;
        float inputSample = (float)(inputLsb / System.Math.Pow(2d, bitsPerSample - 1));
        float[] inputSignals = [inputSample, -inputSample, 0f];
        float[] samples = new float[checked(sampleCountPerSignal * inputSignals.Length)];
        for (int signalIndex = 0; signalIndex < inputSignals.Length; signalIndex++)
        {
            Array.Fill(
                samples,
                inputSignals[signalIndex],
                checked(signalIndex * sampleCountPerSignal),
                sampleCountPerSignal);
        }

        BassAudioWriter.CreateEncoderWAV(outputPathWithoutExtension);
        Assert.AreEqual(
            bitsPerSample != 24,
            BassAudioWriter.EncoderFlags.HasFlag(EncodeFlags.Dither));
        Assert.IsTrue(BassAudioWriter.EncoderFlags.HasFlag(
            bitsPerSample == 16
                ? EncodeFlags.ConvertFloatTo16BitInt
                : EncodeFlags.ConvertFloatTo24Bit));
        BassAudioWriter.StartRecording();
        BassAudioWriter.WritePcm(samples);
        BassAudioWriter.StopRecording();

        AudioTestWaveFile output = AudioTestWaveFileReader.Read(File.ReadAllBytes(outputPathWithoutExtension + ".wav"));
        Assert.AreEqual((ushort)1, output.Format);
        Assert.AreEqual((ushort)2, output.Channels);
        Assert.AreEqual(48000, output.SampleRate);
        Assert.AreEqual((ushort)bitsPerSample, output.BitsPerSample);
        int bytesPerSample = bitsPerSample / 8;
        Assert.AreEqual(sampleCountPerSignal * inputSignals.Length, output.DataLength / bytesPerSample);
        Assert.AreEqual(0, output.DataLength % bytesPerSample);

        double quantizationScale = System.Math.Pow(2d, bitsPerSample - 1);
        for (int signalIndex = 0; signalIndex < inputSignals.Length; signalIndex++)
        {
            double expectedInput = (double)inputSignals[signalIndex] * quantizationScale;
            double errorSum = 0d;
            double maximumAbsoluteError = 0d;
            int firstSample = checked(signalIndex * sampleCountPerSignal);
            int endSample = checked(firstSample + sampleCountPerSignal);
            for (int sampleIndex = firstSample; sampleIndex < endSample; sampleIndex++)
            {
                int offset = output.DataOffset + sampleIndex * bytesPerSample;
                int quantized = bitsPerSample == 16
                    ? BinaryPrimitives.ReadInt16LittleEndian(output.Bytes.AsSpan(offset, 2))
                    : ReadSigned24(output.Bytes.AsSpan(offset, 3));
                double error = quantized - expectedInput;
                errorSum += error;
                maximumAbsoluteError = System.Math.Max(maximumAbsoluteError, System.Math.Abs(error));
            }

            Assert.IsTrue(
                System.Math.Abs(errorSum / sampleCountPerSignal) <= 0.02d,
                "The " + (signalIndex == 0 ? "positive" : signalIndex == 1 ? "negative" : "silent")
                + " signal's signed conversion error mean must stay within 0.02 LSB; actual="
                + (errorSum / sampleCountPerSignal));
            Assert.IsTrue(
                maximumAbsoluteError <= 2d,
                "Every " + (signalIndex == 0 ? "positive" : signalIndex == 1 ? "negative" : "silent")
                + " sample must stay within 2 LSB.");
        }
    }

    [TestMethod]
    public void TwentyFourBitSoftwareDitherHasExpectedSilenceDistribution()
    {
        using var outputDirectory = new TemporaryDirectory();
        using var session = NativeWriterSession.Start(SampleFormat.SAMPLE_INT_24BIT);
        string outputPathWithoutExtension = outputDirectory.File("dither-silence-24");
        const int sampleCount = 1 << 20;
        float[] silence = new float[sampleCount];

        BassAudioWriter.CreateEncoderWAV(outputPathWithoutExtension);
        Assert.IsFalse(BassAudioWriter.EncoderFlags.HasFlag(EncodeFlags.Dither));
        BassAudioWriter.StartRecording();
        BassAudioWriter.WritePcm(silence);
        BassAudioWriter.StopRecording();

        AudioTestWaveFile output = AudioTestWaveFileReader.Read(File.ReadAllBytes(outputPathWithoutExtension + ".wav"));
        Assert.AreEqual((ushort)24, output.BitsPerSample);
        Assert.AreEqual(sampleCount * 3, output.DataLength);

        int negativeOneCount = 0;
        int zeroCount = 0;
        int positiveOneCount = 0;
        for (int sampleIndex = 0; sampleIndex < sampleCount; sampleIndex++)
        {
            int offset = output.DataOffset + sampleIndex * 3;
            switch (ReadSigned24(output.Bytes.AsSpan(offset, 3)))
            {
                case -1:
                    negativeOneCount++;
                    break;
                case 0:
                    zeroCount++;
                    break;
                case 1:
                    positiveOneCount++;
                    break;
                default:
                    Assert.Fail("Silence dither must produce only -1, 0, or +1 LSB.");
                    break;
            }
        }

        Assert.IsTrue(System.Math.Abs((negativeOneCount / (double)sampleCount) - 0.125d) <= 0.005d);
        Assert.IsTrue(System.Math.Abs((zeroCount / (double)sampleCount) - 0.75d) <= 0.005d);
        Assert.IsTrue(System.Math.Abs((positiveOneCount / (double)sampleCount) - 0.125d) <= 0.005d);
    }

    [TestMethod]
    public void NativeTwentyFourBitConversionPreservesQuantizerGridWithoutDither()
    {
        using var outputDirectory = new TemporaryDirectory();
        using var session = NativeWriterSession.Start(SampleFormat.SAMPLE_INT_24BIT);
        string outputPath = outputDirectory.File("native-24-grid.wav");
        int[] expected = [-(1 << 23), -(1 << 23) + 1, -1, 0, 1, (1 << 23) - 2, (1 << 23) - 1, 0];
        float[] samples = new float[expected.Length];
        for (int sampleIndex = 0; sampleIndex < expected.Length; sampleIndex++)
        {
            samples[sampleIndex] = (float)(expected[sampleIndex] / (double)(1 << 23));
        }

        BassAudioSession ownedSession = BassAudioPlayer.ActiveSession
            ?? throw new InvalidOperationException("The test audio session was not initialized.");
        int encoderHandle = BassEnc.EncodeStart(
            ownedSession.MixerHandle,
            outputPath,
            EncodeFlags.PCM | EncodeFlags.ConvertFloatTo24Bit | EncodeFlags.Pause,
            null,
            IntPtr.Zero);
        Assert.AreNotEqual(0, encoderHandle);
        ExceptionDispatchInfo? encoderFailure = null;
        CaptureCleanup(ref encoderFailure, () => Assert.IsTrue(
            BassEnc.EncodeWrite(encoderHandle, samples, samples.Length * sizeof(float))));
        CaptureCleanup(ref encoderFailure, () => Assert.IsTrue(BassEnc.EncodeStop(encoderHandle)));
        encoderFailure?.Throw();

        AudioTestWaveFile output = AudioTestWaveFileReader.Read(File.ReadAllBytes(outputPath));
        Assert.AreEqual((ushort)24, output.BitsPerSample);
        Assert.AreEqual(expected.Length * 3, output.DataLength);
        for (int sampleIndex = 0; sampleIndex < expected.Length; sampleIndex++)
        {
            int offset = output.DataOffset + sampleIndex * 3;
            Assert.AreEqual(expected[sampleIndex], ReadSigned24(output.Bytes.AsSpan(offset, 3)));
        }
    }

    [TestMethod]
    public void FloatWaveManualWritePreservesSamplesBeyondUnitRange()
    {
        using var outputDirectory = new TemporaryDirectory();
        using var session = NativeWriterSession.Start(SampleFormat.SAMPLE_FLOAT_32BIT);
        string outputPathWithoutExtension = outputDirectory.File("float-range");
        float[] samples = [1.25f, -1.25f, 0.5f, -0.5f];

        BassAudioWriter.CreateEncoderWAV(outputPathWithoutExtension);
        Assert.IsFalse(BassAudioWriter.EncoderFlags.HasFlag(EncodeFlags.Dither));
        Assert.IsFalse(BassAudioWriter.EncoderFlags.HasFlag(EncodeFlags.ConvertFloatTo16BitInt));
        Assert.IsFalse(BassAudioWriter.EncoderFlags.HasFlag(EncodeFlags.ConvertFloatTo24Bit));
        Assert.IsFalse(BassAudioWriter.EncoderFlags.HasFlag(EncodeFlags.ConvertFloatTo32Bit));
        BassAudioWriter.StartRecording();
        BassAudioWriter.WritePcm(samples);
        BassAudioWriter.StopRecording();

        AudioTestWaveFile output = AudioTestWaveFileReader.Read(File.ReadAllBytes(outputPathWithoutExtension + ".wav"));
        Assert.AreEqual((ushort)3, output.Format);
        Assert.AreEqual(48000, output.SampleRate);
        Assert.AreEqual((ushort)32, output.BitsPerSample);
        Assert.AreEqual(samples.Length * sizeof(float), output.DataLength);
        for (int sampleIndex = 0; sampleIndex < samples.Length; sampleIndex++)
        {
            float converted = BitConverter.Int32BitsToSingle(BinaryPrimitives.ReadInt32LittleEndian(
                output.Bytes.AsSpan(output.DataOffset + sampleIndex * sizeof(float), sizeof(float))));
            Assert.AreEqual(samples[sampleIndex], converted);
        }
    }

    private static int ReadSigned24(ReadOnlySpan<byte> sample)
    {
        int value = sample[0] | (sample[1] << 8) | (sample[2] << 16);
        return (value & 0x0080_0000) == 0 ? value : value | unchecked((int)0xFF00_0000);
    }

    private static void CaptureCleanup(ref ExceptionDispatchInfo? primaryFailure, Action cleanup)
    {
        try
        {
            cleanup();
        }
        catch (Exception exception)
        {
            primaryFailure ??= ExceptionDispatchInfo.Capture(exception);
        }
    }

    private sealed class TemporaryDirectory : IDisposable
    {
        private readonly string path = Path.Combine(
            Path.GetTempPath(),
            "BeMusicSeeker.AudioOutputNative." + Guid.NewGuid().ToString("N"));

        internal TemporaryDirectory() => Directory.CreateDirectory(path);

        internal string File(string name) => Path.Combine(path, name);

        public void Dispose() => Directory.Delete(path, recursive: true);
    }

    private sealed class NativeWriterSession : IDisposable
    {
        private readonly SampleRate previousFrequency = BassAudioPlayer.Frequency;
        private readonly SampleFormat previousFormat = BassAudioPlayer.Format;
        private readonly float previousDefaultVolume = BassAudioPlayer.DefaultVolume;
        private readonly float previousDeviceVolume = BassAudioPlayer.DeviceVolume;
        private readonly bool previousDeviceMuted = BassAudioPlayer.IsDeviceMuted;
        private BassAudioSession? ownedSession;
        private bool disposed;

        private NativeWriterSession()
        {
        }

        internal static NativeWriterSession Start(SampleFormat outputFormat)
        {
            var session = new NativeWriterSession();
            try
            {
                if (!BassAudioWriter.TryReleaseEncoder())
                {
                    throw new InvalidOperationException("A previous output encoder still owns native resources.");
                }
                BassAudioPlayer.Free();
                BassAudioRuntime.Shutdown();
                BassAudioPlayer.Frequency = SampleRate.SAMPLE_RATE_48000Hz;
                BassAudioPlayer.Format = outputFormat;
                BassAudioWriter.InitializeOwnedSession(out session.ownedSession);
                return session;
            }
            catch (Exception exception)
            {
                try
                {
                    session.Dispose();
                }
                catch (Exception cleanupException)
                {
                    throw new AggregateException(exception, cleanupException);
                }

                throw;
            }
        }

        public void Dispose()
        {
            if (disposed)
            {
                return;
            }

            disposed = true;
            ExceptionDispatchInfo? failure = null;
            CaptureCleanup(ref failure, () =>
            {
                if (BassAudioWriter.RecordState == PlayState.Playing)
                {
                    BassAudioWriter.StopRecording();
                }
            });
            CaptureCleanup(ref failure, () =>
            {
                if (!BassAudioWriter.TryReleaseEncoder())
                {
                    throw new InvalidOperationException("The output encoder did not release.");
                }
            });
            CaptureCleanup(ref failure, () =>
            {
                if (ownedSession == null)
                {
                    BassAudioPlayer.Free();
                }
                else if (!BassAudioPlayer.Free(ownedSession))
                {
                    throw new InvalidOperationException("The output audio session did not release.");
                }
            });
            CaptureCleanup(ref failure, BassAudioRuntime.Shutdown);
            CaptureCleanup(ref failure, () => BassAudioPlayer.IsDeviceMuted = previousDeviceMuted);
            CaptureCleanup(ref failure, () => BassAudioPlayer.DeviceVolume = previousDeviceVolume);
            CaptureCleanup(ref failure, () => BassAudioPlayer.DefaultVolume = previousDefaultVolume);
            CaptureCleanup(ref failure, () => BassAudioPlayer.Frequency = previousFrequency);
            CaptureCleanup(ref failure, () => BassAudioPlayer.Format = previousFormat);
            ownedSession = null;
            failure?.Throw();
        }

        private static void CaptureCleanup(ref ExceptionDispatchInfo? primaryFailure, Action cleanup)
        {
            try
            {
                cleanup();
            }
            catch (Exception exception)
            {
                primaryFailure ??= ExceptionDispatchInfo.Capture(exception);
            }
        }
    }
}
