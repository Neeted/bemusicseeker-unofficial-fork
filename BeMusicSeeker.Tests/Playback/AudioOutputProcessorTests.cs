using System;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Ribbit.Media.Audio;

namespace BeMusicSeeker.Tests;

/// <summary>master gainの適用値、ramp境界、分割不変性を検証します。</summary>
[TestClass]
public sealed class AudioOutputProcessorTests
{
    /// <summary>呼出側の初期gainを最初のPCMから適用し、途中float値を単位範囲へクリップせずpeakを測る。</summary>
    [TestMethod]
    public void InitialGainIsImmediateAndDoesNotClipIntermediateFloatPcm()
    {
        var processor = new AudioOutputProcessor(48000, 0.5d);
        float[] samples = [1.25f, -1.25f];

        AudioOutputProcessResult result = processor.Process(samples, channelCount: 2);

        CollectionAssert.AreEqual(new[] { 0.625f, -0.625f }, samples);
        Assert.AreEqual(0.625d, result.Peak, 1e-12d);
        Assert.IsFalse(result.ExceededFullScale);
    }

    /// <summary>gain変更を5msの各フレームへ線形適用し、最後のフレームで目標値に到達させて急変と終点ずれを防ぐ。</summary>
    [TestMethod]
    public void RampUsesFiveMillisecondsAndReachesTargetOnTheLastFrame()
    {
        var processor = new AudioOutputProcessor(1000, 0.5d);
        processor.SetTargetGain(1d);
        float[] samples = [1f, 1f, 1f, 1f, 1f];

        processor.Process(samples, channelCount: 1);

        CollectionAssert.AreEqual(new[] { 0.6f, 0.7f, 0.8f, 0.9f, 1f }, samples);
        Assert.AreEqual(1d, processor.CurrentGain);
        Assert.AreEqual(5, processor.RampFrameCount);
    }

    /// <summary>5msに対応する半端フレームを偶数丸めし、極低レートでも一フレーム以上のrampを維持する。</summary>
    [TestMethod]
    public void RampLengthUsesTiesToEvenAndAtLeastOneFrame()
    {
        Assert.AreEqual(6, new AudioOutputProcessor(1100, 1d).RampFrameCount);
        Assert.AreEqual(4, new AudioOutputProcessor(900, 1d).RampFrameCount);
        Assert.AreEqual(1, new AudioOutputProcessor(1, 1d).RampFrameCount);
    }

    /// <summary>同じPCMを一括・複数callbackで処理して同じgain軌跡を保ち、callback分割でrampを再起動しない。</summary>
    [TestMethod]
    public void RampIsIndependentOfHowTheCallbackSplitsItsFrames()
    {
        var wholeProcessor = new AudioOutputProcessor(1000, 1d);
        var splitProcessor = new AudioOutputProcessor(1000, 1d);
        wholeProcessor.SetTargetGain(0d);
        splitProcessor.SetTargetGain(0d);
        float[] whole = [1f, 1f, 1f, 1f, 1f, 1f, 1f, 1f];
        float[] split = [1f, 1f, 1f, 1f, 1f, 1f, 1f, 1f];

        wholeProcessor.Process(whole, channelCount: 1);
        splitProcessor.Process(split.AsSpan(0, 2), channelCount: 1);
        splitProcessor.Process(split.AsSpan(2, 3), channelCount: 1);
        splitProcessor.Process(split.AsSpan(5, 3), channelCount: 1);

        CollectionAssert.AreEqual(whole, split);
    }

    /// <summary>ramp途中で目標を変えた際に最後の適用係数から続け、初期値への跳躍を防ぐ。</summary>
    [TestMethod]
    public void MidRampRetargetStartsFromTheLastAppliedCoefficient()
    {
        var processor = new AudioOutputProcessor(1000, 1d);
        processor.SetTargetGain(0d);
        float[] first = [1f, 1f];
        processor.Process(first, channelCount: 1);
        processor.SetTargetGain(1d);
        float[] next = [1f];

        processor.Process(next, channelCount: 1);

        Assert.AreEqual(0.68f, next[0], 1e-6f);
    }

    /// <summary>全チャンネルへ同じフレームgainを適用し、過大レベルは値を保ったままpeakと超過として報告する。</summary>
    [TestMethod]
    public void EveryChannelUsesTheSameGainAndOverLevelIsReportedWithoutClamping()
    {
        var stereo = new AudioOutputProcessor(1000, 1d);
        stereo.SetTargetGain(0.5d);
        float[] stereoSamples = [1f, 1f, 1f, 1f];
        stereo.Process(stereoSamples, channelCount: 2);
        CollectionAssert.AreEqual(new[] { 0.9f, 0.9f, 0.8f, 0.8f }, stereoSamples);

        var overLevel = new AudioOutputProcessor(48000, 1d);
        float[] high = [1.5f];
        AudioOutputProcessResult result = overLevel.Process(high, channelCount: 1);

        Assert.AreEqual(1.5f, high[0]);
        Assert.AreEqual(1.5d, result.Peak);
        Assert.IsTrue(result.ExceededFullScale);
    }

    /// <summary>オフライン固定gainを元PCMへ直接反映し、全サンプルの最大絶対値を報告する。</summary>
    [TestMethod]
    public void ConstantOfflineGainModifiesOnePcmBufferInPlace()
    {
        float[] samples = [1.25f, -0.75f];

        double peak = AudioOutputProcessor.ApplyConstantGain(samples, 0.5d);

        CollectionAssert.AreEqual(new[] { 0.625f, -0.375f }, samples);
        Assert.AreEqual(0.625d, peak, 1e-12d);
    }

    /// <summary>非有限gain・PCMを拒否し、破損した数値を正常音声として後段へ流さない。</summary>
    [TestMethod]
    public void NonFinitePcmAndGainAreRejected()
    {
        var processor = new AudioOutputProcessor(48000, 1d);
        Assert.ThrowsException<ArgumentOutOfRangeException>(() => processor.SetTargetGain(double.NaN));
        AudioOutputProcessException exception = Assert.ThrowsException<AudioOutputProcessException>(
            () => processor.Process([float.PositiveInfinity], 1));
        Assert.AreEqual(AudioOutputProcessFailure.NonFiniteInput, exception.Failure);
        Assert.ThrowsException<ArgumentOutOfRangeException>(
            () => AudioOutputProcessor.ApplyConstantGain([0f], double.PositiveInfinity));
    }

    /// <summary>callbackの非有限入力・演算後の非有限出力を例外生成せず異なる故障値として返す。</summary>
    [TestMethod]
    public void CallbackGainFailureIsReturnedAsAValue()
    {
        var inputFailureProcessor = new AudioOutputProcessor(48000, 1d);
        Assert.IsFalse(inputFailureProcessor.TryProcess(
            [1f, float.NegativeInfinity],
            1,
            out _,
            out AudioOutputProcessFailure inputFailure));
        Assert.AreEqual(AudioOutputProcessFailure.NonFiniteInput, inputFailure);

        var outputFailureProcessor = new AudioOutputProcessor(48000, 1e308d);
        Assert.IsFalse(outputFailureProcessor.TryProcess(
            [float.MaxValue],
            1,
            out _,
            out AudioOutputProcessFailure outputFailure));
        Assert.AreEqual(AudioOutputProcessFailure.NonFiniteOutput, outputFailure);
    }

    /// <summary>整数出力の過大peakから必要減衰dBを算出し、利用者が修正すべきレベル情報を失わない。</summary>
    [TestMethod]
    public void IntegerOutputRangeFailureRetainsPeakAndRequiredAttenuation()
    {
        var exception = new AudioOutputRangeException(1.25d);

        Assert.AreEqual(1.25d, exception.Peak);
        Assert.AreEqual(20d * System.Math.Log10(1.25d), exception.RequiredAttenuationDb, 1e-12d);
    }
}
