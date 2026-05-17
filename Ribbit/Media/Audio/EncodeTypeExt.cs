using System;
using System.IO;
using System.Reflection;

namespace Ribbit.Media.Audio;

internal static class EncodeTypeExt
{
    private static readonly string[] exeFiles = new string[6]
    {
        string.Empty,
        "lame.exe",
        "neroAacEnc.exe",
        "opusenc.exe",
        "flac.exe",
        "oggenc2.exe"
    };

    private static readonly string[] extensions = new string[6] { ".wav", ".mp3", ".aac", ".opus", ".flac", ".ogg" };

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
        if (EncoderDirectory != null && Directory.Exists(EncoderDirectory) && File.Exists(Path.Combine(EncoderDirectory, encoderFileName)))
        {
            return EncoderDirectory;
        }
        string directoryName = Path.GetDirectoryName(Assembly.GetExecutingAssembly().Location);
        if (File.Exists(Path.Combine(directoryName, encoderFileName)))
        {
            return directoryName;
        }
        directoryName = Path.Combine(Path.GetDirectoryName(Assembly.GetExecutingAssembly().Location), "libs", Environment.Is64BitProcess ? "x64" : "x86");
        if (File.Exists(Path.Combine(directoryName, encoderFileName)))
        {
            return directoryName;
        }
        directoryName = Path.Combine(Path.GetDirectoryName(Assembly.GetExecutingAssembly().Location), Environment.Is64BitProcess ? "x64" : "x86");
        if (File.Exists(Path.Combine(directoryName, encoderFileName)))
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
