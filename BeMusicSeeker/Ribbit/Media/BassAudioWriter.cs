using System;
using System.IO;
using BeMusicSeeker.Models.Utils;
using ManagedBass;
using ManagedBass.Enc;
using Ribbit.Media.Audio;

namespace Ribbit.Media;

/// <summary>音声をfloat PCMで保持し、指定したencoderへ一度だけ供給するwriterです。</summary>
public class BassAudioWriter : BassAudioPlayer
{
    /// <summary>完成PCMの形式を確認して変換用レンダラーを作ります。</summary>
    internal static AudioPcmRenderer CreatePcmRenderer()
    {
        int outputHandle = outputMixer;
        if (outputHandle == 0)
        {
            throw new InvalidOperationException("The audio output mixer is not initialized.");
        }

        ChannelInfo channelInfo = Bass.ChannelGetInfo(outputHandle);
        if (channelInfo.Frequency <= 0 || channelInfo.Channels <= 0)
        {
            throw new InvalidOperationException("The audio output mixer reported an invalid PCM format.");
        }
        if (!channelInfo.Flags.HasFlag(BassFlags.Float))
        {
            throw new InvalidOperationException("The audio output mixer must supply Float32 PCM.");
        }

        return new AudioPcmRenderer(
            outputHandle,
            channelInfo.Frequency,
            channelInfo.Channels);
    }

    private static AudioEncoderSession encoder;

    /// <summary>現在のencoderが所有する出力pathを取得します。</summary>
    public string OutputFile => encoder?.OutputFile;

    /// <summary>現在の出力に選択したencoder formatを取得します。</summary>
    public static EncoderType Encoder { get; private set; } = EncoderType.WAVE;

    /// <summary>encoderの現在のlifecycle stateを取得します。</summary>
    public static PlayState RecordState { get; protected set; } = PlayState.Stopped;

    /// <summary>command-line encoderを検索するdirectoryを取得または設定します。</summary>
    public static string EncoderDirectory { get; set; } = AppContext.BaseDirectory;

    /// <summary>現在のencoder command lineを取得します。</summary>
    public static string EncoderCommandLine => encoder?.CommandLine;

    /// <summary>現在のencoderに選択したBASSenc flagsを取得します。</summary>
    internal static EncodeFlags EncoderFlags => encoder?.Flags ?? EncodeFlags.Default;

    /// <summary>音声ファイルを読み込むwriterを作成します。</summary>
    public BassAudioWriter(string fileName)
        : base(fileName)
    {
        Volume = 1f;
    }

    /// <summary>同じ曲ロードが所有する音源cacheを使ってwriterを作成します。</summary>
    internal BassAudioWriter(string fileName, AudioSourceCache sourceCache)
        : base(fileName, sourceCache)
    {
        Volume = 1f;
    }

    /// <summary>音声変換用の無音BASS graphを初期化します。</summary>
    public static void Initialize()
    {
        InitializeOwnedSession(out _);
    }

    /// <summary>互換性のために残された初期化overloadです。変換用graphは常にnull deviceを使います。</summary>
    public static void Initialize(DeviceDriver driver = DeviceDriver.WASAPI_EXCLUSIVE, float lParam = 0f)
    {
        InitializeOwnedSession(out _);
    }

    /// <summary>
    /// native資源を取得した時点で所有sessionを公開します。後続初期化に失敗した場合も、
    /// 呼出元が取得済み資源を解放できます。
    /// </summary>
    /// <param name="ownedSession">取得済みnative資源を所有するsessionです。</param>
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

    /// <summary>指定した外部encoderが見つかるかを返します。</summary>
    public static bool IsEncoderAvailable(EncoderType encodeType)
    {
        return GetEncoderDirectory(encodeType) != null;
    }

    /// <summary>encoder開始前にcommand lineへ設定するmetadataを保存します。</summary>
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

    /// <summary>encoderをpause状態で開始し、後続のfloat PCM手動供給を受け付けます。</summary>
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

    /// <summary>WAV encoder sessionを作成します。</summary>
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

    /// <summary>LAME encoder sessionを作成します。</summary>
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
        ReplaceEncoder(CreateSession(
            Encoder,
            GetAvailableOutputFile(filePathWithoutExtension, Encoder.GetEncoderOutputExtension()),
            quality,
            encoderDirectory));
    }

    /// <summary>Nero AAC encoder sessionを作成します。</summary>
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
        ReplaceEncoder(CreateSession(
            Encoder,
            GetAvailableOutputFile(filePathWithoutExtension, Encoder.GetEncoderOutputExtension()),
            quality,
            encoderDirectory));
    }

    /// <summary>Opus encoder sessionを作成します。</summary>
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
        ReplaceEncoder(CreateSession(
            Encoder,
            GetAvailableOutputFile(filePathWithoutExtension, Encoder.GetEncoderOutputExtension()),
            quality,
            encoderDirectory));
    }

    /// <summary>FLAC encoder sessionを作成します。</summary>
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
        ReplaceEncoder(CreateSession(
            Encoder,
            GetAvailableOutputFile(filePathWithoutExtension, Encoder.GetEncoderOutputExtension()),
            quality,
            encoderDirectory));
    }

    /// <summary>Vorbis encoder sessionを作成します。</summary>
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
        ReplaceEncoder(CreateSession(
            Encoder,
            GetAvailableOutputFile(filePathWithoutExtension, Encoder.GetEncoderOutputExtension()),
            quality,
            encoderDirectory));
    }

    private static AudioEncoderSession CreateSession(
        EncoderType encoderType,
        string outputFile,
        float quality,
        string encoderDirectory = "")
    {
        ChannelInfo channelInfo = Bass.ChannelGetInfo(BassAudioPlayer.outputMixer);
        if (channelInfo.Frequency <= 0 || channelInfo.Channels <= 0)
        {
            throw new InvalidOperationException("The output mixer reported an invalid PCM format.");
        }
        if (!channelInfo.Flags.HasFlag(BassFlags.Float))
        {
            throw new InvalidOperationException("The output mixer must supply Float32 PCM.");
        }

        var request = new AudioEncoderCommandRequest(
            encoderType,
            encoderDirectory,
            outputFile,
            channelInfo.Frequency,
            channelInfo.Channels,
            SampleFormat.SAMPLE_FLOAT_32BIT,
            quality,
            AudioTagInfo.Empty,
            BassAudioPlayer.Format);
        return new AudioEncoderSession(BassAudioPlayer.outputMixer, request);
    }

    /// <summary>
    /// 既存の <c> (n)</c> 衝突suffix規約を保ち、未使用の出力pathを返します。
    /// </summary>
    /// <param name="filePathWithoutExtension">拡張子を除いた正規化済み出力pathです。</param>
    /// <param name="extension">先頭にperiodを含むencoder別の拡張子です。</param>
    /// <returns>まだ存在しない最初のpathです。</returns>
    internal static string GetAvailableOutputFile(string filePathWithoutExtension, string extension)
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

    /// <summary>一度renderしたPCMの整数encoder入力範囲を確認します。</summary>
    internal static void ValidateOutputPeakForEncoder(double peak)
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
        if (!encoder.RequiresIntegerInput)
        {
            return;
        }

        if (!double.IsFinite(peak) || peak < 0d)
        {
            throw new ArgumentOutOfRangeException(nameof(peak), "The encoder peak must be finite and non-negative.");
        }
        if (peak > 1d)
        {
            throw new AudioOutputRangeException(peak);
        }
    }

    /// <summary>完成済みinterleaved float PCMをencoder handleへ手動供給します。</summary>
    internal static void WritePcm(float[] interleavedPcm)
    {
        ArgumentNullException.ThrowIfNull(interleavedPcm);
        using BassAudioOperationLease operation = Ribbit.Media.Audio.BassAudioRuntime.EnterAudioOperation();
        if (RecordState != PlayState.Playing)
        {
            throw new InvalidOperationException("Not recording started");
        }
        if (encoder == null)
        {
            throw new InvalidOperationException("Encoder is not set");
        }

        encoder.EncodeWrite(interleavedPcm, 0, interleavedPcm.Length);
    }

    /// <summary>encoderを終了し、native終了失敗時は所有状態を保持します。</summary>
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
    /// 音源sessionを解放する前にwriter所有のencoderを解放します。
    /// native解放が未確認の場合は、再試行できるようencoder ownerを保持します。
    /// </summary>
    /// <returns>encoder handleとmanaged ownerの解放が完了した場合だけtrueを返します。</returns>
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

    private static void ReplaceEncoder(AudioEncoderSession nextEncoder)
    {
        encoder?.Dispose();
        encoder = nextEncoder;
        RecordState = PlayState.Stopped;
    }
}
