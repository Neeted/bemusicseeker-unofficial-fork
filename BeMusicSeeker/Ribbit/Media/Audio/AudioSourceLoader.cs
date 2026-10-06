#nullable enable annotations
using System;
using System.IO;
using System.Runtime.ExceptionServices;
using System.Runtime.InteropServices;
using ManagedBass;
using Ribbit.Logging;

namespace Ribbit.Media.Audio;

/// <summary>ファイルを拒否した入力処理段階を表します。</summary>
internal enum AudioSourceLoadStage
{
    InspectContainer,
    DecodeVorbis,
    DecodeWithBass,
    CreateBassSource,
    ValidateBassSource
}

/// <summary>パス、段階、native errorを保持して音声入力の失敗を通知します。</summary>
internal sealed class AudioSourceLoadException : IOException
{
    /// <summary>入力元パスと任意のnative error codeを持つ失敗を作成します。</summary>
    internal AudioSourceLoadException(
        AudioSourceLoadStage stage,
        string path,
        string message,
        Exception? innerException = null,
        int? nativeErrorCode = null)
        : base(message, innerException)
    {
        Stage = stage;
        Path = path;
        NativeErrorCode = nativeErrorCode;
    }

    /// <summary>失敗した入力処理段階を取得します。</summary>
    internal AudioSourceLoadStage Stage { get; }

    /// <summary>入力元ファイルのパスを取得します。</summary>
    internal string Path { get; }

    /// <summary>native decoderから返された場合に、そのエラーを取得します。</summary>
    internal int? NativeErrorCode { get; }

    /// <summary>入力の読取り・復号・入力由来容量制限として省略できる失敗かを取得します。</summary>
    internal bool IsInputFailure => Stage is AudioSourceLoadStage.InspectContainer
        or AudioSourceLoadStage.DecodeVorbis
        or AudioSourceLoadStage.DecodeWithBass
        or AudioSourceLoadStage.CreateBassSource;
}

/// <summary>音声環境・worker・native所有の失敗を入力省略へ変換せず通知します。</summary>
internal sealed class AudioSourceFatalException : Exception
{
    /// <summary>fatalな音声処理失敗と、その原因を初期化します。</summary>
    internal AudioSourceFatalException(string message, Exception? innerException = null)
        : base(message, innerException)
    {
    }
}

/// <summary>対応する音声コンテナーを共通の有限float32音源形式へ読み込みます。</summary>
internal static class AudioSourceLoader
{
    private const int MaxBassReadFrames = 16384;
    private const int MaxBassInitialSampleCapacity = 1024 * 1024;

    /// <summary>サンプルレート、レベル、チャンネル順を変えずにファイルを復号します。</summary>
    internal static DecodedAudio Load(string path)
        => LoadWithSession(path).Audio;

    /// <summary>一度読み込んだpath入力のPCMと復号に使ったsessionを一緒に返します。</summary>
    /// <param name="path">読み込む音声pathです。</param>
    /// <param name="openInput">未指定ならpathを開き、指定時はそのstreamを一度だけ読み込みます。</param>
    /// <param name="freeTemporaryDecoder">一時decoderの解放境界です。未指定ならBASSへ解放を依頼します。</param>
    internal static DecodedAudioSessionSource LoadWithSession(
        string path,
        Func<string, Stream>? openInput = null,
        Func<int, bool>? freeTemporaryDecoder = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        BassAudioSession expectedSession;
        using (BassAudioOperationLease operation = BassAudioRuntime.EnterAudioOperation())
        {
            BassAudioSession? session = BassAudioPlayer.CurrentSessionForAdmittedOperation;
            if (session?.State != BassAudioSessionState.Active)
            {
                throw new InvalidOperationException("An active audio session is required to decode a source.");
            }
            expectedSession = session;
        }

        using var input = AudioInputFile.Read(path, openInput);
        DecodedAudio audio = Decode(input, expectedSession, freeTemporaryDecoder);
        return new DecodedAudioSessionSource(audio, expectedSession);
    }

    /// <summary>既に読み込んだ入力を捕捉した音声sessionで復号します。</summary>
    /// <param name="input">一回read済みで、復号後まで所有する入力です。</param>
    /// <param name="expectedSession">読み込み開始時に捕捉したsessionです。</param>
    /// <param name="freeTemporaryDecoder">一時decoderの解放境界です。未指定ならBASSへ解放を依頼します。</param>
    internal static DecodedAudio Decode(
        AudioInputFile input,
        BassAudioSession? expectedSession,
        Func<int, bool>? freeTemporaryDecoder = null)
    {
        ArgumentNullException.ThrowIfNull(input);
        using BassAudioOperationLease operation = BassAudioRuntime.EnterAudioOperation();
        BassAudioSession? session = BassAudioPlayer.CurrentSessionForAdmittedOperation;
        if (session?.State != BassAudioSessionState.Active)
        {
            throw new InvalidOperationException("An active audio session is required to decode a source.");
        }
        if (expectedSession != null
            && (!ReferenceEquals(expectedSession, session) || expectedSession.State != BassAudioSessionState.Active))
        {
            throw new InvalidOperationException("The audio session changed while BMS resources were loading.");
        }
        if (session.CoreDeviceIndex >= 0)
        {
            Bass.CurrentDevice = session.CoreDeviceIndex;
        }

        using Stream signatureInput = input.OpenReadView();
        Span<byte> signature = stackalloc byte[12];
        int signatureLength = 0;
        while (signatureLength < signature.Length)
        {
            int bytesRead = signatureInput.Read(signature[signatureLength..]);
            if (bytesRead == 0)
            {
                break;
            }
            signatureLength += bytesRead;
        }
        bool isOgg = signatureLength >= 4 && signature[..4].SequenceEqual("OggS"u8);
        return isOgg
            ? VorbisDecoder.Decode(input)
            : DecodeWithBass(input, session, freeTemporaryDecoder);
    }

    private static DecodedAudio DecodeWithBass(
        AudioInputFile input,
        BassAudioSession session,
        Func<int, bool>? freeTemporaryDecoder)
    {
        int handle = Bass.CreateStream(
            input.Memory,
            0L,
            input.Length,
            BassFlags.Float | BassFlags.Prescan | BassFlags.Decode);
        if (handle == 0)
        {
            Errors error = Bass.LastError;
            if (IsFatalBassError(error))
            {
                throw new AudioSourceFatalException(
                    "BASS could not create the temporary input decoder: " + BassNativeErrorFormatter.Format(error));
            }
            throw new AudioSourceLoadException(
                AudioSourceLoadStage.DecodeWithBass,
                input.Path,
                "BASS could not decode the audio input as float32.",
                nativeErrorCode: (int)error);
        }

        // 一時decoderも解放確認までセッションに所有させ、失敗時にhandleを失わないようにします。
        try
        {
            session.TrackPlayerStream(handle, input, input.ConfirmNativeRelease);
            input.TransferToSession();
        }
        catch (Exception trackingException)
        {
            bool alreadyOwned = false;
            bool tracked = false;
            Exception? trackingRecoveryFailure = null;
            try
            {
                tracked = session.TryTrackPlayerStreamForCleanup(
                    handle,
                    input,
                    input.ConfirmNativeRelease,
                    out alreadyOwned);
                if (tracked)
                {
                    input.TransferToSession();
                }
            }
            catch (Exception exception)
            {
                trackingRecoveryFailure = exception;
            }

            if (tracked)
            {
                // fallback tracking succeeded; ownership and its release callback are established.
            }
            else if (!alreadyOwned)
            {
                bool trackingReleaseSucceeded = TryReleaseTemporaryDecoder(
                    handle,
                    session: null,
                    out Exception? trackingReleaseFailure,
                    freeTemporaryDecoder);
                if (trackingReleaseSucceeded)
                {
                    input.ConfirmNativeRelease(handle);
                }
                if (trackingReleaseFailure != null)
                {
                    trackingRecoveryFailure = trackingRecoveryFailure == null
                        ? trackingReleaseFailure
                        : new AggregateException(trackingRecoveryFailure, trackingReleaseFailure);
                }
            }
            bool fatalTrackingFailure = IsFatalRuntimeException(trackingException);
            if (trackingRecoveryFailure != null || !tracked || fatalTrackingFailure)
            {
                throw new AudioSourceFatalException(
                    "BASS input decoder ownership could not be confirmed after source tracking failed.",
                    trackingRecoveryFailure == null
                        ? trackingException
                        : new AggregateException(trackingException, trackingRecoveryFailure));
            }
        }
        DecodedAudio? result = null;
        Exception? primaryFailure = null;
        try
        {
            result = DecodeBassStream(handle, input.Path);
        }
        catch (Exception exception)
        {
            primaryFailure = exception;
        }

        bool released = TryReleaseTemporaryDecoder(
            handle,
            session,
            out Exception? releaseFailure,
            freeTemporaryDecoder);

        if (primaryFailure != null)
        {
            if (releaseFailure != null)
            {
                TryLogCleanupFailure(input.Path, releaseFailure);
                throw new AudioSourceFatalException(
                    "The BASS input decoder failed and its native release could not be confirmed.",
                    new AggregateException(primaryFailure, releaseFailure));
            }
            ExceptionDispatchInfo.Capture(primaryFailure).Throw();
        }
        if (!released)
        {
            throw new AudioSourceFatalException(
                "BASS could not confirm release of the temporary input decoder.",
                releaseFailure);
        }

        return result ?? throw new InvalidOperationException("The input decoder returned no PCM.");
    }

    private static bool TryReleaseTemporaryDecoder(
        int handle,
        BassAudioSession? session,
        Func<int, bool>? freeNativeStream = null) =>
        TryReleaseTemporaryDecoder(handle, session, out _, freeNativeStream);

    private static bool TryReleaseTemporaryDecoder(
        int handle,
        BassAudioSession? session,
        out Exception? releaseFailure,
        Func<int, bool>? freeNativeStream = null)
    {
        releaseFailure = null;
        bool released;
        try
        {
            released = freeNativeStream == null
                ? Bass.StreamFree(handle)
                : freeNativeStream(handle);
            if (!released)
            {
                Errors error = Bass.LastError;
                released = error == Errors.Init;
                if (!released)
                {
                    releaseFailure = new InvalidOperationException(
                        "BASS_StreamFree failed for the temporary input decoder: "
                        + BassNativeErrorFormatter.Format(error));
                }
            }
        }
        catch (Exception exception)
        {
            released = false;
            releaseFailure = exception;
        }

        if (released)
        {
            session?.ConfirmPlayerStreamReleased(handle);
        }
        return released;
    }

    private static DecodedAudio DecodeBassStream(int handle, string path)
    {
        ChannelInfo info = Bass.ChannelGetInfo(handle);
        if (info.Frequency <= 0 || info.Channels is < 1 or > 8 || (info.Flags & BassFlags.Float) == 0)
        {
            throw new AudioSourceLoadException(
                AudioSourceLoadStage.DecodeWithBass,
                path,
                "BASS returned an unsupported decoded source format.");
        }

        AudioChannelLayout layout = GetBassChannelLayout(handle, info.Channels, path);
        int frameBytes = checked(layout.ChannelCount * sizeof(float));
        int maxSampleCount = Array.MaxLength - Array.MaxLength % layout.ChannelCount;
        long lengthHint = Bass.ChannelGetLength(handle, PositionFlags.Bytes);
        int initialSampleCount = 0;
        if (lengthHint >= 0)
        {
            initialSampleCount = checked((int)System.Math.Min(
                lengthHint / sizeof(float),
                MaxBassInitialSampleCapacity));
            initialSampleCount -= initialSampleCount % layout.ChannelCount;
        }

        float[] samples = initialSampleCount == 0 ? [] : new float[initialSampleCount];
        float[]? probeFrame = null;
        int samplesWritten = 0;
        while (true)
        {
            if (samplesWritten == maxSampleCount)
            {
                probeFrame ??= new float[layout.ChannelCount];
                if (TryReadBassProbeFrame(handle, path, probeFrame, frameBytes))
                {
                    throw CreateBassPcmCapacityFailure(path);
                }
                break;
            }

            if (samples.Length > 0 && samplesWritten == samples.Length)
            {
                probeFrame ??= new float[layout.ChannelCount];
                if (!TryReadBassProbeFrame(handle, path, probeFrame, frameBytes))
                {
                    break;
                }

                samples = GrowBassBuffer(samples, samplesWritten, maxSampleCount, layout.ChannelCount, path);
                Array.Copy(probeFrame, 0, samples, samplesWritten, layout.ChannelCount);
                samplesWritten += layout.ChannelCount;
                continue;
            }

            if (samples.Length - samplesWritten < layout.ChannelCount)
            {
                samples = GrowBassBuffer(samples, samplesWritten, maxSampleCount, layout.ChannelCount, path);
            }

            int requestFrames = System.Math.Min(
                MaxBassReadFrames,
                (samples.Length - samplesWritten) / layout.ChannelCount);
            int requestBytes = checked(requestFrames * frameBytes);
            int readBytes;
            var pinnedSamples = GCHandle.Alloc(samples, GCHandleType.Pinned);
            try
            {
                long byteOffset = checked((long)samplesWritten * sizeof(float));
                IntPtr destination = new(checked(pinnedSamples.AddrOfPinnedObject().ToInt64() + byteOffset));
                readBytes = Bass.ChannelGetData(handle, destination, requestBytes);
            }
            finally
            {
                pinnedSamples.Free();
            }

            Errors error = Bass.LastError;
            if (readBytes <= 0)
            {
                if (error == Errors.Ended)
                {
                    break;
                }
                if (IsFatalBassError(error))
                {
                    throw new AudioSourceFatalException(
                        "BASS failed while reading decoded input PCM: " + BassNativeErrorFormatter.Format(error));
                }
                throw new AudioSourceLoadException(
                    AudioSourceLoadStage.DecodeWithBass,
                    path,
                    "BASS could not decode the input PCM to its end.",
                    nativeErrorCode: (int)error);
            }
            if (readBytes > requestBytes || readBytes % frameBytes != 0)
            {
                throw new AudioSourceLoadException(
                    AudioSourceLoadStage.DecodeWithBass,
                    path,
                    "BASS returned an incomplete or invalid float32 frame.");
            }

            samplesWritten = checked(samplesWritten + readBytes / sizeof(float));
        }

        try
        {
            return new DecodedAudio(info.Frequency, layout, samples, samplesWritten);
        }
        catch (ArgumentException exception)
        {
            throw new AudioSourceLoadException(
                AudioSourceLoadStage.DecodeWithBass,
                path,
                "The decoded PCM violates the finite float32 source contract.",
                exception);
        }
    }

    private static AudioChannelLayout GetBassChannelLayout(int handle, int channels, string path)
    {
        AudioChannelLayout standard;
        try
        {
            standard = AudioChannelLayout.CreateBassOutput(channels);
        }
        catch (ArgumentOutOfRangeException exception)
        {
            throw new AudioSourceLoadException(
                AudioSourceLoadStage.DecodeWithBass,
                path,
                "BASS returned a channel count without a supported output layout.",
                exception);
        }

        IntPtr formatPointer = Bass.ChannelGetTags(handle, TagType.WaveFormat);
        if (formatPointer == IntPtr.Zero
            || (WaveFormatTag)unchecked((ushort)Marshal.ReadInt16(formatPointer)) != WaveFormatTag.Extensible
            || unchecked((ushort)Marshal.ReadInt16(formatPointer, 16)) < 22)
        {
            return standard;
        }

        uint speakerMask = unchecked((uint)Marshal.ReadInt32(formatPointer, 20));
        try
        {
            var explicitLayout = AudioChannelLayout.CreateWaveMask(speakerMask, channels);
            _ = AudioChannelMatrix.Create(explicitLayout, AudioChannelLayout.CreateBassOutput(channels));
            return explicitLayout;
        }
        catch (ArgumentException)
        {
            return standard;
        }
    }

    private static float[] GrowBassBuffer(
        float[] samples,
        int samplesWritten,
        int maxSampleCount,
        int channelCount,
        string path)
    {
        int currentCapacity = samples.Length;
        if (currentCapacity >= maxSampleCount)
        {
            throw new AudioSourceLoadException(
                AudioSourceLoadStage.DecodeWithBass,
                path,
                "The decoded audio exceeds the managed array size limit.");
        }

        long proposedCapacity = currentCapacity == 0
            ? System.Math.Min((long)MaxBassReadFrames * channelCount, maxSampleCount)
            : System.Math.Min(System.Math.Max((long)currentCapacity * 2, currentCapacity + channelCount), maxSampleCount);
        int nextCapacity = checked((int)proposedCapacity);
        nextCapacity -= nextCapacity % channelCount;
        if (nextCapacity <= samplesWritten)
        {
            throw new AudioSourceLoadException(
                AudioSourceLoadStage.DecodeWithBass,
                path,
                "The decoded audio exceeds the managed array size limit.");
        }

        Array.Resize(ref samples, nextCapacity);
        return samples;
    }

    private static bool TryReadBassProbeFrame(int handle, string path, float[] probeFrame, int frameBytes)
    {
        var pinnedProbe = GCHandle.Alloc(probeFrame, GCHandleType.Pinned);
        int readBytes;
        try
        {
            readBytes = Bass.ChannelGetData(handle, pinnedProbe.AddrOfPinnedObject(), frameBytes);
        }
        finally
        {
            pinnedProbe.Free();
        }

        Errors error = Bass.LastError;
        if (readBytes <= 0 && error == Errors.Ended)
        {
            return false;
        }
        if (readBytes <= 0)
        {
            if (IsFatalBassError(error))
            {
                throw new AudioSourceFatalException(
                    "BASS failed while confirming decoded input EOF: " + BassNativeErrorFormatter.Format(error));
            }
            throw new AudioSourceLoadException(
                AudioSourceLoadStage.DecodeWithBass,
                path,
                "BASS could not confirm decoded input EOF.",
                nativeErrorCode: (int)error);
        }
        if (readBytes != frameBytes)
        {
            throw new AudioSourceLoadException(
                AudioSourceLoadStage.DecodeWithBass,
                path,
                "BASS returned an incomplete or invalid float32 frame.",
                nativeErrorCode: (int)error);
        }
        return true;
    }

    private static AudioSourceLoadException CreateBassPcmCapacityFailure(string path) =>
        new(
            AudioSourceLoadStage.DecodeWithBass,
            path,
            "The decoded audio exceeds the managed array size limit.");

    private static bool IsFatalRuntimeException(Exception exception) => exception is OutOfMemoryException
        or DllNotFoundException
        or BadImageFormatException
        or EntryPointNotFoundException
        or TypeLoadException;

    private static bool IsFatalBassError(Errors error) => error is Errors.Memory
        or Errors.Driver
        or Errors.Handle
        or Errors.Init
        or Errors.Type
        or Errors.Device
        or Errors.Create
        or Errors.Version
        or Errors.Wasapi;

    private static void TryLogCleanupFailure(string path, Exception exception)
    {
        try
        {
            NLogWrapper.GetLogger(nameof(AudioSourceLoader)).Warn(
                "Input decoder cleanup failed after an audio load error. path=" + path
                + " error=" + exception.Message);
        }
        catch
        {
            // 後片付けの診断で主たる復号失敗を置き換えません。
        }
    }
}

/// <summary>一時decoderで復号した音源と、そのnative sourceを作る対象sessionです。</summary>
internal readonly record struct DecodedAudioSessionSource(DecodedAudio Audio, BassAudioSession Session);
