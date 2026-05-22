using System.Collections.Generic;
using BeMusicSeeker.Models.LR2;
using BeMusicSeeker.Models.Utils;

namespace BeMusicSeeker.Models.BmsLibraryInternal;

internal sealed class SongTableFileCheckResult
{
    public List<string> Pragmas { get; } = [];

    public List<BMSFile> AddedFiles { get; } = [];

    public List<LR2SongDBExtended.bmson_song> AddedBmsonSongs { get; } = [];

    public List<LR2SongDBExtended.chart_info> InlineChartInfoRows { get; } = [];

    public List<LR2SongDBExtended.chart_info> InlineChartInfoAppliedRows { get; } = [];

    public List<LR2SongDBExtended.chart_info_parse_failure> InlineChartInfoParseFailureRows { get; } = [];

    public List<string> InlineChartInfoParseFailureDeleteMd5s { get; } = [];

    public List<BMSFile> NextFiles { get; } = [];

    public List<LR2SongDBExtended.bmson_song> NextBmsonSongs { get; } = [];

    public List<string> DeletedPaths { get; } = [];

    public List<string> DeletedBmsonPaths { get; } = [];

    public LibraryMutationDelta MutationDelta { get; } = new();

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

    public long InlineHealthWallMs { get; set; }

    public long InlineEncodingWallMs { get; set; }

    public long InlineEncodingReloadWallMs { get; set; }

    public int InlineEncodingReloadCount { get; set; }

    public int InlineEncodingDetectCount { get; set; }

    public int InlineEncodingFastAsciiCount { get; set; }

    public int InlineEncodingShiftJisCount { get; set; }

    public int InlineEncodingShiftJisQuestionCount { get; set; }

    public int InlineEncodingKoreanCount { get; set; }

    public int InlineEncodingKoreanQuestionCount { get; set; }

    public int InlineEncodingUtf8Count { get; set; }

    public int InlineEncodingUnknownCount { get; set; }

    public int InlineEncodingOtherCount { get; set; }

    public long InlineEncodingMaxItemMs { get; set; }

    public long InlineBmsMaintenanceWallMs { get; set; }

    public long InlineBmsonMaintenanceWallMs { get; set; }

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
        MutationDelta.Clear();
        Pragmas.Clear();
    }
}
