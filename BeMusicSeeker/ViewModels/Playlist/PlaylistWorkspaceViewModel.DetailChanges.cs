using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using BeMusicSeeker.Models;
using BeMusicSeeker.Models.BmsLibraryInternal;

namespace BeMusicSeeker.ViewModels;

public sealed partial class PlaylistWorkspaceViewModel
{
    private BMSPlaylist detailEntryCommitStore;
    private Func<BMSLibrary, CancellationToken, Task> libraryChangeDelivery;
    private readonly List<PlaylistEntryCommitReceipt> deferredEntryCommits = [];
    private int? summaryOwnedHashContentVersion;

    /// <summary>初回構築と局所更新の実投影地点を観測します。通常実行では未設定です。</summary>
    internal Action<string> DetailWorkObserver { get; set; }

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
        dispatchPresentation(() =>
        {
            if (!ReferenceEquals(store, detailEntryCommitStore)) { return; }
            bool summaryChanged = false;
            bool rebuild = false;
            lock (DetailBuildState.SyncRoot)
            {
                foreach (PlaylistEntryCommitReceipt receipt in receipts)
                {
                    summaryChanged |= receipt.IsFullReplacement || !receipt.FolderOrder.IsDefault
                        || receipt.Changes.Any(change => change.Before == null || change.After == null
                            || change.Before.Folder != change.After.Folder || change.Before.IsRemoved != change.After.IsRemoved
                            || !CatalogReadSnapshot.LookupKeyComparer.Equals(LookupKey(change.Before), LookupKey(change.After)));
                    BufferEntryCommit(DetailBuildState.CurrentBuildRequest, receipt);
                    BufferEntryCommit(DetailBuildState.PendingRequest, receipt);
                    if (receipt.IsFullReplacement)
                    {
                        // 別表の置換は現在の source を無効化しません。構築要求は自分の表だけを受け取ります。
                        foreach (PlaylistBuildRequest request in new[] { DetailBuildState.CurrentBuildRequest, DetailBuildState.PendingRequest })
                        {
                            if (request?.InputDependencies != null && ReferenceEquals(request.Identity.Table, receipt.Table)
                                && receipt.Revision > request.Identity.PlaylistRevision) { rebuild = true; }
                        }
                        lock (DetailViewState.SyncRoot)
                        {
                            if (ReferenceEquals(DetailViewState.Source.CurrentTable, receipt.Table)
                                && receipt.Revision > DetailViewState.Source.LastBuiltPlaylistRevision)
                            { DetailViewState.Source.CurrentIdentity = null; rebuild = true; }
                        }
                        continue;
                    }
                    lock (DetailViewState.SyncRoot)
                    {
                        if (receipt.Revision > DetailViewState.Source.LastBuiltPlaylistRevision
                            && (ReferenceEquals(DetailViewState.Source.CurrentTable, receipt.Table)
                                || DetailViewState.Source.Rows.Count > 0 && ReferenceEquals(DetailViewState.Source.Rows[0].Entry.parent, receipt.Table)))
                        {
                            if (deferredEntryCommits.Any(value => ReferenceEquals(value.Table, receipt.Table))
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
            (DetailViewState.View.Rows as PlaylistDetailVirtualView)?.PublishChanges();
            if (summaryChanged) { RequestPlaylistSummaryDataRefresh("playlist_entry_lookup_changed"); }
            if (rebuild && IsPlaylistDetailViewActive) { RequestPlaylistDetailReloadRefresh(); }
        });
    }

    private static void BufferEntryCommit(PlaylistBuildRequest request, PlaylistEntryCommitReceipt receipt)
    {
        if (request == null || !ReferenceEquals(request.Identity.Table, receipt.Table)) { return; }
        request.EntryCommits.Add(receipt);
        request.SignalDataChanged();
    }

    /// <summary>通常単一drainのcatalog・詳細・scoreの同一batchを既存sourceと構築中要求へ渡し、score位置は連続rangeだけ進めます。</summary>
    internal void ApplyLibraryChanges(BMSLibrary library, NormalLibraryRefreshNotificationBatch batch)
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
                    foreach (CatalogCommitReceipt receipt in batch.CatalogCommits)
                    {
                        if (receipt.Revision <= state.LastBuiltLibraryIndexVersion) { continue; }
                        if (receipt.IsFullReplacement || !ApplyCatalogCommit(dependencies, receipt,
                            DetailViewState.View.Rows as PlaylistDetailVirtualView, DetailViewState.View.CurrentIdentity))
                        {
                            state.CurrentIdentity = null;
                            rebuild = true;
                            break;
                        }
                        state.LastBuiltLibraryIndexVersion = receipt.Revision;
                    }
                    if (state.CurrentIdentity.HasValue)
                    {
                        state.LastBuiltScoreSnapshotVersion = ApplyScoreChanges(dependencies, batch.ScoreChanges,
                            state.LastBuiltScoreSnapshotVersion, DetailViewState.View.Rows as PlaylistDetailVirtualView,
                            DetailViewState.View.CurrentIdentity);
                        PatchDetailKeys(dependencies, batch.ChangedDetailMd5s, batch.ChangedDetailSha256s,
                            DetailViewState.View.Rows as PlaylistDetailVirtualView, DetailViewState.View.CurrentIdentity);
                        for (int index = 0; index < deferredEntryCommits.Count;)
                        {
                            PlaylistEntryCommitReceipt receipt = deferredEntryCommits[index];
                            if (!ReferenceEquals(receipt.Table, state.CurrentTable) || receipt.Revision <= state.LastBuiltPlaylistRevision)
                            { deferredEntryCommits.RemoveAt(index); continue; }
                            if (!TryApplyEntryCommit(dependencies, receipt, DetailViewState.View.Rows as PlaylistDetailVirtualView,
                                DetailViewState.View.CurrentIdentity, state.LastBuiltLibraryIndexVersion)) { break; }
                            state.LastBuiltPlaylistRevision = receipt.Revision;
                            deferredEntryCommits.RemoveAt(index);
                        }
                        if (batch.ChangedDetailMd5s.Count > 0 || batch.ChangedDetailSha256s.Count > 0)
                        { state.LastBuiltChartInfoIndexVersion = Math.Max(state.LastBuiltChartInfoIndexVersion, detailVersion); }
                        UpdateDetailIdentities();
                    }
                }
            }
        }
        (DetailViewState.View.Rows as PlaylistDetailVirtualView)?.PublishChanges();
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
        var entries = new HashSet<BMSTableEntry>();
        var candidateEntries = new HashSet<BMSTableEntry>();
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
        BMSLibrary.ScoreSnapshot scores = entries.Count == 0 ? null : detailDataSource.GetScoreSnapshot();
        foreach (BMSTableEntry entry in entries)
        {
            if (!dependencies.TryGet(entry, out PlaylistDetailSourceRow row)) { continue; }
            ChartFile current;
            if (candidateEntries.Contains(entry)) { current = candidates.Resolve(row.LookupKey); }
            else if (receipt.Snapshot?.TryGetChart(row.OwnedCurrent.Token, out current, out _) != true) { current = null; }
            PlaylistDetailSourceRow next = CreateDetailProjection(detailDataSource, entry, row.EntryValue, current,
                row.EntryOrder, scores);
            ReplaceDetailProjection(dependencies, row, next, view, identity);
        }
        return true;
    }

    private bool TryApplyEntryCommit(PlaylistDetailDependencies dependencies, PlaylistEntryCommitReceipt receipt,
        PlaylistDetailVirtualView view, PlaylistRequestIdentity? identity, long catalogRevision)
    {
        if (receipt.IsFullReplacement) { return false; }
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
        BMSLibrary.ScoreSnapshot scores = detailDataSource.GetScoreSnapshot();
        foreach (PlaylistEntryChange change in receipt.Changes)
        {
            dependencies.TryGet(change.Entry, out PlaylistDetailSourceRow before);
            bool inScope = IsDetailEntryInScope(change.After, identity?.SelectionScope ?? DetailViewState.Source.CurrentSelectionScope,
                identity?.FolderName ?? DetailViewState.Source.CurrentFolderName);
            if (!inScope)
            {
                if (before != null) { dependencies.Remove(change.Entry); if (identity.HasValue) { view?.ApplyChange(before, null, identity.Value, DetailViewState.Source.IsPlaylistCellEditing); } }
                continue;
            }
            ChartFile chart = before?.OwnedCurrent;
            if (before == null || !CatalogReadSnapshot.LookupKeyComparer.Equals(before.LookupKey, LookupKey(change.After)))
            {
                chart = catalog.Resolve(LookupKey(change.After));
            }
            PlaylistDetailSourceRow next = CreateDetailProjection(detailDataSource, change.Entry, change.After, chart,
                change.AfterOrder ?? before?.EntryOrder ?? 0, scores);
            ReplaceDetailProjection(dependencies, before, next, view, identity);
        }
        return true;
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
            PlaylistDetailSourceRow next = CreateDetailProjection(detailDataSource, entry, row.EntryValue, row.OwnedCurrent,
                row.EntryOrder, scores);
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
            PatchDetailKeys(dependencies, request.DetailMd5Keys, request.DetailSha256Keys, view, request.Identity);
            request.DetailMd5Keys.Clear(); request.DetailSha256Keys.Clear();
        }
        request.CatalogCommits.RemoveAll(receipt => receipt.Revision <= catalogRevision);
        request.EntryCommits.RemoveAll(receipt => receipt.Revision <= entryRevision);
        request.Identity = WithDetailVersions(request.Identity, catalogRevision, entryRevision, scoreVersion, detailVersion);
        request.WaitingForData = detailDataSource.CatalogRevision > catalogRevision
            || (request.Identity.Table?.PlaylistEntriesRevision ?? 0) > entryRevision
            || detailDataSource.ScoreSnapshotVersion > scoreVersion;
        return !request.WaitingForData;
    }
}
