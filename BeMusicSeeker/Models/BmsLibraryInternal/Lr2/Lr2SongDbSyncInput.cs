using System;
using System.Collections.Generic;
using BeMusicSeeker.Models.Utils;

namespace BeMusicSeeker.Models.BmsLibraryInternal;

internal sealed class Lr2SongDbSyncInput(
    IReadOnlyList<string> rootDirectories,
    IReadOnlyList<string> chartPaths,
    IReadOnlyList<string> normalFolderDirectoryPaths,
    IReadOnlyList<string> folderInfoFilePaths,
    IReadOnlyDictionary<string, RootFileEnumerationEntry> folderInfoFileEntries,
    IReadOnlyDictionary<string, RootFileEnumerationEntry> directoryEntries,
    IReadOnlyList<string> lr2FolderDiscoveryDirectories,
    IReadOnlyList<string> lr2FolderPruneDirectories,
    string lr2RootPath,
    string lr2NormalCustomFolderOutputBaseDir,
    IReadOnlyList<string> lr2AdditionalNormalCustomFolderOutputBaseDirs,
    string lr2RootCustomFolderOutputBaseDir,
    IReadOnlyList<string> lr2BuiltinFolderSourceDirectories,
    Lr2BuiltinCustomFolderSettings lr2BuiltinCustomFolderSettings,
    IReadOnlyList<string> lr2FolderFilePaths,
    IReadOnlyDictionary<string, RootFileEnumerationEntry> lr2FolderFileEntries,
    bool lr2FolderFileDiscoveryComplete,
    IReadOnlyList<BMSFile> songRows,
    IReadOnlyList<string> textFileDirectories,
    int scanSurfaceGeneration,
    int ownedChartCollectionVersion,
    int bmsRowsVersion,
    int bmsonRowsVersion)
{
    public IReadOnlyList<string> RootDirectories { get; } = rootDirectories ?? [];

    public IReadOnlyList<string> ChartPaths { get; } = chartPaths ?? [];

    public IReadOnlyList<string> NormalFolderDirectoryPaths { get; } = normalFolderDirectoryPaths ?? [];

    public IReadOnlyList<string> FolderInfoFilePaths { get; } = folderInfoFilePaths ?? [];

    public IReadOnlyDictionary<string, RootFileEnumerationEntry> FolderInfoFileEntries { get; } =
        folderInfoFileEntries ?? new Dictionary<string, RootFileEnumerationEntry>(StringComparer.OrdinalIgnoreCase);

    public IReadOnlyDictionary<string, RootFileEnumerationEntry> DirectoryEntries { get; } =
        directoryEntries ?? new Dictionary<string, RootFileEnumerationEntry>(StringComparer.OrdinalIgnoreCase);

    public IReadOnlyList<string> Lr2FolderDiscoveryDirectories { get; } = lr2FolderDiscoveryDirectories ?? [];

    public IReadOnlyList<string> Lr2FolderPruneDirectories { get; } = lr2FolderPruneDirectories ?? [];

    public string Lr2RootPath { get; } = lr2RootPath;

    public string Lr2NormalCustomFolderOutputBaseDir { get; } = lr2NormalCustomFolderOutputBaseDir;

    public IReadOnlyList<string> Lr2AdditionalNormalCustomFolderOutputBaseDirs { get; } = lr2AdditionalNormalCustomFolderOutputBaseDirs ?? [];

    public string Lr2RootCustomFolderOutputBaseDir { get; } = lr2RootCustomFolderOutputBaseDir;

    public IReadOnlyList<string> Lr2BuiltinFolderSourceDirectories { get; } = lr2BuiltinFolderSourceDirectories ?? [];

    public Lr2BuiltinCustomFolderSettings Lr2BuiltinCustomFolderSettings { get; } =
        lr2BuiltinCustomFolderSettings ?? new Lr2BuiltinCustomFolderSettings(0, 24, false);

    public IReadOnlyList<string> Lr2FolderFilePaths { get; } = lr2FolderFilePaths ?? [];

    public IReadOnlyDictionary<string, RootFileEnumerationEntry> Lr2FolderFileEntries { get; } =
        lr2FolderFileEntries ?? new Dictionary<string, RootFileEnumerationEntry>(StringComparer.OrdinalIgnoreCase);

    public bool Lr2FolderFileDiscoveryComplete { get; } = lr2FolderFileDiscoveryComplete;

    public IReadOnlyList<BMSFile> SongRows { get; } = songRows ?? [];

    public IReadOnlyList<string> TextFileDirectories { get; } = textFileDirectories ?? [];

    public int ScanSurfaceGeneration { get; } = scanSurfaceGeneration;

    public int OwnedChartCollectionVersion { get; } = ownedChartCollectionVersion;

    public int BmsRowsVersion { get; } = bmsRowsVersion;

    public int BmsonRowsVersion { get; } = bmsonRowsVersion;
}
