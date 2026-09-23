using BeMusicSeeker.Models.BmsLibraryInternal;
using BeMusicSeeker.Models.Utils;
using Ribbit.Cryptography;

namespace BeMusicSeeker.Models;

internal static class ChartResourceKeyHash
{
    internal static uint GetFileNameHash(string fileName)
    {
        return GetLookupHash(ChartResourcePathNormalizer.NormalizeFileNameForLookup(fileName));
    }

    internal static uint GetLookupHash(string normalizedLookupValue)
    {
        if (string.IsNullOrWhiteSpace(normalizedLookupValue))
        {
            return 0u;
        }
        return xxHash32.CalculateHash(normalizedLookupValue.ToUpperInvariant());
    }
}
