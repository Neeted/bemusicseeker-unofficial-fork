using System;
using System.Collections.Generic;
using System.Linq;
using BeMusicSeeker.Properties;

namespace BeMusicSeeker.Models.BmsLibraryInternal;

/// <summary>一操作の設定とLR2公開入力を捕捉し、登録と分類用配置が異なる時点へずれないようにします。</summary>
internal sealed record BmsLibraryOptionsSnapshot
{
    public bool OperationModeLR2DB { get; init; }

    private IReadOnlyList<string> standaloneBmsRootPaths = Array.AsReadOnly(Array.Empty<string>());

    /// <summary>同じ確定readerから操作開始時に捕捉したstandalone登録です。</summary>
    internal IReadOnlyList<string> StandaloneBmsRootPaths
    {
        get => standaloneBmsRootPaths;
        init => standaloneBmsRootPaths = Array.AsReadOnly((value ?? []).ToArray());
    }

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

    /// <summary>同じ取り込み操作の分類に使う全使用リソースの必須設定です。</summary>
    public bool RequireCompleteUsedResourcesForAutoInstall { get; init; }

    public bool AutoApplyAmbiguousInstallDestination { get; init; }

    /// <summary>取り込み後の自動推定に続く導入の有効性を、取り込み開始時の設定で固定します。</summary>
    public bool AutoInstallAfterAutomaticEstimation { get; init; }

    /// <summary>導入先の自動設定に各譜面が必要とする最低音源充足率を捕捉します。許可範囲は90～100で、範囲外は評価側で拒否します。</summary>
    public int InstallDestinationAutoApplyMinAudioHealth { get; init; } = 100;

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

    /// <summary>今回の操作で登録と配置を一緒に捕捉した公開入力です。</summary>
    internal BeMusicSeeker.Models.LR2.Lr2ConfigSnapshot Lr2Input { get; init; }

    /// <summary>操作の他設定を保ち、同時捕捉した登録・配置を接続します。</summary>
    internal BmsLibraryOptionsSnapshot WithLr2Input(BeMusicSeeker.Models.LR2.Lr2ConfigSnapshot input)
        => (input?.Placement == null ? this : WithLr2Placement(input.Placement)) with { Lr2Input = input };

    /// <summary>所有者へ初回の分類用配置だけを渡します。</summary>
    internal CustomFolderOutputSettingsSnapshot CaptureOutputPlacement() => new()
    {
        OperationModeLR2DB = OperationModeLR2DB,
        LR2RootPath = LR2RootPath,
        LR2CustomFolderOutputBaseDir = LR2CustomFolderOutputBaseDir,
        LR2CustomFolderOutputBaseDirRootType = LR2CustomFolderOutputBaseDirRootType,
        LR2CustomFolderAdditionalOutputBaseDirs = LR2CustomFolderAdditionalOutputBaseDirsSerialized
            ?? CustomFolderOutputBaseRegistry.SerializeBaseDirectories(LR2CustomFolderAdditionalOutputBaseDirs)
    };

    /// <summary>同時捕捉した公開済み配置を、登録と同じ操作入力へ反映します。</summary>
    internal BmsLibraryOptionsSnapshot WithLr2Placement(CustomFolderOutputSettingsSnapshot placement) => this with
    {
        LR2CustomFolderOutputBaseDir = placement.LR2CustomFolderOutputBaseDir,
        LR2CustomFolderOutputBaseDirRootType = placement.LR2CustomFolderOutputBaseDirRootType,
        LR2CustomFolderAdditionalOutputBaseDirs = CustomFolderOutputBaseRegistry.DeserializeBaseDirectories(placement.LR2CustomFolderAdditionalOutputBaseDirs),
        LR2CustomFolderAdditionalOutputBaseDirsSerialized = placement.LR2CustomFolderAdditionalOutputBaseDirs
    };

    /// <summary>確認済みの同所prefixだけを登録・関連導入先・公開配置へ反映し、走査結果や元snapshotを変更しません。</summary>
    internal BmsLibraryOptionsSnapshot WithConfirmedRootPrefixes(IReadOnlyList<string> beforeRoots, IReadOnlyList<string> confirmedRoots)
    {
        string Replace(string path) => ApplyConfirmedRootPrefixes(path, beforeRoots, confirmedRoots);
        CustomFolderOutputSettingsSnapshot placement = (Lr2Input?.Placement ?? CaptureOutputPlacement()).WithDirectoryPaths(Replace);
        BmsLibraryOptionsSnapshot result = WithLr2Placement(placement) with
        {
            StandaloneBmsRootPaths = StandaloneBmsRootPaths.Select(Replace).ToArray(),
            BMSInstallDir = beforeRoots.Any(root => CustomFolderOutputBaseSearchRootSyncService.IsSameDirectory(root, BMSInstallDir))
                ? Replace(BMSInstallDir) : BMSInstallDir
        };
        return Lr2Input == null ? result : result.WithLr2Input(Lr2Input.With(Lr2Input.RegisteredRoots.Select(Replace).ToArray(), placement));
    }

    /// <summary>最も具体的な確認済み同所prefixを設定pathへ適用します。登録順によらず子の確認要素を保ち、未確認suffix・末尾区切り・別場所の値は保持します。</summary>
    internal static string ApplyConfirmedRootPrefixes(string path, IReadOnlyList<string> beforeRoots, IReadOnlyList<string> confirmedRoots)
    {
        if (string.IsNullOrEmpty(path)) { return path; }
        int matchedLength = -1;
        string confirmedPrefix = null;
        for (int i = 0; i < beforeRoots.Count; i++)
        {
            string before = beforeRoots[i].TrimEnd('\\', '/');
            string after = confirmedRoots[i].TrimEnd('\\', '/');
            // 登録は入口で構文正規化済みです。確認元のOrdinalIgnoreCase同所照合は綴りだけを返し、文字数や場所は変えません。
            if (string.Equals(before, after, StringComparison.Ordinal) || before.Length != after.Length
                || before.Length <= matchedLength || !path.StartsWith(before, StringComparison.OrdinalIgnoreCase)
                || (path.Length > before.Length && path[before.Length] != '\\' && path[before.Length] != '/')) { continue; }
            matchedLength = before.Length;
            confirmedPrefix = after;
        }
        return matchedLength < 0 ? path : string.Concat(confirmedPrefix, path.AsSpan(matchedLength));
    }

    /// <summary>登録以外の設定入力を捕捉します。出力配置は保存段階の公開値へ揃え、登録はLibraryのsnapshot取得時に同じ確定readerから捕捉します。</summary>
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
            RequireCompleteUsedResourcesForAutoInstall = settings.RequireCompleteUsedResourcesForAutoInstall,
            AutoApplyAmbiguousInstallDestination = settings.AutoApplyAmbiguousInstallDestination,
            AutoInstallAfterAutomaticEstimation = settings.AutoInstallAfterAutomaticEstimation,
            InstallDestinationAutoApplyMinAudioHealth = settings.InstallDestinationAutoApplyMinAudioHealth,
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
