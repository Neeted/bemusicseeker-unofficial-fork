using System;
using System.Collections.Generic;
using System.Linq;

namespace BeMusicSeeker.Models.BmsLibraryInternal;

/// <summary>
/// ライブラリ変更差分から捕捉した不変の移転要求です。保存行を保持しません。
/// </summary>
internal sealed class CatalogRelocationRequest
{
    /// <summary>確定した移転と管理出力の除外範囲をコピーし、DB確定まで変更不能に保持します。</summary>
    internal CatalogRelocationRequest(
        IEnumerable<CatalogFolderPathReplacement> folderPathChanges,
        IEnumerable<BmsSongPathReplacement> bmsPathReplacements,
        IEnumerable<BmsonSongPathReplacement> bmsonPathReplacements,
        IEnumerable<string> folderProjectionExcludedDirectories = null)
    {
        FolderProjectionExcludedDirectories = Snapshot(folderProjectionExcludedDirectories);
        FolderPathChanges = Snapshot(folderPathChanges);
        BmsPathReplacements = Snapshot(bmsPathReplacements);
        BmsonPathReplacements = Snapshot(bmsonPathReplacements);
    }

    /// <summary>管理出力主体が所有するため局所移転から除外する物理範囲です。</summary>
    internal IReadOnlyList<string> FolderProjectionExcludedDirectories { get; }

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
    /// <summary>exact通常行・成功した物理subtree・採番済みcustomファイルを区別して旧新配置を保持します。</summary>
    internal CatalogFolderPathReplacement(string oldFolderPath, string newFolderPath, bool includesDescendants = false, bool isCustomFile = false)
    {
        OldFolderPath = oldFolderPath;
        NewFolderPath = newFolderPath;
        IncludesDescendants = includesDescendants;
        IsCustomFile = isCustomFile;
    }

    /// <summary>配下全体の物理移転成功を表し、exact行だけの変更と区別します。</summary>
    internal bool IncludesDescendants { get; }

    /// <summary>customファイルの採番を含む確定移転です。</summary>
    internal bool IsCustomFile { get; }

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
             catalogRevision: 0,
             addedCharts: [],
             pathFacts: [],
             removalRequests: []);

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
        int catalogRevision,
        IEnumerable<CatalogChartMutationFact> addedCharts,
        IEnumerable<CatalogRelocationPathFact> pathFacts,
        IEnumerable<OwnedChartRemoveRequest> removalRequests,
        int folderDbTargetRows = 0,
        int folderDbFullScanCount = 0,
        IReadOnlyDictionary<string, OwnedChartToken> bmsPathMembershipIdentity = null,
        CatalogCommitReceipt commit = null,
        IEnumerable<CatalogFolderPathReplacement> folderPathChanges = null)
    {
        FolderPathChanges = Array.AsReadOnly([.. folderPathChanges ?? []]);
        Applied = applied;
        Commit = commit;
        StorageRowsVersion = storageRowsVersion;
        FolderDbMs = folderDbMs;
        FolderDbTargetRows = folderDbTargetRows;
        FolderDbFullScanCount = folderDbFullScanCount;
        BmsPathMembershipIdentity = bmsPathMembershipIdentity;
        BmsPathDbMs = bmsPathDbMs;
        BmsonPathDbMs = bmsonPathDbMs;
        BmsRemovalDbMs = bmsRemovalDbMs;
        BmsonRemovalDbMs = bmsonRemovalDbMs;
        LiveApplyMs = liveApplyMs;
        OwnedCollectionApplied = ownedCollectionApplied;
        CatalogRevision = catalogRevision;
        Kind = applied
            ? CatalogMutationApplyKind.GenericMutation
            : CatalogMutationApplyKind.NoOp;
        AddedCharts = Array.AsReadOnly([.. (addedCharts ?? []).Where(fact => fact != null)]);
        PathFacts = Array.AsReadOnly([.. (pathFacts ?? []).Where(fact => fact != null)]);
        MovedCharts = PathFacts;
        RemovedCharts = CatalogChartMutationFact.CreateRemovalFacts(removalRequests);
    }

    /// <summary>同じcatalog確定境界の現在値と差分です。導入済みentryへ要求値を再適用せず、このAfterを共有します。</summary>
    internal CatalogCommitReceipt Commit { get; }

    internal bool Applied { get; }

    internal OwnedChartCollectionVersionSnapshot StorageRowsVersion { get; }

    /// <summary>確定した folder DB 対象行数。</summary>
    internal int FolderDbTargetRows { get; }

    /// <summary>folder DB 全件走査回数。</summary>
    internal int FolderDbFullScanCount { get; }

    /// <summary>確定時のBMS完全一致path所属rootです。LR2の件数・実パス準備に使い、表示値の更新では変わりません。</summary>
    internal IReadOnlyDictionary<string, OwnedChartToken> BmsPathMembershipIdentity { get; }

    internal long FolderDbMs { get; }

    internal long BmsPathDbMs { get; }

    internal long BmsonPathDbMs { get; }

    internal long BmsRemovalDbMs { get; }

    internal long BmsonRemovalDbMs { get; }

    internal long LiveApplyMs { get; }

    internal bool OwnedCollectionApplied { get; }

    internal int CatalogRevision { get; }

    internal CatalogMutationApplyKind Kind { get; }

    internal IReadOnlyList<CatalogChartMutationFact> AddedCharts { get; }

    internal IReadOnlyList<CatalogChartMutationFact> RemovedCharts { get; }

    internal IReadOnlyList<CatalogRelocationPathFact> MovedCharts { get; }

    internal IReadOnlyList<CatalogRelocationPathFact> PathFacts { get; }

    /// <summary>成功した物理移転範囲とcustom実宛先です。譜面形式に依存せず局所依存捕捉へ渡します。</summary>
    internal IReadOnlyList<CatalogFolderPathReplacement> FolderPathChanges { get; }


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
