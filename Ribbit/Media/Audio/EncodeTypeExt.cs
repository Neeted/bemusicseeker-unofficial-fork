using System;
using System.IO;
using BeMusicSeeker.Models.Utils;

namespace Ribbit.Media.Audio;

internal static class EncodeTypeExt
{
    private const string NativeArchitectureDirectoryName = "x64";

    private static readonly string[] exeFiles =
    [
        string.Empty,
        "lame.exe",
        "neroAacEnc.exe",
        "opusenc.exe",
        "flac.exe",
        "oggenc2.exe"
    ];

    private static readonly string[] extensions = [".wav", ".mp3", ".aac", ".opus", ".flac", ".ogg"];

    public static string GetEncoderFileName(this EncoderType encodeType)
    {
        return exeFiles[(int)encodeType];
    }

    public static string SearchEncoderBinary(this EncoderType encodeType, string EncoderDirectory = null)
    {
        if (encodeType == EncoderType.WAVE)
        {
            return string.Empty;
        }
        string encoderFileName = encodeType.GetEncoderFileName();
        if (EncoderDirectory != null && LongPathFileSystem.DirectoryExists(EncoderDirectory) && LongPathFileSystem.FileExists(Path.Combine(EncoderDirectory, encoderFileName)))
        {
            return EncoderDirectory;
        }
        string directoryName = AppContext.BaseDirectory;
        if (LongPathFileSystem.FileExists(Path.Combine(directoryName, encoderFileName)))
        {
            return directoryName;
        }
        directoryName = Path.Combine(AppContext.BaseDirectory, "libs", NativeArchitectureDirectoryName);
        if (LongPathFileSystem.FileExists(Path.Combine(directoryName, encoderFileName)))
        {
            return directoryName;
        }
        directoryName = Path.Combine(AppContext.BaseDirectory, NativeArchitectureDirectoryName);
        if (LongPathFileSystem.FileExists(Path.Combine(directoryName, encoderFileName)))
        {
            return directoryName;
        }
        return null;
    }

    public static string GetExtension(this EncoderType encoderType)
    {
        return extensions[(int)encoderType];
    }

    /// <summary>
    /// Gets the extension produced by the selected encoder binary.
    /// This remains separate from the application setting extension for Nero AAC.
    /// </summary>
    internal static string GetEncoderOutputExtension(this EncoderType encoderType)
    {
        return encoderType switch
        {
            EncoderType.WAVE => ".wav",
            EncoderType.MP3_LAME => ".mp3",
            EncoderType.AAC_NERO => ".m4a",
            EncoderType.OPUS => ".opus",
            EncoderType.FLAC => ".flac",
            EncoderType.OGG_VORBIS => ".ogg",
            _ => throw new ArgumentOutOfRangeException(nameof(encoderType), encoderType, "Unknown encoder type.")
        };
    }
}
