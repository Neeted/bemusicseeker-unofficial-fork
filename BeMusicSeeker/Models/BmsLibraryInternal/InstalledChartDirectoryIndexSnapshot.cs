using System;
using System.Collections.Generic;
using System.Linq;

namespace BeMusicSeeker.Models.BmsLibraryInternal;

internal sealed class InstalledChartDirectoryIndexSnapshot
{
    public Dictionary<string, List<string>> Md5Directories { get; } = new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase);

    public Dictionary<string, List<string>> Sha256Directories { get; } = new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase);

    public int HashCount => Md5Directories.Count + Sha256Directories.Count;

    public int DirectoryReferenceCount => Md5Directories.Sum(x => x.Value.Count)
        + Sha256Directories.Sum(x => x.Value.Count);

    public HashSet<string> KnownChartDirectories { get; } = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

    public InstalledChartDirectoryIndexSnapshot Clone()
    {
        var snapshot = new InstalledChartDirectoryIndexSnapshot();
        foreach (KeyValuePair<string, List<string>> item in Md5Directories)
        {
            snapshot.Md5Directories[item.Key] = [.. item.Value];
        }
        foreach (KeyValuePair<string, List<string>> item2 in Sha256Directories)
        {
            snapshot.Sha256Directories[item2.Key] = [.. item2.Value];
        }
        snapshot.KnownChartDirectories.UnionWith(KnownChartDirectories);
        return snapshot;
    }
}
