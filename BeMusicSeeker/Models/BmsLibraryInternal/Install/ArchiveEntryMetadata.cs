using System;

namespace BeMusicSeeker.Models.BmsLibraryInternal;

internal sealed class ArchiveEntryMetadata
{
    public string FileName { get; set; }

    public bool IsFolder { get; set; }

    public DateTime CreationTime { get; set; }

    public DateTime LastAccessTime { get; set; }

    public DateTime LastWriteTime { get; set; }
}
