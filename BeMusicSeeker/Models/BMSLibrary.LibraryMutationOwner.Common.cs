using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Runtime.ExceptionServices;
using System.Threading;
using BeMusicSeeker.Models.BmsLibraryInternal;
using BeMusicSeeker.Models.LR2;
using BeMusicSeeker.Models.Utils;
using Ribbit.Logging;

namespace BeMusicSeeker.Models;

internal sealed partial class LibraryMutationOwner
{
    /// <summary>pending install currentness と同期する digest mutation 世代です。</summary>
    internal long OwnedDigestMutationGeneration => Volatile.Read(ref ownedDigestMutationGeneration);

    /// <summary>digest mutation 中の索引入力と currentness 世代を開始します。</summary>
    internal IDisposable BeginOwnedDigestMutationWindow()
    {
        lock (pendingInstallEstimateCurrentnessGate)
        {
            catalogOwnedCollectionOwner.BeginDigestMutationWindow();
            ownedDigestMutationGeneration++;
        }
        try
        {
            return new OwnedDigestMutationWindowScope(this, resourceHealthOwner.BeginInputMutation());
        }
        catch
        {
            lock (pendingInstallEstimateCurrentnessGate)
            {
                catalogOwnedCollectionOwner.EndDigestMutationWindow();
                ownedDigestMutationGeneration++;
            }
            throw;
        }
    }

    private void EndOwnedDigestMutationWindow(ResourceHealthIndexOwner.ResourceHealthInputMutation resourceHealthMutation)
    {
        try
        {
            resourceHealthMutation.Dispose();
            resourceHealthOwner.RebaseAfterInputMutation(
                resourceHealthMutation,
                GetCurrentResourceHealthIndexVersion());
        }
        finally
        {
            lock (pendingInstallEstimateCurrentnessGate)
            {
                catalogOwnedCollectionOwner.EndDigestMutationWindow();
                ownedDigestMutationGeneration++;
            }
        }
    }

    /// <summary>digest mutation の入力境界が現在開いているかを返します。</summary>
    internal bool IsOwnedDigestMutationWindowActive()
    {
        return catalogOwnedCollectionOwner.IsDigestMutationWindowActive();
    }

    /// <summary>digest mutation の完了を待ちます。</summary>
    internal void WaitForOwnedDigestMutationWindowIdle(CancellationToken cancellationToken = default)
    {
        catalogOwnedCollectionOwner.WaitForDigestMutationWindowIdle(cancellationToken);
    }

    private sealed class OwnedDigestMutationWindowScope(
        LibraryMutationOwner owner,
        ResourceHealthIndexOwner.ResourceHealthInputMutation resourceHealthMutation) : IDisposable
    {
        private LibraryMutationOwner owner = owner;

        private ResourceHealthIndexOwner.ResourceHealthInputMutation resourceHealthMutation = resourceHealthMutation;

        public void Dispose()
        {
            LibraryMutationOwner currentOwner = Interlocked.Exchange(ref owner, null);
            ResourceHealthIndexOwner.ResourceHealthInputMutation currentResourceHealthMutation = Interlocked.Exchange(ref resourceHealthMutation, null);
            if (currentOwner != null && currentResourceHealthMutation != null)
            {
                currentOwner.EndOwnedDigestMutationWindow(currentResourceHealthMutation);
            }
        }
    }

    /// <summary>所持譜面の派生 collection を失効させます。</summary>
    internal void InvalidateOwnedChartCollection()
    {
        catalogOwnedCollectionOwner.Invalidate();
    }

    /// <summary>所持譜面の確定 version と派生索引を公開します。</summary>
    internal int NotifyOwnedChartCollectionChanged(
        int committedVersion = 0,
        bool rebasePlaylistResolveIndex = false)
    {
        int version = committedVersion > 0
            ? committedVersion
            : catalogOwnedCollectionOwner.IncrementVersion();
        catalogOwnedCollectionOwner.RebaseHashIndexSnapshot();
        if (rebasePlaylistResolveIndex)
        {
            catalogOwnedCollectionOwner.RebasePlaylistLibraryResolveIndexSnapshot(catalogStorageRowsOwner);
        }
        raiseOwnedCollectionVersionChanged();
        return version;
    }

    /// <summary>
    /// scan/property producer が確定した storage rows の置換を、所持状態と派生索引へ反映します。
    /// DB writer、resource input、collection version、公開通知の順序をこの owner が管理します。
    /// </summary>
    internal void ApplyCatalogStorageRows(
        IEnumerable<BMSFile> bmsFiles,
        IEnumerable<LR2SongDBExtended.bmson_song> bmsonSongs,
        bool replaceBmsRows,
        bool replaceBmsonRows,
        bool notifyBmsRows,
        bool notifyBmsonRows,
        Action<Action> postLeaseNotificationObserver = null)
    {
        List<BMSFile> normalizedBmsRows = replaceBmsRows
            ? NormalizeBmsStorageRows(bmsFiles)
            : [];
        List<LR2SongDBExtended.bmson_song> normalizedBmsonRows = replaceBmsonRows
            ? NormalizeBmsonStorageRows(bmsonSongs)
            : [];
        CatalogStorageRowsReplacementRequest request = catalogMutationOwner.CreateStorageRowsReplacementRequest(
            normalizedBmsRows,
            normalizedBmsonRows,
            replaceBmsRows,
            replaceBmsonRows);
        bool bmsRowsChanged = request.BmsRowsChanged;
        bool bmsonRowsChanged = request.BmsonRowsChanged;
        if (!bmsRowsChanged && !bmsonRowsChanged)
        {
            return;
        }

        CatalogStorageRowsReplacementReceipt replacementReceipt;
        Action publishReplacementEffects;
        using (resourceHealthOwner.BeginInputMutation())
        {
            catalogOwnedCollectionOwner.InvalidateHashIndexSnapshot();
            catalogOwnedCollectionOwner.InvalidatePlaylistLibraryResolveIndexSnapshot();
            replacementReceipt = catalogMutationOwner.ApplyStorageRowsReplacement(request);
            bmsRowsChanged = replacementReceipt.BmsRowsChanged;
            bmsonRowsChanged = replacementReceipt.BmsonRowsChanged;
            if (bmsRowsChanged)
            {
                markDuplicateWarningFullClearPending();
            }
            int ownedCollectionVersion = replacementReceipt.OwnedCollectionVersion;
            catalogOwnedCollectionOwner.InvalidateHashIndexSnapshot();
            InvalidateInstalledDirectoryIndex();
            InvalidateParentFolderListCache();
            if (bmsonRowsChanged)
            {
                InvalidateInstallEstimationMetadataProfileCache();
            }
            InvalidateDuplicateChartGroupsCache();
            resourceHealthOwner.Invalidate(bmsRowsChanged && bmsonRowsChanged
                ? "catalog_storage_rows_changed"
                : (bmsRowsChanged ? "bmsfiles_changed" : "bmsons_changed"));
            installDestinationStateOwner.PruneToCurrentOwnedCharts();

            bool notifiesBmsFiles = notifyBmsRows && bmsRowsChanged;
            bool notifiesBmsonSongs = notifyBmsonRows && bmsonRowsChanged;
            publishReplacementEffects = () =>
            {
                int publishedOwnedCollectionVersion = NotifyOwnedChartCollectionChanged(ownedCollectionVersion);
                catalogOwnedCollectionOwner.InvalidatePlaylistLibraryResolveIndexSnapshot(publishedOwnedCollectionVersion);
                if (notifiesBmsFiles || notifiesBmsonSongs)
                {
                    PublishExternalReplacementNormalLibraryRefreshNotification(
                        notifiesBmsFiles,
                        notifiesBmsonSongs);
                }
                notifyStorageRowsChanged(notifiesBmsFiles, notifiesBmsonSongs);
            };
        }
        if (postLeaseNotificationObserver != null)
        {
            postLeaseNotificationObserver(publishReplacementEffects);
        }
        else
        {
            TryInvokePostLeaseNotification(
                publishReplacementEffects,
                "catalog_storage_rows_publication_failed");
        }
    }

    private sealed class OwnedChartCollectionMutationResult
    {
        public OwnedChartCollectionMutationResult()
            : this(new ResourceHealthIndexMutation())
        {
        }

        public OwnedChartCollectionMutationResult(ResourceHealthIndexMutation resourceHealthMutation)
        {
            ResourceHealthMutation = resourceHealthMutation ?? throw new ArgumentNullException(nameof(resourceHealthMutation));
        }

        public OwnedChartCollectionStorageMutation StorageMutation { get; } = new();

        public List<LibraryChartDigestChange> DigestChanges { get; } = [];

        public CatalogDigestMutationRequest DigestMutationRequest { get; set; }

        public bool DigestMutationApplied { get; set; }

        public InstalledChartLookupMutation InstalledLookupMutation { get; set; } = new();

        public bool InstalledLookupMutationApplied { get; set; }

        public bool OwnedHashIndexMutationApplied { get; set; }

        /// <summary>今回のmutation factsをplaylist resolve rootへ適用済みか。</summary>
        public bool PlaylistResolveIndexMutationApplied { get; set; }

        public bool PlaylistResolveIndexInvalidated { get; set; }

        public bool InstallMetadataCacheInvalidated { get; set; }

        public InstallDestinationRuntimeStateMutation InstallDestinationRuntimeStateMutation { get; } = new();

        public bool InstallDestinationRuntimeStateApplied { get; set; }

        public IReadOnlyList<ChartFile> InstallDestinationChangedCharts => InstallDestinationRuntimeStateMutation.AppliedCharts;

        public bool InstallEstimationMetadataProfileCacheInvalidated { get; set; }

        public int AddedCount { get; set; }

        public int RemovedCount { get; set; }

        public int MovedCount { get; set; }

        public int DigestChangedCount => DigestChanges.Count;

        public int InstallDestinationChangedCount { get; set; }

        public int InstalledPackagePathChangedCount { get; set; }

        public bool ParentFolderInvalidated { get; set; }

        public bool DuplicateCacheInvalidated { get; set; }

        public bool OwnedCollectionChanged { get; set; }

        public bool OwnedCollectionChangeNotified { get; set; }

        public bool OwnedCollectionVersionAlreadyAdvanced { get; set; }

        /// <summary>今回のcatalog mutationで初回BMSON canonical順序正規化が発生したか。</summary>
        public bool BmsonCanonicalOrderNormalized { get; set; }

        public int OwnedCollectionVersion { get; set; }

        public int NormalLibraryRefreshNotificationVersion { get; set; }

        public ResourceHealthIndexMutation ResourceHealthMutation { get; }

        public ResourceHealthIndexDispatchResult ResourceHealthDispatchResult { get; set; }

        public bool ResourceHealthIndexInvalidated
        {
            get => ResourceHealthMutation.Invalidate;
            set => ResourceHealthMutation.Invalidate = value;
        }

        public bool WarningPresentationChanged { get; set; }

        public bool MaintenancePresentationChanged { get; set; }

        public bool BmsFilesStorageRowsChanged { get; set; }

        public bool BmsonSongsStorageRowsChanged { get; set; }

        public bool StorageRowsChanged => BmsFilesStorageRowsChanged || BmsonSongsStorageRowsChanged;

        public bool StorageRowsRemoveDeltaComplete { get; set; }

        public bool ShouldDispatchInstalledLookup => InstalledLookupMutation?.HasChanges == true;

        public bool HasLoggableChanges => AddedCount > 0
            || RemovedCount > 0
            || MovedCount > 0
            || DigestChangedCount > 0
            || InstallDestinationChangedCount > 0
            || InstalledPackagePathChangedCount > 0
            || ParentFolderInvalidated
            || DuplicateCacheInvalidated
            || OwnedCollectionChanged
            || ResourceHealthMutation.HasChanges
            || InstallEstimationMetadataProfileCacheInvalidated
            || WarningPresentationChanged
            || MaintenancePresentationChanged
            || StorageRowsChanged
            || InstalledLookupMutation?.HasChanges == true;
    }

    private sealed class OwnedChartCollectionStorageMutation
    {
        public List<BMSFile> AddedBmsFiles { get; } = [];

        public List<LR2SongDBExtended.bmson_song> AddedBmsonSongs { get; } = [];

        public List<ChartFile> AddedCharts { get; } = [];

        public List<OwnedChartRemoveRequest> RemoveRequests { get; } = [];

        public List<LibraryChartPathChange> PathChanges { get; } = [];

        public int AddedCount => AddedCharts.Count;

        public int RemovedCount => RemoveRequests.Count;

        public int MovedCount => PathChanges.Count;

        public bool HasChanges => AddedCount > 0 || RemovedCount > 0 || MovedCount > 0;

        public void AddAddedTargets(ChartStorageTargetSet addedTargets)
        {
            if (addedTargets == null)
            {
                return;
            }

            AddedBmsFiles.AddRange(addedTargets.BmsFiles);
            AddedBmsonSongs.AddRange(addedTargets.BmsonSongs);
            AddedCharts.AddRange(addedTargets.Charts.Where(chart => chart != null));
        }
    }

    private static bool HasOwnedHashSetChanges(OwnedChartCollectionMutationResult result)
    {
        return result != null
            && (result.StorageRowsChanged
                || result.DigestChangedCount > 0
                || result.AddedCount > 0
                || result.RemovedCount > 0
                || result.DuplicateCacheInvalidated);
    }

    /// <summary>
    /// durable mutationの旧新 hash factsを、構築済み owned hash rootへ一度だけ渡します。
    /// </summary>
    /// <param name="result">catalog mutationの結果。</param>
    private void ApplyOwnedChartHashIndexMutation(OwnedChartCollectionMutationResult result)
    {
        if (result == null || result.OwnedHashIndexMutationApplied)
        {
            return;
        }

        bool hasDigestFacts = result.DigestChangedCount > 0 && result.DigestMutationApplied;
        bool hasStorageHashFacts = result.StorageMutation.RemoveRequests.Count > 0
            || result.StorageMutation.AddedCharts.Count > 0;
        if (!hasDigestFacts && !hasStorageHashFacts)
        {
            if (result.OwnedCollectionChanged)
            {
                catalogOwnedCollectionOwner.RebaseHashIndexSnapshot();
            }
            return;
        }

        var deltas = new List<OwnedChartHashIndexDelta>();
        bool requiresFullInvalidate = false;
        if (hasDigestFacts)
        {
            foreach (LibraryChartDigestChange change in result.DigestChanges)
            {
                if (change?.HasDigestChange == true)
                {
                    deltas.Add(new OwnedChartHashIndexDelta(
                        change.OldMd5,
                        change.OldSha256,
                        change.NewMd5,
                        change.NewSha256));
                }
            }
        }
        else
        {
            foreach (InstalledChartLookupMutationEntry removed in result.InstalledLookupMutation?.Removed ?? [])
            {
                deltas.Add(new OwnedChartHashIndexDelta(
                    removed.Md5,
                    removed.Sha256,
                    null,
                    null));
            }
            foreach (ChartFile added in result.StorageMutation.AddedCharts.Where(chart => chart != null))
            {
                deltas.Add(new OwnedChartHashIndexDelta(
                    null,
                    null,
                    added.Md5,
                    added.Sha256));
            }

            requiresFullInvalidate = result.InstalledLookupMutation?.RequiresFullInvalidate == true
                && HasUnknownOwnedHashRemoval(result.StorageMutation.RemoveRequests);
        }

        // 旧行の exact facts が欠ける場合は、候補 hash を部分的に適用せず
        // 既存の full rebuild 契約へ戻します。file scan の削除 payload 不在もここで扱います。
        if (result.InstalledLookupMutation?.RequiresFullInvalidate == true
            && (HasUnknownOwnedHashRemoval(result.StorageMutation.RemoveRequests)
                || result.RemovedCount > result.StorageMutation.RemoveRequests.Count
                || (result.StorageMutation.AddedCharts.Count > 0
                    && result.InstalledLookupMutation.Removed.Count == 0)))
        {
            requiresFullInvalidate = true;
        }
        catalogOwnedCollectionOwner.ApplyHashIndexDeltas(deltas, requiresFullInvalidate);
        result.OwnedHashIndexMutationApplied = true;
    }

    private static bool HasUnknownOwnedHashRemoval(IEnumerable<OwnedChartRemoveRequest> removeRequests)
    {
        foreach (OwnedChartRemoveRequest request in removeRequests ?? [])
        {
            if (request == null)
            {
                continue;
            }
            if (!request.HasCapturedFacts)
            {
                return true;
            }
            if (string.IsNullOrWhiteSpace(request.CapturedMd5))
            {
                return true;
            }
        }
        return false;
    }

    /// <summary>
    /// warmなplaylist resolve rootへ、今回の旧新path/hash factsだけを適用します。
    /// facts不足または全置換境界では既存の全失効契約へ戻します。
    /// </summary>
    /// <param name="result">catalog mutationの結果。</param>
    private void ApplyPlaylistLibraryResolveIndexMutation(OwnedChartCollectionMutationResult result)
    {
        if (result == null || result.PlaylistResolveIndexMutationApplied)
        {
            return;
        }

        result.PlaylistResolveIndexMutationApplied = true;
        if (catalogOwnedCollectionOwner.ApplyPlaylistLibraryResolveIndexMutation(
            catalogStorageRowsOwner,
            result.DigestChanges,
            result.DigestMutationApplied,
            result.InstalledLookupMutation,
            result.StorageMutation.AddedCharts,
            result.OwnedCollectionChanged,
            result.PlaylistResolveIndexInvalidated,
            result.BmsonCanonicalOrderNormalized,
            result.OwnedCollectionVersion))
        {
            result.PlaylistResolveIndexInvalidated = true;
        }
    }

    /// <summary>
    /// 完全な file scan replacement receipt を正本・resource・派生索引へ反映します。
    /// </summary>
    /// <param name="replacementEvent">scan が確定した置換結果。</param>
    /// <returns>lease 解放後に実行する公開処理。</returns>
    internal Action ApplyFileScanCatalogReplacement(FileScanCatalogReplacementEvent replacementEvent)
    {
        if (replacementEvent == null)
        {
            return null;
        }

        CatalogFileScanStorageReplacementReceipt receipt = replacementEvent.Receipt;
        if (replacementEvent.Request.HasDbDiff)
        {
            markDuplicateWarningFullClearPending();
        }
        resourceIndexOwner.Replace(
            replacementEvent.NextResourceIndex
                ?? LibraryResourceIndex.CreateFromScanResult(new ChartScanResult()));
        if (receipt.OwnedCollectionApplied)
        {
            LogOwnedChartCollectionSkippedRows("replace", receipt.FilterSummary);
        }

        OwnedChartCollectionMutationResult mutationResult = CreateFileScanMutationProjection(
            replacementEvent.Request,
            replacementEvent.ResourceHealthIndexCurrentAtBase);
        mutationResult.OwnedCollectionVersion = receipt.OwnedCollectionVersion;
        mutationResult.OwnedCollectionVersionAlreadyAdvanced = receipt.Applied;
        string dispatchReason = string.IsNullOrWhiteSpace(replacementEvent.Reason)
            ? "file_scan"
            : "file_scan_" + replacementEvent.Reason;
        Action duplicateChartGroupsPostLeaseNotification = DispatchOwnedChartCollectionMutation(
            mutationResult,
            dispatchReason,
            publishNormalRefreshNotification: false,
            publishOwnedCollectionNotifications: false,
            deferDuplicateChartGroupsNotification: true);
        return () =>
        {
            TryInvokePostLeaseNotification(
                duplicateChartGroupsPostLeaseNotification,
                "file_scan_duplicate_chart_groups_notification_failed");
            if (mutationResult.ParentFolderInvalidated)
            {
                TryInvokePostLeaseNotification(
                    NotifyParentFolderListCacheChanged,
                    "file_scan_parent_folder_notification_failed");
            }
            if (mutationResult.OwnedCollectionChanged)
            {
                TryInvokePostLeaseNotification(
                    () => PublishOwnedCollectionChangeNotification(mutationResult),
                    "file_scan_owned_collection_notification_failed");
            }
            TryInvokePostLeaseNotification(
                () => PublishNormalLibraryRefreshNotification(mutationResult),
                "file_scan_normal_refresh_publication_failed");
            TryInvokePostLeaseNotification(
                () => RaiseNormalLibraryRefreshNotificationVersionChanged(mutationResult),
                "file_scan_normal_refresh_notification_failed");
        };
    }

    /// <summary>
    /// scan residual facts を common mutation effects へ反映し、lease 解放後の
    /// duplicate・refresh 通知だけを返します。
    /// </summary>
    /// <param name="residualEvent">走査で確定した residual facts。</param>
    /// <returns>lease 解放後に実行する通知、または変更がない場合は <see langword="null"/>。</returns>
    internal Action ApplyFileScanCatalogResidualForScan(FileScanCatalogResidualEvent residualEvent)
    {
        if (residualEvent?.InstallDestinationChangedCharts?.Count > 0 != true)
        {
            return null;
        }

        IReadOnlyList<ChartFile> installDestinationChangedCharts =
            installDestinationStateOwner.ReattachFileScanResidualInstallDestinationCharts(
                residualEvent.InstallDestinationChangedCharts);
        if (installDestinationChangedCharts.Count == 0)
        {
            return null;
        }

        var mutationResult = new OwnedChartCollectionMutationResult
        {
            InstallDestinationChangedCount = installDestinationChangedCharts.Count,
            InstallEstimationMetadataProfileCacheInvalidated = true,
            DuplicateCacheInvalidated = true,
            WarningPresentationChanged = true
        };
        mutationResult.InstallDestinationRuntimeStateMutation.AppliedCharts.AddRange(
            installDestinationChangedCharts);
        string dispatchReason = string.IsNullOrWhiteSpace(residualEvent.Reason)
            ? "file_scan_residual"
            : "file_scan_residual_" + residualEvent.Reason;
        Action duplicateChartGroupsPostLeaseNotification = null;
        try
        {
            duplicateChartGroupsPostLeaseNotification = DispatchOwnedChartCollectionMutation(
                mutationResult,
                dispatchReason,
                publishNormalRefreshNotification: false,
                publishOwnedCollectionNotifications: false,
                deferDuplicateChartGroupsNotification: true);
        }
        catch
        {
            InvalidateDuplicateChartGroupsCache();
            InvalidateInstallEstimationMetadataProfileCache();
            if (mutationResult.InstallDestinationRuntimeStateMutation.HasChanges)
            {
                installDestinationStateOwner.PruneToCurrentOwnedCharts();
            }
            InvalidateOwnedChartCollection();
            ClearNormalLibraryRefreshNotification(mutationResult);
            throw;
        }
        return () =>
        {
            TryInvokePostLeaseNotification(
                duplicateChartGroupsPostLeaseNotification,
                "file_scan_residual_duplicate_chart_groups_notification_failed");
            TryInvokePostLeaseNotification(
                () => PublishNormalLibraryRefreshNotification(mutationResult),
                "file_scan_residual_normal_refresh_publication_failed");
            TryInvokePostLeaseNotification(
                () => RaiseNormalLibraryRefreshNotificationVersionChanged(mutationResult),
                "file_scan_residual_normal_refresh_notification_failed");
        };
    }

    /// <summary>
    /// owned collection が storage row の入力規約で除外した行を、既存の性能ログへ記録します。
    /// </summary>
    /// <param name="reason">collection を構築した理由。</param>
    /// <param name="filterSummary">構築時の除外件数。</param>
    internal void LogOwnedChartCollectionSkippedRows(string reason, OwnedChartStorageRowFilterSummary filterSummary)
    {
        if (!filterSummary.HasSkippedRows)
        {
            return;
        }
        LogInstallPerformance("owned_chart_collection_storage_rows_skipped reason=" + (reason ?? "(null)")
            + " pathlessBms=" + filterSummary.PathlessBmsCount
            + " pathlessBmson=" + filterSummary.PathlessBmsonCount
            + " md5lessBms=" + filterSummary.Md5lessBmsCount
            + " md5lessBmson=" + filterSummary.Md5lessBmsonCount
            + " duplicatePathBms=" + filterSummary.DuplicatePathBmsCount
            + " duplicatePathBmson=" + filterSummary.DuplicatePathBmsonCount);
    }

    private InstalledChartStorageTargetsApplyReceipt ApplyInstalledChartStorageTargetsForDeferredDispatch(
        ChartStorageTargetSet addedTargets,
        string lookupReason,
        LibraryFileMutationCapability mutationCapability,
        ref LibraryMutationSessionApplyCounts applyCounts,
        Action<string> logOverride = null,
        IEnumerable<string> installPathsToDelete = null,
        IEnumerable<ChartPackage> installRowsToUpsert = null)
    {
        if (addedTargets == null)
        {
            return InstalledChartStorageTargetsApplyReceipt.Empty;
        }

        OwnedChartCollectionMutationResult mutationResult = null;
        CatalogInstalledTargetUpsertReceipt installedTargetReceipt = null;
        CatalogWriteFailureFact deferredFailureFact = null;
        ResourceHealthIndexOwner.ResourceHealthInputMutation resourceHealthMutation = null;
        StorageRowsVersionSnapshot storageRowsBefore = catalogStorageRowsOwner.CaptureVersionSnapshot();
        bool catalogValidationPassed = false;
        try
        {
            resourceHealthMutation = resourceHealthOwner.BeginInputMutation();
            try
            {
                mutationResult = BuildOwnedChartCollectionUpsertMutationResult(
                    addedTargets,
                    resourceHealthMutation.BaseInputVersion,
                    resourceHealthIndexCurrentAtBase: resourceHealthMutation.BaseIndexCurrent);
                using (mutationResult.ResourceHealthIndexInvalidated
                    ? resourceHealthOwner.SuppressInvalidation()
                    : null)
                {
                    applyCounts = applyCounts with { InstalledTargetApplyCount = applyCounts.InstalledTargetApplyCount + 1 };
                    installedTargetReceipt = catalogMutationOwner.ApplyInstalledTargetUpsertWithDeferredFailurePublication(
                        addedTargets,
                        out deferredFailureFact,
                        () => catalogValidationPassed = true,
                        installPathsToDelete,
                        installRowsToUpsert);
                    mutationResult.OwnedCollectionVersion = installedTargetReceipt.OwnedCollectionVersion;
                    mutationResult.OwnedCollectionVersionAlreadyAdvanced = installedTargetReceipt.OwnedCollectionApplied;
                    mutationResult.BmsonCanonicalOrderNormalized = installedTargetReceipt.BmsonCanonicalOrderNormalized;
                }
            }
            finally
            {
                resourceHealthMutation.Dispose();
            }

            mutationResult.ResourceHealthMutation.DeltaTargetResourceHealthInputVersion ??= resourceHealthMutation.TargetInputVersion;
            if (mutationResult.ResourceHealthMutation.DeltaTargetResourceHealthInputVersion.Value < 0)
            {
                mutationResult.ResourceHealthMutation.Invalidate = true;
            }
            return new InstalledChartStorageTargetsApplyReceipt(
                mutationResult,
                installedTargetReceipt,
                deferredFailureFact,
                lookupReason,
                logOverride,
                failureFallbackRequired: false,
                failure: null);
        }
        catch (Exception exception)
        {
            StorageRowsVersionSnapshot storageRowsAfter = catalogStorageRowsOwner.CaptureVersionSnapshot();
            bool failureFallbackRequired = !catalogValidationPassed
                || storageRowsBefore.BmsRowsVersion != storageRowsAfter.BmsRowsVersion
                || storageRowsBefore.BmsonRowsVersion != storageRowsAfter.BmsonRowsVersion;
            return new InstalledChartStorageTargetsApplyReceipt(
                mutationResult,
                installedTargetReceipt,
                deferredFailureFact,
                lookupReason,
                logOverride,
                failureFallbackRequired,
                ExceptionDispatchInfo.Capture(exception));
        }
    }

    /// <summary>
    /// Completes installed-target LR2 and semantic state while the caller's
    /// original file-mutation reservation is still active.  This route never
    /// acquires a second reservation; only publication is deferred to the
    /// lease's post-commit effect queue.
    /// </summary>
    private void CompleteInstalledChartStorageTargetsUnderExistingReservation(
        InstalledChartStorageTargetsApplyReceipt receipt,
        LibraryFileMutationCapability mutationCapability,
        ref LibraryMutationSessionApplyCounts applyCounts)
    {
        if (receipt == null || ReferenceEquals(receipt, InstalledChartStorageTargetsApplyReceipt.Empty))
        {
            return;
        }
        if (receipt.Failure != null)
        {
            receipt.Failure.Throw();
        }

        try
        {
            applyCounts = applyCounts with { Lr2SyncCount = applyCounts.Lr2SyncCount + 1 };
            lr2SynchronizationOwner.SyncLr2NormalFoldersForCatalogMutation(
                CreateLr2NormalFolderCatalogMutationReceipt(
                    receipt.InstalledTargetReceipt,
                    receipt.MutationResult.OwnedCollectionVersion),
                receipt.LookupReason ?? "install_package",
                mutationCapability);
            ApplyOwnedChartCollectionSemanticLookupStateUnderGuard(
                receipt.MutationResult,
                receipt.LookupReason,
                receipt.LogOverride);
        }
        catch (Exception exception)
        {
            receipt.RecordCompletionFailure(exception);
            throw;
        }
    }

    private void ApplyOwnedChartCollectionSemanticLookupStateUnderGuard(
        OwnedChartCollectionMutationResult result,
        string reason,
        Action<string> logOverride)
    {
        if (result.InstallDestinationRuntimeStateMutation.HasStateChanges)
        {
            installDestinationStateOwner.Apply(result.InstallDestinationRuntimeStateMutation);
        }
        if (result.InstallDestinationRuntimeStateMutation.PruneToCurrentOwnedCharts)
        {
            installDestinationStateOwner.PruneToCurrentOwnedCharts();
        }
        result.InstallDestinationRuntimeStateApplied = true;
        if (result.InstallEstimationMetadataProfileCacheInvalidated || result.ShouldDispatchInstalledLookup)
        {
            InvalidateInstallEstimationMetadataProfileCache();
            result.InstallMetadataCacheInvalidated = true;
        }
        if (result.ShouldDispatchInstalledLookup)
        {
            ApplyInstalledChartLookupMutation(
                result.InstalledLookupMutation,
                reason,
                logOverride);
            result.InstalledLookupMutationApplied = true;
        }
        ApplyOwnedChartHashIndexMutation(result);
        ApplyPlaylistLibraryResolveIndexMutation(result);
    }

    private void PublishInstalledChartStorageTargetsAfterGuard(
        InstalledChartStorageTargetsApplyReceipt receipt)
    {
        if (receipt == null || ReferenceEquals(receipt, InstalledChartStorageTargetsApplyReceipt.Empty))
        {
            return;
        }
        catalogMutationOwner.PublishCatalogWriteFailureFactBestEffort(receipt.DeferredFailureFact);
        if (receipt.Failure != null)
        {
            if (receipt.FailureFallbackRequired)
            {
                try
                {
                    ApplyInstalledChartStorageTargetsFailureFallback(receipt.MutationResult);
                }
                catch (Exception exception)
                {
                    // Failure-side invalidation is diagnostic once the
                    // primary catalog/DB failure has been recorded.
                    NLogWrapper.FileLogger?.Warn(
                        exception,
                        "installed_chart_storage_target_failure_fallback_failed");
                }
            }
            // Failure-side publication must not replace the primary catalog or
            // filesystem failure already carried by the durable receipt.
            return;
        }
        TryInvokePostLeaseNotification(
            () => PublishOwnedCollectionChangeNotification(receipt.MutationResult),
            "installed_chart_storage_target_collection_notification_failed");
        TryInvokePostLeaseNotification(
            () => DispatchOwnedChartCollectionMutation(receipt.MutationResult, receipt.LookupReason),
            "installed_chart_storage_target_dispatch_failed");
    }

    private sealed class InstalledChartStorageTargetsApplyReceipt(
        OwnedChartCollectionMutationResult mutationResult,
        CatalogInstalledTargetUpsertReceipt installedTargetReceipt,
        CatalogWriteFailureFact deferredFailureFact,
        string lookupReason,
        Action<string> logOverride,
        bool failureFallbackRequired,
        ExceptionDispatchInfo failure)
    {
        internal static InstalledChartStorageTargetsApplyReceipt Empty { get; } = new(
            null,
            null,
            null,
            null,
            null,
            failureFallbackRequired: false,
            failure: null);

        internal OwnedChartCollectionMutationResult MutationResult { get; } = mutationResult;

        internal CatalogInstalledTargetUpsertReceipt InstalledTargetReceipt { get; } = installedTargetReceipt;

        internal CatalogWriteFailureFact DeferredFailureFact { get; } = deferredFailureFact;

        internal string LookupReason { get; } = lookupReason;

        internal Action<string> LogOverride { get; } = logOverride;

        internal bool FailureFallbackRequired { get; private set; } = failureFallbackRequired;

        internal ExceptionDispatchInfo Failure { get; private set; } = failure;

        internal void RecordCompletionFailure(Exception exception)
        {
            FailureFallbackRequired = true;
            Failure ??= ExceptionDispatchInfo.Capture(exception);
        }
    }

    private void ApplyInstalledChartStorageTargetsFailureFallback(OwnedChartCollectionMutationResult mutationResult)
    {
        if (mutationResult?.ShouldDispatchInstalledLookup != false)
        {
            InvalidateInstalledDirectoryIndex();
        }
        if (HasOwnedHashSetChanges(mutationResult))
        {
            catalogOwnedCollectionOwner.InvalidateHashIndexSnapshot();
        }
        if (mutationResult?.OwnedCollectionChanged == true)
        {
            catalogOwnedCollectionOwner.InvalidatePlaylistLibraryResolveIndexSnapshot();
        }
        if (mutationResult?.ParentFolderInvalidated == true)
        {
            InvalidateParentFolderListCacheAndNotify();
        }
        if (mutationResult?.DuplicateCacheInvalidated == true)
        {
            InvalidateDuplicateChartGroupsCache();
        }
        if (mutationResult?.InstallDestinationRuntimeStateMutation.HasChanges == true
            || mutationResult?.InstallDestinationRuntimeStateMutation.PruneToCurrentOwnedCharts == true)
        {
            installDestinationStateOwner.PruneToCurrentOwnedCharts();
        }
        if (mutationResult?.OwnedCollectionChanged == true)
        {
            PublishOwnedCollectionChangeNotification(mutationResult);
        }
        if (mutationResult?.ResourceHealthMutation.HasChanges == true)
        {
            resourceHealthOwner.ForceInvalidate("install_package_failed");
        }
        InvalidateOwnedChartCollection();
        if (mutationResult != null)
        {
            ClearNormalLibraryRefreshNotification(mutationResult);
        }
    }

    private OwnedChartCollectionMutationResult CreateFileScanMutationProjection(
        CatalogFileScanStorageReplacementRequest request,
        bool? resourceHealthIndexCurrentAtBase = null)
    {
        var storageMutation = new OwnedChartCollectionStorageMutation();
        if (request.RemovedPayloadAvailable)
        {
            storageMutation.RemoveRequests.AddRange((request.RemovedCharts ?? [])
                .Select(OwnedChartRemoveRequest.FromOwnerReferenceChart)
                .Where(request => request != null));
        }
        storageMutation.AddAddedTargets(
            ChartStorageTargetSet.FromRows(request.AddedBmsFiles, request.AddedBmsonSongs));
        bool bmsRowsChanged = request.DeletedBmsPaths.Count > 0 || request.AddedBmsFiles.Count > 0;
        bool bmsonRowsChanged = request.DeletedBmsonPaths.Count > 0 || request.AddedBmsonSongs.Count > 0;
        bool storageRowsChanged = bmsRowsChanged || bmsonRowsChanged || request.HasDbDiff;
        bool resourceHealthShouldInvalidate = request.HasDbDiff || (resourceHealthIndexCurrentAtBase ?? resourceHealthOwner.IsCurrent());
        bool fileScanPresentationChanged = request.HasDbDiff || resourceHealthShouldInvalidate;

        var result = new OwnedChartCollectionMutationResult
        {
            InstalledLookupMutation = BuildInstalledChartLookupFileScanMutation(storageMutation, request.RemovedPayloadAvailable, request.HasDbDiff),
            InstallEstimationMetadataProfileCacheInvalidated = request.HasDbDiff,
            AddedCount = storageMutation.AddedCount,
            RemovedCount = request.RemovedPayloadAvailable
                ? storageMutation.RemovedCount
                : request.DeletedBmsPaths.Count + request.DeletedBmsonPaths.Count,
            MovedCount = storageMutation.MovedCount,
            ParentFolderInvalidated = request.HasDbDiff,
            DuplicateCacheInvalidated = request.HasDbDiff,
            OwnedCollectionChanged = storageRowsChanged,
            PlaylistResolveIndexInvalidated = request.HasDbDiff,
            ResourceHealthIndexInvalidated = resourceHealthShouldInvalidate,
            WarningPresentationChanged = fileScanPresentationChanged,
            MaintenancePresentationChanged = fileScanPresentationChanged,
            BmsFilesStorageRowsChanged = bmsRowsChanged,
            BmsonSongsStorageRowsChanged = bmsonRowsChanged
        };
        result.StorageMutation.AddedBmsFiles.AddRange(storageMutation.AddedBmsFiles);
        result.StorageMutation.AddedBmsonSongs.AddRange(storageMutation.AddedBmsonSongs);
        result.StorageMutation.AddedCharts.AddRange(storageMutation.AddedCharts);
        result.StorageMutation.RemoveRequests.AddRange(storageMutation.RemoveRequests);
        result.InstallDestinationRuntimeStateMutation.PruneToCurrentOwnedCharts = storageRowsChanged;
        return result;
    }

    private InstalledChartLookupMutation BuildInstalledChartLookupFileScanMutation(
        OwnedChartCollectionStorageMutation storageMutation,
        bool removedPayloadAvailable,
        bool hasDbDiff)
    {
        InstalledChartLookupMutation mutation = BuildInstalledChartLookupMutation(storageMutation, null);
        if (!removedPayloadAvailable && hasDbDiff)
        {
            mutation.RequiresFullInvalidate = true;
            return mutation;
        }
        foreach (ChartFile chart in storageMutation?.AddedCharts ?? [])
        {
            mutation.Added.Add(CreateInstalledChartLookupMutationEntry(chart));
        }
        return mutation;
    }

    private StorageRowsVersionSnapshot CaptureStorageRowsVersionUnsafe()
    {
        lock (catalogStorageRowsOwner.VersionGate)
        {
            return CreateCurrentStorageRowsVersionSnapshotUnsafe();
        }
    }

    internal static List<BMSFile> NormalizeBmsStorageRows(IEnumerable<BMSFile> files)
    {
        return files == null ? [] : [.. files];
    }

    internal static List<LR2SongDBExtended.bmson_song> NormalizeBmsonStorageRows(
        IEnumerable<LR2SongDBExtended.bmson_song> songs)
    {
        return songs == null ? [] : [.. songs];
    }

    private StorageRowsVersionSnapshot CreateCurrentStorageRowsVersionSnapshotUnsafe()
    {
        return catalogStorageRowsOwner.CaptureVersionSnapshot();
    }

    private StorageRowsVersionSnapshot CreateStorageRowsVersionSnapshotUnsafe(int previousBmsRowsVersion, int previousBmsonRowsVersion)
    {
        return new StorageRowsVersionSnapshot(
            previousBmsRowsVersion,
            previousBmsonRowsVersion,
            catalogStorageRowsOwner.BmsRowsVersion,
            catalogStorageRowsOwner.BmsonRowsVersion);
    }

    private OwnedChartCollectionMutationResult BuildOwnedChartCollectionMutationResult(
        LibraryCatalogMutationFacts catalogFacts,
        LibraryPackageReferenceFacts packageReferenceFacts,
        LibraryStorageRowPathNotificationPolicy storageRowPathNotificationPolicy = LibraryStorageRowPathNotificationPolicy.Notify,
        int? deltaBaseResourceHealthInputVersion = null,
        int? deltaTargetResourceHealthInputVersion = null,
        bool? resourceHealthIndexCurrentAtBase = null)
    {
        OwnedChartCollectionStorageMutation storageMutation = BuildOwnedChartCollectionStorageMutation(catalogFacts);
        var result = CreateOwnedChartCollectionMutationResult(
            storageMutation,
            BuildInstalledChartLookupMutation(storageMutation, catalogFacts?.FolderPathChanges),
            deltaBaseResourceHealthInputVersion,
            deltaTargetResourceHealthInputVersion,
            resourceHealthIndexCurrentAtBase);
        ApplyNormalMutationFacts(result, catalogFacts, packageReferenceFacts, storageRowPathNotificationPolicy);
        result.InstallDestinationRuntimeStateMutation.PathChanges.AddRange(storageMutation.PathChanges);
        result.InstallDestinationRuntimeStateMutation.AppliedCharts.AddRange(
            installDestinationStateOwner.CreateChangedChartSnapshots(
                packageReferenceFacts?.InstallDestinationChanges,
                storageMutation.PathChanges));
        return result;
    }

    /// <summary>
    /// 通常のcatalog factsにだけ含まれるfolder、install overlay、package pathの事実を、
    /// storage mutationから導出した共通結果へ補足します。
    /// </summary>
    private static void ApplyNormalMutationFacts(
        OwnedChartCollectionMutationResult result,
        LibraryCatalogMutationFacts catalogFacts,
        LibraryPackageReferenceFacts packageReferenceFacts,
        LibraryStorageRowPathNotificationPolicy storageRowPathNotificationPolicy)
    {
        if (result == null)
        {
            return;
        }

        bool hasInstallDestinationChanges = packageReferenceFacts?.InstallDestinationChanges.Count > 0;
        bool hasFolderPathChanges = catalogFacts?.FolderPathChanges.Count > 0;
        bool hasInstalledPackagePathChanges = packageReferenceFacts?.InstalledPackagePathChanges.Count > 0;
        result.InstallEstimationMetadataProfileCacheInvalidated |= hasInstallDestinationChanges
            || hasInstalledPackagePathChanges;
        result.InstallDestinationChangedCount = packageReferenceFacts?.InstallDestinationChanges.Count ?? 0;
        result.InstalledPackagePathChangedCount = packageReferenceFacts?.InstalledPackagePathChanges.Count ?? 0;
        result.ParentFolderInvalidated |= hasFolderPathChanges;
        result.DuplicateCacheInvalidated |= hasInstallDestinationChanges
            || hasInstalledPackagePathChanges;
        result.WarningPresentationChanged |= hasInstallDestinationChanges
            || hasInstalledPackagePathChanges;
        if (storageRowPathNotificationPolicy == LibraryStorageRowPathNotificationPolicy.Notify)
        {
            result.BmsFilesStorageRowsChanged |= HasBmsStorageRowPathChange(result.StorageMutation);
            result.BmsonSongsStorageRowsChanged |= HasBmsonStorageRowPathChange(result.StorageMutation);
        }
    }

    private OwnedChartCollectionStorageMutation BuildOwnedChartCollectionStorageMutation(LibraryCatalogMutationFacts catalogFacts)
    {
        var mutation = new OwnedChartCollectionStorageMutation();
        if (catalogFacts == null)
        {
            return mutation;
        }

        mutation.RemoveRequests.AddRange(catalogFacts.ChartRemoveRequests);
        ResolveCurrentOwnedRemoveRequests(mutation.RemoveRequests);
        mutation.PathChanges.AddRange(catalogFacts.ChartPathChanges.Where(change => change?.Chart != null));
        return mutation;
    }

    private void ResolveCurrentOwnedRemoveRequests(List<OwnedChartRemoveRequest> removeRequests)
    {
        if (removeRequests == null || removeRequests.Count == 0)
        {
            return;
        }

        List<OwnedChartRemoveRequest> resolvedRequests;
        lock (catalogStorageRowsOwner.VersionGate)
        {
            int bmsRowsVersion = catalogStorageRowsOwner.BmsRowsVersion;
            int bmsonRowsVersion = catalogStorageRowsOwner.BmsonRowsVersion;
            lock (catalogOwnedCollectionOwner.Gate)
            {
                if (!catalogOwnedCollectionOwner.IsCurrent(bmsRowsVersion, bmsonRowsVersion))
                {
                    resolvedRequests = [.. removeRequests
                        .Where(request => request?.Mode == OwnedChartRemoveMode.OwnerReference
                            && (request.BmsOwner != null || request.BmsonOwner != null))];
                }
                else
                {
                    resolvedRequests = catalogOwnedCollectionOwner.Collection.ResolveCurrentRemoveRequests(removeRequests);
                }
            }
        }

        removeRequests.Clear();
        removeRequests.AddRange(resolvedRequests);
    }

    private static string CreateKindPathRemoveKey(ChartFileKind kind, string path)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            return null;
        }
        return kind + ":" + path;
    }

    private static bool HasBmsStorageRowCollectionChange(OwnedChartCollectionStorageMutation mutation)
    {
        return mutation != null
            && (mutation.AddedBmsFiles.Count > 0
                || mutation.RemoveRequests.Any(request => request?.BmsOwner != null
                    || (request?.Mode == OwnedChartRemoveMode.PathCleanup && request.Kind == ChartFileKind.Bms)));
    }

    private static bool HasBmsonStorageRowCollectionChange(OwnedChartCollectionStorageMutation mutation)
    {
        return mutation != null
            && (mutation.AddedBmsonSongs.Count > 0
                || mutation.RemoveRequests.Any(request => request?.BmsonOwner != null
                    || (request?.Mode == OwnedChartRemoveMode.PathCleanup && request.Kind == ChartFileKind.Bmson)));
    }

    private static bool HasBmsStorageRowPathChange(OwnedChartCollectionStorageMutation mutation)
    {
        return mutation?.PathChanges.Any(change => change?.GetBmsStorageOwner() != null) == true;
    }

    private static bool HasBmsonStorageRowPathChange(OwnedChartCollectionStorageMutation mutation)
    {
        return mutation?.PathChanges.Any(change => change?.GetBmsonStorageOwner() != null) == true;
    }

    private OwnedChartCollectionMutationResult BuildOwnedChartCollectionUpsertMutationResult(
        ChartStorageTargetSet addedTargets,
        int? deltaBaseResourceHealthInputVersion = null,
        int? deltaTargetResourceHealthInputVersion = null,
        bool? resourceHealthIndexCurrentAtBase = null)
    {
        var storageMutation = new OwnedChartCollectionStorageMutation();
        storageMutation.AddAddedTargets(addedTargets);
        var result = CreateOwnedChartCollectionMutationResult(
            storageMutation,
            BuildInstalledChartLookupUpsertMutation(storageMutation),
            deltaBaseResourceHealthInputVersion,
            deltaTargetResourceHealthInputVersion,
            resourceHealthIndexCurrentAtBase);
        // upsertは既存exact行のreplacementを含むため、通常deltaの除去起点とは
        // 異なり、追加対象を反映したinstall destination stateだけをpruneします。
        result.InstallDestinationRuntimeStateMutation.PruneToCurrentOwnedCharts = storageMutation.AddedCount > 0;
        return result;
    }

    private OwnedChartCollectionMutationResult CreateOwnedChartCollectionMutationResult(
        OwnedChartCollectionStorageMutation storageMutation,
        InstalledChartLookupMutation installedLookupMutation,
        int? deltaBaseResourceHealthInputVersion = null,
        int? deltaTargetResourceHealthInputVersion = null,
        bool? resourceHealthIndexCurrentAtBase = null)
    {
        storageMutation ??= new OwnedChartCollectionStorageMutation();
        installedLookupMutation ??= new InstalledChartLookupMutation();
        bool hasStorageMutation = storageMutation.HasChanges;
        var result = new OwnedChartCollectionMutationResult
        {
            InstalledLookupMutation = installedLookupMutation,
            InstallEstimationMetadataProfileCacheInvalidated = hasStorageMutation
                || installedLookupMutation.HasChanges,
            AddedCount = storageMutation.AddedCount,
            RemovedCount = storageMutation.RemovedCount,
            MovedCount = storageMutation.MovedCount,
            ParentFolderInvalidated = hasStorageMutation,
            DuplicateCacheInvalidated = hasStorageMutation,
            OwnedCollectionChanged = hasStorageMutation,
            WarningPresentationChanged = hasStorageMutation,
            BmsFilesStorageRowsChanged = HasBmsStorageRowCollectionChange(storageMutation),
            BmsonSongsStorageRowsChanged = HasBmsonStorageRowCollectionChange(storageMutation),
            StorageRowsRemoveDeltaComplete = storageMutation.RemovedCount > 0
                && storageMutation.AddedCount == 0
                && storageMutation.MovedCount == 0
        };
        result.StorageMutation.AddedBmsFiles.AddRange(storageMutation.AddedBmsFiles);
        result.StorageMutation.AddedBmsonSongs.AddRange(storageMutation.AddedBmsonSongs);
        result.StorageMutation.AddedCharts.AddRange(storageMutation.AddedCharts);
        result.StorageMutation.RemoveRequests.AddRange(storageMutation.RemoveRequests);
        result.StorageMutation.PathChanges.AddRange(storageMutation.PathChanges);
        result.InstallDestinationRuntimeStateMutation.PruneToCurrentOwnedCharts = storageMutation.RemovedCount > 0;
        ConfigureResourceHealthMutationForStorageMutation(
            result,
            storageMutation,
            deltaBaseResourceHealthInputVersion,
            deltaTargetResourceHealthInputVersion,
            resourceHealthIndexCurrentAtBase);
        return result;
    }

    private void ConfigureResourceHealthMutationForStorageMutation(
        OwnedChartCollectionMutationResult result,
        OwnedChartCollectionStorageMutation storageMutation,
        int? deltaBaseResourceHealthInputVersion = null,
        int? deltaTargetResourceHealthInputVersion = null,
        bool? resourceHealthIndexCurrentAtBase = null)
    {
        if (result == null || storageMutation?.HasChanges != true)
        {
            return;
        }
        bool resourceHealthIndexCurrent = resourceHealthIndexCurrentAtBase ?? resourceHealthOwner.IsCurrent();
        if (!resourceHealthIndexCurrent || storageMutation.PathChanges.Count > 0)
        {
            result.ResourceHealthIndexInvalidated = true;
            return;
        }

        if (storageMutation.RemoveRequests.Any(request => request?.Mode == OwnedChartRemoveMode.PathCleanup))
        {
            result.ResourceHealthIndexInvalidated = true;
            return;
        }

        result.ResourceHealthMutation.RemovedTargets.AddRange(CreateRemovedChartSnapshots(storageMutation));
        result.ResourceHealthMutation.UpdatedTargets.AddRange(storageMutation.AddedCharts.Where(chart => chart != null));
        result.ResourceHealthMutation.DeltaBaseResourceHealthInputVersion = deltaBaseResourceHealthInputVersion ?? resourceHealthOwner.CurrentInputVersion;
        result.ResourceHealthMutation.DeltaTargetResourceHealthInputVersion = deltaTargetResourceHealthInputVersion;
        result.ResourceHealthMutation.InvalidateIfDeltaFails = true;
    }

    private static List<ChartFile> CreateRemovedChartSnapshots(OwnedChartCollectionStorageMutation storageMutation)
    {
        var charts = new List<ChartFile>();
        if (storageMutation == null)
        {
            return charts;
        }
        var bmsOwners = new HashSet<BMSFile>();
        var bmsonOwners = new HashSet<LR2SongDBExtended.bmson_song>();
        var pathKeys = new HashSet<string>(StringComparer.Ordinal);
        foreach (ChartFile chart in storageMutation.RemoveRequests
            .Select(request => request?.CreateChartSnapshot())
            .Where(chart => chart != null))
        {
            if (TryAddRemovedChartIdentity(chart, bmsOwners, bmsonOwners, pathKeys))
            {
                charts.Add(chart);
            }
        }
        return charts;
    }

    private static bool TryAddRemovedChartIdentity(
        ChartFile chart,
        ISet<BMSFile> bmsOwners,
        ISet<LR2SongDBExtended.bmson_song> bmsonOwners,
        ISet<string> pathKeys)
    {
        if (chart == null)
        {
            return false;
        }
        BMSFile bmsOwner = chart.GetBmsStorageOwner();
        if (bmsOwner != null)
        {
            return bmsOwners.Add(bmsOwner);
        }
        LR2SongDBExtended.bmson_song bmsonOwner = chart.GetBmsonStorageOwner();
        if (bmsonOwner != null)
        {
            return bmsonOwners.Add(bmsonOwner);
        }
        string pathKey = CreateKindPathRemoveKey(chart.Kind, chart.Path);
        return !string.IsNullOrWhiteSpace(pathKey) && pathKeys.Add(pathKey);
    }

    private OwnedChartCollectionMutationResult CreateOwnedChartCollectionDigestMutationResult(
        IEnumerable<LibraryChartDigestChange> digestChanges,
        bool resourceHealthIndexInvalidated = true)
    {
        List<LibraryChartDigestChange> changes = [.. (digestChanges ?? [])
            .Where(change => change?.HasDigestChange == true)];
        bool anyChanges = changes.Count > 0;
        bool primaryHashChanged = changes.Any(change => change.PrimaryHashChanged);
        bool md5Changed = changes.Any(change => change.Md5Changed);
        var result = new OwnedChartCollectionMutationResult
        {
            InstalledLookupMutation = BuildInstalledChartLookupDigestMutation(changes),
            InstallEstimationMetadataProfileCacheInvalidated = anyChanges,
            DuplicateCacheInvalidated = primaryHashChanged,
            OwnedCollectionChanged = anyChanges,
            ResourceHealthIndexInvalidated = resourceHealthIndexInvalidated && md5Changed,
            WarningPresentationChanged = primaryHashChanged || (resourceHealthIndexInvalidated && md5Changed),
            BmsFilesStorageRowsChanged = changes.Any(change => change.Kind == LibraryChartKind.Bms),
            BmsonSongsStorageRowsChanged = changes.Any(change => change.Kind == LibraryChartKind.Bmson)
        };
        result.DigestChanges.AddRange(changes);
        return result;
    }

    private OwnedChartCollectionMutationResult BuildOwnedChartCollectionMaintenanceMutationResult(
        ResourceHealthIndexMutation resourceHealthMutation,
        bool workflowHasUpdates)
    {
        var result = new OwnedChartCollectionMutationResult();
        CopyResourceHealthIndexMutation(resourceHealthMutation, result.ResourceHealthMutation);
        bool resourceHealthChanged = result.ResourceHealthMutation.HasChanges;
        result.WarningPresentationChanged |= resourceHealthChanged;
        result.MaintenancePresentationChanged = workflowHasUpdates || resourceHealthChanged;
        return result;
    }

    private OwnedChartCollectionMutationResult ApplyCatalogMaintenanceMutation(
        CatalogMaintenanceOperationReceipt receipt,
        out MaintenanceWorkflowResult workflowResult,
        out ResourceHealthIndexMutation resourceHealthMutation)
    {
        workflowResult = receipt?.WorkflowResult?.ToMutable() ?? new MaintenanceWorkflowResult();
        resourceHealthMutation = receipt?.ResourceHealthMutation?.ToMutation()
            ?? new ResourceHealthIndexMutation();
        OwnedChartCollectionMutationResult mutationResult = BuildOwnedChartCollectionMaintenanceMutationResult(
            resourceHealthMutation,
            workflowResult.HasUpdates);
        DispatchOwnedChartCollectionMutation(
            mutationResult,
            receipt?.Reason ?? "maintenance",
            publishNormalRefreshNotification: false,
            publishOwnedCollectionNotifications: false);
        return mutationResult;
    }

    private static void CopyResourceHealthIndexMutation(
        ResourceHealthIndexMutation source,
        ResourceHealthIndexMutation destination)
    {
        if (source == null || destination == null)
        {
            return;
        }
        destination.UpdatedTargets.AddRange(source.UpdatedTargets);
        destination.RemovedTargets.AddRange(source.RemovedTargets);
        destination.FullOwnedTargetSet = source.FullOwnedTargetSet;
        destination.DeltaBaseResourceHealthInputVersion = source.DeltaBaseResourceHealthInputVersion;
        destination.DeltaTargetResourceHealthInputVersion = source.DeltaTargetResourceHealthInputVersion;
        destination.Invalidate = source.Invalidate;
        destination.RebuildFull = source.RebuildFull;
        destination.Defer = source.Defer;
        destination.InvalidateIfDeltaFails = source.InvalidateIfDeltaFails;
    }

    private Action DispatchOwnedChartCollectionMutation(
        OwnedChartCollectionMutationResult result,
        string reason,
        bool publishNormalRefreshNotification = true,
        bool publishOwnedCollectionNotifications = true,
        bool deferDuplicateChartGroupsNotification = false)
    {
        if (result == null)
        {
            return null;
        }

        var stopwatch = Stopwatch.StartNew();
        bool collectDispatchDetails = result.HasLoggableChanges;
        long installDestinationMs = 0;
        long digestMs = 0;
        long parentFolderMs = 0;
        long duplicateMs = 0;
        long playlistSummaryMs = 0;
        long ownedCollectionNotifyMs = 0;
        long resourceHealthMs = 0;
        long installMetadataMs = 0;
        long installedLookupMs = 0;
        long normalRefreshMs = 0;
        bool duplicateChartGroupsInvalidated = false;
        if (result.InstallDestinationRuntimeStateMutation.HasStateChanges
            && !result.InstallDestinationRuntimeStateApplied)
        {
            Stopwatch stepStopwatch = StartPerformanceStepStopwatch(collectDispatchDetails);
            installDestinationStateOwner.Apply(result.InstallDestinationRuntimeStateMutation);
            installDestinationMs += StopPerformanceStepStopwatch(stepStopwatch);
        }
        if (result.InstallDestinationRuntimeStateMutation.PruneToCurrentOwnedCharts
            && !result.InstallDestinationRuntimeStateApplied)
        {
            Stopwatch stepStopwatch = StartPerformanceStepStopwatch(collectDispatchDetails);
            installDestinationStateOwner.PruneToCurrentOwnedCharts();
            installDestinationMs += StopPerformanceStepStopwatch(stepStopwatch);
        }
        if (result.DigestChangedCount > 0 && !result.DigestMutationApplied)
        {
            Stopwatch stepStopwatch = StartPerformanceStepStopwatch(collectDispatchDetails);
            if (result.DigestMutationRequest != null)
            {
                CatalogDigestMutationReceipt digestReceipt = catalogMutationOwner.ApplyDigestMutation(
                    result.DigestMutationRequest);
                result.OwnedHashIndexMutationApplied = digestReceipt.Applied;
            }
            result.DigestMutationApplied = true;
            digestMs += StopPerformanceStepStopwatch(stepStopwatch);
        }
        {
            Stopwatch stepStopwatch = StartPerformanceStepStopwatch(collectDispatchDetails);
            ApplyOwnedChartHashIndexMutation(result);
            ApplyPlaylistLibraryResolveIndexMutation(result);
            playlistSummaryMs += StopPerformanceStepStopwatch(stepStopwatch);
        }
        if (result.ParentFolderInvalidated)
        {
            Stopwatch stepStopwatch = StartPerformanceStepStopwatch(collectDispatchDetails);
            if (publishOwnedCollectionNotifications)
            {
                InvalidateParentFolderListCacheAndNotify();
            }
            else
            {
                InvalidateParentFolderListCache();
            }
            parentFolderMs += StopPerformanceStepStopwatch(stepStopwatch);
        }
        if (result.DuplicateCacheInvalidated)
        {
            Stopwatch stepStopwatch = StartPerformanceStepStopwatch(collectDispatchDetails);
            duplicateChartGroupsInvalidated = InvalidateDuplicateChartGroupsCache(
                publishNotification: !deferDuplicateChartGroupsNotification);
            duplicateMs += StopPerformanceStepStopwatch(stepStopwatch);
        }
        if (result.OwnedCollectionChanged && publishOwnedCollectionNotifications)
        {
            Stopwatch stepStopwatch = StartPerformanceStepStopwatch(collectDispatchDetails);
            PublishOwnedCollectionChangeNotification(result);
            ownedCollectionNotifyMs += StopPerformanceStepStopwatch(stepStopwatch);
        }
        {
            Stopwatch stepStopwatch = StartPerformanceStepStopwatch(collectDispatchDetails);
            if (result.ResourceHealthMutation.RebuildFull && result.OwnedCollectionChanged)
            {
                // collection 更新前の入力は再利用せず、実際に full rebuild する場合だけ取得し直す。
                result.ResourceHealthMutation.FullOwnedTargetSet = default;
            }
            result.ResourceHealthDispatchResult = DispatchResourceHealthIndexMutation(result.ResourceHealthMutation, reason);
            resourceHealthMs += StopPerformanceStepStopwatch(stepStopwatch);
        }
        if (!result.ResourceHealthMutation.HasChanges && result.OwnedCollectionChanged)
        {
            resourceHealthOwner.RebaseCurrentVersion(GetCurrentResourceHealthIndexVersion());
        }
        bool installMetadataProfileCacheInvalidated = result.InstallEstimationMetadataProfileCacheInvalidated || result.ShouldDispatchInstalledLookup;
        if (installMetadataProfileCacheInvalidated && !result.InstallMetadataCacheInvalidated)
        {
            Stopwatch stepStopwatch = StartPerformanceStepStopwatch(collectDispatchDetails);
            InvalidateInstallEstimationMetadataProfileCache();
            installMetadataMs += StopPerformanceStepStopwatch(stepStopwatch);
        }
        if (result.ShouldDispatchInstalledLookup && !result.InstalledLookupMutationApplied)
        {
            Stopwatch stepStopwatch = StartPerformanceStepStopwatch(collectDispatchDetails);
            ApplyInstalledChartLookupMutation(result.InstalledLookupMutation, reason);
            installedLookupMs += StopPerformanceStepStopwatch(stepStopwatch);
        }
        if (publishNormalRefreshNotification)
        {
            Stopwatch stepStopwatch = StartPerformanceStepStopwatch(collectDispatchDetails);
            PublishNormalLibraryRefreshNotification(result);
            RaiseNormalLibraryRefreshNotificationVersionChanged(result);
            normalRefreshMs += StopPerformanceStepStopwatch(stepStopwatch);
        }
        stopwatch.Stop();

        if (result.HasLoggableChanges)
        {
            LogInstallPerformance("owned_collection_mutation_dispatch reason=" + (reason ?? "unknown")
                + " added=" + result.AddedCount
                + " removed=" + result.RemovedCount
                + " moved=" + result.MovedCount
                + " digestChanged=" + result.DigestChangedCount
                + " installDestinations=" + result.InstallDestinationChangedCount
                + " installedPackagePaths=" + result.InstalledPackagePathChangedCount
                + " installedLookup=" + ToMutationDispatchLogValue(result.InstalledLookupMutation)
                + " parentFolder=" + ToInvalidateLogValue(result.ParentFolderInvalidated)
                + " duplicate=" + ToInvalidateLogValue(result.DuplicateCacheInvalidated)
                + " catalogHashSet=" + ToInvalidateLogValue(HasOwnedHashSetChanges(result))
                + " playlistResolve=" + ToInvalidateLogValue(result.PlaylistResolveIndexInvalidated)
                + " ownedCollection=" + ToInvalidateLogValue(result.OwnedCollectionChanged)
                + " resourceHealth=" + ToResourceHealthMutationDispatchLogValue(result.ResourceHealthMutation)
                + " installMetadata=" + ToInvalidateLogValue(installMetadataProfileCacheInvalidated)
                + " warningPresentation=" + ToInvalidateLogValue(result.WarningPresentationChanged)
                + " maintenancePresentation=" + ToInvalidateLogValue(result.MaintenancePresentationChanged)
                + " bmsStorageRows=" + ToInvalidateLogValue(result.BmsFilesStorageRowsChanged)
                + " bmsonStorageRows=" + ToInvalidateLogValue(result.BmsonSongsStorageRowsChanged)
                + " installDestinationMs=" + installDestinationMs
                + " digestMs=" + digestMs
                + " parentFolderMs=" + parentFolderMs
                + " duplicateMs=" + duplicateMs
                + " playlistSummaryMs=" + playlistSummaryMs
                + " ownedCollectionNotifyMs=" + ownedCollectionNotifyMs
                + " resourceHealthMs=" + resourceHealthMs
                + " installMetadataMs=" + installMetadataMs
                + " installedLookupMs=" + installedLookupMs
                + " normalRefreshMs=" + normalRefreshMs
                + " elapsedMs=" + stopwatch.ElapsedMilliseconds);
        }
        return deferDuplicateChartGroupsNotification && duplicateChartGroupsInvalidated
            ? raiseDuplicateChartGroupsChanged
            : null;
    }

    private static Stopwatch StartPerformanceStepStopwatch(bool enabled)
    {
        return enabled ? Stopwatch.StartNew() : null;
    }

    private static long StopPerformanceStepStopwatch(Stopwatch stopwatch)
    {
        if (stopwatch == null)
        {
            return 0;
        }
        stopwatch.Stop();
        return stopwatch.ElapsedMilliseconds;
    }

    /// <summary>
    /// durable 済み digest facts を semantic index へ反映し、digest window 解放後に実行する公開 action を返します。
    /// </summary>
    /// <param name="digestChanges">catalog writer が確定した digest 変更。</param>
    /// <param name="reason">反映・診断理由。</param>
    /// <returns>呼び出し側が digest window 解放後に実行する公開 action。</returns>
    internal Action PrepareOwnedChartDigestPublication(
        IReadOnlyList<LibraryChartDigestChange> digestChanges,
        string reason)
    {
        OwnedChartCollectionMutationResult mutationResult = CreateOwnedChartCollectionDigestMutationResult(
            digestChanges);
        mutationResult.OwnedCollectionVersion = catalogOwnedCollectionOwner.CollectionVersion;
        // CatalogMutationOwner.ApplyDigestMutation が先に正本へ適用している。
        mutationResult.DigestMutationApplied = true;
        mutationResult.OwnedHashIndexMutationApplied = true;
        ApplyOwnedChartCollectionSemanticLookupStateUnderGuard(
            mutationResult,
            reason,
            LogInstallPerformance);
        return () => DispatchOwnedChartCollectionMutationWithResourceHealthLease(mutationResult, reason);
    }

    private void DispatchOwnedChartCollectionMutationWithResourceHealthLease(
        OwnedChartCollectionMutationResult mutationResult,
        string reason)
    {
        if (IsOwnedDigestMutationWindowActive())
        {
            try
            {
                DispatchOwnedChartCollectionMutation(mutationResult, reason);
            }
            catch
            {
                resourceHealthOwner.ForceInvalidate((reason ?? "owned_chart_mutation") + "_failed");
                throw;
            }
            return;
        }

        if (mutationResult?.ResourceHealthMutation?.HasChanges != true)
        {
            DispatchOwnedChartCollectionMutation(mutationResult, reason);
            return;
        }

        ResourceHealthIndexOwner.ResourceHealthInputMutation resourceHealthMutation = resourceHealthOwner.BeginInputMutation();
        try
        {
            DispatchOwnedChartCollectionMutation(mutationResult, reason);
        }
        catch
        {
            resourceHealthOwner.ForceInvalidate((reason ?? "owned_chart_mutation") + "_failed");
            throw;
        }
        finally
        {
            resourceHealthMutation.Dispose();
        }
    }

    private void PublishOwnedCollectionChangeNotification(OwnedChartCollectionMutationResult result)
    {
        if (result == null)
        {
            return;
        }
        if (!result.OwnedCollectionChanged)
        {
            result.OwnedCollectionVersion = catalogOwnedCollectionOwner.CollectionVersion;
            return;
        }
        if (result.OwnedCollectionChangeNotified)
        {
            return;
        }
        bool rebasePlaylistResolveIndex = result.PlaylistResolveIndexMutationApplied
            && !result.PlaylistResolveIndexInvalidated;
        result.OwnedCollectionVersion = result.OwnedCollectionVersionAlreadyAdvanced
            ? NotifyOwnedChartCollectionChanged(
                result.OwnedCollectionVersion,
                rebasePlaylistResolveIndex)
            : NotifyOwnedChartCollectionChanged(
                rebasePlaylistResolveIndex: rebasePlaylistResolveIndex);
        result.OwnedCollectionChangeNotified = true;
    }

    private static string ToMutationDispatchLogValue(InstalledChartLookupMutation mutation)
    {
        if (mutation == null || !mutation.HasChanges)
        {
            return "none";
        }
        return mutation.RequiresFullInvalidate ? "invalidate" : "delta";
    }

    private static string ToResourceHealthMutationDispatchLogValue(ResourceHealthIndexMutation mutation)
    {
        if (mutation == null || !mutation.HasChanges)
        {
            return "none";
        }
        if (mutation.Invalidate)
        {
            return "invalidate";
        }
        if (mutation.Defer)
        {
            return "defer";
        }
        if (mutation.RebuildFull)
        {
            return "full";
        }
        return mutation.HasDeltaTargets ? "delta" : "none";
    }

    private static string ToInvalidateLogValue(bool invalidated)
    {
        return invalidated ? "invalidate" : "none";
    }

    private InstalledChartLookupMutation BuildInstalledChartLookupMutation(
        OwnedChartCollectionStorageMutation storageMutation,
        IReadOnlyCollection<LibraryFolderPathChange> folderPathChanges)
    {
        var mutation = new InstalledChartLookupMutation();
        if (storageMutation == null)
        {
            return mutation;
        }
        foreach (ChartFile chart in CreateRemovedChartSnapshots(storageMutation))
        {
            if (string.IsNullOrWhiteSpace(chart.Md5))
            {
                mutation.RequiresFullInvalidate = true;
                continue;
            }
            mutation.Removed.Add(CreateInstalledChartLookupMutationEntry(chart));
        }
        foreach (LibraryChartPathChange pathChange in storageMutation.PathChanges)
        {
            ChartFile chart = pathChange.Chart;
            string oldPath = string.IsNullOrWhiteSpace(pathChange.OldPath) ? chart.Path : pathChange.OldPath;
            string newPath = pathChange.NewPath;
            if (string.IsNullOrWhiteSpace(oldPath) || string.IsNullOrWhiteSpace(newPath))
            {
                mutation.RequiresFullInvalidate = true;
                continue;
            }
            mutation.Moved.Add(new InstalledChartLookupPathMutationEntry(
                oldPath,
                newPath,
                chart.Md5,
                chart.Sha256,
                chart.Kind));
        }
        if ((folderPathChanges?.Count ?? 0) > 0 && storageMutation.PathChanges.Count == 0)
        {
            mutation.RequiresFullInvalidate = true;
        }
        return mutation;
    }

    private InstalledChartLookupMutation BuildInstalledChartLookupUpsertMutation(OwnedChartCollectionStorageMutation storageMutation)
    {
        var mutation = new InstalledChartLookupMutation();
        bool fullLookupInitialized = IsInstalledChartLookupIndexInitializedUnsafe();
        bool primaryLookupInitialized = IsInstalledPrimaryHashLookupInitializedUnsafe();
        bool ownedHashLookupInitialized = catalogOwnedCollectionOwner.IsHashIndexWarm();
        bool playlistResolveLookupInitialized = IsPlaylistLibraryResolveIndexWarmUnsafe();
        if (storageMutation?.AddedCount > 0 != true
            || (!fullLookupInitialized
                && !primaryLookupInitialized
                && !ownedHashLookupInitialized
                && !playlistResolveLookupInitialized))
        {
            return mutation;
        }

        List<ChartFile> addedChartList = [.. storageMutation.AddedCharts.Where(chart => chart != null && !string.IsNullOrWhiteSpace(chart.Path))];
        // catalogで実際に置換するexact行だけを旧hashの除去対象にする。
        // FS正規化すると、別表記の追加でも残存ownerの所持数を減らしてしまう。
        var addedBmsPaths = new HashSet<string>(
            addedChartList.Where(chart => chart.Kind == ChartFileKind.Bms).Select(chart => chart.Path),
            StringComparer.Ordinal);
        var addedBmsonPaths = new HashSet<string>(
            addedChartList.Where(chart => chart.Kind == ChartFileKind.Bmson).Select(chart => chart.Path),
            StringComparer.Ordinal);
        var addedPaths = new HashSet<string>(addedBmsPaths, StringComparer.Ordinal);
        addedPaths.UnionWith(addedBmsonPaths);
        if (addedPaths.Count > 0)
        {
            bool existingRefsAvailable = TryCreateOwnedCanonicalChartRefsForPathsUnsafe(
                addedPaths,
                out List<LibraryChartRef> existingRefs);
            if (!existingRefsAvailable)
            {
                mutation.RequiresFullInvalidate = true;
                return mutation;
            }
            foreach (LibraryChartRef existingRef in existingRefs)
            {
                if (existingRef == null || string.IsNullOrWhiteSpace(existingRef.Path))
                {
                    continue;
                }
                string existingPathKey = existingRef.Path;
                if (existingRef.Kind == LibraryChartKind.Bms && addedBmsPaths.Contains(existingPathKey)
                    || existingRef.Kind == LibraryChartKind.Bmson && addedBmsonPaths.Contains(existingPathKey))
                {
                    mutation.Removed.Add(new InstalledChartLookupMutationEntry(
                        existingRef.Path,
                        existingRef.Md5,
                        existingRef.Sha256,
                        existingRef.Kind == LibraryChartKind.Bmson
                            ? ChartFileKind.Bmson
                            : ChartFileKind.Bms));
                }
            }
        }
        foreach (ChartFile chart in addedChartList)
        {
            mutation.Added.Add(new InstalledChartLookupMutationEntry(
                chart.Path,
                chart.Md5,
                chart.Sha256,
                chart.Kind));
        }
        return mutation;
    }

    private bool IsInstalledChartLookupIndexInitializedUnsafe()
    {
        return catalogOwnedCollectionOwner.IsInstalledChartLookupIndexInitialized();
    }

    private bool IsInstalledPrimaryHashLookupInitializedUnsafe()
    {
        return catalogOwnedCollectionOwner.IsInstalledPrimaryHashLookupInitialized();
    }

    private bool IsPlaylistLibraryResolveIndexWarmUnsafe()
    {
        return catalogOwnedCollectionOwner.IsPlaylistLibraryResolveIndexWarm();
    }

    /// <summary>
    /// installed chart directory lookup が build を強制せず初期化済みかを返します。
    /// </summary>
    internal bool IsInstalledChartLookupIndexInitializedForDiagnostics()
    {
        return IsInstalledChartLookupIndexInitializedUnsafe();
    }

    /// <summary>
    /// installed primary hash lookup が build を強制せず初期化済みかを返します。
    /// </summary>
    internal bool IsInstalledPrimaryHashLookupInitializedForDiagnostics()
    {
        return IsInstalledPrimaryHashLookupInitializedUnsafe();
    }

    private static InstalledChartLookupMutationEntry CreateInstalledChartLookupMutationEntry(ChartFile chart)
    {
        return new InstalledChartLookupMutationEntry(
            chart?.Path,
            chart?.Md5,
            chart?.Sha256,
            chart?.Kind ?? ChartFileKind.Bms);
    }

    private static InstalledChartLookupMutation BuildInstalledChartLookupDigestMutation(IEnumerable<LibraryChartDigestChange> digestChanges)
    {
        var mutation = new InstalledChartLookupMutation();
        foreach (LibraryChartDigestChange digestChange in digestChanges ?? [])
        {
            if (digestChange?.HasDigestChange != true)
            {
                continue;
            }
            if (string.IsNullOrWhiteSpace(digestChange.Path))
            {
                throw new InvalidOperationException("Owned chart digest changes require a current owner path.");
            }
            ChartFileKind kind = digestChange.Kind == LibraryChartKind.Bmson
                ? ChartFileKind.Bmson
                : ChartFileKind.Bms;
            mutation.Removed.Add(new InstalledChartLookupMutationEntry(
                digestChange.Path,
                digestChange.OldMd5,
                digestChange.OldSha256,
                kind));
            mutation.Added.Add(new InstalledChartLookupMutationEntry(
                digestChange.Path,
                digestChange.NewMd5,
                digestChange.NewSha256,
                kind));
        }
        return mutation;
    }

    private void ApplyInstalledChartLookupMutation(
        InstalledChartLookupMutation mutation,
        string reason,
        Action<string> logOverride = null)
    {
        if (mutation == null || !mutation.HasChanges)
        {
            return;
        }
        IReadOnlyList<string> logMessages;
        lock (pendingInstallEstimateCurrentnessGate)
        {
            logMessages = catalogOwnedCollectionOwner.ApplyInstalledChartLookupMutation(
                mutation,
                reason);
        }
        foreach (string logMessage in logMessages)
        {
            (logOverride ?? LogInstallPerformance)(logMessage);
        }
    }

    /// <summary>
    /// storage rows、owned collection、resource input の現在 version を一つの freshness token として取得します。
    /// </summary>
    internal ResourceHealthIndexCurrentVersion GetCurrentResourceHealthIndexVersion()
    {
        return new ResourceHealthIndexCurrentVersion(
            new StorageRowsVersionSnapshot(
                catalogStorageRowsOwner.BmsRowsVersion,
                catalogStorageRowsOwner.BmsonRowsVersion),
            catalogOwnedCollectionOwner.CollectionVersion,
            resourceHealthOwner.CurrentInputVersion);
    }

    /// <summary>
    /// resource health snapshot を取得します。cold 時は initialized-min と storage の reader 境界で構築し、
    /// catalog writer と並行して未確定の空 health view を公開しないようにします。
    /// </summary>
    /// <param name="reason">snapshot を構築する理由。</param>
    internal ResourceHealthIndexSnapshot GetResourceHealthIndexSnapshot(string reason)
    {
        ResourceHealthIndexSnapshot currentSnapshot = resourceHealthOwner.TryGetCurrentSnapshot();
        if (currentSnapshot != ResourceHealthIndexSnapshot.Empty)
        {
            return currentSnapshot;
        }
        using IDisposable readScope = EnterFolderMoveReadScope();
        return resourceHealthOwner.EnsureCurrent(
            reason,
            createFullOwnedResourceMaintenanceTargetSet(reason),
            GetCurrentResourceHealthIndexVersion());
    }

    private ResourceHealthIndexDispatchResult DispatchResourceHealthIndexMutation(
        ResourceHealthIndexMutation mutation,
        string reason)
    {
        return resourceHealthOwner.Apply(
            mutation?.ToFacts(),
            reason,
            GetCurrentResourceHealthIndexVersion(),
            createFullOwnedResourceMaintenanceTargetSet);
    }

    /// <summary>
    /// 確定済み maintenance target を canonical writer と共通 semantic 反映へ渡し、
    /// lease 解放後に実行する公開 action を組み立てます。
    /// </summary>
    internal MaintenanceWorkflowResult ApplyCatalogMaintenanceCore(
        ResourceMaintenanceTargetSet maintenanceTargets,
        bool forceUpdate,
        Action<MaintenanceWorkflowProgress> progressReporter,
        CancellationToken cancellationToken,
        ResourceHealthIndexUpdateMode resourceHealthIndexUpdateMode,
        string resourceHealthMutationReason,
        out List<ChartFile> currentMaintenanceTargetCharts,
        out Action postCommitEffect,
        Action<Action> postLeaseNotificationObserver = null)
    {
        postCommitEffect = null;
        CatalogWriteFailureFact deferredFailureFact = null;
        Action catalogPostCommitEffects = null;
        CatalogMaintenanceOperationReceipt receipt = catalogMaintenanceOwner.ApplyMaintenance(
            maintenanceTargets,
            forceUpdate,
            progressReporter,
            cancellationToken,
            resourceHealthIndexUpdateMode,
            resourceHealthMutationReason,
            captureFailureFact: fact => deferredFailureFact = fact,
            postCommitEffectsObserver: action => catalogPostCommitEffects = action);
        MaintenanceWorkflowResult workflowResult;
        currentMaintenanceTargetCharts = [.. maintenanceTargets.Charts];
        resourceHealthMutationReason = receipt.Reason;
        ResourceHealthIndexMutation resourceHealthMutation;
        OwnedChartCollectionMutationResult mutationResult = ApplyCatalogMaintenanceMutation(
            receipt,
            out workflowResult,
            out resourceHealthMutation);
        postCommitEffect = () =>
        {
            TryInvokePostLeaseNotification(
                () => catalogMutationOwner.PublishCatalogWriteFailureFactBestEffort(deferredFailureFact),
                "catalog_maintenance_failure_fact_publication_failed");
            TryInvokePostLeaseNotification(
                catalogPostCommitEffects,
                "catalog_maintenance_property_notification_failed");
            TryInvokePostLeaseNotification(
                () =>
                {
                    if (mutationResult.ParentFolderInvalidated)
                    {
                        NotifyParentFolderListCacheChanged();
                    }
                    PublishOwnedCollectionChangeNotification(mutationResult);
                    PublishNormalLibraryRefreshNotification(mutationResult);
                    RaiseNormalLibraryRefreshNotificationVersionChanged(mutationResult);
                },
                "catalog_maintenance_publication_failed");
            ResourceHealthIndexDispatchResult resourceHealthDispatch =
                mutationResult.ResourceHealthDispatchResult ?? new ResourceHealthIndexDispatchResult();
            bool resourceHealthIndexDeferred = resourceHealthDispatch.Deferred;
            workflowResult.ResourceHealthIndexMs = resourceHealthMutation.HasChanges
                && !resourceHealthIndexDeferred
                ? resourceHealthDispatch.IndexMs
                : 0L;
            workflowResult.WarningReapplyTargets = 0;
            workflowResult.WarningChangedCount = 0;
            LogAndReturnMaintenanceWorkflowResult(
                workflowResult,
                resourceHealthDispatch.Snapshot ?? ResourceHealthIndexSnapshot.Empty,
                resourceHealthDispatch.DeltaApplied,
                resourceHealthIndexDeferred,
                resourceHealthDispatch.FullRebuilt);
        };
        postLeaseNotificationObserver?.Invoke(postCommitEffect);
        return workflowResult;
    }

    /// <summary>
    /// 呼び出し側が保持する file-mutation reservation 内で subset maintenance を適用し、
    /// 外部公開だけを lease 解放後の observer へ遅延します。
    /// </summary>
    internal MaintenanceWorkflowResult ApplyCatalogMaintenanceUnderExistingReservation(
        IEnumerable<ChartFile> charts,
        bool forceUpdate,
        string resourceHealthMutationReason,
        Action<Action> postLeaseNotificationObserver)
    {
        if (charts == null)
        {
            return new MaintenanceWorkflowResult();
        }
        ResourceMaintenanceTargetSet maintenanceTargets = CreateResourceMaintenanceTargetSet(charts);
        return ApplyCatalogMaintenanceCore(
            maintenanceTargets,
            forceUpdate,
            progressReporter: null,
            cancellationToken: CancellationToken.None,
            resourceHealthIndexUpdateMode: ResourceHealthIndexUpdateMode.DeltaOnUpdates,
            resourceHealthMutationReason,
            out _,
            out Action postCommitEffect,
            postLeaseNotificationObserver);
    }

    private void TryInvokePostLeaseNotification(Action notification, string diagnostic)
    {
        if (notification == null)
        {
            return;
        }
        try
        {
            notification();
        }
        catch (Exception exception)
        {
            NLogWrapper.FileLogger?.Warn(exception, diagnostic);
        }
    }

    /// <summary>
    /// maintenance の確定結果へ resource index の反映状態を記録し、診断ログを出力します。
    /// 結果の意味付けと公開用 deferred action はこの owner 内で完了させます。
    /// </summary>
    private MaintenanceWorkflowResult LogAndReturnMaintenanceWorkflowResult(
        MaintenanceWorkflowResult workflowResult,
        ResourceHealthIndexSnapshot resourceHealthSnapshot,
        bool resourceHealthDeltaApplied,
        bool resourceHealthIndexDeferred,
        bool resourceHealthIndexFullRebuilt)
    {
        workflowResult.ResourceHealthIndexDeltaApplied = resourceHealthDeltaApplied;
        workflowResult.ResourceHealthIndexDeferred = resourceHealthIndexDeferred;
        workflowResult.ResourceHealthIndexFullRebuilt = resourceHealthIndexFullRebuilt;
        if (workflowResult.CheckedFileCount > 0
            || workflowResult.BmsonReparsedCount > 0
            || workflowResult.BmsonReparseFailedCount > 0
            || workflowResult.BmsonResourceReferenceReusedCount > 0
            || resourceHealthSnapshot.TargetCount > 0)
        {
            logInstallPerformance("maintenance_update checked=" + workflowResult.CheckedFileCount
                + " bmsResourceTargets=" + workflowResult.BmsResourceTargetCount
                + " bmsonResourceTargets=" + workflowResult.BmsonResourceTargetCount
                + " healthTargetCount=" + workflowResult.HealthTargetCount
                + " healthDegree=" + workflowResult.HealthDegree
                + " readerDegree=" + workflowResult.ReaderDegree
                + " readQueueCapacity=" + workflowResult.ReadQueueCapacity
                + " computedQueueCapacity=" + workflowResult.ComputedQueueCapacity
                + " forceTargets=" + workflowResult.ForceTargetCount
                + " missingInfoTargets=" + workflowResult.MissingInfoTargetCount
                + " missingEncodingTargets=" + workflowResult.MissingEncodingTargetCount
                + " bmsonMissingFreshRefs=" + workflowResult.BmsonMissingFreshResourceReferenceCount
                + " healthMs=" + workflowResult.HealthMs
                + " encodingMs=" + workflowResult.EncodingMs
                + " bmsonRefreshMs=" + workflowResult.BmsonRefreshMs
                + " healthCacheHit=" + workflowResult.HealthCacheHitCount
                + " healthFileExistsFallback=" + workflowResult.HealthFileExistsFallbackCount
                + " healthFileExistsFallbackAudio=" + workflowResult.HealthAudioFileExistsFallbackCount
                + " healthFileExistsFallbackImage=" + workflowResult.HealthImageFileExistsFallbackCount
                + " healthFileExistsFallbackMovie=" + workflowResult.HealthMovieFileExistsFallbackCount
                + " healthFileExistsFallbackOptionalImage=" + workflowResult.HealthOptionalImageFileExistsFallbackCount
                + " maintenanceUpserted=" + workflowResult.MaintenanceInfoUpsertCount
                + " maintenanceUnchanged=" + workflowResult.MaintenanceInfoUnchangedCount
                + " bmsonReparsed=" + workflowResult.BmsonReparsedCount
                + " bmsonReparseFailed=" + workflowResult.BmsonReparseFailedCount
                + " bmsonResourceRefsReused=" + workflowResult.BmsonResourceReferenceReusedCount
                + " songReloaded=" + workflowResult.ReloadedSongCount
                + " readMs=" + workflowResult.ReadMs
                + " digestMs=" + workflowResult.DigestMs
                + " computeMs=" + workflowResult.ComputeMs
                + " commitMs=" + workflowResult.CommitMs
                + " resourceHealthIndexMs=" + workflowResult.ResourceHealthIndexMs
                + " resourceHealthIndexMode=" + (resourceHealthIndexDeferred
                    ? "deferred"
                    : (resourceHealthDeltaApplied
                        ? "delta"
                        : (resourceHealthIndexFullRebuilt ? "full" : "current")))
                + " warningReapplyTargets=" + workflowResult.WarningReapplyTargets
                + " warningChanged=" + workflowResult.WarningChangedCount
                + " canceled=" + workflowResult.Canceled.ToString().ToLowerInvariant()
                + " elapsedMs=" + workflowResult.TotalMs);
        }
        return workflowResult;
    }

    /// <summary>
    /// 取得済みのfile mutation lease内でcatalog/package factsを適用します。
    /// 呼出元はそのleaseが発行したcapabilityを渡す必要があります。
    /// </summary>
    private FileDbMutationCommitResult CommitCatalogSessionChanges(
        LibraryCatalogMutationFacts catalogFacts,
        LibraryPackageReferenceFacts packageReferenceFacts,
        string reason,
        LibraryFileMutationCapability mutationCapability,
        Action<Action> postLeaseNotificationObserver,
        ref LibraryMutationSessionApplyCounts applyCounts,
        LibraryStorageRowPathNotificationPolicy storageRowPathNotificationPolicy = LibraryStorageRowPathNotificationPolicy.Notify)
    {
        ArgumentNullException.ThrowIfNull(mutationCapability);
        ArgumentNullException.ThrowIfNull(postLeaseNotificationObserver);

        bool durableCommit = false;
        try
        {
            ApplyLibraryMutationFactsCore(
                catalogFacts,
                packageReferenceFacts,
                reason,
                onDurableCommit: () => durableCommit = true,
                mutationCapability: mutationCapability,
                postLeaseNotificationObserver: postLeaseNotificationObserver,
                applyCounts: ref applyCounts,
                storageRowPathNotificationPolicy: storageRowPathNotificationPolicy);
            return FileDbMutationCommitResult.Durable();
        }
        catch (Exception exception)
        {
            return durableCommit
                ? FileDbMutationCommitResult.Durable(durableFailure: exception)
                : FileDbMutationCommitResult.Failed(exception);
        }
    }

    private void ApplyLibraryMutationFactsCore(
        LibraryCatalogMutationFacts catalogFacts,
        LibraryPackageReferenceFacts packageReferenceFacts,
        string performanceLogContext,
        Action onDurableCommit,
        LibraryFileMutationCapability mutationCapability,
        Action<Action> postLeaseNotificationObserver,
        ref LibraryMutationSessionApplyCounts applyCounts,
        LibraryStorageRowPathNotificationPolicy storageRowPathNotificationPolicy)
    {
        const string defaultReason = "library_delta";
        bool collectPerformanceLog = !string.IsNullOrWhiteSpace(performanceLogContext);
        Stopwatch totalStopwatch = collectPerformanceLog ? Stopwatch.StartNew() : null;
        var timings = new LibraryMutationApplyTimings();
        ResourceHealthIndexOwner.ResourceHealthInputMutation resourceHealthMutation = null;
        OwnedChartCollectionMutationResult mutationResult = null;
        CatalogMutationReceipt catalogReceipt = null;
        bool catalogMutationExpected = false;
        bool catalogMutationCommitted = false;
        ArgumentNullException.ThrowIfNull(postLeaseNotificationObserver);
        ArgumentNullException.ThrowIfNull(mutationCapability);
        try
        {
            Stopwatch resourceHealthBeginStopwatch = collectPerformanceLog ? Stopwatch.StartNew() : null;
            resourceHealthMutation = resourceHealthOwner.BeginInputMutation();
            timings.ResourceHealthBeginMs = StopPerformanceStepStopwatch(resourceHealthBeginStopwatch);
            try
            {
                Stopwatch buildMutationStopwatch = collectPerformanceLog ? Stopwatch.StartNew() : null;
                mutationResult = BuildOwnedChartCollectionMutationResult(
                    catalogFacts,
                    packageReferenceFacts,
                    storageRowPathNotificationPolicy,
                    resourceHealthMutation.BaseInputVersion,
                    resourceHealthIndexCurrentAtBase: resourceHealthMutation.BaseIndexCurrent);
                timings.BuildMutationMs = StopPerformanceStepStopwatch(buildMutationStopwatch);

                using (mutationResult?.ResourceHealthIndexInvalidated == true
                    ? resourceHealthOwner.SuppressInvalidation()
                    : null)
                {
                    Stopwatch stateApplyStopwatch = collectPerformanceLog ? Stopwatch.StartNew() : null;
                    catalogMutationExpected = catalogFacts?.HasChanges == true
                        || mutationResult?.StorageMutation?.RemoveRequests?.Count > 0;
                    applyCounts = applyCounts with { CatalogApplyCount = applyCounts.CatalogApplyCount + 1 };
                    catalogReceipt = catalogMutationOwner.ApplyCatalogMutation(
                        catalogFacts,
                        () =>
                        {
                            catalogMutationCommitted = true;
                            onDurableCommit?.Invoke();
                        });
                    applyCounts = applyCounts with
                    {
                        FolderDbTargetRows = applyCounts.FolderDbTargetRows + (catalogReceipt?.FolderDbTargetRows ?? 0),
                        FolderDbFullScanCount = applyCounts.FolderDbFullScanCount + (catalogReceipt?.FolderDbFullScanCount ?? 0)
                    };
                    ApplyCatalogMutationReceiptProjection(mutationResult, catalogReceipt);
                    PlaylistReferenceCatalogApplyResult playlistReferenceApplyResult = playlistReferenceOwner.ApplyCatalogMutationReceipt(
                        catalogReceipt,
                        SnapshotLibraryChartRefsForPlaylistReferenceApply(
                            catalogReceipt?.AddedCharts));
                    timings.PlaylistReferenceAffectedCharts = playlistReferenceApplyResult.AffectedChartCount;
                    timings.PlaylistReferenceMatchedCharts = playlistReferenceApplyResult.MatchedChartCount;
                    timings.StateApplyMs = StopPerformanceStepStopwatch(stateApplyStopwatch);
                    if (catalogReceipt?.Applied == true)
                    {
                        timings.StateFolderDbMs = catalogReceipt.FolderDbMs;
                        timings.StatePathMemoryApplyMs = catalogReceipt.LiveApplyMs;
                        timings.StateBmsPathDbMs = catalogReceipt.BmsPathDbMs;
                        timings.StateBmsonPathDbMs = catalogReceipt.BmsonPathDbMs;
                        timings.StateBmsRemovalDbMs = catalogReceipt.BmsRemovalDbMs;
                        timings.StateBmsonRemovalDbMs = catalogReceipt.BmsonRemovalDbMs;
                    }
                }

                Stopwatch residualApplyStopwatch = collectPerformanceLog ? Stopwatch.StartNew() : null;
                applyCounts = applyCounts with { PackageReferenceApplyCount = applyCounts.PackageReferenceApplyCount + 1 };
                BmsLibraryStateApplyResult residualStateApplyResult = packageLifecycleOwner.ApplyPackageReferenceFacts(
                    packageReferenceFacts,
                    catalogReceipt?.RemovedCharts,
                    catalogReceipt?.PathFacts);
                timings.StateApplyMs += StopPerformanceStepStopwatch(residualApplyStopwatch);
                timings.StatePackageApplyMs = residualStateApplyResult?.PackageApplyMs ?? 0;
            }
            finally
            {
                Stopwatch resourceHealthDisposeStopwatch = collectPerformanceLog ? Stopwatch.StartNew() : null;
                resourceHealthMutation.Dispose();
                timings.ResourceHealthDisposeMs = StopPerformanceStepStopwatch(resourceHealthDisposeStopwatch);
            }

            mutationResult.ResourceHealthMutation.DeltaTargetResourceHealthInputVersion ??= resourceHealthMutation.TargetInputVersion;
            if (mutationResult.ResourceHealthMutation.DeltaTargetResourceHealthInputVersion.Value < 0)
            {
                mutationResult.ResourceHealthMutation.Invalidate = true;
            }
            Stopwatch lr2NormalFolderSyncStopwatch = collectPerformanceLog ? Stopwatch.StartNew() : null;
            applyCounts = applyCounts with { Lr2SyncCount = applyCounts.Lr2SyncCount + 1 };
            lr2SynchronizationOwner.SyncLr2NormalFoldersForCatalogMutation(
                CreateLr2NormalFolderCatalogMutationReceipt(
                    catalogReceipt,
                    mutationResult.OwnedCollectionVersion),
                performanceLogContext ?? defaultReason,
                mutationCapability);
            timings.Lr2NormalFolderSyncMs = StopPerformanceStepStopwatch(lr2NormalFolderSyncStopwatch);
            Stopwatch dispatchStopwatch = collectPerformanceLog ? Stopwatch.StartNew() : null;
            DispatchOwnedChartCollectionMutation(
                mutationResult,
                defaultReason,
                publishOwnedCollectionNotifications: false,
                publishNormalRefreshNotification: false);
            timings.DispatchMs = StopPerformanceStepStopwatch(dispatchStopwatch);

            Action publishNotifications = () =>
            {
                Stopwatch publishNotificationStopwatch = collectPerformanceLog ? Stopwatch.StartNew() : null;
                TryInvokePostLeaseNotification(() =>
                {
                    if (mutationResult.ParentFolderInvalidated)
                    {
                        NotifyParentFolderListCacheChanged();
                    }
                }, "library_mutation_parent_folder_notification_failed");
                TryInvokePostLeaseNotification(
                    () => PublishOwnedCollectionChangeNotification(mutationResult),
                    "library_mutation_collection_notification_failed");
                timings.PublishNotificationMs = StopPerformanceStepStopwatch(publishNotificationStopwatch);
                TryInvokePostLeaseNotification(
                    () =>
                    {
                        PublishNormalLibraryRefreshNotification(mutationResult);
                        RaiseNormalLibraryRefreshNotificationVersionChanged(mutationResult);
                    },
                    "library_mutation_refresh_notification_failed");
                if (collectPerformanceLog)
                {
                    timings.ElapsedMs = StopPerformanceStepStopwatch(totalStopwatch);
                    TryInvokePostLeaseNotification(
                        () => LogInstallPerformance("library_mutation_facts_apply context=" + performanceLogContext
                            + " unregisterCharts=" + (catalogFacts?.ChartRemoveRequests?.Count ?? 0)
                            + " pathChanges=" + (catalogFacts?.ChartPathChanges?.Count ?? 0)
                            + " folderPathChanges=" + (catalogFacts?.FolderPathChanges?.Count ?? 0)
                            + " installDestinations=" + (packageReferenceFacts?.InstallDestinationChanges?.Count ?? 0)
                            + " installedPackagePaths=" + (packageReferenceFacts?.InstalledPackagePathChanges?.Count ?? 0)
                            + " resourceHealthBeginMs=" + timings.ResourceHealthBeginMs
                            + " buildMutationMs=" + timings.BuildMutationMs
                            + " publishNotificationMs=" + timings.PublishNotificationMs
                            + " stateApplyMs=" + timings.StateApplyMs
                            + " stateFolderDbMs=" + timings.StateFolderDbMs
                            + " statePathMemoryApplyMs=" + timings.StatePathMemoryApplyMs
                            + " stateBmsPathDbMs=" + timings.StateBmsPathDbMs
                            + " stateBmsonPathDbMs=" + timings.StateBmsonPathDbMs
                            + " stateBmsRemovalDbMs=" + timings.StateBmsRemovalDbMs
                            + " stateBmsonRemovalDbMs=" + timings.StateBmsonRemovalDbMs
                            + " statePackageApplyMs=" + timings.StatePackageApplyMs
                            + " playlistReferenceAffectedCharts=" + timings.PlaylistReferenceAffectedCharts
                            + " playlistReferenceMatchedCharts=" + timings.PlaylistReferenceMatchedCharts
                            + " resourceHealthDisposeMs=" + timings.ResourceHealthDisposeMs
                            + " lr2NormalFolderSyncMs=" + timings.Lr2NormalFolderSyncMs
                            + " dispatchMs=" + timings.DispatchMs
                            + " elapsedMs=" + timings.ElapsedMs),
                        "library_mutation_performance_log_failed");
                }
            };
            postLeaseNotificationObserver(publishNotifications);
            applyCounts = applyCounts with { RequiredPublicationCount = applyCounts.RequiredPublicationCount + 1 };
        }
        catch
        {
            if (catalogMutationExpected && !catalogMutationCommitted)
            {
                resourceHealthOwner.RebaseAfterInputMutation(
                    resourceHealthMutation,
                    GetCurrentResourceHealthIndexVersion());
            }
            if (mutationResult?.ShouldDispatchInstalledLookup == true)
            {
                InvalidateInstalledDirectoryIndex();
            }
            else if (mutationResult?.InstallEstimationMetadataProfileCacheInvalidated == true)
            {
                InvalidateInstallEstimationMetadataProfileCache();
            }
            if (mutationResult?.ParentFolderInvalidated == true)
            {
                InvalidateParentFolderListCache();
            }
            if (mutationResult?.DuplicateCacheInvalidated == true)
            {
                InvalidateDuplicateChartGroupsCache();
            }
            if (HasOwnedHashSetChanges(mutationResult))
            {
                catalogOwnedCollectionOwner.InvalidateHashIndexSnapshot();
            }
            if (mutationResult?.OwnedCollectionChanged == true)
            {
                catalogOwnedCollectionOwner.InvalidatePlaylistLibraryResolveIndexSnapshot();
            }
            if (mutationResult?.ResourceHealthMutation.HasChanges == true
                && !(catalogMutationExpected && !catalogMutationCommitted))
            {
                resourceHealthOwner.ForceInvalidate("library_delta_failed");
            }
            if (mutationResult?.InstallDestinationRuntimeStateMutation.HasChanges == true)
            {
                installDestinationStateOwner.PruneToCurrentOwnedCharts();
            }
            InvalidateOwnedChartCollection();
            if (mutationResult != null)
            {
                ClearNormalLibraryRefreshNotification(mutationResult);
            }
            throw;
        }
    }

    private void ApplyCatalogMutationReceiptProjection(
        OwnedChartCollectionMutationResult mutationResult,
        CatalogMutationReceipt receipt)
    {
        if (mutationResult == null || receipt?.Applied != true)
        {
            return;
        }

        mutationResult.AddedCount = receipt.AddedCharts.Count;
        mutationResult.RemovedCount = receipt.RemovedCharts.Count;
        mutationResult.MovedCount = receipt.MovedCharts.Count;
        bool hasCatalogFacts = receipt.AddedCharts.Count > 0
            || receipt.RemovedCharts.Count > 0
            || receipt.MovedCharts.Count > 0;
        mutationResult.OwnedCollectionVersion = receipt.OwnedCollectionVersion;
        mutationResult.OwnedCollectionVersionAlreadyAdvanced = hasCatalogFacts;
        mutationResult.BmsonCanonicalOrderNormalized |= receipt.BmsonCanonicalOrderNormalized;
        mutationResult.OwnedCollectionChanged |= hasCatalogFacts;
        mutationResult.DuplicateCacheInvalidated |= receipt.AddedCharts.Count > 0
            || receipt.RemovedCharts.Count > 0;
        mutationResult.WarningPresentationChanged |= mutationResult.OwnedCollectionChanged;
    }


    private void PublishNormalLibraryRefreshNotification(OwnedChartCollectionMutationResult result)
    {
        if (result == null)
        {
            return;
        }
        IReadOnlyList<ChartFile> installDestinationChangedCharts = result.InstallDestinationChangedCharts ?? [];
        LibraryChartRefreshEffects effects = CreateLibraryChartRefreshEffects(result, installDestinationChangedCharts);
        if (effects == LibraryChartRefreshEffects.None)
        {
            return;
        }
        int ownedCollectionVersion = result.OwnedCollectionVersion > 0 ? result.OwnedCollectionVersion : catalogOwnedCollectionOwner.CollectionVersion;
        bool storageRowsRemoveDeltaComplete = IsCompleteRemoveOnlyStorageRowsMutation(result);
        IReadOnlyList<BMSFile> removedBmsFiles = storageRowsRemoveDeltaComplete
            ? CreateRemovedBmsStorageRowDelta(result.StorageMutation)
            : [];
        IReadOnlyList<LR2SongDBExtended.bmson_song> removedBmsonSongs = storageRowsRemoveDeltaComplete
            ? CreateRemovedBmsonStorageRowDelta(result.StorageMutation)
            : [];
        int version = normalLibraryRefreshPublisher.Publish(new NormalLibraryRefreshPublishRequest
        {
            OwnedCollectionVersion = ownedCollectionVersion,
            Effects = effects,
            InstallDestinationChangedCharts = installDestinationChangedCharts,
            NotifiesStorageRows = result.StorageRowsChanged,
            ResetsPriorNotifications = false,
            NotifiesBmsFiles = result.BmsFilesStorageRowsChanged,
            NotifiesBmsonSongs = result.BmsonSongsStorageRowsChanged,
            RemovedBmsFiles = removedBmsFiles,
            RemovedBmsonSongs = removedBmsonSongs,
            StorageRowsRemoveDeltaComplete = storageRowsRemoveDeltaComplete
        });
        result.NormalLibraryRefreshNotificationVersion = version;
    }

    private static bool IsCompleteRemoveOnlyStorageRowsMutation(OwnedChartCollectionMutationResult result)
    {
        OwnedChartCollectionStorageMutation mutation = result?.StorageMutation;
        return result?.StorageRowsChanged == true
            && result.StorageRowsRemoveDeltaComplete
            && result.DigestChangedCount == 0
            && mutation?.RemovedCount > 0
            && !mutation.RemoveRequests.Any(request => request?.Mode == OwnedChartRemoveMode.PathCleanup)
            && mutation.AddedCount == 0
            && mutation.MovedCount == 0;
    }

    private static IReadOnlyList<BMSFile> CreateRemovedBmsStorageRowDelta(OwnedChartCollectionStorageMutation mutation)
    {
        if (mutation?.RemovedCount > 0 != true)
        {
            return [];
        }
        return [.. mutation.RemoveRequests
            .Select(request => request?.BmsOwner)
            .Where(file => file != null)
            .Distinct()];
    }

    private static IReadOnlyList<LR2SongDBExtended.bmson_song> CreateRemovedBmsonStorageRowDelta(OwnedChartCollectionStorageMutation mutation)
    {
        if (mutation?.RemovedCount > 0 != true)
        {
            return [];
        }
        return [.. mutation.RemoveRequests
            .Select(request => request?.BmsonOwner)
            .Where(song => song != null)
            .Distinct()];
    }

    private static LibraryChartRefreshEffects CreateLibraryChartRefreshEffects(
        OwnedChartCollectionMutationResult result,
        IReadOnlyList<ChartFile> installDestinationChangedCharts)
    {
        var effects = LibraryChartRefreshEffects.None;
        if (result?.OwnedCollectionChanged == true)
        {
            effects |= LibraryChartRefreshEffects.SourceChanged;
        }
        if ((installDestinationChangedCharts?.Count ?? 0) > 0
            || result?.InstallDestinationRuntimeStateMutation?.PruneToCurrentOwnedCharts == true)
        {
            effects |= LibraryChartRefreshEffects.InstallDestinationOverlayChanged;
        }
        if (result?.WarningPresentationChanged == true)
        {
            effects |= LibraryChartRefreshEffects.WarningPresentationChanged;
        }
        if (result?.MaintenancePresentationChanged == true)
        {
            effects |= LibraryChartRefreshEffects.MaintenancePresentationChanged;
        }
        return effects;
    }

    private void PublishNormalLibraryRefreshResetNotification(bool notifiesBmsFiles, bool notifiesBmsonSongs)
    {
        bool notifiesStorageRows = notifiesBmsFiles || notifiesBmsonSongs;
        normalLibraryRefreshPublisher.Publish(new NormalLibraryRefreshPublishRequest
        {
            OwnedCollectionVersion = catalogOwnedCollectionOwner.CollectionVersion,
            Effects = LibraryChartRefreshEffects.SourceChanged | LibraryChartRefreshEffects.InstallDestinationOverlayChanged,
            InstallDestinationChangedCharts = [],
            NotifiesStorageRows = notifiesStorageRows,
            ResetsPriorNotifications = true,
            NotifiesBmsFiles = notifiesBmsFiles,
            NotifiesBmsonSongs = notifiesBmsonSongs
        });
        raiseNormalLibraryRefreshVersionChanged();
    }

    /// <summary>
    /// 外部 replacement が確定した storage rows を facade の refresh 通知へ反映します。
    /// </summary>
    /// <param name="notifiesBmsFiles">BMS rows の変更を通知する場合は true。</param>
    /// <param name="notifiesBmsonSongs">BMSON rows の変更を通知する場合は true。</param>
    internal void PublishExternalReplacementNormalLibraryRefreshNotification(bool notifiesBmsFiles, bool notifiesBmsonSongs)
    {
        PublishNormalLibraryRefreshResetNotification(notifiesBmsFiles, notifiesBmsonSongs);
    }

    private void ClearNormalLibraryRefreshNotification(OwnedChartCollectionMutationResult result)
    {
        if (result == null || result.NormalLibraryRefreshNotificationVersion <= 0)
        {
            return;
        }
        normalLibraryRefreshPublisher.Clear(result.NormalLibraryRefreshNotificationVersion);
    }

    private void RaiseNormalLibraryRefreshNotificationVersionChanged(OwnedChartCollectionMutationResult result)
    {
        if (result?.NormalLibraryRefreshNotificationVersion > 0)
        {
            raiseNormalLibraryRefreshVersionChanged();
        }
    }

    /// <summary>
    /// install estimation が保持する metadata profile cache を失効させます。
    /// </summary>
    internal void InvalidateInstallEstimationMetadataProfileCache()
    {
        invalidateInstallEstimationMetadataProfileCache();
    }

    private bool InvalidateDuplicateChartGroupsCache(bool publishNotification = true)
    {
        return invalidateDuplicateChartGroupsCache(publishNotification);
    }

    private void InvalidateParentFolderListCache()
    {
        invalidateParentFolderListCache();
    }

    private void NotifyParentFolderListCacheChanged()
    {
        notifyParentFolderListCacheChanged();
    }

    private void InvalidateParentFolderListCacheAndNotify()
    {
        InvalidateParentFolderListCache();
        NotifyParentFolderListCacheChanged();
    }

    private List<PlaylistReferenceChartSnapshot> SnapshotLibraryChartRefsForPlaylistReferenceApply(
        IEnumerable<CatalogChartMutationFact> facts)
    {
        HashSet<string> md5Hashes = new(StringComparer.OrdinalIgnoreCase);
        HashSet<string> sha256Hashes = new(StringComparer.OrdinalIgnoreCase);
        foreach (CatalogChartMutationFact fact in facts ?? [])
        {
            if (!string.IsNullOrWhiteSpace(fact?.Md5))
            {
                md5Hashes.Add(fact.Md5);
            }
            if (!string.IsNullOrWhiteSpace(fact?.Sha256))
            {
                sha256Hashes.Add(fact.Sha256);
            }
        }
        if (md5Hashes.Count == 0 && sha256Hashes.Count == 0)
        {
            return [];
        }

        catalogOwnedCollectionOwner.EnsureCurrent(catalogStorageRowsOwner);
        lock (catalogOwnedCollectionOwner.Gate)
        {
            return [.. catalogOwnedCollectionOwner.Collection
                .CreateLibraryChartRefsForHashes(md5Hashes, sha256Hashes)
                .Select(PlaylistReferenceChartSnapshot.FromLibraryChartRef)
                .Where(snapshot => snapshot != null)];
        }
    }

    private Lr2NormalFolderCatalogMutationReceipt CreateLr2NormalFolderCatalogMutationReceipt(
        CatalogInstalledTargetUpsertReceipt receipt,
        int ownedCollectionVersion)
    {
        return createInstalledTargetLr2NormalFolderMutationReceipt(receipt, ownedCollectionVersion);
    }

    private Lr2NormalFolderCatalogMutationReceipt CreateLr2NormalFolderCatalogMutationReceipt(
        CatalogMutationReceipt receipt,
        int ownedCollectionVersion)
    {
        return createCatalogLr2NormalFolderMutationReceipt(receipt, ownedCollectionVersion);
    }

    private bool TryCreateOwnedCanonicalChartRefsForPathsUnsafe(
        IEnumerable<string> paths,
        out List<LibraryChartRef> chartRefs)
    {
        lock (catalogOwnedCollectionOwner.Gate)
        {
            if (!catalogOwnedCollectionOwner.IsInitialized)
            {
                chartRefs = null;
                return false;
            }
            chartRefs = catalogOwnedCollectionOwner.Collection.CreateLibraryChartRefsForCanonicalPaths(paths);
            return true;
        }
    }

    /// <summary>
    /// operation-scoped install session が集約した storage target と pending install row mutation を
    /// 一つの durable apply として適用します。
    /// </summary>
    /// <param name="addedTargets">全成功 package の exact identity 規則で正規化済み target。</param>
    /// <param name="installPathsToDelete">同じ transaction で削除する pending install row path。</param>
    /// <param name="installRowsToUpsert">同じ transaction で upsert する pending install row。</param>
    /// <param name="lookupReason">reverse lookup / publication の診断理由。</param>
    /// <param name="mutationCapability">outer file mutation lease の capability。</param>
    /// <param name="postLeaseNotificationObserver">lease 解放後に行う公開 action の collector。</param>
    /// <param name="applyCounts">実際に試行した owner 反映回数の集約先。</param>
    /// <returns>durable point と required internal apply failure を表す commit result。</returns>
    private FileDbMutationCommitResult CommitInstalledSessionChanges(
        ChartStorageTargetSet addedTargets,
        IEnumerable<string> installPathsToDelete,
        IEnumerable<ChartPackage> installRowsToUpsert,
        string lookupReason,
        LibraryFileMutationCapability mutationCapability,
        Action<Action> postLeaseNotificationObserver,
        ref LibraryMutationSessionApplyCounts applyCounts)
    {
        ArgumentNullException.ThrowIfNull(mutationCapability);
        ArgumentNullException.ThrowIfNull(postLeaseNotificationObserver);
        mutationCapability.Validate(lr2SynchronizationOwner);
        addedTargets ??= ChartStorageTargetSet.FromInstalledCharts([]);
        List<string> installPaths = [.. (installPathsToDelete ?? [])
            .Where(path => !string.IsNullOrWhiteSpace(path))
            .Distinct(StringComparer.Ordinal)];
        List<ChartPackage> installRows = [.. (installRowsToUpsert ?? [])
            .Where(package => package != null && !string.IsNullOrWhiteSpace(package.path))
            .GroupBy(package => package.path, StringComparer.Ordinal)
            .Select(group => group.Last())];
        if (addedTargets.Charts.Count == 0 && installPaths.Count == 0 && installRows.Count == 0)
        {
            return FileDbMutationCommitResult.Durable();
        }

        InstalledChartStorageTargetsApplyReceipt storageReceipt =
            ApplyInstalledChartStorageTargetsForDeferredDispatch(
                addedTargets,
                lookupReason,
                mutationCapability,
                ref applyCounts,
                installPathsToDelete: installPaths,
                installRowsToUpsert: installRows);
        if (storageReceipt.Failure != null)
        {
            postLeaseNotificationObserver(
                () => PublishInstalledChartStorageTargetsAfterGuard(storageReceipt));
            return FileDbMutationCommitResult.Failed(storageReceipt.Failure.SourceException);
        }

        Exception completionFailure = null;
        try
        {
            CompleteInstalledChartStorageTargetsUnderExistingReservation(
                storageReceipt,
                mutationCapability,
                ref applyCounts);
        }
        catch (Exception exception)
        {
            completionFailure = exception;
        }
        if (completionFailure == null)
        {
            postLeaseNotificationObserver(() => PublishInstalledChartStorageTargetsAfterGuard(storageReceipt));
            applyCounts = applyCounts with { RequiredPublicationCount = applyCounts.RequiredPublicationCount + 1 };
        }
        return FileDbMutationCommitResult.Durable(durableFailure: completionFailure);
    }

    /// <summary>
    /// package install result から、確定済みの追加譜面を不変 target set へ変換します。
    /// </summary>
    /// <param name="installResult">package executor が返した確定結果。</param>
    /// <returns>追加譜面の target set。</returns>
    internal static ChartStorageTargetSet CreateAddedStorageTargets(PackageInstallExecutionResult installResult)
    {
        List<ChartFile> charts = [.. (installResult?.AddedCharts ?? [])
            .Where(chart => chart != null && !string.IsNullOrWhiteSpace(chart.Path))];
        if (installResult?.InstalledTargetSet is ChartStorageTargetSet existingTargets
            && existingTargets.Charts.Count == charts.Count
            && existingTargets.Charts
                .Select((chart, index) => ReferenceEquals(chart, charts[index]))
                .All(isSameChart => isSameChart))
        {
            return existingTargets;
        }

        ChartStorageTargetSet targets = ChartStorageTargetSet.FromInstalledCharts(charts);
        if (installResult != null)
        {
            installResult.InstalledTargetSet = targets;
        }
        return targets;
    }

    /// <summary>
    /// warning presentation の変更を common mutation dispatch へ渡します。
    /// </summary>
    /// <param name="reason">dispatch 理由。</param>
    internal void DispatchWarningPresentationChanged(string reason)
    {
        DispatchOwnedChartCollectionMutation(
            new OwnedChartCollectionMutationResult
            {
                WarningPresentationChanged = true
            },
            reason);
    }

    /// <summary>
    /// hydration receipt を resource/maintenance presentation の mutation として公開します。
    /// </summary>
    /// <param name="receipt">hydration が確定した receipt。</param>
    /// <returns>resource index dispatch の処理時間。</returns>
    internal long PublishMaintenanceHydrationReceipt(CatalogMaintenanceHydrationReceipt receipt)
    {
        if (receipt == null)
        {
            return 0L;
        }
        ResourceHealthIndexMutation resourceHealthMutation = receipt.ResourceHealthMutation.ToMutation();
        var mutationResult = new OwnedChartCollectionMutationResult
        {
            WarningPresentationChanged = resourceHealthMutation.HasChanges,
            MaintenancePresentationChanged = receipt.ViewRefreshQueued
                || resourceHealthMutation.HasChanges
        };
        CopyResourceHealthIndexMutation(resourceHealthMutation, mutationResult.ResourceHealthMutation);
        DispatchOwnedChartCollectionMutation(mutationResult, "maintenance_hydration");
        return mutationResult.ResourceHealthDispatchResult?.IndexMs ?? 0L;
    }

    /// <summary>
    /// warning ignore の確定 receipt を common maintenance dispatch へ反映します。
    /// </summary>
    /// <param name="receipt">maintenance writer の確定 receipt。</param>
    /// <param name="reason">dispatch 理由。</param>
    internal void ApplyCatalogMaintenanceWarningIgnore(
        CatalogMaintenanceOperationReceipt receipt,
        string reason)
    {
        OwnedChartCollectionMutationResult mutationResult = BuildOwnedChartCollectionMaintenanceMutationResult(
            receipt?.ResourceHealthMutation?.ToMutation(),
            receipt?.WorkflowResult?.HasUpdates == true);
        mutationResult.MaintenancePresentationChanged = false;
        DispatchOwnedChartCollectionMutation(mutationResult, reason);
    }

    /// <summary>
    /// estimated install に伴う maintenance を実行し、lease 解放後の公開 action を返します。
    /// </summary>
    /// <param name="charts">今回の導入で確定した対象。</param>
    /// <param name="deferredFeedback">導入側の診断・dialog 接続。</param>
    /// <param name="publishAfterGuard">解放後に実行する公開 action。</param>
    /// <returns>対象件数と確定 failure。</returns>
    internal PendingEstimatedInstallPostGuardResult ApplyEstimatedInstallMaintenanceForDeferredDispatch(
        IEnumerable<ChartFile> charts,
        EstimatedInstallDeferredFeedback deferredFeedback,
        out Action publishAfterGuard)
    {
        List<ChartFile> targets = [.. (charts ?? [])
            .Where(chart => chart != null && !string.IsNullOrWhiteSpace(chart.Path))];
        if (targets.Count == 0)
        {
            publishAfterGuard = null;
            return new PendingEstimatedInstallPostGuardResult(0);
        }

        CatalogWriteFailureFact deferredFailureFact = null;
        Action catalogPostCommitEffects = null;
        try
        {
            CatalogMaintenanceOperationReceipt ownerReceipt = catalogMaintenanceOwner.ApplyMaintenance(
                CreateResourceMaintenanceTargetSet(targets),
                forceUpdate: true,
                progressReporter: null,
                cancellationToken: CancellationToken.None,
                resourceHealthIndexUpdateMode: ResourceHealthIndexUpdateMode.DeltaOnUpdates,
                reason: "install_package_estimated",
                dialogServiceOverride: deferredFeedback?.DialogService,
                logPerformanceOverride: deferredFeedback == null
                    ? null
                    : deferredFeedback.LogInstallPerformance,
                captureFailureFact: fact => deferredFailureFact = fact,
                postCommitEffectsObserver: action => catalogPostCommitEffects = action);
            MaintenanceWorkflowResult workflowResult;
            ResourceHealthIndexMutation resourceHealthMutation;
            OwnedChartCollectionMutationResult mutationResult;
            try
            {
                mutationResult = ApplyCatalogMaintenanceMutation(
                    ownerReceipt,
                    out workflowResult,
                    out resourceHealthMutation);
            }
            catch
            {
                InvalidateOwnedChartCollection();
                InvalidateInstalledDirectoryIndex();
                resourceHealthOwner.ForceInvalidate("maintenance_dispatch_failed");
                throw;
            }

            ResourceHealthIndexDispatchResult resourceHealthDispatch =
                mutationResult.ResourceHealthDispatchResult ?? new ResourceHealthIndexDispatchResult();
            workflowResult.ResourceHealthIndexMs = resourceHealthMutation.HasChanges
                && !resourceHealthDispatch.Deferred
                ? resourceHealthDispatch.IndexMs
                : 0L;
            workflowResult.WarningReapplyTargets = 0;
            workflowResult.WarningChangedCount = 0;
            publishAfterGuard = () =>
            {
                catalogMutationOwner.PublishCatalogWriteFailureFactBestEffort(deferredFailureFact);
                TryInvokePostLeaseNotification(
                    catalogPostCommitEffects,
                    "estimated_install_catalog_property_notification_failed");
                TryInvokePostLeaseNotification(
                    () => PublishOwnedCollectionChangeNotification(mutationResult),
                    "estimated_install_collection_notification_failed");
                TryInvokePostLeaseNotification(
                    () =>
                    {
                        PublishNormalLibraryRefreshNotification(mutationResult);
                        RaiseNormalLibraryRefreshNotificationVersionChanged(mutationResult);
                    },
                    "estimated_install_refresh_notification_failed");
                LogAndReturnMaintenanceWorkflowResult(
                    workflowResult,
                    resourceHealthDispatch.Snapshot ?? ResourceHealthIndexSnapshot.Empty,
                    resourceHealthDispatch.DeltaApplied,
                    resourceHealthDispatch.Deferred,
                    resourceHealthDispatch.FullRebuilt);
            };
            return new PendingEstimatedInstallPostGuardResult(targets.Count);
        }
        catch (Exception exception)
        {
            publishAfterGuard = () =>
            {
                catalogMutationOwner.PublishCatalogWriteFailureFactBestEffort(deferredFailureFact);
                InvalidateOwnedChartCollection();
                InvalidateInstalledDirectoryIndex();
                resourceHealthOwner.ForceInvalidate("maintenance_apply_failed");
            };
            return new PendingEstimatedInstallPostGuardResult(
                targets.Count,
                ExceptionDispatchInfo.Capture(exception));
        }
    }
}
