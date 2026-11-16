using System;
using System.Collections.Generic;
using BeMusicSeeker.Models.LR2;

namespace BeMusicSeeker.Models.BmsLibraryInternal;

/// <summary>一回の構築入力を保持し、LR2の正本は構成寿命の所有者を共有します。</summary>
internal sealed class LibraryProfile(
    bool operationModeLR2DB,
    string songDbPath,
    IReadOnlyList<string> searchRoots,
    Lr2ConfigOwner lr2ConfigOwner,
    string lr2ScoreDbPath,
    bool canWriteLr2Config,
    bool canOutputLr2Folders,
    bool canUseLr2Backup,
    bool canUseLr2IrScore,
    string startupRequiredFileScanReason = null)
{
    public bool OperationModeLR2DB { get; } = operationModeLR2DB;

    public string SongDbPath { get; } = songDbPath ?? throw new ArgumentNullException(nameof(songDbPath));

    public IReadOnlyList<string> SearchRoots { get; } = searchRoots ?? [];

    /// <summary>設定・Library・Playlist・プレイヤーへ共用する全文所有者です。単独動作でLR2非利用ならnullです。</summary>
    public Lr2ConfigOwner Lr2ConfigOwner { get; } = lr2ConfigOwner;

    public string Lr2ScoreDbPath { get; } = lr2ScoreDbPath;

    public bool CanWriteLr2Config { get; } = canWriteLr2Config;

    public bool CanOutputLr2Folders { get; } = canOutputLr2Folders;

    public bool CanUseLr2Backup { get; } = canUseLr2Backup;

    public bool CanUseLr2IrScore { get; } = canUseLr2IrScore;

    public string StartupRequiredFileScanReason { get; } = startupRequiredFileScanReason;
}
