using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.IO;
using System.Linq;
using BeMusicSeeker.Models;
using BeMusicSeeker.Models.BmsLibraryInternal;
using BeMusicSeeker.Models.LR2;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using MessageBoxButton = BeMusicSeeker.Models.UiDialogButton;
using MessageBoxImage = BeMusicSeeker.Models.UiDialogIcon;
using MessageBoxResult = BeMusicSeeker.Models.UiDialogDefaultResult;

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
            var library = new TestBmsLibrary(songDbPath, null!, null, null!, dialogService);
            string missingDirectoryPath = Path.Combine(Path.GetTempPath(), "BeMusicSeeker_MutationBoundary_" + Guid.NewGuid().ToString("N"));

            using BMSLibrary.OperationDialogScope scope = library.BeginOperationDialogScope();
            library.RenameChartFolder(missingDirectoryPath, "RenamedFolder");
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
            var library = new TestBmsLibrary(songDbPath, null!, null, null!, dialogService);
            ChartFile pendingFile = ChartTestValues.Empty() with
            {
                Path = "C:\\Pending\\Pkg\\chart.bms"
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

    /// <summary>direct preparedの未承認候補をscope内へ持ち込んでも、従来の非OK拒否を緩めません。</summary>
    [TestMethod]
    public void OperationDialogScope_RejectsPreparedRemovalWithoutExplicitFolderDecision()
    {
        TestResourceInitializer.EnsureJapaneseResources();
        OwnedChartCollectionTestSupport.WithTemporarySongDb(songDbPath =>
        {
            string root = Path.GetDirectoryName(songDbPath) ?? throw new InvalidOperationException();
            string folder = Path.Combine(root, "Pack");
            Directory.CreateDirectory(folder);
            string path = Path.Combine(folder, "chart.bms");
            File.WriteAllText(path, "#PLAYER 1");
            var filesystem = new OwnedChartCollectionTestSupport.TestFileMutationService();
            var dialogs = new RecordingDialogService { ResultToReturn = MessageBoxResult.Yes };
            var library = new TestBmsLibrary(songDbPath, null, null, filesystem, dialogs,
                new TestUiScheduler(() => TestUiDispatcherHost.Dispatcher),
                () => new BmsLibraryOptionsSnapshot { OperationModeLR2DB = false })
            { BmsCharts = [OwnedChartCollectionTestSupport.CreateFile(new string('a', 32), path)], BmsonCharts = [] };
            new BmsLibraryDbGateway(songDbPath).UpsertSongs(library.BmsCharts);
            try
            {
                LibraryChartRemovalPreflight prepared = library.PrepareLibraryChartRemoval(
                    library.BmsCharts.Select(chart => LibraryChartRef.FromChartFile(chart)));
                Assert.AreEqual(1, prepared.WholeFolderCandidatePaths.Count);
                using BMSLibrary.OperationDialogScope scope = library.BeginOperationDialogScope();

                Assert.ThrowsException<InvalidOperationException>(() => library.RemoveLibraryCharts(prepared, sendToRecycleBin: false));

                Assert.AreEqual(0, dialogs.CallCount);
                Assert.AreEqual(0, scope.Messages.Count);
                Assert.AreEqual(0, filesystem.FileDeleteCalls);
                Assert.AreEqual(0, filesystem.DirectoryDeleteCalls);
                Assert.IsTrue(File.Exists(path));
                using var readback = new LR2SongDBExtended(songDbPath);
                Assert.AreEqual(path, readback.Table<LR2SongDB.song>().Single().path);
                Assert.AreEqual(1, library.BmsCharts.Count);
            }
            finally
            {
                library.RequestShutdown("prepared-dialog-boundary-test");
                TestUiDispatcherHost.Drain();
            }
        });
    }

    [TestMethod]
    public void ForceInstallPendingPackages_WithExplicitOverrideDecisionDoesNotPromptInsideMutationScope()
    {
        TestResourceInitializer.EnsureJapaneseResources();
        WithTemporarySongDb(delegate (string songDbPath)
        {
            var dialogService = new RecordingDialogService();
            var library = new TestBmsLibrary(songDbPath, null!, null, null!, dialogService);
            ChartFile pendingFile = ChartTestValues.Empty() with
            {
                Path = "C:\\Pending\\Pkg\\chart.bms"
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

    private static ObservableCollection<ChartPackage> CreatePackageCollection(IEnumerable<ChartPackage> packages)
    {
        return new ObservableCollection<ChartPackage>([.. (packages ?? [])]);
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

}
