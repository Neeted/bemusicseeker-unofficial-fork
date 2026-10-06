#nullable enable annotations
using System;
using System.Runtime.InteropServices;

namespace Ribbit.Media.Audio;

/// <summary>同梱のバージョン固定native bridgeを使って有限のOgg Vorbisを復号します。</summary>
internal static class VorbisDecoder
{
    private const int RequiredAbiVersion = 1;
    private const int NativeReadFrames = 8192;
    private const int MaximumInitialSampleCapacity = 1024 * 1024;
    private static readonly object ApiSync = new();
    private static NativeApi? api;

    /// <summary>同梱bridgeが報告するビルド情報を取得します。</summary>
    internal static string NativeBuildInfo => GetNativeApi().BuildInfo;

    /// <summary>完全なOgg Vorbisファイルを共通のインターリーブfloat32形式へ復号します。</summary>
    internal static DecodedAudio Decode(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        using var input = AudioInputFile.Read(path);
        return Decode(input);
    }

    /// <summary>一回読み込んだOgg入力を閉じるまで同じnative memoryから復号します。</summary>
    internal static DecodedAudio Decode(AudioInputFile input)
    {
        ArgumentNullException.ThrowIfNull(input);
        string path = input.Path;
        using BassAudioOperationLease operation = BassAudioRuntime.EnterAudioOperation();

        NativeApi native = GetNativeApi();
        IntPtr decoder = IntPtr.Zero;
        Exception? primaryFailure = null;
        try
        {
            if (input.Length == 0)
            {
                throw new AudioSourceLoadException(
                    AudioSourceLoadStage.DecodeVorbis,
                    path,
                    "The Ogg input is empty.");
            }
            if (input.Length > Array.MaxLength)
            {
                ValidateEncodedLength(path, input.Length);
            }

            int openStatus = native.OpenMemory(
                input.Memory,
                checked((ulong)input.Length),
                out decoder,
                out int nativeError);
            if (openStatus != 0 || decoder == IntPtr.Zero)
            {
                throw CreateNativeFailure(path, openStatus, nativeError, "The Ogg container or Vorbis stream could not be opened.");
            }

            int infoStatus = native.GetInfo(
                decoder,
                out int sampleRate,
                out int channels,
                out long expectedFrames,
                out int links);
            if (infoStatus != 0)
            {
                throw CreateNativeFailure(path, infoStatus, 0, "The Vorbis source format could not be read.");
            }
            if (sampleRate <= 0 || channels is < 1 or > 8 || links <= 0)
            {
                throw new AudioSourceLoadException(
                    AudioSourceLoadStage.DecodeVorbis,
                    path,
                    "The Vorbis source format is invalid or unsupported.");
            }

            AudioChannelLayout layout;
            try
            {
                layout = AudioChannelLayout.CreateVorbis(channels);
            }
            catch (ArgumentOutOfRangeException exception)
            {
                throw new AudioSourceLoadException(
                    AudioSourceLoadStage.DecodeVorbis,
                    path,
                    "The Vorbis channel layout is unsupported.",
                    exception);
            }

            int maxSampleCount = Array.MaxLength - Array.MaxLength % channels;
            int initialSampleCount = expectedFrames > 0
                ? checked((int)System.Math.Min(expectedFrames, MaximumInitialSampleCapacity / channels) * channels)
                : 0;
            float[] samples = initialSampleCount == 0 ? [] : new float[initialSampleCount];
            float[]? probeFrame = null;
            int samplesWritten = 0;
            while (true)
            {
                if (samplesWritten == maxSampleCount)
                {
                    probeFrame ??= new float[channels];
                    if (ReadProbeFrame(native, decoder, probeFrame, path) > 0)
                    {
                        throw new AudioSourceLoadException(
                            AudioSourceLoadStage.DecodeVorbis,
                            path,
                            "The decoded Vorbis audio exceeds the managed array size limit.");
                    }
                    break;
                }

                if (samples.Length > 0 && samplesWritten == samples.Length)
                {
                    probeFrame ??= new float[channels];
                    if (ReadProbeFrame(native, decoder, probeFrame, path) == 0)
                    {
                        break;
                    }

                    samples = GrowBuffer(samples, samplesWritten, maxSampleCount, channels, path);
                    Array.Copy(probeFrame, 0, samples, samplesWritten, channels);
                    samplesWritten += channels;
                    continue;
                }

                if (samples.Length - samplesWritten < channels)
                {
                    samples = GrowBuffer(samples, samplesWritten, maxSampleCount, channels, path);
                }

                int requestFrames = (int)System.Math.Min(
                    NativeReadFrames,
                    (samples.Length - samplesWritten) / channels);
                int sampleOffset = samplesWritten;
                int status = ReadFramesIntoBuffer(
                    native,
                    decoder,
                    samples,
                    sampleOffset,
                    requestFrames,
                    out long readFrames,
                    out nativeError);
                if (status != 0)
                {
                    throw CreateNativeFailure(path, status, nativeError, "Vorbis decoding failed before the end of the file.");
                }
                if (readFrames == 0)
                {
                    break;
                }
                if (readFrames < 0 || readFrames > requestFrames)
                {
                    throw new AudioSourceLoadException(
                        AudioSourceLoadStage.DecodeVorbis,
                        path,
                        "Vorbis returned an invalid decoded frame count.");
                }
                samplesWritten = checked(samplesWritten + checked((int)readFrames * channels));
            }

            try
            {
                return new DecodedAudio(sampleRate, layout, samples, samplesWritten);
            }
            catch (ArgumentException exception)
            {
                throw new AudioSourceLoadException(
                    AudioSourceLoadStage.DecodeVorbis,
                    path,
                    "The decoded Vorbis PCM violates the finite float32 source contract.",
                    exception);
            }
        }
        catch (Exception exception)
        {
            primaryFailure = exception;
            throw;
        }
        finally
        {
            if (decoder != IntPtr.Zero)
            {
                try
                {
                    native.Close(decoder);
                }
                catch (Exception cleanupFailure)
                {
                    throw new AudioSourceFatalException(
                        "The native Vorbis decoder could not be closed.",
                        primaryFailure == null
                            ? cleanupFailure
                            : new AggregateException(primaryFailure, cleanupFailure));
                }
            }
        }
    }

    private static int ReadFramesIntoBuffer(
        NativeApi native,
        IntPtr decoder,
        float[] samples,
        int sampleOffset,
        int requestFrames,
        out long readFrames,
        out int nativeError)
    {
        var pinnedSamples = GCHandle.Alloc(samples, GCHandleType.Pinned);
        try
        {
            long byteOffset = checked((long)sampleOffset * sizeof(float));
            IntPtr destination = new(checked(pinnedSamples.AddrOfPinnedObject().ToInt64() + byteOffset));
            return native.ReadFrames(
                decoder,
                destination,
                requestFrames,
                out readFrames,
                out nativeError);
        }
        finally
        {
            pinnedSamples.Free();
        }
    }

    private static float[] GrowBuffer(
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
                AudioSourceLoadStage.DecodeVorbis,
                path,
                "The decoded Vorbis audio exceeds the managed array size limit.");
        }

        long proposedCapacity = currentCapacity == 0
            ? System.Math.Min((long)NativeReadFrames * channelCount, maxSampleCount)
            : System.Math.Min(System.Math.Max((long)currentCapacity * 2, currentCapacity + channelCount), maxSampleCount);
        int nextCapacity = checked((int)proposedCapacity);
        nextCapacity -= nextCapacity % channelCount;
        if (nextCapacity <= samplesWritten)
        {
            throw new AudioSourceLoadException(
                AudioSourceLoadStage.DecodeVorbis,
                path,
                "The decoded Vorbis audio exceeds the managed array size limit.");
        }

        Array.Resize(ref samples, nextCapacity);
        return samples;
    }

    private static long ReadProbeFrame(
        NativeApi native,
        IntPtr decoder,
        float[] probeFrame,
        string path)
    {
        int status = ReadFramesIntoBuffer(
            native,
            decoder,
            probeFrame,
            sampleOffset: 0,
            requestFrames: 1,
            out long readFrames,
            out int nativeError);
        if (status != 0)
        {
            throw CreateNativeFailure(path, status, nativeError, "Vorbis failed while confirming end of file.");
        }
        if (readFrames is >= 0 and <= 1)
        {
            return readFrames;
        }
        throw new AudioSourceLoadException(
            AudioSourceLoadStage.DecodeVorbis,
            path,
            "Vorbis returned an invalid decoded frame count while confirming end of file.");
    }

    /// <summary>従来のVorbis入力buffer上限を実allocation前に検査します。</summary>
    internal static void ValidateEncodedLength(string path, long length)
    {
        if (length < 0 || length > Array.MaxLength)
        {
            throw new AudioSourceLoadException(
                AudioSourceLoadStage.DecodeVorbis,
                path,
                "The Ogg input exceeds the managed input-buffer size limit.");
        }
    }

    private static NativeApi GetNativeApi()
    {
        NativeApi? current = System.Threading.Volatile.Read(ref api);
        if (current != null)
        {
            return current;
        }

        lock (ApiSync)
        {
            current = api;
            if (current != null)
            {
                return current;
            }

            IntPtr module = BassNativeRuntime.ResolveLoadedLibrary("bms_vorbis.dll");
            current = NativeApi.Create(module);
            api = current;
            return current;
        }
    }

    private static Exception CreateNativeFailure(
        string path,
        int status,
        int nativeError,
        string message)
    {
        string detail = status switch
        {
            2 => "The native Ogg decoder rejected the container.",
            3 => "Unsupported Ogg codec or channel layout.",
            4 => "libvorbis rejected or failed to decode the stream.",
            5 => "A chained Vorbis stream changed its sample rate or channel layout.",
            _ => "The native Vorbis bridge reported an unknown error."
        };
        if (status == 7)
        {
            return new OutOfMemoryException(message + " The native Vorbis bridge ran out of memory.");
        }
        if (status == 1 || status < 1 || status > 7)
        {
            return new AudioSourceFatalException(
                message + " " + detail,
                new InvalidOperationException("Native Vorbis bridge status=" + status + ", error=" + nativeError));
        }
        return new AudioSourceLoadException(
            AudioSourceLoadStage.DecodeVorbis,
            path,
            message + " " + detail,
            nativeErrorCode: nativeError == 0 ? status : nativeError);
    }

    private sealed class NativeApi
    {
        [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
        internal delegate int GetAbiVersionDelegate();

        [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
        internal delegate IntPtr GetBuildInfoDelegate();

        [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
        internal delegate int OpenMemoryDelegate(IntPtr bytes, ulong length, out IntPtr handle, out int nativeError);

        [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
        internal delegate int GetInfoDelegate(
            IntPtr handle,
            out int sampleRate,
            out int channels,
            out long frameCount,
            out int linkCount);

        [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
        internal delegate int ReadFramesDelegate(
            IntPtr handle,
            IntPtr interleaved,
            int capacityFrames,
            out long readFrames,
            out int nativeError);

        [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
        internal delegate void CloseDelegate(IntPtr handle);

        private NativeApi(IntPtr module)
        {
            GetAbiVersionDelegate getAbiVersion = Bind<GetAbiVersionDelegate>(module, "bms_vorbis_get_abi_version");
            GetBuildInfoDelegate getBuildInfo = Bind<GetBuildInfoDelegate>(module, "bms_vorbis_get_build_info");
            OpenMemory = Bind<OpenMemoryDelegate>(module, "bms_vorbis_open_memory");
            GetInfo = Bind<GetInfoDelegate>(module, "bms_vorbis_get_info");
            ReadFrames = Bind<ReadFramesDelegate>(module, "bms_vorbis_read_frames");
            Close = Bind<CloseDelegate>(module, "bms_vorbis_close");

            int actualAbi = getAbiVersion();
            if (actualAbi != RequiredAbiVersion)
            {
                throw new InvalidOperationException(
                    $"The bundled Vorbis bridge ABI is {actualAbi}; expected {RequiredAbiVersion}.");
            }
            IntPtr buildInfoPointer = getBuildInfo();
            BuildInfo = Marshal.PtrToStringAnsi(buildInfoPointer)
                ?? throw new InvalidOperationException("The bundled Vorbis bridge did not report its build metadata.");
            if (!BuildInfo.Contains("libogg=1.3.6", StringComparison.Ordinal)
                || !BuildInfo.Contains("libvorbis=1.3.7", StringComparison.Ordinal)
                || !BuildInfo.Contains("arch=x64", StringComparison.Ordinal)
                || !BuildInfo.Contains("config=Release", StringComparison.Ordinal)
                || !BuildInfo.Contains("crt=static", StringComparison.Ordinal)
                || !BuildInfo.Contains("fp=precise", StringComparison.Ordinal))
            {
                throw new InvalidOperationException("The bundled Vorbis bridge build metadata does not match the pinned input contract: " + BuildInfo);
            }
        }

        internal string BuildInfo { get; }

        internal OpenMemoryDelegate OpenMemory { get; }

        internal GetInfoDelegate GetInfo { get; }

        internal ReadFramesDelegate ReadFrames { get; }

        internal CloseDelegate Close { get; }

        internal static NativeApi Create(IntPtr module) => new(module);

        private static TDelegate Bind<TDelegate>(IntPtr module, string symbol)
            where TDelegate : Delegate
        {
            IntPtr address = NativeLibrary.GetExport(module, symbol);
            return Marshal.GetDelegateForFunctionPointer<TDelegate>(address);
        }
    }
}
