using System;
using System.Numerics;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Ribbit.BMS;
using Ribbit.Media.Audio;

namespace BeMusicSeeker.Tests;

/// <summary>独立に定めた時刻・有限長・source格子の整数変換を検証します。</summary>
[TestClass]
public sealed class AudioFrameMathTests
{
    /// <summary>
    /// 保証：tick未満の時刻を出力格子・入力格子へ直接丸め、tickに収まる正負の時刻は従来の整数変換と一致させる。
    /// </summary>
    [TestMethod]
    public void SubtickFrameBoundariesAreDirectAndBmsTicksRemainExact()
    {
        BigInteger grid = BigInteger.One << 32;
        var first = new PlaybackTime(625 * grid / 4);
        var third = new PlaybackTime(1875 * grid / 4);
        Assert.AreEqual(0L, first.ToOutputFrame(32000));
        Assert.AreEqual(2L, third.ToOutputFrame(32000));
        Assert.AreEqual(0L, first.ToSourceFrame(32000));
        Assert.AreEqual(1L, third.ToSourceFrame(32000));
        foreach (long ticks in new long[] { -300, 0, 100, 150000, 9999999999 })
        {
            Assert.AreEqual(AudioFrameMath.TimeToFrame(TimeSpan.FromTicks(ticks), 48000), PlaybackTime.FromTimeSpan(TimeSpan.FromTicks(ticks)).ToOutputFrame(48000));
        }
    }

    /// <summary>
    /// 保証：実効サンプルレートで絶対時刻を偶数丸めし、正負の半端フレームを切捨てや一律切上げへ読み替えない。
    /// </summary>
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

    /// <summary>
    /// 保証：高サンプルレートでも独立に定めた時刻のフレーム数へ一致し、通常レートを固定した計算を検出する。
    /// </summary>
    [TestMethod]
    public void AbsoluteTimeMatchesIndependentFixedFramesAtHighSampleRates()
    {
        Assert.AreEqual(4800L, AudioFrameMath.TimeToFrame(TimeSpan.FromTicks(500000), 96000));
        Assert.AreEqual(9600L, AudioFrameMath.TimeToFrame(TimeSpan.FromTicks(500000), 192000));
    }

    /// <summary>
    /// 保証：正負フレームの時刻変換を整数丸めし、表現可能な代表フレームを往復変換で隣のフレームへずらさない。
    /// </summary>
    [TestMethod]
    public void FrameTimeConversionUsesIntegerRoundingAndRoundTripsStableFrames()
    {
        Assert.AreEqual(TimeSpan.Zero, AudioFrameMath.FrameToTime(0, 44100));
        Assert.AreEqual(49887L, AudioFrameMath.FrameToTime(220, 44100).Ticks);
        Assert.AreEqual(-49887L, AudioFrameMath.FrameToTime(-220, 44100).Ticks);
        Assert.AreEqual(220L, AudioFrameMath.TimeToFrame(AudioFrameMath.FrameToTime(220, 44100), 44100));
        Assert.AreEqual(662L, AudioFrameMath.TimeToFrame(AudioFrameMath.FrameToTime(662, 44100), 44100));
    }

    /// <summary>
    /// 保証：有限入力の出力長は不足しないよう切上げ、シーク入力位置は超えないよう切下げて末尾欠落を防ぐ。
    /// </summary>
    [TestMethod]
    public void FiniteSourceLengthRoundsUpWhileSeekPositionRoundsDown()
    {
        Assert.AreEqual(4800L, AudioFrameMath.CeilingOutputFrameCount(4410, 44100, 48000));
        Assert.AreEqual(4802L, AudioFrameMath.CeilingOutputFrameCount(4411, 44100, 48000));
        Assert.AreEqual(4410L, AudioFrameMath.SourceFrameFromMixerFrames(4800, 44100, 48000));
        Assert.AreEqual(0L, AudioFrameMath.SourceFrameFromMixerFrames(0, 44100, 48000));
    }

    /// <summary>
    /// 保証：floatフレーム位置を実際のチャンネル数でバイト位置へ換算し、表現不能な大きさでは桁あふれを失敗として返す。
    /// </summary>
    [TestMethod]
    public void FloatFrameBytePositionUsesMixerChannelCount()
    {
        Assert.AreEqual(312L, AudioFrameMath.Float32FrameToBytePosition(13, 6));
        Assert.ThrowsException<OverflowException>(
            () => AudioFrameMath.Float32FrameToBytePosition(long.MaxValue, 2));
    }

    /// <summary>
    /// 保証：無効レート・負の有限長・不正チャンネル数を拒否し、表現不能な変換は桁あふれを隠さず失敗にする。
    /// </summary>
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
