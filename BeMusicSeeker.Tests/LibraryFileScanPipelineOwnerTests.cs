using System;
using System.Collections.Generic;
using System.IO;
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
        var callbacks = new RecordingLibraryFileScanPipelineCallbacks();
        var owner = CreateOwner(callbacks);

        SongTableFileCheckResult first = owner.ApplyFileScanDiff(
            new BmsLibraryOptionsSnapshot(),
            [],
            null!,
            null!,
            trackLibraryFileCheckProgress: true,
            reason: "test_first",
            installDestinationCleanupSnapshot: InstallDestinationCleanupSnapshot.Empty);
        SongTableFileCheckResult second = owner.ApplyFileScanDiff(
            new BmsLibraryOptionsSnapshot(),
            null!,
            null!,
            null!,
            trackLibraryFileCheckProgress: true,
            reason: "test_second",
            installDestinationCleanupSnapshot: InstallDestinationCleanupSnapshot.Empty);

        Assert.IsNotNull(first);
        Assert.IsNotNull(second);
        Assert.AreEqual(2, callbacks.EnumerationCompletedCount);
        Assert.AreEqual(2, callbacks.DiffCompletedCount);
        Assert.AreEqual(2, callbacks.PerformanceMessages.Count);
        StringAssert.Contains(callbacks.PerformanceMessages[0], "reason=no_bms_directories");
        StringAssert.Contains(callbacks.PerformanceMessages[1], "reason=no_bms_directories");
    }

    [TestMethod]
    public void ApplyFileScanDiff_IncompletePrefetchSkipsStorageAndReportsWarning()
    {
        var callbacks = new RecordingLibraryFileScanPipelineCallbacks();
        var owner = CreateOwner(callbacks);
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
            reason: "test_incomplete",
            installDestinationCleanupSnapshot: InstallDestinationCleanupSnapshot.Empty);

        Assert.IsNotNull(result);
        Assert.AreEqual(1, callbacks.IncompleteWarningCount);
        Assert.AreEqual("test_incomplete_scan", callbacks.LastIncompleteWarningReason);
        Assert.AreEqual(1, callbacks.EnumerationCompletedCount);
        Assert.AreEqual(1, callbacks.DiffCompletedCount);
    }

    [TestMethod]
    public void ApplyActiveFileScan_NoRootsUsesOwnedApplyRoute()
    {
        var callbacks = new RecordingLibraryFileScanPipelineCallbacks();
        var owner = CreateOwner(callbacks);
        long generation = owner.BeginFileScanRequest(new BmsLibraryOptionsSnapshot(), [], "test_request");

        SongTableFileCheckResult result = owner.ApplyActiveFileScan(
            generation,
            trackLibraryFileCheckProgress: true,
            installDestinationCleanupSnapshot: InstallDestinationCleanupSnapshot.Empty);

        Assert.IsNotNull(result);
        Assert.AreEqual(1, callbacks.EnumerationCompletedCount);
        Assert.AreEqual(1, callbacks.DiffCompletedCount);
        StringAssert.Contains(callbacks.PerformanceMessages[0], "reason=no_bms_directories");
    }

    [TestMethod]
    public void BeginFileScanRequest_RejectsOverlapUntilTerminal()
    {
        var callbacks = new RecordingLibraryFileScanPipelineCallbacks();
        var owner = CreateOwner(callbacks);
        long firstGeneration = owner.BeginFileScanRequest(new BmsLibraryOptionsSnapshot(), [], "test_first");

        Assert.ThrowsException<InvalidOperationException>(
            () => owner.BeginFileScanRequest(new BmsLibraryOptionsSnapshot(), [], "test_overlap"));

        SongTableFileCheckResult result = owner.ApplyActiveFileScan(
            firstGeneration,
            trackLibraryFileCheckProgress: true,
            installDestinationCleanupSnapshot: InstallDestinationCleanupSnapshot.Empty);
        Assert.IsNotNull(result);
        long secondGeneration = owner.BeginFileScanRequest(new BmsLibraryOptionsSnapshot(), [], "test_second");
        Assert.IsTrue(secondGeneration > firstGeneration);
        owner.AbortActiveFileScan(secondGeneration);
    }

    [TestMethod]
    public void AbortActiveFileScan_PreventsApplyAndAllowsNextRequest()
    {
        var callbacks = new RecordingLibraryFileScanPipelineCallbacks();
        var owner = CreateOwner(callbacks);
        long abortedGeneration = owner.BeginFileScanRequest(new BmsLibraryOptionsSnapshot(), [], "test_abort");

        owner.AbortActiveFileScan(abortedGeneration);
        long nextGeneration = owner.BeginFileScanRequest(new BmsLibraryOptionsSnapshot(), [], "test_after_abort");

        Assert.ThrowsException<InvalidOperationException>(
            () => owner.ApplyActiveFileScan(
                abortedGeneration,
                trackLibraryFileCheckProgress: true,
                installDestinationCleanupSnapshot: InstallDestinationCleanupSnapshot.Empty));
        Assert.AreEqual(0, callbacks.EnumerationCompletedCount);
        Assert.AreEqual(0, callbacks.DiffCompletedCount);

        SongTableFileCheckResult result = owner.ApplyActiveFileScan(
            nextGeneration,
            trackLibraryFileCheckProgress: true,
            installDestinationCleanupSnapshot: InstallDestinationCleanupSnapshot.Empty);
        Assert.IsNotNull(result);
        Assert.AreEqual(1, callbacks.EnumerationCompletedCount);
        Assert.AreEqual(1, callbacks.DiffCompletedCount);
    }

    [TestMethod]
    public void ApplyCatalogProjection_ReplacesDeletedAndAddedCatalogItems()
    {
        var keepFile = new BMSFile { path = "keep.bms" };
        var replacedFile = new BMSFile { path = "replace.bms" };
        var replacementFile = new BMSFile { path = "replace.bms" };
        var callbacks = new RecordingLibraryFileScanPipelineCallbacks
        {
            BmsFiles = [keepFile, replacedFile]
        };
        var owner = CreateOwner(callbacks);
        var result = new SongTableFileCheckResult
        {
            NextDirectoryResourceLookupCache = new DirectoryResourceLookupCache()
        };
        result.DeletedPaths.Add(replacedFile.path);
        result.AddedFiles.Add(replacementFile);
        CatalogStorageRowsSnapshot capturedRows = callbacks.CatalogStorageRowsOwner.CaptureSnapshot();
        callbacks.CatalogStorageRowsOwner.ReplaceBmsRows([new BMSFile { path = "live-replacement.bms" }]);

        owner.ApplyCatalogProjection(result, [], capturedRows);

        Assert.AreEqual(2, result.NextFiles.Count);
        Assert.AreSame(keepFile, result.NextFiles[0]);
        Assert.AreSame(replacementFile, result.NextFiles[1]);
    }

    [TestMethod]
    public void CatalogStorageRowsSnapshot_CapturesBothKindsAndRemainsStableAcrossReplacement()
    {
        var firstBms = new BMSFile { path = "first.bms" };
        var secondBms = new BMSFile { path = "second.bms" };
        var firstBmson = new LR2SongDBExtended.bmson_song { path = "first.bmson" };
        var secondBmson = new LR2SongDBExtended.bmson_song { path = "second.bmson" };
        var replacementBms = new BMSFile { path = "replacement.bms" };
        var replacementBmson = new LR2SongDBExtended.bmson_song { path = "replacement.bmson" };
        var owner = new CatalogStorageRowsOwner();

        CatalogStorageRowsSnapshot captured = owner.ReplaceRowsAndCaptureSnapshot(
            [firstBms, secondBms],
            [firstBmson, secondBmson]);
        owner.ReplaceBmsRows([replacementBms]);
        owner.ReplaceBmsonRows([replacementBmson]);

        Assert.AreEqual(1, captured.BmsRowsVersion);
        Assert.AreEqual(1, captured.BmsonRowsVersion);
        CollectionAssert.AreEqual(new[] { firstBms, secondBms }, captured.BmsRows.ToArray());
        CollectionAssert.AreEqual(new[] { firstBmson, secondBmson }, captured.BmsonRows.ToArray());

        CatalogStorageRowsSnapshot current = owner.CaptureSnapshot();
        Assert.AreEqual(2, current.BmsRowsVersion);
        Assert.AreEqual(2, current.BmsonRowsVersion);
        CollectionAssert.AreEqual(new[] { replacementBms }, current.BmsRows.ToArray());
        CollectionAssert.AreEqual(new[] { replacementBmson }, current.BmsonRows.ToArray());
    }

    [TestMethod]
    public void ApplyCatalogProjection_ClearsStaleInstallDestinationForCurrentOwner()
    {
        var keepFile = new BMSFile { path = "keep.bms" };
        var callbacks = new RecordingLibraryFileScanPipelineCallbacks
        {
            BmsFiles = [keepFile]
        };
        var owner = CreateOwner(callbacks);
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

        owner.ApplyCatalogProjection(result, [chart], callbacks.CatalogStorageRowsOwner.CaptureSnapshot());

        LibraryInstallDestinationChange change = result.MutationDelta.UpdatedInstallDestinations.Single();
        Assert.AreSame(chart, change.Chart);
        Assert.IsNull(change.NewInstallDestination);
        Assert.IsTrue(change.ClearInstallDestinationState);
        Assert.IsTrue(result.MutationDelta.InvalidateInstalledDirectoryIndex);
        Assert.IsTrue(result.MutationDelta.ClearDuplicatedCache);
    }

    [TestMethod]
    public void FileScanCatalogResidualEvent_CapturesImmutableInstallDestinationFacts()
    {
        var bmsFile = new BMSFile
        {
            path = "C:\\Library\\chart.bms"
        };
        var delta = new LibraryMutationDelta
        {
            InvalidateInstalledDirectoryIndex = true,
            ClearDuplicatedCache = true
        };
        delta.UpdatedInstallDestinations.Add(new LibraryInstallDestinationChange
        {
            NewInstallDestination = "C:\\Install\\chart",
            Chart = ChartFileProjection.WithPackageState(
                ChartFileProjection.FromBmsFile(bmsFile, includeWarningSnapshot: false),
                "C:\\Install\\chart",
                string.Empty,
                string.Empty,
                [])
        });

        FileScanCatalogResidualEvent residual = FileScanCatalogResidualEvent.Create(delta, "residual_test");
        bmsFile.path = "C:\\Library\\renamed.bms";

        Assert.AreEqual("residual_test", residual.Reason);
        Assert.IsTrue(residual.InvalidateInstalledDirectoryIndex);
        Assert.IsTrue(residual.ClearDuplicatedCache);
        Assert.AreEqual("C:\\Library\\chart.bms", residual.InstallDestinationChangedCharts.Single().Path);
        Assert.AreEqual("C:\\Install\\chart", residual.InstallDestinationChangedCharts.Single().InstallDestination);
        Assert.IsNull(residual.InstallDestinationChangedCharts.Single().GetBmsStorageOwner());
    }

    [TestMethod]
    public void FileScanCatalogResidualEvent_RejectsUnsupportedGenericMutation()
    {
        var delta = new LibraryMutationDelta();
        delta.ChartPathChanges.Add(new LibraryChartPathChange
        {
            OldPath = "C:\\Library\\old.bms",
            NewPath = "C:\\Library\\new.bms"
        });

        Assert.ThrowsException<InvalidOperationException>(
            () => FileScanCatalogResidualEvent.Create(delta, "residual_test"));
    }

    [TestMethod]
    public void FileScanCatalogResidualEvent_RejectsPackageEntryMutation()
    {
        var delta = new LibraryMutationDelta();
        delta.UpdatedInstallDestinations.Add(new LibraryInstallDestinationChange
        {
            Entry = new PackageChartEntry(
                ChartFileProjection.FromIdentitySnapshot(
                    ChartFileKind.Bms,
                    "C:\\Library\\chart.bms",
                    string.Empty,
                    string.Empty)),
            NewInstallDestination = "C:\\Install\\chart"
        });

        Assert.ThrowsException<InvalidOperationException>(
            () => FileScanCatalogResidualEvent.Create(delta, "residual_test"));
    }

    [TestMethod]
    public void FileScanCatalogResidualEvent_RejectsUnsupportedParentInvalidation()
    {
        var delta = new LibraryMutationDelta
        {
            InvalidateParentFolderCache = true
        };

        Assert.ThrowsException<InvalidOperationException>(
            () => FileScanCatalogResidualEvent.Create(delta, "residual_test"));
    }

    [TestMethod]
    public void InstallDestinationCleanupSnapshot_DetachesChartProjectionFromMutableSource()
    {
        var bmsFile = new BMSFile
        {
            path = "C:\\Library\\chart.bms"
        };
        ChartFile chart = ChartFileProjection.WithPackageState(
            ChartFileProjection.FromBmsFile(bmsFile, includeWarningSnapshot: false),
            "C:\\Install\\chart",
            string.Empty,
            string.Empty,
            []);

        InstallDestinationCleanupSnapshot snapshot = InstallDestinationCleanupSnapshot.FromCharts([chart]);
        bmsFile.path = "C:\\Library\\renamed.bms";

        ChartFile capturedChart = snapshot.Charts.Single();
        Assert.AreEqual("C:\\Library\\chart.bms", capturedChart.Path);
        Assert.IsNull(capturedChart.GetBmsStorageOwner());
        Assert.AreEqual("C:\\Install\\chart", capturedChart.InstallDestination);
    }

    [TestMethod]
    public void ApplyCatalogProjection_ClearsStaleInstallDestinationFromDetachedBmsSnapshot()
    {
        var keepFile = new BMSFile { path = "C:\\Library\\chart.bms" };
        var callbacks = new RecordingLibraryFileScanPipelineCallbacks
        {
            BmsFiles = [keepFile]
        };
        var owner = CreateOwner(callbacks);
        var result = new SongTableFileCheckResult
        {
            NextDirectoryResourceLookupCache = new DirectoryResourceLookupCache()
        };
        ChartFile chart = ChartFileProjection.WithPackageState(
            ChartFileProjection.FromBmsFile(keepFile, includeWarningSnapshot: false),
            "C:\\Install\\stale",
            string.Empty,
            string.Empty,
            []);
        InstallDestinationCleanupSnapshot snapshot = InstallDestinationCleanupSnapshot.FromCharts([chart]);

        owner.ApplyCatalogProjection(result, snapshot.Charts, callbacks.CatalogStorageRowsOwner.CaptureSnapshot());

        LibraryInstallDestinationChange change = result.MutationDelta.UpdatedInstallDestinations.Single();
        Assert.AreEqual("C:\\Library\\chart.bms", change.Chart.Path);
        Assert.IsNull(change.NewInstallDestination);
        Assert.IsTrue(change.ClearInstallDestinationState);
    }

    [TestMethod]
    public void ApplyCatalogProjection_ClearsStaleInstallDestinationFromDetachedBmsonSnapshot()
    {
        var bmsonSong = new LR2SongDBExtended.bmson_song
        {
            path = "C:\\Library\\chart.bmson",
            md5 = "bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb"
        };
        var callbacks = new RecordingLibraryFileScanPipelineCallbacks
        {
            BmsonSongs = [bmsonSong]
        };
        var owner = CreateOwner(callbacks);
        var result = new SongTableFileCheckResult
        {
            NextDirectoryResourceLookupCache = new DirectoryResourceLookupCache()
        };
        ChartFile chart = ChartFileProjection.WithPackageState(
            ChartFileProjection.FromBmsonSong(bmsonSong, includeWarningSnapshot: false),
            "C:\\Install\\stale",
            string.Empty,
            string.Empty,
            []);
        InstallDestinationCleanupSnapshot snapshot = InstallDestinationCleanupSnapshot.FromCharts([chart]);

        owner.ApplyCatalogProjection(result, snapshot.Charts, callbacks.CatalogStorageRowsOwner.CaptureSnapshot());

        LibraryInstallDestinationChange change = result.MutationDelta.UpdatedInstallDestinations.Single();
        Assert.AreEqual("C:\\Library\\chart.bmson", change.Chart.Path);
        Assert.IsNull(change.NewInstallDestination);
        Assert.IsTrue(change.ClearInstallDestinationState);
    }

    [TestMethod]
    public void ApplyCatalogProjection_DetachedBmsSnapshotDoesNotMatchSamePathBmsonOwner()
    {
        string sharedPath = "C:\\Library\\same-path.chart";
        var bmsonSong = new LR2SongDBExtended.bmson_song
        {
            path = sharedPath,
            md5 = "bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb"
        };
        var callbacks = new RecordingLibraryFileScanPipelineCallbacks
        {
            BmsonSongs = [bmsonSong]
        };
        var owner = CreateOwner(callbacks);
        var result = new SongTableFileCheckResult
        {
            NextDirectoryResourceLookupCache = new DirectoryResourceLookupCache()
        };
        var bmsFile = new BMSFile { path = sharedPath };
        ChartFile bmsChart = ChartFileProjection.WithPackageState(
            ChartFileProjection.FromBmsFile(bmsFile, includeWarningSnapshot: false),
            "C:\\Install\\stale",
            string.Empty,
            string.Empty,
            []);
        InstallDestinationCleanupSnapshot snapshot = InstallDestinationCleanupSnapshot.FromCharts([bmsChart]);

        owner.ApplyCatalogProjection(result, snapshot.Charts, callbacks.CatalogStorageRowsOwner.CaptureSnapshot());

        Assert.AreEqual(0, result.MutationDelta.UpdatedInstallDestinations.Count);
    }

    [TestMethod]
    public void ApplyCatalogStorageReplacement_UsesCatalogOwnerAndPublishesReceipt()
    {
        var keptFile = new BMSFile { path = "keep.bms" };
        var addedFile = new BMSFile { path = "added.bms" };
        var callbacks = new RecordingLibraryFileScanPipelineCallbacks
        {
            BmsFiles = [keptFile]
        };
        var owner = CreateOwner(callbacks);
        var result = new SongTableFileCheckResult
        {
            HasDbDiff = true
        };
        result.NextFiles.AddRange([keptFile, addedFile]);
        result.AddedFiles.Add(addedFile);

        owner.ApplyCatalogStorageReplacement(
            result,
            "test_storage_replacement",
            callbacks.CatalogStorageRowsOwner.CaptureSnapshot());

        Assert.IsNotNull(callbacks.LastCatalogReplacement);
        Assert.IsTrue(callbacks.LastCatalogReplacement.Receipt.Applied);
        Assert.AreEqual(2, callbacks.CatalogStorageRowsOwner.CaptureSnapshot().BmsRows.Count);
    }

    [TestMethod]
    public void ApplyCatalogStorageReplacement_WhenExpectedRowsChangedPublishesFailure()
    {
        var currentFile = new BMSFile { path = "current.bms" };
        var replacementFile = new BMSFile { path = "replacement.bms" };
        var callbacks = new RecordingLibraryFileScanPipelineCallbacks
        {
            BmsFiles = [currentFile]
        };
        var owner = CreateOwner(callbacks);
        CatalogStorageRowsSnapshot expectedRows = callbacks.CatalogStorageRowsOwner.CaptureSnapshot();
        callbacks.CatalogStorageRowsOwner.ReplaceBmsRows([replacementFile]);
        var result = new SongTableFileCheckResult
        {
            HasDbDiff = true
        };
        result.NextFiles.Add(replacementFile);
        result.AddedFiles.Add(replacementFile);

        Assert.ThrowsException<InvalidOperationException>(
            () => owner.ApplyCatalogStorageReplacement(result, "test_storage_conflict", expectedRows));

        Assert.IsNotNull(callbacks.LastCatalogReplacementFailure);
        Assert.IsNull(callbacks.LastCatalogReplacement);
        Assert.AreEqual(
            callbacks.CatalogStorageRowsOwner.CaptureSnapshot().BmsRowsVersion,
            callbacks.LastCatalogReplacementFailure.Request.PreviousBmsRowsVersion);
    }

    private static LibraryFileScanPipelineOwner CreateOwner(RecordingLibraryFileScanPipelineCallbacks callbacks)
    {
        string directoryPath = Path.Combine(Path.GetTempPath(), nameof(LibraryFileScanPipelineOwnerTests), Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directoryPath);
        string songDbPath = Path.Combine(directoryPath, "song.db");
        using (new LR2SongDBExtended(songDbPath))
        {
        }
        var library = new BMSLibrary(songDbPath);
        var dbGateway = new BmsLibraryDbGateway(songDbPath);
        var storageRowsOwner = new CatalogStorageRowsOwner();
        var ownedCollectionOwner = new CatalogOwnedCollectionOwner();
        ownedCollectionOwner.EnsureCurrent(storageRowsOwner);
        callbacks.CatalogStorageRowsOwner = storageRowsOwner;
        callbacks.CatalogOwnedCollectionOwner = ownedCollectionOwner;
        storageRowsOwner.ReplaceBmsRows([.. callbacks.BmsFiles]);
        storageRowsOwner.ReplaceBmsonRows([.. callbacks.BmsonSongs]);
        var catalogMutationOwner = new CatalogMutationOwner(storageRowsOwner, ownedCollectionOwner, dbGateway);
        var catalogChartInfoOwner = new CatalogChartInfoOwner(
            _ => { },
            () => false,
            (_, _) => false,
            null,
            _ => { });
        catalogChartInfoOwner.ConfigureWorkflow(
            dbGateway,
            catalogMutationOwner,
            storageRowsOwner,
            ownedCollectionOwner,
            () => new BmsLibraryOptionsSnapshot(),
            _ => { },
            _ => { });
        var resourceHealthOwner = new ResourceHealthIndexOwner(
            new BmsLibraryMaintenanceService(),
            _ => { },
            () => new ResourceHealthIndexCurrentVersion(
                new StorageRowsVersionSnapshot(0, 0),
                0,
                0));
        return new LibraryFileScanPipelineOwner(
            dbGateway,
            storageRowsOwner,
            new BmsLibraryDialogService(),
            () => false,
            callbacks.ReportLibraryInitializationProgress,
            callbacks.CompleteLibraryFileEnumerationProgress,
            callbacks.CompleteLibraryFileDiffProgress,
            callbacks.LogInstallPerformance,
            callbacks.LogInstallPerformanceWarn,
            callbacks.LogEverythingScan,
            callbacks.LogStartupMemoryCheckpoint,
            callbacks.GetDisplayedExceptionMessage,
            callbacks.QueueEverythingFallbackWarning,
            callbacks.QueueFileScanSkippedIncompleteWarning,
            callbacks.QueueEmptyScanWithExistingDbWarning,
            callbacks.DispatchWarningPresentationChanged,
            library.Lr2Synchronization,
            catalogMutationOwner,
            catalogChartInfoOwner,
            resourceHealthOwner,
            callbacks.PublishCatalogReplacement,
            callbacks.PublishCatalogReplacementFailure,
            callbacks.PublishCatalogResidual,
            new BmsLibraryInitializationService());
    }

    private sealed class RecordingLibraryFileScanPipelineCallbacks
    {
        public IReadOnlyList<BMSFile> BmsFiles { get; set; } = [];

        public IReadOnlyList<LR2SongDBExtended.bmson_song> BmsonSongs { get; set; } = [];

        public int EnumerationCompletedCount { get; private set; }

        public int DiffCompletedCount { get; private set; }

        public int IncompleteWarningCount { get; private set; }

        public string LastIncompleteWarningReason { get; private set; } = string.Empty;

        public List<string> PerformanceMessages { get; } = [];

        public List<string> EverythingMessages { get; } = [];

        public CatalogStorageRowsOwner CatalogStorageRowsOwner { get; set; } = null!;

        public CatalogOwnedCollectionOwner CatalogOwnedCollectionOwner { get; set; } = null!;

        public FileScanCatalogReplacementEvent LastCatalogReplacement { get; private set; } = null!;

        public FileScanCatalogReplacementFailureEvent LastCatalogReplacementFailure { get; private set; } = null!;

        public FileScanCatalogResidualEvent LastCatalogResidual { get; private set; } = null!;

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

        public void PublishCatalogReplacement(FileScanCatalogReplacementEvent replacementEvent)
        {
            LastCatalogReplacement = replacementEvent;
        }

        public void PublishCatalogReplacementFailure(FileScanCatalogReplacementFailureEvent failureEvent)
        {
            LastCatalogReplacementFailure = failureEvent;
        }

        public void PublishCatalogResidual(FileScanCatalogResidualEvent residualEvent)
        {
            LastCatalogResidual = residualEvent;
        }

        public void DispatchWarningPresentationChanged(string reason)
        {
        }

    }

}
