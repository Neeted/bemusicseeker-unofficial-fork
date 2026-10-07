using System;
using System.Collections.Generic;
using System.Linq;

namespace BeMusicSeeker.Models.BmsLibraryInternal;

/// <summary>
/// ライブラリ変更差分から捕捉した不変の移転要求です。保存行を保持しません。
/// </summary>
internal sealed class CatalogRelocationRequest
{
    internal CatalogRelocationRequest(
        IEnumerable<CatalogFolderPathReplacement> folderPathChanges,
        IEnumerable<BmsSongPathReplacement> bmsPathReplacements,
        IEnumerable<BmsonSongPathReplacement> bmsonPathReplacements)
    {
        FolderPathChanges = Snapshot(folderPathChanges);
        BmsPathReplacements = Snapshot(bmsPathReplacements);
        BmsonPathReplacements = Snapshot(bmsonPathReplacements);
    }

    internal IReadOnlyList<CatalogFolderPathReplacement> FolderPathChanges { get; }

    internal IReadOnlyList<BmsSongPathReplacement> BmsPathReplacements { get; }

    internal IReadOnlyList<BmsonSongPathReplacement> BmsonPathReplacements { get; }

    internal bool HasChanges => FolderPathChanges.Count > 0
        || BmsPathReplacements.Count > 0
        || BmsonPathReplacements.Count > 0;

    private static IReadOnlyList<T> Snapshot<T>(IEnumerable<T> values)
    {
        return Array.AsReadOnly([.. (values ?? []).Where(value => value != null)]);
    }
}

/// <summary>
/// 移転トランザクションへ渡す不変の旧・新フォルダパスです。
/// </summary>
internal sealed class CatalogFolderPathReplacement
{
    internal CatalogFolderPathReplacement(string oldFolderPath, string newFolderPath)
    {
        OldFolderPath = oldFolderPath;
        NewFolderPath = newFolderPath;
    }

    internal string OldFolderPath { get; }

    internal string NewFolderPath { get; }
}

/// <summary>
/// 同じ所持識別を継承するBMS移転前後の不変共通値です。
/// </summary>
internal sealed class BmsSongPathReplacement
{
    internal BmsSongPathReplacement(
        ChartFile song,
        ChartFile liveOwner,
        string oldPath,
        ResourceHealthMaintenanceSnapshot maintenanceInfo)
    {
        Song = song ?? throw new ArgumentNullException(nameof(song));
        LiveOwner = liveOwner ?? throw new ArgumentNullException(nameof(liveOwner));
        OldPath = oldPath;
        MaintenanceInfo = maintenanceInfo;
    }

    internal ChartFile Song { get; }

    internal ChartFile LiveOwner { get; }

    internal string OldPath { get; }

    internal ResourceHealthMaintenanceSnapshot MaintenanceInfo { get; }
}

/// <summary>
/// 同じ所持識別を継承するBMSON移転前後の不変共通値です。
/// </summary>
internal sealed class BmsonSongPathReplacement
{
    internal BmsonSongPathReplacement(
        ChartFile song,
        ChartFile liveOwner,
        string oldPath)
    {
        Song = song ?? throw new ArgumentNullException(nameof(song));
        LiveOwner = liveOwner ?? throw new ArgumentNullException(nameof(liveOwner));
        OldPath = oldPath;
    }

    internal ChartFile Song { get; }

    internal ChartFile LiveOwner { get; }

    internal string OldPath { get; }
}

/// <summary>
/// Timing facts returned by the durable catalog relocation transaction.
/// </summary>
internal sealed class CatalogRelocationDbReceipt
{
    /// <summary>exact path query で取得し移転した folder 行数。</summary>
    internal int FolderDbTargetRows { get; set; }

    /// <summary>folder query の全件走査回数。対象 path query のみを使用するため 0 です。</summary>
    internal int FolderDbFullScanCount { get; }

    internal long FolderDbMs { get; set; }

    internal long BmsPathDbMs { get; set; }

    internal long BmsonPathDbMs { get; set; }

    internal long BmsRemovalDbMs { get; set; }

    internal long BmsonRemovalDbMs { get; set; }
}

/// <summary>
/// 一つの移転・削除コマンドの永続確定と共通現在値の適用を終えた結果です。
/// </summary>
internal sealed class CatalogMutationReceipt
{
    internal static CatalogMutationReceipt NotApplied { get; } =
        new(
            applied: false,
            new OwnedChartCollectionVersionSnapshot(0),
            folderDbMs: 0,
            bmsPathDbMs: 0,
            bmsonPathDbMs: 0,
            bmsRemovalDbMs: 0,
            bmsonRemovalDbMs: 0,
            liveApplyMs: 0,
            ownedCollectionApplied: false,
             ownedCollectionVersion: 0,
             addedCharts: [],
             pathFacts: [],
             removalRequests: [],
             bmsonCanonicalOrderNormalized: false);

    /// <summary>
    /// canonical DB と live catalog の確定結果を、関連参照の反映と操作内診断へ渡します。
    /// folder の件数は要求 path 数ではなく、実際に取得して移転した行数です。
    /// </summary>
    internal CatalogMutationReceipt(
        bool applied,
        OwnedChartCollectionVersionSnapshot storageRowsVersion,
        long folderDbMs,
        long bmsPathDbMs,
        long bmsonPathDbMs,
        long bmsRemovalDbMs,
        long bmsonRemovalDbMs,
        long liveApplyMs,
        bool ownedCollectionApplied,
        int ownedCollectionVersion,
        IEnumerable<CatalogChartMutationFact> addedCharts,
        IEnumerable<CatalogRelocationPathFact> pathFacts,
        IEnumerable<OwnedChartRemoveRequest> removalRequests,
        bool bmsonCanonicalOrderNormalized = false,
        int folderDbTargetRows = 0,
        int folderDbFullScanCount = 0)
    {
        Applied = applied;
        StorageRowsVersion = storageRowsVersion;
        FolderDbMs = folderDbMs;
        FolderDbTargetRows = folderDbTargetRows;
        FolderDbFullScanCount = folderDbFullScanCount;
        BmsPathDbMs = bmsPathDbMs;
        BmsonPathDbMs = bmsonPathDbMs;
        BmsRemovalDbMs = bmsRemovalDbMs;
        BmsonRemovalDbMs = bmsonRemovalDbMs;
        LiveApplyMs = liveApplyMs;
        OwnedCollectionApplied = ownedCollectionApplied;
        OwnedCollectionVersion = ownedCollectionVersion;
        Kind = applied
            ? CatalogMutationApplyKind.GenericMutation
            : CatalogMutationApplyKind.NoOp;
        AddedCharts = Array.AsReadOnly([.. (addedCharts ?? []).Where(fact => fact != null)]);
        PathFacts = Array.AsReadOnly([.. (pathFacts ?? []).Where(fact => fact != null)]);
        MovedCharts = PathFacts;
        RemovedCharts = CatalogChartMutationFact.CreateRemovalFacts(removalRequests);
        BmsonCanonicalOrderNormalized = bmsonCanonicalOrderNormalized;
    }

    internal bool Applied { get; }

    internal OwnedChartCollectionVersionSnapshot StorageRowsVersion { get; }

    /// <summary>確定した folder DB 対象行数。</summary>
    internal int FolderDbTargetRows { get; }

    /// <summary>folder DB 全件走査回数。</summary>
    internal int FolderDbFullScanCount { get; }

    internal long FolderDbMs { get; }

    internal long BmsPathDbMs { get; }

    internal long BmsonPathDbMs { get; }

    internal long BmsRemovalDbMs { get; }

    internal long BmsonRemovalDbMs { get; }

    internal long LiveApplyMs { get; }

    internal bool OwnedCollectionApplied { get; }

    internal int OwnedCollectionVersion { get; }

    internal CatalogMutationApplyKind Kind { get; }

    internal IReadOnlyList<CatalogChartMutationFact> AddedCharts { get; }

    internal IReadOnlyList<CatalogChartMutationFact> RemovedCharts { get; }

    internal IReadOnlyList<CatalogRelocationPathFact> MovedCharts { get; }

    internal IReadOnlyList<CatalogRelocationPathFact> PathFacts { get; }

    /// <summary>今回のupsertで初回BMSON canonical順序正規化が発生したか。</summary>
    internal bool BmsonCanonicalOrderNormalized { get; }

}

/// <summary>
/// 移転結果に含める不変のパス変更事実です。
/// </summary>
internal sealed class CatalogRelocationPathFact
{
    /// <summary>内部移転で捕捉した対象識別とパス変更を保持します。保存行や現在値への逆参照は持ちません。</summary>
    /// <param name="kind">移転した譜面形式。</param>
    /// <param name="oldPath">移転前のDBパス。</param>
    /// <param name="newPath">移転後のDBパス。</param>
    /// <param name="md5">移転対象のMD5。パス保護だけの事実はnull。</param>
    /// <param name="sha256">移転対象のSHA256。パス保護だけの事実はnull。</param>
    /// <param name="token">内部移転で継承した所持識別。パス保護だけの事実はnull。</param>
    internal CatalogRelocationPathFact(
        ChartFileKind kind,
        string oldPath,
        string newPath,
        string md5 = null,
        string sha256 = null,
        OwnedChartToken token = null)
    {
        Token = token;
        Kind = kind;
        OldPath = oldPath;
        NewPath = newPath;
        Md5 = md5;
        Sha256 = sha256;
    }

    /// <summary>内部移転で継承した所持識別です。パス保護だけの事実はnullです。</summary>
    internal OwnedChartToken Token { get; }

    internal ChartFileKind Kind { get; }

    internal string OldPath { get; }

    internal string NewPath { get; }

    internal string Md5 { get; }

    internal string Sha256 { get; }
}
