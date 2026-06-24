using System;
using System.IO;
using System.Reflection;
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
        string directoryName = Path.GetDirectoryName(Assembly.GetExecutingAssembly().Location);
        if (LongPathFileSystem.FileExists(Path.Combine(directoryName, encoderFileName)))
        {
            return directoryName;
        }
        directoryName = Path.Combine(Path.GetDirectoryName(Assembly.GetExecutingAssembly().Location), "libs", NativeArchitectureDirectoryName);
        if (LongPathFileSystem.FileExists(Path.Combine(directoryName, encoderFileName)))
        {
            return directoryName;
        }
        directoryName = Path.Combine(Path.GetDirectoryName(Assembly.GetExecutingAssembly().Location), NativeArchitectureDirectoryName);
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
}
