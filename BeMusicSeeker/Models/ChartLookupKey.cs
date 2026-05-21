using System;
namespace BeMusicSeeker.Models;

public enum ChartLookupHashKind
{
    None,
    Md5,
    Sha256
}

internal static class ChartLookupKey
{
    internal static string GetPrimaryHash(ChartFile chart)
    {
        return chart?.PrimaryLookupHash;
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
}
