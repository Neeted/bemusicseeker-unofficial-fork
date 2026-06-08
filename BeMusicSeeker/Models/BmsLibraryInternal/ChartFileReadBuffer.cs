using System;

namespace BeMusicSeeker.Models.BmsLibraryInternal;

internal sealed class ChartFileReadBuffer(
    string path,
    byte[] bytes,
    DateTime lastWriteTimeUtc)
{
    public string Path { get; } = path ?? throw new ArgumentNullException(nameof(path));

    public byte[] Bytes { get; } = bytes ?? throw new ArgumentNullException(nameof(bytes));

    public long Length { get; } = bytes.LongLength;

    public DateTime LastWriteTimeUtc { get; } = lastWriteTimeUtc;
}
