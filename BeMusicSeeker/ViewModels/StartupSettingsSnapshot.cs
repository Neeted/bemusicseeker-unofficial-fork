using System;
using System.Collections.Generic;
using System.Linq;
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

    public static StartupSettingsSnapshot CreateCurrent()
    {
        return new StartupSettingsSnapshot
        {
            OperationModeLR2DB = Settings.Default.OperationModeLR2DB,
            LR2ConfigXmlPath = Settings.Default.LR2ConfigXmlPath,
            LR2SongDBPath = Settings.Default.LR2SongDBPath,
            LR2RootPath = Settings.Default.LR2RootPath,
            LR2CustomFolderOutputBaseDir = Settings.Default.LR2CustomFolderOutputBaseDir,
            LR2CustomFolderAdditionalOutputBaseDirs = Settings.Default.LR2CustomFolderAdditionalOutputBaseDirs,
            StandaloneBmsRootPaths = MainWindowViewModel.SettingDialogViewModel.GetStandaloneBmsRootPathsFromSettings(),
            UsePlayeruBMplay = Settings.Default.UsePlayeruBMplay,
            uBMplayPath = Settings.Default.uBMplayPath,
            UsePlayerBMIIDXView = Settings.Default.UsePlayerBMIIDXView,
            BMIIDXViewPath = Settings.Default.BMIIDXViewPath,
            UsePlayerLR2body = Settings.Default.UsePlayerLR2body,
            LR2bodyPath = ResolveLr2bodyPath(Settings.Default.LR2RootPath, Settings.Default.LR2ConfigXmlPath),
            IsLR2BackupEnabled = Settings.Default.IsLR2BackupEnabled,
            LR2BackupTarget = Settings.Default.LR2BackupTarget,
            LR2BackupPath = Settings.Default.LR2BackupPath,
            LR2BackupSpan = Settings.Default.LR2BackupSpan,
            LR2BackupNum = Settings.Default.LR2BackupNum,
            SkipInitPlaylistLoad = Settings.Default.SkipInitPlaylistLoad,
            TableListURL = Settings.Default.TableListURL
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
