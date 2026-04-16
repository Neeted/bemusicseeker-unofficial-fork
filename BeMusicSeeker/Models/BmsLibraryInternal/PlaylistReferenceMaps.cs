using System.Collections.Generic;

namespace BeMusicSeeker.Models.BmsLibraryInternal;

internal sealed class PlaylistReferenceMaps
{
    public Dictionary<string, BMSTable[]> Md5ToTablesMap { get; set; } = new Dictionary<string, BMSTable[]>(System.StringComparer.OrdinalIgnoreCase);

    public Dictionary<string, BMSTable[]> Sha256ToTablesMap { get; set; } = new Dictionary<string, BMSTable[]>(System.StringComparer.OrdinalIgnoreCase);
}
