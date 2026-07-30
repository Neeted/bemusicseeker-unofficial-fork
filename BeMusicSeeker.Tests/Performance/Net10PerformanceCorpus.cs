using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;

namespace BeMusicSeeker.Tests.Performance;

internal enum Net10PerformanceCorpusScale
{
    Small,
    Medium,
    Large
}

internal readonly record struct Net10PerformanceCorpusRow(
    int Sequence,
    string Title,
    string Artist,
    string Folder,
    int Mode,
    bool HasWarning,
    bool IsOwned);

internal static class Net10PerformanceCorpus
{
    internal const int DefaultSeed = 0xBEE501;

    internal static int GetRowCount(Net10PerformanceCorpusScale scale) => scale switch
    {
        Net10PerformanceCorpusScale.Small => 1_000,
        Net10PerformanceCorpusScale.Medium => 25_000,
        Net10PerformanceCorpusScale.Large => 200_000,
        _ => throw new ArgumentOutOfRangeException(nameof(scale))
    };

    internal static IReadOnlyList<Net10PerformanceCorpusRow> CreateRows(
        Net10PerformanceCorpusScale scale,
        int seed = DefaultSeed)
    {
        int count = GetRowCount(scale);
        var random = new Random(seed);
        var rows = new Net10PerformanceCorpusRow[count];
        for (int i = 0; i < rows.Length; i++)
        {
            int folder = random.Next(Math.Max(1, count / 25));
            int artist = random.Next(257);
            rows[i] = new Net10PerformanceCorpusRow(
                i,
                "Title " + random.Next(count * 2).ToString("D8"),
                "Artist " + artist.ToString("D4"),
                @"C:\Synthetic\Folder-" + folder.ToString("D6"),
                random.Next(5, 15),
                random.Next(100) < 7,
                random.Next(100) < 73);
        }
        return rows;
    }

    internal static string CreateFingerprint(IEnumerable<Net10PerformanceCorpusRow> rows)
    {
        var builder = new StringBuilder();
        foreach (Net10PerformanceCorpusRow row in rows ?? [])
        {
            builder.Append(row.Sequence).Append('|')
                .Append(row.Title).Append('|')
                .Append(row.Artist).Append('|')
                .Append(row.Folder).Append('|')
                .Append(row.Mode).Append('|')
                .Append(row.HasWarning ? '1' : '0').Append('|')
                .Append(row.IsOwned ? '1' : '0').Append('\n');
        }
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(builder.ToString())));
    }

    internal static T WithTemporaryWorkspace<T>(Func<string, T> action)
    {
        string path = Path.Combine(
            Path.GetTempPath(),
            "BeMusicSeeker-net10-performance-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(path);
        try
        {
            return action(path);
        }
        finally
        {
            if (Directory.Exists(path))
            {
                Directory.Delete(path, recursive: true);
            }
        }
    }
}
