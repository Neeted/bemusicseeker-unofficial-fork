using System;
using System.Collections.Generic;
using System.Linq;
using BeMusicSeeker.Properties;

namespace BeMusicSeeker.Models.BmsLibraryInternal;

internal sealed class BmsLibraryOptionsSnapshot
{
    public bool OperationModeLR2DB { get; init; }

    public string LR2RootPath { get; init; }

    public string LR2CustomFolderOutputBaseDir { get; init; }

    private IReadOnlyList<string> lr2CustomFolderAdditionalOutputBaseDirs = Array.AsReadOnly(Array.Empty<string>());

    public IReadOnlyList<string> LR2CustomFolderAdditionalOutputBaseDirs
    {
        get => lr2CustomFolderAdditionalOutputBaseDirs;
        init => lr2CustomFolderAdditionalOutputBaseDirs = Array.AsReadOnly(value?.ToArray() ?? Array.Empty<string>());
    }

    public string LR2CustomFolderOutputBaseDirRootType { get; init; }

    public bool EnableSmartComponentOverwrite { get; init; }

    public bool KeepSmartOverwriteProtectedFilesByRenaming { get; init; }

    public bool DeletePendingPackageSourceAfterInstall { get; init; }

    public bool KeepInstallablePackagesPending { get; init; }

    public bool AutoApplyAmbiguousInstallDestination { get; init; }

    public bool EstimateOfflineScoreRanking { get; init; }

    public bool UpdateLr2IrRankingCacheOnStartup { get; init; }

    public bool EnableDownloadLr2IrScoreAndDetectUnsent { get; init; }

    public bool UseBeatorajaScoreDb { get; init; }

    public string BeatorajaScoreDbPath { get; init; }

    public bool EnableReadOptimizedPragmas { get; init; }

    public bool ScanBmsFilesOnStartup { get; init; }

    public string FolderNameFormat { get; init; }

    public bool UseOnlyShiftJISChars { get; init; }

    public string BMSInstallDir { get; init; }

    public int PendingInstallEstimateMaxParallelPackages { get; init; }

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
            ScanBmsFilesOnStartup = Settings.Default.ScanBmsFilesOnStartup,
            FolderNameFormat = Settings.Default.FolderNameFormat,
            UseOnlyShiftJISChars = Settings.Default.UseOnlyShiftJISChars,
            BMSInstallDir = Settings.Default.BMSInstallDir,
            PendingInstallEstimateMaxParallelPackages = Settings.Default.PendingInstallEstimateMaxParallelPackages
        };
    }
}
