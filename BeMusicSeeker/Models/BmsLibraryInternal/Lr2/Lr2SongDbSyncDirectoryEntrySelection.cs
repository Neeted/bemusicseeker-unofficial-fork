using System.Collections.Generic;
using BeMusicSeeker.Models.Utils;

namespace BeMusicSeeker.Models.BmsLibraryInternal;

internal sealed class Lr2SongDbSyncDirectoryEntrySelection(
    IReadOnlyDictionary<string, RootFileEnumerationEntry> entries,
    int missingCount)
{
    public IReadOnlyDictionary<string, RootFileEnumerationEntry> Entries { get; } = entries;

    public int MissingCount { get; } = missingCount;
}
