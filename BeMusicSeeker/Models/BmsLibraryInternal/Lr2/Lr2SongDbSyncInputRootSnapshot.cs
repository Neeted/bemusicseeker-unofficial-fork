using System;
using System.Collections.Generic;

namespace BeMusicSeeker.Models.BmsLibraryInternal;

internal sealed class Lr2SongDbSyncInputRootSnapshot(
    DateTime capturedAtUtc,
    IReadOnlyList<string> rootDirectories,
    IReadOnlyList<string> lr2FolderDiscoveryDirectories,
    string lr2RootPath)
{
    public DateTime CapturedAtUtc { get; } = capturedAtUtc;

    public IReadOnlyList<string> RootDirectories { get; } = rootDirectories;

    public IReadOnlyList<string> Lr2FolderDiscoveryDirectories { get; } = lr2FolderDiscoveryDirectories;

    public string Lr2RootPath { get; } = lr2RootPath;
}
