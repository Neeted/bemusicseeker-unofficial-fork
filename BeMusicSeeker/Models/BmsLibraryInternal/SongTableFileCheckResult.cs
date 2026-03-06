using System.Collections.Generic;
using BeMusicSeeker.Models.Utils;

namespace BeMusicSeeker.Models.BmsLibraryInternal;

internal sealed class SongTableFileCheckResult
{
    public List<string> Pragmas { get; } = new List<string>();

    public List<BMSFile> AddedFiles { get; } = new List<BMSFile>();

    public List<BMSFile> NextFiles { get; } = new List<BMSFile>();

    public List<string> DeletedPaths { get; } = new List<string>();

    public List<BMSFile> ClearedInstallDestinations { get; } = new List<BMSFile>();

    public BMSDirectoryFileNameHash NextFolderAllFileList { get; set; }

    public bool HasDbDiff { get; set; }

    public bool PrefetchedScanUsed { get; set; }

    public int BmsPathCount { get; set; }

    public int DirectoryCount { get; set; }

    public long ScanElapsedMs { get; set; }

    public long DirhashBuildMs { get; set; }

    public long DiffMs { get; set; }

    public long NewFileParseMs { get; set; }

    public long ApplyMs { get; set; }

    public long DbCommitMs { get; set; }

    public long InstlDstCleanupMs { get; set; }
}
