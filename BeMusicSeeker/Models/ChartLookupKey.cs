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
        return string.IsNullOrWhiteSpace(chart?.Md5) ? null : chart.Md5;
    }

    internal static ChartLookupHashKind GetPrimaryHashKind(ChartFile chart)
    {
        if (!string.IsNullOrWhiteSpace(chart?.Md5))
        {
            return ChartLookupHashKind.Md5;
        }
        return ChartLookupHashKind.None;
    }
}
