using System.Collections.Generic;
using BeMusicSeeker.Models.LR2;
using BeMusicSeeker.Models.Utils;

namespace BeMusicSeeker.Models.BmsLibraryInternal;

internal sealed class SongTableFileCheckResult
{
    public List<string> Pragmas { get; } = new List<string>();

    public List<BMSFile> AddedFiles { get; } = new List<BMSFile>();

    public List<LR2SongDBExtended.bmson_song> AddedBmsonSongs { get; } = new List<LR2SongDBExtended.bmson_song>();

    public List<LR2SongDBExtended.chart_info> InlineChartInfoRows { get; } = new List<LR2SongDBExtended.chart_info>();

    public List<LR2SongDBExtended.chart_info> InlineChartInfoAppliedRows { get; } = new List<LR2SongDBExtended.chart_info>();

    public List<LR2SongDBExtended.chart_info_parse_failure> InlineChartInfoParseFailureRows { get; } = new List<LR2SongDBExtended.chart_info_parse_failure>();

    public List<string> InlineChartInfoParseFailureDeleteMd5s { get; } = new List<string>();

    public List<BMSFile> NextFiles { get; } = new List<BMSFile>();

    public List<LR2SongDBExtended.bmson_song> NextBmsonSongs { get; } = new List<LR2SongDBExtended.bmson_song>();

    public List<string> DeletedPaths { get; } = new List<string>();

    public List<string> DeletedBmsonPaths { get; } = new List<string>();

    public List<BMSFile> ClearedInstallDestinations { get; } = new List<BMSFile>();

    public DirectoryResourceLookupCache NextDirectoryResourceLookupCache { get; set; }

    public LibraryResourceIndex NextResourceIndex { get; set; }

    public bool HasDbDiff { get; set; }

    public bool PrefetchedScanUsed { get; set; }

    public int BmsPathCount { get; set; }

    public int DirectoryCount { get; set; }

    public long ScanElapsedMs { get; set; }

    public long NativeBridgeMs { get; set; }

    public string NativeBridgeReason { get; set; }

    public long ManagedDecodeMs { get; set; }

    public long ManagedMaterializeMs { get; set; }

    public ulong BridgeRawBufferBytes { get; set; }

    public long DirhashBuildMs { get; set; }

    public long ResourceLookupCacheMs { get; set; }

    public long ResourceIndexBuildMs { get; set; }

    public long DiffMs { get; set; }

    public int BmsAddedTargetCount { get; set; }

    public int BmsDeletedTargetCount { get; set; }

    public int BmsonUpsertTargetCount { get; set; }

    public int BmsonDeletedTargetCount { get; set; }

    public int FileDiffParserDegree { get; set; }

    public int ReadQueueCapacity { get; set; }

    public int ParsedQueueCapacity { get; set; }

    public int PostParseQueueCapacity { get; set; }

    public int CommitQueueCapacity { get; set; }

    public long ReaderOutputWaitMs { get; set; }

    public long ParserOutputWaitMs { get; set; }

    public long PostParseQueueWaitMs { get; set; }

    public long CommitQueueWaitMs { get; set; }

    public int PostParseBatchCount { get; set; }

    public long PostParseWallMs { get; set; }

    public long PostParseMaxBatchMs { get; set; }

    public long BmsParseMs { get; set; }

    public long BmsonParseMs { get; set; }

    public int InlineChartInfoTargetCount { get; set; }

    public int InlineChartInfoSuccessCount { get; set; }

    public int InlineChartInfoCurrentSkippedCount { get; set; }

    public int InlineChartInfoFailureSkippedCount { get; set; }

    public int InlineChartInfoParseFailedCount { get; set; }

    public int InlineChartInfoFailurePersistedCount { get; set; }

    public int InlineChartInfoFailureClearedCount { get; set; }

    public int InlineChartInfoIndexPublishedCount { get; set; }

    public long InlineChartInfoParseMs { get; set; }

    public long InlineChartInfoWallMs { get; set; }

    public int InlineChartInfoBatchSize { get; set; }

    public int InlineMaintenanceDegree { get; set; }

    public int InlineMaintenanceTargetCount { get; set; }

    public int InlineMaintenanceSuccessCount { get; set; }

    public int InlineMaintenanceFailedCount { get; set; }

    public int InlineMaintenanceBmsCount { get; set; }

    public int InlineMaintenanceBmsonCount { get; set; }

    public long InlineMaintenanceMs { get; set; }

    public long InlineMaintenanceWallMs { get; set; }

    public long InlineMaintenanceCacheHitCount { get; set; }

    public long InlineMaintenanceFileExistsFallbackCount { get; set; }

    public long ParseReadBytesEstimate { get; set; }

    public long FileDiffReadMs { get; set; }

    public long FileDiffParseMs { get; set; }

    public int SnapshotQueueHighWatermark { get; set; }

    public long NewFileParseMs { get; set; }

    public long ApplyMs { get; set; }

    public long DbCommitMs { get; set; }

    public int DbCommitChunkSize { get; set; }

    public int DbCommitChunks { get; set; }

    public long DbCommitMaxChunkMs { get; set; }

    public long InstlDstCleanupMs { get; set; }

    public ulong AudioResourceKeyHashEntryCount { get; set; }

    public ulong ImageResourceKeyHashEntryCount { get; set; }

    public ulong MovieResourceKeyHashEntryCount { get; set; }

    public void ReleasePostApplyTransientBuffers()
    {
        AddedFiles.Clear();
        AddedBmsonSongs.Clear();
        InlineChartInfoRows.Clear();
        InlineChartInfoAppliedRows.Clear();
        InlineChartInfoParseFailureRows.Clear();
        InlineChartInfoParseFailureDeleteMd5s.Clear();
        DeletedPaths.Clear();
        DeletedBmsonPaths.Clear();
        ClearedInstallDestinations.Clear();
        Pragmas.Clear();
    }
}
