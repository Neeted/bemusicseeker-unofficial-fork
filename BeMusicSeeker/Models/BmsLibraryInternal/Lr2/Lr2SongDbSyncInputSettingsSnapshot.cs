using System.Collections.Generic;

namespace BeMusicSeeker.Models.BmsLibraryInternal;

internal sealed class Lr2SongDbSyncInputSettingsSnapshot(
    Lr2BuiltinCustomFolderSettings lr2BuiltinCustomFolderSettings,
    IReadOnlyList<string> lr2BuiltinFolderSourceDirectories,
    string lr2NormalCustomFolderOutputBaseDir,
    IReadOnlyList<string> lr2AdditionalNormalCustomFolderOutputBaseDirs,
    string lr2RootCustomFolderOutputBaseDir,
    IReadOnlyList<string> lr2FolderPruneDirectories)
{
    public Lr2BuiltinCustomFolderSettings Lr2BuiltinCustomFolderSettings { get; } = lr2BuiltinCustomFolderSettings;

    public IReadOnlyList<string> Lr2BuiltinFolderSourceDirectories { get; } = lr2BuiltinFolderSourceDirectories;

    public string Lr2NormalCustomFolderOutputBaseDir { get; } = lr2NormalCustomFolderOutputBaseDir;

    public IReadOnlyList<string> Lr2AdditionalNormalCustomFolderOutputBaseDirs { get; } =
        lr2AdditionalNormalCustomFolderOutputBaseDirs;

    public string Lr2RootCustomFolderOutputBaseDir { get; } = lr2RootCustomFolderOutputBaseDir;

    public IReadOnlyList<string> Lr2FolderPruneDirectories { get; } = lr2FolderPruneDirectories;
}
