using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Runtime.ExceptionServices;
using System.Text;
using ManagedBass;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Ribbit.Media;
using Ribbit.Media.Audio;

namespace BeMusicSeeker.Tests;

/// <summary>production float source、mixer、SRC の native 信号経路を検証します。</summary>
[TestClass]
[DoNotParallelize]
public sealed class AudioMixerSignalTests
{
    // MSTest が各テストの実行前に注入します。
    public TestContext TestContext { get; set; } = null!;

    [TestMethod]
    public void NativeMixer_FloatInputPreservesOverRangeAndSmallSignal()
    {
        float smallSignal = MathF.ScaleB(1f, -20);
        float[] expected = [1.25f, -1.25f, smallSignal, 0f];
        using var wave = TemporaryFloatWave.Create(48000, expected.Length, frame => expected[checked((int)frame)]);
        using var graph = NativeAudioGraph.Start(48000);
        BassAudioPlayer player = graph.CreatePlayer(wave.Path);
        player.Play();

        float[] actual = graph.ReadFrames(expected.Length);

        for (int frame = 0; frame < expected.Length; frame++)
        {
            int expectedBits = BitConverter.SingleToInt32Bits(expected[frame]);
            Assert.AreEqual(expectedBits, BitConverter.SingleToInt32Bits(actual[frame * 2]), $"left frame {frame}");
            Assert.AreEqual(expectedBits, BitConverter.SingleToInt32Bits(actual[frame * 2 + 1]), $"right frame {frame}");
        }
    }

    [TestMethod]
    public void NativeMixer_SumsSourcesBeforeApplyingFinalGain()
    {
        using var positiveA = TemporaryFloatWave.Create(48000, 128, _ => 0.75f);
        using var positiveB = TemporaryFloatWave.Create(48000, 128, _ => 0.75f);
        using var overRangeA = TemporaryFloatWave.Create(48000, 128, _ => 1.25f);
        using var negativeB = TemporaryFloatWave.Create(48000, 128, _ => -0.75f);
        using var graph = NativeAudioGraph.Start(48000);

        BassAudioPlayer first = graph.CreatePlayer(positiveA.Path);
        BassAudioPlayer second = graph.CreatePlayer(positiveB.Path);
        first.Play();
        second.Play();
        float[] mixAboveFullScale = graph.ReadFrames(16);
        foreach (float sample in mixAboveFullScale)
        {
            Assert.AreEqual(1.5f, sample, 0.000001f);
        }

        double gainedPeak = AudioOutputProcessor.ApplyConstantGain(mixAboveFullScale, 0.5d);
        Assert.AreEqual(0.75d, gainedPeak, 0.000001d);
        foreach (float sample in mixAboveFullScale)
        {
            Assert.AreEqual(0.75f, sample, 0.000001f);
        }

        graph.DisposePlayer(first);
        graph.DisposePlayer(second);

        first = graph.CreatePlayer(overRangeA.Path);
        second = graph.CreatePlayer(negativeB.Path);
        first.Play();
        second.Play();
        float[] unclippedSourceSum = graph.ReadFrames(16);
        foreach (float sample in unclippedSourceSum)
        {
            Assert.AreEqual(0.5f, sample, 0.000001f);
        }
    }

    [TestMethod]
    public void NativeMixer_NoRampinPreservesFirstFrameAndRestartFrame()
    {
        using var wave = TemporaryFloatWave.Create(48000, 256, _ => 0.375f);
        using var graph = NativeAudioGraph.Start(48000);
        BassAudioPlayer player = graph.CreatePlayer(wave.Path);

        player.Play();
        float[] firstStartFrame = graph.ReadFrames(1);
        Assert.AreEqual(0.375f, firstStartFrame[0], 0.000001f);
        Assert.AreEqual(0.375f, firstStartFrame[1], 0.000001f);

        player.Stop();
        player.Play();
        float[] firstRestartFrame = graph.ReadFrames(1);
        Assert.AreEqual(0.375f, firstRestartFrame[0], 0.000001f);
        Assert.AreEqual(0.375f, firstRestartFrame[1], 0.000001f);
    }

    [TestMethod]
    public void NativeMixer_SrcToneAmplitudeStaysWithinPointZeroOneDecibel()
    {
        (int SourceRate, int OutputRate)[] ratePairs =
        [
            (44100, 48000),
            (48000, 44100),
            (48000, 96000),
            (96000, 48000)
        ];
        int[] toneFrequencies = [1000, 5000, 10000, 18000];
        const double inputAmplitude = 0.5d;

        foreach ((int sourceRate, int outputRate) in ratePairs)
        {
            using var graph = NativeAudioGraph.Start(outputRate, AudioResamplingQuality.Maximum);
            foreach (int frequency in toneFrequencies)
            {
                using var wave = TemporaryFloatWave.Create(
                    sourceRate,
                    sourceRate,
                    frame => (float)(inputAmplitude * Math.Sin(2d * Math.PI * frequency * frame / sourceRate)));
                BassAudioPlayer player = graph.CreatePlayer(wave.Path);
                player.Play();
                float[] actual = graph.ReadFrames(CeilingOutputFrames(sourceRate, outputRate, sourceRate));
                graph.DisposePlayer(player);

                double measuredAmplitude = MeasureProjectedAmplitude(
                    actual,
                    outputRate,
                    frequency,
                    outputRate / 4,
                    outputRate / 2);
                double errorDb = Math.Abs(20d * Math.Log10(measuredAmplitude / inputAmplitude));
                Assert.IsTrue(
                    errorDb <= 0.01d,
                    $"SRC {sourceRate}->{outputRate} Hz at {frequency} Hz changed amplitude by {errorDb:F6} dB (measured {measuredAmplitude:G9}).");
            }
        }
    }

    [TestMethod]
    public void NativeMixer_DownsamplingRejectsThirtyToFortyTwoKilohertzAliasesBelowMinusNinetyDecibels()
    {
        const int sourceRate = 96000;
        const int outputRate = 48000;
        const double inputAmplitude = 0.5d;
        (int SourceFrequency, int AliasFrequency)[] tones =
        [
            (30000, 18000),
            (36000, 12000),
            (42000, 6000)
        ];

        using var graph = NativeAudioGraph.Start(outputRate, AudioResamplingQuality.Maximum);
        foreach ((int sourceFrequency, int aliasFrequency) in tones)
        {
            using var wave = TemporaryFloatWave.Create(
                sourceRate,
                sourceRate,
                frame => (float)(inputAmplitude * Math.Sin(2d * Math.PI * sourceFrequency * frame / sourceRate)));
            BassAudioPlayer player = graph.CreatePlayer(wave.Path);
            player.Play();
            float[] actual = graph.ReadFrames(CeilingOutputFrames(sourceRate, outputRate, sourceRate));
            graph.DisposePlayer(player);

            double aliasAmplitude = MeasureProjectedAmplitude(
                actual,
                outputRate,
                aliasFrequency,
                outputRate / 4,
                outputRate / 2);
            double aliasDb = 20d * Math.Log10(aliasAmplitude / inputAmplitude);
            Assert.IsTrue(
                aliasDb <= -90d,
                $"SRC {sourceRate}->{outputRate} Hz aliased {sourceFrequency} Hz to {aliasFrequency} Hz at {aliasDb:F2} dB.");
        }
    }

    [TestMethod]
    public void NativeMixer_FiniteSourceEndsAtTheCeilingRateRatioFrame()
    {
        (int SourceRate, int OutputRate)[] ratePairs =
        [
            (44100, 48000),
            (48000, 44100),
            (48000, 96000),
            (96000, 48000)
        ];

        foreach (int quality in new[] { 2, 3, 4, 5, 6 })
        {
            foreach ((int sourceRate, int outputRate) in ratePairs)
            {
                long sourceFrames = sourceRate + 1L;
                int expectedOutputFrames = CeilingOutputFrames(sourceRate, outputRate, sourceFrames);
                using var wave = TemporaryFloatWave.Create(sourceRate, sourceFrames, _ => 0.25f);
                using var graph = NativeAudioGraph.Start(outputRate, quality);
                BassAudioPlayer player = graph.CreatePlayer(wave.Path);
                foreach (int chunkFrames in new[] { 1, 16, 1024 })
                {
                    for (int playback = 0; playback < 2; playback++)
                    {
                        player.Play();
                        float[] buffer = new float[chunkFrames * 2];
                        int readFrames = 0;
                        int limit = expectedOutputFrames + chunkFrames + 1;
                        while (readFrames < limit)
                        {
                            int count = Math.Min(chunkFrames, limit - readFrames);
                            graph.Renderer.ReadFramesExactly(buffer, 0, count);
                            for (int frame = 0; frame < count; frame++)
                            {
                                int absoluteFrame = readFrames + frame;
                                for (int channel = 0; channel < 2; channel++)
                                {
                                    float sample = buffer[frame * 2 + channel];
                                    if (absoluteFrame == expectedOutputFrames - 1)
                                    {
                                        Assert.AreNotEqual(0f, sample,
                                            $"Missing final frame: SRC {quality}, {sourceRate}->{outputRate}, chunk {chunkFrames}, playback {playback}.");
                                    }
                                    else if (absoluteFrame >= expectedOutputFrames)
                                    {
                                        Assert.AreEqual(0f, sample,
                                            $"PCM after mathematical end: SRC {quality}, frame {absoluteFrame}, {sourceRate}->{outputRate}.");
                                    }
                                }
                            }
                            readFrames += count;
                        }
                    }
                }
                graph.DisposePlayer(player);
            }
        }
    }

    [TestMethod]
    [TestCategory("Performance")]
    public void SyntheticAudioWorkloads_ReportLoadRenderWallTimeAndManagedAllocation()
    {
        PerformanceWorkload[] workloads =
        [
            new("short-song", 8, 12000),
            new("many-keysounds", 128, 4800),
            new("long-track", 1, 48000L * 120)
        ];

        foreach (PerformanceWorkload workload in workloads)
        {
            var waves = new List<TemporaryFloatWave>(workload.SourceCount);
            try
            {
                for (int sourceIndex = 0; sourceIndex < workload.SourceCount; sourceIndex++)
                {
                    float value = 0.0625f + (sourceIndex % 4) * 0.0078125f;
                    waves.Add(TemporaryFloatWave.Create(48000, workload.FramesPerSource, _ => value));
                }

                using var graph = NativeAudioGraph.Start(48000);
                GC.Collect();
                GC.WaitForPendingFinalizers();
                GC.Collect();
                long allocatedBefore = GC.GetTotalAllocatedBytes(precise: true);
                var stopwatch = Stopwatch.StartNew();
                foreach (TemporaryFloatWave wave in waves)
                {
                    graph.CreatePlayer(wave.Path).Play();
                }
                graph.DiscardFramesExactly(workload.FramesPerSource);
                graph.DisposePlayers();
                stopwatch.Stop();
                long allocatedBytes = GC.GetTotalAllocatedBytes(precise: true) - allocatedBefore;

                TestContext.WriteLine(
                    $"{workload.Name}: sources={workload.SourceCount}, sourceRate=48000, framesPerSource={workload.FramesPerSource}, outputRate=48000, renderedFrames={workload.FramesPerSource}, elapsedMs={stopwatch.Elapsed.TotalMilliseconds:F3}, managedAllocatedBytes={allocatedBytes}, playerCleanupIncluded=true, waveGenerationIncluded=false, nullDeviceStartupIncluded=false, baselineRecorded=false, compareOnSameMachineAndConfiguration=true.");
            }
            finally
            {
                foreach (TemporaryFloatWave wave in waves)
                {
                    wave.Dispose();
                }
            }
        }
    }

    private static int CeilingOutputFrames(int sourceRate, int outputRate, long sourceFrames)
    {
        long numerator = checked(sourceFrames * outputRate);
        return checked((int)((numerator + sourceRate - 1L) / sourceRate));
    }

    private static double MeasureProjectedAmplitude(
        float[] interleavedStereo,
        int sampleRate,
        int frequency,
        int startFrame,
        int frameCount)
    {
        if (interleavedStereo.Length % 2 != 0
            || startFrame < 0
            || frameCount <= 0
            || (long)(startFrame + frameCount) * 2 > interleavedStereo.Length)
        {
            throw new ArgumentOutOfRangeException(nameof(frameCount));
        }

        double sineProjection = 0d;
        double cosineProjection = 0d;
        double sineNorm = 0d;
        double cosineNorm = 0d;
        for (int frame = startFrame; frame < startFrame + frameCount; frame++)
        {
            double phase = 2d * Math.PI * frequency * frame / sampleRate;
            double sine = Math.Sin(phase);
            double cosine = Math.Cos(phase);
            double sample = interleavedStereo[frame * 2];
            sineProjection += sample * sine;
            cosineProjection += sample * cosine;
            sineNorm += sine * sine;
            cosineNorm += cosine * cosine;
        }

        double sineAmplitude = sineProjection / sineNorm;
        double cosineAmplitude = cosineProjection / cosineNorm;
        return Math.Sqrt(sineAmplitude * sineAmplitude + cosineAmplitude * cosineAmplitude);
    }

    private readonly record struct PerformanceWorkload(string Name, int SourceCount, long FramesPerSource);

    private sealed class NativeAudioGraph : IDisposable
    {
        private const int PullBufferFrames = 16384;

        private readonly SampleRate previousFrequency;
        private readonly SampleFormat previousFormat;
        private readonly float previousDefaultVolume;
        private readonly float previousDeviceVolume;
        private readonly bool previousMute;
        private readonly List<BassAudioPlayer> players = [];
        private readonly float[] pullBuffer = new float[PullBufferFrames * 2];
        private readonly AudioSourceCache sourceCache = new();
        private BassAudioSession? session;
        private bool disposed;

        private NativeAudioGraph(int outputRate, int sampleRateConversionQuality)
        {
            previousFrequency = BassAudioPlayer.Frequency;
            previousFormat = BassAudioPlayer.Format;
            previousDefaultVolume = BassAudioPlayer.DefaultVolume;
            previousDeviceVolume = BassAudioPlayer.DeviceVolume;
            previousMute = BassAudioPlayer.IsDeviceMuted;

            try
            {
                BassAudioPlayer.Free();
                if (BassAudioPlayer.ActiveSession != null)
                {
                    throw new InvalidOperationException("A previous native audio session did not release.");
                }
                BassAudioRuntime.Shutdown();
                BassAudioPlayer.Frequency = (SampleRate)outputRate;
                BassAudioPlayer.Format = SampleFormat.SAMPLE_FLOAT_32BIT;
                BassAudioPlayer.InitializeOwned(
                    BassAudioPlayer.DeviceDriver.NULL_DEVICE,
                    default,
                    0f,
                    out session,
                    sampleRateConversionQuality);
                BassAudioPlayer.DefaultVolume = 1f;
                BassAudioPlayer.DeviceVolume = 1f;
                BassAudioPlayer.IsDeviceMuted = false;

                BassAudioSession initializedSession = session
                    ?? throw new InvalidOperationException("Null-device initialization returned no owning session.");
                ChannelInfo mixer = Bass.ChannelGetInfo(initializedSession.MixerHandle);
                if (mixer.Frequency != outputRate || mixer.Channels != 2)
                {
                    throw new InvalidOperationException(
                        $"The null mixer exposed {mixer.Frequency} Hz / {mixer.Channels} channels; expected {outputRate} Hz / 2 channels.");
                }
                Renderer = new AudioPcmRenderer(initializedSession.MixerHandle, mixer.Frequency, mixer.Channels);
            }
            catch (Exception initializationFailure)
            {
                try
                {
                    Dispose();
                }
                catch (Exception cleanupFailure)
                {
                    throw new AggregateException("Native audio graph initialization and cleanup both failed.", initializationFailure, cleanupFailure);
                }
                ExceptionDispatchInfo.Capture(initializationFailure).Throw();
                throw;
            }
        }

        internal AudioPcmRenderer Renderer { get; }

        internal static NativeAudioGraph Start(
            int outputRate,
            int sampleRateConversionQuality = AudioResamplingQuality.Default)
            => new(outputRate, sampleRateConversionQuality);

        internal BassAudioPlayer CreatePlayer(string path)
        {
            BassAudioPlayer player = new(path, sourceCache);
            players.Add(player);
            return player;
        }

        internal float[] ReadFrames(int frameCount)
        {
            float[] output = new float[checked(frameCount * 2)];
            Renderer.ReadFramesExactly(output, 0, frameCount);
            return output;
        }

        internal void DiscardFramesExactly(long frameCount)
        {
            long remaining = frameCount;
            while (remaining > 0)
            {
                int frames = checked((int)Math.Min(remaining, PullBufferFrames));
                Renderer.ReadFramesExactly(pullBuffer, 0, frames);
                remaining -= frames;
            }
        }

        internal void DisposePlayer(BassAudioPlayer player)
        {
            players.Remove(player);
            player.Dispose();
        }

        internal void DisposePlayers()
        {
            for (int index = players.Count - 1; index >= 0; index--)
            {
                DisposePlayer(players[index]);
            }
        }

        public void Dispose()
        {
            if (disposed)
            {
                return;
            }
            disposed = true;
            Exception? cleanupFailure = null;
            for (int index = players.Count - 1; index >= 0; index--)
            {
                try
                {
                    players[index].Dispose();
                }
                catch (Exception exception)
                {
                    cleanupFailure ??= exception;
                }
            }
            players.Clear();

            bool sessionReleased = false;
            try
            {
                if (session == null)
                {
                    BassAudioPlayer.Free();
                }
                bool released = session == null ? BassAudioPlayer.ActiveSession == null : BassAudioPlayer.Free(session);
                if (!released)
                {
                    cleanupFailure ??= new InvalidOperationException("The native mixer test session did not release.");
                }
                else
                {
                    sessionReleased = true;
                    session = null;
                }
            }
            catch (Exception exception)
            {
                cleanupFailure ??= exception;
            }

            try
            {
                if (sessionReleased)
                {
                    BassAudioRuntime.Shutdown();
                }
            }
            catch (Exception exception)
            {
                cleanupFailure ??= exception;
            }
            finally
            {
                BassAudioPlayer.IsDeviceMuted = previousMute;
                BassAudioPlayer.DeviceVolume = previousDeviceVolume;
                BassAudioPlayer.DefaultVolume = previousDefaultVolume;
                BassAudioPlayer.Frequency = previousFrequency;
                BassAudioPlayer.Format = previousFormat;
            }

            if (cleanupFailure != null)
            {
                throw new InvalidOperationException("Native mixer test cleanup failed.", cleanupFailure);
            }
        }
    }

    private sealed class TemporaryFloatWave : IDisposable
    {
        private TemporaryFloatWave(string path)
        {
            Path = path;
        }

        internal string Path { get; }

        internal static TemporaryFloatWave Create(int sampleRate, long frameCount, Func<long, float> sample)
        {
            ArgumentNullException.ThrowIfNull(sample);
            long dataLength = checked(frameCount * sizeof(float));
            if (sampleRate <= 0
                || frameCount < 0
                || dataLength > uint.MaxValue - 48L)
            {
                throw new ArgumentOutOfRangeException(nameof(frameCount));
            }

            string path = System.IO.Path.Combine(
                System.IO.Path.GetTempPath(),
                "BeMusicSeeker.AudioMixerSignal." + Guid.NewGuid().ToString("N") + ".wav");
            try
            {
                using FileStream stream = File.Create(path);
                using var writer = new BinaryWriter(stream, Encoding.ASCII, leaveOpen: true);
                writer.Write(Encoding.ASCII.GetBytes("RIFF"));
                writer.Write(checked((uint)(48L + dataLength)));
                writer.Write(Encoding.ASCII.GetBytes("WAVE"));
                WriteChunkHeader(writer, "fmt ", 16);
                writer.Write((ushort)3);
                writer.Write((ushort)1);
                writer.Write(sampleRate);
                writer.Write(checked(sampleRate * sizeof(float)));
                writer.Write((ushort)sizeof(float));
                writer.Write((ushort)32);
                WriteChunkHeader(writer, "fact", 4);
                writer.Write(checked((uint)frameCount));
                WriteChunkHeader(writer, "data", checked((uint)dataLength));
                for (long frame = 0; frame < frameCount; frame++)
                {
                    float value = sample(frame);
                    if (!float.IsFinite(value))
                    {
                        throw new ArgumentException("A generated float WAVE fixture must contain only finite samples.", nameof(sample));
                    }
                    writer.Write(value);
                }
            }
            catch
            {
                File.Delete(path);
                throw;
            }

            return new TemporaryFloatWave(path);
        }

        private static void WriteChunkHeader(BinaryWriter writer, string id, uint length)
        {
            writer.Write(Encoding.ASCII.GetBytes(id));
            writer.Write(length);
        }

        public void Dispose() => File.Delete(Path);
    }
}
