using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Input;
using System.Windows.Interop;
using BeMusicSeeker.Models;
using BeMusicSeeker.Models.BmsLibraryInternal;
using BeMusicSeeker.Models.LR2;
using BeMusicSeeker.Models.Utils;
using BeMusicSeeker.Properties;
using BeMusicSeeker.ViewModels;
using BeMusicSeeker.Views;
using BeMusicSeeker.Views.Dialogs;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace BeMusicSeeker.Tests;

[TestClass]
// This existing owner closes the whole application, whose shutdown drains process-global SQLite connections.
[DoNotParallelize]
public sealed class MainWindowPackageMaintenanceWpfTests
{
    [DataTestMethod]
    [DataRow(false, false, false)]
    [DataRow(true, false, false)]
    [DataRow(false, true, false)]
    [DataRow(false, false, true)]
    [DataRow(true, false, true)]
    public void AutoRenameReportsRealReceiptWithoutLosingFacts(bool allFolders, bool reporterThrows, bool cleanupOnly)
    {
        TestUiDispatcherHost.RunWindowTest(_ =>
        {
            string root = Path.Combine(Path.GetTempPath(), "FSDB-B-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(root);
            var dialogs = new FileDbReportRecordingDialogs();
            MainWindowViewModel? viewModel = null;
            try
            {
                string source = Path.Combine(root, "source");
                Directory.CreateDirectory(source);
                string sourceChart = Path.Combine(source, "chart.bms");
                File.WriteAllText(sourceChart, "#PLAYER 1\r\n#TITLE ReceiptTarget\r\n#ARTIST Artist\r\n");
                var chart = BMSFile.CreateBMSFileFromFile(sourceChart);
                string dbPath = Path.Combine(root, "song.db");
                string lr2Root = Path.Combine(root, "LR2");
                var config = BmsPlaylistTestSupport.CreateLr2Config(lr2Root, root);
                using (var db = new LR2SongDBExtended(dbPath))
                {
                    db.CreateTable<LR2SongDB.song>();
                    db.CreateTable<LR2SongDB.folder>();
                    db.CreateTable<LR2SongDBExtended.maintenance>();
                    db.InsertOrReplace(chart.CreateSongRowPersistenceCopy(), typeof(LR2SongDB.song));
                    if (!cleanupOnly) db.Execute("CREATE TRIGGER fail_report_finalizer BEFORE INSERT ON folder WHEN NEW.path LIKE '%ReceiptTarget%' BEGIN SELECT RAISE(ABORT, 'consumer-finalizer-marker'); END;");
                }
                var library = new TestBmsLibrary(dbPath, () => config, null,
                    cleanupOnly ? new BmsLibraryPackageInstallServiceTests.FailingDestinationDeleteFileMutationService(source)
                        : new ResilientFileMutationService(), dialogs,
                    new TestUiScheduler(() => TestUiDispatcherHost.Dispatcher),
                    () => new BmsLibraryOptionsSnapshot { OperationModeLR2DB = !cleanupOnly, LR2RootPath = lr2Root,
                        FolderNameFormat = "[%ARTIST%] %TITLE%" })
                { BMSFiles = [chart], SearchTargets = [root] };
                viewModel = MainWindowViewModelTestFactory.Create(new Settings(), dialogs);
                viewModel.StartupUpdateWorkflow.NotifyClosing();
                ((IStartupLibraryApplicationPort)viewModel).AttachStartupLibrary(library);
                bool activityInactive = false;
                bool leaseReleased = false;
                dialogs.OnMessage = () =>
                {
                    activityInactive = !viewModel.ChartMutationActivity.IsActive;
                    using var gate = library.TryBeginLibraryFileMutation("report-probe", showMessage: false);
                    leaseReleased = gate != null;
                };
                if (reporterThrows) dialogs.MessageFailure = new IOException("reporter-marker");
                FolderAutoRenameCompletionReceipt? completion = null;
                viewModel.FolderAutoRenameWorkflow.CompletionPublished += receipt => completion = receipt;
                FolderAutoRenameFailure? outcome = null;
                viewModel.FolderAutoRenameWorkflow.FailurePublished += failure => outcome = failure;
                if (allFolders)
                    TestUiDispatcherHost.AwaitTaskOnDispatcher(viewModel.FolderAutoRenameWorkflow.RequestStartAllAsync(root), "B1 all admission");
                else
                    Assert.IsTrue(viewModel.FolderAutoRenameWorkflow.RequestStartSelected([
                        new ChartOperationTarget(ChartFileProjection.FromBmsFile(chart), null,
                            ChartOperationSourceScope.Library, true, false, false, ChartOperationCapabilities.MoveInLibrary)]));
                TestUiDispatcherHost.AwaitTaskOnDispatcher(viewModel.FolderAutoRenameWorkflow.WaitForIdleAsync(), "B1 terminal");
                Assert.IsTrue(activityInactive);
                Assert.IsTrue(leaseReleased);
                Assert.AreEqual(1, dialogs.Messages.Count);
                Assert.AreEqual(0, dialogs.ModelMessages);
                Assert.AreEqual(cleanupOnly ? MessageBoxImage.Warning : MessageBoxImage.Error, dialogs.Messages[0].Icon);
                if (cleanupOnly)
                {
                    Assert.IsNotNull(completion);
                    Assert.IsTrue(completion.RefreshRequired);
                    Assert.IsTrue(completion.MutationReceipt.CompletedWithCleanupFailure);
                    Assert.IsNull(outcome);
                }
                else
                {
                    StringAssert.Contains(dialogs.Messages[0].MessageBoxText, "consumer-finalizer-marker");
                    Assert.IsNotNull(outcome);
                    Assert.IsTrue(outcome.HasDurableCommit);
                    Assert.IsFalse(outcome.ExecutionResult.RefreshRequired);
                }
                Assert.AreEqual(cleanupOnly, File.Exists(sourceChart));
                Assert.IsTrue(File.Exists(chart.path));
                using var verifyDb = new LR2SongDBExtended(dbPath);
                Assert.IsNotNull(verifyDb.Find<LR2SongDB.song>(chart.path));
            }
            finally
            {
                if (viewModel != null)
                {
                    TestUiDispatcherHost.AwaitTaskOnDispatcher(viewModel.ShellShutdownWorkflow.RequestWindowCloseAsync(), "B1 close");
                    viewModel.SettingDialog.Dispose();
                }
                Directory.Delete(root, true);
            }
        });
    }
    [DataTestMethod]
    [DataRow(0)]
    [DataRow(1)]
    [DataRow(2)]
    public void DropInstallReportsRealReceiptIndependentlyOfPackageCount(int failureKind)
    {
        TestUiDispatcherHost.RunWindowTest(_ =>
        {
            string root = Path.Combine(Path.GetTempPath(), "FSDB-B-drop-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(root);
            MainWindowViewModel? viewModel = null;
            try
            {
                string source = Path.Combine(root, "DropSource");
                Directory.CreateDirectory(source);
                string sourceChart = Path.Combine(source, "chart.bms");
                File.WriteAllText(sourceChart, "#PLAYER 1\r\n#TITLE DropTarget\r\n#ARTIST Artist\r\n");
                string installRoot = Path.Combine(root, "Installed");
                string destination = Path.Combine(installRoot, "DropTarget");
                string dbPath = Path.Combine(root, "song.db");
                using (var db = new LR2SongDBExtended(dbPath))
                {
                    db.CreateTable<LR2SongDB.song>();
                    db.CreateTable<LR2SongDB.folder>();
                    db.CreateTable<LR2SongDBExtended.maintenance>();
                    if (failureKind == 2)
                        db.Execute("CREATE TRIGGER fail_drop BEFORE INSERT ON song WHEN NEW.path LIKE '%DropTarget%' BEGIN SELECT RAISE(ABORT, 'drop-primary-marker'); END;");
                }
                var dialogs = new FileDbReportRecordingDialogs();
                IFileMutationService files = failureKind == 0 ? new ResilientFileMutationService()
                    : new BmsLibraryPackageInstallServiceTests.FailingDestinationDeleteFileMutationService(
                        failureKind == 1 ? source : destination);
                var library = new TestBmsLibrary(dbPath, null, null, files, dialogs,
                    new TestUiScheduler(() => TestUiDispatcherHost.Dispatcher),
                    () => new BmsLibraryOptionsSnapshot { OperationModeLR2DB = false,
                        FolderNameFormat = "%TITLE%", BMSInstallDir = installRoot, KeepInstallablePackagesPending = false })
                { BMSFiles = [], SearchTargets = [root] };
                viewModel = MainWindowViewModelTestFactory.Create(new Settings(), dialogs);
                viewModel.StartupUpdateWorkflow.NotifyClosing();
                ((IStartupLibraryApplicationPort)viewModel).AttachStartupLibrary(library);
                PackageInstallCompletionReceipt? outcome = null;
                var completed = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
                viewModel.PackageInstallWorkflow.CompletionPublished += receipt => { outcome = receipt; completed.TrySetResult(true); };
                viewModel.PackageInstallWorkflow.FailurePublished += failure => completed.TrySetException(failure.Exception);
                bool inactiveAtReport = false;
                dialogs.OnMessage = () => inactiveAtReport = !viewModel.ChartMutationActivity.IsActive;
                viewModel.PackageInstallWorkflow.Enqueue([source]);
                TestUiDispatcherHost.AwaitTaskOnDispatcher(viewModel.PackageInstallWorkflow.WaitForIdleAsync(), "B2 idle");
                TestUiDispatcherHost.AwaitTaskOnDispatcher(completed.Task, "B2 completion");
                Assert.IsNotNull(outcome);
                Assert.AreEqual(failureKind == 2 ? 0 : 1, outcome.Packages.Count);
                Assert.AreEqual(failureKind == 0 ? 0 : 1, dialogs.Messages.Count);
                Assert.AreEqual(0, dialogs.ModelMessages);
                if (failureKind != 0)
                {
                    Assert.IsTrue(inactiveAtReport);
                    Assert.AreEqual(failureKind == 1 ? MessageBoxImage.Warning : MessageBoxImage.Error, dialogs.Messages[0].Icon);
                    Assert.AreEqual(failureKind == 1, outcome.HasDurableCommit);
                    Assert.AreEqual(failureKind == 2, outcome.ManualRecoveryRequired);
                    // The report is bounded; detailed primary and compensation causes remain
                    // in the immutable receipt and full diagnostics, not every UI error line.
                    StringAssert.Contains(outcome.MutationReceipt.Receipts.Single().Failure.ToString(),
                        failureKind == 1 ? "injected-destination-delete-failure" : "drop-primary-marker");
                    StringAssert.Contains(dialogs.Messages[0].MessageBoxText, source);
                }
                using var verifyDb = new LR2SongDBExtended(dbPath);
                Assert.AreEqual(failureKind != 2, verifyDb.Find<LR2SongDB.song>(Path.Combine(destination, "chart.bms")) != null);
                Assert.AreEqual(failureKind != 0, Directory.Exists(source));
            }
            finally
            {
                if (viewModel != null)
                {
                    TestUiDispatcherHost.AwaitTaskOnDispatcher(viewModel.ShellShutdownWorkflow.RequestWindowCloseAsync(), "B2 close");
                    viewModel.SettingDialog.Dispose();
                }
                Directory.Delete(root, true);
            }
        });
    }

    [TestMethod]
    public void CompiledTreeMenusPreservePackageSectionsAndPendingOperations()
    {
        var catalogCalls = new List<(string Action, PackageCatalogSection Section, ChartPackage? Package)>();
        var pendingEstimationCalls = new List<(string Action, object Request)>();
        var pendingInstallationCalls = new List<(string Action, object Request)>();
        var packageCatalogTerminal = new MainWindowPackageCatalogTerminal(
            section =>
            {
                catalogCalls.Add(("clear-all", section, null));
                return Task.FromResult(PackageCatalogMutationResult.Rejected);
            },
            (section, package) =>
            {
                catalogCalls.Add(("remove-package", section, package));
                return Task.FromResult(PackageCatalogMutationResult.Rejected);
            },
            request =>
            {
                catalogCalls.Add(("remove-selection", request.Section, null));
                return Task.FromResult(PackageCatalogMutationResult.Rejected);
            });
        var pendingInstallEstimationTerminal = new MainWindowPendingInstallEstimationTerminal(
            (kind, packages) =>
            {
                pendingEstimationCalls.Add(("search-package:" + kind, packages.Single()));
                return Task.CompletedTask;
            },
            request =>
            {
                pendingEstimationCalls.Add(("search-pending:" + request.Kind, request));
                return Task.CompletedTask;
            },
            request =>
            {
                pendingEstimationCalls.Add(("clear-pending", request));
                return Task.CompletedTask;
            },
            packages =>
            {
                pendingEstimationCalls.Add(("clear-packages", packages.Single()));
                return Task.CompletedTask;
            });
        var pendingInstallationTerminal = new MainWindowPendingInstallationTerminal(
            packages =>
            {
                pendingInstallationCalls.Add(("force-install", packages.Single()));
                return Task.FromResult(PendingPackageMutationResult.Rejected);
            },
            packages =>
            {
                pendingInstallationCalls.Add(("manual-install", packages.Single()));
                return Task.FromResult(PendingPackageMutationResult.Rejected);
            },
            request =>
            {
                pendingInstallationCalls.Add(("install-pending:" + request.Kind, request));
                return Task.FromResult(PendingPackageMutationResult.Rejected);
            });

        MainWindowPackageMaintenanceTestHarness.RunConstructorOnly(
            new Settings(),
            (viewModel, window) =>
            {
                ContextMenu installedMenu = (ContextMenu)window.FindResource("treeViewInstalledContextMenu");
                ContextMenu pendingMenu = (ContextMenu)window.FindResource("treeViewInstallPendingContextMenu");
                RaiseMenuClick(installedMenu.Items.OfType<MenuItem>().Single());
                RaiseMenuClick(pendingMenu.Items.OfType<MenuItem>().First());

                ChartPackage pendingPackage = new() { path = @"C:\wave6e-pending-package" };
                TreeViewItem pendingTarget = new() { DataContext = pendingPackage };
                ContextMenu packageMenu = (ContextMenu)window.FindResource("treeViewInstallPackageContextMenu");
                packageMenu.PlacementTarget = pendingTarget;
                MenuItem installMenu = packageMenu.Items.OfType<MenuItem>().Single(item => item.Items.Count == 5);
                MenuItem[] installCommands = installMenu.Items.OfType<MenuItem>().ToArray();
                Assert.AreEqual(5, installCommands.Length);

                foreach (MenuItem command in installCommands)
                {
                    RaiseMenuClick(command);
                }

                RaiseMenuClick(packageMenu.Items.OfType<MenuItem>().Last());

                ChartPackage installedPackage = new() { path = @"C:\wave6e-installed-package" };
                TreeViewItem installedTarget = new() { DataContext = installedPackage };
                ContextMenu installedPackageMenu = (ContextMenu)window.FindResource("treeViewInstalledFolderContextMenu");
                installedPackageMenu.PlacementTarget = installedTarget;
                RaiseMenuClick(installedPackageMenu.Items.OfType<MenuItem>().Last());

                Assert.AreEqual(2, catalogCalls.Count(call => call.Action == "clear-all"));
                Assert.IsTrue(catalogCalls.Any(call => call.Action == "clear-all" && call.Section == PackageCatalogSection.Installed));
                Assert.IsTrue(catalogCalls.Any(call => call.Action == "clear-all" && call.Section == PackageCatalogSection.Pending));
                Assert.IsTrue(catalogCalls.Any(call =>
                    call.Action == "remove-package"
                    && call.Section == PackageCatalogSection.Pending
                    && ReferenceEquals(call.Package, pendingPackage)));
                Assert.IsTrue(catalogCalls.Any(call =>
                    call.Action == "remove-package"
                    && call.Section == PackageCatalogSection.Installed
                    && ReferenceEquals(call.Package, installedPackage)));

                CollectionAssert.AreEquivalent(
                    new[]
                    {
                        "search-package:InstallDestination",
                        "search-package:MergeDestination",
                        "clear-packages"
                    },
                    pendingEstimationCalls.Select(call => call.Action).ToArray());
                CollectionAssert.AreEquivalent(
                    new[] { "manual-install", "force-install" },
                    pendingInstallationCalls.Select(call => call.Action).ToArray());
                Assert.IsTrue(pendingEstimationCalls.All(call =>
                    call.Request is ChartPackage package
                        ? ReferenceEquals(package, pendingPackage)
                        : call.Request is PendingInstallPackageOperationRequest operation
                            ? operation.Targets.Count == 0
                            : true));
                Assert.IsTrue(pendingInstallationCalls.All(call =>
                    call.Request is ChartPackage package
                        ? ReferenceEquals(package, pendingPackage)
                        : call.Request is PendingInstallPackageOperationRequest operation
                            ? operation.Targets.Count == 0
                            : true));
            },
            packageCatalogTerminal: packageCatalogTerminal,
            pendingInstallEstimationTerminal: pendingInstallEstimationTerminal,
            pendingInstallationTerminal: pendingInstallationTerminal);
    }

    [DataTestMethod]
    [DataRow(false, false)]
    [DataRow(false, true)]
    [DataRow(true, false)]
    [DataRow(true, true)]
    public void CompiledPendingInstallRoutesReportReceipt(bool chartRoute, bool manual)
    {
        var failure = new System.IO.IOException("four-route-cleanup-marker");
        var receipt = new FileDbMutationReceipt(Guid.NewGuid(),
            FileDbMutationTerminalState.CompletedWithCleanupFailure, true, 0, 1,
            [@"C:\pending-source"], [@"D:\installed"], [], [], [], failure, cleanupFailure: failure);
        var result = PendingPackageMutationResult.FromTerminal(new FileDbMutationBatchReceipt([receipt]));
        var dialogs = new FileDbReportRecordingDialogs();
        var pendingMutationViewTerminal = new MainWindowPendingPackageMutationViewTerminal(
            () => false, () => 1, _ => Task.FromResult(true), dialogs);
        int calls = 0;
        Task<PendingPackageMutationResult> Complete() { calls++; return Task.FromResult(result); }
        var pendingInstallationTerminal = new MainWindowPendingInstallationTerminal(
            _ => Complete(), _ => Complete(), _ => Complete());
        MainWindowPackageMaintenanceTestHarness.RunConstructorOnly(
            new Settings(),
            (viewModel, window) =>
            {
                ContextMenu menu;
                if (chartRoute)
                {
                    var chart = new BMSFile { path = @"C:\pending-source\chart.bms", hash = new string('a', 32) };
                    var entry = PackageChartEntry.FromChart(ChartFileProjection.FromBmsFile(chart));
                    var row = LibraryChartRow.FromPackageChartEntry(entry);
                    var table = (CustomTableView)window.FindName("customTableView");
                    table.ItemsSource = new List<object> { row };
                    table.SelectRowsByPredicate(_ => true);
                    viewModel.MainChartList.SetOperationContext(MainViewUpdateMode.PendingInstallFolderSelected);
                    menu = (ContextMenu)window.FindResource("tableContextMenu");
                    menu.PlacementTarget = new FrameworkElement { DataContext = row };
                }
                else
                {
                    menu = (ContextMenu)window.FindResource("treeViewInstallPackageContextMenu");
                    menu.PlacementTarget = new TreeViewItem { DataContext = new ChartPackage { path = @"C:\pending-source" } };
                }
                MenuItem group = menu.Items.OfType<MenuItem>()
                    .Single(item => item.Items.OfType<MenuItem>().Count() == 5);
                RaiseMenuClick(group.Items.OfType<MenuItem>().ElementAt(manual ? 2 : 3));
                Assert.AreEqual(1, calls);
                Assert.AreEqual(1, dialogs.Messages.Count);
                Assert.AreEqual(System.Windows.MessageBoxImage.Warning, dialogs.Messages[0].Icon);
                StringAssert.Contains(dialogs.Messages[0].MessageBoxText, failure.Message);
            },
            pendingInstallationTerminal: pendingInstallationTerminal,
            pendingPackageMutationViewTerminal: pendingMutationViewTerminal);
    }

    [TestMethod]
    public void CompiledMaintenanceAndAutoRenameMenusUseTypedTerminals()
    {
        var autoRenamePaths = new List<string>();
        var maintenanceCalls = new List<int>();
        var autoRenameTerminal = new MainWindowFolderAutoRenameTerminal(
            targets =>
            {
                Assert.IsNotNull(targets);
                return true;
            },
            path =>
            {
                autoRenamePaths.Add(path);
                return Task.CompletedTask;
            });
        var maintenanceTerminal = new MainWindowMaintenanceRescanTerminal(() =>
        {
            maintenanceCalls.Add(1);
            return Task.FromResult(MaintenanceRescanStartResult.Rejected);
        });

        MainWindowPackageMaintenanceTestHarness.RunConstructorOnly(
            new Settings(),
            (viewModel, window) =>
            {
                ContextMenu libraryMenu = (ContextMenu)window.FindResource("treeViewLibraryFolderContextMenu");
                TreeViewItem libraryTarget = new() { Header = @"C:\wave6e-library" };
                libraryMenu.PlacementTarget = libraryTarget;
                MenuItem renameAll = libraryMenu.Items.OfType<MenuItem>().Last();
                RaiseMenuClick(renameAll);

                ContextMenu tableMenu = (ContextMenu)window.FindResource("tableContextMenu");
                MenuItem fullScanMenu = tableMenu.Items
                    .OfType<MenuItem>()
                    .First(item => item.Name == "tableContextMenuItemFullScanCheck");
                MenuItem fullRescan = fullScanMenu.Items
                    .OfType<MenuItem>()
                    .First(item => item.Name == "tableContextMenuItemFullScanCheckAllCharts");
                RaiseMenuClick(fullRescan);

                Assert.AreEqual(1, autoRenamePaths.Count);
                Assert.AreEqual(@"C:\wave6e-library", autoRenamePaths[0]);
                Assert.AreEqual(1, maintenanceCalls.Count);
            },
            folderAutoRenameTerminal: autoRenameTerminal,
            maintenanceRescanTerminal: maintenanceTerminal);
    }

    [TestMethod]
    public void CompiledAutoRenameSelectionPreservesBmsAndBmsonTargets()
    {
        IReadOnlyList<ChartOperationTarget>? capturedTargets = null;
        var autoRenameTerminal = new MainWindowFolderAutoRenameTerminal(
            targets =>
            {
                capturedTargets = targets;
                return true;
            },
            _ => Task.CompletedTask);

        MainWindowPackageMaintenanceTestHarness.RunConstructorOnly(
            new Settings(),
            (viewModel, window) =>
            {
                var bmsFile = new BMSFile
                {
                    path = @"C:\wave6e-library\bms\chart.bms",
                    hash = "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa",
                    title = "BMS chart",
                    artist = "Artist"
                };
                var bmsonSong = new LR2SongDBExtended.bmson_song
                {
                    path = @"C:\wave6e-library\bmson\chart.bmson",
                    md5 = "bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb",
                    title = "bmson chart",
                    artist = "Artist"
                };
                LibraryChartRow bmsRow = LibraryChartRow.FromBmsFile(bmsFile);
                LibraryChartRow bmsonRow = LibraryChartRow.FromBmsonSong(bmsonSong);
                var rows = new List<object> { bmsRow, bmsonRow };

                CustomTableView table = (CustomTableView)window.FindName("customTableView");
                Assert.IsNotNull(table);
                table.ItemsSource = rows;
                table.SelectRowsByPredicate(_ => true);
                IReadOnlyList<object> selectedRows = table.GetSelectedRowsSnapshot();
                Assert.AreEqual(2, selectedRows.Count);
                IReadOnlyList<ChartOperationTarget> resolvedTargets = ChartOperationTargetSelectionResolver.Resolve(
                    new ChartOperationTargetSelectionRequest(
                        selectedRows,
                        ChartOperationSourceScope.Library,
                        ChartOperationCapabilities.MoveInLibrary));
                Assert.AreEqual(2, resolvedTargets.Count);
                viewModel.MainChartList.SetOperationContext(MainViewUpdateMode.FolderFilterSelected);
                Assert.AreEqual(ChartOperationSourceScope.Library, viewModel.MainChartList.CurrentOperationContext.SourceScope);

                ContextMenu tableMenu = (ContextMenu)window.FindResource("tableContextMenu");
                tableMenu.PlacementTarget = new FrameworkElement { DataContext = bmsRow };
                tableMenu.RaiseEvent(new RoutedEventArgs(ContextMenu.OpenedEvent, tableMenu));
                MenuItem rename = tableMenu.Items
                    .OfType<MenuItem>()
                    .First(item => item.Name == "tableContextMenuItemAutoRenameFolder");
                RaiseMenuClick(rename);

                Assert.IsNotNull(capturedTargets);
                Assert.AreEqual(2, capturedTargets.Count);
                Assert.IsTrue(capturedTargets.Any(target =>
                    target.Chart.Kind == ChartFileKind.Bms
                    && ReferenceEquals(target.Chart.GetBmsStorageOwner(), bmsFile)));
                Assert.IsTrue(capturedTargets.Any(target =>
                    target.Chart.Kind == ChartFileKind.Bmson
                    && ReferenceEquals(target.Chart.GetBmsonStorageOwner(), bmsonSong)));
            },
            folderAutoRenameTerminal: autoRenameTerminal);
    }

    [TestMethod]
    public void CompiledPendingBulkMenuUsesOneTypedTerminalPerOperation()
    {
        var calls = new List<string>();
        var deleteSources = new TaskCompletionSource<object?>(TaskCreationOptions.RunContinuationsAsynchronously);
        var renameZeroNotes = new TaskCompletionSource<object?>(TaskCreationOptions.RunContinuationsAsynchronously);
        var overwriteResources = new TaskCompletionSource<object?>(TaskCreationOptions.RunContinuationsAsynchronously);
        var bulkTerminal = new MainWindowPendingBulkMaintenanceTerminal(
            () =>
            {
                calls.Add("delete-sources");
                return deleteSources.Task;
            },
            () =>
            {
                calls.Add("rename-zero-notes");
                return renameZeroNotes.Task;
            },
            () =>
            {
                calls.Add("overwrite-resources");
                return overwriteResources.Task;
            });

        MainWindowPackageMaintenanceTestHarness.RunConstructorOnly(
            new Settings(),
            (_, window) =>
            {
                ContextMenu pendingMenu = (ContextMenu)window.FindResource("treeViewInstallPendingContextMenu");
                MenuItem advanced = pendingMenu.Items
                    .OfType<MenuItem>()
                    .Single(item => item.Items.OfType<MenuItem>().Count() == 3);

                TaskCompletionSource<object?>[] completions =
                [deleteSources, overwriteResources, renameZeroNotes];
                string[] expected = ["delete-sources", "overwrite-resources", "rename-zero-notes"];
                MenuItem[] commands = advanced.Items.OfType<MenuItem>().ToArray();
                Assert.AreEqual(3, commands.Length);
                for (int i = 0; i < commands.Length; i++)
                {
                    var args = new RoutedEventArgs(MenuItem.ClickEvent, commands[i]);
                    commands[i].RaiseEvent(args);
                    Assert.IsTrue(args.Handled);
                    Assert.IsFalse(completions[i].Task.IsCompleted);
                    completions[i].SetResult(null);
                    TestUiDispatcherHost.Drain();
                    Assert.AreEqual(expected[i], calls[i]);
                }
                Assert.AreEqual(3, calls.Count);
            },
            pendingBulkMaintenanceTerminal: bulkTerminal);
    }

    [TestMethod]
    public void CompiledDuplicateContextAndShortcutRoutesUseOneTypedTerminal()
    {
        var mergeCalls = new List<(string Source, string Destination, DuplicateGroup Group)>();
        var cleanupCalls = new List<(string Folder, DuplicateGroup Group)>();
        var mergeCompletion = new TaskCompletionSource<DuplicateMaintenanceMutationResult>(TaskCreationOptions.RunContinuationsAsynchronously);
        var keyboardMergeCompletion = new TaskCompletionSource<DuplicateMaintenanceMutationResult>(TaskCreationOptions.RunContinuationsAsynchronously);
        var cleanupCompletion = new TaskCompletionSource<DuplicateMaintenanceMutationResult>(TaskCreationOptions.RunContinuationsAsynchronously);
        var mergeInvocation = 0;
        var shortcutProbe = 0;
        var duplicateTerminal = new MainWindowDuplicateMaintenanceTerminal(
            (source, destination, group) =>
            {
                mergeCalls.Add((source, destination, group));
                return Interlocked.Increment(ref mergeInvocation) == 1
                    ? mergeCompletion.Task
                    : keyboardMergeCompletion.Task;
            },
            (group, folder) =>
            {
                cleanupCalls.Add((folder, group));
                return cleanupCompletion.Task;
            },
            () =>
            {
                shortcutProbe++;
                return true;
            });

        MainWindowPackageMaintenanceTestHarness.RunConstructorOnly(
            new Settings(),
            (viewModel, window) =>
            {
                window.Width = 900d;
                window.Height = 700d;
                using var visualHost = new HwndSource(new HwndSourceParameters("MainWindowDuplicateRouteTest")
                {
                    Width = 900,
                    Height = 700,
                    PositionX = 0,
                    PositionY = 0
                });
                visualHost.RootVisual = (System.Windows.Media.Visual)window.Content;
                window.Measure(new Size(window.Width, window.Height));
                window.Arrange(new Rect(0d, 0d, window.Width, window.Height));
                window.UpdateLayout();
                var sourceChart = new BMSFile
                {
                    path = @"C:\wave6e-duplicate\source\chart.bms",
                    hash = "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa",
                    title = "Duplicate"
                };
                var group = new DuplicateGroup(
                    [ChartFileProjection.FromBmsFile(sourceChart)],
                    [@"C:\wave6e-duplicate\source", @"C:\wave6e-duplicate\destination"]);
                TreeViewItem duplicateRoot = (TreeViewItem)window.FindName("treeViewItemSearchDuplicated");
                duplicateRoot.ItemsSource = new[] { group };
                duplicateRoot.IsExpanded = true;
                duplicateRoot.ApplyTemplate();
                duplicateRoot.Measure(new Size(900d, 700d));
                duplicateRoot.Arrange(new Rect(0d, 0d, 900d, 700d));
                duplicateRoot.UpdateLayout();
                window.UpdateLayout();
                TreeViewItem groupItem = duplicateRoot.ItemContainerGenerator.ContainerFromIndex(0) as TreeViewItem;
                Assert.IsNotNull(
                    groupItem,
                    $"duplicate group container was not generated (items={duplicateRoot.Items.Count}, status={duplicateRoot.ItemContainerGenerator.Status}, visibility={duplicateRoot.Visibility}, expanded={duplicateRoot.IsExpanded}, parent={duplicateRoot.Parent?.GetType().Name ?? "none"})");
                groupItem!.IsExpanded = true;
                groupItem.UpdateLayout();
                TreeViewItem folderItem = groupItem.ItemContainerGenerator.ContainerFromIndex(0) as TreeViewItem;
                Assert.IsNotNull(folderItem, "duplicate folder container was not generated");
                Assert.IsNotNull(groupItem.ItemContainerStyle, "duplicate folder style was not loaded");
                folderItem!.Style = groupItem.ItemContainerStyle;
                folderItem.ContextMenu = (ContextMenu)window.FindResource("treeViewDuplicateFolderContextMenu");
                Assert.IsInstanceOfType(folderItem.DataContext, typeof(string));
                Assert.AreEqual(2, group.Folders.Count);
                Assert.AreEqual(
                    DuplicateFolderKeyboardActionKind.Merge,
                    viewModel.DuplicateMaintenanceWorkflow.CaptureDuplicateFolderKeyboardAction(
                        group,
                        (string)folderItem.DataContext).Kind);
                ContextMenu menu = folderItem.ContextMenu;
                menu.PlacementTarget = folderItem;
                MenuItem mergeMenu = (MenuItem)menu.Items
                    .OfType<MenuItem>()
                    .Single(item => item.Name == "treeViewDuplicateFolderContextMenuItemMergeInto");
                menu.RaiseEvent(new RoutedEventArgs(ContextMenu.OpenedEvent, menu));
                mergeMenu.ApplyTemplate();
                mergeMenu.Measure(new Size(400d, 200d));
                mergeMenu.Arrange(new Rect(0d, 0d, 400d, 200d));
                mergeMenu.UpdateLayout();
                Assert.IsNotNull(mergeMenu.ItemContainerStyle, "duplicate merge target style was not loaded");
                mergeMenu.ItemsSource = null;
                mergeMenu.Items.Clear();
                var target = new MenuItem
                {
                    DataContext = @"C:\wave6e-duplicate\destination",
                    Tag = folderItem,
                    Style = mergeMenu.ItemContainerStyle
                };
                mergeMenu.Items.Add(target);
                var contextArgs = new RoutedEventArgs(MenuItem.ClickEvent, target);
                target!.RaiseEvent(contextArgs);
                Assert.IsTrue(contextArgs.Handled);
                Assert.IsFalse(mergeCompletion.Task.IsCompleted);
                Assert.AreEqual(1, mergeCalls.Count);
                Assert.AreEqual(@"C:\wave6e-duplicate\source", mergeCalls[0].Source);
                Assert.AreEqual(@"C:\wave6e-duplicate\destination", mergeCalls[0].Destination);
                Assert.AreSame(group, mergeCalls[0].Group);
                mergeCompletion.SetResult(DuplicateMaintenanceMutationResult.Rejected(null));
                TestUiDispatcherHost.Drain();

                // Applying the context-menu result may recycle the generated TreeViewItem.
                // Resolve the current visual container before driving the keyboard route.
                groupItem = duplicateRoot.ItemContainerGenerator.ContainerFromIndex(0) as TreeViewItem;
                Assert.IsNotNull(groupItem);
                groupItem!.IsExpanded = true;
                groupItem.UpdateLayout();
                folderItem = groupItem.ItemContainerGenerator.ContainerFromIndex(0) as TreeViewItem;
                Assert.IsNotNull(folderItem);
                folderItem!.Style = groupItem.ItemContainerStyle;
                folderItem.ContextMenu = menu;

                var keyArgs = new KeyEventArgs(
                    Keyboard.PrimaryDevice,
                    PresentationSource.FromVisual((System.Windows.Media.Visual)window.Content),
                    0,
                    Key.G)
                {
                    RoutedEvent = UIElement.KeyDownEvent,
                    Source = folderItem
                };
                Assert.AreEqual(Key.G, keyArgs.Key);
                Assert.AreSame(viewModel, window.DataContext);
                Assert.AreEqual(@"C:\wave6e-duplicate\source", folderItem.DataContext);
                Assert.AreSame(group, WPFUtil.FindVisualParent<TreeViewItem>(folderItem)?.DataContext);
                folderItem.RaiseEvent(keyArgs);
                Assert.AreEqual(1, shortcutProbe);
                Assert.IsTrue(keyArgs.Handled);
                Assert.IsFalse(keyboardMergeCompletion.Task.IsCompleted);
                Assert.AreEqual(2, mergeCalls.Count);
                keyboardMergeCompletion.SetResult(DuplicateMaintenanceMutationResult.Rejected(null));
                TestUiDispatcherHost.Drain();

                var singleFolderGroup = new DuplicateGroup(
                    [ChartFileProjection.FromBmsFile(sourceChart)],
                    [@"C:\wave6e-duplicate\source"]);
                duplicateRoot.ItemsSource = new[] { singleFolderGroup };
                duplicateRoot.IsExpanded = true;
                duplicateRoot.ApplyTemplate();
                duplicateRoot.Measure(new Size(900d, 700d));
                duplicateRoot.Arrange(new Rect(0d, 0d, 900d, 700d));
                duplicateRoot.UpdateLayout();
                groupItem = duplicateRoot.ItemContainerGenerator.ContainerFromIndex(0) as TreeViewItem;
                Assert.IsNotNull(groupItem);
                groupItem!.IsExpanded = true;
                groupItem!.UpdateLayout();
                folderItem = groupItem.ItemContainerGenerator.ContainerFromIndex(0) as TreeViewItem;
                Assert.IsNotNull(folderItem);
                folderItem!.ContextMenu = menu;
                var cleanupArgs = new KeyEventArgs(
                    Keyboard.PrimaryDevice,
                    PresentationSource.FromVisual((System.Windows.Media.Visual)window.Content),
                    0,
                    Key.G)
                {
                    RoutedEvent = UIElement.KeyDownEvent,
                    Source = folderItem
                };
                folderItem.RaiseEvent(cleanupArgs);
                Assert.IsTrue(cleanupArgs.Handled);
                Assert.IsFalse(cleanupCompletion.Task.IsCompleted);
                Assert.AreEqual(1, cleanupCalls.Count);
                Assert.AreSame(singleFolderGroup, cleanupCalls[0].Group);
                Assert.AreEqual(@"C:\wave6e-duplicate\source", cleanupCalls[0].Folder);
                cleanupCompletion.SetResult(DuplicateMaintenanceMutationResult.Rejected(null));
                TestUiDispatcherHost.Drain();
            },
            duplicateMaintenanceTerminal: duplicateTerminal);
    }

    [TestMethod]
    public void CompiledSelectedChartRoutesPreservePendingTargetAndHandledBeforeCompletion()
    {
        var pendingEstimateCalls = new List<(string Kind, PendingInstallDestinationSearchRequest Request)>();
        var clearPendingCalls = new List<PendingInstallDestinationClearRequest>();
        var installCalls = new List<PendingInstallPackageOperationRequest>();
        var repairCalls = new List<(string Kind, RepairInstalledLocationRequest Request)>();
        var catalogRemovalCalls = new List<PackageCatalogRemovalRequest>();
        var searchInstallCompletion = new TaskCompletionSource<object?>(TaskCreationOptions.RunContinuationsAsynchronously);
        var searchMergeCompletion = new TaskCompletionSource<object?>(TaskCreationOptions.RunContinuationsAsynchronously);
        var clearCompletion = new TaskCompletionSource<object?>(TaskCreationOptions.RunContinuationsAsynchronously);
        var forceCompletion = new TaskCompletionSource<PendingPackageMutationResult>(TaskCreationOptions.RunContinuationsAsynchronously);
        var manualCompletion = new TaskCompletionSource<PendingPackageMutationResult>(TaskCreationOptions.RunContinuationsAsynchronously);
        var repairSearchCompletion = new TaskCompletionSource<object?>(TaskCreationOptions.RunContinuationsAsynchronously);
        var repairClearCompletion = new TaskCompletionSource<object?>(TaskCreationOptions.RunContinuationsAsynchronously);
        var repairFixCompletion = new TaskCompletionSource<object?>(TaskCreationOptions.RunContinuationsAsynchronously);
        var catalogCompletion = new TaskCompletionSource<PackageCatalogMutationResult>(TaskCreationOptions.RunContinuationsAsynchronously);
        var pendingFile = new BMSFile
        {
            path = @"C:\wave6e-pending\chart.bms",
            hash = "cccccccccccccccccccccccccccccccc",
            title = "Pending chart"
        };
        PackageChartEntry pendingEntry = PackageChartEntry.FromChart(ChartFileProjection.FromBmsFile(pendingFile));
        LibraryChartRow pendingRow = LibraryChartRow.FromPackageChartEntry(pendingEntry);
        var pendingEstimationTerminal = new MainWindowPendingInstallEstimationTerminal(
            (kind, packages) => Task.CompletedTask,
            request =>
            {
                pendingEstimateCalls.Add((request.Kind.ToString(), request));
                return request.Kind == PendingInstallDestinationSearchKind.InstallDestination
                    ? searchInstallCompletion.Task
                    : searchMergeCompletion.Task;
            },
            request =>
            {
                clearPendingCalls.Add(request);
                return clearCompletion.Task;
            },
            _ => Task.CompletedTask);
        var pendingInstallationTerminal = new MainWindowPendingInstallationTerminal(
            _ =>
            {
                return forceCompletion.Task;
            },
            _ => manualCompletion.Task,
            request =>
            {
                installCalls.Add(request);
                return request.Kind == PendingInstallPackageOperationKind.ForceInstall
                    ? forceCompletion.Task
                    : manualCompletion.Task;
            });
        var repairTerminal = new MainWindowInstalledLocationRepairTerminal(
            request =>
            {
                repairCalls.Add(("search", request));
                return repairSearchCompletion.Task;
            },
            request =>
            {
                repairCalls.Add(("clear", request));
                return repairClearCompletion.Task;
            },
            request =>
            {
                repairCalls.Add(("fix", request));
                return repairFixCompletion.Task;
            });
        var catalogTerminal = new MainWindowPackageCatalogTerminal(
            _ => Task.FromResult(PackageCatalogMutationResult.Rejected),
            (_, _) => Task.FromResult(PackageCatalogMutationResult.Rejected),
            request =>
            {
                catalogRemovalCalls.Add(request);
                return catalogCompletion.Task;
            });

        MainWindowPackageMaintenanceTestHarness.RunConstructorOnly(
            new Settings(),
            (viewModel, window) =>
            {
                CustomTableView table = (CustomTableView)window.FindName("customTableView");
                table.ItemsSource = new List<object> { pendingRow };
                table.SelectRowsByPredicate(_ => true);
                viewModel.MainChartList.SetOperationContext(MainViewUpdateMode.PendingInstallFolderSelected);
                ContextMenu tableMenu = (ContextMenu)window.FindResource("tableContextMenu");
                tableMenu.PlacementTarget = new FrameworkElement { DataContext = pendingRow };

                MenuItem installGroup = tableMenu.Items
                    .OfType<MenuItem>()
                    .Single(item => item.Items.OfType<MenuItem>().Count() == 5);
                MenuItem[] installCommands = installGroup.Items.OfType<MenuItem>().ToArray();
                Assert.AreEqual(5, installCommands.Length);
                var searchInstallArgs = new RoutedEventArgs(MenuItem.ClickEvent, installCommands[0]);
                installCommands[0].RaiseEvent(searchInstallArgs);
                Assert.IsTrue(searchInstallArgs.Handled);
                Assert.IsFalse(searchInstallCompletion.Task.IsCompleted);
                Assert.AreEqual(PendingInstallDestinationSearchKind.InstallDestination, pendingEstimateCalls[0].Request.Kind);
                Assert.AreEqual(1, pendingEstimateCalls.Count);
                Assert.AreEqual(1, pendingEstimateCalls[0].Request.PackageTargets.Count);
                Assert.AreEqual(1, pendingEstimateCalls[0].Request.SelectedRowCount);
                Assert.AreSame(pendingEntry, pendingEstimateCalls[0].Request.PackageTargets[0].PackageEntry);
                searchInstallCompletion.SetResult(null);
                TestUiDispatcherHost.Drain();

                var searchMergeArgs = new RoutedEventArgs(MenuItem.ClickEvent, installCommands[1]);
                installCommands[1].RaiseEvent(searchMergeArgs);
                Assert.IsTrue(searchMergeArgs.Handled);
                Assert.IsFalse(searchMergeCompletion.Task.IsCompleted);
                Assert.AreEqual(PendingInstallDestinationSearchKind.MergeDestination, pendingEstimateCalls[1].Request.Kind);
                Assert.AreEqual(2, pendingEstimateCalls.Count);
                Assert.AreEqual(1, pendingEstimateCalls[1].Request.PackageTargets.Count);
                Assert.AreEqual(1, pendingEstimateCalls[1].Request.SelectedRowCount);
                Assert.AreSame(pendingEntry, pendingEstimateCalls[1].Request.PackageTargets[0].PackageEntry);
                searchMergeCompletion.SetResult(null);
                TestUiDispatcherHost.Drain();

                var manualArgs = new RoutedEventArgs(MenuItem.ClickEvent, installCommands[2]);
                installCommands[2].RaiseEvent(manualArgs);
                Assert.IsTrue(manualArgs.Handled);
                Assert.IsFalse(manualCompletion.Task.IsCompleted);
                Assert.AreEqual(1, installCalls.Count);
                Assert.AreEqual(PendingInstallPackageOperationKind.ManualInstall, installCalls[^1].Kind);
                Assert.AreEqual(1, installCalls[^1].Targets.Count);
                Assert.AreEqual(1, installCalls[^1].SelectedRowCount);
                Assert.AreSame(pendingEntry, installCalls[^1].Targets[0].PackageEntry);
                manualCompletion.SetResult(PendingPackageMutationResult.Rejected);
                TestUiDispatcherHost.Drain();

                var forceArgs = new RoutedEventArgs(MenuItem.ClickEvent, installCommands[3]);
                installCommands[3].RaiseEvent(forceArgs);
                Assert.IsTrue(forceArgs.Handled);
                Assert.IsFalse(forceCompletion.Task.IsCompleted);
                Assert.AreEqual(2, installCalls.Count);
                Assert.AreEqual(PendingInstallPackageOperationKind.ForceInstall, installCalls[^1].Kind);
                Assert.AreEqual(1, installCalls[^1].Targets.Count);
                Assert.AreEqual(1, installCalls[^1].SelectedRowCount);
                Assert.AreSame(pendingEntry, installCalls[^1].Targets[0].PackageEntry);
                forceCompletion.SetResult(PendingPackageMutationResult.Rejected);
                TestUiDispatcherHost.Drain();

                var clearArgs = new RoutedEventArgs(MenuItem.ClickEvent, installCommands[4]);
                installCommands[4].RaiseEvent(clearArgs);
                Assert.IsTrue(clearArgs.Handled);
                Assert.IsFalse(clearCompletion.Task.IsCompleted);
                Assert.AreEqual(1, clearPendingCalls.Count);
                Assert.AreEqual(1, clearPendingCalls[0].PackageTargets.Count);
                Assert.AreEqual(1, clearPendingCalls[0].PackageTargets.Count + clearPendingCalls[0].LooseTargets.Count);
                Assert.AreSame(pendingEntry, clearPendingCalls.Single().PackageTargets[0].PackageEntry);
                clearCompletion.SetResult(null);
                TestUiDispatcherHost.Drain();

                MenuItem selectedRemoval = tableMenu.Items
                    .OfType<MenuItem>()
                    .Single(item => item.Name == "tableContextMenuItemDeleteInstallPackages");
                var removalArgs = new RoutedEventArgs(MenuItem.ClickEvent, selectedRemoval);
                selectedRemoval.RaiseEvent(removalArgs);
                Assert.IsTrue(removalArgs.Handled);
                Assert.IsFalse(catalogCompletion.Task.IsCompleted);
                Assert.AreEqual(1, catalogRemovalCalls.Count);
                Assert.AreEqual(1, catalogRemovalCalls[0].Targets.Count);
                Assert.AreSame(pendingEntry, catalogRemovalCalls.Single().Targets[0].PackageEntry);
                catalogCompletion.SetResult(PackageCatalogMutationResult.Rejected);
                TestUiDispatcherHost.Drain();

                var installedFile = new BMSFile
                {
                    path = @"C:\wave6e-installed\chart.bms",
                    hash = "dddddddddddddddddddddddddddddddd",
                    title = "Installed chart"
                };
                LibraryChartRow installedRow = LibraryChartRow.FromBmsFile(installedFile);
                table.ItemsSource = new List<object> { installedRow };
                table.SelectRowsByPredicate(_ => true);
                viewModel.MainChartList.SetOperationContext(MainViewUpdateMode.FolderFilterSelected);
                tableMenu.PlacementTarget = new FrameworkElement { DataContext = installedRow };
                MenuItem repairGroup = tableMenu.Items
                    .OfType<MenuItem>()
                    .Single(item => item.Items.OfType<MenuItem>().Count() == 3);
                MenuItem[] repairCommands = repairGroup.Items.OfType<MenuItem>().ToArray();
                var repairSearchArgs = new RoutedEventArgs(MenuItem.ClickEvent, repairCommands[0]);
                repairCommands[0].RaiseEvent(repairSearchArgs);
                Assert.IsTrue(repairSearchArgs.Handled);
                Assert.IsFalse(repairSearchCompletion.Task.IsCompleted);
                Assert.AreEqual(1, repairCalls.Count);
                Assert.AreEqual("search", repairCalls.Single().Kind);
                Assert.AreEqual(1, repairCalls.Single().Request.Targets.Count);
                Assert.AreSame(installedRow.Chart, repairCalls.Single().Request.Targets[0].Chart);
                Assert.AreSame(installedFile, repairCalls.Single().Request.Targets[0].Chart.GetBmsStorageOwner());
                repairSearchCompletion.SetResult(null);
                TestUiDispatcherHost.Drain();

                var repairFixArgs = new RoutedEventArgs(MenuItem.ClickEvent, repairCommands[1]);
                repairCommands[1].RaiseEvent(repairFixArgs);
                Assert.IsTrue(repairFixArgs.Handled);
                Assert.IsFalse(repairFixCompletion.Task.IsCompleted);
                Assert.AreEqual(2, repairCalls.Count);
                Assert.AreEqual("fix", repairCalls[^1].Kind);
                Assert.AreEqual(1, repairCalls[^1].Request.Targets.Count);
                Assert.AreSame(installedRow.Chart, repairCalls[^1].Request.Targets[0].Chart);
                Assert.AreSame(installedFile, repairCalls[^1].Request.Targets[0].Chart.GetBmsStorageOwner());
                repairFixCompletion.SetResult(null);
                TestUiDispatcherHost.Drain();

                viewModel.MainChartList.SetOperationContext(MainViewUpdateMode.FullScanAllChartsFilterSelected);
                Assert.AreEqual(1, table.GetSelectedRowsSnapshot().Count);
                var repairClearArgs = new RoutedEventArgs(MenuItem.ClickEvent, repairCommands[2]);
                repairCommands[2].RaiseEvent(repairClearArgs);
                Assert.IsTrue(repairClearArgs.Handled);
                Assert.IsFalse(repairClearCompletion.Task.IsCompleted);
                Assert.AreEqual(3, repairCalls.Count);
                Assert.AreEqual("clear", repairCalls[^1].Kind);
                Assert.AreEqual(1, repairCalls[^1].Request.Targets.Count);
                Assert.AreSame(installedRow.Chart, repairCalls[^1].Request.Targets[0].Chart);
                Assert.AreSame(installedFile, repairCalls[^1].Request.Targets[0].Chart.GetBmsStorageOwner());
                repairClearCompletion.SetResult(null);
                TestUiDispatcherHost.Drain();
            },
            packageCatalogTerminal: catalogTerminal,
            pendingInstallEstimationTerminal: pendingEstimationTerminal,
            pendingInstallationTerminal: pendingInstallationTerminal,
            installedLocationRepairTerminal: repairTerminal);
    }

    private static MenuItem FindMenuItem(ItemsControl root, string name)
    {
        foreach (MenuItem item in root.Items.OfType<MenuItem>())
        {
            if (item.Name == name)
            {
                return item;
            }
            if (item.Items.Count > 0)
            {
                MenuItem nested = FindMenuItem(item, name);
                if (nested != null)
                {
                    return nested;
                }
            }
        }
        throw new AssertFailedException($"Menu item '{name}' was not found.");
    }

    private static void RaiseMenuClick(MenuItem item)
        => item.RaiseEvent(new RoutedEventArgs(MenuItem.ClickEvent, item));
}

internal static class MainWindowPackageMaintenanceTestHarness
{
    internal static void RunConstructorOnly(
        Settings settings,
        Action<MainWindowViewModel, MainWindow> test,
        MainWindowFolderAutoRenameTerminal? folderAutoRenameTerminal = null,
        MainWindowDuplicateMaintenanceTerminal? duplicateMaintenanceTerminal = null,
        MainWindowMaintenanceRescanTerminal? maintenanceRescanTerminal = null,
        MainWindowPackageCatalogTerminal? packageCatalogTerminal = null,
        MainWindowPendingInstallEstimationTerminal? pendingInstallEstimationTerminal = null,
        MainWindowPendingInstallationTerminal? pendingInstallationTerminal = null,
        MainWindowInstalledLocationRepairTerminal? installedLocationRepairTerminal = null,
        MainWindowPendingBulkMaintenanceTerminal? pendingBulkMaintenanceTerminal = null,
        MainWindowMainChartCellEditTerminal? mainChartCellEditTerminal = null,
        MainWindowSelectedChartContextMenuTerminals? selectedChartContextMenuTerminals = null,
        MainWindowPlaybackTerminal? playbackTerminal = null,
        MainWindowPlaylistWorkspaceTerminals? playlistWorkspaceTerminals = null,
        MainWindowPendingPackageMutationViewTerminal? pendingPackageMutationViewTerminal = null,
        Action<MainWindowViewModel>? prepareViewModel = null,
        IUiDialogService? playlistWorkspaceDialogService = null)
    {
        TestUiDispatcherHost.RunWindowTest(_ =>
        {
            MainWindowViewModel? viewModel = null;
            MainWindow? window = null;
            var windowClosed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var lifetime = new MainWindowPresentationTestHarness.PresentationApplicationLifetime();
            bool hadPreviousViewModelResource = Application.Current.Resources.Contains("vm");
            object? previousViewModelResource = hadPreviousViewModelResource
                ? Application.Current.Resources["vm"]
                : null;
            try
            {
                viewModel = new ApplicationComposition(
                    settingsEditSession: new NoOpSettingsEditSession(settings),
                    uiScheduler: new TestUiScheduler(() => TestUiDispatcherHost.Dispatcher),
                    applicationLifetime: lifetime,
                    cultureCatalog: TestApplicationContext.CreateCultureCatalog(),
                    playlistWorkspaceDialogService: playlistWorkspaceDialogService)
                    .CreateMainWindowViewModelForTest();
                viewModel.StartupUpdateWorkflow.NotifyClosing();
                viewModel.ProgressHub.StartupProgress.SetStartupUiInteractionBlocked(false);
                prepareViewModel?.Invoke(viewModel);
                Application.Current.Resources["vm"] = viewModel;
                window = new MainWindow(
                    viewModel,
                    settingsWindowCreated: null,
                    libraryReloadMenuTerminal: null,
                    regularLibraryTreeTerminal: null,
                    maintenanceTreeTerminal: null,
                    installTreeTerminal: null,
                    zeroNoteRecheckTerminal: null,
                    columnResetTerminal: null,
                    rootFolderUnregisterTerminal: null,
                    folderAutoRenameTerminal,
                    duplicateMaintenanceTerminal,
                    maintenanceRescanTerminal,
                    packageCatalogTerminal,
                    pendingInstallEstimationTerminal,
                    pendingInstallationTerminal,
                    installedLocationRepairTerminal,
                    pendingBulkMaintenanceTerminal,
                    mainChartCellEditTerminal,
                    selectedChartContextMenuTerminals,
                    playbackTerminal,
                    playlistWorkspaceTerminals,
                    pendingPackageMutationViewTerminal: pendingPackageMutationViewTerminal,
                    playlistWorkspaceDialogService: playlistWorkspaceDialogService);
                // Constructor-only tests rehost MainWindow.Content in an on-screen HwndSource when they
                // exercise compiled pointer routes. Keep the unshown Window's transform on that same
                // monitor so MouseEventArgs.GetPosition is not based on a saved off-screen placement.
                window.WindowStartupLocation = WindowStartupLocation.Manual;
                window.Left = SystemParameters.WorkArea.Left;
                window.Top = SystemParameters.WorkArea.Top;
                window.ShowActivated = false;
                window.ShowInTaskbar = false;
                window.Closed += (_, _) => windowClosed.TrySetResult();
                TestUiDispatcherHost.Drain();
                test(viewModel, window);
            }
            finally
            {
                try
                {
                    if (window != null && !windowClosed.Task.IsCompleted)
                    {
                        window.Close();
                        TestUiDispatcherHost.AwaitTaskOnDispatcher(
                            lifetime.ShutdownRequested.Task,
                            "MainWindowPackageMaintenanceTestHarness.terminal-shutdown");
                        window.Close();
                        TestUiDispatcherHost.AwaitTaskOnDispatcher(
                            windowClosed.Task,
                            "MainWindowPackageMaintenanceTestHarness.window-closed");
                    }
                }
                finally
                {
                    try
                    {
                        viewModel?.SettingDialog.Dispose();
                    }
                    finally
                    {
                        if (hadPreviousViewModelResource)
                        {
                            Application.Current.Resources["vm"] = previousViewModelResource;
                        }
                        else
                        {
                            Application.Current.Resources.Remove("vm");
                        }
                    }
                }
            }
        });
    }

}
