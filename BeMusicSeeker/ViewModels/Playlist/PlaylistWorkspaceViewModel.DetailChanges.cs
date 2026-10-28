using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using BeMusicSeeker.Models;
using BeMusicSeeker.Models.BmsLibraryInternal;
using BeMusicSeeker.Models.Utils;

namespace BeMusicSeeker.ViewModels;

public sealed partial class PlaylistWorkspaceViewModel
{
    private BMSPlaylist detailEntryCommitStore;
    private Func<BMSLibrary, CancellationToken, Task> libraryChangeDelivery;
    private readonly List<PlaylistEntryCommitReceipt> deferredEntryCommits = [];
    private readonly object detailEntryDeliveryLock = new();
    private Task detailEntryDelivery = Task.CompletedTask;
    private int? summaryOwnedHashContentVersion;

    /// <summary>初回構築と局所更新の実投影地点を観測します。通常実行では未設定です。</summary>
    internal Action<string> DetailWorkObserver { get; set; }

    /// <summary>lock取得前の現在選択を、一配送の変更と公開通知の終端まで保持します。</summary>
    private T PreserveDetailSelection<T>(Func<T> update)
        => DetailViewState.View.Rows is PlaylistDetailVirtualView view ? view.PreserveSelection(update) : update();

    private void ReportDetailWork(string kind) => DetailWorkObserver?.Invoke(kind);

    private PlaylistDetailDependencies EnsureDetailDependencies()
    {
        PlaylistDetailSourceSnapshotState state = DetailViewState.Source;
        if (state.Dependencies == null || !ReferenceEquals(state.Dependencies.Rows, state.Rows))
        {
            state.Dependencies = new(state.Rows ??= [], ReportDetailWork);
        }
        return state.Dependencies;
    }

    private void SetDetailEntryCommitStore(BMSPlaylist store)
    {
        if (ReferenceEquals(detailEntryCommitStore, store)) { return; }
        if (detailEntryCommitStore != null) { detailEntryCommitStore.PlaylistEntryCommitsAvailable -= DetailEntryCommitsAvailable; }
        deferredEntryCommits.Clear();
        deferredCatalogCommits.Clear();
        detailEntryCommitStore = store;
        if (store != null) { store.PlaylistEntryCommitsAvailable += DetailEntryCommitsAvailable; }
    }

    /// <summary>通常一覧が所有する単一drainの実配送終端へ、詳細要求の待機を接続します。</summary>
    internal void SetLibraryChangeDelivery(Func<BMSLibrary, CancellationToken, Task> deliver)
        => libraryChangeDelivery = deliver;

    private void DetailEntryCommitsAvailable(object sender, EventArgs args)
    {
        if (ReferenceEquals(sender, detailEntryCommitStore)) { ApplyAvailableEntryCommits(detailEntryCommitStore); }
    }

    private void ApplyAvailableEntryCommits(BMSPlaylist store)
    {
        // キューを読む consumer は workspace 接続一つに限定し、構築中要求へも同じ結果を渡します。
        ImmutableArray<PlaylistEntryCommitReceipt> receipts = store.DrainPlaylistEntryCommits();
        if (receipts.IsEmpty) { return; }
        lock (detailEntryDeliveryLock)
        {
            PlaylistPropertyEditSession propertyEdit = Volatile.Read(ref summaryPropertyEditSession);
            bool refreshSummary = receipts.Any(receipt => RequiresSummaryDataRefresh(receipt, propertyEdit));
            detailEntryDelivery = DeliverEntryCommitsAsync(detailEntryDelivery, store, receipts, refreshSummary);
            detailEntryDelivery.ObserveFault("playlist_entry_change_delivery");
        }
    }

    private Task WaitForDetailEntryDeliveryAsync()
    {
        lock (detailEntryDeliveryLock) { return detailEntryDelivery; }
    }

    private async Task DeliverEntryCommitsAsync(Task previous, BMSPlaylist store, ImmutableArray<PlaylistEntryCommitReceipt> receipts, bool refreshSummary)
    {
        // 単一consumerの配送順を保ち、前回操作の失敗を次の明示編集へ持ち越しません。
        try { await previous.ConfigureAwait(false); }
        catch { /* 元Taskの所有者が失敗を観測します。後続差分の配送は続けます。 */ }
        await playlistRestoreUiApplyScheduler(() =>
        {
            PreserveDetailSelection(() =>
            {
                if (!ReferenceEquals(store, detailEntryCommitStore)) { return false; }
                lock (DetailBuildState.SyncRoot)
                {
                    foreach (PlaylistEntryCommitReceipt receipt in receipts)
                    {
                        BufferEntryCommit(DetailBuildState.CurrentBuildRequest, receipt);
                        BufferEntryCommit(DetailBuildState.PendingRequest, receipt);
                        lock (DetailViewState.SyncRoot)
                        {
                            if (receipt.Revision > DetailViewState.Source.LastBuiltPlaylistRevision
                                && (ReferenceEquals(DetailViewState.Source.CurrentTable, receipt.Table)
                                    || DetailViewState.Source.Rows.Count > 0 && ReferenceEquals(DetailViewState.Source.Rows[0].Entry.parent, receipt.Table)))
                            {
                                PlaylistDetailDependencies dependencies = EnsureDetailDependencies();
                                if (TryApplyEditedEntryValues(dependencies, receipt))
                                {
                                    if (deferredEntryCommits.Any(value => ReferenceEquals(value.Table, receipt.Table)))
                                    { deferredEntryCommits.Add(receipt); }
                                    else { DetailViewState.Source.LastBuiltPlaylistRevision = receipt.Revision; }
                                    UpdateDetailIdentities();
                                    ReportDetailWork("playlist_entry_commit_applied");
                                }
                                else if (dependencies.PendingReferenceProjections.Count > 0
                                    || deferredEntryCommits.Any(value => ReferenceEquals(value.Table, receipt.Table))
                                    || !TryApplyEntryCommit(EnsureDetailDependencies(), receipt, DetailViewState.View.Rows as PlaylistDetailVirtualView,
                                    DetailViewState.View.CurrentIdentity, DetailViewState.Source.LastBuiltLibraryIndexVersion))
                                { deferredEntryCommits.Add(receipt); }
                                else
                                {
                                    DetailViewState.Source.LastBuiltPlaylistRevision = receipt.Revision;
                                    UpdateDetailIdentities();
                                    ReportDetailWork("playlist_entry_commit_applied");
                                }
                            }
                        }
                    }
                }
                PublishDetailChanges();
                if (refreshSummary) { RequestPlaylistSummaryDataRefresh("playlist_entry_lookup_changed"); }
                return true;
            });
        }).ConfigureAwait(false);
    }

    // Pで他の正本変更を除外した自分のprefix保存だけは、件数に依存しないfolder差分を局所反映します。
    // 開始版も照合して古い未drain差分を巻き込まず、判定をawait前に固定して保存scope後の配送でも保ちます。
    private static bool RequiresSummaryDataRefresh(PlaylistEntryCommitReceipt receipt, PlaylistPropertyEditSession propertyEdit)
    {
        bool countChanged = receipt.Changes.Any(change => change.Before == null || change.After == null
            || change.Before.IsRemoved != change.After.IsRemoved
            || !CatalogReadSnapshot.LookupKeyComparer.Equals(LookupKey(change.Before), LookupKey(change.After)));
        if (countChanged) { return true; }
        bool folderChanged = !receipt.FolderOrder.IsDefault
            || receipt.Changes.Any(change => change.Before.Folder != change.After.Folder);
        if (!folderChanged) { return false; }
        bool ownPrefixEdit = propertyEdit != null
            && ReferenceEquals(propertyEdit.Table, receipt.Table)
            && propertyEdit.OriginalValues.EntriesRevision == receipt.BaseRevision
            && !string.Equals(propertyEdit.OriginalValues.CompatPrefix, propertyEdit.Values.CompatPrefix, StringComparison.Ordinal);
        return !ownPrefixEdit;
    }

    private bool TryApplyEditedEntryValues(PlaylistDetailDependencies dependencies, PlaylistEntryCommitReceipt receipt)
    {
        if (!receipt.FolderOrder.IsDefault || receipt.Changes.Any(change => change.Before == null || change.After == null
            || change.Before.Folder != change.After.Folder || change.Before.IsRemoved != change.After.IsRemoved
            || !CatalogReadSnapshot.LookupKeyComparer.Equals(LookupKey(change.Before), LookupKey(change.After))
            || !dependencies.TryGet(change.Entry, out _))) { return false; }
        foreach (PlaylistEntryChange change in receipt.Changes)
        {
            dependencies.TryGet(change.Entry, out PlaylistDetailSourceRow before);
            ReportDetailWork("playlist_source_projection");
            ReplaceDetailProjection(dependencies, before, before.WithEditedValues(change.After));
        }
        return true;
    }

    private static void BufferEntryCommit(PlaylistBuildRequest request, PlaylistEntryCommitReceipt receipt)
    {
        if (request == null || !ReferenceEquals(request.Identity.Table, receipt.Table)) { return; }
        request.EntryCommits.Add(receipt);
        request.SignalDataChanged();
    }

    /// <summary>通常単一drainのcatalog・詳細・scoreの同一batchを既存sourceと構築中要求へ渡し、score位置は連続rangeだけ進めます。</summary>
    internal void ApplyLibraryChanges(BMSLibrary library, NormalLibraryRefreshNotificationBatch batch)
        => PreserveDetailSelection(() => { ApplyLibraryChangesCore(library, batch); return true; });

    private void ApplyLibraryChangesCore(BMSLibrary library, NormalLibraryRefreshNotificationBatch batch)
    {
        if (library == null || batch == null || detailDataSource is not PlaylistDetailDataSource attached
            || !ReferenceEquals(attached.Library, library)) { return; }
        bool rebuild = false;
        lock (DetailBuildState.SyncRoot)
        {
            foreach (PlaylistBuildRequest request in new[] { DetailBuildState.CurrentBuildRequest, DetailBuildState.PendingRequest })
            {
                if (request == null) { continue; }
                if (request.InputDependencies != null
                    && batch.CatalogCommits.Any(receipt => receipt.IsFullReplacement && receipt.Revision > request.Identity.LibraryIndexVersion))
                { rebuild = true; }
                request.CatalogCommits.AddRange(batch.CatalogCommits);
                request.ScoreChanges.AddRange(batch.ScoreChanges);
                request.ReferenceCommits.AddRange(batch.ReferenceCommits);
                request.DetailMd5Keys.UnionWith(batch.ChangedDetailMd5s);
                request.DetailSha256Keys.UnionWith(batch.ChangedDetailSha256s);
                request.HasChartInfoChanges |= batch.ChangedDetailMd5s.Count > 0 || batch.ChangedDetailSha256s.Count > 0;
                request.SignalDataChanged();
            }
            lock (DetailViewState.SyncRoot)
            {
                int detailVersion = detailDataSource.ChartInfoIndexVersion;
                PlaylistDetailSourceSnapshotState state = DetailViewState.Source;
                if (state.CurrentIdentity.HasValue)
                {
                    PlaylistDetailDependencies dependencies = EnsureDetailDependencies();
                    dependencies.ReferenceCommits.AddRange(batch.ReferenceCommits);
                    dependencies.PendingScores.AddRange(batch.ScoreChanges);
                    dependencies.PendingDetailMd5s.UnionWith(batch.ChangedDetailMd5s);
                    dependencies.PendingDetailSha256s.UnionWith(batch.ChangedDetailSha256s);
                    deferredCatalogCommits.AddRange(batch.CatalogCommits);
                    bool pendingReady = ResumePendingDetailProjection(dependencies,
                        DetailViewState.View.Rows as PlaylistDetailVirtualView, DetailViewState.View.CurrentIdentity);
                    foreach (CatalogCommitReceipt receipt in deferredCatalogCommits)
                    {
                        if (!pendingReady) { break; }
                        if (receipt.Revision <= state.LastBuiltLibraryIndexVersion) { continue; }
                        if (receipt.IsFullReplacement)
                        {
                            state.CurrentIdentity = null;
                            rebuild = true;
                            break;
                        }
                        if (!ApplyCatalogCommit(dependencies, receipt, DetailViewState.View.Rows as PlaylistDetailVirtualView,
                            DetailViewState.View.CurrentIdentity)) { break; }
                        state.LastBuiltLibraryIndexVersion = receipt.Revision;
                    }
                    deferredCatalogCommits.RemoveAll(receipt => receipt.Revision <= state.LastBuiltLibraryIndexVersion);
                    if (state.CurrentIdentity.HasValue)
                    {
                        for (int index = 0; pendingReady && deferredCatalogCommits.Count == 0 && index < deferredEntryCommits.Count;)
                        {
                            PlaylistEntryCommitReceipt receipt = deferredEntryCommits[index];
                            if (!ReferenceEquals(receipt.Table, state.CurrentTable) || receipt.Revision <= state.LastBuiltPlaylistRevision)
                            { deferredEntryCommits.RemoveAt(index); continue; }
                            if (!TryApplyEntryCommit(dependencies, receipt, DetailViewState.View.Rows as PlaylistDetailVirtualView,
                                DetailViewState.View.CurrentIdentity, state.LastBuiltLibraryIndexVersion)) { break; }
                            state.LastBuiltPlaylistRevision = receipt.Revision;
                            deferredEntryCommits.RemoveAt(index);
                        }
                        if (dependencies.PendingReferenceProjections.Count == 0)
                        {
                            state.LastBuiltScoreSnapshotVersion = ApplyScoreChanges(dependencies, dependencies.PendingScores,
                                state.LastBuiltScoreSnapshotVersion, DetailViewState.View.Rows as PlaylistDetailVirtualView,
                                DetailViewState.View.CurrentIdentity);
                            dependencies.PendingScores.RemoveAll(change => change.Version <= state.LastBuiltScoreSnapshotVersion);
                        }
                        if (TryPatchPendingDetails(dependencies, DetailViewState.View.Rows as PlaylistDetailVirtualView,
                            DetailViewState.View.CurrentIdentity))
                        { state.LastBuiltChartInfoIndexVersion = Math.Max(state.LastBuiltChartInfoIndexVersion, detailVersion); }
                        if (dependencies.PendingReferenceProjections.Count == 0)
                        { ApplyReferenceCommits(dependencies, DetailViewState.View.Rows as PlaylistDetailVirtualView, DetailViewState.View.CurrentIdentity); }
                        UpdateDetailIdentities();
                    }
                }
            }
        }
        PublishDetailChanges();
        bool possibleHashChange = batch.CatalogCommits.Any(receipt => receipt.IsFullReplacement || receipt.Changes.Any(change =>
            !StringComparer.OrdinalIgnoreCase.Equals(change.Before?.Md5, change.After?.Md5)
            || !StringComparer.OrdinalIgnoreCase.Equals(change.Before?.Sha256, change.After?.Sha256)));
        // 取得済みsummaryがある場合、hash変更後のcoldは「同じ内容版」ではありません。
        // 既存入力だけを失効し、判定のためにcold索引を構築しません。Title/healthだけの変更は上で除外します。
        if (possibleHashChange && summaryOwnedHashContentVersion.HasValue
            && (!library.TryGetOwnedHashContentVersion(out int version) || summaryOwnedHashContentVersion.Value != version))
        { RequestPlaylistSummaryDataRefresh("playlist_owned_hash_contents_changed"); }
        if (rebuild && IsPlaylistDetailViewActive) { RequestPlaylistDetailReloadRefresh(); }
    }

    private bool ApplyCatalogCommit(PlaylistDetailDependencies dependencies, CatalogCommitReceipt receipt,
        PlaylistDetailVirtualView view, PlaylistRequestIdentity? identity)
    {
        if (dependencies.PendingReferenceProjections.TryGetValue(receipt, out PlaylistPendingReferenceProjection pending))
        { return TryCommitReferenceProjection(dependencies, receipt, pending, view, identity); }
        var entries = new HashSet<BMSTableEntry>();
        var candidateEntries = new HashSet<BMSTableEntry>();
        foreach (OwnedChartToken token in receipt.OrderChangedTokens)
        {
            var ordered = new HashSet<BMSTableEntry>();
            dependencies.AddTokenDependents(token, ordered);
            foreach (BMSTableEntry entry in ordered)
            { if (dependencies.TryGet(entry, out PlaylistDetailSourceRow row)) { dependencies.AddLookupDependents(row.LookupKey, candidateEntries); } }
        }
        foreach (CatalogChartChange change in receipt.Changes)
        {
            dependencies.AddTokenDependents(change.Token, entries);
            if (!change.ChangesCandidate) { continue; }
            foreach (ChartFile chart in new[] { change.Before, change.After })
            {
                if (chart == null) { continue; }
                dependencies.AddLookupDependents(new(PlaylistEntryLookupKeyKind.Md5, chart.Md5), candidateEntries);
                dependencies.AddLookupDependents(new(PlaylistEntryLookupKeyKind.Sha256, chart.Sha256), candidateEntries);
            }
        }
        entries.UnionWith(candidateEntries);
        CatalogReadSnapshot candidates = null;
        if (candidateEntries.Count > 0 && (receipt.Snapshot == null || !receipt.Snapshot.TryCapturePlaylistReadSnapshot(
            candidateEntries.Select(entry => dependencies.TryGet(entry, out PlaylistDetailSourceRow row) ? row.LookupKey : default), out candidates)))
        { return false; }
        var projection = new PlaylistPendingReferenceProjection();
        dependencies.PendingReferenceProjections.Add(receipt, projection);
        BMSLibrary.ScoreSnapshot scores = entries.Count == 0 ? null : detailDataSource.GetScoreSnapshot();
        foreach (BMSTableEntry entry in entries)
        {
            if (!dependencies.TryGet(entry, out PlaylistDetailSourceRow row)) { continue; }
            ChartFile current;
            if (candidateEntries.Contains(entry)) { current = candidates.Resolve(row.LookupKey); }
            else if (receipt.Snapshot?.TryGetChart(row.OwnedCurrent.Token, out current, out _) != true) { current = null; }
            PlaylistDetailSourceRow next = CreateDetailProjection(detailDataSource, entry, row.EntryValue, current,
                row.EntryOrder, scores, referenceInput: dependencies.ReferenceInput);
            projection.Changes.Add((row, next));
        }
        return TryCommitReferenceProjection(dependencies, receipt, projection, view, identity);
    }

    private bool TryApplyEntryCommit(PlaylistDetailDependencies dependencies, PlaylistEntryCommitReceipt receipt,
        PlaylistDetailVirtualView view, PlaylistRequestIdentity? identity, long catalogRevision)
    {
        if (dependencies.PendingReferenceProjections.TryGetValue(receipt, out PlaylistPendingReferenceProjection pending))
        { return TryCommitReferenceProjection(dependencies, receipt, pending, view, identity); }
        // 必要な新 lookup を一度に捕捉し、版を検査してから receipt 全体を適用します。
        // 後半の lookup が未配送の新版を必要としても、前半だけを先行表示しません。
        var keys = new HashSet<PlaylistEntryLookupKey>(CatalogReadSnapshot.LookupKeyComparer);
        foreach (PlaylistEntryChange change in receipt.Changes)
        {
            if (!IsDetailEntryInScope(change.After, identity?.SelectionScope ?? DetailViewState.Source.CurrentSelectionScope,
                identity?.FolderName ?? DetailViewState.Source.CurrentFolderName)) { continue; }
            if (!dependencies.TryGet(change.Entry, out PlaylistDetailSourceRow previous)
                || !CatalogReadSnapshot.LookupKeyComparer.Equals(previous.LookupKey, LookupKey(change.After)))
            { keys.Add(LookupKey(change.After)); }
        }
        CatalogReadSnapshot catalog = keys.Count == 0 ? null : detailDataSource.CapturePlaylistReadSnapshot(keys, CancellationToken.None);
        if (catalog != null && catalog.CatalogRevision > catalogRevision) { return false; }
        var projection = new PlaylistPendingReferenceProjection();
        dependencies.PendingReferenceProjections.Add(receipt, projection);
        BMSLibrary.ScoreSnapshot scores = detailDataSource.GetScoreSnapshot();
        foreach (PlaylistEntryChange change in receipt.Changes)
        {
            dependencies.TryGet(change.Entry, out PlaylistDetailSourceRow before);
            bool inScope = IsDetailEntryInScope(change.After, identity?.SelectionScope ?? DetailViewState.Source.CurrentSelectionScope,
                identity?.FolderName ?? DetailViewState.Source.CurrentFolderName);
            if (!inScope)
            {
                if (before != null) { projection.Changes.Add((before, null)); }
                continue;
            }
            ChartFile chart = before?.OwnedCurrent;
            if (before == null || !CatalogReadSnapshot.LookupKeyComparer.Equals(before.LookupKey, LookupKey(change.After)))
            {
                chart = catalog.Resolve(LookupKey(change.After));
            }
            PlaylistDetailSourceRow next = CreateDetailProjection(detailDataSource, change.Entry, change.After, chart,
                change.AfterOrder ?? before?.EntryOrder ?? 0, scores, referenceInput: dependencies.ReferenceInput);
            projection.Changes.Add((before, next));
        }
        return TryCommitReferenceProjection(dependencies, receipt, projection, view, identity);
    }

    private void ReplaceDetailProjection(PlaylistDetailDependencies dependencies, PlaylistDetailSourceRow before,
        PlaylistDetailSourceRow next, PlaylistDetailVirtualView view = null, PlaylistRequestIdentity? identity = null)
    {
        dependencies.Replace(next);
        if (view == null && ReferenceEquals(dependencies.Rows, DetailViewState.Source.Rows))
        { view = DetailViewState.View.Rows as PlaylistDetailVirtualView; identity ??= DetailViewState.View.CurrentIdentity; }
        if (identity.HasValue) { view?.ApplyChange(before, next, identity.Value, DetailViewState.Source.IsPlaylistCellEditing); }
    }

    private void PatchDetailKeys(PlaylistDetailDependencies dependencies, IEnumerable<string> md5s, IEnumerable<string> sha256s,
        PlaylistDetailVirtualView view, PlaylistRequestIdentity? identity)
    {
        var entries = new HashSet<BMSTableEntry>();
        dependencies.AddDetailDependents(md5s, sha256s, entries);
        if (entries.Count == 0) { return; }
        BMSLibrary.ScoreSnapshot scores = detailDataSource.GetScoreSnapshot();
        foreach (BMSTableEntry entry in entries)
        {
            if (!dependencies.TryGet(entry, out PlaylistDetailSourceRow row)) { continue; }
            DetailWorkObserver?.Invoke("playlist_detail_dependency_projection");
            // scoreだけの配送から、未配送の詳細による新SHA依存を先取りしません。
            ReportDetailWork("playlist_source_projection");
            BMSScore score = ResolveDetailScore(row, row.EntryChartInfo, scores);
            if (score != null) { (detailDataSource as PlaylistDetailDataSource)?.Library.ScoreSnapshotWorkObserver?.Invoke("score_projection_value_copied"); }
            PlaylistDetailSourceRow next = row.WithEntryChartInfoAndScore(row.EntryChartInfo, score);
            ReplaceDetailProjection(dependencies, row, next, view, identity);
        }
    }

    // lookupは可変scoreへの参照を共有するため、捕捉lookupの最新版ではなく配送rangeだけを覆います。
    private int ApplyScoreChanges(PlaylistDetailDependencies dependencies, IEnumerable<ScoreSnapshotChange> changes,
        int applied, PlaylistDetailVirtualView view, PlaylistRequestIdentity? identity)
    {
        foreach (ScoreSnapshotChange change in changes)
        {
            if (change.Version <= applied) { continue; }
            if (change.BaseVersion > applied) { break; }
            PatchDetailKeys(dependencies, change.Md5Keys, change.Sha256Keys, view, identity);
            applied = change.Version;
        }
        return applied;
    }

    private void UpdateDetailIdentities()
    {
        PlaylistDetailSourceSnapshotState state = DetailViewState.Source;
        if (!state.CurrentIdentity.HasValue || DetailViewState.View.CurrentIdentity is not PlaylistRequestIdentity identity) { return; }
        identity = WithDetailVersions(identity, state.LastBuiltLibraryIndexVersion, state.LastBuiltPlaylistRevision,
            state.LastBuiltScoreSnapshotVersion, state.LastBuiltChartInfoIndexVersion);
        state.CurrentIdentity = identity.SourceIdentity;
        DetailViewState.View.CurrentIdentity = identity;
    }

    private static PlaylistRequestIdentity WithDetailVersions(PlaylistRequestIdentity identity, long catalog, long entries, int score, int detail)
        => new(identity.Table, identity.SelectionScope, identity.FolderName, identity.FilterType,
            identity.KeywordFilter, identity.ModeFilter, identity.SortColumnName, identity.SortDirection,
            catalog, entries, score, detail, identity.HasResolvedSelection);

    /// <summary>捕捉後に配送された差分だけを候補へ反映します。未配送の新版は signal 待機へ渡します。</summary>
    private bool TryPrepareDetailCandidate(PlaylistBuildRequest request, List<PlaylistDetailSourceRow> rows,
        PlaylistDetailVirtualView view, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        PlaylistDetailDependencies dependencies = request.InputDependencies;
        if (dependencies == null)
        {
            dependencies = rows == null ? EnsureDetailDependencies() : new(rows, ReportDetailWork);
            request.InputDependencies = dependencies;
        }
        dependencies.ReferenceCommits.AddRange(request.ReferenceCommits);
        dependencies.ReferenceCommits.Sort((left, right) => left.Revision.CompareTo(right.Revision));
        request.ReferenceCommits.Clear();
        if (!ResumePendingDetailProjection(dependencies, view, request.Identity, request))
        { request.WaitingForData = true; return false; }
        long catalogRevision = request.Identity.LibraryIndexVersion;
        long entryRevision = request.Identity.PlaylistRevision;
        int scoreVersion = ApplyScoreChanges(dependencies, request.ScoreChanges, request.Identity.ScoreSnapshotVersion, view, request.Identity);
        request.ScoreChanges.RemoveAll(change => change.Version <= scoreVersion);
        request.Identity = WithDetailVersions(request.Identity, catalogRevision, entryRevision,
            scoreVersion, request.Identity.ChartInfoIndexVersion);
        foreach (CatalogCommitReceipt receipt in request.CatalogCommits)
        {
            if (receipt.Revision <= catalogRevision) { continue; }
            if (receipt.BaseRevision > catalogRevision || receipt.IsFullReplacement
                || !ApplyCatalogCommit(dependencies, receipt, view, request.Identity))
            { request.WaitingForData = true; return false; }
            catalogRevision = receipt.Revision;
            request.Identity = WithDetailVersions(request.Identity, catalogRevision, entryRevision,
                request.Identity.ScoreSnapshotVersion, request.Identity.ChartInfoIndexVersion);
        }
        foreach (PlaylistEntryCommitReceipt receipt in request.EntryCommits)
        {
            if (receipt.Revision <= entryRevision) { continue; }
            if (receipt.BaseRevision > entryRevision
                || !TryApplyEntryCommit(dependencies, receipt, view, request.Identity, catalogRevision))
            { request.WaitingForData = true; return false; }
            entryRevision = receipt.Revision;
            request.Identity = WithDetailVersions(request.Identity, catalogRevision, entryRevision,
                request.Identity.ScoreSnapshotVersion, request.Identity.ChartInfoIndexVersion);
        }
        int detailVersion = request.Identity.ChartInfoIndexVersion;
        if (request.DetailMd5Keys.Count > 0 || request.DetailSha256Keys.Count > 0)
        {
            if (request.HasChartInfoChanges) { detailVersion = Math.Max(detailVersion, detailDataSource.ChartInfoIndexVersion); }
            request.HasChartInfoChanges = false;
            dependencies.PendingDetailMd5s.UnionWith(request.DetailMd5Keys);
            dependencies.PendingDetailSha256s.UnionWith(request.DetailSha256Keys);
            request.DetailMd5Keys.Clear(); request.DetailSha256Keys.Clear();
        }
        if (!TryPatchPendingDetails(dependencies, view, request.Identity)) { request.WaitingForData = true; return false; }
        request.CatalogCommits.RemoveAll(receipt => receipt.Revision <= catalogRevision);
        request.EntryCommits.RemoveAll(receipt => receipt.Revision <= entryRevision);
        request.Identity = WithDetailVersions(request.Identity, catalogRevision, entryRevision, scoreVersion, detailVersion);
        if (dependencies.PendingReferenceProjections.Count == 0) { ApplyReferenceCommits(dependencies, view, request.Identity); }
        request.ReferenceInput = dependencies.ReferenceInput;
        request.RequiredReferenceRevision = dependencies.ReferenceInput?.Library?.CapturePlaylistReferenceReadSnapshot([]).Revision ?? 0;
        request.WaitingForData = !HasRequiredDetailCandidateVersions(request, request.RequiredReferenceRevision);
        return !request.WaitingForData;
    }

    /// <summary>投影や索引準備をせず、候補の実反映位置が既存正本の必要版を覆うかだけを判定します。</summary>
    private bool HasRequiredDetailCandidateVersions(PlaylistBuildRequest request, long requiredReferenceRevision)
        => detailDataSource.CatalogRevision <= request.Identity.LibraryIndexVersion
            && (request.Identity.Table?.PlaylistEntriesRevision ?? 0) <= request.Identity.PlaylistRevision
            && detailDataSource.ScoreSnapshotVersion <= request.Identity.ScoreSnapshotVersion
            && (request.InputDependencies?.ReferenceInput?.Revision ?? 0) >= requiredReferenceRevision;
}
