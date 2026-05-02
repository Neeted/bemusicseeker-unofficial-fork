using System.Collections.Generic;
using BeMusicSeeker.Models.LR2;
using BeMusicSeeker.Models.Utils;

namespace BeMusicSeeker.Models.BmsLibraryInternal;

internal sealed class SongTableFileCheckResult
{
    public List<string> Pragmas { get; } = new List<string>();

    public List<BMSFile> AddedFiles { get; } = new List<BMSFile>();

    public List<LR2SongDBExtended.bmson_song> AddedBmsonSongs { get; } = new List<LR2SongDBExtended.bmson_song>();

    public List<BMSFile> NextFiles { get; } = new List<BMSFile>();

    public List<LR2SongDBExtended.bmson_song> NextBmsonSongs { get; } = new List<LR2SongDBExtended.bmson_song>();

    public List<string> DeletedPaths { get; } = new List<string>();

    public List<string> DeletedBmsonPaths { get; } = new List<string>();

    public List<BMSFile> ClearedInstallDestinations { get; } = new List<BMSFile>();

    public BMSDirectoryFileNameHash NextFolderAllFileList { get; set; }

    public DirectoryResourceLookupCache NextDirectoryResourceLookupCache { get; set; }

    public DirectoryRelativePathHashIndex NextDirectoryRelativePathHashIndex { get; set; }

    public bool HasDbDiff { get; set; }

    public bool PrefetchedScanUsed { get; set; }

    public int BmsPathCount { get; set; }

    public int DirectoryCount { get; set; }

    public long ScanElapsedMs { get; set; }

    public long NativeBridgeMs { get; set; }

    public string NativeBridgeReason { get; set; }

    public long ManagedDecodeMs { get; set; }

    public long ManagedMaterializeMs { get; set; }

    public ulong BridgeRawBufferBytes { get; set; }

    public long DirhashBuildMs { get; set; }

    public long FolderHashIndexMs { get; set; }

    public long ResourceLookupCacheMs { get; set; }

    public long RelativePathHashIndexMs { get; set; }

    public long DiffMs { get; set; }

    public int BmsAddedTargetCount { get; set; }

    public int BmsonUpsertTargetCount { get; set; }

    public long BmsParseMs { get; set; }

    public long BmsonParseMs { get; set; }

    public long ParseReadBytesEstimate { get; set; }

    public long NewFileParseMs { get; set; }

    public long ApplyMs { get; set; }

    public long DbCommitMs { get; set; }

    public long InstlDstCleanupMs { get; set; }

    public ulong AllBaseHashEntryCount { get; set; }

    public ulong AudioBaseHashEntryCount { get; set; }

    public ulong ImageBaseHashEntryCount { get; set; }

    public ulong MovieBaseHashEntryCount { get; set; }

    public ulong AudioRelativeHashEntryCount { get; set; }

    public ulong ImageRelativeHashEntryCount { get; set; }

    public ulong MovieRelativeHashEntryCount { get; set; }
}
