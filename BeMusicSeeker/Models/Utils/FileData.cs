using System;
using System.IO;

namespace BeMusicSeeker.Models.Utils;

[Serializable]
public class FileData
{
    public readonly FileAttributes Attributes;

    public readonly DateTime CreationTimeUtc;

    public readonly DateTime LastAccessTimeUtc;

    public readonly DateTime LastWriteTimeUtc;

    public readonly long Size;

    public readonly string Name;

    public readonly string Path;

    public DateTime CreationTime => CreationTimeUtc.ToLocalTime();

    public DateTime LastAccesTime => LastAccessTimeUtc.ToLocalTime();

    public DateTime LastWriteTime => LastWriteTimeUtc.ToLocalTime();

    public override string ToString()
    {
        return Name;
    }

    internal FileData(string dir, WIN32_FIND_DATA findData)
    {
        Attributes = findData.dwFileAttributes;
        CreationTimeUtc = ConvertDateTime(findData.ftCreationTime_dwHighDateTime, findData.ftCreationTime_dwLowDateTime);
        LastAccessTimeUtc = ConvertDateTime(findData.ftLastAccessTime_dwHighDateTime, findData.ftLastAccessTime_dwLowDateTime);
        LastWriteTimeUtc = ConvertDateTime(findData.ftLastWriteTime_dwHighDateTime, findData.ftLastWriteTime_dwLowDateTime);
        Size = CombineHighLowInts(findData.nFileSizeHigh, findData.nFileSizeLow);
        Name = findData.cFileName;
        Path = System.IO.Path.Combine(dir, findData.cFileName);
    }

    private static long CombineHighLowInts(uint high, uint low)
    {
        long num = (long)(((ulong)high << 32) | low);
        if (num > DateTime.MaxValue.ToFileTime())
        {
            return 0L;
        }
        return num;
    }

    private static DateTime ConvertDateTime(uint high, uint low)
    {
        return DateTime.FromFileTimeUtc(CombineHighLowInts(high, low));
    }
}
