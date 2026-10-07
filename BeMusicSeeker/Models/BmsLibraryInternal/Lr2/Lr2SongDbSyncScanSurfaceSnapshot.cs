using System;
using System.Collections.Generic;
using BeMusicSeeker.Models.Utils;

namespace BeMusicSeeker.Models.BmsLibraryInternal;

internal sealed class Lr2SongDbSyncScanSurfaceSnapshot(int generation, IReadOnlyList<string> rootDirectories, IReadOnlyList<string> normalFolderDirectoryPaths, IReadOnlyDictionary<string, RootFileEnumerationEntry> directoryEntries, IReadOnlyDictionary<string, RootFileEnumerationEntry> normalFolderDirectoryEntries, IReadOnlyList<string> folderInfoFilePaths, IReadOnlyDictionary<string, RootFileEnumerationEntry> folderInfoFileEntries, IReadOnlyList<string> textFileDirectories, IReadOnlyList<string> lr2FolderDiscoveryDirectories, IReadOnlyList<string> lr2FolderFilePaths, IReadOnlyDictionary<string, RootFileEnumerationEntry> lr2FolderFileEntries, bool lr2FolderFileDiscoveryComplete, IReadOnlyDictionary<string, OwnedChartToken> pathMembershipIndex)
{
    public int Generation { get; } = generation;

    public IReadOnlyList<string> RootDirectories { get; } = rootDirectories ?? [];

    public IReadOnlyList<string> NormalFolderDirectoryPaths { get; } = normalFolderDirectoryPaths ?? [];

    public IReadOnlyDictionary<string, RootFileEnumerationEntry> DirectoryEntries { get; } =
        directoryEntries ?? new Dictionary<string, RootFileEnumerationEntry>(StringComparer.OrdinalIgnoreCase);

    public IReadOnlyDictionary<string, RootFileEnumerationEntry> NormalFolderDirectoryEntries { get; } =
        normalFolderDirectoryEntries ?? new Dictionary<string, RootFileEnumerationEntry>(StringComparer.OrdinalIgnoreCase);

    public IReadOnlyList<string> FolderInfoFilePaths { get; } = folderInfoFilePaths ?? [];

    public IReadOnlyDictionary<string, RootFileEnumerationEntry> FolderInfoFileEntries { get; } =
        folderInfoFileEntries ?? new Dictionary<string, RootFileEnumerationEntry>(StringComparer.OrdinalIgnoreCase);

    public IReadOnlyList<string> TextFileDirectories { get; } = textFileDirectories ?? [];

    public IReadOnlyList<string> Lr2FolderDiscoveryDirectories { get; } = lr2FolderDiscoveryDirectories ?? [];

    public IReadOnlyList<string> Lr2FolderFilePaths { get; } = lr2FolderFilePaths ?? [];

    public IReadOnlyDictionary<string, RootFileEnumerationEntry> Lr2FolderFileEntries { get; } =
        lr2FolderFileEntries ?? new Dictionary<string, RootFileEnumerationEntry>(StringComparer.OrdinalIgnoreCase);

    public bool Lr2FolderFileDiscoveryComplete { get; } = lr2FolderFileDiscoveryComplete;

    /// <summary>走査時の所持項目とDB exact pathの共有索引です。再利用判定はroot照合だけで全件走査しません。</summary>
    internal IReadOnlyDictionary<string, OwnedChartToken> PathMembershipIndex { get; } = pathMembershipIndex;


}
