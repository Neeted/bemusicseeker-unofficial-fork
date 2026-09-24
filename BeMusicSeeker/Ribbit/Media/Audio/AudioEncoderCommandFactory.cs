using System;
using System.Globalization;
using System.Text;
using ManagedBass.Enc;

namespace Ribbit.Media.Audio;

/// <summary>BASSenc command lineの生成に使う変更不能なrequestです。</summary>
internal sealed record AudioEncoderCommandRequest(
    EncoderType EncoderType,
    string EncoderDirectory,
    string OutputFile,
    int SampleRate,
    int ChannelCount,
    SampleFormat SampleFormat,
    float Quality,
    AudioTagInfo Tags,
    SampleFormat RequestedOutputFormat = SampleFormat.UNKNOWN);

/// <summary>encoder requestから決定したcommand lineとBASSenc flagsです。</summary>
internal sealed record AudioEncoderCommand(string CommandLine, EncodeFlags Flags);

/// <summary>shellを起動せずにBASSenc command lineと出力変換flagsを生成します。</summary>
internal static class AudioEncoderCommandFactory
{
    /// <summary>encoder requestに対応するBASSenc command lineとflagsを生成します。</summary>
    internal static AudioEncoderCommand Create(AudioEncoderCommandRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentException.ThrowIfNullOrEmpty(request.OutputFile);
        if (request.SampleRate <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(request), "Encoder input sample rate must be positive.");
        }
        if (request.ChannelCount <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(request), "Encoder input channel count must be positive.");
        }

        request = request with { Tags = request.Tags ?? AudioTagInfo.Empty };

        return request.EncoderType switch
        {
            EncoderType.WAVE => CreateWave(request),
            EncoderType.MP3_LAME => CreateLame(request),
            EncoderType.AAC_NERO => CreateNero(request),
            EncoderType.OPUS => CreateOpus(request),
            EncoderType.FLAC => CreateFlac(request),
            EncoderType.OGG_VORBIS => CreateOgg(request),
            _ => throw new ArgumentOutOfRangeException(nameof(request), request.EncoderType, "Unknown encoder type.")
        };
    }

    /// <summary>BASSencが使うWindows command line規則に従いargumentをquoteします。</summary>
    internal static string QuoteWindowsArgument(string value)
    {
        ArgumentNullException.ThrowIfNull(value);
        StringBuilder quoted = new(value.Length + 2);
        quoted.Append('"');

        int backslashCount = 0;
        foreach (char character in value)
        {
            if (character == '\\')
            {
                backslashCount++;
                continue;
            }

            if (character == '"')
            {
                quoted.Append('\\', backslashCount * 2 + 1);
                quoted.Append('"');
                backslashCount = 0;
                continue;
            }

            quoted.Append('\\', backslashCount);
            quoted.Append(character);
            backslashCount = 0;
        }

        quoted.Append('\\', backslashCount * 2);
        quoted.Append('"');
        return quoted.ToString();
    }

    private static AudioEncoderCommand CreateWave(AudioEncoderCommandRequest request)
    {
        // With PCM the command argument is an output filename, not a shell command.
        SampleFormat outputFormat = request.RequestedOutputFormat is SampleFormat.AUTO or SampleFormat.UNKNOWN
            ? request.SampleFormat
            : request.RequestedOutputFormat;
        return new AudioEncoderCommand(request.OutputFile, EncodeFlags.PCM | GetSampleConversionFlags(outputFormat));
    }

    private static AudioEncoderCommand CreateLame(AudioEncoderCommandRequest request)
    {
        int quality = 10 - (int)(10f * System.Math.Clamp(request.Quality, 0.1f, 1f));
        SampleFormat inputFormat = GetExternalInputFormat(request);
        int bits = GetBitsPerSample(inputFormat);
        StringBuilder command = new(BuildExecutable(request, "lame.exe"));
        command.Append(" -r -s ")
            .Append(FormatKilohertz(request.SampleRate))
            .Append(" --bitwidth ")
            .Append(bits.ToString(CultureInfo.InvariantCulture))
            .Append(" -h --replaygain-accurate -V ")
            .Append(quality.ToString(CultureInfo.InvariantCulture))
            .Append(" --ignore-tag-errors");
        AppendLameTag(command, "--tt", request.Tags.Title);
        AppendLameTag(command, "--ta", request.Tags.Artist);
        AppendLameTag(command, "--tc", request.Tags.Comment);
        AppendLameTag(command, "--tg", request.Tags.Genre);
        command.Append(" - ").Append(QuoteWindowsArgument(request.OutputFile));
        return new AudioEncoderCommand(command.ToString(), GetExternalEncoderFlags(inputFormat, rawInput: true));
    }

    private static AudioEncoderCommand CreateNero(AudioEncoderCommandRequest request)
    {
        string quality = System.Math.Clamp(request.Quality, 0f, 1f).ToString("0.0#", CultureInfo.InvariantCulture);
        string command = BuildExecutable(request, "neroAacEnc.exe")
            + " -q " + quality
            + " -if - -of " + QuoteWindowsArgument(request.OutputFile);
        return new AudioEncoderCommand(command, GetExternalEncoderFlags(request.SampleFormat, rawInput: false));
    }

    private static AudioEncoderCommand CreateOpus(AudioEncoderCommandRequest request)
    {
        int bitrate = (int)(6f + 250f * System.Math.Clamp(request.Quality, 0f, 1f));
        string command = BuildExecutable(request, "opusenc.exe")
            + " --ignorelength --bitrate " + bitrate.ToString(CultureInfo.InvariantCulture)
            + " - " + QuoteWindowsArgument(request.OutputFile);
        return new AudioEncoderCommand(command, GetExternalEncoderFlags(request.SampleFormat, rawInput: false));
    }

    private static AudioEncoderCommand CreateFlac(AudioEncoderCommandRequest request)
    {
        int compression = (int)(10f * System.Math.Clamp(request.Quality, 0f, 0.8f));
        SampleFormat inputFormat = GetExternalInputFormat(request);
        int bits = GetBitsPerSample(inputFormat);
        string command = BuildExecutable(request, "flac.exe")
            + " -f --force-raw-format --endian=little --sample-rate="
            + request.SampleRate.ToString(CultureInfo.InvariantCulture)
            + " --channels=" + request.ChannelCount.ToString(CultureInfo.InvariantCulture)
            + " --bps=" + bits.ToString(CultureInfo.InvariantCulture)
            + " --sign=signed --replay-gain -"
            + compression.ToString(CultureInfo.InvariantCulture)
            + " -o " + QuoteWindowsArgument(request.OutputFile)
            + " -- -";
        return new AudioEncoderCommand(command, GetExternalEncoderFlags(inputFormat, rawInput: true));
    }

    private static AudioEncoderCommand CreateOgg(AudioEncoderCommandRequest request)
    {
        int quality = (int)(10f * System.Math.Clamp(request.Quality, 0f, 0.8f));
        SampleFormat inputFormat = GetExternalInputFormat(request);
        StringBuilder command = new(BuildExecutable(request, "oggenc2.exe"));
        command.Append(" -r -F ")
            .Append(inputFormat == SampleFormat.SAMPLE_FLOAT_32BIT ? "3" : "1");
        if (inputFormat != SampleFormat.SAMPLE_FLOAT_32BIT)
        {
            command.Append(" -B ")
                .Append(GetBitsPerSample(inputFormat).ToString(CultureInfo.InvariantCulture));
        }

        command.Append(" -C ")
            .Append(request.ChannelCount.ToString(CultureInfo.InvariantCulture))
            .Append(" -R ")
            .Append(request.SampleRate.ToString(CultureInfo.InvariantCulture))
            .Append(" -q ")
            .Append(quality.ToString("0.0", CultureInfo.InvariantCulture));
        AppendOggTags(command, request.Tags);
        command.Append(" -o ").Append(QuoteWindowsArgument(request.OutputFile)).Append(" -");
        return new AudioEncoderCommand(command.ToString(), GetExternalEncoderFlags(inputFormat, rawInput: true));
    }

    private static string BuildExecutable(AudioEncoderCommandRequest request, string fileName)
    {
        return QuoteWindowsArgument(System.IO.Path.Combine(request.EncoderDirectory, fileName));
    }

    private static SampleFormat GetExternalInputFormat(AudioEncoderCommandRequest request)
    {
        if (request.SampleFormat != SampleFormat.SAMPLE_FLOAT_32BIT)
        {
            return request.SampleFormat;
        }

        return request.EncoderType switch
        {
            EncoderType.MP3_LAME => SampleFormat.SAMPLE_INT_32BIT,
            EncoderType.FLAC => SampleFormat.SAMPLE_INT_24BIT,
            _ => request.SampleFormat
        };
    }

    private static EncodeFlags GetExternalEncoderFlags(SampleFormat sampleFormat, bool rawInput)
    {
        return EncodeFlags.Unicode
            | GetSampleConversionFlags(sampleFormat)
            | (rawInput ? EncodeFlags.NoHeader : EncodeFlags.Default);
    }

    private static EncodeFlags GetSampleConversionFlags(SampleFormat sampleFormat)
    {
        return sampleFormat switch
        {
            SampleFormat.SAMPLE_INT_8BIT => EncodeFlags.ConvertFloatTo8BitInt | EncodeFlags.Dither,
            SampleFormat.SAMPLE_INT_16BIT => EncodeFlags.ConvertFloatTo16BitInt | EncodeFlags.Dither,
            // BASSenc 2.4.17の24bit DITHERは平均誤差が約-0.5LSBへ偏るため、
            // sessionがTPDFを適用した格子値を供給し、native変換は格子保存に使います。
            SampleFormat.SAMPLE_INT_24BIT => EncodeFlags.ConvertFloatTo24Bit,
            SampleFormat.SAMPLE_INT_32BIT => EncodeFlags.ConvertFloatTo32Bit | EncodeFlags.Dither,
            SampleFormat.SAMPLE_FLOAT_32BIT => EncodeFlags.Default,
            _ => throw new NotSupportedException("Format must be either 8, 16, 24 or 32 bit integer")
        };
    }

    private static string FormatKilohertz(int sampleRate)
    {
        return (sampleRate / 1000f).ToString("0.###", CultureInfo.InvariantCulture);
    }

    private static int GetBitsPerSample(SampleFormat sampleFormat)
    {
        return sampleFormat switch
        {
            SampleFormat.SAMPLE_INT_8BIT => 8,
            SampleFormat.SAMPLE_INT_16BIT => 16,
            SampleFormat.SAMPLE_INT_24BIT => 24,
            SampleFormat.SAMPLE_INT_32BIT => 32,
            SampleFormat.SAMPLE_FLOAT_32BIT => 32,
            _ => throw new NotSupportedException("Format must be either 8, 16, 24 or 32 bit integer")
        };
    }

    private static void AppendLameTag(StringBuilder command, string option, string value)
    {
        if (!string.IsNullOrEmpty(value))
        {
            command.Append(' ').Append(option).Append(' ').Append(QuoteWindowsArgument(value));
        }
    }

    private static void AppendOggTags(StringBuilder command, AudioTagInfo tags)
    {
        if (tags == null || (string.IsNullOrEmpty(tags.Title)
            && string.IsNullOrEmpty(tags.Artist)
            && string.IsNullOrEmpty(tags.Genre)
            && string.IsNullOrEmpty(tags.Comment)
            && string.IsNullOrEmpty(tags.Bpm)))
        {
            return;
        }

        command.Append(" --utf8");
        AppendOggTag(command, "-t", tags.Title);
        AppendOggTag(command, "-a", tags.Artist);
        AppendOggTag(command, "-G", tags.Genre);
        if (!string.IsNullOrEmpty(tags.Comment))
        {
            command.Append(" -c ").Append(QuoteWindowsArgument("COMMENT=" + tags.Comment));
        }

        if (!string.IsNullOrEmpty(tags.Bpm))
        {
            command.Append(" -c ").Append(QuoteWindowsArgument("BPM=" + tags.Bpm));
        }
    }

    private static void AppendOggTag(StringBuilder command, string option, string value)
    {
        if (!string.IsNullOrEmpty(value))
        {
            command.Append(' ').Append(option).Append(' ').Append(QuoteWindowsArgument(value));
        }
    }
}
