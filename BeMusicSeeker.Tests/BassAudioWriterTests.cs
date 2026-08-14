using System;
using System.Collections.Generic;
using System.IO;
using ManagedBass;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Ribbit.Media;

namespace BeMusicSeeker.Tests;

/// <summary>Verifies the writer's ManagedBass pull and level boundary without native audio.</summary>
[TestClass]
public sealed class BassAudioWriterTests
{
    [DataTestMethod]
    [DataRow(".mp3")]
    [DataRow(".wav")]
    public void OutputUsesTheExistingCollisionSuffixContract(string extension)
    {
        WithCollisionFixture(extension, path =>
        {
            Assert.AreEqual(
                path + " (2)" + extension,
                BassAudioWriter.GetAvailableOutputFile(path, extension));
        });
    }

    [TestMethod]
    public void RenderUsesFullChunksAndOneRemainderWithoutChangingRequestGeometry()
    {
        var native = new FakeNative
        {
            SecondsToBytes = seconds => seconds == 2d ? 10 : 0
        };
        var renderer = new AudioWriterPullRenderer(11, new byte[4], native);

        renderer.Render(TimeSpan.FromSeconds(2));

        CollectionAssert.AreEqual(new[] { 4, 4, 2 }, native.DataRequests);
    }

    [TestMethod]
    public void RenderStopsAtPartialReadWithoutRetryingOrZeroFilling()
    {
        var native = new FakeNative
        {
            SecondsToBytes = seconds => seconds == 2d ? 10 : 0
        };
        native.DataResults.Enqueue(3);
        native.DataResults.Enqueue(4);
        var renderer = new AudioWriterPullRenderer(11, new byte[4], native);

        renderer.Render(TimeSpan.FromSeconds(2));

        CollectionAssert.AreEqual(new[] { 4 }, native.DataRequests);
    }

    [TestMethod]
    public void RenderTreatsZeroAndNaturalEndAsTerminalResults()
    {
        var zeroNative = new FakeNative
        {
            SecondsToBytes = seconds => seconds == 2d ? 10 : 0
        };
        zeroNative.DataResults.Enqueue(0);
        var zeroRenderer = new AudioWriterPullRenderer(11, new byte[4], zeroNative);

        zeroRenderer.Render(TimeSpan.FromSeconds(2));

        CollectionAssert.AreEqual(new[] { 4 }, zeroNative.DataRequests);

        var endedNative = new FakeNative
        {
            SecondsToBytes = seconds => seconds == 2d ? 10 : 0,
            LastError = Errors.Ended
        };
        endedNative.DataResults.Enqueue(-1);
        var endedRenderer = new AudioWriterPullRenderer(11, new byte[4], endedNative);

        endedRenderer.Render(TimeSpan.FromSeconds(2));

        CollectionAssert.AreEqual(new[] { 4 }, endedNative.DataRequests);
    }

    [TestMethod]
    public void RenderRaisesTypedFailureForNativeDataError()
    {
        var native = new FakeNative
        {
            SecondsToBytes = seconds => seconds == 1d ? 4 : 0,
            LastError = Errors.Handle
        };
        native.DataResults.Enqueue(-1);
        var renderer = new AudioWriterPullRenderer(11, new byte[4], native);

        AudioWriterRenderException exception = Assert.ThrowsException<AudioWriterRenderException>(
            () => renderer.Render(TimeSpan.FromSeconds(1)));

        Assert.AreEqual(11, exception.Channel);
        Assert.AreEqual(AudioWriterRenderStage.DataPull, exception.Stage);
        Assert.AreEqual(Errors.Handle, exception.NativeError);
        CollectionAssert.AreEqual(new[] { 4 }, native.DataRequests);
    }

    [TestMethod]
    public void GetLevelPullsDataBeforeLevelsAndPreservesRmsFlagAndMaximum()
    {
        var native = new FakeNative
        {
            SecondsToBytes = seconds => seconds == 2d ? 8 : seconds == 1d ? 4 : 0,
            BytesToSeconds = bytes => bytes / 4d
        };
        native.LevelResults.Enqueue([0.25f, 0.8f]);
        native.LevelResults.Enqueue([0.4f, 0.6f]);
        var renderer = new AudioWriterPullRenderer(11, new byte[4], native);

        float level = renderer.GetLevel(TimeSpan.FromSeconds(2), isRms: true);

        Assert.AreEqual(0.8f, level);
        CollectionAssert.AreEqual(
            new[]
            {
                "seconds:2",
                "seconds:1",
                "data:4",
                "bytes:4",
                "level:1:RMS",
                "data:4",
                "bytes:4",
                "level:1:RMS"
            },
            native.Calls);
    }

    [TestMethod]
    public void GetLevelTreatsNaturalEndAsTerminalAndDoesNotHideOtherLevelErrors()
    {
        var endedNative = new FakeNative
        {
            SecondsToBytes = seconds => seconds == 2d ? 8 : seconds == 1d ? 4 : 0,
            BytesToSeconds = bytes => bytes / 4d,
            LastError = Errors.Ended
        };
        endedNative.LevelResults.Enqueue([0.6f]);
        endedNative.LevelResults.Enqueue(null);
        var endedRenderer = new AudioWriterPullRenderer(11, new byte[4], endedNative);

        float level = endedRenderer.GetLevel(TimeSpan.FromSeconds(2), isRms: false);

        Assert.AreEqual(0.6f, level);
        Assert.AreEqual("level:1:All", endedNative.Calls[4]);
        Assert.AreEqual("level:1:All", endedNative.Calls[7]);

        var errorNative = new FakeNative
        {
            SecondsToBytes = seconds => seconds == 1d ? 4 : 0,
            BytesToSeconds = bytes => bytes / 4d,
            LastError = Errors.Parameter
        };
        errorNative.LevelResults.Enqueue(null);
        var errorRenderer = new AudioWriterPullRenderer(11, new byte[4], errorNative);

        AudioWriterRenderException exception = Assert.ThrowsException<AudioWriterRenderException>(
            () => errorRenderer.GetLevel(TimeSpan.FromSeconds(1), isRms: false));

        Assert.AreEqual(AudioWriterRenderStage.LevelPull, exception.Stage);
        Assert.AreEqual(Errors.Parameter, exception.NativeError);
    }

    [TestMethod]
    public void GetLevelRaisesTypedFailureForInvalidNativePositionConversion()
    {
        var native = new FakeNative
        {
            SecondsToBytes = seconds => seconds == 1d ? 4 : 8,
            BytesToSeconds = _ => -1d,
            LastError = Errors.Parameter
        };
        var renderer = new AudioWriterPullRenderer(11, new byte[4], native);

        AudioWriterRenderException exception = Assert.ThrowsException<AudioWriterRenderException>(
            () => renderer.GetLevel(TimeSpan.FromSeconds(1), isRms: false));

        Assert.AreEqual(AudioWriterRenderStage.BytesToSeconds, exception.Stage);
        Assert.AreEqual(Errors.Parameter, exception.NativeError);
    }

    private sealed class FakeNative : IAudioWriterNative
    {
        internal Queue<int> DataResults { get; } = new();

        internal Queue<float[]> LevelResults { get; } = new();

        internal List<int> DataRequests { get; } = new();

        internal List<string> Calls { get; } = new();

        internal Func<double, long> SecondsToBytes { get; init; } = _ => 0;

        internal Func<long, double> BytesToSeconds { get; init; } = _ => 0d;

        public Errors LastError { get; init; } = Errors.OK;

        public long ChannelSeconds2Bytes(int channel, double seconds)
        {
            Calls.Add("seconds:" + seconds);
            return SecondsToBytes(seconds);
        }

        public double ChannelBytes2Seconds(int channel, long bytes)
        {
            Calls.Add("bytes:" + bytes);
            return BytesToSeconds(bytes);
        }

        public int ChannelGetData(int channel, byte[] buffer, int length)
        {
            DataRequests.Add(length);
            Calls.Add("data:" + length);
            return DataResults.Count == 0 ? length : DataResults.Dequeue();
        }

        public float[] ChannelGetLevel(int channel, float seconds, LevelRetrievalFlags flags)
        {
            Calls.Add("level:" + seconds + ":" + flags);
            return LevelResults.Count == 0 ? [1f] : LevelResults.Dequeue();
        }
    }

    private static void WithCollisionFixture(string extension, Action<string> assertion)
    {
        string directoryPath = Path.Combine(
            Path.GetTempPath(),
            "BeMusicSeekerWriterContracts",
            Guid.NewGuid().ToString("N"));
        string pathWithoutExtension = Path.Combine(directoryPath, "sample");
        try
        {
            Directory.CreateDirectory(directoryPath);
            File.WriteAllText(pathWithoutExtension + extension, string.Empty);
            assertion(pathWithoutExtension);
        }
        finally
        {
            if (Directory.Exists(directoryPath))
            {
                Directory.Delete(directoryPath, recursive: true);
            }
        }
    }
}
