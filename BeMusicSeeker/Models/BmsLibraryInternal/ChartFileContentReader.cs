using System;
using System.IO;
using System.Security.Cryptography;
using System.Text;

namespace BeMusicSeeker.Models.BmsLibraryInternal;

internal static class ChartFileContentReader
{
    public static ChartFileSnapshot ReadSnapshot(string path)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            throw new ArgumentNullException(nameof(path));
        }

        string fullPath = Path.GetFullPath(path);
        byte[] bytes = File.ReadAllBytes(fullPath);
        DateTime lastWriteTimeUtc = File.GetLastWriteTimeUtc(fullPath);
        return CreateSnapshot(fullPath, bytes, lastWriteTimeUtc);
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
            Path.GetFullPath(path),
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
