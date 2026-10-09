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

    /// <summary>
    /// 追加出力 base の raw 設定を保持し、更新入口が壊れた設定を空として
    /// 扱わずに検査できるようにします。
    /// </summary>
    internal string LR2CustomFolderAdditionalOutputBaseDirsSerialized { get; init; }

    public string LR2CustomFolderOutputBaseDirRootType { get; init; }

    public bool EnableSmartComponentOverwrite { get; init; }

    public bool KeepSmartOverwriteProtectedFilesByRenaming { get; init; }

    public bool DeletePendingPackageSourceAfterInstall { get; init; }

    /// <summary>導入先未設定の保留パッケージを新規として導入する前に確認します。</summary>
    public bool ShowNewPackageInstallConfirmMsg { get; init; } = true;

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

    /// <summary>受理する処理の設定入力を捕捉します。出力配置だけは保存段階で公開済みの値に揃えます。</summary>
    /// <param name="settings">非配置設定を読む共有設定。</param>
    /// <param name="placement">保存段階完了時に公開した配置。署名・出力・管理領域照合で同じ値を使い、非配置入力は現在設定から捕捉します。</param>
    internal static BmsLibraryOptionsSnapshot CreateCurrent(Settings settings, CustomFolderOutputSettingsSnapshot placement = null)
    {
        if (settings == null)
        {
            throw new ArgumentNullException(nameof(settings));
        }

        return new BmsLibraryOptionsSnapshot
        {
            OperationModeLR2DB = placement != null ? placement.OperationModeLR2DB : settings.OperationModeLR2DB,
            LR2RootPath = placement != null ? placement.LR2RootPath : settings.LR2RootPath,
            LR2CustomFolderOutputBaseDir = placement != null ? placement.LR2CustomFolderOutputBaseDir : settings.LR2CustomFolderOutputBaseDir,
            LR2CustomFolderAdditionalOutputBaseDirs = CustomFolderOutputBaseRegistry.DeserializeBaseDirectories(placement != null ? placement.LR2CustomFolderAdditionalOutputBaseDirs : settings.LR2CustomFolderAdditionalOutputBaseDirs),
            LR2CustomFolderAdditionalOutputBaseDirsSerialized = placement != null ? placement.LR2CustomFolderAdditionalOutputBaseDirs : settings.LR2CustomFolderAdditionalOutputBaseDirs,
            LR2CustomFolderOutputBaseDirRootType = placement != null ? placement.LR2CustomFolderOutputBaseDirRootType : settings.LR2CustomFolderOutputBaseDirRootType,
            EnableSmartComponentOverwrite = settings.EnableSmartComponentOverwrite,
            KeepSmartOverwriteProtectedFilesByRenaming = settings.KeepSmartOverwriteProtectedFilesByRenaming,
            DeletePendingPackageSourceAfterInstall = settings.DeletePendingPackageSourceAfterInstall,
            ShowNewPackageInstallConfirmMsg = settings.ShowNewPackageInstallConfirmMsg,
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
