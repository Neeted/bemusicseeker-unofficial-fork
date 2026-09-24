#nullable enable annotations
using System;
using System.IO;
using System.Runtime.InteropServices;
using BeMusicSeeker.Models.Utils;

namespace Ribbit.Media.Audio;

/// <summary>同梱のバージョン固定native bridgeを使って有限のOgg Vorbisを復号します。</summary>
internal static class VorbisDecoder
{
    private const int RequiredAbiVersion = 1;
    private const int NativeReadFrames = 8192;
    private static readonly object ApiSync = new();
    private static NativeApi? api;

    /// <summary>同梱bridgeが報告するビルド情報を取得します。</summary>
    internal static string NativeBuildInfo => GetNativeApi().BuildInfo;

    /// <summary>完全なOgg Vorbisファイルを共通のインターリーブfloat32形式へ復号します。</summary>
    internal static DecodedAudio Decode(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        using BassAudioOperationLease operation = BassAudioRuntime.EnterAudioOperation();
        byte[] encoded;
        try
        {
            using FileStream input = LongPathFileSystem.OpenRead(path);
            if (input.Length > Array.MaxLength)
            {
                throw new AudioSourceLoadException(
                    AudioSourceLoadStage.DecodeVorbis,
                    path,
                    "The Ogg input exceeds the managed input-buffer size limit.");
            }
            encoded = new byte[checked((int)input.Length)];
            input.ReadExactly(encoded);
        }
        catch (AudioSourceLoadException)
        {
            throw;
        }
        catch (Exception exception) when (exception is System.IO.IOException or UnauthorizedAccessException or OverflowException)
        {
            throw new AudioSourceLoadException(
                AudioSourceLoadStage.DecodeVorbis,
                path,
                "The Ogg input could not be read completely.",
                exception);
        }

        NativeApi native = GetNativeApi();
        GCHandle pinnedInput = default;
        IntPtr decoder = IntPtr.Zero;
        try
        {
            if (encoded.Length == 0)
            {
                throw new AudioSourceLoadException(
                    AudioSourceLoadStage.DecodeVorbis,
                    path,
                    "The Ogg input is empty.");
            }

            pinnedInput = GCHandle.Alloc(encoded, GCHandleType.Pinned);
            int openStatus = native.OpenMemory(
                pinnedInput.AddrOfPinnedObject(),
                checked((ulong)encoded.LongLength),
                out decoder,
                out int nativeError);
            if (openStatus != 0 || decoder == IntPtr.Zero)
            {
                throw CreateNativeFailure(path, openStatus, nativeError, "The Ogg container or Vorbis stream is invalid.");
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
            if (sampleRate <= 0 || channels is < 1 or > 8 || expectedFrames < 0 || links <= 0)
            {
                throw new AudioSourceLoadException(
                    AudioSourceLoadStage.DecodeVorbis,
                    path,
                    "The Vorbis source format or frame count is invalid.");
            }

            long sampleCountLong;
            try
            {
                sampleCountLong = checked(expectedFrames * channels);
            }
            catch (OverflowException exception)
            {
                throw new AudioSourceLoadException(
                    AudioSourceLoadStage.DecodeVorbis,
                    path,
                    "The decoded Vorbis source exceeds the managed array size limit.",
                    exception);
            }
            if (sampleCountLong > Array.MaxLength)
            {
                throw new AudioSourceLoadException(
                    AudioSourceLoadStage.DecodeVorbis,
                    path,
                    "The decoded Vorbis source exceeds the managed array size limit.");
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

            float[] samples = new float[(int)sampleCountLong];
            long framesRead = 0;
            if (samples.Length > 0)
            {
                var pinnedSamples = GCHandle.Alloc(samples, GCHandleType.Pinned);
                try
                {
                    IntPtr baseAddress = pinnedSamples.AddrOfPinnedObject();
                    while (framesRead < expectedFrames)
                    {
                        int requestFrames = (int)System.Math.Min(NativeReadFrames, expectedFrames - framesRead);
                        long sampleOffset = checked(framesRead * channels);
                        long byteOffset = checked(sampleOffset * sizeof(float));
                        IntPtr destination = new(checked(baseAddress.ToInt64() + byteOffset));
                        int status = native.ReadFrames(
                            decoder,
                            destination,
                            requestFrames,
                            out long readFrames,
                            out nativeError);
                        if (status != 0)
                        {
                            throw CreateNativeFailure(path, status, nativeError, "Vorbis decoding failed before the end of the file.");
                        }
                        if (readFrames <= 0 || readFrames > requestFrames)
                        {
                            throw new AudioSourceLoadException(
                                AudioSourceLoadStage.DecodeVorbis,
                                path,
                                "Vorbis ended before its declared frame count.");
                        }
                        framesRead = checked(framesRead + readFrames);
                    }

                    float[] endProbe = new float[channels];
                    var pinnedProbe = GCHandle.Alloc(endProbe, GCHandleType.Pinned);
                    try
                    {
                        int status = native.ReadFrames(
                            decoder,
                            pinnedProbe.AddrOfPinnedObject(),
                            1,
                            out long trailingFrames,
                            out nativeError);
                        if (status != 0)
                        {
                            throw CreateNativeFailure(path, status, nativeError, "Vorbis failed while confirming end of file.");
                        }
                        if (trailingFrames != 0)
                        {
                            throw new AudioSourceLoadException(
                                AudioSourceLoadStage.DecodeVorbis,
                                path,
                                "Vorbis decoded more frames than its declared source length.");
                        }
                    }
                    finally
                    {
                        pinnedProbe.Free();
                    }
                }
                finally
                {
                    pinnedSamples.Free();
                }
            }
            else
            {
                float[] endProbe = new float[channels];
                var pinnedProbe = GCHandle.Alloc(endProbe, GCHandleType.Pinned);
                try
                {
                    int status = native.ReadFrames(
                        decoder,
                        pinnedProbe.AddrOfPinnedObject(),
                        1,
                        out long trailingFrames,
                        out nativeError);
                    if (status != 0 || trailingFrames != 0)
                    {
                        throw CreateNativeFailure(path, status, nativeError, "The empty Vorbis source did not end cleanly.");
                    }
                }
                finally
                {
                    pinnedProbe.Free();
                }
            }

            try
            {
                return new DecodedAudio(sampleRate, layout, samples);
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
        finally
        {
            if (decoder != IntPtr.Zero)
            {
                native.Close(decoder);
            }
            if (pinnedInput.IsAllocated)
            {
                pinnedInput.Free();
            }
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

    private static AudioSourceLoadException CreateNativeFailure(
        string path,
        int status,
        int nativeError,
        string message)
    {
        string detail = status switch
        {
            2 => "Invalid Ogg page, CRC, sequence, or end marker.",
            3 => "Unsupported Ogg codec or channel layout.",
            4 => "libvorbis rejected or failed to decode the stream.",
            5 => "A chained Vorbis stream changed its sample rate or channel layout.",
            6 => "The Vorbis source exceeds a supported size.",
            7 => "The native Vorbis bridge ran out of memory.",
            _ => "The native Vorbis bridge reported an unknown error."
        };
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
