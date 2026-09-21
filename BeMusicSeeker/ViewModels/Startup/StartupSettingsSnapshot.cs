using System;
using System.Collections.Generic;
using System.Linq;
using BeMusicSeeker.Models;
using BeMusicSeeker.Models.LR2;
using BeMusicSeeker.Properties;

namespace BeMusicSeeker.ViewModels;

/// <summary>
/// 起動時の library / player composition に必要な設定だけを保持する snapshot です。
/// </summary>
internal sealed class StartupSettingsSnapshot
{
    public bool OperationModeLR2DB { get; init; }

    public string LR2ConfigXmlPath { get; init; }

    public string LR2SongDBPath { get; init; }

    public string LR2RootPath { get; init; }

    public string LR2CustomFolderOutputBaseDir { get; init; }

    public string LR2CustomFolderAdditionalOutputBaseDirs { get; init; }

    private IReadOnlyList<string> standaloneBmsRootPaths = Array.AsReadOnly(Array.Empty<string>());

    public IReadOnlyList<string> StandaloneBmsRootPaths
    {
        get => standaloneBmsRootPaths;
        init => standaloneBmsRootPaths = Array.AsReadOnly(value?.ToArray() ?? Array.Empty<string>());
    }

    public bool UsePlayeruBMplay { get; init; }

    public string uBMplayPath { get; init; }

    public bool UsePlayerBMIIDXView { get; init; }

    public string BMIIDXViewPath { get; init; }

    public bool UsePlayerLR2body { get; init; }

    public string LR2bodyPath { get; init; }

    public bool IsLR2BackupEnabled { get; init; }

    public Backup.Target LR2BackupTarget { get; init; }

    public string LR2BackupPath { get; init; }

    public int LR2BackupSpan { get; init; }

    public int LR2BackupNum { get; init; }

    public bool SkipInitPlaylistLoad { get; init; }

    public Uri TableListURL { get; init; }

    public bool StartupSelectInstallPending { get; init; }

    public bool ShowDuplicateFileCheckConfirmMsg { get; init; }

    public bool UseBeatorajaScoreDb { get; init; }

    public string BeatorajaRootPath { get; init; }

    public string BeatorajaPlayerId { get; init; }

    public string BeatorajaScoreDbPath { get; init; }

    internal static StartupSettingsSnapshot CreateCurrent(Settings settings)
    {
        if (settings == null)
        {
            throw new ArgumentNullException(nameof(settings));
        }
        return new StartupSettingsSnapshot
        {
            OperationModeLR2DB = settings.OperationModeLR2DB,
            LR2ConfigXmlPath = settings.LR2ConfigXmlPath,
            LR2SongDBPath = settings.LR2SongDBPath,
            LR2RootPath = settings.LR2RootPath,
            LR2CustomFolderOutputBaseDir = settings.LR2CustomFolderOutputBaseDir,
            LR2CustomFolderAdditionalOutputBaseDirs = settings.LR2CustomFolderAdditionalOutputBaseDirs,
            StandaloneBmsRootPaths = StandaloneBmsRootPathSettings.Deserialize(
                settings.StandaloneBmsRootPaths,
                settings.BMSRootPath),
            UsePlayeruBMplay = settings.UsePlayeruBMplay,
            uBMplayPath = settings.uBMplayPath,
            UsePlayerBMIIDXView = settings.UsePlayerBMIIDXView,
            BMIIDXViewPath = settings.BMIIDXViewPath,
            UsePlayerLR2body = settings.UsePlayerLR2body,
            LR2bodyPath = ResolveLr2bodyPath(settings.LR2RootPath, settings.LR2ConfigXmlPath),
            IsLR2BackupEnabled = settings.IsLR2BackupEnabled,
            LR2BackupTarget = settings.LR2BackupTarget,
            LR2BackupPath = settings.LR2BackupPath,
            LR2BackupSpan = settings.LR2BackupSpan,
            LR2BackupNum = settings.LR2BackupNum,
            SkipInitPlaylistLoad = settings.SkipInitPlaylistLoad,
            TableListURL = settings.TableListURL,
            StartupSelectInstallPending = settings.StartupSelectInstallPending,
            ShowDuplicateFileCheckConfirmMsg = settings.ShowDuplicateFileCheckConfirmMsg,
            UseBeatorajaScoreDb = settings.UseBeatorajaScoreDb,
            BeatorajaRootPath = settings.BeatorajaRootPath,
            BeatorajaPlayerId = settings.BeatorajaPlayerId,
            BeatorajaScoreDbPath = settings.BeatorajaScoreDbPath
        };
    }

    private static string ResolveLr2bodyPath(string lr2RootPath, string lr2ConfigXmlPath)
    {
        if (string.IsNullOrWhiteSpace(lr2RootPath))
        {
            return string.Empty;
        }

        string lr2bodyPath = System.IO.Path.Combine(lr2RootPath, "LR2body.exe");
        string lrhbodyPath = System.IO.Path.Combine(lr2RootPath, "LRHbody.exe");
        if (!string.IsNullOrWhiteSpace(lr2ConfigXmlPath)
            && lr2ConfigXmlPath.EndsWith(".xmh", StringComparison.OrdinalIgnoreCase)
            && System.IO.File.Exists(lrhbodyPath))
        {
            return lrhbodyPath;
        }

        return lr2bodyPath;
    }
}
