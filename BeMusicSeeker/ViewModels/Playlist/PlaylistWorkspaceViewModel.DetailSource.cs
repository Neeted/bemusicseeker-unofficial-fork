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
        BMSLibrary.ScoreSnapshot scores = dataSource.GetScoreSnapshot();
        int scoreBaseline = scores?.Version ?? dataSource.ScoreSnapshotVersion;
        var rows = new List<PlaylistDetailSourceRow>(selected.Count);
        foreach (PlaylistEntryChange entry in selected)
        {
            cancellationToken.ThrowIfCancellationRequested();
            PlaylistEntryLookupKey key = LookupKey(entry.After);
            ChartFile chart = catalog.Resolve(key);
            catalog.ResolvedCandidates.TryGetValue(key, out LibraryChartRef reference);
            rows.Add(CreateDetailProjection(dataSource, entry.Entry, entry.After, chart, entry.AfterOrder ?? 0, scores, reference));
        }
        return new(rows, rows.Count, stopwatch.ElapsedMilliseconds, 0, 0, new() { TargetCount = rows.Count })
        {
            CatalogRevision = catalog.CatalogRevision,
            PlaylistRevision = entries.Revision,
            ScoreSnapshotVersion = scoreBaseline,
            ChartInfoIndexVersion = dataSource.ChartInfoIndexVersion
        };
    }

    private PlaylistDetailSourceRow CreateDetailProjection(IPlaylistDetailDataSource dataSource, BMSTableEntry entry,
        PlaylistEntryValue value, ChartFile current, long order, BMSLibrary.ScoreSnapshot scores, LibraryChartRef reference = null)
    {
        DetailWorkObserver?.Invoke("playlist_source_projection");
        BeMusicSeeker.Models.ChartDetails detail = current == null ? dataSource.ResolveChartInfo(value.Sha256, value.Md5) : null;
        BMSScore score = PlaylistEntryScoreSnapshotResolver.Resolve(value, current, detail, scores,
            scores?.ScoresByHash, scores?.ScoresBySha256, (dataSource as PlaylistDetailDataSource)?.Library.ScoreSnapshotWorkObserver);
        if (score != null) { (dataSource as PlaylistDetailDataSource)?.Library.ScoreSnapshotWorkObserver?.Invoke("score_projection_value_copied"); }
        return dataSource.CreateSourceRow(entry, current, score, detail, reference ?? LibraryChartRef.FromChartFile(current), value, order);
    }

    private static PlaylistEntryLookupKey LookupKey(PlaylistEntryValue value)
        => value == null || value.IsRemoved ? default : !string.IsNullOrWhiteSpace(value.Md5)
            ? new(PlaylistEntryLookupKeyKind.Md5, value.Md5)
            : new(PlaylistEntryLookupKeyKind.Sha256, value.Sha256);

    private static bool IsDetailEntryInScope(PlaylistEntryValue value, PlaylistDetailSelectionScope scope, string folder)
        => value != null && !value.IsRemoved && value.Md5 != "00000000000000000000000000000000"
            && (folder == null || value.Folder == folder)
            && (scope != PlaylistDetailSelectionScope.OverallNormalFolders || value.Folder != "[NO SONG]");

    /// <summary>旧呼出しも依存索引の未所持行だけを扱い、全件長 patch 配列を作りません。</summary>
    internal bool TryPatchDetailSourceChartInfo(PlaylistBuildRequest request, CancellationToken cancellationToken,
        out int sourceCount, out int dependencyCount, out int patchedCount, out long elapsedMs)
    {
        var watch = Stopwatch.StartNew();
        lock (DetailBuildState.SyncRoot)
        {
            lock (DetailViewState.SyncRoot)
            {
                PlaylistDetailDependencies dependencies = EnsureDetailDependencies();
                sourceCount = dependencies.Rows.Count;
                dependencyCount = patchedCount = 0;
                if (request.RequestVersion != DetailBuildState.RequestVersion) { elapsedMs = watch.ElapsedMilliseconds; return false; }
                BMSLibrary.ScoreSnapshot scores = detailDataSource.GetScoreSnapshot();
                // 正規通知では明示キーを使います。明示再要求の未所持詳細は既存の依存集合だけを評価します。
                foreach (PlaylistDetailSourceRow row in dependencies.EntryChartInfoRows())
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    dependencyCount++;
                    BeMusicSeeker.Models.ChartDetails info = detailDataSource.ResolveChartInfo(row.sha256, row.hash);
                    if (AreSameChartInfoIdentity(row.EntryChartInfo, info)) { continue; }
                    PlaylistDetailSourceRow next = row.WithEntryChartInfoAndScore(info, ResolveDetailScore(row, info, scores));
                    ReplaceDetailProjection(dependencies, row, next);
                    patchedCount++;
                }
                DetailViewState.Source.LastBuiltChartInfoIndexVersion = request.Identity.ChartInfoIndexVersion;
                DetailViewState.Source.CurrentIdentity = WithDetailVersions(request.Identity,
                    DetailViewState.Source.LastBuiltLibraryIndexVersion, DetailViewState.Source.LastBuiltPlaylistRevision,
                    DetailViewState.Source.LastBuiltScoreSnapshotVersion, DetailViewState.Source.LastBuiltChartInfoIndexVersion).SourceIdentity;
            }
        }
        elapsedMs = watch.ElapsedMilliseconds;
        (DetailViewState.View.Rows as PlaylistDetailVirtualView)?.PublishChanges();
        return true;
    }

    private static BMSScore ResolveDetailScore(PlaylistDetailSourceRow row, BeMusicSeeker.Models.ChartDetails info, BMSLibrary.ScoreSnapshot scores)
    {
        return PlaylistEntryScoreSnapshotResolver.Resolve(row.EntryValue, row.OwnedCurrent, info ?? row.EntryChartInfo, scores,
            scores?.ScoresByHash, scores?.ScoresBySha256);
    }

    private static bool AreSameChartInfoIdentity(BeMusicSeeker.Models.ChartDetails left, BeMusicSeeker.Models.ChartDetails right)
        => ReferenceEquals(left, right) || left != null && right != null && !string.IsNullOrWhiteSpace(left.sha256)
            && left.parser_version > 0 && right.parser_version > 0
            && StringComparer.OrdinalIgnoreCase.Equals(left.sha256, right.sha256)
            && left.parser_version == right.parser_version && left.updated_at == right.updated_at;
}
