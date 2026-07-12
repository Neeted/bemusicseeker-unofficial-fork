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
        return new CustomFolderOutputSettingsSnapshot
        {
            OperationModeLR2DB = Settings.Default.OperationModeLR2DB,
            LR2RootPath = Settings.Default.LR2RootPath,
            LR2CustomFolderOutputBaseDir = Settings.Default.LR2CustomFolderOutputBaseDir,
            LR2CustomFolderOutputBaseDirRootType = Settings.Default.LR2CustomFolderOutputBaseDirRootType,
            LR2CustomFolderAdditionalOutputBaseDirs = Settings.Default.LR2CustomFolderAdditionalOutputBaseDirs,
            EnableDownloadLr2IrScoreAndDetectUnsent = Settings.Default.EnableDownloadLr2IrScoreAndDetectUnsent
        };
    }

}
