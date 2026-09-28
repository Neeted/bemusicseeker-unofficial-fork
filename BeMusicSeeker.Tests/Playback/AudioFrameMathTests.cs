using System;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Ribbit.Media.Audio;

namespace BeMusicSeeker.Tests;

/// <summary>独立に定めた時刻・有限長・source格子の整数変換を検証します。</summary>
[TestClass]
public sealed class AudioFrameMathTests
{
    [TestMethod]
    public void AbsoluteTimeUsesTiesToEvenAtTheEffectiveSampleRate()
    {
        Assert.AreEqual(220L, AudioFrameMath.TimeToFrame(TimeSpan.FromTicks(50000), 44100));
        Assert.AreEqual(662L, AudioFrameMath.TimeToFrame(TimeSpan.FromTicks(150000), 44100));
        Assert.AreEqual(0L, AudioFrameMath.TimeToFrame(TimeSpan.FromTicks(100), 48000));
        Assert.AreEqual(1L, AudioFrameMath.TimeToFrame(TimeSpan.FromTicks(200), 48000));
        Assert.AreEqual(1L, AudioFrameMath.TimeToFrame(TimeSpan.FromTicks(300), 48000));
        Assert.AreEqual(0L, AudioFrameMath.TimeToFrame(TimeSpan.FromTicks(-100), 48000));
        Assert.AreEqual(-1L, AudioFrameMath.TimeToFrame(TimeSpan.FromTicks(-200), 48000));
        Assert.AreEqual(2L, AudioFrameMath.TimeToFrame(TimeSpan.FromTicks(300), 50000));
        Assert.AreEqual(-2L, AudioFrameMath.TimeToFrame(TimeSpan.FromTicks(-300), 50000));
    }

    [TestMethod]
    public void AbsoluteTimeMatchesIndependentFixedFramesAtHighSampleRates()
    {
        Assert.AreEqual(4800L, AudioFrameMath.TimeToFrame(TimeSpan.FromTicks(500000), 96000));
        Assert.AreEqual(9600L, AudioFrameMath.TimeToFrame(TimeSpan.FromTicks(500000), 192000));
    }

    [TestMethod]
    public void FrameTimeConversionUsesIntegerRoundingAndRoundTripsStableFrames()
    {
        Assert.AreEqual(TimeSpan.Zero, AudioFrameMath.FrameToTime(0, 44100));
        Assert.AreEqual(49887L, AudioFrameMath.FrameToTime(220, 44100).Ticks);
        Assert.AreEqual(-49887L, AudioFrameMath.FrameToTime(-220, 44100).Ticks);
        Assert.AreEqual(220L, AudioFrameMath.TimeToFrame(AudioFrameMath.FrameToTime(220, 44100), 44100));
        Assert.AreEqual(662L, AudioFrameMath.TimeToFrame(AudioFrameMath.FrameToTime(662, 44100), 44100));
    }

    [TestMethod]
    public void FiniteSourceLengthRoundsUpWhileSeekPositionRoundsDown()
    {
        Assert.AreEqual(4800L, AudioFrameMath.CeilingOutputFrameCount(4410, 44100, 48000));
        Assert.AreEqual(4802L, AudioFrameMath.CeilingOutputFrameCount(4411, 44100, 48000));
        Assert.AreEqual(4410L, AudioFrameMath.SourceFrameFromMixerFrames(4800, 44100, 48000));
        Assert.AreEqual(0L, AudioFrameMath.SourceFrameFromMixerFrames(0, 44100, 48000));
    }

    [TestMethod]
    public void FloatFrameBytePositionUsesMixerChannelCount()
    {
        Assert.AreEqual(312L, AudioFrameMath.Float32FrameToBytePosition(13, 6));
        Assert.ThrowsException<OverflowException>(
            () => AudioFrameMath.Float32FrameToBytePosition(long.MaxValue, 2));
    }

    [TestMethod]
    public void InvalidRatesNegativeLengthsAndUnrepresentableResultsFailExplicitly()
    {
        Assert.ThrowsException<ArgumentOutOfRangeException>(
            () => AudioFrameMath.TimeToFrame(TimeSpan.Zero, 0));
        Assert.ThrowsException<ArgumentOutOfRangeException>(
            () => AudioFrameMath.FrameToTime(0, -1));
        Assert.ThrowsException<ArgumentOutOfRangeException>(
            () => AudioFrameMath.CeilingOutputFrameCount(-1, 44100, 48000));
        Assert.ThrowsException<ArgumentOutOfRangeException>(
            () => AudioFrameMath.SourceFrameFromMixerFrames(-1, 44100, 48000));
        Assert.ThrowsException<ArgumentOutOfRangeException>(
            () => AudioFrameMath.Float32FrameToBytePosition(0, 0));
        Assert.ThrowsException<OverflowException>(
            () => AudioFrameMath.TimeToFrame(TimeSpan.MaxValue, int.MaxValue));
        Assert.ThrowsException<OverflowException>(
            () => AudioFrameMath.FrameToTime(long.MaxValue, 1));
    }
}
