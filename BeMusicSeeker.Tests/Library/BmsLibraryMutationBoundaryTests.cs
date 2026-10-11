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
    /// <summary>
    /// 保証：同じ操作scopeの確認不要警告は重複を集約してFlushまで保留し、実通知には対象パスを保持する。
    /// </summary>
    /// <remarks>実ライブラリのフォルダ変更から警告を生産し、dialog scopeとの実接続と集約を確認する。</remarks>
    [TestMethod]
    public void OperationDialogScope_QueuesOkMessagesUntilFlush()
    {

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

    /// <summary>
    /// 保証：事前判断のない強制導入を変更scope内で呼ぶと拒否し、対話確認も保留パッケージの変更も開始しない。
    /// </summary>
    /// <remarks>実ライブラリ入口からscope境界の対話拒否と保留状態の保持を確認する。</remarks>
    [TestMethod]
    public void OperationDialogScope_RejectsInteractivePromptWhenPreflightDecisionIsMissing()
    {

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

    /// <summary>
    /// 保証：フォルダ削除の事前候補があっても明示承認がなければscope内で拒否し、実ファイル・DB・所持状態を保持する。
    /// </summary>
    /// <remarks>実事前準備と削除入口を接続し、手動の候補入力だけでは分からない承認不足時の無副作用を確認する。</remarks>
    [TestMethod]
    public void OperationDialogScope_RejectsPreparedRemovalWithoutExplicitFolderDecision()
    {

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
                using LR2SongDBExtended readback = new BmsLibraryDbGateway(songDbPath).OpenSongDbReadOnly();
                Assert.AreEqual(path, readback.Table<LR2SongDB.song>().Single().path);
                Assert.AreEqual(1, library.BmsCharts.Count);
            }
            finally
            {
                library.RequestShutdown("prepared-dialog-boundary-test");
            }
        });
    }

    /// <summary>
    /// 保証：強制導入を明示的に不承認としたら変更scope内でも再確認せず、対象保留パッケージを保持する。
    /// </summary>
    /// <remarks>実強制導入入口で事前決定とdialog scopeの接続を確認する。</remarks>
    [TestMethod]
    public void ForceInstallPendingPackages_WithExplicitOverrideDecisionDoesNotPromptInsideMutationScope()
    {

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

    /// <summary>
    /// 保証：保存済み通常・追加・ルート形式の管理出力と移動元・移動先・祖先が交差する変更だけをP活動中に拒否し、非交差は確定する。
    /// </summary>
    /// <remarks>実ディレクトリ移動とDB更新を使い、交差拒否時の無副作用およびP解放後の明示再要求成功を観測する。</remarks>
    [DataTestMethod]
    [DataRow("normal", "source")]
    [DataRow("normal", "destination")]
    [DataRow("normal", "ancestor")]
    [DataRow("normal", "disjoint")]
    [DataRow("root", "source")]
    [DataRow("additional", "source")]
    public void FolderMutation_UsesPlaylistAdmissionOnlyForActualManagedIntersection(string role, string intersection)
    {

        OwnedChartCollectionTestSupport.WithTemporarySongDb(songDbPath =>
        {
            string root = Path.GetDirectoryName(songDbPath) ?? throw new InvalidOperationException();
            string baseDirectory = Path.Combine(root, "Managed");
            string sourceDirectory = intersection switch
            {
                "source" => Path.Combine(baseDirectory, "Pack"),
                "ancestor" => Path.Combine(root, "Container"),
                _ => Path.Combine(root, "Source")
            };
            if (intersection == "ancestor") { baseDirectory = Path.Combine(sourceDirectory, "Managed"); }
            string newName = intersection == "destination" ? "Managed" : "Moved";
            string destinationDirectory = Path.Combine(Path.GetDirectoryName(sourceDirectory) ?? root, newName);
            Directory.CreateDirectory(sourceDirectory);
            string path = Path.Combine(sourceDirectory, "chart.bms");
            File.WriteAllText(path, "#PLAYER 1\n#TITLE Scope\n");
            BmsLibraryOptionsSnapshot options = new()
            {
                OperationModeLR2DB = true,
                LR2CustomFolderOutputBaseDir = role == "normal" ? baseDirectory : Path.Combine(root, "OtherNormal"),
                LR2CustomFolderOutputBaseDirRootType = role == "root" ? baseDirectory : Path.Combine(root, "OtherRoot"),
                LR2CustomFolderAdditionalOutputBaseDirs = role == "additional" ? [baseDirectory] : []
            };
            var dialogs = new RecordingDialogService();
            var library = new TestBmsLibrary(songDbPath, null, null, null, dialogs,
                new TestUiScheduler(() => TestUiDispatcherHost.Dispatcher), () => options)
            { BmsCharts = [OwnedChartCollectionTestSupport.CreateFile(new string('a', 32), path)], BmsonCharts = [] };
            new BmsLibraryDbGateway(songDbPath).UpsertSongs(library.BmsCharts);
            Assert.IsTrue(library.Lr2Synchronization.PlaylistOperationAdmission.TryEnter(out IDisposable playlistLease));
            try
            {
                LibraryMutationSessionReceipt receipt = library.RenameChartFolderWithReceipt(sourceDirectory, newName);
                if (intersection == "disjoint")
                {
                    Assert.AreEqual(1, receipt.ConfirmedChangeCount);
                    Assert.IsFalse(File.Exists(path));
                    Assert.IsTrue(File.Exists(Path.Combine(destinationDirectory, "chart.bms")));
                }
                else
                {
                    Assert.AreEqual(0, receipt.ConfirmedChangeCount);
                    Assert.IsTrue(File.Exists(path));
                    Assert.IsFalse(Directory.Exists(destinationDirectory));
                    Assert.IsTrue(dialogs.CallCount > 0);
                    using (LR2SongDBExtended db = new BmsLibraryDbGateway(songDbPath).OpenSongDbReadOnly())
                    { Assert.AreEqual(path, db.Table<LR2SongDB.song>().Single().path); }
                    playlistLease.Dispose();
                    receipt = library.RenameChartFolderWithReceipt(sourceDirectory, newName);
                    Assert.AreEqual(1, receipt.ConfirmedChangeCount);
                    Assert.IsFalse(File.Exists(path));
                    Assert.IsTrue(File.Exists(Path.Combine(destinationDirectory, "chart.bms")));
                }
                using (LR2SongDBExtended db = new BmsLibraryDbGateway(songDbPath).OpenSongDbReadOnly())
                { Assert.AreEqual(Path.Combine(destinationDirectory, "chart.bms"), db.Table<LR2SongDB.song>().Single().path); }
            }
            finally { playlistLease.Dispose(); library.RequestShutdown("managed-intersection-test"); }
        });
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
