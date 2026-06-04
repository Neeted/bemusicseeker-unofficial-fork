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

    public IReadOnlyCollection<BMSFile> SongRows { get; set; } = [];

    public DateTime StartedAtUtc { get; set; } = DateTime.UtcNow;
}

internal sealed class Lr2FullGenerationBackfillResult
{
    public int TotalCount { get; set; }

    public int ProcessedCount { get; set; }

    public string FinalStage { get; set; }

    public string IncompleteReason { get; set; }

    public Lr2NormalFolderDbSyncResult NormalFolderSyncResult { get; set; }

    public int SongRowProcessedCount { get; set; }

    public long ElapsedMs { get; set; }
}

internal static class Lr2FullGenerationBackfillService
{
    internal const string RemainingStagesPendingStage = "remaining_stages_pending";

    internal const string RemainingStagesPendingReason = "remaining_backfill_stages_not_implemented";

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
        List<BMSFile> songRows = [.. (request.SongRows ?? [])
            .Where(file => file != null && !string.IsNullOrWhiteSpace(file.path))];
        int totalCount = roots.Count + chartPaths.Count + folderInfoFilePaths.Count + songRows.Count;

        Lr2FullGenerationStatusService.MarkRunning(
            songDb,
            request.Signature,
            request.RunId,
            totalCount,
            stage: "normal_folders",
            nowUtc: request.StartedAtUtc);

        Lr2NormalFolderDbSyncResult normalFolderResult = null;
        int normalFolderProcessedCount = 0;
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
            normalFolderProcessedCount = roots.Count + chartPaths.Count + folderInfoFilePaths.Count;
        }

        Lr2FullGenerationStatusService.UpdateCursor(
            songDb,
            request.Signature,
            request.RunId,
            processedCursor: normalFolderProcessedCount,
            totalCount: totalCount,
            stage: "normal_folders_completed",
            nowUtc: DateTime.UtcNow);

        Lr2FullGenerationStatusService.UpdateCursor(
            songDb,
            request.Signature,
            request.RunId,
            processedCursor: normalFolderProcessedCount,
            totalCount: totalCount,
            stage: "song_rows",
            nowUtc: DateTime.UtcNow);

        int songRowProcessedCount = UpsertSongRows(songDb, songRows);
        int processedCount = normalFolderProcessedCount + songRowProcessedCount;

        Lr2FullGenerationStatusService.UpdateCursor(
            songDb,
            request.Signature,
            request.RunId,
            processedCursor: processedCount,
            totalCount: totalCount,
            stage: "song_rows_completed",
            nowUtc: DateTime.UtcNow);

        Lr2FullGenerationStatusService.MarkIncomplete(
            songDb,
            request.Signature,
            request.RunId,
            processedCursor: processedCount,
            totalCount,
            stage: RemainingStagesPendingStage,
            detail: RemainingStagesPendingReason,
            nowUtc: DateTime.UtcNow);

        stopwatch.Stop();
        return new Lr2FullGenerationBackfillResult
        {
            TotalCount = totalCount,
            ProcessedCount = processedCount,
            FinalStage = RemainingStagesPendingStage,
            IncompleteReason = RemainingStagesPendingReason,
            NormalFolderSyncResult = normalFolderResult,
            SongRowProcessedCount = songRowProcessedCount,
            ElapsedMs = stopwatch.ElapsedMilliseconds
        };
    }

    private static int UpsertSongRows(LR2SongDBExtended songDb, IReadOnlyCollection<BMSFile> songRows)
    {
        if (songRows == null || songRows.Count == 0)
        {
            return 0;
        }

        BmsLibraryDbGateway.EnsureBmsonSchema(songDb);
        BmsLibraryDbGateway.EnsureSongLookupIndexes(songDb);
        int processed = 0;
        songDb.BeginTransaction();
        try
        {
            foreach (BMSFile song in songRows)
            {
                if (song == null || string.IsNullOrWhiteSpace(song.path))
                {
                    continue;
                }
                Lr2SongDbWriter.UpsertGeneratedSong(songDb, song);
                processed++;
            }
            songDb.Commit();
        }
        catch
        {
            songDb.Rollback();
            throw;
        }
        return processed;
    }
}
