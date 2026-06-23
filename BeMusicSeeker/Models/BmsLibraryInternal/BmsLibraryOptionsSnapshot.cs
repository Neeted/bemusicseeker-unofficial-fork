using System.Collections.Generic;
using BeMusicSeeker.Properties;

namespace BeMusicSeeker.Models.BmsLibraryInternal;

internal sealed class BmsLibraryOptionsSnapshot
{
    public bool OperationModeLR2DB { get; set; }

    public string LR2RootPath { get; set; }

    public string LR2CustomFolderOutputBaseDir { get; set; }

    public IReadOnlyList<string> LR2CustomFolderAdditionalOutputBaseDirs { get; set; } = [];

    public string LR2CustomFolderOutputBaseDirRootType { get; set; }

    public bool EnableSmartComponentOverwrite { get; set; }

    public bool KeepSmartOverwriteProtectedFilesByRenaming { get; set; }

    public bool DeletePendingPackageSourceAfterInstall { get; set; }

    public bool KeepInstallablePackagesPending { get; set; }

    public bool AutoApplyAmbiguousInstallDestination { get; set; }

    public bool EstimateOfflineScoreRanking { get; set; }

    public bool UpdateLr2IrRankingCacheOnStartup { get; set; }

    public bool EnableDownloadLr2IrScoreAndDetectUnsent { get; set; }

    public bool UseBeatorajaScoreDb { get; set; }

    public string BeatorajaScoreDbPath { get; set; }

    public bool EnableReadOptimizedPragmas { get; set; }

    public bool SkipInitFileCheck { get; set; }

    public string FolderNameFormat { get; set; }

    public bool UseOnlyShiftJISChars { get; set; }

    public string BMSInstallDir { get; set; }

    public static BmsLibraryOptionsSnapshot CreateCurrent()
    {
        return new BmsLibraryOptionsSnapshot
        {
            OperationModeLR2DB = Settings.Default.OperationModeLR2DB,
            LR2RootPath = Settings.Default.LR2RootPath,
            LR2CustomFolderOutputBaseDir = Settings.Default.LR2CustomFolderOutputBaseDir,
            LR2CustomFolderAdditionalOutputBaseDirs = CustomFolderOutputBaseRegistry.ReadAdditionalBaseDirectories(),
            LR2CustomFolderOutputBaseDirRootType = Settings.Default.LR2CustomFolderOutputBaseDirRootType,
            EnableSmartComponentOverwrite = Settings.Default.EnableSmartComponentOverwrite,
            KeepSmartOverwriteProtectedFilesByRenaming = Settings.Default.KeepSmartOverwriteProtectedFilesByRenaming,
            DeletePendingPackageSourceAfterInstall = Settings.Default.DeletePendingPackageSourceAfterInstall,
            KeepInstallablePackagesPending = Settings.Default.KeepInstallablePackagesPending,
            AutoApplyAmbiguousInstallDestination = Settings.Default.AutoApplyAmbiguousInstallDestination,
            EstimateOfflineScoreRanking = Settings.Default.EstimateOfflineScoreRanking,
            UpdateLr2IrRankingCacheOnStartup = Settings.Default.UpdateLr2IrRankingCacheOnStartup,
            EnableDownloadLr2IrScoreAndDetectUnsent = Settings.Default.EnableDownloadLr2IrScoreAndDetectUnsent,
            UseBeatorajaScoreDb = Settings.Default.UseBeatorajaScoreDb,
            BeatorajaScoreDbPath = BeatorajaConfigService.IsBeatorajaRootPathValid(Settings.Default.BeatorajaRootPath)
                ? BeatorajaConfigService.GetScoreDbPath(Settings.Default.BeatorajaRootPath, Settings.Default.BeatorajaPlayerId)
                : Settings.Default.BeatorajaScoreDbPath,
            EnableReadOptimizedPragmas = Settings.Default.EnableReadOptimizedPragmas,
            SkipInitFileCheck = Settings.Default.SkipInitFileCheck,
            FolderNameFormat = Settings.Default.FolderNameFormat,
            UseOnlyShiftJISChars = Settings.Default.UseOnlyShiftJISChars,
            BMSInstallDir = Settings.Default.BMSInstallDir
        };
    }
}
