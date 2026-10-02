using System;
using System.Collections.Concurrent;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Runtime.ExceptionServices;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using BeMusicSeeker.Tests.Helpers;
using AudioDriver = BeMusicSeeker.Models.AudioDriver;
using AudioPlaybackInitializationResult = BeMusicSeeker.Models.AudioPlaybackInitializationResult;
using IAudioPlaybackRuntime = BeMusicSeeker.Models.IAudioPlaybackRuntime;
using IPlayerSettingsGateway = BeMusicSeeker.Models.IPlayerSettingsGateway;
using InternalBMSAutoPlayerSoundOnly = BeMusicSeeker.Models.InternalBMSAutoPlayerSoundOnly;
using PlayerResolution = BeMusicSeeker.Models.PlayerResolution;
using PlaybackStartOperation = BeMusicSeeker.Models.PlaybackStartOperation;
using PlayerSettingsSnapshot = BeMusicSeeker.Models.PlayerSettingsSnapshot;
using WindowPlacement = BeMusicSeeker.Models.Utils.WindowPlacement;
using ManagedBass;
using ManagedBass.Enc;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Ribbit.BMS;
using Ribbit.Media;
using Ribbit.Media.Audio;

namespace BeMusicSeeker.Tests;

[TestClass]
[DoNotParallelize]
public sealed class BmsRealtimeAudioSchedulerTests
{
    public TestContext TestContext { get; set; } = null!;

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
        BassAudioPlayer.DefaultVolume = 1f;
        BassAudioPlayer.IsDeviceMuted = false;
        BassAudioPlayer.DeviceVolume = 1f;
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
                throw new InvalidOperationException("The scheduled audio session did not release.");
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
    public void RealtimeMixer_PreservesWriterFramesAcrossDifferentPullDeadlines()
    {
        using var directory = new TemporaryDirectory();
        float[] sourceSamples = BuildSourceSamples();
        File.WriteAllBytes(directory.File("audio.wav"), BuildFloatWave(48000, sourceSamples));
        string chartPath = directory.File("chart.bms");
        WriteChart(chartPath,
            "#WAV01 audio.wav\n"
            + "#00001:01\n"
            + "#00101:01\n");
        string outputPath = directory.File("writer-output");

        using var writer = new BMSAutoPlayWriter(new BMSFile(chartPath));
        writer.LoadResources();
        writer.Write(EncoderType.WAVE, 0.4f, outputPath, BMSAutoPlayWriter.Normalization.NONE);
        AudioTestWaveFile writerWave = AudioTestWaveFileReader.Read(File.ReadAllBytes(outputPath + ".wav"));
        Assert.AreEqual((ushort)3, writerWave.Format);
        Assert.AreEqual((ushort)2, writerWave.Channels);
        Assert.AreEqual(48000, writerWave.SampleRate);

        BassAudioPlayer.SetBmsTempoChange(1f);
        BassAudioSession session = writer.ResourceSession;
        session.SetCallbackOutputPaused(true);
        session.ObserveCallbackPullSize(2048);
        var mixer = new AudioPcmRenderer(session.MixerHandle, 48000, 2);
        int totalFrames = writerWave.DataLength / (writerWave.Channels * sizeof(float));
        float[] realtimeSamples = new float[checked(totalFrames * writerWave.Channels)];
        int channelCount = Bass.ChannelGetInfo(session.MixerHandle).Channels;
        long mixerFrameAtStart = GetMixerFrame(session, channelCount);

        using (var scheduler = new BmsRealtimeAudioScheduler(
                   writer.AudioSchedule,
                   writer.AudioResourcesByIndex,
                   session,
                   new BassMixerSourceNativeBoundary(),
                   writer.Duration,
                   segmentStartSongFrame: 0,
                   playbackRate: 1f,
                   generation: 7))
        {
            Assert.AreEqual(mixerFrameAtStart, scheduler.OriginMixerFrame,
                "開始時のsong frame 0は、停止中のinput mixer位置へ対応します。");

            int[] pullPattern = [127, 1009, 73, 2048, 399, 1531];
            int patternIndex = 0;

            long renderedFrames = 0;
            while (renderedFrames < totalFrames)
            {
                scheduler.TickWithoutNullOutputAdvance();
                int frames = checked((int)Math.Min(
                    pullPattern[patternIndex++ % pullPattern.Length],
                    totalFrames - renderedFrames));
                AudioPcmReadResult result = mixer.ReadFrames(
                    realtimeSamples.AsSpan(checked((int)renderedFrames * channelCount), checked(frames * channelCount)),
                    frames);
                Assert.AreEqual(frames, result.FramesRead);
                Assert.IsFalse(result.ReachedEnd);
                renderedFrames += frames;
            }
            scheduler.TickWithoutNullOutputAdvance();
        }

        const int secondEventFrame = 96000;
        int sourceFrames = sourceSamples.Length / 2;
        for (int frame = 0; frame < totalFrames; frame++)
        {
            for (int channel = 0; channel < writerWave.Channels; channel++)
            {
                float actual = realtimeSamples[frame * writerWave.Channels + channel];
                float writerValue = ReadFloatSample(writerWave, frame, channel);
                Assert.AreEqual(writerValue, (float)(actual * 0.16d), 1e-8f,
                    $"Realtime and NONE-normalized Writer differ at frame {frame}, channel {channel}.");

                float source = frame < sourceFrames
                    ? sourceSamples[frame * 2 + channel]
                    : frame >= secondEventFrame && frame < secondEventFrame + sourceFrames
                        ? sourceSamples[(frame - secondEventFrame) * 2 + channel]
                        : 0f;
                Assert.AreEqual(source, actual, 0f, $"Independent frame expectation failed at frame {frame}, channel {channel}.");
            }
        }

        Assert.AreEqual(0f, realtimeSamples[(secondEventFrame - 1) * 2]);
        Assert.AreEqual(sourceSamples[0], realtimeSamples[secondEventFrame * 2]);
        writer.DisposeAudioSourcesAfterUse();
    }

    [TestMethod]
    public void TempoOutputPullLookAhead_IsMeasuredAtSupportedRatesAndObservedBlockSizes()
    {
        const int callbackFrames = 4096;
        const int sampleRate = 48000;
        const int tempoBpm = 480;
        const int eventSubdivisions = 32;
        const int bytesPerFrame = 2 * sizeof(float);
        long framesPerMeasure = sampleRate * 60L * 4 / tempoBpm;
        long[] expectedEventSongFrames = Enumerable.Range(0, eventSubdivisions)
            .Select(subdivision => framesPerMeasure * subdivision / eventSubdivisions)
            .ToArray();
        using var directory = new TemporaryDirectory();
        using var source = AudioMixerSignalTests.TemporaryFloatWave.Create(
            sampleRate,
            sampleRate * 12L,
            _ => 0.125f);
        File.Copy(source.Path, directory.File("long.wav"));
        string chartPath = directory.File("tempo-lookahead.bms");
        File.WriteAllText(
            chartPath,
            "#PLAYER 1\n#TITLE tempo look-ahead\n#ARTIST test\n#BPM 480\n"
                + "#WAV01 long.wav\n#00011:"
                + string.Concat(Enumerable.Repeat("01", eventSubdivisions))
                + "\n",
            Encoding.ASCII);

        var player = new BMSAutoPlayer(new BMSFile(chartPath));
        player.LoadResources();
        BassAudioSession session = player.ResourceSession;
        session.SetCallbackOutputPaused(true);
        session.ObserveCallbackPullSize(callbackFrames);
        try
        {
            int[][] pullScenarios =
            [
                [128, callbackFrames],
                [callbackFrames]
            ];
            int generation = 1;
            foreach (float rate in new[] { 0.05f, 1f, 2f, 50f })
            {
                foreach (int[] pulls in pullScenarios)
                {
                    BassAudioPlayer.SetBmsTempoChange(rate);
                    long mixerFrameAtStart = GetMixerFrame(session, 2);
                    var native = new RecordingScheduledNativeBoundary();
                    using (var scheduler = new BmsRealtimeAudioScheduler(
                               player.AudioSchedule,
                               player.AudioResourcesByIndex,
                               session,
                               native,
                               player.Duration,
                               segmentStartSongFrame: 0,
                               playbackRate: rate,
                               generation: generation++))
                    {
                        Assert.AreEqual(mixerFrameAtStart, scheduler.OriginMixerFrame,
                            "予約余裕をsong frame 0の原点へ加えません。");
                        int preReservedVoiceCount = scheduler.ScheduledMixerDiagnostics.ReservedVoiceCount;
                        long maximumExtraInputFrames = 0;
                        long maximumInputFramesConsumed = 0;
                        var renderer = new AudioPcmRenderer(BassAudioPlayer.OutputMixerHandle, 48000, 2);
                        float[] pcm = new float[callbackFrames * 2];

                        ScheduledVoiceReservation[] reservationsBeforeFirstPull = native.SnapshotReservations();
                        long tempoInputReadAheadFrames = Math.Max(
                            4096,
                            checked((long)Math.Ceiling(33d * rate * sampleRate / 1000d)));
                        long requiredReservationFrames = checked(
                            (long)Math.Ceiling(callbackFrames * (double)rate) + tempoInputReadAheadFrames);
                        long[] requiredEventSongFrames = expectedEventSongFrames
                            .Where(eventFrame => eventFrame < requiredReservationFrames)
                            .ToArray();
                        Assert.IsTrue(requiredEventSongFrames.Length > 0);
                        foreach (long songFrame in requiredEventSongFrames)
                        {
                            long expectedStartBytes = checked(
                                (scheduler.OriginMixerFrame + songFrame) * bytesPerFrame);
                            Assert.IsTrue(
                                reservationsBeforeFirstPull.Any(reservation =>
                                    reservation.StartBytes == expectedStartBytes && reservation.Resumed),
                                $"Before the first pull, the event at absolute input byte {expectedStartBytes} "
                                    + $"must be successfully reserved and resumed (rate={rate:G}); observed "
                                    + string.Join(",", reservationsBeforeFirstPull.Select(reservation =>
                                        $"{reservation.StartBytes}:{reservation.Resumed}")));
                        }

                        for (int pullIndex = 0; pullIndex < pulls.Length; pullIndex++)
                        {
                            if (pullIndex != 0)
                            {
                                scheduler.TickWithoutNullOutputAdvance();
                            }

                            int requestedFrames = pulls[pullIndex];
                            long mixerFrameBeforePull = GetMixerFrame(session, 2);
                            AudioPcmReadResult read = renderer.ReadFrames(
                                pcm.AsSpan(0, checked(requestedFrames * 2)),
                                requestedFrames);
                            long mixerFrameAfterPull = GetMixerFrame(session, 2);
                            Assert.AreEqual(requestedFrames, read.FramesRead);
                            Assert.IsFalse(read.ReachedEnd);

                            long inputFramesConsumed = mixerFrameAfterPull - mixerFrameBeforePull;
                            if (pullIndex == 0)
                            {
                                foreach (long songFrame in expectedEventSongFrames)
                                {
                                    long eventMixerFrame = checked(scheduler.OriginMixerFrame + songFrame);
                                    if (eventMixerFrame >= mixerFrameBeforePull && eventMixerFrame < mixerFrameAfterPull)
                                    {
                                        long expectedStartBytes = checked(eventMixerFrame * bytesPerFrame);
                                        Assert.IsTrue(
                                            reservationsBeforeFirstPull.Any(reservation =>
                                                reservation.StartBytes == expectedStartBytes && reservation.Resumed),
                                            $"The event consumed by the first input pull at absolute byte "
                                                + $"{expectedStartBytes} was not reserved and resumed before the pull "
                                                + $"(rate={rate:G}, inputFrames={mixerFrameBeforePull}..{mixerFrameAfterPull}).");
                                    }
                                }
                            }
                            long expectedRateScaledFrames = checked((long)Math.Ceiling(requestedFrames * (double)rate));
                            long extraInputFrames = Math.Max(0, inputFramesConsumed - expectedRateScaledFrames);
                            maximumInputFramesConsumed = Math.Max(maximumInputFramesConsumed, inputFramesConsumed);
                            maximumExtraInputFrames = Math.Max(maximumExtraInputFrames, extraInputFrames);
                            TestContext.WriteLine(
                                $"tempoPull: rate={rate:G}, pullPattern={string.Join('+', pulls)}, requestedOutputFrames={requestedFrames}, inputFramesConsumed={inputFramesConsumed}, rateScaledFrames={expectedRateScaledFrames}, extraInputFrames={extraInputFrames}");
                        }

                        long configuredTempoLookAheadFrames = Math.Max(
                            4096,
                            checked((long)Math.Ceiling(33d * rate * sampleRate / 1000d)));
                        Assert.IsTrue(
                            maximumExtraInputFrames <= configuredTempoLookAheadFrames,
                            $"Measured tempo input look-ahead {maximumExtraInputFrames} frames exceeded the configured "
                            + $"allowance {configuredTempoLookAheadFrames} at rate {rate:G}.");
                        TestContext.WriteLine(
                            $"tempoRateSummary: rate={rate:G}, pullPattern={string.Join('+', pulls)}, preReservedVoiceCount={preReservedVoiceCount}, callbackFrames={callbackFrames}, "
                            + $"requiredReservationFrames={requiredReservationFrames}, requiredEventCount={requiredEventSongFrames.Length}, "
                            + $"maximumInputFramesConsumed={maximumInputFramesConsumed}, maximumExtraInputFrames={maximumExtraInputFrames}, tempoInputReadAheadFrames={configuredTempoLookAheadFrames}");
                    }
                    BassAudioPlayer.ResetTempoChange();
                }
            }
        }
        finally
        {
            BassAudioPlayer.ResetTempoChange();
            player.DisposeAudioSourcesAfterUse();
            session.SetCallbackOutputPaused(false);
        }
    }

    [DataTestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public async Task PhysicalSessionWithoutCallback_RejectsSchedulerConstructionAndPlaybackStart(bool startPlayer)
    {
        using var directory = new TemporaryDirectory();
        string path = directory.File("unobserved.bms");
        WriteChart(path, "#00111:00\n");
        using var player = new BMSAutoPlayer(new BMSFile(path));
        player.LoadResources();
        BassAudioSession session = player.ResourceSession;
        Assert.AreEqual(0, session.MaximumCallbackFrames);
        BassAudioPlayer.DeviceDriver originalBackend = session.ActualBackend;
        try
        {
            session.ActualBackend = BassAudioPlayer.DeviceDriver.WASAPI_SHARED;
            if (startPlayer)
            {
                await Assert.ThrowsExceptionAsync<InvalidOperationException>(player.Start);
                Assert.IsNull(player.RealtimeScheduler);
            }
            else
            {
                Assert.ThrowsException<InvalidOperationException>(() => new BmsRealtimeAudioScheduler(
                    player.AudioSchedule, player.AudioResourcesByIndex, session, new BassMixerSourceNativeBoundary(),
                    player.Duration, 0, 1f, 0));
            }
            Assert.AreEqual(-1, session.RealtimeReservedCallbackFrames);
        }
        finally { session.ActualBackend = originalBackend; }
    }

    [TestMethod]
    public void CallbackBlockGrowthBeyondPreparedLeadFailsBeforeTempoPull()
    {
        const int reservedCallbackFrames = 128;
        const int grownCallbackFrames = 4096;
        using var directory = new TemporaryDirectory();
        using var source = AudioMixerSignalTests.TemporaryFloatWave.Create(
            48000,
            48000L * 4,
            _ => 0.125f);
        File.Copy(source.Path, directory.File("growth.wav"));
        string chartPath = directory.File("callback-growth.bms");
        WriteChart(chartPath, "#WAV01 growth.wav\n#00011:01\n");

        BMSAutoPlayer? player = null;
        BmsRealtimeAudioScheduler? scheduler = null;
        BassAudioSession? session = null;
        int previousCallbackOutputHandle = 0;
        AudioPcmRenderer? previousCallbackPcmRenderer = null;
        AudioOutputProcessor? previousOutputProcessor = null;
        IntPtr callbackBuffer = IntPtr.Zero;
        ExceptionDispatchInfo? cleanupFailure = null;
        try
        {
            player = new BMSAutoPlayer(new BMSFile(chartPath));
            player.LoadResources();
            BassAudioSession activeSession = player.ResourceSession;
            session = activeSession;
            previousCallbackOutputHandle = activeSession.CallbackOutputHandle;
            previousCallbackPcmRenderer = activeSession.CallbackPcmRenderer;
            previousOutputProcessor = activeSession.OutputProcessor;
            activeSession.SetCallbackOutputPaused(true);
            BassAudioPlayer.SetBmsTempoChange(1f);
            activeSession.PublishCallbackOutputHandle(BassAudioPlayer.OutputMixerHandle);
            activeSession.CallbackPcmRenderer = new AudioPcmRenderer(BassAudioPlayer.OutputMixerHandle, 48000, 2);
            activeSession.OutputProcessor = new AudioOutputProcessor(48000, 1d);
            int initialPullBytes = checked(reservedCallbackFrames * 2 * sizeof(float));
            callbackBuffer = Marshal.AllocHGlobal(checked(grownCallbackFrames * 2 * sizeof(float)));
            Assert.AreEqual(
                initialPullBytes,
                BassAudioPlayer.ReadPublishedCallbackOutput(activeSession, callbackBuffer, initialPullBytes));
            Assert.AreEqual(reservedCallbackFrames, activeSession.MaximumCallbackFrames);
            Assert.AreEqual(-1, activeSession.RealtimeReservedCallbackFrames);

            scheduler = new BmsRealtimeAudioScheduler(
                player.AudioSchedule,
                player.AudioResourcesByIndex,
                activeSession,
                new BassMixerSourceNativeBoundary(),
                player.Duration,
                segmentStartSongFrame: 0,
                playbackRate: 1f,
                generation: 41);
            Assert.AreEqual(reservedCallbackFrames, activeSession.RealtimeReservedCallbackFrames);

            activeSession.SetCallbackOutputPaused(false);
            Assert.AreEqual(
                initialPullBytes,
                BassAudioPlayer.ReadPublishedCallbackOutput(activeSession, callbackBuffer, initialPullBytes));
            Assert.IsFalse(activeSession.HasCallbackOutputFailure);

            long mixerFrameBeforeGrowthPull = GetMixerFrame(activeSession, 2);
            int grownPullBytes = checked(grownCallbackFrames * 2 * sizeof(float));
            byte[] sentinel = Enumerable.Repeat((byte)0xA5, grownPullBytes).ToArray();
            Marshal.Copy(sentinel, 0, callbackBuffer, sentinel.Length);
            Assert.AreEqual(
                grownPullBytes,
                BassAudioPlayer.ReadPublishedCallbackOutput(activeSession, callbackBuffer, grownPullBytes));
            long mixerFrameAfterGrowthPull = GetMixerFrame(activeSession, 2);
            byte[] returned = new byte[grownPullBytes];
            Marshal.Copy(callbackBuffer, returned, 0, returned.Length);

            Assert.AreEqual(mixerFrameBeforeGrowthPull, mixerFrameAfterGrowthPull);
            Assert.IsTrue(returned.All(value => value == 0));
            Assert.IsTrue(activeSession.HasCallbackOutputFailure);
            Assert.AreEqual(grownCallbackFrames, activeSession.MaximumCallbackFrames);
            Assert.AreEqual(reservedCallbackFrames, activeSession.RealtimeReservedCallbackFrames);
        }
        finally
        {
            if (player != null && session is BassAudioSession activeSession)
            {
                CaptureCleanup(ref cleanupFailure, () => activeSession.SetCallbackOutputPaused(true));
                if (scheduler != null)
                {
                    CaptureCleanup(ref cleanupFailure, scheduler.Dispose);
                }
                CaptureCleanup(ref cleanupFailure, BassAudioPlayer.ResetTempoChange);
                CaptureCleanup(ref cleanupFailure, () => activeSession.PublishCallbackOutputHandle(previousCallbackOutputHandle));
                CaptureCleanup(ref cleanupFailure, () => activeSession.CallbackPcmRenderer = previousCallbackPcmRenderer);
                CaptureCleanup(ref cleanupFailure, () => activeSession.OutputProcessor = previousOutputProcessor);
                CaptureCleanup(ref cleanupFailure, player.DisposeAudioSourcesAfterUse);
                CaptureCleanup(ref cleanupFailure, () => activeSession.SetCallbackOutputPaused(false));
            }
            if (callbackBuffer != IntPtr.Zero)
            {
                CaptureCleanup(ref cleanupFailure, () => Marshal.FreeHGlobal(callbackBuffer));
            }
        }
        cleanupFailure?.Throw();
    }

    [TestMethod]
    public void ParsedTempoStopAndMeasureChangesMatchIndependentWriterAndRealtimeFrames()
    {
        using var directory = new TemporaryDirectory();
        const int sourceFrameCount = 144000;
        float[] sourceSamples = BuildTimingPatternSamples(sourceFrameCount);
        File.WriteAllBytes(directory.File("audio.wav"), BuildFloatWave(48000, sourceSamples));
        string chartPath = directory.File("timing-chart.bms");
        WriteChart(chartPath,
            "#BPM01 240\n"
            + "#STOP01 192\n"
            + "#WAV01 audio.wav\n"
            + "#WAV02 audio.wav\n"
            + "#00001:01\n"
            + "#00011:01\n"
            + "#00012:0202\n"
            + "#00008:0001\n"
            + "#00009:00000001\n"
            + "#00102:0.5\n"
            + "#00111:0100\n"
            + "#00112:0002\n");
        string outputPath = directory.File("writer-output");

        using var writer = new BMSAutoPlayWriter(new BMSFile(chartPath));
        writer.LoadResources();

        // 120 BPMで小節前半が1秒、EXBPM後の四分小節と192刻みSTOPが各0.25秒・1秒、
        // 残り四分小節が0.25秒。次小節は長さ0.5なので0.5秒、その中点は2.75秒です。
        Assert.AreEqual(TimeSpan.FromSeconds(1), writer.Bms.Measures[0].ExBpm.Single().AbsoluteTime);
        Assert.AreEqual(TimeSpan.FromMilliseconds(1250), writer.Bms.Measures[0].Stop.Single().AbsoluteTime);
        Assert.AreEqual(TimeSpan.FromSeconds(3), writer.Bms.Duration);
        long[][] independentExpectedEvents =
        [
            [1, 0],
            [2, 0],
            [2, 48000],
            [1, 120000],
            [2, 132000]
        ];
        long[][] scheduledEvents = writer.AudioSchedule.Events
            .Select(audioEvent => new[] { (long)audioEvent.WavIndex, audioEvent.StartFrame })
            .ToArray();
        CollectionAssert.AreEqual(independentExpectedEvents, scheduledEvents);

        writer.Write(
            EncoderType.WAVE,
            quality: 0.4f,
            outputPath,
            BMSAutoPlayWriter.Normalization.NONE);
        AudioTestWaveFile writerWave = AudioTestWaveFileReader.Read(File.ReadAllBytes(outputPath + ".wav"));
        int totalFrames = writerWave.DataLength / (writerWave.Channels * sizeof(float));
        Assert.AreEqual(276000, totalFrames);

        BassAudioPlayer.SetBmsTempoChange(1f);
        BassAudioSession session = writer.ResourceSession;
        session.SetCallbackOutputPaused(true);
        session.ObserveCallbackPullSize(2048);
        var renderer = new AudioPcmRenderer(session.MixerHandle, 48000, 2);
        float[] realtimeSamples = new float[checked(totalFrames * 2)];
        long mixerFrameAtStart = GetMixerFrame(session, channelCount: 2);
        using (var scheduler = new BmsRealtimeAudioScheduler(
                   writer.AudioSchedule,
                   writer.AudioResourcesByIndex,
                   session,
                   new BassMixerSourceNativeBoundary(),
                   writer.Duration,
                   segmentStartSongFrame: 0,
                   playbackRate: 1f,
                   generation: 11))
        {
            Assert.AreEqual(mixerFrameAtStart, scheduler.OriginMixerFrame,
                "開始時のsong frame 0は、停止中のinput mixer位置へ対応します。");
            int[] pullPattern = [31, 509, 97, 1703, 251, 2048];
            int patternIndex = 0;

            long renderedFrames = 0;
            while (renderedFrames < totalFrames)
            {
                scheduler.TickWithoutNullOutputAdvance();
                int frames = checked((int)Math.Min(
                    pullPattern[patternIndex++ % pullPattern.Length],
                    totalFrames - renderedFrames));
                AudioPcmReadResult result = renderer.ReadFrames(
                    realtimeSamples.AsSpan(checked((int)renderedFrames * 2), frames * 2),
                    frames);
                Assert.AreEqual(frames, result.FramesRead);
                Assert.IsFalse(result.ReachedEnd);
                renderedFrames += frames;
            }
            scheduler.TickWithoutNullOutputAdvance();
        }

        for (int frame = 0; frame < totalFrames; frame++)
        {
            for (int channel = 0; channel < 2; channel++)
            {
                float expected = GetIndependentTimingSample(sourceSamples, frame, channel);
                float actual = realtimeSamples[frame * 2 + channel];
                float fromWriter = ReadFloatSample(writerWave, frame, channel) / 0.16f;
                Assert.AreEqual(expected, actual, 1e-7f,
                    $"Independent parsed-event expectation failed at frame {frame}, channel {channel}.");
                Assert.AreEqual(expected, fromWriter, 1e-6f,
                    $"Writer and realtime differ at frame {frame}, channel {channel}.");
            }
        }

        BmsAudioResource indexOneResource = writer.AudioResourcesByIndex[1]
            ?? throw new AssertFailedException("The first aliased WAV resource was not loaded.");
        BmsAudioResource indexTwoResource = writer.AudioResourcesByIndex[2]
            ?? throw new AssertFailedException("The second aliased WAV resource was not loaded.");
        Assert.AreSame(indexOneResource, indexTwoResource);
        Assert.AreSame(indexOneResource.Audio, indexTwoResource.Audio);
        writer.DisposeAudioSourcesAfterUse();
    }

    [TestMethod]
    public async Task InternalPlayer_NotifiesExitOnceAfterNaturalPlaybackCleanupCompletes()
    {
        using var directory = new TemporaryDirectory();
        File.WriteAllBytes(
            directory.File("audio.wav"),
            BuildFloatWave(48000, BuildConstantSamples(4800, 0.125f)));
        string chartPath = directory.File("natural-exit-chart.bms");
        WriteChart(chartPath,
            "#WAV01 audio.wav\n"
            + "#00002:0.001\n"
            + "#00011:01\n");
        var stopping = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var releaseStopping = new ManualResetEventSlim(initialState: false);
        var player = new GatedStoppingBmsAutoPlayer(
            new BMSFile(chartPath),
            new BassMixerSourceNativeBoundary(),
            stopping,
            releaseStopping);
        var wrapper = new InternalBMSAutoPlayerSoundOnly(
            new RealtimeTestPlayerSettingsGateway(),
            new RealtimeTestPlaybackRuntime(),
            autoPlayerFactory: _ => player);
        var exitNotified = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        int exitCount = 0;
        bool cleanupWasCompleteAtNotification = false;
        Task playback = Task.CompletedTask;

        try
        {
            playback = wrapper.PlayStart(chartPath, (_, _) =>
            {
                Interlocked.Increment(ref exitCount);
                BassAudioSession session = player.ResourceSession;
                cleanupWasCompleteAtNotification = player.RealtimeScheduler == null
                    && player.PlayState == PlayState.Stopped
                    && player.CurrentTime == player.Duration
                    && BassAudioPlayer.OutputMixerHandle == session.MixerHandle
                    && session.GetPlayerStreams().Count == 0;
                exitNotified.TrySetResult();
            });

            Task boundary = await Task.WhenAny(stopping.Task, playback);
            if (!ReferenceEquals(boundary, stopping.Task))
            {
                await playback;
            }
            await stopping.Task;
            Assert.IsFalse(playback.IsCompleted,
                "曲長到達後も再生Taskは音声cleanupの完了まで保留されます。");
            Assert.IsFalse(exitNotified.Task.IsCompleted,
                "終了通知は再生Taskのcleanup完了前に進みません。");

            releaseStopping.Set();
            Task completionBoundary = await Task.WhenAny(exitNotified.Task, playback);
            if (ReferenceEquals(completionBoundary, playback))
            {
                await playback;
            }
            await exitNotified.Task;
            await playback;

            Assert.AreEqual(1, Volatile.Read(ref exitCount));
            Assert.IsTrue(cleanupWasCompleteAtNotification,
                "通知時点でscheduler、tempo出力、voiceのcleanupが完了している必要があります。");
        }
        finally
        {
            releaseStopping.Set();
            try
            {
                await playback;
            }
            catch
            {
            }
            wrapper.CloseProcess();
        }
    }
    [TestMethod]
    public async Task PauseSeekAndRateChangesKeepTheRealtimeGraphFrozenUntilResume()
    {
        using var directory = new TemporaryDirectory();
        const int sourceFrameCount = 48000 * 4;
        File.WriteAllBytes(
            directory.File("audio.wav"),
            BuildFloatWave(48000, BuildConstantSamples(sourceFrameCount, 0.05f)));
        string chartPath = directory.File("chart.bms");
        WriteChart(chartPath,
            "#WAV01 audio.wav\n"
            + "#00011:01\n"
            + "#00111:01\n");
        var player = new TickObservedBmsAutoPlayer(new BMSFile(chartPath));
        player.LoadResources();
        BassAudioSession session = player.ResourceSession;
        int capturedResamplingQuality = session.SampleRateConversionQuality;
        Assert.IsTrue(Bass.ChannelGetAttribute(
            session.MixerHandle,
            (ChannelAttribute)0x15001,
            out float capturedMixerThreadCount));
        int originalCallbackOutputHandle = session.CallbackOutputHandle;
        AudioPcmRenderer? originalCallbackRenderer = session.CallbackPcmRenderer;
        AudioOutputProcessor? originalOutputProcessor = session.OutputProcessor;
        session.CallbackPcmRenderer = new AudioPcmRenderer(BassAudioPlayer.OutputMixerHandle, 48000, 2);
        session.OutputProcessor = new AudioOutputProcessor(48000, 1d);
        session.PublishCallbackOutputHandle(BassAudioPlayer.OutputMixerHandle);
        long[] eventFramesBefore = player.AudioSchedule.Events.Select(item => item.StartFrame).ToArray();

        void AssertCapturedAudioSettingsRemain()
        {
            Assert.AreEqual(capturedResamplingQuality, session.SampleRateConversionQuality);
            Assert.IsTrue(Bass.ChannelGetAttribute(
                session.MixerHandle,
                (ChannelAttribute)0x15001,
                out float currentMixerThreadCount));
            Assert.AreEqual(capturedMixerThreadCount, currentMixerThreadCount);
        }

        try
        {
            Task<PlaybackStateSnapshot> firstTick = player.ArmNextStateApplication();
            Task playback = player.Start();
            _ = await AwaitTickOrPlaybackCompletion(firstTick, playback);

            player.Pause();
            Assert.AreEqual(PlayState.Paused, player.PlayState);
            AssertCapturedAudioSettingsRemain();
            Assert.IsTrue(session.WithCallbackOutputPull(paused => paused));
            long mixerFrameBeforePausedPull = GetMixerFrame(session, channelCount: 2);
            const int callbackFrames = 32;
            int callbackBytes = callbackFrames * 2 * sizeof(float);
            IntPtr buffer = Marshal.AllocHGlobal(callbackBytes);
            try
            {
                for (int offset = 0; offset < callbackBytes; offset += sizeof(int))
                {
                    Marshal.WriteInt32(buffer, offset, unchecked((int)0x7fc00000));
                }

                Assert.AreEqual(
                    callbackBytes,
                    BassAudioPlayer.ReadPublishedCallbackOutput(session, buffer, callbackBytes));
                float[] callbackPcm = new float[callbackFrames * 2];
                Marshal.Copy(buffer, callbackPcm, 0, callbackPcm.Length);
                CollectionAssert.AreEqual(new float[callbackPcm.Length], callbackPcm);
                Assert.AreEqual(mixerFrameBeforePausedPull, GetMixerFrame(session, channelCount: 2));
            }
            finally
            {
                Marshal.FreeHGlobal(buffer);
            }

            foreach (float rate in new[] { 0.05f, 2f, 1f, 50f, 1f })
            {
                player.PlaybackRate = rate;
                Assert.AreEqual(rate, player.PlaybackRate);
                Assert.IsTrue(session.WithCallbackOutputPull(paused => paused));
            }
            CollectionAssert.AreEqual(eventFramesBefore, player.AudioSchedule.Events.Select(item => item.StartFrame).ToArray());
            AssertCapturedAudioSettingsRemain();

            var firstSeek = TimeSpan.FromMilliseconds(1500);
            player.CurrentTime = firstSeek;
            Assert.AreEqual(PlayState.Paused, player.PlayState);
            AssertCapturedAudioSettingsRemain();
            Assert.IsTrue(session.WithCallbackOutputPull(paused => paused));
            long firstSeekFrame = 72000;
            Assert.AreEqual(firstSeekFrame, player.RealtimeScheduler!.CurrentSongFrame);
            Assert.AreEqual(mixerFrameBeforePausedPull, GetMixerFrame(session, channelCount: 2));

            Task<PlaybackStateSnapshot> resumedTick = player.ArmNextStateApplication();
            player.Pause();
            long resumedMixerFrame = (await AwaitTickOrPlaybackCompletion(resumedTick, playback)).SongFrame;
            Assert.AreEqual(PlayState.Playing, player.PlayState);
            AssertCapturedAudioSettingsRemain();
            Assert.IsFalse(session.WithCallbackOutputPull(paused => paused));
            while (resumedMixerFrame <= firstSeekFrame)
            {
                Task<PlaybackStateSnapshot> nextTick = player.ArmNextStateApplication();
                resumedMixerFrame = (await AwaitTickOrPlaybackCompletion(nextTick, playback)).SongFrame;
            }
            Assert.IsTrue(resumedMixerFrame > firstSeekFrame);

            player.Pause();
            Assert.AreEqual(PlayState.Paused, player.PlayState);
            var finalSeek = TimeSpan.FromMilliseconds(3500);
            player.CurrentTime = finalSeek;
            AssertCapturedAudioSettingsRemain();
            Assert.IsTrue(session.WithCallbackOutputPull(paused => paused));
            Assert.AreEqual(168000L, player.RealtimeScheduler!.CurrentSongFrame);

            Task<PlaybackStateSnapshot> finalTick = player.ArmNextStateApplication();
            player.Pause();
            _ = await AwaitTickOrPlaybackCompletion(finalTick, playback);
            await playback;
            Assert.AreEqual(PlayState.Stopped, player.PlayState);
            AssertCapturedAudioSettingsRemain();
            Assert.IsFalse(session.HasCallbackOutputFailure);
        }
        finally
        {
            try
            {
                if (player.PlayState != PlayState.Stopped)
                {
                    player.Stop();
                }
                player.DisposeAudioSourcesAfterUse();
            }
            finally
            {
                session.SetCallbackOutputPaused(true);
                session.PublishCallbackOutputHandle(originalCallbackOutputHandle);
                session.CallbackPcmRenderer = originalCallbackRenderer;
                session.OutputProcessor = originalOutputProcessor;
            }
        }
    }

    [TestMethod]
    public async Task PausedSliderSeekFailureEndsPlaybackThroughItsOwnerAndKeepsOutputFrozen()
    {
        using var directory = new TemporaryDirectory();
        File.WriteAllBytes(
            directory.File("long.wav"),
            BuildFloatWave(48000, BuildConstantSamples(48000 * 4, 0.125f)));
        string chartPath = directory.File("paused-seek-failure.bms");
        WriteChart(chartPath, "#WAV01 long.wav\n#00011:01\n");

        var native = new FaultInjectingScheduledNativeBoundary(
            failResumeAt: 0,
            failNextUnlock: false,
            failSetPositionAt: 2);
        var stopping = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var releaseStopping = new ManualResetEventSlim();
        GatedStoppingBmsAutoPlayer? activePlayer = null;
        var wrapper = new InternalBMSAutoPlayerSoundOnly(
            new RealtimeTestPlayerSettingsGateway(),
            new RealtimeTestPlaybackRuntime(),
            autoPlayerFactory: path => activePlayer = new GatedStoppingBmsAutoPlayer(
                new BMSFile(path),
                native,
                stopping,
                releaseStopping));
        Task? start = null;

        try
        {
            PlaybackStartOperation operation = wrapper.BeginStart(chartPath, null, allowPreload: true);
            start = operation.Completion;
            await operation.Ready;
            GatedStoppingBmsAutoPlayer player = activePlayer
                ?? throw new AssertFailedException("UI playback did not create its BMS player.");
            BassAudioSession session = player.ResourceSession;
            Assert.AreEqual(PlayState.Playing, player.PlayState);

            wrapper.PausePlayingBMSfileToggle();
            Assert.AreEqual(PlayState.Paused, player.PlayState);
            Assert.IsTrue(session.IsCallbackOutputPaused);
            long mixerFrameBeforeFailure = GetMixerFrame(session, channelCount: 2);

            // UIのslider setterは元のnative seek例外を吸収するため、再生Taskの終了を別に確認する。
            wrapper.CurrentTime = TimeSpan.FromMilliseconds(500);
            Assert.IsTrue(session.HasCallbackOutputFailure,
                "Pause中seekの失敗はcallback healthへ反映されます。");
            Assert.IsTrue(native.SetPositionCalls >= 2,
                "一回目のposition設定は旧voice停止、二回目はseek先voice準備です。");
            await stopping.Task.WaitAsync(TimeSpan.FromSeconds(3));

            Assert.IsFalse(start.IsCompleted,
                "Startはcleanup所有者が完了するまで終了扱いになりません。");
            Assert.AreEqual(PlayState.Paused, player.PlayState,
                "cleanup ownerが解放されるまではpause状態を維持します。");
            Assert.IsTrue(session.IsCallbackOutputPaused,
                "失敗したpause seekはcallback出力を再開しません。");
            Assert.IsTrue(session.HasCallbackOutputFailure);
            Assert.IsNull(player.RealtimeScheduler,
                "失敗したseek区間はschedulerをpublishしません。");

            const int callbackFrames = 32;
            int callbackBytes = callbackFrames * 2 * sizeof(float);
            IntPtr callbackBuffer = Marshal.AllocHGlobal(callbackBytes);
            try
            {
                Assert.AreEqual(
                    callbackBytes,
                    BassAudioPlayer.ReadPublishedCallbackOutput(session, callbackBuffer, callbackBytes));
                float[] callbackPcm = new float[callbackFrames * 2];
                Marshal.Copy(callbackBuffer, callbackPcm, 0, callbackPcm.Length);
                CollectionAssert.AreEqual(new float[callbackPcm.Length], callbackPcm);
                Assert.AreEqual(mixerFrameBeforeFailure, GetMixerFrame(session, channelCount: 2));
            }
            finally
            {
                Marshal.FreeHGlobal(callbackBuffer);
            }

            Assert.IsTrue(native.SetPositionCalls > 0,
                "The injected failure must occur in the real active-voice seek path.");
            releaseStopping.Set();

            Exception? startFailure = null;
            try
            {
                await start.WaitAsync(TimeSpan.FromSeconds(3));
            }
            catch (Exception exception)
            {
                startFailure = exception;
            }

            Assert.IsNotNull(startFailure, "The Start Task must report the seek failure.");
            Assert.IsFalse(startFailure is TimeoutException, "Playback cleanup did not complete after release.");
            Assert.IsTrue(GetExceptionChain(startFailure).Any(exception => exception is BmsScheduledAudioException),
                "The Start Task must retain the original scheduled seek failure.");
            Assert.AreEqual(PlayState.Stopped, player.PlayState);
            Assert.IsNull(player.RealtimeScheduler);
            Assert.AreEqual(session.MixerHandle, BassAudioPlayer.OutputMixerHandle);
            Assert.AreEqual(0, session.GetPlayerStreams().Count,
                "The playback owner releases every scheduled native voice before Start completes.");
            Assert.IsTrue(session.HasCallbackOutputFailure,
                "The session remains faulted after failed seek cleanup.");
            Assert.IsTrue(session.IsCallbackOutputPaused,
                "Faulted output remains paused after the owner completes cleanup.");
        }
        finally
        {
            releaseStopping.Set();
            if (start != null && !start.IsCompleted)
            {
                try
                {
                    await start.WaitAsync(TimeSpan.FromSeconds(3));
                }
                catch
                {
                }
            }
            wrapper.CloseProcess();
        }
    }

    [TestMethod]
    public Task PausedResumeReservationFailureEndsPlaybackThroughItsOwner() =>
        VerifyPausedReservationFailureEndsPlaybackThroughItsOwner(
            static (wrapper, _) => wrapper.PausePlayingBMSfileToggle());

    [TestMethod]
    public Task PausedPlaybackRateReservationFailureEndsPlaybackThroughItsOwner() =>
        VerifyPausedReservationFailureEndsPlaybackThroughItsOwner(
            static (_, player) => player.PlaybackRate = 50f);

    private async Task VerifyPausedReservationFailureEndsPlaybackThroughItsOwner(
        Action<InternalBMSAutoPlayerSoundOnly, GatedStoppingBmsAutoPlayer> failPausedControl)
    {
        using var directory = new TemporaryDirectory();
        File.WriteAllBytes(
            directory.File("long.wav"),
            BuildFloatWave(48000, BuildConstantSamples(48000 * 4, 0.125f)));
        string chartPath = directory.File("paused-reservation-failure.bms");
        WriteChart(chartPath, "#WAV01 long.wav\n#00011:0101\n");

        var native = new FaultInjectingScheduledNativeBoundary(failResumeAt: 0, failNextUnlock: false);
        var stopping = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var releaseStopping = new ManualResetEventSlim();
        GatedStoppingBmsAutoPlayer? activePlayer = null;
        var wrapper = new InternalBMSAutoPlayerSoundOnly(
            new RealtimeTestPlayerSettingsGateway(),
            new RealtimeTestPlaybackRuntime(),
            autoPlayerFactory: path => activePlayer = new GatedStoppingBmsAutoPlayer(
                new BMSFile(path),
                native,
                stopping,
                releaseStopping));
        Task? start = null;

        try
        {
            PlaybackStartOperation operation = wrapper.BeginStart(chartPath, null, allowPreload: true);
            start = operation.Completion;
            await operation.Ready;
            GatedStoppingBmsAutoPlayer player = activePlayer
                ?? throw new AssertFailedException("UI playback did not create its BMS player.");
            BassAudioSession session = player.ResourceSession;
            Assert.AreEqual(PlayState.Playing, player.PlayState);

            wrapper.PausePlayingBMSfileToggle();
            Assert.AreEqual(PlayState.Paused, player.PlayState);
            Assert.IsTrue(session.IsCallbackOutputPaused);
            Assert.AreEqual(1, session.GetPlayerStreams().Count,
                "開始時点のlook-aheadには先頭eventだけが予約されます。");

            // 1秒blockの実測を加え、Pause解除前のEnsureLeadだけが次eventを予約する条件を作る。
            session.ObserveCallbackPullSize(48000);
            native.FailNextResume();
            Exception? controlFailure = null;
            try
            {
                failPausedControl(wrapper, player);
            }
            catch (Exception exception)
            {
                controlFailure = exception;
            }

            Assert.IsNotNull(controlFailure, "先読み予約のnative失敗が制御入口へ返ります。");
            Assert.IsTrue(
                GetExceptionChain(controlFailure).Any(exception => exception is BmsScheduledAudioException),
                "Pause/PlaybackRateのnative失敗はscheduled voiceの主失敗を保ちます。");
            Assert.IsTrue(session.HasCallbackOutputFailure);
            Assert.IsTrue(session.IsCallbackOutputPaused,
                "予約失敗後にcallback outputを再開しません。");
            Assert.AreEqual(1, session.GetPlayerStreams().Count,
                "失敗した予約voiceはrollbackされ、既存voiceだけが終了ownerのcleanupまで残ります。");

            await stopping.Task.WaitAsync(TimeSpan.FromSeconds(3));
            Assert.IsFalse(start.IsCompleted,
                "PlayStartはcleanup ownerが実際に解放するまで完了しません。");
            Assert.IsTrue(session.IsCallbackOutputPaused);

            releaseStopping.Set();
            Exception? startFailure = null;
            try
            {
                await start.WaitAsync(TimeSpan.FromSeconds(3));
            }
            catch (Exception exception)
            {
                startFailure = exception;
            }

            Assert.IsNotNull(startFailure, "PlayStartは制御失敗を公開します。");
            Assert.IsFalse(startFailure is TimeoutException, "再生終了ownerがcleanup完了までに戻りませんでした。");
            Assert.IsTrue(
                GetExceptionChain(startFailure).Any(exception => exception is BmsScheduledAudioException),
                "PlayStartは予約時のnative主失敗を保持します。");
            Assert.AreEqual(PlayState.Stopped, player.PlayState);
            Assert.IsNull(player.RealtimeScheduler);
            Assert.AreEqual(session.MixerHandle, BassAudioPlayer.OutputMixerHandle);
            Assert.AreEqual(0, session.GetPlayerStreams().Count,
                "終了ownerがすべてのscheduled native voiceを解放します。");
            Assert.IsTrue(session.HasCallbackOutputFailure,
                "失敗したsessionは故障状態を維持します。");
            Assert.IsTrue(session.IsCallbackOutputPaused,
                "故障sessionの出力はcleanup後も停止状態を維持します。");
        }
        finally
        {
            releaseStopping.Set();
            if (start != null && !start.IsCompleted)
            {
                try
                {
                    await start.WaitAsync(TimeSpan.FromSeconds(3));
                }
                catch
                {
                }
            }
            wrapper.CloseProcess();
        }
    }

    [TestMethod]
    public async Task RealtimeTickAndControlsAreSerializedThroughChartStateApplication()
    {
        using var directory = new TemporaryDirectory();
        File.WriteAllBytes(
            directory.File("audio.wav"),
            BuildFloatWave(48000, BuildConstantSamples(48000 * 4, 0.05f)));
        string chartPath = directory.File("control-race-chart.bms");
        WriteChart(chartPath,
            "#BPM01 240\n"
            + "#WAV01 audio.wav\n"
            + "#00008:01\n"
            + "#00011:0101\n"
            + "#00111:01\n");

        var player = new TickObservedBmsAutoPlayer(new BMSFile(chartPath));
        player.LoadResources();
        BassAudioSession session = player.ResourceSession;
        int originalCallbackOutputHandle = session.CallbackOutputHandle;
        AudioPcmRenderer? originalCallbackRenderer = session.CallbackPcmRenderer;
        AudioOutputProcessor? originalOutputProcessor = session.OutputProcessor;
        var callbackPull = new BlockingPcmNative(ManagedBassAudioPcmNative.Instance);
        session.CallbackPcmRenderer = new AudioPcmRenderer(
            BassAudioPlayer.OutputMixerHandle,
            48000,
            2,
            callbackPull);
        session.OutputProcessor = new AudioOutputProcessor(48000, 1d);
        session.PublishCallbackOutputHandle(BassAudioPlayer.OutputMixerHandle);

        var operationTasks = new List<Task>();
        IntPtr callbackBuffer = IntPtr.Zero;
        PlaybackTickGate? activeTickGate = null;
        Task<int>? callbackTask = null;
        Task? playback = null;
        try
        {
            const int callbackFrames = 64;
            int callbackBytes = callbackFrames * 2 * sizeof(float);
            callbackBuffer = Marshal.AllocHGlobal(callbackBytes);
            for (int offset = 0; offset < callbackBytes; offset += sizeof(int))
            {
                Marshal.WriteInt32(callbackBuffer, offset, unchecked((int)0x7fc00000));
            }

            activeTickGate = player.BlockNextTickBeforeStateApplication();
            Task<PlaybackStateSnapshot> firstState = player.ArmNextStateApplication();
            playback = player.Start();
            TimeSpan firstTickTime = await activeTickGate.Entered;

            callbackTask = Task.Run(() =>
                BassAudioPlayer.ReadPublishedCallbackOutput(session, callbackBuffer, callbackBytes));
            bool callbackEnteredNativePull = callbackPull.PullCompleted.Wait(TimeSpan.FromSeconds(3));
            callbackPull.AllowCallbackReturn.Set();
            bool callbackCompletedWhileTickHeld = true;
            try
            {
                _ = await callbackTask.WaitAsync(TimeSpan.FromSeconds(3));
            }
            catch (TimeoutException)
            {
                callbackCompletedWhileTickHeld = false;
            }

            ControlOperationObservation pauseObservation = player.ArmPauseOperation();
            var pause = Task.Run(player.Pause);
            operationTasks.Add(pause);
            await pauseObservation.Entered;
            await AssertControlOperationBlocked(pauseObservation.Completed);
            activeTickGate.Release();

            PlaybackStateSnapshot initialState = await AwaitStateApplicationOrPlaybackCompletion(firstState, playback);
            long pauseSequence = await pauseObservation.Completed;
            await pause;
            Assert.IsTrue(initialState.Sequence < pauseSequence);
            Assert.AreEqual(firstTickTime, initialState.TickTime);
            Assert.AreEqual(initialState.TickTime, initialState.CurrentTime,
                "再生時刻の読み取りと譜面への反映は同じtick値を使います。");
            Assert.AreEqual(240d, initialState.CurrentBpm);
            Assert.AreEqual(1, initialState.Combo);
            Assert.IsTrue(initialState.NoteDensity > 0d);
            Assert.AreEqual(PlayState.Paused, player.PlayState);

            activeTickGate.Dispose();
            activeTickGate = player.BlockNextTickBeforeStateApplication();
            Task<PlaybackStateSnapshot> seekState = player.ArmNextStateApplication();
            player.Pause();
            TimeSpan seekTickTime = await activeTickGate.Entered;
            ControlOperationObservation seekObservation = player.ArmSeekOperation();
            var seekTime = TimeSpan.FromMilliseconds(1500);
            Task<PlaybackStateSnapshot> seekApplication = player.ArmSeekStateApplication(seekTime);
            Task seek = Task.Run(() => player.CurrentTime = seekTime);
            operationTasks.Add(seek);
            await seekObservation.Entered;
            await AssertControlOperationBlocked(seekObservation.Completed);
            activeTickGate.Release();

            PlaybackStateSnapshot seekTick = await AwaitStateApplicationOrPlaybackCompletion(seekState, playback);
            long seekSequence = await seekObservation.Completed;
            await seek;
            PlaybackStateSnapshot appliedSeek = await AwaitStateApplicationOrPlaybackCompletion(seekApplication, playback);
            Assert.IsTrue(seekTick.Sequence < seekSequence);
            Assert.AreEqual(seekTickTime, seekTick.TickTime);
            Assert.AreEqual(seekTick.TickTime, seekTick.CurrentTime,
                "seek操作との競合中も譜面状態反映は観測済みのtick時刻を使います。");
            Assert.AreEqual(PlayState.Playing, appliedSeek.PlayState);
            Assert.AreEqual(seekTime, appliedSeek.CurrentTime);
            Assert.AreEqual(240d, appliedSeek.CurrentBpm);
            Assert.AreEqual(3, appliedSeek.Combo);
            Assert.IsTrue(seekTick.Combo < appliedSeek.Combo);
            Assert.AreEqual(2d, appliedSeek.NoteDensity,
                "seek先1.5秒の直前1秒には、0.5秒と1秒の二つのノートが含まれます。");
            Assert.IsTrue(seekTick.NoteDensity < appliedSeek.NoteDensity);

            activeTickGate.Dispose();
            player.Pause();
            activeTickGate = player.BlockNextTickBeforeStateApplication();
            Task<PlaybackStateSnapshot> rateState = player.ArmNextStateApplication();
            player.Pause();
            TimeSpan rateTickTime = await activeTickGate.Entered;
            ControlOperationObservation rateObservation = player.ArmPlaybackRateOperation();
            Task rate = Task.Run(() => player.PlaybackRate = 2f);
            operationTasks.Add(rate);
            await rateObservation.Entered;
            await AssertControlOperationBlocked(rateObservation.Completed);
            activeTickGate.Release();

            PlaybackStateSnapshot rateTick = await AwaitStateApplicationOrPlaybackCompletion(rateState, playback);
            long rateSequence = await rateObservation.Completed;
            await rate;
            Assert.IsTrue(rateTick.Sequence < rateSequence);
            Assert.AreEqual(rateTickTime, rateTick.TickTime);
            Assert.AreEqual(rateTick.TickTime, rateTick.CurrentTime,
                "速度変更との競合中も譜面状態反映は観測済みのtick時刻を使います。");
            Assert.AreEqual(2f, player.PlaybackRate);
            Assert.IsTrue(player.CurrentTime >= seekTime,
                "速度変更後もinput mixer時計はseek先以降へ進みます。");
            Assert.AreEqual(240d, player.CurrentBpm);
            Assert.AreEqual(3, player.Combo);

            Assert.IsTrue(callbackEnteredNativePull);
            Assert.IsTrue(callbackCompletedWhileTickHeld);
            Assert.IsFalse(session.HasCallbackOutputFailure);
        }
        finally
        {
            activeTickGate?.Release();
            callbackPull.AllowCallbackReturn.Set();
            foreach (Task operation in operationTasks)
            {
                try
                {
                    await operation;
                }
                catch
                {
                }
            }
            if (callbackTask != null)
            {
                try
                {
                    _ = await callbackTask;
                }
                catch
                {
                }
            }
            if (playback != null && player.PlayState != PlayState.Stopped)
            {
                player.Stop();
            }
            player.DisposeAudioSourcesAfterUse();
            session.SetCallbackOutputPaused(true);
            session.PublishCallbackOutputHandle(originalCallbackOutputHandle);
            session.CallbackPcmRenderer = originalCallbackRenderer;
            session.OutputProcessor = originalOutputProcessor;
            if (callbackBuffer != IntPtr.Zero)
            {
                Marshal.FreeHGlobal(callbackBuffer);
            }
            activeTickGate?.Dispose();
        }
    }

    [TestMethod]
    public async Task SeekAfterNaturalCleanupChangesStoppedPositionWithoutStartingNewAudioSegment()
    {
        using var directory = new TemporaryDirectory();
        File.WriteAllBytes(directory.File("audio.wav"), BuildFloatWave(48000, BuildSourceSamples()));
        string chartPath = directory.File("natural-end-seek-chart.bms");
        WriteChart(chartPath,
            "#WAV01 audio.wav\n"
            + "#00011:01\n"
            + "#00111:01\n");
        var player = new TickObservedBmsAutoPlayer(new BMSFile(chartPath));
        player.LoadResources();

        PlaybackCleanupGate cleanupGate = player.BlockNextPlaybackCleanup();
        Task<PlaybackStateSnapshot> stateApplication = player.ArmNextCompletedStateApplication();
        Task playback = player.Start();
        try
        {
            await cleanupGate.Entered;
            Assert.IsFalse(playback.IsCompleted, "再生Taskは音声cleanupの完了前に完了してはいけません。");
            PlaybackStateSnapshot finalState = await stateApplication;
            Assert.IsTrue(finalState.TickCompleted);

            ControlOperationObservation seekObservation = player.ArmSeekOperation();
            var stoppedSeek = TimeSpan.FromMilliseconds(250);
            Task seek = Task.Run(() => player.CurrentTime = stoppedSeek);
            try
            {
                await seekObservation.Entered;
                await AssertControlOperationBlocked(seekObservation.Completed);
                Assert.IsFalse(playback.IsCompleted);
            }
            finally
            {
                cleanupGate.Release();
            }

            await playback;
            long seekSequence = await seekObservation.Completed;
            await seek;
            Assert.IsTrue(finalState.Sequence < seekSequence);
            Assert.AreEqual(PlayState.Stopped, player.PlayState);
            Assert.AreEqual(stoppedSeek, player.CurrentTime);
            Assert.IsNull(player.RealtimeScheduler);
        }
        finally
        {
            cleanupGate.Release();
            if (player.PlayState != PlayState.Stopped)
            {
                player.Stop();
            }
            player.DisposeAudioSourcesAfterUse();
            cleanupGate.Dispose();
        }
    }

    [TestMethod]
    public async Task ControlsCompletedBeforeTheFirstTickUseTheNewPausedSeekState()
    {
        using var directory = new TemporaryDirectory();
        File.WriteAllBytes(directory.File("audio.wav"), BuildFloatWave(48000, BuildSourceSamples()));
        string chartPath = directory.File("operation-first-chart.bms");
        WriteChart(chartPath,
            "#BPM01 240\n"
            + "#WAV01 audio.wav\n"
            + "#00008:01\n"
            + "#00011:0101\n"
            + "#00111:0001\n");
        var player = new TickObservedBmsAutoPlayer(new BMSFile(chartPath));
        player.LoadResources();
        using var scheduler = new QueuedPlaybackTaskScheduler();

        Task<Task> startDispatch = Task.Factory.StartNew(
            player.Start,
            CancellationToken.None,
            TaskCreationOptions.None,
            scheduler);
        scheduler.ExecuteNext();
        Task playback = await startDispatch;
        try
        {
            Assert.AreEqual(1, scheduler.PendingCount);
            Assert.AreEqual(PlayState.Playing, player.PlayState);

            player.Pause();
            var seekTime = TimeSpan.FromMilliseconds(1500);
            player.CurrentTime = seekTime;
            player.PlaybackRate = 2f;
            Assert.AreEqual(PlayState.Paused, player.PlayState);
            Assert.AreEqual(AudioFrameMath.TimeToFrame(seekTime, 48000), player.RealtimeScheduler!.CurrentSongFrame);
            Assert.IsTrue(player.ResourceSession.WithCallbackOutputPull(paused => paused));

            Task<PlaybackStateSnapshot> firstStateApplication = player.ArmNextStateApplication();
            player.Pause();
            var worker = Task.Run(scheduler.ExecuteNext);
            await worker;
            scheduler.ExecuteAll();
            PlaybackStateSnapshot state = await firstStateApplication;
            await playback;

            Assert.IsTrue(state.TickTime >= seekTime);
            Assert.IsTrue(state.CurrentTime >= seekTime);
            Assert.AreEqual(240d, state.CurrentBpm);
            Assert.AreEqual(3, state.Combo);
            Assert.IsTrue(state.NoteDensity > 0d);
            Assert.AreEqual(PlayState.Stopped, player.PlayState);
            Assert.IsNull(player.RealtimeScheduler);
            Assert.IsFalse(player.ResourceSession.HasCallbackOutputFailure);
        }
        finally
        {
            if (player.PlayState != PlayState.Stopped)
            {
                var cancellationRequested = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
                using CancellationTokenRegistration registration = player.PlaybackCancellationToken.Register(
                    () => cancellationRequested.TrySetResult());
                var stop = Task.Run(player.Stop);
                await cancellationRequested.Task;
                scheduler.ExecuteAll();
                await stop;
            }
            else
            {
                scheduler.ExecuteAll();
            }
            player.DisposeAudioSourcesAfterUse();
        }
    }

    [TestMethod]
    public void SeekSegment_RestoresOnlyHalfOpenActiveVoicesAtTheirSourceGrid()
    {
        using var directory = new TemporaryDirectory();
        float[] longSource = BuildRampSamples(28665, scale: 1f / 100000f);
        float[] endedAtSeekSource = BuildConstantSamples(8820, 0.125f);
        float[] startsAtSeekSource = BuildConstantSamples(48, 0.0625f);
        File.WriteAllBytes(directory.File("long.wav"), BuildFloatWave(44100, longSource));
        File.WriteAllBytes(directory.File("ended.wav"), BuildFloatWave(44100, endedAtSeekSource));
        File.WriteAllBytes(directory.File("starts.wav"), BuildFloatWave(44100, startsAtSeekSource));
        string chartPath = directory.File("seek-chart.bms");
        WriteChart(chartPath,
            "#WAV01 long.wav\n"
            + "#WAV02 ended.wav\n"
            + "#WAV03 starts.wav\n"
            + "#00001:01000001000000000000\n"
            + "#00011:02030000000000000000\n");

        var player = new BMSAutoPlayer(new BMSFile(chartPath));
        player.LoadResources();
        BassAudioSession session = player.ResourceSession;
        BmsAudioFrameSchedule schedule = player.AudioSchedule;
        BmsAudioFrameEvent startsAtSeek = schedule.Events.Single(item => item.WavIndex == 3);
        const long seekSongFrame = 9600;
        Assert.AreEqual(seekSongFrame, startsAtSeek.StartFrame);
        const long expectedSourceFrame = 8820;
        Assert.AreEqual(expectedSourceFrame, AudioFrameMath.SourceFrameFromMixerFrames(seekSongFrame, 44100, 48000));
        long mixerFrameAtSeek = GetMixerFrame(session, channelCount: 2);

        using (var scheduler = new BmsRealtimeAudioScheduler(
                   schedule,
                   player.AudioResourcesByIndex,
                   session,
                   new BassMixerSourceNativeBoundary(),
                   player.Duration,
                   seekSongFrame,
                   playbackRate: 1f,
                   generation: 3))
        {
            var renderer = new AudioPcmRenderer(session.MixerHandle, 48000, 2);
            Assert.AreEqual(mixerFrameAtSeek, scheduler.OriginMixerFrame + seekSongFrame,
                "seek先のsong frameは、停止中の現在input mixer位置へ対応します。");
            float[] atSeek = new float[200 * 2];
            AudioPcmReadResult firstPull = renderer.ReadFrames(atSeek.AsSpan(0, 100 * 2), 100);
            Assert.AreEqual(100, firstPull.FramesRead);
            Assert.AreEqual((float)(longSource[expectedSourceFrame * 2] + startsAtSeekSource[0]), atSeek[0], 1e-6f);
            Assert.AreEqual((float)(longSource[expectedSourceFrame * 2 + 1] + startsAtSeekSource[1]), atSeek[1], 1e-6f);

            scheduler.TickWithoutNullOutputAdvance();
            AudioPcmReadResult secondPull = renderer.ReadFrames(atSeek.AsSpan(100 * 2, 100 * 2), 100);
            Assert.AreEqual(100, secondPull.FramesRead);
            double secondSourcePosition = expectedSourceFrame + (100d * 44100d / 48000d);
            int secondSourceFrame = checked((int)Math.Floor(secondSourcePosition));
            double secondSourceFraction = secondSourcePosition - secondSourceFrame;
            for (int channel = 0; channel < 2; channel++)
            {
                double expected = longSource[secondSourceFrame * 2 + channel] * (1d - secondSourceFraction)
                    + longSource[(secondSourceFrame + 1) * 2 + channel] * secondSourceFraction;
                Assert.AreEqual((float)expected, atSeek[100 * 2 + channel], 1e-6f);
            }
        }

        player.DisposeAudioSourcesAfterUse();
    }

    [TestMethod]
    public async Task InternalPlayerRestart_RebasesAtCurrentMixerFrameWithoutArtificialSilence()
    {
        using var directory = new TemporaryDirectory();
        float[] sourceSamples = BuildTimingPatternSamples(48000 * 2);
        File.WriteAllBytes(directory.File("audio.wav"), BuildFloatWave(48000, sourceSamples));
        string chartPath = directory.File("restart-chart.bms");
        WriteChart(chartPath, "#WAV01 audio.wav\n#00011:01\n");

        TickObservedBmsAutoPlayer? activePlayer = null;
        var wrapper = new InternalBMSAutoPlayerSoundOnly(
            new RealtimeTestPlayerSettingsGateway(),
            new RealtimeTestPlaybackRuntime(),
            autoPlayerFactory: path => activePlayer = new TickObservedBmsAutoPlayer(new BMSFile(path)));
        Task? start = null;

        try
        {
            PlaybackStartOperation operation = wrapper.BeginStart(chartPath, null, allowPreload: true);
            start = operation.Completion;
            await operation.Ready;
            TickObservedBmsAutoPlayer player = activePlayer
                ?? throw new AssertFailedException("UI playback did not create its BMS player.");
            Task<PlaybackStateSnapshot> firstState = player.ArmNextStateApplication();
            _ = await AwaitTickOrPlaybackCompletion(firstState, start);
            wrapper.PausePlayingBMSfileToggle();

            BassAudioSession session = player.ResourceSession;
            Assert.AreEqual(PlayState.Paused, player.PlayState);
            Assert.IsTrue(session.IsCallbackOutputPaused);
            wrapper.CurrentTime = TimeSpan.FromMilliseconds(250);
            Assert.AreEqual(
                12000L,
                player.RealtimeScheduler!.CurrentSongFrame);

            wrapper.RestartPlayingBMSfile();

            Assert.AreEqual(PlayState.Paused, player.PlayState);
            Assert.IsTrue(session.IsCallbackOutputPaused);
            Assert.AreEqual(0L, player.RealtimeScheduler!.CurrentSongFrame);
            long currentMixerFrame = GetMixerFrame(session, channelCount: 2);
            Assert.AreEqual(currentMixerFrame, player.RealtimeScheduler.OriginMixerFrame,
                "Restart入口は停止中のinput mixer位置をsong frame 0へ対応させます。");

            var renderer = new AudioPcmRenderer(session.MixerHandle, 48000, 2);
            float[] firstFrames = new float[64 * 2];
            AudioPcmReadResult result = renderer.ReadFrames(firstFrames, 64);
            Assert.AreEqual(64, result.FramesRead);
            Assert.IsFalse(result.ReachedEnd);
            for (int frame = 0; frame < result.FramesRead; frame++)
            {
                Assert.AreEqual(sourceSamples[frame * 2], firstFrames[frame * 2], 1e-7f,
                    $"Restart input PCM has artificial silence at frame {frame}, left channel.");
                Assert.AreEqual(sourceSamples[frame * 2 + 1], firstFrames[frame * 2 + 1], 1e-7f,
                    $"Restart input PCM has artificial silence at frame {frame}, right channel.");
            }
        }
        finally
        {
            wrapper.CloseProcess();
            if (start != null)
            {
                try
                {
                    await start;
                }
                catch (OperationCanceledException)
                {
                }
            }
        }
    }

    [TestMethod]
    public async Task ReservationCommitFailureHidesAnInFlightCallbackAndReleasesPreparedVoices()
    {
        using var directory = new TemporaryDirectory();
        File.WriteAllBytes(directory.File("first.wav"), BuildFloatWave(48000, BuildConstantSamples(48000, 0.125f)));
        File.WriteAllBytes(directory.File("second.wav"), BuildFloatWave(48000, BuildConstantSamples(48000, 0.25f)));
        string chartPath = directory.File("atomic-chart.bms");
        WriteChart(chartPath,
            "#WAV01 first.wav\n"
            + "#WAV02 second.wav\n"
            + "#00002:0.001\n"
            + "#00011:0100\n"
            + "#00012:0002\n");
        var player = new BMSAutoPlayer(new BMSFile(chartPath));
        player.LoadResources();
        BassAudioSession session = player.ResourceSession;
        BmsAudioFrameEvent secondEvent = player.AudioSchedule.Events.Single(item => item.WavIndex == 2);
        Assert.IsTrue(secondEvent.StartFrame > player.AudioSchedule.Events.Single(item => item.WavIndex == 1).StartFrame);
        var nativePull = new BlockingPcmNative(ManagedBassAudioPcmNative.Instance);
        session.PublishCallbackOutputHandle(session.MixerHandle);
        session.CallbackPcmRenderer = new AudioPcmRenderer(session.MixerHandle, 48000, 2, nativePull);
        session.OutputProcessor = new AudioOutputProcessor(48000, 1d);
        session.SetCallbackOutputPaused(false);
        var boundary = new FaultInjectingScheduledNativeBoundary(failResumeAt: 2, failNextUnlock: false);
        const int callbackFrames = 256;
        int callbackBytes = callbackFrames * 2 * sizeof(float);
        IntPtr callbackBuffer = Marshal.AllocHGlobal(callbackBytes);

        try
        {
            for (int offset = 0; offset < callbackBytes; offset += sizeof(int))
            {
                Marshal.WriteInt32(callbackBuffer, offset, unchecked((int)0x7fc00000));
            }
            Task<int> callback = Task.Run(() =>
                BassAudioPlayer.ReadPublishedCallbackOutput(session, callbackBuffer, callbackBytes));
            nativePull.PullCompleted.Wait();

            BmsScheduledAudioException failure = Assert.ThrowsException<BmsScheduledAudioException>(() =>
                new BmsRealtimeAudioScheduler(
                    player.AudioSchedule,
                    player.AudioResourcesByIndex,
                    session,
                    boundary,
                    player.Duration,
                    segmentStartSongFrame: 0,
                    playbackRate: 1f,
                    generation: 9));
            Assert.AreEqual(2, failure.Event.WavIndex,
                "The second resume failure must identify the voice whose flags failed.");
            Assert.AreEqual(
                failure.OriginMixerFrame + secondEvent.StartFrame,
                failure.ExpectedMixerFrame,
                "Failure diagnostics must use the failing event's absolute mixer frame.");

            nativePull.AllowCallbackReturn.Set();
            Assert.AreEqual(callbackBytes, await callback);
            float[] callbackPcm = new float[callbackFrames * 2];
            Marshal.Copy(callbackBuffer, callbackPcm, 0, callbackPcm.Length);
            CollectionAssert.AreEqual(new float[callbackPcm.Length], callbackPcm);
            Assert.IsTrue(session.HasCallbackOutputFailure);
            Assert.AreEqual(0, session.AdditionalStreamHandles.Count);
            Assert.ThrowsException<AudioCallbackOutputFailureException>(session.ThrowPendingOutputFailure);
        }
        finally
        {
            nativePull.AllowCallbackReturn.Set();
            Marshal.FreeHGlobal(callbackBuffer);
            player.DisposeAudioSourcesAfterUse();
        }
    }

    [TestMethod]
    public void ReservationCommitFailureAtFirstResumeReportsTheFirstEvent()
    {
        using var directory = new TemporaryDirectory();
        File.WriteAllBytes(directory.File("first.wav"), BuildFloatWave(48000, BuildConstantSamples(48000, 0.125f)));
        File.WriteAllBytes(directory.File("second.wav"), BuildFloatWave(48000, BuildConstantSamples(48000, 0.25f)));
        string chartPath = directory.File("first-resume-failure.bms");
        WriteChart(chartPath,
            "#WAV01 first.wav\n"
            + "#WAV02 second.wav\n"
            + "#00002:0.001\n"
            + "#00011:0100\n"
            + "#00012:0002\n");
        var player = new BMSAutoPlayer(new BMSFile(chartPath));
        player.LoadResources();
        BmsAudioFrameEvent firstEvent = player.AudioSchedule.Events.Single(item => item.WavIndex == 1);
        var boundary = new FaultInjectingScheduledNativeBoundary(failResumeAt: 1, failNextUnlock: false);

        try
        {
            BmsScheduledAudioException failure = Assert.ThrowsException<BmsScheduledAudioException>(() =>
                new BmsRealtimeAudioScheduler(
                    player.AudioSchedule,
                    player.AudioResourcesByIndex,
                    player.ResourceSession,
                    boundary,
                    player.Duration,
                    segmentStartSongFrame: 0,
                    playbackRate: 1f,
                    generation: 10));
            Assert.AreEqual(1, failure.Event.WavIndex,
                "The first resume failure must not be attributed to the next prepared event.");
            Assert.AreEqual(
                failure.OriginMixerFrame + firstEvent.StartFrame,
                failure.ExpectedMixerFrame);
            Assert.IsTrue(player.ResourceSession.HasCallbackOutputFailure);
            Assert.AreEqual(0, player.ResourceSession.GetPlayerStreams().Count,
                "The failed commit releases both the failed and unattempted prepared voice.");
        }
        finally
        {
            player.DisposeAudioSourcesAfterUse();
        }
    }

    [DataTestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public async Task StopOrDisposeRetainsVoiceOwnerAndKeepsOutputPausedWhenVoiceCleanupFails(bool dispose)
    {
        using var directory = new TemporaryDirectory();
        File.WriteAllBytes(
            directory.File("long.wav"),
            BuildFloatWave(48000, BuildConstantSamples(48000 * 8, 0.25f)));
        string chartPath = directory.File("cleanup-failure.bms");
        WriteChart(chartPath, "#WAV01 long.wav\n#00011:01\n");
        var native = new FaultInjectingScheduledNativeBoundary(
            failResumeAt: 0,
            failNextUnlock: false,
            failRemoveChannel: true,
            failStreamFree: true);
        var player = new BMSAutoPlayer(new BMSFile(chartPath), native);
        player.LoadResources();
        BassAudioSession session = player.ResourceSession;
        int baselinePlayerStreams = session.GetPlayerStreams().Count;

        Task playback = player.Start();
        Task? stop = null;
        try
        {
            BmsRealtimeAudioScheduler scheduler = player.RealtimeScheduler
                ?? throw new AssertFailedException("The active playback did not create its realtime scheduler.");
            Assert.IsTrue(session.GetPlayerStreams().Count > baselinePlayerStreams,
                "The active playback must own a scheduled voice before stopping or disposing begins.");

            stop = Task.Run(dispose ? (Action)player.Dispose : player.Stop);
            AggregateException stopFailure = await Assert.ThrowsExceptionAsync<AggregateException>(() => stop);
            AggregateException playbackFailure = await Assert.ThrowsExceptionAsync<AggregateException>(() => playback);
            Assert.AreSame(playbackFailure, stopFailure,
                "Stopping or disposing must return the original playback cleanup failure.");

            Assert.IsTrue(stopFailure.Flatten().InnerExceptions.Any(exception =>
                exception is BassAudioPlaybackException),
                "Stopping or disposing must retain the scheduled voice removal failure.");
            Assert.IsTrue(stopFailure.Flatten().InnerExceptions.Any(exception =>
                exception is OperationCanceledException),
                "Stopping or disposing must preserve the cancellation that initiated cleanup.");
            Assert.IsTrue(playbackFailure.Flatten().InnerExceptions.Any(exception =>
                exception is BassAudioPlaybackException),
                "The Start task must retain the scheduled voice cleanup failure too.");
            Assert.IsTrue(native.RemoveChannelCalls > 0,
                "Voice cleanup must still attempt native channel detachment.");
            Assert.IsTrue(native.StreamFreeCalls > 0,
                "Scheduled voice disposal must attempt native release even after mixer detachment fails.");
            Assert.IsTrue(session.HasCallbackOutputFailure,
                "Cleanup failure must fault the session before callback output can resume.");
            Assert.IsTrue(session.IsCallbackOutputPaused,
                "Stopping or disposing must leave callback output paused after cleanup failure.");
            Assert.AreSame(scheduler, player.RealtimeScheduler,
                "The BMS player must retain its failed scheduler owner.");
            Assert.AreSame(session, player.ResourceSession,
                "The failed audio session must remain attached to its BMS player.");
            Assert.IsTrue(session.GetPlayerStreams().Count > baselinePlayerStreams,
                "A source whose native cleanup is unconfirmed must remain session-owned.");

            session.PublishCallbackOutputHandle(session.MixerHandle);
            session.CallbackPcmRenderer = new AudioPcmRenderer(session.MixerHandle, 48000, 2);
            session.OutputProcessor = new AudioOutputProcessor(48000, 1d);
            const int callbackFrames = 64;
            int callbackBytes = callbackFrames * 2 * sizeof(float);
            IntPtr callbackBuffer = Marshal.AllocHGlobal(callbackBytes);
            try
            {
                for (int offset = 0; offset < callbackBytes; offset += sizeof(int))
                {
                    Marshal.WriteInt32(callbackBuffer, offset, unchecked((int)0x7fc00000));
                }

                Assert.AreEqual(callbackBytes,
                    BassAudioPlayer.ReadPublishedCallbackOutput(session, callbackBuffer, callbackBytes));
                float[] callbackPcm = new float[callbackFrames * 2];
                Marshal.Copy(callbackBuffer, callbackPcm, 0, callbackPcm.Length);
                CollectionAssert.AreEqual(new float[callbackPcm.Length], callbackPcm,
                    "A faulted, paused callback must overwrite its full output block with silence.");
            }
            finally
            {
                Marshal.FreeHGlobal(callbackBuffer);
            }

            using var nextPlayer = new BMSAutoPlayer(new BMSFile(chartPath));
            nextPlayer.LoadResources();
            await Assert.ThrowsExceptionAsync<AudioCallbackOutputFailureException>(() => nextPlayer.Start(),
                "A new BMS player must reject the session while old native cleanup is unconfirmed.");
        }
        finally
        {
            // Assertion failures must not leave the accepted playback task running.
            stop ??= Task.Run(player.Stop);
            try { await Task.WhenAll(stop, playback); }
            catch (Exception exception) when (exception is AggregateException or OperationCanceledException) { }
            // The fixture owns final native session release; the failed player retains its sources.
        }
    }

    [TestMethod]
    public void PreparationFailureOnLastActiveVoiceKeepsPrimaryFailureAndReleasesEveryCreatedPlayer()
    {
        using var directory = new TemporaryDirectory();
        string chartPath = directory.File("prepare-failure.bms");
        WriteChart(chartPath,
            "#WAV01 first.wav\n"
            + "#WAV02 second.wav\n"
            + "#00011:01\n"
            + "#00012:02\n");
        var bms = new BMSFile(chartPath);
        var schedule = BmsAudioFrameSchedule.Create(bms, 48000);
        var layout = new AudioChannelLayout([AudioSpeakerPosition.FrontLeft, AudioSpeakerPosition.FrontRight]);
        var first = new BmsAudioResource(
            "first.wav",
            new DecodedAudio(48000, layout, BuildConstantSamples(48000, 0.125f)),
            1f);
        var second = new BmsAudioResource(
            "second.wav",
            new DecodedAudio(48000, layout, BuildConstantSamples(48000, 0.25f)),
            1f);
        BmsAudioResource?[] resources = [null, first, second];
        int baselineOwnedHandles = ownedSession!.AdditionalStreamHandles.Count;
        var native = new FaultInjectingScheduledNativeBoundary(
            failResumeAt: 0,
            failNextUnlock: false,
            failSetPositionAt: 2);

        BmsScheduledAudioException failure = Assert.ThrowsException<BmsScheduledAudioException>(() =>
            new BmsRealtimeAudioScheduler(
                schedule,
                resources,
                ownedSession,
                native,
                TimeSpan.FromSeconds(2),
                segmentStartSongFrame: 100,
                playbackRate: 1f,
                generation: 31));

        Assert.AreEqual("prepare source voice", failure.Stage);
        Assert.AreEqual(2, failure.Event.WavIndex);
        Assert.IsTrue(
            failure.InnerException is BassAudioPlaybackException
            || (failure.InnerException is AggregateException aggregate
                && aggregate.Flatten().InnerExceptions.Any(exception => exception is BassAudioPlaybackException)));
        Assert.AreEqual(baselineOwnedHandles, ownedSession.AdditionalStreamHandles.Count);
    }

    [TestMethod]
    public void MissedDeadlineFailureUsesObservedSongFrameWithoutReadingMixerAgain()
    {
        using var directory = new TemporaryDirectory();
        string chartPath = directory.File("missed-deadline.bms");
        WriteChart(chartPath, "#WAV01 missing.wav\n#00011:01\n");
        var schedule = BmsAudioFrameSchedule.Create(new BMSFile(chartPath), 48000);
        var layout = new AudioChannelLayout([AudioSpeakerPosition.FrontLeft, AudioSpeakerPosition.FrontRight]);
        BmsAudioResource?[] resources =
        [
            null,
            new BmsAudioResource(
                "missing.wav",
                new DecodedAudio(48000, layout, BuildConstantSamples(48000, 0.125f)),
                1f)
        ];
        var native = new FaultInjectingScheduledNativeBoundary(
            failResumeAt: 0,
            failNextUnlock: false,
            failGetPositionAt: 2);

        using var mixer = new BmsScheduledAudioMixer(
            schedule,
            resources,
            ownedSession!,
            native,
            originMixerFrame: 500);
        BmsScheduledAudioException failure = Assert.ThrowsException<BmsScheduledAudioException>(() =>
            mixer.ReserveBeforeFrame(exclusiveSongFrame: 1, currentSongFrame: 1));

        Assert.AreEqual("missed look-ahead deadline", failure.Stage);
        Assert.AreEqual(500L, failure.ExpectedMixerFrame);
        Assert.AreEqual(501L, failure.CurrentMixerFrame);
        Assert.AreEqual(1, native.GetPositionCalls,
            "The failure diagnostic must use the already observed song frame, without another native read.");
        Assert.IsTrue(ownedSession!.HasCallbackOutputFailure,
            "The missed reservation deadline must still fault callback output.");
        Assert.AreEqual(0, native.StreamFreeCalls,
            "A voice that missed its deadline must not be created or released as an immediate fallback.");
    }

    [TestMethod]
    public void CommitPositionFailureKeepsNativeCauseAndDoesNotReadPositionAgainForDiagnostics()
    {
        using var directory = new TemporaryDirectory();
        string chartPath = directory.File("position-failure.bms");
        WriteChart(chartPath,
            "#WAV01 first.wav\n"
            + "#WAV02 second.wav\n"
            + "#00011:01\n"
            + "#00012:02\n");
        var schedule = BmsAudioFrameSchedule.Create(new BMSFile(chartPath), 48000);
        var layout = new AudioChannelLayout([AudioSpeakerPosition.FrontLeft, AudioSpeakerPosition.FrontRight]);
        BmsAudioResource?[] resources =
        [
            null,
            new BmsAudioResource(
                "first.wav",
                new DecodedAudio(48000, layout, BuildConstantSamples(48000, 0.125f)),
                1f),
            new BmsAudioResource(
                "second.wav",
                new DecodedAudio(48000, layout, BuildConstantSamples(48000, 0.25f)),
                1f)
        ];
        int baselineOwnedHandles = ownedSession!.AdditionalStreamHandles.Count;
        var native = new FaultInjectingScheduledNativeBoundary(
            failResumeAt: 0,
            failNextUnlock: false,
            failGetPositionAt: 2,
            failNextGetPositionAt: 3);

        using var mixer = new BmsScheduledAudioMixer(
            schedule,
            resources,
            ownedSession,
            native,
            originMixerFrame: 500,
            generation: 41);
        BmsScheduledAudioException failure = Assert.ThrowsException<BmsScheduledAudioException>(() =>
            mixer.ReserveSegmentBeforeFrame(segmentStartSongFrame: 100, exclusiveSongFrame: 200));
        mixer.Dispose();

        Assert.AreEqual("atomic mixer commit", failure.Stage);
        Assert.AreEqual(100L, failure.SegmentStartSongFrame);
        Assert.AreEqual(600L, failure.SegmentStartMixerFrame);
        Assert.AreEqual(500L, failure.OriginMixerFrame);
        Assert.IsNull(failure.CurrentMixerFrame,
            "A failed position observation must remain explicitly unknown in diagnostics.");
        Assert.IsTrue(failure.InnerException is BassAudioPlaybackException playbackFailure
            && playbackFailure.NativeErrorCode == Errors.Device,
            "The error captured immediately after the failed native position query must remain primary.");
        Assert.AreEqual(2, native.GetPositionCalls,
            "Only initialization and the failed commit observation should read the mixer position.");
        Assert.AreEqual(2, native.StreamFreeCalls,
            "Both prepared voices must be released after the failed position query.");
        Assert.AreEqual(baselineOwnedHandles, ownedSession.AdditionalStreamHandles.Count);
    }

    [TestMethod]
    public void RollbackFailureIsAddedAfterPrimaryCommitFailureAndAllPreparedSourcesAreReleased()
    {
        using var directory = new TemporaryDirectory();
        string chartPath = directory.File("rollback-failure.bms");
        WriteChart(chartPath,
            "#WAV01 first.wav\n"
            + "#WAV02 second.wav\n"
            + "#00011:01\n"
            + "#00012:02\n");
        var schedule = BmsAudioFrameSchedule.Create(new BMSFile(chartPath), 48000);
        var layout = new AudioChannelLayout([AudioSpeakerPosition.FrontLeft, AudioSpeakerPosition.FrontRight]);
        BmsAudioResource?[] resources =
        [
            null,
            new BmsAudioResource(
                "first.wav",
                new DecodedAudio(48000, layout, BuildConstantSamples(48000, 0.125f)),
                1f),
            new BmsAudioResource(
                "second.wav",
                new DecodedAudio(48000, layout, BuildConstantSamples(48000, 0.25f)),
                1f)
        ];
        int baselineOwnedHandles = ownedSession!.AdditionalStreamHandles.Count;
        var native = new FaultInjectingScheduledNativeBoundary(
            failResumeAt: 1,
            failNextUnlock: false,
            failRemoveChannelAt: 1);

        using var mixer = new BmsScheduledAudioMixer(
            schedule,
            resources,
            ownedSession,
            native,
            originMixerFrame: 500,
            generation: 42);
        BmsScheduledAudioException failure = Assert.ThrowsException<BmsScheduledAudioException>(() =>
            mixer.ReserveSegmentBeforeFrame(segmentStartSongFrame: 100, exclusiveSongFrame: 200));
        mixer.Dispose();

        Assert.AreEqual("atomic mixer commit", failure.Stage);
        AggregateException aggregate = failure.InnerException as AggregateException
            ?? throw new AssertFailedException("The primary and rollback failures were not aggregated.");
        IReadOnlyList<Exception> causes = aggregate
            .Flatten()
            .InnerExceptions;
        string causeDetails = string.Join(
            " | ",
            causes.Select(exception => exception.GetType().Name + ": " + exception.Message));
        Assert.IsTrue(causes.Any(exception => exception is BassAudioPlaybackException playbackFailure
            && playbackFailure.NativeErrorCode == Errors.Device
            && playbackFailure.Message.Contains("resume", StringComparison.OrdinalIgnoreCase)),
            "The original resume failure must remain in the aggregate.");
        Assert.IsTrue(causes.Any(exception => exception is BassAudioPlaybackException playbackFailure
            && playbackFailure.NativeErrorCode == Errors.Device
            && playbackFailure.Message.Contains("remov", StringComparison.OrdinalIgnoreCase)),
            "The rollback failure must be retained as a secondary cause. " + causeDetails);
        Assert.IsTrue(native.RemoveChannelCalls >= 2,
            "Cleanup must retry detaching the retained voice after the injected rollback failure.");
        Assert.AreEqual(2, native.StreamFreeCalls,
            "The retained and uncommitted voices must both reach source release.");
        Assert.AreEqual(baselineOwnedHandles, ownedSession.AdditionalStreamHandles.Count);
    }

    [TestMethod]
    public void SessionMismatchAfterPreparingVoicesReleasesEveryUncommittedSourceWithoutPositionRead()
    {
        using var directory = new TemporaryDirectory();
        string chartPath = directory.File("session-mismatch.bms");
        WriteChart(chartPath,
            "#WAV01 first.wav\n"
            + "#WAV02 second.wav\n"
            + "#00011:01\n"
            + "#00012:02\n");
        var schedule = BmsAudioFrameSchedule.Create(new BMSFile(chartPath), 48000);
        var layout = new AudioChannelLayout([AudioSpeakerPosition.FrontLeft, AudioSpeakerPosition.FrontRight]);
        BmsAudioResource?[] resources =
        [
            null,
            new BmsAudioResource(
                "first.wav",
                new DecodedAudio(48000, layout, BuildConstantSamples(48000, 0.125f)),
                1f),
            new BmsAudioResource(
                "second.wav",
                new DecodedAudio(48000, layout, BuildConstantSamples(48000, 0.25f)),
                1f)
        ];
        BassAudioSession session = ownedSession!;
        int baselineOwnedHandles = session.AdditionalStreamHandles.Count;
        var native = new FaultInjectingScheduledNativeBoundary(
            failResumeAt: 0,
            failNextUnlock: false,
            failGetPositionAt: 2,
            afterSetPosition: call =>
            {
                if (call == 2)
                {
                    session.State = BassAudioSessionState.CleanupPending;
                }
            });

        BmsScheduledAudioException failure;
        using (var mixer = new BmsScheduledAudioMixer(
                   schedule,
                   resources,
                   session,
                   native,
                   originMixerFrame: 500,
                   generation: 43))
        {
            try
            {
                failure = Assert.ThrowsException<BmsScheduledAudioException>(() =>
                    mixer.ReserveSegmentBeforeFrame(segmentStartSongFrame: 100, exclusiveSongFrame: 200));
            }
            finally
            {
                session.State = BassAudioSessionState.Active;
            }
        }

        Assert.AreEqual("session changed before commit", failure.Stage);
        Assert.AreEqual(100L, failure.SegmentStartSongFrame);
        Assert.AreEqual(600L, failure.SegmentStartMixerFrame);
        Assert.AreEqual(500L, failure.OriginMixerFrame);
        Assert.IsNull(failure.CurrentMixerFrame);
        Assert.IsInstanceOfType(failure.InnerException, typeof(InvalidOperationException));
        Assert.AreEqual(1, native.GetPositionCalls,
            "The session mismatch path must not query native position before cleanup.");
        Assert.AreEqual(2, native.StreamFreeCalls,
            "Both voices prepared before the session transition must be released.");
        Assert.AreEqual(baselineOwnedHandles, session.AdditionalStreamHandles.Count);
    }

    [TestMethod]
    public void MatrixPreparationFailureReleasesThePlayerCreatedForTheFailingVoice()
    {
        using var directory = new TemporaryDirectory();
        string chartPath = directory.File("matrix-failure.bms");
        WriteChart(chartPath, "#WAV01 unsupported.wav\n#00011:01\n");
        var schedule = BmsAudioFrameSchedule.Create(new BMSFile(chartPath), 48000);
        var unsupportedLayout = new AudioChannelLayout([AudioSpeakerPosition.FrontLeftOfCenter]);
        float[] monoSamples = new float[48000];
        Array.Fill(monoSamples, 0.125f);
        var resource = new BmsAudioResource(
            "unsupported.wav",
            new DecodedAudio(48000, unsupportedLayout, monoSamples),
            1f);
        BmsAudioResource?[] resources = [null, resource];
        int baselineOwnedHandles = ownedSession!.AdditionalStreamHandles.Count;

        BmsScheduledAudioException failure = Assert.ThrowsException<BmsScheduledAudioException>(() =>
            new BmsRealtimeAudioScheduler(
                schedule,
                resources,
                ownedSession,
                new BassMixerSourceNativeBoundary(),
                TimeSpan.FromSeconds(2),
                segmentStartSongFrame: 0,
                playbackRate: 1f,
                generation: 32));

        Assert.AreEqual("prepare source voice", failure.Stage);
        Assert.AreEqual(1, failure.Event.WavIndex);
        Assert.AreEqual(baselineOwnedHandles, ownedSession.AdditionalStreamHandles.Count);
    }

    [TestMethod]
    public void ReservationUnlockFailureFaultsOutputAndRetainsNoUnownedVoice()
    {
        using var directory = new TemporaryDirectory();
        File.WriteAllBytes(directory.File("first.wav"), BuildFloatWave(48000, BuildConstantSamples(48000, 0.125f)));
        File.WriteAllBytes(directory.File("second.wav"), BuildFloatWave(48000, BuildConstantSamples(48000, 0.25f)));
        string chartPath = directory.File("atomic-chart.bms");
        WriteChart(chartPath,
            "#WAV01 first.wav\n"
            + "#WAV02 second.wav\n"
            + "#00011:01\n"
            + "#00012:02\n");
        var player = new BMSAutoPlayer(new BMSFile(chartPath));
        player.LoadResources();
        BassAudioSession session = player.ResourceSession;
        int ownedStreamCountBefore = session.AdditionalStreamHandles.Count;
        var boundary = new FaultInjectingScheduledNativeBoundary(failResumeAt: 0, failNextUnlock: true);
        session.PublishCallbackOutputHandle(session.MixerHandle);
        session.CallbackPcmRenderer = new AudioPcmRenderer(session.MixerHandle, 48000, 2);
        session.OutputProcessor = new AudioOutputProcessor(48000, 1d);
        session.SetCallbackOutputPaused(false);
        const int callbackFrames = 32;
        int callbackBytes = callbackFrames * 2 * sizeof(float);
        IntPtr buffer = Marshal.AllocHGlobal(callbackBytes);

        try
        {
            Assert.ThrowsException<BmsScheduledAudioException>(() =>
                new BmsRealtimeAudioScheduler(
                    player.AudioSchedule,
                    player.AudioResourcesByIndex,
                    session,
                    boundary,
                    player.Duration,
                    segmentStartSongFrame: 0,
                    playbackRate: 1f,
                    generation: 10));

            Assert.IsTrue(session.HasCallbackOutputFailure);
            Assert.AreEqual(ownedStreamCountBefore, session.AdditionalStreamHandles.Count);
            for (int offset = 0; offset < callbackBytes; offset += sizeof(int))
            {
                Marshal.WriteInt32(buffer, offset, unchecked((int)0x7fc00000));
            }
            Assert.AreEqual(callbackBytes, BassAudioPlayer.ReadPublishedCallbackOutput(session, buffer, callbackBytes));
            float[] callbackPcm = new float[callbackFrames * 2];
            Marshal.Copy(buffer, callbackPcm, 0, callbackPcm.Length);
            CollectionAssert.AreEqual(new float[callbackPcm.Length], callbackPcm);
        }
        finally
        {
            Marshal.FreeHGlobal(buffer);
            player.DisposeAudioSourcesAfterUse();
        }
    }

    private static long GetMixerFrame(BassAudioSession session, int channelCount)
    {
        using BassAudioOperationLease operation = BassAudioRuntime.EnterAudioOperation();
        long positionBytes = Bass.ChannelGetPosition(session.MixerHandle, PositionFlags.Bytes);
        Assert.IsTrue(positionBytes >= 0L);
        long bytesPerFrame = checked((long)channelCount * sizeof(float));
        Assert.AreEqual(0L, positionBytes % bytesPerFrame);
        return positionBytes / bytesPerFrame;
    }

    private static float[] BuildSourceSamples()
    {
        const int frameCount = 24;
        float[] samples = new float[frameCount * 2];
        for (int frame = 0; frame < frameCount; frame++)
        {
            samples[frame * 2] = (frame + 1) / 64f;
            samples[frame * 2 + 1] = -(frame + 1) / 128f;
        }
        return samples;
    }

    private static float[] BuildRampSamples(int frameCount, float scale)
    {
        float[] samples = new float[checked(frameCount * 2)];
        for (int frame = 0; frame < frameCount; frame++)
        {
            samples[frame * 2] = frame * scale;
            samples[frame * 2 + 1] = -frame * scale;
        }
        return samples;
    }

    private static float[] BuildConstantSamples(int frameCount, float value)
    {
        float[] samples = new float[checked(frameCount * 2)];
        for (int frame = 0; frame < frameCount; frame++)
        {
            samples[frame * 2] = value;
            samples[frame * 2 + 1] = -value;
        }
        return samples;
    }

    private static float[] BuildTimingPatternSamples(int frameCount)
    {
        float[] samples = new float[checked(frameCount * 2)];
        for (int frame = 0; frame < frameCount; frame++)
        {
            samples[frame * 2] = ((frame * 17 % 101) - 50) / 8192f;
            samples[frame * 2 + 1] = ((frame * 29 % 113) - 56) / 16384f;
        }
        return samples;
    }

    private static float GetIndependentTimingSample(float[] sourceSamples, int outputFrame, int channel)
    {
        // Fixed fixture events: index 1 starts at 0 and 120000; index 2 aliases the same PCM
        // and starts at 0, 48000, 132000. Each starts until the next event for its own index
        // or the 144000-frame source end, whichever comes first.
        ReadOnlySpan<int> indexOneStarts = [0, 120000];
        ReadOnlySpan<int> indexTwoStarts = [0, 48000, 132000];
        float sample = 0f;
        for (int eventIndex = 0; eventIndex < indexOneStarts.Length; eventIndex++)
        {
            int start = indexOneStarts[eventIndex];
            int end = eventIndex + 1 < indexOneStarts.Length
                ? Math.Min(start + sourceSamples.Length / 2, indexOneStarts[eventIndex + 1])
                : start + sourceSamples.Length / 2;
            if (start <= outputFrame && outputFrame < end)
            {
                sample += sourceSamples[(outputFrame - start) * 2 + channel];
            }
        }
        for (int eventIndex = 0; eventIndex < indexTwoStarts.Length; eventIndex++)
        {
            int start = indexTwoStarts[eventIndex];
            int end = eventIndex + 1 < indexTwoStarts.Length
                ? Math.Min(start + sourceSamples.Length / 2, indexTwoStarts[eventIndex + 1])
                : start + sourceSamples.Length / 2;
            if (start <= outputFrame && outputFrame < end)
            {
                sample += sourceSamples[(outputFrame - start) * 2 + channel];
            }
        }
        return sample;
    }

    private static async Task<PlaybackStateSnapshot> AwaitTickOrPlaybackCompletion(
        Task<PlaybackStateSnapshot> stateApplication,
        Task playback)
    {
        if (!stateApplication.IsCompleted)
        {
            await Task.WhenAny(stateApplication, playback);
        }
        if (stateApplication.IsCompleted)
        {
            return await stateApplication;
        }

        await playback;
        throw new AssertFailedException("再生Taskが次のtick通知より先に完了しました。");
    }

    private static async Task<PlaybackStateSnapshot> AwaitStateApplicationOrPlaybackCompletion(
        Task<PlaybackStateSnapshot> stateApplication,
        Task playback)
    {
        if (!stateApplication.IsCompleted)
        {
            await Task.WhenAny(stateApplication, playback);
        }
        if (stateApplication.IsCompleted)
        {
            return await stateApplication;
        }

        await playback;
        throw new AssertFailedException("再生Taskが譜面状態の反映通知より先に完了しました。");
    }

    private static async Task AssertControlOperationBlocked(Task<long> operationCompleted)
    {
        Task observation = await Task.WhenAny(operationCompleted, Task.Delay(TimeSpan.FromMilliseconds(100)));
        Assert.AreNotSame(
            operationCompleted,
            observation,
            "tickの時計取得後から譜面状態反映まで、制御操作は完了してはいけません。");
    }

    private sealed class TickObservedBmsAutoPlayer(Ribbit.BMS.BMSFile bms) : BMSAutoPlayer(bms)
    {
        public CancellationToken PlaybackCancellationToken =>
            taskTokenSource?.Token ?? throw new InvalidOperationException("Playback has not started.");

        private TaskCompletionSource<PlaybackStateSnapshot>? nextStateApplication;
        private TaskCompletionSource<PlaybackStateSnapshot>? nextCompletedStateApplication;
        private SeekStateObservation? nextSeekStateApplication;
        private PlaybackTickGate? nextTickGate;
        private ControlOperationObservation? nextPauseOperation;
        private ControlOperationObservation? nextSeekOperation;
        private ControlOperationObservation? nextRateOperation;
        private PlaybackCleanupGate? nextPlaybackCleanupGate;
        private TimeSpan lastTickTime;
        private bool lastTickCompleted;
        private long stateSequence;

        public Task<PlaybackStateSnapshot> ArmNextStateApplication()
        {
            var source = new TaskCompletionSource<PlaybackStateSnapshot>(TaskCreationOptions.RunContinuationsAsynchronously);
            Task<PlaybackStateSnapshot> task = source.Task;
            _ = Interlocked.Exchange(ref nextStateApplication, source);
            return task;
        }

        public Task<PlaybackStateSnapshot> ArmNextCompletedStateApplication()
        {
            var source = new TaskCompletionSource<PlaybackStateSnapshot>(TaskCreationOptions.RunContinuationsAsynchronously);
            Task<PlaybackStateSnapshot> task = source.Task;
            _ = Interlocked.Exchange(ref nextCompletedStateApplication, source);
            return task;
        }

        public Task<PlaybackStateSnapshot> ArmSeekStateApplication(TimeSpan time)
        {
            var source = new TaskCompletionSource<PlaybackStateSnapshot>(TaskCreationOptions.RunContinuationsAsynchronously);
            _ = Interlocked.Exchange(ref nextSeekStateApplication, new SeekStateObservation(time, source));
            return source.Task;
        }

        public PlaybackTickGate BlockNextTickBeforeStateApplication()
        {
            var gate = new PlaybackTickGate();
            _ = Interlocked.Exchange(ref nextTickGate, gate);
            return gate;
        }

        public ControlOperationObservation ArmPauseOperation()
        {
            var observation = new ControlOperationObservation();
            _ = Interlocked.Exchange(ref nextPauseOperation, observation);
            return observation;
        }

        public ControlOperationObservation ArmSeekOperation()
        {
            var observation = new ControlOperationObservation();
            _ = Interlocked.Exchange(ref nextSeekOperation, observation);
            return observation;
        }

        public ControlOperationObservation ArmPlaybackRateOperation()
        {
            var observation = new ControlOperationObservation();
            _ = Interlocked.Exchange(ref nextRateOperation, observation);
            return observation;
        }

        public PlaybackCleanupGate BlockNextPlaybackCleanup()
        {
            var gate = new PlaybackCleanupGate();
            _ = Interlocked.Exchange(ref nextPlaybackCleanupGate, gate);
            return gate;
        }

        public override void Pause()
        {
            ControlOperationObservation? observation = Interlocked.Exchange(ref nextPauseOperation, null);
            observation?.NotifyEntered();
            base.Pause();
            observation?.NotifyCompleted(Interlocked.Increment(ref stateSequence));
        }

        protected override void MoveTo(TimeSpan time)
        {
            ControlOperationObservation? observation = Interlocked.Exchange(ref nextSeekOperation, null);
            observation?.NotifyEntered();
            base.MoveTo(time);
            observation?.NotifyCompleted(Interlocked.Increment(ref stateSequence));
        }

        public override float PlaybackRate
        {
            get => base.PlaybackRate;
            set
            {
                ControlOperationObservation? observation = Interlocked.Exchange(ref nextRateOperation, null);
                observation?.NotifyEntered();
                base.PlaybackRate = value;
                observation?.NotifyCompleted(Interlocked.Increment(ref stateSequence));
            }
        }

        protected override void OnPlaybackTick(TimeSpan playbackTime)
        {
            base.OnPlaybackTick(playbackTime);
            lastTickTime = playbackTime;
            lastTickCompleted = playbackTime >= Duration;
            Interlocked.Exchange(ref nextTickGate, null)?.WaitForRelease(playbackTime);
        }

        protected override void ForwardTo(TimeSpan time)
        {
            // seekが反映した状態は、次のtickが密度の集計窓を進める前に読む。
            // 再生側の既存lock内の境界を観測し、制御操作をfixtureのlockで直列化しない。
            SeekStateObservation? seek = Volatile.Read(ref nextSeekStateApplication);
            if (seek != null && CurrentTime == seek.Time)
            {
                Interlocked.Exchange(ref nextSeekStateApplication, null)?.Completion.TrySetResult(CaptureState());
            }
            base.ForwardTo(time);
            PlaybackStateSnapshot state = CaptureState();
            Interlocked.Exchange(ref nextStateApplication, null)?.TrySetResult(state);
            if (state.TickCompleted)
            {
                Interlocked.Exchange(ref nextCompletedStateApplication, null)?.TrySetResult(state);
            }
        }

        private PlaybackStateSnapshot CaptureState() =>
            new(
                lastTickTime,
                lastTickCompleted,
                CurrentTime,
                CurrentBpm,
                Combo,
                NoteDensity,
                PlayState,
                RealtimeScheduler?.CurrentSongFrame ?? 0,
                Interlocked.Increment(ref stateSequence));

        protected override void OnPlaybackStopping()
        {
            base.OnPlaybackStopping();
            Interlocked.Exchange(ref nextPlaybackCleanupGate, null)?.WaitForRelease();
        }
    }

    private readonly record struct PlaybackStateSnapshot(
        TimeSpan TickTime,
        bool TickCompleted,
        TimeSpan CurrentTime,
        double CurrentBpm,
        int Combo,
        double NoteDensity,
        PlayState PlayState,
        long SongFrame,
        long Sequence);

    private sealed record SeekStateObservation(
        TimeSpan Time,
        TaskCompletionSource<PlaybackStateSnapshot> Completion);

    private sealed class ControlOperationObservation
    {
        private readonly TaskCompletionSource entered =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly TaskCompletionSource<long> completed =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        internal Task Entered => entered.Task;

        internal Task<long> Completed => completed.Task;

        internal void NotifyEntered() => entered.TrySetResult();

        internal void NotifyCompleted(long sequence) => completed.TrySetResult(sequence);
    }

    private sealed class PlaybackTickGate
    {
        private readonly TaskCompletionSource<TimeSpan> entered =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly ManualResetEventSlim release = new(initialState: false);

        internal Task<TimeSpan> Entered => entered.Task;

        internal void WaitForRelease(TimeSpan time)
        {
            entered.TrySetResult(time);
            release.Wait();
        }

        internal void Release() => release.Set();

        internal void Dispose() => release.Dispose();
    }

    private sealed class PlaybackCleanupGate : IDisposable
    {
        private readonly TaskCompletionSource entered =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly ManualResetEventSlim release = new(initialState: false);

        internal Task Entered => entered.Task;

        internal void WaitForRelease()
        {
            entered.TrySetResult();
            release.Wait();
        }

        internal void Release() => release.Set();

        public void Dispose() => release.Dispose();
    }

    private sealed class QueuedPlaybackTaskScheduler : TaskScheduler, IDisposable
    {
        private readonly ConcurrentQueue<Task> tasks = new();

        internal int PendingCount => tasks.Count;

        protected override IEnumerable<Task>? GetScheduledTasks() => tasks.ToArray();

        protected override void QueueTask(Task task) => tasks.Enqueue(task);

        protected override bool TryExecuteTaskInline(Task task, bool taskWasPreviouslyQueued) => false;

        internal void ExecuteNext()
        {
            if (!tasks.TryDequeue(out Task? task))
            {
                throw new InvalidOperationException("制御schedulerに実行待ちTaskがありません。");
            }
            _ = TryExecuteTask(task);
        }

        internal void ExecuteAll()
        {
            while (tasks.TryDequeue(out Task? task))
            {
                _ = TryExecuteTask(task);
            }
        }

        public void Dispose() => ExecuteAll();
    }

    private sealed class BlockingPcmNative(IAudioPcmNative inner) : IAudioPcmNative
    {
        private int hasBlocked;

        internal ManualResetEventSlim PullCompleted { get; } = new(initialState: false);

        internal ManualResetEventSlim AllowCallbackReturn { get; } = new(initialState: false);

        public int ChannelGetData(int channel, float[] buffer, int lengthBytes)
        {
            int result = inner.ChannelGetData(channel, buffer, lengthBytes);
            if (Interlocked.Exchange(ref hasBlocked, 1) == 0)
            {
                PullCompleted.Set();
                AllowCallbackReturn.Wait();
            }
            return result;
        }

        public Errors LastError => inner.LastError;
    }

    private sealed class RecordingScheduledNativeBoundary : IBassScheduledMixerNativeBoundary
    {
        private readonly object sync = new();
        private readonly BassMixerSourceNativeBoundary inner = new();
        private readonly Dictionary<int, (long StartBytes, bool Resumed)> reservations = new();

        internal ScheduledVoiceReservation[] SnapshotReservations()
        {
            lock (sync)
            {
                return reservations.Values
                    .Select(reservation => new ScheduledVoiceReservation(reservation.StartBytes, reservation.Resumed))
                    .ToArray();
            }
        }

        public int GetMixer(int sourceHandle) => inner.GetMixer(sourceHandle);

        public bool AddChannel(int mixerHandle, int sourceHandle, BassFlags flags) =>
            inner.AddChannel(mixerHandle, sourceHandle, flags);

        public bool AddChannelAt(int mixerHandle, int sourceHandle, BassFlags flags, long startBytes, long lengthBytes)
        {
            bool added = inner.AddChannelAt(mixerHandle, sourceHandle, flags, startBytes, lengthBytes);
            if (added)
            {
                lock (sync)
                {
                    reservations[sourceHandle] = (startBytes, false);
                }
            }
            return added;
        }

        public bool LockChannel(int mixerHandle, bool locked) => inner.LockChannel(mixerHandle, locked);

        public long GetPosition(int mixerHandle, PositionFlags mode) => inner.GetPosition(mixerHandle, mode);

        public BassFlags SetMixerChannelFlags(int sourceHandle, BassFlags flags, BassFlags mask)
        {
            BassFlags result = inner.SetMixerChannelFlags(sourceHandle, flags, mask);
            if (result != unchecked((BassFlags)(-1))
                && flags == BassFlags.Default
                && mask == BassFlags.MixerChanPause)
            {
                lock (sync)
                {
                    if (reservations.TryGetValue(sourceHandle, out (long StartBytes, bool Resumed) reservation))
                    {
                        reservations[sourceHandle] = (reservation.StartBytes, true);
                    }
                }
            }
            return result;
        }

        public BassMixerChannelInfo GetChannelInfo(int channelHandle) => inner.GetChannelInfo(channelHandle);

        public bool SetSampleRateConversion(int sourceHandle, float quality) =>
            inner.SetSampleRateConversion(sourceHandle, quality);

        public bool GetSampleRateConversion(int sourceHandle, out float quality) =>
            inner.GetSampleRateConversion(sourceHandle, out quality);

        public bool SetMatrix(int sourceHandle, float[,] matrix) => inner.SetMatrix(sourceHandle, matrix);

        public bool RemoveChannel(int sourceHandle) => inner.RemoveChannel(sourceHandle);

        public bool FreeStream(int sourceHandle) => inner.FreeStream(sourceHandle);

        public bool SetPosition(int sourceHandle, long position, PositionFlags mode) =>
            inner.SetPosition(sourceHandle, position, mode);

        public Errors GetError() => inner.GetError();
    }

    private readonly record struct ScheduledVoiceReservation(long StartBytes, bool Resumed);

    private sealed class GatedStoppingBmsAutoPlayer(
        Ribbit.BMS.BMSFile bms,
        IBassScheduledMixerNativeBoundary native,
        TaskCompletionSource stopping,
        ManualResetEventSlim releaseStopping) : BMSAutoPlayer(bms, native)
    {
        protected override void OnPlaybackStopping()
        {
            stopping.TrySetResult();
            releaseStopping.Wait();
            base.OnPlaybackStopping();
        }
    }

    private sealed class RealtimeTestPlayerSettingsGateway : IPlayerSettingsGateway
    {
        public PlayerSettingsSnapshot CaptureSnapshot() => new(
            AudioDriver.NullDevice,
            string.Empty,
            string.Empty,
            SampleRate.AUTO,
            SampleFormat.AUTO,
            0f,
            false,
            50,
            new PlayerResolution(800, 600),
            false,
            default);

        public void UpdateWindowPlacement(WindowPlacement windowPlacement)
        {
        }
    }

    private sealed class RealtimeTestPlaybackRuntime : IAudioPlaybackRuntime
    {
        public Task WaitForOutputReadyAsync() => Task.CompletedTask;

        public AudioPlaybackInitializationResult Initialize(PlayerSettingsSnapshot settings) => new(
            settings.PlayerDriver,
            settings.PlayerDevice,
            settings.PlayerDeviceName,
            settings.PlayerSampleRate,
            settings.PlayerFormat,
            settings.PlayerBufferSize,
            settings.PlayerWASAPIParam,
            settings.PlayerVolume,
            AudioDriver.NullDevice,
            string.Empty,
            string.Empty,
            SampleRate.SAMPLE_RATE_48000Hz,
            SampleFormat.SAMPLE_FLOAT_32BIT,
            SampleFormat.UNKNOWN,
            2,
            0d,
            string.Empty,
            isSilentFallback: false);

        public int CurrentVoices => 0;

        public int MaxVoices => 0;

        public void ClearMaxVoices()
        {
        }

        public void SetVolume(int volume)
        {
        }

        public void Free()
        {
        }
    }

    private static IEnumerable<Exception> GetExceptionChain(Exception exception)
    {
        yield return exception;
        if (exception is AggregateException aggregate)
        {
            foreach (Exception inner in aggregate.InnerExceptions)
            {
                foreach (Exception nested in GetExceptionChain(inner))
                {
                    yield return nested;
                }
            }
        }
        else if (exception.InnerException is Exception inner)
        {
            foreach (Exception nested in GetExceptionChain(inner))
            {
                yield return nested;
            }
        }
    }

    internal sealed class FaultInjectingScheduledNativeBoundary(
        int failResumeAt,
        bool failNextUnlock,
        int failSetPositionAt = 0,
        bool failRemoveChannel = false,
        bool failStreamFree = false,
        int failGetPositionAt = 0,
        int failNextGetPositionAt = 0,
        int failRemoveChannelAt = 0,
        Action<int>? afterSetPosition = null) : IBassScheduledMixerNativeBoundary
    {
        private const BassFlags PauseFlag = BassFlags.MixerChanPause;
        private readonly BassMixerSourceNativeBoundary inner = new();
        private int resumeCount;
        private int setPositionCount;
        private int getPositionCalls;
        private int removeChannelCalls;
        private int streamFreeCalls;
        private int failUnlock = failNextUnlock ? 1 : 0;
        private int failNextResume;
        private Errors injectedError = Errors.OK;

        internal int RemoveChannelCalls => Volatile.Read(ref removeChannelCalls);

        internal int StreamFreeCalls => Volatile.Read(ref streamFreeCalls);

        internal int GetPositionCalls => Volatile.Read(ref getPositionCalls);

        internal int SetPositionCalls => Volatile.Read(ref setPositionCount);

        internal void FailNextResume() => Interlocked.Exchange(ref failNextResume, 1);

        public int GetMixer(int sourceHandle) => inner.GetMixer(sourceHandle);

        public bool AddChannel(int mixerHandle, int sourceHandle, BassFlags flags) =>
            inner.AddChannel(mixerHandle, sourceHandle, flags);

        public bool AddChannelAt(int mixerHandle, int sourceHandle, BassFlags flags, long startBytes, long lengthBytes) =>
            inner.AddChannelAt(mixerHandle, sourceHandle, flags, startBytes, lengthBytes);

        public bool LockChannel(int mixerHandle, bool locked)
        {
            bool result = inner.LockChannel(mixerHandle, locked);
            return result
                && (locked || Interlocked.Exchange(ref failUnlock, 0) == 0 || Inject(Errors.Device));
        }

        public long GetPosition(int mixerHandle, PositionFlags mode)
        {
            int call = Interlocked.Increment(ref getPositionCalls);
            if (call == failGetPositionAt || call == failNextGetPositionAt)
            {
                injectedError = Errors.Device;
                return -1;
            }
            return inner.GetPosition(mixerHandle, mode);
        }

        public BassFlags SetMixerChannelFlags(int sourceHandle, BassFlags flags, BassFlags mask)
        {
            if (flags == BassFlags.Default && mask == PauseFlag)
            {
                int call = Interlocked.Increment(ref resumeCount);
                if (call == failResumeAt || Interlocked.Exchange(ref failNextResume, 0) != 0)
                {
                    injectedError = Errors.Device;
                    return unchecked((BassFlags)(-1));
                }
            }
            return inner.SetMixerChannelFlags(sourceHandle, flags, mask);
        }

        public BassMixerChannelInfo GetChannelInfo(int channelHandle) => inner.GetChannelInfo(channelHandle);

        public bool SetSampleRateConversion(int sourceHandle, float quality) =>
            inner.SetSampleRateConversion(sourceHandle, quality);

        public bool GetSampleRateConversion(int sourceHandle, out float quality) =>
            inner.GetSampleRateConversion(sourceHandle, out quality);

        public bool SetMatrix(int sourceHandle, float[,] matrix) => inner.SetMatrix(sourceHandle, matrix);

        public bool RemoveChannel(int sourceHandle)
        {
            int call = Interlocked.Increment(ref removeChannelCalls);
            if (failRemoveChannel || call == failRemoveChannelAt)
            {
                return Inject(Errors.Device);
            }
            return inner.RemoveChannel(sourceHandle);
        }

        public bool FreeStream(int sourceHandle)
        {
            Interlocked.Increment(ref streamFreeCalls);
            if (failStreamFree)
            {
                return Inject(Errors.Device);
            }
            return inner.FreeStream(sourceHandle);
        }

        public bool SetPosition(int sourceHandle, long position, PositionFlags mode)
        {
            int call = Interlocked.Increment(ref setPositionCount);
            if (failSetPositionAt > 0 && call == failSetPositionAt)
            {
                return Inject(Errors.Device);
            }
            bool result = inner.SetPosition(sourceHandle, position, mode);
            if (result)
            {
                afterSetPosition?.Invoke(call);
            }
            return result;
        }

        public Errors GetError()
        {
            Errors error = injectedError;
            injectedError = Errors.OK;
            return error == Errors.OK ? inner.GetError() : error;
        }

        private bool Inject(Errors error)
        {
            injectedError = error;
            return false;
        }
    }

    internal static byte[] BuildFloatWave(int sampleRate, float[] interleavedSamples)
    {
        int dataLength = checked(interleavedSamples.Length * sizeof(float));
        byte[] wave = new byte[checked(44 + dataLength)];
        using var stream = new MemoryStream(wave);
        using var writer = new BinaryWriter(stream, Encoding.ASCII, leaveOpen: true);
        writer.Write(Encoding.ASCII.GetBytes("RIFF"));
        writer.Write(checked(36 + dataLength));
        writer.Write(Encoding.ASCII.GetBytes("WAVEfmt "));
        writer.Write(16);
        writer.Write((ushort)3);
        writer.Write((ushort)2);
        writer.Write(sampleRate);
        writer.Write(checked(sampleRate * 2 * sizeof(float)));
        writer.Write((ushort)(2 * sizeof(float)));
        writer.Write((ushort)(sizeof(float) * 8));
        writer.Write(Encoding.ASCII.GetBytes("data"));
        writer.Write(dataLength);
        foreach (float sample in interleavedSamples)
        {
            writer.Write(sample);
        }
        return wave;
    }

    private static float ReadFloatSample(AudioTestWaveFile wave, int frame, int channel)
    {
        int sampleIndex = checked(frame * wave.Channels + channel);
        int offset = checked(wave.DataOffset + sampleIndex * sizeof(float));
        int bits = BinaryPrimitives.ReadInt32LittleEndian(wave.Bytes.AsSpan(offset, sizeof(float)));
        return BitConverter.Int32BitsToSingle(bits);
    }

    private static void WriteChart(string path, string eventLines) =>
        File.WriteAllText(
            path,
            "#PLAYER 1\n#TITLE realtime frame fixture\n#ARTIST test\n#BPM 120\n"
                + eventLines,
            Encoding.ASCII);

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
            "BeMusicSeeker.BMSRealtime." + Guid.NewGuid().ToString("N"));

        internal TemporaryDirectory()
        {
            Directory.CreateDirectory(path);
        }

        internal string File(string name) => Path.Combine(path, name);

        public void Dispose() => Directory.Delete(path, recursive: true);
    }

}
