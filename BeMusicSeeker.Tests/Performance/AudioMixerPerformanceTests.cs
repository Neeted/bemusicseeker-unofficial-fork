using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Runtime.InteropServices;
using ManagedBass;
using ManagedBass.Mix;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Ribbit.Media;
using Ribbit.Media.Audio;

namespace BeMusicSeeker.Tests;

/// <summary>共有BASS null-device graphの音声負荷を一定仕事量で測定します。</summary>
[TestClass]
[DoNotParallelize]
public sealed class AudioMixerPerformanceTests
{
    private const ChannelAttribute MixerThreadsAttribute = (ChannelAttribute)0x15001;

    public TestContext TestContext { get; set; } = null!;

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
            var waves = new List<AudioMixerSignalTests.TemporaryFloatWave>(workload.SourceCount);
            try
            {
                for (int sourceIndex = 0; sourceIndex < workload.SourceCount; sourceIndex++)
                {
                    float value = 0.0625f + (sourceIndex % 4) * 0.0078125f;
                    waves.Add(AudioMixerSignalTests.TemporaryFloatWave.Create(
                        48000,
                        workload.FramesPerSource,
                        _ => value));
                }

                using var graph = AudioMixerSignalTests.NativeAudioGraph.Start(48000);
                float actualNativeMixerThreads = ReadNativeMixerThreadCount(graph);
                Assert.AreEqual(1f, actualNativeMixerThreads);
                GC.Collect();
                GC.WaitForPendingFinalizers();
                GC.Collect();
                long allocatedBefore = GC.GetTotalAllocatedBytes(precise: true);
                var stopwatch = Stopwatch.StartNew();
                foreach (AudioMixerSignalTests.TemporaryFloatWave wave in waves)
                {
                    graph.CreatePlayer(wave.Path).Play();
                }
                graph.DiscardFramesExactly(workload.FramesPerSource);
                graph.DisposePlayers();
                stopwatch.Stop();
                long allocatedBytes = GC.GetTotalAllocatedBytes(precise: true) - allocatedBefore;

                TestContext.WriteLine(
                    $"{workload.Name}: sources={workload.SourceCount}, sourceRate=48000, framesPerSource={workload.FramesPerSource}, outputRate=48000, renderedFrames={workload.FramesPerSource}, actualNativeMixerThreads={actualNativeMixerThreads}, threadCountSource=Renderer.Channel native readback, readbackIncludedInMeasurement=false, elapsedMs={stopwatch.Elapsed.TotalMilliseconds:F3}, managedAllocatedBytes={allocatedBytes}, playerCleanupIncluded=true, waveGenerationIncluded=false, nullDeviceStartupIncluded=false, baselineRecorded=false, compareOnSameMachineAndConfiguration=true.");
            }
            finally
            {
                foreach (AudioMixerSignalTests.TemporaryFloatWave wave in waves)
                {
                    wave.Dispose();
                }
            }
        }
    }

    [TestMethod]
    [TestCategory("Performance")]
    public void MixedRateManyVoiceDefaultSrcWorkload_ReportsColdLoadPlayRenderAndCleanup()
    {
        const int sourcesPerRate = 64;
        const int sourceDurationSeconds = 2;
        const int outputRate = 192000;
        const int pullFrames = 256;
        const int repetitions = 3;
        const int sampleRateConversionQuality = AudioResamplingQuality.Default;
#if DEBUG
        const string buildConfiguration = "Debug";
#else
        const string buildConfiguration = "Release";
#endif
        long renderedFramesPerRepetition = checked((long)outputRate * sourceDurationSeconds);
        var waves = new List<AudioMixerSignalTests.TemporaryFloatWave>(sourcesPerRate * 2);
        double[] loadMilliseconds = new double[repetitions];
        double[] playMilliseconds = new double[repetitions];
        double[] renderMilliseconds = new double[repetitions];
        double[] cleanupMilliseconds = new double[repetitions];
        double[] totalMilliseconds = new double[repetitions];
        long[] managedAllocatedBytes = new long[repetitions];

        try
        {
            for (int sourceIndex = 0; sourceIndex < sourcesPerRate * 2; sourceIndex++)
            {
                int sourceRate = sourceIndex < sourcesPerRate ? 44100 : 48000;
                int toneIndex = sourceIndex % sourcesPerRate;
                int frequency = 440 + (toneIndex % 12) * 110;
                long sourceFrames = checked((long)sourceRate * sourceDurationSeconds);
                waves.Add(AudioMixerSignalTests.TemporaryFloatWave.Create(
                    sourceRate,
                    sourceFrames,
                    frame => (float)(0.125d * Math.Sin(2d * Math.PI * frequency * frame / sourceRate))));
            }

            TestContext.WriteLine(
                $"condition: machine={Environment.MachineName}, os={Environment.OSVersion.VersionString}, runtime={RuntimeInformation.FrameworkDescription}, configuration={buildConfiguration}, processors={Environment.ProcessorCount}, bass={Bass.Version}, bassmix={BassMix.Version}, sourceRates=44100,48000, outputRate={outputRate}, sourcesPerRate={sourcesPerRate}, sourceDurationSeconds={sourceDurationSeconds}, voices={waves.Count}, pullFrames={pullFrames}, srcQuality={sampleRateConversionQuality}, renderedFramesPerRepetition={renderedFramesPerRepetition}, repetitions={repetitions}, threadCountObservation=actual native Renderer.Channel readback per repetition, warmupPulls=1, warmupFrames={pullFrames}, warmupSignal=silence, warmupSourcesActive=false, srcWarmup=false, waveGenerationIncluded=false, nativeStartupIncluded=false, bufferAllocationIncluded=false, sessionCleanupIncluded=false.");

            for (int repetition = 0; repetition < repetitions; repetition++)
            {
                using var graph = AudioMixerSignalTests.NativeAudioGraph.Start(outputRate);
                Assert.AreEqual(sampleRateConversionQuality, graph.SampleRateConversionQuality);
                float actualNativeMixerThreads = ReadNativeMixerThreadCount(graph);
                Assert.AreEqual(1f, actualNativeMixerThreads);
                TestContext.WriteLine(
                    $"repetition={repetition + 1}: actualNativeMixerThreads={actualNativeMixerThreads}, source=Renderer.Channel native readback, readbackIncludedInMeasurement=false.");
                float[] warmupPcm = graph.ReadFrames(pullFrames);
                foreach (float sample in warmupPcm)
                {
                    Assert.AreEqual(0f, sample, "The one pre-measurement pull must be silent with no sources active.");
                }

                GC.Collect();
                GC.WaitForPendingFinalizers();
                GC.Collect();
                var players = new List<BassAudioPlayer>(waves.Count);
                long allocatedBefore = GC.GetTotalAllocatedBytes(precise: true);
                var totalStopwatch = Stopwatch.StartNew();
                var phaseStopwatch = Stopwatch.StartNew();
                foreach (AudioMixerSignalTests.TemporaryFloatWave wave in waves)
                {
                    players.Add(graph.CreatePlayer(wave.Path));
                }
                phaseStopwatch.Stop();
                loadMilliseconds[repetition] = phaseStopwatch.Elapsed.TotalMilliseconds;

                phaseStopwatch.Restart();
                foreach (BassAudioPlayer player in players)
                {
                    player.Play();
                }
                phaseStopwatch.Stop();
                playMilliseconds[repetition] = phaseStopwatch.Elapsed.TotalMilliseconds;

                phaseStopwatch.Restart();
                graph.DiscardFramesExactly(renderedFramesPerRepetition, pullFrames);
                phaseStopwatch.Stop();
                renderMilliseconds[repetition] = phaseStopwatch.Elapsed.TotalMilliseconds;

                phaseStopwatch.Restart();
                graph.DisposePlayers();
                phaseStopwatch.Stop();
                cleanupMilliseconds[repetition] = phaseStopwatch.Elapsed.TotalMilliseconds;
                totalStopwatch.Stop();
                long allocatedBytes = GC.GetTotalAllocatedBytes(precise: true) - allocatedBefore;
                totalMilliseconds[repetition] = totalStopwatch.Elapsed.TotalMilliseconds;
                managedAllocatedBytes[repetition] = allocatedBytes;
                TestContext.WriteLine(
                    $"repetition={repetition + 1}: loadMs={loadMilliseconds[repetition]:F3}, playMs={playMilliseconds[repetition]:F3}, renderMs={renderMilliseconds[repetition]:F3}, cleanupMs={cleanupMilliseconds[repetition]:F3}, totalMs={totalMilliseconds[repetition]:F3}, managedAllocatedBytes={managedAllocatedBytes[repetition]}, loadedSources={waves.Count}, playedVoices={waves.Count}, renderedFrames={renderedFramesPerRepetition}, playerCleanupIncluded=true.");
            }
        }
        finally
        {
            foreach (AudioMixerSignalTests.TemporaryFloatWave wave in waves)
            {
                wave.Dispose();
            }
        }
    }

    [TestMethod]
    [TestCategory("Performance")]
    public void DefaultSrcFrequencyResponse_RecordsAllThirtyFiveConditions()
    {
        const int quality = AudioResamplingQuality.Default;
        const double inputAmplitude = 0.5d;
        (int SourceRate, int OutputRate)[] ratePairs =
        [
            (44100, 48000),
            (48000, 44100),
            (48000, 96000),
            (96000, 48000),
            (44100, 192000),
            (48000, 192000),
            (44100, 384000),
            (48000, 384000)
        ];
        int[] toneFrequencies = [1000, 5000, 10000, 18000];
        int conditionCount = 0;

        foreach ((int sourceRate, int outputRate) in ratePairs)
        {
            using var graph = AudioMixerSignalTests.NativeAudioGraph.Start(outputRate, quality);
            Assert.AreEqual(quality, graph.SampleRateConversionQuality);
            float actualNativeMixerThreads = ReadNativeMixerThreadCount(graph);
            Assert.AreEqual(1f, actualNativeMixerThreads);
            foreach (int frequency in toneFrequencies)
            {
                RecordDefaultSrcFrequencyResponse(
                    graph,
                    sourceRate,
                    outputRate,
                    frequency,
                    frequency,
                    actualNativeMixerThreads,
                    inputAmplitude);
                conditionCount++;
            }
        }

        using (var graph = AudioMixerSignalTests.NativeAudioGraph.Start(48000, quality))
        {
            Assert.AreEqual(quality, graph.SampleRateConversionQuality);
            float actualNativeMixerThreads = ReadNativeMixerThreadCount(graph);
            Assert.AreEqual(1f, actualNativeMixerThreads);
            (int InputFrequency, int AliasFrequency)[] aliases =
            [
                (30000, 18000),
                (36000, 12000),
                (42000, 6000)
            ];
            foreach ((int inputFrequency, int aliasFrequency) in aliases)
            {
                RecordDefaultSrcFrequencyResponse(
                    graph,
                    sourceRate: 96000,
                    outputRate: 48000,
                    inputFrequency,
                    aliasFrequency,
                    actualNativeMixerThreads,
                    inputAmplitude);
                conditionCount++;
            }
        }

        Assert.AreEqual(35, conditionCount, "Every SRC4 frequency condition must produce a diagnostic record.");
    }

    private void RecordDefaultSrcFrequencyResponse(
        AudioMixerSignalTests.NativeAudioGraph graph,
        int sourceRate,
        int outputRate,
        int inputFrequency,
        int observedFrequency,
        float actualNativeMixerThreads,
        double inputAmplitude)
    {
        long sourceFrames = sourceRate;
        int requestedOutputFrames = CeilingOutputFrames(sourceRate, outputRate, sourceFrames);
        int measuredStartFrame = outputRate / 4;
        int measuredFrameCount = outputRate / 2;
        using var wave = AudioMixerSignalTests.TemporaryFloatWave.Create(
            sourceRate,
            sourceFrames,
            frame => (float)(inputAmplitude * Math.Sin(2d * Math.PI * inputFrequency * frame / sourceRate)));
        BassAudioPlayer player = graph.CreatePlayer(wave.Path);

        try
        {
            player.Play();
            float[] pcm = graph.ReadFrames(requestedOutputFrames);
            Assert.AreEqual(checked(requestedOutputFrames * 2), pcm.Length);
            foreach (float sample in pcm)
            {
                Assert.IsTrue(float.IsFinite(sample), "SRC output PCM must be finite.");
            }

            (double sineAmplitude, double cosineAmplitude) = AudioMixerSignalTests.MeasureProjectedComponents(
                pcm,
                outputRate,
                observedFrequency,
                measuredStartFrame,
                measuredFrameCount);
            double measuredAmplitude = Math.Sqrt(
                sineAmplitude * sineAmplitude + cosineAmplitude * cosineAmplitude);
            double signedLevelDb = 20d * Math.Log10(measuredAmplitude / inputAmplitude);
            Assert.IsTrue(double.IsFinite(sineAmplitude));
            Assert.IsTrue(double.IsFinite(cosineAmplitude));
            Assert.IsTrue(double.IsFinite(measuredAmplitude));
            Assert.IsTrue(double.IsFinite(signedLevelDb));

            TestContext.WriteLine(
                $"srcQuality={graph.SampleRateConversionQuality}, sourceRate={sourceRate}, outputRate={outputRate}, inputFrequency={inputFrequency}, observedFrequency={observedFrequency}, inputAmplitude={inputAmplitude:G9}, sineProjection={sineAmplitude:G9}, cosineProjection={cosineAmplitude:G9}, measuredAmplitude={measuredAmplitude:G9}, signedLevelDb={signedLevelDb:F6}, requestedOutputFrames={requestedOutputFrames}, measuredStartFrame={measuredStartFrame}, measuredFrameCount={measuredFrameCount}, actualNativeMixerThreads={actualNativeMixerThreads}, threadCountSource=Renderer.Channel native readback, readbackIncludedInMeasurement=false, bass={Bass.Version}, bassmix={BassMix.Version}.");
        }
        finally
        {
            graph.DisposePlayer(player);
        }
    }

    private static int CeilingOutputFrames(int sourceRate, int outputRate, long sourceFrames)
    {
        long scaledFrames = checked(sourceFrames * outputRate);
        return checked((int)(scaledFrames / sourceRate
            + (scaledFrames % sourceRate == 0 ? 0 : 1)));
    }

    private static float ReadNativeMixerThreadCount(AudioMixerSignalTests.NativeAudioGraph graph)
    {
        if (!Bass.ChannelGetAttribute(
                graph.Renderer.Channel,
                MixerThreadsAttribute,
                out float actualNativeMixerThreads))
        {
            Errors error = Bass.LastError;
            Assert.Fail("Reading the NullDevice mixer thread count failed: " + error);
        }

        return actualNativeMixerThreads;
    }

    private readonly record struct PerformanceWorkload(string Name, int SourceCount, long FramesPerSource);
}
