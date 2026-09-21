using System.Diagnostics;

namespace BeMusicSeeker.Models.BmsLibraryInternal;

internal sealed class Lr2SongDbSyncInputSurfaceTimings(
    Stopwatch rowSnapshot,
    Stopwatch roots,
    Stopwatch builtinSettings,
    Stopwatch scanSurface,
    Stopwatch directoryTargets,
    Stopwatch directoryEntries,
    Stopwatch lr2FolderCandidates,
    Stopwatch folderInfoCandidates,
    Stopwatch textFileDirs,
    Stopwatch input)
{
    public Stopwatch RowSnapshot { get; } = rowSnapshot;

    public Stopwatch Roots { get; } = roots;

    public Stopwatch BuiltinSettings { get; } = builtinSettings;

    public Stopwatch ScanSurface { get; } = scanSurface;

    public Stopwatch DirectoryTargets { get; } = directoryTargets;

    public Stopwatch DirectoryEntries { get; } = directoryEntries;

    public Stopwatch Lr2FolderCandidates { get; } = lr2FolderCandidates;

    public Stopwatch FolderInfoCandidates { get; } = folderInfoCandidates;

    public Stopwatch TextFileDirs { get; } = textFileDirs;

    public Stopwatch Input { get; } = input;
}
