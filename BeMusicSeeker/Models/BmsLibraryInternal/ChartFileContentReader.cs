using System;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using BeMusicSeeker.Models.Utils;

namespace BeMusicSeeker.Models.BmsLibraryInternal;

internal static class ChartFileContentReader
{
    public static ChartFileSnapshot ReadSnapshot(string path)
    {
        return CreateSnapshot(ReadBuffer(path));
    }

    public static ChartFileReadBuffer ReadBuffer(string path)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            throw new ArgumentNullException(nameof(path));
        }

        string fullPath = LongPathFileSystem.NormalizePathForStorage(path);
        byte[] bytes = LongPathFileSystem.ReadAllBytes(fullPath);
        DateTime lastWriteTimeUtc = LongPathFileSystem.GetLastWriteTimeUtc(fullPath);
        return new ChartFileReadBuffer(fullPath, bytes, lastWriteTimeUtc);
    }

    internal static ChartFileSnapshot CreateSnapshot(ChartFileReadBuffer buffer)
    {
        if (buffer == null)
        {
            throw new ArgumentNullException(nameof(buffer));
        }

        return CreateSnapshot(buffer.Path, buffer.Bytes, buffer.LastWriteTimeUtc);
    }

    internal static ChartFileSnapshot CreateSnapshot(string path, byte[] bytes, DateTime lastWriteTimeUtc)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            throw new ArgumentNullException(nameof(path));
        }
        if (bytes == null)
        {
            throw new ArgumentNullException(nameof(bytes));
        }

        return new ChartFileSnapshot(
            LongPathFileSystem.NormalizePathForStorage(path),
            bytes,
            lastWriteTimeUtc,
            ComputeHash(bytes, MD5.Create()),
            ComputeHash(bytes, SHA256.Create()));
    }

    private static string ComputeHash(byte[] bytes, HashAlgorithm algorithm)
    {
        using (algorithm)
        {
            byte[] hash = algorithm.ComputeHash(bytes);
            var builder = new StringBuilder(hash.Length * 2);
            foreach (byte value in hash)
            {
                builder.Append(value.ToString("x2"));
            }
            return builder.ToString();
        }
    }
}
