#nullable enable annotations
using System;
using System.Buffers.Binary;
using System.IO;
using System.Runtime.ExceptionServices;
using System.Runtime.InteropServices;
using BeMusicSeeker.Models.Utils;
using ManagedBass;
using Ribbit.Logging;

namespace Ribbit.Media.Audio;

/// <summary>ファイルを拒否した入力処理段階を表します。</summary>
internal enum AudioSourceLoadStage
{
    InspectContainer,
    DecodeVorbis,
    ParseWaveFormat,
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
}

/// <summary>対応する音声コンテナーを共通の有限float32音源形式へ読み込みます。</summary>
internal static class AudioSourceLoader
{
    private const int MaxBassReadFrames = 16384;

    /// <summary>サンプルレート、レベル、チャンネル順を変えずにファイルを復号します。</summary>
    internal static DecodedAudio Load(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        try
        {
            string fullPath = System.IO.Path.GetFullPath(path);
            using FileStream input = LongPathFileSystem.OpenRead(fullPath);
            Span<byte> signature = stackalloc byte[12];
            int signatureLength = 0;
            while (signatureLength < signature.Length)
            {
                int bytesRead = input.Read(signature[signatureLength..]);
                if (bytesRead == 0)
                {
                    break;
                }
                signatureLength += bytesRead;
            }
            bool isOgg = signatureLength >= 4 && signature[..4].SequenceEqual("OggS"u8);
            bool hasWaveSignature = signatureLength >= 12
                && signature[..4].SequenceEqual("RIFF"u8)
                && signature[8..12].SequenceEqual("WAVE"u8);
            if (isOgg)
            {
                return VorbisDecoder.Decode(fullPath);
            }

            WaveFormat? waveFormat = hasWaveSignature ? WaveFormat.Read(fullPath) : null;
            return DecodeWithBass(fullPath, waveFormat);
        }
        catch (AudioSourceLoadException)
        {
            throw;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or ArgumentException)
        {
            throw new AudioSourceLoadException(
                AudioSourceLoadStage.InspectContainer,
                path,
                "The audio input could not be inspected.",
                exception);
        }
    }

    private static DecodedAudio DecodeWithBass(string path, WaveFormat? waveFormat)
    {
        using BassAudioOperationLease operation = BassAudioRuntime.EnterAudioOperation();
        BassAudioSession session = BassAudioPlayer.ActiveSession
            ?? throw new InvalidOperationException("An audio session is required to decode a source.");
        int handle = Bass.CreateStream(
            LongPathFileSystem.ToExtendedPath(path),
            0L,
            0L,
            BassFlags.Float | BassFlags.Prescan | BassFlags.Decode);
        if (handle == 0)
        {
            Errors error = Bass.LastError;
            throw new AudioSourceLoadException(
                AudioSourceLoadStage.DecodeWithBass,
                path,
                "BASS could not decode the audio input as float32.",
                nativeErrorCode: (int)error);
        }

        // 一時decoderも解放確認までセッションに所有させ、失敗時にhandleを失わないようにします。
        session.TrackPlayerStream(handle, session, static _ => { });
        DecodedAudio? result = null;
        Exception? primaryFailure = null;
        try
        {
            result = DecodeBassStream(handle, path, waveFormat);
        }
        catch (Exception exception)
        {
            primaryFailure = exception;
        }

        bool released = false;
        Exception? releaseFailure = null;
        try
        {
            released = Bass.StreamFree(handle);
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
            releaseFailure = exception;
        }

        if (released)
        {
            session.ConfirmPlayerStreamReleased(handle);
        }

        if (primaryFailure != null)
        {
            if (releaseFailure != null)
            {
                primaryFailure.Data["AudioSourceCleanupFailure"] = releaseFailure;
                TryLogCleanupFailure(path, releaseFailure);
            }
            ExceptionDispatchInfo.Capture(primaryFailure).Throw();
        }
        if (!released)
        {
            throw new AudioSourceLoadException(
                AudioSourceLoadStage.DecodeWithBass,
                path,
                "BASS could not release the temporary input decoder.",
                releaseFailure);
        }

        return result ?? throw new InvalidOperationException("The input decoder returned no PCM.");
    }

    private static DecodedAudio DecodeBassStream(int handle, string path, WaveFormat? waveFormat)
    {
        ChannelInfo info;
        try
        {
            info = Bass.ChannelGetInfo(handle);
        }
        catch (Exception exception)
        {
            throw new AudioSourceLoadException(
                AudioSourceLoadStage.DecodeWithBass,
                path,
                "BASS could not report the decoded source format.",
                exception,
                (int)Bass.LastError);
        }

        if (info.Frequency <= 0 || info.Channels <= 0 || (info.Flags & BassFlags.Float) == 0)
        {
            throw new AudioSourceLoadException(
                AudioSourceLoadStage.DecodeWithBass,
                path,
                "BASS did not expose a valid float32 source format.");
        }

        AudioChannelLayout layout = waveFormat?.ChannelLayout
            ?? CreateNonWaveLayout(info.Channels, path);
        if (layout.ChannelCount != info.Channels)
        {
            throw new AudioSourceLoadException(
                AudioSourceLoadStage.DecodeWithBass,
                path,
                "The declared WAVE layout does not match the format returned by BASS.");
        }
        if (waveFormat != null && waveFormat.SampleRate != info.Frequency)
        {
            throw new AudioSourceLoadException(
                AudioSourceLoadStage.DecodeWithBass,
                path,
                "The WAVE metadata does not match the decoded sample rate.");
        }

        long decodedLengthBytes = Bass.ChannelGetLength(handle, PositionFlags.Bytes);
        if (decodedLengthBytes < 0
            || decodedLengthBytes % sizeof(float) != 0
            || decodedLengthBytes % checked(layout.ChannelCount * sizeof(float)) != 0)
        {
            Errors error = Bass.LastError;
            throw new AudioSourceLoadException(
                AudioSourceLoadStage.DecodeWithBass,
                path,
                "BASS returned an invalid or unaligned decoded length.",
                nativeErrorCode: (int)error);
        }

        long sampleCountLong = decodedLengthBytes / sizeof(float);
        if (sampleCountLong > Array.MaxLength)
        {
            throw new AudioSourceLoadException(
                AudioSourceLoadStage.DecodeWithBass,
                path,
                "The decoded audio exceeds the managed array size limit.");
        }
        long frameCount = sampleCountLong / layout.ChannelCount;
        if (waveFormat != null && frameCount != waveFormat.FrameCount)
        {
            throw new AudioSourceLoadException(
                AudioSourceLoadStage.DecodeWithBass,
                path,
                "The decoded WAVE frame count does not match its data chunk.");
        }

        float[] samples = new float[(int)sampleCountLong];
        int samplesWritten = 0;
        GCHandle pinnedSamples = default;
        try
        {
            if (samples.Length > 0)
            {
                pinnedSamples = GCHandle.Alloc(samples, GCHandleType.Pinned);
                IntPtr baseAddress = pinnedSamples.AddrOfPinnedObject();
                while (samplesWritten < samples.Length)
                {
                    int remainingSamples = samples.Length - samplesWritten;
                    int requestFrames = System.Math.Min(MaxBassReadFrames, remainingSamples / layout.ChannelCount);
                    int requestBytes = checked(requestFrames * layout.ChannelCount * sizeof(float));
                    long byteOffset = checked((long)samplesWritten * sizeof(float));
                    IntPtr destination = new(checked(baseAddress.ToInt64() + byteOffset));
                    int readBytes = Bass.ChannelGetData(handle, destination, requestBytes);
                    if (readBytes <= 0)
                    {
                        Errors error = Bass.LastError;
                        throw new AudioSourceLoadException(
                            AudioSourceLoadStage.DecodeWithBass,
                            path,
                            "BASS reached the end of the input before its declared frame count.",
                            nativeErrorCode: (int)error);
                    }
                    if (readBytes % checked(layout.ChannelCount * sizeof(float)) != 0
                        || readBytes > requestBytes)
                    {
                        throw new AudioSourceLoadException(
                            AudioSourceLoadStage.DecodeWithBass,
                            path,
                            "BASS returned a partial or invalid float32 frame.");
                    }
                    samplesWritten = checked(samplesWritten + readBytes / sizeof(float));
                }

                ConfirmBassEndOfInput(handle, path, layout.ChannelCount);
            }
        }
        finally
        {
            if (pinnedSamples.IsAllocated)
            {
                pinnedSamples.Free();
            }
        }

        if (samples.Length == 0)
        {
            ConfirmBassEndOfInput(handle, path, layout.ChannelCount);
        }

        try
        {
            return new DecodedAudio(info.Frequency, layout, samples);
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

    private static void ConfirmBassEndOfInput(int handle, string path, int channelCount)
    {
        float[] endProbe = new float[channelCount];
        var pinnedProbe = GCHandle.Alloc(endProbe, GCHandleType.Pinned);
        try
        {
            int endBytes = Bass.ChannelGetData(
                handle,
                pinnedProbe.AddrOfPinnedObject(),
                checked(channelCount * sizeof(float)));
            Errors error = Bass.LastError;
            if (endBytes > 0)
            {
                throw new AudioSourceLoadException(
                    AudioSourceLoadStage.DecodeWithBass,
                    path,
                    "BASS decoded more frames than the source length reported.");
            }
            if ((endBytes == 0 || endBytes == -1) && error == Errors.Ended)
            {
                return;
            }

            throw new AudioSourceLoadException(
                AudioSourceLoadStage.DecodeWithBass,
                path,
                "BASS did not confirm the end of input after its declared frame count.",
                nativeErrorCode: (int)error);
        }
        finally
        {
            pinnedProbe.Free();
        }
    }

    private static AudioChannelLayout CreateNonWaveLayout(int channels, string path)
    {
        try
        {
            return AudioChannelLayout.CreateStandard(channels);
        }
        catch (ArgumentOutOfRangeException exception)
        {
            throw new AudioSourceLoadException(
                AudioSourceLoadStage.DecodeWithBass,
                path,
                "The input channel count has no explicit speaker layout.",
                exception);
        }
    }

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

    private sealed class WaveFormat
    {
        private static readonly byte[] PcmSubFormat =
        [0x01, 0x00, 0x00, 0x00, 0x00, 0x00, 0x10, 0x00, 0x80, 0x00, 0x00, 0xAA, 0x00, 0x38, 0x9B, 0x71];

        private static readonly byte[] FloatSubFormat =
        [0x03, 0x00, 0x00, 0x00, 0x00, 0x00, 0x10, 0x00, 0x80, 0x00, 0x00, 0xAA, 0x00, 0x38, 0x9B, 0x71];

        private WaveFormat(int sampleRate, long frameCount, AudioChannelLayout channelLayout)
        {
            SampleRate = sampleRate;
            FrameCount = frameCount;
            ChannelLayout = channelLayout;
        }

        internal int SampleRate { get; }

        internal long FrameCount { get; }

        internal AudioChannelLayout ChannelLayout { get; }

        internal static WaveFormat Read(string path)
        {
            try
            {
                using FileStream stream = LongPathFileSystem.OpenRead(path);
                using var reader = new BinaryReader(stream);
                if (stream.Length < 12)
                {
                    throw new InvalidDataException("The RIFF header is truncated.");
                }

                stream.Position = 4;
                uint riffSize = reader.ReadUInt32();
                long riffEnd = checked((long)riffSize + 8);
                if (riffSize < 4 || riffEnd > stream.Length)
                {
                    throw new InvalidDataException("The RIFF length exceeds the file boundary.");
                }

                byte[] format = [];
                long? dataLength = null;
                long cursor = 12;
                while (cursor < riffEnd)
                {
                    if (riffEnd - cursor < 8)
                    {
                        throw new InvalidDataException("The RIFF chunk header is truncated.");
                    }
                    stream.Position = cursor;
                    string chunkId = System.Text.Encoding.ASCII.GetString(reader.ReadBytes(4));
                    uint chunkSize = reader.ReadUInt32();
                    long chunkDataStart = checked(cursor + 8);
                    long chunkEnd = checked(chunkDataStart + chunkSize);
                    long paddedChunkEnd = checked(chunkEnd + (chunkSize & 1));
                    if (paddedChunkEnd > riffEnd)
                    {
                        throw new InvalidDataException("A RIFF chunk extends beyond the declared container.");
                    }

                    if (chunkId == "fmt ")
                    {
                        if (format.Length != 0 || chunkSize < 16 || chunkSize > 4096)
                        {
                            throw new InvalidDataException("The WAVE format chunk has an unsupported size or duplicate.");
                        }
                        format = reader.ReadBytes(checked((int)chunkSize));
                        if (format.Length != chunkSize)
                        {
                            throw new InvalidDataException("The WAVE format chunk is truncated.");
                        }
                    }
                    else if (chunkId == "data")
                    {
                        if (dataLength.HasValue)
                        {
                            throw new InvalidDataException("Multiple WAVE data chunks are not supported.");
                        }
                        dataLength = chunkSize;
                    }

                    cursor = paddedChunkEnd;
                }

                if (format.Length == 0 || !dataLength.HasValue)
                {
                    throw new InvalidDataException("The WAVE container must contain one format and one data chunk.");
                }

                ushort formatTag = BinaryPrimitives.ReadUInt16LittleEndian(format);
                int channels = BinaryPrimitives.ReadUInt16LittleEndian(format.AsSpan(2));
                uint sampleRateValue = BinaryPrimitives.ReadUInt32LittleEndian(format.AsSpan(4));
                uint averageBytesPerSecond = BinaryPrimitives.ReadUInt32LittleEndian(format.AsSpan(8));
                ushort blockAlign = BinaryPrimitives.ReadUInt16LittleEndian(format.AsSpan(12));
                ushort containerBits = BinaryPrimitives.ReadUInt16LittleEndian(format.AsSpan(14));
                ushort validBits = containerBits;
                uint channelMask = 0;
                bool isFloat = formatTag == 3;

                if (formatTag == 0xFFFE)
                {
                    if (format.Length < 40 || BinaryPrimitives.ReadUInt16LittleEndian(format.AsSpan(16)) < 22)
                    {
                        throw new InvalidDataException("The WAVE extensible format chunk is incomplete.");
                    }
                    validBits = BinaryPrimitives.ReadUInt16LittleEndian(format.AsSpan(18));
                    channelMask = BinaryPrimitives.ReadUInt32LittleEndian(format.AsSpan(20));
                    ReadOnlySpan<byte> subFormat = format.AsSpan(24, 16);
                    isFloat = subFormat.SequenceEqual(FloatSubFormat);
                    bool isPcm = subFormat.SequenceEqual(PcmSubFormat);
                    if (!isFloat && !isPcm)
                    {
                        throw new InvalidDataException("The WAVE extensible subformat is not PCM or IEEE float.");
                    }
                }
                else if (formatTag != 1 && formatTag != 3)
                {
                    throw new InvalidDataException("The WAVE format is not uncompressed PCM or IEEE float.");
                }

                if (channels <= 0 || sampleRateValue == 0 || sampleRateValue > int.MaxValue)
                {
                    throw new InvalidDataException("The WAVE sample rate or channel count is invalid.");
                }
                if (validBits == 0 || validBits > containerBits
                    || (validBits != containerBits && !(containerBits == 32 && validBits == 24)))
                {
                    throw new InvalidDataException("The WAVE valid-bit count is unsupported.");
                }
                if (isFloat && (containerBits is not (32 or 64) || validBits != containerBits))
                {
                    throw new InvalidDataException("IEEE float WAVE input must use 32-bit or 64-bit samples.");
                }
                if (!isFloat && containerBits is not (8 or 16 or 24 or 32))
                {
                    throw new InvalidDataException("PCM WAVE input must use 8-, 16-, 24-, or 32-bit samples.");
                }

                long expectedBlockAlign = checked((long)channels * containerBits / 8);
                long expectedAverageBytes = checked((long)sampleRateValue * expectedBlockAlign);
                if (expectedBlockAlign != blockAlign || expectedAverageBytes != averageBytesPerSecond
                    || dataLength.Value % blockAlign != 0)
                {
                    throw new InvalidDataException("The WAVE byte rate, block alignment, or data length is inconsistent.");
                }

                AudioChannelLayout layout;
                try
                {
                    layout = formatTag == 0xFFFE
                        ? AudioChannelLayout.CreateWaveMask(channelMask, channels)
                        : AudioChannelLayout.CreateStandard(channels);
                }
                catch (ArgumentException exception)
                {
                    throw new InvalidDataException("The WAVE channel layout is unknown or inconsistent.", exception);
                }

                return new WaveFormat((int)sampleRateValue, dataLength.Value / blockAlign, layout);
            }
            catch (AudioSourceLoadException)
            {
                throw;
            }
            catch (Exception exception) when (exception is InvalidDataException or IOException or OverflowException or ArgumentException)
            {
                throw new AudioSourceLoadException(
                    AudioSourceLoadStage.ParseWaveFormat,
                    path,
                    "The WAVE input has invalid or unsupported format metadata.",
                    exception);
            }
        }
    }
}
