using System;
using BeMusicSeeker.Properties;

namespace BeMusicSeeker.Models;

/// <summary>
/// custom-folder output の一つの操作で共有する設定値を保持します。
/// </summary>
internal sealed class CustomFolderOutputSettingsSnapshot
{
    public bool OperationModeLR2DB { get; init; }

    public string LR2RootPath { get; init; }

    public string LR2CustomFolderOutputBaseDir { get; init; }

    public string LR2CustomFolderOutputBaseDirRootType { get; init; }

    public string LR2CustomFolderAdditionalOutputBaseDirs { get; init; }

    public bool EnableDownloadLr2IrScoreAndDetectUnsent { get; init; }

    internal static CustomFolderOutputSettingsSnapshot CreateCurrent()
    {
        return CreateCurrent(SettingsEditSession.CreateDefault().Values);
    }

    internal static CustomFolderOutputSettingsSnapshot CreateCurrent(Settings settings)
    {
        if (settings == null)
        {
            throw new ArgumentNullException(nameof(settings));
        }

        return new CustomFolderOutputSettingsSnapshot
        {
            OperationModeLR2DB = settings.OperationModeLR2DB,
            LR2RootPath = settings.LR2RootPath,
            LR2CustomFolderOutputBaseDir = settings.LR2CustomFolderOutputBaseDir,
            LR2CustomFolderOutputBaseDirRootType = settings.LR2CustomFolderOutputBaseDirRootType,
            LR2CustomFolderAdditionalOutputBaseDirs = settings.LR2CustomFolderAdditionalOutputBaseDirs,
            EnableDownloadLr2IrScoreAndDetectUnsent = settings.EnableDownloadLr2IrScoreAndDetectUnsent
        };
    }

}
