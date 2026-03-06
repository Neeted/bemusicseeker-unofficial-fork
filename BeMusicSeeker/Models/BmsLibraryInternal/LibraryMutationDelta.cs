using System.Collections.Generic;

namespace BeMusicSeeker.Models.BmsLibraryInternal;

internal sealed class LibraryMutationDelta
{
    public List<BMSFile> FilesToUnregister { get; } = new List<BMSFile>();

    public List<LibraryFilePathChange> FilePathChanges { get; } = new List<LibraryFilePathChange>();

    public List<LibraryFolderPathChange> FolderPathChanges { get; } = new List<LibraryFolderPathChange>();

    public List<LibraryDeleteFailure> Failures { get; } = new List<LibraryDeleteFailure>();

    public bool RaiseBmsFilesChanged { get; set; }

    public bool RaiseInstalledPackagesChanged { get; set; }

    public bool InvalidateBMSHashIndex { get; set; }

    public bool InvalidateInstalledDirectoryIndex { get; set; }

    public bool InvalidateParentFolderCache { get; set; }

    public int RenamedCount { get; set; }

    public int DuplicateDeletedCount { get; set; }

    public int SkippedCount { get; set; }

    public long TotalMs { get; set; }
}

internal sealed class LibraryFilePathChange
{
    public BMSFile File { get; set; }

    public string NewPath { get; set; }

    public string OldPath { get; set; }

    public bool CalcFolderParent { get; set; } = true;
}

internal sealed class LibraryFolderPathChange
{
    public string NewFolderPath { get; set; }

    public string OldFolderPath { get; set; }
}
