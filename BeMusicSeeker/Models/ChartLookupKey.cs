using System;
using BeMusicSeeker.Models.LR2;

namespace BeMusicSeeker.Models;

public enum ChartLookupHashKind
{
    None,
    Md5,
    Sha256
}

internal static class ChartLookupKey
{
    internal static string GetPrimaryHash(BMSFile file)
    {
        if (!string.IsNullOrWhiteSpace(file?.hash))
        {
            return file.hash;
        }
        if (!string.IsNullOrWhiteSpace(file?.sha256))
        {
            return file.sha256;
        }
        return null;
    }

    internal static ChartLookupHashKind GetPrimaryHashKind(BMSFile file)
    {
        if (!string.IsNullOrWhiteSpace(file?.hash))
        {
            return ChartLookupHashKind.Md5;
        }
        if (!string.IsNullOrWhiteSpace(file?.sha256))
        {
            return ChartLookupHashKind.Sha256;
        }
        return ChartLookupHashKind.None;
    }

    internal static string GetPrimaryHash(LR2SongDBExtended.bmson_song song)
    {
        if (!string.IsNullOrWhiteSpace(song?.md5))
        {
            return song.md5;
        }
        if (!string.IsNullOrWhiteSpace(song?.sha256))
        {
            return song.sha256;
        }
        return null;
    }

    internal static ChartLookupHashKind GetPrimaryHashKind(ChartFile chart)
    {
        if (!string.IsNullOrWhiteSpace(chart?.Md5))
        {
            return ChartLookupHashKind.Md5;
        }
        if (!string.IsNullOrWhiteSpace(chart?.Sha256))
        {
            return ChartLookupHashKind.Sha256;
        }
        return ChartLookupHashKind.None;
    }

    internal static ChartLookupHashKind GetPrimaryHashKind(LR2SongDBExtended.bmson_song song)
    {
        if (!string.IsNullOrWhiteSpace(song?.md5))
        {
            return ChartLookupHashKind.Md5;
        }
        if (!string.IsNullOrWhiteSpace(song?.sha256))
        {
            return ChartLookupHashKind.Sha256;
        }
        return ChartLookupHashKind.None;
    }
}
