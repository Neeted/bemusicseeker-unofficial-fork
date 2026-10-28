using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Threading;
using BeMusicSeeker.Models;
using BeMusicSeeker.Models.BmsLibraryInternal;

namespace BeMusicSeeker.ViewModels;

public sealed partial class PlaylistWorkspaceViewModel
{
    /// <summary>entry 値・版を一緒に捕捉し、必要候補と current の同時捕捉から初回入力を作ります。</summary>
    internal PlaylistSourceBuildResult BuildDetailSourceRows(
        BMSTable table, PlaylistDetailSelectionScope selectionScope, string folderName,
        CancellationToken cancellationToken, ref string cancellationStage)
    {
        IPlaylistDetailDataSource dataSource = Volatile.Read(ref detailDataSource)
            ?? throw new InvalidOperationException("Playlist detail data source is not attached.");
        var stopwatch = Stopwatch.StartNew();
        if (table == null) { return new([], 0, 0, 0, 0, new()); }
        dataSource.EnsureEntriesLoaded(table, "BuildDetailSourceRows");
        cancellationToken.ThrowIfCancellationRequested();
        PlaylistEntryReadSnapshot entries = table.CaptureEntriesReadSnapshot();
        List<PlaylistEntryChange> selected = [];
        foreach (PlaylistEntryChange entry in entries.Entries)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (IsDetailEntryInScope(entry.After, selectionScope, folderName)) { selected.Add(entry); }
        }
        cancellationStage = "entry_resolve";
        CatalogReadSnapshot catalog = dataSource.CapturePlaylistReadSnapshot(selected.Select(entry => LookupKey(entry.After)), cancellationToken);
        var references = new ChartListReferenceInput((dataSource as PlaylistDetailDataSource)?.Library);
        references.CaptureInitial(selected.SelectMany(entry =>
        {
            ChartFile current = catalog.Resolve(LookupKey(entry.After));
            ChartDetails detail = current == null ? dataSource.ResolveChartInfo(entry.After.Sha256, entry.After.Md5) : null;
            return ReferenceKeys(entry.After, current, detail?.sha256);
        }), cancellationToken);
        BMSLibrary.ScoreSnapshot scores = dataSource.GetScoreSnapshot();
        int scoreBaseline = scores?.Version ?? dataSource.ScoreSnapshotVersion;
        var rows = new List<PlaylistDetailSourceRow>(selected.Count);
        foreach (PlaylistEntryChange entry in selected)
        {
            cancellationToken.ThrowIfCancellationRequested();
            PlaylistEntryLookupKey key = LookupKey(entry.After);
            ChartFile chart = catalog.Resolve(key);
            catalog.ResolvedCandidates.TryGetValue(key, out LibraryChartRef reference);
            rows.Add(CreateDetailProjection(dataSource, entry.Entry, entry.After, chart, entry.AfterOrder ?? 0, scores, reference, references));
        }
        return new(rows, rows.Count, stopwatch.ElapsedMilliseconds, 0, 0, new() { TargetCount = rows.Count })
        {
            ReferenceInput = references,
            CatalogRevision = catalog.CatalogRevision,
            PlaylistRevision = entries.Revision,
            ScoreSnapshotVersion = scoreBaseline,
            ChartInfoIndexVersion = dataSource.ChartInfoIndexVersion
        };
    }

    private PlaylistDetailSourceRow CreateDetailProjection(IPlaylistDetailDataSource dataSource, BMSTableEntry entry,
        PlaylistEntryValue value, ChartFile current, long order, BMSLibrary.ScoreSnapshot scores, LibraryChartRef reference = null,
        ChartListReferenceInput referenceInput = null)
    {
        DetailWorkObserver?.Invoke("playlist_source_projection");
        BeMusicSeeker.Models.ChartDetails detail = current == null ? dataSource.ResolveChartInfo(value.Sha256, value.Md5) : null;
        BMSScore score = PlaylistEntryScoreSnapshotResolver.Resolve(value, current, detail, scores,
            scores?.ScoresByHash, scores?.ScoresBySha256, (dataSource as PlaylistDetailDataSource)?.Library.ScoreSnapshotWorkObserver);
        if (score != null) { (dataSource as PlaylistDetailDataSource)?.Library.ScoreSnapshotWorkObserver?.Invoke("score_projection_value_copied"); }
        return dataSource.CreateSourceRow(entry, current, score, detail, reference ?? LibraryChartRef.FromChartFile(current), value, order, referenceInput);
    }

    private static IEnumerable<PlaylistEntryLookupKey> ReferenceKeys(PlaylistEntryValue value, ChartFile current, string detailSha = null)
    {
        foreach (string hash in new[] { current?.Md5, value?.Md5 })
        { if (!string.IsNullOrWhiteSpace(hash)) { yield return new(PlaylistEntryLookupKeyKind.Md5, hash); } }
        string effectiveSha = !string.IsNullOrWhiteSpace(current?.Sha256) ? current.Sha256
            : !string.IsNullOrWhiteSpace(value?.Sha256) ? value.Sha256 : detailSha;
        foreach (string hash in new[] { effectiveSha, value?.Sha256 })
        { if (!string.IsNullOrWhiteSpace(hash)) { yield return new(PlaylistEntryLookupKeyKind.Sha256, hash); } }
    }

    private static PlaylistEntryLookupKey LookupKey(PlaylistEntryValue value)
        => value == null || value.IsRemoved ? default : !string.IsNullOrWhiteSpace(value.Md5)
            ? new(PlaylistEntryLookupKeyKind.Md5, value.Md5)
            : new(PlaylistEntryLookupKeyKind.Sha256, value.Sha256);

    private static bool IsDetailEntryInScope(PlaylistEntryValue value, PlaylistDetailSelectionScope scope, string folder)
        => value != null && !value.IsRemoved && value.Md5 != "00000000000000000000000000000000"
            && (folder == null || value.Folder == folder)
            && (scope != PlaylistDetailSelectionScope.OverallNormalFolders || value.Folder != "[NO SONG]");

    /// <summary>未所持詳細の重い読取りをworkerで準備し、現役sourceへの登録済み投影をUIの一処理で消費します。</summary>
    internal bool TryPatchDetailSourceChartInfo(PlaylistBuildRequest request, CancellationToken cancellationToken,
        out int sourceCount, out int dependencyCount, out int patchedCount, out long elapsedMs)
    {
        var watch = Stopwatch.StartNew();
        PlaylistDetailDependencies preparedDependencies;
        List<PlaylistDetailSourceRow> preparedRows;
        int chartInfoVersion = request.Identity.ChartInfoIndexVersion;
        lock (DetailBuildState.SyncRoot)
        {
            lock (DetailViewState.SyncRoot)
            {
                preparedDependencies = EnsureDetailDependencies();
                preparedRows = preparedDependencies.Rows;
                sourceCount = preparedRows.Count;
                dependencyCount = patchedCount = 0;
                if (request.RequestVersion != DetailBuildState.RequestVersion) { elapsedMs = watch.ElapsedMilliseconds; return false; }
                cancellationToken.ThrowIfCancellationRequested();
                if (preparedDependencies.PendingReferenceProjections.Count > 0) { elapsedMs = watch.ElapsedMilliseconds; return false; }
                BMSLibrary.ScoreSnapshot scores = detailDataSource.GetScoreSnapshot();
                var projection = new PlaylistPendingReferenceProjection { ChartInfoVersion = chartInfoVersion };
                // 明示再要求の未所持詳細は既存の依存集合だけを評価し、参照snapshotの準備もworkerに残します。
                foreach (PlaylistDetailSourceRow row in preparedDependencies.EntryChartInfoRows())
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    dependencyCount++;
                    BeMusicSeeker.Models.ChartDetails info = detailDataSource.ResolveChartInfo(row.sha256, row.hash);
                    if (AreSameChartInfoIdentity(row.EntryChartInfo, info)) { continue; }
                    PlaylistDetailSourceRow next = row.WithEntryChartInfoAndScore(info, ResolveDetailScore(row, info, scores));
                    projection.Changes.Add((row, next));
                    patchedCount++;
                }
                PrepareReferenceProjection(preparedDependencies, projection);
                cancellationToken.ThrowIfCancellationRequested();
                preparedDependencies.PendingReferenceProjections.Add(preparedDependencies.DetailProjectionKey, projection);
            }
        }
        bool applied = false;
        // 登録後の投影はsourceが所有します。要求の取消でUI配送を捨てず、現役sourceなら既存consumerへ渡します。
        playlistRestoreUiApplyScheduler(() => applied = PreserveDetailSelection(() =>
        {
            bool ready, currentRequest;
            lock (DetailBuildState.SyncRoot)
            {
                lock (DetailViewState.SyncRoot)
                {
                    PlaylistDetailSourceSnapshotState state = DetailViewState.Source;
                    if (!ReferenceEquals(state.Dependencies, preparedDependencies) || !ReferenceEquals(state.Rows, preparedRows)
                        || !state.CurrentIdentity.HasValue) { return false; }
                    ready = state.LastBuiltChartInfoIndexVersion >= chartInfoVersion
                        || ResumePendingDetailProjection(preparedDependencies, DetailViewState.View.Rows as PlaylistDetailVirtualView,
                            DetailViewState.View.CurrentIdentity);
                    if (ready) { UpdateDetailIdentities(); }
                    currentRequest = request.RequestVersion == DetailBuildState.RequestVersion && !cancellationToken.IsCancellationRequested;
                }
            }
            PublishDetailChanges();
            return ready && currentRequest;
        })).GetAwaiter().GetResult();
        elapsedMs = watch.ElapsedMilliseconds;
        cancellationToken.ThrowIfCancellationRequested();
        return applied;
    }

    private BMSScore ResolveDetailScore(PlaylistDetailSourceRow row, BeMusicSeeker.Models.ChartDetails info, BMSLibrary.ScoreSnapshot scores)
    {
        return PlaylistEntryScoreSnapshotResolver.Resolve(row.EntryValue, row.OwnedCurrent, info ?? row.EntryChartInfo, scores,
            scores?.ScoresByHash, scores?.ScoresBySha256,
            (detailDataSource as PlaylistDetailDataSource)?.Library.ScoreSnapshotWorkObserver);
    }

    private static bool AreSameChartInfoIdentity(BeMusicSeeker.Models.ChartDetails left, BeMusicSeeker.Models.ChartDetails right)
        => ReferenceEquals(left, right) || left != null && right != null && !string.IsNullOrWhiteSpace(left.sha256)
            && left.parser_version > 0 && right.parser_version > 0
            && StringComparer.OrdinalIgnoreCase.Equals(left.sha256, right.sha256)
            && left.parser_version == right.parser_version && left.updated_at == right.updated_at;
}
