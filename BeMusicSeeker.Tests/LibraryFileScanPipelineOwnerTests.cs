using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using BeMusicSeeker.Models;
using BeMusicSeeker.Models.BmsLibraryInternal;
using BeMusicSeeker.Models.LR2;
using BeMusicSeeker.Models.Utils;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace BeMusicSeeker.Tests;

[TestClass]
[DoNotParallelize]
public sealed class LibraryFileScanPipelineOwnerTests
{
    [TestMethod]
    public void ApplyFileScanDiff_EmptyDirectoryRequestCompletesProgressForRepeatedRequests()
    {
        var host = new RecordingLibraryFileScanPipelineHost();
        var owner = CreateOwner(host);

        SongTableFileCheckResult first = owner.ApplyFileScanDiff(
            new BmsLibraryOptionsSnapshot(),
            [],
            null!,
            null!,
            trackLibraryFileCheckProgress: true,
            reason: "test_first");
        SongTableFileCheckResult second = owner.ApplyFileScanDiff(
            new BmsLibraryOptionsSnapshot(),
            null!,
            null!,
            null!,
            trackLibraryFileCheckProgress: true,
            reason: "test_second");

        Assert.IsNotNull(first);
        Assert.IsNotNull(second);
        Assert.AreEqual(2, host.EnumerationCompletedCount);
        Assert.AreEqual(2, host.DiffCompletedCount);
        Assert.AreEqual(2, host.MutationBlockedChecks);
        Assert.AreEqual("ApplyLibraryFileScanDiff", host.LastMutationBlockedOperation);
        Assert.AreEqual(2, host.PerformanceMessages.Count);
        StringAssert.Contains(host.PerformanceMessages[0], "reason=no_bms_directories");
        StringAssert.Contains(host.PerformanceMessages[1], "reason=no_bms_directories");
    }

    [TestMethod]
    public void ApplyFileScanDiff_IncompletePrefetchSkipsStorageAndReportsWarning()
    {
        var host = new RecordingLibraryFileScanPipelineHost();
        var owner = CreateOwner(host);
        var prefetch = new ChartScanPrefetchInfo
        {
            ScanResult = new ChartScanExecutionResult
            {
                Success = false,
                IsComplete = false,
                ErrorReason = "test_incomplete_scan"
            }
        };

        SongTableFileCheckResult result = owner.ApplyFileScanDiff(
            new BmsLibraryOptionsSnapshot(),
            ["C:\\charts"],
            prefetch,
            null!,
            trackLibraryFileCheckProgress: true,
            reason: "test_incomplete");

        Assert.IsNotNull(result);
        Assert.AreEqual(1, host.IncompleteWarningCount);
        Assert.AreEqual("test_incomplete_scan", host.LastIncompleteWarningReason);
        Assert.AreEqual(1, host.EnumerationCompletedCount);
        Assert.AreEqual(1, host.DiffCompletedCount);
    }

    private static LibraryFileScanPipelineOwner CreateOwner(ILibraryFileScanPipelineHost host)
    {
        return new LibraryFileScanPipelineOwner(
            host,
            new BmsLibraryInitializationService(),
            () => new LibraryFileScanStorageMutationCoordinator(new NoopLibraryFileScanStorageMutationHost()),
            () => new LibraryMutationDeltaApplyCoordinator(new NoopLibraryMutationDeltaApplyHost()));
    }

    private sealed class RecordingLibraryFileScanPipelineHost : ILibraryFileScanPipelineHost
    {
        public BmsLibraryDbGateway DbGateway => null!;

        public IReadOnlyList<BMSFile> BmsFiles => [];

        public IReadOnlyList<LR2SongDBExtended.bmson_song> BmsonSongs => [];

        public IBmsLibraryDialogService DialogService => null!;

        public bool EverythingScanLoggingEnabled => false;

        public int EnumerationCompletedCount { get; private set; }

        public int DiffCompletedCount { get; private set; }

        public int MutationBlockedChecks { get; private set; }

        public string LastMutationBlockedOperation { get; private set; } = string.Empty;

        public int IncompleteWarningCount { get; private set; }

        public string LastIncompleteWarningReason { get; private set; } = string.Empty;

        public List<string> PerformanceMessages { get; } = [];

        public void ThrowIfLr2SongDbSyncMutationBlocked(string operation)
        {
            MutationBlockedChecks++;
            LastMutationBlockedOperation = operation;
        }

        public void ReportLibraryInitializationProgress(
            BMSLibrary.LibraryInitializationProgressStage stage,
            string scannerLabel = null!,
            int totalCount = 0,
            int processedCount = 0,
            string currentPath = null!,
            bool force = false)
        {
        }

        public void CompleteLibraryFileEnumerationProgress()
        {
            EnumerationCompletedCount++;
        }

        public void CompleteLibraryFileDiffProgress()
        {
            DiffCompletedCount++;
        }

        public void LogInstallPerformance(string message)
        {
            PerformanceMessages.Add(message);
        }

        public void LogInstallPerformanceWarn(string message)
        {
        }

        public void LogEverythingScan(string message)
        {
        }

        public void LogStartupMemoryCheckpoint(string phase, string point)
        {
        }

        public string GetDisplayedExceptionMessage(Exception exception)
        {
            return exception?.Message ?? string.Empty;
        }

        public void QueueEverythingFallbackWarning(string fallbackReason)
        {
        }

        public void QueueFileScanSkippedIncompleteWarning(string failureReason)
        {
            IncompleteWarningCount++;
            LastIncompleteWarningReason = failureReason;
        }

        public void QueueEmptyScanWithExistingDbWarning(string failureReason)
        {
        }

        public bool CanPrepareLr2FolderFileDiff(
            BmsLibraryOptionsSnapshot options,
            SongTableFileCheckResult fileCheckResult)
        {
            return false;
        }

        public Lr2FolderFileDiffPreparationResult PrepareLr2FolderFileDiffSync(
            BmsLibraryOptionsSnapshot options,
            IReadOnlyList<string> rootDirectories,
            SongTableFileCheckResult fileCheckResult,
            string reason)
        {
            return null!;
        }

        public void ApplyLr2FolderFileDiffSync(
            BmsLibraryOptionsSnapshot options,
            IReadOnlyList<string> rootDirectories,
            SongTableFileCheckResult fileCheckResult,
            string reason,
            Task<Lr2FolderFileDiffPreparationResult> preparationTask)
        {
        }

        public List<ChartFile> CreateCurrentInstallDestinationCleanupCharts()
        {
            return [];
        }

        public Lr2SongDbSyncAppManagedOutputScope CreateLr2SongDbSyncAppManagedOutputScope()
        {
            return new Lr2SongDbSyncAppManagedOutputScope([], [], [], isComplete: false);
        }

        public Lr2BuiltinCustomFolderSettings CreateCurrentLr2BuiltinCustomFolderSettings(DateTime nowUtc)
        {
            return new Lr2BuiltinCustomFolderSettings(0, 0, false);
        }

        public bool ShouldProtectExistingBmsRowsFromLr2SongDbSyncMigration(BmsLibraryOptionsSnapshot options)
        {
            return false;
        }

        public void CaptureChartInfoCompletedLr2SongDbSyncTrustFromFileDiff(
            BmsLibraryOptionsSnapshot options,
            SongTableFileCheckResult fileCheckResult,
            string reason)
        {
        }

        public void UpsertChartInfoIndexRows(
            IEnumerable<LR2SongDBExtended.chart_info> rows,
            string reason,
            bool dispatchPresentation = true)
        {
        }

        public void DispatchWarningPresentationChanged(string reason)
        {
        }

        public void CaptureLr2SongDbSyncScanSurface(
            BmsLibraryOptionsSnapshot options,
            IEnumerable<string> rootDirectories,
            SongTableFileCheckResult fileCheckResult)
        {
        }

        public void CaptureLr2SongDbSyncFileDiffFreshnessSnapshot(
            BmsLibraryOptionsSnapshot options,
            SongTableFileCheckResult fileCheckResult,
            string reason)
        {
        }

        public void MarkLr2SongDbSyncIncompleteAfterFileDiffNormalFolderSyncFailure(
            BmsLibraryOptionsSnapshot options,
            SongTableFileCheckResult result)
        {
        }
    }

    private sealed class NoopLibraryFileScanStorageMutationHost : ILibraryFileScanStorageMutationHost
    {
        public IDisposable EnterOwnedStorageWriteLock() => null!;

        public bool TryCreateRemovedStorageOwnerIdentityCharts(
            SongTableFileCheckResult fileCheckResult,
            out List<ChartFile> removedCharts)
        {
            removedCharts = [];
            return false;
        }

        public IResourceHealthInputMutationScope BeginResourceHealthInputMutation() => null!;

        public void BuildMutationResult(
            SongTableFileCheckResult fileCheckResult,
            List<ChartFile> removedCharts,
            bool removedPayloadAvailable,
            bool baseIndexCurrent)
        {
        }

        public void PublishOwnedCollectionChangeNotification()
        {
        }

        public IDisposable SuppressResourceHealthIndexInvalidationIfNeeded() => null!;

        public void ApplyStorageRowsResourceIndexAndOwnedCollectionReplacement(SongTableFileCheckResult fileCheckResult)
        {
        }

        public void ApplyFailureFallback()
        {
        }

        public void DispatchOwnedChartCollectionMutation(string reason)
        {
        }
    }

    private sealed class NoopLibraryMutationDeltaApplyHost : ILibraryMutationDeltaApplyHost
    {
        public void ThrowIfLr2SongDbSyncMutationBlocked(string operationName)
        {
        }

        public IResourceHealthInputMutationScope BeginResourceHealthInputMutation() => null!;

        public void BuildMutationResult(LibraryMutationDelta delta, int baseInputVersion, bool baseIndexCurrent)
        {
        }

        public void PublishOwnedCollectionChangeNotification()
        {
        }

        public IDisposable SuppressResourceHealthIndexInvalidationIfNeeded() => null!;

        public StorageRowsVersionSnapshot ApplyLibraryUnregisterStorageRowsUnsafe() => default;

        public BmsLibraryStateApplyResult ApplyLibraryMutationDeltaToState(LibraryMutationDelta delta) => null!;

        public void ApplyOwnedChartCollectionMutation(StorageRowsVersionSnapshot storageRowsVersion)
        {
        }

        public void CompleteResourceHealthMutation(int targetInputVersion)
        {
        }

        public void SyncLr2NormalFoldersForOwnedMutation(string reason)
        {
        }

        public void ApplyFailureFallback()
        {
        }

        public void DispatchOwnedChartCollectionMutation(string reason)
        {
        }

        public void LogLibraryMutationDeltaPerformance(
            LibraryMutationDelta delta,
            string performanceLogContext,
            LibraryMutationDeltaApplyTimings timings)
        {
        }
    }
}
