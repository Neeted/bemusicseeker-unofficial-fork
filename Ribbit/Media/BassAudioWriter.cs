using System;
using System.IO;
using System.Linq;
using BeMusicSeeker.Models.Utils;
using ManagedBass.Enc;
using Ribbit.Media.Audio;
using Un4seen.Bass;

namespace Ribbit.Media;

public class BassAudioWriter : BassAudioPlayer
{
    private static readonly byte[] AudioWriterBuffer = new byte[4194304];

    private static AudioEncoderSession encoder;

    public string OutputFile => encoder?.OutputFile;

    public static EncoderType Encoder { get; private set; } = EncoderType.WAVE;

    public static PlayState RecordState { get; protected set; } = PlayState.Stopped;

    public static string EncoderDirectory { get; set; } = AppContext.BaseDirectory;

    public static string EncoderCommandLine => encoder?.CommandLine;

    /// <summary>Gets the BASSenc flags selected for the current encoder diagnostics.</summary>
    internal static EncodeFlags EncoderFlags => encoder?.Flags ?? EncodeFlags.Default;

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
        InitializeOwnedSession(out _);
    }

    /// <summary>Initializes the silent BASS graph used by audio conversion.</summary>
    public static void Initialize(DeviceDriver driver = DeviceDriver.WASAPI_EXCLUSIVE, float lParam = 0f)
    {
        InitializeOwnedSession(out _);
    }

    /// <summary>
    /// Initializes conversion output and publishes its scoped lifecycle token as soon as
    /// native ownership is acquired, including when later initialization fails.
    /// </summary>
    /// <param name="ownedSession">The session that owns acquired native resources.</param>
    internal static void InitializeOwnedSession(out BassAudioSession ownedSession)
    {
        BassAudioPlayer.InitializeOwned(
            DeviceDriver.NULL_DEVICE,
            default,
            0f,
            out ownedSession);
    }

    private static string GetEncoderDirectory(EncoderType encodeType)
    {
        return encodeType.SearchEncoderBinary(LongPathFileSystem.DirectoryExists(EncoderDirectory) ? EncoderDirectory : null);
    }

    public static bool IsEncoderAvailable(EncoderType encodeType)
    {
        return GetEncoderDirectory(encodeType) != null;
    }

    /// <summary>Sets immutable metadata used when the encoder command line is rebuilt.</summary>
    public static void SetTagInfo(AudioTagInfo tagInfo)
    {
        using BassAudioOperationLease operation = Ribbit.Media.Audio.BassAudioRuntime.EnterAudioOperation();
        if (RecordState != PlayState.Stopped)
        {
            throw new InvalidOperationException("Recording has started already");
        }
        if (encoder == null)
        {
            throw new InvalidOperationException("Encoder is not set");
        }
        encoder.SetTagInfo(tagInfo);
    }

    public static void StartRecording()
    {
        using BassAudioOperationLease operation = Ribbit.Media.Audio.BassAudioRuntime.EnterAudioOperation();
        if (RecordState != PlayState.Stopped)
        {
            throw new InvalidOperationException("Recording has started already");
        }
        if (encoder == null)
        {
            throw new InvalidOperationException("Encoder is not set");
        }

        try
        {
            encoder.Start();
            RecordState = PlayState.Playing;
        }
        catch
        {
            RecordState = PlayState.Stopped;
            throw;
        }
    }

    public static void CreateEncoderWAV(string filePathWithoutExtension)
    {
        using BassAudioOperationLease operation = Ribbit.Media.Audio.BassAudioRuntime.EnterAudioOperation();
        Encoder = EncoderType.WAVE;
        if (!BassAudioPlayer.IsInitialized || BassAudioPlayer.DriverType != DeviceDriver.NULL_DEVICE)
        {
            throw new InvalidOperationException("BassAudioWriter is not initialized");
        }
        filePathWithoutExtension = LongPathFileSystem.NormalizePathForStorage(filePathWithoutExtension);
        ReplaceEncoder(CreateSession(
            EncoderType.WAVE,
            GetAvailableOutputFile(filePathWithoutExtension, EncoderType.WAVE.GetEncoderOutputExtension()),
            quality: 0f));
    }

    public static void CreateEncoderLAME(string filePathWithoutExtension, float quality = 0.4f)
    {
        using BassAudioOperationLease operation = Ribbit.Media.Audio.BassAudioRuntime.EnterAudioOperation();
        Encoder = EncoderType.MP3_LAME;
        if (!BassAudioPlayer.IsInitialized || BassAudioPlayer.DriverType != DeviceDriver.NULL_DEVICE)
        {
            throw new InvalidOperationException("BassAudioWriter is not initialized");
        }
        filePathWithoutExtension = LongPathFileSystem.NormalizePathForStorage(filePathWithoutExtension);
        string encoderDirectory = GetEncoderDirectory(Encoder) ?? throw new FileNotFoundException(Encoder.GetEncoderFileName() + " not found");
        ReplaceEncoder(CreateSession(Encoder, GetAvailableOutputFile(filePathWithoutExtension, Encoder.GetEncoderOutputExtension()), quality, encoderDirectory));
    }

    public static void CreateEncoderNeroAAC(string filePathWithoutExtension, float quality = 0.4f)
    {
        using BassAudioOperationLease operation = Ribbit.Media.Audio.BassAudioRuntime.EnterAudioOperation();
        Encoder = EncoderType.AAC_NERO;
        if (!BassAudioPlayer.IsInitialized || BassAudioPlayer.DriverType != DeviceDriver.NULL_DEVICE)
        {
            throw new InvalidOperationException("BassAudioWriter is not initialized");
        }
        filePathWithoutExtension = LongPathFileSystem.NormalizePathForStorage(filePathWithoutExtension);
        string encoderDirectory = GetEncoderDirectory(Encoder) ?? throw new FileNotFoundException(Encoder.GetEncoderFileName() + " not found");
        ReplaceEncoder(CreateSession(Encoder, GetAvailableOutputFile(filePathWithoutExtension, Encoder.GetEncoderOutputExtension()), quality, encoderDirectory));
    }

    public static void CreateEncoderOPUS(string filePathWithoutExtension, float quality = 0.4f)
    {
        using BassAudioOperationLease operation = Ribbit.Media.Audio.BassAudioRuntime.EnterAudioOperation();
        Encoder = EncoderType.OPUS;
        if (!BassAudioPlayer.IsInitialized || BassAudioPlayer.DriverType != DeviceDriver.NULL_DEVICE)
        {
            throw new InvalidOperationException("BassAudioWriter is not initialized");
        }
        filePathWithoutExtension = LongPathFileSystem.NormalizePathForStorage(filePathWithoutExtension);
        string encoderDirectory = GetEncoderDirectory(Encoder) ?? throw new FileNotFoundException(Encoder.GetEncoderFileName() + " not found");
        ReplaceEncoder(CreateSession(Encoder, GetAvailableOutputFile(filePathWithoutExtension, Encoder.GetEncoderOutputExtension()), quality, encoderDirectory));
    }

    public static void CreateEncoderFLAC(string filePathWithoutExtension, float quality = 0.4f)
    {
        using BassAudioOperationLease operation = Ribbit.Media.Audio.BassAudioRuntime.EnterAudioOperation();
        Encoder = EncoderType.FLAC;
        if (!BassAudioPlayer.IsInitialized || BassAudioPlayer.DriverType != DeviceDriver.NULL_DEVICE)
        {
            throw new InvalidOperationException("BassAudioWriter is not initialized");
        }
        filePathWithoutExtension = LongPathFileSystem.NormalizePathForStorage(filePathWithoutExtension);
        string encoderDirectory = GetEncoderDirectory(Encoder) ?? throw new FileNotFoundException(Encoder.GetEncoderFileName() + " not found");
        ReplaceEncoder(CreateSession(Encoder, GetAvailableOutputFile(filePathWithoutExtension, Encoder.GetEncoderOutputExtension()), quality, encoderDirectory));
    }

    public static void CreateEncoderOGG(string filePathWithoutExtension, float quality = 0.4f)
    {
        using BassAudioOperationLease operation = Ribbit.Media.Audio.BassAudioRuntime.EnterAudioOperation();
        Encoder = EncoderType.OGG_VORBIS;
        if (!BassAudioPlayer.IsInitialized || BassAudioPlayer.DriverType != DeviceDriver.NULL_DEVICE)
        {
            throw new InvalidOperationException("BassAudioWriter is not initialized");
        }
        filePathWithoutExtension = LongPathFileSystem.NormalizePathForStorage(filePathWithoutExtension);
        string encoderDirectory = GetEncoderDirectory(Encoder) ?? throw new FileNotFoundException(Encoder.GetEncoderFileName() + " not found");
        ReplaceEncoder(CreateSession(Encoder, GetAvailableOutputFile(filePathWithoutExtension, Encoder.GetEncoderOutputExtension()), quality, encoderDirectory));
    }

    private static AudioEncoderSession CreateSession(
        EncoderType encoderType,
        string outputFile,
        float quality,
        string encoderDirectory = "")
    {
        ManagedBass.ChannelInfo channelInfo = ManagedBass.Bass.ChannelGetInfo(BassAudioPlayer.outputMixer);
        SampleFormat sourceFormat = channelInfo.Flags.HasFlag(ManagedBass.BassFlags.Float)
            ? SampleFormat.SAMPLE_FLOAT_32BIT
            : SampleFormat.SAMPLE_INT_16BIT;

        var request = new AudioEncoderCommandRequest(
            encoderType,
            encoderDirectory,
            outputFile,
            channelInfo.Frequency,
            channelInfo.Channels,
            sourceFormat,
            quality,
            AudioTagInfo.Empty,
            BassAudioPlayer.Format);
        return new AudioEncoderSession(BassAudioPlayer.outputMixer, request);
    }

    private static string GetAvailableOutputFile(string filePathWithoutExtension, string extension)
    {
        string originalPath = filePathWithoutExtension;
        int suffix = 1;
        string outputFile = originalPath + extension;
        while (LongPathFileSystem.EntryExists(outputFile))
        {
            suffix++;
            outputFile = originalPath + " (" + suffix + ")" + extension;
        }

        return outputFile;
    }

    private static void ReplaceEncoder(AudioEncoderSession nextEncoder)
    {
        encoder?.Dispose();
        encoder = nextEncoder;
        RecordState = PlayState.Stopped;
    }

    public static void RecordToFile(TimeSpan time)
    {
        using BassAudioOperationLease operation = Ribbit.Media.Audio.BassAudioRuntime.EnterAudioOperation();
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
        using BassAudioOperationLease operation = Ribbit.Media.Audio.BassAudioRuntime.EnterAudioOperation();
        if (RecordState != PlayState.Playing)
        {
            throw new InvalidOperationException("Not recording started");
        }
        if (encoder == null)
        {
            throw new InvalidOperationException("Encoder is not set");
        }

        encoder.Stop();
        RecordState = PlayState.Stopped;
    }

    /// <summary>
    /// Attempts to release the writer-owned encoder before its source audio session is freed.
    /// </summary>
    /// <returns><see langword="true" /> only after the encoder handle and managed owner are released.</returns>
    internal static bool TryReleaseEncoder()
    {
        if (encoder == null)
        {
            return true;
        }

        using BassAudioOperationLease operation = Ribbit.Media.Audio.BassAudioRuntime.EnterAudioOperation();
        try
        {
            if (RecordState == PlayState.Playing)
            {
                encoder.Stop();
                RecordState = PlayState.Stopped;
            }

            encoder.Dispose();
            encoder = null;
            RecordState = PlayState.Stopped;
            return true;
        }
        catch
        {
            return false;
        }
    }

    public static float GetLevel(TimeSpan time, bool isRMSVolume = false)
    {
        using BassAudioOperationLease operation = Ribbit.Media.Audio.BassAudioRuntime.EnterAudioOperation();
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
