using System.Collections.Generic;
using System.Linq;
using BeMusicSeeker.Models.BmsLibraryInternal;
using BeMusicSeeker.Models.Utils;
using Ribbit.Cryptography;

namespace BeMusicSeeker.Models;

public static class BMSDirectoryFileNameHash
{
    public static uint[] GetFileNameHashArray(string path)
    {
        return GetFileNameHashArray(FastDirectoryEnumerator.GetFileNames(path));
    }

    public static uint[] GetFileNameHashArray(IEnumerable<string> list)
    {
        return (list ?? Enumerable.Empty<string>()).Select(GetFileNameHash).ToArray();
    }

    public static uint GetFileNameHash(string fileName)
    {
        return GetLookupHash(ChartResourcePathNormalizer.NormalizeFileNameForLookup(fileName));
    }

    public static uint GetLookupHash(string normalizedLookupValue)
    {
        if (string.IsNullOrWhiteSpace(normalizedLookupValue))
        {
            return 0u;
        }
        return xxHash32.CalculateHash(normalizedLookupValue.ToUpperInvariant());
    }
}
