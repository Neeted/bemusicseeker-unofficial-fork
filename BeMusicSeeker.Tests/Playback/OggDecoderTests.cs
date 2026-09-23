using System;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Ribbit.Media;

namespace BeMusicSeeker.Tests;

[TestClass]
public sealed class OggDecoderTests
{
    // Fixtures are the small mono/stereo files from the NVorbis TestFiles directory.
    // Source: https://github.com/NVorbis/NVorbis/tree/master/TestFiles
    [DataTestMethod]
    [DataRow("nvorbis-1test.ogg", 1, 44100, 17318, "8E02A8638D0E55701224D683D8C8CC4D2784084F7CC5156CD15193390B11FEB8")]
    [DataRow("nvorbis-3test.ogg", 2, 44100, 576188, "229F1A3CB20D0BA67BDD3702FFD6F0DA08DC8DD4B44FB50E150A5ECEC1AC4778")]
    public void DecodeOggToWave_PreservesPcmFormatAndDeterministicSamples(
        string fixtureName,
        int expectedChannels,
        int expectedSampleRate,
        int expectedSampleCount,
        string expectedPcmSha256)
    {
        using Stream fixture = ReadFixture(fixtureName);
        byte[] wave = BassAudioPlayer.DecodeOggToWave(fixture);

        Assert.AreEqual(44 + expectedSampleCount * sizeof(short), wave.Length);
        Assert.AreEqual("RIFF", Encoding.ASCII.GetString(wave, 0, 4));
        Assert.AreEqual("WAVE", Encoding.ASCII.GetString(wave, 8, 4));
        Assert.AreEqual((short)expectedChannels, BitConverter.ToInt16(wave, 22));
        Assert.AreEqual(expectedSampleRate, BitConverter.ToInt32(wave, 24));
        Assert.AreEqual(expectedSampleCount * sizeof(short), BitConverter.ToInt32(wave, 40));
        Assert.AreEqual(expectedPcmSha256, Convert.ToHexString(SHA256.HashData(wave.AsSpan(44))));
    }

    [TestMethod]
    public void DecodeOggToWave_RejectsTruncatedContainer()
    {
        byte[] fixture = File.ReadAllBytes(GetFixturePath("nvorbis-1test.ogg"));
        using var truncated = new MemoryStream(fixture, 0, fixture.Length / 2, writable: false);

        Exception? failure = null;
        try
        {
            _ = BassAudioPlayer.DecodeOggToWave(truncated);
        }
        catch (Exception ex)
        {
            failure = ex;
        }

        Assert.IsNotNull(failure);
    }

    [TestMethod]
    public void DecodeOggToWave_ConcatenatesLogicalStreams()
    {
        byte[] single = File.ReadAllBytes(GetFixturePath("nvorbis-1test.ogg"));
        byte[] chained = Concatenate(single, single);

        using var expectedSource = new MemoryStream(single, writable: false);
        using var actualSource = new MemoryStream(chained, writable: false);
        byte[] expectedWave = BassAudioPlayer.DecodeOggToWave(expectedSource);
        byte[] actualWave = BassAudioPlayer.DecodeOggToWave(actualSource);

        Assert.AreEqual(44 + (expectedWave.Length - 44) * 2, actualWave.Length);
        ReadOnlySpan<byte> expectedPcm = expectedWave.AsSpan(44);
        ReadOnlySpan<byte> actualPcm = actualWave.AsSpan(44);
        Assert.IsTrue(actualPcm[..expectedPcm.Length].SequenceEqual(expectedPcm));
        Assert.IsTrue(actualPcm[expectedPcm.Length..].SequenceEqual(expectedPcm));
    }

    [TestMethod]
    public void DecodeOggToWave_RejectsConcatenatedFormatChange()
    {
        byte[] chained = Concatenate(
            File.ReadAllBytes(GetFixturePath("nvorbis-1test.ogg")),
            File.ReadAllBytes(GetFixturePath("nvorbis-3test.ogg")));

        Exception? failure = null;
        using var source = new MemoryStream(chained, writable: false);
        try
        {
            _ = BassAudioPlayer.DecodeOggToWave(source);
        }
        catch (Exception ex)
        {
            failure = ex;
        }

        Assert.IsNotNull(failure);
    }

    private static Stream ReadFixture(string fixtureName)
    {
        return File.OpenRead(GetFixturePath(fixtureName));
    }

    private static byte[] Concatenate(params byte[][] parts)
    {
        using var result = new MemoryStream();
        foreach (byte[] part in parts)
        {
            result.Write(part, 0, part.Length);
        }

        return result.ToArray();
    }

    private static string GetFixturePath(string fixtureName)
    {
        return Path.Combine(AppContext.BaseDirectory, "TestData", "audio", fixtureName);
    }
}
