using System;

namespace BeMusicSeeker.Models.BmsLibraryInternal;

internal sealed class ChartFileSnapshot(
    string path,
    byte[] bytes,
    DateTime lastWriteTimeUtc,
    string md5,
    string sha256)
{
    public string Path { get; } = path ?? throw new ArgumentNullException(nameof(path));

    public byte[] Bytes { get; } = bytes ?? throw new ArgumentNullException(nameof(bytes));

    public long Length { get; } = bytes.LongLength;

    public DateTime LastWriteTimeUtc { get; } = lastWriteTimeUtc;

    public string Md5 { get; } = md5 ?? throw new ArgumentNullException(nameof(md5));

    public string Sha256 { get; } = sha256 ?? throw new ArgumentNullException(nameof(sha256));
}
