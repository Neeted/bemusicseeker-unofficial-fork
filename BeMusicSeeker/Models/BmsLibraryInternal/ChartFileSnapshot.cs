using System;

namespace BeMusicSeeker.Models.BmsLibraryInternal;

internal sealed class ChartFileSnapshot
{
    public ChartFileSnapshot(
        string path,
        byte[] bytes,
        DateTime lastWriteTimeUtc,
        string md5,
        string sha256)
    {
        Path = path ?? throw new ArgumentNullException(nameof(path));
        Bytes = bytes ?? throw new ArgumentNullException(nameof(bytes));
        Length = bytes.LongLength;
        LastWriteTimeUtc = lastWriteTimeUtc;
        Md5 = md5 ?? throw new ArgumentNullException(nameof(md5));
        Sha256 = sha256 ?? throw new ArgumentNullException(nameof(sha256));
    }

    public string Path { get; }

    public byte[] Bytes { get; }

    public long Length { get; }

    public DateTime LastWriteTimeUtc { get; }

    public string Md5 { get; }

    public string Sha256 { get; }
}
