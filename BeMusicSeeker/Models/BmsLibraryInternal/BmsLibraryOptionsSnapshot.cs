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

    internal static BmsLibraryOptionsSnapshot CreateCurrent(Settings settings)
    {
        if (settings == null)
        {
            throw new ArgumentNullException(nameof(settings));
        }

        return new BmsLibraryOptionsSnapshot
        {
            OperationModeLR2DB = settings.OperationModeLR2DB,
            LR2RootPath = settings.LR2RootPath,
            LR2CustomFolderOutputBaseDir = settings.LR2CustomFolderOutputBaseDir,
            LR2CustomFolderAdditionalOutputBaseDirs = CustomFolderOutputBaseRegistry.DeserializeBaseDirectories(settings.LR2CustomFolderAdditionalOutputBaseDirs),
            LR2CustomFolderOutputBaseDirRootType = settings.LR2CustomFolderOutputBaseDirRootType,
            EnableSmartComponentOverwrite = settings.EnableSmartComponentOverwrite,
            KeepSmartOverwriteProtectedFilesByRenaming = settings.KeepSmartOverwriteProtectedFilesByRenaming,
            DeletePendingPackageSourceAfterInstall = settings.DeletePendingPackageSourceAfterInstall,
            KeepInstallablePackagesPending = settings.KeepInstallablePackagesPending,
            AutoApplyAmbiguousInstallDestination = settings.AutoApplyAmbiguousInstallDestination,
            EstimateOfflineScoreRanking = settings.EstimateOfflineScoreRanking,
            UpdateLr2IrRankingCacheOnStartup = settings.UpdateLr2IrRankingCacheOnStartup,
            EnableDownloadLr2IrScoreAndDetectUnsent = settings.EnableDownloadLr2IrScoreAndDetectUnsent,
            UseBeatorajaScoreDb = settings.UseBeatorajaScoreDb,
            BeatorajaScoreDbPath = BeatorajaConfigService.IsBeatorajaRootPathValid(settings.BeatorajaRootPath)
                ? BeatorajaConfigService.GetScoreDbPath(settings.BeatorajaRootPath, settings.BeatorajaPlayerId)
                : settings.BeatorajaScoreDbPath,
            EnableReadOptimizedPragmas = settings.EnableReadOptimizedPragmas,
            ScanBmsFilesOnStartup = settings.ScanBmsFilesOnStartup,
            FolderNameFormat = settings.FolderNameFormat,
            UseOnlyShiftJISChars = settings.UseOnlyShiftJISChars,
            BMSInstallDir = settings.BMSInstallDir,
            PendingInstallEstimateMaxParallelPackages = settings.PendingInstallEstimateMaxParallelPackages
        };
    }
}
