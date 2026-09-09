using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.Serialization;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using BeMusicSeeker.Models;
using BeMusicSeeker.Models.BmsLibraryInternal;
using BeMusicSeeker.Models.LR2;
using BeMusicSeeker.Models.Utils;
using BeMusicSeeker.ViewModels;
using BeMusicSeeker.Views;
using BeMusicSeeker.Views.Dialogs;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace BeMusicSeeker.Tests;

[TestClass]
public sealed class PendingPackageWorkflowOwnerTests
{
    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public async Task FixInstalledLocationsAsync_HandlesOnlyDeletionFailureAfterRelease(bool requiredFinalization)
    {
        var events = new List<string>();
        var catalogFailure = new IOException("repair deletion catalog failure");
        var outcome = new LibraryChartRemovalOutcome([new("removed.bms", LibraryChartRemovalState.Confirmed)],
            true, requiredFinalization, catalogFailure);
        var store = new RecordingStore(events) { RepairFailure = new LibraryChartRemovalException(outcome) };
        var gate = new ChartFileOperationSynchronizer();
        bool releasedAtReport = false;
        var dialogs = new FileDbReportRecordingDialogs
        {
            OnMessage = () =>
        {
            releasedAtReport = gate.TryEnter(out IDisposable lease) && events.Contains("activity-end");
            lease?.Dispose();
        }
        };
        var owner = CreateOwner(CreateLibrary, events, store, dialogs, chartFileOperations: gate);
        var chart = CreateChart(installDestination: @"C:\Installed");
        Assert.IsTrue(RepairInstalledLocationRequest.TryCreate(
            [CreateTarget(chart, ChartOperationCapabilities.RepairInstalledLocation)], out var request));

        await owner.FixInstalledLocationsAsync(request);

        Assert.IsTrue(releasedAtReport);
        Assert.AreEqual(1, dialogs.Messages.Count);
        Assert.AreEqual(System.Windows.MessageBoxImage.Error, dialogs.Messages.Single().Icon);
        Assert.AreEqual(1, events.Count(value => value == "store-fix-installed-locations"));
        Assert.AreSame(catalogFailure, outcome.CatalogFailure);
        store.RepairFailure = new IOException("unrelated failure");
        Exception unrelated = await Assert.ThrowsExceptionAsync<IOException>(() => owner.FixInstalledLocationsAsync(request));
        Assert.AreSame(store.RepairFailure, unrelated);
        Assert.AreEqual(1, dialogs.Messages.Count);
    }

    [TestMethod]
    public async Task FixInstalledLocationsAsync_ReportsFilesystemOnlyOutcomeWithoutStoppingContinuation()
    {
        var events = new List<string>();
        var outcome = new LibraryChartRemovalOutcome([new("unverified.bms", LibraryChartRemovalState.NotExecuted)], true, true);
        var store = new RecordingStore(events) { RemovalOutcome = outcome };
        var dialogs = new FileDbReportRecordingDialogs { MessageFailure = new IOException("optional report failure") };
        var owner = CreateOwner(CreateLibrary, events, store, dialogs);
        Assert.IsTrue(RepairInstalledLocationRequest.TryCreate(
            [CreateTarget(CreateChart(installDestination: @"C:\Installed"), ChartOperationCapabilities.RepairInstalledLocation)], out var request));
        await owner.FixInstalledLocationsAsync(request);
        Assert.AreEqual(1, dialogs.Messages.Count);
        Assert.AreEqual(1, events.Count(value => value == "store-fix-installed-locations"));
        Assert.IsTrue(events.Contains("activity-end"));
    }

    [TestMethod]
    public async Task FixInstalledLocationsAsync_ReportsMovementReceiptAndLaterFailureAfterRelease()
    {
        var events = new List<string>();
        var laterFailure = new IOException("repair maintenance failure");
        var repairResult = new LibraryFixInstallationResult
        {
            MutationReceipt = new FileDbMutationBatchReceipt([
                FileDbMutationReportTests.Receipt(FileDbMutationTerminalState.Completed)]),
            Failure = laterFailure
        };
        var store = new RecordingStore(events) { RepairResult = repairResult };
        var gate = new ChartFileOperationSynchronizer();
        bool releasedAtReport = false;
        var dialogs = new FileDbReportRecordingDialogs
        {
            OnMessage = () =>
            {
                releasedAtReport = gate.TryEnter(out IDisposable lease) && events.Contains("activity-end");
                lease?.Dispose();
            }
        };
        var owner = CreateOwner(CreateLibrary, events, store, dialogs, chartFileOperations: gate);
        Assert.IsTrue(RepairInstalledLocationRequest.TryCreate(
            [CreateTarget(CreateChart(installDestination: @"C:\Installed"), ChartOperationCapabilities.RepairInstalledLocation)], out var request));

        await owner.FixInstalledLocationsAsync(request);

        Assert.IsTrue(releasedAtReport);
        Assert.AreEqual(1, dialogs.Messages.Count);
        Assert.AreEqual(System.Windows.MessageBoxImage.Error, dialogs.Messages.Single().Icon);
        StringAssert.Contains(dialogs.Messages.Single().MessageBoxText, laterFailure.Message);
        Assert.AreSame(repairResult.MutationReceipt, store.RepairResult!.MutationReceipt);
        Assert.AreSame(laterFailure, store.RepairResult.Failure);
    }

    [TestMethod]
    public async Task FixInstalledLocationsAsync_RealModelFailureReportsOnlyAfterGateRelease()
    {
        TestResourceInitializer.EnsureJapaneseResources();
        string tempRootPath = Path.Combine(
            Path.GetTempPath(),
            "BeMusicSeeker_PendingRepairTerminal_" + Guid.NewGuid().ToString("N"));
        string sourceDirectoryPath = Path.Combine(tempRootPath, "Broken");
        string destinationDirectoryPath = Path.Combine(tempRootPath, "Installed");
        string sourceChartPath = Path.Combine(sourceDirectoryPath, "chart.bms");
        string destinationChartPath = Path.Combine(destinationDirectoryPath, "chart.bms");
        string songDbPath = Path.Combine(tempRootPath, "song.db");
        Directory.CreateDirectory(sourceDirectoryPath);
        Directory.CreateDirectory(destinationDirectoryPath);
        File.WriteAllText(sourceChartPath, "#PLAYER 1\r\n#TITLE Pending repair terminal\r\n");
        try
        {
            BMSFile chartOwner = BMSFile.CreateBMSFileFromFile(sourceChartPath);
            using (var songDb = new LR2SongDBExtended(songDbPath))
            {
                songDb.CreateTable<LR2SongDB.song>();
                songDb.CreateTable<LR2SongDB.folder>();
                songDb.CreateTable<LR2SongDBExtended.maintenance>();
                songDb.CreateTable<LR2SongDBExtended.bmson_song>();
                songDb.InsertOrReplace(chartOwner.CreateSongRowPersistenceCopy(), typeof(LR2SongDB.song));
                string escapedDestinationPath = destinationChartPath.Replace("'", "''");
                songDb.Execute(
                    "CREATE TRIGGER pending_repair_path_failure BEFORE INSERT ON song WHEN NEW.path = '"
                    + escapedDestinationPath
                    + "' BEGIN SELECT RAISE(ABORT, 'pending-repair-path-fault'); END;");
            }

            var dialogs = new FileDbReportRecordingDialogs();
            var library = new TestBmsLibrary(
                songDbPath,
                null,
                null,
                new ResilientFileMutationService(),
                dialogs)
            {
                BMSFiles = [chartOwner]
            };
            var events = new List<string>();
            var store = new RecordingStore(events)
            {
                FixInstalledLocationsAction = (activeLibrary, charts, approvedPaths) =>
                    activeLibrary.FixInstallationDirectoryCharts(charts, approvedPaths)
            };
            var gate = new ChartFileOperationSynchronizer();
            bool reportAfterGateRelease = false;
            dialogs.OnMessage = () =>
            {
                bool acquired = gate.TryEnter(out IDisposable lease);
                reportAfterGateRelease = acquired && events.Contains("activity-end");
                lease?.Dispose();
            };

            var owner = CreateOwner(
                () => library,
                events,
                store,
                dialogs,
                chartFileOperations: gate);
            ChartFile repairChart = ChartFileProjection.WithPackageState(
                ChartFileProjection.FromBmsFile(chartOwner),
                destinationDirectoryPath,
                string.Empty,
                string.Empty,
                []);
            Assert.IsTrue(RepairInstalledLocationRequest.TryCreate(
                [CreateTarget(repairChart, ChartOperationCapabilities.RepairInstalledLocation)],
                out RepairInstalledLocationRequest request));

            await owner.FixInstalledLocationsAsync(request);

            Assert.IsTrue(reportAfterGateRelease);
            Assert.AreEqual(1, dialogs.Messages.Count);
            Assert.AreEqual(0, dialogs.ModelMessages);
            Assert.AreEqual(1, events.Count(value => value == "store-fix-installed-locations"));
            Assert.IsTrue(File.Exists(sourceChartPath));
            Assert.IsFalse(File.Exists(destinationChartPath));
        }
        finally
        {
            if (Directory.Exists(tempRootPath))
            {
                Directory.Delete(LongPathFileSystem.ToExtendedPath(tempRootPath), recursive: true);
            }
        }
    }

    [TestMethod]
    public void CanOpenInstallDestination_RequiresPendingSectionAndEffectiveTargetCapability()
    {
        var owner = CreateOwner(
            () => null!,
            [],
            new RecordingStore([]),
            AcceptedDialogs());
        ChartOperationTarget pendingTarget = CreateTarget(CreateChart());
        ChartOperationTarget unsupportedTarget = CreateTarget(
            CreateChart(),
            ChartOperationCapabilities.None);
        ChartOperationTarget missingTarget = new(
            CreateChart(),
            null,
            ChartOperationSourceScope.PlaylistMissing,
            isOwned: false,
            isPending: false,
            isPlaylistMissing: true,
            ChartOperationCapabilities.UpdateInstallDestination);

        Assert.IsTrue(owner.CanOpenInstallDestination(
            pendingTarget,
            [pendingTarget],
            isPendingSection: true,
            isPlaylistRow: false));
        Assert.IsTrue(owner.CanOpenInstallDestination(
            pendingTarget,
            [],
            isPendingSection: true,
            isPlaylistRow: false));
        Assert.IsFalse(owner.CanOpenInstallDestination(
            pendingTarget,
            [unsupportedTarget],
            isPendingSection: true,
            isPlaylistRow: false));
        Assert.IsFalse(owner.CanOpenInstallDestination(
            pendingTarget,
            [pendingTarget],
            isPendingSection: false,
            isPlaylistRow: false));
        Assert.IsFalse(owner.CanOpenInstallDestination(
            pendingTarget,
            [pendingTarget],
            isPendingSection: true,
            isPlaylistRow: true));
        Assert.IsFalse(owner.CanOpenInstallDestination(
            missingTarget,
            [missingTarget],
            isPendingSection: true,
            isPlaylistRow: false));
    }

    [TestMethod]
    public async Task OpenInstallDestinationForChartsAsync_UsesDirectDestinationAndOpensIt()
    {
        TestResourceInitializer.EnsureJapaneseResources();
        string temporaryDirectory = Path.Combine(Path.GetTempPath(), nameof(PendingPackageWorkflowOwnerTests), Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(temporaryDirectory);
        try
        {
            var openedDirectories = new List<string>();
            var dialogs = AcceptedDialogs();
            var owner = CreateOwner(
                () => null!,
                [],
                new RecordingStore([]),
                dialogs,
                directory =>
                {
                    openedDirectories.Add(directory);
                    return new ExplorerOpenResult { Kind = ExplorerOpenResultKind.OpenedDirectory };
                });

            await owner.OpenInstallDestinationForChartsAsync(
                [CreateTarget(CreateChart(installDestination: temporaryDirectory))]);

            CollectionAssert.AreEqual(new[] { temporaryDirectory }, openedDirectories);
            Assert.AreEqual(0, dialogs.MessageRequests.Count);
        }
        finally
        {
            if (Directory.Exists(temporaryDirectory))
            {
                Directory.Delete(temporaryDirectory, recursive: true);
            }
        }
    }

    [TestMethod]
    public async Task OpenInstallDestinationForChartsAsync_StaleDirectDestinationDoesNotUseHashFallback()
    {
        TestResourceInitializer.EnsureJapaneseResources();
        int providerCallCount = 0;
        var dialogs = AcceptedDialogs();
        var openedDirectories = new List<string>();
        var owner = CreateOwner(
            () =>
            {
                providerCallCount++;
                return null!;
            },
            [],
            new RecordingStore([]),
            dialogs,
            directory =>
            {
                openedDirectories.Add(directory);
                return new ExplorerOpenResult { Kind = ExplorerOpenResultKind.OpenedDirectory };
            });

        string staleDirectory = Path.Combine(Path.GetTempPath(), nameof(PendingPackageWorkflowOwnerTests), Guid.NewGuid().ToString("N"));
        await owner.OpenInstallDestinationForChartsAsync(
            [CreateTarget(CreateChart(installDestination: staleDirectory))]);

        Assert.AreEqual(0, providerCallCount);
        Assert.AreEqual(0, openedDirectories.Count);
        Assert.AreEqual(
            string.Format(BeMusicSeeker.Properties.Resources.Msg_open_install_destination_not_found, staleDirectory),
            dialogs.MessageRequests.Single().MessageBoxText);
    }

    [TestMethod]
    public async Task OpenInstallDestinationForChartsAsync_NotifiesMultipleSelectionBeforeOpeningFirstTarget()
    {
        TestResourceInitializer.EnsureJapaneseResources();
        string temporaryDirectory = Path.Combine(Path.GetTempPath(), nameof(PendingPackageWorkflowOwnerTests), Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(temporaryDirectory);
        try
        {
            var openedDirectories = new List<string>();
            var dialogs = AcceptedDialogs();
            var owner = CreateOwner(
                () => null!,
                [],
                new RecordingStore([]),
                dialogs,
                directory =>
                {
                    openedDirectories.Add(directory);
                    return new ExplorerOpenResult { Kind = ExplorerOpenResultKind.OpenedDirectory };
                });

            await owner.OpenInstallDestinationForChartsAsync(
                [
                    CreateTarget(CreateChart(installDestination: temporaryDirectory)),
                    CreateTarget(CreateChart(path: @"C:\Charts\second.bms", installDestination: temporaryDirectory))
                ]);

            Assert.AreEqual(1, dialogs.MessageRequests.Count);
            Assert.AreEqual(
                BeMusicSeeker.Properties.Resources.Msg_open_install_destination_multiple_selected,
                dialogs.MessageRequests[0].MessageBoxText);
            CollectionAssert.AreEqual(new[] { temporaryDirectory }, openedDirectories);
        }
        finally
        {
            if (Directory.Exists(temporaryDirectory))
            {
                Directory.Delete(temporaryDirectory, recursive: true);
            }
        }
    }

    [TestMethod]
    public async Task OpenInstallDestinationForPackageAsync_UsesFirstResolvableChartEntry()
    {
        TestResourceInitializer.EnsureJapaneseResources();
        string temporaryDirectory = Path.Combine(Path.GetTempPath(), nameof(PendingPackageWorkflowOwnerTests), Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(temporaryDirectory);
        try
        {
            ChartPackage package = ChartPackage.FromChartEntries(
            [
                PackageChartEntry.FromChart(CreateChart()),
                PackageChartEntry.FromChart(CreateChart(
                    path: @"C:\Charts\second.bms",
                    installDestination: temporaryDirectory))
            ]);
            var openedDirectories = new List<string>();
            var dialogs = AcceptedDialogs();
            var owner = CreateOwner(
                () => null!,
                [],
                new RecordingStore([]),
                dialogs,
                directory =>
                {
                    openedDirectories.Add(directory);
                    return new ExplorerOpenResult { Kind = ExplorerOpenResultKind.OpenedDirectory };
                });

            await owner.OpenInstallDestinationForPackageAsync(package);

            CollectionAssert.AreEqual(new[] { temporaryDirectory }, openedDirectories);
            Assert.AreEqual(0, dialogs.MessageRequests.Count);
        }
        finally
        {
            if (Directory.Exists(temporaryDirectory))
            {
                Directory.Delete(temporaryDirectory, recursive: true);
            }
        }
    }

    [TestMethod]
    public async Task OpenInstallDestinationForPackageAsync_AllEntriesMissingShowsWarning()
    {
        TestResourceInitializer.EnsureJapaneseResources();
        ChartPackage package = ChartPackage.FromChartEntries(
        [
            PackageChartEntry.FromChart(CreateChart()),
            PackageChartEntry.FromChart(CreateChart(path: @"C:\Charts\second.bms"))
        ]);
        var dialogs = AcceptedDialogs();
        var openedDirectories = new List<string>();
        var owner = CreateOwner(
            () => null!,
            [],
            new RecordingStore([]),
            dialogs,
            directory =>
            {
                openedDirectories.Add(directory);
                return new ExplorerOpenResult { Kind = ExplorerOpenResultKind.OpenedDirectory };
            });

        await owner.OpenInstallDestinationForPackageAsync(package);

        Assert.AreEqual(0, openedDirectories.Count);
        Assert.AreEqual(
            BeMusicSeeker.Properties.Resources.Msg_open_install_destination_missing,
            dialogs.MessageRequests.Single().MessageBoxText);
    }

    [TestMethod]
    public async Task OpenInstallDestinationForChartsAsync_DialogFailurePropagatesWithoutOpeningExplorer()
    {
        TestResourceInitializer.EnsureJapaneseResources();
        var failure = new InvalidOperationException("dialog failed");
        var dialogs = AcceptedDialogs();
        dialogs.MessageResult = UiDialogResult.Failed(failure);
        var openedDirectories = new List<string>();
        var owner = CreateOwner(
            () => null!,
            [],
            new RecordingStore([]),
            dialogs,
            directory =>
            {
                openedDirectories.Add(directory);
                return new ExplorerOpenResult { Kind = ExplorerOpenResultKind.OpenedDirectory };
            });

        InvalidOperationException exception = await Assert.ThrowsExceptionAsync<InvalidOperationException>(
            () => owner.OpenInstallDestinationForChartsAsync(
                [CreateTarget(CreateChart(installDestination: Path.Combine(Path.GetTempPath(), nameof(PendingPackageWorkflowOwnerTests), Guid.NewGuid().ToString("N"))))]));

        Assert.AreSame(failure, exception.InnerException);
        Assert.AreEqual(0, openedDirectories.Count);
    }

    [TestMethod]
    public void OpenPackageSourceInExplorer_OpensExistingDirectoryOnlyThroughDirectoryRoute()
    {
        string temporaryDirectory = Path.Combine(Path.GetTempPath(), nameof(PendingPackageWorkflowOwnerTests), Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(temporaryDirectory);
        try
        {
            var openedDirectories = new List<string>();
            var selectedFiles = new List<string>();
            var owner = CreateOwner(
                () => null!,
                [],
                new RecordingStore([]),
                AcceptedDialogs(),
                directory =>
                {
                    openedDirectories.Add(directory);
                    return new ExplorerOpenResult { Kind = ExplorerOpenResultKind.OpenedDirectory };
                },
                file =>
                {
                    selectedFiles.Add(file);
                    return new ExplorerOpenResult { Kind = ExplorerOpenResultKind.SelectedFile };
                });

            owner.OpenPackageSourceInExplorer(new ChartPackage { path = temporaryDirectory });

            CollectionAssert.AreEqual(new[] { temporaryDirectory }, openedDirectories);
            Assert.AreEqual(0, selectedFiles.Count);
        }
        finally
        {
            if (Directory.Exists(temporaryDirectory))
            {
                Directory.Delete(temporaryDirectory, recursive: true);
            }
        }
    }

    [TestMethod]
    public void OpenPackageSourceInExplorer_OpensExistingFileOnlyThroughFileRoute()
    {
        string temporaryDirectory = Path.Combine(Path.GetTempPath(), nameof(PendingPackageWorkflowOwnerTests), Guid.NewGuid().ToString("N"));
        string temporaryFile = Path.Combine(temporaryDirectory, "package.zip");
        Directory.CreateDirectory(temporaryDirectory);
        File.WriteAllText(temporaryFile, string.Empty);
        try
        {
            var openedDirectories = new List<string>();
            var selectedFiles = new List<string>();
            var owner = CreateOwner(
                () => null!,
                [],
                new RecordingStore([]),
                AcceptedDialogs(),
                directory =>
                {
                    openedDirectories.Add(directory);
                    return new ExplorerOpenResult { Kind = ExplorerOpenResultKind.OpenedDirectory };
                },
                file =>
                {
                    selectedFiles.Add(file);
                    return new ExplorerOpenResult { Kind = ExplorerOpenResultKind.SelectedFile };
                });

            owner.OpenPackageSourceInExplorer(new ChartPackage { path = temporaryFile });

            Assert.AreEqual(0, openedDirectories.Count);
            CollectionAssert.AreEqual(new[] { temporaryFile }, selectedFiles);
        }
        finally
        {
            if (Directory.Exists(temporaryDirectory))
            {
                Directory.Delete(temporaryDirectory, recursive: true);
            }
        }
    }

    [TestMethod]
    public void OpenPackageSourceInExplorer_MissingOrEmptyPathIsSilent()
    {
        var openedDirectories = new List<string>();
        var selectedFiles = new List<string>();
        var owner = CreateOwner(
            () => null!,
            [],
            new RecordingStore([]),
            AcceptedDialogs(),
            directory =>
            {
                openedDirectories.Add(directory);
                return new ExplorerOpenResult { Kind = ExplorerOpenResultKind.OpenedDirectory };
            },
            file =>
            {
                selectedFiles.Add(file);
                return new ExplorerOpenResult { Kind = ExplorerOpenResultKind.SelectedFile };
            });

        owner.OpenPackageSourceInExplorer(new ChartPackage { path = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N")) });
        owner.OpenPackageSourceInExplorer(new ChartPackage { path = string.Empty });

        Assert.AreEqual(0, openedDirectories.Count);
        Assert.AreEqual(0, selectedFiles.Count);
    }

    [TestMethod]
    public void OpenPackageSourceInExplorer_DoesNotTranslateExplorerFailureIntoAnotherRoute()
    {
        string temporaryDirectory = Path.Combine(Path.GetTempPath(), nameof(PendingPackageWorkflowOwnerTests), Guid.NewGuid().ToString("N"));
        string temporaryFile = Path.Combine(temporaryDirectory, "package.zip");
        Directory.CreateDirectory(temporaryDirectory);
        File.WriteAllText(temporaryFile, string.Empty);
        try
        {
            int directoryOpenCount = 0;
            int fileSelectCount = 0;
            var owner = CreateOwner(
                () => null!,
                [],
                new RecordingStore([]),
                AcceptedDialogs(),
                directory =>
                {
                    directoryOpenCount++;
                    return new ExplorerOpenResult { Kind = ExplorerOpenResultKind.Failed, FailureReason = "directory_failed" };
                },
                file =>
                {
                    fileSelectCount++;
                    return new ExplorerOpenResult { Kind = ExplorerOpenResultKind.Failed, FailureReason = "file_failed" };
                });

            owner.OpenPackageSourceInExplorer(new ChartPackage { path = temporaryFile });

            Assert.AreEqual(0, directoryOpenCount);
            Assert.AreEqual(1, fileSelectCount);
        }
        finally
        {
            if (Directory.Exists(temporaryDirectory))
            {
                Directory.Delete(temporaryDirectory, recursive: true);
            }
        }
    }

    [TestMethod]
    public async Task SearchPendingAsync_AppliesMutationAndTerminalRefreshInOrder()
    {
        var events = new List<string>();
        var chart = CreateChart();
        var store = new RecordingStore(events) { ChangedCharts = [chart] };
        var owner = CreateOwner(CreateLibrary, events, store, AcceptedDialogs());
        PendingInstallDestinationSearchRequest request =
            PendingInstallDestinationSearchRequest.CreateInstallDestinationSearch([CreateTarget(chart)]);

        await owner.SearchPendingAsync(request);

        CollectionAssert.AreEqual(
            new[]
            {
                "activity-start",
                "suppression-start",
                "store-search-pending",
                "transient",
                "suppression-end",
                "activity-end",
                "invalidate",
                "identity-refresh"
            },
            events);
    }

    [TestMethod]
    public async Task SearchPendingAsync_MergeRejectionDoesNotStartMutation()
    {
        var events = new List<string>();
        var store = new RecordingStore(events);
        var dialogs = new FakeUiDialogService
        {
            ConfirmationResult = UiDialogResult.FromMessageBoxResult(MessageBoxResult.Cancel)
        };
        var owner = CreateOwner(CreateLibrary, events, store, dialogs);
        PendingInstallDestinationSearchRequest request =
            PendingInstallDestinationSearchRequest.CreateMergeDestinationSearch([CreateTarget(CreateChart())]);

        await owner.SearchPendingAsync(request);

        Assert.IsNotNull(dialogs.ConfirmationRequest);
        Assert.AreEqual(BeMusicSeeker.Properties.Resources.Msg_estimate_merge_confirm, dialogs.ConfirmationRequest.MessageBoxText);
        Assert.AreEqual(0, store.SearchPendingCount);
        CollectionAssert.AreEqual(Array.Empty<string>(), events);
    }

    [TestMethod]
    public async Task SearchPackagesAsync_MergeAcceptanceRunsMutation()
    {
        var events = new List<string>();
        var store = new RecordingStore(events);
        var dialogs = AcceptedDialogs();
        var owner = CreateOwner(CreateLibrary, events, store, dialogs);
        var package = new ChartPackage();

        await owner.SearchPackagesAsync(PendingInstallDestinationSearchKind.MergeDestination, [package]);

        Assert.AreEqual(PendingInstallDestinationSearchKind.MergeDestination, store.LastSearchKind);
        CollectionAssert.AreEqual(new[] { package }, (System.Collections.ICollection)store.LastPackages);
        CollectionAssert.AreEqual(
            new[]
            {
                "activity-start",
                "suppression-start",
                "store-search-packages",
                "suppression-end",
                "activity-end",
                "invalidate",
                "identity-refresh"
            },
            events);
    }

    [TestMethod]
    public async Task SearchPendingAsync_DialogFailureIsNotTreatedAsRejection()
    {
        var events = new List<string>();
        var store = new RecordingStore(events);
        var failure = new InvalidOperationException("dialog failed");
        var dialogs = new FakeUiDialogService { ConfirmationResult = UiDialogResult.Failed(failure) };
        var owner = CreateOwner(CreateLibrary, events, store, dialogs);
        PendingInstallDestinationSearchRequest request =
            PendingInstallDestinationSearchRequest.CreateMergeDestinationSearch([CreateTarget(CreateChart())]);

        InvalidOperationException exception = await Assert.ThrowsExceptionAsync<InvalidOperationException>(
            () => owner.SearchPendingAsync(request));

        Assert.AreSame(failure, exception.InnerException);
        Assert.AreEqual(0, store.SearchPendingCount);
        CollectionAssert.AreEqual(Array.Empty<string>(), events);
    }

    [TestMethod]
    public async Task ClearPackagesAsync_WithoutAttachedLibraryStillClearsInMemoryState()
    {
        var events = new List<string>();
        var chart = CreateChart();
        var store = new RecordingStore(events) { ChangedCharts = [chart] };
        var owner = CreateOwner(() => null!, events, store, AcceptedDialogs());

        await owner.ClearPackagesAsync([new ChartPackage()]);

        CollectionAssert.AreEqual(
            new[]
            {
                "activity-start",
                "suppression-start",
                "store-clear-packages",
                "transient",
                "suppression-end",
                "activity-end",
                "invalidate"
            },
            events);
    }

    [TestMethod]
    public async Task SearchCorrectAsync_ProjectsAndInvalidatesBeforeSuppressionEnds()
    {
        var events = new List<string>();
        var chart = CreateChart();
        var store = new RecordingStore(events) { ChangedCharts = [chart] };
        var owner = CreateOwner(CreateLibrary, events, store, AcceptedDialogs());
        Assert.IsTrue(RepairInstalledLocationRequest.TryCreate(
            [CreateTarget(chart, ChartOperationCapabilities.RepairInstalledLocation)],
            out RepairInstalledLocationRequest request));

        await owner.SearchCorrectAsync(request);

        CollectionAssert.AreEqual(
            new[]
            {
                "activity-start",
                "suppression-start",
                "store-search-correct",
                "transient",
                "invalidate",
                "suppression-end",
                "activity-end"
            },
            events);
    }

    [TestMethod]
    public async Task SetPendingAsync_RefreshesEditedRowAfterMutationBoundary()
    {
        var events = new List<string>();
        var chart = CreateChart();
        var store = new RecordingStore(events) { SetResult = chart };
        var owner = CreateOwner(CreateLibrary, events, store, AcceptedDialogs());
        Assert.IsTrue(PendingInstallDestinationEditRequest.TryCreate(
            CreateTarget(chart),
            out PendingInstallDestinationEditRequest request));

        await owner.SetPendingAsync(request, "C:\\Installed");

        Assert.AreEqual("C:\\Installed", store.LastDestinationDirectory);
        CollectionAssert.AreEqual(
            new[]
            {
                "activity-start",
                "suppression-start",
                "store-set",
                "suppression-end",
                "activity-end",
                "transient",
                "invalidate",
                "display-refresh"
            },
            events);
    }

    [TestMethod]
    public async Task ClearPendingAsync_PropagatesFailureAndClosesMutationBoundary()
    {
        var events = new List<string>();
        var store = new RecordingStore(events) { Failure = new InvalidOperationException("clear failed") };
        var owner = CreateOwner(CreateLibrary, events, store, AcceptedDialogs());
        Assert.IsTrue(PendingInstallDestinationClearRequest.TryCreate(
            [CreateTarget(CreateChart())],
            out PendingInstallDestinationClearRequest request));

        InvalidOperationException exception = await Assert.ThrowsExceptionAsync<InvalidOperationException>(
            () => owner.ClearPendingAsync(request));

        Assert.AreEqual("clear failed", exception.Message);
        CollectionAssert.AreEqual(
            new[]
            {
                "activity-start",
                "suppression-start",
                "store-clear-pending",
                "suppression-end",
                "activity-end"
            },
            events);
    }

    [TestMethod]
    public async Task SearchPackagesAsync_EndActivityFailureStillDetachesAndFlushesDialogScope()
    {
        TestResourceInitializer.EnsureJapaneseResources();
        string tempDirectory = Path.Combine(Path.GetTempPath(), nameof(PendingPackageWorkflowOwnerTests), Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempDirectory);
        try
        {
            var events = new List<string>();
            var dialogService = new RecordingLibraryDialogService();
            string songDbPath = Path.Combine(tempDirectory, "song.db");
            using (var _ = new LR2SongDBExtended(songDbPath))
            {
            }
            var library = new TestBmsLibrary(songDbPath, null!, null, null!, dialogService);
            string missingDirectory = Path.Combine(tempDirectory, "missing");
            var store = new RecordingStore(events)
            {
                SearchPackagesAction = activeLibrary => activeLibrary.RenameChartFolder(missingDirectory, "renamed")
            };
            var failure = new InvalidOperationException("activity end failed");
            var presentation = new RecordingPresentation(events) { EndActivityFailure = failure };
            var owner = CreateOwner(
                () => library,
                events,
                store,
                AcceptedDialogs(),
                presentation: presentation,
                playback: new NoOpPendingPackageMutationPlaybackPort());

            InvalidOperationException exception = await Assert.ThrowsExceptionAsync<InvalidOperationException>(
                () => owner.SearchPackagesAsync(
                    PendingInstallDestinationSearchKind.InstallDestination,
                    [new ChartPackage()]));

            Assert.AreSame(failure, exception);
            Assert.AreEqual(1, dialogService.CallCount);
            CollectionAssert.AreEqual(
                new[]
                {
                    "activity-start",
                    "suppression-start",
                    "store-search-packages",
                    "suppression-end",
                    "activity-end"
                },
                events);
        }
        finally
        {
            Directory.Delete(tempDirectory, recursive: true);
        }
    }

    [TestMethod]
    public async Task SearchPackagesAsync_MultipleFailuresPreserveMutationAndEveryCleanupFailure()
    {
        TestResourceInitializer.EnsureJapaneseResources();
        string tempDirectory = Path.Combine(Path.GetTempPath(), nameof(PendingPackageWorkflowOwnerTests), Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempDirectory);
        try
        {
            var mutationFailure = new InvalidOperationException("mutation failed");
            var suppressionFailure = new InvalidOperationException("suppression end failed");
            var activityFailure = new InvalidOperationException("activity end failed");
            var flushFailure = new InvalidOperationException("dialog flush failed");
            var events = new List<string>();
            var dialogService = new RecordingLibraryDialogService { Failure = flushFailure };
            string songDbPath = Path.Combine(tempDirectory, "song.db");
            using (var _ = new LR2SongDBExtended(songDbPath))
            {
            }
            var library = new TestBmsLibrary(songDbPath, null!, null, null!, dialogService);
            string missingDirectory = Path.Combine(tempDirectory, "missing");
            var store = new RecordingStore(events)
            {
                Failure = mutationFailure,
                SearchPackagesAction = activeLibrary => activeLibrary.RenameChartFolder(missingDirectory, "renamed")
            };
            var presentation = new RecordingPresentation(events)
            {
                EndRefreshSuppressionFailure = suppressionFailure,
                EndActivityFailure = activityFailure
            };
            var owner = CreateOwner(
                () => library,
                events,
                store,
                AcceptedDialogs(),
                presentation: presentation,
                playback: new NoOpPendingPackageMutationPlaybackPort());

            AggregateException exception = await Assert.ThrowsExceptionAsync<AggregateException>(
                () => owner.SearchPackagesAsync(
                    PendingInstallDestinationSearchKind.InstallDestination,
                    [new ChartPackage()]));

            CollectionAssert.AreEqual(
                new Exception[] { mutationFailure, suppressionFailure, activityFailure, flushFailure },
                exception.InnerExceptions);
            CollectionAssert.AreEqual(
                new[]
                {
                    "activity-start",
                    "suppression-start",
                    "store-search-packages",
                    "suppression-end",
                    "activity-end"
                },
                events);
        }
        finally
        {
            Directory.Delete(tempDirectory, recursive: true);
        }
    }

    [TestMethod]
    public async Task ForceInstallPackagesAsync_ApprovesOverrideThenStopsPlaybackAndMutatesPackageScope()
    {
        TestResourceInitializer.EnsureJapaneseResources();
        var events = new List<string>();
        ChartFile chart = CreateChart(installDestination: @"C:\Installed\song.bms");
        ChartPackage package = ChartPackage.FromChartEntries([PackageChartEntry.FromChart(chart)]);
        var store = new RecordingStore(events)
        {
            EmptyPendingSection = true
        };
        var presentation = new RecordingPresentation(events);
        var playback = new RecordingPlayback(events);
        var dialogs = new FakeUiDialogService
        {
            ConfirmationResult = UiDialogResult.FromMessageBoxResult(MessageBoxResult.Yes)
        };
        var owner = CreateOwner(
            CreateLibrary,
            events,
            store,
            dialogs,
            presentation: presentation,
            playback: playback);

        PendingPackageMutationResult result = await owner.ForceInstallPackagesAsync([package]);

        Assert.IsTrue(result.Succeeded);
        Assert.IsNull(result.Failure);
        Assert.IsTrue(result.ShouldApplyView);
        Assert.AreEqual(PackageCatalogSection.Pending, result.EmptySection);
        CollectionAssert.AreEqual(
            new[]
            {
                "activity-start",
                "playback-stop",
                "suppression-start",
                "store-force-install",
                "suppression-end",
                "activity-end"
            },
            events);
        Assert.AreEqual(PendingPackageRefreshScope.PackageMutation, presentation.LastRefreshScope);
        Assert.AreEqual(chart.Path, playback.StoppedCharts.Single().Path);
        Assert.IsTrue(store.ApprovedNormalInstallOverridePackages.Contains(package));
        Assert.AreEqual(BeMusicSeeker.Properties.Resources.Confirm_NormalInstallOverride, dialogs.ConfirmationRequest!.MessageBoxText);
    }

    [TestMethod]
    public async Task ForceInstallPackagesAsync_OverrideRejectionRunsBoundaryWithoutApprovingPackage()
    {
        TestResourceInitializer.EnsureJapaneseResources();
        var events = new List<string>();
        ChartFile chart = CreateChart(installDestination: @"C:\Installed\song.bms");
        ChartPackage package = ChartPackage.FromChartEntries([PackageChartEntry.FromChart(chart)]);
        var store = new RecordingStore(events);
        var dialogs = new FakeUiDialogService
        {
            ConfirmationResult = UiDialogResult.FromMessageBoxResult(MessageBoxResult.No)
        };
        var owner = CreateOwner(CreateLibrary, events, store, dialogs);

        PendingPackageMutationResult result = await owner.ForceInstallPackagesAsync([package]);

        Assert.IsTrue(result.Succeeded);
        Assert.IsNull(result.Failure);
        Assert.IsTrue(result.ShouldApplyView);
        CollectionAssert.AreEqual(
            new[]
            {
                "activity-start",
                "playback-stop",
                "suppression-start",
                "store-force-install",
                "suppression-end",
                "activity-end"
            },
            events);
        Assert.AreEqual(0, store.ApprovedNormalInstallOverridePackages.Count);
        Assert.AreSame(package, store.LastPackages.Single());
    }

    [TestMethod]
    public async Task ManualInstallPackagesAsync_RejectionPreservesShellSelectionAndSkipsMutation()
    {
        TestResourceInitializer.EnsureJapaneseResources();
        var events = new List<string>();
        var store = new RecordingStore(events);
        var dialogs = new FakeUiDialogService
        {
            ConfirmationResult = UiDialogResult.FromMessageBoxResult(MessageBoxResult.Cancel)
        };
        var owner = CreateOwner(
            CreateLibrary,
            events,
            store,
            dialogs,
            playback: new NoOpPendingPackageMutationPlaybackPort(),
            settingsProvider: () => new InstallDestinationWorkflowSettingsSnapshot(
                showManualInstallConfirmation: true,
                deletePendingPackageSourceAfterInstall: true));
        PendingPackageMutationResult result = await owner.ManualInstallPackagesAsync(
            [ChartPackage.FromChartEntries([PackageChartEntry.FromChart(CreateChart())])]);

        Assert.IsFalse(result.Succeeded);
        Assert.IsNull(result.Failure);
        Assert.IsFalse(result.ShouldApplyView);
        Assert.AreEqual(0, events.Count);
        Assert.AreEqual(
            BeMusicSeeker.Properties.Resources.Msg_manual_installation_delete_source,
            dialogs.ConfirmationRequest!.MessageBoxText);
    }

    [TestMethod]
    public async Task ManualInstallPackagesAsync_ConfirmationFailureReturnsBeforeMutation()
    {
        TestResourceInitializer.EnsureJapaneseResources();
        var events = new List<string>();
        var failure = new InvalidOperationException("manual confirmation failed");
        var store = new RecordingStore(events);
        var dialogs = new FakeUiDialogService
        {
            ConfirmationResult = UiDialogResult.Failed(failure)
        };
        var owner = CreateOwner(
            CreateLibrary,
            events,
            store,
            dialogs,
            playback: new NoOpPendingPackageMutationPlaybackPort(),
            settingsProvider: () => new InstallDestinationWorkflowSettingsSnapshot(
                showManualInstallConfirmation: true,
                deletePendingPackageSourceAfterInstall: true));

        PendingPackageMutationResult result = await owner.ManualInstallPackagesAsync(
            [ChartPackage.FromChartEntries([PackageChartEntry.FromChart(CreateChart())])]);

        Assert.IsFalse(result.Succeeded);
        Assert.IsNotNull(result.Failure);
        Assert.AreSame(failure, result.Failure.InnerException);
        Assert.IsFalse(result.ShouldApplyView);
        Assert.AreEqual(0, events.Count);
    }

    [TestMethod]
    public async Task ForceInstallPackagesAsync_MutationFailurePreservesTerminalViewApply()
    {
        var events = new List<string>();
        var mutationFailure = new InvalidOperationException("force install failed");
        var cleanupFailure = new InvalidOperationException("refresh cleanup failed");
        var store = new RecordingStore(events)
        {
            Failure = mutationFailure,
            EmptyPendingSection = true
        };
        var presentation = new RecordingPresentation(events)
        {
            EndRefreshSuppressionFailure = cleanupFailure
        };
        var owner = CreateOwner(
            CreateLibrary,
            events,
            store,
            AcceptedDialogs(),
            presentation: presentation,
            playback: new RecordingPlayback(events));

        PendingPackageMutationResult result = await owner.ForceInstallPackagesAsync(
            [ChartPackage.FromChartEntries([PackageChartEntry.FromChart(CreateChart())])]);

        Assert.IsFalse(result.Succeeded);
        Assert.IsTrue(result.ShouldApplyView);
        Assert.AreEqual(PackageCatalogSection.Pending, result.EmptySection);
        Assert.IsInstanceOfType<AggregateException>(result.Failure);
        CollectionAssert.AreEqual(
            new[]
            {
                "activity-start",
                "playback-stop",
                "suppression-start",
                "store-force-install",
                "suppression-end",
                "activity-end"
            },
            events);
        var exception = (AggregateException)result.Failure;
        Assert.AreSame(mutationFailure, exception.InnerExceptions[0]);
        Assert.AreSame(cleanupFailure, exception.InnerExceptions[1]);
    }

    [TestMethod]
    public async Task ForceInstallPackagesAsync_DoesNotCapturePendingSectionBeforeMutation()
    {
        var events = new List<string>();
        var suppressionFailure = new InvalidOperationException("suppression start failed");
        var store = new RecordingStore(events)
        {
            EmptyPendingSection = true,
            EmptyPendingSectionFailure = new InvalidOperationException("empty-section query should not run")
        };
        var presentation = new RecordingPresentation(events)
        {
            StartRefreshSuppressionFailure = suppressionFailure
        };
        var owner = CreateOwner(
            CreateLibrary,
            events,
            store,
            AcceptedDialogs(),
            presentation: presentation,
            playback: new RecordingPlayback(events));

        PendingPackageMutationResult result = await owner.ForceInstallPackagesAsync(
            [ChartPackage.FromChartEntries([PackageChartEntry.FromChart(CreateChart())])]);

        Assert.IsFalse(result.Succeeded);
        Assert.IsTrue(result.ShouldApplyView);
        Assert.IsNull(result.EmptySection);
        Assert.AreEqual(0, store.IsPendingSectionEmptyCount);
        Assert.AreSame(suppressionFailure, result.Failure);
    }

    [TestMethod]
    public async Task InstallPendingAsync_ResolutionFailureDoesNotApplySelection()
    {
        var events = new List<string>();
        var failure = new InvalidOperationException("pending resolution failed");
        var store = new RecordingStore(events) { Failure = failure };
        var owner = CreateOwner(CreateLibrary, events, store, AcceptedDialogs());
        PendingInstallPackageOperationRequest request =
            PendingInstallPackageOperationRequest.CreateForceInstall([CreateTarget(CreateChart())]);

        PendingPackageMutationResult result = await owner.InstallPendingAsync(request);

        Assert.IsFalse(result.Succeeded);
        Assert.IsFalse(result.ShouldApplyView);
        Assert.AreSame(failure, result.Failure);
        CollectionAssert.AreEqual(new[] { "store-resolve-packages" }, events);
    }

    [TestMethod]
    public async Task InstallPendingAsync_ResolvesRequestBeforePreparingShellAndForceInstalling()
    {
        var events = new List<string>();
        ChartFile chart = CreateChart();
        ChartPackage package = ChartPackage.FromChartEntries([PackageChartEntry.FromChart(chart)]);
        var store = new RecordingStore(events) { ResolvedPackages = [package] };
        var owner = CreateOwner(CreateLibrary, events, store, new FakeUiDialogService());
        PendingInstallPackageOperationRequest request =
            PendingInstallPackageOperationRequest.CreateForceInstall([CreateTarget(chart)]);

        PendingPackageMutationResult result = await owner.InstallPendingAsync(request);

        Assert.IsTrue(result.Succeeded);
        Assert.IsNull(result.Failure);
        Assert.IsTrue(result.ShouldApplyView);
        CollectionAssert.AreEqual(
            new[]
            {
                "store-resolve-packages",
                "activity-start",
                "playback-stop",
                "suppression-start",
                "store-force-install",
                "suppression-end",
                "activity-end"
            },
            events);
        Assert.AreSame(package, store.LastPackages.Single());
    }

    [TestMethod]
    public async Task FixInstalledLocationsAsync_WarnsWithoutDestinationAndDoesNotMutate()
    {
        TestResourceInitializer.EnsureJapaneseResources();
        var events = new List<string>();
        var store = new RecordingStore(events);
        var dialogs = new FakeUiDialogService();
        var owner = CreateOwner(CreateLibrary, events, store, dialogs);
        Assert.IsTrue(RepairInstalledLocationRequest.TryCreate(
            [CreateTarget(CreateChart(), ChartOperationCapabilities.RepairInstalledLocation)],
            out RepairInstalledLocationRequest request));

        await owner.FixInstalledLocationsAsync(request);

        Assert.AreEqual(0, events.Count);
        Assert.AreEqual(
            BeMusicSeeker.Properties.Resources.Msg_fix_installation_warning,
            dialogs.MessageRequest!.MessageBoxText);
    }

    [TestMethod]
    public async Task FixInstalledLocationsAsync_WarningDisplayFailurePropagates()
    {
        TestResourceInitializer.EnsureJapaneseResources();
        var events = new List<string>();
        var failure = new InvalidOperationException("warning display failed");
        var store = new RecordingStore(events);
        var dialogs = new FakeUiDialogService
        {
            MessageResult = UiDialogResult.Failed(failure)
        };
        var owner = CreateOwner(CreateLibrary, events, store, dialogs);
        Assert.IsTrue(RepairInstalledLocationRequest.TryCreate(
            [CreateTarget(CreateChart(), ChartOperationCapabilities.RepairInstalledLocation)],
            out RepairInstalledLocationRequest request));

        InvalidOperationException exception = await Assert.ThrowsExceptionAsync<InvalidOperationException>(
            () => owner.FixInstalledLocationsAsync(request));

        Assert.AreSame(failure, exception.InnerException);
        StringAssert.Contains(exception.Message, "Installed-location repair warning");
        Assert.AreEqual(0, events.Count);
    }

    [TestMethod]
    public async Task FixInstalledLocationsAsync_ApprovesDuplicateRemovalAndMutatesPackageScope()
    {
        TestResourceInitializer.EnsureJapaneseResources();
        var events = new List<string>();
        ChartFile chart = CreateChart(
            path: @"C:\Charts\repair.bms",
            installDestination: @"C:\Installed\repair.bms");
        var store = new RecordingStore(events)
        {
            DuplicateConfirmations =
            [
                new BMSLibrary.DuplicateInstallRepairConfirmation(
                    chart,
                    [@"C:\Duplicate\repair.bms"])
            ]
        };
        var presentation = new RecordingPresentation(events);
        var playback = new RecordingPlayback(events);
        var dialogs = new FakeUiDialogService();
        dialogs.EnqueueConfirmation(UiDialogResult.FromMessageBoxResult(MessageBoxResult.OK));
        dialogs.EnqueueConfirmation(UiDialogResult.FromMessageBoxResult(MessageBoxResult.Yes));
        var owner = CreateOwner(
            CreateLibrary,
            events,
            store,
            dialogs,
            presentation: presentation,
            playback: playback);
        Assert.IsTrue(RepairInstalledLocationRequest.TryCreate(
            [CreateTarget(chart, ChartOperationCapabilities.RepairInstalledLocation)],
            out RepairInstalledLocationRequest request));

        await owner.FixInstalledLocationsAsync(request);

        CollectionAssert.AreEqual(
            new[]
            {
                "store-get-duplicate-confirmations",
                "activity-start",
                "playback-stop",
                "suppression-start",
                "store-fix-installed-locations",
                "suppression-end",
                "activity-end"
            },
            events);
        Assert.AreEqual(PendingPackageRefreshScope.PackageMutation, presentation.LastRefreshScope);
        Assert.AreEqual(chart.Path, playback.StoppedCharts.Single().Path);
        CollectionAssert.AreEqual(
            new[] { chart.Path },
            store.ApprovedDuplicateRemovalChartPaths.ToArray());
    }

    [TestMethod]
    public async Task DeleteInstalledOnlyPendingPackageSourcesAsync_ApprovedMutationUsesOwnerBoundary()
    {
        TestResourceInitializer.EnsureJapaneseResources();
        var events = new List<string>();
        ChartPackage package = ChartPackage.FromChartEntries([
            PackageChartEntry.FromChart(CreateChart())
        ]);
        var store = new RecordingStore(events) { InstalledOnlyPendingPackages = [package] };
        var owner = CreateOwner(CreateLibrary, events, store, AcceptedDialogs());

        await owner.DeleteInstalledOnlyPendingPackageSourcesAsync();

        CollectionAssert.AreEqual(
            new[]
            {
                "store-get-installed-only",
                "activity-start",
                "suppression-start",
                "store-delete-sources",
                "suppression-end",
                "activity-end"
            },
            events);
        Assert.AreSame(package, store.LastPackages.Single());
    }

    [TestMethod]
    public async Task DeleteInstalledOnlyPendingPackageSourcesAsync_EmptySelectionWarnsWithoutMutation()
    {
        TestResourceInitializer.EnsureJapaneseResources();
        var events = new List<string>();
        var store = new RecordingStore(events);
        var dialogs = AcceptedDialogs();
        var owner = CreateOwner(CreateLibrary, events, store, dialogs);

        await owner.DeleteInstalledOnlyPendingPackageSourcesAsync();

        CollectionAssert.AreEqual(new[] { "store-get-installed-only" }, events);
        Assert.AreEqual(
            BeMusicSeeker.Properties.Resources.Warn_no_pending_installed_only_packages,
            dialogs.MessageRequest!.MessageBoxText);
    }

    [TestMethod]
    public async Task DeleteInstalledOnlyPendingPackageSourcesAsync_RejectionSkipsMutation()
    {
        TestResourceInitializer.EnsureJapaneseResources();
        var events = new List<string>();
        ChartPackage package = ChartPackage.FromChartEntries([
            PackageChartEntry.FromChart(CreateChart())
        ]);
        var store = new RecordingStore(events) { InstalledOnlyPendingPackages = [package] };
        var dialogs = new FakeUiDialogService
        {
            ConfirmationResult = UiDialogResult.FromMessageBoxResult(MessageBoxResult.Cancel)
        };
        var owner = CreateOwner(CreateLibrary, events, store, dialogs);

        await owner.DeleteInstalledOnlyPendingPackageSourcesAsync();

        CollectionAssert.AreEqual(new[] { "store-get-installed-only" }, events);
    }

    [TestMethod]
    public async Task RenamePendingZeroNoteChartsAsync_StopsPlaybackBeforeMutation()
    {
        TestResourceInitializer.EnsureJapaneseResources();
        var events = new List<string>();
        ChartFile chart = CreateChart();
        var store = new RecordingStore(events) { PendingBmsFormatCharts = [chart] };
        var presentation = new RecordingPresentation(events);
        var playback = new RecordingPlayback(events);
        var owner = CreateOwner(
            CreateLibrary,
            events,
            store,
            AcceptedDialogs(),
            presentation: presentation,
            playback: playback);

        await owner.RenamePendingZeroNoteChartsAsync();

        CollectionAssert.AreEqual(
            new[]
            {
                "store-get-pending-charts",
                "activity-start",
                "playback-stop",
                "suppression-start",
                "store-rename-zero-note",
                "suppression-end",
                "activity-end"
            },
            events);
        Assert.AreEqual(chart.Path, playback.StoppedCharts.Single().Path);
    }

    [TestMethod]
    public async Task OverwriteInstalledOnlyPendingPackageResourcesAsync_ShowsSummaryAfterMutation()
    {
        TestResourceInitializer.EnsureJapaneseResources();
        var events = new List<string>();
        ChartFile chart = CreateChart();
        ChartPackage package = ChartPackage.FromChartEntries([PackageChartEntry.FromChart(chart)]);
        var overwriteResult = new PendingInstalledOnlyResourceOverwriteResult
        {
            Requested = 1,
            Processed = 1,
            SucceededInstall = 1
        };
        var store = new RecordingStore(events)
        {
            InstalledOnlyPendingPackages = [package],
            OverwriteResult = overwriteResult
        };
        var dialogs = AcceptedDialogs();
        var presentation = new RecordingPresentation(events);
        var playback = new RecordingPlayback(events);
        var owner = CreateOwner(
            CreateLibrary,
            events,
            store,
            dialogs,
            presentation: presentation,
            playback: playback);

        await owner.OverwriteInstalledOnlyPendingPackageResourcesAsync();

        CollectionAssert.AreEqual(
            new[]
            {
                "store-get-installed-only",
                "activity-start",
                "playback-stop",
                "suppression-start",
                "store-overwrite-resources",
                "suppression-end",
                "activity-end"
            },
            events);
        Assert.AreEqual(chart.Path, playback.StoppedCharts.Single().Path);
        StringAssert.Contains(dialogs.MessageRequest!.MessageBoxText, "1");
    }

    [TestMethod]
    public async Task DeleteInstalledOnlyPendingPackageSourcesAsync_MultiplePackagesUsesProgressRoute()
    {
        TestResourceInitializer.EnsureJapaneseResources();
        var events = new List<string>();
        ChartPackage first = ChartPackage.FromChartEntries([PackageChartEntry.FromChart(CreateChart(@"C:\Charts\first.bms"))]);
        ChartPackage second = ChartPackage.FromChartEntries([PackageChartEntry.FromChart(CreateChart(@"C:\Charts\second.bms"))]);
        var store = new RecordingStore(events) { InstalledOnlyPendingPackages = [first, second] };
        var dialogs = AcceptedDialogs();
        var owner = CreateOwner(CreateLibrary, events, store, dialogs);

        await owner.DeleteInstalledOnlyPendingPackageSourcesAsync();

        Assert.IsNotNull(dialogs.ProgressRequest);
        Assert.AreEqual(BeMusicSeeker.Properties.Resources.Remove, dialogs.ProgressRequest.Title);
        CollectionAssert.AreEqual(new[] { first, second }, (System.Collections.ICollection)store.LastPackages);
    }

    [TestMethod]
    public async Task DeleteInstalledOnlyPendingPackageSourcesAsync_ProgressFailurePropagates()
    {
        TestResourceInitializer.EnsureJapaneseResources();
        var events = new List<string>();
        ChartPackage first = ChartPackage.FromChartEntries([PackageChartEntry.FromChart(CreateChart(@"C:\Charts\first.bms"))]);
        ChartPackage second = ChartPackage.FromChartEntries([PackageChartEntry.FromChart(CreateChart(@"C:\Charts\second.bms"))]);
        var store = new RecordingStore(events) { InstalledOnlyPendingPackages = [first, second] };
        var failure = new InvalidOperationException("progress failed");
        var dialogs = AcceptedDialogs();
        dialogs.ProgressResult = new UiProgressResult(UiDialogStatus.Failed, error: failure);
        var owner = CreateOwner(CreateLibrary, events, store, dialogs);

        InvalidOperationException exception = await Assert.ThrowsExceptionAsync<InvalidOperationException>(
            owner.DeleteInstalledOnlyPendingPackageSourcesAsync);

        Assert.AreSame(failure, exception.InnerException);
    }

    [TestMethod]
    public async Task DeleteInstalledOnlyPendingPackageSourcesAsync_UserCancellationIsNormalCompletion()
    {
        TestResourceInitializer.EnsureJapaneseResources();
        var events = new List<string>();
        ChartPackage first = ChartPackage.FromChartEntries([PackageChartEntry.FromChart(CreateChart(@"C:\Charts\first.bms"))]);
        ChartPackage second = ChartPackage.FromChartEntries([PackageChartEntry.FromChart(CreateChart(@"C:\Charts\second.bms"))]);
        var store = new RecordingStore(events)
        {
            InstalledOnlyPendingPackages = [first, second],
            WaitForDeleteSourcesCancellation = true
        };
        var dialogs = AcceptedDialogs();
        dialogs.ProgressResult = new UiProgressResult(UiDialogStatus.CancelledByUser);
        var owner = CreateOwner(CreateLibrary, events, store, dialogs);

        await owner.DeleteInstalledOnlyPendingPackageSourcesAsync();

        CollectionAssert.Contains(events, "store-delete-sources");
    }

    [TestMethod]
    public async Task DeleteInstalledOnlyPendingPackageSourcesAsync_PreservesDialogAndMutationFailures()
    {
        TestResourceInitializer.EnsureJapaneseResources();
        var events = new List<string>();
        ChartPackage first = ChartPackage.FromChartEntries([PackageChartEntry.FromChart(CreateChart(@"C:\Charts\first.bms"))]);
        ChartPackage second = ChartPackage.FromChartEntries([PackageChartEntry.FromChart(CreateChart(@"C:\Charts\second.bms"))]);
        var mutationFailure = new IOException("source deletion failed");
        var store = new RecordingStore(events)
        {
            InstalledOnlyPendingPackages = [first, second],
            DeleteSourcesFailure = mutationFailure
        };
        var dialogFailure = new InvalidOperationException("progress failed");
        var dialogs = AcceptedDialogs();
        dialogs.ProgressResult = new UiProgressResult(UiDialogStatus.Failed, error: dialogFailure);
        var owner = CreateOwner(CreateLibrary, events, store, dialogs);

        AggregateException exception = await Assert.ThrowsExceptionAsync<AggregateException>(
            owner.DeleteInstalledOnlyPendingPackageSourcesAsync);

        Assert.AreSame(dialogFailure, exception.InnerExceptions[0].InnerException);
        Assert.AreSame(mutationFailure, exception.InnerExceptions[1]);
    }

    [TestMethod]
    public async Task SearchPackagesAsync_FailsFastWhenOwnerAdmissionIsBusyThenRunsAfterRelease()
    {
        TestResourceInitializer.EnsureJapaneseResources();
        string tempRootPath = Path.Combine(
            Path.GetTempPath(),
            "BeMusicSeeker_PendingWorkflowAdmissionTests_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempRootPath);
        string songDbPath = Path.Combine(tempRootPath, "song.db");
        File.WriteAllBytes(songDbPath, []);
        try
        {
            var library = new TestBmsLibrary(songDbPath);
            var events = new List<string>();
            var store = new RecordingStore(events);
            FakeUiDialogService dialogs = AcceptedDialogs();
            var owner = CreateOwner(
                () => library,
                events,
                store,
                dialogs,
                playback: new NoOpPendingPackageMutationPlaybackPort());
            Assert.IsTrue(library.TryEnterPendingOperation(out IDisposable incumbent));
            try
            {
                await owner.SearchPackagesAsync(
                    PendingInstallDestinationSearchKind.InstallDestination,
                    [new ChartPackage()]);
                Assert.AreEqual(0, store.SearchPackagesCount);
                Assert.AreEqual(
                    BeMusicSeeker.Properties.Resources.Warn_Lr2SongDbSyncRunning,
                    dialogs.MessageRequest?.MessageBoxText);
            }
            finally
            {
                incumbent.Dispose();
            }

            await owner.SearchPackagesAsync(
                PendingInstallDestinationSearchKind.InstallDestination,
                [new ChartPackage()]);
            Assert.AreEqual(1, store.SearchPackagesCount);
        }
        finally
        {
            if (Directory.Exists(tempRootPath))
            {
                Directory.Delete(LongPathFileSystem.ToExtendedPath(tempRootPath), recursive: true);
            }
        }
    }

    [DataTestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public async Task InstallRetainsReceiptWhenOuterSuppressionCleanupFails(bool manual)
    {
        var events = new List<string>();
        var primary = new IOException("pending-primary-marker");
        var receipt = new FileDbMutationReceipt(Guid.NewGuid(), FileDbMutationTerminalState.DurableFinalizationFailed,
            true, 0, 1, [@"C:\source"], [@"D:\destination"], [], [], [], primary, primary,
            new IOException("pending-cleanup-marker"));
        var batch = new FileDbMutationBatchReceipt([receipt]);
        var store = new RecordingStore(events) { TerminalReceipt = batch };
        var gate = new ChartFileOperationSynchronizer();
        var owner = CreateOwner(CreateLibrary, events, store, AcceptedDialogs(), chartFileOperations: gate);
        var scopeFailure = new IOException("pending-scope-marker");
        owner.WorkflowChanged += (_, args) =>
        {
            if (args is PendingPackageRefreshSuppressionChangedEventArgs suppression && !suppression.IsSuppressed)
                throw scopeFailure;
        };
        var package = ChartPackage.FromChartEntries([PackageChartEntry.FromChart(CreateChart())]);
        PendingPackageMutationResult result = manual
            ? await owner.ManualInstallPackagesAsync([package]) : await owner.ForceInstallPackagesAsync([package]);
        Assert.AreSame(batch, result.MutationReceipt);
        Assert.AreSame(scopeFailure, result.Failure);
        Assert.IsTrue(result.HasDurableCommit);
        Assert.IsTrue(result.HasDurableFinalizationFailure);
        Assert.IsTrue(gate.TryEnter(out IDisposable released));
        released.Dispose();
        Assert.AreEqual(1, events.Count(value => value == (manual ? "store-manual-install" : "store-force-install")));
    }

    [TestMethod]
    public async Task RealPendingOwnerResultReportsConflictAfterOwnerReleasesMutationGate()
    {
        TestResourceInitializer.EnsureJapaneseResources();
        var conflict = new FileDbMutationDestinationTypeConflict(
            @"C:\pending\BGA",
            @"D:\installed\BGA",
            expectedIsDirectory: false,
            existingIsDirectory: true);
        var receipt = new FileDbMutationReceipt(
            Guid.NewGuid(),
            FileDbMutationTerminalState.Failed,
            durableCommit: false,
            compensationAttemptCount: 0,
            cleanupAttemptCount: 0,
            [@"C:\pending"],
            [@"D:\installed"],
            [],
            [],
            [],
            new FileDbMutationDestinationTypeConflictException(conflict),
            destinationTypeConflicts: [conflict]);
        var events = new List<string>();
        var store = new RecordingStore(events)
        {
            TerminalReceipt = new FileDbMutationBatchReceipt([receipt])
        };
        var gate = new ChartFileOperationSynchronizer();
        bool releasedAtReport = false;
        var dialogs = new FileDbReportRecordingDialogs
        {
            OnMessage = () =>
            {
                bool acquired = gate.TryEnter(out IDisposable lease);
                releasedAtReport = acquired && events.Contains("activity-end");
                lease?.Dispose();
            }
        };
        var owner = CreateOwner(
            CreateLibrary,
            events,
            store,
            dialogs,
            chartFileOperations: gate);
        ChartPackage package = ChartPackage.FromChartEntries([
            PackageChartEntry.FromChart(CreateChart())
        ]);

        PendingPackageMutationResult result = await owner.ManualInstallPackagesAsync([package]);
        var terminal = new MainWindowPendingPackageMutationViewTerminal(
            () => false,
            () => 1,
            _ => Task.FromResult(true),
            dialogs);
        await terminal.ApplyAsync(
            result,
            PackageCatalogSection.Pending,
            MainViewUpdateMode.PendingInstallFolderSelected,
            nameof(RealPendingOwnerResultReportsConflictAfterOwnerReleasesMutationGate));

        Assert.IsTrue(releasedAtReport);
        Assert.AreEqual(1, dialogs.Messages.Count);
        Assert.AreEqual(System.Windows.MessageBoxImage.Warning, dialogs.Messages[0].Icon);
        StringAssert.Contains(dialogs.Messages[0].MessageBoxText, conflict.DestinationPath);
        Assert.AreSame(conflict, result.DestinationTypeConflicts[0]);
        Assert.AreEqual(1, events.Count(value => value == "store-manual-install"));
    }

    private static PendingPackageWorkflowOwner CreateOwner(
        Func<BMSLibrary> libraryProvider,
        List<string> events,
        IPendingPackageStore store,
        IUiDialogService dialogs,
        Func<string, ExplorerOpenResult>? explorerOpener = null,
        Func<string, ExplorerOpenResult>? fileExplorerOpener = null,
        RecordingPresentation? presentation = null,
        IPendingPackageMutationPlaybackPort? playback = null,
        ChartFileOperationSynchronizer? chartFileOperations = null,
        Func<InstallDestinationWorkflowSettingsSnapshot>? settingsProvider = null)
    {
        presentation ??= new RecordingPresentation(events);
        ChartMutationActivityOwner activity = new();
        var owner = new PendingPackageWorkflowOwner(
            libraryProvider,
            chartFileOperations ?? new ChartFileOperationSynchronizer(),
            activity,
            playback ?? new RecordingPlayback(events),
            dialogs,
            settingsProvider ?? DefaultSettings,
            new TestExternalShellGateway(
                explorerOpener ?? (_ => new ExplorerOpenResult()),
                fileExplorerOpener ?? (_ => new ExplorerOpenResult())),
            store);
        owner.WorkflowChanged += presentation.OnWorkflowChanged;
        activity.ActivityChanged += presentation.OnActivityChanged;
        return owner;
    }

    private sealed class TestExternalShellGateway : IExternalShellGateway
    {
        private readonly Func<string, ExplorerOpenResult> explorerOpener;
        private readonly Func<string, ExplorerOpenResult> fileExplorerOpener;

        internal TestExternalShellGateway(
            Func<string, ExplorerOpenResult> explorerOpener,
            Func<string, ExplorerOpenResult> fileExplorerOpener)
        {
            this.explorerOpener = explorerOpener;
            this.fileExplorerOpener = fileExplorerOpener;
        }

        public void Open(ExternalShellRequest request)
        {
        }

        public ExplorerOpenResult OpenFileAndSelect(string filePath) => fileExplorerOpener(filePath);

        public ExplorerOpenResult OpenDirectory(string directoryPath) => explorerOpener(directoryPath);

        public bool TryOpenDirectoryWithExplorerProcess(string directoryPath, out string failureReason)
        {
            failureReason = string.Empty;
            return true;
        }
    }

    private static FakeUiDialogService AcceptedDialogs()
    {
        return new FakeUiDialogService
        {
            ConfirmationResult = UiDialogResult.FromMessageBoxResult(MessageBoxResult.OK)
        };
    }

    private static InstallDestinationWorkflowSettingsSnapshot DefaultSettings()
    {
        return new InstallDestinationWorkflowSettingsSnapshot(
            showManualInstallConfirmation: false,
            deletePendingPackageSourceAfterInstall: false);
    }

    private static BMSLibrary CreateLibrary()
    {
        return (BMSLibrary)FormatterServices.GetUninitializedObject(typeof(BMSLibrary));
    }

    private static ChartFile CreateChart(
        string path = @"C:\Charts\song.bms",
        string? installDestination = null)
    {
        var bmsFile = new BMSFile { path = path };
        return new ChartFile(
            ChartFileKind.Bms,
            path,
            md5: "0123456789abcdef0123456789abcdef",
            sha256: null,
            title: "Title",
            rawTitle: "Title",
            artist: "Artist",
            genre: "Genre",
            folder: "Charts",
            tag: null,
            levelText: null,
            level: null,
            mode: null,
            chartInfo: null,
            bmsFile: bmsFile,
            bmsonSong: null,
            installDestination: installDestination);
    }

    private static ChartOperationTarget CreateTarget(
        ChartFile chart,
        ChartOperationCapabilities capabilities = ChartOperationCapabilities.UpdateInstallDestination)
    {
        return new ChartOperationTarget(
            chart,
            null,
            ChartOperationSourceScope.PendingPackage,
            isOwned: false,
            isPending: true,
            isPlaylistMissing: false,
            capabilities,
            PackageChartEntry.FromChart(chart));
    }

    private sealed class RecordingPresentation
    {
        private readonly List<string> events;

        internal RecordingPresentation(List<string> events)
        {
            this.events = events;
        }

        internal Exception? EndActivityFailure { get; set; }

        internal Exception? EndRefreshSuppressionFailure { get; set; }

        internal Exception? StartRefreshSuppressionFailure { get; set; }

        internal void OnActivityChanged(object sender, EventArgs e)
        {
            var activity = (ChartMutationActivityOwner)sender;
            events.Add(activity.IsActive ? "activity-start" : "activity-end");
            if (!activity.IsActive && EndActivityFailure != null)
            {
                throw EndActivityFailure;
            }
        }

        internal PendingPackageRefreshScope LastRefreshScope { get; private set; }

        internal void OnWorkflowChanged(
            object sender,
            PendingPackageWorkflowChangedEventArgs e)
        {
            switch (e)
            {
                case PendingPackageRefreshSuppressionChangedEventArgs suppressionChanged:
                    if (suppressionChanged.IsSuppressed)
                    {
                        if (StartRefreshSuppressionFailure != null)
                        {
                            throw StartRefreshSuppressionFailure;
                        }
                        LastRefreshScope = suppressionChanged.Scope
                            ?? throw new InvalidOperationException("Refresh suppression scope was not published.");
                        events.Add("suppression-start");
                    }
                    else
                    {
                        events.Add("suppression-end");
                        if (EndRefreshSuppressionFailure != null)
                        {
                            throw EndRefreshSuppressionFailure;
                        }
                    }
                    break;
                case PendingPackageMutationAppliedEventArgs mutationApplied:
                    if (mutationApplied.ChangedCharts.Count > 0)
                    {
                        events.Add("transient");
                    }
                    if (mutationApplied.InstallDestinationStateChanged)
                    {
                        events.Add("invalidate");
                    }
                    if (mutationApplied.IdentitySortKeyChanged)
                    {
                        events.Add("identity-refresh");
                    }
                    if (mutationApplied.DisplayStateChanged)
                    {
                        events.Add("display-refresh");
                    }
                    break;
                default:
                    throw new ArgumentOutOfRangeException(nameof(e), e, "Unsupported pending-package workflow change.");
            }
        }
    }

    private sealed class RecordingPlayback : IPendingPackageMutationPlaybackPort
    {
        private readonly List<string> events;

        internal RecordingPlayback(List<string> events)
        {
            this.events = events;
        }

        internal IReadOnlyList<ChartFile> StoppedCharts { get; private set; } = [];

        public void StopIfPlayingCharts(IReadOnlyList<ChartFile> charts)
        {
            StoppedCharts = charts;
            events.Add("playback-stop");
        }
    }

    internal sealed class RecordingStore : IPendingPackageStore, IPendingPackageTerminalMutationStore
    {
        private readonly List<string> events;

        internal RecordingStore(List<string> events)
        {
            this.events = events;
        }

        internal int SearchPackagesCount { get; private set; }

        internal int SearchPendingCount { get; private set; }

        internal PendingInstallDestinationSearchKind LastSearchKind { get; private set; }

        internal IReadOnlyList<ChartPackage> LastPackages { get; private set; } = [];

        internal IReadOnlyList<ChartFile> ChangedCharts { get; set; } = [];

        internal ChartFile? SetResult { get; set; }

        internal PendingInstallDestinationEditRequest? LastPendingRequest { get; private set; }

        internal string? LastDestinationDirectory { get; private set; }

        internal IReadOnlyList<ChartPackage> ResolvedPackages { get; set; } = [];

        internal ISet<ChartPackage> ApprovedNormalInstallOverridePackages { get; private set; } = new HashSet<ChartPackage>();

        internal IReadOnlyList<BMSLibrary.DuplicateInstallRepairConfirmation> DuplicateConfirmations { get; set; } = [];

        internal IReadOnlyList<string> ApprovedDuplicateRemovalChartPaths { get; private set; } = [];

        internal IReadOnlyList<ChartPackage> InstalledOnlyPendingPackages { get; set; } = [];

        internal IReadOnlyList<ChartFile> PendingBmsFormatCharts { get; set; } = [];

        internal PendingInstalledOnlyResourceOverwriteResult? OverwriteResult { get; set; }

        internal bool WaitForDeleteSourcesCancellation { get; set; }

        internal Exception? DeleteSourcesFailure { get; set; }

        internal Exception? Failure { get; set; }

        internal bool EmptyPendingSection { get; set; }

        internal Exception? EmptyPendingSectionFailure { get; set; }

        internal int IsPendingSectionEmptyCount { get; private set; }

        internal Action<BMSLibrary>? SearchPackagesAction { get; set; }

        public void SearchPackages(
            BMSLibrary library,
            PendingInstallDestinationSearchKind kind,
            IReadOnlyList<ChartPackage> packages)
        {
            events.Add("store-search-packages");
            SearchPackagesCount++;
            LastSearchKind = kind;
            LastPackages = packages;
            SearchPackagesAction?.Invoke(library);
            ThrowIfConfigured();
        }

        public IReadOnlyList<ChartFile> SearchPending(
            BMSLibrary library,
            PendingInstallDestinationSearchRequest request)
        {
            events.Add("store-search-pending");
            SearchPendingCount++;
            ThrowIfConfigured();
            return ChangedCharts;
        }

        public IReadOnlyList<ChartFile> ClearPackages(IReadOnlyList<ChartPackage> packages)
        {
            events.Add("store-clear-packages");
            ThrowIfConfigured();
            return ChangedCharts;
        }

        public IReadOnlyList<ChartFile> ClearPending(
            BMSLibrary library,
            PendingInstallDestinationClearRequest request)
        {
            events.Add("store-clear-pending");
            ThrowIfConfigured();
            return ChangedCharts;
        }

        public IReadOnlyList<ChartFile> SearchCorrect(
            BMSLibrary library,
            RepairInstalledLocationRequest request)
        {
            events.Add("store-search-correct");
            ThrowIfConfigured();
            return ChangedCharts;
        }

        public IReadOnlyList<ChartFile> ClearCorrect(
            BMSLibrary library,
            RepairInstalledLocationRequest request)
        {
            events.Add("store-clear-correct");
            ThrowIfConfigured();
            return ChangedCharts;
        }

        public ChartFile SetPending(
            BMSLibrary library,
            PendingInstallDestinationEditRequest request,
            string destinationDirectory)
        {
            events.Add("store-set");
            LastPendingRequest = request;
            LastDestinationDirectory = destinationDirectory;
            ThrowIfConfigured();
            return SetResult!;
        }

        public IReadOnlyList<ChartPackage> ResolvePendingPackages(
            BMSLibrary library,
            IReadOnlyList<ChartOperationTarget> targets)
        {
            events.Add("store-resolve-packages");
            ThrowIfConfigured();
            return ResolvedPackages;
        }

        public void ForceInstallPackages(
            BMSLibrary library,
            IReadOnlyList<ChartPackage> packages,
            ISet<ChartPackage> approvedNormalInstallOverridePackages)
        {
            events.Add("store-force-install");
            LastPackages = packages;
            ApprovedNormalInstallOverridePackages = new HashSet<ChartPackage>(approvedNormalInstallOverridePackages);
            ThrowIfConfigured();
        }

        public void ManualInstallPackages(
            BMSLibrary library,
            IReadOnlyList<ChartPackage> packages)
        {
            events.Add("store-manual-install");
            LastPackages = packages;
            ThrowIfConfigured();
        }

        internal FileDbMutationBatchReceipt? TerminalReceipt { get; set; }

        public FileDbMutationBatchReceipt ForceInstallPackagesWithReceipt(BMSLibrary library,
            IReadOnlyList<ChartPackage> packages, ISet<ChartPackage> approvedNormalInstallOverridePackages)
        {
            ForceInstallPackages(library, packages, approvedNormalInstallOverridePackages);
            return TerminalReceipt!;
        }

        public PendingInstallBatchResult ManualInstallPackagesWithReceipt(BMSLibrary library,
            IReadOnlyList<ChartPackage> packages)
        {
            ManualInstallPackages(library, packages);
            return new PendingInstallBatchResult { MutationReceipt = TerminalReceipt };
        }

        public bool IsPendingSectionEmpty(BMSLibrary library)
        {
            IsPendingSectionEmptyCount++;
            if (EmptyPendingSectionFailure != null)
            {
                throw EmptyPendingSectionFailure;
            }
            return EmptyPendingSection;
        }

        public IReadOnlyList<BMSLibrary.DuplicateInstallRepairConfirmation> GetDuplicateInstallRepairConfirmations(
            BMSLibrary library,
            IReadOnlyList<ChartFile> repairCharts)
        {
            events.Add("store-get-duplicate-confirmations");
            ThrowIfConfigured();
            return DuplicateConfirmations;
        }

        internal LibraryChartRemovalOutcome RemovalOutcome { get; set; } = null!;
        internal Exception? RepairFailure { get; set; }
        internal LibraryFixInstallationResult? RepairResult { get; set; }

        internal Func<BMSLibrary, IReadOnlyList<ChartFile>, IReadOnlyList<string>, LibraryFixInstallationResult>? FixInstalledLocationsAction { get; set; }

        public LibraryFixInstallationResult FixInstalledLocations(
            BMSLibrary library,
            IReadOnlyList<ChartFile> repairCharts,
            IReadOnlyList<string> approvedDuplicateRemovalChartPaths)
        {
            events.Add("store-fix-installed-locations");
            ChangedCharts = repairCharts;
            ApprovedDuplicateRemovalChartPaths = approvedDuplicateRemovalChartPaths;
            ThrowIfConfigured();
            if (RepairFailure != null) throw RepairFailure;
            if (FixInstalledLocationsAction != null)
            {
                return FixInstalledLocationsAction(library, repairCharts, approvedDuplicateRemovalChartPaths);
            }
            return RepairResult ?? new LibraryFixInstallationResult
            {
                RemovalOutcome = RemovalOutcome
            };
        }

        public IReadOnlyList<ChartPackage> GetInstalledOnlyPendingPackages(BMSLibrary library)
        {
            events.Add("store-get-installed-only");
            ThrowIfConfigured();
            return InstalledOnlyPendingPackages;
        }

        public IReadOnlyList<ChartFile> GetPendingBmsFormatCharts(BMSLibrary library)
        {
            events.Add("store-get-pending-charts");
            ThrowIfConfigured();
            return PendingBmsFormatCharts;
        }

        public void DeletePendingPackageSources(
            BMSLibrary library,
            IReadOnlyList<ChartPackage> packages,
            CancellationToken cancellationToken,
            Action onEachProcessed)
        {
            events.Add("store-delete-sources");
            LastPackages = packages;
            if (DeleteSourcesFailure != null)
            {
                throw DeleteSourcesFailure;
            }
            if (WaitForDeleteSourcesCancellation)
            {
                Assert.IsTrue(cancellationToken.WaitHandle.WaitOne(TimeSpan.FromSeconds(5)));
            }
            foreach (ChartPackage _ in packages)
            {
                cancellationToken.ThrowIfCancellationRequested();
                onEachProcessed?.Invoke();
            }
            ThrowIfConfigured();
        }

        public void RenamePendingZeroNoteCharts(
            BMSLibrary library,
            IReadOnlyList<ChartFile> charts,
            CancellationToken cancellationToken,
            Action onEachProcessed)
        {
            events.Add("store-rename-zero-note");
            ChangedCharts = charts;
            foreach (ChartFile _ in charts)
            {
                cancellationToken.ThrowIfCancellationRequested();
                onEachProcessed?.Invoke();
            }
            ThrowIfConfigured();
        }

        public PendingInstalledOnlyResourceOverwriteResult OverwriteInstalledOnlyPendingPackageResources(
            BMSLibrary library,
            IReadOnlyList<ChartPackage> packages,
            CancellationToken cancellationToken,
            Action onEachProcessed)
        {
            events.Add("store-overwrite-resources");
            LastPackages = packages;
            foreach (ChartPackage _ in packages)
            {
                cancellationToken.ThrowIfCancellationRequested();
                onEachProcessed?.Invoke();
            }
            ThrowIfConfigured();
            return OverwriteResult!;
        }

        private void ThrowIfConfigured()
        {
            if (Failure != null)
            {
                throw Failure;
            }
        }
    }

    private sealed class FakeUiDialogService : IUiDialogService
    {
        private readonly Queue<UiDialogResult> confirmationResults = new();

        internal UiDialogResult? ConfirmationResult { get; set; }

        internal UiConfirmationRequest? ConfirmationRequest { get; private set; }

        internal UiMessageRequest? MessageRequest { get; private set; }

        internal List<UiMessageRequest> MessageRequests { get; } = [];

        internal UiDialogResult? MessageResult { get; set; }

        internal UiProgressRequest? ProgressRequest { get; private set; }

        internal UiProgressResult? ProgressResult { get; set; }

        internal void EnqueueConfirmation(UiDialogResult result)
        {
            confirmationResults.Enqueue(result);
        }

        public Task<UiDialogResult> ShowMessageAsync(UiMessageRequest request, CancellationToken cancellationToken = default)
        {
            MessageRequest = request;
            MessageRequests.Add(request);
            return Task.FromResult(MessageResult ?? UiDialogResult.FromMessageBoxResult(MessageBoxResult.OK));
        }

        public Task<UiDialogResult> ConfirmAsync(UiConfirmationRequest request, CancellationToken cancellationToken = default)
        {
            ConfirmationRequest = request;
            if (confirmationResults.Count > 0)
            {
                return Task.FromResult(confirmationResults.Dequeue());
            }
            return Task.FromResult(ConfirmationResult ?? throw new InvalidOperationException("Confirmation result was not configured."));
        }

        public Task<UiWindowDialogResult<TResult>> ShowWindowAsync<TWindow, TResult>(UiWindowDialogRequest<TWindow, TResult> request, CancellationToken cancellationToken = default)
            where TWindow : Window => throw new NotSupportedException();

        public Task<UiFilePickerResult> PickFileAsync(UiFilePickerRequest request, CancellationToken cancellationToken = default) => throw new NotSupportedException();

        public Task<UiFolderPickerResult> PickFolderAsync(UiFolderPickerRequest request, CancellationToken cancellationToken = default) => throw new NotSupportedException();

        public Task<UiSaveFilePickerResult> PickSaveFileAsync(UiSaveFilePickerRequest request, CancellationToken cancellationToken = default) => throw new NotSupportedException();

        public Task<UiProgressResult> RunWithProgressAsync(UiProgressRequest request, Func<UiProgressContext, Task> operation, CancellationToken cancellationToken = default)
        {
            ProgressRequest = request;
            return Task.FromResult(ProgressResult ?? new UiProgressResult(UiDialogStatus.Accepted));
        }
    }

    private sealed class RecordingLibraryDialogService : IBmsLibraryDialogService
    {
        internal int CallCount { get; private set; }

        internal Exception? Failure { get; set; }

        public UiDialogDefaultResult Show(
            string messageBoxText,
            string caption,
            UiDialogButton button,
            UiDialogIcon icon,
            UiDialogDefaultResult defaultResult = UiDialogDefaultResult.None)
        {
            CallCount++;
            if (Failure != null)
            {
                throw Failure;
            }
            return defaultResult == UiDialogDefaultResult.None ? UiDialogDefaultResult.OK : defaultResult;
        }
    }
}
