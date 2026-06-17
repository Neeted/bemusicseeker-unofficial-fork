using System;
using System.Collections.Generic;
using BeMusicSeeker.Models.LR2;
using BeMusicSeeker.Models.Utils;

namespace BeMusicSeeker.Models.BmsLibraryInternal;

internal sealed class SongTableFileCheckResult
{
    public List<string> Pragmas { get; } = [];

    public List<BMSFile> AddedFiles { get; } = [];

    public HashSet<string> NewlyInsertedBmsPaths { get; } = new(StringComparer.OrdinalIgnoreCase);

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

    public bool ScanFallbackUsed { get; set; }

    public string ScanFallbackReason { get; set; } = string.Empty;

    public bool Lr2ScanSurfaceAvailable { get; set; }

    public IReadOnlyList<string> Lr2ScanFolderInfoFilePaths { get; set; } = [];

    public IReadOnlyDictionary<string, RootFileEnumerationEntry> Lr2ScanFolderInfoFileEntries { get; set; } =
        new Dictionary<string, RootFileEnumerationEntry>(StringComparer.OrdinalIgnoreCase);

    public IReadOnlyList<string> Lr2ScanNormalFolderDirectoryPaths { get; set; } = [];

    public IReadOnlyDictionary<string, RootFileEnumerationEntry> Lr2ScanDirectoryEntries { get; set; } =
        new Dictionary<string, RootFileEnumerationEntry>(StringComparer.OrdinalIgnoreCase);

    public IReadOnlyDictionary<string, RootFileEnumerationEntry> Lr2ScanNormalFolderDirectoryEntries { get; set; } =
        new Dictionary<string, RootFileEnumerationEntry>(StringComparer.OrdinalIgnoreCase);

    public IReadOnlyList<string> Lr2ScanTextFileDirectories { get; set; } = [];

    public IReadOnlyList<string> Lr2ScanLr2FolderDiscoveryDirectories { get; set; } = [];

    public IReadOnlyList<string> Lr2ScanLr2FolderFilePaths { get; set; } = [];

    public IReadOnlyDictionary<string, RootFileEnumerationEntry> Lr2ScanLr2FolderFileEntries { get; set; } =
        new Dictionary<string, RootFileEnumerationEntry>(StringComparer.OrdinalIgnoreCase);

    public bool Lr2ScanLr2FolderFileDiscoveryComplete { get; set; }

    public bool Lr2ScanLr2FolderCandidatesAlreadyFiltered { get; set; }

    public int Lr2ScanLr2FolderAppManagedFilteredCount { get; set; }

    public int Lr2ScanLr2FolderAppManagedScopeDirectoryCount { get; set; }

    public int Lr2ScanLr2FolderAppManagedExactFileCount { get; set; }

    public IReadOnlyList<string> Lr2ScanAppManagedCustomFolderOutputFilePaths { get; set; } = [];

    public IReadOnlyDictionary<string, RootFileEnumerationEntry> Lr2ScanAppManagedCustomFolderOutputFileEntries { get; set; } =
        new Dictionary<string, RootFileEnumerationEntry>(StringComparer.OrdinalIgnoreCase);

    public bool Lr2ScanAppManagedCustomFolderOutputDiscoveryComplete { get; set; }

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

    public long DiffCurrentIndexMs { get; set; }

    public long DiffScannedSplitMs { get; set; }

    public long DiffDeletedMs { get; set; }

    public long DiffBmsTargetMs { get; set; }

    public long DiffBmsonTargetMs { get; set; }

    public int BmsAddedTargetCount { get; set; }

    public int BmsDeletedTargetCount { get; set; }

    public int BmsDateOnlyUpdateCount { get; set; }

    public int BmsTextOnlyUpdateCount { get; set; }

    public int BmsPathCaseUpdateCount { get; set; }

    public int BmsLegacyExistingProtectedCount { get; set; }

    public int BmsMovedHashRelinkCount { get; set; }

    public int BmsMovedHashRelinkAmbiguousCount { get; set; }

    public int BmsonUpsertTargetCount { get; set; }

    public int BmsonDeletedTargetCount { get; set; }

    public int BmsonPathCaseUpdateCount { get; set; }

    public int BmsMtimeFallbackCount { get; set; }

    public int BmsonMtimeFallbackCount { get; set; }

    public int FileDiffParserDegree { get; set; }

    public int FileDiffPostParseWorkerDegree { get; set; }

    public int FileDiffReaderDegree { get; set; }

    public int ReadQueueCapacity { get; set; }

    public int ParsedQueueCapacity { get; set; }

    public int PostParseQueueCapacity { get; set; }

    public int PostParseResultQueueCapacity { get; set; }

    public int CommitQueueCapacity { get; set; }

    public int CommitWriterQueueCapacity { get; set; }

    public int CommitWriterQueueHighWatermark { get; set; }

    public bool CommitStreamingEnabled { get; set; }

    public string CommitStreamingBarrierReason { get; set; }

    public long ReaderOutputWaitMs { get; set; }

    public long ParserOutputWaitMs { get; set; }

    public long PostParseQueueWaitMs { get; set; }

    public long PostParseOutputWaitMs { get; set; }

    public long CommitQueueWaitMs { get; set; }

    public long CommitWriterQueueWaitMs { get; set; }

    public long DbCommitFirstChunkStartMs { get; set; }

    public int PostParseWorkItemCount { get; set; }

    public long PostParseWallMs { get; set; }

    public long PostParseMaxItemMs { get; set; }

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

    public long InlineMaintenanceResourceIndexHitCount { get; set; }

    public long InlineMaintenanceResourceSetCacheHitCount { get; set; }

    public long InlineMaintenanceFileExistsFallbackCount { get; set; }

    public int InlineMaintenanceSharedResourceCacheEntries { get; set; }

    public int InlineMaintenanceResourceSetCacheEntries { get; set; }

    public long ParseReadBytesEstimate { get; set; }

    public long FileDiffReadMs { get; set; }

    public long FileDiffDigestMs { get; set; }

    public long FileDiffParseMs { get; set; }

    public int SnapshotQueueHighWatermark { get; set; }

    public long NewFileParseMs { get; set; }

    public long ApplyMs { get; set; }

    public long DbCommitMs { get; set; }

    public long DbCommitApplyMs { get; set; }

    public long DbCommitSchemaMs { get; set; }

    public long DbCommitBmsDeleteMs { get; set; }

    public long DbCommitBmsPathCaseUpdateMs { get; set; }

    public long DbCommitBmsDateUpdateMs { get; set; }

    public long DbCommitBmsUpsertMs { get; set; }

    public int DbCommitBmsChangedCount { get; set; }

    public long DbCommitBmsonDeleteMs { get; set; }

    public long DbCommitBmsonPathCaseUpdateMs { get; set; }

    public long DbCommitBmsonUpsertMs { get; set; }

    public long DbCommitMaintenanceUpsertMs { get; set; }

    public long DbCommitChartInfoMs { get; set; }

    public long DbCommitSqliteCommitMs { get; set; }

    public int DbCommitChunkSize { get; set; }

    public int DbCommitChunks { get; set; }

    public long DbCommitMaxChunkMs { get; set; }

    public long InstlDstCleanupMs { get; set; }

    public bool Lr2NormalFolderSyncExecuted { get; set; }

    public bool Lr2NormalFolderSyncFailed { get; set; }

    public string Lr2NormalFolderSyncFailureReason { get; set; }

    public int Lr2NormalFolderGeneratedCount { get; set; }

    public int Lr2NormalFolderUpsertedCount { get; set; }

    public int Lr2NormalFolderDeletedCount { get; set; }

    public int Lr2NormalFolderSkippedUnsupportedPathCount { get; set; }

    public int Lr2NormalFolderSkippedMissingMetadataCount { get; set; }

    public int Lr2NormalFolderSkippedIncompatibleChartPathCount { get; set; }

    public int Lr2NormalFolderMetadataRequestedDirectoryCount { get; set; }

    public int Lr2NormalFolderMetadataResolvedDirectoryCount { get; set; }

    public int Lr2NormalFolderInfoCandidateCount { get; set; }

    public int Lr2NormalFolderInfoAppliedCount { get; set; }

    public int Lr2NormalFolderInfoReadFailureCount { get; set; }

    public long Lr2NormalFolderSyncMs { get; set; }

    public ulong AudioResourceKeyHashEntryCount { get; set; }

    public ulong ImageResourceKeyHashEntryCount { get; set; }

    public ulong MovieResourceKeyHashEntryCount { get; set; }

    public void ReleasePostApplyTransientBuffers()
    {
        AddedFiles.Clear();
        NewlyInsertedBmsPaths.Clear();
        AddedBmsonSongs.Clear();
        InlineChartInfoRows.Clear();
        InlineChartInfoAppliedRows.Clear();
        InlineChartInfoParseFailureRows.Clear();
        InlineChartInfoParseFailureDeleteMd5s.Clear();
        DeletedPaths.Clear();
        DeletedBmsonPaths.Clear();
        MutationDelta.Clear();
        Pragmas.Clear();
        Lr2ScanSurfaceAvailable = false;
        Lr2ScanFolderInfoFilePaths = [];
        Lr2ScanFolderInfoFileEntries = new Dictionary<string, RootFileEnumerationEntry>(StringComparer.OrdinalIgnoreCase);
        Lr2ScanNormalFolderDirectoryPaths = [];
        Lr2ScanDirectoryEntries = new Dictionary<string, RootFileEnumerationEntry>(StringComparer.OrdinalIgnoreCase);
        Lr2ScanNormalFolderDirectoryEntries = new Dictionary<string, RootFileEnumerationEntry>(StringComparer.OrdinalIgnoreCase);
        Lr2ScanTextFileDirectories = [];
        Lr2ScanLr2FolderDiscoveryDirectories = [];
        Lr2ScanLr2FolderFilePaths = [];
        Lr2ScanLr2FolderFileEntries = new Dictionary<string, RootFileEnumerationEntry>(StringComparer.OrdinalIgnoreCase);
        Lr2ScanLr2FolderFileDiscoveryComplete = false;
        Lr2ScanLr2FolderCandidatesAlreadyFiltered = false;
        Lr2ScanLr2FolderAppManagedFilteredCount = 0;
        Lr2ScanLr2FolderAppManagedScopeDirectoryCount = 0;
        Lr2ScanLr2FolderAppManagedExactFileCount = 0;
        Lr2ScanAppManagedCustomFolderOutputFilePaths = [];
        Lr2ScanAppManagedCustomFolderOutputFileEntries = new Dictionary<string, RootFileEnumerationEntry>(StringComparer.OrdinalIgnoreCase);
        Lr2ScanAppManagedCustomFolderOutputDiscoveryComplete = false;
    }
}
