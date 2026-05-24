using System;
using System.Collections.Generic;

namespace BeMusicSeeker.Models.BmsLibraryInternal;

internal sealed class OwnedChartHashIndexSnapshot
{
    internal HashSet<string> Md5Hashes { get; } = new(StringComparer.OrdinalIgnoreCase);

    internal HashSet<string> Sha256Hashes { get; } = new(StringComparer.OrdinalIgnoreCase);
}
