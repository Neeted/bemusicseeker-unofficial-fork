using BeMusicSeeker.Properties;

namespace BeMusicSeeker.Models.BmsLibraryInternal;

internal sealed class BmsLibraryOptionsSnapshot
{
    public bool OperationModeLR2DB { get; set; }

    public string LR2CustomFolderOutputBaseDir { get; set; }

    public string LR2CustomFolderOutputBaseDirRootType { get; set; }

    public bool EnableSmartComponentOverwrite { get; set; }

    public bool KeepSmartOverwriteProtectedFilesByRenaming { get; set; }

    public bool DeletePendingPackageSourceAfterInstall { get; set; }

    public bool KeepInstallablePackagesPending { get; set; }

    public bool UseEverythingForPendingPackageSourceScan { get; set; }

    public bool AutoApplyAmbiguousInstallDestination { get; set; }

    public bool SkipEstimateOfflineScoreRanking { get; set; }

    public bool EnableDownloadLr2IrScoreAndDetectUnsent { get; set; }

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
            LR2CustomFolderOutputBaseDir = Settings.Default.LR2CustomFolderOutputBaseDir,
            LR2CustomFolderOutputBaseDirRootType = Settings.Default.LR2CustomFolderOutputBaseDirRootType,
            EnableSmartComponentOverwrite = Settings.Default.EnableSmartComponentOverwrite,
            KeepSmartOverwriteProtectedFilesByRenaming = Settings.Default.KeepSmartOverwriteProtectedFilesByRenaming,
            DeletePendingPackageSourceAfterInstall = Settings.Default.DeletePendingPackageSourceAfterInstall,
            KeepInstallablePackagesPending = Settings.Default.KeepInstallablePackagesPending,
            UseEverythingForPendingPackageSourceScan = Settings.Default.UseEverythingForPendingPackageSourceScan,
            AutoApplyAmbiguousInstallDestination = Settings.Default.AutoApplyAmbiguousInstallDestination,
            SkipEstimateOfflineScoreRanking = Settings.Default.SkipEstimateOfflineScoreRanking,
            EnableDownloadLr2IrScoreAndDetectUnsent = Settings.Default.EnableDownloadLr2IrScoreAndDetectUnsent,
            EnableReadOptimizedPragmas = Settings.Default.EnableReadOptimizedPragmas,
            SkipInitFileCheck = Settings.Default.SkipInitFileCheck,
            FolderNameFormat = Settings.Default.FolderNameFormat,
            UseOnlyShiftJISChars = Settings.Default.UseOnlyShiftJISChars,
            BMSInstallDir = Settings.Default.BMSInstallDir
        };
    }
}
