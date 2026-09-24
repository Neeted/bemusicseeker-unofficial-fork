using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Ribbit.Media.Audio;

namespace BeMusicSeeker.Tests;

[TestClass]
[DoNotParallelize]
public sealed class VorbisDecoderTests
{
    [TestInitialize]
    public void InitializeNativeRuntime()
    {
        BassAudioRuntime.Shutdown();
        BassAudioRuntime.Initialize();
    }

    [TestCleanup]
    public void ShutdownNativeRuntime()
    {
        BassAudioRuntime.Shutdown();
    }

    [DataTestMethod]
    [DataRow("nvorbis-1test.ogg", "nvorbis-1test-reference.f32", 1, 17318L)]
    [DataRow("nvorbis-3test.ogg", "nvorbis-3test-reference.f32", 2, 288094L)]
    public void DecodeOgg_UsesPinnedFloatReferenceWithoutClipping(
        string inputName,
        string referenceName,
        int expectedChannels,
        long expectedFrames)
    {
        string inputPath = GetAudioPath(inputName);
        DecodedAudio actual = VorbisDecoder.Decode(inputPath);
        byte[] referenceBytes = File.ReadAllBytes(GetAudioPath(referenceName));
        int expectedSamples = checked((int)(expectedFrames * expectedChannels));
        Assert.AreEqual(checked(expectedSamples * sizeof(float)), referenceBytes.Length);
        Assert.AreEqual(44100, actual.SampleRate);
        Assert.AreEqual(expectedChannels, actual.ChannelCount);
        Assert.AreEqual(expectedFrames, actual.FrameCount);

        for (int index = 0; index < expectedSamples; index++)
        {
            int expectedBits = BinaryPrimitives.ReadInt32LittleEndian(referenceBytes.AsSpan(index * sizeof(float)));
            Assert.AreEqual(expectedBits, BitConverter.SingleToInt32Bits(actual.GetSample(index)), $"sample {index}");
        }

        if (expectedChannels == 2)
        {
            Assert.IsTrue(Enumerable.Range(0, expectedSamples).Any(index => System.Math.Abs(actual.GetSample(index)) > 1f));
        }
    }

    [TestMethod]
    public void DecodeOgg_ConcatenatesAValidSameFormatChain()
    {
        byte[] input = File.ReadAllBytes(GetAudioPath("nvorbis-1test.ogg"));
        byte[] reference = File.ReadAllBytes(GetAudioPath("nvorbis-1test-reference.f32"));
        string chainedPath = WriteTemporaryOgg(CreateChain(input, input));
        try
        {
            DecodedAudio actual = VorbisDecoder.Decode(chainedPath);
            Assert.AreEqual(34636L, actual.FrameCount);
            Assert.AreEqual(reference.Length * 2L, actual.PcmByteCount);
            int samplesPerLink = reference.Length / sizeof(float);
            for (int index = 0; index < samplesPerLink; index++)
            {
                int expectedBits = BinaryPrimitives.ReadInt32LittleEndian(reference.AsSpan(index * sizeof(float)));
                Assert.AreEqual(expectedBits, BitConverter.SingleToInt32Bits(actual.GetSample(index)), $"first link sample {index}");
                Assert.AreEqual(expectedBits, BitConverter.SingleToInt32Bits(actual.GetSample(index + samplesPerLink)), $"second link sample {index}");
            }
        }
        finally
        {
            File.Delete(chainedPath);
        }
    }

    [TestMethod]
    public void DecodeOgg_RejectsAChainedFormatChange()
    {
        byte[] mono = File.ReadAllBytes(GetAudioPath("nvorbis-1test.ogg"));
        byte[] stereo = File.ReadAllBytes(GetAudioPath("nvorbis-3test.ogg"));
        string chainedPath = WriteTemporaryOgg(CreateChain(mono, stereo));
        try
        {
            AudioSourceLoadException failure = Assert.ThrowsException<AudioSourceLoadException>(
                () => VorbisDecoder.Decode(chainedPath));
            Assert.AreEqual(AudioSourceLoadStage.DecodeVorbis, failure.Stage);
            StringAssert.Contains(failure.Message, "sample rate or channel layout");
        }
        finally
        {
            File.Delete(chainedPath);
        }
    }

    [DataTestMethod]
    [DataRow("crc")]
    [DataRow("missing-page")]
    [DataRow("truncated")]
    [DataRow("missing-eos")]
    public void DecodeOgg_RejectsBrokenFiniteContainer(string defect)
    {
        byte[] input = File.ReadAllBytes(GetAudioPath("nvorbis-3test.ogg"));
        byte[] damaged = defect switch
        {
            "crc" => CorruptChecksum(input),
            "missing-page" => RemovePage(input, 1),
            "truncated" => input[..^1],
            "missing-eos" => RemoveFinalEos(input),
            _ => throw new ArgumentOutOfRangeException(nameof(defect))
        };
        string damagedPath = WriteTemporaryOgg(damaged);
        try
        {
            AudioSourceLoadException failure = Assert.ThrowsException<AudioSourceLoadException>(
                () => VorbisDecoder.Decode(damagedPath));
            Assert.AreEqual(AudioSourceLoadStage.DecodeVorbis, failure.Stage);
            Assert.AreEqual(2, failure.NativeErrorCode);
        }
        finally
        {
            File.Delete(damagedPath);
        }
    }

    [TestMethod]
    public void NativeBridge_ReportsThePinnedAbiAndBuild()
    {
        StringAssert.Contains(VorbisDecoder.NativeBuildInfo, "abi=1");
        StringAssert.Contains(VorbisDecoder.NativeBuildInfo, "libogg=1.3.6");
        StringAssert.Contains(VorbisDecoder.NativeBuildInfo, "libvorbis=1.3.7");
        StringAssert.Contains(VorbisDecoder.NativeBuildInfo, "arch=x64;config=Release;crt=static;fp=precise");
    }

    private static string GetAudioPath(string fileName)
        => Path.Combine(AppContext.BaseDirectory, "TestData", "audio", fileName);

    private static string WriteTemporaryOgg(byte[] bytes)
    {
        string path = Path.Combine(Path.GetTempPath(), "BeMusicSeeker-" + Guid.NewGuid().ToString("N") + ".ogg");
        File.WriteAllBytes(path, bytes);
        return path;
    }

    private static byte[] CreateChain(params byte[][] sources)
    {
        using var result = new MemoryStream();
        for (int sourceIndex = 0; sourceIndex < sources.Length; sourceIndex++)
        {
            foreach (byte[] page in SplitPages(sources[sourceIndex]))
            {
                BinaryPrimitives.WriteUInt32LittleEndian(page.AsSpan(14), checked(0xC0100000u + (uint)sourceIndex));
                WriteChecksum(page);
                result.Write(page);
            }
        }
        return result.ToArray();
    }

    private static byte[] CorruptChecksum(byte[] input)
    {
        List<byte[]> pages = SplitPages(input);
        pages[^1][22] ^= 0x01;
        return JoinPages(pages);
    }

    private static byte[] RemovePage(byte[] input, int index)
    {
        List<byte[]> pages = SplitPages(input);
        Assert.IsTrue(pages.Count > index + 1, "The fixture needs a later page after the removed page.");
        pages.RemoveAt(index);
        return JoinPages(pages);
    }

    private static byte[] RemoveFinalEos(byte[] input)
    {
        List<byte[]> pages = SplitPages(input);
        pages[^1][5] &= 0xFB;
        WriteChecksum(pages[^1]);
        return JoinPages(pages);
    }

    private static List<byte[]> SplitPages(byte[] input)
    {
        var pages = new List<byte[]>();
        int offset = 0;
        while (offset < input.Length)
        {
            if (input.Length - offset < 27 || !input.AsSpan(offset, 4).SequenceEqual("OggS"u8))
            {
                throw new InvalidDataException("The Ogg fixture has an invalid page header.");
            }

            int segmentCount = input[offset + 26];
            int headerLength = 27 + segmentCount;
            if (input.Length - offset < headerLength)
            {
                throw new InvalidDataException("The Ogg fixture has a truncated segment table.");
            }

            int bodyLength = 0;
            for (int index = 0; index < segmentCount; index++)
            {
                bodyLength = checked(bodyLength + input[offset + 27 + index]);
            }
            int pageLength = checked(headerLength + bodyLength);
            if (input.Length - offset < pageLength)
            {
                throw new InvalidDataException("The Ogg fixture has a truncated page body.");
            }

            pages.Add(input.AsSpan(offset, pageLength).ToArray());
            offset += pageLength;
        }
        return pages;
    }

    private static byte[] JoinPages(IEnumerable<byte[]> pages)
    {
        using var stream = new MemoryStream();
        foreach (byte[] page in pages)
        {
            stream.Write(page);
        }
        return stream.ToArray();
    }

    private static void WriteChecksum(byte[] page)
    {
        page.AsSpan(22, 4).Clear();
        uint checksum = 0;
        foreach (byte value in page)
        {
            checksum ^= (uint)value << 24;
            for (int bit = 0; bit < 8; bit++)
            {
                checksum = (checksum & 0x80000000u) != 0
                    ? (checksum << 1) ^ 0x04C11DB7u
                    : checksum << 1;
            }
        }
        BinaryPrimitives.WriteUInt32LittleEndian(page.AsSpan(22), checksum);
    }
}
