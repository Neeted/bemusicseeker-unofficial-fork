namespace BeMusicSeeker.Models.BmsLibraryInternal;

/// <summary>
/// 一つの mutation session の反映回数を値として保持します。成功件数ではなく owner への試行回数で、
/// SQL の chunk 数とは区別します。required publication は準備回数であり、subscriber の成功数ではありません。
/// </summary>
internal readonly record struct LibraryMutationSessionApplyCounts
{
    /// <summary>catalog relocation/removal owner の呼出回数。</summary>
    internal int CatalogApplyCount { get; init; }

    /// <summary>installed target と install row の一括反映 owner の呼出回数。</summary>
    internal int InstalledTargetApplyCount { get; init; }

    /// <summary>package reference owner の呼出回数。</summary>
    internal int PackageReferenceApplyCount { get; init; }

    /// <summary>resource reverse lookup の集合反映 owner の呼出回数。</summary>
    internal int ReverseLookupApplyCount { get; init; }

    /// <summary>LR2 normal folder 同期 owner の呼出回数。</summary>
    internal int Lr2SyncCount { get; init; }

    /// <summary>通常の required publication を準備した回数。障害診断の通知は含めません。</summary>
    internal int RequiredPublicationCount { get; init; }

    /// <summary>catalog DB が exact path query で取得し、移転を確定した folder 行数。</summary>
    internal int FolderDbTargetRows { get; init; }

    /// <summary>folder DB 全件走査回数。exact path query のみを使う経路では 0 です。</summary>
    internal int FolderDbFullScanCount { get; init; }
}
