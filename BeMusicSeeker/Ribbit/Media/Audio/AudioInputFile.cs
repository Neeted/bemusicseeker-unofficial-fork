#nullable enable annotations
using System;
using System.IO;
using System.Runtime.InteropServices;
using System.Threading;
using BeMusicSeeker.Models.Utils;

namespace Ribbit.Media.Audio;

/// <summary>一回の読み込みで取得した音声入力をnative memoryで所有します。</summary>
internal sealed class AudioInputFile : IDisposable
{
    private static readonly byte[] EmptyInput = Array.Empty<byte>();
    private const int ReadBufferSize = 64 * 1024;
    private readonly NativeBuffer? buffer;
    private int ownership;

    private AudioInputFile(string path, long length, NativeBuffer? buffer)
    {
        Path = path;
        Length = length;
        this.buffer = buffer;
    }

    /// <summary>正規化した入力pathを取得します。</summary>
    internal string Path { get; }

    /// <summary>native memory上の入力長を取得します。</summary>
    internal long Length { get; }

    /// <summary>native decoderへ渡す入力先頭を取得します。</summary>
    internal IntPtr Memory
    {
        get
        {
            ThrowIfReleased();
            return buffer?.DangerousGetHandle() ?? IntPtr.Zero;
        }
    }

    /// <summary>signatureを一度読み、Ogg上限確認後に入力全体を連続native memoryへ読み込みます。</summary>
    /// <param name="path">読み込むファイルpathです。</param>
    /// <param name="openInput">指定時はpathに対する一回read用streamを作成します。</param>
    /// <param name="beforeNativeAllocation">native owner作成直前の境界を観測するcallbackです。</param>
    internal static AudioInputFile Read(
        string path,
        Func<string, Stream>? openInput = null,
        Action<long>? beforeNativeAllocation = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        string fullPath;
        try
        {
            fullPath = System.IO.Path.GetFullPath(path);
            using Stream input = openInput == null
                ? LongPathFileSystem.OpenRead(fullPath)
                : openInput(fullPath)
                    ?? throw new InvalidOperationException("The audio input factory returned no stream.");
            long length = input.Length;
            if (length < 0)
            {
                _ = GetNativeAllocationSize(length);
            }

            int prefixLength = (int)System.Math.Min(length, 4L);
            byte[] prefix = new byte[prefixLength];
            int prefixOffset = 0;
            while (prefixOffset < prefixLength)
            {
                int read = input.Read(prefix, prefixOffset, prefixLength - prefixOffset);
                if (read == 0)
                {
                    throw new EndOfStreamException("The audio input changed while its signature was being read.");
                }
                prefixOffset = checked(prefixOffset + read);
            }

            if (prefixLength == 4 && prefix.AsSpan().SequenceEqual("OggS"u8))
            {
                VorbisDecoder.ValidateEncodedLength(fullPath, length);
            }

            IntPtr allocationSize = GetNativeAllocationSize(length);
            if (length == 0)
            {
                return new AudioInputFile(fullPath, length, buffer: null);
            }

            beforeNativeAllocation?.Invoke(length);
            var buffer = new NativeBuffer(allocationSize);
            try
            {
                byte[] chunk = new byte[ReadBufferSize];
                if (prefixLength > 0)
                {
                    Marshal.Copy(prefix, 0, buffer.DangerousGetHandle(), prefixLength);
                }
                long offset = prefixLength;
                while (offset < length)
                {
                    int requested = (int)System.Math.Min(chunk.Length, length - offset);
                    int read = input.Read(chunk, 0, requested);
                    if (read == 0)
                    {
                        throw new EndOfStreamException("The audio input changed while it was being read.");
                    }
                    Marshal.Copy(chunk, 0, AddOffset(buffer.DangerousGetHandle(), offset), read);
                    offset = checked(offset + read);
                }

                return new AudioInputFile(fullPath, length, buffer);
            }
            catch
            {
                buffer.Dispose();
                throw;
            }
        }
        catch (AudioSourceLoadException)
        {
            throw;
        }
        catch (Exception exception) when (exception is IOException
            or UnauthorizedAccessException
            or ArgumentException
            or OverflowException
            or OutOfMemoryException)
        {
            throw new AudioSourceLoadException(
                AudioSourceLoadStage.InspectContainer,
                path,
                "The audio input could not be read completely.",
                exception);
        }
    }

    /// <summary>native ownerを保持する独立したread-only viewを作成します。</summary>
    internal Stream OpenReadView()
    {
        ThrowIfReleased();
        return buffer == null
            ? new MemoryStream(EmptyInput, writable: false)
            : new NativeBufferReadStream(buffer, Length);
    }

    /// <summary>BASS decoderへnative memoryを渡した後、native解放確認まで所有をsessionへ移します。</summary>
    internal void TransferToSession()
    {
        if (Interlocked.CompareExchange(ref ownership, 1, 0) != 0)
        {
            throw new InvalidOperationException("Audio input ownership was already transferred or released.");
        }
    }

    /// <summary>sessionがnative decoderの解放を確認したときに入力memoryを解放します。</summary>
    internal void ConfirmNativeRelease(int handle)
    {
        if (Interlocked.Exchange(ref ownership, 2) != 2)
        {
            buffer?.Dispose();
        }
    }

    /// <summary>size_tへ安全に変換可能なnative allocation sizeを検査します。</summary>
    internal static IntPtr GetNativeAllocationSize(long length)
    {
        if (length < 0 || (IntPtr.Size == sizeof(int) && length > int.MaxValue))
        {
            throw new IOException("The audio input length exceeds the native address-space limit.");
        }
        return new IntPtr(length);
    }

    private static IntPtr AddOffset(IntPtr address, long offset) =>
        new(checked(address.ToInt64() + offset));

    private void ThrowIfReleased()
    {
        if (Volatile.Read(ref ownership) == 2)
        {
            throw new ObjectDisposedException(nameof(AudioInputFile));
        }
    }

    /// <summary>read-only viewはownerのnative memoryを解放しません。</summary>
    public void Dispose()
    {
        if (Interlocked.CompareExchange(ref ownership, 2, 0) == 0)
        {
            buffer?.Dispose();
        }
        GC.SuppressFinalize(this);
    }

    private sealed class NativeBuffer : SafeBuffer
    {
        internal NativeBuffer(IntPtr size)
            : base(ownsHandle: true)
        {
            SetHandle(Marshal.AllocHGlobal(size));
            if (IsInvalid)
            {
                throw new OutOfMemoryException("Native memory allocation for the audio input failed.");
            }
            Initialize(checked((ulong)size.ToInt64()));
        }

        protected override bool ReleaseHandle()
        {
            Marshal.FreeHGlobal(handle);
            return true;
        }
    }

    private sealed unsafe class NativeBufferReadStream : UnmanagedMemoryStream
    {
        private readonly NativeBuffer owner;
        private int disposed;

        internal NativeBufferReadStream(NativeBuffer owner, long length)
        {
            this.owner = owner;
            byte* pointer = null;
            bool acquired = false;
            try
            {
                owner.AcquirePointer(ref pointer);
                acquired = true;
                Initialize(pointer, length, length, FileAccess.Read);
            }
            catch
            {
                if (acquired)
                {
                    owner.ReleasePointer();
                }
                Interlocked.Exchange(ref disposed, 1);
                GC.SuppressFinalize(this);
                throw;
            }
        }

        ~NativeBufferReadStream()
        {
            Dispose(disposing: false);
        }

        protected override void Dispose(bool disposing)
        {
            if (Interlocked.Exchange(ref disposed, 1) == 0)
            {
                try
                {
                    base.Dispose(disposing);
                }
                finally
                {
                    owner.ReleasePointer();
                }
            }
            else
            {
                base.Dispose(disposing);
            }
            if (disposing)
            {
                GC.SuppressFinalize(this);
            }
        }
    }
}
