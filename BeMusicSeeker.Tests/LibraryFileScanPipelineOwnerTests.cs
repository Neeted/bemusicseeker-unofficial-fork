using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using BeMusicSeeker.Models;
using BeMusicSeeker.Models.BmsLibraryInternal;
using BeMusicSeeker.Models.LR2;
using BeMusicSeeker.Models.Utils;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace BeMusicSeeker.Tests;

[TestClass]
// This fixture changes the process-wide localization resource state through TestResourceInitializer.
[DoNotParallelize]
public sealed class LibraryFileScanPipelineOwnerTests
{
    [TestMethod]
    public void CatalogChartInfoOwner_PublishWarningPresentationChangedPublishesWarningEvent()
    {
        var callbacks = new RecordingLibraryFileScanPipelineCallbacks();
        CreateOwner(callbacks);

        callbacks.CatalogChartInfoOwner.PublishWarningPresentationChanged("test_warning");

        CatalogChartInfoOwnerEvent ownerEvent = callbacks.CatalogChartInfoEvents.Single();
        Assert.AreEqual(CatalogChartInfoOwnerEventKind.WarningPresentationChanged, ownerEvent.Kind);
        Assert.AreEqual("test_warning", ownerEvent.Reason);
    }

    [TestMethod]
    public void ApplyFileScanDiff_InlineChartInfoFailurePublishesOwnerWarningBeforeResidual()
    {
        TestResourceInitializer.EnsureJapaneseResources();
        string directoryPath = Path.Combine(Path.GetTempPath(), nameof(LibraryFileScanPipelineOwnerTests), Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directoryPath);
        try
        {
            string bmsPath = Path.Combine(directoryPath, "bad.bms");
            File.WriteAllText(bmsPath, "#PLAYER 1\r\n#TITLE Bad\r\n#00111:01\r\n");
            var callbacks = new RecordingLibraryFileScanPipelineCallbacks();
            var owner = CreateOwner(callbacks);
            Lr2FolderFileDiffPreparationResult scan = owner.ApplyFileScanDiff(
                new BmsLibraryOptionsSnapshot(),
                [directoryPath],
                new ChartScanPrefetchInfo
                {
                    ScanResult = new ChartScanExecutionResult
                    {
                        Success = true,
                        Result = new ChartScanResult
                        {
                            ChartFilePaths = new HashSet<string>([bmsPath], StringComparer.Ordinal),
                            ChartDirectories = new HashSet<string>([directoryPath], StringComparer.OrdinalIgnoreCase)
                        }
                    }
                },
                null!,
                trackLibraryFileCheckProgress: true,
                reason: "test_inline_chart_info_failure",
                installDestinationCleanupSnapshot: InstallDestinationCleanupSnapshot.Empty);
            SongTableFileCheckResult result = scan.FileCheckResult;

            Assert.AreEqual(1, result.InlineChartInfoParseFailedCount, "parse_failed");
            Assert.AreEqual(1, result.InlineChartInfoFailurePersistedCount, "failure_persisted");
            CollectionAssert.AreEqual(
                new[] { "WarningPresentationChanged", "Residual" },
                callbacks.EventOrder.ToArray());
            CatalogChartInfoOwnerEvent warning = callbacks.CatalogChartInfoEvents.Single();
            Assert.AreEqual(CatalogChartInfoOwnerEventKind.WarningPresentationChanged, warning.Kind);
            Assert.AreEqual("file_diff_inline_chart_info_parse_failure", warning.Reason);
            Assert.IsNotNull(callbacks.LastCatalogResidual);
        }
        finally
        {
            if (Directory.Exists(directoryPath))
            {
                Directory.Delete(directoryPath, recursive: true);
            }
        }
    }

    [TestMethod]
    public void ApplyFileScanDiff_LatePostLeaseObserverFailureDiscardsCommittedReceipt()
    {
        TestResourceInitializer.EnsureJapaneseResources();
        string directoryPath = Path.Combine(Path.GetTempPath(), nameof(LibraryFileScanPipelineOwnerTests), Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directoryPath);
        try
        {
            string bmsPath = Path.Combine(directoryPath, "committed.bms");
            File.WriteAllText(bmsPath, "#PLAYER 1\r\n#TITLE Committed\r\n#BPM 120\r\n#00111:01\r\n");
            var callbacks = new RecordingLibraryFileScanPipelineCallbacks();
            var owner = CreateOwner(callbacks, lr2ModeEnabled: true);
            var injectedException = new InvalidOperationException("injected post-lease observer failure");
            int initialOwnedCollectionVersion = callbacks.CatalogOwnedCollectionOwner.CollectionVersion;
            int initialSynchronizationOwnedCollectionVersion =
                callbacks.Lr2Synchronization.CreateLr2SongDbSyncInput().OwnedChartCollectionVersion;

            InvalidOperationException thrown = Assert.ThrowsException<InvalidOperationException>(
                () => owner.ApplyFileScanDiff(
                    new BmsLibraryOptionsSnapshot
                    {
                        OperationModeLR2DB = true
                    },
                    [directoryPath],
                    new ChartScanPrefetchInfo
                    {
                        ScanResult = new ChartScanExecutionResult
                        {
                            Success = true,
                            Result = new ChartScanResult
                            {
                                ChartFilePaths = new HashSet<string>([bmsPath], StringComparer.Ordinal),
                                ChartDirectories = new HashSet<string>([directoryPath], StringComparer.OrdinalIgnoreCase)
                            }
                        }
                    },
                    null!,
                    trackLibraryFileCheckProgress: true,
                    reason: "test_late_receipt_failure",
                    installDestinationCleanupSnapshot: InstallDestinationCleanupSnapshot.Empty,
                    postLeaseEffectObserver: _ => throw injectedException));

            Assert.AreSame(injectedException, thrown);
            Assert.IsNotNull(callbacks.LastCatalogReplacement);
            int committedOwnedCollectionVersion = callbacks.LastCatalogReplacement.Receipt.OwnedCollectionVersion;
            Assert.AreEqual(initialOwnedCollectionVersion + 1, committedOwnedCollectionVersion);
            Assert.AreEqual(committedOwnedCollectionVersion, callbacks.CatalogOwnedCollectionOwner.CollectionVersion);
            Assert.IsNull(callbacks.Lr2Synchronization.CommittedPathReceipt);
            Lr2SongDbSyncInput input = callbacks.Lr2Synchronization.CreateLr2SongDbSyncInput();
            Assert.AreEqual(initialSynchronizationOwnedCollectionVersion, input.OwnedChartCollectionVersion);
            Assert.IsNull(callbacks.Lr2Synchronization.TakeLr2SongDbSyncCommittedPathReceipt(input, "test_late_receipt_failure_first_take"));
            Assert.IsNull(callbacks.Lr2Synchronization.TakeLr2SongDbSyncCommittedPathReceipt(input, "test_late_receipt_failure_second_take"));
            Assert.AreEqual(committedOwnedCollectionVersion, callbacks.CatalogOwnedCollectionOwner.CollectionVersion);
        }
        finally
        {
            if (Directory.Exists(directoryPath))
            {
                Directory.Delete(directoryPath, recursive: true);
            }
        }
    }

    [TestMethod]
    public void ApplyActiveFileScan_NoDiffDiscardsCommittedReceiptWithoutAdvancingVersion()
    {
        TestResourceInitializer.EnsureJapaneseResources();
        string directoryPath = Path.Combine(Path.GetTempPath(), nameof(LibraryFileScanPipelineOwnerTests), Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directoryPath);
        try
        {
            string bmsPath = Path.Combine(directoryPath, "unchanged.bms");
            File.WriteAllText(bmsPath, "#PLAYER 1\r\n#TITLE Unchanged\r\n#BPM 120\r\n#00111:01\r\n");
            IChartFileScanner chartFileScanner = CapturedChartFileScanner.FromFixture(
                [bmsPath],
                new Dictionary<string, IEnumerable<string>>(StringComparer.OrdinalIgnoreCase)
                {
                    [directoryPath] = []
                },
                [directoryPath]);
            var callbacks = new RecordingLibraryFileScanPipelineCallbacks();
            var owner = CreateOwner(callbacks, lr2ModeEnabled: true, chartFileScanner: chartFileScanner);
            BmsLibraryOptionsSnapshot options = new() { OperationModeLR2DB = true };
            int initialOwnedCollectionVersion = callbacks.CatalogOwnedCollectionOwner.CollectionVersion;
            int initialSynchronizationOwnedCollectionVersion =
                callbacks.Lr2Synchronization.CreateLr2SongDbSyncInput().OwnedChartCollectionVersion;

            long firstGeneration = owner.BeginFileScanRequest(options, [directoryPath], "test_initial_commit");
            Lr2FolderFileDiffPreparationResult first = owner.ApplyActiveFileScan(
                firstGeneration,
                trackLibraryFileCheckProgress: true,
                installDestinationCleanupSnapshot: InstallDestinationCleanupSnapshot.Empty);
            Assert.IsTrue(first.FileCheckResult.HasDbDiff);
            Assert.IsNotNull(callbacks.Lr2Synchronization.CommittedPathReceipt);
            int committedOwnedCollectionVersion = callbacks.CatalogOwnedCollectionOwner.CollectionVersion;
            Assert.AreEqual(initialOwnedCollectionVersion + 1, committedOwnedCollectionVersion);

            long secondGeneration = owner.BeginFileScanRequest(options, [directoryPath], "test_no_diff");
            Lr2FolderFileDiffPreparationResult second = owner.ApplyActiveFileScan(
                secondGeneration,
                trackLibraryFileCheckProgress: true,
                installDestinationCleanupSnapshot: InstallDestinationCleanupSnapshot.Empty);

            Assert.IsFalse(second.FileCheckResult.HasDbDiff);
            Assert.IsNotNull(callbacks.LastCatalogReplacement);
            Assert.IsFalse(callbacks.LastCatalogReplacement.Receipt.Applied);
            Assert.AreEqual(committedOwnedCollectionVersion, callbacks.CatalogOwnedCollectionOwner.CollectionVersion);
            Assert.AreEqual(
                initialSynchronizationOwnedCollectionVersion,
                callbacks.Lr2Synchronization.CreateLr2SongDbSyncInput().OwnedChartCollectionVersion);
            Assert.IsNull(callbacks.Lr2Synchronization.CommittedPathReceipt);
            Assert.IsNull(callbacks.Lr2Synchronization.TakeLr2SongDbSyncCommittedPathReceipt(
                callbacks.Lr2Synchronization.CreateLr2SongDbSyncInput(),
                "test_no_diff_first_take"));
            Assert.IsNull(callbacks.Lr2Synchronization.TakeLr2SongDbSyncCommittedPathReceipt(
                callbacks.Lr2Synchronization.CreateLr2SongDbSyncInput(),
                "test_no_diff_second_take"));
        }
        finally
        {
            if (Directory.Exists(directoryPath))
            {
                Directory.Delete(directoryPath, recursive: true);
            }
        }
    }

    [TestMethod]
    public void ApplyActiveFileScan_IncompletePrefetchDiscardsCommittedReceiptWithoutAdvancingVersion()
    {
        TestResourceInitializer.EnsureJapaneseResources();
        string directoryPath = Path.Combine(Path.GetTempPath(), nameof(LibraryFileScanPipelineOwnerTests), Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directoryPath);
        try
        {
            string bmsPath = Path.Combine(directoryPath, "committed.bms");
            File.WriteAllText(bmsPath, "#PLAYER 1\r\n#TITLE Committed\r\n#BPM 120\r\n#00111:01\r\n");
            ChartScanExecutionResult completeScan = new()
            {
                Success = true,
                IsComplete = true,
                Result = new ChartScanResult
                {
                    ChartFilePaths = new HashSet<string>([bmsPath], StringComparer.Ordinal),
                    ChartDirectories = new HashSet<string>([directoryPath], StringComparer.OrdinalIgnoreCase)
                }
            };
            ChartScanExecutionResult incompleteScan = new()
            {
                Success = false,
                IsComplete = false,
                ErrorReason = "bridge_dll_not_found:test_incomplete_after_commit"
            };
            var callbacks = new RecordingLibraryFileScanPipelineCallbacks();
            var owner = CreateOwner(
                callbacks,
                lr2ModeEnabled: true,
                chartFileScanner: new SequenceChartFileScanner(completeScan, incompleteScan));
            BmsLibraryOptionsSnapshot options = new() { OperationModeLR2DB = true };
            int initialOwnedCollectionVersion = callbacks.CatalogOwnedCollectionOwner.CollectionVersion;
            int initialSynchronizationOwnedCollectionVersion =
                callbacks.Lr2Synchronization.CreateLr2SongDbSyncInput().OwnedChartCollectionVersion;

            long firstGeneration = owner.BeginFileScanRequest(options, [directoryPath], "test_initial_commit");
            Lr2FolderFileDiffPreparationResult first = owner.ApplyActiveFileScan(
                firstGeneration,
                trackLibraryFileCheckProgress: true,
                installDestinationCleanupSnapshot: InstallDestinationCleanupSnapshot.Empty);
            Assert.IsTrue(first.FileCheckResult.HasDbDiff);
            Assert.IsNotNull(callbacks.Lr2Synchronization.CommittedPathReceipt);
            int committedOwnedCollectionVersion = callbacks.CatalogOwnedCollectionOwner.CollectionVersion;
            Assert.AreEqual(initialOwnedCollectionVersion + 1, committedOwnedCollectionVersion);

            long secondGeneration = owner.BeginFileScanRequest(options, [directoryPath], "test_incomplete");
            Assert.ThrowsException<InvalidOperationException>(
                () => owner.ApplyActiveFileScan(
                    secondGeneration,
                    trackLibraryFileCheckProgress: true,
                    installDestinationCleanupSnapshot: InstallDestinationCleanupSnapshot.Empty));

            Assert.IsTrue(callbacks.LastCatalogReplacement.Receipt.Applied);
            Assert.AreEqual(committedOwnedCollectionVersion, callbacks.CatalogOwnedCollectionOwner.CollectionVersion);
            Assert.AreEqual(
                initialSynchronizationOwnedCollectionVersion,
                callbacks.Lr2Synchronization.CreateLr2SongDbSyncInput().OwnedChartCollectionVersion);
            Assert.IsNull(callbacks.Lr2Synchronization.CommittedPathReceipt);
            Lr2SongDbSyncInput input = callbacks.Lr2Synchronization.CreateLr2SongDbSyncInput();
            Assert.IsNull(callbacks.Lr2Synchronization.TakeLr2SongDbSyncCommittedPathReceipt(
                input,
                "test_incomplete_first_take"));
            Assert.IsNull(callbacks.Lr2Synchronization.TakeLr2SongDbSyncCommittedPathReceipt(
                input,
                "test_incomplete_second_take"));
        }
        finally
        {
            if (Directory.Exists(directoryPath))
            {
                Directory.Delete(directoryPath, recursive: true);
            }
        }
    }

    [TestMethod]
    public void ApplyActiveFileScan_RechecksSameRequestWithoutRepeatingOutputProbe()
    {
        string directoryPath = Path.Combine(
            Path.GetTempPath(),
            nameof(LibraryFileScanPipelineOwnerTests),
            Guid.NewGuid().ToString("N"));
        string outputBase = Path.Combine(directoryPath, "output");
        Directory.CreateDirectory(directoryPath);
        Directory.CreateDirectory(outputBase);
        try
        {
            string bmsPath = Path.Combine(directoryPath, "recheck.bms");
            File.WriteAllText(bmsPath, "#PLAYER 1\r\n#TITLE Recheck\r\n#BPM 120\r\n#00111:01\r\n");
            var fileSystem = new RecordingPreflightFileSystem();
            var preflightService = new LibraryDirectoryPreflightService(fileSystem);
            var preflightOptions = new BmsLibraryOptionsSnapshot
            {
                OperationModeLR2DB = true,
                LR2CustomFolderOutputBaseDir = outputBase
            };
            LibraryDirectoryPreflightRequest request = preflightService.CreateRequest(
                [directoryPath],
                [directoryPath],
                preflightOptions);
            preflightService.EnsureAvailable(request, probeOutputBases: true);
            int createdProbeCount = fileSystem.CreatedProbePaths.Count;
            int openedPathCount = fileSystem.OpenedPaths.Count;
            int deletedPathCount = fileSystem.DeletedPaths.Count;

            IChartFileScanner chartFileScanner = CapturedChartFileScanner.FromFixture(
                [bmsPath],
                new Dictionary<string, IEnumerable<string>>(StringComparer.OrdinalIgnoreCase)
                {
                    [directoryPath] = []
                },
                [directoryPath]);
            var callbacks = new RecordingLibraryFileScanPipelineCallbacks();
            LibraryFileScanPipelineOwner owner = CreateOwner(
                callbacks,
                chartFileScanner: chartFileScanner,
                directoryPreflightService: preflightService);
            long generation = owner.BeginFileScanRequest(
                new BmsLibraryOptionsSnapshot(),
                [directoryPath],
                "test_preflight_recheck",
                directoryPreflightRequest: request);

            Lr2FolderFileDiffPreparationResult result = owner.ApplyActiveFileScan(
                generation,
                trackLibraryFileCheckProgress: true,
                installDestinationCleanupSnapshot: InstallDestinationCleanupSnapshot.Empty);

            Assert.IsNotNull(result);
            Assert.AreEqual(createdProbeCount, fileSystem.CreatedProbePaths.Count);
            Assert.AreEqual(openedPathCount, fileSystem.OpenedPaths.Count);
            Assert.AreEqual(deletedPathCount, fileSystem.DeletedPaths.Count);
        }
        finally
        {
            if (Directory.Exists(directoryPath))
            {
                Directory.Delete(directoryPath, recursive: true);
            }
        }
    }

    [TestMethod]
    public void ApplyFileScanDiff_EmptyDirectoryRequestCompletesProgressForRepeatedRequests()
    {
        var callbacks = new RecordingLibraryFileScanPipelineCallbacks();
        var owner = CreateOwner(callbacks);

        Lr2FolderFileDiffPreparationResult first = owner.ApplyFileScanDiff(
            new BmsLibraryOptionsSnapshot(),
            [],
            null!,
            null!,
            trackLibraryFileCheckProgress: true,
            reason: "test_first",
            installDestinationCleanupSnapshot: InstallDestinationCleanupSnapshot.Empty);
        Lr2FolderFileDiffPreparationResult second = owner.ApplyFileScanDiff(
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

        Lr2FolderFileDiffPreparationResult scan = owner.ApplyFileScanDiff(
            new BmsLibraryOptionsSnapshot(),
            ["C:\\charts"],
            prefetch,
            null!,
            trackLibraryFileCheckProgress: true,
            reason: "test_incomplete",
            installDestinationCleanupSnapshot: InstallDestinationCleanupSnapshot.Empty);
        SongTableFileCheckResult result = scan.FileCheckResult;

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
        Lr2FolderFileDiffPreparationResult scan = owner.ApplyActiveFileScan(
            generation,
            trackLibraryFileCheckProgress: true,
            installDestinationCleanupSnapshot: InstallDestinationCleanupSnapshot.Empty);
        SongTableFileCheckResult result = scan.FileCheckResult;

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

        Lr2FolderFileDiffPreparationResult scan = owner.ApplyActiveFileScan(
            firstGeneration,
            trackLibraryFileCheckProgress: true,
            installDestinationCleanupSnapshot: InstallDestinationCleanupSnapshot.Empty);
        SongTableFileCheckResult result = scan.FileCheckResult;
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

        Lr2FolderFileDiffPreparationResult scan = owner.ApplyActiveFileScan(
            nextGeneration,
            trackLibraryFileCheckProgress: true,
            installDestinationCleanupSnapshot: InstallDestinationCleanupSnapshot.Empty);
        SongTableFileCheckResult result = scan.FileCheckResult;
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
        CatalogStorageRowsStateSnapshot state = owner.CaptureStateSnapshot();
        Assert.AreEqual(2, current.BmsRowsVersion);
        Assert.AreEqual(2, current.BmsonRowsVersion);
        Assert.AreEqual(current.BmsRowsVersion, state.BmsRowsVersion);
        Assert.AreEqual(current.BmsonRowsVersion, state.BmsonRowsVersion);
        Assert.AreEqual(current.BmsRows.Count, state.BmsRowCount);
        Assert.AreEqual(current.BmsonRows.Count, state.BmsonRowCount);
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
            "test_storage_replacement");

        Assert.IsNotNull(callbacks.LastCatalogReplacement);
        Assert.IsTrue(callbacks.LastCatalogReplacement.Receipt.Applied);
        Assert.AreEqual(2, callbacks.CatalogStorageRowsOwner.CaptureSnapshot().BmsRows.Count);
    }

    private static LibraryFileScanPipelineOwner CreateOwner(
        RecordingLibraryFileScanPipelineCallbacks callbacks,
        bool lr2ModeEnabled = false,
        IChartFileScanner chartFileScanner = null,
        LibraryDirectoryPreflightService directoryPreflightService = null)
    {
        string directoryPath = Path.Combine(Path.GetTempPath(), nameof(LibraryFileScanPipelineOwnerTests), Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directoryPath);
        string songDbPath = Path.Combine(directoryPath, "song.db");
        using (new LR2SongDBExtended(songDbPath))
        {
        }
        TestBmsLibrary library = lr2ModeEnabled
            ? new TestBmsLibrary(
                songDbPath,
                getLR2Config: null,
                _lr2ScoreDB: null,
                startupRequiredFileScanReason: null,
                optionsSnapshotProvider: () => new BmsLibraryOptionsSnapshot
                {
                    OperationModeLR2DB = true
                })
            : new TestBmsLibrary(songDbPath);
        var dbGateway = new BmsLibraryDbGateway(songDbPath);
        var storageRowsOwner = new CatalogStorageRowsOwner();
        var ownedCollectionOwner = new CatalogOwnedCollectionOwner();
        ownedCollectionOwner.EnsureCurrent(storageRowsOwner);
        callbacks.CatalogStorageRowsOwner = storageRowsOwner;
        callbacks.CatalogOwnedCollectionOwner = ownedCollectionOwner;
        storageRowsOwner.ReplaceBmsRows([.. callbacks.BmsFiles]);
        storageRowsOwner.ReplaceBmsonRows([.. callbacks.BmsonSongs]);
        callbacks.Lr2Synchronization = library.Lr2Synchronization;
        var catalogMutationOwner = new CatalogMutationOwner(storageRowsOwner, ownedCollectionOwner, dbGateway);
        var catalogChartInfoOwner = new CatalogChartInfoOwner(
            _ => { },
            () => false,
            (_, _) => false,
            null,
            _ => { });
        callbacks.CatalogChartInfoOwner = catalogChartInfoOwner;
        catalogChartInfoOwner.ConfigureWorkflow(
            dbGateway,
            catalogMutationOwner,
            storageRowsOwner,
            ownedCollectionOwner,
            _ => { },
            ownerEvent =>
            {
                callbacks.CatalogChartInfoEvents.Add(ownerEvent);
                callbacks.EventOrder.Add(ownerEvent.Kind.ToString());
            });
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
            library.Lr2Synchronization,
            catalogMutationOwner,
            catalogChartInfoOwner,
            resourceHealthOwner,
            callbacks.PublishCatalogReplacement,
            callbacks.PublishCatalogResidual,
            new BmsLibraryInitializationService(),
            new EverythingNative(ApplicationPathPolicy.Current),
            chartFileScanner,
            directoryPreflightService: directoryPreflightService);
    }

    private sealed class SequenceChartFileScanner(params ChartScanExecutionResult[] results) : IChartFileScanner
    {
        private readonly IReadOnlyList<ChartScanExecutionResult> results = results ?? throw new ArgumentNullException(nameof(results));
        private int nextResultIndex;

        public ChartScanExecutionResult Scan(
            IEnumerable<string> rootDirectories,
            IEnumerable<string> chartExtensions,
            bool verboseLog = false,
            bool includeTextSurface = true,
            bool includeDirectorySurface = false)
        {
            int resultIndex = Math.Min(
                Interlocked.Increment(ref nextResultIndex) - 1,
                results.Count - 1);
            return results[resultIndex];
        }
    }

    private sealed class RecordingPreflightFileSystem : ILibraryDirectoryPreflightFileSystem
    {
        internal List<string> CreatedProbePaths { get; } = [];

        internal List<string> OpenedPaths { get; } = [];

        internal List<string> DeletedPaths { get; } = [];

        public FileAttributes GetAttributes(string path) => LongPathFileSystem.GetAttributes(path);

        public IEnumerable<string> EnumerateDirectoryEntries(string path) =>
            LongPathFileSystem.EnumerateFileSystemEntries(path, "*", SearchOption.TopDirectoryOnly);

        public string CreateOwnedProbePath(string directoryPath, string purpose)
        {
            string path = Path.Combine(
                directoryPath,
                ".bemusicseeker-" + purpose + "-" + Guid.NewGuid().ToString("N") + ".tmp");
            CreatedProbePaths.Add(path);
            return path;
        }

        public Stream Open(string path, FileMode mode, FileAccess access, FileShare share)
        {
            OpenedPaths.Add(path);
            return LongPathFileSystem.Open(path, mode, access, share);
        }

        public void DeleteFile(string path)
        {
            DeletedPaths.Add(path);
            LongPathFileSystem.DeleteFile(path);
        }
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

        public CatalogChartInfoOwner CatalogChartInfoOwner { get; set; } = null!;

        public BMSLibrary.Lr2SynchronizationOwner Lr2Synchronization { get; set; } = null!;

        public List<CatalogChartInfoOwnerEvent> CatalogChartInfoEvents { get; } = [];

        public List<string> EventOrder { get; } = [];

        public FileScanCatalogReplacementEvent LastCatalogReplacement { get; private set; } = null!;

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

        public Action PublishCatalogReplacement(FileScanCatalogReplacementEvent replacementEvent)
        {
            LastCatalogReplacement = replacementEvent;
            return null;
        }

        public Action PublishCatalogResidual(FileScanCatalogResidualEvent residualEvent)
        {
            LastCatalogResidual = residualEvent;
            return () => EventOrder.Add("Residual");
        }

    }

}
