using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using BeMusicSeeker.Models.LR2;

namespace BeMusicSeeker.Models.BmsLibraryInternal;

internal sealed class Lr2FullGenerationBackfillRequest
{
    public string Signature { get; set; }

    public string RunId { get; set; }

    public IReadOnlyCollection<string> RootDirectories { get; set; } = [];

    public IReadOnlyCollection<string> ChartPaths { get; set; } = [];

    public IReadOnlyCollection<string> FolderInfoFilePaths { get; set; } = [];

    public DateTime StartedAtUtc { get; set; } = DateTime.UtcNow;
}

internal sealed class Lr2FullGenerationBackfillResult
{
    public int TotalCount { get; set; }

    public int ProcessedCount { get; set; }

    public string FinalStage { get; set; }

    public string IncompleteReason { get; set; }

    public Lr2NormalFolderDbSyncResult NormalFolderSyncResult { get; set; }

    public long ElapsedMs { get; set; }
}

internal static class Lr2FullGenerationBackfillService
{
    internal const string SongRowsPendingStage = "song_rows_pending";

    internal const string SongRowsPendingReason = "song_backfill_not_implemented";

    internal static Lr2FullGenerationBackfillResult Run(
        LR2SongDBExtended songDb,
        Lr2FullGenerationBackfillRequest request)
    {
        if (songDb == null)
        {
            throw new ArgumentNullException(nameof(songDb));
        }

        request ??= new Lr2FullGenerationBackfillRequest();
        var stopwatch = Stopwatch.StartNew();
        List<string> roots = [.. (request.RootDirectories ?? [])
            .Where(path => !string.IsNullOrWhiteSpace(path))
            .Distinct(StringComparer.OrdinalIgnoreCase)];
        List<string> chartPaths = [.. (request.ChartPaths ?? [])
            .Where(path => !string.IsNullOrWhiteSpace(path))
            .Distinct(StringComparer.OrdinalIgnoreCase)];
        List<string> folderInfoFilePaths = [.. (request.FolderInfoFilePaths ?? [])
            .Where(path => !string.IsNullOrWhiteSpace(path))
            .Distinct(StringComparer.OrdinalIgnoreCase)];
        int totalCount = roots.Count + chartPaths.Count + folderInfoFilePaths.Count;

        Lr2FullGenerationStatusService.MarkRunning(
            songDb,
            request.Signature,
            request.RunId,
            totalCount,
            stage: "normal_folders",
            nowUtc: request.StartedAtUtc);

        Lr2NormalFolderDbSyncResult normalFolderResult = null;
        if (roots.Count > 0)
        {
            normalFolderResult = Lr2NormalFolderDbSyncService.Sync(songDb, new Lr2NormalFolderDbSyncRequest
            {
                RootDirectories = roots,
                ChartPaths = chartPaths,
                FolderInfoFilePaths = folderInfoFilePaths,
                AllowPrune = true,
                GeneratedAtUtc = request.StartedAtUtc
            });
        }

        Lr2FullGenerationStatusService.UpdateCursor(
            songDb,
            request.Signature,
            request.RunId,
            processedCursor: totalCount,
            totalCount: totalCount,
            stage: "normal_folders_completed",
            nowUtc: DateTime.UtcNow);

        Lr2FullGenerationStatusService.MarkIncomplete(
            songDb,
            request.Signature,
            request.RunId,
            processedCursor: totalCount,
            totalCount,
            stage: SongRowsPendingStage,
            detail: SongRowsPendingReason,
            nowUtc: DateTime.UtcNow);

        stopwatch.Stop();
        return new Lr2FullGenerationBackfillResult
        {
            TotalCount = totalCount,
            ProcessedCount = totalCount,
            FinalStage = SongRowsPendingStage,
            IncompleteReason = SongRowsPendingReason,
            NormalFolderSyncResult = normalFolderResult,
            ElapsedMs = stopwatch.ElapsedMilliseconds
        };
    }
}
