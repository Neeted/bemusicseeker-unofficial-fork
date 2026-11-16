using ManagedBass.Mix;
using Ribbit.Media;
using Ribbit.Media.Audio;
using static BeMusicSeeker.TestSupport.Components.AudioMixerSignalFixture;

namespace BeMusicSeeker.Tests;

/// <summary>本番のfloat source・mixer・SRCを通るnative信号経路を検査します。</summary>
[TestClass]
[DoNotParallelize]
public sealed class AudioMixerSignalTests
{
    // MSTest が各テストの実行前に注入します。
    public TestContext TestContext { get; set; } = null!;

    /// <summary>実float入力の過大・微小・無音サンプルを左右出力へbit単位で維持し、native経路のクリップや精度低下を検出する。</summary>
    /// <remarks>実NULL_DEVICEのsource・mixer・SRC接続を使い、managed代替では見えない出力PCMと資源所有を検査するため共有native状態を直列所有する。</remarks>
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

    /// <summary>ゼロframe音源を正常に所有し、Playとseekを停止状態のまま扱い、mixer voiceを増やさず通常Disposeでdecoderを解放する。</summary>
    /// <remarks>実NULL_DEVICEのsource・mixer・SRC接続を使い、managed代替では見えない出力PCMと資源所有を検査するため共有native状態を直列所有する。</remarks>
    [TestMethod]
    public void NativeMixer_EmptyWaveLoadsAsStoppedSeekableSourceWithoutMixerVoice()
    {
        using var wave = TemporaryFloatWave.Create(48000, 0, _ => 0f);
        using var graph = NativeAudioGraph.Start(48000);
        BassAudioSession session = BassAudioPlayer.ActiveSession
            ?? throw new AssertFailedException("The null-device session was not active.");
        int initialVoices = BassAudioPlayer.CurrentVoices;
        int initialStreams = session.GetPlayerStreams().Count;
        BassAudioPlayer player = graph.CreatePlayer(wave.Path);

        Assert.AreEqual(TimeSpan.Zero, player.Duration);
        Assert.AreEqual(TimeSpan.Zero, player.CurrentTime);
        Assert.AreEqual(0L, player.GetOutputFrameCount(48000));
        Assert.AreEqual(initialStreams + 1, session.GetPlayerStreams().Count,
            "The empty decoder handle remains owned until ordinary player disposal.");

        player.Play();
        Assert.AreEqual(PlayState.Stopped, player.PlayState,
            "Play must leave a zero-frame source stopped immediately.");
        player.CurrentTime = TimeSpan.Zero;
        Assert.ThrowsExactly<ArgumentOutOfRangeException>(() => player.CurrentTime = TimeSpan.FromTicks(1));
        player.Stop();

        Assert.AreEqual(PlayState.Stopped, player.PlayState);
        Assert.AreEqual(initialVoices, BassAudioPlayer.CurrentVoices);
        BassAudioOwnedStream owned = session.GetPlayerStreams().Single(stream =>
            stream.TryGetOwner<BassAudioPlayer>(out BassAudioPlayer owner) && ReferenceEquals(player, owner));
        Assert.AreEqual(0, BassMix.ChannelGetMixer(owned.Handle), "An empty source is never registered with the mixer.");
        foreach (float sample in graph.ReadFrames(32))
        {
            Assert.AreEqual(0f, sample);
        }

        graph.DisposePlayer(player);

        Assert.IsTrue(player.NativeReleaseConfirmed);
        Assert.AreEqual(initialStreams, session.GetPlayerStreams().Count);
        Assert.AreEqual(initialVoices, BassAudioPlayer.CurrentVoices);
    }

    /// <summary>複数sourceをfloatのまま加算してから最終gainを適用し、sourceごとのクリップで相殺結果を壊さない。</summary>
    /// <remarks>実NULL_DEVICEのsource・mixer・SRC接続を使い、managed代替では見えない出力PCMと資源所有を検査するため共有native状態を直列所有する。</remarks>
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

    /// <summary>開始と停止後の再開始の最初のframeを保持し、native ramp-inによる先頭音の減衰を防ぐ。</summary>
    /// <remarks>実NULL_DEVICEのsource・mixer・SRC接続を使い、managed代替では見えない出力PCMと資源所有を検査するため共有native状態を直列所有する。</remarks>
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

    /// <summary>複数の昇降rateと帯域内toneで実SRC振幅の誤差を0.01dB以内に保ち、変換時の信号劣化を検出する。</summary>
    /// <remarks>実NULL_DEVICEのsource・mixer・SRC接続を使い、managed代替では見えない出力PCMと資源所有を検査するため共有native状態を直列所有する。</remarks>
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

    /// <summary>96kHzから48kHzへの変換で帯域外toneのaliasを－90dB以下に抑え、低域への折返し漏れを検出する。</summary>
    /// <remarks>実NULL_DEVICEのsource・mixer・SRC接続を使い、managed代替では見えない出力PCMと資源所有を検査するため共有native状態を直列所有する。</remarks>
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

    /// <summary>全品質・昇降rate・read分割・再演奏で数学的切上げ終端の最終frameを残し、その後にPCMを付け足さない。</summary>
    /// <remarks>実NULL_DEVICEのsource・mixer・SRC接続を使い、managed代替では見えない出力PCMと資源所有を検査するため共有native状態を直列所有する。</remarks>
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
        (double sineAmplitude, double cosineAmplitude) = MeasureProjectedComponents(
            interleavedStereo,
            sampleRate,
            frequency,
            startFrame,
            frameCount);
        return Math.Sqrt(sineAmplitude * sineAmplitude + cosineAmplitude * cosineAmplitude);
    }
}
