using ManagedBass.Enc;
using Microsoft.VisualStudio.TestTools.UnitTesting;
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

        AudioEncoderCommand opusCommand = AudioEncoderCommandFactory.Create(CreateRequest(
            EncoderType.OPUS,
            @"C:\output\sample.opus",
            AudioTagInfo.Empty,
            SampleFormat.SAMPLE_FLOAT_32BIT));
        Assert.IsFalse(opusCommand.Flags.HasFlag(EncodeFlags.NoHeader));
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
        Assert.IsTrue(lameCommand.Flags.HasFlag(EncodeFlags.Dither));

        AudioEncoderCommand flacCommand = AudioEncoderCommandFactory.Create(CreateRequest(
            EncoderType.FLAC,
            @"C:\output\sample.flac",
            AudioTagInfo.Empty,
            SampleFormat.SAMPLE_FLOAT_32BIT));
        StringAssert.Contains(flacCommand.CommandLine, "--bps=24");
        Assert.IsTrue(flacCommand.Flags.HasFlag(EncodeFlags.ConvertFloatTo24Bit));
        Assert.IsFalse(flacCommand.Flags.HasFlag(EncodeFlags.Dither));

        AudioEncoderCommand opusCommand = AudioEncoderCommandFactory.Create(CreateRequest(
            EncoderType.OPUS,
            @"C:\output\sample.opus",
            AudioTagInfo.Empty,
            SampleFormat.SAMPLE_FLOAT_32BIT));
        StringAssert.Contains(opusCommand.CommandLine, " --ignorelength --bitrate ");
        Assert.IsFalse(opusCommand.CommandLine.Contains("--raw-", System.StringComparison.Ordinal));
        Assert.IsFalse(opusCommand.Flags.HasFlag(EncodeFlags.NoHeader));
        Assert.IsFalse(opusCommand.Flags.HasFlag(EncodeFlags.ConvertFloatTo8BitInt));
        Assert.IsFalse(opusCommand.Flags.HasFlag(EncodeFlags.ConvertFloatTo16BitInt));
        Assert.IsFalse(opusCommand.Flags.HasFlag(EncodeFlags.ConvertFloatTo24Bit));
        Assert.IsFalse(opusCommand.Flags.HasFlag(EncodeFlags.ConvertFloatTo32Bit));
        Assert.IsFalse(opusCommand.Flags.HasFlag(EncodeFlags.Dither));

        AudioEncoderCommand oggCommand = AudioEncoderCommandFactory.Create(CreateRequest(
            EncoderType.OGG_VORBIS,
            @"C:\output\sample.ogg",
            AudioTagInfo.Empty,
            SampleFormat.SAMPLE_FLOAT_32BIT));
        StringAssert.Contains(oggCommand.CommandLine, "-r -F 3 -C");
        Assert.IsFalse(oggCommand.CommandLine.Contains(" -B ", System.StringComparison.Ordinal));
        Assert.IsFalse(oggCommand.Flags.HasFlag(EncodeFlags.ConvertFloatTo16BitInt));
        Assert.IsFalse(oggCommand.Flags.HasFlag(EncodeFlags.Dither));

        AudioEncoderCommand neroCommand = AudioEncoderCommandFactory.Create(CreateRequest(
            EncoderType.AAC_NERO,
            @"C:\output\sample.m4a",
            AudioTagInfo.Empty,
            SampleFormat.SAMPLE_FLOAT_32BIT));
        Assert.IsFalse(neroCommand.Flags.HasFlag(EncodeFlags.NoHeader));
        Assert.IsFalse(neroCommand.Flags.HasFlag(EncodeFlags.ConvertFloatTo16BitInt));
        Assert.IsFalse(neroCommand.Flags.HasFlag(EncodeFlags.Dither));

        AudioEncoderCommand wavCommand = AudioEncoderCommandFactory.Create(CreateRequest(
            EncoderType.WAVE,
            @"C:\output\sample.wav",
            AudioTagInfo.Empty,
            SampleFormat.SAMPLE_FLOAT_32BIT,
            requestedOutputFormat: SampleFormat.SAMPLE_INT_16BIT));
        Assert.IsTrue(wavCommand.Flags.HasFlag(EncodeFlags.PCM));
        Assert.IsTrue(wavCommand.Flags.HasFlag(EncodeFlags.ConvertFloatTo16BitInt));
        Assert.IsTrue(wavCommand.Flags.HasFlag(EncodeFlags.Dither));
    }

    [TestMethod]
    public void IntegerConversionFlagsUse24BitSoftwareDitherWithoutNativeDither()
    {
        (SampleFormat Format, EncodeFlags Conversion, bool NativeDither)[] formats =
        [
            (SampleFormat.SAMPLE_INT_8BIT, EncodeFlags.ConvertFloatTo8BitInt, true),
            (SampleFormat.SAMPLE_INT_16BIT, EncodeFlags.ConvertFloatTo16BitInt, true),
            (SampleFormat.SAMPLE_INT_24BIT, EncodeFlags.ConvertFloatTo24Bit, false),
            (SampleFormat.SAMPLE_INT_32BIT, EncodeFlags.ConvertFloatTo32Bit, true)
        ];
        const EncodeFlags conversionMask =
            EncodeFlags.ConvertFloatTo8BitInt
            | EncodeFlags.ConvertFloatTo16BitInt
            | EncodeFlags.ConvertFloatTo32Bit;

        foreach ((SampleFormat format, EncodeFlags conversion, bool nativeDither) in formats)
        {
            AudioEncoderCommand command = AudioEncoderCommandFactory.Create(CreateRequest(
                EncoderType.WAVE,
                @"C:\output\sample.wav",
                AudioTagInfo.Empty,
                requestedOutputFormat: format));

            Assert.AreEqual(conversion, command.Flags & conversionMask, format.ToString());
            Assert.AreEqual(nativeDither, command.Flags.HasFlag(EncodeFlags.Dither), format.ToString());
        }
    }

    [TestMethod]
    public void QualityValuesClampToEachEncoderProductionRange()
    {
        StringAssert.Contains(CreateCommand(EncoderType.MP3_LAME, -1f), " -V 9 ");
        StringAssert.Contains(CreateCommand(EncoderType.MP3_LAME, 2f), " -V 0 ");

        StringAssert.Contains(CreateCommand(EncoderType.AAC_NERO, -1f), " -q 0.0 ");
        StringAssert.Contains(CreateCommand(EncoderType.AAC_NERO, 2f), " -q 1.0 ");

        StringAssert.Contains(CreateCommand(EncoderType.OPUS, -1f), "--bitrate 6 ");
        StringAssert.Contains(CreateCommand(EncoderType.OPUS, 2f), "--bitrate 256 ");

        StringAssert.Contains(CreateCommand(EncoderType.FLAC, -1f), "--replay-gain -0 ");
        StringAssert.Contains(CreateCommand(EncoderType.FLAC, 2f), "--replay-gain -8 ");

        StringAssert.Contains(CreateCommand(EncoderType.OGG_VORBIS, -1f), " -q 0.0 ");
        StringAssert.Contains(CreateCommand(EncoderType.OGG_VORBIS, 2f), " -q 8.0 ");
    }

    [TestMethod]
    public void EncoderCommandLinesPreserveQualityAndTagMappings()
    {
        AudioTagInfo tags = new(
            "artist value",
            "title value",
            "genre value",
            12.5,
            "120",
            "source.bms",
            "comment value");

        Assert.AreEqual(
            @"""C:\encoder tools\lame.exe"" -r -s 44.1 --bitwidth 16 -h --replaygain-accurate -V 4 --ignore-tag-errors --tt ""title value"" --ta ""artist value"" --tc ""comment value"" --tg ""genre value"" - ""C:\output folder\sample file.mp3""",
            AudioEncoderCommandFactory.Create(CreateRequest(
                EncoderType.MP3_LAME,
                @"C:\output folder\sample file.mp3",
                tags,
                quality: 0.6f)).CommandLine);

        Assert.AreEqual(
            @"""C:\encoder tools\neroAacEnc.exe"" -q 0.6 -if - -of ""C:\output folder\sample file.m4a""",
            AudioEncoderCommandFactory.Create(CreateRequest(
                EncoderType.AAC_NERO,
                @"C:\output folder\sample file.m4a",
                tags,
                quality: 0.6f)).CommandLine);

        Assert.AreEqual(
            @"""C:\encoder tools\opusenc.exe"" --ignorelength --bitrate 106 - ""C:\output folder\sample file.opus""",
            AudioEncoderCommandFactory.Create(CreateRequest(
                EncoderType.OPUS,
                @"C:\output folder\sample file.opus",
                tags,
                SampleFormat.SAMPLE_FLOAT_32BIT,
                quality: 0.4f)).CommandLine);

        Assert.AreEqual(
            @"""C:\encoder tools\flac.exe"" -f --force-raw-format --endian=little --sample-rate=44100 --channels=2 --bps=16 --sign=signed --replay-gain -4 -o ""C:\output folder\sample file.flac"" -- -",
            AudioEncoderCommandFactory.Create(CreateRequest(
                EncoderType.FLAC,
                @"C:\output folder\sample file.flac",
                tags,
                quality: 0.4f)).CommandLine);

        Assert.AreEqual(
            @"""C:\encoder tools\oggenc2.exe"" -r -F 1 -B 16 -C 2 -R 44100 -q 4.0 -o ""C:\output folder\sample file.ogg"" -",
            AudioEncoderCommandFactory.Create(CreateRequest(
                EncoderType.OGG_VORBIS,
                @"C:\output folder\sample file.ogg",
                AudioTagInfo.Empty,
                quality: 0.4f)).CommandLine);

        string oggWithTags = AudioEncoderCommandFactory.Create(CreateRequest(
            EncoderType.OGG_VORBIS,
            @"C:\output folder\sample file.ogg",
            tags,
            quality: 0.4f)).CommandLine;
        StringAssert.Contains(oggWithTags, "--utf8 -t \"title value\" -a \"artist value\"");
        StringAssert.Contains(oggWithTags, "-c \"COMMENT=comment value\" -c \"BPM=120\"");
    }

    [TestMethod]
    public void EncoderFactoryRejectsUnspecifiedSampleRateInsteadOfAssuming44100Hz()
    {
        AudioEncoderCommandRequest request = CreateRequest(
            EncoderType.OPUS,
            @"C:\output\sample.opus",
            AudioTagInfo.Empty) with
        { SampleRate = 0 };

        Assert.ThrowsException<System.ArgumentOutOfRangeException>(
            () => AudioEncoderCommandFactory.Create(request));
    }

    private static string CreateCommand(EncoderType encoderType, float quality)
    {
        return AudioEncoderCommandFactory.Create(CreateRequest(
            encoderType,
            @"C:\output\sample" + encoderType.GetEncoderOutputExtension(),
            AudioTagInfo.Empty,
            quality: quality)).CommandLine;
    }

    private static AudioEncoderCommandRequest CreateRequest(
        EncoderType encoderType,
        string outputFile,
        AudioTagInfo? tags,
        SampleFormat sampleFormat = SampleFormat.SAMPLE_INT_16BIT,
        float quality = 0.4f,
        SampleFormat requestedOutputFormat = SampleFormat.UNKNOWN)
    {
        return new AudioEncoderCommandRequest(
            encoderType,
            @"C:\encoder tools",
            outputFile,
            44100,
            2,
            sampleFormat,
            quality,
            tags!,
            requestedOutputFormat);
    }
}
