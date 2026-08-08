using System;
using System.IO;
using BeMusicSeeker.Models.Utils;
using ManagedBass;
using ManagedBass.Enc;
using Ribbit.Media.Audio;

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
        if (encoder == null)
        {
            throw new InvalidOperationException("Encoder is not set");
        }
        if (time <= TimeSpan.Zero)
        {
            return;
        }

        new AudioWriterPullRenderer(
            BassAudioPlayer.outputMixer,
            AudioWriterBuffer,
            ManagedBassAudioWriterNative.Instance,
            encoder)
            .Render(time);
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
            if (RecordState == PlayState.Playing && encoder.State == AudioEncoderSessionState.Started)
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
        return new AudioWriterPullRenderer(
            BassAudioPlayer.outputMixer,
            AudioWriterBuffer,
            ManagedBassAudioWriterNative.Instance)
            .GetLevel(time, isRMSVolume);
    }
}

/// <summary>Identifies the native operation that failed during PCM rendering or level scan.</summary>
internal enum AudioWriterRenderStage
{
    SecondsToBytes,
    DataPull,
    BytesToSeconds,
    LevelPull
}

/// <summary>Typed failure raised by the writer's ManagedBass core pull boundary.</summary>
internal sealed class AudioWriterRenderException : Exception
{
    /// <summary>Initializes a writer render failure with its native context.</summary>
    internal AudioWriterRenderException(
        int channel,
        AudioWriterRenderStage stage,
        Errors? nativeError,
        Exception innerException = null)
        : base(
            "Audio writer channel " + channel
            + " failed at " + stage
            + (nativeError.HasValue ? " nativeError=" + nativeError.Value : string.Empty),
            innerException)
    {
        Channel = channel;
        Stage = stage;
        NativeError = nativeError;
    }

    /// <summary>Gets the channel involved in the failed operation.</summary>
    internal int Channel { get; }

    /// <summary>Gets the failed native operation.</summary>
    internal AudioWriterRenderStage Stage { get; }

    /// <summary>Gets the ManagedBass error captured at the native boundary, when available.</summary>
    internal Errors? NativeError { get; }
}

/// <summary>Narrow ManagedBass core boundary owned by the audio writer.</summary>
internal interface IAudioWriterNative
{
    /// <summary>Converts channel seconds to native byte position.</summary>
    long ChannelSeconds2Bytes(int channel, double seconds);

    /// <summary>Converts native byte position to channel seconds.</summary>
    double ChannelBytes2Seconds(int channel, long bytes);

    /// <summary>Pulls PCM data from a channel into the supplied buffer.</summary>
    int ChannelGetData(int channel, byte[] buffer, int length);

    /// <summary>Pulls channel levels for the requested duration.</summary>
    float[] ChannelGetLevel(int channel, float seconds, LevelRetrievalFlags flags);

    /// <summary>Gets the error reported by the most recent native call.</summary>
    Errors LastError { get; }
}

/// <summary>ManagedBass implementation of the writer's narrow native boundary.</summary>
internal sealed class ManagedBassAudioWriterNative : IAudioWriterNative
{
    /// <summary>Gets the process-wide stateless native boundary.</summary>
    internal static ManagedBassAudioWriterNative Instance { get; } = new();

    private ManagedBassAudioWriterNative()
    {
    }

    /// <inheritdoc />
    public long ChannelSeconds2Bytes(int channel, double seconds) => Bass.ChannelSeconds2Bytes(channel, seconds);

    /// <inheritdoc />
    public double ChannelBytes2Seconds(int channel, long bytes) => Bass.ChannelBytes2Seconds(channel, bytes);

    /// <inheritdoc />
    public int ChannelGetData(int channel, byte[] buffer, int length) => Bass.ChannelGetData(channel, buffer, length);

    /// <inheritdoc />
    public float[] ChannelGetLevel(int channel, float seconds, LevelRetrievalFlags flags) =>
        Bass.ChannelGetLevel(channel, seconds, flags);

    /// <inheritdoc />
    public Errors LastError => Bass.LastError;
}

/// <summary>Pulls PCM data and levels while preserving the writer's chunk geometry.</summary>
internal sealed class AudioWriterPullRenderer
{
    private enum DataPullResult
    {
        Full,
        Partial
    }

    private readonly int channel;
    private readonly byte[] buffer;
    private readonly IAudioWriterNative native;
    private readonly AudioEncoderSession encoder;

    /// <summary>Creates a deterministic pull renderer for one source channel.</summary>
    internal AudioWriterPullRenderer(
        int channel,
        byte[] buffer,
        IAudioWriterNative native,
        AudioEncoderSession encoder = null)
    {
        if (channel == 0)
        {
            throw new ArgumentOutOfRangeException(nameof(channel));
        }

        this.channel = channel;
        this.buffer = buffer ?? throw new ArgumentNullException(nameof(buffer));
        if (buffer.Length == 0)
        {
            throw new ArgumentException("The pull buffer must not be empty.", nameof(buffer));
        }

        this.native = native ?? throw new ArgumentNullException(nameof(native));
        this.encoder = encoder;
    }

    /// <summary>Pulls the requested duration and stops at the first partial or natural end.</summary>
    internal void Render(TimeSpan time)
    {
        if (time <= TimeSpan.Zero)
        {
            return;
        }

        long requestedBytes = ConvertSecondsToBytes(time.TotalSeconds);
        if (requestedBytes <= 0)
        {
            return;
        }

        long fullChunks = requestedBytes / buffer.Length;
        int remainder = (int)(requestedBytes % buffer.Length);
        for (long index = 0; index < fullChunks; index++)
        {
            if (PullData(buffer.Length) == DataPullResult.Partial)
            {
                return;
            }
        }

        if (remainder != 0)
        {
            PullData(remainder);
        }
    }

    /// <summary>Scans levels using the existing data-pull then level-pull consumption order.</summary>
    internal float GetLevel(TimeSpan time, bool isRms)
    {
        if (time <= TimeSpan.Zero)
        {
            return 0f;
        }

        long requestedBytes = ConvertSecondsToBytes(time.TotalSeconds);
        if (requestedBytes <= 0)
        {
            return 0f;
        }

        long oneSecondBytes = ConvertSecondsToBytes(1d);
        long levelChunkBytes = System.Math.Min(buffer.Length, oneSecondBytes);
        if (levelChunkBytes <= 0)
        {
            throw new AudioWriterRenderException(channel, AudioWriterRenderStage.SecondsToBytes, nativeError: null);
        }

        LevelRetrievalFlags flags = isRms ? LevelRetrievalFlags.RMS : LevelRetrievalFlags.All;
        long fullChunks = requestedBytes / levelChunkBytes;
        int remainder = (int)(requestedBytes % levelChunkBytes);
        float maximum = 0f;
        for (long index = 0; index < fullChunks; index++)
        {
            if (!PullLevel((int)levelChunkBytes, flags, ref maximum))
            {
                return maximum;
            }
        }

        if (remainder != 0)
        {
            PullLevel(remainder, flags, ref maximum);
        }

        return maximum;
    }

    private DataPullResult PullData(int requestedBytes)
    {
        int actualBytes;
        try
        {
            actualBytes = native.ChannelGetData(channel, buffer, requestedBytes);
        }
        catch (Exception exception)
        {
            throw CreateException(AudioWriterRenderStage.DataPull, exception);
        }

        Errors nativeError = CaptureLastError();
        if (actualBytes < 0)
        {
            if (nativeError == Errors.Ended)
            {
                return DataPullResult.Partial;
            }

            throw CreateException(AudioWriterRenderStage.DataPull, nativeError);
        }

        if (actualBytes > 0)
        {
            encoder?.EnsureActiveAfterRender();
        }

        return actualBytes == requestedBytes ? DataPullResult.Full : DataPullResult.Partial;
    }

    private bool PullLevel(int requestedBytes, LevelRetrievalFlags flags, ref float maximum)
    {
        DataPullResult dataResult = PullData(requestedBytes);
        if (dataResult == DataPullResult.Partial)
        {
            return false;
        }

        float seconds = ConvertBytesToSeconds(requestedBytes);
        float[] levels;
        try
        {
            levels = native.ChannelGetLevel(channel, seconds, flags);
        }
        catch (Exception exception)
        {
            throw CreateException(AudioWriterRenderStage.LevelPull, exception);
        }

        Errors nativeError = CaptureLastError();
        if (levels == null)
        {
            if (nativeError == Errors.Ended)
            {
                return false;
            }

            throw CreateException(AudioWriterRenderStage.LevelPull, nativeError);
        }
        if (levels.Length == 0)
        {
            throw CreateException(AudioWriterRenderStage.LevelPull, nativeError: (Errors?)null);
        }

        foreach (float level in levels)
        {
            maximum = System.Math.Max(maximum, level);
        }

        return true;
    }

    private long ConvertSecondsToBytes(double seconds)
    {
        long bytes;
        try
        {
            bytes = native.ChannelSeconds2Bytes(channel, seconds);
        }
        catch (Exception exception)
        {
            throw CreateException(AudioWriterRenderStage.SecondsToBytes, exception);
        }

        Errors nativeError = CaptureLastError();
        if (bytes < 0)
        {
            throw CreateException(AudioWriterRenderStage.SecondsToBytes, nativeError);
        }

        return bytes;
    }

    private float ConvertBytesToSeconds(int bytes)
    {
        double seconds;
        try
        {
            seconds = native.ChannelBytes2Seconds(channel, bytes);
        }
        catch (Exception exception)
        {
            throw CreateException(AudioWriterRenderStage.BytesToSeconds, exception);
        }

        Errors nativeError = CaptureLastError();
        if (seconds < 0d || double.IsNaN(seconds) || double.IsInfinity(seconds))
        {
            throw CreateException(AudioWriterRenderStage.BytesToSeconds, nativeError);
        }

        return (float)seconds;
    }

    private Errors CaptureLastError()
    {
        try
        {
            return native.LastError;
        }
        catch
        {
            return Errors.Unknown;
        }
    }

    private AudioWriterRenderException CreateException(
        AudioWriterRenderStage stage,
        Exception innerException = null)
    {
        return new AudioWriterRenderException(channel, stage, CaptureLastError(), innerException);
    }

    private AudioWriterRenderException CreateException(
        AudioWriterRenderStage stage,
        Errors? nativeError,
        Exception innerException = null)
    {
        return new AudioWriterRenderException(channel, stage, nativeError, innerException);
    }
}
