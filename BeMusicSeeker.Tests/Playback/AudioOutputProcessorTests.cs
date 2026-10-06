using System;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Ribbit.Media.Audio;

namespace BeMusicSeeker.Tests;

/// <summary>master gainの適用値、ramp境界、分割不変性を検証します。</summary>
[TestClass]
public sealed class AudioOutputProcessorTests
{
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

    [TestMethod]
    public void RampLengthUsesTiesToEvenAndAtLeastOneFrame()
    {
        Assert.AreEqual(6, new AudioOutputProcessor(1100, 1d).RampFrameCount);
        Assert.AreEqual(4, new AudioOutputProcessor(900, 1d).RampFrameCount);
        Assert.AreEqual(1, new AudioOutputProcessor(1, 1d).RampFrameCount);
    }

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

    [TestMethod]
    public void ConstantOfflineGainModifiesOnePcmBufferInPlace()
    {
        float[] samples = [1.25f, -0.75f];

        double peak = AudioOutputProcessor.ApplyConstantGain(samples, 0.5d);

        CollectionAssert.AreEqual(new[] { 0.625f, -0.375f }, samples);
        Assert.AreEqual(0.625d, peak, 1e-12d);
    }

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

    [TestMethod]
    public void IntegerOutputRangeFailureRetainsPeakAndRequiredAttenuation()
    {
        var exception = new AudioOutputRangeException(1.25d);

        Assert.AreEqual(1.25d, exception.Peak);
        Assert.AreEqual(20d * System.Math.Log10(1.25d), exception.RequiredAttenuationDb, 1e-12d);
    }
}
