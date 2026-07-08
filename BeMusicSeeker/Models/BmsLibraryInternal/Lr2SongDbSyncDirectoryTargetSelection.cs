using System.Collections.Generic;

namespace BeMusicSeeker.Models.BmsLibraryInternal;

internal sealed class Lr2SongDbSyncDirectoryTargetSelection(
    IReadOnlyCollection<string> directoryMetadataTargets,
    IReadOnlyCollection<string> lr2FolderParentDirectoryTargets,
    IReadOnlyCollection<string> directoryEntryTargets)
{
    public IReadOnlyCollection<string> DirectoryMetadataTargets { get; } = directoryMetadataTargets ?? [];

    public IReadOnlyCollection<string> Lr2FolderParentDirectoryTargets { get; } = lr2FolderParentDirectoryTargets ?? [];

    public IReadOnlyCollection<string> DirectoryEntryTargets { get; } = directoryEntryTargets ?? [];
}
