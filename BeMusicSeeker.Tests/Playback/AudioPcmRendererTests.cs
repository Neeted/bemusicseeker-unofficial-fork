using System;
using System.Collections.Generic;
using ManagedBass;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Ribbit.Media.Audio;

namespace BeMusicSeeker.Tests;

/// <summary>float PCM rendererのframe境界、有限終端、測定規則を検証します。</summary>
[TestClass]
public sealed class AudioPcmRendererTests
{
    [TestMethod]
    public void ReadContinuesPositiveShortReadsUntilTheRequestedFramesAreComplete()
    {
        FakeNative native = new();
        native.Results.Enqueue(new ReadResult([0.1f, -0.1f], null, Errors.OK));
        native.Results.Enqueue(new ReadResult([0.2f, -0.2f], null, Errors.OK));
        native.Results.Enqueue(new ReadResult([0.3f, -0.3f], null, Errors.OK));
        var renderer = new AudioPcmRenderer(11, 48000, 2, native);
        float[] output = new float[6];

        renderer.ReadFramesExactly(output, 0, 3);

        CollectionAssert.AreEqual(new[] { 0.1f, -0.1f, 0.2f, -0.2f, 0.3f, -0.3f }, output);
        CollectionAssert.AreEqual(new[] { 24, 16, 8 }, native.RequestLengths);
    }

    [TestMethod]
    public void ReadIntoSpanWritesRequestedFramesWithoutChangingGuardSamples()
    {
        FakeNative native = new();
        native.Results.Enqueue(new ReadResult([0.25f, -0.25f], null, Errors.OK));
        native.Results.Enqueue(new ReadResult([0.5f, -0.5f], null, Errors.OK));
        var renderer = new AudioPcmRenderer(11, 48000, 2, native);
        float[] guarded = [-1f, 0f, 0f, 0f, 0f, -1f];

        AudioPcmReadResult result = renderer.ReadFrames(guarded.AsSpan(1, 4), requestedFrames: 2);

        Assert.AreEqual(2, result.FramesRead);
        CollectionAssert.AreEqual(new[] { -1f, 0.25f, -0.25f, 0.5f, -0.5f, -1f }, guarded);
    }

    [TestMethod]
    public void ReadReturnsARealShortEndAndExactOfflineReadRejectsIt()
    {
        FakeNative native = new();
        native.Results.Enqueue(new ReadResult([0.25f, -0.25f], null, Errors.OK));
        native.Results.Enqueue(new ReadResult([], -1, Errors.Ended));
        var renderer = new AudioPcmRenderer(11, 44100, 2, native);
        float[] output = new float[6];

        AudioPcmReadResult result = renderer.ReadFrames(output, 0, 3);
        Assert.AreEqual(1, result.FramesRead);
        Assert.IsTrue(result.ReachedEnd);

        FakeNative exactNative = new();
        exactNative.Results.Enqueue(new ReadResult([0.25f, -0.25f], null, Errors.OK));
        exactNative.Results.Enqueue(new ReadResult([], -1, Errors.Ended));
        var exactRenderer = new AudioPcmRenderer(11, 44100, 2, exactNative);
        AudioPcmRenderException exception = Assert.ThrowsException<AudioPcmRenderException>(
            () => exactRenderer.ReadFramesExactly(output, 0, 3));

        Assert.AreEqual(AudioPcmRenderStage.UnexpectedEnd, exception.Stage);
        Assert.AreEqual(3L, exception.ExpectedFrames);
        Assert.AreEqual(1L, exception.ActualFrames);
    }

    [TestMethod]
    public void ZeroReadWithoutEndIsReportedAsAStall()
    {
        FakeNative native = new();
        native.Results.Enqueue(new ReadResult([], 0, Errors.OK));
        var renderer = new AudioPcmRenderer(11, 48000, 2, native);

        AudioPcmRenderException exception = Assert.ThrowsException<AudioPcmRenderException>(
            () => renderer.ReadFrames(new float[2], 0, 1));

        Assert.AreEqual(AudioPcmRenderStage.Stalled, exception.Stage);
    }

    [TestMethod]
    public void CallbackPullReturnsNativeFailureWithoutCreatingAnException()
    {
        FakeNative native = new();
        native.Results.Enqueue(new ReadResult([], 0, Errors.OK));
        var renderer = new AudioPcmRenderer(11, 48000, 2, native);

        bool succeeded = renderer.TryReadFrames(
            11,
            new float[2],
            requestedFrames: 1,
            out AudioPcmReadResult result,
            out AudioPcmRenderStage stage,
            out Errors? nativeError);

        Assert.IsFalse(succeeded);
        Assert.AreEqual(0, result.FramesRead);
        Assert.IsFalse(result.ReachedEnd);
        Assert.AreEqual(AudioPcmRenderStage.Stalled, stage);
        Assert.AreEqual(Errors.OK, nativeError);
    }

    [TestMethod]
    public void CallbackPullReturnsNonFiniteAndNativeErrorsAsValues()
    {
        FakeNative nonFiniteNative = new();
        nonFiniteNative.Results.Enqueue(new ReadResult([float.NaN, 0f], null, Errors.OK));
        var nonFiniteRenderer = new AudioPcmRenderer(11, 48000, 2, nonFiniteNative);
        Assert.IsFalse(nonFiniteRenderer.TryReadFrames(
            11,
            new float[2],
            requestedFrames: 1,
            out _,
            out AudioPcmRenderStage nonFiniteStage,
            out Errors? nonFiniteError));
        Assert.AreEqual(AudioPcmRenderStage.NonFiniteSample, nonFiniteStage);
        Assert.IsNull(nonFiniteError);

        FakeNative nativeErrorNative = new();
        nativeErrorNative.Results.Enqueue(new ReadResult([], -1, Errors.SampleFormat));
        var nativeErrorRenderer = new AudioPcmRenderer(11, 48000, 2, nativeErrorNative);
        Assert.IsFalse(nativeErrorRenderer.TryReadFrames(
            11,
            new float[2],
            requestedFrames: 1,
            out _,
            out AudioPcmRenderStage nativeStage,
            out Errors? nativeError));
        Assert.AreEqual(AudioPcmRenderStage.NativeRead, nativeStage);
        Assert.AreEqual(Errors.SampleFormat, nativeError);
    }

    [TestMethod]
    public void CallbackPullContainsAnUnexpectedManagedBoundaryThrow()
    {
        FakeNative native = new()
        {
            ReadException = new InvalidOperationException("managed boundary failure"),
            LastError = Errors.FileOpen
        };
        var renderer = new AudioPcmRenderer(11, 48000, 2, native);

        bool succeeded = renderer.TryReadFrames(
            11,
            new float[2],
            requestedFrames: 1,
            out AudioPcmReadResult result,
            out AudioPcmRenderStage stage,
            out Errors? nativeError);

        Assert.IsFalse(succeeded);
        Assert.AreEqual(0, result.FramesRead);
        Assert.AreEqual(AudioPcmRenderStage.NativeRead, stage);
        Assert.AreEqual(Errors.FileOpen, nativeError);
    }

    [TestMethod]
    public void PartialFrameAndNonFiniteSamplesAreRejected()
    {
        FakeNative unalignedNative = new();
        unalignedNative.Results.Enqueue(new ReadResult([0.5f], 4, Errors.OK));
        var unalignedRenderer = new AudioPcmRenderer(11, 48000, 2, unalignedNative);
        AudioPcmRenderException unaligned = Assert.ThrowsException<AudioPcmRenderException>(
            () => unalignedRenderer.ReadFrames(new float[2], 0, 1));
        Assert.AreEqual(AudioPcmRenderStage.UnalignedFrame, unaligned.Stage);

        FakeNative nonFiniteNative = new();
        nonFiniteNative.Results.Enqueue(new ReadResult([float.NaN, 0f], null, Errors.OK));
        var nonFiniteRenderer = new AudioPcmRenderer(11, 48000, 2, nonFiniteNative);
        AudioPcmRenderException nonFinite = Assert.ThrowsException<AudioPcmRenderException>(
            () => nonFiniteRenderer.ReadFrames(new float[2], 0, 1));
        Assert.AreEqual(AudioPcmRenderStage.NonFiniteSample, nonFinite.Stage);
    }

    [TestMethod]
    public void WholeBufferMeasurementUsesEverySampleAndAllowsEmptyPcm()
    {
        AudioPcmLevels levels = AudioPcmRenderer.Measure([1f, 0f], channelCount: 1);
        AudioPcmLevels empty = AudioPcmRenderer.Measure([], channelCount: 2);

        Assert.AreEqual(1d, levels.Peak);
        Assert.AreEqual(System.Math.Sqrt(0.5d), levels.Rms, 1e-12d);
        Assert.AreEqual(0d, empty.Peak);
        Assert.AreEqual(0d, empty.Rms);
    }

    [TestMethod]
    public void AbsoluteFrameConversionUsesTiesToEven()
    {
        Assert.AreEqual(220L, AudioPcmRenderer.TimeToFrame(TimeSpan.FromTicks(50000), 44100));
        Assert.AreEqual(662L, AudioPcmRenderer.TimeToFrame(TimeSpan.FromTicks(150000), 44100));
    }

    private sealed class FakeNative : IAudioPcmNative
    {
        internal Queue<ReadResult> Results { get; } = new();

        internal List<int> RequestLengths { get; } = [];

        internal Exception? ReadException { get; init; }

        public Errors LastError { get; set; } = Errors.OK;

        public int ChannelGetData(int channel, float[] buffer, int lengthBytes)
        {
            RequestLengths.Add(lengthBytes);
            if (ReadException is { } exception)
            {
                throw exception;
            }

            if (Results.Count == 0)
            {
                LastError = Errors.OK;
                return lengthBytes;
            }

            ReadResult result = Results.Dequeue();
            LastError = result.Error;
            int bytes = result.ReturnBytes ?? checked(result.Samples.Length * sizeof(float));
            int samplesToCopy = System.Math.Min(
                result.Samples.Length,
                System.Math.Max(0, bytes / sizeof(float)));
            Array.Copy(result.Samples, buffer, samplesToCopy);
            return bytes;
        }
    }

    private sealed record ReadResult(float[] Samples, int? ReturnBytes, Errors Error);
}
