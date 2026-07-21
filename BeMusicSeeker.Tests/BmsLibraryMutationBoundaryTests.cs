using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.IO;
using System.Linq;
using System.Threading;
using System.Windows;
using System.Windows.Threading;
using BeMusicSeeker.Models;
using BeMusicSeeker.Models.BmsLibraryInternal;
using Livet;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace BeMusicSeeker.Tests;

[TestClass]
public sealed class BmsLibraryMutationBoundaryTests
{
    [TestMethod]
    public void OperationDialogScope_QueuesOkMessagesUntilFlush()
    {
        TestResourceInitializer.EnsureJapaneseResources();
        WithTemporarySongDb(delegate (string songDbPath)
        {
            var dialogService = new RecordingDialogService();
            var library = new BMSLibrary(songDbPath, null!, null, null!, dialogService);
            string missingDirectoryPath = Path.Combine(Path.GetTempPath(), "BeMusicSeeker_MutationBoundary_" + Guid.NewGuid().ToString("N"));

            using BMSLibrary.OperationDialogScope scope = library.BeginOperationDialogScope();
            library.RenameChartFolder(missingDirectoryPath, "RenamedFolder");

            Assert.AreEqual(0, dialogService.CallCount);
            Assert.AreEqual(1, scope.Messages.Count);
            scope.Flush();
            Assert.AreEqual(1, dialogService.CallCount);
            StringAssert.Contains(dialogService.GetCall(0).Message, missingDirectoryPath);
        });
    }

    [TestMethod]
    public void OperationDialogScope_RejectsInteractivePromptWhenPreflightDecisionIsMissing()
    {
        TestResourceInitializer.EnsureJapaneseResources();
        WithTemporarySongDb(delegate (string songDbPath)
        {
            var dialogService = new RecordingDialogService
            {
                ResultToReturn = MessageBoxResult.Yes
            };
            var library = new BMSLibrary(songDbPath, null!, null, null!, dialogService);
            var pendingFile = new TestableBmsFile
            {
                path = "C:\\Pending\\Pkg\\chart.bms"
            };
            var pendingPackage = ChartPackage.FromChartEntries(
                [ChartPackageTestExtensions.CreateEntryWithInstallDestination(pendingFile, "C:\\Installed\\Pkg")]);
            pendingPackage.path = "C:\\Pending\\Pkg";
            pendingPackage.delete_parent = false;
            library.ChartPackagesPending = CreatePackageCollection([pendingPackage]);

            using BMSLibrary.OperationDialogScope scope = library.BeginOperationDialogScope();

            Assert.ThrowsException<InvalidOperationException>(() => library.ForceInstallPendingPackages([pendingPackage]));
            Assert.AreEqual(0, dialogService.CallCount);
            Assert.AreSame(pendingPackage, library.ChartPackagesPending.Single());
        });
    }

    [TestMethod]
    public void ForceInstallPendingPackages_WithExplicitOverrideDecisionDoesNotPromptInsideMutationScope()
    {
        TestResourceInitializer.EnsureJapaneseResources();
        WithTemporarySongDb(delegate (string songDbPath)
        {
            var dialogService = new RecordingDialogService();
            var library = new BMSLibrary(songDbPath, null!, null, null!, dialogService);
            var pendingFile = new TestableBmsFile
            {
                path = "C:\\Pending\\Pkg\\chart.bms"
            };
            var pendingPackage = ChartPackage.FromChartEntries(
                [ChartPackageTestExtensions.CreateEntryWithInstallDestination(pendingFile, "C:\\Installed\\Pkg")]);
            pendingPackage.path = "C:\\Pending\\Pkg";
            pendingPackage.delete_parent = false;
            library.ChartPackagesPending = CreatePackageCollection([pendingPackage]);

            using BMSLibrary.OperationDialogScope scope = library.BeginOperationDialogScope();
            library.ForceInstallPendingPackages([pendingPackage], approveNormalInstallOverride: false);
            scope.Flush();

            Assert.AreEqual(0, dialogService.CallCount);
            Assert.AreSame(pendingPackage, library.ChartPackagesPending.Single());
        });
    }

    [TestMethod]
    public void MainWindowViewModel_UsesChartPackageMutationBoundary()
    {
        string source = SourceTextTestHelper.ReadMainWindowViewModelSourceText();
        string installDestinationSource = SourceTextTestHelper.ReadProductionSourceText(
            "BeMusicSeeker", "ViewModels", "MainWindow", "InstallDestinationWorkflowOwner.cs");
        string librarySource = SourceTextTestHelper.ReadBmsLibrarySourceText();
        string runMethod = ExtractMethodBody(source, "private void RunChartPackageMutation(");
        string autoInstallMethod = ExtractMethodBody(source, "private IReadOnlyList<ChartPackage> ExecutePackageInstallMutation(");
        string forceInstallMethod = ExtractMethodBody(source, "public void ForceInstallPendingPackages(");
        string installDestinationBoundary = ExtractMethodBody(installDestinationSource, "private bool Execute(");
        string repairInstallDestinationMethod = ExtractMethodBody(installDestinationSource, "internal Task SearchCorrectAsync(");
        string autoInstallLibraryMethod = ExtractMethodBody(librarySource, "public List<ChartPackage> InstallChartPackagesAuto(");
        string pendingOverwriteMethod = ExtractMethodBody(librarySource, "public PendingInstalledOnlyResourceOverwriteResult OverwritePendingInstalledOnlyPackagesResources(");
        string pendingZeroNoteRenameMethod = ExtractMethodBody(librarySource, "internal void RenamePendingZeroNoteBmsFormatChartsToInvalidExtensions(");
        string pendingSourceDeleteMethod = ExtractMethodBody(librarySource, "public void DeletePendingPackageSources(");
        string autoRenameMethod = ExtractMethodBody(librarySource, "internal void AutoRenameChartFolders(");
        string autoRenameAllMethod = ExtractMethodBody(librarySource, "internal bool AutoRenameAllChartFolders(");
        string dialogEnqueueMethod = ExtractMethodBody(librarySource, "internal void Enqueue(OperationDialogMessage message)");

        StringAssert.Contains(source, "public bool IsChartPackageMutationInProgress");
        StringAssert.Contains(source, "private void RunPendingInstallMutation(Action action");
        StringAssert.Contains(source, "RunChartPackageMutation(action");
        StringAssert.Contains(runMethod, "BeginOperationDialogScope()");
        StringAssert.Contains(runMethod, "BeginChartPackageMutation()");
        StringAssert.Contains(runMethod, "using (chartFileOperations.Enter())");
        StringAssert.Contains(runMethod, "EndUiUpdateSuppression()");
        StringAssert.Contains(runMethod, "dialogScope?.Flush()");
        StringAssert.Contains(autoInstallMethod, "RunChartPackageMutation");
        StringAssert.Contains(forceInstallMethod, "approvedNormalInstallOverridePackages");
        StringAssert.Contains(forceInstallMethod, "approveNormalInstallOverride: false");
        StringAssert.Contains(installDestinationBoundary, "BeginOperationDialogScope()");
        StringAssert.Contains(installDestinationBoundary, "operationGate = chartFileOperations.Enter()");
        StringAssert.Contains(installDestinationBoundary, "presentation.BeginRefreshSuppression()");
        StringAssert.Contains(installDestinationBoundary, "CaptureCleanupFailure(dialogScope.Flush, failures)");
        StringAssert.Contains(installDestinationBoundary, "ThrowFailures(failures)");
        StringAssert.Contains(repairInstallDestinationMethod, "Execute(library =>");
        Assert.IsFalse(autoInstallLibraryMethod.Contains("deferredSourceProcessedCount"));
        StringAssert.Contains(autoInstallLibraryMethod, "onEachSourceProcessed");
        StringAssert.Contains(autoInstallLibraryMethod, "onEachArchiveExtractStarted");
        Assert.IsTrue(
            autoInstallLibraryMethod.IndexOf("packageInstallService.ExpandInstallSources", StringComparison.Ordinal) < autoInstallLibraryMethod.IndexOf("rwlockBMSFilesInitializedAll.GetReaderGuard", StringComparison.Ordinal),
            "Archive expansion progress callbacks should be emitted before BMS state locks are acquired.");
        StringAssert.Contains(pendingOverwriteMethod, "ExecuteInstalledOnlyResourceOverwrite");
        StringAssert.Contains(pendingOverwriteMethod, "deferredProcessedCount");
        StringAssert.Contains(pendingZeroNoteRenameMethod, "RenamePendingZeroNoteBmsFormatChartsToInvalidExtensions");
        StringAssert.Contains(pendingZeroNoteRenameMethod, "deferredProcessedCount");
        StringAssert.Contains(pendingSourceDeleteMethod, "DeletePendingPackageSources");
        StringAssert.Contains(pendingSourceDeleteMethod, "deferredProcessedCount");
        StringAssert.Contains(autoRenameMethod, "deferredProgressReporter");
        StringAssert.Contains(autoRenameMethod, "FlushAutoRenameProgressReports");
        StringAssert.Contains(autoRenameAllMethod, "deferredProgressReporter");
        StringAssert.Contains(autoRenameAllMethod, "FlushAutoRenameProgressReports");
        StringAssert.Contains(dialogEnqueueMethod, "messages.Any");
    }

    [TestMethod]
    public void MainWindow_BlocksChartPackageContextMenuWhileMutationIsRunning()
    {
        string source = SourceTextTestHelper.ReadMainWindowSourceText();
        string rowContextMenu = ExtractMethodBody(source, "private void customTableView_RowContextMenuRequested");
        string tableContextMenuOpened = ExtractMethodBody(source, "private void tableContextMenuOpened");
        string removeInstallDestination = ExtractMethodBody(source, "private async void tableContextMenuRemoveInstallDestinationClick");
        string searchCorrectInstallationDirectory = ExtractMethodBody(source, "private async void tableContextMenuSearchCorrectInstallationDirectoryChartsClick");

        StringAssert.Contains(source, "private bool ShouldBlockChartPackageMutationInteraction");
        StringAssert.Contains(source, "viewModel.IsChartPackageMutationInProgress");
        StringAssert.Contains(rowContextMenu, "ShouldBlockChartPackageMutationInteraction(\"custom_table_row_context_menu\")");
        StringAssert.Contains(tableContextMenuOpened, "ShouldBlockChartPackageMutationInteraction(\"datagrid_context_menu_opened\")");
        StringAssert.Contains(removeInstallDestination, "ShouldBlockChartPackageMutationInteraction");
        StringAssert.Contains(searchCorrectInstallationDirectory, "ShouldBlockChartPackageMutationInteraction");
        StringAssert.Contains(source, "ShouldBlockChartPackageMutationInteraction(\"datagrid_move_chart\")");
        StringAssert.Contains(source, "ShouldBlockChartPackageMutationInteraction(\"datagrid_remove_chart\")");
        StringAssert.Contains(source, "ShouldBlockChartPackageMutationInteraction(\"tree_duplicate_merge_into\")");
    }

    private static DispatcherCollection<ChartPackage> CreatePackageCollection(IEnumerable<ChartPackage> packages)
    {
        return new DispatcherCollection<ChartPackage>(new ObservableCollection<ChartPackage>([.. (packages ?? [])]), Dispatcher.CurrentDispatcher);
    }

    private static void WithTemporarySongDb(Action<string> testAction)
    {
        string tempRootPath = Path.Combine(Path.GetTempPath(), "BeMusicSeeker_MutationBoundaryTests_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempRootPath);
        string songDbPath = Path.Combine(tempRootPath, "song.db");
        File.WriteAllBytes(songDbPath, []);
        try
        {
            testAction(songDbPath);
        }
        finally
        {
            if (Directory.Exists(tempRootPath))
            {
                Directory.Delete(tempRootPath, recursive: true);
            }
        }
    }

    private static string FindRepositoryRoot()
    {
        string directory = AppDomain.CurrentDomain.BaseDirectory;
        while (!string.IsNullOrEmpty(directory))
        {
            if (File.Exists(Path.Combine(directory, "BeMusicSeeker.sln")))
            {
                return directory;
            }
            DirectoryInfo parent = Directory.GetParent(directory);
            directory = parent == null ? string.Empty : parent.FullName;
        }
        Assert.Fail("Repository root was not found.");
        return string.Empty;
    }

    private static string ExtractMethodBody(string source, string methodName)
    {
        int nameIndex = source.IndexOf(methodName, StringComparison.Ordinal);
        Assert.IsTrue(nameIndex >= 0, methodName + " was not found.");
        int braceIndex = source.IndexOf('{', nameIndex);
        Assert.IsTrue(braceIndex >= 0, methodName + " body was not found.");

        int depth = 0;
        for (int i = braceIndex; i < source.Length; i++)
        {
            if (source[i] == '{')
            {
                depth++;
            }
            else if (source[i] == '}')
            {
                depth--;
                if (depth == 0)
                {
                    return source.Substring(braceIndex, i - braceIndex + 1);
                }
            }
        }

        Assert.Fail(methodName + " body was not closed.");
        return string.Empty;
    }

    private sealed class RecordingDialogService : IBmsLibraryDialogService
    {
        private readonly object lockObject = new();

        public List<DialogCall> Calls { get; } = [];

        public MessageBoxResult ResultToReturn { get; set; } = MessageBoxResult.OK;

        public int CallCount
        {
            get
            {
                lock (lockObject)
                {
                    return Calls.Count;
                }
            }
        }

        public DialogCall GetCall(int index)
        {
            lock (lockObject)
            {
                return Calls[index];
            }
        }

        public MessageBoxResult Show(string messageBoxText, string caption, MessageBoxButton button, MessageBoxImage icon, MessageBoxResult defaultResult = MessageBoxResult.None)
        {
            lock (lockObject)
            {
                Calls.Add(new DialogCall
                {
                    Message = messageBoxText,
                    Caption = caption,
                    Button = button,
                    Icon = icon,
                    DefaultResult = defaultResult
                });
            }
            return ResultToReturn;
        }
    }

    private sealed class DialogCall
    {
        public string Message { get; set; } = string.Empty;

        public string Caption { get; set; } = string.Empty;

        public MessageBoxButton Button { get; set; }

        public MessageBoxImage Icon { get; set; }

        public MessageBoxResult DefaultResult { get; set; }
    }

    private sealed class TestableBmsFile : BMSFile
    {
    }
}
