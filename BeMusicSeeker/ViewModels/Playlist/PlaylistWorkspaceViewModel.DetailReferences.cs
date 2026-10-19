using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.ExceptionServices;
using BeMusicSeeker.Models;
using BeMusicSeeker.Models.BmsLibraryInternal;

namespace BeMusicSeeker.ViewModels;

public sealed partial class PlaylistWorkspaceViewModel
{
    private readonly List<CatalogCommitReceipt> deferredCatalogCommits = [];

    private bool ResumePendingDetailProjection(PlaylistDetailDependencies dependencies, PlaylistDetailVirtualView view,
        PlaylistRequestIdentity? identity, PlaylistBuildRequest request = null)
    {
        if (dependencies.PendingReferenceProjections.Count == 0) { return true; }
        KeyValuePair<object, PlaylistPendingReferenceProjection> pending = dependencies.PendingReferenceProjections.Single();
        if (!TryCommitReferenceProjection(dependencies, pending.Key, pending.Value, view, identity)) { return false; }
        if (request != null)
        {
            request.Identity = WithDetailVersions(request.Identity,
                pending.Key is CatalogCommitReceipt catalog ? Math.Max(catalog.Revision, request.Identity.LibraryIndexVersion) : request.Identity.LibraryIndexVersion,
                pending.Key is PlaylistEntryCommitReceipt entries ? Math.Max(entries.Revision, request.Identity.PlaylistRevision) : request.Identity.PlaylistRevision,
                request.Identity.ScoreSnapshotVersion, Math.Max(pending.Value.ChartInfoVersion ?? 0, request.Identity.ChartInfoIndexVersion));
        }
        else
        {
            if (pending.Key is CatalogCommitReceipt catalog) { DetailViewState.Source.LastBuiltLibraryIndexVersion = catalog.Revision; }
            if (pending.Key is PlaylistEntryCommitReceipt entries) { DetailViewState.Source.LastBuiltPlaylistRevision = entries.Revision; }
            if (pending.Value.ChartInfoVersion.HasValue)
            { DetailViewState.Source.LastBuiltChartInfoIndexVersion = Math.Max(DetailViewState.Source.LastBuiltChartInfoIndexVersion, pending.Value.ChartInfoVersion.Value); }
        }
        return true;
    }

    private void ApplyReferenceCommits(PlaylistDetailDependencies dependencies, PlaylistDetailVirtualView view,
        PlaylistRequestIdentity? identity, long boundary = long.MaxValue)
    {
        ChartListReferenceInput input = dependencies.ReferenceInput;
        if (input == null) { return; }
        foreach (PlaylistReferenceCommitReceipt receipt in dependencies.ReferenceCommits)
        {
            if (receipt.Revision <= input.Revision) { continue; }
            if (receipt.Revision > boundary || receipt.BaseRevision > input.Revision) { break; }
            input = input.Apply(receipt, dependencies.IncludesReferenceKey);
            dependencies.ReferenceInput = input;
            var affected = new HashSet<BMSTableEntry>();
            foreach (PlaylistEntryLookupKey key in receipt.Changes.Keys) { dependencies.AddReferenceDependents(key, affected); }
            foreach (BMSTableEntry entry in affected)
            {
                if (!dependencies.TryGet(entry, out PlaylistDetailSourceRow row)) { continue; }
                ReportDetailWork("playlist_reference_projection");
                ReplaceDetailProjection(dependencies, row, row.WithReferenceDisplay(input), view, identity);
            }
        }
        dependencies.ReferenceCommits.RemoveAll(receipt => receipt.Revision <= input.Revision);
    }

    /// <summary>必要な捕捉境界へ到達してから元投影を全件適用し、その最終依存で参照入力を整理して後続の参照確定へ進みます。</summary>
    private bool TryCommitReferenceProjection(PlaylistDetailDependencies dependencies, object receipt,
        PlaylistPendingReferenceProjection projection, PlaylistDetailVirtualView view, PlaylistRequestIdentity? identity)
    {
        ChartListReferenceInput input = dependencies.ReferenceInput;
        if (input != null)
        {
            if (projection.Keys == null)
            {
                projection.Keys = new(CatalogReadSnapshot.LookupKeyComparer);
                foreach ((PlaylistDetailSourceRow Before, PlaylistDetailSourceRow After) change in projection.Changes)
                {
                    if (change.After == null) { continue; }
                    foreach (PlaylistEntryLookupKey key in ReferenceKeys(change.After.EntryValue, change.After.Chart, change.After.sha256))
                    { if (!dependencies.IncludesReferenceKey(key)) { projection.Keys.Add(key); } }
                }
                if (projection.Keys.Count > 0 && input.Library != null)
                { projection.Snapshot = input.Library.CapturePlaylistReferenceReadSnapshot(projection.Keys); }
            }
            if (projection.Snapshot != null)
            {
                ApplyReferenceCommits(dependencies, view, identity, projection.Snapshot.Revision);
                input = dependencies.ReferenceInput;
                if (input.Revision < projection.Snapshot.Revision) { return false; }
                dependencies.ReferenceInput = input = input.Include(projection.Snapshot, projection.Keys);
            }
        }
        foreach ((PlaylistDetailSourceRow Before, PlaylistDetailSourceRow After) change in projection.Changes)
        {
            // 待機中に参照だけが更新された場合も、現在の行をbeforeに使って順序木を置換します。
            dependencies.TryGet(change.After?.Entry ?? change.Before.Entry, out PlaylistDetailSourceRow before);
            if (change.After == null)
            {
                if (before == null) { continue; }
                dependencies.Remove(before.Entry);
                if (identity.HasValue) { view?.ApplyChange(before, null, identity.Value, DetailViewState.Source.IsPlaylistCellEditing); }
            }
            else
            {
                PlaylistDetailSourceRow after = input == null ? change.After : change.After.WithReferenceDisplay(input);
                ReplaceDetailProjection(dependencies, before, after, view, identity);
            }
        }
        // 行ごとの途中状態では、同じ投影内で別entryへ引き継ぐhashまで解放してしまう。
        dependencies.PruneReferenceInput(projection.Changes.Select(change => change.Before));
        dependencies.PendingReferenceProjections.Remove(receipt);
        ApplyReferenceCommits(dependencies, view, identity);
        return true;
    }

    private bool TryPatchPendingDetails(PlaylistDetailDependencies dependencies, PlaylistDetailVirtualView view,
        PlaylistRequestIdentity? identity)
    {
        if (dependencies.PendingReferenceProjections.TryGetValue(dependencies.DetailProjectionKey, out PlaylistPendingReferenceProjection pending))
        {
            if (!TryCommitReferenceProjection(dependencies, dependencies.DetailProjectionKey, pending, view, identity)) { return false; }
        }
        if (dependencies.PendingReferenceProjections.Count > 0) { return false; }
        var entries = new HashSet<BMSTableEntry>();
        dependencies.AddDetailDependents(dependencies.PendingDetailMd5s, dependencies.PendingDetailSha256s, entries);
        dependencies.PendingDetailMd5s.Clear(); dependencies.PendingDetailSha256s.Clear();
        if (entries.Count == 0) { return true; }
        var projection = new PlaylistPendingReferenceProjection { ChartInfoVersion = detailDataSource.ChartInfoIndexVersion };
        BMSLibrary.ScoreSnapshot scores = detailDataSource.GetScoreSnapshot();
        foreach (BMSTableEntry entry in entries)
        {
            if (!dependencies.TryGet(entry, out PlaylistDetailSourceRow row)) { continue; }
            ReportDetailWork("playlist_detail_dependency_projection");
            projection.Changes.Add((row, CreateDetailProjection(detailDataSource, entry, row.EntryValue, row.OwnedCurrent,
                row.EntryOrder, scores, referenceInput: dependencies.ReferenceInput)));
        }
        dependencies.PendingReferenceProjections.Add(dependencies.DetailProjectionKey, projection);
        return TryCommitReferenceProjection(dependencies, dependencies.DetailProjectionKey, projection, view, identity);
    }

    /// <summary>採用中viewの差分通知と同じ終端で、既存メタデータから集計を公開します。</summary>
    private void PublishDetailChanges()
    {
        if (DetailViewState.View.Rows is not PlaylistDetailVirtualView view) { return; }
        ExceptionDispatchInfo firstFailure = null;
        try { view.PublishChanges(); }
        catch (Exception failure) { firstFailure = ExceptionDispatchInfo.Capture(failure); }
        try
        {
            if (ReferenceEquals(detailMainChartList?.Rows, view)) { detailMainChartList.UpdateSummaryText(view); }
        }
        catch (Exception failure)
        {
            if (firstFailure == null) { throw; }
            TryLogPlaylistDropSecondaryFailure(failure, "playlist_detail_summary_notification_failed");
        }
        firstFailure?.Throw();
    }
}
