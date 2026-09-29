using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Runtime.ExceptionServices;
using System.Text;
using ManagedBass;
using ManagedBass.Mix;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Ribbit.BMS;
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
    public void BmsRealtimeLookAheadWorkload_ReportsMarginTimingVoiceAndSharedPcmMetrics()
    {
        const int pullFrames = 1024;
        SampleRate previousFrequency = BassAudioPlayer.Frequency;
        SampleFormat previousFormat = BassAudioPlayer.Format;
        float previousDefaultVolume = BassAudioPlayer.DefaultVolume;
        float previousDeviceVolume = BassAudioPlayer.DeviceVolume;
        bool previousDeviceMuted = BassAudioPlayer.IsDeviceMuted;
        BassAudioSession? ownedSession = null;
        BMSAutoPlayer? player = null;
        BmsRealtimeAudioScheduler? scheduler = null;
        BmsRealtimeAudioScheduler? tempoScheduler = null;
        int customMixer = 0;
        int originalMixer = 0;
        bool tempoChangeRequested = false;
        ExceptionDispatchInfo? failure = null;

        try
        {
            BassAudioPlayer.Free();
            BassAudioRuntime.Shutdown();
            BassAudioPlayer.Frequency = SampleRate.SAMPLE_RATE_48000Hz;
            BassAudioPlayer.Format = SampleFormat.SAMPLE_FLOAT_32BIT;
            BassAudioWriter.InitializeOwnedSession(out BassAudioSession activeSession);
            ownedSession = activeSession;
            BassAudioPlayer.DefaultVolume = 1f;
            BassAudioPlayer.IsDeviceMuted = false;
            BassAudioPlayer.DeviceVolume = 1f;

            originalMixer = ownedSession.MixerHandle;
            using var directory = new TemporaryDirectory();
            using var shortWave = AudioMixerSignalTests.TemporaryFloatWave.Create(48000, 240, _ => 0.0625f);
            using var longWave = AudioMixerSignalTests.TemporaryFloatWave.Create(44100, 44100L * 2, _ => 0.03125f);
            using var mediumWave = AudioMixerSignalTests.TemporaryFloatWave.Create(48000, 4800, _ => 0.125f);
            using var retriggerWave = AudioMixerSignalTests.TemporaryFloatWave.Create(48000, 48000, _ => 0.015625f);
            File.Copy(shortWave.Path, directory.File("short.wav"));
            File.Copy(longWave.Path, directory.File("long.wav"));
            File.Copy(mediumWave.Path, directory.File("medium.wav"));
            File.Copy(retriggerWave.Path, directory.File("retrigger.wav"));

            string chartPath = directory.File("performance-chart.bms");
            var chart = new StringBuilder(
                "#PLAYER 1\n#TITLE realtime schedule performance\n#BPM 120\n"
                + "#WAV01 short.wav\n#WAV02 long.wav\n#WAV03 medium.wav\n"
                + "#WAV04 retrigger.wav\n#WAV05 short.wav\n#WAV06 long.wav\n");
            string[] channels = ["01", "11", "12", "13", "21", "22"];
            int[][] patterns =
            [
                [1, 2, 3, 4, 5, 6],
                [4],
                [1, 5],
                [2, 6],
                [3, 4],
                [1, 2, 3, 4, 5, 6]
            ];
            const int measureCount = 3;
            const int objectCount = 96;
            for (int measure = 0; measure < measureCount; measure++)
            {
                for (int channelIndex = 0; channelIndex < channels.Length; channelIndex++)
                {
                    chart.Append('#')
                        .Append(measure.ToString("D3"))
                        .Append(channels[channelIndex])
                        .Append(':')
                        .Append(BuildObjectLine(objectCount, patterns[channelIndex]))
                        .Append('\n');
                }
            }
            File.WriteAllText(chartPath, chart.ToString(), Encoding.ASCII);

            using (BassAudioOperationLease operation = BassAudioRuntime.EnterAudioOperation())
            {
                if (ownedSession.CoreDeviceIndex >= 0)
                {
                    Bass.CurrentDevice = ownedSession.CoreDeviceIndex;
                }
                customMixer = BassMix.CreateMixerStream(
                    48000,
                    8,
                    BassFlags.Float | BassFlags.Decode | BassFlags.MixerNonStop);
                if (customMixer == 0)
                {
                    Errors error = Bass.LastError;
                    Assert.Fail("Creating an eight-channel NullDevice decode mixer failed: " + error);
                }
            }

            int realtimeMixerThreadCount = BassMixerThreadConfigurator.RealtimeThreadCount;
            float actualScheduledMixerThreadCount;
            var mixerThreadNative = new BassMixerThreadNativeBoundary();
            using (BassAudioOperationLease operation = BassAudioRuntime.EnterAudioOperation())
            {
                if (ownedSession.CoreDeviceIndex >= 0)
                {
                    Bass.CurrentDevice = ownedSession.CoreDeviceIndex;
                }
                BassMixerThreadConfigurator.SetAndConfirm(
                    customMixer,
                    mixerThreadNative,
                    realtimeMixerThreadCount);
                if (!mixerThreadNative.GetMixerThreadCount(customMixer, out actualScheduledMixerThreadCount))
                {
                    Errors error = mixerThreadNative.GetMixerThreadError();
                    Assert.Fail("Reading the scheduled NullDevice mixer thread count failed: " + error);
                }
            }
            Assert.AreEqual((float)realtimeMixerThreadCount, actualScheduledMixerThreadCount);

            ownedSession.MixerHandle = customMixer;
            player = new BMSAutoPlayer(new BMSFile(chartPath));
            player.LoadResources(asParallel: false);
            Assert.AreEqual(48000, player.AudioSchedule.SampleRate);
            Assert.IsTrue(player.AudioSchedule.Events.Count > 1000);
            Assert.AreSame(player.AudioResourcesByIndex[1]!.Audio, player.AudioResourcesByIndex[5]!.Audio);
            Assert.AreSame(player.AudioResourcesByIndex[2]!.Audio, player.AudioResourcesByIndex[6]!.Audio);

            BassAudioSession session = player.ResourceSession;
            int baselineOwnedHandleCount = session.OwnedStreamCount;
            session.ObserveCallbackPullSize(pullFrames);
            long sharedDecodedPcmBytes = player.AudioResourcesByIndex
                .Where(resource => resource != null)
                .GroupBy(resource => resource!.Path, StringComparer.OrdinalIgnoreCase)
                .Sum(group => group.First()!.Audio.PcmByteCount);
            long allocatedBefore = GC.GetTotalAllocatedBytes(precise: true);
            scheduler = new BmsRealtimeAudioScheduler(
                player.AudioSchedule,
                player.AudioResourcesByIndex,
                session,
                new BassMixerSourceNativeBoundary(),
                player.Duration,
                segmentStartSongFrame: 0,
                playbackRate: 1f,
                generation: 23);
            BmsScheduledAudioMixer scheduledMixer = scheduler.ScheduledMixerDiagnostics;

            var prepareDistribution = new List<long>();
            var commitDistribution = new List<long>();
            var nativeLockDistribution = new List<long>();
            long preparationTotal = scheduledMixer.PreparationTicks;
            long commitTotal = scheduledMixer.CommitTicks;
            long nativeLockTotal = scheduledMixer.NativeLockTicks;
            if (preparationTotal > 0)
            {
                prepareDistribution.Add(preparationTotal);
            }
            if (commitTotal > 0)
            {
                commitDistribution.Add(commitTotal);
            }
            if (nativeLockTotal > 0)
            {
                nativeLockDistribution.Add(nativeLockTotal);
            }

            scheduler.ApplyPlaybackRate(50f, static () => { });
            int scheduledMixerHandle = session.MixerHandle;
            var renderer = new AudioPcmRenderer(scheduledMixerHandle, 48000, 8);
            int[] scheduledInputPullPattern = [31, 509, 97, 1703, 251, 2048];
            float[] pcm = new float[scheduledInputPullPattern.Max() * 8];
            int scheduledInputPullIndex = 0;
            int maximumTrackedHandleCount = baselineOwnedHandleCount;
            long durationSongFrames = AudioFrameMath.TimeToFrame(player.Duration, player.AudioSchedule.SampleRate);
            long renderedFrames = 0;
            long outerReservationTicks = 0;
            while (renderedFrames < durationSongFrames)
            {
                long before = Stopwatch.GetTimestamp();
                scheduler.TickWithoutNullOutputAdvance();
                outerReservationTicks += Stopwatch.GetTimestamp() - before;

                long preparationAfter = scheduledMixer.PreparationTicks;
                long commitAfter = scheduledMixer.CommitTicks;
                long nativeLockAfter = scheduledMixer.NativeLockTicks;
                if (preparationAfter > preparationTotal)
                {
                    prepareDistribution.Add(preparationAfter - preparationTotal);
                }
                if (commitAfter > commitTotal)
                {
                    commitDistribution.Add(commitAfter - commitTotal);
                }
                if (nativeLockAfter > nativeLockTotal)
                {
                    nativeLockDistribution.Add(nativeLockAfter - nativeLockTotal);
                }
                preparationTotal = preparationAfter;
                commitTotal = commitAfter;
                nativeLockTotal = nativeLockAfter;
                maximumTrackedHandleCount = Math.Max(maximumTrackedHandleCount, session.OwnedStreamCount);

                int requestedInputFrames = (int)Math.Min(
                    scheduledInputPullPattern[scheduledInputPullIndex++ % scheduledInputPullPattern.Length],
                    durationSongFrames - renderedFrames);
                AudioPcmReadResult read = renderer.ReadFrames(pcm, requestedInputFrames);
                renderedFrames += read.FramesRead;
                Assert.AreEqual(requestedInputFrames, read.FramesRead);
            }

            scheduler.TickWithoutNullOutputAdvance();

            long minimumMarginFrames = scheduledMixer.MinimumReservationMarginFrames;
            int maximumReservedVoices = scheduledMixer.MaximumReservedVoiceCount;
            int maximumActiveVoices = scheduledMixer.MaximumActiveVoiceCount;
            long preparedVoices = scheduledMixer.PreparedVoiceCount;
            int retiredVoices = scheduledMixer.RetiredVoiceCount;
            int preparationFailures = scheduledMixer.PreparationFailureCount;
            int commitFailures = scheduledMixer.CommitFailureCount;
            long maximumCommitTicks = scheduledMixer.MaxCommitTicks;
            long maximumNativeLockTicks = scheduledMixer.MaximumNativeLockTicks;
            long maximumTickIntervalTicks = scheduler.MaximumTickIntervalTicks;
            long maximumReservationTicks = scheduler.MaximumReservationTicks;
            int afterPlaybackHandleCount = session.OwnedStreamCount;

            Assert.IsTrue(minimumMarginFrames >= 0);
            Assert.IsTrue(preparedVoices > 1000);
            Assert.AreEqual(0, preparationFailures);
            Assert.AreEqual(0, commitFailures);
            Assert.IsFalse(session.HasCallbackOutputFailure);
            scheduler.Dispose();
            scheduler = null;
            long managedAllocatedBytes = GC.GetTotalAllocatedBytes(precise: true) - allocatedBefore;

            ownedSession.MixerHandle = originalMixer;
            tempoChangeRequested = true;
            BassAudioPlayer.SetBmsTempoChange(50f);
            int tempoOutputHandle = BassAudioPlayer.OutputMixerHandle;
            Assert.AreNotEqual(0, tempoOutputHandle);
            tempoScheduler = new BmsRealtimeAudioScheduler(
                player.AudioSchedule,
                player.AudioResourcesByIndex,
                session,
                new BassMixerSourceNativeBoundary(),
                player.Duration,
                segmentStartSongFrame: 0,
                playbackRate: 50f,
                generation: 24);
            var tempoRenderer = new AudioPcmRenderer(tempoOutputHandle, 48000, 2);
            float[] tempoPcm = new float[pullFrames * 2];
            long tempoRenderedFrames = 0;
            long tempoInputFramesConsumed = 0;
            long tempoInputReadAheadFrames = Math.Max(
                4096,
                checked((long)Math.Ceiling(33d * 50d * 48000d / 1000d)));
            long tempoFramesForDuration = checked((long)Math.Ceiling(durationSongFrames / 50d));
            long tempoAllocatedBefore = GC.GetTotalAllocatedBytes(precise: true);
            while (tempoRenderedFrames < tempoFramesForDuration)
            {
                tempoScheduler.TickWithoutNullOutputAdvance();
                maximumTrackedHandleCount = Math.Max(maximumTrackedHandleCount, session.OwnedStreamCount);

                long mixerPositionBefore = GetMixerFrame(session, 2);
                int requestedTempoFrames = (int)Math.Min(pullFrames, tempoFramesForDuration - tempoRenderedFrames);
                AudioPcmReadResult read = tempoRenderer.ReadFrames(tempoPcm, requestedTempoFrames);
                long mixerPositionAfter = GetMixerFrame(session, 2);
                tempoInputFramesConsumed += mixerPositionAfter - mixerPositionBefore;
                tempoRenderedFrames += read.FramesRead;
                Assert.AreEqual(requestedTempoFrames, read.FramesRead);
            }
            tempoScheduler.TickWithoutNullOutputAdvance();
            long tempoManagedAllocatedBytes = GC.GetTotalAllocatedBytes(precise: true) - tempoAllocatedBefore;
            tempoScheduler.Dispose();
            tempoScheduler = null;
            BassAudioPlayer.ResetTempoChange();
            tempoChangeRequested = false;
            int afterCleanupHandleCount = session.OwnedStreamCount;
            Assert.AreEqual(baselineOwnedHandleCount, afterCleanupHandleCount);

            TestContext.WriteLine(
                $"condition: workload=high-density-short-long-retrigger-alias, events={player.AudioSchedule.Events.Count}, measures={measureCount}, objectSlotsPerChannel={objectCount}, sourceRates=44100,48000, outputRate=48000, scheduledSongFrames={durationSongFrames}, scheduledInputChannels=8, actualScheduledMixerThreads={actualScheduledMixerThreadCount}, requestedRealtimeMixerThreads={realtimeMixerThreadCount}, actualTempoOutputChannels=2, playbackRateForLookAhead=50, tempoPhase=production-stereo-scheduler, tempoOutputRendered=true, tempoOutputHandle={tempoOutputHandle}, tempoFramesForDuration={tempoFramesForDuration}, tempoInputReadAheadFrames={tempoInputReadAheadFrames}, tempoRenderedFrames={tempoRenderedFrames}, tempoInputFramesConsumed={tempoInputFramesConsumed}, tempoManagedAllocatedBytes={tempoManagedAllocatedBytes}, callbackPullFrames={pullFrames}, scheduledInputPullPattern={string.Join('+', scheduledInputPullPattern)}, sharedDecodedPcmBytes={sharedDecodedPcmBytes}, pcmCopiedPerVoiceBytes=0, FloatWaveSourceBackingPayloadPerMonoVoiceBytes={80 + sizeof(int)}, managedAllocatedBytes={managedAllocatedBytes}, managedAllocatedBytesPerPreparedVoice={(double)managedAllocatedBytes / preparedVoices:F2}, minimumReservationMarginFrames={minimumMarginFrames}, preparationCount={scheduledMixer.PreparationOperationCount}, preparationTicks={scheduledMixer.PreparationTicks}, preparationDistribution={DescribeDistribution(prepareDistribution)}, commitCount={scheduledMixer.CommitOperationCount}, commitTicks={scheduledMixer.CommitTicks}, commitDistribution={DescribeDistribution(commitDistribution)}, maximumCommitTicks={maximumCommitTicks}, nativeLockTicks={scheduledMixer.NativeLockTicks}, nativeLockDistribution={DescribeDistribution(nativeLockDistribution)}, maximumNativeLockTicks={maximumNativeLockTicks}, maximumReservationTickTicks={maximumReservationTicks}, maximumControlIntervalTicks={maximumTickIntervalTicks}, preparedVoices={preparedVoices}, retiredVoices={retiredVoices}, maximumReservedVoices={maximumReservedVoices}, maximumActiveVoices={maximumActiveVoices}, sessionTrackedHandlesBaseline={baselineOwnedHandleCount}, sessionTrackedHandlesPeak={maximumTrackedHandleCount}, sessionTrackedHandlesAfterPlayback={afterPlaybackHandleCount}, sessionTrackedHandlesAfterCleanup={afterCleanupHandleCount}, renderedFrames={renderedFrames}, preparationFailures={preparationFailures}, commitFailures={commitFailures}, callbackFailure={session.HasCallbackOutputFailure}, underruns=0 (reservation failures abort this measurement), timingSource=Stopwatch ticks, noFixedPerformanceThreshold=true.");
        }
        catch (Exception exception)
        {
            failure = ExceptionDispatchInfo.Capture(exception);
        }
        finally
        {
            if (tempoScheduler != null)
            {
                CaptureCleanup(ref failure, tempoScheduler.Dispose);
            }
            if (scheduler != null)
            {
                CaptureCleanup(ref failure, scheduler.Dispose);
            }
            if (tempoChangeRequested)
            {
                CaptureCleanup(ref failure, BassAudioPlayer.ResetTempoChange);
            }
            if (player != null)
            {
                CaptureCleanup(ref failure, player.DisposeAudioSourcesAfterUse);
            }
            if (ownedSession != null)
            {
                ownedSession.MixerHandle = originalMixer;
                if (customMixer != 0)
                {
                    CaptureCleanup(ref failure, () =>
                    {
                        using BassAudioOperationLease operation = BassAudioRuntime.EnterAudioOperation();
                        if (ownedSession.CoreDeviceIndex >= 0)
                        {
                            Bass.CurrentDevice = ownedSession.CoreDeviceIndex;
                        }
                        if (!Bass.StreamFree(customMixer))
                        {
                            Errors error = Bass.LastError;
                            throw new InvalidOperationException("Releasing the benchmark mixer failed: " + error);
                        }
                    });
                }
            }
            CaptureCleanup(ref failure, () =>
            {
                if (!BassAudioWriter.TryReleaseEncoder())
                {
                    throw new InvalidOperationException("The benchmark encoder did not release.");
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
                    throw new InvalidOperationException("The benchmark audio session did not release.");
                }
            });
            CaptureCleanup(ref failure, BassAudioRuntime.Shutdown);
            CaptureCleanup(ref failure, () => BassAudioPlayer.IsDeviceMuted = previousDeviceMuted);
            CaptureCleanup(ref failure, () => BassAudioPlayer.DeviceVolume = previousDeviceVolume);
            CaptureCleanup(ref failure, () => BassAudioPlayer.DefaultVolume = previousDefaultVolume);
            CaptureCleanup(ref failure, () => BassAudioPlayer.Frequency = previousFrequency);
            CaptureCleanup(ref failure, () => BassAudioPlayer.Format = previousFormat);
        }

        failure?.Throw();
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

    private static long GetMixerFrame(BassAudioSession session, int channelCount)
    {
        using BassAudioOperationLease operation = BassAudioRuntime.EnterAudioOperation();
        if (session.CoreDeviceIndex >= 0)
        {
            Bass.CurrentDevice = session.CoreDeviceIndex;
        }
        long positionBytes = Bass.ChannelGetPosition(session.MixerHandle, PositionFlags.Bytes);
        if (positionBytes < 0)
        {
            Errors error = Bass.LastError;
            throw new InvalidOperationException("Reading the BMS input mixer position failed: " + error);
        }
        int bytesPerFrame = checked(channelCount * sizeof(float));
        if (positionBytes % bytesPerFrame != 0)
        {
            throw new InvalidOperationException("The BMS input mixer position was not frame aligned.");
        }
        return positionBytes / bytesPerFrame;
    }

    private static string BuildObjectLine(int objectCount, IReadOnlyList<int> pattern)
    {
        var value = new StringBuilder(checked(objectCount * 2));
        for (int objectIndex = 0; objectIndex < objectCount; objectIndex++)
        {
            value.Append(pattern[objectIndex % pattern.Count].ToString("D2"));
        }
        return value.ToString();
    }

    private static string DescribeDistribution(IReadOnlyCollection<long> stopwatchTicks)
    {
        if (stopwatchTicks.Count == 0)
        {
            return "count=0";
        }

        long[] ordered = stopwatchTicks.Order().ToArray();
        int medianIndex = (ordered.Length - 1) / 2;
        int percentile95Index = Math.Max(0, checked((int)Math.Ceiling(ordered.Length * 0.95d) - 1));
        return "count=" + ordered.Length
            + ",minMs=" + TimeSpan.FromSeconds((double)ordered[0] / Stopwatch.Frequency).TotalMilliseconds.ToString("F3")
            + ",p50Ms=" + TimeSpan.FromSeconds((double)ordered[medianIndex] / Stopwatch.Frequency).TotalMilliseconds.ToString("F3")
            + ",p95Ms=" + TimeSpan.FromSeconds((double)ordered[percentile95Index] / Stopwatch.Frequency).TotalMilliseconds.ToString("F3")
            + ",maxMs=" + TimeSpan.FromSeconds((double)ordered[^1] / Stopwatch.Frequency).TotalMilliseconds.ToString("F3");
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
            "BeMusicSeeker.AudioMixerPerformance." + Guid.NewGuid().ToString("N"));

        internal TemporaryDirectory() => Directory.CreateDirectory(path);

        internal string File(string name) => Path.Combine(path, name);

        public void Dispose() => Directory.Delete(path, recursive: true);
    }

    private readonly record struct PerformanceWorkload(string Name, int SourceCount, long FramesPerSource);
}
