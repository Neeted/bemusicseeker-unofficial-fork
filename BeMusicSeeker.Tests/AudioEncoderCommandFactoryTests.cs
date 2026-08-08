using Microsoft.VisualStudio.TestTools.UnitTesting;
using ManagedBass.Enc;
using Ribbit.Media.Audio;

namespace BeMusicSeeker.Tests;

/// <summary>
/// Verifies command construction and argument ownership independently of native BASSenc state.
/// </summary>
[TestClass]
public sealed class AudioEncoderCommandFactoryTests
{
    [TestMethod]
    public void WindowsArgumentQuotingPreservesQuotesUnicodeAndTrailingBackslash()
    {
        Assert.AreEqual("\"\"", AudioEncoderCommandFactory.QuoteWindowsArgument(string.Empty));
        Assert.AreEqual("\"C:\\encoder tools\\lame.exe\"", AudioEncoderCommandFactory.QuoteWindowsArgument(@"C:\encoder tools\lame.exe"));
        Assert.AreEqual("\"value\\\"with quote\"", AudioEncoderCommandFactory.QuoteWindowsArgument("value\"with quote"));
        Assert.AreEqual("\"C:\\trailing\\\\\"", AudioEncoderCommandFactory.QuoteWindowsArgument("C:\\trailing\\"));
        Assert.AreEqual("\"音楽 データ\"", AudioEncoderCommandFactory.QuoteWindowsArgument("音楽 データ"));
    }

    [TestMethod]
    public void MissingMetadataDoesNotAddEmptyEncoderOptions()
    {
        AudioEncoderCommand command = AudioEncoderCommandFactory.Create(CreateRequest(
            EncoderType.MP3_LAME,
            @"C:\output\sample.mp3",
            AudioTagInfo.Empty));

        Assert.IsFalse(command.CommandLine.Contains("--tt", System.StringComparison.Ordinal));
        Assert.IsFalse(command.CommandLine.Contains("--ta", System.StringComparison.Ordinal));
        Assert.IsFalse(command.CommandLine.Contains("--tc", System.StringComparison.Ordinal));
        Assert.IsFalse(command.CommandLine.Contains("--tg", System.StringComparison.Ordinal));
    }

    [TestMethod]
    public void NullMetadataIsCanonicalizedWithoutChangingCommandFormat()
    {
        AudioEncoderCommandRequest request = CreateRequest(
            EncoderType.OGG_VORBIS,
            @"C:\output\sample.ogg",
            null);

        AudioEncoderCommand command = AudioEncoderCommandFactory.Create(request);

        StringAssert.EndsWith(command.CommandLine, @"-o ""C:\output\sample.ogg"" -");
    }

    [TestMethod]
    public void EncoderFlagsPreserveRawInputHeaderContract()
    {
        EncoderType[] rawInputEncoders =
        [
            EncoderType.MP3_LAME,
            EncoderType.OPUS,
            EncoderType.FLAC,
            EncoderType.OGG_VORBIS
        ];

        foreach (EncoderType encoderType in rawInputEncoders)
        {
            AudioEncoderCommand command = AudioEncoderCommandFactory.Create(CreateRequest(
                encoderType,
                @"C:\output\sample.out",
                AudioTagInfo.Empty));

            Assert.IsTrue(command.Flags.HasFlag(EncodeFlags.NoHeader), encoderType.ToString());
        }

        AudioEncoderCommand neroCommand = AudioEncoderCommandFactory.Create(CreateRequest(
            EncoderType.AAC_NERO,
            @"C:\output\sample.m4a",
            AudioTagInfo.Empty));
        Assert.IsFalse(neroCommand.Flags.HasFlag(EncodeFlags.NoHeader));

        AudioEncoderCommand wavCommand = AudioEncoderCommandFactory.Create(CreateRequest(
            EncoderType.WAVE,
            @"C:\output\sample.wav",
            AudioTagInfo.Empty));
        Assert.IsTrue(wavCommand.Flags.HasFlag(EncodeFlags.PCM));
        Assert.IsFalse(wavCommand.Flags.HasFlag(EncodeFlags.NoHeader));
    }

    [TestMethod]
    public void FloatInputUsesEncoderCompatibleRawFormats()
    {
        AudioEncoderCommand lameCommand = AudioEncoderCommandFactory.Create(CreateRequest(
            EncoderType.MP3_LAME,
            @"C:\output\sample.mp3",
            AudioTagInfo.Empty,
            SampleFormat.SAMPLE_FLOAT_32BIT));
        StringAssert.Contains(lameCommand.CommandLine, "--bitwidth 32");
        Assert.IsTrue(lameCommand.Flags.HasFlag(EncodeFlags.ConvertFloatTo32Bit));

        AudioEncoderCommand flacCommand = AudioEncoderCommandFactory.Create(CreateRequest(
            EncoderType.FLAC,
            @"C:\output\sample.flac",
            AudioTagInfo.Empty,
            SampleFormat.SAMPLE_FLOAT_32BIT));
        StringAssert.Contains(flacCommand.CommandLine, "--bps=24");
        Assert.IsTrue(flacCommand.Flags.HasFlag(EncodeFlags.ConvertFloatTo24Bit));

        AudioEncoderCommand opusCommand = AudioEncoderCommandFactory.Create(CreateRequest(
            EncoderType.OPUS,
            @"C:\output\sample.opus",
            AudioTagInfo.Empty,
            SampleFormat.SAMPLE_FLOAT_32BIT));
        StringAssert.Contains(opusCommand.CommandLine, "--raw-bits 24");
        Assert.IsTrue(opusCommand.Flags.HasFlag(EncodeFlags.ConvertFloatTo24Bit));

        AudioEncoderCommand oggCommand = AudioEncoderCommandFactory.Create(CreateRequest(
            EncoderType.OGG_VORBIS,
            @"C:\output\sample.ogg",
            AudioTagInfo.Empty,
            SampleFormat.SAMPLE_FLOAT_32BIT));
        StringAssert.Contains(oggCommand.CommandLine, "-r -F 3 -C");
        Assert.IsFalse(oggCommand.CommandLine.Contains(" -B ", System.StringComparison.Ordinal));
        Assert.IsFalse(oggCommand.Flags.HasFlag(EncodeFlags.ConvertFloatTo16BitInt));
    }

    private static AudioEncoderCommandRequest CreateRequest(
        EncoderType encoderType,
        string outputFile,
        AudioTagInfo tags,
        SampleFormat sampleFormat = SampleFormat.SAMPLE_INT_16BIT)
    {
        return new AudioEncoderCommandRequest(
            encoderType,
            @"C:\encoder tools",
            outputFile,
            44100,
            2,
            sampleFormat,
            0.4f,
            tags);
    }
}
