using System;
using System.Collections.Generic;
using System.Linq;
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

    [TestMethod]
    public void ApplyActiveFileScan_NoRootsUsesOwnedApplyRoute()
    {
        var host = new RecordingLibraryFileScanPipelineHost();
        var owner = CreateOwner(host);
        long generation = owner.BeginFileScanRequest(new BmsLibraryOptionsSnapshot(), [], "test_request");

        SongTableFileCheckResult result = owner.ApplyActiveFileScan(generation, trackLibraryFileCheckProgress: true);

        Assert.IsNotNull(result);
        Assert.AreEqual(1, host.EnumerationCompletedCount);
        Assert.AreEqual(1, host.DiffCompletedCount);
        Assert.AreEqual(1, host.MutationBlockedChecks);
        Assert.AreEqual("ApplyLibraryFileScanDiff", host.LastMutationBlockedOperation);
        StringAssert.Contains(host.PerformanceMessages[0], "reason=no_bms_directories");
    }

    [TestMethod]
    public void BeginFileScanRequest_RejectsOverlapUntilTerminal()
    {
        var host = new RecordingLibraryFileScanPipelineHost();
        var owner = CreateOwner(host);
        long firstGeneration = owner.BeginFileScanRequest(new BmsLibraryOptionsSnapshot(), [], "test_first");

        Assert.ThrowsException<InvalidOperationException>(
            () => owner.BeginFileScanRequest(new BmsLibraryOptionsSnapshot(), [], "test_overlap"));

        SongTableFileCheckResult result = owner.ApplyActiveFileScan(firstGeneration, trackLibraryFileCheckProgress: true);
        Assert.IsNotNull(result);
        long secondGeneration = owner.BeginFileScanRequest(new BmsLibraryOptionsSnapshot(), [], "test_second");
        Assert.IsTrue(secondGeneration > firstGeneration);
        owner.AbortActiveFileScan(secondGeneration);
    }

    [TestMethod]
    public void AbortActiveFileScan_PreventsApplyAndAllowsNextRequest()
    {
        var host = new RecordingLibraryFileScanPipelineHost();
        var owner = CreateOwner(host);
        long abortedGeneration = owner.BeginFileScanRequest(new BmsLibraryOptionsSnapshot(), [], "test_abort");

        owner.AbortActiveFileScan(abortedGeneration);
        long nextGeneration = owner.BeginFileScanRequest(new BmsLibraryOptionsSnapshot(), [], "test_after_abort");

        Assert.ThrowsException<InvalidOperationException>(
            () => owner.ApplyActiveFileScan(abortedGeneration, trackLibraryFileCheckProgress: true));
        Assert.AreEqual(0, host.EnumerationCompletedCount);
        Assert.AreEqual(0, host.DiffCompletedCount);

        SongTableFileCheckResult result = owner.ApplyActiveFileScan(nextGeneration, trackLibraryFileCheckProgress: true);
        Assert.IsNotNull(result);
        Assert.AreEqual(1, host.EnumerationCompletedCount);
        Assert.AreEqual(1, host.DiffCompletedCount);
    }

    [TestMethod]
    public void ApplyCatalogProjection_ReplacesDeletedAndAddedCatalogItems()
    {
        var keepFile = new BMSFile { path = "keep.bms" };
        var replacedFile = new BMSFile { path = "replace.bms" };
        var replacementFile = new BMSFile { path = "replace.bms" };
        var host = new RecordingLibraryFileScanPipelineHost
        {
            BmsFiles = [keepFile, replacedFile]
        };
        var owner = CreateOwner(host);
        var result = new SongTableFileCheckResult
        {
            NextDirectoryResourceLookupCache = new DirectoryResourceLookupCache()
        };
        result.DeletedPaths.Add(replacedFile.path);
        result.AddedFiles.Add(replacementFile);

        owner.ApplyCatalogProjection(result, []);

        Assert.AreEqual(2, result.NextFiles.Count);
        Assert.AreSame(keepFile, result.NextFiles[0]);
        Assert.AreSame(replacementFile, result.NextFiles[1]);
    }

    [TestMethod]
    public void ApplyCatalogProjection_ClearsStaleInstallDestinationForCurrentOwner()
    {
        var keepFile = new BMSFile { path = "keep.bms" };
        var host = new RecordingLibraryFileScanPipelineHost
        {
            BmsFiles = [keepFile]
        };
        var owner = CreateOwner(host);
        var result = new SongTableFileCheckResult
        {
            NextDirectoryResourceLookupCache = new DirectoryResourceLookupCache()
        };
        ChartFile chart = ChartFileProjection.WithPackageState(
            ChartFileProjection.FromBmsFile(keepFile, includeWarningSnapshot: false),
            "stale-install-destination",
            string.Empty,
            string.Empty,
            []);

        owner.ApplyCatalogProjection(result, [chart]);

        LibraryInstallDestinationChange change = result.MutationDelta.UpdatedInstallDestinations.Single();
        Assert.AreSame(chart, change.Chart);
        Assert.IsNull(change.NewInstallDestination);
        Assert.IsTrue(change.ClearInstallDestinationState);
        Assert.IsTrue(result.MutationDelta.InvalidateInstalledDirectoryIndex);
        Assert.IsTrue(result.MutationDelta.ClearDuplicatedCache);
    }

    private static LibraryFileScanPipelineOwner CreateOwner(ILibraryFileScanPipelineHost host)
    {
        return new LibraryFileScanPipelineOwner(
            host,
            (ILibraryFileScanLr2FolderHost)host,
            new BmsLibraryInitializationService(),
            () => new LibraryMutationDeltaApplyCoordinator(new NoopLibraryMutationDeltaApplyHost()));
    }

    private sealed class RecordingLibraryFileScanPipelineHost : ILibraryFileScanPipelineHost, ILibraryFileScanLr2FolderHost
    {
        public BmsLibraryDbGateway DbGateway => null!;

        public IReadOnlyList<BMSFile> BmsFiles { get; set; } = [];

        public IReadOnlyList<LR2SongDBExtended.bmson_song> BmsonSongs { get; set; } = [];

        public IBmsLibraryDialogService DialogService => null!;

        public bool EverythingScanLoggingEnabled => false;

        public int EnumerationCompletedCount { get; private set; }

        public int DiffCompletedCount { get; private set; }

        public int MutationBlockedChecks { get; private set; }

        public string LastMutationBlockedOperation { get; private set; } = string.Empty;

        public int IncompleteWarningCount { get; private set; }

        public string LastIncompleteWarningReason { get; private set; } = string.Empty;

        public List<string> PerformanceMessages { get; } = [];

        public List<string> EverythingMessages { get; } = [];

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
            EverythingMessages.Add(message);
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

        public void ApplyFileScanStorageMutation(SongTableFileCheckResult fileCheckResult, string reason)
        {
        }

        public List<ChartFile> CreateCurrentInstallDestinationCleanupCharts()
        {
            return [];
        }

        public BmsLibraryOptionsSnapshot CurrentOptionsSnapshot => new();

        public List<string> CreateLr2SongDbSyncBuiltinFolderSourceDirectories(BmsLibraryOptionsSnapshot options)
        {
            return [];
        }

        public List<string> CreateLr2SongDbSyncLr2FolderPruneDirectories(
            IEnumerable<string> rootDirectories,
            IEnumerable<string> builtinSourceDirectories,
            BmsLibraryOptionsSnapshot options)
        {
            return [];
        }

        public Lr2FolderFileDbSyncResult SyncLr2FolderFileRows(
            BmsLibraryOptionsSnapshot options,
            Lr2SongDbSyncRequest request,
            string reason,
            string logName,
            bool allowPrune = true,
            IReadOnlyCollection<string> pruneExcludedDirectories = null!,
            IReadOnlyCollection<string> pruneExcludedPaths = null!,
            bool scopeReadLr2FolderRowsOnly = false,
            bool updateParentDirectoryRowsForPreservedItems = true)
        {
            return null!;
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

        public StorageRowsVersionSnapshot ApplyCatalogStorageRowsRemoval() => default;

        public BmsLibraryStateApplyResult ApplyLibraryMutationDeltaToState(LibraryMutationDelta delta) => null!;

        public void ApplyCatalogOwnedCollectionMutation(StorageRowsVersionSnapshot storageRowsVersion)
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
