using System;
using System.IO;
using System.Linq;
using BeMusicSeeker.Models.Utils;
using Ribbit.Media.Audio;
using Un4seen.Bass;
using Un4seen.Bass.AddOn.Tags;
using Un4seen.Bass.Misc;

namespace Ribbit.Media;

public class BassAudioWriter : BassAudioPlayer
{
    private static readonly byte[] AudioWriterBuffer = new byte[4194304];

    private static BaseEncoder encoder;

    public string OutputFile => encoder?.OutputFile;

    public static EncoderType Encoder { get; private set; } = EncoderType.WAVE;

    public static PlayState RecordState { get; protected set; } = PlayState.Stopped;

    public static string EncoderDirectory { get; set; } = AppContext.BaseDirectory;

    public static string EncoderCommandLine => encoder?.EncoderCommandLine;

    public BassAudioWriter(string fileName)
        : base(fileName, onMemory: false)
    {
    }

    public BassAudioWriter(string fileName, bool onMemory = true)
        : base(fileName, onMemory)
    {
    }

    /// <summary>Initializes the silent BASS graph used by audio conversion.</summary>
    public static void Initialize()
    {
        InitializeOwnedSession();
    }

    /// <summary>Initializes the silent BASS graph used by audio conversion.</summary>
    public static void Initialize(DeviceDriver driver = DeviceDriver.WASAPI_EXCLUSIVE, float lParam = 0f)
    {
        InitializeOwnedSession();
    }

    /// <summary>
    /// Initializes conversion output and returns its scoped lifecycle token.
    /// </summary>
    internal static BassAudioSession InitializeOwnedSession()
    {
        BassAudioPlayer.InitializeOwned(
            DeviceDriver.NULL_DEVICE,
            default,
            0f,
            out BassAudioSession ownedSession);
        return ownedSession;
    }

    private static string GetEncoderDirectory(EncoderType encodeType)
    {
        return encodeType.SearchEncoderBinary(LongPathFileSystem.DirectoryExists(EncoderDirectory) ? EncoderDirectory : null);
    }

    public static bool IsEncoderAvailable(EncoderType encodeType)
    {
        return GetEncoderDirectory(encodeType) != null;
    }

    public static void SetTagInfo(TAG_INFO tagInfo)
    {
        using BassAudioOperationLease operation = Ribbit.Media.Audio.BassNet.EnterAudioOperation();
        if (RecordState != PlayState.Stopped)
        {
            throw new InvalidOperationException("Recording has started already");
        }
        if (encoder == null)
        {
            throw new InvalidOperationException("Encoder is not set");
        }
        encoder.TAGs = tagInfo;
    }

    public static void StartRecording()
    {
        using BassAudioOperationLease operation = Ribbit.Media.Audio.BassNet.EnterAudioOperation();
        if (RecordState != PlayState.Stopped)
        {
            throw new InvalidOperationException("Recording has started already");
        }
        RecordState = ((!encoder.Start(null, IntPtr.Zero, paused: false)) ? PlayState.Stopped : PlayState.Playing);
        if (RecordState != PlayState.Playing)
        {
            BASSError bASSError = Bass.BASS_ErrorGetCode();
            throw new Exception("Encoder.Start failed: " + bASSError);
        }
    }

    public static void CreateEncoderWAV(string filePathWithoutExtension)
    {
        using BassAudioOperationLease operation = Ribbit.Media.Audio.BassNet.EnterAudioOperation();
        Encoder = EncoderType.WAVE;
        if (!BassAudioPlayer.IsInitialized || BassAudioPlayer.DriverType != DeviceDriver.NULL_DEVICE)
        {
            throw new InvalidOperationException("BassAudioWriter is not initialized");
        }
        filePathWithoutExtension = LongPathFileSystem.NormalizePathForStorage(filePathWithoutExtension);
        bool wAV_Use32BitInteger = false;
        int num;
        switch (BassAudioPlayer.Format)
        {
            case SampleFormat.SAMPLE_INT_8BIT:
                num = 8;
                break;
            case SampleFormat.SAMPLE_INT_16BIT:
                num = 16;
                break;
            case SampleFormat.SAMPLE_INT_24BIT:
                num = 24;
                break;
            case SampleFormat.SAMPLE_INT_32BIT:
                num = 32;
                wAV_Use32BitInteger = true;
                break;
            case SampleFormat.SAMPLE_FLOAT_32BIT:
                num = 32;
                break;
            default:
                throw new NotSupportedException("Format must be either 8, 16, 24 or 32 bit integer");
        }
        encoder = new EncoderWAV(BassAudioPlayer.outputMixer)
        {
            InputFile = null,
            WAV_BitsPerSample = num,
            WAV_Use32BitInteger = wAV_Use32BitInteger
        };
        int num2 = 1;
        string text = filePathWithoutExtension;
        while (LongPathFileSystem.EntryExists(filePathWithoutExtension + encoder.DefaultOutputExtension))
        {
            num2++;
            filePathWithoutExtension = text + " (" + num2 + ")";
        }
        encoder.OutputFile = filePathWithoutExtension + encoder.DefaultOutputExtension;
    }

    public static void CreateEncoderLAME(string filePathWithoutExtension, float quality = 0.4f)
    {
        using BassAudioOperationLease operation = Ribbit.Media.Audio.BassNet.EnterAudioOperation();
        Encoder = EncoderType.MP3_LAME;
        if (!BassAudioPlayer.IsInitialized || BassAudioPlayer.DriverType != DeviceDriver.NULL_DEVICE)
        {
            throw new InvalidOperationException("BassAudioWriter is not initialized");
        }
        string encoderDirectory = GetEncoderDirectory(Encoder) ?? throw new FileNotFoundException(Encoder.GetEncoderFileName() + " not found");
        filePathWithoutExtension = LongPathFileSystem.NormalizePathForStorage(filePathWithoutExtension);
        quality = System.Math.Max(System.Math.Min(1f, quality), 0.1f);
        encoder = new EncoderLAME(BassAudioPlayer.outputMixer)
        {
            InputFile = null,
            EncoderDirectory = encoderDirectory,
            LAME_UseVBR = true,
            LAME_ReplayGain = EncoderLAME.LAMEReplayGain.Accurate,
            LAME_VBRQuality = (EncoderLAME.LAMEVBRQuality)(10 - (int)(10f * quality))
        };
        int num = 1;
        string text = filePathWithoutExtension;
        while (LongPathFileSystem.EntryExists(filePathWithoutExtension + encoder.DefaultOutputExtension))
        {
            num++;
            filePathWithoutExtension = text + " (" + num + ")";
        }
        encoder.OutputFile = filePathWithoutExtension + encoder.DefaultOutputExtension;
    }

    public static void CreateEncoderNeroAAC(string filePathWithoutExtension, float quality = 0.4f)
    {
        using BassAudioOperationLease operation = Ribbit.Media.Audio.BassNet.EnterAudioOperation();
        Encoder = EncoderType.AAC_NERO;
        if (!BassAudioPlayer.IsInitialized || BassAudioPlayer.DriverType != DeviceDriver.NULL_DEVICE)
        {
            throw new InvalidOperationException("BassAudioWriter is not initialized");
        }
        string encoderDirectory = GetEncoderDirectory(Encoder) ?? throw new FileNotFoundException(Encoder.GetEncoderFileName() + " not found");
        filePathWithoutExtension = LongPathFileSystem.NormalizePathForStorage(filePathWithoutExtension);
        quality = System.Math.Max(System.Math.Min(1f, quality), 0f);
        encoder = new EncoderNeroAAC(BassAudioPlayer.outputMixer)
        {
            InputFile = null,
            EncoderDirectory = encoderDirectory,
            NERO_UseQualityMode = true,
            NERO_Quality = quality
        };
        int num = 1;
        string text = filePathWithoutExtension;
        while (LongPathFileSystem.EntryExists(filePathWithoutExtension + encoder.DefaultOutputExtension))
        {
            num++;
            filePathWithoutExtension = text + " (" + num + ")";
        }
        encoder.OutputFile = filePathWithoutExtension + encoder.DefaultOutputExtension;
    }

    public static void CreateEncoderOPUS(string filePathWithoutExtension, float quality = 0.4f)
    {
        using BassAudioOperationLease operation = Ribbit.Media.Audio.BassNet.EnterAudioOperation();
        Encoder = EncoderType.OPUS;
        if (!BassAudioPlayer.IsInitialized || BassAudioPlayer.DriverType != DeviceDriver.NULL_DEVICE)
        {
            throw new InvalidOperationException("BassAudioWriter is not initialized");
        }
        string encoderDirectory = GetEncoderDirectory(Encoder) ?? throw new FileNotFoundException(Encoder.GetEncoderFileName() + " not found");
        filePathWithoutExtension = LongPathFileSystem.NormalizePathForStorage(filePathWithoutExtension);
        quality = System.Math.Max(System.Math.Min(1f, quality), 0f);
        encoder = new EncoderOPUS(BassAudioPlayer.outputMixer)
        {
            InputFile = null,
            EncoderDirectory = encoderDirectory,
            OPUS_Bitrate = (int)(6f + 250f * quality)
        };
        int num = 1;
        string text = filePathWithoutExtension;
        while (LongPathFileSystem.EntryExists(filePathWithoutExtension + encoder.DefaultOutputExtension))
        {
            num++;
            filePathWithoutExtension = text + " (" + num + ")";
        }
        encoder.OutputFile = filePathWithoutExtension + encoder.DefaultOutputExtension;
    }

    public static void CreateEncoderFLAC(string filePathWithoutExtension, float quality = 0.4f)
    {
        using BassAudioOperationLease operation = Ribbit.Media.Audio.BassNet.EnterAudioOperation();
        Encoder = EncoderType.FLAC;
        if (!BassAudioPlayer.IsInitialized || BassAudioPlayer.DriverType != DeviceDriver.NULL_DEVICE)
        {
            throw new InvalidOperationException("BassAudioWriter is not initialized");
        }
        string encoderDirectory = GetEncoderDirectory(Encoder) ?? throw new FileNotFoundException(Encoder.GetEncoderFileName() + " not found");
        filePathWithoutExtension = LongPathFileSystem.NormalizePathForStorage(filePathWithoutExtension);
        quality = System.Math.Max(System.Math.Min(0.8f, quality), 0f);
        encoder = new EncoderFLAC(BassAudioPlayer.outputMixer)
        {
            InputFile = null,
            EncoderDirectory = encoderDirectory,
            FLAC_ReplayGain = true,
            FLAC_CompressionLevel = (int)(10f * quality)
        };
        int num = 1;
        string text = filePathWithoutExtension;
        while (LongPathFileSystem.EntryExists(filePathWithoutExtension + encoder.DefaultOutputExtension))
        {
            num++;
            filePathWithoutExtension = text + " (" + num + ")";
        }
        encoder.OutputFile = filePathWithoutExtension + encoder.DefaultOutputExtension;
    }

    public static void CreateEncoderOGG(string filePathWithoutExtension, float quality = 0.4f)
    {
        using BassAudioOperationLease operation = Ribbit.Media.Audio.BassNet.EnterAudioOperation();
        Encoder = EncoderType.OGG_VORBIS;
        if (!BassAudioPlayer.IsInitialized || BassAudioPlayer.DriverType != DeviceDriver.NULL_DEVICE)
        {
            throw new InvalidOperationException("BassAudioWriter is not initialized");
        }
        string encoderDirectory = GetEncoderDirectory(Encoder) ?? throw new FileNotFoundException(Encoder.GetEncoderFileName() + " not found");
        filePathWithoutExtension = LongPathFileSystem.NormalizePathForStorage(filePathWithoutExtension);
        quality = System.Math.Max(System.Math.Min(0.8f, quality), 0f);
        encoder = new EncoderOGG(BassAudioPlayer.outputMixer)
        {
            InputFile = null,
            EncoderDirectory = encoderDirectory,
            OGG_UseQualityMode = true,
            OGG_Quality = (int)(10f * quality)
        };
        int num = 1;
        string text = filePathWithoutExtension;
        while (LongPathFileSystem.EntryExists(filePathWithoutExtension + encoder.DefaultOutputExtension))
        {
            num++;
            filePathWithoutExtension = text + " (" + num + ")";
        }
        encoder.OutputFile = filePathWithoutExtension + encoder.DefaultOutputExtension;
    }

    public static void RecordToFile(TimeSpan time)
    {
        using BassAudioOperationLease operation = Ribbit.Media.Audio.BassNet.EnterAudioOperation();
        if (RecordState != PlayState.Playing)
        {
            throw new InvalidOperationException("Not recording started");
        }
        if (time <= TimeSpan.Zero)
        {
            return;
        }
        long num = Bass.BASS_ChannelSeconds2Bytes(BassAudioPlayer.outputMixer, time.TotalSeconds);
        if (num < 0)
        {
            BASSError bASSError = Bass.BASS_ErrorGetCode();
            throw new Exception("BASS_ChannelSeconds2Bytes failed: " + bASSError);
        }
        if (num == 0L)
        {
            return;
        }
        long num2 = num / AudioWriterBuffer.Length;
        long num3 = num % AudioWriterBuffer.Length;
        static void action(int size)
        {
            if (Bass.BASS_ChannelGetData(BassAudioPlayer.outputMixer, AudioWriterBuffer, size) >= 0)
            {
                return;
            }
            BASSError bASSError2 = Bass.BASS_ErrorGetCode();
            throw new Exception("BASS_ChannelGetData failed: " + bASSError2);
        }
        for (int num4 = 0; num4 < num2; num4++)
        {
            action(AudioWriterBuffer.Length);
        }
        if (num3 != 0L)
        {
            action((int)num3);
        }
    }

    public static void StopRecording()
    {
        using BassAudioOperationLease operation = Ribbit.Media.Audio.BassNet.EnterAudioOperation();
        if (RecordState != PlayState.Playing)
        {
            throw new InvalidOperationException("Not recording started");
        }
        RecordState = (encoder.Stop() ? PlayState.Stopped : PlayState.Playing);
    }

    public static float GetLevel(TimeSpan time, bool isRMSVolume = false)
    {
        using BassAudioOperationLease operation = Ribbit.Media.Audio.BassNet.EnterAudioOperation();
        if (RecordState != PlayState.Stopped)
        {
            throw new InvalidOperationException("Recording has started already");
        }
        if (time <= TimeSpan.Zero)
        {
            return 0f;
        }
        long num = Bass.BASS_ChannelSeconds2Bytes(BassAudioPlayer.outputMixer, time.TotalSeconds);
        if (num < 0)
        {
            BASSError bASSError = Bass.BASS_ErrorGetCode();
            throw new Exception("BASS_ChannelSeconds2Bytes failed: " + bASSError);
        }
        if (num == 0L)
        {
            return 0f;
        }
        long num2 = System.Math.Min(AudioWriterBuffer.Length, Bass.BASS_ChannelSeconds2Bytes(BassAudioPlayer.outputMixer, 1.0));
        long num3 = num / num2;
        long num4 = num % num2;
        float func(int size)
        {
            double num7 = Bass.BASS_ChannelBytes2Seconds(BassAudioPlayer.outputMixer, size);
            if (Bass.BASS_ChannelGetData(BassAudioPlayer.outputMixer, AudioWriterBuffer, size) >= 0)
            {
                return Bass.BASS_ChannelGetLevels(BassAudioPlayer.outputMixer, (float)num7, isRMSVolume ? BASSLevel.BASS_LEVEL_RMS : BASSLevel.BASS_LEVEL_ALL).Max();
            }
            BASSError bASSError2 = Bass.BASS_ErrorGetCode();
            throw new Exception("BASS_ChannelGetData failed: " + bASSError2);
        }
        float num5 = 0f;
        for (int num6 = 0; num6 < num3; num6++)
        {
            num5 = System.Math.Max(num5, func((int)num2));
        }
        if (num4 != 0L)
        {
            num5 = System.Math.Max(num5, func((int)num4));
        }
        return num5;
    }
}
