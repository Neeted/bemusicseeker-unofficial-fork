using System;
using System.Collections.Generic;
using BeMusicSeeker.Models.LR2;

namespace BeMusicSeeker.Models.BmsLibraryInternal;

internal sealed class LibraryProfile(
    bool operationModeLR2DB,
    string songDbPath,
    IReadOnlyList<string> searchRoots,
    Func<LR2Config> lr2ConfigProvider,
    string lr2ScoreDbPath,
    bool canWriteLr2Config,
    bool canOutputLr2Folders,
    bool canUseLr2Backup,
    bool canUseLr2IrScore)
{
    public bool OperationModeLR2DB { get; } = operationModeLR2DB;

    public string SongDbPath { get; } = songDbPath ?? throw new ArgumentNullException(nameof(songDbPath));

    public IReadOnlyList<string> SearchRoots { get; } = searchRoots ?? [];

    public Func<LR2Config> Lr2ConfigProvider { get; } = lr2ConfigProvider;

    public string Lr2ScoreDbPath { get; } = lr2ScoreDbPath;

    public bool CanWriteLr2Config { get; } = canWriteLr2Config;

    public bool CanOutputLr2Folders { get; } = canOutputLr2Folders;

    public bool CanUseLr2Backup { get; } = canUseLr2Backup;

    public bool CanUseLr2IrScore { get; } = canUseLr2IrScore;
}
