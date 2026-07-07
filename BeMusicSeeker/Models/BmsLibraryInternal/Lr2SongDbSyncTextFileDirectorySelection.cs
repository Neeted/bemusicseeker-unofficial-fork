using System.Collections.Generic;

namespace BeMusicSeeker.Models.BmsLibraryInternal;

internal sealed class Lr2SongDbSyncTextFileDirectorySelection(
    IReadOnlyList<string> directories,
    string source)
{
    public IReadOnlyList<string> Directories { get; } = directories;

    public string Source { get; } = source;
}
