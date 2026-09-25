using System;
using System.Buffers.Binary;
using System.IO;
using System.Runtime.ExceptionServices;
using System.Text;
using BeMusicSeeker.Tests.Helpers;
using ManagedBass;
using ManagedBass.Enc;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Ribbit.BMS;
using Ribbit.Media;
using Ribbit.Media.Audio;

namespace BeMusicSeeker.Tests;

[TestClass]
[DoNotParallelize]
public sealed class BMSAutoPlayWriterTests
{
    private SampleRate previousFrequency;
    private SampleFormat previousFormat;
    private float previousDefaultVolume;
    private float previousDeviceVolume;
    private bool previousDeviceMuted;
    private BassAudioSession? ownedSession;

    [TestInitialize]
    public void InitializeAudioRuntime()
    {
        previousFrequency = BassAudioPlayer.Frequency;
        previousFormat = BassAudioPlayer.Format;
        previousDefaultVolume = BassAudioPlayer.DefaultVolume;
        previousDeviceVolume = BassAudioPlayer.DeviceVolume;
        previousDeviceMuted = BassAudioPlayer.IsDeviceMuted;

        BassAudioPlayer.Free();
        BassAudioRuntime.Shutdown();
        BassAudioPlayer.Frequency = SampleRate.SAMPLE_RATE_48000Hz;
        BassAudioPlayer.Format = SampleFormat.SAMPLE_FLOAT_32BIT;
        BassAudioWriter.InitializeOwnedSession(out ownedSession);
        BassAudioPlayer.IsDeviceMuted = false;
        BassAudioPlayer.DeviceVolume = 0.23f;
        BassAudioPlayer.IsDeviceMuted = true;
    }

    [TestCleanup]
    public void ShutdownAudioRuntime()
    {
        ExceptionDispatchInfo? failure = null;
        CaptureCleanup(ref failure, () =>
        {
            if (!BassAudioWriter.TryReleaseEncoder())
            {
                throw new InvalidOperationException("The writer encoder owner did not release.");
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
                throw new InvalidOperationException("The writer audio session did not release.");
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

    [TestMethod]
    public void Write_RendersInitialNoteOnceAndIgnoresDeviceVolumeAndMute()
    {
        using var directory = new TemporaryDirectory();
        File.WriteAllBytes(directory.File("audio.wav"), BuildPcmWave());
        File.WriteAllText(
            directory.File("chart.bms"),
            "#PLAYER 1\n#TITLE offline output\n#ARTIST test\n#BPM 120\n"
                + "#WAV01 audio.wav\n#00101:01\n",
            Encoding.ASCII);
        string outputWithoutExtension = directory.File("converted");

        using var writer = new BMSAutoPlayWriter(new BMSFile(directory.File("chart.bms")));
        writer.LoadResources();
        long expectedFrameCount = AudioPcmRenderer.TimeToFrame(writer.Duration, 48000);

        writer.Write(
            EncoderType.WAVE,
            quality: 0.4f,
            outputWithoutExtension,
            BMSAutoPlayWriter.Normalization.PEAK_LEVEL);

        Assert.AreEqual(0.23f, BassAudioPlayer.DeviceVolume);
        Assert.IsTrue(BassAudioPlayer.IsDeviceMuted);

        byte[] output = File.ReadAllBytes(outputWithoutExtension + ".wav");
        AudioTestWaveFile wave = AudioTestWaveFileReader.Read(output);
        Assert.AreEqual((ushort)3, wave.Format);
        int channels = wave.Channels;
        int sampleRate = wave.SampleRate;
        int dataLength = wave.DataLength;
        Assert.AreEqual(48000, sampleRate);
        Assert.AreEqual(0, dataLength % (channels * sizeof(float)));
        Assert.AreEqual(expectedFrameCount, dataLength / (channels * sizeof(float)));

        float peak = 0f;
        for (int offset = wave.DataOffset; offset < wave.DataOffset + dataLength; offset += sizeof(float))
        {
            float sample = BitConverter.Int32BitsToSingle(BinaryPrimitives.ReadInt32LittleEndian(output.AsSpan(offset, sizeof(float))));
            Assert.IsTrue(float.IsFinite(sample));
            peak = Math.Max(peak, Math.Abs(sample));
        }
        Assert.IsTrue(peak > 0.98f, "The note at time zero must be rendered and normalized.");
        Assert.IsTrue(peak <= 0.991f, "Peak normalization must use the rendered PCM without clipping.");
    }

    [TestMethod]
    public void OfflineNormalizationUsesWholeRenderRmsAndFixedNoneGain()
    {
        using var directory = new TemporaryDirectory();

        AudioTestWaveFile peakOutput = RenderChart(
            directory,
            "peak",
            BMSAutoPlayWriter.Normalization.PEAK_LEVEL,
            silentInput: false);
        AudioTestWaveFile rmsOutput = RenderChart(
            directory,
            "rms",
            BMSAutoPlayWriter.Normalization.RMS_VALUE,
            silentInput: false);
        AudioTestWaveFile noneOutput = RenderChart(
            directory,
            "none",
            BMSAutoPlayWriter.Normalization.NONE,
            silentInput: false);
        AudioTestWaveFile amplifiedNoneOutput = RenderChart(
            directory,
            "amplified-none",
            BMSAutoPlayWriter.Normalization.NONE,
            silentInput: false,
            normalizationAmplifier: 0.5f);

        Assert.AreEqual(0.99d, MeasureWave(peakOutput).Peak, 0.002d);
        Assert.AreEqual(0.4d, MeasureWave(rmsOutput).Rms, 0.002d);
        Assert.AreEqual(0.08d, MeasureWave(noneOutput).Peak, 0.002d);
        Assert.AreEqual(0.04d, MeasureWave(amplifiedNoneOutput).Peak, 0.002d);
    }

    [DataTestMethod]
    [DataRow(BMSAutoPlayWriter.Normalization.PEAK_LEVEL)]
    [DataRow(BMSAutoPlayWriter.Normalization.RMS_VALUE)]
    public void SilentInputRemainsSilentForPeakAndRmsNormalization(
        BMSAutoPlayWriter.Normalization normalization)
    {
        using var directory = new TemporaryDirectory();
        AudioTestWaveFile output = RenderChart(directory, "silent", normalization, silentInput: true);
        (double peak, double rms) = MeasureWave(output);

        Assert.AreEqual(0d, peak);
        Assert.AreEqual(0d, rms);
    }

    [TestMethod]
    public void EmptyRenderFailsBeforeEncoderCreationWithoutChangingExistingOutput()
    {
        using var directory = new TemporaryDirectory();
        File.WriteAllText(
            directory.File("empty.bms"),
            "#PLAYER 1\n#TITLE empty\n#ARTIST test\n#BPM 120\n#00011:00\n",
            Encoding.ASCII);
        byte[] existingOutput = [1, 2, 3];
        File.WriteAllBytes(directory.File("empty-output.wav"), existingOutput);
        string? existingCommand = BassAudioWriter.EncoderCommandLine;
        PlayState existingState = BassAudioWriter.RecordState;
        using (var writer = new BMSAutoPlayWriter(new BMSFile(directory.File("empty.bms"))))
        {
            writer.LoadResources();
            Assert.AreEqual(TimeSpan.Zero, writer.Duration);
            Assert.ThrowsException<InvalidOperationException>(() => writer.Write(
                EncoderType.WAVE,
                quality: 0.4f,
                directory.File("empty-output"),
                BMSAutoPlayWriter.Normalization.PEAK_LEVEL));
        }
        CollectionAssert.AreEqual(existingOutput, File.ReadAllBytes(directory.File("empty-output.wav")));
        Assert.IsFalse(File.Exists(directory.File("empty-output (2).wav")));
        Assert.AreEqual(existingCommand, BassAudioWriter.EncoderCommandLine);
        Assert.AreEqual(existingState, BassAudioWriter.RecordState);
    }

    [TestMethod]
    public void InvalidAmplifiersFailBeforeEncoderCreation()
    {
        using var directory = new TemporaryDirectory();
        File.WriteAllBytes(directory.File("audio.wav"), BuildPcmWave(false));
        File.WriteAllText(directory.File("chart.bms"),
            "#PLAYER 1\n#TITLE valid\n#ARTIST test\n#BPM 120\n#WAV01 audio.wav\n#00001:01\n", Encoding.ASCII);
        using var invalidWriter = new BMSAutoPlayWriter(new BMSFile(directory.File("chart.bms")));
        invalidWriter.LoadResources();
        Assert.IsTrue(invalidWriter.Duration > TimeSpan.Zero);
        string? existingEncoderCommand = BassAudioWriter.EncoderCommandLine;
        Assert.ThrowsException<ArgumentOutOfRangeException>(() => invalidWriter.Write(
            EncoderType.WAVE,
            0.4f,
            directory.File("negative-amplifier"),
            BMSAutoPlayWriter.Normalization.NONE,
            normalizationAmplifier: -0.1f));
        Assert.ThrowsException<ArgumentOutOfRangeException>(() => invalidWriter.Write(
            EncoderType.WAVE,
            0.4f,
            directory.File("non-finite-amplifier"),
            BMSAutoPlayWriter.Normalization.NONE,
            normalizationAmplifier: float.NaN));
        Assert.AreEqual(existingEncoderCommand, BassAudioWriter.EncoderCommandLine);
        Assert.IsFalse(File.Exists(directory.File("negative-amplifier.wav")));
        Assert.IsFalse(File.Exists(directory.File("non-finite-amplifier.wav")));
    }

    [TestMethod]
    public void IntegerOutputRejectsAnOverRangeNormalizedRenderBeforeEncoderStart()
    {
        ReinitializeAudioRuntime(SampleFormat.SAMPLE_INT_16BIT);
        using var directory = new TemporaryDirectory();
        File.WriteAllBytes(directory.File("audio.wav"), BuildPcmWave());
        File.WriteAllText(
            directory.File("chart.bms"),
            "#PLAYER 1\n#TITLE integer range\n#ARTIST test\n#BPM 120\n"
                + "#WAV01 audio.wav\n#00101:01\n",
            Encoding.ASCII);
        using var writer = new BMSAutoPlayWriter(new BMSFile(directory.File("chart.bms")));
        writer.LoadResources();

        AudioOutputRangeException exception = Assert.ThrowsException<AudioOutputRangeException>(() => writer.Write(
            EncoderType.WAVE,
            0.4f,
            directory.File("over-range"),
            BMSAutoPlayWriter.Normalization.NONE,
            normalizationAmplifier: 16f));

        Assert.IsTrue(exception.Peak > 1d);
        Assert.AreEqual(20d * Math.Log10(exception.Peak), exception.RequiredAttenuationDb, 1e-12d);
        Assert.AreEqual(PlayState.Stopped, BassAudioWriter.RecordState);
        Assert.IsTrue(BassAudioWriter.EncoderFlags.HasFlag(EncodeFlags.ConvertFloatTo16BitInt));
        Assert.IsTrue(BassAudioWriter.EncoderFlags.HasFlag(EncodeFlags.Dither));
    }

    private static AudioTestWaveFile RenderChart(
        TemporaryDirectory directory,
        string name,
        BMSAutoPlayWriter.Normalization normalization,
        bool silentInput,
        float normalizationAmplifier = 1f)
    {
        File.WriteAllBytes(directory.File(name + ".wav"), BuildPcmWave(silentInput));
        File.WriteAllText(
            directory.File(name + ".bms"),
            "#PLAYER 1\n#TITLE " + name + "\n#ARTIST test\n#BPM 120\n"
                + "#WAV01 " + name + ".wav\n#00101:01\n",
            Encoding.ASCII);
        string outputWithoutExtension = directory.File(name + "-output");
        using (var writer = new BMSAutoPlayWriter(new BMSFile(directory.File(name + ".bms"))))
        {
            writer.LoadResources();
            long expectedFrames = AudioPcmRenderer.TimeToFrame(writer.Duration, 48000);
            writer.Write(
                EncoderType.WAVE,
                0.4f,
                outputWithoutExtension,
                normalization,
                normalizationAmplifier);
            AudioTestWaveFile output = AudioTestWaveFileReader.Read(File.ReadAllBytes(outputWithoutExtension + ".wav"));
            int sampleCount = output.DataLength / sizeof(float);
            Assert.AreEqual(expectedFrames, sampleCount / output.Channels);
            return output;
        }
    }

    private void ReinitializeAudioRuntime(SampleFormat outputFormat)
    {
        if (!BassAudioWriter.TryReleaseEncoder())
        {
            throw new InvalidOperationException("The prior encoder did not release before output-format setup.");
        }
        if (ownedSession != null && !BassAudioPlayer.Free(ownedSession))
        {
            throw new InvalidOperationException("The prior audio session did not release before output-format setup.");
        }

        ownedSession = null;
        BassAudioRuntime.Shutdown();
        BassAudioPlayer.Frequency = SampleRate.SAMPLE_RATE_48000Hz;
        BassAudioPlayer.Format = outputFormat;
        BassAudioWriter.InitializeOwnedSession(out ownedSession);
    }

    private static (double Peak, double Rms) MeasureWave(AudioTestWaveFile wave)
    {
        Assert.AreEqual((ushort)3, wave.Format);
        Assert.AreEqual((ushort)32, wave.BitsPerSample);
        double peak = 0d;
        double squareSum = 0d;
        int sampleCount = wave.DataLength / sizeof(float);
        for (int offset = wave.DataOffset; offset < wave.DataOffset + wave.DataLength; offset += sizeof(float))
        {
            float sample = BitConverter.Int32BitsToSingle(BinaryPrimitives.ReadInt32LittleEndian(
                wave.Bytes.AsSpan(offset, sizeof(float))));
            Assert.IsTrue(float.IsFinite(sample));
            peak = Math.Max(peak, Math.Abs((double)sample));
            squareSum += (double)sample * sample;
        }

        return (peak, sampleCount == 0 ? 0d : Math.Sqrt(squareSum / sampleCount));
    }

    private static byte[] BuildPcmWave(bool silence = false)
    {
        const int sampleRate = 44100;
        const int sampleCount = sampleRate / 10;
        int dataLength = sampleCount * sizeof(short);
        byte[] wave = new byte[44 + dataLength];
        using var stream = new MemoryStream(wave);
        using var output = new BinaryWriter(stream, Encoding.ASCII, leaveOpen: true);
        output.Write(Encoding.ASCII.GetBytes("RIFF"));
        output.Write(36 + dataLength);
        output.Write(Encoding.ASCII.GetBytes("WAVEfmt "));
        output.Write(16);
        output.Write((short)1);
        output.Write((short)1);
        output.Write(sampleRate);
        output.Write(sampleRate * sizeof(short));
        output.Write((short)sizeof(short));
        output.Write((short)16);
        output.Write(Encoding.ASCII.GetBytes("data"));
        output.Write(dataLength);
        for (int index = 0; index < sampleCount; index++)
        {
            double phase = 2d * Math.PI * 440d * index / sampleRate;
            output.Write(silence
                ? (short)0
                : (short)Math.Round(Math.Sin(phase) * short.MaxValue * 0.5d));
        }

        return wave;
    }

    private sealed class TemporaryDirectory : IDisposable
    {
        private readonly string path = Path.Combine(
            Path.GetTempPath(),
            "BeMusicSeeker.BMSAudioWriter." + Guid.NewGuid().ToString("N"));

        internal TemporaryDirectory()
        {
            Directory.CreateDirectory(path);
        }

        internal string File(string name) => Path.Combine(path, name);

        public void Dispose() => Directory.Delete(path, recursive: true);
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
