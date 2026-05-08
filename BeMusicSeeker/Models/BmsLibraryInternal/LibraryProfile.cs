using System;
using System.Collections.Generic;
using BeMusicSeeker.Models.LR2;

namespace BeMusicSeeker.Models.BmsLibraryInternal;

internal sealed class LibraryProfile
{
    public LibraryProfile(
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
        OperationModeLR2DB = operationModeLR2DB;
        SongDbPath = songDbPath ?? throw new ArgumentNullException(nameof(songDbPath));
        SearchRoots = searchRoots ?? Array.Empty<string>();
        Lr2ConfigProvider = lr2ConfigProvider;
        Lr2ScoreDbPath = lr2ScoreDbPath;
        CanWriteLr2Config = canWriteLr2Config;
        CanOutputLr2Folders = canOutputLr2Folders;
        CanUseLr2Backup = canUseLr2Backup;
        CanUseLr2IrScore = canUseLr2IrScore;
    }

    public bool OperationModeLR2DB { get; }

    public string SongDbPath { get; }

    public IReadOnlyList<string> SearchRoots { get; }

    public Func<LR2Config> Lr2ConfigProvider { get; }

    public string Lr2ScoreDbPath { get; }

    public bool CanWriteLr2Config { get; }

    public bool CanOutputLr2Folders { get; }

    public bool CanUseLr2Backup { get; }

    public bool CanUseLr2IrScore { get; }
}
