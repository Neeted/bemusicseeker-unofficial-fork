using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;
using BeMusicSeeker.Models.LR2;

namespace BeMusicSeeker.Models.BmsLibraryInternal;

internal sealed class Lr2FullGenerationBackfillRequest
{
    public string Signature { get; set; }

    public string RunId { get; set; }

    public IReadOnlyCollection<string> RootDirectories { get; set; } = [];

    public IReadOnlyCollection<string> ChartPaths { get; set; } = [];

    public IReadOnlyCollection<string> FolderInfoFilePaths { get; set; } = [];

    public IReadOnlyCollection<string> Lr2FolderFilePaths { get; set; } = [];

    public IReadOnlyCollection<string> Lr2FolderDiscoveryDirectories { get; set; } = [];

    public IReadOnlyCollection<string> Lr2FolderPruneDirectories { get; set; } = [];

    public bool Lr2FolderFileDiscoveryComplete { get; set; }

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

    public Lr2FolderFileDbSyncResult Lr2FolderFileSyncResult { get; set; }

    public int Lr2FolderFileProcessedCount { get; set; }

    public int SongRowProcessedCount { get; set; }

    public int SongRowParseFailureCount { get; set; }

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
        List<string> lr2FolderFilePaths = [.. (request.Lr2FolderFilePaths ?? [])
            .Where(path => !string.IsNullOrWhiteSpace(path))
            .Distinct(StringComparer.OrdinalIgnoreCase)];
        List<string> lr2FolderDiscoveryDirectories = [.. (request.Lr2FolderDiscoveryDirectories ?? [])
            .Where(path => !string.IsNullOrWhiteSpace(path))
            .Distinct(StringComparer.OrdinalIgnoreCase)];
        List<string> lr2FolderPruneDirectories = [.. (request.Lr2FolderPruneDirectories ?? [])
            .Where(path => !string.IsNullOrWhiteSpace(path))
            .Distinct(StringComparer.OrdinalIgnoreCase)];
        List<BMSFile> songRows = [.. (request.SongRows ?? [])
            .Where(file => file != null && !string.IsNullOrWhiteSpace(file.path))];
        int normalFolderTargetCount = roots.Count > 0
            ? roots.Count + chartPaths.Count + folderInfoFilePaths.Count
            : 0;
        int totalCount = normalFolderTargetCount + lr2FolderFilePaths.Count + songRows.Count;

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
            normalFolderProcessedCount = normalFolderTargetCount;
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
            stage: "lr2folder_files",
            nowUtc: DateTime.UtcNow);

        Lr2FolderFileDbSyncResult lr2FolderFileResult = null;
        int lr2FolderFileProcessedCount = 0;
        if (lr2FolderDiscoveryDirectories.Count > 0)
        {
            Lr2FolderFileSyncItemsResult syncItems = CreateLr2FolderFileSyncItems(lr2FolderFilePaths);
            lr2FolderFileResult = Lr2FolderFileDbSyncService.Sync(songDb, new Lr2FolderFileDbSyncRequest
            {
                Items = syncItems.Items,
                ScopeDirectories = lr2FolderPruneDirectories,
                AllowPrune = lr2FolderPruneDirectories.Count > 0
                    && request.Lr2FolderFileDiscoveryComplete
                    && !syncItems.HasReadFailures,
                GeneratedAtUtc = request.StartedAtUtc
            });
            lr2FolderFileProcessedCount = lr2FolderFileResult.ItemCount;
        }
        int folderProcessedCount = normalFolderProcessedCount + lr2FolderFileProcessedCount;

        Lr2FullGenerationStatusService.UpdateCursor(
            songDb,
            request.Signature,
            request.RunId,
            processedCursor: folderProcessedCount,
            totalCount: totalCount,
            stage: "lr2folder_files_completed",
            nowUtc: DateTime.UtcNow);

        Lr2FullGenerationStatusService.UpdateCursor(
            songDb,
            request.Signature,
            request.RunId,
            processedCursor: folderProcessedCount,
            totalCount: totalCount,
            stage: "song_rows",
            nowUtc: DateTime.UtcNow);

        SongRowBackfillResult songRowResult = UpsertSongRows(songDb, songRows);
        int processedCount = folderProcessedCount + songRowResult.ProcessedCount;

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
            Lr2FolderFileSyncResult = lr2FolderFileResult,
            Lr2FolderFileProcessedCount = lr2FolderFileProcessedCount,
            SongRowProcessedCount = songRowResult.ProcessedCount,
            SongRowParseFailureCount = songRowResult.ParseFailureCount,
            ElapsedMs = stopwatch.ElapsedMilliseconds
        };
    }

    private static Lr2FolderFileSyncItemsResult CreateLr2FolderFileSyncItems(IEnumerable<string> filePaths)
    {
        var items = new List<Lr2FolderFileSyncItem>();
        bool hasReadFailures = false;
        foreach (string filePath in filePaths ?? [])
        {
            if (string.IsNullOrWhiteSpace(filePath))
            {
                continue;
            }

            Lr2FolderFileSyncItem item = CreateLr2FolderFileSyncItem(filePath);
            if (item.LastWriteTimeUtc == null)
            {
                hasReadFailures = true;
            }
            items.Add(item);
        }
        return new Lr2FolderFileSyncItemsResult(items, hasReadFailures);
    }

    private static Lr2FolderFileSyncItem CreateLr2FolderFileSyncItem(string filePath)
    {
        try
        {
            return new Lr2FolderFileSyncItem
            {
                FilePath = filePath,
                LastWriteTimeUtc = File.GetLastWriteTimeUtc(filePath),
                Definition = Lr2FolderFileProjection.ParseDefinition(File.ReadLines(filePath, Encoding.GetEncoding("shift_jis")))
            };
        }
        catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException || ex is ArgumentException || ex is NotSupportedException || ex is PathTooLongException || ex is DecoderFallbackException)
        {
            return new Lr2FolderFileSyncItem
            {
                FilePath = filePath,
                LastWriteTimeUtc = null,
                Definition = null
            };
        }
    }

    private sealed class Lr2FolderFileSyncItemsResult(
        IReadOnlyCollection<Lr2FolderFileSyncItem> items,
        bool hasReadFailures)
    {
        public IReadOnlyCollection<Lr2FolderFileSyncItem> Items { get; } = items ?? [];

        public bool HasReadFailures { get; } = hasReadFailures;
    }

    private static SongRowBackfillResult UpsertSongRows(LR2SongDBExtended songDb, IReadOnlyCollection<BMSFile> songRows)
    {
        if (songRows == null || songRows.Count == 0)
        {
            return new SongRowBackfillResult(0, 0);
        }

        var rowsToWrite = new List<BMSFile>();
        int parseFailureCount = 0;
        foreach (BMSFile song in songRows.Where(song => song != null && !string.IsNullOrWhiteSpace(song.path)))
        {
            BMSFile row = CreateBackfillSongRow(song, out bool parsedFromSnapshot);
            if (row == null || string.IsNullOrWhiteSpace(row.path))
            {
                continue;
            }
            if (!parsedFromSnapshot)
            {
                parseFailureCount++;
            }
            rowsToWrite.Add(row);
        }
        if (rowsToWrite.Count == 0)
        {
            return new SongRowBackfillResult(0, parseFailureCount);
        }

        BmsLibraryDbGateway.EnsureBmsonSchema(songDb);
        BmsLibraryDbGateway.EnsureSongLookupIndexes(songDb);
        int processed = 0;
        songDb.BeginTransaction();
        try
        {
            foreach (BMSFile song in rowsToWrite)
            {
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
        return new SongRowBackfillResult(processed, parseFailureCount);
    }

    private static BMSFile CreateBackfillSongRow(BMSFile existingSong, out bool parsedFromSnapshot)
    {
        parsedFromSnapshot = false;
        if (existingSong == null || string.IsNullOrWhiteSpace(existingSong.path))
        {
            return null;
        }

        try
        {
            ChartFileSnapshot snapshot = ChartFileContentReader.ReadSnapshot(existingSong.path);
            BMSFile.BmsEncodingDetectionResult detectionResult = BMSFile.DetectEncodingOfBMSFileDetailed(snapshot);
            string encodingName = ResolveSafeBackfillParseEncoding(detectionResult);
            if (string.IsNullOrWhiteSpace(encodingName))
            {
                return existingSong.CreateSongRowPersistenceCopy();
            }

            BMSFile parsed = BMSFile.CreateBMSFileFromSnapshot(snapshot, encodingName);
            Lr2SongRowEnricher.EnrichParsedSong(
                parsed,
                snapshot,
                existingSong.txt.GetValueOrDefault(),
                existingSong);
            parsedFromSnapshot = true;
            return parsed;
        }
        catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException || ex is ArgumentException || ex is NotSupportedException || ex is PathTooLongException || ex is DecoderFallbackException)
        {
            return existingSong.CreateSongRowPersistenceCopy();
        }
    }

    private static string ResolveSafeBackfillParseEncoding(BMSFile.BmsEncodingDetectionResult detectionResult)
    {
        if (detectionResult == null)
        {
            return null;
        }
        if (detectionResult.Outcome == BMSFile.EncodingDetectionOutcome.Unknown
            || detectionResult.Outcome == BMSFile.EncodingDetectionOutcome.Other)
        {
            return null;
        }

        string encodingName = detectionResult.EncodingName;
        if (string.IsNullOrWhiteSpace(encodingName))
        {
            return null;
        }
        return encodingName.TrimEnd('?');
    }

    private sealed class SongRowBackfillResult(int processedCount, int parseFailureCount)
    {
        public int ProcessedCount { get; } = processedCount;

        public int ParseFailureCount { get; } = parseFailureCount;
    }
}
